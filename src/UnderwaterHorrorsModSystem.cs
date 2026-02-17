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
        int count = 0;
        List<Entity> toRemove = new();

        foreach (Entity entity in sapi.World.LoadedEntities.Values)
        {
            if (entity == null || !entity.Alive) continue;
            string code = entity.Code?.Domain ?? "";
            if (code == "underwaterhorrors")
            {
                toRemove.Add(entity);
            }
        }

        foreach (Entity entity in toRemove)
        {
            entity.Die(EnumDespawnReason.Expire);
            count++;
        }

        activeCreatures.Clear();
        landTimers.Clear();

        return TextCommandResult.Success($"Removed {count} Underwater Horrors entities");
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
                DebugLog(sapi, $"Previous creature for {player.PlayerName} is dead or gone, clearing tracking");
            }

            // Skip if player is mounted (boat/raft)
            if (player.Entity.MountedOn != null)
            {
                DebugLog(sapi, $"Spawn check: {player.PlayerName} is mounted, skipping");
                continue;
            }

            // Count saltwater depth below player
            int depth = CountSaltwaterDepth(player.Entity);

            if (depth > 0)
            {
                DebugLog(sapi, $"Spawn check: {player.PlayerName} at ({player.Entity.SidedPos.X:F0}, {player.Entity.SidedPos.Y:F0}, {player.Entity.SidedPos.Z:F0}) - saltwater depth: {depth} (need {Config.MinSaltwaterDepth})");
            }

            if (depth < Config.MinSaltwaterDepth) continue;

            // Random chance check
            double roll = sapi.World.Rand.NextDouble();
            if (roll > Config.SpawnChancePerCheck)
            {
                DebugLog(sapi, $"Spawn attempt for {player.PlayerName}: roll {roll:F3} missed (needed {Config.SpawnChancePerCheck:F3} or less)");
                continue;
            }

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

    private int CountSaltwaterDepth(Entity playerEntity)
    {
        BlockPos pos = playerEntity.SidedPos.AsBlockPos.Copy();
        int count = 0;
        bool foundSaltwater = false;

        for (int y = pos.Y; y >= 0; y--)
        {
            pos.Y = y;
            Block block = sapi.World.BlockAccessor.GetBlock(pos);
            if (block == null) break;

            string code = block.Code?.Path ?? "";
            if (code.StartsWith("saltwater"))
            {
                count++;
                foundSaltwater = true;
            }
            else if (foundSaltwater)
            {
                // Hit non-saltwater after counting saltwater (reached the sea floor)
                break;
            }
            // Skip air/freshwater blocks above the saltwater column
        }

        return count;
    }

    private Entity SpawnSerpent(IServerPlayer player)
    {
        EntityProperties props = sapi.World.GetEntityType(new AssetLocation("underwaterhorrors", "seaserpent"));
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

        DebugLog(sapi, $"SPAWNED Sea Serpent targeting {player.PlayerName} at ({spawnX:F1}, {spawnY:F1}, {spawnZ:F1}), {depthOffset} blocks below player");

        return serpent;
    }

    private Entity SpawnKraken(IServerPlayer player)
    {
        EntityProperties props = sapi.World.GetEntityType(new AssetLocation("underwaterhorrors", "krakenbody"));
        if (props == null)
        {
            DebugLog(sapi, "ERROR: Could not find entity type underwaterhorrors:krakenbody");
            return null;
        }

        // Find sea floor directly below player
        BlockPos pos = player.Entity.SidedPos.AsBlockPos.Copy();
        int floorY = pos.Y;

        for (int y = pos.Y; y >= 0; y--)
        {
            pos.Y = y;
            Block block = sapi.World.BlockAccessor.GetBlock(pos);
            string code = block?.Code?.Path ?? "";
            if (!code.StartsWith("saltwater") && !code.StartsWith("water"))
            {
                floorY = y + 1;
                break;
            }
        }

        Entity kraken = sapi.World.ClassRegistry.CreateEntity(props);
        kraken.ServerPos.SetPos(player.Entity.SidedPos.X, floorY, player.Entity.SidedPos.Z);
        kraken.ServerPos.Dimension = player.Entity.SidedPos.Dimension;
        kraken.Pos.SetFrom(kraken.ServerPos);
        kraken.WatchedAttributes.SetString("underwaterhorrors:targetPlayerUid", player.PlayerUID);
        sapi.World.SpawnEntity(kraken);

        DebugLog(sapi, $"SPAWNED Kraken targeting {player.PlayerName} on sea floor at ({player.Entity.SidedPos.X:F1}, {floorY}, {player.Entity.SidedPos.Z:F1})");

        return kraken;
    }

    private void OnDespawnCheck(float dt)
    {
        List<string> toRemove = new();

        foreach (var kvp in activeCreatures)
        {
            string playerUid = kvp.Key;
            long entityId = kvp.Value;

            Entity creature = sapi.World.GetEntityById(entityId);
            if (creature == null || !creature.Alive)
            {
                toRemove.Add(playerUid);
                continue;
            }

            // Find the target player
            IPlayer player = null;
            foreach (IPlayer p in sapi.World.AllOnlinePlayers)
            {
                if (p.PlayerUID == playerUid)
                {
                    player = p;
                    break;
                }
            }

            if (player?.Entity == null)
            {
                toRemove.Add(playerUid);
                continue;
            }

            // Check if player's feet are in saltwater
            BlockPos feetPos = player.Entity.SidedPos.AsBlockPos;
            Block feetBlock = sapi.World.BlockAccessor.GetBlock(feetPos);
            bool inSaltwater = feetBlock?.Code?.Path?.StartsWith("saltwater") == true;

            if (!inSaltwater)
            {
                if (!landTimers.ContainsKey(entityId))
                {
                    landTimers[entityId] = 0;
                    DebugLog(sapi, $"Despawn timer started: {player.PlayerName} left saltwater, creature {creature.Code} will despawn in {Config.DespawnAfterLandSeconds}s");
                }
                landTimers[entityId] += Config.DespawnCheckIntervalSeconds;

                if (landTimers[entityId] >= Config.DespawnAfterLandSeconds)
                {
                    DebugLog(sapi, $"DESPAWNING {creature.Code} (id {entityId}): {player.PlayerName} on land for {landTimers[entityId]:F0}s");
                    creature.Die(EnumDespawnReason.Expire);
                    toRemove.Add(playerUid);
                }
            }
            else
            {
                if (landTimers.ContainsKey(entityId))
                {
                    DebugLog(sapi, $"Despawn timer reset: {player.PlayerName} re-entered saltwater");
                }
                landTimers.Remove(entityId);
            }
        }

        foreach (string uid in toRemove)
        {
            if (activeCreatures.TryGetValue(uid, out long eid))
            {
                landTimers.Remove(eid);
            }
            activeCreatures.Remove(uid);
        }
    }
}
