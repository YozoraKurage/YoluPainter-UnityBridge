using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>選択範囲の保存（selection.bin）: 半端な量も端のタイルもそのまま戻り、同じ選択は同じバイト列、自分で書かない形は断る。
    /// 読み込んだ選択は履歴にも版にも入らない。</summary>
    public sealed class SelectionPersistenceTests
    {
        static PaintDocument Doc() => new PaintDocument(70, 50, 16);
        static SelectionMask Sample(PaintDocument d) =>
            SelectionMask.Ellipse(d, 30, 20, 18, 11).Combine(SelectionMask.Rectangle(d, 60, 40, 70, 50), SelectionCombine.Add).Feather(3);

        static void AssertSame(SelectionMask a, SelectionMask b)
        {
            Assert.That(b.Width, Is.EqualTo(a.Width)); Assert.That(b.Height, Is.EqualTo(a.Height));
            for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) if (a[x, y] != b[x, y]) Assert.Fail("differs at " + x + "," + y + ": " + a[x, y] + " vs " + b[x, y]);
        }

        [Test] public void PartialAmountsAndEdgeTilesRoundTripToTheSameBytes()
        {
            var d = Doc(); var m = Sample(d);
            Assert.That(Enumerable.Range(0, 70).Any(x => m[x, 20] > 0 && m[x, 20] < 255), Is.True, "the sample has partial amounts");
            Assert.That(m[69, 49], Is.GreaterThan(0), "the sample reaches the partial edge tile");
            var bytes = SelectionBinary.Write(m);
            var read = SelectionBinary.Read(bytes, d);
            AssertSame(m, read);
            Assert.That(SelectionBinary.Write(read), Is.EqualTo(bytes));
            // 作る順が違っても同じ選択なら同じバイト列
            var other = SelectionMask.Rectangle(d, 60, 40, 70, 50).Combine(SelectionMask.Ellipse(d, 30, 20, 18, 11), SelectionCombine.Add).Feather(3);
            Assert.That(SelectionBinary.Write(other), Is.EqualTo(bytes));
        }

        [Test] public void AnEmptySelectionReadsAsNothingSelected()
        {
            var d = Doc();
            Assert.That(SelectionBinary.Read(SelectionBinary.Write(SelectionMask.None(d)), d), Is.Null);
            var all = SelectionMask.All(d);
            AssertSame(all, SelectionBinary.Read(SelectionBinary.Write(all), d));
        }

        static byte[] Patch(byte[] bytes, int offset, int value) { var b = (byte[])bytes.Clone(); BitConverter.GetBytes(value).CopyTo(b, offset); return b; }

        [TestCase("magic", "not a YoluPainter selection")]
        [TestCase("version", "Unsupported selection version")]
        [TestCase("size", "but the document is")]
        [TestCase("count", "impossible tile count")]
        [TestCase("outside", "outside the document")]
        [TestCase("order", "duplicated or out of order")]
        [TestCase("padding", "amounts outside the document")]
        [TestCase("empty", "tile is empty")]
        [TestCase("truncated", "length does not match")]
        [TestCase("trailing", "length does not match")]
        [TestCase("short", "not a YoluPainter selection")]
        public void AnythingItDoesNotWriteIsRefused(string kind, string message)
        {
            var d = Doc(); var bytes = SelectionBinary.Write(SelectionMask.Rectangle(d, 0, 0, 70, 50));
            int n = 16 * 16, first = 24, second = first + 8 + n;
            byte[] bad;
            switch (kind)
            {
                case "magic": bad = (byte[])bytes.Clone(); bad[0] = (byte)'X'; break;
                case "version": bad = Patch(bytes, 4, 2); break;
                case "size": bad = Patch(bytes, 8, 71); break;
                case "count": bad = Patch(bytes, 20, 5 * 4 + 1); break;
                case "outside": bad = Patch(bytes, first, 5); break;
                case "order": bad = Patch(bytes, second, 0); break; // 2 枚目を 1 枚目と同じ座標に
                case "padding": // 最後のタイル（右下、幅 6・高さ 2）の文書の外に量を入れる
                    bad = (byte[])bytes.Clone(); bad[bad.Length - 1] = 9; break;
                case "empty": bad = (byte[])bytes.Clone(); Array.Clear(bad, first + 8, n); break;
                case "truncated": bad = bytes.Take(bytes.Length - 1).ToArray(); break;
                case "trailing": bad = bytes.Concat(new byte[1]).ToArray(); break;
                default: bad = bytes.Take(10).ToArray(); break;
            }
            Assert.That(() => SelectionBinary.Read(bad, d), Throws.TypeOf<InvalidDataException>().With.Message.Contains(message));
        }

        [Test] public void RestoringAddsNoHistoryAndOnlyRightAfterLoading()
        {
            var d = Doc(); d.AddLayer("a"); d.ClearHistory();
            long revision = d.Revision; var m = Sample(d);
            d.RestoreSelection(SelectionBinary.Read(SelectionBinary.Write(m), d));
            Assert.That(d.Selection, Is.Not.Null); AssertSame(m, d.Selection);
            Assert.That(d.Revision, Is.EqualTo(revision)); Assert.That(d.CanUndo, Is.False);
            // 復元した選択は普通の選択として Undo で戻る
            d.ClearSelection(); Assert.That(d.Selection, Is.Null);
            d.Undo(); AssertSame(m, d.Selection);
            Assert.That(() => d.RestoreSelection(null), Throws.InvalidOperationException, "not once there is history");
            var e = Doc();
            Assert.That(() => e.RestoreSelection(SelectionMask.All(new PaintDocument(64, 64, 16))), Throws.ArgumentException);
            e.RestoreSelection(SelectionMask.None(e)); Assert.That(e.Selection, Is.Null, "an empty selection restores as none");
        }
    }
}
