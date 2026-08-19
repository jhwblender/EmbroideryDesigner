# Embroidery Designer

A Windows desktop app for designing patterns on perforated backing where the
holes sit on a **45°-rotated grid** instead of a normal square grid. Instead
of filling in pixels, you draw **threads** — straight lines between any two
holes.

## How the grid works

Holes are addressed by `(column, row)` indices in a normal rectangular
layout — `Cols` holes across, `Rows` holes down, matching what you see on
screen (not a diamond-shaped outline). Every other row is shifted right by
half a cell, which is what makes each hole's 4 nearest neighbors sit at the
true 45° diagonal positions on the fabric, each exactly "Hole spacing" pixels
away. A thread is just a straight line between the on-screen positions of any
two holes — nearest-neighbor "X" stitches and longer skip-stitches
(blackwork/satin style) both work the same way.

## Features

- Configurable grid size (columns × rows) and hole spacing, plus adjustable
  hole size, hole outline color/thickness, and thread thickness — all in the
  settings bar under the menu, with live preview.
- **Left-click a hole and drag to another hole** to add a thread there (any
  hole to any hole, not just neighbors). Doing the same drag again removes
  that thread (toggle).
- **Left-click directly on an existing thread** (away from its endpoints) to
  delete it in one click.
- **Right-click drag** to pan, **mouse wheel** to zoom (toward the cursor).
- **Undo/redo** for thread add/remove and Clear All Threads — toolbar
  buttons, `Ctrl+Z`, and `Ctrl+Y` / `Ctrl+Shift+Z`.
- A color palette: pick the active thread color, add custom colors via the
  system color picker, remove colors you don't need.
- Adjustable canvas background color (default black) and hole outline color
  (default dark gray) via the View menu.
- A live legend panel: thread count and total length per color (length is
  shown in "hole-gap" units, i.e., how many stitch-lengths of floss you'd
  need — independent of zoom/pixel spacing).
- File menu: New, Open/Save project files (`*.etp.json`, plain JSON — easy to
  inspect or version-control), and Export as PNG (for printing or reference
  while you stitch).

## Requirements

- Windows 10/11.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (free) to
  build it. (End users of a published build only need the
  self-contained .exe — see Publish below — not the SDK.)

## Build & run

From this folder, in a terminal (PowerShell or cmd):

```
dotnet build
dotnet run
```

## Publish a standalone .exe (no SDK needed to run it)

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The resulting `EmbroideryDesigner.exe` will be under
`bin\Release\net8.0-windows\win-x64\publish\`. Copy that one file anywhere on
a Windows machine and run it directly.

## Self-updating

Every push to `master` triggers a GitHub Actions workflow
(`.github/workflows/release.yml`) that builds the self-contained exe, tags it
`vX.Y.Z` (patch auto-incremented), and publishes it as a GitHub Release with
auto-generated notes. The running app checks that repo's releases on every
startup and via **Help → Check for Updates...**; if a newer version exists it
downloads it, replaces its own exe, relaunches, and shows what changed. No
installer or admin rights needed — just run the published `.exe` from
anywhere writable.

## Building the installer

An Inno Setup script at `installer\EmbroideryDesigner.iss` produces a single
`EmbroideryDesignerSetup-<version>.exe` that installs to Program Files, adds
Start Menu/desktop shortcuts, and includes an uninstaller. Because it uses a
fixed `AppId`, running a newer installer automatically replaces (rather than
duplicates) whatever version is already installed — bump `MyAppVersion` in
the `.iss` file (and `<Version>` in `EmbroideryDesigner.csproj`) for each new
release so Windows shows the right version in Add/Remove Programs.

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
"C:\Users\Jesse\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer\EmbroideryDesigner.iss
```

The finished installer lands in `installer\output\`. That single .exe is
everything Emily needs — no .NET install required on her end.

## Notes / things you may want to tweak

- Default grid is 30×30 holes at 24px spacing; use the settings bar to
  resize. Very large grids (a few hundred holes per side) will still work
  but get slower to render — say if you want a specific real fabric
  hole-count and it's large, I can add a "render only visible holes"
  optimization.
- Because of the 45° rotation, the horizontal gap between holes in the same
  row is always twice the vertical gap between rows (that keeps true
  diagonal neighbor distance equal to "Hole spacing"). A grid with equal
  Columns and Rows will look about twice as wide as tall; set Rows to
  roughly double the Columns if you want a square-looking canvas.
- The legend's "Length" column is in hole-gap units, not physical
  inches/cm. If you tell me your fabric's actual hole spacing (e.g. holes
  every 1/8"), I can add a real-world length/floss-estimate column.
- Patterns saved before the grid became rectangular (i.e., saved by an
  earlier version of this app) will reload with a different overall shape —
  same thread topology, different outline. Let me know if you have old
  `.etp.json` files you need migrated.
