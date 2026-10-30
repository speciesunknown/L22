# 007 — Elevation brush: restore raise / lower (not set-to-value)

## Context

The elevation brush used to blend each cell toward a target height
(`BrushViewModel.ElevationValue`). Each stamp did:

```
next = cur + (target - cur) * falloff * strength
```

So a target of 1.0 would raise flat ground toward 1.0; a target of 0.0 would
lower peaks back toward 0. Strength controlled how fast each stroke
converged. The `ElevationValue` slider in the right panel was the target.

When plan 005/006 introduced the four-mode sculpt system
(Additive / Subtractive / Smooth / Unsmooth, driven by Shift/Alt), the
elevation branch was rewritten in `PaintBrushTool.Stamp` to:

```
Additive:    next = cur + w
Subtractive: next = cur - w
```

That's a *constant* raise/lower per stamp, independent of any target. The
`ElevationValue` slider is still in the view-model and still exposed in the
UI, but no code path reads it anymore for the brush. The brush can now only
monotonically raise or lower — the "paint toward a specific height" idiom is
gone, and the slider is dead state.

## Items

- [ ] **Decide the semantics.** Two reasonable options; the plan should
  commit to one before writing code:
  1. **Target-blend (old behavior).** Keep Shift to invert raise→lower, but
     the direction is derived from the sign of `(target − cur)` rather than
     a mode. One slider = one height to brush toward.
  2. **Pure raise/lower + ceiling/floor.** Keep Additive/Subtractive but
     clamp each cell to `ElevationValue` as a per-stroke ceiling/floor:
     Additive stops at `ElevationValue`, Subtractive stops at `-ElevationValue`
     (or zero). The slider becomes a stop-at-this-height limit, which is
     what sculpting tools in most DCCs do.
- [ ] **Wire the chosen mode in `PaintBrushTool.Stamp`.** Reinstate reads
  of `_brush.ElevationValue`; keep the Shift/Alt → `SculptMode` modifier
  pipeline so the four cursor icons still mean something.
- [ ] **UI label.** If option 2 is picked, relabel the `SlElevation`
  slider from "Target" (or whatever today's label is) to "Height limit"
  so the behavior isn't surprising.
- [ ] **Smooth / Unsmooth untouched.** Those already read neighbors and
  don't need a target; this plan only concerns Additive / Subtractive.

## Critical files

- `L22.Editor/Models/Tools/PaintBrushTool.cs` — `Stamp`, elevation branch.
- `L22.Editor/ViewModels/BrushViewModel.cs` — `ElevationValue` already
  exists; no model change required.
- `L22.Editor/MainWindow.axaml` — `SlElevation` label if option 2.

## Verification

- Paint a flat starting world. With `ElevationValue = 0.6`, Additive should
  stop raising cells once they reach 0.6 (option 2) or asymptote toward
  0.6 (option 1). Shift+paint should mirror this on the way down.
- Undo + redo of a sculpt stroke must round-trip exactly, same as today —
  the stroke still writes a single `ElevationPaintCommand`.
- Smooth/Unsmooth behavior must not regress.
