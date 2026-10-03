using System;
using System.Collections.Generic;
using UnityEditor;
using Yozolab.YoluPainter.Core;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>オプションバーの小さなポップアップの種類。</summary>
    internal enum OptionPopupKind { Symmetry, Stabilizer }

    /// <summary>
    /// オプションバーの右端の、描くツールの対称と手ぶれ補正（Substance Painter のビューの上のツールバーの「対称」「レイジーマウス」）: 切り替えの
    /// ボタンと、細かい設定を出す ▾ の小さなポップアップ（中身はプロパティの欄にあった SymmetrySection・StrokeAssistSection のまま）。
    /// ポップアップは Unity の PopupWindow（3D の表示の設定と同じ）で、試験はその中身（<see cref="DrawOptionPopup"/>）を直接描く。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>手ぶれ補正を切ってから入れ直すときの長さ（px。最後に使った長さ）。</summary>
        [SerializeField] float stabilizerWhenOn = 20;
        readonly Dictionary<OptionPopupKind, float> optionPopupHeights = new Dictionary<OptionPopupKind, float> { { OptionPopupKind.Symmetry, 560 }, { OptionPopupKind.Stabilizer, 110 } };
        internal const float OptionPopupWidth = 300;
        /// <summary>テスト用: ポップアップを開く代わりに呼ぶ（null なら PopupWindow を開く）。</summary>
        internal Action<OptionPopupKind, Rect> OpenOptionPopupOverride;

        /// <summary>オプションバーの右端に、対称と手ぶれ補正の切り替えと ▾ を描く。左の部品が使える右の端（x）を返す。</summary>
        float DrawStrokeAssistOptions(Rect bar)
        {
            float h = bar.height - 12, y = bar.y + 6, x = bar.xMax - 8;
            Rect Left(float width) { x -= width; var at = new Rect(x, y, width, h); x -= 4; return at; }
            // 手ぶれ補正（右から: ▾・長さ・切り替え）
            var more = Left(18);
            float slider = Mathf.Clamp(bar.width * .1f, 90, 130);
            var length = Left(slider);
            var toggle = Left(28);
            bool on = brush.stabilizer > 0;
            if (PaintGui.IconButton(Mark("options.stabilizer", toggle), "ink_stroke", L.Tr("Stabilizer (lazy mouse): the brush trails the pointer and smooths shaky lines. 2D canvas only."), on, stroke == null, 18))
                SetStabilizerOn(!on);
            // 名前は収まるときだけ（狭いオプションバーでは左の切り替えのアイコンが名前の代わり。詰めた名前は出さない）
            string label = L.Tr("Stabilizer"), tip = L.Tr("The string length the brush trails the pointer by (0 is off)");
            bool named = PaintGui.TextWidth(label, PaintTheme.Label) + PaintGui.TextWidth("100 px", PaintTheme.Value) + 22 <= length.width;
            float next = PaintGui.Slider(Mark("options.stabilizer.length", length), named ? label : null, brush.stabilizer, 0, 100, "0", " px", named ? tip : label + "\n" + tip, stroke == null);
            if (next != brush.stabilizer) { brush.stabilizer = next; if (next > 0) stabilizerWhenOn = next; }
            if (PaintGui.IconButton(Mark("options.stabilizer.more", more), "arrow_drop_down", L.Tr("Stabilizer and taper settings"), false, stroke == null, 16)) OpenOptionPopup(OptionPopupKind.Stabilizer, more);
            x -= 6;
            PaintGui.VLine(x, bar.y + 8, bar.yMax - 8, PaintTheme.Separator);
            x -= 6;
            // 対称（右から: ▾・切り替え）
            more = Left(18);
            toggle = Left(28);
            if (PaintGui.IconButton(Mark("options.symmetry", toggle), "flip", L.Tr("Symmetry: mirror 3D brush strokes across the model's plane") + "\n" + L.Tr("Turns off every symmetry that is on (3D mirror, 3D radial, 2D canvas), and turns the same ones on again."),
                    AnySymmetry, stroke == null, 18)) ToggleAllSymmetry();
            if (PaintGui.IconButton(Mark("options.symmetry.more", more), "arrow_drop_down", L.Tr("Symmetry settings (axis, center, plane)"), false, stroke == null, 16)) OpenOptionPopup(OptionPopupKind.Symmetry, more);
            x -= 6;
            PaintGui.VLine(x, bar.y + 8, bar.yMax - 8, PaintTheme.Separator);
            return x - 4;
        }

        /// <summary>どれかの対称（3D の鏡映・3D の放射状・2D キャンバス）が入っているか。</summary>
        internal bool AnySymmetry => symmetry || brush.radialSymmetry3D || brush.canvasSymmetry != CanvasSymmetryMode.None;
        /// <summary>オプションバーで切った対称（入れ直すときに戻す。何も覚えていなければ 3D の鏡映）。</summary>
        [SerializeField] bool symmetryOffMirror = true, symmetryOffRadial; [SerializeField] CanvasSymmetryMode symmetryOffCanvas;

        /// <summary>オプションバーの対称の切り替え（Substance の対称のボタン）: 入っている対称を全部切り、次に押したら同じものを入れ直す。</summary>
        internal void ToggleAllSymmetry()
        {
            if (AnySymmetry)
            {
                symmetryOffMirror = symmetry; symmetryOffRadial = brush.radialSymmetry3D; symmetryOffCanvas = brush.canvasSymmetry;
                symmetry = false; brush.radialSymmetry3D = false; brush.canvasSymmetry = CanvasSymmetryMode.None;
            }
            else if (!symmetryOffMirror && !symmetryOffRadial && symmetryOffCanvas == CanvasSymmetryMode.None) symmetry = true;
            else { symmetry = symmetryOffMirror; brush.radialSymmetry3D = symmetryOffRadial; brush.canvasSymmetry = symmetryOffCanvas; }
            Repaint();
        }

        /// <summary>手ぶれ補正を入れる（最後に使った長さで）・切る（0 に）。</summary>
        internal void SetStabilizerOn(bool on)
        {
            if (on) brush.stabilizer = stabilizerWhenOn > 0 ? stabilizerWhenOn : 20;
            else { if (brush.stabilizer > 0) stabilizerWhenOn = brush.stabilizer; brush.stabilizer = 0; }
            Repaint();
        }

        void OpenOptionPopup(OptionPopupKind kind, Rect at)
        {
            if (OpenOptionPopupOverride != null) { OpenOptionPopupOverride(kind, at); return; }
            if (LayoutOverride.HasValue) return; // オフスクリーンの描画は窓が無い
            PopupWindow.Show(at, new PainterOptionPopup(this, kind));
        }

        internal Vector2 OptionPopupSize(OptionPopupKind kind) => new Vector2(OptionPopupWidth, Mathf.Clamp(optionPopupHeights[kind], 60, 900));

        /// <summary>ポップアップの中身（題と、プロパティの欄にあった欄の中身。中身の見出しは描かない）。描いた高さを覚えて窓の大きさに使う。</summary>
        internal void DrawOptionPopup(OptionPopupKind kind, Rect r)
        {
            var marks = BeginMarks("popup." + kind);
            try { DrawOptionPopupInner(kind, r); }
            finally { EndMarks(marks); }
        }

        void DrawOptionPopupInner(OptionPopupKind kind, Rect r)
        {
            PaintGui.Fill(r, PaintTheme.PanelBg);
            PaintGui.Outline(r, PaintTheme.Border, 1, 0);
            var rows = new UiRows(r, 8);
            bool symmetryKind = kind == OptionPopupKind.Symmetry;
            var title = rows.Row(18, 6);
            PaintGui.Icon(new Rect(title.x, title.y, 16, title.height), symmetryKind ? "flip" : "ink_stroke", PaintTheme.TextDim, 15);
            PaintGui.Text(new Rect(title.x + 22, title.y, title.width - 22, title.height), symmetryKind ? L.Tr("Symmetry") : L.Tr("Stabilizer & Taper"), PaintTheme.Header);
            var previous = drawingRoute; bool previousDrawn = drawingRouteHeaderDrawn;
            drawingRoute = new PropertyRoute { Key = symmetryKind ? "brush-symmetry" : "brush-stroke", Contexts = new PropertyContext[0] };
            drawingRouteHeaderDrawn = true; // 中身の関数の見出しは、この題で置き換える（ポップアップは畳まない）
            bool was = GUI.enabled; GUI.enabled = was && stroke == null;
            try { if (symmetryKind) SymmetrySection(rows); else StrokeAssistSection(rows); }
            finally { drawingRoute = previous; drawingRouteHeaderDrawn = previousDrawn; GUI.enabled = was; rows.Indent = 0; }
            if (Event.current.type == EventType.Repaint) optionPopupHeights[kind] = rows.Used + 6;
        }
    }

    /// <summary>オプションバーの ▾ から開く小さな窓（中身は持ち主の <see cref="TexturePaintWindow.DrawOptionPopup"/>）。</summary>
    internal sealed class PainterOptionPopup : PopupWindowContent
    {
        readonly TexturePaintWindow owner; readonly OptionPopupKind kind;
        public PainterOptionPopup(TexturePaintWindow owner, OptionPopupKind kind) { this.owner = owner; this.kind = kind; }
        public override Vector2 GetWindowSize() => owner != null ? owner.OptionPopupSize(kind) : new Vector2(TexturePaintWindow.OptionPopupWidth, 100);
        public override void OnGUI(Rect rect)
        {
            if (owner == null) { editorWindow.Close(); return; }
            if (Event.current.type == EventType.MouseMove) editorWindow.Repaint();
            owner.DrawOptionPopup(kind, rect);
            if (GUI.changed) owner.Repaint();
        }
        public override void OnOpen() { editorWindow.wantsMouseMove = true; }
    }
}
