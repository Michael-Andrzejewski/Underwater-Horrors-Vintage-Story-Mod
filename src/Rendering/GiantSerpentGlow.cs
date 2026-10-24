using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UnderwaterHorrors;

/// <summary>
/// The glowing rust leviathan's bioluminescence: patches of cyan dots grown
/// over its hide like coral on a wreck, and a few real lights along its body
/// that light the water around it. A giant glows when its
/// <see cref="BiolumAttr"/> watched attribute is set, the same flag the
/// night kraken uses.
///
/// The patches are picked once from the giant's own tesselated mesh, at
/// random but seeded by the entity id so every player sees the same ones.
/// Each dot remembers the spine joint its triangle belongs to, so it moves
/// with that bone. Every frame the dots are posed with the same matrices as
/// the model and drawn as soft additive sprites, right after the deep
/// silhouette (<see cref="GiantSerpentSilhouette"/> calls <see cref="Draw"/>).
/// Like the silhouette they draw after the water and the fog, so they show
/// in the murk, and they depth test against the silhouette's nearest
/// surface: dots on the far side of the body, or inside it, stay hidden.
/// </summary>
public sealed class GiantSerpentGlow : IDisposable
{
    public const string BiolumAttr = "underwaterhorrors:bioluminescent";

    private const int Patches = 150;
    // The kraken's glow color; each patch leans a little greener or bluer.
    private const float R = 0.35f, G = 0.95f, B = 1.0f;
    // The sprite reaches this many times the dot's radius, for the halo.
    private const float HaloScale = 4f;
    // Far away a dot never shrinks below this much of its distance (a few
    // pixels), so the patches still trace the body from afar.
    private const float MinAngularSize = 0.004f;
    // Dots fade out before the silhouette does.
    private const float FadeStartBlocks = 150f;
    // A little further toward the camera than the silhouette's pull, so a
    // dot on the nearest surface passes its depth and one behind it fails.
    private const float ExtraPullBlocks = 0.4f;
    private const float NearerBy = 2e-6f;
    // A bioluminescent corpse goes dark over this many seconds.
    private const float DeathFadeSeconds = 40f;

    private const int LightCount = 4;
    private const float LightStrength = 12f;

    private static IShaderProgram shader;
    private static ICoreClientAPI shaderApi;
    private static bool shaderFailed;

    private readonly ICoreClientAPI capi;
    private readonly Entity entity;
    private readonly EntityBehaviorGiantSerpentBody body;

    // Dots in the shape's own space (size-1 blocks), each riding one joint.
    private float[] dotPos = Array.Empty<float>(), dotNormal = Array.Empty<float>();
    private int[] dotJoint = Array.Empty<int>();
    private float[] dotRadius = Array.Empty<float>(), dotBright = Array.Empty<float>(), dotPhase = Array.Empty<float>();
    private byte[] dotColor = Array.Empty<byte>();
    private bool built;

    private MeshData mesh;
    private MeshRef meshRef;
    private float time;
    private float alive = 1f;

    // Dimmer where sunlight reaches it; sampled twice a second.
    private float dayDim = 1f;
    private float nextDimAt;
    private readonly BlockPos probe = new(0);

    private sealed class GlowLight : IPointLight
    {
        public Vec3f Color { get; } = new();
        public Vec3d Pos { get; } = new();
    }
    private readonly GlowLight[] lights = new GlowLight[LightCount];
    private bool lightsAdded;

    public int DotCount => dotJoint.Length;
    public int LightsShining => lightsAdded ? LightCount : 0;

    public GiantSerpentGlow(ICoreClientAPI capi, Entity entity, EntityBehaviorGiantSerpentBody body)
    {
        this.capi = capi;
        this.entity = entity;
        this.body = body;
        for (int i = 0; i < LightCount; i++) lights[i] = new GlowLight();
    }

    public void Dispose()
    {
        RemoveLights();
        meshRef?.Dispose();
        meshRef = null;
    }

