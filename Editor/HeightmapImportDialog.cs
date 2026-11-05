#if TOOLS
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain.Editor;

/// <summary>
/// The options for making a terrain's ground from a heightmap (HeightmapImportDialog.tscn): the file, how wide
/// the ground is, the heights black and white stand for, and what becomes of regions it already has; or a grid of files
/// for a larger map, placed by their names and moved about by dragging. One step to undo.
/// </summary>
[Tool]
public partial class HeightmapImportDialog : ConfirmationDialog
{
    /// <summary>Above this many regions there is too much to keep for undoing: it warns instead.</summary>
    private const int UndoLimit = 64;

    private TerrainNode? _terrain;

    private LineEdit File => GetNode<LineEdit>("%File");
    private FileDialog Picker => GetNode<FileDialog>("%Picker");
    private Label Info => GetNode<Label>("%Info");
    private SpinBox TerrainWidth => GetNode<SpinBox>("%Width");
    private SpinBox MinHeight => GetNode<SpinBox>("%MinHeight");
    private SpinBox MaxHeight => GetNode<SpinBox>("%MaxHeight");
    private OptionButton Existing => GetNode<OptionButton>("%Existing");
    private SpinBox RawWidth => GetNode<SpinBox>("%RawWidth");
    private SpinBox RawHeight => GetNode<SpinBox>("%RawHeight");
    private OptionButton RawOrder => GetNode<OptionButton>("%RawOrder");

    private const int CellNameLength = 16;

    private const string CellScene = "res://addons/unpleasant_terrain/Editor/HeightmapTileCell.tscn";

    /// <summary>The tiles chosen, by (column, row).</summary>
    private readonly Dictionary<Vector2I, string> _tiles = [];

    /// <summary>The place a file is being chosen for.</summary>
    private int _choosingFor;

    private bool Tiled => GetNode<OptionButton>("%Source").Selected == 1;
    private bool SharedEdges => GetNode<OptionButton>("%Edges").Selected == 0;
    private int GridColumns => (int)GetNode<SpinBox>("%Columns").Value;
    private int GridRows => (int)GetNode<SpinBox>("%TileRows").Value;
    private GridContainer Grid => GetNode<GridContainer>("%TileGrid");

