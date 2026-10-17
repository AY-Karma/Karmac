"""Compose README visuals from background-only, synthetic app UI captures."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
CAPTURES = ROOT / "artifacts/ui-release"
OUTPUT = ROOT / "docs/assets"
OUTPUT.mkdir(parents=True, exist_ok=True)

WHITE = "#f4f4f4"
MUTED = "#a6a6a6"
DIM = "#777777"


def font(size, bold=False):
    return ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf" if bold else "C:/Windows/Fonts/segoeui.ttf", size)


def capture(key):
    return Image.open(CAPTURES / ("catalog-" + key + "-synthetic-fixture.png")).convert("RGB")


def center(draw, text, y, size, fill, midpoint, bold=False):
    face = font(size, bold)
    bounds = draw.textbbox((0, 0), text, font=face)
    draw.text((round(midpoint - (bounds[2] - bounds[0]) / 2), y), text, fill=fill, font=face)


def overview():
    canvas = Image.new("RGB", (1500, 1000), "black")
    draw = ImageDraw.Draw(canvas)
    draw.text((60, 34), "KARMAC / WINDOWS", font=font(19, True), fill=MUTED)
    draw.text((56, 72), "Your Apple audio. One click away.", font=font(53, True), fill=WHITE)
    draw.text((60, 149), "Connect, follow live progress, and see available battery readings.", font=font(24), fill=MUTED)

    entries = [("airpods-pro-3", "AirPods Pro 3"),
               ("airpods-max-usbc", "AirPods Max (USB-C)"),
               ("beats-studio-pro", "Beats Studio Pro")]
    for index, (key, label) in enumerate(entries):
        x = 60 + index * 480
        picture = capture(key)
        picture.thumbnail((420, 620), Image.Resampling.LANCZOS)
        canvas.paste(picture, (x, 226))
        center(draw, label, 798, 25, WHITE, x + 210, True)

    draw.text((60, 878), "APP PRESENTATION PREVIEWS", font=font(18, True), fill=MUTED)
    draw.text((60, 915), "Example charge values and synthetic device states. These are not hardware compatibility or speed measurements.",
              font=font(21), fill=MUTED)
    draw.text((60, 951), "Original app artwork shown. Battery availability depends on the device and Windows.", font=font(20), fill=DIM)
    canvas.save(OUTPUT / "app-overview.png", optimize=True)


def lineup():
    canvas = Image.new("RGB", (1440, 1140), "black")
    draw = ImageDraw.Draw(canvas)
    draw.text((48, 32), "DEVICE ARTWORK", font=font(18, True), fill=MUTED)
    draw.text((44, 68), "Recognizable at a glance.", font=font(49, True), fill=WHITE)
    draw.text((48, 139), "AirPods, AirPods Pro, AirPods Max and Beats — with model-aware presentation.", font=font(23), fill=MUTED)
    entries = [("airpods-1", "AirPods (1st generation)"),
               ("airpods-4-anc", "AirPods 4 with ANC"),
               ("airpods-pro-3", "AirPods Pro 3"),
               ("airpods-max-usbc", "AirPods Max (USB-C)"),
               ("beats-studio-pro", "Beats Studio Pro"),
               ("beats-fit-pro", "Beats Fit Pro"),
               ("beats-flex", "Beats Flex"),
               ("powerbeats-pro-2", "Powerbeats Pro 2"),
               ("airpods-pro-2-usbc", "AirPods Pro (2nd generation)"),
               ("beats-studio-buds", "Beats Studio Buds"),
               ("beats-solo-4", "Beats Solo 4"),
               ("powerbeats-3", "Powerbeats3 Wireless")]
    for index, (key, label) in enumerate(entries):
        column, row = index % 4, index // 4
        midpoint = 192 + column * 352
        top = 212 + row * 270
        art = capture(key).crop((90, 58, 410, 246))
        art.thumbnail((280, 200), Image.Resampling.LANCZOS)
        canvas.paste(art, (midpoint - art.width // 2, top))
        center(draw, label, top + 196, 21, WHITE, midpoint)
    draw.text((48, 1055), "Original app artwork • An illustrative selection of models in the catalog", font=font(21), fill=MUTED)
    draw.text((48, 1093), "Artwork recognition does not establish device-tested connection speed or battery support.", font=font(20), fill=DIM)
    canvas.save(OUTPUT / "device-lineup.png", optimize=True)


if __name__ == "__main__":
    overview()
    lineup()
    for path in OUTPUT.glob("*.png"):
        print(path.name, path.stat().st_size)
