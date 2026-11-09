using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>A terrain's ground materials, in a file of their own: each one's id is its place in the list.</summary>
[Tool]
[GlobalClass]
public partial class TerrainMaterialList : Resource
{
    [Export]
    public Godot.Collections.Array<TerrainMaterial> Materials { get; set; } = new();
}
