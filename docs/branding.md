# Karmac icon

The original signal-K mark turns the initial into two flowing audio paths. Ivory
and amber branches sit on an ink-blue tile, giving it a distinct silhouette in
the Windows tray, taskbar, Explorer and Task Manager.

Design references were Apple Music's strong foreground/background contrast,
Linear's simple geometry, and Arc's rounded flowing forms. These informed the
design approach; no logo artwork, typefaces or third-party icon files are used.
The mark is not an Apple, Spotify, Linear or Arc logo.

References: [Apple's icon guidance](https://developer.apple.com/design/human-interface-guidelines/app-icons),
[Microsoft's Windows icon guidance](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icons).

`python tools/Create-AppIcon.py` regenerates the SVG, 1024px transparent PNG,
and Windows ICO from shared geometry using Pillow. The ICO contains independent
16, 20, 24, 32, 40, 48, 64, 128 and 256px DIB frames, supersampled for crisp small
sizes and compatibility with the app's `System.Drawing.Icon` tray icon.
These tools are development dependencies only; end users need no Python packages.

The compiler embeds the ICO as both a Windows executable icon and the app's
tray resource. Exported artwork is covered by the project MIT license.
