using Godot;

namespace UnpleasantMedia.Terrain;

/// <summary>A terrain's ground textures, in a file of their own: each one's id is its place in the list.</summary>
[Tool]
[GlobalClass]
public partial class TerrainTextureList : Resource
{
    [Export]
    public Godot.Collections.Array<TerrainTexture> Textures { get; set; } = new();
}
