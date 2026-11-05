using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// A heightmap in separate files laid out on a grid: all one size, read a few at a time as they are needed.
/// Pixels are numbered across the whole grid. With shared edges, a tile's last row and column of pixels
/// repeat as its neighbour's first; otherwise tiles simply butt together.
/// </summary>
public sealed class HeightmapTileSet(string[] files, int columns, int rawWidth, int rawHeight, bool rawBigEndian, bool sharedEdges)
{
    private readonly Dictionary<(int Column, int Row), HeightmapFile> _loaded = [];
    private float _lowest;
    private float _range = 1f;

    public int Columns { get; } = columns;

    public int Rows { get; } = columns > 0 ? files.Length / columns : 0;

    public int TileWidth { get; private set; }

    public int TileHeight { get; private set; }

    /// <summary>How many pixels on the next tile starts, across and down.</summary>
    public int StepX => sharedEdges ? TileWidth - 1 : TileWidth;

    public int StepY => sharedEdges ? TileHeight - 1 : TileHeight;

    /// <summary>The last pixel across and down the whole grid.</summary>
    public int LastX => Columns * StepX - (sharedEdges ? 0 : 1);

    public int LastY => Rows * StepY - (sharedEdges ? 0 : 1);

    public bool Has(int column, int row) =>
        column >= 0 && row >= 0 && column < Columns && row < Rows && files[row * Columns + column].Length > 0;

    /// <summary>
    /// Reads every tile once: their common size, and the range floating-point ones are scaled to, which has
    /// to be the same for all of them or they would not meet. Throws an <see cref="InvalidDataException"/>, with a message to show, if any will not do.
    /// </summary>
    public void Scan()
    {
        var (low, high) = (float.MaxValue, float.MinValue);
        TileWidth = TileHeight = 0;
        foreach (var path in files.Where(f => f.Length > 0))
        {
            var tile = Read(path);
            if (TileWidth == 0)
                (TileWidth, TileHeight) = (tile.Width, tile.Height);
            else if (tile.Width != TileWidth || tile.Height != TileHeight)
                throw new InvalidDataException($"{Path.GetFileName(path)} is {tile.Width} x {tile.Height}, but the first tile is {TileWidth} x {TileHeight}: all must be one size.");
            if (!tile.Floating)
                continue;
            foreach (var value in tile.Values.Where(float.IsFinite))
                (low, high) = (Math.Min(low, value), Math.Max(high, value));
        }

        if (TileWidth == 0)
            throw new InvalidDataException("There are no tiles in the grid.");
        if (TileWidth < 2 || TileHeight < 2)
            throw new InvalidDataException("Tiles must be at least 2 pixels each way.");
        if (high >= low)
            (_lowest, _range) = (low, high > low ? high - low : 1f);
    }

    /// <summary>Has these tiles in memory for <see cref="Pixel"/>, safely read from several threads after this.</summary>
    public void Load(IEnumerable<(int Column, int Row)> tiles)
    {
        foreach (var at in tiles)
        {
            if (Has(at.Column, at.Row) && !_loaded.ContainsKey(at))
                _loaded[at] = Read(files[at.Row * Columns + at.Column]);
        }
    }

    /// <summary>Lets go of the tiles above a row, to read a big grid without holding all of it.</summary>
    public void ForgetAbove(int row)
    {
        foreach (var at in _loaded.Keys.Where(k => k.Row < row).ToList())
            _loaded.Remove(at);
    }

    /// <summary>A pixel of the grid, 0 to 1; NaN where its tile is not there or not loaded.</summary>
    public float Pixel(int x, int y)
    {
        var (column, row) = (Math.Min(x / StepX, Columns - 1), Math.Min(y / StepY, Rows - 1));
        if (!_loaded.TryGetValue((column, row), out var tile))
            return float.NaN;
        var value = tile.Values[(y - row * StepY) * TileWidth + (x - column * StepX)];
        return !tile.Floating ? value : float.IsFinite(value) ? (value - _lowest) / _range : 0f;
    }

