using System.Text.Json;
using BeastContest.Judge;
using BeastContest.Web.Data;
using BeastContest.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
if(args.Contains("--self-test")){await RunnerChecks.RunAsync(args.FirstOrDefault(a=>a.StartsWith("--language="))?.Split('=',2)[1]);return;}
var builder=Host.CreateApplicationBuilder(args);
var connection=builder.Configuration["ConnectionStrings:Database"]??throw new InvalidOperationException("Database connection required.");
builder.Services.AddDbContextFactory<AppDbContext>(o=>o.UseNpgsql(connection));
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command",LogLevel.Warning);
builder.Services.AddSingleton(new DockerRunner(builder.Configuration["Judge:Image"]??"beastcontest-sandbox:local"));
builder.Services.AddSingleton<WorkerPresence>();
builder.Services.AddHostedService<PresenceHeartbeat>();
builder.Services.AddHostedService<JudgeWorker>();
await builder.Build().RunAsync();

sealed class JudgeWorker(IDbContextFactory<AppDbContext> factory,DockerRunner runner,WorkerPresence presence,ILogger<JudgeWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken){
        var nextCleanup=DateTimeOffset.MinValue;
        while(!stoppingToken.IsCancellationRequested){
            try{
                if(DateTimeOffset.UtcNow>=nextCleanup){await runner.CleanupAbandonedAsync(stoppingToken);nextCleanup=DateTimeOffset.UtcNow.AddMinutes(1);}
                await using var db=await factory.CreateDbContextAsync(stoppingToken);
                await using var tx=await db.Database.BeginTransactionAsync(stoppingToken);
                var now=DateTimeOffset.UtcNow;
                var jobs=await db.Submissions.FromSqlInterpolated($"SELECT * FROM \"Submissions\" WHERE \"Status\"='Queued' OR (\"Status\"='Running' AND \"LeaseUntil\" < {now}) ORDER BY \"CreatedAt\" LIMIT 1 FOR UPDATE SKIP LOCKED").ToArrayAsync(stoppingToken);
                var job=jobs.SingleOrDefault();
                if(job is null){presence.Set("Idle");await tx.CommitAsync(stoppingToken);await Task.Delay(1500,stoppingToken);continue;}
                if(job.Attempts>=3){job.Status="SystemError";job.Diagnostic="Worker could not complete this submission after three attempts.";await db.SaveChangesAsync(stoppingToken);await tx.CommitAsync(stoppingToken);continue;}
                presence.Set("Busy",job.Id);job.Status="Running";job.Attempts++;job.LeaseOwner=Guid.NewGuid().ToString("N");job.LeaseUntil=now.AddSeconds(60);
                await db.SaveChangesAsync(stoppingToken);
                await LiveEvents.NotifyAsync(db,new("submission",job.UserId,job.ContestId),stoppingToken);
                await tx.CommitAsync(stoppingToken);
                using var execution=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeat=HeartbeatAsync(job.Id,job.LeaseOwner,execution);
                JudgeResult result;
                try{
                    var snapshot=JsonSerializer.Deserialize<JudgeProblem>(job.SnapshotJson)??throw new InvalidDataException();
                    TestSuiteStore.Validate(snapshot.Tests);
                    if(snapshot.TimeMs is <100 or >30000 || BeastContest.Web.Models.LanguageProfiles.Find(job.Language) is null)throw new InvalidDataException();
                    (snapshot.Checker??JudgeChecker.Default).Validate();
                    // Practice runs without a contest are administrator-only: show checker diagnostics there.
                    result=await runner.JudgeAsync(job.Language,job.Source,snapshot,execution.Token,revealCheckerErrors:job.ContestId is null);
                }catch(OperationCanceledException){throw;}
                catch(Exception error){log.LogError(error,"Judge infrastructure failed for {Id}",job.Id);result=new("SystemError",0,"Judge infrastructure error. Ask the administrator to check Worker logs.");}
                finally{execution.Cancel();try{await heartbeat;}catch(OperationCanceledException){}}
                await using var finish=await factory.CreateDbContextAsync(stoppingToken);
                var testsJson=JsonSerializer.Serialize(result.Tests??[]);
                var saved=await finish.Submissions.Where(s=>s.Id==job.Id && s.LeaseOwner==job.LeaseOwner && s.Status=="Running").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,result.Status).SetProperty(x=>x.Passed,result.Passed).SetProperty(x=>x.Score,result.Score).SetProperty(x=>x.TestResultsJson,testsJson).SetProperty(x=>x.Diagnostic,result.Diagnostic).SetProperty(x=>x.LeaseUntil,(DateTimeOffset?)null),stoppingToken);
                if(saved==1)await LiveEvents.NotifyAsync(finish,new("submission",job.UserId,job.ContestId),stoppingToken);
                log.LogInformation("Submission {Id}: {Verdict}",job.Id,result.Status);
            }catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception error){presence.Set("Error");log.LogWarning(error,"Worker waiting for database or recovering a lease.");await Task.Delay(3000,stoppingToken);}
        }
    }
    private async Task HeartbeatAsync(Guid id,string owner,CancellationTokenSource execution){
        try{using var timer=new PeriodicTimer(TimeSpan.FromSeconds(10));while(await timer.WaitForNextTickAsync(execution.Token)){
            await using var db=await factory.CreateDbContextAsync(execution.Token);
            var updated=await db.Submissions.Where(s=>s.Id==id && s.LeaseOwner==owner && s.Status=="Running").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LeaseUntil,DateTimeOffset.UtcNow.AddSeconds(60)),execution.Token);
            if(updated!=1){execution.Cancel();return;}
        }}catch(OperationCanceledException){}catch(Exception error){log.LogWarning(error,"Lease renewal failed.");execution.Cancel();}
    }
}


