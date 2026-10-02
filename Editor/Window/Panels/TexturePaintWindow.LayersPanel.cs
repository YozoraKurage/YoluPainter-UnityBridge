using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>レイヤーのパネル: 合成モードと不透明度、行（サムネイル・マスク・名前の変更・右クリック）、ドラッグでの並べ替え、下のツールバー、レイヤーの操作（メニューと共有）。</summary>
    public sealed partial class TexturePaintWindow
    {
        const float LayerRowHeight = 30, LayerToolbarHeight = 30;

        void DrawLayersPanel(Rect r)
        {
            var active = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            // 上: 合成モードと不透明度（Photoshop の配置）
            var top = new Rect(r.x + PaintTheme.Padding, r.y + 6, r.width - 2 * PaintTheme.Padding, 24);
            var halves = UiRows.Split(top, 2, 6);
            if (active != null)
            {
                var modes = ((LayerBlendMode[])Enum.GetValues(typeof(LayerBlendMode))).Where(m => active.IsGroup || m != LayerBlendMode.PassThrough).ToArray();
                PaintGui.EnumDropdown(halves[0], null, active.BlendMode, modes, m => L.TrIn("blend mode", BlendName(m)), m => TryAction(() => document.SetLayerBlendMode(active.Id, m)));
                float opacity = PaintGui.Slider(halves[1], L.Tr("Opacity"), (float)active.Opacity * 100, 0, 100, "0", "%") / 100;
                if (Math.Abs(opacity - active.Opacity) > .00001) document.SetLayerOpacity(active.Id, opacity, coalesce: true);
            }
            // 一覧
            if (Event.current.type == EventType.Repaint) ForgetStaleThumbnails();
            var list = new Rect(r.x, top.yMax + 6, r.width, r.height - (top.yMax + 6 - r.y) - LayerToolbarHeight);
            PaintGui.Fill(list, PaintTheme.ControlBg);
            float content = document.Layers.Count * LayerRowHeight;
            var view = new Rect(0, 0, list.width - (content > list.height ? 10 : 0), content);
            layerScroll.y = Mathf.Clamp(layerScroll.y, 0, Mathf.Max(0, content - list.height));
            PaintGui.BeginScroll(list, layerScroll);
            int row = 0;
            for (int i = document.Layers.Count - 1; i >= 0; i--, row++) DrawLayerRow(new Rect(0, row * LayerRowHeight, view.width, LayerRowHeight), document.Layers[i], i);
            HandleLayerDrag(view.width);
            PaintGui.EndScroll();
            if (Event.current.type == EventType.ScrollWheel && list.Contains(Event.current.mousePosition))
            { layerScroll.y = Mathf.Clamp(layerScroll.y + Event.current.delta.y * 12, 0, Mathf.Max(0, content - list.height)); Event.current.Use(); Repaint(); }
            if (content > list.height) PaintGui.Rounded(new Rect(list.xMax - 6, list.y + list.height * layerScroll.y / content, 4, list.height * list.height / content), PaintTheme.ControlActive, 2);
            // 下: 操作
            var bar = new Rect(r.x, list.yMax, r.width, LayerToolbarHeight);
            PaintGui.Fill(bar, PaintTheme.PanelHeader);
            float x = bar.x + 4; Rect B() { var b = new Rect(x, bar.y + 3, 26, 24); x += 27; return b; }
            if (PaintGui.IconButton(B(), "add", L.Tr("New Layer"), false, true, 18)) TryAction(AddPaintLayer);
            if (PaintGui.IconButton(B(), "format_color_fill", L.Tr("New Fill Layer"), false, true, 17)) TryAction(AddFillLayerHere);
            if (PaintGui.IconButton(B(), "tune", L.Tr("New Adjustment Layer"), false, true, 17))
            {
                var menu = new GenericMenu();
                foreach (var (label, make) in AdjustmentMenu) { var mk = make; var lb = label; menu.AddItem(new GUIContent(L.Tr(lb)), false, () => TryAction(() => selectedLayer = document.AddAdjustmentLayer(L.Tr(lb), mk(), above: AboveSelected()).Id)); }
                menu.ShowAsContext();
            }
            if (PaintGui.IconButton(B(), "folder", L.Tr("Group Layers"), false, active != null, 17)) TryAction(() => selectedLayer = document.GroupLayers(new[] { selectedLayer }, L.Tr("Group") + " " + (document.Layers.Count(l => l.IsGroup) + 1)).Id);
            bool hasMask = active?.Mask != null;
            if (PaintGui.IconButton(B(), "vignette", hasMask ? L.Tr("Edit Layer Mask") : L.Tr("Add Layer Mask"), hasMask && editMask, active != null, 17))
                TryAction(() => { if (!hasMask) { document.AddLayerMask(selectedLayer); editMask = true; } else editMask = !editMask; });
            if (PaintGui.IconButton(B(), "auto_awesome", L.Tr("Add Filter"), false, active != null, 17)) { var menu = new GenericMenu(); FilterMenuItems(menu); menu.ShowAsContext(); }
            x = bar.xMax - 4 - 27 * 3;
            if (PaintGui.IconButton(B(), "expand_less", L.Tr("Move Layer Up"), false, active != null, 18)) TryAction(() => MoveSelectedLayer(+1));
            if (PaintGui.IconButton(B(), "expand_more", L.Tr("Move Layer Down"), false, active != null, 18)) TryAction(() => MoveSelectedLayer(-1));
            if (PaintGui.IconButton(B(), "delete", L.Tr("Delete Layer"), false, document.Layers.Count > 1, 17)) TryAction(DeleteSelectedLayer);
        }

        void DrawLayerRow(Rect r, PaintLayer layer, int index)
        {
            var e = Event.current; bool selected = layer.Id == selectedLayer, hover = r.Contains(e.mousePosition);
            if (selected) { PaintGui.Fill(r, PaintTheme.AccentSoft); PaintGui.Fill(new Rect(r.x, r.y, 3, r.height), PaintTheme.Accent); }
            else if (hover) PaintGui.Fill(r, PaintTheme.ControlHover);
            PaintGui.HLine(r.x, r.xMax, r.yMax - 1, PaintTheme.Border);
            // 目
            var eye = new Rect(r.x + 4, r.y + 3, 24, r.height - 6);
            if (PaintGui.IconButton(eye, layer.Visible ? "visibility" : "visibility_off", L.Tr(layer.Visible ? "Hide" : "Show"), false, true, 16)) TryAction(() => document.SetLayerVisibility(layer.Id, !layer.Visible));
            float x = eye.xMax + 4 + 14 * document.DepthOf(layer.Id);
            if (document.IsEffectivelyClipped(index)) { PaintGui.Icon(new Rect(x, r.y, 14, r.height), "keyboard_arrow_down", PaintTheme.TextDim, 14); x += 14; }
            // 種類（サムネイルの位置）
            var thumb = new Rect(x, r.y + 4, r.height - 8, r.height - 8);
            var picture = LayerThumbnail(layer);
            if (picture != null || layer.Kind == LayerKind.Raster && !layer.IsGroup) DrawThumbnail(thumb, picture);
            else
            {
                PaintGui.Rounded(thumb, PaintTheme.PanelHeader, 3);
                PaintGui.Icon(thumb, layer.IsGroup ? "folder" : layer.Kind == LayerKind.Fill ? "format_color_fill" : "tune", PaintTheme.TextDim, 15);
            }
            if (layer.Path != null) PaintGui.Icon(new Rect(thumb.xMax - 11, thumb.yMax - 11, 12, 12), "conversion_path", Color.white, 11); // パスで描かれた層の印
            x = thumb.xMax + 6;
            if (layer.Mask != null)
            {
                var maskBox = new Rect(x, r.y + 4, r.height - 8, r.height - 8);
                bool editing = selected && editMask;
                DrawThumbnail(maskBox, MaskThumbnail(layer));
                if (editing) PaintGui.Outline(new Rect(maskBox.x - 2, maskBox.y - 2, maskBox.width + 4, maskBox.height + 4), PaintTheme.Accent, 2, 2);
                PaintGui.Tooltip(maskBox, L.Tr("Layer mask (click to paint on it)"));
                if (e.type == EventType.MouseDown && e.button == 0 && maskBox.Contains(e.mousePosition) && GUI.enabled) { selectedLayer = layer.Id; editMask = !editing; e.Use(); }
                x = maskBox.xMax + 6;
            }
            // 名前（ダブルクリックで変える）
            var nameRect = new Rect(x, r.y + 4, r.xMax - x - 26, r.height - 8);
            if (renamingLayer == layer.Id)
            {
                string next = PaintGui.TextField(nameRect, layer.Name);
                if (next != layer.Name) { TryAction(() => document.SetLayerName(layer.Id, next)); renamingLayer = Guid.Empty; }
                if (e.type == EventType.MouseDown && !nameRect.Contains(e.mousePosition)) renamingLayer = Guid.Empty;
            }
            else PaintGui.Text(nameRect, layer.Name, PaintTheme.Label, layer.Visible ? (selected ? Color.white : PaintTheme.Text) : PaintTheme.TextDim);
            // 右端の印
            if (layer.Filters.Count > 0 || layer.Mask != null && layer.Mask.Filters.Count > 0) PaintGui.Icon(new Rect(r.xMax - 24, r.y, 20, r.height), "auto_awesome", PaintTheme.TextDim, 13);
            else if (!layer.IsGroup && layer.Kind == LayerKind.Raster && !layer.IsChannelEnabled(channel)) { PaintGui.Icon(new Rect(r.xMax - 24, r.y, 20, r.height), "link_off", PaintTheme.TextDisabled, 13); PaintGui.Tooltip(new Rect(r.xMax - 24, r.y, 20, r.height), L.Tr("This layer has no pixels in the selected channel yet")); }
            // 選ぶ・ダブルクリックで名前
            if (e.type == EventType.MouseDown && e.button == 0 && hover && GUI.enabled && renamingLayer != layer.Id)
            {
                bool twice = lastLayerClicked == layer.Id && EditorApplication.timeSinceStartup - lastLayerClick < .4;
                selectedLayer = layer.Id; lastLayerClicked = layer.Id; lastLayerClick = EditorApplication.timeSinceStartup;
                if (twice && nameRect.Contains(e.mousePosition)) renamingLayer = layer.Id;
                if (!selected) editMask = false;
                layerDragCandidate = layer.Id; layerDragStart = e.mousePosition; layerDragging = false;
                e.Use(); Repaint();
            }
            if (e.type == EventType.ContextClick && hover && GUI.enabled) { selectedLayer = layer.Id; var menu = new GenericMenu(); LayerMenu(menu); menu.ShowAsContext(); e.Use(); }
        }

        // ───────── ドラッグでの並べ替え ─────────
        Guid layerDragCandidate; Vector2 layerDragStart; bool layerDragging;

        /// <summary>ドラッグで落とす先: 行と行の間（gap 番目の行のすぐ上。gap が行の数なら一番下）か、グループの行の中ほど（その中の一番上）。</summary>
        (int gap, PaintLayer into) LayerDropTarget(float y)
        {
            int n = document.Layers.Count, rowIndex = Mathf.FloorToInt(y / LayerRowHeight);
            float within = y / LayerRowHeight - rowIndex;
            if (rowIndex >= 0 && rowIndex < n)
            {
                var layer = document.Layers[n - 1 - rowIndex];
                if (layer.IsGroup && within > .3f && within < .7f) return (-1, layer);
            }
            return (Mathf.Clamp(within < .5f ? rowIndex : rowIndex + 1, 0, n), null);
        }

        void HandleLayerDrag(float width)
        {
            var e = Event.current;
            if (layerDragCandidate == Guid.Empty) return;
            if (e.rawType == EventType.MouseUp)
            {
                if (layerDragging) { var target = LayerDropTarget(e.mousePosition.y); var id = layerDragCandidate; TryAction(() => DropLayer(id, target.gap, target.into)); }
                layerDragCandidate = Guid.Empty; layerDragging = false; Repaint();
                return;
            }
            if (e.type == EventType.MouseDrag && e.button == 0)
            {
                if (!layerDragging && Vector2.Distance(e.mousePosition, layerDragStart) > 5) layerDragging = true;
                if (layerDragging) { e.Use(); Repaint(); }
            }
            if (layerDragging && e.type == EventType.Repaint)
            {
                var target = LayerDropTarget(e.mousePosition.y);
                if (target.into != null)
                {
                    int rowOf = document.Layers.Count - 1 - document.Layers.ToList().IndexOf(target.into);
                    PaintGui.Outline(new Rect(1, rowOf * LayerRowHeight + 1, width - 2, LayerRowHeight - 2), PaintTheme.Accent, 2, 3);
                }
                else PaintGui.Fill(new Rect(4, target.gap * LayerRowHeight - 1, width - 8, 2), PaintTheme.Accent);
            }
        }

        /// <summary>ドラッグで落とした所へ層を移す（1 回の Undo）。</summary>
        internal void DropLayer(Guid id, int gap, PaintLayer into)
        {
            var dragged = document.GetLayer(id);
            if (into != null)
            {
                if (into == dragged) return;
                document.MoveLayerTo(id, into.Id, document.ChildrenOf(into.Id).Count(l => l != dragged));
                message = L.Tr("Moved into {0}.", into.Name); return;
            }
            int n = document.Layers.Count;
            if (gap >= n) { document.MoveLayerTo(id, Guid.Empty, 0); return; } // 一番下
            var below = document.Layers[n - 1 - gap]; // 落とした線のすぐ下の行の層。その上に置く
            if (below == dragged) return;
            var siblings = document.ChildrenOf(below.ParentId).Where(l => l != dragged).ToList();
            document.MoveLayerTo(id, below.ParentId, siblings.IndexOf(below) + 1);
        }

        static string BlendName(LayerBlendMode mode)
        {
            switch (mode)
            {
                case LayerBlendMode.PassThrough: return "Pass Through";
                case LayerBlendMode.LinearBurn: return "Linear Burn";
                case LayerBlendMode.ColorBurn: return "Color Burn";
                case LayerBlendMode.ColorDodge: return "Color Dodge";
                case LayerBlendMode.LinearDodge: return "Linear Dodge (Add)";
                case LayerBlendMode.SoftLight: return "Soft Light";
                case LayerBlendMode.HardLight: return "Hard Light";
                case LayerBlendMode.VividLight: return "Vivid Light";
                case LayerBlendMode.LinearLight: return "Linear Light";
                case LayerBlendMode.PinLight: return "Pin Light";
                case LayerBlendMode.HardMix: return "Hard Mix";
                case LayerBlendMode.DarkerColor: return "Darker Color";
                case LayerBlendMode.LighterColor: return "Lighter Color";
                default: return mode.ToString();
            }
        }


        Guid? AboveSelected() => document.Layers.Any(l => l.Id == selectedLayer) ? selectedLayer : (Guid?)null;
        void AddPaintLayer() => selectedLayer = document.AddLayer(L.Tr("Layer") + " " + (document.Layers.Count + 1), above: AboveSelected()).Id;
        void AddFillLayerHere() => selectedLayer = document.AddFillLayer(L.Tr("Fill") + " " + (document.Layers.Count + 1), new Dictionary<PaintChannel, Rgba32> { { channel, GetBrush().Color } }, above: AboveSelected()).Id;
        static readonly (string label, Func<AdjustmentSettings> make)[] AdjustmentMenu =
            { ("Invert", AdjustmentSettings.Invert), ("Levels", () => AdjustmentSettings.Levels()), ("Hue / Saturation", () => AdjustmentSettings.HueSaturation()) };
        void DeleteSelectedLayer()
        {
            if (document.Layers.Count < 2) return;
            document.RemoveLayer(selectedLayer);
            selectedLayer = document.Layers.Count > 0 ? document.Layers[document.Layers.Count - 1].Id : Guid.Empty;
        }
        void UngroupSelected()
        {
            var first = document.ChildrenOf(selectedLayer).LastOrDefault();
            document.Ungroup(selectedLayer);
            selectedLayer = first != null ? first.Id : (document.Layers.Count > 0 ? document.Layers[document.Layers.Count - 1].Id : Guid.Empty);
        }
        void MoveSelectedLayer(int delta)
        {
            var active = document.GetLayer(selectedLayer);
            var siblings = document.ChildrenOf(active.ParentId).ToList(); int at = siblings.IndexOf(active) + delta;
            if (at < 0 || at >= siblings.Count) return;
            document.MoveLayer(active.Id, at);
        }
    }
}
