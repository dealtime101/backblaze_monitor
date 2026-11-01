using System.Globalization;
using BackblazeMonitor.Core;
using Xunit;

namespace BackblazeMonitor.Core.Tests;

public class ScheduleAndCommandTests
{
    private static readonly Schedule Day = Schedule.Parse("08:00-22:00=3")!;
    private static readonly Schedule Night = Schedule.Parse("22:00-06:00=5")!; // past midnight

    // ---------- Input and next switch ----------
    [Fact]
    public void Window_read() =>
        Assert.Equal("480 1320 3 08:00-22:00=3", $"{Day.Start} {Day.End} {Day.Mbps} {Day.Text}");

    [Fact]
    public void Window_normalised() => Assert.Equal("08:05-22:00=3", Schedule.Parse(" 8:05 - 22:00 = 3 ")!.Text);

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("24:00-06:00=3")]
    [InlineData("08:60-22:00=3")]
    [InlineData("08:00-08:00=3")]
    [InlineData("08:00-22:00=0")]
    [InlineData("08:00-22:00=100")]
    [InlineData("08:00-22:00=3;x")]
    public void Window_rejected(string bad) => Assert.Null(Schedule.Parse(bad));

    [Fact]
    public void Window_null_is_rejected() => Assert.Null(Schedule.Parse(null));

    [Theory]
    [InlineData("day", "12:00", "22:00 -> none")]
    [InlineData("day", "07:59", "08:00 -> 3 Mbps")]
    [InlineData("day", "08:00", "22:00 -> none")]
    [InlineData("day", "22:00", "08:00 -> 3 Mbps")]
    [InlineData("day", "23:30", "08:00 -> 3 Mbps")]
    [InlineData("night", "23:00", "06:00 -> none")]
    [InlineData("night", "03:00", "06:00 -> none")]
    [InlineData("night", "12:00", "22:00 -> 5 Mbps")]
    public void Next_switch(string which, string at, string want)
    {
        var s = which == "day" ? Day : Night;
        var now = DateTime.Parse("2026-09-25 " + at, CultureInfo.InvariantCulture);
        Assert.Equal(want, s.GetNext(now));
    }

    [Fact]
    public void Widest_next_switch_text()
    {
        var now = new DateTime(2026, 9, 25, 12, 0, 0);
        Assert.Equal("23:59 -> none", Schedule.Parse("00:00-23:59=99")!.GetNext(now));
        Assert.Equal("23:59 -> 99 Mbps", Schedule.Parse("23:59-00:00=99")!.GetNext(now));
    }

    [Theory]
    [InlineData(479, false)]
    [InlineData(480, true)]
    [InlineData(1319, true)]
    [InlineData(1320, false)]
    public void Window_contains_is_start_inclusive_end_exclusive(int minute, bool inside) => Assert.Equal(inside, Day.Contains(minute));

    // ---------- Command text (built here, run elsewhere: compared literally) ----------
    private const string Q = "BackblazeMonitorThrottle";

    private const string ExpectedBody =
        "$old = Get-NetQosPolicy -Name '" + Q + "' -ErrorAction SilentlyContinue; try { " +
        "if ($old) { if ($bits -gt 0) { Set-NetQosPolicy -Name '" + Q + "' -ThrottleRateActionBitsPerSecond $bits -ErrorAction Stop } " +
        "else { Remove-NetQosPolicy -Name '" + Q + "' -Confirm:$false -ErrorAction Stop } } " +
        "elseif ($bits -gt 0) { New-NetQosPolicy -Name '" + Q + "' -AppPathNameMatchCondition 'bztransmit.exe' -NetworkProfile All -ThrottleRateActionBitsPerSecond $bits -ErrorAction Stop | Out-Null } } " +
        "catch { if ($old -and -not (Get-NetQosPolicy -Name '" + Q + "' -ErrorAction SilentlyContinue)) { " +
        "New-NetQosPolicy -Name '" + Q + "' -AppPathNameMatchCondition 'bztransmit.exe' -NetworkProfile All -ThrottleRateActionBitsPerSecond $old.ThrottleRateAction -ErrorAction SilentlyContinue | Out-Null }; throw }; " +
        "Unregister-ScheduledTask -TaskName 'BackblazeMonitorSchedule' -Confirm:$false -ErrorAction SilentlyContinue; ";

    [Fact]
    public void Qos_body_is_the_exact_text() => Assert.Equal(ExpectedBody, QosCommands.GetBody());

    [Theory]
    [InlineData(0L)]
    [InlineData(5000000L)]
    [InlineData(50000000L)]
    public void Qos_command_wraps_the_body(long bits) =>
        Assert.Equal($"try {{ $bits = {bits}; " + ExpectedBody + "exit 0 } catch { exit 1 }", QosCommands.GetCommand(bits));

    [Fact]
    public void Service_commands()
    {
        Assert.Equal("Start-Service -Name bzserv -ErrorAction Stop", ServiceCommands.GetCommand(ServiceAction.Start));
        Assert.Equal("Stop-Service -Name bzserv -Force -ErrorAction Stop", ServiceCommands.GetCommand(ServiceAction.Stop));
        Assert.Equal("Restart-Service -Name bzserv -Force -ErrorAction Stop", ServiceCommands.GetCommand(ServiceAction.Restart));
        Assert.Equal("try { Stop-Service -Name bzserv -Force -ErrorAction Stop; exit 0 } catch { exit 1 }", ServiceCommands.GetElevatedScript(ServiceAction.Stop));
        Assert.Equal(new[] { "start", "stop", "restart" }, new[] { ServiceAction.Start, ServiceAction.Stop, ServiceAction.Restart }.Select(ServiceCommands.RequestName));
        Assert.Equal(new[] { "Start", "Stop", "Restart" }, new[] { ServiceAction.Start, ServiceAction.Stop, ServiceAction.Restart }.Select(ServiceCommands.Verb));
    }

    [Fact]
    public void Schedule_command_decides_by_the_time_of_the_task_not_the_trigger()
    {
        var c = ScheduleCommands.GetCommand(Day);
        Assert.Contains("$d = (Get-Date).AddSeconds(30)\r\n", c);
        Assert.Contains("if (Test-InSchedule @{ Start = 480; End = 1320 } ($d.Hour * 60 + $d.Minute)) {\r\n", c);
        Assert.Contains("Set-NetQosPolicy -Name 'BackblazeMonitorThrottle' -ThrottleRateActionBitsPerSecond 3000000 -ErrorAction Stop }\r\n", c);
        Assert.Contains("New-NetQosPolicy -Name 'BackblazeMonitorThrottle' -AppPathNameMatchCondition 'bztransmit.exe' -NetworkProfile All -ThrottleRateActionBitsPerSecond 3000000 -ErrorAction Stop | Out-Null } }\r\n", c);
        Assert.EndsWith("else { Remove-NetQosPolicy -Name 'BackblazeMonitorThrottle' -Confirm:$false -ErrorAction SilentlyContinue }", c);
        Assert.StartsWith("function Test-InSchedule { param($s, [int]$m) ", c);
        Assert.Contains("@{ Start = 1320; End = 360 }", ScheduleCommands.GetCommand(Night));
        Assert.Contains("5000000", ScheduleCommands.GetCommand(Night));
    }

    [Fact]
    public void Schedule_setup_triggers_in_local_time_task_as_SYSTEM_then_the_wanted_state()
    {
        var setup = ScheduleCommands.GetSetup(Day, new DateTime(2026, 9, 25, 15, 42, 0));
        Assert.StartsWith("try {\r\n$t = '2026-09-25T08:00:00', '2026-09-25T22:00:00' | ForEach-Object { $x = New-ScheduledTaskTrigger -Daily -At $_; $x.StartBoundary = $_; $x }\r\n", setup);
        Assert.Contains("-User 'SYSTEM' -RunLevel Highest -Force -ErrorAction Stop | Out-Null\r\n", setup);
        Assert.Contains("New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 5)", setup);
        Assert.Contains("Register-ScheduledTask -TaskName 'BackblazeMonitorSchedule' -Trigger $t -Action $a -Settings $o ", setup);
        Assert.Contains(@"-Execute 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'", setup);
        Assert.EndsWith("\r\nexit 0 } catch { exit 1 }", setup);
        // the task receives the tested command, and the wanted state follows right away
        var enc = System.Text.RegularExpressions.Regex.Match(setup, @"-EncodedCommand (\S+)'").Groups[1].Value;
        var cmd = ScheduleCommands.GetCommand(Day);
        Assert.Equal(cmd, EncodedCommand.Decode(enc));
        Assert.Contains("\r\n" + cmd + "\r\nexit 0 }", setup);
        Assert.Contains(@"-Execute 'D:\PS\powershell.exe'", ScheduleCommands.GetSetup(Day, DateTime.Today, @"D:\PS"));
    }

    // ---------- SYSTEM agent ----------
    private static readonly long[] Bits = { 0, 5000000, 50000000 };

    [Fact]
    public void Agent_command_reads_64_bytes_and_compares_against_a_closed_list()
    {
        var c = AgentCommands.GetCommand(@"C:\x\requete.txt", Bits);
        var want =
            "try {\r\n" +
            @"$s = [IO.File]::OpenRead('C:\x\requete.txt'); $b = New-Object byte[] 64; $n = $s.Read($b, 0, 64); $s.Dispose()" + "\r\n" +
            "$r = [Text.Encoding]::ASCII.GetString($b, 0, $n).Trim()\r\n" +
            "if ($r -ceq 'start') { Start-Service -Name bzserv -ErrorAction Stop; exit 0 }\r\n" +
            "if ($r -ceq 'stop') { Stop-Service -Name bzserv -Force -ErrorAction Stop; exit 0 }\r\n" +
            "if ($r -ceq 'restart') { Restart-Service -Name bzserv -Force -ErrorAction Stop; exit 0 }\r\n" +
            "if ($r -cin '0', '5000000', '50000000') { $bits = [int64]$r; " + ExpectedBody + "exit 0 }\r\n" +
            "exit 2 } catch { exit 1 }";
        Assert.Equal(want, c);
    }

    [Fact]
    public void Agent_command_escapes_straight_and_typographic_apostrophes_in_the_path()
    {
        var c = AgentCommands.GetCommand("C:\\bbm'\u2019essai\\requete.txt", Bits);
        Assert.Contains("OpenRead('C:\\bbm''\u2019\u2019essai\\requete.txt')", c);
        Assert.Equal("a''b\u2018\u2018c\u201a\u201a\u201b\u201b", AgentCommands.EscapeSingleQuoted("a'b\u2018c\u201a\u201b"));
    }

    [Fact]
    public void Agent_menu_steps_come_from_the_menu_not_the_window()
    {
        var bits = QosChoices.AgentBits(QosChoices.All).ToArray();
        Assert.Equal(15, bits.Length);
        Assert.Equal(0, bits[0]);
        Assert.Equal(50_000_000, bits[^1]);
        Assert.DoesNotContain(-1L, bits);
        Assert.Equal(new[] { 0L, 5000000, 50000000 }, QosChoices.AgentBits(new[] { new QosChoice("a", 0), new QosChoice("b", 5000000), new QosChoice("c", 50000000), new QosChoice("w", -1) }));
    }

    [Fact]
    public void Agent_arguments_encode_the_command_and_identify_the_version()
    {
        var args = AgentCommands.GetArguments(@"C:\x\requete.txt", Bits);
        Assert.StartsWith("-NoProfile -NonInteractive -EncodedCommand ", args);
        var enc = args.Substring("-NoProfile -NonInteractive -EncodedCommand ".Length);
        Assert.Equal(AgentCommands.GetCommand(@"C:\x\requete.txt", Bits), EncodedCommand.Decode(enc));
        Assert.True(AgentCommands.IsCurrent(args, @"C:\x\requete.txt", Bits));
        Assert.False(AgentCommands.IsCurrent(args, @"C:\moved\requete.txt", Bits));
        Assert.False(AgentCommands.IsCurrent(null, @"C:\x\requete.txt", Bits));
        Assert.False(AgentCommands.IsCurrent(args.ToUpperInvariant(), @"C:\x\requete.txt", Bits));
    }

    [Fact]
    public void Agent_setup_queues_requests_and_grants_read_and_run_to_the_user_only()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var setup = AgentCommands.GetSetup(@"C:\x\requete.txt", Bits, sid);
        // the request folder: protected ACL, SYSTEM and administrators in full, the tile's user to read, write and delete, nobody else
        Assert.StartsWith("try {\r\nNew-Item -ItemType Directory -Force -Path 'C:\\x' | Out-Null\r\n", setup);
        Assert.Contains("SetSecurityDescriptorSddlForm('D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301df;;;" + sid + ")')\r\n", setup);
        Assert.Contains("Set-Acl -LiteralPath 'C:\\x' -AclObject $ac\r\n$a = New-ScheduledTaskAction", setup);
        foreach (var wide in new[] { ";;;WD)", ";;;BU)", ";;;AU)" }) Assert.DoesNotContain(wide, setup); // no Everyone, Users, Authenticated Users
        Assert.Contains("$a = New-ScheduledTaskAction -Execute 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe' -Argument '-NoProfile -NonInteractive -EncodedCommand ", setup);
        Assert.Contains("'\r\n$o = New-ScheduledTaskSettingsSet -MultipleInstances Queue -ExecutionTimeLimit (New-TimeSpan -Minutes 5)\r\n", setup);
        Assert.Contains("Register-ScheduledTask -TaskName 'BackblazeMonitorAgent' -Action $a -Settings $o -User 'SYSTEM' -RunLevel Highest -Force -ErrorAction Stop | Out-Null\r\n", setup);
        Assert.Contains("$c.GetFolder('\\').GetTask('BackblazeMonitorAgent').SetSecurityDescriptor('D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;" + sid + ")', 0)\r\n", setup);
        Assert.EndsWith("} catch { }\r\n", setup);
        // the task receives the tested command
        var enc = System.Text.RegularExpressions.Regex.Match(setup, @"-EncodedCommand (\S+)'").Groups[1].Value;
        Assert.Equal(AgentCommands.GetCommand(@"C:\x\requete.txt", Bits), EncodedCommand.Decode(enc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("S-1-5-21');Remove-Item C:\\ -Recurse;('")]
    public void Agent_setup_gives_the_folder_to_nobody_when_the_sid_is_missing_or_odd(string sid)
    {
        var setup = AgentCommands.GetSetup(@"C:\x\requete.txt", Bits, sid);
        Assert.Contains("SetSecurityDescriptorSddlForm('D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')\r\n", setup);
    }

    // ---------- Limit set elsewhere ----------
    [Theory]
    [InlineData(2500000.0, "2.5 Mbps")]
    [InlineData(400000.0, "400 kbps")]
    [InlineData(8.0, "0.008 kbps")]
    [InlineData(12345672.0, "12.346 Mbps")]
    [InlineData(7000000.0, "7 Mbps")]
    public void Limit_set_elsewhere_is_shown_as_is(double bits, string want) => Assert.Equal(want, QosChoices.LabelFor(bits));

    [Fact]
    public void Limit_tier_is_chosen_without_duplicate()
    {
        var choices = new[] { new QosChoice("Aucune", 0), new QosChoice("3 Mbps", 3000000), new QosChoice("Plage horaire...", -1) };
        Assert.Equal(1, QosChoices.IndexOf(choices, 3000000));
        Assert.Equal(-1, QosChoices.IndexOf(choices, 2500000));
        Assert.Equal(0, QosChoices.IndexOf(choices, 0));
    }

    [Fact]
    public void Menu_entries()
    {
        var all = QosChoices.All;
        Assert.Equal(16, all.Count);
        Assert.Equal("None", all[0].Text);
        Assert.Equal("1 Mbps", all[1].Text);
        Assert.Equal(1_000_000, all[1].Bits);
        Assert.Equal("10 Mbps", all[6].Text);
        Assert.Equal("50 Mbps", all[14].Text);
        Assert.Equal("Time window...", all[15].Text);
        Assert.Equal(-1, all[15].Bits);
        Assert.Equal(new[] { "30 min", "24 h", "7 d" }, Periods.All.Select(p => p.Text));
        Assert.Equal(604800, Periods.LongestSec);
    }
}
