using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// A terrain's ground: square regions of samples, <see cref="RegionSize"/> across and
/// <see cref="VertexSpacing"/> metres apart, each in a file of its own in <see cref="Folder"/>
/// (named by <see cref="FileOf"/>). Regions are loaded when first asked for. Its heights, normals
/// and control can be read anywhere in its regions; outside them, and in holes, there is no ground.
/// </summary>
[Tool]
[GlobalClass]
public partial class TerrainData : Resource
{
    /// <summary>How many samples across a region is.</summary>
    [Export]
    public int RegionSize { get; set; } = 512;

    /// <summary>How many metres apart its samples are.</summary>
    [Export(PropertyHint.Range, "0.25,10,0.25,suffix:m")]
    public float VertexSpacing { get; set; } = 2f;

    /// <summary>The folder its regions' files are in.</summary>
    [Export(PropertyHint.Dir)]
    public string Folder { get; set; } = "";

    /// <summary>Which regions it has.</summary>
    [Export]
    public Godot.Collections.Array<Vector2I> Locations
    {
        get => _locations;
        set
        {
            _locations = value;
            // Looked for again, in case it has them now.
            _looked.Clear();
        }
    }

    private Godot.Collections.Array<Vector2I> _locations = new();

    // Godot's own collections, and not readonly: the editor keeps these when it rebuilds the
    // scripts. A plain dictionary was lost then, and with nothing else holding them its regions
    // were let go, edits not yet saved and all, to be read again from their files.
    private Godot.Collections.Dictionary<Vector2I, TerrainRegion> _regions = new();

    /// <summary>
    /// The regions looked up so far (missing ones as null), in a plain dictionary in front of the
    /// Godot one above: every height asked for looks its region up, and the Godot one is slow to ask
    /// (each time into the engine and back). Lost as the scripts are rebuilt, it is filled again from that one.
    /// </summary>
    private readonly Dictionary<Vector2I, TerrainRegion?> _looked = new();
    /// <summary>The file a region is kept in, as Region-X+01-Z-02.res.</summary>
    public static string FileOf(Vector2I location) => $"Region-X{location.X:+00;-00}-Z{location.Y:+00;-00}.res";

    /// <summary>How many metres across a region is.</summary>
    public float RegionWidth => RegionSize * VertexSpacing;

    /// <summary>A region, if it has one there.</summary>
    public TerrainRegion? Region(Vector2I location)
    {
        if (_looked.TryGetValue(location, out var known))
            return known;
        if (_regions.TryGetValue(location, out var loaded))
            return _looked[location] = loaded;
        var path = $"{Folder}/{FileOf(location)}";
        var region = Locations.Contains(location) && ResourceLoader.Exists(path) ? GD.Load<TerrainRegion>(path) : null;
        if (region is not null)
            _regions[location] = region;
        return _looked[location] = region;
    }

    /// <summary>Adds a flat region at height 0, left to the auto-shader, kept in memory until saved. False if it has one there.</summary>
    public bool AddRegion(Vector2I location)
    {
        if (Locations.Contains(location))
            return false;
        var heights = Image.CreateEmpty(RegionSize, RegionSize, false, Image.Format.Rf);
        var control = Image.CreateEmpty(RegionSize, RegionSize, false, Image.Format.Rf);
        var colors = Image.CreateEmpty(RegionSize, RegionSize, false, Image.Format.Rgba8);
        // A white tint, and neither wet nor dry.
        colors.Fill(new Color(1f, 1f, 1f, 0.5f));
        colors.GenerateMipmaps();
        var region = new TerrainRegion { Location = location, Heights = heights, Control = control, Colors = colors, Modified = true };
        System.Array.Fill(region.ControlValues, TerrainControl.AutoBit);
        region.Commit(false, true);
        _regions[location] = region;
        _looked.Remove(location);
        Locations.Add(location);
        _locationsChanged = true;
        return true;
    }

