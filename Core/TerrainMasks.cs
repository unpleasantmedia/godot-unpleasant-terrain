using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// Masks: areas where the ground is neither drawn nor solid, without changing its data, and without
/// being bound to its grid as holes are. Given as convex shapes in world X and Z, each hiding the
/// ground below a height of its own, so what stands above it stays. Drawn by a texture
/// the shader reads; solid by taking the cells they touch out of the collision grid and putting
/// those cells back as a small mesh, cut to the shapes. Not saved: whatever sets one sets it each
/// time it loads.
/// </summary>
public partial class TerrainNode
{
    /// <summary>How many metres each pixel of the mask's texture covers, at best.</summary>
    private const float MaskPixel = 0.25f;

    /// <summary>How many pixels across the mask's texture may be: a wider area gets bigger pixels.</summary>
    private const int MaskMaxSize = 4096;

    /// <summary>How far past its shape a mask cuts the ground's collision, in metres.</summary>
    private const float MaskCutOverlap = 0.02f;

    private readonly Dictionary<string, (Vector2[] Shape, float Top)[]> _masks = [];
    private readonly Dictionary<Vector2I, Rid> _maskPatches = [];
    private readonly List<Rect2> _maskChanges = [];
    private ImageTexture? _maskTexture;
    private Vector3 _maskPlace = new(0f, 0f, 1f);

    /// <summary>
    /// Sets a mask by its name, in place of any by that name: convex shapes, in world X and Z, each
    /// hiding the ground below its height in tops. None takes it away.
    /// </summary>
    public void SetMask(string name, Vector2[][] shapes, float[] tops)
    {
        if (shapes.Length == 0)
        {
            RemoveMask(name);
            return;
        }

        // Each shape's own bounds: a road's tunnels can be far apart, and the ground between them is not changed.
        if (_masks.TryGetValue(name, out var old))
            _maskChanges.AddRange(old.Select(o => BoundsOf([o.Shape])));
        _masks[name] = [.. shapes.Select((shape, i) => (shape, tops[i]))];
        _maskChanges.AddRange(shapes.Select(shape => BoundsOf([shape])));
        QueueMasks();
    }

    /// <summary>Takes a mask away.</summary>
    public void RemoveMask(string name)
    {
        if (!_masks.Remove(name, out var old))
            return;
        _maskChanges.AddRange(old.Select(o => BoundsOf([o.Shape])));
        QueueMasks();
    }

    private bool _masksQueued;

    private void QueueMasks()
    {
        if (_masksQueued)
            return;
        _masksQueued = true;
        Callable.From(ApplyMasks).CallDeferred();
    }

    private static Rect2 BoundsOf(IEnumerable<Vector2[]> shapes)
    {
        var points = shapes.SelectMany(s => s).ToList();
        var bounds = new Rect2(points[0], Vector2.Zero);
        foreach (var point in points)
            bounds = bounds.Expand(point);
        return bounds;
    }

    /// <summary>Draws and makes solid what changed: each change once, however many masks were set.</summary>
    private void ApplyMasks()
    {
        _masksQueued = false;
        if (!IsInsideTree() || _data is null)
            return;
        DrawMasks();
        var tileWidth = CollisionTile * _data.VertexSpacing;
        var tiles = new HashSet<Vector2I>();
        foreach (var change in _maskChanges)
        {
            var low = ((change.Position - Vector2.One * _data.VertexSpacing) / tileWidth).Floor();
            var high = ((change.End + Vector2.One * _data.VertexSpacing) / tileWidth).Floor();
            for (var z = (int)low.Y; z <= (int)high.Y; z++)
            {
                for (var x = (int)low.X; x <= (int)high.X; x++)
                    tiles.Add(new Vector2I(x, z));
            }
        }

        _maskChanges.Clear();
        foreach (var tile in tiles)
            Collide(tile);
    }

