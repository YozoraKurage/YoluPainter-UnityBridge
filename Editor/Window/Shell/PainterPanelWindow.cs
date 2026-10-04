using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// ドックのパネル（またはタブのまとまり）を出した別のウィンドウ。既定は Unity の窓より手前のユーティリティ窓。普通の窓へ
    /// 切り替えると Unity のレイアウトにドッキングできる。中身は持ち主の <see cref="TexturePaintWindow"/> がドックと同じ描き方で描き、文書・選択・Undo は
    /// 持ち主のもの。
    /// <list type="bullet">
    /// <item>描き手が閉じると、パネルは持ち主の列（出す前の位置）に戻る。持ち主の窓を閉じるとこのウィンドウも閉じる（配置には別のウィンドウの
    /// まま覚え、次に持ち主を開いたときにまた出す）。</item>
    /// <item>ドメインのリロードでは持ち主への参照がそのまま残る。Unity の再起動やレイアウトの復元で参照を失ったら <see cref="Reconnect"/> で
    /// 探し直す: 持ち主の名前（<see cref="TexturePaintWindow.PanelOwnerKey"/>）が同じ窓、無ければ開いているペイントの窓が 1 つだけならそれ。
    /// 窓が無い・複数あって決まらないときは案内と、開く・選ぶ・閉じるボタンを出す。持ち主が別のウィンドウにしていないパネルなら閉じる。</item>
    /// </list>
    /// </summary>
    internal sealed class PainterPanelWindow : EditorWindow, IPainterShortcutScope
    {
        public const float MinHeight = 120;
        [SerializeField] TexturePaintWindow owner;
        [SerializeField] string ownerKey, groupId;
        [SerializeField] bool dockableWindow;
        [SerializeField] List<string> shownPanels = new List<string>();
        /// <summary>true なら閉じても配置を変えない（持ち主が閉じた・配置の側で閉じた）。</summary>
        bool leaveLayout;
        long drawnStamp = long.MinValue;
        Vector2 screenOrigin;
        /// <summary>今のキーが届いたとき、このウィンドウの文字の欄で入力中だったか（ShortcutGuard に知らせる）。</summary>
        bool editingText;
        /// <summary>持ち主のショートカットとして使ったキー（ShortcutGuard が確かめたら消す）。</summary>
        KeyCode tookKey; EventModifiers tookModifiers;
        static readonly List<PainterPanelWindow> open = new List<PainterPanelWindow>();

        /// <summary>Unity を終えるところ（ウィンドウが壊されても配置を変えない）。</summary>
        internal static bool Quitting { get; private set; }
        [InitializeOnLoadMethod] static void WatchQuit() => EditorApplication.quitting += () => Quitting = true;

        internal static IReadOnlyList<PainterPanelWindow> All => open.Where(w => w != null).ToList();
        internal TexturePaintWindow Owner => owner;
        internal string OwnerKey => ownerKey;
        internal string GroupId => groupId;
        internal bool DockableWindow => dockableWindow;
        internal IReadOnlyList<string> ShownPanels => shownPanels;
        /// <summary>GUI の原点のスクリーン座標（直前の Repaint の。テストでドラッグの位置を決めるのに使う）。</summary>
        internal Vector2 ScreenOriginForTests => screenOrigin;

        internal enum Link { Attached, NoPainter, Ambiguous, NothingToShow }

        // ショートカットのガード（ShortcutGuard）: このウィンドウにフォーカスがあるときも、持ち主の窓と同じに扱う。文字の欄で入力中か、
        // 持ち主へ回したキーを持ち主がショートカットとして使ったかを知らせる（持ち主の TexturePaintWindow.Keys.cs と同じ決まり）
        bool IPainterShortcutScope.EditingText => editingText;
        bool IPainterShortcutScope.TookKey(KeyCode key, EventModifiers modifiers)
        {
            bool took = tookKey != KeyCode.None && key == tookKey && Mods(modifiers) == Mods(tookModifiers);
            tookKey = KeyCode.None; return took;
        }
        static EventModifiers Mods(EventModifiers m) => m & (EventModifiers.Control | EventModifiers.Command | EventModifiers.Shift | EventModifiers.Alt);
        /// <summary>持ち主へ回したキーを持ち主が使った（<see cref="TexturePaintWindow.ForwardPanelWindowKey"/> から）。</summary>
        internal void NoteTookKey(Event e) { tookKey = e.keyCode; tookModifiers = e.modifiers; }

        /// <summary>まとまり g を映す別のウィンドウを、保存した窓モードで開く。</summary>
        internal static PainterPanelWindow Open(TexturePaintWindow owner, DockGroup g, Rect at)
        {
            var w = CreateInstance<PainterPanelWindow>();
            w.Attach(owner, g);
            w.dockableWindow = g.dockableWindow;
            w.position = at;
            if (w.dockableWindow) w.Show(); else w.ShowUtility();
            w.position = at;
            return w;
        }

        internal void Attach(TexturePaintWindow painter, DockGroup g)
        {
            owner = painter; ownerKey = painter.PanelOwnerKey; groupId = g.id; shownPanels = new List<string>(g.panels);
            UpdateTitle(g);
            Repaint();
        }

        /// <summary>ウィンドウのタブの名前（まとまりのパネルの名前を並べる）。</summary>
        internal void UpdateTitle(DockGroup g)
        {
            string title = string.Join(" · ", g.panels.Select(TexturePaintWindow.PanelTitle));
            if (titleContent == null || titleContent.text != title) titleContent = new GUIContent(title);
            if (!shownPanels.SequenceEqual(g.panels)) shownPanels = new List<string>(g.panels);
        }

        /// <summary>持ち主の窓が閉じるので閉じる（配置は変えない）。</summary>
        internal void CloseWithOwner() { if (owner != null) owner.RememberPanelWindowRect(this); leaveLayout = true; Close(); }
        /// <summary>配置の側でもう別のウィンドウでなくなったので閉じる（配置は変えない）。</summary>
        internal void CloseLeavingLayout() { leaveLayout = true; Close(); }

        void OnEnable()
        {
            if (!open.Contains(this)) open.Add(this);
            wantsMouseMove = true; minSize = new Vector2(DockLayout.MinWidth - 40, MinHeight);
            L.LanguageChanged += Repaint;
        }

        void OnDisable()
        {
            PaintMenuSession.CloseFor(this); open.Remove(this); L.LanguageChanged -= Repaint;
            if (owner != null && !leaveLayout && !Quitting) owner.RememberPanelWindowRect(this);
        }

        void OnDestroy()
        {
            if (leaveLayout || Quitting) return;
            if (owner != null) owner.PanelWindowClosed(this);
            else DockInStoredLayout();
        }

        /// <summary>持ち主の無いまま閉じた: 覚えている配置の上でまとまりを列に戻す（次にペイントの窓を開いたとき列にある）。</summary>
        void DockInStoredLayout()
        {
            var layout = DockLayoutStore.Load();
            if (layout.HasGroup(groupId) && layout.Dock(groupId)) DockLayoutStore.Save(layout);
        }

        /// <summary>持ち主を探し直す（上の決まり）。見つけたらつなぎ、見つからなければその訳を返す。</summary>
        internal Link Reconnect()
        {
            if (owner != null) return Link.Attached;
            var chosen = ChooseOwner(Painters(), ownerKey, out var why);
            if (chosen == null) return why;
            return chosen.AdoptPanelWindow(this) ? Link.Attached : Link.NothingToShow;
        }

        /// <summary>持ち主の決まり: 名前（key）が同じ窓、無ければ窓が 1 つだけならそれ。決まらなければ null と、その訳（窓が無い・複数）。</summary>
        internal static TexturePaintWindow ChooseOwner(IReadOnlyList<TexturePaintWindow> painters, string key, out Link why)
        {
            var chosen = painters.FirstOrDefault(p => !string.IsNullOrEmpty(key) && p.PanelOwnerKey == key) ?? (painters.Count == 1 ? painters[0] : null);
            why = chosen != null ? Link.Attached : painters.Count == 0 ? Link.NoPainter : Link.Ambiguous;
            return chosen;
        }
        static List<TexturePaintWindow> Painters() => Resources.FindObjectsOfTypeAll<TexturePaintWindow>().Where(p => p != null).ToList();

        /// <summary>テスト用: 表示していない（Show の前の）ウィンドウを、配置を変えずに壊す（Close は表示した窓にしか使えない）。</summary>
        internal void DestroyUnshownForTests() { leaveLayout = true; DestroyImmediate(this); }

        /// <summary>テスト用: 持ち主への参照を失わせる（Unity の再起動の後と同じ）。forgetKey なら持ち主の名前も。</summary>
        internal void ForgetOwnerForTests(bool forgetKey) { owner = null; if (forgetKey) ownerKey = null; }

        void OnGUI()
        {
            var e = Event.current; if (PaintMenuSession.HandleOwnerEvent(this, e)) return; var type = e.type;
            if (type == EventType.KeyDown) { editingText = GUIUtility.keyboardControl != 0; tookKey = KeyCode.None; }
            if (owner == null)
            {
                var link = Reconnect();
                if (link == Link.NothingToShow) { CloseSoon(); return; }
                if (link != Link.Attached) { DrawOrphan(link); return; }
            }
            if (type == EventType.MouseMove) Repaint(); // マウスの乗った部品の見た目
            if (type == EventType.Repaint) screenOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero);
            if (owner.DrawPanelWindow(this, new Rect(0, 0, position.width, position.height)) || this == null) return; // 落としたドラッグで閉じた
            // このウィンドウで何か操作したら、持ち主（キャンバス・3D）も描き直す
            if (e.type == EventType.Used && type != EventType.Layout && type != EventType.Repaint && type != EventType.MouseMove) { Repaint(); if (owner != null) owner.Repaint(); }
            if (type == EventType.Repaint && owner != null) drawnStamp = owner.PanelStamp;
        }

        /// <summary>持ち主が見えていない間に（Undo などで）持ち主の状態が変わったら描き直す。持ち主を失っていれば探し直す。</summary>
        void OnInspectorUpdate()
        {
            if (owner == null)
            {
                var link = Reconnect();
                if (link == Link.Attached) Repaint(); else if (link == Link.NothingToShow) CloseSoon();
                return;
            }
            if (owner.PanelStamp != drawnStamp) Repaint();
        }

        void CloseSoon() => EditorApplication.delayCall += () => { if (this != null) CloseLeavingLayout(); };

        /// <summary>持ち主が見つからないときの案内。</summary>
        void DrawOrphan(Link link)
        {
            var r = new Rect(0, 0, position.width, position.height);
            PaintGui.Fill(r, PaintTheme.PanelBg);
            var rows = new UiRows(r, 12);
            if (link == Link.NoPainter)
            {
                PaintGui.Notice(rows, L.Tr("The YoluPainter window this panel belongs to is not open."), "info", PaintTheme.TextDim);
                if (PaintGui.Button(rows.Row(24), L.Tr("Open YoluPainter"), true)) { TexturePaintWindow.Open(); Reconnect(); Repaint(); }
            }
            else
            {
                PaintGui.Notice(rows, L.Tr("Several YoluPainter windows are open."), "info", PaintTheme.TextDim);
                var painters = Painters();
                for (int i = 0; i < painters.Count; i++)
                {
                    var p = painters[i];
                    if (PaintGui.Button(rows.Row(24), L.Tr("Attach to {0}", p.titleContent.text + " (" + (i + 1) + ")")))
                    { if (!p.AdoptPanelWindow(this)) CloseSoon(); Repaint(); }
                }
            }
            rows.Space(6);
            if (PaintGui.Button(rows.Row(24), L.TrIn("panel", "Close"), false, true, L.Tr("The panel goes back to the dock."))) Close();
        }
    }
}
