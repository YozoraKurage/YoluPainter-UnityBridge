using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// ドックの描画: 左右の列、パネルのまとまりの見出し（タブ・畳む・ドラッグで動かす・右クリックのメニュー）、境目（高さの比・列の幅）。
    /// 見出しやタブを別の見出しに落とすとタブでまとまり、列の間に落とすとその位置へ、窓の外や表示域に落とすと別のウィンドウ
    /// （<see cref="PainterPanelWindow"/>、持ち主の側の扱いは TexturePaintWindow.PanelWindows.cs）になる。配置の型は DockLayout.cs。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        const float PanelHeaderHeight = 24;
        /// <summary>見出しのドラッグの間に持つ hotControl（ほかの部品がマウスに反応しないように。ウィンドウの外へ出ても離すまで続く）。</summary>
        const int PanelDragControl = 0x59500003;

        /// <summary>ドックのパネル。FixedHeight が null なら残りを高さの比で分け合う。</summary>
        sealed class DockPanel { public string Id, Title, Icon; public Func<float> FixedHeight; public float MinHeight = 60; public Action<Rect> Draw; }

        /// <summary>見出しに落とす先: JoinGroup があればそのまとまりの Tab 番目のタブに、無ければ列 Place の Index 番目に。</summary>
        struct PanelDrop { public DockPlace Place; public int Index; public string JoinGroup; public int Tab; public Rect Header; }

        DockLayout dockLayout;
        DockPanel[] dockPanels;
        Rect leftDockRect;
        // 見出しのドラッグ: まとまり（dragPanel が null）か 1 つのタブ。dragHost はドラッグを始めた別のウィンドウ（持ち主の窓なら null）
        string dragGroup, dragPanel; PainterPanelWindow dragHost; Vector2 panelDragStart; bool panelDragging;
        // ドラッグの最後のマウスの位置（持ち主の窓の座標とスクリーン座標）。落とす先の印はこれで描く（Repaint では 3D の描画の後で
        // Event.current.mousePosition が当てにならないので、MouseDrag で覚えたものを使う）
        Vector2 panelDragMouse, panelDragScreen;
        // 境目のドラッグ（高さの比・列の幅）
        string splitterUpper, splitterLower; bool splitterLeft; float splitterStartY, splitterUpperH, splitterLowerH;
        int widthDragColumn; float widthDragStartX, widthDragStartWidth; // 1 = 左、2 = 右
        float colorColumnHeight = 800;

        DockLayout Layout => dockLayout ?? (dockLayout = DockLayoutStore.Load().Normalized());
        internal DockLayout DockLayoutForTests => Layout;

        /// <summary>パネルの名前（訳す前）。別のウィンドウは持ち主を失っていてもタブの名前に使う。</summary>
        static readonly Dictionary<string, string> PanelTitles = new Dictionary<string, string> { { "color", "Color" }, { "textureSet", "Texture Set" }, { "layers", "Layers" }, { "properties", "Properties" }, { "material", "Material" } };
        internal static string PanelTitle(string id) => PanelTitles.TryGetValue(id, out var title) ? L.Tr(title) : id;

        DockPanel[] Panels => dockPanels ?? (dockPanels = new[]
        {
            new DockPanel { Id = "color", Title = PanelTitles["color"], Icon = "palette", FixedHeight = () => ColorPanelHeight, Draw = DrawColorPanel },
            new DockPanel { Id = "textureSet", Title = PanelTitles["textureSet"], Icon = "deployed_code", FixedHeight = () => TextureSetHeight, Draw = DrawTextureSetPanel },
            new DockPanel { Id = "layers", Title = PanelTitles["layers"], Icon = "layers", MinHeight = 140, Draw = DrawLayersPanel },
            new DockPanel { Id = "properties", Title = PanelTitles["properties"], Icon = "tune", MinHeight = 80, Draw = DrawPropertiesPanel },
            new DockPanel { Id = "material", Title = PanelTitles["material"], Icon = "auto_awesome", MinHeight = 160, Draw = DrawMaterialPanel },
        });
        DockPanel Panel(string id) => Panels.First(p => p.Id == id);

        /// <summary>列の幅（まとまりが無い列は 0）。窓の幅の 45% までに収める。</summary>
        float DockWidth(bool left, float windowWidth)
        {
            if (!Layout.groups.Any(g => g.place == (left ? DockPlace.Left : DockPlace.Right))) return 0;
            return Mathf.Min(left ? Layout.leftWidth : Layout.rightWidth, Mathf.Max(DockLayout.MinWidth, windowWidth * .45f));
        }

        void SaveDockLayout() => DockLayoutStore.Save(Layout);

        /// <summary>両方の列を描く（パネルのドラッグの落とし先も）。</summary>
        void DrawDocks()
        {
            var e = Event.current;
            if (e.type == EventType.Repaint && !LayoutOverride.HasValue) dockScreenOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero);
            if (leftDockRect.width > 0) DrawDockColumn(leftDockRect, DockPlace.Left);
            if (dockRect.width > 0) DrawDockColumn(dockRect, DockPlace.Right);
            HandlePanelDrag();
            DrawForeignPanelDrop();
            AfterDocksDrawn(e);
        }

        void DrawDockColumn(Rect r, DockPlace place)
        {
            bool left = place == DockPlace.Left;
            var groups = Layout.Column(place);
            PaintGui.Fill(r, PaintTheme.PanelBg);
            PaintGui.VLine(left ? r.xMax - 1 : r.x, r.y, r.yMax, PaintTheme.Border);
            // 高さ: 見出しと決まった高さのまとまりを引いた残りを、開いている高さの決まっていないまとまりで比に分ける
            var heights = ColumnHeights(r.height, groups);
            float y = r.y;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i]; var panel = Panel(g.Active);
                var head = new Rect(r.x + (left ? 0 : 1), y, r.width - 1, PanelHeaderHeight); y += PanelHeaderHeight;
                DrawGroupHeader(head, g, null);
                if (!g.collapsed && heights[i] > 0)
                {
                    var body = new Rect(head.x, y, head.width, heights[i]);
                    if (panel.Id == "color") colorColumnHeight = r.height;
                    panel.Draw(body); y += heights[i];
                    // 次の開いた高さの決まっていないまとまりとの境目で高さの比を変える
                    int next = NextFlexible(groups, i);
                    if (panel.FixedHeight == null && next > i && next == i + 1) DrawSplitter(new Rect(body.x, body.yMax - 3, body.width, 6), g, groups[next], heights[i], heights[next], left);
                }
            }
            // 列の内側の縁で幅を変える
            var edge = left ? new Rect(r.xMax - 4, r.y, 6, r.height) : new Rect(r.x - 2, r.y, 6, r.height);
            DrawWidthHandle(edge, left);
        }

        /// <summary>列の各まとまりの中身の高さ（畳んだものは 0）。高さは見えているタブのパネルで決まる。</summary>
        float[] ColumnHeights(float total, List<DockGroup> groups)
        {
            var heights = new float[groups.Count];
            float rest = total - groups.Count * PanelHeaderHeight; float weightSum = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i]; var p = Panel(g.Active);
                if (g.collapsed) continue;
                if (p.FixedHeight != null) { heights[i] = Mathf.Min(p.FixedHeight(), Mathf.Max(0, rest)); rest -= heights[i]; }
                else weightSum += Layout.Weight(g);
            }
            rest = Mathf.Max(0, rest);
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (g.collapsed || Panel(g.Active).FixedHeight != null) continue;
                heights[i] = weightSum > 0 ? rest * Layout.Weight(g) / weightSum : 0;
            }
            return heights;
        }

        int NextFlexible(List<DockGroup> groups, int from)
        {
            for (int j = from + 1; j < groups.Count; j++) { if (groups[j].collapsed) continue; return Panel(groups[j].Active).FixedHeight == null ? j : -1; }
            return -1;
        }

        // ───────── 見出しとタブ ─────────

        /// <summary>見出しの中のタブの矩形。1 つだけのまとまりは見出し全体（列では左の畳む印を除く）。収まらなければ幅を比で縮める。</summary>
        Rect[] TabRects(Rect head, DockGroup g)
        {
            float x = head.x + (g.Floating ? 4 : 22), available = head.xMax - 4 - x;
            int n = g.panels.Count;
            if (n == 1) return new[] { new Rect(x, head.y, available, head.height) };
            var natural = g.panels.Select(id => 20 + PaintGui.TextWidth(L.Tr(Panel(id).Title), PaintTheme.Header) + 12).ToArray();
            float scale = Mathf.Min(1, available / natural.Sum());
            var rects = new Rect[n];
            for (int i = 0; i < n; i++) { rects[i] = new Rect(x, head.y, Mathf.Floor(natural[i] * scale), head.height); x = rects[i].xMax; }
            return rects;
        }

        /// <summary>x の位置に入れるときのタブの番号（タブの中央より左ならその前）。</summary>
        int TabIndexAt(Rect head, DockGroup g, float x)
        {
            var tabs = TabRects(head, g);
            for (int i = 0; i < tabs.Length; i++) if (x < tabs[i].center.x) return i;
            return tabs.Length;
        }

        /// <summary>まとまりの見出し（列でも別のウィンドウでも）。host はそれを描いている別のウィンドウ（持ち主の窓なら null）。</summary>
        void DrawGroupHeader(Rect r, DockGroup g, PainterPanelWindow host)
        {
            var e = Event.current; bool single = g.panels.Count == 1;
            bool hover = r.Contains(e.mousePosition) && GUI.enabled;
            bool draggedWhole = panelDragging && dragGroup == g.id && dragPanel == null;
            PaintGui.Fill(r, draggedWhole ? PaintTheme.AccentDim : hover && single && !g.Floating ? PaintTheme.ControlHover : PaintTheme.PanelHeader);
            PaintGui.HLine(r.x, r.xMax, r.yMax - 1, PaintTheme.Border);
            if (!g.Floating) PaintGui.Icon(new Rect(r.x + 4, r.y, 16, r.height), g.collapsed ? "chevron_right" : "expand_more", PaintTheme.TextDim, 16);
            var tabs = TabRects(r, g);
            for (int i = 0; i < tabs.Length; i++)
            {
                var panel = Panel(g.panels[i]); var t = tabs[i]; bool shown = panel.Id == g.Active;
                if (!single)
                {
                    var face = new Rect(t.x, t.y + 3, t.width - 1, t.height - 3);
                    if (panelDragging && dragPanel == panel.Id) PaintGui.Fill(face, PaintTheme.AccentDim);
                    else if (shown) PaintGui.Fill(face, PaintTheme.PanelBg);
                    else if (t.Contains(e.mousePosition) && GUI.enabled) PaintGui.Fill(face, PaintTheme.ControlHover);
                    if (shown) PaintGui.Fill(new Rect(face.x, face.y, face.width, 2), g.collapsed ? PaintTheme.TextDim : PaintTheme.Accent);
                }
                DrawTabLabel(t, panel, single || shown);
                PaintGui.Tooltip(t, single ? HeaderTooltip(g) : L.Tr(panel.Title) + "\n" + HeaderTooltip(g));
            }
            if (!GUI.enabled) return;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                int tab = Array.FindIndex(tabs, t => t.Contains(e.mousePosition));
                dragGroup = g.id; dragPanel = !single && tab >= 0 ? g.panels[tab] : null; dragHost = host;
                panelDragStart = panelDragMouse = e.mousePosition; panelDragging = false;
                GUIUtility.hotControl = PanelDragControl; e.Use();
            }
            else if (e.type == EventType.ContextClick && r.Contains(e.mousePosition))
            {
                int tab = Array.FindIndex(tabs, t => t.Contains(e.mousePosition));
                PanelHeaderMenu(g, tab >= 0 ? g.panels[tab] : g.Active);
                e.Use();
            }
        }

        string HeaderTooltip(DockGroup g) => g.Floating
            ? L.Tr("Drag a tab onto the painter window's dock to put it back, or onto another header to make tabs. Right-click for more.")
            : L.Tr("Click to fold; drag to move, onto another header to make tabs, or out of the window to open it in a separate window. Right-click for more.");

        /// <summary>タブの中身（アイコンと名前）。幅が足りなければ名前を … で詰め、さらに狭ければアイコンだけ。</summary>
        void DrawTabLabel(Rect t, DockPanel panel, bool bright)
        {
            var color = bright ? PaintTheme.Text : PaintTheme.TextDim;
            if (t.width < 44) { PaintGui.Icon(new Rect(t.x, t.y + 1, t.width, t.height), panel.Icon, color, 15); return; }
            PaintGui.Icon(new Rect(t.x, t.y + 1, 16, t.height), panel.Icon, PaintTheme.TextDim, 15);
            var text = new Rect(t.x + 20, t.y + 1, t.width - 24, t.height);
            PaintGui.Text(text, PaintGui.Fit(L.Tr(panel.Title), text.width, PaintTheme.Header, false), PaintTheme.Header, color);
        }

        // ───────── ドラッグ ─────────

        /// <summary>見出しを押したまま動かせばまとまり（タブならそのパネル）の移動、動かさずに離せばタブの切り替えか畳む・開く。Escape で取り消す。</summary>
        void HandlePanelDrag()
        {
            var e = Event.current;
            if (dragGroup == null) return;
            if (!ReferenceEquals(dragHost, null)) { if (dragHost == null) EndPanelDrag(); return; } // 別のウィンドウのドラッグ（そのウィンドウが閉じていたら捨てる）
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { EndPanelDrag(); e.Use(); Repaint(); return; }
            if (e.rawType == EventType.MouseDrag && e.type != EventType.Used)
            {
                if (!panelDragging && Vector2.Distance(e.mousePosition, panelDragStart) > 5) panelDragging = true;
                panelDragMouse = e.mousePosition; panelDragScreen = ToScreen(e.mousePosition);
                e.Use(); Repaint(); return;
            }
            if (e.rawType == EventType.MouseUp)
            {
                string group = dragGroup, panel = dragPanel; bool dragged = panelDragging;
                EndPanelDrag(); e.Use();
                if (!Layout.HasGroup(group)) { Repaint(); return; }
                if (dragged) DropPanel(group, panel, null, e.mousePosition, ToScreen(e.mousePosition));
                else ClickHeader(group, panel);
                Repaint(); return;
            }
            if (panelDragging && e.type == EventType.Repaint) DrawDropPreview(panelDragMouse, panelDragScreen, true);
        }

        /// <summary>持ち主の窓の座標をスクリーン座標に（オフスクリーンの描画では窓が無いのでそのまま）。</summary>
        Vector2 ToScreen(Vector2 gui) => LayoutOverride.HasValue ? gui : GUIUtility.GUIToScreenPoint(gui);

        void EndPanelDrag()
        {
            dragGroup = dragPanel = null; dragHost = null; panelDragging = false; foreignDragScreen = null;
            if (GUIUtility.hotControl == PanelDragControl) GUIUtility.hotControl = 0;
        }

        /// <summary>動かさずに離した見出し: 見えていないタブなら見えるように（畳んでいれば開く）、それ以外は列のまとまりを畳む・開く。</summary>
        void ClickHeader(string groupId, string panel)
        {
            var g = Layout.Group(groupId);
            if (panel != null && panel != g.Active) Layout.SetActive(panel);
            else if (!g.Floating) g.collapsed = !g.collapsed;
            else return;
            PanelLayoutChanged();
        }

        /// <summary>
        /// ドラッグしたまとまり（panel があればそのタブだけ）を落とす。順に: ほかの別のウィンドウの上ならそのタブに、持ち主の窓の見出しの上ならタブに、
        /// 列の中ならその位置に、持ち主の窓の外か表示域（キャンバス・3D）なら別のウィンドウに。from は別のウィンドウから動かしたときのそのウィンドウ
        /// （そこからは、タブを窓の外へ出したときだけ新しい別のウィンドウにする）。gui は持ち主の窓の座標、screen はスクリーンの座標。
        /// </summary>
        void DropPanel(string groupId, string panel, PainterPanelWindow from, Vector2 gui, Vector2 screen)
        {
            if (!Layout.HasGroup(groupId)) return;
            var group = Layout.Group(groupId);
            var window = PanelWindowAt(screen, groupId);
            if (window != null)
            {
                if (panel != null) Layout.Join(panel, window.GroupId); else Layout.JoinGroup(groupId, window.GroupId);
            }
            else if (WindowRect.Contains(gui) && PanelDropTarget(gui, groupId, panel) is PanelDrop d)
            {
                if (d.JoinGroup != null) { if (panel != null) Layout.Join(panel, d.JoinGroup, d.Tab); else Layout.JoinGroup(groupId, d.JoinGroup, d.Tab); }
                else if (panel != null) Layout.MovePanel(panel, d.Place, d.Index);
                else Layout.MoveGroup(groupId, d.Place, d.Index);
            }
            else if (from == null ? !WindowRect.Contains(gui) || viewAreaRect.Contains(gui) : panel != null && group.panels.Count > 1)
            {
                var rect = PanelWindowRectAt(screen, group, panel);
                if (panel != null) Layout.FloatPanel(panel, rect); else Layout.FloatGroup(groupId, rect);
            }
            else return;
            PanelLayoutChanged();
        }

        /// <summary>空の列に落とすと列を作る所（表示域の左端・右端）。</summary>
        Rect EmptyColumnDropZone(DockPlace place) => place == DockPlace.Left
            ? new Rect(viewAreaRect.x, viewAreaRect.y, 48, viewAreaRect.height)
            : new Rect(viewAreaRect.xMax - 48, viewAreaRect.y, 48, viewAreaRect.height);
        Rect ColumnDropRect(DockPlace place)
        {
            var column = place == DockPlace.Left ? leftDockRect : dockRect;
            return column.width > 0 ? column : EmptyColumnDropZone(place);
        }

        /// <summary>
        /// マウスの位置から落とす先。見出しの上（上の縁を除く）ならそのまとまりのタブに（自分のまとまりをまるごと自分に落とすのは除く）、
        /// それ以外の列の中なら見出しの間。どこでもなければ null。
        /// </summary>
        PanelDrop? PanelDropTarget(Vector2 mouse, string groupId = null, string panel = null)
        {
            foreach (var place in new[] { DockPlace.Left, DockPlace.Right })
            {
                var column = ColumnDropRect(place);
                if (!column.Contains(mouse)) continue;
                var groups = Layout.Column(place);
                var heights = ColumnHeights(column.height, groups);
                float y = column.y;
                for (int i = 0; i < groups.Count; i++)
                {
                    var head = new Rect(column.x, y, column.width, PanelHeaderHeight);
                    bool self = groups[i].id == groupId && (panel == null || groups[i].panels.Count == 1);
                    if (head.Contains(mouse) && mouse.y > head.y + 5 && !self)
                        return new PanelDrop { JoinGroup = groups[i].id, Tab = TabIndexAt(head, groups[i], mouse.x), Header = head };
                    float height = PanelHeaderHeight + heights[i];
                    if (mouse.y < y + height * .5f) return new PanelDrop { Place = place, Index = i };
                    y += height;
                }
                return new PanelDrop { Place = place, Index = groups.Count };
            }
            return null;
        }

        float HeaderY(Rect column, List<DockGroup> groups, int index)
        {
            var heights = ColumnHeights(column.height, groups); float y = column.y;
            for (int i = 0; i < index && i < groups.Count; i++) y += PanelHeaderHeight + heights[i];
            return y;
        }

        /// <summary>落とす先の印（見出しなら枠とタブの入る位置、列なら線）と、マウスの脇の持っているパネルの名前。drawLabel が false なら印だけ
        /// （別のウィンドウからのドラッグを持ち主の窓に映すとき）。</summary>
        void DrawDropPreview(Vector2 gui, Vector2 screen, bool drawLabel)
        {
            if (dragGroup == null || !Layout.HasGroup(dragGroup)) return;
            var group = Layout.Group(dragGroup);
            string what = L.Tr(Panel(dragPanel ?? group.Active).Title) + (dragPanel == null && group.panels.Count > 1 ? " +" + (group.panels.Count - 1) : "");
            var window = PanelWindowAt(screen, dragGroup);
            var target = window == null && WindowRect.Contains(gui) ? PanelDropTarget(gui, dragGroup, dragPanel) : null;
            if (target is PanelDrop d)
            {
                if (d.JoinGroup != null)
                {
                    PaintGui.Fill(d.Header, PaintTheme.AccentSoft); PaintGui.Outline(d.Header, PaintTheme.Accent, 1, 0);
                    var tabs = TabRects(d.Header, Layout.Group(d.JoinGroup));
                    float x = d.Tab < tabs.Length ? tabs[d.Tab].x : tabs[tabs.Length - 1].xMax;
                    PaintGui.Fill(new Rect(x - 1, d.Header.y + 3, 3, d.Header.height - 6), PaintTheme.Accent);
                }
                else
                {
                    var column = ColumnDropRect(d.Place);
                    if ((d.Place == DockPlace.Left ? leftDockRect : dockRect).width == 0) PaintGui.Rounded(column, PaintTheme.AccentSoft, 4);
                    float y = HeaderY(column, Layout.Column(d.Place), d.Index);
                    PaintGui.Fill(new Rect(column.x + 4, y - 1, column.width - 8, 3), PaintTheme.Accent);
                }
            }
            if (!drawLabel) return;
            bool floats = window == null && target == null && (!WindowRect.Contains(gui) || viewAreaRect.Contains(gui));
            if (floats) what += " — " + L.Tr("New window");
            else if (window != null) what += " — " + L.Tr("Tab in the separate window");
            float w = Mathf.Min(260, PaintGui.TextWidth(what, PaintTheme.Label) + 24);
            var label = new Rect(gui.x + 12, gui.y + 8, w, 22);
            if (label.xMax > WindowRect.xMax - 4) label.x = gui.x - 12 - w; // 窓の右の端ではマウスの左に出す
            PaintGui.Rounded(label, PaintTheme.PanelHeader, 4); PaintGui.Outline(label, PaintTheme.Accent, 1, 4);
            PaintGui.Text(new Rect(label.x + 8, label.y, label.width - 12, label.height), what, PaintTheme.Label);
        }

        // ───────── 境目と幅 ─────────

        void DrawSplitter(Rect r, DockGroup upper, DockGroup lower, float upperHeight, float lowerHeight, bool left)
        {
            var e = Event.current;
            CursorRect(r, MouseCursor.ResizeVertical);
            bool active = splitterUpper == upper.id && splitterLower == lower.id;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition) && GUI.enabled)
            { splitterUpper = upper.id; splitterLower = lower.id; splitterLeft = left; splitterStartY = e.mousePosition.y; splitterUpperH = upperHeight; splitterLowerH = lowerHeight; e.Use(); }
            if (active && e.type == EventType.MouseDrag)
            {
                float total = splitterUpperH + splitterLowerH, up = Mathf.Clamp(splitterUpperH + e.mousePosition.y - splitterStartY, Panel(upper.Active).MinHeight, total - Panel(lower.Active).MinHeight);
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

        // ───────── メニュー ─────────

        /// <summary>見出しの右クリック: 別のウィンドウにする・タブを分ける・左右の列へ・畳む（別のウィンドウでは、列に戻す・自分のウィンドウにする）。</summary>
        void PanelHeaderMenu(DockGroup g, string panel)
        {
            var menu = new GenericMenu();
            foreach (var (text, action) in PanelHeaderMenuItems(g, panel))
            {
                if (text == null) { menu.AddSeparator(""); continue; }
                var run = action; menu.AddItem(new GUIContent(text), false, () => TryAction(run));
            }
            menu.ShowAsContext();
        }

        /// <summary>見出しの右クリックの項目（null の文字は区切り）。panel は押したタブ（1 つだけのまとまりならそのパネル）。</summary>
        internal List<(string text, Action action)> PanelHeaderMenuItems(DockGroup g, string panel)
        {
            var items = new List<(string, Action)>(); string gid = g.id; bool tabs = g.panels.Count > 1;
            if (!g.Floating)
            {
                items.Add((tabs ? L.Tr("Open This Panel in a Separate Window") : L.Tr("Open in Separate Window"), () => FloatPanel(panel)));
                if (tabs)
                {
                    items.Add((L.Tr("Open Tab Group in Separate Window"), () => FloatGroup(gid)));
                    items.Add((L.Tr("Separate from Tab Group"), () => { var at = Layout.Group(gid); Layout.MovePanel(panel, at.place, Layout.Column(at.place).IndexOf(at) + 1); PanelLayoutChanged(); }));
                }
                items.Add((null, null));
                var other = g.place == DockPlace.Left ? DockPlace.Right : DockPlace.Left;
                items.Add((other == DockPlace.Left ? L.Tr("Move to Left Column") : L.Tr("Move to Right Column"), () => { Layout.MoveGroup(gid, other, int.MaxValue); PanelLayoutChanged(); }));
                items.Add((g.collapsed ? L.TrIn("panel", "Unfold") : L.TrIn("panel", "Fold"), () => { var at = Layout.Group(gid); at.collapsed = !at.collapsed; PanelLayoutChanged(); }));
            }
            else
            {
                items.Add((L.Tr("Return to the Dock"), () => DockPanelGroup(gid)));
                if (tabs)
                {
                    items.Add((L.Tr("Return This Panel to the Dock"), () => { var at = Layout.Group(gid); Layout.MovePanel(panel, at.home, at.homeIndex); PanelLayoutChanged(); }));
                    items.Add((L.Tr("Separate into Its Own Window"), () =>
                    {
                        var at = Layout.Group(gid);
                        var rect = at.window.width > 0 ? new Rect(at.window.x + 32, at.window.y + 32, at.window.width, at.window.height) : DefaultPanelWindowRect(Layout.GroupOf(panel), panel);
                        Layout.FloatPanel(panel, rect); PanelLayoutChanged();
                    }));
                }
            }
            return items;
        }

        /// <summary>ウィンドウ ▸ のパネルの項目: 見えていれば畳む、見えていなければ（畳んだ・隠れたタブ）見えるようにする。別のウィンドウなら前に出す。</summary>
        internal void ShowOrHidePanel(string id)
        {
            var g = Layout.GroupOf(id);
            if (g.Floating) { Layout.SetActive(id); PanelWindows.FirstOrDefault(w => w.GroupId == g.id)?.Focus(); }
            else if (PanelShown(id)) g.collapsed = true;
            else Layout.SetActive(id);
            PanelLayoutChanged();
        }
        internal bool PanelShown(string id) { var g = Layout.GroupOf(id); return g.Floating || !g.collapsed && g.Active == id; }

        internal void ResetDockLayout()
        {
            foreach (var w in PanelWindows.ToList()) w.CloseLeavingLayout();
            DockLayoutStore.Reset(); dockLayout = DockLayout.Default(); Repaint();
        }
    }
}
