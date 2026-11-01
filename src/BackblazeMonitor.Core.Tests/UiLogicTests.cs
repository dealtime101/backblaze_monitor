using BackblazeMonitor.Core;
using Xunit;

namespace BackblazeMonitor.Core.Tests;

public class UiLogicTests
{
    [Theory]
    [InlineData(0, ActionOutcome.Done)]
    [InlineData(1, ActionOutcome.Failed)]
    [InlineData(2, ActionOutcome.Failed)]
    [InlineData(-1, ActionOutcome.Failed)]
    public void ExitCodeMapsToOutcome(int code, ActionOutcome expected) =>
        Assert.Equal(expected, ActionOutcomes.FromExitCode(code));

    [Theory]
    [InlineData(1223, ActionOutcome.Cancelled)]
    [InlineData(5, ActionOutcome.Failed)]
    [InlineData(0, ActionOutcome.Failed)]
    public void NativeErrorMapsToOutcome(int code, ActionOutcome expected) =>
        Assert.Equal(expected, ActionOutcomes.FromNativeError(code));

    // BAC466.34: a bzcli run cut after the delay says so
    [Theory]
    [InlineData(BzAutoOutcome.Done, "auto enabled", true)]
    [InlineData(BzAutoOutcome.Failed, "failed", false)]
    [InlineData(BzAutoOutcome.TimedOut, "timed out", false)]
    public void AutoModeMessageNamesTheOutcome(BzAutoOutcome outcome, string text, bool ok) =>
        Assert.Equal((text, ok), LimitMessages.ForAuto(outcome));

    // BAC466.14: a failed stop/restart still counts as requested here; only a declined UAC prompt does not
    [Theory]
    [InlineData(ActionOutcome.Done, true)]
    [InlineData(ActionOutcome.Failed, true)]
    [InlineData(ActionOutcome.Cancelled, false)]
    public void Only_a_declined_uac_prompt_is_not_a_request(ActionOutcome outcome, bool expected) =>
        Assert.Equal(expected, ActionOutcomes.CountsAsAsked(outcome));

