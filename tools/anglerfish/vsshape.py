"""Vintage Story shape math and a small software renderer for previews.

The transforms copy the game's own (VintagestoryLib ShapeTesselator and
VintagestoryAPI ShapeElement.GetLocalTransformMatrix, animation version 0):

    local = T(rotationOrigin) * Rx * Ry * Rz * T(from - rotationOrigin)

with keyframe rotations added to the element's own and keyframe offsets added
to "from". Children are placed in their parent's frame, so a child's "from"
and "rotationOrigin" are relative to the parent's "from" corner.

Face corners and UVs follow CubeMeshUtil: vertex j of a face gets UV corner
(r + j) % 4 of (1,0) (1,1) (0,1) (0,0), where u runs u1..u2 and v runs v2..v1.

Everything here works in model units (1/16 block at entity size 1).
"""

import math

import numpy as np
from PIL import Image

FACES = ["north", "east", "south", "west", "up", "down"]

# CubeMeshUtil.CubeVertices, per face, as -1/+1 corners.
CORNERS = {
    "north": [(-1, -1, -1), (-1, 1, -1), (1, 1, -1), (1, -1, -1)],
    "east": [(1, -1, -1), (1, 1, -1), (1, 1, 1), (1, -1, 1)],
    "south": [(1, -1, 1), (1, 1, 1), (-1, 1, 1), (-1, -1, 1)],
    "west": [(-1, -1, 1), (-1, 1, 1), (-1, 1, -1), (-1, -1, -1)],
    "up": [(1, 1, 1), (1, 1, -1), (-1, 1, -1), (-1, 1, 1)],
    "down": [(-1, -1, 1), (-1, -1, -1), (1, -1, -1), (1, -1, 1)],
}
NORMALS = {
    "north": (0, 0, -1), "east": (1, 0, 0), "south": (0, 0, 1),
    "west": (-1, 0, 0), "up": (0, 1, 0), "down": (0, -1, 0),
}
UV_CORNERS = [(1, 0), (1, 1), (0, 1), (0, 0)]


def rot_x(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0, 0], [0, c, -s, 0], [0, s, c, 0], [0, 0, 0, 1]], float)


def rot_y(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s, 0], [0, 1, 0, 0], [-s, 0, c, 0], [0, 0, 0, 1]], float)


def rot_z(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, -s, 0, 0], [s, c, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]], float)


def trans(v):
    m = np.eye(4)
    m[:3, 3] = v
    return m


def scale(v):
    return np.diag([v[0], v[1], v[2], 1.0])


def local_matrix(el, pose=None):
    """The element's transform in its parent's frame, for an optional pose
    {rotationX.., offsetX.., stretchX..} from a keyframe."""
    p = pose or {}
    o = np.array(el.get("rotationOrigin") or [0, 0, 0], float)
    f = np.array(el["from"], float) + [p.get("offsetX", 0), p.get("offsetY", 0), p.get("offsetZ", 0)]
    d = math.pi / 180
    rx = (el.get("rotationX", 0) + p.get("rotationX", 0)) * d
    ry = (el.get("rotationY", 0) + p.get("rotationY", 0)) * d
    rz = (el.get("rotationZ", 0) + p.get("rotationZ", 0)) * d
    m = trans(o)
    if rx:
        m = m @ rot_x(rx)
    if ry:
        m = m @ rot_y(ry)
    if rz:
        m = m @ rot_z(rz)
    st = (p.get("stretchX", 1), p.get("stretchY", 1), p.get("stretchZ", 1))
    if st != (1, 1, 1):
        m = m @ scale(st)
    return m @ trans(f - o)


def walk(elements, parent=None, poses=None, out=None):
    """Yields (element, world matrix) for every element, depth first."""
    if out is None:
        out = []
    pm = np.eye(4) if parent is None else parent
    for el in elements:
        m = pm @ local_matrix(el, (poses or {}).get(el["name"]))
        out.append((el, m))
        walk(el.get("children") or [], m, poses, out)
    return out


