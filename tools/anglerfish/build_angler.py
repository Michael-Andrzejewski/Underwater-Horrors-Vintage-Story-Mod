"""Builds the giant anglerfish: its shapes, texture atlas and previews.

The design follows MikeZilla_'s rough (a gigantic whipnose angler, big
enough to swallow a sailboat, with a lure longer than its body) and
Soareverix's sketch (rust-brown hide, a jutting lower jaw full of pale
fangs, a milky eye, spiny fin rays, and a glowing temporal gear for a lure).

Everything is cuboids, so the game and VS Model Creator open it as it is:

- The hide is faceted: each body section is a ring of tilted plates around
  an egg-shaped cross section, so the silhouette reads round from any side.
- The upper head and the lower jaw are separate half rings. The jaw hinges
  far back, so the gape is most of the head. A palate, a tongue and a dark
  throat wall fill the mouth when it opens.
- Fins are thin cards with a cut-out outline and painted rays.
- The texture is painted from 3D positions: every texel knows where it
  sits on the fish, so patterns run across faces without seams.

Outputs, all sharing one atlas:

    shapes/entity/anglerfish.json            display model, full lure rod
    shapes/entity/anglerfish-live.json       hostile body, lure stub only
    shapes/entity/anglerfishlure.json        the glowing gear at the rope's end
    shapes/entity/anglerfishluresegment.json one rope piece (size 1)
    textures/entity/anglerfish.png

Units: model units, 1/16 block at size 1. The entity renders at size 2,
so 8 units are one block. Model -X is forward (the AI's convention), +Y up,
and the fish is centred on z = 8, the entity's own centre.

Run from the repo root:  python tools/anglerfish/build_angler.py [--preview DIR]
"""

import json
import math
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import vsshape as V  # noqa: E402

SHAPES = "assets/underwaterhorrors/shapes/entity"
TEXTURE = "assets/underwaterhorrors/textures/entity/anglerfish.png"
TEX = "anglerfish"
TEX_PATH = "underwaterhorrors:entity/anglerfish"
ATLAS_W = 1024
DENSITY = 1.0          # texels per model unit
CZ = 8.0               # centre line, z
D2R = math.pi / 180


# ══ Geometry ════════════════════════════════════════════════════════════

def norm(v):
    v = np.asarray(v, float)
    n = np.linalg.norm(v)
    return v / n if n > 1e-12 else v


def euler_xyz(R):
    """Angles (degrees) with R = Rx(a) Ry(b) Rz(c), the game's order."""
    b = math.asin(max(-1.0, min(1.0, R[0, 2])))
    if abs(math.cos(b)) > 1e-6:
        a = math.atan2(-R[1, 2], R[2, 2])
        c = math.atan2(-R[0, 1], R[0, 0])
    else:
        a = math.atan2(R[2, 1], R[1, 1])
        c = 0.0
    return a / D2R, b / D2R, c / D2R


def frame(L, N):
    """Rotation whose local x is L and local y is N (made perpendicular)."""
    L = norm(L)
    N = norm(np.asarray(N, float) - L * float(np.dot(N, L)))
    W = np.cross(L, N)
    return np.column_stack([L, N, W])


class Model:
    """Bones carry no rest rotation, so a part's absolute rest position is
    its parent chain's translations plus its own. Only boxes rotate."""

    def __init__(self):
        self.parts = {}
        self.roots = []

    def _add(self, p, parent):
        if p["name"] in self.parts:
            raise ValueError("duplicate name " + p["name"])
        self.parts[p["name"]] = p
        p["parent"] = parent
        (self.parts[parent]["children"] if parent else self.roots).append(p)
        return p

    def name(self, prefix):
        i = 0
        while f"{prefix}{i}" in self.parts:
            i += 1
        return f"{prefix}{i}"

    def bone(self, name, parent, pivot):
        return self._add({"name": name, "kind": "bone", "pivot": np.asarray(pivot, float), "children": []}, parent)

    def box(self, name, parent, origin, R, lo, hi, mat, faces=None, glow=0, **paint):
        """A box spanning local lo..hi, rotated by R about origin (all
        absolute). faces: which faces to keep (default all)."""
        return self._add({
            "name": name, "kind": "box", "origin": np.asarray(origin, float), "R": R,
            "lo": np.asarray(lo, float), "hi": np.asarray(hi, float), "mat": mat,
            "faces": list(faces or V.FACES), "glow": glow, "paint": paint, "children": [],
        }, parent)

    def aabox(self, name, parent, frm, to, mat, **kw):
        return self.box(name, parent, frm, np.eye(3), (0, 0, 0), np.subtract(to, frm), mat, **kw)

    def beam(self, name, parent, a, b, w, d=None, mat="tooth", up=(0, 1, 0), faces=None, glow=0, **paint):
        """A box along a->b with a w x d cross section."""
        a, b = np.asarray(a, float), np.asarray(b, float)
        d = w if d is None else d
        L = b - a
        length = np.linalg.norm(L)
        upv = np.asarray(up, float)
        if abs(np.dot(norm(L), norm(upv))) > 0.95:
            upv = np.array([1.0, 0, 0]) if abs(L[0]) < abs(L[1]) else np.array([0, 0, 1.0])
        R = frame(L, upv)
        paint.setdefault("a", a)
        paint.setdefault("b", b)
        return self.box(name, parent, a, R, (0, -d / 2, -w / 2), (length, d / 2, w / 2), mat, faces=faces, glow=glow, **paint)

    def plate(self, name, parent, m0, m1, n, width, thick, mat, overlap=1.5, faces=None, **paint):
        """A plate whose outer face runs through m0 -> m1, facing n."""
        m0, m1 = np.asarray(m0, float), np.asarray(m1, float)
        L = norm(m1 - m0)
        start = m0 - L * overlap
        length = np.linalg.norm(m1 - m0) + 2 * overlap
        R = frame(L, n)
        return self.box(name, parent, start, R, (0, -thick, -width / 2), (length, 0, width / 2), mat, faces=faces, **paint)


def pchip(points):
    """Smooth, overshoot-free interpolation through (x, y) points."""
    xs = np.array([p[0] for p in points], float)
    ys = np.array([p[1] for p in points], float)
    h = np.diff(xs)
    dl = np.diff(ys) / h
    m = np.zeros_like(ys)
    for i in range(1, len(xs) - 1):
        if dl[i - 1] * dl[i] > 0:
            w1, w2 = 2 * h[i] + h[i - 1], h[i] + 2 * h[i - 1]
            m[i] = (w1 + w2) / (w1 / dl[i - 1] + w2 / dl[i])
    m[0], m[-1] = dl[0], dl[-1]

    def f(x):
        x = float(np.clip(x, xs[0], xs[-1]))
        i = int(np.clip(np.searchsorted(xs, x) - 1, 0, len(xs) - 2))
        t = (x - xs[i]) / h[i]
        t2, t3 = t * t, t * t * t
        return ((2 * t3 - 3 * t2 + 1) * ys[i] + (t3 - 2 * t2 + t) * h[i] * m[i]
                + (-2 * t3 + 3 * t2) * ys[i + 1] + (t3 - t2) * h[i] * m[i + 1])
    return f


# ── The fish's profile (units; snout at x = -50, caudal base at x = 200) ──

TOP = pchip([(-52, 44), (-46, 56), (-38, 68), (-26, 78), (-10, 86), (8, 91), (28, 92), (48, 89),
             (68, 83), (90, 75), (114, 66), (140, 57), (166, 50), (188, 46), (204, 45)])
