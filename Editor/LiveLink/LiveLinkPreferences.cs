using System;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>Preferences &gt; YoluPainter: このパッケージの画面の言語、スタンドアロンの YoluPainter の実行ファイルの場所と書き出しの置き場の元
    /// （この PC の自分だけの設定。EditorPrefs）。Windows の実行ファイルはインストーラーが書く場所を先に使うので、空でよい。</summary>
    internal sealed class LiveLinkPreferences : SettingsProvider
    {
        LiveLinkPreferences() : base("Preferences/YoluPainter", SettingsScope.User, new[] { "YoluPainter", "Live Link", "language", "standalone", "executable", "export" }) { }

        [SettingsProvider] public static SettingsProvider Create() => new LiveLinkPreferences();

        public override void OnGUI(string searchContext)
        {
            EditorGUIUtility.labelWidth = 220;
            DrawLanguage();
            LiveLinkWindow.DrawExecutable();
            LiveLinkWindow.DrawExportFolder();
        }

        /// <summary>言語の欄の選び（並びの順）。</summary>
        internal static readonly PainterLanguage[] Languages = { PainterLanguage.Auto, PainterLanguage.Japanese, PainterLanguage.English };

        /// <summary>言語の欄の選びの名前。言語の名前はその言語で書き、どの言語の画面からも読めるようにする（訳さない）。</summary>
        internal static GUIContent[] LanguageNames() => new[] { new GUIContent(L.Tr("Automatic")), new GUIContent("日本語"), new GUIContent("English") };

        /// <summary>言語の欄。変えると、開いている Live Link のウィンドウもすぐ描き直す（<see cref="L.LanguageChanged"/>）。</summary>
        internal static void DrawLanguage()
        {
            int now = Math.Max(0, Array.IndexOf(Languages, L.Language));
            int next = EditorGUILayout.Popup(L.Content("Language", "The language of YoluPainter in Unity. Automatic follows the language of the operating system."), now, LanguageNames());
            if (next != now && next >= 0 && next < Languages.Length) L.Language = Languages[next];
        }
    }
}
