using System.Globalization;

namespace BackblazeMonitor.Core;

/// <summary>How a privileged action ended.</summary>
public enum ActionOutcome
{
    /// <summary>The action ran and reported success.</summary>
    Done,

    /// <summary>The action failed, was refused, or could not be started.</summary>
    Failed,

    /// <summary>The user declined the UAC prompt: nothing was attempted.</summary>
    Cancelled,

    /// <summary>
    /// The elevated PowerShell did not finish in time and is still running: it may still apply the change.
    /// Neither success nor failure; no new privileged action is accepted until it has exited.
    /// </summary>
    Unknown,
}

/// <summary>How the switch of Backblaze to automatic mode (bzcli) ended.</summary>
public enum BzAutoOutcome
{
    Done,
    Failed,

    /// <summary>bzcli did not finish in time and was killed.</summary>
    TimedOut,
}

/// <summary>Mapping of exit codes and Windows errors to an <see cref="ActionOutcome"/>.</summary>
public static class ActionOutcomes
{
    /// <summary>Win32 error returned when the user declines a UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>Exit code 0 is success; anything else (1 = failure, 2 = request refused by the agent) is a failure.</summary>
    public static ActionOutcome FromExitCode(int exitCode) =>
        exitCode == 0 ? ActionOutcome.Done : ActionOutcome.Failed;

    /// <summary>A start that threw a Win32 error: 1223 means the UAC prompt was declined.</summary>
    public static ActionOutcome FromNativeError(int nativeErrorCode) =>
        nativeErrorCode == ErrorCancelled ? ActionOutcome.Cancelled : ActionOutcome.Failed;

    /// <summary>
    /// Whether a stop or restart that ended this way counts as "requested here" for the stop alert. A failed one
    /// may have left the service stopped half-way: still ours, no false alert. Only a declined UAC prompt
    /// changed nothing (BAC466.14).
    /// </summary>
    public static bool CountsAsAsked(ActionOutcome outcome) => outcome != ActionOutcome.Cancelled;

    /// <summary>Result of an agent request: <c>true</c>/<c>false</c> once it ran.</summary>
    public static ActionOutcome FromBool(bool ok) => ok ? ActionOutcome.Done : ActionOutcome.Failed;
}

/// <summary>Balloon notice shown after a service action that did not succeed.</summary>
/// <param name="Title">Balloon title.</param>
/// <param name="Text">Balloon text.</param>
/// <param name="IsError">true = error icon, false = warning icon.</param>
public sealed record ActionNotice(string Title, string Text, bool IsError);

/// <summary>Texts of the notices and of the limit message label.</summary>
public static class ActionNotices
{
    /// <summary>
    /// Notice for a finished service action, or <c>null</c> when it succeeded. A declined UAC prompt reads
    /// "cancelled", a failure reads "failed" with the service state.
    /// </summary>
    /// <param name="action">The requested action.</param>
    /// <param name="outcome">How it ended.</param>
    /// <param name="statusText">The status label as shown after the action ("Running", "Stopped"...).</param>
    public static ActionNotice? ForServiceAction(ServiceAction action, ActionOutcome outcome, string statusText)
    {
        var verb = ServiceCommands.Verb(action);
        return outcome switch
        {
            ActionOutcome.Cancelled => new ActionNotice($"{verb} cancelled", "UAC prompt declined.", false),
            ActionOutcome.Failed => new ActionNotice($"{verb} failed", $"Backblaze: {statusText}", true),
            ActionOutcome.Unknown => new ActionNotice($"{verb} not finished", $"Still running in the background, result unknown. Backblaze: {statusText}", false),
            _ => null,
        };
    }
}

/// <summary>Texts of the small message next to the limit menu.</summary>
public static class LimitMessages
{
    public const string Working = "working...";
    public const string Applied = "applied";
    public const string Failed = "failed/denied";
    public const string Unfinished = "still running";
    public const string AutoEnabled = "auto enabled";
    public const string AutoFailed = "failed";
    public const string AutoTimedOut = "timed out";

