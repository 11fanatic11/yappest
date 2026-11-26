using System.Windows;
using System.Windows.Media;
using Yappest.Core;

namespace Yappest.UI;

public static class ThemeService
{
    public static readonly string DefaultAccent = "#FCC603";

    public static readonly string[] Presets =
    {
        "#FCC603", "#FF8A3D", "#FF4F5E", "#E86AC8", "#9B6BFF", "#4C8DFF", "#2CC9D6", "#4FD98A",
    };

    public static bool TryParse(string hex, out Color c)
    {
        try { c = (Color)ColorConverter.ConvertFromString(hex); return true; }
        catch { c = default; return false; }
    }

    public static void Apply(string hex)
    {
        if (!TryParse(hex, out var c)) { hex = DefaultAccent; TryParse(hex, out c); }
        var res = System.Windows.Application.Current.Resources;

        // legible text on top of the accent
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        var fg = lum > 0.55 ? Colors.Black : Colors.White;

        var hover = Color.FromRgb(
            (byte)Math.Min(255, c.R + (255 - c.R) * 0.22),
            (byte)Math.Min(255, c.G + (255 - c.G) * 0.22),
            (byte)Math.Min(255, c.B + (255 - c.B) * 0.22));

        res["AccentColor"] = c;
        res["AccentBrush"] = Freeze(new SolidColorBrush(c));
        res["AccentFgBrush"] = Freeze(new SolidColorBrush(fg));
        res["AccentSoftBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x29, c.R, c.G, c.B)));
        res["AccentHoverBrush"] = Freeze(new SolidColorBrush(hover));
    }

    static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}
