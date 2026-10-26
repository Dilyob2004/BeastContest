using BeastContest.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace BeastContest.Judge;
public sealed record WorkerActivity(string State,Guid? SubmissionId=null);
public sealed class WorkerPresence
{
    public string Id {get;}=Environment.MachineName+"-"+Guid.NewGuid().ToString("N")[..8];
    private WorkerActivity _activity=new("Starting");
    public WorkerActivity Activity=>Volatile.Read(ref _activity);
    public void Set(string state,Guid? id=null)=>Volatile.Write(ref _activity,new(state,id));
}
public sealed class PresenceHeartbeat(IDbContextFactory<AppDbContext> factory,WorkerPresence presence,ILogger<PresenceHeartbeat> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken){
        try{while(!stoppingToken.IsCancellationRequested){
            try{await SaveAsync(presence.Activity,stoppingToken);}catch(Exception ex) when(ex is not OperationCanceledException){log.LogWarning("Cannot publish Worker heartbeat: {ErrorType}",ex.GetType().Name);}
            await Task.Delay(5000,stoppingToken);
        }}catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){}
        finally{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));try{await SaveAsync(new("Stopped"),timeout.Token);}catch(Exception){}}
    }
    private async Task SaveAsync(WorkerActivity activity,CancellationToken token){
        await using var db=await factory.CreateDbContextAsync(token);
        var image=Environment.GetEnvironmentVariable("Judge__Image")??"beastcontest-sandbox:local";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"JudgeWorkers\" (\"Id\",\"LastSeen\",\"State\",\"SubmissionId\",\"Image\") VALUES ({presence.Id},{DateTimeOffset.UtcNow},{activity.State},{activity.SubmissionId},{image}) ON CONFLICT (\"Id\") DO UPDATE SET \"LastSeen\"=EXCLUDED.\"LastSeen\",\"State\"=EXCLUDED.\"State\",\"SubmissionId\"=EXCLUDED.\"SubmissionId\",\"Image\"=EXCLUDED.\"Image\"",token);
    }
}
