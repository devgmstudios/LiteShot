"""
Builds the website's feature animations as Lottie files (server/public/lottie/*.json).

Each one is a white line drawing on a 96x96 canvas, made to sit on the colored glass badges of the feature cards,
drawn in the same style as LiteShot's icons. They loop every three seconds. Run: python server/tools/make_lottie.py
"""
import json
import math
import os

FPS, LENGTH, SIZE = 60, 180, 96
WHITE = [1, 1, 1, 1]
OUT = os.path.join(os.path.dirname(__file__), "..", "public", "lottie")


# ───────────────────────────── values and keyframes ─────────────────────────────

def static(v):
    return {"a": 0, "k": v}


def anim(*keys, ease=(0.42, 0.0, 0.58, 1.0)):
    """keys: (frame, value) pairs. Values are numbers or lists; easing applies between every pair."""
    frames = []
    for i, (t, v) in enumerate(keys):
        v = v if isinstance(v, list) else [v]
        k = {"t": t, "s": v}
        if i < len(keys) - 1:
            n = len(v)
            k["o"] = {"x": [ease[0]] * n, "y": [ease[1]] * n}
            k["i"] = {"x": [ease[2]] * n, "y": [ease[3]] * n}
        frames.append(k)
    return {"a": 1, "k": frames}


SPRING = (0.34, 0.0, 0.2, 1.4)   # a little overshoot


# ───────────────────────────── shapes ─────────────────────────────

def path(points, closed=False, tangents=None):
    n = len(points)
    zero = [[0, 0]] * n
    ins, outs = (tangents if tangents else (zero, zero))
    return {"ty": "sh", "ks": static({"c": closed, "v": points, "i": ins, "o": outs})}


def arc_path(cx, cy, r, start, sweep, segments=8):
    """A circular arc as a bezier path (angles in degrees, 0 = right, clockwise)."""
    pts, ins, outs = [], [], []
    step = sweep / segments
    k = 4 / 3 * math.tan(math.radians(step) / 4) * r
    for i in range(segments + 1):
        a = math.radians(start + step * i)
        x, y = cx + r * math.cos(a), cy + r * math.sin(a)
        tx, ty = -math.sin(a), math.cos(a)
        pts.append([x, y])
        ins.append([-tx * k, -ty * k] if i > 0 else [0, 0])
        outs.append([tx * k, ty * k] if i < segments else [0, 0])
    return path(pts, False, (ins, outs))


def ellipse(cx, cy, w, h):
    return {"ty": "el", "p": static([cx, cy]), "s": static([w, h]), "d": 1}


def rect(cx, cy, w, h, r):
    return {"ty": "rc", "p": static([cx, cy]), "s": static([w, h]), "r": static(r), "d": 1}


def stroke(width=5, color=WHITE, opacity=100):
    return {"ty": "st", "c": static(color), "o": static(opacity) if not isinstance(opacity, dict) else opacity,
            "w": static(width), "lc": 2, "lj": 2, "ml": 4}


def fill(color=WHITE, opacity=100):
    return {"ty": "fl", "c": static(color), "o": static(opacity) if not isinstance(opacity, dict) else opacity, "r": 1}


def trim(start=None, end=None):
    return {"ty": "tm", "s": start or static(0), "e": end or static(100), "o": static(0), "m": 1}


def transform(p=(0, 0), a=(0, 0), s=None, r=None, o=None):
    return {"ty": "tr", "p": static(list(p)) if not isinstance(p, dict) else p, "a": static(list(a)),
            "s": s or static([100, 100]), "r": r or static(0), "o": o or static(100),
            "sk": static(0), "sa": static(0)}


def group(*items, tr=None):
    return {"ty": "gr", "it": list(items) + [tr or transform()]}


def layer(name, *shapes, p=(48, 48), a=(48, 48), s=None, r=None, o=None, index=1):
    return {
        "ddd": 0, "ind": index, "ty": 4, "nm": name, "sr": 1, "ao": 0, "bm": 0, "ip": 0, "op": LENGTH, "st": 0,
        "ks": {"o": o or static(100), "r": r or static(0),
               "p": static(list(p) + [0]) if not isinstance(p, dict) else p,
               "a": static(list(a) + [0]), "s": s or static([100, 100, 100])},
        "shapes": list(shapes),
    }


def draw_on(start, end):
    """Trim path that draws a stroke on between two frames, holds, then fades with the loop."""
    return trim(end=anim((start, 0), (end, 100)))


