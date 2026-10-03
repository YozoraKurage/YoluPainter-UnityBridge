using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;
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
        /// <summary>マテリアルの組（同じ Material のスロットの組。テクスチャセット 1 つに当たる。<see cref="SurfaceTriangle.Material"/>）。</summary>
        public int Material;
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
        /// <summary>最大の被覆率を与えた面の来歴。重複 UV では同率なら先に訪れた面。</summary>
        public int TriangleIndex;
        public Vector3 Position;
        public SurfacePixel(int x, int y, float coverage) : this(x, y, coverage, -1, default) { }
        public SurfacePixel(int x, int y, float coverage, Vector3 position) : this(x, y, coverage, -1, position) { }
        public SurfacePixel(int x, int y, float coverage, int triangleIndex, Vector3 position)
        { X = x; Y = y; Coverage = coverage; TriangleIndex = triangleIndex; Position = position; }
    }

    public sealed class SurfaceDabResult
    {
        public readonly List<SurfacePixel> Pixels = new List<SurfacePixel>();
        public bool WasClipped { get; internal set; }
        public string Diagnostic { get; internal set; } = string.Empty;
        public int CandidatePixels { get; internal set; }
        public int VisibilityRays { get; internal set; }
        public int RayTriangleTests { get; internal set; }
        public int VisitedTriangles { get; internal set; }
        public long RayNodeVisits { get; internal set; }
        internal SurfaceDabResult Reject(string message)
        {
            Pixels.Clear();
            WasClipped = true;
            Diagnostic = message;
            return this;
        }
    }

    /// <summary>
    /// 1 回のストロークのあいだ、テクセルがカメラから見えるか（可視のレイの結果）を覚えておく。ストロークの間はカメラもスナップショットも
    /// 動かないので、重なり合う次のダブで同じテクセルのレイを撃ち直さなくてよい。カメラ・スナップショットの世代・キャンバスの大きさが
    /// 変わったら空にする。覚えたレイは撃たないので、BVH の仕事量の予算にも数えない（画素の結果は変わらない）。上限を超えたら空にする。
    /// </summary>
    public sealed class SurfaceVisibilityCache
    {
        public const int MaxEntries = 1 << 21;
        internal readonly Dictionary<long, CachedRay> Rays = new Dictionary<long, CachedRay>();
        internal int Revision = -1, Width, Height; internal Vector3 Camera;
        /// <summary>覚えていた結果を使った数（試験と計測用）。</summary>
        public long Hits { get; internal set; }
        public int Count => Rays.Count;
        internal struct CachedRay { public bool Skipped, HasHit; public int HitTriangle; public float HitDistance, CameraDistance; public Vector3 HitPosition; }
        internal void Prepare(int revision, Vector3 camera, int width, int height)
        {
            if (revision == Revision && camera == Camera && width == Width && height == Height && Rays.Count < MaxEntries) return;
            Rays.Clear(); Revision = revision; Camera = camera; Width = width; Height = height;
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
        /// <summary>マテリアルの組の番号: 同じ Material のオブジェクトを使うスロットは同じ組（マテリアルの無いスロットは 1 つの組）。テクスチャセット
        /// 1 つが 1 つの組を描く。範囲（マテリアルの種類）・対称・UV の点の三角形は組で比べる。省けば（負なら）スロットの番号と同じ。</summary>
        public int Material;
        public SurfaceTriangle(Vector3 a, Vector3 b, Vector3 c, Vector2 uvA, Vector2 uvB, Vector2 uvC,
            int rendererIndex = 0, int materialSlot = 0, int material = -1)
        {
            A = a; B = b; C = c; UvA = uvA; UvB = uvB; UvC = uvC;
            RendererIndex = rendererIndex; MaterialSlot = materialSlot; Material = material >= 0 ? material : materialSlot;
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
    public sealed partial class SurfaceGeometry
    {
        readonly SurfaceTriangle[] triangles;
        readonly int[][] adjacency;
        readonly int[] indices;
        readonly List<BvhNode> nodes = new List<BvhNode>();
        readonly float visibilityEpsilon, seamTolerance;
        internal double SnapshotMilliseconds { get; }
        internal double AdjacencyMilliseconds { get; }
        internal double BvhMilliseconds { get; }
        internal IReadOnlyList<int[]> Neighbors => adjacency;
        public int SnapshotRevision { get; }
        public int TriangleCount => triangles.Length;
        /// <summary>スナップショットの三角形（読むだけ。並びは TriangleIndex と同じ）。</summary>
        public IReadOnlyList<SurfaceTriangle> Triangles => triangles;
        public int NonManifoldEdgeCount { get; private set; }
        public Bounds Bounds { get; }

        struct BvhNode { public Bounds Bounds; public int Left, Right, Start, Count; }
        struct BuildBox { public Vector3 Min, Max, Center; }
        sealed class RayQueryBudget
        {
            public int RemainingTriangleTests, RemainingNodeVisits;
            public bool Exceeded;
        }
        readonly struct PositionKey : IEquatable<PositionKey>
        {
            readonly long x, y, z;
            public PositionKey(Vector3 p, double tolerance)
            { x = (long)Math.Round(p.x / tolerance); y = (long)Math.Round(p.y / tolerance); z = (long)Math.Round(p.z / tolerance); }
            public bool Equals(PositionKey other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is PositionKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return (int)Mix(Mix((ulong)x) ^ Mix((ulong)y + 0x9e3779b97f4a7c15UL) ^ Mix((ulong)z + 0x3c6ef372fe94f82aUL)); }
            }
        }
        static ulong Mix(ulong value)
        {
            unchecked { value ^= value >> 30; value *= 0xbf58476d1ce4e5b9UL; value ^= value >> 27; value *= 0x94d049bb133111ebUL; return value ^ (value >> 31); }
        }
        readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            readonly int a, b, renderer, material;
            public EdgeKey(int p, int q, int renderer, int material)
            { a = Math.Min(p, q); b = Math.Max(p, q); this.renderer = renderer; this.material = material; }
            public bool Equals(EdgeKey other) => a == other.a && b == other.b && renderer == other.renderer && material == other.material;
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return (int)Mix(((ulong)(uint)a << 32 | (uint)b) ^ Mix((ulong)(uint)renderer << 32 | (uint)material)); }
            }
        }
        struct EdgeUse { public int First, Second, Count; }

        public SurfaceGeometry(IList<SurfaceTriangle> source, int snapshotRevision = 1, float weldTolerance = 0.000001f)
            : this(source, snapshotRevision, weldTolerance, CancellationToken.None, null) { }

        // Unity オブジェクトを持たない配列の計算。作りかけを公開せず、取消は呼出し元へ返す。
        internal SurfaceGeometry(IList<SurfaceTriangle> source, int snapshotRevision, float weldTolerance, CancellationToken cancellation, Action<float> progress)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!Finite(weldTolerance) || weldTolerance <= 0) throw new ArgumentOutOfRangeException(nameof(weldTolerance));
            cancellation.ThrowIfCancellationRequested();
            var clock = Stopwatch.StartNew();
            SnapshotRevision = snapshotRevision; seamTolerance = weldTolerance;
            triangles = new SurfaceTriangle[source.Count]; source.CopyTo(triangles, 0);
            indices = new int[triangles.Length];
            var boxes = new BuildBox[triangles.Length]; var centers = new Vector3[triangles.Length];
            var bounds = triangles.Length == 0 ? new Bounds(Vector3.zero, Vector3.zero) : triangles[0].Bounds;
            for (int i = 0; i < triangles.Length; i++)
            {
                if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
                SurfaceTriangle t = triangles[i];
                if (!Finite(t.A) || !Finite(t.B) || !Finite(t.C) || !Finite(t.UvA) || !Finite(t.UvB) || !Finite(t.UvC) || !Finite(Vector3.Cross(t.B - t.A, t.C - t.A)))
                    throw new ArgumentException("Mesh contains non-finite position or UV data.", nameof(source));
                var box = t.Bounds; centers[i] = box.center; boxes[i] = new BuildBox { Min = box.min, Max = box.max, Center = box.center };
                indices[i] = i; bounds.Encapsulate(box);
            }
            if (!Finite(bounds.size)) throw new ArgumentException("Mesh bounds exceed supported numeric range.", nameof(source));
            Bounds = bounds;
            visibilityEpsilon = Mathf.Max(0.0000001f, bounds.size.magnitude * 0.000001f);
            SnapshotMilliseconds = clock.Elapsed.TotalMilliseconds; progress?.Invoke(.1f); clock.Restart();
            adjacency = BuildAdjacency(weldTolerance, cancellation, progress);
            AdjacencyMilliseconds = clock.Elapsed.TotalMilliseconds; progress?.Invoke(.65f); clock.Restart();
            if (triangles.Length != 0) BuildBvh(0, triangles.Length, boxes, centers, cancellation, progress);
            BvhMilliseconds = clock.Elapsed.TotalMilliseconds;
            cancellation.ThrowIfCancellationRequested(); progress?.Invoke(1);
        }

        readonly bool[] visibleTriangles;
        readonly int queryRevision;
        static int viewRevision;
        /// <summary>表示用の読み取り専用の問い合わせ。元の三角形・BVH・隣接は共有し、非表示面の印だけを別に持つ。</summary>
        internal SurfaceGeometry VisibleView(Func<SurfaceTriangle, bool> visible)
        {
            if (visible == null) throw new ArgumentNullException(nameof(visible));
            var flags = new bool[triangles.Length]; for (int i = 0; i < flags.Length; i++) flags[i] = Visible(i) && visible(triangles[i]);
            return new SurfaceGeometry(this, flags);
        }
        SurfaceGeometry(SurfaceGeometry source, bool[] visible)
        {
            triangles = source.triangles; adjacency = source.adjacency; indices = source.indices; nodes = source.nodes;
            visibilityEpsilon = source.visibilityEpsilon; seamTolerance = source.seamTolerance; SnapshotRevision = source.SnapshotRevision; Bounds = source.Bounds; NonManifoldEdgeCount = source.NonManifoldEdgeCount;
            SnapshotMilliseconds = source.SnapshotMilliseconds; AdjacencyMilliseconds = source.AdjacencyMilliseconds; BvhMilliseconds = source.BvhMilliseconds;
            visibleTriangles = visible; queryRevision = -System.Threading.Interlocked.Increment(ref viewRevision);
        }
        bool Visible(int triangle) => visibleTriangles == null || visibleTriangles[triangle];

        int[][] BuildAdjacency(float tolerance, CancellationToken cancellation, Action<float> progress)
        {
            var vertices = new Dictionary<PositionKey, int>(triangles.Length);
            var edges = new Dictionary<EdgeKey, int>(triangles.Length * 2);
            var uses = new List<EdgeUse>(triangles.Length * 2);
            for (int i = 0; i < triangles.Length; i++)
            {
                if ((i & 255) == 0) { cancellation.ThrowIfCancellationRequested(); progress?.Invoke(.1f + .4f * i / Math.Max(1, triangles.Length)); }
                var t = triangles[i]; int a = Weld(t.A), b = Weld(t.B), c = Weld(t.C);
                AddEdge(new EdgeKey(a, b, t.RendererIndex, t.MaterialSlot), i);
                AddEdge(new EdgeKey(b, c, t.RendererIndex, t.MaterialSlot), i);
                AddEdge(new EdgeKey(c, a, t.RendererIndex, t.MaterialSlot), i);
            }
            var counts = new int[triangles.Length];
            for (int i = 0; i < uses.Count; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var e = uses[i];
                if (e.Count == 2 && e.First != e.Second) { counts[e.First]++; counts[e.Second]++; }
                else if (e.Count > 2) NonManifoldEdgeCount++;
            }
            var result = new int[triangles.Length][];
            for (int i = 0; i < result.Length; i++) { result[i] = counts[i] == 0 ? Array.Empty<int>() : new int[counts[i]]; counts[i] = 0; }
            // 辺の初出順を保つ。隣接を辿る順番とダブの予算の使い方が変わらない。
            for (int i = 0; i < uses.Count; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var e = uses[i];
                if (e.Count == 2 && e.First != e.Second) { result[e.First][counts[e.First]++] = e.Second; result[e.Second][counts[e.Second]++] = e.First; }
            }
            return result;

            int Weld(Vector3 p)
            {
                var key = new PositionKey(p, tolerance);
                if (!vertices.TryGetValue(key, out int id)) { id = vertices.Count; vertices.Add(key, id); }
                return id;
            }
            void AddEdge(EdgeKey key, int triangle)
            {
                if (!edges.TryGetValue(key, out int id)) { edges.Add(key, uses.Count); uses.Add(new EdgeUse { First = triangle, Count = 1 }); return; }
                var e = uses[id]; if (e.Count == 1) e.Second = triangle; e.Count++; uses[id] = e;
            }
        }

        int BuildBvh(int start, int count, BuildBox[] boxes, Vector3[] positions, CancellationToken cancellation, Action<float> progress)
        {
            cancellation.ThrowIfCancellationRequested();
            var first = boxes[indices[start]];
            Vector3 min = first.Min, max = first.Max, centerMin = first.Center, centerMax = first.Center;
            for (int i = start + 1; i < start + count; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var box = boxes[indices[i]]; min = Vector3.Min(min, box.Min); max = Vector3.Max(max, box.Max);
                centerMin = Vector3.Min(centerMin, box.Center); centerMax = Vector3.Max(centerMax, box.Center);
            }
            var bounds = new Bounds(); bounds.SetMinMax(min, max); var size = centerMax - centerMin;
            int id = nodes.Count;
            nodes.Add(new BvhNode { Bounds = bounds, Start = start, Count = count, Left = -1, Right = -1 });
            if (count <= 8) { progress?.Invoke(.65f + .35f * (start + count) / triangles.Length); return id; }
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            int half = count / 2;
            SelectMedian(start, start + count - 1, start + half, axis, positions, cancellation);
            int left = BuildBvh(start, half, boxes, positions, cancellation, progress), right = BuildBvh(start + half, count - half, boxes, positions, cancellation, progress);
            nodes[id] = new BvhNode { Bounds = bounds, Start = start, Count = 0, Left = left, Right = right };
            return id;
        }

        void SelectMedian(int low, int high, int middle, int axis, Vector3[] positions, CancellationToken cancellation)
        {
            // 3 方向の分割で同じ中心の面も一度に進む。偏りが続く入力はヒープソートに切り替えて仕事量を抑える。
            int depth = 2 * (int)Math.Log(high - low + 1, 2) + 1;
            while (low < high)
            {
                cancellation.ThrowIfCancellationRequested();
                if (depth-- == 0) { HeapSort(low, high, axis, positions, cancellation); return; }
                float a = positions[indices[low]][axis], b = positions[indices[(low + high) / 2]][axis], c = positions[indices[high]][axis];
                float pivot = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
                int lt = low, scan = low, gt = high;
                while (scan <= gt)
                {
                    if ((scan & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                    float value = positions[indices[scan]][axis];
                    if (value < pivot) Swap(lt++, scan++); else if (value > pivot) Swap(scan, gt--); else scan++;
                }
                if (middle < lt) high = lt - 1; else if (middle > gt) low = gt + 1; else return;
            }
        }
        void Swap(int a, int b) { int t = indices[a]; indices[a] = indices[b]; indices[b] = t; }
        void HeapSort(int low, int high, int axis, Vector3[] positions, CancellationToken cancellation)
        {
            int count = high - low + 1;
            for (int i = count / 2 - 1; i >= 0; i--) Sift(i, count);
            for (int end = count - 1; end > 0; end--) { if ((end & 255) == 0) cancellation.ThrowIfCancellationRequested(); Swap(low, low + end); Sift(0, end); }
            void Sift(int i, int length)
            {
                while (i * 2 + 1 < length)
                {
                    int child = i * 2 + 1;
                    if (child + 1 < length && positions[indices[low + child]][axis] < positions[indices[low + child + 1]][axis]) child++;
                    if (positions[indices[low + i]][axis] >= positions[indices[low + child]][axis]) break;
                    Swap(low + i, low + child); i = child;
                }
            }
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
                SnapshotRevision = SnapshotRevision, TriangleIndex = triangle, RendererIndex = t.RendererIndex, MaterialSlot = t.MaterialSlot, Material = t.Material,
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
                int index = indices[i];
                if (!Visible(index)) continue;
                if (work != null && work.RemainingTriangleTests-- <= 0) { work.Exceeded = true; return; }
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
            float hardness = 0.8f, SurfaceBrushBudget budget = null, SurfaceVisibilityCache cache = null, bool ignoreVisibility = false)
        {
            var result = new SurfaceDabResult();
            if (hit.SnapshotRevision != SnapshotRevision || hit.TriangleIndex < 0 || hit.TriangleIndex >= triangles.Length)
            { result.Diagnostic = "The model snapshot changed. Start a new stroke."; return result; }
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || !Finite(radiusWorld) || radiusWorld <= 0 || !Finite(cameraPosition) || !Finite(hit.Position))
            { result.Diagnostic = "Invalid surface brush size, resolution or camera."; return result; }
            var seed = triangles[hit.TriangleIndex];
            if (!Visible(hit.TriangleIndex) || seed.RendererIndex != hit.RendererIndex || seed.MaterialSlot != hit.MaterialSlot)
            { result.Diagnostic = "Surface binding does not match the current snapshot."; return result; }
            budget = budget ?? new SurfaceBrushBudget(); hardness = Mathf.Clamp01(hardness);

            // 1. 三角形を幅優先で辿り、半径の中の候補のテクセルを並びのまま集める（三角形の数と候補の画素の予算はここで、元と同じ所で断る）
            var queue = new Queue<int>(); var visited = new HashSet<int>(); var candidates = new List<DabCandidate>();
            queue.Enqueue(hit.TriangleIndex); visited.Add(hit.TriangleIndex);
            float radiusSquared = radiusWorld * radiusWorld; int processed = 0; string stop = null;
            while (queue.Count > 0 && stop == null)
            {
                int triangleIndex = queue.Dequeue(); var t = triangles[triangleIndex];
                result.VisitedTriangles = ++processed;
                if (processed > budget.MaxTriangles) { stop = "Surface dab exceeded the triangle budget. No pixels were changed; reduce the brush radius."; break; }
                if (!Visible(triangleIndex) || t.RendererIndex != hit.RendererIndex || t.MaterialSlot != hit.MaterialSlot || t.Bounds.SqrDistance(hit.Position) > radiusSquared) continue;
                if ((ClosestPoint(hit.Position, t) - hit.Position).sqrMagnitude > radiusSquared) continue;
                if (ignoreVisibility ? Vector3.Dot(t.Normal, hit.Normal) <= 0 : Vector3.Dot(t.Normal, cameraPosition - (t.A + t.B + t.C) / 3) <= 0) continue;
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

            // 対称先の足跡はカメラから独立。連結・同じスロット・法線の向き・候補画素の予算は守る。
            if (ignoreVisibility)
            {
                if (stop != null) return result.Reject(stop);
                var merged = new Dictionary<int, float>();
                foreach (var candidate in candidates)
                {
                    float coverage = candidate.Distance <= hardness || hardness >= .9999f ? 1 : 1 - Mathf.SmoothStep(0, 1, (candidate.Distance - hardness) / (1 - hardness));
                    int key = candidate.Y * width + candidate.X;
                    if (!merged.TryGetValue(key, out float old) || coverage > old) merged[key] = coverage;
                }
                var sorted = new List<int>(merged.Keys); sorted.Sort();
                foreach (int key in sorted) result.Pixels.Add(new SurfacePixel(key % width, key / width, merged[key]));
                return result;
            }

            // 2・3. 可視のレイを組ごとに並列に撃ち、組ごとに並びのとおりに予算を数えて受け入れる。予算を超える所が見つかればすぐ断る
            // （元の逐次の処理と同じく、超えた後のレイは撃たない。無駄に撃つのは多くても 1 組）。レイの数の予算を超える候補は撃たない。
            int rays = Math.Min(candidates.Count, budget.MaxVisibilityRays);
            const int Chunk = 4096;
            var outcomes = new RayOutcome[Math.Min(rays, Chunk)];
            var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Yozolab.YoluPainter.Core.CoreParallelism.Degree };
            int chunkStart = 0, chunkEnd = 0;
            cache?.Prepare(visibleTriangles == null ? SnapshotRevision : queryRevision, cameraPosition, width, height);
            long Key(DabCandidate c) => (long)c.Triangle << 31 | (long)c.Y * width + c.X;
            void Shoot(int k)
            {
                var c = candidates[chunkStart + k];
                if (cache != null && cache.Rays.TryGetValue(Key(c), out var known)) // 覚えた結果（組を撃つあいだキャッシュは読むだけ）
                {
                    outcomes[k] = new RayOutcome { Cached = true, Skipped = known.Skipped, HasHit = known.HasHit, CameraDistance = known.CameraDistance,
                        Hit = new SurfaceHit { TriangleIndex = known.HitTriangle, Distance = known.HitDistance, Position = known.HitPosition } };
                    return;
                }
                var direction = c.Position - cameraPosition; float distance = direction.magnitude;
                if (distance <= visibilityEpsilon) { outcomes[k] = new RayOutcome { Skipped = true }; return; }
                var work = new RayQueryBudget { RemainingTriangleTests = budget.MaxRayTriangleTests, RemainingNodeVisits = budget.MaxRayNodeVisits };
                bool hasHit = TryRaycastInternal(new Ray(cameraPosition, direction / distance), out var visible, false, distance + visibilityEpsilon * 2, work);
                outcomes[k] = new RayOutcome { HasHit = hasHit, Hit = visible, CameraDistance = distance,
                    Tests = budget.MaxRayTriangleTests - work.RemainingTriangleTests, Visits = budget.MaxRayNodeVisits - work.RemainingNodeVisits, Exceeded = work.Exceeded };
            }
            long tests = 0, visits = 0; var pixels = new Dictionary<int, SurfacePixel>(); int collected = result.CandidatePixels;
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
                    if (cache != null)
                        for (int k = 0; k < count; k++)
                        {
                            var o2 = outcomes[k];
                            if (o2.Cached) { cache.Hits++; continue; }
                            if (o2.Exceeded) continue; // 予算で途中まで撃ったレイは覚えない
                            cache.Rays[Key(candidates[chunkStart + k])] = new SurfaceVisibilityCache.CachedRay { Skipped = o2.Skipped, HasHit = o2.HasHit,
                                HitTriangle = o2.Hit.TriangleIndex, HitDistance = o2.Hit.Distance, HitPosition = o2.Hit.Position, CameraDistance = o2.CameraDistance };
                        }
                }
                var o = outcomes[i - chunkStart];
                if (o.Skipped) continue;
                tests += o.Tests; visits += o.Visits;
                result.RayNodeVisits = visits;
                bool exceeded = o.Exceeded || tests > budget.MaxRayTriangleTests || visits > budget.MaxRayNodeVisits;
                result.RayTriangleTests = (int)Math.Min(tests, (long)budget.MaxRayTriangleTests + 1);
                if (exceeded) return result.Reject("Surface visibility exceeded the BVH work budget. No pixels were changed; reduce the radius or simplify overlapping geometry.");
                var c = candidates[i];
                if (!o.HasHit || o.Hit.Distance < o.CameraDistance - visibilityEpsilon) continue;
                if (o.Hit.TriangleIndex != c.Triangle)
                {
                    // Distance tolerance alone can leak through extremely thin, nearby clothing.
                    // Only an actual adjacent triangle at this triangle's edge may share a visible hit.
                    if (!AtEdge(c.Triangle, c.Bary) ||
                        (o.Hit.Position - c.Position).sqrMagnitude > visibilityEpsilon * visibilityEpsilon ||
                        Array.IndexOf(adjacency[c.Triangle], o.Hit.TriangleIndex) < 0) continue;
                }
                float coverage = c.Distance <= hardness || hardness >= 0.9999f ? 1 : 1 - Mathf.SmoothStep(0, 1, (c.Distance - hardness) / (1 - hardness));
                int key = c.Y * width + c.X;
                if (!pixels.TryGetValue(key, out var current) || coverage > current.Coverage) pixels[key] = new SurfacePixel(c.X, c.Y, coverage, c.Triangle, c.Position);
            }
            result.CandidatePixels = collected;
            if (stop != null) return result.Reject(stop);
            // Deterministic bottom-left row order and max-union prevent shared-edge double paint.
            var keys = new List<int>(pixels.Keys); keys.Sort();
            foreach (int key in keys) result.Pixels.Add(pixels[key]);
            return result;
        }

        struct DabCandidate { public int Triangle, X, Y, CandidatesSoFar; public Vector3 Bary, Position; public float Distance; }
        struct RayOutcome { public bool Skipped, HasHit, Exceeded, Cached; public SurfaceHit Hit; public float CameraDistance; public long Tests, Visits; }

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
            if (!Visible(hit.TriangleIndex) || seed.RendererIndex != hit.RendererIndex || seed.MaterialSlot != hit.MaterialSlot)
            { result.Diagnostic = "Surface binding does not match the current snapshot."; return result; }
            budget = budget ?? new SurfaceBrushBudget(); hardness = Mathf.Clamp01(hardness);
            var rayWork = new RayQueryBudget { RemainingTriangleTests = budget.MaxRayTriangleTests, RemainingNodeVisits = budget.MaxRayNodeVisits };
            var queue = new Queue<int>(); var visited = new HashSet<int>(); var pixels = new Dictionary<int, SurfacePixel>();
            queue.Enqueue(hit.TriangleIndex); visited.Add(hit.TriangleIndex);
            float radiusSquared = radiusWorld * radiusWorld; int processed = 0;
            while (queue.Count > 0)
            {
                int triangleIndex = queue.Dequeue(); var t = triangles[triangleIndex];
                if (++processed > budget.MaxTriangles) return result.Reject("Surface dab exceeded the triangle budget. No pixels were changed; reduce the brush radius.");
                if (!Visible(triangleIndex) || t.RendererIndex != hit.RendererIndex || t.MaterialSlot != hit.MaterialSlot || t.Bounds.SqrDistance(hit.Position) > radiusSquared) continue;
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
                        if (!AtEdge(triangleIndex, bary) ||
                            (visible.Position - position).sqrMagnitude > visibilityEpsilon * visibilityEpsilon ||
                            Array.IndexOf(adjacency[triangleIndex], visible.TriangleIndex) < 0) continue;
                    }
                    float coverage = normalizedDistance <= hardness || hardness >= 0.9999f ? 1 : 1 - Mathf.SmoothStep(0, 1, (normalizedDistance - hardness) / (1 - hardness));
                    int key = y * width + x;
                    if (!pixels.TryGetValue(key, out var current) || coverage > current.Coverage) pixels[key] = new SurfacePixel(x, y, coverage, triangleIndex, position);
                }
            }
            // Deterministic bottom-left row order and max-union prevent shared-edge double paint.
            var keys = new List<int>(pixels.Keys); keys.Sort();
            foreach (int key in keys) result.Pixels.Add(pixels[key]);
            return result;
        }

        /// <summary>
        /// point にいちばん近い面の上の点。BVH を近い枝から辿り、今の最短より遠い枝は刈る。maxDistance より遠い点しか無ければ false。
        /// facing が 0 でなければ、法線が facing と同じ側を向く（内積が正の）三角形だけを見る（薄い板の裏と表、重なった服の内側を
        /// 取り違えないため）。同じ距離なら番号の小さい三角形。節点を見る数が maxNodeVisits を超えたら諦めて false を返し、exceeded を
        /// true にする（重なり合った幾何で問い合わせが止まらないように）。返す当たりの Distance は point からの距離、Barycentric と UV は
        /// その点のもの、Normal は三角形の法線。
        /// </summary>
        /// <param name="material">0 以上なら、そのマテリアルの組（<see cref="SurfaceTriangle.Material"/>）の三角形だけ。</param>
        public bool TryFindClosestPoint(Vector3 point, float maxDistance, Vector3 facing, int maxNodeVisits, out SurfaceHit hit, out bool exceeded, int material = -1)
        {
            hit = default; exceeded = false;
            if (triangles.Length == 0 || !Finite(point) || !Finite(facing) || float.IsNaN(maxDistance) || maxDistance < 0) return false;
            float bestSquared = float.IsPositiveInfinity(maxDistance) ? float.PositiveInfinity : maxDistance * maxDistance;
            int best = -1; Vector3 bestPoint = default; int visits = 0;
            bool useFacing = facing.sqrMagnitude > 0;
            var stack = new Stack<int>(); stack.Push(0);
            while (stack.Count > 0)
            {
                if (++visits > maxNodeVisits) { exceeded = true; return false; }
                var node = nodes[stack.Pop()];
                if (node.Bounds.SqrDistance(point) > bestSquared) continue;
                if (node.Count == 0)
                {
                    // 近い子を先に見る（積むのは遠い子が先）
                    float left = nodes[node.Left].Bounds.SqrDistance(point), right = nodes[node.Right].Bounds.SqrDistance(point);
                    if (left <= right) { stack.Push(node.Right); stack.Push(node.Left); } else { stack.Push(node.Left); stack.Push(node.Right); }
                    continue;
                }
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    int index = indices[i]; if (!Visible(index)) continue; var t = triangles[index];
                    if (useFacing && Vector3.Dot(t.Normal, facing) <= 0) continue;
                    if (material >= 0 && t.Material != material) continue;
                    var closest = ClosestPoint(point, t); float squared = (closest - point).sqrMagnitude;
                    if (squared < bestSquared || (squared == bestSquared && (best < 0 || index < best))) { bestSquared = squared; best = index; bestPoint = closest; }
                }
            }
            if (best < 0) return false;
            var found = triangles[best]; var weights = Barycentric(bestPoint, found);
            hit = new SurfaceHit
            {
                SnapshotRevision = SnapshotRevision, TriangleIndex = best, RendererIndex = found.RendererIndex, MaterialSlot = found.MaterialSlot, Material = found.Material,
                Position = bestPoint, Normal = found.Normal, Distance = Mathf.Sqrt(bestSquared), Barycentric = weights,
                UV = found.UvA * weights.x + found.UvB * weights.y + found.UvC * weights.z
            };
            return true;
        }
        /// <summary>三角形の上の点の重心座標（A・B・C の重み。丸めの誤差で出る負は 0 にして足して 1 に戻す）。</summary>
        static Vector3 Barycentric(Vector3 p, SurfaceTriangle t)
        {
            Vector3 e0 = t.B - t.A, e1 = t.C - t.A, e2 = p - t.A;
            float d00 = Vector3.Dot(e0, e0), d01 = Vector3.Dot(e0, e1), d11 = Vector3.Dot(e1, e1), d20 = Vector3.Dot(e2, e0), d21 = Vector3.Dot(e2, e1);
            float denominator = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denominator) < 1e-30f) return new Vector3(1, 0, 0);
            float v = (d11 * d20 - d01 * d21) / denominator, w = (d00 * d21 - d01 * d20) / denominator;
            var weights = new Vector3(Mathf.Max(0, 1 - v - w), Mathf.Max(0, v), Mathf.Max(0, w));
            float sum = weights.x + weights.y + weights.z;
            return sum > 0 ? weights / sum : new Vector3(1, 0, 0);
        }

        /// <summary>三角形の上の点（重心座標）が辺の上にあるか: いちばん小さい重みが 1e-6 以下か、3D でいちばん近い辺までの距離が
        /// visibilityEpsilon 以下。重みは UV から求めるので、UV の小さい三角形では丸めの誤差が 1e-6 を超え（対角線の上のテクセルで 1.1e-6
        /// を実測）、隣の三角形の継ぎ目の上の当たりを断って穴が開いていた。距離で見る幅は三角形の大きさによらない。</summary>
        bool AtEdge(int triangle, Vector3 bary)
        {
            if (Mathf.Min(bary.x, Mathf.Min(bary.y, bary.z)) <= 1e-6f) return true;
            var t = triangles[triangle]; float twiceArea = Vector3.Cross(t.B - t.A, t.C - t.A).magnitude;
            float toBc = bary.x * twiceArea / Mathf.Max(1e-30f, (t.C - t.B).magnitude);
            float toCa = bary.y * twiceArea / Mathf.Max(1e-30f, (t.A - t.C).magnitude);
            float toAb = bary.z * twiceArea / Mathf.Max(1e-30f, (t.B - t.A).magnitude);
            return Mathf.Min(toBc, Mathf.Min(toCa, toAb)) <= visibilityEpsilon;
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