    /// <summary>Takes a region away (its file, if it has been saved, is left unused).</summary>
    public void RemoveRegion(Vector2I location)
    {
        Locations.Remove(location);
        _regions.Remove(location);
        _looked.Remove(location);
        _locationsChanged = true;
    }

    /// <summary>Its regions made these (copies of them), the rest taken away.</summary>
    public void PutRegions(Godot.Collections.Array<TerrainRegion> regions)
    {
        foreach (var old in Locations.ToList())
            RemoveRegion(old);
        foreach (var region in regions)
        {
            var copy = (TerrainRegion)region.Duplicate(true);
            copy.Modified = true;
            _regions[copy.Location] = copy;
            _looked.Remove(copy.Location);
            Locations.Add(copy.Location);
        }

        _locationsChanged = true;
    }

    private bool _locationsChanged;

    /// <summary>Which region a sample is in, and where in it.</summary>
    public (TerrainRegion? Region, int Index) Sample(int x, int z)
    {
        var location = new Vector2I(Mathf.FloorToInt(x / (float)RegionSize), Mathf.FloorToInt(z / (float)RegionSize));
        var region = Region(location);
        return (region, (z - location.Y * RegionSize) * RegionSize + (x - location.X * RegionSize));
    }

    /// <summary>A sample's height (holes keep theirs), or NaN where there is no region.</summary>
    public float HeightOf(int x, int z)
    {
        var (region, index) = Sample(x, z);
        var heights = region?.HeightValues;
        return heights is not null && index < heights.Length ? heights[index] : float.NaN;
    }

    /// <summary>A sample's control word, or 0 where there is no region.</summary>
    public uint ControlOf(int x, int z)
    {
        var (region, index) = Sample(x, z);
        var control = region?.ControlValues;
        return control is not null && index < control.Length ? control[index] : 0u;
    }

    /// <summary>How high the ground is at a place (between its samples, blended from the four round it), or NaN where there is none.</summary>
    public float HeightAt(Vector3 at)
    {
        if (TerrainControl.IsHole(ControlAt(at)))
            return float.NaN;
        var x = at.X / VertexSpacing;
        var z = at.Z / VertexSpacing;
        var x0 = Mathf.FloorToInt(x);
        var z0 = Mathf.FloorToInt(z);
        var fx = x - x0;
        var fz = z - z0;
        var a = HeightOf(x0, z0);
        var b = HeightOf(x0 + 1, z0);
        var c = HeightOf(x0, z0 + 1);
        var d = HeightOf(x0 + 1, z0 + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
    }

    /// <summary>Which way is square to the ground at a place (from the slope to a sample's spacing further along x and z), or NaN where there is none.</summary>
    public Vector3 NormalAt(Vector3 at)
    {
        var height = HeightAt(at);
        var alongX = HeightAt(at + new Vector3(VertexSpacing, 0f, 0f));
        var alongZ = HeightAt(at + new Vector3(0f, 0f, VertexSpacing));
        return new Vector3(height - alongX, VertexSpacing, height - alongZ).Normalized();
    }

    /// <summary>The control word of the sample at or before a place.</summary>
    public uint ControlAt(Vector3 at) => ControlOf(Mathf.FloorToInt(at.X / VertexSpacing), Mathf.FloorToInt(at.Z / VertexSpacing));

    /// <summary>Saves the regions with edits not yet saved.</summary>
    public void SaveModified()
    {
        foreach (var region in _regions.Values)
        {
            if (region is not { Modified: true })
                continue;
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(Folder));
            region.UpdateHeightRange();
            ResourceSaver.Save(region, $"{Folder}/{FileOf(region.Location)}", ResourceSaver.SaverFlags.Compress);
            region.Modified = false;
        }

        // In a file of its own, not the scene: which regions it has saved there.
        if (_locationsChanged && ResourcePath.StartsWith("res://") && !ResourcePath.Contains("::"))
            ResourceSaver.Save(this, ResourcePath);
        _locationsChanged = false;
    }
}
