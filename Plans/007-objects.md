# 007 — Objects (prefabs + instances) and metadata

Implements `Journeys/0007-objects-metadata.md`.

## Context

An **object** is a point annotation in a scene the game cares about but
the terrain editor doesn't use for geometry — spawn points, enemies,
triggers, loot, etc. Each object has a position, a prefab it inherits
from, a user-visible icon, and a free-form property tree the game reads.

An **object prefab** is a reusable template. It defines the default
display name, foreign identifier, icon, and property defaults. Instances
reference a prefab and override only what differs.

Objects don't modify the elevation/terrain PNGs — they live in their
own file inside the project folder. Prefabs live in a sibling library
folder so the same prefab can appear in multiple scenes.

## Data model

```
ObjectPrefab {
    id            : string (stable GUID, used by references)
    displayName   : string
    foreignIdentifier : string          # the key the game indexes on
    icon          : IconRef?            # null → pink-cube fallback
    properties    : PropertyNode[]      # default values; see tree below
}

ObjectInstance {
    id            : string              # per-scene GUID
    prefabId      : string
    cellX, cellY  : int                 # grid cell the icon sits over
    yOffsetWorld  : float               # 0 = snap to terrain surface
    nameOverride  : string?             # null → inherit prefab.displayName
    iconOverride  : IconRef?            # null → inherit prefab.icon
    propertyOverrides : PropertyNode[]  # sparse — only edited paths
}

IconRef {
    imagePath     : string              # relative to project (or library) root
    gridCols      : int = 1
    gridRows      : int = 1
    cellCol       : int = 0             # 0..gridCols-1
    cellRow       : int = 0             # 0..gridRows-1
}

PropertyNode {
    name          : string
    kind          : "text" | "number" | "array" | "properties"
    value         : string | double | PropertyNode[] (for array/properties)
}
```

`array` and `properties` are both child-bearing nodes; the distinction
is that `properties` children are *named* fields and `array` children
are *positional* (the editor renders them as `[0]`, `[1]`, … with no
`name` entry displayed).

## File layout

Scene folder (per project):
```
scene-3.l22proj/
  index.json
  chunks/…
  objects.json            # NEW — list of ObjectInstance, scene-local
```

Prefab library (shared across projects, lives next to `Levels/`):
```
GilgameshE1/
  Levels/
    scene-3.l22proj/…
  Prefabs/                # NEW
    enemy-slime.prefab.json
    enemy-slime.png       # icon source — lives next to the .prefab.json
    spawn-point.prefab.json
```

Prefab resolution: `ObjectInstance.prefabId` is looked up in the
`Prefabs/` sibling of the project's parent `Levels/` directory. A
missing prefab renders the pink cube and shows a red indicator in the
browser.

## Items

### Phase 1 — Data model + IO (no UI, no rendering)

- [ ] **`L22.Editor/Models/Objects/` subtree.** `ObjectPrefab`,
  `ObjectInstance`, `IconRef`, `PropertyNode` as record types.
- [ ] **`ObjectIO.cs`.** Save/load `objects.json` within the scene
  folder. Save/load individual `.prefab.json` files from a given
  library directory. JSON via `System.Text.Json`.
- [ ] **Wire into `ProjectIO.Save/Load`.** On save, write `objects.json`
  under the scene folder. On load, read it back. Round-trip test added
  to `IoVerify`.
- [ ] **`PrefabLibrary`.** In-memory cache of loaded prefabs keyed by
  id, with a reload-from-disk method. Owned by `EditorContext`.

### Phase 2 — Rendering

- [ ] **3D view: billboards.** New GL program in `TerrainGlRenderer`
  that draws a camera-facing quad per object. Instance data uploaded
  per frame (position + icon tex-coord rect). Texture atlas built lazily
  from the icons the current scene references. **No icon → pink cube**
  (switch to a cube shader path, not a flat sprite, so the fallback is
  distinct even at odd angles).
- [ ] **Top-down view: 2D icons.** Draw sprites with depth test off so
  objects always sit on top of terrain. Rendered via Avalonia overlay
  Canvas *or* an extra GL pass — pick the GL pass to stay inside the
  renderer and get the frustum-overlay treatment for free.

### Phase 3 — UI browser + placement

- [ ] **Object browser panel.** New right-panel tab or section listing
  all instances in the scene (grouped by prefab). Clicking an instance
  focuses it in both viewports. "Add from prefab" button opens a
  flyout listing available prefabs.
- [ ] **Placement tool.** New `ToolKind.PlaceObject`. Click in the
  top-down view → creates an `ObjectInstance` at the hovered cell with
  the currently-selected prefab. The 3D camera's frustum stays visible
  during placement.
- [ ] **Selection + delete.** Left-click an instance in either viewport
  to select; Delete key removes.

### Phase 4 — Property editor

- [ ] **Tree editor** for the `properties` of the selected instance.
  Renders the inherited prefab default greyed-out next to each override;
  "revert to default" clears the override. Nested properties/arrays
  render as collapsible nodes.
- [ ] **Add/remove property.** For array nodes, append/pop. For
  properties nodes, add a named child.
- [ ] **Icon override.** File-pick a PNG + grid cell selector (two
  spinners for cols/rows, click a cell in a preview).

### Phase 5 — Prefab editor

- [ ] **Prefab list panel.** List all `.prefab.json` under the library
  dir. Create / rename / delete.
- [ ] **Prefab editor dialog.** Same property-tree UI as instance
  editing but operating on the prefab's defaults; icon + foreignId
  editable; `displayName` editable.
- [ ] **Propagation.** Changing a prefab default refreshes all
  instances that haven't overridden that property path in the current
  scene (and any other scenes the user has open).

## Critical files

- `L22.Editor/Models/Objects/*.cs` — new.
- `L22.Editor/Models/World/ProjectIO.cs` — add `objects.json` round-trip.
- `L22.Editor/EditorContext.cs` — hold `ObjectInstance[]` and the shared
  `PrefabLibrary` reference.
- `L22.Editor/Models/Commands/*ObjectCommand.cs` — new (place / delete /
  property-edit as undoable commands).
- `L22.Editor/Views/TerrainGlRenderer.cs` — billboard pass + 2D sprite
  pass + pink-cube fallback.
- `L22.Editor/MainWindow.axaml` — browser panel, placement tool button,
  property-tree pane.
- `L22.Editor/IoVerify.cs` — extend to cover objects + prefabs.

## Verification

- **Round-trip.** Build a scene with 5 instances across 2 prefabs,
  mix of overrides. Save, reload, cell-by-cell compare — identical.
- **Fallback render.** Load a scene whose prefab references a missing
  icon file — render shows pink cube in 3D, placeholder icon in 2D,
  red indicator in the browser.
- **Prefab propagation.** Instance A inherits prefab default `hp = 10`.
  Edit prefab to `hp = 20`. Instance A reads 20. Override A to `hp = 5`.
  Edit prefab to `hp = 100`. Instance A still reads 5.
- **Deletion safety.** Deleting a prefab the current scene references
  flips every instance to the missing-prefab visual state (not an
  error dialog on load; the editor should keep working).
- **IPC.** Add `object-place`, `object-delete`, `object-set-property`
  verbs so an agent can author objects without the UI. Smoke-test via
  L22.Cli against a `--yolo` session.

## Out of scope (deferred)

- Multi-select / box-select of objects.
- Object rotation and scale (billboards are axis-aligned; cube fallback
  likewise).
- Prefab inheritance chains (prefab-of-prefab). Prefabs are leaves.
- Property schema validation. The editor accepts arbitrary trees; the
  game decides what to do with them.
