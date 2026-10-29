"""
Builds fortefile's security badge animations as Lottie files (public/lottie/*.json), plus the matching static icons
(public/assets/badges.svg) drawn from the same geometry.

Each icon is a graphite line drawing on a 96x96 canvas, engraved into the silver badges of the security pages. They
play once when a badge scrolls into view and end on their "rest" frame, which is exactly what the static icon shows,
so the page looks finished with reduced motion, before the player loads, or if it never does.

Rule for every animation: at its last frame every transform is back to identity (or a fixed pose) and every trim is
complete, so svg_symbol() can render that frame as a plain SVG. Run: python fortefile/tools/make_lottie.py
"""
import json
import math
import os

FPS, SIZE = 60, 96
INK = [0.192, 0.216, 0.263, 1]          # #313743, graphite; the static icons use the same color through CSS
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "public", "lottie")
SPRITE = os.path.join(HERE, "..", "public", "assets", "badges.svg")


# ───────────────────────────── values and keyframes ─────────────────────────────

EASE = (0.42, 0.0, 0.58, 1.0)
EASE_IN = (0.55, 0.0, 0.9, 0.45)         # speeding up, like something falling
EASE_OUT = (0.1, 0.55, 0.45, 1.0)        # slowing down
LINEAR = (0.0, 0.0, 1.0, 1.0)
SPRING = (0.34, 0.0, 0.2, 1.4)           # a little overshoot


def static(v):
    return {"a": 0, "k": v}


def anim(*keys, ease=EASE):
    """keys: (frame, value) or (frame, value, ease) tuples; a key's ease shapes the move to the next key."""
    frames = []
    for i, key in enumerate(keys):
        t, v = key[0], key[1]
        e = key[2] if len(key) > 2 else ease
        v = v if isinstance(v, list) else [v]
        k = {"t": t, "s": v}
        if i < len(keys) - 1:
            n = len(v)
            k["o"] = {"x": [e[0]] * n, "y": [e[1]] * n}
            k["i"] = {"x": [e[2]] * n, "y": [e[3]] * n}
        frames.append(k)
    return {"a": 1, "k": frames}


def rest(prop):
    """The value a property holds once the animation is over (its last keyframe)."""
    v = prop["k"][-1]["s"] if prop["a"] else prop["k"]
    return v[0] if isinstance(v, list) and len(v) == 1 else v


# ───────────────────────────── shapes ─────────────────────────────

def path(points, closed=False, tangents=None):
    n = len(points)
    zero = [[0, 0]] * n
    ins, outs = tangents if tangents else (zero, zero)
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
        pts.append([round(x, 3), round(y, 3)])
        ins.append([round(-tx * k, 3), round(-ty * k, 3)] if i > 0 else [0, 0])
        outs.append([round(tx * k, 3), round(ty * k, 3)] if i < segments else [0, 0])
    return path(pts, False, (ins, outs))


def ellipse(cx, cy, w, h):
    return {"ty": "el", "p": static([cx, cy]), "s": static([w, h]), "d": 1}


def rect(cx, cy, w, h, r):
    return {"ty": "rc", "p": static([cx, cy]), "s": static([w, h]), "r": static(r), "d": 1}


def stroke(width=5, opacity=100):
    return {"ty": "st", "c": static(INK), "o": static(opacity), "w": static(width), "lc": 2, "lj": 2, "ml": 4}


def fill(opacity=100):
    return {"ty": "fl", "c": static(INK), "o": static(opacity), "r": 1}


def trim(start=None, end=None):
    return {"ty": "tm", "s": start or static(0), "e": end or static(100), "o": static(0), "m": 1}


def draw_on(start, end, ease=EASE):
    return trim(end=anim((start, 0), (end, 100), ease=ease))


def prop2(v):
    return v if isinstance(v, dict) else static(list(v))


def transform(p=(0, 0), a=(0, 0), s=None, r=None, o=None):
    return {"ty": "tr", "p": prop2(p), "a": prop2(a), "s": s or static([100, 100]), "r": r or static(0),
            "o": o or static(100), "sk": static(0), "sa": static(0)}


def group(*items, tr=None):
    return {"ty": "gr", "it": list(items) + [tr or transform()]}


