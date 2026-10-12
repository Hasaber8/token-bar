using System.Runtime.InteropServices;

namespace TokenBar.App;

internal static class TrayIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var bars = new SolidBrush(Color.FromArgb(88, 166, 255));
        graphics.FillRectangle(bars, 3, 19, 6, 10);
        graphics.FillRectangle(bars, 13, 12, 6, 17);
        graphics.FillRectangle(bars, 23, 3, 6, 26);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
