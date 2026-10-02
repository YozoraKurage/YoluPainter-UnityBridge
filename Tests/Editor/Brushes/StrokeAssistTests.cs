using System;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>手ぶれ補正（引っ張る糸）と入り抜き。既定（0）では今までのストロークと同じ。補正は入力の頻度によらず、離したときに
    /// 最後の点まで描く。抜きは線の終わりが分かるまで最後の部分を待ち、取り消せば何も残らない。</summary>
    public sealed class StrokeAssistTests
    {
        static BrushSettings Hard(double radius = 4) => new BrushSettings { Radius = radius, Hardness = 1, Spacing = .1, Color = new Rgba32(0, 0, 0), PressureSize = false, PressureOpacity = false };
        static (PaintDocument d, PaintLayer l) Doc() { var d = new PaintDocument(128, 64, 16); var l = d.AddLayer("L"); d.ClearHistory(); return (d, l); }
        static byte[] Draw(BrushSettings s, params (double x, double y)[] points)
        {
            var (d, l) = Doc();
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s)) { foreach (var p in points) stroke.Add(new BrushSample(p.x, p.y)); stroke.Commit(); }
            return d.Composite(PaintChannel.Color);
        }
        static int Alpha(byte[] c, int x, int y) => c[(y * 128 + x) * 4 + 3];
        /// <summary>列 x で塗られた画素の数（線の太さ）。</summary>
        static int Thickness(byte[] c, int x) => Enumerable.Range(0, 64).Count(y => Alpha(c, x, y) > 0);

        [Test] public void ZeroMeansTheStrokeIsUnchanged()
        {
            var path = new[] { (10.0, 30.0), (40.0, 35.0), (80.0, 20.0) };
            var plain = Draw(Hard(), path);
            var s = Hard(); s.Stabilizer = 0; s.TaperIn = 0; s.TaperOut = 0;
            Assert.That(Draw(s, path), Is.EqualTo(plain));
        }

        [Test] public void TheStabilizerSmoothsJitterAndFinishesAtTheLastPoint()
        {
            var zigzag = Enumerable.Range(0, 50).Select(i => (10.0 + i * 2, 32.0 + (i % 2 == 0 ? 4 : -4))).ToArray();
            var raw = Draw(Hard(2), zigzag);
            var s = Hard(2); s.Stabilizer = 12;
            var smooth = Draw(s, zigzag);
            int RowsTouched(byte[] c) => Enumerable.Range(0, 64).Count(y => Enumerable.Range(30, 60).Any(x => Alpha(c, x, y) > 0));
            Assert.That(RowsTouched(smooth), Is.LessThan(RowsTouched(raw)), "jitter shorter than the string is smoothed");
            Assert.That(Alpha(smooth, 108, 28) + Alpha(smooth, 108, 36) + Alpha(smooth, 108, 32), Is.GreaterThan(0), "the line is drawn on to the last input point");
        }

        [Test] public void TheStabilizerDoesNotDependOnTheInputRate()
        {
            var s = Hard(); s.Stabilizer = 8;
            var sparse = Draw(s, (10, 30), (60, 30), (110, 30));
            var dense = Draw(s, Enumerable.Range(0, 101).Select(i => (10.0 + i, 30.0)).ToArray());
            Assert.That(dense, Is.EqualTo(sparse), "a straight line comes out the same however often the pointer reports");
        }

        [Test] public void TapersThinBothEndsAndTheEndWaitsForTheStrokeToFinish()
        {
            var s = Hard(6); s.TaperIn = 30; s.TaperOut = 30;
            var (d, l) = Doc();
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s))
            {
                stroke.Add(new BrushSample(10, 32)); stroke.Add(new BrushSample(110, 32));
                var before = d.Composite(PaintChannel.Color);
                Assert.That(Alpha(before, 100, 32), Is.Zero, "the last TaperOut pixels wait until the end is known");
                Assert.That(Alpha(before, 60, 32), Is.GreaterThan(0));
                stroke.Commit();
            }
            var c = d.Composite(PaintChannel.Color);
            Assert.That(Thickness(c, 14), Is.LessThan(Thickness(c, 60)), "taper in");
            Assert.That(Thickness(c, 106), Is.LessThan(Thickness(c, 60)), "taper out");
            Assert.That(Thickness(c, 100), Is.GreaterThan(0), "the end is drawn on commit");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo(); Assert.That(d.Composite(PaintChannel.Color).All(b => b == 0), Is.True);
        }

        [Test] public void CancellingWithHeldBackDabsLeavesNothing()
        {
            var s = Hard(); s.TaperOut = 40; s.Stabilizer = 5;
            var (d, l) = Doc();
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s))
            { stroke.Add(new BrushSample(10, 32)); stroke.Add(new BrushSample(100, 32)); stroke.Cancel(); }
            Assert.That(d.Composite(PaintChannel.Color).All(b => b == 0), Is.True);
            Assert.That(d.UndoCount, Is.Zero); Assert.That(d.HasActiveStroke, Is.False);
        }

        [Test] public void InvalidValuesAreRefused()
        {
            foreach (var bad in new Action<BrushSettings>[] { b => b.Stabilizer = -1, b => b.TaperIn = double.NaN, b => b.TaperOut = BrushSettings.MaxStrokeAssist + 1 })
            {
                var s = Hard(); bad(s);
                Assert.That(() => s.Validate(), Throws.InstanceOf<ArgumentOutOfRangeException>());
            }
            var ok = Hard(); ok.Stabilizer = 3; ok.TaperIn = 5; ok.TaperOut = 7;
            var copy = ok.Clone();
            Assert.That((copy.Stabilizer, copy.TaperIn, copy.TaperOut), Is.EqualTo((3.0, 5.0, 7.0)));
            var (d, l) = Doc();
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, ok))
            {
                stroke.Add(new BrushSample(5, 5, 1, 2));
                Assert.That(() => stroke.Add(new BrushSample(9, 9, 1, 1)), Throws.ArgumentException, "time still has to increase with the stabilizer");
            }
        }
    }
}
