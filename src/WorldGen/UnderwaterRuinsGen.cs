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
        string ctype = "collapsed" + variant;

        Block chest = ResolveBlock("game:chest-" + side);
        if (chest == null) return 0;

        var stack = new ItemStack(chest);
        stack.Attributes.SetString("type", ctype);
        if (acc is IWorldGenBlockAccessor wg)
        {
            wg.SetBlock(chest.BlockId, pos);
            wg.SpawnBlockEntity(chest.EntityClass, pos, stack);
        }
        else
        {
            acc.SetBlock(chest.BlockId, pos, stack);
        }

        BlockEntity be = acc.GetBlockEntity(pos);
        if (be != null)
        {
            FieldInfo tf = be.GetType().GetField("type");
            if (tf != null && tf.FieldType == typeof(string)) tf.SetValue(be, ctype);

            if (be is Vintagestory.API.Common.IBlockEntityContainer bec && bec.Inventory != null)
            {
                IInventory inv = bec.Inventory;
                int toFill = Math.Min(inv.Count, 3 + rnd.Next(4));
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
            if (rnd.NextDouble() >= 0.5) return 0;   // 50%: no spawner here at all
            kraken = krakenMode;
        }

        Block spawner = ResolveBlock("underwaterhorrors:" + (kraken ? "krakenspawner" : "serpentspawner"));
        if (spawner == null) return 0;

        var pos = new BlockPos(Rel(t[1], o.X), Rel(t[2], o.Y), Rel(t[3], o.Z), o.dimension);
        if (acc is IWorldGenBlockAccessor wg)
        {
            wg.SetBlock(spawner.BlockId, pos);
            if (spawner.EntityClass != null) wg.SpawnBlockEntity(spawner.EntityClass, pos, null);
        }
        else
        {
            acc.SetBlock(spawner.BlockId, pos);
        }
        return 1;
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
