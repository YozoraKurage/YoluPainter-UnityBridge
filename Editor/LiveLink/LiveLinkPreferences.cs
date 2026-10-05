using UnityEditor;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>Preferences &gt; YoluPainter: スタンドアロンの YoluPainter の実行ファイルの場所（この PC の自分だけの設定。EditorPrefs）と、
    /// スタンドアロン版のおすすめの入切（<see cref="StandalonePrompt"/>。既定は入）。
    /// Windows はインストーラーが書く場所を先に使うので、空でよい。ブリッジの DLL は読まない（開いただけで Live Link を使い始めない）。</summary>
    internal sealed class LiveLinkPreferences : SettingsProvider
    {
        LiveLinkPreferences() : base("Preferences/YoluPainter", SettingsScope.User, new[] { "YoluPainter", "Live Link", "standalone", "executable", "suggest" }) { }

        [SettingsProvider] public static SettingsProvider Create() => new LiveLinkPreferences();

        public override void OnGUI(string searchContext)
        {
            EditorGUIUtility.labelWidth = 220;
            LiveLinkWindow.DrawExecutable();
            StandalonePrompt.DrawPreference();
        }
    }
}
