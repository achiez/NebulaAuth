using System;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NebulaAuth.Core;
using NebulaAuth.Theme;

namespace NebulaAuth.View.Dialogs;

public partial class UpdateDialog : UserControl
{
    /// <summary>
    ///     Installed version without trailing zero parts ("1.8.3.0" -> "1.8.3").
    /// </summary>
    public static readonly FuncValueConverter<Version?, string?> VersionTextConverter = new(v =>
    {
        if (v == null) return null;
        if (v.Revision > 0) return v.ToString();
        return v.Build >= 0 ? v.ToString(3) : v.ToString(2);
    });

    /// <summary>
    ///     URL without the scheme, for the caption under "Update now".
    /// </summary>
    public static readonly FuncValueConverter<string?, string?> UrlTextConverter = new(url =>
    {
        if (string.IsNullOrEmpty(url)) return url;
        var i = url.IndexOf("://", StringComparison.Ordinal);
        return i >= 0 ? url[(i + 3)..] : url;
    });

    public UpdateDialog()
    {
        InitializeComponent();
        ApplyBadgeForegrounds();
    }

    /// <summary>
    ///     Badge text is white where it is readable, otherwise the palette's dark shadow color
    ///     (e.g. FIX on yellow Warning, NEW on green Success).
    ///     The theme can't change while this dialog is open (settings is a dialog too), so this runs once.
    /// </summary>
    private void ApplyBadgeForegrounds()
    {
        var palette = ThemeManager.CurrentPalette;
        if (palette == null) return;
        SetBadgeForeground("UpdateBadgeOnPrimaryBrush", palette.Primary, palette);
        SetBadgeForeground("UpdateBadgeOnSuccessBrush", palette.Success, palette);
        SetBadgeForeground("UpdateBadgeOnWarningBrush", palette.Warning, palette);
        SetBadgeForeground("UpdateBadgeOnAccentBrush", palette.Accent, palette);
        SetBadgeForeground("UpdateBadgeOnErrorBrush", palette.Error, palette);
    }

    private void SetBadgeForeground(string key, Color background, ThemePalette palette)
    {
        Resources[key] = new SolidColorBrush(ColorMath.Readable(background, Colors.White, palette.BaseShadow));
    }
}
