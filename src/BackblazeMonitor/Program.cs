using BackblazeMonitor.Core;

namespace BackblazeMonitor;

internal static class Program
{
    private const string MutexName = @"Local\BackblazeMonitor.Tile";
    private const string AppUserModelId = "BackblazeMonitor.Tile";

    private static int _errorBoxShown;

    [STAThread]
    private static void Main()
    {
        // One tile per user session: a second launch exits quietly
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew) return;

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
