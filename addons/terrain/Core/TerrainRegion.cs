using System;
using Godot;

namespace UnpleasantMedia.Terrain;

/// <summary>
/// One square region of a terrain, <see cref="TerrainData.RegionSize"/> samples across, in a file of
/// its own: its heights (one float a sample), its control (one 32-bit word a sample, packed as
/// Terrain3D packed it, which the terrain's shader reads: see <see cref="TerrainControl"/>),
/// and its colour (a tint and wetness a sample). Sampling reads copies of the images kept as
/// arrays; edits change the arrays and are written back to the images with <see cref="Commit"/>.
/// </summary>
[Tool]
[GlobalClass]
public partial class TerrainRegion : Resource
{
    /// <summary>Which region it is: its corner is this many regions from the terrain's origin.</summary>
    [Export]
    public Vector2I Location { get; set; }

    [Export]
    public Image? Heights { get; set; }

    [Export]
    public Image? Control { get; set; }

    [Export]
    public Image? Colors { get; set; }

    /// <summary>Its lowest and highest heights, for culling.</summary>
    [Export]
    public Vector2 HeightRange { get; set; }

    private float[]? _heights;
    private uint[]? _control;

    /// <summary>Whether it has edits not yet saved.</summary>
    public bool Modified { get; set; }

    /// <summary>Its heights, row by row (z then x).</summary>
    public float[] HeightValues => _heights ??= Floats(Heights);

    /// <summary>Its control words, row by row (z then x).</summary>
    public uint[] ControlValues => _control ??= Words(Control);

    /// <summary>Writes the arrays' edits back into its images, and marks it modified.</summary>
    public void Commit(bool heights, bool control)
    {
        if (heights && _heights is not null && Heights is not null)
            Heights.SetData(Heights.GetWidth(), Heights.GetHeight(), false, Image.Format.Rf, Bytes(_heights));
        if (control && _control is not null && Control is not null)
            Control.SetData(Control.GetWidth(), Control.GetHeight(), false, Image.Format.Rf, Bytes(_control));
        Modified = true;
    }

    /// <summary>Works out its lowest and highest heights again: before saving, not on every edit.</summary>
    public void UpdateHeightRange()
    {
        var low = float.MaxValue;
        var high = float.MinValue;
        foreach (var height in HeightValues)
        {
            low = Math.Min(low, height);
            high = Math.Max(high, height);
        }

        HeightRange = low <= high ? new Vector2(low, high) : Vector2.Zero;
    }

    private static float[] Floats(Image? image)
    {
        if (image is null || image.GetFormat() != Image.Format.Rf)
            return Array.Empty<float>();
        var bytes = image.GetData();
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * sizeof(float));
        return values;
    }

    private static uint[] Words(Image? image)
    {
        if (image is null || image.GetFormat() != Image.Format.Rf)
            return Array.Empty<uint>();
        var bytes = image.GetData();
        var values = new uint[bytes.Length / sizeof(uint)];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * sizeof(uint));
        return values;
    }

    private static byte[] Bytes(Array values)
    {
        var bytes = new byte[Buffer.ByteLength(values)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
