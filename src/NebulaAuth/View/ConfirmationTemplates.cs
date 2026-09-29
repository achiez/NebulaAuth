using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media.Imaging;
using Avalonia.Metadata;
using Avalonia.Platform;
using NebulaAuth.Model;

namespace NebulaAuth.View;

/// <summary>
///     Item template of the confirmations list (View/ConfirmationTemplates.axaml). Uses the first template whose
///     <c>DataType</c> matches: Avalonia matches data templates with <c>IsInstanceOfType</c>, so
///     derived confirmation types are listed before the base <c>Confirmation</c> template.
/// </summary>
public class ConfirmationTemplateSelector : IDataTemplate
{
    [Content] public List<IDataTemplate> Templates { get; } = [];

    public Control? Build(object? param)
    {
        return Templates.FirstOrDefault(t => t.Match(param))?.Build(param);
    }

    public bool Match(object? data)
    {
        return Templates.Any(t => t.Match(data));
    }
}

/// <summary>
///     <c>view:RemoteImage.Source="https://..."</c> on an <see cref="Image" />: downloads and shows a web image (trade partner
///     avatar, market item icon). Avalonia <c>Image.Source</c> does not load http(s) URIs. <c>avares://</c> URIs are
///     loaded from the embedded resources.
///     Images are decoded small (they are shown at 24-32 px) and cached per URI.
/// </summary>
public static class RemoteImage
{
    private const int DECODE_WIDTH = 64;
    private const int MAX_CACHE_ENTRIES = 256;

    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Source", typeof(RemoteImage));

    private static readonly HttpClient Http = new() {Timeout = TimeSpan.FromSeconds(20)};
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Cache = new();

    static RemoteImage()
    {
        SourceProperty.Changed.AddClassHandler<Image>(OnSourceChanged);
    }

    public static string? GetSource(Image image)
    {
        return image.GetValue(SourceProperty);
    }

    public static void SetSource(Image image, string? value)
    {
        image.SetValue(SourceProperty, value);
    }

    private static async void OnSourceChanged(Image image, AvaloniaPropertyChangedEventArgs e)
    {
        image.Source = null;
        if (e.NewValue is not string uri || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return;
        }

        // Embedded images (debug demo data) are loaded directly
        if (parsed.Scheme == "avares")
        {
            image.Source = LoadAsset(parsed);
            return;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            return;
        }

        var bitmap = await Load(uri);
        if (bitmap != null && GetSource(image) == uri)
        {
            image.Source = bitmap;
        }
    }

    private static Bitmap? LoadAsset(Uri uri)
    {
        try
        {
            using var stream = AssetLoader.Open(uri);
            return Bitmap.DecodeToWidth(stream, DECODE_WIDTH);
        }
        catch (Exception ex)
        {
            Shell.Logger.Debug(ex, "Can't load confirmation image {uri}", uri);
            return null;
        }
    }

    private static Task<Bitmap?> Load(string uri)
    {
        if (Cache.Count > MAX_CACHE_ENTRIES) Cache.Clear();
        return Cache.GetOrAdd(uri, static u => Task.Run(() => Download(u)));
    }

    private static async Task<Bitmap?> Download(string uri)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(uri);
            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, DECODE_WIDTH);
        }
        catch (Exception ex)
        {
            Shell.Logger.Debug(ex, "Can't load confirmation image {uri}", uri);
            Cache.TryRemove(uri, out _); // retry next time
            return null;
        }
    }
}
