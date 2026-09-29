using System;
using System.IO;
using System.Text;
using CodingSeb.Localization;
using CodingSeb.Localization.Loaders;

namespace NebulaAuth.Core;

public static class LocManager
{
    public const string CODE_BEHIND_PATH_PART = "CodeBehind";
    public const string COMMON_PATH_PART = "Common";
    private const string LOC_DIR = "Localization";

    public static void SetApplicationLocalization(LocalizationLanguage language)
    {
        Loc.Instance.CurrentLanguage = GetLanguageCode(language);
        //Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(GetLanguageCode(language));
        //Thread.CurrentThread.CurrentCulture= CultureInfo.GetCultureInfo(GetLanguageCode(language));
    }

    public static string GetCurrentLanguageCode()
    {
        return Loc.Instance.CurrentLanguage;
    }

    public static string GetLanguageCode(LocalizationLanguage language)
    {
        return language switch
        {
            LocalizationLanguage.English => "en",
            LocalizationLanguage.Russian => "ru",
            LocalizationLanguage.Ukrainian => "uk",
            LocalizationLanguage.ChineseSimplified => "zh",
            LocalizationLanguage.French => "fr",
            LocalizationLanguage.Spanish => "es",
            LocalizationLanguage.Turkish => "tr",
            LocalizationLanguage.Kazakh => "kk",
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
        };
    }


    private static bool _initialized;

    /// <summary>
    ///     Loads the translation files once (the data folder choice needs them before <c>Shell.Initialize</c>).
    /// </summary>
    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        Loc.Instance.LogOutMissingTranslations = true;

        LocalizationLoader.Instance.FileLanguageLoaders.Add(new JsonFileLoader());

        ReloadFiles();
    }

    public static void ReloadFiles()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, LOC_DIR);
        LocalizationLoader.Instance.AddDirectory(path);
    }

    public static string? GetCodeBehind(params string[] path)
    {
        return GetConcat(CODE_BEHIND_PATH_PART, path);
    }

    public static string GetCodeBehindOrDefault(string def, params string[] path)
    {
        return GetCodeBehind(path) ?? def;
    }

    public static string? GetCommon(params string[] path)
    {
        return GetConcat(COMMON_PATH_PART, path);
    }

    public static string GetCommonOrDefault(string def, params string[] path)
    {
        return GetCommon(path) ?? def;
    }


    public static string? Get(params string[] path)
    {
        var sb = new StringBuilder();
        foreach (var part in path)
        {
            sb.Append('.');
            sb.Append(part);
        }

        sb.Remove(0, 1);
        var fullPath = sb.ToString();
        var message = Loc.Tr(fullPath, "~|*ABSENT|~");
        return message == "~|*ABSENT|~" ? null : message;
    }

    public static string GetOrDefault(string def, params string[] path)
    {
        return Get(path) ?? def;
    }

    private static string? GetConcat(string first, string[] path)
    {
        var newArray = new string[path.Length + 1];
        newArray[0] = first;
        Array.Copy(path, 0, newArray, 1, path.Length);
        return Get(newArray);
    }
}

public enum LocalizationLanguage
{
    English,
    Russian,
    Ukrainian,
    ChineseSimplified,
    French,
    Spanish,
    Turkish,
    Kazakh
}