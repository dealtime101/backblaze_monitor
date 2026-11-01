using BackblazeMonitor.Core;
using Xunit;

namespace BackblazeMonitor.Core.Tests;

public class SettingsAndBzInfoTests
{
    // ---------- settings.txt ----------
    [Fact]
    public void Defaults_when_nothing_is_saved()
    {
        var s = Settings.Parse(null);
        Assert.False(s.TopMost);
        Assert.True(s.ShowBits);
        Assert.Equal(0, s.Period);
        Assert.Equal(120, s.StallAlertMin);
        Assert.Null(s.Schedule);
    }

    [Fact]
    public void Serialized_lines_and_file_text()
    {
        var s = new Settings { TopMost = true, ShowBits = false, Period = 2, StallAlertMin = 90, Schedule = Schedule.Parse("8:00-22:00=3") };
        Assert.Equal(new[] { "TopMost=True", "Units=bytes", "Period=2", "StallAlertMin=90", "Schedule=08:00-22:00=3" }, s.ToLines());
        Assert.Equal("TopMost=True\r\nUnits=bytes\r\nPeriod=2\r\nStallAlertMin=90\r\nSchedule=08:00-22:00=3\r\n", s.ToFileText());
        Assert.Equal(new[] { "TopMost=False", "Units=bits", "Period=0", "StallAlertMin=120", "Schedule=" }, new Settings().ToLines());
    }

    [Fact]
    public void Round_trip()
    {
        var s = new Settings { TopMost = true, ShowBits = false, Period = 2, StallAlertMin = 0, Schedule = Schedule.Parse("22:00-06:00=5") };
        var back = Settings.Parse(s.ToFileText());
        Assert.True(back.TopMost);
        Assert.False(back.ShowBits);
        Assert.Equal(2, back.Period);
        Assert.Equal(0, back.StallAlertMin);
        Assert.Equal("22:00-06:00=5", back.Schedule!.Text);
        Assert.Equal(1320, back.Schedule.Start);
    }

    [Fact]
    public void Period_is_taken_modulo_the_number_of_periods_and_garbage_is_ignored()
    {
        Assert.Equal(1, Settings.Parse("Period=7").Period);
        Assert.Equal(0, Settings.Parse("Period=x").Period);
        Assert.Equal(120, Settings.Parse("StallAlertMin=zz").StallAlertMin);
        Assert.Equal(120, Settings.Parse("StallAlertMin=99999999999999999999").StallAlertMin);
        Assert.Null(Settings.Parse("Schedule=nonsense").Schedule);
        Assert.False(Settings.Parse("TopMost=False").TopMost);
        Assert.True(Settings.Parse("topmost=true").TopMost);
    }

