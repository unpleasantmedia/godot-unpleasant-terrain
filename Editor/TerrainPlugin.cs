#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.Terrain.Editor;

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
    private enum Brush { None, Raise, Lower, Smooth, Flatten, SetHeight, Paint, Auto, Holes }

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

    // What the samples touched in this stroke were before it, for undo.
    private readonly Dictionary<(int X, int Z), float> _heightsBefore = [];
    private readonly Dictionary<(int X, int Z), uint> _controlBefore = [];

    public override void _EnterTree()
    {
        _toolbar = GD.Load<PackedScene>("res://addons/terrain/TerrainToolbar.tscn").Instantiate<Control>();
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

        _dock = GD.Load<PackedScene>("res://addons/terrain/TerrainDock.tscn").Instantiate<Control>();
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

        PluginDocks.Remove(this, _dock);

    }

    /// <summary>A brush button pressed or let go (the signal says which; either way the brush outline goes until the mouse moves).</summary>
    public void BrushToggled(bool pressed)
    {
        _brushAt = null;
        UpdateOverlays();
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
        var list = _terrain?.Textures?.Textures;
        if (_hint is not null)
            _hint.Text = _terrain is null ? "Select a TerrainNode to paint with its textures."
                : list is not { Count: > 0 } ? "Give the TerrainNode a texture list (Textures) to paint with."
                : "Pick the texture to paint:";
        if (list is null)
            return;
        foreach (var texture in list)
        {
            var index = _textures.AddItem(texture?.Name ?? "", texture?.Albedo);
            _textures.SetItemTooltip(index, $"{texture?.Name} (id {index})");
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

        // How far towards a target a sample moves now: by strength a second, at most all the way.
        float Towards(float falloff) => Mathf.Clamp(falloff * strength * seconds, 0f, 1f);

        var brush = Current;
        switch (brush)
        {
            case Brush.Raise or Brush.Lower:
                var sign = (brush == Brush.Raise) != _inverted ? 1f : -1f;
                EditHeights(samples, (x, z, h) => h + sign * strength * seconds * Falloff(x, z));
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
            case Brush.SetHeight:
                var height = (float)_height!.Value;
                EditHeights(samples, (x, z, h) => Mathf.Lerp(h, height, Towards(Falloff(x, z))));
                break;
            case Brush.Paint:
                var id = _textures?.GetSelectedItems().FirstOrDefault(0) ?? 0;
                EditControl(samples, (x, z, c) => Painted(c, id, Falloff(x, z) * Mathf.Clamp(strength * seconds * 4f, 0f, 1f), Falloff(x, z)));
                break;
            case Brush.Auto:
                EditControl(samples, (x, z, c) => Falloff(x, z) > 0.3f ? TerrainControl.With(c, TerrainControl.AutoBit, !_inverted) : c);
                break;
            case Brush.Holes:
                EditControl(samples, (x, z, c) => Falloff(x, z) > 0.3f ? TerrainControl.With(c, TerrainControl.HoleBit, !_inverted) : c);
                break;
        }
    }

    /// <summary>
    /// A sample painted with a texture this much: off the auto-shader, with the texture blended in
    /// over what it had (as its overlay), until it is all that texture (its base). A sample still
    /// under the auto-shader (whose textures are only known to the shader) takes the texture
    /// outright where the brush is at least half strength (by its falloff), there at once.
    /// </summary>
    private static uint Painted(uint control, int id, float amount, float falloff)
    {
        if (amount <= 0f)
            return control;
        var manual = TerrainControl.With(control, TerrainControl.AutoBit, false);
        int first = TerrainControl.Base(control), second = TerrainControl.Overlay(control);
        var blend = TerrainControl.Blend(control);
        // Off the auto-shader, or already this texture underneath: it all goes to the texture.
        if (TerrainControl.IsAuto(control) || first == id && (second == id || blend <= 0f))
            return falloff >= 0.5f || !TerrainControl.IsAuto(control) ? TerrainControl.WithTextures(manual, id, id, 0f) : control;
        if (second != id)
        {
            // The texture comes in over whichever of the two shows most now.
            first = blend < 0.5f ? first : second;
            second = id;
            blend = 0f;
        }

        blend = Mathf.Min(blend + amount, 1f);
        return blend >= 1f ? TerrainControl.WithTextures(manual, id, id, 0f) : TerrainControl.WithTextures(manual, first, second, blend);
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

    /// <summary>A circle drawn over the ground, following its heights.</summary>
    private void Circle(Control overlay, Vector3 centre, float radius, Color colour, float width)
    {
        var points = new List<Vector2>();
        for (var i = 0; i <= 64; i++)
        {
            var angle = i * MathF.Tau / 64f;
            var at = centre + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            var ground = _terrain!.HeightAt(at);
            if (!float.IsNaN(ground))
                at.Y = ground;
            if (_camera!.IsPositionBehind(at))
                return;
            points.Add(_camera.UnprojectPosition(at));
        }

        overlay.DrawPolyline([.. points], colour, width, true);
    }

    /// <summary>Where the mouse is on the terrain's ground.</summary>
    private Vector3? Ground(Camera3D camera, Vector2 mouse) =>
        _terrain?.Intersect(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse));
}
#endif
