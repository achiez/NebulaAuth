using System.Globalization;
using Avalonia;
using Avalonia.Threading;
using CodingSeb.Localization;
using Semi.Avalonia;
using Ursa.Themes.Semi;

namespace NebulaAuth.Localization;

/// <summary>
///     Keeps the built-in strings of Semi/Ursa controls (TextBox context menu, pickers, ...) in the app language.
///     Called once by MainWindow; follows <c>Loc.Instance.CurrentLanguage</c> afterwards.
/// </summary>
public static class ControlsLocale
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        Apply(Loc.Instance.CurrentLanguage);
        Loc.Instance.CurrentLanguageChanged += (_, e) => Dispatcher.UIThread.Post(() => Apply(e.NewLanguageId));
    }

    private static void Apply(string? languageCode)
    {
        var app = Application.Current;
        if (app == null) return;
        var culture = new CultureInfo(languageCode switch
        {
            "ru" or "kk" => "ru-RU",
            "uk" => "uk-UA",
            "zh" => "zh-CN",
            "fr" => "fr-FR",
            "es" => "es-ES",
            _ => "en-US"
        });
        SemiTheme.OverrideLocaleResources(app, culture);
        // Ursa has fewer locales: fall back to English for the missing ones
        UrsaSemiTheme.OverrideLocaleResources(app, culture.Name is "uk-UA" or "es-ES" ? new CultureInfo("en-US") : culture);
    }
}
