using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// Draws a terrain's ground (<see cref="Data"/>) with its <see cref="AutoMaterial"/>, as a clipmap: a
/// square of <see cref="RingCells"/> cells each way round the camera, then <see cref="Levels"/> - 1
/// square rings round it, each with cells twice the size of the one inside it. The rings are flat
/// grids drawn straight through the RenderingServer (no nodes); the material's shader lifts them to
/// the heights it reads from the regions' maps, which this puts on the GPU as texture arrays, along
/// with the ground's materials (<see cref="Materials"/>). The rings follow the camera in steps of their
/// cells' size, so moving round redraws nothing.
///
/// It is also the ground to collide with (a heightmap shape per region, in one static body, in the
/// game and, with <see cref="EditorCollision"/>, in the editor), and the way to read and change the
/// ground: <see cref="HeightAt"/>, <see cref="NormalAt"/> and <see cref="ControlAt"/> read it, and
/// <see cref="EditHeights"/> and <see cref="EditControl"/> change it, redrawing and re-colliding
/// only the regions changed, to be saved with <see cref="Save"/>.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_terrain/Icons/Terrain.svg")]
public partial class TerrainNode : Node3D, ITerrainGround
{
    /// <summary>
    /// Whether its saved settings have all been put back since it was made: false from when the
    /// scripts are reloaded (it is made anew, already in the tree and built) to the end of that
    /// frame. Settings put back then are not changes, and rebuild nothing: rebuilding everything
    /// after each reload froze the editor for a minute.
    /// </summary>
    private bool _reloadSettled;

    public TerrainNode() => new Callable(this, MethodName.ReloadSettle).CallDeferred();

    private void ReloadSettle() => _reloadSettled = true;

    /// <summary>Whether a setting is being put back as the scripts are reloaded, rather than changed.</summary>
    private bool Reloading => IsInsideTree() && !_reloadSettled;

    /// <summary>How many of its own cells each ring reaches out from its middle; the shader's morphing is set to it.</summary>
    public const int RingCells = 80;

    /// <summary>
    /// The visual layer it is drawn on (3) and nothing else is: lamps leave it out of their shadows
    /// (their shadow_caster_mask), which would otherwise draw every ring for every lamp; the sun does not.
    /// </summary>
    public const uint VisualLayer = 4;

    /// <summary>How many samples across each collision shape is, so an edit re-collides only the tiles it touched.</summary>
    public const int CollisionTile = 128;

    /// <summary>How many regions across the shader's region map is (it finds a region's layer by it).</summary>
    private const int RegionMapSize = 32;

    private TerrainData? _data;
    private ShaderMaterial? _material;

    /// <summary>The material a terrain starts with, of the visual shader: colours by height and slope, its nodes editable.</summary>
    public const string DefaultShader = "res://addons/unpleasant_terrain/Shaders/TerrainGroundVisual.tres";

    public const string DefaultMaterial = "res://addons/unpleasant_terrain/Shaders/TerrainGroundDefault.tres";

    /// <summary>What it is drawn with: its own material, or else its own copy of the default one (a material holds its terrain's maps, so terrains can't share one).</summary>
    private ShaderMaterial Drawn => _material ??= (ShaderMaterial)GD.Load<ShaderMaterial>(DefaultMaterial).Duplicate();
    private TerrainMaterialList? _textures;
    private float _edgeHeightNoise = 1.5f;
    private float _edgeSlopeNoise = 4f;
    private float _edgeNoiseSize = 25f;
    private float _blendSharpness = 0.5f;
    private float _pavingEdgeHardness = 1f;
    private float _farUvScale = 0.2f;
    private float _farBlendStart = 15f;
    private float _farBlendEnd = 90f;
    private float _farBlendAmount = 1f;
    private float _farRotation = 0.7f;
    private float _farthestUvScale = 0.04f;
    private float _farthestBlendStart = 150f;
    private float _farthestBlendEnd = 600f;
    private float _farthestBlendAmount = 1f;
    private float _farthestRotation = 2.1f;

    /// <summary>Whether it is drawn with a generated shader, rather than one of your own.</summary>
    private bool Generated => Drawn.Shader is { } shader && (shader.HasMeta(TerrainGroundBuilder.SignatureMeta) || shader.ResourcePath == DefaultShader);

    /// <summary>What its generated shader was built for. Empty if that isn't known (the default shader, saved over by an editor holding an older copy), so it is built again.</summary>
    private string BuiltSignature => Drawn.Shader.GetMeta(TerrainGroundBuilder.SignatureMeta, "").AsString();

    /// <summary>How many texture slots its shader has.</summary>
    private int _slots;
    private string _slotValues = "";
    private int _slotCheck;

    private readonly record struct AutoRule(int Layer, float MinHeight, float MaxHeight, float HeightBlend, float MinSlope, float MaxSlope, float SlopeBlend);

    // With no textures, the rules for those plain colours: Dry Grass under everything, then Grass, then Sand, with Rock on top.
    private static readonly Dictionary<int, AutoRule> PlainRules = new()
    {
        [0] = new(1, -10000f, 200f, 30f, -90f, 26f, 4f),
        [1] = new(3, -10000f, 10000f, 1f, 38f, 180f, 4f),
        [2] = new(2, -10000f, 3.5f, 1.5f, -90f, 26f, 4f),
        [3] = new(0, -10000f, 10000f, 1f, -90f, 180f, 4f),
    };

    // With no textures, the slots a terrain usually has, in plain colours: Grass, Rock, Sand, Dry Grass, Asphalt, Sidewalk.
    private static readonly Color[] PlainColors = [new(0.29f, 0.37f, 0.18f), new(0.37f, 0.36f, 0.34f), new(0.70f, 0.62f, 0.47f), new(0.52f, 0.47f, 0.30f), new(0.20f, 0.19f, 0.18f), new(0.62f, 0.61f, 0.58f)];
    private int _levels = 8;

    // Godot's own collections, and not readonly: the editor keeps these when it reloads the
    // scripts, so the rings, maps and shapes made before are still known (and let go of) after.
    private Godot.Collections.Array<Rid> _instances = new();
    private Godot.Collections.Array<ArrayMesh> _meshes = new();
    private Godot.Collections.Array<Texture2DArray> _arrays = new();
    private Godot.Collections.Dictionary<Vector2I, int> _layers = new();
    private Godot.Collections.Dictionary<Vector2I, Rid> _shapes = new();
    private Texture2DArray? _heightMaps;
    private Texture2DArray? _controlMaps;
    /// <summary>Its collision: a static body for each region, holding that region's tiles, so adding or changing a tile rebuilds only its region's body, not one body of thousands of shapes.</summary>
    private readonly Dictionary<Vector2I, Rid> _bodies = new();
    private bool _collides;
    private bool _setUp;
    private bool _editorCollision;
    private uint _collisionLayer = 1;
    private uint _collisionMask = 1;

    [Export]
    public TerrainData? Data
    {
        get => _data;
        set
        {
            _data = value;
            Redo();
        }
    }

    /// <summary>
    /// The ground's shader material, filled in by the terrain: its copy of the default one
    /// (Shaders/TerrainGroundDefault.tres, which clearing this goes back to) has a graph built for its
    /// <see cref="Materials"/>, set up by the settings below; or assign a ShaderMaterial with a shader
    /// written for this terrain.
    /// </summary>
    [Export]
    public ShaderMaterial? AutoMaterial
    {
        get => Drawn;
        set
        {
            _material = value;
            Redo();
        }
    }

    /// <summary>The ground's materials, by slot: the order the Terrain dock lists them in, and the ids painted with.</summary>
    [Export]
    public TerrainMaterialList? Materials
    {
        get => _textures;
        set
        {
            _textures = value;
            Redo();
        }
    }

    /// <summary>How many rings it is drawn with: each doubles how far it reaches.</summary>
    [Export(PropertyHint.Range, "1,12,1")]
    public int Levels
    {
        get => _levels;
        set
        {
            _levels = Mathf.Clamp(value, 1, 12);
            Redo();
        }
    }

