#!/usr/bin/env python3
"""
Nuvia icon builder.

Renders the lowercase Segoe Script "n" (the Nuvia mark) to:
  - src/Nuvia.App/Assets/Nuvia.svg   vector master (exact glyph outline, portable)
  - src/Nuvia.App/Assets/Nuvia.ico   multi-resolution Windows icon
  - logos/v2/final/*.png             preview rasters (for eyeballing)

Reproducible on Windows: uses the system Segoe Script font (segoesc.ttf).
Deps: Pillow, fonttools  (pip install pillow fonttools)
"""
import os

FONT_PATH = r"C:\Windows\Fonts\segoesc.ttf"
LIME = "#A3E635"                      # Nuvia lime — the "n"
LIME_RGBA = (0xA3, 0xE6, 0x35, 255)
TILE = "#1F2419"                      # dark tile so the lime "n" pops on light AND dark backgrounds
TILE_RGBA = (0x1F, 0x24, 0x19, 255)
GLYPH = "n"                           # lowercase, as requested
TILE_MARGIN = 0.04                    # transparent breathing room outside the rounded tile
TILE_RADIUS = 0.20                    # corner radius (fraction of the icon square) — Win11 tile look
GLYPH_PAD = 0.17                      # padding between the "n" and the tile edge
ICO_SIZES = [16, 20, 24, 32, 48, 64, 128, 256]

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(REPO, "src", "Nuvia.App", "Assets")
PREVIEW = os.path.join(REPO, "logos", "v2", "final")
SVG_OUT = os.path.join(ASSETS, "Nuvia.svg")
ICO_OUT = os.path.join(ASSETS, "Nuvia.ico")


def build_svg():
    """Extract the exact 'n' outline from Segoe Script and place it, lime, on a dark rounded tile."""
    from fontTools.ttLib import TTFont
    from fontTools.pens.svgPathPen import SVGPathPen
    from fontTools.pens.boundsPen import BoundsPen

    font = TTFont(FONT_PATH)
    glyph_set = font.getGlyphSet()
    glyph_name = font.getBestCmap()[ord(GLYPH)]

    pen = SVGPathPen(glyph_set)
    glyph_set[glyph_name].draw(pen)
    d = pen.getCommands()

    bp = BoundsPen(glyph_set)
    glyph_set[glyph_name].draw(bp)
    x_min, y_min, x_max, y_max = bp.bounds
    gw, gh = x_max - x_min, y_max - y_min

    V = 256.0
    margin = V * TILE_MARGIN
    inner = V - 2 * margin
    radius = V * TILE_RADIUS
    avail = inner * (1 - 2 * GLYPH_PAD)
    scale = min(avail / gw, avail / gh)
    sw, sh = gw * scale, gh * scale
    tx = (V - sw) / 2.0 - scale * x_min
    ty = (V - sh) / 2.0 + scale * y_max       # + because of the Y flip (font Y-up -> SVG Y-down)

    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-label="Nuvia">
  <!-- Nuvia mark: lowercase "n" (Segoe Script, outlined) in lime on a dark rounded tile. -->
  <rect x="{margin:.2f}" y="{margin:.2f}" width="{inner:.2f}" height="{inner:.2f}" rx="{radius:.2f}" ry="{radius:.2f}" fill="{TILE}"/>
  <g fill="{LIME}" transform="translate({tx:.2f} {ty:.2f}) scale({scale:.5f} {-scale:.5f})">
    <path d="{d}"/>
  </g>
</svg>
'''
    os.makedirs(ASSETS, exist_ok=True)
    with open(SVG_OUT, "w", encoding="utf-8") as f:
        f.write(svg)
    print("wrote", SVG_OUT)


def build_ico():
    """Render the lime glyph, centre it on a dark rounded tile, emit a multi-size .ico."""
    from PIL import Image, ImageDraw, ImageFont

    S = 1024
    font = ImageFont.truetype(FONT_PATH, 720)
    tmp = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(tmp).text((S // 2, S // 2), GLYPH, font=font, fill=LIME_RGBA, anchor="mm")
    glyph = tmp.crop(tmp.getbbox())           # tight crop to the ink
    gw, gh = glyph.size

    # Dark rounded tile so the lime "n" reads on light title bars/taskbars as well as dark.
    margin = int(S * TILE_MARGIN)
    radius = int(S * TILE_RADIUS)
    master = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(master).rounded_rectangle(
        [margin, margin, S - 1 - margin, S - 1 - margin], radius=radius, fill=TILE_RGBA)

    # Fit the glyph inside the tile with padding, preserving its aspect ratio.
    avail = int((S - 2 * margin) * (1 - 2 * GLYPH_PAD))
    scale = min(avail / gw, avail / gh)
    nw, nh = max(1, round(gw * scale)), max(1, round(gh * scale))
    glyph = glyph.resize((nw, nh), Image.LANCZOS)
    master.alpha_composite(glyph, ((S - nw) // 2, (S - nh) // 2))

    master = master.resize((256, 256), Image.LANCZOS)

    # Base image must be the largest frame; ICO stores each requested size.
    master.save(ICO_OUT, format="ICO", sizes=[(s, s) for s in ICO_SIZES])
    print("wrote", ICO_OUT)

    os.makedirs(PREVIEW, exist_ok=True)
    for s in (16, 32, 64, 256):
        p = os.path.join(PREVIEW, f"n-{s}.png")
        master.resize((s, s), Image.LANCZOS).save(p)
        print("wrote", p)


if __name__ == "__main__":
    build_svg()
    build_ico()
