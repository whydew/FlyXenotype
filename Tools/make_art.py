"""
Fly Xenotype art generator. Draws every texture at 8x resolution and downsamples to the
vanilla size, so edges get the same soft antialiasing as vanilla art.

Vanilla conventions matched (measured from Core/Biotech textures):
  heads/attachments : 128x128, grayscale (tinted by skin colour in game), ~3px black outline,
                      head ~46x50 px centred at (64,64); south = darker top/side band fading to
                      ~240 face; east = flat 181 back half, lighter front; north = flat 181 + 198 rim.
  xenotype icon     : 64x64, white fill, ~3px black outline, black features.
"""
import math, os
import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from scipy import ndimage

S = 8                     # supersample factor
OUT = "/tmp/fb/art/out"
os.makedirs(OUT, exist_ok=True)


# ---------------------------------------------------------------- helpers
def canvas(size=128):
    return Image.new("L", (size * S, size * S), 0)


def poly_mask(points, size=128):
    m = canvas(size)
    ImageDraw.Draw(m).polygon([(x * S, y * S) for x, y in points], fill=255)
    return np.array(m) > 127


def ellipse_mask(cx, cy, rx, ry, size=128, rot=0.0):
    pts = []
    for i in range(180):
        t = 2 * math.pi * i / 180
        x, y = rx * math.cos(t), ry * math.sin(t)
        c, s = math.cos(rot), math.sin(rot)
        pts.append((cx + x * c - y * s, cy + x * s + y * c))
    return poly_mask(pts, size)


def dilate(mask, px):
    r = int(round(px * S))
    yy, xx = np.ogrid[-r:r + 1, -r:r + 1]
    disk = xx * xx + yy * yy <= r * r
    return ndimage.binary_dilation(mask, structure=disk)


def dist(mask):
    return ndimage.distance_transform_edt(mask) / S  # in output pixels


def shift(mask, dx, dy):
    return np.roll(np.roll(mask, int(dy * S), axis=0), int(dx * S), axis=1)


class Layer:
    """A filled shape with a black outline. gray = HxW float array of fill values."""
    def __init__(self, mask, gray, outline=3.0):
        self.mask, self.gray, self.outline = mask, gray, outline


def compose(layers, size=128):
    """Paint layers bottom to top. Each layer: outline (black) then fill."""
    g = np.zeros((size * S, size * S), np.float32)
    a = np.zeros_like(g)
    for L in layers:
        if L.outline > 0:
            o = dilate(L.mask, L.outline)
            g[o] = 0; a[o] = 255
        g[L.mask] = L.gray[L.mask] if isinstance(L.gray, np.ndarray) else L.gray
        a[L.mask] = 255
    rgba = np.dstack([g, g, g, a]).astype(np.uint8)
    im = Image.fromarray(rgba, "RGBA")
    # premultiply -> downsample -> unpremultiply keeps edges clean
    arr = np.array(im).astype(np.float32)
    arr[:, :, :3] *= arr[:, :, 3:4] / 255.0
    small = Image.fromarray(arr.astype(np.uint8), "RGBA").resize((size, size), Image.LANCZOS)
    s = np.array(small).astype(np.float32)
    al = np.maximum(s[:, :, 3:4], 1)
    s[:, :, :3] = np.clip(s[:, :, :3] * 255.0 / al, 0, 255)
    return Image.fromarray(s.astype(np.uint8), "RGBA")


def save(im, name):
    im.save(os.path.join(OUT, name))


# ---------------------------------------------------------------- head shapes
def head_outline_south(width, height, chin, cx=64, cy=64):
    """Rounded skull, cheeks widest just below the eyes, tapering to a small chin."""
    pts = []
    for i in range(240):
        t = 2 * math.pi * i / 240            # 0 = right, pi/2 = down (image coords)
        x, y = math.cos(t), math.sin(t)
        if y > 0:                             # lower half tapers toward the chin
            x *= 1 - chin * y ** 1.6
        pts.append((cx + x * width / 2, cy + y * height / 2 + (1.5 if y > 0 else 0)))
    return pts


def head_outline_east(width, height, chin, cx=64, cy=64):
    """Profile facing right: round cranium, face leaning forward, small snout for the mouthparts."""
    pts = []
    for i in range(240):
        t = 2 * math.pi * i / 240
        x, y = math.cos(t), math.sin(t)
        if y > 0 and x > 0:                   # lower front: push forward slightly (snout)
            x *= 1 + 0.10 * y
        if y > 0 and x < 0:                   # lower back: tuck in (nape)
            x *= 1 - chin * y ** 1.4
        pts.append((cx + x * width / 2, cy + y * height / 2 + (1.5 if y > 0 else 0)))
    return pts


