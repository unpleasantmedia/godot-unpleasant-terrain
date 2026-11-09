# UnpleasantTerrain

A terrain plugin for Godot 4.7 (.NET version). `TerrainNode` draws its ground as a clipmap and collides with it, and brushes in the 3D view sculpt and paint it.

## Features

- Sculpting brushes: Raise, Lower, Smooth, Flatten, Slope and Set Height
- Importing a heightmap (PNG, EXR, TIFF or 16-bit RAW) to generate the ground, from one file or a grid of tiled files
- Painting with up to 32 textures, or an auto-shader that picks textures by height and slope
- Holes in the ground
- Two ground shaders included, one a visual shader you can edit as a graph; replace them with your own if you like
- Ground split into regions, with only changed regions written when you save
- One undo step per brush stroke
- An event when a brush stroke finishes, for other code to react to

## Requirements

- The **.NET version** of Godot 4.7
- .NET 8 SDK and C# 12
- A C# project: this plugin can't run in a project without one

## Install

The plugin must end up at exactly `addons/unpleasant_terrain`.

**From the Asset Library:** install the asset. It comes from this repository's `release` branch, which has the files in place under `addons/unpleasant_terrain/`.

**As a submodule:** use the `main` branch, which has the plugin's files at its root:

```bash
git submodule add https://github.com/unpleasantmedia/godot-unpleasant-terrain.git addons/unpleasant_terrain
```

### Then, in Godot

1. If the project has no `.csproj` file, choose **Project > Tools > C# > Create C# solution**.
2. Click **Build** (the hammer at the top right).
3. Enable **UnpleasantTerrain** under **Project > Project Settings > Plugins**.

If you see `Unable to load addon script from path`, the project hasn't been built yet: build it, then enable the plugin again.

## Use

Add a `TerrainNode` to a scene and select it. In the Inspector, under **Regions**, set **New Region** (`0, 0` is at the origin; each can be -16 to 15) and press **Add Region**. That adds a flat region at height 0, as one undo step, and creates a `TerrainData` and a folder for it (`res://Terrain/<node name>-Regions`) if the node has none. Repeat for more ground. With no **Textures** assigned, the ground is drawn in the shader's flat colours, picked by height and slope.

To generate ground from a heightmap, select the node and press **Import Heightmap** in the Terrain dock. Choose the file, the **Width** in metres, and the **Min Height** and **Max Height** that black and white stand for. **Existing Regions** either replaces all regions, or overwrites heights only where the map lands and keeps the rest. The map is centred on the terrain's origin. 16-bit files give much smoother ground than 8-bit ones. TIFF files must use strips (not tiles), with no compression, LZW or Deflate. Floating-point files (EXR, 32-bit float TIFF) are scaled from their own lowest and highest values to Min Height and Max Height.

Brushes only work on regions that exist. They appear in the 3D view's toolbar, and a Terrain dock lists the textures to paint with.

| Brush | What it does | With Ctrl |
|---|---|---|
| **Raise** | Raises the ground | Lowers it |
| **Lower** | Lowers the ground | Raises it |
| **Smooth** | Smooths the ground out | |
| **Flatten** | Flattens to the height where the stroke starts | |
| **Slope** | Makes a straight slope across the brush, from the lowest point inside it to the highest | |
| **Set Height** | Brings the ground to the Height setting | Click takes the height from the ground |
| **Paint** | Paints the texture picked in the dock | |
| **Auto** | Hands the ground back to the auto-shader | Takes it off the auto-shader |
| **Holes** | Cuts holes | Fills them |

With Paint picked, **Paint over** in the dock limits it to the textures picked there, or everything if none are.

The toolbar also sets the brush's **Size**, **Falloff** and **Strength**. Edits are written when you save the scene.

Raise, Lower, Paint and Auto can each have their own shape, picked under **Brush shape** in the Terrain dock: a noise, or an image in `addons/unpleasant_terrain/Brushes`. An image's alpha is used, or its brightness if it has no alpha. The shape moves with the brush and is multiplied by its falloff. Each brush also remembers its **Rotation** (or **Random**, a new angle per stroke) and **Scale**, and a noise's **Features**, **Octaves** and **Seed**.

