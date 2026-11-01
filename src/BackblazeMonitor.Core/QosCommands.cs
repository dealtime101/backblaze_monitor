using System.Globalization;

namespace BackblazeMonitor.Core;

/// <summary>
/// Builders of the PowerShell text run elevated (QoS policy, service, SYSTEM agent). Pure string
/// builders: nothing is executed here.
/// </summary>
public static class QosCommands
{
    /// <summary>
    /// Fixed step <c>$bits</c> (0 = none), then removal of the time-window task, otherwise it would change
    /// it at its next switch. A refusal from Windows is reported and leaves the limit and window as they
    /// were: Set on the existing policy (no gap), and the old one restored if Windows removed it anyway
    /// (measured: a refused Set destroys it, 0x80041002). Expects a <c>$bits</c> variable in scope.
    /// </summary>
    public static string GetBody()
    {
        var q = BzConstants.QosName;
        var neu = $"New-NetQosPolicy -Name '{q}' -AppPathNameMatchCondition '{BzConstants.QosAppName}' -NetworkProfile All -ThrottleRateActionBitsPerSecond";
        return $"$old = Get-NetQosPolicy -Name '{q}' -ErrorAction SilentlyContinue; try {{ " +
               $"if ($old) {{ if ($bits -gt 0) {{ Set-NetQosPolicy -Name '{q}' -ThrottleRateActionBitsPerSecond $bits -ErrorAction Stop }} " +
               $"else {{ Remove-NetQosPolicy -Name '{q}' -Confirm:$false -ErrorAction Stop }} }} " +
               $"elseif ($bits -gt 0) {{ {neu} $bits -ErrorAction Stop | Out-Null }} }} " +
               $"catch {{ if ($old -and -not (Get-NetQosPolicy -Name '{q}' -ErrorAction SilentlyContinue)) {{ " +
               $"{neu} $old.ThrottleRateAction -ErrorAction SilentlyContinue | Out-Null }}; throw }}; " +
               $"Unregister-ScheduledTask -TaskName '{BzConstants.ScheduleTask}' -Confirm:$false -ErrorAction SilentlyContinue; ";
    }

    /// <summary>Complete elevated command for a fixed step (0 = no limit). Exit code 0 done, 1 failure.</summary>
    public static string GetCommand(long bits) =>
        $"try {{ $bits = {bits.ToString(CultureInfo.InvariantCulture)}; " + GetBody() + "exit 0 } catch { exit 1 }";
}

/// <summary>Service actions offered by the tile and the tray menu.</summary>
public enum ServiceAction
{
    Start,
    Stop,
    Restart,
}

