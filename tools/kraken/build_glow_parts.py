"""Builds the kraken's long arm piece and its glowing spots.

An arm used to be 96 pieces of 0.84 blocks, one entity each: every one that
moved sent its own position to every nearby player, every tick. This script
fuses three of the hand-made mid pieces (krakententsegment_mid, a 6x9x6
trunk with twelve glowing nubs) end to end into one rigid 2.53-block piece,
so an arm needs a third as many.

Each long piece also gets small glowing spots set into its trunk, the way the
giant anglerfish's patches are: little squares raised a hair off the hide,
pale cyan, face glow 200. A spotted shape repeated every 2.5 blocks would
read as stripes, so there are three patterns (a, b, c) with different spots,
and the chain picks one per piece from the arm and the piece's place in it.
The body gets spots too, on a copy of its shape.

The hand-made shapes are read, never written. Run from the repo root:

    python tools/kraken/build_glow_parts.py
"""

import copy
import json
import math
import os
import random

SHAPES = "assets/underwaterhorrors/shapes/entity"
MID = f"{SHAPES}/krakententsegment_mid.json"
BODY = f"{SHAPES}/krakenbody.json"
LONG = f"{SHAPES}/krakententsegment_long_{{}}.json"
BODY_OUT = f"{SHAPES}/krakenbody_spots.json"

GLOW_TEXTURE = "underwaterhorrors:entity/giantglowdot"
SPOT_GLOW = 200
TRUNK_HEIGHT = 9.0      # model units of one mid piece along the arm
COPIES = 3

# Spot clusters per long piece in each pattern: plain-ish to busy.
PATTERNS = {"a": 3, "b": 1, "c": 2}
BODY_CLUSTERS = 10

# Face name, the axis it faces along, its side, and the two axes across it.
FACE_AXES = [("east", 0, 1, 1, 2), ("west", 0, -1, 1, 2), ("up", 1, 1, 0, 2),
             ("down", 1, -1, 0, 2), ("south", 2, 1, 0, 1), ("north", 2, -1, 0, 1)]
OPPOSITE = {"east": "west", "west": "east", "up": "down", "down": "up", "south": "north", "north": "south"}


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def write(path, shape):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(shape, f, indent="\t")
        f.write("\n")


def sub(a, b):
    return [x - y for x, y in zip(a, b)]


def spots(el, faces, rand, clusters, size_range, spread_range, n0):
    """Adds clusters of glowing spots to element el on the named faces, in
    its own frame (a child's coordinates start at its parent's 'from').
    Returns the next spot number."""
    size = [abs(v) for v in sub(el["to"], el["from"])]
    choices = [f for f in FACE_AXES if f[0] in faces]
    n = n0
    for _ in range(clusters):
        fname, axis, side, a, b = rand.choice(choices)
        u0 = 0.8 + rand.random() * (size[a] - 1.6)
        v0 = 0.8 + rand.random() * (size[b] - 1.6)
        spread = spread_range[0] + rand.random() * (spread_range[1] - spread_range[0])
        for d in range(2 + rand.randrange(4)):
            ang = rand.random() * 2 * math.pi
            dist = 0 if d == 0 else spread * math.sqrt(rand.random())
            half = size_range[1] if d == 0 else size_range[0] + rand.random() * (size_range[1] - size_range[0])
            u = min(max(u0 + math.cos(ang) * dist, half + 0.1), size[a] - half - 0.1)
            v = min(max(v0 + math.sin(ang) * dist, half + 0.1), size[b] - half - 0.1)
            lo, hi = [0.0, 0.0, 0.0], [0.0, 0.0, 0.0]
            lo[a], hi[a] = u - half, u + half
            lo[b], hi[b] = v - half, v + half
            surface = size[axis] if side > 0 else 0.0
            lo[axis], hi[axis] = (surface - 0.05, surface + 0.1) if side > 0 else (surface - 0.1, surface + 0.05)
            n += 1
            el.setdefault("children", []).append({
                "name": f"glowspot{n}",
                "from": [round(x, 3) for x in lo],
                "to": [round(x, 3) for x in hi],
                "faces": {f[0]: {"texture": "#glowdot", "uv": [0, 0, 4, 4], "glow": SPOT_GLOW}
                          for f in FACE_AXES if f[0] != OPPOSITE[fname]},
            })
    return n


def long_piece(mid, pattern, clusters):
    rand = random.Random(f"kraken-{pattern}")
    elements = []
    n = 0
    # Clusters fall on any of the three trunks.
    per_trunk = [0] * COPIES
    for _ in range(clusters):
        per_trunk[rand.randrange(COPIES)] += 1
    for k in range(COPIES):
        for el in copy.deepcopy(mid["elements"]):
            dy = TRUNK_HEIGHT * k
            el["name"] = f"{el['name']}_{k}"
            el["from"][1] += dy
            el["to"][1] += dy
            if el.get("rotationOrigin"):
                el["rotationOrigin"][1] += dy
            # The trunk is 6 units across: spots of one or two voxels.
            n = spots(el, ("north", "south", "east", "west"), rand, per_trunk[k], (0.3, 0.6), (0.6, 1.4), n)
            elements.append(el)
    shape = {k: copy.deepcopy(mid[k]) for k in ("textureWidth", "textureHeight", "textureSizes", "textures")}
    shape["textures"]["glowdot"] = GLOW_TEXTURE
    shape["textureSizes"]["glowdot"] = [4, 4]
    shape["elements"] = elements
    return shape, n


def body_spots(body):
    rand = random.Random("kraken-body")
    shape = copy.deepcopy(body)
    shape.pop("editor", None)
    shape.setdefault("textureSizes", {})["glowdot"] = [4, 4]
    shape["textures"]["glowdot"] = GLOW_TEXTURE
    n = spots(shape["elements"][0], ("up", "north", "south", "east", "west"), rand, BODY_CLUSTERS, (0.8, 1.6), (2.0, 5.0), 0)
    return shape, n


def main():
    mid = load(MID)
    for pattern, clusters in PATTERNS.items():
        shape, n = long_piece(mid, pattern, clusters)
        write(LONG.format(pattern), shape)
        print(f"wrote {LONG.format(pattern)}: {COPIES} trunks, {n} spots")
    shape, n = body_spots(load(BODY))
    write(BODY_OUT, shape)
    print(f"wrote {BODY_OUT}: {n} spots")


if __name__ == "__main__":
    os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    main()
