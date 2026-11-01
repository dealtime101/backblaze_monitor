using System.Globalization;

namespace BackblazeMonitor.Core;

/// <summary>Application identity (window title, tray).</summary>
public static class AppInfo
{
    public const string Name = "Backblaze Monitor";
    public const string Version = "1.1";

    /// <summary>Window title: name and version.</summary>
    public static string Title => $"{Name} v{Version}";
}

/// <summary>Tunable constants shared by the whole monitor (ported from the PowerShell script header).</summary>
public static class BzConstants
{
    /// <summary>Name of the Backblaze Windows service.</summary>
    public const string ServiceName = "bzserv";

    /// <summary>Refresh period of the UI timer, in milliseconds.</summary>
    public const int TickMs = 3000;

    /// <summary>Current rate, peak and finish estimate window: 30 min.</summary>
    public const int WindowSec = 1800;

    /// <summary>No new line beyond this: transmission stopped, unless a slow block is in flight.</summary>
    public const int IdleSec = 150;

    /// <summary>Current rate: bytes completed over 2 min.</summary>
    public const int RateSec = 120;

    /// <summary>A gap longer than this, with no block in flight, drops back to zero.</summary>
    public const int GapSec = 180;

    /// <summary>Most recent files kept in the "last files sent" list.</summary>
    public const int RecentMax = 20;

    /// <summary>History slot length in ticks: 15 minutes.</summary>
    public const long SlotTicks = 9_000_000_000L;

    /// <summary>At most one volume sample per disk per hour.</summary>
    public const long VolStepSec = 3600;

    /// <summary>Volume samples older than 8 days are dropped.</summary>
    public const long VolKeepSec = 8 * 86400;

    /// <summary>Volume pace is computed over 7 days.</summary>
    public const long VolRateSec = 7 * 86400;

    /// <summary>Windows QoS policy name used for the upload limit.</summary>
    public const string QosName = "BackblazeMonitorThrottle";

    /// <summary>Executable throttled by the QoS policy.</summary>
    public const string QosAppName = "bztransmit.exe";

    /// <summary>Name of the SYSTEM scheduled task that switches the time window.</summary>
    public const string ScheduleTask = "BackblazeMonitorSchedule";

    /// <summary>Name of the on-demand SYSTEM task that runs the tile's requests.</summary>
    public const string AgentTask = "BackblazeMonitorAgent";

    /// <summary>Largest tray tooltip accepted by NotifyIcon: 127 characters since .NET Core 3 (it was 63 on .NET Framework).</summary>
    public const int TrayTextMax = 127;

    /// <summary>The culture used for every formatted number and day name.</summary>
    public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>English day names, whatever the language of the machine.</summary>
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
}

/// <summary>
/// Locations of the Backblaze files (Windows), built from the system's ProgramData folder: it is not always
/// on C:.
/// </summary>
public static class BzPaths
{
    private static string Data => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static string BzData => Path.Combine(Data, "Backblaze", "bzdata");

    public static string LogDir => Path.Combine(BzData, "bzlogs", "bzreports_lastfilestransmitted");

    public static string InfoPath => Path.Combine(BzData, "bzinfo.xml");

    public static string RemainPath => Path.Combine(BzData, "bzreports", "bzstat_remainingbackup.xml");

    public static string TotalPath => Path.Combine(BzData, "bzreports", "bzstat_totalbackup.xml");

    /// <summary>
    /// Witness an external tool writes just before it stops the service on purpose (first line: the reason)
    /// and deletes before it starts it again. Absent on a machine without such a tool: then nothing changes.
    /// </summary>
    public static string PauseWitnessPath => Path.Combine(Data, "BackblazeMonitor", "arr-watch.txt");

    /// <summary>
    /// PowerShell home used when a caller does not give one (the tests). The tile itself always passes the
    /// real one, resolved from the system folder (AppPaths.PsHome).
    /// </summary>
    public const string DefaultPsHome = @"C:\Windows\System32\WindowsPowerShell\v1.0";
}

/// <summary>A chart period selectable by right-click.</summary>
/// <param name="Text">Menu text.</param>
/// <param name="Sec">Span in seconds; 0 for the live 30-minute curve.</param>
/// <param name="Step">Slot length in seconds; 0 for the live 30-minute curve.</param>
/// <param name="Tip">Tooltip fragment.</param>
public sealed record Period(string Text, int Sec, int Step, string Tip);

/// <summary>The chart periods, in menu order.</summary>
public static class Periods
{
    public static readonly IReadOnlyList<Period> All = new[]
    {
        new Period("30 min", 0, 0, "Last 30 minutes"),
        new Period("24 h", 86400, 900, "Last 24 hours, average per quarter hour"),
        new Period("7 d", 604800, 3600, "Last 7 days, average per hour"),
    };

    public static int Count => All.Count;

    /// <summary>The period at <paramref name="index"/>, kept inside the list: a stale index never throws.</summary>
    public static Period At(int index) => All[Math.Clamp(index, 0, All.Count - 1)];

    /// <summary>Span of the longest period, in seconds (history retention).</summary>
    public static int LongestSec => All[^1].Sec;
}

/// <summary>An upload-limit menu entry.</summary>
/// <param name="Text">Menu text.</param>
/// <param name="Bits">Bits per second; 0 = none, -1 = time window.</param>
public sealed record QosChoice(string Text, long Bits);

/// <summary>The upload-limit menu: None, 1..50 Mbps, "Time window...".</summary>
public static class QosChoices
{
    public static readonly IReadOnlyList<QosChoice> All = Build();

    private static List<QosChoice> Build()
    {
        var list = new List<QosChoice> { new("None", 0) };
        foreach (var m in new[] { 1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50 })
        {
            list.Add(new QosChoice(string.Format(BzConstants.Invariant, "{0} Mbps", m), m * 1_000_000L));
        }

        list.Add(new QosChoice("Time window...", -1));
        return list;
    }

    /// <summary>Bit rates (>= 0) the SYSTEM agent accepts as a request.</summary>
    public static IEnumerable<long> AgentBits(IEnumerable<QosChoice> choices) =>
        choices.Where(c => c.Bits >= 0).Select(c => c.Bits);

    /// <summary>Index of the entry whose rate equals <paramref name="bits"/>, or -1.</summary>
    public static int IndexOf(IReadOnlyList<QosChoice> choices, double bits)
    {
        for (var i = 0; i < choices.Count; i++)
        {
            if (choices[i].Bits == bits) return i;
        }

        return -1;
    }

    /// <summary>
    /// Label for a policy set elsewhere with a value missing from the list: shown as is, in kbps below
    /// 1 Mbps (rounded to the Mbps, 2.5 would read as "3 Mbps" and 400 kbit/s as "0 Mbps").
    /// </summary>
    public static string LabelFor(double bits) =>
        bits >= 1_000_000
            ? (bits / 1_000_000).ToString("0.###", BzConstants.Invariant) + " Mbps"
            : (bits / 1000).ToString("0.###", BzConstants.Invariant) + " kbps";
}
