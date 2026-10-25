"""Build docs/brand/lockup.svg and lockup-dark.svg: the bunny-and-receipt mark plus the "tattlebun" wordmark, outlined.

Usage, from the repo root:
  uv run --no-project --with fonttools --with uharfbuzz python -I scripts/make_lockup.py <Fredoka-SemiBold.ttf> docs/brand

Fredoka (OFL) is a variable font in github.com/google/fonts under ofl/fredoka. Make the SemiBold instance first:
  python -m fontTools.varLib.instancer "Fredoka[wdth,wght].ttf" wght=600 wdth=100 -o Fredoka-SemiBold.ttf
The mark and the wordmark's colour come from mark.svg and mark-dark.svg, so rerun this when the mark changes.
"""
import re
import sys
from pathlib import Path

import uharfbuzz as hb
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont

font_path = Path(sys.argv[1])
brand = Path(sys.argv[2])

# The mark's ink in its 64-unit box: the receipt's 4-unit stroke spans x 10-54 and its torn edge reaches 61.5, and the upright ear's top 3.
# Update this with the mark, or the gap to the wordmark goes uneven.
bx0, by0, bx1, by1 = 10.0, 3.0, 54.0, 61.5
bw, bh = bx1 - bx0, by1 - by0


def n(v: float) -> str:
    t = f"{v:.3f}".rstrip("0").rstrip(".")
    return "0" if t in ("-0", "") else t


def mark(name: str) -> tuple[str, str]:
    """The mark's drawing and its ink colour, which the wordmark takes too."""
    text = (brand / name).read_text(encoding="utf-8")
    assert 'viewBox="0 0 64 64"' in text, f"{name} left its 64-unit box; update the ink box above"
    body = re.search(r"</title>(.*)</svg>", text, re.S).group(1)
    ink = re.search(r'stroke="(#[0-9a-fA-F]{6})"', body).group(1)
    return body, ink


font = TTFont(font_path)
upm = font["head"].unitsPerEm
gs = font.getGlyphSet()

hbfont = hb.Font(hb.Face(hb.Blob.from_file_path(str(font_path))))
buf = hb.Buffer()
buf.add_str("tattlebun")
buf.guess_segment_properties()
hb.shape(hbfont, buf, {"kern": True, "liga": True})
order = font.getGlyphOrder()
glyphs = [(order[i.codepoint], p.x_advance, p.x_offset, p.y_offset) for i, p in zip(buf.glyph_infos, buf.glyph_positions)]
print("shaped glyphs:", [g[0] for g in glyphs])

# The mark's ink is about as tall as Fredoka's caps (0.7 em) and centred halfway up its x-height (0.5 em).
EM = 100.0
fs = EM / upm
mark_h = 0.68 * EM
gap = 0.28 * EM
mark_cy = -0.25 * EM
tracking = -0.01 * upm

m = mark_h / bh
mark_w = bw * m

first = BoundsPen(gs)
gs[glyphs[0][0]].draw(first)
text_x0 = mark_w + gap - first.bounds[0] * fs  # gap measured ink to ink

parts = []
wb = BoundsPen(gs)
x = 0.0
for idx, (gname, adv, xo, yo) in enumerate(glyphs):
    t = (fs, 0, 0, -fs, text_x0 + (x + xo) * fs, -yo * fs)
    sp = SVGPathPen(gs, ntos=n)
    gs[gname].draw(TransformPen(sp, t))
    parts.append(sp.getCommands())
    gs[gname].draw(TransformPen(wb, t))
    x += adv + (tracking if idx < len(glyphs) - 1 else 0)
wx0, wy0, wx1, wy1 = wb.bounds

mx = -bx0 * m
my = mark_cy - (by0 + bh / 2) * m
mark_y0, mark_y1 = mark_cy - mark_h / 2, mark_cy + mark_h / 2

x0, y0 = min(0.0, wx0), min(mark_y0, wy0)
x1, y1 = max(mark_w, wx1), max(mark_y1, wy1)
W, H = x1 - x0, y1 - y0
print(f"lockup: mark {mark_w:.1f}x{mark_h:.1f} gap {gap:.1f} word ink {wx0:.1f}..{wx1:.1f} / {wy0:.1f}..{wy1:.1f}; size {W:.1f}x{H:.1f}")

word_d = " ".join(p for p in parts if p)
for source, name in (("mark.svg", "lockup.svg"), ("mark-dark.svg", "lockup-dark.svg")):
    body, ink = mark(source)
    svg = (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{n(W)}" height="{n(H)}" viewBox="0 0 {n(W)} {n(H)}" '
        f'role="img" aria-label="Tattlebun"><title>Tattlebun</title>'
        f'<g transform="translate({n(-x0)} {n(-y0)})">'
        f'<g fill="none" transform="translate({n(mx)} {n(my)}) scale({m:.5f})">{body}</g>'
        f'<path d="{word_d}" fill="{ink}"/>'
        "</g></svg>\n"
    )
    (brand / name).write_text(svg, encoding="utf-8", newline="\n")

print("wrote lockup.svg and lockup-dark.svg")