    /// <summary>Message and tone after the switch to automatic mode: a cut run says "timed out", not just "failed" (BAC466.34).</summary>
    public static (string Text, bool Ok) ForAuto(BzAutoOutcome outcome) => outcome switch
    {
        BzAutoOutcome.Done => (AutoEnabled, true),
        BzAutoOutcome.TimedOut => (AutoTimedOut, false),
        _ => (AutoFailed, false),
    };
    public const string InvalidWindow = "invalid window";
    public const string WindowNotActive = "window not active";
}

/// <summary>Texts of the time-window dialog.</summary>
public static class ScheduleTexts
{
    public const string Title = "Time window";

    /// <summary>Window proposed when none is in place.</summary>
    public const string DefaultSpec = "08:00-22:00=3";

    public const string Prompt =
        "Limit during a time window, none the rest of the time.\r\n" +
        "Format: START-END=Mbps, in local time.\r\n\r\n" +
        "08:00-22:00=3 : 3 Mbps from 8 am to 10 pm\r\n" +
        "22:00-06:00=5 : 5 Mbps at night (crosses midnight)\r\n\r\n" +
        "A SYSTEM scheduled task does the switching: a single UAC prompt.";
}

/// <summary>Texts and arguments of the "switch Backblaze to automatic" action.</summary>
public static class BzAutoCommands
{
    public const string MissingText = "bzcli.exe was not found.";
    public const string MissingTitle = "Backblaze";
    public const string ConfirmTitle = "Confirmation";

    public const string ConfirmText =
        "Switch Backblaze to automatic mode?\r\n\r\n" +
        "Backblaze will choose its own number of threads and its own limit.\r\n" +
        "The limit in this menu will remain the cap enforced by Windows.";

    /// <summary>Content of the temporary configuration file given to bzcli.</summary>
    public const string Json = "{ \"settings\": { \"net_auto_throttle\": true } }";

    /// <summary>Arguments of <c>bzcli configure</c> for a configuration file.</summary>
    public static string GetArguments(string jsonPath) => "configure -j \"" + jsonPath + "\"";
}

/// <summary>
/// The Startup folder flag kept by Task Manager: a disabled shortcut stays in the folder, and its
/// StartupApproved value starts with an odd byte (03 = disabled, 02 = enabled).
/// </summary>
public static class StartupApproved
{
    /// <summary>Whether the registry value marks the shortcut as disabled.</summary>
    public static bool IsDisabled(object? registryValue) =>
        registryValue is byte[] { Length: > 0 } b && (b[0] & 1) != 0;

    /// <summary>
    /// Launch at sign-in is on when the shortcut exists and is not disabled.
    /// </summary>
    public static bool IsEnabled(bool shortcutExists, object? registryValue) =>
        shortcutExists && !IsDisabled(registryValue);