def south_shading(mask):
    lit = shift(mask, 0, 4)                   # light comes from below-front like vanilla
    d = dist(lit & mask)
    return 181 + 63 * np.clip(d / 5.5, 0, 1)


def east_shading(mask, cx=64):
    d = dist(shift(mask, 7, 2) & mask)
    return 181 + 66 * np.clip(d / 4.0, 0, 1)


def north_shading(mask):
    d = dist(mask)
    g = np.full(mask.shape, 181.0)
    g[d < 2.2] = 198
    return g


HEADS = {
    # name: (width, height, chin taper)
    "FB_FlyHead_Round": (48, 50, 0.30),
    "FB_FlyHead_Narrow": (42, 51, 0.36),
    "FB_FlyHead_Wide": (52, 48, 0.26),
}


def make_heads():
    for name, (w, h, chin) in HEADS.items():
        ms = poly_mask(head_outline_south(w, h, chin))
        save(compose([Layer(ms, south_shading(ms))]), f"{name}_south.png")
        me = poly_mask(head_outline_east(w + 1, h, chin))
        save(compose([Layer(me, east_shading(me))]), f"{name}_east.png")
        mn = poly_mask(head_outline_south(w, h, chin * 0.6))
        save(compose([Layer(mn, north_shading(mn))]), f"{name}_north.png")


# ---------------------------------------------------------------- compound eyes (tinted per pawn)
def facet_fill(mask, cx, cy, rx, ry, base_hi=235, base_lo=150):
    """Grayscale compound-eye fill: radial light, hex facet lattice, pseudopupil, highlight."""
    H, W = mask.shape
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32) / S
    r = np.sqrt(((xx - cx) / rx) ** 2 + ((yy - cy + ry * 0.15) / ry) ** 2)
    g = base_hi - (base_hi - base_lo) * np.clip(r, 0, 1) ** 1.3
    # hex facet lattice (spacing ~1.9 output px): dark seams between facets
    sp = 1.9
    q = xx / sp; rr = (yy / sp) / 0.866
    rowi = np.floor(rr); q = q - 0.5 * (rowi % 2)
    fx = q - np.round(q); fy = rr - np.round(rr)
    edge = np.sqrt(fx * fx + (fy * 0.866) ** 2)
    g -= 26 * (edge > 0.36)
    # pseudopupil: small dark smudge that follows the viewer, offset toward the face
    pr = np.sqrt(((xx - cx) / (rx * 0.30)) ** 2 + ((yy - cy - ry * 0.05) / (ry * 0.28)) ** 2)
    g -= 55 * np.clip(1 - pr, 0, 1)
    # specular highlight upper-left
    hr = np.sqrt(((xx - (cx - rx * 0.38)) / (rx * 0.22)) ** 2 + ((yy - (cy - ry * 0.40)) / (ry * 0.16)) ** 2)
    g = np.where(hr < 1, 255, g)
    return np.clip(g, 0, 255)


def make_eyes():
    # south: two big bulging ovals, set high on the face, overlapping the head outline
    L, R = (51.5, 58.5), (76.5, 58.5)
    rx, ry = 10.5, 12.5
    layers = []
    for (cx, cy), rot in ((L, 0.35), (R, -0.35)):
        m = ellipse_mask(cx, cy, rx, ry, rot=rot)
        layers.append(Layer(m, facet_fill(m, cx, cy, rx, ry)))
    save(compose(layers), "FB_CompoundEyes_south.png")
    # east: one large eye at the front of the profile
    cx, cy = 74, 58
    m = ellipse_mask(cx, cy, 11.5, 13.5, rot=-0.15)
    save(compose([Layer(m, facet_fill(m, cx, cy, 11.5, 13.5))]), "FB_CompoundEyes_east.png")
    # north: the bulges peek out past both sides of the skull
    layers = []
    for cx in (40.5, 87.5):
        m = ellipse_mask(cx, 58.5, 5.0, 10.5) & ~ellipse_mask(64, 64, 22.5, 25.5)
        layers.append(Layer(m, facet_fill(m, cx, 58.5, 5.0, 10.5, 200, 140), outline=2.6))
    save(compose(layers), "FB_CompoundEyes_north.png")


