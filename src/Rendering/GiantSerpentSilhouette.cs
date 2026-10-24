using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// The giant serpent's deep silhouette: a dark, soft-edged shape that shows
/// where the serpent is, even where its real model cannot be seen.
///
/// Water hides a deep entity completely. From above, the water surface is
/// drawn over everything a dozen blocks down; underwater, the fog swallows
/// the model a couple of dozen blocks out. So the serpent, which spends its
/// time 15 to 30 blocks down, could not be seen at all. This draws a tube
/// around its spine (built every frame from the joints the body behavior
/// just posed, sized from the trunk at each bone) after the scene is
/// finished, so neither the water surface nor the fog covers it. It still
/// depth-tests against terrain and the serpent's own model, and the shader
/// fades it out where the real model is visible: near the surface when
/// looking down from above, and near the camera underwater.
/// </summary>
public sealed class GiantSerpentSilhouette : IRenderer
{
    private const int RingVerts = 10;
    // A little larger than the trunk so it is not buried inside the model,
    // and taller on top where the spines stand up.
    private const double Inflate = 1.15;
    private const double TopBoost = 1.4;
    // Per layer: the tube's near and far sides both draw, so the middle of
    // the shape reaches about 1 - (1 - 0.5)^2 = 0.75.
    private const float MaxAlpha = 0.5f;
    // How far the shape is pulled toward the camera for depth testing, as a
    // multiple of the entity size: enough to clear the fins and spikes.
    private const float DepthPull = 1.5f;

    private static IShaderProgram shader;
    private static ICoreClientAPI shaderApi;
    private static bool shaderFailed;

    private readonly ICoreClientAPI capi;
    private readonly Entity entity;
    private readonly EntityBehaviorGiantSerpentBody body;
    private readonly double[][] radii;

    private MeshData mesh;
    private MeshRef meshRef;
    private int rings;
    private double[] cx, cy, cz, rw, rh;

    private double surfaceY;
    private float surfaceTimer;
    private readonly BlockPos probe = new(0);

    public double RenderOrder => 0.5;
    public int RenderRange => 9999;

