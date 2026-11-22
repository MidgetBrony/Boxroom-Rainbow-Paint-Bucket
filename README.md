# Rainbow Paint Bucket

Rainbow Paint Bucket adds a selectable paint material to BOXROOM's Mods
catalogue. It uses BOXROOM's normal painting interaction, but lets you choose
any RGB color or six-digit hex code instead of being limited to the built-in
paint colors.

It works anywhere BOXROOM accepts paint, including walls, ceilings, floors and
paintable furniture.

## Features

- BOXROOM-native RGB, HSV and hex color picker
- Any six-digit color such as `#FF4FA3`
- Normal single-surface and whole-room painting
- Named favorite colors with live swatches
- Compact trash buttons for removing favorites
- Favorites persist between game sessions and mod updates
- Current paint color is shown on the native paint-roller icon
- Painted colors persist in BOXROOM room saves

## Controls

1. Open BOXROOM's catalogue and select **Rainbow Paint Bucket** from the Mods
   section.
2. **Right-click** while the paint is active to open the native color picker.
3. Pick a color visually or type its hex value, then close the picker.
4. Paint normally with **left-click**.
5. Hold BOXROOM's placement modifier (**Shift** by default) and left-click to
   paint the whole room, just like the standard paint tool.

Use **Shift + right-click** to open the favorites menu. Select a favorite to
use it, name the current color to save it, or use the trash icon to remove it.

Favorites are stored in
`UserData/RainbowPaintBucket/favorites.json`. A backup is kept beside the file
so updating the mod does not erase your palette.

## Installation

Install **Rainbow Paint Bucket** through BoxMate. BoxMate will also install or
update the required **ModsPanel 2.7.8** dependency.

For a manual installation, place `RainbowPaintBucket.dll` and a compatible
`ModsPanel.dll` in BOXROOM's `Mods` folder.

## Building

Copy `Directory.Build.user.props.example` to `Directory.Build.user.props` and
set `GamePath`, or provide `-p:GamePath=...` when building.

```powershell
dotnet build -c Release
```

## Version

Current release: **0.1.11**

