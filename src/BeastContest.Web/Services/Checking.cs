using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeastContest.Web.Data;
using BeastContest.Web.Models;

namespace BeastContest.Web.Services;

/// <summary>How a participant's output is compared with the expected answer.</summary>
public static class CheckerModes
{
    /// <summary>Whitespace-separated tokens must match exactly (default, previous behaviour).</summary>
    public const string Tokens = "tokens";
    /// <summary>Lines must match exactly; trailing spaces and trailing empty lines are ignored.</summary>
    public const string Lines = "lines";
    /// <summary>Tokens must match; numeric tokens may differ by an absolute or relative error.</summary>
    public const string Float = "float";
    /// <summary>Administrator-provided C++ checker compiled with testlib.h.</summary>
    public const string Testlib = "testlib";
    public static readonly string[] All = [Tokens, Lines, Float, Testlib];
    public const double DefaultEpsilon = 1e-6;
    public const double MinEpsilon = 1e-12;
    public const double MaxEpsilon = 1;
    public const int MaxSourceBytes = 65536;

    public static string[] Validate(string? mode, double epsilon, string? source)
    {
        var errors = new List<string>();
        if (mode is null || !All.Contains(mode)) errors.Add("checker.invalidMode");
        if (mode == Float && (double.IsNaN(epsilon) || epsilon < MinEpsilon || epsilon > MaxEpsilon)) errors.Add("checker.epsilonInvalid");
        if (mode == Testlib && (string.IsNullOrWhiteSpace(source) || Encoding.UTF8.GetByteCount(source) > MaxSourceBytes || source.Contains('\0'))) errors.Add("checker.sourceInvalid");
        return [.. errors];
    }
}

/// <summary>Checker settings captured in a judge snapshot.</summary>
public sealed record JudgeChecker(string Mode = CheckerModes.Tokens, double Epsilon = CheckerModes.DefaultEpsilon, string? Source = null)
{
    public static readonly JudgeChecker Default = new();
    public void Validate()
    {
        if (CheckerModes.Validate(Mode, Epsilon, Source).Length > 0) throw new InvalidDataException("Invalid checker snapshot.");
    }
}

/// <summary>Built-in answer comparisons. Pure functions, shared by the Worker and the checks.</summary>
public static class AnswerChecker
{
    public static bool Check(JudgeChecker checker, string actual, string expected) => checker.Mode switch
    {
        CheckerModes.Lines => LinesEqual(actual, expected),
        CheckerModes.Float => FloatTokensEqual(actual, expected, checker.Epsilon),
        CheckerModes.Tokens => TokensEqual(actual, expected),
        _ => throw new InvalidOperationException("External checkers are executed by the Worker.")
    };

    public static string[] Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static bool TokensEqual(string actual, string expected) => Tokens(actual).SequenceEqual(Tokens(expected));

    public static bool LinesEqual(string actual, string expected) => NormalizeLines(actual).SequenceEqual(NormalizeLines(expected));

    private static List<string> NormalizeLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd(' ', '\t', '\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    public static bool FloatTokensEqual(string actual, string expected, double epsilon)
    {
        var a = Tokens(actual); var e = Tokens(expected);
        if (a.Length != e.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == e[i]) continue;
            if (!TryNumber(a[i], out var x) || !TryNumber(e[i], out var y)) return false;
            // Absolute error for small values, relative error for large ones.
            if (Math.Abs(x - y) > epsilon * Math.Max(1, Math.Abs(y))) return false;
        }
        return true;
    }

    private static bool TryNumber(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}

/// <summary>Points-mode scoring with test weights and all-or-nothing groups.</summary>
public static class TestScoring
{
    public const int MaxPoints = 1000;
    public const int MaxGroupLength = 32;
    private static readonly Regex GroupPattern = new("^[A-Za-z0-9_-]*$", RegexOptions.Compiled);
    public static bool ValidGroup(string? group) => group is not null && group.Length <= MaxGroupLength && GroupPattern.IsMatch(group);

    /// <summary>
    /// Score in the range 0–100. Each test contributes its weight; tests sharing a non-empty group
    /// count only when every test of the group passed. Rounded down.
    /// </summary>
    public static int Score(IReadOnlyList<TestCaseDraft> tests, IReadOnlyList<bool> passed)
    {
        long total = tests.Sum(t => (long)t.Points);
        if (total <= 0) return 0;
        long earned = 0;
        foreach (var unit in tests.Select((test, index) => (test, index))
                     .GroupBy(x => string.IsNullOrEmpty(x.test.Group) ? "\0" + x.index : x.test.Group))
            if (unit.All(x => x.index < passed.Count && passed[x.index])) earned += unit.Sum(x => (long)x.test.Points);
        return (int)(earned * 100 / total);
    }
}

/// <summary>
/// Recognizes a program that failed because its runtime ran out of memory. Managed runtimes (Java, .NET,
/// Node.js) hit their own heap limit before the container is OOM-killed and exit with an error instead.
/// </summary>
public static class RuntimeSignals
{
    private static readonly Regex PythonMemoryError = new(@"(?m)^MemoryError\b", RegexOptions.Compiled);

    public static bool OutOfMemory(string language, string stderr) => language switch
    {
        "Java" => stderr.Contains("java.lang.OutOfMemoryError", StringComparison.Ordinal),
        "C#" => stderr.Contains("System.OutOfMemoryException", StringComparison.Ordinal),
        "JavaScript" => stderr.Contains("JavaScript heap out of memory", StringComparison.Ordinal) || stderr.Contains("Reached heap limit", StringComparison.Ordinal),
        "Python" => PythonMemoryError.IsMatch(stderr),
        "Go" => stderr.Contains("runtime: out of memory", StringComparison.Ordinal),
        "Haskell" => stderr.Contains("Heap exhausted", StringComparison.Ordinal) || stderr.Contains("heap overflow", StringComparison.Ordinal),
        "C++" => stderr.Contains("std::bad_alloc", StringComparison.Ordinal),
        _ => false
    };
}

public static class JudgeSnapshots
{
    public static JudgeChecker Checker(ProblemEntity problem) =>
        new(problem.CheckerMode, problem.CheckerEpsilon, problem.CheckerMode == CheckerModes.Testlib ? problem.CheckerSource : null);

    public static JudgeProblem Create(ProblemEntity problem, ProblemTestEntity suite) =>
        new(problem.Id, suite.Revision, problem.TimeMs, problem.MemoryMb, JsonSerializer.Deserialize<TestCaseDraft[]>(suite.TestsJson)!, Checker(problem));
}
