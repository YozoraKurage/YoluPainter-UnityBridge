// 改善前の幾何構築とレイ判定。回帰試験の基準として固定する。
using System;
using System.Collections.Generic;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;
namespace Yozolab.YoluPainter.Tests
{
    internal sealed class LegacySurfaceGeometry
    {
        readonly SurfaceTriangle[] triangles;
        readonly int[][] adjacency;
        readonly int[] indices;
        readonly List<BvhNode> nodes = new List<BvhNode>();
        readonly float visibilityEpsilon;
        public int SnapshotRevision { get; }
        public int TriangleCount => triangles.Length;
        /// <summary>スナップショットの三角形（読むだけ。並びは TriangleIndex と同じ）。</summary>
        public IReadOnlyList<SurfaceTriangle> Triangles => triangles;
        public int NonManifoldEdgeCount { get; private set; }
        public Bounds Bounds { get; }

        struct BvhNode { public Bounds Bounds; public int Left, Right, Start, Count; }
        sealed class RayQueryBudget
        {
            public int RemainingTriangleTests, RemainingNodeVisits;
            public bool Exceeded;
        }
        readonly struct PositionKey : IEquatable<PositionKey>, IComparable<PositionKey>
        {
            readonly long x, y, z;
            public PositionKey(Vector3 p, double tolerance)
            { x = (long)Math.Round(p.x / tolerance); y = (long)Math.Round(p.y / tolerance); z = (long)Math.Round(p.z / tolerance); }
            public bool Equals(PositionKey other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is PositionKey other && Equals(other);
            public override int GetHashCode() { unchecked { return ((x.GetHashCode() * 397) ^ y.GetHashCode()) * 397 ^ z.GetHashCode(); } }
            public int CompareTo(PositionKey other)
            { int c = x.CompareTo(other.x); if (c != 0) return c; c = y.CompareTo(other.y); return c != 0 ? c : z.CompareTo(other.z); }
        }
        readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            readonly PositionKey a, b;
            readonly int renderer, material;
            public EdgeKey(Vector3 p, Vector3 q, int renderer, int material, float tolerance)
            {
                var k1 = new PositionKey(p, tolerance); var k2 = new PositionKey(q, tolerance);
                a = k1.CompareTo(k2) <= 0 ? k1 : k2; b = k1.CompareTo(k2) <= 0 ? k2 : k1;
                this.renderer = renderer; this.material = material;
            }
            public bool Equals(EdgeKey other) => a.Equals(other.a) && b.Equals(other.b) && renderer == other.renderer && material == other.material;
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() { unchecked { return ((a.GetHashCode() * 397 ^ b.GetHashCode()) * 397 ^ renderer) * 397 ^ material; } }
        }

