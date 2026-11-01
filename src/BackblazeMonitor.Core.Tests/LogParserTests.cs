using System.Text;
using BackblazeMonitor.Core;
using Xunit;

namespace BackblazeMonitor.Core.Tests;

/// <summary>Real line formats (two machines, 2026-09-25), in CRLF as on the machines.</summary>
internal static class Lines
{
    public static string Blk(string at, long bytes) =>
        $"{at} -  large  - throttle manual   8  -  1045 kBits/sec - {bytes} bytes - Chunk 00032 of I:\\Series\\a b.mkv";

    public const string Dedup = @"2026-09-23 09:16:31 -  small  - throttle x           -           dedup - 0 bytes - C:\ProgramData\x.ini";

    public const string Batch = "2026-09-24 04:20:24 -  large  - throttle autotest 11 -   337 kBits/sec -  1661213 bytes - Multiple small files batched in one request, the 999 files are listed below:";

    public static string Join(params string[] lines) => string.Join("\r\n", lines);
}

public class LogParserTests
{
    [Fact]
    public void Blocks_dedup_and_listing_ignored()
    {
        var blocs = LogParser.GetLogBlocks(Lines.Join(Lines.Blk("2026-09-25 10:00:05", 10486458), Lines.Dedup, Lines.Batch, @"U:\Unity\liste.db", ""));
        Assert.Equal(2, blocs.Count);
        Assert.Equal(1045000, blocs[0].Bits);
        Assert.Equal(10486458, blocs[0].Bytes);
        Assert.Equal(@"Chunk 00032 of I:\Series\a b.mkv", blocs[0].Name);
        Assert.Equal("2026-09-24 04:20:24 1661213", $"{blocs[1].Time:yyyy-MM-dd HH:mm:ss} {blocs[1].Bytes}");
    }

    [Fact]
    public void Block_last_line_without_a_line_ending() =>
        Assert.Single(LogParser.GetLogBlocks(Lines.Blk("2026-09-25 10:00:05", 1)));

    [Fact]
    public void Block_impossible_date_ignored() =>
        Assert.Empty(LogParser.GetLogBlocks(Lines.Blk("2026-13-45 10:00:05", 1)));

    [Fact]
    public void Block_empty_text() => Assert.Empty(LogParser.GetLogBlocks(""));

    [Fact]
    public void Block_in_flight_time_for_a_slow_block()
    {
        var lent = new LogBlock(new DateTime(2026, 9, 25, 11, 40, 0), 262e3, 10485760, "l1.mkv");
        Assert.Equal(320.2, Math.Round(lent.InFlightSeconds, 1));
    }

    [Fact]
    public void Block_without_rate_is_zero_seconds_no_error() =>
        Assert.Equal(0, new LogBlock(DateTime.MinValue, 0, 1e7, "x").InFlightSeconds);

    // ---------- Recent files ----------
    private static readonly string Ea = "\u00e9";
    private static readonly string Ap = "\u2019";

    private static string RecentText() => Lines.Join(
        @"2026-09-25 10:00:01 -  large  - throttle manual   8  -  1045 kBits/sec - 52428800 bytes - I:\Films\Le film.mkv",
        Lines.Blk("2026-09-25 10:00:05", 10486458),
        Lines.Batch,
        @"2026-09-25 10:00:06 -                                                       - C:\Docs\Blacklist - S03E11 - 1080p.mkv",
        $@"2026-09-25 10:00:06 -                                                       - C:\Docs\{Ea}t{Ea}.txt",
        Lines.Dedup.Replace("2026-09-23 09:16:31", "2026-09-25 10:00:07"),
        $@"2026-09-25 10:00:08 -  small  - throttle x           -           dedup - 0 bytes - Chunk 00009 of D:\Photos\l{Ap}{Ea}t{Ea}\IMG.CR3",
        Lines.Blk("2026-09-25 10:00:09", 10486458).Replace(@"I:\Series\a b.mkv", @"I:\SERIES\A B.MKV"),
        Lines.Blk("2026-13-45 10:00:10", 1).Replace("a b.mkv", "date.mkv"),
        @"U:\Unity\liste.db",
        "");

