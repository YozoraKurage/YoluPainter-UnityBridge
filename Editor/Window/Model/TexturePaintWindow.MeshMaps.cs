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
        /// <summary>プロパティの欄のメッシュマップのセクションが開いているか（既定は閉。長い欄なので）。</summary>
        internal bool ShowMeshMapPanel { get => SectionIsOpen("mesh-maps", false); set => sectionOpen["mesh-maps"] = value; }
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
            if (preview == null || !preview.HasModel) { message = L.Tr("Load a model (or the demo cube) before baking mesh maps."); return null; }
            if (!preview.CanPaint) { message = L.Tr("Mesh maps are baked only from a complete static snapshot that can be painted; this one is incomplete (see the load diagnostics)."); return null; }
            var settings = meshBakeSettings.Clone();
            settings.Width = document.Width; settings.Height = document.Height; settings.TargetSlot = materialSlot;
            var reference = CurrentHighPolyInput();
            if (highPolyModel != null && reference == null) { message = L.Tr("Mesh maps were not baked: {0}", highPolyNote ?? L.Tr("the high poly has no readable mesh.")); return null; }
            MeshBakeResult result;
            try
            {
                settings.Validate();
                result = RunMeshBake(CurrentMeshBakeInput(), settings, new MeshBakeBudget { MaxBytes = PainterSettings.StrokeBudgetBytes }, reference);
            }
            catch (Exception ex) when (ex is MeshBakeRefusedException || ex is ArgumentException)
            { message = L.Tr("Mesh maps were not baked: {0}", ex.Message); return null; }
            finally { Dialogs.ClearProgress(); }
            lastMeshBakeReport = result.Report;
            if (result.Status != MeshBakeStatus.Completed)
            {
                message = result.Status == MeshBakeStatus.TimedOut ? L.Tr("The mesh-map bake hit its time limit; the previous maps are unchanged.") : L.Tr("The mesh-map bake was canceled; the previous maps are unchanged.");
                return result.Status;
            }
            meshMaps.Put(result.Maps);
            if (meshMapView == MeshMapView.None) meshMapView = MeshMapView.Coverage;
            message = L.Tr("Baked {0} at {1}×{2} for slot {3} (rays: {4}): {5}. Mesh maps are derived data, not layers; Save keeps them in the .ylp.",
                string.Join(", ", result.Maps.Select(m => MeshMapLabel(m.Kind))), settings.Width, settings.Height, settings.TargetSlot, result.Report.RayBackend, result.Report.Summary());
            Repaint();
            return result.Status;
        }

        /// <summary>ベイクは別のスレッドで回し、ここ（主スレッド）で進捗バーを出して取消を受ける。始める前にも 1 回尋ねる。</summary>
        MeshBakeResult RunMeshBake(MeshBakeInput input, MeshBakeSettings settings, MeshBakeBudget budget, MeshBakeInput reference)
        {
            string title = L.Tr("Baking mesh maps");
            // GPU の呼び出しは主スレッドでしかできないので、別のスレッドのベイクから回ってきた仕事をこのループで行う
            var tracer = meshBakeUseGpu ? new GpuMeshBakeRayTracer(PainterSettings.GpuCacheBytes) : null;
            using (var cancel = new CancellationTokenSource())
            {
                if (MeshBakeProgress(title, L.Tr("Preparing…"), 0)) cancel.Cancel();
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

        /// <summary>高ポリを選ぶオブジェクトピッカーの印（ExecuteCommand で結果を見分ける）。</summary>
        const int HighPolyPickerId = 0x59500010;

        /// <summary>セクションの見出し: 古いマップがあれば数を添える。</summary>
        string MeshMapsTitle()
        {
            int stale = 0;
            if (meshMaps.Count > 0)
            {
                var expected = CurrentMeshMapExpectation();
                stale = meshMaps.Maps.Count(m => m.Provenance.Check(expected).State == MeshMapState.Stale);
            }
            return L.Tr("Mesh Maps") + (stale > 0 ? " · " + L.Tr("{0} stale", stale) : "");
        }

        /// <summary>
        /// メッシュマップのベイクの欄（Substance Painter の「メッシュマップをベイク」の配置）: 出力の大きさ・スロット、ベイクするマップ、
        /// 共通の設定、高ポリ、種類ごとの設定、ベイクのボタン、ベイク済みのマップ（状態・キャンバスへの重ね表示・消去）。欄の範囲は
        /// 扱える範囲より狭く、保存したマップの値が範囲の外でも触らなければそのまま（「設定が変わった」と古くしない）。
        /// </summary>
        void DrawMeshMapPanel(UiRows rows)
        {
            var s = meshBakeSettings;
            var expected = meshMaps.Count > 0 ? CurrentMeshMapExpectation() : null;
            bool was = GUI.enabled; GUI.enabled = was && stroke == null;
            try
            {
                PaintGui.ValueBox(rows.Row(), L.TrIn("mesh map", "Output"), L.Tr("{0} × {1} · slot {2}", document.Width, document.Height, materialSlot), PropertyLabelWidth, null,
                    L.Tr("Mesh maps are baked at the document size, for the material slot this document paints"));
                // ベイクするマップ（2 列）
                PaintGui.GroupLabel(rows.Row(16), L.Tr("Maps to Bake"));
                var kinds = MeshBakeSettings.AllKinds;
                bool kindsChanged = false; var picked = new List<MeshMapKind>();
                for (int i = 0; i < kinds.Count; i += 2)
                {
                    var c = UiRows.Split(rows.Row(20, 2), 2, 6);
                    for (int k = 0; k < 2 && i + k < kinds.Count; k++)
                    {
                        var kind = kinds[i + k]; bool on = s.Includes(kind);
                        bool next = PaintGui.FitToggle(Spot("meshmap.kind." + kind, c[k]), MeshMapGridLabel(kind), on, MeshMapLabel(kind));
                        if (next != on) kindsChanged = true;
                        if (next) picked.Add(kind);
                    }
                }
                rows.Space(2);
                if (kindsChanged && picked.Count > 0) s.Maps = picked.ToArray(); // 1 つも無しにはしない
                // 共通
                PaintGui.GroupLabel(rows.Row(16), L.TrIn("mesh map", "Common"));
                s.Padding = PaintGui.KeepIntSlider(Spot("meshmap.padding", rows.Row()), L.TrIn("mesh map", "Padding"), s.Padding, 0, MeshBakeSettings.MaxPadding, " px", L.Tr("Texels the islands are extended into the empty space around them (never over another island)"));
                ChoiceDropdown(rows.Row(), L.TrIn("mesh map", "Antialiasing"), s.Antialiasing, new[] { 1, 2, 3, 4 }, n => n == 1 ? L.Tr("None") : n + " × " + n, n => meshBakeSettings.Antialiasing = n,
                    L.Tr("Subsamples per texel side (the bake takes n² times as long)"));
                if (s.Includes(MeshMapKind.AmbientOcclusion) || s.Includes(MeshMapKind.Thickness) || s.Includes(MeshMapKind.BentNormal))
                    ChoiceDropdown(rows.Row(), L.TrIn("mesh map", "Occluders"), s.Occluders, (MeshOccluders[])Enum.GetValues(typeof(MeshOccluders)), OccludersName, v => meshBakeSettings.Occluders = v,
                        L.Tr("Which faces block AO and thickness rays"));
                meshBakeUseGpu = PaintGui.FitToggle(rows.Row(), L.Tr("Use the GPU for rays"), meshBakeUseGpu,
                    L.Tr("Ambient occlusion, bent normal and thickness rays run in a compute shader (same formulas, float precision). Falls back to the CPU when the GPU cannot."));
                // 高ポリ
                PaintGui.GroupLabel(rows.Row(16), L.Tr("High Poly (optional)"));
                var high = highPolyModel;
                if (PaintGui.ObjectBox(rows.Row(), ref high, HighPolyPickerId, true, L.Tr("None (drop a high poly here)"), "view_in_ar",
                        L.Tr("A second model whose detail is projected onto this one (meshes are read only; nothing is instantiated or changed)"), L.Tr("Stop using the high poly")))
                { highPolyModel = high; CurrentHighPolyInput(); Repaint(); }
                if (highPolyModel != null)
                {
                    CurrentHighPolyInput(); // 選んであれば読む（同じものなら読み直さない）。数と知らせを出すため
                    var c = UiRows.Split(rows.Row(), 2, 6);
                    s.ReferenceFrontal = PaintGui.KeepSlider(c[0], L.TrIn("mesh map", "Frontal"), s.ReferenceFrontal, .0001, .2, "0.####", "", L.Tr("Max frontal distance: how far outside the low poly the high poly is searched (relative to the bounding-box diagonal)"));
                    s.ReferenceRear = PaintGui.KeepSlider(c[1], L.TrIn("mesh map", "Rear"), s.ReferenceRear, .0001, .2, "0.####", "", L.Tr("Max rear distance: how far inside the low poly the high poly is searched"));
                    c = UiRows.Split(rows.Row(), 2, 6);
                    s.ReferenceAverageNormals = PaintGui.FitToggle(c[0], L.Tr("Average normals"), s.ReferenceAverageNormals, L.Tr("Project along normals averaged over hard edges (a cage without gaps)"));
                    s.ReferenceMatchByName = PaintGui.FitToggle(c[1], L.Tr("Match by name"), s.ReferenceMatchByName, L.Tr("Project 'part_low' only onto 'part_high' (or 'part')"));
                    var reference = highPolyPreview != null ? highPolyPreview.Geometry : null;
                    var row = rows.Row();
                    PaintGui.ValueBox(new Rect(row.x, row.y, row.width - 28, row.height), L.Tr("Triangles"), reference != null ? reference.TriangleCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : L.Tr("Not readable"), DropdownLabelWidth,
                        reference != null ? (Color?)null : PaintTheme.Warning, L.Tr("Triangles read from the high poly"));
                    if (PaintGui.IconButton(new Rect(row.xMax - 24, row.y, 24, row.height), "sync", L.Tr("Read the high poly's meshes again (after editing them)"), false, GUI.enabled, 16)) { DisposeHighPoly(); CurrentHighPolyInput(); }
                    if (highPolyNote != null) NoteRow(rows, highPolyNote, NoteKind.Warning);
                }
                // 種類ごとの設定
                if (s.Includes(MeshMapKind.AmbientOcclusion) || s.Includes(MeshMapKind.BentNormal))
                {
                    PaintGui.GroupLabel(rows.Row(16), MeshMapLabel(MeshMapKind.AmbientOcclusion));
                    var c = UiRows.Split(rows.Row(), 2, 6);
                    s.AoSamples = PaintGui.KeepIntSlider(c[0], L.TrIn("mesh map", "Rays"), s.AoSamples, 1, 512, "", L.Tr("Rays per texel (more is smoother and slower)"));
                    s.AoMaxDistance = PaintGui.KeepSlider(c[1], L.TrIn("mesh map", "Distance"), s.AoMaxDistance, .001, 1, "0.###", "", L.Tr("Max distance, relative to the model's bounding-box diagonal"));
                    c = UiRows.Split(rows.Row(), 2, 6);
                    s.AoSpreadDegrees = PaintGui.KeepSlider(c[0], L.TrIn("mesh map", "Spread"), s.AoSpreadDegrees, 1, 180, "0", "°", L.Tr("180° = the whole hemisphere"));
                    s.AoIgnoreBackfaces = PaintGui.FitToggle(c[1], L.Tr("Ignore back faces"), s.AoIgnoreBackfaces, L.Tr("Rays pass through faces seen from behind"));
                    ChoiceDropdown(rows.Row(), L.TrIn("mesh map", "Falloff"), s.AoFalloff, (MeshOcclusionFalloff[])Enum.GetValues(typeof(MeshOcclusionFalloff)), FalloffName, v => meshBakeSettings.AoFalloff = v,
                        L.Tr("Linear: nearer occluders darken more"));
                }
                if (s.Includes(MeshMapKind.Curvature))
                {
                    PaintGui.GroupLabel(rows.Row(16), MeshMapLabel(MeshMapKind.Curvature));
                    s.CurvatureRadius = PaintGui.KeepSlider(rows.Row(), L.TrIn("mesh map", "Radius"), s.CurvatureRadius, MeshBakeSettings.MinCurvatureRadius, .2, "0.###", "",
                        L.Tr("Edges within this distance count (relative to the bounding-box diagonal). Larger = wider, softer edges"));
                }
                if (s.Includes(MeshMapKind.Thickness))
                {
                    PaintGui.GroupLabel(rows.Row(16), MeshMapLabel(MeshMapKind.Thickness));
                    var c = UiRows.Split(rows.Row(), 2, 6);
                    s.ThicknessSamples = PaintGui.KeepIntSlider(c[0], L.TrIn("mesh map", "Rays"), s.ThicknessSamples, 1, 512, "", L.Tr("Rays per texel (more is smoother and slower)"));
                    s.ThicknessMaxDistance = PaintGui.KeepSlider(c[1], L.TrIn("mesh map", "Distance"), s.ThicknessMaxDistance, .001, 1, "0.###", "", L.Tr("Max distance, relative to the bounding-box diagonal; thicker parts read 1"));
                    s.ThicknessSpreadDegrees = PaintGui.KeepSlider(rows.Row(), L.TrIn("mesh map", "Spread"), s.ThicknessSpreadDegrees, 1, 180, "0", "°", L.Tr("180° = the whole hemisphere"));
                }
                if (s.Includes(MeshMapKind.Id))
                {
                    PaintGui.GroupLabel(rows.Row(16), MeshMapLabel(MeshMapKind.Id));
                    ChoiceDropdown(rows.Row(), L.Tr("Colors from"), s.IdSource, (MeshIdSource[])Enum.GetValues(typeof(MeshIdSource)), IdSourceName, v => meshBakeSettings.IdSource = v,
                        L.Tr("What gets its own colour in the ID map"));
                }
                rows.Space(4);
                bool hasModel = preview != null && preview.HasModel;
                if (PaintGui.Button(Spot("meshmap.bake", rows.Row(28, 6)), L.Tr("Bake Mesh Maps"), true, GUI.enabled && hasModel, L.Tr("Bake the checked maps from the loaded model. The model, its materials and textures are not changed."), "local_fire_department"))
                    TryAction(() => BakeMeshMaps());
                if (!hasModel) NoteRow(rows, L.Tr("Load a model in Texture Set (or 3D ▸ Demo Cube) to bake."), NoteKind.Info);
            }
            finally { GUI.enabled = was; }
            if (meshMaps.Count > 0) DrawBakedMeshMaps(rows, expected);
            if (lastMeshBakeReport != null && lastMeshBakeReport.Diagnostics.Count > 0) NoteRow(rows, string.Join("\n", lastMeshBakeReport.Diagnostics), NoteKind.Info);
            if (meshMapNote != null) NoteRow(rows, meshMapNote, NoteKind.Warning);
        }

        /// <summary>ベイク済みのマップ: 種類ごとの状態（最新・古い・未確認）、古い理由、キャンバスへの重ね表示、消去。</summary>
        void DrawBakedMeshMaps(UiRows rows, MeshMapExpectation expected)
        {
            PaintGui.GroupLabel(rows.Row(16), L.Tr("Baked Maps"));
            foreach (var map in meshMaps.Maps)
            {
                var check = map.Provenance.Check(expected);
                var row = rows.Row(20, 2);
                var color = check.State == MeshMapState.Current ? new Color(.35f, .78f, .42f) : check.State == MeshMapState.Stale ? PaintTheme.Warning : PaintTheme.TextDim;
                PaintGui.Dot(new Rect(row.x, row.y, 12, row.height), color);
                string state = MeshMapStateName(check.State);
                float right = PaintGui.TextWidth(state, PaintTheme.LabelDim) + 4;
                PaintGui.Text(new Rect(row.x + 18, row.y, row.width - 18 - right, row.height), PaintGui.Fit(MeshMapLabel(map.Kind), row.width - 22 - right, PaintTheme.Label), PaintTheme.Label);
                PaintGui.Text(new Rect(row.xMax - right, row.y, right, row.height), state, StateStyle, color);
                PaintGui.Tooltip(row, L.Tr("{0} × {1} · slot {2}", map.Width, map.Height, map.Provenance.TargetSlot));
                if (check.State == MeshMapState.Stale) NoteRow(rows, L.Tr("{0} is stale and is not used: {1} Bake again to update it.", MeshMapLabel(map.Kind), string.Join(" ", check.Reasons).Replace(";", "; ")), NoteKind.Warning, 18); // 由来の条件の文字列は ; で折り返せるようにする
            }
            if (expected.MeshHash == null) NoteRow(rows, L.Tr("No model is loaded, so the maps cannot be checked against it."), NoteKind.Info);
            rows.Space(2);
            var views = new List<MeshMapView> { MeshMapView.None, MeshMapView.Coverage };
            views.AddRange(meshMaps.Maps.Select(m => (MeshMapView)m.Kind));
            var current = views.Contains(meshMapView) ? meshMapView : MeshMapView.None;
            ChoiceDropdown(rows.Row(), L.Tr("Show on canvas"), current, views.ToArray(), MeshMapViewName, v => { meshMapView = v; Repaint(); }, L.Tr("Read-only overlay; nothing is painted"));
            if (meshMapView != MeshMapView.None)
                meshMapOpacity = (float)PaintGui.KeepSlider(rows.Row(), L.Tr("Overlay opacity"), meshMapOpacity, 0, 1, "0", "%", null, true, 100);
            if (meshMapView == MeshMapView.Coverage)
            {
                var row = rows.Row(18);
                var legend = new[] { (L.TrIn("mesh map", "Baked"), new Color(.2f, .8f, .3f)), (L.Tr("Overlapping UVs"), new Color(.9f, .25f, .2f)), (L.TrIn("mesh map", "Padding"), new Color(.25f, .45f, .95f)) };
                float x = row.x;
                foreach (var (label, c) in legend)
                {
                    PaintGui.Dot(new Rect(x, row.y, 10, row.height), c);
                    float w = PaintGui.TextWidth(label, PaintTheme.LabelDim);
                    PaintGui.Text(new Rect(x + 13, row.y, Mathf.Min(w + 2, row.xMax - x - 13), row.height), label, PaintTheme.LabelDim);
                    x += 13 + w + 12;
                }
            }
            if (!MeshMapsSaved) NoteRow(rows, L.Tr("These mesh maps are not in the saved file yet. Save keeps them in the .ylp (they are not part of Undo)."));
            if (PaintGui.Button(rows.Row(), L.Tr("Clear Mesh Maps"), false, GUI.enabled && stroke == null, L.Tr("Remove the baked maps (derived data; the layers do not change)"), "delete"))
            { meshMaps.Clear(); meshMapView = MeshMapView.None; message = L.Tr("Cleared the mesh maps (derived data; nothing in the layers changed)."); }
        }

        static string MeshMapLabel(MeshMapKind kind)
        {
            switch (kind)
            {
                case MeshMapKind.WorldNormal: return L.TrIn("mesh map", "Normal (world)");
                case MeshMapKind.Position: return L.TrIn("mesh map", "Position");
                case MeshMapKind.AmbientOcclusion: return L.TrIn("mesh map", "Ambient occlusion");
                case MeshMapKind.Curvature: return L.TrIn("mesh map", "Curvature");
                case MeshMapKind.Thickness: return L.TrIn("mesh map", "Thickness");
                case MeshMapKind.TangentNormal: return L.TrIn("mesh map", "Normal (tangent)");
                case MeshMapKind.Height: return L.Tr("Height");
                case MeshMapKind.Id: return L.TrIn("mesh map", "ID");
                case MeshMapKind.BentNormal: return L.TrIn("mesh map", "Bent normal");
                default: return L.Tr("Opacity");
            }
        }
        /// <summary>ベイクするマップの 2 列の欄の名前（長い名前は欄に収まる短い名前。ツールチップは <see cref="MeshMapLabel"/>）。</summary>
        static string MeshMapGridLabel(MeshMapKind kind) => kind == MeshMapKind.AmbientOcclusion ? L.TrIn("mesh map short", "AO") : MeshMapLabel(kind);
        static string MeshMapViewName(MeshMapView v) => v == MeshMapView.None ? L.Tr("None") : v == MeshMapView.Coverage ? L.Tr("UV coverage") : MeshMapLabel((MeshMapKind)v);
        static GUIStyle s_stateStyle;
        static GUIStyle StateStyle => s_stateStyle ?? (s_stateStyle = new GUIStyle(PaintTheme.LabelDim) { alignment = TextAnchor.MiddleRight });
        static string MeshMapStateName(MeshMapState state)
        {
            switch (state)
            {
                case MeshMapState.Current: return L.TrIn("mesh map", "current");
                case MeshMapState.Stale: return L.TrIn("mesh map", "stale");
                case MeshMapState.Unverified: return L.TrIn("mesh map", "unverified");
                default: return L.TrIn("mesh map", "missing");
            }
        }
        static string OccludersName(MeshOccluders o) => o == MeshOccluders.TargetSlotOnly ? L.Tr("This slot only") : L.Tr("Whole model");
        static string FalloffName(MeshOcclusionFalloff f) => f == MeshOcclusionFalloff.Linear ? L.Tr("Linear") : L.Tr("None");
        static string IdSourceName(MeshIdSource s)
        {
            switch (s)
            {
                case MeshIdSource.Mesh: return L.Tr("Mesh");
                case MeshIdSource.VertexColor: return L.Tr("Vertex color");
                case MeshIdSource.UvIsland: return L.Tr("UV island");
                default: return L.TrIn("mesh map", "Material slot");
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
