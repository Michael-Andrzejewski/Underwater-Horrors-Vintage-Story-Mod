using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace UnderwaterHorrors;

public class UnderwaterHorrorsModSystem : ModSystem
{
    public static UnderwaterHorrorsConfig Config { get; private set; }

    private ICoreServerAPI sapi;
    private ICoreClientAPI capi;
    private SpectralRenderer spectralRenderer;
    private BioluminescentGlowRenderer biolumGlowRenderer;
    private BiolumTextureRenderer biolumTexRenderer;
    private bool glowActive;

    // playerUID -> entityId of assigned creature
    private Dictionary<string, long> activeCreatures = new();

    // entityId -> seconds target player has been on land
    private Dictionary<long, float> landTimers = new();

    // Session-only flag: when false, OnSpawnCheck returns immediately
    // (commands like /uh spawn still work — only NATURAL spawn checks
    // are gated). Resets to true on server restart.
    private bool naturalSpawningEnabled = true;

    // Reusable BlockPos to avoid per-call allocation in hot paths
    private readonly BlockPos reusableBlockPos = new BlockPos(0, 0, 0, 0);


    // Reusable list for despawn removals to avoid allocation each check
    private readonly List<string> despawnRemoveList = new();

    public static void DebugLog(ICoreAPI api, string message)
    {
        if (Config == null || !Config.DebugLogging) return;
        if (api.Side != EnumAppSide.Server) return;

        ICoreServerAPI serverApi = api as ICoreServerAPI;
        if (serverApi == null) return;

        serverApi.SendMessageToGroup(GlobalConstants.GeneralChatGroup, "[UH] " + message, EnumChatType.Notification);
    }

    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        api.RegisterEntityBehaviorClass("underwaterhorrors:oceancreature", typeof(EntityBehaviorOceanCreature));
        api.RegisterEntityBehaviorClass("underwaterhorrors:serpentai", typeof(EntityBehaviorSerpentAI));
        api.RegisterEntityBehaviorClass("underwaterhorrors:deepserpentai", typeof(EntityBehaviorDeepSerpentAI));
        api.RegisterEntityBehaviorClass("underwaterhorrors:krakenbody", typeof(EntityBehaviorKrakenBody));
        api.RegisterEntityBehaviorClass("underwaterhorrors:tentacle", typeof(EntityBehaviorTentacle));
        api.RegisterEntityBehaviorClass("underwaterhorrors:ambienttentacle", typeof(EntityBehaviorAmbientTentacle));
        api.RegisterEntityBehaviorClass("underwaterhorrors:tentaclerenderer", typeof(EntityBehaviorTentacleRenderer));

        api.RegisterEntity("EntityBioluminescentLight", typeof(EntityBioluminescentLight));
        api.RegisterEntity("SerpentEntity", typeof(SerpentEntity));
        api.RegisterEntity("DeepSerpentEntity", typeof(DeepSerpentEntity));

        api.Network.RegisterChannel("underwaterhorrors")
            .RegisterMessageType(typeof(DebugToggleMessage));
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        capi = api;

        spectralRenderer = new SpectralRenderer(capi);
        capi.Event.RegisterRenderer(spectralRenderer, EnumRenderStage.AfterOIT, "underwaterhorrors-spectral");

        // Bioluminescent glow renderer — registered at FOUR stages so the
        // /uh biolum testmode <n> command can binary-search which stage
        // (OIT vs AfterOIT vs AfterPostProcessing vs AfterFinalComposition)
        // actually produces visible glow at night underwater. The renderer
        // itself gates on its `Mode` field and only draws at the stage
        // that matches the active mode.
        biolumGlowRenderer = new BioluminescentGlowRenderer(capi);
        capi.Event.RegisterRenderer(biolumGlowRenderer, EnumRenderStage.OIT,                    "underwaterhorrors-biolum-oit");
        capi.Event.RegisterRenderer(biolumGlowRenderer, EnumRenderStage.AfterOIT,               "underwaterhorrors-biolum-aoit");
        capi.Event.RegisterRenderer(biolumGlowRenderer, EnumRenderStage.AfterPostProcessing,    "underwaterhorrors-biolum-app");
        capi.Event.RegisterRenderer(biolumGlowRenderer, EnumRenderStage.AfterFinalComposition,  "underwaterhorrors-biolum-afc");
        // Compile on initial shader load AND on /reload shaders so live
        // shader edits work without a full restart. Returning true tells
        // the engine the reload succeeded.
        capi.Event.ReloadShader += () => biolumGlowRenderer.Initialize();

        // Texture-based emissive renderer: re-renders the actual segment_mid
        // 3D mesh with a custom additive shader so the glow appears ON the
        // orange dashes themselves rather than as a separate billboard. Modes
        // 30-34 in the test menu use this renderer.
        biolumTexRenderer = new BiolumTextureRenderer(capi);
        capi.Event.RegisterRenderer(biolumTexRenderer, EnumRenderStage.AfterFinalComposition, "underwaterhorrors-biolumtex");
        capi.Event.ReloadShader += () => biolumTexRenderer.Initialize();

