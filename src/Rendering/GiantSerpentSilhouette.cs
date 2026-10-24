using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UnderwaterHorrors;

/// <summary>
/// The giant serpent's deep silhouette: its own model, posed exactly as it
/// is drawn this frame, laid over the finished scene as a flat dark shape.
///
/// Water hides a deep entity completely. From above, the water surface is
/// drawn over everything a dozen blocks down; underwater, the fog swallows
/// the model a couple of dozen blocks out. So the serpent, which spends its
/// time 15 to 30 blocks down, could not be seen at all. This draws the
/// entity's mesh a second time after the scene is finished, so neither the
/// water surface nor the fog covers it, with the same bone matrices the
/// game just used for the real model: fins, spikes and the swimming curve
/// all match. The shader fades it out where the real model is visible: near
/// the surface when looking down from above, and near the camera underwater.
///
/// Each pixel gets one even layer of darkness, from two passes over the
/// mesh. The first writes only depth: the nearest surface of the shape at
/// every pixel, pulled a few blocks toward the camera so the real model's
/// own surfaces never hide it (terrain further in front still does). The
/// second draws the color a hair nearer still, with depth writes on, so only
/// that nearest surface passes, once: the far side, inner faces and the
/// second face of a flat fin all fail. A single pass tested against the real
/// model's depth instead broke up into shards at a distance.
///
/// The matrices are copied during the Opaque stage, where the real model
/// was drawn.
/// </summary>
public sealed class GiantSerpentSilhouette : IRenderer
{
    /// <summary>
    /// Past this distance from the camera the shape has faded out entirely.
    /// The AI only removes a leaving serpent once its whole body is beyond
    /// this from every player, so it never vanishes in plain view.
    /// </summary>
    public const float FadeEndBlocks = 230f;
    private const float FadeStartBlocks = 170f;

    // How dark the shape gets where it is fully shown: a murky outline, a
    // little darker than the water around it, like a far wreck.
    private const float MaxAlpha = 0.3f;
    // How far the shape is pulled toward the camera for the depth test, as
    // a multiple of the entity size, plus a little per block of distance.
    private const float DepthPull = 0.6f;
    private const float DepthPullPerBlock = 0.002f;
    // How much nearer the color pass is than the depth pass, in depth buffer
    // units: about sixteen steps of a 24-bit buffer. Far away a fraction of
    // a block is only a few steps, and a margin in blocks was lost to
    // rounding there; this one is under a block of depth out to 230 blocks,
    // far less than the body is thick.
    private const float ColorPassNearer = 1e-6f;

    private static readonly FieldInfo MeshField =
        typeof(EntityShapeRenderer).GetField("meshRefOpaque", BindingFlags.Instance | BindingFlags.NonPublic);

    private static IShaderProgram shader;
    private static UBORef animUbo;
    private static ICoreClientAPI shaderApi;
    private static bool shaderFailed;

    private readonly ICoreClientAPI capi;
    private readonly Entity entity;

    private double surfaceY;
    private float surfaceTimer;
    private readonly BlockPos probe = new(0);

    // The matrices of this frame's Opaque stage, where the model was drawn.
    private readonly float[] opaqueProjection = new float[16];
    private readonly float[] opaqueView = new float[16];
    private readonly float[] opaqueModel = new float[16];
    private bool haveOpaqueMatrices;

    /// <summary>The glowing variant's dots, drawn right after the shape.</summary>
    public GiantSerpentGlow Glow { get; set; }

    public double RenderOrder => 0.5;
    public int RenderRange => 9999;

