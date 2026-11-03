using System;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// A region's control words as textures a visual shader can read, as it cannot read their bits:
/// how much of each texture slot a sample has, four slots to a map; and in the flags, red for the
/// auto-shader and green for holes. Paint past the last slot shows as the first.
/// </summary>
public static class TerrainBake
{
    /// <summary>How many maps it takes for so many slots.</summary>
    public static int MapsFor(int slots) => (slots + 3) / 4;

    public static (Image[] Weights, Image Flags) Bake(uint[] control, int size, int slots)
    {
        var maps = MapsFor(slots);
        var weights = new byte[maps][];
        for (var i = 0; i < maps; i++)
            weights[i] = new byte[size * size * 4];
        var flags = new byte[size * size * 2];
        for (var s = 0; s < control.Length && s < size * size; s++)
        {
            var word = control[s];
            flags[s * 2] = (byte)(TerrainControl.AutoLevel(word) * 255 / TerrainControl.FullAuto);
            flags[s * 2 + 1] = TerrainControl.IsHole(word) ? (byte)255 : (byte)0;
            var blend = TerrainControl.Blend(word);
            Add(weights, s, Within(TerrainControl.Base(word), slots), 1f - blend);
            Add(weights, s, Within(TerrainControl.Overlay(word), slots), blend);
        }

        var images = new Image[maps];
        for (var i = 0; i < maps; i++)
            images[i] = Image.CreateFromData(size, size, false, Image.Format.Rgba8, weights[i]);
        return (images, Image.CreateFromData(size, size, false, Image.Format.Rg8, flags));
    }

    private static int Within(int slot, int slots) => slot < slots ? slot : 0;

    private static void Add(byte[][] weights, int sample, int slot, float amount)
    {
        if (amount <= 0f)
            return;
        var at = sample * 4 + slot % 4;
        var map = weights[slot / 4];
        map[at] = (byte)Math.Min(map[at] + (int)(amount * 255f + 0.5f), 255);
    }
}
