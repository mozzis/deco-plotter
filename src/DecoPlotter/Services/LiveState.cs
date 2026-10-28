using DecoPlotter.Deco;

namespace DecoPlotter.Services;

public sealed record RouterRate(double UpBytesPerSec, double DownBytesPerSec, int OnlineDevices);

public sealed record LiveSnapshot(
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastAttempt,
    string? LastError,
    RouterRate Router,
    IReadOnlyList<DecoClientInfo> Devices);

public sealed class LiveState
{
    private volatile LiveSnapshot _snapshot = new(null, null, null, new RouterRate(0, 0, 0), []);
    public LiveSnapshot Current => _snapshot;
    public void Update(LiveSnapshot s) => _snapshot = s;
}
