using System.ComponentModel;
using System.Security.Cryptography;
using ProtoBuf;
using SteamLib.ProtoCore.Enums;
using SteamLib.ProtoCore.Interfaces;
using SteamLib.Utility;

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo
#pragma warning disable CS8618

namespace SteamLib.ProtoCore.Services;

[ProtoContract]
public class GetPasswordRSAPublicKey_Request : IProtoMsg
{
    [ProtoMember(1)] public string AccountName { get; set; }
}

[ProtoContract]
public class GetPasswordRSAPublicKey_Response : IProtoMsg
{
    [ProtoMember(1)] public string PublickKeyMod { get; set; }
    [ProtoMember(2)] public string PublickKeyExp { get; set; }
    [ProtoMember(3)] public ulong Timestamp { get; set; }
}

[ProtoContract]
public class BeginAuthSessionViaCredentials_Request : IProtoMsg
{
    [ProtoMember(1)] public string DeviceFriendlyName { get; set; }
    [ProtoMember(2)] public string AccountName { get; set; }
    [ProtoMember(3)] public string EncryptedPassword { get; set; }
    [ProtoMember(4)] public ulong EncryptionTimestamp { get; set; }
    [ProtoMember(5)] public bool RememberLogin { get; set; }
    [ProtoMember(6)] public EAuthTokenPlatformType PlatformType { get; set; }
    [ProtoMember(7)] public int Persistence { get; set; }

    /// <summary>
    ///     Gets or sets the website id that the login will be performed for.
    ///     Known values are "Unknown", "Client", "Mobile", "Website", "Store", "Community", "Partner", "SteamStats".
    /// </summary>
    /// <value>The website id.</value>
    [ProtoMember(8)]
    public string WebsiteId { get; set; }

    [ProtoMember(9)] public DeviceDetails DeviceDetails { get; set; } = new();
    [ProtoMember(10)] public string GuardData { get; set; }
    [ProtoMember(11)] public uint Language { get; set; }
}

[ProtoContract]
public class DeviceDetails : IProtoMsg
{
    /// <summary>
    ///     User-Agent if platform is SteamCommunity
    /// </summary>
    [ProtoMember(1)]
    public string DeviceFriendlyName { get; }

    [ProtoMember(2)] public EAuthTokenPlatformType PlatformType { get; }
    [ProtoMember(3)] public int? OsType { get; }
    [ProtoMember(4)] public uint? GamingDeviceType { get; }

    #region Members

    public DeviceDetails()
    {
    }

    public DeviceDetails(string deviceFriendlyName, EAuthTokenPlatformType platformType, int? osType,
        uint? gamingDeviceType)
    {
        DeviceFriendlyName = deviceFriendlyName;
        PlatformType = platformType;
        OsType = osType;
        GamingDeviceType = gamingDeviceType;
    }

    public DeviceDetails(string deviceFriendlyName, EAuthTokenPlatformType platformType, EOSType? osType,
        uint? gamingDeviceType)
    {
        DeviceFriendlyName = deviceFriendlyName;
        PlatformType = platformType;
        OsType = (int?) osType;
        GamingDeviceType = gamingDeviceType;
    }

    public static DeviceDetails CreateDefault()
    {
        return new DeviceDetails("", EAuthTokenPlatformType.WebBrowser, (int?) null, null);
    }

    public static DeviceDetails CreateCommunityDetails(string userAgent)
    {
        return new DeviceDetails(userAgent, EAuthTokenPlatformType.WebBrowser, (int?) null, null);
    }

    public static DeviceDetails CreateMobileDetails(string deviceName)
    {
        return new DeviceDetails(deviceName, EAuthTokenPlatformType.MobileApp, EOSType.AndroidUnknown, 528);
    }

    #endregion
}

[ProtoContract]
public class BeginAuthSessionViaCredentials_Response : IProtoMsg
{
    [ProtoMember(1)] public ulong ClientId { get; set; }
    [ProtoMember(2)] public byte[] RequestId { get; set; }
    [ProtoMember(3)] public float Interval { get; set; }
    [ProtoMember(4)] public List<AllowedConfirmationMsg> AllowedConfirmations { get; } = new();
    [ProtoMember(5)] public ulong Steamid { get; set; }
    [ProtoMember(6)] public string WeakToken { get; set; }
    [ProtoMember(7)] public string AgreementSessionUrl { get; set; }
    [ProtoMember(8)] public string ExtendedErrorMessage { get; set; }
}

[ProtoContract]
public class AllowedConfirmationMsg : IProtoMsg
{
    [ProtoMember(1)] public EAuthSessionGuardType ConfirmationType { get; set; }
    [ProtoMember(2)] public string AssociatedMessage { get; set; }
}

