using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NebulaAuth.Model;

namespace NebulaAuth.Converters.Background;

/// <summary>
///     BackgroundMode -> Bitmap. Custom: <c>Background.png</c> in the current directory (portable layout),
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
        if (value is BackgroundMode.Custom && File.Exists(CUSTOM_BACKGROUND_FILE))
        {
            try
            {
                // Read into memory so the file is not locked and a replaced file is picked up on the next conversion
                using var stream = new MemoryStream(File.ReadAllBytes(Path.GetFullPath(CUSTOM_BACKGROUND_FILE)));
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
