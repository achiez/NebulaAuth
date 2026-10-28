using System;
using System.Globalization;
using Avalonia.Media;
using Newtonsoft.Json;

namespace NebulaAuth.Utility;

/// <summary>
///     Serializes <see cref="Color" /> / <c>Color?</c> exactly like WPF <c>System.Windows.Media.Color</c> was serialized
///     by Newtonsoft through its TypeConverter: <c>"#AARRGGBB"</c> (upper-case hex), <c>null</c> when unset.
///     Reading accepts anything <see cref="Color.TryParse(string, out Color)" /> understands (#AARRGGBB, #RRGGBB, names);
///     an unparsable value (also an object or array, which is skipped entirely) is read as null (logged) instead of
///     failing the whole settings file.
/// </summary>
public class HexColorJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(Color) || objectType == typeof(Color?);
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not Color c)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteValue(ToHex(c));
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer)
    {
        var nullable = objectType == typeof(Color?);
        if (reader.TokenType == JsonToken.Null)
        {
            return nullable ? null : default(Color);
        }

        if (reader.TokenType is JsonToken.StartObject or JsonToken.StartArray or JsonToken.StartConstructor)
        {
            // Consume the whole value: the reader must stop on its last token, otherwise the serializer reads the
            // object's inner tokens as the next settings and every following property is lost
            var token = reader.TokenType;
            reader.Skip();
            NebulaAuth.Model.Shell.Logger?.Warn("Can't parse color value: unexpected {token}", token);
            return nullable ? null : default(Color);
        }

        var str = reader.TokenType == JsonToken.String
            ? (string?) reader.Value
            : Convert.ToString(reader.Value, CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(str) && Color.TryParse(str.Trim(), out var color))
        {
            return color;
        }

        NebulaAuth.Model.Shell.Logger?.Warn("Can't parse color value {value}", str);
        return nullable ? null : default(Color);
    }

    public static string ToHex(Color c)
    {
        return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