    /// <summary>A setting of the generated ground shader, passed on to it now. A material with a shader of your own keeps its own.</summary>
    private void Setting(ref float field, float value, string parameter)
    {
        field = value;
        if (Generated)
            RenderingServer.MaterialSetParam(Drawn.GetRid(), parameter, value);
    }

    [ExportGroup("Auto Shader")]
    [Export(PropertyHint.Range, "0,20,0.1")]
    public float EdgeHeightNoise
    {
        get => _edgeHeightNoise;
        set => Setting(ref _edgeHeightNoise, value, "edge_height_noise");
    }

    [Export(PropertyHint.Range, "0,20,0.1")]
    public float EdgeSlopeNoise
    {
        get => _edgeSlopeNoise;
        set => Setting(ref _edgeSlopeNoise, value, "edge_slope_noise");
    }

    [Export(PropertyHint.Range, "1,200,1")]
    public float EdgeNoiseSize
    {
        get => _edgeNoiseSize;
        set => Setting(ref _edgeNoiseSize, value, "edge_noise_size");
    }

    [ExportGroup("Blending")]
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float BlendSharpness
    {
        get => _blendSharpness;
        set => Setting(ref _blendSharpness, value, "blend_sharpness");
    }

    [ExportGroup("Paving")]
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float PavingEdgeHardness
    {
        get => _pavingEdgeHardness;
        set => Setting(ref _pavingEdgeHardness, value, "paving_edge_hardness");
    }

    [ExportGroup("Distance Tiling")]
    [Export(PropertyHint.Range, "0.02,1,0.01")]
    public float FarUvScale
    {
        get => _farUvScale;
        set => Setting(ref _farUvScale, value, "far_uv_scale");
    }

    [Export(PropertyHint.Range, "0,500,1")]
    public float FarBlendStart
    {
        get => _farBlendStart;
        set => Setting(ref _farBlendStart, value, "far_blend_start");
    }

    [Export(PropertyHint.Range, "0,2000,1")]
    public float FarBlendEnd
    {
        get => _farBlendEnd;
        set => Setting(ref _farBlendEnd, value, "far_blend_end");
    }

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float FarBlendAmount
    {
        get => _farBlendAmount;
        set => Setting(ref _farBlendAmount, value, "far_blend_amount");
    }

    [Export(PropertyHint.Range, "0,6.283,0.01")]
    public float FarRotation
    {
        get => _farRotation;
        set => Setting(ref _farRotation, value, "far_rotation");
    }

    [Export(PropertyHint.Range, "0.005,1,0.005")]
    public float FarthestUvScale
    {
        get => _farthestUvScale;
        set => Setting(ref _farthestUvScale, value, "farthest_uv_scale");
    }

    [Export(PropertyHint.Range, "0,2000,1")]
    public float FarthestBlendStart
    {
        get => _farthestBlendStart;
        set => Setting(ref _farthestBlendStart, value, "farthest_blend_start");
    }

    [Export(PropertyHint.Range, "0,4000,1")]
    public float FarthestBlendEnd
    {
        get => _farthestBlendEnd;
        set => Setting(ref _farthestBlendEnd, value, "farthest_blend_end");
    }

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float FarthestBlendAmount
    {
        get => _farthestBlendAmount;
        set => Setting(ref _farthestBlendAmount, value, "farthest_blend_amount");
    }

    [Export(PropertyHint.Range, "0,6.283,0.01")]
    public float FarthestRotation
    {
        get => _farthestRotation;
        set => Setting(ref _farthestRotation, value, "farthest_rotation");
    }

    [ExportGroup("")]
    /// <summary>Whether it collides in the editor too (in the game it always does).</summary>
    [Export]
    public bool EditorCollision
    {
        get => _editorCollision;
        set
        {
            _editorCollision = value;
            Redo();
        }
    }

    [Export(PropertyHint.Layers3DPhysics)]
    public uint CollisionLayer
    {
        get => _collisionLayer;
        set
        {
            _collisionLayer = value;
            foreach (var body in _bodies.Values)
                PhysicsServer3D.BodySetCollisionLayer(body, value);
        }
    }

    [Export(PropertyHint.Layers3DPhysics)]
    public uint CollisionMask
    {
        get => _collisionMask;
        set
        {
            _collisionMask = value;
            foreach (var body in _bodies.Values)
                PhysicsServer3D.BodySetCollisionMask(body, value);
        }
    }

    /// <summary>How high the ground is at a place, or NaN where there is none.</summary>
    public float HeightAt(Vector3 at) => _data?.HeightAt(at) ?? float.NaN;

    /// <summary>Which way is square to the ground at a place, or NaN where there is none.</summary>
    public Vector3 NormalAt(Vector3 at) => _data?.NormalAt(at) ?? new Vector3(float.NaN, float.NaN, float.NaN);

    /// <summary>The ground's control word at a place (see <see cref="TerrainControl"/>): its textures, and whether it is a hole or the auto-shader's.</summary>
    public uint ControlAt(Vector3 at) => _data?.ControlAt(at) ?? 0u;

    /// <summary>
    /// Changes the heights of a block of samples (in sample coordinates: metres over
    /// <see cref="TerrainData.VertexSpacing"/>): each one's new height, from its sample coordinates
    /// and its height. Samples outside the regions are left out. Redraws and re-collides the regions changed.
    /// </summary>
    public void EditHeights(Rect2I samples, System.Func<int, int, float, float> change) =>
        Edit(Block(samples), true, (x, z, region, index) => Change(region.HeightValues, index, change(x, z, region.HeightValues[index])));

    /// <summary>
    /// The heights of a straight slope across a circle of samples (in sample coordinates), from its
    /// lowest sample's height to its highest's, edge to edge in the direction from the one to the
    /// other. Heights are read through heightOf where given, so a stroke can go by what the ground was
    /// before it, and not wear its own slope down. Null if it has no slope to follow: level, or no ground there.
    /// </summary>
    public System.Func<int, int, float>? SlopeAcross(Vector2 middle, float reach, System.Func<int, int, float, float>? heightOf = null)
    {
        if (_data is null || reach <= 0f)
            return null;
        var (low, high) = (float.PositiveInfinity, float.NegativeInfinity);
        Vector2 lowAt = default, highAt = default;
        var from = Mathf.FloorToInt(-reach) - 1;
        var to = Mathf.CeilToInt(reach) + 1;
        for (var dz = from; dz <= to; dz++)
        {
            for (var dx = from; dx <= to; dx++)
            {
                var at = new Vector2(Mathf.FloorToInt(middle.X) + dx, Mathf.FloorToInt(middle.Y) + dz);
                if (at.DistanceTo(middle) > reach)
                    continue;
                var height = _data.HeightOf((int)at.X, (int)at.Y);
                if (float.IsNaN(height))
                    continue;
                if (heightOf is not null)
                    height = heightOf((int)at.X, (int)at.Y, height);
                if (height < low)
                    (low, lowAt) = (height, at);
                if (height > high)
                    (high, highAt) = (height, at);
            }
        }

        if (high <= low || lowAt == highAt)
            return null;
        var along = (highAt - lowAt).Normalized();
        return (x, z) => Mathf.Lerp(low, high, 0.5f + 0.5f * (new Vector2(x, z) - middle).Dot(along) / reach);
    }

    /// <summary>Changes the control words of a block of samples, as <see cref="EditHeights"/> does heights.</summary>
    public void EditControl(Rect2I samples, System.Func<int, int, uint, uint> change) =>
        Edit(Block(samples), false, (x, z, region, index) => Change(region.ControlValues, index, change(x, z, region.ControlValues[index])));

