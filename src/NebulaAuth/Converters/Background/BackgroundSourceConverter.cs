using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NebulaAuth.Core;
using NebulaAuth.Model;

namespace NebulaAuth.Converters.Background;

/// <summary>
///     BackgroundMode -> Bitmap. Custom: <c>Background.png</c> in the data folder (<see cref="DataRoot" />),
///     otherwise (or when that file is missing/unreadable) the embedded default background.
/// </summary>
public class BackgroundSourceConverter : IValueConverter
{
    private const string CUSTOM_BACKGROUND_FILE = "Background.png";

    // Decoding the 2880px source to this width keeps the image sharp on large windows but saves memory
    private const int DECODE_WIDTH = 1920;

    private static readonly Uri DefaultUri = new("avares://NebulaAuth/Theme/Background.png");
    private static Bitmap? _default;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var customFile = value is BackgroundMode.Custom ? DataRoot.Combine(CUSTOM_BACKGROUND_FILE) : null;
        if (customFile != null && File.Exists(customFile))
        {
            try
            {
                // Read into memory so the file is not locked and a replaced file is picked up on the next conversion
                using var stream = new MemoryStream(File.ReadAllBytes(customFile));
                return Bitmap.DecodeToWidth(stream, DECODE_WIDTH);
            }
            catch (Exception ex)
            {
                Shell.Logger.Warn(ex, "Can't load the custom background image");
            }
        }

        if (_default == null)
        {
            using var stream = AssetLoader.Open(DefaultUri);
            _default = Bitmap.DecodeToWidth(stream, DECODE_WIDTH);
        }

        return _default;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
