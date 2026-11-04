namespace MouseBat;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(initiallyOwned: true, @"Local\MouseBat.SingleInstance", out bool isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
