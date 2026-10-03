using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>レイヤーのパネル: 合成モードと不透明度、ロック、行（サムネイル・マスク・名前の変更・右クリック・Ctrl/Shift での複数選択）と
    /// その下の効果の段の行（Panels/TexturePaintWindow.EffectRows.cs）、ドラッグでの並べ替え、下のツールバー、レイヤーの操作（メニューと共有）。</summary>
    public sealed partial class TexturePaintWindow
    {
        const float LayerRowHeight = 30, LayerToolbarHeight = 30;
        /// <summary>レイヤーのパネルの上の行の左の、今のチャンネルに層の自分の合成モードと不透明度を持たせる切り替えの幅。</summary>
        const float ChannelBlendToggleWidth = 18;
        /// <summary>ロックの切り替えを合成モードと不透明度の行の右に並べられる幅か（既定のドックの幅 300 では並べ、一覧の高さを減らさない。
        /// 狭いドックでは 2 行目に出す）。</summary>
        static bool LockRowInline(float panelWidth) => panelWidth - 2 * PaintTheme.Padding >= 270;
        /// <summary>パネルの上の端から一覧の上の端まで（合成モードと不透明度の行と、狭いときのロックの行）。</summary>
        internal static float LayerListOffsetFor(float panelWidth) => LockRowInline(panelWidth) ? 6 + 24 + 6 : 6 + 24 + 4 + LockRowHeight + 4;

        /// <summary>試験用: 最後の描画での一覧（"list"）・行（"row." + ID）・目（"eye." + ID）・ロックの切り替え（"lock." + 種類）の画面の矩形。</summary>
        internal readonly Dictionary<string, Rect> LayerPanelScreenRects = new Dictionary<string, Rect>();
        Rect PanelSpot(string id, Rect r) { if (Event.current.type == EventType.Repaint) LayerPanelScreenRects[id] = GUIUtility.GUIToScreenRect(r); return r; }

        void DrawLayersPanel(Rect r)
        {
            if (Event.current.type == EventType.Repaint) LayerPanelScreenRects.Clear();
            var active = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            // 上: 合成モードと不透明度（Photoshop の配置）
            var top = new Rect(r.x + PaintTheme.Padding, r.y + 6, r.width - 2 * PaintTheme.Padding, 24);
            bool lockInline = LockRowInline(r.width);
            var lockRect = lockInline ? new Rect(top.xMax - LockButtonsWidth, top.y, LockButtonsWidth, top.height) : new Rect(top.x, top.yMax + 4, top.width, LockRowHeight);
            if (lockInline) top.width -= LockButtonsWidth + 6;
            // 左端にチャンネルごとの合成の切り替え。残りを合成モードと不透明度で分ける（ロックを並べるときは不透明度（名前と値を出す）に広く
            // 取る。合成モードの名前は前から長いものは詰めて出している）
            var own = new Rect(top.x, top.y, ChannelBlendToggleWidth, top.height);
            top = new Rect(top.x + ChannelBlendToggleWidth + 2, top.y, top.width - ChannelBlendToggleWidth - 2, top.height);
            var halves = !lockInline ? UiRows.Split(top, 2, 6)
                : new[] { new Rect(top.x, top.y, Mathf.Floor((top.width - 6) * .42f), top.height), new Rect(top.x + Mathf.Floor((top.width - 6) * .42f) + 6, top.y, top.width - Mathf.Floor((top.width - 6) * .42f) - 6, top.height) };
            if (active != null)
            {
                var modes = ((LayerBlendMode[])Enum.GetValues(typeof(LayerBlendMode))).Where(m => active.IsGroup || m != LayerBlendMode.PassThrough).ToArray();
                bool settingsLocked = (document.EffectiveLocks(active.Id) & LayerLocks.All) != 0; // すべてのロックでは合成モードと不透明度も変えない
                // チャンネルごとの合成（Substance と同じく、今のチャンネルの合成モードと不透明度を見せる）: 左の切り替えがオンなら、このチャンネル
                // だけの値を見せて変える。オフなら層の値（自分の値を持たないチャンネル全部に効く）
                PanelSpot("channelBlend", own);
                bool perChannel = active.ChannelBlends.ContainsKey(channel);
                if (PaintGui.IconButton(own, ChannelIcon(channel), perChannel ? L.Tr("Blend mode and opacity for {0} only. Click to use the layer's again.", L.Tr(channel.ToString()))
                        : L.Tr("Blend mode and opacity of the layer, shared by every channel without its own. Click to give {0} its own.", L.Tr(channel.ToString())), perChannel, !settingsLocked, 15))
                    TryAction(() => SetOwnChannelBlend(active.Id, !perChannel));
                var mode = active.BlendModeIn(channel); double shownOpacity = active.OpacityIn(channel);
                PaintGui.EnumDropdown(PanelSpot("blendMode", halves[0]), null, mode, modes, m => L.TrIn("blend mode", BlendName(m)),
                    m => TryAction(() => { if (perChannel) document.SetChannelBlendMode(active.Id, channel, m); else document.SetLayerBlendMode(active.Id, m); }), !settingsLocked);
                float opacity = PaintGui.Slider(PanelSpot("opacity", halves[1]), L.Tr("Opacity"), (float)shownOpacity * 100, 0, 100, "0", "%", null, !settingsLocked) / 100;
                if (Math.Abs(opacity - shownOpacity) > .00001)
                    TryAction(() => { if (perChannel) document.SetChannelOpacity(active.Id, channel, opacity, coalesce: true); else document.SetLayerOpacity(active.Id, opacity, coalesce: true); });
                if (perChannel) PaintGui.Outline(new Rect(halves[0].x - 2, halves[0].y - 2, halves[1].xMax - halves[0].x + 4, halves[0].height + 4), PaintTheme.Accent, 1, 4); // このチャンネルだけの値を見せている印
            }
            // ロック（選んでいる層の全部に効く）
            DrawLockRow(lockRect, !lockInline);
            // 一覧
            if (Event.current.type == EventType.Repaint) ForgetStaleThumbnails();
            float listTop = LayerListOffsetFor(r.width);
            var list = PanelSpot("list", new Rect(r.x, r.y + listTop, r.width, r.height - listTop - LayerToolbarHeight));
            PaintGui.Fill(list, PaintTheme.ControlBg);
            var listRows = LayerListRows(); // 層の行と、その下の効果の段の行（Panels/TexturePaintWindow.EffectRows.cs）
            float content = LayerListHeight(listRows);
            var view = new Rect(0, 0, PaintGui.ScrollContentWidth(list, content), content);
            layerScroll.y = Mathf.Clamp(layerScroll.y, 0, Mathf.Max(0, content - list.height));
            bool overList = list.Contains(Event.current.mousePosition);
            PanelSpot("scrollbar", PaintGui.ScrollTrack(list));
            if (PaintGui.Scrollbar(list, ref layerScroll, content, 12)) Repaint();
            PaintGui.BeginScroll(list, layerScroll);
            foreach (var row in listRows)
            {
                var at = new Rect(0, row.Y, view.width, row.Height);
                if (row.IsEffect) DrawEffectRow(at, row); else if (row.IsAnchor) DrawAnchorListRow(at, row); else DrawLayerRow(at, row.Layer, row.Index);
            }
            HandleLayerDrag(view.width);
            HandleSmartDrop(view.width, overList); // アセットのパネルからのスマートマテリアル・スマートマスク（Project/TexturePaintWindow.SmartMaterials.cs）
            PaintGui.EndScroll();
            LayerBlankContext(list, content, layerScroll.y, view.width);
            // 下: 操作
            var bar = new Rect(r.x, list.yMax, r.width, LayerToolbarHeight);
            PaintGui.Fill(bar, PaintTheme.PanelHeader);
            float x = bar.x + 4; Rect B() { var b = new Rect(x, bar.y + 3, 26, 24); x += 27; return b; }
            if (PaintGui.IconButton(B(), "add", L.Tr("New Layer"), false, true, 18)) TryAction(AddPaintLayer);
            if (PaintGui.IconButton(B(), "format_color_fill", L.Tr("New Fill Layer"), false, true, 17)) TryAction(AddFillLayerHere);
            if (PaintGui.IconButton(B(), "tune", L.Tr("New Adjustment Layer"), false, true, 17))
            {
                var menu = new PaintMenu();
                foreach (var (label, make) in AdjustmentMenu) { var mk = make; var lb = label; menu.AddItem(new GUIContent(L.Tr(lb)), false, () => TryAction(() => selectedLayer = document.AddAdjustmentLayer(L.Tr(lb), mk(), above: AboveSelected()).Id)); }
                menu.ShowAsContext();
            }
            if (PaintGui.IconButton(B(), "folder", L.Tr("Group Layers"), false, active != null, 17)) TryAction(GroupSelectedLayers);
            bool hasMask = active?.Mask != null;
            if (PaintGui.IconButton(B(), "vignette", hasMask ? L.Tr("Edit Layer Mask") : L.Tr("Add Layer Mask"), hasMask && editMask, active != null, 17))
                TryAction(() => { if (!hasMask) { document.AddLayerMask(selectedLayer); editMask = true; } else editMask = !editMask; });
            if (PaintGui.IconButton(B(), "auto_awesome", L.Tr("Add Filter"), false, active != null, 17)) { var menu = new PaintMenu(); FilterMenuItems(menu); menu.ShowAsContext(); }
            x = bar.xMax - 4 - 27 * 3;
            if (PaintGui.IconButton(B(), "expand_less", L.Tr("Move Layer Up"), false, active != null, 18)) TryAction(() => MoveSelectedLayer(+1));
            if (PaintGui.IconButton(B(), "expand_more", L.Tr("Move Layer Down"), false, active != null, 18)) TryAction(() => MoveSelectedLayer(-1));
            if (PaintGui.IconButton(B(), "delete", L.Tr(SelectedLayers.Count > 1 ? "Delete Layers" : "Delete Layer"), false, document.Layers.Count > 1, 17)) TryAction(DeleteSelectedLayer);
        }

        void DrawLayerRow(Rect r, PaintLayer layer, int index)
        {
            var e = Event.current; bool selected = layer.Id == selectedLayer, inSelection = IsLayerSelected(layer.Id), hover = r.Contains(e.mousePosition);
            PanelSpot("row." + layer.Id, r);
            // 選んでいる層は薄い色、描く先はそれに左の帯
            if (inSelection) PaintGui.Fill(r, PaintTheme.AccentSoft);
            if (selected) PaintGui.Fill(new Rect(r.x, r.y, 3, r.height), PaintTheme.Accent);
            else if (hover && !inSelection) PaintGui.Fill(r, PaintTheme.ControlHover);
            PaintGui.HLine(r.x, r.xMax, r.yMax - 1, PaintTheme.Border);
            // 目
            var eye = PanelSpot("eye." + layer.Id, new Rect(r.x + 4, r.y + 3, 24, r.height - 6));
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
            if (layer.Anchor != null) AnchorBadge(new Rect(thumb.x - 2, thumb.y - 2, 12, 12), layer.Anchor); // Anchor の印
            x = thumb.xMax + 6;
            if (layer.Mask != null)
            {
                var maskBox = PanelSpot("mask." + layer.Id, new Rect(x, r.y + 4, r.height - 8, r.height - 8));
                bool editing = selected && editMask;
                DrawThumbnail(maskBox, MaskThumbnail(layer));
                if (layer.Mask.Anchor != null) AnchorBadge(new Rect(maskBox.x - 2, maskBox.y - 2, 12, 12), layer.Mask.Anchor);
                if (editing) PaintGui.Outline(new Rect(maskBox.x - 2, maskBox.y - 2, maskBox.width + 4, maskBox.height + 4), PaintTheme.Accent, 2, 2);
                PaintGui.Tooltip(maskBox, L.Tr("Layer mask (click to paint on it)") + "\n" + L.Tr("Right-click for the mask's settings."));
                if (e.type == EventType.MouseDown && e.button == 0 && maskBox.Contains(e.mousePosition) && GUI.enabled) { SelectSingleLayer(layer.Id); selectedFilter = Guid.Empty; editMask = !editing; e.Use(); }
                if (e.type == EventType.ContextClick && maskBox.Contains(e.mousePosition) && GUI.enabled) { var menu = new PaintMenu(); MaskMenu(menu, layer); menu.ShowAsContext(); e.Use(); }
                x = maskBox.xMax + 6;
            }
            // 右端の印（効果・チャンネル無し、その左にロック）
            bool mark = !layer.IsGroup && layer.Kind == LayerKind.Raster && !layer.IsChannelEnabled(channel); // 効果は層の下の行に出る
            bool locked = DrawRowLock(new Rect(r.xMax - (mark ? 44 : 24), r.y, 20, r.height), layer);
            // 名前（ダブルクリックで変える）
            var nameRect = new Rect(x, r.y + 4, r.xMax - x - 26 - (locked ? 20 : 0), r.height - 8);
            if (renamingLayer == layer.Id)
            {
                string next = PaintGui.TextField(nameRect, layer.Name);
                if (next != layer.Name) { TryAction(() => document.SetLayerName(layer.Id, next)); renamingLayer = Guid.Empty; }
                if (e.type == EventType.MouseDown && !nameRect.Contains(e.mousePosition)) renamingLayer = Guid.Empty;
            }
            else PaintGui.Text(nameRect, layer.Name, PaintTheme.Label, layer.Visible ? (inSelection ? Color.white : PaintTheme.Text) : PaintTheme.TextDim);
            // 右端の印
            if (!layer.IsGroup && layer.Kind == LayerKind.Raster && !layer.IsChannelEnabled(channel)) { PaintGui.Icon(new Rect(r.xMax - 24, r.y, 20, r.height), "link_off", PaintTheme.TextDisabled, 13); PaintGui.Tooltip(new Rect(r.xMax - 24, r.y, 20, r.height), L.Tr("This layer has no pixels in the selected channel yet")); }
            // 選ぶ（Ctrl/Cmd で足し引き、Shift で範囲）・ダブルクリックで名前
            if (e.type == EventType.MouseDown && e.button == 0 && hover && GUI.enabled && renamingLayer != layer.Id)
            {
                if (e.control || e.command || e.shift)
                {
                    if (e.shift) SelectLayerRange(layer.Id, e.control || e.command); else ToggleLayerSelected(layer.Id);
                    lastLayerClicked = Guid.Empty; renamingLayer = Guid.Empty;
                    e.Use(); Repaint(); return;
                }
                bool twice = lastLayerClicked == layer.Id && EditorApplication.timeSinceStartup - lastLayerClick < .4;
                selectedFilter = Guid.Empty; // 層の行を押したら、プロパティの欄はその層（かツール）に戻る
                // 複数選択の中の行を押したら、ドラッグで全部を動かせるように選択を残し、ドラッグにならなければ離したところでその 1 つにする
                if (inSelection && MultiSelectionValid) { if (!selected) SelectLayers(SelectedLayers, layer.Id); layerCollapsePending = layer.Id; }
                else SelectSingleLayer(layer.Id);
                lastLayerClicked = layer.Id; lastLayerClick = EditorApplication.timeSinceStartup;
                if (twice && nameRect.Contains(e.mousePosition)) { renamingLayer = layer.Id; SelectSingleLayer(layer.Id); layerCollapsePending = Guid.Empty; }
                if (!selected) editMask = false;
                layerDragCandidate = layer.Id; layerDragStart = e.mousePosition; layerDragging = false;
                e.Use(); Repaint();
            }
            if (e.type == EventType.ContextClick && hover && GUI.enabled)
            {
                if (!inSelection) SelectSingleLayer(layer.Id); else if (!selected) SelectLayers(SelectedLayers, layer.Id);
                var menu = new PaintMenu(); LayerMenu(menu); menu.ShowAsContext(); e.Use();
            }
        }

        // ───────── ドラッグでの並べ替え ─────────
        Guid layerDragCandidate; Vector2 layerDragStart; bool layerDragging;

        /// <summary>ドラッグで落とす先: 層の行と行の間（gap 番目の層の行のすぐ上。gap が層の数なら一番下）か、グループの行の中ほど（その中の一番上）。
        /// 効果の段の行の上は、その層の下（効果の段のまとまりの後）。</summary>
        internal (int gap, PaintLayer into) LayerDropTargetForTests(float y) => LayerDropTarget(y);
        (int gap, PaintLayer into) LayerDropTarget(float y)
        {
            var rows = LayerListRows(); int n = document.Layers.Count;
            if (y < 0 || n == 0) return (0, null);
            var hit = LayerListRowAt(rows, y);
            if (hit == null) return (n, null);
            var row = hit.Value; int rowIndex = n - 1 - row.Index; // 層の行の上からの番号
            if (row.IsChild) return (rowIndex + 1, null);
            float within = (y - row.Y) / row.Height;
            if (row.Layer.IsGroup && within > .3f && within < .7f) return (-1, row.Layer);
            return (Mathf.Clamp(within < .5f ? rowIndex : rowIndex + 1, 0, n), null);
        }

        void HandleLayerDrag(float width)
        {
            var e = Event.current;
            if (layerDragCandidate == Guid.Empty) return;
            if (e.rawType == EventType.MouseUp)
            {
                if (layerDragging)
                {
                    var target = LayerDropTarget(e.mousePosition.y); var id = layerDragCandidate;
                    var ids = IsLayerSelected(id) ? SelectedLayers : new[] { id };
                    TryAction(() => DropLayers(ids, target.gap, target.into));
                }
                else if (layerCollapsePending != Guid.Empty && layerCollapsePending == selectedLayer) SelectSingleLayer(layerCollapsePending);
                layerDragCandidate = Guid.Empty; layerDragging = false; layerCollapsePending = Guid.Empty; Repaint();
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
                if (target.into != null) PaintGui.Outline(new Rect(1, LayerRowY(target.into) + 1, width - 2, LayerRowHeight - 2), PaintTheme.Accent, 2, 3);
                else PaintGui.Fill(new Rect(4, LayerGapY(target.gap) - 1, width - 8, 2), PaintTheme.Accent);
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


        /// <summary>今のチャンネルに層の自分の合成モードと不透明度を持たせる（今の値から始める）か、層の値に戻す（1 回の Undo）。</summary>
        internal void SetOwnChannelBlend(Guid layerId, bool own)
        {
            var layer = document.GetLayer(layerId);
            document.SetChannelBlend(layerId, channel, own ? new ChannelBlend(layer.BlendMode, layer.Opacity) : default);
            message = own ? L.Tr("{0} now has its own blend mode and opacity on this layer.", L.Tr(channel.ToString())) : L.Tr("{0} uses the layer's blend mode and opacity again.", L.Tr(channel.ToString()));
        }

        Guid? AboveSelected() => document.Layers.Any(l => l.Id == selectedLayer) ? selectedLayer : (Guid?)null;
        void AddPaintLayer() => selectedLayer = document.AddLayer(L.Tr("Layer") + " " + (document.Layers.Count + 1), above: AboveSelected()).Id;
        void AddFillLayerHere() => selectedLayer = document.AddFillLayer(L.Tr("Fill") + " " + (document.Layers.Count + 1), new Dictionary<PaintChannel, Rgba32> { { channel, GetBrush().Color } }, above: AboveSelected()).Id;
        static readonly (string label, Func<AdjustmentSettings> make)[] AdjustmentMenu =
            { ("Invert", AdjustmentSettings.Invert), ("Levels", () => AdjustmentSettings.Levels()), ("Hue / Saturation", () => AdjustmentSettings.HueSaturation()) };
        void UngroupSelected()
        {
            var first = document.ChildrenOf(selectedLayer).LastOrDefault();
            document.Ungroup(selectedLayer);
            selectedLayer = first != null ? first.Id : (document.Layers.Count > 0 ? document.Layers[document.Layers.Count - 1].Id : Guid.Empty);
        }
    }
}
