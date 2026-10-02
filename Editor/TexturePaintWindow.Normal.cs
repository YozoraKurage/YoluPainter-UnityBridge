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
                message = next.DeriveFromHeight ? "Height → Normal on: the Normal output now has the normal derived from Height under the painted Normal layers (nothing is painted into a layer)." : "Height → Normal off: the Normal output is the painted Normal layers only.";
            else if (next.FileDirection != old.FileDirection)
                message = "Normal files (Export Images / PNG / PSD) are now written in " + DirectionName(next.FileDirection) + " order. The .ylp texture and the preview stay OpenGL (Unity).";
        }
        static string DirectionName(NormalYDirection d) => d == NormalYDirection.DirectX ? "DirectX (Y−)" : "OpenGL (Y+)";

        void DrawNormalPanel()
        {
            if (channel != PaintChannel.Normal && channel != PaintChannel.Height) return;
            using (new EditorGUI.DisabledScope(stroke != null))
            {
                GUILayout.Space(6);
                GUILayout.Label("Normal output", EditorStyles.boldLabel);
                var s = document.NormalSettings;
                bool derive = EditorGUILayout.Toggle(new GUIContent("Height → Normal", "Add the normal derived from the Height channel under the painted Normal layers in the Normal output (preview, .ylp texture, exports). It is regenerated from Height, never painted into a layer."), s.DeriveFromHeight);
                double strength = s.Strength; var edges = s.Edges;
                using (new EditorGUI.DisabledScope(!derive))
                {
                    // スライダーは float。触っていなければ文書の値（double）のままにする（読み込んだ値を勝手に丸めて Undo を作らない）
                    float shown = (float)s.Strength, picked = EditorGUILayout.Slider(new GUIContent("Strength", "Texels of rise for the full height range (0 → 1). Negative turns bumps into dents."), shown, -(float)NormalSettings.MaxStrength, (float)NormalSettings.MaxStrength);
                    if (picked != shown) strength = picked;
                    edges = (HeightEdgeMode)EditorGUILayout.EnumPopup(new GUIContent("Edges", "Clamp: the slope at the canvas edge uses the edge texel. Wrap: it reads the opposite edge (tiling textures)."), s.Edges);
                }
                var direction = (NormalYDirection)EditorGUILayout.EnumPopup(new GUIContent("File Y", "Green direction of Normal images written by Export Images / PNG / PSD. Unity uses OpenGL (Y+); the .ylp texture and the preview always do."), s.FileDirection);
                if (derive != s.DeriveFromHeight || strength != s.Strength || edges != s.Edges || direction != s.FileDirection)
                {
                    bool drag = strength != s.Strength && derive == s.DeriveFromHeight && edges == s.Edges && direction == s.FileDirection;
                    TryAction(() => ApplyNormalSettings(new NormalSettings(derive, strength, edges, direction), coalesce: drag));
                }
                if (channel == PaintChannel.Height)
                {
                    if (derive) EditorGUILayout.HelpBox("Height also shapes the Normal output. Switch to the Normal channel to see it.", MessageType.None);
                    return;
                }
                bool show = EditorGUILayout.Toggle(new GUIContent("Show output", "On: the Normal output (renormalized, flat where unpainted, Height → Normal included). Off: the painted Normal layers with transparency."), showNormalOutput);
                if (show != showNormalOutput) ShowNormalOutput = show;
                DrawNormalBrushValue();
                var layer = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
                if (layer != null && !layer.IsGroup && layer.Kind != LayerKind.Adjustment && !NormalMaps.IsVectorMode(layer.BlendMode))
                    EditorGUILayout.HelpBox(layer.BlendMode + " has no meaning for normals: in the Normal channel this layer replaces what is below, like Normal. Use Overlay to add it as detail (reoriented normal mapping).", MessageType.Info);
                else if (layer != null && layer.Kind == LayerKind.Adjustment)
                    EditorGUILayout.HelpBox("Adjustments change the encoded values of the Normal channel (not the vectors); the output renormalizes them.", MessageType.None);
            }
        }

        /// <summary>ブラシの値を法線の傾きで選ぶ（X: 右、Y: 上、Z は単位長さになるように決める）。</summary>
        void DrawNormalBrushValue()
        {
            float x = brush.color.r * 2 - 1, y = brush.color.g * 2 - 1;
            float nx = EditorGUILayout.Slider(new GUIContent("Brush tilt X", "The brush value as a normal: +1 leans right"), x, -1, 1);
            float ny = EditorGUILayout.Slider(new GUIContent("Brush tilt Y", "+1 leans up (OpenGL / Unity)"), y, -1, 1);
            if (nx != x || ny != y) SetBrushNormal(nx, ny);
            if (GUILayout.Button(new GUIContent("Flat brush value", "(128, 128, 255): paints a flat normal"))) SetBrushNormal(0, 0);
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
