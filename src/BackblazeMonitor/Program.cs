using BackblazeMonitor.Core;

namespace BackblazeMonitor;

internal static class Program
{
    private const string MutexName = @"Local\BackblazeMonitor.Tile";
    private const string ShowEventName = @"Local\BackblazeMonitor.Tile.Show";
    private const string AppUserModelId = "BackblazeMonitor.Tile";

    private static int _errorBoxShown;

    [STAThread]
    private static void Main()
    {
        // One tile per user session. A second launch asks the first one to show itself (it may be hidden in the
        // notification area), then exits: the user who starts it again sees something happen
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!createdNew)
        {
            try
            {
                showEvent.Set();
            }
            catch
            {
                // The first instance is gone or unreachable: nothing more to say
            }

            return;
        }

        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => OnFatal("ui thread", e.Exception, recoverable: true);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                OnFatal("unhandled", e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()), recoverable: false);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                ErrorLog.Write("task", e.Exception);
                e.SetObserved();
            };

            // Own application identity: the taskbar shows this window's icon and groups it apart
            try
            {
                Native.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            }
            catch
            {
                // Cosmetic only
            }

            ApplicationConfiguration.Initialize();
            using var form = new MainForm();
            var listener = new Thread(() => ListenForSecondLaunch(showEvent, form)) { IsBackground = true, Name = "show-request" };
            listener.Start();
            // Not ShowDialog: hiding a modal window ends its loop, and minimizing the tile to the
            // notification area would close the monitor.
            Application.Run(form);
        }
        catch (Exception ex)
        {
            OnFatal("startup", ex, recoverable: false);
        }
        finally
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned: nothing to release
            }
        }
    }

    // Each second launch signals the event: bring the tile back on the UI thread
    private static void ListenForSecondLaunch(EventWaitHandle showEvent, MainForm form)
    {
        while (showEvent.WaitOne())
        {
            try
            {
                if (form.IsDisposed) return;
                if (form.IsHandleCreated) form.BeginInvoke(form.ShowTile);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                return; // the window is closing
            }
        }
    }

    // Logs every error; shows a friendly box once, so a loop of errors cannot bury the desktop in dialogs
    private static void OnFatal(string where, Exception ex, bool recoverable)
    {
        ErrorLog.Write(where, ex);
        if (Interlocked.Exchange(ref _errorBoxShown, 1) != 0) return;
        try
        {
            MessageBox.Show(
                FatalTexts.Message(recoverable),
                AppInfo.Title,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch
        {
            // Nothing more we can do
        }
    }
}
