using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// Draws a terrain's ground (<see cref="Data"/>) with its <see cref="Material"/>, as a clipmap: a
/// square of <see cref="RingCells"/> cells each way round the camera, then <see cref="Levels"/> - 1
/// square rings round it, each with cells twice the size of the one inside it. The rings are flat
/// grids drawn straight through the RenderingServer (no nodes); the material's shader lifts them to
/// the heights it reads from the regions' maps, which this puts on the GPU as texture arrays, along
/// with the ground textures (<see cref="Textures"/>). The rings follow the camera in steps of their
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

    /// <summary>The shader that comes with the plugin, for a terrain given no material of its own.</summary>
    public const string DefaultShader = "res://addons/unpleasant_terrain/Shaders/TerrainGround.gdshader";

    private ShaderMaterial? _defaultMaterial;

    /// <summary>What it is drawn with: its own material, or else one of the default shader.</summary>
    private ShaderMaterial Drawn => _material ?? (_defaultMaterial ??= new ShaderMaterial { Shader = GD.Load<Shader>(DefaultShader) });
    private TerrainTextureList? _textures;
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
    private Rid _body;
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

    /// <summary>The ground's material: a ShaderMaterial with a shader written for this terrain. Left empty, the default shader (addons/unpleasant_terrain/Shaders/TerrainGround.gdshader) is used.</summary>
    [Export]
    public ShaderMaterial? Material
    {
        get => _material;
        set
        {
            _material = value;
            Redo();
        }
    }

    [Export]
    public TerrainTextureList? Textures
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
            if (_body.IsValid)
                PhysicsServer3D.BodySetCollisionLayer(_body, value);
        }
    }

    [Export(PropertyHint.Layers3DPhysics)]
    public uint CollisionMask
    {
        get => _collisionMask;
        set
        {
            _collisionMask = value;
            if (_body.IsValid)
                PhysicsServer3D.BodySetCollisionMask(_body, value);
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
            if (_layers.TryGetValue(region.Location, out var layer))
            {
                if (heights)
                    _heightMaps?.UpdateLayer(region.Heights, layer);
                else
                    _controlMaps?.UpdateLayer(region.Control, layer);
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

    // Add Region pressed: a flat region at New Region, one step to undo; data and a folder for it first, if it has none.
    private void AddNewRegion()
    {
        var at = NewRegion;
        var half = RegionMapSize / 2;
        if (at.X < -half || at.Y < -half || at.X >= half || at.Y >= half)
        {
            GD.PushWarning($"{Name}: New Region must be from {-half} to {half - 1} each way.");
            return;
        }

        _data ??= new TerrainData();
        if (string.IsNullOrEmpty(_data.Folder))
            _data.Folder = $"res://Terrain/{Name}-Regions";
        if (_data.Locations.Contains(at))
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
        if (!_setUp || Camera() is not { } camera)
            return;
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
            _body = PhysicsServer3D.BodyCreate();
            PhysicsServer3D.BodySetMode(_body, PhysicsServer3D.BodyMode.Static);
            PhysicsServer3D.BodySetSpace(_body, world.Space);
            PhysicsServer3D.BodyAttachObjectInstanceId(_body, GetInstanceId());
            PhysicsServer3D.BodySetCollisionLayer(_body, _collisionLayer);
            PhysicsServer3D.BodySetCollisionMask(_body, _collisionMask);
            var tiles = _data.RegionSize / CollisionTile;
            foreach (var location in _layers.Keys)
            {
                for (var z = 0; z < tiles; z++)
                {
                    for (var x = 0; x < tiles; x++)
                        Collide(location * tiles + new Vector2I(x, z));
                }
            }
        }

        _setUp = true;
        Follow(Camera()?.GlobalPosition ?? Vector3.Zero);
    }

    private void TearDown()
    {
        foreach (var instance in _instances)
            RenderingServer.FreeRid(instance);
        _instances.Clear();
        if (_body.IsValid)
            PhysicsServer3D.FreeRid(_body);
        foreach (var shape in _shapes.Values)
            PhysicsServer3D.FreeRid(shape);
        _body = default;
        _shapes.Clear();
        _layers.Clear();
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
        _setUp = false;
    }

    /// <summary>Moves the rings round a place: each to its own cells' doubled step, with the hole for the ring inside it where that ring is.</summary>
    private void Follow(Vector3 at)
    {
        if (_data is null)
            return;
        RenderingServer.MaterialSetParam(Drawn.GetRid(), "_camera_pos", at);
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

    /// <summary>Puts the regions' heights, control and colours on the GPU, and tells the shader where each region is.</summary>
    private bool SetMaps(Rid material)
    {
        var regions = _data!.Locations.Select(_data.Region).OfType<TerrainRegion>().ToList();
        if (regions.Count == 0 || regions.Any(r => r.Heights is null || r.Control is null || r.Colors is null))
            return false;

        var map = new int[RegionMapSize * RegionMapSize];
        var locations = new Vector2[RegionMapSize * RegionMapSize];
        for (var layer = 0; layer < regions.Count; layer++)
        {
            var at = regions[layer].Location + new Vector2I(RegionMapSize / 2, RegionMapSize / 2);
            if (at.X < 0 || at.Y < 0 || at.X >= RegionMapSize || at.Y >= RegionMapSize)
                continue;
            map[at.Y * RegionMapSize + at.X] = layer + 1;
            locations[layer] = regions[layer].Location;
            _layers[regions[layer].Location] = layer;
        }

        _heightMaps = Layers(regions.Select(r => r.Heights!));
        _controlMaps = Layers(regions.Select(r => r.Control!));
        RenderingServer.MaterialSetParam(material, "_height_maps", _heightMaps.GetRid());
        RenderingServer.MaterialSetParam(material, "_control_maps", _controlMaps.GetRid());
        RenderingServer.MaterialSetParam(material, "_color_maps", Array(regions.Select(r => r.Colors!)));
        RenderingServer.MaterialSetParam(material, "_region_map", map);
        RenderingServer.MaterialSetParam(material, "_region_locations", locations);
        RenderingServer.MaterialSetParam(material, "_region_map_size", RegionMapSize);
        RenderingServer.MaterialSetParam(material, "_region_size", (float)_data.RegionSize);
        RenderingServer.MaterialSetParam(material, "_region_texel_size", 1f / _data.RegionSize);
        RenderingServer.MaterialSetParam(material, "_vertex_spacing", _data.VertexSpacing);
        RenderingServer.MaterialSetParam(material, "_vertex_density", 1f / _data.VertexSpacing);
        RenderingServer.MaterialSetParam(material, "_ring_cells", (float)RingCells);
        return true;
    }

    /// <summary>Puts the ground textures on the GPU as two texture arrays (all made the same size and format), with each one's settings.</summary>
    private void SetTextures(Rid material)
    {
        var textures = _textures?.Textures.Where(t => t is not null).ToList() ?? new List<TerrainTexture>();
        var albedos = Same(textures.Select(t => t.Albedo?.GetImage()).ToList());
        var normals = Same(textures.Select(t => t.Normal?.GetImage()).ToList());
        if (albedos.Count > 0)
            RenderingServer.MaterialSetParam(material, "_texture_array_albedo", Array(albedos));
        if (normals.Count > 0)
            RenderingServer.MaterialSetParam(material, "_texture_array_normal", Array(normals));

        float[] Each(System.Func<TerrainTexture, float> of) =>
            Enumerable.Range(0, 32).Select(i => i < textures.Count ? of(textures[i]) : 0f).ToArray();
        RenderingServer.MaterialSetParam(material, "_texture_uv_scale_array", Each(t => t.UvScale));
        RenderingServer.MaterialSetParam(material, "_texture_normal_depth_array", Each(t => t.NormalDepth));
        RenderingServer.MaterialSetParam(material, "_texture_ao_strength_array", Each(t => t.AoStrength));
        RenderingServer.MaterialSetParam(material, "_texture_roughness_mod_array", Each(t => t.RoughnessMod));
        RenderingServer.MaterialSetParam(material, "_texture_color_array",
            Enumerable.Range(0, 32).Select(i => i < textures.Count ? textures[i].Tint : Colors.White).ToArray());
    }

    /// <summary>Images as one texture array takes them: as they are if all alike, else each made the largest's size, uncompressed, with mipmaps.</summary>
    private static List<Image> Same(List<Image?> images)
    {
        var found = images.OfType<Image>().ToList();
        if (found.Count == 0 || found.Count != images.Count)
            return new List<Image>();
        var first = found[0];
        if (found.All(i => i.GetSize() == first.GetSize() && i.GetFormat() == first.GetFormat() && i.HasMipmaps() == first.HasMipmaps()))
            return found;

        var size = found.Select(i => i.GetSize()).Aggregate((a, b) => a.X * a.Y >= b.X * b.Y ? a : b);
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

    private Rid Array(IEnumerable<Image> images) => Layers(images).GetRid();

    private Texture2DArray Layers(IEnumerable<Image> images)
    {
        var array = new Texture2DArray();
        array.CreateFromImages(new Godot.Collections.Array<Image>(images));
        _arrays.Add(array);
        return array;
    }

    /// <summary>
    /// Makes (or makes again) a collision tile's shape: its <see cref="CollisionTile"/> samples each
    /// way, and the first row and column of the tiles after it, so the shapes meet; NaN in holes,
    /// which the shape leaves open. A heightmap shape splits its cells along the other diagonal from
    /// the drawn ground's, so it is turned a quarter round, its rows running along x.
    /// </summary>
    private void Collide(Vector2I tile)
    {
        if (!_body.IsValid || _data is null)
            return;
        var size = _data.RegionSize;
        var location = new Vector2I(FloorDiv(tile.X * CollisionTile, size), FloorDiv(tile.Y * CollisionTile, size));
        if (_data.Region(location) is null)
            return;

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
                var (region, index) = _data.Sample(sx, sz);
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

        if (!_shapes.TryGetValue(tile, out var shape))
        {
            shape = PhysicsServer3D.HeightmapShapeCreate();
            _shapes[tile] = shape;
            var middle = (new Vector2(tile.X, tile.Y) + new Vector2(0.5f, 0.5f)) * CollisionTile * _data.VertexSpacing;
            var turn = new Basis(Vector3.Up, Mathf.Pi / 2f).Scaled(new Vector3(_data.VertexSpacing, 1f, _data.VertexSpacing));
            PhysicsServer3D.BodyAddShape(_body, shape, new Transform3D(turn, new Vector3(middle.X, 0f, middle.Y)));
        }

        PhysicsServer3D.ShapeSetData(shape, new Godot.Collections.Dictionary
        {
            ["width"] = across,
            ["depth"] = across,
            ["heights"] = heights,
            ["min_height"] = low <= high ? low : 0f,
            ["max_height"] = low <= high ? high : 0f,
        });
    }
}
