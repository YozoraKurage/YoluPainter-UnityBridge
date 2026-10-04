# G1 isolated surface preview

`IsolatedModelPreview` owns a `PreviewRenderUtility`, newly constructed MeshFilter/MeshRenderer-only GameObjects in its preview scene, transformed Mesh copies, and private Material copies. It never instantiates a supplied GameObject/Prefab, copies user behaviours, changes a source material/mesh/import setting, or uses global Physics picking. Disposal destroys owned objects/meshes/materials and calls `PreviewRenderUtility.Cleanup()`; call it on EditorWindow disable/reload. Source transform translation is removed as a common snapshot origin, while its scale, rotation and child transforms remain represented.

## Contract

マテリアルの欄は、ペイント画像を差し込む前の `HideAndDontSave` 複製に `MaterialEditor` を作り、シェーダーの `ShaderGUI` を埋め込みます。通常のプロパティの値は複製へ直接書き込み、元の Undo/Redo を保ちます。編集による差分（テクスチャとタイリング、キーワード、描画順、GI、タグ、パスを含む）は `PreviewMaterialEdits` に記録し、表示用の複製へ渡します。塗ったチャンネルの流し込み先ではペイント画像を優先し、選んだ下地のテクスチャとタイリングは記録に残します。変更は窓の状態として復元し、`.ylp` には入りません。「全部元に戻す」で取り消し、「マテリアルに反映…」は一覧の確認後に元のマテリアルへ書き込み、Unity の Undo で戻せます。保存しないテクスチャの反映は断ります。

標準シェーダーと lilToon 2.3.4 の通常の描画・プロパティ編集を確認しています。インスペクターからのシェーダー差し替えは取り消すので、欄の上の選択を使ってください。ShaderGUI の描画中の例外はその欄で案内し、再試行できます。ShaderGUI が持つプリセット保存やシェーダー全体の設定など、任意の外部書き込み機能を隔離する仕組みではありません。

