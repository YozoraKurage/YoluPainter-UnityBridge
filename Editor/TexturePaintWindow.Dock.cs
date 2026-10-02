using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>右のドック: テクスチャセット（モデル・マテリアルのスロット・チャンネル）、レイヤー、プロパティ（今のツールと層の詳しい設定）。</summary>
    public sealed partial class TexturePaintWindow
    {
        const float LayerRowHeight = 30, PanelHeaderHeight = 24, LayerToolbarHeight = 30;

        void DrawDock()
        {
            var r = dockRect;
            PaintGui.Fill(r, PaintTheme.PanelBg);
            PaintGui.VLine(r.x, r.y, r.yMax, PaintTheme.Border);
            float y = r.y;
            // カラー（中身の高さはチャンネルで決まる）
            var colorHead = new Rect(r.x + 1, y, r.width - 1, PanelHeaderHeight); y += PanelHeaderHeight;
            dockColorOpen = PaintGui.SectionHeader(colorHead, L.Tr("Color"), dockColorOpen, "palette");
            if (dockColorOpen) { float h = ColorPanelHeight; DrawColorPanel(new Rect(r.x + 1, y, r.width - 1, h)); y += h; }
            // テクスチャセット（中身の高さは決まっている）
            var head = new Rect(r.x + 1, y, r.width - 1, PanelHeaderHeight); y += PanelHeaderHeight;
            dockTextureSetOpen = PaintGui.SectionHeader(head, L.Tr("Texture Set"), dockTextureSetOpen, "deployed_code");
            if (dockTextureSetOpen) { float h = TextureSetHeight; DrawTextureSetPanel(new Rect(r.x + 1, y, r.width - 1, h)); y += h; }
            // 残りをレイヤーとプロパティで分ける
            float headers = PanelHeaderHeight * 2, rest = r.yMax - y - headers;
            // レイヤーとプロパティで残りを半分ずつ（レイヤーは少なくとも 3 行ほど）
            float layersH = dockLayersOpen ? (dockPropertiesOpen ? Mathf.Clamp(rest * .5f, Mathf.Min(140, rest), rest) : rest) : 0;
            head = new Rect(r.x + 1, y, r.width - 1, PanelHeaderHeight); y += PanelHeaderHeight;
            dockLayersOpen = PaintGui.SectionHeader(head, L.Tr("Layers"), dockLayersOpen, "layers");
            if (dockLayersOpen) { DrawLayersPanel(new Rect(r.x + 1, y, r.width - 1, layersH)); y += layersH; }
            head = new Rect(r.x + 1, y, r.width - 1, PanelHeaderHeight); y += PanelHeaderHeight;
            dockPropertiesOpen = PaintGui.SectionHeader(head, L.Tr("Properties"), dockPropertiesOpen, "tune");
            if (dockPropertiesOpen && r.yMax - y > 20) DrawPropertiesPanel(new Rect(r.x + 1, y, r.width - 1, r.yMax - y));
        }

        // ───────── テクスチャセット ─────────

        static readonly PaintChannel[] Channels = (PaintChannel[])Enum.GetValues(typeof(PaintChannel));
        const int ChannelColumns = 3; const float ChannelChipHeight = 26;
        float TextureSetHeight => 8 + 26 + 4 + 26 + 8 + Mathf.Ceil(Channels.Length / (float)ChannelColumns) * (ChannelChipHeight + 4) + 4;
        static string ChannelIcon(PaintChannel c)
        {
            switch (c)
            {
                case PaintChannel.Color: return "palette";
                case PaintChannel.Roughness: return "blur_on";
                case PaintChannel.Metallic: return "contrast";
                case PaintChannel.Height: return "texture";
                case PaintChannel.Normal: return "3d_rotation";
                case PaintChannel.Emission: return "light_mode";
                default: return "layers";
            }
        }

        void DrawTextureSetPanel(Rect r)
        {
            var rows = new UiRows(r, 8);
            // モデル（ドロップでも、ピッカーでも）
            var row = rows.Row(26);
            PaintGui.Text(new Rect(row.x, row.y, 64, row.height), L.Tr("Model"), PaintTheme.Label);
            var box = new Rect(row.x + 64, row.y, row.width - 64 - 30, row.height);
            PaintGui.Rounded(box, PaintTheme.ControlBg, 3); PaintGui.Outline(box, PaintTheme.Border, 1, 3);
            PaintGui.Icon(new Rect(box.x + 2, box.y, 20, box.height), "deployed_code", PaintTheme.TextDim, 15);
            string modelName = model != null ? model.name : preview.HasModel ? L.Tr("Demo cube") : L.Tr("None (drop a model here)");
            PaintGui.Text(new Rect(box.x + 24, box.y, box.width - 28, box.height), modelName, PaintTheme.Label, model != null || preview.HasModel ? PaintTheme.Text : PaintTheme.TextDim);
            HandleModelDrop(box);
            if (PaintGui.IconButton(new Rect(box.xMax + 4, row.y, 26, row.height), "folder_open", L.Tr("Choose a model…"), false, true, 17))
                EditorGUIUtility.ShowObjectPicker<GameObject>(model, false, "t:Model t:Prefab", ModelPickerId);
            // マテリアルのスロット（= テクスチャセット）
            row = rows.Row(26);
            int slots = Mathf.Max(1, preview.MaterialSlotCount);
            PaintGui.Dropdown(row, L.Tr("Material"), SlotName(materialSlot), at =>
            {
                var menu = new GenericMenu();
                for (int i = 0; i < slots; i++) { int s = i; menu.AddItem(new GUIContent(SlotName(s)), s == materialSlot, () => { materialSlot = s; repaintPixels = true; }); }
                menu.DropDown(at);
            }, L.Tr("The material slot this document paints"), preview.HasModel, 64);
            rows.Space(4);
            // チャンネル（3 列のボタン。使っているチャンネルには青い点）
            var used = new HashSet<PaintChannel>(YlpContent.UsedChannels(document));
            for (int start = 0; start < Channels.Length; start += ChannelColumns)
            {
                var cells = UiRows.Split(rows.Row(ChannelChipHeight), ChannelColumns, 4);
                for (int k = 0; k < ChannelColumns && start + k < Channels.Length; k++)
                {
                    var c = Channels[start + k]; var cell = cells[k];
                    bool selected = c == channel, hover = cell.Contains(Event.current.mousePosition) && GUI.enabled;
                    PaintGui.Rounded(cell, selected ? PaintTheme.AccentDim : hover ? PaintTheme.ControlHover : PaintTheme.ControlBg, 4);
                    PaintGui.Icon(new Rect(cell.x + 3, cell.y, 18, cell.height), ChannelIcon(c), selected ? Color.white : PaintTheme.TextDim, 14);
                    PaintGui.Text(new Rect(cell.x + 21, cell.y, cell.width - 27, cell.height), L.Tr(c.ToString()), PaintTheme.LabelSmall, selected ? Color.white : PaintTheme.Text);
                    if (used.Contains(c)) PaintGui.Rounded(new Rect(cell.xMax - 8, cell.y + 4, 5, 5), selected ? Color.white : PaintTheme.Accent, 2.5f);
                    PaintGui.Tooltip(cell, L.Tr(c.ToString()));
                    if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover) { SetChannel(c); Event.current.Use(); }
                }
            }
        }

        string SlotName(int slot)
        {
            var material = preview.HasModel ? preview.SourceMaterial(slot) : null;
            return slot + (material != null ? ": " + material.name : "");
        }

        internal void SetChannel(PaintChannel next)
        {
            if (next == channel) return;
            channel = next; repaintPixels = true;
            message = channel == PaintChannel.Normal ? L.Tr("Normal channel: layers composite as unit normals (Overlay adds detail, other modes replace).") : L.Tr("Painting only the selected channel; the other channels stay as they are.");
        }

        void HandleModelDrop(Rect box)
        {
            var e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !box.Contains(e.mousePosition)) return;
            var dropped = DragAndDrop.objectReferences.OfType<GameObject>().FirstOrDefault();
            if (dropped == null) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            if (e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); SetModel(dropped); }
            e.Use();
        }

        // ───────── レイヤー ─────────

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

    }
}
