using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace UnderwaterHorrors;

/// <summary>
/// Makes a killed serpent harvestable from ANY of its hit proxies (or
/// the body itself): right-click with a knife on any part of the dead
/// body harvests the whole carcass once. Loot is a random assortment
/// of raw fish fillets and bones; the rust serpent additionally yields
/// rusty gears and Jonas parts. Harvesting (or the corpse decaying
/// unharvested) leaves a bonyremains pile on the sea floor below each
/// hit-proxy offset, so the skeleton of the beast marks where it fell.
///
/// The vanilla "harvestable" behavior is not used because its knife
/// flow keys off the clicked entity having the behavior itself; the
/// clicked entity here is usually a hit proxy 10+ blocks from the
/// serpent's center, and the loot must be a single shared pool.
/// </summary>
public class EntityBehaviorSerpentHarvest : EntityBehavior
{
    public const string HarvestedAttr = "underwaterhorrors:harvested";
    private const string PilesLeftAttr = "underwaterhorrors:bonePilesLeft";

    private static readonly AssetLocation FishAsset = new("game", "fishfillet-raw");
    private static readonly AssetLocation BoneAsset = new("game", "bone");
    private static readonly AssetLocation GearAsset = new("game", "gear-rusty");
    private static readonly AssetLocation BonePileAsset = new("game", "bonyremains-ribcage");

    private static readonly string[] JonasPartTypes =
    {
        "connector01", "cylinder01", "cylinder02", "tank01", "tank02", "valve01", "pumphead"
    };

    // Set on the rust (aggressive surface) serpent via JSON attributes.
    private bool rustLoot;

    private readonly BlockPos scanPos = new(0);

    public EntityBehaviorSerpentHarvest(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        rustLoot = attributes["rustLoot"].AsBool(false);
    }

    /// <summary>
    /// Called from SerpentEntity.OnInteract and EntitySerpentHitProxy.
    /// OnInteract (server side). Returns true when the interaction was
    /// consumed as a harvest attempt on a dead serpent.
    /// </summary>
    public bool TryHarvest(EntityAgent byEntity, ItemSlot slot, Vec3d dropPos)
    {
        if (entity.Api.Side != EnumAppSide.Server) return false;
        if (entity.Alive) return false;
        if (!entity.WatchedAttributes.GetBool(EntityBehaviorSerpentDeath.DiedForRealAttr)) return false;
        if (entity.WatchedAttributes.GetBool(HarvestedAttr)) return true;

        IServerPlayer player = (byEntity as EntityPlayer)?.Player as IServerPlayer;

        if (slot?.Itemstack?.Collectible?.Tool != EnumTool.Knife)
        {
            player?.SendIngameError("underwaterhorrors-needknife",
                "You need a knife to harvest the serpent.");
            return true;
        }

        entity.WatchedAttributes.SetBool(HarvestedAttr, true);

        SpawnLoot(dropPos ?? entity.Pos.XYZ);
        LeaveBonePiles();

        // Carcass has been butchered: remove it with the vanilla decay
        // particle burst instead of leaving a picked-clean body around.
        entity.GetBehavior<EntityBehaviorDeadDecay>()?.DecayNow();
        return true;
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
    private void SpawnLoot(Vec3d dropPos)
    {
        var world = entity.World;
        var rand = world.Rand;

        SpawnStack(FishAsset, 3 + rand.Next(4), dropPos);   // 3-6 raw fillets
        SpawnStack(BoneAsset, 4 + rand.Next(5), dropPos);   // 4-8 bones

        if (rustLoot)
        {
            SpawnStack(GearAsset, 5 + rand.Next(6), dropPos);   // 5-10 rusty gears

            int parts = 2 + rand.Next(3);                       // 2-4 Jonas parts
            for (int i = 0; i < parts; i++)
            {
                string type = JonasPartTypes[rand.Next(JonasPartTypes.Length)];
                SpawnStack(new AssetLocation("game", "jonasparts-" + type), 1, dropPos);
            }
        }
    }

    private void SpawnStack(AssetLocation itemCode, int quantity, Vec3d dropPos)
    {
        if (quantity <= 0) return;

        Item item = entity.World.GetItem(itemCode);
        if (item == null) return;   // item missing in heavily modded worlds: skip quietly

        var rand = entity.World.Rand;
        Vec3d pos = dropPos.AddCopy(
            (rand.NextDouble() - 0.5) * 0.8,
            0.3,
            (rand.NextDouble() - 0.5) * 0.8);

        entity.World.SpawnItemEntity(new ItemStack(item, quantity), pos);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Bone piles — one per hit-proxy offset, on the sea floor below
    // ═══════════════════════════════════════════════════════════════════
    private void LeaveBonePiles()
    {
        if (entity.WatchedAttributes.GetBool(PilesLeftAttr)) return;
        entity.WatchedAttributes.SetBool(PilesLeftAttr, true);

        var world = entity.World;
        Block pileBlock = world.GetBlock(BonePileAsset);
        if (pileBlock == null) return;

        float[] offsets = entity.GetBehavior<EntityBehaviorSerpentHitProxies>()?.Offsets;
        if (offsets == null || offsets.Length == 0) offsets = new float[] { 0f };

        float yaw = entity.Pos.Yaw;
        double fx = Math.Sin(yaw);
        double fz = Math.Cos(yaw);

        var accessor = world.BlockAccessor;
        var placed = new HashSet<BlockPos>();

        foreach (float off in offsets)
        {
            double x = entity.Pos.X + fx * off;
            double z = entity.Pos.Z + fz * off;

            int floorY = FindRestingYBelow(accessor, x, entity.Pos.Y + 1, z, entity.Pos.Dimension);
            if (floorY < 0) continue;

            BlockPos pos = new((int)x, floorY, (int)z, entity.Pos.Dimension);
            if (!placed.Add(pos)) continue;

            Block present = accessor.GetBlock(pos);
            if (present == null || !present.IsReplacableBy(pileBlock)) continue;

            accessor.SetBlock(pileBlock.BlockId, pos);
            accessor.MarkBlockDirty(pos);
        }
    }

    /// <summary>
    /// Scans down from fromY for the first solid block and returns the Y
    /// of the space just above it (where a pile can rest). Returns -1 if
    /// no floor is found within range.
    /// </summary>
    private int FindRestingYBelow(IBlockAccessor accessor, double x, double fromY, double z, int dimension)
    {
        int startY = (int)fromY;
        int limit = Math.Max(0, startY - 80);
        scanPos.Set((int)x, startY, (int)z);
        scanPos.dimension = dimension;

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
