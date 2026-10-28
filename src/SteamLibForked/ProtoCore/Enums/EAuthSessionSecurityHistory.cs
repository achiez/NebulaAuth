using ProtoBuf;

namespace SteamLib.ProtoCore.Enums;

[ProtoContract]
public enum EAuthSessionSecurityHistory
{
    Invalid = 0,
    UsedPreviously = 1,
    NoPriorHistory = 2
}
