using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace UnderwaterHorrors;

/// <summary>
/// Client-side renderer that modulates GlowLevel on kraken entity types
/// with staggered phase offsets, producing a bioluminescent wave that
/// appears to travel outward from the body along the tentacles.
/// </summary>
public class BioluminescentRenderer : IRenderer
{
    public bool Active;

    private readonly ICoreClientAPI capi;

    // Entity type references — resolved once on first frame
    private EntityProperties propsBody;
    private EntityProperties propsSegInner;
    private EntityProperties propsSegMid;
    private EntityProperties propsSegOuter;
    private EntityProperties propsTentacle;
    private EntityProperties propsAmbient;
    private EntityProperties propsClaw;
    private bool resolved;
    private bool wasActive;

    // Wave parameters — initialized with defaults, updated via LoadConfig
    private float pulseSpeed  = 1.4f;
    private int glowMin       = 32;
    private int glowMax       = 200;
    private int bodyGlowMin   = 16;
    private int bodyGlowMax   = 128;

    // Phase offsets for each entity group (radians).
    // Lower values peak first → wave travels body → inner → mid → outer → tip.
    private const float PhaseBody       = 0.0f;
    private const float PhaseSegInner   = 0.6f;
    private const float PhaseSegMid     = 1.2f;
    private const float PhaseSegOuter   = 1.8f;
    private const float PhaseTip        = 2.4f;
    private const float PhaseAmbient    = 1.0f;
    private const float PhaseClaw       = 2.4f;

    // AssetLocations
    private static readonly AssetLocation LocBody      = new("underwaterhorrors", "krakenbody");
    private static readonly AssetLocation LocSegInner   = new("underwaterhorrors", "krakententsegment");
    private static readonly AssetLocation LocSegMid     = new("underwaterhorrors", "krakententsegment_mid");
    private static readonly AssetLocation LocSegOuter   = new("underwaterhorrors", "krakententsegment_outer");
    private static readonly AssetLocation LocTentacle   = new("underwaterhorrors", "krakententacle");
    private static readonly AssetLocation LocAmbient    = new("underwaterhorrors", "krakenambienttentacle");
    private static readonly AssetLocation LocClaw       = new("underwaterhorrors", "krakententacleclaw");

    public double RenderOrder => 0.0;
    public int RenderRange => 9999;

    public BioluminescentRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void LoadConfig(UnderwaterHorrorsConfig config)
    {
        if (config == null) return;
        pulseSpeed  = config.BiolumPulseSpeed;
        glowMin     = config.BiolumGlowMin;
        glowMax     = config.BiolumGlowMax;
        bodyGlowMin = config.BiolumBodyGlowMin;
        bodyGlowMax = config.BiolumBodyGlowMax;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!resolved)
        {
            resolved = true;
            propsBody     = capi.World.GetEntityType(LocBody);
            propsSegInner = capi.World.GetEntityType(LocSegInner);
            propsSegMid   = capi.World.GetEntityType(LocSegMid);
            propsSegOuter = capi.World.GetEntityType(LocSegOuter);
            propsTentacle = capi.World.GetEntityType(LocTentacle);
            propsAmbient  = capi.World.GetEntityType(LocAmbient);
            propsClaw     = capi.World.GetEntityType(LocClaw);
        }

        // When toggled off, reset all glow levels to 0 once
        if (!Active)
        {
            if (wasActive)
            {
                wasActive = false;
                ResetGlow(propsBody);
                ResetGlow(propsSegInner);
                ResetGlow(propsSegMid);
                ResetGlow(propsSegOuter);
                ResetGlow(propsTentacle);
                ResetGlow(propsAmbient);
                ResetGlow(propsClaw);
            }
            return;
        }

        wasActive = true;
        float t = (float)capi.World.ElapsedMilliseconds / 1000f;

        ApplyGlow(propsBody,      t, PhaseBody,     bodyGlowMin, bodyGlowMax);
        ApplyGlow(propsSegInner,  t, PhaseSegInner, glowMin,     glowMax);
        ApplyGlow(propsSegMid,    t, PhaseSegMid,   glowMin,     glowMax);
        ApplyGlow(propsSegOuter,  t, PhaseSegOuter, glowMin,     glowMax);
        ApplyGlow(propsTentacle,  t, PhaseTip,      glowMin,     glowMax);
        ApplyGlow(propsAmbient,   t, PhaseAmbient,  glowMin,     glowMax);
        ApplyGlow(propsClaw,      t, PhaseClaw,     glowMin,     glowMax);
    }

    private static void ResetGlow(EntityProperties props)
    {
        if (props != null) props.Client.GlowLevel = 0;
    }

    private void ApplyGlow(EntityProperties props, float time, float phase, int min, int max)
    {
        if (props == null) return;

        // sin wave mapped from [-1,1] to [min,max]
        float wave = (float)(0.5 + 0.5 * Math.Sin(time * pulseSpeed - phase));
        int glow = min + (int)((max - min) * wave);
        props.Client.GlowLevel = glow;
    }

    public void Dispose() { }
}