HALFW = pchip([(-52, 12), (-46, 20), (-38, 27), (-26, 33), (-10, 37), (8, 39), (28, 39.5), (48, 38),
               (68, 33), (90, 27), (114, 21), (140, 15), (166, 10), (188, 7), (204, 6)])
# Upper lip, sloping up toward the snout; the jaw hinge sits at its back end.
LIP = pchip([(-52, 41), (-30, 39), (0, 37), (24, 35), (40, 34)])
HINGE = np.array([40.0, 34.0, CZ])
# Lower jaw: juts 8 units past the snout and curls up at the chin. It is
# wider than the upper head, so its rim closes outside the upper lip.
JAW_LIP = pchip([(-60, 46), (-54, 43), (-44, 40), (-20, 38.5), (10, 36), (40, 34)])
JAW_BOT = pchip([(-60, 34), (-54, 23), (-44, 13), (-24, 5), (0, 2), (24, 1), (40, 3)])
JAW_HALFW = pchip([(-60, 10), (-54, 18), (-44, 25), (-28, 36), (-8, 41), (14, 43), (40, 42)])
# Belly behind the hinge.
BELLY = pchip([(30, 3), (48, 4), (64, 6), (88, 11), (112, 19), (140, 29), (166, 37), (188, 42), (204, 43)])


def body_center(x):
    return (TOP(x) + BELLY(x)) / 2


def egg(phi, cy, a, hu, hd):
    """Point (y, z) on an egg cross section: hu above cy, hd below."""
    s, c = math.sin(phi), math.cos(phi)
    return np.array([cy + (hu if s > 0 else hd) * s, CZ + a * c])


def ring(m, parent, prefix, x0, x1, prof, phis, mat, thick=4.0, inner=None, overlap=1.5, **paint):
    """Plates around the cross section between x0 and x1. prof(x) gives
    (cy, a, hu, hd); phis are polygon angles in degrees, in order."""
    out = []
    for i in range(len(phis) - 1):
        f0, f1 = phis[i] * D2R, phis[i + 1] * D2R
        ends = []
        for x in (x0, x1):
            cy, a, hu, hd = prof(x)
            pa, pb = egg(f0, cy, a, hu, hd), egg(f1, cy, a, hu, hd)
            mid = (pa + pb) / 2
            e = pb - pa
            n = np.array([e[1], -e[0]])        # perpendicular in (y, z)
            if np.dot(n, mid - np.array([cy, CZ])) < 0:
                n = -n
            ends.append((np.array([x, mid[0], mid[1]]), np.array([0.0, n[0], n[1]]), np.linalg.norm(e)))
        (m0, n0, w0), (m1, n1, w1) = ends
        nrm = norm(norm(n0) + norm(n1))
        faces = ["up", "east", "west", "north", "south"] + (["down"] if inner else [])
        out.append(m.plate(m.name(prefix), parent, m0, m1, nrm, max(w0, w1) + 1.2, thick, mat,
                           overlap=overlap, faces=faces, inner=inner, **paint))
    return out


# ══ The anglerfish ══════════════════════════════════════════════════════

UPPER_PHIS = [-14, 14, 38, 62, 90, 118, 142, 166, 194]
JAW_PHIS = [166, 196, 222, 248, 270, 292, 318, 344, 374]
BODY_PHIS = [0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330, 360]
TAIL_PHIS = [0, 40, 80, 100, 140, 180, 220, 260, 280, 320, 360]

BODY_JOINTS = [44, 72, 100, 128, 154, 180]      # body0..body4, tail


def head_prof(x):
    lip = LIP(x)
    return lip, HALFW(x), TOP(x) - lip, 4.0


def jaw_prof(x):
    jl = JAW_LIP(x)
    return jl, JAW_HALFW(x), 4.0, jl - JAW_BOT(x)


def body_prof(x):
    top, bot = TOP(x), BELLY(x)
    cy = body_center(x) - 2
    return cy, HALFW(x), top - cy, cy - bot


def surface_point(x, phi_deg, prof):
    cy, a, hu, hd = prof(x)
    y, z = egg(phi_deg * D2R, cy, a, hu, hd)
    return np.array([x, y, z])


def surface_normal(x, phi_deg, prof):
    cy, a, hu, hd = prof(x)
    s, c = math.sin(phi_deg * D2R), math.cos(phi_deg * D2R)
    h = hu if s > 0 else hd
    return norm([0.0, s / h, c / a])


