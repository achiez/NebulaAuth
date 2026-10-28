using ProtoBuf;

namespace SteamLib.ProtoCore.Enums;

[ProtoContract]
public enum ESessionPersistence
{
    Invalid = -1,
    Ephemeral = 0,
    Persistent = 1
}