    public GiantSerpentSilhouette(ICoreClientAPI capi, Entity entity)
    {
        this.capi = capi;
        this.entity = entity;
        surfaceY = capi.World.SeaLevel;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "underwaterhorrors-giantsilhouette");
        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterFinalComposition, "underwaterhorrors-giantsilhouette");
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);
    }

    private bool EnsureShader()
    {
        if (shaderApi == capi && shader != null) return true;
        if (shaderApi == capi && shaderFailed) return false;
        shaderApi = capi;
        shaderFailed = false;
        try
        {
            shader = capi.Shader.NewShaderProgram();
            shader.AssetDomain = "underwaterhorrors";
            shader.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
            shader.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
            capi.Shader.RegisterFileShaderProgram("giantsilhouette", shader);
            if (!shader.Compile()) throw new Exception("compile failed");
            // The bone matrices, laid out as the game's entity shader has them.
            animUbo = capi.Render.CreateUBO(shader, 0, "Animation", GlobalConstants.MaxAnimatedElements * 16 * 4);
            shader.UBOs["Animation"] = animUbo;
            return true;
        }
        catch (Exception e)
        {
            capi.Logger.Error("[underwaterhorrors] giant serpent silhouette shader: {0}", e.Message);
            shader = null;
            shaderFailed = true;
            return false;
        }
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        var render = capi.Render;
        if (stage == EnumRenderStage.Opaque)
        {
            if (entity.Properties.Client.Renderer is not EntityShapeRenderer er || er.ModelMat == null) return;
            Array.Copy(render.CurrentProjectionMatrix, opaqueProjection, 16);
            Array.Copy(render.CameraMatrixOriginf, opaqueView, 16);
            Array.Copy(er.ModelMat, opaqueModel, 16);
            haveOpaqueMatrices = true;
            return;
        }
        if (!haveOpaqueMatrices) return;
        haveOpaqueMatrices = false;

        // Only when the game drew the model this frame: its bone matrices
        // are then this frame's.
        if (!entity.IsRendered) return;
        if (entity.Properties.Client.Renderer is not EntityShapeRenderer esr || esr.ModelMat == null) return;
        if (MeshField?.GetValue(esr) is not MultiTextureMeshRef mesh || mesh.Disposed || !mesh.Initialized) return;
        IAnimator animator = entity.AnimManager?.Animator;
        if (animator?.Matrices == null) return;
        EntityPlayer player = capi.World.Player?.Entity;
        if (player == null || !EnsureShader()) return;

        var pos = entity.Pos;
        Vec3d cam = player.CameraPos;
        UpdateSurface(deltaTime, pos.X, pos.InternalY, pos.Z);

        probe.Set((int)Math.Floor(cam.X), (int)Math.Floor(cam.Y), (int)Math.Floor(cam.Z));
        probe.dimension = pos.Dimension;
        bool camUnderwater = WaterHelper.IsWaterBlock(capi.World.BlockAccessor.GetBlock(probe, BlockLayersAccess.Fluid));

        float size = entity.Properties.Client.Size;
        render.GLEnableDepthTest();
        render.GLDepthMask(true);
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        // Entities are drawn without face culling; so is this, or flat fins
        // seen from their back would be missing.
        render.GlDisableCullFace();

        shader.Use();
        shader.UniformMatrix("projectionMatrix", opaqueProjection);
        shader.UniformMatrix("viewMatrix", opaqueView);
        shader.UniformMatrix("modelMatrix", opaqueModel);
        shader.Uniform("cameraY", (float)cam.Y);
        shader.Uniform("surfaceY", (float)surfaceY);
        shader.Uniform("camUnderwater", camUnderwater ? 1 : 0);
        shader.Uniform("tint", 0.015f, 0.03f, 0.045f);
        shader.Uniform("maxAlpha", MaxAlpha);
        shader.Uniform("depthPullPerBlock", DepthPullPerBlock);
        shader.Uniform("fadeStart", FadeStartBlocks);
        shader.Uniform("fadeEnd", FadeEndBlocks);
        animUbo.Update(animator.Matrices, 0, Math.Min(animator.MaxJointId, GlobalConstants.MaxAnimatedElements) * 16 * 4);

        shader.Uniform("depthPull", DepthPull * size);
        // Pass 1: the nearest surface's depth, no color. It covers the whole
        // shape, faded or not, so the glow can test against it everywhere.
        render.GlColorMask(false, false, false, false);
        shader.Uniform("depthOnly", 1);
        shader.Uniform("nearerBy", 0f);
        render.RenderMultiTextureMesh(mesh, "entityTex", 0);
        // Pass 2: color, only where a fragment is that nearest surface.
        render.GlColorMask(true, true, true, true);
        shader.Uniform("depthOnly", 0);
        shader.Uniform("nearerBy", ColorPassNearer);
        render.RenderMultiTextureMesh(mesh, "entityTex", 0);
        shader.Stop();

        Glow?.Draw(esr, opaqueProjection, opaqueView, opaqueModel, animator.Matrices, DepthPull * size, DepthPullPerBlock);

        render.GlEnableCullFace();
    }

    // The water surface over the serpent: the top of the water column at
    // its mouth, rescanned twice a second.
    private void UpdateSurface(float dt, double x, double y, double z)
    {
        surfaceTimer -= dt;
        if (surfaceTimer > 0) return;
        surfaceTimer = 0.5f;

        var acc = capi.World.BlockAccessor;
        probe.Set((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
        probe.dimension = entity.Pos.Dimension;
        if (WaterHelper.IsWaterBlock(acc.GetBlock(probe, BlockLayersAccess.Fluid)))
        {
            for (int i = 0; i < 160; i++)
            {
                probe.Y++;
                if (!WaterHelper.IsWaterBlock(acc.GetBlock(probe, BlockLayersAccess.Fluid))) break;
            }
            surfaceY = probe.Y;
            return;
        }
        for (int i = 0; i < 40; i++)
        {
            probe.Y--;
            if (WaterHelper.IsWaterBlock(acc.GetBlock(probe, BlockLayersAccess.Fluid)))
            {
                surfaceY = probe.Y + 1;
                return;
            }
        }
    }
}
