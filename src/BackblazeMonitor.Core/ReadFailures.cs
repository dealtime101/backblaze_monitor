namespace BackblazeMonitor.Core;

/// <summary>
/// Where the readers of Backblaze's files report a failure that is not a plain "file missing": a locked file,
/// access denied, an I/O error, a file too big. The reader still answers <c>null</c> (the caller retries at the
/// next pass); this is how the cause reaches whoever keeps the error log. Raised on the caller's thread.
/// </summary>
public static class ReadFailures
{
    public static event Action<string, Exception>? Reported;

    /// <summary>
    /// The failures worth a line in the log. A missing file or folder is routine (a log rotates away between
    /// the listing and the read), anything else is not.
    /// </summary>
    internal static bool IsReportable(Exception ex) =>
        ex is not (FileNotFoundException or DirectoryNotFoundException);

    /// <param name="reading">false for a write: there, a missing folder is a failure worth a line too.</param>
    internal static void Report(string path, Exception ex, bool reading = true)
    {
        if (reading && !IsReportable(ex)) return;
        try
        {
            Reported?.Invoke(path, ex);
        }
        catch
        {
            // A broken listener must not turn a retry into a crash
        }
    }
}
