using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NebulaAuth.Theme;

/// <summary>
///     Builds the main window icon: the app lock icon, optionally with a colored dot in the bottom-right corner
///     (<c>Settings.IconColor</c>). Avalonia has no taskbar overlay API, so the dot is part of the icon.
/// </summary>
public static class WindowIconComposer
{
    private const int ICON_SIZE = 256;
    private static readonly Uri IconUri = new("avares://NebulaAuth/Theme/lock.ico");
    private static Bitmap? _baseIcon;

    /// <summary>
    ///     Returns the plain app icon when <paramref name="dotColor" /> is null, otherwise the icon with a colored dot.
    /// </summary>
    public static WindowIcon Create(Color? dotColor)
    {
        if (dotColor == null)
        {
            using var stream = AssetLoader.Open(IconUri);
            return new WindowIcon(stream);
        }

        var baseIcon = _baseIcon ??= LoadBaseIcon();
        using var target = new RenderTargetBitmap(new PixelSize(ICON_SIZE, ICON_SIZE), new Vector(96, 96));
        using (var context = target.CreateDrawingContext(true))
        {
            context.DrawImage(baseIcon, new Rect(0, 0, ICON_SIZE, ICON_SIZE));
            // Proportions of a 14px overlay on a 32px taskbar icon: a large dot in the bottom-right corner
            // with a dark ring so it stays visible on any taskbar color
            const double radius = ICON_SIZE * 0.23;
            var center = new Point(ICON_SIZE - radius - 4, ICON_SIZE - radius - 4);
            var ring = new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x11, 0x19)), ICON_SIZE * 0.045);
            context.DrawEllipse(new SolidColorBrush(dotColor.Value), ring, center, radius, radius);
        }

        return new WindowIcon(target);
    }

    private static Bitmap LoadBaseIcon()
    {
        using var stream = AssetLoader.Open(IconUri);
        return new Bitmap(stream);
    }
}
