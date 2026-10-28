namespace LiteShot;

internal static class Program
{
    public const string CaptureEventName = @"Local\LiteShot.CaptureRequest.6A1F";
    public const string ExitEventName = @"Local\LiteShot.ExitRequest.6A1F";
    const string MutexName = @"Local\LiteShot.Instance.6A1F";

    [STAThread]
    static void Main(string[] args)
    {
        // After a portable update the new exe starts while the old one is still quitting; wait for it first.
        int wait = Array.IndexOf(args, "--wait");
        if (wait >= 0 && wait + 1 < args.Length && int.TryParse(args[wait + 1], out int oldPid))
        {
            try
            {
                using var old = System.Diagnostics.Process.GetProcessById(oldPid);
                old.WaitForExit(15000);
            }
            catch (ArgumentException) { /* already gone */ }
        }

        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: launching again means "take a screenshot" (or quit, with --exit).
            try
            {
                using var signal = EventWaitHandle.OpenExisting(args.Contains("--exit") ? ExitEventName : CaptureEventName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            if (args.Contains("--exit"))
            {
                // Block until the running copy has quit, so an installer can replace the exe right after.
                try
                {
                    if (mutex.WaitOne(TimeSpan.FromSeconds(8))) mutex.ReleaseMutex();
                }
                catch (AbandonedMutexException) { mutex.ReleaseMutex(); }
            }
            return;
        }

        if (args.Contains("--exit")) return;
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);

        Application.Run(new TrayApp(launchedAtStartup: args.Contains("--startup"), justUpdated: args.Contains("--updated")));
        mutex.ReleaseMutex();
    }

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            File.AppendAllText(Path.Combine(AppSettings.Directory, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }
}
