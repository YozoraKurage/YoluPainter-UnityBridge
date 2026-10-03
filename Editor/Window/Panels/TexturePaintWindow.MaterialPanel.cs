using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.MaterialApply;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>元のマテリアルの見出しとチャンネルの流し込み先、そのシェーダーのインスペクター。
    /// 編集先は保存しない複製。元のアセットへは確認付きの反映だけで書き込む。</summary>
    public sealed partial class TexturePaintWindow
    {
        Vector2 materialScroll, materialHeaderScroll;
        float materialHeaderHeight = 200;
        Shader materialInfoShader; LilToon.LilToonReport materialInfoLil; List<MaterialPropertyInfo> materialInfos;
        const float MaterialLabelWidth = 72;
        PreviewMaterialInspector materialInspector;
        internal PreviewMaterialInspector MaterialInspector => materialInspector;
        /// <summary>欄の部品の画面上の矩形（Repaint のたびに覚え直す。GUI モードの試験が本物のマウスの入力で押すため）。</summary>
        internal readonly Dictionary<string, Rect> MaterialControlScreenRects = new Dictionary<string, Rect>();
        Rect MaterialSpot(string id, Rect r)
        {
            if (Event.current.type == EventType.Repaint) MaterialControlScreenRects[id] = GUIUtility.GUIToScreenRect(r);
            return r;
        }

        /// <summary>今のセットのマテリアル表示が見せるマテリアル（選んだもの、選んでいなければ元のマテリアル。モデルが無い・割り当てが無ければ null。
        /// Model/TexturePaintWindow.PreviewMaterial.cs）。</summary>
        internal Material PanelMaterial { get { SyncMaterialChoices(); return ViewMaterialOf(currentSet); } }

        /// <summary>シェーダーのプロパティ（シェーダーと lilToon の検査が同じあいだは作り直さない）。</summary>
        internal List<MaterialPropertyInfo> MaterialProperties(Material source)
        {
            var binding = preview.MaterialBinding(CurrentFirstSlot);
            if (materialInfos == null || materialInfoShader != source.shader || !ReferenceEquals(materialInfoLil, binding?.LilToon))
            { materialInfos = MaterialPropertyInfo.Describe(source.shader, binding?.LilToon); materialInfoShader = source.shader; materialInfoLil = binding?.LilToon; }
            return materialInfos;
        }

        void DrawMaterialPanel(Rect r)
        {
            if (Event.current.type == EventType.Repaint) MaterialControlScreenRects.Clear();
            PaintGui.Fill(r, PaintTheme.PanelBg);
            var source = PanelMaterial;
            bool was = GUI.enabled; GUI.enabled = was && stroke == null && !toolDragging;
            try
            {
                // 短いドックでも取消・反映と編集欄を残す。上の情報が長いときは見出しだけスクロールする。
                float headHeight = source == null ? r.height : Mathf.Min(200, Mathf.Max(48, r.height - 64 - 96));
                var header = new Rect(r.x, r.y, r.width, headHeight);
                MaterialSpot("material.header", header);
                var rows = new UiRows(new Rect(0, 0, r.width - 16, materialHeaderHeight), 8);
                materialHeaderScroll = GUI.BeginScrollView(header, materialHeaderScroll, new Rect(0, 0, r.width - 16, materialHeaderHeight), false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
                var binding = source != null ? preview.MaterialBinding(CurrentFirstSlot) : null;
                try
                {
                    // 1 行目: 見せるマテリアル（元のマテリアル・プロジェクトのマテリアル・シェーダーから選ぶ。Model/TexturePaintWindow.PreviewMaterial.cs）。
                    // 元のマテリアルが無いスロットでも選べる
                    if (preview != null && preview.HasModel) DrawPreviewMaterialRow(rows, source);
                    if (source == null)
                    {
                        PaintGui.Notice(rows, preview != null && preview.HasModel ? L.Tr("This material slot has no source material (the demo cube or an unassigned slot). Choose a material or a shader above to see the painted maps with it.") : L.Tr("Load a model to see and adjust its material."), "info", PaintTheme.TextDim);
                        DisposeMaterialInspector(); return;
                    }
                    EnsureMaterialInspector(source);
                    PaintGui.ValueBox(rows.Row(), L.Tr("Shader"), source.shader != null ? source.shader.name : "-", MaterialLabelWidth, null, null, true);
                    bool usable = binding != null && binding.CanShow;
                    var viewRow = rows.Row();
                    PaintGui.ValueBox(new Rect(viewRow.x, viewRow.y, viewRow.width - 28, viewRow.height), L.Tr("3D view"), binding?.Summary ?? "-", MaterialLabelWidth, usable ? PaintTheme.TextDim : PaintTheme.Warning, binding?.Unusable, true);
                    DrawChannelRoutesToggle(new Rect(viewRow.xMax - 24, viewRow.y, 24, viewRow.height), binding); // 流し込み先の一覧を開く
                    bool on = PaintGui.ToggleButton(MaterialSpot("material.shading", rows.Row(24)), L.Tr("Show This Material in 3D"), previewShading == PreviewShading.Material,
                        L.Tr("Material shading: the 3D view draws a preview copy of this material with the painted maps. Off: neutral shading."), "auto_awesome", GUI.enabled);
                    if (on != (previewShading == PreviewShading.Material)) Shading = on ? PreviewShading.Material : PreviewShading.Neutral;
                    DrawMaterialNotes(rows, source, binding);
                    DrawPaintedTextureSummary(rows, binding);
                }
                finally
                {
                    if (Event.current.type == EventType.Repaint && !Mathf.Approximately(materialHeaderHeight, rows.Used + 4)) { materialHeaderHeight = rows.Used + 4; Repaint(); }
                    GUI.EndScrollView();
                }
                DrawMaterialFooter(new Rect(r.x + PaintTheme.Padding, header.yMax + 4, r.width - 2 * PaintTheme.Padding, 52), source);
                float top = header.yMax + 64;
                if (top < r.yMax) DrawMaterialInspector(new Rect(r.x + 4, top, r.width - 8, r.yMax - top), source, binding);
            }
            finally { GUI.enabled = was; }
        }

        bool materialNotesOpen;
        /// <summary>見せ方の注意は短い数の行にまとめる（押すと開く・閉じる。閉じていてもツールチップに全文）。</summary>
        void DrawMaterialNotes(UiRows rows, Material source, PreviewMaterialBinding binding)
        {
            if (binding == null) return;
            var notes = MaterialNotes(source, binding);
            if (notes.Count == 0) return;
            var head = MaterialSpot("material.notes", rows.Row(20));
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && head.Contains(Event.current.mousePosition) && GUI.enabled) { materialNotesOpen = !materialNotesOpen; Event.current.Use(); Repaint(); }
            PaintGui.Icon(new Rect(head.x, head.y, 16, head.height), "warning", PaintTheme.Warning, 14);
            PaintGui.Icon(new Rect(head.xMax - 16, head.y, 16, head.height), materialNotesOpen ? "expand_less" : "expand_more", PaintTheme.TextDim, 16);
            PaintGui.Text(new Rect(head.x + 22, head.y, head.width - 40, head.height), L.Tr("Material notes ({0})", notes.Count), PaintTheme.LabelDim, PaintTheme.Warning);
            PaintGui.Tooltip(head, string.Join("\n\n", notes));
            if (materialNotesOpen) foreach (var note in notes) PaintGui.Notice(rows, note, "warning", PaintTheme.Warning);
        }

        /// <summary>今のスロットの見せ方の注意（中立に戻した理由、使うチャンネルの対応・トグル・倍率・タイリング、欄の変更が中立では見えないこと）。</summary>
        internal List<string> MaterialNotes(Material source, PreviewMaterialBinding binding)
        {
            var notes = new List<string>();
            string reason = preview.MaterialReason(CurrentFirstSlot);
            if (reason != null && reason != binding.Unusable) notes.Add(reason);
            notes.AddRange(binding.Notes(preview.DisplayMaterial(CurrentFirstSlot), YlpContent.UsedChannels(document)));
            if (materialEdits.For(source).Any() && previewShading == PreviewShading.Neutral) notes.Add(L.Tr("The changes show in the 3D view with material shading."));
            return notes;
        }

        void DrawMaterialFooter(Rect r, Material source)
        {
            int changed = materialEdits.For(source).Count();
            var line = new Rect(r.x, r.y, r.width, 18);
            PaintGui.Text(line, changed == 0 ? L.Tr("No changes") : L.Tr("{0} changed in the preview only (not applied)", changed), PaintTheme.LabelDim, changed == 0 ? PaintTheme.TextDim : PaintTheme.Warning);
            var buttons = UiRows.Split(new Rect(r.x, line.yMax + 4, r.width, 26), 2, 6);
            if (PaintGui.FitButton(MaterialSpot("material.revertAll", buttons[0]), L.Tr("Revert All"), false, GUI.enabled && changed > 0, L.Tr("Put every property of this material back to the material's own value (the preview only)")))
                TryAction(() => { int n = materialEdits.RevertAll(source); message = L.Tr("Reverted {0} change(s); the preview shows the material's own values again.", n); repaintPixels = true; });
            if (PaintGui.FitButton(MaterialSpot("material.apply", buttons[1]), L.Tr("Apply to Material…"), true, GUI.enabled && changed > 0, L.Tr("Write the changed values into the material itself, after listing them (Unity's Undo reverts it)")))
                TryAction(ApplyMaterialEdits);
        }

        internal void EnsureMaterialInspector(Material source)
        {
            if (materialInspector == null) materialInspector = new PreviewMaterialInspector();
            materialInspector.Sync(source, materialEdits);
        }

        void DrawPaintedTextureSummary(UiRows rows, PreviewMaterialBinding binding)
        {
            if (binding == null) return;
            var used = YlpContent.UsedChannels(document);
            string list = string.Join(" · ", binding.Channels.Where(c => used.Contains(c.Channel)).Select(c => L.Tr(c.Channel.ToString()) + " → " + c.Property));
            PaintGui.Notice(rows, L.Tr("Painted textures: {0}", list.Length == 0 ? L.Tr("not shown") : list), "info", PaintTheme.TextDim);
            var row = rows.Row(18);
            PaintGui.Text(row, L.Tr("Painted maps take priority here."), PaintTheme.LabelDim, PaintTheme.TextDim);
            PaintGui.Tooltip(row, L.Tr("Painted maps take priority in these slots. The inspector edits the underlying texture; routing above changes where paint is shown."));
        }

        void DrawMaterialInspector(Rect area, Material source, PreviewMaterialBinding binding)
        {
            // オフスクリーンでも実際に使うネイティブの文字色に背景を合わせる。
            PaintGui.Fill(area, EditorStyles.label.normal.textColor.grayscale > .5f ? new Color32(56, 56, 56, 255) : new Color32(194, 194, 194, 255));
            MaterialSpot("material.inspector", area);
            var enabled = GUI.enabled; var skin = GUI.skin; var color = GUI.color; var background = GUI.backgroundColor; var content = GUI.contentColor;
            var matrix = GUI.matrix;
            float label = EditorGUIUtility.labelWidth, field = EditorGUIUtility.fieldWidth;
            int indent = EditorGUI.indentLevel; bool wide = EditorGUIUtility.wideMode, hierarchy = EditorGUIUtility.hierarchyMode;
            bool mixed = EditorGUI.showMixedValue;
            GUI.skin = EditorGUIUtility.GetBuiltinSkin(EditorSkin.Inspector);
            GUILayout.BeginArea(area);
            try
            {
                materialScroll = EditorGUILayout.BeginScrollView(materialScroll);
                try
                {
                    // MaterialEditor.PropertiesGUI は、通常のインスペクターが用意する縦のグループを閉じて開き直す。
                    EditorGUILayout.BeginVertical();
                    try
                    {
                        EditorGUIUtility.wideMode = area.width >= 330; EditorGUIUtility.labelWidth = Mathf.Clamp(area.width * .42f, 70, 160); EditorGUI.indentLevel = 0;
                        // 流し込み先の詳細はインスペクターと一緒にスクロールする。開いても反映・取消のボタンと編集欄を押し出さない。
                        if (materialRoutesOpen && binding != null && binding.CanShow)
                        {
                            float height = 20 + Enum.GetValues(typeof(PaintChannel)).Length * (PaintTheme.RowHeight + 4) + 28;
                            var routes = new UiRows(new Rect(0, 0, Mathf.Max(0, area.width - 20), height), 4);
                            PaintGui.Fill(new Rect(0, 0, area.width, height), PaintTheme.PanelBg);
                            DrawChannelRoutes(routes, source, binding);
                            GUILayout.Space(routes.Used + 8);
                        }
                        if (materialInspector.Problem != null)
                        {
                            EditorGUI.HelpBox(new Rect(0, 0, area.width - 20, 100), materialInspector.Problem, MessageType.Warning);
                            if (GUI.Button(new Rect(0, 106, area.width - 20, 24), L.Tr("Retry Inspector"))) materialInspector.Retry();
                        }
                        else if (materialInspector.Inspect(materialEdits)) { repaintPixels = true; Repaint(); }
                    }
                    finally { EditorGUILayout.EndVertical(); }
                }
                finally { EditorGUILayout.EndScrollView(); }
            }
            finally
            {
                GUILayout.EndArea();
                GUI.enabled = enabled; GUI.skin = skin; GUI.color = color; GUI.backgroundColor = background; GUI.contentColor = content; GUI.matrix = matrix;
                EditorGUI.showMixedValue = mixed;
                EditorGUIUtility.labelWidth = label; EditorGUIUtility.fieldWidth = field; EditorGUI.indentLevel = indent; EditorGUIUtility.wideMode = wide; EditorGUIUtility.hierarchyMode = hierarchy;
            }
        }

        void CaptureMaterialInspector()
        {
            if (materialInspector != null && materialInspector.Capture(materialEdits)) { repaintPixels = true; Repaint(); }
        }

        internal void DisposeMaterialInspector() { materialInspector?.Dispose(); materialInspector = null; }

        /// <summary>プレビューの複製の値を変える（元のマテリアルは変えない）。</summary>
        internal void SetMaterialValue(Material source, MaterialPropertyInfo info, Vector4 value)
        {
            TryAction(() => { materialEdits.Set(source, info, value); repaintPixels = true; Repaint(); });
        }

        /// <summary>「マテリアルに反映…」: 変えた値の一覧を出して確かめ、受ければ元のマテリアルに入れる（Unity の Undo に 1 つ）。断れば何もしない。</summary>
        internal void ApplyMaterialEdits()
        {
            if (stroke != null) { message = L.Tr("Finish the stroke first."); return; }
            CaptureMaterialInspector();
            var source = PanelMaterial;
            if (IsMadeMaterial(source)) // シェーダーから作ったプレビューだけのマテリアル（Model/TexturePaintWindow.PreviewMaterial.cs）
            {
                string why = L.Tr("{0} was made from a shader for the preview only, so there is no material asset to apply to.", source.name);
                message = L.Tr("Not applied: {0}", why); Dialogs.Inform(L.Tr("Cannot apply to the material"), why); return;
            }
            var plan = PreviewMaterialApply.Plan(source, materialEdits);
            if (!plan.CanApply) { message = L.Tr("Not applied: {0}", string.Join(" ", plan.Refusals)); Dialogs.Inform(L.Tr("Cannot apply to the material"), plan.Describe()); return; }
            if (!Dialogs.Confirm(L.Tr("Apply to the material?"), plan.Describe(), L.Tr("Apply"), L.Tr("Cancel"))) { message = L.Tr("Not applied; the material is unchanged."); return; }
            int n = PreviewMaterialApply.Apply(plan, materialEdits);
            repaintPixels = true;
            message = L.Tr("Applied {0} change(s) to {1}. Unity's Undo (Edit ▸ Undo) reverts the material.", n, source.name);
        }

        // ───────── 未反映の変更を知らせる ─────────

        int reconciledSnapshot = -1; bool materialEditsNoticePending;
        /// <summary>Tick から: モデルを読み替えたら、そのモデルに無いマテリアルの変更を捨てて知らせる。プロジェクトを替えたら、残っている未反映の
        /// 変更を知らせる（変更はマテリアルのもので、プロジェクトには入らない）。</summary>
        internal void ReconcileMaterialEdits()
        {
            if (preview == null || materialEdits == null) return;
            if (preview.SnapshotRevision != reconciledSnapshot) // ポーズの焼き直しでも変わるが、マテリアルは同じなので何も捨てない
            {
                reconciledSnapshot = preview.SnapshotRevision;
                var present = new HashSet<Material>();
                for (int i = 0; i < preview.MaterialSlotCount; i++) { var m = preview.SourceMaterial(i); if (m != null) present.Add(m); }
                foreach (var set in TextureSets) { var chosen = ViewMaterialOf(set); if (chosen != null) present.Add(chosen); } // 選んだマテリアル（PreviewMaterial.cs）
                var gone = materialEdits.Materials.Where(m => !present.Contains(m)).ToList();
                int dropped = 0; foreach (var m in gone) dropped += materialEdits.RevertAll(m);
                if (dropped > 0) { message = L.Tr("Discarded {0} preview-only material change(s) of materials that are not in this model (they were never applied).", dropped); Repaint(); }
            }
            if (materialEditsNoticePending)
            {
                materialEditsNoticePending = false;
                int left = materialEdits.Count;
                if (left > 0) { message = (string.IsNullOrEmpty(message) ? "" : message + " ") + L.Tr("The Material panel still has {0} change(s) that are only in the preview (not applied to the material).", left); Repaint(); }
            }
        }

        /// <summary>ウィンドウを閉じるとき（OnDestroy。リロードでは呼ばれない）: 未反映の変更があれば、捨てることを Console に知らせる。</summary>
        void WarnUnappliedMaterialEdits()
        {
            if (materialEdits == null || materialEdits.Count == 0) return;
            Debug.LogWarning("YoluPainter: " + L.Tr("{0} preview-only material change(s) were not applied to the material and are discarded: {1}", materialEdits.Count,
                string.Join(", ", materialEdits.All.Select(e => e.material.name + "." + e.property))));
        }
    }
}
