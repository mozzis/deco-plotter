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

app.Run();
