using ProtoBuf;

namespace UnderwaterHorrors;

/// <summary>
/// Server to client request to play (or stop) a sea monster sound for one player.
/// The server decides what should play and enforces the single channel rule via
/// per player busy timers. The client plays it on a single managed slot so that
/// a bite can cut off whatever was playing.
/// </summary>
[ProtoContract]
public class MonsterSoundMessage
{
    /// <summary>Domain relative asset path, e.g. "sounds/creature/bite".</summary>
    [ProtoMember(1)]
    public string Sound;

    [ProtoMember(2)]
    public float Volume = 1f;

    /// <summary>When true, cut the current channel sound and play this on top immediately.</summary>
    [ProtoMember(3)]
    public bool Bite;

    /// <summary>When true, stop the current channel sound and play nothing.</summary>
    [ProtoMember(4)]
    public bool Stop;

    /// <summary>World position to play the sound at (positional 3D audio).</summary>
    [ProtoMember(5)]
    public double X;

    [ProtoMember(6)]
    public double Y;

    [ProtoMember(7)]
    public double Z;

    /// <summary>Source creature entity id. The client keeps one sound slot per creature.</summary>
    [ProtoMember(8)]
    public long EntityId;

    /// <summary>Distance (blocks) the sound stays at full volume before it starts attenuating.</summary>
    [ProtoMember(9)]
    public float RefDistance = 8f;

    /// <summary>
    /// When true, play as a non-positional 2D sound at exactly Volume (the server has already
    /// applied any distance falloff per player). Used for the dramatic surface screech so it is
    /// clearly audible. 2D sounds always override the creature's current sound.
    /// </summary>
    [ProtoMember(10)]
    public bool TwoD;
}
