using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>WindowTests の窓: オプションバーの小さな窓（PopupWindow）の中身を、普通の窓に描く（PopupWindow はフォーカスを失うと閉じるので、
    /// 試験は同じ中身をこの窓に描いて本物のマウスの入力で押す）。</summary>
    public sealed class OptionPopupProbe : EditorWindow
    {
        internal TexturePaintWindow Owner; internal OptionPopupKind Kind;
        void OnGUI()
        {
            if (Owner == null) return;
            Owner.DrawOptionPopup(Kind, new Rect(0, 0, position.width, position.height));
            if (GUI.changed) Owner.Repaint();
        }
    }
}
