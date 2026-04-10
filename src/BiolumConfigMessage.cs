using ProtoBuf;

namespace UnderwaterHorrors;

[ProtoContract]
public class BiolumConfigMessage
{
    [ProtoMember(1)]
    public float PulseSpeed;

    [ProtoMember(2)]
    public int GlowMin;

    [ProtoMember(3)]
    public int GlowMax;

    [ProtoMember(4)]
    public int BodyGlowMin;

    [ProtoMember(5)]
    public int BodyGlowMax;
}
