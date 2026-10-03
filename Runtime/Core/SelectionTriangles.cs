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
        /// A sample exactly on an edge belongs to the triangle on its left/top side only (a consistent tie rule).
        /// <see cref="TriangleFill"/> covers pixels with the same samples (<see cref="TriangleSamples"/>).</summary>
        public static SelectionMask FromTriangles(PaintDocument document, IReadOnlyList<(double ax, double ay, double bx, double by, double cx, double cy)> triangles)
        {
            Require(document);
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            int width = document.Width, height = document.Height, tileSize = document.TileSize;
            var prepared = TriangleSamples.Prepare(triangles, width, height);
            var mask = new SelectionMask(width, height, tileSize);
            if (prepared.Count == 0) return mask;
            var bins = TriangleSamples.Bin(prepared, tileSize);
            var coords = new List<TileCoord>(bins.Keys); coords.Sort();
            int n = tileSize * tileSize;
            // まとめの枠ごとに作業用の配列を先に用意して使い回す（ワーカーでは割り当てない）
            const int Batch = 64;
            int slots = Math.Min(Batch, coords.Count);
            var bits = new ushort[slots][]; var rgba = new byte[slots][]; var any = new bool[slots];
            for (int k = 0; k < slots; k++) { bits[k] = new ushort[n]; rgba[k] = new byte[n * 4]; }
            for (int start = 0; start < coords.Count; start += Batch)
            {
                int count = Math.Min(Batch, coords.Count - start), first = start;
                CoreParallelism.For(count, CoreParallelism.Degree, k =>
                {
                    var coord = coords[first + k]; var b = bits[k]; var bytes = rgba[k];
                    Array.Clear(b, 0, n); Array.Clear(bytes, 0, bytes.Length);
                    TriangleSamples.Rasterize(prepared, bins[coord], coord, tileSize, width, height, b);
                    bool found = false;
                    for (int i = 0; i < n; i++) if (b[i] != 0) { bytes[i * 4 + 3] = TriangleSamples.Coverage(b[i]); found = true; }
                    any[k] = found;
                });
                for (int k = 0; k < count; k++) if (any[k]) mask.surface.ImportTile(coords[first + k], rgba[k]);
            }
            return mask;
        }
    }

    /// <summary>
    /// 三角形が覆う画素の 4×4 のサンプル（<see cref="SelectionMask.FromTriangles"/> と <see cref="TriangleFill"/> で共有）。画素ごとに 16 ビット
    /// （ビット sy·4 + sx がサンプル (px + (sx + .5)/4, py + (sy + .5)/4)）。三角形は反時計回りにそろえ、サンプルは 3 本の辺の左側
    /// （<see cref="Inside"/>、辺の上の点は辺が上向きか、水平で左向きのときだけ）にあれば覆われる。
    /// 速くするために、画素のサンプルの外接の 4 隅で辺の式を見て、どの隅でも余裕を持って内側（または 1 本の辺のどの隅でも外側）なら
    /// サンプルごとの判定を飛ばす。辺の式はサンプルの位置について 1 次なので、4 隅の値がどれも m を超えればどのサンプルの真の値も m を
    /// 超える。余裕 m = 1e-12·(M + 1)²（M は三角形の座標とキャンバスの大きさの絶対値の最大）は、倍精度で辺の式を計算する誤差（多くて
    /// 約 32·2⁻⁵³·M²）の 100 倍を超えるので、早道とサンプルごとの判定は同じビットを出す（余裕の内側の画素はサンプルごとに判定する）。
    /// </summary>
    internal static class TriangleSamples
    {
        internal const int Supersample = 4;
        /// <summary>画素のサンプルを全部覆ったときのビット。</summary>
        internal const ushort Full = 0xFFFF;

        /// <summary>準備した三角形: 反時計回りの頂点、キャンバスに切った画素の範囲 [X0, X1) × [Y0, Y1)、早道の余裕。</summary>
        internal struct Prepared
        {
            public double Ax, Ay, Bx, By, Cx, Cy, Margin;
            public int X0, Y0, X1, Y1;
        }

        /// <summary>座標が有限かをすべて確かめてから（どれかが NaN・無限なら何も返さずに投げる）、面積がほぼ 0 のものとキャンバスの
        /// 外のものを除いて準備する。</summary>
        internal static List<Prepared> Prepare(IReadOnlyList<(double ax, double ay, double bx, double by, double cx, double cy)> triangles, int width, int height)
        {
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            foreach (var t in triangles)
                foreach (var v in new[] { t.ax, t.ay, t.bx, t.by, t.cx, t.cy }) MathUtil.RequireFinite(v, "triangle");
            var list = new List<Prepared>(triangles.Count);
            foreach (var t in triangles)
            {
                double ax = t.ax, ay = t.ay, bx = t.bx, by = t.by, cx = t.cx, cy = t.cy;
                double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                if (Math.Abs(area) < 1e-12) continue;
                if (area < 0) { double sx = bx, sy = by; bx = cx; by = cy; cx = sx; cy = sy; } // 反時計回りにそろえる
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(width, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))) + 1);
                int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), y1 = Math.Min(height, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))) + 1);
                if (x0 >= x1 || y0 >= y1) continue;
                double m = Math.Max(Math.Max(width, height), Math.Max(Math.Max(Math.Abs(ax), Math.Abs(ay)), Math.Max(Math.Max(Math.Abs(bx), Math.Abs(by)), Math.Max(Math.Abs(cx), Math.Abs(cy)))));
                list.Add(new Prepared { Ax = ax, Ay = ay, Bx = bx, By = by, Cx = cx, Cy = cy, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Margin = 1e-12 * (m + 1) * (m + 1) });
            }
            return list;
        }

        /// <summary>タイルごとの、範囲が掛かる三角形の番号（prepared の中の番号、昇順）。</summary>
        internal static Dictionary<TileCoord, List<int>> Bin(List<Prepared> prepared, int tileSize)
        {
            var bins = new Dictionary<TileCoord, List<int>>();
            for (int i = 0; i < prepared.Count; i++)
            {
                var p = prepared[i];
                for (int ty = p.Y0 / tileSize; ty <= (p.Y1 - 1) / tileSize; ty++)
                    for (int tx = p.X0 / tileSize; tx <= (p.X1 - 1) / tileSize; tx++)
                    {
                        var c = new TileCoord(tx, ty);
                        if (!bins.TryGetValue(c, out var list)) bins.Add(c, list = new List<int>());
                        list.Add(i);
                    }
            }
            return bins;
        }

        /// <summary>タイル coord の画素のうち、triangles（prepared の番号）が覆うサンプルのビットを bits（タイルの中の行優先、TileSize²）に
        /// OR する。新しく立ったビットがあれば true。bits には書くだけで、ほかの物には触らない（タイルごとに別のスレッドで呼べる）。</summary>
        internal static bool Rasterize(List<Prepared> prepared, List<int> triangles, TileCoord coord, int tileSize, int width, int height, ushort[] bits)
        {
            int left = coord.X * tileSize, bottom = coord.Y * tileSize;
            int right = Math.Min(width, left + tileSize), top = Math.Min(height, bottom + tileSize);
            bool changed = false;
            const double Low = .5 / Supersample, High = 1 - .5 / Supersample; // 画素の中のサンプルの外接の隅
            foreach (int index in triangles)
            {
                var t = prepared[index];
                double ax = t.Ax, ay = t.Ay, bx = t.Bx, by = t.By, cx = t.Cx, cy = t.Cy, m = t.Margin;
                int x0 = Math.Max(left, t.X0), x1 = Math.Min(right, t.X1), y0 = Math.Max(bottom, t.Y0), y1 = Math.Min(top, t.Y1);
                for (int py = y0; py < y1; py++)
                {
                    double yl = py + Low, yh = py + High;
                    int row = (py - bottom) * tileSize - left;
                    for (int px = x0; px < x1; px++)
                    {
                        double xl = px + Low, xh = px + High;
                        int bitsHere;
                        int ab = Side(ax, ay, bx, by, xl, xh, yl, yh, m), bc = Side(bx, by, cx, cy, xl, xh, yl, yh, m), ca = Side(cx, cy, ax, ay, xl, xh, yl, yh, m);
                        if (ab < 0 || bc < 0 || ca < 0) continue; // どれかの辺の外側
                        if (ab > 0 && bc > 0 && ca > 0) bitsHere = Full;
                        else
                        {
                            bitsHere = 0;
                            for (int sy = 0; sy < Supersample; sy++) for (int sx = 0; sx < Supersample; sx++)
                            {
                                double x = px + (sx + .5) / Supersample, y = py + (sy + .5) / Supersample;
                                if (Inside(ax, ay, bx, by, x, y) && Inside(bx, by, cx, cy, x, y) && Inside(cx, cy, ax, ay, x, y)) bitsHere |= 1 << (sy * Supersample + sx);
                            }
                            if (bitsHere == 0) continue;
                        }
                        int i = row + px; int old = bits[i], next = old | bitsHere;
                        if (next != old) { bits[i] = (ushort)next; changed = true; }
                    }
                }
            }
            return changed;
        }

        /// <summary>辺 (a → b) から見た画素のサンプルの外接の 4 隅: どれも余裕 m を超えて左（内側）なら 1、どれも −m より右（外側）なら −1、
        /// それ以外（サンプルごとに判定する）は 0。</summary>
        static int Side(double ax, double ay, double bx, double by, double xl, double xh, double yl, double yh, double m)
        {
            double dx = bx - ax, dy = by - ay;
            double e0 = dx * (yl - ay) - dy * (xl - ax), e1 = dx * (yl - ay) - dy * (xh - ax), e2 = dx * (yh - ay) - dy * (xl - ax), e3 = dx * (yh - ay) - dy * (xh - ax);
            if (e0 > m && e1 > m && e2 > m && e3 > m) return 1;
            if (e0 < -m && e1 < -m && e2 < -m && e3 < -m) return -1;
            return 0;
        }

        /// <summary>点が辺 (a → b) の左側にあるか。辺の上の点は、辺が上向きか、水平で左向きのときだけ内側（共有辺を一方だけが持つ）。</summary>
        internal static bool Inside(double ax, double ay, double bx, double by, double x, double y)
        {
            double e = (bx - ax) * (y - ay) - (by - ay) * (x - ax);
            if (e != 0) return e > 0;
            return by > ay || (by == ay && bx < ax);
        }

        /// <summary>覆われたサンプルの数を 0〜255 にしたもの（16 個なら 255）。</summary>
        internal static byte Coverage(ushort bits)
        {
            int count = 0, b = bits;
            while (b != 0) { count += b & 1; b >>= 1; }
            return (byte)((count * 255 + Supersample * Supersample / 2) / (Supersample * Supersample));
        }
    }
}
