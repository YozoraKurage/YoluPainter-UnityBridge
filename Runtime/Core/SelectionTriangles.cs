using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>三角形の集まり（UV をキャンバスの画素座標にしたもの）からの選択範囲。3D ビューで選んだ UV アイランドやメッシュの塊を
    /// 選択範囲にするのに使う。</summary>
    public sealed partial class SelectionMask
    {
        /// <summary>The union of triangles (canvas pixel coordinates, bottom-left origin), anti-aliased with 4×4 samples per pixel.
        /// A sample covered by several triangles counts once, so triangles sharing an edge leave no seam and no double coverage.
        /// A sample exactly on an edge belongs to the triangle on its left/top side only (a consistent tie rule).</summary>
        public static SelectionMask FromTriangles(PaintDocument document, IReadOnlyList<(double ax, double ay, double bx, double by, double cx, double cy)> triangles)
        {
            Require(document);
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            int width = document.Width, height = document.Height;
            if (triangles.Count == 0) return None(document);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var t in triangles)
            {
                foreach (var v in new[] { t.ax, t.ay, t.bx, t.by, t.cx, t.cy }) MathUtil.RequireFinite(v, "triangle");
                minX = Math.Min(minX, Math.Min(t.ax, Math.Min(t.bx, t.cx))); maxX = Math.Max(maxX, Math.Max(t.ax, Math.Max(t.bx, t.cx)));
                minY = Math.Min(minY, Math.Min(t.ay, Math.Min(t.by, t.cy))); maxY = Math.Max(maxY, Math.Max(t.ay, Math.Max(t.by, t.cy)));
            }
            int x0 = Math.Max(0, (int)Math.Floor(minX)), y0 = Math.Max(0, (int)Math.Floor(minY));
            int x1 = Math.Min(width, (int)Math.Ceiling(maxX) + 1), y1 = Math.Min(height, (int)Math.Ceiling(maxY) + 1);
            if (x0 >= x1 || y0 >= y1) return None(document);
            int w = x1 - x0, h = y1 - y0;
            var samples = new ushort[w * h]; // 画素ごとに 16 サンプルのビット
            foreach (var t in triangles)
            {
                double ax = t.ax, ay = t.ay, bx = t.bx, by = t.by, cx = t.cx, cy = t.cy;
                double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                if (Math.Abs(area) < 1e-12) continue;
                if (area < 0) { double sx = bx, sy = by; bx = cx; by = cy; cx = sx; cy = sy; } // 反時計回りにそろえる
                int tx0 = Math.Max(x0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), tx1 = Math.Min(x1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))) + 1);
                int ty0 = Math.Max(y0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), ty1 = Math.Min(y1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))) + 1);
                for (int py = ty0; py < ty1; py++) for (int px = tx0; px < tx1; px++)
                {
                    int bits = 0;
                    for (int sy = 0; sy < Supersample; sy++) for (int sx = 0; sx < Supersample; sx++)
                    {
                        double x = px + (sx + .5) / Supersample, y = py + (sy + .5) / Supersample;
                        if (Inside(ax, ay, bx, by, x, y) && Inside(bx, by, cx, cy, x, y) && Inside(cx, cy, ax, ay, x, y)) bits |= 1 << (sy * Supersample + sx);
                    }
                    if (bits != 0) samples[(py - y0) * w + px - x0] |= (ushort)bits;
                }
            }
            return Build(width, height, document.TileSize, (x, y) =>
            {
                int bits = samples[(y - y0) * w + x - x0], count = 0;
                while (bits != 0) { count += bits & 1; bits >>= 1; }
                return (byte)((count * 255 + Supersample * Supersample / 2) / (Supersample * Supersample));
            }, x0, y0, x1, y1, parallel: true);
        }

        /// <summary>点が辺 (a → b) の左側にあるか。辺の上の点は、辺が上向きか、水平で左向きのときだけ内側（共有辺を一方だけが持つ）。</summary>
        static bool Inside(double ax, double ay, double bx, double by, double x, double y)
        {
            double e = (bx - ax) * (y - ay) - (by - ay) * (x - ax);
            if (e != 0) return e > 0;
            return by > ay || (by == ay && bx < ax);
        }
    }
}
