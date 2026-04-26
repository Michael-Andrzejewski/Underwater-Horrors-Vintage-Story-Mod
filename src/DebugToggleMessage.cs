using ProtoBuf;

namespace UnderwaterHorrors;

[ProtoContract]
public class DebugToggleMessage
{
    [ProtoMember(1)]
    public string Toggle;

    [ProtoMember(2)]
    public bool Active;

    // Optional integer payload. Used by Toggle="biolum_mode" to carry
    // the mode index (0..N) without inventing a separate packet type.
    [ProtoMember(3)]
    public int Value;
}
