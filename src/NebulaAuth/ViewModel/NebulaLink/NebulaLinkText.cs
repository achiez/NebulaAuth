using System;
using System.Globalization;
using NebulaAuth.Core;
using NebulaAuth.NebulaLink;
using NebulaAuth.NebulaLink.Protocol;
using NebulaAuth.Utility;

namespace NebulaAuth.ViewModel.NebulaLink;

/// <summary>Localized texts of Nebula Link (Localization/dialogs.nebulalink.loc.json, root "NebulaLink").</summary>
public static class NebulaLinkText
{
    private const string ROOT = "NebulaLink";

    public static string Get(string def, params string[] path)
    {
        return LocManager.GetOrDefault(def, Full(path));
    }

    public static string Format(string def, string[] path, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(def, path), args);
    }

    public static string Plural(int count, string def, params string[] path)
    {
        return PluralText.Get(count, def, Full(path));
    }

    private static string[] Full(string[] path)
    {
        var full = new string[path.Length + 1];
        full[0] = ROOT;
        Array.Copy(path, 0, full, 1, path.Length);
        return full;
    }

    /// <summary>Wire strings use dots / snake case; localization keys use the snake case with '_' instead of '.'.</summary>
    private static string KeyOf(string wire)
    {
        return wire.Replace('.', '_');
    }

    public static string State(NebulaLinkState state)
    {
        return Get(state.ToString(), "State", state.ToString());
    }

    public static string Result(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "—";
        return Get(code, "Result", KeyOf(code));
    }

    public static string Scope(string scope)
    {
        return Get(scope, "Scope", KeyOf(scope));
    }

    public static string Module(string module)
    {
        return Get(module, "Module", KeyOf(module));
    }

    /// <summary>A module of the history: its catalog name, else a known module name, else the id (plain text).</summary>
    public static string Module(NebulaModuleCatalog catalog, string? moduleId)
    {
        if (string.IsNullOrEmpty(moduleId)) return "—";
        return ModuleCatalogs.DisplayName(catalog, moduleId, LanguageCode) ?? Module(moduleId);
    }

    /// <summary>Permission category: trades, market sales, market purchases.</summary>
    public static string Category(string category)
    {
        return Get(category, "Category", KeyOf(category));
    }

    /// <summary>Language code of the UI ("en", "ru"...) for the module names of the server catalog.</summary>
    public static string LanguageCode => LocManager.GetCurrentLanguageCode();

    public static string Command(string type)
    {
        return Get(type, "Command", KeyOf(type));
    }

    public static string Operation(string? type)
    {
        return type == null ? "" : Get(type, "Operation", KeyOf(type));
    }

    public static string AccountState(string state)
    {
        return Get(state, "AccountState", KeyOf(state));
    }

    public static string AccountStateHint(string state)
    {
        return Get(state, "AccountStateHint", KeyOf(state));
    }

    public static string LinkResult(string result)
    {
        return Get(result, "LinkResult", KeyOf(result));
    }

    /// <summary>How long ago, short: "now", "5 min", "2 h", "yesterday", then the date.</summary>
    public static string Ago(DateTimeOffset time)
    {
        var span = DateTimeOffset.Now - time;
        var day = time.ToLocalTime().Date;
        if (span < TimeSpan.FromMinutes(1)) return Get("now", "Ago", "Now");
        if (span < TimeSpan.FromHours(1)) return Format("{0} min", ["Ago", "Minutes"], (int) span.TotalMinutes);
        if (day == DateTime.Today) return Format("{0} h", ["Ago", "Hours"], (int) span.TotalHours);
        if (day == DateTime.Today.AddDays(-1)) return Get("yesterday", "Ago", "Yesterday");
        return day.ToString("dd.MM", CultureInfo.CurrentCulture);
    }
}
