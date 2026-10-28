using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.LinkHost;
using NebulaAuth.NebulaLink;
using NebulaAuth.NebulaLink.Confirmations;
using SteamLib.Abstractions;
using SteamLib.Api.Services;
using SteamLib.Authentication;
using SteamLib.ProtoCore.Enums;

namespace NebulaAuth.Model;

/// <summary>
///     Steam Guard code of a password login in the app (manual relogin, automatic login of a Nebula Link account): taken
///     from the app's registry (<see cref="NebulaLinkHost.GuardCodes" />), so it never reuses the code a login of this account (here or through
///     Achiefy) already got in the same 30 s window.
/// </summary>
public sealed class LoginGuardCodeProvider(Mafile mafile) : IAuthProvider
{
    public bool IsSupportedGuardType(ILoginConsumer consumer, EAuthSessionGuardType type)
    {
        return type == EAuthSessionGuardType.DeviceCode;
    }

    public async Task UpdateAuthSession(HttpClient authClient, ILoginConsumer loginConsumer, UpdateAuthSessionModel model,
        CancellationToken cancellationToken = default)
    {
        var (code, _) = await NebulaLinkHost.GuardCodes.GetCodeAsync(AccountSteamClientPool.KeyOf(mafile), mafile.SharedSecret,
            cancellationToken);
        var req = AuthRequestHelper.CreateMobileCodeRequest(code, model.ClientId, model.SteamId);
        await AuthenticationServiceApi.UpdateAuthSessionWithSteamGuardCode(authClient, req, cancellationToken);
    }
}