    /// <summary>The mask's texture made again from all its shapes, and given to the shader.</summary>
    private void DrawMasks()
    {
        if (_masks.Count == 0)
        {
            _maskTexture = null;
            GiveMaskToShader();
            return;
        }

        var bounds = BoundsOf(_masks.Values.SelectMany(s => s).Select(s => s.Shape));
        var extent = Mathf.Max(bounds.Size.X, bounds.Size.Y) + 2f;
        var pixel = Mathf.Max(MaskPixel, extent / (MaskMaxSize - 4));
        var (width, height) = ((int)(bounds.Size.X / pixel) + 4, (int)(bounds.Size.Y / pixel) + 4);
        var origin = bounds.Position - Vector2.One * pixel * 2f;
        // In each pixel, whether it is masked, and the height below which: that spread a pixel past the
        // shapes, for the texture's smoothing between pixels not to lower it at their edges.
        var values = new float[width * height * 2];
        for (var i = 1; i < values.Length; i += 2)
            values[i] = float.MinValue;
        foreach (var (shape, top) in _masks.Values.SelectMany(s => s))
        {
            var box = BoundsOf([shape]);
            var (fromX, toX) = (Mathf.Max((int)((box.Position.X - origin.X) / pixel) - 1, 0), Mathf.Min((int)((box.End.X - origin.X) / pixel) + 1, width - 1));
            var (fromY, toY) = (Mathf.Max((int)((box.Position.Y - origin.Y) / pixel) - 1, 0), Mathf.Min((int)((box.End.Y - origin.Y) / pixel) + 1, height - 1));
            for (var y = fromY; y <= toY; y++)
            {
                for (var x = fromX; x <= toX; x++)
                {
                    var at = (y * width + x) * 2;
                    values[at + 1] = Mathf.Max(values[at + 1], top);
                    if (Inside(shape, origin + new Vector2(x + 0.5f, y + 0.5f) * pixel))
                        values[at] = 1f;
                }
            }
        }

        var bytes = new byte[values.Length * sizeof(float)];
        System.Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        var image = Image.CreateFromData(width, height, false, Image.Format.Rgf, bytes);
        _maskTexture = ImageTexture.CreateFromImage(image);
        // Its place: where it starts, and how many pixels' worth of metres it is across, kept square per pixel.
        _maskPlace = new Vector3(origin.X, origin.Y, pixel);
        _maskSize = new Vector2(width, height);
        GiveMaskToShader();
    }

    private Vector2 _maskSize = Vector2.One;

    private void GiveMaskToShader()
    {
        var material = Drawn.GetRid();
        RenderingServer.MaterialSetParam(material, "terrain_mask", _maskTexture is { } texture ? texture.GetRid() : default);
        RenderingServer.MaterialSetParam(material, "terrain_mask_x", _maskPlace.X);
        RenderingServer.MaterialSetParam(material, "terrain_mask_z", _maskPlace.Y);
        RenderingServer.MaterialSetParam(material, "terrain_mask_width", _maskPlace.Z * _maskSize.X);
        RenderingServer.MaterialSetParam(material, "terrain_mask_depth", _maskPlace.Z * _maskSize.Y);
    }

    private static bool Inside(Vector2[] convex, Vector2 point)
    {
        var sign = 0f;
        for (var i = 0; i < convex.Length; i++)
        {
            var (a, b) = (convex[i], convex[(i + 1) % convex.Length]);
            var side = (b - a).Cross(point - a);
            if (side == 0f)
                continue;
            if (sign != 0f && side * sign < 0f)
                return false;
            sign = side;
        }

        return true;
    }

    /// <summary>The masks' shapes that overlap a cell of samples, and are above some of its ground.</summary>
    private List<Vector2[]> ShapesOver(int x, int z, List<(Rect2 Box, Vector2[] Shape, float Top)> shapes)
    {
        var lowest = float.MaxValue;
        foreach (var (cx, cz) in new[] { (x, z), (x + 1, z), (x, z + 1), (x + 1, z + 1) })
        {
            var (region, index) = _data!.Sample(cx, cz);
            if (region is not null)
                lowest = Mathf.Min(lowest, region.HeightValues[index]);
        }

        var spacing = _data!.VertexSpacing;
        var cell = new Rect2(new Vector2(x, z) * spacing, Vector2.One * spacing);
        var corners = new[] { cell.Position, cell.Position + new Vector2(spacing, 0f), cell.End, cell.Position + new Vector2(0f, spacing) };
        return shapes.Where(s => lowest < s.Top && s.Box.Intersects(cell) && Geometry2D.IntersectPolygons(corners, s.Shape).Count > 0).Select(s => s.Shape).ToList();
    }

    /// <summary>
    /// A collision tile's heights with the masks taken out, as not a number: every sample of a cell a
    /// shape touches, which takes out each cell round those samples too. Each of those cells goes
    /// into the tile's patch, a mesh of it less the shapes, so what is solid is what is drawn.
    /// </summary>
    /// <summary>Every mask's shapes with their bounds, as they are now: for tiles worked out on a loading thread, which must not read the masks as they change.</summary>
    private List<(Rect2 Box, Vector2[] Shape, float Top)> MaskShapes() =>
        _masks.Values.SelectMany(s => s).Select(s => (Box: BoundsOf([s.Shape]), s.Shape, s.Top)).ToList();

