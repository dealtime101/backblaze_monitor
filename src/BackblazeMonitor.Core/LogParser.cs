using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BackblazeMonitor.Core;

/// <summary>A transmitted block read from the Backblaze log.</summary>
/// <param name="Time">Local time written in the log line (end of the block).</param>
/// <param name="Bits">Rate measured by Backblaze for this block, in bits per second.</param>
/// <param name="Bytes">Block size in bytes.</param>
/// <param name="Name">Text after the byte count ("Chunk 00032 of I:\...").</param>
public sealed record LogBlock(DateTime Time, double Bits, double Bytes, string Name)
{
    /// <summary>
    /// In-flight time of the block before its line appears: the line is written at the end, and each
    /// thread has its own rate. At 262 kbit/s a 10 MB block takes 320 s with no line at all.
    /// </summary>
    public double InFlightSeconds => Bits > 0 ? 8 * Bytes / Bits : 0;
}

/// <summary>A file named by a log line, with the time of that line.</summary>
public sealed record RecentFile(DateTime Time, string Path);

/// <summary>
/// Complete lines read from a log: the text and the position just after the last line ending.
/// <paramref name="Restarted"/>: the file was replaced by a longer one, so the text is read from the start.
/// </summary>
public sealed record LogChunk(string Text, long Pos, bool Restarted = false);

/// <summary>Parsing of the Backblaze transmission log (pure functions over text).</summary>
public static class LogParser
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// A transmitted block: <c>2026-09-07 22:41:50 -  large  - throttle manual  9 - 2077 kBits/sec - 10462586 bytes - Chunk 00054 of G:\...</c>.
    /// "dedup - 0 bytes" lines and plain listing lines do not match.
    /// </summary>
    public static readonly Regex LineRegex = new(
        @"^([0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2})\s+-.*?-\s*([0-9]+)\s+kBits/sec\s+-\s+([0-9]+)\s+bytes\s+-\s+(.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Any line that names a file: sent whole, in chunks, in a batch of small files (no byte count) or
    /// deduplicated. The path starts at the drive letter: file names contain " - ".
    /// </summary>
    public static readonly Regex FileRegex = new(
        @"^([0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}) .*? - (?:Chunk [0-9a-f]+ of )?([A-Za-z]:\\.*)$",
        RegexOptions.CultureInvariant);

    private static bool TryParseTime(string s, out DateTime t) =>
        DateTime.TryParseExact(s, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    /// <summary>
    /// Transmitted blocks of a log chunk. "kBits/sec" is searched first and the regex runs afterwards,
    /// line by line: over the whole text the regex costs 20 to 120 times more on some machines.
    /// </summary>
    public static IReadOnlyList<LogBlock> GetLogBlocks(string text)
    {
        var list = new List<LogBlock>();
        var i = 0;
        while (i < text.Length && (i = text.IndexOf("kBits/sec", i, StringComparison.Ordinal)) >= 0)
        {
            var a = text.LastIndexOf('\n', i) + 1;
            var b = text.IndexOf('\n', i);
            if (b < 0) b = text.Length;
            var m = LineRegex.Match(text.Substring(a, b - a).TrimEnd());
            i = b;
            if (!m.Success) continue;
            if (!TryParseTime(m.Groups[1].Value, out var t)) continue;
            if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kbits)) continue;
            if (!double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bytes)) continue;
            list.Add(new LogBlock(t, kbits * 1000.0, bytes, m.Groups[4].Value));
        }

        return list;
    }

    /// <summary>
    /// Files named by a log chunk, from newest to oldest, each once (case-insensitive path), at most
    /// <paramref name="max"/>. Read from the end and stopped at the max-th file: on the first pass the text
    /// is the whole log (31.7 MB measured on one machine).
    /// </summary>
    public static IReadOnlyList<RecentFile> GetRecentFiles(string text, int max)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<RecentFile>();
        var b = text.Length;
        while (b > 0 && list.Count < max)
        {
            var a = text.LastIndexOf('\n', b - 1) + 1;
            var m = FileRegex.Match(text.Substring(a, b - a).TrimEnd());
            b = a - 1;
            if (!m.Success || seen.Contains(m.Groups[2].Value)) continue;
            if (!TryParseTime(m.Groups[1].Value, out var t)) continue;
            seen.Add(m.Groups[2].Value);
            list.Add(new RecentFile(t, m.Groups[2].Value));
        }

        return list;
    }
}

/// <summary>
/// Incremental reading of a growing log: each log is read once, then only what gets appended, up to the
/// last line ending (a line still being written is read whole on the next pass).
/// </summary>
public static class LogReader
{
    /// <summary>
    /// Pure core: from the bytes read at <paramref name="pos"/>, keeps the complete lines only.
    /// </summary>
    /// <param name="buffer">Bytes read from the log, starting at <paramref name="pos"/>.</param>
    /// <param name="read">Number of valid bytes in the buffer.</param>
    /// <param name="pos">Position of the first byte in the file.</param>
    public static LogChunk ExtractCompleteLines(byte[] buffer, int read, long pos)
    {
        var end = 0;
        if (read > 0) end = Array.LastIndexOf(buffer, (byte)10, read - 1) + 1;
        // A UTF-8 byte order mark at the start of the file is not text: left in, it would sit before the first
        // line's timestamp and the anchored patterns would skip that line
        var skip = pos == 0 && end >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        return new LogChunk(Encoding.UTF8.GetString(buffer, skip, end - skip), pos + end);
    }

    /// <summary>
    /// Reads from <paramref name="pos"/>. Returns <c>null</c> if the stream is shorter than
    /// <paramref name="pos"/> (log truncated or replaced). A saved position always follows a line ending: if
    /// the byte before it is not one, the file is another one (recreated, and already longer than the old
    /// position), so it is read from the start and the chunk says so.
    /// </summary>
    public static LogChunk? ReadFrom(Stream stream, long pos)
    {
        if (stream.Length < pos) return null;
        var restarted = false;
        if (pos > 0)
        {
            stream.Seek(pos - 1, SeekOrigin.Begin);
            if (stream.ReadByte() != 10)
            {
                pos = 0;
                restarted = true;
            }
        }

        stream.Seek(pos, SeekOrigin.Begin);
        var buf = new byte[stream.Length - pos];
        var read = 0;
        while (read < buf.Length)
        {
            var n = stream.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }

        var chunk = ExtractCompleteLines(buf, read, pos);
        return restarted ? chunk with { Restarted = true } : chunk;
    }

    /// <summary>
    /// Opens the log with full sharing (bztransmit writes to it at the same time) and reads from
    /// <paramref name="pos"/>. <c>null</c> when unreadable, missing or shorter than <paramref name="pos"/>.
    /// </summary>
    public static LogChunk? ReadFrom(string path, long pos)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadFrom(fs, pos);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            ReadFailures.Report(path, ex);
            return null;
        }
    }
}

/// <summary>Whole-file text read with full sharing (Backblaze rewrites its reports).</summary>
public static class SharedFile
{
    /// <summary>
    /// Reads the whole file as UTF-8, or returns <c>null</c> when it cannot be read. A missing file is silent; a
    /// locked, refused or failed one is also passed to <see cref="ReadFailures"/>.
    /// </summary>
    public static string? ReadText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[fs.Length];
            var read = 0;
            while (read < buf.Length)
            {
                var n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }

            return Encoding.UTF8.GetString(buf, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            ReadFailures.Report(path, ex);
            return null;
        }
    }
}
