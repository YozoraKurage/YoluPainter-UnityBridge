using UnityEditor;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>StandalonePromptTests の窓: Preferences ▸ YoluPainter の入切（<see cref="StandalonePrompt.DrawPreference"/>）を描く。</summary>
    public sealed class PreferenceProbeWindow : EditorWindow
    {
        void OnGUI() => StandalonePrompt.DrawPreference();
    }
}