def build(display=True, lure_stub=2):
    m = Model()
    root = m.bone("angler", None, (64, 44, CZ))
    head = m.bone("head", "angler", (44, 46, CZ))

    # ── Upper head: half rings above the lip, palate underneath ─────────
    xs = [-50, -42, -32, -20, -6, 8, 22, 36, 48]
    for x0, x1 in zip(xs, xs[1:]):
        ring(m, "head", "skull", x0, x1, head_prof, UPPER_PHIS, "skin", thick=4, inner="flesh", region="head")
        lip0, lip1 = LIP(x0), LIP(x1)
        a = min(HALFW(x0), HALFW(x1))
        m.plate(m.name("palate"), "head", (x0, lip0 + 1.2, CZ), (x1, lip1 + 1.2, CZ), (0, -1, 0), 2 * a - 4, 5,
                "flesh", overlap=1.0, faces=["up", "down", "north", "south", "east", "west"], region="palate")
    # Snout cap.
    m.aabox(m.name("snout"), "head", (-53, LIP(-50) - 1, CZ - 8), (-47, TOP(-50) - 1, CZ + 8), "skin", region="head")
    m.aabox(m.name("snout"), "head", (-52.5, LIP(-50) - 1, CZ - 11), (-47, TOP(-50) - 5, CZ + 11), "skin", region="head")

    # Throat: a dark wall at the back of the mouth.
    m.aabox("throat", "head", (34, 6, CZ - 26), (40, 33, CZ + 26), "throat")

    # Eyes: milky, set in a dark socket above the corner of the mouth.
    for side, sname in ((1, "R"), (-1, "L")):
        phi = 24 if side > 0 else 156
        x = 6.0
        p = surface_point(x, phi, head_prof)
        n = surface_normal(x, phi, head_prof)
        R = frame((1, 0, 0), n)
        m.box(f"eye{sname}", "head", p, R, (-9, -2, -9), (9, 1.6, 9), "eye", center=p + n * 1.6, normal=n,
              glow=80)
        # A heavy brow ridge.
        pb = surface_point(x - 2, phi + (14 if side > 0 else -14), head_prof)
        nb = surface_normal(x - 2, phi + (14 if side > 0 else -14), head_prof)
        m.box(m.name("brow"), "head", pb, frame((1, 0.05, 0), nb), (-12, -3, -4), (11, 2.0, 4), "skin", region="head")

    # Gill covers: crescents behind the eye, hinged at their front edge.
    for side, sname in ((1, "R"), (-1, "L")):
        phi = 4 if side > 0 else 176
        x = 46.0
        p = surface_point(x, phi, body_prof)
        n = surface_normal(x, phi, body_prof)
        g = m.bone(f"gill{sname}", "head", (x - 6, p[1], p[2]))
        R = frame((1, 0, 0), n)
        m.box(f"operculum{sname}", g["name"], p + n * 0.6, R, (-6, -2, -24), (12, 1.4, 22), "gillcover",
              center=p, normal=n, side=side)

    # ── Lower jaw ────────────────────────────────────────────────────────
    jaw = m.bone("jaw", "head", HINGE)
    jxs = [-60, -54, -46, -36, -24, -10, 6, 22, 40]
    for x0, x1 in zip(jxs, jxs[1:]):
        ring(m, "jaw", "mandible", x0, x1, jaw_prof, JAW_PHIS, "skin", thick=4, inner="gum", region="jaw")
        # Tongue / floor of the mouth.
        y0 = JAW_BOT(x0) + 0.55 * (JAW_LIP(x0) - JAW_BOT(x0))
        y1 = JAW_BOT(x1) + 0.55 * (JAW_LIP(x1) - JAW_BOT(x1))
        # The bowl narrows with depth: 45% down, it is sqrt(1 - 0.45^2) wide.
        a = min(JAW_HALFW(x0), JAW_HALFW(x1)) * 0.893 * 0.88
        m.plate(m.name("tongue"), "jaw", (x0, y0, CZ), (x1, y1, CZ), (0, 1, 0), 2 * a, 5, "flesh",
                overlap=1.0, region="tongue")
    for hw, y0, y1 in ((9.5, JAW_LIP(-60) - 7, JAW_LIP(-60) - 0.5), (6.5, JAW_BOT(-59) + 0.5, JAW_LIP(-60) - 3)):
        m.aabox(m.name("chin"), "jaw", (-62, y0, CZ - hw), (-56, y1, CZ + hw), "skin", region="jaw")

    # ── Teeth ────────────────────────────────────────────────────────────
    def fang(parent, base, direction, length, root_w, prefix, curl=(0.25, 0, 0)):
        d = norm(direction)
        mid = base + d * length * 0.48
        tip_dir = norm(d + np.asarray(curl, float))
        tip = mid + tip_dir * length * 0.58
        m.beam(m.name(prefix + "root"), parent, base - d * 2, mid, root_w, mat="tooth", t0=0.0, t1=0.5)
        m.beam(m.name(prefix + "tip"), parent, mid - d * 0.5, tip, root_w * 0.55, mat="tooth", t0=0.45, t1=1.0)

    rng = np.random.default_rng(7)

    def spaced(a, b, n, jitter):
        xs = np.linspace(a, b, n)
        return xs + rng.uniform(-jitter, jitter, n) * (b - a) / n

    # Upper: hidden in the jaw when it is shut, a wall of fangs when open.
    for side in (1, -1):
        for x in spaced(-45, 24, 11, 0.35):
            a = HALFW(x) - 4
            base = np.array([x, LIP(x) + 2, CZ + side * a])
            k = (x + 45) / 69
            length = 18 - 11 * k + rng.uniform(-3, 3)
            d = (0.25 + rng.uniform(-0.2, 0.2), -1, -side * rng.uniform(0.1, 0.45))
            fang("head", base, d, length, 3.2 - 1.0 * k, "uptooth", curl=(0.5, 0, -side * 0.15))
    for z in (-9, -4.5, 0, 4.5, 9):
        base = np.array([-47.5, LIP(-48) + 2, CZ + z + rng.uniform(-1, 1)])
        fang("head", base, (0.1 + rng.uniform(-0.1, 0.15), -1, -z * 0.03), 19 + rng.uniform(-2, 4), 3.4,
             "uptooth", curl=(0.55, 0, 0))
    # Lower: jutting fangs at the chin, then a crooked row up each side,
    # leaning out over the upper lip.
    for z in (-6.5, -2.5, 2.5, 6.5):
        base = np.array([-57.5, JAW_LIP(-57.5) - 1.5, CZ + z])
        fang("jaw", base, (-0.75 + rng.uniform(-0.15, 0.15), 1, z * 0.08), 23 + (4 if abs(z) < 3 else 0) +
             rng.uniform(-2, 2), 3.8, "lowtooth", curl=(0.4, 0, 0))
    for side in (1, -1):
        for x in spaced(-52, 22, 10, 0.35):
            a = JAW_HALFW(x) - 3
            base = np.array([x, JAW_LIP(x) - 2.5, CZ + side * a])
            k = (x + 52) / 74
            length = 21 - 13 * k + rng.uniform(-3, 3)
            d = (-0.15 + rng.uniform(-0.3, 0.3), 1, side * (0.15 + rng.uniform(0, 0.3)))
            fang("jaw", base, d, length, 3.6 - 1.4 * k, "lowtooth", curl=(0.3, 0, -side * 0.35))
        # A shorter inner row.
        for x in spaced(-44, 8, 6, 0.3):
            a = JAW_HALFW(x) - 9
            base = np.array([x, JAW_LIP(x) - 3, CZ + side * a])
            fang("jaw", base, (0.1, 1, side * 0.05), 8 + rng.uniform(-1, 3), 2.2, "innertooth", curl=(0.4, 0, 0))

    # Bristles: dark spines along the chin and jawline.
    for i in range(10):
        t = i / 9 + rng.uniform(-0.04, 0.04)
        x = -58 + t * 64
        side = 1 if i % 2 else -1
        phi = 270 + side * (35 + 40 * t)
        base = surface_point(x, phi, jaw_prof)
        d = norm(surface_normal(x, phi, jaw_prof) + np.array([rng.uniform(-0.3, 0.5), -0.6, 0]))
        m.beam(m.name("bristle"), "jaw", base - d * 2, base + d * (7 + 8 * rng.random()), 1.2, mat="bristle")

    # ── Body ─────────────────────────────────────────────────────────────
    parent = "head"
    ranges = [(30, 72), (72, 100), (100, 128), (128, 154), (154, 180), (180, 206)]
    names = ["body0", "body1", "body2", "body3", "body4", "tail"]
    for (x0, x1), bname, pivot_x in zip(ranges, names, BODY_JOINTS):
        m.bone(bname, parent, (pivot_x, body_center(pivot_x) - 2, CZ))
        phis = BODY_PHIS if x1 <= 154 else TAIL_PHIS
        cut = np.linspace(x0, x1, 3 if x1 - x0 > 30 else 2)
        for a0, a1 in zip(cut, cut[1:]):
            ring(m, bname, "hide", a0, a1, body_prof, phis, "skin", thick=4 if x0 < 150 else 3, region="body")
        cy, a, hu, hd = body_prof((x0 + x1) / 2)
        m.aabox(m.name("core"), bname, (x0, cy - hd * 0.62, CZ - a * 0.62), (x1, cy + hu * 0.62, CZ + a * 0.62),
                "skin", region="core")
        parent = bname

    # Pectoral fins: fans behind the gill covers.
    for side, sname in ((1, "R"), (-1, "L")):
        p = surface_point(62, 330 if side > 0 else 210, body_prof)
        f = m.bone(f"fin{sname}", "body0", p)
        d = norm([1, -0.45, side * 0.55])
        R = frame(d, [0, 0.35, -side * 1.0])
        m.box(f"pectoral{sname}", f["name"], p, R, (-2, -0.6, -16), (40, 0.6, 16), "fin", fan=(p, d), spread=55,
              reach=40)
        for k in range(6):
            ang = (-24 + k * 10) * D2R
            Rd = R @ np.array([math.cos(ang), 0, math.sin(ang)])
            tip = p + Rd * (44 + 4 * math.cos(ang * 3))
            m.beam(m.name("pecspine"), f["name"], p + Rd * 30, tip, 1.0, mat="ray")

    # Dorsal and anal fins: tall spiny fans on the rear body.
    for fname, sign in (("dorsal", 1), ("anal", -1)):
        x = 138 if sign > 0 else 142
        y = TOP(x) - 2 if sign > 0 else BELLY(x) + 2
        f = m.bone(fname, "body3", (x, y, CZ))
        d = norm([0.8, sign * 1.0, 0])
        R = frame(d, [0, 0, 1])
        m.box(f"{fname}fin", fname, (x, y, CZ), R, (-6, -0.6, -0.6), (40, 0.6, 0.6), "skin", region="body",
              faces=[])
        m.box(f"{fname}card", fname, (x, y, CZ), frame([1, 0, 0], [0, 0, 1]),
              (-8, -0.6, -36) if sign > 0 else (-8, -0.6, 0), (38, 0.6, 0) if sign > 0 else (38, 0.6, 36),
              "fin", faces=["up", "down"], fan=((x - 8, y - sign * 3, CZ), np.array([0.75, sign * 1.0, 0])),
              spread=42, reach=36, membrane=0.75)
        for k in range(5):
            base = np.array([x - 4 + k * 8, y - sign * 1, CZ])
            tip = base + norm([0.55 + k * 0.1, sign * 1.0, 0]) * (40 - k * 4)
            m.beam(m.name(fname + "spine"), fname, base, tip, 1.2, mat="ray")

    # Caudal fin: a fan of long rays, two of them whip-long filaments.
    tail = m.bone("caudal", "tail", (204, body_center(204) - 2, CZ))
    cx, cy = 204, body_center(204) - 2
    m.box("caudalcard", "caudal", (cx - 4, cy, CZ), frame([1, 0, 0], [0, 0, 1]), (0, -0.6, -40), (72, 0.6, 40),
          "fin", faces=["up", "down"], fan=((cx - 6, cy, CZ), np.array([1.0, 0, 0])), spread=34, reach=74,
          forked=True, membrane=0.55)
    for k in range(11):
        ang = (-30 + k * 6) * D2R
        reach = 78 - 10 * math.cos((k - 5) / 5 * math.pi / 2) + rng.uniform(-4, 4)
        tip = np.array([cx + math.cos(ang) * reach, cy + math.sin(ang) * reach * 1.15, CZ])
        m.beam(m.name("caudalray"), "caudal", (cx, cy, CZ), tip, 1.4 if k % 5 == 0 else 1.1, mat="ray")
    for fname, ang in (("filU", 22), ("filD", -26)):
        a0 = np.array([cx + 70 * math.cos(ang * D2R), cy + 70 * 1.15 * math.sin(ang * D2R), CZ])
        f1 = m.bone(fname + "a", "caudal", a0)
        a1 = a0 + norm([1, math.tan(ang * D2R) * 0.6, 0]) * 48
        m.beam(m.name(fname), f1["name"], a0, a1, 1.2, mat="ray")
        f2 = m.bone(fname + "b", f1["name"], a1)
        a2 = a1 + norm([1, math.tan(ang * D2R) * 0.2, 0]) * 52
        m.beam(m.name(fname), f2["name"], a1, a2, 0.9, mat="ray")

    # ── Lure: the rod from the snout and the gear at its end ─────────────
    lure_path = rod_path()
    count = len(lure_path) - 1 if display else lure_stub
    parent = "head"
    for i in range(count):
        a, b = lure_path[i], lure_path[i + 1]
        bn = m.bone(f"lure{i}", parent, a)
        w = 5.0 - 2.6 * i / (len(lure_path) - 2)
        m.beam(m.name("rod"), bn["name"], a - norm(b - a) * 0.8, b + norm(b - a) * 0.8, w, mat="rod", up=(0, 0, 1))
        parent = bn["name"]
    if display:
        g = m.bone("gear", parent, lure_path[-1])
        build_gear(m, g["name"], lure_path[-1] + np.array([0, -17, 0]), stalk_top=lure_path[-1])
    return m


