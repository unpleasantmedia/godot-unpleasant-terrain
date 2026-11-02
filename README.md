# Godot Terrain

A terrain plugin for Godot 4.7 (.NET version). `TerrainNode` draws its ground as a clipmap and collides with it, and brushes in the 3D view sculpt and paint it.

## Features

- Sculpting brushes: Raise, Lower, Smooth, Flatten and Set Height
- Painting with up to 32 textures, or an auto-shader that picks textures by height and slope
- Holes in the ground
- A ground shader included, which you can replace with your own
- Ground split into regions, with only changed regions written when you save
- One undo step per brush stroke
- An event when a brush stroke finishes, for other code to react to

## Requirements

- The **.NET version** of Godot 4.7
- .NET 8 SDK and C# 12
- A C# project: this plugin can't run in a project without one

## Known limitation

**The plugin can't create new ground yet.** Brushes only work on regions that already exist, and a new `TerrainData` has none. A new terrain has nothing to draw or sculpt until region files are made by other means.

## Install

The plugin must end up at exactly `addons/terrain`.

**From the Asset Library:** install the asset. It comes from this repository's `release` branch, which has the files in place under `addons/terrain/`.

**As a submodule:** use the `main` branch, which has the plugin's files at its root:

```bash
git submodule add https://github.com/unpleasantmedia/godot-terrain.git addons/terrain
```

### Then, in Godot

1. If the project has no `.csproj` file, choose **Project > Tools > C# > Create C# solution**.
2. Click **Build** (the hammer at the top right).
3. Enable **Terrain** under **Project > Project Settings > Plugins**.

If you see `Unable to load addon script from path`, the project hasn't been built yet: build it, then enable the plugin again.

## Use

Add a `TerrainNode` to a scene and select it. The brushes appear in the 3D view's toolbar, and a Terrain dock lists the textures to paint with.

| Brush | What it does | With Ctrl |
|---|---|---|
| **Raise** | Raises the ground | Lowers it |
| **Lower** | Lowers the ground | Raises it |
| **Smooth** | Smooths the ground out | |
| **Flatten** | Flattens to the height where the stroke starts | |
| **Set Height** | Brings the ground to the Height setting | Click takes the height from the ground |
| **Paint** | Paints the texture picked in the dock | |
| **Auto** | Hands the ground back to the auto-shader | Takes it off the auto-shader |
| **Holes** | Cuts holes | Fills them |

The toolbar also sets the brush's **Size**, **Falloff** and **Strength**. Edits are written when you save the scene.

### TerrainNode settings

| Setting | What it does |
|---|---|
| **Data** | A `TerrainData` resource holding the ground |
| **Material** | A `ShaderMaterial`; left empty, the included shader is used |
| **Textures** | A `TerrainTextureList` resource of ground textures; their order is the order in the dock |
| **Levels** | How many rings of detail are drawn |
| **Editor Collision** | Whether the ground collides in the editor as well as in the game |
| **Collision Layer**, **Collision Mask** | Physics layers for the ground |

`TerrainData` sets **Region Size** (samples across a region), **Vertex Spacing** (metres between samples), **Folder** (where region files are kept) and **Locations** (which regions it has).

Each `TerrainTexture` needs an **Albedo** (height in alpha) and a **Normal** (roughness in alpha), and has **Tint**, **UV Scale**, **Normal Depth**, **AO Strength** and **Roughness Mod** settings.

### Shader

With **Material** empty, the terrain uses `Shaders/TerrainGround.gdshader`. To change its settings, such as the auto-shader's heights and slopes, assign a `ShaderMaterial` using that shader.

To replace it, assign a `ShaderMaterial` with your own shader. It must take the private uniforms `TerrainNode` sets (the ones starting with `_`), as the included shader does.

## Code

Everything is in the `UnpleasantMedia.Terrain` namespace. `TerrainNode` reads and changes the ground through `HeightAt`, `NormalAt`, `ControlAt`, `Intersect`, `EditHeights`, `EditControl` and `Save`. `ITerrainGround` is the same idea as an interface, for code that shouldn't depend on the node.

### Brush stroke event

`TerrainBrush.StrokeCompleted` fires in the editor when a brush stroke finishes, and gives the terrain and the box of samples changed. A C# event doesn't last through a script reload, so subscribe from a module initializer:

```csharp
using System.Runtime.CompilerServices;
using Godot;
using UnpleasantMedia.Terrain;

internal static class TerrainHook
{
    [ModuleInitializer]
    internal static void Hook() =>
        TerrainBrush.StrokeCompleted += static (_, e) => GD.Print(e.Terrain.Name, " changed ", e.Samples);
}
```

## License

MIT: see [LICENSE](LICENSE).
