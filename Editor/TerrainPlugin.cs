#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain.Editor;

/// <summary>
/// Sculpts and paints a <see cref="TerrainNode"/>'s ground in the 3D editor. With one selected,
/// the 3D view's toolbar (TerrainToolbar.tscn) has the brushes, and the Terrain dock
/// (TerrainDock.tscn) its textures to paint with. Hold the mouse on the ground to work it: the
/// brush keeps working while held, strongest at its middle and fading to nothing at its edge.
/// Ctrl turns Raise into Lower (and back), cuts into fills, and Auto into taking the auto-shader
/// off. Each stroke is one step to undo.
/// </summary>
[Tool]
public partial class TerrainPlugin : EditorPlugin
{
    private enum Brush { None, Raise, Lower, Smooth, Flatten, Slope, SetHeight, Paint, Auto, Holes }

    private Control? _toolbar;
    private Control? _dock;
    private ItemList? _textures;
    private Label? _hint;
    private SpinBox? _size;
    private SpinBox? _falloff;
    private SpinBox? _strength;
    private SpinBox? _height;

    private TerrainNode? _terrain;
    private Camera3D? _camera;
    private Vector2 _mouse;
    private Vector3? _brushAt;
    private bool _stroking;
    private bool _inverted;
    private float _flattenTo;
    private ulong _lastTick;
    private float _strokeWork;

    // What the samples touched in this stroke were before it, for undo.
    private readonly Dictionary<(int X, int Z), float> _heightsBefore = [];
    private readonly Dictionary<(int X, int Z), uint> _controlBefore = [];

    // The images a brush can take its shape from.
    private const string ShapesFolder = "res://addons/unpleasant_terrain/Brushes";
    private const string NoisePrefix = "noise:";
    private const string SettingsSection = "unpleasant_terrain_brush_shapes";

    // The shape this stroke uses (null: a plain circle), and how it lies. Shapes already made are kept by what they were made from.
    private BrushAlpha? _stamp;
    private float _stampAngle;
    private float _stampScale = 1f;
    private readonly Dictionary<string, BrushAlpha> _alphas = [];

    // The textures this stroke paints over (none: everything), and how much of each sample it touches showed them when it started.
    private readonly List<int> _paintOver = [];
    private readonly Dictionary<(int X, int Z), float> _paintOverShare = [];