[ProtoContract]
public class PollAuthSessionStatus_Request : IProtoMsg
{
    [ProtoMember(1)] public ulong ClientId { get; set; }
    [ProtoMember(2)] public byte[] RequestId { get; set; }
    [ProtoMember(3)] public ulong TokenToRevoke { get; set; }
}

[ProtoContract]
public class PollAuthSessionStatus_Response : IProtoMsg
{
    [ProtoMember(1)] public ulong NewClientId { get; set; }
    [ProtoMember(2)] public string NewChallengeUrl { get; set; }
    [ProtoMember(3)] public string RefreshToken { get; set; }
    [ProtoMember(4)] public string AccessToken { get; set; }
    [ProtoMember(5)] public bool HadRemoteInteraction { get; set; }
    [ProtoMember(6)] public string AccountName { get; set; }
    [ProtoMember(7)] public string NewGuardData { get; set; }
    [ProtoMember(8)] public string AgreementSessionUrl { get; set; }
}

[ProtoContract]
public class UpdateAuthSessionWithSteamGuardCode_Request : IProtoMsg
{
    [ProtoMember(1)] public ulong ClientId { get; set; }

    [ProtoMember(2, DataFormat = DataFormat.FixedSize)]
    public ulong Steamid { get; set; }

    [ProtoMember(3)] public string Code { get; set; }
    [ProtoMember(4)] public EAuthSessionGuardType CodeType { get; set; }
}

[ProtoContract]
public class GetAuthSessionsForAccount_Response : IProtoMsg
{
    [ProtoMember(1)] public List<ulong> ClientIds { get; set; } = new();
}

[ProtoContract]
public class GetAuthSessionInfo_Request : IProtoMsg
{
    [ProtoMember(1)] public ulong ClientId { get; set; }
}

[ProtoContract]
public class GetAuthSessionInfo_Response : IProtoMsg
{
    [ProtoMember(1)] public string IP { get; set; }
    [ProtoMember(2)] public string GeoLoc { get; set; }
    [ProtoMember(3)] public string City { get; set; }
    [ProtoMember(4)] public string State { get; set; }
    [ProtoMember(5)] public string Country { get; set; }
    [ProtoMember(6)] public EAuthTokenPlatformType PlatformType { get; set; }
    [ProtoMember(7)] public string DeviceFriendlyName { get; set; }
    [ProtoMember(8)] public int Version { get; set; }
    [ProtoMember(9)] public EAuthSessionSecurityHistory LoginHistory { get; set; }
    [ProtoMember(10)] public bool RequestorLocationMismatch { get; set; }
    [ProtoMember(11)] public bool HighUsageLogin { get; set; }

    /// <summary>
    ///     Proto default is <see cref="ESessionPersistence.Invalid" /> (-1): an absent field reads as Invalid.
    /// </summary>
    [ProtoMember(12)]
    [DefaultValue(ESessionPersistence.Invalid)]
    public ESessionPersistence RequestedPersistence { get; set; } = ESessionPersistence.Invalid;

    [ProtoMember(13)] public int DeviceTrust { get; set; }
    [ProtoMember(14)] public EAuthTokenAppType AppType { get; set; }
}

[ProtoContract]
public class UpdateAuthSessionWithMobileConfirmation_Request : IProtoMsg
{
    [ProtoMember(1)] public int Version { get; set; }
    [ProtoMember(2)] public ulong ClientId { get; set; }

    [ProtoMember(3, DataFormat = DataFormat.FixedSize)]
    public ulong Steamid { get; set; }

    [ProtoMember(4)] public byte[] Signature { get; set; }
    [ProtoMember(5)] public bool Confirm { get; set; }
    [ProtoMember(6)] public int Persistence { get; set; }


    public void ComputeSignature(string privateKey)
    {
        var signatureData = new byte[2 + 8 + 8];
        BitConverter.GetBytes(Version).CopyTo(signatureData, 0);
        BitConverter.GetBytes(ClientId).CopyTo(signatureData, 2);
        BitConverter.GetBytes(Steamid).CopyTo(signatureData, 10);

        using (var hmac = new HMACSHA256(Convert.FromBase64String(privateKey)))
        {
            Signature = hmac.ComputeHash(signatureData);
        }
    }
}

[ProtoContract]
public class GenerateAccessTokenForApp_Request : IProtoMsg
{
    [ProtoMember(1)] public string RefreshToken { get; set; }

    [ProtoMember(2, DataFormat = DataFormat.FixedSize)]
    public long SteamId { get; set; }

    [ProtoMember(3)] public bool TokenRenewalType { get; set; } = true; //FIXME: enum: ETokenRenewalType 
}

[ProtoContract]
public class GenerateAccessTokenForApp_Response : IProtoMsg
{
    [ProtoMember(1)] public string AccessToken { get; set; }
}