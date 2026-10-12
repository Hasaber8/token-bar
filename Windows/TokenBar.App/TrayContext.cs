using Microsoft.Win32;
using TokenBar.Core;

namespace TokenBar.App;

internal sealed class TrayContext : ApplicationContext
{
    private readonly Settings settings = Settings.Load();
    private readonly Scanner scanner = new();
    private readonly SourcePaths paths = SourcePaths.Discover();
    private readonly Dashboard dashboard;
    private readonly NotifyIcon tray;
    private readonly Icon trayIcon = TrayIcon.Create();
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly HashSet<string> watched = new(StringComparer.OrdinalIgnoreCase);
    private long requested = 1, scanned;
    private DateTime lastScan = DateTime.MinValue;
    private bool scanning, disposed;

    public TrayContext()
    {
        dashboard = new(settings);
        _ = dashboard.Handle; // Marshal all watcher notifications through a UI handle.
        dashboard.SettingsChanged += (_, _) => { SaveSettings(); BuildMenu(); Interlocked.Increment(ref requested); };
        dashboard.Icon = trayIcon;
        tray = new() { Icon = trayIcon, Text = "Token Bar: loading usage…", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowDashboard(); };
        BuildMenu();
        SystemEvents.UserPreferenceChanged += AppearanceChanged;
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
    }

    private void BuildMenu()
    {
        foreach (ToolStripItem old in menu.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
        menu.Items.Clear();
        menu.Items.Add("Open dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add("Refresh now", null, (_, _) => Interlocked.Increment(ref requested));
        menu.Items.Add(new ToolStripSeparator());
        var periods = new ToolStripMenuItem("Reporting period");
        foreach (var period in Enum.GetValues<UsagePeriod>())
        {
            var item = new ToolStripMenuItem(period.ToString()) { Checked = settings.Period == period };
            item.Click += (_, _) => { settings.Period = period; Changed(); };
            periods.DropDownItems.Add(item);
        }
        menu.Items.Add(periods);
        Toggle("Relative ranges", settings.Relative, () => settings.Relative = !settings.Relative);
        Toggle("Show spend graph", settings.ShowGraph, () => settings.ShowGraph = !settings.ShowGraph);
        Toggle("Projects & sessions", settings.ShowProjects, () => settings.ShowProjects = !settings.ShowProjects);
        var themes = new ToolStripMenuItem("Theme");
        foreach (var name in Palette.Names)
        {
            var item = new ToolStripMenuItem(name) { Checked = settings.Theme == name };
            item.Click += (_, _) => { settings.Theme = name; Changed(); };
            themes.DropDownItems.Add(item);
        }
        menu.Items.Add(themes);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
    }
    private void Toggle(string title, bool value, Action change)
    {
        var item = new ToolStripMenuItem(title) { Checked = value };
        item.Click += (_, _) => { change(); Changed(); };
        menu.Items.Add(item);
    }
    private void SaveSettings() { if (settings.Save() is { } error) dashboard.SetStatus(error); }
    private void Changed() { SaveSettings(); BuildMenu(); dashboard.ApplySettings(); Interlocked.Increment(ref requested); }
    private void AppearanceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!disposed && dashboard.IsHandleCreated) dashboard.BeginInvoke(() => { if (!disposed) dashboard.ApplySettings(); });
    }
    private void ShowDashboard()
    {
        dashboard.ApplySettings();
        if (!dashboard.Visible)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            dashboard.Location = new Point(Math.Max(area.Left, area.Right - dashboard.Width - 12), Math.Max(area.Top, area.Bottom - dashboard.Height - 12));
            dashboard.Show();
        }
        dashboard.Activate();
    }
    private void EnsureWatchers()
    {
        // Retry missing sources every minute, so installing an agent after
        // Token Bar starts enables live updates without restarting the app.
        foreach (var path in paths.WatchDirectories)
        {
            if (watched.Contains(path) || !Directory.Exists(path)) continue;
            try
            {
                var watcher = new FileSystemWatcher(path) { IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName };
                watcher.Changed += OnFileChange; watcher.Created += OnFileChange; watcher.Deleted += OnFileChange; watcher.Renamed += OnFileChange;
                watcher.Error += (_, _) => Interlocked.Increment(ref requested);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher); watched.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { dashboard.SetStatus("Live updates unavailable for " + path + "; checking every minute."); }
        }
    }
    private void OnFileChange(object sender, FileSystemEventArgs e) => Interlocked.Increment(ref requested);
    private async Task RefreshAsync()
    {
        if (scanning || disposed) return;
        var version = Interlocked.Read(ref requested);
        if (version == scanned && DateTime.UtcNow - lastScan < TimeSpan.FromSeconds(60)) return;
        scanning = true;
        try
        {
            EnsureWatchers();
            var relative = settings.Relative;
            var range = new ReportingRange(settings.Period, settings.Relative);
            var sources = await Task.Run(() => scanner.Scan(paths, range));
            if (disposed) return;
            // A period switch during a scan must not paint the old period.
            if (settings.Period == range.Period && settings.Relative == relative)
            {
                dashboard.UpdateUsage(sources, range);
                var total = sources.Aggregate(new Usage(), (a, s) => a + s.Total);
                var text = $"Token Bar: {Format.Money(total.Cost)} | {Format.Tokens(total.Input)} in | {Format.Tokens(total.Output)} out | {total.HitRate:P0} hit";
                tray.Text = text[..Math.Min(text.Length, 63)];
            }
        }
        catch (Exception ex) { if (!disposed) dashboard.SetStatus("Usage refresh failed: " + ex.Message); }
        finally { scanned = version; lastScan = DateTime.UtcNow; scanning = false; }
    }
    protected override void ExitThreadCore() { Dispose(); base.ExitThreadCore(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            SystemEvents.UserPreferenceChanged -= AppearanceChanged;
            timer.Dispose(); foreach (var watcher in watchers) watcher.Dispose();
            tray.Visible = false; tray.Dispose(); menu.Dispose(); dashboard.Dispose(); trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