# ---------------------------------------------------------------- antennae (skin tinted, darker)
def stalk(points, width):
    m = canvas()
    ImageDraw.Draw(m).line([(x * S, y * S) for x, y in points], fill=255, width=int(width * S), joint="curve")
    for x, y in (points[0], points[-1]):
        r = width / 2
        ImageDraw.Draw(m).ellipse([(x - r) * S, (y - r) * S, (x + r) * S, (y + r) * S], fill=255)
    return np.array(m) > 127


def arista(points, width=1.2):
    """Thin bristle drawn as pure black line (no fill)."""
    m = canvas()
    ImageDraw.Draw(m).line([(x * S, y * S) for x, y in points], fill=255, width=int(width * S), joint="curve")
    return np.array(m) > 127


def antenna(base, mid, club_c, club_r, bristle, gray=150):
    st = stalk([base, mid], 2.4)
    club = ellipse_mask(club_c[0], club_c[1], club_r * 0.8, club_r)
    return [Layer(st | club, gray, outline=2.2), Layer(arista([club_c, *bristle]), 0, outline=0)]


def make_antennae():
    # south: rooted between the eyes, angling up and out above the forehead
    layers = []
    for sgn in (-1, 1):
        base = (64 + 3.5 * sgn, 43)
        mid = (64 + 7.5 * sgn, 35)
        club = (64 + 10.5 * sgn, 30)
        bristle = [(64 + 15.5 * sgn, 26), (64 + 19 * sgn, 25)]
        layers += antenna(base, mid, club, 3.2, bristle)
    save(compose(layers), "FB_Antennae_south.png")
    # east: two overlapping antennae pointing forward-up
    layers = []
    for off, g in ((-1.5, 130), (1.5, 150)):
        layers += antenna((70 + off, 42), (76 + off, 34), (80 + off, 30), 3.2,
                          [(86 + off, 27), (90 + off, 27)], gray=g)
    save(compose(layers), "FB_Antennae_east.png")
    # north: seen from behind, sticking up over the crown
    layers = []
    for sgn in (-1, 1):
        layers += antenna((64 + 3.5 * sgn, 42), (64 + 7 * sgn, 34), (64 + 9.5 * sgn, 30), 3.0,
                          [(64 + 13.5 * sgn, 26), (64 + 16.5 * sgn, 25)], gray=140)
    save(compose(layers), "FB_Antennae_north.png")


# ---------------------------------------------------------------- mandibles (skin tinted, darker)
def pincer(base, tip, bulge, width_base=4.2):
    """Curved tapered pincer from base to tip, bowing toward 'bulge'."""
    pts_l, pts_r = [], []
    n = 24
    for i in range(n + 1):
        t = i / n
        # quadratic bezier centre line
        x = (1 - t) ** 2 * base[0] + 2 * (1 - t) * t * bulge[0] + t * t * tip[0]
        y = (1 - t) ** 2 * base[1] + 2 * (1 - t) * t * bulge[1] + t * t * tip[1]
        dx = 2 * (1 - t) * (bulge[0] - base[0]) + 2 * t * (tip[0] - bulge[0])
        dy = 2 * (1 - t) * (bulge[1] - base[1]) + 2 * t * (tip[1] - bulge[1])
        ln = math.hypot(dx, dy) or 1
        nx, ny = -dy / ln, dx / ln
        w = width_base * (1 - t) ** 0.8 / 2 + 0.25
        pts_l.append((x + nx * w, y + ny * w)); pts_r.append((x - nx * w, y - ny * w))
    return poly_mask(pts_l + pts_r[::-1])


def make_mandibles():
    layers = []
    for sgn in (-1, 1):
        m = pincer((64 + 6 * sgn, 84), (64 + 1.2 * sgn, 92.5), (64 + 8.5 * sgn, 90.5))
        layers.append(Layer(m, 165, outline=2.2))
    save(compose(layers), "FB_Mandibles_south.png")
    m = pincer((84, 82), (88.5, 90.5), (89.5, 84.5))
    m2 = pincer((82, 83.5), (85, 91.5), (86.5, 86.5))
    save(compose([Layer(m2, 140, outline=2.2), Layer(m, 165, outline=2.2)]), "FB_Mandibles_east.png")
    save(Image.new("RGBA", (128, 128), (0, 0, 0, 0)), "FB_Mandibles_north.png")


