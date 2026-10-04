using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>ペイント画面の自前メニュー。項目は GenericMenu と同じ / 区切りで登録し、実行する前にすべての窓を閉じる。</summary>
    internal sealed class PaintMenu
    {
        public delegate void MenuFunction();
        public delegate void MenuFunction2(object userData);
        internal sealed class Entry
        {
            public GUIContent content; public bool separator, on, enabled, heading, radio;
            public MenuFunction func; public MenuFunction2 func2; public object userData;
            internal void Run() { if (enabled) { func?.Invoke(); func2?.Invoke(userData); } }
        }
        readonly List<Entry> menuItems = new List<Entry>();
        internal IReadOnlyList<Entry> Entries => menuItems;
        public void AddItem(GUIContent content, bool on, MenuFunction func) => menuItems.Add(new Entry { content = new GUIContent(content), on = on, enabled = func != null, func = func });
        public void AddItem(GUIContent content, bool on, MenuFunction2 func, object userData) => menuItems.Add(new Entry { content = new GUIContent(content), on = on, enabled = func != null, func2 = func, userData = userData });
        public void AddDisabledItem(GUIContent content, bool on = false) => menuItems.Add(new Entry { content = new GUIContent(content), on = on });
        public void AddSeparator(string path) => menuItems.Add(new Entry { content = new GUIContent(path ?? ""), separator = true });
        public void AddHeading(string path) => menuItems.Add(new Entry { content = new GUIContent(path), heading = true });
        public void AddRadioItem(GUIContent content, bool on, MenuFunction func) { AddItem(content, on, func); menuItems[menuItems.Count - 1].radio = true; }
        public int GetItemCount() => menuItems.Count;
        public void ShowAsContext() => DropDown(new Rect(Event.current.mousePosition, Vector2.zero));
        public void DropDown(Rect at) => PaintMenuSession.Open(this, GUIUtility.GUIToScreenRect(at), EditorWindow.focusedWindow);

        internal sealed class Node
        {
            internal string Label, Shortcut; internal Entry Item;
            internal readonly List<Node> Children = new List<Node>();
            internal bool Separator => Item != null && Item.separator;
            internal bool Heading => Item != null && Item.heading;
            internal bool Enabled => Children.Count > 0 ? Children.Any(c => c.Enabled) : Item != null && Item.enabled;
        }
        internal List<Node> Build()
        {
            var root = new Node();
            foreach (var item in menuItems)
            {
                string path = item.content.text ?? ""; string shortcut = null;
                int split = path.IndexOf('\t'); int spaces = path.IndexOf("    ", StringComparison.Ordinal);
                if (split < 0 || spaces >= 0 && spaces < split) split = spaces;
                if (!item.separator && split >= 0) { shortcut = path.Substring(split).Trim(); path = path.Substring(0, split); }
                var parts = path.Split('/'); var parent = root;
                int count = parts.Length - 1;
                for (int i = 0; i < count; i++)
                {
                    if (parts[i].Length == 0) continue;
                    var next = parent.Children.FirstOrDefault(n => n.Item == null && n.Label == parts[i]);
                    if (next == null) { next = new Node { Label = parts[i] }; parent.Children.Add(next); }
                    parent = next;
                }
                string label = item.separator ? "" : parts[parts.Length - 1];
                parent.Children.Add(new Node { Label = label, Shortcut = shortcut, Item = item });
            }
            return root.Children;
        }
    }

    /// <summary>バーの見出しと開いたメニューの対応。別の見出しへマウスが移れば、クリックを待たずに切り替える。</summary>
    internal sealed class PaintMenuBar
    {
        internal EditorWindow Owner; internal Func<int, PaintMenu> Make; internal Action BeforeOpen;
        internal Rect[] ScreenRects = Array.Empty<Rect>(); internal int Selected = -1;
        internal bool Opened => PaintMenuSession.Current?.Bar == this;
        internal void Open(int index, bool toggle = false)
        {
            if (index < 0 || index >= ScreenRects.Length) return;
            if (toggle && Opened && Selected == index) { PaintMenuSession.CloseAll(); return; }
            BeforeOpen?.Invoke();
            Selected = index; PaintMenuSession.Open(Make(index), ScreenRects[index], Owner, this);
        }
        internal void Step(int amount) { if (ScreenRects.Length > 0) Open((Selected + amount + ScreenRects.Length) % ScreenRects.Length); }
        internal void Pointer(Vector2 screen, bool click)
        {
            for (int i = 0; i < ScreenRects.Length; i++)
                if (ScreenRects[i].Contains(screen)) { if (click) Open(i, true); else if (Opened && Selected != i) Open(i); return; }
        }
    }

    [InitializeOnLoad]
    internal sealed class PaintMenuSession
    {
        internal static PaintMenuSession Current { get; private set; }
        internal readonly List<PaintMenuPopup> Windows = new List<PaintMenuPopup>();
        internal EditorWindow Owner; internal PaintMenuBar Bar;
        bool closing; double focusCheckAfter;
        static PaintMenuSession()
        {
            AssemblyReloadEvents.beforeAssemblyReload += CloseAll;
            EditorApplication.playModeStateChanged += _ => CloseAll();
            EditorApplication.quitting += CloseAll;
            EditorApplication.update += () => Current?.CheckFocus();
        }
        internal static void Open(PaintMenu menu, Rect screen, EditorWindow owner, PaintMenuBar bar = null)
        {
            CloseAll();
            if (Application.isBatchMode) return;
            var session = new PaintMenuSession { Owner = owner, Bar = bar, focusCheckAfter = EditorApplication.timeSinceStartup + .15 };
            Current = session;
            session.Show(menu.Build(), screen, false);
            owner?.Repaint();
        }
        internal static void CloseAll() => Current?.Close();
        internal static void CloseFor(EditorWindow owner) { if (Current?.Owner == owner) CloseAll(); }
        internal void Close()
        {
            if (closing) return;
            closing = true; if (Current == this) Current = null;
            var windows = Windows.ToArray(); Windows.Clear();
            foreach (var window in windows.Reverse()) if (window != null) { window.Session = null; window.Close(); }
            Owner?.Repaint();
        }
        internal bool Owns(EditorWindow window) => window != null && (window == Owner || Windows.Contains(window as PaintMenuPopup));
        internal void CheckFocus()
        {
            if (closing || EditorApplication.timeSinceStartup < focusCheckAfter) return;
            if (Owner == null || !Owns(EditorWindow.focusedWindow) || !UnityEditorInternal.InternalEditorUtility.isApplicationActive) Close();
        }
        internal void CheckFocusSoon() { focusCheckAfter = EditorApplication.timeSinceStartup + .05; }
        internal void PopupDestroyed(PaintMenuPopup popup)
        {
            if (!closing && Windows.Contains(popup)) Close();
        }
        internal void Show(List<PaintMenu.Node> nodes, Rect anchor, bool child)
        {
            var popup = ScriptableObject.CreateInstance<PaintMenuPopup>();
            popup.hideFlags = HideFlags.HideAndDontSave; popup.Session = this; popup.Nodes = nodes;
            Rect desktop;
            try { desktop = UnityEditorInternal.InternalEditorUtility.GetBoundsOfDesktopAtPoint(anchor.center); }
            catch (Exception ex) when (ex is NotImplementedException || ex is ArgumentException) { desktop = new Rect(0, 0, Screen.currentResolution.width, Screen.currentResolution.height); }
            var size = PaintMenuPopup.Measure(nodes);
            popup.position = PaintMenuPopup.Place(anchor, size, desktop, child);
            popup.minSize = popup.maxSize = popup.position.size;
            Windows.Add(popup); popup.ShowPopup(); popup.position = PaintMenuPopup.Place(anchor, size, desktop, child);
            popup.Focus(); popup.Repaint(); focusCheckAfter = EditorApplication.timeSinceStartup + .15;
        }
        internal void TrimAfter(PaintMenuPopup popup)
        {
            int depth = Windows.IndexOf(popup);
            for (int i = Windows.Count - 1; i > depth; i--) { var w = Windows[i]; Windows.RemoveAt(i); if (w != null) w.Close(); }
        }
        internal void Submenu(PaintMenuPopup popup, int index, bool keyboard)
        {
            if (index < 0 || index >= popup.Nodes.Count) return;
            var node = popup.Nodes[index]; if (!node.Enabled || node.Children.Count == 0) return;
            int depth = Windows.IndexOf(popup);
            if (Windows.Count > depth + 1 && ReferenceEquals(Windows[depth + 1].Nodes, node.Children)) return;
            TrimAfter(popup);
            var row = popup.Row(index); row.position += popup.position.position;
            Show(node.Children, row, true);
            if (keyboard) Windows.Last().Move(1);
        }
        internal void Back(PaintMenuPopup popup)
        {
            int depth = Windows.IndexOf(popup);
            if (depth > 0) { var parent = Windows[depth - 1]; TrimAfter(parent); parent.Focus(); }
            else Close();
        }
        internal void Execute(PaintMenu.Node node)
        {
            if (!node.Enabled || node.Children.Count > 0) return;
            var action = node.Item; var owner = Owner; Close(); owner?.Focus(); action.Run(); owner?.Repaint();
        }
        /// <summary>持ち主に届いた外側のクリックは閉じるために使い、下のキャンバスへ渡さない。</summary>
        internal static bool HandleOwnerEvent(EditorWindow owner, Event e)
        {
            var session = Current;
            if (session == null || session.Owner != owner) return false;
            var screen = GUIUtility.GUIToScreenPoint(e.mousePosition);
            bool bar = session.Bar != null && session.Bar.ScreenRects.Any(r => r.Contains(screen));
            if (e.type == EventType.MouseMove && bar) { session.Bar.Pointer(screen, false); owner.Repaint(); }
            if (e.type == EventType.MouseDown)
            {
                if (bar) session.Bar.Pointer(screen, true); else session.Close();
                e.Use(); return true;
            }
            if (e.type == EventType.KeyDown) { session.Windows.LastOrDefault()?.Key(e); return e.type == EventType.Used; }
            return false;
        }
    }

    internal sealed class PaintMenuPopup : EditorWindow, IPainterShortcutScope
    {
        internal const float Margin = 8, Padding = 6, RowHeight = 28, SeparatorHeight = 9;
        internal PaintMenuSession Session; internal List<PaintMenu.Node> Nodes = new List<PaintMenu.Node>();
        internal int Selected = -1; float scroll; int pressed = -1; double hoverAt; int hoverIndex = -1;
        bool IPainterShortcutScope.EditingText => false;
        bool IPainterShortcutScope.TookKey(KeyCode key, EventModifiers modifiers) => true;
        void OnEnable() { wantsMouseMove = true; wantsMouseEnterLeaveWindow = true; }
        void OnLostFocus() => Session?.CheckFocusSoon();
        void OnDestroy() => Session?.PopupDestroyed(this);
        void OnInspectorUpdate()
        {
            if (Session == null) { Close(); return; }
            if (hoverIndex >= 0 && EditorApplication.timeSinceStartup >= hoverAt) { Session.Submenu(this, hoverIndex, false); hoverIndex = -1; }
        }
        internal static float Height(PaintMenu.Node node) => node.Separator ? SeparatorHeight : RowHeight;
        internal static Vector2 Measure(List<PaintMenu.Node> nodes)
        {
            float label = 0, keys = 0;
            foreach (var node in nodes) { label = Mathf.Max(label, PaintGui.TextWidth(node.Label, PaintTheme.Label)); keys = Mathf.Max(keys, PaintGui.TextWidth(node.Shortcut ?? "", PaintTheme.LabelSmall)); }
            return new Vector2(Mathf.Max(180, label + (keys > 0 ? keys + 28 : 0) + 64 + Margin * 2), nodes.Sum(Height) + (Margin + Padding) * 2);
        }
        internal static Rect Place(Rect anchor, Vector2 size, Rect desktop, bool child)
        {
            size.x = Mathf.Min(size.x, desktop.width); size.y = Mathf.Min(size.y, desktop.height);
            float x = child ? anchor.xMax - Margin : anchor.x - Margin;
            float y = child ? anchor.y - Margin - Padding : anchor.yMax - Margin;
            if (child && x + size.x > desktop.xMax) x = anchor.x - size.x + Margin;
            if (!child && y + size.y > desktop.yMax) y = anchor.y - size.y + Margin;
            return new Rect(Mathf.Clamp(x, desktop.xMin, desktop.xMax - size.x), Mathf.Clamp(y, desktop.yMin, desktop.yMax - size.y), size.x, size.y);
        }
        internal Rect Body => new Rect(Margin, Margin, position.width - Margin * 2, position.height - Margin * 2);
        internal Rect Row(int index)
        {
            float y = Margin + Padding - scroll; for (int i = 0; i < index; i++) y += Height(Nodes[i]);
            return new Rect(Margin + 4, y, position.width - Margin * 2 - 8, Height(Nodes[index]));
        }
        internal void Move(int direction)
        {
            for (int step = 1; step <= Nodes.Count; step++)
            {
                int start = Selected < 0 && direction < 0 ? 0 : Selected;
                int index = (start + direction * step + Nodes.Count * 2) % Nodes.Count;
                if (Nodes[index].Enabled && !Nodes[index].Separator && !Nodes[index].Heading) { Select(index, false); return; }
            }
        }
        void Select(int index, bool mouse)
        {
            if (Selected == index) return;
            Selected = index; Session?.TrimAfter(this); hoverIndex = -1;
            if (index >= 0)
            {
                var row = Row(index); float bottom = position.height - Margin - Padding;
                if (row.y < Margin + Padding) scroll -= Margin + Padding - row.y;
                else if (row.yMax > bottom) scroll += row.yMax - bottom;
                if (mouse && Nodes[index].Children.Count > 0) { hoverIndex = index; hoverAt = EditorApplication.timeSinceStartup + .16; }
            }
            Repaint();
        }
        internal void Key(Event e)
        {
            if (e.type != EventType.KeyDown) return;
            switch (e.keyCode)
            {
                case KeyCode.DownArrow: Move(1); break;
                case KeyCode.UpArrow: Move(-1); break;
                case KeyCode.RightArrow:
                    if (Selected >= 0 && Nodes[Selected].Children.Count > 0) Session.Submenu(this, Selected, true); else if (Session.Windows.IndexOf(this) == 0) Session.Bar?.Step(1);
                    break;
                case KeyCode.LeftArrow:
                    if (Session.Windows.IndexOf(this) > 0) Session.Back(this); else Session.Bar?.Step(-1);
                    break;
                case KeyCode.Escape: Session.Back(this); break;
                case KeyCode.Return: case KeyCode.KeypadEnter: case KeyCode.Space:
                    if (Selected >= 0) { if (Nodes[Selected].Children.Count > 0) Session.Submenu(this, Selected, true); else Session.Execute(Nodes[Selected]); }
                    break;
                default:
                    if (e.control || e.command || e.alt || char.IsControl(e.character)) { e.Use(); return; }
                    for (int step = 1; step <= Nodes.Count; step++)
                    {
                        int index = (Mathf.Max(-1, Selected) + step) % Nodes.Count; var node = Nodes[index];
                        if (node.Enabled && node.Label.Length > 0 && char.ToUpperInvariant(node.Label[0]) == char.ToUpperInvariant(e.character)) { Select(index, false); break; }
                    }
                    break;
            }
            e.Use(); Repaint();
        }
        void OnGUI()
        {
            if (Session == null || Session != PaintMenuSession.Current) return;
            var e = Event.current;
            if (e.type == EventType.KeyDown) { Key(e); return; }
            var body = Body;
            if (e.type == EventType.MouseMove)
            {
                Repaint();
                var screen = GUIUtility.GUIToScreenPoint(e.mousePosition);
                if (Session.Bar != null && Session.Bar.ScreenRects.Any(r => r.Contains(screen))) { Session.Bar.Pointer(screen, false); return; }
                for (int i = 0; i < Nodes.Count; i++) if (new Rect(body.x, body.y, PaintGui.ScrollContentWidth(body, Nodes.Sum(Height) + Padding * 2), body.height).Contains(e.mousePosition) && Row(i).Contains(e.mousePosition)) { Select(Nodes[i].Enabled ? i : -1, true); break; }
            }
            if (e.type == EventType.MouseDown && !body.Contains(e.mousePosition)) { e.Use(); Session.Close(); return; }
            Draw(new Rect(0, 0, position.width, position.height), Nodes, Selected, scroll);
            var positionInList = new Vector2(0, scroll);
            if (PaintGui.Scrollbar(body, ref positionInList, Nodes.Sum(Height) + Padding * 2, 16))
            { hoverIndex = -1; Session.TrimAfter(this); Repaint(); }
            scroll = positionInList.y;
            for (int i = 0; i < Nodes.Count; i++)
            {
                var node = Nodes[i]; var row = Row(i);
                if (!body.Contains(e.mousePosition) || !row.Contains(e.mousePosition) || !node.Enabled) continue;
                if (e.type == EventType.MouseDown && e.button == 0) { pressed = i; e.Use(); if (node.Children.Count > 0) Session.Submenu(this, i, false); return; }
                if (e.type == EventType.MouseUp && e.button == 0) { bool run = pressed == i; pressed = -1; e.Use(); if (run && node.Children.Count == 0) Session.Execute(node); return; }
            }
            if (e.type == EventType.MouseUp) pressed = -1;
        }
        /// <summary>窓を開かずにも同じ部品を描ける（オフスクリーンの見た目の検証）。</summary>
        internal static void Draw(Rect rect, List<PaintMenu.Node> nodes, int selected, float scroll = 0)
        {
            if (Event.current.type != EventType.Repaint) return;
            for (int i = 5; i >= 1; i--) PaintGui.Rounded(new Rect(rect.x + Margin - i, rect.y + Margin - i + 2, rect.width - Margin * 2 + i * 2, rect.height - Margin * 2 + i * 2), new Color(0, 0, 0, .055f), 8 + i);
            var body = new Rect(rect.x + Margin, rect.y + Margin, rect.width - Margin * 2, rect.height - Margin * 2);
            PaintGui.Rounded(body, PaintTheme.PanelBg, 8); PaintGui.Outline(body, PaintTheme.Separator, 1, 8);
            GUI.BeginClip(body);
            float y = Padding - scroll;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i]; var row = new Rect(4, y, PaintGui.ScrollContentWidth(body, nodes.Sum(Height) + Padding * 2) - 8, Height(n)); y += row.height;
                if (n.Separator) { PaintGui.HLine(row.x + 8, row.xMax - 8, row.center.y, PaintTheme.Separator); continue; }
                PaintGui.Tooltip(row, n.Item?.content.tooltip);
                Color color = n.Enabled ? PaintTheme.Text : n.Heading ? PaintTheme.TextDim : PaintTheme.TextDisabled;
                if (i == selected && n.Enabled) PaintGui.Rounded(row, PaintTheme.ControlHover, 4);
                if (n.Item?.on == true) PaintGui.Text(new Rect(row.x + 4, row.y, 20, row.height), n.Item.radio ? "●" : "✓", PaintTheme.LabelCenter, color);
                float keyWidth = PaintGui.TextWidth(n.Shortcut ?? "", PaintTheme.LabelSmall);
                PaintGui.Text(new Rect(row.x + 28, row.y, Mathf.Max(0, row.width - 48 - (keyWidth > 0 ? keyWidth + 24 : 0)), row.height), n.Label, n.Heading ? PaintTheme.Header : PaintTheme.Label, color);
                if (keyWidth > 0) PaintGui.Text(new Rect(row.xMax - 18 - keyWidth, row.y, keyWidth, row.height), n.Shortcut, PaintTheme.LabelSmall, n.Enabled ? PaintTheme.TextDim : PaintTheme.TextDisabled);
                if (n.Children.Count > 0) PaintGui.Text(new Rect(row.xMax - 18, row.y, 16, row.height), "›", PaintTheme.LabelCenter, color);
            }
            GUI.EndClip();
        }
    }
}
