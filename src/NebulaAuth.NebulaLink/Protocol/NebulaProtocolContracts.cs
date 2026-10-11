#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NebulaAuth.NebulaLink.Protocol; // server: Achiefy.Tenant.Contracts.Nebula.Protocol

public static class NebulaProtocol
{
    public const int Major = 1;
    public const int Minor = 0;
    public const string HubPath = "/hubs/nebula-device";
    public const string CredentialPrefix = "nbl1";
}

public static class NebulaCapabilities
{
    public const string CommandLoginCode = "cmd.login_code";
    public const string CommandCheckConfirmation = "cmd.check_confirmation";
    public const string CommandConfirm = "cmd.confirm";
    public const string CommandCheckReady = "cmd.check_ready";
    public const string OperationTrade = "op.trade";
    public const string OperationMarketListing = "op.market_listing";
    public const string OperationMarketPurchase = "op.market_purchase";
    public const string EventUserAction = "evt.user_action";
    public const string CredentialRotate = "credential.rotate";
    public static readonly string[] V1 =
    [
        CommandLoginCode, CommandCheckConfirmation, CommandConfirm, CommandCheckReady,
        OperationTrade, OperationMarketListing, OperationMarketPurchase, EventUserAction, CredentialRotate
    ];
}

public static class NebulaCommandTypes
{
    public const string LoginCode = "login_code";
    public const string CheckConfirmation = "check_confirmation";
    public const string Confirm = "confirm";
    public const string CheckReady = "check_ready";
}

public static class NebulaOperationTypes
{
    public const string Trade = "trade";                    // Steam ConfirmationType 2
    public const string MarketListing = "market_listing";   // 3
    public const string MarketPurchase = "market_purchase"; // 12
}

public static class NebulaDecisions { public const string Accept = "accept"; public const string Decline = "decline"; }

public static class NebulaModules
{
    public const string AccountRuntime = "account_runtime";
    public const string Harvester = "harvester";
    public const string P2PSeller = "p2p_seller";
    public const string Trader = "trader";
    public const string SteamSeller = "steam_seller";
    public const string SteamBuyer = "steam_buyer";
    public const string TaskManager = "task_manager";
}

/// <summary>Permission categories, fixed in the protocol. Modules come from the server catalog.</summary>
public static class NebulaCategories
{
    public const string Trade = "trade";
    public const string MarketSell = "market_sell";
    public const string MarketBuy = "market_buy";
    public static readonly string[] All = [Trade, MarketSell, MarketBuy];

    /// <summary>The category of an operation type; null for an unknown one.</summary>
    public static string? FromOperationType(string? operationType) => operationType switch
    {
        NebulaOperationTypes.Trade => Trade,
        NebulaOperationTypes.MarketListing => MarketSell,
        NebulaOperationTypes.MarketPurchase => MarketBuy,
        _ => null
    };
}

public static class NebulaCatalogLimits
{
    public const int MaxModules = 64;
    public const int MaxIdLength = 32;
    public const int MaxNameLength = 32;
}

public static class NebulaResultCodes
{
    public const string Ok = "ok";
    public const string Pending = "pending";
    public const string NotFound = "not_found";
    public const string Confirmed = "confirmed";
    public const string Declined = "declined";
    public const string SteamRejected = "steam_rejected";
    public const string ResultUnknown = "result_unknown";
    public const string DeviceOffline = "device_offline";
    public const string Busy = "busy";
    public const string RateLimited = "rate_limited";
    public const string ProxyFailed = "proxy_failed";
    public const string SteamUnavailable = "steam_unavailable";
    public const string InternalError = "internal_error";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string DevicePaused = "device_paused";
    public const string AccessSuspended = "access_suspended";
    public const string SteamSessionRequired = "steam_session_required";
    public const string InvalidLocalPassword = "invalid_local_password";
    public const string IncompatibleVersion = "incompatible_version";
    public const string NotPaired = "not_paired";
    public const string AccountUnlinked = "account_unlinked";
    public const string MafileMissing = "mafile_missing";
    public const string MatchConflict = "match_conflict";
    public const string ScopeDenied = "scope_denied";
    public const string Unsupported = "unsupported";
    public const string UnsupportedForLocalAuthenticator = "unsupported_for_local_authenticator";
    public const string InvalidRequest = "invalid_request";
}

