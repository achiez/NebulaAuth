using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Metadata;
using Avalonia.Threading;
using CodingSeb.Localization;

// {Tr ...} is usable in every .axaml file without an xmlns prefix.
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "NebulaAuth.Localization")]

namespace NebulaAuth.Localization;

/// <summary>
///     Localization markup extension: <c>{Tr Some.Key}</c> (follows runtime language switches) and
///     <c>{Tr Some.Key, IsDynamic=False}</c> (translated once, when the element is created).
///     Works on any Avalonia property (Text, Content, Header, ToolTip.Tip, Run.Text, PlaceholderText, Watermark,
///     custom styled properties such as DialogHeader.Title), in DataTemplates and in style setters.
///     A missing key renders the key itself (CodingSeb default).
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string textId)
    {
        TextId = textId;
    }

    /// <summary>
    ///     Translation key, e.g. <c>Common.OK</c>.
    /// </summary>
    public string TextId { get; set; } = string.Empty;

    /// <summary>
    ///     Text used when the key has no translation (default: the key).
    /// </summary>
    public string? DefaultText { get; set; }

    /// <summary>
    ///     True (default): the value follows language switches. False: translated once.
    /// </summary>
    public bool IsDynamic { get; set; } = true;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (!IsDynamic)
        {
            return Translate(TextId, DefaultText);
        }

        // A string value is set directly by the XAML runtime, a binding is attached to the target Avalonia property
        return new TranslationObservable(TextId, DefaultText).ToBinding();
    }

    public static string Translate(string textId, string? defaultText = null)
    {
        return Loc.Tr(textId, defaultText);
    }

    /// <summary>
    ///     Emits the translation on subscription and after every language change. Subscriptions are held weakly by
    ///     <see cref="LanguageChangeNotifier" />, so views are not kept alive by the static language event.
    /// </summary>
    private sealed class TranslationObservable(string textId, string? defaultText) : IObservable<object?>
    {
        public IDisposable Subscribe(IObserver<object?> observer)
        {
            var subscription = new Subscription(observer, textId, defaultText);
            subscription.Push();
            LanguageChangeNotifier.Add(subscription);
            return subscription;
        }
    }

    internal sealed class Subscription(IObserver<object?> observer, string textId, string? defaultText) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            _disposed = true;
        }

        public bool IsDisposed => _disposed;

        public void Push()
        {
            if (!_disposed) observer.OnNext(Translate(textId, defaultText));
        }
    }
}

/// <summary>
///     Forwards <c>Loc.Instance.CurrentLanguageChanged</c> to the live <see cref="TrExtension" /> bindings on the UI thread.
/// </summary>
internal static class LanguageChangeNotifier
{
    private static readonly List<WeakReference<TrExtension.Subscription>> Subscriptions = [];
    private static bool _hooked;
    private static int _addsSinceCleanup;

    public static void Add(TrExtension.Subscription subscription)
    {
        if (!_hooked)
        {
            _hooked = true;
            Loc.Instance.CurrentLanguageChanged += OnLanguageChanged;
        }

        Subscriptions.Add(new WeakReference<TrExtension.Subscription>(subscription));
        if (++_addsSinceCleanup >= 512)
        {
            _addsSinceCleanup = 0;
            Cleanup();
        }
    }

    private static void OnLanguageChanged(object? sender, CurrentLanguageChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        Refresh();
    }

    private static void Refresh()
    {
        // Copy: a pushed value may create new bindings (e.g. templates re-applied) that register while we iterate
        var alive = new List<TrExtension.Subscription>(Subscriptions.Count);
        foreach (var weak in Subscriptions)
        {
            if (weak.TryGetTarget(out var s) && !s.IsDisposed) alive.Add(s);
        }

        Cleanup();
        foreach (var subscription in alive)
        {
            subscription.Push();
        }
    }

    private static void Cleanup()
    {
        Subscriptions.RemoveAll(w => !w.TryGetTarget(out var s) || s.IsDisposed);
    }
}
