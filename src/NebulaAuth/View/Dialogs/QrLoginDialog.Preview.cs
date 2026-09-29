#if DEBUG
using NebulaAuth.ViewModel.Other;
using SteamLib.ProtoCore.Enums;
using SteamLib.ProtoCore.Services;

namespace NebulaAuth.View.Dialogs;

public partial class QrLoginDialog
{
    /// <summary>
    ///     Debug preview (NEBULA_PREVIEW=NebulaAuth.View.Dialogs.QrLoginDialog), fake session info, no network.
    ///     NEBULA_PREVIEW_ARG: "risk" = every risk flag set, "minimal" = Steam returned no device / location,
    ///     anything else = a regular browser login.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        return new QrLoginDialog
        {
            DataContext = new QrLoginDialogVM("alphaecho", "76561198000007919", DemoSessionInfo(arg))
        };
    }

    internal static GetAuthSessionInfo_Response DemoSessionInfo(string? kind)
    {
        return kind switch
        {
            "minimal" => new GetAuthSessionInfo_Response {Version = 1},
            "risk" => new GetAuthSessionInfo_Response
            {
                IP = "198.51.100.77",
                GeoLoc = "52.52,13.40",
                City = "Berlin",
                State = "Berlin",
                Country = "Germany",
                PlatformType = EAuthTokenPlatformType.WebBrowser,
                DeviceFriendlyName = "Firefox, Linux",
                Version = 1,
                LoginHistory = EAuthSessionSecurityHistory.NoPriorHistory,
                RequestorLocationMismatch = true,
                HighUsageLogin = true,
                RequestedPersistence = ESessionPersistence.Persistent
            },
            _ => new GetAuthSessionInfo_Response
            {
                IP = "203.0.113.42",
                GeoLoc = "50.45,30.52",
                City = "Kyiv",
                State = "Kyiv City",
                Country = "Ukraine",
                PlatformType = EAuthTokenPlatformType.WebBrowser,
                DeviceFriendlyName = "Chrome, Windows",
                Version = 1,
                LoginHistory = EAuthSessionSecurityHistory.UsedPreviously,
                RequestedPersistence = ESessionPersistence.Persistent
            }
        };
    }
}
#endif
