using System.Diagnostics;
using TokenBar.Core;

namespace TokenBar.App;

internal sealed class Dashboard : Form
{
    private readonly Settings settings;
    private readonly FlowLayoutPanel content = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, AutoScroll = true, Padding = new Padding(20) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(20, 8, 20, 0), AutoEllipsis = true };
    private SourceStats[] sources = [];
    private ReportingRange range;
    private Palette palette;
    private readonly Dictionary<(float Size, bool Bold), Font> labelFonts = [];
    public event EventHandler? SettingsChanged;

    public Dashboard(Settings settings)
    {
        this.settings = settings;
        range = new(settings.Period, settings.Relative);
        palette = Palette.For(settings.Theme);
        Text = "Token Bar";
        Icon = SystemIcons.Application;
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(640, 650);
        MinimumSize = new Size(560, 400);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Controls.Add(content); Controls.Add(status);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Resize += (_, _) => ResizeContent();
        ApplySettings();
    }
    public void SetStatus(string text) => status.Text = text;
    public void ApplySettings()
    {
        palette = Palette.For(settings.Theme);
        BackColor = content.BackColor = status.BackColor = palette.Background;
        ForeColor = status.ForeColor = palette.Muted;
        Render();
    }
    public void UpdateUsage(SourceStats[] value, ReportingRange reportingRange)
    {
        sources = value; range = reportingRange; Render();
        var errors = sources.Where(s => s.Error is not null).Select(s => s.Name + ": " + s.Error).ToArray();
        status.Text = errors.Length > 0 ? string.Join("; ", errors) : $"Updated {DateTime.Now:t} · API-equivalent spend · ~ estimated price";
    }
    private Font LabelFont(float size, bool bold)
    {
        if (!labelFonts.TryGetValue((size, bold), out var font))
            labelFonts[(size, bold)] = font = new Font(Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular);
        return font;
    }
    private Label Label(string text, float size = 10, bool bold = false, bool muted = false) => new()
    {
        Text = text, AutoSize = true, Font = LabelFont(size, bold),
        ForeColor = muted ? palette.Muted : palette.Text, Margin = new Padding(0, 0, 0, 12)
    };
    private void Render()
    {
        content.SuspendLayout();
        foreach (Control old in content.Controls.Cast<Control>().ToArray()) { content.Controls.Remove(old); old.Dispose(); }
        content.Controls.Add(Label("Token Bar", 20, true));
        var options = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 18) };
        foreach (var period in Enum.GetValues<UsagePeriod>())
        {
            var button = new Button { Text = period.ToString(), AutoSize = true, FlatStyle = FlatStyle.Flat,
                BackColor = period == settings.Period ? palette.Surface : palette.Background,
                ForeColor = period == settings.Period ? palette.Accent : palette.Text, Margin = new Padding(0, 0, 6, 0) };
            button.Click += (_, _) => { settings.Period = period; SettingsChanged?.Invoke(this, EventArgs.Empty); Render(); };
            options.Controls.Add(button);
        }
        var relative = new CheckBox { Text = "Relative range", AutoSize = true, Checked = settings.Relative,
            ForeColor = palette.Text, Margin = new Padding(8, 6, 0, 0) };
        relative.CheckedChanged += (_, _) => { settings.Relative = relative.Checked; SettingsChanged?.Invoke(this, EventArgs.Empty); };
        options.Controls.Add(relative); content.Controls.Add(options);

        // Leave the last snapshot labelled with its actual date interval until
        // the asynchronous scan for the newly selected period completes.
        content.Controls.Add(Label($"{range.Start.LocalDateTime:MMM d, yyyy} – {range.End.LocalDateTime.AddDays(-1):MMM d, yyyy}", muted: true));
        var total = sources.Aggregate(new Usage(), (a, s) => a + s.Total);
        var estimated = sources.Any(s => s.EstimatedPricing.Count > 0);
        content.Controls.Add(Label((estimated ? "~" : "") + Format.Money(total.Cost), 30, true));
        content.Controls.Add(Label($"{Format.Tokens(total.Input)} input    {Format.Tokens(total.Output)} output    {total.HitRate:P0} cache hit", 12));

        if (sources.Length == 0) content.Controls.Add(Label("Loading local usage…", muted: true));
        else if (!sources.Any(s => s.PerModel.Count > 0))
        {
            content.Controls.Add(Label("No usage recorded in this period", 12, true));
            content.Controls.Add(Label("Looking for Claude Code, Codex, OpenCode, and pi logs\nin your Windows user profile. WSL logs need a separate scan.", muted: true));
        }
        if (settings.ShowGraph)
        {
            var values = new double[range.Labels.Length];
            foreach (var source in sources)
                for (var i = 0; i < Math.Min(values.Length, source.Buckets.Length); i++) values[i] += source.Buckets[i];
            content.Controls.Add(new SpendGraph(values, range.Labels, palette) { Height = 150, Margin = new Padding(0, 8, 0, 20) });
        }
        foreach (var source in sources.Where(s => s.PerModel.Count > 0))
        {
            content.Controls.Add(Label(source.Name, 12, true));
            var grid = Grid("Model", "Spend", "Input", "Output", "Hit");
            foreach (var (model, usage) in source.PerModel.OrderByDescending(pair => pair.Value.Cost))
                grid.Rows.Add(model, (source.EstimatedPricing.Contains(model) ? "~" : "") + Format.Money(usage.Cost),
                    Format.Tokens(usage.Input), Format.Tokens(usage.Output), usage.HitRate.ToString("P0"));
            FitGrid(grid); content.Controls.Add(grid);
        }
        if (settings.ShowProjects) RenderProjects();
        var available = string.Join(", ", sources.Where(s => s.Available).Select(s => s.Name));
        if (sources.Length > 0) content.Controls.Add(Label("Sources found: " + (available.Length > 0 ? available : "none"), muted: true));
        ResizeContent(); content.ResumeLayout(true);
    }
    private void ResizeContent()
    {
        var width = Math.Max(100, content.ClientSize.Width - content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        foreach (Control control in content.Controls) if (control is not System.Windows.Forms.Label) control.Width = width;
    }
    private DataGridView Grid(params string[] columns)
    {
        var grid = new DataGridView { ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false, BorderStyle = BorderStyle.None,
            BackgroundColor = palette.Background, GridColor = palette.Surface, EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, ScrollBars = ScrollBars.Vertical, Margin = new Padding(0, 0, 0, 20), AccessibleName = columns[0] + " usage" };
        grid.DefaultCellStyle.BackColor = palette.Background; grid.DefaultCellStyle.ForeColor = palette.Text;
        grid.DefaultCellStyle.SelectionBackColor = palette.Surface; grid.DefaultCellStyle.SelectionForeColor = palette.Text;
        grid.ColumnHeadersDefaultCellStyle.BackColor = palette.Surface; grid.ColumnHeadersDefaultCellStyle.ForeColor = palette.Muted;
        grid.ColumnHeadersHeight = 30; grid.RowTemplate.Height = 28;
        foreach (var name in columns) grid.Columns.Add(name, name);
        grid.Columns[0].FillWeight = 250;
        for (var i = 1; i < columns.Length; i++)
        {
            grid.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns[i].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        return grid;
    }
    private static void FitGrid(DataGridView grid) => grid.Height = Math.Min(340, grid.ColumnHeadersHeight + grid.Rows.Count * grid.RowTemplate.Height + 2);
    private void RenderProjects()
    {
        var sessions = sources.SelectMany(s => s.Sessions.Values.Select(session => (Source: s.Name, Session: session))).ToArray();
        content.Controls.Add(Label("Projects & sessions", 12, true));
        var projects = Grid("Project", "Spend", "Tokens", "Sessions");
        foreach (var project in sessions.GroupBy(s => s.Session.ProjectPath ?? "Unknown project", StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Path = g.Key, Total = g.Aggregate(new Usage(), (a, s) => a + s.Session.Total), Count = g.Count() })
            .OrderByDescending(p => p.Total.Cost).Take(8))
        {
            var row = projects.Rows[projects.Rows.Add(Path.GetFileName(project.Path.TrimEnd('\\', '/')), Format.Money(project.Total.Cost), Format.Tokens(project.Total.Context + project.Total.Output), project.Count)];
            row.Tag = project.Path; row.Cells[0].ToolTipText = project.Path + " (double-click to open)";
        }
        projects.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && projects.Rows[e.RowIndex].Tag is string path && Directory.Exists(path))
                try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { SetStatus("Could not open project: " + ex.Message); }
        };
        FitGrid(projects); content.Controls.Add(projects);
        var sessionGrid = Grid("Session", "Agent", "Spend", "Tokens");
        foreach (var (source, session) in sessions.OrderByDescending(s => s.Session.Total.Cost).Take(8))
        {
            var row = sessionGrid.Rows[sessionGrid.Rows.Add(session.Title ?? session.Id, source, Format.Money(session.Total.Cost), Format.Tokens(session.Total.Context + session.Total.Output))];
            row.Cells[0].ToolTipText = session.Id + "\n" + session.ProjectPath;
        }
        FitGrid(sessionGrid); content.Controls.Add(sessionGrid);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { foreach (var font in labelFonts.Values) font.Dispose(); labelFonts.Clear(); }
    }
}

