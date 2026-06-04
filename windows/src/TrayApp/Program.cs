using System.Threading;

namespace ClaudeUsageBar;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single instance per user session. Use a Local mutex (not Global, which
        // needs a privilege and would block across sessions) and treat an
        // abandoned mutex (previous instance crashed) as a successful acquire so
        // the app can always be relaunched.
        using var mutex = new Mutex(initiallyOwned: false, "Local\\ClaudeUsageBar");
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return;

        ApplicationConfiguration.Initialize();
        using var controller = new TrayController();
        Application.Run();
    }
}
