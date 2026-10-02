using System;
using System.Collections.Generic;
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
        const int MaximumVerticesPerMesh = 250000;
        const int MaximumTriangles = 150000;
        readonly List<GameObject> objects = new List<GameObject>();
        readonly List<Mesh> meshes = new List<Mesh>();
        readonly List<Material> materials = new List<Material>();
        readonly List<Texture> sourceTextures = new List<Texture>();
        // スロットごとの元のマテリアル（参照だけ。プレビューは複製を描き、元には触れない）。デモキューブは null
        readonly List<Material> sourceMaterials = new List<Material>();
        readonly List<Color> sourceColors = new List<Color>();
        readonly List<string> slotNames = new List<string>();
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

        public PreviewLoadReport Load(GameObject source)
        {
            ThrowIfDisposed();
            ClearModel(); revision++; report = new PreviewLoadReport();
            if (source == null) { report.Diagnostics.Add("Choose a model GameObject or Prefab to load."); return report; }
            EnsurePreview();
            var shader = Shader.Find("Hidden/YoluPainter/PreviewSurface");
            if (shader == null) { report.Diagnostics.Add("The package's neutral preview shader could not be loaded."); return report; }
            bool incomplete = false;
            Vector3 origin = source.transform.position;
            loadOrigin = origin;
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
                meshes.Add(mesh); mesh.SetVertices(vertices); mesh.SetUVs(0, uvs); mesh.SetTriangles(indices, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
                var material = new Material(shader) { name = "Seam demo (preview only)", hideFlags = HideFlags.HideAndDontSave };
                materials.Add(material); sourceTextures.Add(null); sourceColors.Add(Color.white); sourceMaterials.Add(null); slotNames.Add("Seam cube / 0 / Neutral");
                var go = new GameObject("Texture painter seam cube (preview only)") { hideFlags = HideFlags.HideAndDontSave };
                objects.Add(go); preview.AddSingleGO(go);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                var triangles = new List<SurfaceTriangle>();
                for (int i = 0; i < indices.Count; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    triangles.Add(new SurfaceTriangle(vertices[a], vertices[b], vertices[c], uvs[a], uvs[b], uvs[c]));
                }
                geometry = new SurfaceGeometry(triangles, revision);
                report.LoadedRendererCount = 1; report.TriangleCount = geometry.TriangleCount; report.CanPaint = true;
                report.Diagnostics.Add("Demo: a tool-owned cube with six separate UV islands. Paint across a visible cube edge to check seam propagation, then orbit to check that hidden faces stayed unchanged. No source object or asset is created.");
                FrameModel();
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
            if (input.vertexCount > MaximumVerticesPerMesh)
            { incomplete = true; report.Diagnostics.Add(renderer.name + ": exceeds the prototype vertex budget (250,000 per mesh)."); return false; }
            long inputTriangles = 0; bool unsupported = false;
            for (int sub = 0; sub < input.subMeshCount; sub++)
            {
                if (input.GetTopology(sub) != MeshTopology.Triangles) { unsupported = true; break; }
                inputTriangles += (long)input.GetIndexCount(sub) / 3;
            }
            if (unsupported || triangleTotal + inputTriangles > MaximumTriangles)
            { incomplete = true; report.Diagnostics.Add(renderer.name + ": unsupported topology or prototype total triangle budget (150,000)."); return false; }
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
                mesh.RecalculateBounds();
                var go = new GameObject(renderer.name + " (isolated paint preview)") { hideFlags = HideFlags.HideAndDontSave };
                objects.Add(go); preview.AddSingleGO(go);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var copy = go.AddComponent<MeshRenderer>(); copy.sharedMaterials = clonedMaterials;
                copy.shadowCastingMode = ShadowCastingMode.Off; copy.receiveShadows = false;
                copy.lightProbeUsage = LightProbeUsage.Off; copy.reflectionProbeUsage = ReflectionProbeUsage.Off;
                entries.Add(entry);
                report.LoadedRendererCount++;
                return true;
            }
            catch (Exception exception)
            {
                incomplete = true;
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
                e.Display.RecalculateBounds();
            }
            revision++;
            geometry = new SurfaceGeometry(BuildTriangles(), revision);
            return true;
        }

        /// <summary>全レンダラーの今の頂点から、当たり判定と面の描画に使う三角形を作る（面積 0 の三角形は除く）。</summary>
        List<SurfaceTriangle> BuildTriangles()
        {
            var triangles = new List<SurfaceTriangle>();
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
                    }
                }
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
            Texture texture = null;
            // PreviewRenderUtility.Render in Unity 2022.3 temporarily changes this editor flag
            // without its own finally. Preserve it here even if a render callback throws.
            bool previousPipelineFlag = Unsupported.useScriptableRenderPipeline;
            preview.BeginPreview(rect, GUIStyle.none);
            try
            {
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
        public bool TryPick(Rect viewRect, Vector2 guiPosition, out SurfaceHit hit)
        {
            hit = default;
            if (!CanPaint || !viewRect.Contains(guiPosition) || viewRect.width <= 0 || viewRect.height <= 0) return false;
            UpdateCamera(viewRect);
            var viewport = new Vector3((guiPosition.x - viewRect.x) / viewRect.width, 1 - (guiPosition.y - viewRect.y) / viewRect.height, 0);
            // Normalized viewport coordinates are independent of EditorGUIUtility.pixelsPerPoint.
            return geometry.TryRaycast(preview.camera.ViewportPointToRay(viewport), out hit, true);
        }
        public SurfaceDabResult BuildSurfaceDabs(SurfaceHit hit, float radiusWorld, int width, int height, float hardness = 0.8f)
        {
            if (!CanPaint) return new SurfaceDabResult { Diagnostic = "Load a complete supported static mesh snapshot before surface painting." };
            return geometry.BuildSurfaceDabs(hit, radiusWorld, width, height, preview.camera.transform.position, hardness, BrushBudget);
        }
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
            CancelNavigation(); geometry = null;
            foreach (var e in entries) e.Skin?.Dispose();
            entries.Clear();
            skeleton?.Dispose(); skeleton = null; humanAvatar = null; humanRoot = null;
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
            ClearModel();
            if (preview != null) { preview.Cleanup(); preview = null; }
            disposed = true;
        }
    }
}
