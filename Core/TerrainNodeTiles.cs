using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

// Making the ground from a heightmap in separate files.
public partial class TerrainNode
{
    /// <summary>What is wrong with importing tiles with these options, or nothing. Quick: it reads one tile, and importing checks the rest before changing anything.</summary>
    public string CheckHeightmapTiles(string[] files, int columns, float tileWidth, float low, float high, int rawWidth, int rawHeight, bool rawBigEndian, bool sharedEdges)
    {
        if (high <= low)
            return "Max Height must be above Min Height.";
        var first = files.FirstOrDefault(f => f.Length > 0);
        if (first is null || columns < 1)
            return "There are no tiles in the grid.";
        try
        {
            var map = HeightmapFile.Read(first, rawWidth, rawHeight, rawBigEndian, true);
            var data = _data ?? new TerrainData();
            var limit = RegionMapSize * data.RegionSize * data.VertexSpacing;
            var rows = files.Length / columns;
            var tileDepth = tileWidth * DepthPerWidth(map.Width, map.Height, sharedEdges);
            var total = System.Math.Max(columns * tileWidth, rows * tileDepth);
            if (total <= limit)
                return "";
            // Tiles of sizes like 1025 and 2049 are nearly always made to share their edges.
            var hint = !sharedEdges && map.Width % 2 == 1 ? $" {map.Width} pixel tiles usually share their edges: try Shared Edges." : "";
            return $"Not imported: the map would be {total} m across, and is limited to {limit} m with these region settings.{hint}";
        }
        catch (InvalidDataException problem)
        {
            return problem.Message;
        }
    }

    /// <summary>How tiles meet, in words: for choosing between shared and separate edges, and for finding one in the wrong place. Reads them all.</summary>
    public string DescribeSeams(string[] files, int columns, float low, float high, int rawWidth, int rawHeight, bool rawBigEndian, bool sharedEdges)
    {
        try
        {
            var set = new HeightmapTileSet(files, columns, rawWidth, rawHeight, rawBigEndian, sharedEdges);
            set.Scan();
            var (mean, largest, inside, repeats) = set.Seams();
            var range = high - low;
            var numbers = $"Across seams: mean step {mean * range:0.###} m, largest {largest * range:0.###} m. Just inside tiles: mean {inside * range:0.###} m.";
            if (repeats)
                return $"{numbers} Edge pixels repeat: use Shared Edges.";
            return mean > inside * 5f && inside > 0f
                ? $"{numbers} Steps this large suggest a tile in the wrong place, or tiles flipped."
                : $"{numbers} Edge pixels do not repeat: use Separate Edges.";
        }
        catch (InvalidDataException problem)
        {
            return problem.Message;
        }
    }

    private static float DepthPerWidth(int width, int height, bool sharedEdges) =>
        sharedEdges ? (height - 1f) / (width - 1f) : height / (float)width;

