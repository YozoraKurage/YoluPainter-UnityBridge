using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 別のウィンドウに出したパネル（<see cref="PainterPanelWindow"/>）の、持ち主の側: 配置（DockLayout の Floating のまとまり）に合わせて
    /// ウィンドウを開く・閉じる・つなぎ直す、ウィンドウの中身をドックと同じ描き方で描く、そこでのドラッグとキーを持ち主に回す。
    /// 文書・選択・Undo はすべて持ち主のもので、どちらで変えても両方を描き直す（持ち主の Repaint の度に別のウィンドウも描き直し、
    /// 別のウィンドウで何か操作したら持ち主も描き直す）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>別のウィンドウが持ち主を見つけ直すための名前（ウィンドウと一緒にレイアウトに保存される）。</summary>
        [SerializeField] string panelOwnerKey;
        /// <summary>持ち主の窓の GUI の原点のスクリーン座標（別のウィンドウから持ち主のドックへ落とすときの換算に使う）。</summary>
        Vector2 dockScreenOrigin;
        /// <summary>別のウィンドウからドラッグしているときのマウスのスクリーン座標（持ち主の窓に落とす先の印を出す）。</summary>
        Vector2? foreignDragScreen;
        bool panelWindowsSynced;

        internal string PanelOwnerKey => string.IsNullOrEmpty(panelOwnerKey) ? panelOwnerKey = Guid.NewGuid().ToString("N") : panelOwnerKey;
        internal IEnumerable<PainterPanelWindow> PanelWindows => PainterPanelWindow.All.Where(w => w.Owner == this);
        /// <summary>ストロークや、持ち主のツールのドラッグ（選択範囲・変形など）の間は、パネルを触れない（ドックと同じく GUI.enabled で止める）。</summary>
        internal bool PanelsLocked => stroke != null || toolDragging;
        /// <summary>パネルが映す持ち主の状態の目印。持ち主が見えていない間に変わったら、別のウィンドウは自分で描き直す。</summary>
        internal long PanelStamp => document == null ? 0 : unchecked(document.Revision * 31 + selectedLayer.GetHashCode() * 17 + LayerSelectionStamp * 13L + (int)channel * 7 + (int)tool * 3 + (editMask ? 1 : 0));
        internal Vector2 DockScreenOriginForTests => dockScreenOrigin;

        /// <summary>持ち主の窓を閉じたら別のウィンドウも閉じる（配置には別のウィンドウのまま覚え、次に開いたときにまた出す）。</summary>
        void OnDestroy()
        {
            WarnUnappliedMaterialEdits();
            if (PainterPanelWindow.Quitting) return;
            foreach (var w in PanelWindows.ToList()) w.CloseWithOwner();
        }

        /// <summary>列を描いた後: 開いた最初に配置の別のウィンドウを開き（またはつなぎ直し）、Repaint では別のウィンドウも描き直す。</summary>
        void AfterDocksDrawn(Event e)
        {
            if (!panelWindowsSynced && !LayoutOverride.HasValue) { panelWindowsSynced = true; EditorApplication.delayCall += () => { if (this != null) SyncPanelWindows(); }; }
            if (e.type == EventType.Repaint) foreach (var w in PanelWindows) w.Repaint();
        }

        /// <summary>配置を変えた後: 覚え、別のウィンドウを配置に合わせ、描き直す。</summary>
        void PanelLayoutChanged()
        {
            SaveDockLayout(); SyncPanelWindows(); Repaint();
            foreach (var w in PanelWindows) w.Repaint();
        }

        /// <summary>
        /// 別のウィンドウを配置に合わせる: 配置で別のウィンドウでなくなったまとまりのウィンドウ（と重複）を閉じ、別のウィンドウのまとまりで
        /// ウィンドウの無いものは、持ち主を失ったウィンドウ（Unity の再起動やレイアウトの復元で戻ったもの）を同じまとまりならつなぎ直し、
        /// 無ければ開く。バッチモードではウィンドウを開けないので配置だけ（テストとオフスクリーンの描画）。
        /// </summary>
        internal void SyncPanelWindows()
        {
            var shown = new HashSet<string>();
            foreach (var w in PanelWindows.ToList())
            {
                bool floating = Layout.HasGroup(w.GroupId) && Layout.Group(w.GroupId).Floating;
                if (!floating || !shown.Add(w.GroupId)) w.CloseLeavingLayout();
            }
            if (Application.isBatchMode) return;
            foreach (var g in Layout.Column(DockPlace.Floating))
            {
                if (shown.Contains(g.id)) continue;
                var orphan = PainterPanelWindow.All.FirstOrDefault(w => w.Owner == null && w.GroupId == g.id);
                if (orphan != null) orphan.Attach(this, g); else PainterPanelWindow.Open(this, g, g.window.width > 0 ? g.window : DefaultPanelWindowRect(g, null));
            }
        }

        /// <summary>持ち主を失った別のウィンドウを引き取る。同じ名前の、無ければ同じパネルを持つ別のウィンドウのまとまりを映させる。配置で
        /// 別のウィンドウになっていない、またはもう別のウィンドウが映しているなら false（そのウィンドウは閉じる）。</summary>
        internal bool AdoptPanelWindow(PainterPanelWindow w)
        {
            var floating = Layout.Column(DockPlace.Floating);
            var g = floating.FirstOrDefault(x => x.id == w.GroupId) ?? floating.FirstOrDefault(x => x.panels.Intersect(w.ShownPanels).Any());
            if (g == null || PanelWindows.Any(o => o != w && o.GroupId == g.id)) return false;
            w.Attach(this, g);
            return true;
        }

        /// <summary>描き手が別のウィンドウを閉じた: まとまりを戻り先の列へ戻す。</summary>
        internal void PanelWindowClosed(PainterPanelWindow w)
        {
            if (Layout.HasGroup(w.GroupId) && Layout.Dock(w.GroupId)) { SaveDockLayout(); Repaint(); }
        }

        /// <summary>別のウィンドウの位置を配置に覚える（閉じる・リロードの前）。</summary>
        internal void RememberPanelWindowRect(PainterPanelWindow w)
        {
            if (!Layout.HasGroup(w.GroupId)) return;
            var g = Layout.Group(w.GroupId);
            if (g.Floating && g.window != w.position) { g.window = w.position; SaveDockLayout(); }
        }

        // ───────── 別のウィンドウにする・戻す（メニュー） ─────────

        /// <summary>パネルを別のウィンドウにする（タブでまとめていればそのパネルだけ）。持ち主の窓の列の脇に出す。</summary>
        internal void FloatPanel(string panel)
        {
            var from = Layout.GroupOf(panel);
            if (from.Floating && from.panels.Count == 1) return;
            Layout.FloatPanel(panel, DefaultPanelWindowRect(from, panel));
            PanelLayoutChanged();
        }

        /// <summary>まとまりごと別のウィンドウにする。</summary>
        internal void FloatGroup(string groupId)
        {
            var g = Layout.Group(groupId);
            if (g.Floating) return;
            Layout.FloatGroup(groupId, DefaultPanelWindowRect(g, null));
            PanelLayoutChanged();
        }

        /// <summary>別のウィンドウのまとまりを列へ戻す（ウィンドウは閉じる）。</summary>
        internal void DockPanelGroup(string groupId) { if (Layout.HasGroup(groupId) && Layout.Dock(groupId)) PanelLayoutChanged(); }
        internal void DockAllPanels() { foreach (var g in Layout.Column(DockPlace.Floating)) Layout.Dock(g.id); PanelLayoutChanged(); }
        /// <summary>ウィンドウ ▸ 別のウィンドウで開く ▸ のパネル: 列にあれば別のウィンドウへ、別のウィンドウにあれば（そのパネルだけ）列へ戻す。</summary>
        internal void ToggleFloating(string panel)
        {
            var g = Layout.GroupOf(panel);
            if (!g.Floating) { FloatPanel(panel); return; }
            if (g.panels.Count > 1) Layout.MovePanel(panel, g.home, g.homeIndex); else Layout.Dock(g.id);
            PanelLayoutChanged();
        }

        /// <summary>メニューから出すときの別のウィンドウの位置: 持ち主の窓の、まとまりのあった列の内側の脇。</summary>
        Rect DefaultPanelWindowRect(DockGroup g, string panel)
        {
            var origin = dockScreenOrigin != Vector2.zero || LayoutOverride.HasValue ? dockScreenOrigin : position.position;
            var size = PanelWindowSize(g, panel);
            bool left = (g.Floating ? g.home : g.place) == DockPlace.Left;
            var column = left ? leftDockRect : dockRect;
            float x = left ? column.xMax + 16 : (column.width > 0 ? column.x : WindowRect.width) - size.x - 16;
            return new Rect(origin.x + x, origin.y + Mathf.Max(column.y, PaintTheme.MenuBarHeight + PaintTheme.OptionsBarHeight) + 40, size.x, size.y);
        }

        /// <summary>ドラッグで窓の外へ出したときの別のウィンドウの位置（マウスの下に見出しが来るように）。</summary>
        Rect PanelWindowRectAt(Vector2 screen, DockGroup g, string panel)
        {
            var size = PanelWindowSize(g, panel);
            return new Rect(screen.x - 40, screen.y - 12, size.x, size.y);
        }

        Vector2 PanelWindowSize(DockGroup g, string panel)
        {
            var p = Panel(panel ?? g.Active);
            float width = g.Floating && g.window.width > 0 ? g.window.width : Mathf.Max(DockLayout.MinWidth, (g.place == DockPlace.Left ? Layout.leftWidth : Layout.rightWidth));
            float height = p.FixedHeight != null ? PanelHeaderHeight + p.FixedHeight() + 12 : 420;
            return new Vector2(width, Mathf.Max(PainterPanelWindow.MinHeight, height));
        }

        /// <summary>スクリーン座標の下にある、この持ち主の別のウィンドウ（except のまとまりのものを除く）。</summary>
        PainterPanelWindow PanelWindowAt(Vector2 screen, string except) => LayoutOverride.HasValue ? null : PanelWindows.FirstOrDefault(w => w.GroupId != except && w.position.Contains(screen));

        // ───────── 別のウィンドウの中身 ─────────

        /// <summary>
        /// 別のウィンドウ（host）の中身: まとまりの見出し（タブ）と見えているパネル。描き方・入力はドックと同じで、ストロークや持ち主のツールの
        /// ドラッグの間は止める。このウィンドウで開いたオブジェクトピッカーの知らせと、パネルが使わなかったキーは持ち主に回す。
        /// 落としたドラッグでこのウィンドウが閉じたら true（呼ぶ側はそれ以上描かない）。
        /// </summary>
        internal bool DrawPanelWindow(PainterPanelWindow host, Rect r)
        {
            var e = Event.current;
            if (document == null || preview == null) return false;
            var g = Layout.HasGroup(host.GroupId) ? Layout.Group(host.GroupId) : null;
            if (g == null || !g.Floating) { EditorApplication.delayCall += () => { if (host != null) host.CloseLeavingLayout(); }; return false; }
            HandleModelPicker(e);
            host.UpdateTitle(g);
            PaintGui.Fill(r, PaintTheme.PanelBg);
            using (new EditorGUI.DisabledScope(PanelsLocked))
            {
                var head = new Rect(r.x, r.y, r.width, PanelHeaderHeight);
                DrawGroupHeader(head, g, host);
                Panel(g.Active).Draw(new Rect(r.x, head.yMax, r.width, r.height - head.height));
                if (HandleFloatingDrag(host, g, head)) return true;
            }
            if (e.type == EventType.KeyDown) ForwardPanelWindowKey(e, host);
            return false;
        }

        /// <summary>別のウィンドウの見出しのドラッグ: 離した所がこのウィンドウの見出しならタブの並べ替え、ほかは <see cref="DropPanel"/>。
        /// ドラッグの間は持ち主の窓に落とす先の印を出す。</summary>
        bool HandleFloatingDrag(PainterPanelWindow host, DockGroup g, Rect head)
        {
            var e = Event.current;
            if (dragGroup == null || dragHost != host) return false;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { EndPanelDrag(); e.Use(); host.Repaint(); Repaint(); return false; }
            if (e.rawType == EventType.MouseDrag && e.type != EventType.Used)
            {
                if (!panelDragging && Vector2.Distance(e.mousePosition, panelDragStart) > 5) panelDragging = true;
                panelDragMouse = e.mousePosition;
                if (panelDragging) { foreignDragScreen = GUIUtility.GUIToScreenPoint(e.mousePosition); Repaint(); }
                e.Use(); host.Repaint(); return false;
            }
            if (e.rawType == EventType.MouseUp)
            {
                string group = dragGroup, panel = dragPanel; bool dragged = panelDragging;
                var screen = GUIUtility.GUIToScreenPoint(e.mousePosition); var local = e.mousePosition;
                EndPanelDrag(); e.Use(); Repaint(); host.Repaint();
                if (!Layout.HasGroup(group)) return false;
                if (!dragged) { ClickHeader(group, panel); return false; }
                if (head.Contains(local)) { if (panel != null) { Layout.Join(panel, group, TabIndexAt(head, g, local.x)); PanelLayoutChanged(); } return false; }
                DropPanel(group, panel, host, screen - dockScreenOrigin, screen);
                return host == null;
            }
            if (panelDragging && e.type == EventType.Repaint)
            {
                string what = L.Tr(Panel(dragPanel ?? g.Active).Title);
                var label = new Rect(panelDragMouse.x + 12, panelDragMouse.y + 8, Mathf.Min(220, PaintGui.TextWidth(what, PaintTheme.Label) + 24), 22);
                PaintGui.Rounded(label, PaintTheme.PanelHeader, 4); PaintGui.Outline(label, PaintTheme.Accent, 1, 4);
                PaintGui.Text(new Rect(label.x + 8, label.y, label.width - 12, label.height), what, PaintTheme.Label);
            }
            return false;
        }

        /// <summary>別のウィンドウからドラッグしている間、持ち主の窓に落とす先の印を出す。</summary>
        void DrawForeignPanelDrop()
        {
            if (Event.current.type != EventType.Repaint || !foreignDragScreen.HasValue || dragHost == null || !panelDragging) return;
            DrawDropPreview(foreignDragScreen.Value - dockScreenOrigin, foreignDragScreen.Value, false);
        }

        /// <summary>
        /// 別のウィンドウでパネルが使わなかったキーを持ち主のショートカットに回す（Ctrl+Z・Ctrl+S、1 文字のツールのキーなど。ペイントソフトの
        /// パネルと同じく、どのパネルに居てもアプリのショートカットが効く）。文字の入力中（keyboardControl が 0 でない）は 1 文字のキーは回らない。
        /// 持ち主が使ったキーは、そのウィンドウ（host）に印を付ける（ShortcutGuard がそれを見て Unity のショートカットへ渡さない）。
        /// </summary>
        internal void ForwardPanelWindowKey(Event e, PainterPanelWindow host)
        {
            if (e.type != EventType.KeyDown) return;
            HandleKeys(e);
            if (e.type == EventType.KeyDown) HandleToolKeys(e);
            if (e.type != EventType.Used) return;
            if (host != null) host.NoteTookKey(e);
            Repaint(); foreach (var w in PanelWindows) w.Repaint();
        }

        // ───────── テスト用 ─────────

        /// <summary>別のウィンドウの中身を、ウィンドウを開かずに描く（オフスクリーンの描画）。</summary>
        internal void DrawFloatingGroupForTests(string groupId, Rect r)
        {
            var g = Layout.Group(groupId);
            PaintGui.Fill(r, PaintTheme.PanelBg);
            var head = new Rect(r.x, r.y, r.width, PanelHeaderHeight);
            DrawGroupHeader(head, g, null);
            Panel(g.Active).Draw(new Rect(r.x, head.yMax, r.width, r.height - head.height));
        }

        /// <summary>列の中のパネルのまとまりの見出しの矩形（直前の描画の配置で）。</summary>
        internal Rect PanelHeaderRectForTests(string panel)
        {
            var g = Layout.GroupOf(panel);
            if (g.Floating) return default;
            var column = g.place == DockPlace.Left ? leftDockRect : dockRect;
            var groups = Layout.Column(g.place);
            return new Rect(column.x, HeaderY(column, groups, groups.IndexOf(g)), column.width, PanelHeaderHeight);
        }

        /// <summary>見出しをドラッグしている途中にする（オフスクリーンの描画で落とす先の印を見る）。tab ならそのパネルだけ、でなければまとまりごと。</summary>
        internal void PretendPanelDragForTests(string panel, bool tab, Vector2 mouse)
        { var g = Layout.GroupOf(panel); dragGroup = g.id; dragPanel = tab ? panel : null; dragHost = null; panelDragging = true; panelDragMouse = panelDragScreen = mouse; }
        internal void EndPanelDragForTests() => EndPanelDrag();

        /// <summary>別のウィンドウからのドラッグを、スクリーン座標 screen で離したことにする。</summary>
        internal void DropFromPanelWindowForTests(PainterPanelWindow from, string panel, Vector2 screen)
            => DropPanel(from.GroupId, panel, from, screen - dockScreenOrigin, screen);
    }
}
