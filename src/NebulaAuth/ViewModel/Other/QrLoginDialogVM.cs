using System.Collections.Generic;
using System.Linq;
using NebulaAuth.Core;
using SteamLib.ProtoCore.Enums;
using SteamLib.ProtoCore.Services;

namespace NebulaAuth.ViewModel.Other;

/// <summary>
///     Confirmation of a QR login: the account that approves and what Steam reported about the login attempt
///     (GetAuthSessionInfo). Empty values are shown as "Unknown".
/// </summary>
public class QrLoginDialogVM
{
    private const string LOC_PATH = "QrLoginDialog";

    public string AccountName { get; }
    public string? SteamId { get; }
    public bool HasSteamId => !string.IsNullOrEmpty(SteamId);
    public string Device { get; }
    public string Platform { get; }

    /// <summary>The platform is shown under the device name (the device falls back to the platform).</summary>
    public bool ShowPlatform => Device != Platform;

    public string IpAddress { get; }
    public string Location { get; }

    /// <summary>Risk flags reported by Steam, one per line (null when none).</summary>
    public string? RiskText { get; }

    public bool HasRisks => RiskText != null;

    /// <summary>Warning with the risk flags and the phishing reminder, or only the reminder as an info callout.</summary>
    public string HintText { get; }

    public HintBoxSeverity HintSeverity => HasRisks ? HintBoxSeverity.Warning : HintBoxSeverity.Info;

    public QrLoginDialogVM(string accountName, string? steamId, GetAuthSessionInfo_Response info)
    {
        AccountName = accountName;
        SteamId = steamId;
        var unknown = Loc("Unknown", "Unknown");
        Platform = PlatformName(info.PlatformType);
        Device = NotEmpty(info.DeviceFriendlyName) ?? Platform;
        IpAddress = NotEmpty(info.IP) ?? unknown;
        var location = new[] {info.City, info.State, info.Country}.Select(NotEmpty).OfType<string>().Distinct()
            .ToArray();
        Location = location.Length > 0 ? string.Join(", ", location) : unknown;

        var risks = new List<string>();
        if (info.RequestorLocationMismatch)
            risks.Add(Loc("The request comes from a different location than this device", "RiskLocationMismatch"));
        if (info.LoginHistory == EAuthSessionSecurityHistory.NoPriorHistory)
            risks.Add(Loc("No previous logins from this device or location", "RiskNoPriorHistory"));
        if (info.HighUsageLogin)
            risks.Add(Loc("Unusually many login attempts", "RiskHighUsage"));
        RiskText = risks.Count > 0 ? string.Join("\n", risks.Select(r => "• " + r)) : null;

        var safety = Loc("Never approve a QR code that someone sent you or asked you to scan: " +
                         "it gives them full access to the account.", "SafetyHint");
        HintText = RiskText == null
            ? safety
            : Loc("Steam flagged this login attempt:", "RiskHeader") + "\n" + RiskText + "\n\n" + safety;
    }

    private static string PlatformName(EAuthTokenPlatformType platform)
    {
        return platform switch
        {
            EAuthTokenPlatformType.SteamClient => Loc("Steam client", "Platform", "SteamClient"),
            EAuthTokenPlatformType.WebBrowser => Loc("Web browser", "Platform", "WebBrowser"),
            EAuthTokenPlatformType.MobileApp => Loc("Mobile app", "Platform", "MobileApp"),
            _ => Loc("Unknown device", "Platform", "Unknown")
        };
    }

    private static string? NotEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string Loc(string def, params string[] path)
    {
        return LocManager.GetOrDefault(def, [LOC_PATH, ..path]);
    }
}
