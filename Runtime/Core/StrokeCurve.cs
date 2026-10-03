using System;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>
    /// 手で描いた入力の点を結ぶ曲線: centripetal Catmull-Rom（α = 0.5。区間の中で尖りも自己交差も作らず、点の間隔が不揃いでも
    /// 行き過ぎが小さい）。区間 p1 → p2 を、前後の点 p0・p3 で向きを決めて結ぶ。曲線は制御点を必ず通り、一直線に並んだ点では
    /// 直線のまま。前後の点が無い端は <see cref="Reflect"/> の点（端の点の向こうへ折り返した点）で補う。
    /// 2D のストローク（<see cref="BrushSettings.CurveInterpolation"/>）と、3D ビューのストロークの画面上の補間が使う。
    /// 2D のパス（Paths.CanvasPathRenderer）も同じ曲線だが、あちらは保存済みのパスの画素を変えないよう自分の式のまま。
    /// </summary>
    public static class StrokeCurve
    {
        /// <summary>重なった点とみなす距離（画素や GUI の点）。これより近い点の間は区間を作らない。</summary>
        public const double CoincidentDistance = 1e-6;

        /// <summary>区間 p1 → p2 の t（0 で p1、1 で p2）の位置（Barry と Goldman のピラミッド形の式）。</summary>
        public static void Point(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double t, out double x, out double y)
        {
            double k01 = Knot(x0, y0, x1, y1), k12 = Knot(x1, y1, x2, y2), k23 = Knot(x2, y2, x3, y3);
            double t0 = 0, t1 = k01, t2 = t1 + k12, t3 = t2 + k23;
            double u = t1 + k12 * t;
            double a1x = Mix(x0, x1, t0, t1, u), a1y = Mix(y0, y1, t0, t1, u);
            double a2x = Mix(x1, x2, t1, t2, u), a2y = Mix(y1, y2, t1, t2, u);
            double a3x = Mix(x2, x3, t2, t3, u), a3y = Mix(y2, y3, t2, t3, u);
            double b1x = Mix(a1x, a2x, t0, t2, u), b1y = Mix(a1y, a2y, t0, t2, u);
            double b2x = Mix(a2x, a3x, t1, t3, u), b2y = Mix(a2y, a3y, t1, t3, u);
            x = Mix(b1x, b2x, t1, t2, u); y = Mix(b1y, b2y, t1, t2, u);
        }

        /// <summary>端の外の点: p を about の向こうへ折り返した点（2·about − p）。端の区間はその向きの接線で始まり・終わる。</summary>
        public static void Reflect(double x, double y, double aboutX, double aboutY, out double rx, out double ry)
        { rx = 2 * aboutX - x; ry = 2 * aboutY - y; }

        /// <summary>2 点が <see cref="CoincidentDistance"/> より近いか。</summary>
        public static bool Coincident(double x0, double y0, double x1, double y1)
        { double dx = x1 - x0, dy = y1 - y0; return dx * dx + dy * dy < CoincidentDistance * CoincidentDistance; }

        // 結び目の間隔は距離の平方根（centripetal）。重なった前後の点でも割り算が壊れないよう下限を置く。
        static double Knot(double xa, double ya, double xb, double yb)
        { double dx = xb - xa, dy = yb - ya; return Math.Max(1e-9, Math.Sqrt(Math.Sqrt(dx * dx + dy * dy))); }
        static double Mix(double a, double b, double ta, double tb, double u) => (tb - u) / (tb - ta) * a + (u - ta) / (tb - ta) * b;
    }
}
