using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// mesh map のベイク（サブスタンス由来の機能の土台）: 読み込んだモデルのスナップショット（<see cref="IsolatedModelPreview.Geometry"/>）から、
    /// テクスチャセットの材質スロットとドキュメントの大きさで法線・位置・AO・曲率・厚みを焼く。マップはテクスチャセットごとに持つ（欄に出すのは
    /// 今のセットのもの。焼く設定はプロジェクトで 1 つ）。結果は描くレイヤーではない派生物で、Undo に入らず、ドキュメントの版も変えない。
    /// .ylp にセットごとの sets/&lt;ID&gt;/meshmap-&lt;種類&gt;.bin として保存し、開いたときに戻す。由来（モデルの指紋・大きさ・スロット・設定・
    /// エンジンの版）が今と違うマップは「古い」と表示し、黙って使わない。キャンバスには読むだけの重ね表示で見せる。
    /// 焼くマップと設定はベイクの窓で選び、ベイクの仕事は TexturePaintWindow.MeshBake.cs。プロパティの欄の区画は焼いたマップの一覧と
    /// 窓を開く口だけ。元のモデル・マテリアル・テクスチャ・取り込み設定には触れない（プレビューの複製の三角形を読むだけ）。
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

        /// <summary>焼く条件（プロジェクトで 1 つ）。ウィンドウの状態としてシリアライズし、スクリプトのコンパイル（ドメインのリロード）をまたいで残す。</summary>
        [SerializeField] MeshBakeSettings meshBakeSettings = new MeshBakeSettings();
        MeshMapView meshMapView = MeshMapView.None; float meshMapOpacity = 1;
        Texture2D meshMapOverlay; long overlayRevision = -1; MeshMapView overlayView = MeshMapView.None;
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
        /// <summary>テクスチャセットの設定のメッシュマップのセクションが見えているか（既定は閉。モデルを読み込んでから使う区画なので）。
        /// true にするとセクションを開き、テクスチャセットの設定のタブを見せる。</summary>
        internal bool ShowMeshMapPanel
        {
            get => SectionIsOpen("mesh-maps", false) && PanelShown("textureSetSettings");
            set { sectionOpen["mesh-maps"] = value; if (value && !PanelShown("textureSetSettings")) Layout.SetActive("textureSetSettings"); }
        }
        /// <summary><see cref="BakeMeshMaps"/>（呼んだ所で待つベイク）の進み具合（題、説明、0〜1）。true を返すと取消。既定は Unity の取消
        /// できる進捗バー（テストは差し替える）。ベイクの窓からのベイクは窓の中に進み具合を出すので、これを使わない。</summary>
        internal Func<string, string, float, bool> MeshBakeProgress = EditorUtility.DisplayCancelableProgressBar;

        /// <summary>プレビューのスナップショットから焼き込みの入力を作る。同じスナップショットのあいだは作り直さない。モデルが無ければ null。
        /// 頂点法線は形から作り直す（<see cref="MeshMapNormalCrease"/>）。</summary>
        internal MeshBakeInput CurrentMeshBakeInput()
        {
            var geometry = preview != null ? preview.Geometry : null;
            if (geometry == null || geometry.TriangleCount == 0) { meshBakeInputFor = null; meshBakeInput = null; return null; }
            if (ReferenceEquals(geometry, meshBakeInputFor)) return meshBakeInput;
            meshBakeInput = BuildMeshBakeInput(geometry, preview.Attributes, MaterialIdentityKeys(preview));
            meshBakeInputFor = geometry;
            return meshBakeInput;
        }

        /// <summary>スナップショットの三角形（位置・UV0・スロット・レンダラー）と属性（頂点法線・接線・頂点カラー・レンダラー名）を
        /// 焼き込みの入力にする。属性が無い（または並びが合わない）ときは、頂点法線を形から作り直し、接線・色・名前は無し。</summary>
        internal static MeshBakeInput BuildMeshBakeInput(SurfaceGeometry geometry, SurfaceAttributes attributes = null, IReadOnlyList<string> materialKeys = null)
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
                return new MeshBakeInput(corners, MeshBakeInput.ReconstructNormals(corners, MeshMapNormalCrease), uvs, slots, 0, "reconstructed-crease-" + MeshMapNormalCrease, renderers: renderers, materialKeys: materialKeys);
            var names = attributes.RendererNames;
            if (names != null) foreach (int r in renderers) if (r >= names.Count) { names = null; break; }
            return new MeshBakeInput(corners, attributes.Normals, uvs, slots, 0, "authored", attributes.Tangents, attributes.Colors, renderers, names, materialKeys);
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
            if (!ReferenceEquals(geometry, highPolyInputFor)) { highPolyInput = BuildMeshBakeInput(geometry, highPolyPreview.Attributes, MaterialIdentityKeys(highPolyPreview)); highPolyInputFor = geometry; }
            return highPolyInput;
        }
        internal GameObject HighPolyModel { get => highPolyModel; set { highPolyModel = value; Repaint(); } }
        void DisposeHighPoly() { highPolyPreview?.Dispose(); highPolyPreview = null; highPolyLoaded = null; highPolyInput = null; highPolyInputFor = null; highPolyNote = null; }

        /// <summary>マップを使う側の今の条件（モデル・今のテクスチャセットのドキュメントの大きさ・スロット・欄の設定）。</summary>
        internal MeshMapExpectation CurrentMeshMapExpectation() => MeshMapExpectationOf(document, currentSet);
        /// <summary>テクスチャセットのマップの今の条件。</summary>
        MeshMapExpectation MeshMapExpectationFor(TextureSet set) => set == currentSet ? CurrentMeshMapExpectation() : MeshMapExpectationOf(set.Document, set);
        /// <summary>セットのマテリアルを使う全部のスロット（モデルに無ければ −1 の 1 つ: どのマップとも合わない）。</summary>
        MeshMapExpectation MeshMapExpectationOf(Core.PaintDocument d, TextureSet set)
        {
            var input = CurrentMeshBakeInput();
            var slots = set != null && set.InModel ? set.Slots.ToArray() : null;
            return new MeshMapExpectation
            {
                MeshHash = input?.Hash, TopologyHash = input?.TopologyHash, ReferenceHash = CurrentHighPolyInput()?.Hash, Width = d.Width, Height = d.Height,
                TargetSlot = slots != null ? slots[0] : -2, TargetSlots = slots, UvChannel = 0, Settings = meshBakeSettings.WithIdContext(input, CurrentHighPolyInput(), d.IdColors),
            };
        }
        /// <summary>焼いたスロットの見せ方（"0" や "0, 2"）。</summary>
        static string SlotList(MeshMapProvenance p) => p.TargetSlots.Count == 0 ? "—" : string.Join(", ", p.TargetSlots);

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
        /// プロパティの欄のメッシュマップの区画: ベイクの窓を開く口（焼いている間は進み具合と取消）と、ベイク済みのマップ（状態・古い理由・
        /// キャンバスへの重ね表示・消去）。焼くマップとその設定はベイクの窓（<see cref="MeshBakeWindow"/>）で選ぶ（同じ <see cref="MeshBakeSettings"/>）。
        /// </summary>
        void DrawMeshMapPanel(UiRows rows)
        {
            var expected = meshMaps.Count > 0 ? CurrentMeshMapExpectation() : null;
            if (meshBakeJob != null) DrawMeshBakeProgressRow(rows);
            else if (PaintGui.Button(Spot("meshmap.bake", rows.Row(28, 6)), L.Tr("Bake Mesh Maps…"), true, GUI.enabled && stroke == null,
                    L.Tr("Open the bake window: check the maps, set them up and bake them from the loaded model"), "local_fire_department"))
                TryAction(() => OpenMeshBakeWindow());
            if (preview == null || !preview.HasModel) NoteRow(rows, L.Tr("Load a model in Texture Set (or 3D ▸ Demo Cube) to bake."), NoteKind.Info);
            if (meshMaps.Count > 0) DrawBakedMeshMaps(rows, expected);
            else if (preview != null && preview.HasModel) NoteRow(rows, L.Tr("No mesh maps are baked yet."), NoteKind.Plain);
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
                PaintGui.Tooltip(row, L.Tr("{0} × {1} · slot {2}", map.Width, map.Height, SlotList(map.Provenance)));
                if (check.State == MeshMapState.Stale) NoteRow(rows, L.Tr("{0} is stale and is not used: {1} Bake again to update it.", MeshMapLabel(map.Kind), string.Join(" ", check.Reasons).Replace(";", "; ")), NoteKind.Warning, 18); // 由来の条件の文字列は ; で折り返せるようにする
            }
            if (expected.MeshHash == null) NoteRow(rows, L.Tr("No model is loaded, so the maps cannot be checked against it."), NoteKind.Info);
            rows.Space(2);
            var views = new List<MeshMapView> { MeshMapView.None, MeshMapView.Coverage };
            views.AddRange(meshMaps.Maps.Select(m => (MeshMapView)m.Kind));
            var current = views.Contains(meshMapView) ? meshMapView : MeshMapView.None;
            ChoiceDropdown(rows.Row(), L.Tr("Show on canvas"), current, views.ToArray(), MeshMapViewName, v => { meshMapView = v; Repaint(); }, L.Tr("Read-only overlay; nothing is painted"));
            if (meshMapView != MeshMapView.None)
                meshMapOpacity = (float)PaintGui.KeepSlider(rows.SliderRow(), L.Tr("Overlay opacity"), meshMapOpacity, 0, 1, "0", "%", null, true, 100);
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
                case MeshIdSource.MaterialAsset: return L.Tr("Material asset");
                case MeshIdSource.Mesh: return L.Tr("Mesh");
                case MeshIdSource.VertexColor: return L.Tr("Vertex color");
                case MeshIdSource.UvIsland: return L.Tr("UV island");
                case MeshIdSource.MeshPart: return L.TrIn("3D pick", "Mesh Part");
                default: return L.TrIn("mesh map", "Material slot (submesh)");
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
        /// <summary>OnDisable から（窓を閉じる・ドメインのリロード）。走っているベイクは取り消して終わるまで待ち、結果は捨てる。ベイクの窓は
        /// 自分で持ち主が閉じたことに気づいて閉じる（リロードでは閉じずに残る）。</summary>
        void DisposeMeshMaps() { AbandonMeshBake(); DisposeMeshMapOverlay(); DisposeHighPoly(); }

        /// <summary>ドキュメントを替えたとき（BindDocument から）。マップは前のドキュメントのものなので捨てる。</summary>
        void ClearMeshMaps()
        {
            // 前のドキュメントのために走っているベイクは止める（結果は前のドキュメントのもの）
            if (AbandonMeshBake()) message = L.Tr("The mesh-map bake was stopped because the document changed.");
            meshMaps.Clear(); savedMeshMapRevision = meshMaps.Revision; savingMeshMapRevision = -1;
            meshMapNote = null; lastMeshBakeReport = null; meshMapView = MeshMapView.None;
        }

        /// <summary>保存する中身に全部のテクスチャセットの mesh map を足す（SaveProject から。sets/&lt;ID&gt;/meshmap-&lt;種類&gt;.bin）。入れると .ylp の
        /// 予算を超えるなら、どのセットのマップも入れずに知らせる（ドキュメントそのものは保存する。マップは焼き直せる派生物）。</summary>
        void AddMeshMapFiles(Dictionary<string, byte[]> files)
        {
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var set in textureSets)
            {
                set.MeshMapNote = null; set.SavingMeshMapRevision = set.MeshMaps.Revision;
                foreach (var map in set.MeshMaps.Maps) entries[YlpFormat.SetEntry(set.Id, MeshMapBinary.EntryName(map.Kind))] = MeshMapBinary.Write(map);
            }
            if (entries.Count == 0) return;
            string problem = MeshMapSaveProblem(files.Values.Sum(b => b.LongLength), files.Count, entries.Values.Select(b => b.LongLength));
            if (problem != null)
            {
                string note = "Mesh maps were left out of the last save: " + problem + " The document itself was saved; bake the maps again after opening it.";
                foreach (var set in textureSets) { set.SavingMeshMapRevision = -1; if (set.MeshMaps.Count > 0) set.MeshMapNote = note; }
                Debug.LogWarning("Texture Painter: " + note);
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
        void MeshMapsWereSaved() { foreach (var set in textureSets) if (set.SavingMeshMapRevision >= 0) set.SavedMeshMapRevision = set.SavingMeshMapRevision; }

        /// <summary>開いた .ylp のテクスチャセットの mesh map を戻す（OpenProjectAt から、モデルを読んだ後。files はセットの下の名前）。読めないもの・
        /// 知らない種類は捨てたことを知らせる（ドキュメントは開く。マップは焼き直せる派生物）。焼く設定はプロジェクトで 1 つなので、保存したマップの
        /// 条件にそろえる（セットごとに違う設定で焼いたマップは、最後に読んだもの以外が古いと出る）。</summary>
        void LoadMeshMapFiles(TextureSet set, IReadOnlyDictionary<string, byte[]> files, List<string> notes)
        {
            string who = SetNotePrefix(set);
            var loaded = new List<BakedMeshMap>();
            foreach (var entry in files.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!entry.Key.StartsWith(MeshMapBinary.EntryPrefix, StringComparison.Ordinal)) continue;
                if (!MeshMapBinary.TryParseEntryName(entry.Key, out var kind)) { notes.Add(who + "Unknown mesh map " + entry.Key + " (from a newer YoluPainter?) was not loaded."); continue; }
                try
                {
                    var map = MeshMapBinary.Read(entry.Value);
                    if (map.Kind != kind) throw new InvalidDataException("it holds " + map.Kind + ".");
                    loaded.Add(map);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException)
                { notes.Add(who + "Mesh map " + kind + " could not be read (" + ex.Message + "); bake it again."); }
            }
            if (loaded.Count > 0)
            {
                set.MeshMaps.Put(loaded);
                // 欄の設定を、保存したマップの条件にそろえる（開いただけで「設定が変わった」と古くならないように）
                foreach (var map in loaded)
                {
                    meshBakeSettings.ApplyKindKey(map.Kind, map.Provenance.SettingsKey, map.Provenance.Padding);
                    meshBakeSettings.Antialiasing = map.Provenance.Antialiasing; meshBakeSettings.ApplySourceKey(map.Provenance.Source);
                }
                var kinds = new HashSet<MeshMapKind>(loaded.Select(m => m.Kind));
                if (meshMapsLoadedInto.Count > 0) kinds.UnionWith(meshBakeSettings.Maps ?? Array.Empty<MeshMapKind>());
                meshBakeSettings.Maps = MeshBakeSettings.AllKinds.Where(kinds.Contains).ToArray();
                meshMapsLoadedInto.Add(set.Id);
                var expected = MeshMapExpectationFor(set);
                var stale = loaded.Where(m => m.Provenance.Check(expected).State == MeshMapState.Stale).Select(m => m.Kind.ToString()).ToList();
                notes.Add(who + loaded.Count + " mesh map(s) restored" + (stale.Count > 0 ? "; " + string.Join(", ", stale) + " are stale for the loaded model (see Mesh maps)." : "."));
            }
            set.SavedMeshMapRevision = set.MeshMaps.Revision; set.SavingMeshMapRevision = -1;
        }
        /// <summary>今開いているファイルで、マップを読んだセット（2 つ目からは焼くマップの一覧を足し合わせる）。開くたびに空にする。</summary>
        readonly HashSet<Guid> meshMapsLoadedInto = new HashSet<Guid>();
    }
}
