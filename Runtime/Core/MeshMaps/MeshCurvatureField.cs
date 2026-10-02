using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core.MeshMaps
{
    /// <summary>
    /// 曲率: 位置で溶接した辺の二面角を、テクセルの位置を中心とする半径 r の球の中で、滑らかな重み (1 − d²/r²)² を付けて
    /// 辺に沿って積分する（離散の平均曲率の積分 ½Σφ|e| の考え方）。凸の辺は +φ、凹の辺は −φ。正規化は「中心を通る 90° の
    /// 辺が ±1」になる定数 (π/2)(16/15)r で割る。まっすぐな 90° の辺から距離 x のテクセルの値は (1 − x²/r²)^(5/2)。
    /// 頂点法線（スムーズの設定）や UV の継ぎ目には依らない（ハードエッジの立方体もスムーズの立方体も同じ角が出る）。
    /// 縁の辺（面が 1 つ）、非多様体の辺（3 つ以上）、向きのそろわない辺は数えない。球の中でも、つながっていない別の部品
    /// （溶接した辺でつながる成分が違う）の辺は数えない。
    /// </summary>
    internal sealed class MeshCurvatureField
    {
        readonly double radius, cell, norm, minX, minY, minZ;
        readonly double[] segments;     // 7 個ずつ: a xyz, b xyz, 符号付きの二面角
        readonly int[] segmentComponent;
        readonly Dictionary<long, long> cells; // 格子の鍵 → (最初の線分 << 32) | 数
        public readonly int[] TriangleComponent;
        public readonly int BoundaryEdges, NonManifoldEdges, InconsistentEdges, SegmentCount;
        const long GridAxis = 1L << 21;

        struct WeldKey : IEquatable<WeldKey>
        {
            public long X, Y, Z;
            public bool Equals(WeldKey o) => X == o.X && Y == o.Y && Z == o.Z;
            public override bool Equals(object obj) => obj is WeldKey o && Equals(o);
            public override int GetHashCode() { unchecked { return ((X.GetHashCode() * 397) ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode(); } }
        }
        struct EdgeRecord { public int Face1, Face2, Count; public bool Inconsistent; }

        /// <param name="valid">面積のある三角形（面の法線が決まるもの）。</param>
        /// <param name="faceNormals">三角形ごとの単位法線（3 個ずつ）。</param>
        /// <param name="remainingBytes">線分を割り当てる前に確かめる予算の残り。</param>
        public MeshCurvatureField(MeshBakeInput input, bool[] valid, float[] faceNormals, double radiusAbsolute, long remainingBytes)
        {
            radius = radiusAbsolute; cell = 1.5 * radius; norm = Math.PI / 2 * (16.0 / 15.0) * radius;
            minX = input.MinX; minY = input.MinY; minZ = input.MinZ;
            var corners = input.Corners; int count = input.TriangleCount;
            // 位置を量子化して溶接する（UV の継ぎ目で分かれた頂点を 1 つに）
            double tolerance = Math.Max(1e-9, input.Diagonal * 1e-6);
            var weld = new Dictionary<WeldKey, int>(count);
            var vertexId = new int[count * 3];
            for (int i = 0; i < count * 3; i++)
            {
                var key = new WeldKey { X = (long)Math.Round(corners[i * 3] / tolerance), Y = (long)Math.Round(corners[i * 3 + 1] / tolerance), Z = (long)Math.Round(corners[i * 3 + 2] / tolerance) };
                if (!weld.TryGetValue(key, out int id)) { id = weld.Count; weld.Add(key, id); }
                vertexId[i] = id;
            }
            var edges = new Dictionary<long, EdgeRecord>(count * 2);
            var edgeOrder = new List<long>(count * 2);
            for (int t = 0; t < count; t++)
            {
                if (!valid[t]) continue;
                for (int e = 0; e < 3; e++)
                {
                    int a = vertexId[t * 3 + e], b = vertexId[t * 3 + (e + 1) % 3];
                    if (a == b) continue;
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    bool forward = a < b;
                    if (!edges.TryGetValue(key, out var record)) { record = new EdgeRecord { Face1 = t, Face2 = -1, Count = 1, Inconsistent = false }; edgeOrder.Add(key); }
                    else
                    {
                        record.Count++;
                        if (record.Count == 2)
                        {
                            record.Face2 = t;
                            // 向きのそろった 2 面なら、共有する辺を逆向きにたどる
                            bool firstForward = EdgeForward(vertexId, record.Face1, a < b ? a : b, a < b ? b : a);
                            if (firstForward == forward) record.Inconsistent = true;
                        }
                    }
                    edges[key] = record;
                }
            }
            // 成分（溶接した多様体の辺でつながる三角形）
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            var curved = new List<(long key, double phi)>();
            long segmentTotal = 0;
            int boundary = 0, nonManifold = 0, inconsistent = 0;
            foreach (long key in edgeOrder)
            {
                var record = edges[key];
                if (record.Count == 1) { boundary++; continue; }
                if (record.Count > 2) { nonManifold++; continue; }
                if (record.Inconsistent) { inconsistent++; continue; }
                Union(parent, record.Face1, record.Face2);
                int va = (int)(key >> 32), vb = (int)(key & 0xFFFFFFFF);
                double phi = SignedDihedral(corners, faceNormals, vertexId, record.Face1, record.Face2, va, vb);
                if (Math.Abs(phi) < 1e-6) continue;
                curved.Add((key, phi));
                CornerOf(corners, vertexId, record.Face1, va, out double ax, out double ay, out double az);
                CornerOf(corners, vertexId, record.Face1, vb, out double bx, out double by, out double bz);
                double length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
                segmentTotal += Math.Max(1, (long)Math.Ceiling(length / radius));
            }
            BoundaryEdges = boundary; NonManifoldEdges = nonManifold; InconsistentEdges = inconsistent;
            long need = segmentTotal * (7 * 8 + 4 + 8 + 8);
            if (segmentTotal > int.MaxValue / 8 || need > remainingBytes)
                throw new MeshBakeRefusedException("Curvature needs about " + (need >> 20) + " MiB for " + segmentTotal + " edge segments, over the remaining bake budget. Use a larger curvature radius or raise the memory budget.");
            TriangleComponent = new int[count];
            for (int i = 0; i < count; i++) TriangleComponent[i] = Find(parent, i);
            int segmentCount = (int)segmentTotal;
            var raw = new double[segmentCount * 7]; var rawComponent = new int[segmentCount]; var rawCell = new long[segmentCount];
            int s = 0;
            foreach (var (key, phi) in curved)
            {
                var record = edges[key];
                int va = (int)(key >> 32), vb = (int)(key & 0xFFFFFFFF);
                CornerOf(corners, vertexId, record.Face1, va, out double ax, out double ay, out double az);
                CornerOf(corners, vertexId, record.Face1, vb, out double bx, out double by, out double bz);
                double length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
                int pieces = Math.Max(1, (int)Math.Ceiling(length / radius));
                int component = TriangleComponent[record.Face1];
                for (int j = 0; j < pieces; j++)
                {
                    double t0 = (double)j / pieces, t1 = (double)(j + 1) / pieces;
                    int o = s * 7;
                    raw[o] = ax + (bx - ax) * t0; raw[o + 1] = ay + (by - ay) * t0; raw[o + 2] = az + (bz - az) * t0;
                    raw[o + 3] = ax + (bx - ax) * t1; raw[o + 4] = ay + (by - ay) * t1; raw[o + 5] = az + (bz - az) * t1;
                    raw[o + 6] = phi; rawComponent[s] = component;
                    rawCell[s] = CellKey(Cell((raw[o] + raw[o + 3]) * 0.5, minX), Cell((raw[o + 1] + raw[o + 4]) * 0.5, minY), Cell((raw[o + 2] + raw[o + 5]) * 0.5, minZ));
                    s++;
                }
            }
            // 格子の鍵の順（同じ鍵の中は作った順）に並べ、鍵ごとの範囲を引けるようにする
            var sorted = new int[segmentCount];
            for (int i = 0; i < segmentCount; i++) sorted[i] = i;
            Array.Sort(sorted, (x, y) => { int c = rawCell[x].CompareTo(rawCell[y]); return c != 0 ? c : x.CompareTo(y); });
            segments = new double[segmentCount * 7]; segmentComponent = new int[segmentCount]; cells = new Dictionary<long, long>();
            for (int i = 0; i < segmentCount; i++)
            {
                int from = sorted[i];
                Array.Copy(raw, from * 7, segments, i * 7, 7); segmentComponent[i] = rawComponent[from];
                long key = rawCell[from];
                if (cells.TryGetValue(key, out long packed)) cells[key] = packed + 1;
                else cells.Add(key, ((long)i << 32) | 1);
            }
            SegmentCount = segmentCount;
        }

        static bool EdgeForward(int[] vertexId, int face, int lo, int hi)
        {
            for (int e = 0; e < 3; e++) if (vertexId[face * 3 + e] == lo && vertexId[face * 3 + (e + 1) % 3] == hi) return true;
            return false;
        }
        static void CornerOf(float[] corners, int[] vertexId, int face, int id, out double x, out double y, out double z)
        {
            for (int e = 0; e < 3; e++)
                if (vertexId[face * 3 + e] == id) { int k = (face * 3 + e) * 3; x = corners[k]; y = corners[k + 1]; z = corners[k + 2]; return; }
            throw new InvalidOperationException("Edge vertex not on its face.");
        }
        /// <summary>符号付きの二面角。face2 の辺に乗らない頂点が face1 の面より下なら凸（+）。</summary>
        static double SignedDihedral(float[] corners, float[] normals, int[] vertexId, int face1, int face2, int va, int vb)
        {
            double n1x = normals[face1 * 3], n1y = normals[face1 * 3 + 1], n1z = normals[face1 * 3 + 2];
            double n2x = normals[face2 * 3], n2y = normals[face2 * 3 + 1], n2z = normals[face2 * 3 + 2];
            double cx = n1y * n2z - n1z * n2y, cy = n1z * n2x - n1x * n2z, cz = n1x * n2y - n1y * n2x;
            double phi = Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), n1x * n2x + n1y * n2y + n1z * n2z);
            int far = -1;
            for (int e = 0; e < 3; e++) { int id = vertexId[face2 * 3 + e]; if (id != va && id != vb) { far = (face2 * 3 + e) * 3; break; } }
            if (far < 0) return 0;
            CornerOf(corners, vertexId, face1, va, out double ax, out double ay, out double az);
            double side = n1x * (corners[far] - ax) + n1y * (corners[far + 1] - ay) + n1z * (corners[far + 2] - az);
            return side <= 0 ? phi : -phi;
        }
        static int Find(int[] parent, int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        static void Union(int[] parent, int a, int b)
        {
            a = Find(parent, a); b = Find(parent, b);
            if (a == b) return;
            if (a < b) parent[b] = a; else parent[a] = b; // 小さい番号を根に（決まった名前）
        }
        long Cell(double value, double origin) => (long)Math.Floor((value - origin) / cell);
        static long CellKey(long x, long y, long z) => (x * GridAxis + y) * GridAxis + z;

        /// <summary>点 (px, py, pz) の曲率。成分 component の辺だけを数える。正規化した値（90° の辺の中心で ±1、範囲で切らない）。</summary>
        public double Evaluate(double px, double py, double pz, int component)
        {
            if (cells.Count == 0) return 0;
            double reach = 1.5 * radius, r2 = radius * radius, inverseR2 = 1 / r2;
            long x0 = Math.Max(0, Cell(px - reach, minX)), x1 = Cell(px + reach, minX);
            long y0 = Math.Max(0, Cell(py - reach, minY)), y1 = Cell(py + reach, minY);
            long z0 = Math.Max(0, Cell(pz - reach, minZ)), z1 = Cell(pz + reach, minZ);
            double sum = 0;
            for (long x = x0; x <= x1; x++) for (long y = y0; y <= y1; y++) for (long z = z0; z <= z1; z++)
            {
                if (!cells.TryGetValue(CellKey(x, y, z), out long packed)) continue;
                int start = (int)(packed >> 32), n = (int)(packed & 0xFFFFFFFF);
                for (int s = start; s < start + n; s++)
                {
                    if (segmentComponent[s] != component) continue;
                    int o = s * 7;
                    double mx = segments[o] - px, my = segments[o + 1] - py, mz = segments[o + 2] - pz;
                    double ex = segments[o + 3] - segments[o], ey = segments[o + 4] - segments[o + 1], ez = segments[o + 5] - segments[o + 2];
                    double ee = ex * ex + ey * ey + ez * ez, me = mx * ex + my * ey + mz * ez, mm = mx * mx + my * my + mz * mz;
                    if (ee <= 0) continue;
                    // |m + t e|² ≤ r² となる t の範囲（線分の 0〜1 に切る）
                    double disc = me * me - ee * (mm - r2);
                    if (disc <= 0) continue;
                    double root = Math.Sqrt(disc);
                    double t0 = Math.Max(0, (-me - root) / ee), t1 = Math.Min(1, (-me + root) / ee);
                    if (t1 <= t0) continue;
                    // g(t) = 1 − |m + t e|²/r² = A + B t + C t²、∫ g² dt を閉じた式で
                    double A = 1 - mm * inverseR2, B = -2 * me * inverseR2, C = -ee * inverseR2;
                    double integral = Antiderivative(A, B, C, t1) - Antiderivative(A, B, C, t0);
                    sum += segments[o + 6] * integral * Math.Sqrt(ee);
                }
            }
            return sum / norm;
        }
        static double Antiderivative(double a, double b, double c, double t)
        {
            double t2 = t * t, t3 = t2 * t;
            return a * a * t + a * b * t2 + (b * b + 2 * a * c) * t3 / 3 + b * c * t2 * t2 / 2 + c * c * t3 * t2 / 5;
        }
    }
}