# ---------------------------------------------------------------- xenotype icon (64x64, white + black)
def make_icon():
    sz = 64
    def em(cx, cy, rx, ry, rot=0.0): return ellipse_mask(cx, cy, rx, ry, sz, rot)
    head = poly_mask(head_outline_south(36, 36, 0.30, cx=32, cy=36), sz)
    ant = np.zeros_like(head)
    for sgn in (-1, 1):
        m = canvas(sz)
        ImageDraw.Draw(m).line([((32 + 3.5 * sgn) * S, 21 * S), ((32 + 8 * sgn) * S, 12 * S)], fill=255, width=int(3.4 * S))
        ant |= (np.array(m) > 127) | em(32 + 9.5 * sgn, 9.5, 3.6, 3.6)
    eyes = np.zeros_like(head); glint = np.zeros_like(head)
    for sgn, rot in ((-1, 0.35), (1, -0.35)):
        eyes |= em(32 + 8.5 * sgn, 33, 7.4, 8.8, rot)
        glint |= em(32 + 8.5 * sgn - 2.6, 29.5, 1.8, 1.5)
    # pincers: two small black hooks on the chin, like vanilla's black facial features
    mand = np.zeros_like(head)
    for sgn in (-1, 1):
        mand |= pincer_small(sgn, sz)
    layers = [Layer(head | ant, 255, outline=2.9), Layer(eyes, 0, outline=0), Layer(glint, 255, outline=0),
              Layer(mand & head, 0, outline=0)]
    save(compose(layers, sz), "FB_Fly_XenotypeIcon.png")


def pincer_small(sgn, sz):
    pts_l, pts_r = [], []
    base, bulge, tip = (32 + 6.5 * sgn, 45.5), (32 + 7.5 * sgn, 51.5), (32 + 1.8 * sgn, 51.8)
    for i in range(21):
        t = i / 20
        x = (1 - t) ** 2 * base[0] + 2 * (1 - t) * t * bulge[0] + t * t * tip[0]
        y = (1 - t) ** 2 * base[1] + 2 * (1 - t) * t * bulge[1] + t * t * tip[1]
        dx = 2 * (1 - t) * (bulge[0] - base[0]) + 2 * t * (tip[0] - bulge[0])
        dy = 2 * (1 - t) * (bulge[1] - base[1]) + 2 * t * (tip[1] - bulge[1])
        ln = math.hypot(dx, dy) or 1
        nx, ny = -dy / ln, dx / ln
        w = 2.4 * (1 - t) ** 0.7 / 2 + 0.35
        pts_l.append((x + nx * w, y + ny * w)); pts_r.append((x - nx * w, y - ny * w))
    return poly_mask(pts_l + pts_r[::-1], sz)


# ---------------------------------------------------------------- gene icons (grayscale, 128x128)
def make_gene_icons():
    """Simple gene icons in the vanilla style: white/grey shapes, black outline, on transparent."""
    # compound eye
    m = ellipse_mask(64, 64, 34, 40, rot=0.2)
    save(compose([Layer(m, facet_fill(m, 64, 64, 34, 40, 245, 170), outline=5)]), "Gene_FB_CompoundEyes.png")
    # antennae
    layers = []
    for sgn in (-1, 1):
        st = stalk([(64 + 10 * sgn, 104), (64 + 24 * sgn, 60)], 9)
        club = ellipse_mask(64 + 32 * sgn, 42, 10, 13)
        layers.append(Layer(st | club, 225, outline=5))
        layers.append(Layer(arista([(64 + 32 * sgn, 42), (64 + 46 * sgn, 22), (64 + 54 * sgn, 18)], 4), 0, outline=0))
    save(compose(layers), "Gene_FB_Antennae.png")
    # mandibles
    layers = []
    for sgn in (-1, 1):
        layers.append(Layer(pincer((64 + 30 * sgn, 34), (64 + 4 * sgn, 98), (64 + 44 * sgn, 86), 20), 225, outline=5))
    save(compose(layers), "Gene_FB_Mandibles.png")
    # chitin skin: layered carapace plates
    layers = []
    for i, (cy, rx, ry) in enumerate(((50, 40, 22), (72, 36, 20), (92, 30, 17))):
        layers.append(Layer(ellipse_mask(64, cy, rx, ry), 230 - 25 * i, outline=5))
    save(compose(layers), "Gene_FB_ChitinSkin.png")
    # fly head: the head shape scaled up
    m = poly_mask(head_outline_south(80, 86, 0.32))
    save(compose([Layer(m, south_shading(m), outline=5)]), "Gene_FB_FlyHead.png")


if __name__ == "__main__":
    make_heads(); make_eyes(); make_antennae(); make_mandibles(); make_icon(); make_gene_icons()
    print(sorted(os.listdir(OUT)))
