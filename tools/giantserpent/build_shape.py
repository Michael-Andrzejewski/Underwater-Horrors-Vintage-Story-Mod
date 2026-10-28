"""Builds the giant serpent shape from the rust serpent's.

The rust serpent's rig is rooted at its chest: the neck chain runs forward
to the head and the tail chain runs back. That suits keyframed swimming but
not a body that follows its head along a path, because turning the chest
swings the whole neck sideways. This script re-roots the same model at the
middle of the mouth:

    root (mouth) > head > neck10 > neck9 > neck6 ... neck1 > chest > chest4 > ... tail20

Every element keeps its rest position, so the mesh, UVs and texture are the
rust serpent's own. Only the parent order of the neck chain flips, and each
flipped bone now pivots at its head-side joint. The client then sets each
spine bone's yaw and pitch every frame so the body traces the mouth's path
(EntityBehaviorGiantSerpentBody).

Animations: the spine bones get zero keyframes only, which makes them
animation joints (the game only animates elements that some animation
names). Fins and whiskers keep the swim flutter. The jaw gets gape and gulp
clips.

It also writes the 22 piece shapes a killed giant breaks into
(shapes/entity/giantserpentpiece), and measures the rest model for the
entity file (entities/giantserpent.json): each spine bone's pivot arc, link
elevation and solid extent, and the table of hit proxy boxes.

Run from the repo root:  python tools/giantserpent/build_shape.py
"""

import copy
import json
import math
import os
import random
import re

SRC = "assets/underwaterhorrors/shapes/entity/seaserpent.json"
DST = "assets/underwaterhorrors/shapes/entity/giantserpent.json"

# Head-side first. The tail chain keeps its original parenting.
NECK_FROM_HEAD = ["neck10", "neck9", "neck6", "neck5", "neck4", "neck3", "neck2", "neck1"]
TAIL = ["chest4", "chest5"] + [f"tail{i}" for i in range(11, 21)]
SPINE = ["head"] + NECK_FROM_HEAD + ["chest"] + TAIL

# Middle of the mouth in the head's frame: halfway along the teeth rows,
# at the height of the jaw hinge where the two jaws meet.
MOUTH_IN_HEAD = (-38.0, -1.75, 0.0)

# The new root sits here in model space (16 units = 1 block before scaling).
# x and z of 8 cancel the renderer's (-0.5, 0, -0.5) centering shift, so the
# mouth lands exactly on the entity position at any entity size.
ROOT_AT = (8.0, 0.0, 8.0)


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def find(elements, name):
    for e in elements:
        if e["name"] == name:
            return e
        hit = find(e.get("children", []), name)
        if hit is not None:
            return hit
    return None


def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def abs_frames(shape):
    """Absolute frame origin of every named spine element at rest."""
    out = {}

    def walk(elements, parent_abs):
        for e in elements:
            here = add(parent_abs, tuple(e["from"]))
            if e["name"] in SPINE or e["name"] in ("main", "head", "jaw"):
                rot = (e.get("rotationX", 0), e.get("rotationY", 0), e.get("rotationZ", 0))
                if e["name"] != "jaw" and any(abs(r) > 1e-9 for r in rot):
                    raise SystemExit(f"{e['name']} has a rest rotation; the re-root assumes none")
                out[e["name"]] = here
            walk(e.get("children", []), here)

    walk(shape["elements"], (0.0, 0.0, 0.0))
    return out


def without_child(element, child_name):
    copy_ = {k: copy.deepcopy(v) for k, v in element.items() if k != "children"}
    copy_["children"] = [copy.deepcopy(c) for c in element.get("children", []) if c["name"] != child_name]
    return copy_


