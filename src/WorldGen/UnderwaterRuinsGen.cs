using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace UnderwaterHorrors;

/// <summary>
/// Generates the Building-Commands ruin / portal / shipwreck / city structures
/// naturally, rarely, on deep ocean floors. Each structure is authored as a
/// tilde-relative build script (shipped under assets/underwaterhorrors/
/// ruinscripts/) and placed at the sea floor. Collapsed loot chests are stocked
/// like vanilla lore-location chests (stackrandomizer tokens resolved in place),
/// and each structure rolls for a serpent spawner (50%), with a per-structure 5%
/// chance that every spawner becomes a kraken instead. The huge wreck carries two
/// spawner spots, everything else one.
///
/// The same placement runs from the /uhruin test command using the normal block
/// accessor, so the structures can be inspected on land without hunting an ocean.
/// </summary>
public class UnderwaterRuinsGen : ModSystem
{
    private ICoreServerAPI sapi;
    private IWorldGenBlockAccessor wgba;
    private UnderwaterHorrorsConfig config;
    private int seaLevel;

    // structure name -> parsed script lines
    private readonly Dictionary<string, string[]> scripts = new();
    // block code -> resolved block (0 = air). Cached across the run.
    private readonly Dictionary<string, Block> blockCache = new();

    // Chests and spawners are block entities. Placing them via the worldgen
    // accessor left them empty (its GetBlockEntity returns null mid-gen), so
    // during gen we only RECORD them here and place them for real on the main
    // thread once the containing chunk column loads, using the normal block
    // accessor (which reliably creates the BE, stocks loot, and starts the
    // spawner). The list is persisted so pre-generated-but-never-loaded chunks
    // still get their features when a player finally visits.
    private readonly object pendingLock = new();
    private List<PendingFeature> pending = new();
    private const string PendingDataKey = "underwaterhorrors:pendingruinfeatures";

    [ProtoBuf.ProtoContract]
    private class PendingFeature
    {
        [ProtoBuf.ProtoMember(1)] public int X;
        [ProtoBuf.ProtoMember(2)] public int Y;
        [ProtoBuf.ProtoMember(3)] public int Z;
        [ProtoBuf.ProtoMember(4)] public int Dim;
        [ProtoBuf.ProtoMember(5)] public bool IsChest;
        [ProtoBuf.ProtoMember(6)] public int Variant;
        [ProtoBuf.ProtoMember(7)] public string Side;
        [ProtoBuf.ProtoMember(8)] public bool Kraken;
        [ProtoBuf.ProtoMember(9)] public bool IsIngots;
        [ProtoBuf.ProtoMember(10)] public string Metal;
        [ProtoBuf.ProtoMember(11)] public int Count;
    }

    private static readonly string[] StructureNames =
        { "ruin", "portal", "shipwreck-small", "shipwreck-medium", "city", "shipwreck-huge" };

    // Ruin-appropriate stackrandomizer loot pools, matching Building Commands.
    private static readonly string[] RuinLootTypes =
    {
        "gear", "resource", "ruinedweapon", "coppertool", "ingot", "ore",
        "cloth-lowstatus", "accessory-lowstatus", "lantern", "lore-research",
        "lore-diaries", "tuningcylinder"
    };

    private const int ChunkSize = 32;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        try { config = api.LoadModConfig<UnderwaterHorrorsConfig>("UnderwaterHorrorsConfig.json"); }
        catch { config = null; }
        if (config == null) config = new UnderwaterHorrorsConfig();

        LoadScripts();

