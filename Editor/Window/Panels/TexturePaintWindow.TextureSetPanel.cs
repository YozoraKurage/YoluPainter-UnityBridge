using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>テクスチャセットのパネル: モデル（ドロップ・選択）、マテリアルのスロット、チャンネル。</summary>
    public sealed partial class TexturePaintWindow
    {
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
    }
}
