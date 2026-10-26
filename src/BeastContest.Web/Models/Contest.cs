namespace BeastContest.Web.Models;

public enum ScoringMode { Icpc, Points }
public enum ContestStatus { Draft, Scheduled, Running, Finished }
public record Translated(string Ru, string En);
public record Problem(string Id, string Letter, Translated Title, string Difficulty, int TimeMs, int MemoryMb, string[] Tags,
    Translated? Statement = null, Translated? Input = null, Translated? Output = null,
    string SampleInput = "", string SampleOutput = "", bool IsDemo = true, bool HasPdfRu = false, bool HasPdfEn = false,
    string CheckerMode = "tokens", double CheckerEpsilon = 1e-6);
public record Contest(Guid Id, Translated Title, Translated Description, ScoringMode Scoring,
    int DurationMinutes, int ParticipantLimit, string[] Languages, string[] ProblemIds, bool IsDemo, DateTimeOffset CreatedAt,
    DateTimeOffset? StartsAt = null, DateTimeOffset? EndsAt = null, string[]? ParticipantIds = null)
{
    public ContestStatus StatusAt(DateTimeOffset now) => StartsAt is null ? ContestStatus.Draft
        : EndsAt <= now ? ContestStatus.Finished : StartsAt > now ? ContestStatus.Scheduled : ContestStatus.Running;
    public ContestStatus Status => StatusAt(DateTimeOffset.UtcNow);
}

public sealed class ContestDraft
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public ScoringMode Scoring { get; set; } = ScoringMode.Icpc;
    public int DurationMinutes { get; set; } = 120;
    public int ParticipantLimit { get; set; } = 100;
    public List<string> Languages { get; set; } = ["C++", "Python"];
    public List<string> ProblemIds { get; set; } = [];
    public List<string> ParticipantIds { get; set; } = [];
    public DateTimeOffset? StartsAt { get; set; }

    public string[] Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Title) || Title.Trim().Length > 100) errors.Add("validation.title");
        if (Description.Length > 2000) errors.Add("validation.description");
        if (DurationMinutes is < 5 or > 10080) errors.Add("validation.duration");
        if (ParticipantLimit is < 1 or > 100) errors.Add("validation.participants");
        if (!Enum.IsDefined(Scoring)) errors.Add("validation.scoring");
        if (Languages.Count == 0 || Languages.Any(x => !DemoCatalog.Languages.Contains(x))) errors.Add("validation.languages");
        if (ProblemIds.Count > 100 || ProblemIds.Distinct().Count() != ProblemIds.Count) errors.Add("validation.problems");
        if (ParticipantIds.Count > ParticipantLimit || ParticipantIds.Distinct().Count()!=ParticipantIds.Count) errors.Add("enrollment.invalid");
        if (StartsAt is {} start && (start<=DateTimeOffset.UtcNow || start>DateTimeOffset.UtcNow.AddYears(1))) errors.Add("schedule.invalid");
        return [.. errors];
    }
}

public sealed class DomainException(params string[] keys) : Exception(string.Join(", ", keys))
{
    public string[] Keys { get; } = keys;
}

public static class DemoCatalog
{
    public static readonly string[] Languages = ["C++", "C#", "Java", "Python", "Go", "Haskell", "JavaScript"];
    public static readonly string[] Tags = ["math", "implementation", "graphs", "dp", "strings"];
    public static readonly Problem[] Problems =
    [
        new("sum", "A", new("Сумма двух чисел", "Sum of two numbers"), "easy", 1000, 256, ["math", "implementation"],
            new("Даны два целых числа a и b. Вычислите их сумму.", "Given two integers a and b, calculate their sum."),
            new("В одной строке заданы a и b (−10⁹ ≤ a, b ≤ 10⁹).", "One line contains a and b (−10⁹ ≤ a, b ≤ 10⁹)."),
            new("Выведите сумму a и b.", "Print the sum of a and b."), "2 3", "5"),
        new("route", "B", new("Кратчайший маршрут", "The shortest route"), "medium", 2000, 256, ["graphs"]),
        new("sequence", "C", new("Идеальная последовательность", "A perfect sequence"), "medium", 2000, 256, ["dp"]),
        new("network", "D", new("Надёжная сеть", "A resilient network"), "hard", 3000, 512, ["graphs"]),
        new("archive", "E", new("Архив хранителя", "The keeper’s archive"), "hard", 2000, 256, ["strings"])
    ];
    public static readonly Contest[] Contests =
    [
        new(Guid.Parse("10000000-0000-0000-0000-000000000001"), new("Первый рубеж", "First frontier"),
            new("Пять задач. Семь языков. Один шанс превзойти себя.", "Five problems. Seven languages. One chance to outdo yourself."),
            ScoringMode.Icpc, 120, 100, Languages, ["sum", "route", "sequence", "network", "archive"], true,
            DateTimeOffset.Parse("2026-10-01T08:00:00Z"), DateTimeOffset.Parse("2026-10-01T08:00:00Z"), DateTimeOffset.Parse("2026-10-01T10:00:00Z")),
        new(Guid.Parse("10000000-0000-0000-0000-000000000002"), new("Алгоритмическая разминка", "Algorithm warm-up"),
            new("Начните с простого и найдите свой ритм.", "Start with the basics and find your rhythm."),
            ScoringMode.Points, 60, 100, Languages, ["sum", "route", "sequence"], true,
            DateTimeOffset.Parse("2026-10-02T08:00:00Z"), DateTimeOffset.Parse("2026-10-02T08:00:00Z"), DateTimeOffset.Parse("2026-10-02T09:00:00Z"))
    ];
    public static string Number(int index) => (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
