using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 「メッシュマップのベイク」の窓（Substance Painter の Baker と同じ配置）。中身は持ち主の <see cref="TexturePaintWindow"/> が描き
    /// （<see cref="TexturePaintWindow.DrawMeshBakeWindow"/>）、設定・焼いたマップ・ベイクの仕事は持ち主のもの。この窓が持つのは、左の一覧で
    /// 選んだ項目と、スクロールの位置だけ。持ち主 1 つにつき 1 枚。
    /// <list type="bullet">
    /// <item>持ち主の窓を閉じたら、この窓も閉じる（OnInspectorUpdate と OnGUI で確かめる）。走っていたベイクは持ち主が閉じるときに取り消す。</item>
    /// <item>ドメインのリロードでは持ち主への参照がそのまま残る。参照を失ったまま開いていたら（この読み込みでまだ持ち主につないでいない）、
    /// ペイントの窓が 1 つだけならそれにつなぎ、無い・複数なら閉じる。</item>
    /// <item>この窓を閉じてもベイクは続く（プロパティの欄のメッシュマップの区画に進み具合と取消が出る）。</item>
    /// </list>
    /// </summary>
    internal sealed class MeshBakeWindow : EditorWindow, IPainterShortcutScope
    {
        /// <summary>左の一覧の「共通の設定」。ほかの値は <see cref="Core.MeshMaps.MeshMapKind"/> の値。</summary>
        internal const int CommonPage = -1;
        internal const float MinWidth = 640, MinHeight = 460, DefaultWidth = 780, DefaultHeight = 600;

        [SerializeField] TexturePaintWindow owner;
        /// <summary>Show した窓か（Close は表示した窓にしか使えない。テストは表示しない窓を作る）。</summary>
        [SerializeField] bool shown;
        [SerializeField] int page = CommonPage;
        [SerializeField] internal Vector2 ListScroll, PageScroll;
        /// <summary>左の一覧と右の設定の中身の高さ（Repaint で測る。はみ出したらスクロール）。</summary>
        internal float ListHeight = 200, PageHeight = 200;
        /// <summary>この読み込み（ドメイン）のあいだに持ち主につないだか。つないだ後で持ち主が無くなったら、持ち主が閉じたということ。</summary>
        bool hadOwner;
        bool closing, editingText;
        /// <summary>部品の画面上の矩形（Repaint のたびに覚え直す）。テストが本物のマウスの入力で押すため。</summary>
        internal readonly Dictionary<string, Rect> ControlScreenRects = new Dictionary<string, Rect>();
        static readonly List<MeshBakeWindow> open = new List<MeshBakeWindow>();

        internal TexturePaintWindow Owner => owner;
        internal bool Closing => closing;
        /// <summary>右に出している項目（<see cref="CommonPage"/> か、マップの種類の値）。</summary>
        internal int Page { get => page; set { if (page != value) PageScroll = Vector2.zero; page = value; Repaint(); } }

        /// <summary>その持ち主の窓（開いていなければ null）。</summary>
        internal static MeshBakeWindow For(TexturePaintWindow painter)
        {
            if (painter == null) return null;
            foreach (var w in open) if (w != null && !w.closing && w.owner == painter) return w;
            return null;
        }

        /// <summary>持ち主の窓を開く（開いていれば前に出す）。持ち主の窓の真ん中に、Unity の浮いた窓として出す。</summary>
        internal static MeshBakeWindow Open(TexturePaintWindow painter)
        {
            if (painter == null) throw new ArgumentNullException(nameof(painter));
            var w = For(painter);
            if (w == null)
            {
                w = CreateInstance<MeshBakeWindow>();
                w.Attach(painter);
                w.shown = true;
                w.ShowUtility();
                var at = painter.position;
                w.position = new Rect(Mathf.Round(at.center.x - DefaultWidth / 2), Mathf.Round(at.center.y - DefaultHeight / 2), DefaultWidth, DefaultHeight);
            }
            w.Focus();
            return w;
        }

        internal void Attach(TexturePaintWindow painter)
        {
            owner = painter; hadOwner = painter != null; closing = false;
            UpdateTitle(); Repaint();
        }

        void UpdateTitle() { titleContent = new GUIContent(L.Tr("Bake Mesh Maps")); }

        void OnEnable()
        {
            if (!open.Contains(this)) open.Add(this);
            wantsMouseMove = true; minSize = new Vector2(MinWidth, MinHeight);
            L.LanguageChanged += LanguageChanged;
            UpdateTitle();
        }

        void OnDisable() { open.Remove(this); L.LanguageChanged -= LanguageChanged; }

        void LanguageChanged() { UpdateTitle(); Repaint(); }

        /// <summary>1 秒に 10 回: 持ち主がまだあるか確かめ、ベイクの最中は進み具合を描き直す。</summary>
        void OnInspectorUpdate()
        {
            if (EnsureOwner(false) && owner.IsBakingMeshMaps) Repaint();
        }

        internal enum Link { Attached, Adopted, Close }

        /// <summary>
        /// 持ち主の決まり: 持ち主があればそのまま。この読み込みで持ち主につないだ後で無くなったなら、持ち主が閉じたので閉じる。まだつないで
        /// いない（参照を失ったまま開いた）なら、ペイントの窓が 1 つだけならそれ、無い・複数なら閉じる（どの文書のベイクか決まらない）。
        /// </summary>
        internal static Link ChooseOwner(TexturePaintWindow owner, bool hadOwner, IReadOnlyList<TexturePaintWindow> painters, out TexturePaintWindow chosen)
        {
            chosen = owner;
            if (owner != null) return Link.Attached;
            if (hadOwner || painters == null || painters.Count != 1) { chosen = null; return Link.Close; }
            chosen = painters[0];
            return Link.Adopted;
        }

        /// <summary>持ち主を確かめる（上の決まり）。閉じるなら閉じて false。inGui なら閉じるのは次の更新で（OnGUI の途中では閉じない）。</summary>
        internal bool EnsureOwner(bool inGui)
        {
            if (closing) return false;
            var painters = owner == null && !hadOwner ? Resources.FindObjectsOfTypeAll<TexturePaintWindow>().Where(p => p != null).ToList() : null;
            switch (ChooseOwner(owner, hadOwner, painters, out var chosen))
            {
                case Link.Attached: hadOwner = true; return true;
                case Link.Adopted: Attach(chosen); return true;
                default:
                    closing = true;
                    if (inGui) EditorApplication.delayCall += CloseNow; else CloseNow();
                    return false;
            }
        }

        /// <summary>次の更新で閉じる（閉じるボタン・Esc から）。</summary>
        internal void CloseSoon()
        {
            if (closing) return;
            closing = true; EditorApplication.delayCall += CloseNow;
        }

        void CloseNow()
        {
            if (this == null) return;
            closing = true;
            if (shown) Close(); else DestroyImmediate(this);
        }

        /// <summary>テスト用: 持ち主への参照を失わせる（Unity の再起動やレイアウトの復元の後と同じ。この読み込みではまだつないでいない扱い）。</summary>
        internal void ForgetOwnerForTests() { owner = null; hadOwner = false; }

        bool IPainterShortcutScope.EditingText => editingText;
        bool IPainterShortcutScope.TookKey(KeyCode key, EventModifiers modifiers) => false;

        void OnGUI()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown) editingText = GUIUtility.keyboardControl != 0;
            if (!EnsureOwner(true)) return;
            if (e.type == EventType.MouseMove) Repaint();
            // Esc: 焼いている間は取消、そうでなければ閉じる（数値の欄で入力中なら、その欄の取消に任せる）
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && GUIUtility.keyboardControl == 0)
            {
                if (owner.IsBakingMeshMaps) owner.CancelMeshBake(); else CloseSoon();
                e.Use(); return;
            }
            if (e.type == EventType.Repaint) ControlScreenRects.Clear();
            owner.DrawMeshBakeWindow(new Rect(0, 0, position.width, position.height), this);
        }

        /// <summary>部品の矩形を覚える（テスト用）。r をそのまま返す。</summary>
        internal Rect Spot(string id, Rect r)
        {
            if (Event.current != null && Event.current.type == EventType.Repaint) ControlScreenRects[id] = GUIUtility.GUIToScreenRect(r);
            return r;
        }
    }
}