def rod_path():
    """Joints of the lure rod: up from the snout, arcing forward and down."""
    pts = [(-44, 48), (-42, 62), (-38, 78), (-36, 96), (-40, 114), (-52, 130), (-70, 140), (-92, 143),
           (-114, 139), (-134, 128), (-148, 112), (-154, 96)]
    return [np.array([x, y, CZ]) for x, y in pts]


def build_gear(m, parent, c, stalk_top=None, prefix="", axis=(-0.82, 0, -0.57)):
    """A glowing temporal gear on a fleshy stalk, with frayed filaments
    hanging below. axis: the gear's face normal."""
    ax = norm(axis)
    e1 = np.array([0.0, 1.0, 0.0])
    e2 = np.cross(ax, e1)
    if stalk_top is not None:
        m.beam(prefix + "gearstalk", parent, stalk_top, c + e1 * 12, 3.0, mat="rod", up=(0, 0, 1))
    for k, turn in enumerate((0.0, math.pi / 4)):
        u = math.cos(turn) * e2 + math.sin(turn) * e1
        v = np.cross(ax, u)
        m.box(prefix + f"geardisc{k}", parent, c, np.column_stack([u, v, ax]), (-10.6, -10.6, -2.4),
              (10.6, 10.6, 2.4), "gear", glow=255, center=c, axis=ax)
    for k in range(8):
        ang = (k + 0.5) * 2 * math.pi / 8
        rdir = math.cos(ang) * e1 + math.sin(ang) * e2
        tdir = np.cross(rdir, ax)
        m.box(m.name(prefix + "geartooth"), parent, c + rdir * 14.2, np.column_stack([tdir, rdir, ax]),
              (-2.6, -4.6, -2.0), (2.6, 0, 2.0), "gear", glow=255, center=c, axis=ax)
    rng = np.random.default_rng(3)
    for k in range(5):
        off = -7 + k * 3.5
        top = c - e1 * 10.5 + e2 * off + ax * rng.uniform(-1, 1)
        m.beam(m.name(prefix + "lurefil"), parent, top,
               top + np.array([rng.uniform(-1.5, 1.5), -9 - 7 * rng.random(), rng.uniform(-1.5, 1.5)]),
               0.9, mat="filament")


def build_lure_entity():
    """The live lure: the gear alone, centred on the entity."""
    m = Model()
    m.bone("lurebody", None, (8, 0, 8))
    build_gear(m, "lurebody", np.array([8.0, -2.0, 8.0]), stalk_top=np.array([8.0, 12.0, 8.0]), prefix="",
               axis=(-1, 0, 0))
    return m


def build_segment():
    """One rope piece of the live lure, 4 x 65 units, upright (size 1)."""
    m = Model()
    m.box("lure-segment", None, (6, -0.5, 6), np.eye(3), (0, 0, 0), (4, 65, 4), "rope")
    return m


# ══ Export ══════════════════════════════════════════════════════════════

def face_dims(p, fname):
    s = np.abs(p["hi"] - p["lo"])
    if fname in ("north", "south"):
        return s[0], s[1]
    if fname in ("east", "west"):
        return s[2], s[1]
    return s[0], s[2]


