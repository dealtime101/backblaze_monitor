using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BackblazeMonitor.Core;

/// <summary>Remaining bytes and files from bzstat_remainingbackup.xml.</summary>
/// <param name="Bytes">Remaining bytes (0 is a real 0: backup up to date).</param>
/// <param name="Files">Remaining files, or <c>null</c> when the attribute is absent.</param>
public sealed record RemainingReport(long Bytes, long? Files);

/// <summary>Progress of one disk.</summary>
/// <param name="Drive">Upper-case drive letter.</param>
/// <param name="Selected">Bytes selected for backup.</param>
/// <param name="Remaining">Bytes remaining.</param>
public sealed record VolumeInfo(string Drive, long Selected, long Remaining);

/// <summary>A per-disk sample: finished bytes at a time.</summary>
/// <param name="T">Unix time in seconds.</param>
/// <param name="D">Drive letter.</param>
/// <param name="C">Finished bytes: selected minus remaining.</param>
public sealed record VolumeSample(long T, string D, long C);

/// <summary>Reading of the Backblaze report files (text in, values out).</summary>
public static class BackupReports
{
    private static readonly Regex RemainBytes = new("remainingnumbytesforbackup=\"([0-9]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RemainFiles = new("remainingnumfilesforbackup=\"([0-9]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VolumeTag = new(@"<bzvolume\b[^>]*>", RegexOptions.CultureInvariant);
    private static readonly Regex Digits = new("^[0-9]+$", RegexOptions.CultureInvariant);

    private static bool TryLong(string s, out long v) =>
        long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);

    /// <summary>
    /// Reads the remaining bytes and files. No number yields <c>null</c>, never 0: a truncated report read
    /// as "0 remaining" would display an up-to-date backup. A number too large for 64 bits is unknown too.
    /// </summary>
    public static RemainingReport? ReadRemaining(string? raw)
    {
        var m = RemainBytes.Match(raw ?? "");
        if (!m.Success || !TryLong(m.Groups[1].Value, out var bytes)) return null;
        long? files = null;
        var f = RemainFiles.Match(raw ?? "");
        if (f.Success && TryLong(f.Groups[1].Value, out var n)) files = n;
        return new RemainingReport(bytes, files);
    }

