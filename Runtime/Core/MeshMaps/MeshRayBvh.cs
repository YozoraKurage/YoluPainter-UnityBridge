using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core.MeshMaps
{
    /// <summary>
    /// ベイクのレイ用の BVH（12 個のビンで SAH、葉は 4 三角形まで、深さ 60 を超えたら中央値で分ける）。三角形は葉の順に
    /// v0・e1・e2 で並べ替えて持つ。作った後は読むだけなので、複数のスレッドから同時に使える（スタックは呼び出し側が渡す）。
    /// 当たり判定は両面（Möller–Trumbore）で、裏面（レイが法線と同じ向きに入る面）は指定すれば飛ばす。
    /// 節と三角形は構造体の配列にして ref で読む（Mono は配列の添字の範囲確認を要素ごとにするので、1 回の確認で済ませる）。
    /// 計算は double（Unity の Mono は float の式も double で計算し、float の変数に入れるたびに変換するので、double の方が速い）。
    /// </summary>
    internal sealed class MeshRayBvh
    {
        const int LeafSize = 4, Bins = 12, MaxDepth = 60;
        public const int StackSize = 128;

        struct Node { public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ; public int First, Count; } // Count 0 は内側の節（First が左の子、右はその次）
        struct Tri { public double Ax, Ay, Az, E1x, E1y, E1z, E2x, E2y, E2z, Epsilon; public int Original; }

        readonly Node[] nodes;
        readonly Tri[] tris;
        int nodeCount;
        // 作る間だけ使う
        float[] triBounds, centers; int[] order; float[] sortKeys;

        public int TriangleCount => tris.Length;
        public int NodeCount => nodeCount;

        /// <summary>見積もり: 三角形 1 つあたりのバイト（節は三角形の 2 倍まで、作る間の一時領域を含む）。</summary>
        public const long BytesPerTriangle = 2 * 56 + 88 + 6 * 4 + 3 * 4 + 4 + 4;

        public MeshRayBvh(float[] corners, IReadOnlyList<int> triangles)
        {
            int n = triangles.Count;
            tris = new Tri[n]; nodes = new Node[Math.Max(1, 2 * n)];
            if (n == 0) { nodeCount = 0; return; }
            triBounds = new float[n * 6]; centers = new float[n * 3]; order = new int[n]; sortKeys = new float[n];
            for (int i = 0; i < n; i++)
            {
                int k = triangles[i] * 9;
                for (int axis = 0; axis < 3; axis++)
                {
                    float a = corners[k + axis], b = corners[k + 3 + axis], c = corners[k + 6 + axis];
                    float lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
                    triBounds[i * 6 + axis] = lo; triBounds[i * 6 + 3 + axis] = hi; centers[i * 3 + axis] = (lo + hi) * 0.5f;
                }
                order[i] = i;
            }
            nodeCount = 1;
            Build(0, 0, n, 0);
            for (int i = 0; i < n; i++)
            {
                int source = triangles[order[i]], k = source * 9;
                double ax = corners[k], ay = corners[k + 1], az = corners[k + 2];
                double e1x = corners[k + 3] - ax, e1y = corners[k + 4] - ay, e1z = corners[k + 5] - az;
                double e2x = corners[k + 6] - ax, e2y = corners[k + 7] - ay, e2z = corners[k + 8] - az;
                tris[i] = new Tri
                {
                    Ax = ax, Ay = ay, Az = az, E1x = e1x, E1y = e1y, E1z = e1z, E2x = e2x, E2y = e2y, E2z = e2z, Original = source,
                    Epsilon = 1e-9 * Math.Sqrt((e1x * e1x + e1y * e1y + e1z * e1z) * (e2x * e2x + e2y * e2y + e2z * e2z)),
                };
            }
            triBounds = null; centers = null; order = null; sortKeys = null;
        }

        void Build(int node, int start, int n, int depth)
        {
            float bx0 = float.MaxValue, by0 = float.MaxValue, bz0 = float.MaxValue, bx1 = float.MinValue, by1 = float.MinValue, bz1 = float.MinValue;
            float cx0 = float.MaxValue, cy0 = float.MaxValue, cz0 = float.MaxValue, cx1 = float.MinValue, cy1 = float.MinValue, cz1 = float.MinValue;
            for (int i = start; i < start + n; i++)
            {
                int t = order[i] * 6, c = order[i] * 3;
                bx0 = Math.Min(bx0, triBounds[t]); by0 = Math.Min(by0, triBounds[t + 1]); bz0 = Math.Min(bz0, triBounds[t + 2]);
                bx1 = Math.Max(bx1, triBounds[t + 3]); by1 = Math.Max(by1, triBounds[t + 4]); bz1 = Math.Max(bz1, triBounds[t + 5]);
                cx0 = Math.Min(cx0, centers[c]); cy0 = Math.Min(cy0, centers[c + 1]); cz0 = Math.Min(cz0, centers[c + 2]);
                cx1 = Math.Max(cx1, centers[c]); cy1 = Math.Max(cy1, centers[c + 1]); cz1 = Math.Max(cz1, centers[c + 2]);
            }
            nodes[node] = new Node { MinX = bx0, MinY = by0, MinZ = bz0, MaxX = bx1, MaxY = by1, MaxZ = bz1, First = start, Count = n };
            if (n <= LeafSize) return;
            float ex = cx1 - cx0, ey = cy1 - cy0, ez = cz1 - cz0;
            int axis = ex >= ey && ex >= ez ? 0 : ey >= ez ? 1 : 2;
            float lo = axis == 0 ? cx0 : axis == 1 ? cy0 : cz0, extent = axis == 0 ? ex : axis == 1 ? ey : ez;
            int mid = -1;
            if (extent > 1e-30f && depth < MaxDepth)
            {
                var binCount = new int[Bins]; var binBounds = new float[Bins * 6];
                for (int i = 0; i < Bins; i++) { binBounds[i * 6] = binBounds[i * 6 + 1] = binBounds[i * 6 + 2] = float.MaxValue; binBounds[i * 6 + 3] = binBounds[i * 6 + 4] = binBounds[i * 6 + 5] = float.MinValue; }
                float scale = Bins / extent;
                for (int i = start; i < start + n; i++)
                {
                    int bin = BinOf(centers[order[i] * 3 + axis], lo, scale); binCount[bin]++;
                    int t = order[i] * 6, o = bin * 6;
                    for (int k = 0; k < 3; k++) { binBounds[o + k] = Math.Min(binBounds[o + k], triBounds[t + k]); binBounds[o + 3 + k] = Math.Max(binBounds[o + 3 + k], triBounds[t + 3 + k]); }
                }
                // 左から・右から累積した面積と数で、ビンの境目ごとの SAH の費用を求める
                var rightArea = new float[Bins]; var rightCount = new int[Bins];
                float[] acc = { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue }; int accCount = 0;
                for (int i = Bins - 1; i > 0; i--) { Grow(acc, binBounds, i); accCount += binCount[i]; rightArea[i] = Area(acc); rightCount[i] = accCount; }
                acc = new[] { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue }; accCount = 0;
                float bestCost = float.MaxValue; int bestSplit = -1;
                for (int i = 0; i < Bins - 1; i++)
                {
                    Grow(acc, binBounds, i); accCount += binCount[i];
                    if (accCount == 0 || rightCount[i + 1] == 0) continue;
                    float cost = Area(acc) * accCount + rightArea[i + 1] * rightCount[i + 1];
                    if (cost < bestCost) { bestCost = cost; bestSplit = i; }
                }
                if (bestSplit >= 0)
                {
                    int left = start, right = start + n - 1;
                    while (left <= right)
                    {
                        if (BinOf(centers[order[left] * 3 + axis], lo, scale) <= bestSplit) left++;
                        else { int temp = order[left]; order[left] = order[right]; order[right] = temp; right--; }
                    }
                    mid = left;
                    if (mid == start || mid == start + n) mid = -1;
                }
            }
            if (mid < 0)
            {
                // 中心がほぼ一点、または深すぎる: 軸の上の並びで半分に分ける（決まった順。深さは log で止まる）
                for (int i = start; i < start + n; i++) sortKeys[i] = centers[order[i] * 3 + axis];
                Array.Sort(sortKeys, order, start, n);
                mid = start + n / 2;
            }
            int child = nodeCount; nodeCount += 2;
            nodes[node].First = child; nodes[node].Count = 0;
            Build(child, start, mid - start, depth + 1);
            Build(child + 1, mid, start + n - mid, depth + 1);
        }
        static int BinOf(float center, float lo, float scale) { int bin = (int)((center - lo) * scale); return bin < 0 ? 0 : bin >= Bins ? Bins - 1 : bin; }
        static void Grow(float[] acc, float[] bins, int bin)
        {
            int o = bin * 6; if (bins[o] > bins[o + 3]) return; // 空のビン
            for (int k = 0; k < 3; k++) { acc[k] = Math.Min(acc[k], bins[o + k]); acc[3 + k] = Math.Max(acc[3 + k], bins[o + 3 + k]); }
        }
        static float Area(float[] b)
        {
            if (b[0] > b[3]) return 0;
            float x = b[3] - b[0], y = b[4] - b[1], z = b[5] - b[2];
            return x * y + y * z + z * x;
        }

        /// <summary>レイを飛ばす。方向は単位ベクトル。距離が 0 より大きく tMax より小さい当たりだけを数え、元の番号が ignore の三角形は
        /// 無視する。anyHit なら最初に見つけた当たり（最も近いとは限らない）で返す。</summary>
        /// <returns>当たった距離。当たらなければ float.PositiveInfinity。</returns>
        public double Trace(double ox, double oy, double oz, double dx, double dy, double dz, double tMax, int ignore, bool anyHit, bool ignoreBackfaces, int[] stack, double[] stackT)
        {
            if (nodeCount == 0) return double.PositiveInfinity;
            // 0 の成分は小さな値に置き換え、逆数を有限のまま使う（0×∞ の NaN を避ける）
            if (dx == 0) dx = 1e-300; if (dy == 0) dy = 1e-300; if (dz == 0) dz = 1e-300;
            double ix = 1 / dx, iy = 1 / dy, iz = 1 / dz, oix = ox * ix, oiy = oy * iy, oiz = oz * iz;
            // 向きの符号で、箱の手前と奥の面（min か max か）を決めておく
            bool px = ix >= 0, py = iy >= 0, pz = iz >= 0;
            var nodes = this.nodes; var tris = this.tris;
            double best = tMax; bool found = false;
            {
                ref Node root = ref nodes[0];
                if (!Hit(ref root, px, py, pz, ix, iy, iz, oix, oiy, oiz, best, out _)) return double.PositiveInfinity;
            }
            int sp = 0, node = 0;
            while (true)
            {
                ref Node current = ref nodes[node];
                if (current.Count > 0)
                {
                    int end = current.First + current.Count;
                    for (int t = current.First; t < end; t++)
                    {
                        ref Tri tr = ref tris[t];
                        double e2x = tr.E2x, e2y = tr.E2y, e2z = tr.E2z;
                        double qx0 = dy * e2z - dz * e2y, qy0 = dz * e2x - dx * e2z, qz0 = dx * e2y - dy * e2x;
                        double e1x = tr.E1x, e1y = tr.E1y, e1z = tr.E1z;
                        double det = e1x * qx0 + e1y * qy0 + e1z * qz0, eps = tr.Epsilon;
                        if (det > -eps && det < eps) continue;
                        if (ignoreBackfaces && det < 0) continue;
                        double inv = 1 / det, sx = ox - tr.Ax, sy = oy - tr.Ay, sz = oz - tr.Az;
                        double u = (sx * qx0 + sy * qy0 + sz * qz0) * inv;
                        if (u < 0 || u > 1) continue;
                        double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                        double v = (dx * qx + dy * qy + dz * qz) * inv;
                        if (v < 0 || u + v > 1) continue;
                        double hit = (e2x * qx + e2y * qy + e2z * qz) * inv;
                        if (hit <= 0 || hit >= best || tr.Original == ignore) continue;
                        best = hit; found = true;
                        if (anyHit) return best;
                    }
                }
                else
                {
                    int l = current.First;
                    // 2 つの子の箱（呼び出しにすると Mono で遅いので、ここに書き下す）
                    ref Node ln = ref nodes[l];
                    double a0 = (px ? ln.MinX : ln.MaxX) * ix - oix, a1 = (py ? ln.MinY : ln.MaxY) * iy - oiy, a2 = (pz ? ln.MinZ : ln.MaxZ) * iz - oiz;
                    double c0 = (px ? ln.MaxX : ln.MinX) * ix - oix, c1 = (py ? ln.MaxY : ln.MinY) * iy - oiy, c2 = (pz ? ln.MaxZ : ln.MinZ) * iz - oiz;
                    double l0 = a0 > a1 ? a0 : a1; if (a2 > l0) l0 = a2; if (l0 < 0) l0 = 0;
                    double l1 = c0 < c1 ? c0 : c1; if (c2 < l1) l1 = c2; if (best < l1) l1 = best;
                    ref Node rn = ref nodes[l + 1];
                    a0 = (px ? rn.MinX : rn.MaxX) * ix - oix; a1 = (py ? rn.MinY : rn.MaxY) * iy - oiy; a2 = (pz ? rn.MinZ : rn.MaxZ) * iz - oiz;
                    c0 = (px ? rn.MaxX : rn.MinX) * ix - oix; c1 = (py ? rn.MaxY : rn.MinY) * iy - oiy; c2 = (pz ? rn.MaxZ : rn.MinZ) * iz - oiz;
                    double r0 = a0 > a1 ? a0 : a1; if (a2 > r0) r0 = a2; if (r0 < 0) r0 = 0;
                    double r1 = c0 < c1 ? c0 : c1; if (c2 < r1) r1 = c2; if (best < r1) r1 = best;
                    bool hl = l0 <= l1, hr = r0 <= r1;
                    if (hl && hr)
                    {
                        // 近い方へ進み、遠い方は入る距離と一緒に積む
                        if (l0 <= r0) { node = l; stack[sp] = l + 1; stackT[sp++] = r0; }
                        else { node = l + 1; stack[sp] = l; stackT[sp++] = l0; }
                        continue;
                    }
                    if (hl) { node = l; continue; }
                    if (hr) { node = l + 1; continue; }
                }
                do { if (sp == 0) return found ? best : double.PositiveInfinity; sp--; } while (stackT[sp] >= best);
                node = stack[sp];
            }
        }

        /// <summary>箱とレイ（0〜limit）の当たり。Math.Max/Min は Mono で呼び出しになるので比較で書く。</summary>
        static bool Hit(ref Node n, bool px, bool py, bool pz, double ix, double iy, double iz, double oix, double oiy, double oiz, double limit, out double enter)
        {
            double a0 = (px ? n.MinX : n.MaxX) * ix - oix, a1 = (py ? n.MinY : n.MaxY) * iy - oiy, a2 = (pz ? n.MinZ : n.MaxZ) * iz - oiz;
            double c0 = (px ? n.MaxX : n.MinX) * ix - oix, c1 = (py ? n.MaxY : n.MinY) * iy - oiy, c2 = (pz ? n.MaxZ : n.MinZ) * iz - oiz;
            double t0 = a0 > a1 ? a0 : a1; if (a2 > t0) t0 = a2; if (t0 < 0) t0 = 0;
            double t1 = c0 < c1 ? c0 : c1; if (c2 < t1) t1 = c2; if (limit < t1) t1 = limit;
            enter = t0;
            return t0 <= t1;
        }
    }
}
