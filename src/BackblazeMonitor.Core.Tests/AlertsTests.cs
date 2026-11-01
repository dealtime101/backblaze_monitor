using BackblazeMonitor.Core;
using Xunit;

namespace BackblazeMonitor.Core.Tests;

public class AlertsTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0);
    private static readonly DateTime Never = DateTime.MinValue;

    [Fact]
    public void Elapsed_counts_the_time_that_passed_across_a_clock_change()
    {
        var toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        // spring forward 2026-03-08 02:00 -> 03:00: 01:30 to 03:30 is one hour, not two
        Assert.Equal(60, Elapsed.Minutes(new DateTime(2026, 3, 8, 1, 30, 0), new DateTime(2026, 3, 8, 3, 30, 0), toronto));
        // fall back 2026-11-01 02:00 -> 01:00: 00:30 to 02:30 is three hours, not two
        Assert.Equal(180, Elapsed.Minutes(new DateTime(2026, 11, 1, 0, 30, 0), new DateTime(2026, 11, 1, 2, 30, 0), toronto));
        Assert.Equal(1, Elapsed.Minutes(new DateTime(2026, 3, 8, 1, 59, 0), new DateTime(2026, 3, 8, 3, 0, 0), toronto));
        Assert.True(Elapsed.Minutes(new DateTime(2026, 3, 8, 2, 30, 0), Now, toronto) > 0); // a time inside the skipped hour does not throw
        Assert.Equal(7, Elapsed.Minutes(new DateTime(2026, 9, 25, 12, 0, 0), new DateTime(2026, 9, 25, 12, 7, 0), toronto));
        Assert.True(Elapsed.Minutes(Never, Now, toronto) > 5); // "never asked"
    }

    // ---------- Stop alert ----------
    [Fact]
    public void Stop_not_requested() => Assert.NotNull(Alerts.GetStopAlert("Running", "Stopped", Never, Now));

    [Fact]
    public void Stop_after_StopPending() => Assert.NotNull(Alerts.GetStopAlert("StopPending", "Stopped", Never, Now));

    [Fact]
    public void Stop_requested_2_min_ago() => Assert.Null(Alerts.GetStopAlert("Running", "Stopped", Now.AddMinutes(-2), Now));

    [Fact]
    public void Stop_requested_10_min_ago() => Assert.NotNull(Alerts.GetStopAlert("Running", "Stopped", Now.AddMinutes(-10), Now));

    [Fact]
    public void Already_stopped_at_launch() => Assert.Null(Alerts.GetStopAlert("", "Stopped", Never, Now));

    [Fact]
    public void Stays_stopped() => Assert.Null(Alerts.GetStopAlert("Stopped", "Stopped", Never, Now));

    [Fact]
    public void Still_running_does_not_ring() => Assert.Null(Alerts.GetStopAlert("Running", "Running", Never, Now));

    [Fact]
    public void Stop_message_is_the_exact_text() =>
        Assert.Equal("The service stopped without going through the monitor: backups are suspended.", Alerts.GetStopAlert("Running", "Stopped", Never, Now));

    // ---------- Stall alert ----------
    [Fact]
    public void Stall_continuous() =>
        Assert.Equal("Nothing has been sent for 2 h 30 while 3,380 files are waiting.",
            Alerts.GetStallAlert(Now.AddMinutes(-150), 3380, "continuously", 120, Now));

    [Fact]
    public void Continuous_90_min_nothing() => Assert.Null(Alerts.GetStallAlert(Now.AddMinutes(-90), 3380, "continuously", 120, Now));

    [Fact]
    public void Nothing_waiting_nothing() => Assert.Null(Alerts.GetStallAlert(Now.AddHours(-5), 0, "continuously", 120, Now));

    [Fact]
    public void Remaining_unknown_nothing() => Assert.Null(Alerts.GetStallAlert(Now.AddHours(-5), null, "continuously", 120, Now));

    [Fact]
    public void StallAlertMin_0_disables() => Assert.Null(Alerts.GetStallAlert(Now.AddHours(-5), 5, "continuously", 0, Now));

    [Fact]
    public void Unknown_start_of_silence_nothing() => Assert.Null(Alerts.GetStallAlert(null, 5, "continuously", 120, Now));

    // A machine in once-a-day mode (4 h - 8 h): 1,393 min of normal silence measured, 09-24 05:34 -> 09-25 04:47
    [Fact]
    public void Once_a_day_1393_min_nothing() => Assert.Null(Alerts.GetStallAlert(Now.AddMinutes(-1393), 3380, "once_per_day", 120, Now));

    [Fact]
    public void Once_a_day_1590_min() => Assert.NotNull(Alerts.GetStallAlert(Now.AddMinutes(-1590), 3380, "once_per_day", 120, Now));

    [Fact]
    public void Unknown_schedule_never() => Assert.Null(Alerts.GetStallAlert(Now.AddDays(-9), 5, "", 120, Now));

    [Fact]
    public void Manual_schedule_never() => Assert.Null(Alerts.GetStallAlert(Now.AddDays(-9), 5, "manual", 120, Now));

    // ---------- Stop wanted by an external tool (its witness file) ----------
    [Fact]
    public void Witness_written_2_min_ago_silences_the_stop_alert() =>
        Assert.Null(Alerts.GetStopAlert("Running", "Stopped", Never, Now, new PauseWitness(Now.AddMinutes(-2), "plex")));

    [Fact]
    public void Witness_written_10_min_ago_does_not() =>
        Assert.NotNull(Alerts.GetStopAlert("Running", "Stopped", Never, Now, new PauseWitness(Now.AddMinutes(-10), "plex")));

    [Fact]
    public void Witness_parse_takes_the_first_line_trimmed_and_null_when_unreadable()
    {
        Assert.Equal("remote Plex stream", PauseWitness.Parse(Now, " remote Plex stream \r\nsecond line")!.Why);
        Assert.Equal("", PauseWitness.Parse(Now, "")!.Why);
        Assert.Null(PauseWitness.Parse(Now, null));
        Assert.Null(PauseWitness.Read(Path.Combine(Path.GetTempPath(), "bbm-no-such-witness.txt")));
    }

    [Fact]
    public void Witness_read_from_a_file_carries_its_write_time()
    {
        var f = Path.Combine(Path.GetTempPath(), "bbm-witness-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(f, "other machine's turn\r\n");
            File.SetLastWriteTime(f, Now);
            Assert.Equal(new PauseWitness(Now, "other machine's turn"), PauseWitness.Read(f));
        }
        finally
        {
            File.Delete(f);
        }
    }

    [Fact]
    public void Tracker_ignores_a_witness_older_than_the_last_run()
    {
        var t = new AlertTracker();
        var w = new PauseWitness(Now.AddMinutes(-30), "plex");
        Assert.Equal(w, t.Effective(w)); // never seen running: kept
        t.OnServiceStatus("Running", Now);
        Assert.Null(t.Effective(w)); // paused earlier, started here since: leftover
        Assert.NotNull(t.Effective(new PauseWitness(Now.AddSeconds(1), "plex")));
        // and the stop that follows is a real one
        Assert.Equal(Alerts.StopMessage, t.OnServiceStatus("Stopped", Now.AddMinutes(10), t.Effective(w)));
    }

    [Fact]
    public void Tracker_stop_by_the_tool_does_not_ring()
    {
        var t = new AlertTracker();
        t.OnServiceStatus("Running", Now);
        var w = new PauseWitness(Now.AddSeconds(2), "plex");
        Assert.Null(t.OnServiceStatus("Stopped", Now.AddSeconds(3), t.Effective(w)));
    }

    [Fact]
    public void Details_carry_the_reason_after_the_start_type() =>
        Assert.Equal("Automatic  -  remote Plex stream", ServiceText.GetDetails("Automatic", null, null, false, "remote Plex stream"));

    // ---------- Tracker: one alert per silence ----------
    [Fact]
    public void Tracker_stop_rings_once_on_the_transition_and_not_after_a_request()
    {
        var t = new AlertTracker();
        Assert.Null(t.OnServiceStatus("Stopped", Now)); // already stopped at launch
        t = new AlertTracker();
        Assert.Null(t.OnServiceStatus("Running", Now));
        Assert.Equal(Now, t.RunSince);
        Assert.Equal(Alerts.StopMessage, t.OnServiceStatus("Stopped", Now.AddSeconds(3)));
        Assert.Null(t.OnServiceStatus("Stopped", Now.AddSeconds(6)));

        var asked = new AlertTracker();
        asked.OnServiceStatus("Running", Now);
        asked.MarkAsked(Now.AddSeconds(1));
        Assert.Null(asked.OnServiceStatus("Stopped", Now.AddSeconds(3)));
    }

    [Fact]
    public void Tracker_stall_one_alert_per_silence_a_new_send_opens_another()
    {
        var t = new AlertTracker { StallMin = 120 };
        t.OnServiceStatus("Running", Now);
        Assert.Null(t.CheckStall(null, 50, "continuously", Now.AddMinutes(60)));
        var first = t.CheckStall(null, 50, "continuously", Now.AddMinutes(130));
        Assert.Equal("Nothing has been sent for 2 h 10 while 50 files are waiting.", first);
        Assert.Null(t.CheckStall(null, 50, "continuously", Now.AddMinutes(140))); // same silence
        var sent = Now.AddMinutes(150);
        Assert.Null(t.CheckStall(sent, 50, "continuously", Now.AddMinutes(200)));
        Assert.NotNull(t.CheckStall(sent, 50, "continuously", Now.AddMinutes(280))); // a new silence
    }

    [Fact]
    public void Tracker_stall_is_silent_while_the_service_is_not_running()
    {
        var t = new AlertTracker();
        Assert.Null(t.CheckStall(Now.AddHours(-9), 50, "continuously", Now));
    }

    // ---------- Service text ----------
    [Theory]
    [InlineData("StartPending", "Starting...")]
    [InlineData("StopPending", "Stopping...")]
    [InlineData("ContinuePending", "Resuming...")]
    [InlineData("PausePending", "Pausing...")]
    [InlineData("Paused", "Paused")]
    [InlineData("Running", "Running")]
    [InlineData("Stopped", "Stopped")]
    [InlineData("Automatic", "Automatic")]
    [InlineData("Manual", "Manual")]
    [InlineData("Disabled", "Disabled")]
    [InlineData("Boot", "Boot")]
    public void Service_text_in_plain_words_unknown_as_is(string raw, string want) => Assert.Equal(want, ServiceText.Get(raw));

    [Fact]
    public void Service_status_text_and_kind()
    {
        Assert.Equal("Service not found", ServiceText.GetStatusText(null));
        Assert.Equal(ServiceStatusKind.NotFound, ServiceText.Classify(null));
        Assert.Equal(ServiceStatusKind.NotFound, ServiceText.Classify("")); // the alert tracker's word for "absent"
        Assert.Equal("Service not found", ServiceText.GetStatusText(""));
        Assert.Equal(ServiceStatusKind.Running, ServiceText.Classify("Running"));
        Assert.Equal(ServiceStatusKind.Stopped, ServiceText.Classify("Stopped"));
        Assert.Equal(ServiceStatusKind.Transitional, ServiceText.Classify("StartPending"));
        Assert.Equal(ServiceStatusKind.Paused, ServiceText.Classify("Paused"));
        Assert.Equal("Paused", ServiceText.GetStatusText("Paused"));
        Assert.Equal("Paused", ServiceText.GetStatusText("Paused"));
        Assert.Equal("Starting...", ServiceText.GetStatusText("StartPending"));
        Assert.Equal("'bzserv' does not exist", ServiceText.NotFoundDetails);
    }

    [Fact]
    public void Service_details_line()
    {
        Assert.Equal("Automatic", ServiceText.GetDetails("Automatic", null, null));
        Assert.Equal("Manual  -  PID 4242  -  45.2 MB", ServiceText.GetDetails("Manual", 4242, 47395635));
        Assert.Equal("Automatic  -  PID 17", ServiceText.GetDetails("Automatic", 17, null));
        Assert.Equal("Automatic", ServiceText.GetDetails("Automatic", 0, 1));
        Assert.Equal("Automatic  -  PID ?", ServiceText.GetDetails("Automatic", null, null, pidFailed: true));
        Assert.Equal("Disabled", ServiceText.GetDetails("Disabled", null, null));
    }

    // ---------- Confirmations and requests ----------
    [Fact]
    public void Start_and_Stop_ask_Restart_goes_without_a_box()
    {
        Assert.Equal("Start the Backblaze service?", ServiceCommands.GetConfirmation(ServiceAction.Start));
        Assert.Equal("Stop the Backblaze service? Backups will be suspended.", ServiceCommands.GetConfirmation(ServiceAction.Stop));
        Assert.Null(ServiceCommands.GetConfirmation(ServiceAction.Restart));
        // every action classified, Restart alone bare
        var bare = Enum.GetValues<ServiceAction>().Where(a => ServiceCommands.GetConfirmation(a) is null).ToArray();
        Assert.Equal(new[] { ServiceAction.Restart }, bare);
    }
}