    /// <summary>
    /// The running process is the .NET host (<c>dotnet BackblazeMonitor.dll</c>), not the program's own exe: a
    /// shortcut to it would start the host with no arguments and nothing else.
    /// </summary>
    public static bool IsDotnetHost(string? processPath) =>
        !string.IsNullOrEmpty(processPath) &&
        string.Equals(Path.GetFileNameWithoutExtension(processPath.Replace('\\', '/')), "dotnet", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reading of the SYSTEM agent's run: when did the request we started finish, and how.</summary>
public static class AgentRun
{
    /// <summary><c>LastTaskResult</c> while the task is running (SCHED_S_TASK_RUNNING).</summary>
    public const int RunningResult = 267009;

    /// <summary>
    /// Our run is over once the last run time moved off the one seen before the start (different, not
    /// greater: a replayed hour still works) and the result is no longer "running".
    /// </summary>
    public static bool IsFinished(DateTime before, DateTime lastRun, int lastResult) =>
        lastRun != before && lastResult != RunningResult;

    /// <summary>Exit code 0 is success; 2 (request refused) and 1 (failure) are not.</summary>
    public static bool Succeeded(int lastResult) => lastResult == 0;

    /// <summary>
    /// Milliseconds to wait before starting the task again: LastRunTime has one-second resolution, a start
    /// within the same second as the previous one would be indistinguishable from it. 0 when no wait is
    /// needed.
    /// </summary>
    public static double GetStartGapMs(DateTime before, DateTime now)
    {
        var gap = (before.AddSeconds(1.1) - now).TotalMilliseconds;
        return gap > 0 && gap <= 1100 ? gap : 0;
    }
}

/// <summary>Tone of a message label.</summary>
public enum MessageTone
{
    Neutral,
    Good,
    Bad,
}

/// <summary>
/// The message next to the limit menu: a result that stays about 12 s (4 ticks), then the next switch of the
/// time window, if there is one.
/// </summary>
public sealed class LimitMessageState
{
    /// <summary>Ticks a result message stays (about 12 s at the tick rate).</summary>
    public const int MessageTicks = 4;

    private int _ticksLeft;

    /// <summary>Text shown.</summary>
    public string Text { get; private set; } = "";

    /// <summary>Color tone of the text.</summary>
    public MessageTone Tone { get; private set; } = MessageTone.Neutral;

    /// <summary>Shows a result.</summary>
    public void Set(string text, bool ok)
    {
        Tone = ok ? MessageTone.Good : MessageTone.Bad;
        Text = text;
        _ticksLeft = MessageTicks;
    }

    /// <summary>
    /// One tick: ages the message, and once it is gone shows the next window switch (or nothing).
    /// Returns whether the text or tone changed.
    /// </summary>
    /// <param name="schedule">The window in place, <c>null</c> = none.</param>
    /// <param name="now">Current time.</param>
    /// <param name="qosBits">Policy rate read last, -1 = not read yet: a window that does not follow is flagged.</param>
    public bool Tick(Schedule? schedule, DateTime now, double qosBits = -1)
    {
        if (_ticksLeft > 0) _ticksLeft--;
        if (_ticksLeft > 0) return false;
        var next = "";
        var tone = MessageTone.Neutral;
        if (schedule is not null)
        {
            next = schedule.GetNext(now);
            // The task may have been deleted by hand: the effect no longer follows
            if (!schedule.IsApplied(now, qosBits))
            {
                next = LimitMessages.WindowNotActive;
                tone = MessageTone.Bad;
            }
        }

        if (Text == next && Tone == tone) return false;
        Tone = tone;
        Text = next;
        return true;
    }
}

/// <summary>
/// Pace of the QoS policy read: the first read costs 180 to 370 ms in PowerShell, so it runs once every
/// 20 ticks (a minute), and sooner after an action.
/// </summary>
public sealed class QosReadPacer
{
    /// <summary>Ticks between two reads.</summary>
    public const int Period = 20;

    private int _ticks;

    /// <summary>Counts one tick; true when a read is due (and re-arms the countdown).</summary>
    public bool Due()
    {
        _ticks--;
        if (_ticks > 0) return false;
        _ticks = Period;
        return true;
    }

    /// <summary>Makes the next tick read the real state.</summary>
    public void RequestSoon() => _ticks = 1;
}

/// <summary>Parsing of the QoS read output.</summary>
public static class QosReadResult
{
    /// <summary>
    /// PowerShell script that prints the current throttle (bits/s) of the policy, nothing if absent. Only
    /// "no such policy" (ObjectNotFound) is an empty answer; any other failure (access denied, module or CIM
    /// error) exits with 1, so it is read as unknown and not as "no limit".
    /// </summary>
    public static string GetScript() =>
        $"try {{ $p = Get-NetQosPolicy -Name '{BzConstants.QosName}' -ErrorAction Stop; " +
        "if ($p) { [Console]::Out.Write(([double]$p.ThrottleRateAction).ToString('R', [Globalization.CultureInfo]::InvariantCulture)) } " +
        "exit 0 } catch { if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { exit 0 } exit 1 }";

    /// <summary>Bits per second from the script output: empty means no policy (0); garbage is unknown (<c>null</c>).</summary>
    public static double? Parse(string? output)
    {
        var t = (output ?? "").Trim();
        if (t.Length == 0) return 0;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v >= 0 ? v : null;
    }
}