    /// <summary>Value of attribute <paramref name="name"/> in an XML tag (case-insensitive), or <c>null</c>.</summary>
    public static string? GetAttr(string tag, string name)
    {
        var m = Regex.Match(tag, @"\b" + Regex.Escape(name) + "=\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Progress per disk. Backblaze writes, per disk, the selected and remaining bytes keyed by
    /// bzVolumeGuid; the drive letter comes from bzinfo.xml (which also holds the account e-mail: only
    /// computed values leave this function). A disk with no letter or missing one of the two numbers is
    /// dropped, never read as 0. Sorted by drive letter.
    /// </summary>
    public static IReadOnlyList<VolumeInfo> ReadVolumes(string? total, string? remain, string? info)
    {
        var letter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VolumeTag.Matches(info ?? ""))
        {
            var g = GetAttr(m.Value, "bzVolumeGuid");
            var p = GetAttr(m.Value, "mountPointPath");
            // Only a volume with a drive letter ("E:\\"): a folder mount or a \\?\ path has no letter, and a sample
            // saved under it would be rejected by VolumeSamples.Read at the next launch
            if (!string.IsNullOrEmpty(g) && p is { Length: >= 2 } && char.IsAsciiLetter(p[0]) && p[1] == ':') letter[g] = p.Substring(0, 1).ToUpperInvariant();
        }

        var left = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VolumeTag.Matches(remain ?? ""))
        {
            var g = GetAttr(m.Value, "bzVolumeGuid");
            var n = GetAttr(m.Value, "pervol_remaining_files_numbytes");
            if (!string.IsNullOrEmpty(g) && n is not null && Digits.IsMatch(n) && TryLong(n, out var v)) left[g] = v;
        }

        var vols = new List<VolumeInfo>();
        foreach (Match m in VolumeTag.Matches(total ?? ""))
        {
            var g = GetAttr(m.Value, "bzVolumeGuid");
            var s = GetAttr(m.Value, "pervol_sel_for_backup_numbytes");
            if (!string.IsNullOrEmpty(g) && letter.TryGetValue(g, out var drive) && s is not null && Digits.IsMatch(s)
                && TryLong(s, out var sel) && left.TryGetValue(g, out var rem))
            {
                vols.Add(new VolumeInfo(drive, sel, rem));
            }
        }

        return vols.OrderBy(v => v.Drive, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Per-disk samples: pruning, pace, text and file format.</summary>
public static class VolumeSamples
{
    /// <summary>
    /// Adds a sample of FINISHED bytes (selected minus remaining: what Backblaze has sent or attached to a
    /// copy already there) per disk. At most one sample per disk per hour; anything older than 8 days is
    /// dropped.
    /// </summary>
    public static IReadOnlyList<VolumeSample> Add(IEnumerable<VolumeSample> samples, IEnumerable<VolumeInfo> vols, long nowSec)
    {
        var output = new List<VolumeSample>();
        foreach (var s in samples)
        {
            if (s.T >= nowSec - BzConstants.VolKeepSec) output.Add(s);
        }

        foreach (var v in vols)
        {
            long? last = null;
            foreach (var s in output)
            {
                if (string.Equals(s.D, v.Drive, StringComparison.OrdinalIgnoreCase) && (last is null || s.T > last)) last = s.T;
            }

            if (last is null || nowSec - last >= BzConstants.VolStepSec)
            {
                output.Add(new VolumeSample(nowSec, v.Drive, v.Selected - v.Remaining));
            }
        }

        return output;
    }

    /// <summary>
    /// Finished bytes per second over 7 days: only increases between two samples are summed, because a
    /// decrease comes from a shrinking selection (disk or folder excluded), not from the backup going
    /// backwards. Less than one hour of samples is unknown (<c>null</c>), not 0.
    /// </summary>
    public static double? GetRate(IEnumerable<VolumeSample> samples, string drive, long nowSec)
    {
        var s = samples
            .Where(x => string.Equals(x.D, drive, StringComparison.OrdinalIgnoreCase) && x.T >= nowSec - BzConstants.VolRateSec)
            .OrderBy(x => x.T)
            .ToList();
        if (s.Count < 2) return null;
        var span = s[^1].T - s[0].T;
        if (span < 3600) return null;
        double up = 0;
        for (var i = 1; i < s.Count; i++)
        {
            var d = s[i].C - s[i - 1].C;
            if (d > 0) up += d;
        }

        return up / span;
    }

    /// <summary>Text of the "Progress per disk" window (CR LF separated).</summary>
    public static string GetText(IReadOnlyList<VolumeInfo> vols, IReadOnlyList<VolumeSample> samples, DateTime now)
    {
        if (vols.Count == 0) return "Backblaze report unreadable: no disk";
        var ci = BzConstants.Invariant;
        var sec = new DateTimeOffset(now).ToUnixTimeSeconds();
        var lines = new List<string> { $"{"Drive",-10}{"Sent",4}{"Left",12}{"Rate 7 d",14}   ETA" };
        foreach (var v in vols)
        {
            if (v.Selected <= 0)
            {
                lines.Add($"{v.Drive}:       nothing selected");
                continue;
            }

            var pct = 100.0 * (v.Selected - v.Remaining) / v.Selected;
            var rate = GetRate(samples, v.Drive, sec);
            var rt = "—";
            var fin = "—";
            if (rate is not null) rt = Formatting.FormatSize(rate.Value * 86400) + "/d";
            if (v.Remaining == 0) fin = "up to date";
            else if (rate > 0) fin = Formatting.FormatEta(v.Remaining / rate.Value, now);
            lines.Add($"{v.Drive}:   {pct.ToString("N1", ci),7} %  {Formatting.FormatSize(v.Remaining),10}  {rt,12}   {fin}");
        }

        lines.Add("");
        lines.Add("Sent = uploaded, or matched to a copy already stored (0 bytes transferred).");
        lines.Add("The ETA applies to what is selected now, at the pace of the last 7 days.");
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Reads volumes.txt ("T TAB letter TAB finished bytes"). Missing file or damaged lines are ignored.
    /// </summary>
    public static IReadOnlyList<VolumeSample> Read(string path) => TryRead(path) ?? new List<VolumeSample>();

    /// <summary>
    /// Like <see cref="Read"/>, but <c>null</c> when the file exists and cannot be read (locked, no access):
    /// that is not "no history", and writing over it would lose it. A missing file is an empty list.
    /// </summary>
    public static IReadOnlyList<VolumeSample>? TryRead(string path)
    {
        var list = new List<VolumeSample>();
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var p = line.Split('\t');
                if (p.Length == 3
                    && Regex.IsMatch(p[0], "^[0-9]+$") && Regex.IsMatch(p[1], "^[A-Za-z]$") && Regex.IsMatch(p[2], "^-?[0-9]+$")
                    && long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var t)
                    && long.TryParse(p[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var c))
                {
                    list.Add(new VolumeSample(t, p[1], c));
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new List<VolumeSample>();
        }
        catch (Exception ex)
        {
            ReadFailures.Report(path, ex);
            return null;
        }

        return list;
    }

    /// <summary>Writes volumes.txt. Returns whether it was written: a failure is retried on the next tick.</summary>
    public static bool Save(string path, IEnumerable<VolumeSample> samples)
    {
        try
        {
            AtomicFile.WriteAllLines(path, samples.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.T}\t{s.D}\t{s.C}")));
            return true;
        }
        catch (Exception ex)
        {
            ReadFailures.Report(path, ex, reading: false);
            return false;
        }
    }
}

/// <summary>
/// Samples of per-disk progress with their file: loaded at launch, updated when Backblaze rewrites its
/// report (about once an hour).
/// </summary>
public sealed class VolumeSampleStore
{
    private readonly string _path;

    public VolumeSampleStore(string path) => _path = path;

    /// <summary>Current samples.</summary>
    public IReadOnlyList<VolumeSample> Samples { get; private set; } = Array.Empty<VolumeSample>();

    // The file exists but could not be read: nothing is written over it until a read succeeds.
    private bool _loadFailed;

    /// <summary>Loads the samples from disk (none if missing).</summary>
    public void Load() => Reload();

    // On success, what was recorded in memory in the meantime is merged into what the file holds.
    private void Reload()
    {
        var onDisk = VolumeSamples.TryRead(_path);
        if (onDisk is null)
        {
            _loadFailed = true;
            return;
        }

        _loadFailed = false;
        Samples = onDisk.Union(Samples).OrderBy(s => s.T).ToList();
    }

    /// <summary>
    /// Records a sample per disk (no more than one per disk per hour) and saves when the samples changed. No
    /// disk: nothing happens. A failed save is retried at the next record.
    /// </summary>
    public VolumeRecord Record(IReadOnlyList<VolumeInfo> vols, long nowSec)
    {
        if (vols.Count == 0) return VolumeRecord.Unchanged;
        if (_loadFailed) Reload();
        var next = VolumeSamples.Add(Samples, vols, nowSec);
        var changed = !next.SequenceEqual(Samples);
        Samples = next;
        if (_loadFailed)
        {
            Pending = true;
            return VolumeRecord.Failed;
        }

        // Within the hour nothing is added: no write (unless one is still waiting)
        if (!changed && !Pending) return VolumeRecord.Unchanged;

        // The flag stays up until a write succeeds: Record only runs when the report changes (about hourly),
        // so without it a failed write would never be retried (BAC466.35)
        Pending = !VolumeSamples.Save(_path, Samples);
        return Pending ? VolumeRecord.Failed : VolumeRecord.Written;
    }

    /// <summary>A save failed and has not been retried successfully yet.</summary>
    public bool Pending { get; private set; }

    /// <summary>Retries the save in waiting, if any; called every tick, whatever the report does.</summary>
    public void RetryPending()
    {
        if (!Pending) return;
        if (_loadFailed) Reload();
        if (!_loadFailed) Pending = !VolumeSamples.Save(_path, Samples);
    }
}

/// <summary>Outcome of a <see cref="VolumeSampleStore.Record"/> call.</summary>
public enum VolumeRecord
{
    /// <summary>No sample added (none due, or no disk): the file was not touched.</summary>
    Unchanged,

    /// <summary>The samples changed and the file was written.</summary>
    Written,

    /// <summary>The file could not be written (or read first): retried later.</summary>
    Failed,
}

/// <summary>Outcome of a <see cref="RemainingTracker.Refresh"/> pass.</summary>
public enum RemainingRefresh
{
    /// <summary>Report file missing: remaining unknown.</summary>
    Missing,

    /// <summary>Same timestamp as the last successful read: nothing to do.</summary>
    Unchanged,

    /// <summary>Read and parsed: a volume sample should be taken now.</summary>
    Updated,

    /// <summary>Unreadable or truncated: remaining unknown, retried at the next pass.</summary>
    Unreadable,
}

/// <summary>
/// Remaining bytes and files, cached by the report's timestamp. The timestamp is remembered only after a
/// successful read, otherwise a failure would never be retried.
/// </summary>
public sealed class RemainingTracker
{
    private DateTime? _stamp;

    /// <summary>Remaining bytes; <c>null</c> = unknown, never 0.</summary>
    public long? Bytes { get; private set; }

    /// <summary>Remaining files.</summary>
    public long? Files { get; private set; }

    /// <param name="stampUtc">Write time of the report, or <c>null</c> when the file does not exist.</param>
    /// <param name="readText">Reads the report text (only called when the timestamp changed).</param>
    public RemainingRefresh Refresh(DateTime? stampUtc, Func<string?> readText)
    {
        if (stampUtc is null)
        {
            Bytes = null;
            Files = null;
            _stamp = null;
            return RemainingRefresh.Missing;
        }

        if (_stamp == stampUtc) return RemainingRefresh.Unchanged;
        var r = BackupReports.ReadRemaining(readText());
        if (r is null)
        {
            Bytes = null;
            Files = null;
            return RemainingRefresh.Unreadable;
        }

        Bytes = r.Bytes;
        Files = r.Files;
        _stamp = stampUtc;
        return RemainingRefresh.Updated;
    }
}
