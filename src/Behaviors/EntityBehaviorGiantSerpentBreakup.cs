using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UnderwaterHorrors;

/// <summary>
/// Server side: a killed giant serpent breaks apart.
///
/// For breakDelay seconds after it dies the body goes limp, holding its last
/// curve, and starts to sink. Then, with a crack of rusted metal, it falls
/// into one piece per spine bone (GiantSerpentPieceEntity): the head with
/// its jaws and teeth, each neck and tail segment with its own fins and
/// spikes. Every piece is laid exactly where and how that bone was drawn,
/// side wave and all, so the body comes apart in place. Each piece holds
/// its share of the hoard for a knife to harvest, drifts a little way from
/// its neighbours and sinks to the sea floor.
///
/// Only real deaths break apart (vanilla fires OnEntityDeath for damage
/// deaths only); a giant that swims out of sight just goes. The corpse is
/// removed once its pieces are out, and its hit proxies go with it.
/// </summary>
public class EntityBehaviorGiantSerpentBreakup : EntityBehavior
{
    public const string BrokenAttr = "underwaterhorrors:brokenUp";

    private AssetLocation pieceCode = new("underwaterhorrors", "giantserpentpiece");
    private float breakDelay = 2f;
    private float deadFor;

    private string[] bones = Array.Empty<string>();
    private double[] arcs = Array.Empty<double>();
    private double[] elev = Array.Empty<double>();
    private double[][] extents = Array.Empty<double[]>();
    private double size = 5;

    // The rusted spine snapping, slowed down for something this big.
    private static readonly AssetLocation CrackSound = new("game", "sounds/effect/toolbreak");
    private static readonly AssetLocation GroanSound = new("game", "sounds/effect/treefell");

    public EntityBehaviorGiantSerpentBreakup(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        string code = attributes["pieceEntity"].AsString(null);
        if (code != null) pieceCode = new AssetLocation(code);
        breakDelay = attributes["breakDelay"].AsFloat(2f);
        bones = GiantSerpentRig.SpineBones(properties);
        arcs = GiantSerpentRig.SpineArcs(properties);
        elev = GiantSerpentRig.SpineElev(properties);
        extents = GiantSerpentRig.PieceExtents(properties);
        size = properties.Client?.Size ?? 5;
    }

    public override void OnGameTick(float deltaTime)
    {
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (entity.Alive)
        {
            deadFor = 0;
            return;
        }
        if (!entity.WatchedAttributes.GetBool(EntityBehaviorSerpentDeath.DiedForRealAttr)) return;
        if (entity.WatchedAttributes.GetBool(BrokenAttr)) return;

        deadFor += deltaTime;
        if (deadFor < breakDelay) return;
        BreakApart();
    }

