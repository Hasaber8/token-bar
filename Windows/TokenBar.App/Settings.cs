using System.Text.Json;
using TokenBar.Core;

namespace TokenBar.App;

internal sealed class Settings
{
    public UsagePeriod Period { get; set; }
    public bool Relative { get; set; }
    public bool ShowGraph { get; set; } = true;
    public bool ShowProjects { get; set; }
    public string Theme { get; set; } = "System";
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenBar", "settings.json");
    public static Settings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            if (!Enum.IsDefined(settings.Period)) settings.Period = UsagePeriod.Day;
            if (!Palette.Names.Contains(settings.Theme)) settings.Theme = "System";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
            File.Move(FilePath + ".tmp", FilePath, true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Could not save settings: " + ex.Message; }
    }
}

internal record Palette(Color Background, Color Surface, Color Text, Color Muted, Color Accent)
{
    public static readonly string[] Names = ["System", "Catppuccin Mocha", "Dracula", "Gruvbox Dark", "Nord", "Solarized Dark", "Tokyo Night", "Catppuccin Latte", "GitHub Light", "Gruvbox Light", "Solarized Light"];
    public static Palette For(string name)
    {
        static Color C(int hex) => Color.FromArgb((hex >> 16) & 255, (hex >> 8) & 255, hex & 255);
        var colors = name switch
        {
            "Catppuccin Mocha" => new[] { 0x1e1e2e, 0x313244, 0xcdd6f4, 0xa6adc8, 0xcba6f7 },
            "Dracula" => [0x282a36, 0x44475a, 0xf8f8f2, 0xbfbfbf, 0xbd93f9],
            "Gruvbox Dark" => [0x282828, 0x3c3836, 0xebdbb2, 0xa89984, 0xfabd2f],
            "Nord" => [0x2e3440, 0x3b4252, 0xeceff4, 0x81a1c1, 0x88c0d0],
            "Solarized Dark" => [0x002b36, 0x073642, 0x93a1a1, 0x839496, 0x2aa198],
            "Tokyo Night" => [0x1a1b26, 0x24283b, 0xc0caf5, 0xa9b1d6, 0x7aa2f7],
            "Catppuccin Latte" => [0xeff1f5, 0xdce0e8, 0x4c4f69, 0x6c6f85, 0x8839ef],
            "GitHub Light" => [0xffffff, 0xf6f8fa, 0x1f2328, 0x59636e, 0x0969da],
            "Gruvbox Light" => [0xfbf1c7, 0xebdbb2, 0x3c3836, 0x7c6f64, 0xb57614],
            "Solarized Light" => [0xfdf6e3, 0xeee8d5, 0x657b83, 0x839496, 0x268bd2],
            _ => null
        };
        return colors is null ? new(SystemColors.Window, SystemColors.Control, SystemColors.WindowText, SystemColors.GrayText, SystemColors.Highlight)
            : new(C(colors[0]), C(colors[1]), C(colors[2]), C(colors[3]), C(colors[4]));
    }
}
