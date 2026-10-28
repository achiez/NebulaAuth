using NebulaAuth.NebulaLink;

namespace NebulaAuth.Model.LinkHost;

public static partial class NebulaLinkHost
{
    /// <summary>A fake integration replaces the real one.</summary>
    internal static void Replace(NebulaLinkIntegration integration)
    {
        _integration = integration;
    }
}
