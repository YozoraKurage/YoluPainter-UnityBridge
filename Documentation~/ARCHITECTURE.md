# Architecture and invariants

## Source and adapters

`Runtime/Core` is engine-independent C#8 using System only. Unity compiles it as an editor-only assembly with no engine references (`noEngineReferences`); its tests run as ordinary Unity EditMode tests. Its source archive is RGBA8 straight alpha, bottom-left row order. Color and emission currently retain untagged encoded samples; no ICC transform, HDR or Photoshop rendering equivalence is claimed. Scalar channel UI replicates a scalar into RGB. Normal currently stores bytes and is not a validated normal-vector layer compositor.

The authoritative CPU brush is the correctness/reference backend for this checkpoint. It allocates only touched sparse tiles; uniform and zero tiles have compact representations. Before/after tile references use copy-on-write so one stroke can be canceled or undone without brush replay. Tile exports are detached arrays; UI never changes source tile arrays behind the history service. Source and history budgets are payload bytes, not total managed heap or OS memory measurements.

`Editor/TexturePaintWindow` uses one IMGUI event route with `Event.pressure`, explicit mouse capture, pressure curve, and fallback semantics provided by Unity. A stroke freezes its brush. Escape, focus loss, reload and play transition roll back the active stroke. Structural controls and camera navigation are disabled during painting. Rendering can skip frames; committed dab samples are not discarded. The 2D reference sampler is arc-distance based. The surface adapter resamples screen motion, rerays visible triangles and rasterizes surface coverage in UV, never draws a line between distant UV islands.

## GPU status

`TileGpuCompositor` holds a composite texture, a single layer upload tile, and two ping-pong work tiles. It recomposites only the tiles `PaintDocument.TryGetChangedTiles` reports as changed since its last update, with one layer at a time resident; it does not allocate a full GPU texture for every layer. Layers without a tile are skipped (exact, since blending a transparent source is the identity). Structural changes (layer order, visibility, opacity, blend, enabled channels), a channel switch or a different document recomposite every occupied/previously occupied tile; at 4K that is still seconds and needs a layered cache. Work tiles use point sampling: the second pass reads the first pass's result, and bilinear filtering blended neighbors on llvmpipe. Where `Graphics.CopyTexture` is unavailable (Unity disables it below OpenGL 4.3 even if the driver has ARB_copy_image), finished tiles are drawn into the composite with a plain copy pass (TileComposite pass 1) instead. The CPU fallback (no usable GPU, or a shader with compile errors per `ShaderUtil.ShaderHasError`) keeps one full-frame byte buffer and rewrites only changed tiles. Effects with halos and graph-driven dependency scheduling are not handled. Source byte precision remains independent of GPU result precision.

The change journal (`ChangeSerial` / `TryGetChangedTiles`) is display-cache state only: it is not part of undo history or persistence. Pixel writes report through `SparseTileSurface.SetPixelInternal` and `Restore`, which every stroke, cancel, undo and redo goes through; history-external mutations and composite-affecting structure changes force a full answer. It can report tiles that changed and changed back.

`OrderedBrush.shader` and `GpuBrushProbe` are an explicit G0 GPU-stamp experiment. They are not part of the authoritative brush path. The probe reports error against the CPU reference after test-only synchronous readback. Runtime GPU failures do not replace CPU source. Shader correctness, device orientation, project color-space display, copy support and graphics-driver behavior remain Unity gates.

## Preview ownership

`IsolatedModelPreview` reads supported source renderers, copies Mesh objects, transforms snapshot geometry, and builds only tool-owned MeshFilter/MeshRenderer objects in PreviewRenderUtility. It never instantiates a user's GameObject/Prefab or scripts. It owns its mesh/material lifecycle. All intersections use that snapshot, not global Physics. The neutral opaque preview intentionally does not reproduce arbitrary displacement, alpha-test/transparency, or target lilToon material appearance.

`SurfaceGeometry` creates adjacency using quantized geometric edge positions, so distinct UV vertices can connect across seams. Material slots, connected components, facing tests and camera visibility limit a surface dab. Non-manifold edges stop traversal. Duplicate-position geometry, overlapping UVs, highly folded meshes and tiny scale require further fixtures. UVs outside [0,1] are not treated as UDIM/repeat. Budgets reject the whole dab, so the window cancels that stroke instead of committing incomplete coverage.

## Persistence

`DocumentBinary` serializes version, project/layer IDs, attributes, enabled channels and exact sparse tile RGBA. It rejects unknown versions, duplicate IDs/tiles/channels, impossible dimensions, truncation and excess sizes. Raster data with alpha zero retains RGB. There are no persisted procedural/path nodes yet; the existing dependency validator must be integrated into a versioned graph schema before those features ship.

`GenerationStore` creates a new same-root staging folder, writes and Flush(true)s each file, generates a sorted checksum manifest, rereads/verifies it, moves the directory under generations, checks current expected token+hashes again, then replaces the pointer last. Old generations stay intact. A cooperating-tool lock serializes this tool's saves. Noncooperating external apps are detected but not locked; a hash check is not a transaction lock against all outside writers. Directory fsync and power-loss guarantees differ by platform and are unverified. Failure after pointer replacement can mean a new committed generation despite an interrupted acknowledgement; callers must reload current before retry.

The current UI pauses input during explicit save. It creates an immutable native byte snapshot before writing. This is intentionally honest synchronous prototype behavior, not a claimed asynchronous, lock-free 4K save. PSD encoding and current verification can allocate additional bounded buffers. Recovery stores native source without relying on GPU lifetime. Generations are not auto-pruned yet.

## PSD boundary

The custom codec is deliberately small and bounded, and has no external runtime package. Its DTO pixels are top-down, so `PsdBridge` explicitly flips rows. Supported raster PSD export is separate from native project save; if metadata/blend/mode cannot be represented safely, PSD projection stops. Off-canvas PSD layers are rejected at native import because native v1 lacks off-canvas sparse tiles. Original supported-import bytes remain a protected separate file. The codec does not read linked resources, execute embedded payloads or launch external applications.

The public PSD mode is not an external-application certification. See PSD_COMPATIBILITY.md for structural acceptance, original-byte preservation and image fidelity evidence.

## Remaining architecture

Typed DAG validation currently covers stable node identity, type/semantic compatibility, same Texture Set, layer/effect order and cycle prevention. This is executable validation code, not an implemented material graph editor. Add evaluation scheduling, persisted node parameters, typed mesh maps, anchor-stage outputs, mask/content distinction, stale revisions and deletion/reordering workflows before claiming Generator/Filter/Anchor support.
