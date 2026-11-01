namespace BackblazeMonitor.Core;

/// <summary>State of the transmission, from the log.</summary>
public enum NetState
{
    /// <summary>Nothing read yet.</summary>
    Init,

    /// <summary>Sending: a block was completed recently, or a slow one is still in flight.</summary>
    Ok,

    /// <summary>Nothing sent.</summary>
    Idle,

    /// <summary>No transmission log found.</summary>
    NoLog,
}

/// <summary>A file of the log folder, as <see cref="LogMonitor.Sample(IReadOnlyList{LogFileInfo}, DateTime, Func{string, long, LogChunk?}?)"/> sees it.</summary>
/// <param name="FullPath">Full path.</param>
/// <param name="LastWriteLocal">Last write time, local.</param>
/// <param name="LastWriteUtc">Last write time, UTC.</param>
/// <param name="Length">Length in bytes.</param>
public sealed record LogFileInfo(string FullPath, DateTime LastWriteLocal, DateTime LastWriteUtc, long Length)
{
    /// <summary>True for a ".log" file (the day's log; past days end up zipped).</summary>
    public bool IsLog => string.Equals(Path.GetExtension(FullPath), ".log", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the Backblaze transmission logs incrementally and keeps what the tile shows: window points, 15-minute
/// history slots, the latest files, the last block sent. Ports <c>Sample-Network</c> (reading) and the
/// computing half of <c>Refresh-Network</c>.
/// </summary>
/// <remarks>
/// The log rotates daily (NN.log, NN = day of the month); past days end up zipped one to three days later and
/// those zips are readable only by SYSTEM and administrators: the non-elevated tile keeps what it has read
/// itself (historique.txt).
/// </remarks>
public sealed class LogMonitor
{
    private readonly string _logDir;
    private readonly string _historyPath;
    private IReadOnlyList<LogBlock> _points = Array.Empty<LogBlock>();
    private bool _histDirty;
    private DateTime? _histSaveAt;

    public LogMonitor(string logDir, string historyPath)
    {
        _logDir = logDir;
        _historyPath = historyPath;
    }

    /// <summary>Logs read, by path.</summary>
    public Dictionary<string, LogEntry> Logs { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Transmission state.</summary>
    public NetState State { get; private set; } = NetState.Init;

    /// <summary>Window points (last 30 minutes), sorted by time.</summary>
    public IReadOnlyList<LogBlock> Points => _points;

    /// <summary>Newest block seen in the log, even outside the window: the send alert relies on it.</summary>
    public DateTime? LastSent { get; private set; }

    /// <summary>Latest files named by the logs.</summary>
    public RecentFileSet Recent { get; } = new();

    /// <summary>Slots in historique.txt at the last successful save.</summary>
    public int HistSaved { get; set; }

    /// <summary>Current rate in bits/s (after <see cref="Refresh"/>).</summary>
    public double Rate { get; private set; }

    /// <summary>Highest block rate within the window, bits/s.</summary>
    public double Peak { get; private set; }

    /// <summary>Bytes transmitted within the window.</summary>
    public double Total { get; private set; }

    /// <summary>File currently being transmitted (name of the last block).</summary>
    public string CurrentFile { get; private set; } = "";

    /// <summary>Loads the slots saved by a previous launch. Missing or damaged: starts from the logs.</summary>
    public void LoadHistory() => Logs = HistoryFile.Read(_historyPath, _logDir);

    /// <summary>Saves the slots. Returns whether it was written (the caller retries otherwise).</summary>
    public bool SaveHistory() => HistoryFile.Save(_historyPath, Logs);

    /// <summary>
    /// The read order: by last write time, the name only to break a tie. By name alone, 30.log of last month
    /// would be read after 01.log of this one.
    /// </summary>
    public static IEnumerable<FileInfo> OldestFirst(IEnumerable<FileInfo> files) =>
        files.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the log folder as of <paramref name="now"/> and updates every derived value.</summary>
    /// <returns>Number of characters read (a large value invites a garbage collection).</returns>
    public int Sample(DateTime now)
    {
        var files = new List<LogFileInfo>();
        try
        {
            if (Directory.Exists(_logDir))
            {
                foreach (var fi in OldestFirst(new DirectoryInfo(_logDir).GetFiles()))
                {
                    files.Add(new LogFileInfo(fi.FullName, fi.LastWriteTime, fi.LastWriteTimeUtc, fi.Length));
                }
            }
        }
        catch
        {
            // Unreadable folder: same as no log
        }

        return Sample(files, now);
    }

    /// <summary>
    /// Core of the pass, with the folder listing and the reader supplied by the caller.
    /// </summary>
    /// <param name="files">Files of the log folder, in the order they must be read (oldest write first, see <see cref="OldestFirst"/>).</param>
    /// <param name="now">Current time.</param>
    /// <param name="reader">Reads a log from a position; default: <see cref="LogReader.ReadFrom(string, long)"/>.</param>
    /// <returns>Number of characters read.</returns>
    public int Sample(IReadOnlyList<LogFileInfo> files, DateTime now, Func<string, long, LogChunk?>? reader = null)
    {
        reader ??= LogReader.ReadFrom;
        if (!files.Any(f => f.IsLog))
        {
            State = NetState.NoLog;
            _points = Array.Empty<LogBlock>();
            // No .log left (all zipped, or the rotation has not created the new one yet): the slots already
            // read stay, the tile cannot read them again from the zips. Only those outside the period go.
            var oldest = now.AddSeconds(-Periods.LongestSec).Ticks;
            foreach (var k in Logs.Keys.ToList())
            {
                if (Logs[k].Slots.Keys.Any(t => t >= oldest)) Logs[k].Points = Array.Empty<LogBlock>();
                else Logs.Remove(k);
            }

            return 0;
        }

        // The log was missing and has just reappeared
        if (State == NetState.NoLog) State = NetState.Init;

        var cut = now.AddSeconds(-BzConstants.WindowSec);
        var since = now.AddSeconds(-Periods.LongestSec);
        var keep = files.Where(f => f.IsLog && f.LastWriteLocal >= since).ToList();
        var keepPaths = new HashSet<string>(keep.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var read = 0;

        // Log zipped since: the zip is readable only by SYSTEM and administrators. We keep what we read from
        // it as long as it stays within the period.
        foreach (var k in Logs.Keys.ToList())
        {
            if (keepPaths.Contains(k)) continue;
            if (!Logs[k].Slots.Keys.Any(t => t >= since.Ticks))
            {
                Logs.Remove(k);
                changed = true;
            }
        }

        foreach (var f in keep)
        {
            Logs.TryGetValue(f.FullPath, out var e);
            if (e is not null && e.Stamp == f.LastWriteUtc && e.Length == f.Length) continue;
            // New, restored from historique.txt, or truncated: start again from zero
            if (e is null || e.Stamp is null || f.Length < e.Pos) e = new LogEntry();
            var r = reader(f.FullPath, e.Pos);
            if (r is null) continue; // unreadable for now: next pass
            read += r.Text.Length;

            // The newest of each file: 30.log is read after 01.log
            Recent.Merge(LogParser.GetRecentFiles(r.Text, BzConstants.RecentMax));

            var pts = new List<LogBlock>();
            foreach (var p in e.Points)
            {
                if (p.Time >= cut) pts.Add(p);
            }

            foreach (var p in LogParser.GetLogBlocks(r.Text))
            {
                HistoryCalculator.AddBlocks(e.Slots, new[] { p });
                if (LastSent is null || p.Time > LastSent) LastSent = p.Time;
                if (p.Time >= cut) pts.Add(p);
            }

            e.Points = pts;
            e.Stamp = f.LastWriteUtc;
            e.Length = f.Length;
            e.Pos = r.Pos;
            Logs[f.FullPath] = e;
            changed = true;
        }

        Recent.Trim();

        // All logs: at midnight, the 30-minute window straddles two of them
        if (changed) _points = Logs.Values.SelectMany(e => e.Points).OrderBy(p => p.Time).ToList();

        // Saved on every new slot, and as soon as the content changed (more bytes in a slot already counted),
        // at most every 5 min: at worst, 5 min of a log that got zipped while the tile was closed. The flag
        // stays up until a write succeeds.
        if (changed) _histDirty = true;
        var n = 0;
        foreach (var e in Logs.Values) n += e.Slots.Count;
        var due = _histDirty && (_histSaveAt is null || (now - _histSaveAt.Value).TotalMinutes >= 5);
        if ((n != HistSaved || due) && SaveHistory())
        {
            HistSaved = n;
            _histDirty = false;
            _histSaveAt = now;
        }

        return read;
    }

    /// <summary>
    /// Computes peak, volume, current rate and state as of <paramref name="now"/>. The window slides even
    /// when the log does not move.
    /// </summary>
    public void Refresh(DateTime now)
    {
        if (State == NetState.NoLog)
        {
            Rate = 0.0;
            return;
        }

        var cut = now.AddSeconds(-BzConstants.WindowSec);
        _points = _points.Where(p => p.Time >= cut).ToList();

        // The peak is the highest TOTAL rate (sum of the blocks in flight), not the rate of one block
        Peak = 0.0;
        Total = 0.0;
        foreach (var v in RateCalculator.GetRateSeries(_points))
        {
            if (v > Peak) Peak = v;
        }

        foreach (var p in _points) Total += p.Bytes;

        if (_points.Count > 0)
        {
            var last = _points[^1];
            if (RateCalculator.IsSending(last, now))
            {
                Rate = RateCalculator.GetCurrentRate(_points, now);
                CurrentFile = last.Name;
                State = NetState.Ok;
            }
            else
            {
                Rate = 0.0;
                State = NetState.Idle;
            }
        }
        else
        {
            Rate = 0.0;
            State = NetState.Idle;
        }

        // The peak is the higher of the two measures: never under the rate displayed just above (BAC466.28)
        if (Rate > Peak) Peak = Rate;
    }

    /// <summary>History of the chart period (24 h, 7 d); <c>null</c> for the live 30-minute curve (period 0).</summary>
    public HistoryResult? GetHistory(int period, DateTime now)
    {
        if (period <= 0) return null;
        var per = Periods.All[period];
        return HistoryCalculator.Get(Logs.Values.Select(e => (IReadOnlyDictionary<long, double>)e.Slots).ToList(), per.Sec, per.Step, now);
    }
}

/// <summary>Texts of the rate block of the tile.</summary>
public static class NetworkTexts
{
    /// <summary>Tag next to the rate: "Uploading", "No log" or "No transfer".</summary>
    public static string GetTag(NetState state) => state switch
    {
        NetState.Ok => "Uploading",
        NetState.NoLog => "No log",
        _ => "No transfer",
    };

    /// <summary>Peak and volume line, or the average over a history period.</summary>
    /// <param name="state">Transmission state.</param>
    /// <param name="period">Chart period index.</param>
    /// <param name="hist">History of the period (period &gt; 0).</param>
    /// <param name="peak">Window peak, bits/s.</param>
    /// <param name="total">Window volume, bytes.</param>
    /// <param name="showBits">true = Mbps, false = MB/s.</param>
    public static string GetStatsText(NetState state, int period, HistoryResult? hist, double peak, double total, bool showBits)
    {
        if (state == NetState.NoLog) return "Transmission log not found";
        if (period > 0 && hist is not null)
        {
            return Formatting.FormatSize(hist.Bytes) + " over " + Periods.All[period].Text + "  -  average " +
                   Formatting.FormatRate(hist.Bytes * 8 / hist.Seconds, showBits);
        }

        return "Peak " + Formatting.FormatRate(peak, showBits) + "  -  " + Formatting.FormatSize(total) + " in 30 min";
    }

    /// <summary>Tooltip help, without the current file.</summary>
    public static string GetTipHelp(int period) =>
        "Upload rate over 2 min, all threads combined - " + Periods.All[period].Tip + "\r\n" +
        "Click: switch between Mbps and MB/s" + "\r\n" +
        "Right-click: 30 min, 24 h, 7 d";

    /// <summary>Rate tooltip: the current file first while sending, then the help.</summary>
    public static string GetTip(NetState state, string file, string help) =>
        state == NetState.Ok && file.Length > 0 ? "Now sending: " + file + "\r\n" + help : help;

    /// <summary>Chart tooltip: the hovered block first, then the help; without a hit, the ordinary tooltip.</summary>
    public static string GetChartTip(GraphHit? hit, string help, string ordinaryTip) =>
        hit is null ? ordinaryTip : hit.Text + "\r\n" + help;
}