        public LegacySurfaceGeometry(IList<SurfaceTriangle> source, int snapshotRevision = 1, float weldTolerance = 0.000001f)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!Finite(weldTolerance) || weldTolerance <= 0) throw new ArgumentOutOfRangeException(nameof(weldTolerance));
            SnapshotRevision = snapshotRevision;
            triangles = new SurfaceTriangle[source.Count]; source.CopyTo(triangles, 0);
            indices = new int[triangles.Length];
            var bounds = triangles.Length == 0 ? new Bounds(Vector3.zero, Vector3.zero) : triangles[0].Bounds;
            for (int i = 0; i < triangles.Length; i++)
            {
                SurfaceTriangle t = triangles[i];
                if (!Finite(t.A) || !Finite(t.B) || !Finite(t.C) || !Finite(t.UvA) || !Finite(t.UvB) || !Finite(t.UvC) || !Finite(Vector3.Cross(t.B - t.A, t.C - t.A)))
                    throw new ArgumentException("Mesh contains non-finite position or UV data.", nameof(source));
                indices[i] = i; bounds.Encapsulate(t.Bounds);
            }
            if (!Finite(bounds.size)) throw new ArgumentException("Mesh bounds exceed supported numeric range.", nameof(source));
            Bounds = bounds;
            visibilityEpsilon = Mathf.Max(0.0000001f, bounds.size.magnitude * 0.000001f);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            adjacency = BuildAdjacency(weldTolerance); AdjacencyMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart(); if (triangles.Length != 0) BuildBvh(0, triangles.Length); BvhMs = clock.Elapsed.TotalMilliseconds;
        }

        internal int[][] Neighbors => adjacency;
        internal double AdjacencyMs, BvhMs;
        int[][] BuildAdjacency(float tolerance)
        {
            var edges = new Dictionary<EdgeKey, List<int>>();
            var neighbors = new List<int>[triangles.Length];
            for (int i = 0; i < triangles.Length; i++)
            {
                neighbors[i] = new List<int>(3); var t = triangles[i];
                AddEdge(edges, new EdgeKey(t.A, t.B, t.RendererIndex, t.MaterialSlot, tolerance), i);
                AddEdge(edges, new EdgeKey(t.B, t.C, t.RendererIndex, t.MaterialSlot, tolerance), i);
                AddEdge(edges, new EdgeKey(t.C, t.A, t.RendererIndex, t.MaterialSlot, tolerance), i);
            }
            foreach (var edge in edges.Values)
            {
                if (edge.Count == 2 && edge[0] != edge[1])
                { neighbors[edge[0]].Add(edge[1]); neighbors[edge[1]].Add(edge[0]); }
                else if (edge.Count > 2) NonManifoldEdgeCount++;
            }
            var result = new int[triangles.Length][];
            for (int i = 0; i < result.Length; i++) result[i] = neighbors[i].ToArray();
            return result;
        }
        static void AddEdge(Dictionary<EdgeKey, List<int>> edges, EdgeKey key, int index)
        { if (!edges.TryGetValue(key, out var items)) { items = new List<int>(2); edges.Add(key, items); } items.Add(index); }

        int BuildBvh(int start, int count)
        {
            var bounds = triangles[indices[start]].Bounds;
            var centers = new Bounds(bounds.center, Vector3.zero);
            for (int i = start + 1; i < start + count; i++)
            { var b = triangles[indices[i]].Bounds; bounds.Encapsulate(b); centers.Encapsulate(b.center); }
            int id = nodes.Count;
            nodes.Add(new BvhNode { Bounds = bounds, Start = start, Count = count, Left = -1, Right = -1 });
            if (count <= 8) return id;
            int axis = centers.size.x >= centers.size.y && centers.size.x >= centers.size.z ? 0 : centers.size.y >= centers.size.z ? 1 : 2;
            Array.Sort(indices, start, count, Comparer<int>.Create((a, b) => triangles[a].Bounds.center[axis].CompareTo(triangles[b].Bounds.center[axis])));
            int half = count / 2;
            int left = BuildBvh(start, half), right = BuildBvh(start + half, count - half);
            nodes[id] = new BvhNode { Bounds = bounds, Start = start, Count = 0, Left = left, Right = right };
            return id;
        }

        public bool TryRaycast(Ray ray, out SurfaceHit hit, bool cullBackfaces = true, float maximumDistance = float.PositiveInfinity)
        {
            return TryRaycastInternal(ray, out hit, cullBackfaces, maximumDistance, null);
        }
        bool TryRaycastInternal(Ray ray, out SurfaceHit hit, bool cullBackfaces, float maximumDistance, RayQueryBudget work)
        {
            hit = default;
            if (triangles.Length == 0 || !Finite(ray.origin) || !Finite(ray.direction) || ray.direction.sqrMagnitude < 1e-20f) return false;
            ray.direction = ray.direction.normalized;
            float nearest = maximumDistance; int triangle = -1; Vector3 barycentric = default;
            RaycastNode(0, ray, cullBackfaces, ref nearest, ref triangle, ref barycentric, work);
            if (triangle < 0) return false;
            var t = triangles[triangle];
            hit = new SurfaceHit
            {
                SnapshotRevision = SnapshotRevision, TriangleIndex = triangle, RendererIndex = t.RendererIndex, MaterialSlot = t.MaterialSlot,
                Position = ray.GetPoint(nearest), Normal = t.Normal, Distance = nearest, Barycentric = barycentric,
                UV = t.UvA * barycentric.x + t.UvB * barycentric.y + t.UvC * barycentric.z
            };
            return true;
        }
        void RaycastNode(int id, Ray ray, bool cull, ref float nearest, ref int triangle, ref Vector3 barycentric, RayQueryBudget work)
        {
            if (work != null && (work.Exceeded || work.RemainingNodeVisits-- <= 0)) { work.Exceeded = true; return; }
            var node = nodes[id];
            if (!IntersectsBounds(node.Bounds, ray, nearest)) return;
            if (node.Count == 0)
            { RaycastNode(node.Left, ray, cull, ref nearest, ref triangle, ref barycentric, work); RaycastNode(node.Right, ray, cull, ref nearest, ref triangle, ref barycentric, work); return; }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                if (work != null && work.RemainingTriangleTests-- <= 0) { work.Exceeded = true; return; }
                int index = indices[i];
                if (IntersectTriangle(ray, triangles[index], cull, out float distance, out var weights) && distance < nearest)
                { nearest = distance; triangle = index; barycentric = weights; }
            }
        }
        static bool IntersectsBounds(Bounds bounds, Ray ray, float maxDistance)
        {
            float min = 0, max = maxDistance;
            var lower = bounds.min; var upper = bounds.max;
            for (int axis = 0; axis < 3; axis++)
            {
                float d = ray.direction[axis], o = ray.origin[axis];
                if (Mathf.Abs(d) < 1e-12f) { if (o < lower[axis] || o > upper[axis]) return false; continue; }
                float a = (lower[axis] - o) / d, b = (upper[axis] - o) / d;
                if (a > b) { float temp = a; a = b; b = temp; }
                min = Mathf.Max(min, a); max = Mathf.Min(max, b);
                if (max < min) return false;
            }
            return true;
        }
        public static bool IntersectTriangle(Ray ray, SurfaceTriangle triangle, bool cullBackfaces, out float distance, out Vector3 barycentric)
        {
            distance = 0; barycentric = default;
            var e1 = triangle.B - triangle.A; var e2 = triangle.C - triangle.A;
            var p = Vector3.Cross(ray.direction, e2); float determinant = Vector3.Dot(e1, p);
            float epsilon = Mathf.Max(1e-20f, Mathf.Sqrt(e1.sqrMagnitude * e2.sqrMagnitude) * 1e-7f);
            if (cullBackfaces ? determinant <= epsilon : Mathf.Abs(determinant) <= epsilon) return false;
            float inverse = 1 / determinant; var offset = ray.origin - triangle.A;
            float u = Vector3.Dot(offset, p) * inverse;
            if (u < -1e-6f || u > 1.000001f) return false;
            var q = Vector3.Cross(offset, e1); float v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < -1e-6f || u + v > 1.000001f) return false;
            distance = Vector3.Dot(e2, q) * inverse;
            if (distance < 0) return false;
            barycentric = new Vector3(1 - u - v, u, v); return true;
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        static bool Finite(Vector2 v) => Finite(v.x) && Finite(v.y);
    }
}
