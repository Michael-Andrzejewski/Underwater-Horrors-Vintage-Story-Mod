using ProtoBuf;

namespace UnderwaterHorrors;

[ProtoContract]
public class DebugToggleMessage
{
    [ProtoMember(1)]
    public string Toggle;

    [ProtoMember(2)]
    public bool Active;
}
