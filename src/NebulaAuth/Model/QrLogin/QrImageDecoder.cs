using System;
using System.Collections.Generic;
using System.Linq;
using ZXing;
using ZXing.Common;
using ZXing.Multi.QrCode;
using ZXing.QrCode;

namespace NebulaAuth.Model.QrLogin;

public enum QrDecodeStatus
{
    /// <summary>Exactly one QR code was found (<see cref="QrDecodeResult.Text" />).</summary>
    Single,
    NotFound,
    Multiple,

    /// <summary>The image is empty or too large to scan.</summary>
    InvalidImage
}

public readonly record struct QrDecodeResult(QrDecodeStatus Status, string? Text = null);

/// <summary>
///     Finds QR codes in a grayscale image (managed ZXing decoder, no platform code). Exactly one code is expected:
///     after each found code its area is blanked and the image is scanned again, so a second code is reported even when
///     the multi-code reader misses it. Light-on-dark codes are tried (inverted image) when nothing is found.
/// </summary>
public static class QrImageDecoder
{
    /// <summary>A 8K screenshot is ~33 MP; larger clipboard images are rejected (memory).</summary>
    public const long MAX_PIXELS = 50_000_000;

    private const int MIN_SIZE = 21; // smallest QR code (version 1) without quiet zone, one pixel per module
    private const int MAX_PASSES = 3;

    /// <summary>Part of the found code's size added around it when it is blanked (finder centers are ~3.5 modules in).</summary>
    private const double BLANK_MARGIN = 0.3;

    private static readonly Dictionary<DecodeHintType, object> Hints = new()
    {
        [DecodeHintType.TRY_HARDER] = true,
        [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> {BarcodeFormat.QR_CODE}
    };

    /// <param name="gray">Luminance, one byte per pixel, rows without padding (<paramref name="width" /> bytes each).</param>
    public static QrDecodeResult Decode(byte[] gray, int width, int height)
    {
        if (width < MIN_SIZE || height < MIN_SIZE || (long) width * height > MAX_PIXELS ||
            gray.Length < (long) width * height)
            return new QrDecodeResult(QrDecodeStatus.InvalidImage);

        var found = FindAll((byte[]) gray.Clone(), width, height);
        if (found.Count == 0)
            found = FindAll(Invert(gray, width * height), width, height);

        return found.Count switch
        {
            0 => new QrDecodeResult(QrDecodeStatus.NotFound),
            1 => new QrDecodeResult(QrDecodeStatus.Single, found[0]),
            _ => new QrDecodeResult(QrDecodeStatus.Multiple)
        };
    }

    /// <summary>
    ///     Converts 32-bit BGRA pixels (unpremultiplied alpha, <paramref name="stride" /> bytes per row) to luminance over a
    ///     white background (transparent screenshot areas become white).
    /// </summary>
    public static byte[] BgraToGray(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var gray = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            var offset = y * width;
            for (var x = 0; x < width; x++)
            {
                var p = x * 4;
                int b = row[p], g = row[p + 1], r = row[p + 2], a = row[p + 3];
                var lum = (r * 299 + g * 587 + b * 114) / 1000;
                gray[offset + x] = (byte) ((lum * a + 255 * (255 - a)) / 255);
            }
        }

        return gray;
    }

    /// <returns>Texts of the codes found, at most 2 (enough to report "several codes").</returns>
    private static List<string> FindAll(byte[] gray, int width, int height)
    {
        var texts = new List<string>();
        for (var pass = 0; pass < MAX_PASSES && texts.Count < 2; pass++)
        {
            var results = FindPass(gray, width, height);
            if (results.Count == 0) break;
            foreach (var result in results)
            {
                texts.Add(result.Text);
                Blank(gray, width, height, result.ResultPoints);
            }
        }

        return texts;
    }

    private static List<Result> FindPass(byte[] gray, int width, int height)
    {
        var source = new RGBLuminanceSource(gray, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var bitmap = new BinaryBitmap(new HybridBinarizer(source));

        var multi = new QRCodeMultiReader().decodeMultiple(bitmap, Hints);
        var results = multi?.Where(r => r is {Text: not null, ResultPoints.Length: >= 3}).ToList() ?? [];
        if (results.Count > 0) return Distinct(results);

        var single = new QRCodeReader().decode(bitmap, Hints);
        return single is {Text: not null, ResultPoints.Length: >= 3} ? [single] : [];
    }

    /// <summary>The multi reader can report one code twice (same text at the same place).</summary>
    private static List<Result> Distinct(List<Result> results)
    {
        var distinct = new List<Result>();
        foreach (var result in results)
        {
            var (cx, cy, size) = Box(result.ResultPoints);
            var duplicate = distinct.Any(d =>
            {
                var (dx, dy, dSize) = Box(d.ResultPoints);
                return d.Text == result.Text && Math.Abs(dx - cx) < Math.Max(size, dSize) / 2 &&
                       Math.Abs(dy - cy) < Math.Max(size, dSize) / 2;
            });
            if (!duplicate) distinct.Add(result);
        }

        return distinct;
    }

    private static (float CenterX, float CenterY, float Size) Box(ResultPoint[] points)
    {
        float minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        float minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        return ((minX + maxX) / 2, (minY + maxY) / 2, Math.Max(maxX - minX, maxY - minY));
    }

    /// <summary>Paints the area of a found code white (dark-on-light image) so the next pass looks for other codes.</summary>
    private static void Blank(byte[] gray, int width, int height, ResultPoint[] points)
    {
        var (cx, cy, size) = Box(points);
        var half = size / 2 + size * BLANK_MARGIN + 2;
        var x0 = Math.Clamp((int) (cx - half), 0, width);
        var x1 = Math.Clamp((int) Math.Ceiling(cx + half), 0, width);
        var y0 = Math.Clamp((int) (cy - half), 0, height);
        var y1 = Math.Clamp((int) Math.Ceiling(cy + half), 0, height);
        for (var y = y0; y < y1; y++)
            gray.AsSpan(y * width + x0, x1 - x0).Fill(255);
    }

    private static byte[] Invert(byte[] gray, int length)
    {
        var inverted = new byte[length];
        for (var i = 0; i < length; i++) inverted[i] = (byte) (255 - gray[i]);
        return inverted;
    }
}
