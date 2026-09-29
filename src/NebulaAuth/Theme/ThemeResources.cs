using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Semi.Avalonia;

namespace NebulaAuth.Theme;

/// <summary>
///     Builds every color resource of the application from a <see cref="ThemePalette" /> and applies it at runtime.
///     <list type="bullet">
///         <item>
///             Palette keys: <c>{Name}Color</c> (Color, replaced on every switch) and
///             <c>{Name}Brush</c> (one SolidColorBrush instance per key, recolored in place), plus <c>MainBackgroundBrush</c>.
///         </item>
///         <item>Derived app brushes (<c>TitleBarBackgroundBrush</c>, <c>DialogBackgroundBrush</c>, ...).</item>
///         <item>
///             Semi.Avalonia semantic tokens (<c>SemiColorPrimary</c>, <c>SemiColorText0</c>, <c>SemiColorFill0</c>, ...):
///             the brush instances owned by Semi are recolored in place. Semi and Ursa control tokens are
///             <c>StaticResource</c> aliases of those instances, so every Semi/Ursa control follows the palette.
///         </item>
///         <item>A few control tokens that Semi hardcodes (selection, tooltip, caret, slider) are overridden app-wide.</item>
///     </list>
///     Because brushes are recolored instead of replaced, both <c>{DynamicResource XBrush}</c> and
///     <c>{StaticResource XBrush}</c> follow runtime theme switches. <c>{..Resource XColor}</c> (Color values) only
///     follow switches with DynamicResource.
/// </summary>
public static class ThemeResources
{
    private static readonly ResourceDictionary BrushDictionary = new();
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();
    private static ResourceDictionary? _colorDictionary;
    private static bool _attached;

    public static void Apply(Application app, ThemePalette palette, ThemeVariant variant)
    {
        var dark = variant == ThemeVariant.Dark;
        Attach(app);

        // 1. Palette colors (Color values): swap a whole dictionary so resource consumers get one change notification
        var colors = new ResourceDictionary();
        foreach (var name in ThemePalette.Names)
        {
            colors[name + "Color"] = palette[name];
        }

        var merged = app.Resources.MergedDictionaries;
        var index = _colorDictionary == null ? -1 : merged.IndexOf(_colorDictionary);
        if (index >= 0) merged[index] = colors;
        else merged.Insert(0, colors);
        _colorDictionary = colors;

        // 2. Palette brushes + derived app brushes (instances are created once and recolored)
        foreach (var name in ThemePalette.Names)
        {
            SetBrush(name + "Brush", palette[name]);
        }

        foreach (var (key, color) in ComputeAppBrushes(palette, dark))
        {
            SetBrush(key, color);
        }

        // 3. Semi/Ursa semantic tokens, recolored in place for the variant that is about to be used
        var semi = app.Styles.OfType<SemiTheme>().FirstOrDefault();
        if (semi != null)
        {
            var tokens = ComputeSemiTokens(palette, dark);
            RecolorSemi(semi, variant, tokens);
            if (variant == ThemeVariant.Light)
            {
                // ThemeVariant.Default resolves to Semi's "Default" dictionary (a separate Light instance)
                RecolorSemi(semi, ThemeVariant.Default, tokens);
            }
        }
    }

    /// <summary>
    ///     Returns true when Semi's control tokens really follow the recolored semantic tokens
    ///     (used as a self-check after <see cref="Apply" />).
    /// </summary>
    public static bool VerifySemiMapping(Application app, ThemePalette palette, ThemeVariant variant)
    {
        return app.TryGetResource("ButtonSolidPrimaryBackground", variant, out var value) &&
               value is ISolidColorBrush brush && brush.Color == palette.Primary;
    }

    private static void Attach(Application app)
    {
        if (_attached) return;
        _attached = true;
        // Added last: app brushes and control overrides win over dictionaries merged earlier
        app.Resources.MergedDictionaries.Add(BrushDictionary);
    }

    private static void SetBrush(string key, Color color)
    {
        if (!Brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(color);
            Brushes[key] = brush;
            BrushDictionary[key] = brush;
            return;
        }

        brush.Color = color;
        brush.Opacity = 1;
    }