def layer(name, *shapes, p=(48, 48), a=(48, 48), s=None, r=None, o=None, mask=None):
    out = {
        "ddd": 0, "ind": 1, "ty": 4, "nm": name, "sr": 1, "ao": 0, "bm": 0, "ip": 0, "op": 0, "st": 0,
        "ks": {"o": o or static(100), "r": r or static(0),
               "p": static(list(p) + [0]) if not isinstance(p, dict) else p,
               "a": static(list(a) + [0]), "s": s or static([100, 100, 100])},
        "shapes": list(shapes),
    }
    if mask:  # a fixed rectangle (x0, y0, x1, y1) in canvas coordinates; only what is inside shows
        x0, y0, x1, y1 = mask
        out["hasMask"] = True
        out["masksProperties"] = [{"inv": False, "mode": "a", "nm": "window", "o": static(100), "x": static(0),
                                   "pt": static({"c": True, "v": [[x0, y0], [x1, y0], [x1, y1], [x0, y1]],
                                                 "i": [[0, 0]] * 4, "o": [[0, 0]] * 4})}]
    return out


def scene(name, length, layers):
    for i, l in enumerate(layers):
        l["ind"] = i + 1
        l["op"] = length
    return {"v": "5.7.4", "fr": FPS, "ip": 0, "op": length, "w": SIZE, "h": SIZE, "nm": name, "ddd": 0,
            "assets": [], "layers": layers}


# ───────────────────────────── the animations ─────────────────────────────
# Layers listed first are drawn on top (Lottie order).

def lock():
    """The shackle swings shut and drops into the body, which gives a small click."""
    k = 7.18  # 13 * 0.5523: quarter-circle handles for the shackle's 13-unit radius
    shackle = group(path([[35, 44], [35, 33], [48, 20], [61, 33], [61, 44]],
                         tangents=([[0, 0], [0, 0], [-k, 0], [0, -k], [0, 0]],
                                   [[0, 0], [0, -k], [k, 0], [0, 0], [0, 0]])), stroke(5))
    body = group(rect(48, 62, 46, 36, 9), stroke(5))
    hole = group(ellipse(48, 58.5, 9, 9), fill())
    stem = group(path([[48, 60], [48, 68]]), stroke(5))
    return scene("lock", 96, [
        layer("shackle", shackle, p=anim((0, [35, 31, 0]), (16, [35, 31, 0]), (34, [35, 44, 0], EASE), ease=EASE_IN),
              a=(35, 44), r=anim((0, -26), (10, -26), (28, 0))),
        layer("body", body, hole, stem, p=(48, 80), a=(48, 80),
              s=anim((33, [100, 100, 100]), (39, [106, 93, 100]), (54, [100, 100, 100]))),
    ])


def shield():
    """The shield outline draws itself, then the check mark, then a small pop."""
    outline = group(path([[48, 16], [73, 26], [73, 45], [48, 80], [23, 45], [23, 26]], True,
                         tangents=([[0, 0], [0, 0], [0, 0], [14, -9], [0, 15], [0, 0]],
                                   [[0, 0], [0, 0], [0, 15], [-14, -9], [0, 0], [0, 0]])),
                    draw_on(0, 38), stroke(5))
    check = group(path([[36.5, 48], [45, 56.5], [60.5, 40]]), draw_on(36, 56, ease=EASE_OUT), stroke(6))
    pop = anim((54, [100, 100, 100]), (64, [108, 108, 100]), (80, [100, 100, 100]))
    return scene("shield", 96, [layer("shield", check, outline, s=pop)])


def key():
    """The key slides in along its shaft, then turns a full circle around it (a squash through zero)."""
    bow = group(ellipse(27, 48, 26, 26), stroke(5))
    blade = group(path([[40, 48], [80, 48]]), path([[70, 48], [70, 58]]), path([[61, 48], [61, 55]]), stroke(5))
    turn = transform(p=anim((0, [34, 48]), (18, [48, 48]), ease=EASE_OUT), a=(48, 48),
                     s=anim((20, [100, 100], EASE_IN), (32, [100, 0], EASE_OUT), (44, [100, -100], EASE_IN),
                            (56, [100, 0], EASE_OUT), (68, [100, 100])),
                     o=anim((0, 0), (8, 100)))
    return scene("key", 96, [layer("key", group(blade, bow, tr=turn), r=static(-45))])


