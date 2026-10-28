using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace LiteShot;

/// <summary>
/// Reads the Windows personalization and accessibility settings the glass design adapts to:
/// accent color, transparency effects, animation effects, high contrast and light/dark mode.
/// </summary>
internal static class SystemTheme
{
    public static Color Accent { get; private set; } = Color.FromArgb(0, 120, 212);
    /// <summary>Settings › Personalization › Colors › Transparency effects. Off = frosted, near-opaque glass.</summary>
    public static bool TransparencyEnabled { get; private set; } = true;
    /// <summary>Settings › Accessibility › Visual effects › Animation effects. Off = no motion.</summary>
    public static bool AnimationsEnabled { get; private set; } = true;
    public static bool HighContrast => SystemInformation.HighContrast;
    public static bool AppsUseLightTheme { get; private set; } = true;

    static SystemTheme() => Refresh();

    public static void Refresh()
    {
        try
        {
            var ui = new UISettings();
            var c = ui.GetColorValue(UIColorType.Accent);
            Accent = Color.FromArgb(255, c.R, c.G, c.B);
            TransparencyEnabled = ui.AdvancedEffectsEnabled;
            AnimationsEnabled = ui.AnimationsEnabled;
        }
        catch
        {
            AnimationsEnabled = SystemInformation.UIEffectsEnabled;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            AppsUseLightTheme = key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch { AppsUseLightTheme = true; }
    }

    public static bool Dark => !AppsUseLightTheme && !HighContrast;
}
