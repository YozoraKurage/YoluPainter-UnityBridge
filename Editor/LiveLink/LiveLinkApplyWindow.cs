using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// 書き出したテクスチャをマテリアルに当てるかを確かめる窓: 変える組（マテリアル・プロパティ・前のテクスチャ → 新しいテクスチャ）を並べ、
    /// 「全部当てる」「選んだ物だけ当てる」「当てない」。当てられない組は理由を添えて並べる（選べない）。書き出しが続けて来たら、順に出す。
    /// 確かめる前のものはドメインのリロードで消える（取り込んだテクスチャは残る。マテリアルは変えていない）。
    /// </summary>
    internal sealed class LiveLinkApplyWindow : EditorWindow
    {
        static readonly Queue<LiveLinkImport.Prepared> waiting = new Queue<LiveLinkImport.Prepared>();
        LiveLinkImport.Prepared current;
        Vector2 scroll;

        public static void Show(LiveLinkImport.Prepared prepared)
        {
            waiting.Enqueue(prepared);
            var w = GetWindow<LiveLinkApplyWindow>(true, L.Tr("Apply to Materials"), true);
            if (w.current == null) w.Next();
            w.minSize = new Vector2(520, 180);
            w.Show();
        }

        /// <summary>試験: 待っているものを捨てる。</summary>
        internal static void ClearWaiting() => waiting.Clear();

        internal LiveLinkImport.Prepared Current => current;

        void Next()
        {
            current = waiting.Count > 0 ? waiting.Dequeue() : null;
            if (current == null) { Close(); return; }
            titleContent = new GUIContent(L.Tr("Apply to Materials"));
            Repaint();
        }

        /// <summary>選んだ答え（窓と試験から）。</summary>
        internal void Choose(bool apply, bool selectedOnly)
        {
            if (current != null && apply) LiveLinkImport.Apply(selectedOnly ? current.Rows.Where(r => r.Selected) : current.Rows);
            Next();
        }

        void OnGUI()
        {
            if (current == null) { Next(); GUIUtility.ExitGUI(); return; }
            if (!string.IsNullOrEmpty(current.TargetName)) EditorGUILayout.LabelField(current.TargetName, EditorStyles.boldLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var row in current.Rows) DrawRow(row);
            EditorGUILayout.EndScrollView();
            GUILayout.FlexibleSpace();
            bool any = current.Changes.Any();
            bool anySelected = current.Changes.Any(r => r.Selected);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!any))
                    if (GUILayout.Button(new GUIContent(L.Tr("Apply All"), L.Tr("Sets every new texture on its material (one undo step).")))) { Choose(true, false); GUIUtility.ExitGUI(); }
                using (new EditorGUI.DisabledScope(!anySelected))
                    if (GUILayout.Button(new GUIContent(L.Tr("Apply Selected"), L.Tr("Sets only the checked rows (one undo step).")))) { Choose(true, true); GUIUtility.ExitGUI(); }
                if (GUILayout.Button(new GUIContent(L.Tr("Don't Apply"), L.Tr("Keeps the materials as they are. The imported textures stay in the project.")))) { Choose(false, false); GUIUtility.ExitGUI(); }
            }
        }

        void DrawRow(LiveLinkImport.Row row)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool can = row.CanApply && row.Old != row.Texture;
                using (new EditorGUI.DisabledScope(!can))
                    row.Selected = EditorGUILayout.Toggle(can && row.Selected, GUILayout.Width(18)) && can;
                string material = row.Material != null ? row.Material.name : row.MaterialKey;
                GUILayout.Label(new GUIContent(material + " · " + row.Property, row.NewPath), EditorStyles.label, GUILayout.MinWidth(80), GUILayout.ExpandWidth(true));
                if (row.Problem != null)
                {
                    GUILayout.Label(new GUIContent(LiveLinkReason.Text(row.Problem), row.NewPath), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    return;
                }
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(row.Old, typeof(Texture), false, GUILayout.Width(150));
                    GUILayout.Label("→", GUILayout.Width(14));
                    EditorGUILayout.ObjectField(row.Texture, typeof(Texture), false, GUILayout.Width(150));
                }
            }
        }
    }
}