    [Fact]
    public void Recent_one_per_file_most_recent_to_oldest()
    {
        var got = string.Join(" | ", LogParser.GetRecentFiles(RecentText(), 20).Select(f => $"{f.Time:HH:mm:ss} {f.Path}"));
        Assert.Equal(
            $@"10:00:09 I:\SERIES\A B.MKV | 10:00:08 D:\Photos\l{Ap}{Ea}t{Ea}\IMG.CR3 | 10:00:07 C:\ProgramData\x.ini | 10:00:06 C:\Docs\{Ea}t{Ea}.txt | 10:00:06 C:\Docs\Blacklist - S03E11 - 1080p.mkv | 10:00:01 I:\Films\Le film.mkv",
            got);
    }

    [Fact]
    public void Recent_max_at_most_the_most_recent() =>
        Assert.Equal($@"I:\SERIES\A B.MKV | D:\Photos\l{Ap}{Ea}t{Ea}\IMG.CR3", string.Join(" | ", LogParser.GetRecentFiles(RecentText(), 2).Select(f => f.Path)));

    [Fact]
    public void Recent_empty_text() => Assert.Empty(LogParser.GetRecentFiles("", 20));

    // ---------- Reading in chunks ----------
    [Fact]
    public void Read_up_to_the_last_line_ending_then_the_rest_whole()
    {
        var bytes = Encoding.UTF8.GetBytes("ligne 1\r\nligne 2 en cou");
        using var ms = new MemoryStream();
        ms.Write(bytes);
        var r = LogReader.ReadFrom(ms, 0)!;
        Assert.Equal("ligne 1\r\n|9", r.Text + "|" + r.Pos);

        var more = Encoding.UTF8.GetBytes("rs\r\n");
        ms.Seek(0, SeekOrigin.End);
        ms.Write(more);
        r = LogReader.ReadFrom(ms, r.Pos)!;
        Assert.Equal("ligne 2 en cours\r\n|27", r.Text + "|" + r.Pos);

        Assert.Null(LogReader.ReadFrom(ms, 1000));
    }

    [Fact]
    public void Read_from_a_file_and_missing_file()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-journaux").FullName;
        try
        {
            var p = Path.Combine(dir, "essai.log");
            File.WriteAllText(p, "ligne 1\r\nligne 2 en cou");
            var r = LogReader.ReadFrom(p, 0)!;
            Assert.Equal("ligne 1\r\n|9", r.Text + "|" + r.Pos);
            File.AppendAllText(p, "rs\r\n");
            r = LogReader.ReadFrom(p, r.Pos)!;
            Assert.Equal("ligne 2 en cours\r\n|27", r.Text + "|" + r.Pos);
            Assert.Null(LogReader.ReadFrom(p, 1000));
            Assert.Null(LogReader.ReadFrom(Path.Combine(dir, "absent.log"), 0));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_file_replaced_by_a_longer_one_is_read_from_the_start()
    {
        // the saved position (4) follows "abc\n"; the new file is longer but 4 is in the middle of its first line
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("first-line\nsecond\n"));
        var r = LogReader.ReadFrom(ms, 4)!;
        Assert.True(r.Restarted);
        Assert.Equal("first-line\nsecond\n", r.Text);
        Assert.Equal(18, r.Pos);
        // the same file read from a real line end is a plain continuation
        using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes("first-line\nsecond\n"));
        var c = LogReader.ReadFrom(ms2, 11)!;
        Assert.False(c.Restarted);
        Assert.Equal("second\n", c.Text);
    }

    [Fact]
    public void Read_with_no_new_data_gives_empty_chunk_at_same_position()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("abc\n"));
        var r = LogReader.ReadFrom(ms, 4)!;
        Assert.Equal("", r.Text);
        Assert.Equal(4, r.Pos);
    }
}