        api.ChatCommands.Create("uhruin")
            .WithDescription("Build an Underwater Horrors ruin structure at your feet (test).")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("name"))
            .HandleWith(OnRuinTestCommand);

        if (!config.UnderwaterRuinsEnabled || scripts.Count == 0)
        {
            api.Logger.Notification("[UH ruins] worldgen disabled or no scripts loaded ({0} loaded).", scripts.Count);
            return;
        }

        seaLevel = api.World.SeaLevel;
        api.Event.GetWorldgenBlockAccessor(chunkProvider => wgba = chunkProvider.GetBlockAccessor(false));
        api.Event.ChunkColumnGeneration(OnChunkColumnGen, EnumWorldGenPass.TerrainFeatures, "standard");
        api.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
        api.Event.RegisterGameTickListener(ProcessPendingTick, 3000);
        api.Event.SaveGameLoaded += LoadPending;
        api.Event.GameWorldSave += SavePending;
        api.Logger.Notification("[UH ruins] worldgen active: {0} structures, 1 per ~{1} deep-ocean columns.",
            scripts.Count, config.RuinRarity);
    }

    // ── load the shipped build scripts ────────────────────────────────────
    // Shipped as one JSON bundle (name -> lines) under config/, a real asset
    // category. A custom folder like ruinscripts/ would never be loaded.
    private void LoadScripts()
    {
        var loc = new AssetLocation("underwaterhorrors", "config/ruinscripts.json");
        IAsset asset = sapi.Assets.TryGet(loc);
        if (asset == null)
        {
            sapi.Logger.Warning("[UH ruins] missing script bundle {0}", loc);
            return;
        }
        try
        {
            var bundle = asset.ToObject<Dictionary<string, string[]>>();
            foreach (string name in StructureNames)
                if (bundle.TryGetValue(name, out string[] lines) && lines != null && lines.Length > 0)
                    scripts[name] = lines;
        }
        catch (Exception e)
        {
            sapi.Logger.Error("[UH ruins] failed to parse script bundle: {0}", e.Message);
        }
    }

    // ── worldgen entry: one rare roll per chunk column ────────────────────
    private void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        if (wgba == null || scripts.Count == 0) return;

        int cx = request.ChunkX, cz = request.ChunkZ;
        // deterministic per-column RNG so a given world always places the same
        // ruins in the same spots
        int hash = unchecked((int)(sapi.World.Seed * 8161L ^ cx * 341873128712L ^ cz * 132897987541L));
        var rnd = new Random(hash);

        int rarity = Math.Max(1, config.RuinRarity);
        if (rnd.Next(rarity) != 0) return;

        int wx = cx * ChunkSize + ChunkSize / 2;
        int wz = cz * ChunkSize + ChunkSize / 2;

        if (!FindOceanFloor(wx, wz, out int floorY)) return;

        string name = PickStructure(rnd);
        if (!scripts.TryGetValue(name, out string[] lines)) return;

        wgba.BeginColumn();
        bool krakenMode = rnd.NextDouble() < 0.05;
        var origin = new BlockPos(wx, floorY + 1, wz, 0);
        int placed = PlaceScript(wgba, lines, origin, rnd, krakenMode);
        sapi.Logger.VerboseDebug("[UH ruins] placed {0} at {1},{2},{3} ({4} blocks)", name, wx, floorY + 1, wz, placed);
    }

    // Deep saltwater only: saltwater at sea level, a solid floor far enough
    // below, and saltwater the whole way down.
    private bool FindOceanFloor(int wx, int wz, out int floorY)
    {
        floorY = 0;
        int minDepth = Math.Max(2, config.RuinMinOceanDepth);

        Block atSea = wgba.GetBlock(new BlockPos(wx, seaLevel - 1, wz, 0));
        if (!WaterHelper.IsSaltwater(atSea)) return false;

        for (int y = seaLevel - 1; y > 1; y--)
        {
            Block b = wgba.GetBlock(new BlockPos(wx, y, wz, 0));
            if (b == null || b.Id == 0) return false;          // air gap: not a clean ocean column
            if (WaterHelper.IsWaterBlock(b)) continue;
            floorY = y;                                          // first solid block from the top
            return (seaLevel - floorY) >= minDepth;
        }
        return false;
    }

    // Weighted: small ruins common, the huge wreck and the city rare.
    private static string PickStructure(Random rnd)
    {
        double r = rnd.NextDouble();
        if (r < 0.34) return "ruin";
        if (r < 0.56) return "portal";
        if (r < 0.74) return "shipwreck-small";
        if (r < 0.88) return "shipwreck-medium";
        if (r < 0.96) return "city";
        return "shipwreck-huge";
    }

    // ── script runner (works with worldgen OR normal accessor) ────────────
    private int PlaceScript(IBlockAccessor acc, string[] lines, BlockPos origin, Random rnd, bool krakenMode)
    {
        int placed = 0;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("//")) continue;
            if (line[0] == '/') line = line.Substring(1);

            string[] t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;

            switch (t[0].ToLowerInvariant())
            {
                case "fill": placed += DoFill(acc, t, origin); break;
                case "setblock":
                case "cbsetblock": placed += DoSet(acc, t, origin); break;
                case "lootchest": placed += DoChest(acc, t, origin, rnd); break;
                case "spawner": placed += DoSpawner(acc, t, origin, rnd, krakenMode); break;
                case "ingots": placed += DoIngots(acc, t, origin); break;
            }
        }
        return placed;
    }

    private int DoFill(IBlockAccessor acc, string[] t, BlockPos o)
    {
        if (t.Length < 8) return 0;
        int x1 = Rel(t[1], o.X), y1 = Rel(t[2], o.Y), z1 = Rel(t[3], o.Z);
        int x2 = Rel(t[4], o.X), y2 = Rel(t[5], o.Y), z2 = Rel(t[6], o.Z);
        Block b = ResolveBlock(t[7]);
        int id = b?.BlockId ?? 0;
        if (x1 > x2) (x1, x2) = (x2, x1);
        if (y1 > y2) (y1, y2) = (y2, y1);
        if (z1 > z2) (z1, z2) = (z2, z1);
        int n = 0;
        var p = new BlockPos(0, 0, 0, o.dimension);
        for (int x = x1; x <= x2; x++)
            for (int y = y1; y <= y2; y++)
                for (int z = z1; z <= z2; z++)
                {
                    p.Set(x, y, z);
                    acc.SetBlock(id, p);
                    n++;
                }
        return n;
    }

    private int DoSet(IBlockAccessor acc, string[] t, BlockPos o)
    {
        if (t.Length < 5) return 0;
        Block b = ResolveBlock(t[4]);
        acc.SetBlock(b?.BlockId ?? 0, new BlockPos(Rel(t[1], o.X), Rel(t[2], o.Y), Rel(t[3], o.Z), o.dimension));
        return 1;
    }

    private int DoChest(IBlockAccessor acc, string[] t, BlockPos o, Random rnd)
    {
        if (t.Length < 4) return 0;
        var pos = new BlockPos(Rel(t[1], o.X), Rel(t[2], o.Y), Rel(t[3], o.Z), o.dimension);

        string side = t.Length > 5 ? t[5].ToLowerInvariant() : "north";
        if (side != "north" && side != "east" && side != "south" && side != "west") side = "north";
        int variant = 2;
        if (t.Length > 4 && int.TryParse(t[4], out int v) && v >= 1 && v <= 4) variant = v;

        // During worldgen, defer to the main thread (block entities + loot do
        // not populate on the worldgen accessor). Otherwise place immediately.
        if (acc is IWorldGenBlockAccessor)
        {
            lock (pendingLock)
                pending.Add(new PendingFeature { X = pos.X, Y = pos.Y, Z = pos.Z, Dim = pos.dimension, IsChest = true, Variant = variant, Side = side });
            return 1;
        }
        PlaceChestNow(acc, pos, variant, side, rnd);
        return 1;
    }

    private int DoSpawner(IBlockAccessor acc, string[] t, BlockPos o, Random rnd, bool krakenMode)
    {
        if (t.Length < 4) return 0;

        string typeArg = t.Length > 4 ? t[4].ToLowerInvariant() : "";
        bool kraken;
        if (typeArg == "serpent") kraken = false;
        else if (typeArg == "kraken") kraken = true;
        else
        {
            if (rnd.NextDouble() >= 0.85) return 0;   // 85% chance to place a spawner here
            kraken = krakenMode;
        }

        var pos = new BlockPos(Rel(t[1], o.X), Rel(t[2], o.Y), Rel(t[3], o.Z), o.dimension);

        if (acc is IWorldGenBlockAccessor)
        {
            lock (pendingLock)
                pending.Add(new PendingFeature { X = pos.X, Y = pos.Y, Z = pos.Z, Dim = pos.dimension, IsChest = false, Kraken = kraken });
            return 1;
        }
        return PlaceSpawnerNow(acc, pos, kraken) ? 1 : 0;
    }

    // ingots x y z <metal> <count>
    private int DoIngots(IBlockAccessor acc, string[] t, BlockPos o)
    {
        if (t.Length < 4) return 0;
        var pos = new BlockPos(Rel(t[1], o.X), Rel(t[2], o.Y), Rel(t[3], o.Z), o.dimension);
        string metal = t.Length > 4 ? t[4].ToLowerInvariant() : "copper";
        int count = 8;
        if (t.Length > 5 && int.TryParse(t[5], out int c)) count = Math.Max(1, Math.Min(64, c));

        if (acc is IWorldGenBlockAccessor)
        {
            lock (pendingLock)
                pending.Add(new PendingFeature { X = pos.X, Y = pos.Y, Z = pos.Z, Dim = pos.dimension, IsIngots = true, Metal = metal, Count = count });
            return 1;
        }
        return PlaceIngotsNow(acc, pos, metal, count) ? 1 : 0;
    }

    // ── immediate placement on the main thread (normal accessor) ──────────
    private void PlaceChestNow(IBlockAccessor acc, BlockPos pos, int variant, string side, Random rnd)
    {
        string ctype = "collapsed" + variant;
        Block chest = ResolveBlock("game:chest-" + side);
        if (chest == null) return;

        var stack = new ItemStack(chest);
        stack.Attributes.SetString("type", ctype);
        acc.SetBlock(chest.BlockId, pos, stack);

        BlockEntity be = acc.GetBlockEntity(pos);
        if (be == null) return;

        FieldInfo tf = be.GetType().GetField("type");
        if (tf != null && tf.FieldType == typeof(string)) tf.SetValue(be, ctype);

        if (be is Vintagestory.API.Common.IBlockEntityContainer bec && bec.Inventory != null)
        {
            IInventory inv = bec.Inventory;
            int toFill = Math.Min(inv.Count, 5 + rnd.Next(4));
            for (int i = 0; i < toFill; i++)
            {
                string lootType = RuinLootTypes[rnd.Next(RuinLootTypes.Length)];
                Item randomizer = sapi.World.GetItem(new AssetLocation("game", "stackrandomizer-" + lootType));
                if (randomizer == null) continue;
                ItemSlot slot = inv[i];
                slot.Itemstack = new ItemStack(randomizer, 1);
                if (randomizer is IResolvableCollectible resolvable) resolvable.Resolve(slot, sapi.World);
                slot.MarkDirty();
            }
        }
        be.MarkDirty(true);
    }

    private bool PlaceSpawnerNow(IBlockAccessor acc, BlockPos pos, bool kraken)
    {
        Block spawner = ResolveBlock("underwaterhorrors:" + (kraken ? "krakenspawner" : "serpentspawner"));
        if (spawner == null) return false;
        acc.SetBlock(spawner.BlockId, pos);
        acc.MarkBlockDirty(pos);
        return true;
    }

    // Place a game:ingotpile and stock it with <count> ingots of <metal>. The
    // pile is a block entity, so a plain SetBlock renders nothing; we fill its
    // inventory slot and re-tessellate.
    private bool PlaceIngotsNow(IBlockAccessor acc, BlockPos pos, string metal, int count)
    {
        Item ingot = sapi.World.GetItem(new AssetLocation("game", "ingot-" + metal));
        Block pile = ResolveBlock("game:ingotpile");
        if (ingot == null || pile == null) return false;

        acc.SetBlock(pile.BlockId, pos);
        BlockEntity be = acc.GetBlockEntity(pos);
        IInventory inv = GetBlockEntityInventory(be);
        if (inv != null && inv.Count > 0)
        {
            inv[0].Itemstack = new ItemStack(ingot, count);
            inv[0].MarkDirty();
        }
        be?.MarkDirty(true);
        acc.MarkBlockDirty(pos);
        return true;
    }

    // Item piles expose their contents as an "inventory" field rather than
    // IBlockEntityContainer, so fall back to reflection.
    private static IInventory GetBlockEntityInventory(BlockEntity be)
    {
        if (be == null) return null;
        if (be is Vintagestory.API.Common.IBlockEntityContainer c && c.Inventory != null) return c.Inventory;
        for (Type t = be.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField("inventory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null && typeof(IInventory).IsAssignableFrom(f.FieldType)) return f.GetValue(be) as IInventory;
        }
        return null;
    }

    // ── place deferred features once their chunk is loaded ────────────────
    // Two triggers: ChunkColumnLoaded (immediate, right after a column loads)
    // and a slow tick fallback (catches anything the event missed, e.g. chunks
    // pre-generated in a batch). Both funnel through PlacePending, which removes
    // under lock so a feature is never placed twice.
    private void OnChunkColumnLoaded(Vec2i chunkCoord, IWorldChunk[] chunks)
    {
        PlacePending(f => (f.X >> 5) == chunkCoord.X && (f.Z >> 5) == chunkCoord.Y);
    }

    private void ProcessPendingTick(float dt)
    {
        bool any;
        lock (pendingLock) any = pending.Count > 0;
        if (!any) return;
        IBlockAccessor ba = sapi.World.BlockAccessor;
        PlacePending(f => ba.GetChunk(f.X >> 5, f.Y >> 5, f.Z >> 5) != null);
    }

    private void PlacePending(System.Func<PendingFeature, bool> match)
    {
        List<PendingFeature> toPlace = null;
        lock (pendingLock)
        {
            if (pending.Count == 0) return;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (match(pending[i]))
                {
                    (toPlace ??= new List<PendingFeature>()).Add(pending[i]);
                    pending.RemoveAt(i);
                }
            }
        }
        if (toPlace == null) return;

        IBlockAccessor acc = sapi.World.BlockAccessor;
        var rnd = new Random();
        foreach (PendingFeature f in toPlace)
        {
            var pos = new BlockPos(f.X, f.Y, f.Z, f.Dim);
            if (f.IsChest) PlaceChestNow(acc, pos, f.Variant, f.Side, rnd);
            else if (f.IsIngots) PlaceIngotsNow(acc, pos, f.Metal, f.Count);
            else PlaceSpawnerNow(acc, pos, f.Kraken);
        }
    }

    private void LoadPending()
    {
        try
        {
            byte[] data = sapi.WorldManager.SaveGame.GetData(PendingDataKey);
            if (data != null && data.Length > 0)
            {
                var loaded = Vintagestory.API.Util.SerializerUtil.Deserialize<List<PendingFeature>>(data);
                if (loaded != null) lock (pendingLock) pending = loaded;
            }
        }
        catch (Exception e) { sapi.Logger.Warning("[UH ruins] could not load pending features: {0}", e.Message); }
    }

    private void SavePending()
    {
        try
        {
            byte[] data;
            lock (pendingLock) data = Vintagestory.API.Util.SerializerUtil.Serialize(pending);
            sapi.WorldManager.SaveGame.StoreData(PendingDataKey, data);
        }
        catch (Exception e) { sapi.Logger.Warning("[UH ruins] could not save pending features: {0}", e.Message); }
    }

    // ~ -> base, ~n -> base + n, plain n -> absolute
    private static int Rel(string tok, int baseCoord)
    {
        if (tok.Length == 0) return baseCoord;
        if (tok[0] == '~')
        {
            string rest = tok.Substring(1);
            if (rest.Length == 0) return baseCoord;
            return int.TryParse(rest, out int off) ? baseCoord + off : baseCoord;
        }
        return int.TryParse(tok, out int abs) ? abs : baseCoord;
    }

    private Block ResolveBlock(string code)
    {
        if (code == "air" || code == "game:air") return null;
        if (blockCache.TryGetValue(code, out Block cached)) return cached;
        Block b = sapi.World.GetBlock(new AssetLocation(code));
        blockCache[code] = b;
        return b;
    }

    // ── /uhruin test command (normal accessor, at the player) ─────────────
    private TextCommandResult OnRuinTestCommand(TextCommandCallingArgs args)
    {
        string name = args.Parsers[0].GetValue() as string;
        if (string.IsNullOrEmpty(name))
            return TextCommandResult.Success("Structures: " + string.Join(", ", StructureNames) + ". Use /uhruin &lt;name&gt;.");
        name = name.ToLowerInvariant();
        if (!scripts.TryGetValue(name, out string[] lines))
            return TextCommandResult.Error($"Unknown structure '{name}'. One of: {string.Join(", ", StructureNames)}.");

        var ent = args.Caller.Entity;
        if (ent == null) return TextCommandResult.Error("Run this in-game so it can build at your feet.");

        BlockPos feet = ent.Pos.AsBlockPos;
        var origin = new BlockPos(feet.X, feet.Y, feet.Z, feet.dimension);
        var rnd = new Random();
        bool krakenMode = rnd.NextDouble() < 0.05;
        int placed = PlaceScript(sapi.World.BlockAccessor, lines, origin, rnd, krakenMode);
        return TextCommandResult.Success($"Built {name} ({placed} blocks){(krakenMode ? ", kraken variant" : "")}.");
    }
}
