# 001 — New File

## Goal

The user opens the editor, hasn't opened or saved anything before, and
wants to start painting a new scene within a few seconds.

## Preconditions

- Editor is not running.
- No `.l22proj` file is loaded via the command line.

## Flow

1. **Launch the editor.**
   - Window title shows `L22 World Editor`.
   - Welcome screen is centered: title, one-line hint, three buttons
     (`New Scene`, `New Prefab`, `Open...`), a `Recent scenes` list.
   - Tab strip and both viewports are hidden.
   - Right panel is visible but inert (no active brush values to show).

2. **Click `New Scene`.**
   - Welcome screen disappears immediately.
   - A tab appears labelled `scene-1`. It is selected.
   - **Both viewports render a visible terrain**, not a dark empty plane:
     - The top-down viewport shows a solid green (grass) 128×128 area
       filling the ortho frame. Chunk boundaries may be invisible until
       the grid overlay lands (plan `001`).
     - The 3D preview viewport shows the same green plane in perspective,
       framed by the preview camera default.
   - The right panel updates: `Layer: Elevation` (default), brush
     sliders show the default values, elevation target slider is visible.
   - The left toolbar shows `Elev` and `Type` buttons; clicking them
     switches the active layer immediately.
   - The camera-drive indicator (`◎`) is in the bottom-right of each
     viewport.

3. **Paint on the top-down.**
   - Left-click + drag on the top-down writes to the active layer.
   - Elevation layer: the painted area raises as a greyscale underlay
     (black=low, white=high) in the top-down and as an actual mound in
     the preview.
   - Terrain Type layer: the painted area recolours in the top-down and
     the preview.
   - Ctrl+Z undoes the whole stroke.

4. **Open a second new scene** (`File → New Scene` or welcome screen if
   all tabs are closed).
   - A second tab appears, `scene-2`, selected.
   - The viewports swap to show the new scene — a fresh green plane,
     no painted strokes.
   - Switching back to the first tab restores its painted content and
     camera position.

5. **Close the last tab** (`File → Close Tab`).
   - The welcome screen reappears. The editor doesn't exit.

## Non-goals (deferred)

- Save / load from disk — plan `002`.
- Populating the Recent scenes list — plan `002`.
- Prefab instancing — plan `003`.
- `Open...` button functionality — shows a placeholder dialog pointing
  at plan `002`.

## Done when

- All five steps above work with no errors in `stderr`.
- The top-down and preview never show a dark empty plane for a brand-new
  scene — the ground is always green from the start.
- Tab switching never loses content from the other tab.
- Ctrl+Z works regardless of pointer location.

## How to verify (agents)

The editor exposes a permanent testing API over its IPC socket. An agent
should drive journeys using this API, not through one-off `--verify-*`
flags. Typical loop:

1. Launch the editor in test mode:
   ```
   dotnet run --project L22.Editor -- --yolo
   ```
   `--yolo` auto-approves agent connections (no PIN dialog) and shows a
   `YOLO` badge on the server status label so a human won't miss it.

2. From an agent terminal, use the CLI with `--file "*"` to open an admin
   session (works against the active document):
   ```
   dotnet run --project L22.Cli -- new-scene --file * --pin test
   dotnet run --project L22.Cli -- screenshot --file * --pin test \
       --viewport edit --path C:/tmp/edit.png
   dotnet run --project L22.Cli -- paint --file * --pin test \
       --layer elevation --x 60 --y 60 --radius 12 --value 0.9
   dotnet run --project L22.Cli -- screenshot --file * --pin test \
       --viewport free --path C:/tmp/preview.png
   dotnet run --project L22.Cli -- undo --file * --pin test
   ```

3. Agent reads the PNGs with its file-read tool and asserts the expected
   visuals (green plane, raised mound, back to flat, etc.).

Any journey file in this folder should be testable via this loop. If
something can't be tested this way, add the missing IPC verb — not a new
one-off CLI flag.
