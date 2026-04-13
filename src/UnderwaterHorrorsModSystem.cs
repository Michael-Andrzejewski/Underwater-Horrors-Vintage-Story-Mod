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
    private bool glowActive;

    // playerUID -> entityId of assigned creature
    private Dictionary<string, long> activeCreatures = new();

    // entityId -> seconds target player has been on land
    private Dictionary<long, float> landTimers = new();

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
        api.RegisterEntityBehaviorClass("underwaterhorrors:krakenbody", typeof(EntityBehaviorKrakenBody));
        api.RegisterEntityBehaviorClass("underwaterhorrors:tentacle", typeof(EntityBehaviorTentacle));
        api.RegisterEntityBehaviorClass("underwaterhorrors:ambienttentacle", typeof(EntityBehaviorAmbientTentacle));
        api.RegisterEntityBehaviorClass("underwaterhorrors:tentaclerenderer", typeof(EntityBehaviorTentacleRenderer));

        api.RegisterEntity("EntityBioluminescentLight", typeof(EntityBioluminescentLight));

        api.Network.RegisterChannel("underwaterhorrors")
            .RegisterMessageType(typeof(DebugToggleMessage));
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        capi = api;

        spectralRenderer = new SpectralRenderer(capi);
        capi.Event.RegisterRenderer(spectralRenderer, EnumRenderStage.AfterOIT, "underwaterhorrors-spectral");

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
    }

    // All mod entity type codes to apply glow to
    private static readonly AssetLocation[] ModEntityTypes = new[]
    {
        new AssetLocation("underwaterhorrors", "seaserpent"),
        new AssetLocation("underwaterhorrors", "krakenbody"),
        new AssetLocation("underwaterhorrors", "krakententacle"),
        new AssetLocation("underwaterhorrors", "krakenambienttentacle"),
        new AssetLocation("underwaterhorrors", "krakententacleclaw"),
        new AssetLocation("underwaterhorrors", "krakententsegment"),
        new AssetLocation("underwaterhorrors", "krakententsegment_mid"),
        new AssetLocation("underwaterhorrors", "krakententsegment_outer"),
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

        // Bioluminescence is now fully server-side (light entities), no client sync needed
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
            .BeginSubCommand("spawn")
                .WithDescription("Force spawn a creature on the calling player")
                .WithArgs(api.ChatCommands.Parsers.Word("type", new[] { "serpent", "kraken" }))
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

    private TextCommandResult OnCmdSpawn(TextCommandCallingArgs args)
    {
        string type = args.Parsers[0].GetValue() as string;
        IServerPlayer caller = args.Caller.Player as IServerPlayer;
        if (caller == null) return TextCommandResult.Error("Must be called by a player");

        Entity creature;
        if (type == "serpent")
        {
            creature = SpawnSerpent(caller);
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
            double dist = e.ServerPos.DistanceTo(caller.Entity.ServerPos.XYZ);
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
        return config;
    }

    private void OnSpawnCheck(float dt)
    {
        if (sapi?.World?.AllOnlinePlayers == null) return;

        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
        {
            if (player?.Entity == null || !player.Entity.Alive) continue;

            // Skip if player already has an active creature
            if (activeCreatures.TryGetValue(player.PlayerUID, out long existingId))
            {
                Entity existing = sapi.World.GetEntityById(existingId);
                if (existing != null && existing.Alive)
                {
                    continue;
                }
                // Creature is dead or gone, remove tracking
                activeCreatures.Remove(player.PlayerUID);
                landTimers.Remove(existingId);
                if (Config.DebugLogging)
                    DebugLog(sapi, $"Previous creature for {player.PlayerName} is dead or gone, clearing tracking");
            }

            // Skip if player is mounted (boat/raft)
            if (player.Entity.MountedOn != null)
            {
                if (Config.DebugLogging)
                    DebugLog(sapi, $"Spawn check: {player.PlayerName} is mounted, skipping");
                continue;
            }

            // Count saltwater depth below player (early-exits once threshold is met)
            int depth = CountSaltwaterDepth(player.Entity, Config.MinSaltwaterDepth);

            if (Config.DebugLogging && depth > 0)
            {
                DebugLog(sapi, $"Spawn check: {player.PlayerName} at ({player.Entity.SidedPos.X:F0}, {player.Entity.SidedPos.Y:F0}, {player.Entity.SidedPos.Z:F0}) - saltwater depth: {depth} (need {Config.MinSaltwaterDepth})");
            }

            if (depth < Config.MinSaltwaterDepth) continue;

            // Random chance check
            double roll = sapi.World.Rand.NextDouble();
            if (roll > Config.SpawnChancePerCheck)
            {
                if (Config.DebugLogging)
                    DebugLog(sapi, $"Spawn attempt for {player.PlayerName}: roll {roll:F3} missed (needed {Config.SpawnChancePerCheck:F3} or less)");
                continue;
            }

            if (Config.DebugLogging)
                DebugLog(sapi, $"Spawn attempt for {player.PlayerName}: roll {roll:F3} succeeded (threshold {Config.SpawnChancePerCheck:F3})");

            // Decide creature type
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

            if (creature != null)
            {
                activeCreatures[player.PlayerUID] = creature.EntityId;
            }
        }
    }

    private int CountSaltwaterDepth(Entity playerEntity, int earlyExitThreshold)
    {
        if (playerEntity?.SidedPos == null) return 0;

        var accessor = sapi?.World?.BlockAccessor;
        if (accessor == null) return 0;

        int mapHeight = accessor.MapSizeY;
        int startX = (int)playerEntity.SidedPos.X;
        int startY = (int)playerEntity.SidedPos.Y;
        int startZ = (int)playerEntity.SidedPos.Z;
        int dim = playerEntity.SidedPos.Dimension;
        int count = 0;

        // Reuse a single BlockPos to avoid allocation per call
        reusableBlockPos.Set(startX, startY, startZ);
        reusableBlockPos.dimension = dim;

        // Count saltwater below (including player's block)
        for (int y = startY; y >= 0; y--)
        {
            reusableBlockPos.Y = y;
            Block block = accessor.GetBlock(reusableBlockPos);
            if (block == null || !WaterHelper.IsSaltwater(block)) break;
            count++;
            if (count >= earlyExitThreshold) return count;
        }

        // Count saltwater above
        for (int y = startY + 1; y < mapHeight; y++)
        {
            reusableBlockPos.Y = y;
            Block block = accessor.GetBlock(reusableBlockPos);
            if (block == null || !WaterHelper.IsSaltwater(block)) break;
            count++;
            if (count >= earlyExitThreshold) return count;
        }

        return count;
    }

    // Cached AssetLocations to avoid repeated allocations
    private static readonly AssetLocation SerpentAsset = new AssetLocation("underwaterhorrors", "seaserpent");
    private static readonly AssetLocation KrakenAsset = new AssetLocation("underwaterhorrors", "krakenbody");

    private Entity SpawnSerpent(IServerPlayer player)
    {
        EntityProperties props = sapi.World.GetEntityType(SerpentAsset);
        if (props == null)
        {
            DebugLog(sapi, "ERROR: Could not find entity type underwaterhorrors:seaserpent");
            return null;
        }

        var rand = sapi.World.Rand;
        int depthOffset = Config.SerpentSpawnDepthMin + rand.Next(Config.SerpentSpawnDepthMax - Config.SerpentSpawnDepthMin);
        double angle = rand.NextDouble() * Math.PI * 2;
        double offsetX = Math.Cos(angle) * Config.SerpentSpawnHorizontalOffset;
        double offsetZ = Math.Sin(angle) * Config.SerpentSpawnHorizontalOffset;

        Entity serpent = sapi.World.ClassRegistry.CreateEntity(props);
        double spawnX = player.Entity.SidedPos.X + offsetX;
        double spawnY = player.Entity.SidedPos.Y - depthOffset;
        double spawnZ = player.Entity.SidedPos.Z + offsetZ;

        serpent.ServerPos.SetPos(spawnX, spawnY, spawnZ);
        serpent.ServerPos.Dimension = player.Entity.SidedPos.Dimension;
        serpent.Pos.SetFrom(serpent.ServerPos);
        serpent.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", player.PlayerUID);
        sapi.World.SpawnEntity(serpent);

        if (Config.DebugLogging)
            DebugLog(sapi, $"SPAWNED Sea Serpent targeting {player.PlayerName} at ({spawnX:F1}, {spawnY:F1}, {spawnZ:F1}), {depthOffset} blocks below player");

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

        // Find sea floor directly below player — scan through air, then water, until solid ground
        int startY = (int)player.Entity.SidedPos.Y;
        int floorY = startY;
        bool foundWater = false;
        reusableBlockPos.Set((int)player.Entity.SidedPos.X, startY, (int)player.Entity.SidedPos.Z);
        reusableBlockPos.dimension = player.Entity.SidedPos.Dimension;

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
                floorY = y + 1;
                break;
            }
            // Skip air/non-water blocks above the water surface
        }

        Entity kraken = sapi.World.ClassRegistry.CreateEntity(props);
        kraken.ServerPos.SetPos(player.Entity.SidedPos.X, floorY, player.Entity.SidedPos.Z);
        kraken.ServerPos.Dimension = player.Entity.SidedPos.Dimension;
        kraken.Pos.SetFrom(kraken.ServerPos);
        kraken.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", player.PlayerUID);
        sapi.World.SpawnEntity(kraken);

        if (Config.DebugLogging)
            DebugLog(sapi, $"SPAWNED Kraken targeting {player.PlayerName} on sea floor at ({player.Entity.SidedPos.X:F1}, {floorY}, {player.Entity.SidedPos.Z:F1})");

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

            // Check if player's feet are in saltwater using cached block ID check
            BlockPos feetPos = player.Entity.SidedPos.AsBlockPos;
            Block feetBlock = sapi.World.BlockAccessor.GetBlock(feetPos);
            bool inSaltwater = feetBlock != null && WaterHelper.IsSaltwater(feetBlock);

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
