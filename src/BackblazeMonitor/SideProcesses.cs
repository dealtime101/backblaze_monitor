using System.Diagnostics;
using BackblazeMonitor.Core;

namespace BackblazeMonitor;

/// <summary>
/// The one way the tile runs a side process and waits for it: start, read both pipes, wait up to a limit, kill
/// on timeout, never wait for ever. Blocking: call it from a worker thread.
/// </summary>
internal static class ProcessRun
{
    /// <summary>After a kill, or after the exit: how long to wait for the end and for the pipes. A refused kill, or a child that holds the pipes, must not hold the caller.</summary>
    public const int KillWaitMs = 3_000;

    /// <param name="TimedOut">The limit passed and the process was killed.</param>
    /// <param name="ExitCode">Exit code; -1 when it timed out.</param>
    public sealed record Result(bool TimedOut, int ExitCode, string Output, string Error);

    /// <summary>Starts the process (stdout and stderr must be redirected in psi) and waits at most timeoutMs. Throws if it cannot start.</summary>
    public static Result Run(ProcessStartInfo psi, int timeoutMs)
    {
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("the process did not start");
        var err = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            Kill(p);
            return new Result(true, -1, "", "");
        }

        p.WaitForExit(KillWaitMs); // the exit code is only valid after this
        var output = outTask.Wait(1000) ? outTask.Result : "";
        var error = err.Wait(1000) ? err.Result : "";
        return new Result(false, p.ExitCode, output, error);
    }

    /// <summary>Kills the process tree and waits a little: already gone, or a refused kill, both just return.</summary>
    public static void Kill(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or refused: the wait is bounded either way
        }

        p.WaitForExit(KillWaitMs);
    }
}

/// <summary>Reads the QoS policy through a hidden PowerShell, off the UI thread.</summary>
internal static class QosReader
{
    private const int TimeoutMs = 20_000;

    /// <summary>
    /// Current throttle in bits/s (0 = no policy). Null when the output could not be understood (the
    /// caller keeps what it shows). A failure to run it (no PowerShell, a script error, a timeout) throws, with
    /// the exit code and PowerShell error text in the message: the caller logs it once and keeps what it
    /// shows. Blocking: call it from a worker thread.
    /// </summary>
    public static double? Read()
    {
        var psi = new ProcessStartInfo(AppPaths.PowerShellExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + EncodedCommand.Encode(QosReadResult.GetScript()),
        };
        var r = ProcessRun.Run(psi, TimeoutMs);
        if (r.TimedOut) throw new TimeoutException($"QoS read: no answer after {TimeoutMs / 1000} s");
        if (r.ExitCode != 0) throw new InvalidOperationException($"QoS read: PowerShell exited with {r.ExitCode}. {r.Error.Trim()}".Trim());
        return QosReadResult.Parse(r.Output);
    }
}

/// <summary>One ssh round trip of the vacation mode, with the key named in settings.txt.</summary>
internal static class VacationSsh
{
    /// <summary>
    /// Runs one remote command (status, off, on hours) and returns whether ssh exited
    /// with 0, plus its output (stdout and stderr) and its exit code (-1 when it did not run to the end: timeout,
    /// ssh missing). Cut after Vacation.TimeoutMs. Blocking: call it from a worker thread.
    /// </summary>
    public static (bool Ok, string Text, int ExitCode) Run(string host, string key, string command)
    {
        try
        {
            var ssh = AppPaths.SshExe();
            if (ssh is null) return (false, "ssh.exe not found", -1);
            var psi = new ProcessStartInfo(ssh)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in Vacation.SshArguments(host, key, command)) psi.ArgumentList.Add(a);
            var r = ProcessRun.Run(psi, Vacation.TimeoutMs);
            if (r.TimedOut) return (false, "timeout", -1);
            return (r.ExitCode == 0, (r.Output + " " + r.Error).Trim(), r.ExitCode);
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
                // Bounded: a child that inherited the pipes would keep a plain WaitForExit() from ever returning
                ProcessRun.Kill(p);
                return BzAutoOutcome.TimedOut;
            }

            // The process has exited; the pipes may still be held by a child: the exit code is valid now
            p.WaitForExit(ProcessRun.KillWaitMs);
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
