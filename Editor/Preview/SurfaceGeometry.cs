using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>A hit on an immutable preview snapshot. Position is in preview world space.</summary>
    public struct SurfaceHit
    {
        public int SnapshotRevision;
        public int RendererIndex;
        /// <summary>Global flattened renderer/submesh slot, not the renderer-local index.</summary>
        public int MaterialSlot;
        public int TriangleIndex;
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Barycentric;
        public Vector2 UV;
        public float Distance;
    }

    public struct SurfacePixel
    {
        /// <summary>Texture pixel coordinates, with (0,0) at bottom left.</summary>
        public int X, Y;
        public float Coverage;
        public SurfacePixel(int x, int y, float coverage) { X = x; Y = y; Coverage = coverage; }
    }

    public sealed class SurfaceDabResult
    {
        public readonly List<SurfacePixel> Pixels = new List<SurfacePixel>();
        public bool WasClipped { get; internal set; }
        public string Diagnostic { get; internal set; } = string.Empty;
        public int CandidatePixels { get; internal set; }
        public int VisibilityRays { get; internal set; }
        public int RayTriangleTests { get; internal set; }
        internal SurfaceDabResult Reject(string message)
        {
            Pixels.Clear();
            WasClipped = true;
            Diagnostic = message;
            return this;
        }
    }

    public sealed class SurfaceBrushBudget
    {
        public int MaxTriangles = 2048;
        public int MaxCandidatePixels = 262144;
        public int MaxVisibilityRays = 131072;
        public int MaxRayTriangleTests = 2000000;
        public int MaxRayNodeVisits = 4000000;
    }

    /// <summary>Plain snapshot data; indices remain stable for the life of SurfaceGeometry.</summary>
    public struct SurfaceTriangle
    {
        public Vector3 A, B, C;
        public Vector2 UvA, UvB, UvC;
        public int RendererIndex, MaterialSlot;
        public SurfaceTriangle(Vector3 a, Vector3 b, Vector3 c, Vector2 uvA, Vector2 uvB, Vector2 uvC,
            int rendererIndex = 0, int materialSlot = 0)
        {
            A = a; B = b; C = c; UvA = uvA; UvB = uvB; UvC = uvC;
            RendererIndex = rendererIndex; MaterialSlot = materialSlot;
        }
        public Vector3 Normal
        {
            get
            {
                // Vector3.normalized uses a 1e-5 magnitude cutoff, too large for small triangle areas.
                var cross = Vector3.Cross(B - A, C - A); float length = cross.magnitude;
                return length > 1e-20f ? cross / length : Vector3.zero;
            }
        }
        public Bounds Bounds
        {
            get { var value = new Bounds(A, Vector3.zero); value.Encapsulate(B); value.Encapsulate(C); return value; }
        }
    }

    /// <summary>
    /// CPU prototype: median BVH picking, same-renderer/material geometric edge adjacency,
    /// and UV triangle rasterization with per-texel opaque visibility. No Physics queries.
    /// Vertices coincident within weldTolerance may join UV seams; nonmanifold edges do not join.
    /// This is a bounded spherical surface footprint, not a geodesic, GPU or Burst backend.
    /// </summary>
    public sealed class SurfaceGeometry
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

        public SurfaceGeometry(IList<SurfaceTriangle> source, int snapshotRevision = 1, float weldTolerance = 0.000001f)
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
            adjacency = BuildAdjacency(weldTolerance);
            if (triangles.Length != 0) BuildBvh(0, triangles.Length);
        }

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

        /// <summary>
        /// The texture pixels a spherical surface dab covers, with coverage. Triangles are visited breadth-first from the hit (the same
        /// order as always) and their candidate texels collected in that order; the visibility rays from the camera, which dominate the
        /// cost, then run in parallel (the BVH is read-only); finally the texels are accepted and merged in the collected order, and the
        /// budgets are checked in that order too, so the pixels, the counts and the reason for a refusal are the same as the sequential
        /// <see cref="BuildSurfaceDabsReference"/> at any degree of parallelism (Core's CoreParallelism).
        /// </summary>
        public SurfaceDabResult BuildSurfaceDabs(SurfaceHit hit, float radiusWorld, int width, int height, Vector3 cameraPosition,
            float hardness = 0.8f, SurfaceBrushBudget budget = null)
        {
            var result = new SurfaceDabResult();
            if (hit.SnapshotRevision != SnapshotRevision || hit.TriangleIndex < 0 || hit.TriangleIndex >= triangles.Length)
            { result.Diagnostic = "The model snapshot changed. Start a new stroke."; return result; }
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || !Finite(radiusWorld) || radiusWorld <= 0 || !Finite(cameraPosition) || !Finite(hit.Position))
            { result.Diagnostic = "Invalid surface brush size, resolution or camera."; return result; }
            var seed = triangles[hit.TriangleIndex];
            if (seed.RendererIndex != hit.RendererIndex || seed.MaterialSlot != hit.MaterialSlot)
            { result.Diagnostic = "Surface binding does not match the current snapshot."; return result; }
            budget = budget ?? new SurfaceBrushBudget(); hardness = Mathf.Clamp01(hardness);

            // 1. 三角形を幅優先で辿り、半径の中の候補のテクセルを並びのまま集める（三角形の数と候補の画素の予算はここで、元と同じ所で断る）
            var queue = new Queue<int>(); var visited = new HashSet<int>(); var candidates = new List<DabCandidate>();
            queue.Enqueue(hit.TriangleIndex); visited.Add(hit.TriangleIndex);
            float radiusSquared = radiusWorld * radiusWorld; int processed = 0; string stop = null;
            while (queue.Count > 0 && stop == null)
            {
                int triangleIndex = queue.Dequeue(); var t = triangles[triangleIndex];
                if (++processed > budget.MaxTriangles) { stop = "Surface dab exceeded the triangle budget. No pixels were changed; reduce the brush radius."; break; }
                if (t.RendererIndex != hit.RendererIndex || t.MaterialSlot != hit.MaterialSlot || t.Bounds.SqrDistance(hit.Position) > radiusSquared) continue;
                if ((ClosestPoint(hit.Position, t) - hit.Position).sqrMagnitude > radiusSquared) continue;
                if (Vector3.Dot(t.Normal, cameraPosition - (t.A + t.B + t.C) / 3) <= 0) continue;
                foreach (int neighbor in adjacency[triangleIndex]) if (visited.Add(neighbor)) queue.Enqueue(neighbor);
                if (!UvFootprintBounds(t, hit.Position, radiusWorld, out var uvMin, out var uvMax)) continue;
                int minX = Mathf.Max(0, Mathf.CeilToInt(uvMin.x * width - 0.5f));
                int maxX = Mathf.Min(width - 1, Mathf.FloorToInt(uvMax.x * width - 0.5f));
                int minY = Mathf.Max(0, Mathf.CeilToInt(uvMin.y * height - 0.5f));
                int maxY = Mathf.Min(height - 1, Mathf.FloorToInt(uvMax.y * height - 0.5f));
                if (maxX < minX || maxY < minY) continue;
                long candidateCount = (long)(maxX - minX + 1) * (maxY - minY + 1);
                if ((long)result.CandidatePixels + candidateCount > budget.MaxCandidatePixels)
                { stop = "Surface dab exceeded the pixel budget. No pixels were changed; reduce brush radius or use a smaller document."; break; }
                result.CandidatePixels += (int)candidateCount;
                for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++)
                {
                    var uv = new Vector2((x + 0.5f) / width, (y + 0.5f) / height);
                    if (!TryUvBarycentric(uv, t, out var bary)) continue;
                    var position = t.A * bary.x + t.B * bary.y + t.C * bary.z;
                    float normalizedDistance = (position - hit.Position).magnitude / radiusWorld;
                    if (normalizedDistance >= 1) continue;
                    candidates.Add(new DabCandidate { Triangle = triangleIndex, X = x, Y = y, Bary = bary, Position = position, Distance = normalizedDistance, CandidatesSoFar = result.CandidatePixels });
                }
            }

            // 2・3. 可視のレイを組ごとに並列に撃ち、組ごとに並びのとおりに予算を数えて受け入れる。予算を超える所が見つかればすぐ断る
            // （元の逐次の処理と同じく、超えた後のレイは撃たない。無駄に撃つのは多くても 1 組）。レイの数の予算を超える候補は撃たない。
            int rays = Math.Min(candidates.Count, budget.MaxVisibilityRays);
            const int Chunk = 4096;
            var outcomes = new RayOutcome[Math.Min(rays, Chunk)];
            var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Yozolab.YoluPainter.Core.CoreParallelism.Degree };
            int chunkStart = 0, chunkEnd = 0;
            void Shoot(int k)
            {
                var c = candidates[chunkStart + k]; var direction = c.Position - cameraPosition; float distance = direction.magnitude;
                if (distance <= visibilityEpsilon) { outcomes[k] = new RayOutcome { Skipped = true }; return; }
                var work = new RayQueryBudget { RemainingTriangleTests = budget.MaxRayTriangleTests, RemainingNodeVisits = budget.MaxRayNodeVisits };
                bool hasHit = TryRaycastInternal(new Ray(cameraPosition, direction / distance), out var visible, false, distance + visibilityEpsilon * 2, work);
                outcomes[k] = new RayOutcome { HasHit = hasHit, Hit = visible, CameraDistance = distance,
                    Tests = budget.MaxRayTriangleTests - work.RemainingTriangleTests, Visits = budget.MaxRayNodeVisits - work.RemainingNodeVisits, Exceeded = work.Exceeded };
            }
            long tests = 0, visits = 0; var pixels = new Dictionary<int, float>(); int collected = result.CandidatePixels;
            for (int i = 0; i < candidates.Count; i++)
            {
                // 元の逐次の処理がこの候補を見ていた時点の、候補の画素の数（断るときに同じ数を返す）
                result.CandidatePixels = candidates[i].CandidatesSoFar;
                if (++result.VisibilityRays > budget.MaxVisibilityRays)
                    return result.Reject("Surface dab exceeded the visibility budget. No pixels were changed; reduce the brush radius.");
                if (i >= chunkEnd)
                {
                    chunkStart = i; int count = Math.Min(Chunk, rays - i); chunkEnd = i + count;
                    if (count < 64 || options.MaxDegreeOfParallelism == 1) for (int k = 0; k < count; k++) Shoot(k);
                    else System.Threading.Tasks.Parallel.For(0, count, options, Shoot);
                }
                var o = outcomes[i - chunkStart];
                if (o.Skipped) continue;
                tests += o.Tests; visits += o.Visits;
                bool exceeded = o.Exceeded || tests > budget.MaxRayTriangleTests || visits > budget.MaxRayNodeVisits;
                result.RayTriangleTests = (int)Math.Min(tests, (long)budget.MaxRayTriangleTests + 1);
                if (exceeded) return result.Reject("Surface visibility exceeded the BVH work budget. No pixels were changed; reduce the radius or simplify overlapping geometry.");
                var c = candidates[i];
                if (!o.HasHit || o.Hit.Distance < o.CameraDistance - visibilityEpsilon) continue;
                if (o.Hit.TriangleIndex != c.Triangle)
                {
                    // Distance tolerance alone can leak through extremely thin, nearby clothing.
                    // Only an actual adjacent triangle at this triangle's edge may share a visible hit.
                    if (Mathf.Min(c.Bary.x, Mathf.Min(c.Bary.y, c.Bary.z)) > 1e-6f ||
                        (o.Hit.Position - c.Position).sqrMagnitude > visibilityEpsilon * visibilityEpsilon ||
                        Array.IndexOf(adjacency[c.Triangle], o.Hit.TriangleIndex) < 0) continue;
                }
                float coverage = c.Distance <= hardness || hardness >= 0.9999f ? 1 : 1 - Mathf.SmoothStep(0, 1, (c.Distance - hardness) / (1 - hardness));
                int key = c.Y * width + c.X;
                if (!pixels.TryGetValue(key, out float current) || coverage > current) pixels[key] = coverage;
            }
            result.CandidatePixels = collected;
            if (stop != null) return result.Reject(stop);
            // Deterministic bottom-left row order and max-union prevent shared-edge double paint.
            var keys = new List<int>(pixels.Keys); keys.Sort();
            foreach (int key in keys) result.Pixels.Add(new SurfacePixel(key % width, key / width, pixels[key]));
            return result;
        }

        struct DabCandidate { public int Triangle, X, Y, CandidatesSoFar; public Vector3 Bary, Position; public float Distance; }
        struct RayOutcome { public bool Skipped, HasHit, Exceeded; public SurfaceHit Hit; public float CameraDistance; public long Tests, Visits; }

        /// <summary>The sequential original of <see cref="BuildSurfaceDabs"/>, kept to test that the parallel version gives the same pixels,
        /// counts and refusals.</summary>
        internal SurfaceDabResult BuildSurfaceDabsReference(SurfaceHit hit, float radiusWorld, int width, int height, Vector3 cameraPosition,
            float hardness = 0.8f, SurfaceBrushBudget budget = null)
        {
            var result = new SurfaceDabResult();
            if (hit.SnapshotRevision != SnapshotRevision || hit.TriangleIndex < 0 || hit.TriangleIndex >= triangles.Length)
            { result.Diagnostic = "The model snapshot changed. Start a new stroke."; return result; }
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || !Finite(radiusWorld) || radiusWorld <= 0 || !Finite(cameraPosition) || !Finite(hit.Position))
            { result.Diagnostic = "Invalid surface brush size, resolution or camera."; return result; }
            var seed = triangles[hit.TriangleIndex];
            if (seed.RendererIndex != hit.RendererIndex || seed.MaterialSlot != hit.MaterialSlot)
            { result.Diagnostic = "Surface binding does not match the current snapshot."; return result; }
            budget = budget ?? new SurfaceBrushBudget(); hardness = Mathf.Clamp01(hardness);
            var rayWork = new RayQueryBudget { RemainingTriangleTests = budget.MaxRayTriangleTests, RemainingNodeVisits = budget.MaxRayNodeVisits };
            var queue = new Queue<int>(); var visited = new HashSet<int>(); var pixels = new Dictionary<int, float>();
            queue.Enqueue(hit.TriangleIndex); visited.Add(hit.TriangleIndex);
            float radiusSquared = radiusWorld * radiusWorld; int processed = 0;
            while (queue.Count > 0)
            {
                int triangleIndex = queue.Dequeue(); var t = triangles[triangleIndex];
                if (++processed > budget.MaxTriangles) return result.Reject("Surface dab exceeded the triangle budget. No pixels were changed; reduce the brush radius.");
                if (t.RendererIndex != hit.RendererIndex || t.MaterialSlot != hit.MaterialSlot || t.Bounds.SqrDistance(hit.Position) > radiusSquared) continue;
                if ((ClosestPoint(hit.Position, t) - hit.Position).sqrMagnitude > radiusSquared) continue;
                if (Vector3.Dot(t.Normal, cameraPosition - (t.A + t.B + t.C) / 3) <= 0) continue;
                foreach (int neighbor in adjacency[triangleIndex]) if (visited.Add(neighbor)) queue.Enqueue(neighbor);
                if (!UvFootprintBounds(t, hit.Position, radiusWorld, out var uvMin, out var uvMax)) continue;
                int minX = Mathf.Max(0, Mathf.CeilToInt(uvMin.x * width - 0.5f));
                int maxX = Mathf.Min(width - 1, Mathf.FloorToInt(uvMax.x * width - 0.5f));
                int minY = Mathf.Max(0, Mathf.CeilToInt(uvMin.y * height - 0.5f));
                int maxY = Mathf.Min(height - 1, Mathf.FloorToInt(uvMax.y * height - 0.5f));
                if (maxX < minX || maxY < minY) continue;
                long candidateCount = (long)(maxX - minX + 1) * (maxY - minY + 1);
                if ((long)result.CandidatePixels + candidateCount > budget.MaxCandidatePixels)
                    return result.Reject("Surface dab exceeded the pixel budget. No pixels were changed; reduce brush radius or use a smaller document.");
                result.CandidatePixels += (int)candidateCount;
                for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++)
                {
                    var uv = new Vector2((x + 0.5f) / width, (y + 0.5f) / height);
                    if (!TryUvBarycentric(uv, t, out var bary)) continue;
                    var position = t.A * bary.x + t.B * bary.y + t.C * bary.z;
                    float normalizedDistance = (position - hit.Position).magnitude / radiusWorld;
                    if (normalizedDistance >= 1) continue;
                    if (++result.VisibilityRays > budget.MaxVisibilityRays)
                        return result.Reject("Surface dab exceeded the visibility budget. No pixels were changed; reduce the brush radius.");
                    var direction = position - cameraPosition; float distance = direction.magnitude;
                    // Two-sided opaque occlusion also blocks back-facing covering surfaces.
                    if (distance <= visibilityEpsilon) continue;
                    bool hasVisibleHit = TryRaycastInternal(new Ray(cameraPosition, direction / distance), out var visible, false,
                        distance + visibilityEpsilon * 2, rayWork);
                    result.RayTriangleTests = budget.MaxRayTriangleTests - rayWork.RemainingTriangleTests;
                    if (rayWork.Exceeded) return result.Reject("Surface visibility exceeded the BVH work budget. No pixels were changed; reduce the radius or simplify overlapping geometry.");
                    if (!hasVisibleHit || visible.Distance < distance - visibilityEpsilon) continue;
                    if (visible.TriangleIndex != triangleIndex)
                    {
                        // Distance tolerance alone can leak through extremely thin, nearby clothing.
                        // Only an actual adjacent triangle at this triangle's edge may share a visible hit.
                        if (Mathf.Min(bary.x, Mathf.Min(bary.y, bary.z)) > 1e-6f ||
                            (visible.Position - position).sqrMagnitude > visibilityEpsilon * visibilityEpsilon ||
                            Array.IndexOf(adjacency[triangleIndex], visible.TriangleIndex) < 0) continue;
                    }
                    float coverage = normalizedDistance <= hardness || hardness >= 0.9999f ? 1 : 1 - Mathf.SmoothStep(0, 1, (normalizedDistance - hardness) / (1 - hardness));
                    int key = y * width + x;
                    if (!pixels.TryGetValue(key, out float current) || coverage > current) pixels[key] = coverage;
                }
            }
            // Deterministic bottom-left row order and max-union prevent shared-edge double paint.
            var keys = new List<int>(pixels.Keys); keys.Sort();
            foreach (int key in keys) result.Pixels.Add(new SurfacePixel(key % width, key / width, pixels[key]));
            return result;
        }

        static bool UvFootprintBounds(SurfaceTriangle t, Vector3 center, float radius, out Vector2 min, out Vector2 max)
        {
            min = max = default;
            var e1 = t.B - t.A; var e2 = t.C - t.A;
            float a = Vector3.Dot(e1, e1), b = Vector3.Dot(e1, e2), c = Vector3.Dot(e2, e2), det = a * c - b * b;
            if (det <= Mathf.Max(1e-30f, a * c * 1e-10f)) return false;
            var duv1 = t.UvB - t.UvA; var duv2 = t.UvC - t.UvA;
            var gradientU = ((duv1.x * c - duv2.x * b) * e1 + (duv2.x * a - duv1.x * b) * e2) / det;
            var gradientV = ((duv1.y * c - duv2.y * b) * e1 + (duv2.y * a - duv1.y * b) * e2) / det;
            var delta = center - t.A;
            var projectedUv = t.UvA + new Vector2(Vector3.Dot(gradientU, delta), Vector3.Dot(gradientV, delta));
            var uvRadius = radius * new Vector2(gradientU.magnitude, gradientV.magnitude);
            min = Vector2.Max(Vector2.Min(t.UvA, Vector2.Min(t.UvB, t.UvC)), projectedUv - uvRadius);
            max = Vector2.Min(Vector2.Max(t.UvA, Vector2.Max(t.UvB, t.UvC)), projectedUv + uvRadius);
            // Outside-0..1 UVs are deliberately clipped, not wrapped or treated as UDIM.
            min = Vector2.Max(Vector2.zero, min); max = Vector2.Min(Vector2.one, max);
            return true;
        }
        public static bool TryUvBarycentric(Vector2 point, SurfaceTriangle t, out Vector3 barycentric)
        {
            var a = t.UvB - t.UvA; var b = t.UvC - t.UvA; var p = point - t.UvA;
            float determinant = a.x * b.y - a.y * b.x;
            barycentric = default;
            if (Mathf.Abs(determinant) <= 1e-12f) return false;
            float u = (p.x * b.y - p.y * b.x) / determinant, v = (a.x * p.y - a.y * p.x) / determinant;
            barycentric = new Vector3(1 - u - v, u, v);
            return barycentric.x >= -1e-6f && u >= -1e-6f && v >= -1e-6f;
        }
        static Vector3 ClosestPoint(Vector3 p, SurfaceTriangle t)
        {
            // Ericson's Voronoi-region triangle closest-point algorithm.
            Vector3 ab = t.B - t.A, ac = t.C - t.A, ap = p - t.A;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return t.A;
            Vector3 bp = p - t.B; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return t.B;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return t.A + ab * (d1 / (d1 - d3));
            Vector3 cp = p - t.C; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return t.C;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return t.A + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return t.B + (t.C - t.B) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float sum = va + vb + vc;
            if (Mathf.Abs(sum) < 1e-30f) return t.A;
            return t.A + ab * (vb / sum) + ac * (vc / sum);
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
