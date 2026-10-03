using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.MaterialApply;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// マテリアルの欄: 今のテクスチャセットのスロットの元のマテリアル（シェーダー・lilToon なら版とバリアント・マテリアル表示の対応と注意）と、
    /// シェーダーのプロパティの一覧（[HideInInspector] は出さない。切り替え・スライダー・色・数値・ベクトル・選択肢、テクスチャは名前と
    /// 「塗ったチャンネルが入る」印だけ）。値を変えるとプレビューの複製だけが変わり（<see cref="PreviewMaterialEdits"/>。マテリアル表示で見える）、
    /// 変えたものに印・プロパティごとと全部の「元に戻す」。元のマテリアルに入れるのは「マテリアルに反映…」（一覧で確かめ、Unity の Undo に 1 つ）だけ。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        string materialFilter = "";
        Vector2 materialScroll;
        Shader materialInfoShader; LilToon.LilToonReport materialInfoLil; List<MaterialPropertyInfo> materialInfos;
        const float MaterialRowHeight = 24, MaterialLabelWidth = 72;
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
            var binding = preview.MaterialBinding(materialSlot);
            if (materialInfos == null || materialInfoShader != source.shader || !ReferenceEquals(materialInfoLil, binding?.LilToon))
            { materialInfos = MaterialPropertyInfo.Describe(source.shader, binding?.LilToon); materialInfoShader = source.shader; materialInfoLil = binding?.LilToon; }
            return materialInfos;
        }

        /// <summary>欄に出すプロパティ: [HideInInspector] と、同じ名前で何度も宣言された見出し用のもの（lilToon の _DummyProperty など。
        /// どれを変えても同じ 1 つの値）を除き、絞り込みの文字を名前か説明に含むもの。</summary>
        internal List<MaterialPropertyInfo> VisibleMaterialProperties(Material source)
        {
            var all = MaterialProperties(source);
            var repeated = new HashSet<string>(all.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key));
            return all.Where(p => !p.Hidden && !repeated.Contains(p.Name) && (string.IsNullOrEmpty(materialFilter) || p.Name.IndexOf(materialFilter, StringComparison.OrdinalIgnoreCase) >= 0
                || (p.Description ?? "").IndexOf(materialFilter, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }
        internal string MaterialFilter { get => materialFilter; set => materialFilter = value ?? ""; }

        void DrawMaterialPanel(Rect r)
        {
            if (Event.current.type == EventType.Repaint) MaterialControlScreenRects.Clear();
            PaintGui.Fill(r, PaintTheme.PanelBg);
            var source = PanelMaterial;
            var rows = new UiRows(r, 8);
            bool was = GUI.enabled; GUI.enabled = was && stroke == null;
            try
            {
                // 1 行目: 見せるマテリアル（元のマテリアル・プロジェクトのマテリアル・シェーダーから選ぶ。Model/TexturePaintWindow.PreviewMaterial.cs）。
                // 元のマテリアルが無いスロットでも選べる
                if (preview != null && preview.HasModel) DrawPreviewMaterialRow(rows, source);
                if (source == null)
                {
                    PaintGui.Notice(rows, preview != null && preview.HasModel ? L.Tr("This material slot has no source material (the demo cube or an unassigned slot). Choose a material or a shader above to see the painted maps with it.") : L.Tr("Load a model to see and adjust its material."), "info", PaintTheme.TextDim);
                    return;
                }
                var binding = preview.MaterialBinding(materialSlot);
                PaintGui.ValueBox(rows.Row(), L.Tr("Shader"), source.shader != null ? source.shader.name : "-", MaterialLabelWidth, null, null, true);
                bool usable = binding != null && binding.CanShow;
                var viewRow = rows.Row();
                PaintGui.ValueBox(new Rect(viewRow.x, viewRow.y, viewRow.width - 28, viewRow.height), L.Tr("3D view"), binding?.Summary ?? "-", MaterialLabelWidth, usable ? PaintTheme.TextDim : PaintTheme.Warning, binding?.Unusable, true);
                DrawChannelRoutesToggle(new Rect(viewRow.xMax - 24, viewRow.y, 24, viewRow.height), binding); // 流し込み先の一覧を開く
                bool on = PaintGui.ToggleButton(MaterialSpot("material.shading", rows.Row(24)), L.Tr("Show This Material in 3D"), previewShading == PreviewShading.Material,
                    L.Tr("Material shading: the 3D view draws a preview copy of this material with the painted maps. Off: neutral shading."), "auto_awesome", GUI.enabled);
                if (on != (previewShading == PreviewShading.Material)) Shading = on ? PreviewShading.Material : PreviewShading.Neutral;
                DrawMaterialNotes(rows, source, binding);
                DrawChannelRoutes(rows, source, binding);
                materialFilter = PaintGui.SearchField(MaterialSpot("material.filter", rows.Row()), materialFilter, L.Tr("Filter properties"), L.Tr("Show the properties whose name or description contains this")) ?? "";
                // 下の 2 行（変えた数・ボタン）を残して、残りをプロパティの一覧にする
                const float footer = 4 + 18 + 4 + 26 + 8;
                float top = r.y + rows.Used + 2;
                var list = new Rect(r.x, top, r.width, Mathf.Max(MaterialRowHeight, r.yMax - footer - top));
                DrawMaterialPropertyList(list, source, binding);
                DrawMaterialFooter(new Rect(r.x + PaintTheme.Padding, list.yMax + 4, r.width - 2 * PaintTheme.Padding, footer - 4), source);
            }
            finally { GUI.enabled = was; }
        }

        bool materialNotesOpen;
        /// <summary>見せ方の注意。1 つならそのまま、2 つ以上なら数の行（押すと開く・閉じる。閉じていてもツールチップに全文）。</summary>
        void DrawMaterialNotes(UiRows rows, Material source, PreviewMaterialBinding binding)
        {
            if (binding == null) return;
            var notes = MaterialNotes(source, binding);
            if (notes.Count == 1) { PaintGui.Notice(rows, notes[0], "warning", PaintTheme.Warning); return; }
            if (notes.Count == 0) return;
            var head = MaterialSpot("material.notes", rows.Row(20));
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && head.Contains(Event.current.mousePosition) && GUI.enabled) { materialNotesOpen = !materialNotesOpen; Event.current.Use(); Repaint(); }
            PaintGui.Icon(new Rect(head.x, head.y, 16, head.height), "warning", PaintTheme.Warning, 14);
            PaintGui.Icon(new Rect(head.xMax - 16, head.y, 16, head.height), materialNotesOpen ? "expand_less" : "expand_more", PaintTheme.TextDim, 16);
            PaintGui.Text(new Rect(head.x + 22, head.y, head.width - 40, head.height), L.Tr("{0} notes on how the 3D view shows this material", notes.Count), PaintTheme.LabelDim, PaintTheme.Warning);
            PaintGui.Tooltip(head, string.Join("\n\n", notes));
            if (materialNotesOpen) foreach (var note in notes) PaintGui.Notice(rows, note, "warning", PaintTheme.Warning);
        }

        /// <summary>今のスロットの見せ方の注意（中立に戻した理由、使うチャンネルの対応・トグル・倍率・タイリング、欄の変更が中立では見えないこと）。</summary>
        internal List<string> MaterialNotes(Material source, PreviewMaterialBinding binding)
        {
            var notes = new List<string>();
            string reason = preview.MaterialReason(materialSlot);
            if (reason != null && reason != binding.Unusable) notes.Add(reason);
            notes.AddRange(binding.Notes(preview.DisplayMaterial(materialSlot), YlpContent.UsedChannels(document)));
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

        /// <summary>プロパティの一覧（見えている行だけを描く。多いシェーダー（lilToon は 480 を超える）でも重くしない）。</summary>
        void DrawMaterialPropertyList(Rect list, Material source, PreviewMaterialBinding binding)
        {
            var e = Event.current;
            var props = VisibleMaterialProperties(source);
            float content = props.Count * MaterialRowHeight;
            bool overflow = content > list.height + .5f;
            materialScroll.y = Mathf.Clamp(materialScroll.y, 0, Mathf.Max(0, content - list.height));
            PaintGui.Rounded(list, PaintTheme.MenuBg, 0);
            if (props.Count == 0) { PaintGui.Text(new Rect(list.x + PaintTheme.Padding, list.y, list.width - 16, MaterialRowHeight), L.Tr("No property matches the filter."), PaintTheme.LabelDim); return; }
            var used = new HashSet<PaintChannel>(YlpContent.UsedChannels(document));
            int first = Mathf.Max(0, Mathf.FloorToInt(materialScroll.y / MaterialRowHeight)), last = Mathf.Min(props.Count - 1, Mathf.CeilToInt((materialScroll.y + list.height) / MaterialRowHeight));
            PaintGui.BeginScroll(list, materialScroll);
            for (int i = first; i <= last; i++)
            {
                var row = new Rect(PaintTheme.Padding, i * MaterialRowHeight + 1, list.width - 2 * PaintTheme.Padding - (overflow ? 6 : 0), MaterialRowHeight - 2);
                DrawMaterialProperty(row, source, binding, props[i], used);
            }
            PaintGui.EndScroll();
            // 押すための矩形（見えている範囲に切り詰める）
            if (e.type == EventType.Repaint)
                for (int i = first; i <= last; i++)
                {
                    float y = list.y + i * MaterialRowHeight - materialScroll.y;
                    if (y + MaterialRowHeight <= list.y || y >= list.yMax) continue;
                    MaterialSpot("material.prop." + props[i].Name, Rect.MinMaxRect(list.x + PaintTheme.Padding, Mathf.Max(y + 1, list.y), list.xMax - PaintTheme.Padding - (overflow ? 6 : 0) - 24, Mathf.Min(y + MaterialRowHeight - 1, list.yMax)));
                    if (materialEdits.Find(source, props[i].Name) != null)
                        MaterialSpot("material.revert." + props[i].Name, new Rect(list.xMax - PaintTheme.Padding - (overflow ? 6 : 0) - 22, y + 1, 22, MaterialRowHeight - 2));
                }
            if (e.type == EventType.ScrollWheel && list.Contains(e.mousePosition) && overflow)
            { materialScroll.y = Mathf.Clamp(materialScroll.y + e.delta.y * 14, 0, content - list.height); e.Use(); Repaint(); }
            if (overflow) PaintGui.Rounded(new Rect(list.xMax - 6, list.y + list.height * materialScroll.y / content, 4, Mathf.Max(8, list.height * list.height / content)), PaintTheme.ControlActive, 2);
        }

        /// <summary>プロパティの 1 行: 変えた印・値の部品・元に戻す。</summary>
        void DrawMaterialProperty(Rect row, Material source, PreviewMaterialBinding binding, MaterialPropertyInfo info, HashSet<PaintChannel> used)
        {
            var edit = materialEdits.Find(source, info.Name);
            var value = edit != null ? edit.value : MaterialPropertyEdit.Read(source, info.Name, info.Type);
            string tip = info.Name + (string.IsNullOrWhiteSpace(info.Description) || info.Description == info.Name ? "" : "\n" + info.Description) + "\n" + info.Type;
            if (edit != null) PaintGui.Dot(new Rect(row.x - 7, row.y, 6, row.height), PaintTheme.Accent, 5);
            var control = new Rect(row.x, row.y, row.width - 24, row.height);
            Vector4? next = null;
            switch (info.Control)
            {
                case MaterialPropertyControl.Toggle:
                {
                    bool v = value.x != 0;
                    bool picked = PaintGui.Toggle(control, PaintGui.Fit(info.Label, control.width - 23, PaintTheme.Label, false), v, tip);
                    if (picked != v) next = new Vector4(picked ? 1 : 0, 0, 0, 0);
                    break;
                }
                case MaterialPropertyControl.Slider:
                case MaterialPropertyControl.IntSlider:
                {
                    bool integer = info.Control == MaterialPropertyControl.IntSlider;
                    double picked = PaintGui.KeepSlider(control, info.Label, value.x, info.Range.x, info.Range.y, integer ? "0" : "0.###", "", tip, true, 1, labelIsData: true);
                    if (picked != value.x) next = new Vector4(integer ? Mathf.Round((float)picked) : (float)picked, 0, 0, 0);
                    break;
                }
                case MaterialPropertyControl.Float:
                case MaterialPropertyControl.Int:
                {
                    bool integer = info.Control == MaterialPropertyControl.Int;
                    string text = integer ? value.x.ToString("0", CultureInfo.InvariantCulture) : value.x.ToString("0.###", CultureInfo.InvariantCulture);
                    string label = PaintGui.Fit(info.Label, control.width - 14 - PaintGui.TextWidth(text, PaintTheme.Value) - 8, PaintTheme.Label, false);
                    float picked = PaintGui.NumberField(control, label, value.x, integer ? "0" : "0.###", "", integer ? 1 : .01f, float.MinValue, float.MaxValue, tip);
                    if (picked != value.x) next = new Vector4(integer ? Mathf.Round(picked) : picked, 0, 0, 0);
                    break;
                }
                case MaterialPropertyControl.Enum:
                {
                    float lw = Mathf.Round(control.width * .45f);
                    string option = info.Options.Where(o => o.value == value.x).Select(o => o.label).FirstOrDefault() ?? value.x.ToString("0.###", CultureInfo.InvariantCulture);
                    var captured = info;
                    PaintGui.FitDropdown(control, PaintGui.Fit(info.Label, lw - 6, PaintTheme.Label, false), option, at =>
                    {
                        var menu = new GenericMenu();
                        foreach (var (label, v) in captured.Options) { float chosen = v; menu.AddItem(new GUIContent(label), chosen == value.x, () => SetMaterialValue(source, captured, new Vector4(chosen, 0, 0, 0))); }
                        menu.DropDown(at);
                    }, tip, GUI.enabled, lw, true);
                    break;
                }
                case MaterialPropertyControl.Color:
                {
                    float sw = Mathf.Min(64, control.width * .35f);
                    PaintGui.Text(new Rect(control.x, control.y, control.width - sw - 6, control.height), PaintGui.Fit(info.Label, control.width - sw - 6, PaintTheme.Label, false), PaintTheme.Label, GUI.enabled ? PaintTheme.Text : PaintTheme.TextDisabled);
                    PaintGui.Tooltip(new Rect(control.x, control.y, control.width - sw - 6, control.height), tip);
                    var captured = info;
                    PaintGui.ColorSwatch(new Rect(control.xMax - sw, control.y + 2, sw, control.height - 4), new Color(value.x, value.y, value.z, value.w),
                        c => SetMaterialValue(source, captured, new Vector4(c.r, c.g, c.b, c.a)), true, tip + (info.Hdr ? "\nHDR" : ""), GUI.enabled, info.Hdr);
                    break;
                }
                case MaterialPropertyControl.Vector:
                {
                    var cells = PaintGui.LabeledColumns(control, PaintGui.Fit(info.Label, control.width * .32f - 6, PaintTheme.Label, false), Mathf.Round(control.width * .32f), 4, 2);
                    var v = value;
                    for (int k = 0; k < 4; k++)
                    {
                        float picked = PaintGui.NumberField(cells[k], "", v[k], "0.##", "", .01f, float.MinValue, float.MaxValue, tip);
                        if (picked != v[k]) { v[k] = picked; next = v; }
                    }
                    break;
                }
                case MaterialPropertyControl.Texture:
                {
                    var painted = binding != null && binding.CanShow ? binding.Channels.Where(c => c.Property == info.Name && used.Contains(c.Channel)).Select(c => L.Tr(c.Channel.ToString())).ToList() : new List<string>();
                    var texture = source.GetTexture(info.Name);
                    string shown = painted.Count > 0 ? L.Tr("painted {0}", string.Join(" + ", painted)) : texture != null ? texture.name : L.Tr("None");
                    float lw = Mathf.Round(control.width * .45f);
                    PaintGui.ValueBox(control, PaintGui.Fit(info.Label, lw - 6, PaintTheme.Label, false), shown, lw, painted.Count > 0 ? PaintTheme.Accent : PaintTheme.TextDim,
                        tip + (painted.Count > 0 ? "\n" + L.Tr("The material view puts the painted {0} here (the material keeps {1}).", string.Join(" + ", painted), texture != null ? texture.name : L.Tr("None")) : ""), true);
                    break;
                }
            }
            if (next.HasValue) SetMaterialValue(source, info, next.Value);
            if (edit != null && PaintGui.IconButton(new Rect(row.xMax - 22, row.y, 22, row.height), "restart_alt", L.Tr("Back to the material's own value"), false, GUI.enabled, 15))
                TryAction(() => { materialEdits.Revert(source, info.Name); repaintPixels = true; Repaint(); });
        }

        /// <summary>プレビューの複製の値を変える（元のマテリアルは変えない）。</summary>
        internal void SetMaterialValue(Material source, MaterialPropertyInfo info, Vector4 value)
        {
            TryAction(() => { materialEdits.Set(source, info, value); repaintPixels = true; Repaint(); });
        }

        /// <summary>「マテリアルに反映…」: 変えた値の一覧を出して確かめ、受ければ元のマテリアルに入れる（Unity の Undo に 1 つ）。断れば何もしない。</summary>
        internal void ApplyMaterialEdits()
        {
            if (stroke != null) { message = L.Tr("Finish the stroke first."); return; }
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
