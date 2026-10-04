using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>塗ったテクスチャを入れる前のプレビュー複製と、そのシェーダーインスペクターの所有者。</summary>
    internal sealed class PreviewMaterialInspector : IDisposable
    {
        Material source;
        int sourceDirty = -1, editsVersion = -1;
        Shader shader;
        int copyDirty;
        static readonly FieldInfo ShaderGUIField = typeof(MaterialEditor).GetField("m_CustomShaderGUI", BindingFlags.Instance | BindingFlags.NonPublic);
        public Material Copy { get; private set; }
        public MaterialEditor Editor { get; private set; }
        public string Problem { get; private set; }

        public void Sync(Material original, PreviewMaterialEdits edits)
        {
            if (original == null) { Dispose(); return; }
            if (source != original || shader != original.shader || Copy == null || Editor == null)
            {
                Dispose(); source = original; shader = original.shader;
                Copy = new Material(original) { hideFlags = HideFlags.HideAndDontSave, name = original.name + " (inspector preview only)" };
                Editor = UnityEditor.Editor.CreateEditor(Copy, typeof(MaterialEditor)) as MaterialEditor;
                PrepareEditor();
                sourceDirty = -1; editsVersion = -1;
            }
            int dirty = EditorUtility.GetDirtyCount(original);
            if (sourceDirty != dirty || editsVersion != edits.Version)
            {
                Copy.CopyPropertiesFromMaterial(original); Copy.shaderKeywords = original.shaderKeywords;
                edits.ApplyTo(original, Copy); sourceDirty = dirty; editsVersion = edits.Version;
                Editor.serializedObject.Update();
                copyDirty = EditorUtility.GetDirtyCount(Copy);
            }
        }

        public bool Capture(PreviewMaterialEdits edits, bool force = false)
        {
            if (source == null || Copy == null) return false;
            if (sourceDirty != EditorUtility.GetDirtyCount(source) || editsVersion != edits.Version) { Sync(source, edits); return false; }
            if (!force && copyDirty == EditorUtility.GetDirtyCount(Copy)) return false;
            if (Copy.shader != shader)
            {
                Copy.shader = shader; Copy.CopyPropertiesFromMaterial(source); Copy.shaderKeywords = source.shaderKeywords; edits.ApplyTo(source, Copy);
                Problem = L.Tr("The inspector changed the shader; that change was cancelled.");
                return false;
            }
            bool changed = edits.Capture(source, Copy);
            editsVersion = edits.Version;
            copyDirty = EditorUtility.GetDirtyCount(Copy);
            ClearUndo();
            return changed;
        }

        public bool Inspect(PreviewMaterialEdits edits, Action<MaterialEditor, MaterialProperty[]> draw = null)
        {
            if (Editor == null || Problem != null) return false;
            var gui = Event.current == null ? null : new GuiRecovery();
            // 通常の MaterialEditor の差分 Undo は複製だけ取り除く。履歴へ入れてから消すと、元の保留中 Redo まで失う。
            Undo.PostprocessModifications filter = modifications => modifications.Where(m => m.currentValue.target != Copy && m.previousValue.target != Copy).ToArray();
            bool hadRedo = (bool)(typeof(Undo).GetMethod("HasRedo", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null) ?? false);
            Undo.postprocessModifications += filter;
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
            var originalGUI = Editor.customShaderGUI;
            try
            {
                if (draw == null)
                {
                    if (ShaderGUIField == null) throw new NotSupportedException("MaterialEditor ShaderGUI hook is unavailable.");
                    ShaderGUIField.SetValue(Editor, new CopyPropertyGUI(this, originalGUI));
                    Editor.OnInspectorGUI();
                }
                else
                {
                    var properties = MaterialEditor.GetMaterialProperties(Editor.targets);
                    PrepareProperties(properties); draw(Editor, properties);
                }
                return Capture(edits, true);
            }
            catch (ExitGUIException) { Capture(edits, true); throw; }
            catch (Exception ex)
            {
                gui?.Restore();
                // 途中まで変えて例外になった場合は、直前の編集記録から複製を復元する。
                Copy.shader = shader; Copy.CopyPropertiesFromMaterial(source); Copy.shaderKeywords = source.shaderKeywords; edits.ApplyTo(source, Copy);
                Problem = L.Tr("The shader inspector could not be drawn: {0}. Other panels are still available.", ex.Message);
                return false;
            }
            finally
            {
                try
                {
                    ShaderGUIField?.SetValue(Editor, originalGUI);
                    Undo.FlushUndoRecordObjects();
                    // RevertAllDownToGroup は空のグループでも Redo を消す。保留中なら差分除外だけで履歴を保つ。
                    if (!hadRedo) Undo.RevertAllDownToGroup(group);
                    // 差分の正本から戻すので、複製の履歴を消してもプレビューの変更は残る。
                    if (Copy != null && source != null) { Copy.shader = shader; Copy.CopyPropertiesFromMaterial(source); Copy.shaderKeywords = source.shaderKeywords; edits.ApplyTo(source, Copy); copyDirty = EditorUtility.GetDirtyCount(Copy); }
                    ClearUndo();
                }
                finally { Undo.postprocessModifications -= filter; }
            }
        }

        // MaterialProperty の既定の setter は即座に Undo を登録し、元の Redo を消す。
        // 元の ShaderGUI に渡す配列だけを調整し、値は保存されていない複製へ直接書く。
        void PrepareProperties(MaterialProperty[] properties)
        {
            foreach (var property in properties) property.applyPropertyCallback = ApplyCopyProperty;
        }

        bool ApplyCopyProperty(MaterialProperty property, int mask, object previous)
        {
            if (Copy == null || property.targets.Any(target => target != Copy)) return true;
            switch (property.type)
            {
                case MaterialProperty.PropType.Color: Copy.SetColor(property.name, property.colorValue); break;
                case MaterialProperty.PropType.Vector: Copy.SetVector(property.name, property.vectorValue); break;
                case MaterialProperty.PropType.Float:
                case MaterialProperty.PropType.Range: Copy.SetFloat(property.name, property.floatValue); break;
                case MaterialProperty.PropType.Int: Copy.SetInteger(property.name, property.intValue); break;
                case MaterialProperty.PropType.Texture:
                    if ((mask & 1) != 0) Copy.SetTexture(property.name, property.textureValue);
                    var st = property.textureScaleAndOffset;
                    if ((mask & 6) != 0) Copy.SetTextureScale(property.name, new Vector2(st.x, st.y));
                    if ((mask & 24) != 0) Copy.SetTextureOffset(property.name, new Vector2(st.z, st.w));
                    break;
            }
            EditorUtility.SetDirty(Copy);
            return true;
        }

        sealed class CopyPropertyGUI : ShaderGUI
        {
            readonly PreviewMaterialInspector owner;
            readonly ShaderGUI original;
            public CopyPropertyGUI(PreviewMaterialInspector owner, ShaderGUI original) { this.owner = owner; this.original = original; }
            public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties)
            {
                owner.PrepareProperties(properties);
                // ShaderGUI 自身から見える Editor と検証・後片付けの呼び先も、元のインスペクターのままにする。
                ShaderGUIField.SetValue(editor, original);
                if (original != null) original.OnGUI(editor, properties); else editor.PropertiesDefaultGUI(properties);
            }
        }

        // ShaderGUI が BeginVertical/BeginScrollView の途中で例外になっても、外側のパネルの Layout とクリップへ戻す。
        sealed class GuiRecovery
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            readonly object cache;
            readonly FieldInfo topField;
            readonly object top;
            readonly Stack groups;
            readonly object[] savedGroups;
            readonly Stack scrollViews;
            readonly object[] savedScrollViews;
            readonly MethodInfo clipCount, clipPop;
            readonly int clips;
            public GuiRecovery()
            {
                cache = typeof(GUILayoutUtility).GetField("current", Flags)?.GetValue(null);
                if (cache != null)
                {
                    topField = cache.GetType().GetField("topLevel", Flags); top = topField?.GetValue(cache);
                    groups = cache.GetType().GetField("layoutGroups", Flags)?.GetValue(cache) as Stack;
                    savedGroups = groups?.ToArray();
                }
                scrollViews = typeof(GUI).GetProperty("scrollViewStates", Flags)?.GetValue(null) as Stack;
                savedScrollViews = scrollViews?.ToArray();
                var clip = typeof(GUIUtility).Assembly.GetType("UnityEngine.GUIClip");
                clipCount = clip?.GetMethod("Internal_GetCount", Flags); clipPop = clip?.GetMethod("Internal_Pop", Flags);
                if (clipCount != null) clips = (int)clipCount.Invoke(null, null);
            }
            public void Restore()
            {
                if (groups != null && savedGroups != null)
                { groups.Clear(); for (int i = savedGroups.Length - 1; i >= 0; i--) groups.Push(savedGroups[i]); }
                if (scrollViews != null && savedScrollViews != null)
                { scrollViews.Clear(); for (int i = savedScrollViews.Length - 1; i >= 0; i--) scrollViews.Push(savedScrollViews[i]); }
                topField?.SetValue(cache, top);
                if (clipCount != null && clipPop != null) while ((int)clipCount.Invoke(null, null) > clips) clipPop.Invoke(null, null);
            }
        }

        public void Retry()
        {
            if (Editor != null) Object.DestroyImmediate(Editor);
            Editor = Copy != null ? UnityEditor.Editor.CreateEditor(Copy, typeof(MaterialEditor)) as MaterialEditor : null;
            PrepareEditor();
            Problem = null;
        }

        void PrepareEditor()
        {
            if (Editor == null || Copy == null) return;
            Editor.hideFlags = HideFlags.HideAndDontSave;
            // 2022.3 の MaterialEditor は InspectorWindow の先頭 Editor かどうかで可視状態と縦グループを変える。
            // 埋め込み先にはオブジェクトの折り畳みもバージョン管理バーも無いので、先頭として扱う。
            typeof(UnityEditor.Editor).GetProperty("firstInspectedEditor", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(Editor, true);
            UnityEditorInternal.InternalEditorUtility.SetIsInspectorExpanded(Copy, true);
        }

        void ClearUndo()
        {
            if (Copy == null) return;
            Undo.FlushUndoRecordObjects(); Undo.ClearUndo(Copy);
        }

        public void Dispose()
        {
            ClearUndo();
            if (Editor != null) Object.DestroyImmediate(Editor);
            if (Copy != null) Object.DestroyImmediate(Copy);
            Editor = null; Copy = null; source = null; shader = null; sourceDirty = -1; editsVersion = -1; Problem = null;
        }
    }
}
