using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.Preview
{
    public sealed class PreviewLoadReport
    {
        public int LoadedRendererCount { get; internal set; }
        public int TriangleCount { get; internal set; }
        public bool Success => LoadedRendererCount > 0;
        public bool CanPaint { get; internal set; }
        public readonly List<string> Diagnostics = new List<string>();
        public override string ToString() => string.Join("\n", Diagnostics);
    }

    /// <summary>
    /// G1 prototype. Never instantiates a source GameObject/Prefab or copies scripts.
    /// Reconstructs only MeshFilter/MeshRenderer objects, owned mesh snapshots and material clones. Skinned meshes are posed on
    /// a transform-only copy of the model's hierarchy and drawn from their CPU-baked shape.
    /// Render, pick and paint share one immutable, transformed snapshot. Call Dispose on disable/reload.
    /// </summary>
    public sealed class IsolatedModelPreview : IDisposable
    {
        PreviewLoadOptions loadOptions = new PreviewLoadOptions();
        SurfaceAttributes attributes;
        readonly List<GameObject> objects = new List<GameObject>();
        readonly List<Mesh> meshes = new List<Mesh>();
        readonly List<Material> materials = new List<Material>();
        readonly List<Texture> sourceTextures = new List<Texture>();
        // スロットごとの元のマテリアル（参照だけ。プレビューは複製を描き、元には触れない）。デモキューブは null
        readonly List<Material> sourceMaterials = new List<Material>();
        readonly List<Color> sourceColors = new List<Color>();
        readonly List<string> slotNames = new List<string>();
        // 表示するレンダラーと、そのサブメッシュごとのスロット（描き方を切り替えるときに sharedMaterials を入れ替える）
        readonly List<(MeshRenderer renderer, int[] slots)> slotRenderers = new List<(MeshRenderer, int[])>();
        PreviewMaterialView materialView;
        PreviewShading shading = PreviewShading.Neutral;
        PreviewRenderUtility preview;
        SurfaceGeometry geometry;
        PreviewLoadReport report = new PreviewLoadReport();
        int revision, navigationControl;
        bool navigating, panning, disposed;
        float yaw = 25, pitch = 10, distance = 3;
        Vector3 target;
        Rect lastRect;
        public bool HasModel => geometry != null && geometry.TriangleCount > 0;
        /// <summary>今のスナップショットの幾何（読むだけ）。モデルが無ければ null。</summary>
        public SurfaceGeometry Geometry => geometry;
        /// <summary>今のスナップショットの三角形ごとの頂点の属性（法線・接線・頂点カラー・レンダラー名。<see cref="Geometry"/> と同じ並び）。</summary>
        public SurfaceAttributes Attributes => attributes;
        public bool CanPaint => HasModel && report.CanPaint;
        public Bounds Bounds => geometry != null ? geometry.Bounds : new Bounds(Vector3.zero, Vector3.one);
        public float ModelRadius => Mathf.Max(0.0001f, Bounds.extents.magnitude);
        public float CameraDistance => distance;
        public int SnapshotRevision => revision;
        public int MaterialSlotCount => materials.Count;
        /// <summary>スロットの元のマテリアル（読み込んだモデルの Renderer のもの）。デモキューブや範囲外は null。</summary>
        public Material SourceMaterial(int slot) => slot >= 0 && slot < sourceMaterials.Count ? sourceMaterials[slot] : null;
        public IReadOnlyList<string> MaterialSlotNames => slotNames;
        public IReadOnlyList<string> Diagnostics => report.Diagnostics;
        public SurfaceBrushBudget BrushBudget { get; } = new SurfaceBrushBudget();
        public bool LitPreview { get; set; } = true;
        /// <summary>擬似的なシーン（光・環境光・背景。null なら既定）。描くたびに中立のマテリアルとプレビューの光に入れる。</summary>
        public PreviewSceneSettings Scene { get; set; }
        static readonly PreviewSceneSettings defaultScene = PreviewSceneSettings.Default();
        public float CameraYaw => yaw;
        public float CameraPitch => pitch;

        /// <summary>カメラを (yaw, pitch) の向きにして、モデル全体が入るように置き直す（モデルが無ければ何もしない）。</summary>
        public void ViewFrom(float viewYaw, float viewPitch)
        {
            if (!HasModel) return;
            target = Bounds.center; yaw = viewYaw; pitch = Mathf.Clamp(viewPitch, -89, 89);
            distance = ModelRadius / Mathf.Sin(15 * Mathf.Deg2Rad) * 1.15f;
            UpdateCamera(lastRect.width > 0 ? lastRect : new Rect(0, 0, 500, 500));
        }
        public void ViewFrom(PreviewCameraView view) { var (y, p) = PreviewSceneSettings.CameraAngles(view); ViewFrom(y, p); }

        /// <summary>シーンの設定を中立のマテリアルとプレビューの光・環境光・背景に入れる（プレビューの中だけ。シーンの RenderSettings には触れない）。</summary>
        void ApplyScene()
        {
            var s = Scene ?? defaultScene;
            preview.camera.backgroundColor = s.background;
            var toLight = s.LightDirection;
            if (preview.lights != null && preview.lights.Length > 0 && preview.lights[0] != null)
            {
                var key = preview.lights[0];
                key.transform.rotation = Quaternion.LookRotation(-toLight);
                key.intensity = s.intensity; key.color = new Color(s.lightColor.r * .769f, s.lightColor.g * .769f, s.lightColor.b * .769f, 1);
            }
            preview.ambientColor = new Color(s.ambient.r * .4f, s.ambient.g * .4f, s.ambient.b * .4f, 0);
            var direction = new Vector4(toLight.x, toLight.y, toLight.z, 0);
            var light = new Vector4(.65f * s.intensity * s.lightColor.r, .65f * s.intensity * s.lightColor.g, .65f * s.intensity * s.lightColor.b, 1);
            var ambient = new Vector4(.7f * s.ambient.r, .7f * s.ambient.g, .7f * s.ambient.b, 1);
            foreach (var material in materials)
            {
                material.SetVector("_PreviewLightDir", direction); material.SetVector("_PreviewLight", light); material.SetVector("_PreviewAmbient", ambient);
            }
        }

        // ───────────── 描き方（中立 / マテリアル） ─────────────

        PreviewMaterialView MaterialView => materialView ?? (materialView = CreateMaterialView());
        PreviewMaterialView CreateMaterialView() { var view = new PreviewMaterialView(SourceMaterial); view.Reset(materials.Count); return view; }

        /// <summary>
        /// 3D ビューの描き方。Material では、各スロットを元のマテリアルの複製（元のシェーダー・キーワード・値）で描き、塗った中身は
        /// <see cref="SetMaterialChannels"/> で対応のあるプロパティにだけ入れる。見せられないスロット（元のマテリアルが無い・シェーダーが
        /// 壊れている・SRP など）は中立のまま（理由は <see cref="MaterialReason"/>）。当たり判定・ブラシは描き方によらず同じスナップショット。
        /// </summary>
        public PreviewShading Shading
        {
            get => shading;
            set
            {
                ThrowIfDisposed();
                if (shading == value) return;
                shading = value;
                if (shading == PreviewShading.Neutral) MaterialView.Reset(materials.Count); // 複製と詰めたテクスチャを放す
                ApplyRendererMaterials();
            }
        }
        /// <summary>マテリアルの欄で変えた値（マテリアル表示の複製に重ねる。元のマテリアルには入らない）。</summary>
        public PreviewMaterialEdits MaterialEdits { get => MaterialView.Edits; set => MaterialView.Edits = value; }
        /// <summary>スロットの元のマテリアルの対応（元が変わっていれば決め直す）。範囲外は null。</summary>
        public PreviewMaterialBinding MaterialBinding(int slot) => slot >= 0 && slot < materials.Count ? MaterialView.Binding(slot) : null;
        /// <summary>マテリアル表示で、そのスロットを中立で見せている理由（マテリアルで見せている・中立の表示なら null）。</summary>
        public string MaterialReason(int slot) => shading == PreviewShading.Material ? MaterialView.Reason(slot) : null;
        /// <summary>マテリアル表示でそのスロットを描いている複製（中立なら null）。読むだけにする（欄の変更は <see cref="MaterialEdits"/> へ）。</summary>
        public Material DisplayMaterial(int slot) => shading == PreviewShading.Material ? MaterialView.Display(slot) : null;
        /// <summary>そのスロットを今描いているマテリアル（試験用）。</summary>
        internal Material RenderedMaterial(int slot)
        {
            foreach (var (renderer, slots) in slotRenderers)
            {
                int sub = Array.IndexOf(slots, slot);
                if (sub >= 0) { var shared = renderer.sharedMaterials; return sub < shared.Length ? shared[sub] : null; }
            }
            return null;
        }
        /// <summary>試験用: スロットの、プロパティに入れた詰めたテクスチャ。</summary>
        internal RenderTexture MaterialPackedTexture(int slot, string property) => MaterialView.PackedTexture(slot, property);
        /// <summary>シェーダーのコンパイルを待っているか（マテリアル表示ではコンパイルが済むまで描き直す）。</summary>
        public bool CompilingShaders => shading == PreviewShading.Material && ShaderUtil.anythingCompiling;

        /// <summary>
        /// マテリアル表示に塗った中身を渡す（slots に無いスロットは元のマテリアルのテクスチャのまま）。中立の表示のあいだは何もしない
        /// （中立の表示は <see cref="SetPaintTextures"/> と <see cref="SetNormalTextures"/>）。表示だけで、元のマテリアルには触れない。
        /// </summary>
        public void SetMaterialChannels(IReadOnlyDictionary<int, PreviewSlotChannels> slots)
        {
            ThrowIfDisposed();
            if (shading != PreviewShading.Material) return;
            for (int i = 0; i < materials.Count; i++)
            {
                PreviewSlotChannels painted = null;
                if (slots != null) slots.TryGetValue(i, out painted);
                MaterialView.Bind(i, painted);
            }
            ApplyRendererMaterials();
        }

        /// <summary>レンダラーのマテリアルを今の描き方に合わせる（マテリアル表示で見せられるスロットは複製、それ以外は中立）。</summary>
        void ApplyRendererMaterials()
        {
            foreach (var (renderer, slots) in slotRenderers)
            {
                if (renderer == null) continue;
                var shared = new Material[slots.Length];
                for (int sub = 0; sub < slots.Length; sub++)
                {
                    int slot = slots[sub];
                    var display = shading == PreviewShading.Material ? MaterialView.Display(slot) : null;
                    shared[sub] = display != null ? display : materials[slot];
                }
                renderer.sharedMaterials = shared;
            }
        }

        public PreviewLoadReport Load(GameObject source, PreviewLoadOptions options = null)
        {
            ThrowIfDisposed();
            ClearModel(); revision++; report = new PreviewLoadReport(); loadOptions = options ?? new PreviewLoadOptions();
            if (source == null) { report.Diagnostics.Add("Choose a model GameObject or Prefab to load."); return report; }
            EnsurePreview();
            var shader = Shader.Find("Hidden/YoluPainter/PreviewSurface");
            if (shader == null) { report.Diagnostics.Add("The package's neutral preview shader could not be loaded."); return report; }
            bool incomplete = false;
            Vector3 origin = loadOptions.Origin ?? source.transform.position;
            loadOrigin = origin;
            ModelRootPosition = source.transform.position - origin; ModelRootRotation = source.transform.rotation;
            foreach (var unsupportedRenderer in source.GetComponentsInChildren<Renderer>(true))
            {
                if (unsupportedRenderer is MeshRenderer || unsupportedRenderer is SkinnedMeshRenderer || !IsActiveRenderer(unsupportedRenderer, source.transform)) continue;
                incomplete = true;
                report.Diagnostics.Add(unsupportedRenderer.name + ": unsupported renderer type " + unsupportedRenderer.GetType().Name + " was not copied. Surface painting is disabled because it may occlude the target.");
            }
            long triangleTotal = 0;
            foreach (var skin in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!IsActiveRenderer(skin, source.transform)) continue;
                var input = skin.sharedMesh;
                if (input == null) continue;
                if (!CheckInput(skin, input, ref incomplete, ref triangleTotal)) continue;
                SkinnedSnapshot snapshot = null;
                try
                {
                    // 骨は Transform だけを複製した階層で動かす（元のモデルは Instantiate しない）。描画は焼いたメッシュで行う
                    if (skeleton == null) { skeleton = new TransformCopy(source.transform); preview.AddSingleGO(skeleton.Root); }
                    snapshot = SkinnedSnapshot.Create(skeleton, skin);
                    var baked = snapshot.Bake();
                    var t = snapshot.Renderer.transform; var scale = t.lossyScale;
                    if (!AddEntry(skin, baked, Matrix4x4.TRS(t.position, t.rotation, Vector3.one), scale.x * scale.y * scale.z < 0, origin, shader, ref incomplete, snapshot)) snapshot.Dispose();
                }
                catch (Exception exception)
                {
                    incomplete = true; snapshot?.Dispose();
                    report.Diagnostics.Add(skin.name + ": could not build a skinned snapshot: " + exception.Message);
                }
            }
            foreach (var renderer in source.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!IsActiveRenderer(renderer, source.transform)) continue;
                var filter = renderer.GetComponent<MeshFilter>(); var input = filter != null ? filter.sharedMesh : null;
                if (input == null) continue;
                if (!CheckInput(renderer, input, ref incomplete, ref triangleTotal)) continue;
                var matrix = renderer.localToWorldMatrix;
                AddEntry(renderer, input, matrix, matrix.determinant < 0, origin, shader, ref incomplete, null);
            }
            var triangles = BuildTriangles();
            var animator = source.GetComponentInChildren<Animator>(true);
            if (skeleton != null && animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman && skeleton[animator.transform] != null)
            { humanAvatar = animator.avatar; humanRoot = animator.transform; }
            try
            {
                if (triangles.Count > 0)
                {
                    geometry = new SurfaceGeometry(triangles, revision);
                    report.TriangleCount = geometry.TriangleCount;
                    if (geometry.NonManifoldEdgeCount > 0) report.Diagnostics.Add("Nonmanifold edges are not crossed by the surface brush.");
                    FrameModel();
                }
            }
            catch (Exception exception)
            { incomplete = true; geometry = null; report.Diagnostics.Add("Invalid mesh snapshot: " + exception.Message); }
            report.CanPaint = HasModel && !incomplete;
            MaterialView.Reset(materials.Count); ApplyRendererMaterials();
            report.Diagnostics.Add("G1 uses UV0, 0–1 UVs, static readable meshes and an opaque neutral shader. UV tiling, alpha cutouts, shader displacement, exact lilToon/SRP appearance and shader-driven vertex motion are not represented. Skinned meshes show their current pose and BlendShapes, baked on the CPU.");
            report.Diagnostics.Add("Overlapping UVs share pixels. Duplicate-position edges may join seams; surface filtering cannot make overlapping UVs independent.");
            if (incomplete) report.Diagnostics.Add("Surface painting is disabled for this incomplete snapshot, so omitted geometry cannot silently allow painting through clothes. Choose a supported static-mesh root.");
            return report;
        }

        /// <summary>Loads a tool-created cube with six separated UV islands. No source assets or scene objects are needed.</summary>
        public PreviewLoadReport LoadDemoMesh()
        {
            ThrowIfDisposed(); ClearModel(); revision++; report = new PreviewLoadReport(); EnsurePreview();
            var shader = Shader.Find("Hidden/YoluPainter/PreviewSurface");
            if (shader == null) { report.Diagnostics.Add("The package's neutral preview shader could not be loaded."); return report; }
            try
            {
                var vertices = new List<Vector3>(); var uvs = new List<Vector2>(); var indices = new List<int>();
                // Each face has independent UV vertices; geometric duplicate-position edges form the seams.
                AddDemoFace(vertices, uvs, indices, new Vector3(-.5f,-.5f,-.5f), new Vector3(-.5f,.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,.5f,-.5f));
                AddDemoFace(vertices, uvs, indices, new Vector3(.5f,-.5f,.5f), new Vector3(.5f,.5f,.5f), new Vector3(-.5f,-.5f,.5f), new Vector3(-.5f,.5f,.5f));
                AddDemoFace(vertices, uvs, indices, new Vector3(-.5f,-.5f,.5f), new Vector3(-.5f,.5f,.5f), new Vector3(-.5f,-.5f,-.5f), new Vector3(-.5f,.5f,-.5f));
                AddDemoFace(vertices, uvs, indices, new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(.5f,-.5f,.5f), new Vector3(.5f,.5f,.5f));
                AddDemoFace(vertices, uvs, indices, new Vector3(-.5f,.5f,-.5f), new Vector3(-.5f,.5f,.5f), new Vector3(.5f,.5f,-.5f), new Vector3(.5f,.5f,.5f));
                AddDemoFace(vertices, uvs, indices, new Vector3(-.5f,-.5f,.5f), new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,.5f), new Vector3(.5f,-.5f,-.5f));
                var mesh = new Mesh { name = "Texture painter UV seam demo", hideFlags = HideFlags.HideAndDontSave };
                meshes.Add(mesh); mesh.SetVertices(vertices); mesh.SetUVs(0, uvs); mesh.SetTriangles(indices, 0); mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
                var material = new Material(shader) { name = "Seam demo (preview only)", hideFlags = HideFlags.HideAndDontSave };
                materials.Add(material); sourceTextures.Add(null); sourceColors.Add(Color.white); sourceMaterials.Add(null); slotNames.Add("Seam cube / 0 / Neutral");
                var go = new GameObject("Texture painter seam cube (preview only)") { hideFlags = HideFlags.HideAndDontSave };
                objects.Add(go); preview.AddSingleGO(go);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material; slotRenderers.Add((renderer, new[] { 0 }));
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                var triangles = new List<SurfaceTriangle>(); var attribute = new SurfaceAttributes.Builder();
                var demoNormals = mesh.normals; var demoTangents = mesh.tangents;
                for (int i = 0; i < indices.Count; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    triangles.Add(new SurfaceTriangle(vertices[a], vertices[b], vertices[c], uvs[a], uvs[b], uvs[c]));
                    attribute.Add(demoNormals, demoTangents, null, a, b, c);
                }
                attributes = attribute.Build(new[] { "Seam cube" });
                geometry = new SurfaceGeometry(triangles, revision);
                report.LoadedRendererCount = 1; report.TriangleCount = geometry.TriangleCount; report.CanPaint = true;
                report.Diagnostics.Add("Demo: a tool-owned cube with six separate UV islands. Paint across a visible cube edge to check seam propagation, then orbit to check that hidden faces stayed unchanged. No source object or asset is created.");
                FrameModel();
                MaterialView.Reset(materials.Count); ApplyRendererMaterials();
            }
            catch (Exception exception)
            {
                ClearModel(); report.CanPaint = false; report.LoadedRendererCount = 0; report.TriangleCount = 0;
                report.Diagnostics.Add("Could not create seam demo: " + exception.Message);
            }
            return report;
        }
        /// <summary>レンダラー 1 つぶんのスナップショット（表示するメッシュ、三角形を作るための頂点・UV・添字）。スキンメッシュは
        /// Skin で焼き直せる。</summary>
        sealed class SnapshotEntry
        {
            public string Name; public Mesh Display; public int RendererIndex;
            public int[] Slots; public int[][] Indices; public Vector2[] Uv; public bool HasUv, Mirrored;
            /// <summary>読み込みの原点を引いたワールド座標。</summary>
            public Vector3[] Vertices;
            /// <summary>表示のメッシュの法線・接線（w 込み）・頂点カラー（無ければ長さ 0）。</summary>
            public Vector3[] Normals; public Vector4[] Tangents; public Color[] Colors;
            public SkinnedSnapshot Skin;
        }
        readonly List<SnapshotEntry> entries = new List<SnapshotEntry>();
        TransformCopy skeleton;
        Vector3 loadOrigin;
        // Humanoid のポーズ用: 元のモデルの Animator の Avatar（アセットへの参照だけ）と、その Animator の位置
        Avatar humanAvatar; Transform humanRoot;

        bool CheckInput(Renderer renderer, Mesh input, ref bool incomplete, ref long triangleTotal)
        {
            if (!input.isReadable)
            { incomplete = true; report.Diagnostics.Add(renderer.name + ": mesh is not CPU-readable. Import settings were not changed."); return false; }
            if (input.vertexCount > loadOptions.MaxVerticesPerMesh)
            { incomplete = true; report.Diagnostics.Add(renderer.name + ": exceeds the vertex budget (" + loadOptions.MaxVerticesPerMesh.ToString("N0") + " per mesh)."); return false; }
            long inputTriangles = 0; bool unsupported = false;
            for (int sub = 0; sub < input.subMeshCount; sub++)
            {
                if (input.GetTopology(sub) != MeshTopology.Triangles) { unsupported = true; break; }
                inputTriangles += (long)input.GetIndexCount(sub) / 3;
            }
            if (unsupported || triangleTotal + inputTriangles > loadOptions.MaxTriangles)
            { incomplete = true; report.Diagnostics.Add(renderer.name + ": unsupported topology or total triangle budget (" + loadOptions.MaxTriangles.ToString("N0") + ")."); return false; }
            triangleTotal += inputTriangles;
            return true;
        }

        /// <summary>メッシュ（静的メッシュはそのもの、スキンメッシュは焼いたもの）を matrix でワールドへ置いたスナップショットを作り、
        /// 表示用の複製（MeshFilter / MeshRenderer）とマテリアルの複製を用意する。失敗したら作りかけを片付けて false。</summary>
        bool AddEntry(Renderer renderer, Mesh input, Matrix4x4 matrix, bool mirrored, Vector3 origin, Shader shader, ref bool incomplete, SkinnedSnapshot skin)
        {
            int firstSlot = materials.Count, firstMesh = meshes.Count, firstObject = objects.Count;
            try
            {
                Mesh mesh = Object.Instantiate(input); // Mesh only: no source GameObject or behaviour is instantiated.
                mesh.name = input.name + " (paint snapshot)"; mesh.hideFlags = HideFlags.HideAndDontSave; meshes.Add(mesh);
                var vertices = mesh.vertices;
                var normalMatrix = matrix.inverse.transpose;
                for (int v = 0; v < vertices.Length; v++) vertices[v] = matrix.MultiplyPoint3x4(vertices[v]) - origin;
                mesh.vertices = vertices;
                var normals = mesh.normals;
                if (normals.Length == vertices.Length)
                {
                    for (int n = 0; n < normals.Length; n++) normals[n] = NormalizeNonzero(normalMatrix.MultiplyVector(normals[n]));
                    mesh.normals = normals;
                }
                var tangents = mesh.tangents;
                if (tangents.Length == vertices.Length)
                {
                    for (int n = 0; n < tangents.Length; n++)
                    {
                        Vector3 tangent = NormalizeNonzero(matrix.MultiplyVector(new Vector3(tangents[n].x, tangents[n].y, tangents[n].z)));
                        tangents[n] = new Vector4(tangent.x, tangent.y, tangent.z, tangents[n].w * (mirrored ? -1 : 1));
                    }
                    mesh.tangents = tangents;
                }
                var uv = mesh.uv; bool hasUv = uv.Length == vertices.Length;
                if (!hasUv) report.Diagnostics.Add(renderer.name + ": missing UV0. It remains an occluder, but cannot receive paint.");
                if (hasUv)
                {
                    foreach (var coordinate in uv)
                    {
                        if (coordinate.x < 0 || coordinate.x > 1 || coordinate.y < 0 || coordinate.y > 1)
                        {
                            incomplete = true;
                            report.Diagnostics.Add(renderer.name + ": UV0 lies outside 0–1. Repeating/UDIM mapping is unsupported; surface painting is disabled rather than silently clipping or wrapping strokes.");
                            break;
                        }
                    }
                }
                var originalMaterials = renderer.sharedMaterials;
                var clonedMaterials = new Material[mesh.subMeshCount];
                var entry = new SnapshotEntry { Name = renderer.name, Display = mesh, RendererIndex = report.LoadedRendererCount, Slots = new int[mesh.subMeshCount], Indices = new int[mesh.subMeshCount][],
                    Uv = uv, HasUv = hasUv, Mirrored = mirrored, Vertices = vertices, Skin = skin };
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    var original = sub < originalMaterials.Length ? originalMaterials[sub] : null;
                    Texture texture = null; Color color = Color.white;
                    if (original != null)
                    {
                        if (original.HasProperty("_MainTex")) texture = original.GetTexture("_MainTex");
                        else if (original.HasProperty("_BaseMap")) texture = original.GetTexture("_BaseMap");
                        if (original.HasProperty("_Color")) color = original.GetColor("_Color");
                        else if (original.HasProperty("_BaseColor")) color = original.GetColor("_BaseColor");
                    }
                    var material = original != null ? new Material(original) : new Material(shader);
                    material.hideFlags = HideFlags.HideAndDontSave;
                    material.name = (original != null ? original.name : "Unassigned") + " (paint preview only)";
                    material.shader = shader; material.renderQueue = -1; material.shaderKeywords = Array.Empty<string>();
                    material.SetOverrideTag("RenderType", "Opaque"); material.SetShaderPassEnabled("Always", true);
                    material.SetTexture("_MainTex", texture != null ? texture : Texture2D.whiteTexture);
                    material.SetTextureScale("_MainTex", Vector2.one); material.SetTextureOffset("_MainTex", Vector2.zero);
                    material.SetColor("_Color", color); material.SetFloat("_PreviewLit", LitPreview ? 1 : 0);
                    int slot = materials.Count;
                    materials.Add(material); sourceTextures.Add(texture); sourceColors.Add(color); sourceMaterials.Add(original);
                    slotNames.Add(renderer.name + " / " + sub + " / " + (original != null ? original.name : "Unassigned"));
                    clonedMaterials[sub] = material;
                    var indices = mesh.GetTriangles(sub);
                    if (mirrored) { for (int i = 0; i < indices.Length; i += 3) { int temp = indices[i + 1]; indices[i + 1] = indices[i + 2]; indices[i + 2] = temp; } mesh.SetTriangles(indices, sub, false); }
                    entry.Slots[sub] = slot; entry.Indices[sub] = indices;
                }
                if (normals.Length != vertices.Length) mesh.RecalculateNormals();
                if (hasUv && tangents.Length != vertices.Length) mesh.RecalculateTangents(); // ノーマルマップの表示に要る
                mesh.RecalculateBounds();
                entry.Normals = mesh.normals; entry.Tangents = mesh.tangents; entry.Colors = mesh.colors;
                var go = new GameObject(renderer.name + " (isolated paint preview)") { hideFlags = HideFlags.HideAndDontSave };
                objects.Add(go); preview.AddSingleGO(go);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var copy = go.AddComponent<MeshRenderer>(); copy.sharedMaterials = clonedMaterials; slotRenderers.Add((copy, (int[])entry.Slots.Clone()));
                copy.shadowCastingMode = ShadowCastingMode.Off; copy.receiveShadows = false;
                copy.lightProbeUsage = LightProbeUsage.Off; copy.reflectionProbeUsage = ReflectionProbeUsage.Off;
                entries.Add(entry);
                report.LoadedRendererCount++;
                return true;
            }
            catch (Exception exception)
            {
                incomplete = true;
                slotRenderers.RemoveAll(r => r.renderer == null || r.slots.Any(slot => slot >= firstSlot));
                DestroyTail(objects, firstObject); DestroyTail(meshes, firstMesh); DestroyTail(materials, firstSlot);
                sourceTextures.RemoveRange(firstSlot, sourceTextures.Count - firstSlot);
                sourceMaterials.RemoveRange(firstSlot, sourceMaterials.Count - firstSlot);
                sourceColors.RemoveRange(firstSlot, sourceColors.Count - firstSlot);
                slotNames.RemoveRange(firstSlot, slotNames.Count - firstSlot);
                report.Diagnostics.Add(renderer.name + ": could not build safe mesh snapshot: " + exception.Message);
                return false;
            }
        }

        // ───────────── ポーズと BlendShape（複製の骨と複製のメッシュだけを動かす） ─────────────

        /// <summary>BlendShape 1 つ（スキンメッシュの番号と、その中の番号）。</summary>
        public readonly struct PoseBlendShape { public readonly int Mesh, Index; public readonly string Label; public PoseBlendShape(int mesh, int index, string label) { Mesh = mesh; Index = index; Label = label; } }

        IEnumerable<SkinnedSnapshot> Skins { get { foreach (var e in entries) if (e.Skin != null) yield return e.Skin; } }
        public bool HasSkinnedMeshes { get { foreach (var _ in Skins) return true; return false; } }
        public IReadOnlyList<PoseBlendShape> BlendShapes
        {
            get
            {
                var list = new List<PoseBlendShape>(); int m = 0;
                foreach (var skin in Skins) { for (int i = 0; i < skin.BlendShapeCount; i++) list.Add(new PoseBlendShape(m, i, skin.Name + " / " + skin.BlendShapeName(i))); m++; }
                return list;
            }
        }
        SkinnedSnapshot SkinAt(int mesh) { int m = 0; foreach (var skin in Skins) if (m++ == mesh) return skin; throw new ArgumentOutOfRangeException(nameof(mesh)); }
        public float GetBlendShapeWeight(PoseBlendShape shape) => SkinAt(shape.Mesh).GetBlendShapeWeight(shape.Index);
        /// <summary>重みを変える。形に反映するのは <see cref="ApplyPose"/> のとき。</summary>
        public void SetBlendShapeWeight(PoseBlendShape shape, float weight)
        {
            if (float.IsNaN(weight) || float.IsInfinity(weight)) throw new ArgumentOutOfRangeException(nameof(weight));
            SkinAt(shape.Mesh).SetBlendShapeWeight(shape.Index, weight);
        }
        /// <summary>アニメーションクリップの time 秒の姿勢を複製の骨に置く。汎用（Generic）はパスで骨を動かし、Humanoid は元のモデルの
        /// Avatar で筋肉の値から骨の向きを決める（HumanPoseHandler。複製の骨だけを動かす）。形に反映するのは <see cref="ApplyPose"/> のとき。</summary>
        public void SamplePose(AnimationClip clip, float time)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (skeleton == null) throw new InvalidOperationException("The loaded model has no skinned mesh to pose.");
            time = Mathf.Clamp(time, 0, clip.length);
            if (!clip.humanMotion) { clip.SampleAnimation(skeleton.Root, time); return; }
            if (humanAvatar == null) throw new InvalidOperationException("This is a Humanoid clip, but the loaded model has no valid Humanoid Avatar on its Animator.");
            using (var handler = new HumanPoseHandler(humanAvatar, skeleton[humanRoot]))
            {
                var pose = new HumanPose(); handler.GetHumanPose(ref pose);
                Vector3 body = pose.bodyPosition; Quaternion rotation = pose.bodyRotation; bool hasRotation = false;
                foreach (var binding in UnityEditor.AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(Animator)) continue;
                    var curve = UnityEditor.AnimationUtility.GetEditorCurve(clip, binding); if (curve == null) continue;
                    float value = curve.Evaluate(time); string name = binding.propertyName;
                    switch (name)
                    {
                        case "RootT.x": body.x = value; continue;
                        case "RootT.y": body.y = value; continue;
                        case "RootT.z": body.z = value; continue;
                        case "RootQ.x": rotation.x = value; hasRotation = true; continue;
                        case "RootQ.y": rotation.y = value; hasRotation = true; continue;
                        case "RootQ.z": rotation.z = value; hasRotation = true; continue;
                        case "RootQ.w": rotation.w = value; hasRotation = true; continue;
                    }
                    int muscle = MuscleIndex(name);
                    if (muscle >= 0) pose.muscles[muscle] = value;
                }
                pose.bodyPosition = body;
                if (hasRotation) { float length = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w); if (length > 1e-6f) pose.bodyRotation = new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length); }
                handler.SetHumanPose(ref pose);
            }
        }
        /// <summary>Humanoid のクリップのカーブの名前に当たる筋肉の番号（HumanTrait.MuscleName）。指はクリップでは
        /// "LeftHand.Thumb.1 Stretched"、筋肉名では "Left Thumb 1 Stretched" と書くので読み替える。無ければ −1。</summary>
        internal static int MuscleIndex(string curveName)
        {
            int index = Array.IndexOf(HumanTrait.MuscleName, curveName);
            if (index >= 0 || curveName == null) return index;
            string name = curveName.StartsWith("LeftHand.", StringComparison.Ordinal) ? "Left " + curveName.Substring(9)
                : curveName.StartsWith("RightHand.", StringComparison.Ordinal) ? "Right " + curveName.Substring(10) : null;
            return name == null ? -1 : Array.IndexOf(HumanTrait.MuscleName, name.Replace('.', ' '));
        }
        /// <summary>Humanoid のクリップでポーズできるモデルか（元のモデルの Animator に有効な Humanoid の Avatar がある）。</summary>
        public bool HasHumanoidAvatar => humanAvatar != null;
        /// <summary>骨と BlendShape を読み込んだときの状態に戻す。形に反映するのは <see cref="ApplyPose"/> のとき。</summary>
        public void ResetPose() { skeleton?.Reset(); foreach (var skin in Skins) skin.ResetBlendShapes(); }
        /// <summary>今の骨と BlendShape でスキンメッシュを焼き直し、表示と当たり判定の形を新しい世代（<see cref="SnapshotRevision"/>）に
        /// 切り替える。UV と三角形の並びは変わらない。ストロークの最中に呼ばない（呼ぶ側が止める）。</summary>
        public bool ApplyPose()
        {
            ThrowIfDisposed();
            if (!HasSkinnedMeshes) return false;
            foreach (var e in entries)
            {
                if (e.Skin == null) continue;
                var baked = e.Skin.Bake(); var t = e.Skin.Renderer.transform;
                var matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one); var normalMatrix = matrix.inverse.transpose;
                var vertices = baked.vertices; var normals = baked.normals;
                if (vertices.Length != e.Vertices.Length) throw new InvalidOperationException("The baked mesh changed its vertex count.");
                for (int v = 0; v < vertices.Length; v++) vertices[v] = matrix.MultiplyPoint3x4(vertices[v]) - loadOrigin;
                e.Vertices = vertices; e.Display.vertices = vertices;
                if (normals.Length == vertices.Length) { for (int n = 0; n < normals.Length; n++) normals[n] = NormalizeNonzero(normalMatrix.MultiplyVector(normals[n])); e.Display.normals = normals; }
                else e.Display.RecalculateNormals();
                if (e.HasUv) e.Display.RecalculateTangents();
                e.Display.RecalculateBounds();
                e.Normals = e.Display.normals; e.Tangents = e.Display.tangents;
            }
            revision++;
            geometry = new SurfaceGeometry(BuildTriangles(), revision);
            return true;
        }

        /// <summary>全レンダラーの今の頂点から、当たり判定と面の描画に使う三角形を作る（面積 0 の三角形は除く）。</summary>
        List<SurfaceTriangle> BuildTriangles()
        {
            var triangles = new List<SurfaceTriangle>(); var attribute = new SurfaceAttributes.Builder(); var names = new List<string>();
            foreach (var e in entries) names.Add(e.Name);
            foreach (var e in entries)
                for (int sub = 0; sub < e.Indices.Length; sub++)
                {
                    var indices = e.Indices[sub]; var vertices = e.Vertices;
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                        if (Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude < 1e-20f) continue;
                        triangles.Add(new SurfaceTriangle(vertices[a], vertices[b], vertices[c],
                            e.HasUv ? e.Uv[a] : Vector2.zero, e.HasUv ? e.Uv[b] : Vector2.zero, e.HasUv ? e.Uv[c] : Vector2.zero,
                            e.RendererIndex, e.Slots[sub]));
                        attribute.Add(e.Normals, e.HasUv ? e.Tangents : null, e.Colors, a, b, c);
                    }
                }
            attributes = attribute.Build(names);
            return triangles;
        }

        static void AddDemoFace(List<Vector3> vertices, List<Vector2> uvs, List<int> indices, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int start = vertices.Count, face = start / 4;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            float u = (face % 3) / 3f + 0.02f, v = (face / 3) * 0.5f + 0.03f;
            float width = 1f / 3f - 0.04f, height = 0.44f;
            uvs.Add(new Vector2(u, v)); uvs.Add(new Vector2(u, v + height));
            uvs.Add(new Vector2(u + width, v)); uvs.Add(new Vector2(u + width, v + height));
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start + 2); indices.Add(start + 1); indices.Add(start + 3);
        }

        static Vector3 NormalizeNonzero(Vector3 vector)
        {
            float length = vector.magnitude;
            return length > 1e-20f ? vector / length : Vector3.zero;
        }
        static bool IsActiveRenderer(Renderer renderer, Transform root)
        {
            if (!renderer.enabled) return false;
            // A disabled selected root is still previewable; inactive descendants are excluded.
            for (var t = renderer.transform; t != null && t != root; t = t.parent) if (!t.gameObject.activeSelf) return false;
            return true;
        }
        void EnsurePreview()
        {
            if (preview != null) return;
            preview = new PreviewRenderUtility();
            preview.cameraFieldOfView = 30;
            preview.camera.clearFlags = CameraClearFlags.Color;
            preview.camera.backgroundColor = new Color(0.12f, 0.13f, 0.15f, 1);
            preview.camera.allowHDR = false; preview.camera.allowMSAA = false;
        }
        public void FrameModel()
        {
            if (!HasModel) return;
            target = Bounds.center; yaw = 25; pitch = 10;
            distance = ModelRadius / Mathf.Sin(15 * Mathf.Deg2Rad) * 1.15f;
            UpdateCamera(lastRect.width > 0 ? lastRect : new Rect(0, 0, 500, 500));
        }
        void UpdateCamera(Rect rect)
        {
            if (preview == null || rect.width <= 0 || rect.height <= 0) return;
            lastRect = rect;
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0);
            preview.camera.transform.SetPositionAndRotation(target - rotation * Vector3.forward * distance, rotation);
            preview.camera.aspect = rect.width / rect.height;
            preview.camera.fieldOfView = 30;
            preview.camera.nearClipPlane = Mathf.Max(0.00001f, Mathf.Min(distance * 0.02f, ModelRadius * 0.01f));
            preview.camera.farClipPlane = Mathf.Max(distance + ModelRadius * 20, preview.camera.nearClipPlane + 1);
        }
        public void Render(Rect rect)
        {
            ThrowIfDisposed();
            if (Event.current != null && Event.current.type != EventType.Repaint) return;
            if (!HasModel || rect.width < 2 || rect.height < 2) return;
            EnsurePreview(); UpdateCamera(rect);
            foreach (var material in materials) material.SetFloat("_PreviewLit", LitPreview ? 1 : 0);
            ApplyScene(); UpdateSymmetryPlaneObject();
            Texture texture = null;
            // PreviewRenderUtility.Render in Unity 2022.3 temporarily changes this editor flag
            // without its own finally. Preserve it here even if a render callback throws.
            bool previousPipelineFlag = Unsupported.useScriptableRenderPipeline;
            preview.BeginPreview(rect, GUIStyle.none);
            try
            {
                DrawShapeOverlay();
                // G1 neutral shader uses the built-in preview rendering path explicitly.
                // No global shader keywords, source materials or project pipeline settings are changed.
                preview.Render(false, false);
            }
            finally
            {
                try { texture = preview.EndPreview(); }
                finally { Unsupported.useScriptableRenderPipeline = previousPipelineFlag; }
            }
            if (texture != null) GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, false);
        }
        /// <summary>モデルの空間の点が 3D ビューのどこに見えるか（GUI 座標）。カメラの後ろなら false。</summary>
        public bool TryWorldToGui(Rect viewRect, Vector3 world, out Vector2 gui)
        {
            gui = default;
            if (preview == null || viewRect.width <= 0 || viewRect.height <= 0) return false;
            UpdateCamera(viewRect);
            var v = preview.camera.WorldToViewportPoint(world);
            if (v.z <= 0) return false;
            gui = new Vector2(viewRect.x + v.x * viewRect.width, viewRect.y + (1 - v.y) * viewRect.height);
            return true;
        }
        /// <summary>The camera ray through a GUI point of the 3D view (also outside the model). False without a camera or view.</summary>
        public bool TryGuiRay(Rect viewRect, Vector2 guiPosition, out Ray ray)
        {
            ray = default;
            if (preview == null || viewRect.width <= 0 || viewRect.height <= 0) return false;
            UpdateCamera(viewRect);
            ray = preview.camera.ViewportPointToRay(new Vector3((guiPosition.x - viewRect.x) / viewRect.width, 1 - (guiPosition.y - viewRect.y) / viewRect.height, 0));
            return true;
        }
        /// <summary>The 3D view as the shape gizmo sees it (projection with the preview's camera in that rectangle).</summary>
        public IGizmoView GizmoView(Rect viewRect) => new PreviewGizmoView(this, viewRect);
        sealed class PreviewGizmoView : IGizmoView
        {
            readonly IsolatedModelPreview owner; readonly Rect rect;
            public PreviewGizmoView(IsolatedModelPreview owner, Rect rect) { this.owner = owner; this.rect = rect; }
            public bool ToGui(Vector3 world, out Vector2 gui) => owner.TryWorldToGui(rect, world, out gui);
            public bool Ray(Vector2 gui, out Ray ray) => owner.TryGuiRay(rect, gui, out ray);
        }
        public bool TryPick(Rect viewRect, Vector2 guiPosition, out SurfaceHit hit)
        {
            hit = default;
            if (!CanPaint || !viewRect.Contains(guiPosition) || viewRect.width <= 0 || viewRect.height <= 0) return false;
            UpdateCamera(viewRect);
            var viewport = new Vector3((guiPosition.x - viewRect.x) / viewRect.width, 1 - (guiPosition.y - viewRect.y) / viewRect.height, 0);
            // Normalized viewport coordinates are independent of EditorGUIUtility.pixelsPerPoint.
            return geometry.TryRaycast(preview.camera.ViewportPointToRay(viewport), out hit, true);
        }
        /// <param name="cache">1 回のストロークのあいだテクセルの見え方を覚えるもの（ストロークごとに 1 つ。null なら覚えない）。</param>
        public SurfaceDabResult BuildSurfaceDabs(SurfaceHit hit, float radiusWorld, int width, int height, float hardness = 0.8f, SurfaceVisibilityCache cache = null)
        {
            if (!CanPaint) return new SurfaceDabResult { Diagnostic = "Load a complete supported static mesh snapshot before surface painting." };
            return geometry.BuildSurfaceDabs(hit, radiusWorld, width, height, preview.camera.transform.position, hardness, BrushBudget, cache);
        }
        // ───────────── シンメトリー ─────────────

        /// <summary>読み込んだモデルのルートの位置（プレビューの空間）と向き。対称の面はこのローカルの軸で決める。デモのキューブと
        /// モデルが無いときは原点と回転なし。</summary>
        public Vector3 ModelRootPosition { get; private set; }
        public Quaternion ModelRootRotation { get; private set; } = Quaternion.identity;
        /// <summary>モデルのローカルの axis に直交し、ルートから offset（シーンの単位）ずらした対称の面。</summary>
        public MirrorPlane SymmetryPlane(SymmetryAxis axis, float offset) => MirrorPlane.FromModel(ModelRootPosition, ModelRootRotation, axis, offset);
        /// <summary><see cref="BuildSurfaceDabs"/> に、対称の面で映した側のダブを合わせたもの（<see cref="SurfaceSymmetry"/>）。</summary>
        public SymmetricSurfaceDab BuildSymmetricSurfaceDabs(SurfaceHit hit, MirrorPlane plane, float radiusWorld, int width, int height, float hardness = 0.8f, SurfaceVisibilityCache cache = null)
        {
            if (!CanPaint)
            {
                var refused = new SurfaceDabResult { Diagnostic = "Load a complete supported static mesh snapshot before surface painting." };
                return new SymmetricSurfaceDab { Original = refused, Result = refused, Outcome = MirrorOutcome.OnPlane };
            }
            return SurfaceSymmetry.Build(geometry, hit, plane, radiusWorld, width, height, preview.camera.transform.position, hardness, BrushBudget, cache);
        }
        /// <summary>今のカメラの位置（プレビューの空間。最後に描いた・当たりを調べた矩形での位置）。</summary>
        public Vector3 CameraPosition => preview != null ? preview.camera.transform.position : Vector3.zero;

        /// <summary>
        /// 3D ビューに薄く見せる対称の面（null で見せない）。モデルを覆う大きさの四角とその縁で、奥行きを見て描く（モデルの手前にある
        /// 所だけが重なって見え、面がモデルを切る所が分かる）。プレビューの中だけの物で、当たり判定・ブラシ・ベイクには入らない。
        /// </summary>
        public MirrorPlane? ShownSymmetryPlane { get; set; }
        GameObject planeObject; Mesh planeMesh; Material planeMaterial; MirrorPlane builtPlane; Bounds builtBounds; bool planeBuilt;
        static readonly Color PlaneFill = new Color(.35f, .78f, 1f, .16f), PlaneEdge = new Color(.35f, .78f, 1f, .7f);
        /// <summary>試験用: 対称の面を見せる物が今の描画に入っているか。</summary>
        internal bool SymmetryPlaneVisible => planeObject != null && planeObject.activeSelf;

        void UpdateSymmetryPlaneObject()
        {
            bool show = ShownSymmetryPlane.HasValue && HasModel;
            if (!show) { if (planeObject != null) planeObject.SetActive(false); return; }
            if (planeObject == null)
            {
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) return; // 組み込みのシェーダーが無ければ見せない（描くことには関わらない）
                planeMaterial = new Material(shader) { name = "Symmetry plane (preview only)", hideFlags = HideFlags.HideAndDontSave, renderQueue = 3000 };
                planeMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); planeMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                planeMaterial.SetInt("_ZWrite", 0); planeMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual); planeMaterial.SetInt("_Cull", (int)CullMode.Off);
                planeMesh = new Mesh { name = "Symmetry plane (preview only)", hideFlags = HideFlags.HideAndDontSave };
                planeObject = new GameObject("Symmetry plane (preview only)") { hideFlags = HideFlags.HideAndDontSave };
                planeObject.AddComponent<MeshFilter>().sharedMesh = planeMesh;
                var renderer = planeObject.AddComponent<MeshRenderer>(); renderer.sharedMaterials = new[] { planeMaterial, planeMaterial };
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                preview.AddSingleGO(planeObject); planeBuilt = false;
            }
            var plane = ShownSymmetryPlane.Value; var bounds = Bounds;
            if (!planeBuilt || !builtPlane.Equals(plane) || builtBounds != bounds)
            {
                // 面に落としたモデルの中心から、モデルの外接球の半径の 1.1 倍の四角（どの向きの面でもモデルを覆う）
                var center = bounds.center - plane.SignedDistance(bounds.center) * plane.Normal;
                float half = Mathf.Max(.0001f, bounds.extents.magnitude * 1.1f);
                Vector3 u = plane.AxisU * half, v = plane.AxisV * half;
                var corners = new[] { center - u - v, center + u - v, center + u + v, center - u + v };
                planeMesh.Clear();
                planeMesh.SetVertices(new[] { corners[0], corners[1], corners[2], corners[3], corners[0], corners[1], corners[2], corners[3] });
                planeMesh.SetColors(new[] { PlaneFill, PlaneFill, PlaneFill, PlaneFill, PlaneEdge, PlaneEdge, PlaneEdge, PlaneEdge });
                planeMesh.subMeshCount = 2;
                planeMesh.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0);
                planeMesh.SetIndices(new[] { 4, 5, 5, 6, 6, 7, 7, 4 }, MeshTopology.Lines, 1);
                planeMesh.RecalculateBounds();
                builtPlane = plane; builtBounds = bounds; planeBuilt = true;
            }
            planeObject.SetActive(true);
        }
        void DisposeSymmetryPlane()
        {
            if (planeObject != null) Object.DestroyImmediate(planeObject);
            if (planeMesh != null) Object.DestroyImmediate(planeMesh);
            if (planeMaterial != null) Object.DestroyImmediate(planeMaterial);
            planeObject = null; planeMesh = null; planeMaterial = null; planeBuilt = false;
        }

        // ───────────── 形のグラデーションの重ね表示 ─────────────

        /// <summary>
        /// 3D ビューに薄く重ねる形のグラデーションの値（null で重ねない）: そのスロットの面に、形の値（レベル・反転まで。崩しと合成は
        /// 入れない）を色の不透明度として重ねる。値はシェーダーが面の点の位置から画素ごとに計算する（Core の式と同じ形・同じ順。float）ので、
        /// メッシュマップを焼く前でも形の範囲が見える。プレビューの中だけの描画で、当たり判定・ブラシ・ベイク・合成には入らない。
        /// シェーダーが無い・壊れている環境では重ねない（ギズモの線は窓が描く）。
        /// </summary>
        public ShapeGradientOverlay? ShownShapeGradient { get; set; }
        Material shapeOverlayMaterial; bool shapeOverlayUnavailable;
        /// <summary>試験用: 直前の描画で重ねたか。</summary>
        internal bool ShapeOverlayDrawn { get; private set; }

        void DrawShapeOverlay()
        {
            ShapeOverlayDrawn = false;
            if (!ShownShapeGradient.HasValue || !HasModel || preview == null) return;
            var o = ShownShapeGradient.Value;
            if (shapeOverlayMaterial == null && !shapeOverlayUnavailable)
            {
                var shader = Shader.Find("Hidden/YoluPainter/ShapeGradientOverlay");
                if (!Yozolab.YoluPainter.Editor.ShaderHealth.IsUsable(shader)) shapeOverlayUnavailable = true;
                else shapeOverlayMaterial = new Material(shader) { name = "Shape gradient overlay (preview only)", hideFlags = HideFlags.HideAndDontSave };
            }
            if (shapeOverlayMaterial == null) return;
            o.Apply(shapeOverlayMaterial);
            foreach (var (renderer, slots) in slotRenderers)
            {
                if (renderer == null) continue;
                var filter = renderer.GetComponent<MeshFilter>(); var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;
                for (int sub = 0; sub < slots.Length && sub < mesh.subMeshCount; sub++)
                    if (slots[sub] == o.Slot) { preview.DrawMesh(mesh, renderer.transform.localToWorldMatrix, shapeOverlayMaterial, sub); ShapeOverlayDrawn = true; }
            }
        }
        void DisposeShapeOverlay() { if (shapeOverlayMaterial != null) Object.DestroyImmediate(shapeOverlayMaterial); shapeOverlayMaterial = null; }

        /// <summary>Perspective estimate for screen-space resampling; geometry still determines the actual footprint.</summary>
        public float WorldRadiusToGuiPoints(Vector3 worldPosition, float radiusWorld)
        {
            if (preview == null || radiusWorld <= 0 || lastRect.height <= 0) return 0;
            float depth = Vector3.Dot(worldPosition - preview.camera.transform.position, preview.camera.transform.forward);
            if (depth <= preview.camera.nearClipPlane) return 0;
            return radiusWorld * lastRect.height / (2 * depth * Mathf.Tan(preview.camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
        }
        public void SetPaintTexture(Texture texture, int materialSlot = -1)
        {
            ThrowIfDisposed();
            for (int i = 0; i < materials.Count; i++)
            {
                bool selected = texture != null && (materialSlot < 0 || materialSlot == i);
                materials[i].SetTexture("_MainTex", selected ? texture : sourceTextures[i] != null ? sourceTextures[i] : Texture2D.whiteTexture);
                materials[i].SetColor("_Color", selected ? Color.white : sourceColors[i]);
            }
        }
        /// <summary>スロットごとに描いたテクスチャを見せる（テクスチャセットごとの合成）。textures に無いスロットと null のスロットは
        /// 元のマテリアルのテクスチャと色に戻す。表示だけで、元のマテリアルには触れない。</summary>
        public void SetPaintTextures(IReadOnlyDictionary<int, Texture> textures)
        {
            ThrowIfDisposed();
            for (int i = 0; i < materials.Count; i++)
            {
                Texture texture = null;
                bool painted = textures != null && textures.TryGetValue(i, out texture) && texture != null;
                materials[i].SetTexture("_MainTex", painted ? texture : sourceTextures[i] != null ? sourceTextures[i] : Texture2D.whiteTexture);
                materials[i].SetColor("_Color", painted ? Color.white : sourceColors[i]);
            }
        }
        /// <summary>スロットごとのノーマルマップ（<see cref="SetNormalTexture"/> と同じ形）。normals に無いスロットと null は使わない。</summary>
        public void SetNormalTextures(IReadOnlyDictionary<int, Texture> normals)
        {
            ThrowIfDisposed();
            for (int i = 0; i < materials.Count; i++)
            {
                Texture normal = null;
                bool used = normals != null && normals.TryGetValue(i, out normal) && normal != null;
                materials[i].SetTexture("_NormalMap", used ? normal : null);
                materials[i].SetFloat("_UseNormalMap", used ? 1 : 0);
            }
        }
        /// <summary>スロットのプレビューのマテリアルが今見せているテクスチャ（試験用。範囲外は null）。</summary>
        internal Texture ShownTexture(int slot) => slot >= 0 && slot < materials.Count ? materials[slot].GetTexture("_MainTex") : null;
        /// <summary>スロットのプレビューのマテリアルの照明に使っているノーマルマップ（試験用。使っていない・範囲外は null）。</summary>
        internal Texture ShownNormal(int slot) => slot >= 0 && slot < materials.Count && materials[slot].GetFloat("_UseNormalMap") > .5f ? materials[slot].GetTexture("_NormalMap") : null;
        /// <summary>ノーマルマップ（接空間、リニアの RGB、OpenGL の Y+）をスロットの照明に使う。null で使わない。表示だけで、元のマテリアルには触れない。</summary>
        public void SetNormalTexture(Texture normalMap, int materialSlot = -1)
        {
            ThrowIfDisposed();
            for (int i = 0; i < materials.Count; i++)
            {
                bool selected = normalMap != null && (materialSlot < 0 || materialSlot == i);
                materials[i].SetTexture("_NormalMap", selected ? normalMap : null);
                materials[i].SetFloat("_UseNormalMap", selected ? 1 : 0);
            }
        }
        /// <summary>今のカメラで width × height に描いた画像（試験用）。</summary>
        internal Texture2D RenderStatic(int width, int height)
        {
            ThrowIfDisposed();
            if (!HasModel) return null;
            EnsurePreview(); var rect = new Rect(0, 0, width, height); UpdateCamera(rect); ApplyScene(); UpdateSymmetryPlaneObject();
            bool previousPipelineFlag = Unsupported.useScriptableRenderPipeline, previousAsync = ShaderUtil.allowAsyncCompilation;
            // 試験の画像は、元のシェーダーのコンパイルを待った絵にする（非同期のあいだは仮のシアンで描かれる）
            ShaderUtil.allowAsyncCompilation = false;
            preview.BeginStaticPreview(rect);
            try { DrawShapeOverlay(); preview.Render(false, false); return preview.EndStaticPreview(); }
            finally { Unsupported.useScriptableRenderPipeline = previousPipelineFlag; ShaderUtil.allowAsyncCompilation = previousAsync; }
        }
        public bool HandleNavigation(Rect rect, Event current)
        {
            if (current == null || !HasModel) return false;
            int id = GUIUtility.GetControlID("YoluPainterPreviewNavigation".GetHashCode(), FocusType.Passive, rect);
            if (current.type == EventType.MouseDown && rect.Contains(current.mousePosition) && GUIUtility.hotControl == 0 &&
                (current.button == 1 || current.button == 2 || (current.alt && current.button == 0)))
            {
                navigating = true; panning = current.button == 2 || current.shift; navigationControl = id;
                GUIUtility.hotControl = id; current.Use(); return true;
            }
            if (navigating && GUIUtility.hotControl == navigationControl)
            {
                if (current.type == EventType.MouseDrag)
                {
                    if (panning)
                    {
                        var rotation = Quaternion.Euler(pitch, yaw, 0);
                        float unitsPerPixel = 2 * distance * Mathf.Tan(15 * Mathf.Deg2Rad) / Mathf.Max(1, rect.height);
                        target += rotation * new Vector3(-current.delta.x * unitsPerPixel, current.delta.y * unitsPerPixel, 0);
                    }
                    else { yaw += current.delta.x * 0.35f; pitch = Mathf.Clamp(pitch + current.delta.y * 0.35f, -89, 89); }
                    UpdateCamera(rect); current.Use(); return true;
                }
                if (current.type == EventType.MouseUp) { CancelNavigation(); current.Use(); return true; }
            }
            if (current.type == EventType.ScrollWheel && rect.Contains(current.mousePosition) && GUIUtility.hotControl == 0)
            {
                distance = Mathf.Clamp(distance * Mathf.Exp(current.delta.y * 0.06f), ModelRadius * 0.05f, ModelRadius * 100);
                UpdateCamera(rect); current.Use(); return true;
            }
            return false;
        }
        public void CancelNavigation()
        {
            if (navigating && GUIUtility.hotControl == navigationControl) GUIUtility.hotControl = 0;
            navigating = false;
        }
        void ClearModel()
        {
            CancelNavigation(); geometry = null; attributes = null;
            ModelRootPosition = Vector3.zero; ModelRootRotation = Quaternion.identity;
            foreach (var e in entries) e.Skin?.Dispose();
            entries.Clear();
            skeleton?.Dispose(); skeleton = null; humanAvatar = null; humanRoot = null;
            slotRenderers.Clear(); materialView?.Reset(0);
            DestroyTail(objects, 0); DestroyTail(meshes, 0); DestroyTail(materials, 0);
            sourceTextures.Clear(); sourceColors.Clear(); sourceMaterials.Clear(); slotNames.Clear();
        }
        static void DestroyTail<T>(List<T> items, int start) where T : Object
        {
            for (int i = items.Count - 1; i >= start; i--) { if (items[i] != null) Object.DestroyImmediate(items[i]); items.RemoveAt(i); }
        }
        void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(IsolatedModelPreview)); }
        public void Dispose()
        {
            if (disposed) return;
            ClearModel(); DisposeSymmetryPlane(); DisposeShapeOverlay();
            materialView?.Dispose(); materialView = null;
            if (preview != null) { preview.Cleanup(); preview = null; }
            disposed = true;
        }
    }
}
