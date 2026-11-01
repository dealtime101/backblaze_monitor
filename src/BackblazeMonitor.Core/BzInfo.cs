using System.Globalization;
using System.Text.RegularExpressions;

namespace BackblazeMonitor.Core;

/// <summary>
/// Backblaze's own throttle settings, read from bzinfo.xml. If its throttle is lower than our QoS limit,
/// it is the one that decides.
/// </summary>
/// <param name="Auto">net_auto_throttle: automatic threading/throttle.</param>
/// <param name="Throttle">net_throttle: Backblaze's slider, a percentage (shown as such in manual mode). Not a bit rate.</param>
/// <param name="Threads">num_backup_threads.</param>
/// <param name="Schedule">backup_schedule_type ("continuously", "once_per_day", ...); empty = unknown.</param>
public sealed record BzInfo(bool Auto, int Throttle, int Threads, string Schedule)
{
    private static readonly RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex AutoRx = new("(?<![A-Za-z0-9_])net_auto_throttle=\"([^\"]*)\"", Opt);
    private static readonly Regex ThrottleRx = new("(?<![A-Za-z0-9_])net_throttle=\"([^\"]*)\"", Opt);
    private static readonly Regex ThreadsRx = new("(?<![A-Za-z0-9_])num_backup_threads=\"([^\"]*)\"", Opt);
    private static readonly Regex ScheduleRx = new("(?<![A-Za-z0-9_])backup_schedule_type=\"([^\"]*)\"", Opt);

    /// <summary>Nothing read yet.</summary>
    public static readonly BzInfo Unknown = new(false, 0, 0, "") { Known = false };

    /// <summary>
    /// At least one setting was read from the file. Until then the values are placeholders: the line must not
    /// say "manual, 0 %" nor offer a link that switches Backblaze to automatic on a configuration nobody has seen.
    /// </summary>
    public bool Known { get; init; } = true;

    /// <summary>Text shown when the file was read but holds none of the settings.</summary>
    public const string UnreadText = "Backblaze: configuration not read";

    /// <summary>Text shown when bzinfo.xml does not exist.</summary>
    public const string NotFoundText = "Backblaze: configuration not found";

    /// <summary>
    /// Parses the file text. An attribute that is absent (or not a number where one is expected) keeps the
    /// value of <paramref name="previous"/>.
    /// </summary>
    public static BzInfo Parse(string raw, BzInfo? previous = null)
    {
        var info = previous ?? Unknown;
        var found = false;
        var m = AutoRx.Match(raw);
        found |= m.Success;
        if (m.Success) info = info with { Auto = string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase) };
        m = ThrottleRx.Match(raw);
        found |= m.Success;
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var throttle)) info = info with { Throttle = throttle };
        m = ThreadsRx.Match(raw);
        found |= m.Success;
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threads)) info = info with { Threads = threads };
        m = ScheduleRx.Match(raw);
        found |= m.Success;
        if (m.Success) info = info with { Schedule = m.Groups[1].Value };
        return found ? info with { Known = true } : info;
    }

    /// <summary>
    /// Line under the limit menu. Automatic: Backblaze adjusts its threads and slider by itself and the QoS
    /// limit becomes the cap (the intended configuration). Manual: its own throttle may be lower than ours
    /// and then decides, mainly through the number of threads.
    /// </summary>
    public string StateText => !Known ? UnreadText : Auto
        ? "Backblaze: automatic - the limit above is the cap"
        : string.Format(CultureInfo.InvariantCulture, "Backblaze manual: {0} %, {1} thread(s) - click for auto", Throttle, Threads);

    /// <summary>In manual mode the whole text is a link that switches back to auto: true when the link exists.</summary>
    public bool HasLink => Known && !Auto;
}

/// <summary>Outcome of a <see cref="BzInfoSync.Sync"/> pass.</summary>
public enum BzInfoSyncResult
{
    /// <summary>bzinfo.xml does not exist.</summary>
    NotFound,

    /// <summary>Same timestamp as the last successful read.</summary>
    Unchanged,

    /// <summary>Empty, unreadable (locked) or cut (no closing tag): the timestamp is not remembered, retried at the next pass.</summary>
    Unreadable,

    /// <summary>Read and parsed.</summary>
    Updated,
}

/// <summary>
/// bzinfo.xml cached by its timestamp. The timestamp is remembered only after a successful read: NTFS dates
/// the write, not the close, and a tick that lands on the locked file would otherwise never re-read it
/// (blank or stale line, unknown schedule).
/// </summary>
public sealed class BzInfoSync
{
    private DateTime? _stamp;

    /// <summary>Last parsed values.</summary>
    public BzInfo Info { get; private set; } = BzInfo.Unknown;

    /// <summary>Forces a re-read at the next pass (after switching Backblaze to auto).</summary>
    public void Invalidate() => _stamp = null;

    /// <param name="stampUtc">Write time of bzinfo.xml, or <c>null</c> when the file does not exist.</param>
    /// <param name="readText">Reads the file text (only called when the timestamp changed).</param>
    public BzInfoSyncResult Sync(DateTime? stampUtc, Func<string?> readText)
    {
        if (stampUtc is null) return BzInfoSyncResult.NotFound;
        if (_stamp == stampUtc) return BzInfoSyncResult.Unchanged;
        var raw = readText();
        if (string.IsNullOrEmpty(raw)) return BzInfoSyncResult.Unreadable;
        // Read in the middle of a rewrite: no closing tag (the real files all carry it). Neither the display
        // nor the timestamp moves, the next pass reads again, instead of mixing old and new values
        // (BAC466.33)
        if (!raw.TrimEnd().EndsWith("</bzinfo>", StringComparison.Ordinal)) return BzInfoSyncResult.Unreadable;
        _stamp = stampUtc;
        Info = BzInfo.Parse(raw, Info);
        return BzInfoSyncResult.Updated;
    }
}
