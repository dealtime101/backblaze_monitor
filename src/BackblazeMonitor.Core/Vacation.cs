using System.Globalization;
using System.Text.RegularExpressions;

namespace BackblazeMonitor.Core;

/// <summary>
/// Optional "vacation mode": the tile asks another machine, over ssh, to hold off whatever stops the service
/// on purpose. Off unless <c>VacationHost</c> and <c>VacationKey</c> are set in settings.txt. The remote end
/// accepts a fixed set of commands only (<c>status</c>, <c>off</c>, <c>on &lt;hours&gt;</c>, hours from
/// <see cref="Hours"/>): the tile never sends anything else.
/// </summary>
public static class Vacation
{
    /// <summary>Durations offered, hours. The remote end accepts exactly these; 336 h (14 days) is its ceiling.</summary>
    public static readonly IReadOnlyList<int> Hours = new[] { 1, 4, 8, 12, 24, 48, 72, 336 };

    /// <summary>Duration of the "Start + vacation" button of the start box.</summary>
    public const int DefaultHours = 48;

    /// <summary>The state is read once per this many ticks (5 min): an ssh round trip is not free.</summary>
    public const int ReadEveryTicks = 100;

    /// <summary>An ssh round trip is cut after this long.</summary>
    public const int TimeoutMs = 25_000;

    private static readonly Regex HostRx = new(@"^[A-Za-z0-9][A-Za-z0-9._:@-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Active = new(@"hold:\s*ACTIF\s*\(\s*([^)]*?)\s*(?:restantes)?\s*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Expired = new(@"hold:\s*expire", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex None = new(@"hold:\s*AUCUN", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Whether the settings describe a usable remote: a host name (no leading dash, no space: it becomes an
    /// ssh argument) and a key path.
    /// </summary>
    public static bool IsConfigured(string? host, string? key) =>
        !string.IsNullOrWhiteSpace(host) && HostRx.IsMatch(host.Trim()) &&
        !string.IsNullOrWhiteSpace(key) && !key.Any(char.IsControl);

    /// <summary>
    /// Arguments of the ssh call, one entry per argument (ProcessStartInfo.ArgumentList quotes them, a key
    /// path with a space stays one argument). Bounded: no prompt, 8 s to connect, only the named key.
    /// </summary>
    public static IReadOnlyList<string> SshArguments(string host, string key, string command) => new[]
    {
        "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none",
        "-i", key.Trim(), "-n", host.Trim(), command,
    };

    /// <summary>The remote command for a duration; 0 = stop the mode. <c>null</c> for a duration not in the list.</summary>
    public static string? Command(int hours) =>
        hours == 0 ? "off" : Hours.Contains(hours) ? "on " + hours.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>Label of a duration in the sub-menu. 336 h is the remote ceiling, not "forever": it says so.</summary>
    public static string Label(int hours)
    {
        if (hours >= 336) return "14 d (336 h, maximum)";
        if (hours >= 24) return string.Format(CultureInfo.InvariantCulture, "{0} d ({1} h)", hours / 24, hours);
        return string.Format(CultureInfo.InvariantCulture, "{0} h", hours);
    }

    /// <summary>
    /// State from the output of the remote <c>status</c> (its words are the remote script's: ACTIF, expire,
    /// AUCUN). Anything else reads as unreadable, never as "no hold".
    /// </summary>
    public static VacationState ParseState(string output)
    {
        var m = Active.Match(output);
        // "hold: ACTIF ()" with nothing inside the brackets still says the mode is on
        if (m.Success) return new VacationState(true, m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "active", false);
        if (Expired.IsMatch(output)) return new VacationState(false, "expired", false);
        if (None.IsMatch(output)) return new VacationState(false, "none", false);
        return VacationState.Unreadable;
    }

    /// <summary>State after a <c>status</c> call: the output when it succeeded, "unreachable" otherwise.</summary>
    public static VacationState FromStatusCall(bool ok, string output) => ok ? ParseState(output) : VacationState.Unreachable;

    /// <summary>Text of the tray menu entry, from the last known state.</summary>
    public static string MenuText(VacationState? state) => "Vacation mode: " + (state?.Text ?? "?");

    /// <summary>
    /// "Stop" is greyed only when the state is KNOWN to hold nothing ("none"). Active or unknown stays offered,
    /// since a hold may be in place, and so does "expired": the remote has not purged it yet (the threads are
    /// still raised until its next tick), and stopping forces the purge now.
    /// </summary>
    public static bool StopEnabled(VacationState? state) => state is null || state.Active || state.Unknown || state.Text == "expired";

    /// <summary>Message after a pose or a stop.</summary>
    public static string ResultMessage(int hours, bool ok) =>
        !ok ? "vacation: failed" : hours > 0 ? "vacation on: " + Label(hours) : "vacation stopped";

    /// <summary>Text of the start box offered when vacation mode is set up.</summary>
    public const string StartAsk =
        "Start the Backblaze service?\n\nIf it was stopped on purpose by another tool, that tool may stop it " +
        "again. To keep it running, use the button below, or \"Vacation mode\" in the tray menu.";

    /// <summary>Question when the hold could not be set from the start box.</summary>
    public const string PoseFailedAsk =
        "Vacation mode could not be set: the service may be stopped again. Start anyway?";
}

/// <summary>Last known state of the vacation mode.</summary>
/// <param name="Active">A hold is in place.</param>
/// <param name="Text">Time left when active, otherwise "none", "expired", "unreadable" or "unreachable".</param>
/// <param name="Unknown">The state could not be read: a hold may be in place.</param>
public sealed record VacationState(bool Active, string Text, bool Unknown)
{
    public static readonly VacationState Unreadable = new(false, "unreadable", true);

    public static readonly VacationState Unreachable = new(false, "unreachable", true);
}