public static class NebulaHubErrors
{
    public const string HelloRequired = "hello_required";
    public const string StaleConnection = "stale_connection";
    public const string IncompatibleVersion = "incompatible_version";
    public const string NotPaired = "not_paired";
    public const string InvalidRequest = "invalid_request";
}

public static class NebulaResolutions
{
    public const string ConfirmedByUser = "confirmed_by_user";
    public const string DeclinedByUser = "declined_by_user";
    public const string ConfirmedByAchiefy = "confirmed_by_achiefy";
    public const string DeclinedByAchiefy = "declined_by_achiefy";
    public const string UnknownAfterSend = "unknown_after_send";
}

public static class NebulaUserActions
{
    public const string ConfirmedByUser = "confirmed_by_user";
    public const string DeclinedByUser = "declined_by_user";
}

public static class NebulaAccountStates
{
    public const string Ready = "ready";
    public const string SteamSessionRequired = "steam_session_required";
    public const string InvalidLocalPassword = "invalid_local_password";
    public const string MafileMissing = "mafile_missing";
    public const string MatchConflict = "match_conflict";
}

public static class NebulaAuthenticatorSources
{
    public const string CloudMafile = "cloud_mafile";
    public const string Nebula = "nebula";
    public const string None = "none";
}

public static class NebulaLinkStates
{
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string Revoked = "revoked";
    public const string Replaced = "replaced";
}

public static class NebulaLinkReasons
{
    public const string RevokedByUser = "revoked_by_user";
    public const string ReplacedByNewPairing = "replaced_by_new_pairing";
    public const string UnlinkedByDevice = "unlinked_by_device";
    public const string AccessLost = "access_lost";
    public const string SubscriptionInactive = "subscription_inactive";
    public const string Inactive = "inactive";
}

public static class NebulaAccountLinkActions { public const string Link = "link"; public const string Unlink = "unlink"; }

public static class NebulaAccountLinkResults
{
    public const string Linked = "linked";
    public const string Unlinked = "unlinked";
    public const string AlreadyLinked = "already_linked";
    public const string NotLinked = "not_linked";
    public const string NotFound = "not_found";
    public const string LoginMismatch = "login_mismatch";
    public const string Failed = "failed";
}

public static class NebulaPairingClaimStatuses
{
    public const string AwaitingApproval = "awaiting_approval";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string Completed = "completed";
}

public static class NebulaOs { public const string Windows = "windows"; public const string MacOs = "macos"; public const string Linux = "linux"; }

// ---------------- HTTP (device) ----------------

public sealed class NebulaDeviceInfo
{
    [JsonProperty("deviceName")] public string DeviceName { get; set; } = "";   // 1..64, user-editable, default machine name
    [JsonProperty("os")] public string Os { get; set; } = "";                   // NebulaOs.*
    [JsonProperty("osVersion")] public string? OsVersion { get; set; }          // <= 64
    [JsonProperty("appVersion")] public string AppVersion { get; set; } = "";   // <= 32
}

public sealed class PairingRedeemRequest
{
    [JsonProperty("code")] public string Code { get; set; } = "";               // user input; server normalizes
    [JsonProperty("device")] public NebulaDeviceInfo Device { get; set; } = new();
    [JsonProperty("protocolMajor")] public int ProtocolMajor { get; set; }
    [JsonProperty("protocolMinor")] public int ProtocolMinor { get; set; }
}

