using System.Diagnostics;
using TokenBar.Core;

namespace TokenBar.App;

internal sealed class Dashboard : Form
{
    private readonly Settings settings;
    private readonly FlowLayoutPanel content = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
        AutoScroll = true
    };
    private readonly Label status = new()
    {
        Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(24, 12, 24, 0), AutoEllipsis = true
    };
    private readonly Dictionary<(float Size, bool Bold), Font> fonts = [];
    private readonly ToolTip tooltips = new();
    private SourceStats[] sources = [];
    private ReportingRange range;
    private Palette palette;
    public event EventHandler? SettingsChanged;
    public event EventHandler? RefreshRequested;

    public Dashboard(Settings settings)
    {
        this.settings = settings;
        range = new(settings.Period, settings.Relative);
        palette = Palette.For(settings.Theme);
        Text = "Token Bar";
        Icon = SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.None;
        Font = UiFont(9.5f);
        _ = Handle; // Establish the actual display DPI before sizing controls.
        ClientSize = new Size(S(640), S(700));
        MinimumSize = new Size(S(600), S(420));
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        Controls.Add(content);
        Controls.Add(status);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        content.ClientSizeChanged += (_, _) => ResizeContent();
        DpiChanged += (_, _) => { MinimumSize = new Size(S(600), S(420)); ApplySettings(); };
        ApplySettings();
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
    private Font UiFont(float size, bool bold = false)
    {
        if (!fonts.TryGetValue((size, bold), out var font))
            fonts[(size, bold)] = font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
        return font;
    }
    private Label TextLabel(string text, float size = 9.5f, bool bold = false, bool muted = false) => new()
    {
        Text = text, AutoSize = true, Font = UiFont(size, bold), ForeColor = muted ? palette.Muted : palette.Text,
        BackColor = Color.Transparent, Margin = Padding.Empty
    };
    private RoundedCard Card(int height) => new(palette) { Height = S(height), Margin = new Padding(0, 0, 0, S(10)) };
    public void SetStatus(string text) { status.Text = text; tooltips.SetToolTip(status, text); }
    public void ApplySettings()
    {
        palette = Palette.For(settings.Theme);
        BackColor = content.BackColor = status.BackColor = palette.Background;
        status.ForeColor = palette.Muted;
        status.Font = UiFont(8.5f);
        status.Height = S(34);
        status.Padding = new Padding(S(20), S(8), S(20), 0);
        content.Padding = new Padding(S(20), S(16), S(20), S(12));
        Render();
    }
    public void UpdateUsage(SourceStats[] value, ReportingRange reportingRange)
    {
        sources = value;
        range = reportingRange;
        Render();
        var errors = sources.Where(s => s.Error is not null).Select(s => s.Name + ": " + s.Error).ToArray();
        SetStatus(errors.Length > 0 ? string.Join("; ", errors) : $"Updated {DateTime.Now:t}  ·  All data stays on this device");
    }

    private void Render()
    {
        var scroll = -content.AutoScrollPosition.Y;
        content.SuspendLayout();
        foreach (Control old in content.Controls.Cast<Control>().ToArray()) { content.Controls.Remove(old); old.Dispose(); }

        var header = new Panel { Height = S(34), Margin = new Padding(0, 0, 0, S(10)) };
        var title = TextLabel("Token Bar", 18, true);
        title.Location = Point.Empty;
        var subtitle = TextLabel("LOCAL AI USAGE", 8, muted: true);
        var refresh = new PillButton("Refresh", palette, false) { Size = new Size(S(82), S(32)), Font = UiFont(9) };
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        tooltips.SetToolTip(refresh, "Refresh local usage now");
        header.Controls.AddRange([title, subtitle, refresh]);
        header.Resize += (_, _) =>
        {
            refresh.Location = new Point(header.Width - refresh.Width, 0);
            subtitle.Location = new Point(Math.Max(title.Right + S(14), header.Width - refresh.Width - subtitle.Width - S(18)), S(11));
            subtitle.Visible = header.Width > S(510);
        };
        content.Controls.Add(header);
        content.Controls.Add(PeriodControls());

        var total = sources.Aggregate(new Usage(), (a, source) => a + source.Total);
        var estimated = sources.Any(s => s.EstimatedPricing.Count > 0);
        var summary = Card(110);
        var caption = TextLabel("API-equivalent spend", 9, muted: true);
        caption.Location = new Point(S(18), S(14));
        var amount = TextLabel((estimated ? "~" : "") + Format.Money(total.Cost), 30, true);
        amount.Location = new Point(S(15), S(32));
        var date = TextLabel(DateCaption(), 9, muted: true);
        var activeCount = sources.Count(s => s.PerModel.Count > 0);
        var activity = TextLabel(sources.Length == 0 ? "Reading local usage…" :
            $"{activeCount} active {(activeCount == 1 ? "agent" : "agents")}", 8.5f, muted: true);
        activity.Location = new Point(S(18), S(87));
        var pricing = TextLabel(estimated ? "Includes estimated prices" : "Bundled model pricing", 8.5f, muted: true);
        tooltips.SetToolTip(pricing, "Spend uses API-equivalent rates, not subscription billing. ~ marks estimates or unavailable prices.");
        summary.Controls.AddRange([caption, amount, date, activity, pricing]);
        summary.Resize += (_, _) =>
        {
            date.Location = new Point(summary.Width - date.Width - S(18), S(15));
            pricing.Location = new Point(summary.Width - pricing.Width - S(18), S(87));
        };
        content.Controls.Add(summary);
        content.Controls.Add(Metrics(total));

        if (settings.ShowGraph)
        {
            var values = new double[range.Labels.Length];
            foreach (var source in sources)
                for (var i = 0; i < Math.Min(values.Length, source.Buckets.Length); i++) values[i] += source.Buckets[i];
            content.Controls.Add(new SpendGraph(values, range, palette, UiFont(10, true), UiFont(8.5f))
                { Height = S(160), Margin = new Padding(0, 0, 0, S(10)), AccessibleName = "Spend over time" });
        }

        foreach (var source in sources.Where(s => s.PerModel.Count > 0))
        {
            var rows = source.PerModel.OrderByDescending(pair => pair.Value.Cost).Select(pair =>
            {
                var slash = pair.Key.IndexOf('/');
                var model = slash > 0 ? pair.Key[(slash + 1)..] : pair.Key;
                var provider = slash > 0 ? pair.Key[..slash] : "anthropic";
                var usage = pair.Value;
                return new UsageRow([model, (source.EstimatedPricing.Contains(pair.Key) ? "~" : "") + Format.Money(usage.Cost),
                    Format.Tokens(usage.Input), Format.Tokens(usage.Output), usage.HitRate.ToString("P0")],
                    ProviderName(provider), pair.Key);
            }).ToArray();
            content.Controls.Add(UsageCard(source.Name, Format.Money(source.Total.Cost), ["MODEL", "SPEND", "INPUT", "OUTPUT", "CACHE HIT"], rows));
        }
        if (sources.Length > 0 && !sources.Any(s => s.PerModel.Count > 0))
        {
            var empty = Card(96);
            var titleLabel = TextLabel("No usage in this period", 11, true);
            titleLabel.Location = new Point(S(20), S(20));
            var help = TextLabel("Try a wider range, or start a session with a supported agent.", 9, muted: true);
            help.Location = new Point(S(20), S(50));
            empty.Controls.AddRange([titleLabel, help]);
            content.Controls.Add(empty);
        }
        if (settings.ShowProjects) RenderProjects();

        var found = sources.Where(s => s.Available).Select(s => s.Name).ToArray();
        if (sources.Length > 0)
        {
            var sourceLabel = TextLabel(found.Length > 0 ? "CONNECTED  ·  " + string.Join("  ·  ", found) : "No agent logs found in this profile", 8.5f, muted: true);
            sourceLabel.Margin = new Padding(S(2), S(3), 0, S(8));
            content.Controls.Add(sourceLabel);
        }
        ResizeContent();
        content.ResumeLayout(true);
        content.AutoScrollPosition = new Point(0, scroll);
    }

    private string DateCaption()
    {
        var start = range.Start.LocalDateTime;
        var end = range.End.LocalDateTime.AddDays(-1);
        if (range.Period == UsagePeriod.Day) return start.ToString("ddd, MMM d, yyyy");
        return start.Year == end.Year ? $"{start:MMM d} – {end:MMM d, yyyy}" : $"{start:MMM d, yyyy} – {end:MMM d, yyyy}";
    }

    private Panel PeriodControls()
    {
        var row = new Panel { Height = S(40), Margin = new Padding(0, 0, 0, S(10)) };
        var periods = new RoundedCard(palette) { Height = S(40), Width = S(312), Radius = 10 };
        foreach (var period in Enum.GetValues<UsagePeriod>())
        {
            var button = new PillButton(period.ToString(), palette, settings.Period == period)
                { Location = new Point(S(4 + (int)period * 76), S(4)), Size = new Size(S(76), S(32)), Font = UiFont(9, true) };
            button.Click += (_, _) => { settings.Period = period; SettingsChanged?.Invoke(this, EventArgs.Empty); Render(); };
            periods.Controls.Add(button);
        }
        var styles = new RoundedCard(palette) { Height = S(40), Width = S(158), Radius = 10 };
        foreach (var relative in new[] { false, true })
        {
            var button = new PillButton(relative ? "Rolling" : "Calendar", palette, settings.Relative == relative)
                { Location = new Point(S(relative ? 79 : 4), S(4)), Size = new Size(S(75), S(32)), Font = UiFont(8.5f) };
            tooltips.SetToolTip(button, relative ? "Today, last 7 days, last 30 days, or last 12 months" : "Today, this week, this month, or this year");
            button.Click += (_, _) => { settings.Relative = relative; SettingsChanged?.Invoke(this, EventArgs.Empty); Render(); };
            styles.Controls.Add(button);
        }
        row.Controls.AddRange([periods, styles]);
        row.Resize += (_, _) => styles.Location = new Point(row.Width - styles.Width, 0);
        return row;
    }

    private TableLayoutPanel Metrics(Usage total)
    {
        var row = new TableLayoutPanel { Height = S(84), ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 0, 0, S(10)) };
        for (var i = 0; i < 3; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        var values = new[] { Format.Tokens(total.Input), Format.Tokens(total.Output), total.HitRate.ToString("P0") };
        var titles = new[] { "Input tokens", "Output tokens", "Cache hit rate" };
        var descriptions = new[] { "Fresh input", "Includes reasoning", "Cache read / context" };
        for (var i = 0; i < 3; i++)
        {
            var card = Card(84);
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(i == 0 ? 0 : S(4), 0, i == 2 ? 0 : S(4), 0);
            var title = TextLabel(titles[i], 8.5f, muted: true); title.Location = new Point(S(14), S(11));
            var value = TextLabel(values[i], 17, true); value.Location = new Point(S(12), S(30));
            var description = TextLabel(descriptions[i], 8, muted: true); description.Location = new Point(S(14), S(65));
            card.Controls.AddRange([title, value, description]);
            tooltips.SetToolTip(card, i == 2 ? $"{Format.Tokens(total.CacheRead)} cached / {Format.Tokens(total.Context)} context tokens" : titles[i]);
            row.Controls.Add(card, i, 0);
        }
        return row;
    }

    private RoundedCard UsageCard(string title, string total, string[] columns, UsageRow[] rows)
    {
        var card = Card(74 + rows.Length * 44);
        var titleLabel = TextLabel(title, 11, true); titleLabel.Location = new Point(S(18), S(14));
        var totalLabel = TextLabel(total, 10, true); totalLabel.ForeColor = palette.Muted;
        card.Controls.AddRange([titleLabel, totalLabel]);
        var table = new TableLayoutPanel { Location = new Point(S(18), S(45)), RowCount = rows.Length + 1,
            ColumnCount = columns.Length, Height = S(22 + rows.Length * 44), Margin = Padding.Empty, Padding = Padding.Empty,
            BackColor = palette.Surface, CellBorderStyle = TableLayoutPanelCellBorderStyle.None };
        var widths = columns.Length == 5 ? new[] { 40f, 17f, 14f, 14f, 15f } : [49f, 17f, 17f, 17f];
        for (var i = 0; i < columns.Length; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, widths[i]));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, S(22)));
        for (var col = 0; col < columns.Length; col++) table.Controls.Add(Cell(columns[col], col == 0, muted: true, header: true), col, 0);
        for (var r = 0; r < rows.Length; r++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, S(44)));
            for (var col = 0; col < columns.Length; col++)
            {
                if (col == 0 && rows[r].Secondary is { } secondary)
                {
                    var model = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = palette.Surface };
                    var name = Cell(rows[r].Cells[0], true); name.Dock = DockStyle.Top; name.Height = S(26); name.Font = UiFont(9.5f, true);
                    var provider = Cell(secondary, true, muted: true); provider.Dock = DockStyle.Bottom; provider.Height = S(18); provider.Font = UiFont(8);
                    model.Controls.AddRange([name, provider]);
                    tooltips.SetToolTip(name, rows[r].Tooltip ?? rows[r].Cells[0]);
                    table.Controls.Add(model, col, r + 1);
                }
                else
                {
                    Label label;
                    if (col == 0 && rows[r].Open is { } open)
                    {
                        var link = new LinkLabel { Text = rows[r].Cells[col], Dock = DockStyle.Fill, AutoEllipsis = true,
                            Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, BackColor = palette.Surface,
                            LinkColor = palette.Accent, ActiveLinkColor = palette.Accent, VisitedLinkColor = palette.Accent,
                            LinkBehavior = LinkBehavior.HoverUnderline, AccessibleName = rows[r].Cells[col] };
                        link.LinkClicked += (_, _) => open();
                        label = link;
                    }
                    else label = Cell(rows[r].Cells[col], col == 0);
                    label.Font = UiFont(9.5f, col == 1);
                    tooltips.SetToolTip(label, col == 0 ? rows[r].Tooltip ?? label.Text : label.Text);
                    table.Controls.Add(label, col, r + 1);
                }
            }
        }
        card.Controls.Add(table);
        card.Resize += (_, _) =>
        {
            totalLabel.Location = new Point(card.Width - totalLabel.Width - S(18), S(16));
            table.Width = card.Width - S(36);
        };
        return card;
    }
    private Label Cell(string text, bool left, bool muted = false, bool header = false) => new()
    {
        Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty, AutoEllipsis = true,
        TextAlign = left ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight,
        Font = UiFont(header ? 7.5f : 9.5f), ForeColor = muted ? palette.Muted : palette.Text,
        BackColor = palette.Surface, AccessibleName = text
    };
    private static string ProviderName(string provider) => provider switch
    {
        "openai" => "OpenAI", "anthropic" => "Anthropic", "amazon-bedrock" => "Amazon Bedrock",
        "openrouter" => "OpenRouter", _ => provider
    };

    private void RenderProjects()
    {
        var sessions = sources.SelectMany(s => s.Sessions.Values.Select(session => (Source: s.Name, Session: session))).ToArray();
        var projects = sessions.GroupBy(s => s.Session.ProjectPath ?? "Unknown project", StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Path = g.Key, Total = g.Aggregate(new Usage(), (a, s) => a + s.Session.Total), Count = g.Count() })
            .OrderByDescending(p => p.Total.Cost).Take(8).Select(p => new UsageRow(
                [Path.GetFileName(p.Path.TrimEnd('\\', '/')), Format.Money(p.Total.Cost), Format.Tokens(p.Total.Context + p.Total.Output), p.Count.ToString()],
                Tooltip: p.Path, Open: Directory.Exists(p.Path) ? () => OpenProject(p.Path) : null)).ToArray();
        content.Controls.Add(UsageCard("Projects", projects.Length + " shown", ["PROJECT", "SPEND", "TOKENS", "SESSIONS"], projects));
        var sessionRows = sessions.OrderByDescending(s => s.Session.Total.Cost).Take(8).Select(s => new UsageRow(
            [s.Session.Title ?? s.Session.Id, s.Source, Format.Money(s.Session.Total.Cost), Format.Tokens(s.Session.Total.Context + s.Session.Total.Output)],
            Tooltip: s.Session.Id + "\n" + s.Session.ProjectPath)).ToArray();
        content.Controls.Add(UsageCard("Sessions", sessionRows.Length + " shown", ["SESSION", "AGENT", "SPEND", "TOKENS"], sessionRows));
    }
    private void OpenProject(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { SetStatus("Could not open project: " + ex.Message); }
    }
    private void ResizeContent()
    {
        // ClientSize already excludes the native scroll bar. Subtracting it a
        // second time makes cards change width as the content grows.
        var width = Math.Max(S(480), content.ClientSize.Width - content.Padding.Horizontal);
        foreach (Control control in content.Controls) if (control is not Label) control.Width = width;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5) { RefreshRequested?.Invoke(this, EventArgs.Empty); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { tooltips.Dispose(); foreach (var font in fonts.Values) font.Dispose(); fonts.Clear(); }
    }
    private sealed record UsageRow(string[] Cells, string? Secondary = null, string? Tooltip = null, Action? Open = null);
}
