# 001 — Painting UX fit & finish

Written 2026-10-28. Follow-up plans live alongside this one in
`C:\projects\L22\Plans\`:

- `002-project-persistence.md` — save/load, multi-project, autosave.
- `003-prefabs.md` — prefab instancing, scene graph, layer affects, live
  propagation.

Future plans are authored by the user; don't invent new numbered plan
files without being asked.

---

## Context (where we are now)

**Done.** Reference points so future work doesn't duplicate:

- `L22.Editor` Avalonia 12 desktop app, `L22.Cli` client, shared solution.
- UI shell: menu bar, left toolbar (Elev / Type), right context-sensitive
  options panel, two central viewports. Stub menu entries (New Project,
  Agents) and the single-tool "Brush" button have been **removed** so the
  surface reflects what actually works.
- **Edit viewport (left)**: top-down ortho (`TerrainViewport.TopDown=true`,
  `Camera.IsOrthographic=true`). WASD pans, R/F zooms, wheel zooms,
  right-drag pans. Pitch and yaw are reset to the top-down values at
  every render (`EnforceTopDown`), so nothing can tilt it.
- **Preview viewport (right)**: perspective. WASD walks, R/F rise/fall,
  wheel dolly, right-drag rotates.
- Window-level input routing (`MainWindow.OnWindowKeyDown/Up` +
  `OnWindowPointer*`). Pointer events go to the viewport under the cursor.
  Camera-move keys (WASD / R / F) route to the **last-hovered viewport**
  so they still register when the pointer briefly wanders over the menu
  or options panel. Ctrl+Z / Ctrl+Y are handled at the window level
  before per-viewport dispatch so they work regardless of pointer
  location.
- **View-model / data-layer split**:
  - `ViewModels/BrushViewModel.cs` holds brush options with
    `INotifyPropertyChanged`.
  - `ViewModels/MainViewModel.cs` holds the brush, exposes `Undo`/`Redo` /
    `SetActiveLayer` / `PickTerrainType`. Its `Brush` property returns
    `_ctx.Brush` so the UI and the paint tool mutate the same instance.
  - `EditorContext` is the composition root: constructs `World`,
    `CommandHistory`, `BrushViewModel`, and `ToolManager` in order.
  - `ToolManager` is now thin glue; `PaintBrushTool` reads brush options
    from the shared `BrushViewModel` and writes to `World` only through
    `TerrainTypePaintCommand` / `ElevationPaintCommand`.
- Rendering: `Avalonia.OpenGL.Controls.OpenGlControlBase`, GLSL ES 3.00
  shaders. `TerrainGlRenderer` owns per-chunk VBO/VAO/EBO.
  `Render` snapshots the dirty-chunk set to an array before iterating so a
  late `ChunkChanged`/`ChunkCreated` event can't surprise it mid-build.
- World model: `World` → sparse `Dictionary<ChunkCoord, Chunk>`, 32×32 cells
  per chunk, auto-materializes on write (virtual chunks). Initial scene is
  4×4 empty chunks. When a chunk is created the four cardinal neighbors
  are marked dirty (`GetOrCreateChunk → MarkDirty`) so their edge
  vertices re-sample.
- Jittered triangle grid: deterministic hash (`Jitter.Offset`) means edge
  vertices agree across chunks. Cross-chunk-aware normals via central
  differences through `TerrainMeshBuilder.SampleVertexElevation`.
- Command pattern: `IEditorCommand`, `CommandHistory` with plain
  `Stack<IEditorCommand>` undo/redo, capacity 256.
  `TerrainTypePaintCommand` + `ElevationPaintCommand` capture before/after
  per cell in a dictionary for brute-force undo.
- Layers: terrain type (sand=yellow, grass=green, rock=grey), elevation
  (0..1, scaled by `TerrainMeshBuilder.ElevationScale`). When Elevation layer
  is active, top-down renders in greyscale (`ShowElevationGreyscale`).
- IPC: `IpcServer` on TCP `127.0.0.1:47622`, JSON Lines protocol. `PinManager`
  caches approvals per `(fileId, remoteIp, pin)`. `PinApprovalDialog` with
  Allow 1h / Allow 24h / Block. All command execution is marshalled onto the
  UI thread. The IPC path reads `ctx.Brush` and writes to the world only
  through commands (same discipline as the UI).
- Headless verify path (`--verify <png>`) paints a sample mound and
  exports a PNG via dependency-free `PngWriter`. The verify render forces
  perspective + non-greyscale on the edit viewport so the capture
  exercises terrain-type colors as well as elevation.

---

## Goals of this plan

Polish the top-down painting + 3D preview experience so it feels precise and
responsive. These are the smallest items that must land before anything
built on top of them (persistence, export) is worth doing.

---

## Items

- [x] **Input reliability.** Window-level routing in place for both
  keyboard and pointer events. Ctrl+Z / Ctrl+Y work regardless of
  pointer location; WASD / R / F route to the last-hovered viewport so
  the camera keeps moving when the pointer wanders over the menu or
  right panel. Verified twice by external audit (second pass caught the
  WASD gap, which has since been fixed).
- [x] **Top-down hard lock.** `EnforceTopDown()` runs every frame in
  `OnOpenGlRender`. No code path writes `Camera.Pitch` for the top-down
  viewport outside `ConfigureTopDown`.
- [x] **View-model / data-layer split.** VM mutations go through
  commands; views never touch `World` for writes. Confirmed by audit.
- [x] **Remove un-wired UI.** New Project menu, Agents menu, and the
  Brush tool button are gone. Everything the UI shows is wired.
- [ ] **Grid overlay on the top-down viewport.** Thin lines every N cells
  (configurable, default 8), with heavier lines on chunk boundaries (every
  32). Draw in a separate GL program (`ShaderSource.GridVertex/Fragment`
  is stubbed but never used — wire it up or replace with a quad-based
  grid).
- [ ] **Status bar** at the bottom of `MainWindow`: cursor world cell,
  chunk coord, hovered terrain type + elevation value, undo/redo depths,
  server endpoint. One `TextBlock` row; read values from
  `TerrainViewport` on pointer move.
- [ ] **Brush cursor fidelity.** `TerrainFragment` paints a yellow ring in
  world XZ. On the greyscale elevation underlay it reads low-contrast;
  pick a brush color that contrasts regardless of underlay (inverted
  luminance or a dashed outline).
- [ ] **Brush shape options.** Add square and single-cell shapes
  alongside the disc. New field `BrushViewModel.Shape` (enum). Update
  `PaintBrushTool.Stamp` + IPC `StampDisk` to dispatch on shape.
- [ ] **Keyboard shortcuts for layer / brush type.** 1/2 toggle layer,
  Q/E cycle brush type when Terrain Type layer is active, `[`/`]` adjust
  radius.
- [ ] **Reset Cameras alignment.** `OnResetCameras` and `MainWindow.ctor`
  both hard-code preview camera defaults. Centralise as a static method
  on `TerrainViewport` so they can't drift (flagged again by the audit).
- [ ] **Middle-mouse pan** on both viewports. Frees right-click up for a
  future tilt tool on the ortho view if we want one later.
- [ ] **`--verify-size WxH` respects the request.** Today it sizes the
  window, not the viewport, so chrome (menu + toolbar + options panel)
  eats most of the pixels. Either size the window so the viewport ends
  up at the requested resolution, or render to an off-screen FBO sized
  to the request.
- [ ] **`--verify` should not start the IPC listener.** Headless capture
  collides with a running editor on `127.0.0.1:47622`. Skip
  `IpcServer.Start()` when `Program.Args.VerifyMode` is true, or accept
  `--port 0` as "don't listen".
- [ ] **Clean IPC shutdown on exit.** `App.axaml.cs` never calls
  `IpcServer.Stop()` when the window closes. Not user-visible today but
  leaks the TCP listener until process exit — hook `Closed` on
  `MainWindow` or `IClassicDesktopStyleApplicationLifetime.Exit`.

## Critical files

- `L22.Editor/Views/TerrainViewport.cs` — input handlers, camera, picking.
- `L22.Editor/MainWindow.axaml` + `.cs` — layout, Window-level routing.
- `L22.Editor/Models/Rendering/ShaderSource.cs` — fragment brush rendering,
  grid shaders (currently unused).
- `L22.Editor/Models/Tools/PaintBrushTool.cs` — brush shape plumbing.
- `L22.Editor/ViewModels/BrushViewModel.cs` — brush shape enum lives here.

## Verification

- Paint elevation and terrain-type strokes using mouse only. Confirm:
  - Brush cursor follows the mouse smoothly on both viewports.
  - Painting works end-to-end (left-click → stroke → undo restores).
  - WASD / R / F / wheel all move the hovered viewport's camera.
  - Right-drag tilts the preview and pans the top-down. Top-down can't be
    tilted by any input.
  - Ctrl+Z / Ctrl+Y work from anywhere, including over the menu or the
    right panel.
- Status bar updates every pointer move.
- `--verify` capture produces a colored mound (rock cap, grass ring, sand
  skirt) on blue, at the requested resolution.
