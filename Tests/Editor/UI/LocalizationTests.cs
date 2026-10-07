using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.LiveLink;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>テストの間は UI の言語を英語に固定する（設定には書かない）。メッセージについての確かめが、日本語の Unity で走らせても
    /// 同じ意味になるように。</summary>
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
            Assert.That(L.Tr("Open in YoluPainter"), Is.EqualTo("Open in YoluPainter"), "pinned to English for tests");
            L.OverrideLanguage(PainterLanguage.Japanese);
            Assert.That(L.IsJapanese, Is.True);
            Assert.That(L.Tr("Open in YoluPainter"), Is.EqualTo("YoluPainter で開く"));
            Assert.That(L.Tr("Language"), Is.EqualTo("言語"));
            Assert.That(L.TrIn("LiveLink", "Choose…"), Is.EqualTo("選ぶ…"), "an entry with a context");
            Assert.That(L.Tr("Choose…"), Is.EqualTo("Choose…"), "the same text without the context has no entry");
            Assert.That(L.Tr("A string nobody translated"), Is.EqualTo("A string nobody translated"));
            Assert.That(L.Tr(""), Is.Empty);
            L.OverrideLanguage(PainterLanguage.English);
            Assert.That(L.TrIn("LiveLink", "Choose…"), Is.EqualTo("Choose…"));
        }

        /// <summary>Preferences ▸ YoluPainter の言語の欄: 選びの並びが言語の値と 1 対 1 で、言語の名前はその言語のまま（どの言語の画面からも読める）、
        /// 「自動」は今の言語で出る。</summary>
        [Test] public void ThePreferencesLanguageFieldListsEveryLanguageOnceWithItsOwnName()
        {
            var languages = LiveLinkPreferences.Languages;
            Assert.That(languages, Is.EquivalentTo(Enum.GetValues(typeof(PainterLanguage))), "every language can be chosen");
            Assert.That(languages.Distinct().Count(), Is.EqualTo(languages.Length));
            // 画面で選んだ i 番目がそのまま保存される値になる（DrawLanguage は Languages[i] を L.Language に入れる）ので、並びを固定して名前と対で見る。
            Assert.That(languages, Is.EqualTo(new[] { PainterLanguage.Auto, PainterLanguage.Japanese, PainterLanguage.English }), "the order of the field, the default first");
            var english = LiveLinkPreferences.LanguageNames().Select(c => c.text).ToArray();
            L.OverrideLanguage(PainterLanguage.Japanese);
            var japanese = LiveLinkPreferences.LanguageNames().Select(c => c.text).ToArray();
            L.OverrideLanguage(PainterLanguage.English);
            Assert.That(english, Has.Length.EqualTo(languages.Length));
            Assert.That(japanese, Has.Length.EqualTo(languages.Length));
            var namesByLanguage = new Dictionary<PainterLanguage, (string inEnglish, string inJapanese)>
            {
                { PainterLanguage.Auto, ("Automatic", "自動") },
                { PainterLanguage.Japanese, ("日本語", "日本語") },
                { PainterLanguage.English, ("English", "English") },
            };
            for (int i = 0; i < languages.Length; i++)
            {
                Assert.That((english[i], japanese[i]), Is.EqualTo(namesByLanguage[languages[i]]), $"the name at {i} belongs to {languages[i]}");
            }
        }

        /// <summary>選んだ言語は EditorPrefs に残り、その値に従う（試験だけの言語はその上に効く）。知らない値は自動として読む。試験の間だけ値を変え、
        /// 元の値（無ければ無いこと）に戻す。</summary>
        [Test] public void TheChosenLanguageIsKeptAndTheTestOverrideWinsOverIt()
        {
            const string key = "Yozolab.YoluPainter.Language";
            bool had = EditorPrefs.HasKey(key); int saved = EditorPrefs.GetInt(key, 0);
            try
            {
                L.OverrideLanguage(null);
                L.Language = PainterLanguage.Japanese;
                Assert.That(EditorPrefs.GetInt(key, -1), Is.EqualTo((int)PainterLanguage.Japanese));
                Assert.That(L.IsJapanese, Is.True);
                L.Language = PainterLanguage.English;
                Assert.That(L.IsJapanese, Is.False);
                L.Language = PainterLanguage.Auto;
                Assert.That(L.IsJapanese, Is.EqualTo(Application.systemLanguage == SystemLanguage.Japanese), "automatic follows the operating system");
                EditorPrefs.SetInt(key, 99);
                Assert.That(L.Language, Is.EqualTo(PainterLanguage.Auto), "an unknown value reads as automatic");
                L.Language = PainterLanguage.English;
                L.OverrideLanguage(PainterLanguage.Japanese);
                Assert.That(L.IsJapanese, Is.True, "the override for the tests wins over the stored choice");
            }
            finally
            {
                if (had) EditorPrefs.SetInt(key, saved); else EditorPrefs.DeleteKey(key);
                L.OverrideLanguage(PainterLanguage.English);
            }
        }

        /// <summary>パッケージの情報（PackageInfo）が取れない配置（Assets への複製など）でも、アセンブリ定義の場所からパッケージの根を求めて、表を読める。</summary>
        [Test] public void TheCatalogIsFoundFromTheAssemblyDefinitionWhenNoPackageInformationIsAvailable()
        {
            var savedInfo = PackagePaths.InfoAssetPath;
            try
            {
                PackagePaths.InfoAssetPath = () => null; PackagePaths.Reset();
                string fromAssembly = PackagePaths.FromAssemblyDefinition();
                Assert.That(fromAssembly, Is.Not.Null, "the assembly definition is found by its name");
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PackagePaths).Assembly);
                if (info != null) Assert.That(fromAssembly, Is.EqualTo(info.assetPath), "it leads to the same root as the package information");
                Assert.That(PackagePaths.Root, Is.EqualTo(fromAssembly));
                Assert.That(PoCatalog.Folder(), Is.Not.Null);
                Assert.That(Path.GetFullPath(PoCatalog.Folder()), Is.Not.EqualTo(Path.GetFullPath(PoCatalog.Location)), "not looked up under the project folder");
                Assert.That(PoCatalog.Load("ja"), Is.Not.Empty);
            }
            finally { PackagePaths.InfoAssetPath = savedInfo; PackagePaths.Reset(); }
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
            Assert.That(catalog, Is.Not.Empty, "the Japanese catalog did not load; the rest of this test would prove nothing");
            var wanted = new Dictionary<string, string>(); // キー → どこで使っているか
            string editor = PackagePaths.Physical("Editor");
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
            var missing = wanted.Where(w => !catalog.TryGetValue(w.Key, out var ja) || string.IsNullOrEmpty(ja))
                .Select(w => "  " + w.Value + ": " + w.Key.Replace("\u0004", " | ").Replace("\n", "\\n")).OrderBy(s => s).ToList();
            Assert.That(wanted.Count, Is.GreaterThan(40), "the scan found the strings of the Editor");
            Assert.That(missing, Is.Empty, missing.Count + " UI string(s) reach a Japanese user in English. Translate them in " + PoCatalog.Location + "/ja/:\n" + string.Join("\n", missing));
        }
    }
}
