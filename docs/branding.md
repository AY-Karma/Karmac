# Karmac icon

A white K drawn as one stroke: a vertical stem and two arms, with the upper arm
ending in a round earbud head. It is the product's initial and an earbud in a
single shape, black and white so it reads at 16 px in the tray, sits quietly on
light and dark taskbars, and matches the app's pure-black interface. A faint
white hairline keeps the tile's edge visible on a black taskbar. The mark is
original geometry and is not an Apple logo.

`python tools/Create-AppIcon.py` regenerates the SVG, 1024px PNG and Windows
ICO from shared geometry using Pillow. The ICO contains independent 16, 20, 24,
32, 40, 48, 64, 128 and 256px DIB frames, each rendered at 4x and downsampled,
for crisp small sizes and compatibility with `System.Drawing.Icon`.
These tools are development dependencies only; end users need no Python packages.

The compiler embeds the ICO as both the executable icon and the tray resource.
Exported artwork is covered by the project MIT license.
