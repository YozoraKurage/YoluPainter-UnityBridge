using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// mesh map のベイク（サブスタンス由来の機能の土台）: 読み込んだモデルのスナップショット（<see cref="IsolatedModelPreview.Geometry"/>）から、
    /// 今の材質スロットとドキュメントの大きさで法線・位置・AO・曲率・厚みを焼く。結果は描くレイヤーではない派生物で、Undo に入らず、
    /// ドキュメントの版も変えない。.ylp に meshmap-&lt;種類&gt;.bin として保存し、開いたときに戻す。由来（モデルの指紋・大きさ・スロット・設定・
    /// エンジンの版）が今と違うマップは「古い」と表示し、黙って使わない。キャンバスには読むだけの重ね表示で見せる。
    /// ベイクは呼んだ所で待つ（取消できる進捗バー）。元のモデル・マテリアル・テクスチャ・取り込み設定には触れない（プレビューの複製の
    /// 三角形を読むだけ）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>キャンバスに重ねて見るもの。Coverage はテクセルの由来（覆う・UV の重なり・余白・空）。</summary>
        internal enum MeshMapView
        {
            None = -2, Coverage = -1,
            WorldNormal = MeshMapKind.WorldNormal, Position = MeshMapKind.Position, AmbientOcclusion = MeshMapKind.AmbientOcclusion,
            Curvature = MeshMapKind.Curvature, Thickness = MeshMapKind.Thickness, TangentNormal = MeshMapKind.TangentNormal, Height = MeshMapKind.Height,
            Id = MeshMapKind.Id, BentNormal = MeshMapKind.BentNormal, Opacity = MeshMapKind.Opacity,
        }
        /// <summary>スナップショットの属性（頂点法線）が無いときに、形から作り直すスムージング角度（Unity の取り込みの既定と同じ）。</summary>
        internal const double MeshMapNormalCrease = 60;
        /// <summary>高ポリ（ベイクの参照）の読み込みの上限。表示の試作の上限（15 万三角形）より大きい。</summary>
        internal const int HighPolyMaxTriangles = 2000000, HighPolyMaxVertices = 4000000;

        [SerializeField] GameObject highPolyModel;
        /// <summary>AO・ベントノーマル・厚みのレイを GPU（計算シェーダー）で処理する。使えない GPU では CPU で焼き、そう知らせる。</summary>
        [SerializeField] bool meshBakeUseGpu = true;
        internal bool MeshBakeUseGpu { get => meshBakeUseGpu; set => meshBakeUseGpu = value; }
        IsolatedModelPreview highPolyPreview; GameObject highPolyLoaded; Vector3 highPolyOrigin; Matrix4x4 highPolyMatrix; string highPolyNote;
        SurfaceGeometry highPolyInputFor; MeshBakeInput highPolyInput;

        readonly MeshMapSet meshMaps = new MeshMapSet();
        MeshBakeSettings meshBakeSettings = new MeshBakeSettings();
        bool showMeshMaps;
        MeshMapView meshMapView = MeshMapView.None; float meshMapOpacity = 1;
        Texture2D meshMapOverlay; long overlayRevision = -1; MeshMapView overlayView = MeshMapView.None;
        long savedMeshMapRevision, savingMeshMapRevision = -1;
        string meshMapNote;
        MeshBakeReport lastMeshBakeReport;
        SurfaceGeometry meshBakeInputFor; MeshBakeInput meshBakeInput;

        internal MeshMapSet MeshMaps => meshMaps;
        internal MeshBakeSettings MeshBakeSettings => meshBakeSettings;
        /// <summary>今の mesh map が保存したファイルと同じか（ベイクしてまだ保存していなければ false）。</summary>
        internal bool MeshMapsSaved => meshMaps.Revision == savedMeshMapRevision;
        /// <summary>前の保存で mesh map を入れられなかったときなどの知らせ。</summary>
        internal string MeshMapNote => meshMapNote;
        internal MeshBakeReport LastMeshBakeReport => lastMeshBakeReport;
        internal MeshMapView MeshMapOverlay { get => meshMapView; set { meshMapView = value; Repaint(); } }
        internal float MeshMapOverlayOpacity { get => meshMapOpacity; set => meshMapOpacity = Mathf.Clamp01(value); }
        internal bool ShowMeshMapPanel { get => showMeshMaps; set => showMeshMaps = value; }
        /// <summary>ベイクの進み具合（題、説明、0〜1）。true を返すと取消。既定は Unity の取消できる進捗バー（テストは差し替える）。</summary>
        internal Func<string, string, float, bool> MeshBakeProgress = EditorUtility.DisplayCancelableProgressBar;

        /// <summary>プレビューのスナップショットから焼き込みの入力を作る。同じスナップショットのあいだは作り直さない。モデルが無ければ null。
        /// 頂点法線は形から作り直す（<see cref="MeshMapNormalCrease"/>）。</summary>
        internal MeshBakeInput CurrentMeshBakeInput()
        {
            var geometry = preview != null ? preview.Geometry : null;
            if (geometry == null || geometry.TriangleCount == 0) { meshBakeInputFor = null; meshBakeInput = null; return null; }
            if (ReferenceEquals(geometry, meshBakeInputFor)) return meshBakeInput;
            meshBakeInput = BuildMeshBakeInput(geometry, preview.Attributes);
            meshBakeInputFor = geometry;
            return meshBakeInput;
        }

        /// <summary>スナップショットの三角形（位置・UV0・スロット・レンダラー）と属性（頂点法線・接線・頂点カラー・レンダラー名）を
        /// 焼き込みの入力にする。属性が無い（または並びが合わない）ときは、頂点法線を形から作り直し、接線・色・名前は無し。</summary>
        internal static MeshBakeInput BuildMeshBakeInput(SurfaceGeometry geometry, SurfaceAttributes attributes = null)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            var triangles = geometry.Triangles; int n = triangles.Count;
            var corners = new float[n * 9]; var uvs = new float[n * 6]; var slots = new int[n]; var renderers = new int[n];
            for (int i = 0; i < n; i++)
            {
                var t = triangles[i]; int k = i * 9, u = i * 6;
                corners[k] = t.A.x; corners[k + 1] = t.A.y; corners[k + 2] = t.A.z;
                corners[k + 3] = t.B.x; corners[k + 4] = t.B.y; corners[k + 5] = t.B.z;
                corners[k + 6] = t.C.x; corners[k + 7] = t.C.y; corners[k + 8] = t.C.z;
                uvs[u] = t.UvA.x; uvs[u + 1] = t.UvA.y; uvs[u + 2] = t.UvB.x; uvs[u + 3] = t.UvB.y; uvs[u + 4] = t.UvC.x; uvs[u + 5] = t.UvC.y;
                slots[i] = Math.Max(-1, t.MaterialSlot); renderers[i] = Math.Max(0, t.RendererIndex);
            }
            if (attributes == null || attributes.TriangleCount != n)
                return new MeshBakeInput(corners, MeshBakeInput.ReconstructNormals(corners, MeshMapNormalCrease), uvs, slots, 0, "reconstructed-crease-" + MeshMapNormalCrease, renderers: renderers);
            var names = attributes.RendererNames;
            if (names != null) foreach (int r in renderers) if (r >= names.Count) { names = null; break; }
            return new MeshBakeInput(corners, attributes.Normals, uvs, slots, 0, "authored", attributes.Tangents, attributes.Colors, renderers, names);
        }

        /// <summary>高ポリの入力。選んでいない・読めない・形が無ければ null。低ポリのモデル（の原点）が替わったら読み直す。
        /// 読むのはメッシュだけ（MeshRenderer と SkinnedMeshRenderer。元の GameObject は Instantiate しない）で、空間は低ポリと同じ
        /// （ワールドから低ポリのルートの位置を引いたもの）。</summary>
        internal MeshBakeInput CurrentHighPolyInput()
        {
            if (highPolyModel == null) { DisposeHighPoly(); return null; }
            var origin = model != null ? model.transform.position : Vector3.zero; var matrix = highPolyModel.transform.localToWorldMatrix;
            // 選び直し・低ポリの原点・高ポリのルートの置き方が変われば読み直す（メッシュの中身の編集は「Reload」で）
            if (highPolyPreview == null || highPolyLoaded != highPolyModel || highPolyOrigin != origin || highPolyMatrix != matrix)
            {
                DisposeHighPoly();
                highPolyPreview = new IsolatedModelPreview();
                var report = highPolyPreview.Load(highPolyModel, new PreviewLoadOptions { MaxTriangles = HighPolyMaxTriangles, MaxVerticesPerMesh = HighPolyMaxVertices, Origin = origin });
                highPolyLoaded = highPolyModel; highPolyOrigin = origin; highPolyMatrix = matrix;
                highPolyNote = !highPolyPreview.HasModel ? "The high poly could not be read: " + string.Join(" ", report.Diagnostics.Take(3))
                    : !report.CanPaint ? "Parts of the high poly were not read (its UVs do not matter, but missing parts would be missing from the bake): " + string.Join(" ", report.Diagnostics.Take(3)) : null;
            }
            var geometry = highPolyPreview.Geometry;
            if (geometry == null || geometry.TriangleCount == 0) return null;
            if (!ReferenceEquals(geometry, highPolyInputFor)) { highPolyInput = BuildMeshBakeInput(geometry, highPolyPreview.Attributes); highPolyInputFor = geometry; }
            return highPolyInput;
        }
        internal GameObject HighPolyModel { get => highPolyModel; set { highPolyModel = value; Repaint(); } }
        void DisposeHighPoly() { highPolyPreview?.Dispose(); highPolyPreview = null; highPolyLoaded = null; highPolyInput = null; highPolyInputFor = null; highPolyNote = null; }

        /// <summary>マップを使う側の今の条件（モデル・ドキュメントの大きさ・スロット・欄の設定）。</summary>
        internal MeshMapExpectation CurrentMeshMapExpectation()
        {
            var input = CurrentMeshBakeInput();
            return new MeshMapExpectation
            {
                MeshHash = input?.Hash, TopologyHash = input?.TopologyHash, ReferenceHash = CurrentHighPolyInput()?.Hash, Width = document.Width, Height = document.Height,
                TargetSlot = materialSlot, UvChannel = 0, Settings = meshBakeSettings,
            };
        }

        /// <summary>選んだ mesh map を今のモデル・スロット・ドキュメントの大きさで焼き、同じ種類のマップを置き換える。取消・時間切れ・予算の
        /// 拒否では今のマップを変えない。描画中、モデルが無い、塗れない（不完全な）スナップショットでは焼かない。</summary>
        /// <returns>焼いた結果の状態。始めなかったら null。</returns>
        internal MeshBakeStatus? BakeMeshMaps()
        {
            if (stroke != null || document == null) return null;
            if (preview == null || !preview.HasModel) { message = "Load a model (or the demo cube) before baking mesh maps."; return null; }
            if (!preview.CanPaint) { message = "Mesh maps are baked only from a complete static snapshot that can be painted; this one is incomplete (see the load diagnostics)."; return null; }
            var settings = meshBakeSettings.Clone();
            settings.Width = document.Width; settings.Height = document.Height; settings.TargetSlot = materialSlot;
            var reference = CurrentHighPolyInput();
            if (highPolyModel != null && reference == null) { message = "Mesh maps were not baked: " + (highPolyNote ?? "the high poly has no readable mesh."); return null; }
            MeshBakeResult result;
            try
            {
                settings.Validate();
                result = RunMeshBake(CurrentMeshBakeInput(), settings, new MeshBakeBudget { MaxBytes = PainterSettings.StrokeBudgetBytes }, reference);
            }
            catch (Exception ex) when (ex is MeshBakeRefusedException || ex is ArgumentException)
            { message = "Mesh maps were not baked: " + ex.Message; return null; }
            finally { Dialogs.ClearProgress(); }
            lastMeshBakeReport = result.Report;
            if (result.Status != MeshBakeStatus.Completed)
            {
                message = (result.Status == MeshBakeStatus.TimedOut ? "The mesh-map bake hit its time limit" : "The mesh-map bake was canceled") + "; the previous maps are unchanged.";
                return result.Status;
            }
            meshMaps.Put(result.Maps);
            if (meshMapView == MeshMapView.None) meshMapView = MeshMapView.Coverage;
            message = "Baked " + string.Join(", ", result.Maps.Select(m => m.Kind)) + " at " + settings.Width + "×" + settings.Height + " for slot " + settings.TargetSlot + " (rays: " + result.Report.RayBackend + "): " + result.Report.Summary()
                + ". Mesh maps are derived data, not layers; Save keeps them in the .ylp.";
            Repaint();
            return result.Status;
        }

        /// <summary>ベイクは別のスレッドで回し、ここ（主スレッド）で進捗バーを出して取消を受ける。始める前にも 1 回尋ねる。</summary>
        MeshBakeResult RunMeshBake(MeshBakeInput input, MeshBakeSettings settings, MeshBakeBudget budget, MeshBakeInput reference)
        {
            const string title = "Baking mesh maps";
            // GPU の呼び出しは主スレッドでしかできないので、別のスレッドのベイクから回ってきた仕事をこのループで行う
            var tracer = meshBakeUseGpu ? new GpuMeshBakeRayTracer(PainterSettings.GpuCacheBytes) : null;
            using (var cancel = new CancellationTokenSource())
            {
                if (MeshBakeProgress(title, "Preparing…", 0)) cancel.Cancel();
                var gate = new object(); double fraction = 0; string phase = "Preparing";
                var task = Task.Run(() => MeshBaker.Bake(input, settings, budget, (f, p) => { lock (gate) { fraction = f; phase = p; } return true; }, cancel.Token, reference, tracer));
                var shown = System.Diagnostics.Stopwatch.StartNew();
                while (!WaitQuietly(task, tracer != null ? 2 : 50))
                {
                    tracer?.Pump();
                    if (tracer != null && shown.ElapsedMilliseconds < 50) continue;
                    shown.Restart();
                    double f; string p;
                    lock (gate) { f = fraction; p = phase; }
                    if (MeshBakeProgress(title, p + "… " + (int)(f * 100) + "% (" + settings.Width + "×" + settings.Height + ", " + string.Join(", ", settings.Maps) + ")", (float)f)) cancel.Cancel();
                }
                tracer?.Pump();
                return task.GetAwaiter().GetResult(); // 拒否などの例外はそのまま（AggregateException に包まない）
            }
        }
        static bool WaitQuietly(Task task, int milliseconds) { try { return task.Wait(milliseconds); } catch (AggregateException) { return true; } }

        /// <summary>double の値のスライダー。触っていなければ元の値のまま返す（float に丸めた値や欄の範囲で切った値で、保存した条件を
        /// 黙って変えて「設定が変わった」と古くしない）。</summary>
        static double KeepSlider(GUIContent label, double value, float min, float max)
        {
            float shown = Mathf.Clamp((float)value, min, max), picked = EditorGUILayout.Slider(label, shown, min, max);
            return picked != shown ? picked : value;
        }
        static int KeepIntSlider(string label, int value, int min, int max)
        {
            int shown = Mathf.Clamp(value, min, max), picked = EditorGUILayout.IntSlider(label, shown, min, max);
            return picked != shown ? picked : value;
        }

        void DrawMeshMapPanel()
        {
            GUILayout.Space(6);
            var expected = meshMaps.Count > 0 ? CurrentMeshMapExpectation() : null;
            int stale = expected == null ? 0 : meshMaps.Maps.Count(m => m.Provenance.Check(expected).State == MeshMapState.Stale);
            showMeshMaps = EditorGUILayout.Foldout(showMeshMaps, "Mesh maps (bake)" + (stale > 0 ? " — " + stale + " stale" : ""), true);
            if (!showMeshMaps) return;
            var s = meshBakeSettings;
            using (new EditorGUI.DisabledScope(stroke != null))
            {
                EditorGUILayout.LabelField("Bakes " + document.Width + "×" + document.Height + " (document) for slot " + materialSlot + " from the loaded model", EditorStyles.wordWrappedMiniLabel);
                var kinds = new List<MeshMapKind>();
                foreach (var kind in MeshBakeSettings.AllKinds)
                    if (EditorGUILayout.ToggleLeft(MeshMapLabel(kind), s.Includes(kind))) kinds.Add(kind);
                if (kinds.Count > 0 && !kinds.SequenceEqual(s.Maps)) s.Maps = kinds.ToArray();
                s.Padding = EditorGUILayout.IntSlider(new GUIContent("Padding", "Texels the islands are extended into the empty space around them (never over another island)"), s.Padding, 0, MeshBakeSettings.MaxPadding);
                // 欄の範囲は扱える範囲より狭い（保存したマップの値が範囲の外でも、触らなければそのまま）
                if (s.Includes(MeshMapKind.AmbientOcclusion) || s.Includes(MeshMapKind.BentNormal))
                {
                    GUILayout.Label("Ambient occlusion", EditorStyles.miniBoldLabel);
                    s.AoSamples = KeepIntSlider("Rays", s.AoSamples, 1, 512);
                    s.AoMaxDistance = KeepSlider(new GUIContent("Max distance", "Relative to the model's bounding-box diagonal"), s.AoMaxDistance, .001f, 1);
                    s.AoSpreadDegrees = KeepSlider(new GUIContent("Spread °", "180 = the whole hemisphere"), s.AoSpreadDegrees, 1, 180);
                    s.AoFalloff = (MeshOcclusionFalloff)EditorGUILayout.EnumPopup(new GUIContent("Falloff", "Linear: nearer occluders darken more"), s.AoFalloff);
                    s.AoIgnoreBackfaces = EditorGUILayout.Toggle(new GUIContent("Ignore back faces", "Rays pass through faces seen from behind"), s.AoIgnoreBackfaces);
                }
                if (s.Includes(MeshMapKind.Curvature))
                {
                    GUILayout.Label("Curvature", EditorStyles.miniBoldLabel);
                    s.CurvatureRadius = KeepSlider(new GUIContent("Radius", "Edges within this distance count (relative to the bounding-box diagonal). Larger = wider, softer edges"), s.CurvatureRadius, (float)MeshBakeSettings.MinCurvatureRadius, .2f);
                }
                if (s.Includes(MeshMapKind.Thickness))
                {
                    GUILayout.Label("Thickness", EditorStyles.miniBoldLabel);
                    s.ThicknessSamples = KeepIntSlider("Rays", s.ThicknessSamples, 1, 512);
                    s.ThicknessMaxDistance = KeepSlider(new GUIContent("Max distance", "Relative to the bounding-box diagonal; thicker parts read 1"), s.ThicknessMaxDistance, .001f, 1);
                    s.ThicknessSpreadDegrees = KeepSlider(new GUIContent("Spread °"), s.ThicknessSpreadDegrees, 1, 180);
                }
                if (s.Includes(MeshMapKind.Id)) s.IdSource = (MeshIdSource)EditorGUILayout.EnumPopup(new GUIContent("ID from", "What gets its own colour in the ID map"), s.IdSource);
                meshBakeUseGpu = EditorGUILayout.Toggle(new GUIContent("Use GPU for rays", "Ambient occlusion, bent normal and thickness rays run in a compute shader (same formulas, float precision). Falls back to the CPU when the GPU cannot."), meshBakeUseGpu);
                s.Antialiasing = EditorGUILayout.IntPopup(new GUIContent("Antialiasing", "Subsamples per texel side (the bake takes n² times as long)"), s.Antialiasing, new[] { new GUIContent("None"), new GUIContent("2×2"), new GUIContent("3×3"), new GUIContent("4×4") }, new[] { 1, 2, 3, 4 });
                GUILayout.Label("High poly (optional)", EditorStyles.miniBoldLabel);
                var high = (GameObject)EditorGUILayout.ObjectField(new GUIContent("High poly", "A second model whose detail is projected onto this one (meshes are read only; nothing is instantiated or changed)"), highPolyModel, typeof(GameObject), true);
                if (high != highPolyModel) { highPolyModel = high; CurrentHighPolyInput(); }
                if (highPolyModel != null)
                {
                    s.ReferenceFrontal = KeepSlider(new GUIContent("Max frontal", "How far outside the low poly the high poly is searched (relative to the bounding-box diagonal)"), s.ReferenceFrontal, .0001f, .2f);
                    s.ReferenceRear = KeepSlider(new GUIContent("Max rear", "How far inside the low poly the high poly is searched"), s.ReferenceRear, .0001f, .2f);
                    s.ReferenceAverageNormals = EditorGUILayout.Toggle(new GUIContent("Average normals", "Project along normals averaged over hard edges (a cage without gaps)"), s.ReferenceAverageNormals);
                    s.ReferenceMatchByName = EditorGUILayout.Toggle(new GUIContent("Match by name", "Project 'part_low' only onto 'part_high' (or 'part')"), s.ReferenceMatchByName);
                    var reference = highPolyPreview != null ? highPolyPreview.Geometry : null;
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(reference != null ? reference.TriangleCount.ToString("N0") + " high-poly triangles" : "Not readable", EditorStyles.miniLabel);
                    if (GUILayout.Button(new GUIContent("Reload", "Read the high poly's meshes again (after editing them)"), EditorStyles.miniButton, GUILayout.Width(60))) { DisposeHighPoly(); CurrentHighPolyInput(); }
                    GUILayout.EndHorizontal();
                    if (highPolyNote != null) EditorGUILayout.HelpBox(highPolyNote, MessageType.Warning);
                }
                if (s.Includes(MeshMapKind.AmbientOcclusion) || s.Includes(MeshMapKind.Thickness) || s.Includes(MeshMapKind.BentNormal))
                    s.Occluders = (MeshOccluders)EditorGUILayout.EnumPopup(new GUIContent("Occluders", "Which faces block AO and thickness rays"), s.Occluders);
                using (new EditorGUI.DisabledScope(preview == null || !preview.HasModel))
                    if (GUILayout.Button(new GUIContent("Bake mesh maps", "Bake the checked maps from the loaded model. The model, its materials and textures are not changed."))) TryAction(() => BakeMeshMaps());
            }
            if (meshMaps.Count > 0)
            {
                foreach (var map in meshMaps.Maps)
                {
                    var check = map.Provenance.Check(expected);
                    EditorGUILayout.LabelField(MeshMapLabel(map.Kind), map.Width + "×" + map.Height + " slot " + map.Provenance.TargetSlot + " — " + check.State.ToString().ToLowerInvariant(), EditorStyles.miniLabel);
                    if (check.State == MeshMapState.Stale) EditorGUILayout.HelpBox(MeshMapLabel(map.Kind) + " is stale and is not used: " + string.Join(" ", check.Reasons) + " Bake again to update it.", MessageType.Warning);
                }
                if (expected.MeshHash == null) EditorGUILayout.HelpBox("No model is loaded, so the maps cannot be checked against it.", MessageType.Info);
                var views = new List<MeshMapView> { MeshMapView.None, MeshMapView.Coverage };
                views.AddRange(meshMaps.Maps.Select(m => (MeshMapView)m.Kind));
                int index = Math.Max(0, views.IndexOf(meshMapView));
                int picked = EditorGUILayout.Popup(new GUIContent("Show on canvas", "Read-only overlay; nothing is painted"), index, views.Select(v => new GUIContent(v == MeshMapView.None ? "None" : v == MeshMapView.Coverage ? "Coverage (UV islands)" : MeshMapLabel((MeshMapKind)v))).ToArray());
                if (picked != index) { meshMapView = views[picked]; Repaint(); }
                if (meshMapView != MeshMapView.None) meshMapOpacity = EditorGUILayout.Slider("Overlay opacity", meshMapOpacity, 0, 1);
                if (meshMapView == MeshMapView.Coverage) EditorGUILayout.LabelField("Green: baked · Red: overlapping UVs · Blue: padding", EditorStyles.miniLabel);
                if (!MeshMapsSaved) EditorGUILayout.HelpBox("These mesh maps are not in the saved file yet. Save keeps them in the .ylp (they are not part of Undo).", MessageType.None);
                using (new EditorGUI.DisabledScope(stroke != null))
                    if (GUILayout.Button("Clear mesh maps")) { meshMaps.Clear(); meshMapView = MeshMapView.None; message = "Cleared the mesh maps (derived data; nothing in the layers changed)."; }
            }
            if (lastMeshBakeReport != null && lastMeshBakeReport.Diagnostics.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", lastMeshBakeReport.Diagnostics), MessageType.Info);
            if (meshMapNote != null) EditorGUILayout.HelpBox(meshMapNote, MessageType.Warning);
        }
        static string MeshMapLabel(MeshMapKind kind)
        {
            switch (kind)
            {
                case MeshMapKind.WorldNormal: return "Normal (world)";
                case MeshMapKind.Position: return "Position";
                case MeshMapKind.AmbientOcclusion: return "Ambient occlusion";
                case MeshMapKind.Curvature: return "Curvature";
                case MeshMapKind.Thickness: return "Thickness";
                case MeshMapKind.TangentNormal: return "Normal (tangent)";
                case MeshMapKind.Height: return "Height";
                case MeshMapKind.Id: return "ID";
                case MeshMapKind.BentNormal: return "Bent normal";
                default: return "Opacity";
            }
        }

        /// <summary>選んだマップ（または由来の色分け）をキャンバスに重ねる。表示だけで、レイヤーには何も書かない。</summary>
        void DrawMeshMapOverlay(Rect image)
        {
            if (meshMapView == MeshMapView.None || Event.current.type != EventType.Repaint) return;
            var texture = EnsureMeshMapOverlay(); if (texture == null) return;
            var previous = GUI.color; GUI.color = new Color(1, 1, 1, meshMapOpacity);
            GUI.DrawTexture(image, texture, ScaleMode.StretchToFill, true);
            GUI.color = previous;
        }
        /// <summary>重ね表示のテクスチャ（マップか表示の種類が変わったときだけ作り直す）。見せるものが無ければ null。</summary>
        internal Texture2D EnsureMeshMapOverlay()
        {
            BakedMeshMap map = null;
            if (meshMapView == MeshMapView.Coverage) map = meshMaps.Maps.FirstOrDefault();
            else if (meshMapView != MeshMapView.None) meshMaps.TryGet((MeshMapKind)meshMapView, out map);
            if (map == null) return null;
            if (meshMapOverlay != null && overlayRevision == meshMaps.Revision && overlayView == meshMapView) return meshMapOverlay;
            if (meshMapOverlay == null || meshMapOverlay.width != map.Width || meshMapOverlay.height != map.Height)
            {
                DisposeMeshMapOverlay();
                meshMapOverlay = new Texture2D(map.Width, map.Height, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "YoluPainter mesh map overlay" };
            }
            meshMapOverlay.LoadRawTextureData(map.ToRgba8(meshMapView == MeshMapView.Coverage)); meshMapOverlay.Apply(false, false);
            overlayRevision = meshMaps.Revision; overlayView = meshMapView;
            return meshMapOverlay;
        }
        void DisposeMeshMapOverlay() { if (meshMapOverlay != null) { DestroyImmediate(meshMapOverlay); meshMapOverlay = null; } overlayRevision = -1; }
        /// <summary>OnDisable から。</summary>
        void DisposeMeshMaps() { DisposeMeshMapOverlay(); DisposeHighPoly(); }

        /// <summary>ドキュメントを替えたとき（BindDocument から）。マップは前のドキュメントのものなので捨てる。</summary>
        void ClearMeshMaps()
        {
            meshMaps.Clear(); savedMeshMapRevision = meshMaps.Revision; savingMeshMapRevision = -1;
            meshMapNote = null; lastMeshBakeReport = null; meshMapView = MeshMapView.None;
        }

        /// <summary>保存する中身に mesh map を足す（SaveProject から）。入れると .ylp の予算を超えるなら入れずに知らせる
        /// （ドキュメントそのものは保存する。マップは焼き直せる派生物）。</summary>
        void AddMeshMapFiles(Dictionary<string, byte[]> files)
        {
            meshMapNote = null; savingMeshMapRevision = meshMaps.Revision;
            if (meshMaps.Count == 0) return;
            var entries = meshMaps.Maps.ToDictionary(m => MeshMapBinary.EntryName(m.Kind), MeshMapBinary.Write);
            string problem = MeshMapSaveProblem(files.Values.Sum(b => b.LongLength), files.Count, entries.Values.Select(b => b.LongLength));
            if (problem != null)
            {
                savingMeshMapRevision = -1;
                meshMapNote = "Mesh maps were left out of the last save: " + problem + " The document itself was saved; bake the maps again after opening it.";
                Debug.LogWarning("Texture Painter: " + meshMapNote);
                return;
            }
            foreach (var entry in entries) files[entry.Key] = entry.Value;
        }
        /// <summary>mesh map のエントリーを足すと .ylp の予算（1 エントリー・合計・数）を超えるなら、その理由。収まるなら null。</summary>
        internal static string MeshMapSaveProblem(long documentBytes, int documentEntries, IEnumerable<long> mapBytes)
        {
            var sizes = mapBytes.ToList();
            if (sizes.Any(b => b > YlpArchive.MaxEntryBytes)) return "a mesh map exceeds the " + (YlpArchive.MaxEntryBytes >> 20) + " MiB entry budget of a .ylp.";
            if (documentBytes + sizes.Sum() > YlpArchive.MaxTotalBytes) return "with them the .ylp would exceed its " + (YlpArchive.MaxTotalBytes >> 20) + " MiB budget.";
            if (documentEntries + sizes.Count > YlpArchive.MaxEntries) return "with them the .ylp would hold too many entries.";
            return null;
        }
        /// <summary>保存が済んだら呼ぶ（SaveProject から）。</summary>
        void MeshMapsWereSaved() { if (savingMeshMapRevision >= 0) savedMeshMapRevision = savingMeshMapRevision; }

        /// <summary>開いた .ylp の mesh map を戻す（OpenProjectAt から、BindDocument の後）。読めないもの・知らない種類は捨てたことを
        /// 知らせる（ドキュメントは開く。マップは焼き直せる派生物）。</summary>
        void LoadMeshMapFiles(IReadOnlyDictionary<string, byte[]> files, List<string> notes)
        {
            var loaded = new List<BakedMeshMap>();
            foreach (var entry in files.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!entry.Key.StartsWith(MeshMapBinary.EntryPrefix, StringComparison.Ordinal)) continue;
                if (!MeshMapBinary.TryParseEntryName(entry.Key, out var kind)) { notes.Add("Unknown mesh map " + entry.Key + " (from a newer YoluPainter?) was not loaded."); continue; }
                try
                {
                    var map = MeshMapBinary.Read(entry.Value);
                    if (map.Kind != kind) throw new InvalidDataException("it holds " + map.Kind + ".");
                    loaded.Add(map);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException)
                { notes.Add("Mesh map " + kind + " could not be read (" + ex.Message + "); bake it again."); }
            }
            if (loaded.Count > 0)
            {
                meshMaps.Put(loaded);
                // 欄の設定を、保存したマップの条件にそろえる（開いただけで「設定が変わった」と古くならないように）
                foreach (var map in loaded)
                {
                    meshBakeSettings.ApplyKindKey(map.Kind, map.Provenance.SettingsKey, map.Provenance.Padding);
                    meshBakeSettings.Antialiasing = map.Provenance.Antialiasing; meshBakeSettings.ApplySourceKey(map.Provenance.Source);
                }
                meshBakeSettings.Maps = loaded.Select(m => m.Kind).ToArray();
                var expected = CurrentMeshMapExpectation();
                var stale = loaded.Where(m => m.Provenance.Check(expected).State == MeshMapState.Stale).Select(m => m.Kind.ToString()).ToList();
                notes.Add(loaded.Count + " mesh map(s) restored" + (stale.Count > 0 ? "; " + string.Join(", ", stale) + " are stale for the loaded model (see Mesh maps)." : "."));
            }
            savedMeshMapRevision = meshMaps.Revision; savingMeshMapRevision = -1;
        }
    }
}
