using System;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>選択範囲の拡張・縮小・境界・ぼかし・鋭く（GIMP の Select メニューと同じ考え方）。期待値は GIMP の円（compute_border）を
    /// 手で計算したもの。どれも新しい選択範囲を返し、元を変えない。</summary>
    public sealed class SelectionModifyTests
    {
        const int Size = 40;
        static PaintDocument Doc() => new PaintDocument(Size, Size, 8);
        static SelectionMask Dot(PaintDocument d, int x, int y) => SelectionMask.Rectangle(d, x, y, x + 1, y + 1);
        static int Count(SelectionMask m, Func<byte, bool> pick) { int n = 0; for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) if (pick(m[x, y])) n++; return n; }

        [Test] public void GrowUsesGimpsCircle()
        {
            var d = Doc(); var dot = Dot(d, 20, 20);
            var grown = dot.Grow(3);
            // 半径 3 の円の列の高さ（GIMP の compute_border: dx=0 は 3、|dx|=1,2 は rint(√(9−0.25))=3・rint(√(9−2.25))=3、|dx|=3 は rint(√(9−6.25))=2）
            int[] heights = { 2, 3, 3, 3, 3, 3, 2 };
            for (int dy = -5; dy <= 5; dy++) for (int dx = -5; dx <= 5; dx++)
            {
                bool inside = Math.Abs(dx) <= 3 && Math.Abs(dy) <= heights[dx + 3];
                Assert.That(grown[20 + dx, 20 + dy], Is.EqualTo(inside ? 255 : 0), dx + "," + dy);
            }
            Assert.That(dot[21, 20], Is.Zero, "the original is unchanged");
            Assert.That(dot.Grow(0), Is.SameAs(dot));
        }

        [Test] public void GrowAndShrinkKeepSoftEdges()
        {
            var d = Doc();
            var soft = SelectionMask.Ellipse(d, 20, 20, 6.3, 6.3);
            Assert.That(Enumerable.Range(0, Size).Any(x => soft[x, 20] > 0 && soft[x, 20] < 255));
            var grown = soft.Grow(2);
            Assert.That(Enumerable.Range(0, Size).Any(x => grown[x, 20] > 0 && grown[x, 20] < 255), Is.True, "the anti-aliased edge moves out, still soft");
            // 総当たりの参照: 半径 2 の円（列の高さ |dx|=0,1 は 2、|dx|=2 は rint(√(4−2.25))=1）の中の最大・最小
            int[] h2 = { 1, 2, 2, 2, 1 };
            byte Reference(int x, int y, bool max)
            {
                byte v = max ? (byte)0 : (byte)255;
                for (int dx = -2; dx <= 2; dx++) for (int dy = -h2[dx + 2]; dy <= h2[dx + 2]; dy++)
                {
                    int xx = x + dx, yy = y + dy; byte a = xx < 0 || yy < 0 || xx >= Size || yy >= Size ? (byte)0 : soft[xx, yy];
                    v = max ? Math.Max(v, a) : Math.Min(v, a);
                }
                return v;
            }
            var shrunk = soft.Shrink(2);
            Assert.That(Count(shrunk, a => a > 0), Is.LessThan(Count(soft, a => a > 0)));
            Assert.That(Enumerable.Range(0, Size).Any(x => shrunk[x, 20] > 0 && shrunk[x, 20] < 255), Is.True);
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
            { Assert.That(grown[x, y], Is.EqualTo(Reference(x, y, true)), "grow " + x + "," + y); Assert.That(shrunk[x, y], Is.EqualTo(Reference(x, y, false)), "shrink " + x + "," + y); }
        }

        [Test] public void ShrinkingARectangleAndEdgeLock()
        {
            var d = Doc(); var rect = SelectionMask.Rectangle(d, 10, 10, 20, 20);
            var shrunk = rect.Shrink(2);
            Assert.That(Count(shrunk, a => a == 255), Is.EqualTo(6 * 6)); Assert.That(shrunk[12, 12], Is.EqualTo(255)); Assert.That(shrunk[11, 15], Is.Zero);
            var atEdge = SelectionMask.Rectangle(d, 0, 0, 10, 10);
            Assert.That(atEdge.Shrink(2)[0, 5], Is.Zero, "outside the canvas is unselected by default");
            Assert.That(atEdge.Shrink(2, edgeLock: true)[0, 5], Is.EqualTo(255), "edge lock: the selection continues outside the canvas");
            Assert.That(atEdge.Shrink(2, edgeLock: true)[8, 5], Is.Zero, "the inner edge still shrinks");
            Assert.That(rect.Shrink(10).IsEmpty, Is.True);
        }

        [Test] public void BorderIsGrowMinusShrink()
        {
            var d = Doc(); var rect = SelectionMask.Rectangle(d, 10, 10, 20, 20);
            var border = rect.Border(2);
            Assert.That(border[15, 15], Is.Zero, "deep inside is not on the border");
            Assert.That(border[10, 15], Is.EqualTo(255)); Assert.That(border[19, 15], Is.EqualTo(255));
            Assert.That(border[8, 15], Is.EqualTo(255), "2 px outside"); Assert.That(border[7, 15], Is.Zero);
            Assert.That(border[12, 15], Is.EqualTo(255), "inside up to Shrink(3)"); Assert.That(border[13, 15], Is.Zero);
            var one = rect.Border(1);
            Assert.That(one[9, 15], Is.Zero, "radius 1 does not grow (GIMP's special case)"); Assert.That(one[10, 15], Is.EqualTo(255)); Assert.That(one[11, 15], Is.Zero);
            Assert.That(rect.Border(0).IsEmpty, Is.True);
        }

        [TestCase(3.0)] [TestCase(14.0)]
        public void FeatherSoftensSymmetricallyAndKeepsTheAmount(double radius)
        {
            var d = Doc(); var rect = SelectionMask.Rectangle(d, 12, 12, 28, 28);
            var soft = rect.Feather(radius);
            double before = 0, after = 0;
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) { before += rect[x, y]; after += soft[x, y]; }
            Assert.That(after, Is.EqualTo(before).Within(before * .01), "a blur keeps the total (nothing reached the canvas edge)");
            Assert.That(soft[20, 20], Is.EqualTo(255).Within(radius > 10 ? 40 : 0));
            Assert.That(soft[11, 20], Is.GreaterThan(0)); Assert.That(soft[12, 20], Is.LessThan(255));
            Assert.That(soft[12, 20], Is.EqualTo(soft[27, 20]).Within(1), "symmetric");
            Assert.That(soft[12, 20], Is.EqualTo(soft[20, 12]).Within(1));
        }

        [Test] public void FeatherAtTheCanvasEdgeFollowsEdgeLock()
        {
            var d = Doc(); var all = SelectionMask.All(d);
            Assert.That(all.Feather(7)[0, 20], Is.LessThan(200), "outside the canvas is unselected: the edge fades");
            Assert.That(all.Feather(7, edgeLock: true)[0, 20], Is.EqualTo(255), "edge lock keeps the canvas edge selected");
        }

        [Test] public void SharpenThresholdsAtHalf()
        {
            var d = Doc(); var soft = SelectionMask.Ellipse(d, 20, 20, 6.3, 6.3).Feather(4);
            var sharp = soft.Sharpen();
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) Assert.That(sharp[x, y], Is.EqualTo(soft[x, y] >= 128 ? 255 : 0));
        }

        [Test] public void RadiiAreCheckedAndEmptySelectionsStayEmpty()
        {
            var d = Doc(); var rect = SelectionMask.Rectangle(d, 1, 1, 5, 5);
            Assert.That(() => rect.Grow(-1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => rect.Shrink(SelectionMask.MaxModifyRadius + 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => rect.Feather(double.NaN), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => rect.Feather(-1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            var none = SelectionMask.None(d);
            Assert.That(none.Grow(3).IsEmpty && none.Shrink(3).IsEmpty && none.Border(3).IsEmpty && none.Feather(3).IsEmpty && none.Sharpen().IsEmpty, Is.True);
        }

        [Test] public void AModifiedSelectionIsOneUndoStepAndLimitsPainting()
        {
            var d = Doc(); var layer = d.AddLayer("L"); d.ClearHistory();
            d.SetSelection(SelectionMask.Rectangle(d, 10, 10, 20, 20));
            d.SetSelection(d.Selection.Grow(4));
            Assert.That(d.Fill(layer.Id, PaintChannel.Color, new Rgba32(1, 2, 3)), Is.True);
            Assert.That(layer.GetPixel(PaintChannel.Color, 7, 15).A, Is.EqualTo(255)); Assert.That(layer.GetPixel(PaintChannel.Color, 4, 15).A, Is.Zero);
            d.Undo(); d.Undo();
            Assert.That(d.Selection[7, 15], Is.Zero); Assert.That(d.Selection[12, 15], Is.EqualTo(255));
        }
    }
}
