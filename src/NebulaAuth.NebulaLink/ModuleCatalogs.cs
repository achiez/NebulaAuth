using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NebulaAuth.NebulaLink.Protocol;
using Newtonsoft.Json;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     One optional permission of an account: confirmations of <paramref name="Category" /> asked by the server module
///     <paramref name="ModuleId" /> (an entry of the module catalog). Stored by key, so a renamed module keeps its grant.
/// </summary>
public sealed record GrantKey(
    [property: JsonProperty("category")] string Category,
    [property: JsonProperty("moduleId")] string ModuleId)
{
    public NebulaGrant ToWire()
    {
        return new NebulaGrant {Category = Category, ModuleId = ModuleId};
    }

    public override string ToString()
    {
        return Category + ":" + ModuleId;
    }
}

/// <summary>
///     The server module catalog as NebulaAuth uses it: entries with an unknown category or a malformed id are dropped,
///     names are plain text of at most <see cref="NebulaCatalogLimits.MaxNameLength" /> characters (control characters
///     removed), at most <see cref="NebulaCatalogLimits.MaxModules" /> entries.
/// </summary>
public static partial class ModuleCatalogs
{
    public static readonly NebulaModuleCatalog Empty = new();

    public static NebulaModuleCatalog Sanitize(NebulaModuleCatalog? catalog)
    {
        var result = new NebulaModuleCatalog {Version = Clean(catalog?.Version, 64)};
        var modules = new List<NebulaModuleInfo>();
        var seen = new HashSet<GrantKey>();
        foreach (var module in catalog?.Modules ?? [])
        {
            if (modules.Count >= NebulaCatalogLimits.MaxModules) break;
            if (module == null || !IsKnownCategory(module.Category) || !IsValidId(module.Id)) continue;
            if (!seen.Add(new GrantKey(module.Category, module.Id))) continue;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (lang, name) in module.Names ?? new Dictionary<string, string>())
            {
                if (lang == null || !LanguageCode().IsMatch(lang)) continue;
                var clean = Clean(name, NebulaCatalogLimits.MaxNameLength);
                if (clean.Length > 0) names[lang.ToLowerInvariant()] = clean;
            }

            modules.Add(new NebulaModuleInfo {Id = module.Id, Category = module.Category, Names = names});
        }

        result.Modules = modules.ToArray();
        return result;
    }

    public static bool IsKnownCategory(string? category)
    {
        return category != null && NebulaCategories.All.Contains(category);
    }

    public static bool IsValidId(string? id)
    {
        return id != null && ModuleId().IsMatch(id);
    }

    public static bool Contains(NebulaModuleCatalog? catalog, GrantKey key)
    {
        return catalog?.Modules.Any(m => m.Category == key.Category && m.Id == key.ModuleId) == true;
    }

    /// <summary>The module name in <paramref name="language" /> ("ru"), else English, else the id.</summary>
    public static string DisplayName(NebulaModuleInfo module, string language)
    {
        if (module.Names.TryGetValue(language, out var name)) return name;
        return module.Names.TryGetValue("en", out var en) ? en : module.Id;
    }

    /// <summary>Name of a module id for the history: the catalog name when known, else null.</summary>
    public static string? DisplayName(NebulaModuleCatalog? catalog, string? moduleId, string language)
    {
        var module = catalog?.Modules.FirstOrDefault(m => m.Id == moduleId);
        return module == null ? null : DisplayName(module, language);
    }

    /// <summary>Plain text: control and format characters removed, whitespace collapsed, at most <paramref name="max" />.</summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max * 2));
        foreach (var c in text)
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }

            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.OtherNotAssigned) continue;
            sb.Append(c);
        }

        var s = sb.ToString().Trim();
        return s.Length <= max ? s : s[..max].TrimEnd();
    }

    [GeneratedRegex("^[a-z0-9_]{1,32}$")]
    private static partial Regex ModuleId();

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex LanguageCode();
}
