using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeastContest.Web.Models;
using BeastContest.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BeastContest.Web.Data;

public sealed class DatabaseBootstrapper(IServiceScopeFactory scopes, AppStorage storage)
{



    public async Task InitializeAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach(var name in new[]{"Admin","Participant"})
            if(!await roles.RoleExistsAsync(name)) {
                var result=await roles.CreateAsync(new IdentityRole(name));
                if(!result.Succeeded) throw new InvalidOperationException("Unable to initialize roles.");
            }
        if(!await db.Markers.AnyAsync(m=>m.Id=="seed-v2"))
        {
            await using var transaction=await db.Database.BeginTransactionAsync();
            foreach(var p in DemoCatalog.Problems)
                if(!await db.Problems.AnyAsync(x=>x.Id==p.Id))
                    db.Problems.Add(new ProblemEntity {Id=p.Id,Letter=p.Letter,TitleRu=p.Title.Ru,TitleEn=p.Title.En,
                        StatementRu=p.Statement?.Ru ?? "",StatementEn=p.Statement?.En ?? "",InputRu=p.Input?.Ru ?? "",InputEn=p.Input?.En ?? "",
                        OutputRu=p.Output?.Ru ?? "",OutputEn=p.Output?.En ?? "",SampleInput=p.SampleInput,SampleOutput=p.SampleOutput,
                        Difficulty=p.Difficulty,TimeMs=p.TimeMs,MemoryMb=p.MemoryMb,TagsJson=JsonSerializer.Serialize(p.Tags),IsDemo=true,
                        CreatedAt=DateTimeOffset.Parse("2026-10-01T00:00:00Z")});
            foreach(var c in DemoCatalog.Contests)
                if(!await db.Contests.AnyAsync(x=>x.Id==c.Id)) db.Contests.Add(ContestStore.ToEntity(c));
            var legacyPath=Path.Combine(storage.Root,"contests.json");
            if(File.Exists(legacyPath)) {
                var legacy=JsonSerializer.Deserialize<Contest[]>(await File.ReadAllTextAsync(legacyPath))
                    ?? throw new InvalidDataException("Legacy contest storage is invalid; original file was preserved.");
                foreach(var c in legacy.Where(c=>!c.IsDemo))
                    if(!await db.Contests.AnyAsync(x=>x.Id==c.Id)) db.Contests.Add(ContestStore.ToEntity(c));
            }
            db.Markers.Add(new AppMarker{Id="seed-v2"});
            await db.SaveChangesAsync(); await transaction.CommitAsync();
        }
    }
}