    public GiantSerpentSilhouette(ICoreClientAPI capi, Entity entity, EntityBehaviorGiantSerpentBody body, double[][] radii)
    {
        this.capi = capi;
        this.entity = entity;
        this.body = body;
        this.radii = radii;
        surfaceY = capi.World.SeaLevel;
        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterFinalComposition, "underwaterhorrors-giantsilhouette");
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);
        meshRef?.Dispose();
        meshRef = null;
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
        int joints = body.JointCount;
        if (joints < 3 || radii.Length < joints - 1) return;
        EntityPlayer player = capi.World.Player?.Entity;
        if (player == null || !EnsureShader()) return;

        double size = entity.Properties.Client.Size;
        var pos = entity.Pos;
        double ox = pos.X, oy = pos.InternalY, oz = pos.Z;
        Vec3d cam = player.CameraPos;

        UpdateSurface(deltaTime, ox, oy, oz);
        BuildRings(joints, size);
        WriteMesh(cam.X - ox, cam.Y - oy, cam.Z - oz);

        if (meshRef == null) meshRef = capi.Render.UploadMesh(mesh);
        else capi.Render.UpdateMesh(meshRef, mesh);

        probe.Set((int)Math.Floor(cam.X), (int)Math.Floor(cam.Y), (int)Math.Floor(cam.Z));
        probe.dimension = pos.Dimension;
        bool camUnderwater = WaterHelper.IsWaterBlock(capi.World.BlockAccessor.GetBlock(probe, BlockLayersAccess.Fluid));

        var render = capi.Render;
        render.GLEnableDepthTest();
        render.GLDepthMask(false);
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        render.GlDisableCullFace();

        shader.Use();
        shader.UniformMatrix("projectionMatrix", render.CurrentProjectionMatrix);
        shader.UniformMatrix("viewMatrix", render.CameraMatrixOriginf);
        shader.Uniform("originRel", (float)(ox - cam.X), (float)(oy - cam.Y), (float)(oz - cam.Z));
        shader.Uniform("originY", (float)oy);
        shader.Uniform("surfaceY", (float)surfaceY);
        shader.Uniform("camUnderwater", camUnderwater ? 1 : 0);
        shader.Uniform("tint", 0.015f, 0.03f, 0.045f);
        shader.Uniform("maxAlpha", MaxAlpha);
        shader.Uniform("depthPull", (float)(DepthPull * size));
        render.RenderMesh(meshRef);
        shader.Stop();

        render.GLDepthMask(true);
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

    // Ring centers (relative to the mouth) and radii: a narrow snout ahead
    // of the mouth, one ring per spine joint, and a tall thin tail fin.
    private void BuildRings(int joints, double size)
    {
        int needed = joints + 2;
        if (rings != needed)
        {
            rings = needed;
            cx = new double[rings]; cy = new double[rings]; cz = new double[rings];
            rw = new double[rings]; rh = new double[rings];
            mesh = null;
            meshRef?.Dispose();
            meshRef = null;
        }

        double[] jx = body.RelX, jy = body.RelY, jz = body.RelZ;
        double s = Inflate * size;
        for (int i = 0; i < joints; i++)
        {
            double[] r = radii[Math.Min(i, radii.Length - 1)];
            double shrink = i == joints - 1 ? 0.85 : 1.0;
            cx[i + 1] = jx[i]; cy[i + 1] = jy[i]; cz[i + 1] = jz[i];
            rw[i + 1] = r[0] * s * shrink;
            rh[i + 1] = r[1] * s * shrink;
        }

        Extend(0, 1, 2, 1.6 * size, 0.22 * s, 0.2 * s);
        Extend(rings - 1, rings - 2, rings - 3, 3.0 * size, 0.08 * s, 0.55 * s);
    }

    // Ring `at` placed `length` beyond ring `from`, continuing the line from `back`.
    private void Extend(int at, int from, int back, double length, double w, double h)
    {
        double dx = cx[from] - cx[back], dy = cy[from] - cy[back], dz = cz[from] - cz[back];
        double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (l < 1e-6) { dx = 1; dy = 0; dz = 0; l = 1; }
        cx[at] = cx[from] + dx / l * length;
        cy[at] = cy[from] + dy / l * length;
        cz[at] = cz[from] + dz / l * length;
        rw[at] = w;
        rh[at] = h;
    }

    private void WriteMesh(double camX, double camY, double camZ)
    {
        int verts = rings * RingVerts;
        if (mesh == null)
        {
            mesh = new MeshData(verts, (rings - 1) * RingVerts * 6, false, true, false, false);
            for (int k = 0; k < rings - 1; k++)
            {
                for (int j = 0; j < RingVerts; j++)
                {
                    int a = k * RingVerts + j, b = k * RingVerts + (j + 1) % RingVerts;
                    int c = a + RingVerts, d = b + RingVerts;
                    mesh.AddIndex(a); mesh.AddIndex(c); mesh.AddIndex(b);
                    mesh.AddIndex(b); mesh.AddIndex(c); mesh.AddIndex(d);
                }
            }
        }
        mesh.VerticesCount = verts;
        float[] xyz = mesh.xyz, uv = mesh.Uv;

        double sideX = 1, sideZ = 0;
        for (int k = 0; k < rings; k++)
        {
            int prev = Math.Max(0, k - 1), next = Math.Min(rings - 1, k + 1);
            double tx = cx[prev] - cx[next], ty = cy[prev] - cy[next], tz = cz[prev] - cz[next];
            double tl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            if (tl < 1e-6) { tx = 1; ty = 0; tz = 0; tl = 1; }
            tx /= tl; ty /= tl; tz /= tl;

            // Sideways is horizontal and square to the body; up completes
            // the frame and is kept pointing skyward.
            double sx = -tz, sz = tx, sl = Math.Sqrt(sx * sx + sz * sz);
            if (sl > 1e-4) { sideX = sx / sl; sideZ = sz / sl; }
            // up = side x tangent, with side = (sideX, 0, sideZ)
            double ux = -sideZ * ty;
            double uy = sideZ * tx - sideX * tz;
            double uz = sideX * ty;
            if (uy < 0) { ux = -ux; uy = -uy; uz = -uz; }

            for (int j = 0; j < RingVerts; j++)
            {
                double ang = 2 * Math.PI * j / RingVerts;
                double cs = Math.Cos(ang), sn = Math.Sin(ang);
                double w = rw[k], h = sn > 0 ? rh[k] * TopBoost : rh[k];
                double px = cx[k] + sideX * cs * w + ux * sn * h;
                double py = cy[k] + uy * sn * h;
                double pz = cz[k] + sideZ * cs * w + uz * sn * h;

                // How squarely this point faces the camera, for the soft edge.
                double nx = sideX * cs / w + ux * sn / h, ny = uy * sn / h, nz = sideZ * cs / w + uz * sn / h;
                double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                double vx = camX - px, vy = camY - py, vz = camZ - pz;
                double vl = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                double facing = nl > 1e-9 && vl > 1e-9 ? Math.Abs((nx * vx + ny * vy + nz * vz) / (nl * vl)) : 1;

                int v = k * RingVerts + j;
                xyz[v * 3] = (float)px;
                xyz[v * 3 + 1] = (float)py;
                xyz[v * 3 + 2] = (float)pz;
                uv[v * 2] = (float)facing;
                uv[v * 2 + 1] = 0;
            }
        }
    }
}
