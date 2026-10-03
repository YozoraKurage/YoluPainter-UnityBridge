using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>表示の合成器（2D の表示と 3D のプレビュー）を個人の設定「Display compositing」から作る。設定が変わったら作り直す。
    /// ストロークの最中は、そのストロークが終わってから Tick が作り直す（描いている途中で表示の経路を替えない）。照明用の Normal の
    /// 合成器と、Normal の出力の Height の合成器も同じ設定に従う（作り直すときに捨て、次の表示の更新で作られる）。
    /// 描くたびの合成は、個人の設定「Display compositing time per frame」の時間で区切り、残りは次の描画に回す（窓を止めない）。
    /// 先に合成するのはストロークの所、2D で見えている所、3D ビューの UV の所の順（<see cref="CompositeSchedule"/>）。</summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>今の合成器を作ったときの「表示の合成」。</summary>
        CompositorBackend compositorBackend;
        bool compositorRebuildPending;
        /// <summary>設定が変わり、ストロークが終わるのを待って合成器を作り直すところか（テスト用）。</summary>
        internal bool CompositorRebuildPending => compositorRebuildPending;

        TileGpuCompositor CreateCompositor()
        {
            compositorBackend = PainterSettings.DisplayCompositing;
            return new TileGpuCompositor(compositorBackend) { ResidentBudgetBytes = PainterSettings.GpuCacheBytes };
        }
        /// <summary>ほかの表示用の合成器（照明用の Normal など）。今の合成器と同じ「表示の合成」で作る。</summary>
        internal TileGpuCompositor NewDisplayCompositor(long residentBudgetBytes) => new TileGpuCompositor(compositorBackend) { ResidentBudgetBytes = residentBudgetBytes };
        internal NormalOutputView NewNormalOutputView() => new NormalOutputView(heightBackend: compositorBackend);

        /// <summary>設定が変わったとき: 「表示の合成」が今の合成器と違えば作り直す。ストロークの最中なら印だけ付けて待つ。</summary>
        void ApplyCompositorSettings()
        {
            if (compositor == null) return;
            compositorRebuildPending = PainterSettings.DisplayCompositing != compositorBackend;
            RebuildCompositorIfPending();
        }
        /// <summary>待っている作り直しを、ストロークの外ならする（設定の変更と Tick から）。</summary>
        void RebuildCompositorIfPending()
        {
            if (!compositorRebuildPending || stroke != null || compositor == null) return;
            compositorRebuildPending = false;
            DisposeNormalOutput(); DisposeLighting();
            compositor.Dispose();
            compositor = CreateCompositor();
            repaintPixels = true; Repaint();
        }

        // ───────── 描くたびの合成（時間で区切る） ─────────

        /// <summary>この描画で表示の合成が要るか（文書が変わった・表示を作り直す印・合成器に残りの仕事がある）。</summary>
        bool DisplayNeedsCompositing => repaintPixels || renderedRevision != document.Revision || compositor != null && compositor.HasPendingWork;

        /// <summary>残りの表示の合成を今すべて終える（窓を 1 回だけ描いて残すとき: OffscreenGui）。</summary>
        internal void FinishDisplayCompositing() { if (document != null && compositor != null && DisplayNeedsCompositing) RefreshPreviewTextures(); }

        /// <summary>Repaint のたびに: 時間を区切って合成し、残りがあれば次の描画を頼む（残りは見えている所から少しずつ）。</summary>
        void RefreshDisplayForFrame()
        {
            RefreshPreviewTextures(DisplaySchedule());
            if (compositor != null && compositor.HasPendingWork) Repaint();
        }

        /// <summary>今の描画の合成の指示: 個人の設定の時間（0 = 分けない）、ストロークの最中は描いた所を急ぎに、2D で見えている文書の範囲、
        /// 3D ビューに今のセットのスロットの UV が掛かるタイル。</summary>
        internal CompositeSchedule DisplaySchedule()
        {
            int ms = PainterSettings.DisplayFrameBudgetMs;
            var schedule = new CompositeSchedule { BudgetMilliseconds = ms <= 0 ? double.PositiveInfinity : ms, Urgent = stroke != null };
            if (canvasRect.width > 0 && canvasRect.height > 0) schedule.Visible = VisibleDocumentRect();
            if (surfaceRect.width > 0 && surfaceRect.height > 0 && preview != null && preview.HasModel) schedule.ModelTiles = ModelTiles();
            schedule.PreviewStep = PreviewStepNow();
            return schedule;
        }

        /// <summary>間引いた合成（<see cref="CompositeSchedule.PreviewStep"/>）の幅: スライダーなどのコントロールをドラッグしている最中
        /// （ストローク・ツール・パス・表示の回転のドラッグは除く）で、見えている表示のどれもが画面の 1 画素に文書の画素を 4 つ以上
        /// 映しているとき 4（1/4）。2D は拡大率から、3D はモデルの画面の上の大きさに対するテクスチャの大きさから（UV が詰まっていれば
        /// テクスチャはもっと小さく映るので、控えめな見積もり）。それ以外は 1（使わない）。1/2 は使わない: 4096² で 1 回 37–52 ms と
        /// 予算に収まらず、2048² では全解像度が 3 回ほどで揃うので割に合わなかった（VALIDATION）。</summary>
        internal int PreviewStepNow()
        {
            if (GUIUtility.hotControl == 0 || stroke != null || toolDragging || pathDrag >= 0 || canvasRotating || document == null) return 1;
            float ppp = EditorGUIUtility.pixelsPerPoint; int step = MaxPreviewStep;
            if (canvasRect.width > 0 && canvasRect.height > 0)
            {
                float onScreen = CanvasViewNow().PixelSize * ppp; // 文書の 1 画素の画面の画素
                step = Math.Min(step, onScreen <= 0 ? 1 : StepFor(1 / onScreen));
            }
            if (surfaceRect.width > 0 && surfaceRect.height > 0 && preview != null && preview.HasModel)
            {
                float diameter = 2 * preview.WorldRadiusToGuiPoints(preview.Bounds.center, preview.ModelRadius) * ppp;
                step = Math.Min(step, diameter <= 0 ? 1 : StepFor(Mathf.Max(document.Width, document.Height) / diameter));
            }
            return step;
        }
        /// <summary>間引きの幅（1/4。それより粗いと、ドラッグの最中でも絵が読めない）。</summary>
        internal const int MaxPreviewStep = 4;
        static int StepFor(float documentPixelsPerScreenPixel) => documentPixelsPerScreenPixel >= MaxPreviewStep ? MaxPreviewStep : 1;

        /// <summary>間引いた合成を見せているあいだ、2D の表示域の左上に小さな印を出す（全解像度が揃うまで。DrawCanvas から）。</summary>
        void DrawCompositingBadge()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint || compositor == null) return;
            int step = compositor.PreviewStepShown;
            if (step <= 1) return;
            var area = canvasRect;
            if (area.width <= 0) return;
            string text = L.Tr("Reduced preview") + " 1/" + step;
            var style = badgeStyle ?? (badgeStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.white } });
            var size = style.CalcSize(new GUIContent(text));
            var badge = new Rect(area.x + 6, area.y + 6, size.x + 8, size.y + 2);
            EditorGUI.DrawRect(badge, new Color(0, 0, 0, .6f));
            GUI.Label(new Rect(badge.x + 4, badge.y + 1, size.x, size.y), text, style);
        }
        static GUIStyle badgeStyle;

        /// <summary>2D の表示域に見えている文書の範囲（画素、左下原点。回転していればそれを囲む矩形）。見えていなければ null。</summary>
        internal RectInt? VisibleDocumentRect()
        {
            if (document == null || canvasRect.width <= 0 || canvasRect.height <= 0) return null;
            var view = CanvasViewNow();
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var corner in new[] { new Vector2(canvasRect.xMin, canvasRect.yMin), new Vector2(canvasRect.xMax, canvasRect.yMin), new Vector2(canvasRect.xMin, canvasRect.yMax), new Vector2(canvasRect.xMax, canvasRect.yMax) })
            {
                var p = view.ToCanvas(corner);
                x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y); x1 = Mathf.Max(x1, p.x); y1 = Mathf.Max(y1, p.y);
            }
            int ix0 = Mathf.Clamp(Mathf.FloorToInt(x0), 0, document.Width), iy0 = Mathf.Clamp(Mathf.FloorToInt(y0), 0, document.Height);
            int ix1 = Mathf.Clamp(Mathf.CeilToInt(x1), 0, document.Width), iy1 = Mathf.Clamp(Mathf.CeilToInt(y1), 0, document.Height);
            if (ix1 <= ix0 || iy1 <= iy0) return null;
            return new RectInt(ix0, iy0, ix1 - ix0, iy1 - iy0);
        }

        HashSet<TileCoord> modelTiles; (int revision, int slot, int width, int height, int tile) modelTilesKey;
        /// <summary>今のセットのスロットの三角形の UV（0〜1 に切り詰めた外接矩形）が掛かるタイル。モデル・スロット・文書の大きさが同じあいだは
        /// 作り直さない。</summary>
        internal ISet<TileCoord> ModelTiles()
        {
            var geometry = preview?.Geometry;
            if (geometry == null || document == null) return null;
            var key = (preview.SnapshotRevision, materialSlot, document.Width, document.Height, document.TileSize);
            if (modelTiles != null && modelTilesKey.Equals(key)) return modelTiles;
            int tile = document.TileSize, maxX = (document.Width + tile - 1) / tile - 1, maxY = (document.Height + tile - 1) / tile - 1;
            long all = (long)(maxX + 1) * (maxY + 1);
            var tiles = new HashSet<TileCoord>();
            int Column(float u) => Mathf.Clamp((int)(Mathf.Clamp01(u) * document.Width) / tile, 0, maxX);
            int Row(float v) => Mathf.Clamp((int)(Mathf.Clamp01(v) * document.Height) / tile, 0, maxY);
            foreach (var t in geometry.Triangles)
            {
                if (t.MaterialSlot != materialSlot) continue;
                int tx0 = Column(Mathf.Min(t.UvA.x, Mathf.Min(t.UvB.x, t.UvC.x))), tx1 = Column(Mathf.Max(t.UvA.x, Mathf.Max(t.UvB.x, t.UvC.x)));
                int ty0 = Row(Mathf.Min(t.UvA.y, Mathf.Min(t.UvB.y, t.UvC.y))), ty1 = Row(Mathf.Max(t.UvA.y, Mathf.Max(t.UvB.y, t.UvC.y)));
                for (int ty = ty0; ty <= ty1; ty++) for (int tx = tx0; tx <= tx1; tx++) tiles.Add(new TileCoord(tx, ty));
                if (tiles.Count >= all) break; // 全部のタイル
            }
            modelTiles = tiles; modelTilesKey = key;
            return tiles;
        }
    }
}
