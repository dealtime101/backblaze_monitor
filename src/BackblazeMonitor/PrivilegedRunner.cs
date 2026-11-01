using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BackblazeMonitor.Core;

namespace BackblazeMonitor;

/// <summary>
/// Client of the on-demand SYSTEM scheduled task (the "agent") through the Task Scheduler COM object,
/// late-bound: no interop package. Every call is short; the caller pumps messages between calls.
/// </summary>
internal static class AgentTaskClient
{
    // Every COM object obtained along the way is released when the scope ends, in reverse order: left to the
    // finalizer, each poll (up to 240 per action) would pile up new wrappers.
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = new();

        public dynamic Track(object? com)
        {
            if (com is not null) _objects.Add(com);
            return com!;
        }

        public void Dispose()
        {
            for (var i = _objects.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (Marshal.IsComObject(_objects[i])) Marshal.FinalReleaseComObject(_objects[i]);
                }
                catch
                {
                    // Nothing to do
                }
            }

            _objects.Clear();
        }
    }

    private static dynamic? Connect(ComScope com)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return null;
        dynamic svc = com.Track(Activator.CreateInstance(type));
        svc.Connect();
        return svc;
    }

    private static dynamic GetTask(ComScope com)
    {
        dynamic svc = Connect(com) ?? throw new InvalidOperationException("Task Scheduler unavailable");
        dynamic folder = com.Track(svc.GetFolder("\\"));
        return com.Track(folder.GetTask(BzConstants.AgentTask));
    }

    /// <summary>Arguments of the task's first action, or <c>null</c> when the task does not exist or cannot be read.</summary>
    public static string? GetArguments()
    {
        try
        {
            using var com = new ComScope();
            dynamic task = GetTask(com);
            dynamic definition = com.Track(task.Definition);
            dynamic actions = com.Track(definition.Actions);
            dynamic action;
            try
            {
                action = com.Track(actions.Item(1));
            }
            catch
            {
                action = com.Track(actions[1]);
            }

            return (string?)action.Arguments;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Last run time and last result of the task. Throws if unreadable.</summary>
    public static (DateTime LastRun, int LastResult) GetLastRun()
    {
        using var com = new ComScope();
        dynamic task = GetTask(com);
        DateTime lastRun = task.LastRunTime;
        int lastResult = task.LastTaskResult;
        return (lastRun, lastResult);
    }

    /// <summary>Starts the task. Throws if it cannot start.</summary>
    public static void Start()
    {
        using var com = new ComScope();
        dynamic task = GetTask(com);
        com.Track(task.Run(null)); // returns the running instance
    }
}

/// <summary>
/// Runs privileged actions: first through the SYSTEM agent when it is installed and current (no UAC
/// prompt), otherwise through an elevated PowerShell (UAC) that also installs the agent. All waits pump
/// messages so the window stays alive; the caller disables the form and the timer around the call.
/// </summary>
internal static class PrivilegedRunner
{
    private const int AgentPollMs = 250;
    private const int AgentPolls = 240;
    private const int ElevatedTimeoutMs = 10 * 60 * 1000;

    /// <summary>
    /// Runs the action. <paramref name="request"/> is the agent request (service verb or bit rate), or
    /// <c>null</c> for an action the agent cannot do (the time window): UAC alone, the agent is installed at
    /// the next action. <paramref name="elevatedCommand"/> is the full PowerShell command for the UAC route.
    /// </summary>
    public static ActionOutcome Run(string? request, string elevatedCommand)
    {
        var command = elevatedCommand;
        if (request is not null)
        {
            var ok = RunAgent(request);
            if (ok is not null) return ActionOutcomes.FromBool(ok.Value);
            command = GetAgentSetup() + command;
        }

        var outcome = RunElevated(command);
        if (request is not null) LogAgentSetupFailure();
        return outcome;
    }

    // The elevated setup leaves the reason of a failed installation in a file (it cannot fail the action that
    // follows): put it in error.log, once, and remove the file.
    private static void LogAgentSetupFailure()
    {
        try
        {
            var file = AgentCommands.ErrorFile(AppPaths.AgentFile);
            if (!File.Exists(file)) return;
            var text = File.ReadAllText(file).Trim();
            File.Delete(file);
            if (text.Length > 0) ErrorLog.Write("agent setup", new InvalidOperationException(text));
        }
        catch
        {
            // Unreadable now: the next action tries again
        }
    }

    private static IReadOnlyList<long> AgentBits() => QosChoices.AgentBits(QosChoices.All).ToList();

    private static string GetAgentSetup()
    {
        string sid;
        try
        {
            sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        }
        catch
        {
            sid = "";
        }

        return AgentCommands.GetSetup(AppPaths.AgentFile, AgentBits(), sid, AppPaths.PsHome);
    }

    /// <summary>
    /// Runs the request through the agent: true/false according to its result, <c>null</c> when the agent is
    /// missing, is no longer the one for this version (folder moved, new build) or refuses to start: it must
    /// be (re)installed. Wait capped at 60 s.
    /// </summary>
    private static bool? RunAgent(string request)
    {
        var installed = AgentTaskClient.GetArguments();
        if (installed is null || !AgentCommands.IsCurrent(installed, AppPaths.AgentFile, AgentBits())) return null;
        DateTime before;
        try
        {
            File.WriteAllText(AppPaths.AgentFile, request);
            (before, _) = AgentTaskClient.GetLastRun();
            var gap = AgentRun.GetStartGapMs(before, DateTime.Now);
            if (gap > 0) UiWait.Pump((int)Math.Ceiling(gap));
            AgentTaskClient.Start();
        }
        catch
        {
            return null;
        }

        for (var n = 0; n < AgentPolls; n++)
        {
            UiWait.Pump(AgentPollMs);
            try
            {
                var (lastRun, lastResult) = AgentTaskClient.GetLastRun();
                if (AgentRun.IsFinished(before, lastRun, lastResult)) return AgentRun.Succeeded(lastResult);
            }
            catch
            {
                // Transient read failure: poll again
            }
        }

        return false;
    }

    /// <summary>
    /// Elevated PowerShell with -EncodedCommand (immune to quoting problems). Not waited on with a blocking
    /// call: the wait pumps messages, then WaitForExit makes the exit code valid.
    /// </summary>
    private static ActionOutcome RunElevated(string command)
    {
        try
        {
            var psi = new ProcessStartInfo(AppPaths.PowerShellExe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "-NoProfile -EncodedCommand " + EncodedCommand.Encode(command),
            };
            using var p = Process.Start(psi);
            if (p is null) return ActionOutcome.Failed;
            if (!UiWait.Until(() => p.HasExited, ElevatedTimeoutMs)) return ActionOutcome.Failed;
            p.WaitForExit();
            return ActionOutcomes.FromExitCode(p.ExitCode);
        }
        catch (Win32Exception ex)
        {
            return ActionOutcomes.FromNativeError(ex.NativeErrorCode);
        }
        catch
        {
            return ActionOutcome.Failed;
        }
    }
}
