using UnityEditor;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>Preferences &gt; YoluPainter: スタンドアロンの YoluPainter の実行ファイルの場所と書き出しの置き場の元（この PC の自分だけの設定。EditorPrefs）、
    /// スタンドアロン版のおすすめの入切（<see cref="StandalonePrompt"/>。既定は入）。Windows の実行ファイルはインストーラーが書く場所を先に使うので、空でよい。</summary>
    internal sealed class LiveLinkPreferences : SettingsProvider
    {
        LiveLinkPreferences() : base("Preferences/YoluPainter", SettingsScope.User, new[] { "YoluPainter", "Live Link", "standalone", "executable", "export", "suggest" }) { }

        [SettingsProvider] public static SettingsProvider Create() => new LiveLinkPreferences();

        public override void OnGUI(string searchContext)
        {
            EditorGUIUtility.labelWidth = 220;
            LiveLinkWindow.DrawExecutable();
            LiveLinkWindow.DrawExportFolder();
            StandalonePrompt.DrawPreference();
        }
    }
}
