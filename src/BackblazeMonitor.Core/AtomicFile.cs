using System.Text;

namespace BackblazeMonitor.Core;

/// <summary>
/// Writes a file so that a full disk or a power cut never leaves it cut: the text goes to a neighbouring
/// .tmp, then replaces the target in one step. The previous copy stays whole until the new one is complete.
/// </summary>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/>. Throws on failure, the target untouched.</summary>
    public static void WriteAllText(string path, string text, Encoding encoding)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, encoding);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // Nothing more to clean
            }

            throw;
        }
    }

    /// <summary>One line per item, each ended by the platform line break, like <see cref="File.WriteAllLines(string, IEnumerable{string})"/>.</summary>
    public static void WriteAllLines(string path, IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines) sb.Append(line).Append(Environment.NewLine);
        WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
