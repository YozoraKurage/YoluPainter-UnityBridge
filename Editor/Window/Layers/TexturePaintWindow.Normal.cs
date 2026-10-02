using System;
using System.Linq;
using Yozolab.YoluPainter.Core;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>Normal の出力: 文書の設定（Height → Normal・強さ・端・ファイルの Y の向き）の欄、Normal チャンネルの表示（出力 / レイヤーの
    /// 合成）、法線を傾きで選ぶブラシの値、書き出しの注意。設定の変更は文書の Undo に 1 回ずつ入る（スライダーのドラッグは 1 回にまとめる）。</summary>
    public sealed partial class TexturePaintWindow
    {
        NormalOutputView normalOutput;
        bool showNormalOutput = true;

        /// <summary>Normal チャンネルで、レイヤーの合成ではなく出力（平らな法線の上・不透明・Height → Normal 込み）を表示するか。</summary>
        internal bool ShowNormalOutput { get => showNormalOutput; set { showNormalOutput = value; repaintPixels = true; } }
        internal bool ShowsNormalOutput => channel == PaintChannel.Normal && showNormalOutput;
        internal NormalOutputView NormalOutput => normalOutput;
        /// <summary>2D キャンバスと 3D プレビューに出すテクスチャ。</summary>
        internal Texture DisplayTexture => ShowsNormalOutput && normalOutput != null && normalOutput.Texture != null ? normalOutput.Texture : compositor.Texture;

        /// <summary>合成の直後に呼ぶ。出力を表示しないあいだは、出力と Height の合成器の GPU のメモリを放す。</summary>
        void UpdateNormalOutput()
        {
            if (!ShowsNormalOutput) { DisposeNormalOutput(); return; }
            if (normalOutput == null) normalOutput = new NormalOutputView();
            normalOutput.Update(document, compositor.Texture);
        }
        void DisposeNormalOutput() { normalOutput?.Dispose(); normalOutput = null; }

        /// <summary>文書の Normal の出力設定を変える（1 回の Undo。coalesce ならスライダーのドラッグとしてまとめる）。</summary>
        internal void ApplyNormalSettings(NormalSettings next, bool coalesce = false)
        {
            var old = document.NormalSettings;
            if (next == null || next.Equals(old)) return;
            document.SetNormalSettings(next, coalesce);
            repaintPixels = true;
            if (next.DeriveFromHeight != old.DeriveFromHeight)
                message = next.DeriveFromHeight ? L.Tr("Height → Normal on: the Normal output now has the normal derived from Height under the painted Normal layers (nothing is painted into a layer).") : L.Tr("Height → Normal off: the Normal output is the painted Normal layers only.");
            else if (next.FileDirection != old.FileDirection)
                message = L.Tr("Normal files (Export Images / PNG / PSD) are now written in {0} order. The .ylp texture and the preview stay OpenGL (Unity).", DirectionName(next.FileDirection));
        }
        static string DirectionName(NormalYDirection d) => d == NormalYDirection.DirectX ? "DirectX (Y−)" : "OpenGL (Y+)";
        static string EdgeName(HeightEdgeMode m) => m == HeightEdgeMode.Wrap ? L.Tr("Wrap (tiling)") : L.TrIn("normal output", "Clamp");

        void DrawNormalPanel(UiRows rows)
        {
            bool was = GUI.enabled; GUI.enabled = was && stroke == null;
            try
            {
                var s = document.NormalSettings;
                bool derive = PaintGui.FitToggle(Spot("normal.derive", rows.Row()), L.Tr("Height → Normal"), s.DeriveFromHeight,
                    L.Tr("Add the normal derived from the Height channel under the painted Normal layers in the Normal output (preview, .ylp texture, exports). It is regenerated from Height, never painted into a layer."));
                // スライダーは float。触っていなければ文書の値（double）のままにする（読み込んだ値を勝手に丸めて Undo を作らない）
                double strength = PaintGui.KeepSlider(Spot("normal.strength", rows.Row()), L.TrIn("normal output", "Strength"), s.Strength, -NormalSettings.MaxStrength, NormalSettings.MaxStrength, "0.##", "",
                    L.Tr("Texels of rise for the full height range (0 → 1). Negative turns bumps into dents."), derive);
                if (derive != s.DeriveFromHeight) TryAction(() => ApplyNormalSettings(s.WithDerive(derive)));
                else if (strength != s.Strength) TryAction(() => ApplyNormalSettings(s.WithStrength(strength), coalesce: true));
                ChoiceDropdown(rows.Row(), L.TrIn("normal output", "Edges"), s.Edges, (HeightEdgeMode[])Enum.GetValues(typeof(HeightEdgeMode)), EdgeName,
                    v => TryAction(() => ApplyNormalSettings(document.NormalSettings.WithEdges(v))),
                    L.Tr("Clamp: the slope at the canvas edge uses the edge texel. Wrap: it reads the opposite edge (tiling textures)."), s.DeriveFromHeight);
                ChoiceDropdown(rows.Row(), L.Tr("File Y"), s.FileDirection, (NormalYDirection[])Enum.GetValues(typeof(NormalYDirection)), DirectionName,
                    v => TryAction(() => ApplyNormalSettings(document.NormalSettings.WithFileDirection(v))),
                    L.Tr("Green direction of Normal images written by Export Images / PNG / PSD. Unity uses OpenGL (Y+); the .ylp texture and the preview always do."));
                if (channel == PaintChannel.Height)
                {
                    if (s.DeriveFromHeight) NoteRow(rows, L.Tr("Height also shapes the Normal output. Switch to the Normal channel to see it."), NoteKind.Info);
                    return;
                }
                bool show = PaintGui.FitToggle(rows.Row(), L.Tr("Show output"), showNormalOutput,
                    L.Tr("On: the Normal output (renormalized, flat where unpainted, Height → Normal included). Off: the painted Normal layers with transparency."));
                if (show != showNormalOutput) ShowNormalOutput = show;
                DrawNormalBrushValue(rows);
                var layer = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
                if (layer != null && !layer.IsGroup && layer.Kind != LayerKind.Adjustment && !NormalMaps.IsVectorMode(layer.BlendMode))
                    NoteRow(rows, L.Tr("{0} has no meaning for normals: in the Normal channel this layer replaces what is below, like Normal. Use Overlay to add it as detail (reoriented normal mapping).", L.TrIn("blend mode", BlendName(layer.BlendMode))), NoteKind.Info);
                else if (layer != null && layer.Kind == LayerKind.Adjustment)
                    NoteRow(rows, L.Tr("Adjustments change the encoded values of the Normal channel (not the vectors); the output renormalizes them."));
            }
            finally { GUI.enabled = was; }
        }

        /// <summary>ブラシの値を法線の傾きで選ぶ（X: 右、Y: 上、Z は単位長さになるように決める）。</summary>
        void DrawNormalBrushValue(UiRows rows)
        {
            PaintGui.GroupLabel(rows.Row(16), L.Tr("Brush value (normal)"));
            var row = rows.Row();
            var c = UiRows.Split(new Rect(row.x, row.y, row.width - 28, row.height), 2, 6);
            double x = brush.color.r * 2 - 1, y = brush.color.g * 2 - 1;
            double nx = PaintGui.KeepSlider(Spot("normal.tiltX", c[0]), L.TrIn("normal brush", "Tilt X"), x, -1, 1, "0.00", "", L.Tr("The brush value as a normal: +1 leans right"));
            double ny = PaintGui.KeepSlider(c[1], L.TrIn("normal brush", "Tilt Y"), y, -1, 1, "0.00", "", L.Tr("+1 leans up (OpenGL / Unity)"));
            if (nx != x || ny != y) SetBrushNormal((float)nx, (float)ny);
            if (PaintGui.IconButton(Spot("normal.flat", new Rect(row.xMax - 24, row.y, 24, row.height)), "restart_alt", L.Tr("Flat brush value (128, 128, 255): paints a flat normal"), false, GUI.enabled, 16)) SetBrushNormal(0, 0);
        }
        /// <summary>ブラシの値を (x, y, √(1 − x² − y²)) の法線にする（長さが 1 を超える傾きは z = 0 の向きに縮める）。</summary>
        internal void SetBrushNormal(float x, float y)
        {
            float l2 = x * x + y * y; if (l2 > 1) { float l = Mathf.Sqrt(l2); x /= l; y /= l; l2 = 1; }
            float z = Mathf.Sqrt(1 - l2);
            brush.color = new Color(x * .5f + .5f, y * .5f + .5f, z * .5f + .5f, brush.color.a);
        }

        /// <summary>Normal を書き出したときに状態の欄へ添える注意（何が書かれたか）。Normal を含まなければ空。</summary>
        string NormalExportNote(bool includesNormal, bool psd = false)
        {
            if (!includesNormal) return "";
            var s = document.NormalSettings;
            string note = " Normal is the evaluated output (unit normals, flat where unpainted, opaque" + (s.DeriveFromHeight ? ", Height → Normal included" : "") + ") in "
                + DirectionName(s.FileDirection) + " order" + (s.FileDirection == NormalYDirection.DirectX ? "; Unity expects OpenGL (Y+)." : ".");
            if (psd) note += " Its layers are the painted normals; Photoshop does not reproduce YoluPainter's normal composite when it recomposites them.";
            return note;
        }
    }
}