    public void SetHeights(IEnumerable<(int X, int Z, float Height)> heights)
    {
        var wanted = heights.ToDictionary(h => (h.X, h.Z), h => h.Height);
        Edit(wanted.Keys, true, (x, z, region, index) => Change(region.HeightValues, index, wanted[(x, z)]));
    }

    public void SetHoles(IEnumerable<(int X, int Z, bool Hole)> holes)
    {
        var wanted = holes.ToDictionary(h => (h.X, h.Z), h => h.Hole);
        Edit(wanted.Keys, false, (x, z, region, index) =>
            Change(region.ControlValues, index, TerrainControl.With(region.ControlValues[index], TerrainControl.HoleBit, wanted[(x, z)])));
    }

    /// <summary>Sets samples' heights, each given as (x index, height, z index): for undo and redo.</summary>
    public void SetSampleHeights(Vector3[] points) => SetHeights(points.Select(p => ((int)p.X, (int)p.Z, p.Y)));

    /// <summary>Sets samples' control words, given in threes (x index, z index, word): for undo and redo.</summary>
    public void SetSampleControl(int[] triples)
    {
        var wanted = new Dictionary<(int X, int Z), uint>();
        for (var i = 0; i + 2 < triples.Length; i += 3)
            wanted[(triples[i], triples[i + 1])] = unchecked((uint)triples[i + 2]);
        Edit(wanted.Keys, false, (x, z, region, index) => Change(region.ControlValues, index, wanted[(x, z)]));
    }

    public float VertexSpacing => _data?.VertexSpacing ?? 1f;

    public float? SampleHeight(int x, int z) => _data?.HeightOf(x, z) is { } height && !float.IsNaN(height) ? height : null;

    /// <summary>
    /// Where a ray first meets the ground: stepped along, by half its height over the ground (but a
    /// sample's spacing at least, so a steep slope is not stepped through far), until under it, then
    /// narrowed down to a centimetre.
    /// </summary>
    public Vector3? Intersect(Vector3 from, Vector3 direction)
    {
        if (_data is null || direction.LengthSquared() < 1e-12f)
            return null;
        direction = direction.Normalized();
        var reach = _data.RegionWidth * RegionMapSize;
        var travelled = 0f;
        var before = 0f;
        while (travelled < reach)
        {
            var at = from + direction * travelled;
            var height = _data.HeightAt(at);
            var above = float.IsNaN(height) ? float.NaN : at.Y - height;
            if (above <= 0f)
            {
                // Between the last step above it and this one.
                var (low, high) = (before, travelled);
                while (high - low > 0.01f)
                {
                    var middle = (low + high) / 2f;
                    var point = from + direction * middle;
                    var ground = _data.HeightAt(point);
                    if (!float.IsNaN(ground) && point.Y <= ground)
                        high = middle;
                    else
                        low = middle;
                }

                return from + direction * high;
            }

            before = travelled;
            travelled += float.IsNaN(above) ? _data.VertexSpacing * 4f : Mathf.Max(above * 0.5f, _data.VertexSpacing * 0.5f);
        }

        return null;
    }

    public Variant ShaderParameter(StringName name) => Drawn.GetShaderParameter(name);

    /// <summary>Saves the regions changed since they were loaded or last saved.</summary>
    public void Save() => _data?.SaveModified();

    private static IEnumerable<(int X, int Z)> Block(Rect2I samples)
    {
        for (var z = samples.Position.Y; z < samples.End.Y; z++)
        {
            for (var x = samples.Position.X; x < samples.End.X; x++)
                yield return (x, z);
        }
    }