def rebuild(shape):
    absf = abs_frames(shape)
    mouth = add(absf["head"], MOUTH_IN_HEAD)
    shift = sub(ROOT_AT, mouth)

    def place(element, parent_abs, own_abs):
        # Same rest position, new parent; pivot at the parent's frame origin,
        # which is the joint on the head side of this bone.
        element["from"] = list(sub(own_abs, parent_abs))
        element["to"] = list(element["from"])
        element["rotationOrigin"] = [0, 0, 0]

    # head keeps its own children (teeth, jaw, cubes) and gains neck10.
    head = without_child(find(shape["elements"], "head"), None)
    place(head, mouth, absf["head"])

    prev, prev_abs = head, absf["head"]
    chain_order = NECK_FROM_HEAD + ["chest"]
    for i, name in enumerate(chain_order):
        original = find(shape["elements"], name)
        # Drop the child that is now this bone's parent. The chest keeps
        # chest4, so the tail chain stays as it was.
        toward_head = "head" if i == 0 else chain_order[i - 1]
        node = without_child(original, toward_head)
        place(node, prev_abs, absf[name])
        prev["children"].append(node)
        prev, prev_abs = node, absf[name]

    root = {
        "name": "root",
        "from": list(ROOT_AT),
        "to": list(ROOT_AT),
        "rotationOrigin": list(ROOT_AT),
        "children": [head],
    }
    # head's from is relative to the root frame, which sits at the mouth.
    head["from"] = list(sub(absf["head"], mouth))
    head["to"] = list(head["from"])
    return root, absf, mouth, shift


def spine_report(shape, root):
    """Rest pivot of each spine bone and the arc length to it, in model units."""
    pivots = []

    def walk(e, frame_abs, parent_abs):
        here = add(parent_abs, tuple(e["from"]))
        if e["name"] in SPINE:
            pivots.append((e["name"], add(parent_abs, tuple(e["rotationOrigin"]))))
        for c in e.get("children", []):
            walk(c, here, here)

    walk(root, None, (0.0, 0.0, 0.0))
    order = {n: i for i, n in enumerate(SPINE)}
    pivots.sort(key=lambda p: order[p[0]])
    return pivots


def animations(shape):
    by_code = {a["code"]: a for a in shape["animations"]}
    spine_and_root = set(SPINE) | {"root"}

    def zeros(names):
        return {n: {"rotationX": 0, "rotationY": 0, "rotationZ": 0} for n in sorted(names)}

    swim = copy.deepcopy(by_code["swim"])
    for kf in swim["keyframes"]:
        kf["elements"] = {k: v for k, v in kf["elements"].items() if k not in spine_and_root and k != "jaw"}
    swim["keyframes"][0]["elements"].update(zeros(spine_and_root))
    swim["name"] = swim["code"] = "idle"

    def jaw_clip(code, frames, keys, end="Hold"):
        return {
            "name": code,
            "code": code,
            "quantityframes": frames,
            "onActivityStopped": "EaseOut",
            "onAnimationEnd": end,
            "keyframes": [
                {"frame": f, "elements": {"jaw": {"rotationX": 0, "rotationY": 0, "rotationZ": z}}}
                for f, z in keys
            ],
        }

    gape = jaw_clip("gape", 16, [(0, 0), (10, 70), (15, 78)])
    gulp = jaw_clip("gulp", 12, [(0, 78), (3, 0), (11, 0)])

    dead = {
        "name": "dead",
        "code": "dead",
        "quantityframes": 2,
        "onActivityStopped": "Stop",
        "onAnimationEnd": "Repeat",
        "keyframes": [{"frame": 0, "elements": zeros(spine_and_root | {"jaw"})}],
    }
    dead["keyframes"][0]["elements"]["jaw"]["rotationZ"] = 20
    return [swim, gape, gulp, dead]


# ── Pieces, hitboxes and spine numbers ────────────────────────────────
#
# A killed giant breaks apart into one piece per spine bone. Each piece
# shape is that bone with everything it holds except the next spine bone,
# pivoted at its head-side joint, which lands on the entity position.
#
# The hit proxies and the pieces need to know where the body is, so this
# also measures the rest model and writes the numbers into the entity file.
# Lengths there are blocks at entity size 1, measured from the mouth along
# the body (the rest body is straight along +x, so arc = x).

PIECE_DIR = "assets/underwaterhorrors/shapes/entity/giantserpentpiece"
ENTITY = "assets/underwaterhorrors/entities/giantserpent.json"

# A cube counts as solid body when its thinnest side is at least this many
# model units. Fins and whiskers are flat planes, spikes thin rods.
SOLID_MIN = 4.0

# Hitbox spacing along the body: 0.8 of the body's width there, kept
# between these (blocks at size 1). Every box is at least as wide as the
# spacing, so the boxes overlap along the body whichever way it points.
PROXY_STEP_MIN = 0.5
PROXY_STEP_MAX = 1.2

# The live giant's glow is drawn at run time (GiantSerpentGlow). Its remains
# keep it as glowing spots baked into the piece shapes, the way the
# anglerfish's patches are: about as many patches over the whole body as the
# live glow has, set on the faces of the solid cubes, in the same pale cyan.
GLOW_TEXTURE = "underwaterhorrors:entity/giantglowdot"
GLOW_PATCHES = 150
SPOT_GLOW = 200


