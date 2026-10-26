using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BeastContest.Web.Data;

public sealed class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public bool Disabled { get; set; }
}

public sealed class ContestEntity
{
    public Guid Id { get; set; }
    public string TitleRu { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public string DescriptionRu { get; set; } = "";
    public string DescriptionEn { get; set; } = "";
    public int Scoring { get; set; }
    public int DurationMinutes { get; set; }
    public int ParticipantLimit { get; set; }
    public string LanguagesJson { get; set; } = "[]";
    public string ProblemIdsJson { get; set; } = "[]";
    public string[] ParticipantIds { get; set; } = [];
    public bool IsDemo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public int Revision { get; set; }
    public string JudgeProblemsJson {get;set;}="[]";
    public DateTimeOffset? StandingsFrozenAt {get;set;}
    public string FrozenStandingsJson {get;set;}="[]";
}

public sealed class ProblemEntity
{
    public string Id { get; set; } = "";
    public string Letter { get; set; } = "";
    public string TitleRu { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public string StatementRu { get; set; } = "";
    public string StatementEn { get; set; } = "";
    public string InputRu { get; set; } = "";
    public string InputEn { get; set; } = "";
    public string OutputRu { get; set; } = "";
    public string OutputEn { get; set; } = "";
    public string SampleInput { get; set; } = "";
    public string SampleOutput { get; set; } = "";
    public string Difficulty { get; set; } = "easy";
    public int TimeMs { get; set; }
    public int MemoryMb { get; set; }
    public string TagsJson { get; set; } = "[]";
    public bool IsDemo { get; set; }
    public byte[]? PdfRu { get; set; }
    public byte[]? PdfEn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CheckerMode { get; set; } = "tokens";
    public double CheckerEpsilon { get; set; } = 1e-6;
    public string? CheckerSource { get; set; }
}

public sealed class ChatMessage
{
    public long Id { get; set; }
    public string ParticipantId { get; set; } = "";
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public bool FromAdmin { get; set; }
    public string Body { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
}
public sealed class ProblemTestEntity
{
    public string ProblemId {get;set;}="";
    public string TestsJson {get;set;}="[]";
    public int Revision {get;set;}
}
public sealed class SubmissionEntity
{
    public Guid Id {get;set;}
    public string UserId {get;set;}="";
    public string UserName {get;set;}="";
    public string ProblemId {get;set;}="";
    public Guid? ContestId {get;set;}
    public string Language {get;set;}="";
    public string Source {get;set;}="";
    public string SnapshotJson {get;set;}="";
    public string Status {get;set;}="Queued";
    public string Diagnostic {get;set;}="";
    public int Passed {get;set;}
    public int Total {get;set;}
    /// <summary>Weighted points-mode score 0–100 computed by the Worker.</summary>
    public int Score {get;set;}
    /// <summary>Short per-test verdict codes (AC, WA, TL, ML, RE, OL, CF); empty while queued.</summary>
    public string TestResultsJson {get;set;}="[]";
    public int Attempts {get;set;}
    public DateTimeOffset CreatedAt {get;set;}
    public DateTimeOffset? LeaseUntil {get;set;}
    public string? LeaseOwner {get;set;}
}
public sealed class RejudgeEntity
{
    public Guid Id {get;set;}
    public string AdminName {get;set;}="";
    public string AdminId {get;set;}="";
    public string Reason {get;set;}="";
    public DateTimeOffset CreatedAt {get;set;}
    public Guid? ContestId {get;set;}
    public Guid? SubmissionId {get;set;}
    public bool UpdatedTests {get;set;}
    public int Count {get;set;}
    public string PreviousJson {get;set;}="[]";
}
public sealed class AppMarker
{
    public string Id { get; set; } = "";
}

public sealed class JudgeWorkerEntity
{
    public string Id {get;set;}="";
    public DateTimeOffset LastSeen {get;set;}
    public string State {get;set;}="Starting";
    public Guid? SubmissionId {get;set;}
    public string Image {get;set;}="";
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ContestEntity> Contests => Set<ContestEntity>();
    public DbSet<ProblemEntity> Problems => Set<ProblemEntity>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ProblemTestEntity> ProblemTests => Set<ProblemTestEntity>();
    public DbSet<SubmissionEntity> Submissions => Set<SubmissionEntity>();
    public DbSet<AppMarker> Markers => Set<AppMarker>();
    public DbSet<JudgeWorkerEntity> JudgeWorkers => Set<JudgeWorkerEntity>();
    public DbSet<RejudgeEntity> Rejudges => Set<RejudgeEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<SubmissionEntity>().HasIndex(s=>new{s.Status,s.CreatedAt});
        builder.Entity<SubmissionEntity>().HasIndex(s=>new{s.UserId,s.ProblemId});
        builder.Entity<ProblemTestEntity>().HasKey(x=>x.ProblemId);
        builder.Entity<ProblemTestEntity>().Property(x=>x.Revision).IsConcurrencyToken();
        builder.Entity<ProblemTestEntity>().HasOne<ProblemEntity>().WithMany().HasForeignKey(x=>x.ProblemId);
        builder.Entity<ChatMessage>().HasIndex(x=>new {x.ParticipantId,x.Id});
        builder.Entity<ChatMessage>().Property(x=>x.Body).HasMaxLength(2000);
        builder.Entity<ContestEntity>().Property(x => x.Revision).IsConcurrencyToken();
        builder.Entity<ContestEntity>().Property(x => x.TitleRu).HasMaxLength(100);
        builder.Entity<ContestEntity>().Property(x => x.TitleEn).HasMaxLength(100);
        builder.Entity<ProblemEntity>().Property(x => x.Id).HasMaxLength(64);
        builder.Entity<ProblemEntity>().Property(x => x.TitleRu).HasMaxLength(100);
        builder.Entity<ProblemEntity>().Property(x => x.TitleEn).HasMaxLength(100);
        builder.Entity<ProblemEntity>().Property(x => x.CheckerMode).HasMaxLength(16).HasDefaultValue("tokens");
        builder.Entity<ProblemEntity>().Property(x => x.CheckerEpsilon).HasDefaultValue(1e-6);
        builder.Entity<SubmissionEntity>().Property(x => x.TestResultsJson).HasDefaultValue("[]");
        builder.Entity<ApplicationUser>().Property(x => x.DisplayName).HasMaxLength(80);
        builder.Entity<AppMarker>().HasKey(x => x.Id);
    }
}



