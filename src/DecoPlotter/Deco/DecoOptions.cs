namespace DecoPlotter.Deco;

public sealed class DecoOptions
{
    public const string Section = "Deco";

    /// <summary>IP or host name of any Deco unit (normally the main gateway).</summary>
    public string Host { get; set; } = "192.168.68.1";

    public string Username { get; set; } = "admin";

    /// <summary>The TP-Link ID / Deco app password (or the local manager account password).</summary>
    public string Password { get; set; } = "";

    public int PollIntervalSeconds { get; set; } = 5;

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Pause between polls after a rejected login, to avoid triggering the router's lockout.</summary>
    public int AuthBackoffMinutes { get; set; } = 15;

    /// <summary>Pause after the router reports a lockout (-5003); the Deco web UI quotes about 2 hours.</summary>
    public int LockoutBackoffMinutes { get; set; } = 125;

    public int RetentionHours { get; set; } = 24 * 7;

    /// <summary>
    /// Deco reports client up_speed/down_speed in kilobytes per second; this converts it to bytes per second.
    /// </summary>
    public double SpeedUnitBytes { get; set; } = 1000;

    public string DatabasePath { get; set; } = "data/deco.db";
}
