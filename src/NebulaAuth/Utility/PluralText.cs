using System;
using System.Globalization;
using NebulaAuth.Core;

namespace NebulaAuth.Utility;

/// <summary>
///     Count-dependent texts. A translation holds its plural forms separated by '|' in the order of the language rule:
///     en/es "one|other", fr "one (0 and 1)|other", ru/uk "one|few|many", zh/tr/kk a single form. A text with fewer forms
///     than the rule uses its last form. <c>{0}</c> is replaced by the count.
/// </summary>
public static class PluralText
{
    public static string Get(int count, string def, params string[] path)
    {
        return Format(LocManager.GetOrDefault(def, path), count, LocManager.GetCurrentLanguageCode());
    }

    public static string Format(string forms, int count, string languageCode)
    {
        var parts = forms.Split('|');
        var index = Math.Min(GetFormIndex(count, languageCode), parts.Length - 1);
        return string.Format(CultureInfo.InvariantCulture, parts[index], count);
    }

    public static int GetFormIndex(int count, string languageCode)
    {
        var n = Math.Abs(count);
        switch (languageCode)
        {
            case "ru":
            case "uk":
                if (n % 10 == 1 && n % 100 != 11) return 0;
                if (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) return 1;
                return 2;
            case "fr":
                return n <= 1 ? 0 : 1;
            case "zh":
            case "tr":
            case "kk":
                return 0;
            default:
                return n == 1 ? 0 : 1;
        }
    }
}
