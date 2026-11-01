using System.Globalization;

namespace BackblazeMonitor.Core;

/// <summary>
/// Time between two local times. Subtracting two local <see cref="DateTime"/> values counts the hour a
/// clock change adds or removes; going through UTC gives the time that really passed.
/// </summary>
public static class Elapsed
{
    /// <summary>Minutes from <paramref name="from"/> to <paramref name="to"/>, both local times.</summary>
    public static double Minutes(DateTime from, DateTime to, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        return (ToUtc(to, zone) - ToUtc(from, zone)).TotalMinutes;
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        // MinValue stands for "never": converting it would fall below the representable range
        if (local <= DateTime.MinValue.AddDays(2)) return local;
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1); // the skipped hour of spring forward
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }
}

/// <summary>Alert messages and the logic that decides when they ring.</summary>
public static class Alerts
{
    public const string StopTitle = "Backblaze stopped";
    public const string StallTitle = "Backblaze is not sending";
    public const string StopMessage = "The service stopped without going through the monitor: backups are suspended.";

    /// <summary>
    /// Stop alert: only on the transition to stopped (a service already stopped at launch does not ring),
    /// and not within 5 min of a stop or restart requested here: Stop-Service and Restart-Service both go
    /// through Stopped. Returns the message, or <c>null</c>.
    /// </summary>
    /// <param name="prev">Service state on the previous tick; empty = not seen yet.</param>
    /// <param name="status">Service state now.</param>
    /// <param name="askedAt">Last stop requested from the tile.</param>
    public static string? GetStopAlert(string prev, string status, DateTime askedAt, DateTime now, PauseWitness? witness = null)
    {
        if (!Eq(status, "Stopped") || prev.Length == 0 || Eq(prev, "Stopped")) return null;
        if (Elapsed.Minutes(askedAt, now) < 5) return null;
        // A stop wanted by the external tool is not a failure: not within 5 min of its witness
        if (witness is not null && Elapsed.Minutes(witness.At, now) < 5) return null;
        return StopMessage;
    }

