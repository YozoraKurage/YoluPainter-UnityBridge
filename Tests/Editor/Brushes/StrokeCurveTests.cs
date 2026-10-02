using System;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>入力の点の間を曲線で結ぶ（BrushSettings.CurveInterpolation、centripetal Catmull-Rom）。既定は無効で直線のまま。
    /// まばらな点で円を描くと、直線で結ぶより真の円に近い（手ぶれ補正を掛けても角が出にくい）。最新の点への区間は次の点か確定まで
    /// 待ち、取り消せば何も残らない。直線の上の等間隔の点では、入り抜き・デュアルブラシ・間隔の累積まで直線のときと同じ画素になる。</summary>
    public sealed class StrokeCurveTests
    {
        const double Cx = 64.3, Cy = 63.7, R = 40;

        static BrushSettings Soft(bool curve) => new BrushSettings { Radius = 4, Hardness = 0, Spacing = .05, Color = new Rgba32(0, 0, 0), PressureSize = false, PressureOpacity = false, CurveInterpolation = curve };

        /// <summary>中心 (Cx, Cy)・半径 R の円の 3/4 の弧を、1 周 n 等分の角度の点で描く。</summary>
        static byte[] Arc(int n, BrushSettings s)
        {
            var d = new PaintDocument(128, 128, 32); var l = d.AddLayer("L");
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s))
            {
                for (int i = 0; i <= n * 3 / 4; i++) stroke.Add(new BrushSample(Cx + R * Math.Cos(2 * Math.PI * i / n), Cy + R * Math.Sin(2 * Math.PI * i / n), 1, i));
                stroke.Commit();
            }
            return d.Composite(PaintChannel.Color);
        }

        /// <summary>線の中心の、円からのずれ: 角度の区切りごとに塗った画素の中心からの距離を濃さで平均し、最初と最後の区間を除いた
        /// 範囲での（円からの最大のずれ、最大と最小の差）。画素で測るので、真の曲線でも 0.6 px ほどの測りの揺れが残る。</summary>
        internal static (double deviation, double ripple) CenterLine(byte[] rgba, int width, int height, double cx, double cy, double radius, int n, int bins = 240)
        {
            var sum = new double[bins]; var weight = new double[bins];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int a = rgba[(y * width + x) * 4 + 3]; if (a == 0) continue;
                double dx = x + .5 - cx, dy = y + .5 - cy, angle = Math.Atan2(dy, dx); if (angle < 0) angle += 2 * Math.PI;
                int b = (int)(angle / (2 * Math.PI) * bins) % bins; sum[b] += Math.Sqrt(dx * dx + dy * dy) * a; weight[b] += a;
            }
            double step = 2 * Math.PI / n, end = (n * 3 / 4) * step, deviation = 0, low = double.MaxValue, high = 0;
            for (int b = 0; b < bins; b++)
            {
                double angle = (b + .5) / bins * 2 * Math.PI;
                if (weight[b] == 0 || angle <= step || angle >= end - step) continue;
                double r = sum[b] / weight[b];
                deviation = Math.Max(deviation, Math.Abs(r - radius)); low = Math.Min(low, r); high = Math.Max(high, r);
            }
            return (deviation, high - low);
        }

        [Test] public void TheCurvePassesThroughItsPointsAndKeepsALineStraight()
        {
            StrokeCurve.Point(-3, 1, 0, 0, 10, 5, 12, 9, 0, out double x, out double y);
            Assert.That(Math.Abs(x) + Math.Abs(y), Is.LessThan(1e-9));
            StrokeCurve.Point(-3, 1, 0, 0, 10, 5, 12, 9, 1, out x, out y);
            Assert.That(Math.Abs(x - 10) + Math.Abs(y - 5), Is.LessThan(1e-9));
            // 一直線に並んだ不揃いな点: 線から外れず、行き過ぎて戻らない（centripetal は区間の中で尖らない）
            double last = double.MinValue;
            for (int k = 0; k <= 100; k++)
            {
                StrokeCurve.Point(0, 0, 1, 0, 30, 0, 31, 0, k / 100.0, out x, out y);
                Assert.That(Math.Abs(y), Is.LessThan(1e-12)); Assert.That(x, Is.GreaterThanOrEqualTo(last - 1e-12)); last = x;
            }
            // 重なった前後の点でも数にならない値は出さない
            StrokeCurve.Point(5, 5, 5, 5, 9, 5, 9, 5, .5, out x, out y);
            Assert.That(double.IsNaN(x) || double.IsNaN(y), Is.False); Assert.That(x, Is.InRange(5.0, 9.0));
        }

        [TestCase(8)] [TestCase(10)] [TestCase(12)]
        public void TheCurveThroughSparsePointsOfACircleStaysOnIt(int n)
        {
            (double, double) P(int i) => (R * Math.Cos(2 * Math.PI * i / n), R * Math.Sin(2 * Math.PI * i / n));
            double worst = 0, sag = R * (1 - Math.Cos(Math.PI / n));
            var (x0, y0) = P(-1); var (x1, y1) = P(0); var (x2, y2) = P(1); var (x3, y3) = P(2);
            for (int k = 0; k <= 200; k++)
            {
                StrokeCurve.Point(x0, y0, x1, y1, x2, y2, x3, y3, k / 200.0, out double x, out double y);
                worst = Math.Max(worst, Math.Abs(Math.Sqrt(x * x + y * y) - R));
            }
            // 実測: n = 8 で 0.339 px（直線の弦は 3.04 px 内側）、10 で 0.141（1.96）、12 で 0.069（1.36）
            Assert.That(worst, Is.LessThan(sag / 8), "the curve stays far closer to the circle than the chords");
        }

        [TestCase(8)] [TestCase(10)] [TestCase(12)]
        public void ASparseCircleIsDrawnRoundOnTheCanvas(int n)
        {
            var straight = CenterLine(Arc(n, Soft(false)), 128, 128, Cx, Cy, R, n);
            var curved = CenterLine(Arc(n, Soft(true)), 128, 128, Cx, Cy, R, n);
            // 実測（線の中心の最大のずれ）: n = 8 で 3.15 → 0.58 px、10 で 2.12 → 0.64、12 で 1.94 → 0.59（曲線の側は測りの揺れの大きさ）
            Assert.That(curved.deviation, Is.LessThan(1.0), "the curved line follows the circle");
            Assert.That(curved.deviation, Is.LessThan(straight.deviation / 2), "straight segments cut the corners: " + straight.deviation);
        }

        [Test] public void TheStabilizerOnSparsePointsNoLongerMakesCorners()
        {
            var s = Soft(false); s.Stabilizer = 6; var straight = CenterLine(Arc(8, s), 128, 128, Cx, Cy, R, 8);
            s = Soft(true); s.Stabilizer = 6; var curved = CenterLine(Arc(8, s), 128, 128, Cx, Cy, R, 8);
            // 糸を引いた筆先は円の内側を回るので、円からのずれではなく線の中心の半径の揺れ（角）で比べる。実測 3.07 → 1.43 px。
            Assert.That(curved.ripple, Is.LessThan(straight.ripple * .6), "straight: " + straight.ripple + ", curved: " + curved.ripple);
        }

        [Test] public void TheSegmentToTheNewestPointWaitsAndCommitDrawsIt()
        {
            var d = new PaintDocument(128, 64, 16); var l = d.AddLayer("L"); d.ClearHistory();
            var s = Soft(true); s.Hardness = 1; s.Radius = 3;
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s))
            {
                stroke.Add(new BrushSample(10, 32)); stroke.Add(new BrushSample(60, 40, 1, 1)); stroke.Add(new BrushSample(110, 32, 1, 2));
                var during = d.Composite(PaintChannel.Color);
                Assert.That(during[(36 * 128 + 30) * 4 + 3], Is.GreaterThan(0), "the first segment is drawn once the next point is known");
                Assert.That(Enumerable.Range(0, 64).All(y => during[(y * 128 + 100) * 4 + 3] == 0), Is.True, "the segment to the newest point waits");
                stroke.Commit();
            }
            var c = d.Composite(PaintChannel.Color);
            Assert.That(Enumerable.Range(0, 64).Any(y => c[(y * 128 + 100) * 4 + 3] > 0), Is.True, "commit draws the last segment");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo(); Assert.That(d.Composite(PaintChannel.Color).All(b => b == 0), Is.True, "one undo removes the whole curve");
            d.Redo(); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(c));
        }

        [Test] public void CancellingWithAHeldSegmentLeavesNothing()
        {
            foreach (bool dispose in new[] { false, true })
            {
                var d = new PaintDocument(128, 64, 16); var l = d.AddLayer("L"); d.ClearHistory();
                var s = Soft(true); s.TaperOut = 20; s.Stabilizer = 3;
                var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s);
                stroke.Add(new BrushSample(10, 32)); stroke.Add(new BrushSample(60, 40, 1, 1)); stroke.Add(new BrushSample(110, 20, 1, 2));
                if (dispose) stroke.Dispose(); else stroke.Cancel();
                Assert.That(d.Composite(PaintChannel.Color).All(b => b == 0), Is.True);
                Assert.That(d.UndoCount, Is.Zero); Assert.That(d.HasActiveStroke, Is.False);
            }
        }

        [Test] public void OnALineTheCurveDrawsTheSamePixelsAsStraightSegments()
        {
            foreach (bool dual in new[] { false, true })
            {
                byte[] Draw(bool curve, out long stamps)
                {
                    var d = new PaintDocument(128, 64, 16); var l = d.AddLayer("L");
                    var s = new BrushSettings { Radius = 5, Hardness = .6, Spacing = .1, Color = new Rgba32(0, 0, 0), TaperIn = 20, TaperOut = 25, CurveInterpolation = curve };
                    // デュアルの間隔（1.842 px）は主の間隔（1 px）とこの長さの中で重ならない。同じ線の長さに両方のダブが来ると、
                    // 線の長さの足し算の丸めで、主のダブがそのデュアルのダブを先に見るかどうかが変わりうる（その画素だけ違う）。
                    if (dual) s.Dual = new DualBrush { Radius = 3.07, Spacing = .3, Scatter = .5, Count = 2 };
                    using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s))
                    { for (int i = 0; i <= 5; i++) stroke.Add(new BrushSample(10 + 20 * i, 32, .3 + .14 * i, i)); stroke.Commit(); stamps = stroke.StampCount; }
                    return d.Composite(PaintChannel.Color);
                }
                var straight = Draw(false, out long a); var curved = Draw(true, out long b);
                Assert.That(b, Is.EqualTo(a), "the spacing accumulates the same along the curve");
                Assert.That(curved, Is.EqualTo(straight), "pressure, tapers" + (dual ? " and the dual brush" : "") + " follow the same arc length");
            }
        }

        [Test] public void TapersShapeACurvedStrokeAndTheEndWaitsForCommit()
        {
            var d = new PaintDocument(128, 128, 32); var l = d.AddLayer("L"); d.ClearHistory();
            var s = Soft(true); s.Hardness = 1; s.Radius = 5; s.TaperIn = 25; s.TaperOut = 25;
            int n = 8;
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s))
            {
                for (int i = 0; i <= 6; i++) stroke.Add(new BrushSample(Cx + R * Math.Cos(2 * Math.PI * i / n), Cy + R * Math.Sin(2 * Math.PI * i / n), 1, i));
                Assert.That(d.Composite(PaintChannel.Color)[((int)Cy * 128 + (int)(Cx + R * Math.Cos(2 * Math.PI * 6 / n))) * 4 + 3], Is.Zero);
                stroke.Commit();
            }
            var c = d.Composite(PaintChannel.Color);
            int Across(double angle)
            {
                // 円の中心から外へ向かう線の上で、塗られた画素の数（線の太さ）
                int count = 0;
                for (double r = R - 10; r <= R + 10; r += .25)
                {
                    int x = (int)(Cx + r * Math.Cos(angle)), y = (int)(Cy + r * Math.Sin(angle));
                    if (c[(y * 128 + x) * 4 + 3] > 0) count++;
                }
                return count;
            }
            double end = 2 * Math.PI * 6 / n;
            Assert.That(Across(.15), Is.LessThan(Across(end / 2)), "taper in");
            Assert.That(Across(end - .15), Is.LessThan(Across(end / 2)), "taper out");
            Assert.That(Across(end - .3), Is.GreaterThan(0), "the end is drawn on commit");
            Assert.That(d.UndoCount, Is.EqualTo(1));
        }

        [Test] public void AnOversizedCurvedSegmentIsRefusedWithoutPartialEdits()
        {
            foreach (bool atCommit in new[] { false, true })
            {
                var d = new PaintDocument(64, 64, 16); var l = d.AddLayer("L"); d.ClearHistory();
                var s = new BrushSettings { Radius = .05, Spacing = .1, Color = new Rgba32(0, 0, 0), CurveInterpolation = true };
                var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s);
                stroke.Add(new BrushSample(20, 20.5)); stroke.Add(new BrushSample(30, 20.5, 1, 1));
                stroke.Add(new BrushSample(5000000, 20.5, 1, 2)); // まだ待っている区間（ダブの数は確かめていない）
                if (atCommit) Assert.That(() => stroke.Commit(), Throws.InvalidOperationException.With.Message.Contains("one-million-stamp"));
                else Assert.That(() => stroke.Add(new BrushSample(5000000, 40, 1, 3)), Throws.InvalidOperationException.With.Message.Contains("one-million-stamp"));
                Assert.That(stroke.IsFinished, Is.True); Assert.That(d.HasActiveStroke, Is.False);
                Assert.That(d.Composite(PaintChannel.Color).All(b => b == 0), Is.True, "the refused stroke leaves no pixels");
                Assert.That(d.UndoCount, Is.Zero);
            }
        }

        [Test] public void TheSettingIsOffByDefaultAndCopiedWithTheBrush()
        {
            Assert.That(new BrushSettings().CurveInterpolation, Is.False, "straight segments unless asked (strokes stay byte-identical)");
            var s = new BrushSettings { CurveInterpolation = true };
            Assert.That(s.Clone().CurveInterpolation, Is.True);
            Assert.That(s.ForChannel(PaintChannel.Roughness).CurveInterpolation, Is.True);
            Assert.That(s.ForChannel(null).CurveInterpolation, Is.True, "mask strokes too");
            foreach (var preset in BuiltInBrushes.Presets) Assert.That(preset.CreateSettings().CurveInterpolation, Is.False, preset.Name);
        }
    }
}
