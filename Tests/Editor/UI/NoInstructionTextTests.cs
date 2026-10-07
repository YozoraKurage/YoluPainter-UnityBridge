using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Yozolab.YoluPainter.Editor.LiveLink;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 画面に操作の説明文（「〜をクリックして点を追加」「ブラシはその上から塗ります」のような使い方の文、説明の段落、空の状態の案内）を置かない。
    /// 画面の文字は名前・状態・短い理由だけで、説明はツールチップに置く。ここでは、画面に常時出る文（状態・知らせ・段落・断った理由）の
    /// ソースの文字列が、操作の指示の言い回しを含まないことを確かめる。
    /// 見る範囲: Editor のソースで、状態（message）・知らせ（Notice・NoteRow・Paragraph）・断った理由（why・reason・note）・
    /// return する文・throw する例外の文・notes / remarks / refusals / warnings / problems への Add を含む文の、文字列リテラルすべてと、その日本語訳。
    /// 見ない範囲: ツールチップ、ダイアログの確かめの文（疑問文は除く）、ボタンやメニューの名前、上の書き方に当たらない組み立て方の文
    /// （変数に入れてから別の場所で出す文など）。
    /// 文として判定するのは「。で終わる」か「。のあとに続きがある」文字列だけ（名前や状態の短い語は見ない）。
    /// </summary>
    public sealed class NoInstructionTextTests
    {
        /// <summary>操作の指示の言葉（英語は単語、日本語は語）。</summary>
        static readonly Regex Words = new Regex(@"\b(click|drag|press|tap|double-click|right-click)\b|\bhold\s+(?:down|[A-Z]\b|Alt|Ctrl|Shift|the\b)|\byou\b|\bplease\b|クリック|ドラッグ|押し|押す|ください|選ぶと", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        const string Verbs = "Read|Place|Choose|Select|Wait|Make|Turn|Check|Unlock|Bake|Pick|Enter|Type|Switch|Load|Enable|Disable|Set|Add|Use|Move|Merge|Finish|Show|Hide|Rasterize|Redraw|Reset|Plan|Try|Reduce|Remove|Open|Close|Apply|Copy|Edit|Paint|Restore|Delete|Create|Reload|Export|Rename|Fix";
        /// <summary>命令形で始まる文（文の頭・「, or」・「;」のあとの動詞）と、「…を先に」「もう一度…」の案内。</summary>
        static readonly Regex Imperative = new Regex(@"(^|[.;:!?]\s+|,\s+or\s+|;\s+)(?:" + Verbs + @")\b", RegexOptions.CultureInvariant);
        static readonly Regex ImperativeClause = new Regex(@"[;,]\s+(?:" + Verbs.ToLowerInvariant() + @")\b", RegexOptions.CultureInvariant);
        static readonly Regex Tail = new Regex(@"\bfirst\W*$|\bfirst\b[.,;]|(?:^|[(;,]\s*|\b(?:or|and|then)\s+)(?i:bake|plan|set|redraw|reset|try|choose|select)\b[^.;:]*\bagain\b", RegexOptions.CultureInvariant);
        static readonly Regex SentenceSplit = new Regex(@"(?<=[.;:!?])\s+", RegexOptions.CultureInvariant);

        /// <summary>画面に出る文として画面の文字に使ってよいか（操作の指示でないか）。<paramref name="sentence"/> なら、命令形・「先に」「もう一度」の案内も見る。</summary>
        internal static bool LooksLikeInstruction(string text, bool sentence = false)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (Words.IsMatch(text)) return true;
            if (!sentence) return false;
            text = text.Trim().TrimStart('.', ';', ':', ' ');
            if (!(text.EndsWith(".") || text.Contains(". "))) return false; // 名前や状態の短い語は文として見ない
            if (!text.EndsWith("?") && ImperativeClause.IsMatch(text)) return true; // 「…; bake after it is applied」のように、文の途中の「; 動詞」
            foreach (var part in SentenceSplit.Split(text))
            {
                if (part.EndsWith("?")) continue; // 確かめの疑問文（ダイアログ）
                if (Imperative.IsMatch(part) || Tail.IsMatch(part)) return true;
            }
            return false;
        }

        /// <summary>指示の言い回しを含むが、断った理由や起きたことを言っているだけの文（原文）。新しい文が引っかかったら、使い方の説明でないかを確かめ、
        /// 説明なら状態か理由に書き直す（ここに足すのは、断った理由・起きたこと・確かめの文だけ）。</summary>
        static readonly string[] Allowed =
        {
            "The view does not rotate or flip during a stroke or a drag.", // 回転・反転を断った理由
            "The shape drag was cancelled; the shape is where it was.",    // 取り消したという状態
            "Resized texture sets are resampled when you apply; their undo history is cleared.", // 適用すると何が変わるか（確かめの警告）
        };

        /// <summary>Editor のソースで文を出す所: 状態・知らせ・断った理由・return・throw・notes 系への Add。</summary>
        static readonly Regex EditorSite = new Regex(@"\w*(?:notes|remarks|refusals|warnings|problems)\w*\??\.Add\(|\bmessage\s*\+?=(?!=)|\bNotice\(|\bNoteRow\(|\bParagraph\(|\bwhy\s*=|\breason\s*=|\bnote\s*=|\breturn\s+L\.Tr|throw new \w+\(");
        static readonly Regex Literal = new Regex(@"""((?:[^""\\]|\\.)*)""");

        /// <summary>folder の下の .cs で、site に当たる行から文の終わり（;）までの文字列リテラルを返す（ファイル名:行: 原文）。</summary>
        static IEnumerable<(string where, string text)> SiteLiterals(string folder, Regex site)
        {
            foreach (var file in Directory.GetFiles(PackagePaths.Physical(folder), "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith("//")) continue;
                    var at = site.Match(lines[i]);
                    if (!at.Success) continue;
                    string statement = lines[i].Substring(at.Index);
                    for (int j = i; !lines[j].Contains(";") && j < i + 4 && j + 1 < lines.Length; j++) statement += "\n" + lines[j + 1]; // 改行で続く文
                    foreach (Match m in Literal.Matches(statement))
                    {
                        string text = Regex.Unescape(m.Groups[1].Value);
                        if (text.Length >= 6 && text.Any(char.IsLetter) && !Allowed.Contains(text)) yield return (Path.GetFileName(file) + ":" + (i + 1), text);
                    }
                }
            }
        }

        /// <summary>状態・知らせ・段落・断った理由・例外の文に出す文の原文と日本語が、操作の指示の言い回しを含まない。</summary>
        [Test] public void StatusNoticeParagraphAndRefusalTextsAreStatesAndReasonsNotInstructions()
        {
            var found = new List<string>(); int count = 0;
            foreach (var (where, text) in SiteLiterals("Editor", EditorSite))
            {
                count++;
                L.OverrideLanguage(PainterLanguage.English);
                bool bad = LooksLikeInstruction(text, true);
                L.OverrideLanguage(PainterLanguage.Japanese);
                string ja = L.Tr(text);
                L.OverrideLanguage(PainterLanguage.English);
                if (bad || ja != text && LooksLikeInstruction(ja)) found.Add("Editor/" + where + ": " + text + (ja != text ? "  /  " + ja : ""));
            }
            Assert.That(count, Is.GreaterThan(40), "the scan found the status, notice, return and throw sites of the Editor");
            Assert.That(found, Is.Empty, "a status, notice, paragraph, refusal or exception text reads like operating instructions (put the how-to in a tooltip; show only a state or a short reason):\n" + string.Join("\n", found));
        }

        [TearDown] public void BackToEnglish() => L.OverrideLanguage(PainterLanguage.English);

        /// <summary>規則が、止めたい文（操作の指示と「〜してから」「もう一度」の案内）を捉え、状態と理由を通すこと。</summary>
        [Test] public void TheRuleCatchesInstructionsAndPassesStatesAndReasons()
        {
            foreach (var bad in new[]
            {
                "Click the 2D canvas or the model to add points; drag a point to move it; Delete removes the last one.",
                "2D キャンバスかモデルをクリックして点を追加。点はドラッグで移動、Delete で最後の点を削除。",
                "ブラシはその上から塗ります。T を押したまま…",
                "Drag to move, corners to scale, outside to rotate. Arrow keys nudge.",
                "ドラッグか矢印キー（Shift で 10 px）で、レイヤーを動かします。",
                "Press T to place the stencil.",
                "Right-click for more.",
                "Please bake the maps.",
                "ベイクしてください。",
            }) Assert.That(LooksLikeInstruction(bad), Is.True, bad);
            foreach (var bad in new[]
            {
                "Choose a model in Texture Set, or 3D ▸ Demo Cube. The original prefab is never instantiated.", "Make a selection first.", "Bake the ID map first.",
                "Wait for the model preparation to finish, or cancel it.", "先に選択範囲を作ってください。",
                "Enable the channel before cutting from it.", "A mask cannot sample the channel composite. Choose Current layer.", "Choose at least one texture set.",
                "The material changed since the plan was made; nothing was changed. Plan again.", "The pose is being changed; bake after it is applied (when the slider is released).",
                "A fill layer is generated from its value and cannot be cut. Copy it, or paint on its mask.", "The layer below is a group. Merge the group first, or move the layer into it.",
                "The baked maps were not used because {0} changed during the bake; the previous maps are unchanged. Bake again.",
                "The path on the layer {0} was resampled, not redrawn: its model is not loaded. Redraw it in the Path section when it is.",
                "Mesh map X could not be read (it holds Y); bake it again.",
                "{0} and {1} both painted material slots of {2}; {1} is kept, but it is not shown on the model. Choose its material in File ▸ Project Configuration.",
            })
                Assert.That(LooksLikeInstruction(bad, true), Is.True, bad);
            foreach (var good in new[]
            {
                "No model", "Layer mask", "A stroke is in progress.", "Path on the model: 1 point(s) on Color", "別のモデルで描かれたパス", "No clone source", "写し元なし", "Pen pressure", "ペンの筆圧",
                "Drawn on another model", "モデル上のパス: 1 点（カラー）", "No map is checked", "The channel is not enabled on this layer.", "The bake was discarded because {0} changed; the previous maps are unchanged.",
"The texture set would hold more than {0} layers. Nothing was placed.", "This tip is not in this Unity project; a round tip is used instead.",
                "The model is posed: the paths were matched on its posed shape, not on the shape it was loaded with.", "The decal cannot be baked because it is not shown now. Baking would leave it out. Nothing was changed.",
                "Add the texture sets? Each takes memory.", "テクスチャセットが選ばれていません。",
            })
                Assert.That(LooksLikeInstruction(good, true), Is.False, good);
        }
    }
}
