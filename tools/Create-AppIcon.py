"""Build Karmac's black-and-white icon with Pillow, without fonts or downloads.

A K drawn as a single stroke: a vertical stem and two arms, with the upper arm
ending in a round earbud head. White on a black rounded tile. SVG and raster
share the geometry below. Every ICO size is rendered at 4x and downsampled.
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
TILE = (8, 8, 248, 248)
RADIUS = 56
WIDTH = 30
STEM = (72, 54, 102, 190)                 # left, top, right, bottom of the stem
UPPER = ((102, 124), (162, 82))           # arm into the earbud head
HEAD = (172, 76, 31)                      # earbud head: centre x, y, radius
LOWER = ((106, 126), (172, 190))          # straight lower arm


def render(size):
    pixels = size * 4
    scale = pixels / 256

    def box(values):
        return tuple(round(value * scale) for value in values)

    def line(draw, points):
        (x1, y1), (x2, y2) = points
        draw.line([(x1 * scale, y1 * scale), (x2 * scale, y2 * scale)], fill=(255, 255, 255, 255), width=round(WIDTH * scale))
        for x, y in points:
            r = WIDTH / 2
            draw.ellipse(box((x - r, y - r, x + r, y + r)), fill=(255, 255, 255, 255))

    image = Image.new("RGBA", (pixels, pixels))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle(box(TILE), radius=round(RADIUS * scale), fill=(0, 0, 0, 255))
    # A hairline keeps the tile's edge visible on a black taskbar.
    if size >= 24:
        draw.rounded_rectangle(box((8.5, 8.5, 247.5, 247.5)), radius=round(RADIUS * scale),
                               outline=(255, 255, 255, 46), width=max(1, round(1.5 * scale)))
    draw.rounded_rectangle(box(STEM), radius=round(WIDTH / 2 * scale), fill=(255, 255, 255, 255))
    line(draw, UPPER)
    line(draw, LOWER)
    x, y, r = HEAD
    draw.ellipse(box((x - r, y - r, x + r, y + r)), fill=(255, 255, 255, 255))
    return image.resize((size, size), Image.Resampling.LANCZOS)


def write_svg(path):
    (ux1, uy1), (ux2, uy2) = UPPER
    (lx1, ly1), (lx2, ly2) = LOWER
    hx, hy, hr = HEAD
    left, top, right, bottom = STEM
    path.write_text(f'''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <title>Karmac</title>
  <desc>A white K whose upper arm ends in an earbud, on a black rounded tile.</desc>
  <rect x="8" y="8" width="240" height="240" rx="{RADIUS}" fill="#000" stroke="#fff" stroke-opacity=".18" stroke-width="1.5"/>
  <rect x="{left}" y="{top}" width="{right - left}" height="{bottom - top}" rx="{WIDTH / 2:g}" fill="#fff"/>
  <path d="M{ux1} {uy1}L{ux2} {uy2}M{lx1} {ly1}L{lx2} {ly2}" stroke="#fff" stroke-width="{WIDTH}" stroke-linecap="round" fill="none"/>
  <circle cx="{hx}" cy="{hy}" r="{hr}" fill="#fff"/>
</svg>
''', encoding="utf-8")


if __name__ == "__main__":
    assets = ROOT / "assets"
    write_svg(assets / "Karmac.svg")
    render(1024).save(assets / "Karmac.png", optimize=True)
    frames = [render(size) for size in SIZES]
    # DIB frames work with System.Drawing.Icon and the Windows shell.
    frames[-1].save(assets / "Karmac.ico", format="ICO", bitmap_format="bmp",
                    sizes=[(size, size) for size in SIZES], append_images=frames[:-1])
    print("Created black-and-white SVG, 1024px PNG, and nine-size Windows ICO")
