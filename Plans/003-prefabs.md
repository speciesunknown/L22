# 008 — Prefabs on the elevation layer

## Context

A prefab is a reusable terrain fragment that can be instanced as a child of
another document. Editing the prefab updates every document that references
it. Each prefab is a separately-editable scene; the editor already handles
multiple open documents through tabs.

Today (end of this plan's prerequisites):

- `OpenDocument` wraps an `EditorContext` with a `DocumentKind` of `Scene`
  or `Prefab`.
- `MainViewModel.Documents` is an `ObservableCollection<OpenDocument>`;
  `MainViewModel.Active` is the one bound to the viewports.
- `MainWindow` has a `TabControl` (one tab per open document) and a
  placeholder Scene section in the right panel.
- `File → New Scene` / `New Prefab` create a new empty document. Open
  dialogs are placeholders until plan 002 lands.
- No prefab instancing, no serialization, no live update propagation.

Depends on **plan 002 (project persistence)** for anything on disk — until
that lands prefabs are session-scoped only.

## Items

- [ ] **PrefabInstance data type.** A document (`OpenDocument`) holds a
  list of `PrefabInstance { fileId, offsetX, offsetY, rotation,
  affects: {Elevation, TerrainType} }`. Lives in
  `Models/World/PrefabInstance.cs`. Serialised inside `.l22proj`.
- [ ] **Scene graph tree.** The right-panel `TreeView` renders the active
  document as root and each `PrefabInstance` as a child. Clicking a child
  focuses it (jump viewport to its origin, select it for property edit).
- [ ] **Add Prefab command.** `File → Add Prefab…` / scene-graph context
  menu → pick an open prefab (or stub until Open lands) → stamp it at
  the brush cursor as a `PrefabInstance`. Pushes an
  `AddPrefabInstanceCommand` so undo removes it.
- [ ] **Layer-affect checkboxes.** The scene-graph item for a prefab
  instance exposes two checkboxes (Elevation, TerrainType). Toggling fires
  a `ToggleAffectCommand` so undo tracks it. Rendering reads only the
  enabled layers from the prefab.
- [ ] **Composite world rendering.** `TerrainMeshBuilder` or a new
  `CompositeWorldView` combines the host document's cells with each
  prefab instance's cells (translated by offset). Topmost rule or
  additive blend TBD — start with additive for elevation (host + Σ
  prefabs) and overwrite for terrain type (prefab wins where it has a
  non-zero cell and TerrainType affect is on).
- [ ] **Live update propagation.** When the user edits a prefab tab, every
  document that holds an instance of it marks the overlapping chunks dirty
  and re-renders. Use `MainViewModel` as the pub-sub: `OpenDocument.World`
  fires `ChunkChanged`; subscribers include all host documents that
  reference it via `PrefabInstance`.
  Guard against cycles — document A can't instance document B if B
  instances A (direct or transitive).
- [ ] **Prefab-aware IPC.** CLI gets `add-prefab`, `remove-prefab`,
  `set-prefab-affects` verbs. Agents can target prefab tabs by their
  own file ID.
- [ ] **Prefab origin indicator.** On the top-down of a host scene, draw
  a marker (small square + direction arrow) at each prefab instance's
  origin.

## Open design questions

- Should a prefab be bounded (fixed footprint) or sparse (whatever cells
  it's painted)? Sparse feels right — matches the virtual-chunk model.
  Then origin + sparse chunk set is the full placement data.
- Rotation: 0° / 90° / 180° / 270° only (easy to blit) or arbitrary (needs
  cell interpolation)? Start with 90° steps.
- When a prefab has elevation affecting a region where the host also has
  elevation, should the result be additive (sum, clamped to 1) or layered
  (max, min)? Pick one default, make it configurable per instance later.

## Critical files

- New: `L22.Editor/Models/World/PrefabInstance.cs`,
  `Models/World/CompositeWorldView.cs`,
  `Models/Commands/AddPrefabInstanceCommand.cs`,
  `Models/Commands/RemovePrefabInstanceCommand.cs`,
  `Models/Commands/ToggleAffectCommand.cs`.
- `L22.Editor/Models/OpenDocument.cs` — add `PrefabInstances` list.
- `L22.Editor/Models/Rendering/TerrainMeshBuilder.cs` — read via composite
  view, not raw world.
- `L22.Editor/MainWindow.axaml` + `.cs` — scene-graph population, context
  menu, layer-affect checkboxes.
- `L22.Editor/Models/Ipc/AgentSession.cs` — new verbs.

## Verification

- Open two tabs: a scene and a prefab. Paint elevation on the prefab.
  Add the prefab as a child of the scene. The scene viewport reflects
  the prefab's elevation at the chosen origin.
- Edit the prefab tab further → scene tab updates on the next frame.
- Toggle the prefab instance's Elevation affect off → prefab's elevation
  disappears from the scene, terrain-type still applies.
- Undo removes the instance; redo re-adds it with the same affects set.
- Cycle guard: adding scene A as a prefab inside prefab B which is
  already a child of A is rejected.
- `l22 add-prefab --file <sceneId> --prefab <prefabId> --x 32 --y 32 ...`
  produces the same effect as the UI.
