using System.Diagnostics;
using BackblazeMonitor.Core;

namespace BackblazeMonitor;

/// <summary>Reads the QoS policy through a hidden PowerShell, off the UI thread.</summary>
internal static class QosReader
{
    private const int TimeoutMs = 20_000;
    private const int KillWaitMs = 3_000; // after a kill: a refused kill must not hold the worker for ever

    /// <summary>
    /// Current throttle in bits/s (0 = no policy), or <c>null</c> when it could not be read (the caller
    /// keeps what it shows). Blocking: call it from a worker thread.
    /// </summary>
    public static double? Read()
    {
        try
        {
            var psi = new ProcessStartInfo(AppPaths.PowerShellExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + EncodedCommand.Encode(QosReadResult.GetScript()),
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var err = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(TimeoutMs))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone
                }

                p.WaitForExit(KillWaitMs);
                return null;
            }

            p.WaitForExit(KillWaitMs);
            _ = err.Wait(1000);
            if (!outTask.Wait(1000)) return null;
            return p.ExitCode == 0 ? QosReadResult.Parse(outTask.Result) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>One ssh round trip of the vacation mode, with the key named in settings.txt.</summary>
internal static class VacationSsh
{
    private const int KillWaitMs = 3_000; // as in QosReader

    /// <summary>
    /// Runs one remote command (<c>status</c>, <c>off</c>, <c>on &lt;hours&gt;</c>) and returns whether ssh exited
    /// with 0, plus its output (stdout and stderr) and its exit code (-1 when it did not run to the end: timeout,
    /// ssh missing). Cut after <see cref="Vacation.TimeoutMs"/>. Blocking: call it from a worker thread.
    /// </summary>
    public static (bool Ok, string Text, int ExitCode) Run(string host, string key, string command)
    {
        try
        {
            var psi = new ProcessStartInfo("ssh.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in Vacation.SshArguments(host, key, command)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return (false, "", -1);
            var err = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(Vacation.TimeoutMs))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone
                }

                p.WaitForExit(KillWaitMs);
                return (false, "timeout", -1);
            }

            p.WaitForExit(KillWaitMs); // the exit code is only valid after this
            var text = ((outTask.Wait(1000) ? outTask.Result : "") + " " + (err.Wait(1000) ? err.Result : "")).Trim();
            return (p.ExitCode == 0, text, p.ExitCode);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, -1);
        }
    }
}

/// <summary>Switches Backblaze to automatic mode through bzcli (no admin rights needed).</summary>
internal static class BzCli
{
    private const int TimeoutMs = 30_000; // as the script: bzcli configure takes a second or two
    private const int KillWaitMs = 3_000;

    /// <summary>Whether bzcli.exe is installed.</summary>
    public static bool Exists() => File.Exists(AppPaths.BzCli);

    /// <summary>
    /// Runs <c>bzcli configure</c> with a temporary configuration file, pumping messages while it runs.
    /// Says whether it exited with 0, failed, or was killed for running too long. Always cleans up the temporary files.
    /// </summary>
    public static BzAutoOutcome EnableAuto()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bzauto_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tmp, BzAutoCommands.Json, System.Text.Encoding.ASCII);
            var psi = new ProcessStartInfo(AppPaths.BzCli)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = BzAutoCommands.GetArguments(tmp),
            };
            using var p = Process.Start(psi);
            if (p is null) return BzAutoOutcome.Failed;
            // Drain both pipes so a chatty tool cannot block on a full buffer
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!UiWait.Until(() => p.HasExited, TimeoutMs))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone
                }

                // Bounded: a child that inherited the pipes would keep a plain WaitForExit() from ever returning,
                // on the UI thread
                p.WaitForExit(KillWaitMs);
                return BzAutoOutcome.TimedOut;
            }

            // The process has exited; the pipes may still be held by a child: the exit code is valid now
            p.WaitForExit(KillWaitMs);
            return p.ExitCode == 0 ? BzAutoOutcome.Done : BzAutoOutcome.Failed;
        }
        catch
        {
            return BzAutoOutcome.Failed;
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // Temp file left behind: harmless
            }
        }
    }
}