    /// <summary>
    /// Send alert: nothing has been sent since the threshold while files are waiting. "Once per day" allows
    /// 24 h more (23 h 13 of normal silence between two windows, measured on one machine); manual or unknown
    /// schedule: never.
    /// </summary>
    /// <param name="since">Start of the silence; <c>null</c> = unknown.</param>
    /// <param name="files">Files waiting; <c>null</c> = unknown.</param>
    /// <param name="schedule">Backblaze schedule type ("continuously", "once_per_day", other).</param>
    /// <param name="stallMin">Threshold in minutes; 0 = never.</param>
    public static string? GetStallAlert(DateTime? since, long? files, string schedule, int stallMin, DateTime now)
    {
        if (stallMin <= 0 || since is null || !(files > 0)) return null;
        int limit;
        if (Eq(schedule, "continuously")) limit = stallMin;
        else if (Eq(schedule, "once_per_day")) limit = 1440 + stallMin;
        else return null;
        // PowerShell's [int] cast rounds to the nearest even integer
        var min = (int)Math.Round(Elapsed.Minutes(since.Value, now), MidpointRounding.ToEven);
        if (min < limit) return null;
        return string.Format(
            BzConstants.Invariant,
            "Nothing has been sent for {0} h {1:00} while {2:N0} files are waiting.",
            (int)Math.Floor(min / 60.0), min % 60, files);
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A stop wanted by an external tool, as its witness file tells it.</summary>
/// <param name="At">Write time of the witness (local).</param>
/// <param name="Why">First line of the witness: the reason; empty when there is none.</param>
public sealed record PauseWitness(DateTime At, string Why)
{
    /// <summary>The witness from its write time and text; <c>null</c> when the text could not be read.</summary>
    public static PauseWitness? Parse(DateTime at, string? text) =>
        text is null ? null : new PauseWitness(at, text.Split('\n')[0].Trim());

    /// <summary>Reads the witness file; <c>null</c> when it does not exist or cannot be read.</summary>
    public static PauseWitness? Read(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.GetLastWriteTime(path), SharedFile.ReadText(path)) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Alert state across ticks: the previous service state, when it has been running, the last stop requested
/// here, and which silence was already reported (one alert per silence).
/// </summary>
public sealed class AlertTracker
{
    /// <summary>Service state on the previous tick; empty = not seen yet.</summary>
    public string SvcPrev { get; private set; } = "";

    /// <summary>Last stop or restart requested from the tile.</summary>
    public DateTime AskedAt { get; set; } = DateTime.MinValue;

    /// <summary>Service seen running since.</summary>
    public DateTime? RunSince { get; private set; }

    /// <summary>Start of the silence already reported.</summary>
    public DateTime? StallFor { get; private set; }

    /// <summary>Minutes without sending before the alert; 0 = never.</summary>
    public int StallMin { get; set; } = 120;

    /// <summary>
    /// The witness, unless it is older than the last time the service was seen running (then it is the leftover
    /// of an earlier pause: the tool paused, Start was pressed here, and this stop is something else).
    /// </summary>
    public PauseWitness? Effective(PauseWitness? witness) =>
        witness is not null && RunSince is { } since && witness.At < since ? null : witness;

    /// <summary>Records a stop or restart requested here: no stop alert for the next 5 minutes.</summary>
    public void MarkAsked(DateTime now) => AskedAt = now;

    /// <summary>
    /// Feeds the service state read on this tick ("" when the service does not exist). Returns the stop-alert
    /// message to show, or <c>null</c>.
    /// </summary>
    public string? OnServiceStatus(string status, DateTime now, PauseWitness? witness = null)
    {
        var msg = Alerts.GetStopAlert(SvcPrev, status, AskedAt, now, witness);
        if (status == "Running" && SvcPrev != "Running") RunSince = now;
        SvcPrev = status;
        return msg;
    }

    /// <summary>
    /// Send alert for this tick, or <c>null</c>. One alert per silence: a new send or a service restart opens
    /// another. The silence only counts from the moment the service started.
    /// </summary>
    /// <param name="lastSent">Last block sent seen in the log.</param>
    /// <param name="remainingFiles">Remaining files from the report; <c>null</c> = unknown.</param>
    /// <param name="bzSchedule">Backblaze schedule type; empty = unknown.</param>
    public string? CheckStall(DateTime? lastSent, long? remainingFiles, string bzSchedule, DateTime now)
    {
        if (SvcPrev != "Running") return null;
        var since = RunSince;
        if (lastSent is not null && (since is null || lastSent > since)) since = lastSent;
        var msg = Alerts.GetStallAlert(since, remainingFiles, bzSchedule, StallMin, now);
        if (msg is null || since == StallFor) return null;
        StallFor = since;
        return msg;
    }
}

/// <summary>Kind of a service state, for the status dot, text color and button states.</summary>
public enum ServiceStatusKind
{
    NotFound,
    Running,
    Stopped,

    /// <summary>A stable state that does not change by itself: it stays until someone acts (Stop or Restart).</summary>
    Paused,
    Transitional,
}

/// <summary>Service state and start type in the tile's words.</summary>
public static class ServiceText
{
    /// <summary>
    /// State and start type: ServiceController returns raw enum names ("StartPending", "Automatic").
    /// Only transitional states end with "..."; an unknown value is displayed as is.
    /// </summary>
    public static string Get(string v)
    {
        if (v.Equals("StartPending", StringComparison.OrdinalIgnoreCase)) return "Starting...";
        if (v.Equals("StopPending", StringComparison.OrdinalIgnoreCase)) return "Stopping...";
        if (v.Equals("ContinuePending", StringComparison.OrdinalIgnoreCase)) return "Resuming...";
        if (v.Equals("PausePending", StringComparison.OrdinalIgnoreCase)) return "Pausing...";
        if (v.Equals("Paused", StringComparison.OrdinalIgnoreCase)) return "Paused";
        if (v.Equals("Automatic", StringComparison.OrdinalIgnoreCase)) return "Automatic";
        if (v.Equals("Manual", StringComparison.OrdinalIgnoreCase)) return "Manual";
        if (v.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return "Disabled";
        return v;
    }

    /// <summary>Kind of a service state; <c>null</c> or empty = the service does not exist (the alert tracker says "").</summary>
    public static ServiceStatusKind Classify(string? status)
    {
        if (string.IsNullOrEmpty(status)) return ServiceStatusKind.NotFound;
        if (status == "Running") return ServiceStatusKind.Running;
        if (status == "Stopped") return ServiceStatusKind.Stopped;
        if (status == "Paused") return ServiceStatusKind.Paused;
        return ServiceStatusKind.Transitional;
    }

    /// <summary>Status label: "Running", "Stopped", a transitional state in plain words, or "Service not found".</summary>
    public static string GetStatusText(string? status) => Classify(status) switch
    {
        ServiceStatusKind.NotFound => "Service not found",
        ServiceStatusKind.Running => "Running",
        ServiceStatusKind.Stopped => "Stopped",
        _ => Get(status!),
    };

    /// <summary>Status label of a service stopped on purpose by an external tool (orange, not the red of a failure).</summary>
    public const string PausedByWitness = "Paused (arr-watch)";

    /// <summary>Details line when the service does not exist.</summary>
    public static string NotFoundDetails => $"'{BzConstants.ServiceName}' does not exist";

    /// <summary>
    /// Details line: "Automatic  -  PID 1234  -  45.2 MB". A CIM failure is reported as "PID ?".
    /// </summary>
    /// <param name="startType">Raw start type ("Automatic", "Manual", "Disabled").</param>
    /// <param name="processId">Process id; <c>null</c> or 0 = service not running.</param>
    /// <param name="workingSetBytes">Working set of the process; <c>null</c> = not available.</param>
    /// <param name="pidFailed">The PID query threw.</param>
    /// <param name="stopReason">Reason written by the external tool that stopped the service; empty = none.</param>
    public static string GetDetails(string startType, int? processId, long? workingSetBytes, bool pidFailed = false, string? stopReason = null)
    {
        var parts = new List<string> { Get(startType) };
        if (!string.IsNullOrEmpty(stopReason)) parts.Add(stopReason);
        if (pidFailed)
        {
            parts.Add("PID ?");
        }
        else if (processId > 0)
        {
            parts.Add("PID " + processId.Value.ToString(CultureInfo.InvariantCulture));
            if (workingSetBytes is not null)
            {
                parts.Add((workingSetBytes.Value / (1024.0 * 1024)).ToString("N1", BzConstants.Invariant) + " MB");
            }
        }

        return string.Join("  -  ", parts);
    }
}
