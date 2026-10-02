using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 3D ビューのダブ（SurfaceGeometry.BuildSurfaceDabs）の可視のレイを並列にしても、画素・覆い・数（候補の画素・レイの数・三角形の
    /// テスト）・断る理由が、元の逐次の実装（BuildSurfaceDabsReference）と同じこと。三角形 1,600 枚の球と、その手前で一部を隠す板で、
    /// 当たり所・半径・硬さ・小さい予算を変え、並列度 1 と既定の両方で比べる。
    /// </summary>
    public sealed class SurfaceDabParallelTests
    {
        static readonly Vector3 Camera = new Vector3(0.3f, 0.2f, -3);

        static SurfaceGeometry Scene()
        {
            var triangles = new List<SurfaceTriangle>();
            const int lat = 20, lon = 40;
            Vector3 P(int i, int j) { float t = Mathf.PI * i / lat, p = 2 * Mathf.PI * j / lon; return new Vector3(Mathf.Sin(t) * Mathf.Cos(p), Mathf.Cos(t), Mathf.Sin(t) * Mathf.Sin(p)); }
            Vector2 U(int i, int j) => new Vector2(j / (float)lon, 1 - i / (float)lat);
            void Add(Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc, int renderer)
            {
                // 外向き（球）・カメラ向き（板）の法線になるよう巻き順を揃える
                var n = Vector3.Cross(b - a, c - a); var centroid = (a + b + c) / 3;
                bool flip = renderer == 0 ? Vector3.Dot(n, centroid) < 0 : Vector3.Dot(n, Camera - centroid) < 0;
                triangles.Add(flip ? new SurfaceTriangle(a, c, b, ua, uc, ub, renderer, renderer) : new SurfaceTriangle(a, b, c, ua, ub, uc, renderer, renderer));
            }
            for (int i = 0; i < lat; i++)
                for (int j = 0; j < lon; j++)
                {
                    Add(P(i, j), P(i + 1, j), P(i, j + 1), U(i, j), U(i + 1, j), U(i, j + 1), 0);
                    Add(P(i + 1, j), P(i + 1, j + 1), P(i, j + 1), U(i + 1, j), U(i + 1, j + 1), U(i, j + 1), 0);
                }
            // 手前の板（別のレンダラー）: 球の一部をカメラから隠す
            Vector3 a0 = new Vector3(-.2f, -.1f, -1.6f), a1 = new Vector3(.4f, -.1f, -1.6f), a2 = new Vector3(.4f, .5f, -1.6f), a3 = new Vector3(-.2f, .5f, -1.6f);
            Add(a0, a1, a2, Vector2.zero, Vector2.right, Vector2.one, 1); Add(a0, a2, a3, Vector2.zero, Vector2.one, Vector2.up, 1);
            return new SurfaceGeometry(triangles);
        }

        static string Describe(SurfaceDabResult r) =>
            r.Diagnostic + " | clipped=" + r.WasClipped + " candidates=" + r.CandidatePixels + " rays=" + r.VisibilityRays + " tests=" + r.RayTriangleTests + " pixels=" + r.Pixels.Count;

        static void Same(SurfaceDabResult expected, SurfaceDabResult actual, string what)
        {
            Assert.That(Describe(actual), Is.EqualTo(Describe(expected)), what);
            for (int i = 0; i < expected.Pixels.Count; i++)
            {
                var e = expected.Pixels[i]; var a = actual.Pixels[i];
                Assert.That((a.X, a.Y, a.Coverage), Is.EqualTo((e.X, e.Y, e.Coverage)), what + " pixel " + i);
            }
        }

        [Test] public void TheParallelDabMatchesTheSequentialOneExactly()
        {
            var geometry = Scene(); var random = new System.Random(7); int compared = 0, occluded = 0, refused = 0, multiChunk = 0;
            int saved = CoreParallelism.MaxDegreeOfParallelism;
            try
            {
                for (int n = 0; n < 160; n++)
                {
                    var target = new Vector3((float)random.NextDouble() * 1.4f - .7f, (float)random.NextDouble() * 1.4f - .7f, -.5f);
                    if (!geometry.TryRaycast(new Ray(Camera, target - Camera), out var hit)) continue;
                    if (hit.RendererIndex != 0) continue;
                    float radius = new[] { .05f, .15f, .3f, .6f }[n % 4]; float hardness = new[] { .8f, .3f, 1f }[n % 3];
                    SurfaceBrushBudget budget = null;
                    if (n % 5 == 3) budget = new SurfaceBrushBudget { MaxVisibilityRays = 500 };
                    if (n % 5 == 4) budget = new SurfaceBrushBudget { MaxRayTriangleTests = 20000 };
                    if (n % 7 == 6) budget = new SurfaceBrushBudget { MaxCandidatePixels = 3000 };
                    if (n % 11 == 10) budget = new SurfaceBrushBudget { MaxTriangles = 12 };
                    // ときどき大きな解像度にして、レイが何組（4096 本ずつ）にもまたがり、途中の組で予算を超える場合も比べる
                    int size = n % 8 == 5 ? 2048 : 512;
                    if (size == 2048 && n % 16 == 13) budget = new SurfaceBrushBudget { MaxRayTriangleTests = 150000 };
                    var expected = geometry.BuildSurfaceDabsReference(hit, radius, size, size, Camera, hardness, budget);
                    foreach (int degree in new[] { 1, 0 })
                    {
                        CoreParallelism.MaxDegreeOfParallelism = degree;
                        Same(expected, geometry.BuildSurfaceDabs(hit, radius, size, size, Camera, hardness, budget), "dab " + n + " degree " + degree + " size " + size);
                    }
                    if (expected.VisibilityRays > 4096) multiChunk++;
                    compared++;
                    if (expected.Diagnostic != null && expected.Diagnostic.Length > 0) refused++;
                    else if (expected.VisibilityRays > expected.Pixels.Count) occluded++;
                }
            }
            finally { CoreParallelism.MaxDegreeOfParallelism = saved; }
            Assert.That(compared, Is.GreaterThan(50));
            Assert.That(refused, Is.GreaterThan(3), "some dabs hit the small budgets, so refusals are compared too");
            Assert.That(occluded, Is.GreaterThan(3), "some dabs have texels hidden by the plate or the sphere's own curve");
            Assert.That(multiChunk, Is.GreaterThan(2), "some dabs shoot their rays in several chunks");
        }
    }
}
