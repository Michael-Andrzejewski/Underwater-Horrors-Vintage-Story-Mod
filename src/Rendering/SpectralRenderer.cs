using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
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
        { "krakenbody",            ColorUtil.ToRgba(255, 50, 255, 50) },    // Green
        { "krakententacle",        ColorUtil.ToRgba(255, 50, 255, 255) },   // Cyan
        { "krakenambienttentacle", ColorUtil.ToRgba(255, 80, 80, 255) },    // Blue
        { "krakententacleclaw",    ColorUtil.ToRgba(255, 255, 255, 50) },   // Yellow
        { "krakententsegment",       ColorUtil.ToRgba(255, 255, 50, 255) },   // Magenta
        { "krakententsegment_mid",   ColorUtil.ToRgba(255, 200, 50, 255) },   // Magenta-ish
        { "krakententsegment_outer", ColorUtil.ToRgba(255, 150, 50, 255) },   // Magenta-ish
    };

    // Bright yellow for the serpent's head marker
    private static readonly int HeadColor = ColorUtil.ToRgba(255, 255, 255, 0);

    // Must match EntityBehaviorSerpentAI.HeadForwardOffset
    private const float SerpentHeadOffset = 9.0f;
    private const float HeadBoxRadius = 1.5f;
    private const float HeadBoxHeight = 2.0f;

    private const int FallbackColor = unchecked((int)0xFFFFFFFF); // White

    public double RenderOrder => 1.0;
    public int RenderRange => 9999;

    public SpectralRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!Active) return;

        var entities = capi.World.LoadedEntities;
        if (entities == null || entities.Count == 0) return;

        capi.Render.GLDisableDepthTest();

        foreach (Entity entity in entities.Values)
        {
            if (entity == null || !entity.Alive) continue;
            if (entity.Code?.Domain != "underwaterhorrors") continue;

            string code = entity.Code.Path;
            if (!EntityColors.TryGetValue(code, out int color))
                color = FallbackColor;

            DrawEntityBox(entity, color);

            // Draw a separate head box for the sea serpent
            if (code == "seaserpent")
            {
                DrawSerpentHead(entity);
            }
        }

        capi.Render.GLEnableDepthTest();
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
