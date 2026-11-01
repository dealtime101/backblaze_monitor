using System.Globalization;
using BackblazeMonitor.Core;
using Xunit;

namespace BackblazeMonitor.Core.Tests;

public class HistoryAndGraphTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0);
    private static readonly DateTime Later = new(2026, 9, 25, 12, 7, 30);

    private static long Slot(string at) => DateTime.Parse(at, CultureInfo.InvariantCulture).Ticks;

    private static IReadOnlyDictionary<long, double> Set(params (string at, double bytes)[] items) =>
        items.ToDictionary(i => Slot(i.at), i => i.bytes);

    private static readonly IReadOnlyDictionary<long, double>[] Sets =
    {
        Set(("2026-09-25 11:45", 9e8), ("2026-09-25 12:00", 4.5e7), ("2026-09-25 12:15", 1e3)),
        Set(("2026-09-24 12:00", 1e9), ("2026-09-24 12:15", 9e6)),
    };

    private static readonly HistoryResult Hj = HistoryCalculator.Get(Sets, 86400, 900, Later);
    private static readonly HistoryResult Hs = HistoryCalculator.Get(Sets, 604800, 3600, Later);

    // ---------- History ----------
    [Fact]
    public void H24_has_96_slots() => Assert.Equal(96, Hj.Rates.Length);

    [Fact]
    public void H24_current_slot_7_min_30_elapsed() => Assert.Equal(800000, Hj.Rates[95]);

    [Fact]
    public void H24_full_slot() => Assert.Equal(8000000, Hj.Rates[94]);

    [Fact]
    public void H24_first_slot() => Assert.Equal(80000, Hj.Rates[0]);

    [Fact]
    public void H24_volume_without_the_previous_day_at_the_same_time_or_the_future() => Assert.Equal(954000000, Hj.Bytes);

    [Fact]
    public void H24_duration_covered() => Assert.Equal(85950, Hj.Seconds);

    [Fact]
    public void Current_slot_at_least_60_s() =>
        Assert.Equal(4.5e7 * 8 / 60, HistoryCalculator.Get(Sets, 86400, 900, new DateTime(2026, 9, 25, 12, 0, 10)).Rates[95], 6);

    [Fact]
    public void D7_has_168_slots() => Assert.Equal(168, Hs.Rates.Length);

    [Fact]
    public void D7_quarter_hours_grouped_by_hour() => Assert.Equal((1e9 + 9e6) * 8 / 3600, Hs.Rates[143], 6);

    [Fact]
    public void D7_volume_without_the_future_slot() => Assert.Equal(1954000000, Hs.Bytes); // 12:15 is after now (12:07:30): BAC466.24

    [Fact]
    public void A_span_shorter_than_half_a_step_still_gives_one_slot()
    {
        var h = HistoryCalculator.Get(Sets, 100, 900, Now);
        Assert.Single(h.Rates);
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryCalculator.Get(Sets, 86400, 0, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryCalculator.Get(Sets, 0, 900, Now));
    }

    [Fact]
    public void History_without_a_log() =>
        Assert.Equal(0, HistoryCalculator.Get(Array.Empty<IReadOnlyDictionary<long, double>>(), 86400, 900, Now).Bytes);

    [Fact]
    public void Blocks_are_added_to_15_minute_slots()
    {
        var slots = new Dictionary<long, double>();
        HistoryCalculator.AddBlocks(slots, new[]
        {
            new LogBlock(new DateTime(2026, 9, 25, 0, 5, 0), 1, 2000, "a"),
            new LogBlock(new DateTime(2026, 9, 25, 0, 6, 0), 1, 3000, "b"),
            new LogBlock(new DateTime(2026, 9, 25, 0, 15, 0), 1, 7, "c"),
        });
        Assert.Equal(2, slots.Count);
        Assert.Equal(5000, slots[new DateTime(2026, 9, 25, 0, 0, 0).Ticks]);
        Assert.Equal(7, slots[new DateTime(2026, 9, 25, 0, 15, 0).Ticks]);
    }

    // ---------- History file ----------
    [Fact]
    public void History_file_round_trips_and_failures_are_reported()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-hist").FullName;
        try
        {
            var path = Path.Combine(dir, "historique.txt");
            var logs = new Dictionary<string, LogEntry>(StringComparer.OrdinalIgnoreCase);
            var e = new LogEntry();
            e.Slots[639000000000000000L] = 5000;
            e.Slots[639000009000000000L] = 1234.5;
            logs[Path.Combine(dir, "25.log")] = e;
            Assert.True(HistoryFile.Save(path, logs));
            Assert.Equal(new[] { "25.log 639000000000000000 5000", "25.log 639000009000000000 1234.5" }, File.ReadAllLines(path));

            var back = HistoryFile.Read(path, dir);
            var entry = Assert.Single(back);
            Assert.Equal(Path.Combine(dir, "25.log"), entry.Key, ignoreCase: true); // re-attached to the right .log
            var b = entry.Value;
            Assert.Equal(5000, b.Slots[639000000000000000L]);
            Assert.Equal(1234.5, b.Slots[639000009000000000L]);
            Assert.Null(b.Stamp);

            // missing folder: reported, so the caller retries
            Assert.False(HistoryFile.Save(Path.Combine(dir, "nulle-part", "historique.txt"), logs));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void History_missing_or_damaged_restarts_from_the_logs()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-hist2").FullName;
        try
        {
            var path = Path.Combine(dir, "historique.txt");
            Assert.Empty(HistoryFile.Read(path, dir));
            // one damaged line is skipped, the good one is kept
            File.WriteAllText(path, "25.log 639000000000000000 5000\r\nn importe quoi\r\n25.log x 5\r\n");
            var kept = Assert.Single(HistoryFile.Read(path, dir)).Value;
            Assert.Equal(5000, Assert.Single(kept.Slots).Value);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------- Graph hover ----------
    private static readonly LogBlock[] Pts =
    {
        new(new DateTime(2026, 9, 25, 11, 40, 0), 2e6, 5e6, "a.mkv"),
        new(new DateTime(2026, 9, 25, 11, 40, 30), 4e6, 1e7, "b.mkv"),
        new(new DateTime(2026, 9, 25, 11, 50, 0), 1e6, 2e6, "c.mkv"),
    };

    private static string LastLine(GraphHit? h) => h is null ? "-" : h.Text.Split('\n')[^1];

    [Fact]
    public void Hover_30min_closest_block_nothing_in_a_gap()
    {
        var names = new[] { 0, 85, 91, 92, 93, 108, 110, 181, 185, 271 }
            .Select(x => LastLine(GraphCurve.GetHit(Pts, null, 0, x, 272, Now, true)));
        Assert.Equal("- a.mkv a.mkv a.mkv b.mkv b.mkv - c.mkv c.mkv -", string.Join(' ', names));
    }

    [Fact]
    public void Hover_30min_time_rate_size_and_file() =>
        Assert.Equal("11:40:00  -  2.0 Mbps  -  4.8 MB\r\na.mkv", GraphCurve.GetHit(Pts, null, 0, 91, 272, Now, true)!.Text);

    [Fact]
    public void Hover_30min_marker_on_the_block() =>
        Assert.Equal(90.67, Math.Round(GraphCurve.GetHit(Pts, null, 0, 91, 272, Now, true)!.X, 2));

    [Fact]
    public void Hover_30min_in_MBps() =>
        Assert.Equal("11:40:00  -  244 KB/s  -  4.8 MB\r\na.mkv", GraphCurve.GetHit(Pts, null, 0, 91, 272, Now, false)!.Text);

    [Fact]
    public void Hover_30min_no_block_no_hit() =>
        Assert.Null(GraphCurve.GetHit(Array.Empty<LogBlock>(), null, 0, 91, 272, Now, true));

    [Fact]
    public void Hover_24h_slot_pointed_at() =>
        Assert.Equal("Fri 25, 11:45 to 12:00  -  858.3 MB  -  average 8.0 Mbps", GraphCurve.GetHit(Array.Empty<LogBlock>(), Hj, 1, 267, 272, Later, true)!.Text);

    [Fact]
    public void Hover_24h_marker_in_the_middle_of_the_slot() =>
        Assert.Equal(267.75, GraphCurve.GetHit(Array.Empty<LogBlock>(), Hj, 1, 267, 272, Later, true)!.X);

    [Fact]
    public void Hover_24h_first_slot() =>
        Assert.Equal("Thu 24, 12:15 to 12:30  -  8.6 MB  -  average 80 kbps", GraphCurve.GetHit(Array.Empty<LogBlock>(), Hj, 1, 0, 272, Later, true)!.Text);

    [Fact]
    public void Hover_24h_current_slot_up_to_now() =>
        Assert.Equal("Fri 25, 12:00 to 12:07  -  42.9 MB  -  average 800 kbps", GraphCurve.GetHit(Array.Empty<LogBlock>(), Hj, 1, 271, 272, Later, true)!.Text);

    [Fact]
    public void Hover_24h_outside_the_graph_in_MBps() =>
        Assert.Equal("Fri 25, 12:00 to 12:07  -  42.9 MB  -  average 98 KB/s", GraphCurve.GetHit(Array.Empty<LogBlock>(), Hj, 1, 400, 272, Later, false)!.Text);

    [Fact]
    public void Hover_7d_hour_pointed_at() =>
        Assert.Equal("Thu 24, 12:00 to 13:00  -  962.3 MB  -  average 2.2 Mbps", GraphCurve.GetHit(Array.Empty<LogBlock>(), Hs, 2, 232, 272, Later, true)!.Text);

    [Fact]
    public void Hover_empty_history_nothing() =>
        Assert.Null(GraphCurve.GetHit(Array.Empty<LogBlock>(), HistoryCalculator.Get(Array.Empty<IReadOnlyDictionary<long, double>>(), 86400, 900, Now), 1, 100, 272, Now, true));

    // ---------- Slow blocks: hover and curve ----------
    private static readonly LogBlock Lent = new(new DateTime(2026, 9, 25, 11, 40, 0), 262e3, 10485760, "l1.mkv");
    private static readonly LogBlock[] Lents =
    {
        Lent,
        new(new DateTime(2026, 9, 25, 11, 45, 0), 262e3, 10485760, "l2.mkv"),
    };

    [Fact]
    public void Slow_hover_continuous_between_blocks_then_while_the_upload_continues()
    {
        var names = new (int x, string at)[] { (200, "11:50"), (210, "11:50"), (260, "11:50"), (260, "11:56") }
            .Select(c => LastLine(GraphCurve.GetHit(Lents, null, 0, c.x, 272, DateTime.Parse("2026-09-25 " + c.at, CultureInfo.InvariantCulture), true)));
        Assert.Equal("l1.mkv l2.mkv l2.mkv -", string.Join(' ', names));
    }

    private static string Curve(IReadOnlyList<LogBlock> p, string at) =>
        string.Join(' ', GraphCurve.Get(p, DateTime.Parse("2026-09-25 " + at, CultureInfo.InvariantCulture), 272, 38)
            .Select(c => $"{c.X.ToString("0", CultureInfo.InvariantCulture)},{c.Y.ToString("0", CultureInfo.InvariantCulture)}"));

    // l2 (ends 11:45) started 20 s before the end of l1 (11:40): TWO threads in flight then, 524 kbps (BAC466.28)
    [Fact]
    public void Slow_curve_continuous_right_edge_held() => Assert.Equal("0,36 181,36 181,21 227,28 272,28", Curve(Lents, "11:50"));

    [Fact]
    public void Slow_curve_right_edge_at_zero_upload_stopped() => Assert.Equal("0,36 127,36 127,21 172,28 272,36", Curve(Lents, "11:56"));

    // BAC466.28: the curve and the peak took the rate of ONE block, the current rate is the sum of the threads.
    // Three 10 MB blocks at 400 kbps (209.7 s each) ending at t0, t0+10 and t0+20: 3 in flight at t0, then 2, then 1
    private static readonly DateTime Tp = new(2026, 9, 25, 12, 0, 0);
    private static readonly LogBlock[] Trois =
        new[] { 0, 10, 20 }.Select(s => new LogBlock(Tp.AddSeconds(s), 400e3, 10485760, $"p{s}.mkv")).ToArray();

    [Fact]
    public void Series_is_the_blocks_in_flight_at_the_end_of_each() =>
        Assert.Equal("1200 800 400", string.Join(' ', RateCalculator.GetRateSeries(Trois).Select(v => (v / 1e3).ToString("0", CultureInfo.InvariantCulture))));

    [Fact]
    public void Series_five_blocks_ending_in_the_same_second_are_all_in_flight_for_each_other()
    {
        var t = new DateTime(2026, 9, 27, 14, 0, 0);
        var five = Enumerable.Range(0, 5).Select(i => new LogBlock(t, 452e3, 10485760, $"f{i}")).ToArray();
        var s = RateCalculator.GetRateSeries(five);
        Assert.All(s, v => Assert.Equal(5 * 452e3, v));
        for (var i = 0; i < five.Length; i++) Assert.Equal(s[i], RateCalculator.GetInFlightRate(five, i)); // series = one point
    }

    [Fact]
    public void Series_a_block_alone_keeps_its_rate_and_no_block_is_an_empty_series()
    {
        Assert.Equal(new[] { 262e3 }, RateCalculator.GetRateSeries(new[] { Lent }));
        Assert.Empty(RateCalculator.GetRateSeries(Array.Empty<LogBlock>()));
    }

    // 30 min ending 12:00:30, scale 1.2 Mbps x 1.15: y = 36 - 1.2/1.38 x 33 = 7; 0.8 -> 17; 0.4 -> 26 (a single block gave 24)
    [Fact]
    public void Curve_height_follows_the_sum_of_threads_not_the_block() =>
        Assert.Equal("0,36 267,36 267,7 269,17 270,26 272,26", Curve(Trois, "12:00:30"));

    [Fact]
    public void Hover_shows_the_sum_of_threads() =>
        Assert.Equal(
            "12:00:00  -  1.2 Mbps  -  10.0 MB\r\np0.mkv",
            GraphCurve.GetHit(Trois, null, 0, (1800 - 30) / 1800.0 * 272, 272, Tp.AddSeconds(30), true)!.Text);

    [Fact]
    public void Curve_a_real_gap_drops_back_to_zero() => Assert.Equal("0,36 91,36 91,22 95,7 95,36 181,36 181,29 272,36", Curve(Pts, "12:00"));

    [Fact]
    public void Blocks_older_than_the_window_are_left_off_the_curve_and_do_not_set_the_scale()
    {
        var now = new DateTime(2026, 9, 25, 12, 0, 0);
        var recent = new LogBlock(now.AddMinutes(-10), 2e6, 5e6, "new.mkv");
        var old = new LogBlock(now.AddMinutes(-60), 400e6, 5e8, "old.mkv"); // a huge peak outside the 30 minutes
        var with = GraphCurve.Get(new[] { old, recent }, now, 272, 38);
        var without = GraphCurve.Get(new[] { recent }, now, 272, 38);
        Assert.All(with, pt => Assert.True(pt.X >= 0));
        Assert.Equal(without.Select(pt => (pt.X, pt.Y)), with.Select(pt => (pt.X, pt.Y)));
        Assert.Empty(GraphCurve.Get(new[] { old }, now, 272, 38)); // nothing in the window
    }

    [Fact]
    public void Curve_of_no_point_is_empty() => Assert.Empty(GraphCurve.Get(Array.Empty<LogBlock>(), Now, 272, 38));

    [Fact]
    public void History_curve_has_two_points_per_slot_and_is_empty_without_bytes()
    {
        Assert.Equal(96 * 2, GraphCurve.GetHistory(Hj, 272, 38).Count);
        Assert.Empty(GraphCurve.GetHistory(HistoryCalculator.Get(Array.Empty<IReadOnlyDictionary<long, double>>(), 86400, 900, Now), 272, 38));
        Assert.Empty(GraphCurve.GetHistory(null, 272, 38));
    }

    // ---------- Rates ----------
    [Fact]
    public void Slow_block_in_progress_for_twice_its_time_in_flight()
    {
        var got = new[] { 200, 640, 641 }.Select(s => RateCalculator.IsSending(Lent, Lent.Time.AddSeconds(s)));
        Assert.Equal("True True False", string.Join(' ', got));
    }

    [Fact]
    public void Fast_block_in_progress_150_s_after_its_line()
    {
        var rapide = new LogBlock(new DateTime(2026, 9, 25, 11, 40, 0), 4e6, 1e7, "r.mkv");
        var got = new[] { 150, 151 }.Select(s => RateCalculator.IsSending(rapide, rapide.Time.AddSeconds(s)));
        Assert.Equal("True False", string.Join(' ', got));
    }

    [Fact]
    public void Gap_beyond_GapSec_excluding_in_flight_time()
    {
        var ta = new DateTime(2026, 9, 25, 11, 30, 0);
        var trous = new (int s, double bits, double bytes)[] { (200, 4e6, 1e7), (201, 4e6, 1e7), (580, 1e5, 5e6), (581, 1e5, 5e6) }
            .Select(c => RateCalculator.IsHole(ta, new LogBlock(ta.AddSeconds(c.s), c.bits, c.bytes, "")));
        Assert.Equal("False True False True", string.Join(' ', trous));
    }

    [Fact]
    public void Rate_is_the_sum_of_the_threads_not_the_last_block()
    {
        var t0 = new DateTime(2026, 9, 27, 12, 4, 10);
        var fils = Enumerable.Range(1, 5).Select(i => new LogBlock(t0, 452e3, 10485760, $"f{i}.mkv")).ToList();
        fils.Add(new LogBlock(t0.AddSeconds(-36), 564e3, 10485760, "f6.mkv"));
        fils.Add(new LogBlock(t0.AddSeconds(15), 421e3, 10485760, "f7.mkv"));
        var sorted = fils.OrderBy(p => p.Time).ToList();
        // 4.9 Mbps holds for the 2-minute window pinned by The_rate_window_is_two_minutes
        Assert.Equal("4.9 Mbps", Formatting.FormatRate(RateCalculator.GetCurrentRate(sorted, t0.AddSeconds(15)), true));
    }

    [Fact]
    public void The_rate_window_is_two_minutes() => Assert.Equal(120, BzConstants.RateSec);

    [Fact]
    public void Rate_nothing_finished_block_in_flight_keeps_its_rate()
    {
        var t0 = new DateTime(2026, 9, 27, 12, 4, 10);
        var enVol = new[] { new LogBlock(t0.AddSeconds(-300), 262e3, 10485760, "l.mkv") };
        Assert.Equal(262e3, RateCalculator.GetCurrentRate(enVol, t0));
    }

    [Fact]
    public void Rate_no_block_zero() => Assert.Equal(0, RateCalculator.GetCurrentRate(Array.Empty<LogBlock>(), Now));
}
