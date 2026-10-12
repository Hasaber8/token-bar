using System.Drawing.Drawing2D;
using TokenBar.Core;

namespace TokenBar.App;

internal static class Drawing
{
    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
    public static Color Blend(Color foreground, Color background, double amount) => Color.FromArgb(
        (int)(foreground.R * amount + background.R * (1 - amount)),
        (int)(foreground.G * amount + background.G * (1 - amount)),
        (int)(foreground.B * amount + background.B * (1 - amount)));
}

internal class RoundedCard : Panel
{
    protected Palette Palette { get; }
    [System.ComponentModel.DefaultValue(14)]
    public int Radius { get; set; } = 14;
    public RoundedCard(Palette palette)
    {
        Palette = palette;
        BackColor = palette.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Width < 2 || Height < 2) return;
        using var path = Drawing.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), Radius * DeviceDpi / 96f);
        using var surface = new SolidBrush(Palette.Surface);
        using var border = new Pen(Drawing.Blend(Palette.Text, Palette.Surface, .1));
        e.Graphics.FillPath(surface, path);
        e.Graphics.DrawPath(border, path);
    }
}

internal sealed class PillButton : Button
{
    private readonly Palette palette;
    private readonly bool selected;
    private bool hovered;
    public PillButton(string text, Palette palette, bool selected)
    {
        Text = text; this.palette = palette; this.selected = selected;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = palette.Surface; ForeColor = selected ? palette.Accent : palette.Muted;
        Cursor = Cursors.Hand; UseVisualStyleBackColor = false; AccessibleName = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? palette.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Drawing.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 7 * DeviceDpi / 96f);
        using var fill = new SolidBrush(selected || hovered ? Drawing.Blend(palette.Accent, palette.Surface, selected ? .12 : .055) : palette.Surface);
        e.Graphics.FillPath(fill, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, selected ? palette.Accent : palette.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}

internal sealed class SpendGraph : RoundedCard
{
    private readonly double[] values;
    private readonly ReportingRange range;
    private readonly Font titleFont, detailFont;
    private readonly ToolTip tooltip = new();
    private int hovered = -1;
    public SpendGraph(double[] values, ReportingRange range, Palette palette, Font titleFont, Font detailFont) : base(palette)
    {
        this.values = values; this.range = range; this.titleFont = titleFont; this.detailFont = detailFont;
        AccessibleDescription = "Per-period API spend. Peak " + Format.Money(values.DefaultIfEmpty(0).Max());
    }
    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, "Spend over time", titleFont, new Point(S(18), S(14)), Palette.Text);
        var subtitle = range.Period switch { UsagePeriod.Day => "Hourly", UsagePeriod.Year => "Monthly", _ => "Daily" };
        var peak = values.DefaultIfEmpty(0).Max();
        var peakText = "Peak " + Format.Money(peak);
        TextRenderer.DrawText(g, peakText, detailFont, new Rectangle(S(18), S(16), Width - S(36), S(18)), Palette.Muted, TextFormatFlags.Right);
        TextRenderer.DrawText(g, subtitle, detailFont, new Point(S(18), S(37)), Palette.Muted);
        var plot = new RectangleF(S(58), S(64), Math.Max(1, Width - S(80)), Math.Max(1, Height - S(97)));
        var ceiling = peak > 0 ? Math.Ceiling(peak / Math.Pow(10, Math.Floor(Math.Log10(peak))) * 5) / 5 * Math.Pow(10, Math.Floor(Math.Log10(peak))) : 1;
        using var grid = new Pen(Drawing.Blend(Palette.Text, Palette.Surface, .08));
        for (var i = 0; i < 3; i++)
        {
            var y = plot.Top + i * plot.Height / 2;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            var label = i == 2 ? "$0" : Format.Money(ceiling * (1 - i / 2.0));
            TextRenderer.DrawText(g, label, detailFont, new Rectangle(S(8), (int)y - S(8), S(42), S(18)), Palette.Muted, TextFormatFlags.Right | TextFormatFlags.SingleLine);
        }
        var step = plot.Width / Math.Max(1, values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var height = (float)(Math.Max(0, values[i]) / ceiling * plot.Height);
            if (height <= 0) continue;
            var bounds = new RectangleF(plot.Left + step * i + step * .16f, plot.Bottom - Math.Max(S(2), height), step * .68f, Math.Max(S(2), height));
            using var bar = new SolidBrush(i == hovered ? Palette.Accent : Drawing.Blend(Palette.Accent, Palette.Surface, .78));
            using var shape = Drawing.Rounded(bounds, Math.Min(S(3), bounds.Width / 2));
            g.FillPath(bar, shape);
        }
        var ticks = range.Period == UsagePeriod.Day ? new[] { 0, 6, 12, 18, 24 } : new[] { 0, values.Length / 2, values.Length - 1 };
        foreach (var index in ticks)
        {
            var text = range.Period == UsagePeriod.Day ? $"{index:00}:00" : range.Labels[index];
            var x = plot.Left + plot.Width * index / (range.Period == UsagePeriod.Day ? 24f : Math.Max(1, values.Length - 1));
            var size = TextRenderer.MeasureText(text, detailFont);
            TextRenderer.DrawText(g, text, detailFont, new Point((int)Math.Clamp(x - size.Width / 2f, plot.Left, plot.Right - size.Width), (int)plot.Bottom + S(10)), Palette.Muted);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plotWidth = Math.Max(1, Width - S(80));
        var index = e.X < S(58) || e.X > Width - S(22) ? -1 : Math.Clamp((int)((e.X - S(58)) / (float)plotWidth * values.Length), 0, values.Length - 1);
        if (index == hovered) return;
        hovered = index;
        tooltip.SetToolTip(this, index >= 0 ? range.Labels[index] + ": " + Format.Money(values[index]) : null);
        Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e) { hovered = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void Dispose(bool disposing) { if (disposing) tooltip.Dispose(); base.Dispose(disposing); }
}
