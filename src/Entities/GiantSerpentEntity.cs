using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace UnderwaterHorrors;

/// <summary>
/// The giant serpent. Its body reaches some 220 blocks behind the entity
/// position (the middle of its mouth), so:
///
/// - the frustum sphere covers the whole body, or it would vanish whenever
///   the mouth left the screen;
/// - AllowOutsideLoadedRange lets clients keep drawing it when the mouth is
///   beyond their view distance or in a chunk they have not drawn, while
///   its body is right next to them (the Erel does the same);
/// - its tracking range is raised so clients keep it while any part of its
///   body could still be seen: a giant swimming away trails its whole body
///   back toward the player, and is only removed once that is out of sight
///   (EntityBehaviorGiantSerpentAI).
/// </summary>
public class GiantSerpentEntity : DeepSerpentEntity
{
    private double reach = 450;

    public override double FrustumSphereRadius => reach + 20;

    public override bool AllowOutsideLoadedRange => true;

    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        base.Initialize(properties, api, InChunkIndex3d);
        reach = GiantSerpentRig.BodyLength(properties) * (properties.Client?.Size ?? 10);
        SimulationRange = (int)Math.Max(SimulationRange, reach + GiantSerpentSilhouette.FadeEndBlocks + 40);
    }
}

/// <summary>
/// Rig numbers for the giant serpent, read from the entity JSON's
/// "giantSerpent" attributes so the client body, the AI and the hit proxies
/// all use the same ones. Lengths are blocks at entity size 1.
/// </summary>
public static class GiantSerpentRig
{
    private static JsonObject Rig(EntityProperties p) => p?.Attributes?["giantSerpent"];

    /// <summary>Mouth to tail tip.</summary>
    public static double BodyLength(EntityProperties p) => Rig(p)?["bodyLength"].AsDouble(44.5) ?? 44.5;

    /// <summary>Sideways reach of the swimming wave.</summary>
    public static double WaveAmplitude(EntityProperties p) => Rig(p)?["waveAmplitude"].AsDouble(0.6) ?? 0.6;

    /// <summary>Length of one full side-to-side wave.</summary>
    public static double WaveLength(EntityProperties p) => Rig(p)?["waveLength"].AsDouble(11) ?? 11;

    /// <summary>Spine bones from the head back, as named in the shape.</summary>
    public static string[] SpineBones(EntityProperties p) =>
        Rig(p)?["spineBones"].AsArray<string>(Array.Empty<string>()) ?? Array.Empty<string>();
}