def mat_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def mat_trans(v):
    return [[1, 0, 0, v[0]], [0, 1, 0, v[1]], [0, 0, 1, v[2]], [0, 0, 0, 1]]


def mat_rot(axis, deg):
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    if axis == "x":
        return [[1, 0, 0, 0], [0, c, -s, 0], [0, s, c, 0], [0, 0, 0, 1]]
    if axis == "y":
        return [[c, 0, s, 0], [0, 1, 0, 0], [-s, 0, c, 0], [0, 0, 0, 1]]
    return [[c, -s, 0, 0], [s, c, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]]


def local_matrix(el):
    """The game's element transform: T(origin) Rx Ry Rz T(from - origin)."""
    o = el.get("rotationOrigin") or [0, 0, 0]
    m = mat_trans(o)
    for axis in "xyz":
        deg = el.get("rotation" + axis.upper(), 0)
        if deg:
            m = mat_mul(m, mat_rot(axis, deg))
    return mat_mul(m, mat_trans(sub(el["from"], o)))


def apply(m, p):
    return tuple(sum(m[i][k] * (p[k] if k < 3 else 1) for k in range(4)) for i in range(3))


def solid_boxes(root, solid=True):
    """(owning spine bone, world AABB) of every solid cube, in model units.
    With solid=False, of everything drawn but the whiskers: fins, frills and
    spikes too, the parts a player swings at."""
    out = []

    def walk(el, parent, bone, whisker):
        m = mat_mul(parent, local_matrix(el))
        bone = el["name"] if el["name"] in SPINE else bone
        whisker = whisker or "whisker" in el["name"]
        size = sub(el["to"], el["from"])
        keep = min(abs(v) for v in size) >= SOLID_MIN if solid else not whisker
        if el.get("faces") and keep:
            corners = [apply(m, (x, y, z)) for x in (0, size[0]) for y in (0, size[1]) for z in (0, size[2])]
            lo = tuple(min(c[i] for c in corners) for i in range(3))
            hi = tuple(max(c[i] for c in corners) for i in range(3))
            out.append((bone, lo, hi))
        for c in el.get("children", []):
            walk(c, m, bone, whisker)

    walk(root, [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]], None, False)
    return out


def write_pieces(out):
    os.makedirs(PIECE_DIR, exist_ok=True)
    pieces = []
    for i, name in enumerate(SPINE):
        bone = find(out["elements"], name)
        next_bone = SPINE[i + 1] if i + 1 < len(SPINE) else None
        piece = without_child(bone, next_bone)
        # Pivot on the root: the neck bones already pivot at their parent's
        # frame origin, the tail bones still at their own.
        origin = piece.get("rotationOrigin") or [0, 0, 0]
        piece["from"] = list(sub(piece["from"], origin))
        piece["to"] = list(sub(piece["to"], origin))
        piece["rotationOrigin"] = [0, 0, 0]
        pieces.append((name, piece))

    # Patches go to the pieces by their solid surface, as on the live body.
    faces = {name: solid_faces(piece) for name, piece in pieces}
    total = sum(f[3] for fs in faces.values() for f in fs)
    for i, (name, piece) in enumerate(pieces):
        area = sum(f[3] for f in faces[name])
        rand = random.Random(1000 + i)
        glow_spots(faces[name], rand, max(1, round(GLOW_PATCHES * area / total)))
        root = {
            "name": "root",
            "from": list(ROOT_AT),
            "to": list(ROOT_AT),
            "rotationOrigin": list(ROOT_AT),
            "children": [piece],
        }
        shape = {k: copy.deepcopy(out[k]) for k in ("textureWidth", "textureHeight", "textureSizes", "textures")}
        shape["textures"]["glowdot"] = GLOW_TEXTURE
        shape["textureSizes"]["glowdot"] = [4, 4]
        shape["elements"] = [root]
        write_shape(f"{PIECE_DIR}/{name}.json", shape)


# Face name, the axis it faces along, its side, and the two axes across it.
FACE_AXES = [("east", 0, 1, 1, 2), ("west", 0, -1, 1, 2), ("up", 1, 1, 0, 2),
             ("down", 1, -1, 0, 2), ("south", 2, 1, 0, 1), ("north", 2, -1, 0, 1)]