public sealed class PairingRedeemResponse
{
    [JsonProperty("pairingId")] public Guid PairingId { get; set; }
    [JsonProperty("claimSecret")] public string ClaimSecret { get; set; } = "";
    [JsonProperty("apiOrigin")] public string ApiOrigin { get; set; } = "";
    [JsonProperty("tenantId")] public int TenantId { get; set; }
    [JsonProperty("tenantLabel")] public string TenantLabel { get; set; } = "";
    [JsonProperty("accountLabel")] public string AccountLabel { get; set; } = "";
    [JsonProperty("approvalExpiresAt")] public DateTimeOffset ApprovalExpiresAt { get; set; }
    [JsonProperty("pollIntervalMs")] public int PollIntervalMs { get; set; }
}

public sealed class PairingClaimRequest
{
    [JsonProperty("pairingId")] public Guid PairingId { get; set; }
    [JsonProperty("claimSecret")] public string ClaimSecret { get; set; } = "";
}

public sealed class PairingClaimResponse
{
    [JsonProperty("status")] public string Status { get; set; } = "";          // NebulaPairingClaimStatuses.*
    [JsonProperty("pollIntervalMs")] public int PollIntervalMs { get; set; }
    [JsonProperty("linkId")] public Guid? LinkId { get; set; }                  // approved only
    [JsonProperty("credential")] public string? Credential { get; set; }        // approved only
    [JsonProperty("credentialGeneration")] public int? CredentialGeneration { get; set; }
}

public sealed class PairingCancelRequest
{
    [JsonProperty("pairingId")] public Guid PairingId { get; set; }
    [JsonProperty("claimSecret")] public string ClaimSecret { get; set; } = "";
}

// ---------------- Hub ----------------

public sealed class NebulaLimits
{
    [JsonProperty("maxMessageBytes")] public int MaxMessageBytes { get; set; }
    [JsonProperty("maxInFlight")] public int MaxInFlight { get; set; }
    [JsonProperty("pageSize")] public int PageSize { get; set; }
    [JsonProperty("maxConfirmTargets")] public int MaxConfirmTargets { get; set; }
}

public sealed class HelloRequest
{
    [JsonProperty("protocolMajor")] public int ProtocolMajor { get; set; }
    [JsonProperty("protocolMinor")] public int ProtocolMinor { get; set; }
    [JsonProperty("capabilities")] public string[] Capabilities { get; set; } = [];
    [JsonProperty("appVersion")] public string AppVersion { get; set; } = "";
    [JsonProperty("os")] public string Os { get; set; } = "";
    [JsonProperty("osVersion")] public string? OsVersion { get; set; }
    [JsonProperty("deviceName")] public string DeviceName { get; set; } = "";
    [JsonProperty("linkedAccounts")] public int LinkedAccounts { get; set; }
}

public sealed class NebulaModuleInfo
{
    [JsonProperty("id")] public string Id { get; set; } = "";                  // [a-z0-9_], <= 32
    [JsonProperty("category")] public string Category { get; set; } = "";      // NebulaCategories.*
    [JsonProperty("names")] public Dictionary<string, string> Names { get; set; } = new(); // "en" always; plain text <= 32
}

public sealed class NebulaModuleCatalog
{
    [JsonProperty("version")] public string Version { get; set; } = "";        // opaque; changes with the content
    [JsonProperty("modules")] public NebulaModuleInfo[] Modules { get; set; } = [];
}

public sealed class NebulaGrant
{
    [JsonProperty("category")] public string Category { get; set; } = "";
    [JsonProperty("moduleId")] public string ModuleId { get; set; } = "";
}

public sealed class HelloResponse
{
    [JsonProperty("accepted")] public bool Accepted { get; set; }
    [JsonProperty("code")] public string Code { get; set; } = "";               // ok | incompatible_version | not_paired
    [JsonProperty("protocolMajor")] public int ProtocolMajor { get; set; }
    [JsonProperty("protocolMinor")] public int ProtocolMinor { get; set; }      // negotiated
    [JsonProperty("capabilities")] public string[] Capabilities { get; set; } = [];
    [JsonProperty("serverTime")] public DateTimeOffset ServerTime { get; set; }
    [JsonProperty("linkId")] public Guid LinkId { get; set; }
    [JsonProperty("tenantId")] public int TenantId { get; set; }
    [JsonProperty("tenantLabel")] public string TenantLabel { get; set; } = "";
    [JsonProperty("accountLabel")] public string AccountLabel { get; set; } = "";
    [JsonProperty("linkState")] public string LinkState { get; set; } = "";     // active | suspended
    [JsonProperty("linkStateReason")] public string? LinkStateReason { get; set; }
    [JsonProperty("heartbeatIntervalMs")] public int HeartbeatIntervalMs { get; set; }
    [JsonProperty("rotateCredential")] public bool RotateCredential { get; set; }
    [JsonProperty("limits")] public NebulaLimits Limits { get; set; } = new();
    [JsonProperty("moduleCatalog")] public NebulaModuleCatalog? ModuleCatalog { get; set; } // accepted only
}

