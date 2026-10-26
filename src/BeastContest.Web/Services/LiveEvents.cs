using System.Text.Json;
using BeastContest.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeastContest.Web.Services;

/// <summary>
/// A change that open pages may want to show. Kinds: submission, rejudge, standings, chat, resync.
/// Payloads carry identifiers only; pages reload data through the usual access-checked services.
/// </summary>
public sealed record LiveEvent(string Kind, string? UserId = null, Guid? ContestId = null, string? ParticipantId = null)
{
    public static readonly LiveEvent Resync = new("resync");
    /// <summary>Events that may affect a user's own submissions and verdicts.</summary>
    public bool AffectsUser(string userId) => Kind is "resync" or "rejudge" || (Kind == "submission" && UserId == userId);
    public bool AffectsContest(Guid contestId) => Kind is "resync" || (Kind is "submission" or "rejudge" or "standings" && ContestId == contestId);
}

/// <summary>
/// Listens to PostgreSQL NOTIFY on one connection and fans events out to open Blazor circuits.
/// The Worker and the site publish with <see cref="NotifyAsync"/>; delivery happens on commit.
/// </summary>
public sealed class LiveEvents(IConfiguration configuration, ILogger<LiveEvents> log) : BackgroundService
{
    public const string Channel = "beast_events";
    private event Action<LiveEvent>? Received;

    public static Task NotifyAsync(AppDbContext db, LiveEvent change, CancellationToken token = default) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_notify({Channel}, {JsonSerializer.Serialize(change)})", token);

    /// <summary>
    /// Calls <paramref name="handler"/> after matching events. Bursts are coalesced into one call,
    /// so a rejudge of a whole contest refreshes each page once rather than once per submission.
    /// </summary>
    public IDisposable Subscribe(Func<LiveEvent, bool> filter, Func<Task> handler)
    {
        var subscription = new Subscription(this, filter, handler, log);
        Received += subscription.OnEvent;
        return subscription;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = configuration.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(connection)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var listener = new NpgsqlConnection(connection);
                await listener.OpenAsync(stoppingToken);
                listener.Notification += (_, args) => Dispatch(args.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", listener)) await listen.ExecuteNonQueryAsync(stoppingToken);
                // Anything may have changed while the listener was offline.
                Publish(LiveEvent.Resync);
                while (!stoppingToken.IsCancellationRequested) await listener.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                log.LogWarning("Live updates unavailable, retrying: {Error}", error.GetType().Name);
                try { await Task.Delay(3000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Dispatch(string payload)
    {
        LiveEvent? change;
        try { change = JsonSerializer.Deserialize<LiveEvent>(payload); } catch (JsonException) { return; }
        if (change is not null) Publish(change);
    }

    private void Publish(LiveEvent change) => Received?.Invoke(change);

    private sealed class Subscription(LiveEvents owner, Func<LiveEvent, bool> filter, Func<Task> handler, ILogger log) : IDisposable
    {
        private int _scheduled;
        private volatile bool _disposed;

        public void OnEvent(LiveEvent change)
        {
            if (_disposed) return;
            bool matches;
            try { matches = filter(change); } catch { return; }
            if (!matches || Interlocked.Exchange(ref _scheduled, 1) == 1) return;
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            await Task.Delay(250);
            Volatile.Write(ref _scheduled, 0);
            if (_disposed) return;
            try { await handler(); }
            catch (Exception error) when (error is not OutOfMemoryException) { log.LogDebug(error, "Live refresh failed."); }
        }

        public void Dispose()
        {
            _disposed = true;
            owner.Received -= OnEvent;
        }
    }
}