    /// <summary>The faces of a tile's patch (null if it needs none), opening its heights where masked: reads only what it is given and the regions.</summary>
    private Vector3[]? MaskFaces(Vector2I tile, float[] heights, int across, List<(Rect2 Box, Vector2[] Shape, float Top)> all)
    {
        if (all.Count == 0)
            return null;
        var spacing = _data!.VertexSpacing;
        var (firstX, firstZ) = (tile.X * CollisionTile, tile.Y * CollisionTile);
        var area = new Rect2(new Vector2(firstX - 2, firstZ - 2) * spacing, Vector2.One * (CollisionTile + 4) * spacing);
        var shapes = all.Where(s => s.Box.Intersects(area)).ToList();
        var taken = new HashSet<(int X, int Z)>();
        if (shapes.Count > 0)
        {
            for (var z = firstZ - 1; z <= firstZ + CollisionTile; z++)
            {
                for (var x = firstX - 1; x <= firstX + CollisionTile; x++)
                {
                    if (ShapesOver(x, z, shapes).Count == 0)
                        continue;
                    taken.Add((x, z));
                    taken.Add((x + 1, z));
                    taken.Add((x, z + 1));
                    taken.Add((x + 1, z + 1));
                }
            }
        }

        var faces = new List<Vector3>();
        for (var oz = 0; oz < across; oz++)
        {
            for (var ox = 0; ox < across; ox++)
            {
                var (x, z) = (firstX + ox, firstZ + oz);
                var cellTaken = taken.Contains((x, z)) || taken.Contains((x + 1, z)) || taken.Contains((x, z + 1)) || taken.Contains((x + 1, z + 1));
                if (ox < CollisionTile && oz < CollisionTile && cellTaken)
                    CellLessShapes(x, z, shapes, faces);
                if (taken.Contains((x, z)))
                    heights[ox * across + (CollisionTile - oz)] = float.NaN;
            }
        }

        return faces.Count == 0 ? null : faces.ToArray();
    }

    /// <summary>A tile's patch given its faces, made if it has none yet; emptied if it has one and needs none now.</summary>
    private void GivePatch(Vector2I tile, Vector3[]? faces)
    {
        if (!_maskPatches.TryGetValue(tile, out var patch))
        {
            if (faces is null)
                return;
            patch = PhysicsServer3D.ConcavePolygonShapeCreate();
            _maskPatches[tile] = patch;
            PhysicsServer3D.BodyAddShape(BodyOf(RegionOfTile(tile)), patch);
        }

        PhysicsServer3D.ShapeSetData(patch, new Godot.Collections.Dictionary { ["faces"] = faces ?? [], ["backface_collision"] = true });
    }

    /// <summary>A cell of samples, as triangles less the shapes over it, added to faces. None if any of its samples is off the ground or a hole.</summary>
    private void CellLessShapes(int x, int z, List<(Rect2 Box, Vector2[] Shape, float Top)> shapes, List<Vector3> faces)
    {
        var spacing = _data!.VertexSpacing;
        Vector3? Corner(int cx, int cz)
        {
            var (region, index) = _data.Sample(cx, cz);
            return region is null || TerrainControl.IsHole(region.ControlValues[index]) ? null : new Vector3(cx * spacing, region.HeightValues[index], cz * spacing);
        }

        if (Corner(x, z) is not { } a || Corner(x + 1, z) is not { } b || Corner(x, z + 1) is not { } c || Corner(x + 1, z + 1) is not { } d)
            return;
        // A little wider, for neighbouring shapes to overlap: left to meet edge to edge, rounding leaves
        // hairline gaps, whose slivers of ground can't be seen, but are solid.
        var over = ShapesOver(x, z, shapes).SelectMany(shape => Geometry2D.OffsetPolygon(shape, MaskCutOverlap)).ToList();
        foreach (var (p, q, r) in new[] { (a, b, c), (b, d, c) })
        {
            var left = new List<Vector2[]> { new[] { Flat(p), Flat(q), Flat(r) } };
            foreach (var shape in over)
                left = left.SelectMany(piece => Geometry2D.ClipPolygons(piece, shape)).ToList();
            foreach (var piece in left)
            {
                var indices = Geometry2D.TriangulatePolygon(piece);
                foreach (var index in indices)
                    faces.Add(OnPlane(p, q, r, piece[index]));
            }
        }
    }

    private static Vector2 Flat(Vector3 at) => new(at.X, at.Z);

    /// <summary>A place across a triangle, at the triangle's height there.</summary>
    private static Vector3 OnPlane(Vector3 p, Vector3 q, Vector3 r, Vector2 at)
    {
        var normal = (q - p).Cross(r - p);
        var y = Mathf.IsZeroApprox(normal.Y) ? p.Y : p.Y - (normal.X * (at.X - p.X) + normal.Z * (at.Y - p.Z)) / normal.Y;
        return new Vector3(at.X, y, at.Y);
    }

    private void FreeMaskPatches()
    {
        foreach (var patch in _maskPatches.Values)
            PhysicsServer3D.FreeRid(patch);
        _maskPatches.Clear();
    }
}