    public override void _Ready()
    {
        GetNode<OptionButton>("%Source").Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnSourceChosen));
        GetNode<OptionButton>("%Edges").Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnEdgesChosen));
        GetNode<Button>("%AddTiles").Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddTiles));
        GetNode<Button>("%ClearTiles").Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.ClearTiles));
        GetNode<Button>("%CheckSeams").Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnCheckSeams));
        GetNode<SpinBox>("%Columns").Connect(Godot.Range.SignalName.ValueChanged, new Callable(this, MethodName.OnGridSizeChanged));
        GetNode<SpinBox>("%TileRows").Connect(Godot.Range.SignalName.ValueChanged, new Callable(this, MethodName.OnGridSizeChanged));
        GetNode<FileDialog>("%TilesPicker").Connect(FileDialog.SignalName.FilesSelected, new Callable(this, MethodName.AddTiles));
        GetNode<FileDialog>("%TilePicker").Connect(FileDialog.SignalName.FileSelected, new Callable(this, MethodName.OnTilePicked));
        ShowSource();
        RebuildGrid();
        // By method name, not C# events: the editor can then let go of them as it reloads the scripts.
        Connect(AcceptDialog.SignalName.Confirmed, new Callable(this, MethodName.OnImport));
        GetNode<Button>("%Browse").Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnBrowse));
        Picker.Connect(FileDialog.SignalName.FileSelected, new Callable(this, MethodName.OnFilePicked));
        File.Connect(LineEdit.SignalName.TextSubmitted, new Callable(this, MethodName.OnFilePicked));
        ShowRaw(false);
    }

    /// <summary>Opens it for a terrain.</summary>
    public void Open(TerrainNode terrain)
    {
        _terrain = terrain;
        PopupCentered();
    }

    private void OnBrowse() => Picker.PopupCentered(new Vector2I(800, 500));

    private void OnSourceChosen(int source) => ShowSource();

    private void ShowSource()
    {
        foreach (var (name, tiled) in new[] { ("FileLabel", false), ("FileRow", false), ("WidthLabel", false), ("Width", false),
                     ("TilesLabel", true), ("Tiles", true), ("TileWidthLabel", true), ("TileWidth", true), ("EdgesLabel", true), ("Edges", true) })
            GetNode<Control>($"Options/{name}").Visible = tiled == Tiled;
    }

    private void OnAddTiles() => GetNode<FileDialog>("%TilesPicker").PopupCentered(new Vector2I(800, 500));

    /// <summary>Puts files on the grid: where their names say (their lowest numbers being the first column and row), the rest in the empty places. It grows to fit.</summary>
    public void AddTiles(string[] paths)
    {
        var named = paths.Select(p => (Path: p, Place: HeightmapTileSet.PlaceFromName(p.GetFile().GetBaseName()))).ToList();
        var places = named.Where(n => n.Place is not null).Select(n => n.Place!.Value).ToList();
        var (firstColumn, firstRow) = places.Count > 0 ? (places.Min(p => p.Column), places.Min(p => p.Row)) : (0, 0);
        var unnamed = new List<string>();
        foreach (var (path, place) in named)
        {
            if (place is { } at)
                _tiles[new Vector2I(at.Column - firstColumn, at.Row - firstRow)] = path;
            else
                unnamed.Add(path);
        }

        var (columns, rows) = (GridColumns, GridRows);
        if (_tiles.Count > 0)
            (columns, rows) = (Mathf.Max(columns, _tiles.Keys.Max(k => k.X) + 1), Mathf.Max(rows, _tiles.Keys.Max(k => k.Y) + 1));
        var fresh = 0;
        foreach (var path in unnamed)
        {
            // The first empty place, in another row if all are full.
            while (_tiles.ContainsKey(new Vector2I(fresh % columns, fresh / columns)))
                fresh++;
            _tiles[new Vector2I(fresh % columns, fresh / columns)] = path;
            rows = Mathf.Max(rows, fresh / columns + 1);
        }

        SetGridSize(columns, rows);
        FitToFirstTile();
    }

    private void SetGridSize(int columns, int rows)
    {
        GetNode<SpinBox>("%Columns").SetValueNoSignal(columns);
        GetNode<SpinBox>("%TileRows").SetValueNoSignal(rows);
        RebuildGrid();
    }

    private void ClearTiles()
    {
        _tiles.Clear();
        RebuildGrid();
        Info.Text = "";
    }

    // Tiles outside a smaller grid are kept, and come back if it grows again; only those on it are imported.
    private void OnGridSizeChanged(double value) => RebuildGrid();

    private void RebuildGrid()
    {
        var grid = Grid;
        foreach (var old in grid.GetChildren())
        {
            grid.RemoveChild(old);
            old.QueueFree();
        }

        grid.Columns = GridColumns;
        var scene = GD.Load<PackedScene>(CellScene);
        for (var i = 0; i < GridColumns * GridRows; i++)
        {
            var cell = scene.Instantiate<HeightmapTileCell>();
            cell.Index = i;
            var place = new Vector2I(i % GridColumns, i / GridColumns);
            // The end of its name, which is what tells tiles apart.
            cell.Text = _tiles.TryGetValue(place, out var path) ? path.GetFile().GetBaseName()[^Mathf.Min(path.GetFile().GetBaseName().Length, CellNameLength)..] : "";
            cell.TooltipText = path ?? $"Column {place.X}, row {place.Y}: empty";
            cell.Connect(HeightmapTileCell.SignalName.Swapped, new Callable(this, MethodName.SwapTiles));
            cell.Connect(HeightmapTileCell.SignalName.Cleared, new Callable(this, MethodName.OnCellCleared));
            cell.Connect(BaseButton.SignalName.Pressed, Callable.From(() => OnCellPressed(cell.Index)));
            grid.AddChild(cell);
        }
    }

    private Vector2I PlaceOf(int index) => new(index % GridColumns, index / GridColumns);

    /// <summary>Swaps what is at two places on the grid.</summary>
    public void SwapTiles(int from, int to)
    {
        var (a, b) = (PlaceOf(from), PlaceOf(to));
        var (hadA, pathA) = (_tiles.TryGetValue(a, out var first), first);
        var (hadB, pathB) = (_tiles.TryGetValue(b, out var second), second);
        _tiles.Remove(a);
        _tiles.Remove(b);
        if (hadA)
            _tiles[b] = pathA!;
        if (hadB)
            _tiles[a] = pathB!;
        CallDeferred(MethodName.RebuildGrid);
    }

    private void OnCellCleared(int index)
    {
        _tiles.Remove(PlaceOf(index));
        CallDeferred(MethodName.RebuildGrid);
    }

    private void OnCellPressed(int index)
    {
        _choosingFor = index;
        GetNode<FileDialog>("%TilePicker").PopupCentered(new Vector2I(800, 500));
    }

    private void OnTilePicked(string path)
    {
        _tiles[PlaceOf(_choosingFor)] = path;
        RebuildGrid();
        FitToFirstTile();
    }

    private void OnEdgesChosen(int edges) => FitToFirstTile();

    /// <summary>The grid's files, row by row, "" where there is none.</summary>
    public string[] GridFiles() =>
        [.. Enumerable.Range(0, GridColumns * GridRows).Select(i => _tiles.GetValueOrDefault(PlaceOf(i), ""))];

    // What the tiles are, and their width set to one sample a pixel.
    private void FitToFirstTile()
    {
        var first = GridFiles().FirstOrDefault(f => f.Length > 0);
        if (first is null)
            return;
        var raw = HeightmapFile.IsRaw(first);
        ShowRaw(raw);
        if (raw && _rawSizeOf != first)
        {
            _rawSizeOf = first;
            var side = (int)Mathf.Sqrt(Godot.FileAccess.GetFileAsBytes(first).Length / 2.0);
            RawWidth.Value = RawHeight.Value = side;
        }

        try
        {
            var map = HeightmapFile.Read(first, (int)RawWidth.Value, (int)RawHeight.Value, RawOrder.Selected == 1, true);
            var pixels = SharedEdges ? map.Width - 1 : map.Width;
            var spacing = _terrain?.VertexSpacing ?? 1f;
            GetNode<SpinBox>("%TileWidth").Value = pixels * spacing;
            Info.Text = $"{GridColumns} x {GridRows} tiles of {map.Width} x {map.Height} pixels: {GridColumns * pixels * spacing} m across.";
        }
        catch (InvalidDataException problem)
        {
            Info.Text = problem.Message;
        }
    }

    private string? _rawSizeOf;

    private void OnCheckSeams()
    {
        if (_terrain is null || !IsInstanceValid(_terrain))
            return;
        Info.Text = _terrain.DescribeSeams(GridFiles(), GridColumns, (float)MinHeight.Value, (float)MaxHeight.Value,
            (int)RawWidth.Value, (int)RawHeight.Value, RawOrder.Selected == 1, SharedEdges);
    }

    private void ShowRaw(bool raw)
    {
        foreach (var name in new[] { "RawWidthLabel", "%RawWidth", "RawHeightLabel", "%RawHeight", "RawOrderLabel", "%RawOrder" })
            GetNode<Control>(name.StartsWith('%') ? name : $"Options/{name}").Visible = raw;
    }

    // A file chosen: what it is, and the ground's width set to one sample a pixel.
    private void OnFilePicked(string path)
    {
        File.Text = path;
        var raw = HeightmapFile.IsRaw(path);
        ShowRaw(raw);
        if (raw)
        {
            // A square file's size follows from its length.
            var side = (int)Mathf.Sqrt(Godot.FileAccess.GetFileAsBytes(path).Length / 2.0);
            RawWidth.Value = RawHeight.Value = side;
            Info.Text = $"Assuming {side} x {side}: correct below if it is not square.";
            return;
        }

        try
        {
            var map = HeightmapFile.Read(path);
            Info.Text = $"{map.Width} x {map.Height} pixels";
            if (_terrain is not null)
                TerrainWidth.Value = (map.Width - 1) * _terrain.VertexSpacing;
        }
        catch (InvalidDataException problem)
        {
            Info.Text = problem.Message;
        }
    }

    private void OnImport()
    {
        if (_terrain is null || !IsInstanceValid(_terrain))
            return;
        var (path, width, low, high) = (File.Text, (float)TerrainWidth.Value, (float)MinHeight.Value, (float)MaxHeight.Value);
        var replace = Existing.Selected == 0;
        var (rawWidth, rawHeight, bigEndian) = ((int)RawWidth.Value, (int)RawHeight.Value, RawOrder.Selected == 1);
        if (Tiled)
        {
            ImportTiles(low, high, replace, rawWidth, rawHeight, bigEndian);
            return;
        }

        if (_terrain.CheckHeightmap(path, width, low, high, replace, rawWidth, rawHeight, bigEndian) is { Length: > 0 } problem)
        {
            Info.Text = problem;
            CallDeferred(Window.MethodName.PopupCentered);
            return;
        }

        var regions = _terrain.Data?.Locations.Count ?? 0;
        if (regions > UndoLimit)
        {
            GD.PushWarning($"{_terrain.Name}: heightmap imported with no undo: {regions} regions is too many to keep.");
            _terrain.ImportHeightmap(path, width, low, high, replace, rawWidth, rawHeight, bigEndian);
            return;
        }

        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        undo.CreateAction($"Import heightmap {path.GetFile()}");
        undo.AddDoMethod(_terrain, TerrainNode.MethodName.ImportHeightmap, path, width, low, high, replace, rawWidth, rawHeight, bigEndian);
        undo.AddUndoMethod(_terrain, TerrainNode.MethodName.PutRegions, _terrain.CopyRegions());
        undo.CommitAction();
    }

    private void ImportTiles(float low, float high, bool replace, int rawWidth, int rawHeight, bool bigEndian)
    {
        var (files, columns, tileWidth, shared) = (GridFiles(), GridColumns, (float)GetNode<SpinBox>("%TileWidth").Value, SharedEdges);
        if (_terrain!.CheckHeightmapTiles(files, columns, tileWidth, low, high, rawWidth, rawHeight, bigEndian, shared) is { Length: > 0 } problem)
        {
            Info.Text = problem;
            CallDeferred(Window.MethodName.PopupCentered);
            return;
        }

        var regions = _terrain.Data?.Locations.Count ?? 0;
        if (regions > UndoLimit)
        {
            GD.PushWarning($"{_terrain.Name}: heightmap tiles imported with no undo: {regions} regions is too many to keep.");
            _terrain.ImportHeightmapTiles(files, columns, tileWidth, low, high, replace, rawWidth, rawHeight, bigEndian, shared);
            return;
        }

        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        undo.CreateAction($"Import {files.Count(f => f.Length > 0)} heightmap tiles");
        undo.AddDoMethod(_terrain, TerrainNode.MethodName.ImportHeightmapTiles, files, columns, tileWidth, low, high, replace, rawWidth, rawHeight, bigEndian, shared);
        undo.AddUndoMethod(_terrain, TerrainNode.MethodName.PutRegions, _terrain.CopyRegions());
        undo.CommitAction();
    }
}
#endif
