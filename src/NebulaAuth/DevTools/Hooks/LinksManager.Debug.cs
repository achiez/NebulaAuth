using System;

namespace NebulaAuth.Core;

public static partial class LinksManager
{
    // NEBULA_LINK_APIS="https://a/api,https://b/api" replaces the published list (empty value: not available)
    static partial void DebugOverrideApis(ref string[]? apis)
    {
        var value = Environment.GetEnvironmentVariable("NEBULA_LINK_APIS");
        if (value != null) apis = value.Length == 0 ? null : value.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }
}
