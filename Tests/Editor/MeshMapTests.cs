using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>mesh map のベイク: 平面・箱・L 字・板の形で式どおりの値（AO・曲率・厚み・法線・位置）、UV の覆いと余白、UV の重なり、
    /// 決定性（並列の分け方に依らず同じバイト列）、取消・時間切れで何も残さない、予算と型の拒否、保存形式の往復と壊れた入力の拒否、
    /// 由来による古さの判定、プレビューのスナップショットからの入力（元のアセットを変えない）。</summary>
    public sealed class MeshMapTests
    {
        [SetUp] public void TolerateBrokenShaderCompiler() { EditorShaderCompiler.TolerateErrorLogsIfBroken(); }

        /// <summary>三角形の並びを組み立てる。四角形は (p0,p1,p2) と (p2,p1,p3) で、表は (p1−p0)×(p2−p0) の向き（Unity と同じ）。
        /// UV は p0=(u0,v0)、p1=(u0,v1)、p2=(u1,v0)、p3=(u1,v1)。頂点法線は面の法線。</summary>
        sealed class MeshBuilder
        {
            readonly List<float> corners = new List<float>(), normals = new List<float>(), uvs = new List<float>();
            readonly List<int> slots = new List<int>();
            public MeshBuilder Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u0, float v0, float u1, float v1, int slot = 0)
            {
                var n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                Corner(p0, n, u0, v0); Corner(p1, n, u0, v1); Corner(p2, n, u1, v0); slots.Add(slot);
                Corner(p2, n, u1, v0); Corner(p1, n, u0, v1); Corner(p3, n, u1, v1); slots.Add(slot);
                return this;
            }
            void Corner(Vector3 p, Vector3 n, float u, float v)
            { corners.Add(p.x); corners.Add(p.y); corners.Add(p.z); normals.Add(n.x); normals.Add(n.y); normals.Add(n.z); uvs.Add(u); uvs.Add(v); }
            public float[] Corners => corners.ToArray();
            public float[] Normals => normals.ToArray();
            public float[] Uvs => uvs.ToArray();
            public MeshBakeInput Build(bool withNormals = true) => new MeshBakeInput(corners.ToArray(), withNormals ? normals.ToArray() : null, uvs.ToArray(), slots.ToArray());
        }

        const float Margin = 0.02f;
        /// <summary>箱。6 面がそれぞれ別の UV の島（3×2 の格子、余白 0.02）。面の順はデモのキューブと同じ: −z, +z, −x, +x, +y, −y。</summary>
        static MeshBuilder Box(Vector3 min, Vector3 max, MeshBuilder b = null)
        {
            b = b ?? new MeshBuilder();
            Vector3 P(int x, int y, int z) => new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z);
            var faces = new[]
            {
                new[] { P(0, 0, 0), P(0, 1, 0), P(1, 0, 0), P(1, 1, 0) }, new[] { P(1, 0, 1), P(1, 1, 1), P(0, 0, 1), P(0, 1, 1) },
                new[] { P(0, 0, 1), P(0, 1, 1), P(0, 0, 0), P(0, 1, 0) }, new[] { P(1, 0, 0), P(1, 1, 0), P(1, 0, 1), P(1, 1, 1) },
                new[] { P(0, 1, 0), P(0, 1, 1), P(1, 1, 0), P(1, 1, 1) }, new[] { P(0, 0, 1), P(0, 0, 0), P(1, 0, 1), P(1, 0, 0) },
            };
            for (int f = 0; f < 6; f++) { var r = Island(f); b.Quad(faces[f][0], faces[f][1], faces[f][2], faces[f][3], r.u0, r.v0, r.u1, r.v1); }
            return b;
        }
        static (float u0, float v0, float u1, float v1) Island(int face) => ((face % 3) / 3f + Margin, (face / 3) * 0.5f + Margin, (face % 3 + 1) / 3f - Margin, (face / 3 + 1) * 0.5f - Margin);
        /// <summary>床（y = 0、法線 +y、u → x、v → z）と壁（x = 0、法線 +x）が内角 90° で接する L 字。どちらも 1×1。</summary>
        static MeshBuilder Step()
        {
            return new MeshBuilder()
                .Quad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1), 0.02f, 0.02f, 0.48f, 0.98f)
                .Quad(new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1), 0.52f, 0.02f, 0.98f, 0.98f);
        }
        /// <summary>テクセルの中心の UV を、u0..u1 → a0..a1 に線形に写す。</summary>
        static double Along(int texel, int size, float u0, float u1, double a0, double a1) => a0 + ((texel + 0.5) / size - u0) / (u1 - u0) * (a1 - a0);
        static bool Baked(BakedMeshMap map, int x, int y) { var c = map.CoverageAt(x, y); return c == MeshTexelCoverage.Covered || c == MeshTexelCoverage.Overlap; }
        static Dictionary<MeshMapKind, BakedMeshMap> Bake(MeshBakeInput input, MeshBakeSettings settings, MeshBakeBudget budget = null)
        {
            var result = MeshBaker.Bake(input, settings, budget);
            Assert.That(result.Status, Is.EqualTo(MeshBakeStatus.Completed));
            Assert.That(result.Maps.Select(m => m.Kind), Is.EquivalentTo(settings.Maps));
            return result.Maps.ToDictionary(m => m.Kind);
        }
        /// <summary>まっすぐな 90° の辺から距離 d のテクセルの曲率（凸 +、凹 −）の期待値: 0.5 ± 0.5 (1 − d²/r²)^(5/2)。</summary>
        static double EdgeCurvature(double d, double r, int sign) => d >= r ? 0.5 : 0.5 + sign * 0.5 * Math.Pow(1 - d * d / (r * r), 2.5);

        [Test] public void FlatQuadHasFullOcclusionNeutralCurvatureAndConstantNormal()
        {
            var input = new MeshBuilder().Quad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1), 0.1f, 0.1f, 0.9f, 0.9f).Build();
            var settings = new MeshBakeSettings { Width = 128, Height = 128, Padding = 4 };
            var result = MeshBaker.Bake(input, settings);
            var maps = result.Maps.ToDictionary(m => m.Kind);
            // 中心が 0.1..0.9 に入るテクセルは 13..114 の 102 列（両軸）。余白 4 段は 8 近傍で広がるので 110 × 110 まで
            Assert.That(result.Report.CoveredTexels, Is.EqualTo(102 * 102)); Assert.That(result.Report.OverlapTexels, Is.Zero, "the quad's own diagonal is not an overlap");
            Assert.That(result.Report.PaddedTexels, Is.EqualTo(110 * 110 - 102 * 102)); Assert.That(result.Report.EmptyTexels, Is.EqualTo(128 * 128 - 110 * 110));
            for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
            {
                bool inside = x >= 13 && x <= 114 && y >= 13 && y <= 114, padded = !inside && x >= 9 && x <= 118 && y >= 9 && y <= 118;
                var c = maps[MeshMapKind.AmbientOcclusion].CoverageAt(x, y);
                Assert.That(c, Is.EqualTo(inside ? MeshTexelCoverage.Covered : padded ? MeshTexelCoverage.Padding : MeshTexelCoverage.Empty), x + "," + y);
                if (c == MeshTexelCoverage.Empty)
                {
                    foreach (var map in maps.Values) for (int ch = 0; ch < map.Channels; ch++) Assert.That(map.RawValue(x, y, ch), Is.Zero, "empty texels hold no invented data");
                    continue;
                }
                Assert.That(maps[MeshMapKind.AmbientOcclusion].RawValue(x, y), Is.EqualTo(65535), "nothing occludes a lone quad");
                Assert.That(maps[MeshMapKind.Curvature].RawValue(x, y), Is.EqualTo(32768), "flat: curvature 0.5");
                Assert.That(maps[MeshMapKind.Thickness].RawValue(x, y), Is.EqualTo(65535), "inward rays leave an open quad");
                Assert.That(new[] { maps[MeshMapKind.WorldNormal].RawValue(x, y, 0), maps[MeshMapKind.WorldNormal].RawValue(x, y, 1), maps[MeshMapKind.WorldNormal].RawValue(x, y, 2) }, Is.EqualTo(new ushort[] { 32768, 65535, 32768 }), "normal +y");
                Assert.That(maps[MeshMapKind.Position].RawValue(x, y, 1), Is.EqualTo(32768), "an axis without extent reads 0.5");
                if (inside)
                {
                    Assert.That(maps[MeshMapKind.Position].Value(x, y, 0), Is.EqualTo(Along(x, 128, 0.1f, 0.9f, 0, 1)).Within(2e-5), "position x follows u");
                    Assert.That(maps[MeshMapKind.Position].Value(x, y, 2), Is.EqualTo(Along(y, 128, 0.1f, 0.9f, 0, 1)).Within(2e-5), "position z follows v");
                }
            }
        }

        [Test] public void ConvexBoxEdgesFollowTheCurvatureProfileAndStayUnoccluded()
        {
            var input = Box(Vector3.zero, Vector3.one).Build();
            const int size = 512;
            var maps = Bake(input, new MeshBakeSettings { Width = size, Height = size, Maps = new[] { MeshMapKind.Curvature, MeshMapKind.AmbientOcclusion }, AoSamples = 32, Padding = 0 });
            double r = 0.02 * Math.Sqrt(3);
            var top = Island(4); // +y: u → x, v → z
            int checkedEdge = 0, checkedFlat = 0;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                if (!Baked(maps[MeshMapKind.Curvature], x, y)) continue;
                Assert.That(maps[MeshMapKind.AmbientOcclusion].RawValue(x, y), Is.EqualTo(65535), "a convex box cannot occlude itself");
                double u = (x + 0.5) / size, v = (y + 0.5) / size;
                if (u < top.u0 || u > top.u1 || v < top.v0 || v > top.v1) continue;
                double px = Along(x, size, top.u0, top.u1, 0, 1), pz = Along(y, size, top.v0, top.v1, 0, 1);
                double dx = Math.Min(px, 1 - px), dz = Math.Min(pz, 1 - pz);
                if (Math.Min(dx, dz) < r && Math.Max(dx, dz) < r + 1e-3) continue; // 角の近くは 2 本の辺が入る
                double expected = EdgeCurvature(Math.Min(dx, dz), r, +1);
                Assert.That(maps[MeshMapKind.Curvature].Value(x, y), Is.EqualTo(expected).Within(1e-3), "texel " + x + "," + y + " at " + Math.Min(dx, dz) / r + " r from a convex edge");
                if (expected > 0.5) checkedEdge++; else checkedFlat++;
            }
            Assert.That(checkedEdge, Is.GreaterThan(1000)); Assert.That(checkedFlat, Is.GreaterThan(10000));
        }

        [Test] public void ConcaveStepDarkensTheInnerCornerAndReadsConcave()
        {
            var input = Step().Build();
            const int size = 256;
            var settings = new MeshBakeSettings { Width = size, Height = size, Maps = new[] { MeshMapKind.Curvature, MeshMapKind.AmbientOcclusion }, Padding = 0 };
            var maps = Bake(input, settings);
            double r = 0.02 * Math.Sqrt(3);
            float u0 = 0.02f, u1 = 0.48f, v0 = 0.02f, v1 = 0.98f; // 床: u → x（壁からの距離）、v → z
            int y = size / 2;
            double AoAt(double distance)
            {
                int x = (int)Math.Round(u0 * size + distance * (u1 - u0) * size - 0.5);
                return maps[MeshMapKind.AmbientOcclusion].Value(x, y);
            }
            Assert.That(AoAt(0.02), Is.LessThan(0.85), "next to the wall half of the hemisphere is blocked");
            Assert.That(AoAt(0.02), Is.LessThan(AoAt(0.08)), "occlusion fades with distance from the corner");
            Assert.That(AoAt(0.3), Is.EqualTo(1), "beyond the max distance (0.1 of the diagonal) nothing occludes");
            int concave = 0;
            for (int yy = 0; yy < size; yy++) for (int x = 0; x < size; x++)
            {
                double u = (x + 0.5) / size, v = (yy + 0.5) / size;
                if (u < u0 || u > u1 || v < v0 || v > v1 || !Baked(maps[MeshMapKind.Curvature], x, yy)) continue;
                double px = Along(x, size, u0, u1, 0, 1), pz = Along(yy, size, v0, v1, 0, 1);
                if (pz < r || pz > 1 - r) continue; // 辺の両端（縁）では球が辺の外に出る
                double expected = EdgeCurvature(px, r, -1);
                Assert.That(maps[MeshMapKind.Curvature].Value(x, yy), Is.EqualTo(expected).Within(1e-3), "texel " + x + "," + yy);
                if (expected < 0.5) concave++;
            }
            Assert.That(concave, Is.GreaterThan(200));

            // 減衰なし・遠くまで: 内角のすぐそばは、壁側の半球（余弦重みでちょうど半分）が塞がる
            var flat = Bake(input, new MeshBakeSettings { Width = size, Height = size, Maps = new[] { MeshMapKind.AmbientOcclusion }, AoFalloff = MeshOcclusionFalloff.None, AoMaxDistance = 2, AoSamples = 256, Padding = 0 });
            int nearest = (int)Math.Ceiling(u0 * size - 0.5);
            Assert.That(flat[MeshMapKind.AmbientOcclusion].Value(nearest, y), Is.EqualTo(0.5).Within(0.03));
        }

        [TestCase(10, 0.005)]
        [TestCase(90, 0.03)]
        public void ThicknessOfASlabMatchesItsDepth(double spread, double tolerance)
        {
            const float depth = 0.1f; const int size = 256;
            var input = Box(Vector3.zero, new Vector3(1, depth, 1)).Build();
            var settings = new MeshBakeSettings { Width = size, Height = size, Maps = new[] { MeshMapKind.Thickness }, ThicknessMaxDistance = 0.5, ThicknessSpreadDegrees = spread, Padding = 0 };
            var map = Bake(input, settings)[MeshMapKind.Thickness];
            double maximum = 0.5 * input.Diagonal, half = spread / 2 * Math.PI / 180;
            double expected = depth * 2 / (1 + Math.Cos(half)); // 余弦重みの円錐での 1/cosθ の平均
            var top = Island(4); double sum = 0; int n = 0;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                double u = (x + 0.5) / size, v = (y + 0.5) / size;
                if (u < top.u0 || u > top.u1 || v < top.v0 || v > top.v1) continue;
                double px = Along(x, size, top.u0, top.u1, 0, 1), pz = Along(y, size, top.v0, top.v1, 0, 1);
                if (px < 0.2 || px > 0.8 || pz < 0.2 || pz > 0.8) continue; // 斜めのレイが側面に当たらない所
                sum += map.Value(x, y) * maximum; n++;
            }
            Assert.That(n, Is.GreaterThan(500));
            Assert.That(sum / n, Is.EqualTo(expected).Within(expected * tolerance), "mean thickness over " + n + " texels");
        }

        [Test] public void PaddingCopiesTheNearestIslandAndNeverTouchesAnotherIsland()
        {
            // 2 つの島（法線 +y と −y）の間に 6 テクセルのすき間（29..34）。右の島は UV を左右反転（UV の面積の符号が逆）
            var input = new MeshBuilder()
                .Quad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1), 0.05f, 0.05f, 0.45f, 0.95f)
                .Quad(new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), 0.95f, 0.05f, 0.55f, 0.95f).Build();
            var maps = new[] { MeshMapKind.WorldNormal, MeshMapKind.Position };
            var bare = Bake(input, new MeshBakeSettings { Width = 64, Height = 64, Maps = maps, Padding = 0 });
            var padded = Bake(input, new MeshBakeSettings { Width = 64, Height = 64, Maps = maps, Padding = 8 });
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            {
                var before = bare[MeshMapKind.WorldNormal].CoverageAt(x, y);
                if (before != MeshTexelCoverage.Empty)
                {
                    Assert.That(padded[MeshMapKind.WorldNormal].CoverageAt(x, y), Is.EqualTo(before));
                    foreach (var kind in maps) for (int c = 0; c < 3; c++) Assert.That(padded[kind].RawValue(x, y, c), Is.EqualTo(bare[kind].RawValue(x, y, c)), "padding never writes over an island");
                    continue;
                }
                Assert.That(padded[MeshMapKind.WorldNormal].CoverageAt(x, y), Is.EqualTo(MeshTexelCoverage.Padding), x + "," + y);
                if (y < 3 || y > 60) continue;
                // すき間の左 3 列は左の島（+y）、右 3 列は右の島（−y）の値の写し
                if (x >= 29 && x <= 34) Assert.That(padded[MeshMapKind.WorldNormal].RawValue(x, y, 1), Is.EqualTo(x <= 31 ? 65535 : 0), "gap texel " + x + " copies the nearer island");
            }
            int mirrored = 0;
            for (int y = 3; y <= 60; y++) for (int x = 35; x <= 60; x++) { Assert.That(bare[MeshMapKind.WorldNormal].CoverageAt(x, y), Is.EqualTo(MeshTexelCoverage.Covered)); mirrored++; }
            Assert.That(mirrored, Is.EqualTo(26 * 58), "the mirrored island is fully covered");
            // 反転した島: u が大きいほど z が小さい（u 0.95 → z 0、u 0.55 → z 1）
            Assert.That(bare[MeshMapKind.Position].Value(36, 30, 2), Is.GreaterThan(0.9f)); Assert.That(bare[MeshMapKind.Position].Value(59, 30, 2), Is.LessThan(0.1f));
        }

        [Test] public void OverlappingUvsAreFlaggedAndTheLowerTriangleWins()
        {
            var single = new MeshBuilder().Quad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1), 0.1f, 0.1f, 0.9f, 0.9f);
            var one = MeshBaker.Bake(single.Build(), new MeshBakeSettings { Width = 64, Height = 64, Maps = new[] { MeshMapKind.Position } });
            Assert.That(one.Report.OverlapTexels, Is.Zero, "a quad's shared diagonal is not an overlap");
            // 同じ UV に、離れた 2 枚目の四角形（x が 2..3）
            var both = new MeshBuilder()
                .Quad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1), 0.1f, 0.1f, 0.9f, 0.9f)
                .Quad(new Vector3(2, 0, 0), new Vector3(2, 0, 1), new Vector3(3, 0, 0), new Vector3(3, 0, 1), 0.1f, 0.1f, 0.9f, 0.9f).Build();
            var result = MeshBaker.Bake(both, new MeshBakeSettings { Width = 64, Height = 64, Maps = new[] { MeshMapKind.Position }, Padding = 0 });
            long baked = result.Report.CoveredTexels + result.Report.OverlapTexels;
            Assert.That(baked, Is.EqualTo(one.Report.CoveredTexels));
            Assert.That(result.Report.OverlapTexels, Is.GreaterThan(baked * 9 / 10), "nearly every texel is shared");
            Assert.That(result.Report.Diagnostics.Any(d => d.Contains("overlapping")), Is.True);
            var map = result.Maps[0];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                if (Baked(map, x, y)) Assert.That(map.Value(x, y, 0), Is.LessThanOrEqualTo(1 / 3f + 1e-4f), "the first quad (x 0..1 of 0..3) wins");
            Assert.That(map.CountCoverage(MeshTexelCoverage.Overlap), Is.EqualTo(result.Report.OverlapTexels));
        }

        [Test] public void BakesAreDeterministicAcrossRunsAndThreadCounts()
        {
            var input = Step().Build();
            var settings = new MeshBakeSettings { Width = 96, Height = 96, AoSamples = 24, ThicknessSamples = 24 };
            byte[][] Run(int threads) => MeshBaker.Bake(input, settings, new MeshBakeBudget { MaxDegreeOfParallelism = threads }).Maps.Select(MeshMapBinary.Write).ToArray();
            var parallel = Run(0); var serial = Run(1); var again = Run(0);
            Assert.That(parallel.Length, Is.EqualTo(5));
            for (int i = 0; i < parallel.Length; i++)
            {
                Assert.That(serial[i], Is.EqualTo(parallel[i]), "one thread and all threads give the same bytes");
                Assert.That(again[i], Is.EqualTo(parallel[i]), "a second run gives the same bytes");
            }
        }

        [Test] public void CancelingOrRunningOutOfTimeReturnsNoMaps()
        {
            var input = Step().Build();
            var settings = new MeshBakeSettings { Width = 128, Height = 128 };
            var set = new MeshMapSet(); set.Put(Bake(input, new MeshBakeSettings { Width = 128, Height = 128, Maps = new[] { MeshMapKind.WorldNormal } }).Values);
            long revision = set.Revision; set.TryGet(MeshMapKind.WorldNormal, out var previous);
            var first = MeshBaker.Bake(input, settings, null, (f, phase) => false);
            Assert.That(first.Status, Is.EqualTo(MeshBakeStatus.Canceled)); Assert.That(first.Maps, Is.Empty);
            double reached = 0;
            var middle = MeshBaker.Bake(input, settings, null, (f, phase) => { reached = f; return f < 0.3; });
            Assert.That(middle.Status, Is.EqualTo(MeshBakeStatus.Canceled)); Assert.That(middle.Maps, Is.Empty); Assert.That(reached, Is.InRange(0.3, 0.99), "canceled part way");
            using (var token = new CancellationTokenSource())
            {
                token.Cancel();
                var canceled = MeshBaker.Bake(input, settings, null, null, token.Token);
                Assert.That(canceled.Status, Is.EqualTo(MeshBakeStatus.Canceled)); Assert.That(canceled.Maps, Is.Empty);
            }
            var late = MeshBaker.Bake(input, settings, new MeshBakeBudget { MaxSeconds = 1e-9 });
            Assert.That(late.Status, Is.EqualTo(MeshBakeStatus.TimedOut)); Assert.That(late.Maps, Is.Empty);
            // 呼び出し側の組は、完了した結果しか受け取らないので変わらない
            Assert.That(set.Revision, Is.EqualTo(revision)); Assert.That(set.TryGet(MeshMapKind.WorldNormal, out var still) && ReferenceEquals(still, previous), Is.True);
        }

        [Test] public void OversizeAndInvalidRequestsAreRefusedBeforeBaking()
        {
            var input = Step().Build();
            var big = new MeshBakeSettings { Width = 1024, Height = 1024 };
            Assert.That(MeshBaker.EstimateBytes(input, big), Is.GreaterThan(1L << 20));
            bool started = false;
            Assert.That(() => MeshBaker.Bake(input, big, new MeshBakeBudget { MaxBytes = 1L << 20 }, (f, p) => started = true),
                Throws.TypeOf<MeshBakeRefusedException>().With.Message.Contains("budget"));
            Assert.That(started, Is.False, "refused before anything was baked");
            void Invalid(Action<MeshBakeSettings> change)
            {
                var s = new MeshBakeSettings { Width = 64, Height = 64 }; change(s);
                Assert.That(() => MeshBaker.Bake(input, s), Throws.InstanceOf<ArgumentException>());
            }
            Invalid(s => s.Width = 0); Invalid(s => s.Height = MeshBakeSettings.MaxSize + 1); Invalid(s => s.Padding = MeshBakeSettings.MaxPadding + 1);
            Invalid(s => s.AoSamples = 0); Invalid(s => s.ThicknessSamples = MeshBakeSettings.MaxSamples + 1); Invalid(s => s.AoMaxDistance = double.NaN);
            Invalid(s => s.ThicknessMaxDistance = 0); Invalid(s => s.AoSpreadDegrees = 0.5); Invalid(s => s.CurvatureRadius = 0.0001); Invalid(s => s.TargetSlot = -2);
            Invalid(s => s.Maps = new MeshMapKind[0]); Invalid(s => s.Maps = new[] { MeshMapKind.Curvature, MeshMapKind.Curvature }); Invalid(s => s.Maps = new[] { (MeshMapKind)99 });
            Invalid(s => s.Occluders = (MeshOccluders)7); Invalid(s => s.AoFalloff = (MeshOcclusionFalloff)7);
            // 焼く三角形が無い、UV が 0〜1 の外（別のスロットなら焼かないので構わない）
            Assert.That(() => MeshBaker.Bake(input, new MeshBakeSettings { Width = 64, Height = 64, TargetSlot = 3 }), Throws.TypeOf<MeshBakeRefusedException>().With.Message.Contains("slot 3"));
            var outside = new MeshBuilder().Quad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1), 0.1f, 0.1f, 1.5f, 0.9f)
                .Quad(new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), 0.1f, 0.1f, 0.9f, 0.9f, slot: 1).Build();
            Assert.That(() => MeshBaker.Bake(outside, new MeshBakeSettings { Width = 64, Height = 64 }), Throws.TypeOf<MeshBakeRefusedException>().With.Message.Contains("outside 0–1"));
            Assert.That(MeshBaker.Bake(outside, new MeshBakeSettings { Width = 64, Height = 64, TargetSlot = 1, Maps = new[] { MeshMapKind.WorldNormal } }).Status, Is.EqualTo(MeshBakeStatus.Completed));
            // 入力の型
            var corners = Step().Corners;
            Assert.That(() => new MeshBakeInput(new float[8], null, new float[0], new int[0]), Throws.ArgumentException);
            Assert.That(() => new MeshBakeInput(new float[0], null, new float[0], new int[0]), Throws.ArgumentException);
            Assert.That(() => new MeshBakeInput(corners, null, new float[20], new int[4]), Throws.ArgumentException, "UV count");
            Assert.That(() => new MeshBakeInput(corners, null, new float[24], new int[3]), Throws.ArgumentException, "slot count");
            Assert.That(() => new MeshBakeInput(corners, new float[3], new float[24], new int[4]), Throws.ArgumentException, "normal count");
            Assert.That(() => new MeshBakeInput(corners, null, new float[24], new[] { 0, 0, -2, 0 }), Throws.InstanceOf<ArgumentOutOfRangeException>());
            var nan = (float[])corners.Clone(); nan[4] = float.NaN;
            Assert.That(() => new MeshBakeInput(nan, null, new float[24], new int[4]), Throws.ArgumentException);
            Assert.That(() => new MeshBakeInput(new float[9], null, new float[6], new int[1]), Throws.TypeOf<MeshBakeRefusedException>(), "no extent");
        }

        [Test] public void SavedMapsRoundTripExactlyAndDamagedOnesAreRejected()
        {
            var input = Step().Build();
            var maps = Bake(input, new MeshBakeSettings { Width = 64, Height = 48, AoSamples = 16, ThicknessSamples = 16, Padding = 3 });
            foreach (var map in maps.Values)
            {
                byte[] bytes = MeshMapBinary.Write(map);
                var back = MeshMapBinary.Read(bytes);
                var p = map.Provenance; var q = back.Provenance;
                Assert.That((q.Kind, q.EngineVersion, q.MeshHash, q.TopologyHash, q.UvChannel, q.Width, q.Height, q.TargetSlot, q.Padding, q.SettingsKey, q.Space, q.Pose, q.ConditionKey),
                    Is.EqualTo((p.Kind, p.EngineVersion, p.MeshHash, p.TopologyHash, p.UvChannel, p.Width, p.Height, p.TargetSlot, p.Padding, p.SettingsKey, p.Space, p.Pose, p.ConditionKey)));
                Assert.That(q.Source, Is.EqualTo(MeshBaker.Source)); Assert.That(q.Source, Is.EqualTo("Self"));
                for (int a = 0; a < 3; a++) { Assert.That(q.BoundsMin(a), Is.EqualTo(p.BoundsMin(a))); Assert.That(q.BoundsMax(a), Is.EqualTo(p.BoundsMax(a))); }
                for (int y = 0; y < 48; y++) for (int x = 0; x < 64; x++)
                {
                    Assert.That(back.CoverageAt(x, y), Is.EqualTo(map.CoverageAt(x, y)));
                    for (int c = 0; c < map.Channels; c++) Assert.That(back.RawValue(x, y, c), Is.EqualTo(map.RawValue(x, y, c)));
                }
                Assert.That(MeshMapBinary.Write(back), Is.EqualTo(bytes), "writing again gives the same bytes (no timestamps)");
            }
            byte[] good = MeshMapBinary.Write(maps[MeshMapKind.Position]);
            byte[] Damaged(Action<byte[]> change) { var copy = (byte[])good.Clone(); change(copy); return copy; }
            void Rejects(byte[] bytes, string contains = null)
            {
                var constraint = Throws.TypeOf<InvalidDataException>();
                Assert.That(() => MeshMapBinary.Read(bytes), contains == null ? constraint : constraint.With.Message.Contains(contains));
            }
            Rejects(Damaged(b => b[0] = (byte)'X'), "Not a YoluPainter mesh map");
            Rejects(good.Take(good.Length / 2).ToArray());
            Rejects(good.Concat(new byte[] { 0 }).ToArray(), "trailing");
            Rejects(Damaged(b => BitConverter.GetBytes(MeshMapBinary.FormatVersion + 1).CopyTo(b, 8)), "newer");
            const int widthAt = 8 + 4 + 4 + 4 + (4 + 64) + (4 + 64) + 4, heightAt = widthAt + 4, channelsAt = heightAt + 12;
            Assert.That(BitConverter.ToInt32(good, widthAt), Is.EqualTo(64)); Assert.That(BitConverter.ToInt32(good, heightAt), Is.EqualTo(48));
            Rejects(Damaged(b => BitConverter.GetBytes(100000).CopyTo(b, widthAt)), "out of range");
            Rejects(Damaged(b => BitConverter.GetBytes(47).CopyTo(b, heightAt)), "expands beyond");
            Rejects(Damaged(b => BitConverter.GetBytes(49).CopyTo(b, heightAt)), "shorter");
            Rejects(Damaged(b => BitConverter.GetBytes(1).CopyTo(b, channelsAt)), "channel");
            var pp = maps[MeshMapKind.Position].Provenance;
            int lengthAt = channelsAt + 4 + 4 + System.Text.Encoding.UTF8.GetByteCount(pp.SettingsKey) + 4 + pp.Space.Length + 4 + pp.Pose.Length + 4 + pp.Source.Length + 48;
            Assert.That(BitConverter.ToInt32(good, lengthAt), Is.EqualTo(good.Length - lengthAt - 4), "payload length field");
            Rejects(Damaged(b => BitConverter.GetBytes(int.MaxValue).CopyTo(b, lengthAt)), "truncated");
            Assert.That(MeshMapBinary.TryParseEntryName(MeshMapBinary.EntryName(MeshMapKind.AmbientOcclusion), out var kind) && kind == MeshMapKind.AmbientOcclusion, Is.True);
            Assert.That(MeshMapBinary.TryParseEntryName("meshmap-Wetness.bin", out _), Is.False); Assert.That(MeshMapBinary.TryParseEntryName("meshmap-2.bin", out _), Is.False);
            // .ylp の中: 追加のエントリーは manifest に載り、今の読み手（合成画像だけを読むインポーターを含む）はそのまま読める
            var document = new PaintDocument(64, 48); document.AddLayer("Paint 1");
            var files = new Dictionary<string, byte[]> { { YlpArchive.NativeName, DocumentBinary.Write(document) }, { MeshMapBinary.EntryName(MeshMapKind.Position), good } };
            byte[] archive = YlpArchive.Write(files);
            Assert.That(YlpArchive.Read(archive)[MeshMapBinary.EntryName(MeshMapKind.Position)], Is.EqualTo(good));
            Assert.That(YlpArchive.Read(archive, e => e == YlpArchive.NativeName).Keys, Is.EquivalentTo(new[] { YlpArchive.NativeName }));
        }

        [Test] public void StaleMapsAreReportedWhenTheModelOrTheSettingsChange()
        {
            var builder = Step(); var input = builder.Build();
            var settings = new MeshBakeSettings { Width = 64, Height = 64, Maps = new[] { MeshMapKind.AmbientOcclusion, MeshMapKind.WorldNormal }, AoSamples = 16 };
            var set = new MeshMapSet(); set.Put(MeshBaker.Bake(input, settings).Maps);
            MeshMapExpectation Expect(MeshBakeInput i, MeshBakeSettings s = null, int width = 64, int slot = 0) => new MeshMapExpectation { MeshHash = i?.Hash, TopologyHash = i?.TopologyHash, Width = width, Height = 64, TargetSlot = slot, Settings = s ?? settings };
            Assert.That(set.Check(MeshMapKind.AmbientOcclusion, Expect(input)).State, Is.EqualTo(MeshMapState.Current));
            Assert.That(set.TryGetUsable(MeshMapKind.WorldNormal, Expect(input), out var usable, out _), Is.True); Assert.That(usable, Is.Not.Null);
            Assert.That(set.Check(MeshMapKind.Curvature, Expect(input)).State, Is.EqualTo(MeshMapState.Missing));
            Assert.That(set.Check(MeshMapKind.AmbientOcclusion, Expect(null)).State, Is.EqualTo(MeshMapState.Unverified), "no model to check against");
            Assert.That(set.TryGetUsable(MeshMapKind.AmbientOcclusion, Expect(null), out _, out string why), Is.False); Assert.That(why, Does.Contain("unverified"));
            // 頂点を 1 つ動かす（ポーズや BlendShape と同じく UV と三角形はそのまま）
            var moved = builder.Corners; moved[4] += 0.01f;
            var posed = new MeshBakeInput(moved, null, builder.Uvs, new int[4]);
            var check = set.Check(MeshMapKind.AmbientOcclusion, Expect(posed));
            Assert.That(check.State, Is.EqualTo(MeshMapState.Stale)); Assert.That(string.Join(" ", check.Reasons), Does.Contain("shape changed"));
            Assert.That(set.TryGetUsable(MeshMapKind.AmbientOcclusion, Expect(posed), out var none, out _), Is.False); Assert.That(none, Is.Null, "a stale map is never handed out");
            // UV を変える
            var otherUv = new MeshBuilder()
                .Quad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1), 0.02f, 0.02f, 0.47f, 0.98f)
                .Quad(new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1), 0.52f, 0.02f, 0.98f, 0.98f).Build();
            Assert.That(string.Join(" ", set.Check(MeshMapKind.WorldNormal, Expect(otherUv)).Reasons), Does.Contain("UVs"));
            // 大きさ・スロット・設定
            Assert.That(set.Check(MeshMapKind.WorldNormal, Expect(input, width: 128)).State, Is.EqualTo(MeshMapState.Stale));
            Assert.That(set.Check(MeshMapKind.WorldNormal, Expect(input, slot: 1)).State, Is.EqualTo(MeshMapState.Stale));
            var more = settings.Clone(); more.AoSamples = 32;
            Assert.That(set.Check(MeshMapKind.AmbientOcclusion, Expect(input, more)).State, Is.EqualTo(MeshMapState.Stale), "AO settings changed");
            Assert.That(set.Check(MeshMapKind.WorldNormal, Expect(input, more)).State, Is.EqualTo(MeshMapState.Current), "the normal map does not depend on AO settings");
            var padded = settings.Clone(); padded.Padding = 2;
            Assert.That(set.Check(MeshMapKind.WorldNormal, Expect(input, padded)).State, Is.EqualTo(MeshMapState.Stale));
            // 条件の鍵: 同じ条件なら同じ鍵、違えば違う鍵（違う条件の結果をキャッシュとして共有しない）
            Assert.That(set.TryGetExact(MeshMapKind.AmbientOcclusion, MeshBaker.ConditionKey(input, settings, MeshMapKind.AmbientOcclusion), out _), Is.True);
            Assert.That(set.TryGetExact(MeshMapKind.AmbientOcclusion, MeshBaker.ConditionKey(input, more, MeshMapKind.AmbientOcclusion), out _), Is.False);
            Assert.That(set.TryGetExact(MeshMapKind.AmbientOcclusion, MeshBaker.ConditionKey(posed, settings, MeshMapKind.AmbientOcclusion), out _), Is.False);
            Assert.That(MeshBaker.ConditionKey(input, settings, MeshMapKind.WorldNormal), Is.EqualTo(MeshBaker.ConditionKey(input, more, MeshMapKind.WorldNormal)));
            // 保存した設定を欄に戻す
            var restored = new MeshBakeSettings(); set.TryGet(MeshMapKind.AmbientOcclusion, out var ao);
            restored.ApplyKindKey(MeshMapKind.AmbientOcclusion, ao.Provenance.SettingsKey, ao.Provenance.Padding);
            Assert.That(restored.KindKey(MeshMapKind.AmbientOcclusion), Is.EqualTo(ao.Provenance.SettingsKey));
        }

        [Test] public void ReconstructedNormalsSmoothCurvesAndKeepHardEdges()
        {
            var box = Box(-Vector3.one, Vector3.one);
            var normals = MeshBakeInput.ReconstructNormals(box.Corners); var faces = box.Normals;
            Assert.That(normals.Length, Is.EqualTo(faces.Length));
            // 箱の 90° の角はスムージング角度（60°）より大きいので、各面の法線のまま
            for (int i = 0; i < normals.Length; i++) Assert.That(normals[i], Is.EqualTo(faces[i]).Within(1e-6f));
            // 粗い球: 中心からの向きに近い（面の法線よりずっと）
            var sphere = Sphere(16, 24, 1f);
            var smooth = MeshBakeInput.ReconstructNormals(sphere.corners);
            double worst = 0, sum = 0, faceWorst = 0;
            for (int i = 0; i < smooth.Length; i += 3)
            {
                var p = new Vector3(sphere.corners[i], sphere.corners[i + 1], sphere.corners[i + 2]).normalized;
                double angle = Vector3.Angle(p, new Vector3(smooth[i], smooth[i + 1], smooth[i + 2])); worst = Math.Max(worst, angle); sum += angle;
                int t = i / 9 * 9;
                var face = Vector3.Cross(new Vector3(sphere.corners[t + 3] - sphere.corners[t], sphere.corners[t + 4] - sphere.corners[t + 1], sphere.corners[t + 5] - sphere.corners[t + 2]),
                    new Vector3(sphere.corners[t + 6] - sphere.corners[t], sphere.corners[t + 7] - sphere.corners[t + 1], sphere.corners[t + 8] - sphere.corners[t + 2]));
                faceWorst = Math.Max(faceWorst, Vector3.Angle(p, face));
            }
            Assert.That(worst, Is.LessThan(3.0), "degrees between the rebuilt normal and the true sphere normal");
            Assert.That(sum / (smooth.Length / 3), Is.LessThan(1.0), "mean degrees");
            Assert.That(faceWorst, Is.GreaterThan(5.0), "the flat face normals are much further off");
        }

        /// <summary>緯度経度の球（外向き）。UV は 0.01〜0.99 に収める。</summary>
        static (float[] corners, float[] uvs, int[] slots) Sphere(int rings, int segments, float radius)
        {
            var corners = new List<float>(); var uvs = new List<float>();
            Vector3 P(int i, int j) { double th = Math.PI * i / rings, ph = 2 * Math.PI * j / segments; return new Vector3((float)(radius * Math.Sin(th) * Math.Cos(ph)), (float)(radius * Math.Cos(th)), (float)(radius * Math.Sin(th) * Math.Sin(ph))); }
            void V(int i, int j) { var p = P(i, j); corners.Add(p.x); corners.Add(p.y); corners.Add(p.z); uvs.Add(0.01f + 0.98f * j / segments); uvs.Add(0.01f + 0.98f * (1 - (float)i / rings)); }
            for (int i = 0; i < rings; i++) for (int j = 0; j < segments; j++)
            {
                if (i > 0) { V(i, j); V(i, j + 1); V(i + 1, j); }
                if (i < rings - 1) { V(i + 1, j); V(i, j + 1); V(i + 1, j + 1); }
            }
            return (corners.ToArray(), uvs.ToArray(), new int[corners.Count / 9]);
        }

        [Test] public void PreviewSnapshotsBecomeBakeInputsWithoutTouchingTheSource()
        {
            MeshBakeInput demo, again;
            using (var preview = new IsolatedModelPreview()) { Assert.That(preview.LoadDemoMesh().CanPaint, Is.True); demo = TexturePaintWindow.BuildMeshBakeInput(preview.Geometry); }
            using (var preview = new IsolatedModelPreview()) { preview.LoadDemoMesh(); again = TexturePaintWindow.BuildMeshBakeInput(preview.Geometry); }
            Assert.That(demo.TriangleCount, Is.EqualTo(12)); Assert.That(again.Hash, Is.EqualTo(demo.Hash), "the same snapshot gives the same fingerprint");
            Assert.That(demo.NormalSource, Does.Contain("reconstructed"));
            var maps = Bake(demo, new MeshBakeSettings { Width = 128, Height = 128, Maps = new[] { MeshMapKind.Curvature, MeshMapKind.AmbientOcclusion }, AoSamples = 16 });
            var curvature = maps[MeshMapKind.Curvature]; float highest = 0;
            for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
                if (Baked(curvature, x, y)) { highest = Math.Max(highest, curvature.Value(x, y)); Assert.That(maps[MeshMapKind.AmbientOcclusion].RawValue(x, y), Is.EqualTo(65535)); }
            Assert.That(highest, Is.GreaterThan(0.7f), "the cube's edges read convex");

            // シーンのモデル（組み込みのキューブ。6 面とも UV が同じ 0〜1 で重なる）
            var source = new GameObject("Mesh map source model");
            var mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            source.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(Shader.Find("Hidden/YoluPainter/PreviewSurface")) { mainTexture = Texture2D.grayTexture };
            var renderer = source.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            var vertices = mesh.vertices; var uv = mesh.uv; var texture = material.mainTexture;
            int meshDirty = EditorUtility.GetDirtyCount(mesh), materialDirty = EditorUtility.GetDirtyCount(material), objectDirty = EditorUtility.GetDirtyCount(source);
            try
            {
                using (var preview = new IsolatedModelPreview())
                {
                    Assert.That(preview.Load(source).CanPaint, Is.True);
                    var result = MeshBaker.Bake(TexturePaintWindow.BuildMeshBakeInput(preview.Geometry), new MeshBakeSettings { Width = 64, Height = 64, AoSamples = 8, ThicknessSamples = 8 });
                    Assert.That(result.Status, Is.EqualTo(MeshBakeStatus.Completed));
                    Assert.That(result.Report.OverlapTexels, Is.GreaterThan(0), "the built-in cube's faces share UVs");
                }
                Assert.That(mesh.vertices, Is.EqualTo(vertices)); Assert.That(mesh.uv, Is.EqualTo(uv));
                Assert.That(renderer.sharedMaterial, Is.SameAs(material)); Assert.That(material.mainTexture, Is.SameAs(texture));
                Assert.That(EditorUtility.GetDirtyCount(mesh), Is.EqualTo(meshDirty)); Assert.That(EditorUtility.GetDirtyCount(material), Is.EqualTo(materialDirty));
                Assert.That(EditorUtility.GetDirtyCount(source), Is.EqualTo(objectDirty));
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(material); }
        }

        [Test] public void SavingLeavesMeshMapsOutOnlyWhenTheYlpBudgetWouldBeExceeded()
        {
            Assert.That(TexturePaintWindow.MeshMapSaveProblem(100L << 20, 5, new[] { 10L << 20, 20L << 20 }), Is.Null);
            Assert.That(TexturePaintWindow.MeshMapSaveProblem(700L << 20, 5, new[] { 40L << 20, 40L << 20 }), Does.Contain("budget"), "over the total");
            Assert.That(TexturePaintWindow.MeshMapSaveProblem(1L << 20, 5, new[] { YlpArchive.MaxEntryBytes + 1 }), Does.Contain("entry"));
            Assert.That(TexturePaintWindow.MeshMapSaveProblem(1L << 20, YlpArchive.MaxEntries - 1, new[] { 1L, 1L }), Does.Contain("entries"));
        }

        /// <summary>緯度経度の球（外向き）。UV は 0.01〜0.99 に収める。</summary>
        internal static (float[] corners, float[] uvs, int[] slots) TestSphere(int rings, int segments, float radius) => Sphere(rings, segments, radius);
    }

    /// <summary>計測（明示して走らせる: --filter 'Yozolab.YoluPainter.Tests.MeshMapTimings'）: デモのキューブと約 2 万三角形の球を、
    /// 全マップ・既定の設定で 1024² と 2048² に 3 回ずつ焼き、中央値をログに出す。</summary>
    [Explicit("Timing measurement; run explicitly")]
    public sealed class MeshMapTimings
    {
        [SetUp] public void TolerateBrokenShaderCompiler() { EditorShaderCompiler.TolerateErrorLogsIfBroken(); }

        [Test] public void MeasureBakeTimes()
        {
            MeshBakeInput cube;
            using (var preview = new IsolatedModelPreview()) { preview.LoadDemoMesh(); cube = TexturePaintWindow.BuildMeshBakeInput(preview.Geometry); }
            var s = MeshMapTests.TestSphere(100, 100, 0.5f);
            var sphere = new MeshBakeInput(s.corners, MeshBakeInput.ReconstructNormals(s.corners), s.uvs, s.slots, 0, "reconstructed-crease-60");
            var lines = new List<string> { "CPU threads " + Environment.ProcessorCount + ", sphere triangles " + sphere.TriangleCount };
            foreach (var (name, input) in new[] { ("demo cube", cube), ("sphere", sphere) })
                foreach (int size in new[] { 1024, 2048 })
                {
                    var times = new List<double>(); MeshBakeReport report = null;
                    for (int run = 0; run < 3; run++)
                    {
                        var result = MeshBaker.Bake(input, new MeshBakeSettings { Width = size, Height = size }, new MeshBakeBudget { MaxBytes = 2048L << 20 });
                        Assert.That(result.Status, Is.EqualTo(MeshBakeStatus.Completed));
                        times.Add(result.Report.TotalSeconds); report = result.Report;
                    }
                    times.Sort();
                    lines.Add(name + " " + size + "²: median " + times[1].ToString("F2") + " s (runs " + string.Join(", ", times.Select(t => t.ToString("F2"))) + "), prepare " + report.PrepareSeconds.ToString("F2")
                        + " s, rays " + report.Rays + " (" + (report.Rays / report.RasterSeconds / 1e6).ToString("F1") + " M/s), padding " + report.PaddingSeconds.ToString("F2") + " s, estimate " + (report.EstimatedBytes >> 20) + " MiB");
                }
            Debug.Log("Mesh-map bake timings:\n" + string.Join("\n", lines));
            TestContext.WriteLine(string.Join("\n", lines));
        }
    }
}
