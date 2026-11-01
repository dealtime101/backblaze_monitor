using System.Globalization;

namespace BackblazeMonitor.Core;

/// <summary>
/// The latest files named by the logs: path to time (paths are compared case-insensitively, like Windows).
/// Kept to at most <see cref="BzConstants.RecentMax"/> entries, the most recent ones.
/// </summary>
public sealed class RecentFileSet
{
    private readonly Dictionary<string, DateTime> _items = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of files kept.</summary>
    public int Count => _items.Count;

    /// <summary>Time recorded for a path, or <c>null</c>.</summary>
    public DateTime? this[string path] => _items.TryGetValue(path, out var t) ? t : null;

    /// <summary>Records the files of a log chunk: the newest time of each file wins.</summary>
    public void Merge(IEnumerable<RecentFile> files)
    {
        foreach (var x in files)
        {
            if (!_items.TryGetValue(x.Path, out var old) || x.Time > old) _items[x.Path] = x.Time;
        }
    }

    /// <summary>Beyond <paramref name="max"/> entries, the oldest drop out.</summary>
    public void Trim(int max = BzConstants.RecentMax)
    {
        if (_items.Count <= max) return;
        foreach (var kv in _items.OrderByDescending(kv => kv.Value).Skip(max).ToList()) _items.Remove(kv.Key);
    }

    /// <summary>
    /// Sets or overwrites the time of one path, without comparing with the time it already has (unlike
    /// <see cref="Merge"/>, where the newest wins). It does not empty the set.
    /// </summary>
    public void Set(string path, DateTime time) => _items[path] = time;

    /// <summary>Removes a path.</summary>
    public bool Remove(string path) => _items.Remove(path);

    /// <summary>Rows from the most recent to the oldest.</summary>
    public IReadOnlyList<RecentFile> Rows() =>
        _items.OrderByDescending(kv => kv.Value).Select(kv => new RecentFile(kv.Value, kv.Key)).ToList();

    /// <summary>
    /// Content of the displayed list as a single string, to rebuild the list only when it changes.
    /// </summary>
    public string Key() => string.Join(
        "\n",
        Rows().Select(r => r.Path + "|" + r.Time.ToString("o", CultureInfo.InvariantCulture)));

    /// <summary>File name of a path: what follows the last backslash.</summary>
    public static string FileName(string path) => path.Substring(path.LastIndexOf('\\') + 1);
}