    private void RemoveLights()
    {
        if (!lightsAdded) return;
        foreach (GlowLight light in lights) capi.Render.RemovePointLight(light);
        lightsAdded = false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Lights (every frame, from the body behavior)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Moves the lights to their spine joints and fades everything out after
    /// death. Called every frame at the Before stage, after the spine is
    /// posed, whether or not the body is in view.
    /// </summary>
    public void Update(float dt)
    {
        time += dt;
        if (!entity.Alive) alive = Math.Max(0f, alive - dt / DeathFadeSeconds);
        if (alive <= 0f)
        {
            RemoveLights();
            return;
        }

        int joints = body.JointCount;
        if (joints < 2) return;
        var pos = entity.Pos;
        for (int i = 0; i < LightCount; i++)
        {
            int j = (int)Math.Round((i + 0.5) / LightCount * (joints - 1));
            lights[i].Pos.Set(pos.X + body.RelX[j], pos.InternalY + body.RelY[j], pos.Z + body.RelZ[j]);
            float s = LightStrength * alive * (0.8f + 0.2f * (float)Math.Sin(time * 0.9 + i * 1.7));
            // The engine reads point light colors back to front.
            lights[i].Color.Set(B * s, G * s, R * s);
        }
        if (!lightsAdded)
        {
            foreach (GlowLight light in lights) capi.Render.AddPointLight(light);
            lightsAdded = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Dots
    // ═══════════════════════════════════════════════════════════════════

    // The hide's texture, read once per game: dots only go where it is
    // opaque. The fins and whiskers are mostly see-through texels, and a dot
    // there floats in the water beside the body.
    private static int[] texPixels;
    private static int texW, texH;
    private static ICoreClientAPI texApi;

    /// <summary>Candidate dots dropped because the hide is see-through there.</summary>
    public int TransparentRejects { get; private set; }
    /// <summary>True when the texture was read and dots were checked against it.</summary>
    public bool AlphaChecked { get; private set; }
    /// <summary>
    /// Share of the mesh's surface whose face centers are opaque in the
    /// texture: a check that UVs map onto the texture the right way round.
    /// </summary>
    public double OpaqueAreaShare { get; private set; }

    private TextureAtlasPosition LoadHideAlpha(Shape shape, EntityShapeRenderer esr)
    {
        if (shape.Textures == null || shape.Textures.Count != 1) return null;
        string code = null;
        AssetLocation loc = null;
        foreach (var kv in shape.Textures) { code = kv.Key; loc = kv.Value; }
        TextureAtlasPosition atlas = esr[code];
        if (atlas == null || loc == null) return null;
        if (texApi != capi)
        {
            texApi = capi;
            texPixels = null;
            IAsset asset = capi.Assets.TryGet(loc.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"));
            if (asset == null) return null;
            BitmapExternal bmp = capi.Render.BitmapCreateFromPng(asset.Data);
            try
            {
                texPixels = bmp.Pixels;
                texW = bmp.Width;
                texH = bmp.Height;
            }
            finally
            {
                bmp.Dispose();
            }
        }
        return texPixels == null ? null : atlas;
    }

    // Alpha (0 to 255) of the hide at an atlas UV.
    private static int AlphaAt(TextureAtlasPosition atlas, double u, double v)
    {
        double lu = (u - atlas.x1) / (atlas.x2 - atlas.x1);
        double lv = (v - atlas.y1) / (atlas.y2 - atlas.y1);
        int x = GameMath.Clamp((int)(lu * texW), 0, texW - 1);
        int y = GameMath.Clamp((int)(lv * texH), 0, texH - 1);
        return (texPixels[y * texW + x] >> 24) & 0xFF;
    }

    // Picks the patches from the giant's mesh: a face at random, by area, a
    // point on it, and a little cluster of dots around that point on the
    // same face. Every dot lies on its face, where the hide is opaque.
    private void BuildDots(EntityShapeRenderer esr)
    {
        built = true;
        Shape shape = entity.Properties.Client.LoadedShapeForEntity;
        if (shape == null) return;
        MeshData md;
        try
        {
            capi.Tesselator.TesselateShapeWithJointIds("giantserpentglow", shape, out md, esr, new Vec3f());
        }
        catch (Exception e)
        {
            capi.Logger.Error("[underwaterhorrors] giant serpent glow: could not read the mesh: {0}", e.Message);
            return;
        }
        int[] joints = md?.CustomInts?.Values;
        if (md == null || joints == null || md.Flags == null || md.Uv == null) return;

        TextureAtlasPosition atlas = LoadHideAlpha(shape, esr);
        AlphaChecked = atlas != null;
        if (!AlphaChecked)
            capi.Logger.Warning("[underwaterhorrors] giant serpent glow: could not read the hide texture; dots are not kept off see-through parts");

        // Each box face is a quad: indices (0, 1, 2) and (0, 2, 3), so its
        // corners are i0, i1, i2 (opposite i0) and i3, with edges i0-i1 and
        // i0-i3 spanning it.
        float[] xyz = md.xyz, uvs = md.Uv;
        int[] idx = md.Indices;
        int faces = md.IndicesCount / 6;
        var cum = new double[faces];
        double total = 0, opaqueArea = 0;
        for (int f = 0; f < faces; f++)
        {
            int i0 = idx[6 * f], i1 = idx[6 * f + 1], i3 = idx[6 * f + 5];
            bool quad = idx[6 * f + 3] == i0 && idx[6 * f + 4] == idx[6 * f + 2];
            double area = 0;
            if (quad)
            {
                double ux = xyz[i1 * 3] - xyz[i0 * 3], uy = xyz[i1 * 3 + 1] - xyz[i0 * 3 + 1], uz = xyz[i1 * 3 + 2] - xyz[i0 * 3 + 2];
                double vx = xyz[i3 * 3] - xyz[i0 * 3], vy = xyz[i3 * 3 + 1] - xyz[i0 * 3 + 1], vz = xyz[i3 * 3 + 2] - xyz[i0 * 3 + 2];
                double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                area = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            }
            total += area;
            cum[f] = total;
            if (atlas != null && area > 0)
            {
                double u = (uvs[i0 * 2] + uvs[idx[6 * f + 2] * 2]) / 2, v = (uvs[i0 * 2 + 1] + uvs[idx[6 * f + 2] * 2 + 1]) / 2;
                if (AlphaAt(atlas, u, v) >= 128) opaqueArea += area;
            }
        }
        if (total <= 0) return;
        OpaqueAreaShare = opaqueArea / total;

        var rand = new Random(unchecked((int)(entity.EntityId * 2654435761L)));
        var pos = new List<float>();
        var nrm = new List<float>();
        var jnt = new List<int>();
        var rad = new List<float>();
        var bri = new List<float>();
        var pha = new List<float>();
        var col = new List<byte>();
        var n = new float[3];
        int rejects = 0;

        bool Opaque(int i0, int i1, int i3, double fs, double ft)
        {
            if (atlas == null) return true;
            double u = uvs[i0 * 2] + fs * (uvs[i1 * 2] - uvs[i0 * 2]) + ft * (uvs[i3 * 2] - uvs[i0 * 2]);
            double v = uvs[i0 * 2 + 1] + fs * (uvs[i1 * 2 + 1] - uvs[i0 * 2 + 1]) + ft * (uvs[i3 * 2 + 1] - uvs[i0 * 2 + 1]);
            if (AlphaAt(atlas, u, v) >= 128) return true;
            rejects++;
            return false;
        }

        int patches = 0;
        for (int attempt = 0; attempt < Patches * 8 && patches < Patches; attempt++)
        {
            int f = Array.BinarySearch(cum, rand.NextDouble() * total);
            if (f < 0) f = ~f;
            f = Math.Min(f, faces - 1);
            int i0 = idx[6 * f], i1 = idx[6 * f + 1], i3 = idx[6 * f + 5];

            double e1x = xyz[i1 * 3] - xyz[i0 * 3], e1y = xyz[i1 * 3 + 1] - xyz[i0 * 3 + 1], e1z = xyz[i1 * 3 + 2] - xyz[i0 * 3 + 2];
            double e2x = xyz[i3 * 3] - xyz[i0 * 3], e2y = xyz[i3 * 3 + 1] - xyz[i0 * 3 + 1], e2z = xyz[i3 * 3 + 2] - xyz[i0 * 3 + 2];
            double l1 = Math.Sqrt(e1x * e1x + e1y * e1y + e1z * e1z), l2 = Math.Sqrt(e2x * e2x + e2y * e2y + e2z * e2z);
            if (l1 < 1e-4 || l2 < 1e-4) continue;

            // A point on an opaque part of the face.
            double s0 = 0, t0 = 0;
            bool found = false;
            for (int k = 0; k < 12 && !found; k++)
            {
                s0 = 0.08 + rand.NextDouble() * 0.84;
                t0 = 0.08 + rand.NextDouble() * 0.84;
                found = Opaque(i0, i1, i3, s0, t0);
            }
            if (!found) continue;

            VertexFlags.UnpackNormal(md.Flags[i0], n);
            double nl = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (nl < 1e-6) continue;
            double nx = n[0] / nl, ny = n[1] / nl, nz = n[2] / nl;
            patches++;

            int dots = 3 + rand.Next(6);
            double spread = 0.12 + rand.NextDouble() * 0.3;
            // No dot wider than the face it sits on.
            double maxRadius = Math.Max(0.02, 0.6 * Math.Min(l1, l2));
            float patchBright = 0.6f + (float)rand.NextDouble() * 0.4f;
            // From a greener to a bluer cyan.
            float lean = (float)rand.NextDouble() * 2 - 1;
            byte cr = (byte)(255 * R), cg = (byte)(255 * (G - 0.15f * Math.Max(0, lean))), cb = (byte)(255 * (B - 0.3f * Math.Max(0, -lean)));
            for (int d = 0; d < dots; d++)
            {
                double fs = s0, ft = t0;
                if (d > 0)
                {
                    bool placed = false;
                    for (int k = 0; k < 8 && !placed; k++)
                    {
                        double ang = rand.NextDouble() * Math.PI * 2;
                        double dist = spread * Math.Sqrt(rand.NextDouble());
                        fs = s0 + Math.Cos(ang) * dist / l1;
                        ft = t0 + Math.Sin(ang) * dist / l2;
                        placed = fs > 0.03 && fs < 0.97 && ft > 0.03 && ft < 0.97 && Opaque(i0, i1, i3, fs, ft);
                    }
                    if (!placed) continue;
                }
                // On the face, a hair above it.
                pos.Add((float)(xyz[i0 * 3] + fs * e1x + ft * e2x + nx * 0.02));
                pos.Add((float)(xyz[i0 * 3 + 1] + fs * e1y + ft * e2y + ny * 0.02));
                pos.Add((float)(xyz[i0 * 3 + 2] + fs * e1z + ft * e2z + nz * 0.02));
                nrm.Add((float)nx); nrm.Add((float)ny); nrm.Add((float)nz);
                jnt.Add(joints[i0]);
                double r = d == 0 ? 0.07 + rand.NextDouble() * 0.05 : 0.035 + rand.NextDouble() * 0.045;
                rad.Add((float)Math.Min(r, maxRadius));
                bri.Add(patchBright * (0.7f + 0.3f * (float)rand.NextDouble()));
                pha.Add((float)(rand.NextDouble() * Math.PI * 2));
                col.Add(cr); col.Add(cg); col.Add(cb);
            }
        }

        TransparentRejects = rejects;
        dotPos = pos.ToArray();
        dotNormal = nrm.ToArray();
        dotJoint = jnt.ToArray();
        dotRadius = rad.ToArray();
        dotBright = bri.ToArray();
        dotPhase = pha.ToArray();
        dotColor = col.ToArray();
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
            capi.Shader.RegisterFileShaderProgram("giantglow", shader);
            if (!shader.Compile()) throw new Exception("compile failed");
            return true;
        }
        catch (Exception e)
        {
            capi.Logger.Error("[underwaterhorrors] giant serpent glow shader: {0}", e.Message);
            shader = null;
            shaderFailed = true;
            return false;
        }
    }

    /// <summary>
    /// Poses and draws the dots. Called by the silhouette straight after its
    /// two passes, with the matrices it drew with; depthPull is its pull in
    /// blocks.
    /// </summary>
    public void Draw(EntityShapeRenderer esr, float[] projection, float[] view, float[] model,
        float[] jointMatrices, float depthPull, float depthPullPerBlock)
    {
        if (alive <= 0f) return;
        if (!built) BuildDots(esr);
        int count = dotJoint.Length;
        if (count == 0 || !EnsureShader()) return;

        UpdateDayDim();
        BuildSprites(view, model, jointMatrices, out int used);
        if (used == 0) return;

        if (meshRef == null) meshRef = capi.Render.UploadMesh(mesh);
        else capi.Render.UpdateMesh(meshRef, mesh);

        var render = capi.Render;
        render.GLEnableDepthTest();
        render.GLDepthMask(false);
        render.GlToggleBlend(true, EnumBlendMode.Glow);
        render.GlDisableCullFace();

        shader.Use();
        shader.UniformMatrix("projectionMatrix", projection);
        shader.UniformMatrix("viewMatrix", view);
        shader.Uniform("depthPull", depthPull + ExtraPullBlocks);
        shader.Uniform("depthPullPerBlock", depthPullPerBlock);
        shader.Uniform("nearerBy", NearerBy);
        render.RenderMesh(meshRef);
        shader.Stop();

        render.GLDepthMask(true);
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        render.GlEnableCullFace();
    }

    // Light at the mouth: where daylight reaches the serpent, the glow is
    // faint; in the deep or at night it shines at full strength.
    private void UpdateDayDim()
    {
        if (time < nextDimAt) return;
        nextDimAt = time + 0.5f;
        var p = entity.Pos;
        probe.Set((int)Math.Floor(p.X), (int)Math.Floor(p.InternalY), (int)Math.Floor(p.Z));
        probe.dimension = p.Dimension;
        Vec4f light = capi.World.BlockAccessor.GetLightRGBs(probe);
        dayDim = 1f - 0.65f * GameMath.Clamp(light.W, 0f, 1f);
    }

    private void BuildSprites(float[] view, float[] model, float[] mats, out int used)
    {
        int count = dotJoint.Length;
        if (mesh == null)
        {
            mesh = new MeshData(count * 4, count * 6, false, true, true, false);
            for (int i = 0; i < count; i++)
            {
                int v = i * 4;
                mesh.AddIndex(v); mesh.AddIndex(v + 1); mesh.AddIndex(v + 2);
                mesh.AddIndex(v); mesh.AddIndex(v + 2); mesh.AddIndex(v + 3);
            }
            mesh.VerticesCount = count * 4;
            // The first upload sizes the buffers for every dot; later
            // updates may draw fewer.
            meshRef?.Dispose();
            meshRef = null;
        }

        // Camera right and up in camera-relative world space: the first two
        // rows of the view matrix (column-major).
        float rx = view[0], ry = view[4], rz = view[8];
        float ux = view[1], uy = view[5], uz = view[9];
        float size = entity.Properties.Client.Size;
        float wave = time * 0.9f;
        float[] xyz = mesh.xyz, uv = mesh.Uv;
        byte[] rgba = mesh.Rgba;
        used = 0;

        for (int i = 0; i < count; i++)
        {
            int o = dotJoint[i] * 16;
            if (o + 16 > mats.Length) continue;
            float px = dotPos[i * 3], py = dotPos[i * 3 + 1], pz = dotPos[i * 3 + 2];
            float nx = dotNormal[i * 3], ny = dotNormal[i * 3 + 1], nz = dotNormal[i * 3 + 2];

            // Joint, then model (both column-major); the model matrix is
            // camera-relative, so w* is the dot relative to the camera.
            float jx = mats[o] * px + mats[o + 4] * py + mats[o + 8] * pz + mats[o + 12];
            float jy = mats[o + 1] * px + mats[o + 5] * py + mats[o + 9] * pz + mats[o + 13];
            float jz = mats[o + 2] * px + mats[o + 6] * py + mats[o + 10] * pz + mats[o + 14];
            float wx = model[0] * jx + model[4] * jy + model[8] * jz + model[12];
            float wy = model[1] * jx + model[5] * jy + model[9] * jz + model[13];
            float wz = model[2] * jx + model[6] * jy + model[10] * jz + model[14];

            float kx = mats[o] * nx + mats[o + 4] * ny + mats[o + 8] * nz;
            float ky = mats[o + 1] * nx + mats[o + 5] * ny + mats[o + 9] * nz;
            float kz = mats[o + 2] * nx + mats[o + 6] * ny + mats[o + 10] * nz;
            float mx = model[0] * kx + model[4] * ky + model[8] * kz;
            float my = model[1] * kx + model[5] * ky + model[9] * kz;
            float mz = model[2] * kx + model[6] * ky + model[10] * kz;

            float dist = (float)Math.Sqrt(wx * wx + wy * wy + wz * wz);
            float ml = (float)Math.Sqrt(mx * mx + my * my + mz * mz);
            if (dist < 1e-3f || ml < 1e-6f) continue;
            // Dots on surfaces turned away from the camera fade out.
            float facing = -(mx * wx + my * wy + mz * wz) / (ml * dist);
            float face = GameMath.Clamp((facing + 0.05f) / 0.3f, 0f, 1f);
            float far = 1f - GameMath.Clamp((dist - FadeStartBlocks) / (GiantSerpentSilhouette.FadeEndBlocks - FadeStartBlocks), 0f, 1f);
            float pulse = 0.7f + 0.3f * (float)Math.Sin(wave + dotPhase[i]);
            float a = dotBright[i] * pulse * face * far * dayDim * alive;
            if (a < 0.01f) continue;

            float s = Math.Max(dotRadius[i] * size, dist * MinAngularSize) * HaloScale;
            int v = used * 4;
            Corner(xyz, uv, v, wx, wy, wz, -rx - ux, -ry - uy, -rz - uz, s, -1, -1);
            Corner(xyz, uv, v + 1, wx, wy, wz, rx - ux, ry - uy, rz - uz, s, 1, -1);
            Corner(xyz, uv, v + 2, wx, wy, wz, rx + ux, ry + uy, rz + uz, s, 1, 1);
            Corner(xyz, uv, v + 3, wx, wy, wz, -rx + ux, -ry + uy, -rz + uz, s, -1, 1);
            byte alpha = (byte)(GameMath.Clamp(a, 0f, 1f) * 255);
            for (int k = 0; k < 4; k++)
            {
                int c = (v + k) * 4;
                rgba[c] = dotColor[i * 3]; rgba[c + 1] = dotColor[i * 3 + 1]; rgba[c + 2] = dotColor[i * 3 + 2]; rgba[c + 3] = alpha;
            }
            used++;
        }

        mesh.VerticesCount = Math.Max(used, 1) * 4;
        mesh.IndicesCount = used * 6;
        if (meshRef == null)
        {
            // Size the buffers for every dot on the first upload.
            mesh.VerticesCount = count * 4;
            mesh.IndicesCount = count * 6;
            for (int i = used * 4 * 4 + 3; i < count * 16; i += 4) rgba[i] = 0;
        }
    }

    private static void Corner(float[] xyz, float[] uv, int v,
        float cx, float cy, float cz, float dx, float dy, float dz, float s, float u, float w)
    {
        xyz[v * 3] = cx + dx * s;
        xyz[v * 3 + 1] = cy + dy * s;
        xyz[v * 3 + 2] = cz + dz * s;
        uv[v * 2] = u;
        uv[v * 2 + 1] = w;
    }
}