    private static void RecolorSemi(SemiTheme semi, ThemeVariant variant, Dictionary<string, Color> tokens)
    {
        foreach (var (key, color) in tokens)
        {
            if (semi.TryGetResource(key, variant, out var value) && value is SolidColorBrush brush)
            {
                brush.Color = color;
                brush.Opacity = 1;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Derived app brushes (NebulaAuth names)
    // ---------------------------------------------------------------------------------------------------------------
    private static IEnumerable<(string, Color)> ComputeAppBrushes(ThemePalette p, bool dark)
    {
        var text = p.BaseContent;
        var onPrimary = ColorMath.Readable(p.Primary, p.PrimaryContent, dark ? p.Base200 : Colors.White, 3.5);
        var titleForeground = ColorMath.Readable(p.Base300, p.PrimaryContent, p.BaseContent, 4.5);
        var elevated = dark ? ColorMath.Mix(p.Base100, text, 0.06) : p.Base100;
        // Snackbar: inverted surface (text color as background, surface color as text), so it stands out from every
        // card and input of the theme
        var snackbar = ColorMath.Mix(text, p.Base100, 0.06);
        var snackbarText = ColorMath.Readable(snackbar, p.Base100, dark ? Colors.Black : Colors.White, 4.5);

        // Neutral under its original key
        yield return ("MainBackgroundBrush", p.Neutral);

        // Surfaces
        yield return ("WindowBackgroundBrush", p.Base200);
        yield return ("CardBackgroundBrush", p.Base100);
        yield return ("CardBorderBrush", ColorMath.WithAlpha(text, dark ? 0.10 : 0.14));
        yield return ("ElevatedBackgroundBrush", elevated);
        yield return ("DividerBrush", ColorMath.WithAlpha(text, dark ? 0.12 : 0.14));
        yield return ("HoverFillBrush", ColorMath.WithAlpha(text, dark ? 0.07 : 0.05));
        yield return ("PressedFillBrush", ColorMath.WithAlpha(text, dark ? 0.12 : 0.09));

        // Text levels
        yield return ("TextPrimaryBrush", text);
        yield return ("MutedForegroundBrush", ColorMath.WithAlpha(text, 0.64));
        yield return ("SubtleForegroundBrush", ColorMath.WithAlpha(text, 0.42));

        // Accents
        yield return ("OnPrimaryBrush", onPrimary);
        // Text on a red (danger) button: white reads as "danger" and passes 3:1 on every palette red; black otherwise
        yield return ("OnErrorBrush", ColorMath.Readable(p.Error, Colors.White, Colors.Black, 3.0));
        yield return ("PrimaryTintBrush", ColorMath.WithAlpha(p.Primary, dark ? 0.16 : 0.10));
        yield return ("FocusRingBrush", p.Primary);
        yield return ("LinkBrush", AccentOn(p.Base300, p.Accent));
        yield return ("LinkOnSurfaceBrush", AccentOn(p.Base100, p.Accent));

        // Window chrome
        yield return ("TitleBarBackgroundBrush", p.Base300);
        yield return ("TitleBarForegroundBrush", titleForeground);
        yield return ("CaptionButtonForegroundBrush", ColorMath.WithAlpha(titleForeground, 0.82));
        yield return ("CaptionButtonHoverBrush", ColorMath.WithAlpha(titleForeground, 0.12));
        yield return ("CaptionButtonPressedBrush", ColorMath.WithAlpha(titleForeground, 0.20));
        yield return ("CaptionCloseHoverBrush", Color.FromRgb(0xE8, 0x11, 0x23));
        yield return ("CaptionClosePressedBrush", Color.FromRgb(0xC4, 0x2B, 0x1C));
        yield return ("CaptionCloseForegroundBrush", Colors.White);
        yield return ("FooterBackgroundBrush", p.Base300);
        yield return ("FooterForegroundBrush", ColorMath.Readable(p.Base300, p.SecondaryContent, p.BaseContent, 4.5));

        // Dialogs
        yield return ("DialogBackgroundBrush", dark ? p.Base200 : p.Base100);
        yield return ("DialogBorderBrush", ColorMath.WithAlpha(text, dark ? 0.10 : 0.12));
        yield return ("DialogHeaderBackgroundBrush", p.Base300);
        yield return ("DialogHeaderForegroundBrush", titleForeground);
        yield return ("DialogOverlayBrush", Colors.Black);

        // Snackbar
        yield return ("SnackbarBackgroundBrush", snackbar);
        yield return ("SnackbarForegroundBrush", snackbarText);
        yield return ("SnackbarActionBrush", AccentOn(snackbar, p.Primary));
        yield return ("SnackbarActionHoverBrush", ColorMath.WithAlpha(AccentOn(snackbar, p.Primary), 0.14));
        yield return ("SnackbarBorderBrush", Colors.Transparent);

        // Subtle inputs (TextBox.Subtle / ComboBox.Subtle): thin outline instead of a fill
        yield return ("InputSubtleBorderBrush", ColorMath.WithAlpha(text, dark ? 0.16 : 0.20));
        yield return ("InputSubtleHoverBorderBrush", ColorMath.WithAlpha(text, dark ? 0.30 : 0.34));

        // HintBox (tinted callouts)
        yield return ("InfoTintBrush", ColorMath.WithAlpha(p.Info, dark ? 0.12 : 0.10));
        yield return ("WarningTintBrush", ColorMath.WithAlpha(p.Warning, dark ? 0.12 : 0.12));
        yield return ("ErrorTintBrush", ColorMath.WithAlpha(p.Error, dark ? 0.12 : 0.10));
        yield return ("SuccessTintBrush", ColorMath.WithAlpha(p.Success, dark ? 0.12 : 0.10));
        yield return ("InfoBorderBrush", ColorMath.WithAlpha(p.Info, 0.35));
        yield return ("WarningBorderBrush", ColorMath.WithAlpha(p.Warning, 0.35));
        yield return ("ErrorBorderBrush", ColorMath.WithAlpha(p.Error, 0.35));
        yield return ("SuccessBorderBrush", ColorMath.WithAlpha(p.Success, 0.35));

        // Semi control tokens that Semi hardcodes or points to white/black: overridden app-wide
        yield return ("ButtonSolidForeground", onPrimary);
        yield return ("ButtonSolidPrimaryPressedForeground", onPrimary);
        yield return ("CheckBoxGlyphFill", onPrimary);
        yield return ("ToolTipBackground", dark ? p.BaseContent : p.Base300);
        yield return ("ToolTipForeground", ColorMath.Readable(dark ? p.BaseContent : p.Base300, p.Base100, p.BaseContent, 4.5));
        yield return ("ToolTipBorderBrush", Colors.Transparent);
        yield return ("TextBoxSelectionBackground", ColorMath.WithAlpha(p.Primary, dark ? 0.45 : 0.30));
        yield return ("TextBoxSelectionForeground", text);
        yield return ("TextBlockSelectionBackground", ColorMath.WithAlpha(p.Primary, dark ? 0.45 : 0.30));
        yield return ("TextBoxTextCaretBrush", text);
        // Semi uses the Success color for checked toggle switches; the app uses the primary color like other selections
        yield return ("ToggleSwitchContainerCheckedDefaultBackground", p.Primary);
        yield return ("ToggleSwitchContainerCheckedPointeroverBackground", ColorMath.Shift(p.Primary, dark, 0.14));
        yield return ("ToggleSwitchContainerCheckedPressedBackground", ColorMath.Shift(p.Primary, dark, 0.26));
        yield return ("ToggleSwitchContainerCheckedDisabledBackground", ColorMath.Mix(p.Primary, p.Base100, 0.55));
        yield return ("SliderThumbBackground", dark ? ColorMath.Mix(p.BaseContent, Colors.White, 0.6) : Colors.White);
        yield return ("SliderTickForeground", ColorMath.WithAlpha(text, 0.35));
        yield return ("SliderTrackDisabledForeground", ColorMath.WithAlpha(text, 0.25));
        yield return ("SliderTrackDisabledBackground", ColorMath.WithAlpha(text, 0.08));
        // Semi's track is ~1.1:1 on light cards (almost invisible): use a clearly visible track
        yield return ("SliderTrackBackground", ColorMath.WithAlpha(text, dark ? 0.16 : 0.18));
        yield return ("SliderThumbDisabledBorderBrush", ColorMath.WithAlpha(text, 0.3));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Semi.Avalonia 12 semantic tokens (Tokens/Palette/{Dark,Light}.axaml) mapped onto the palette
    // ---------------------------------------------------------------------------------------------------------------
    private static Dictionary<string, Color> ComputeSemiTokens(ThemePalette p, bool dark)
    {
        var t = new Dictionary<string, Color>();
        var text = p.BaseContent;

        void Family(string name, Color c)
        {
            t[$"SemiColor{name}"] = c;
            t[$"SemiColor{name}Pointerover"] = ColorMath.Shift(c, dark, 0.14);
            t[$"SemiColor{name}Active"] = ColorMath.Shift(c, dark, 0.26);
            t[$"SemiColor{name}Disabled"] = ColorMath.Mix(c, p.Base100, 0.55);
            t[$"SemiColor{name}Light"] = ColorMath.WithAlpha(c, dark ? 0.16 : 0.10);
            t[$"SemiColor{name}LightPointerover"] = ColorMath.WithAlpha(c, dark ? 0.24 : 0.16);
            t[$"SemiColor{name}LightActive"] = ColorMath.WithAlpha(c, dark ? 0.32 : 0.22);
        }

        Family("Primary", p.Primary);
        Family("Secondary", p.Secondary);
        Family("Tertiary", ColorMath.Mix(text, p.Base200, 0.35));
        Family("Information", p.Info);
        Family("Success", p.Success);
        Family("Warning", p.Warning);
        Family("Danger", p.Error);

        // Text and icons
        t["SemiColorText0"] = text;
        t["SemiColorText1"] = ColorMath.WithAlpha(text, 0.82);
        t["SemiColorText2"] = ColorMath.WithAlpha(text, 0.62);
        t["SemiColorText3"] = ColorMath.WithAlpha(text, 0.40);

        // Links
        var link = AccentOn(p.Base100, p.Accent);
        t["SemiColorLink"] = link;
        t["SemiColorLinkPointerover"] = ColorMath.Shift(link, dark, 0.15);
        t["SemiColorLinkActive"] = ColorMath.Shift(link, dark, 0.28);
        t["SemiColorLinkVisited"] = link;

        // Background layers: 0 = window, 1 = card, 2..4 = raised surfaces (dialogs, popups, menus, tags)
        t["SemiColorBackground0"] = p.Base200;
        t["SemiColorBackground1"] = p.Base100;
        t["SemiColorBackground2"] = dark ? ColorMath.Mix(p.Base100, text, 0.04) : p.Base100;
        t["SemiColorBackground3"] = dark ? ColorMath.Mix(p.Base100, text, 0.07) : p.Base100;
        t["SemiColorBackground4"] = dark ? ColorMath.Mix(p.Base100, text, 0.11) : ColorMath.Mix(p.Base100, text, 0.04);
        t["SemiColorNavBackground"] = p.Base100;
        t["SemiColorOverlayBackground"] = ColorMath.WithAlpha(dark ? p.Base200 : p.Base300, 0.6);

        // Fills (neutral tinted surfaces of inputs/buttons) and borders
        t["SemiColorFill0"] = ColorMath.WithAlpha(text, dark ? 0.08 : 0.06);
        t["SemiColorFill1"] = ColorMath.WithAlpha(text, dark ? 0.12 : 0.10);
        t["SemiColorFill2"] = ColorMath.WithAlpha(text, dark ? 0.16 : 0.14);
        t["SemiColorBorder"] = ColorMath.WithAlpha(text, dark ? 0.14 : 0.16);
        t["SemiColorFocusBorder"] = p.Primary;

        // Disabled
        t["SemiColorDisabledText"] = ColorMath.WithAlpha(text, 0.38);
        t["SemiColorDisabledBorder"] = ColorMath.WithAlpha(text, 0.12);
        t["SemiColorDisabledBackground"] = ColorMath.WithAlpha(text, 0.10);
        t["SemiColorDisabledFill"] = ColorMath.WithAlpha(text, 0.04);
        return t;
    }

    /// <summary>
    ///     <paramref name="accent" /> moved toward white/black until it is readable (contrast &gt;= 3) on
    ///     <paramref name="background" />.
    /// </summary>
    private static Color AccentOn(Color background, Color accent)
    {
        var towardWhite = ColorMath.Luminance(background) < 0.4;
        var color = accent;
        for (var step = 1; step <= 6 && ColorMath.Contrast(background, color) < 3.0; step++)
        {
            color = ColorMath.Mix(accent, towardWhite ? Colors.White : Colors.Black, step * 0.1);
        }

        return color;
    }

    internal static ThemeVariant VariantFor(ThemePalette palette)
    {
        return palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