def to_vs(m, uvs):
    """Shape elements, with "from"/"rotationOrigin" relative to the parent."""
    def conv(p, ppivot):
        if p["kind"] == "bone":
            rel = (p["pivot"] - ppivot).round(4).tolist()
            el = {"name": p["name"], "from": rel, "to": rel, "rotationOrigin": rel}
            kids = [conv(c, p["pivot"]) for c in p["children"]]
            if kids:
                el["children"] = kids
            return el
        rx, ry, rz = euler_xyz(p["R"])
        o = p["origin"] - ppivot
        el = {"name": p["name"],
              "from": (o + p["lo"]).round(4).tolist(),
              "to": (o + p["hi"]).round(4).tolist(),
              "rotationOrigin": o.round(4).tolist()}
        for k, v in (("rotationX", rx), ("rotationY", ry), ("rotationZ", rz)):
            if abs(v) > 1e-6:
                el[k] = round(v, 4)
        faces = {}
        for f in p["faces"]:
            uv = uvs.get((p["name"], f))
            if uv is None:
                continue
            face = {"texture": "#" + TEX, "uv": [float(u) for u in uv]}
            if p["glow"]:
                face["glow"] = int(p["glow"])
            faces[f] = face
        el["faces"] = faces
        return el
    return [conv(r, np.zeros(3)) for r in m.roots]


def pack(models):
    """Shelf-packs every face of every model into one atlas. Returns
    {(name, face): [u1, v1, u2, v2]} per model, and the atlas height."""
    rects = []
    for mi, m in enumerate(models):
        for p in m.parts.values():
            if p["kind"] != "box":
                continue
            for f in p["faces"]:
                w, h = face_dims(p, f)
                tw = max(1, int(math.ceil(w * DENSITY - 0.25)))
                th = max(1, int(math.ceil(h * DENSITY - 0.25)))
                rects.append((mi, p["name"], f, tw, th))
    rects.sort(key=lambda r: (-r[4], -r[3]))
    out = [dict() for _ in models]
    x = y = shelf = 0
    pad = 1
    for mi, name, f, tw, th in rects:
        if x + tw + 2 * pad > ATLAS_W:
            x, y, shelf = 0, y + shelf, 0
        out[mi][(name, f)] = [x + pad, y + pad, x + pad + tw, y + pad + th]
        x += tw + 2 * pad
        shelf = max(shelf, th + 2 * pad)
    height = y + shelf
    h2 = 1
    while h2 < height:
        h2 *= 2
    return out, h2


def shape_json(m, uvs, height, animations):
    return {
        "editor": {"allAngles": True, "entityTextureMode": True},
        "textureWidth": ATLAS_W, "textureHeight": height,
        "textureSizes": {TEX: [ATLAS_W, height]},
        "textures": {TEX: TEX_PATH},
        "elements": to_vs(m, uvs),
        "animations": animations,
    }


# ══ Painting ════════════════════════════════════════════════════════════

def _hash(ix, iy, iz, seed):
    h = (ix.astype(np.int64) * 73856093) ^ (iy.astype(np.int64) * 19349663) ^ (iz.astype(np.int64) * 83492791)
    h = (h ^ (seed * 2654435761)) & 0xFFFFFFFF
    h = (h ^ (h >> 13)) * 1274126177 & 0xFFFFFFFF
    h = h ^ (h >> 16)
    return (h & 0xFFFFFF) / float(0xFFFFFF)


def vnoise(P, freq, seed=0):
    Q = P * freq
    i = np.floor(Q).astype(np.int64)
    f = Q - i
    f = f * f * (3 - 2 * f)
    out = 0
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                w = (f[:, 0] if dx else 1 - f[:, 0]) * (f[:, 1] if dy else 1 - f[:, 1]) * (f[:, 2] if dz else 1 - f[:, 2])
                out = out + w * _hash(i[:, 0] + dx, i[:, 1] + dy, i[:, 2] + dz, seed)
    return out


def fbm(P, freq, octaves=4, seed=0):
    total, amp, norm_ = 0, 1.0, 0
    for o in range(octaves):
        total = total + amp * vnoise(P, freq * 2 ** o, seed + o * 17)
        norm_ += amp
        amp *= 0.5
    return total / norm_


def ramp(hexes):
    return np.array([[int(h[i:i + 2], 16) for i in (1, 3, 5)] for h in hexes], float)


PAL = {
    "skin": ramp(["#1a0d0c", "#271310", "#351a15", "#45211a", "#552920", "#663226", "#783c2e", "#8b4838", "#9f5745"]),
    "belly": ramp(["#1d1311", "#2b1c18", "#3a2620", "#4b3129", "#5d3d33", "#70493d", "#845847"]),
    "flesh": ramp(["#0b0405", "#160709", "#230b0f", "#330f15", "#45151c", "#5a1d24", "#70272d"]),
    "gum": ramp(["#2a0d10", "#3e1418", "#561d22", "#6e282c", "#873638", "#9c4643"]),
    "throat": ramp(["#020101", "#060203", "#0d0405", "#160709"]),
    "tooth": ramp(["#3d342e", "#5a524a", "#7a746b", "#9a958b", "#b7b3a8", "#d1cec4", "#e8e6de"]),
    "eye": ramp(["#4e4f4e", "#6c6e6c", "#8d8f8c", "#aaaca8", "#c4c5c0", "#d9d9d3", "#ecece6"]),
    "socket": ramp(["#0c0606", "#160b0a", "#22110e", "#2e1813"]),
    "fin": ramp(["#1a0f0d", "#26150f", "#331c15", "#43251c", "#542f23", "#673a2b"]),
    "ray": ramp(["#3c271e", "#523628", "#6a4734", "#835a43", "#9c7055", "#b68a6c"]),
    "rod": ramp(["#2e1910", "#432415", "#5b311b", "#743f21", "#8d4f29", "#a46234", "#b97843"]),
    "gear": ramp(["#0f4f6a", "#177496", "#22a0c4", "#3fc8e6", "#79e6f7", "#b9f8ff", "#e9ffff"]),
    "gearhub": ramp(["#0a1e33", "#0f2c4a", "#163d63", "#1f4f7d", "#2c6596"]),
    "gearcore": ramp(["#5fd9f5", "#9cf0ff", "#d6fbff", "#ffffff"]),
    "bristle": ramp(["#150c0a", "#22130f", "#311c15", "#40251b"]),
    "filament": ramp(["#2a1712", "#3b2219", "#4e2e21", "#62392a"]),
    "gillcover": ramp(["#2a1814", "#3c231b", "#4f2f24", "#643d2e", "#7d5040", "#9a6a56", "#b88a74"]),
}


def quantize(t, pal, ij, dither=1.0):
    n = len(pal)
    b = (_hash(ij[:, 0], ij[:, 1], np.zeros(len(ij), np.int64), 991) - 0.5) * 0.9 * dither
    idx = np.clip(np.floor(t * (n - 1) + 0.5 + b), 0, n - 1).astype(int)
    return pal[idx]


# Scars: a few long scratches across the flanks and head (a, b, width).
SCARS = [((-10, 70, 34), (40, 40, 40), 1.2), ((70, 66, -18), (130, 40, -14), 1.0), ((86, 30, 34), (126, 52, 30), 0.9),
         ((-26, 58, -20), (14, 76, -26), 1.0), ((150, 58, 16), (186, 46, 13), 0.8), ((60, 20, -20), (96, 14, -14), 1.1)]


def seg_dist(P, a, b):
    a, b = np.asarray(a, float), np.asarray(b, float)
    ab = b - a
    t = np.clip(((P - a) @ ab) / (ab @ ab), 0, 1)
    return np.linalg.norm(P - (a + t[:, None] * ab), axis=1), t


