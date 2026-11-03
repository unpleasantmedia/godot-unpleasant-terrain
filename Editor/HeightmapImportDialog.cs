#if TOOLS
using System.IO;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain.Editor;

/// <summary>
/// The options for making a terrain's ground from a heightmap (HeightmapImportDialog.tscn): the file, how wide
/// the ground is, the heights black and white stand for, and what becomes of regions it already has. One step to undo.
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

    public override void _Ready()
    {
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
}
#endif
