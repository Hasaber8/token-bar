using TokenBar.Core;

namespace TokenBar.App;

internal static class DashboardSmokeCheck
{
    public static void Run(string? snapshot)
    {
        var cases = new[]
        {
            (Name: "light", Theme: "GitHub Light", Width: 640, Projects: false, Empty: false),
            (Name: "dark", Theme: "Catppuccin Mocha", Width: 640, Projects: false, Empty: false),
            (Name: "compact", Theme: "GitHub Light", Width: 620, Projects: true, Empty: false),
            (Name: "empty", Theme: "GitHub Light", Width: 640, Projects: false, Empty: true)
        };
        foreach (var test in cases)
        {
            using var panel = new Dashboard(new Settings { ShowProjects = test.Projects, Theme = test.Theme });
            panel.ClientSize = new Size((int)(test.Width * panel.DeviceDpi / 96.0), (int)(700 * panel.DeviceDpi / 96.0));
            var range = new ReportingRange(UsagePeriod.Day);
            var date = DateTimeOffset.Now;
            var source = new SourceStats("Codex", range.Labels.Length) { Available = !test.Empty };
            if (!test.Empty)
            {
                source.Add("openai/gpt-6.1-sol", new(120000, 40000, 4_000_000, Cost: 1.1), false, range.Bucket(date), "session", null, "Example", date);
                source.Add("openai/gpt-6.1-sol", new(25000, 7300, 800000, Cost: .13), false, Math.Max(0, range.Bucket(date) - 1), "session", null, "Example", date);
                if (test.Projects) source.Add("openai/a-long-model-name-with-extra-routing-qualifiers", new(1000, 1000, Cost: 12), true, range.Bucket(date), "other-session", null, "A longer session name", date);
            }
            panel.UpdateUsage([source], range);
            // WinForms skips invisible children in DrawToBitmap. An invisible,
            // offscreen window paints the same controls as the live dashboard.
            panel.Opacity = 0;
            panel.Location = new Point(-32000, -32000);
            panel.Show();
            Application.DoEvents();
            if (Descendants(panel).OfType<FlowLayoutPanel>().Any(p => p.AutoScroll && p.HorizontalScroll.Visible))
                throw new InvalidOperationException(test.Name + ": dashboard requires horizontal scrolling.");
            foreach (var label in Descendants(panel).OfType<Label>())
            {
                if (label.Parent is null || label.Dock != DockStyle.None || label.Parent is ScrollableControl { AutoScroll: true }) continue;
                if (label.Right > label.Parent.ClientSize.Width + 2 || label.Bottom > label.Parent.ClientSize.Height + 2)
                    throw new InvalidOperationException($"{test.Name}: label extends past its container: {label.Text}");
            }
            using var bitmap = new Bitmap(panel.Width, panel.Height);
            panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, panel.Size));
            var painted = 0;
            for (var y = 60; y < bitmap.Height - 60; y += 2)
                for (var x = 20; x < bitmap.Width - 20; x += 2)
                    if (bitmap.GetPixel(x, y).ToArgb() != panel.BackColor.ToArgb()) painted++;
            if (painted < 500) throw new InvalidOperationException(test.Name + ": dashboard content was not painted.");
            if (snapshot is not null)
            {
                var path = Path.GetFullPath(snapshot);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (test.Name != "light") path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + test.Name + ".png");
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            panel.Hide();
        }
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
