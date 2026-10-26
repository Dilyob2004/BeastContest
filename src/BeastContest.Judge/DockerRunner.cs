using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeastContest.Web.Models;
using BeastContest.Web.Services;

namespace BeastContest.Judge;

/// <param name="Tests">Per-test codes: AC, WA, TL, ML, RE, OL, or CF when the checker itself failed.</param>
public sealed record JudgeResult(string Status, int Passed, string Diagnostic = "", int Score = 0, string[]? Tests = null);
public sealed record CommandResult(int Exit, string Output, string Error, bool TimedOut = false, bool Overflow = false);

public sealed class DockerRunner(string image)
{
    private const int MaxArtifactBytes = 16 * 1024 * 1024;
    private const int OutputLimitBytes = 1024 * 1024;
    private const int CheckerTimeoutMs = 15000;
    /// <summary>Checker containers are replaced well before abandoned-container cleanup (5 minutes) could remove them.</summary>
    private static readonly TimeSpan CheckerContainerLifetime = TimeSpan.FromMinutes(3);
    /// <summary>Compiled checkers by source hash; administrators rarely change them.</summary>
    private readonly ConcurrentDictionary<string, byte[]> _checkers = new();

    private static readonly Dictionary<string, string> Codes = new()
    {
        ["Accepted"] = "AC", ["WrongAnswer"] = "WA", ["TimeLimitExceeded"] = "TL", ["MemoryLimitExceeded"] = "ML",
        ["RuntimeError"] = "RE", ["OutputLimitExceeded"] = "OL", ["SystemError"] = "CF"
    };

