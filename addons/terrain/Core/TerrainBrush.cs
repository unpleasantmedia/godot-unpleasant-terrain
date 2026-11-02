using System;
using Godot;

namespace UnpleasantMedia.Terrain;

/// <summary>A finished brush stroke: the terrain it changed, and the samples it changed, as the box round them.</summary>
public sealed class TerrainBrushStrokeEventArgs(TerrainNode terrain, Rect2I samples) : EventArgs
{
    public TerrainNode Terrain { get; } = terrain;

    public Rect2I Samples { get; } = samples;
}

/// <summary>Handles a finished brush stroke.</summary>
public delegate void TerrainBrushStrokeCompletedHandler(object? sender, TerrainBrushStrokeEventArgs e);

/// <summary>
/// The terrain brush's events, for other code to hook into. A plain C# event does not last through a
/// reload of the scripts, so subscribe from a [ModuleInitializer], which runs again after each one.
/// </summary>
public static class TerrainBrush
{
    /// <summary>A brush stroke was finished, in the editor, and its undo step made.</summary>
    public static event TerrainBrushStrokeCompletedHandler? StrokeCompleted;

    internal static void RaiseStrokeCompleted(object? sender, TerrainBrushStrokeEventArgs e) => StrokeCompleted?.Invoke(sender, e);
}