public sealed class HeartbeatResponse
{
    [JsonProperty("serverTime")] public DateTimeOffset ServerTime { get; set; }
    [JsonProperty("linkState")] public string LinkState { get; set; } = "";
}

public sealed class AckResponse
{
    [JsonProperty("accepted")] public bool Accepted { get; set; }
    [JsonProperty("code")] public string? Code { get; set; }                    // when not accepted: result/hub error code
}

public sealed class AccountsQuery
{
    [JsonProperty("cursor")] public string? Cursor { get; set; }                // opaque; null = first page
    [JsonProperty("pageSize")] public int PageSize { get; set; }                // 1..500
    [JsonProperty("accountIds")] public int[]? AccountIds { get; set; }         // when set: exactly these (<=500), cursor ignored
}

public sealed class AchiefyAccount
{
    [JsonProperty("accountId")] public int AccountId { get; set; }
    [JsonProperty("login")] public string Login { get; set; } = "";             // normalized: Trim().ToLowerInvariant()
    [JsonProperty("source")] public string Source { get; set; } = "";           // NebulaAuthenticatorSources.*
    [JsonProperty("hasServerMafile")] public bool HasServerMafile { get; set; }
    [JsonProperty("linkedToThisDevice")] public bool LinkedToThisDevice { get; set; }
}

public sealed class AccountsPage
{
    [JsonProperty("items")] public AchiefyAccount[] Items { get; set; } = [];
    [JsonProperty("nextCursor")] public string? NextCursor { get; set; }
    [JsonProperty("total")] public int Total { get; set; }
    [JsonProperty("deletedAccountIds")] public int[] DeletedAccountIds { get; set; } = []; // only for accountIds queries: ids that no longer exist
}

public sealed class AccountLinkChange
{
    [JsonProperty("accountId")] public int AccountId { get; set; }
    [JsonProperty("action")] public string Action { get; set; } = "";           // link | unlink
    [JsonProperty("login")] public string Login { get; set; } = "";             // normalized local login
}

public sealed class SetAccountLinksRequest
{
    [JsonProperty("items")] public AccountLinkChange[] Items { get; set; } = [];
}

public sealed class AccountLinkChangeResult
{
    [JsonProperty("accountId")] public int AccountId { get; set; }
    [JsonProperty("result")] public string Result { get; set; } = "";           // NebulaAccountLinkResults.*
    [JsonProperty("source")] public string? Source { get; set; }                // source after the change
}

public sealed class SetAccountLinksResponse
{
    [JsonProperty("items")] public AccountLinkChangeResult[] Items { get; set; } = [];
}

public sealed class AccountStateItem
{
    [JsonProperty("accountId")] public int AccountId { get; set; }
    [JsonProperty("state")] public string State { get; set; } = "";             // NebulaAccountStates.*
    [JsonProperty("grants")] public NebulaGrant[] Grants { get; set; } = [];    // (category, moduleId) pairs currently ON
}

public sealed class AccountStateReport
{
    [JsonProperty("full")] public bool Full { get; set; }
    [JsonProperty("fullBatchIndex")] public int FullBatchIndex { get; set; }    // full only: 0 starts a new full set
    [JsonProperty("fullBatchCount")] public int FullBatchCount { get; set; }    // full only: total batches; server deletes rows not reported after the last batch
    [JsonProperty("items")] public AccountStateItem[] Items { get; set; } = [];
}