    /// <summary>
    /// Makes its ground from a grid of heightmap files (row by row, with "" where there is none), centred on
    /// its origin, each tile tileWidth metres across, black at low and white at high: the regions it needs
    /// added, the rest removed if replacing. Heights only: existing regions keep their painting. A tile that
    /// will not do leaves everything as it was.
    /// </summary>
    public void ImportHeightmapTiles(string[] files, int columns, float tileWidth, float low, float high, bool replace, int rawWidth, int rawHeight, bool rawBigEndian, bool sharedEdges)
    {
        var set = new HeightmapTileSet(files, columns, rawWidth, rawHeight, rawBigEndian, sharedEdges);
        try
        {
            set.Scan();
        }
        catch (InvalidDataException problem)
        {
            GD.PushError($"{Name}: {problem.Message}");
            return;
        }

        EnsureData();
        var data = _data!;
        var spacing = data.VertexSpacing;
        // With shared edges, tiles span one pixel fewer each than they have, and the grid's far edge adds it back.
        var extra = sharedEdges ? 1 : 0;
        var across = Mathf.Max(Mathf.RoundToInt(columns * tileWidth / spacing) + extra, 2);
        var down = Mathf.Max(Mathf.RoundToInt(set.Rows * tileWidth * DepthPerWidth(set.TileWidth, set.TileHeight, sharedEdges) / spacing) + extra, 2);
        // The most samples its regions hold: a full map with shared edges has one more, and is fitted to them.
        var most = RegionMapSize * data.RegionSize;
        (across, down) = (Mathf.Min(across, most), Mathf.Min(down, most));
        var (startX, startZ) = (-across / 2, -down / 2);
        if (replace)
        {
            foreach (var old in data.Locations.ToList())
                data.RemoveRegion(old);
        }

        var size = data.RegionSize;
        var (toPixelX, toPixelY) = (set.LastX / (float)(across - 1), set.LastY / (float)(down - 1));

        // The samples a tile covers: from where its first pixel falls to just before the next tile's.
        (int From, int To) Span(int tile, int tiles, int step, float toPixel, int samples) =>
            (Mathf.CeilToInt(tile * step / toPixel), tile == tiles - 1 ? samples - 1 : Mathf.Min(Mathf.CeilToInt((tile + 1) * step / toPixel) - 1, samples - 1));

        var regions = new Dictionary<Vector2I, TerrainRegion>();
        // Each region's array got here, once: it is made when first asked for, and threads asking at once would each make their own.
        var arrays = new Dictionary<Vector2I, float[]>();
        var written = new Dictionary<Vector2I, bool[]>();
        void Need(Vector2I location)
        {
            if (regions.ContainsKey(location))
                return;
            var added = data.AddRegion(location);
            regions[location] = data.Region(location)!;
            arrays[location] = regions[location].HeightValues;
            // Only a new region has samples to fill past the map's edge.
            if (added)
                written[location] = new bool[size * size];
        }

        for (var row = 0; row < set.Rows; row++)
        {
            var (fromZ, toZ) = Span(row, set.Rows, set.StepY, toPixelY, down);
            for (var column = 0; column < columns; column++)
            {
                if (!set.Has(column, row))
                    continue;
                var (fromX, toX) = Span(column, columns, set.StepX, toPixelX, across);
                for (var rz = FloorDiv(startZ + fromZ, size); rz <= FloorDiv(startZ + toZ, size); rz++)
                {
                    for (var rx = FloorDiv(startX + fromX, size); rx <= FloorDiv(startX + toX, size); rx++)
                        Need(new Vector2I(rx, rz));
                }

                set.Load([(column, row), (column + 1, row), (column, row + 1), (column + 1, row + 1)]);
                Parallel.For(fromZ, toZ + 1, sz =>
                {
                    var z = startZ + sz;
                    var (rz, oz) = (FloorDiv(z, size), z - FloorDiv(z, size) * size);
                    for (var sx = fromX; sx <= toX; sx++)
                    {
                        var x = startX + sx;
                        var location = new Vector2I(FloorDiv(x, size), rz);
                        var index = oz * size + (x - location.X * size);
                        arrays[location][index] = Mathf.Lerp(low, high, set.Blended(sx * toPixelX, sz * toPixelY));
                        if (written.TryGetValue(location, out var done))
                            done[index] = true;
                    }
                });
            }

            set.ForgetAbove(row + 1);
        }

        // A new region's samples past the map's edge take the edge's height, so it has no cliff there; where a tile is missing inside the map, the lowest height.
        foreach (var (location, done) in written)
        {
            var heights = arrays[location];
            for (var oz = 0; oz < size; oz++)
            {
                for (var ox = 0; ox < size; ox++)
                {
                    if (done[oz * size + ox])
                        continue;
                    var (x, z) = (location.X * size + ox, location.Y * size + oz);
                    var (edgeX, edgeZ) = (Mathf.Clamp(x, startX, startX + across - 1), Mathf.Clamp(z, startZ, startZ + down - 1));
                    var edge = (edgeX, edgeZ) == (x, z) ? float.NaN : data.HeightOf(edgeX, edgeZ);
                    heights[oz * size + ox] = float.IsNaN(edge) ? low : edge;
                }
            }
        }

        foreach (var region in regions.Values)
            region.Commit(true, false);
        Redo();
        EmitSignal(SignalName.GroundChanged, new Rect2(startX * spacing, startZ * spacing, across * spacing, down * spacing));
    }
}