    /// <summary>A place on the grid in pixels, blended from the four round it, where those are there.</summary>
    public float Blended(float x, float y)
    {
        var (x0, y0) = (Math.Min((int)x, LastX), Math.Min((int)y, LastY));
        var (x1, y1) = (Math.Min(x0 + 1, LastX), Math.Min(y0 + 1, LastY));
        var (fx, fy) = (x - x0, y - y0);
        var a = Pixel(x0, y0);
        float Or(float value, float other) => float.IsNaN(value) ? other : value;
        var b = Or(Pixel(x1, y0), a);
        var c = Or(Pixel(x0, y1), a);
        var d = Or(Pixel(x1, y1), Or(b, c));
        return (a * (1f - fx) + b * fx) * (1f - fy) + (c * (1f - fx) + d * fx) * fy;
    }

    /// <summary>
    /// How tiles meet, from reading them all (after <see cref="Scan"/>): the mean and largest height steps
    /// across their seams and the mean step just inside them, each 0 to 1, and whether the pixels at their
    /// edges repeat, as they do when they share them.
    /// </summary>
    public (float SeamMean, float SeamLargest, float InsideMean, bool Repeats) Seams()
    {
        var (seam, largest, inside, count) = (0.0, 0f, 0.0, 0L);
        for (var row = 0; row < Rows; row++)
        {
            Load(Enumerable.Range(0, Columns).SelectMany(c => new[] { (c, row), (c, row + 1) }));
            for (var column = 0; column < Columns; column++)
            {
                if (!Has(column, row))
                    continue;
                var a = _loaded[(column, row)];
                void Compare(HeightmapFile b, bool sideways)
                {
                    for (var i = 0; i < (sideways ? TileHeight : TileWidth); i++)
                    {
                        var (end, before, next) = sideways
                            ? (Scaled(a, i, TileWidth - 1), Scaled(a, i, TileWidth - 2), Scaled(b, i, 0))
                            : (Scaled(a, TileHeight - 1, i), Scaled(a, TileHeight - 2, i), Scaled(b, 0, i));
                        var step = MathF.Abs(end - next);
                        (seam, inside, count) = (seam + step, inside + MathF.Abs(end - before), count + 1);
                        largest = MathF.Max(largest, step);
                    }
                }

                if (Has(column + 1, row))
                    Compare(_loaded[(column + 1, row)], true);
                if (Has(column, row + 1))
                    Compare(_loaded[(column, row + 1)], false);
            }

            ForgetAbove(row + 1);
        }

        _loaded.Clear();
        if (count == 0)
            return (0f, 0f, 0f, false);
        var (mean, within) = ((float)(seam / count), (float)(inside / count));
        return (mean, largest, within, mean <= within * 0.1f);
    }

    private float Scaled(HeightmapFile tile, int y, int x)
    {
        var value = tile.Values[y * TileWidth + x];
        return !tile.Floating ? value : float.IsFinite(value) ? (value - _lowest) / _range : 0f;
    }

    private HeightmapFile Read(string path) => HeightmapFile.Read(path, rawWidth, rawHeight, rawBigEndian, true);

    private static readonly (Regex Pattern, bool RowFirst)[] NamePatterns =
    [
        (new Regex(@"x[^0-9a-z]*(\d+)[^0-9a-z]*y[^0-9a-z]*(\d+)", RegexOptions.IgnoreCase), false),
        (new Regex(@"y[^0-9a-z]*(\d+)[^0-9a-z]*x[^0-9a-z]*(\d+)", RegexOptions.IgnoreCase), true),
        (new Regex(@"r(?:ow)?[^0-9a-z]*(\d+)[^0-9a-z]*c(?:ol(?:umn)?)?[^0-9a-z]*(\d+)", RegexOptions.IgnoreCase), true),
        (new Regex(@"c(?:ol(?:umn)?)?[^0-9a-z]*(\d+)[^0-9a-z]*r(?:ow)?[^0-9a-z]*(\d+)", RegexOptions.IgnoreCase), false),
        (new Regex(@"(\d+)[^0-9a-z]+(\d+)$"), false),
    ];

    /// <summary>The column and row a file's name gives, as Map_x0_y1, tile_x000_y001, Map_y1_x0, Map_r1_c0 or Map_0_1; none if it gives neither.</summary>
    public static (int Column, int Row)? PlaceFromName(string fileNameWithoutExtension)
    {
        foreach (var (pattern, rowFirst) in NamePatterns)
        {
            var match = pattern.Match(fileNameWithoutExtension);
            if (!match.Success)
                continue;
            var (first, second) = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
            return rowFirst ? (second, first) : (first, second);
        }

        return null;
    }
}
