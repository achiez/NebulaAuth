using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace NebulaAuth.Model.DeviceModels;

/// <summary>A built-in phone model: <see cref="Model" /> is the Android <c>Build.MODEL</c> value sent to Steam.</summary>
public sealed record DeviceModelInfo(string Brand, string MarketingName, string Model)
{
    /// <summary>Marketing name with the brand in front unless it already starts with it ("Samsung Galaxy S23", "POCO F5").</summary>
    public string DisplayName => MarketingName.StartsWith(Brand, StringComparison.OrdinalIgnoreCase)
        ? MarketingName
        : $"{Brand} {MarketingName}";
}

/// <summary>
///     An entry of the user's model list: <see cref="Name" /> is the device name sent to Steam, <see cref="BuiltIn" /> the
///     catalog entry for a built-in model (null for a name added by the user).
/// </summary>
public sealed record DeviceModelEntry(string Name, DeviceModelInfo? BuiltIn)
{
    public bool IsBuiltIn => BuiltIn != null;
}

public enum DeviceModelValidation
{
    Ok,
    Empty,
    TooLong,
    InvalidCharacters,
    Duplicate
}

/// <summary>
///     Phone models an account can be given as its device name (<see cref="Entities.Mafile.DeviceName" />): the built-in
///     list minus the removed ones (<see cref="Settings.RemovedBuiltInDeviceModels" />) plus the user's own names
///     (<see cref="Settings.CustomDeviceModels" />).
/// </summary>
public static class DeviceModelCatalog
{
    public const int MAX_CUSTOM_LENGTH = 64;

    /// <summary>
    ///     Real Android phones with their <c>Build.MODEL</c> value (global variants). Every entry is listed with exactly
    ///     this brand, marketing name and model in the Google Play supported devices list.
    /// </summary>
    public static IReadOnlyList<DeviceModelInfo> BuiltIn { get; } =
    [
        new("Google", "Pixel 7", "Pixel 7"),
        new("Google", "Pixel 7a", "Pixel 7a"),
        new("Google", "Pixel 8", "Pixel 8"),
        new("Google", "Pixel 8 Pro", "Pixel 8 Pro"),
        new("Google", "Pixel 9", "Pixel 9"),
        new("Samsung", "Galaxy S22", "SM-S901B"),
        new("Samsung", "Galaxy S23", "SM-S911B"),
        new("Samsung", "Galaxy S23 Ultra", "SM-S918B"),
        new("Samsung", "Galaxy S24", "SM-S921B"),
        new("Samsung", "Galaxy A54 5G", "SM-A546B"),
        new("Samsung", "Galaxy A34 5G", "SM-A346B"),
        new("Samsung", "Galaxy A14", "SM-A145F"),
        new("Redmi", "Redmi Note 12", "23021RAAEG"),
        new("Redmi", "Redmi Note 13 Pro", "23117RA68G"),
        new("POCO", "POCO X5 Pro 5G", "22101320G"),
        new("POCO", "POCO F5", "23049PCD8G"),
        new("Xiaomi", "Xiaomi 13", "2211133G"),
        new("Xiaomi", "Xiaomi 13T", "2306EPN60G"),
        new("OnePlus", "OnePlus 11 5G", "CPH2449"),
        new("OnePlus", "OnePlus Nord CE 3 Lite 5G", "CPH2465"),
        new("Motorola", "moto g84 5G", "moto g84 5G"),
        new("Motorola", "motorola edge 40", "motorola edge 40"),
        new("Nothing", "Nothing Phone (2)", "A065"),
        new("Sony", "Xperia 10 V", "XQ-DC54")
    ];

    /// <summary>
    ///     Checks a model name the user wants to add. <paramref name="normalized" /> is the trimmed name. A name equal to one
    ///     of <paramref name="existing" /> (the current list) up to case is a duplicate.
    /// </summary>
    public static DeviceModelValidation ValidateName(string? input, IEnumerable<string> existing, out string normalized)
    {
        normalized = (input ?? string.Empty).Trim();
        if (normalized.Length == 0) return DeviceModelValidation.Empty;
        if (normalized.Length > MAX_CUSTOM_LENGTH) return DeviceModelValidation.TooLong;
        if (normalized.Any(char.IsControl)) return DeviceModelValidation.InvalidCharacters;
        var name = normalized;
        return existing.Any(e => string.Equals(e?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            ? DeviceModelValidation.Duplicate
            : DeviceModelValidation.Ok;
    }

    /// <summary>The built-in model with this <see cref="DeviceModelInfo.Model" /> value (any case), or null.</summary>
    public static DeviceModelInfo? FindBuiltIn(string? model)
    {
        var name = model?.Trim();
        return string.IsNullOrEmpty(name)
            ? null
            : BuiltIn.FirstOrDefault(m => string.Equals(m.Model, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     The user's model list: the usable custom names first (trimmed, valid, in the order they were added), then the
    ///     built-in models that were not removed, in catalog order. No two entries are equal up to case (a custom name equal
    ///     to a listed built-in model is shown once, as the built-in one). May be empty.
    /// </summary>
    public static IReadOnlyList<DeviceModelEntry> GetEntries(IEnumerable<string>? removedBuiltIn,
        IEnumerable<string>? custom)
    {
        var removed = new HashSet<string>((removedBuiltIn ?? []).Where(r => r != null).Select(r => r.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var builtIn = BuiltIn.Where(m => !removed.Contains(m.Model)).ToList();
        var seen = new HashSet<string>(builtIn.Select(m => m.Model), StringComparer.OrdinalIgnoreCase);
        var entries = new List<DeviceModelEntry>(builtIn.Count);
        foreach (var model in custom ?? [])
        {
            var name = model?.Trim();
            if (!IsUsableName(name) || !seen.Add(name!)) continue;
            entries.Add(new DeviceModelEntry(name!, null));
        }

        entries.AddRange(builtIn.Select(m => new DeviceModelEntry(m.Model, m)));
        return entries;
    }

    /// <summary>
    ///     Names a new account can get: the user's list (<see cref="GetEntries" />). Never empty: when the settings leave
    ///     no model at all, every built-in model is used.
    /// </summary>
    public static IReadOnlyList<string> GetPool(IEnumerable<string>? removedBuiltIn, IEnumerable<string>? custom)
    {
        var pool = GetEntries(removedBuiltIn, custom).Select(e => e.Name).ToList();
        return pool.Count > 0 ? pool : BuiltIn.Select(m => m.Model).ToList();
    }

    public static string PickRandom(IReadOnlyList<string> pool)
    {
        if (pool.Count == 0) throw new ArgumentException("The model list is empty", nameof(pool));
        return pool[RandomNumberGenerator.GetInt32(pool.Count)];
    }

    private static bool IsUsableName(string? name)
    {
        return !string.IsNullOrEmpty(name) && name.Length <= MAX_CUSTOM_LENGTH && !name.Any(char.IsControl);
    }
}
