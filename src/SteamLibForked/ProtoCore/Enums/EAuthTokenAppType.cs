using ProtoBuf;

namespace SteamLib.ProtoCore.Enums;

[ProtoContract]
public enum EAuthTokenAppType
{
    Unknown = 0,
    MobileSteamApp = 1,
    MobileChatApp = 2
}
