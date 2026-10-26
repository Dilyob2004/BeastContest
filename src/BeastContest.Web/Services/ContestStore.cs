using System.Text.Json;
using BeastContest.Web.Data;
using BeastContest.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace BeastContest.Web.Services;

public sealed class ContestStore(IDbContextFactory<AppDbContext> factory, ICurrentActor actor)
{
    public async Task<Contest[]> GetAllAsync()
    {
        var current = await actor.GetAsync();
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.Contests.AsNoTracking().Where(c => current.IsAdmin || (current.IsAuthenticated && !c.IsDemo && c.ParticipantIds.Contains(current.Id))).ToArrayAsync();
        return rows.Select(ToModel).Select(c=>!current.IsAdmin && c.Status is ContestStatus.Draft or ContestStatus.Scheduled ? c with{ProblemIds=[],ParticipantIds=null} : current.IsAdmin?c:c with{ParticipantIds=null}).OrderByDescending(x => x.CreatedAt).ToArray();
    }
    public async Task<Contest[]> GetCompletedAsync() => (await GetAllAsync()).Where(c => c.Status == ContestStatus.Finished).OrderByDescending(c => c.EndsAt).ToArray();
    public async Task<Contest> CreateAsync(ContestDraft draft)
    {
        await actor.RequireAdminAsync();
        var errors = draft.Validate();
        if (errors.Length > 0) throw new DomainException(errors);
        await using var db = await factory.CreateDbContextAsync();
        await using var tx=await db.Database.BeginTransactionAsync();
        foreach(var problemId in draft.ProblemIds.Order(StringComparer.Ordinal)){
            var problem=await db.Problems.FromSqlInterpolated($"SELECT * FROM \"Problems\" WHERE \"Id\"={problemId} FOR SHARE").SingleOrDefaultAsync();
            if(problem is null)throw new DomainException("validation.problems");
        }
        await ValidateParticipantsAsync(db,draft.ParticipantIds.ToArray(),draft.ParticipantLimit);
        var contest = new Contest(Guid.NewGuid(), new(draft.Title.Trim(), draft.Title.Trim()), new(draft.Description.Trim(), draft.Description.Trim()),
            draft.Scoring, draft.DurationMinutes, draft.ParticipantLimit, draft.Languages.Distinct().ToArray(), draft.ProblemIds.ToArray(), false, DateTimeOffset.UtcNow, draft.StartsAt?.ToUniversalTime(),draft.StartsAt?.ToUniversalTime().AddMinutes(draft.DurationMinutes),draft.ParticipantIds.ToArray());
        var entity=ToEntity(contest);
        if(draft.StartsAt is not null)entity.JudgeProblemsJson=await SnapshotAsync(db,draft.ProblemIds.ToArray());
        db.Contests.Add(entity); await db.SaveChangesAsync(); await tx.CommitAsync(); return contest;
    }
    public async Task StartAsync(Guid id)
    {
        await actor.RequireAdminAsync();
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.Contests.SingleOrDefaultAsync(x => x.Id == id);
        if (row is null || row.IsDemo || row.StartsAt != null) throw new DomainException("contest.invalidTransition");
        row.JudgeProblemsJson=await SnapshotAsync(db,JsonSerializer.Deserialize<string[]>(row.ProblemIdsJson)!);
        row.StartsAt = DateTimeOffset.UtcNow; row.EndsAt = row.StartsAt.Value.AddMinutes(row.DurationMinutes); row.Revision++;
        await SaveStateAsync(db);
    }
    public async Task FinishAsync(Guid id)
    {
        await actor.RequireAdminAsync();
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.Contests.SingleOrDefaultAsync(x => x.Id == id);
        if (row is null || row.IsDemo || ToModel(row).Status != ContestStatus.Running) throw new DomainException("contest.invalidTransition");
        row.EndsAt = DateTimeOffset.UtcNow; row.Revision++;
        await SaveStateAsync(db);
    }
    public async Task SetParticipantsAsync(Guid id,string[] ids){
        await actor.RequireAdminAsync();
        await using var db=await factory.CreateDbContextAsync();
        await using var tx=await db.Database.BeginTransactionAsync();
        var row=await db.Contests.FromSqlInterpolated($"SELECT * FROM \"Contests\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync();
        if(row is null||row.IsDemo)throw new DomainException("contest.invalidTransition");
        await ValidateParticipantsAsync(db,ids,row.ParticipantLimit);
        row.ParticipantIds=ids;row.Revision++;
        await SaveStateAsync(db);await tx.CommitAsync();
    }
    public async Task ScheduleAsync(Guid id,DateTimeOffset start){
        await actor.RequireAdminAsync();
        if(start<=DateTimeOffset.UtcNow||start>DateTimeOffset.UtcNow.AddYears(1))throw new DomainException("schedule.invalid");
        await using var db=await factory.CreateDbContextAsync();
        await using var tx=await db.Database.BeginTransactionAsync();
        var row=await db.Contests.FromSqlInterpolated($"SELECT * FROM \"Contests\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync();
        if(row is null||row.IsDemo||ToModel(row).Status is not (ContestStatus.Draft or ContestStatus.Scheduled))throw new DomainException("contest.invalidTransition");
        row.JudgeProblemsJson=await SnapshotAsync(db,JsonSerializer.Deserialize<string[]>(row.ProblemIdsJson)!);
        row.StartsAt=start.ToUniversalTime();row.EndsAt=row.StartsAt.Value.AddMinutes(row.DurationMinutes);row.Revision++;
        await SaveStateAsync(db);await tx.CommitAsync();
    }
    private static async Task ValidateParticipantsAsync(AppDbContext db,string[] ids,int limit){
        if(ids.Length>limit||ids.Distinct().Count()!=ids.Length)throw new DomainException("enrollment.invalid");
        var valid=await (from user in db.Users join role in db.UserRoles on user.Id equals role.UserId
                        join definition in db.Roles on role.RoleId equals definition.Id
                        where ids.Contains(user.Id)&&!user.Disabled&&definition.Name=="Participant"
                        select user.Id).Distinct().CountAsync();
        if(valid!=ids.Length)throw new DomainException("enrollment.invalid");
    }
    private static async Task<string> SnapshotAsync(AppDbContext db,string[] ids){
        if(ids.Length==0)throw new DomainException("contest.needsProblems");
        var snapshots=new List<JudgeProblem>();
        foreach(var id in ids){
            var problem=await db.Problems.SingleAsync(p=>p.Id==id);
            var suite=await db.ProblemTests.SingleOrDefaultAsync(t=>t.ProblemId==id);
            if(suite is null)throw new DomainException("judge.noTests");
            var snapshot=JudgeSnapshots.Create(problem,suite);
            if(snapshot.Tests.Length==0)throw new DomainException("judge.noTests");
            snapshots.Add(snapshot);
        }
        return JsonSerializer.Serialize(snapshots);
    }
    /// <summary>Locks scheduled, not yet started contests that include the problem. Call before locking the problem row.</summary>
    internal static async Task<ContestEntity[]> LockPendingContestsAsync(AppDbContext db,string problemId){
        var now=DateTimeOffset.UtcNow;
        var rows=await db.Contests.FromSqlInterpolated($"SELECT * FROM \"Contests\" WHERE NOT \"IsDemo\" AND \"StartsAt\" > {now} ORDER BY \"Id\" FOR UPDATE").ToArrayAsync();
        return rows.Where(c=>JsonSerializer.Deserialize<string[]>(c.ProblemIdsJson)!.Contains(problemId)).ToArray();
    }
    /// <summary>Re-captures judge snapshots after problem settings or tests were saved in the same transaction.</summary>
    internal static async Task RefreshSnapshotsAsync(AppDbContext db,IEnumerable<ContestEntity> contests){
        var changed=false;
        foreach(var contest in contests){
            contest.JudgeProblemsJson=await SnapshotAsync(db,JsonSerializer.Deserialize<string[]>(contest.ProblemIdsJson)!);
            contest.Revision++;changed=true;
        }
        if(changed)await SaveStateAsync(db);
    }
    private static async Task SaveStateAsync(AppDbContext db)
    {
        try { await db.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { throw new DomainException("contest.invalidTransition"); }
    }
    internal static Contest ToModel(ContestEntity c) => new(c.Id,new(c.TitleRu,c.TitleEn),new(c.DescriptionRu,c.DescriptionEn),
        (ScoringMode)c.Scoring,c.DurationMinutes,c.ParticipantLimit,JsonSerializer.Deserialize<string[]>(c.LanguagesJson)!,
        JsonSerializer.Deserialize<string[]>(c.ProblemIdsJson)!,c.IsDemo,c.CreatedAt,c.StartsAt,c.EndsAt,c.ParticipantIds);
    internal static ContestEntity ToEntity(Contest c) => new() { Id=c.Id,TitleRu=c.Title.Ru,TitleEn=c.Title.En,DescriptionRu=c.Description.Ru,DescriptionEn=c.Description.En,
        Scoring=(int)c.Scoring,DurationMinutes=c.DurationMinutes,ParticipantLimit=c.ParticipantLimit,LanguagesJson=JsonSerializer.Serialize(c.Languages),
        ParticipantIds=c.ParticipantIds??[],ProblemIdsJson=JsonSerializer.Serialize(c.ProblemIds),IsDemo=c.IsDemo,CreatedAt=c.CreatedAt,StartsAt=c.StartsAt,EndsAt=c.EndsAt };
}

