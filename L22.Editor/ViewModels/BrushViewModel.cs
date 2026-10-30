using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Input;
using L22.Editor.Models.World;

namespace L22.Editor.ViewModels;

public enum ToolKind
{
    Brush,
    CornerElevation,
    Dropper,
    PlaceObject,
}

// Sculpt mode is driven by modifier keys while the brush is active.
// Additive (default) and Subtractive raise/lower; Smooth/Unsmooth
// averages/sharpens against neighbors.
public enum SculptMode
{
    Additive,
    Subtractive,
    Smooth,
    Unsmooth,
}

// Pure view-model state. Holds brush options that the UI binds to and that
// tools read when they build commands. The data layer never sees this type.
public sealed class BrushViewModel : INotifyPropertyChanged
{
    private ToolKind _activeTool = ToolKind.Brush;
    private LayerKind _activeLayer = LayerKind.Elevation;
    private int _radius = 4;
    // Strength and ElevationValue are in world units (same scale the 3D
    // view renders in). PaintBrushTool divides by TerrainMeshBuilder.ElevationScale
    // to get the stored 0..1 normalized value.
    private float _strength = 0.05f;
    private bool _paintSolid = true;
    private byte _terrainType = (byte)Models.World.TerrainType.Grass;
    private float _elevationValue = 7f;
    private float _cornerValue = 0.5f;
    private SculptMode _sculptMode = SculptMode.Additive;

    public ToolKind ActiveTool { get => _activeTool; set => Set(ref _activeTool, value); }
    public float CornerValue { get => _cornerValue; set => Set(ref _cornerValue, value); }

    public LayerKind ActiveLayer { get => _activeLayer; set => Set(ref _activeLayer, value); }
    public int Radius { get => _radius; set => Set(ref _radius, value); }
    public float Strength { get => _strength; set => Set(ref _strength, value); }
    public bool PaintSolid { get => _paintSolid; set => Set(ref _paintSolid, value); }
    public byte TerrainType { get => _terrainType; set => Set(ref _terrainType, value); }
    public float ElevationValue { get => _elevationValue; set => Set(ref _elevationValue, value); }

    public SculptMode SculptMode { get => _sculptMode; set => Set(ref _sculptMode, value); }

    public static SculptMode SculptModeFor(KeyModifiers mods)
    {
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        bool alt   = mods.HasFlag(KeyModifiers.Alt);
        if (alt && shift) return SculptMode.Unsmooth;
        if (alt)          return SculptMode.Smooth;
        if (shift)        return SculptMode.Subtractive;
        return SculptMode.Additive;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
