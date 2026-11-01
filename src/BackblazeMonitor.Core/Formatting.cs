namespace BackblazeMonitor.Core;

/// <summary>Culture-invariant formatting of rates, sizes, finish times and tray text.</summary>
public static class Formatting
{
    private const double KB = 1024.0;
    private const double MB = 1024.0 * 1024;
    private const double GB = 1024.0 * 1024 * 1024;
    private const double TB = 1024.0 * 1024 * 1024 * 1024;

    /// <summary>Rate: bits as "Mbps"/"kbps", bytes as "MB/s"/"KB/s".</summary>
    /// <param name="bitsPerSec">Rate in bits per second.</param>
    /// <param name="bits">true = Mbps (megabits), false = MB/s (megabytes).</param>
    public static string FormatRate(double bitsPerSec, bool bits)
    {
        var ci = BzConstants.Invariant;
        if (bits)
        {
            // Tiers change where the figure would round up to the next unit ("1,000 kbps" is "1.0 Mbps")
            if (bitsPerSec >= 1_000_000 - 500) return (bitsPerSec / 1_000_000).ToString("N1", ci) + " Mbps";
            if (bitsPerSec >= 1000) return (bitsPerSec / 1000).ToString("N0", ci) + " kbps";
            return bitsPerSec > 0 ? "<1 kbps" : "0 kbps"; // slow but sending is not 0
        }

        var bps = bitsPerSec / 8;
        if (bps >= MB - KB / 2) return (bps / MB).ToString("N1", ci) + " MB/s";
        if (bps >= KB) return (bps / KB).ToString("N0", ci) + " KB/s";
        return bps > 0 ? "<1 KB/s" : "0 KB/s";
    }

    /// <summary>Size in binary units: TB, GB (2 decimals), MB (1), KB (0); under 1 KB the bytes, so a remainder is never shown as 0.</summary>
    public static string FormatSize(double b)
    {
        var ci = BzConstants.Invariant;
        // A tier starts where the figure of the one below would round up to 1,024 of its unit
        if (b >= TB - GB * 0.005) return (b / TB).ToString("N2", ci) + " TB";
        if (b >= GB - MB * 0.05) return (b / GB).ToString("N2", ci) + " GB";
        if (b >= MB - KB / 2) return (b / MB).ToString("N1", ci) + " MB";
        if (b >= KB) return (b / KB).ToString("N0", ci) + " KB";
        if (b > 0) return Math.Ceiling(b).ToString("N0", ci) + " B";
        return "0 KB";
    }

    /// <summary>
    /// Finish time. On a media disk the finish is counted in years, hence the tiers; past 100 years the
    /// figure means nothing.
    /// </summary>
    public static string FormatEta(double sec, DateTime now)
    {
        var ci = BzConstants.Invariant;
        if (sec < 86400)
        {
            var end = now.AddSeconds(sec);
            var hhmm = end.ToString("HH':'mm", ci);
            return end.Date == now.Date ? "done " + hhmm : "done tomorrow " + hhmm;
        }

        var days = sec / 86400;
        if (days < 100) return "done ~" + days.ToString("N0", ci) + " d";
        if (days < 36525) return "done ~" + (days / 365.25).ToString("N1", ci) + " yr";
        return "done > 100 yr";
    }

    /// <summary>
    /// Remaining-to-back-up line. <paramref name="bytesPerSec"/> is the average over the last 30 minutes,
    /// gaps included. Unknown bytes (<c>null</c>) is never shown as 0.
    /// </summary>
    public static string GetRemainingText(long? bytes, long? files, double bytesPerSec, DateTime now)
    {
        if (bytes is null) return "Remaining: unknown (Backblaze report unreadable)";
        if (bytes == 0) return "Backup up to date";
        var t = "Remaining " + FormatSize(bytes.Value);
        if (files is not null) t += " - " + files.Value.ToString("N0", BzConstants.Invariant) + " files";
        if (bytesPerSec > 0) t += " - " + FormatEta(bytes.Value / bytesPerSec, now);
        return t;
    }

    /// <summary>Tray tooltip: "Backblaze: status\nspeed", capped at TrayTextMax characters (NotifyIcon throws beyond).</summary>
    public static string GetTrayText(string status, string speed, string? vacation = null)
    {
        var t = $"Backblaze: {status}\n{speed}";
        if (!string.IsNullOrEmpty(vacation)) t += $"\nVacation: {vacation}";
        return CapTrayText(t);
    }

    /// <summary>The text cut to what NotifyIcon.Text accepts (it throws beyond).</summary>
    public static string CapTrayText(string t) =>
        t.Length > BzConstants.TrayTextMax ? t.Substring(0, BzConstants.TrayTextMax) : t;

    /// <summary>"HH:mm" for today, "ddd d, HH:mm" (English) for another day: recent-files list.</summary>
    public static string FormatWhen(DateTime value, DateTime today)
    {
        if (value.Date != today.Date) return value.ToString("ddd d, HH':'mm", BzConstants.English);
        return value.ToString("HH':'mm", BzConstants.Invariant);
    }
}
