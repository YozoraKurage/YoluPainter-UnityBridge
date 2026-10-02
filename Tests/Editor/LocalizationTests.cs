using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>テストの間は UI の言語を英語に固定する（設定には書かない）。メッセージについての確かめが、日本語の Unity で走らせても同じ
    /// 意味になるように。</summary>
    [SetUpFixture]
    public sealed class LanguagePin
    {
        [OneTimeSetUp] public void Pin() => L.OverrideLanguage(PainterLanguage.English);
        [OneTimeTearDown] public void Unpin() => L.OverrideLanguage(null);
    }

    /// <summary>UI の文字列表（.po）: 読み手の決まり、言語の切り替え、そして UI の文字列が日本語の表から漏れていないこと。</summary>
    public sealed class LocalizationTests
    {
        [TearDown] public void BackToEnglish() => L.OverrideLanguage(PainterLanguage.English);

        [Test] public void PoReadsEscapesContinuationsAndContexts()
        {
            var table = PoCatalog.Parse(string.Join("\n",
                "# comment", "msgid \"\"", "msgstr \"\"", "\"Language: ja\\n\"", "",
                "msgid \"Two\\nlines \\\"quoted\\\" \\\\ tab\\t\"", "msgstr \"\"", "\"二行\\n\"", "\"目\"", "",
                "msgctxt \"blend mode\"", "msgid \"Normal\"", "msgstr \"通常\"", "",
                "msgid \"Normal\"", "msgstr \"ノーマル\"", "",
                "msgid \"Untranslated\"", "msgstr \"\""));
            Assert.That(table["Two\nlines \"quoted\" \\ tab\t"], Is.EqualTo("二行\n目"));
            Assert.That(table["blend mode\u0004Normal"], Is.EqualTo("通常"));
            Assert.That(table["Normal"], Is.EqualTo("ノーマル"));
            Assert.That(table.ContainsKey(""), Is.False, "the header is not an entry");
            Assert.That(table["Untranslated"], Is.Empty);
        }

        [TestCase("msgid \"a\"\nmsgid_plural \"b\"\nmsgstr[0] \"c\"", "plural")]
        [TestCase("msgid \"a\nmsgstr \"b\"", "quoted")]
        [TestCase("msgstr \"b\"", "without msgid")]
        [TestCase("msgid \"a\\q\"\nmsgstr \"b\"", "unknown escape")]
        [TestCase("msgid \"a\"\nmsgstr \"b\"\nnonsense", "unexpected")]
        public void PoRefusesWhatItDoesNotUnderstand(string text, string message)
        {
            Assert.That(() => PoCatalog.Parse(text), Throws.TypeOf<InvalidDataException>().With.Message.Contains(message));
        }

        [Test] public void JapaneseIsUsedWhenChosenAndAnythingMissingStaysEnglish()
        {
            Assert.That(L.Tr("Layers"), Is.EqualTo("Layers"), "pinned to English for tests");
            L.OverrideLanguage(PainterLanguage.Japanese);
            Assert.That(L.IsJapanese, Is.True);
            Assert.That(L.Tr("Layers"), Is.EqualTo("レイヤー"));
            Assert.That(L.Tr("Normal"), Is.EqualTo("ノーマル"), "the channel");
            Assert.That(L.TrIn("blend mode", "Normal"), Is.EqualTo("通常"), "the blend mode");
            Assert.That(L.Tr("A string nobody translated"), Is.EqualTo("A string nobody translated"));
            Assert.That(L.Tr(""), Is.Empty);
            L.OverrideLanguage(PainterLanguage.English);
            Assert.That(L.TrIn("blend mode", "Normal"), Is.EqualTo("Normal"));
        }

        [Test] public void TheJapaneseCatalogHasNoDuplicateEntries()
        {
            // 原文は続きの行まで足して比べる（複数行の原文はどれも msgid "" で始まる）。分けた表どうしの重複も見る
            var keys = new List<string>(); string context = null, id = null;
            foreach (var line in PoCatalog.Files("ja").SelectMany(File.ReadAllLines))
            {
                if (line.StartsWith("msgctxt ", StringComparison.Ordinal)) context = line.Substring(8);
                else if (line.StartsWith("msgid ", StringComparison.Ordinal)) id = line.Substring(6);
                else if (line.StartsWith("\"", StringComparison.Ordinal) && id != null) id += line;
                else if (line.StartsWith("msgstr", StringComparison.Ordinal) && id != null) { if (id != "\"\"") keys.Add((context ?? "") + "|" + id); context = null; id = null; }
            }
            var duplicates = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.That(duplicates, Is.Empty, "a later entry silently replaces an earlier one");
        }

        // ───────── 漏れの検出 ─────────

        static readonly Regex Literal = new Regex(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        /// <summary>訳す口に直接渡した文字列（続けて + で足した文字列も）。変数を渡すものはここでは見えない（下で型から集める）。</summary>
        static readonly Regex Call = new Regex(
            @"(?:L\.Tr|L\.Content|Item\(\s*(?:m|menu)\s*,)\s*\(?\s*(?<lits>""(?:[^""\\]|\\.)*""(?:\s*\+\s*""(?:[^""\\]|\\.)*"")*)", RegexOptions.Compiled);
        static readonly Regex InContext = new Regex(@"L\.TrIn\(\s*""(?<ctx>(?:[^""\\]|\\.)*)""\s*,\s*""(?<text>(?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

        static string Unescape(string s) => Regex.Replace(s, @"\\(.)", m => m.Groups[1].Value == "n" ? "\n" : m.Groups[1].Value == "t" ? "\t" : m.Groups[1].Value);

        [Test] public void EveryUiStringHasAJapaneseTranslation()
        {
            var catalog = PoCatalog.Load("ja");
            Assert.That(catalog, Is.Not.Empty, "ja.po did not load; the rest of this test would prove nothing");
            var wanted = new Dictionary<string, string>(); // キー → どこで使っているか
            string editor = Path.GetDirectoryName(PoCatalog.Folder());
            foreach (var file in Directory.GetFiles(editor, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file), name = Path.GetFileName(file);
                foreach (Match m in Call.Matches(text))
                {
                    string key = string.Concat(Literal.Matches(m.Groups["lits"].Value).Cast<Match>().Select(l => Unescape(l.Groups[1].Value)));
                    if (Regex.IsMatch(key, "[A-Za-z]") && !wanted.ContainsKey(key)) wanted[key] = name;
                }
                foreach (Match m in InContext.Matches(text)) wanted[Unescape(m.Groups["ctx"].Value) + "\u0004" + Unescape(m.Groups["text"].Value)] = name;
            }
            // 変数で渡しているもの: 表と列挙から集める
            var window = typeof(TexturePaintWindow);
            object Static(string field) => window.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) ?? throw new MissingMemberException(window.Name, field);
            foreach (var title in (string[])Static("MenuTitles")) wanted[title] = "MenuTitles";
            foreach (var slot in ((Array)Static("ToolSlots")).Cast<object>().Where(s => s != null)) wanted[(string)slot.GetType().GetField("Name").GetValue(slot)] = "ToolSlots";
            foreach (var table in new[] { "AdjustmentMenu", "FilterMenu" })
                foreach (var entry in ((Array)Static(table)).Cast<object>()) wanted[(string)entry.GetType().GetField("Item1").GetValue(entry)] = table;
            foreach (var help in new[] { "ShortcutHelp", "LimitsHelp" }) wanted[(string)Static(help)] = help;
            var blendName = window.GetMethod("BlendName", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (LayerBlendMode mode in Enum.GetValues(typeof(LayerBlendMode))) wanted["blend mode\u0004" + (string)blendName.Invoke(null, new object[] { mode })] = "BlendName";
            foreach (var type in new[] { typeof(PaintChannel), typeof(GradientShape), typeof(Resampling) })
                foreach (var n in Enum.GetNames(type)) wanted[n] = type.Name;

            var missing = wanted.Where(w => !catalog.TryGetValue(w.Key, out var ja) || string.IsNullOrEmpty(ja))
                .Select(w => "  " + w.Value + ": " + w.Key.Replace("\u0004", " | ").Replace("\n", "\\n")).OrderBy(s => s).ToList();
            Assert.That(missing, Is.Empty, missing.Count + " UI string(s) reach a Japanese user in English. Translate them in Editor/Localization/ja.po:\n" + string.Join("\n", missing));
        }
    }
}
