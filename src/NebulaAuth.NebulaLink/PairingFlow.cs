using System;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.NebulaLink.Protocol;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     Pairing: the pasted link or code is redeemed at a published API base, the claim is polled until the cabinet
///     decides, and on approval the device credential is stored first (secure store), then the link in nebula.json. A
///     failed or cancelled pairing leaves nothing behind.
/// </summary>
internal sealed partial class PairingFlow(INebulaHttpFactory http, LinkStore links, DeviceCredentialManager credentials,
    Func<DateTimeOffset> clock, Action onPaired)
{
    private PendingPairing? _pairing;

    public PendingPairing? Pairing => _pairing;

    /// <summary>Redeems what the user pasted (a pairing link or a bare code, see <see cref="PairingLink" />).</summary>
    /// <exception cref="NebulaApiException">Input invalid, origin not allowed, code invalid, network...</exception>
    public Task<PendingPairing> RedeemAsync(string input, CancellationToken ct)
    {
        if (!PairingLink.TryParse(input, out var target, out var error))
            throw new NebulaApiException(error!, "The pairing input is not valid");
        return RedeemAsync(target!, ct);
    }

    /// <summary>Redeems a pairing code at an allowed API base. The returned pairing is shown on the verify page.</summary>
    /// <exception cref="NebulaApiException">Origin not allowed, code invalid, rate limited, network, redirect...</exception>
    public async Task<PendingPairing> RedeemAsync(PairingTarget target, CancellationToken ct)
    {
        var normalized = PairingLink.NormalizeCode(target.Code) ??
                         throw new NebulaApiException(NebulaApiException.CODE_PAIRING_CODE_INVALID, "Invalid code format");
        // Checked before any request: only published API bases (Debug: also loopback)
        if (!Uri.TryCreate(target.TenantBaseUrl, UriKind.Absolute, out var baseUri) ||
            !NebulaLinkApis.IsAllowedBase(target.TenantBaseUrl) || NebulaLinkApis.GetOrigin(baseUri) != target.Origin)
            throw new NebulaApiException(PairingLink.ERROR_ORIGIN_NOT_ALLOWED, "The Achiefy address is not allowed");
        using var client = new PairingClient(http.CreateHandler(), target.TenantBaseUrl);
        var response = await client.RedeemAsync(new PairingRedeemRequest
        {
            Code = normalized,
            Device = LinkDeviceInfo.Create(links.Snapshot()?.DeviceName),
            ProtocolMajor = NebulaProtocol.Major,
            ProtocolMinor = NebulaProtocol.Minor
        }, ct);
        RuntimeSecrets.Register(response.ClaimSecret);
        var pairing = new PendingPairing
        {
            PairingId = response.PairingId,
            ClaimSecret = response.ClaimSecret,
            Origin = target.Origin,
            TenantBaseUrl = target.TenantBaseUrl,
            ApiOrigin = response.ApiOrigin,
            TenantId = response.TenantId,
            TenantLabel = response.TenantLabel,
            AccountLabel = response.AccountLabel,
            ApprovalExpiresAt = response.ApprovalExpiresAt,
            PollIntervalMs = Math.Clamp(response.PollIntervalMs <= 0 ? 2000 : response.PollIntervalMs, 1000, 10000),
            OriginMatches = NebulaLinkApis.SameOrigin(response.ApiOrigin, target.Origin)
        };
        NebulaLinkLog.Info($"Pairing {pairing.PairingId} redeemed (origin matches: {pairing.OriginMatches})");
        _pairing = pairing;
        if (!pairing.OriginMatches && NebulaLinkApis.AllowUntrusted)
        {
            NebulaLinkLog.Warn($"apiOrigin \"{response.ApiOrigin}\" differs from \"{target.Origin}\": accepted (untrusted addresses are allowed)");
        }
        else if (!pairing.OriginMatches)
        {
            // Another server answered for this address: the pairing is aborted before anything is stored
            await CancelAsync();
            throw new NebulaApiException(NebulaApiException.CODE_ORIGIN_MISMATCH, "The server reported another origin");
        }

        return pairing;
    }

    /// <summary>Cancels the pending pairing on the server (best effort).</summary>
    public async Task CancelAsync()
    {
        var pairing = Interlocked.Exchange(ref _pairing, null);
        if (pairing == null) return;
        try
        {
            using var client = new PairingClient(http.CreateHandler(), pairing.TenantBaseUrl);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.CancelAsync(new PairingCancelRequest {PairingId = pairing.PairingId, ClaimSecret = pairing.ClaimSecret},
                cts.Token);
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn($"Pairing {pairing.PairingId} cancel failed", ex);
        }
        finally
        {
            RuntimeSecrets.Unregister(pairing.ClaimSecret);
        }
    }

    /// <summary>
    ///     Polls claim until the cabinet approves or rejects, the approval window ends or <paramref name="ct" /> cancels.
    ///     On approval the credential is stored first (secure store), then nebula.json, then the connection starts.
    /// </summary>
    public async Task<PairingWaitResult> WaitForApprovalAsync(CancellationToken ct)
    {
        var pairing = _pairing ?? throw new InvalidOperationException("No pending pairing");
        using var client = new PairingClient(http.CreateHandler(), pairing.TenantBaseUrl);
        var failures = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            PairingClaimResponse response;
            try
            {
                response = await client.ClaimAsync(
                    new PairingClaimRequest {PairingId = pairing.PairingId, ClaimSecret = pairing.ClaimSecret}, ct);
                failures = 0;
            }
            catch (NebulaApiException ex) when (ex.Code is NebulaApiException.CODE_NETWORK or NebulaResultCodes.RateLimited
                                                     or NebulaApiException.CODE_BAD_RESPONSE)
            {
                if (++failures > 30 || clock() > pairing.ApprovalExpiresAt + TimeSpan.FromMinutes(1))
                    return PairingWaitResult.Failed;
                await Task.Delay(pairing.PollIntervalMs * 2, ct);
                continue;
            }
            catch (NebulaApiException ex)
            {
                NebulaLinkLog.Warn($"Pairing {pairing.PairingId} claim failed: {ex.Code}");
                _pairing = null;
                return ex.Code == NebulaApiException.CODE_PAIRING_NOT_FOUND ? PairingWaitResult.Expired : PairingWaitResult.Failed;
            }

            switch (response.Status)
            {
                case NebulaPairingClaimStatuses.AwaitingApproval:
                    await Task.Delay(response.PollIntervalMs > 0 ? Math.Clamp(response.PollIntervalMs, 1000, 10000) : pairing.PollIntervalMs, ct);
                    continue;
                case NebulaPairingClaimStatuses.Approved when response.LinkId is { } linkId &&
                                                               !string.IsNullOrEmpty(response.Credential):
                    _pairing = null;
                    RuntimeSecrets.Unregister(pairing.ClaimSecret);
                    return await CompleteAsync(pairing, linkId, response.Credential!)
                        ? PairingWaitResult.Approved
                        : PairingWaitResult.StoreFailed;
                case NebulaPairingClaimStatuses.Rejected:
                    _pairing = null;
                    return PairingWaitResult.Rejected;
                case NebulaPairingClaimStatuses.Expired:
                    _pairing = null;
                    return PairingWaitResult.Expired;
                case NebulaPairingClaimStatuses.Cancelled:
                    _pairing = null;
                    return PairingWaitResult.Cancelled;
                default:
                    // "completed" without a credential: another claim got it; nothing usable here
                    _pairing = null;
                    return PairingWaitResult.Failed;
            }
        }
    }

    private async Task<bool> CompleteAsync(PendingPairing pairing, Guid linkId, string credential)
    {
        // A link of an earlier pairing is replaced (its credential is useless now: the server revoked it)
        var previous = links.Snapshot();
        var store = await credentials.StoreNewAsync(linkId, credential);
        if (store == null) return false;
        if (previous?.LinkId is { } oldLink && oldLink != linkId)
            await credentials.DeleteAsync(oldLink, previous.CredentialStore);

        links.SetLink(linkId, d =>
        {
            d.Paused = false;
            d.TenantBaseUrl = pairing.TenantBaseUrl;
            d.TenantId = pairing.TenantId;
            d.TenantLabel = pairing.TenantLabel;
            d.AccountLabel = pairing.AccountLabel;
            d.DeviceName ??= LinkDeviceInfo.DefaultDeviceName;
            d.PairedAt = clock();
            d.CredentialStore = store.Kind;
            d.IncompatibleAt = null;
            d.IncompatibleServerMajor = null;
        });
        NebulaLinkLog.Info($"Paired: link {linkId:N}, credential in {store.DisplayName}");
        onPaired();
        return true;
    }
}
