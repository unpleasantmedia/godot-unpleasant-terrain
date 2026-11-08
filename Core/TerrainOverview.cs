using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// The whole terrain, small: every region's heights, control and colours taken every
/// <see cref="OverviewStep"/> samples, in one set of textures. Where a region is not loaded (further
/// off than <see cref="NearDistance"/> in the game), its shader draws the ground from these instead,
/// so only the regions near the camera need their full maps on the GPU. Made as it is set up, from
/// the regions it has loaded anyway; edits are taken into it as they are made.
/// </summary>
public partial class TerrainNode
{
    /// <summary>How far from the camera regions have their full maps (and are solid) in the game, in metres, when the overview stands in for those further off.</summary>
    public const float NearDistance = 1000f;

    /// <summary>How many samples apart the overview's texels are: 8 metres, at a metre between samples.</summary>
    private const int OverviewStep = 8;

    /// <summary>How long edits are gathered before the overview is given to the GPU again, in seconds.</summary>
    private const double OverviewDelay = 0.25;

    /// <summary>Whether its shader draws far ground from the overview (one built before it does not).</summary>
    private bool _overview;

    private Vector2I _overviewFirst;
    private int _overviewTexels;
    private float[] _overviewHeights = [];
    private uint[] _overviewControl = [];
    private byte[] _overviewColors = [];
    private readonly List<ImageTexture> _overviewTextures = [];
    private bool _overviewDirty;
    private double _overviewWait;

    /// <summary>How many of the overview's texels a region is across.</summary>
    private int OverviewCells => Math.Max(1, _data!.RegionSize / OverviewStep);

    /// <summary>Whether its shader reads the overview.</summary>
    private bool ShaderHasOverview() =>
        _baked && RenderingServer.GetShaderParameterList(Drawn.Shader.GetRid()).Any(p => p["name"].AsString() == "terrain_overview_heights");

    /// <summary>
    /// Makes the overview from every region, and gives it to the shader. A texel of holes rings it
    /// (and stands where it has no region), so nothing is drawn past its edge.
    /// </summary>
    private void SetUpOverview(Rid material, List<TerrainRegion> regions)
    {
        _overview = ShaderHasOverview();
        if (!_overview || regions.Count == 0)
        {
            _overview = false;
            return;
        }

        var low = new Vector2I(regions.Min(r => r.Location.X), regions.Min(r => r.Location.Y));
        var high = new Vector2I(regions.Max(r => r.Location.X), regions.Max(r => r.Location.Y));
        var across = Math.Max(high.X - low.X, high.Y - low.Y) + 1;
        _overviewFirst = low;
        _overviewTexels = across * OverviewCells + 2;
        var count = _overviewTexels * _overviewTexels;
        _overviewHeights = new float[count];
        _overviewControl = new uint[count];
        System.Array.Fill(_overviewControl, TerrainControl.HoleBit);
        _overviewColors = new byte[count * 4];
        System.Array.Fill(_overviewColors, (byte)255);
        foreach (var region in regions)
            TakeIntoOverview(region);

        var step = _data!.RegionSize / OverviewCells;
        SetParam(material, "_overview_x", (float)(low.X * _data.RegionSize - step));
        SetParam(material, "_overview_z", (float)(low.Y * _data.RegionSize - step));
        SetParam(material, "_overview_step", (float)step);
        SetParam(material, "_overview_texels", (float)_overviewTexels);
        GiveOverview(material);
    }

    /// <summary>A region's samples, every so many, written into the overview.</summary>
    private void TakeIntoOverview(TerrainRegion region)
    {
        if (region.Heights is null || region.Control is null || region.Colors is null)
            return;
        var size = _data!.RegionSize;
        var cells = OverviewCells;
        var step = size / cells;
        var heights = region.Heights.GetData();
        var control = region.Control.GetData();
        // Its colours' first level: the image has mipmaps after it.
        var colors = region.Colors.GetData();
        var firstX = (region.Location.X - _overviewFirst.X) * cells + 1;
        var firstZ = (region.Location.Y - _overviewFirst.Y) * cells + 1;
        for (var z = 0; z < cells; z++)
        {
            var row = (firstZ + z) * _overviewTexels + firstX;
            for (var x = 0; x < cells; x++)
            {
                var sample = z * step * size + x * step;
                _overviewHeights[row + x] = BitConverter.ToSingle(heights, sample * 4);
                _overviewControl[row + x] = BitConverter.ToUInt32(control, sample * 4);
                Buffer.BlockCopy(colors, sample * 4, _overviewColors, (row + x) * 4, 4);
            }
        }
    }

    /// <summary>The overview's textures made from it as it is now, and given to the shader (or updated, if they were already).</summary>
    private void GiveOverview(Rid material)
    {
        var texels = _overviewTexels;
        var heightBytes = new byte[_overviewHeights.Length * 4];
        Buffer.BlockCopy(_overviewHeights, 0, heightBytes, 0, heightBytes.Length);
        var (weights, flags) = TerrainBake.Bake(_overviewControl, texels, _slots);
        var images = new List<(string Name, Image Image)>
        {
            ("_overview_heights", Image.CreateFromData(texels, texels, false, Image.Format.Rf, heightBytes)),
            ("_overview_flags", flags),
            ("_overview_colors", Image.CreateFromData(texels, texels, false, Image.Format.Rgba8, _overviewColors)),
        };
        images.AddRange(weights.Select((image, i) => ($"_overview_weights_{i}", image)));

        var fresh = _overviewTextures.Count != images.Count;
        if (fresh)
            FreeOverviewTextures();
        for (var i = 0; i < images.Count; i++)
        {
            if (fresh)
            {
                var texture = ImageTexture.CreateFromImage(images[i].Image);
                _overviewTextures.Add(texture);
                SetParam(material, images[i].Name, texture.GetRid());
            }
            else
            {
                _overviewTextures[i].Update(images[i].Image);
            }
        }

        _overviewDirty = false;
    }

    /// <summary>A region edited: taken into the overview again, which is given to the GPU once edits pause a moment.</summary>
    private void OverviewEdited(TerrainRegion region)
    {
        if (!_overview)
            return;
        TakeIntoOverview(region);
        _overviewDirty = true;
        _overviewWait = OverviewDelay;
    }

    private void ProcessOverview(double delta)
    {
        if (!_overviewDirty || (_overviewWait -= delta) > 0)
            return;
        GiveOverview(Drawn.GetRid());
    }

    private void FreeOverviewTextures()
    {
        // Let go of now, not when the garbage collector gets to them (see TearDown).
        foreach (var texture in _overviewTextures)
            texture.Dispose();
        _overviewTextures.Clear();
    }

    private void TearDownOverview()
    {
        FreeOverviewTextures();
        _overview = false;
        _overviewDirty = false;
        _overviewHeights = [];
        _overviewControl = [];
        _overviewColors = [];
    }
}
