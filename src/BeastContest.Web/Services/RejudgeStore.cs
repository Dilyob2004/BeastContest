using System.Text.Json;
using BeastContest.Web.Data;
using BeastContest.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace BeastContest.Web.Services;

public sealed record RejudgeLog(Guid Id,string AdminName,string Reason,DateTimeOffset CreatedAt,int Count,bool UpdatedTests,Guid? ContestId,Guid? SubmissionId);
public sealed record PreviousVerdict(Guid Id,string Status,int Passed,int Total,string Diagnostic,string SnapshotJson,int Score=0,string TestResultsJson="[]");

public sealed class RejudgeStore(IDbContextFactory<AppDbContext> factory,ICurrentActor actor)
{
    public async Task<RejudgeLog[]> HistoryAsync(){
        await actor.RequireAdminAsync();
        await using var db=await factory.CreateDbContextAsync();
        return await db.Rejudges.AsNoTracking().OrderByDescending(r=>r.CreatedAt).Take(20)
            .Select(r=>new RejudgeLog(r.Id,r.AdminName,r.Reason,r.CreatedAt,r.Count,r.UpdatedTests,r.ContestId,r.SubmissionId)).ToArrayAsync();
    }

    public async Task<int> RunAsync(Guid requestId,Guid? submissionId,Guid? contestId,bool updatedTests,string reason){
        var admin=await actor.RequireAdminAsync();
        reason=reason?.Trim()??"";
        if(requestId==Guid.Empty || (submissionId is null)==(contestId is null) || reason.Length is <3 or >500 || (updatedTests&&submissionId is not null))
            throw new DomainException("rejudge.invalid");
        await using var db=await factory.CreateDbContextAsync();
        await using var tx=await db.Database.BeginTransactionAsync();
        // Serialize requests and retries; workers retain their independent row leases.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(684201903)");
        var existing=await db.Rejudges.SingleOrDefaultAsync(r=>r.Id==requestId);
        if(existing is not null){
            if(existing.AdminId!=admin.Id || existing.SubmissionId!=submissionId || existing.ContestId!=contestId || existing.UpdatedTests!=updatedTests || existing.Reason!=reason)
                throw new DomainException("rejudge.invalid");
            return existing.Count;
        }
        ContestEntity? contest=null;
        if(contestId is {} cid){
            // Same lock order as SubmitAsync: contest first, submission rows second.
            contest=await db.Contests.FromSqlInterpolated($"SELECT * FROM \"Contests\" WHERE \"Id\"={cid} FOR UPDATE").SingleOrDefaultAsync();
            if(contest is null || contest.IsDemo || contest.StartsAt is null)throw new DomainException("rejudge.contest");
        }
        var jobs=submissionId is {} sid
            ?await db.Submissions.FromSqlInterpolated($"SELECT * FROM \"Submissions\" WHERE \"Id\"={sid} FOR UPDATE").ToArrayAsync()
            :await db.Submissions.FromSqlInterpolated($"SELECT * FROM \"Submissions\" WHERE \"ContestId\"={contestId} ORDER BY \"Id\" FOR UPDATE").ToArrayAsync();
        if(jobs.Length==0)throw new DomainException("rejudge.empty");
        Dictionary<string,JudgeProblem>? snapshots=null;
        if(updatedTests){
            snapshots=new();
            var ids=JsonSerializer.Deserialize<string[]>(contest!.ProblemIdsJson)!;
            foreach(var id in ids.Order(StringComparer.Ordinal)){
                var problem=await db.Problems.FromSqlInterpolated($"SELECT * FROM \"Problems\" WHERE \"Id\"={id} FOR SHARE").SingleOrDefaultAsync()??throw new DomainException("judge.noTests");
                var suite=await db.ProblemTests.FromSqlInterpolated($"SELECT * FROM \"ProblemTests\" WHERE \"ProblemId\"={id} FOR SHARE").SingleOrDefaultAsync()??throw new DomainException("judge.noTests");
                var tests=JsonSerializer.Deserialize<TestCaseDraft[]>(suite.TestsJson)!;
                TestSuiteStore.Validate(tests);
                if(tests.Length==0)throw new DomainException("judge.noTests");
                var languages=JsonSerializer.Deserialize<string[]>(contest.LanguagesJson)!;
                if(languages.Any(l=>LanguageProfiles.Find(l) is not {} profile || profile.MinimumMemoryMb>problem.MemoryMb))throw new DomainException("judge.memory");
                snapshots.Add(id,JudgeSnapshots.Create(problem,suite));
            }
            if(jobs.Any(j=>!snapshots.ContainsKey(j.ProblemId)))throw new DomainException("judge.noTests");
            contest.JudgeProblemsJson=JsonSerializer.Serialize(snapshots.Values.ToArray());
            contest.Revision++;
        }
        db.Rejudges.Add(new(){Id=requestId,AdminId=admin.Id,AdminName=admin.Name,Reason=reason,CreatedAt=DateTimeOffset.UtcNow,
            ContestId=contestId,SubmissionId=submissionId,UpdatedTests=updatedTests,Count=jobs.Length,
            PreviousJson=JsonSerializer.Serialize(jobs.Select(j=>new PreviousVerdict(j.Id,j.Status,j.Passed,j.Total,j.Diagnostic,j.SnapshotJson,j.Score,j.TestResultsJson)))});
        foreach(var job in jobs){
            if(snapshots is not null){job.SnapshotJson=JsonSerializer.Serialize(snapshots[job.ProblemId]);job.Total=snapshots[job.ProblemId].Tests.Length;}
            job.Status="Queued";job.Passed=0;job.Score=0;job.TestResultsJson="[]";job.Diagnostic="";job.Attempts=0;
            // Invalidates an in-flight worker: its conditional write can no longer win.
            job.LeaseOwner=null;job.LeaseUntil=null;
        }
        await db.SaveChangesAsync();
        await LiveEvents.NotifyAsync(db,new("rejudge",null,contestId??jobs[0].ContestId));
        await tx.CommitAsync();return jobs.Length;
    }
}
