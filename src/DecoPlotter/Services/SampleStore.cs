using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using DecoPlotter.Deco;

namespace DecoPlotter.Services;

public sealed record HistoryPoint(long T, double Up, double Down);

public sealed record HistoryResult(
    string Key, long From, long To, int BucketSeconds,
    double TotalUpBytes, double TotalDownBytes, double PeakUp, double PeakDown,
    IReadOnlyList<HistoryPoint> Points);

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
            """);
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
        foreach (var (key, up, down) in rows)
        {
            pTs.Value = ts; pMac.Value = key; pUp.Value = up; pDown.Value = down; pDt.Value = dtSeconds;
            cmd.ExecuteNonQuery();
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
