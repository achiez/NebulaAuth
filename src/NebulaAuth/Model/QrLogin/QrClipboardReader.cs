using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NebulaAuth.Utility;

namespace NebulaAuth.Model.QrLogin;

public enum QrClipboardError
{
    None,
    ClipboardEmpty,
    NoImage,
    InvalidImage,
    NoQrCode,
    MultipleQrCodes,
    NotSteamQrCode
}

/// <param name="Challenge">Valid when <paramref name="Error" /> is <see cref="QrClipboardError.None" />.</param>
public readonly record struct QrClipboardResult(QrClipboardError Error, SteamQrChallenge Challenge = default)
{
    public bool Success => Error == QrClipboardError.None;
}

/// <summary>
///     Reads the Steam login QR code from the clipboard image. No network access: every failure is reported as a
///     <see cref="QrClipboardError" />.
/// </summary>
public static class QrClipboardReader
{
    public static async Task<QrClipboardResult> ReadAsync()
    {
        var clipboard = await ClipboardHelper.GetBitmapAsync();
        switch (clipboard.Status)
        {
            case ClipboardBitmapStatus.Empty:
                return new QrClipboardResult(QrClipboardError.ClipboardEmpty);
            case ClipboardBitmapStatus.NoImage:
                return new QrClipboardResult(QrClipboardError.NoImage);
            case ClipboardBitmapStatus.InvalidImage:
                return new QrClipboardResult(QrClipboardError.InvalidImage);
        }

        using var bitmap = clipboard.Bitmap!;
        QrDecodeResult decoded;
        try
        {
            decoded = await Task.Run(() => Decode(bitmap));
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, "Failed to scan the clipboard image for a QR code");
            return new QrClipboardResult(QrClipboardError.InvalidImage);
        }

        // Diagnostics without the QR content (it identifies the login attempt)
        Shell.Logger.Info("QR login: clipboard image {width}x{height} ({format}): {status}", bitmap.PixelSize.Width,
            bitmap.PixelSize.Height, bitmap.Format?.ToString() ?? "unknown format", decoded.Status);
        return FromDecoded(decoded);
    }

    /// <summary>Maps a decoder result to the QR login result (the text must be a Steam login challenge URL).</summary>
    public static QrClipboardResult FromDecoded(QrDecodeResult decoded)
    {
        return decoded.Status switch
        {
            QrDecodeStatus.Single => SteamQrChallenge.TryParse(decoded.Text, out var challenge)
                ? new QrClipboardResult(QrClipboardError.None, challenge)
                : new QrClipboardResult(QrClipboardError.NotSteamQrCode),
            QrDecodeStatus.Multiple => new QrClipboardResult(QrClipboardError.MultipleQrCodes),
            QrDecodeStatus.NotFound => new QrClipboardResult(QrClipboardError.NoQrCode),
            _ => new QrClipboardResult(QrClipboardError.InvalidImage)
        };
    }

    /// <summary>Scans an Avalonia bitmap (any pixel format Avalonia can transcode to BGRA).</summary>
    public static QrDecodeResult Decode(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        if (size.Width <= 0 || size.Height <= 0 || (long) size.Width * size.Height > QrImageDecoder.MAX_PIXELS)
            return new QrDecodeResult(QrDecodeStatus.InvalidImage);

        byte[] gray;
        using (var target = new WriteableBitmap(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul))
        using (var buffer = target.Lock())
        {
            bitmap.CopyPixels(buffer);
            unsafe
            {
                var pixels = new ReadOnlySpan<byte>((void*) buffer.Address, buffer.RowBytes * size.Height);
                gray = QrImageDecoder.BgraToGray(pixels, size.Width, size.Height, buffer.RowBytes);
            }
        }

        return QrImageDecoder.Decode(gray, size.Width, size.Height);
    }
}
