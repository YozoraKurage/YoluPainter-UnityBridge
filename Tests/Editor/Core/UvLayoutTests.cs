using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>UV のレイアウトの比べ方（モデルの差し替えで、描いた画素が新しいメッシュでも同じ所に付くか）: 並びの順・頂点の回し方・丸めの
    /// 違いは同じ、覆う所の重なりと増えた所、型の拒否。</summary>
    public sealed class UvLayoutTests
    {
        static UvTriangle T(double ax, double ay, double bx, double by, double cx, double cy) => new UvTriangle(ax, ay, bx, by, cx, cy);
        /// <summary>(u, v) から幅 w・高さ h の正方形の 2 つの三角形。</summary>
        static List<UvTriangle> Quad(double u, double v, double w, double h) => new List<UvTriangle> { T(u, v, u + w, v, u + w, v + h), T(u, v, u + w, v + h, u, v + h) };

        [Test] public void TheSameTrianglesInAnotherOrderOrRotationAreTheSame()
        {
            var a = Quad(.1, .1, .4, .4).Concat(Quad(.6, .1, .3, .3)).ToList();
            var shuffled = new List<UvTriangle> { a[3], a[0], a[2], a[1] };
            var rotated = shuffled.Select(t => T(t.Bx, t.By, t.Cx, t.Cy, t.Ax, t.Ay)).ToList(); // 回し方だけ変える（向きは同じ）
            var noisy = rotated.Select(t => T(t.Ax + 1e-8, t.Ay - 1e-8, t.Bx, t.By, t.Cx + 2e-8, t.Cy)).ToList(); // 書き出し直した FBX の丸め
            foreach (var other in new[] { shuffled, rotated, noisy })
            {
                var c = UvLayout.Compare(a, other);
                Assert.That((c.Same, c.Kept, c.Added), Is.EqualTo((true, 1.0, 0.0)));
            }
            Assert.That(UvLayout.SameTriangles(a, a.Take(3).ToList()), Is.False, "a triangle fewer");
            var mirrored = a.Select(t => T(t.Ax, t.Ay, t.Cx, t.Cy, t.Bx, t.By)).ToList();
            Assert.That(UvLayout.SameTriangles(a, mirrored), Is.False, "the other winding is another triangle (a flipped island)");
        }

        [Test] public void DifferentUvsReportHowMuchOfTheOldAreaIsStillCovered()
        {
            var before = Quad(0, 0, .5, .5);
            var smaller = Quad(0, 0, .25, .5);
            var c = UvLayout.Compare(before, smaller, 64);
            Assert.That(c.Same, Is.False);
            Assert.That(c.Kept, Is.EqualTo(.5).Within(.05), "half of the old square");
            Assert.That(c.Added, Is.Zero.Within(.02), "nothing outside the old square");
            var moved = Quad(.5, .5, .5, .5);
            var m = UvLayout.Compare(before, moved, 64);
            Assert.That(m.Kept, Is.LessThan(.05)); Assert.That(m.Added, Is.GreaterThan(.95));
            var none = UvLayout.Compare(new UvTriangle[0], moved, 64);
            Assert.That((none.Same, none.Kept), Is.EqualTo((false, 1.0)), "nothing was covered before: nothing is lost");
            Assert.That(UvLayout.Compare(new UvTriangle[0], new UvTriangle[0]).Same, Is.True);
        }

        [Test] public void BadInputIsRefused()
        {
            var q = Quad(0, 0, 1, 1);
            Assert.That(() => UvLayout.Compare(null, q), Throws.ArgumentNullException);
            Assert.That(() => UvLayout.Compare(q, null), Throws.ArgumentNullException);
            Assert.That(() => UvLayout.Compare(q, Quad(0, 0, .5, .5), 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => UvLayout.Compare(q, Quad(0, 0, .5, .5), UvLayout.MaxResolution + 1), Throws.TypeOf<ArgumentOutOfRangeException>());
            var nan = new List<UvTriangle> { T(double.NaN, 0, 1, 0, 1, 1) };
            Assert.That(UvLayout.Compare(nan, q, 16).Same, Is.False, "a broken UV is never the same, and it covers nothing");
        }
    }
}
