using System.Collections.Generic;
using System.Linq;
using NebulaAuth.NebulaLink.Protocol;

namespace NebulaAuth.NebulaLink;

// State setters of the in-memory preview integration (NebulaLinkIntegration.CreatePreview)

internal sealed partial class LinkSession
{
    internal void PreviewSet(NebulaLinkState state, string? reason, bool serverNewer, NebulaModuleCatalog catalog)
    {
        State = state;
        StateReason = reason;
        ServerNewer = serverNewer;
        _catalog = catalog;
    }

    internal void PreviewSetReason(string? reason)
    {
        StateReason = reason;
    }

    internal void PreviewSetServerNewer(bool newer)
    {
        ServerNewer = newer;
    }
}

internal sealed partial class PairingFlow
{
    internal void PreviewSet(PendingPairing pairing)
    {
        _pairing = pairing;
    }
}

internal sealed partial class ServerAccountMirror
{
    internal void PreviewSet(IReadOnlyList<AchiefyAccount> accounts)
    {
        lock (_lock) _accounts = accounts.ToDictionary(a => a.AccountId);
        Loaded = accounts.Count > 0;
    }
}
