using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Client side: bends the giant serpent's spine every frame so the body
/// follows the path its mouth swam (a <see cref="SerpentTrail"/> of the
/// interpolated entity position), with a sideways swimming wave on top.
///
/// The giant's shape (tools/giantserpent/build_shape.py) is rooted at the
/// middle of the mouth, which sits exactly on the entity position, and its
/// spine runs head first: head, neck10 ... neck1, chest, chest4 ... tail20.
/// Each bone pivots at its head-side joint. For every bone the wanted
/// direction of its link (pivot to next pivot) is taken from the trail,
/// turned into the parent bone's frame, and written as a yaw (degOffY) and
/// pitch (degOffZ). The animator applies those on top of the clips, and
/// only animates elements some clip names, which is why the shape's idle
/// clip carries zero keyframes for every spine bone.
///
/// Runs at the Before render stage after giantserpentmotion (order 0) has
/// moved the entity and before the entity animator (order 0.4) builds this
/// frame's matrices. A dead serpent keeps its last pose.
/// </summary>
public class EntityBehaviorGiantSerpentBody : EntityBehavior, IRenderer
{
    public const string SeedAttr = "underwaterhorrors:giantTrailSeed";
    public const double SeedSpacing = 8.0;

    // Half the stretch of trail the swimming wave's sideways direction is
    // measured across, in blocks at size 1 (see SerpentTrail.SampleSmooth).
    // Under half the shortest spine link (1.06 at size 1).
    public const double TangentSpan = 0.25;

    // A bone never bends further than this from its parent, so a glitched
    // trail (a teleport inside one frame) can't fold the body inside out.
    private const double MaxBendRad = 75 * GameMath.DEG2RAD;

    private ICoreClientAPI capi;
    private string[] boneNames = Array.Empty<string>();
    private double waveAmplitude, waveLength;

    private IAnimator rigAnimator;
    private ElementPose[] poses;
    private double[] restArc;   // mouth to each bone pivot, plus the end of the last link (size-1 blocks)
    private double[] restElev;  // rest elevation of each bone's link, radians
    private double bodyLength;  // mouth to tail tip, size-1 blocks

    private SerpentTrail trail;
    private double[] arcs, qx, qy, qz, qodo, qtx, qtz;
    private bool warnedMissingBone;

    /// <summary>
    /// Spine joints as last posed, relative to the mouth (the entity
    /// position): the mouth, each bone's pivot, and the end of the last
    /// link. A dead serpent keeps its last set, which moves with the sinking
    /// corpse. Read by the lighting.
    /// </summary>
    public double[] RelX { get; private set; }
    public double[] RelY { get; private set; }
    public double[] RelZ { get; private set; }
    public int JointCount { get; private set; }

    private GiantSerpentSilhouette silhouette;
    private GiantSerpentGlow glow;

    /// <summary>Bioluminescent dots on a glowing giant (0 on a plain one).</summary>
    public int GlowDots => glow?.DotCount ?? 0;
    /// <summary>Point lights a glowing giant has shining right now.</summary>
    public int GlowLights => glow?.LightsShining ?? 0;
    /// <summary>The glow's checks against the hide texture, for tests.</summary>
    public bool GlowAlphaChecked => glow?.AlphaChecked ?? false;
    public int GlowTransparentRejects => glow?.TransparentRejects ?? 0;
    public double GlowOpaqueAreaShare => glow?.OpaqueAreaShare ?? 0;

    public EntityBehaviorGiantSerpentBody(Entity entity) : base(entity) { }

    public double RenderOrder => 0.35;
    public int RenderRange => 9999;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        boneNames = GiantSerpentRig.SpineBones(properties);
        waveAmplitude = GiantSerpentRig.WaveAmplitude(properties);
        waveLength = GiantSerpentRig.WaveLength(properties);
        bodyLength = GiantSerpentRig.BodyLength(properties);