    public async Task CleanupAbandonedAsync(CancellationToken token)
    {
        var containers = await CommandAsync(["ps", "-a", "--filter", "label=beastcontest.sandbox=true", "--format", "{{.ID}} {{.Label \"beastcontest.created\"}}"], null, 10000, 65536, token);
        if (containers.Exit != 0 || containers.TimedOut || containers.Overflow) throw new IOException("Cannot inspect judge containers.");
        foreach (var line in containers.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && Regex.IsMatch(parts[0], "^[a-f0-9]{12,64}$") && long.TryParse(parts[1], out var created)
                && created < DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds())
                await RemoveAsync(parts[0]);
        }
    }

    /// <param name="revealCheckerErrors">Show checker compilation output in the diagnostic (administrator practice runs only).</param>
    public async Task<JudgeResult> JudgeAsync(string language, string source, JudgeProblem problem, CancellationToken token, bool revealCheckerErrors = false)
    {
        var profile = LanguageProfiles.Find(language) ?? throw new InvalidDataException("Unsupported language.");
        if (problem.MemoryMb < profile.MinimumMemoryMb) throw new InvalidDataException("Insufficient language memory limit.");
        var checker = problem.Checker ?? JudgeChecker.Default;
        checker.Validate();

        byte[]? checkerBinary = null;
        if (checker.Mode == CheckerModes.Testlib)
        {
            var compiled = await CompileCheckerAsync(checker.Source!, token);
            if (compiled.Binary is null)
                return new("SystemError", 0, revealCheckerErrors ? "Checker compilation failed:\n" + compiled.Diagnostic : "Checker is not available. Ask the administrator.");
            checkerBinary = compiled.Binary;
        }

        var artifacts = new Dictionary<string, byte[]> { [profile.SourceName] = Encoding.UTF8.GetBytes(source) };
        if (profile.Compile is not null)
        {
            var build = await CompileAsync(language, profile.SourceName, artifacts[profile.SourceName], profile.Compile, token);
            if (build.Artifacts is null) return new("CompilationError", 0, build.Diagnostic);
            artifacts = build.Artifacts;
        }

        var passed = new bool[problem.Tests.Length];
        var codes = new string[problem.Tests.Length];
        var verdict = "Accepted";
        await using var checkerHost = checkerBinary is null ? null : new CheckerHost(this, checkerBinary);
        for (var index = 0; index < problem.Tests.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var test = problem.Tests[index];
            var run = await RunTestAsync(profile, artifacts, problem, test.Input, token);
            var status = run.Status;
            if (status == "Accepted")
            {
                if (checkerHost is null) status = AnswerChecker.Check(checker, run.Output, test.Output) ? "Accepted" : "WrongAnswer";
                else
                {
                    var check = await checkerHost.CheckAsync(test.Input, run.Output, test.Output, token);
                    if (check.Failure is not null)
                    {
                        codes[index] = "CF";
                        // A broken checker invalidates the whole verdict: never guess.
                        return new("SystemError", passed.Count(p => p), $"Checker failed on test {index + 1}." + (revealCheckerErrors ? "\n" + check.Failure : ""), 0, Fill(codes));
                    }
                    status = check.Accepted ? "Accepted" : "WrongAnswer";
                }
            }
            codes[index] = Codes[status];
            passed[index] = status == "Accepted";
            if (status != "Accepted" && verdict == "Accepted") verdict = status;
        }
        return new(verdict, passed.Count(p => p), "", TestScoring.Score(problem.Tests, passed), codes);
    }

    private static string[] Fill(string[] codes) => codes.Select(c => c ?? "").ToArray();

    private sealed record RunResult(string Status, string Output);

    private async Task<RunResult> RunTestAsync(LanguageProfile profile, Dictionary<string, byte[]> artifacts, JudgeProblem problem, string input, CancellationToken token)
    {
        var name = "beast-run-" + Guid.NewGuid().ToString("N");
        try
        {
            await CreateAsync(name, problem.MemoryMb, ["sleep", "120"], token);
            await MustAsync(["start", name], token);
            foreach (var artifact in artifacts) await CopyIntoAsync(name, artifact.Value, "/solution/" + artifact.Key, token);
            var killAfter = (problem.TimeMs / 1000.0 + 2).ToString(CultureInfo.InvariantCulture) + "s";
            string[] command = ["timeout", "--signal=KILL", killAfter, .. profile.Run(problem.MemoryMb)];
            var run = await CommandAsync(["exec", "-i", "--user", "65534:65534", name, .. command], input, problem.TimeMs + 500, OutputLimitBytes, token);
            var oom = await EnsureContainerAsync(name, token);
            var status = run.Overflow ? "OutputLimitExceeded" : oom ? "MemoryLimitExceeded" : run.TimedOut ? "TimeLimitExceeded" : run.Exit != 0 ? "RuntimeError" : "Accepted";
            return new(status, run.Output);
        }
        finally { await RemoveAsync(name); }
    }

    private sealed record BuildResult(Dictionary<string, byte[]>? Artifacts, string Diagnostic);

    private async Task<BuildResult> CompileAsync(string language, string sourceName, byte[] source, string[] compileCommand, CancellationToken token)
    {
        var name = "beast-compile-" + Guid.NewGuid().ToString("N");
        try
        {
            await CreateAsync(name, 1024, ["sleep", "120"], token, true);
            await MustAsync(["start", name], token);
            await CopyIntoAsync(name, source, "/solution/" + sourceName, token);
            var compile = await CommandAsync(["exec", "--user", "65534:65534", name, .. compileCommand], null, 60000, 32768, token);
            if (compile.Exit != 0 || compile.TimedOut || compile.Overflow)
            {
                await EnsureContainerAsync(name, token);
                var diagnostic = compile.TimedOut ? "Compilation exceeded 60 seconds." : compile.Output + "\n" + compile.Error;
                return new(null, diagnostic[..Math.Min(4000, diagnostic.Length)]);
            }
            var packed = await CommandAsync(["exec", name, "python3", "-I", "-B", "/opt/judge/pack.py", language], null, 20000, 24 * 1024 * 1024, token);
            if (packed.Exit != 0 || packed.TimedOut || packed.Overflow) return new(null, "Compiled artifacts must be regular files, up to 16 MB in total.");
            var encoded = JsonSerializer.Deserialize<Dictionary<string, string>>(packed.Output) ?? throw new InvalidDataException();
            if (encoded.Count is < 1 or > 128) throw new InvalidDataException();
            var artifacts = new Dictionary<string, byte[]>(); long total = 0;
            foreach (var entry in encoded)
            {
                if (!Regex.IsMatch(entry.Key, "^[A-Za-z0-9_$.-]+$") || entry.Key is "." or "..") throw new InvalidDataException();
                var bytes = Convert.FromBase64String(entry.Value); total += bytes.Length;
                if (total > MaxArtifactBytes) throw new InvalidDataException();
                artifacts.Add(entry.Key, bytes);
            }
            return new(artifacts, "");
        }
        finally { await RemoveAsync(name); }
    }

    private sealed record CheckerBuild(byte[]? Binary, string Diagnostic);

    private async Task<CheckerBuild> CompileCheckerAsync(string source, CancellationToken token)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        if (_checkers.TryGetValue(key, out var cached)) return new(cached, "");
        // testlib.h is part of the sandbox image at /opt/judge/testlib.h.
        var build = await CompileAsync("C++", "checker.cpp", Encoding.UTF8.GetBytes(source),
            ["g++", "-std=c++20", "-O2", "-pipe", "-I/opt/judge", "/solution/checker.cpp", "-o", "/tmp/main"], token);
        if (build.Artifacts is null || !build.Artifacts.TryGetValue("main", out var binary)) return new(null, build.Diagnostic);
        if (_checkers.Count > 32) _checkers.Clear();
        _checkers[key] = binary;
        return new(binary, "");
    }

    private sealed record CheckResult(bool Accepted, string? Failure);

    /// <summary>
    /// Runs the administrator's checker in its own sandbox, separate from the participant's program, as
    /// <c>checker input.txt output.txt answer.txt</c>. testlib exit codes: 0 OK, 1 WA, 2 PE (counted as WA);
    /// anything else, including a timeout, is a checker failure.
    /// </summary>
    private sealed class CheckerHost(DockerRunner runner, byte[] binary) : IAsyncDisposable
    {
        private string? _name;
        private DateTimeOffset _created;

        public async Task<CheckResult> CheckAsync(string input, string output, string answer, CancellationToken token)
        {
            await EnsureAsync(token);
            foreach (var (file, text) in new[] { ("input.txt", input), ("output.txt", output), ("answer.txt", answer) })
                await runner.CopyIntoAsync(_name!, Encoding.UTF8.GetBytes(text), "/solution/" + file, token);
            var run = await CommandAsync(["exec", "--user", "65534:65534", _name!, "timeout", "--signal=KILL", "15s",
                "/solution/checker", "/solution/input.txt", "/solution/output.txt", "/solution/answer.txt"], null, CheckerTimeoutMs + 2000, 65536, token);
            var message = (run.Error + "\n" + run.Output).Trim();
            message = message[..Math.Min(1000, message.Length)];
            if (run.TimedOut) return new(false, "Checker exceeded 15 seconds.");
            if (await EnsureContainerAsync(_name!, token)) return new(false, "Checker exceeded 512 MB.");
            return run.Exit switch
            {
                0 => new(true, null),
                1 or 2 => new(false, null),
                _ => new(false, $"Exit code {run.Exit}. {message}")
            };
        }

        private async Task EnsureAsync(CancellationToken token)
        {
            if (_name is not null && DateTimeOffset.UtcNow - _created < CheckerContainerLifetime) return;
            await DisposeAsync();
            var name = "beast-check-" + Guid.NewGuid().ToString("N");
            _name = name; _created = DateTimeOffset.UtcNow;
            await runner.CreateAsync(name, 512, ["sleep", "300"], token);
            await runner.MustAsync(["start", name], token);
            await runner.CopyIntoAsync(name, binary, "/solution/checker", token);
        }

        public async ValueTask DisposeAsync()
        {
            if (_name is null) return;
            var name = _name; _name = null;
            await RemoveAsync(name);
        }
    }

    private static async Task<bool> EnsureContainerAsync(string name, CancellationToken token)
    {
        var state = await CommandAsync(["inspect", "--format", "{{.State.OOMKilled}}", name], null, 10000, 4096, token);
        if (state.Exit != 0 || state.TimedOut || state.Overflow) throw new IOException("Cannot inspect execution environment.");
        return state.Output.Trim() == "true";
    }

    private async Task CreateAsync(string name, int memory, string[] command, CancellationToken token, bool compilation = false)
    {
        if (memory is < 16 or > 2048) throw new InvalidOperationException("Invalid memory limit.");
        await MustAsync(["create", "--name", name, "--label", "beastcontest.sandbox=true", "--label", "beastcontest.created=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
            "--pids-limit", compilation ? "128" : "64", "--memory", memory + "m", "--memory-swap", memory + "m", "--cpus", "1",
            "--ulimit", "fsize=268435456:268435456", "--ulimit", compilation ? "nofile=1024:1024" : "nofile=256:256", "--ulimit", "core=0:0",
            "--tmpfs", compilation ? "/tmp:rw,noexec,nosuid,size=512m,mode=1777" : "/tmp:rw,noexec,nosuid,size=64m,mode=1777",
            "--tmpfs", "/solution:rw,exec,nosuid,size=16m,mode=0755", "--user", "0:0", "--workdir", "/solution", "--log-driver", "none", image, .. command], token);
    }

    private async Task CopyIntoAsync(string container, byte[] bytes, string destination, CancellationToken token)
    {
        if (bytes.Length > MaxArtifactBytes) throw new IOException("Compiled executable is too large.");
        // unlink first: files are read-only and root has no CAP_DAC_OVERRIDE, so they cannot be overwritten in place.
        var result = await CommandAsync(["exec", "-i", container, "python3", "-I", "-B", "-c",
            "import sys,base64,pathlib; p=pathlib.Path(sys.argv[1]); p.unlink(missing_ok=True); p.write_bytes(base64.b64decode(sys.stdin.buffer.read())); p.chmod(0o555)",
            destination], Convert.ToBase64String(bytes), 20000, 4096, token);
        if (result.Exit != 0 || result.TimedOut || result.Overflow) throw new IOException("Cannot stage solution: " + result.Error);
    }

    private async Task MustAsync(string[] args, CancellationToken token)
    {
        var result = await CommandAsync(args, null, 20000, 16384, token);
        if (result.Exit != 0 || result.TimedOut || result.Overflow) throw new IOException("Docker operation failed: " + args[0] + " " + result.Error);
    }

    private static async Task RemoveAsync(string name) => await CommandAsync(["rm", "-f", name], null, 10000, 4096, CancellationToken.None);

    public static async Task<CommandResult> CommandAsync(string[] args, string? input, int timeout, int limit, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Docker CLI.");
        var overflow = 0;
        async Task<string> ReadAsync(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[4096];
            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer, deadline.Token);
                    if (read == 0) break;
                    var remaining = limit - text.Length;
                    text.Append(buffer, 0, Math.Min(read, remaining));
                    if (read > remaining) { Interlocked.Exchange(ref overflow, 1); deadline.Cancel(); break; }
                }
            }
            catch (OperationCanceledException) { }
            return text.ToString();
        }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        var timedOut = false;
        try
        {
            if (input is not null) try { await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token); } catch (IOException) { }
            process.StandardInput.Close(); await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = Volatile.Read(ref overflow) == 0;
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        var output = await stdout; var error = await stderr; token.ThrowIfCancellationRequested();
        return new(process.ExitCode, output, error, timedOut, overflow != 0);
    }
}
