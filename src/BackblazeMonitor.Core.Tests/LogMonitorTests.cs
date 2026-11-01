using System.Globalization;
using BackblazeMonitor.Core;
using Xunit;

namespace BackblazeMonitor.Core.Tests;

/// <summary>End-to-end tests of the log folder reading, on real files in a temporary folder.</summary>
public sealed class LogMonitorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bbm-monitor").FullName;
    private readonly string _logDir;
    private readonly string _history;

    public LogMonitorTests()
    {
        _logDir = Path.Combine(_dir, "journaux");
        Directory.CreateDirectory(_logDir);
        _history = Path.Combine(_dir, "historique.txt");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static DateTime T(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture);

    private void WriteLog(string name, string text, string at)
    {
        var f = Path.Combine(_logDir, name);
        File.WriteAllText(f, text);
        File.SetLastWriteTime(f, T(at));
    }

    private LogMonitor NewMonitor() => new(_logDir, _history);

    private static string Summary(LogMonitor m, string at = "2026-09-25 00:10:00")
    {
        m.Sample(T(at));
        var tot = 0.0;
        var n = 0;
        foreach (var e in m.Logs.Values)
        {
            foreach (var v in e.Slots.Values)
            {
                tot += v;
                n++;
            }
        }

        return FormattableString.Invariant($"{tot} bytes in {n} slots, {m.Points.Count} points, last {m.LastSent:HH:mm:ss}");
    }

    private string Saved()
    {
        var h = HistoryFile.Read(_history, _logDir);
        var tot = h.Values.Sum(e => e.Slots.Values.Sum());
        return $"{h.Count} logs, {tot} bytes";
    }

    // The shared story of the next tests: each step is a method, and each test replays the steps it depends on
    // (without asserting them) before its own, so one failure points at one scenario.
    private sealed record Story(LogMonitor M, string L24, string L25b, int CutAt);

    private Story Straddling()
    {
        var m = NewMonitor();
        var l24 = Lines.Join(Lines.Blk("2026-09-24 12:00:00", 5000), Lines.Blk("2026-09-24 23:50:00", 1000), "");
        var l25a = Lines.Blk("2026-09-25 00:05:00", 2000) + "\r\n";
        var l25b = Lines.Blk("2026-09-25 00:06:00", 3000) + "\r\n";
        var cutAt = l25b.IndexOf("3000 bytes", StringComparison.Ordinal) + 2; // cut inside the bytes: "...kBits/sec - 30"
        WriteLog("24.log", l24, "2026-09-24 23:59:59");
        WriteLog("25.log", l25a + l25b.Substring(0, cutAt), "2026-09-25 00:06:00");
        WriteLog("17.log", Lines.Blk("2026-09-17 12:00:00", 90000) + "\r\n", "2026-09-17 23:59:59");
        WriteLog("23.zip", Lines.Blk("2026-09-23 08:00:00", 7000) + "\r\n", "2026-09-24 04:23:00");
        WriteLog("bzstat_lastfile_transmitted.xml", "<x>1 kBits/sec</x>", "2026-09-25 00:06:00");
        return new Story(m, l24, l25b, cutAt);
    }

    private void AppendRest(Story st)
    {
        File.AppendAllText(Path.Combine(_logDir, "25.log"), st.L25b.Substring(st.CutAt));
        File.SetLastWriteTime(Path.Combine(_logDir, "25.log"), T("2026-09-25 00:06:30"));
    }

    private void Zip24(Story st)
    {
        WriteLog("24.zip", st.L24, "2026-09-25 00:07:00");
        File.Delete(Path.Combine(_logDir, "24.log"));
    }

    private LogMonitor Restart()
    {
        var m2 = NewMonitor();
        m2.LoadHistory();
        return m2;
    }

    private void Shorten25() =>
        WriteLog("25.log", Lines.Blk("2026-09-25 00:08:00", 500) + "\r\n", "2026-09-25 00:08:00");

    [Fact]
    public void The_window_straddles_two_logs_and_history_follows_the_logs()
    {
        var st = Straddling();
        Assert.Equal("8000 bytes in 3 slots, 2 points, last 00:05:00", Summary(st.M), ignoreCase: false);
        Assert.Equal("2 logs, 8000 bytes", Saved());
    }

    [Fact]
    public void A_log_that_grows_is_read_from_where_it_stopped_and_nothing_is_counted_twice()
    {
        var st = Straddling();
        Summary(st.M);
        AppendRest(st);
        Assert.Equal("11000 bytes in 3 slots, 3 points, last 00:06:00", Summary(st.M));
        Assert.Equal("11000 bytes in 3 slots, 3 points, last 00:06:00", Summary(st.M)); // nothing new, nothing more
    }

    [Fact]
    public void A_log_that_gets_zipped_keeps_what_was_read_and_the_history_still_counts_it()
    {
        var st = Straddling();
        Summary(st.M);
        AppendRest(st);
        Summary(st.M);
        Zip24(st);
        Assert.Equal("11000 bytes in 3 slots, 3 points, last 00:06:00", Summary(st.M)); // zipped: what was read stays

        var hist = HistoryCalculator.Get(st.M.Logs.Values.Select(e => (IReadOnlyDictionary<long, double>)e.Slots).ToList(), 86400, 900, T("2026-09-25 00:10:00"));
        Assert.Equal(11000, hist.Bytes);
    }

    [Fact]
    public void After_a_restart_a_log_gone_from_the_folder_lives_on_in_the_history_file()
    {
        var st = Straddling();
        Summary(st.M);
        AppendRest(st);
        Summary(st.M);
        Zip24(st);
        Summary(st.M);

        // 24.log now lives only in historique.txt; 25.log, saved at 2000 bytes, is re-read
        Assert.Equal("11000 bytes in 3 slots, 2 points, last 00:06:00", Summary(Restart()));
    }

    [Fact]
    public void A_log_that_got_shorter_is_read_again_from_zero()
    {
        var st = Straddling();
        Summary(st.M);
        AppendRest(st);
        Summary(st.M);
        Zip24(st);
        Summary(st.M);
        var m2 = Restart();
        Summary(m2);

        Shorten25();
        Assert.Equal("6500 bytes in 3 slots, 1 points, last 00:08:00", Summary(m2));
    }

    [Fact]
    public void Seven_days_later_the_zipped_log_leaves_the_period()
    {
        var st = Straddling();
        Summary(st.M);
        AppendRest(st);
        Summary(st.M);
        Zip24(st);
        Summary(st.M);
        var m2 = Restart();
        Summary(m2);
        Shorten25();
        Summary(m2);

        Assert.Equal("500 bytes in 1 slots, 1 points, last 00:08:00", Summary(m2, "2026-10-02 00:05:00"));
        Assert.Equal("1 logs, 500 bytes", Saved());
    }

    [Fact]
    public void With_no_more_log_left_the_state_is_nolog_and_nothing_is_plotted()
    {
        var st = Straddling();
        Summary(st.M);
        AppendRest(st);
        Summary(st.M);
        Zip24(st);
        Summary(st.M);
        var m2 = Restart();
        Summary(m2);
        Shorten25();
        Summary(m2);
        Summary(m2, "2026-10-02 00:05:00");

        File.Delete(Path.Combine(_logDir, "25.log"));
        File.Delete(Path.Combine(_logDir, "17.log"));
        m2.Sample(T("2026-10-02 00:05:00"));
        Assert.Equal("NoLog 0 0", $"{m2.State} {m2.Logs.Count} {m2.Points.Count}");
    }

    [Fact]
    public void A_missing_log_folder_is_nolog_and_reappearing_goes_back_to_init()
    {
        var m = new LogMonitor(Path.Combine(_dir, "absent"), _history);
        m.Sample(T("2026-09-25 00:10:00"));
        Assert.Equal(NetState.NoLog, m.State);
        m.Refresh(T("2026-09-25 00:10:00"));
        Assert.Equal(0, m.Rate);
        Assert.Equal("Transmission log not found", NetworkTexts.GetStatsText(m.State, 0, null, 0, 0, true));
        Assert.Equal("No log", NetworkTexts.GetTag(m.State));

        var m2 = NewMonitor();
        m2.Sample(T("2026-09-25 00:10:00")); // empty folder
        Assert.Equal(NetState.NoLog, m2.State);
        WriteLog("25.log", Lines.Blk("2026-09-25 00:05:00", 10) + "\r\n", "2026-09-25 00:05:00");
        m2.Sample(T("2026-09-25 00:10:00"));
        Assert.Equal(NetState.Init, m2.State);
    }

    [Fact]
    public void A_history_that_fails_to_write_is_reported_and_retried()
    {
        WriteLog("02.log", Lines.Blk("2026-10-02 00:05:00", 700) + "\r\n", "2026-10-02 00:05:00");
        var broken = new LogMonitor(_logDir, Path.Combine(_dir, "nulle-part", "historique.txt")) { HistSaved = -42 };
        Assert.False(broken.SaveHistory());
        broken.Sample(T("2026-10-02 00:06:00"));
        Assert.Equal(-42, broken.HistSaved); // failure, so it will retry

        var ok = new LogMonitor(_logDir, _history) { HistSaved = -42 };
        ok.Sample(T("2026-10-02 00:07:00"));
        Assert.NotEqual(-42, ok.HistSaved);
        Assert.True(File.Exists(_history));
        // unchanged files, but the slot count differs from the last save: written again after a failure
        var retry = new LogMonitor(_logDir, Path.Combine(_dir, "later", "historique.txt"));
        retry.Sample(T("2026-10-02 00:07:00"));
        Assert.Equal(0, retry.HistSaved);
        Directory.CreateDirectory(Path.Combine(_dir, "later"));
        retry.Sample(T("2026-10-02 00:07:30"));
        Assert.Equal(1, retry.HistSaved);
        Assert.True(File.Exists(Path.Combine(_dir, "later", "historique.txt")));
    }

    // C1: no .log left (everything zipped, rotation not done): the slots already read must survive, and the
    // reappearing log must not overwrite historique.txt with its own slots only
    [Fact]
    public void Losing_every_log_keeps_the_history_and_a_returning_log_does_not_overwrite_it()
    {
        WriteLog("24.log", Lines.Blk("2026-09-24 12:00:00", 5000) + "\r\n", "2026-09-24 12:00:00");
        var m = NewMonitor();
        m.Sample(T("2026-09-24 12:05:00"));
        Assert.Equal("1 logs, 5000 bytes", Saved());

        File.Delete(Path.Combine(_logDir, "24.log"));
        m.Sample(T("2026-09-25 00:00:30"));
        Assert.Equal(NetState.NoLog, m.State);
        Assert.Empty(m.Points);
        Assert.Single(m.Logs); // kept: still inside the 7 days
        Assert.Equal("1 logs, 5000 bytes", Saved());

        WriteLog("25.log", Lines.Blk("2026-09-25 00:01:00", 300) + "\r\n", "2026-09-25 00:01:00");
        m.Sample(T("2026-09-25 00:02:00"));
        Assert.Equal(NetState.Init, m.State);
        Assert.Equal("2 logs, 5300 bytes", Saved());
    }

    // C3: more bytes in a slot already counted: the slot count does not change, the content did
    [Fact]
    public void More_bytes_in_a_counted_slot_are_saved_within_five_minutes()
    {
        WriteLog("25.log", Lines.Blk("2026-09-25 00:01:00", 100) + "\r\n", "2026-09-25 00:01:00");
        var m = NewMonitor();
        m.Sample(T("2026-09-25 00:02:00"));
        Assert.Equal("1 logs, 100 bytes", Saved());

        File.AppendAllText(Path.Combine(_logDir, "25.log"), Lines.Blk("2026-09-25 00:03:00", 200) + "\r\n");
        File.SetLastWriteTime(Path.Combine(_logDir, "25.log"), T("2026-09-25 00:03:00"));
        m.Sample(T("2026-09-25 00:04:00")); // same slot, 2 min after the last save: held back
        Assert.Equal("1 logs, 100 bytes", Saved());
        m.Sample(T("2026-09-25 00:07:30")); // 5 min passed: written
        Assert.Equal("1 logs, 300 bytes", Saved());
    }

    // C2: a write that fails leaves the previous file intact, and no .tmp behind
    [Fact]
    public void A_failed_history_write_leaves_the_previous_file_intact()
    {
        var logs = new Dictionary<string, LogEntry> { [Path.Combine(_logDir, "25.log")] = new() };
        logs.Values.Single().Slots[1L] = 42;
        Assert.True(HistoryFile.Save(_history, logs));
        var before = File.ReadAllText(_history);
        Assert.False(File.Exists(_history + ".tmp"));

        Directory.CreateDirectory(_history + ".tmp"); // the temporary file cannot be created
        logs.Values.Single().Slots[2L] = 99;
        Assert.False(HistoryFile.Save(_history, logs));
        Assert.Equal(before, File.ReadAllText(_history));
    }

    // BAC466.28: the peak is never under the rate displayed above it. Three blocks finished in 2 min = 31.5 MB / 120 s =
    // 2.1 Mbps current, while the sum in flight only reaches 1.2 Mbps (few threads seen): the peak follows the larger
    [Fact]
    public void The_peak_is_never_under_the_current_rate()
    {
        static string L(string at) => $"{at} -  large  - throttle manual   8  -  400 kBits/sec - 10485760 bytes - Chunk 00032 of I:\\a.mkv";
        WriteLog("25.log", Lines.Join(L("2026-09-25 12:00:00"), L("2026-09-25 12:00:10"), L("2026-09-25 12:00:20"), ""), "2026-09-25 12:00:20");
        var m = NewMonitor();
        var now = T("2026-09-25 12:00:30");
        m.Sample(now);
        m.Refresh(now);
        Assert.Equal(3 * 10485760 * 8 / 120.0, m.Rate);
        Assert.Equal(m.Rate, m.Peak);
        Assert.True(m.Peak > 1.2e6);
    }

    // ---------- Slow upload, seen by the tile ----------
    [Fact]
    public void Slow_upload_is_uploading_then_no_transfer()
    {
        var line = @"2026-09-25 11:40:00 -  large  - throttle manual   8  -  262 kBits/sec - 10485760 bytes - Chunk 00001 of I:\l1.mkv";
        WriteLog("25.log", line + "\r\n", "2026-09-25 11:40:00");
        var m = NewMonitor();
        var texts = new List<string>();
        foreach (var s in new[] { 200, 641 })
        {
            var now = T("2026-09-25 11:40:00").AddSeconds(s);
            m.Sample(now);
            m.Refresh(now);
            texts.Add(NetworkTexts.GetTag(m.State) + ", " + Formatting.FormatRate(m.Rate, true));
        }

        Assert.Equal("Uploading, 262 kbps | No transfer, 0 kbps", string.Join(" | ", texts));
        Assert.Equal(@"Chunk 00001 of I:\l1.mkv", m.CurrentFile);
    }

    // ---------- Heavy log ----------
    private static string Vol(string at, int kbits, long bytes) =>
        $"{at} -  large  - throttle manual   11 - {kbits,5} kBits/sec - {bytes} bytes - Chunk 00000 of J:\\Series\\f.mkv";

    private static string Dedups(string at) =>
        string.Join("\r\n", Enumerable.Range(1, 1500).Select(i => $@"{at} -  small  - throttle x           -           dedup - 0 bytes - C:\Users\user\AppData\Local\f{i}.tmp")) + "\r\n";

    private string Tile(LogMonitor m, string at)
    {
        var now = T(at);
        m.Sample(now);
        m.Refresh(now);
        return NetworkTexts.GetTag(m.State) + ", " + Formatting.FormatRate(m.Rate, true) + " | " +
               NetworkTexts.GetStatsText(m.State, 0, null, m.Peak, m.Total, true);
    }

    [Fact]
    public void Heavy_log_a_dedup_burst_exceeds_128_KB_and_both_blocks_remain()
    {
        Assert.True(Dedups("2026-10-03 12:05:00").Length > 128 * 1024);
        var f = "03.log";
        WriteLog(f, Vol("2026-10-03 12:01:00", 5000, 10485760) + "\r\n" + Dedups("2026-10-03 12:05:00") + Vol("2026-10-03 12:28:30", 1000, 1048576) + "\r\n", "2026-10-03 12:28:30");
        var m = NewMonitor();
        // 70 kbps: the only block in the last 2 minutes is 1 MB (1 MB / 120 s), not the 1 Mbps of its line
        Assert.Equal("Uploading, 70 kbps | Peak 5.0 Mbps  -  11.0 MB in 30 min", Tile(m, "2026-10-03 12:29:00"));
        var p = Path.Combine(_logDir, f);
        File.AppendAllText(p, Dedups("2026-10-03 12:29:30"));
        File.SetLastWriteTime(p, T("2026-10-03 12:29:30"));
        Assert.Equal("Uploading, 70 kbps | Peak 5.0 Mbps  -  11.0 MB in 30 min", Tile(m, "2026-10-03 12:30:00"));
    }

    // ---------- Recent files across logs ----------
    private static string Sent(string at, string name) =>
        $@"{at} -  large  - throttle manual   8  -  1045 kBits/sec - 5000 bytes - C:\d\{name}";

    private static string Recent(LogMonitor m) =>
        string.Join(", ", m.Recent.Rows().OrderBy(r => r.Time).Select(r => $"{r.Time:HH:mm} {RecentFileSet.FileName(r.Path)}"));

    [Fact]
    public void Recent_the_20_most_recent_of_all_logs_and_the_oldest_drop_out()
    {
        WriteLog("01.log", string.Join("\r\n", Enumerable.Range(1, 25).Select(i => Sent($"2026-10-01 00:{i:00}:00", $"f{i}.bin"))) + "\r\n", "2026-10-01 00:25:00");
        WriteLog("30.log", Sent("2026-09-30 23:50:00", "f25.bin") + "\r\n", "2026-09-30 23:59:59");
        var m = NewMonitor();
        m.Sample(T("2026-10-01 00:30:00"));
        Assert.Equal(string.Join(", ", Enumerable.Range(6, 20).Select(i => $"00:{i:00} f{i}.bin")), Recent(m));

        var p = Path.Combine(_logDir, "01.log");
        File.AppendAllText(p, string.Join("\r\n", Sent("2026-10-01 00:26:00", "f3.bin"), Sent("2026-10-01 00:27:00", "F25.BIN"), ""));
        File.SetLastWriteTime(p, T("2026-10-01 00:27:00"));
        m.Sample(T("2026-10-01 00:30:00"));
        var want = string.Join(", ", Enumerable.Range(7, 18).Select(i => $"00:{i:00} f{i}.bin").Concat(new[] { "00:26 f3.bin", "00:27 f25.bin" }));
        Assert.Equal(want, Recent(m));
    }

    [Fact]
    public void Recent_set_keys_rows_and_file_names()
    {
        var s = new RecentFileSet();
        s.Set(@"C:\a\ici.bin", T("2026-09-25 11:58:00"));
        s.Set("C:\\a\0b.txt", T("2026-09-25 11:00:00"));
        s.Set(@"C:\a\parti.mkv", T("2026-09-24 23:10:00"));
        // An unreadable path (a NUL, as in a damaged log) is kept as is: the list shows it without failing
        Assert.Equal("ici.bin ; a?b.txt ; parti.mkv", string.Join(" ; ", s.Rows().Select(r => RecentFileSet.FileName(r.Path).Replace("\0", "?"))));
        var key = s.Key();
        s.Set(@"C:\a\neuf.txt", T("2026-09-25 11:59:00"));
        Assert.NotEqual(key, s.Key());
        Assert.Equal("neuf.txt", RecentFileSet.FileName(s.Rows()[0].Path));
        s.Remove(@"C:\a\parti.mkv");
        Assert.Equal(3, s.Count);
        Assert.Equal(T("2026-09-25 11:58:00"), s[@"C:\A\ICI.BIN"]); // paths compare case-insensitively
    }

    // ---------- Texts of the rate block ----------
    [Fact]
    public void Network_texts()
    {
        Assert.Equal("Uploading", NetworkTexts.GetTag(NetState.Ok));
        Assert.Equal("No transfer", NetworkTexts.GetTag(NetState.Idle));
        Assert.Equal("No transfer", NetworkTexts.GetTag(NetState.Init));
        Assert.Equal("Peak 5.0 Mbps  -  11.0 MB in 30 min", NetworkTexts.GetStatsText(NetState.Ok, 0, null, 5e6, 11534336, true));

        var hist = HistoryCalculator.Get(
            new[] { (IReadOnlyDictionary<long, double>)new Dictionary<long, double> { [T("2026-09-25 11:45").Ticks] = 9e8 } },
            86400, 900, T("2026-09-25 12:07:30"));
        Assert.Equal("858.3 MB over 24 h  -  average 84 kbps", NetworkTexts.GetStatsText(NetState.Ok, 1, hist, 0, 0, true));

        var help = NetworkTexts.GetTipHelp(0);
        Assert.Equal("Upload rate over 2 min, all threads combined - Last 30 minutes\r\nClick: switch between Mbps and MB/s\r\nRight-click: 30 min, 24 h, 7 d", help);
        Assert.Equal("Now sending: c.mkv\r\n" + help, NetworkTexts.GetTip(NetState.Ok, "c.mkv", help));
        Assert.Equal(help, NetworkTexts.GetTip(NetState.Idle, "c.mkv", help));
        Assert.Equal("hit\r\n" + help, NetworkTexts.GetChartTip(new GraphHit(1, "hit"), help, "ordinary"));
        Assert.Equal("ordinary", NetworkTexts.GetChartTip(null, help, "ordinary"));
    }
}
