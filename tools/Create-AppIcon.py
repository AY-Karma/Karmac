"""Build the original signal-K icon with Pillow, without fonts or downloads.
SVG and raster masks share geometry. Every ICO size is rendered at 4x.
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
UPPER = ((82, 128), (129, 128), (137, 70), (181, 70))
LOWER = ((82, 128), (129, 128), (137, 186), (181, 186))


def gradient(size, first, last):
    image = Image.new("RGBA", (size, size))
    draw = ImageDraw.Draw(image)
    for y in range(size):
        mix = y / (size - 1)
        color = tuple(round(a + (b-a)*mix) for a, b in zip(first, last))
        draw.line((0, y, size, y), fill=color)
    return image


def render(size):
    pixels = size * 4
    scale = pixels / 256
    def box(values):
        return tuple(round(value * scale) for value in values)
    def mask():
        return Image.new("L", (pixels, pixels))
    def ribbon(points):
        shape = mask()
        draw = ImageDraw.Draw(shape)
        curve = []
        for step in range(161):
            t = step / 160
            weights = ((1-t)**3, 3*(1-t)**2*t, 3*(1-t)*t*t, t**3)
            curve.append(tuple(round(sum(w*p[axis] for w, p in zip(weights, points))*scale) for axis in (0, 1)))
        draw.line(curve, fill=255, width=round(29*scale), joint="curve")
        for x, y in (points[0], points[-1]):
            draw.ellipse(box((x-14.5, y-14.5, x+14.5, y+14.5)), fill=255)
        return shape

    image = Image.new("RGBA", (pixels, pixels))
    tile = mask()
    ImageDraw.Draw(tile).rounded_rectangle(box((8, 8, 248, 248)), radius=round(55*scale), fill=255)
    image.paste(gradient(pixels, (41, 52, 68), (9, 15, 24)), (0, 0), tile)
    if size >= 32:
        ImageDraw.Draw(image).rounded_rectangle(box((8.5, 8.5, 247.5, 247.5)), radius=round(55*scale),
                                               outline=(103, 119, 139, 120), width=max(1, round(scale)))
    # Warm lower branch sits behind the ivory upper branch and upright.
    image.paste(gradient(pixels, (255, 199, 114), (244, 89, 61)), (0, 0), ribbon(LOWER))
    image.paste(gradient(pixels, (255, 242, 211), (255, 163, 82)), (0, 0), ribbon(UPPER))
    stem = mask()
    ImageDraw.Draw(stem).rounded_rectangle(box((60, 55, 89, 201)), radius=round(14.5*scale), fill=255)
    image.paste(gradient(pixels, (255, 249, 231), (255, 202, 130)), (0, 0), stem)
    return image.resize((size, size), Image.Resampling.LANCZOS)


def svg_path(points):
    return "M{} {} C{} {},{} {},{} {}".format(*(coordinate for point in points for coordinate in point))


def write_svg(path):
    path.write_text(f'''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <title>Karmac</title>
  <desc>An ivory and amber K with flowing signal branches on an ink-blue tile.</desc>
  <defs>
    <linearGradient id="tile" x2="0" y2="1"><stop stop-color="#293444"/><stop offset="1" stop-color="#090f18"/></linearGradient>
    <linearGradient id="lower" x2="0" y2="256" gradientUnits="userSpaceOnUse"><stop stop-color="#ffc772"/><stop offset="1" stop-color="#f4593d"/></linearGradient>
    <linearGradient id="upper" x2="0" y2="256" gradientUnits="userSpaceOnUse"><stop stop-color="#fff2d3"/><stop offset="1" stop-color="#ffa352"/></linearGradient>
    <linearGradient id="stem" x2="0" y2="256" gradientUnits="userSpaceOnUse"><stop stop-color="#fff9e7"/><stop offset="1" stop-color="#ffca82"/></linearGradient>
  </defs>
  <rect x="8" y="8" width="240" height="240" rx="55" fill="url(#tile)" stroke="#67778b" stroke-opacity=".47"/>
  <path d="{svg_path(LOWER)}" stroke="url(#lower)" stroke-width="29" stroke-linecap="round" fill="none"/>
  <path d="{svg_path(UPPER)}" stroke="url(#upper)" stroke-width="29" stroke-linecap="round" fill="none"/>
  <rect x="60" y="55" width="29" height="146" rx="14.5" fill="url(#stem)"/>
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
    print("Created original SVG, 1024px PNG, and nine-size Windows ICO")
