using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Multi-mode bioluminescent glow tester for kraken tentacle segments.
///
/// Modes 1-10 were the first round of variants (sanity test, additive
/// at different render stages, HDR pumping, scale variants). The user
/// landed on mode 7 (AfterFinalComposition + additive cyan) as the
/// best baseline but flagged two issues:
///
///   (a) the glow billboards are anchored at entity.Pos which is the
///       BOTTOM of the segment_mid model. The visible "orange dashes"
///       texture extends 0.84b along the segment's local trunk axis,
///       so the glow appears as small spheres OFFSET from the texture.
///       Fix: anchor at entity.Pos + 0.42 * trunkAxis where trunkAxis
///       is computed from the segment's pitch/roll. For untilted
///       segments (test spawns) trunk = +Y. For tilted real-kraken
///       segments trunk follows the spline.
///
///   (b) glow should be "barely more diffuse with depth" - approximating
///       the soft scattering you get looking at a light through deep
///       water. We don't have access to scene depth at this render
///       stage, but we can use camera-to-billboard distance (computed
///       in the vertex shader as length(worldPos), which IS view-space
///       distance because worldPos is already camera-relative). Pass
///       this into the fragment shader as vViewZ and use it to widen
///       the falloff curve subtly.
///
/// This file adds 10 NEW modes (11-20) that all run at AfterFinalComposition,
/// all use the trunk-axis offset, and vary the falloff shape, billboard
/// size, dual-layer (core+halo) rendering, and depth-diffusion factor
/// so the user can A/B them.
/// </summary>
public class BioluminescentGlowRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private IShaderProgram shaderProg;
    private MeshRef quadMeshRef;
    private bool initialized;
    private bool compileFailed;
    private float timeAccum;
    private int diagFrameCounter;
    private int lastReportedMode = -1;

    private readonly BlockPos diagOriginPos = new BlockPos(0, 0, 0, 0);

    /// <summary>Active test mode. 0 = off. Updated via network from
    /// the /uh biolumtest mode chat command.</summary>
    public int Mode = 0;

    public double RenderOrder => 0.55;
    public int RenderRange => 256;

    // Segment-mid model extends 0.84b along the trunk axis from
    // entity.Pos. Center of the visible texture sits at half that.
    private const double TextureCenterOffset = 0.42;

    public BioluminescentGlowRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public bool Initialize()
    {
        quadMeshRef?.Dispose();
        shaderProg?.Dispose();
        initialized = false;
        compileFailed = false;

        try
        {
            shaderProg = capi.Shader.NewShaderProgram();
            shaderProg.AssetDomain = "underwaterhorrors";
            shaderProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
            shaderProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
            capi.Shader.RegisterFileShaderProgram("biolumglow", shaderProg);
            if (!shaderProg.Compile())
            {
                capi.Logger.Error("[underwaterhorrors] biolumglow shader failed to compile.");
                compileFailed = true;
                initialized = true;
                return false;
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Error($"[underwaterhorrors] biolumglow shader exception: {ex.Message}");
            compileFailed = true;
            initialized = true;
            return false;
        }

        var meshData = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: false, withFlags: false);
        meshData.AddVertex(-0.5f, -0.5f, 0f, 0f, 0f);
        meshData.AddVertex( 0.5f, -0.5f, 0f, 1f, 0f);
        meshData.AddVertex( 0.5f,  0.5f, 0f, 1f, 1f);
        meshData.AddVertex(-0.5f,  0.5f, 0f, 0f, 1f);
        meshData.AddIndices(new int[] { 0, 1, 2, 0, 2, 3 });
        quadMeshRef = capi.Render.UploadMesh(meshData);

        initialized = true;
        capi.Logger.Notification("[underwaterhorrors] biolumglow shader compiled OK.");
        return true;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (Mode == 0) return;
        if (stage != GetStageForMode(Mode)) return;

        timeAccum += deltaTime;
        diagFrameCounter++;
        bool logThisFrame = (diagFrameCounter % 120) == 0;

        if (Mode != lastReportedMode)
        {
            lastReportedMode = Mode;
            string shaderState = compileFailed ? "(SHADER FAILED - only mode 1 will draw)"
                                : !initialized ? "(NOT INITIALIZED YET - try /reload shaders)"
                                : "(shader OK)";
            capi.ShowChatMessage($"[uh] biolum mode={Mode} stage={stage} {shaderState}");
        }

        var entities = capi.World.LoadedEntities;
        int matched = 0;
        if (entities != null)
        {
            foreach (Entity e in entities.Values)
            {
                if (IsTentacleChain(e)) matched++;
            }
        }

        if (logThisFrame)
        {
            capi.Logger.Notification(
                $"[underwaterhorrors] biolumglow mode={Mode} stage={stage} matched={matched} compileFailed={compileFailed}");
        }

        if (Mode == 1)
        {
            DrawLineDiagnostic(matched);
            return;
        }

        if (compileFailed || shaderProg == null || quadMeshRef == null) return;

        DrawShaderForMode(Mode, matched);
    }

    private void DrawLineDiagnostic(int matched)
    {
        const int playerColor = unchecked((int)0xFFFF00FF);
        const int entityColor = unchecked((int)0xFFFFFFFF);

        var ppos = capi.World.Player.Entity.Pos;
        float yaw = (float)ppos.Yaw;
        double mx = ppos.X + Math.Sin(yaw) * 2.0;
        double my = ppos.Y + 1.5;
        double mz = ppos.Z + Math.Cos(yaw) * 2.0;
        DrawCross(mx, my, mz, 1.0f, playerColor);

        if (matched > 0)
        {
            foreach (Entity entity in capi.World.LoadedEntities.Values)
            {
                if (!IsTentacleChain(entity)) continue;
                DrawCross(entity.Pos.X, entity.Pos.Y, entity.Pos.Z, 0.6f, entityColor);
            }
        }
    }

    private void DrawCross(double wx, double wy, double wz, float halfSize, int color)
    {
        int bx = (int)wx;
        int by = (int)wy;
        int bz = (int)wz;
        diagOriginPos.Set(bx, by, bz);
        diagOriginPos.dimension = capi.World.Player.Entity.Pos.Dimension;

        float ox = (float)(wx - bx);
        float oy = (float)(wy - by);
        float oz = (float)(wz - bz);

        capi.Render.RenderLine(diagOriginPos, ox - halfSize, oy, oz, ox + halfSize, oy, oz, color);
        capi.Render.RenderLine(diagOriginPos, ox, oy - halfSize, oz, ox, oy + halfSize, oz, color);
        capi.Render.RenderLine(diagOriginPos, ox, oy, oz - halfSize, ox, oy, oz + halfSize, color);
    }

    private static EnumRenderStage GetStageForMode(int mode)
    {
        // Modes 11-20 all run at AfterFinalComposition - the user picked
        // mode 7 as the visual baseline and 11+ are refinements of it.
        if (mode >= 11) return EnumRenderStage.AfterFinalComposition;
        return mode switch
        {
            6  => EnumRenderStage.AfterPostProcessing,
            7  => EnumRenderStage.AfterFinalComposition,
            8  => EnumRenderStage.OIT,
            _  => EnumRenderStage.AfterOIT,
        };
    }

    /// <summary>
    /// Per-mode rendering parameters. Centralized in one place so the
    /// shader-side switch and the C#-side per-entity loop stay in sync.
    /// </summary>
    private struct ModeParams
    {
        public int ShaderBranch;        // 0 quad, 2 pulse, 3 gaussian, 4 halo-only, 1 solid
        public float ScaleCore;         // billboard scale for first pass
        public float ScaleHalo;         // 0 = no second pass, otherwise a soft halo on top
        public float ColorR, ColorG, ColorB;
        public float CoreAlpha;         // alpha for the inner/main pass
        public float HaloAlpha;         // alpha for the halo pass (only if ScaleHalo > 0)
        public float DiffuseScale;      // 0..1 - how much viewZ widens the falloff
        public bool UseDepthTest;
        public bool UseBlend;           // false = no blend, opaque draw
        public bool UseTrunkOffset;     // true = anchor at entity.Pos + 0.42*trunk
    }

    private static ModeParams GetParams(int mode)
    {
        // Default starting point - matches mode 7 (the user's preferred
        // baseline) but with UseTrunkOffset=true so every mode 2..20
        // inherits the proper VS-equivalent texture-center alignment.
        // The old default of false produced billboards floating ~0.75
        // blocks horizontally away from where VS draws the visible
        // mesh; modes 7 and 10 (which the user specifically flagged)
        // inherit this default.
        var p = new ModeParams
        {
            ShaderBranch  = 0,
            ScaleCore     = 1.5f,
            ScaleHalo     = 0f,
            ColorR        = 0.35f,
            ColorG        = 0.95f,
            ColorB        = 1.00f,
            CoreAlpha     = 0.95f,
            HaloAlpha     = 0f,
            DiffuseScale  = 0f,
            UseDepthTest  = true,
            UseBlend      = true,
            UseTrunkOffset = true,
        };

        switch (mode)
        {
            // ----- Legacy modes 2-10 (kept for A/B reference) -----
            case 2:  p.CoreAlpha = 0.55f; break;
            case 3:  p.CoreAlpha = 0.55f; p.UseDepthTest = false; break;
            case 4:  p.ColorR = 2.8f; p.ColorG = 7.6f; p.ColorB = 8.0f; p.CoreAlpha = 1f; break;
            case 5:  p.ScaleCore = 4.0f; p.CoreAlpha = 0.55f; break;
            case 6:  p.CoreAlpha = 0.85f; break;
            case 7:  p.CoreAlpha = 0.95f; break;
            case 8:  p.CoreAlpha = 0.7f;  break;
            case 9:  p.CoreAlpha = 1.0f; p.ShaderBranch = 1; p.UseBlend = false; break;
            case 10: p.ShaderBranch = 2; p.CoreAlpha = 0.85f; break;

            // ----- New modes 11-20: trunk-aligned + slight diffusion -----
            // 11: mode 7 + simple +0.42 along trunk (no other change)
            case 11:
                p.UseTrunkOffset = true;
                break;

            // 12: trunk-aligned + gaussian falloff (smoother edges)
            case 12:
                p.UseTrunkOffset = true;
                p.ShaderBranch = 3;
                p.CoreAlpha = 1.0f;
                break;

            // 13: trunk-aligned + gaussian + tiny diffusion (~0.1 per 10b)
            case 13:
                p.UseTrunkOffset = true;
                p.ShaderBranch = 3;
                p.CoreAlpha = 1.0f;
                p.DiffuseScale = 0.5f;
                break;

            // 14: trunk-aligned + gaussian + slight diffusion + slightly bigger
            case 14:
                p.UseTrunkOffset = true;
                p.ShaderBranch = 3;
                p.ScaleCore = 1.8f;
                p.CoreAlpha = 1.0f;
                p.DiffuseScale = 0.5f;
                break;

            // 15: trunk-aligned + dual-layer (bright core + soft halo)
            case 15:
                p.UseTrunkOffset = true;
                p.ShaderBranch = 0;
                p.ScaleCore = 1.0f;
                p.ScaleHalo = 2.5f;
                p.CoreAlpha = 0.95f;
                p.HaloAlpha = 0.35f;
                break;

            // 16: trunk-aligned + dual-layer + slight diffusion
            case 16:
                p.UseTrunkOffset = true;
                p.ShaderBranch = 0;
                p.ScaleCore = 0.9f;
                p.ScaleHalo = 2.2f;
                p.CoreAlpha = 0.95f;
                p.HaloAlpha = 0.35f;
                p.DiffuseScale = 0.5f;
                break;

            // 17: trunk-aligned + soft halo only (no bright core)
            case 17:
                p.UseTrunkOffset = true;
                p.ShaderBranch = 4;
                p.ScaleCore = 2.5f;
                p.CoreAlpha = 0.95f;
                p.DiffuseScale = 0.5f;
                break;

            // 18: trunk-aligned + gaussian wider + more diffusion
            case 18:
                p.UseTrunkOffset = true;
                p.ShaderBranch = 3;
                p.ScaleCore = 2.0f;
                p.CoreAlpha = 1.0f;
                p.DiffuseScale = 1.0f;
                break;

            // 19: trunk-aligned + gaussian + dual halo (large outer)
            case 19:
                p.UseTrunkOffset = true;
                p.ShaderBranch = 3;
                p.ScaleCore = 1.4f;
                p.ScaleHalo = 3.0f;
                p.CoreAlpha = 1.0f;
                p.HaloAlpha = 0.25f;
                p.DiffuseScale = 0.5f;
                break;

            // 20: RECOMMENDED MIX - mode 7 baseline + alignment + tiny diffusion
            case 20:
                p.UseTrunkOffset = true;
                p.ShaderBranch = 0;
                p.ScaleCore = 1.6f;
                p.CoreAlpha = 0.95f;
                p.DiffuseScale = 0.4f;
                break;
        }

        return p;
    }

    private void DrawShaderForMode(int mode, int matchedEntities)
    {
        var p = GetParams(mode);
        var render = capi.Render;
        var cameraPos = capi.World.Player.Entity.CameraPos;

        // ----- GL state -----
        if (p.UseDepthTest) render.GLEnableDepthTest();
        else                render.GLDisableDepthTest();
        render.GLDepthMask(false);

        if (p.UseBlend) render.GlToggleBlend(true, EnumBlendMode.Glow);
        else            render.GlToggleBlend(false, EnumBlendMode.Standard);

        render.GlDisableCullFace();

        // ----- Shader uniforms (constant across this draw) -----
        shaderProg.Use();
        shaderProg.UniformMatrix("projectionMatrix", render.CurrentProjectionMatrix);
        shaderProg.UniformMatrix("modelViewMatrix",  render.CameraMatrixOriginf);
        shaderProg.Uniform("modeBranch",   p.ShaderBranch);
        shaderProg.Uniform("uTime",        timeAccum);
        shaderProg.Uniform("diffuseScale", p.DiffuseScale);

        // PASS 1: core/main billboard
        shaderProg.Uniform("scale",     p.ScaleCore);
        shaderProg.Uniform("glowColor", p.ColorR, p.ColorG, p.ColorB, p.CoreAlpha);
        DrawAllEntities(p);

        // PASS 2: optional soft halo on top of the core
        if (p.ScaleHalo > 0f && p.HaloAlpha > 0f)
        {
            shaderProg.Uniform("scale",      p.ScaleHalo);
            shaderProg.Uniform("glowColor",  p.ColorR, p.ColorG, p.ColorB, p.HaloAlpha);
            // Use halo-only branch for the second pass so the outer glow
            // doesn't have a hot center stacked on top of the core.
            shaderProg.Uniform("modeBranch", 4);
            DrawAllEntities(p);
        }

        shaderProg.Stop();

        // Restore standard state.
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        render.GLDepthMask(true);
        render.GLEnableDepthTest();
        render.GlEnableCullFace();
    }

    private void DrawAllEntities(ModeParams p)
    {
        var render = capi.Render;
        var cameraPos = capi.World.Player.Entity.CameraPos;
        double rangeSq = (double)RenderRange * RenderRange;

        foreach (Entity entity in capi.World.LoadedEntities.Values)
        {
            if (!IsTentacleChain(entity)) continue;

            double cx, cy, cz;
            if (p.UseTrunkOffset)
            {
                // Use the same VS-equivalent transform the mesh renderer
                // uses, applied to the segment-mid trunk midpoint in
                // mesh-local block space (y = 0.281 is half of the
                // tessellated mesh's Y bound 0.562). This places the
                // billboard exactly where the orange dashes appear,
                // accounting for VS's (-0.5, 0, -0.5) entity shift, the
                // 1.5x size scale, the hitbox-Y rotation pivot, and the
                // X-Y-Z rotation order. Matches BiolumTextureRenderer.
                EntityModelTransform.ApplyModelMatrix(
                    entity, scale: 1.5f,
                    localX: 0.0, localY: 0.281, localZ: 0.0,
                    out cx, out cy, out cz);
            }
            else
            {
                // Legacy path: anchor at raw entity.Pos for backward
                // compatibility with modes 2-10 (no trunk-offset modes).
                cx = entity.Pos.X;
                cy = entity.Pos.InternalY;
                cz = entity.Pos.Z;
            }

            double dx = cx - cameraPos.X;
            double dy = cy - cameraPos.Y;
            double dz = cz - cameraPos.Z;
            if (dx * dx + dy * dy + dz * dz > rangeSq) continue;

            shaderProg.Uniform("worldPos", (float)dx, (float)dy, (float)dz);
            render.RenderMesh(quadMeshRef);
        }
    }

    /// <summary>
    /// Match the visible chain pieces - heads (krakententacle, krakenamb*)
    /// use the invisible shape so they have no visual to glow around.
    /// In test modes, INCLUDES static-flagged entities so /uh biolumtest spawn
    /// segments are visible (the original code skipped them).
    /// </summary>
    private static bool IsTentacleChain(Entity entity)
    {
        if (entity?.Code?.Domain != "underwaterhorrors") return false;
        string path = entity.Code.Path;
        return path == "krakententsegment"
            || path == "krakententsegment_mid"
            || path == "krakententsegment_mid_claw"
            || path == "krakententsegment_outer";
    }

    public void Dispose()
    {
        quadMeshRef?.Dispose();
        shaderProg?.Dispose();
        quadMeshRef = null;
        shaderProg = null;
        initialized = false;
    }
}
