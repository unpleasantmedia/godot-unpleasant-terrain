#if TOOLS
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain.Editor;

/// <summary>A place on the grid of heightmap tiles (HeightmapTileCell.tscn): drag it onto another to swap them, click to choose its file, right-click to empty it.</summary>
[Tool]
public partial class HeightmapTileCell : Button
{
    private const string DragKey = "heightmap_tile";

    [Signal]
    public delegate void SwappedEventHandler(int from, int to);

    [Signal]
    public delegate void ClearedEventHandler(int index);

    [Export]
    public int Index { get; set; }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
        {
            EmitSignal(SignalName.Cleared, Index);
            AcceptEvent();
        }
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (Text.Length == 0)
            return default;
        SetDragPreview(new Label { Text = Text });
        return new Godot.Collections.Dictionary { [DragKey] = Index };
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) =>
        data.VariantType == Variant.Type.Dictionary && data.AsGodotDictionary().ContainsKey(DragKey);

    public override void _DropData(Vector2 atPosition, Variant data) =>
        EmitSignal(SignalName.Swapped, data.AsGodotDictionary()[DragKey].AsInt32(), Index);
}
#endif
