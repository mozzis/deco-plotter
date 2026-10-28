using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using DecoPlotter.Deco;

namespace DecoPlotter.Services;

public sealed record HistoryPoint(long T, double Up, double Down);

public sealed record HistoryResult(
    string Key, long From, long To, int BucketSeconds,
    double TotalUpBytes, double TotalDownBytes, double PeakUp, double PeakDown,
    IReadOnlyList<HistoryPoint> Points);

public sealed record UsageRow(string Mac, string Name, string Ip, double UpBytes, double DownBytes)
{
    public double TotalBytes => UpBytes + DownBytes;
}

public sealed record UsageResult(string From, string To, UsageRow Router, IReadOnlyList<UsageRow> Devices);

public sealed record DailyUsage(string Day, double UpBytes, double DownBytes);

public sealed class SampleStore
{
    public const string RouterKey = "ROUTER";
    private readonly string _connectionString;

    public SampleStore(IOptions<DecoOptions> options)
    {
        var path = Path.GetFullPath(options.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS samples(ts INTEGER NOT NULL, mac TEXT NOT NULL, up REAL NOT NULL, down REAL NOT NULL, dt REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_samples_mac_ts ON samples(mac, ts);
            CREATE TABLE IF NOT EXISTS daily(day TEXT NOT NULL, mac TEXT NOT NULL, up REAL NOT NULL, down REAL NOT NULL, PRIMARY KEY(day, mac));
            CREATE TABLE IF NOT EXISTS devices(mac TEXT PRIMARY KEY, name TEXT NOT NULL, ip TEXT NOT NULL, last_seen INTEGER NOT NULL);
            """);

        // First run after upgrade: seed the rollup from the raw samples already collected.
        Exec(c, """
            INSERT INTO daily(day, mac, up, down)
            SELECT strftime('%Y-%m-%d', ts, 'unixepoch', 'localtime'), mac, SUM(up*dt), SUM(down*dt)
            FROM samples WHERE (SELECT COUNT(*) FROM daily) = 0 GROUP BY 1, 2;
            """);
    }

    public static string DayOf(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("yyyy-MM-dd");

    public void UpsertDevices(long ts, IEnumerable<DecoClientInfo> devices)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO devices(mac,name,ip,last_seen) VALUES($mac,$name,$ip,$ts)
            ON CONFLICT(mac) DO UPDATE SET name=excluded.name, ip=excluded.ip, last_seen=excluded.last_seen
            """;
        var pMac = cmd.Parameters.Add("$mac", SqliteType.Text);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pIp = cmd.Parameters.Add("$ip", SqliteType.Text);
        var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
        foreach (var d in devices)
        {
            pMac.Value = d.Mac; pName.Value = d.Name; pIp.Value = d.Ip; pTs.Value = ts;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public UsageResult QueryUsage(string fromDay, string toDay)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT d.mac, COALESCE(v.name,''), COALESCE(v.ip,''), SUM(d.up), SUM(d.down)
            FROM daily d LEFT JOIN devices v ON v.mac = d.mac
            WHERE d.day >= $from AND d.day <= $to GROUP BY d.mac
            """;
        cmd.Parameters.AddWithValue("$from", fromDay);
        cmd.Parameters.AddWithValue("$to", toDay);
        var rows = new List<UsageRow>();
        UsageRow? router = null;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new UsageRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4));
            if (row.Mac == RouterKey) router = row; else rows.Add(row);
        }
        return new UsageResult(fromDay, toDay, router ?? new UsageRow(RouterKey, "Router", "", 0, 0), rows);
    }

    public IReadOnlyList<DailyUsage> QueryDaily(string key, string fromDay, string toDay)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT day, up, down FROM daily WHERE mac=$mac AND day>=$from AND day<=$to ORDER BY day";
        cmd.Parameters.AddWithValue("$mac", key);
        cmd.Parameters.AddWithValue("$from", fromDay);
        cmd.Parameters.AddWithValue("$to", toDay);
        var list = new List<DailyUsage>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new DailyUsage(r.GetString(0), r.GetDouble(1), r.GetDouble(2)));
        return list;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Add(long ts, double dtSeconds, IEnumerable<(string Key, double Up, double Down)> rows)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO samples(ts,mac,up,down,dt) VALUES($ts,$mac,$up,$down,$dt)";
        var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var pMac = cmd.Parameters.Add("$mac", SqliteType.Text);
        var pUp = cmd.Parameters.Add("$up", SqliteType.Real);
        var pDown = cmd.Parameters.Add("$down", SqliteType.Real);
        var pDt = cmd.Parameters.Add("$dt", SqliteType.Real);

        using var roll = c.CreateCommand();
        roll.Transaction = tx;
        roll.CommandText = """
            INSERT INTO daily(day,mac,up,down) VALUES($day,$mac,$up,$down)
            ON CONFLICT(day,mac) DO UPDATE SET up=up+excluded.up, down=down+excluded.down
            """;
        var rDay = roll.Parameters.Add("$day", SqliteType.Text);
        var rMac = roll.Parameters.Add("$mac", SqliteType.Text);
        var rUp = roll.Parameters.Add("$up", SqliteType.Real);
        var rDown = roll.Parameters.Add("$down", SqliteType.Real);
        rDay.Value = DayOf(ts);

        foreach (var (key, up, down) in rows)
        {
            pTs.Value = ts; pMac.Value = key; pUp.Value = up; pDown.Value = down; pDt.Value = dtSeconds;
            cmd.ExecuteNonQuery();
            rMac.Value = key; rUp.Value = up * dtSeconds; rDown.Value = down * dtSeconds;
            roll.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Purge(long olderThanTs)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM samples WHERE ts < $t";
        cmd.Parameters.AddWithValue("$t", olderThanTs);
        cmd.ExecuteNonQuery();
    }

    public HistoryResult Query(string key, long from, long to, int maxPoints)
    {
        var bucket = (int)Math.Max(1, Math.Ceiling((to - from) / (double)Math.Max(1, maxPoints)));
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT (ts / $b) * $b AS t, AVG(up), AVG(down), SUM(up*dt), SUM(down*dt), MAX(up), MAX(down)
            FROM samples WHERE mac=$mac AND ts>=$from AND ts<=$to GROUP BY t ORDER BY t
            """;
        cmd.Parameters.AddWithValue("$b", bucket);
        cmd.Parameters.AddWithValue("$mac", key);
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);

        var points = new List<HistoryPoint>();
        double totUp = 0, totDown = 0, peakUp = 0, peakDown = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            points.Add(new HistoryPoint(r.GetInt64(0), r.GetDouble(1), r.GetDouble(2)));
            totUp += r.GetDouble(3); totDown += r.GetDouble(4);
            peakUp = Math.Max(peakUp, r.GetDouble(5)); peakDown = Math.Max(peakDown, r.GetDouble(6));
        }
        return new HistoryResult(key, from, to, bucket, totUp, totDown, peakUp, peakDown, points);
    }
}
