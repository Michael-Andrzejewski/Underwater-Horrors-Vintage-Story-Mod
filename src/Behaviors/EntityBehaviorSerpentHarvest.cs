using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UnderwaterHorrors;

/// <summary>
/// A killed serpent's loot and bones. The carcass is harvested the game's
/// way (EntityBehaviorRemainsHarvestable: a knife, then the carcass contents
/// dialog), from any of its hit proxies or the body itself; this behavior
/// fills it: a random assortment of raw fish fillets and bones, and for the
/// rust serpent rusty gears and Jonas parts too. When the carcass goes,
/// emptied or decayed, it leaves a bonyremains pile on the sea floor below
/// each hit-proxy offset, so the skeleton of the beast marks where it fell.
/// </summary>
public class EntityBehaviorSerpentHarvest : EntityBehavior, IHarvestableDrops
{
    private const string PilesLeftAttr = "underwaterhorrors:bonePilesLeft";

    // The vanilla fillet lives in itemtypes/food/fishfillet.json but its
    // item code is "fish", not "fishfillet". SpawnStack skips items it
    // cannot resolve, so the old "fishfillet-raw" meant serpents silently
    // never dropped any fish at all.
    // Shared with the giant serpent's pieces (GiantSerpentPieceEntity).
    internal static readonly AssetLocation FishAsset = new("game", "fish-raw");
    internal static readonly AssetLocation BoneAsset = new("game", "bone");
    internal static readonly AssetLocation GearAsset = new("game", "gear-rusty");
    internal static readonly AssetLocation BonePileAsset = new("game", "bonyremains-ribcage");

    // The Jonas parts the creatures carry. Not the pump head: nothing needs
    // one from them.
    internal static readonly string[] JonasPartTypes =
    {
        "connector01", "cylinder01", "cylinder02", "tank01", "tank02", "valve01"
    };

    // Set on the rust (aggressive surface) serpent via JSON attributes.
    private bool rustLoot;
    // Multiplies every stack and part count; the giant serpent carries a hoard.
    private int lootMultiplier = 1;

