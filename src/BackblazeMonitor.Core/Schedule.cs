using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BackblazeMonitor.Core;

/// <summary>
/// A limit time window such as "08:00-22:00=3": 3 Mbps from 8 am to 10 pm, no limit the rest of the time.
/// An end earlier than the start wraps past midnight.
/// </summary>
/// <param name="Start">Start, in minutes of the day.</param>
/// <param name="End">End, in minutes of the day.</param>
/// <param name="Mbps">Limit during the window, in Mbps (1 to 99).</param>
/// <param name="Text">Normalised text ("08:05-22:00=3"), the form saved in settings.txt.</param>
public sealed record Schedule(int Start, int End, int Mbps, string Text)
{
    private static readonly Regex Spec = new(
        @"^\s*([0-9]{1,2}):([0-9]{2})\s*-\s*([0-9]{1,2}):([0-9]{2})\s*=\s*([0-9]{1,2})\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>Parses a window specification. Invalid yields <c>null</c>.</summary>
    public static Schedule? Parse(string? spec)
    {
        var m = Spec.Match(spec ?? "");
        if (!m.Success) return null;
        var g = new int[5];
        for (var i = 0; i < 5; i++) g[i] = int.Parse(m.Groups[i + 1].Value, CultureInfo.InvariantCulture);
        int h1 = g[0], m1 = g[1], h2 = g[2], m2 = g[3], n = g[4];
        if (h1 > 23 || h2 > 23 || m1 > 59 || m2 > 59 || n < 1) return null;
        var start = h1 * 60 + m1;
        var end = h2 * 60 + m2;
        if (start == end) return null;
        var text = string.Create(CultureInfo.InvariantCulture, $"{h1:00}:{m1:00}-{h2:00}:{m2:00}={n}");
        return new Schedule(start, end, n, text);
    }

    /// <summary>True if minute-of-day <paramref name="minute"/> falls within the window.</summary>
    public bool Contains(int minute)
    {
        if (Start < End) return minute >= Start && minute < End;
        return minute >= Start || minute < End;
    }

    /// <summary>Next switch, shown next to the menu: "22:00 -&gt; none".</summary>
    public string GetNext(DateTime now)
    {
        int at;
        string to;
        if (Contains(now.Hour * 60 + now.Minute))
        {
            at = End;
            to = "none";
        }
        else
        {
            at = Start;
            to = string.Create(CultureInfo.InvariantCulture, $"{Mbps} Mbps");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{at / 60:00}:{at % 60:00} -> {to}");
    }

    /// <summary>
    /// Whether the policy in place (<paramref name="bits"/>, -1 = not read yet) is what the window wants now.
    /// Near a switch (5 min either side) the task may not have run yet: not a fault. False when the window
    /// is saved but its effect does not follow, e.g. the SYSTEM task was deleted by hand (BAC466.46).
    /// </summary>
    public bool IsApplied(DateTime now, double bits)
    {
        if (bits < 0) return true;
        var m = now.Hour * 60 + now.Minute;
        foreach (var b in new[] { Start, End })
        {
            if (((m - b + 1440) % 1440) < 5) return true;
        }

        var want = Contains(m) ? (double)Bits : 0;
        return Math.Abs(bits - want) < 8;
    }

    /// <summary>Bits per second applied during the window.</summary>
    public long Bits => Mbps * 1_000_000L;
}

/// <summary>Command text for the SYSTEM scheduled task that maintains a time window.</summary>
public static class ScheduleCommands
{
    /// <summary>
    /// What the task runs on each trigger: the state wanted NOW, not the trigger's own, so a late wake-up
    /// (StartWhenAvailable) or two missed switches still land on the right state. +30 s: a trigger that
    /// fires a fraction of a second early must not keep the old state until the next one. Set if the
    /// policy exists: no unlimited gap between a removal and a creation.
    /// </summary>
    public static string GetCommand(Schedule s)
    {
        var bits = s.Bits.ToString(CultureInfo.InvariantCulture);
        var q = BzConstants.QosName;
        return "function Test-InSchedule { param($s, [int]$m) " +
               "if ($s.Start -lt $s.End) { return ($m -ge $s.Start -and $m -lt $s.End) } " +
               "return ($m -ge $s.Start -or $m -lt $s.End) }\r\n" +
               "$d = (Get-Date).AddSeconds(30)\r\n" +
               $"if (Test-InSchedule @{{ Start = {s.Start}; End = {s.End} }} ($d.Hour * 60 + $d.Minute)) {{\r\n" +
               $"    if (Get-NetQosPolicy -Name '{q}' -ErrorAction SilentlyContinue) {{\r\n" +
               $"        Set-NetQosPolicy -Name '{q}' -ThrottleRateActionBitsPerSecond {bits} -ErrorAction Stop }}\r\n" +
               $"    else {{ New-NetQosPolicy -Name '{q}' -AppPathNameMatchCondition '{BzConstants.QosAppName}' " +
               $"-NetworkProfile All -ThrottleRateActionBitsPerSecond {bits} -ErrorAction Stop | Out-Null }} }}\r\n" +
               $"else {{ Remove-NetQosPolicy -Name '{q}' -Confirm:$false -ErrorAction SilentlyContinue }}";
    }

    /// <summary>
    /// Installed in a single elevation: the task (inline command, nothing to read from a folder the user
    /// can write to; powershell by its full path), then the wanted state right away. Triggers in LOCAL
    /// time: with the "Z" that New-ScheduledTaskTrigger writes, 22:00 would fire at 21:00 in standard time.
    /// </summary>
    /// <param name="s">The window.</param>
    /// <param name="today">Today's date (its time of day is ignored).</param>
    /// <param name="psHome">PowerShell home folder (PowerShell's $PSHOME).</param>
    public static string GetSetup(Schedule s, DateTime today, string psHome = BzPaths.DefaultPsHome)
    {
        var cmd = GetCommand(s);
        var enc = EncodedCommand.Encode(cmd);
        var day = today.Date;
        var at = string.Join(", ", new[] { s.Start, s.End }
            .Select(m => "'" + day.AddMinutes(m).ToString("s", CultureInfo.InvariantCulture) + "'"));
        return "try {\r\n" +
               $"$t = {at} | ForEach-Object {{ $x = New-ScheduledTaskTrigger -Daily -At $_; $x.StartBoundary = $_; $x }}\r\n" +
               $"$a = New-ScheduledTaskAction -Execute '{AgentCommands.EscapeSingleQuoted(psHome)}\\powershell.exe' -Argument '-NoProfile -NonInteractive -EncodedCommand {enc}'\r\n" +
               "$o = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 5)\r\n" +
               $"Register-ScheduledTask -TaskName '{BzConstants.ScheduleTask}' -Trigger $t -Action $a -Settings $o " +
               "-User 'SYSTEM' -RunLevel Highest -Force -ErrorAction Stop | Out-Null\r\n" +
               $"{cmd}\r\nexit 0 }} catch {{ exit 1 }}";
    }
}

/// <summary>Encoding of a command for <c>powershell -EncodedCommand</c> (immune to quoting problems).</summary>
public static class EncodedCommand
{
    /// <summary>Base64 of the UTF-16LE bytes of the command.</summary>
    public static string Encode(string command) => Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    /// <summary>Inverse of <see cref="Encode"/>.</summary>
    public static string Decode(string encoded) => Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
}
