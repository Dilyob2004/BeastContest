using System.Text.Json;
using BeastContest.Web.Data;
using BeastContest.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace BeastContest.Web.Services;

public sealed class ProblemStore(IDbContextFactory<AppDbContext> factory, ICurrentActor actor)
{
    public async Task DeleteAsync(string id)
    {
        await actor.RequireAdminAsync();
        await using var db=await factory.CreateDbContextAsync();
        await using var tx=await db.Database.BeginTransactionAsync();
        // Writers referencing a problem hold a shared row lock until their insert commits.
        var row=await db.Problems.FromSqlInterpolated($"SELECT * FROM \"Problems\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync();
        if(row is null)throw new DomainException("common.notfound");
        if(row.IsDemo)throw new DomainException("problem.deleteDemo");
        var contestProblems=await db.Contests.Select(c=>c.ProblemIdsJson).ToArrayAsync();
        if(contestProblems.Any(json=>JsonSerializer.Deserialize<string[]>(json)!.Contains(id)) || await db.Submissions.AnyAsync(s=>s.ProblemId==id))
            throw new DomainException("problem.deleteUsed");
        db.Problems.Remove(row); // Hidden tests cascade; PDFs are stored in the problem row.
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
    public async Task<Problem[]> GetAllAsync()
    {
        var current = await actor.GetAsync();
        await using var db = await factory.CreateDbContextAsync();
        var visible = await VisibleIdsAsync(db, current);
        var rows = await db.Problems.AsNoTracking().Where(p => current.IsAdmin || visible.Contains(p.Id))
            .Select(p => new { p.Id, p.Letter, p.TitleRu, p.TitleEn, p.Difficulty, p.TimeMs, p.MemoryMb, p.TagsJson,
                p.StatementRu, p.StatementEn, p.InputRu, p.InputEn, p.OutputRu, p.OutputEn, p.SampleInput, p.SampleOutput, p.IsDemo,
                HasPdfRu = p.PdfRu != null, HasPdfEn = p.PdfEn != null, p.CreatedAt, p.CheckerMode, p.CheckerEpsilon }).ToListAsync();
        return rows.OrderBy(x=>current.IsAdmin?0:Array.IndexOf(visible,x.Id)).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Letter).Select(p => new Problem(p.Id, p.Letter,
            new(p.TitleRu, p.TitleEn), current.IsAdmin ? p.Difficulty : "", p.TimeMs, p.MemoryMb, JsonSerializer.Deserialize<string[]>(p.TagsJson)!,
            new(p.StatementRu,p.StatementEn), new(p.InputRu,p.InputEn), new(p.OutputRu,p.OutputEn),
            p.SampleInput,p.SampleOutput,p.IsDemo,p.HasPdfRu,p.HasPdfEn,p.CheckerMode,p.CheckerEpsilon)).ToArray();
    }
    internal static async Task<string[]> VisibleIdsAsync(AppDbContext db, Actor current)
    {
        if (!current.IsAuthenticated) return [];
        var contest=await ParticipantAccess.CurrentAsync(db,current);
        return ParticipantAccess.Open(contest)?contest!.ProblemIds:[];
    }
    public async Task<Problem> CreateAsync(ProblemDraft draft)
    {
        await actor.RequireAdminAsync();
        var errors = draft.Validate();
        if (errors.Length > 0) throw new DomainException(errors);
        TestSuiteStore.Validate(draft.Tests.ToArray());
        await using var db = await factory.CreateDbContextAsync();
        var row = new ProblemEntity { Id = Guid.NewGuid().ToString("N"), Letter = "P", CreatedAt = DateTimeOffset.UtcNow };
        Apply(row, draft);
        db.Problems.Add(row);
        if(draft.Tests.Count>0)db.ProblemTests.Add(new ProblemTestEntity{ProblemId=row.Id,Revision=1,TestsJson=JsonSerializer.Serialize(draft.Tests)});
        await db.SaveChangesAsync();
        return (await GetAllAsync()).Single(p => p.Id == row.Id);
    }

    /// <summary>Current settings of a problem for the edit form. Tests are edited on their own page.</summary>
    public async Task<ProblemDraft> GetDraftAsync(string id)
    {
        await actor.RequireAdminAsync();
        await using var db = await factory.CreateDbContextAsync();
        var p = await db.Problems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id) ?? throw new DomainException("common.notfound");
        return new ProblemDraft {
            TitleRu = p.TitleRu, TitleEn = p.TitleEn, StatementRu = p.StatementRu, StatementEn = p.StatementEn,
            InputRu = p.InputRu, InputEn = p.InputEn, OutputRu = p.OutputRu, OutputEn = p.OutputEn,
            SampleInput = p.SampleInput, SampleOutput = p.SampleOutput, Difficulty = p.Difficulty,
            TimeMs = p.TimeMs, MemoryMb = p.MemoryMb, Tags = JsonSerializer.Deserialize<List<string>>(p.TagsJson)!,
            PdfRu = p.PdfRu, PdfEn = p.PdfEn,
            CheckerMode = p.CheckerMode, CheckerEpsilon = p.CheckerEpsilon, CheckerSource = p.CheckerSource ?? ""
        };
    }

    /// <summary>
    /// Saves statement, limits and checker settings. Running and finished contests keep their snapshots
    /// (use a rejudge with updated tests to apply changes); scheduled contests pick the changes up immediately.
    /// </summary>
    public async Task UpdateAsync(string id, ProblemDraft draft)
    {
        await actor.RequireAdminAsync();
        var errors = draft.Validate();
        if (errors.Length > 0) throw new DomainException(errors);
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var pending = await ContestStore.LockPendingContestsAsync(db, id);
        var row = await db.Problems.FromSqlInterpolated($"SELECT * FROM \"Problems\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync()
            ?? throw new DomainException("common.notfound");
        Apply(row, draft);
        await db.SaveChangesAsync();
        await ContestStore.RefreshSnapshotsAsync(db, pending);
        await tx.CommitAsync();
    }

    private static void Apply(ProblemEntity row, ProblemDraft draft)
    {
        row.TitleRu = draft.TitleRu.Trim(); row.TitleEn = draft.TitleEn.Trim();
        row.StatementRu = draft.StatementRu.Trim(); row.StatementEn = draft.StatementEn.Trim();
        row.InputRu = draft.InputRu.Trim(); row.InputEn = draft.InputEn.Trim(); row.OutputRu = draft.OutputRu.Trim(); row.OutputEn = draft.OutputEn.Trim();
        row.SampleInput = draft.SampleInput; row.SampleOutput = draft.SampleOutput; row.Difficulty = draft.Difficulty;
        row.TimeMs = draft.TimeMs; row.MemoryMb = draft.MemoryMb; row.TagsJson = JsonSerializer.Serialize(draft.Tags.Distinct());
        row.PdfRu = draft.PdfRu; row.PdfEn = draft.PdfEn;
        row.CheckerMode = draft.CheckerMode;
        row.CheckerEpsilon = draft.CheckerMode == CheckerModes.Float ? draft.CheckerEpsilon : CheckerModes.DefaultEpsilon;
        row.CheckerSource = draft.CheckerMode == CheckerModes.Testlib ? draft.CheckerSource : null;
    }
}
