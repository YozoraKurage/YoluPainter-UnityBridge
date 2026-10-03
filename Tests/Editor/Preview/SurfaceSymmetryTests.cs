using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 左右対称の箱（X = 0 で半分に分け、左の半分は右の半分を映したもの。UV も u → 1 − u で映すので、テクセル x の対応は W − 1 − x）。
    /// 半分ごとに面の細かさとスロットを選べる（映した側だけ細かくして予算を、別のスロットにして断りを確かめる）。
    /// </summary>
    internal static class SymmetricBox
    {
        /// <summary>箱 [-1, 1]³ の三角形。leftCells / rightCells は面の一辺の分け方、leftSlot は左の半分のスロット。左を作らないなら leftCells = 0。</summary>
        public static List<SurfaceTriangle> Triangles(int rightCells = 1, int leftCells = 1, int leftSlot = 0)
        {
            var list = new List<SurfaceTriangle>();
            list.AddRange(Half(rightCells, 0));
            if (leftCells > 0) list.AddRange(Half(leftCells, leftSlot).Select(Mirror));
            return list;
        }

        /// <summary>右の半分（x ≥ 0）。面の UV は前 v 0.05–0.25、後ろ 0.30–0.50、上 0.55–0.75、下 0.78–0.98（u は x = 0 で 0.5 から 0.7 まで）、
        /// 右の側面は u 0.75–0.95・v 0.30–0.70。</summary>
        static IEnumerable<SurfaceTriangle> Half(int cells, int slot)
        {
            var list = new List<SurfaceTriangle>();
            void Face(Vector3 o, Vector3 es, Vector3 et, Vector3 outward, Vector2 uo, Vector2 us, Vector2 ut)
            {
                Vector3 P(float s, float t) => o + es * s + et * t;
                Vector2 U(float s, float t) => uo + us * s + ut * t;
                void Add(Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc)
                {
                    if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0) { var t = b; b = c; c = t; var tu = ub; ub = uc; uc = tu; }
                    list.Add(new SurfaceTriangle(a, b, c, ua, ub, uc, 0, slot));
                }
                for (int i = 0; i < cells; i++) for (int j = 0; j < cells; j++)
                {
                    float s0 = i / (float)cells, s1 = (i + 1) / (float)cells, t0 = j / (float)cells, t1 = (j + 1) / (float)cells;
                    Add(P(s0, t0), P(s1, t0), P(s1, t1), U(s0, t0), U(s1, t0), U(s1, t1));
                    Add(P(s0, t0), P(s1, t1), P(s0, t1), U(s0, t0), U(s1, t1), U(s0, t1));
                }
            }
            var x = Vector3.right; Vector2 su = new Vector2(.2f, 0), tv = new Vector2(0, .2f);
            Face(new Vector3(0, -1, -1), x, new Vector3(0, 2, 0), Vector3.back, new Vector2(.5f, .05f), su, tv);
            Face(new Vector3(0, -1, 1), x, new Vector3(0, 2, 0), Vector3.forward, new Vector2(.5f, .30f), su, tv);
            Face(new Vector3(0, 1, -1), x, new Vector3(0, 0, 2), Vector3.up, new Vector2(.5f, .55f), su, tv);
            Face(new Vector3(0, -1, -1), x, new Vector3(0, 0, 2), Vector3.down, new Vector2(.5f, .78f), su, tv);
            Face(new Vector3(1, -1, -1), new Vector3(0, 0, 2), new Vector3(0, 2, 0), Vector3.right, new Vector2(.75f, .3f), new Vector2(.2f, 0), new Vector2(0, .4f));
            return list;
        }

        /// <summary>x → −x、u → 1 − u（向きが逆になるので B と C を入れ替えて表を外に保つ）。</summary>
        static SurfaceTriangle Mirror(SurfaceTriangle t)
        {
            Vector3 M(Vector3 p) => new Vector3(-p.x, p.y, p.z);
            Vector2 W(Vector2 uv) => new Vector2(1 - uv.x, uv.y);
            return new SurfaceTriangle(M(t.A), M(t.C), M(t.B), W(t.UvA), W(t.UvC), W(t.UvB), t.RendererIndex, t.MaterialSlot);
        }

        /// <summary>同じ三角形の Unity の Mesh（頂点は三角形ごと。スロットが 2 つあればサブメッシュも 2 つ）。</summary>
        public static Mesh Mesh(IList<SurfaceTriangle> triangles)
        {
            var vertices = new List<Vector3>(); var uvs = new List<Vector2>(); var bySlot = new SortedDictionary<int, List<int>>();
            foreach (var t in triangles)
            {
                if (!bySlot.TryGetValue(t.MaterialSlot, out var indices)) bySlot[t.MaterialSlot] = indices = new List<int>();
                indices.Add(vertices.Count); indices.Add(vertices.Count + 1); indices.Add(vertices.Count + 2);
                vertices.Add(t.A); vertices.Add(t.B); vertices.Add(t.C); uvs.Add(t.UvA); uvs.Add(t.UvB); uvs.Add(t.UvC);
            }
            var mesh = new Mesh { name = "Symmetric test box", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uvs); mesh.subMeshCount = bySlot.Count;
            int sub = 0; foreach (var indices in bySlot.Values) mesh.SetTriangles(indices, sub++);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>
    /// 面のいちばん近い点の問い合わせ（解析的に分かる形で）と、3D のシンメトリーのダブ: 左右対称の箱で片側に置くと反対側の対応する
    /// テクセルに同じ覆いが入り、面の近くで重なっても画素ごとに大きいほうで 1 回だけ、映した先が別のスロット・面の無い所・カメラから
    /// 見えない側なら塗らず、映した側が予算を超えればダブごと断る。
    /// </summary>
    public sealed class SurfaceSymmetryTests
    {
        const int Size = 128;
        static readonly Vector3 FrontCamera = new Vector3(0, 0, -5);

        static SurfaceGeometry Quad()
        {
            // z = 0 の単位の正方形（表は −Z）。UV は x, y のまま
            return new SurfaceGeometry(new[]
            {
                new SurfaceTriangle(Vector3.zero, Vector3.up, Vector3.right, Vector2.zero, Vector2.up, Vector2.right),
                new SurfaceTriangle(Vector3.right, Vector3.up, new Vector3(1, 1, 0), Vector2.right, Vector2.up, Vector2.one),
            });
        }
        static bool Closest(SurfaceGeometry g, Vector3 p, float max, out SurfaceHit hit, Vector3 facing = default)
            => g.TryFindClosestPoint(p, max, facing, SurfaceSymmetry.MaxClosestPointNodeVisits, out hit, out _);

        [Test] public void TheClosestPointIsTheProjectionInsideTheEdgeOutsideAndTheCornerBeyond()
        {
            var g = Quad();
            Assert.That(Closest(g, new Vector3(.3f, .4f, .25f), 1, out var inside), Is.True);
            Assert.That(Vector3.Distance(inside.Position, new Vector3(.3f, .4f, 0)), Is.LessThan(1e-6f));
            Assert.That(inside.Distance, Is.EqualTo(.25f).Within(1e-6f));
            Assert.That(inside.UV.x, Is.EqualTo(.3f).Within(1e-5f)); Assert.That(inside.UV.y, Is.EqualTo(.4f).Within(1e-5f));
            var t = g.Triangles[inside.TriangleIndex];
            Assert.That(Vector3.Distance(t.A * inside.Barycentric.x + t.B * inside.Barycentric.y + t.C * inside.Barycentric.z, inside.Position), Is.LessThan(1e-6f), "barycentric weights rebuild the point");
            Assert.That(inside.Normal.z, Is.EqualTo(-1).Within(1e-6f));

            Assert.That(Closest(g, new Vector3(1.5f, .5f, 0), 1, out var edge), Is.True);
            Assert.That(Vector3.Distance(edge.Position, new Vector3(1, .5f, 0)), Is.LessThan(1e-6f)); Assert.That(edge.Distance, Is.EqualTo(.5f).Within(1e-6f));
            Assert.That(Closest(g, new Vector3(1.3f, 1.4f, 0), 1, out var corner), Is.True);
            Assert.That(Vector3.Distance(corner.Position, Vector3.one - Vector3.forward), Is.LessThan(1e-6f)); Assert.That(corner.Distance, Is.EqualTo(.5f).Within(1e-6f));

            Assert.That(Closest(g, new Vector3(1.5f, .5f, 0), .49f, out _), Is.False, "farther than the limit");
            Assert.That(Closest(g, new Vector3(1.5f, .5f, 0), .5f, out _), Is.True, "exactly at the limit");
        }

        [Test] public void TheSearchMatchesBruteForceOnAClosedBoxAndHonoursTheFacing()
        {
            var triangles = SymmetricBox.Triangles(3, 3); var g = new SurfaceGeometry(triangles);
            var random = new System.Random(7);
            for (int n = 0; n < 200; n++)
            {
                var p = new Vector3((float)random.NextDouble() * 3 - 1.5f, (float)random.NextDouble() * 3 - 1.5f, (float)random.NextDouble() * 3 - 1.5f);
                Assert.That(Closest(g, p, float.PositiveInfinity, out var hit), Is.True);
                float brute = triangles.Min(t => ClosestDistance(p, t));
                Assert.That(hit.Distance, Is.EqualTo(brute).Within(1e-5f), p.ToString("F3"));
            }
            // 箱の中の点: 近いのは +X の面（0.5）。−X を向く面だけなら反対の面（1.5）
            var inside = new Vector3(.5f, .2f, .1f);
            Assert.That(Closest(g, inside, 10, out var near), Is.True); Assert.That(near.Distance, Is.EqualTo(.5f).Within(1e-5f)); Assert.That(near.Normal.x, Is.EqualTo(1).Within(1e-5f));
            Assert.That(Closest(g, inside, 10, out var facing, Vector3.left), Is.True); Assert.That(facing.Distance, Is.EqualTo(1.5f).Within(1e-5f)); Assert.That(facing.Normal.x, Is.EqualTo(-1).Within(1e-5f));
            Assert.That(Closest(g, inside, 1, out _, Vector3.left), Is.False, "the facing side is beyond the limit");
        }

        [Test] public void TheSearchStopsAtItsNodeBudget()
        {
            var g = new SurfaceGeometry(SymmetricBox.Triangles(4, 4));
            Assert.That(g.TryFindClosestPoint(Vector3.zero, 10, default, 1, out _, out bool exceeded), Is.False);
            Assert.That(exceeded, Is.True);
            Assert.That(g.TryFindClosestPoint(Vector3.zero, 10, default, SurfaceSymmetry.MaxClosestPointNodeVisits, out _, out exceeded), Is.True);
            Assert.That(exceeded, Is.False);
        }

        static float ClosestDistance(Vector3 p, SurfaceTriangle t)
        {
            // 平面への射影が三角形の中ならその距離、外なら 3 辺への距離の最小（ClosestPoint とは別の素朴な計算）
            var n = Vector3.Cross(t.B - t.A, t.C - t.A).normalized; var q = p - Vector3.Dot(p - t.A, n) * n;
            bool In(Vector3 a, Vector3 b) => Vector3.Dot(Vector3.Cross(b - a, q - a), n) >= 0;
            if (In(t.A, t.B) && In(t.B, t.C) && In(t.C, t.A)) return Mathf.Abs(Vector3.Dot(p - t.A, n));
            float Segment(Vector3 a, Vector3 b) { var ab = b - a; float s = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude); return Vector3.Distance(p, a + ab * s); }
            return Mathf.Min(Segment(t.A, t.B), Mathf.Min(Segment(t.B, t.C), Segment(t.C, t.A)));
        }

        static SurfaceHit Pick(SurfaceGeometry g, Vector3 camera, Vector3 at)
        {
            Assert.That(g.TryRaycast(new Ray(camera, at - camera), out var hit), Is.True);
            return hit;
        }
        static MirrorPlane PlaneX => MirrorPlane.FromModel(Vector3.zero, Quaternion.identity, SymmetryAxis.X, 0);
        static Dictionary<(int, int), float> Map(SurfaceDabResult r) => r.Pixels.ToDictionary(p => (p.X, p.Y), p => p.Coverage);

        /// <summary>a のどの画素も、b の映した画素（W − 1 − x）に同じ覆いがある（境の丸めで片方だけに出る、ほぼ 0 の画素は除く）。</summary>
        static void AssertMirrored(Dictionary<(int, int), float> a, Dictionary<(int, int), float> b, string what)
        {
            int compared = 0;
            foreach (var (key, coverage) in a.Select(kv => (kv.Key, kv.Value)))
            {
                var mirror = (Size - 1 - key.Item1, key.Item2);
                float other = b.TryGetValue(mirror, out float c) ? c : 0;
                if (coverage < 1e-3f && other < 1e-3f) continue;
                Assert.That(other, Is.EqualTo(coverage).Within(1e-4f), what + " at " + key);
                compared++;
            }
            Assert.That(compared, Is.GreaterThan(20), what + ": the dab covered a real area");
        }

        [Test] public void APointOnOneSidePaintsTheMirroredTexelsWithTheSameCoverage()
        {
            var g = new SurfaceGeometry(SymmetricBox.Triangles());
            var hit = Pick(g, FrontCamera, new Vector3(.4f, .1f, -1));
            var dab = SurfaceSymmetry.Build(g, hit, PlaneX, .3f, Size, Size, FrontCamera, .5f);
            Assert.That(dab.Outcome, Is.EqualTo(MirrorOutcome.Painted));
            Assert.That(dab.HasMirrorHit, Is.True);
            Assert.That(Vector3.Distance(dab.MirrorHit.Position, new Vector3(-hit.Position.x, hit.Position.y, hit.Position.z)), Is.LessThan(1e-5f));
            var original = Map(dab.Original); var mirror = Map(dab.Mirror);
            Assert.That(original.Keys.All(k => k.Item1 >= Size / 2) && mirror.Keys.All(k => k.Item1 < Size / 2), Is.True, "0.8 apart with radius 0.3: the two dabs are separate");
            AssertMirrored(original, mirror, "mirror of the original");
            AssertMirrored(mirror, original, "original of the mirror");
            Assert.That(dab.Result.Pixels.Count, Is.EqualTo(original.Count + mirror.Count));
            Assert.That(dab.Result.WasClipped, Is.False);
        }

        [Test] public void NearThePlaneTheTwoDabsAreJoinedByTheLargerCoverageOnce()
        {
            var g = new SurfaceGeometry(SymmetricBox.Triangles());
            var hit = Pick(g, FrontCamera, new Vector3(.1f, .1f, -1));
            var dab = SurfaceSymmetry.Build(g, hit, PlaneX, .3f, Size, Size, FrontCamera, .5f);
            Assert.That(dab.Outcome, Is.EqualTo(MirrorOutcome.Painted));
            var original = Map(dab.Original); var mirror = Map(dab.Mirror); var result = dab.Result.Pixels;
            Assert.That(original.Keys.Intersect(mirror.Keys).Count(), Is.GreaterThan(20), "the dabs overlap across the plane");
            Assert.That(result.Select(p => (p.X, p.Y)).Distinct().Count(), Is.EqualTo(result.Count), "every pixel once");
            Assert.That(result.Count, Is.EqualTo(original.Keys.Union(mirror.Keys).Count()));
            foreach (var p in result)
            {
                float expected = Mathf.Max(original.TryGetValue((p.X, p.Y), out float a) ? a : 0, mirror.TryGetValue((p.X, p.Y), out float b) ? b : 0);
                Assert.That(p.Coverage, Is.EqualTo(expected), "the larger coverage, not the sum, at " + p.X + "," + p.Y);
            }
            var keys = result.Select(p => p.Y * Size + p.X).ToList();
            Assert.That(keys, Is.Ordered, "bottom-left row order, like every surface dab");
            AssertMirrored(Map(dab.Result), Map(dab.Result), "the joined dab is symmetric");
        }

        [Test] public void ADabCenteredOnThePlaneIsPaintedOnce()
        {
            var g = new SurfaceGeometry(SymmetricBox.Triangles());
            var hit = Pick(g, FrontCamera, new Vector3(0, .1f, -1));
            Assert.That(Mathf.Abs(hit.Position.x), Is.LessThan(1e-6f));
            var dab = SurfaceSymmetry.Build(g, hit, PlaneX, .3f, Size, Size, FrontCamera, .5f);
            Assert.That(dab.Outcome, Is.EqualTo(MirrorOutcome.OnPlane));
            Assert.That(dab.Mirror, Is.Null);
            var alone = g.BuildSurfaceDabs(hit, .3f, Size, Size, FrontCamera, .5f);
            Assert.That(dab.Result.Pixels.Select(p => (p.X, p.Y, p.Coverage)), Is.EqualTo(alone.Pixels.Select(p => (p.X, p.Y, p.Coverage))));
        }

        [Test] public void AMirrorOnAnotherSlotOrWithNoSurfaceIsNotPainted()
        {
            var other = new SurfaceGeometry(SymmetricBox.Triangles(leftSlot: 1));
            var hit = Pick(other, FrontCamera, new Vector3(.4f, .1f, -1));
            var dab = SurfaceSymmetry.Build(other, hit, PlaneX, .3f, Size, Size, FrontCamera, .5f);
            Assert.That(dab.Outcome, Is.EqualTo(MirrorOutcome.OtherSlot));
            Assert.That(dab.MirrorHit.MaterialSlot, Is.EqualTo(1));
            Assert.That(dab.Result.Pixels.Select(p => (p.X, p.Y, p.Coverage)), Is.EqualTo(dab.Original.Pixels.Select(p => (p.X, p.Y, p.Coverage))), "only this side");

            // 左の半分が無い: 映した点 (−0.4, …) から向きの合う面はいちばん近くても 0.4 先（半径 0.3 より遠い）
            var half = new SurfaceGeometry(SymmetricBox.Triangles(leftCells: 0));
            hit = Pick(half, FrontCamera, new Vector3(.4f, .1f, -1));
            dab = SurfaceSymmetry.Build(half, hit, PlaneX, .3f, Size, Size, FrontCamera, .5f);
            Assert.That(dab.Outcome, Is.EqualTo(MirrorOutcome.NoSurface));
            Assert.That(dab.Result.Pixels.Count, Is.EqualTo(dab.Original.Pixels.Count)); Assert.That(dab.Result.Pixels.Count, Is.GreaterThan(0));
            // 半径がそれより大きければ、届く面（右の前の面の、面の際）に置く
            dab = SurfaceSymmetry.Build(half, hit, PlaneX, .45f, Size, Size, FrontCamera, .5f);
            Assert.That(dab.Outcome, Is.EqualTo(MirrorOutcome.Painted));
            Assert.That(dab.MirrorHit.Position.x, Is.EqualTo(0).Within(1e-5f));
        }

        [Test] public void AMirrorTheCameraCannotSeeIsNotPainted()
        {
            var g = new SurfaceGeometry(SymmetricBox.Triangles());
            var camera = new Vector3(5, .2f, .3f); // +X の側から見る: 映した −X の面は裏
            var hit = Pick(g, camera, new Vector3(1, .2f, .3f));
            var dab = SurfaceSymmetry.Build(g, hit, PlaneX, .3f, Size, Size, camera, .5f);
            Assert.That(dab.Outcome, Is.EqualTo(MirrorOutcome.Hidden));
            Assert.That(dab.MirrorHit.Normal.x, Is.EqualTo(-1).Within(1e-5f), "the mirror landed on the far side, facing the right way");
            Assert.That(dab.Mirror.Pixels, Is.Empty);
            Assert.That(dab.Result.Pixels.Count, Is.EqualTo(dab.Original.Pixels.Count)); Assert.That(dab.Result.Pixels.Count, Is.GreaterThan(0));
        }

        [Test] public void AMirroredDabOverItsBudgetRefusesTheWholeDab()
        {
            // 右は粗く（面ごとに 2 枚）、映した左は細かい（面ごとに 512 枚）。三角形の予算 64 は右なら足り、左では超える
            var g = new SurfaceGeometry(SymmetricBox.Triangles(1, 16));
            var hit = Pick(g, FrontCamera, new Vector3(.4f, .1f, -1));
            var budget = new SurfaceBrushBudget { MaxTriangles = 64 };
            var alone = g.BuildSurfaceDabs(hit, .3f, Size, Size, FrontCamera, .5f, budget);
            Assert.That(alone.WasClipped, Is.False, alone.Diagnostic);
            var dab = SurfaceSymmetry.Build(g, hit, PlaneX, .3f, Size, Size, FrontCamera, .5f, budget);
            Assert.That(dab.Result.WasClipped, Is.True);
            Assert.That(dab.Result.Pixels, Is.Empty, "no partial coverage");
            Assert.That(dab.Result.Diagnostic, Does.Contain("budget").And.Contain("Mirrored side"));
            // 予算が足りれば同じ所に塗れる（左右で三角形の分け方が違っても、覆いは同じ）
            dab = SurfaceSymmetry.Build(g, hit, PlaneX, .3f, Size, Size, FrontCamera, .5f);
            Assert.That(dab.Outcome, Is.EqualTo(MirrorOutcome.Painted));
            AssertMirrored(Map(dab.Original), Map(dab.Mirror), "fine and coarse halves");
        }

        [Test] public void ThePlaneFollowsTheModelRootAxisAndOffset()
        {
            var rotation = Quaternion.Euler(0, 90, 0); // ルートのローカルの X は世界の −Z
            var plane = MirrorPlane.FromModel(new Vector3(1, 2, 3), rotation, SymmetryAxis.X, .5f);
            Assert.That(Vector3.Distance(plane.Normal, rotation * Vector3.right), Is.LessThan(1e-6f));
            Assert.That(plane.SignedDistance(new Vector3(1, 2, 3)), Is.EqualTo(-.5f).Within(1e-6f));
            var p = new Vector3(4, -1, 7);
            Assert.That(Vector3.Distance(plane.Reflect(plane.Reflect(p)), p), Is.LessThan(1e-5f), "reflecting twice returns");
            Assert.That(plane.SignedDistance(plane.Reflect(p)), Is.EqualTo(-plane.SignedDistance(p)).Within(1e-5f));
            var y = MirrorPlane.FromModel(Vector3.zero, Quaternion.identity, SymmetryAxis.Y, 0);
            Assert.That(y.Reflect(new Vector3(1, 2, 3)), Is.EqualTo(new Vector3(1, -2, 3)));
            var z = MirrorPlane.FromModel(Vector3.zero, Quaternion.identity, SymmetryAxis.Z, 1);
            Assert.That(z.Reflect(new Vector3(1, 2, 3)), Is.EqualTo(new Vector3(1, 2, -1)));
        }
    }
}
