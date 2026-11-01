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
    public void Positions_are_in_bytes_with_multibyte_characters_before_and_after_the_cut()
    {
        // e-acute (2 bytes) and the typographic apostrophe (3 bytes), as in real paths
        var first = "C:\\musique\\\u00e9t\u00e9 1\r\n";
        var cutLine = "C:\\musique\\l\u2019\u00e9t\u00e9 en cou";
        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes(first + cutLine));
        var r = LogReader.ReadFrom(ms, 0)!;
        Assert.Equal(first, r.Text);
        Assert.Equal(Encoding.UTF8.GetByteCount(first), r.Pos); // bytes, not characters
        Assert.NotEqual(first.Length, r.Pos);

        var rest = "rs\r\n";
        ms.Seek(0, SeekOrigin.End);
        ms.Write(Encoding.UTF8.GetBytes(rest));
        r = LogReader.ReadFrom(ms, r.Pos)!;
        Assert.Equal(cutLine + rest, r.Text); // the line is whole, nothing doubled or lost
        Assert.Equal(Encoding.UTF8.GetByteCount(first + cutLine + rest), r.Pos);

        // a read that ends in the middle of a multibyte character waits for its end: LF is the only cut
        var bytes = Encoding.UTF8.GetBytes("a\n\u00e9");
        var chunk = LogReader.ExtractCompleteLines(bytes.Take(bytes.Length - 1).ToArray(), bytes.Length - 1, 0);
        Assert.Equal("a\n", chunk.Text);
        Assert.Equal(2, chunk.Pos);
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
    public void A_big_log_is_taken_in_capped_passes_that_end_on_line_ends_and_add_up()
    {
        var text = string.Concat(Enumerable.Range(1, 20).Select(i => $"line-{i:00}\n")); // 8 bytes a line
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var got = new StringBuilder();
        long pos = 0;
        var passes = 0;
        LogChunk? c;
        do
        {
            c = LogReader.ReadFrom(ms, pos, 50)!; // 6 lines fit in 50 bytes
            Assert.True(c.Text.Length <= 50);
            Assert.EndsWith("\n", c.Text);
            got.Append(c.Text);
            pos = c.Pos;
            passes++;
        }
        while (c.More);
        Assert.Equal(text, got.ToString());
        Assert.Equal(4, passes); // 6 + 6 + 6 + 2
        Assert.False(c.More);
    }

    [Fact]
    public void A_line_longer_than_the_cap_is_skipped_so_the_reading_goes_on()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 100) + "\nok\n"));
        var c = LogReader.ReadFrom(ms, 0, 40)!;
        Assert.True(c.More);
        Assert.Equal(40, c.Pos); // moved on, not stuck at 0
    }

    [Fact]
    public void A_missing_file_is_silent_and_an_unreadable_one_is_reported()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-fail").FullName;
        var seen = new List<string>();
        void Listen(string p, Exception e) { if (p.StartsWith(dir, StringComparison.Ordinal)) seen.Add(Path.GetFileName(p) + ":" + e.GetType().Name); } // only this test's folder: other tests run in parallel
        ReadFailures.Reported += Listen;
        try
        {
            Assert.Null(LogReader.ReadFrom(Path.Combine(dir, "absent.log"), 0));
            Assert.Null(SharedFile.ReadText(Path.Combine(dir, "absent.xml")));
            Assert.Empty(seen);
            Directory.CreateDirectory(Path.Combine(dir, "locked.xml")); // a folder where a file is expected: access refused
            Assert.Null(SharedFile.ReadText(Path.Combine(dir, "locked.xml")));
            Assert.Single(seen);
            Assert.StartsWith("locked.xml:", seen[0]);
        }
        finally
        {
            ReadFailures.Reported -= Listen;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_history_that_cannot_be_written_or_read_is_reported()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-histfail").FullName;
        var seen = new List<string>();
        void Listen(string p, Exception e) { if (p.StartsWith(dir, StringComparison.Ordinal)) seen.Add(Path.GetFileName(p)); }
        ReadFailures.Reported += Listen;
        try
        {
            var logs = new Dictionary<string, LogEntry>();
            Assert.False(HistoryFile.Save(Path.Combine(dir, "no-such-folder", "historique.txt"), logs));
            Assert.Contains("historique.txt", seen);
            Directory.CreateDirectory(Path.Combine(dir, "volumes.txt"));
            Assert.Null(VolumeSamples.TryRead(Path.Combine(dir, "volumes.txt")));
            Assert.Contains("volumes.txt", seen);
        }
        finally
        {
            ReadFailures.Reported -= Listen;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_byte_order_mark_at_the_start_of_the_file_is_not_part_of_the_text()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("line\n")).ToArray();
        var r = LogReader.ReadFrom(new MemoryStream(bytes), 0)!;
        Assert.Equal("line\n", r.Text);
        Assert.Equal(bytes.Length, r.Pos); // the position still counts the bytes of the file
        // only at the start: from a later position nothing is dropped
        Assert.Equal("\uFEFFx\n", LogReader.ExtractCompleteLines(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x', 10 }, 5, 7).Text);
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