        api.Network.GetChannel("underwaterhorrors")
            .SetMessageHandler<DebugToggleMessage>(OnDebugToggleReceived);
    }

    private void OnDebugToggleReceived(DebugToggleMessage msg)
    {
        if (msg.Toggle == "glow")
        {
            glowActive = msg.Active;
            ApplyGlow(msg.Active);
        }
        else if (msg.Toggle == "spectral")
        {
            spectralRenderer.Active = msg.Active;
        }
        else if (msg.Toggle == "biolum_mode")
        {
            // Modes 30..39 drive the texture-mesh renderer; everything else
            // drives the billboard renderer. Setting the unused renderer's
            // Mode to 0 disables it cleanly.
            int v = msg.Value;
            if (v >= 30)
            {
                biolumGlowRenderer.Mode = 0;
                biolumTexRenderer.Mode  = v - 29; // 30 -> 1, 31 -> 2, ..., 34 -> 5
            }
            else
            {
                biolumTexRenderer.Mode  = 0;
                biolumGlowRenderer.Mode = v;
            }
            capi.Logger.Notification($"[underwaterhorrors] biolum mode set to {v} (billboard={biolumGlowRenderer.Mode}, tex={biolumTexRenderer.Mode})");
        }
    }

    // All mod entity type codes to apply glow to
    private static readonly AssetLocation[] ModEntityTypes = new[]
    {
        new AssetLocation("underwaterhorrors", "seaserpent"),
        new AssetLocation("underwaterhorrors", "seaserpent2"),
        new AssetLocation("underwaterhorrors", "seaserpent3"),
        new AssetLocation("underwaterhorrors", "krakenbody"),
        new AssetLocation("underwaterhorrors", "krakententacle"),
        new AssetLocation("underwaterhorrors", "krakenambienttentacle"),
        new AssetLocation("underwaterhorrors", "krakententacleclaw"),
        new AssetLocation("underwaterhorrors", "krakententsegment"),
        new AssetLocation("underwaterhorrors", "krakententsegment_mid"),
        new AssetLocation("underwaterhorrors", "krakententsegment_outer"),
        new AssetLocation("underwaterhorrors", "krakententbase"),
        new AssetLocation("underwaterhorrors", "krakententmid"),
        new AssetLocation("underwaterhorrors", "krakententtaper"),
        new AssetLocation("underwaterhorrors", "krakententsnapper"),
        new AssetLocation("underwaterhorrors", "krakententexamp"),
    };

    private void ApplyGlow(bool on)
    {
        // Note: GlowLevel is read when the entity's mesh is first tesselated,
        // so modifying the shared EntityProperties only affects NEWLY SPAWNED
        // entities. Existing creatures keep their original glow level until
        // they despawn and respawn. (Confirmed in VS-GlowingArrows mod, which
        // also only sets GlowLevel once at world load.)
        int level = on ? 255 : 0;

        foreach (var assetLoc in ModEntityTypes)
        {
            EntityProperties props = capi.World.GetEntityType(assetLoc);
            if (props != null)
            {
                props.Client.GlowLevel = level;
            }
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        sapi = api;

        Config = LoadConfig();

        int spawnInterval = (int)(Config.SpawnCheckIntervalSeconds * 1000);
        int despawnInterval = (int)(Config.DespawnCheckIntervalSeconds * 1000);

        api.Event.RegisterGameTickListener(OnSpawnCheck, spawnInterval);
        api.Event.RegisterGameTickListener(OnDespawnCheck, despawnInterval);

        api.Event.PlayerJoin += OnPlayerJoinSyncDebug;

        RegisterCommands(api);
    }

    private void OnPlayerJoinSyncDebug(IServerPlayer player)
    {
        var channel = sapi.Network.GetChannel("underwaterhorrors");
        if (Config.GlowDebugActive)
        {
            channel.SendPacket(new DebugToggleMessage { Toggle = "glow", Active = true }, player);
        }
        if (Config.SpectralDebugActive)
        {
            channel.SendPacket(new DebugToggleMessage { Toggle = "spectral", Active = true }, player);
        }

        // Push the active glow test mode so the renderer comes up in the
        // right state right away (no need to re-issue /uh biolum testmode
        // after every join). Sent regardless of value — 0 disables.
        channel.SendPacket(
            new DebugToggleMessage { Toggle = "biolum_mode", Active = Config.BiolumTestMode != 0, Value = Config.BiolumTestMode },
            player);
    }

    private void RegisterCommands(ICoreServerAPI api)
    {
        api.ChatCommands.Create("uh")
            .WithDescription("Underwater Horrors settings")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("spawnchance")
                .WithDescription("Get or set spawn chance per check (0.0 to 1.0)")
                .WithArgs(api.ChatCommands.Parsers.OptionalFloat("chance"))
                .HandleWith(OnCmdSpawnChance)
            .EndSubCommand()
            .BeginSubCommand("debug")
                .WithDescription("Toggle debug logging on or off")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("onoff"))
                .HandleWith(OnCmdDebug)
            .EndSubCommand()
            .BeginSubCommand("natural")
                .WithDescription("Enable or disable natural creature spawning for this session (resets on server restart)")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("onoff"))
                .HandleWith(OnCmdNatural)
            .EndSubCommand()
            .BeginSubCommand("spawn")
                .WithDescription("Force spawn a creature on the calling player")
                .WithArgs(api.ChatCommands.Parsers.Word("type", new[] { "serpent", "deepserpent", "serpent3", "kraken" }))
                .HandleWith(OnCmdSpawn)
            .EndSubCommand()
            .BeginSubCommand("dragspeed")
                .WithDescription("Get or set tentacle drag speed")
                .WithArgs(api.ChatCommands.Parsers.OptionalFloat("speed"))
                .HandleWith(OnCmdDragSpeed)
            .EndSubCommand()
            .BeginSubCommand("killall")
                .WithDescription("Remove all Underwater Horrors entities from the world")
                .HandleWith(OnCmdKillAll)
            .EndSubCommand()
            .BeginSubCommand("glow")
                .WithDescription("Toggle max brightness glow on all mod entities")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("onoff"))
                .HandleWith(OnCmdGlow)
            .EndSubCommand()
            .BeginSubCommand("spectral")
                .WithDescription("Toggle see-through-blocks wireframe outlines on all mod entities")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("onoff"))
                .HandleWith(OnCmdSpectral)
            .EndSubCommand()
            .BeginSubCommand("status")
                .WithDescription("Show all toggle states and entity counts")
                .HandleWith(OnCmdStatus)
            .EndSubCommand()
            .BeginSubCommand("serpent")
                .WithDescription("Serpent-specific settings")
                .BeginSubCommand("anim")
                    .WithDescription("Freeze nearest serpent and loop an animation (idle, idle2, walk, walk1, pose, off)")
                    .WithArgs(api.ChatCommands.Parsers.Word("animation"))
                    .HandleWith(OnCmdSerpentAnim)
                .EndSubCommand()
            .EndSubCommand()
            .BeginSubCommand("kraken")
                .WithDescription("Kraken-specific settings")
                .BeginSubCommand("biolum")
                    .WithDescription("Toggle bioluminescent glow on kraken tentacles")
                    .WithArgs(api.ChatCommands.Parsers.OptionalWord("onoff"))
                    .HandleWith(OnCmdBiolum)
                    .BeginSubCommand("pulse")
                        .WithDescription("Toggle pulsing/ripple effect on bioluminescent glow")
                        .WithArgs(api.ChatCommands.Parsers.OptionalWord("onoff"))
                        .HandleWith(OnCmdBiolumPulse)
                    .EndSubCommand()
                .EndSubCommand()
                .BeginSubCommand("show")
                    .WithDescription("Spawn a single kraken model in front of you, frozen with no AI, for inspection")
                    .WithArgs(api.ChatCommands.Parsers.Word("part", new[] {
                        "body", "tentacle", "ambient", "segment", "segment_mid", "segment_outer", "claw",
                        "base", "mid", "taper", "snapper", "examp"
                    }))
                    .HandleWith(OnCmdKrakenShow)
                .EndSubCommand()
            .EndSubCommand()
            .BeginSubCommand("biolumtest")
                .WithDescription("Bioluminescent glow shader tester (10 variants)")
                .BeginSubCommand("mode")
                    .WithDescription("Set the active glow renderer mode (0=off, 1=sanity, 2-10=variants). Run with no arg to print catalog.")
                    .WithArgs(api.ChatCommands.Parsers.OptionalInt("n"))
                    .HandleWith(OnCmdBiolumTestMode)
                .EndSubCommand()
                .BeginSubCommand("spawn")
                    .WithDescription("Spawn 5 static kraken mid-segments in a line in front of you for visual testing")
                    .HandleWith(OnCmdBiolumTestSpawn)
                .EndSubCommand()
                .BeginSubCommand("clear")
                    .WithDescription("Kill all static-flagged kraken segments and claws spawned for testing")
                    .HandleWith(OnCmdBiolumTestClear)
                .EndSubCommand()
            .EndSubCommand();
    }

    private TextCommandResult OnCmdStatus(TextCommandCallingArgs args)
    {
        // Count entities by type
        var counts = new Dictionary<string, int>();
        int totalAlive = 0;

        foreach (Entity entity in sapi.World.LoadedEntities.Values)
        {
            if (entity == null || !entity.Alive) continue;
            if (entity.Code?.Domain != "underwaterhorrors") continue;

            string path = entity.Code.Path;
            if (!counts.ContainsKey(path))
                counts[path] = 0;
            counts[path]++;
            totalAlive++;
        }

        string nl = "\n";
        string msg = "=== Underwater Horrors Status ===" + nl;

        // Toggles
        msg += nl + "-- Toggles --" + nl;
        msg += $"  Debug logging: {(Config.DebugLogging ? "on" : "off")}" + nl;
        msg += $"  Natural spawning: {(naturalSpawningEnabled ? "on" : "off")}" + nl;
        msg += $"  Glow debug: {(Config.GlowDebugActive ? "on" : "off")}" + nl;
        msg += $"  Spectral debug: {(Config.SpectralDebugActive ? "on" : "off")}" + nl;
        msg += $"  Bioluminescence: {(Config.BiolumActive ? "on" : "off")}" + nl;
        msg += $"  Biolum pulsing: {(Config.BiolumPulsing ? "on" : "off")}" + nl;

        // Config values
        msg += nl + "-- Config --" + nl;
        msg += $"  Spawn chance: {Config.SpawnChancePerCheck:F3}" + nl;
        msg += $"  Drag speed: {Config.TentacleDragSpeed:F1}" + nl;
        msg += $"  Biolum pulse speed: {Config.BiolumPulseSpeed:F1}" + nl;

        // Entities
        msg += nl + "-- Entities ({0} alive) --" + nl;
        if (counts.Count == 0)
        {
            msg = msg.Replace("{0}", "0");
            msg += "  (none)" + nl;
        }
        else
        {
            msg = msg.Replace("{0}", totalAlive.ToString());
            foreach (var kvp in counts)
            {
                msg += $"  {kvp.Key}: {kvp.Value}" + nl;
            }
        }

        // Active creature assignments
        msg += nl + $"-- Active creatures: {activeCreatures.Count} players tracked --";

        return TextCommandResult.Success(msg);
    }

    private TextCommandResult OnCmdKillAll(TextCommandCallingArgs args)
    {
        int killed = 0;
        int corpses = 0;
        List<Entity> toRemove = new();

        foreach (Entity entity in sapi.World.LoadedEntities.Values)
        {
            if (entity == null) continue;
            string code = entity.Code?.Domain ?? "";
            if (code == "underwaterhorrors")
            {
                toRemove.Add(entity);
            }
        }

        foreach (Entity entity in toRemove)
        {
            if (entity.Alive)
            {
                killed++;
            }
            else
            {
                corpses++;
            }
            entity.Die(EnumDespawnReason.Expire);
        }

        activeCreatures.Clear();
        landTimers.Clear();

        return TextCommandResult.Success($"Removed {killed} living + {corpses} dead Underwater Horrors entities");
    }

    private TextCommandResult OnCmdSpawnChance(TextCommandCallingArgs args)
    {
        float? chance = args.Parsers[0].GetValue() as float?;
        if (chance == null)
        {
            return TextCommandResult.Success($"Current spawn chance: {Config.SpawnChancePerCheck:F3}");
        }
        Config.SpawnChancePerCheck = chance.Value;
        sapi.StoreModConfig(Config, "UnderwaterHorrorsConfig.json");
        return TextCommandResult.Success($"Spawn chance set to {Config.SpawnChancePerCheck:F3}");
    }

    private TextCommandResult OnCmdDebug(TextCommandCallingArgs args)
    {
        string val = args.Parsers[0].GetValue() as string;
        if (string.IsNullOrEmpty(val))
        {
            return TextCommandResult.Success($"Debug logging: {(Config.DebugLogging ? "on" : "off")}");
        }
        Config.DebugLogging = val == "on" || val == "true" || val == "1";
        sapi.StoreModConfig(Config, "UnderwaterHorrorsConfig.json");
        return TextCommandResult.Success($"Debug logging: {(Config.DebugLogging ? "on" : "off")}");
    }

    private TextCommandResult OnCmdNatural(TextCommandCallingArgs args)
    {
        string val = args.Parsers[0].GetValue() as string;
        if (string.IsNullOrEmpty(val))
        {
            naturalSpawningEnabled = !naturalSpawningEnabled;
        }
        else
        {
            naturalSpawningEnabled = val == "on" || val == "true" || val == "1";
        }
        return TextCommandResult.Success(
            $"Natural spawning: {(naturalSpawningEnabled ? "on" : "off")} (session-only, resets on server restart)");
    }

    private TextCommandResult OnCmdSpawn(TextCommandCallingArgs args)
    {
        string type = args.Parsers[0].GetValue() as string;
        IServerPlayer caller = args.Caller.Player as IServerPlayer;
        if (caller == null) return TextCommandResult.Error("Must be called by a player");

        Entity creature;
        if (type == "serpent")
        {
            creature = SpawnSerpent(caller, forceDeep: false);  // always regular
        }
        else if (type == "deepserpent")
        {
            creature = SpawnSerpent(caller, forceDeep: true);
        }
        else if (type == "serpent3")
        {
            creature = SpawnSerpent3(caller);
        }
        else
        {
            creature = SpawnKraken(caller);
        }

        if (creature == null) return TextCommandResult.Error("Failed to spawn creature");

        activeCreatures[caller.PlayerUID] = creature.EntityId;
        return TextCommandResult.Success($"Spawned {type} targeting {caller.PlayerName}");
    }

    private TextCommandResult OnCmdDragSpeed(TextCommandCallingArgs args)
    {
        float? speed = args.Parsers[0].GetValue() as float?;
        if (speed == null)
        {
            return TextCommandResult.Success($"Current drag speed: {Config.TentacleDragSpeed:F3}");
        }
        Config.TentacleDragSpeed = speed.Value;
        sapi.StoreModConfig(Config, "UnderwaterHorrorsConfig.json");
        return TextCommandResult.Success($"Drag speed set to {Config.TentacleDragSpeed:F3}");
    }

    private TextCommandResult OnCmdGlow(TextCommandCallingArgs args)
    {
        string val = args.Parsers[0].GetValue() as string;
        if (string.IsNullOrEmpty(val))
        {
            Config.GlowDebugActive = !Config.GlowDebugActive;
        }
        else
        {
            Config.GlowDebugActive = val == "on" || val == "true" || val == "1";
        }

        sapi.StoreModConfig(Config, "UnderwaterHorrorsConfig.json");
        sapi.Network.GetChannel("underwaterhorrors")
            .BroadcastPacket(new DebugToggleMessage { Toggle = "glow", Active = Config.GlowDebugActive });
        return TextCommandResult.Success(
            $"Glow debug: {(Config.GlowDebugActive ? "on" : "off")} " +
            "(note: only affects newly spawned creatures — existing ones keep their glow until they respawn)");
    }

    private TextCommandResult OnCmdSpectral(TextCommandCallingArgs args)
    {
        string val = args.Parsers[0].GetValue() as string;
        if (string.IsNullOrEmpty(val))
        {
            Config.SpectralDebugActive = !Config.SpectralDebugActive;
        }
        else
        {
            Config.SpectralDebugActive = val == "on" || val == "true" || val == "1";
        }
        sapi.StoreModConfig(Config, "UnderwaterHorrorsConfig.json");
        sapi.Network.GetChannel("underwaterhorrors")
            .BroadcastPacket(new DebugToggleMessage { Toggle = "spectral", Active = Config.SpectralDebugActive });
        return TextCommandResult.Success($"Spectral debug: {(Config.SpectralDebugActive ? "on" : "off")}");
    }

    private TextCommandResult OnCmdBiolum(TextCommandCallingArgs args)
    {
        string val = args.Parsers[0].GetValue() as string;
        if (string.IsNullOrEmpty(val))
        {
            Config.BiolumActive = !Config.BiolumActive;
        }
        else
        {
            Config.BiolumActive = val == "on" || val == "true" || val == "1";
        }
        sapi.StoreModConfig(Config, "UnderwaterHorrorsConfig.json");

        // Toggle biolum lights on all existing tentacles immediately
        foreach (Entity entity in sapi.World.LoadedEntities.Values)
        {
            if (entity == null || !entity.Alive) continue;
            if (entity.Code?.Domain != "underwaterhorrors") continue;

            var tentBehavior = entity.GetBehavior<EntityBehaviorTentacle>();
            if (tentBehavior != null)
            {
                tentBehavior.SetBiolumActive(Config.BiolumActive);
            }
        }

        return TextCommandResult.Success($"Kraken bioluminescence: {(Config.BiolumActive ? "on" : "off")}");
    }

    private TextCommandResult OnCmdBiolumPulse(TextCommandCallingArgs args)
    {
        string val = args.Parsers[0].GetValue() as string;
        if (string.IsNullOrEmpty(val))
        {
            Config.BiolumPulsing = !Config.BiolumPulsing;
        }
        else
        {
            Config.BiolumPulsing = val == "on" || val == "true" || val == "1";
        }
        sapi.StoreModConfig(Config, "UnderwaterHorrorsConfig.json");
        return TextCommandResult.Success($"Kraken biolum pulsing: {(Config.BiolumPulsing ? "on" : "off")}");
    }

    private TextCommandResult OnCmdKrakenShow(TextCommandCallingArgs args)
    {
        string part = args.Parsers[0].GetValue() as string;
        IServerPlayer caller = args.Caller.Player as IServerPlayer;
        if (caller?.Entity == null) return TextCommandResult.Error("Must be called by a player");

        string code = part switch
        {
            "body"          => "krakenbody",
            "tentacle"      => "krakententacle",
            "ambient"       => "krakenambienttentacle",
            "segment"       => "krakententsegment",
            "segment_mid"   => "krakententsegment_mid",
            "segment_outer" => "krakententsegment_outer",
            "claw"          => "krakententacleclaw",
            "base"          => "krakententbase",
            "mid"           => "krakententmid",
            "taper"         => "krakententtaper",
            "snapper"       => "krakententsnapper",
            "examp"         => "krakententexamp",
            _ => null,
        };
        if (code == null) return TextCommandResult.Error("Unknown part");

        AssetLocation asset = new AssetLocation("underwaterhorrors", code);
        EntityProperties props = sapi.World.GetEntityType(asset);
        if (props == null) return TextCommandResult.Error($"Entity type {asset} not registered");

        // Spawn 4 blocks ahead of the player along their yaw, at eye height
        float yaw = caller.Entity.Pos.Yaw;
        const double dist = 4.0;
        double sx = caller.Entity.Pos.X + Math.Sin(yaw) * dist;
        double sz = caller.Entity.Pos.Z + Math.Cos(yaw) * dist;
        double sy = caller.Entity.Pos.Y;

        Entity ent = sapi.World.ClassRegistry.CreateEntity(props);
        ent.Pos.SetPos(sx, sy, sz);
        ent.Pos.Dimension = caller.Entity.Pos.Dimension;
        ent.Pos.SetFrom(ent.Pos);

        // Mark as static so AI behaviors and tentacle renderer skip — pure inspection
        ent.WatchedAttributes.SetBool("underwaterhorrors:static", true);

        sapi.World.SpawnEntity(ent);

        return TextCommandResult.Success($"Spawned {code} (static) at ({sx:F1}, {sy:F1}, {sz:F1}) — clean up with /uh killall");
    }

    // ---- Bioluminescent glow shader test commands ----

    private static readonly string[] BiolumModeDescriptions = new[]
    {
        "0   OFF                      - no glow rendered (clean baseline)",
        "1   SANITY                   - line crosses, no shader (proves renderer is firing)",
        "--- Billboard rounds 1+2 (kept for fallback comparison) ---------",
        "2   AfterOIT additive        - original cyan halo",
        "7   AfterFinalComposite      - additive cyan after UI composite (round 1 favorite)",
        "20  trunk-aligned billboard  - mode 7 + alignment + tiny diffusion",
        "--- TEXTURE-ON-MESH (renders the actual segment_mid 3D model) ---",
        "30  no depth attenuation     - uniform cyan glow over orange dashes, top to bottom",
        "31  weak depth attenuation   - slight top-to-bottom dimming",
        "32  medium depth attenuation - clear top-to-bottom contrast",
        "33  strong depth attenuation - deep parts much dimmer (visible only near surface)",
        "34  custom (mode 31 default) - same as 31 but reserved for future tweaks",
    };

    private TextCommandResult OnCmdBiolumTestMode(TextCommandCallingArgs args)
    {
        int? n = args.Parsers[0].GetValue() as int?;
        if (n == null)
        {
            string lines = string.Join("\n  ", BiolumModeDescriptions);
            return TextCommandResult.Success(
                $"Current biolum test mode: {Config.BiolumTestMode}\nModes:\n  {lines}");
        }

        int mode = n.Value;
        // Allow 0-20 (billboard modes) and 30-34 (texture-mesh modes).
        if (mode < 0 || mode > 34 || (mode > 20 && mode < 30))
            return TextCommandResult.Error("Mode must be 0..20 (billboard) or 30..34 (texture-mesh)");

        Config.BiolumTestMode = mode;
        sapi.StoreModConfig(Config, "UnderwaterHorrorsConfig.json");
        sapi.Network.GetChannel("underwaterhorrors")
            .BroadcastPacket(new DebugToggleMessage { Toggle = "biolum_mode", Active = mode != 0, Value = mode });

        // Find the description line that starts with this mode number.
        string prefix = mode + " ";
        string desc = "(set)";
        foreach (string line in BiolumModeDescriptions)
        {
            if (line.StartsWith(prefix) || line.StartsWith(mode + "  "))
            {
                desc = line;
                break;
            }
        }
        return TextCommandResult.Success($"Biolum test mode -> {desc}");
    }

    private TextCommandResult OnCmdBiolumTestSpawn(TextCommandCallingArgs args)
    {
        IServerPlayer caller = args.Caller.Player as IServerPlayer;
        if (caller?.Entity == null) return TextCommandResult.Error("Must be called by a player");

        AssetLocation asset = new AssetLocation("underwaterhorrors", "krakententsegment_mid");
        EntityProperties props = sapi.World.GetEntityType(asset);
        if (props == null) return TextCommandResult.Error("Entity type krakententsegment_mid not registered");

        // Spawn 5 segments in a horizontal line in front of the player at
        // eye level, 2 blocks apart (so the soft-falloff billboards have
        // visible gaps between them — easier to see overlap behavior than
        // a single tightly-packed kraken chain).
        float yaw = caller.Entity.Pos.Yaw;
        double sinYaw = Math.Sin(yaw);
        double cosYaw = Math.Cos(yaw);
        double startX = caller.Entity.Pos.X + sinYaw * 4.0;
        double startZ = caller.Entity.Pos.Z + cosYaw * 4.0;
        double startY = caller.Entity.Pos.Y;

        // Perpendicular axis (lateral) so the line is broadside to the player
        double rightX = Math.Cos(yaw);
        double rightZ = -Math.Sin(yaw);

        int spawned = 0;
        for (int i = -2; i <= 2; i++)
        {
            double sx = startX + rightX * (i * 2.0);
            double sz = startZ + rightZ * (i * 2.0);
            double sy = startY;

            Entity ent = sapi.World.ClassRegistry.CreateEntity(props);
            ent.Pos.SetPos(sx, sy, sz);
            ent.Pos.Dimension = caller.Entity.Pos.Dimension;
            ent.Pos.SetFrom(ent.Pos);
            ent.WatchedAttributes.SetBool("underwaterhorrors:static", true);
            sapi.World.SpawnEntity(ent);
            spawned++;
        }

        return TextCommandResult.Success(
            $"Spawned {spawned} static mid-segments. Run /uh biolumtest mode <n> to switch shader variants. " +
            $"Clean up with /uh biolumtest clear.");
    }

    private TextCommandResult OnCmdBiolumTestClear(TextCommandCallingArgs args)
    {
        int killed = 0;
        var toKill = new List<Entity>();
        foreach (Entity entity in sapi.World.LoadedEntities.Values)
        {
            if (entity == null) continue;
            if (entity.Code?.Domain != "underwaterhorrors") continue;
            if (!entity.WatchedAttributes.GetBool("underwaterhorrors:static", false)) continue;
            toKill.Add(entity);
        }
        foreach (Entity e in toKill)
        {
            e.Die(EnumDespawnReason.Expire);
            killed++;
        }
        return TextCommandResult.Success($"Removed {killed} static test entities");
    }

    private TextCommandResult OnCmdSerpentAnim(TextCommandCallingArgs args)
    {
        string animName = args.Parsers[0].GetValue() as string;
        if (string.IsNullOrEmpty(animName))
            return TextCommandResult.Error("Usage: /uh serpent anim <idle|idle2|walk|walk1|pose|off>");

        IServerPlayer caller = args.Caller.Player as IServerPlayer;
        if (caller?.Entity == null)
            return TextCommandResult.Error("No player entity");

        // Find the nearest serpent
        Entity nearest = null;
        double nearestDist = double.MaxValue;
        foreach (Entity e in sapi.World.LoadedEntities.Values)
        {
            if (e == null || !e.Alive) continue;
            if (e.Code?.Path != "seaserpent") continue;
            double dist = e.Pos.DistanceTo(caller.Entity.Pos.XYZ);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = e;
            }
        }

        if (nearest == null)
            return TextCommandResult.Error("No living serpent found. Spawn one with /uh spawn serpent");

        var behavior = nearest.GetBehavior<EntityBehaviorSerpentAI>();
        if (behavior == null)
            return TextCommandResult.Error("Serpent has no SerpentAI behavior");

        if (animName == "off")
        {
            behavior.SetDebugAnimation(null);
            return TextCommandResult.Success($"Serpent debug anim OFF — AI resumed (dist: {nearestDist:F0})");
        }

        behavior.SetDebugAnimation(animName);
        return TextCommandResult.Success(
            $"Serpent frozen, looping '{animName}' every {EntityBehaviorSerpentAI.DebugAnimIntervalPublic:F0}s (dist: {nearestDist:F0})");
    }

    private UnderwaterHorrorsConfig LoadConfig()
    {
        UnderwaterHorrorsConfig config;

        try
        {
            config = sapi.LoadModConfig<UnderwaterHorrorsConfig>("UnderwaterHorrorsConfig.json");
        }
        catch
        {
            config = null;
        }

        if (config == null)
        {
            config = new UnderwaterHorrorsConfig();
            sapi.StoreModConfig(config, "UnderwaterHorrorsConfig.json");
            Mod.Logger.Notification("Created default UnderwaterHorrors config.");
        }

        // One-shot migration: earlier mod versions shipped with
        // DebugLogging=true by default and that flag spams chat for everyone
        // on the server. The first time we load a config that hasn't been
        // through this migration, force the flag off and mark the migration
        // applied. Subsequent loads (and any /uh debug on or hand-edit) are
        // left alone so debug mode is still reachable when wanted.
        if (!config.DebugLoggingResetApplied)
        {
            config.DebugLogging = false;
            config.DebugLoggingResetApplied = true;
            sapi.StoreModConfig(config, "UnderwaterHorrorsConfig.json");
        }
        return config;
    }

    private void OnSpawnCheck(float dt)
    {
        if (!naturalSpawningEnabled) return;
        if (sapi?.World?.AllOnlinePlayers == null) return;

        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
        {
            if (player?.Entity == null || !player.Entity.Alive) continue;

            // If player already has a tracked creature, normally skip.
            // But roll SecondCreatureSpawnChance for a bonus untracked
            // spawn — gives rare "two threats at once" encounters.
            bool spawnAsExtra = false;
            if (activeCreatures.TryGetValue(player.PlayerUID, out long existingId))
            {
                Entity existing = sapi.World.GetEntityById(existingId);
                if (existing != null && existing.Alive)
                {
                    if (sapi.World.Rand.NextDouble() < Config.SecondCreatureSpawnChance)
                    {
                        spawnAsExtra = true;
                        if (Config.DebugLogging)
                            DebugLog(sapi, $"Bonus second-creature roll succeeded for {player.PlayerName}");
                    }
                    else
                    {
                        continue;
                    }
                }
                else
                {
                    // Creature is dead or gone, remove tracking and fall
                    // through to a normal spawn check.
                    activeCreatures.Remove(player.PlayerUID);
                    landTimers.Remove(existingId);
                    if (Config.DebugLogging)
                        DebugLog(sapi, $"Previous creature for {player.PlayerName} is dead or gone, clearing tracking");
                }
            }

            // NOTE: mounted players (on boat/raft) no longer skipped —
            // CountSaltwaterDepth scans down to find the water surface
            // when mounted, so spawn can still fire.  The spawned
            // serpent will circle harmlessly around the boat.

            // Count saltwater depth below player (early-exits once threshold is met)
            int depth = CountSaltwaterDepth(player.Entity, Config.MinSaltwaterDepth);

            if (Config.DebugLogging && depth > 0)
            {
                DebugLog(sapi, $"Spawn check: {player.PlayerName} at ({player.Entity.Pos.X:F0}, {player.Entity.Pos.Y:F0}, {player.Entity.Pos.Z:F0}) - saltwater depth: {depth} (need {Config.MinSaltwaterDepth})");
            }

            if (depth < Config.MinSaltwaterDepth) continue;

            // Primary spawns still need to pass the per-check dice.
            // Second-creature spawns already rolled their own rare
            // dice above, so they bypass this gate.
            if (!spawnAsExtra)
            {
                double roll = sapi.World.Rand.NextDouble();
                if (roll > Config.SpawnChancePerCheck)
                {
                    if (Config.DebugLogging)
                        DebugLog(sapi, $"Spawn attempt for {player.PlayerName}: roll {roll:F3} missed (needed {Config.SpawnChancePerCheck:F3} or less)");
                    continue;
                }

                if (Config.DebugLogging)
                    DebugLog(sapi, $"Spawn attempt for {player.PlayerName}: roll {roll:F3} succeeded (threshold {Config.SpawnChancePerCheck:F3})");
            }

            // Decide creature type. SerpentSpawnWeight=0.75 means 75% of
            // the time we spawn a serpent, 25% a kraken.
            bool spawnSerpent = sapi.World.Rand.NextDouble() < Config.SerpentSpawnWeight;

            Entity creature;
            if (spawnSerpent)
            {
                creature = SpawnSerpent(player);
            }
            else
            {
                creature = SpawnKraken(player);
            }

            if (creature != null && !spawnAsExtra)
            {
                // Only track the primary creature.  Extras manage their
                // own lifecycle via the AI state machine.
                activeCreatures[player.PlayerUID] = creature.EntityId;
            }
        }
    }

    // How far above the water surface we're willing to look when finding
    // water for mounted players (boats sit 1-2 blocks above surface).
    private const int MountedWaterScanDownLimit = 5;

    private int CountSaltwaterDepth(Entity playerEntity, int earlyExitThreshold)
    {
        if (playerEntity?.Pos == null) return 0;

        var accessor = sapi?.World?.BlockAccessor;
        if (accessor == null) return 0;

        int mapHeight = accessor.MapSizeY;
        int startX = (int)playerEntity.Pos.X;
        int startY = (int)playerEntity.Pos.Y;
        int startZ = (int)playerEntity.Pos.Z;
        int dim = playerEntity.Pos.Dimension;

        // Reuse a single BlockPos to avoid allocation per call
        reusableBlockPos.Set(startX, startY, startZ);
        reusableBlockPos.dimension = dim;

        // For mounted players (on a boat), their block is AIR.  Scan
        // down a few blocks to find the water surface first.
        int scanY = startY;
        if (playerEntity is EntityAgent agent && agent.MountedOn != null)
        {
            int scanLimit = Math.Max(0, startY - MountedWaterScanDownLimit);
            while (scanY > scanLimit)
            {
                reusableBlockPos.Y = scanY;
                Block block = accessor.GetBlock(reusableBlockPos);
                if (block != null && WaterHelper.IsSaltwater(block)) break;
                scanY--;
            }
            // If we didn't find water, scanY will be at the limit with
            // a non-water block — count will be 0 naturally.
        }

        int count = 0;

        // Count saltwater below (including the water-surface block)
        for (int y = scanY; y >= 0; y--)
        {
            reusableBlockPos.Y = y;
            Block block = accessor.GetBlock(reusableBlockPos);
            if (block == null || !WaterHelper.IsSaltwater(block)) break;
            count++;
            if (count >= earlyExitThreshold) return count;
        }

        // Count saltwater above (useful when scanning from an in-water
        // position — for mounted players scanY is already the surface
        // so this loop won't find anything above).
        for (int y = scanY + 1; y < mapHeight; y++)
        {
            reusableBlockPos.Y = y;
            Block block = accessor.GetBlock(reusableBlockPos);
            if (block == null || !WaterHelper.IsSaltwater(block)) break;
            count++;
            if (count >= earlyExitThreshold) return count;
        }

        return count;
    }

    /// <summary>
    /// Returns true if the player is directly above saltwater within a
    /// short scan distance.  Used by the despawn check so mounted
    /// players hovering over deep water (on a boat) don't falsely
    /// trigger the "out of water" land timer.
    /// </summary>
    private bool PlayerHasSaltwaterBelow(Entity playerEntity, int scanBlocks)
    {
        if (playerEntity?.Pos == null) return false;
        var accessor = sapi?.World?.BlockAccessor;
        if (accessor == null) return false;

        int startX = (int)playerEntity.Pos.X;
        int startY = (int)playerEntity.Pos.Y;
        int startZ = (int)playerEntity.Pos.Z;
        int dim = playerEntity.Pos.Dimension;

        reusableBlockPos.Set(startX, startY, startZ);
        reusableBlockPos.dimension = dim;

        int limit = Math.Max(0, startY - scanBlocks);
        for (int y = startY; y >= limit; y--)
        {
            reusableBlockPos.Y = y;
            Block block = accessor.GetBlock(reusableBlockPos);
            if (block != null && WaterHelper.IsSaltwater(block)) return true;
        }
        return false;
    }

    // Cached AssetLocations to avoid repeated allocations
    private static readonly AssetLocation SerpentAsset = new AssetLocation("underwaterhorrors", "seaserpent");
    private static readonly AssetLocation DeepSerpentAsset = new AssetLocation("underwaterhorrors", "seaserpent2");
    private static readonly AssetLocation Serpent3Asset = new AssetLocation("underwaterhorrors", "seaserpent3");
    private static readonly AssetLocation KrakenAsset = new AssetLocation("underwaterhorrors", "krakenbody");

    private Entity SpawnSerpent(IServerPlayer player, bool? forceDeep = null)
    {
        // Decide serpent variant: normally rolled via DeepSerpentSpawnWeight,
        // but forceDeep overrides (used by /uh spawn deepserpent).
        bool deep = forceDeep ?? (sapi.World.Rand.NextDouble() < Config.DeepSerpentSpawnWeight);
        AssetLocation asset = deep ? DeepSerpentAsset : SerpentAsset;
        string label = deep ? "Deep Sea Serpent" : "Sea Serpent";

        EntityProperties props = sapi.World.GetEntityType(asset);
        if (props == null)
        {
            DebugLog(sapi, $"ERROR: Could not find entity type {asset}");
            return null;
        }

        var rand = sapi.World.Rand;
        int depthOffset = Config.SerpentSpawnDepthMin + rand.Next(Config.SerpentSpawnDepthMax - Config.SerpentSpawnDepthMin);

        // Retry horizontal position until we find one where the chosen
        // spawn Y is inside a water block (no spawning into terrain when
        // the random offset lands over a shore or shallow bottom).
        double spawnX = 0, spawnZ = 0;
        double spawnY = player.Entity.Pos.Y - depthOffset;
        bool found = false;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            double angle = rand.NextDouble() * Math.PI * 2;
            // Uniform disk distribution: sqrt() avoids clustering near center.
            double dist = Math.Sqrt(rand.NextDouble()) * Config.SerpentSpawnHorizontalRadiusMax;
            double tryX = player.Entity.Pos.X + Math.Cos(angle) * dist;
            double tryZ = player.Entity.Pos.Z + Math.Sin(angle) * dist;

            reusableBlockPos.Set((int)tryX, (int)spawnY, (int)tryZ);
            reusableBlockPos.dimension = player.Entity.Pos.Dimension;
            Block block = sapi.World.BlockAccessor.GetBlock(reusableBlockPos);
            if (block != null && WaterHelper.IsWaterBlock(block))
            {
                spawnX = tryX;
                spawnZ = tryZ;
                found = true;
                break;
            }
        }

        if (!found)
        {
            if (Config.DebugLogging)
                DebugLog(sapi, $"Failed to find valid {label} spawn (no water within {Config.SerpentSpawnHorizontalRadiusMax} blocks of {player.PlayerName} at depth {depthOffset})");
            return null;
        }

        Entity serpent = sapi.World.ClassRegistry.CreateEntity(props);

        serpent.Pos.SetPos(spawnX, spawnY, spawnZ);
        serpent.Pos.Dimension = player.Entity.Pos.Dimension;
        serpent.Pos.SetFrom(serpent.Pos);
        serpent.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", player.PlayerUID);
        sapi.World.SpawnEntity(serpent);

        if (Config.DebugLogging)
            DebugLog(sapi, $"SPAWNED {label} targeting {player.PlayerName} at ({spawnX:F1}, {spawnY:F1}, {spawnZ:F1}), {depthOffset} blocks below player");

        return serpent;
    }

    /// <summary>
    /// Prototype spawn for the new "serpent3" model. Spawns 4 blocks in front
    /// of the player at eye level so the model can be visually inspected
    /// immediately, then hands off to DeepSerpentAI like the deep serpent.
    /// AI will pull it underwater on its own — until then the player can see
    /// the rotation/scale/textures up close.
    /// </summary>
    private Entity SpawnSerpent3(IServerPlayer player)
    {
        EntityProperties props = sapi.World.GetEntityType(Serpent3Asset);
        if (props == null)
        {
            DebugLog(sapi, $"ERROR: Could not find entity type {Serpent3Asset}");
            return null;
        }

        float yaw = player.Entity.Pos.Yaw;
        const double dist = 4.0;
        double spawnX = player.Entity.Pos.X + Math.Sin(yaw) * dist;
        double spawnZ = player.Entity.Pos.Z + Math.Cos(yaw) * dist;
        double spawnY = player.Entity.Pos.Y;

        Entity serpent = sapi.World.ClassRegistry.CreateEntity(props);
        serpent.Pos.SetPos(spawnX, spawnY, spawnZ);
        serpent.Pos.Dimension = player.Entity.Pos.Dimension;
        serpent.Pos.SetFrom(serpent.Pos);
        serpent.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", player.PlayerUID);
        sapi.World.SpawnEntity(serpent);

        if (Config.DebugLogging)
            DebugLog(sapi, $"SPAWNED Serpent3 prototype targeting {player.PlayerName} at ({spawnX:F1}, {spawnY:F1}, {spawnZ:F1})");

        return serpent;
    }

    private Entity SpawnKraken(IServerPlayer player)
    {
        EntityProperties props = sapi.World.GetEntityType(KrakenAsset);
        if (props == null)
        {
            DebugLog(sapi, "ERROR: Could not find entity type underwaterhorrors:krakenbody");
            return null;
        }

        // Retry horizontal position until we find one that sits over a
        // genuine water column with a sea floor (avoids spawning kraken
        // inside a shoreline hill when the random offset lands on land).
        var rand = sapi.World.Rand;
        int startY = (int)player.Entity.Pos.Y;
        double spawnX = 0, spawnZ = 0;
        int floorY = startY;
        bool found = false;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            double angle = rand.NextDouble() * Math.PI * 2;
            double dist = Math.Sqrt(rand.NextDouble()) * Config.KrakenSpawnHorizontalRadiusMax;
            double tryX = player.Entity.Pos.X + Math.Cos(angle) * dist;
            double tryZ = player.Entity.Pos.Z + Math.Sin(angle) * dist;

            // Scan down from player Y through air+water until we hit solid ground.
            bool foundWater = false;
            int tryFloorY = startY;
            reusableBlockPos.Set((int)tryX, startY, (int)tryZ);
            reusableBlockPos.dimension = player.Entity.Pos.Dimension;

            for (int y = startY; y >= 0; y--)
            {
                reusableBlockPos.Y = y;
                Block block = sapi.World.BlockAccessor.GetBlock(reusableBlockPos);
                if (block == null) break;
                bool isWater = WaterHelper.IsWaterBlock(block);

                if (isWater)
                {
                    foundWater = true;
                }
                else if (foundWater)
                {
                    // Hit solid ground after passing through water — this is the sea floor
                    tryFloorY = y + 1;
                    break;
                }
                // Skip air/non-water blocks above the water surface
            }

            if (foundWater)
            {
                spawnX = tryX;
                spawnZ = tryZ;
                floorY = tryFloorY;
                found = true;
                break;
            }
        }

        if (!found)
        {
            if (Config.DebugLogging)
                DebugLog(sapi, $"Failed to find valid Kraken spawn (no water column within {Config.KrakenSpawnHorizontalRadiusMax} blocks of {player.PlayerName})");
            return null;
        }

        Entity kraken = sapi.World.ClassRegistry.CreateEntity(props);
        kraken.Pos.SetPos(spawnX, floorY, spawnZ);
        kraken.Pos.Dimension = player.Entity.Pos.Dimension;
        kraken.Pos.SetFrom(kraken.Pos);
        kraken.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", player.PlayerUID);

        // Day vs night kraken: night-spawned krakens get a bioluminescent
        // flag that the client renderer reads to draw a pulsing cyan glow.
        // The flag propagates from the body to its tentacles in
        // EntityBehaviorKrakenBody, then to chain segments in
        // TentacleSegmentChain.EnsureSpawned, so a single check at spawn
        // time fixes the visual identity for the kraken's whole life.
        double hour = sapi.World.Calendar.HourOfDay;
        bool isDay = hour >= Config.DayKrakenStartHour && hour < Config.DayKrakenEndHour;
        if (!isDay)
        {
            kraken.WatchedAttributes.SetBool("underwaterhorrors:bioluminescent", true);
        }

        sapi.World.SpawnEntity(kraken);

        if (Config.DebugLogging)
            DebugLog(sapi, $"SPAWNED Kraken ({(isDay ? "day" : "NIGHT-bioluminescent")}, hour={hour:F1}) targeting {player.PlayerName} on sea floor at ({spawnX:F1}, {floorY}, {spawnZ:F1})");

        return kraken;
    }

    private void OnDespawnCheck(float dt)
    {
        despawnRemoveList.Clear();

        foreach (var kvp in activeCreatures)
        {
            string playerUid = kvp.Key;
            long entityId = kvp.Value;

            Entity creature = sapi.World.GetEntityById(entityId);
            if (creature == null || !creature.Alive)
            {
                despawnRemoveList.Add(playerUid);
                continue;
            }

            // Direct UID lookup instead of iterating AllOnlinePlayers
            IPlayer player = sapi.World.PlayerByUid(playerUid);

            if (player?.Entity == null)
            {
                despawnRemoveList.Add(playerUid);
                continue;
            }

            // Distance despawn: if the creature has drifted too far from
            // its target player (escaped by boat, or player respawned far
            // away after death), remove it so a new one can spawn naturally.
            double ddx = creature.Pos.X - player.Entity.Pos.X;
            double ddy = creature.Pos.Y - player.Entity.Pos.Y;
            double ddz = creature.Pos.Z - player.Entity.Pos.Z;
            double distSq = ddx * ddx + ddy * ddy + ddz * ddz;
            double maxDist = Config.DespawnMaxDistance;
            if (distSq > maxDist * maxDist)
            {
                if (Config.DebugLogging)
                    DebugLog(sapi, $"DESPAWNING {creature.Code} (id {entityId}): too far from {player.PlayerName} ({Math.Sqrt(distSq):F0} > {maxDist} blocks)");
                creature.Die(EnumDespawnReason.Expire);
                despawnRemoveList.Add(playerUid);
                continue;
            }

            // Check if player's feet are in saltwater using cached block ID check
            BlockPos feetPos = player.Entity.Pos.AsBlockPos;
            Block feetBlock = sapi.World.BlockAccessor.GetBlock(feetPos);
            bool inSaltwater = feetBlock != null && WaterHelper.IsSaltwater(feetBlock);

            // Mounted players (on a boat) have AIR at their feet even
            // over deep water.  Don't despawn if they're hovering over
            // saltwater within a few blocks.
            if (!inSaltwater && player.Entity.MountedOn != null)
            {
                inSaltwater = PlayerHasSaltwaterBelow(player.Entity, MountedWaterScanDownLimit);
            }

            if (!inSaltwater)
            {
                if (!landTimers.ContainsKey(entityId))
                {
                    landTimers[entityId] = 0;
                    if (Config.DebugLogging)
                        DebugLog(sapi, $"Despawn timer started: {player.PlayerName} left saltwater, creature {creature.Code} will despawn in {Config.DespawnAfterLandSeconds}s");
                }
                landTimers[entityId] += Config.DespawnCheckIntervalSeconds;

                if (landTimers[entityId] >= Config.DespawnAfterLandSeconds)
                {
                    if (Config.DebugLogging)
                        DebugLog(sapi, $"DESPAWNING {creature.Code} (id {entityId}): {player.PlayerName} on land for {landTimers[entityId]:F0}s");
                    creature.Die(EnumDespawnReason.Expire);
                    despawnRemoveList.Add(playerUid);
                }
            }
            else
            {
                if (landTimers.ContainsKey(entityId))
                {
                    if (Config.DebugLogging)
                        DebugLog(sapi, $"Despawn timer reset: {player.PlayerName} re-entered saltwater");
                }
                landTimers.Remove(entityId);
            }
        }

        for (int i = 0; i < despawnRemoveList.Count; i++)
        {
            string uid = despawnRemoveList[i];
            if (activeCreatures.TryGetValue(uid, out long eid))
            {
                landTimers.Remove(eid);
            }
            activeCreatures.Remove(uid);
        }
    }
}