def paint_face(p, fname, P, nrm, ij, fu, fv):
    """RGBA for the texels of one face: P (n,3) rest positions, nrm the
    face normal, ij texel coords, fu/fv the position across the face."""
    mat = p["mat"]
    inner = fname == "down" and p["paint"].get("inner")
    if inner:
        mat = p["paint"]["inner"]
    n = len(P)
    alpha = np.full(n, 255.0)
    region = p["paint"].get("region")

    if mat == "skin":
        x, y, z = P[:, 0], P[:, 1], P[:, 2]
        big = fbm(P, 0.035, 3, 1)
        small = fbm(P, 0.16, 2, 5)
        t = 0.42 + (big - 0.5) * 0.55 + (small - 0.5) * 0.25
        # Dark blotches like the sketch's patches.
        blot = fbm(P, 0.05, 3, 9)
        t -= np.clip((blot - 0.58) * 2.2, 0, 0.3)
        # Darker back, faintly lighter flanks.
        cy = np.array([body_center(v) for v in np.clip(x, -50, 204)])
        t += np.clip((cy - y) / 60, -0.12, 0.08)
        # Pores: sparse pale specks.
        speck = _hash(np.floor(P[:, 0] * 1.0).astype(np.int64), np.floor(P[:, 1]).astype(np.int64),
                      np.floor(P[:, 2]).astype(np.int64), 77)
        t += np.where(speck > 0.985, 0.25, 0)
        # Lateral line: a dotted pale row along each flank.
        if region in ("body", "core"):
            ll = np.array([body_center(v) + 2 for v in np.clip(x, -50, 204)])
            dots = (np.abs(y - ll) < 1.0) & (np.mod(np.floor(x), 6) < 2) & (np.abs(z - CZ) > 6)
            t += np.where(dots, 0.28, 0)
        # Gill slit: a dark crescent behind the gill cover.
        gx = 60 + 0.004 * (y - 40) ** 2
        slit = (np.abs(x - gx) < 1.4) & (y > 12) & (y < 70) & (np.abs(z - CZ) > 18)
        t = np.where(slit, 0.02, t)
        for a, b, w in SCARS:
            d, _ = seg_dist(P, a, b)
            t = np.where(d < w, t + 0.32, np.where(d < w + 1.0, t - 0.1, t))
        if region == "jaw":
            lipy = np.array([JAW_LIP(v) for v in np.clip(x, -60, 40)])
            t = np.where(y > lipy - 2.5, t - 0.3, t)
        elif region == "head":
            lipy = np.array([LIP(v) for v in np.clip(x, -52, 40)])
            t = np.where((y < lipy + 2.5) & (np.abs(z - CZ) > 6), t - 0.3, t)
        pal = PAL["skin"]
        if region == "jaw" and nrm[1] < -0.3:
            pal = PAL["belly"]
            t += 0.05
        col = quantize(np.clip(t, 0, 1), pal, ij)
    elif mat in ("flesh", "gum"):
        folds = 0.5 + 0.5 * np.sin(P[:, 0] * 0.55 + fbm(P, 0.2, 2, 3) * 6)
        t = 0.35 + 0.35 * folds + (fbm(P, 0.12, 2, 4) - 0.5) * 0.4
        if region == "tongue":
            t += 0.12
        # Darker toward the throat.
        t -= np.clip((P[:, 0] - 0) / 120, 0, 0.35)
        col = quantize(np.clip(t, 0, 1), PAL[mat], ij)
    elif mat == "throat":
        r = np.hypot((P[:, 1] - 18) / 14, (P[:, 2] - CZ) / 24)
        t = np.clip(r * 0.8, 0, 1)
        col = quantize(t, PAL["throat"], ij)
    elif mat == "tooth":
        a, b = p["paint"]["a"], p["paint"]["b"]
        _, s = seg_dist(P, a, b)
        along = p["paint"]["t0"] + s * (p["paint"]["t1"] - p["paint"]["t0"])
        t = 0.25 + along * 0.75 + (fbm(P, 0.6, 1, 11) - 0.5) * 0.25
        col = quantize(np.clip(t, 0, 1), PAL["tooth"], ij, 0.6)
        # Stained roots.
        stain = along < 0.18
        col = np.where(stain[:, None], col * np.array([0.72, 0.6, 0.52]), col)
    elif mat == "eye":
        c, nv = p["paint"]["center"], p["paint"]["normal"]
        d = P - c
        d -= np.outer(d @ nv, nv)
        r = np.linalg.norm(d, axis=1) / 7.5
        up = d @ np.array([0, 1.0, 0])
        fwd = d @ np.array([-1.0, 0, 0])
        t = 0.92 - r * 0.7 + (fbm(P, 0.5, 2, 21) - 0.5) * 0.2
        # A cataract smear and a hard highlight up front.
        hl = np.hypot(up - 2.4, fwd - 2.2) < 1.5
        t = np.where(hl, 1.0, t)
        col = quantize(np.clip(t, 0, 1), PAL["eye"], ij, 0.5)
        ring_ = (r > 0.86) & (r <= 1.12)
        col = np.where(ring_[:, None], PAL["socket"][1], col)
        lid = (r > 1.12) & (r <= 1.22)
        col = np.where(lid[:, None], PAL["skin"][2], col)
        col = np.where((r > 1.22)[:, None], quantize(np.full(n, 0.38), PAL["skin"], ij), col)
        if fname != "up":
            col = quantize(np.full(n, 0.3), PAL["skin"], ij)
    elif mat == "gillcover":
        side = p["paint"]["side"]
        c = p["paint"]["center"]
        lx = P[:, 0] - c[0]
        ly = P[:, 1] - c[1]
        # Crescent: the area between two arcs, pale along its rear rim.
        outer = ((lx + 4) / 14) ** 2 + (ly / 24) ** 2 < 1
        inner_ = ((lx + 12) / 15) ** 2 + (ly / 26) ** 2 < 1
        shape = outer & ~inner_
        rim = outer & ~(((lx + 5.5) / 12.5) ** 2 + (ly / 22.5) ** 2 < 1)
        t = 0.35 + (fbm(P, 0.15, 2, 31) - 0.5) * 0.5 + np.where(rim, 0.45, 0)
        col = quantize(np.clip(t, 0, 1), PAL["gillcover"], ij)
        if fname in ("up", "down"):
            alpha = np.where(shape, 255, 0)
        else:
            alpha = np.zeros(n)
    elif mat in ("fin",):
        base, d = p["paint"]["fan"]
        base = np.asarray(base, float)
        d = norm(d)
        rel = P - base
        dist = np.linalg.norm(rel, axis=1)
        reach = p["paint"]["reach"]
        spread = p["paint"]["spread"] * D2R
        cosang = np.clip((rel @ d) / np.maximum(dist, 1e-6), -1, 1)
        ang = np.arccos(cosang)
        # Signed angle in the card plane.
        side_ax = np.cross(d, nrm)
        sgn = np.sign(rel @ side_ax)
        sang = ang * sgn
        rays = 0.5 + 0.5 * np.cos(sang * 22)
        edge = reach * (0.82 + 0.18 * np.cos(sang * 22)) * (1 - 0.25 * (np.abs(sang) / spread) ** 2)
        if p["paint"].get("forked"):
            edge *= 1 - 0.32 * np.exp(-(sang / 0.22) ** 2)
        tear = fbm(P, 0.12, 2, 41)
        mem = p["paint"].get("membrane", 1.0)
        edge = edge * mem
        inside = (np.abs(sang) < spread) & (dist < edge * (0.85 + 0.3 * tear)) & (dist > 1)
        t = 0.3 + rays * 0.45 * (dist / reach) + (tear - 0.5) * 0.3
        col = quantize(np.clip(t, 0, 1), PAL["fin"], ij)
        # Bony rays show paler.
        col = np.where((rays > 0.92)[:, None], quantize(np.full(n, 0.5), PAL["ray"], ij), col)
        holes = (fbm(P, 0.3, 1, 43) > 0.74) & (dist > reach * 0.5)
        alpha = np.where(inside & ~holes, 255, 0)
    elif mat == "ray":
        a, b = p["paint"]["a"], p["paint"]["b"]
        _, s = seg_dist(P, a, b)
        t = 0.75 - s * 0.45 + (fbm(P, 0.4, 1, 51) - 0.5) * 0.3
        col = quantize(np.clip(t, 0, 1), PAL["ray"], ij)
    elif mat in ("rod", "rope"):
        along = P[:, 1] if mat == "rope" else (P @ norm(p["paint"]["b"] - p["paint"]["a"]))
        bands = 0.5 + 0.5 * np.sin(along * 0.9)
        t = 0.3 + bands * 0.35 + (fbm(P, 0.25, 2, 61) - 0.5) * 0.35
        if fname in ("down", "south"):
            t -= 0.12
        col = quantize(np.clip(t, 0, 1), PAL["rod"], ij)
    elif mat in ("gear", "gearhub", "gearcore"):
        c, ax = p["paint"]["center"], p["paint"]["axis"]
        d = P - c
        r = np.linalg.norm(d - np.outer(d @ ax, ax), axis=1)
        # Face: bright core, dark blue hub ring, cyan body, darker rim.
        t = 0.45 + 0.4 * np.clip((r - 7) / 8, 0, 1) + (fbm(P, 0.35, 2, 71) - 0.5) * 0.3
        col = quantize(np.clip(t, 0, 1), PAL["gear"], ij, 0.6)
        if fname in ("north", "south", "east", "west"):
            col = quantize(np.clip(t - 0.25, 0, 1), PAL["gear"], ij, 0.6)
        hub = (r > 3.2) & (r < 6.6)
        col = np.where(hub[:, None], quantize(np.clip((r - 3.2) / 3.4, 0, 1), PAL["gearhub"], ij), col)
        core = r <= 3.2
        col = np.where(core[:, None], quantize(np.clip(1 - r / 3.2, 0, 1), PAL["gearcore"], ij), col)
    elif mat in ("bristle", "filament"):
        t = 0.5 + (fbm(P, 0.5, 1, 81) - 0.5) * 0.6
        col = quantize(np.clip(t, 0, 1), PAL[mat], ij)
    else:
        raise ValueError(mat)
    return np.concatenate([np.asarray(col, float), alpha[:, None]], axis=1)