def fade_loop():
    """Every layer fades out at the end so the loop restarts cleanly."""
    return anim((0, 0), (8, 100), (LENGTH - 24, 100), (LENGTH - 6, 0))


def scene(name, layers):
    for i, l in enumerate(layers):
        l["ind"] = i + 1
    return {"v": "5.7.4", "fr": FPS, "ip": 0, "op": LENGTH, "w": SIZE, "h": SIZE, "nm": name, "ddd": 0,
            "assets": [], "layers": layers}


# ───────────────────────────── the animations ─────────────────────────────

def markup():
    arrow = group(path([[24, 72], [68, 28]]), draw_on(6, 40), stroke())
    head = group(path([[46, 28], [68, 28], [68, 50]]), draw_on(36, 56), stroke())
    squiggle = group(path([[22, 82], [34, 76], [46, 84], [58, 76], [72, 82]],
                          tangents=([[0, 0], [-4, 0], [-4, 0], [-4, 0], [-4, 0]], [[4, 0], [4, 0], [4, 0], [4, 0], [0, 0]])),
                     draw_on(50, 84), stroke(4, opacity=75))
    return scene("markup", [layer("drawing", squiggle, head, arrow, o=fade_loop())])


def snap():
    window = group(rect(48, 50, 48, 36, 6), stroke(4, opacity=70))
    corners = []
    for sx, sy in [(-1, -1), (1, -1), (1, 1), (-1, 1)]:
        cx, cy = 48 + sx * 32, 48 + sy * 26
        corners.append(group(path([[cx, cy - sy * 10], [cx, cy], [cx - sx * 10, cy]]), stroke(5)))
    zoom = anim((0, [135, 135, 100]), (34, [100, 100, 100]), ease=SPRING)
    return scene("snap", [
        layer("corners", *corners, s=zoom, o=fade_loop()),
        layer("window", window, o=fade_loop()),
    ])


def names():
    lines = [group(rect(48, y, 52, 7, 3.5), fill(opacity=55)) for y in (36, 52, 68)]
    pill = group(rect(0, 0, 30, 14, 7), fill(opacity=95))
    grow = anim((20, [0, 100, 100]), (46, [100, 100, 100]), ease=SPRING)
    return scene("names", [
        layer("blur", pill, p=(22, 52), a=(-15, 0), s=grow, o=fade_loop()),
        layer("text", *lines, o=fade_loop()),
    ])


def faces():
    """A smiling face; then its features turn into a mosaic, square by square."""
    head = group(ellipse(48, 38, 42, 42), stroke(5))
    shoulders = group(path([[17, 86], [48, 62], [79, 86]],
                           tangents=([[0, 0], [-18, 0], [0, -14]], [[0, -14], [18, 0], [0, 0]])), stroke(5))
    fade_out = anim((0, 100), (38, 100), (52, 0), (LENGTH - 6, 0), (LENGTH, 100))
    features = [group(ellipse(40, 35, 6, 7), fill()), group(ellipse(56, 35, 6, 7), fill()),
                group(arc_path(48, 40, 10, 25, 130, 4), stroke(4.5))]
    layers = [layer("features", *features, o=fade_out)]
    # 3x3 mosaic inside the head (a 24 px square fits within its inner edge), checkered in two tones.
    for row in range(3):
        for col in range(3):
            x, y = 40 + col * 8, 30 + row * 8
            t = 40 + (row * 3 + col) * 3
            tone = 100 if (row + col) % 2 == 0 else 55
            pop = anim((t, [0, 0, 100]), (t + 12, [100, 100, 100]), ease=SPRING)
            layers.append(layer(f"tile{row}{col}", group(rect(x, y, 7.2, 7.2, 1.4), fill(opacity=tone)),
                                p=(x, y), a=(x, y), s=pop, o=fade_loop()))
    layers.append(layer("face", head, shoulders))
    return scene("faces", layers)


def bank():
    """Columns rise under a roof: finance."""
    roof = group(path([[18, 40], [48, 20], [78, 40]], True), stroke(5))
    base = group(path([[16, 80], [80, 80]]), stroke(5))
    cols = []
    for i, x in enumerate((27, 41, 55, 69)):
        grow = anim((8 + i * 6, [100, 0, 100]), (26 + i * 6, [100, 100, 100]), ease=SPRING)
        cols.append(layer(f"col{i}", group(path([[x, 46], [x, 74]]), stroke(5)), p=(x, 74), a=(x, 74), s=grow, o=fade_loop()))
    return scene("bank", cols + [layer("frame", roof, base, o=fade_loop())])


