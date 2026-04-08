using System;
using System.Collections.Generic;
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

    // playerUID -> entityId of assigned creature
    private Dictionary<string, long> activeCreatures = new();

    // entityId -> seconds target player has been on land
    private Dictionary<long, float> landTimers = new();

    // Reusable BlockPos to avoid per-call allocation in hot paths
    private readonly BlockPos reusableBlockPos = new BlockPos(0, 0, 0, 0);

    // Block ID caches: avoids repeated string comparisons on block codes.
    // Populated lazily — each unique block ID is string-checked once, then cached.
    private readonly HashSet<int> saltwaterBlockIds = new();
    private readonly HashSet<int> nonSaltwaterBlockIds = new();

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

        RegisterCommands(api);
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
            .EndSubCommand();
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
        else
        {
            sapi.StoreModConfig(config, "UnderwaterHorrorsConfig.json");
        }

        return config;
    }

    private void OnSpawnCheck(float dt)
    {
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
        {
            if (player.Entity == null || !player.Entity.Alive) continue;

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
        var accessor = sapi.World.BlockAccessor;
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
            if (block == null || !IsSaltwater(block)) break;
            count++;
            if (count >= earlyExitThreshold) return count;
        }

        // Count saltwater above
        for (int y = startY + 1; y < mapHeight; y++)
        {
            reusableBlockPos.Y = y;
            Block block = accessor.GetBlock(reusableBlockPos);
            if (block == null || !IsSaltwater(block)) break;
            count++;
            if (count >= earlyExitThreshold) return count;
        }

        return count;
    }

    /// <summary>
    /// Checks if a block is saltwater using cached block ID lookups when possible,
    /// falling back to string comparison only on first encounter.
    /// </summary>
    private bool IsSaltwater(Block block)
    {
        int id = block.Id;
        if (id == 0) return false;

        // Check positive cache first
        if (saltwaterBlockIds.Contains(id)) return true;
        // Check negative cache
        if (nonSaltwaterBlockIds.Contains(id)) return false;

        // First encounter with this block ID — do the string check once and cache
        string path = block.Code?.Path;
        if (path != null && path.StartsWith("saltwater"))
        {
            saltwaterBlockIds.Add(id);
            return true;
        }
        else
        {
            nonSaltwaterBlockIds.Add(id);
            return false;
        }
    }

    // Cached water block ID sets for IsWaterBlock
    private readonly HashSet<int> waterBlockIds = new();
    private readonly HashSet<int> nonWaterBlockIds = new();

    /// <summary>
    /// Checks if a block is any kind of water (salt or fresh) using cached ID lookups.
    /// </summary>
    private bool IsWaterBlock(Block block)
    {
        int id = block.Id;
        if (id == 0) return false;

        if (waterBlockIds.Contains(id)) return true;
        if (nonWaterBlockIds.Contains(id)) return false;

        string path = block.Code?.Path;
        if (path != null && (path.StartsWith("saltwater") || path.StartsWith("water")))
        {
            waterBlockIds.Add(id);
            return true;
        }
        else
        {
            nonWaterBlockIds.Add(id);
            return false;
        }
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
            bool isWater = IsWaterBlock(block);

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
            bool inSaltwater = feetBlock != null && IsSaltwater(feetBlock);

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
