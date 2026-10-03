using System;
using System.Collections.Generic;
using UnityEngine;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>
    /// スナップショットの三角形から範囲をすぐ引く索引（ポリゴン塗りつぶしの、ポインタの下の範囲とクリック・ドラッグ）。ジオメトリごとに作り、
    /// UV アイランド・メッシュの塊の成分は初めて要るときに 1 回だけ求める。範囲は <see cref="SurfaceRegions.Region"/> と同じ三角形の集まり
    /// （同じスロットの中で、UV（1e-6 で丸めた端点）か 3D の位置（1e-5 で丸めた端点）の辺を共有してつながる三角形。三角形が 3 つ以上
    /// 集まる辺もつなぐ）。分け方は Core の <see cref="MeshRegions"/> で、ID マップの「メッシュの塊」「UV アイランド」の部品と同じ。
    /// Region は呼ぶたびに辺の表を作るので、三角形が変わるたびに引く強調には使えない。
    /// 2D キャンバスのクリックのために、マテリアルの組ごとの UV の格子（点を含む三角形を引く）と、範囲の UV の輪郭（範囲の中で 1 回だけ現れる
    /// UV の辺）も持つ。どれも読むだけで、ジオメトリを変えない。
    /// </summary>
    public sealed class SurfaceRegionIndex
    {
        public SurfaceGeometry Geometry { get; }
        readonly IReadOnlyList<SurfaceTriangle> triangles;
        /// <summary>成分の表（種類ごと）: 三角形 → 成分、成分 → 三角形（昇順、CSR）。</summary>
        sealed class Components { public int[] Of, Start, Members; }
        Components uvIslands, meshParts, slots;
        /// <summary>UV の連結で作った頂点の番号（三角形ごとに 3 つ）と、番号ごとの UV（最初に現れたもの）。輪郭を求めるのに使う。</summary>
        int[] uvCorners; Vector2[] uvVertexUv;
        readonly Dictionary<int, UvGrid> grids = new Dictionary<int, UvGrid>();
        readonly Dictionary<long, Vector2[]> outlines = new Dictionary<long, Vector2[]>();

        public SurfaceRegionIndex(SurfaceGeometry geometry)
        {
            Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
            triangles = geometry.Triangles;
        }

        /// <summary>三角形 triangle を含む kind の範囲の鍵。同じ鍵なら同じ三角形の集まり（種類は鍵に入る）。</summary>
        public long Key(int triangle, SurfaceRegionKind kind)
        {
            Require(triangle, kind);
            int id = kind == SurfaceRegionKind.Triangle ? triangle : Table(kind).Of[triangle];
            return ((long)kind << 40) | (uint)id;
        }

        /// <summary>三角形 triangle を含む kind の範囲の三角形（昇順。<see cref="SurfaceRegions.Region"/> と同じ）。</summary>
        public IReadOnlyList<int> Region(int triangle, SurfaceRegionKind kind)
        {
            Require(triangle, kind);
            if (kind == SurfaceRegionKind.Triangle) return new[] { triangle };
            var table = Table(kind); int c = table.Of[triangle];
            return new ArraySegment<int>(table.Members, table.Start[c], table.Start[c + 1] - table.Start[c]);
        }

        void Require(int triangle, SurfaceRegionKind kind)
        {
            if (triangle < 0 || triangle >= triangles.Count) throw new ArgumentOutOfRangeException(nameof(triangle));
            if (!Enum.IsDefined(typeof(SurfaceRegionKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Components Table(SurfaceRegionKind kind)
        {
            switch (kind)
            {
                case SurfaceRegionKind.UvIsland: return uvIslands ?? (uvIslands = Connect(true));
                case SurfaceRegionKind.MeshPart: return meshParts ?? (meshParts = Connect(false));
                default: return slots ?? (slots = BySlot());
            }
        }

        /// <summary>マテリアルの組ごと（マテリアルの範囲。同じ Material を使うスロットは 1 つ）。</summary>
        Components BySlot()
        {
            var ids = new Dictionary<int, int>(); var of = new int[triangles.Count];
            for (int i = 0; i < triangles.Count; i++)
            {
                int slot = triangles[i].Material;
                if (!ids.TryGetValue(slot, out int id)) ids.Add(slot, id = ids.Count);
                of[i] = id;
            }
            return Grouped(of, ids.Count);
        }

        /// <summary>辺を共有する三角形をつなぐ（UV か 3D の位置）。分け方は Core の <see cref="MeshRegions"/>（ID マップのベイクと同じ決まり）:
        /// 同じスロットの中で、端点を丸めた辺を共有する三角形を union-find でまとめ、成分の番号はいちばん小さい三角形の順。</summary>
        Components Connect(bool uv)
        {
            int n = triangles.Count; var slots = new int[n];
            float[] values = new float[n * (uv ? 6 : 9)];
            for (int i = 0; i < n; i++)
            {
                var t = triangles[i]; slots[i] = t.MaterialSlot;
                if (uv) { int k = i * 6; values[k] = t.UvA.x; values[k + 1] = t.UvA.y; values[k + 2] = t.UvB.x; values[k + 3] = t.UvB.y; values[k + 4] = t.UvC.x; values[k + 5] = t.UvC.y; }
                else
                {
                    int k = i * 9;
                    values[k] = t.A.x; values[k + 1] = t.A.y; values[k + 2] = t.A.z; values[k + 3] = t.B.x; values[k + 4] = t.B.y; values[k + 5] = t.B.z;
                    values[k + 6] = t.C.x; values[k + 7] = t.C.y; values[k + 8] = t.C.z;
                }
            }
            int count; int[] of;
            if (uv)
            {
                of = MeshRegions.UvIslands(values, slots, out count, out var corners, out int vertexCount);
                // UV の頂点の番号ごとに、最初に現れた角の UV（輪郭に使う）
                var firstUv = new Vector2[vertexCount]; var seen = new bool[vertexCount];
                for (int c = 0; c < corners.Length; c++)
                {
                    int v = corners[c]; if (seen[v]) continue;
                    seen[v] = true; var t = triangles[c / 3]; firstUv[v] = c % 3 == 0 ? t.UvA : c % 3 == 1 ? t.UvB : t.UvC;
                }
                uvCorners = corners; uvVertexUv = firstUv;
            }
            else of = MeshRegions.MeshParts(values, slots, out count);
            return Grouped(of, count);
        }

        static long Edge(int a, int b) => a <= b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        static Components Grouped(int[] of, int count)
        {
            var start = new int[count + 1];
            foreach (int c in of) start[c + 1]++;
            for (int c = 0; c < count; c++) start[c + 1] += start[c];
            var members = new int[of.Length]; var fill = (int[])start.Clone();
            for (int i = 0; i < of.Length; i++) members[fill[of[i]]++] = i; // 三角形の順に入れるので、成分の中は昇順
            return new Components { Of = of, Start = start, Members = members };
        }

        // ───────── 2D キャンバス: UV の点の三角形と、範囲の輪郭 ─────────

        /// <summary>マテリアルの組 material（<see cref="SurfaceTriangle.Material"/>。テクスチャセット 1 つ）の三角形のうち、UV で点 uv を含むもの
        /// （辺と頂点の上も含む）。重なった UV では番号のいちばん小さいもの。無ければ −1。</summary>
        public int TriangleAtUv(int material, Vector2 uv)
        {
            if (!grids.TryGetValue(material, out var grid)) grids.Add(material, grid = new UvGrid(triangles, material));
            return grid.Find(triangles, uv);
        }

        /// <summary>UV の点に重なる全三角形（昇順）。呼び手のリストを再利用する。</summary>
        public void TrianglesAtUv(int material, Vector2 uv, List<int> result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result)); result.Clear();
            if (!grids.TryGetValue(material, out var grid)) grids.Add(material, grid = new UvGrid(triangles, material));
            grid.Find(triangles, uv, result);
        }

        /// <summary>範囲の UV の輪郭（範囲の中で 1 回だけ現れる UV の辺。2 点ずつ並べた UV）。辺は UV アイランドと同じ丸めで比べ、つぶれた辺は
        /// 入れない。辺を 64 ビットの鍵にして並べ、1 回だけのものを拾う（辞書より速く、大きな範囲でも少ない記憶）。鍵ごとに覚える。</summary>
        public Vector2[] UvOutline(int triangle, SurfaceRegionKind kind)
        {
            long key = Key(triangle, kind);
            if (outlines.TryGetValue(key, out var cached)) return cached;
            Table(SurfaceRegionKind.UvIsland); // UV の頂点の番号
            var region = Region(triangle, kind);
            var edges = new long[3 * region.Count]; int m = 0;
            foreach (int i in region)
            {
                int a = uvCorners[3 * i], b = uvCorners[3 * i + 1], c = uvCorners[3 * i + 2];
                if (a != b) edges[m++] = Edge(a, b); if (b != c) edges[m++] = Edge(b, c); if (c != a) edges[m++] = Edge(c, a);
            }
            Array.Sort(edges, 0, m);
            var lines = new List<Vector2>();
            for (int k = 0; k < m;)
            {
                int run = 1; while (k + run < m && edges[k + run] == edges[k]) run++;
                if (run == 1) { lines.Add(uvVertexUv[(int)(edges[k] >> 32)]); lines.Add(uvVertexUv[(int)(uint)edges[k]]); }
                k += run;
            }
            var result = lines.ToArray();
            if (outlines.Count > 64) outlines.Clear(); // 覚えるのは最近の範囲だけ
            outlines[key] = result; return result;
        }

        /// <summary>マテリアルの組の三角形の UV を、外接矩形を割った格子のセルに入れたもの（セル → 三角形、昇順、CSR）。</summary>
        sealed class UvGrid
        {
            readonly int size; readonly float x0, y0, cellW, cellH; readonly int[] start, items;
            public UvGrid(IReadOnlyList<SurfaceTriangle> triangles, int material)
            {
                var own = new List<int>(); float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int i = 0; i < triangles.Count; i++)
                {
                    var t = triangles[i]; if (t.Material != material) continue;
                    own.Add(i);
                    minX = Mathf.Min(minX, Mathf.Min(t.UvA.x, Mathf.Min(t.UvB.x, t.UvC.x))); maxX = Mathf.Max(maxX, Mathf.Max(t.UvA.x, Mathf.Max(t.UvB.x, t.UvC.x)));
                    minY = Mathf.Min(minY, Mathf.Min(t.UvA.y, Mathf.Min(t.UvB.y, t.UvC.y))); maxY = Mathf.Max(maxY, Mathf.Max(t.UvA.y, Mathf.Max(t.UvB.y, t.UvC.y)));
                }
                size = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(own.Count / 2f)), 1, 1024);
                if (own.Count == 0) { start = new int[2]; items = new int[0]; x0 = y0 = 0; cellW = cellH = 1; return; }
                x0 = minX; y0 = minY; cellW = Mathf.Max(1e-12f, (maxX - minX) / size); cellH = Mathf.Max(1e-12f, (maxY - minY) / size);
                var counts = new int[size * size + 1];
                foreach (int i in own)
                {
                    CellRange(triangles[i], out int cx0, out int cx1, out int cy0, out int cy1);
                    for (int cy = cy0; cy <= cy1; cy++) for (int cx = cx0; cx <= cx1; cx++) counts[cy * size + cx + 1]++;
                }
                for (int c = 0; c < size * size; c++) counts[c + 1] += counts[c];
                start = counts; items = new int[counts[size * size]]; var fill = (int[])counts.Clone();
                foreach (int i in own) // 三角形の順に入れるので、セルの中は昇順
                {
                    CellRange(triangles[i], out int cx0, out int cx1, out int cy0, out int cy1);
                    for (int cy = cy0; cy <= cy1; cy++) for (int cx = cx0; cx <= cx1; cx++) items[fill[cy * size + cx]++] = i;
                }
            }
            void CellRange(SurfaceTriangle t, out int cx0, out int cx1, out int cy0, out int cy1)
            {
                cx0 = Cell(Mathf.Min(t.UvA.x, Mathf.Min(t.UvB.x, t.UvC.x)), x0, cellW); cx1 = Cell(Mathf.Max(t.UvA.x, Mathf.Max(t.UvB.x, t.UvC.x)), x0, cellW);
                cy0 = Cell(Mathf.Min(t.UvA.y, Mathf.Min(t.UvB.y, t.UvC.y)), y0, cellH); cy1 = Cell(Mathf.Max(t.UvA.y, Mathf.Max(t.UvB.y, t.UvC.y)), y0, cellH);
            }
            int Cell(float v, float origin, float width) => Mathf.Clamp((int)((v - origin) / width), 0, size - 1);

            public int Find(IReadOnlyList<SurfaceTriangle> triangles, Vector2 p, List<int> result = null)
            {
                if (items.Length == 0 || float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.x) || float.IsInfinity(p.y)) return -1;
                float fx = (p.x - x0) / cellW, fy = (p.y - y0) / cellH;
                if (fx < -1e-4f || fy < -1e-4f || fx > size + 1e-4f || fy > size + 1e-4f) return -1;
                int c = Mathf.Clamp((int)fx, 0, size - 1) + Mathf.Clamp((int)fy, 0, size - 1) * size;
                for (int k = start[c]; k < start[c + 1]; k++) { int i = items[k]; if (!Contains(triangles[i], p)) continue; if (result == null) return i; result.Add(i); }
                return -1;
            }

            /// <summary>UV の三角形が点を含むか（辺の上も。向きはどちらでも）。</summary>
            static bool Contains(SurfaceTriangle t, Vector2 p)
            {
                double ax = t.UvA.x, ay = t.UvA.y, bx = t.UvB.x, by = t.UvB.y, cx = t.UvC.x, cy = t.UvC.y;
                double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                if (Math.Abs(area) < 1e-18) return false;
                double s = Math.Sign(area), eps = 1e-9 * Math.Abs(area);
                double e0 = ((bx - ax) * (p.y - ay) - (by - ay) * (p.x - ax)) * s, e1 = ((cx - bx) * (p.y - by) - (cy - by) * (p.x - bx)) * s, e2 = ((ax - cx) * (p.y - cy) - (ay - cy) * (p.x - cx)) * s;
                return e0 >= -eps && e1 >= -eps && e2 >= -eps;
            }
        }
    }
}