internal sealed class SpendGraph(double[] values, string[] labels, Palette palette) : Control
{
    private readonly ToolTip tooltip = new();
    private int hovered = -1;
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(palette.Background);
        var max = values.DefaultIfEmpty(0).Max();
        using var brush = new SolidBrush(palette.Accent);
        using var text = new SolidBrush(palette.Muted);
        e.Graphics.DrawString("Spend " + (max > 0 ? "· peak " + Format.Money(max) : "· no recorded spend"), Font, text, 0, 0);
        var step = Width / (float)Math.Max(1, values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var height = max > 0 ? (float)(values[i] / max * (Height - 50)) : 0;
            e.Graphics.FillRectangle(brush, i * step + 2, Height - 25 - height, Math.Max(1, step - 4), height);
            if (i == 0 || i == values.Length / 2 || i == values.Length - 1)
            {
                var size = e.Graphics.MeasureString(labels[i], Font);
                e.Graphics.DrawString(labels[i], Font, text, Math.Clamp(i * step, 0, Math.Max(0, Width - size.Width)), Height - 20);
            }
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = Math.Clamp((int)(e.X / (float)Math.Max(1, Width) * values.Length), 0, Math.Max(0, values.Length - 1));
        if (index != hovered && values.Length > 0) { hovered = index; tooltip.SetToolTip(this, labels[index] + ": " + Format.Money(values[index])); }
    }
    protected override void Dispose(bool disposing) { if (disposing) tooltip.Dispose(); base.Dispose(disposing); }
}
