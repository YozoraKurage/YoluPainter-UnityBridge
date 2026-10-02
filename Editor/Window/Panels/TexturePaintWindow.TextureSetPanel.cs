using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>テクスチャセットのパネル: モデル（ドロップ・選択）、テクスチャセットの一覧（名前・スロット・解像度・Color の小さなサムネイル。
    /// 今のセットを強調し、押すと切り替える）、ベイクとプロジェクト設定の口、今のセットのチャンネル。ドックの別のウィンドウでも同じ描画。</summary>
    public sealed partial class TexturePaintWindow
    {
        static readonly PaintChannel[] Channels = (PaintChannel[])Enum.GetValues(typeof(PaintChannel));
        const int ChannelColumns = 3; const float ChannelChipHeight = 26;
        const float SetRowHeight = 30; const int SetRowsShown = 4;
        Vector2 setListScroll;
        /// <summary>パネルの部品の画面上の矩形（Repaint のたびに覚え直す。テストが本物のマウスの入力で押すため。プロパティの欄の
        /// <see cref="LayerControlScreenRects"/> とは別に持つ。あちらは欄を描くたびに空にする）。</summary>
        internal readonly Dictionary<string, Rect> TextureSetScreenRects = new Dictionary<string, Rect>();
        Rect SetSpot(string id, Rect r)
        {
            if (Event.current.type == EventType.Repaint) TextureSetScreenRects[id] = GUIUtility.GUIToScreenRect(r);
            return r;
        }
        float SetListHeight => Mathf.Min(Mathf.Max(1, textureSets.Count), SetRowsShown) * SetRowHeight;
        float TextureSetHeight => 8 + 26 + 6 + SetListHeight + 6 + 26 + 8 + Mathf.Ceil(Channels.Length / (float)ChannelColumns) * (ChannelChipHeight + 4) + 4;
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
            if (Event.current.type == EventType.Repaint) TextureSetScreenRects.Clear();
            var rows = new UiRows(r, 8);
            // モデル（ドロップでも、ピッカーでも）
            var row = rows.Row(26, 6);
            PaintGui.Text(new Rect(row.x, row.y, 64, row.height), L.Tr("Model"), PaintTheme.Label);
            var box = new Rect(row.x + 64, row.y, row.width - 64 - 30, row.height);
            PaintGui.Rounded(box, PaintTheme.ControlBg, 3); PaintGui.Outline(box, PaintTheme.Border, 1, 3);
            PaintGui.Icon(new Rect(box.x + 2, box.y, 20, box.height), "deployed_code", PaintTheme.TextDim, 15);
            string modelName = model != null ? model.name : preview.HasModel ? L.Tr("Demo cube") : L.Tr("None (drop a model here)");
            PaintGui.Text(new Rect(box.x + 24, box.y, box.width - 28, box.height), PaintGui.Fit(modelName, box.width - 28, PaintTheme.Label, false), PaintTheme.Label, model != null || preview.HasModel ? PaintTheme.Text : PaintTheme.TextDim);
            HandleModelDrop(box);
            if (PaintGui.IconButton(new Rect(box.xMax + 4, row.y, 26, row.height), "folder_open", L.Tr("Choose a model…"), false, true, 17))
                EditorGUIUtility.ShowObjectPicker<GameObject>(model, false, "t:Model t:Prefab", ModelPickerId);
            // テクスチャセットの一覧
            DrawTextureSetList(rows.Row(SetListHeight, 6));
            // ベイクとプロジェクト設定
            row = rows.Row(26, 8);
            var bakeRect = SetSpot("textureSet.bake", new Rect(row.x, row.y, row.width - 30, row.height));
            string bakeText = L.Tr("Bake Mesh Maps…"), bakeTip = L.Tr("Open the bake window: check the maps, set them up and bake them from the loaded model");
            // 狭いドックではアイコンを外して文字を詰める
            bool bakeClicked = PaintGui.TextWidth(bakeText, PaintTheme.Label) + 22 + 12 <= bakeRect.width
                ? PaintGui.Button(bakeRect, bakeText, false, GUI.enabled && stroke == null, bakeTip, "local_fire_department")
                : PaintGui.FitButton(bakeRect, bakeText, false, GUI.enabled && stroke == null, bakeTip);
            if (bakeClicked) TryAction(() => OpenMeshBakeWindow());
            if (PaintGui.IconButton(SetSpot("textureSet.configure", new Rect(row.xMax - 26, row.y, 26, row.height)), "settings", L.Tr("Project Configuration: add, remove or rename texture sets, change their material slots or the model"), false, GUI.enabled && stroke == null, 17))
                TryAction(ProjectConfigurationDialog);
            // チャンネル（3 列のボタン。今のセットで使っているチャンネルには青い点）
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

        /// <summary>テクスチャセットの行: サムネイル・名前・スロットと解像度。今のセットを強調し、押すと切り替える。多ければスクロール。</summary>
        void DrawTextureSetList(Rect list)
        {
            SyncCurrentSet();
            var e = Event.current;
            float content = textureSets.Count * SetRowHeight;
            bool overflow = content > list.height + .5f;
            setListScroll.y = Mathf.Clamp(setListScroll.y, 0, Mathf.Max(0, content - list.height));
            PaintGui.Rounded(list, PaintTheme.MenuBg, 4);
            TextureSet clicked = null;
            PaintGui.BeginScroll(list, setListScroll);
            for (int i = 0; i < textureSets.Count; i++)
            {
                var set = textureSets[i];
                var row = new Rect(0, i * SetRowHeight, list.width - (overflow ? 8 : 0), SetRowHeight);
                bool current = set == currentSet, hover = GUI.enabled && row.Contains(e.mousePosition) && new Rect(0, setListScroll.y, list.width, list.height).Contains(e.mousePosition);
                if (current) PaintGui.Rounded(new Rect(row.x + 2, row.y + 2, row.width - 4, row.height - 4), PaintTheme.AccentDim, 4);
                else if (hover) PaintGui.Rounded(new Rect(row.x + 2, row.y + 2, row.width - 4, row.height - 4), PaintTheme.ControlHover, 4);
                var thumb = new Rect(row.x + 5, row.y + 4, 22, 22);
                if (e.type == EventType.Repaint) DrawThumbnail(thumb, SetThumbnail(set));
                bool missing = preview.HasModel && set.MaterialSlot >= preview.MaterialSlotCount;
                string detail = L.Tr("slot {0}", set.MaterialSlot) + " · " + set.Document.Width + (set.Document.Width == set.Document.Height ? "²" : " × " + set.Document.Height);
                float x = thumb.xMax + 6, right = row.xMax - 6;
                float detailWidth = PaintGui.TextWidth(detail, PaintTheme.LabelSmall);
                bool showDetail = right - x > detailWidth + 60;
                if (showDetail) { PaintGui.Text(new Rect(right - detailWidth, row.y, detailWidth, row.height), detail, PaintTheme.LabelSmall, current ? Color.white : PaintTheme.TextDim); right -= detailWidth + 6; }
                if (missing) { PaintGui.Icon(new Rect(right - 16, row.y, 16, row.height), "warning", PaintTheme.Warning, 14); right -= 18; }
                var style = current ? PaintTheme.LabelBold : PaintTheme.Label;
                PaintGui.Text(new Rect(x, row.y, Mathf.Max(0, right - x), row.height), PaintGui.Fit(set.Name, Mathf.Max(0, right - x), style, false), style, current ? Color.white : PaintTheme.Text);
                string tip = set.Name + "\n" + L.Tr("Material slot") + ": " + SlotName(set.MaterialSlot) + "\n" + set.Document.Width + " × " + set.Document.Height
                    + (missing ? "\n" + L.Tr("The loaded model has no material slot {0}; this texture set is not shown in 3D. Change its slot in File ▸ Project Configuration.", set.MaterialSlot) : "")
                    + (current ? "" : "\n" + L.Tr("Click to paint this texture set."));
                PaintGui.Tooltip(row, tip);
                if (e.type == EventType.MouseDown && e.button == 0 && hover && !current) clicked = set;
            }
            PaintGui.EndScroll();
            for (int i = 0; i < textureSets.Count; i++)
            {
                var visible = new Rect(list.x, list.y + i * SetRowHeight - setListScroll.y, list.width, SetRowHeight);
                if (visible.yMax > list.y && visible.y < list.yMax) SetSpot("textureSet." + i, Rect.MinMaxRect(visible.x, Mathf.Max(visible.y, list.y), visible.xMax, Mathf.Min(visible.yMax, list.yMax)));
            }
            if (e.type == EventType.ScrollWheel && list.Contains(e.mousePosition) && overflow)
            { setListScroll.y = Mathf.Clamp(setListScroll.y + e.delta.y * 12, 0, content - list.height); e.Use(); Repaint(); }
            if (overflow) PaintGui.Rounded(new Rect(list.xMax - 6, list.y + list.height * setListScroll.y / content, 4, list.height * list.height / content), PaintTheme.ControlActive, 2);
            if (clicked != null) { e.Use(); TryAction(() => SwitchTextureSet(clicked.Id)); }
        }

        string SlotName(int slot)
        {
            var material = preview.HasModel ? preview.SourceMaterial(slot) : null;
            return slot + (material != null ? ": " + material.name : preview.HasModel && slot >= preview.MaterialSlotCount ? " (" + L.Tr("not in this model") + ")" : "");
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
