using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ShortcutGuardTests の窓: 受け取ったキーを記録し、文字の入力中か・キーを使ったかをテストから決められる。</summary>
    public sealed class GuardProbeWindow : EditorWindow, IPainterShortcutScope
    {
        public string Seen = "";
        public bool Editing, Took;
        bool IPainterShortcutScope.EditingText => Editing;
        bool IPainterShortcutScope.TookKey(KeyCode key, EventModifiers modifiers) => Took;
        void OnGUI() { var e = Event.current; if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None) { Seen += e.keyCode + " "; e.Use(); } }
    }
}
