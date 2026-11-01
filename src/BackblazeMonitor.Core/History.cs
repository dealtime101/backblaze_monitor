using System.Globalization;

namespace BackblazeMonitor.Core;

/// <summary>State kept for one log file: what was read, where, and the slots derived from it.</summary>
public sealed class LogEntry
{
    /// <summary>UTC write time at the last read; <c>null</c> for an entry restored from the history file.</summary>
    public DateTime? Stamp { get; set; }

    /// <summary>File length at the last read.</summary>
    public long Length { get; set; }

    /// <summary>Position just after the last complete line read.</summary>
    public long Pos { get; set; }

    /// <summary>Bytes per 15-minute slot: slot start in ticks to bytes.</summary>
    public Dictionary<long, double> Slots { get; } = new();

    /// <summary>Blocks of the last 30 minutes (window points).</summary>
    public IReadOnlyList<LogBlock> Points { get; set; } = Array.Empty<LogBlock>();
}

/// <summary>Result of <see cref="HistoryCalculator.Get"/>.</summary>
/// <param name="Rates">Average rate (bits/s) per slot; the last, still in progress, is scaled to the elapsed time.</param>
/// <param name="Bytes">Total bytes over the span.</param>
/// <param name="Seconds">Duration covered, current slot included.</param>
/// <param name="First">Start of the first slot (used for hover).</param>
/// <param name="SlotBytes">Bytes of each slot (used for hover).</param>
public sealed record HistoryResult(double[] Rates, double Bytes, double Seconds, DateTime First, double[] SlotBytes);

/// <summary>History: average rate per slot, slots aligned to the clock.</summary>
public static class HistoryCalculator
{
    /// <summary>Slot start (ticks) containing <paramref name="time"/> in 15-minute slots.</summary>
    public static long SlotStart(DateTime time) => time.Ticks - (time.Ticks % BzConstants.SlotTicks);

    /// <summary>Adds the bytes of <paramref name="blocks"/> to <paramref name="slots"/> (15-minute slots).</summary>
    public static void AddBlocks(IDictionary<long, double> slots, IEnumerable<LogBlock> blocks)
    {
        foreach (var p in blocks)
        {
            var k = SlotStart(p.Time);
            slots[k] = (slots.TryGetValue(k, out var old) ? old : 0.0) + p.Bytes;
        }
    }

    /// <summary>
    /// Average rate (bits/s) per slot of <paramref name="step"/> seconds over <paramref name="span"/>
    /// seconds, slots aligned to the clock. The last one, still in progress, is scaled to the time
    /// already elapsed (at least 60 s).
    /// </summary>
    /// <param name="slotSets">Tables {start in ticks to bytes}, one per log.</param>
    public static HistoryResult Get(IEnumerable<IReadOnlyDictionary<long, double>> slotSets, int span, int step, DateTime now)
    {
        // The live 30-minute curve has span 0 and step 0 and does not come through here
        if (span <= 0) throw new ArgumentOutOfRangeException(nameof(span), span, "The span must be positive.");
        if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step), step, "The step must be positive.");
        var stepT = (long)step * 10_000_000L;
        var cur = now.Ticks - (now.Ticks % stepT);
        // At least one slot: a span under half a step would round to none, and rates[n - 1] would not exist
        var n = Math.Max(1, (int)Math.Round((double)span / step, MidpointRounding.ToEven));
        var first = cur - (n - 1) * stepT;
        var bytes = new double[n];
        var total = 0.0;
        foreach (var slots in slotSets)
        {
            foreach (var (k, v) in slots)
            {
                // Never the future, whatever the slot width: a 15-minute slot later than now would fall in the
                // current hour of the 7 days but not in the 24 h (BAC466.24)
                if (k < first || k >= cur + stepT || k > now.Ticks) continue;
                bytes[(int)((k - first) / stepT)] += v;
                total += v;
            }
        }

        var elapsed = Math.Max(60.0, (now.Ticks - cur) / 1e7);
        var rates = new double[n];
        for (var i = 0; i < n; i++) rates[i] = bytes[i] * 8 / step;
        rates[n - 1] = bytes[n - 1] * 8 / elapsed;
        return new HistoryResult(rates, total, (n - 1) * (double)step + elapsed, new DateTime(first), bytes);
    }
}