        capi = entity.Api as ICoreClientAPI;
        if (capi != null)
        {
            lighting = new BodyLighting(this);
            capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "underwaterhorrors-giantserpentbody");
            capi.Event.RegisterRenderer(lighting, EnumRenderStage.Before, "underwaterhorrors-giantserpentlight");
            silhouette = new GiantSerpentSilhouette(capi, entity);
        }
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        capi?.Event.UnregisterRenderer(this, EnumRenderStage.Before);
        if (lighting != null) capi?.Event.UnregisterRenderer(lighting, EnumRenderStage.Before);
        silhouette?.Dispose();
        silhouette = null;
        glow?.Dispose();
        glow = null;
        base.OnEntityDespawn(despawn);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Lighting
    // ═══════════════════════════════════════════════════════════════════
    // The game lights a whole entity with the light at its position. Here
    // that is the mouth, often 30 blocks down in the dark, which left the
    // part of the body breaking the surface beside a boat nearly black.
    // After the renderer has taken that value (entity pass, order 0.4),
    // swap in the light at the spine joint nearest the camera, so the body
    // is lit where the player is looking at it.
    private static readonly System.Reflection.FieldInfo LightField =
        typeof(Vintagestory.GameContent.EntityShapeRenderer).GetField("lightrgbs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private BodyLighting lighting;
    private readonly BlockPos lightPos = new(0);

    private sealed class BodyLighting : IRenderer
    {
        private readonly EntityBehaviorGiantSerpentBody body;
        public BodyLighting(EntityBehaviorGiantSerpentBody body) { this.body = body; }
        public double RenderOrder => 0.41;
        public int RenderRange => 9999;
        public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => body.RelightFromNearestJoint();
        public void Dispose() { }
    }

    private void RelightFromNearestJoint()
    {
        if (!entity.IsRendered || JointCount == 0 || LightField == null) return;
        if (entity.Properties.Client.Renderer is not Vintagestory.GameContent.EntityShapeRenderer esr) return;
        Vec3d cam = capi.World.Player?.Entity?.CameraPos;
        if (cam == null) return;

        var pos = entity.Pos;
        int best = 0;
        double bestSq = double.MaxValue;
        for (int i = 0; i < JointCount; i++)
        {
            double dx = pos.X + RelX[i] - cam.X, dy = pos.InternalY + RelY[i] - cam.Y, dz = pos.Z + RelZ[i] - cam.Z;
            double sq = dx * dx + dy * dy + dz * dz;
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        lightPos.Set((int)Math.Floor(pos.X + RelX[best]), (int)Math.Floor(pos.InternalY + RelY[best]), (int)Math.Floor(pos.Z + RelZ[best]));
        lightPos.dimension = entity.Pos.Dimension;
        Vec4f near = capi.World.BlockAccessor.GetLightRGBs(lightPos);
        if (LightField.GetValue(esr) is not Vec4f current || near.W > current.W) LightField.SetValue(esr, near);
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        // A glowing giant's flag comes with its spawn; make its glow on
        // first sight. The corpse's glow keeps fading after death.
        if (glow == null && silhouette != null && entity.WatchedAttributes.GetBool(GiantSerpentGlow.BiolumAttr))
        {
            glow = new GiantSerpentGlow(capi, entity, this);
            silhouette.Glow = glow;
        }
        glow?.Update(deltaTime);

        if (!entity.Alive) return;
        if (!ResolveRig()) return;

        double size = entity.Properties.Client.Size;
        var pos = entity.Pos;

        if (trail == null) trail = new SerpentTrail((bodyLength + 4) * size);
        if (!trail.Initialized)
        {
            // Start from the server's copy of the path if there is one, so a
            // serpent first seen mid-swim already has its curves.
            float[] seed = (entity.WatchedAttributes[SeedAttr] as FloatArrayAttribute)?.value;
            if (!trail.FromSeed(seed, SeedSpacing, pos.X, pos.InternalY, pos.Z))
                trail.Reset(pos.X, pos.InternalY, pos.Z, pos.Yaw, 0);
        }
        else
        {
            trail.Push(pos.X, pos.InternalY, pos.Z, pos.Yaw);
        }

        PoseSpine(size, pos.Yaw);
    }

    private bool ResolveRig()
    {
        IAnimator animator = entity.AnimManager?.Animator;
        if (animator == null) return false;
        if (animator == rigAnimator && poses != null) return true;

        int n = boneNames.Length;
        if (n < 2) return false;
        var found = new ElementPose[n];
        for (int i = 0; i < n; i++)
        {
            found[i] = animator.GetPosebyName(boneNames[i]);
            if (found[i]?.ForElement == null)
            {
                if (!warnedMissingBone)
                {
                    warnedMissingBone = true;
                    entity.Api.Logger.Warning("[underwaterhorrors] giant serpent: spine bone '{0}' not found in the shape", boneNames[i]);
                }
                return false;
            }
        }

        // Rest pivots in model space. Every spine bone and its ancestors
        // have no rest rotation, so frames just add up their From offsets.
        var pivots = new Vec3d[n + 1];
        for (int i = 0; i < n; i++)
        {
            ShapeElement el = found[i].ForElement;
            Vec3d parentFrame = FrameOrigin(el.ParentElement);
            double[] o = el.RotationOrigin ?? new double[3];
            pivots[i] = new Vec3d(parentFrame.X + o[0], parentFrame.Y + o[1], parentFrame.Z + o[2]);
        }
        // The last bone has no child joint; give its link the length and
        // direction of the one before it.
        pivots[n] = pivots[n - 1] + (pivots[n - 1] - pivots[n - 2]);

        restArc = new double[n + 1];
        restElev = new double[n];
        for (int i = 0; i < n; i++)
        {
            Vec3d link = pivots[i + 1] - pivots[i];
            restArc[i + 1] = restArc[i] + link.Length() / 16.0;
            restElev[i] = Math.Atan2(link.Y, Math.Sqrt(link.X * link.X + link.Z * link.Z));
        }

        arcs = new double[n + 1];
        qx = new double[n + 1]; qy = new double[n + 1]; qz = new double[n + 1];
        qodo = new double[n + 1]; qtx = new double[n + 1]; qtz = new double[n + 1];
        poses = found;
        rigAnimator = animator;
        return true;
    }

    private static Vec3d FrameOrigin(ShapeElement el)
    {
        var at = new Vec3d();
        for (; el != null; el = el.ParentElement)
        {
            at.X += el.From[0];
            at.Y += el.From[1];
            at.Z += el.From[2];
        }
        return at;
    }

    private void PoseSpine(double size, float yaw)
    {
        int n = poses.Length;
        for (int i = 0; i <= n; i++) arcs[i] = restArc[i] * size;
        trail.SampleSmooth(arcs, n + 1, TangentSpan * size, qx, qy, qz, qodo, qtx, qtz);

        double amp = waveAmplitude * size, wl = waveLength * size, len = bodyLength * size;
        for (int i = 1; i <= n; i++)
        {
            double off = SerpentTrail.WaveOffset(arcs[i], qodo[i], amp, wl, len);
            // Sideways = the horizontal travel direction turned 90 degrees.
            qx[i] += qtz[i] * off;
            qz[i] -= qtx[i] * off;
        }

        if (RelX == null || RelX.Length != n + 1)
        {
            RelX = new double[n + 1]; RelY = new double[n + 1]; RelZ = new double[n + 1];
        }
        for (int i = 0; i <= n; i++)
        {
            RelX[i] = qx[i] - qx[0];
            RelY[i] = qy[i] - qy[0];
            RelZ[i] = qz[i] - qz[0];
        }
        JointCount = n + 1;

        // World to model: the renderer turns the model by yaw + 90 degrees
        // about Y (pitch and roll stay 0 for this serpent).
        double ang = -(yaw + GameMath.PIHALF);
        double cy = Math.Cos(ang), sy = Math.Sin(ang);

        // Accumulated rotation of the parent bone, row-major 3x3.
        double r00 = 1, r01 = 0, r02 = 0, r10 = 0, r11 = 1, r12 = 0, r20 = 0, r21 = 0, r22 = 1;

        for (int i = 0; i < n; i++)
        {
            double dx = qx[i + 1] - qx[i], dy = qy[i + 1] - qy[i], dz = qz[i + 1] - qz[i];
            double mx = dx * cy + dz * sy;
            double my = dy;
            double mz = -dx * sy + dz * cy;

            // Into the parent's frame: R^T * m.
            double tx = r00 * mx + r10 * my + r20 * mz;
            double ty = r01 * mx + r11 * my + r21 * mz;
            double tz = r02 * mx + r12 * my + r22 * mz;
            double tl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            if (tl < 1e-9) { tx = 1; ty = 0; tz = 0; tl = 1; }
            tx /= tl; ty /= tl; tz /= tl;

            // The game's element rotation is Ry(degY) * Rz(degZ), which
            // sends +X to (cos y cos z, sin z, -sin y cos z).
            double a = Math.Clamp(Math.Atan2(-tz, tx), -MaxBendRad, MaxBendRad);
            double b = Math.Clamp(Math.Asin(Math.Clamp(ty, -1, 1)) - restElev[i], -MaxBendRad, MaxBendRad);

            ElementPose pose = poses[i];
            pose.degOffY = (float)(a * GameMath.RAD2DEG);
            pose.degOffZ = (float)(b * GameMath.RAD2DEG);

            // R = R * Ry(a) * Rz(b)
            double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b);
            double m00 = ca * cb, m01 = -ca * sb, m02 = sa;
            double m10 = sb, m11 = cb, m12 = 0;
            double m20 = -sa * cb, m21 = sa * sb, m22 = ca;
            double n00 = r00 * m00 + r01 * m10 + r02 * m20;
            double n01 = r00 * m01 + r01 * m11 + r02 * m21;
            double n02 = r00 * m02 + r01 * m12 + r02 * m22;
            double n10 = r10 * m00 + r11 * m10 + r12 * m20;
            double n11 = r10 * m01 + r11 * m11 + r12 * m21;
            double n12 = r10 * m02 + r11 * m12 + r12 * m22;
            double n20 = r20 * m00 + r21 * m10 + r22 * m20;
            double n21 = r20 * m01 + r21 * m11 + r22 * m21;
            double n22 = r20 * m02 + r21 * m12 + r22 * m22;
            r00 = n00; r01 = n01; r02 = n02;
            r10 = n10; r11 = n11; r12 = n12;
            r20 = n20; r21 = n21; r22 = n22;
        }
    }

    public void Dispose() { }

    public override string PropertyName() => "underwaterhorrors:giantserpentbody";
}
