using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>
    /// 書き出しのパディング（UV の外への塗り広げ。Substance Painter の書き出しの「Dilation」）。UV の三角形が少しでも重なるテクセル（<see cref="Coverage"/>）は
    /// そのまま残し、その外のテクセルを、外へ 1 テクセルずつ広げながら、すでに埋まった 8 近傍の色で埋める。Unity がミップマップで縮めたり、
    /// バイリニアで UV の境目の外を読んだりしても、背景（透明や黒）がにじまないようにするため。
    /// 正本は変えない。書き出す画像（straight RGBA8、左下が原点）を作り直すだけ。
    /// </summary>
    public static class TexturePadding
    {
        /// <summary><see cref="Dilate"/> の texels にこれを渡すと、届くかぎり全部を埋める（Substance の「Infinite」）。</summary>
        public const int Fill = -1;

        /// <summary>
        /// UV の三角形（画素の座標、左下が原点。UV × 大きさ）が少しでも重なるテクセルの印（[y * width + x]）。テクセル (x, y) は正方形 [x, x+1] × [y, y+1]。
        /// 重なりは分離軸（x・y と三角形の 3 辺の法線）で判定する（保守的: 角に触れるだけでも重なりとする）。
        /// 塗った画素は UV の境目の上のテクセル（三角形が一部だけ覆う）にも入るので、それを書き出しで塗り広げの色に置き換えないため。
        /// 画像の外にはみ出した部分は切る（UV の繰り返しは見ない）。NaN や無限大を含む三角形は飛ばす。
        /// </summary>
        public static bool[] Coverage(int width, int height, IEnumerable<(double ax, double ay, double bx, double by, double cx, double cy)> triangles)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            var covered = new bool[checked(width * height)];
            foreach (var t in triangles)
            {
                if (!Finite(t.ax) || !Finite(t.ay) || !Finite(t.bx) || !Finite(t.by) || !Finite(t.cx) || !Finite(t.cy)) continue;
                double minX = Math.Min(t.ax, Math.Min(t.bx, t.cx)), maxX = Math.Max(t.ax, Math.Max(t.bx, t.cx));
                double minY = Math.Min(t.ay, Math.Min(t.by, t.cy)), maxY = Math.Max(t.ay, Math.Max(t.by, t.cy));
                int x0 = Math.Max(0, (int)Math.Floor(minX) - (minX == Math.Floor(minX) ? 1 : 0)), x1 = Math.Min(width - 1, (int)Math.Floor(maxX));
                int y0 = Math.Max(0, (int)Math.Floor(minY) - (minY == Math.Floor(minY) ? 1 : 0)), y1 = Math.Min(height - 1, (int)Math.Floor(maxY));
                if (x0 > x1 || y0 > y1) continue;
                // 3 辺の法線（向きは問わない）と、各法線への三角形の射影の範囲
                Span3 e0 = Edge(t.ax, t.ay, t.bx, t.by, t.cx, t.cy), e1 = Edge(t.bx, t.by, t.cx, t.cy, t.ax, t.ay), e2 = Edge(t.cx, t.cy, t.ax, t.ay, t.bx, t.by);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = y * width + x;
                        if (covered[i]) continue;
                        if (e0.Separates(x, y) || e1.Separates(x, y) || e2.Separates(x, y)) continue;
                        covered[i] = true;
                    }
            }
            return covered;
        }

        /// <summary>辺 (a → b) の法線への射影: 三角形は [min, max] にあり、正方形の 4 隅の射影の範囲と重ならなければ分離している。</summary>
        readonly struct Span3
        {
            readonly double nx, ny, min, max; readonly bool none;
            public Span3(double nx, double ny, double min, double max) { this.nx = nx; this.ny = ny; this.min = min; this.max = max; none = nx == 0 && ny == 0; }
            public bool Separates(int x, int y)
            {
                if (none) return false;
                double p0 = nx * x + ny * y, px = nx, py = ny; // 正方形の隅 (x, y) からの差: (1,0)→px、(0,1)→py
                double lo = p0 + Math.Min(0, px) + Math.Min(0, py), hi = p0 + Math.Max(0, px) + Math.Max(0, py);
                return hi < min || lo > max;
            }
        }
        static Span3 Edge(double ax, double ay, double bx, double by, double cx, double cy)
        {
            double nx = -(by - ay), ny = bx - ax; // 辺の法線
            double pa = nx * ax + ny * ay, pc = nx * cx + ny * cy; // a と b は同じ値
            return new Span3(nx, ny, Math.Min(pa, pc), Math.Max(pa, pc));
        }
        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>
        /// keep の外のテクセルを、外へ 1 段ずつ埋めた新しい画像を返す（入力は変えない）。段 k で埋まるのは、段 k−1 までに埋まった 8 近傍を持つテクセルで、
        /// 色はそれらの近傍の、アルファで重みを付けた色の平均（全部が透明なら色の平均）と、アルファの平均（どちらも四捨五入）。同じ段の中では
        /// 前の段の結果だけを読むので、結果は処理の順に依らない。texels 段で止める（<see cref="Fill"/> なら届くかぎり）。届かなかったテクセルは元のまま。
        /// keep が 1 つも無ければ何もしない（元の写しを返す）。作業のメモリ（画像の写しと段の印）が maxWorkingBytes を超えるなら断る。
        /// </summary>
        public static byte[] Dilate(byte[] rgba, int width, int height, bool[] keep, int texels, long maxWorkingBytes = long.MaxValue)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (keep == null) throw new ArgumentNullException(nameof(keep));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height));
            long n = (long)width * height;
            if (rgba.LongLength != n * 4 || keep.LongLength != n) throw new ArgumentException("The image and the coverage must be width × height.");
            if (texels < Fill) throw new ArgumentOutOfRangeException(nameof(texels));
            if (n * 4 + n * 4 + n * 4 > maxWorkingBytes) throw new InvalidOperationException("Padding needs more working memory than the budget allows.");
            var output = (byte[])rgba.Clone();
            if (texels == 0) return output;
            var step = new int[n]; // 0: 埋まっていない、1: 残すテクセル、k+1: 段 k で埋めた
            var frontier = new List<int>();
            for (int i = 0; i < n; i++) if (keep[i]) step[i] = 1;
            for (int i = 0; i < n; i++)
                if (keep[i] && HasNeighbor((int)(i % width), (int)(i / width), width, height, step, s => s == 0)) frontier.Add(i);
            var candidates = new List<int>(); var colors = new List<uint>();
            for (int k = 1; frontier.Count > 0 && (texels == Fill || k <= texels); k++)
            {
                candidates.Clear(); colors.Clear();
                int mark = -(k + 1); // 段 k の候補の印（負の値。0 の「空き」とも、埋まった正の値とも区別する）
                foreach (int f in frontier)
                {
                    int fx = f % width, fy = f / width;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = fx + dx, y = fy + dy;
                            if ((dx | dy) == 0 || x < 0 || y < 0 || x >= width || y >= height) continue;
                            int j = y * width + x;
                            if (step[j] != 0) continue;
                            step[j] = mark; candidates.Add(j);
                        }
                }
                candidates.Sort(); // 色の計算と書き込みの順を決める（結果は順に依らないが、同じ入力で同じ手順にする）
                foreach (int c in candidates) colors.Add(Average(c % width, c / width, width, height, step, output));
                for (int m = 0; m < candidates.Count; m++)
                {
                    int c = candidates[m]; uint v = colors[m];
                    output[c * 4] = (byte)v; output[c * 4 + 1] = (byte)(v >> 8); output[c * 4 + 2] = (byte)(v >> 16); output[c * 4 + 3] = (byte)(v >> 24);
                    step[c] = k + 1;
                }
                frontier.Clear(); frontier.AddRange(candidates);
            }
            return output;
        }

        static bool HasNeighbor(int x, int y, int width, int height, int[] step, Func<int, bool> test)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    if (test(step[ny * width + nx])) return true;
                }
            return false;
        }

        /// <summary>埋まった（正の段の）8 近傍の色の平均。色はアルファで重みを付ける（透明な近傍の色に引っ張られない）。全部が透明なら色の平均。</summary>
        static uint Average(int x, int y, int width, int height, int[] step, byte[] image)
        {
            long r = 0, g = 0, b = 0, a = 0, rw = 0, gw = 0, bw = 0; int count = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int j = ny * width + nx;
                    if (step[j] <= 0) continue;
                    int o = j * 4; int alpha = image[o + 3];
                    r += image[o]; g += image[o + 1]; b += image[o + 2]; a += alpha; count++;
                    rw += image[o] * alpha; gw += image[o + 1] * alpha; bw += image[o + 2] * alpha;
                }
            if (count == 0) return 0; // 候補は埋まった近傍から来るので起きない
            uint R, G, B;
            if (a > 0) { R = Round(rw, a); G = Round(gw, a); B = Round(bw, a); }
            else { R = Round(r, count); G = Round(g, count); B = Round(b, count); }
            uint A = Round(a, count);
            return R | G << 8 | B << 16 | A << 24;
        }
        static uint Round(long sum, long count) => (uint)((2 * sum + count) / (2 * count));
    }
}