def timer():
    """The crown is pressed, the 24-hour ring fills, then counts down a quarter while the hand moves on."""
    track = group(arc_path(48, 52, 28, -90, 360, 12), stroke(5, opacity=26))
    ring = group(arc_path(48, 52, 28, -90, 360, 12),
                 trim(start=anim((30, 0), (96, 25), ease=LINEAR), end=anim((2, 0), (26, 100), ease=EASE_OUT)),
                 stroke(5))
    hand = group(path([[48, 52], [48, 35]]), stroke(5),
                 tr=transform(p=(48, 52), a=(48, 52), r=anim((30, 0), (96, 90), ease=LINEAR)))
    hub = group(ellipse(48, 52, 8, 8), fill())
    crown = group(path([[41, 15], [55, 15]]), path([[48, 15], [48, 23]]), stroke(5))
    button = group(path([[69.6, 30.4], [73, 27]]), stroke(5))
    return scene("timer", 110, [
        layer("hand", hub, hand),
        layer("ring", ring),
        layer("crown", crown, button, p=anim((0, [48, 48, 0]), (7, [48, 51, 0]), (16, [48, 48, 0]))),
        layer("track", track),
    ])


def trash():
    """The lid tips open, a file drops in, and the lid falls shut."""
    bin_ = group(path([[30, 40], [33.8, 73], [38.5, 78], [57.5, 78], [62.2, 73], [66, 40]],
                      tangents=([[0, 0], [0, 0], [-3, 0], [0, 0], [-0.4, 3.2], [0, 0]],
                                [[0, 0], [0.4, 3.2], [0, 0], [3, 0], [0, 0], [0, 0]])), stroke(5))
    stripes = group(path([[42, 49], [42.7, 68]]), path([[54, 49], [53.3, 68]]), stroke(4.5))
    lid = group(path([[24, 34], [72, 34]]), path([[40, 34], [40, 27.5], [56, 27.5], [56, 34]]), stroke(5))
    doc = group(path([[40, 36], [51, 36], [57, 42], [57, 60], [40, 60]], True), path([[51, 36], [51, 42], [57, 42]]),
                stroke(4.5),
                tr=transform(p=anim((14, [48, -4]), (42, [48, 72]), ease=EASE_IN), a=(48, 48),
                             r=anim((14, -14), (42, 8)), o=anim((12, 0), (18, 100), (40, 100), (42, 0))))
    return scene("trash", 100, [
        layer("lid", lid, p=anim((0, [24, 34, 0], EASE_OUT), (14, [24, 30, 0]), (44, [24, 30, 0]), (58, [24, 34, 0])),
              a=(24, 34), r=anim((0, 0, EASE_OUT), (14, -32), (44, -32, SPRING), (62, 0))),
        layer("file", doc, mask=(0, -60, 96, 38.5)),
        layer("bin", bin_, stripes, p=(48, 78), a=(48, 78),
              s=anim((56, [100, 100, 100]), (62, [103, 95, 100]), (74, [100, 100, 100]))),
    ])


def eye_off():
    """The eye opens, then a slash draws across it."""
    lids = group(path([[16, 48], [80, 48]], True, tangents=([[14, 20], [-14, -20]], [[14, -20], [-14, 20]])), stroke(5))
    iris = group(ellipse(48, 48, 19, 19), stroke(5))
    slash = group(path([[23, 21], [73, 75]]), draw_on(22, 46, ease=EASE_OUT), stroke(5))
    return scene("eye-off", 84, [
        layer("slash", slash),
        layer("eye", lids, iris, s=anim((0, [100, 6, 100]), (18, [100, 100, 100]), ease=EASE_OUT)),
    ])


ANIMATIONS = {"lock": lock, "shield": shield, "key": key, "timer": timer, "trash": trash, "eye-off": eye_off}


def eye():
    """Static only: the open eye from eye_off(), for "what we do see"."""
    comp = eye_off()
    comp["layers"] = [l for l in comp["layers"] if l["nm"] == "eye"]
    return comp


STATIC_ONLY = {"eye": eye}


# ───────────────────────────── the rest frame as a static SVG ─────────────────────────────

def num(x):
    s = f"{x:.2f}".rstrip("0").rstrip(".")
    return "0" if s == "-0" else s


def svg_transform(p, a, s, r):
    p, a, s = list(p)[:2], list(a)[:2], list(s)[:2]
    if p == a and r == 0 and s == [100, 100]:
        return ""
    out = f"translate({num(p[0])} {num(p[1])})"
    if r:
        out += f" rotate({num(r)})"
    if s != [100, 100]:
        out += f" scale({num(s[0] / 100)} {num(s[1] / 100)})"
    if a != [0, 0]:
        out += f" translate({num(-a[0])} {num(-a[1])})"
    return f' transform="{out}"'


