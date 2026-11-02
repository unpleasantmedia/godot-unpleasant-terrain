using Godot;

namespace UnpleasantMedia.Terrain;

/// <summary>
/// One of a terrain's ground textures, by its place in its <see cref="TerrainTextureList"/> (the id
/// the control map paints with): its albedo (with height in alpha) and normal (with roughness in
/// alpha), how it tiles, and how it is shaded.
/// </summary>
[Tool]
[GlobalClass]
public partial class TerrainTexture : Resource
{
    [Export]
    public string Name { get; set; } = "";

    /// <summary>Its colour, with its height in alpha (for blending by height).</summary>
    [Export]
    public Texture2D? Albedo { get; set; }

    /// <summary>Its normal map, with its roughness in alpha.</summary>
    [Export]
    public Texture2D? Normal { get; set; }

    /// <summary>A tint over its colour.</summary>
    [Export]
    public Color Tint { get; set; } = Colors.White;

    /// <summary>How often it repeats: per two metres, at 1.</summary>
    [Export(PropertyHint.Range, "0.001,2,0.001")]
    public float UvScale { get; set; } = 0.5f;

    [Export(PropertyHint.Range, "0,2,0.01")]
    public float NormalDepth { get; set; } = 1f;

    [Export(PropertyHint.Range, "0,2,0.01")]
    public float AoStrength { get; set; } = 0.5f;

    /// <summary>How much rougher (or, below 0, smoother) than its normal map says.</summary>
    [Export(PropertyHint.Range, "-1,1,0.01")]
    public float RoughnessMod { get; set; }
}
