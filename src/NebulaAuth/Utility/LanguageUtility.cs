using System;
using System.Globalization;
using System.Linq;
using NebulaAuth.Core;

namespace NebulaAuth.Utility;

public static class LanguageUtility
{
    public static LocalizationLanguage DetectPreferredLanguage()
    {
        var userCulture = CultureInfo.CurrentUICulture;
        var userLang = userCulture.TwoLetterISOLanguageName;

        switch (userLang)
        {
            case "ru": return LocalizationLanguage.Russian;
            case "uk": return LocalizationLanguage.Ukrainian;
            case "en": return LocalizationLanguage.English;
            case "zh": return LocalizationLanguage.ChineseSimplified;
            case "fr": return LocalizationLanguage.French;
        }

        var userRegion = GetRegionName(userCulture);
        if (userRegion == null)
            return LocalizationLanguage.English;

        if (userRegion.EndsWith("UA", StringComparison.OrdinalIgnoreCase))
            return LocalizationLanguage.Ukrainian;

        string[] cisRegions =
        [
            "RU", "BY", "KZ", "KG", "TJ", "TM", "UZ", "AM", "AZ", "GE", "MD"
        ];

        return cisRegions.Any(r => userRegion.EndsWith(r, StringComparison.OrdinalIgnoreCase))
            ? LocalizationLanguage.Russian
            : LocalizationLanguage.English;
    }

    /// <summary>
    ///     Two-letter region of <paramref name="culture" />, or null when it has none.
    ///     The invariant culture (Linux with LANG=C / POSIX / unset, invariant globalization mode) has an empty name and
    ///     neutral cultures ("kk") have no region: <see cref="RegionInfo" /> would throw for them.
    /// </summary>
    private static string? GetRegionName(CultureInfo culture)
    {
        if (string.IsNullOrEmpty(culture.Name) || culture.IsNeutralCulture) return null;
        try
        {
            return new RegionInfo(culture.Name).TwoLetterISORegionName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
