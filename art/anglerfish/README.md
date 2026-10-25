# Giant Anglerfish

A gigantic angler for Underwater Horrors, about 100 blocks long. It lies on
the sea floor off coasts, raises a pole with a glowing temporal gear at its
tip, and has a mouth that could swallow a sailboat whole.

The design follows two sources:

- MikeZilla_'s Blockbench rough: a whipnose angler (Gigantactis) body, sized
  against a sailboat, with the lure as part of the model on bones.
- Soareverix's sketch: rust-brown hide with dark blotches, a jutting lower
  jaw full of pale fangs, a milky eye, spiny fin rays, and a cyan gear for a
  lure.

## Files

- `tools/anglerfish/build_angler.py`: the source. It builds every shape, the
  texture atlas and the animations. Rerun it after any change.
- `tools/anglerfish/vsshape.py`: the game's shape transforms and UV rules,
  plus a small software renderer for previews.
- `assets/underwaterhorrors/shapes/entity/anglerfish.json`: display model,
  with the pole and its gear (`/uhangler show`).
- `assets/underwaterhorrors/shapes/entity/anglerfish-live.json`: the hostile
  body, with the pole but no gear; the live gear is its own entity, which
  docks on the pole tip and plays out on a rope from there.
- `assets/underwaterhorrors/shapes/entity/anglerfishlure.json`: the live
  gear (entity size 3), sitting on the entity's position.
- `assets/underwaterhorrors/shapes/entity/anglerfishluresegment.json`: one
  rope piece, 4 blocks long and 0.75 thick (entity size 3).
- `assets/underwaterhorrors/textures/entity/anglerfish.png`: the shared
  1024 x 1024 atlas.
- `reference-prototype.json`: an earlier prototype from a collaboration with
  a modeler, kept unchanged.

The JSON files open in VS Model Creator, or in Blockbench with the Vintage
Story plugin. Edits made there are lost when the script runs again, so carry
them over into the script.

## How it is built

- Everything is cuboids. Each body section is a ring of tilted plates around
  an egg-shaped cross section, so the outline reads round from every side.
- The upper head and the lower jaw are separate half rings. The jaw hinges
  far back, so the gape is most of the head. A palate, a tongue and a dark
  throat wall fill the mouth when it opens.
- The lower jaw is wider than the upper head, so its crooked fangs close
  outside the upper lip. The upper fangs hide inside the jaw until it opens.
- Fins are thin cards with a cut-out outline and painted rays. Long bony
  rays stick out past the membrane.
- The texture is painted from 3D positions: each texel knows where it sits
  on the fish. Blotches, pores, scars, the lateral line and the gill slit
  run across faces without seams.
- The gear is the vanilla temporal gear upscaled: its bars from
  `assets/survival/shapes/item/gear-temporal.json` laid on a half-unit grid
  and merged into 13 boxes that never overlap, so no two faces share a
  plane, then painted from the vanilla texture's own pixels. It is 4.5
  blocks across on the pole and as the live lure, and glows at full strength.
  The build reads that texture from the installed game.
- The eyes glow faintly, so two pale eyes show behind the lure in the dark.
- 26 faint patches of cyan spots (145 small boxes) sit in the hide's
  plates, mostly on the flanks and back, clear of the eyes, lips and gill.
  Each spot is a child of its plate's bone, so it bends with the body.

## Scale and rig

The entity renders at size 6 (`SIZE` in the script), so 8/3 model units are
one block. Model -X is forward, as the AI expects. At rest the creature
measures about:

| Part | Blocks |
| --- | --- |
| Body, chin to tail fin base | 100 long |
| With the tail fin's rays | 132 long |
| With the two trailing filaments | 165 long |
| Head | 35 tall, 30 wide |
| Pole | rises 43 above the belly, its tip 20 ahead of the entity's centre |

For scale, the game's sailboat is 8.5 x 7.8 x 6.25 blocks.

The display model has 24 animated joints, the live body 23. Both stay under
46, the cap on older client settings. The spine runs head, body0 to body4,
tail, caudal. Each spine joint is its parent's child, so a bend adds up
toward the tail.

| Clip | Frames at 30 fps | End | What it does |
| --- | --- | --- | --- |
| idle | 150 | Loop | Lurking: gills flare, jaw works slowly |
| swim | 90 | Loop | Body wave down the spine |
| swimfast | 45 | Loop | Bigger, faster wave, jaw ajar |
| lure | 180 | Loop | Nearly still, rod and gear sway |
| bite | 76 | Hold | Opens to 58 degrees, snaps shut at frame 49 |
| swallow | 106 | Hold | Opens to 64 degrees, gulps at frame 57 |
| hurt | 28 | Hold | Flinch |
| die | 121 | Hold | Rolls belly up, jaw slack |

The AI in `src/Behaviors/EntityBehaviorAnglerfishAI.cs` uses block offsets
taken from this model at size 2 and scales them by the entity's size:
`BodyAlong`, `BodySides`, `BodyHeights`, `MouthForward`, `MouthUp`,
`BodyCenterUp`, `BodyTop`, the pole tip (`PoleForward`, `PoleUp`) and the
rope's hiding place (`StackForward`, `StackUp`). Change them together with
the profile curves and `pole_path` in the script.

## Rebuild and validate

Run from the repository root:

```powershell
python tools/anglerfish/build_angler.py --preview <folder>
dotnet run --project tools/anglerfish/validate/Validate.csproj -- .
dotnet build -c Release
```

`--preview` also writes five renders of the display model to the folder.
The validator loads the shapes with the game's own classes, generates every
clip's frames and checks names, UVs and loop continuity.

## In-game inspection

```text
/uhangler show
/uhangler anim swallow
/uhangler anim off
/uh spawn anglerfish
```

`show` places a still, animated model 60 blocks ahead, facing you. `anim`
plays a clip on the nearest anglerfish within 128 blocks. `/uh spawn
anglerfish` lays the hostile one on the floor in nearby water at least 40
deep.