    private void BreakApart()
    {
        entity.WatchedAttributes.SetBool(BrokenAttr, true);
        int n = bones.Length;
        if (n == 0 || arcs.Length < n + 1 || extents.Length < n)
        {
            entity.Api.Logger.Warning("[underwaterhorrors] giant serpent: no spine numbers in the entity file, it cannot break apart");
            return;
        }

        var world = entity.World;
        var rand = world.Rand;
        var ai = entity.GetBehavior<EntityBehaviorGiantSerpentAI>();

        // Every joint: each bone's pivot, then the end of the last link.
        var jx = new double[n + 1];
        var jy = new double[n + 1];
        var jz = new double[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double s = arcs[i] * size;
            if (ai != null)
            {
                ai.SpinePoint(s, out jx[i], out jy[i], out jz[i]);
            }
            else
            {
                jx[i] = entity.Pos.X - Math.Sin(entity.Pos.Yaw) * s;
                jy[i] = entity.Pos.Y;
                jz[i] = entity.Pos.Z - Math.Cos(entity.Pos.Yaw) * s;
            }
        }

        int spawned = 0;
        for (int i = 0; i < n; i++)
        {
            EntityProperties props = world.GetEntityType(new AssetLocation(pieceCode.Domain, pieceCode.Path + "-" + bones[i]));
            if (props == null) continue;

            // The piece's model runs tailward along +x; the renderer turns
            // +x to (-sin yaw, 0, -cos yaw), and Roll lifts the tail end.
            double dx = jx[i + 1] - jx[i], dy = jy[i + 1] - jy[i], dz = jz[i + 1] - jz[i];
            double h = Math.Sqrt(dx * dx + dz * dz);
            float yaw = (float)Math.Atan2(-dx, -dz);
            float roll = (float)(Math.Atan2(dy, Math.Max(1e-6, h)) - (i < elev.Length ? elev[i] : 0));

            Entity piece = world.ClassRegistry.CreateEntity(props);
            piece.Pos.SetPos(jx[i], jy[i], jz[i]);
            piece.Pos.Dimension = entity.Pos.Dimension;
            piece.Pos.Yaw = GameMath.Mod(yaw, GameMath.TWOPI);
            piece.Pos.Roll = roll;
            piece.Pos.SetFrom(piece.Pos);

            double[] ext = extents[i];
            piece.WatchedAttributes.SetAttribute(GiantSerpentPieceEntity.ExtentAttr, new FloatArrayAttribute(new[]
            {
                (float)(ext[0] * size), (float)(ext[1] * size), (float)(ext[2] * size), (float)(ext[3] * size), (float)(ext[4] * size)
            }));
            piece.WatchedAttributes.SetDouble(GiantSerpentPieceEntity.BornAttr, world.Calendar.TotalHours);

            // Apart from its neighbours: out along the body from the middle,
            // and a little sideways, slowing as the water takes it.
            double along = (i - (n - 1) / 2.0) / n;
            double side = (rand.NextDouble() - 0.5) * 2;
            double ux = h > 1e-6 ? dx / h : 0, uz = h > 1e-6 ? dz / h : 0;
            piece.Attributes.SetDouble("driftX", (ux * along * 3 + uz * side) * 0.6);
            piece.Attributes.SetDouble("driftZ", (uz * along * 3 - ux * side) * 0.6);
            piece.Attributes.SetFloat("spin", (float)((rand.NextDouble() - 0.5) * 0.4));

            world.SpawnEntity(piece);
            spawned++;

            Burst(jx[i], jy[i], jz[i], (float)(ext[4] * size));
        }

        // A few cracks along the body rather than twenty at once.
        for (int k = 0; k <= 3; k++)
        {
            int i = Math.Min(n, k * n / 3);
            world.PlaySoundAt(CrackSound, jx[i], jy[i], jz[i], null, 0.45f + (float)rand.NextDouble() * 0.15f, 96, 1f);
        }
        world.PlaySoundAt(GroanSound, jx[n / 2], jy[n / 2], jz[n / 2], null, 0.5f, 128, 1f);

        entity.Api.Logger.Notification("[underwaterhorrors] Rust Leviathan at ({0:F0}, {1:F0}, {2:F0}) broke apart into {3} pieces",
            entity.Pos.X, entity.Pos.Y, entity.Pos.Z, spawned);

        // The pieces take over: remove the corpse, and with it the proxies.
        if (entity is EntityAgent agent)
        {
            entity.DespawnReason = new EntityDespawnData { Reason = EnumDespawnReason.Removed };
            agent.AllowDespawn = true;
        }
    }

    // Rust flakes and a rush of bubbles where the body splits.
    private void Burst(double x, double y, double z, float halfWidth)
    {
        var world = entity.World;
        var flakes = new SimpleParticleProperties(
            20, 35, ColorUtil.ToRgba(255, 120, 62, 28),
            new Vec3d(x - halfWidth, y - halfWidth, z - halfWidth), new Vec3d(x + halfWidth, y + halfWidth, z + halfWidth),
            new Vec3f(-1.2f, -0.6f, -1.2f), new Vec3f(1.2f, 1.2f, 1.2f),
            3f, 0.15f, 0.4f, 1.1f, EnumParticleModel.Cube);
        world.SpawnParticles(flakes);

        var bubbles = new SimpleParticleProperties(
            25, 40, ColorUtil.ToRgba(150, 200, 225, 255),
            new Vec3d(x - halfWidth, y - halfWidth, z - halfWidth), new Vec3d(x + halfWidth, y + halfWidth, z + halfWidth),
            new Vec3f(-0.4f, 0.6f, -0.4f), new Vec3f(0.4f, 2.0f, 0.4f),
            3.5f, -0.05f, 0.2f, 0.6f, EnumParticleModel.Quad);
        world.SpawnParticles(bubbles);
    }

    public override string PropertyName() => "underwaterhorrors:giantserpentbreakup";
}
