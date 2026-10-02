# Pure C# raster reference core

This folder targets C# 8 / Unity 2022.3-compatible `System` APIs. It has no UnityEngine dependency.

## Coordinates and storage

- Pixel `(0, 0)` is the bottom-left pixel; its center is `(0.5, 0.5)`.
- Layers are ordered bottom-to-top. Tile and full-frame buffers are row-major RGBA8, lowest Y first.
- Alpha is straight/unassociated coverage. RGB is stored as supplied; this proof does not perform ICC conversion or linear-light color management.
- Missing tiles are zero RGBA. Uniform tiles retain one RGBA value. Other tiles use full fixed-size buffers. Edge padding must remain zero.
- Zero-alpha RGB imported from files is retained exactly. Explicit complete erasure clears RGB as well as alpha.
- Tile exports are detached copies; import copies caller memory. Internal transaction snapshots are copy-on-write, never brush replay.

## Painting and history

`PaintDocument.BeginStroke(layerId, channel, settings)` freezes settings. Feed pixel-space `BrushSample` values with nondecreasing times using `Add`. The stroke samples arc length at a fixed fraction of the nominal brush diameter; pressure is linearly interpolated at each stamp. Duplicate positions create no extra stamps. There is no forced second endpoint stamp on commit. Pressure has independent linear size, opacity and flow assignments.

For mesh-derived dabs, `ApplyPixel(x, y, coverage, pressure)` accepts already-computed geometric coverage. Union duplicate triangle coverage for each dab before calling. PressureSize is not applied here because the caller already chose the footprint. Painting and erasure affect exactly one channel per transaction.

`Commit()` stores exact changed-tile before/after states. `Cancel()` and `Dispose()` restore the original state. Invalid input and budget errors during Add/ApplyPixel automatically cancel. No-op commits retain redo. Undo/redo cannot interleave an active stroke. Layer structure/metadata changes use the same bounded history.

Direct `SparseTileSurface.SetPixel`, `ImportTile` and `Clear` are import/administrative mutations, intentionally clearing document history. Call `ClearHistory` after constructing an imported document. `GetChannel` creates an absent empty enabled channel; `TryGetChannel` is the side-effect-free query. A disabled existing channel remains disabled and its pixels are retained.

## Memory contracts

`SourceBudgetBytes` defaults to 256 MiB; `ActiveStrokeBudgetBytes` defaults to 64 MiB; undo/redo payload defaults to 64 MiB. Tile expansion, rollback capture and undo restoration preflight their budgets. Refused strokes restore previous pixels rather than retrying allocations. `HistoryTrimming` fires before dropping exact old commands, reporting the number of payload bytes discarded.

Counters account for pixel payload and nominal history headers, not managed object overhead, uploaded GPU textures or external codec buffers. `PeakWorkingBytes` is a conservative current payload estimate, not a measured application-memory peak. These limits do not promise the full specification's 100-layer 4K workload fits in memory. Single-threaded access is required; readers must not race mutation. Serialization should reject active strokes or explicitly finish them first.

## Reference compositing and graph boundary

Normal, Multiply and Screen use separable source-over blending with correct partial-alpha terms. Composition is performed in stored RGB space and rounded to RGBA8 after each layer. This is a reference raster path, not proof of PSD application matching. Normal-channel pixels can be retained and inspected, but this core does not implement tangent-vector normal blending.

`DependencyGraph` stores typed declarations for later effect/anchor evaluation. Connections enforce same texture set, input type, optional semantic channel, one source per input, acyclicity, and lower-layer/earlier-effect order. Invalid node/layer-order changes are atomic. Referenced deletion is rejected. Invalidation increments source and downstream revisions, including hidden consumers. Actual filter/generator execution, halo-aware dependency scheduling (display-side tile change tracking exists: `ChangeSerial` / `TryGetChangedTiles`), graphs in native persistence, multi-channel atomic strokes, masks and pressure-curve editing are later work.
