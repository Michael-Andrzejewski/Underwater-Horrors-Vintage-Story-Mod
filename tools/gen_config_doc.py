"""Build CONFIG.md from the comments in src/UnderwaterHorrorsConfig.cs.

Run from the repo root:  python tools/gen_config_doc.py

Every public property in the config class becomes one table row. The
description is the block of // comments directly above the property, so
the source comments stay the single place to write documentation.
"""
import re, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "UnderwaterHorrorsConfig.cs")
OUT = os.path.join(ROOT, "CONFIG.md")

PROP = re.compile(r'^\s*public\s+([\w<>\[\], ]+?)\s+(\w+)\s*\{\s*get;\s*set;\s*\}\s*(?:=\s*(.+?);)?\s*(?://\s*(.*))?$')

rows = []
comment = []
in_class = False
for line in open(SRC, encoding="utf-8"):
    s = line.rstrip("\n")
    if "public class UnderwaterHorrorsConfig" in s:
        in_class = True
        comment = []
        continue
    if not in_class:
        continue
    st = s.strip()
    if st.startswith("//"):
        comment.append(st.lstrip("/").strip())
        continue
    m = PROP.match(s)
    if m:
        typ, name, default, trail = m.groups()
        desc = " ".join(comment).strip()
        if trail:
            desc = (desc + " " + trail.strip()).strip()
        if default and default.startswith("Default") and default.endswith("()"):
            default = "see table below" if "Count" in name or "Types" in name else default
        # A one-line comment of a few words with no period is a section
        # header ("Spawn system", "Sea serpent"), not a description.
        if len(comment) == 1 and len(desc.split()) <= 3 and not desc.endswith(".") and not trail:
            rows.append(("__header__", desc, "", ""))
            desc = ""
        rows.append((name, typ.strip(), (default or "").strip(), desc))
        comment = []
        continue
    if st == "" or st.startswith("public static") or st.startswith("private") or st.startswith("public void") or st.startswith("public double Resolve"):
        comment = []

lines = []
lines.append("# Underwater Horrors config reference")
lines.append("")
lines.append("File: `VintagestoryData/ModConfig/UnderwaterHorrorsConfig.json`. Created with defaults on first run. Edit it with the server stopped, or use the `/uh` commands where one exists and the mod writes the file for you.")
lines.append("")
lines.append("Generated from the source comments by `tools/gen_config_doc.py`; do not edit by hand.")
lines.append("")
lines.append("Properties ending in `Migrated` or `Applied` are one-shot upgrade markers. Leave them alone.")
lines.append("")
lines.append("| Setting | Type | Default | What it does |")
lines.append("|---|---|---|---|")
for name, typ, default, desc in rows:
    if name == "__header__":
        lines.append(f"| **{typ}** | | | |")
        continue
    desc = desc.replace("|", "\\|")
    default = default.replace("|", "\\|")
    lines.append(f"| `{name}` | {typ} | `{default}` | {desc} |")
lines.append("")
lines.append("## Ruin loot tables")
lines.append("")
lines.append("`RuinLootChestsPerStructure` and `RuinIngotPilesPerStructure` map each structure name (`ruin`, `portal`, `shipwreck-small`, `shipwreck-medium`, `shipwreck-huge`, `city`) to a `{ Min, Max }` count. `RuinIngotTypes` maps a metal name (any `game:ingot-<metal>`) to `{ Weight, CountMin, CountMax }`; a pile picks its metal by weight and its size uniformly between the two counts. Set a structure's pile count to 0 to give it no ingots.")
lines.append("")
lines.append("Since 0.20.0 a pile's metal and size are rolled from the current config when a player first comes near the ruin, so changing `RuinIngotTypes` also changes every ruin nobody has visited yet. The number of chests and piles per structure is fixed when the chunk generates. Chests are vanilla stack randomizers and follow the vanilla loot tables; the chest count is the only lever for them.")
lines.append("")
open(OUT, "w", encoding="utf-8", newline="\n").write("\n".join(lines))
print(f"wrote {OUT}: {len(rows)} settings")
