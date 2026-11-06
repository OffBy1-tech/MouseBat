namespace MouseBat;

internal static class Program
{
    private static readonly TimeSpan UpdateHandoverTimeout = TimeSpan.FromSeconds(15);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains(Updater.VerifyArgument)) return 0;

        bool justUpdated = args.Contains(Updater.UpdatedArgument);
        using var instance = new Mutex(initiallyOwned: false, @"Local\MouseBat.SingleInstance");
        if (!Acquire(instance, justUpdated ? UpdateHandoverTimeout : TimeSpan.Zero)) return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(justUpdated));
        return 0;
    }

    private static bool Acquire(Mutex instance, TimeSpan timeout)
    {
        try
        {
            return instance.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // The previous copy exited without releasing it, which is how every copy exits.
        }
    }
}
