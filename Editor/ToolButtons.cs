#if TOOLS
using Godot;

namespace UnpleasantMedia.Terrain.Editor;

/// <summary>
/// How the plugin's tool buttons look when on: filled with the editor's accent colour, with bright
/// text, so which tool is on shows at a glance (a flat toggle button's own pressed look is faint).
/// </summary>
internal static class ToolButtons
{
    /// <summary>Gives a toggle button the on look.</summary>
    public static void Style(Button button)
    {
        var theme = EditorInterface.Singleton.GetEditorTheme();
        var accent = theme.GetColor("accent_color", "Editor");
        var on = new StyleBoxFlat
        {
            BgColor = accent,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2,
        };
        var onHover = (StyleBoxFlat)on.Duplicate();
        onHover.BgColor = accent.Lightened(0.15f);
        // Not flat: a flat button draws no background at all, so its on look could not show. Off,
        // it still looks flat (nothing behind it), with a faint accent on hover.
        button.Flat = false;
        var off = new StyleBoxEmpty
        {
            ContentMarginLeft = on.ContentMarginLeft, ContentMarginRight = on.ContentMarginRight,
            ContentMarginTop = on.ContentMarginTop, ContentMarginBottom = on.ContentMarginBottom,
        };
        var offHover = (StyleBoxFlat)on.Duplicate();
        offHover.BgColor = accent with { A = 0.25f };
        button.AddThemeStyleboxOverride("normal", off);
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        button.AddThemeStyleboxOverride("disabled", off);
        button.AddThemeStyleboxOverride("hover", offHover);
        button.AddThemeStyleboxOverride("pressed", on);
        button.AddThemeStyleboxOverride("hover_pressed", onHover);
        var text = Colors.White;
        button.AddThemeColorOverride("font_pressed_color", text);
        button.AddThemeColorOverride("font_hover_pressed_color", text);
        button.AddThemeColorOverride("icon_pressed_color", text);
        button.AddThemeColorOverride("icon_hover_pressed_color", text);
    }

    /// <summary>Gives every toggle button under a control the on look.</summary>
    public static void StyleAll(Node root)
    {
        foreach (var node in root.FindChildren("*", nameof(Button), true, false))
        {
            if (node is Button { ToggleMode: true } button)
                Style(button);
        }
    }
}
#endif