- `Load(GameObject)` builds one immutable renderer snapshot and returns a diagnostic report. `LoadDemoMesh()` builds a tool-owned cube with six separated UV islands and no source assets. Both raise `Loaded` when they finish (also on failure or with no model, and again when a background preparation finishes), so the owner can bind its texture sets again.
- Material groups (texture sets are per material, like Substance Painter's): a slot is a flattened renderer × submesh; `MaterialGroups` puts the slots that use the same `Material` object into one group (its `Slots`, the `Meshes` that use it), and every slot without a material into one `Unassigned` group, in the order their first slot appears. `MaterialGroupOfSlot(slot)` and `SurfaceTriangle.Material` / `SurfaceHit.Material` give a slot's or triangle's group. The fingerprint, adjacency, dab traversal, UV islands and mesh parts stay per slot; the region kind Material, a UV point's triangle, symmetry and the shape gradient overlay go by group. `Posed` says whether the snapshot was re-baked with a pose or BlendShapes other than the loaded ones.
- `Render(Rect)` draws the 3D picture only when an input of it changed (`ComputeRenderKey` in `IsolatedModelPreview.RenderCache.cs`: pixel size, camera, snapshot revision, a content version bumped by every texture/material/shading setter, `LitPreview`, the scene's values, the symmetry plane, shape gradient and region overlays, a pending shader compile of the material view); otherwise it draws the last picture again. While inputs keep changing it draws at most `FrameRateLimit` times a second (the personal setting, default 60, 0 = none) and `WantsRepaint` tells the window when a held-back change or a finished compile should be drawn. A change outside these inputs needs `InvalidateRender`. `Render(Rect)` uses an opaque neutral shader by default. `LitPreview` toggles simple neutral lighting; the neutral view is not lilToon, source-shader, transparency/cutout, custom deformation or SRP parity.
- `Shading = Material` draws each slot with a private copy of its source material instead (`PreviewMaterialView`; the copy follows the source's dirty count and shader, and `MaterialEdits` are layered on it). `SetMaterialChannels` gives each slot its straight-RGBA composites and the Normal output; only properties in the slot's `MaterialBinding` receive them (lilToon: LilToonAdapter's verified mapping; built-in Standard: `_MainTex`, `_BumpMap` + `_NORMALMAP`, `_EmissionMap` + `_EMISSION`, `_MetallicGlossMap` R = Metallic / A = 1 − Roughness + `_METALLICGLOSSMAP`, `_ParallaxMap` + `_PARALLAXMAP`; any other shader: Color into the main texture). Data channels are value × alpha, Roughness for lilToon is 1 − value, Color/Emission are bound as they are in Gamma and copied into an sRGB RenderTexture in Linear. A missing/erroring shader, an unverified render pipeline or an unusable packing shader keeps the slot neutral (`MaterialReason`).
- `SetMaterialChoice(slot, material, routes)` makes the material view show that slot with a copy of `material` instead of its own (`ViewMaterial(slot)`; `SourceMaterial(slot)` stays the renderer's own material) and overrides channel routes (`PreviewChannelRoute`, applied by `PreviewMaterialBindings.WithRoutes`). Shaders that are neither verified lilToon nor the built-in Standard get `PreviewMaterialKind.Guessed` routes from property names and flags (an unverified lilToon keeps Color only). Choices are cleared on a new load; the caller sets them again.
- `SetUnlitTextures(textures)` shows each slot's texture with the neutral shader unlit (no light, environment, shadow, tone mapping, normal map); null returns to the shading. Picking, brushes and the cursor do not change.
- `Scene` (`PreviewSceneSettings`) sets the light direction/intensity/colour, the ambient and the background for both views (preview lights and the neutral shader's `_PreviewLight*` vectors only; `RenderSettings` and scene lights are untouched), and the environment, self-shadows and tone mapping (`IsolatedModelPreview.Display.cs`). `Render` no longer calls `PreviewRenderUtility.Render`, which resets the ambient to flat right before drawing; `RenderCamera` repeats its steps (override lighting, lights, pipeline flag) and, with an environment, puts its SH into `RenderSettings.ambientProbe` and its cube into `customReflectionTexture` inside the preview scene's override (restored by `EndPreview`). The defaults (no environment, no shadows, no tone mapping, exposure 0) draw the former picture byte for byte. `EnvironmentTexture` is the Cubemap or latitude-longitude Texture2D for `PreviewEnvironmentSource.Texture` (sampled on the GPU only; unusable ones are refused with a note and the built-in sky is drawn). While an environment or shadows are used the neutral materials draw with `Hidden/YoluPainter/PreviewSurfaceLit` (otherwise the unchanged `PreviewSurface`). `PreviewEnvironment` bakes it (orientation of cube faces and readback rows is checked once per session with a known pattern; refused rather than guessed where neither fits), `PreviewShadowMap` draws the light-depth map (only when the light, bounds or shape revision change), and tone mapping draws the camera into a half-float target and maps it into an 8-bit one. `DisplayNote` says what was asked but could not be shown. `ViewFrom` frames the model from a preset or an angle.
- `TryPick(Rect, Vector2, out SurfaceHit)` returns preview-space position, face normal, UV0, barycentric weights, renderer ID, global flattened material/submesh slot and snapshot revision. GUI points become normalized viewport coordinates, with Y reversed; there is no screen/HiDPI pixel guessing.
- `SetPaintTexture(Texture, int)` only updates private materials. A negative slot applies the texture to all slots; otherwise nonselected slots return to their source texture/color copies. Paint always targets the hit's exact global material slot.
- `BuildSurfaceDabs` returns individual bottom-left-origin texture pixel coordinates and coverage, and each pixel's point on the model (`SurfacePixel.Position`, the texel centre on the triangle that gave the coverage; the stencil projects it to the screen). Apply these directly to the existing stroke; never interpolate UV coordinates between hits.
- `TryGetGuiProjection(Rect, out GuiProjection)` gives the camera's projection for that view as one matrix (`GuiProjection.TryProject`, the same mapping as `TryWorldToGui` without updating the camera per point), for mapping many points at once.
- `WorldRadiusToGuiPoints(Vector3, float)` reads the current camera/viewport for screen-space input resampling. It does not mutate the camera.
- `SymmetryPlane(axis, offset)` is the mirror plane perpendicular to the loaded root's local X/Y/Z axis, moved `offset` scene units along it from the root (`ModelRootPosition` / `ModelRootRotation`; the demo cube and no model use the origin and no rotation; the root's scale does not turn the plane). `BuildSymmetricSurfaceDabs` returns `BuildSurfaceDabs` joined with the mirrored dab (`SurfaceSymmetry`, below). `ShownSymmetryPlane` shows the plane faintly in the preview scene only; picking, brushes and baking never see it.
- `ShownShapeGradient` (`ShapeGradientOverlay`) tints the faces of one material group (every slot of a texture set) with a shape gradient's value (the box, sphere or plane of `ShapeVolume` placed with the root's pose, then the generator's levels and invert; no breakup or blend), computed per pixel from the surface position by `Hidden/YoluPainter/ShapeGradientOverlay` (the same formula as Core, in float), drawn with `PreviewRenderUtility.DrawMesh` over the preview meshes (depth-tested, offset toward the camera, alpha-blended). It needs no mesh map, so the range shows before Position is baked. Preview only: picking, brushes, baking and compositing never see it; without a usable shader it is skipped. `TryGuiRay` and `GizmoView` give the camera ray and projection for `ShapeGizmo` (below).
- `ShapeGizmo` (pure math over `IGizmoView`) gives the shape gradient's outline, handles, hit tests and drags in preview space: move arrows along the root's axes and a centre square moving in the plane facing the camera, rings turning about the root's axes (Ctrl: the supplied `ShapeSnap` rotation increment; a ring seen edge-on turns by the mouse movement along it), and size knobs on the shape's faces along its own axes (one face moves and the opposite stays; Shift moves both; a sphere's knobs change its radius, a plane's two knobs its ramp width). Knobs win a press over arrows and rings; handles whose axis points at the camera (within about 14°) are not shown. Every drag is computed from where it started. The window supplies `EditorSnapSettings.move`, `.scale` and `.rotate`; Ctrl rounds the movement delta in the root axes, the full size delta or the rotation delta. Handles and the outline draw in front of the model (the previous Handles depth state is restored). Direct fill gradients use the same gizmo and cancel/coalescing path. Its own handles, not Unity's `Handles.PositionHandle`: those need a SceneView-style camera, Layout passes and handle shaders, while this view is a PreviewRenderUtility image inside GUI rectangles.
- `SurfaceGeometry.TryFindClosestPoint(point, maxDistance, facing, maxNodeVisits, …)` returns the nearest surface point (BVH, nearer child first, branches farther than the best so far pruned), optionally only on triangles whose normal points the same way as `facing`; it gives up with `exceeded` past its node budget.
- Regions (the polygon fill and the 3D pick of the selection tools and the bucket): `SurfaceRegions.Region(geometry, triangle, kind)` walks from a triangle to its triangle, UV island (triangles sharing a UV edge, endpoints rounded to 1e-6), mesh part (sharing a 3D edge, rounded to 1e-5, across UV seams) or material (every triangle of the material group); islands and parts never cross slots, and three or more triangles on one edge are all joined. `SurfaceRegionIndex` gives the same sets without rebuilding an edge table per call (components found once per geometry by Core's `MeshRegions`, the one place the rule lives, shared with the ID map's mesh parts and UV islands: sorted 64-bit edge keys and union-find; `Key` names a region, `Region` returns its triangles in ascending order), the triangle of a material group under a UV point (a grid over the group's UV bounds; `TrianglesAtUv` returns every overlapping triangle in ascending order) and a region's UV outline (edges that occur once in the region). `SurfaceRegions.Selection` / the window turn a region's UVs into canvas-pixel triangles for `SelectionMask.FromTriangles` and `TriangleFill`. `ShowRegion(key, triangles, color)` tints a region faintly in the preview scene only (PickHighlight.shader, depth-tested, no depth write, Offset -2,-2, translucent faces and a thin wireframe); its mesh is rebuilt only when the key or the geometry changes, and picking, brushes and baking never see it.
- Navigation is RMB/Alt-LMB orbit, MMB/Shift-RMB pan, wheel zoom. Disable navigation during strokes and call `CancelNavigation` on focus loss.
- A new load invalidates older hits by snapshot revision. Caller must terminate/cancel input across model, material, document, camera or pose changes.

- `SurfaceGeometry.TryFindClosestPoint(..., material)` searches only that material group's triangles when `material` is 0 or more.
- `SurfacePathRebind.TryRebind(path, from, to, material, …)` moves a path's points to another snapshot (the model swap): each point's 3D position on `from` goes to the nearest point of `to` on a triangle of that material group facing the same way, within the larger of the path brush's radius and 1% of `from`'s diagonal; one point that cannot be placed refuses the whole path (the window then rasterizes it). Positions are compared in both snapshots' space (world minus the root), so the two models are assumed to be placed alike; a posed snapshot is compared in its pose.

## 表示の目

`SetVisibility` は所有する描画用メッシュのサブメッシュのインデックスだけを空にし、レンダラー単位なら描画を無効にする。入力元のアセットは変えない。描画と影のキャッシュは可視性の世代を鍵に含める。`PickingGeometry` の `VisibleView` は三角形番号・BVH・連結性を元の `Geometry` と共有し、レイ・最短点・ダブの受け手と遮蔽物から非表示面を除く。可視性レイのキャッシュも表示の変更を区別する。`Geometry` と属性は常に全体のままで、ベイクと保存したパスの再評価は目の影響を受けない。ポーズで属性を更新する際も、隠れた面の法線と接線を全体から計算する。

## What surface projection actually does

1. A median BVH finds triangle intersections on the same transformed geometry that is rendered.
2. Adjacency joins coincident geometric edges only within the same renderer/material slot. This connects duplicated UV-seam vertices. Quantization uses a fixed 0.000001 preview-unit tolerance; it is not a general semantic seam reconstruction algorithm. Nonmanifold edges are not traversed.
3. A spherical world-space footprint visits nearby connected front-facing triangles and clips each candidate UV bounding box using the world-to-UV gradients.
4. Pixel-center barycentric coordinates recover world positions. Distance falloff produces coverage, while a per-texel two-sided opaque BVH ray from the preview camera rejects occluded texels. Identity checks also reject an unrelated foreground triangle even within numerical distance tolerance. Adjacent shared-edge hits are allowed.
5. Multiple triangles contributing to the same texture pixel combine with maximum coverage, preventing double addition along shared edges.

対称（`SurfaceSymmetry.Build` / `SurfaceRadialSymmetry.Build`）はダブの中心と法線を鏡映・回転し、届く距離の最近点へ置き直す。鏡映面はモデルのルートのローカル X/Y/Z に直交する面とそのずれ。放射状はルートの X/Y/Z 軸のまわりの 2〜16 個（スケールによらずシーンの単位で回す）。組合せは鏡映の後に回転し、最大 N × 2 個。最近点は同じマテリアルの組（テクスチャセット）に限り（同じマテリアルなら別のメッシュでもよい）、別のマテリアルや面の無いコピーは知らせて省く。全コピーの覆いは画素ごとの最大値で合併、同じ中心は省く。

`ignoreVisibility` を有効にすると対称先の足跡はカメラに依存せず、法線が写した法線と同じ側を向く連結三角形へ広がる。可視性のレイは撃たず、裏側・遮蔽されたコピーにも塗る。元のダブは可視性を確かめる従来の足跡のまま。無効なら見えないコピーは `MirrorOutcome.Hidden`。三角形・候補画素・可視性レイ・レイの三角形と節点の予算はコピーの合計、最近点の探索はコピーごとに 1,048,576 節点まで。超過時はダブの覆いを空で返して窓がストローク全体を取り消す。設定はブラシ状態に保存、ストロークの最初の有効なダブで対称を凍結する。鏡映面の表示は既存の `ComputeRenderKey` の入力、放射軸はGUIの重ね表示なのでプレビュー画像の鍵には入れない。

This is a CPU spherical footprint prototype, not a geodesic brush, GPU brush backend, Burst job system or production throughput claim. It does not yet implement supersampled edge antialiasing, UV island padding/dilation, or surface masks. Coincident/overlapping UVs share texture pixels and cannot receive independent final colors.

## Explicit limitations and rejection

- 有効な静的メッシュと、CPU でベイクしたスキンメッシュの三角形を扱う。エディタの `MeshUtility.AcquireReadOnlyMeshData` で Read/Write が無効でも読み取る。 250,000 vertices per mesh and 150,000 total input triangles are admission budgets, not measured support claims.
- Unsupported active renderers, meshes the Editor cannot read, unsupported topology, invalid geometry or an omitted over-budget mesh disable surface painting for the incomplete snapshot. Omitted clothing cannot silently become transparent to the brush.
- Missing UV0 meshes remain visible occluders but cannot receive paint.
- Any UV0 coordinate outside 0–1 disables surface painting. The prototype does not silently wrap, truncate, or approximate repeated/UDIM painting.
- Source texture transforms, material effects, alpha cutouts, shader displacement, shader-driven vertex motion is not reproduced by the neutral view. Bones and BlendShapes use the CPU-baked snapshot. The material view shows the source shader's own effects with the material's tiling/offset, while painting still uses UV0 directly (a note says so when a mapped property is tiled).
- All enabled MeshRenderers are extracted; source LODGroup-driven selection, occlusion culling, renderer property blocks and animation are not replicated.
- Preview isolation is within Unity's process and graphics device. Render callbacks and global GPU/resource contention are not sandboxed.

Per dab, default limits are 2,048 visited triangles, 262,144 candidate texels, 131,072 visibility rays, 2,000,000 ray/triangle tests and 4,000,000 BVH node visits. The latter prevent pathological overlapping geometry from escaping the ray-count limit. If a limit is exceeded, `WasClipped` is true, `Pixels` is empty and a diagnostic explains why. The EditorWindow cancels the entire stroke. No timing or responsiveness SLA has been measured.

## Validation status

All geometry tests and the three preview ownership tests run as Unity 2022.3.22f1 EditMode tests (2026-10-02). WindowTests drive the EditorWindow through real IMGUI events and confirm source-asset isolation, cleanup and stroke cancellation on focus loss / reload / play notifications. Still unverified: actual domain reload and play-mode transitions mid-stroke, HiDPI, tablets, SRP, and Windows D3D11. The container GPU checks use OpenGL.

API references checked against Unity 2022.3:
- [PreviewRenderUtility source](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/Inspector/PreviewRenderUtility.cs)
- [Unsupported preview-pipeline flag source](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/Unsupported.bindings.cs)
- [Material.SetShaderPassEnabled](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Material.SetShaderPassEnabled.html)

Unity 2022.3's `PreviewRenderUtility.Render` temporarily changes a public editor pipeline flag without a `finally`. The wrapper preserves and restores it in `finally`, as well as always ending a successfully begun preview. This narrowly guards that known path; it is not proof that arbitrary Editor callback exceptions are isolated.

3D のホバーはポリゴン塗りつぶし・通常の選択・バケツで同じ範囲索引を使う。ID 選択と Generator の ID スポイトは、クリックしたテクセルの RGB と許容幅で焼いた ID マップをシェーダー内で照合する。三角形の中心の色で近似せず、高ポリからの色や重複 UV も実際の選択と同じ画素を強調する。ID のテクスチャ・色・許容幅も描画の鍵に入り、同じ範囲／色の中で動くあいだは前の絵を再利用する。色だけ変えてもメッシュを作り直さず、ID のテクスチャはベイクが替わったときだけ作る。前のテクスチャを保持した置き換えと作業画素を含め、予算を超える強調は描かない。

2D の重複 UV は範囲ごとの候補を提示し、Tab／Shift+Tab・右クリック・オプションバーから切り替える。候補のホバーとクリック・ドラッグは同じ範囲を使い、候補の変更自体は Undo に入れない。共有画素の最終色を面ごとに分離することはできない。

## 大きなモデルの準備

新規プロジェクトのダイアログで用意したプレビューは、モデルの指紋を確かめて作成先へ渡す。同じ形・UV・姿勢・スロットなら読み直さない。元のメッシュ、インポート設定、保存済みのモデルの GUID は変更しない。[エディタの読み取り用 API](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/MeshUtility.AcquireReadOnlyMeshData.html) は `isReadable` の検査を省略する。

`Load` は同期の契約を保つ。窓からの大きなモデルの読込みは、表示用のメッシュと三角形を主スレッドで写し、隣り合わせと BVH を別スレッドで作る。完成までは `Geometry` を公開せず、`CanPaint` は false。進捗・取消・再試行を表示し、取消の後もモデルの表示と2Dの操作を残す。リロード、Play への移行、破棄、別の読込みの後には古い結果を採用しない。元のスクリプトは生成しない。

幾何の頂点は従来と同じ量子化で溶接し、辺の初出順で隣り合わせを作る。BVH の構築は境界と分割中心を一度だけ計算し、中央値で分ける。非多様体の辺、マテリアルスロットの境界、UV の扱いと予算の拒否は従来どおり。完全に同距離で重なる面について、旧 BVH の葉内の選択順は保証しない。

ほかのテクスチャセットの CPU 表示と照明は、表示のフレーム予算で順に準備する。取消と再開ができ、正本と Undo は変わらない。予算は仕事の区切りで見るため、1 セットの合成、メッシュの複製・ベイク、GPU の転送・最初の描画に掛かる時間を厳密には制限しない。実モデル、すべての解像度やドライバーに対する応答時間の保証ではない。

アセットのパネルの球のサムネイルは Core の `SmartPreview` が決定的に描く見本で、3D ビューとは別の小さな評価である。
法線マップの取り込み用シェーダー `Hidden/YoluPainter/NormalResourceReadback` は Unity の `UnpackNormal` で GPU 用の詰め方を復号し、
単位 XYZ を RGB・不透明の RGBA8 として返す。元のテクスチャと取り込み設定には触れない。使える GPU とシェーダーが無い環境では理由を出して断る。

## GPU の資源と読み戻しの寿命

表示の合成・ノーマル出力・サムネイルは CPU の正本から作る派生データで、描画のたびに GPU から正本を読み戻さない。
読み取りが許されていない画像の取り込みは、一時の RenderTexture に写して `ReadPixels` で同期して読む。RGBA8 の転送を最大 1 MiB の行の束に分け、CPU の読み戻し台を再利用する。CPU 側のテクスチャの確保や
転送に失敗した場合も、一時の描画先を返し、以前の `RenderTexture.active` を戻す。環境の向きと環境光の確認も同じ解放の契約を持つ。
1 MiB は 1 回の転送と CPU の読み戻し台の大きさで、画像全体の描画先と結果の画素配列は画像のサイズに応じて確保する。ノーマル資源の復号にも同じ分割を使う。

メッシュマップの GPU ベイクは `ComputeBuffer.GetData` の完了を待ってからバッファを再利用する。仕事と結果の台は小さく始め、実際の Dispatch が必要とする容量まで増やす（最大容量の予算は準備時に確認する）。再準備は以前の組を手放し、
準備が失敗した場合は途中で作ったものも解放して CPU のベイクへ切り替える。古い組で処理を続けない。
現在のこれらの経路は `AsyncGPUReadback` の未完了要求や、所有する `NativeArray` を持たない。

合成器のキャッシュ予算は製品が所有する写しの量を数える。グループを CPU で合成する経路へ切り替えて GPU の写しを捨てる際も、その量を引く。
Unity やグラフィックスドライバーの内部プールを含むプロセス全体の GPU メモリ上限ではない。資源を破棄した直後に OS の計器が同じ量だけ減ることは保証しない。

## クローンと指先の表面参照

`SurfacePixel` は最大の被覆率を与えた三角形と表面位置を保つ。同率は最初の面を採用し、重複 UV の画素は1回だけ描く。`BuildSamplingChart` は同じレンダラー・マテリアルの幾何隣接辺を局所的に展開する。クローンは指定した面と描き始めた面の局所座標を対応付け、指先は前の描点までの表面のずれを読み元に足す。鏡映した UV でも画像上の向きに依存しない。対称との併用は、対称先ごとの参照と運動を定義していないため、描き始める前に理由を表示して断る。ステンシルは書き込み先の表面位置を画面へ投影して量だけを掛ける。補間点が UV の三角形を越えると隣接面に運び、島の余白を読まない。面の端の欠けた補間点は残りの重みで正規化する。

非連結の面・別のレンダラーやマテリアル・非多様体の辺を参照経路として横断しない。指先が非連結の面へ移ると拾い直す。種の面から幅優先で展開し、閉路は最初の経路を採るため、強い曲率のある閉じた面全体での一意な測地線座標や Substance Painter の画素一致は保証しない。3D のクローンは面の法線間の最短回転で横軸を合わせる（任意の回転・拡大縮小の指定はない）。既定の局所展開は2048三角形、1つのチャートの参照探索は200万三角形照合まで。名目サイズはストローク予算にも数え、超過は部分的に描かずストロークを取り消す。写し元と位置合わせはモデル・文書・レイヤー・マスク・解像度に結び付いた一時状態で、保存しない。
