# 010 — Rivers (bezier curves with riverbed mesh)

Implements `Journeys/005-river-tool.md`.

## Context

Rivers are curvy — users want to lay them out with bezier control
points, each with a width. The editor stores them as a list of
polylines (eventually bezier-interpolated) per scene; the renderer
pushes terrain down along the path (erosion) and swaps affected
triangles for a dedicated riverbed mesh so the shoreline reads
cleanly at an angle. Export reports which triangles have been
reassigned so the game can hook per-region behaviour.

Scope expectation: this is a multi-phase feature. Phase 1-2 get a
usable editor flow and round-tripping storage; phase 3+ ships the
actual erosion / tessellation / export piping.

## Data model

```
River {
    id          : string (per-scene GUID)
    name        : string (user-facing label)
    vertices    : RiverVertex[]      # ordered, 2+ required
    branches    : River[]            # child rivers forking off this one
}

RiverVertex {
    cellX, cellY   : int             # grid cell the handle sits over
    subCellX, subCellY : float       # 0..1 inset within the cell, so
                                     # curves aren't locked to the grid
    widthWorld     : float           # river bank-to-bank, world units
    bezierHandleIn  : Vector2        # relative, in world units
    bezierHandleOut : Vector2        # relative, in world units
}
```

Branches are modelled as fresh `River`s attached to a parent vertex —
simpler than tracking per-vertex forks on the parent. The parent still
reads as one polyline; branches render independently from their origin.

## File layout

Scene folder (adds a sibling to `objects.json`):
```
scene-N.l22proj/
  index.json
  chunks/…
  objects.json
  rivers.json          # NEW — list of River records, scene-local
```

Export (phase 6) adds:
```
scene-N.l22proj/
  export/              # NEW — game-input artifacts (not editor-canonical)
    riverbed.png       # mask of cells reassigned to the riverbed mesh
    riverbed.obj       # the riverbed mesh itself (or .ply, TBD)
```

## Items

### Phase 1 — Data model + IO (no rendering, no UI)

- [ ] **`L22.Editor/Models/Rivers/` subtree.** `River`, `RiverVertex`
  as mutable classes (the editor mutates them in place, same pattern
  as `ObjectInstance`).
- [ ] **`RiverIO.cs`.** Save/load `rivers.json` next to objects.json
  via `System.Text.Json`. Branches recurse.
- [ ] **Wire into `ProjectIO.Save/Load`.** Rivers always written
  (empty list = `{"version":1,"rivers":[]}`) same as objects. New
  `LoadResult.Rivers` field.
- [ ] **`EditorContext.Rivers`** list + `RiversChanged` event + a
  `SelectedRiverVertex` selection token (river id + vertex index) for
  the Width slider to key off.
- [ ] **`--verify-io` extended.** Round-trip a scene with two
  rivers, each with 3 vertices and one branch; JSON-string compare.

### Phase 2 — Tool + top-down overlay

- [ ] **`ToolKind.River`** + ◞ toolbar button on the left.
- [ ] **`TerrainViewport.RiverToolActive`** flag + handlers:
  - click on empty cell → append a vertex to the "current" river (new
    `River` if none active).
  - click on an existing vertex handle → select it (becomes the
    insertion anchor for the next click).
  - drag an existing vertex handle → move it.
- [ ] **Top-down overlay.** Avalonia `Path` layered above the GL
  control with lines between vertices and small dots at each handle.
  Yellow for selected vertex. (Same pattern as the camera-frustum
  overlay before it moved into GL.)
- [ ] **Right-panel River browser.** List of rivers; selecting one
  activates it; "+ New River" button; width slider that writes to the
  selected vertex and its two neighbours (per journey scenario).

### Phase 3 — Basic GL render of the river line

- [ ] **River shader + pass in `TerrainGlRenderer`.** Draws each
  river as a textured strip along the bezier (two triangles per
  segment) at terrain surface height. Flat blue for now; no erosion
  of the terrain under it yet.
- [ ] **Bezier evaluation.** Shared helper that samples each segment
  at N steps (configurable; defaults to ~16 per segment) so both the
  renderer and the overlay use the same curve.

### Phase 4 — Terrain erosion

- [ ] **TerrainMeshBuilder consumes rivers.** For every vertex of
  each chunk, compute distance to the nearest river segment. Inside
  the river's `widthWorld`, lower vertex Y by a configurable
  `riverbedDepth`. Normals recompute automatically from the new
  heights.
- [ ] **`World.MarkRiversDirty`** triggers chunk re-mesh across the
  affected bounding box when a river vertex changes.

### Phase 5 — Mesh tessellation along the river bank

- [ ] **Edge-aware tessellation.** Where a river edge crosses the
  terrain grid, split the straddling triangles so the bank sits
  precisely on the river boundary. Keep shared vertices on the
  non-river side so there are no seams.
- [ ] **Tagging.** Each affected triangle gets a boolean "is this
  riverbed?" flag that export reads.

### Phase 6 — Export

- [ ] **`riverbed.png` under `export/`.** Grey-scale channel: 0 =
  terrain, 255 = riverbed, intermediate = transition band. One pixel
  per cell.
- [ ] **`riverbed.obj` under `export/`.** Just the riverbed triangles
  — same vertex positions as the terrain mesh for the shared edges, so
  the game can match them 1:1. Format TBD (OBJ is simplest; GLTF is
  prettier).
- [ ] **IPC verbs** `river-list`, `river-create`, `river-add-vertex`,
  `river-set-width`, `river-delete` so an agent can lay out rivers
  from the CLI the same way it drives objects.

## Critical files

- `L22.Editor/Models/Rivers/*.cs` — new.
- `L22.Editor/Models/World/ProjectIO.cs` — rivers.json round-trip.
- `L22.Editor/EditorContext.cs` — Rivers list, selection, events.
- `L22.Editor/Models/Commands/*RiverCommand.cs` — add / delete /
  move-vertex / change-width as undoable commands.
- `L22.Editor/Models/Rendering/TerrainMeshBuilder.cs` — erosion +
  tessellation (phases 4 + 5).
- `L22.Editor/Views/TerrainGlRenderer.cs` — river shader + pass.
- `L22.Editor/MainWindow.axaml` + `.axaml.cs` — toolbar button,
  overlay, right-panel browser.
- `L22.Editor/IoVerify.cs` — extend to cover rivers.

## Verification

- **Round-trip.** Build two rivers (one with a branch), save, reload,
  cell-by-cell and vertex-by-vertex compare — identical.
- **Click flow.** River tool active → click an empty cell → river
  with one vertex appears. Click another cell → second vertex
  appended. Click an existing vertex → it becomes the anchor. Drag an
  existing vertex → it moves, chunks re-mesh.
- **Width.** Select a vertex, bump its slider → that vertex and its
  two neighbours widen; three nodes further out are unaffected.
- **Erosion render.** Vertex 1 at (10, 10), vertex 2 at (30, 30),
  width 2 → terrain between those is visibly sunken; outside, flat.
- **Export.** `riverbed.png` has non-zero pixels only inside the
  river bands; `.obj` contains only the swapped triangles; vertex
  positions on the bank match the surrounding terrain vertices byte-
  for-byte.

## Out of scope (deferred)

- Water animation / shaders beyond flat blue.
- Current / flow direction propagation to the game.
- Lakes (closed loops of river) — rivers are open polylines for now.
- Multi-level rivers (waterfalls / height discontinuity along a
  river). The erosion in phase 4 just lowers Y uniformly along the
  path.
