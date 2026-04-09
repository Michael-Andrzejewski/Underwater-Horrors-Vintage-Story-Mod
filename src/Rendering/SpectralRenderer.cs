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
        { "krakententsegment",     ColorUtil.ToRgba(255, 255, 50, 255) },   // Magenta
    };

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

            DrawWireframeBox(entity, color);
        }

        capi.Render.GLEnableDepthTest();
    }

    private void DrawWireframeBox(Entity entity, int color)
    {
        // Use the entity's block position as the origin so VS handles camera transform
        var pos = entity.Pos;
        int bx = (int)pos.X;
        int by = (int)pos.Y;
        int bz = (int)pos.Z;
        originPos.Set(bx, by, bz);
        originPos.dimension = pos.Dimension;

        // Sub-block offset from the origin
        float offX = (float)(pos.X - bx);
        float offY = (float)(pos.Y - by);
        float offZ = (float)(pos.Z - bz);

        // Hitbox dimensions -- use CollisionBox or SelectionBox, with fallback
        float halfW = 0.5f;
        float height = 1.0f;

        var box = entity.SelectionBox ?? entity.CollisionBox;
        if (box != null)
        {
            halfW = (box.X2 - box.X1) / 2f;
            height = box.Y2 - box.Y1;
        }

        // Minimum visible size for small entities (segments, claws)
        if (halfW < 0.25f) halfW = 0.25f;
        if (height < 0.5f) height = 0.5f;

        float x0 = offX - halfW;
        float x1 = offX + halfW;
        float y0 = offY;
        float y1 = offY + height;
        float z0 = offZ - halfW;
        float z1 = offZ + halfW;

        // Bottom face (4 edges)
        capi.Render.RenderLine(originPos, x0, y0, z0, x1, y0, z0, color);
        capi.Render.RenderLine(originPos, x1, y0, z0, x1, y0, z1, color);
        capi.Render.RenderLine(originPos, x1, y0, z1, x0, y0, z1, color);
        capi.Render.RenderLine(originPos, x0, y0, z1, x0, y0, z0, color);

        // Top face (4 edges)
        capi.Render.RenderLine(originPos, x0, y1, z0, x1, y1, z0, color);
        capi.Render.RenderLine(originPos, x1, y1, z0, x1, y1, z1, color);
        capi.Render.RenderLine(originPos, x1, y1, z1, x0, y1, z1, color);
        capi.Render.RenderLine(originPos, x0, y1, z1, x0, y1, z0, color);

        // Vertical edges (4 edges)
        capi.Render.RenderLine(originPos, x0, y0, z0, x0, y1, z0, color);
        capi.Render.RenderLine(originPos, x1, y0, z0, x1, y1, z0, color);
        capi.Render.RenderLine(originPos, x1, y0, z1, x1, y1, z1, color);
        capi.Render.RenderLine(originPos, x0, y0, z1, x0, y1, z1, color);
    }

    public void Dispose() { }
}
