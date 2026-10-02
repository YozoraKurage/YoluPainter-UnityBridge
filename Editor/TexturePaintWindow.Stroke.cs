using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>手ぶれ補正（引っ張る糸）と入り抜き の欄。2D キャンバスのストロークに効く（3D ビューのブラシは面の上のダブで描くので効かない）。</summary>
    public sealed partial class TexturePaintWindow
    {
        void StrokeAssistSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-stroke", L.Tr("Stabilizer & Taper"), "ink_stroke")) return;
            brush.stabilizer = PaintGui.FitSlider(rows.Row(), L.Tr("Stabilizer"), brush.stabilizer, 0, 100, "0", " px",
                L.Tr("The brush trails the pointer by this string length, smoothing shaky lines (Krita's / Lazy Nezumi's pulled string). The line is finished to the pointer when you lift. 2D canvas only."));
            var c = UiRows.Split(rows.Row(), 2, 6);
            brush.taperIn = PaintGui.FitSlider(c[0], L.Tr("Taper in"), brush.taperIn, 0, 500, "0", " px", L.Tr("The brush grows from nothing over this much stroke length."));
            brush.taperOut = PaintGui.FitSlider(c[1], L.Tr("Taper out"), brush.taperOut, 0, 500, "0", " px", L.Tr("The brush shrinks to nothing over the last this much stroke length. That part appears when the stroke ends."));
            rows.Space(4);
        }

        internal float Stabilizer { get => brush.stabilizer; set => brush.stabilizer = value; }
    }
}
