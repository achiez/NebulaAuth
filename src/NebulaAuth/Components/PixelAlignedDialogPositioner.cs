using System;
using Avalonia;
using DialogHostAvalonia.Positioners;
using NebulaAuth.Core;

namespace NebulaAuth;

/// <summary>
///     <see cref="AlignmentDialogPopupPositioner" /> whose result starts on a whole device pixel.
///     Centering produces fractional positions (e.g. 12.5 DIP = 15.625 px at 125 %): every 1 px line inside the dialog
///     (input borders, focus rings, dividers) then straddles two pixels on one axis and looks 2 px wide and dimmer there,
///     while the other axis stays crisp. Layout rounding inside the dialog only works when its origin is pixel-aligned.
/// </summary>
public class PixelAlignedDialogPositioner : AlignmentDialogPopupPositioner, IDialogPopupPositioner
{
    Rect IDialogPopupPositioner.Update(Size anchorRectangle, Size size)
    {
        var rect = Update(anchorRectangle, size);
        var scaling = AppHost.MainWindow?.RenderScaling is > 0 and var s ? s : 1;
        return new Rect(Snap(rect.X, scaling), Snap(rect.Y, scaling), rect.Width, rect.Height);
    }

    private static double Snap(double value, double scaling)
    {
        return Math.Round(value * scaling) / scaling;
    }
}
