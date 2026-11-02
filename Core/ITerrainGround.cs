using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.Terrain;

/// <summary>
/// A level's ground as roads, towns, foliage and the editor tools use it: read anywhere, and
/// changed at points of its grid of samples (<see cref="VertexSpacing"/> metres apart, given by
/// their indices): a <see cref="TerrainNode"/>. <see cref="TerrainGround.Of"/> gives a node's.
/// </summary>
public interface ITerrainGround
{
    /// <summary>How many metres apart its samples are.</summary>
    float VertexSpacing { get; }

    /// <summary>How high the ground is at a place, or NaN where there is none.</summary>
    float HeightAt(Vector3 at);

    /// <summary>Which way is square to the ground at a place, or NaN where there is none.</summary>
    Vector3 NormalAt(Vector3 at);

    /// <summary>The control word at a place (see <see cref="TerrainControl"/>).</summary>
    uint ControlAt(Vector3 at);

    /// <summary>A sample's height (a hole's too), or null outside the ground.</summary>
    float? SampleHeight(int x, int z);

    /// <summary>Where a ray first meets the ground, if it does.</summary>
    Vector3? Intersect(Vector3 from, Vector3 direction);

    /// <summary>A parameter of the ground's material (a colour or a setting of its shader), or nil if it has none set.</summary>
    Variant ShaderParameter(StringName name);

    /// <summary>Sets samples' heights, and redraws the ground.</summary>
    void SetHeights(IEnumerable<(int X, int Z, float Height)> heights);

    /// <summary>Opens or closes holes at samples, and redraws the ground.</summary>
    void SetHoles(IEnumerable<(int X, int Z, bool Hole)> holes);

    /// <summary>Writes its changes to its files.</summary>
    void Save();
}

/// <summary>Finds the ground of a node, or the first in a scene.</summary>
public static class TerrainGround
{
    public static ITerrainGround? Of(Node3D? node) => node as ITerrainGround;

    /// <summary>
    /// The first terrain under a node (a level): from those in the tree, not by going through the
    /// whole level, which a level of tens of thousands of nodes makes slow (and every fence and path asks).
    /// </summary>
    public static Node3D? FindIn(Node? root) =>
        root is null ? null : TerrainNode.InTree.Where(t => GodotObject.IsInstanceValid(t) && (t == root || root.IsAncestorOf(t)))
            .OrderBy(t => t.GetPath().ToString(), System.StringComparer.Ordinal).FirstOrDefault();
}
