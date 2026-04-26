using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// Texture-based emissive renderer. Re-renders the krakententsegment_mid
/// 3D mesh at every loaded segment entity at AfterFinalComposition stage,
/// with a custom shader that samples the bellhead texture and emits
/// additive cyan from the orange-painted parts only.
///
/// Different from BioluminescentGlowRenderer, which draws screen-aligned
/// billboard quads at each entity. That approach put soft glowing orbs
/// near each segment rather than ON the orange dashes themselves.
/// This renderer fixes that by re-rendering the actual segment mesh:
/// the glow follows the painted texture's orange pattern wherever it is
/// on the model, and waves/twists with the segment as the tentacle moves.
///
/// Modes:
///   0 = OFF
///   1 = no depth attenuation (uniform brightness top to bottom)
///   2 = weak depth attenuation (top slightly brighter than bottom)
///   3 = medium depth attenuation (clear top/bottom contrast)
///   4 = strong depth attenuation (deep parts much dimmer)
/// </summary>
public class BiolumTextureRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private IShaderProgram shaderProg;
    private MeshRef segmentMidMesh;
    private int atlasTextureId;
    private bool initialized;
    private bool compileFailed;
    private int lastReportedMode = -1;

    public int Mode = 0;
    public double RenderOrder => 0.55;
    public int RenderRange => 256;

    public BiolumTextureRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public bool Initialize()
    {
        segmentMidMesh?.Dispose();
        shaderProg?.Dispose();
        initialized = false;
        compileFailed = false;

        // ---- Compile shader ----
        try
        {
            shaderProg = capi.Shader.NewShaderProgram();
            shaderProg.AssetDomain = "underwaterhorrors";
            shaderProg.VertexShader   = capi.Shader.NewShader(EnumShaderType.VertexShader);
            shaderProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
            capi.Shader.RegisterFileShaderProgram("biolumtex", shaderProg);
            if (!shaderProg.Compile())
            {
                capi.Logger.Error("[underwaterhorrors] biolumtex shader compile failed");
                compileFailed = true;
                return false;
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Error($"[underwaterhorrors] biolumtex shader exception: {ex.Message}");
            compileFailed = true;
            return false;
        }

        // ---- Load + tessellate segment_mid mesh ----
        try
        {
            var shapeAsset = capi.Assets.TryGet(new AssetLocation(
                "underwaterhorrors", "shapes/entity/krakententsegment_mid.json"));
            if (shapeAsset == null)
            {
                capi.Logger.Error("[underwaterhorrors] biolumtex: segment_mid shape asset not found");
                return false;
            }
            var shape = shapeAsset.ToObject<Shape>();
            if (shape == null)
            {
                capi.Logger.Error("[underwaterhorrors] biolumtex: shape JSON parse failed");
                return false;
            }

            // The bellhead texture lives in the entity texture atlas.
            // Look up its UV position so the tessellator can map shape
            // texture codes (which all resolve to "base") to those UVs.
            var bellheadLoc = new AssetLocation("game", "entity/lore/shiver/bellhead");
            TextureAtlasPosition atlasPos = capi.EntityTextureAtlas[bellheadLoc];
            if (atlasPos == null)
            {
                capi.Logger.Error("[underwaterhorrors] biolumtex: bellhead texture not in entity atlas");
                return false;
            }
            atlasTextureId = atlasPos.atlasTextureId;

            var texSource = new SingleTextureSource(atlasPos, capi.EntityTextureAtlas.Size);
            capi.Tesselator.TesselateShape(
                "uhbiolumglow",
                shape,
                out MeshData meshData,
                texSource);
            if (meshData == null)
            {
                capi.Logger.Error("[underwaterhorrors] biolumtex: tessellation produced null mesh");
                return false;
            }
            // Diagnostic: dump mesh bounds so we can verify the
            // tesselator's coordinate convention. If X/Y/Z spans look
            // like ~[-0.2, 0.6] then the mesh is in block units and we
            // multiply by `size` (1.5) at render. If they look like
            // [-3, 9] the mesh is in raw model units (1/16 block) and
            // we'd multiply by `size / 16f`.
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            for (int i = 0; i < meshData.VerticesCount; i++)
            {
                float vx = meshData.xyz[i * 3 + 0];
                float vy = meshData.xyz[i * 3 + 1];
                float vz = meshData.xyz[i * 3 + 2];
                if (vx < minX) minX = vx; if (vx > maxX) maxX = vx;
                if (vy < minY) minY = vy; if (vy > maxY) maxY = vy;
                if (vz < minZ) minZ = vz; if (vz > maxZ) maxZ = vz;
            }
            segmentMidMesh = capi.Render.UploadMesh(meshData);
            capi.Logger.Notification(
                $"[underwaterhorrors] biolumtex initialized; mesh {meshData.VerticesCount} verts, " +
                $"bounds X[{minX:F3}..{maxX:F3}] Y[{minY:F3}..{maxY:F3}] Z[{minZ:F3}..{maxZ:F3}]");
            initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            capi.Logger.Error($"[underwaterhorrors] biolumtex tessellation exception: {ex.Message}");
            return false;
        }
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (Mode == 0) return;
        if (stage != EnumRenderStage.AfterFinalComposition) return;

        if (Mode != lastReportedMode)
        {
            lastReportedMode = Mode;
            string state = compileFailed ? "(SHADER FAILED)"
                          : !initialized ? "(NOT INITIALIZED)"
                          : "(OK)";
            capi.ShowChatMessage($"[uh] biolumtex mode={Mode} {state}");
        }

        if (compileFailed || !initialized || segmentMidMesh == null) return;

        // Mode -> depth attenuation strength.
        // depthFactor multiplied by 0.01 in the shader, so values feel
        // like "fade per 100 blocks of depth": 0.3 = ~30% dimmer over
        // 100 blocks; 1.5 = nearly floored after 100 blocks.
        float depthFactor = Mode switch
        {
            1 => 0.0f,
            2 => 0.3f,
            3 => 0.7f,
            4 => 1.5f,
            _ => 0.5f,
        };

        var render = capi.Render;
        render.GLEnableDepthTest();
        render.GLDepthMask(false);
        render.GlToggleBlend(true, EnumBlendMode.Glow);
        render.GlDisableCullFace();

        shaderProg.Use();
        shaderProg.UniformMatrix("projectionMatrix", render.CurrentProjectionMatrix);
        shaderProg.UniformMatrix("viewMatrix",       render.CameraMatrixOriginf);
        shaderProg.Uniform("glowColor",   0.35f, 0.95f, 1.00f);
        shaderProg.Uniform("intensity",   1.0f);
        shaderProg.Uniform("depthFactor", depthFactor);
        shaderProg.Uniform("surfaceY",    110.0f);
        shaderProg.BindTexture2D("entityTex", atlasTextureId, 0);

        var cameraPos = capi.World.Player.Entity.CameraPos;
        float[] modelMat = new float[16];

        foreach (Entity entity in capi.World.LoadedEntities.Values)
        {
            if (!IsTentacleChain(entity)) continue;

            // Mirror VS's EntityShapeRenderer.loadModelMatrix exactly so
            // our additive mesh sits on top of the orange render. The
            // crucial pieces are (a) hitbox-half-Y pre/post translates so
            // rotation pivots through the hitbox center, and (b) the
            // final (-0.5, 0, -0.5) shift VS applies to every entity to
            // center authored 0..16 voxel models on the entity origin.
            // See VSEssentials EntityShapeRenderer for the reference.
            float halfH = entity.SelectionBox != null
                ? entity.SelectionBox.Y2 / 2f
                : 0.05f;
            // entity Properties.Client.Size is 1.5; multiply by 1.02 so the
            // additive shell sits 1% outside the orange render surface and
            // doesn't z-fight with it. Without this nudge the cyan and
            // orange share the same depth value and the depth test (GL_LESS
            // by default) discards the second-pass cyan.
            const float scale = 1.5f * 1.02f;
            float effectiveYaw = entity.Pos.Yaw + (float)(Math.PI / 2.0);

            Mat4f.Identity(modelMat);
            Mat4f.Translate(modelMat, modelMat,
                (float)(entity.Pos.X - cameraPos.X),
                (float)(entity.Pos.InternalY - cameraPos.Y),
                (float)(entity.Pos.Z - cameraPos.Z));
            Mat4f.Translate(modelMat, modelMat, 0f, halfH, 0f);

            // X-Y-Z rotation order (matches VS's Quaterniond composition).
            Mat4f.RotateX(modelMat, modelMat, entity.Pos.Pitch);
            Mat4f.RotateY(modelMat, modelMat, effectiveYaw);
            Mat4f.RotateZ(modelMat, modelMat, entity.Pos.Roll);

            Mat4f.Translate(modelMat, modelMat, 0f, -halfH, 0f);
            Mat4f.Scale(modelMat, modelMat, scale, scale, scale);
            Mat4f.Translate(modelMat, modelMat, -0.5f, 0f, -0.5f);

            shaderProg.UniformMatrix("modelMatrix", modelMat);
            render.RenderMesh(segmentMidMesh);
        }

        shaderProg.Stop();
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        render.GLDepthMask(true);
        render.GlEnableCullFace();
    }

    private static bool IsTentacleChain(Entity entity)
    {
        if (entity?.Code?.Domain != "underwaterhorrors") return false;
        string path = entity.Code.Path;
        // For now, only segment_mid (which has the orange dashes texture).
        // Other segment types use different shapes/UVs and would need
        // their own pre-tessellation; we can extend later.
        return path == "krakententsegment_mid";
    }

    /// <summary>
    /// ITexPositionSource that returns the same atlas position for every
    /// texture code lookup. Works because our shape's textures all resolve
    /// to the bellhead atlas slot.
    /// </summary>
    private class SingleTextureSource : ITexPositionSource
    {
        private readonly TextureAtlasPosition pos;
        private readonly Size2i size;
        public Size2i AtlasSize => size;
        public TextureAtlasPosition this[string textureCode] => pos;
        public SingleTextureSource(TextureAtlasPosition pos, Size2i size)
        {
            this.pos = pos;
            this.size = size;
        }
    }

    public void Dispose()
    {
        segmentMidMesh?.Dispose();
        shaderProg?.Dispose();
        segmentMidMesh = null;
        shaderProg = null;
    }
}
