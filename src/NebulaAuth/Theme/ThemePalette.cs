using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace NebulaAuth.Theme;

/// <summary>
///     The 21 colors of one NebulaAuth palette (Theme/Themes/*Theme.axaml).
///     Everything else (brushes, Semi/Ursa tokens, derived app brushes) is computed from it by <see cref="ThemeResources" />.
/// </summary>
public sealed class ThemePalette
{
    /// <summary>
    ///     Palette token names. The resource keys are <c>{Name}Color</c> (Color) and <c>{Name}Brush</c> (SolidColorBrush).
    /// </summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "Primary", "PrimaryContent", "Secondary", "SecondaryContent", "Accent", "AccentContent",
        "Neutral", "NeutralContent", "Base100", "Base200", "Base300", "BaseContent", "BaseShadow",
        "Info", "InfoContent", "Success", "SuccessContent", "Warning", "WarningContent", "Error", "ErrorContent"
    ];

    private readonly Dictionary<string, Color> _colors;

    private ThemePalette(string name, Dictionary<string, Color> colors)
    {
        Name = name;
        _colors = colors;
    }

    public string Name { get; }

    public Color this[string name] => _colors[name];

    public Color Primary => this["Primary"];
    public Color PrimaryContent => this["PrimaryContent"];
    public Color Secondary => this["Secondary"];
    public Color SecondaryContent => this["SecondaryContent"];
    public Color Accent => this["Accent"];
    public Color AccentContent => this["AccentContent"];
    public Color Neutral => this["Neutral"];
    public Color NeutralContent => this["NeutralContent"];
    public Color Base100 => this["Base100"];
    public Color Base200 => this["Base200"];
    public Color Base300 => this["Base300"];
    public Color BaseContent => this["BaseContent"];
    public Color BaseShadow => this["BaseShadow"];
    public Color Info => this["Info"];
    public Color InfoContent => this["InfoContent"];
    public Color Success => this["Success"];
    public Color SuccessContent => this["SuccessContent"];
    public Color Warning => this["Warning"];
    public Color WarningContent => this["WarningContent"];
    public Color Error => this["Error"];
    public Color ErrorContent => this["ErrorContent"];

    /// <summary>
    ///     True when the palette is dark (decided by the luminance of the main surface <c>Base100</c>).
    ///     Light and Shadcn are light palettes, Default, Black and Luxury are dark.
    /// </summary>
    public bool IsDark => ColorMath.Luminance(Base100) < 0.4;

    /// <summary>
    ///     Loads <c>avares://NebulaAuth/Theme/Themes/{themeName}.axaml</c>.
    /// </summary>
    public static ThemePalette Load(string themeName)
    {
        var dict = (ResourceDictionary) AvaloniaXamlLoader.Load(
            new Uri($"avares://NebulaAuth/Theme/Themes/{themeName}.axaml"));
        var colors = new Dictionary<string, Color>();
        foreach (var name in Names)
        {
            if (!dict.TryGetResource(name + "Color", null, out var value) || value is not Color color)
            {
                throw new InvalidOperationException($"Palette '{themeName}' has no color '{name}Color'");
            }

            colors[name] = color;
        }

        return new ThemePalette(themeName, colors);
    }
}

/// <summary>
///     Small color helpers used to derive hover/pressed/tint tokens from a palette.
/// </summary>
public static class ColorMath
{
    public static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (byte) Math.Round(from.A + (to.A - from.A) * amount),
            (byte) Math.Round(from.R + (to.R - from.R) * amount),
            (byte) Math.Round(from.G + (to.G - from.G) * amount),
            (byte) Math.Round(from.B + (to.B - from.B) * amount));
    }

    public static Color WithAlpha(Color color, double alpha)
    {
        return Color.FromArgb((byte) Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);
    }

    /// <summary>
    ///     WCAG relative luminance, 0..1.
    /// </summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>
    ///     WCAG contrast ratio, 1..21.
    /// </summary>
    public static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    ///     Returns <paramref name="preferred" /> when it is readable on <paramref name="background" />
    ///     (contrast &gt;= <paramref name="minContrast" />), otherwise the more readable of the candidates.
    /// </summary>
    public static Color Readable(Color background, Color preferred, Color alternative, double minContrast = 3.0)
    {
        if (Contrast(background, preferred) >= minContrast) return preferred;
        return Contrast(background, alternative) > Contrast(background, preferred) ? alternative : preferred;
    }

    /// <summary>
    ///     Moves the color toward white on dark themes and toward black on light themes (hover/pressed states).
    /// </summary>
    public static Color Shift(Color color, bool dark, double amount)
    {
        return Mix(color, dark ? Colors.White : Colors.Black, amount);
    }
}
