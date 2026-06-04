using System.Threading;

namespace ClaudeUsageBar;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "Global\\ClaudeUsageBar", out bool isNew);
        if (!isNew) return; // another instance already running

        ApplicationConfiguration.Initialize();
        using var controller = new TrayController();
        Application.Run();
    }
}
