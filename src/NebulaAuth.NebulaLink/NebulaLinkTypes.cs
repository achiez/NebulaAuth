using System;
using System.Collections.Generic;
using NebulaAuth.NebulaLink.Confirmations;
using NebulaAuth.NebulaLink.Credentials;
using NebulaAuth.NebulaLink.Sessions;
using SteamLib;

namespace NebulaAuth.NebulaLink;

public enum NebulaLinkState
{
    /// <summary>Not paired (no nebula.json): the pairing pages; no connection, no credential.</summary>
    Disabled,

    Connecting,
    Connected,

    /// <summary>Paired but not connected; retrying with backoff.</summary>
    Offline,

    Paused,

    /// <summary>Connected, but the tenant has no access (subscription): commands are refused by the server.</summary>
    Suspended,

    /// <summary>The link was revoked (cabinet, new pairing, device unlink, inactivity): the credential is deleted.</summary>
    Revoked,

    /// <summary>Another NebulaAuth copy connected with this link; reconnect only by a user action.</summary>
    Replaced,

    /// <summary>The protocol major version differs from the server's.</summary>
    Incompatible,

    /// <summary>The credential can't be read (no secure store, missing item).</summary>
    CredentialUnavailable,

    /// <summary>The server refuses the credential (401) repeatedly.</summary>
    CredentialRejected
}

public enum NebulaLinkChange
{
    State,
    Accounts,
    History
}

/// <summary>Where a pairing currently is (pages of the modal).</summary>
public sealed class PendingPairing
{
    public Guid PairingId { get; init; }
    internal string ClaimSecret { get; init; } = "";

    /// <summary>Origin the pairing was redeemed at (checked against <see cref="NebulaLinkApis" />).</summary>
    public string Origin { get; init; } = "";

    public string TenantBaseUrl { get; init; } = "";
    public string ApiOrigin { get; init; } = "";
    public int TenantId { get; init; }
    public string TenantLabel { get; init; } = "";
    public string AccountLabel { get; init; } = "";
    public DateTimeOffset ApprovalExpiresAt { get; init; }
    public int PollIntervalMs { get; init; }
    public bool OriginMatches { get; init; }
}

public enum PairingWaitResult
{
    Approved,
    Rejected,
    Expired,
    Cancelled,
    Failed,
    StoreFailed
}

/// <summary>Result of a bulk link / unlink / grant change.</summary>
public sealed class BulkOutcome
{
    public int Changed { get; set; }
    public int Skipped { get; set; }

    /// <summary>Per account: protocol link result (login_mismatch, not_found...) or a local reason.</summary>
    public Dictionary<int, string> Reasons { get; } = new();

    public string? Error { get; set; }
}

public sealed class NebulaLinkOptions
{
    /// <summary>Folder of nebula.json / nebula-history.json; null keeps everything in memory.</summary>
    public string? DataDirectory { get; init; }

    public required INebulaHttpFactory Http { get; init; }
    public required DeviceCredentialStoreSelector Selector { get; init; }

    /// <summary>The local accounts (a copy that is safe to read from any thread).</summary>
    public required Func<IReadOnlyList<MobileDataExtended>> Mafiles { get; init; }

    public required IHeadlessSessionBackend SessionBackend { get; init; }

    /// <summary>Session handling of the manual confirmations screen (dialogs); null: interactive reads are not supported.</summary>
    public IInteractiveSessionRunner? InteractiveSessions { get; init; }

    public IConfirmationTransport? ConfirmationTransport { get; init; }

    /// <summary>The Steam clients of the accounts (the app's pool knows the proxies); null: a pool without proxies.</summary>
    public AccountSteamClientPool? ClientPool { get; init; }

    /// <summary>File name and modification time of a mafile (duplicate mafiles are listed with them).</summary>
    public Func<MobileDataExtended, MafileFileInfo?>? FileInfo { get; init; }

    /// <summary>An account was linked: the app turns its auto-confirmation off.</summary>
    public Action<MobileDataExtended>? AccountLinked { get; init; }

    /// <summary>Deletes the encrypted copy of the device credential (also when the encrypted store is not available).</summary>
    public Action? PurgeEncryptedCredential { get; init; }

    /// <summary>The login code registry shared with the app's own logins; null: a registry of this integration.</summary>
    public GuardCodeService? GuardCodes { get; init; }

    public Func<DateTimeOffset>? Clock { get; init; }
}
