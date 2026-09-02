namespace Kairon.Desktop;

static class Program
{
    private const string MutexName = "Kairon.Desktop.SingleInstance";

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        if (!createdNew)
        {
            // A second launch: fail clearly rather than starting a second backend/AI pair that
            // would fight the first one for :8000/:8001 (docs/DESKTOP_SHELL.md).
            MessageBox.Show("Kairon is already running.", "Kairon", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.ThreadException += (_, e) => ReportUnhandled(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportUnhandled(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void ReportUnhandled(Exception? exception)
    {
        try
        {
            AppPaths.WriteCrashLog(exception);
        }
        catch
        {
            // Logging the crash must never itself crash the crash handler.
        }

        MessageBox.Show(
            "Kairon hit an unexpected error and needs to close.\n\nSee Kairon logs for details.",
            "Kairon", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
