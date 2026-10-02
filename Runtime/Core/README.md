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

Counters account for pixel payload and nominal history headers, not managed object overhead, uploaded GPU textures or external codec buffers. `PeakWorkingBytes` is a conservative current payload estimate, not a measured application-memory peak. These limits do not promise the full specification's 100-layer 4K workload fits in memory. Single-threaded access is required; readers must not race mutation. Serialization should reject active strokes or explicitly finish them first. `SparseTileSurface.AllocatedBytes` is a running total kept by every tile change (budget checks do not walk the tiles).

## Threads

The document is single-threaded for its callers, but several operations use worker threads internally and return only after they finish: `CpuCompositor.CompositeRegion` (and `Composite`), `NormalMaps.Output`, `Fill` / `FillMask` / `Gradient` / `ReplacePixels`, `Transform`, `Resampled`, the magic wand, selection builders and edits, filter evaluation, and brush dabs whose bounding box has at least 128 × 128 pixels. `CoreParallelism.MaxDegreeOfParallelism` caps them (0 = one per processor, 1 = everything on the calling thread). Every output pixel is computed from its own inputs, so the bytes do not depend on the number of threads (tests compare 1, 2, 3 and the default). Workers only read the document and write their own output slots; everything that allocates tiles, checks a budget, records history or touches the filter cache stays on the calling thread, in the same order as a sequential run, so a refused or failing operation leaves exactly what the sequential code left. Brush strokes notify the surface's revision and change journal once per tile per dab rather than per pixel; which tiles are reported is unchanged, the revision numbers themselves are not part of the result. The GPU compositor, mesh baking (its own `MeshBakeBudget`) and PSD/persistence code are outside this setting.

## Canvas size

`PaintDocument.Resampled(width, height, resampling, sourceBudgetBytes)` returns a new document at another size (1..8192 per side, `MaxNativeSide`, the largest the native archive reads back) and never changes the original: the same document and layer IDs, structure, attributes and settings, no history, a `Revision` that continues after the original's. Raster channels, masks and the selection are resampled with `CanvasResampling` (Nearest copies the pixel under the new centre; Bilinear weighs the four around it; Area averages the covered source area with exact fractional overlaps). Weights come from integer arithmetic, the canvas edge repeats outside, averages are premultiplied, a pixel read only from equal pixels is that pixel, a pixel that comes out fully transparent keeps the plain average colour of the transparent pixels it covers (so the RGB stored under zero alpha survives), and averaged Normal pixels are renormalized. Values measured in pixels follow the scale (geometric mean of the two axes): blur and sharpen radii (rounded half away from zero, 1..the filter's maximum), the Height → Normal strength (±256), 2D paths (points and brush radius, drawn again from the scaled path). Clamped values are reported in `Notes`; layers with a path on the model keep their UV-bound path, get resampled pixels and are listed for the caller to redraw. Layer pixels are checked against the given budget tile by tile; going over throws and nothing is kept. Target tiles are computed in parallel (each from the source only) and stored in order, so the bytes do not depend on the number of threads.

## Reference compositing and graph boundary

Layer blend modes cover Photoshop's set except Dissolve (26 modes: separable ones with Photoshop's formulas, including its soft light, and the non-separable Hue/Saturation/Color/Luminosity with the W3C formulas), combined by W3C source-over with correct partial-alpha terms; clipping and adjustment layers use the same blend colour. The GPU tile compositor implements the same formulas. Equality with Photoshop's own rendering has not been measured. Composition is performed in stored RGB space and rounded to RGBA8 after each layer. This is a reference raster path, not proof of PSD application matching. Normal-channel pixels can be retained and inspected, but this core does not implement tangent-vector normal blending.

`DependencyGraph` stores typed declarations for later effect/anchor evaluation. Connections enforce same texture set, input type, optional semantic channel, one source per input, acyclicity, and lower-layer/earlier-effect order. Invalid node/layer-order changes are atomic. Referenced deletion is rejected. Invalidation increments source and downstream revisions, including hidden consumers. Actual filter/generator execution, halo-aware dependency scheduling (display-side tile change tracking exists: `ChangeSerial` / `TryGetChangedTiles`), graphs in native persistence, multi-channel atomic strokes, masks and pressure-curve editing are later work.
