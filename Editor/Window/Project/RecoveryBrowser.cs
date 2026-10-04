using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>閉じたウィンドウの checkpoint を、描き手が復旧・破棄する一覧。</summary>
    internal sealed class RecoveryBrowser : EditorWindow
    {
        TexturePaintWindow owner;
        List<RecoveryCatalog.Entry> entries;
        Vector2 scroll;
        string problem, storageNotice;
        internal static void Open(TexturePaintWindow owner)
        {
            var browser = GetWindow<RecoveryBrowser>(L.Tr("Recovery checkpoints")); browser.owner = owner;
            browser.minSize = new Vector2(580, 300); browser.Refresh(); browser.Show();
        }
        void Refresh()
        {
            try { entries = RecoveryCatalog.List(); storageNotice = RecoveryCatalog.StorageNotice(RecoveryCatalog.DirectoryBytes(RecoveryCatalog.BaseRoot)); problem = null; }
            catch (Exception ex) { problem = ex.Message; entries = new List<RecoveryCatalog.Entry>(); }
        }
        void OnGUI()
        {
            if (entries == null) Refresh();
            EditorGUILayout.LabelField(new GUIContent(L.Tr("Recovery checkpoints"), L.Tr("Closed windows with unsaved work are kept here. Recover opens the last checkpoint; save it as a .ylp. Discard removes that window's checkpoints and cannot be undone.")), EditorStyles.boldLabel);
            if (storageNotice != null) EditorGUILayout.HelpBox(storageNotice, MessageType.Warning);
            if (problem != null) EditorGUILayout.HelpBox(problem, MessageType.Error);
            if (GUILayout.Button(L.Tr("Refresh checkpoints"))) Refresh();
            if (entries.Count == 0) EditorGUILayout.LabelField(L.Tr("No closed checkpoints."));
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var entry in entries)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(entry.Title, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(entry.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "   " + (entry.Bytes / 1048576.0).ToString("0.##") + " MiB", EditorStyles.miniLabel);
                if (entry.Problem != null) EditorGUILayout.HelpBox(entry.Problem, MessageType.Warning);
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(owner == null || entry.Problem != null || owner.IsStroking))
                    if (GUILayout.Button(L.Tr("Recover checkpoint"))) { if (owner.OpenRecoveryAt(entry.Root)) { Close(); GUIUtility.ExitGUI(); } else problem = owner.StatusMessage; }
                if (GUILayout.Button(L.Tr("Discard checkpoint")))
                {
                    try { if (RecoveryCatalog.Discard(entry, owner != null ? owner.Dialogs : EditorPainterDialogs.Instance)) { Refresh(); GUIUtility.ExitGUI(); } }
                    catch (ExitGUIException) { throw; }
                    catch (Exception ex) { problem = ex.Message; }
                }
                EditorGUILayout.EndHorizontal(); EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
