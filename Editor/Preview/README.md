# G1 isolated static surface preview

`IsolatedModelPreview` owns a `PreviewRenderUtility`, newly constructed MeshFilter/MeshRenderer-only GameObjects in its preview scene, transformed Mesh copies, and private Material copies. It never instantiates a supplied GameObject/Prefab, copies user behaviours, changes a source material/mesh/import setting, or uses global Physics picking. Disposal destroys owned objects/meshes/materials and calls `PreviewRenderUtility.Cleanup()`; call it on EditorWindow disable/reload. Source transform translation is removed as a common snapshot origin, while its scale, rotation and child transforms remain represented.

## Contract

- `Load(GameObject)` builds one immutable renderer snapshot and returns a diagnostic report. `LoadDemoMesh()` builds a tool-owned cube with six separated UV islands and no source assets.
- `Render(Rect)` uses an opaque neutral shader by default. `LitPreview` toggles simple neutral lighting; the neutral view is not lilToon, source-shader, transparency/cutout, custom deformation or SRP parity.
- `Shading = Material` draws each slot with a private copy of its source material instead (`PreviewMaterialView`; the copy follows the source's dirty count and shader, and `MaterialEdits` are layered on it). `SetMaterialChannels` gives each slot its straight-RGBA composites and the Normal output; only properties in the slot's `MaterialBinding` receive them (lilToon: LilToonAdapter's verified mapping; built-in Standard: `_MainTex`, `_BumpMap` + `_NORMALMAP`, `_EmissionMap` + `_EMISSION`, `_MetallicGlossMap` R = Metallic / A = 1 − Roughness + `_METALLICGLOSSMAP`, `_ParallaxMap` + `_PARALLAXMAP`; any other shader: Color into the main texture). Data channels are value × alpha, Roughness for lilToon is 1 − value, Color/Emission are bound as they are in Gamma and copied into an sRGB RenderTexture in Linear. A missing/erroring shader, an unverified render pipeline or an unusable packing shader keeps the slot neutral (`MaterialReason`).
- `Scene` (`PreviewSceneSettings`) sets the light direction/intensity/colour, the ambient and the background for both views (preview lights and the neutral shader's `_PreviewLight*` vectors only; `RenderSettings` and scene lights are untouched). `ViewFrom` frames the model from a preset or an angle.
- `TryPick(Rect, Vector2, out SurfaceHit)` returns preview-space position, face normal, UV0, barycentric weights, renderer ID, global flattened material/submesh slot and snapshot revision. GUI points become normalized viewport coordinates, with Y reversed; there is no screen/HiDPI pixel guessing.
- `SetPaintTexture(Texture, int)` only updates private materials. A negative slot applies the texture to all slots; otherwise nonselected slots return to their source texture/color copies. Paint always targets the hit's exact global material slot.
- `BuildSurfaceDabs` returns individual bottom-left-origin texture pixel coordinates and coverage. Apply these directly to the existing stroke; never interpolate UV coordinates between hits.
- `WorldRadiusToGuiPoints(Vector3, float)` reads the current camera/viewport for screen-space input resampling. It does not mutate the camera.
- Navigation is RMB/Alt-LMB orbit, MMB/Shift-RMB pan, wheel zoom. Disable navigation during strokes and call `CancelNavigation` on focus loss.
- A new load invalidates older hits by snapshot revision. Caller must terminate/cancel input across model, material, document, camera or pose changes.

## What surface projection actually does

1. A median BVH finds triangle intersections on the same transformed geometry that is rendered.
2. Adjacency joins coincident geometric edges only within the same renderer/material slot. This connects duplicated UV-seam vertices. Quantization uses a fixed 0.000001 preview-unit tolerance; it is not a general semantic seam reconstruction algorithm. Nonmanifold edges are not traversed.
3. A spherical world-space footprint visits nearby connected front-facing triangles and clips each candidate UV bounding box using the world-to-UV gradients.
4. Pixel-center barycentric coordinates recover world positions. Distance falloff produces coverage, while a per-texel two-sided opaque BVH ray from the preview camera rejects occluded texels. Identity checks also reject an unrelated foreground triangle even within numerical distance tolerance. Adjacent shared-edge hits are allowed.
5. Multiple triangles contributing to the same texture pixel combine with maximum coverage, preventing double addition along shared edges.

This is a CPU spherical footprint prototype, not a geodesic brush, GPU brush backend, Burst job system or production throughput claim. It does not yet implement supersampled edge antialiasing, UV island padding/dilation, or surface masks. Coincident/overlapping UVs share texture pixels and cannot receive independent final colors.

## Explicit limitations and rejection

- Enabled readable static triangular meshes only; 250,000 vertices per mesh and 150,000 total input triangles are admission budgets, not measured support claims.
- Unsupported active renderers (including skinned meshes), unreadable meshes, unsupported topology, invalid geometry or an omitted over-budget mesh disable surface painting for the incomplete snapshot. Omitted clothing cannot silently become transparent to the brush.
- Missing UV0 meshes remain visible occluders but cannot receive paint.
- Any UV0 coordinate outside 0–1 disables surface painting. The prototype does not silently wrap, truncate, or approximate repeated/UDIM painting.
- Source texture transforms, material effects, alpha cutouts, shader displacement, bones and BlendShapes are not reproduced by the neutral view. The material view shows the source shader's own effects with the material's tiling/offset, while painting still uses UV0 directly (a note says so when a mapped property is tiled). Pose/BlendShape editing remains future scope.
- All enabled MeshRenderers are extracted; source LODGroup-driven selection, occlusion culling, renderer property blocks and animation are not replicated.
- Preview isolation is within Unity's process and graphics device. Render callbacks and global GPU/resource contention are not sandboxed.

Per dab, default limits are 2,048 visited triangles, 262,144 candidate texels, 131,072 visibility rays, 2,000,000 ray/triangle tests and 4,000,000 BVH node visits. The latter prevent pathological overlapping geometry from escaping the ray-count limit. If a limit is exceeded, `WasClipped` is true, `Pixels` is empty and a diagnostic explains why. The EditorWindow cancels the entire stroke. No timing or responsiveness SLA has been measured.

## Validation status

All geometry tests and the three preview ownership tests run as Unity 2022.3.22f1 EditMode tests (2026-10-02). WindowTests drive the EditorWindow through real IMGUI events and confirm source-asset isolation, cleanup and stroke cancellation on focus loss / reload / play notifications. Still unverified: actual domain reload and play-mode transitions mid-stroke, HiDPI, tablets, SRP, and real GPUs (the devcontainer renders with llvmpipe).

API references checked against Unity 2022.3:
- [PreviewRenderUtility source](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/Inspector/PreviewRenderUtility.cs)
- [Unsupported preview-pipeline flag source](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/Unsupported.bindings.cs)
- [Material.SetShaderPassEnabled](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Material.SetShaderPassEnabled.html)

Unity 2022.3's `PreviewRenderUtility.Render` temporarily changes a public editor pipeline flag without a `finally`. The wrapper preserves and restores it in `finally`, as well as always ending a successfully begun preview. This narrowly guards that known path; it is not proof that arbitrary Editor callback exceptions are isolated.
