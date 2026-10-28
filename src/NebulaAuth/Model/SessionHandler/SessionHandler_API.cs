using System.Threading.Tasks;
using AchiesUtilities.Web.Models;
using NebulaAuth.Model.DeviceModels;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles;
using SteamLib.Api.Mobile;
using SteamLib.Authentication;
using SteamLib.Authentication.LoginV2;
using SteamLib.Exceptions.Authorization;
using SteamLib.SteamMobile;
using SteamLibForked.Models.Session;

namespace NebulaAuth.Model;

public partial class SessionHandler //API
{
    public static async Task RefreshMobileToken(HttpClientHandlerPair chp, Mafile mafile)
    {
        if (mafile.SessionData is not {RefreshToken.IsExpired: false})
            throw new SessionPermanentlyExpiredException(SessionInvalidException.SESSION_NULL_MSG);

        var mobileToken = await SteamMobileApi.RefreshJwt(chp.Client, mafile.SessionData.RefreshToken.Token,
            mafile.SessionData.SteamId);
        Shell.Logger.Info("MobileToken on {name} {steamid} successfully refreshed", mafile.AccountName,
            mafile.SessionData.SteamId);

        var newToken = SteamTokenHelper.Parse(mobileToken);
        mafile.SessionData.SetMobileToken(newToken);

        //Trigger PropertyChanged event for PortableMaClient handling session updated from MaClient
        mafile.SetSessionData(mafile.SessionData);
        await Storage.UpdateMafileAsync(mafile);
        chp.Handler.CookieContainer.SetSteamMobileCookies(mafile.SessionData);
    }

    public static async Task LoginAgain(HttpClientHandlerPair chp, Mafile mafile, string password, bool savePassword)
    {
        // The code comes from the shared registry: one login per 30 s window and account
        var sgGenerator = new LoginGuardCodeProvider(mafile);
        // The account's own device name: assigned now when missing and written to its mafile before the request, so a
        // failed login keeps it and a retry uses the same device
        var deviceDetails = await DeviceNames.PrepareLoginDeviceAsync(mafile);
        var options = new LoginV2ExecutorOptions(StaticLoginConsumer.Instance, chp.Client)
        {
            Logger = Shell.ExtensionsLogger,
            AuthProviders = [sgGenerator],
            DeviceDetails = deviceDetails,
            WebsiteId = "Mobile"
        };
        chp.Handler.CookieContainer.ClearMobileSessionCookies();
        var result = await LoginV2Executor.DoLogin(options, mafile.AccountName, password);
        Shell.Logger.Info("Logged in again on {name} {steamid}", mafile.AccountName, result.SteamId);
        AdmissionHelper.TransferCommunityCookies(chp.Handler.CookieContainer);

        //Triggers PropertyChanged event for PortableMaClient handling session updated from MaClient
        mafile.SetSessionData((MobileSessionData) result);
        // Saved only in an encrypted storage (the dialogs offer it only then); otherwise the password was typed for this
        // login only and is not kept
        if (SteamPasswords.CanSave)
        {
            if (savePassword) SteamPasswords.TrySet(mafile.AccountName, password);
            else SteamPasswords.Remove(mafile.AccountName);
        }

        await Storage.UpdateMafileAsync(mafile);
        // The account can log in again: a stored password rejected earlier is not blocked any more
        ClearStoredPasswordRejected(mafile.AccountName);
    }
}