    [Fact]
    public void Load_and_save_a_file()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-settings").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.txt");
            Assert.Equal(120, Settings.Load(path).StallAlertMin); // missing file
            var s = new Settings { TopMost = true, Period = 1, Schedule = Schedule.Parse("08:00-22:00=3") };
            Assert.True(s.Save(path));
            Assert.Equal("TopMost=True\r\nUnits=bits\r\nPeriod=1\r\nStallAlertMin=120\r\nSchedule=08:00-22:00=3\r\n", File.ReadAllText(path));
            var back = Settings.Load(path);
            Assert.True(back.TopMost);
            Assert.Equal(1, back.Period);
            Assert.Equal("08:00-22:00=3", back.Schedule!.Text);
            Assert.False(s.Save(Path.Combine(dir, "absent", "settings.txt")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------- bzinfo.xml ----------
    // BAC466.33: read in the middle of a rewrite (no closing tag): nothing moves, the stamp is not kept
    [Fact]
    public void A_bzinfo_cut_before_its_closing_tag_is_not_read_and_is_retried()
    {
        var sync = new BzInfoSync();
        var stamp = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var whole = BzXml("true", "continuously");
        var cut = whole.Substring(0, whole.IndexOf("</bzinfo>", StringComparison.Ordinal));
        Assert.Equal(BzInfoSyncResult.Unreadable, sync.Sync(stamp, () => cut));
        Assert.Equal(BzInfo.Unknown, sync.Info);
        Assert.Equal(BzInfoSyncResult.Updated, sync.Sync(stamp, () => whole + "\r\n")); // same stamp: read again
        Assert.Equal("continuously", sync.Info.Schedule);
    }

    private static string BzXml(string auto, string sched) =>
        $"<bzinfo><do_backup net_auto_throttle=\"{auto}\" net_throttle=\"50\" num_backup_threads=\"4\" backup_schedule_type=\"{sched}\" /></bzinfo>";

    private const string Manual = "Backblaze manual: 50 %, 4 thread(s) - click for auto / once_per_day";
    private const string Auto = "Backblaze: automatic - the limit above is the cap / continuously";

    private static string Show(BzInfo i) => $"{i.StateText} / {i.Schedule}";

    [Fact]
    public void Parse_manual_and_automatic()
    {
        var m = BzInfo.Parse(BzXml("false", "once_per_day"));
        Assert.Equal(Manual, Show(m));
        Assert.True(m.HasLink);
        Assert.Equal(50, m.Throttle);
        Assert.Equal(4, m.Threads);
        var a = BzInfo.Parse(BzXml("true", "continuously"));
        Assert.Equal(Auto, Show(a));
        Assert.False(a.HasLink);
        Assert.Equal("Backblaze: configuration not found", BzInfo.NotFoundText);
    }

    [Fact]
    public void A_longer_attribute_name_is_not_read_as_the_one_wanted()
    {
        var raw = "<bzinfo><do_backup xx_net_throttle=\"99\" max_num_backup_threads=\"99\" old_backup_schedule_type=\"never\" " +
                  "net_throttle=\"50\" num_backup_threads=\"4\" backup_schedule_type=\"continuously\" net_auto_throttle=\"true\" /></bzinfo>";
        var info = BzInfo.Parse(raw);
        Assert.Equal(50, info.Throttle);
        Assert.Equal(4, info.Threads);
        Assert.Equal("continuously", info.Schedule);
        Assert.True(info.Auto);
        // only the lookalikes: nothing is read
        Assert.False(BzInfo.Parse("<bzinfo><do_backup xx_net_throttle=\"99\" max_num_backup_threads=\"99\" /></bzinfo>").Known);
    }

    [Fact]
    public void Nothing_read_is_not_shown_as_manual_zero_percent_with_a_link()
    {
        Assert.False(BzInfo.Unknown.Known);
        Assert.Equal(BzInfo.UnreadText, BzInfo.Unknown.StateText);
        Assert.False(BzInfo.Unknown.HasLink);
        // a file that holds none of the settings changes nothing
        var empty = BzInfo.Parse("<bzinfo><do_backup /></bzinfo>");
        Assert.False(empty.Known);
        Assert.False(empty.HasLink);
        // one setting is enough to know
        var some = BzInfo.Parse("<bzinfo><do_backup net_auto_throttle=\"false\" /></bzinfo>");
        Assert.True(some.Known);
        Assert.True(some.HasLink);
        Assert.True(BzInfo.Parse(BzXml("true", "continuously")).Known);
    }

    [Fact]
    public void Parse_keeps_the_previous_value_of_an_absent_attribute()
    {
        var prev = BzInfo.Parse(BzXml("false", "once_per_day"));
        var next = BzInfo.Parse("<bzinfo><do_backup net_throttle=\"80\" /></bzinfo>", prev); // the real structure, attributes missing from do_backup
        Assert.Equal(80, next.Throttle);
        Assert.Equal(4, next.Threads);
        Assert.Equal("once_per_day", next.Schedule);
        Assert.False(next.Auto);
        Assert.Equal(prev.Threads, BzInfo.Parse("<bzinfo><do_backup num_backup_threads=\"x\" /></bzinfo>", prev).Threads);
    }

    // The timestamp is remembered only after a successful read.
    [Fact]
    public void Locked_at_startup_is_re_read_on_the_next_pass()
    {
        var sync = new BzInfoSync();
        var stamp = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(BzInfoSyncResult.Unreadable, sync.Sync(stamp, () => null));
        Assert.Equal(BzInfoSyncResult.Unreadable, sync.Sync(stamp, () => ""));
        Assert.Equal(BzInfoSyncResult.Updated, sync.Sync(stamp, () => BzXml("false", "once_per_day")));
        Assert.Equal(Manual, Show(sync.Info));
    }

    [Fact]
    public void Written_during_the_read_is_re_read_on_the_next_pass()
    {
        var sync = new BzInfoSync();
        var stamp = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var text = BzXml("false", "once_per_day");

        // The pass takes the file's timestamp first, then reads: the file is rewritten (new text, new
        // timestamp) while the read runs, so the timestamp the pass holds is older than the text it got.
        var held = stamp;
        Assert.Equal(BzInfoSyncResult.Updated, sync.Sync(held, () =>
        {
            text = BzXml("true", "continuously");
            stamp = stamp.AddSeconds(1);
            return text;
        }));

        // next pass: the file's timestamp moved, so it is read again (not Unchanged), and only then it settles
        Assert.Equal(BzInfoSyncResult.Updated, sync.Sync(stamp, () => text));
        Assert.Equal(BzInfoSyncResult.Unchanged, sync.Sync(stamp, () => text));
        Assert.Equal(Auto, Show(sync.Info));
    }

    [Fact]
    public void Same_timestamp_is_not_re_read_and_invalidate_forces_it()
    {
        var sync = new BzInfoSync();
        var stamp = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(BzInfoSyncResult.Updated, sync.Sync(stamp, () => BzXml("true", "continuously")));
        Assert.Equal(BzInfoSyncResult.Unchanged, sync.Sync(stamp, () => BzXml("false", "once_per_day")));
        Assert.Equal(Auto, Show(sync.Info));
        sync.Invalidate();
        Assert.Equal(BzInfoSyncResult.Updated, sync.Sync(stamp, () => BzXml("false", "once_per_day")));
        Assert.Equal(Manual, Show(sync.Info));
        Assert.Equal(BzInfoSyncResult.NotFound, sync.Sync(null, () => "x"));
    }

    // ---------- Misc ----------
    [Fact]
    public void Shared_file_reads_text_and_returns_null_when_missing()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-shared").FullName;
        try
        {
            var p = Path.Combine(dir, "bzinfo.xml");
            File.WriteAllText(p, BzXml("true", "continuously"));
            Assert.Equal(BzXml("true", "continuously"), SharedFile.ReadText(p));
            Assert.Null(SharedFile.ReadText(Path.Combine(dir, "absent.xml")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Encoded_command_round_trips_unicode()
    {
        const string cmd = "Write-Output 'été ’ ok'";
        Assert.Equal(cmd, EncodedCommand.Decode(EncodedCommand.Encode(cmd)));
        Assert.Equal("VwByAGkAdABlAA==", EncodedCommand.Encode("Write"));
    }

    [Fact]
    public void Backblaze_paths_follow_the_system_program_data_folder()
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var p in new[] { BzPaths.LogDir, BzPaths.InfoPath, BzPaths.RemainPath, BzPaths.TotalPath, BzPaths.PauseWitnessPath })
        {
            Assert.StartsWith(data, p, StringComparison.Ordinal);
        }

        Assert.Equal(Path.Combine(data, "Backblaze", "bzdata", "bzinfo.xml"), BzPaths.InfoPath);
        Assert.Equal("bzreports_lastfilestransmitted", Path.GetFileName(BzPaths.LogDir));
    }

    [Fact]
    public void App_identity()
    {
        // the number changes with each release: only the shape of the title is pinned
        Assert.Matches(@"^[0-9]+\.[0-9]+$", AppInfo.Version);
        Assert.Equal($"Backblaze Monitor v{AppInfo.Version}", AppInfo.Title);
        Assert.Equal(3000, BzConstants.TickMs);
        Assert.Equal(9_000_000_000L, BzConstants.SlotTicks);
        Assert.Equal(1800, BzConstants.WindowSec);
        Assert.Equal(150, BzConstants.IdleSec);
        Assert.Equal(180, BzConstants.GapSec);
        Assert.Equal(20, BzConstants.RecentMax);
        Assert.Equal(3600, BzConstants.VolStepSec);
        Assert.Equal(8 * 86400, BzConstants.VolKeepSec);
        Assert.Equal(7 * 86400, BzConstants.VolRateSec);
    }
}
