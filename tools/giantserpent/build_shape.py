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

Run from the repo root:  python tools/giantserpent/build_shape.py
"""

import copy
import json
import math
import os
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
    text = json.dumps(out, indent="\t")
    # Keep number arrays on one line, as the model creator writes them.
    text = re.sub(
        r"\[([-\d.eE,\s]*)\]",
        lambda m: "[" + ", ".join(p.strip() for p in m.group(1).split(",")) + "]",
        text)
    with open(DST, "w", encoding="utf-8") as f:
        f.write(text)
        f.write("\n")

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
