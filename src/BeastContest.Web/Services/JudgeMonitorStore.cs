using BeastContest.Web.Data;
using Microsoft.EntityFrameworkCore;
namespace BeastContest.Web.Services;
public sealed record QueueItem(Guid Id,string UserName,string ProblemId,Guid? ContestId,string Language,string Status,int Passed,int Total,int Attempts,DateTimeOffset CreatedAt,string Diagnostic,int Score=0,string TestResultsJson="[]")
{
    public string[] TestCodes=>System.Text.Json.JsonSerializer.Deserialize<string[]>(TestResultsJson)??[];
}
public sealed record WorkerView(string Id,DateTimeOffset LastSeen,string State,Guid? SubmissionId,string Image,bool Online);
public sealed record JudgeMonitor(int Queued,int Running,int Expired,int Errors,WorkerView[] Workers,QueueItem[] Items);
public sealed class JudgeMonitorStore(IDbContextFactory<AppDbContext> factory,ICurrentActor actor)
{
    public async Task<JudgeMonitor> GetAsync(string? status=null,string? language=null,Guid? contestId=null){
        await actor.RequireAdminAsync();
        await using var db=await factory.CreateDbContextAsync();
        var now=DateTimeOffset.UtcNow;var day=now.AddDays(-1);
        var queued=await db.Submissions.CountAsync(s=>s.Status=="Queued");
        var running=await db.Submissions.CountAsync(s=>s.Status=="Running");
        var expired=await db.Submissions.CountAsync(s=>s.Status=="Running"&&(s.LeaseUntil==null||s.LeaseUntil<now));
        var errors=await db.Submissions.CountAsync(s=>s.Status=="SystemError"&&s.CreatedAt>=day);
        var workers=await db.JudgeWorkers.AsNoTracking().OrderByDescending(w=>w.LastSeen).Take(10).ToArrayAsync();
        var query=db.Submissions.AsNoTracking().AsQueryable();
        if(!string.IsNullOrEmpty(status))query=query.Where(s=>s.Status==status);
        if(!string.IsNullOrEmpty(language))query=query.Where(s=>s.Language==language);
        if(contestId is {} id)query=query.Where(s=>s.ContestId==id);
        var items=await query.OrderByDescending(s=>s.CreatedAt).Take(50).Select(s=>new QueueItem(s.Id,s.UserName,s.ProblemId,s.ContestId,s.Language,s.Status,s.Passed,s.Total,s.Attempts,s.CreatedAt,s.Diagnostic,s.Score,s.TestResultsJson)).ToArrayAsync();
        return new(queued,running,expired,errors,workers.Select(w=>new WorkerView(w.Id,w.LastSeen,w.State,w.SubmissionId,w.Image,w.State!="Stopped"&&w.LastSeen>=now.AddSeconds(-20))).ToArray(),items);
    }
}
