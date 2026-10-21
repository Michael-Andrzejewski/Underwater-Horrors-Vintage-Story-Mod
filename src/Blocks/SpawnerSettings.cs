using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

/// <summary>
/// One editable per-spawner setting, shown as a row in
/// <see cref="GuiDialogSpawnerSettings"/>: a number box or an on/off switch.
/// </summary>
public class SpawnerSettingField
{
    public string Key;
    public string Label;
    public bool IsSwitch;
    public float Number;
    public bool Flag;
}

/// <summary>
/// The creature spawner block. Behaves like any block, except that a
/// creative player who shift-right-clicks it gets the spawner's settings
/// window (trigger radius and the rest), so each spawner in a dungeon can
/// be tuned on its own without touching the mod config.
/// </summary>
public class BlockCreatureSpawner : Block
{
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (byPlayer?.WorldData?.CurrentGameMode == EnumGameMode.Creative && byPlayer.Entity?.Controls?.ShiftKey == true)
        {
            if (world.Api is ICoreClientAPI capi && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityCreatureSpawner be)
            {
                be.OpenSettings(capi);
            }
            return true;
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }
}

/// <summary>
/// A small settings window: one row per field, Save sends the values to the
/// server as a block entity packet (the server re-checks creative mode).
/// </summary>
public class GuiDialogSpawnerSettings : GuiDialogGeneric
{
    public const int SavePacketId = 4411;

    private readonly BlockPos pos;
    private readonly List<SpawnerSettingField> fields;
    private readonly string note;

    public GuiDialogSpawnerSettings(string title, BlockPos pos, List<SpawnerSettingField> fields, string note, ICoreClientAPI capi)
        : base(title, capi)
    {
        this.pos = pos.Copy();
        this.fields = fields;
        this.note = note;
        Compose();
    }

    private void Compose()
    {
        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;

        var c = capi.Gui.CreateCompo("spawnersettings-" + pos, dialogBounds)
            .AddShadedDialogBG(bg)
            .AddDialogTitleBar(DialogTitle, () => TryClose())
            .BeginChildElements(bg);

        double y = 35;
        foreach (var f in fields)
        {
            c.AddStaticText(f.Label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y + 4, 300, 25));
            if (f.IsSwitch) c.AddSwitch(_ => { }, ElementBounds.Fixed(310, y, 30, 30), f.Key);
            else c.AddNumberInput(ElementBounds.Fixed(310, y, 110, 29), null, CairoFont.WhiteDetailText(), f.Key);
            y += 38;
        }
        if (!string.IsNullOrEmpty(note))
        {
            c.AddStaticText(note, CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y + 2, 420, 40));
            y += 44;
        }
        c.AddSmallButton(Lang.Get("Cancel"), () => TryClose(), ElementBounds.Fixed(0, y + 6, 110, 25))
         .AddSmallButton(Lang.Get("Save"), OnSave, ElementBounds.Fixed(310, y + 6, 110, 25))
         .EndChildElements();
        SingleComposer = c.Compose();

        foreach (var f in fields)
        {
            if (f.IsSwitch) SingleComposer.GetSwitch(f.Key).SetValue(f.Flag);
            else SingleComposer.GetNumberInput(f.Key).SetValue(f.Number.ToString(CultureInfo.InvariantCulture));
        }
    }

    private bool OnSave()
    {
        var tree = new TreeAttribute();
        foreach (var f in fields)
        {
            if (f.IsSwitch) tree.SetBool(f.Key, SingleComposer.GetSwitch(f.Key).On);
            else tree.SetFloat(f.Key, SingleComposer.GetNumberInput(f.Key).GetValue());
        }
        capi.Network.SendBlockEntityPacket(pos, SavePacketId, tree.ToBytes());
        TryClose();
        return true;
    }

    public override string ToggleKeyCombinationCode => null;
}
