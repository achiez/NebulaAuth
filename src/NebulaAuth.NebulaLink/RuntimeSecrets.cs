using System;
using System.Collections.Generic;
using System.Threading;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     Secret values known only at run time that must never reach the log: the current device credential, the claim
///     secret of a pairing and the Steam Guard codes issued for Nebula Link requests during the last 90 seconds. The log
///     sanitizers (<see cref="NebulaLinkLog" /> and the app's) mask them.
/// </summary>
public static class RuntimeSecrets
{
    private static readonly TimeSpan ShortLived = TimeSpan.FromSeconds(90);
    private static readonly Lock Lock = new();
    private static readonly List<(string Value, DateTime? Until)> Values = [];

    /// <summary>Registers a value; <paramref name="shortLived" /> values are forgotten after 90 s.</summary>
    public static void Register(string? value, bool shortLived = false)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (Lock)
        {
            Values.RemoveAll(v => v.Value == value);
            Values.Add((value, shortLived ? DateTime.UtcNow + ShortLived : null));
        }
    }

    public static void Unregister(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (Lock) Values.RemoveAll(v => v.Value == value);
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Lock)
        {
            if (Values.Count == 0) return [];
            var now = DateTime.UtcNow;
            Values.RemoveAll(v => v.Until != null && v.Until < now);
            return Values.ConvertAll(v => v.Value);
        }
    }
}
