using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NebulaAuth.Converters.Background;

/// <summary>
///     Blur radius (double) -> <see cref="IEffect" /> for <c>Visual.Effect</c>. Radius &lt;= 0 gives no effect at all,
///     so an unblurred background does not pay for an effect layer.
/// </summary>
public class BlurEffectConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var radius = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0
        };
        return radius > 0.05 ? new ImmutableBlurEffect(radius) : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