    public override void _EnterTree()
    {
        _toolbar = GD.Load<PackedScene>("res://addons/unpleasant_terrain/TerrainToolbar.tscn").Instantiate<Control>();
        foreach (var brush in Enum.GetValues<Brush>().Where(b => b != Brush.None))
        {
            var button = _toolbar.GetNode<Button>(brush.ToString());
            // By method name, not a lambda: the editor can then let go of them as it reloads the scripts.
            button.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.BrushToggled));
        }

        _size = _toolbar.GetNode<SpinBox>("Size");
        _falloff = _toolbar.GetNode<SpinBox>("Falloff");
        _strength = _toolbar.GetNode<SpinBox>("Strength");
        _height = _toolbar.GetNode<SpinBox>("Height");
        _toolbar.Visible = false;
        ToolButtons.StyleAll(_toolbar);
        AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _toolbar);

        _dock = GD.Load<PackedScene>("res://addons/unpleasant_terrain/TerrainDock.tscn").Instantiate<Control>();
        _hint = _dock.GetNode<Label>("Hint");
        _textures = _dock.GetNode<ItemList>("Textures");
        // In its slot only while the plugin is shown: see _MakeVisible.
        SetProcess(true);
        Connect(EditorPlugin.SignalName.SceneChanged, new Callable(this, MethodName.OnSceneChanged));
    }

    // Another scene's tab opened: if the terrain being edited is not in it, the tools put away, as
    // the editor does not say so itself.
    // The signal hands over the new scene's root; it is not needed here, but has to be taken.
    private void OnSceneChanged(Node? root)
    {
        if (Editing)
            return;
        if (_stroking)
            EndStroke();
        foreach (var brush in Enum.GetValues<Brush>().Where(b => b != Brush.None))
        {
            if (_toolbar?.GetNodeOrNull<Button>(brush.ToString()) is { ButtonPressed: true } button)
                button.SetPressedNoSignal(false);
        }

        _terrain = null;
        ShowTextures();
        _MakeVisible(false);
    }

    /// <summary>Whether its terrain is there to edit: still in the scene being edited.</summary>
    private bool Editing =>
        _terrain is not null && IsInstanceValid(_terrain) && _terrain.IsInsideTree()
        && EditorInterface.Singleton.GetEditedSceneRoot() is { } root && (root == _terrain || root.IsAncestorOf(_terrain));

    public override void _ExitTree()
    {
        if (_toolbar is not null)
        {
            RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, _toolbar);
            _toolbar.QueueFree();
        }

        var shapes = new Callable(this, MethodName.ShowShape);
        var files = EditorInterface.Singleton.GetResourceFilesystem();
        if (files.IsConnected(EditorFileSystem.SignalName.FilesystemChanged, shapes))
            files.Disconnect(EditorFileSystem.SignalName.FilesystemChanged, shapes);
        PluginDocks.Remove(this, _dock);
        if (_import is not null && IsInstanceValid(_import))
            _import.QueueFree();

    }

    // Import Heightmap pressed: its dialog, made the first time.
    private void ImportPressed()
    {
        if (!Editing)
        {
            GD.PushWarning("Terrain: select a TerrainNode first.");
            return;
        }

        if (_import is null || !IsInstanceValid(_import))
        {
            _import = GD.Load<PackedScene>("res://addons/unpleasant_terrain/Editor/HeightmapImportDialog.tscn").Instantiate<HeightmapImportDialog>();
            EditorInterface.Singleton.GetBaseControl().AddChild(_import);
        }

        _import.Open(_terrain!);
    }

    private HeightmapImportDialog? _import;

    /// <summary>A brush button pressed or let go (the signal says which; either way the brush outline goes until the mouse moves).</summary>
    public void BrushToggled(bool pressed)
    {
        _brushAt = null;
        UpdateOverlays();
        ShowShape();
    }

    /// <summary>Whether a brush takes a shape from the dock.</summary>
    private static bool TakesShape(Brush brush) => brush is Brush.Raise or Brush.Lower or Brush.Paint or Brush.Auto;

    private ItemList? Alphas => _dock?.GetNodeOrNull<ItemList>("Shape/Box/Alphas");

    private SpinBox? ShapeSpin(string path) => _dock?.GetNodeOrNull<SpinBox>($"Shape/Box/{path}");

    private CheckBox? ShapeRandom => _dock?.GetNodeOrNull<CheckBox>("Shape/Box/Settings/Random");

    /// <summary>The dock's shape controls, showing what the brush picked in the toolbar last used.</summary>
    private void ShowShape()
    {
        if (_dock?.GetNodeOrNull<Control>("Shape") is not { } section || Alphas is not { } list)
            return;
        var brush = Current;
        if (_dock.GetNodeOrNull<Control>("PaintOver") is { } paintOver)
            paintOver.Visible = brush == Brush.Paint;
        section.Visible = TakesShape(brush);
        if (!section.Visible)
            return;
        list.Clear();
        list.AddItem("None");
        list.SetItemMetadata(0, "");
        foreach (var (name, type) in BrushAlpha.Noises)
        {
            var index = list.AddItem(name, BrushAlpha.FromNoise(type, 6f, 4, 0).Preview());
            list.SetItemMetadata(index, NoisePrefix + name);
        }

        if (DirAccess.DirExistsAbsolute(ShapesFolder))
        {
            foreach (var file in DirAccess.GetFilesAt(ShapesFolder))
            {
                var path = $"{ShapesFolder}/{file}";
                if (file.EndsWith(".import", StringComparison.Ordinal) || file.EndsWith(".uid", StringComparison.Ordinal)
                    || !ResourceLoader.Exists(path, "Texture2D") || GD.Load<Texture2D>(path)?.GetImage() is not { } image)
                    continue;
                var index = list.AddItem(file.GetBaseName(), BrushAlpha.FromImage(image).Preview());
                list.SetItemMetadata(index, path);
            }
        }

        var saved = EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata(SettingsSection, brush.ToString(), new Godot.Collections.Dictionary()).AsGodotDictionary();
        double Saved(string key, double fallback) => saved.TryGetValue(key, out var value) ? value.AsDouble() : fallback;
        var shape = saved.TryGetValue("shape", out var picked) ? picked.AsString() : "";
        var item = Enumerable.Range(0, list.ItemCount).FirstOrDefault(i => list.GetItemMetadata(i).AsString() == shape);
        list.Select(item);
        ShapeSpin("Settings/Rotation")?.SetValueNoSignal(Saved("rotation", 0d));
        ShapeSpin("Settings/Scale")?.SetValueNoSignal(Saved("scale", 1d));
        ShapeRandom?.SetPressedNoSignal(Saved("random", 0d) != 0d);
        ShapeSpin("Noise/Features")?.SetValueNoSignal(Saved("features", 6d));
        ShapeSpin("Noise/Octaves")?.SetValueNoSignal(Saved("octaves", 4d));
        ShapeSpin("Noise/Seed")?.SetValueNoSignal(Saved("seed", 0d));
        ShowShapeSettings();
    }

    // The settings that apply: the noise's for a noise, and Rotation unless the angle is random.
    private void ShowShapeSettings()
    {
        if (_dock?.GetNodeOrNull<Control>("Shape/Box/Noise") is { } noise)
            noise.Visible = PickedShape.StartsWith(NoisePrefix, StringComparison.Ordinal);
        if (ShapeSpin("Settings/Rotation") is { } rotation)
            rotation.Editable = ShapeRandom?.ButtonPressed != true;
    }

    private string PickedShape =>
        Alphas is { } list && list.GetSelectedItems() is { Length: > 0 } picked ? list.GetItemMetadata(picked[0]).AsString() : "";

    // A shape control changed, by each kind of control's signal: the brush remembers it.
    private void ShapeItemPicked(long index) => SaveShape();

    private void ShapeValueChanged(double value) => SaveShape();

    private void ShapeToggled(bool pressed) => SaveShape();

    private void SaveShape()
    {
        ShowShapeSettings();
        if (!TakesShape(Current))
            return;
        EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata(SettingsSection, Current.ToString(), new Godot.Collections.Dictionary
        {
            ["shape"] = PickedShape,
            ["rotation"] = ShapeSpin("Settings/Rotation")?.Value ?? 0d,
            ["random"] = ShapeRandom?.ButtonPressed == true ? 1d : 0d,
            ["scale"] = ShapeSpin("Settings/Scale")?.Value ?? 1d,
            ["features"] = ShapeSpin("Noise/Features")?.Value ?? 6d,
            ["octaves"] = ShapeSpin("Noise/Octaves")?.Value ?? 4d,
            ["seed"] = ShapeSpin("Noise/Seed")?.Value ?? 0d,
        });
    }

    /// <summary>Connects the dock's shape controls: here, not on entering the tree, which a rebuild of the scripts does not repeat.</summary>
    private void ConnectShapeControls()
    {
        void Join(GodotObject? control, StringName signal, StringName method)
        {
            var callable = new Callable(this, method);
            if (control is not null && !control.IsConnected(signal, callable))
                control.Connect(signal, callable);
        }

        Join(Alphas, ItemList.SignalName.ItemSelected, MethodName.ShapeItemPicked);
        Join(_dock?.GetNodeOrNull<Button>("PaintOver/Header/Clear"), BaseButton.SignalName.Pressed, MethodName.ClearPaintOver);
        Join(ShapeRandom, BaseButton.SignalName.Toggled, MethodName.ShapeToggled);
        foreach (var path in new[] { "Settings/Rotation", "Settings/Scale", "Noise/Features", "Noise/Octaves", "Noise/Seed" })
            Join(ShapeSpin(path), Godot.Range.SignalName.ValueChanged, MethodName.ShapeValueChanged);
        Join(EditorInterface.Singleton.GetResourceFilesystem(), EditorFileSystem.SignalName.FilesystemChanged, MethodName.ShowShape);
    }

    /// <summary>The shape picked in the dock, made the first time it is used. Null for none.</summary>
    private BrushAlpha? PickedAlpha()
    {
        var shape = PickedShape;
        if (shape == "")
            return null;
        if (shape.StartsWith(NoisePrefix, StringComparison.Ordinal))
        {
            var name = shape[NoisePrefix.Length..];
            var (features, octaves, seed) = ((float)ShapeSpin("Noise/Features")!.Value, (int)ShapeSpin("Noise/Octaves")!.Value, (int)ShapeSpin("Noise/Seed")!.Value);
            var key = $"{name}/{features}/{octaves}/{seed}";
            if (!_alphas.TryGetValue(key, out var noise))
                _alphas[key] = noise = BrushAlpha.FromNoise(BrushAlpha.Noises.First(n => n.Name == name).Type, features, octaves, seed);
            return noise;
        }

        // Images are made anew, in case the file has changed.
        return GD.Load<Texture2D>(shape)?.GetImage() is { } image ? BrushAlpha.FromImage(image) : null;
    }

    private ItemList? PaintOverList => _dock?.GetNodeOrNull<ItemList>("PaintOver/List");

    private void ClearPaintOver() => PaintOverList?.DeselectAll();

    /// <summary>How much Paint may paint a sample, by the textures it paints over: going by what the sample was when the stroke first touched it, as the paint itself changes what it shows.</summary>
    private float PaintOverShare(int x, int z, uint control)
    {
        if (_paintOver.Count == 0)
            return 1f;
        if (_paintOverShare.TryGetValue((x, z), out var share))
            return share;
        var spacing = _terrain!.VertexSpacing;
        return _paintOverShare[(x, z)] = _terrain.ShareOf(new Vector3(x * spacing, 0f, z * spacing), control, _paintOver);
    }

    /// <summary>Sets up the shape for a stroke starting now.</summary>
    private void StartStamp()
    {
        _paintOver.Clear();
        _paintOverShare.Clear();
        if (PaintOverList is { } over)
            _paintOver.AddRange(over.GetSelectedItems());
        _stamp = TakesShape(Current) ? PickedAlpha() : null;
        _stampScale = (float)(ShapeSpin("Settings/Scale")?.Value ?? 1d);
        _stampAngle = ShapeRandom?.ButtonPressed == true ? GD.Randf() * MathF.Tau : Mathf.DegToRad((float)(ShapeSpin("Settings/Rotation")?.Value ?? 0d));
    }

    /// <summary>How much a shape works at a point, given as where it is across the brush (-1 to 1 each way from its middle).</summary>
    internal static float StampAt(BrushAlpha stamp, Vector2 across, float angle, float scale)
    {
        var turned = across.Rotated(-angle) / scale;
        return stamp.Sample(turned.X * 0.5f + 0.5f, turned.Y * 0.5f + 0.5f);
    }

    public override bool _Handles(GodotObject @object) => @object is TerrainNode;

    public override void _Edit(GodotObject? @object)
    {
        if (_stroking)
            EndStroke();
        _terrain = @object as TerrainNode;
        ShowTextures();
    }

    public override void _MakeVisible(bool visible)
    {
        PluginDocks.Show(this, _dock, visible);
        // Here, not on entering the tree, which a rebuild of the scripts does not repeat.
        var pressed = new Callable(this, MethodName.ImportPressed);
        if (_dock?.GetNodeOrNull<Button>("ImportHeightmap") is { } import && !import.IsConnected(BaseButton.SignalName.Pressed, pressed))
            import.Connect(BaseButton.SignalName.Pressed, pressed);
        ConnectShapeControls();
        ShowShape();
        if (_toolbar is not null)
            _toolbar.Visible = visible;
        if (!visible)
        {
            _brushAt = null;
            UpdateOverlays();
        }
    }

    /// <summary>The terrain's textures in the dock, each with its albedo as its icon.</summary>
    private void ShowTextures()
    {
        if (_textures is null)
            return;
        var picked = _textures.GetSelectedItems().FirstOrDefault(-1);
        _textures.Clear();
        var over = PaintOverList;
        var overPicked = over?.GetSelectedItems() ?? [];
        over?.Clear();
        var list = _terrain?.Materials?.Materials;
        if (_hint is not null)
            _hint.Text = _terrain is null ? "Select a TerrainNode to paint with its textures."
                : list is not { Count: > 0 } ? "Give the TerrainNode a texture list (Textures) to paint with."
                : "Pick the texture to paint:";
        if (list is null)
            return;
        foreach (var texture in list)
        {
            var index = _textures.AddItem(texture?.Name ?? "", texture?.UsedAlbedo);
            _textures.SetItemTooltip(index, $"{texture?.Name} (id {index})");
            over?.AddItem(texture?.Name ?? "", texture?.UsedAlbedo);
        }

        foreach (var index in overPicked)
        {
            if (index < over!.ItemCount)
                over.Select(index, false);
        }

        _textures.Select(picked >= 0 && picked < _textures.ItemCount ? picked : 0);
    }

    /// <summary>
    /// The brush pressed in the toolbar: looked up there each time, as the editor keeps the toolbar
    /// but not a list of its buttons when it reloads the scripts.
    /// </summary>
    private Brush Current =>
        _toolbar is null ? Brush.None
            : Enum.GetValues<Brush>().FirstOrDefault(b => b != Brush.None && _toolbar.GetNodeOrNull<Button>(b.ToString())?.ButtonPressed == true);

    private bool Active => Editing && _terrain!.Data is not null && Current != Brush.None;

    public override int _Forward3DGuiInput(Camera3D camera, InputEvent @event)
    {
        if (!Active || _terrain is null)
            return (int)AfterGuiInput.Pass;
        _camera = camera;
        switch (@event)
        {
            case InputEventMouseMotion motion:
                _mouse = motion.Position;
                _brushAt = Ground(camera, motion.Position);
                UpdateOverlays();
                return _stroking ? (int)AfterGuiInput.Stop : (int)AfterGuiInput.Pass;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left } click:
                if (click.Pressed)
                {
                    _mouse = click.Position;
                    if (Ground(camera, click.Position) is not { } start)
                        return (int)AfterGuiInput.Pass;

                    // Ctrl-click with Set Height takes the ground's height there.
                    if (Current == Brush.SetHeight && click.CtrlPressed)
                    {
                        _height!.Value = Math.Round(start.Y, 1);
                        return (int)AfterGuiInput.Stop;
                    }

                    _brushAt = start;
                    _stroking = true;
                    _inverted = click.CtrlPressed;
                    _flattenTo = start.Y;
                    _lastTick = Godot.Time.GetTicksMsec();
                    _heightsBefore.Clear();
                    _controlBefore.Clear();
                    _strokeWork = 0f;
                    StartStamp();
                    Work(1f / 30f);
                    return (int)AfterGuiInput.Stop;
                }

                if (_stroking)
                    EndStroke();
                return (int)AfterGuiInput.Stop;
        }

        return (int)AfterGuiInput.Pass;
    }

    // While the mouse is held, the brush keeps working where it is, by the time gone since it last did.
    public override void _Process(double delta)
    {
        if (!_stroking)
            return;
        if (!Active)
        {
            EndStroke();
            return;
        }

        var now = Godot.Time.GetTicksMsec();
        var seconds = Mathf.Min((now - _lastTick) / 1000f, 0.1f);
        _lastTick = now;
        if (_camera is not null)
            _brushAt = Ground(_camera, _mouse) ?? _brushAt;
        if (seconds > 0f)
            Work(seconds);
        UpdateOverlays();
    }

    /// <summary>The brush's work for this long, where it is.</summary>
    private void Work(float seconds)
    {
        if (_terrain is not { Data: { } data } || _brushAt is not { } at)
            return;
        var spacing = data.VertexSpacing;
        var radius = (float)_size!.Value;
        var strength = (float)_strength!.Value;
        var reach = Mathf.CeilToInt(radius / spacing);
        var middle = new Vector2(at.X, at.Z) / spacing;
        var samples = new Rect2I(Mathf.FloorToInt(middle.X) - reach, Mathf.FloorToInt(middle.Y) - reach, 2 * reach + 2, 2 * reach + 2);

        // How much the brush works at a sample: all of it inside the falloff, easing to nothing at its edge.
        var fade = (float)_falloff!.Value;
        float Falloff(int x, int z)
        {
            var distance = new Vector2(x, z).DistanceTo(middle) * spacing / radius;
            if (distance >= 1f)
                return 0f;
            if (distance <= 1f - fade)
                return 1f;
            return 0.5f + 0.5f * Mathf.Cos((distance - (1f - fade)) / fade * Mathf.Pi);
        }

        // The falloff with the brush's shape over it.
        float Shaped(int x, int z)
        {
            var falloff = Falloff(x, z);
            if (_stamp is null || falloff <= 0f)
                return falloff;
            return falloff * StampAt(_stamp, (new Vector2(x, z) - middle) * spacing / radius, _stampAngle, _stampScale);
        }

        // How far towards a target a sample moves now: by strength a second, at most all the way.
        float Towards(float falloff) => Mathf.Clamp(falloff * strength * seconds, 0f, 1f);

        var brush = Current;
        switch (brush)
        {
            case Brush.Raise or Brush.Lower:
                var sign = (brush == Brush.Raise) != _inverted ? 1f : -1f;
                EditHeights(samples, (x, z, h) => h + sign * strength * seconds * Shaped(x, z));
                break;
            case Brush.Smooth:
                // Towards the mean of the sample and its eight neighbours, as they were before this touch.
                var before = new Dictionary<(int X, int Z), float>();
                for (var z = samples.Position.Y - 1; z <= samples.End.Y; z++)
                {
                    for (var x = samples.Position.X - 1; x <= samples.End.X; x++)
                        before[(x, z)] = data.HeightOf(x, z);
                }

                EditHeights(samples, (x, z, h) =>
                {
                    var sum = 0f;
                    var count = 0;
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var n = before[(x + dx, z + dz)];
                            if (float.IsNaN(n))
                                continue;
                            sum += n;
                            count++;
                        }
                    }

                    return count == 0 ? h : Mathf.Lerp(h, sum / count, Towards(Falloff(x, z)));
                });
                break;
            case Brush.Flatten:
                EditHeights(samples, (x, z, h) => Mathf.Lerp(h, _flattenTo, Towards(Falloff(x, z))));
                break;
            case Brush.Slope:
                if (_terrain.SlopeAcross(middle, radius / spacing, (x, z, h) => _heightsBefore.GetValueOrDefault((x, z), h)) is { } slope)
                    EditHeights(samples, (x, z, h) => Mathf.Lerp(h, slope(x, z), Towards(Falloff(x, z))));
                break;
            case Brush.SetHeight:
                var height = (float)_height!.Value;
                EditHeights(samples, (x, z, h) => Mathf.Lerp(h, height, Towards(Falloff(x, z))));
                break;
            case Brush.Paint:
                var id = _textures?.GetSelectedItems().FirstOrDefault(0) ?? 0;
                var (blendStep, paintSteps) = WorkSteps(strength * seconds);
                EditControl(samples, (x, z, c) => Painted(c, id, Shaped(x, z) * PaintOverShare(x, z, c), blendStep, paintSteps));
                break;
            case Brush.Auto:
                var (_, autoSteps) = WorkSteps(strength * seconds);
                EditControl(samples, (x, z, c) => _inverted ? TakenOffAuto(c, Shaped(x, z), autoSteps) : GivenToAuto(c, Shaped(x, z), autoSteps));
                break;
            case Brush.Holes:
                EditControl(samples, (x, z, c) => Falloff(x, z) > 0.3f ? TerrainControl.With(c, TerrainControl.HoleBit, !_inverted) : c);
                break;
        }
    }

    /// <summary>
    /// What this much more of the stroke's work comes to in what control words can hold: a blend,
    /// in its steps of 1/255, and whole steps of the auto level. Counted over the stroke, as a
    /// frame's share at low strength can be less than one step.
    /// </summary>
    private (float Blend, int Auto) WorkSteps(float work)
    {
        var before = _strokeWork;
        _strokeWork += work;
        return ((Mathf.FloorToInt(_strokeWork * 255f) - Mathf.FloorToInt(before * 255f)) / 255f,
            Mathf.FloorToInt(_strokeWork * TerrainControl.FullAuto) - Mathf.FloorToInt(before * TerrainControl.FullAuto));
    }

    /// <summary>
    /// A sample painted with a texture where the brush works this much (its falloff and shape), by
    /// these steps: it comes in up to that much and no further, however long the brush is held, so the
    /// brush's edge stays soft. The auto-shader gives way by as much, with the painted texture
    /// showing in its place.
    /// </summary>
    internal static uint Painted(uint control, int id, float shape, float blendStep, int autoSteps)
    {
        if (shape <= 0f)
            return control;
        var wasAuto = TerrainControl.AutoLevel(control);
        var nowAuto = LessAuto(wasAuto, shape, autoSteps);
        if (wasAuto == TerrainControl.FullAuto && nowAuto == wasAuto)
            return control;
        var word = TerrainControl.WithAutoLevel(control, nowAuto);
        // All auto until now: what it had underneath never showed, so it is all this texture.
        if (wasAuto == TerrainControl.FullAuto)
            return TerrainControl.WithTextures(word, id, id, 0f);
        int first = TerrainControl.Base(control), second = TerrainControl.Overlay(control);
        var blend = TerrainControl.Blend(control);
        if (first == id && (second == id || blend <= 0f) || second == id && blend >= 1f)
            return TerrainControl.WithTextures(word, id, id, 0f);
        if (first == id)
        {
            (first, second, blend) = (second, id, 1f - blend);
        }
        else if (second != id)
        {
            // The texture comes in over whichever of the two shows most now.
            first = blend < 0.5f ? first : second;
            second = id;
            blend = 0f;
        }

        blend = Mathf.Max(blend, Mathf.Min(blend + blendStep, shape));
        return blend >= 1f ? TerrainControl.WithTextures(word, id, id, 0f) : TerrainControl.WithTextures(word, first, second, blend);
    }

    /// <summary>A sample handed back to the auto-shader: up to as much as the brush works there.</summary>
    internal static uint GivenToAuto(uint control, float shape, int steps)
    {
        var level = TerrainControl.AutoLevel(control);
        var most = Mathf.RoundToInt(shape * TerrainControl.FullAuto);
        return TerrainControl.WithAutoLevel(control, Mathf.Max(level, Mathf.Min(level + steps, most)));
    }

    /// <summary>A sample taken off the auto-shader: by as much as the brush works there.</summary>
    internal static uint TakenOffAuto(uint control, float shape, int steps) =>
        TerrainControl.WithAutoLevel(control, LessAuto(TerrainControl.AutoLevel(control), shape, steps));

    private static int LessAuto(int level, float shape, int steps)
    {
        if (shape <= 0f)
            return level;
        var least = Mathf.RoundToInt((1f - shape) * TerrainControl.FullAuto);
        return Mathf.Min(level, Mathf.Max(level - steps, least));
    }

    /// <summary>Changes heights, keeping what each sample was before the stroke first touched it.</summary>
    private void EditHeights(Rect2I samples, Func<int, int, float, float> change) =>
        _terrain!.EditHeights(samples, (x, z, h) =>
        {
            _heightsBefore.TryAdd((x, z), h);
            return change(x, z, h);
        });

    /// <summary>Changes control words, keeping what each sample was before the stroke first touched it.</summary>
    private void EditControl(Rect2I samples, Func<int, int, uint, uint> change) =>
        _terrain!.EditControl(samples, (x, z, c) =>
        {
            _controlBefore.TryAdd((x, z), c);
            return change(x, z, c);
        });

    /// <summary>The stroke done: one step to undo, from what the samples it touched were to what they are now.</summary>
    private void EndStroke()
    {
        _stroking = false;
        if (_terrain is not { Data: { } data })
            return;
        var undo = GetUndoRedo();
        if (_heightsBefore.Count > 0)
        {
            var before = _heightsBefore.Select(p => new Vector3(p.Key.X, p.Value, p.Key.Z)).ToArray();
            var after = _heightsBefore.Keys.Select(p => new Vector3(p.X, data.HeightOf(p.X, p.Z), p.Z)).ToArray();
            undo.CreateAction($"Terrain: {Current}");
            undo.AddDoMethod(_terrain, TerrainNode.MethodName.SetSampleHeights, after);
            undo.AddUndoMethod(_terrain, TerrainNode.MethodName.SetSampleHeights, before);
            undo.CommitAction(false);
        }

        if (_controlBefore.Count > 0)
        {
            int[] Triples(Func<(int X, int Z), uint> word) =>
                [.. _controlBefore.Keys.SelectMany(p => new[] { p.X, p.Z, unchecked((int)word(p)) })];
            var before = Triples(p => _controlBefore[p]);
            var after = Triples(p => data.ControlOf(p.X, p.Z));
            undo.CreateAction($"Terrain: {Current}");
            undo.AddDoMethod(_terrain, TerrainNode.MethodName.SetSampleControl, after);
            undo.AddUndoMethod(_terrain, TerrainNode.MethodName.SetSampleControl, before);
            undo.CommitAction(false);
        }

        var changed = _heightsBefore.Keys.Concat(_controlBefore.Keys).ToList();
        _heightsBefore.Clear();
        _controlBefore.Clear();
        if (changed.Count == 0)
            return;
        var (minX, maxX) = (changed.Min(p => p.X), changed.Max(p => p.X));
        var (minZ, maxZ) = (changed.Min(p => p.Z), changed.Max(p => p.Z));
        TerrainBrush.RaiseStrokeCompleted(this, new TerrainBrushStrokeEventArgs(_terrain, new Rect2I(minX, minZ, maxX - minX + 1, maxZ - minZ + 1)));
    }

    /// <summary>The brush: a circle on the ground round where it is, and inside it one where its falloff starts.</summary>
    public override void _Forward3DDrawOverViewport(Control overlay)
    {
        if (!Active || _brushAt is not { } centre || _camera is null || _terrain is null)
            return;
        var radius = (float)_size!.Value;
        var lowering = Current == Brush.Lower != _inverted && _stroking || Current == Brush.Holes;
        var colour = lowering ? new Color(1f, 0.45f, 0.3f) : new Color(0.5f, 0.85f, 1f);
        Circle(overlay, centre, radius, colour, 2f);
        var inner = radius * (1f - (float)_falloff!.Value);
        if (inner > 0.05f && inner < radius - 0.05f)
            Circle(overlay, centre, inner, colour with { A = 0.6f }, 1.5f);
    }

    /// <summary>A circle drawn over the ground, following its heights: the parts in front of the camera, as a big one can reach behind it.</summary>
    private void Circle(Control overlay, Vector3 centre, float radius, Color colour, float width)
    {
        var points = new List<Vector2>();
        void Draw()
        {
            if (points.Count >= 2)
                overlay.DrawPolyline([.. points], colour, width, true);
            points.Clear();
        }

        for (var i = 0; i <= 64; i++)
        {
            var angle = i * MathF.Tau / 64f;
            var at = centre + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            var ground = _terrain!.HeightAt(at);
            if (!float.IsNaN(ground))
                at.Y = ground;
            if (_camera!.IsPositionBehind(at))
                Draw();
            else
                points.Add(_camera.UnprojectPosition(at));
        }

        Draw();
    }

    /// <summary>Where the mouse is on the terrain's ground.</summary>
    private Vector3? Ground(Camera3D camera, Vector2 mouse) =>
        _terrain?.Intersect(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse));
}
#endif