/// <summary>
/// historique.txt: slots of the logs already read, carried over between launches. One
/// <c>"NN.log ticks bytes"</c> line per slot.
/// </summary>
public static class HistoryFile
{
    /// <summary>
    /// Reads the file. Missing: an empty table (history restarts from the logs). A damaged line is skipped, the
    /// others are kept. Unreadable (locked, no access): also empty here, see <see cref="TryRead"/> for the
    /// caller that must not then write over the file. Entries come back without a Stamp, so a present log is
    /// re-read from zero.
    /// </summary>
    /// <param name="historyPath">Path of historique.txt.</param>
    /// <param name="logDir">Folder joined to each log name.</param>
    public static Dictionary<string, LogEntry> Read(string historyPath, string logDir) =>
        TryRead(historyPath, logDir) ?? new Dictionary<string, LogEntry>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Like <see cref="Read"/>, but <c>null</c> when the file exists and cannot be read: that is not an empty
    /// history, and saving over it would lose the slots of logs that are already zipped.
    /// </summary>
    public static Dictionary<string, LogEntry>? TryRead(string historyPath, string logDir)
    {
        var logs = new Dictionary<string, LogEntry>(StringComparer.OrdinalIgnoreCase);
        string[] lines;
        try
        {
            lines = File.ReadAllLines(historyPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return logs;
        }
        catch (Exception ex)
        {
            ReadFailures.Report(historyPath, ex);
            return null;
        }

        foreach (var l in lines)
        {
            if (l.Length == 0) continue;
            var f = l.Split(' ');
            if (f.Length != 3
                || !long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
                || !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) continue;
            var p = Path.Combine(logDir, f[0]);
            if (!logs.TryGetValue(p, out var e)) logs[p] = e = new LogEntry();
            e.Slots[t] = b;
        }

        return logs;
    }

    // The file about to be replaced is kept as .bak (unless empty): if it ever gets cut or emptied, the slots of
    // logs that are already zipped can be put back by hand.
    private static void KeepBackup(string historyPath)
    {
        try
        {
            if (new FileInfo(historyPath) is { Exists: true, Length: > 0 }) File.Copy(historyPath, historyPath + ".bak", overwrite: true);
        }
        catch
        {
            // A backup that cannot be made does not stop the save
        }
    }

    /// <summary>
    /// Writes the file. Returns whether it was written: on a full disk, vanished folder or locked file the
    /// caller retries on the next tick instead of recording a save that did not happen.
    /// </summary>
    public static bool Save(string historyPath, IReadOnlyDictionary<string, LogEntry> logs)
    {
        try
        {
            var lines = new List<string>();
            foreach (var (p, e) in logs)
            {
                var name = Path.GetFileName(p);
                foreach (var (t, b) in e.Slots)
                {
                    lines.Add(name + " " + t.ToString(CultureInfo.InvariantCulture) + " " + b.ToString("R", CultureInfo.InvariantCulture));
                }
            }

            // Written to a neighbouring .tmp, then swapped in: a plain WriteAllLines truncates the target BEFORE
            // writing, and a full disk or a power cut would leave an empty or cut file, the last valid copy
            // lost (zipped logs cannot be re-read).
            try
            {
                KeepBackup(historyPath);
                AtomicFile.WriteAllLines(historyPath, lines);
                return true;
            }
            catch (Exception ex)
            {
                // Full disk, rights, vanished folder: said once in the error log (the caller retries)
                ReadFailures.Report(historyPath, ex, reading: false);
                return false;
            }
        }
        catch
        {
            return false;
        }
    }
}
