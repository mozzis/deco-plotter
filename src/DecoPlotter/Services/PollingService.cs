using DecoPlotter.Deco;
using Microsoft.Extensions.Options;

namespace DecoPlotter.Services;

public sealed class PollingService(
    DecoClient client, SampleStore store, LiveState state, IOptions<DecoOptions> options, ILogger<PollingService> log)
    : BackgroundService
{
    private readonly DecoOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _opt.PollIntervalSeconds));
        if (string.IsNullOrEmpty(_opt.Password))
        {
            const string msg = "Deco:Password is not configured; polling disabled. Set it with 'dotnet user-secrets set Deco:Password ...' and restart.";
            log.LogError(msg);
            state.Update(state.Current with { LastAttempt = DateTimeOffset.UtcNow, LastError = msg });
            return;
        }
        var lastPoll = DateTimeOffset.UtcNow;
        var lastPurge = DateTimeOffset.MinValue;

        // The Deco takes several seconds per request, so wait the interval after each poll finishes
        // rather than on a fixed timer (which would run requests back-to-back).
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            try
            {
                var clients = await client.GetClientsAsync(stoppingToken);
                now = DateTimeOffset.UtcNow;
                var online = clients.Where(c => c.Online).ToList();
                var router = new RouterRate(online.Sum(c => c.UpBytesPerSec), online.Sum(c => c.DownBytesPerSec), online.Count);

                // Cap dt so a long outage does not inflate the data usage totals.
                var dt = Math.Min((now - lastPoll).TotalSeconds, interval.TotalSeconds + 60);
                var rows = online.Select(c => (c.Mac, c.UpBytesPerSec, c.DownBytesPerSec))
                    .Append((SampleStore.RouterKey, router.UpBytesPerSec, router.DownBytesPerSec));
                store.Add(now.ToUnixTimeSeconds(), dt, rows);
                state.Update(new LiveSnapshot(now, now, null, router, clients));
                lastPoll = now;

                if (now - lastPurge > TimeSpan.FromHours(1))
                {
                    store.Purge(now.AddHours(-_opt.RetentionHours).ToUnixTimeSeconds());
                    lastPurge = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (DecoAuthException ex)
            {
                var wait = TimeSpan.FromMinutes(Math.Max(1, _opt.AuthBackoffMinutes));
                if (ex.ErrorCode == -5003) wait = TimeSpan.FromMinutes(Math.Max(wait.TotalMinutes, _opt.LockoutBackoffMinutes));
                log.LogError("{Message} Pausing polling for {Minutes} min.", ex.Message, wait.TotalMinutes);
                state.Update(state.Current with { LastAttempt = now, LastError = $"{ex.Message} Next attempt in {wait.TotalMinutes:0} min." });
                try { await Task.Delay(wait, stoppingToken); } catch (OperationCanceledException) { break; }
                lastPoll = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                log.LogWarning("Poll failed: {Message}", ex.Message);
                var cur = state.Current;
                state.Update(cur with { LastAttempt = now, LastError = ex.Message });
            }
            try { await Task.Delay(interval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
