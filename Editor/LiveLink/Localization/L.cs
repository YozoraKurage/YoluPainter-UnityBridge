using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>UI の言語。Auto は OS の言語（<see cref="Application.systemLanguage"/>）に従う。Preferences ▸ YoluPainter で選ぶ。</summary>
    internal enum PainterLanguage { Auto = 0, English = 1, Japanese = 2 }

    /// <summary>
    /// エディタの UI の文字列表。キーは英語の原文そのもの（呼ぶ側が読みやすく、訳の無い文字列は英語のまま出る）。訳は
    /// Editor/LiveLink/Localization/&lt;言語&gt;/*.po（gettext の形式）にあり、訳すのに C# は要らない。
    /// 言語を足すときは <see cref="PainterLanguage"/> と <see cref="LocaleCode"/> にも足す。
    /// </summary>
    internal static class L
    {
        const string PrefKey = "Yozolab.YoluPainter.Language";

        /// <summary>言語を変えたとき（UI は描き直す）。</summary>
        public static event Action LanguageChanged;

        public static PainterLanguage Language
        {
            get
            {
                var stored = (PainterLanguage)EditorPrefs.GetInt(PrefKey, (int)PainterLanguage.Auto);
                return Enum.IsDefined(typeof(PainterLanguage), stored) ? stored : PainterLanguage.Auto;
            }
            set
            {
                if (value == Language) return;
                EditorPrefs.SetInt(PrefKey, (int)value);
                s_locale = null; s_catalog = null;
                LanguageChanged?.Invoke();
            }
        }

        /// <summary>このセッションだけの言語（設定には書かない）。テストは英語に固定する: メッセージについての確かめは、日本語の
        /// エディタでも英語のエディタでも同じ意味でなければならず、そのために本物の設定を書き換えると、テストを走らせた人の設定を
        /// 変えてしまう（途中で落ちれば変えたまま残る）。</summary>
        public static void OverrideLanguage(PainterLanguage? language)
        {
            if (s_override == language) return;
            s_override = language; s_locale = null; s_catalog = null;
            LanguageChanged?.Invoke();
        }
        static PainterLanguage? s_override;

        /// <summary>今の言語の .po の名前（拡張子なし）。英語は "en"（表は空で、原文のまま）。</summary>
        public static string LocaleCode
        {
            get
            {
                if (s_locale == null)
                {
                    var language = s_override ?? Language;
                    bool japanese = language == PainterLanguage.Japanese || language == PainterLanguage.Auto && Application.systemLanguage == SystemLanguage.Japanese;
                    s_locale = japanese ? "ja" : "en";
                }
                return s_locale;
            }
        }
        static string s_locale;
        public static bool IsJapanese => LocaleCode == "ja";

        /// <summary><paramref name="english"/> を今の言語の表で引く。訳が無ければ英語の原文のまま（空にはしない）。</summary>
        public static string Tr(string english)
        {
            if (string.IsNullOrEmpty(english)) return english;
            return Catalog.TryGetValue(english, out var translated) && !string.IsNullOrEmpty(translated) ? translated : english;
        }
        public static string Tr(string english, params object[] args) => string.Format(Tr(english), args);
        /// <summary>同じ英語でも場面で訳が違うもの（gettext の msgctxt。例: 合成モードの "Normal" とチャンネルの "Normal"）。</summary>
        public static string TrIn(string context, string english)
        {
            if (string.IsNullOrEmpty(english)) return english;
            return Catalog.TryGetValue(context + "\u0004" + english, out var translated) && !string.IsNullOrEmpty(translated) ? translated : english;
        }
        /// <summary>訳した文字列と訳したツールチップの GUIContent。</summary>
        public static GUIContent Content(string english, string tooltip = null) => new GUIContent(Tr(english), tooltip == null ? null : Tr(tooltip));

        // ドメインのリロードごとに 1 回（と、言語の切り替え・.po の取り込み直しのあと）読む
        static Dictionary<string, string> s_catalog;
        static Dictionary<string, string> Catalog => s_catalog ?? (s_catalog = LocaleCode == "en" ? new Dictionary<string, string>() : PoCatalog.Load(LocaleCode));

        /// <summary>表を読み直す（.po を取り込み直したとき）。</summary>
        public static void ReloadCatalog() { s_catalog = null; LanguageChanged?.Invoke(); }
    }

    /// <summary>.po を取り込み直したら表を読み直す。</summary>
    internal sealed class PoCatalogPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var path in imported) if (path.EndsWith(".po", StringComparison.OrdinalIgnoreCase) && path.Contains("/Localization/")) { L.ReloadCatalog(); return; }
        }
    }
}