def people():
    """Two people; the second slides in beside the first: teams."""
    def person(cx, s=1.0):
        return [group(ellipse(cx, 36, 24 * s, 24 * s), stroke(5)),
                group(path([[cx - 20 * s, 78], [cx, 58], [cx + 20 * s, 78]],
                           tangents=([[0, 0], [-12 * s, 0], [0, -10 * s]], [[0, -10 * s], [12 * s, 0], [0, 0]])), stroke(5))]
    slide = anim((10, [36, 0, 0]), (34, [0, 0, 0]), ease=SPRING)
    return scene("people", [
        layer("second", *person(62, 0.9), p=slide, a=(0, 0), o=anim((10, 0), (24, 100), (LENGTH - 24, 100), (LENGTH - 6, 0))),
        layer("first", *person(38), o=fade_loop()),
    ])


def download():
    """An arrow drops into a tray."""
    tray = group(path([[22, 58], [22, 74], [74, 74], [74, 58]]), stroke(5))
    arrow = [group(path([[48, 18], [48, 56]]), stroke(5)), group(path([[34, 44], [48, 58], [62, 44]]), stroke(5))]
    drop = anim((0, [0, -26, 0]), (30, [0, 0, 0]), (120, [0, 0, 0]), (150, [0, 6, 0]), ease=SPRING)
    return scene("download", [layer("arrow", *arrow, p=drop, a=(0, 0), o=fade_loop()), layer("tray", tray)])


def check():
    """A circle and a check that draw themselves on."""
    ring = group(ellipse(48, 48, 64, 64), draw_on(4, 40), stroke(5))
    mark = group(path([[33, 49], [44, 60], [64, 38]]), draw_on(36, 60), stroke(6))
    return scene("check", [layer("check", ring, mark, o=fade_loop())])


def sparkle(cx, cy, r):
    k = 0.12
    tips = [[cx, cy - r], [cx + r, cy], [cx, cy + r], [cx - r, cy]]
    pts, ins, outs = [], [], []
    for i, t in enumerate(tips):
        pts.append(t)
        prev, nxt = tips[i - 1], tips[(i + 1) % 4]
        c_in = [cx + (prev[0] - cx + t[0] - cx) * k - t[0], cy + (prev[1] - cy + t[1] - cy) * k - t[1]]
        c_out = [cx + (t[0] - cx + nxt[0] - cx) * k - t[0], cy + (t[1] - cy + nxt[1] - cy) * k - t[1]]
        ins.append(c_in)
        outs.append(c_out)
    return path(pts, True, (ins, outs))


def ai():
    big = group(sparkle(42, 54, 24), fill())
    small = group(sparkle(70, 26, 10), fill())
    tiny = group(sparkle(72, 72, 7), fill())
    pulse = anim((0, [100, 100, 100]), (40, [112, 112, 100]), (80, [100, 100, 100]), (180, [100, 100, 100]))
    twinkle = anim((20, [0, 0, 100]), (44, [100, 100, 100]), (120, [100, 100, 100]), (150, [0, 0, 100]), ease=SPRING)
    twinkle2 = anim((44, [0, 0, 100]), (68, [100, 100, 100]), (130, [100, 100, 100]), (160, [0, 0, 100]), ease=SPRING)
    return scene("ai", [
        layer("tiny", tiny, p=(72, 72), a=(72, 72), s=twinkle2),
        layer("small", small, p=(70, 26), a=(70, 26), s=twinkle),
        layer("big", big, p=(42, 54), a=(42, 54), s=pulse, r=anim((0, 0), (40, 12), (80, 0))),
    ])


def links():
    shackle = group(path([[38, 44], [38, 36], [58, 36], [58, 44]],
                         tangents=([[0, 0], [0, 6], [-6, 0], [0, 0]], [[0, -6], [6, 0], [0, 6], [0, 0]])), stroke(5))
    body = group(rect(48, 56, 34, 26, 6), stroke(5))
    keyhole = group(ellipse(48, 55, 6, 6), fill())
    countdown = group(arc_path(48, 50, 40, -90, 360, 12), trim(end=anim((10, 100), (170, 0), ease=(0, 0, 1, 1))), stroke(4, opacity=80))
    return scene("links", [
        layer("clock", countdown),
        layer("lock", shackle, body, keyhole, o=fade_loop()),
    ])


