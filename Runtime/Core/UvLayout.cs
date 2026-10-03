using System;
using System.Collections.Generic;
using System.Linq;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>UV の三角形 1 つ（0〜1 の UV。a・b・c の順）。</summary>
    public readonly struct UvTriangle
    {
        public readonly double Ax, Ay, Bx, By, Cx, Cy;
        public UvTriangle(double ax, double ay, double bx, double by, double cx, double cy) { Ax = ax; Ay = ay; Bx = bx; By = by; Cx = cx; Cy = cy; }
    }

    /// <summary>2 つの UV の並びの比べ方（モデルを差し替えたとき、テクスチャセットの描いた画素が新しいメッシュでも同じ所に付くか）。</summary>
    public sealed class UvComparison
    {
        /// <summary>同じ UV の三角形の集まりか（順と三角形の中の頂点の回し方は問わない。2^-20 で丸めて比べる）。同じなら描いた画素は同じ所に付く。</summary>
        public bool Same { get; }
        /// <summary>前の UV が覆うテクセルのうち、新しい UV も覆う割合（0〜1。前の UV が何も覆わなければ 1）。<see cref="Same"/> なら 1。</summary>
        public double Kept { get; }
        /// <summary>新しい UV が覆うテクセルのうち、前の UV が覆っていなかった割合（0〜1。新しい UV が何も覆わなければ 0）。</summary>
        public double Added { get; }
        internal UvComparison(bool same, double kept, double added) { Same = same; Kept = kept; Added = added; }
    }

    /// <summary>
    /// UV のレイアウトの比べ方。モデルの差し替えで、テクスチャセットの前のスロットと新しいスロットの UV を比べ、同じなら「見た目も同じ」、
    /// 違えば覆う所がどれだけ重なるかを知らせる（画素は変えない）。三角形の並びの順・頂点の回し方・浮動小数の小さな違い（書き出し直した FBX）は
    /// 同じとみなす。重なりは resolution × resolution のテクセルで数える（<see cref="TexturePadding.Coverage"/> と同じ、少しでも掛かるテクセル）。
    /// </summary>
    public static class UvLayout
    {
        const double Quantum = 1 << 20;
        public const int DefaultResolution = 256, MaxResolution = 4096;

        public static UvComparison Compare(IReadOnlyCollection<UvTriangle> before, IReadOnlyCollection<UvTriangle> after, int resolution = DefaultResolution)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));
            if (resolution < 1 || resolution > MaxResolution) throw new ArgumentOutOfRangeException(nameof(resolution));
            if (SameTriangles(before, after)) return new UvComparison(true, 1, 0);
            var a = TexturePadding.Coverage(resolution, resolution, Scaled(before, resolution));
            var b = TexturePadding.Coverage(resolution, resolution, Scaled(after, resolution));
            long inA = 0, inB = 0, both = 0;
            for (int i = 0; i < a.Length; i++) { if (a[i]) inA++; if (b[i]) inB++; if (a[i] && b[i]) both++; }
            return new UvComparison(false, inA == 0 ? 1 : both / (double)inA, inB == 0 ? 0 : (inB - both) / (double)inB);
        }

        /// <summary>同じ三角形の集まりか（丸めた頂点の組を、頂点の回し方を正規化して並べ替えて比べる。面積の無い三角形も数える）。</summary>
        public static bool SameTriangles(IReadOnlyCollection<UvTriangle> a, IReadOnlyCollection<UvTriangle> b)
        {
            if (a == null || b == null) throw new ArgumentNullException(a == null ? nameof(a) : nameof(b));
            if (a.Count != b.Count) return false;
            var x = Canonical(a); var y = Canonical(b);
            for (int i = 0; i < x.Length; i++) if (!x[i].Equals(y[i])) return false;
            return true;
        }

        static IEnumerable<(double, double, double, double, double, double)> Scaled(IEnumerable<UvTriangle> triangles, int n)
            => triangles.Select(t => (t.Ax * n, t.Ay * n, t.Bx * n, t.By * n, t.Cx * n, t.Cy * n));

        static (long, long, long, long, long, long)[] Canonical(IEnumerable<UvTriangle> triangles)
        {
            var list = triangles.Select(t =>
            {
                var p = new[] { (Q(t.Ax), Q(t.Ay)), (Q(t.Bx), Q(t.By)), (Q(t.Cx), Q(t.Cy)) };
                int first = 0; for (int i = 1; i < 3; i++) if (p[i].CompareTo(p[first]) < 0) first = i; // 回し方だけをそろえる（向きは変えない）
                var a = p[first]; var b = p[(first + 1) % 3]; var c = p[(first + 2) % 3];
                return (a.Item1, a.Item2, b.Item1, b.Item2, c.Item1, c.Item2);
            }).ToArray();
            Array.Sort(list);
            return list;
        }
        static long Q(double v) => double.IsNaN(v) || double.IsInfinity(v) ? long.MinValue : (long)Math.Round(v * Quantum);
    }
}
