using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Dot.TexturePainter.Editor.Preview
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
    /// Static-mesh G1 prototype. Never instantiates a source GameObject/Prefab or copies scripts.
    /// Reconstructs only MeshFilter/MeshRenderer objects, owned mesh snapshots and material clones.
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
        public bool CanPaint => HasModel && report.CanPaint;
        public Bounds Bounds => geometry != null ? geometry.Bounds : new Bounds(Vector3.zero, Vector3.one);
        public float ModelRadius => Mathf.Max(0.0001f, Bounds.extents.magnitude);
        public float CameraDistance => distance;
        public int SnapshotRevision => revision;
        public int MaterialSlotCount => materials.Count;
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
            var shader = Shader.Find("Hidden/DotTexturePainter/PreviewSurface");
            if (shader == null) { report.Diagnostics.Add("The package's neutral preview shader could not be loaded."); return report; }
            var triangles = new List<SurfaceTriangle>();
            bool incomplete = false;
            Vector3 origin = source.transform.position;
            foreach (var skin in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!IsActiveRenderer(skin, source.transform)) continue;
                incomplete = true;
                report.Diagnostics.Add(skin.name + ": skinned meshes are not included in this G1 static-mesh snapshot. Pose/BlendShape editing remains planned.");
            }
            foreach (var unsupportedRenderer in source.GetComponentsInChildren<Renderer>(true))
            {
                if (unsupportedRenderer is MeshRenderer || unsupportedRenderer is SkinnedMeshRenderer || !IsActiveRenderer(unsupportedRenderer, source.transform)) continue;
                incomplete = true;
                report.Diagnostics.Add(unsupportedRenderer.name + ": unsupported renderer type " + unsupportedRenderer.GetType().Name + " was not copied. Surface painting is disabled because it may occlude the target.");
            }
            foreach (var renderer in source.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!IsActiveRenderer(renderer, source.transform)) continue;
                var filter = renderer.GetComponent<MeshFilter>(); var input = filter != null ? filter.sharedMesh : null;
                if (input == null) continue;
                if (!input.isReadable)
                { incomplete = true; report.Diagnostics.Add(renderer.name + ": mesh is not CPU-readable. Import settings were not changed."); continue; }
                if (input.vertexCount > MaximumVerticesPerMesh)
                { incomplete = true; report.Diagnostics.Add(renderer.name + ": exceeds the prototype vertex budget (250,000 per mesh)."); continue; }
                long inputTriangles = 0; bool unsupported = false;
                for (int sub = 0; sub < input.subMeshCount; sub++)
                {
                    if (input.GetTopology(sub) != MeshTopology.Triangles) { unsupported = true; break; }
                    inputTriangles += (long)input.GetIndexCount(sub) / 3;
                }
                if (unsupported || triangles.Count + inputTriangles > MaximumTriangles)
                { incomplete = true; report.Diagnostics.Add(renderer.name + ": unsupported topology or prototype total triangle budget (150,000)."); continue; }
                int firstTriangle = triangles.Count, firstSlot = materials.Count;
                int firstMesh = meshes.Count, firstObject = objects.Count;
                try
                {
                    Mesh mesh = Object.Instantiate(input); // Mesh only: no source GameObject or behaviour is instantiated.
                    mesh.name = input.name + " (paint snapshot)"; mesh.hideFlags = HideFlags.HideAndDontSave; meshes.Add(mesh);
                    var vertices = mesh.vertices;
                    var matrix = renderer.localToWorldMatrix; var normalMatrix = matrix.inverse.transpose;
                    bool mirrored = matrix.determinant < 0;
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
                        materials.Add(material); sourceTextures.Add(texture); sourceColors.Add(color);
                        slotNames.Add(renderer.name + " / " + sub + " / " + (original != null ? original.name : "Unassigned"));
                        clonedMaterials[sub] = material;
                        var indices = mesh.GetTriangles(sub);
                        if (mirrored) { for (int i = 0; i < indices.Length; i += 3) { int temp = indices[i + 1]; indices[i + 1] = indices[i + 2]; indices[i + 2] = temp; } mesh.SetTriangles(indices, sub, false); }
                        for (int i = 0; i < indices.Length; i += 3)
                        {
                            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                            if (Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude < 1e-20f) continue;
                            triangles.Add(new SurfaceTriangle(vertices[a], vertices[b], vertices[c],
                                hasUv ? uv[a] : Vector2.zero, hasUv ? uv[b] : Vector2.zero, hasUv ? uv[c] : Vector2.zero,
                                report.LoadedRendererCount, slot));
                        }
                    }
                    if (normals.Length != vertices.Length) mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    var go = new GameObject(renderer.name + " (isolated paint preview)") { hideFlags = HideFlags.HideAndDontSave };
                    objects.Add(go); preview.AddSingleGO(go);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var copy = go.AddComponent<MeshRenderer>(); copy.sharedMaterials = clonedMaterials;
                    copy.shadowCastingMode = ShadowCastingMode.Off; copy.receiveShadows = false;
                    copy.lightProbeUsage = LightProbeUsage.Off; copy.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    report.LoadedRendererCount++;
                }
                catch (Exception exception)
                {
                    incomplete = true;
                    triangles.RemoveRange(firstTriangle, triangles.Count - firstTriangle);
                    DestroyTail(objects, firstObject); DestroyTail(meshes, firstMesh); DestroyTail(materials, firstSlot);
                    sourceTextures.RemoveRange(firstSlot, sourceTextures.Count - firstSlot);
                    sourceColors.RemoveRange(firstSlot, sourceColors.Count - firstSlot);
                    slotNames.RemoveRange(firstSlot, slotNames.Count - firstSlot);
                    report.Diagnostics.Add(renderer.name + ": could not build safe mesh snapshot: " + exception.Message);
                }
            }
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
            report.Diagnostics.Add("G1 uses UV0, 0–1 UVs, static readable meshes and an opaque neutral shader. UV tiling, alpha cutouts, shader displacement, exact lilToon/SRP appearance and skinning are not represented.");
            report.Diagnostics.Add("Overlapping UVs share pixels. Duplicate-position edges may join seams; surface filtering cannot make overlapping UVs independent.");
            if (incomplete) report.Diagnostics.Add("Surface painting is disabled for this incomplete snapshot, so omitted geometry cannot silently allow painting through clothes. Choose a supported static-mesh root.");
            return report;
        }

        /// <summary>Loads a tool-created cube with six separated UV islands. No source assets or scene objects are needed.</summary>
        public PreviewLoadReport LoadDemoMesh()
        {
            ThrowIfDisposed(); ClearModel(); revision++; report = new PreviewLoadReport(); EnsurePreview();
            var shader = Shader.Find("Hidden/DotTexturePainter/PreviewSurface");
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
                materials.Add(material); sourceTextures.Add(null); sourceColors.Add(Color.white); slotNames.Add("Seam cube / 0 / Neutral");
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
            int id = GUIUtility.GetControlID("DotTexturePainterPreviewNavigation".GetHashCode(), FocusType.Passive, rect);
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
            DestroyTail(objects, 0); DestroyTail(meshes, 0); DestroyTail(materials, 0);
            sourceTextures.Clear(); sourceColors.Clear(); slotNames.Clear();
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
