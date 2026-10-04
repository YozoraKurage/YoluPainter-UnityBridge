using System;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    internal sealed class AssetNameWindow : EditorWindow
    {
        string value, problem;
        Action<string> apply;
        public static void Open(string name, Action<string> action)
        {
            var w = CreateInstance<AssetNameWindow>(); w.value = name; w.apply = action;
            w.titleContent = new GUIContent(L.Tr("Rename asset")); w.minSize = w.maxSize = new Vector2(360, 112); w.ShowUtility();
        }
        void OnGUI()
        {
            PaintGui.Fill(new Rect(0, 0, position.width, position.height), PaintTheme.PanelBg);
            value = PaintGui.TextField(new Rect(12, 12, position.width - 24, 24), value);
            if (problem != null) PaintGui.Text(new Rect(12, 40, position.width - 24, 24), problem, PaintTheme.LabelSmall);
            if (PaintGui.FitButton(new Rect(12, 76, 160, 24), L.Tr("Cancel"))) { Close(); return; }
            if (PaintGui.FitButton(new Rect(184, 76, 164, 24), L.Tr("Rename"), true))
            {
                try { apply(value); Close(); } catch (Exception ex) { problem = L.Tr(ex.Message); Repaint(); }
            }
        }
    }
}
