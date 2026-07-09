using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public class SpectralRenderer : IRenderer
{
    public bool Active;

    private readonly ICoreClientAPI capi;
    private readonly BlockPos originPos = new BlockPos(0, 0, 0, 0);

    // Color-coded by entity type (ARGB integers)
    private static readonly Dictionary<string, int> EntityColors = new()
    {
        { "seaserpent",            ColorUtil.ToRgba(255, 255, 50, 50) },    // Red
        { "serpenthitbox",         ColorUtil.ToRgba(255, 255, 160, 0) },    // Orange
        { "krakenbody",            ColorUtil.ToRgba(255, 50, 255, 50) },    // Green
        { "krakententacle",        ColorUtil.ToRgba(255, 50, 255, 255) },   // Cyan
        { "krakenambienttentacle", ColorUtil.ToRgba(255, 80, 80, 255) },    // Blue
        { "krakententacleclaw",    ColorUtil.ToRgba(255, 255, 255, 50) },   // Yellow
        { "krakententsegment",       ColorUtil.ToRgba(255, 255, 50, 255) },   // Magenta
        { "krakententsegment_mid",   ColorUtil.ToRgba(255, 200, 50, 255) },   // Magenta-ish
        { "krakententsegment_outer", ColorUtil.ToRgba(255, 150, 50, 255) },   // Magenta-ish
    };

    // Perf guards: each wireframe box is 12 individual RenderLine draw
    // calls, and a kraken fields hundreds of chain segments plus one
    // biolight per segment. Drawing them all tanked the frame rate the
    // moment spectral was toggled near a kraken. Biolights are pure
    // light emitters and are never drawn; chain segments are drawn as a
    // stable 1-in-8 sample (enough to trace the tentacle's path); and
    // anything beyond MaxDrawDist blocks from the player is skipped.
    private const double MaxDrawDist = 120.0;
    private const double MaxDrawDistSq = MaxDrawDist * MaxDrawDist;

    private static bool IsChainSegment(string code) =>
        code.StartsWith("krakententsegment") && code != "krakententsegment_mid_claw";

    // Bright yellow for the serpent's head marker
    private static readonly int HeadColor = ColorUtil.ToRgba(255, 255, 255, 0);

    // Must match EntityBehaviorSerpentAI.HeadForwardOffset
    private const float SerpentHeadOffset = 9.0f;
    private const float HeadBoxRadius = 1.5f;
    private const float HeadBoxHeight = 2.0f;

    private const int FallbackColor = unchecked((int)0xFFFFFFFF); // White

    // Spawner block markers (found by scanning loaded chunks for the creature
    // spawner block entity). Serpent = hot pink, kraken = purple, both bright
    // so they stand out from the red serpent boxes.
    private static readonly int SerpentSpawnerColor = ColorUtil.ToRgba(255, 255, 105, 180);
    private static readonly int KrakenSpawnerColor = ColorUtil.ToRgba(255, 170, 0, 255);
    private readonly List<BlockPos> serpentSpawners = new();
    private readonly List<BlockPos> krakenSpawners = new();
    private float spawnerScanAccum = 999f;          // force a scan on the first frame
    private const float SpawnerScanInterval = 0.5f; // rescan twice a second
    private const int SpawnerScanChunkRadius = 4;   // ~128 blocks horizontally

    public double RenderOrder => 1.0;
    public int RenderRange => 9999;

    public SpectralRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!Active) return;
        // World can be torn down while this debug renderer is still registered
        // (it is only unregistered on mod dispose), e.g. just after leaving a
        // world with the toggle still on.
        if (capi.World == null) return;

        var entities = capi.World.LoadedEntities;
        var playerPos = capi.World.Player?.Entity?.Pos;

        capi.Render.GLDisableDepthTest();

        foreach (Entity entity in entities != null ? (IEnumerable<Entity>)entities.Values : Array.Empty<Entity>())
        {
            if (entity == null || !entity.Alive) continue;
            if (entity.Code?.Domain != "underwaterhorrors") continue;

            string code = entity.Code.Path;
            if (code == "biolight") continue;

            // Stable 1-in-8 sample of chain segments (keyed on the
            // entity id so the same segments stay visible, no flicker).
            if (IsChainSegment(code) && (entity.EntityId & 7) != 0) continue;

            if (playerPos != null)
            {
                double dx = entity.Pos.X - playerPos.X;
                double dy = entity.Pos.Y - playerPos.Y;
                double dz = entity.Pos.Z - playerPos.Z;
                if (dx * dx + dy * dy + dz * dz > MaxDrawDistSq) continue;
            }

            if (!EntityColors.TryGetValue(code, out int color))
                color = FallbackColor;

            DrawEntityBox(entity, color);

            // Draw a separate head box for the sea serpent
            if (code == "seaserpent")
            {
                DrawSerpentHead(entity);
            }
        }

        RescanSpawners(deltaTime, playerPos);
        DrawSpawners(serpentSpawners, SerpentSpawnerColor, playerPos);
        DrawSpawners(krakenSpawners, KrakenSpawnerColor, playerPos);

        capi.Render.GLEnableDepthTest();
    }

    // Periodically walk the loaded chunks near the player and cache the
    // positions of any creature-spawner block entities, split by type.
    private void RescanSpawners(float dt, EntityPos playerPos)
    {
        spawnerScanAccum += dt;
        if (spawnerScanAccum < SpawnerScanInterval) return;
        spawnerScanAccum = 0f;

        serpentSpawners.Clear();
        krakenSpawners.Clear();
        if (playerPos == null) return;

        int pcx = (int)playerPos.X >> 5;
        int pcy = (int)playerPos.Y >> 5;
        int pcz = (int)playerPos.Z >> 5;
        int r = SpawnerScanChunkRadius;
        var ba = capi.World.BlockAccessor;

        for (int cx = pcx - r; cx <= pcx + r; cx++)
            for (int cz = pcz - r; cz <= pcz + r; cz++)
                for (int cy = Math.Max(0, pcy - r); cy <= pcy + r; cy++)
                {
                    IWorldChunk chunk = ba.GetChunk(cx, cy, cz);
                    if (chunk?.BlockEntities == null) continue;
                    foreach (BlockEntity be in chunk.BlockEntities.Values)
                    {
                        if (be is not BlockEntityCreatureSpawner) continue;
                        string path = be.Block?.Code?.Path ?? "";
                        (path.Contains("kraken") ? krakenSpawners : serpentSpawners).Add(be.Pos.Copy());
                    }
                }
    }

    private void DrawSpawners(List<BlockPos> positions, int color, EntityPos playerPos)
    {
        for (int i = 0; i < positions.Count; i++)
        {
            BlockPos p = positions[i];
            if (playerPos != null)
            {
                double dx = p.X - playerPos.X, dy = p.Y - playerPos.Y, dz = p.Z - playerPos.Z;
                if (dx * dx + dy * dy + dz * dz > MaxDrawDistSq) continue;
            }
            originPos.Set(p.X, p.Y, p.Z);
            originPos.dimension = p.dimension;
            DrawBox(-0.1f, 0f, -0.1f, 1.1f, 1.7f, 1.1f, color);
        }
    }

    private void DrawEntityBox(Entity entity, int color)
    {
        var pos = entity.Pos;
        int bx = (int)pos.X;
        int by = (int)pos.Y;
        int bz = (int)pos.Z;
        originPos.Set(bx, by, bz);
        originPos.dimension = pos.Dimension;

        float offX = (float)(pos.X - bx);
        float offY = (float)(pos.Y - by);
        float offZ = (float)(pos.Z - bz);

        var box = entity.SelectionBox ?? entity.CollisionBox;

        float halfW = 0.5f;
        float height = 1.0f;
        if (box != null)
        {
            halfW = (box.X2 - box.X1) / 2f;
            height = box.Y2 - box.Y1;
        }

        if (halfW < 0.25f) halfW = 0.25f;
        if (height < 0.5f) height = 0.5f;

        DrawBox(offX - halfW, offY, offZ - halfW,
                offX + halfW, offY + height, offZ + halfW, color);
    }

    /// <summary>
    /// Draws a wireframe box at the serpent's computed head position.
    /// The head is offset forward along the entity's yaw by SerpentHeadOffset blocks.
    /// </summary>
    private void DrawSerpentHead(Entity entity)
    {
        var pos = entity.Pos;
        float yaw = (float)pos.Yaw;

        // Head world position
        double headX = pos.X + Math.Sin(yaw) * SerpentHeadOffset;
        double headY = pos.Y;
        double headZ = pos.Z + Math.Cos(yaw) * SerpentHeadOffset;

        // Use the head's block position as the render origin
        int bx = (int)headX;
        int by = (int)headY;
        int bz = (int)headZ;
        originPos.Set(bx, by, bz);
        originPos.dimension = pos.Dimension;

        float offX = (float)(headX - bx);
        float offY = (float)(headY - by);
        float offZ = (float)(headZ - bz);

        float r = HeadBoxRadius;
        DrawBox(offX - r, offY, offZ - r,
                offX + r, offY + HeadBoxHeight, offZ + r, HeadColor);
    }

    private void DrawBox(float x0, float y0, float z0,
                         float x1, float y1, float z1, int color)
    {
        // Bottom face
        capi.Render.RenderLine(originPos, x0, y0, z0, x1, y0, z0, color);
        capi.Render.RenderLine(originPos, x1, y0, z0, x1, y0, z1, color);
        capi.Render.RenderLine(originPos, x1, y0, z1, x0, y0, z1, color);
        capi.Render.RenderLine(originPos, x0, y0, z1, x0, y0, z0, color);

        // Top face
        capi.Render.RenderLine(originPos, x0, y1, z0, x1, y1, z0, color);
        capi.Render.RenderLine(originPos, x1, y1, z0, x1, y1, z1, color);
        capi.Render.RenderLine(originPos, x1, y1, z1, x0, y1, z1, color);
        capi.Render.RenderLine(originPos, x0, y1, z1, x0, y1, z0, color);

        // Vertical edges
        capi.Render.RenderLine(originPos, x0, y0, z0, x0, y1, z0, color);
        capi.Render.RenderLine(originPos, x1, y0, z0, x1, y1, z0, color);
        capi.Render.RenderLine(originPos, x1, y0, z1, x1, y1, z1, color);
        capi.Render.RenderLine(originPos, x0, y0, z1, x0, y1, z1, color);
    }

    public void Dispose() { }
}
