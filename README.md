# Godot Terrain

A terrain plugin for Godot 4.7 .NET projects: a `TerrainNode` that draws and collides its ground, with brushes in the 3D view to sculpt and paint it.

## Install

Put this repository at `addons/terrain` (the path must be exactly that), for example as a submodule:

```bash
git submodule add https://github.com/unpleasantmedia/godot-terrain.git addons/terrain
```

Build the C# project, then enable **Terrain** under Project Settings > Plugins. The project needs .NET 8 and C# 12.

## Use

Add a `TerrainNode` to a scene and give it a `TerrainData` resource. Select it to get the brushes in the 3D view's toolbar.

### Shader

With **Material** left empty, the terrain uses `Shaders/TerrainGround.gdshader`. To override it, assign your own `ShaderMaterial`; its shader must take the private uniforms `TerrainNode` sets, as the default shader does.

### Brush stroke event

Other code can react when a brush stroke finishes. `TerrainBrush.StrokeCompleted` gives the terrain and the box of samples changed. A C# event doesn't last through a script reload, so subscribe from a module initializer:

```csharp
[ModuleInitializer]
internal static void Hook() =>
    TerrainBrush.StrokeCompleted += static (_, e) => GD.Print(e.Terrain.Name, e.Samples);
```

## License

MIT: see [LICENSE](LICENSE).
