namespace NebulaAuth.Model;

/// <summary>links.json of the repository: every external link the app shows or opens.</summary>
public class RemoteLinks
{
    public string? Website { get; set; }
    public string? Documentation { get; set; }
    public string? Telegram { get; set; }
    public string? GitHub { get; set; }

    /// <summary>The latest release page (manual download when an update can't be installed).</summary>
    public string? Releases { get; set; }

    /// <summary>Linking troubleshooting guide; "{lang}" is replaced by the UI language code.</summary>
    public string? LinkingTroubleshooting { get; set; }

    /// <summary>Achiefy API bases Nebula Link may pair with (full addresses with their path).</summary>
    public string[]? NebulaLinkApis { get; set; }

    /// <summary>The Nebula Link page of the Achiefy cabinet (pairing links are created there).</summary>
    public string? NebulaLinkCabinet { get; set; }

    /// <summary>What Nebula Link sends to Achiefy (data description).</summary>
    public string? NebulaLinkData { get; set; }
}