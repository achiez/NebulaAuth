using System.Threading.Tasks;
using AchiesUtilities.Web.Models;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles.Protection;
using NebulaAuth.NebulaLink.Confirmations;
using NebulaAuth.NebulaLink.Sessions;
using SteamLib;

namespace NebulaAuth.Model.LinkHost;

/// <summary>The app's backend: <see cref="SessionHandler" /> API calls and its rejected-password flag, the Steam password store.</summary>
public sealed class AppHeadlessSessionBackend : IHeadlessSessionBackend
{
    public bool IsInvalidPasswordFlagged(MobileDataExtended mafile)
    {
        return SessionHandler.IsStoredPasswordRejected((Mafile) mafile);
    }

    public void SetInvalidPasswordFlag(MobileDataExtended mafile, bool value)
    {
        if (value) SessionHandler.MarkStoredPasswordRejected((Mafile) mafile);
        else SessionHandler.ClearStoredPasswordRejected(mafile.AccountName);
    }

    public string? GetStoredPassword(MobileDataExtended mafile)
    {
        return SteamPasswords.Get(mafile.AccountName);
    }

    public Task RefreshAsync(HttpClientHandlerPair chp, MobileDataExtended mafile)
    {
        return SessionHandler.RefreshMobileToken(chp, (Mafile) mafile);
    }

    public Task LoginAsync(HttpClientHandlerPair chp, MobileDataExtended mafile, string password)
    {
        return SessionHandler.LoginAgain(chp, (Mafile) mafile, password, true);
    }

    public void OnSessionRenewed(MobileDataExtended mafile)
    {
        NebulaLinkHost.Confirmations.Invalidate(AccountSteamClientPool.KeyOf(mafile));
    }
}
