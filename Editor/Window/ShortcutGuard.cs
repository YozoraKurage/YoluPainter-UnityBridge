using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>YoluPainter の窓にフォーカスがあるとき、Unity と他の拡張のショートカットをどこまで止めるか（個人の設定）。</summary>
    internal enum ShortcutGuardMode
    {
        /// <summary>すべて止める。YoluPainter の窓では YoluPainter のキーだけが効く。</summary>
        BlockAll = 0,
        /// <summary>YoluPainter が使ったキーだけ止める（ほかの Unity のショートカット、例えば Ctrl+P の再生は効く）。</summary>
        BlockPainterKeys = 1,
        /// <summary>止めない（Unity の既定のまま。YoluPainter が使ったキーでも、修飾キーの無いものは Unity のショートカットにも届く）。</summary>
        Off = 2,
    }

    /// <summary>ショートカットのガードを受ける YoluPainter の窓。</summary>
    internal interface IPainterShortcutScope
    {
        /// <summary>今のキーが届いたとき、窓の中の文字の欄で入力中だったか。</summary>
        bool EditingText { get; }
        /// <summary>このキー（KeyDown）を窓がショートカットとして使ったか。確かめると印は消える。</summary>
        bool TookKey(KeyCode key, EventModifiers modifiers);
    }

    /// <summary>
    /// YoluPainter の窓にフォーカスがあるあいだ、Unity の ShortcutManager（とそれに登録された他の拡張のショートカット）にキーを
    /// 渡さない。Unity はキーをまずフォーカスのある窓へ送り、そのあと全体のキー処理（EditorApplication.globalEventHandler、
    /// ShortcutManager がここで受ける）を呼ぶ。窓が Use() しても、修飾キーの無い文字のキー（W・E など）は全体の処理にも届き、
    /// シーンのツールが切り替わる（2026-10-03、コンテナの GUI モードで xdotool の実キーで実測）。また窓の中の文字の欄
    /// （GUI.TextField）での入力は EditorGUI の欄と違って入力中の印が立たないので、打った文字がショートカットとしても働く。
    /// そこで全体のキー処理の先頭に割り込み、止めるべきキーをそこで Use() する（窓はもう受け取った後なので、窓の操作は変わらない）。
    /// globalEventHandler は internal なので、見つからない版の Unity では何もしない（<see cref="Available"/>）。
    /// </summary>
    [InitializeOnLoad]
    internal static class ShortcutGuard
    {
        static readonly FieldInfo s_field = typeof(EditorApplication).GetField("globalEventHandler", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly EditorApplication.CallbackFunction s_guard = Guard;

        static ShortcutGuard() => Install();

        /// <summary>この版の Unity で割り込めるか。</summary>
        internal static bool Available => s_field != null && s_field.FieldType == typeof(EditorApplication.CallbackFunction);

        /// <summary>全体のキー処理の先頭に割り込めているか。</summary>
        internal static bool Installed
        {
            get
            {
                if (!Available) return false;
                var list = (s_field.GetValue(null) as Delegate)?.GetInvocationList();
                return list != null && list.Length > 0 && Equals(list[0], s_guard);
            }
        }

        /// <summary>止めたキーの数（テストと診断用）。</summary>
        internal static int BlockedCount { get; private set; }

        /// <summary>先頭に入れ直す（何度呼んでも 1 つだけ）。後から += で足された処理は後ろに付くので、先頭のままになる。</summary>
        internal static void Install()
        {
            if (!Available) return;
            var current = (EditorApplication.CallbackFunction)s_field.GetValue(null);
            current -= s_guard;
            s_field.SetValue(null, s_guard + current);
        }

        /// <summary>止めるか: 文字の入力中はいつも、それ以外は設定による。</summary>
        internal static bool ShouldBlock(ShortcutGuardMode mode, bool editingText, bool tookKey)
            => editingText || mode == ShortcutGuardMode.BlockAll || mode == ShortcutGuardMode.BlockPainterKeys && tookKey;

        static void Guard() { var e = Event.current; if (e != null) Filter(e, EditorWindow.focusedWindow); }

        /// <summary>全体のキー処理に渡ったキーを調べ、止めるなら Use() する（後ろの ShortcutManager は Use() 済みのイベントを無視する）。止めたら true。</summary>
        internal static bool Filter(Event e, EditorWindow focused)
        {
            if (e.type != EventType.KeyDown || !(focused is IPainterShortcutScope scope)) return false;
            bool took = scope.TookKey(e.keyCode, e.modifiers);
            if (!ShouldBlock(PainterSettings.ShortcutGuard, scope.EditingText, took)) return false;
            e.Use(); BlockedCount++; return true;
        }
    }
}
