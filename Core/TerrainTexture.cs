using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// One of a terrain's ground textures, by its place in its <see cref="TerrainTextureList"/> (the id
/// the control map paints with): its albedo (with height in alpha) and normal (with roughness in
/// alpha), how it tiles, and how it is shaded.
/// </summary>
[Tool]
[GlobalClass]
public partial class TerrainTexture : Resource
{
    /// <summary>Its name, which is also the resource's, so the texture list shows it.</summary>
    [Export]
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            ResourceName = value;
            EmitChanged();
        }
    }

    private string _name = "";

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

    /// <summary>Its colour when the terrain draws flat colours.</summary>
    [Export]
    public Color FlatColor { get; set; } = new(0.5f, 0.5f, 0.5f);

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float FlatRoughness { get; set; } = 0.9f;

    /// <summary>Whether it is paving, blended apart from the natural ground: see the terrain's Paving Edge Hardness.</summary>
    [Export]
    public bool IsPaving { get; set; }

    /// <summary>Whether the auto-shader puts it on the ground, by the rules below.</summary>
    [ExportGroup("Auto Shader")]
    [Export]
    public bool AutoShader { get; set; }

    /// <summary>Higher layers cover lower ones; textures on the same layer share the ground they both claim.</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int AutoLayer { get; set; }

    [Export(PropertyHint.Range, "-10000,10000,0.1,or_greater,or_less")]
    public float AutoMinHeight { get; set; } = -10000f;

    [Export(PropertyHint.Range, "-10000,10000,0.1,or_greater,or_less")]
    public float AutoMaxHeight { get; set; } = 10000f;

    /// <summary>How far either side of its height limits it fades.</summary>
    [Export(PropertyHint.Range, "0,200,0.1")]
    public float AutoHeightBlend { get; set; } = 1f;

    /// <summary>In degrees. Below 0 (and above 90 for the most) means no limit, as it fades either side of a limit.</summary>
    [Export(PropertyHint.Range, "-90,180,0.1")]
    public float AutoMinSlope { get; set; } = -90f;

    [Export(PropertyHint.Range, "-90,180,0.1")]
    public float AutoMaxSlope { get; set; } = 180f;

    /// <summary>How far either side of its slope limits it fades.</summary>
    [Export(PropertyHint.Range, "0,30,0.1")]
    public float AutoSlopeBlend { get; set; } = 4f;
}
