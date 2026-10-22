r"""Turn the octopus artwork into the three icons HydraClaude ships.

Source: assets/octopus-source.png - a flat-colour octopus on a pink plate.

Outputs (into assets/):
  hydra.ico             colour, transparent, 16-256px  -> exe, window, taskbar, shortcuts
  hydra-tray-light.ico  near-black glyph, 16-32px       -> notification area on a light taskbar
  hydra-tray-dark.ico   white glyph, 16-32px            -> notification area on a dark taskbar
  hydra.icns            colour, 16-256pt @1x/@2x        -> the macOS app and its shortcuts
  hydra-menubar.png     black template glyph, 18pt      -> macOS menu bar (plus @2x)

The .icns is only written on macOS, where iconutil exists. The menu bar glyph is a template
image: macOS recolours it for a light or dark menu bar, so one file replaces the two tray icons.

The pink plate is removed by flood-filling the background colour in from the border, so
background-coloured pixels *inside* the drawing would survive. Edge pixels are un-premultiplied
against the plate colour instead of being hard-keyed, which is what stops a pink fringe showing
up when the icon sits on a dark taskbar.

The tray glyph is a solid silhouette with the interior features (eyes, mouth) punched back out -
line art vanishes at 16px, a silhouette with holes still reads as an octopus.

Pure Pillow, no numpy: the source is 266x266, so plain Python loops are instant.

    python tools/make_icons.py [--debug]
"""

import os
import shutil
import subprocess
import sys
import tempfile
from collections import Counter, deque

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
ASSETS = os.path.join(PROJECT, "assets")
SRC = os.path.join(ASSETS, "octopus-source.png")

# Near-black rather than #000: a pure-black tray glyph reads as a hole, not a shape.
INK_LIGHT = (24, 24, 27)
INK_DARK = (255, 255, 255)

COLOUR_SIZES = [256, 128, 96, 64, 48, 40, 32, 24, 20, 16]
TRAY_SIZES = [32, 24, 20, 16]
# Stops at 256@2x (512px): the artwork is 266px, so anything larger is pure upscale and bulk.
ICNS_SIZES = [16, 32, 128, 256]
MENUBAR_PT = 18           # menu bar extras are drawn at 18pt; the glyph sits inside a 1pt margin
INK_TEMPLATE = (0, 0, 0)  # template images are read for alpha only, so black is the convention

PLATE_TOLERANCE = 44      # colour distance still considered "plate", for the flood fill
EDGE_SOFT = 8             # below this distance the pixel is fully plate
DARK_LUMA = 110           # what counts as line work when building the tray glyph
HOLE_MIN_AREA = 0.004     # interior features smaller than this share of the body are ignored


def load_rgb(path):
    im = Image.open(path).convert("RGB")
    return im, im.size[0], im.size[1], list(im.getdata())


def plate_colour(px):
    return Counter(px).most_common(1)[0][0]


def distance(c, plate):
    return max(abs(c[0] - plate[0]), abs(c[1] - plate[1]), abs(c[2] - plate[2]))


def flood_outside(w, h, dist):
    """Border-connected run of plate-coloured pixels."""
    outside = bytearray(w * h)
    q = deque()
    for x in range(w):
        for i in (x, (h - 1) * w + x):
            if not outside[i] and dist[i] < PLATE_TOLERANCE:
                outside[i] = 1
                q.append(i)
    for y in range(h):
        for i in (y * w, y * w + w - 1):
            if not outside[i] and dist[i] < PLATE_TOLERANCE:
                outside[i] = 1
                q.append(i)
    while q:
        i = q.popleft()
        x, y = i % w, i // w
        for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if 0 <= nx < w and 0 <= ny < h:
                j = ny * w + nx
                if not outside[j] and dist[j] < PLATE_TOLERANCE:
                    outside[j] = 1
                    q.append(j)
    return outside


def cut_plate(im, w, h, px):
    """RGBA with the plate removed and edge pixels un-premultiplied against it."""
    plate = plate_colour(px)
    dist = [distance(c, plate) for c in px]
    outside = flood_outside(w, h, dist)

    out = []
    for i, c in enumerate(px):
        if not outside[i]:
            out.append((c[0], c[1], c[2], 255))
            continue
        d = dist[i]
        if d <= EDGE_SOFT:
            out.append((0, 0, 0, 0))
            continue
        a = min(1.0, (d - EDGE_SOFT) / float(PLATE_TOLERANCE - EDGE_SOFT))
        # observed = a*fg + (1-a)*plate  ->  fg = (observed - (1-a)*plate) / a
        fg = tuple(
            max(0, min(255, int(round((c[k] - (1 - a) * plate[k]) / a))))
            for k in range(3)
        )
        out.append((fg[0], fg[1], fg[2], int(round(a * 255))))

    im2 = Image.new("RGBA", (w, h))
    im2.putdata(out)
    return im2.crop(im2.getbbox()), plate