public sealed class NebulaOperation
{
    [JsonProperty("type")] public string Type { get; set; } = "";              // NebulaOperationTypes.*
    [JsonProperty("ids")] public string[] Ids { get; set; } = [];               // decimal ulong CreatorIds
    [JsonProperty("createdAt")] public DateTimeOffset? CreatedAt { get; set; }  // server time the Steam op was created, if known (diagnostic only)
}

public sealed class NebulaCommand
{
    [JsonProperty("requestId")] public Guid RequestId { get; set; }
    [JsonProperty("type")] public string Type { get; set; } = "";              // NebulaCommandTypes.*
    [JsonProperty("accountId")] public int AccountId { get; set; }
    [JsonProperty("login")] public string Login { get; set; } = "";
    [JsonProperty("moduleId")] public string ModuleId { get; set; } = "";      // server module (catalog id or account_runtime)
    [JsonProperty("category")] public string? Category { get; set; }           // null for login_code/check_ready
    [JsonProperty("operation")] public NebulaOperation? Operation { get; set; }
    [JsonProperty("decision")] public string? Decision { get; set; }
    [JsonProperty("issuedAt")] public DateTimeOffset IssuedAt { get; set; }
    [JsonProperty("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
    [JsonProperty("ttlMs")] public int TtlMs { get; set; }                     // authoritative for the client deadline
}

public sealed class NebulaOperationResult
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("code")] public string Code { get; set; } = "";
    [JsonProperty("resolution")] public string? Resolution { get; set; }
}

public sealed class NebulaCommandResult
{
    [JsonProperty("requestId")] public Guid RequestId { get; set; }
    [JsonProperty("code")] public string Code { get; set; } = "";
    [JsonProperty("guardCode")] public string? GuardCode { get; set; }          // login_code only; NEVER logged
    [JsonProperty("validForSeconds")] public int? ValidForSeconds { get; set; }
    [JsonProperty("resolution")] public string? Resolution { get; set; }        // check_confirmation
    [JsonProperty("accountState")] public string? AccountState { get; set; }    // check_ready (and any blocking account error)
    [JsonProperty("items")] public NebulaOperationResult[]? Items { get; set; } // confirm
    [JsonProperty("diagnostic")] public string? Diagnostic { get; set; }        // <= 200 chars, sanitized, EN, for logs only
}

public sealed class UserActionEvent
{
    [JsonProperty("eventId")] public Guid EventId { get; set; }
    [JsonProperty("accountId")] public int AccountId { get; set; }
    [JsonProperty("action")] public string Action { get; set; } = "";           // NebulaUserActions.*
    [JsonProperty("operation")] public NebulaOperation Operation { get; set; } = new(); // exactly 1 id
    [JsonProperty("occurredAt")] public DateTimeOffset OccurredAt { get; set; }
}

public sealed class SetPausedRequest { [JsonProperty("paused")] public bool Paused { get; set; } }

public sealed class UnlinkRequest { [JsonProperty("reason")] public string Reason { get; set; } = "user"; }

public sealed class RotateCredentialResponse
{
    [JsonProperty("credential")] public string Credential { get; set; } = "";
    [JsonProperty("credentialGeneration")] public int CredentialGeneration { get; set; }
}

public sealed class CancelCommandMessage
{
    [JsonProperty("requestId")] public Guid RequestId { get; set; }
    [JsonProperty("reason")] public string Reason { get; set; } = "";          // result code: cancelled | not_paired | device_paused | expired
}

public sealed class AccountsChangedMessage
{
    [JsonProperty("accountIds")] public int[] AccountIds { get; set; } = [];
    [JsonProperty("full")] public bool Full { get; set; }                      // true: re-sync everything
}

public sealed class LinkStateMessage
{
    [JsonProperty("state")] public string State { get; set; } = "";            // NebulaLinkStates.*
    [JsonProperty("reason")] public string? Reason { get; set; }               // NebulaLinkReasons.*
}