### TerrainNode settings

| Setting | What it does |
|---|---|
| **Data** | A `TerrainData` resource holding the ground |
| **Material** | A `ShaderMaterial`; every terrain starts with its own copy of `Shaders/TerrainGroundDefault.tres`, and clearing this goes back to a new copy |
| **Textures** | A `TerrainMaterialList` resource of ground textures; their order is the order in the dock |
| **Levels** | How many rings of detail are drawn |
| **Editor Collision** | Whether the ground collides in the editor as well as in the game |
| **Collision Layer**, **Collision Mask** | Physics layers for the ground |

`TerrainData` sets **Region Size** (samples across a region), **Vertex Spacing** (metres between samples; set both before adding regions), **Folder** (where region files are kept) and **Locations** (which regions it has).

Each `TerrainMaterial` needs an **Albedo** (height in alpha) and a **Normal** (roughness in alpha), and has **Tint**, **UV Scale**, **Normal Depth**, **AO Strength** and **Roughness Mod** settings.

### Shader

With **Material** empty, the terrain uses the plugin's shader:

- A visual shader made entirely of nodes, generated for your Textures (4 to 32 slots) and saved next to the terrain's data as `<Data name>Ground.tres`. It's rebuilt when the number of Textures or their **Is Paving** boxes change, so **hand edits to a generated graph are lost**; to keep edits, save a copy under another name and assign it in a material (a copy keeps the generated marker, so remove the `terrain_ground_signature` metadata). Its settings are grouped on the `TerrainNode`: **Flat Colours**, **Auto Shader**, **Blending**, **Paving** and **Distance Tiling**. Each texture has its own **Flat Color**, **Flat Roughness**, **Is Paving** and **Auto Shader** rules: a height and slope range and a layer. Higher layers cover lower ones, textures on one layer share the ground they both claim, and ground no rule claims shows the first texture. **Paving Edge Hardness** sets how sharply paving meets the ground: 1 is a clean line, 0 a wide blend. With no Textures, it uses six plain colours, with slots 4 and 5 as paving.

To change settings, or to edit the graph without changing the plugin's copy, assign a `ShaderMaterial` using a copy of that shader.

To replace it, assign a `ShaderMaterial` with your own shader. It must take the private uniforms `TerrainNode` sets (the ones starting with `_`), as the included shader do.

## Code

Everything is in the `UnpleasantMedia.UnpleasantTerrain` namespace. `TerrainNode` reads and changes the ground through `HeightAt`, `NormalAt`, `ControlAt`, `Intersect`, `EditHeights`, `EditControl` and `Save`. `ITerrainGround` is the same idea as an interface, for code that shouldn't depend on the node.

### Brush stroke event

`TerrainBrush.StrokeCompleted` fires in the editor when a brush stroke finishes, and gives the terrain and the box of samples changed. A C# event doesn't last through a script reload, so subscribe from a module initializer:

```csharp
using System.Runtime.CompilerServices;
using Godot;
using UnpleasantMedia.UnpleasantTerrain;

internal static class TerrainHook
{
    [ModuleInitializer]
    internal static void Hook() =>
        TerrainBrush.StrokeCompleted += static (_, e) => GD.Print(e.Terrain.Name, " changed ", e.Samples);
}
```

## Masks

A mask hides the ground in an area without changing its data, following any outline rather than the 2 m grid that holes follow. Each shape has a height: ground below it is neither drawn nor solid, ground above it stays, and collision matches the outline.

```csharp
terrain.SetMask("tunnel:1", shapes, tops);   // convex shapes as Vector2[][] in world X and Z, and a height for each
terrain.RemoveMask("tunnel:1");
```

Masks aren't saved, so set them each time your code loads. A shader of your own needs to read `terrain_mask` to hide them, as the included one does.

## License

MIT: see [LICENSE](LICENSE).
