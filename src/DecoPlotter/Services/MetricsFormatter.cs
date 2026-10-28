using System.Globalization;
using System.Text;

namespace DecoPlotter.Services;

public static class MetricsFormatter
{
    public static string Render(LiveSnapshot s)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        void Header(string name, string help)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        }

        Header("deco_up", "1 if the last poll of the Deco succeeded, else 0.");
        sb.Append("deco_up ").Append(s.LastError is null && s.LastSuccess is not null ? 1 : 0).Append('\n');

        if (s.LastSuccess is { } ok)
        {
            Header("deco_last_success_timestamp_seconds", "Unix time of the last successful poll.");
            sb.Append("deco_last_success_timestamp_seconds ").Append(ok.ToUnixTimeSeconds()).Append('\n');
        }

        Header("deco_router_upload_bytes_per_second", "Router upstream rate (sum of online clients).");
        sb.Append("deco_router_upload_bytes_per_second ").Append(s.Router.UpBytesPerSec.ToString("R", inv)).Append('\n');
        Header("deco_router_download_bytes_per_second", "Router downstream rate (sum of online clients).");
        sb.Append("deco_router_download_bytes_per_second ").Append(s.Router.DownBytesPerSec.ToString("R", inv)).Append('\n');
        Header("deco_devices_online", "Number of online client devices.");
        sb.Append("deco_devices_online ").Append(s.Router.OnlineDevices).Append('\n');

        var online = s.Devices.Where(d => d.Online).ToList();
        Header("deco_device_upload_bytes_per_second", "Per-device upstream rate.");
        foreach (var d in online)
            sb.Append("deco_device_upload_bytes_per_second").Append(Labels(d)).Append(' ').Append(d.UpBytesPerSec.ToString("R", inv)).Append('\n');
        Header("deco_device_download_bytes_per_second", "Per-device downstream rate.");
        foreach (var d in online)
            sb.Append("deco_device_download_bytes_per_second").Append(Labels(d)).Append(' ').Append(d.DownBytesPerSec.ToString("R", inv)).Append('\n');

        return sb.ToString();
    }

    private static string Labels(Deco.DecoClientInfo d) =>
        $"{{mac=\"{Esc(d.Mac)}\",name=\"{Esc(d.Name)}\",ip=\"{Esc(d.Ip)}\",connection=\"{Esc(d.ConnectionType)}\"}}";

    private static string Esc(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
