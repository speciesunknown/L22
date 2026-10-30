using System.Collections.Generic;

namespace L22.Editor.Models.Objects;

// One concrete object placed in a scene. Points at a prefab by Id and
// stores only sparse overrides — null/empty inherits from the prefab.
public sealed class ObjectInstance
{
    public string Id { get; set; } = "";
    public string PrefabId { get; set; } = "";

    // Grid cell the icon sits over. Objects don't participate in terrain
    // geometry, so sub-cell precision is deferred (see 007 "out of scope").
    public int CellX { get; set; }
    public int CellY { get; set; }

    // World-space Y offset above whatever the terrain surface is at
    // (CellX, CellY). 0 = snap to the surface.
    public float YOffsetWorld { get; set; }

    public string? NameOverride { get; set; }
    public IconRef? IconOverride { get; set; }

    // Only paths the user actually edited appear here. Each child's `Name`
    // is the top-level property name on the prefab; nested overrides are
    // represented by the same tree shape as the prefab default.
    public List<PropertyNode> PropertyOverrides { get; set; } = new();
}