def solid_faces(piece):
    """(element, face, size, area) of every drawn face of the piece's solid
    cubes, in the cube's own frame."""
    out = []

    def walk(el):
        size = [abs(v) for v in sub(el["to"], el["from"])]
        if el.get("faces") and min(size) >= SOLID_MIN:
            for face in FACE_AXES:
                if face[0] in el["faces"]:
                    out.append((el, face, size, size[face[3]] * size[face[4]]))
        for c in el.get("children", []):
            walk(c)

    walk(piece)
    return out


def glow_spots(faces, rand, patches):
    """Clusters of small glowing spots on random solid faces, picked by area,
    each spot a thin square raised a hair off its face. Children of the cube
    they sit on, so they move with it."""
    if not faces:
        return
    total = sum(f[3] for f in faces)
    n = 0
    for _ in range(patches):
        r = rand.random() * total
        for el, (fname, axis, side, a, b), size, area in faces:
            r -= area
            if r <= 0:
                break
        u0 = 1 + rand.random() * (size[a] - 2)
        v0 = 1 + rand.random() * (size[b] - 2)
        spread = 0.6 + rand.random() * 1.6
        for d in range(3 + rand.randrange(6)):
            ang = rand.random() * 2 * math.pi
            dist = 0 if d == 0 else spread * math.sqrt(rand.random())
            u = min(max(u0 + math.cos(ang) * dist, 0.6), size[a] - 0.6)
            v = min(max(v0 + math.sin(ang) * dist, 0.6), size[b] - 0.6)
            half = (0.45 + rand.random() * 0.35) if d == 0 else (0.2 + rand.random() * 0.3)
            lo, hi = [0.0, 0.0, 0.0], [0.0, 0.0, 0.0]
            lo[a], hi[a] = u - half, u + half
            lo[b], hi[b] = v - half, v + half
            surface = size[axis] if side > 0 else 0.0
            lo[axis], hi[axis] = (surface - 0.05, surface + 0.12) if side > 0 else (surface - 0.12, surface + 0.05)
            n += 1
            el.setdefault("children", []).append({
                "name": f"glowspot{n}",
                "from": [round(x, 3) for x in lo],
                "to": [round(x, 3) for x in hi],
                "faces": {f[0]: {"texture": "#glowdot", "uv": [0, 0, 4, 4], "glow": SPOT_GLOW}
                          for f in FACE_AXES if f[0] != {"east": "west", "west": "east", "up": "down",
                                                           "down": "up", "south": "north", "north": "south"}[fname]},
            })


def spine_numbers(out, root):
    """Pivot arcs, link elevations, piece extents and the hitbox table."""
    pivots = [p for _, p in spine_report(out, root)]
    # The last bone has no child joint: its link repeats the one before.
    pivots.append(add(pivots[-1], sub(pivots[-1], pivots[-2])))
    arcs = [0.0]
    for a, b in zip(pivots, pivots[1:]):
        arcs.append(arcs[-1] + math.dist(a, b) / 16)
    elev = [math.atan2(b[1] - a[1], math.hypot(b[0] - a[0], b[2] - a[2])) for a, b in zip(pivots, pivots[1:])]

    def rel(p):
        return ((p[0] - ROOT_AT[0]) / 16, (p[1] - ROOT_AT[1]) / 16, (p[2] - ROOT_AT[2]) / 16)

    piv = [rel(p) for p in pivots]
    boxes = [(bone, rel(lo), rel(hi)) for bone, lo, hi in solid_boxes(root)]
    drawn = [(bone, rel(lo), rel(hi)) for bone, lo, hi in solid_boxes(root, solid=False)]

    # Each piece's solid body around its own pivot: along, below, above,
    # sideways. A piece rests on this; its fins can sink into the sand.
    extents = []
    for i, name in enumerate(SPINE):
        own = [(lo, hi) for bone, lo, hi in boxes if bone == name]
        p = piv[i]
        extents.append([
            min(lo[0] for lo, _ in own) - p[0], max(hi[0] for _, hi in own) - p[0],
            min(lo[1] for lo, _ in own) - p[1], max(hi[1] for _, hi in own) - p[1],
            max(max(abs(lo[2] - p[2]), abs(hi[2] - p[2])) for lo, hi in own),
        ])

    # The spine line's rest height at arc s, between the pivots (and on
    # past the mouth for the snout).
    def spine_y(s):
        for k in range(len(arcs) - 1):
            if s <= arcs[k + 1] or k == len(arcs) - 2:
                t = (s - arcs[k]) / (arcs[k + 1] - arcs[k])
                return piv[k][1] + (piv[k + 1][1] - piv[k][1]) * t
        return piv[-1][1]

    # The hitboxes take in everything drawn there but the whiskers, which
    # would stretch the head's boxes 48 blocks wide.
    def section(a, b):
        hit = [(lo, hi) for _, lo, hi in drawn if lo[0] < b and hi[0] > a]
        if not hit:
            return None
        mid = spine_y((a + b) / 2)
        return (2 * max(max(abs(lo[2]), abs(hi[2])) for lo, hi in hit),
                min(lo[1] for lo, _ in hit) - mid, max(hi[1] for _, hi in hit) - mid)

    front = min(lo[0] for _, lo, _ in drawn)
    back = max(hi[0] for _, _, hi in drawn)
    proxies = []
    s = front + PROXY_STEP_MIN / 2
    while s < back:
        here = section(s - 0.05, s + 0.05) or section(s - PROXY_STEP_MAX, s + PROXY_STEP_MAX)
        step = min(PROXY_STEP_MAX, max(PROXY_STEP_MIN, 0.8 * here[0]))
        width, low, high = section(s - step / 2, s + step / 2) or here
        proxies.append([s, max(width, step), low, high])
        s += step
    return arcs, elev, extents, proxies


