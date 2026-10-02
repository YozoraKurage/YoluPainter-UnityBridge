using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>手ぶれ補正（引っ張る糸）と入り抜き の欄。2D キャンバスのストロークに効く（3D ビューのブラシは面の上のダブで描くので効かない）。</summary>
    public sealed partial class TexturePaintWindow
    {
        void DrawStrokeAssist()
        {
            brush.stabilizer=EditorGUILayout.Slider(new GUIContent("Stabilizer (px)","The brush trails the pointer by this string length, smoothing shaky lines (Krita's / Lazy Nezumi's pulled string). The line is finished to the pointer when you lift. 2D canvas only."),brush.stabilizer,0,100);
            brush.taperIn=EditorGUILayout.Slider(new GUIContent("Taper in (px)","The brush grows from nothing over this much stroke length (入り)."),brush.taperIn,0,500);
            brush.taperOut=EditorGUILayout.Slider(new GUIContent("Taper out (px)","The brush shrinks to nothing over the last this much stroke length (抜き). That part appears when the stroke ends."),brush.taperOut,0,500);
        }
        internal float Stabilizer { get => brush.stabilizer; set => brush.stabilizer = value; }
    }
}
