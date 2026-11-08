using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// In the game, with a <see cref="ViewDistance"/> set: only the regions in reach of the camera are
/// on the GPU and solid. The texture arrays hold as many layers as can be in reach at once; a region
/// coming in reach takes a free layer and has its tiles made solid, one going out of reach gives its
/// layer back and its tiles are let go. A region's maps and tiles are worked out on a thread of its
/// own; once done, the main thread gives them to the GPU and to physics, one region a frame. Out of reach the shader finds no region there, and draws
/// nothing. Its heights stay readable everywhere (<see cref="HeightAt"/> and the like): only what is
/// drawn and solid goes. In the editor, all of it always.
/// </summary>
public partial class TerrainNode
{
    private static float _viewDistance;

    /// <summary>
    /// How far from the camera regions are drawn and solid in the game, in metres: a region is in
    /// reach when any of it is that near. 0 for all of them. Changing it sets up every terrain again.
    /// </summary>
    public static float ViewDistance
    {
        get => _viewDistance;
        set
        {
            if (Mathf.IsEqualApprox(_viewDistance, value))
                return;
            _viewDistance = value;
            foreach (var terrain in InTree.ToList())
            {
                if (GodotObject.IsInstanceValid(terrain) && !Engine.IsEditorHint())
                    terrain.Redo();
            }
        }
    }

    /// <summary>How much further than <see cref="ViewDistance"/> a region stays once in reach, so one at the edge does not come and go.</summary>
    private const float StreamMargin = 256f;

    /// <summary>How near the camera a region is, at most, to be taken in at once as it is set up (the ground under the player); those further off come in from loading threads.</summary>
    private const float AtOnce = 256f;

    /// <summary>How far the camera moves before what is in reach is looked at again, in metres.</summary>
    private const float StreamStep = 32f;

    private bool _streaming;
    private float _editorViewDistance = 2000f;

    /// <summary>
    /// How far from the editor's camera regions are drawn (and solid, with EditorCollision), in metres;
    /// 0 for all of them. Those further off are kept to be edited all the same, only not on the GPU.
    /// </summary>
    [Export(PropertyHint.Range, "0,8000,250,suffix:m")]
    public float EditorViewDistance
    {
        get => _editorViewDistance;
        set
        {
            if (Mathf.IsEqualApprox(_editorViewDistance, value))
                return;
            _editorViewDistance = Mathf.Max(0f, value);
            if (Engine.IsEditorHint())
                Redo();
        }
    }

    /// <summary>
    /// How far regions are kept in reach here: in the editor, its own view distance; in the game, near
    /// (the overview drawing those further off), or with no overview, the game's view distance.
    /// </summary>
    private float StreamDistance => Engine.IsEditorHint() ? _editorViewDistance : _overview ? NearDistance : ViewDistance;
    private readonly Stack<int> _freeSlots = new();
    private Vector3? _streamedAt;
    private int[] _map = [];
    private Vector2[] _locations = [];
    private Texture2DArray? _colorMaps;

    /// <summary>The most regions that can be within a distance of one place at once.</summary>
    private int SlotsFor(float distance)
    {
        var across = Mathf.CeilToInt(2f * distance / _data!.RegionWidth) + 1;
        return across * across;
    }

    /// <summary>A region worked out on a loading thread, ready to be given to the GPU and physics.</summary>
    private sealed record RegionMade(Vector2I Location, (Image[] Weights, Image Flags)? Baked, List<TileMade> Tiles);

    /// <summary>Regions being worked out on loading threads. Let go of when it is set up again: those still running finish unused.</summary>
    private readonly Dictionary<Vector2I, Task<RegionMade>> _pending = new();

    private void StreamIfMoved(Vector3 at)
    {
        GiveFinished(at);
        if (_streamedAt is { } was && Flat(was).DistanceTo(Flat(at)) < StreamStep)
            return;
        // Nothing in reach yet (no camera as it was set up): at once, for there to be ground under the player.
        StreamAround(at, now: _layers.Count == 0);
    }

    /// <summary>How far a place is from a region, flat: 0 inside it.</summary>
    private float DistanceTo(Vector2I region, Vector2 at)
    {
        var width = _data!.RegionWidth;
        var low = new Vector2(region.X, region.Y) * width;
        var nearest = at.Clamp(low, low + Vector2.One * width);
        return nearest.DistanceTo(at);
    }

    /// <summary>
    /// Lets go of the regions now out of reach and takes in those come in reach, nearest first: at
    /// once (as it is set up, for there to be ground under the player from the start), or worked out
    /// on loading threads, to be given over as they are done (see <see cref="GiveFinished"/>).
    /// </summary>
    private void StreamAround(Vector3 camera, bool now = false)
    {
        if (_data is null)
            return;
        _streamedAt = camera;
        var at = Flat(camera);
        var changed = false;
        foreach (var location in _layers.Keys.ToList())
        {
            if (DistanceTo(location, at) > StreamDistance + StreamMargin)
            {
                Unload(location);
                changed = true;
            }
        }

        var wanted = _data.Locations
            .Where(l => !_layers.ContainsKey(l) && !_pending.ContainsKey(l) && DistanceTo(l, at) <= StreamDistance)
            .OrderBy(l => DistanceTo(l, at));
        var masks = MaskShapes();
        foreach (var location in wanted)
        {
            if (_freeSlots.Count <= _pending.Count)
                break;
            if (!Ready(location))
                continue;
            if (now && DistanceTo(location, at) <= AtOnce)
            {
                Give(Make(location, masks));
                changed = true;
            }
            else
            {
                _pending[location] = Task.Run(() => Make(location, masks));
            }
        }

        if (changed)
            GiveMapToShader();
    }