def square(im, pad_ratio=0.02):
    """Centre on a transparent square with a little breathing room (craft R8: space is deliberate)."""
    w, h = im.size
    side = int(max(w, h) * (1 + pad_ratio * 2))
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(im, ((side - w) // 2, (side - h) // 2), im)
    return canvas


def tray_mask(im, debug=False):
    """Solid body with interior features punched out."""
    w, h = im.size
    px = list(im.getdata())
    body = [1 if p[3] > 128 else 0 for p in px]
    dark = [
        1 if body[i] and (0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2]) < DARK_LUMA else 0
        for i, p in enumerate(px)
    ]

    # The outline is the dark run that touches the edge of the body; everything else dark and
    # enclosed is a feature (eyes, mouth, suckers).
    seen = bytearray(w * h)
    comps = []
    for start in range(w * h):
        if not dark[start] or seen[start]:
            continue
        q = deque([start])
        seen[start] = 1
        comp, touches_edge = [], False
        while q:
            i = q.popleft()
            comp.append(i)
            x, y = i % w, i // w
            for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                if not (0 <= nx < w and 0 <= ny < h):
                    touches_edge = True
                    continue
                j = ny * w + nx
                if not body[j]:
                    touches_edge = True
                elif dark[j] and not seen[j]:
                    seen[j] = 1
                    q.append(j)
        comps.append((comp, touches_edge))

    area = sum(body)
    mask = bytearray(body)
    for comp, touches_edge in comps:
        if touches_edge:
            continue
        if len(comp) / float(area) < HOLE_MIN_AREA:
            continue
        if debug:
            print("  punch feature: %d px (%.2f%% of body)" % (len(comp), 100.0 * len(comp) / area))
        for i in comp:
            mask[i] = 0

    out = Image.new("L", (w, h))
    out.putdata([255 if v else 0 for v in mask])
    return out


def tint(mask, rgb):
    img = Image.new("RGBA", mask.size, rgb + (0,))
    img.putalpha(mask)
    return img


def save_ico(im, path, sizes):
    frames = [im.resize((s, s), Image.LANCZOS) for s in sizes]
    frames[0].save(path, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[1:])
    print("  %-24s %s" % (os.path.basename(path), ", ".join(str(s) for s in sizes)))


def save_icns(im, path):
    if not shutil.which("iconutil"):
        print("  %-24s skipped (iconutil is macOS only)" % os.path.basename(path))
        return
    with tempfile.TemporaryDirectory() as tmp:
        iconset = os.path.join(tmp, "hydra.iconset")
        os.makedirs(iconset)
        for s in ICNS_SIZES:
            im.resize((s, s), Image.LANCZOS).save(os.path.join(iconset, "icon_%dx%d.png" % (s, s)))
            im.resize((s * 2, s * 2), Image.LANCZOS).save(os.path.join(iconset, "icon_%dx%d@2x.png" % (s, s)))
        subprocess.run(["iconutil", "-c", "icns", iconset, "-o", path], check=True)
    print("  %-24s %s @1x/@2x" % (os.path.basename(path), ", ".join(str(s) for s in ICNS_SIZES)))


def save_menubar(mask, path):
    glyph = tint(mask, INK_TEMPLATE)
    for scale, suffix in ((1, ""), (2, "@2x")):
        side = MENUBAR_PT * scale
        inner = side - 2 * scale
        canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
        small = glyph.resize((inner, inner), Image.LANCZOS)
        canvas.paste(small, (scale, scale), small)
        canvas.save(path.replace(".png", suffix + ".png"))
    print("  %-24s %dpt @1x/@2x" % (os.path.basename(path), MENUBAR_PT))


def main():
    debug = "--debug" in sys.argv
    if not os.path.isfile(SRC):
        sys.exit("source artwork not found: %s" % SRC)
    os.makedirs(ASSETS, exist_ok=True)

    im, w, h, px = load_rgb(SRC)
    cut, plate = cut_plate(im, w, h, px)
    print("source %dx%d, plate %s, subject %dx%d" % (w, h, plate, cut.size[0], cut.size[1]))

    colour = square(cut)
    save_ico(colour, os.path.join(ASSETS, "hydra.ico"), COLOUR_SIZES)

    mask = tray_mask(colour, debug=debug)
    save_ico(tint(mask, INK_LIGHT), os.path.join(ASSETS, "hydra-tray-light.ico"), TRAY_SIZES)
    save_ico(tint(mask, INK_DARK), os.path.join(ASSETS, "hydra-tray-dark.ico"), TRAY_SIZES)

    save_icns(colour, os.path.join(ASSETS, "hydra.icns"))
    save_menubar(mask, os.path.join(ASSETS, "hydra-menubar.png"))

    if debug:
        d = os.path.join(ASSETS, "preview")
        os.makedirs(d, exist_ok=True)
        colour.resize((256, 256), Image.LANCZOS).save(os.path.join(d, "colour-256.png"))
        for s in (48, 16):
            colour.resize((s, s), Image.LANCZOS).save(os.path.join(d, "colour-%d.png" % s))
            tint(mask, INK_LIGHT).resize((s, s), Image.LANCZOS).save(os.path.join(d, "tray-%d.png" % s))
        print("previews in %s" % d)


if __name__ == "__main__":
    main()
