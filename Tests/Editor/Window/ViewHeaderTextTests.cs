using NUnit.Framework;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// ビューの上の見出しの文字（3D は「3D · モデル名」、2D は「2D · テクスチャセット名 · チャンネル  拡大率」）と、表示の切り替えのボタンの幅の決め方。
    /// 狭いときに … で詰めてよいのは利用者の名前（モデル・テクスチャセット）の部分だけで、この UI の固定の文字（「2D」「Color」「100%」）は詰めない。
    /// </summary>
    public sealed class ViewHeaderTextTests
    {
        const string Name = "Zebra-long-model-name-for-the-view-header";
        static float Width(string text) => PaintGui.TextWidth(text, PaintTheme.LabelDim);

        [SetUp] public void NeedsAFont() => Assume.That(Width(Name), Is.GreaterThan(100), "no font to measure with");

        [Test] public void AHeaderThatFitsIsWholeAndALongNameIsCutInsideItsOwnPlace()
        {
            string full = "3D · " + Name;
            Assert.That(TexturePaintWindow.ViewHeaderText(Width(full) + 1, "3D · ", Name, "", "3D"), Is.EqualTo(full));
            float room = Width("3D · ") + 90; // 名前に 90 px: 名前は … で詰まるが読める
            string cut = TexturePaintWindow.ViewHeaderText(room, "3D · ", Name, "", "3D");
            Assert.That(cut, Does.StartWith("3D · Z").And.EndWith("…"), "the long model name is cut with an ellipsis, not replaced by '3D'");
            Assert.That(Width(cut), Is.LessThanOrEqualTo(room));
            Assert.That(TexturePaintWindow.ViewHeaderText(Width("3D · ") + 20, "3D · ", Name, "", "3D"), Is.EqualTo("3D"), "with no room for a readable name, the short form");

            string after = " · Color  100%";
            float two = Width("2D · ") + Width(after) + 70;
            string cut2D = TexturePaintWindow.ViewHeaderText(two, "2D · ", Name, after, "2D  100%", "2D");
            Assert.That(cut2D, Does.StartWith("2D · Z").And.EndWith("…" + after), "only the texture set name is cut; the channel and the zoom stay");
            Assert.That(Width(cut2D), Is.LessThanOrEqualTo(two));
        }

        /// <summary>幅を 0 から広げていくと、出さない → 短い形 → 名前を詰めた形 → 全部、の順になり、固定の文字は決して … で詰まらない。</summary>
        [Test] public void EveryWidthGivesNothingTheShortFormTheCutNameOrTheWholeText()
        {
            string before = "2D · ", after = " · Color  100%", full = before + Name + after;
            int cutSeen = 0, shortSeen = 0, noneSeen = 0, wholeSeen = 0;
            for (float width = 0; width <= Width(full) + 20; width += 1)
            {
                string text = TexturePaintWindow.ViewHeaderText(width, before, Name, after, "2D  100%", "2D");
                string at = "width " + width + ": " + text;
                if (text == null) { noneSeen++; continue; }
                Assert.That(Width(text), Is.LessThanOrEqualTo(width + .01f), at);
                if (text == full) { wholeSeen++; continue; }
                if (text.Contains("…")) { cutSeen++; Assert.That(text, Does.StartWith(before).And.EndWith("…" + after), at); continue; }
                shortSeen++; Assert.That(text, Is.EqualTo("2D  100%").Or.EqualTo("2D"), at);
            }
            Assert.That(noneSeen, Is.GreaterThan(0), "very narrow: not drawn"); Assert.That(shortSeen, Is.GreaterThan(0), "narrow: the short form"); Assert.That(wholeSeen, Is.GreaterThan(0), "wide: whole");
            Assert.That(cutSeen, Is.GreaterThan(10), "a wide range of widths cuts the name instead of dropping it");
            // 名前を詰められる幅（固定の文字 + 名前に 60 px）では、名前が消えない
            for (float width = Width(before) + Width(after) + 60; width < Width(full); width += 1)
                Assert.That(TexturePaintWindow.ViewHeaderText(width, before, Name, after, "2D  100%", "2D"), Does.Contain("…"), "width " + width + ": the name is cut, not dropped");
        }

        [Test] public void AHeaderWithoutAUserNameIsWholeShortOrNotDrawnAndNeverCut()
        {
            string text = "3D · Demo cube";
            for (float width = 0; width <= Width(text) + 10; width += 1)
            {
                string shown = TexturePaintWindow.ViewHeaderText(width, text, null, "", "3D");
                Assert.That(shown, Is.Null.Or.EqualTo(text).Or.EqualTo("3D"), "width " + width);
                if (shown != null) Assert.That(Width(shown), Is.LessThanOrEqualTo(width + .01f));
            }
        }

        /// <summary>表示の切り替えのボタン: 名前が全部入れば名前つき、入らなければ目のアイコン＋▾（40 px）、それも入らなければ出さない（幅 0）。</summary>
        [Test] public void TheDisplayDropdownIsWholeIconOnlyOrNotDrawn()
        {
            Assert.That(TexturePaintWindow.ViewShowButtonWidth(100, 86), Is.EqualTo(86));
            Assert.That(TexturePaintWindow.ViewShowButtonWidth(86, 86), Is.EqualTo(86));
            Assert.That(TexturePaintWindow.ViewShowButtonWidth(85.9f, 86), Is.EqualTo(40));
            Assert.That(TexturePaintWindow.ViewShowButtonWidth(40, 150), Is.EqualTo(40));
            Assert.That(TexturePaintWindow.ViewShowButtonWidth(39.9f, 150), Is.Zero);
            Assert.That(TexturePaintWindow.ViewShowButtonWidth(0, 86), Is.Zero);
        }
    }
}
