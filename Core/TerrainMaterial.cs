using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// One of a terrain's ground materials, by its place in its <see cref="TerrainMaterialList"/> (the id
/// the control map paints with): its look, from a <see cref="Material"/> (or, with none, from its
/// own fields below it), and what is the terrain's own: whether it is paving, and where the
/// auto-shader puts it. A material with no albedo texture draws as a flat colour (its albedo colour,
/// and its roughness).
/// </summary>
[Tool]
[GlobalClass]
public partial class TerrainMaterial : Resource
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

    private StandardMaterial3D? _material;

    /// <summary>
    /// Its look, as a material: its albedo texture (height in alpha, for blending by height) and
    /// colour (a tint over it), its normal map (roughness in alpha) and how deep, its roughness (1:
    /// as the normal map's alpha says; less is smoother), its UV scale (how often it repeats: per two
    /// metres, at 1), and its ambient occlusion (its light affect, when on). Without an albedo
    /// texture, it is a flat colour. With one, the fields below it are not used (and not shown).
    /// </summary>
    [Export]
    public StandardMaterial3D? Material
    {
        get => _material;
        set
        {
            var changed = new Callable(this, Resource.MethodName.EmitChanged);
            if (_material is not null && _material.IsConnected(Resource.SignalName.Changed, changed))
                _material.Disconnect(Resource.SignalName.Changed, changed);
            _material = value;
            if (_material is not null && !_material.IsConnected(Resource.SignalName.Changed, changed))
                _material.Connect(Resource.SignalName.Changed, changed);
            NotifyPropertyListChanged();
            EmitChanged();
        }
    }

    // Its look as the terrain uses it: from its material if it has one, or else its own fields.
    public Texture2D? UsedAlbedo => _material is { } m ? m.AlbedoTexture : Albedo;
    public Texture2D? UsedNormal => _material is { } m ? m.NormalEnabled ? m.NormalTexture : null : Normal;
    public Color UsedTint => _material?.AlbedoColor ?? Tint;
    public float UsedUvScale => _material?.Uv1Scale.X ?? UvScale;
    public float UsedNormalDepth => _material is { } m ? m.NormalEnabled ? m.NormalScale : 0f : NormalDepth;
    public float UsedRoughnessMod => _material is { } m ? m.Roughness - 1f : RoughnessMod;
    public float UsedAoStrength => _material is { } m ? m.AOEnabled ? m.AOLightAffect : 0f : 0.5f;

    /// <summary>Its own look fields hidden while a material gives its look, so each value is set in one place.</summary>
    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        if (_material is not null && property["name"].AsStringName() is var name
            && (name == PropertyName.Albedo || name == PropertyName.Normal || name == PropertyName.Tint || name == PropertyName.UvScale
                || name == PropertyName.NormalDepth || name == PropertyName.RoughnessMod))
            property["usage"] = (int)(property["usage"].As<PropertyUsageFlags>() & ~PropertyUsageFlags.Editor);
    }

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

    /// <summary>How much rougher (or, below 0, smoother) than its normal map says.</summary>
    [Export(PropertyHint.Range, "-1,1,0.01")]
    public float RoughnessMod { get; set; }

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
