using ProtoBuf;

namespace UnderwaterHorrors;

/// <summary>
/// Client to server: the player right clicked a Rust Leviathan segment with a
/// knife. The server's own entity click is measured from the segment's pivot,
/// at one end of a body up to 15 blocks long, and drops clicks on the far
/// end; GiantSerpentPieceEntity.TryCutOpen measures reach against the body.
/// </summary>
[ProtoContract]
public class SegmentCutMessage
{
    [ProtoMember(1)]
    public long EntityId;
}
