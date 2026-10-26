using System.Text.Json;
using System.Text;
using BeastContest.Web.Data;
using BeastContest.Web.Models;
using Microsoft.EntityFrameworkCore;
namespace BeastContest.Web.Services;
public sealed record JudgeProblem(string ProblemId,int Revision,int TimeMs,int MemoryMb,TestCaseDraft[] Tests,JudgeChecker? Checker=null);
public sealed record SubmissionSummary(Guid Id,string Language,string Status,int Passed,int Total,DateTimeOffset CreatedAt,string Diagnostic,int Score=0,bool ShowScore=false);
public enum PersonalProblemStatus { Accepted, Pending, Failed, SystemError }
public sealed class SubmissionStore(IDbContextFactory<AppDbContext> factory,ICurrentActor actor)
{
    public async Task<Dictionary<string,PersonalProblemStatus>> ProblemStatusesAsync(CancellationToken cancellationToken=default){
        var user=await actor.GetAsync();if(!user.IsAuthenticated)throw new UnauthorizedAccessException();
        await using var db=await factory.CreateDbContextAsync(cancellationToken);
        var contest=user.IsAdmin?null:await ParticipantAccess.CurrentAsync(db,user);
        if(!user.IsAdmin&&!ParticipantAccess.Open(contest))return [];
        // Only the current user's verdicts are needed, never source, tests or other users' results.
        var currentContestId=contest?.Id;
        var verdicts=await db.Submissions.AsNoTracking().Where(s=>s.UserId==user.Id&&(user.IsAdmin||s.ContestId==currentContestId))
            .Select(s=>new{s.ProblemId,s.Status}).Distinct().ToArrayAsync(cancellationToken);
        return verdicts.GroupBy(s=>s.ProblemId).ToDictionary(g=>g.Key,g=>
            g.Any(s=>s.Status=="Accepted")?PersonalProblemStatus.Accepted:
            g.Any(s=>s.Status is "Queued" or "Running")?PersonalProblemStatus.Pending:
            g.Any(s=>s.Status is "WrongAnswer" or "CompilationError" or "TimeLimitExceeded" or "MemoryLimitExceeded" or "RuntimeError" or "OutputLimitExceeded")?PersonalProblemStatus.Failed:
            PersonalProblemStatus.SystemError);
    }
    public async Task<SubmissionSummary[]> HistoryAsync(string problemId){
        var user=await actor.GetAsync();if(!user.IsAuthenticated)throw new UnauthorizedAccessException();
        await using var db=await factory.CreateDbContextAsync();
        var contest=user.IsAdmin?null:await ParticipantAccess.CurrentAsync(db,user);
        if(!user.IsAdmin&&(!ParticipantAccess.Open(contest)||!contest!.ProblemIds.Contains(problemId)))return [];
        var currentContestId=contest?.Id;
        // Scores matter only in points contests; administrators always see them.
        var showScore=user.IsAdmin||contest?.Scoring==ScoringMode.Points;
        return await db.Submissions.AsNoTracking().Where(s=>s.UserId==user.Id && s.ProblemId==problemId && (user.IsAdmin||s.ContestId==currentContestId)).OrderByDescending(s=>s.CreatedAt).Take(30)
            .Select(s=>new SubmissionSummary(s.Id,s.Language,s.Status,s.Passed,s.Total,s.CreatedAt,s.Diagnostic,s.Score,showScore)).ToArrayAsync();
    }
    public async Task<Guid> SubmitAsync(Guid requestId,string problemId,Guid? contestId,string language,string source){
        var user=await actor.GetAsync();if(!user.IsAuthenticated)throw new UnauthorizedAccessException();
        var profile=LanguageProfiles.Find(language);
        if(requestId==Guid.Empty || profile is null || string.IsNullOrWhiteSpace(source) || Encoding.UTF8.GetByteCount(source)>65536)throw new DomainException("judge.invalid");
        await using var db=await factory.CreateDbContextAsync();await using var tx=await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({user.Id},0))");
        var existing=await db.Submissions.SingleOrDefaultAsync(s=>s.Id==requestId);
        if(existing is not null){if(existing.UserId!=user.Id)throw new UnauthorizedAccessException();return existing.Id;}
        if(await db.Submissions.CountAsync(s=>s.UserId==user.Id && (s.Status=="Queued" || s.Status=="Running"))>=3)throw new DomainException("judge.queueFull");
        JudgeProblem snapshot;
        if(contestId is {} id){
            var contest=await db.Contests.FromSqlInterpolated($"SELECT * FROM \"Contests\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync();
            if(contest is null || (!user.IsAdmin&&!contest.ParticipantIds.Contains(user.Id)) || contest.IsDemo || ContestStore.ToModel(contest).Status!=ContestStatus.Running || !JsonSerializer.Deserialize<string[]>(contest.LanguagesJson)!.Contains(language))throw new DomainException("judge.contestClosed");
            snapshot=JsonSerializer.Deserialize<JudgeProblem[]>(contest.JudgeProblemsJson)?.FirstOrDefault(p=>p.ProblemId==problemId) ?? throw new DomainException("judge.noTests");
        }else{
            if(!user.IsAdmin)throw new DomainException("judge.selectContest");
            var problem=await db.Problems.FromSqlInterpolated($"SELECT * FROM \"Problems\" WHERE \"Id\"={problemId} FOR SHARE").SingleOrDefaultAsync() ?? throw new DomainException("common.notfound");
            var tests=await db.ProblemTests.SingleOrDefaultAsync(t=>t.ProblemId==problemId) ?? throw new DomainException("judge.noTests");
            snapshot=JudgeSnapshots.Create(problem,tests);
        }
        if(snapshot.Tests.Length==0)throw new DomainException("judge.noTests");
        if(snapshot.MemoryMb<profile.MinimumMemoryMb)throw new DomainException("judge.memory");
        db.Submissions.Add(new SubmissionEntity{Id=requestId,UserId=user.Id,UserName=user.Name,ProblemId=problemId,ContestId=contestId,Language=language,Source=source,SnapshotJson=JsonSerializer.Serialize(snapshot),Total=snapshot.Tests.Length,CreatedAt=DateTimeOffset.UtcNow});
        await db.SaveChangesAsync();
        await LiveEvents.NotifyAsync(db,new("submission",user.Id,contestId));
        await tx.CommitAsync();return requestId;
    }
}