    /// <summary>
    /// Whether a region can be taken in: on the region map, with its maps. Looks it and the regions
    /// its tiles reach into up, and reads their values, here on the main thread, so a loading thread
    /// only ever finds them.
    /// </summary>
    private bool Ready(Vector2I location)
    {
        if (_data!.Region(location) is not { } region || region.Heights is null || region.Control is null || region.Colors is null)
            return false;
        var at = location + new Vector2I(RegionMapSize / 2, RegionMapSize / 2);
        if (at.X < 0 || at.Y < 0 || at.X >= RegionMapSize || at.Y >= RegionMapSize)
            return false;
        for (var z = -1; z <= 1; z++)
        {
            for (var x = -1; x <= 1; x++)
            {
                if (_data.Region(location + new Vector2I(x, z)) is { } near)
                {
                    _ = near.HeightValues;
                    _ = near.ControlValues;
                }
            }
        }

        return true;
    }

    /// <summary>A region's baked maps and collision tiles, worked out: safe on a loading thread, once <see cref="Ready"/>.</summary>
    private RegionMade Make(Vector2I location, List<(Rect2 Box, Vector2[] Shape, float Top)> masks)
    {
        var region = _data!.Region(location)!;
        var baked = _baked ? TerrainBake.Bake(region.ControlValues, _data.RegionSize, _slots) : ((Image[], Image)?)null;
        var tiles = _collides ? TilesOf(location).Select(t => TileShape(t, masks)).OfType<TileMade>().ToList() : [];
        return new RegionMade(location, baked, tiles);
    }

    /// <summary>A region worked out, given a free layer and its body: on the main thread.</summary>
    private void Give(RegionMade made)
    {
        var location = made.Location;
        var region = _data!.Region(location)!;
        var at = location + new Vector2I(RegionMapSize / 2, RegionMapSize / 2);
        var slot = _freeSlots.Pop();
        _layers[location] = slot;
        _heightMaps?.UpdateLayer(region.Heights, slot);
        _controlMaps?.UpdateLayer(region.Control, slot);
        _colorMaps?.UpdateLayer(region.Colors, slot);
        if (made.Baked is { } baked)
            GiveBakedMaps(baked, slot);
        _map[at.Y * RegionMapSize + at.X] = slot + 1;
        _locations[slot] = location;

        if (_collides && GetWorld3D() is { } world)
        {
            foreach (var tile in made.Tiles)
                GiveTile(tile);
            // Its tiles made before its body is in the world: each shape added to a body in it rebuilds the body.
            if (_bodies.TryGetValue(location, out var body))
                PhysicsServer3D.BodySetSpace(body, world.Space);
        }
    }

    /// <summary>The nearest region done on a loading thread, given over if still in reach: one a frame, so none takes long.</summary>
    private void GiveFinished(Vector3 camera)
    {
        var done = _pending.Where(p => p.Value.IsCompleted).Select(p => p.Key).OrderBy(l => DistanceTo(l, Flat(camera))).ToList();
        if (done.Count == 0)
            return;
        var location = done[0];
        var task = _pending[location];
        _pending.Remove(location);
        if (task.IsFaulted)
        {
            GD.PushError($"Terrain: could not load region {location}: {task.Exception?.InnerException?.Message}");
            return;
        }

        if (DistanceTo(location, Flat(camera)) > StreamDistance + StreamMargin || _freeSlots.Count == 0)
            return;
        Give(task.Result);
        GiveMapToShader();
    }

    private void Unload(Vector2I location)
    {
        var slot = _layers[location];
        _layers.Remove(location);
        _freeSlots.Push(slot);
        var at = location + new Vector2I(RegionMapSize / 2, RegionMapSize / 2);
        _map[at.Y * RegionMapSize + at.X] = 0;

        foreach (var tile in TilesOf(location))
        {
            if (_shapes.Remove(tile, out var shape))
                PhysicsServer3D.FreeRid(shape);
            if (_maskPatches.Remove(tile, out var patch))
                PhysicsServer3D.FreeRid(patch);
        }

        if (_bodies.Remove(location, out var body))
            PhysicsServer3D.FreeRid(body);
    }

    private IEnumerable<Vector2I> TilesOf(Vector2I location)
    {
        var tiles = _data!.RegionSize / CollisionTile;
        for (var z = 0; z < tiles; z++)
        {
            for (var x = 0; x < tiles; x++)
                yield return location * tiles + new Vector2I(x, z);
        }
    }

    /// <summary>Tells the shader which regions are where now.</summary>
    private void GiveMapToShader()
    {
        var material = Drawn.GetRid();
        SetParam(material, "_region_map", _map);
        SetParam(material, "_region_locations", _locations);
        if (_regionLookup is null)
            return;
        var bytes = new byte[_map.Length * sizeof(float)];
        System.Buffer.BlockCopy(_map.Select(m => (float)m).ToArray(), 0, bytes, 0, bytes.Length);
        _regionLookup.Update(Image.CreateFromData(RegionMapSize, RegionMapSize, false, Image.Format.Rf, bytes));
    }
}
