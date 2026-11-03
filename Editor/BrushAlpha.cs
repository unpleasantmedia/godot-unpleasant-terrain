#if TOOLS
using System;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain.Editor;

/// <summary>
/// A brush's shape: how much it works across a square laid over its circle, from an image or from
/// noise. A plain class, as the editor would carry an array in the plugin itself through a reload of
/// the scripts and damage it.
/// </summary>
internal sealed class BrushAlpha
{
    /// <summary>The noises to pick from, as the dock lists them.</summary>
    public static readonly (string Name, FastNoiseLite.NoiseTypeEnum Type)[] Noises =
    [
        ("Perlin", FastNoiseLite.NoiseTypeEnum.Perlin),
        ("Simplex", FastNoiseLite.NoiseTypeEnum.SimplexSmooth),
        ("Cellular", FastNoiseLite.NoiseTypeEnum.Cellular),
        ("Value", FastNoiseLite.NoiseTypeEnum.Value),
    ];

    private const int NoiseSize = 256;
    private const int LargestImage = 1024;

    private readonly int _width;
    private readonly int _height;
    private readonly float[] _values;

    private BrushAlpha(int width, int height, float[] values) => (_width, _height, _values) = (width, height, values);

    /// <summary>The shape of an image: its alpha if it has any, otherwise its brightness.</summary>
    public static BrushAlpha FromImage(Image source)
    {
        var image = (Image)source.Duplicate();
        if (image.IsCompressed())
            image.Decompress();
        var channel = image.DetectAlpha() == Image.AlphaMode.None ? 0 : 3;
        image.ClearMipmaps();
        image.Convert(Image.Format.Rgba8);
        if (Mathf.Max(image.GetWidth(), image.GetHeight()) > LargestImage)
            image.Resize(Mathf.Min(image.GetWidth(), LargestImage), Mathf.Min(image.GetHeight(), LargestImage), Image.Interpolation.Bilinear);
        var data = image.GetData();
        var values = new float[image.GetWidth() * image.GetHeight()];
        for (var i = 0; i < values.Length; i++)
            values[i] = data[i * 4 + channel] / 255f;
        return new BrushAlpha(image.GetWidth(), image.GetHeight(), values);
    }

    /// <summary>The shape of a noise, with about this many blobs across it.</summary>
    public static BrushAlpha FromNoise(FastNoiseLite.NoiseTypeEnum type, float features, int octaves, int seed)
    {
        var noise = new FastNoiseLite
        {
            NoiseType = type,
            Frequency = features / NoiseSize,
            Seed = seed,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = octaves,
        };
        return FromImage(noise.GetImage(NoiseSize, NoiseSize));
    }

    /// <summary>How much the shape works at a point over its square (0 to 1 each way), between its pixels; nothing off the square.</summary>
    public float Sample(float u, float v)
    {
        if (u < 0f || u > 1f || v < 0f || v > 1f)
            return 0f;
        var x = u * (_width - 1);
        var y = v * (_height - 1);
        var (left, top) = ((int)x, (int)y);
        var (right, bottom) = (Math.Min(left + 1, _width - 1), Math.Min(top + 1, _height - 1));
        var above = Mathf.Lerp(_values[top * _width + left], _values[top * _width + right], x - left);
        var below = Mathf.Lerp(_values[bottom * _width + left], _values[bottom * _width + right], x - left);
        return Mathf.Lerp(above, below, y - top);
    }

    /// <summary>The shape as a small grey picture for the dock.</summary>
    public ImageTexture Preview(int size = 64)
    {
        var bytes = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
                bytes[y * size + x] = (byte)(Sample((float)x / (size - 1), (float)y / (size - 1)) * 255f);
        }

        return ImageTexture.CreateFromImage(Image.CreateFromData(size, size, false, Image.Format.L8, bytes));
    }
}
#endif