def paint_atlas(models, shapes, height):
    atlas = np.zeros((height, ATLAS_W, 4), float)
    filled = np.zeros((height, ATLAS_W), bool)
    for m, shape in zip(models, shapes):
        for el, fname, pts, uvs, nrm, glow in V.face_quads(shape):
            p = m.parts[el["name"]]
            u1, v1, u2, v2 = el["faces"][fname]["uv"]
            us = np.arange(int(min(u1, u2)), int(max(u1, u2)))
            vs = np.arange(int(min(v1, v2)), int(max(v1, v2)))
            UU, VV = np.meshgrid(us + 0.5, vs + 0.5)
            UV = np.stack([UU.ravel(), VV.ravel()], 1)
            # Affine map from uv to rest position, from three corners.
            T = np.column_stack([uvs[1] - uvs[0], uvs[3] - uvs[0]])
            Pm = np.column_stack([pts[1] - pts[0], pts[3] - pts[0]])
            M = Pm @ np.linalg.inv(T)
            P = pts[0] + (UV - uvs[0]) @ M.T
            fu = (UV[:, 0] - min(u1, u2)) / max(1e-6, abs(u2 - u1))
            fv = (UV[:, 1] - min(v1, v2)) / max(1e-6, abs(v2 - v1))
            ij = np.stack([UU.ravel().astype(int), VV.ravel().astype(int)], 1)
            rgba = paint_face(p, fname, P, nrm, ij, fu, fv)
            atlas[ij[:, 1], ij[:, 0]] = rgba
            filled[ij[:, 1], ij[:, 0]] = True
    # Bleed each island's edge texels into its padding, for mipmaps.
    for _ in range(2):
        grow = atlas.copy()
        gf = filled.copy()
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            src = np.roll(np.roll(atlas, dy, 0), dx, 1)
            sf = np.roll(np.roll(filled, dy, 0), dx, 1)
            take = sf & ~gf
            grow[take] = src[take]
            gf |= take
        atlas, filled = grow, gf
    return np.clip(atlas, 0, 255).astype(np.uint8)


# ══ Animations ═════════════════════════════════════════════════════════

SPINE = ["body0", "body1", "body2", "body3", "body4", "tail", "caudal"]
FILS = ["filUa", "filUb", "filDa", "filDb"]


def rest_pose(display):
    pose = {"angler": {}, "head": {}, "jaw": {"rotationZ": 4}, "gillR": {}, "gillL": {}, "finR": {}, "finL": {},
            "dorsal": {}, "anal": {}}
    for s in SPINE + FILS:
        pose[s] = {}
    for i in range(LURE_JOINTS if display else LURE_STUB):
        pose[f"lure{i}"] = {}
    if display:
        pose["gear"] = {}
    return pose


LURE_JOINTS = len(rod_path()) - 1
LURE_STUB = 2


def wave(pose, phase, amp, lure_amp, display, breathe=0.0, jaw=4.0):
    """Swimming wave down the spine, with fins, gills and lure following."""
    pose["head"]["rotationY"] = -1.2 * amp * math.sin(phase + 0.6)
    for i, s in enumerate(SPINE):
        pose[s]["rotationY"] = amp * (0.8 + i * 0.6) * math.sin(phase - 0.7 * (i + 1))
    for j, f in enumerate(FILS):
        pose[f]["rotationY"] = amp * 10 * math.sin(phase - 5.5 - j * 0.6)
        pose[f]["rotationZ"] = 6 * math.sin(phase - j) * (0.4 + amp)
    pose["finR"]["rotationX"] = 10 * math.sin(phase + 1.0) * (0.5 + amp)
    pose["finL"]["rotationX"] = -10 * math.sin(phase + 1.0) * (0.5 + amp)
    pose["finR"]["rotationY"] = 6 * math.sin(phase) * (0.5 + amp)
    pose["finL"]["rotationY"] = -6 * math.sin(phase) * (0.5 + amp)
    pose["dorsal"]["rotationX"] = 5 * math.sin(phase - 3) * (0.4 + amp)
    pose["anal"]["rotationX"] = -5 * math.sin(phase - 3) * (0.4 + amp)
    g = breathe
    pose["gillR"]["rotationY"] = -g
    pose["gillL"]["rotationY"] = g
    pose["jaw"]["rotationZ"] = jaw
    n = LURE_JOINTS if display else LURE_STUB
    for i in range(n):
        pose[f"lure{i}"]["rotationZ"] = lure_amp * (0.6 + i * 0.25) * math.sin(phase - i * 0.45)
        pose[f"lure{i}"]["rotationX"] = lure_amp * 0.5 * math.sin(phase - i * 0.3 + 1.3)
    if display:
        pose["gear"]["rotationX"] = lure_amp * 4 * math.sin(phase - 2)
        pose["gear"]["rotationZ"] = lure_amp * 2 * math.sin(phase - 3)
    return pose