    private static bool Change<T>(T[] values, int index, T value)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(values[index], value))
            return false;
        values[index] = value;
        return true;
    }

    private void Edit(IEnumerable<(int X, int Z)> samples, bool heights, System.Func<int, int, TerrainRegion, int, bool> change)
    {
        if (_data is null)
            return;
        var changed = new HashSet<TerrainRegion>();
        var tiles = new HashSet<Vector2I>();
        var (lowest, highest) = (new Vector2I(int.MaxValue, int.MaxValue), new Vector2I(int.MinValue, int.MinValue));
        foreach (var (x, z) in samples)
        {
            var (region, index) = _data.Sample(x, z);
            if (region is null || !change(x, z, region, index))
                continue;
            changed.Add(region);
            lowest = new Vector2I(Mathf.Min(lowest.X, x), Mathf.Min(lowest.Y, z));
            highest = new Vector2I(Mathf.Max(highest.X, x), Mathf.Max(highest.Y, z));
            // Its tile, and those before it whose edges take its tile's first row and column.
            var tile = new Vector2I(FloorDiv(x, CollisionTile), FloorDiv(z, CollisionTile));
            var firstX = x - tile.X * CollisionTile == 0;
            var firstZ = z - tile.Y * CollisionTile == 0;
            tiles.Add(tile);
            if (firstX)
                tiles.Add(tile + new Vector2I(-1, 0));
            if (firstZ)
                tiles.Add(tile + new Vector2I(0, -1));
            if (firstX && firstZ)
                tiles.Add(tile + new Vector2I(-1, -1));
        }

        foreach (var region in changed)
        {
            region.Commit(heights, !heights);
            OverviewEdited(region);
            if (_layers.TryGetValue(region.Location, out var layer))
            {
                if (heights)
                    _heightMaps?.UpdateLayer(region.Heights, layer);
                else
                {
                    _controlMaps?.UpdateLayer(region.Control, layer);
                    if (_baked)
                        UpdateBakedMaps(region, layer);
                }
            }
        }

        foreach (var tile in tiles)
            Collide(tile);

        // What stands on the ground there (foliage) follows it: the samples changed, and the cells round them.
        if (changed.Count > 0)
        {
            var spacing = _data.VertexSpacing;
            var from = new Vector2(lowest.X - 1, lowest.Y - 1) * spacing;
            EmitSignal(SignalName.GroundChanged, new Rect2(from, new Vector2(highest.X + 1, highest.Y + 1) * spacing - from));
        }
    }

    private static int FloorDiv(int value, int by) => Mathf.FloorToInt(value / (float)by);

    /// <summary>Its ground changed (heights or holes) in this area: x and z in metres, in the world.</summary>
    [Signal]
    public delegate void GroundChangedEventHandler(Rect2 area);

    /// <summary>Where Add Region puts a new region, in regions from the terrain's origin: -16 to 15 each way.</summary>
    [ExportGroup("Regions")]
    [Export]
    public Vector2I NewRegion { get; set; }

    [ExportToolButton("Add Region", Icon = "Add")]
    public Callable AddRegionButton => Callable.From(AddNewRegion);

    // Data and a folder for its regions, if it has none.
    private void EnsureData()
    {
        _data ??= new TerrainData();
        if (string.IsNullOrEmpty(_data.Folder))
            _data.Folder = $"res://Terrain/{Name}-Regions";
    }

    // Add Region pressed: a flat region at New Region, one step to undo; data and a folder for it first, if it has none.
    /// <summary>
    /// Whether some job (of any plugin) is reading a terrain now, on a thread of its own: while it is,
    /// none is to be edited. Kept on the main loop by such jobs, as a count, so no plugin need know another.
    /// </summary>
    public static bool ReadByJobs => Engine.GetMainLoop().GetMeta("unpleasant_terrain_readers", 0).AsInt32() > 0;

    /// <summary>Whether it may be edited now; if not, says why in the Output.</summary>
    public static bool MayEdit(string what)
    {
        if (!ReadByJobs)
            return true;
        GD.PushWarning($"Terrain: {what} has to wait; a job is reading the terrain (see the Output for its progress).");
        return false;
    }

    private void AddNewRegion()
    {
        if (!MayEdit("Add Region"))
            return;
        var at = NewRegion;
        var half = RegionMapSize / 2;
        if (at.X < -half || at.Y < -half || at.X >= half || at.Y >= half)
        {
            GD.PushWarning($"{Name}: New Region must be from {-half} to {half - 1} each way.");
            return;
        }

        EnsureData();
        if (_data!.Locations.Contains(at))
        {
            GD.PushWarning($"{Name}: it already has a region at {at}.");
            return;
        }

#if TOOLS
        if (Engine.IsEditorHint())
        {
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction($"Add terrain region {at}");
            undo.AddDoMethod(this, MethodName.AddRegion, at);
            undo.AddUndoMethod(this, MethodName.RemoveRegion, at);
            undo.CommitAction();
            return;
        }
#endif
        AddRegion(at);
    }

    /// <summary>What is wrong with importing a heightmap with these options, or nothing.</summary>
    public string CheckHeightmap(string path, float width, float low, float high, bool replace, int rawWidth, int rawHeight, bool rawBigEndian)
    {
        if (high <= low)
            return "Max Height must be above Min Height.";
        try
        {
            var map = HeightmapFile.Read(path, rawWidth, rawHeight, rawBigEndian);
            var data = _data ?? new TerrainData();
            var limit = RegionMapSize * data.RegionSize * data.VertexSpacing;
            return System.Math.Max(width, width * map.Height / map.Width) > limit ? $"Width is limited to {limit} m with these region settings." : "";
        }
        catch (System.IO.InvalidDataException problem)
        {
            return problem.Message;
        }
    }

    /// <summary>
    /// Makes its ground from a heightmap, centred on its origin, width metres across, black at low and white at high:
    /// the regions it needs added, the rest removed if replacing. Heights only: existing regions keep their painting.
    /// </summary>
    public void ImportHeightmap(string path, float width, float low, float high, bool replace, int rawWidth, int rawHeight, bool rawBigEndian)
    {
        var map = HeightmapFile.Read(path, rawWidth, rawHeight, rawBigEndian);
        EnsureData();
        var data = _data!;
        var across = Mathf.Max(Mathf.RoundToInt(width / data.VertexSpacing), 2);
        var down = Mathf.Max(Mathf.RoundToInt(width * map.Height / map.Width / data.VertexSpacing), 2);
        var (startX, startZ) = (-across / 2, -down / 2);
        if (replace)
        {
            foreach (var old in data.Locations.ToList())
                data.RemoveRegion(old);
        }

        var size = data.RegionSize;
        for (var rz = FloorDiv(startZ, size); rz <= FloorDiv(startZ + down - 1, size); rz++)
        {
            for (var rx = FloorDiv(startX, size); rx <= FloorDiv(startX + across - 1, size); rx++)
            {
                var location = new Vector2I(rx, rz);
                // A new region's samples past the map's edge take the edge's height, so it has no cliff there.
                var added = data.AddRegion(location);
                var region = data.Region(location)!;
                var heights = region.HeightValues;
                System.Threading.Tasks.Parallel.For(0, size, oz =>
                {
                    var sz = rz * size + oz - startZ;
                    for (var ox = 0; ox < size; ox++)
                    {
                        var sx = rx * size + ox - startX;
                        if (!added && (sx < 0 || sz < 0 || sx >= across || sz >= down))
                            continue;
                        heights[oz * size + ox] = Mathf.Lerp(low, high, map.At(sx / (float)(across - 1), sz / (float)(down - 1)));
                    }
                });
                region.Commit(true, false);
            }
        }

        Redo();
        var spacing = data.VertexSpacing;
        EmitSignal(SignalName.GroundChanged, new Rect2(startX * spacing, startZ * spacing, across * spacing, down * spacing));
    }

    /// <summary>Copies of all its regions, for undoing.</summary>
    public Godot.Collections.Array<TerrainRegion> CopyRegions() =>
        _data is null ? [] : [.. _data.Locations.Select(_data.Region).OfType<TerrainRegion>().Select(r => (TerrainRegion)r.Duplicate(true))];

    /// <summary>Its regions put back to copies made by <see cref="CopyRegions"/>.</summary>
    public void PutRegions(Godot.Collections.Array<TerrainRegion> regions)
    {
        if (_data is null)
            return;
        _data.PutRegions(regions);
        Redo();
    }

    /// <summary>Adds a flat region (see <see cref="TerrainData.AddRegion"/>), and draws and collides it.</summary>
    public void AddRegion(Vector2I location)
    {
        _data ??= new TerrainData();
        if (_data.AddRegion(location))
            Redo();
    }

    /// <summary>Takes a region away, and draws and collides what is left.</summary>
    public void RemoveRegion(Vector2I location)
    {
        _data?.RemoveRegion(location);
        Redo();
    }

    /// <summary>Every terrain in the tree: for finding a level's ground without going through all the level (see TerrainGround.FindIn).</summary>
    public static readonly HashSet<TerrainNode> InTree = [];

    public override void _EnterTree()
    {
        InTree.Add(this);
        SetUp();
    }

    public override void _Notification(int what)
    {
        // Its regions are saved with the scene.
        if (what == NotificationEditorPreSave)
            Save();
        else if (what == NotificationVisibilityChanged)
        {
            foreach (var instance in _instances)
                RenderingServer.InstanceSetVisible(instance, IsVisibleInTree());
        }
    }

    public override void _ExitTree()
    {
        InTree.Remove(this);
        TearDown();
    }


    /// <summary>Where the camera was when the rings were last moved round it.</summary>
    private Vector3? _followed;

    public override void _Process(double delta)
    {
        // Rebuilding the scripts empties the list, and it does not enter the tree again.
        InTree.Add(this);
        if (!_setUp)
            return;
        ProcessOverview(delta);
        if (Engine.IsEditorHint() && ++_slotCheck >= 30)
        {
            _slotCheck = 0;
            CheckSlots();
        }

        if (Camera() is not { } camera)
            return;
        if (_streaming)
            StreamIfMoved(camera.GlobalPosition);
        // Only as the camera moves: standing still, nothing to move.
        var at = camera.GlobalPosition;
        if (_followed is { } was && was.DistanceSquaredTo(at) < 0.0001f)
            return;
        _followed = at;
        Follow(at);
    }

    private void Redo()
    {
        // Its settings put back as the scripts are reloaded: it is set up with them already.
        if (Reloading)
            return;
        if (!IsInsideTree())
            return;
        TearDown();
        SetUp();
        // Its rings made anew: moved round the camera again, wherever it is.
        _followed = null;
    }

    private Camera3D? Camera()
    {
        if (Engine.IsEditorHint())
            return EditorInterface.Singleton.GetEditorViewport3D(0)?.GetCamera3D();
        return GetViewport()?.GetCamera3D();
    }

    private void SetUp()
    {
        if (_data is null || GetWorld3D() is not { } world)
            return;
        EnsureShader();
        var material = Drawn.GetRid();
        if (!SetMaps(material))
            return;
        SetTextures(material);

        _meshes.Add(Grid(null));
        for (var offset = 0; offset < 4; offset++)
            _meshes.Add(Grid(new Vector2I(offset % 2, offset / 2)));

        for (var level = 0; level < _levels; level++)
        {
            var instance = RenderingServer.InstanceCreate2(_meshes[level == 0 ? 0 : 1].GetRid(), world.Scenario);
            RenderingServer.InstanceGeometrySetMaterialOverride(instance, material);
            RenderingServer.InstanceSetLayerMask(instance, VisualLayer);
            RenderingServer.InstanceSetCustomAabb(instance, new Aabb(new Vector3(-RingCells - 2, -2000f, -RingCells - 2), new Vector3(2 * RingCells + 4, 6000f, 2 * RingCells + 4)));
            RenderingServer.InstanceSetVisible(instance, IsVisibleInTree());
            _instances.Add(instance);
        }

        if (!Engine.IsEditorHint() || _editorCollision)
        {
            _collides = true;
            var tiles = _data.RegionSize / CollisionTile;
            // Streamed: each region's tiles made as it comes in reach (see TerrainStreaming).
            foreach (var location in _streaming ? [] : _layers.Keys)
            {
                for (var z = 0; z < tiles; z++)
                {
                    for (var x = 0; x < tiles; x++)
                        Collide(location * tiles + new Vector2I(x, z));
                }

                // Put in the world once whole: each shape added to a body in it rebuilds the body.
                if (_bodies.TryGetValue(location, out var body))
                    PhysicsServer3D.BodySetSpace(body, world.Space);
            }
        }

        _setUp = true;
        if (_streaming && Camera() is { } seen)
            StreamAround(seen.GlobalPosition, now: true);
        Follow(Camera()?.GlobalPosition ?? Vector3.Zero);
    }

    private void TearDown()
    {
        foreach (var instance in _instances)
            RenderingServer.FreeRid(instance);
        _instances.Clear();
        foreach (var body in _bodies.Values)
            PhysicsServer3D.FreeRid(body);
        foreach (var shape in _shapes.Values)
            PhysicsServer3D.FreeRid(shape);
        FreeMaskPatches();
        _bodies.Clear();
        _collides = false;
        _shapes.Clear();
        _layers.Clear();
        _freeSlots.Clear();
        _pending.Clear();
        _streamedAt = null;
        _streaming = false;
        // Let go of the meshes and texture arrays now, not when the garbage collector gets to them,
        // which can be after the engine has shut down (and crashes it).
        foreach (var mesh in _meshes)
            mesh.Dispose();
        foreach (var array in _arrays)
            array.Dispose();
        _meshes.Clear();
        _arrays.Clear();
        _heightMaps = null;
        _controlMaps = null;
        TearDownOverview();
        _setUp = false;
    }

    /// <summary>Moves the rings round a place: each to its own cells' doubled step, with the hole for the ring inside it where that ring is.</summary>
    private void Follow(Vector3 at)
    {
        if (_data is null)
            return;
        SetParam(Drawn.GetRid(), "_camera_pos", at);
        var inner = Vector2.Zero;
        for (var level = 0; level < _instances.Count; level++)
        {
            var size = _data.VertexSpacing * (1 << level);
            var step = 2f * size;
            var middle = new Vector2(Mathf.Floor(at.X / step) * step, Mathf.Floor(at.Z / step) * step);
            if (level > 0)
            {
                var offset = new Vector2I(Mathf.RoundToInt((inner.X - middle.X) / size), Mathf.RoundToInt((inner.Y - middle.Y) / size));
                RenderingServer.InstanceSetBase(_instances[level], _meshes[1 + offset.X + 2 * offset.Y].GetRid());
            }

            var place = new Transform3D(Basis.FromScale(new Vector3(size, 1f, size)), new Vector3(middle.X, 0f, middle.Y));
            RenderingServer.InstanceSetTransform(_instances[level], place);
            inner = middle;
        }
    }

    /// <summary>
    /// A flat grid of unit cells, <see cref="RingCells"/> each way from its middle: whole, or a ring
    /// round a hole half as wide, as far off its middle as the ring inside it can be (0 or 1 cells
    /// each way). Every cell is split corner to corner the same way, so a ring's morphed odd lines
    /// fold onto the next ring's triangles.
    /// </summary>
    private static ArrayMesh Grid(Vector2I? hole)
    {
        const int across = 2 * RingCells + 1;
        var vertices = new Vector3[across * across];
        for (var z = 0; z < across; z++)
        {
            for (var x = 0; x < across; x++)
                vertices[z * across + x] = new Vector3(x - RingCells, 0f, z - RingCells);
        }

        var indices = new List<int>();
        for (var z = 0; z < across - 1; z++)
        {
            for (var x = 0; x < across - 1; x++)
            {
                if (hole is { } h && InHole(x - RingCells, h.X) && InHole(z - RingCells, h.Y))
                    continue;
                var a = z * across + x;
                indices.AddRange(new[] { a, a + 1, a + across + 1, a, a + across + 1, a + across });
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static bool InHole(int cell, int offset) => cell >= offset - RingCells / 2 && cell < offset + RingCells / 2;

    // A shader parameter; a visual shader cannot have names starting with an underscore, so its are terrain_ instead.
    private void SetParam(Rid material, string name, Variant value)
    {
        RenderingServer.MaterialSetParam(material, _baked ? $"terrain{name}" : name, value);
        // Used in both its stages, a visual shader has a parameter twice: the second named with a 2.
        if (_baked)
            RenderingServer.MaterialSetParam(material, $"terrain{name}2", value);
    }

    /// <summary>Whether its shader reads baked maps in place of control words: a visual shader, which cannot read their bits.</summary>
    private bool _baked;

    private Texture2DArray?[] _weightMaps = [];
    private Texture2DArray? _flagMaps;
    private ImageTexture? _regionLookup;

    /// <summary>What a visual shader reads in place of control words and the region arrays: textures of the same things.</summary>
    private void SetBakedMaps(Rid material, List<TerrainRegion> regions, int[] map)
    {
        // Each region baked once, however many layers it fills (streamed, all start as the first region's).
        var bakes = new Dictionary<TerrainRegion, (Image[] Weights, Image Flags)>();
        var baked = regions.Select(r => bakes.TryGetValue(r, out var made) ? made : bakes[r] = TerrainBake.Bake(r.ControlValues, _data!.RegionSize, _slots)).ToList();
        _weightMaps = new Texture2DArray?[TerrainBake.MapsFor(_slots)];
        for (var i = 0; i < _weightMaps.Length; i++)
        {
            var slot = i;
            _weightMaps[i] = Layers(baked.Select(b => b.Weights[slot]));
            SetParam(material, $"_weight_maps_{i}", _weightMaps[i]!.GetRid());
        }

        _flagMaps = Layers(baked.Select(b => b.Flags));
        SetParam(material, "_flag_maps", _flagMaps.GetRid());

        var lookup = new float[RegionMapSize * RegionMapSize];
        for (var i = 0; i < lookup.Length; i++)
            lookup[i] = map[i];
        var bytes = new byte[lookup.Length * sizeof(float)];
        System.Buffer.BlockCopy(lookup, 0, bytes, 0, bytes.Length);
        _regionLookup = ImageTexture.CreateFromImage(Image.CreateFromData(RegionMapSize, RegionMapSize, false, Image.Format.Rf, bytes));
        SetParam(material, "_region_lookup", _regionLookup.GetRid());
    }

    private void UpdateBakedMaps(TerrainRegion region, int layer) =>
        GiveBakedMaps(TerrainBake.Bake(region.ControlValues, _data!.RegionSize, _slots), layer);

    private void GiveBakedMaps((Image[] Weights, Image Flags) baked, int layer)
    {
        for (var i = 0; i < _weightMaps.Length; i++)
            _weightMaps[i]?.UpdateLayer(baked.Weights[i], layer);
        _flagMaps?.UpdateLayer(baked.Flags, layer);
    }

    /// <summary>The graph Shaders/TerrainGroundVisual.tres holds: for a terrain with no textures.</summary>
    public VisualShader BuildDefaultShader() => TerrainGroundBuilder.Build(PlainColors.Length, [4, 5], PlainLayers);

    private List<TerrainMaterial?> SlotTextures() => _textures?.Materials.Select(t => (TerrainMaterial?)t).ToList() ?? [];

    private static Dictionary<int, int> PlainLayers => PlainRules.ToDictionary(r => r.Key, r => r.Value.Layer);

    /// <summary>
    /// How much of the ground at a place, with this control word, shows any of these textures (by
    /// their ids), from 0 to 1. On the auto-shader it goes by the rules alone, without the noise the
    /// shader adds to their edges.
    /// </summary>
    public float ShareOf(Vector3 at, uint control, IReadOnlyCollection<int> ids)
    {
        var blend = TerrainControl.Blend(control);
        var manual = (ids.Contains(TerrainControl.Base(control)) ? 1f - blend : 0f) + (ids.Contains(TerrainControl.Overlay(control)) ? blend : 0f);
        var auto = TerrainControl.AutoLevel(control) / (float)TerrainControl.FullAuto;
        return auto <= 0f ? manual : Mathf.Lerp(manual, AutoShareOf(at, ids), auto);
    }

    // As the shader does: from the top layer down, each takes what is left, and what no rule claims is the first slot's.
    private float AutoShareOf(Vector3 at, IReadOnlyCollection<int> ids)
    {
        var height = HeightAt(at);
        var normal = NormalAt(at);
        if (float.IsNaN(height) || !normal.IsFinite())
            return 0f;
        var slope = Mathf.RadToDeg(Mathf.Acos(Mathf.Min(normal.Normalized().Y, 1f)));
        static float InRange(float value, float low, float high, float blend)
        {
            var fade = Mathf.Max(blend, 0.001f);
            return Mathf.SmoothStep(low - fade, low + fade, value) * (1f - Mathf.SmoothStep(high - fade, high + fade, value));
        }

        var textures = SlotTextures();
        var rules = textures.Count == 0 ? PlainRules
            : Enumerable.Range(0, textures.Count).Where(i => textures[i]?.AutoShader == true).ToDictionary(i => i, i =>
            {
                var own = textures[i]!;
                return new AutoRule(own.AutoLayer, own.AutoMinHeight, own.AutoMaxHeight, own.AutoHeightBlend, own.AutoMinSlope, own.AutoMaxSlope, own.AutoSlopeBlend);
            });
        var share = 0f;
        var remaining = 1f;
        foreach (var layer in rules.GroupBy(r => r.Value.Layer).OrderByDescending(g => g.Key))
        {
            var claims = layer.Select(r => (Slot: r.Key, Claim: InRange(height, r.Value.MinHeight, r.Value.MaxHeight, r.Value.HeightBlend) * InRange(slope, r.Value.MinSlope, r.Value.MaxSlope, r.Value.SlopeBlend))).ToList();
            var total = claims.Sum(c => c.Claim);
            var over = Mathf.Max(total, 1f);
            share += remaining * claims.Where(c => ids.Contains(c.Slot)).Sum(c => c.Claim) / over;
            remaining *= Mathf.Max(1f - total, 0f);
        }

        return share + (ids.Contains(0) ? remaining : 0f);
    }

    /// <summary>
    /// How much of the ground at a place, with this control word, shows each texture (by its id), from
    /// 0 to 1, as <see cref="ShareOf"/> gives one at a time: for asking about all of them at once.
    /// </summary>
    public float[] SharesAt(Vector3 at, uint control)
    {
        var textures = SlotTextures();
        var shares = new float[Mathf.Max(textures.Count, PlainColors.Length)];
        var blend = TerrainControl.Blend(control);
        var auto = TerrainControl.AutoLevel(control) / (float)TerrainControl.FullAuto;
        void Add(int id, float amount)
        {
            if (id < shares.Length)
                shares[id] += amount;
        }

        Add(TerrainControl.Base(control), (1f - blend) * (1f - auto));
        Add(TerrainControl.Overlay(control), blend * (1f - auto));
        if (auto <= 0f)
            return shares;

        var height = HeightAt(at);
        var normal = NormalAt(at);
        if (float.IsNaN(height) || !normal.IsFinite())
            return shares;
        var slope = Mathf.RadToDeg(Mathf.Acos(Mathf.Min(normal.Normalized().Y, 1f)));
        static float InRange(float value, float low, float high, float fade)
        {
            fade = Mathf.Max(fade, 0.001f);
            return Mathf.SmoothStep(low - fade, low + fade, value) * (1f - Mathf.SmoothStep(high - fade, high + fade, value));
        }

        var rules = new List<(int Slot, AutoRule Rule)>();
        for (var i = 0; i < textures.Count; i++)
        {
            if (textures[i] is { AutoShader: true } own)
                rules.Add((i, new AutoRule(own.AutoLayer, own.AutoMinHeight, own.AutoMaxHeight, own.AutoHeightBlend, own.AutoMinSlope, own.AutoMaxSlope, own.AutoSlopeBlend)));
        }

        if (textures.Count == 0)
        {
            foreach (var (slot, rule) in PlainRules)
                rules.Add((slot, rule));
        }

        // As the shader does: from the top layer down, each takes what is left, and what no rule claims is the first slot's.
        rules.Sort((a, b) => b.Rule.Layer.CompareTo(a.Rule.Layer));
        var remaining = 1f;
        for (var from = 0; from < rules.Count;)
        {
            var to = from;
            var total = 0f;
            for (; to < rules.Count && rules[to].Rule.Layer == rules[from].Rule.Layer; to++)
            {
                var r = rules[to].Rule;
                total += InRange(height, r.MinHeight, r.MaxHeight, r.HeightBlend) * InRange(slope, r.MinSlope, r.MaxSlope, r.SlopeBlend);
            }

            var over = Mathf.Max(total, 1f);
            for (var i = from; i < to; i++)
            {
                var r = rules[i].Rule;
                Add(rules[i].Slot, auto * remaining * InRange(height, r.MinHeight, r.MaxHeight, r.HeightBlend) * InRange(slope, r.MinSlope, r.MaxSlope, r.SlopeBlend) / over);
            }

            remaining *= Mathf.Max(1f - total, 0f);
            from = to;
        }

        Add(0, auto * remaining);
        return shares;
    }

    private (int Slots, List<int> Paving, Dictionary<int, int> AutoLayers) WantedSlots()
    {
        var textures = SlotTextures();
        if (textures.Count == 0)
            return (PlainColors.Length, [4, 5], PlainLayers);
        var slots = Mathf.Clamp(textures.Count, TerrainGroundBuilder.MinSlots, TerrainGroundBuilder.MaxSlots);
        var used = Enumerable.Range(0, Mathf.Min(slots, textures.Count)).ToList();
        return (slots, used.Where(i => textures[i]?.IsPaving == true).ToList(), used.Where(i => textures[i]?.AutoShader == true).ToDictionary(i => i, i => textures[i]!.AutoLayer));
    }

    /// <summary>Gives a generated shader the slots its textures want, built again (and saved beside its data) if they changed. A shader of your own is left alone.</summary>
    private void EnsureShader()
    {
        var material = Drawn;
        if (!Generated)
            return;
        var (slots, paving, autoLayers) = WantedSlots();
        _slots = slots;
        var signature = TerrainGroundBuilder.Signature(slots, paving, autoLayers);
        if (BuiltSignature == signature)
            return;

        var path = _data is { ResourcePath: var dataPath } && dataPath.StartsWith("res://") && !dataPath.Contains("::")
            ? dataPath.GetBaseName() + "Ground.tres"
            : null;
        if (path is not null && ResourceLoader.Exists(path) && ResourceLoader.Load<VisualShader>(path) is { } saved
            && saved.HasMeta(TerrainGroundBuilder.SignatureMeta) && saved.GetMeta(TerrainGroundBuilder.SignatureMeta).AsString() == signature)
        {
            material.Shader = saved;
            return;
        }

        var built = TerrainGroundBuilder.Build(slots, paving, autoLayers);
        if (path is not null && Engine.IsEditorHint() && ResourceSaver.Save(built, path) == Error.Ok)
            built.TakeOverPath(path);
        material.Shader = built;
    }

    /// <summary>Follows changes to the textures in the editor, which tell it nothing: a new graph if the slots changed, or else just their settings.</summary>
    private void CheckSlots()
    {
        if (!Generated)
            return;
        var (slots, paving, autoLayers) = WantedSlots();
        if (BuiltSignature != TerrainGroundBuilder.Signature(slots, paving, autoLayers))
        {
            Redo();
            return;
        }

        if (SlotValues() != _slotValues)
            SetSlots(Drawn.GetRid());
    }

    private string SlotValues() => string.Join(";", SlotTextures().Select(t => t is null ? "" : $"{t.UsedUvScale},{t.UsedNormalDepth},{t.UsedRoughnessMod},{t.UsedTint},{t.AutoMinHeight},{t.AutoMaxHeight},{t.AutoHeightBlend},{t.AutoMinSlope},{t.AutoMaxSlope},{t.AutoSlopeBlend}"));

    /// <summary>Each slot's settings, from its texture; and the shader's own, from this node.</summary>
    private void SetSlots(Rid material)
    {
        var textures = SlotTextures();
        for (var i = 0; i < _slots; i++)
        {
            var texture = i < textures.Count ? textures[i] : null;
            var plain = i < PlainColors.Length ? PlainColors[i] : new Color(0.5f, 0.5f, 0.5f);
            SetParam(material, $"_slot_{i}_uv_scale", texture?.UsedUvScale ?? 0f);
            SetParam(material, $"_slot_{i}_normal_depth", texture?.UsedNormalDepth ?? 0f);
            SetParam(material, $"_slot_{i}_roughness_mod", texture?.UsedRoughnessMod ?? 0f);
            // With no texture of its own (see SetTextures: a plain white one), its tint is its flat colour.
            SetParam(material, $"_slot_{i}_tint", texture?.UsedTint ?? plain);
            var rule = texture is { } own
                ? new AutoRule(own.AutoLayer, own.AutoMinHeight, own.AutoMaxHeight, own.AutoHeightBlend, own.AutoMinSlope, own.AutoMaxSlope, own.AutoSlopeBlend)
                : PlainRules.GetValueOrDefault(i);
            SetParam(material, $"_slot_{i}_auto_min_height", rule.MinHeight);
            SetParam(material, $"_slot_{i}_auto_max_height", rule.MaxHeight);
            SetParam(material, $"_slot_{i}_auto_height_blend", rule.HeightBlend);
            SetParam(material, $"_slot_{i}_auto_min_slope", rule.MinSlope);
            SetParam(material, $"_slot_{i}_auto_max_slope", rule.MaxSlope);
            SetParam(material, $"_slot_{i}_auto_slope_blend", rule.SlopeBlend);
        }

        _slotValues = SlotValues();
        if (Generated)
            SetSettings(material);
        GiveMaskToShader();
    }

    private void SetSettings(Rid material)
    {
        RenderingServer.MaterialSetParam(material, "edge_height_noise", _edgeHeightNoise);
        RenderingServer.MaterialSetParam(material, "edge_slope_noise", _edgeSlopeNoise);
        RenderingServer.MaterialSetParam(material, "edge_noise_size", _edgeNoiseSize);
        RenderingServer.MaterialSetParam(material, "blend_sharpness", _blendSharpness);
        RenderingServer.MaterialSetParam(material, "paving_edge_hardness", _pavingEdgeHardness);
        RenderingServer.MaterialSetParam(material, "far_uv_scale", _farUvScale);
        RenderingServer.MaterialSetParam(material, "far_blend_start", _farBlendStart);
        RenderingServer.MaterialSetParam(material, "far_blend_end", _farBlendEnd);
        RenderingServer.MaterialSetParam(material, "far_blend_amount", _farBlendAmount);
        RenderingServer.MaterialSetParam(material, "far_rotation", _farRotation);
        RenderingServer.MaterialSetParam(material, "farthest_uv_scale", _farthestUvScale);
        RenderingServer.MaterialSetParam(material, "farthest_blend_start", _farthestBlendStart);
        RenderingServer.MaterialSetParam(material, "farthest_blend_end", _farthestBlendEnd);
        RenderingServer.MaterialSetParam(material, "farthest_blend_amount", _farthestBlendAmount);
        RenderingServer.MaterialSetParam(material, "farthest_rotation", _farthestRotation);
    }

    /// <summary>Puts the regions' heights, control and colours on the GPU, and tells the shader where each region is.</summary>
    private bool SetMaps(Rid material)
    {
        var regions = _data!.Locations.Select(_data.Region).OfType<TerrainRegion>().ToList();
        if (regions.Count == 0 || regions.Any(r => r.Heights is null || r.Control is null || r.Colors is null))
            return false;

        var map = new int[RegionMapSize * RegionMapSize];
        var locations = new Vector2[RegionMapSize * RegionMapSize];
        _baked = RenderingServer.GetShaderParameterList(Drawn.Shader.GetRid()).Any(p => p["name"].AsString() == "terrain_weight_maps_0");
        // The whole terrain, small, for the shader to draw far ground from (when it can): before
        // deciding how much is streamed, as with it, only near regions need their full maps.
        SetUpOverview(material, regions);
        _streaming = StreamDistance > 0f;
        if (_streaming)
        {
            // Streamed: as many layers as can be in reach at once, filled as regions come in reach
            // (see TerrainStreaming); until then the first region's images hold their places.
            var slots = System.Math.Min(regions.Count, SlotsFor(StreamDistance + StreamMargin));
            regions = Enumerable.Repeat(regions[0], slots).ToList();
            _freeSlots.Clear();
            for (var slot = slots - 1; slot >= 0; slot--)
                _freeSlots.Push(slot);
        }

        for (var layer = 0; layer < (_streaming ? 0 : regions.Count); layer++)
        {
            var at = regions[layer].Location + new Vector2I(RegionMapSize / 2, RegionMapSize / 2);
            if (at.X < 0 || at.Y < 0 || at.X >= RegionMapSize || at.Y >= RegionMapSize)
                continue;
            map[at.Y * RegionMapSize + at.X] = layer + 1;
            locations[layer] = regions[layer].Location;
            _layers[regions[layer].Location] = layer;
        }

        if (_baked)
            SetBakedMaps(material, regions, map);
        _heightMaps = Layers(regions.Select(r => r.Heights!));
        SetParam(material, "_height_maps", _heightMaps.GetRid());
        // A shader reading baked maps never reads the control words: not put on the GPU for it.
        if (!_baked)
        {
            _controlMaps = Layers(regions.Select(r => r.Control!));
            SetParam(material, "_control_maps", _controlMaps.GetRid());
        }
        _colorMaps = Layers(regions.Select(r => r.Colors!));
        SetParam(material, "_color_maps", _colorMaps.GetRid());
        _map = map;
        _locations = locations;
        SetParam(material, "_region_map", map);
        SetParam(material, "_region_locations", locations);
        SetParam(material, "_region_map_size", RegionMapSize);
        SetParam(material, "_region_size", (float)_data.RegionSize);
        SetParam(material, "_region_texel_size", 1f / _data.RegionSize);
        SetParam(material, "_vertex_spacing", _data.VertexSpacing);
        SetParam(material, "_vertex_density", 1f / _data.VertexSpacing);
        SetParam(material, "_ring_cells", (float)RingCells);
        return true;
    }

    /// <summary>
    /// Puts the ground textures on the GPU as two texture arrays (all made the same size and format),
    /// with each one's settings. A slot with no albedo texture gets a plain white one, and with no
    /// normal map a flat one (roughness 1 in alpha): it then draws as a flat colour, its tint, with
    /// its roughness. With no textures at all, every slot is one of those.
    /// </summary>
    private void SetTextures(Rid material)
    {
        var textures = _textures?.Materials.Where(t => t is not null).ToList() ?? new List<TerrainMaterial>();
        // As many layers as it has textures (the shader's slots past them have no weight anywhere);
        // with none, one for each slot, each a flat colour.
        var count = textures.Count > 0 ? textures.Count : _slots;
        TerrainMaterial? At(int i) => i < textures.Count ? textures[i] : null;
        var albedos = Same(Enumerable.Range(0, count).Select(i => At(i)?.UsedAlbedo?.GetImage()).ToList(), Colors.White);
        var normals = Same(Enumerable.Range(0, count).Select(i => At(i)?.UsedNormal?.GetImage()).ToList(), new Color(0.5f, 0.5f, 1f, 1f));
        SetParam(material, "_texture_array_albedo", Array(albedos));
        SetParam(material, "_texture_array_normal", Array(normals));

        float[] Each(System.Func<TerrainMaterial, float> of) =>
            Enumerable.Range(0, 32).Select(i => i < textures.Count ? of(textures[i]) : 0f).ToArray();
        if (_baked)
            SetSlots(material);

        SetParam(material, "_texture_uv_scale_array", Each(t => t.UsedUvScale));
        SetParam(material, "_texture_normal_depth_array", Each(t => t.UsedNormalDepth));
        SetParam(material, "_texture_ao_strength_array", Each(t => t.UsedAoStrength));
        SetParam(material, "_texture_roughness_mod_array", Each(t => t.UsedRoughnessMod));
        SetParam(material, "_texture_color_array",
            Enumerable.Range(0, 32).Select(i => i < textures.Count ? textures[i].UsedTint : Colors.White).ToArray());
    }

    /// <summary>
    /// Images as one texture array takes them: as they are if all alike, else each made the largest's
    /// size, uncompressed, with mipmaps. One missing is filled with a plain one of this colour.
    /// </summary>
    private static List<Image> Same(List<Image?> images, Color plain)
    {
        var size = images.OfType<Image>().Select(i => i.GetSize()).DefaultIfEmpty(new Vector2I(4, 4)).Aggregate((a, b) => a.X * a.Y >= b.X * b.Y ? a : b);
        var found = images.Select(i => i ?? Plain(size, plain)).ToList();
        var first = found[0];
        if (found.All(i => i.GetSize() == first.GetSize() && i.GetFormat() == first.GetFormat() && i.HasMipmaps() == first.HasMipmaps()))
            return found;

        return found.Select(i =>
        {
            var copy = (Image)i.Duplicate();
            if (copy.IsCompressed())
                copy.Decompress();
            copy.Convert(Image.Format.Rgba8);
            if (copy.GetSize() != size)
                copy.Resize(size.X, size.Y, Image.Interpolation.Cubic);
            copy.GenerateMipmaps();
            return copy;
        }).ToList();
    }

    /// <summary>A plain image of one colour, with mipmaps, to stand in for a missing texture.</summary>
    private static Image Plain(Vector2I size, Color colour)
    {
        var image = Image.CreateEmpty(size.X, size.Y, false, Image.Format.Rgba8);
        image.Fill(colour);
        image.GenerateMipmaps();
        return image;
    }

    private Rid Array(IEnumerable<Image> images) => Layers(images).GetRid();

    private Texture2DArray Layers(IEnumerable<Image> images)
    {
        var array = new Texture2DArray();
        array.CreateFromImages(new Godot.Collections.Array<Image>(images));
        _arrays.Add(array);
        return array;
    }

    /// <summary>
    /// A region's body, made the first time it is asked for. Made while setting up, it is put in the
    /// world once its tiles are on it (see SetUp); made later, at once.
    /// </summary>
    private Rid BodyOf(Vector2I region)
    {
        if (_bodies.TryGetValue(region, out var body))
            return body;
        body = PhysicsServer3D.BodyCreate();
        PhysicsServer3D.BodySetMode(body, PhysicsServer3D.BodyMode.Static);
        PhysicsServer3D.BodyAttachObjectInstanceId(body, GetInstanceId());
        PhysicsServer3D.BodySetCollisionLayer(body, _collisionLayer);
        PhysicsServer3D.BodySetCollisionMask(body, _collisionMask);
        if (_setUp && GetWorld3D() is { } world)
            PhysicsServer3D.BodySetSpace(body, world.Space);
        _bodies[region] = body;
        return body;
    }

    /// <summary>The region a collision tile is in.</summary>
    private Vector2I RegionOfTile(Vector2I tile) =>
        new(FloorDiv(tile.X * CollisionTile, _data!.RegionSize), FloorDiv(tile.Y * CollisionTile, _data.RegionSize));

    /// <summary>
    /// Makes (or makes again) a collision tile's shape: its <see cref="CollisionTile"/> samples each
    /// way, and the first row and column of the tiles after it, so the shapes meet; NaN in holes,
    /// which the shape leaves open. A heightmap shape splits its cells along the other diagonal from
    /// the drawn ground's, so it is turned a quarter round, its rows running along x.
    /// </summary>
    private void Collide(Vector2I tile)
    {
        if (!_collides || _data is null)
            return;
        var location = RegionOfTile(tile);
        // Streamed and out of reach: not solid until it comes in reach.
        if (_streaming && !_layers.ContainsKey(location))
            return;
        if (TileShape(tile, MaskShapes()) is { } made)
            GiveTile(made);
    }

    /// <summary>A collision tile worked out: its heights (NaN where open), and its patch's faces where masks cut it, if any.</summary>
    private sealed record TileMade(Vector2I Tile, float[] Heights, float Low, float High, Vector3[]? Faces);

    /// <summary>
    /// Works out a tile's shape, reading only its regions and the masks' shapes given: safe on a
    /// loading thread, once the regions round it have been looked up.
    /// </summary>
    private TileMade? TileShape(Vector2I tile, List<(Rect2 Box, Vector2[] Shape, float Top)> masks)
    {
        var size = _data!.RegionSize;
        var location = new Vector2I(FloorDiv(tile.X * CollisionTile, size), FloorDiv(tile.Y * CollisionTile, size));
        if (_data.Region(location) is not { } own)
            return null;

        const int across = CollisionTile + 1;
        var heights = new float[across * across];
        var low = float.MaxValue;
        var high = float.MinValue;
        for (var oz = 0; oz < across; oz++)
        {
            for (var ox = 0; ox < across; ox++)
            {
                // The sample; past the last region, this tile's edge again.
                var sx = tile.X * CollisionTile + ox;
                var sz = tile.Y * CollisionTile + oz;
                // Inside the tile's own region (all but, at its edge, the last row and column): read straight from it.
                var (lx, lz) = (sx - location.X * size, sz - location.Y * size);
                var (region, index) = lx < size && lz < size ? (own, lz * size + lx) : _data.Sample(sx, sz);
                if (region is null)
                    (region, index) = _data.Sample(tile.X * CollisionTile + System.Math.Min(ox, CollisionTile - 1), tile.Y * CollisionTile + System.Math.Min(oz, CollisionTile - 1));
                var height = TerrainControl.IsHole(region!.ControlValues[index]) ? float.NaN : region.HeightValues[index];
                heights[ox * across + (CollisionTile - oz)] = height;
                if (!float.IsNaN(height))
                {
                    low = System.Math.Min(low, height);
                    high = System.Math.Max(high, height);
                }
            }
        }

        var faces = MaskFaces(tile, heights, across, masks);
        return new TileMade(tile, heights, low, high, faces);
    }

    /// <summary>A tile worked out, given to physics: on the main thread.</summary>
    private void GiveTile(TileMade made)
    {
        const int across = CollisionTile + 1;
        var tile = made.Tile;
        var location = RegionOfTile(tile);
        if (!_shapes.TryGetValue(tile, out var shape))
        {
            shape = PhysicsServer3D.HeightmapShapeCreate();
            _shapes[tile] = shape;
            var middle = (new Vector2(tile.X, tile.Y) + new Vector2(0.5f, 0.5f)) * CollisionTile * _data!.VertexSpacing;
            var turn = new Basis(Vector3.Up, Mathf.Pi / 2f).Scaled(new Vector3(_data.VertexSpacing, 1f, _data.VertexSpacing));
            PhysicsServer3D.BodyAddShape(BodyOf(location), shape, new Transform3D(turn, new Vector3(middle.X, 0f, middle.Y)));
        }

        GivePatch(tile, made.Faces);
        PhysicsServer3D.ShapeSetData(shape, new Godot.Collections.Dictionary
        {
            ["width"] = across,
            ["depth"] = across,
            ["heights"] = made.Heights,
            ["min_height"] = made.Low <= made.High ? made.Low : 0f,
            ["max_height"] = made.Low <= made.High ? made.High : 0f,
        });
    }
}
