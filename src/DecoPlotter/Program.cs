using DecoPlotter.Deco;
using DecoPlotter.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<DecoOptions>(builder.Configuration.GetSection(DecoOptions.Section));
builder.Services.AddSingleton<DecoClient>();
builder.Services.AddSingleton<SampleStore>();
builder.Services.AddSingleton<LiveState>();
builder.Services.AddHostedService<PollingService>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
builder.WebHost.ConfigureKestrel(k => k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30));
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Deco Plotter API";
    doc.Info.Version = "1.0";
    doc.Info.Description = "Bandwidth and device usage collected from a TP-Link Deco mesh. Rates are bytes per second, " +
        "estimated from the per-client speeds the Deco reports; the router figure is the sum over online clients. " +
        "Unix timestamps are in seconds (UTC).";
    return Task.CompletedTask;
}));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();
app.MapScalarApiReference("/docs", o => o.WithTitle("Deco Plotter API").WithOpenApiRoutePattern("/openapi/v1.json"));

static string NormalizeMac(string mac) => mac.Trim().ToUpperInvariant().Replace(':', '-');
static (long From, long To) Range(int minutes, long? to = null)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var end = Math.Min(to ?? now, now);
    return (end - Math.Clamp(minutes, 1, 60 * 24 * 365) * 60L, end);
}

var api = app.MapGroup("/api");

app.MapGet("/metrics", (LiveState s) =>
        Results.Text(MetricsFormatter.Render(s.Current), "text/plain; version=0.0.4; charset=utf-8"))
    .WithTags("Metrics").WithSummary("Prometheus metrics")
    .WithDescription("Current router and per-device rates (gauges) in Prometheus text exposition format.")
    .Produces<string>(200, "text/plain");

api.MapGet("/status", (LiveState s, SampleStore store) =>
    {
        var c = s.Current;
        return new StatusResponse(c.LastSuccess, c.LastAttempt, c.LastError, c.Router, c.Devices.Count, store.OldestSample());
    })
    .WithTags("Router").WithSummary("Polling status")
    .WithDescription("Result of the last poll of the Deco, the current router rate, and the oldest stored sample (Unix seconds).");

api.MapGet("/router/current", (LiveState s) => s.Current.Router)
    .WithTags("Router").WithSummary("Current router rate")
    .WithDescription("Upstream/downstream bytes per second, summed over all online clients.");

api.MapGet("/router/history", (SampleStore store, int minutes = 60, int maxPoints = 600, long? to = null) =>
    {
        var (from, end) = Range(minutes, to);
        return store.Query(SampleStore.RouterKey, from, end, maxPoints);
    })
    .WithTags("Router").WithSummary("Router rate history")
    .WithDescription("Downsampled averages for the window ending at `to` (default now) and `minutes` long, plus peak rates and estimated total bytes. " +
        "`minutes` is clamped to 1..525600; `to` in the future is treated as now. Raw samples are retained for Deco:RetentionHours (default 168).");

api.MapGet("/devices", (LiveState s, bool all = false) =>
        s.Current.Devices.Where(d => all || d.Online).OrderByDescending(d => d.DownBytesPerSec + d.UpBytesPerSec).ThenBy(d => d.Name))
    .WithTags("Devices").WithSummary("List devices")
    .WithDescription("Connected devices with their current rates, busiest first. Pass `all=true` to include offline devices.");

api.MapGet("/devices/{mac}", (string mac, LiveState s) =>
        s.Current.Devices.FirstOrDefault(d => d.Mac == NormalizeMac(mac)) is { } d ? Results.Ok(d) : Results.NotFound())
    .WithTags("Devices").WithSummary("Get one device")
    .WithDescription("Looks a device up by MAC address (colon or dash separated, any case).")
    .Produces<DecoClientInfo>(200).Produces(404);

api.MapGet("/devices/{mac}/history", (string mac, SampleStore store, int minutes = 60, int maxPoints = 600, long? to = null) =>
    {
        var (from, end) = Range(minutes, to);
        return store.Query(NormalizeMac(mac), from, end, maxPoints);
    })
    .WithTags("Devices").WithSummary("Device rate history")
    .WithDescription("Same as the router history, for one device. Empty `points` if the device was never seen in the window.");

api.MapGet("/usage", (SampleStore store, string range = "today", string? from = null, string? to = null) =>
    {
        if (!TryResolveDays(range, from, to, out var f, out var t, out var error)) return Results.BadRequest(error);
        return Results.Ok(store.QueryUsage(f, t));
    })
    .WithTags("Usage").WithSummary("Data usage per device")
    .WithDescription("Estimated bytes transferred per device and for the router over a calendar range (server local days, kept permanently). " +
        "`range` is one of today, yesterday, week (from Monday), month, last7, last30 or custom; custom requires `from` and `to` as yyyy-MM-dd.")
    .Produces<UsageResult>(200).Produces<string>(400);

api.MapGet("/usage/{mac}/daily", (string mac, SampleStore store, string range = "last30", string? from = null, string? to = null) =>
    {
        if (!TryResolveDays(range, from, to, out var f, out var t, out var error)) return Results.BadRequest(error);
        return Results.Ok(store.QueryDaily(mac.Equals("router", StringComparison.OrdinalIgnoreCase) ? SampleStore.RouterKey : NormalizeMac(mac), f, t));
    })
    .WithTags("Usage").WithSummary("Daily usage for one device")
    .WithDescription("Per-day byte totals for a MAC address, or the literal `router`. Takes the same range parameters as /api/usage.")
    .Produces<IReadOnlyList<DailyUsage>>(200).Produces<string>(400);

app.Run();

static bool TryResolveDays(string range, string? from, string? to, out string f, out string t, out string? error)
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    error = null;
    DateOnly a, b;
    switch (range.ToLowerInvariant())
    {
        case "today": a = b = today; break;
        case "yesterday": a = b = today.AddDays(-1); break;
        case "week": a = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); b = today; break; // weeks start Monday
        case "month": a = new DateOnly(today.Year, today.Month, 1); b = today; break;
        case "last7": a = today.AddDays(-6); b = today; break;
        case "last30": a = today.AddDays(-29); b = today; break;
        case "custom":
            if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", out a) || !DateOnly.TryParseExact(to, "yyyy-MM-dd", out b) || b < a)
            {
                f = t = "";
                error = "custom range needs from and to as yyyy-MM-dd, with to >= from.";
                return false;
            }
            break;
        default:
            f = t = "";
            error = "range must be today, yesterday, week, month, last7, last30 or custom.";
            return false;
    }
    f = a.ToString("yyyy-MM-dd");
    t = b.ToString("yyyy-MM-dd");
    return true;
}

public sealed record StatusResponse(
    DateTimeOffset? LastSuccess, DateTimeOffset? LastAttempt, string? LastError,
    RouterRate Router, int DeviceCount, long? OldestSample);