/// <summary>Service command text, requests and confirmation texts.</summary>
public static class ServiceCommands
{
    /// <summary>Lower-case name of the action, also the request text sent to the SYSTEM agent.</summary>
    public static string RequestName(ServiceAction action) => action switch
    {
        ServiceAction.Start => "start",
        ServiceAction.Stop => "stop",
        ServiceAction.Restart => "restart",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Capitalised verb for notification titles ("Start", "Stop", "Restart").</summary>
    public static string Verb(ServiceAction action) => action switch
    {
        ServiceAction.Start => "Start",
        ServiceAction.Stop => "Stop",
        ServiceAction.Restart => "Restart",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>The PowerShell command of the action.</summary>
    public static string GetCommand(ServiceAction action) => action switch
    {
        ServiceAction.Start => $"Start-Service -Name {BzConstants.ServiceName} -ErrorAction Stop",
        ServiceAction.Stop => $"Stop-Service -Name {BzConstants.ServiceName} -Force -ErrorAction Stop",
        ServiceAction.Restart => $"Restart-Service -Name {BzConstants.ServiceName} -Force -ErrorAction Stop",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Complete elevated script for the action (used when the agent is not installed yet).</summary>
    public static string GetElevatedScript(ServiceAction action) =>
        $"try {{ {GetCommand(action)}; exit 0 }} catch {{ exit 1 }}";

    /// <summary>
    /// Confirmation text, or <c>null</c> when the action goes without a box. Start and Stop ask; Restart is
    /// bare by decision: offered only while running, and any bztransmit in progress finishes its block when
    /// bzserv stops.
    /// </summary>
    public static string? GetConfirmation(ServiceAction action) => action switch
    {
        ServiceAction.Start => "Start the Backblaze service?",
        ServiceAction.Stop => "Stop the Backblaze service? Backups will be suspended.",
        ServiceAction.Restart => null,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}

/// <summary>
/// The SYSTEM agent: an on-demand scheduled task that runs, without a UAC prompt, the requests the tile
/// writes to a file. The request is only DATA (64 bytes at most), compared against a closed list, never
/// executed. Exit code: 0 done, 2 request refused, 1 failure.
/// </summary>
public static class AgentCommands
{
    /// <summary>
    /// Doubles every single-quote character, like PowerShell's EscapeSingleQuotedStringContent: the
    /// straight quote and the typographic ones.
    /// </summary>
    public static string EscapeSingleQuoted(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            sb.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>The command run by the agent task.</summary>
    /// <param name="agentFile">Path of the request file (requete.txt).</param>
    /// <param name="allowedBits">Bit rates accepted as a request: the menu steps.</param>
    public static string GetCommand(string agentFile, IEnumerable<long> allowedBits)
    {
        var f = EscapeSingleQuoted(agentFile);
        var bits = string.Join(", ", allowedBits.Select(b => "'" + b.ToString(CultureInfo.InvariantCulture) + "'"));
        var cmd = "try {\r\n" +
                  $"$s = [IO.File]::OpenRead('{f}'); $b = New-Object byte[] 64; $n = $s.Read($b, 0, 64); $s.Dispose()\r\n" +
                  "$r = [Text.Encoding]::ASCII.GetString($b, 0, $n).Trim()\r\n";
        foreach (var a in new[] { ServiceAction.Start, ServiceAction.Stop, ServiceAction.Restart })
        {
            cmd += $"if ($r -ceq '{ServiceCommands.RequestName(a)}') {{ {ServiceCommands.GetCommand(a)}; exit 0 }}\r\n";
        }

        return cmd + $"if ($r -cin {bits}) {{ $bits = [int64]$r; {QosCommands.GetBody()}exit 0 }}\r\n" +
               "exit 2 } catch { exit 1 }";
    }

    /// <summary>Arguments of the agent task's action: the command, encoded.</summary>
    public static string GetArguments(string agentFile, IEnumerable<long> allowedBits) =>
        "-NoProfile -NonInteractive -EncodedCommand " + EncodedCommand.Encode(GetCommand(agentFile, allowedBits));

    /// <summary>
    /// Installation, run inside the elevation of an action: a queue (a run started while another is still going
    /// waits for it; the request file holds ONE request, so the tile sends one at a time and waits for the
    /// result before writing the next, see PrivilegedRunner.RunAgent) and a descriptor that lets the tile's user read and start the task, but not modify
    /// it. A failed installation does not prevent the action that follows.
    /// </summary>
    /// <param name="agentFile">Path of the request file.</param>
    /// <param name="allowedBits">Bit rates accepted as a request.</param>
    /// <param name="sid">SID of the tile's user (read by the tile: the elevation may run under another account).</param>
    /// <param name="psHome">PowerShell home folder (PowerShell's $PSHOME).</param>
    public static string GetSetup(string agentFile, IEnumerable<long> allowedBits, string sid, string psHome = BzPaths.DefaultPsHome)
    {
        var t = BzConstants.AgentTask;
        return "try {\r\n" +
               GetFolderSetup(agentFile, sid) +
               $"$a = New-ScheduledTaskAction -Execute '{psHome}\\powershell.exe' -Argument '{GetArguments(agentFile, allowedBits)}'\r\n" +
               "$o = New-ScheduledTaskSettingsSet -MultipleInstances Queue -ExecutionTimeLimit (New-TimeSpan -Minutes 5)\r\n" +
               $"Register-ScheduledTask -TaskName '{t}' -Action $a -Settings $o -User 'SYSTEM' " +
               "-RunLevel Highest -Force -ErrorAction Stop | Out-Null\r\n" +
               "$c = New-Object -ComObject Schedule.Service; $c.Connect()\r\n" +
               $"$c.GetFolder('\\').GetTask('{t}').SetSecurityDescriptor('D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{sid})', 0)\r\n" +
               "} catch { }\r\n";
    }

    /// <summary>
    /// The folder of the request file, closed to everybody but SYSTEM, the administrators and the tile's user:
    /// the SYSTEM agent runs what that file asks for, so no other account may write in it. The ACL is
    /// protected (no inheritance from ProgramData, where every user can create files). Without a SID the
    /// user gets no right: the tile then falls back to the UAC prompt rather than leave the folder open.
    /// </summary>
    private static string GetFolderSetup(string agentFile, string sid)
    {
        var cut = agentFile.LastIndexOf('\\');
        if (cut <= 0) return "";
        var dir = EscapeSingleQuoted(agentFile.Substring(0, cut));
        var user = sid.Length > 0 && sid.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? $"(A;OICI;0x1301df;;;{sid})" : "";
        return $"New-Item -ItemType Directory -Force -Path '{dir}' | Out-Null\r\n" +
               "$ac = New-Object System.Security.AccessControl.DirectorySecurity\r\n" +
               $"$ac.SetSecurityDescriptorSddlForm('D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA){user}')\r\n" +
               $"Set-Acl -LiteralPath '{dir}' -AclObject $ac\r\n";
    }

    /// <summary>
    /// Whether the installed agent task's arguments are those of this version: otherwise (script updated,
    /// folder moved) it must be reinstalled. Case-sensitive, like the PowerShell <c>-cne</c>.
    /// </summary>
    public static bool IsCurrent(string? installedArguments, string agentFile, IEnumerable<long> allowedBits) =>
        string.Equals(installedArguments, GetArguments(agentFile, allowedBits), StringComparison.Ordinal);
}