    [Fact]
    public void ServiceNoticeDistinguishesCancelledFromFailed()
    {
        Assert.Null(ActionNotices.ForServiceAction(ServiceAction.Stop, ActionOutcome.Done, "Stopped"));

        var c = ActionNotices.ForServiceAction(ServiceAction.Restart, ActionOutcome.Cancelled, "Running")!;
        Assert.Equal("Restart cancelled", c.Title);
        Assert.Equal("UAC prompt declined.", c.Text);
        Assert.False(c.IsError);

        var f = ActionNotices.ForServiceAction(ServiceAction.Start, ActionOutcome.Failed, "Stopped")!;
        Assert.Equal("Start failed", f.Title);
        Assert.Equal("Backblaze: Stopped", f.Text);
        Assert.True(f.IsError);

        // an elevated run that outlived its wait is neither a success nor a failure
        var u = ActionNotices.ForServiceAction(ServiceAction.Stop, ActionOutcome.Unknown, "Running")!;
        Assert.Equal("Stop not finished", u.Title);
        Assert.Contains("result unknown", u.Text);
        Assert.False(u.IsError);
        Assert.True(ActionOutcomes.CountsAsAsked(ActionOutcome.Unknown)); // it may stop the service: no false alert
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", true)]
    [InlineData(@"C:\Program Files\dotnet\DOTNET.EXE", true)]
    [InlineData("/usr/bin/dotnet", true)]
    [InlineData(@"C:\Tools\BackblazeMonitor.exe", false)]
    [InlineData(@"C:\Tools\dotnet-tools\BackblazeMonitor.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheDotnetHostIsNeverTheShortcutTarget(string? path, bool host) => Assert.Equal(host, StartupApproved.IsDotnetHost(path));

    [Fact]
    public void StartupApprovedFlagUsesTheOddByte()
    {
        Assert.False(StartupApproved.IsDisabled(null));
        Assert.False(StartupApproved.IsDisabled(Array.Empty<byte>()));
        Assert.False(StartupApproved.IsDisabled(new byte[] { 2, 0, 0, 0 }));
        Assert.True(StartupApproved.IsDisabled(new byte[] { 3, 0, 0, 0 }));
        Assert.False(StartupApproved.IsDisabled("not bytes"));
    }

    [Fact]
    public void StartupNeedsTheShortcutAndNoDisableFlag()
    {
        Assert.False(StartupApproved.IsEnabled(false, null));
        Assert.True(StartupApproved.IsEnabled(true, null));
        Assert.True(StartupApproved.IsEnabled(true, new byte[] { 2 }));
        Assert.False(StartupApproved.IsEnabled(true, new byte[] { 3 }));
    }

    [Fact]
    public void AgentRunFinishesOnlyWhenTheRunMovedAndIsNotRunning()
    {
        var before = new DateTime(2026, 9, 30, 10, 0, 0);
        Assert.False(AgentRun.IsFinished(before, before, 0));
        Assert.False(AgentRun.IsFinished(before, before.AddSeconds(2), AgentRun.RunningResult));
        Assert.True(AgentRun.IsFinished(before, before.AddSeconds(2), 0));
        Assert.True(AgentRun.IsFinished(before, before.AddHours(-1), 1)); // clocks went back
        Assert.True(AgentRun.Succeeded(0));
        Assert.False(AgentRun.Succeeded(2));
    }

    [Fact]
    public void AgentStartGapOnlyWithinTheSameSecond()
    {
        var before = new DateTime(2026, 9, 30, 10, 0, 0);
        Assert.Equal(0, AgentRun.GetStartGapMs(before, before.AddSeconds(5)));
        Assert.Equal(600, AgentRun.GetStartGapMs(before, before.AddMilliseconds(500)), 3);
    }

    [Fact]
    public void LimitMessageShowsResultThenNextSwitch()
    {
        var s = Schedule.Parse("08:00-22:00=3");
        var now = new DateTime(2026, 9, 30, 12, 0, 0);
        var m = new LimitMessageState();
        m.Set("applied", true);
        Assert.Equal(MessageTone.Good, m.Tone);
        for (var i = 0; i < LimitMessageState.MessageTicks - 1; i++)
        {
            Assert.False(m.Tick(s, now));
            Assert.Equal("applied", m.Text);
        }

        Assert.True(m.Tick(s, now));
        Assert.Equal("22:00 -> none", m.Text);
        Assert.Equal(MessageTone.Neutral, m.Tone);
        Assert.False(m.Tick(s, now));
        Assert.True(m.Tick(null, now));
        Assert.Equal("", m.Text);
    }

    // BAC466.46: the window is saved but the policy in place does not follow it (task deleted by hand)
    [Theory]
    [InlineData(12, 0, 3_000_000, true)]  // in the window, 3 Mbps wanted and in place
    [InlineData(12, 0, 0, false)]         // in the window, no policy: not applied
    [InlineData(23, 0, 0, true)]          // outside, none wanted and in place
    [InlineData(23, 0, 3_000_000, false)] // outside, the limit is still there
    [InlineData(8, 2, 0, true)]           // 2 min after a switch: the task may not have run yet
    [InlineData(21, 57, 0, true)]         // 3 min BEFORE the next switch, no policy where the window wants one: tolerance alone
    [InlineData(21, 57, 3_000_000, true)] // 3 min before the next one, and the policy is the expected one anyway
    [InlineData(7, 57, 3_000_000, true)]  // 3 min before the window opens, the limit still there: the task may not have run
    [InlineData(21, 54, 0, false)]        // 6 min before the switch: beyond the tolerance
    [InlineData(8, 5, 0, false)]          // 5 min after a switch: beyond the tolerance
    [InlineData(12, 0, -1, true)]         // policy not read yet
    public void ScheduleIsAppliedChecksThePolicyAwayFromSwitches(int h, int min, double bits, bool expected) =>
        Assert.Equal(expected, Schedule.Parse("08:00-22:00=3")!.IsApplied(new DateTime(2026, 9, 30, h, min, 0), bits));

    [Fact]
    public void LimitMessageFlagsAWindowThatDoesNotFollow()
    {
        var s = Schedule.Parse("08:00-22:00=3");
        var now = new DateTime(2026, 9, 30, 12, 0, 0);
        var m = new LimitMessageState();
        Assert.True(m.Tick(s, now, 0));
        Assert.Equal("window not active", m.Text);
        Assert.Equal(MessageTone.Bad, m.Tone);
        Assert.False(m.Tick(s, now, 0));
        Assert.True(m.Tick(s, now, 3_000_000)); // the policy follows again
        Assert.Equal("22:00 -> none", m.Text);
        Assert.Equal(MessageTone.Neutral, m.Tone);
    }

    [Fact]
    public void QosPacerReadsFirstTickThenEveryTwentyAndSoonOnRequest()
    {
        var p = new QosReadPacer();
        Assert.True(p.Due());
        for (var i = 0; i < QosReadPacer.Period - 1; i++) Assert.False(p.Due());
        Assert.True(p.Due());
        p.RequestSoon();
        Assert.True(p.Due());
    }

    [Theory]
    [InlineData("", 0.0)]
    [InlineData("  \r\n", 0.0)]
    [InlineData("3000000", 3000000.0)]
    [InlineData("2500000.5\r\n", 2500000.5)]
    public void QosOutputParses(string output, double expected) =>
        Assert.Equal(expected, QosReadResult.Parse(output));

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("Infinity")]
    [InlineData("NaN")]
    public void QosGarbageIsUnknown(string output) => Assert.Null(QosReadResult.Parse(output));

    [Fact]
    public void QosScriptTargetsTheOwnPolicy()
    {
        var script = QosReadResult.GetScript();
        Assert.Contains("'" + BzConstants.QosName + "'", script);
        Assert.Contains("-ErrorAction Stop", script); // a failed read must not look like an empty answer
        Assert.DoesNotContain("SilentlyContinue", script);
        Assert.Contains("ObjectNotFound') { exit 0 }", script); // only a missing policy is "no limit"
        Assert.EndsWith("exit 1 }", script);
    }

    [Fact]
    public void BzCliArgumentsQuoteThePath() =>
        Assert.Equal("configure -j \"C:\\Temp\\a b.json\"", BzAutoCommands.GetArguments("C:\\Temp\\a b.json"));
}
