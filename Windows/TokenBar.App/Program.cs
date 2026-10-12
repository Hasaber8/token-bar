using TokenBar.Core;

namespace TokenBar.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--smoke-test"))
        {
            try { DashboardSmokeCheck.Run(args.Length > 1 ? args[1] : null); }
            catch (Exception ex)
            {
                var log = args.Length > 1 ? Path.ChangeExtension(Path.GetFullPath(args[1]), ".error.txt") : Path.Combine(Path.GetTempPath(), "token-bar-smoke-error.txt");
                File.WriteAllText(log, ex.ToString());
                Environment.ExitCode = 1;
            }
            return;
        }
        using var mutex = new Mutex(true, "Local\\TokenBar-" + Environment.UserName, out var firstInstance);
        if (!firstInstance) return;
        using var context = new TrayContext(args.Contains("--show"));
        Application.Run(context);
    }
}
