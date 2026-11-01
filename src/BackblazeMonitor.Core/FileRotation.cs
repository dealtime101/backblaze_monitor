namespace BackblazeMonitor.Core;

/// <summary>Keeps an append-only log from growing for ever.</summary>
public static class FileRotation
{
    /// <summary>1 MB: about a thousand stack traces.</summary>
    public const long DefaultMaxBytes = 1024 * 1024;

    /// <summary>
    /// When the file is larger than <paramref name="maxBytes"/>, moves it to "path.old" (replacing the previous
    /// one), so the log never holds more than two files of that size. Returns whether it rotated.
    /// </summary>
    public static bool RotateIfLarger(string path, long maxBytes = DefaultMaxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= maxBytes) return false;
        File.Move(path, path + ".old", overwrite: true);
        return true;
    }
}
