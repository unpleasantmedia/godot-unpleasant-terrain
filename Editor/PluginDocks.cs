#if TOOLS
using Godot;

namespace UnpleasantMedia.Terrain.Editor;

/// <summary>
/// The plugin's docks, shown only while their tool is in use (the node they edit selected): put in
/// their dock slot, at the front, when the plugin is shown, and taken out when it is hidden.
/// </summary>
internal static class PluginDocks
{
    /// <summary>Puts its dock in its slot (at the front), or takes it out.</summary>
    public static void Show(EditorPlugin plugin, Control? dock, bool visible, EditorPlugin.DockSlot slot = EditorPlugin.DockSlot.RightBl)
    {
        if (dock is null || !GodotObject.IsInstanceValid(dock))
            return;
        var docked = dock.GetParent() is not null;
        if (visible && !docked)
        {
            plugin.AddControlToDock(slot, dock);
            // In front in its slot, rather than behind another tab.
            Node child = dock;
            for (var parent = dock.GetParent(); parent is not null; child = parent, parent = parent.GetParent())
            {
                if (parent is TabContainer tabs)
                {
                    tabs.CurrentTab = child.GetIndex();
                    break;
                }
            }
        }
        else if (!visible && docked)
        {
            plugin.RemoveControlFromDocks(dock);
        }
    }

    /// <summary>Takes its dock out, if it is in, and frees it: for when the plugin goes.</summary>
    public static void Remove(EditorPlugin plugin, Control? dock)
    {
        if (dock is null || !GodotObject.IsInstanceValid(dock))
            return;
        if (dock.GetParent() is not null)
            plugin.RemoveControlFromDocks(dock);
        dock.QueueFree();
    }
}
#endif