def patch_entity(values):
    """Writes each value over the same key's value in the entity file."""
    with open(ENTITY, encoding="utf-8") as f:
        text = f.read()
    for key, value in values.items():
        pattern = re.compile(r'^([ \t]*)("' + key + r'"\s*:\s*)\[(?:[^\[\]]|\[[^\[\]]*\])*\]', re.M)
        m = pattern.search(text)
        if m is None:
            raise SystemExit(f"{ENTITY} has no \"{key}\" to update")
        indent = m.group(1)

        def row(v):
            return "[" + ", ".join(f"{x:.4g}" if isinstance(x, float) else str(x) for x in v) + "]"

        if value and isinstance(value[0], list):
            inner = indent + "    "
            body = "[\n" + ",\n".join(inner + row(v) for v in value) + "\n" + indent + "]"
        else:
            body = row(value)
        text = text[:m.start()] + m.group(1) + m.group(2) + body + text[m.end():]
    with open(ENTITY, "w", encoding="utf-8") as f:
        f.write(text)


def write_shape(path, shape):
    text = json.dumps(shape, indent="\t")
    # Keep number arrays on one line, as the model creator writes them.
    text = re.sub(
        r"\[([-\d.eE,\s]*)\]",
        lambda m: "[" + ", ".join(p.strip() for p in m.group(1).split(",")) + "]",
        text)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
        f.write("\n")


def main():
    shape = load(SRC)
    root, absf, mouth, shift = rebuild(shape)
    out = {
        "editor": shape.get("editor", {}),
        "textureWidth": shape["textureWidth"],
        "textureHeight": shape["textureHeight"],
        "textureSizes": shape["textureSizes"],
        # The rust serpent's shape leaves this path empty; the giant's
        # creature icon resolves it and warns, so name the real texture.
        "textures": {"serpent.png": "underwaterhorrors:entity/serpent"},
        "elements": [root],
        "animations": animations(shape),
    }
    write_shape(DST, out)
    write_pieces(out)

    arcs, elev, extents, proxies = spine_numbers(out, root)
    patch_entity({"spineArcs": arcs, "spineElev": elev, "pieceExtents": extents, "proxies": proxies})
    print(f"wrote {len(SPINE)} piece shapes to {PIECE_DIR}")
    print(f"wrote {len(proxies)} hitboxes and the spine numbers to {ENTITY}")

    pivots = spine_report(out, root)
    print(f"wrote {DST}")
    print(f"mouth at {mouth} in the rust serpent's model space")
    total = 0.0
    prev = None
    for name, p in pivots:
        if prev is not None:
            total += math.dist(prev, p)
        print(f"  {name:8s} pivot {tuple(round(v, 2) for v in p)}  arc {total / 16:.3f} blocks")
        prev = p
    tail_tip = 84.0  # tail20's fin reaches this far past its pivot
    print(f"spine end (tail tip) at {(total + tail_tip) / 16:.3f} blocks from the mouth, at size 1")


if __name__ == "__main__":
    os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    main()
