using System.Runtime.InteropServices;
using BackblazeMonitor.Core;
using Microsoft.Win32;

namespace BackblazeMonitor;

/// <summary>
/// Launch at sign-in: a shortcut to this program in the user's Startup folder. Task Manager disables it
/// without deleting it, through a StartupApproved registry value, which this class honours.
/// </summary>
internal static class StartupShortcut
{
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string FileName = "Backblaze Monitor.lnk";

    private static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), FileName);

    // The program's own exe; null when run through the dotnet host, whose path must never become a shortcut target
    private static string? ExePath => StartupApproved.IsDotnetHost(Environment.ProcessPath) ? null : Environment.ProcessPath;

    /// <summary>On when the shortcut exists and is not disabled.</summary>
    public static bool IsEnabled()
    {
        try
        {
            if (!File.Exists(LinkPath)) return false;
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return StartupApproved.IsEnabled(true, key?.GetValue(FileName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Switches it: on removes the shortcut; off writes it (target = this program, working folder = its
    /// folder) and clears a disable flag, which outlives the shortcut and would otherwise make it be reborn
    /// disabled. Returns false when it could not be done.
    /// </summary>
    public static bool Toggle()
    {
        try
        {
            if (IsEnabled())
            {
                File.Delete(LinkPath);
                return !File.Exists(LinkPath);
            }

            WriteLink();
            ClearDisabledFlag();
            // What it is now, not what was attempted: a flag that could not be cleared leaves it disabled
            return IsEnabled();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("startup shortcut", ex);
            return false;
        }
    }

    /// <summary>
    /// A shortcut left by an earlier version (script launcher) or by another copy of this program points
    /// elsewhere: repoint it here, keeping its enabled/disabled state.
    /// </summary>
    public static void RepairIfStale()
    {
        try
        {
            var exe = ExePath;
            if (exe is null || !File.Exists(LinkPath)) return;
            var target = ReadTarget();
            // Only a target that was read and differs: an unreadable one (no WScript.Shell) is not "stale", and
            // rewriting would fail again at every launch
            if (target is not null && !string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)) WriteLink();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("startup shortcut repair", ex);
        }
    }

    private static void ClearDisabledFlag()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            key?.DeleteValue(FileName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            // No access to the flag: the shortcut stays disabled, and the caller sees it through IsEnabled
            ErrorLog.Write("startup shortcut flag", ex);
        }
    }

    private static string? ReadTarget()
    {
        object? shell = null;
        object? link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            shell = Activator.CreateInstance(type);
            dynamic sh = shell!;
            link = sh.CreateShortcut(LinkPath);
            dynamic lnk = link!;
            return (string?)lnk.TargetPath;
        }
        finally
        {
            ReleaseCom(link);
            ReleaseCom(shell);
        }
    }

    private static void WriteLink()
    {
        var exe = ExePath ?? throw new InvalidOperationException("Program path unknown");
        object? shell = null;
        object? link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
            shell = Activator.CreateInstance(type);
            dynamic sh = shell!;
            link = sh.CreateShortcut(LinkPath);
            dynamic lnk = link!;
            lnk.TargetPath = exe;
            lnk.Arguments = "";
            lnk.WorkingDirectory = Path.GetDirectoryName(exe) ?? AppPaths.Dir;
            lnk.IconLocation = exe + ",0";
            lnk.Description = AppInfo.Name;
            lnk.Save();
        }
        finally
        {
            ReleaseCom(link);
            ReleaseCom(shell);
        }
    }

    private static void ReleaseCom(object? com)
    {
        try
        {
            if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
        }
        catch
        {
            // Nothing to do
        }
    }
}
