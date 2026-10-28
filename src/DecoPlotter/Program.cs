using DecoPlotter.Deco;
using DecoPlotter.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<DecoOptions>(builder.Configuration.GetSection(DecoOptions.Section));
builder.Services.AddSingleton<DecoClient>();
builder.Services.AddSingleton<SampleStore>();
builder.Services.AddSingleton<LiveState>();
builder.Services.AddHostedService<PollingService>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
builder.WebHost.ConfigureKestrel(k => k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

static string NormalizeMac(string mac) => mac.Trim().ToUpperInvariant().Replace(':', '-');
static (long From, long To) Range(int minutes)
{
    var to = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    return (to - Math.Clamp(minutes, 1, 60 * 24 * 365) * 60L, to);
}

var api = app.MapGroup("/api");

app.MapGet("/metrics", (LiveState s) =>
    Results.Text(MetricsFormatter.Render(s.Current), "text/plain; version=0.0.4; charset=utf-8"));

api.MapGet("/status", (LiveState s) =>
{
    var c = s.Current;
    return new { c.LastSuccess, c.LastAttempt, c.LastError, c.Router, deviceCount = c.Devices.Count };
});

api.MapGet("/router/current", (LiveState s) => s.Current.Router);

api.MapGet("/router/history", (SampleStore store, int minutes = 60, int maxPoints = 600) =>
{
    var (from, to) = Range(minutes);
    return store.Query(SampleStore.RouterKey, from, to, maxPoints);
});

api.MapGet("/devices", (LiveState s, bool all = false) =>
    s.Current.Devices.Where(d => all || d.Online).OrderByDescending(d => d.DownBytesPerSec + d.UpBytesPerSec).ThenBy(d => d.Name));

api.MapGet("/devices/{mac}", (string mac, LiveState s) =>
    s.Current.Devices.FirstOrDefault(d => d.Mac == NormalizeMac(mac)) is { } d ? Results.Ok(d) : Results.NotFound());

api.MapGet("/devices/{mac}/history", (string mac, SampleStore store, int minutes = 60, int maxPoints = 600) =>
{
    var (from, to) = Range(minutes);
    return store.Query(NormalizeMac(mac), from, to, maxPoints);
});

api.MapGet("/usage", (SampleStore store, string range = "today", string? from = null, string? to = null) =>
{
    if (!TryResolveDays(range, from, to, out var f, out var t, out var error)) return Results.BadRequest(error);
    return Results.Ok(store.QueryUsage(f, t));
});

api.MapGet("/usage/{mac}/daily", (string mac, SampleStore store, string range = "last30", string? from = null, string? to = null) =>
{
    if (!TryResolveDays(range, from, to, out var f, out var t, out var error)) return Results.BadRequest(error);
    return Results.Ok(store.QueryDaily(mac.Equals("router", StringComparison.OrdinalIgnoreCase) ? SampleStore.RouterKey : NormalizeMac(mac), f, t));
});

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