def ocr():
    corners = []
    for sx, sy in [(-1, -1), (1, -1), (1, 1), (-1, 1)]:
        cx, cy = 48 + sx * 30, 48 + sy * 30
        corners.append(group(path([[cx, cy - sy * 12], [cx, cy], [cx - sx * 12, cy]]), stroke(5)))
    lines = [group(path([[34, y], [34 + w, y]]), draw_on(s, s + 22), stroke(5))
             for y, w, s in ((38, 28, 10), (49, 28, 26), (60, 16, 42))]
    return scene("ocr", [layer("text", *lines, o=fade_loop()), layer("frame", *corners)])


def pin():
    card = group(rect(48, 66, 52, 26, 6), stroke(4, opacity=70))
    pin_shape = group(path([[42, 22], [54, 22], [54, 34], [60, 42], [36, 42], [42, 34]], True), stroke(5))
    needle = group(path([[48, 42], [48, 56]]), stroke(5))
    drop = anim((0, [48, 10, 0]), (30, [48, 48, 0]), ease=SPRING)
    squash = anim((26, [100, 100, 100]), (34, [106, 92, 100]), (44, [100, 100, 100]))
    return scene("pin", [
        layer("pin", pin_shape, needle, p=drop, a=(48, 48), o=fade_loop()),
        layer("card", card, p=(48, 66), a=(48, 66), s=squash, o=fade_loop()),
    ])


def signed():
    shield = group(path([[48, 18], [72, 28], [72, 46], [48, 80], [24, 46], [24, 28]], True,
                        tangents=([[0, 0], [0, 0], [0, 0], [10, -8], [0, 0], [0, 0]],
                                  [[0, 0], [0, 0], [0, 14], [0, 0], [0, -14], [0, 0]])),
                   draw_on(4, 50), stroke(5))
    check = group(path([[37, 48], [45, 56], [60, 40]]), draw_on(50, 72), stroke(6))
    return scene("signed", [layer("shield", shield, check, o=fade_loop())])


def monitor():
    screen = group(rect(48, 42, 64, 44, 6), stroke(5))
    stand = group(path([[48, 64], [48, 76]]), stroke(5))
    base = group(path([[36, 78], [60, 78]]), stroke(5))
    loupe = group(ellipse(0, 0, 18, 18), stroke(4), fill(opacity=30))
    travel = anim((0, [32, 34, 0]), (60, [62, 46, 0]), (120, [40, 50, 0]), (180, [32, 34, 0]))
    return scene("monitor", [
        layer("loupe", loupe, p=travel, a=(0, 0)),
        layer("monitor", screen, stand, base),
    ])


def opensource():
    left = group(path([[36, 30], [20, 48], [36, 66]]), stroke(6))
    right = group(path([[60, 30], [76, 48], [60, 66]]), stroke(6))
    slash = group(path([[54, 26], [42, 70]]), draw_on(30, 60), stroke(6))
    apart_l = anim((0, [56, 48, 0]), (28, [48, 48, 0]), ease=SPRING)
    apart_r = anim((0, [40, 48, 0]), (28, [48, 48, 0]), ease=SPRING)
    return scene("opensource", [
        layer("slash", slash, o=fade_loop()),
        layer("left", left, p=apart_l, o=fade_loop()),
        layer("right", right, p=apart_r, o=fade_loop()),
    ])


def keyboard():
    layers = []
    for i, x in enumerate((24, 48, 72)):
        key = group(rect(x, 48, 20, 20, 5), stroke(4), fill(opacity=25))
        t = 12 + i * 22
        press = anim((t, [x, 48, 0]), (t + 6, [x, 54, 0]), (t + 16, [x, 48, 0]))
        layers.append(layer(f"key{i}", key, p=press, a=(x, 48)))
    return scene("keyboard", layers)


ANIMATIONS = {
    "markup": markup, "snap": snap, "names": names, "faces": faces, "ai": ai, "links": links,
    "ocr": ocr, "pin": pin, "signed": signed, "monitor": monitor, "opensource": opensource, "keyboard": keyboard,
    "bank": bank, "people": people, "download": download, "check": check,
}

if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for name, build in ANIMATIONS.items():
        with open(os.path.join(OUT, f"{name}.json"), "w", encoding="utf-8") as f:
            json.dump(build(), f, separators=(",", ":"))
        print(f"wrote lottie/{name}.json")
