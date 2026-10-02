using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>ドックの描画: 左右の列、パネルの見出し（畳む・ドラッグで動かす）、境目（高さの比・列の幅）。配置の型は DockLayout.cs。</summary>
    public sealed partial class TexturePaintWindow
    {
        const float PanelHeaderHeight = 24;

        /// <summary>ドックのパネル。FixedHeight が null なら残りを高さの比で分け合う。</summary>
        sealed class DockPanel { public string Id, Title, Icon; public Func<float> FixedHeight; public float MinHeight = 60; public Action<Rect> Draw; }

        DockLayout dockLayout;
        DockPanel[] dockPanels;
        Rect leftDockRect;
        // パネルの見出しのドラッグ
        string panelDragCandidate; Vector2 panelDragStart; bool panelDragging;
        // 境目のドラッグ（高さの比・列の幅）
        string splitterUpper, splitterLower; bool splitterLeft; float splitterStartY, splitterUpperH, splitterLowerH;
        int widthDragColumn; float widthDragStartX, widthDragStartWidth; // 1 = 左、2 = 右
        float colorColumnHeight = 800;

        DockLayout Layout => dockLayout ?? (dockLayout = DockLayoutStore.Load().Normalized());
        internal DockLayout DockLayoutForTests => Layout;

        DockPanel[] Panels => dockPanels ?? (dockPanels = new[]
        {
            new DockPanel { Id = "color", Title = "Color", Icon = "palette", FixedHeight = () => ColorPanelHeight, Draw = DrawColorPanel },
            new DockPanel { Id = "textureSet", Title = "Texture Set", Icon = "deployed_code", FixedHeight = () => TextureSetHeight, Draw = DrawTextureSetPanel },
            new DockPanel { Id = "layers", Title = "Layers", Icon = "layers", MinHeight = 140, Draw = DrawLayersPanel },
            new DockPanel { Id = "properties", Title = "Properties", Icon = "tune", MinHeight = 80, Draw = DrawPropertiesPanel },
        });
        DockPanel Panel(string id) => Panels.First(p => p.Id == id);

        /// <summary>列の幅（パネルが無い列は 0）。窓の幅の 45% までに収める。</summary>
        float DockWidth(bool left, float windowWidth)
        {
            var ids = left ? Layout.left : Layout.right;
            if (ids.Count == 0) return 0;
            return Mathf.Min(left ? Layout.leftWidth : Layout.rightWidth, Mathf.Max(DockLayout.MinWidth, windowWidth * .45f));
        }

        void SaveDockLayout() => DockLayoutStore.Save(Layout);
        internal void ResetDockLayout() { DockLayoutStore.Reset(); dockLayout = DockLayout.Default(); Repaint(); }

        /// <summary>両方の列を描く（パネルのドラッグの落とし先も）。</summary>
        void DrawDocks()
        {
            if (leftDockRect.width > 0) DrawDockColumn(leftDockRect, Layout.left, true);
            if (dockRect.width > 0) DrawDockColumn(dockRect, Layout.right, false);
            HandlePanelDrag();
        }

        void DrawDockColumn(Rect r, List<string> ids, bool left)
        {
            PaintGui.Fill(r, PaintTheme.PanelBg);
            PaintGui.VLine(left ? r.xMax - 1 : r.x, r.y, r.yMax, PaintTheme.Border);
            // 高さ: 見出しと決まった高さのパネルを引いた残りを、開いている高さの決まっていないパネルで比に分ける
            var heights = ColumnHeights(r.height, ids);
            float y = r.y;
            for (int i = 0; i < ids.Count; i++)
            {
                var panel = Panel(ids[i]); bool collapsed = Layout.IsCollapsed(panel.Id);
                var head = new Rect(r.x + (left ? 0 : 1), y, r.width - 1, PanelHeaderHeight); y += PanelHeaderHeight;
                DrawPanelHeader(head, panel, collapsed);
                if (!collapsed && heights[i] > 0)
                {
                    var body = new Rect(head.x, y, head.width, heights[i]);
                    if (panel.Id == "color") colorColumnHeight = r.height;
                    panel.Draw(body); y += heights[i];
                    // 次の開いた高さの決まっていないパネルとの境目で高さの比を変える
                    int next = NextFlexible(ids, i);
                    if (panel.FixedHeight == null && next > i && next == i + 1) DrawSplitter(new Rect(body.x, body.yMax - 3, body.width, 6), panel.Id, ids[next], heights[i], heights[next], left);
                }
            }
            // 列の内側の縁で幅を変える
            var edge = left ? new Rect(r.xMax - 4, r.y, 6, r.height) : new Rect(r.x - 2, r.y, 6, r.height);
            DrawWidthHandle(edge, left);
        }

        /// <summary>列の各パネルの中身の高さ（畳んだものは 0）。</summary>
        float[] ColumnHeights(float total, List<string> ids)
        {
            var heights = new float[ids.Count];
            float rest = total - ids.Count * PanelHeaderHeight; float weightSum = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                var p = Panel(ids[i]);
                if (Layout.IsCollapsed(p.Id)) continue;
                if (p.FixedHeight != null) { heights[i] = Mathf.Min(p.FixedHeight(), Mathf.Max(0, rest)); rest -= heights[i]; }
                else weightSum += Layout.Weight(p.Id);
            }
            rest = Mathf.Max(0, rest);
            for (int i = 0; i < ids.Count; i++)
            {
                var p = Panel(ids[i]);
                if (Layout.IsCollapsed(p.Id) || p.FixedHeight != null) continue;
                heights[i] = weightSum > 0 ? rest * Layout.Weight(p.Id) / weightSum : 0;
            }
            return heights;
        }

        int NextFlexible(List<string> ids, int from)
        {
            for (int j = from + 1; j < ids.Count; j++) { var p = Panel(ids[j]); if (Layout.IsCollapsed(p.Id)) continue; return p.FixedHeight == null ? j : -1; }
            return -1;
        }

        void DrawPanelHeader(Rect r, DockPanel panel, bool collapsed)
        {
            var e = Event.current; bool hover = r.Contains(e.mousePosition) && GUI.enabled;
            PaintGui.Fill(r, panelDragging && panelDragCandidate == panel.Id ? PaintTheme.AccentDim : hover ? PaintTheme.ControlHover : PaintTheme.PanelHeader);
            PaintGui.HLine(r.x, r.xMax, r.yMax - 1, PaintTheme.Border);
            PaintGui.Icon(new Rect(r.x + 4, r.y, 16, r.height), collapsed ? "chevron_right" : "expand_more", PaintTheme.TextDim, 16);
            PaintGui.Icon(new Rect(r.x + 22, r.y, 16, r.height), panel.Icon, PaintTheme.TextDim, 15);
            PaintGui.Text(new Rect(r.x + 42, r.y, r.width - 66, r.height), L.Tr(panel.Title), PaintTheme.Header);
            PaintGui.Tooltip(r, L.Tr("Click to fold; drag to move the panel"));
            if (!GUI.enabled) return;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition)) { panelDragCandidate = panel.Id; panelDragStart = e.mousePosition; panelDragging = false; e.Use(); }
        }

        /// <summary>見出しを押したまま動かせばパネルの移動、動かさずに離せば畳む・開く。</summary>
        void HandlePanelDrag()
        {
            var e = Event.current;
            if (panelDragCandidate == null) return;
            if (e.type == EventType.MouseDrag && e.button == 0)
            {
                if (!panelDragging && Vector2.Distance(e.mousePosition, panelDragStart) > 5) panelDragging = true;
                e.Use(); Repaint(); return;
            }
            if (e.rawType == EventType.MouseUp)
            {
                if (panelDragging) { var target = PanelDropTarget(e.mousePosition); if (target.HasValue) { Layout.Move(panelDragCandidate, target.Value.left, target.Value.index); SaveDockLayout(); } }
                else { Layout.SetCollapsed(panelDragCandidate, !Layout.IsCollapsed(panelDragCandidate)); SaveDockLayout(); }
                panelDragCandidate = null; panelDragging = false; Repaint(); e.Use(); return;
            }
            if (panelDragging && e.type == EventType.Repaint)
            {
                var target = PanelDropTarget(e.mousePosition);
                if (target.HasValue)
                {
                    var (column, ids) = target.Value.left ? (leftDockRect.width > 0 ? leftDockRect : LeftDropZone, Layout.left) : (dockRect, Layout.right);
                    float y = HeaderY(column, ids, target.Value.index);
                    if (target.Value.left && leftDockRect.width == 0) PaintGui.Rounded(LeftDropZone, PaintTheme.AccentSoft, 4);
                    PaintGui.Fill(new Rect(column.x + 4, y - 1, column.width - 8, 3), PaintTheme.Accent);
                }
                var label = new Rect(e.mousePosition.x + 12, e.mousePosition.y + 8, 160, 22);
                PaintGui.Rounded(label, PaintTheme.PanelHeader, 4); PaintGui.Outline(label, PaintTheme.Accent, 1, 4);
                PaintGui.Text(new Rect(label.x + 8, label.y, label.width - 12, label.height), L.Tr(Panel(panelDragCandidate).Title), PaintTheme.Label);
            }
        }

        /// <summary>左の列が空のときに、パネルを落とすと左の列を作る所（表示域の左端）。</summary>
        Rect LeftDropZone => new Rect(viewAreaRect.x, viewAreaRect.y, 48, viewAreaRect.height);

        /// <summary>マウスの位置から、落とす列と位置（見出しの間）。どこでもなければ null。</summary>
        (bool left, int index)? PanelDropTarget(Vector2 mouse)
        {
            bool inLeft = leftDockRect.width > 0 ? leftDockRect.Contains(mouse) : LeftDropZone.Contains(mouse);
            bool inRight = dockRect.Contains(mouse);
            if (!inLeft && !inRight) return null;
            var column = inLeft ? (leftDockRect.width > 0 ? leftDockRect : LeftDropZone) : dockRect;
            var ids = inLeft ? Layout.left : Layout.right;
            var heights = ColumnHeights(column.height, ids);
            float y = column.y; int index = ids.Count;
            for (int i = 0; i < ids.Count; i++)
            {
                float panelHeight = PanelHeaderHeight + heights[i];
                if (mouse.y < y + panelHeight * .5f) { index = i; break; }
                y += panelHeight;
            }
            return (inLeft, index);
        }

        float HeaderY(Rect column, List<string> ids, int index)
        {
            var heights = ColumnHeights(column.height, ids); float y = column.y;
            for (int i = 0; i < index && i < ids.Count; i++) y += PanelHeaderHeight + heights[i];
            return y;
        }

        void DrawSplitter(Rect r, string upper, string lower, float upperHeight, float lowerHeight, bool left)
        {
            var e = Event.current;
            CursorRect(r, MouseCursor.ResizeVertical);
            bool active = splitterUpper == upper && splitterLower == lower;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition) && GUI.enabled)
            { splitterUpper = upper; splitterLower = lower; splitterLeft = left; splitterStartY = e.mousePosition.y; splitterUpperH = upperHeight; splitterLowerH = lowerHeight; e.Use(); }
            if (active && e.type == EventType.MouseDrag)
            {
                float total = splitterUpperH + splitterLowerH, up = Mathf.Clamp(splitterUpperH + e.mousePosition.y - splitterStartY, Panel(upper).MinHeight, total - Panel(lower).MinHeight);
                float sum = Layout.Weight(upper) + Layout.Weight(lower);
                Layout.SetWeight(upper, sum * up / total); Layout.SetWeight(lower, sum * (total - up) / total);
                e.Use(); Repaint();
            }
            if (active && e.rawType == EventType.MouseUp) { splitterUpper = splitterLower = null; SaveDockLayout(); e.Use(); }
            if (active || r.Contains(e.mousePosition)) PaintGui.Fill(new Rect(r.x, r.center.y - 1, r.width, 2), PaintTheme.AccentDim);
        }

        void DrawWidthHandle(Rect r, bool left)
        {
            var e = Event.current; int column = left ? 1 : 2;
            CursorRect(r, MouseCursor.ResizeHorizontal);
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition) && GUI.enabled)
            { widthDragColumn = column; widthDragStartX = e.mousePosition.x; widthDragStartWidth = left ? Layout.leftWidth : Layout.rightWidth; e.Use(); }
            if (widthDragColumn == column && e.type == EventType.MouseDrag)
            {
                float delta = e.mousePosition.x - widthDragStartX, width = Mathf.Clamp(widthDragStartWidth + (left ? delta : -delta), DockLayout.MinWidth, DockLayout.MaxWidth);
                if (left) Layout.leftWidth = width; else Layout.rightWidth = width;
                e.Use(); Repaint();
            }
            if (widthDragColumn == column && e.rawType == EventType.MouseUp) { widthDragColumn = 0; SaveDockLayout(); e.Use(); }
            if (widthDragColumn == column || r.Contains(e.mousePosition)) PaintGui.Fill(new Rect(r.center.x - 1, r.y, 2, r.height), PaintTheme.AccentDim);
        }

        /// <summary>マウスカーソルの形（オフスクリーンの描画では窓が無いので付けない）。</summary>
        void CursorRect(Rect r, MouseCursor cursor) { if (!LayoutOverride.HasValue) EditorGUIUtility.AddCursorRect(r, cursor); }

        /// <summary>ウィンドウ ▸ のパネルの項目: 畳む・開く。</summary>
        void TogglePanel(string id) { Layout.SetCollapsed(id, !Layout.IsCollapsed(id)); SaveDockLayout(); Repaint(); }
    }
}
