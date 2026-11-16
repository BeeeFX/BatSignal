using System.Drawing.Text;
using Microsoft.Win32;
using BatSignal.Core.Hidpp;

namespace BatSignal.App;

internal sealed record Palette(
    bool Dark, Color Text, Color Secondary, Color Muted, Color Background, Color Surface, Color Border,
    Color Track, Color Good, Color Warn, Color Low, Color Hover);

/// <summary>Windows 11 colors for the taskbar / flyout, following the system light/dark setting.</summary>
internal static class Theme
{
    static readonly Palette DarkPalette = new(true,
        Text: Color.White, Secondary: Color.FromArgb(0xC5, 0xC5, 0xC5), Muted: Color.FromArgb(0x9A, 0x9A, 0x9A),
        Background: Color.FromArgb(0x2B, 0x2B, 0x2B), Surface: Color.FromArgb(0x38, 0x38, 0x38), Border: Color.FromArgb(0x45, 0x45, 0x45),
        Track: Color.FromArgb(0x48, 0x48, 0x48), Good: Color.FromArgb(0x6C, 0xCB, 0x5F), Warn: Color.FromArgb(0xFC, 0xE1, 0x00),
        Low: Color.FromArgb(0xFF, 0x99, 0xA4), Hover: Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    static readonly Palette LightPalette = new(false,
        Text: Color.FromArgb(0x1B, 0x1B, 0x1B), Secondary: Color.FromArgb(0x5C, 0x5C, 0x5C), Muted: Color.FromArgb(0x8A, 0x8A, 0x8A),
        Background: Color.FromArgb(0xF9, 0xF9, 0xF9), Surface: Color.FromArgb(0xEE, 0xEE, 0xEE), Border: Color.FromArgb(0xE0, 0xE0, 0xE0),
        Track: Color.FromArgb(0xE3, 0xE3, 0xE3), Good: Color.FromArgb(0x0F, 0x7B, 0x0F), Warn: Color.FromArgb(0x9D, 0x5D, 0x00),
        Low: Color.FromArgb(0xC4, 0x2B, 0x1C), Hover: Color.FromArgb(0x10, 0, 0, 0));

    public static Palette Current => SystemUsesDarkTheme() ? DarkPalette : LightPalette;

    static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 0;
        }
        catch
        {
            return true;
        }
    }

    public static Color LevelColor(this Palette p, int percent, int threshold) =>
        percent <= threshold ? p.Low : percent <= Math.Max(30, threshold + 10) ? p.Warn : p.Good;

    // Fonts ---------------------------------------------------------------------------------

    static readonly HashSet<string> Installed =
        new InstalledFontCollection().Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10.</summary>
    public static readonly string IconFont =
        Installed.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static readonly string UiFont =
        Installed.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    public static readonly string UiSemibold =
        Installed.Contains("Segoe UI Variable Text Semibold") ? "Segoe UI Variable Text Semibold"
        : Installed.Contains("Segoe UI Semibold") ? "Segoe UI Semibold" : "Segoe UI";

    public static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse or DeviceKind.Trackball or DeviceKind.Touchpad => "",
        DeviceKind.Keyboard or DeviceKind.Numpad => "",
        DeviceKind.Gamepad => "",
        DeviceKind.Headset => "",
        _ => "",
    };

    public const string GlyphRefresh = "";
    public const string GlyphSettings = "";
    public const string GlyphCharging = "";
}
