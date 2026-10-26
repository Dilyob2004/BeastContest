using BeastContest.Web.Models;
using BeastContest.Web.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
namespace BeastContest.Web.Services;
public sealed record StandingRow(int Rank,string Name,int Score,int Penalty,string[] Results,long? TimeSeconds=null,int? Attempts=null,long?[]? ProblemSeconds=null,int[]? ProblemAttempts=null);
public sealed record StandingsView(StandingRow[] Rows,DateTimeOffset? FrozenAt,bool IsAdmin,Contest? Contest=null);
public sealed class StandingsStore(IDbContextFactory<AppDbContext> factory,ICurrentActor actor,ContestStore contests)
{
    public async Task<StandingRow[]> GetAsync(Guid id){
        if(!(await actor.GetAsync()).IsAuthenticated)throw new UnauthorizedAccessException();
        if(!(await contests.GetCompletedAsync()).Any(c=>c.Id==id))return [];
        return (await GetViewAsync(id)).Rows;
    }
    public async Task<StandingsView> GetViewAsync(Guid id){
        var current=await actor.GetAsync();
        if(!current.IsAuthenticated)throw new UnauthorizedAccessException();
        await using var db=await factory.CreateDbContextAsync();
        await using var tx=await db.Database.BeginTransactionAsync();
        // Hold the visibility policy stable while retrieving rows, including for admins.
        var entity=await db.Contests.FromSqlInterpolated($"SELECT * FROM \"Contests\" WHERE \"Id\"={id} FOR SHARE").SingleOrDefaultAsync();
        if(entity is null || (!current.IsAdmin&&!entity.ParticipantIds.Contains(current.Id)) || entity.IsDemo || entity.StartsAt is null || entity.StartsAt>DateTimeOffset.UtcNow)return new([],null,current.IsAdmin);
        if(entity.StandingsFrozenAt is {} frozen && !current.IsAdmin)
            return new(JsonSerializer.Deserialize<StandingRow[]>(entity.FrozenStandingsJson)!,frozen,false,ContestStore.ToModel(entity));
        var submissions=await LoadVerdictsAsync(db,id);
        return new(Calculate(ContestStore.ToModel(entity),submissions),entity.StandingsFrozenAt,current.IsAdmin,ContestStore.ToModel(entity));
    }
    public async Task SetFrozenAsync(Guid id,bool frozen){
        await actor.RequireAdminAsync();
        await using var db=await factory.CreateDbContextAsync();
        await using var tx=await db.Database.BeginTransactionAsync();
        var entity=await db.Contests.FromSqlInterpolated($"SELECT * FROM \"Contests\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync();
        if(entity is null || entity.IsDemo || entity.StartsAt is null || entity.StartsAt>DateTimeOffset.UtcNow)throw new DomainException("freeze.invalid");
        // Repeating the same action must not replace the frozen snapshot.
        if(frozen==(entity.StandingsFrozenAt is not null))return;
        if(frozen){
            var submissions=await LoadVerdictsAsync(db,id);
            entity.FrozenStandingsJson=JsonSerializer.Serialize(Calculate(ContestStore.ToModel(entity),submissions));
            entity.StandingsFrozenAt=DateTimeOffset.UtcNow;
        }else{entity.StandingsFrozenAt=null;entity.FrozenStandingsJson="[]";}
        entity.Revision++;
        await db.SaveChangesAsync();
        await LiveEvents.NotifyAsync(db,new("standings",null,id));
        await tx.CommitAsync();
    }
    /// <summary>
    /// Only the columns the table needs: never source code or test snapshots.
    /// Administrators may test contest problems; their submissions never enter the standings.
    /// Submissions of deleted accounts stay, as before.
    /// </summary>
    private static Task<SubmissionEntity[]> LoadVerdictsAsync(AppDbContext db,Guid id){
        var admins=from link in db.UserRoles join role in db.Roles on link.RoleId equals role.Id where role.Name=="Admin" select link.UserId;
        return db.Submissions.AsNoTracking().Where(s=>s.ContestId==id&&!admins.Contains(s.UserId)).OrderBy(s=>s.CreatedAt).ThenBy(s=>s.Id)
            .Select(s=>new SubmissionEntity{Id=s.Id,UserId=s.UserId,UserName=s.UserName,ProblemId=s.ProblemId,ContestId=s.ContestId,Status=s.Status,Passed=s.Passed,Total=s.Total,Score=s.Score,CreatedAt=s.CreatedAt})
            .ToArrayAsync();
    }
    public static StandingRow[] Calculate(Contest contest,IEnumerable<SubmissionEntity> submissions){
        var rows=new List<StandingRow>();
        foreach(var user in submissions.Where(s=>s.ContestId==contest.Id).GroupBy(s=>s.UserId)){
            var score=0;var penalty=0;var results=new List<string>();
            var times=new List<long?>();var counts=new List<int>();
            long Seconds(SubmissionEntity s)=>Math.Max(0,(long)(s.CreatedAt-contest.StartsAt!.Value).TotalSeconds);
            foreach(var problem in contest.ProblemIds){
                var attempts=user.Where(s=>s.ProblemId==problem).OrderBy(s=>s.CreatedAt).ThenBy(s=>s.Id).ToArray();
                counts.Add(attempts.Length);
                if(contest.Scoring==ScoringMode.Points){
                    var completed=attempts.Where(s=>s.Status is "Accepted" or "WrongAnswer" or "TimeLimitExceeded" or "MemoryLimitExceeded" or "RuntimeError" or "OutputLimitExceeded").ToArray();
                    // Score is the Worker's weighted result (test weights and groups), 0–100 per problem.
                    var best=completed.Select(s=>s.Score).DefaultIfEmpty(0).Max();score+=best;results.Add(completed.Length>0?best.ToString():"—");
                    times.Add(best>0?Seconds(completed.First(s=>s.Score==best)):null);
                }else{
                    var solved=attempts.FirstOrDefault(s=>s.Status=="Accepted");
                    times.Add(solved is null?null:Seconds(solved));
                    var wrong=attempts.TakeWhile(s=>s!=solved).Count(s=>s.Status is "WrongAnswer" or "TimeLimitExceeded" or "MemoryLimitExceeded" or "RuntimeError" or "OutputLimitExceeded");
                    if(solved is null){results.Add(wrong>0?"-"+wrong:"—");continue;}
                    var minutes=Math.Max(0,(int)(solved.CreatedAt-contest.StartsAt!.Value).TotalMinutes);score++;penalty+=minutes+20*wrong;results.Add((wrong>0?"+"+wrong:"+")+" "+minutes);
                }
            }
            rows.Add(new(0,user.OrderBy(s=>s.CreatedAt).ThenBy(s=>s.Id).Last().UserName,score,penalty,results.ToArray(),times.Max(),counts.Sum(),times.ToArray(),counts.ToArray()));
        }
        long TieKey(StandingRow r)=>contest.Scoring==ScoringMode.Icpc?r.Penalty:r.TimeSeconds??long.MaxValue;
        var sorted=rows.OrderByDescending(r=>r.Score).ThenBy(TieKey).ThenBy(r=>r.Name,StringComparer.Ordinal).ToArray();
        for(var i=0;i<sorted.Length;i++)sorted[i]=sorted[i] with{Rank=i>0&&sorted[i].Score==sorted[i-1].Score&&TieKey(sorted[i])==TieKey(sorted[i-1])?sorted[i-1].Rank:i+1};
        return sorted;
    }
    public static string FormatTime(long? seconds)=>seconds is {} value?$"{value/3600:00}:{value/60%60:00}:{value%60:00}":"—";
}
