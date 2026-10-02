using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>高ポリからのベイクと新しい種類: 傾いた面・ずれた面の接空間の法線と高さ（式どおり、Unity の読み方で戻すと高ポリの法線）、
    /// 高ポリの無い所の不透明度と平らな法線、名前での対応づけ、ID（スロット・メッシュ・頂点カラー・UV アイランド）、内角のベントノーマル、
    /// アンチエイリアス（辺をまたぐテクセルの平均・ID の多数決・部分的な覆い）、決定性、予算と型の拒否、保存の往復（版 1 の読み込み）、
    /// 高ポリが替わったときの古さ、プレビューの属性（法線・接線の w・頂点カラー・名前）と元のアセットを変えないこと。</summary>
    public sealed class MeshMapReferenceTests
    {
        [SetUp] public void TolerateBrokenShaderCompiler() { EditorShaderCompiler.TolerateErrorLogsIfBroken(); }

        /// <summary>三角形の並び。四角形は (p0,p1,p2) と (p2,p1,p3)、表は (p1−p0)×(p2−p0)。UV は p0=(u0,v0)、p1=(u0,v1)、p2=(u1,v0)、p3=(u1,v1)。
        /// 頂点法線は面の法線。接線を渡せば全頂点に同じ接線。</summary>
        sealed class Builder
        {
            readonly List<float> corners = new List<float>(), normals = new List<float>(), uvs = new List<float>(), tangents = new List<float>(), colors = new List<float>();
            readonly List<int> slots = new List<int>(), renderers = new List<int>();
            public readonly List<string> Names = new List<string>();
            bool anyTangent, anyColor;
            public Builder Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u0, float v0, float u1, float v1, int slot = 0, string name = "Mesh", Color? color = null, Vector4? tangent = null)
            {
                var n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                int renderer = Names.IndexOf(name); if (renderer < 0) { Names.Add(name); renderer = Names.Count - 1; }
                var c = color ?? Color.white; var t = tangent ?? Vector4.zero; anyTangent |= tangent.HasValue; anyColor |= color.HasValue;
                void V(Vector3 p, float u, float v)
                {
                    corners.Add(p.x); corners.Add(p.y); corners.Add(p.z); normals.Add(n.x); normals.Add(n.y); normals.Add(n.z); uvs.Add(u); uvs.Add(v);
                    tangents.Add(t.x); tangents.Add(t.y); tangents.Add(t.z); tangents.Add(t.w); colors.Add(c.r); colors.Add(c.g); colors.Add(c.b); colors.Add(c.a);
                }
                V(p0, u0, v0); V(p1, u0, v1); V(p2, u1, v0); slots.Add(slot); renderers.Add(renderer);
                V(p2, u1, v0); V(p1, u0, v1); V(p3, u1, v1); slots.Add(slot); renderers.Add(renderer);
                return this;
            }
            public MeshBakeInput Build(bool tangentsToo = true) => new MeshBakeInput(corners.ToArray(), normals.ToArray(), uvs.ToArray(), slots.ToArray(), 0, "authored",
                anyTangent && tangentsToo ? tangents.ToArray() : null, anyColor ? colors.ToArray() : null, renderers.ToArray(), Names);
        }

        /// <summary>床（y = 0、x・z が 0..1、u → x、v → z）。Unity の従法線 cross(n, t)·w が +z になる接線は (1, 0, 0, −1)。</summary>
        static Builder Floor(float u0 = 0.05f, float u1 = 0.95f, Vector4? tangent = null, string name = "Mesh", float x0 = 0, float x1 = 1, int slot = 0, Builder b = null)
            => (b ?? new Builder()).Quad(new Vector3(x0, 0, 0), new Vector3(x0, 0, 1), new Vector3(x1, 0, 0), new Vector3(x1, 0, 1), u0, 0.05f, u1, 0.95f, slot, name, null, tangent ?? new Vector4(1, 0, 0, -1));
        /// <summary>高ポリの面 y = h + k·x（上向き）。x・z は −0.1..1.1 か指定の範囲。</summary>
        static Builder Plane(float h, float k, string name = "High", float x0 = -0.1f, float x1 = 1.1f, Builder b = null)
            => (b ?? new Builder()).Quad(new Vector3(x0, h + k * x0, -0.1f), new Vector3(x0, h + k * x0, 1.1f), new Vector3(x1, h + k * x1, -0.1f), new Vector3(x1, h + k * x1, 1.1f), 0, 0, 0, 0, 0, name);
        static double Along(int texel, int size, float u0, float u1, double a0, double a1) => a0 + ((texel + 0.5) / size - u0) / (u1 - u0) * (a1 - a0);
        static Dictionary<MeshMapKind, BakedMeshMap> Bake(MeshBakeInput low, MeshBakeSettings settings, MeshBakeInput high = null, MeshBakeBudget budget = null)
        {
            var result = MeshBaker.Bake(low, settings, budget, null, default, high);
            Assert.That(result.Status, Is.EqualTo(MeshBakeStatus.Completed));
            return result.Maps.ToDictionary(m => m.Kind);
        }
        static Vector3 Decode(BakedMeshMap map, int x, int y) => new Vector3(BakedMeshMap.Signed(map.Value(x, y, 0)), BakedMeshMap.Signed(map.Value(x, y, 1)), BakedMeshMap.Signed(map.Value(x, y, 2)));
        static bool Baked(BakedMeshMap map, int x, int y) { var c = map.CoverageAt(x, y); return c == MeshTexelCoverage.Covered || c == MeshTexelCoverage.Overlap; }

        [TestCase(true)]
        [TestCase(false)]
        public void TiltedHighPolyGivesTheAnalyticTangentNormalAndHeight(bool lowTangents)
        {
            const float h = 0.01f, k = 0.02f; const int size = 64;
            var low = Floor().Build(lowTangents); var high = Plane(h, k).Build();
            var settings = new MeshBakeSettings { Width = size, Height = size, Padding = 0, ReferenceFrontal = 0.05, ReferenceRear = 0.05,
                Maps = new[] { MeshMapKind.TangentNormal, MeshMapKind.Height, MeshMapKind.Opacity, MeshMapKind.WorldNormal, MeshMapKind.AmbientOcclusion }, AoSamples = 8 };
            var result = MeshBaker.Bake(low, settings, null, null, default, high);
            Assert.That(result.Status, Is.EqualTo(MeshBakeStatus.Completed)); Assert.That(result.Report.MissedSamples, Is.Zero);
            if (!lowTangents) Assert.That(result.Report.Diagnostics.Any(d => d.Contains("derived from the UVs")), Is.True);
            var maps = result.Maps.ToDictionary(m => m.Kind);
            var expectedNormal = new Vector3(-k, 1, 0).normalized; double range = 0.05 * low.Diagonal;
            int checkedTexels = 0;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                if (!Baked(maps[MeshMapKind.TangentNormal], x, y)) continue;
                // 低ポリの接空間 T = +x、B = +z、N = +y で、高ポリの法線 (−k, 1, 0)/|…| は (−sinα, 0, cosα)
                var tn = Decode(maps[MeshMapKind.TangentNormal], x, y);
                Assert.That(tn.x, Is.EqualTo(expectedNormal.x).Within(1e-4)); Assert.That(tn.y, Is.EqualTo(0).Within(1e-4)); Assert.That(tn.z, Is.EqualTo(expectedNormal.y).Within(1e-4));
                // Unity の読み方 normalize(T·x + B·y + N·z) で戻すと高ポリの法線
                var world = (new Vector3(1, 0, 0) * tn.x + new Vector3(0, 0, 1) * tn.y + new Vector3(0, 1, 0) * tn.z).normalized;
                Assert.That(Vector3.Angle(world, expectedNormal), Is.LessThan(0.05f));
                Assert.That(Vector3.Angle(Decode(maps[MeshMapKind.WorldNormal], x, y), expectedNormal), Is.LessThan(0.05f), "the world normal comes from the high poly");
                double px = Along(x, size, 0.05f, 0.95f, 0, 1);
                Assert.That(maps[MeshMapKind.Height].Value(x, y), Is.EqualTo(0.5 + 0.5 * (h + k * px) / range).Within(2e-4), "height = signed distance / max ray distance");
                Assert.That(maps[MeshMapKind.Opacity].RawValue(x, y), Is.EqualTo(65535));
                Assert.That(maps[MeshMapKind.AmbientOcclusion].RawValue(x, y), Is.EqualTo(65535), "nothing above the high plane");
                checkedTexels++;
            }
            Assert.That(checkedTexels, Is.GreaterThan(3000));
        }

        [Test] public void HighPolyBelowReadsLowAndMissingHighPolyIsTransparentAndFlat()
        {
            const int size = 64; var low = Floor().Build();
            var below = Bake(low, new MeshBakeSettings { Width = size, Height = size, Padding = 0, ReferenceFrontal = 0.05, ReferenceRear = 0.05, Maps = new[] { MeshMapKind.Height, MeshMapKind.TangentNormal } }, Plane(-0.02f, 0).Build());
            Assert.That(below[MeshMapKind.Height].Value(32, 32), Is.EqualTo(0.5 - 0.5 * 0.02 / (0.05 * low.Diagonal)).Within(2e-4), "a lower surface reads below 0.5");
            Assert.That(Decode(below[MeshMapKind.TangentNormal], 32, 32).z, Is.EqualTo(1).Within(1e-4));
            // 高ポリが x < 0.5 にしか無い
            var settings = new MeshBakeSettings { Width = size, Height = size, Padding = 0, ReferenceFrontal = 0.05, ReferenceRear = 0.05, Maps = new[] { MeshMapKind.Opacity, MeshMapKind.Height, MeshMapKind.TangentNormal } };
            var result = MeshBaker.Bake(low, settings, null, null, default, Plane(0.01f, 0.03f, x0: -0.1f, x1: 0.5f).Build());
            Assert.That(result.Report.MissedSamples, Is.GreaterThan(0)); Assert.That(result.Report.Diagnostics.Any(d => d.Contains("found no high-poly surface")), Is.True);
            var maps = result.Maps.ToDictionary(m => m.Kind);
            for (int x = 4; x < 60; x++)
            {
                double px = Along(x, size, 0.05f, 0.95f, 0, 1);
                if (Math.Abs(px - 0.5) < 0.03) continue;
                Assert.That(maps[MeshMapKind.Opacity].RawValue(x, 32), Is.EqualTo(px < 0.5 ? 65535 : 0), "opacity at x " + px);
                if (px > 0.5)
                {
                    Assert.That(maps[MeshMapKind.Height].RawValue(x, 32), Is.EqualTo(32768), "no hit: height 0.5");
                    Assert.That(Decode(maps[MeshMapKind.TangentNormal], x, 32).z, Is.EqualTo(1).Within(1e-4), "no hit: flat");
                }
            }
            // 高ポリが無ければ不透明度は 1、法線は平ら、高さ 0.5
            var selfBake = MeshBaker.Bake(low, settings);
            var self = selfBake.Maps.ToDictionary(m => m.Kind);
            Assert.That(self[MeshMapKind.Opacity].RawValue(32, 32), Is.EqualTo(65535)); Assert.That(self[MeshMapKind.Height].RawValue(32, 32), Is.EqualTo(32768));
            Assert.That(selfBake.Report.Diagnostics.Any(d => d.Contains("No high-poly reference")), Is.True);
        }

        [Test] public void MatchByNameProjectsEachLowPartOnlyOntoItsHighPart()
        {
            const int size = 64;
            var lowBuilder = Floor(0.05f, 0.30f, name: "Left_low", x0: 0, x1: 0.33f);
            Floor(0.35f, 0.60f, name: "Right_low", x0: 0.33f, x1: 0.66f, b: lowBuilder);
            Floor(0.65f, 0.95f, name: "Lonely_low", x0: 0.66f, x1: 1, b: lowBuilder);
            var low = lowBuilder.Build();
            var highBuilder = Plane(0.01f, 0, "Left_high"); Plane(0.03f, 0, "Right", b: highBuilder); // 接尾辞の無い高ポリも対応する
            var high = highBuilder.Build();
            var settings = new MeshBakeSettings { Width = size, Height = size, Padding = 0, ReferenceFrontal = 0.05, ReferenceRear = 0.05, Maps = new[] { MeshMapKind.Height, MeshMapKind.Opacity } };
            double range = 0.05 * low.Diagonal;
            int left = 8, right = 30, lonely = 50; // テクセルの列
            var all = Bake(low, settings, high);
            foreach (int x in new[] { left, right, lonely }) Assert.That(all[MeshMapKind.Height].Value(x, 32), Is.EqualTo(0.5 + 0.5 * 0.03 / range).Within(2e-4), "without matching every part hits the upper plane first");
            settings.ReferenceMatchByName = true;
            var matched = Bake(low, settings, high);
            Assert.That(matched[MeshMapKind.Height].Value(left, 32), Is.EqualTo(0.5 + 0.5 * 0.01 / range).Within(2e-4), "Left_low only sees Left_high");
            Assert.That(matched[MeshMapKind.Height].Value(right, 32), Is.EqualTo(0.5 + 0.5 * 0.03 / range).Within(2e-4), "Right_low sees Right");
            Assert.That(matched[MeshMapKind.Opacity].RawValue(lonely, 32), Is.Zero, "a low part without a high part gets nothing");
            Assert.That(MeshBaker.BaseName("Arm_LOW"), Is.EqualTo("Arm")); Assert.That(MeshBaker.BaseName(" Arm_high "), Is.EqualTo("Arm")); Assert.That(MeshBaker.BaseName("Arm"), Is.EqualTo("Arm"));
        }

        [Test] public void IdMapsColourEachSlotMeshVertexColourAndUvIsland()
        {
            const int size = 32;
            var b = new Builder()
                .Quad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1), 0.05f, 0.05f, 0.30f, 0.95f, 0, "A", Color.red)
                .Quad(new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(2, 0, 0), new Vector3(2, 0, 1), 0.35f, 0.05f, 0.60f, 0.95f, 1, "A", Color.blue)
                .Quad(new Vector3(2, 0, 0), new Vector3(2, 0, 1), new Vector3(3, 0, 0), new Vector3(3, 0, 1), 0.65f, 0.05f, 0.95f, 0.95f, 1, "B", Color.blue);
            var input = b.Build();
            int x0 = 5, x1 = 15, x2 = 25; // 3 つの島の中
            (int, int, int) Id(MeshIdSource source, int x)
            {
                var map = Bake(input, new MeshBakeSettings { Width = size, Height = size, TargetSlot = -1, Padding = 0, Maps = new[] { MeshMapKind.Id }, IdSource = source })[MeshMapKind.Id];
                for (int y = 2; y < 30; y++) Assert.That((map.RawValue(x, y, 0), map.RawValue(x, y, 1), map.RawValue(x, y, 2)), Is.EqualTo((map.RawValue(x, 16, 0), map.RawValue(x, 16, 1), map.RawValue(x, 16, 2))), "one flat colour per quad");
                return (map.RawValue(x, 16, 0) / 257, map.RawValue(x, 16, 1) / 257, map.RawValue(x, 16, 2) / 257);
            }
            Assert.That(Id(MeshIdSource.MaterialSlot, x0), Is.Not.EqualTo(Id(MeshIdSource.MaterialSlot, x1)));
            Assert.That(Id(MeshIdSource.MaterialSlot, x1), Is.EqualTo(Id(MeshIdSource.MaterialSlot, x2)), "same slot, same colour");
            Assert.That(Id(MeshIdSource.Mesh, x0), Is.EqualTo(Id(MeshIdSource.Mesh, x1)), "same renderer");
            Assert.That(Id(MeshIdSource.Mesh, x1), Is.Not.EqualTo(Id(MeshIdSource.Mesh, x2)));
            Assert.That(Id(MeshIdSource.VertexColor, x0), Is.EqualTo((255, 0, 0))); Assert.That(Id(MeshIdSource.VertexColor, x2), Is.EqualTo((0, 0, 255)));
            var islands = new[] { Id(MeshIdSource.UvIsland, x0), Id(MeshIdSource.UvIsland, x1), Id(MeshIdSource.UvIsland, x2) };
            Assert.That(islands.Distinct().Count(), Is.EqualTo(3), "three separate UV islands");
            Assert.That(Id(MeshIdSource.MaterialSlot, x0), Is.EqualTo(Id(MeshIdSource.MaterialSlot, x0)), "deterministic colour");
        }

        [Test] public void BentNormalLeansAwayFromTheWall()
        {
            var step = new Builder()
                .Quad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1), 0.02f, 0.02f, 0.48f, 0.98f)
                .Quad(new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1), 0.52f, 0.02f, 0.98f, 0.98f).Build();
            const int size = 128;
            var map = Bake(step, new MeshBakeSettings { Width = size, Height = size, Padding = 0, Maps = new[] { MeshMapKind.BentNormal }, AoSamples = 128 })[MeshMapKind.BentNormal];
            int Texel(double distance) => (int)Math.Round(0.02 * size + distance * 0.46 * size - 0.5);
            var near = Decode(map, Texel(0.01), 64); var far = Decode(map, Texel(0.4), 64);
            Assert.That(near.x, Is.GreaterThan(0.3f), "near the wall the open directions lean away from it (+x)");
            Assert.That(far.y, Is.GreaterThan(0.99f), "in the open the bent normal is the normal");
            Assert.That(near.magnitude, Is.EqualTo(1).Within(1e-3));
        }

        /// <summary>屋根: 左右の面が UV で接し、その辺がテクセル 32 の真ん中を通る。</summary>
        static MeshBakeInput Roof(float left = 6.6f / 64)
        {
            const float edge = 32.5f / 64;
            return new Builder()
                .Quad(new Vector3(-1, 0, 0), new Vector3(-1, 0, 1), new Vector3(0, 1, 0), new Vector3(0, 1, 1), left, 0.1f, edge, 0.9f, 0, "Left")
                .Quad(new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1), edge, 0.1f, 0.9f, 0.9f, 1, "Right").Build();
        }

        [Test] public void AntialiasingAveragesAcrossEdgesAndVotesIds()
        {
            var roof = Roof();
            Dictionary<MeshMapKind, BakedMeshMap> Run(int samples) => Bake(roof, new MeshBakeSettings { Width = 64, Height = 64, TargetSlot = -1, Padding = 0, Antialiasing = samples, Maps = new[] { MeshMapKind.WorldNormal, MeshMapKind.Id } });
            var one = Run(1); var four = Run(4);
            Assert.That(Decode(one[MeshMapKind.WorldNormal], 32, 32).x, Is.EqualTo(-0.7071f).Within(1e-3), "1×1: the texel centre lies on the ridge; the lower triangle wins");
            var mixed = Decode(four[MeshMapKind.WorldNormal], 32, 32);
            Assert.That(mixed.x, Is.EqualTo(0).Within(2e-3)); Assert.That(mixed.y, Is.EqualTo(1).Within(2e-3), "4×4: half left, half right, averaged and renormalised");
            Assert.That(Decode(four[MeshMapKind.WorldNormal], 30, 32).x, Is.EqualTo(-0.7071f).Within(1e-3), "inside one face nothing changes");
            var idOne = one[MeshMapKind.Id]; var idFour = four[MeshMapKind.Id];
            Assert.That(idFour.RawValue(32, 32, 0), Is.EqualTo(idOne.RawValue(30, 32, 0)), "an 8–8 tie keeps the first subsample's id (the left face), never a blend");
            Assert.That(idFour.RawValue(34, 32, 0), Is.EqualTo(idOne.RawValue(34, 32, 0)));
            // 部分的な覆い: 左の端 u = 6.6/64 はテクセル 6 の中心より右、サブサンプル 6.875 より左
            Assert.That(one[MeshMapKind.WorldNormal].CoverageAt(6, 32), Is.EqualTo(MeshTexelCoverage.Empty));
            Assert.That(four[MeshMapKind.WorldNormal].CoverageAt(6, 32), Is.EqualTo(MeshTexelCoverage.Covered), "any covered subsample covers the texel");
            Assert.That(Decode(four[MeshMapKind.WorldNormal], 6, 32).x, Is.EqualTo(-0.7071f).Within(1e-3), "the value is the mean of the covered subsamples only");
        }

        [Test] public void ReferenceBakesAreDeterministicAndRefuseBadRequests()
        {
            var low = Floor().Build(); var high = Plane(0.01f, 0.02f).Build();
            var settings = new MeshBakeSettings { Width = 48, Height = 48, Antialiasing = 2, ReferenceFrontal = 0.05, ReferenceRear = 0.05, AoSamples = 8, ThicknessSamples = 8, Maps = MeshBakeSettings.AllKinds.ToArray() };
            byte[][] Run(int threads) => MeshBaker.Bake(low, settings, new MeshBakeBudget { MaxDegreeOfParallelism = threads }, null, default, high).Maps.Select(MeshMapBinary.Write).ToArray();
            var a = Run(0); var b = Run(1);
            Assert.That(a.Length, Is.EqualTo(10));
            for (int i = 0; i < a.Length; i++) Assert.That(b[i], Is.EqualTo(a[i]), "one thread and all threads give the same bytes (" + i + ")");
            Assert.That(MeshBaker.EstimateBytes(low, settings, null, high), Is.GreaterThan(MeshBaker.EstimateBytes(low, settings)), "the high poly is counted");
            bool started = false;
            Assert.That(() => MeshBaker.Bake(low, settings, new MeshBakeBudget { MaxBytes = MeshBaker.EstimateBytes(low, settings) }, (f, p) => started = true, default, high),
                Throws.TypeOf<MeshBakeRefusedException>().With.Message.Contains("high poly"));
            Assert.That(started, Is.False);
            var canceled = MeshBaker.Bake(low, settings, null, (f, p) => f < 0.3, default, high);
            Assert.That(canceled.Status, Is.EqualTo(MeshBakeStatus.Canceled)); Assert.That(canceled.Maps, Is.Empty);
            void Invalid(Action<MeshBakeSettings> change) { var s = settings.Clone(); change(s); Assert.That(() => MeshBaker.Bake(low, s, null, null, default, high), Throws.InstanceOf<ArgumentException>()); }
            Invalid(s => s.Antialiasing = 0); Invalid(s => s.Antialiasing = 5); Invalid(s => s.ReferenceFrontal = 0); Invalid(s => s.ReferenceRear = double.NaN); Invalid(s => s.IdSource = (MeshIdSource)9);
            Invalid(s => s.Maps = new[] { (MeshMapKind)10 });
            var corners = new float[9 * 2];
            Assert.That(() => new MeshBakeInput(new float[18] { 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 1 }, null, new float[12], new int[2], tangents: new float[23]), Throws.ArgumentException, "tangent count");
            Assert.That(() => new MeshBakeInput(new float[18] { 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 1 }, null, new float[12], new int[2], colors: new float[12]), Throws.ArgumentException, "colour count");
            Assert.That(() => new MeshBakeInput(new float[18] { 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 1 }, null, new float[12], new int[2], renderers: new[] { 0, 2 }, rendererNames: new[] { "a" }), Throws.InstanceOf<ArgumentOutOfRangeException>(), "renderer index");
            GC.KeepAlive(corners);
        }

        /// <summary>レイをまとめて処理するふり: 準備で断る、または処理の途中で失敗する。</summary>
        sealed class BrokenTracer : IMeshBakeRayTracer
        {
            public string Refuse; public int Prepared, Traced, Released;
            public string Name => "Broken";
            public string Prepare(MeshBakeRayScene scene) { Prepared++; Assert.That(scene.SceneCount, Is.GreaterThanOrEqualTo(1)); Assert.That(scene.Bytes, Is.GreaterThan(0)); return Refuse; }
            public void Trace(MeshBakeRayJob[] jobs, int count, MeshBakeRayResult[] results) { Traced++; throw new InvalidOperationException("device lost (test)"); }
            public void Release() { Released++; }
        }

        [Test] public void ATracerThatRefusesOrFailsFallsBackToTheCpuResult()
        {
            var low = Floor().Build(); var high = Plane(0.01f, 0.02f).Build();
            var settings = new MeshBakeSettings { Width = 32, Height = 32, ReferenceFrontal = 0.05, ReferenceRear = 0.05, AoSamples = 8, ThicknessSamples = 8, Maps = new[] { MeshMapKind.AmbientOcclusion, MeshMapKind.Thickness, MeshMapKind.BentNormal } };
            var cpu = MeshBaker.Bake(low, settings, null, null, default, high).Maps.Select(MeshMapBinary.Write).ToArray();
            var refusing = new BrokenTracer { Refuse = "no compute (test)" };
            var a = MeshBaker.Bake(low, settings, null, null, default, high, refusing);
            Assert.That(a.Report.RayBackend, Is.EqualTo("CPU")); Assert.That(a.Report.Diagnostics.Any(d => d.Contains("no compute (test)")), Is.True);
            Assert.That(a.Maps.Select(MeshMapBinary.Write).ToArray(), Is.EqualTo(cpu)); Assert.That(refusing.Released, Is.GreaterThanOrEqualTo(1));
            var failing = new BrokenTracer();
            var b = MeshBaker.Bake(low, settings, null, null, default, high, failing);
            Assert.That(failing.Traced, Is.EqualTo(1)); Assert.That(failing.Released, Is.GreaterThanOrEqualTo(1));
            Assert.That(b.Status, Is.EqualTo(MeshBakeStatus.Completed)); Assert.That(b.Report.Diagnostics.Any(d => d.Contains("device lost (test)") && d.Contains("redone on the CPU")), Is.True);
            Assert.That(b.Maps.Select(MeshMapBinary.Write).ToArray(), Is.EqualTo(cpu), "the CPU redo is the reference result");
            // レイを使わない種類だけなら、レイの処理系は使わない
            var unused = new BrokenTracer();
            MeshBaker.Bake(low, new MeshBakeSettings { Width = 16, Height = 16, Maps = new[] { MeshMapKind.Height } }, null, null, default, high, unused);
            Assert.That(unused.Prepared, Is.Zero);
        }

        [Test] public void NewKindsRoundTripAndVersionOneFilesStillRead()
        {
            var low = Floor().Build(); var high = Plane(0.01f, 0.02f).Build();
            var settings = new MeshBakeSettings { Width = 32, Height = 32, Antialiasing = 2, ReferenceFrontal = 0.05, ReferenceRear = 0.05, IdSource = MeshIdSource.Mesh, AoSamples = 8, Maps = new[] { MeshMapKind.TangentNormal, MeshMapKind.Height, MeshMapKind.Id, MeshMapKind.BentNormal, MeshMapKind.Opacity } };
            foreach (var map in MeshBaker.Bake(low, settings, null, null, default, high).Maps)
            {
                var bytes = MeshMapBinary.Write(map); var back = MeshMapBinary.Read(bytes);
                Assert.That(back.Provenance.ConditionKey, Is.EqualTo(map.Provenance.ConditionKey)); Assert.That(back.Provenance.Antialiasing, Is.EqualTo(2));
                Assert.That(back.Provenance.Source, Does.StartWith("Reference:" + high.Hash));
                Assert.That(back.Channels, Is.EqualTo(BakedMeshMap.ChannelCount(map.Kind)));
                Assert.That(MeshMapBinary.Write(back), Is.EqualTo(bytes));
                var restored = new MeshBakeSettings(); restored.ApplyKindKey(map.Kind, back.Provenance.SettingsKey, back.Provenance.Padding); restored.ApplySourceKey(back.Provenance.Source);
                Assert.That(restored.KindKey(map.Kind), Is.EqualTo(back.Provenance.SettingsKey)); Assert.That(restored.SourceKey(high.Hash), Is.EqualTo(back.Provenance.Source));
            }
            // 版 1: アンチエイリアスの欄が無い（読むと 1）
            var old = MeshBaker.Bake(low, new MeshBakeSettings { Width = 16, Height = 16, Maps = new[] { MeshMapKind.Position } }).Maps[0];
            var v2 = MeshMapBinary.Write(old);
            const int antialiasingAt = 8 + 4 + 4 + 4 + (4 + 64) + (4 + 64) + 4 + 4 + 4 + 4 + 4;
            Assert.That(BitConverter.ToInt32(v2, antialiasingAt), Is.EqualTo(1));
            var v1 = v2.Take(antialiasingAt).Concat(v2.Skip(antialiasingAt + 4)).ToArray(); BitConverter.GetBytes(1).CopyTo(v1, 8);
            var read = MeshMapBinary.Read(v1);
            Assert.That(read.Provenance.Antialiasing, Is.EqualTo(1)); Assert.That(read.RawValue(8, 8, 0), Is.EqualTo(old.RawValue(8, 8, 0)));
            var bad = (byte[])v2.Clone(); BitConverter.GetBytes(9).CopyTo(bad, antialiasingAt);
            Assert.That(() => MeshMapBinary.Read(bad), Throws.TypeOf<InvalidDataException>().With.Message.Contains("antialiasing"));
        }

        [Test] public void MapsGoStaleWhenTheHighPolyOrItsProjectionChanges()
        {
            var low = Floor().Build(); var high = Plane(0.01f, 0.02f).Build(); var other = Plane(0.02f, 0.02f).Build();
            var settings = new MeshBakeSettings { Width = 16, Height = 16, ReferenceFrontal = 0.05, ReferenceRear = 0.05, Maps = new[] { MeshMapKind.Height, MeshMapKind.Position } };
            var set = new MeshMapSet(); set.Put(MeshBaker.Bake(low, settings, null, null, default, high).Maps);
            MeshMapExpectation Expect(string reference, MeshBakeSettings s = null) => new MeshMapExpectation { MeshHash = low.Hash, TopologyHash = low.TopologyHash, ReferenceHash = reference, Width = 16, Height = 16, Settings = s ?? settings };
            Assert.That(set.Check(MeshMapKind.Height, Expect(high.Hash)).State, Is.EqualTo(MeshMapState.Current));
            var changed = set.Check(MeshMapKind.Height, Expect(other.Hash));
            Assert.That(changed.State, Is.EqualTo(MeshMapState.Stale)); Assert.That(string.Join(" ", changed.Reasons), Does.Contain("high-poly reference"));
            Assert.That(string.Join(" ", set.Check(MeshMapKind.Position, Expect(null)).Reasons), Does.Contain("none is chosen"), "every kind depends on the reference");
            var rays = settings.Clone(); rays.ReferenceRear = 0.06;
            Assert.That(set.Check(MeshMapKind.Height, Expect(high.Hash, rays)).State, Is.EqualTo(MeshMapState.Stale), "ray distances changed");
            var aa = settings.Clone(); aa.Antialiasing = 2;
            Assert.That(string.Join(" ", set.Check(MeshMapKind.Height, Expect(high.Hash, aa)).Reasons), Does.Contain("antialiasing"));
            Assert.That(set.TryGetExact(MeshMapKind.Height, MeshBaker.ConditionKey(low, settings, MeshMapKind.Height, high), out _), Is.True);
            Assert.That(set.TryGetExact(MeshMapKind.Height, MeshBaker.ConditionKey(low, settings, MeshMapKind.Height, other), out _), Is.False);
            Assert.That(set.TryGetExact(MeshMapKind.Height, MeshBaker.ConditionKey(low, settings, MeshMapKind.Height), out _), Is.False);
            // 自己ベイクのマップに高ポリを選ぶと古い
            var self = new MeshMapSet(); self.Put(MeshBaker.Bake(low, settings).Maps);
            Assert.That(string.Join(" ", self.Check(MeshMapKind.Height, Expect(high.Hash)).Reasons), Does.Contain("without a high-poly reference"));
        }

        [Test] public void PreviewAttributesFeedAuthoredNormalsTangentsColoursAndNames()
        {
            var mesh = new Mesh
            {
                vertices = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1) },
                uv = new[] { new Vector2(0.1f, 0.1f), new Vector2(0.1f, 0.9f), new Vector2(0.9f, 0.1f), new Vector2(0.9f, 0.9f) },
                normals = new[] { new Vector3(0, 1, 0), new Vector3(0, 1, 0), new Vector3(0.6f, 0.8f, 0), new Vector3(0, 1, 0) },
                tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 4).ToArray(),
                colors = new[] { Color.red, Color.green, Color.blue, Color.white },
                triangles = new[] { 0, 1, 2, 2, 1, 3 },
            };
            var source = new GameObject("Attribute source");
            var material = new Material(Shader.Find("Hidden/YoluPainter/PreviewSurface"));
            source.AddComponent<MeshFilter>().sharedMesh = mesh; source.AddComponent<MeshRenderer>().sharedMaterial = material;
            var vertices = mesh.vertices; int dirty = EditorUtility.GetDirtyCount(mesh);
            try
            {
                foreach (bool mirrored in new[] { false, true })
                {
                    source.transform.localScale = new Vector3(mirrored ? -1 : 1, 1, 1);
                    using (var preview = new IsolatedModelPreview())
                    {
                        Assert.That(preview.Load(source).CanPaint, Is.True);
                        var a = preview.Attributes; Assert.That(a.TriangleCount, Is.EqualTo(preview.Geometry.TriangleCount));
                        Assert.That(a.RendererNames, Is.EqualTo(new[] { "Attribute source" }));
                        Assert.That(a.Colors, Is.Not.Null); Assert.That(a.Tangents, Is.Not.Null);
                        // 頂点 2 の法線（0.6, 0.8, 0）が、反転したレンダラーでは x が反転して入る。接線の w も反転（Unity の読み方と同じ）
                        var colorsAtNormal = new List<(Vector3, Color, float)>();
                        for (int c = 0; c < a.Normals.Length / 3; c++)
                            colorsAtNormal.Add((new Vector3(a.Normals[c * 3], a.Normals[c * 3 + 1], a.Normals[c * 3 + 2]), new Color(a.Colors[c * 4], a.Colors[c * 4 + 1], a.Colors[c * 4 + 2]), a.Tangents[c * 4 + 3]));
                        Assert.That(colorsAtNormal.Where(e => e.Item2 == Color.blue).All(e => Vector3.Distance(e.Item1, new Vector3(mirrored ? -0.6f : 0.6f, 0.8f, 0)) < 1e-5f), Is.True, "authored normal, transformed");
                        Assert.That(colorsAtNormal.All(e => e.Item3 == (mirrored ? 1 : -1)), Is.True, "tangent w is flipped for a mirrored renderer");
                        var input = TexturePaintWindow.BuildMeshBakeInput(preview.Geometry, a);
                        Assert.That(input.NormalSource, Is.EqualTo("authored")); Assert.That(input.HasTangents && input.HasColors, Is.True);
                        var id = MeshBaker.Bake(input, new MeshBakeSettings { Width = 32, Height = 32, Padding = 0, Maps = new[] { MeshMapKind.Id }, IdSource = MeshIdSource.VertexColor }).Maps[0];
                        Assert.That(id.RawValue(3, 4, 0), Is.GreaterThan(60000), "the red corner is red");
                        Assert.That(input.Hash, Is.Not.EqualTo(TexturePaintWindow.BuildMeshBakeInput(preview.Geometry).Hash), "authored attributes change the fingerprint");
                    }
                }
                Assert.That(mesh.vertices, Is.EqualTo(vertices)); Assert.That(EditorUtility.GetDirtyCount(mesh), Is.EqualTo(dirty));
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(material); Object.DestroyImmediate(mesh); }
        }

        [Test] public void HighPolyLoadsInTheLowModelsSpaceWithoutTouchingIt()
        {
            var low = new GameObject("Low root"); low.transform.position = new Vector3(5, 0, 0);
            var high = new GameObject("High part"); high.transform.position = new Vector3(5, 1, 0);
            var mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var material = new Material(Shader.Find("Hidden/YoluPainter/PreviewSurface"));
            high.AddComponent<MeshFilter>().sharedMesh = mesh; high.AddComponent<MeshRenderer>().sharedMaterial = material;
            int dirty = EditorUtility.GetDirtyCount(high), components = high.GetComponents<Component>().Length;
            try
            {
                using (var preview = new IsolatedModelPreview())
                {
                    var report = preview.Load(high, new PreviewLoadOptions { MaxTriangles = TexturePaintWindow.HighPolyMaxTriangles, MaxVerticesPerMesh = TexturePaintWindow.HighPolyMaxVertices, Origin = low.transform.position });
                    Assert.That(report.Success, Is.True);
                    Assert.That(preview.Bounds.center.x, Is.EqualTo(0).Within(1e-5)); Assert.That(preview.Bounds.center.y, Is.EqualTo(1).Within(1e-5), "placed relative to the low root, not its own");
                    var input = TexturePaintWindow.BuildMeshBakeInput(preview.Geometry, preview.Attributes);
                    Assert.That(input.TriangleCount, Is.EqualTo(12));
                }
                Assert.That(EditorUtility.GetDirtyCount(high), Is.EqualTo(dirty)); Assert.That(high.GetComponents<Component>().Length, Is.EqualTo(components));
                Assert.That(high.transform.position, Is.EqualTo(new Vector3(5, 1, 0)));
            }
            finally { Object.DestroyImmediate(low); Object.DestroyImmediate(high); Object.DestroyImmediate(material); }
        }
    }
}
