using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BackblazeMonitor.Core;

namespace BackblazeMonitor;

/// <summary>Locations of the files kept next to the program.</summary>
internal static class AppPaths
{
    /// <summary>Folder of the executable.</summary>
    public static string Dir { get; } = ComputeDir();

    public static string Settings => Path.Combine(Dir, "settings.txt");

    public static string History => Path.Combine(Dir, "historique.txt");

    public static string Volumes => Path.Combine(Dir, "volumes.txt");

    /// <summary>
    /// Request file read by the SYSTEM agent. Not next to the program: that folder is writable by the user
    /// (settings, history), and the agent runs what the file asks for. This one is created with an ACL by the
    /// agent's installation (see AgentCommands.GetSetup).
    /// </summary>
    public static string AgentFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackblazeMonitor", "agent", "requete.txt");

    public static string ErrorLog => Path.Combine(Dir, "error.log");

    /// <summary>
    /// bzcli.exe: the 64-bit install (Program Files) or the 32-bit one (Program Files (x86), where older clients
    /// live), whichever exists; the 64-bit path when neither does.
    /// </summary>
    public static string BzCli => FindBzCli();

    private static string FindBzCli()
    {
        string Candidate(Environment.SpecialFolder f) => Path.Combine(Environment.GetFolderPath(f), "Backblaze", "bzcli.exe");
        var x64 = Candidate(Environment.SpecialFolder.ProgramFiles);
        var x86 = Candidate(Environment.SpecialFolder.ProgramFilesX86);
        return !File.Exists(x64) && File.Exists(x86) ? x86 : x64;
    }

    /// <summary>Windows PowerShell home folder.</summary>
    public static string PsHome => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0");

    public static string PowerShellExe => Path.Combine(PsHome, "powershell.exe");

    private static string ComputeDir()
    {
        var exe = Environment.ProcessPath;
        var dir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
    }
}

/// <summary>Text log of unexpected errors, next to the program (at most about 1 MB, then error.log.old).</summary>
internal static class ErrorLog
{
    private static readonly object Gate = new();

    /// <summary>Appends an entry. Never throws: falls back to the temp folder, then gives up silently.</summary>
    public static void Write(string where, Exception ex)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {AppInfo.Title}\r\n{ex}\r\n\r\n";
        lock (Gate)
        {
            foreach (var path in new[] { AppPaths.ErrorLog, Path.Combine(Path.GetTempPath(), "BackblazeMonitor-error.log") })
            {
                try
                {
                    FileRotation.RotateIfLarger(path);
                    File.AppendAllText(path, text);
                    return;
                }
                catch
                {
                    // Try the next location
                }
            }
        }
    }
}

/// <summary>Waiting that keeps the window alive.</summary>
internal static class UiWait
{
    /// <summary>Pumps messages for <paramref name="ms"/> milliseconds (the window repaints, clicks are blocked by the caller).</summary>
    public static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
        while (sw.ElapsedMilliseconds < ms);
    }

    /// <summary>Pumps until <paramref name="done"/> is true or <paramref name="timeoutMs"/> passes. Returns whether it finished.</summary>
    public static bool Until(Func<bool> done, int timeoutMs, int stepMs = 50)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            Pump(stepMs);
        }

        return true;
    }
}

/// <summary>Win32 entry points used by the tile.</summary>
internal static class Native
{
    public const uint ScManagerConnect = 0x0001;
    public const uint ServiceQueryConfig = 0x0001;
    public const uint ServiceQueryStatus = 0x0004;
    public const int ErrorServiceDoesNotExist = 1060;
    public const int ErrorInsufficientBuffer = 122;
    public const int ScStatusProcessInfo = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatusProcess
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
        public int ProcessId;
        public int ServiceFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, IntPtr buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "QueryServiceConfigW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, int bufferSize, out int bytesNeeded);
}

/// <summary>What one query of the Backblaze service found.</summary>
/// <param name="Exists">false when the service does not exist.</param>
/// <param name="Status">Raw state name ("Running", "Stopped", "StartPending"...).</param>
/// <param name="StartType">Raw start type ("Automatic", "Manual", "Disabled"...).</param>
/// <param name="ProcessId">Process id, 0 when not running.</param>
/// <param name="WorkingSet">Working set of the process, <c>null</c> when unavailable.</param>
/// <param name="PidFailed">The process lookup failed unexpectedly.</param>
internal sealed record ServiceSnapshot(bool Exists, string Status, string StartType, int ProcessId, long? WorkingSet, bool PidFailed)
{
    public static readonly ServiceSnapshot Missing = new(false, "", "", 0, null, false);

    /// <summary>The Core status text of this snapshot (<c>null</c> status = not found).</summary>
    public string? StatusOrNull => Exists ? Status : null;
}

/// <summary>
/// Reads the state of a Windows service straight from the service manager (no extra package): state, start
/// type and process id in one call each. Not named ServiceController, which exists in System.ServiceProcess.
/// </summary>
internal static class ServiceMonitor
{
    /// <summary>Queries the service. Throws on an unexpected failure (the caller shows a harmless status).</summary>
    public static ServiceSnapshot Query(string name)
    {
        var scm = Native.OpenSCManager(null, null, Native.ScManagerConnect);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var svc = Native.OpenService(scm, name, Native.ServiceQueryConfig | Native.ServiceQueryStatus);
            if (svc == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == Native.ErrorServiceDoesNotExist) return ServiceSnapshot.Missing;
                throw new Win32Exception(err);
            }

            try
            {
                var st = ReadStatus(svc);
                var startType = ReadStartType(svc);
                long? ws = null;
                var pidFailed = false;
                if (st.ProcessId > 0)
                {
                    try
                    {
                        using var p = Process.GetProcessById(st.ProcessId);
                        ws = p.WorkingSet64;
                    }
                    catch (ArgumentException)
                    {
                        // Process gone between the two calls: no size, not an error
                    }
                    catch (Win32Exception)
                    {
                        // Access denied on the process: keep the PID, skip the size
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited while reading
                    }
                    catch
                    {
                        pidFailed = true;
                    }
                }

                return new ServiceSnapshot(true, StateName(st.CurrentState), startType, st.ProcessId, ws, pidFailed);
            }
            finally
            {
                Native.CloseServiceHandle(svc);
            }
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }
    }

    private static Native.ServiceStatusProcess ReadStatus(IntPtr svc)
    {
        var size = Marshal.SizeOf<Native.ServiceStatusProcess>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.QueryServiceStatusEx(svc, Native.ScStatusProcessInfo, buf, size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Marshal.PtrToStructure<Native.ServiceStatusProcess>(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string ReadStartType(IntPtr svc)
    {
        Native.QueryServiceConfig(svc, IntPtr.Zero, 0, out var needed);
        // The size query must fail with "buffer too small"; any other error is not a question of size
        if (Marshal.GetLastWin32Error() != Native.ErrorInsufficientBuffer || needed <= 0 || needed > 64 * 1024) return "Unknown";
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!Native.QueryServiceConfig(svc, buf, needed, out _)) return "Unknown";
            // QUERY_SERVICE_CONFIG: dwServiceType, then dwStartType
            return Marshal.ReadInt32(buf, 4) switch
            {
                0 => "Boot",
                1 => "System",
                2 => "Automatic",
                3 => "Manual",
                4 => "Disabled",
                _ => "Unknown",
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string StateName(int state) => state switch
    {
        1 => "Stopped",
        2 => "StartPending",
        3 => "StopPending",
        4 => "Running",
        5 => "ContinuePending",
        6 => "PausePending",
        7 => "Paused",
        _ => "Unknown",
    };
}