def clip(code, frames, keys, end="Repeat"):
    for _, els in keys:
        for e in els.values():
            for k in ("rotationX", "rotationY", "rotationZ"):
                e.setdefault(k, 0.0)
            for k in list(e):
                e[k] = round(float(e[k]), 3)
    return {"name": code, "code": code, "version": 0, "quantityframes": frames, "onActivityStopped": "EaseOut",
            "onAnimationEnd": end, "keyframes": [{"frame": f, "elements": els} for f, els in keys]}


def loop_clip(code, frames, steps, fn, display):
    keys = []
    for i in range(steps + 1):
        ph = 2 * math.pi * i / steps
        keys.append((round(i * (frames - 1) / steps), fn(rest_pose(display), ph)))
    return clip(code, frames, keys)


def animations(display):
    A = []
    # Idle: lurking, breathing through the gills, jaw working slowly.
    A.append(loop_clip("idle", 150, 20, lambda p, ph: wave(p, ph, 0.25, 1.0, display,
                       breathe=10 + 10 * math.sin(ph), jaw=5 + 4 * math.sin(ph - 0.8)), display))
    A.append(loop_clip("swim", 90, 18, lambda p, ph: wave(p, ph, 1.0, 1.6, display,
                       breathe=6 + 4 * math.sin(2 * ph), jaw=4 + 2 * math.sin(ph)), display))
    A.append(loop_clip("swimfast", 45, 12, lambda p, ph: wave(p, ph, 1.6, 2.4, display,
                       breathe=14, jaw=8 + 3 * math.sin(ph)), display))
    A.append(loop_clip("lure", 180, 24, lambda p, ph: wave(p, ph, 0.15, 3.0, display,
                       breathe=8 + 8 * math.sin(ph), jaw=10 + 5 * math.sin(ph - 1)), display))

    def strike(f, jaw, pitch, amp, ph):
        p = wave(rest_pose(display), ph, amp, 0.6, display, breathe=12 if jaw > 20 else 4, jaw=jaw)
        p["angler"]["rotationZ"] = pitch
        return (f, p)

    A.append(clip("bite", 76, [strike(0, 4, 0, 0.2, 0), strike(14, 22, -3, 0.5, 0.8), strike(26, 48, -7, 0.8, 1.6),
                               strike(36, 56, -9, 1.0, 2.4), strike(44, 58, -8, 1.0, 3.0), strike(49, -2, 4, 1.2, 3.6),
                               strike(56, 0, 3, 0.8, 4.2), strike(66, 3, 1, 0.4, 5.0), strike(75, 4, 0, 0.2, 6.28)],
                  "Hold"))
    A.append(clip("swallow", 106, [strike(0, 4, 0, 0.2, 0), strike(14, 30, -4, 0.6, 0.8), strike(25, 62, -8, 1.0, 1.6),
                                   strike(40, 64, -10, 1.2, 2.6), strike(48, 64, -9, 1.2, 3.2), strike(57, -2, 5, 1.4, 4.0),
                                   strike(66, 2, 2, 1.0, 4.8), strike(74, -1, 0, 1.2, 5.6), strike(84, 3, 0, 0.8, 6.4),
                                   strike(96, 1, 0, 0.5, 7.2), strike(105, 4, 0, 0.2, 8.0)], "Hold"))

    def hurt(f, bend, jaw):
        p = wave(rest_pose(display), 0, 0, 0.3, display, breathe=16, jaw=jaw)
        for i, s in enumerate(SPINE):
            p[s]["rotationY"] = bend * (0.6 + i * 0.3)
        p["head"]["rotationY"] = -bend * 1.5
        return (f, p)

    A.append(clip("hurt", 28, [hurt(0, 0, 4), hurt(5, 5, 22), hurt(14, -2.5, 14), hurt(27, 0, 4)], "Hold"))

    def die(f, roll, jaw, droop):
        p = wave(rest_pose(display), 0, 0, 0, display, breathe=20, jaw=jaw)
        p["angler"]["rotationX"] = roll
        for i, s in enumerate(SPINE):
            p[s]["rotationZ"] = droop * (0.5 + i * 0.3)
        p["finR"]["rotationX"] = -25
        p["finL"]["rotationX"] = 25
        for i in range(LURE_JOINTS if display else LURE_STUB):
            p[f"lure{i}"]["rotationZ"] = droop * 1.5
        return (f, p)

    A.append(clip("die", 121, [die(0, 0, 4, 0), die(30, 20, 30, 1), die(70, 110, 38, 2.5), die(100, 168, 36, 3),
                               die(120, 180, 34, 3)], "Hold"))
    return A


# ══ Main ═══════════════════════════════════════════════════════════════

def main():
    preview_dir = None
    if "--preview" in sys.argv:
        preview_dir = sys.argv[sys.argv.index("--preview") + 1]
    display = build(display=True)
    live = build(display=False)
    lure = build_lure_entity()
    seg = build_segment()
    models = [display, live, lure, seg]
    uvsets, height = pack(models)
    # The live body shares the display model's UVs, part for part.
    for k, v in uvsets[0].items():
        if k[0] in live.parts:
            uvsets[1][k] = v
    shapes = [
        shape_json(display, uvsets[0], height, animations(True)),
        shape_json(live, uvsets[1], height, animations(False)),
        shape_json(lure, uvsets[2], height, []),
        shape_json(seg, uvsets[3], height, []),
    ]
    atlas = paint_atlas([display, lure, seg], [shapes[0], shapes[2], shapes[3]], height)
    os.makedirs(SHAPES, exist_ok=True)
    for name, sh in zip(["anglerfish", "anglerfish-live", "anglerfishlure", "anglerfishluresegment"], shapes):
        if not sh["animations"]:
            del sh["animations"]
        with open(os.path.join(SHAPES, name + ".json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(sh, f, indent=1)
    Image.fromarray(atlas).save(TEXTURE)
    count = sum(1 for p in display.parts.values() if p["kind"] == "box")
    joints = set()
    for a in shapes[0]["animations"]:
        for k in a["keyframes"]:
            joints.update(k["elements"].keys())
    print(f"display: {count} boxes, {len(joints)} animated joints; atlas {ATLAS_W}x{height}")
    if preview_dir:
        previews(shapes[0], atlas, preview_dir)


def previews(shape, atlas, out):
    os.makedirs(out, exist_ok=True)
    q = V.face_quads(shape)
    pts = np.concatenate([p for _, _, p, _, _, _ in q])
    lo, hi = pts.min(0), pts.max(0)
    print("bounds (blocks at size 2):", ((hi - lo) / 8).round(2))
    c = np.array([60.0, 50.0, CZ])
    views = {
        "side": c + [0, 0, -420],
        "front34": c + [-330, 90, -240],
        "front": c + [-420, 20, 0],
        "top": c + [0, 420, -40],
        "rear34": c + [300, 120, 260],
    }
    for name, eye in views.items():
        V.render(q, atlas, eye, c, size=(1000, 640), fov=42).save(os.path.join(out, f"{name}.png"))


if __name__ == "__main__":
    main()