def svg_path_d(ks):
    v, i, o, closed = ks["v"], ks["i"], ks["o"], ks["c"]
    d = [f"M{num(v[0][0])} {num(v[0][1])}"]
    pairs = list(zip(range(len(v) - 1), range(1, len(v)))) + ([(len(v) - 1, 0)] if closed else [])
    for a, b in pairs:
        c1 = [v[a][0] + o[a][0], v[a][1] + o[a][1]]
        c2 = [v[b][0] + i[b][0], v[b][1] + i[b][1]]
        if o[a] == [0, 0] and i[b] == [0, 0]:
            d.append(f"L{num(v[b][0])} {num(v[b][1])}")
        else:
            d.append(f"C{num(c1[0])} {num(c1[1])} {num(c2[0])} {num(c2[1])} {num(v[b][0])} {num(v[b][1])}")
    return "".join(d) + ("Z" if closed else "")


def svg_shape(shape, paint):
    if shape["ty"] == "sh":
        return f'<path d="{svg_path_d(rest(shape["ks"]))}"{paint}/>'
    if shape["ty"] == "el":
        (cx, cy), (w, h) = rest(shape["p"]), rest(shape["s"])
        if w == h:
            return f'<circle cx="{num(cx)}" cy="{num(cy)}" r="{num(w / 2)}"{paint}/>'
        return f'<ellipse cx="{num(cx)}" cy="{num(cy)}" rx="{num(w / 2)}" ry="{num(h / 2)}"{paint}/>'
    if shape["ty"] == "rc":
        (cx, cy), (w, h), r = rest(shape["p"]), rest(shape["s"]), min(rest(shape["r"]), rest(shape["s"])[0] / 2)
        return (f'<rect x="{num(cx - w / 2)}" y="{num(cy - h / 2)}" width="{num(w)}" height="{num(h)}" '
                f'rx="{num(r)}"{paint}/>')
    raise ValueError(shape["ty"])


def svg_group(g):
    *body, tr = g["it"]
    if rest(tr["o"]) == 0:
        return ""
    shapes = [i for i in body if i["ty"] in ("sh", "el", "rc")]
    styles = [i for i in body if i["ty"] in ("st", "fl")]
    cut = next((i for i in body if i["ty"] == "tm"), None)
    out = []
    for g2 in (i for i in body if i["ty"] == "gr"):
        out.append(svg_group(g2))
    for style in reversed(styles):
        o = rest(style["o"])
        if style["ty"] == "st":
            paint = (f' fill="none" stroke="currentColor" stroke-width="{num(rest(style["w"]))}" '
                     f'stroke-linecap="round" stroke-linejoin="round"')
            if o < 100:
                paint += f' stroke-opacity="{num(o / 100)}"'
            if cut:
                s, e = rest(cut["s"]), rest(cut["e"])
                if e <= s:
                    continue
                if (s, e) != (0, 100):
                    paint += f' pathLength="100" stroke-dasharray="{num(e - s)} 100" stroke-dashoffset="{num(-s)}"'
        else:
            paint = ' fill="currentColor"' + (f' fill-opacity="{num(o / 100)}"' if o < 100 else "")
        out.extend(svg_shape(sh, paint) for sh in reversed(shapes))
    inner = "".join(out)
    t = svg_transform(rest(tr["p"]), rest(tr["a"]), rest(tr["s"]), rest(tr["r"]))
    op = rest(tr["o"])
    if t or op < 100:
        return f'<g{t}{f" opacity={chr(34)}{num(op / 100)}{chr(34)}" if op < 100 else ""}>{inner}</g>'
    return inner


def svg_symbol(name, comp):
    parts = []
    for l in reversed(comp["layers"]):
        ks = l["ks"]
        if rest(ks["o"]) == 0:
            continue
        inner = "".join(svg_group(g) for g in reversed(l["shapes"]))
        t = svg_transform(rest(ks["p"]), rest(ks["a"]), rest(ks["s"]), rest(ks["r"]))
        parts.append(f"<g{t}>{inner}</g>" if t else inner)
    return f'  <symbol id="{name}" viewBox="0 0 {SIZE} {SIZE}">{"".join(parts)}</symbol>'


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    symbols = []
    for name, build in ANIMATIONS.items():
        comp = build()
        with open(os.path.join(OUT, f"{name}.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(comp, f, separators=(",", ":"))
        symbols.append(svg_symbol(name, comp))
        print(f"wrote lottie/{name}.json")
    symbols += [svg_symbol(name, build()) for name, build in STATIC_ONLY.items()]
    with open(SPRITE, "w", encoding="utf-8", newline="\n") as f:
        f.write("<!-- Generated by tools/make_lottie.py: the last frame of each badge animation in public/lottie/. -->\n")
        f.write('<svg xmlns="http://www.w3.org/2000/svg">\n' + "\n".join(symbols) + "\n</svg>\n")
    print("wrote assets/badges.svg")
