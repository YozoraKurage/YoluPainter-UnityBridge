using System;
using System.Collections.Generic;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor.Preview
{
    public sealed partial class SurfaceGeometry
    {
        /// <summary>幾何の隣接辺を展開した局所座標。UV の鏡映には依らず、非多様体の辺や別のレンダラーへ進まない。
        /// 曲率のある閉じた面の全体を一意に展開するものではない。閉路では最初の幅優先経路を使う。</summary>
        public sealed class SamplingChart
        {
            internal struct Entry { public int Triangle; public Vector2 A, B, C; }
            readonly SurfaceGeometry owner;
            readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
            readonly List<Entry> ordered = new List<Entry>();
            int remainingQueries = 2000000;
            public int TriangleCount => entries.Count;
            public long NominalBytes => (long)entries.Count * 128;
            internal SamplingChart(SurfaceGeometry owner, int seed) { this.owner = owner; }
            internal void Add(Entry entry) { entries.Add(entry.Triangle, entry); ordered.Add(entry); }
            internal bool Contains(int triangle) => entries.ContainsKey(triangle);
            internal Entry Get(int triangle) => entries[triangle];
            public bool TryCoordinates(SurfaceHit hit, out Vector2 point)
            {
                point = default;
                if (hit.SnapshotRevision != owner.SnapshotRevision || !entries.TryGetValue(hit.TriangleIndex, out var e)) return false;
                var t = owner.triangles[hit.TriangleIndex];
                if (hit.RendererIndex != t.RendererIndex || hit.MaterialSlot != t.MaterialSlot || !Finite(hit.Position)) return false;
                var b = Barycentric(hit.Position, t); point = e.A * b.x + e.B * b.y + e.C * b.z; return Finite(point);
            }
            public bool TryCoordinates(SurfacePixel pixel, out Vector2 point)
            {
                point = default;
                if (pixel.TriangleIndex < 0 || !entries.TryGetValue(pixel.TriangleIndex, out var e)) return false;
                var b = Barycentric(pixel.Position, owner.triangles[pixel.TriangleIndex]);
                point = e.A * b.x + e.B * b.y + e.C * b.z; return Finite(point);
            }
            static bool Weights(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 w)
            {
                var u = b - a; var v = c - a; var q = p - a; float det = Cross(u, v); w = default;
                if (Mathf.Abs(det) < 1e-20f) return false;
                float y = Cross(q, v) / det, z = Cross(u, q) / det; w = new Vector3(1 - y - z, y, z); return true;
            }
            bool Locate(Vector2 point, out Entry found, out Vector3 weights)
            {
                // 重複する展開では種の面からの幅優先経路を優先する。UV の画像上の近さは使わない。
                foreach (var e in ordered)
                {
                    if (--remainingQueries < 0) throw new InvalidOperationException("Surface sampling exceeded its lookup budget. Stroke canceled.");
                    if (Weights(point, e.A, e.B, e.C, out weights) && weights.x >= -1e-6f && weights.y >= -1e-6f && weights.z >= -1e-6f)
                    { found = e; return true; }
                }
                found = default; weights = default; return false;
            }
            public bool TrySample(Vector2 point, BrushPixel pixel, int width, int height, out BrushMappedPixel mapped)
            {
                mapped = default;
                if (!Finite(point) || width <= 0 || height <= 0 || !Locate(point, out var e, out var b)) return false;
                var t = owner.triangles[e.Triangle]; var uv = t.UvA * b.x + t.UvB * b.y + t.UvC * b.z;
                double x = Snap(uv.x * (double)width - .5), y = Snap(uv.y * (double)height - .5);
                int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y); double fx = x - ix, fy = y - iy;
                BrushSourceTap Tap(int px, int py, double weight)
                {
                    if (weight <= 0) return default;
                    // UV の補間点が三角形の外なら、同じ展開上の隣接面へ運ぶ。島の余白を読み込まない。
                    var tapUv = new Vector2((px + .5f) / width, (py + .5f) / height);
                    if (!Weights(tapUv, t.UvA, t.UvB, t.UvC, out var wb)) return default;
                    var q = e.A * wb.x + e.B * wb.y + e.C * wb.z;
                    if (!Locate(q, out var next, out var nb)) return default;
                    var nt = owner.triangles[next.Triangle]; var nuv = nt.UvA * nb.x + nt.UvB * nb.y + nt.UvC * nb.z;
                    int nx = Mathf.FloorToInt(nuv.x * width), ny = Mathf.FloorToInt(nuv.y * height);
                    return nx < 0 || ny < 0 || nx >= width || ny >= height ? default : new BrushSourceTap(nx, ny, weight);
                }
                var a = Tap(ix, iy, (1 - fx) * (1 - fy)); var c = Tap(ix, iy + 1, (1 - fx) * fy);
                var d = Tap(ix + 1, iy + 1, fx * fy); var ab = Tap(ix + 1, iy, fx * (1 - fy));
                if (a.Weight + ab.Weight + c.Weight + d.Weight <= 0) return false;
                mapped = new BrushMappedPixel(pixel, a, ab, c, d); return true;
            }
            static double Snap(double value) => Math.Abs(value - Math.Round(value)) < 1e-4 ? Math.Round(value) : value;
        }
        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        /// <summary>アンカーから radius の局所展開。maxTriangles または maxBytes を超えれば部分結果を返さず拒否する。
        /// tangent は展開の横軸（0 なら世界の軸から作る）。2 つのアンカーを結ぶクローンは対応する横軸を使う。</summary>
        public SamplingChart BuildSamplingChart(SurfaceHit anchor, float radius, Vector3 tangent = default, int maxTriangles = 2048, long maxBytes = long.MaxValue)
        {
            if (anchor.SnapshotRevision != SnapshotRevision || anchor.TriangleIndex < 0 || anchor.TriangleIndex >= triangles.Length)
                throw new InvalidOperationException("The model snapshot changed. Set the source again.");
            if (!Finite(radius) || radius <= 0 || !Finite(anchor.Position) || !Finite(tangent)) throw new ArgumentOutOfRangeException(nameof(radius));
            var seed = triangles[anchor.TriangleIndex];
            if (!Visible(anchor.TriangleIndex) || anchor.RendererIndex != seed.RendererIndex || anchor.MaterialSlot != seed.MaterialSlot) throw new InvalidOperationException("Surface binding does not match the current snapshot.");
            Vector3 n = seed.Normal;
            Vector3 u = Vector3.ProjectOnPlane(tangent.sqrMagnitude > 0 ? tangent : Vector3.right, n);
            if (u.sqrMagnitude < 1e-12f) u = Vector3.ProjectOnPlane(Vector3.up, n);
            u.Normalize(); Vector3 v = Vector3.Cross(n, u);
            Vector2 Project(Vector3 p) => new Vector2(Vector3.Dot(p - anchor.Position, u), Vector3.Dot(p - anchor.Position, v));
            var chart = new SamplingChart(this, anchor.TriangleIndex);
            var queue = new Queue<int>(); queue.Enqueue(anchor.TriangleIndex);
            void Add(SamplingChart.Entry entry)
            {
                if (chart.TriangleCount >= maxTriangles || (chart.TriangleCount + 1L) * 128 > maxBytes)
                    throw new InvalidOperationException("Surface sampling exceeded its triangle or memory budget. Stroke canceled.");
                chart.Add(entry);
            }
            Add(new SamplingChart.Entry { Triangle = anchor.TriangleIndex, A = Project(seed.A), B = Project(seed.B), C = Project(seed.C) });
            while (queue.Count > 0)
            {
                int current = queue.Dequeue(); var t = triangles[current]; var e = chart.Get(current);
                foreach (int neighbor in adjacency[current])
                {
                    if (chart.Contains(neighbor) || !Visible(neighbor)) continue;
                    var nt = triangles[neighbor];
                    var oldWorld = new[] { t.A, t.B, t.C }; var oldFlat = new[] { e.A, e.B, e.C }; var newWorld = new[] { nt.A, nt.B, nt.C };
                    int a = -1, b = -1, na = -1, nb = -1;
                    for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
                        if (new PositionKey(oldWorld[i], seamTolerance).Equals(new PositionKey(newWorld[j], seamTolerance)))
                        { if (a < 0) { a = i; na = j; } else if (i != a && j != na) { b = i; nb = j; } }
                    if (b < 0) continue;
                    int other = 3 - a - b, nextOther = 3 - na - nb;
                    Vector2 edge = oldFlat[b] - oldFlat[a]; float length = edge.magnitude;
                    if (length <= 1e-12f) continue;
                    Vector2 along = edge / length, across = new Vector2(-along.y, along.x);
                    float da = (newWorld[nextOther] - newWorld[na]).sqrMagnitude, db = (newWorld[nextOther] - newWorld[nb]).sqrMagnitude;
                    float x = (da - db + length * length) / (2 * length), h = Mathf.Sqrt(Mathf.Max(0, da - x * x));
                    float side = Cross(edge, oldFlat[other] - oldFlat[a]) >= 0 ? -1 : 1;
                    var flat = new Vector2[3]; flat[na] = oldFlat[a]; flat[nb] = oldFlat[b]; flat[nextOther] = oldFlat[a] + along * x + across * h * side;
                    var min = Vector2.Min(flat[0], Vector2.Min(flat[1], flat[2])); var max = Vector2.Max(flat[0], Vector2.Max(flat[1], flat[2]));
                    float bx = Mathf.Max(min.x, Mathf.Min(0, max.x)), by = Mathf.Max(min.y, Mathf.Min(0, max.y));
                    if (bx * bx + by * by > radius * radius) continue;
                    Add(new SamplingChart.Entry { Triangle = neighbor, A = flat[0], B = flat[1], C = flat[2] }); queue.Enqueue(neighbor);
                }
            }
            return chart;
        }
    }
}