def face_quads(shape, poses=None):
    """Every enabled face as (element, face name, 4 world corners, 4 uvs,
    world normal, glow)."""
    quads = []
    for el, m in walk(shape["elements"], None, poses):
        faces = el.get("faces") or {}
        if not faces:
            continue
        size = np.array(el["to"], float) - np.array(el["from"], float)
        center = size / 2
        rot3 = m[:3, :3]
        for fname in FACES:
            fc = faces.get(fname)
            if not fc or fc.get("enabled", True) is False:
                continue
            uv = fc.get("uv")
            if uv is None:
                continue
            u1, v1, u2, v2 = uv
            r = int(fc.get("rotation", 0) // 90) % 4
            pts, uvs = [], []
            for j, c in enumerate(CORNERS[fname]):
                lp = center + size * np.array(c, float) / 2
                pts.append((m @ np.append(lp, 1.0))[:3])
                cu, cv = UV_CORNERS[(r + j) % 4]
                uvs.append((u1 + (u2 - u1) * cu, v2 + (v1 - v2) * cv))
            n = rot3 @ np.array(NORMALS[fname], float)
            n = n / (np.linalg.norm(n) or 1)
            quads.append((el, fname, np.array(pts), np.array(uvs), n, fc.get("glow", 0)))
    return quads


# ── Animation sampling ─────────────────────────────────────────────────

def anim_pose(anim, frame):
    """Linear interpolation between keyframes, per element and channel, the
    way the game blends a clip's frames. Wraps for looping clips."""
    keys = sorted(anim["keyframes"], key=lambda k: k["frame"])
    total = anim.get("quantityframes", keys[-1]["frame"] + 1)
    names = set()
    for k in keys:
        names.update(k["elements"].keys())
    pose = {}
    for name in names:
        ks = [(k["frame"], k["elements"][name]) for k in keys if name in k["elements"]]
        chans = set()
        for _, e in ks:
            chans.update(e.keys())
        pose[name] = {}
        for ch in chans:
            pts = [(f, e[ch]) for f, e in ks if ch in e and e[ch] is not None]
            if not pts:
                continue
            if frame <= pts[0][0]:
                val = pts[0][1]
            elif frame >= pts[-1][0]:
                val = pts[-1][1]
            else:
                for (fa, va), (fb, vb) in zip(pts, pts[1:]):
                    if fa <= frame <= fb:
                        t = (frame - fa) / max(1e-9, fb - fa)
                        val = va + (vb - va) * t
                        break
            pose[name][ch] = val
    return pose


# ── Rendering ──────────────────────────────────────────────────────────

def look_at(eye, target, up=(0, 1, 0)):
    eye, target, up = (np.array(v, float) for v in (eye, target, up))
    f = target - eye
    f /= np.linalg.norm(f)
    r = np.cross(f, up)
    r /= np.linalg.norm(r)
    u = np.cross(r, f)
    m = np.eye(4)
    m[0, :3], m[1, :3], m[2, :3] = r, u, -f
    m[:3, 3] = -m[:3, :3] @ eye
    return m


def render(quads, texture, eye, target, size=(960, 600), fov=40, light=(-0.4, 0.8, -0.45),
           background=((14, 26, 38), (4, 8, 14)), fog=None, extra=None, ambient=0.42):
    """Rasterizes quads with perspective-correct UVs, alpha test and simple
    lighting. texture: RGBA uint8 array. extra: flat-shaded quads
    [(corners, rgb)] such as scale references. Returns a PIL image."""
    w, h = size
    tex = texture
    th, tw = tex.shape[:2]
    view = look_at(eye, target)
    focal = (h / 2) / math.tan(math.radians(fov) / 2)
    img = np.zeros((h, w, 3), float)
    top, bottom = np.array(background[0], float), np.array(background[1], float)
    for y in range(h):
        img[y] = top + (bottom - top) * (y / (h - 1))
    zbuf = np.full((h, w), np.inf)
    L = np.array(light, float)
    L /= np.linalg.norm(L)
    eye_v = np.array(eye, float)

    def project(p):
        c = view @ np.append(p, 1.0)
        z = -c[2]
        return np.array([w / 2 + focal * c[0] / z, h / 2 - focal * c[1] / z]), z

    tris = []
    for (el, fname, pts, uvs, n, glow) in quads:
        bright = 1.0 if glow and glow > 100 else ambient + (1 - ambient) * max(0.0, float(n @ L)) * 0.9 + 0.1 * abs(n[1])
        tris.append((pts, uvs, bright, None))
    for pts, rgb in (extra or []):
        n = np.cross(pts[1] - pts[0], pts[2] - pts[0])
        n = n / (np.linalg.norm(n) or 1)
        bright = ambient + (1 - ambient) * abs(float(n @ L))
        tris.append((np.array(pts, float), None, bright, rgb))

    for pts, uvs, bright, rgb in tris:
        proj = [project(p) for p in pts]
        if any(z <= 1 for _, z in proj):
            continue
        for a, b, c in ((0, 1, 2), (0, 2, 3)):
            (pa, za), (pb, zb), (pc, zc) = proj[a], proj[b], proj[c]
            minx = max(int(math.floor(min(pa[0], pb[0], pc[0]))), 0)
            maxx = min(int(math.ceil(max(pa[0], pb[0], pc[0]))), w - 1)
            miny = max(int(math.floor(min(pa[1], pb[1], pc[1]))), 0)
            maxy = min(int(math.ceil(max(pa[1], pb[1], pc[1]))), h - 1)
            if minx > maxx or miny > maxy:
                continue
            area = (pb[0] - pa[0]) * (pc[1] - pa[1]) - (pb[1] - pa[1]) * (pc[0] - pa[0])
            if abs(area) < 1e-9:
                continue
            xs, ys = np.meshgrid(np.arange(minx, maxx + 1) + 0.5, np.arange(miny, maxy + 1) + 0.5)
            w0 = ((pb[0] - xs) * (pc[1] - ys) - (pb[1] - ys) * (pc[0] - xs)) / area
            w1 = ((pc[0] - xs) * (pa[1] - ys) - (pc[1] - ys) * (pa[0] - xs)) / area
            w2 = 1 - w0 - w1
            inside = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
            if not inside.any():
                continue
            iz = w0 / za + w1 / zb + w2 / zc
            z = 1 / iz
            sub = zbuf[miny:maxy + 1, minx:maxx + 1]
            mask = inside & (z < sub)
            if not mask.any():
                continue
            if uvs is not None:
                ua, ub, uc = uvs[a], uvs[b], uvs[c]
                u = (w0 * ua[0] / za + w1 * ub[0] / zb + w2 * uc[0] / zc) * z
                v = (w0 * ua[1] / za + w1 * ub[1] / zb + w2 * uc[1] / zc) * z
                umin, umax = min(ua[0], ub[0], uc[0]), max(ua[0], ub[0], uc[0])
                vmin, vmax = min(ua[1], ub[1], uc[1]), max(ua[1], ub[1], uc[1])
                ui = np.clip(np.floor(u), math.floor(umin), max(math.floor(umin), math.ceil(umax) - 1)).astype(int)
                vi = np.clip(np.floor(v), math.floor(vmin), max(math.floor(vmin), math.ceil(vmax) - 1)).astype(int)
                ui = np.clip(ui, 0, tw - 1)
                vi = np.clip(vi, 0, th - 1)
                texel = tex[vi, ui]
                mask &= texel[..., 3] >= 128
                if not mask.any():
                    continue
                col = texel[..., :3].astype(float) * bright
            else:
                col = np.broadcast_to(np.array(rgb, float) * bright, mask.shape + (3,))
            if fog is not None:
                fcol, density = fog
                f = 1 - np.exp(-z * density)
                col = col * (1 - f[..., None]) + np.array(fcol, float) * f[..., None]
            region = img[miny:maxy + 1, minx:maxx + 1]
            region[mask] = col[mask]
            sub[mask] = z[mask]
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))


def box_quads(lo, hi):
    """Flat quads of an axis-aligned box, for scale references."""
    x0, y0, z0 = lo
    x1, y1, z1 = hi
    P = lambda x, y, z: np.array([x, y, z], float)
    return [
        [P(x0, y0, z0), P(x0, y1, z0), P(x1, y1, z0), P(x1, y0, z0)],
        [P(x0, y0, z1), P(x1, y0, z1), P(x1, y1, z1), P(x0, y1, z1)],
        [P(x0, y0, z0), P(x0, y0, z1), P(x0, y1, z1), P(x0, y1, z0)],
        [P(x1, y0, z0), P(x1, y1, z0), P(x1, y1, z1), P(x1, y0, z1)],
        [P(x0, y1, z0), P(x0, y1, z1), P(x1, y1, z1), P(x1, y1, z0)],
        [P(x0, y0, z0), P(x1, y0, z0), P(x1, y0, z1), P(x0, y0, z1)],
    ]


def load_texture(path):
    return np.array(Image.open(path).convert("RGBA"))
