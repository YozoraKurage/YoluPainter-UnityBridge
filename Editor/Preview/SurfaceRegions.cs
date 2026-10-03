using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>3D ビューでクリックした三角形から選ぶ範囲。</summary>
    public enum SurfaceRegionKind
    {
        /// <summary>その三角形だけ。</summary>
        Triangle,
        /// <summary>UV で辺を共有してつながる三角形（UV アイランド）。</summary>
        UvIsland,
        /// <summary>3D の位置で辺を共有してつながる三角形（UV の継ぎ目をまたぐメッシュの塊）。</summary>
        MeshPart,
        /// <summary>同じマテリアル（<see cref="SurfaceTriangle.Material"/> の組。テクスチャセット 1 つ）の三角形すべて。</summary>
        Material,
    }

    /// <summary>スナップショットの三角形から範囲をたどり、テクスチャの選択範囲にする（Substance の Polygon Fill に当たる）。
    /// UV アイランド・メッシュの塊は同じスロット（レンダラー × サブメッシュ）の中だけをたどり、マテリアルは同じマテリアルの組の全部。</summary>
    public static class SurfaceRegions
    {
        /// <summary>三角形 start を含む範囲の三角形の番号（昇順）。</summary>
        public static List<int> Region(SurfaceGeometry geometry, int start, SurfaceRegionKind kind)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            var triangles = geometry.Triangles;
            if (start < 0 || start >= triangles.Count) throw new ArgumentOutOfRangeException(nameof(start));
            if (!Enum.IsDefined(typeof(SurfaceRegionKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            int slot = triangles[start].MaterialSlot, material = triangles[start].Material;
            switch (kind)
            {
                case SurfaceRegionKind.Triangle: return new List<int> { start };
                case SurfaceRegionKind.Material: return Enumerable.Range(0, triangles.Count).Where(i => triangles[i].Material == material).ToList();
            }
            // 辺 → 三角形。UV アイランドは UV の端点、メッシュの塊は 3D の端点（量子化して溶接）で辺を比べる
            var edges = new Dictionary<(long, long, long, long, long, long), List<int>>();
            (long, long, long) Key(Vector2 uv, Vector3 p) => kind == SurfaceRegionKind.UvIsland
                ? ((long)Math.Round(uv.x * 1e6), (long)Math.Round(uv.y * 1e6), 0L)
                : ((long)Math.Round(p.x * 1e5), (long)Math.Round(p.y * 1e5), (long)Math.Round(p.z * 1e5));
            IEnumerable<(long, long, long, long, long, long)> Edges(SurfaceTriangle t)
            {
                var a = Key(t.UvA, t.A); var b = Key(t.UvB, t.B); var c = Key(t.UvC, t.C);
                yield return Edge(a, b); yield return Edge(b, c); yield return Edge(c, a);
            }
            for (int i = 0; i < triangles.Count; i++)
            {
                if (triangles[i].MaterialSlot != slot) continue;
                foreach (var e in Edges(triangles[i])) { if (!edges.TryGetValue(e, out var list)) edges.Add(e, list = new List<int>()); list.Add(i); }
            }
            var seen = new HashSet<int> { start }; var queue = new Queue<int>(); queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                foreach (var e in Edges(triangles[t])) foreach (int n in edges[e]) if (seen.Add(n)) queue.Enqueue(n);
            }
            var result = seen.ToList(); result.Sort(); return result;
        }

        static (long, long, long, long, long, long) Edge((long, long, long) a, (long, long, long) b)
            => a.CompareTo(b) <= 0 ? (a.Item1, a.Item2, a.Item3, b.Item1, b.Item2, b.Item3) : (b.Item1, b.Item2, b.Item3, a.Item1, a.Item2, a.Item3);

        /// <summary>三角形の UV をテクスチャの選択範囲にする（UV (0, 0) がキャンバスの左下）。</summary>
        public static SelectionMask Selection(PaintDocument document, SurfaceGeometry geometry, IEnumerable<int> triangles)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            double w = document.Width, h = document.Height;
            var list = new List<(double, double, double, double, double, double)>();
            foreach (int i in triangles)
            {
                var t = geometry.Triangles[i];
                list.Add((t.UvA.x * w, t.UvA.y * h, t.UvB.x * w, t.UvB.y * h, t.UvC.x * w, t.UvC.y * h));
            }
            return SelectionMask.FromTriangles(document, list);
        }
    }
}
