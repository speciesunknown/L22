# 002 — Project persistence

## Context

The original brief talks about "a project I have loaded" and routing agents
by file ID. Today the editor has one in-memory `EditorContext` with the
hard-coded file ID `"default"`. This plan makes a project a real artifact
on disk and lets agents target it by a stable ID derived from the file.

## Items

- [ ] **File format: `.l22proj`.** Binary container, gzip-wrapped:
  - 8-byte magic `L22PROJ\0` + 2-byte version.
  - 2 × int32 world chunk-origin bounds (currently unused; reserved).
  - int32 chunk count.
  - Per chunk: int32 cx, int32 cy, byte[1024] terrainType (32×32),
    float[4096] elevation (32×32).
  - Future layers extend by appending named sections after the chunk block;
    unknown sections are skipped on load.
- [ ] **`ProjectIO.cs`** in `L22.Editor/Models/World/`: `Save(World, string
  path)` and `Load(string path, string fileId)`. Round-trip tested with a
  small xUnit project OR an in-process self-test under `--verify-io`.
- [ ] **File menu wiring.**
  - `New` → fresh `EditorContext`, re-bind viewports.
  - `Open` → `IStorageProvider.OpenFilePickerAsync` → load.
  - `Save` / `Save As…` → write `.l22proj` + update window title.
  - `Recent` sub-menu populated from settings.
- [ ] **Project ID resolution.** On load, file ID = SHA-256 of the absolute
  path truncated to 16 hex chars. New files pick a fresh random ID until
  they're saved for the first time. Expose a "copy file ID" command.
- [ ] **Multiple open projects (prep).** Refactor `EditorContext.Current`
  into `EditorContext.ByFileId` dictionary + an `Active` pointer. For v1
  only one is ever opened by the UI; IPC can still address others once we
  support them.
- [ ] **Dirty tracking.** `CommandHistory.PushExecuted` flips `Dirty=true`
  on the context. `Save` clears it. Close/exit asks to save if dirty.
  Window title shows `*` when dirty.
- [ ] **Autosave backup.** Every N commands (default 50) or every T minutes
  (default 5), serialize to `<project>.l22proj.autosave`. Keep up to 3
  rotating backups.

## Critical files

- `L22.Editor/Models/World/World.cs`, `Chunk.cs`, `ChunkCoord.cs`.
- `L22.Editor/EditorContext.cs`.
- `L22.Editor/MainWindow.axaml.cs` — menu wiring, dirty title.
- `L22.Editor/Models/Commands/CommandHistory.cs` — dirty hook.
- New: `L22.Editor/Models/World/ProjectIO.cs`,
  `L22.Editor/Models/World/ProjectSettings.cs` (recent files list).

## Verification

- Create a world, paint strokes on both layers, save, close, reopen —
  binary-equal world (compare `World.Chunks` dictionaries cell by cell).
- Save a 4×4 painted world → file size well under 1 MB after gzip.
- Dirty flag survives save/undo/redo correctly (undo on a dirty project
  that was saved → still dirty if it diverged from on-disk state).
- Autosave triggers on time and command count; capped at 3 files.
