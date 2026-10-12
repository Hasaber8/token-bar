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
            using var panel = new Dashboard(new Settings { ShowProjects = true, Theme = "Catppuccin Mocha" });
            var range = new ReportingRange(UsagePeriod.Day);
            var source = new SourceStats("Fixture", range.Labels.Length) { Available = true };
            source.Add("fixture-model", new(1000, 100, 500, Cost: 1.25), false, 0, "session", null, "Example", DateTimeOffset.Now);
            panel.UpdateUsage([source], range);
            // WinForms skips invisible child controls in DrawToBitmap. Create
            // a transparent offscreen window so the check paints the real UI.
            panel.Opacity = 0;
            panel.Location = new Point(-32000, -32000);
            panel.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(panel.Width, panel.Height);
            panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, panel.Size));
            var painted = 0;
            var background = panel.BackColor.ToArgb();
            for (var y = 60; y < bitmap.Height - 60; y += 2)
                for (var x = 20; x < bitmap.Width - 20; x += 2)
                    if (bitmap.GetPixel(x, y).ToArgb() != background) painted++;
            if (painted < 500) throw new InvalidOperationException("Dashboard content was not painted.");
            if (args.Length > 1) bitmap.Save(Path.GetFullPath(args[1]), System.Drawing.Imaging.ImageFormat.Png);
            panel.Hide();
            return;
        }
        using var mutex = new Mutex(true, "Local\\TokenBar-" + Environment.UserName, out var firstInstance);
        if (!firstInstance) return;
        using var context = new TrayContext();
        Application.Run(context);
    }
}