    public EntityBehaviorSerpentHarvest(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        rustLoot = attributes["rustLoot"].AsBool(false);
        lootMultiplier = Math.Max(1, attributes["lootMultiplier"].AsInt(1));
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        // A killed serpent that decays away unharvested still leaves its
        // skeleton. Unload/disconnect despawns are not real removals, so
        // they must not scatter bones.
        if (entity.Api.Side == EnumAppSide.Server &&
            entity.WatchedAttributes.GetBool(EntityBehaviorSerpentDeath.DiedForRealAttr) &&
            (despawn == null ||
             despawn.Reason == EnumDespawnReason.Death ||
             despawn.Reason == EnumDespawnReason.Expire ||
             despawn.Reason == EnumDespawnReason.Removed))
        {
            LeaveBonePiles();
        }

        base.OnEntityDespawn(despawn);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Loot
    // ═══════════════════════════════════════════════════════════════════
    /// <summary>What the knife finds in the carcass (the harvest asks once).</summary>
    public ItemStack[] GetHarvestableDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
    {
        var rand = world.Rand;
        var loot = new List<ItemStack>();
        int m = lootMultiplier;
        AddStacks(world, loot, FishAsset, (3 + rand.Next(4)) * m);   // 3-6 raw fillets
        AddStacks(world, loot, BoneAsset, (4 + rand.Next(5)) * m);   // 4-8 bones

        if (rustLoot)
        {
            AddStacks(world, loot, GearAsset, (5 + rand.Next(6)) * m);   // 5-10 rusty gears

            int parts = (2 + rand.Next(3)) * m;                       // 2-4 Jonas parts
            for (int i = 0; i < parts; i++)
            {
                string type = JonasPartTypes[rand.Next(JonasPartTypes.Length)];
                AddStacks(world, loot, new AssetLocation("game", "jonasparts-" + type), 1);
            }
        }
        return loot.ToArray();
    }

    /// <summary>
    /// Adds <paramref name="quantity"/> of an item to the loot, merged into a
    /// stack of it already there and split at its stack size.
    /// </summary>
    internal static void AddStacks(IWorldAccessor world, List<ItemStack> loot, AssetLocation itemCode, int quantity)
    {
        if (quantity <= 0) return;

        Item item = world.GetItem(itemCode);
        if (item == null)
        {
            // Missing item: skip rather than crash the harvest, but say so.
            // Silently swallowing this hid a typo'd fish code for months.
            world.Api.Logger.Warning(
                "[underwaterhorrors] loot item '{0}' does not exist, dropping nothing for it", itemCode);
            return;
        }

        int max = Math.Max(1, item.MaxStackSize);
        foreach (ItemStack stack in loot)
        {
            if (stack.Collectible != item || stack.StackSize >= max) continue;
            int add = Math.Min(quantity, max - stack.StackSize);
            stack.StackSize += add;
            quantity -= add;
        }
        while (quantity > 0)
        {
            int q = Math.Min(quantity, max);
            loot.Add(new ItemStack(item, q));
            quantity -= q;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Bone piles — one per hit-proxy offset, on the sea floor below
    // ═══════════════════════════════════════════════════════════════════
    private void LeaveBonePiles()
    {
        if (entity.WatchedAttributes.GetBool(PilesLeftAttr)) return;
        entity.WatchedAttributes.SetBool(PilesLeftAttr, true);

        var proxies = entity.GetBehavior<EntityBehaviorSerpentHitProxies>();
        int count = proxies?.Offsets.Length ?? 0;
        var placed = new HashSet<BlockPos>();

        for (int i = 0; i < Math.Max(1, count); i++)
        {
            double x = entity.Pos.X, z = entity.Pos.Z;
            if (count > 0) proxies.GetSpinePoint(i, out x, out _, out z);
            PlaceBonePile(entity.World, x, entity.Pos.Y + 1, z, entity.Pos.Dimension, placed);
        }
    }

    /// <summary>
    /// Puts a bone pile on the sea floor below (x, fromY, z), where the
    /// block there can take it. <paramref name="placed"/> skips spots that
    /// already got one.
    /// </summary>
    internal static void PlaceBonePile(IWorldAccessor world, double x, double fromY, double z, int dimension,
        HashSet<BlockPos> placed = null)
    {
        Block pileBlock = world.GetBlock(BonePileAsset);
        if (pileBlock == null) return;

        var accessor = world.BlockAccessor;
        int floorY = FindRestingYBelow(accessor, x, fromY, z, dimension);
        if (floorY < 0) return;

        BlockPos pos = new((int)Math.Floor(x), floorY, (int)Math.Floor(z), dimension);
        if (placed != null && !placed.Add(pos)) return;

        Block present = accessor.GetBlock(pos);
        if (present == null || !present.IsReplacableBy(pileBlock)) return;

        accessor.SetBlock(pileBlock.BlockId, pos);
        accessor.MarkBlockDirty(pos);
    }

    /// <summary>
    /// Scans down from fromY for the first solid block and returns the Y
    /// of the space just above it (where a pile can rest, or a corpse can
    /// come to rest). Returns -1 if no floor is found within range.
    /// Shared with EntityBehaviorSerpentDeath's sink logic.
    /// </summary>
    internal static int FindRestingYBelow(IBlockAccessor accessor, double x, double fromY, double z, int dimension)
    {
        int startY = (int)fromY;
        int limit = Math.Max(0, startY - 80);
        BlockPos scanPos = new((int)x, startY, (int)z, dimension);

        for (int y = startY; y >= limit; y--)
        {
            scanPos.Y = y;
            Block block = accessor.GetBlock(scanPos);
            if (block == null) continue;
            if (block.Id != 0 && !block.IsLiquid() && block.Replaceable < 6000)
            {
                return y + 1;
            }
        }
        return -1;
    }

    public override string PropertyName() => "underwaterhorrors:serpentharvest";
}
