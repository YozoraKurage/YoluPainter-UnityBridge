using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// ポリゴン塗りつぶし（Substance Painter の Polygon Fill）: 3D ビューでクリックした三角形か、2D キャンバスでクリックした UV の三角形
    /// （今のテクスチャセットのスロットのもの）から範囲（三角形・メッシュの塊・UV アイランド・マテリアル。「3D Pick」と同じ設定）をたどり、
    /// その UV を塗る・消す。押したままドラッグすると通った範囲を足していき、離すと 1 回の Undo になる。塗るのは Core の
    /// <see cref="TriangleFill"/>（範囲の和集合を選択範囲にして Fill したのと同じ画素）で、窓のストロークとして扱うので、Esc・フォーカスを
    /// 失う・リロード・Play・例外で取り消される。ポインタの下の範囲は、3D ビューでは薄い色、2D キャンバスでは UV の輪郭で見せる（範囲を
    /// 引くのは三角形が変わったときだけ。<see cref="SurfaceRegionIndex"/>）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] bool polyFillErase;
        /// <summary>消す（画素の透明。マスクでは黒 = 隠す）か、塗る（描画色と不透明度。マスクでは白 = 見せる）か。</summary>
        internal bool PolygonFillErase { get => polyFillErase; set { polyFillErase = value; Repaint(); } }

        TriangleFill polyFill; bool polyFillOnSurface; Vector2 polyFillLast;
        readonly HashSet<long> polyFillRegions = new HashSet<long>();
        /// <summary>ポリゴン塗りつぶしのドラッグの最中か（その間は <see cref="stroke"/> がこのドラッグ）。</summary>
        internal bool IsPolygonFilling => polyFill != null && stroke != null;
        /// <summary>ドラッグの 1 回の入力で、前の位置からの線の上を調べる点の数の上限（画面で 4 px おき）。</summary>
        internal const int PolygonFillSamplesPerEvent = 64;

        /// <summary>強調の色: 塗るは橙、消すは桃色（UV のワイヤーフレームの水色と見分けられる色）。</summary>
        static readonly Color PaintHighlight = new Color(1f, .62f, .16f, 1f), EraseHighlight = new Color(1f, .36f, .62f, 1f);
        Color HighlightColor => polyFillErase ? EraseHighlight : PaintHighlight;

        // ───────── 範囲の索引（ジオメトリごと） ─────────

        SurfaceRegionIndex regionIndex;
        internal SurfaceRegionIndex RegionIndex()
        {
            var geometry = preview != null && preview.HasModel ? preview.Geometry : null;
            if (geometry == null) return null;
            if (regionIndex == null || !ReferenceEquals(regionIndex.Geometry, geometry)) regionIndex = new SurfaceRegionIndex(geometry);
            return regionIndex;
        }

        /// <summary>ポインタの下の、今のテクスチャセットの三角形（無ければ −1）。3D ビューはその面の三角形、2D キャンバスはその点の UV を含む
        /// スロットの三角形（重なった UV では番号の小さいもの）。</summary>
        int PolygonFillTriangleAt(Vector2 pointer, bool onSurface)
        {
            if (preview == null || !preview.CanPaint) return -1;
            if (onSurface)
            {
                if (!surfaceRect.Contains(pointer) || !preview.TryPick(surfaceRect, pointer, out var hit)) return -1;
                return hit.MaterialSlot == materialSlot ? hit.TriangleIndex : -1;
            }
            if (!canvasRect.Contains(pointer)) return -1;
            var p = CanvasPoint(pointer);
            return RegionIndex()?.TriangleAtUv(materialSlot, new Vector2(p.x / document.Width, p.y / document.Height)) ?? -1;
        }

        /// <summary>範囲の三角形の UV を、キャンバスの画素座標の三角形に（UV (0, 0) がキャンバスの左下。SurfaceRegions.Selection と同じ）。</summary>
        List<(double, double, double, double, double, double)> CanvasTriangles(IReadOnlyList<int> region)
        {
            var all = preview.Geometry.Triangles; double w = document.Width, h = document.Height;
            var list = new List<(double, double, double, double, double, double)>(region.Count);
            foreach (int i in region) { var t = all[i]; list.Add((t.UvA.x * w, t.UvA.y * h, t.UvB.x * w, t.UvB.y * h, t.UvC.x * w, t.UvC.y * h)); }
            return list;
        }

        // ───────── 入力 ─────────

        /// <summary>ポリゴン塗りつぶしのキャンバスと 3D ビューの入力（ドラッグの最中も）。扱ったら true。</summary>
        bool HandlePolygonFillInput(Event e)
        {
            if (polyFill == null && tool != PaintTool.PolygonFill) return false;
            if (e.type == EventType.MouseMove) { UpdatePolygonFillHover(e.mousePosition); return false; }
            if (e.type == EventType.MouseLeaveWindow) { ClearPolygonFillHover(); Repaint(); return false; }
            if (polyFill == null)
            {
                if (e.type != EventType.MouseDown || e.button != 0 || e.alt || stroke != null || toolDragging) return false;
                bool onSurface = surfaceRect.Contains(e.mousePosition);
                if (!onSurface && !canvasRect.Contains(e.mousePosition)) return false;
                var at = e.mousePosition;
                TryAction(() => BeginPolygonFill(at, onSurface));
                UpdatePolygonFillHover(at);
                e.Use(); Repaint(); return true;
            }
            if (e.type == EventType.MouseDrag && e.button == 0)
            {
                var at = e.mousePosition;
                TryAction(() => DragPolygonFill(at));
                UpdatePolygonFillHover(at);
                e.Use(); Repaint(); return true;
            }
            if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp) { FinishPolygonFill(); e.Use(); Repaint(); return true; }
            return false;
        }

        void BeginPolygonFill(Vector2 pointer, bool onSurface)
        {
            if (preview == null || !preview.HasModel) { message = L.Tr("Load a model (or the demo cube) to fill its polygons on the 3D view or its UVs on the 2D canvas."); return; }
            if (!preview.CanPaint) { message = "This preview snapshot is not safe to paint. See its load diagnostics."; return; }
            if (onSurface && preview.TryPick(surfaceRect, pointer, out var hit) && hit.MaterialSlot != materialSlot) { OtherSlotPressed(hit.MaterialSlot); return; }
            var layer = document.GetLayer(selectedLayer);
            TriangleFill fill;
            if (EditingMask) fill = document.BeginMaskTriangleFill(selectedLayer, brush.opacity, reveal: MaskFillReveals(layer));
            else
            {
                if (layer.IsGroup) throw new InvalidOperationException("A group has no pixels. Select a layer inside it to fill, or fill the group's mask.");
                if (layer.Kind != LayerKind.Raster) throw new InvalidOperationException("Fill paints pixels: select a paint layer, or edit the layer's mask.");
                document.EnsurePixelsEditable(selectedLayer, polyFillErase); // ロックで断るなら、チャンネルを有効にする前に（何も残さない）
                if (!layer.IsChannelEnabled(channel)) document.SetChannelEnabled(selectedLayer, channel, true);
                if (!polyFillErase) RememberColor();
                fill = brush.material ? document.BeginMaterialTriangleFill(selectedLayer, StrokeChannels(), brush.opacity, polyFillErase)
                    : document.BeginTriangleFill(selectedLayer, channel, BrushColor32(), brush.opacity, polyFillErase);
            }
            polyFill = fill; stroke = fill.Stroke; surfaceStroke = false; polyFillOnSurface = onSurface; polyFillRegions.Clear(); polyFillLast = pointer;
            GUIUtility.hotControl = GUIUtility.GetControlID(FocusType.Passive);
            AddPolygonFillAt(pointer);
        }

        /// <summary>マスクの「塗る」は白（見せる）、「消す」は黒（隠す）。白・黒はマスクのサムネイルの色なので、反転したマスクでは書く値を逆にする。</summary>
        bool MaskFillReveals(PaintLayer layer) => polyFillErase == (layer.Mask != null && layer.Mask.Inverted);

        Rgba32 BrushColor32() => new Rgba32((byte)Mathf.RoundToInt(brush.color.r * 255), (byte)Mathf.RoundToInt(brush.color.g * 255), (byte)Mathf.RoundToInt(brush.color.b * 255), (byte)Mathf.RoundToInt(brush.color.a * 255));

        /// <summary>前の位置からこの位置までの線の上（画面で 4 px おき、多くて <see cref="PolygonFillSamplesPerEvent"/> 点）の範囲を足す（速く
        /// 動かしても、間の三角形を飛ばしにくい）。ドラッグは始めたビューの中だけで効く。</summary>
        void DragPolygonFill(Vector2 pointer)
        {
            if (polyFill == null) return;
            var from = polyFillLast; float length = Vector2.Distance(from, pointer);
            int steps = Mathf.Clamp(Mathf.CeilToInt(length / 4), 1, PolygonFillSamplesPerEvent);
            for (int i = 1; i <= steps && polyFill != null; i++) AddPolygonFillAt(Vector2.Lerp(from, pointer, i / (float)steps));
            polyFillLast = pointer;
        }

        void AddPolygonFillAt(Vector2 pointer)
        {
            int triangle = PolygonFillTriangleAt(pointer, polyFillOnSurface);
            if (triangle < 0) return;
            var index = RegionIndex(); long key = index.Key(triangle, surfacePick);
            if (!polyFillRegions.Add(key)) return; // 足した範囲はもう塗ってある
            polyFill.Add(CanvasTriangles(index.Region(triangle, surfacePick)));
            repaintPixels = true;
        }

        void FinishPolygonFill()
        {
            var fill = polyFill; if (fill == null) return;
            int regions = polyFillRegions.Count; bool done = false, changed = false;
            TryAction(() => { changed = fill.Stroke.Commit(); done = true; }); // 失敗すれば TryAction が取り消す
            FinishStroke(false); // 確定したストロークを手放す（確定の後の取り消しは何もしない）
            if (!done) return;
            string what = SurfacePickName(surfacePick);
            message = changed ? (polyFillErase ? L.Tr("Erased {0} × {1}.", what, regions) : L.Tr("Filled {0} × {1}.", what, regions))
                : regions == 0 ? L.Tr("No triangle of this texture set under the pointer.") : L.Tr("Nothing to fill there.");
        }

        /// <summary>ストロークの終わり（FinishStroke）から: ドラッグの状態を捨てる。</summary>
        void EndPolygonFillDrag() { polyFill = null; polyFillRegions.Clear(); }

        // ───────── ポインタの下の範囲の強調 ─────────

        int hoverTriangle = -1; bool hoverOnSurface; SurfaceRegionKind hoverKind; SurfaceGeometry hoverGeometry; int hoverSlot = -1; long hoverKey = -1;
        Vector2[] hoverOutline; Vector3[] hoverOutlineGui; (int, int, float, Vector2, float, bool, Vector2, Vector2[]) hoverOutlineGuiKey;
        /// <summary>試験と計測用: 強調している範囲の鍵（無ければ −1）と、範囲を引き直した回数。</summary>
        internal long PolygonFillHoverKey => hoverKey;
        internal int PolygonFillHoverLookups { get; private set; }
        internal Vector2[] PolygonFillHoverOutline => hoverOutline;

        /// <summary>ポインタの下の範囲を求め直す（三角形が変わったときだけ範囲を引き、範囲の鍵が変わったときだけ見せる物を変える）。</summary>
        internal void UpdatePolygonFillHover(Vector2 pointer)
        {
            if (tool != PaintTool.PolygonFill) { ClearPolygonFillHover(); return; }
            bool onSurface = polyFill != null ? polyFillOnSurface : surfaceRect.Contains(pointer);
            int triangle = PolygonFillTriangleAt(pointer, onSurface);
            SetPolygonFillHover(triangle, onSurface);
        }

        void SetPolygonFillHover(int triangle, bool onSurface)
        {
            var geometry = preview != null && preview.HasModel ? preview.Geometry : null;
            if (triangle == hoverTriangle && onSurface == hoverOnSurface && surfacePick == hoverKind && ReferenceEquals(geometry, hoverGeometry) && materialSlot == hoverSlot) return;
            hoverTriangle = triangle; hoverOnSurface = onSurface; hoverKind = surfacePick; hoverGeometry = geometry; hoverSlot = materialSlot;
            if (triangle < 0 || geometry == null) { hoverKey = -1; hoverOutline = null; preview?.HideRegion(); return; }
            var index = RegionIndex(); long key = index.Key(triangle, surfacePick);
            if (key == hoverKey) return;
            hoverKey = key; PolygonFillHoverLookups++;
            preview.ShowRegion(key, index.Region(triangle, surfacePick), HighlightColor * new Color(1, 1, 1, .34f));
            hoverOutline = index.UvOutline(triangle, surfacePick); hoverOutlineGui = null;
        }

        void ClearPolygonFillHover()
        {
            if (hoverTriangle < 0 && hoverKey < 0) return;
            hoverTriangle = -1; hoverKey = -1; hoverOutline = null; hoverOutlineGui = null; hoverGeometry = null; preview?.HideRegion();
        }

        /// <summary>描く前に（Repaint ごと）: ツールが替わった・モデルや範囲の種類やテクスチャセットが変わったときに強調を合わせる。</summary>
        void SyncPolygonFillHover()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (tool != PaintTool.PolygonFill) { ClearPolygonFillHover(); return; }
            if (hoverTriangle < 0) return;
            var geometry = preview != null && preview.HasModel ? preview.Geometry : null;
            if (!ReferenceEquals(geometry, hoverGeometry) || hoverSlot != materialSlot) { ClearPolygonFillHover(); return; }
            if (hoverKind != surfacePick) { int t = hoverTriangle; hoverTriangle = -1; hoverKey = -1; SetPolygonFillHover(t, hoverOnSurface); }
            else preview.ShowRegion(hoverKey, RegionIndex().Region(hoverTriangle, surfacePick), HighlightColor * new Color(1, 1, 1, .34f)); // 色（塗る/消す）の切り替え
        }

        /// <summary>2D キャンバスのクリップの中で、強調している範囲の UV の輪郭を描く（Repaint のときだけ。UV のワイヤーフレームと同じく
        /// Handles.DrawLines で、写した座標は表示か範囲が変わったときだけ作り直す。暗い影を 1 px ずらして重ね、明るい所でも見える）。</summary>
        void DrawPolygonFillOutline(CanvasView view)
        {
            if (Event.current.type != EventType.Repaint || tool != PaintTool.PolygonFill || hoverOutline == null || hoverOutline.Length == 0) return;
            var key = (document.Width, document.Height, canvasZoom, canvasPan, canvasAngle, canvasFlip, canvasRect.size, hoverOutline);
            if (hoverOutlineGui == null || !hoverOutlineGuiKey.Equals(key))
            {
                if (hoverOutlineGui == null || hoverOutlineGui.Length != hoverOutline.Length) hoverOutlineGui = new Vector3[hoverOutline.Length];
                float w = document.Width, h = document.Height;
                for (int i = 0; i < hoverOutline.Length; i++) hoverOutlineGui[i] = view.ToGui(hoverOutline[i].x * w, hoverOutline[i].y * h);
                hoverOutlineGuiKey = key;
            }
            var color = Handles.color; var matrix = Handles.matrix;
            try
            {
                Handles.color = new Color(0, 0, 0, .7f); Handles.matrix = matrix * Matrix4x4.Translate(new Vector3(1, 1, 0)); Handles.DrawLines(hoverOutlineGui);
                Handles.color = HighlightColor; Handles.matrix = matrix; Handles.DrawLines(hoverOutlineGui);
                Handles.matrix = matrix * Matrix4x4.Translate(new Vector3(1, 0, 0)); Handles.DrawLines(hoverOutlineGui); // 2 px の太さ
            }
            finally { Handles.color = color; Handles.matrix = matrix; }
        }

        // ───────── オプションバー ─────────

        /// <summary>範囲の選び方のメニュー（オプションバーと「3D Pick」の欄で共有）。</summary>
        void OpenSurfacePickMenu(Rect at)
        {
            var menu = new GenericMenu();
            foreach (SurfaceRegionKind kind in Enum.GetValues(typeof(SurfaceRegionKind))) { var k = kind; menu.AddItem(new GUIContent(SurfacePickName(k)), k == surfacePick, () => { surfacePick = k; Repaint(); }); }
            menu.DropDown(at);
        }

        string PolygonFillPaintLabel => EditingMask ? L.Tr("White (show)") : L.TrIn("polygon fill", "Paint");
        string PolygonFillEraseLabel => EditingMask ? L.Tr("Black (hide)") : L.TrIn("polygon fill", "Erase");
        string PolygonFillHint => EditingMask ? L.Tr("Mask: white shows, black hides (X swaps). Click or drag; one undo when you let go.")
            : L.Tr("Click or drag over the model or its UVs; one undo when you let go.");
    }
}
