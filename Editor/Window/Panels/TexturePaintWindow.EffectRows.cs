using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// レイヤーの重なりの効果の行（Substance Painter の効果の行）: 層の行の下に、その層のフィルター・Generator と Anchor（アンカーポイント）を
    /// 字下げした行で並べる（上が後に掛かる。マスクの段はマスクの印を付けて画素の段の下）。行は目（有効の切り替え）・アイコン・名前と主な値で、押すとその段を選び、プロパティの
    /// 欄にその段の設定が出る。マウスの乗った行と選んだ行に上へ・下へ・消すのボタン、右クリックで同じ操作と足す・焼き込みのメニュー。
    /// 一覧の行の高さが層と効果で違うので、行の位置・落とす先・行の矩形はここの <see cref="LayerListRows"/> で数える（並べ替えのドラッグと
    /// スマートマテリアルのドロップも）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        internal const float EffectRowHeight = 22;

        /// <summary>レイヤーの一覧の 1 行（層か、その層の子の行: 効果の段か Anchor）。y は一覧の中の上の端。</summary>
        internal struct LayerListRow
        {
            public PaintLayer Layer; public int Index; public FilterEffect Effect; public AnchorPoint Anchor; public FilterTarget Target; public int StackIndex, StackCount; public float Y, Height;
            /// <summary>その層の子の行の最後（つなぎの線をここで止める）。</summary>
            public bool LastChild;
            public bool IsEffect => Effect != null;
            public bool IsAnchor => Anchor != null;
            public bool IsChild => Effect != null || Anchor != null;
        }

        /// <summary>一覧の行（上から）。層の行の下にその層の子の行: 層の Anchor（そこまでの結果）、画素の効果の段（上が後）、マスクの Anchor、
        /// マスクの効果の段（Substance Painter のアンカーポイントと同じく、効果の行と並べる）。</summary>
        internal List<LayerListRow> LayerListRows()
        {
            var rows = new List<LayerListRow>(); float y = 0;
            if (document == null) return rows;
            for (int i = document.Layers.Count - 1; i >= 0; i--)
            {
                var layer = document.Layers[i];
                rows.Add(new LayerListRow { Layer = layer, Index = i, Y = y, Height = LayerRowHeight }); y += LayerRowHeight;
                int first = rows.Count;
                foreach (var target in new[] { FilterTarget.Content, FilterTarget.Mask })
                {
                    if (target == FilterTarget.Mask && layer.Mask == null) continue;
                    var anchor = target == FilterTarget.Content ? layer.Anchor : layer.Mask.Anchor;
                    if (anchor != null) { rows.Add(new LayerListRow { Layer = layer, Index = i, Anchor = anchor, Target = target, Y = y, Height = EffectRowHeight }); y += EffectRowHeight; }
                    var stack = target == FilterTarget.Content ? layer.Filters : layer.Mask.Filters;
                    for (int k = stack.Count - 1; k >= 0; k--)
                    { rows.Add(new LayerListRow { Layer = layer, Index = i, Effect = stack[k], Target = target, StackIndex = k, StackCount = stack.Count, Y = y, Height = EffectRowHeight }); y += EffectRowHeight; }
                }
                if (rows.Count > first) { var last = rows[rows.Count - 1]; last.LastChild = true; rows[rows.Count - 1] = last; }
            }
            return rows;
        }

        static float LayerListHeight(List<LayerListRow> rows) => rows.Count == 0 ? 0 : rows[rows.Count - 1].Y + rows[rows.Count - 1].Height;

        /// <summary>一覧の中の y にある行（効果の段の行も）。どの行にも当たらなければ null。</summary>
        static LayerListRow? LayerListRowAt(List<LayerListRow> rows, float y)
        {
            foreach (var r in rows) if (y >= r.Y && y < r.Y + r.Height) return r;
            return null;
        }

        /// <summary>層の行の上の端（一覧の中）。</summary>
        float LayerRowY(PaintLayer layer)
        {
            foreach (var r in LayerListRows()) if (!r.IsChild && r.Layer == layer) return r.Y;
            return 0;
        }

        /// <summary>gap 番目の層の行のすぐ上の線の高さ（gap が層の数なら一覧の下の端）。</summary>
        float LayerGapY(int gap)
        {
            var rows = LayerListRows(); int n = document.Layers.Count;
            if (gap >= n) return LayerListHeight(rows);
            var layer = document.Layers[n - 1 - Mathf.Max(0, gap)];
            return rows.First(r => !r.IsChild && r.Layer == layer).Y;
        }

        /// <summary>y にある層（効果の段の行はその層）。無ければ null。</summary>
        PaintLayer LayerAtRow(float y) => LayerListRowAt(LayerListRows(), y)?.Layer;

        // ───────── 効果の行 ─────────

        void DrawEffectRow(Rect r, LayerListRow row)
        {
            var ev = Event.current; var layer = row.Layer; var e = row.Effect;
            bool selected = layer.Id == selectedLayer && selectedFilter == e.Id, hover = GUI.enabled && r.Contains(ev.mousePosition);
            PanelSpot("effect." + e.Id, r);
            if (selected) { PaintGui.Fill(r, PaintTheme.AccentSoft); PaintGui.Fill(new Rect(r.x, r.y, 3, r.height), PaintTheme.Accent); }
            else if (hover) PaintGui.Fill(r, PaintTheme.ControlHover);
            PaintGui.HLine(r.x + 28, r.xMax, r.yMax - 1, PaintTheme.Separator);
            // 目（有効の切り替え）。層の目と同じ列
            var eye = PanelSpot("effect." + e.Id + ".eye", new Rect(r.x + 6, r.y + 2, 20, r.height - 4));
            if (PaintGui.IconButton(eye, e.Enabled ? "visibility" : "visibility_off", e.Enabled ? L.Tr("Turn the filter off") : L.Tr("Turn the filter on"), false, GUI.enabled, 14))
                SetEffectEnabled(layer.Id, e.Id, !e.Enabled);
            // 層の名前の位置から一段下げ、つなぎの線とアイコン
            float x = eye.xMax + 6 + 14 * document.DepthOf(layer.Id) + 10;
            ChildRowGuide(r, x, row.LastChild);
            if (row.Target == FilterTarget.Mask) { PaintGui.Icon(new Rect(x, r.y, 14, r.height), "vignette", PaintTheme.TextDim, 12); x += 15; }
            bool generator = e.Settings.Type == FilterType.Generator;
            PaintGui.Icon(new Rect(x, r.y, 16, r.height), generator ? "texture" : "auto_awesome", e.Enabled ? (selected ? Color.white : PaintTheme.Text) : PaintTheme.TextDisabled, 13); x += 19;
            // 上へ・下へ・消す（マウスの乗った行と選んだ行）
            bool buttons = selected || hover;
            float right = r.xMax - 4 - (buttons ? 3 * 20 : 0);
            string label = FilterLabel(e, row.Target), shown = PaintGui.Fit(label, Mathf.Max(0, right - x - 2), PaintTheme.Label, false); // 値を含む要約なので、詰めても数えない
            PaintGui.Text(new Rect(x, r.y, Mathf.Max(0, right - x), r.height), shown, PaintTheme.Label, !e.Enabled ? PaintTheme.TextDim : selected ? Color.white : PaintTheme.Text);
            PaintGui.Tooltip(new Rect(x, r.y, Mathf.Max(0, right - x), r.height), (shown != label ? label + "\n" : "") + L.Tr("Click to edit it in Properties. Right-click for more."));
            if (buttons)
            {
                if (PaintGui.IconButton(PanelSpot("effect." + e.Id + ".up", new Rect(right, r.y + 1, 20, r.height - 2)), "expand_less", L.Tr("Move up (applied later)"), false, GUI.enabled && row.StackIndex < row.StackCount - 1, 14))
                    MoveEffect(layer.Id, e.Id, row.StackIndex + 1);
                if (PaintGui.IconButton(PanelSpot("effect." + e.Id + ".down", new Rect(right + 20, r.y + 1, 20, r.height - 2)), "expand_more", L.Tr("Move down (applied earlier)"), false, GUI.enabled && row.StackIndex > 0, 14))
                    MoveEffect(layer.Id, e.Id, row.StackIndex - 1);
                if (PaintGui.IconButton(PanelSpot("effect." + e.Id + ".remove", new Rect(right + 40, r.y + 1, 20, r.height - 2)), "close", L.Tr("Remove the filter"), false, GUI.enabled, 13))
                    RemoveEffect(layer.Id, e.Id);
            }
            // 押すとその段を選ぶ（プロパティの欄にその段の設定）
            if (ev.type == EventType.MouseDown && ev.button == 0 && hover)
            {
                SelectEffect(layer.Id, e.Id);
                ev.Use(); Repaint();
            }
            if (ev.type == EventType.ContextClick && hover)
            {
                SelectEffect(layer.Id, e.Id);
                var menu = new GenericMenu(); EffectMenu(menu, row); menu.ShowAsContext(); ev.Use();
            }
        }

        /// <summary>子の行のつなぎの線（層の名前の下から、最後の子の行で止める）。</summary>
        static void ChildRowGuide(Rect r, float x, bool last)
        {
            PaintGui.VLine(x - 6, r.y, r.yMax - (last ? r.height / 2 : 0), PaintTheme.Separator);
            PaintGui.HLine(x - 6, x - 1, Mathf.Round(r.center.y), PaintTheme.Separator);
        }

        /// <summary>Anchor の行（Substance のアンカーポイントの行）: 印・名前・読んでいる段の数。押すとプロパティの欄に名前と外すボタン、
        /// マウスの乗った行と選んだ行に外すボタン、右クリックで外す。Anchor には有効・無効が無いので目は出さない。</summary>
        void DrawAnchorListRow(Rect r, LayerListRow row)
        {
            var ev = Event.current; var layer = row.Layer; var anchor = row.Anchor;
            bool selected = layer.Id == selectedLayer && selectedFilter == anchor.Id, hover = GUI.enabled && r.Contains(ev.mousePosition);
            PanelSpot("anchor." + anchor.Id, r);
            if (selected) { PaintGui.Fill(r, PaintTheme.AccentSoft); PaintGui.Fill(new Rect(r.x, r.y, 3, r.height), PaintTheme.Accent); }
            else if (hover) PaintGui.Fill(r, PaintTheme.ControlHover);
            PaintGui.HLine(r.x + 28, r.xMax, r.yMax - 1, PaintTheme.Separator);
            float x = r.x + 6 + 20 + 6 + 14 * document.DepthOf(layer.Id) + 10;
            ChildRowGuide(r, x, row.LastChild);
            if (row.Target == FilterTarget.Mask) { PaintGui.Icon(new Rect(x, r.y, 14, r.height), "vignette", PaintTheme.TextDim, 12); x += 15; }
            PaintGui.Icon(new Rect(x, r.y, 16, r.height), "anchor", PaintTheme.Accent, 13); x += 19;
            bool buttons = selected || hover;
            float right = r.xMax - 4 - (buttons ? 20 : 0);
            int readers = document.AnchorReaders(anchor.Id).Count;
            string count = L.Tr("read by {0}", readers);
            float countWidth = Mathf.Min(PaintGui.TextWidth(count, PaintTheme.LabelDim) + 6, Mathf.Max(0, right - x - 40));
            var nameRect = new Rect(x, r.y, Mathf.Max(0, right - x - countWidth), r.height);
            PaintGui.Text(nameRect, PaintGui.Fit(anchor.Name, nameRect.width - 2, PaintTheme.Label, false), PaintTheme.Label, selected ? Color.white : PaintTheme.Text); // 描き手の付けた名前なので詰めても数えない
            if (countWidth > 20) PaintGui.Text(new Rect(nameRect.xMax, r.y, countWidth, r.height), PaintGui.Fit(count, countWidth, PaintTheme.LabelDim, false), PaintTheme.LabelDim);
            PaintGui.Tooltip(new Rect(x, r.y, Mathf.Max(0, right - x), r.height), L.Tr("Anchor \"{0}\"", anchor.Name) + "\n" + L.Tr("Click to edit it in Properties. Right-click for more."));
            if (buttons && PaintGui.IconButton(PanelSpot("anchor." + anchor.Id + ".remove", new Rect(right, r.y + 1, 20, r.height - 2)), "close",
                    L.Tr("Remove the anchor (generators that read it pass their input through until you undo)"), false, GUI.enabled && stroke == null, 13))
            { RemoveAnchorOf(anchor); if (selectedFilter == anchor.Id) selectedFilter = Guid.Empty; }
            if (ev.type == EventType.MouseDown && ev.button == 0 && hover) { SelectEffect(layer.Id, anchor.Id); ev.Use(); Repaint(); }
            if (ev.type == EventType.ContextClick && hover)
            {
                SelectEffect(layer.Id, anchor.Id);
                var menu = new GenericMenu(); var a = anchor;
                if (stroke == null) menu.AddItem(new GUIContent(L.Tr("Remove Anchor")), false, () => { RemoveAnchorOf(a); if (selectedFilter == a.Id) selectedFilter = Guid.Empty; });
                else menu.AddDisabledItem(new GUIContent(L.Tr("Remove Anchor")));
                menu.ShowAsContext(); ev.Use();
            }
        }

        /// <summary>効果の段を選ぶ（その層を 1 つだけ選び、プロパティの欄にその段の設定を出す）。ほかの層の段ならマスクへの描画もやめる。</summary>
        internal void SelectEffect(Guid layerId, Guid effectId)
        {
            if (layerId != selectedLayer || IsLayerSelected(layerId) && SelectedLayers.Count > 1) { if (layerId != selectedLayer) editMask = false; SelectSingleLayer(layerId); }
            selectedFilter = effectId; renamingLayer = Guid.Empty;
        }

        internal void SetEffectEnabled(Guid layerId, Guid effectId, bool enabled) => TryAction(() => { document.SetFilterEnabled(layerId, effectId, enabled); repaintPixels = true; });
        internal void MoveEffect(Guid layerId, Guid effectId, int index) => TryAction(() => { document.MoveFilter(layerId, effectId, index); repaintPixels = true; });
        internal void RemoveEffect(Guid layerId, Guid effectId) => TryAction(() => { document.RemoveFilter(layerId, effectId); if (selectedFilter == effectId) selectedFilter = Guid.Empty; repaintPixels = true; });

        /// <summary>効果の行の右クリック: 有効の切り替え・上へ・下へ・消す、この層に足す（画素・マスク）、焼き込み。</summary>
        void EffectMenu(GenericMenu m, LayerListRow row)
        {
            var layer = row.Layer; var e = row.Effect; Guid layerId = layer.Id, id = e.Id;
            m.AddItem(new GUIContent(e.Enabled ? L.Tr("Turn the filter off") : L.Tr("Turn the filter on")), false, () => SetEffectEnabled(layerId, id, !e.Enabled));
            if (row.StackIndex < row.StackCount - 1) m.AddItem(new GUIContent(L.Tr("Move up (applied later)")), false, () => MoveEffect(layerId, id, row.StackIndex + 1));
            else m.AddDisabledItem(new GUIContent(L.Tr("Move up (applied later)")));
            if (row.StackIndex > 0) m.AddItem(new GUIContent(L.Tr("Move down (applied earlier)")), false, () => MoveEffect(layerId, id, row.StackIndex - 1));
            else m.AddDisabledItem(new GUIContent(L.Tr("Move down (applied earlier)")));
            m.AddItem(new GUIContent(L.Tr("Remove the filter")), false, () => RemoveEffect(layerId, id));
            m.AddSeparator("");
            FilterMenuItemsInto(m, L.Tr("Add Filter") + "/"); // 足すのは選んだ層（この行の層。押したときに選んだ）
            m.AddSeparator("");
            if (layer.Filters.Count > 0 || layer.Mask != null && layer.Mask.Filters.Count > 0) m.AddItem(new GUIContent(L.Tr("Bake Filters into Pixels")), false, BakeFilters);
        }

        /// <summary><see cref="FilterMenuItems"/> と同じ項目（選んだ層の画素・マスクへのフィルターと Generator）を、prefix の下の階層に入れる（効果の行の右クリック）。</summary>
        void FilterMenuItemsInto(GenericMenu m, string prefix)
        {
            var active = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            foreach (var target in new[] { FilterTarget.Content, FilterTarget.Mask })
            {
                if (active == null || target == FilterTarget.Mask && active.Mask == null) continue;
                string at = prefix + (target == FilterTarget.Content ? "" : L.Tr("On Mask") + "/");
                foreach (var (label, settings, why) in FilterChoices(active.Id, target))
                {
                    var t = target; var st = settings;
                    if (why == null) m.AddItem(new GUIContent(at + label), false, () => AddFilter(t, st)); else m.AddDisabledItem(new GUIContent(at + label + " — " + why));
                }
                foreach (var (label, type, why) in GeneratorChoices(active.Id, target))
                {
                    var t = target; var ty = type;
                    string item = at + L.Tr("Generator") + "/" + label;
                    if (why == null) m.AddItem(new GUIContent(item), false, () => AddGenerator(t, ty)); else m.AddDisabledItem(new GUIContent(item + " — " + why));
                }
            }
        }

        /// <summary>マスクのサムネイルの右クリック（Substance のマスクのメニュー）: マスクに描く・有効・反転・消す、マスクに足すフィルター。
        /// 濃度はマスクに描くあいだのプロパティの欄の「マスク」のタブ。</summary>
        void MaskMenu(GenericMenu m, PaintLayer layer)
        {
            var mask = layer.Mask; Guid id = layer.Id;
            if (mask == null) return;
            m.AddItem(new GUIContent(L.Tr("Paint on Mask")), editMask && selectedLayer == id, () => { SelectSingleLayer(id); selectedFilter = Guid.Empty; editMask = !(editMask && selectedLayer == id); });
            m.AddItem(new GUIContent(L.TrIn("mask", "Enabled")), mask.Enabled, () => TryAction(() => document.SetLayerMaskEnabled(id, !mask.Enabled)));
            m.AddItem(new GUIContent(L.TrIn("mask", "Invert")), mask.Inverted, () => TryAction(() => document.SetLayerMaskInverted(id, !mask.Inverted)));
            m.AddSeparator("");
            foreach (var (label, settings, why) in FilterChoices(id, FilterTarget.Mask))
            {
                var st = settings;
                string item = L.Tr("Add Filter") + "/" + label;
                if (why == null) m.AddItem(new GUIContent(item), false, () => { SelectSingleLayer(id); AddFilter(FilterTarget.Mask, st); });
                else m.AddDisabledItem(new GUIContent(item + " — " + why));
            }
            foreach (var (label, type, why) in GeneratorChoices(id, FilterTarget.Mask))
            {
                var ty = type;
                string item = L.Tr("Add Filter") + "/" + L.Tr("Generator") + "/" + label;
                if (why == null) m.AddItem(new GUIContent(item), false, () => { SelectSingleLayer(id); AddGenerator(FilterTarget.Mask, ty); });
                else m.AddDisabledItem(new GUIContent(item + " — " + why));
            }
            m.AddSeparator("");
            if (mask.Anchor == null) m.AddItem(new GUIContent(L.Tr("Add Anchor to Mask")), false, () => { SelectSingleLayer(id); selectedFilter = Guid.Empty; var added = AddAnchorTo(AnchorPlacement.Mask); if (added != null) selectedFilter = added.Id; });
            else { var a = mask.Anchor; m.AddItem(new GUIContent(L.Tr("Remove Mask Anchor")), false, () => RemoveAnchorOf(a)); }
            m.AddSeparator("");
            m.AddItem(new GUIContent(L.Tr("Delete Layer Mask")), false, () => TryAction(() => { document.RemoveLayerMask(id); if (selectedLayer == id) editMask = false; selectedFilter = Guid.Empty; }));
        }
    }
}
