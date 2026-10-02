using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>mesh map のベイクの GPU（計算シェーダー）の経路と CPU の基準の一致: AO・厚み・ベントノーマルは float の精度の差だけ
    /// （許容: AO と厚みは 1 テクセルで 0.02 以内・平均 5e-4 以内、ベントノーマルは 2° 以内）、レイを使わない種類はバイト一致、高ポリと
    /// アンチエイリアスでも同じ、GPU どうしの決定性、予算を超えると CPU で焼くこと、取消で GPU のバッファを残さないこと。
    /// GPU が無い・シェーダーが壊れた・計算シェーダーの無いエディタではスキップ（batch-gl で回す）。</summary>
    [Category("GPU")]
    public sealed class GpuMeshBakeTests
    {
        [SetUp] public void RequireGpu()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics). Run with test-daemon.sh start --batch-gl.");
            if (EditorShaderCompiler.IsBroken) Assert.Ignore("Built-in shaders fail to compile in this Editor (devcontainer GUI mode). Use --batch-gl.");
            string why = GpuMeshBakeRayTracer.Unavailable();
            if (why != null) Assert.Ignore("GPU mesh-map baking is unavailable here: " + why);
        }

        static MeshBakeInput Step()
        {
            var corners = new List<float>(); var normals = new List<float>(); var uvs = new List<float>();
            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u0, float u1)
            {
                var n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                void V(Vector3 p, float u, float v) { corners.AddRange(new[] { p.x, p.y, p.z }); normals.AddRange(new[] { n.x, n.y, n.z }); uvs.Add(u); uvs.Add(v); }
                V(p0, u0, 0.02f); V(p1, u0, 0.98f); V(p2, u1, 0.02f); V(p2, u1, 0.02f); V(p1, u0, 0.98f); V(p3, u1, 0.98f);
            }
            Quad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 1), 0.02f, 0.31f);
            Quad(new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1), 0.35f, 0.64f);
            Quad(new Vector3(0, 0.3f, 0), new Vector3(1, 0.3f, 0), new Vector3(0, 0.3f, 1), new Vector3(1, 0.3f, 1), 0.68f, 0.98f); // 天井（下向き）
            return new MeshBakeInput(corners.ToArray(), normals.ToArray(), uvs.ToArray(), new int[corners.Count / 9]);
        }

        static void AssertParity(BakeResultPair pair, string what)
        {
            Assert.That(pair.Gpu.Status, Is.EqualTo(MeshBakeStatus.Completed)); Assert.That(pair.Gpu.Report.RayBackend, Does.StartWith("GPU"), string.Join(" ", pair.Gpu.Report.Diagnostics));
            Assert.That(pair.Gpu.Report.Rays, Is.EqualTo(pair.Cpu.Report.Rays), "the same rays were cast");
            // 1 回の Dispatch と読み戻しは、GPU のタイムアウト（2 秒）から十分に離れている（最初の 1 回はシェーダーの準備を含みうる）
            Assert.That(pair.Dispatches, Is.GreaterThan(0)); Assert.That(pair.MaxDispatchMilliseconds, Is.LessThan(300), what + ": longest dispatch");
            TestContext.WriteLine(what + ": " + pair.Dispatches + " dispatches, longest " + pair.MaxDispatchMilliseconds.ToString("F1") + " ms");
            var cpu = pair.Cpu.Maps.ToDictionary(m => m.Kind); var gpu = pair.Gpu.Maps.ToDictionary(m => m.Kind);
            foreach (var kind in cpu.Keys)
            {
                var a = cpu[kind]; var b = gpu[kind];
                double max = 0, sum = 0, angle = 0; long n = 0;
                for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++)
                {
                    Assert.That(b.CoverageAt(x, y), Is.EqualTo(a.CoverageAt(x, y)));
                    if (kind == MeshMapKind.BentNormal)
                    {
                        if (a.CoverageAt(x, y) == MeshTexelCoverage.Empty) continue;
                        var va = new Vector3(BakedMeshMap.Signed(a.Value(x, y, 0)), BakedMeshMap.Signed(a.Value(x, y, 1)), BakedMeshMap.Signed(a.Value(x, y, 2)));
                        var vb = new Vector3(BakedMeshMap.Signed(b.Value(x, y, 0)), BakedMeshMap.Signed(b.Value(x, y, 1)), BakedMeshMap.Signed(b.Value(x, y, 2)));
                        angle = Math.Max(angle, Vector3.Angle(va, vb));
                        continue;
                    }
                    for (int c = 0; c < a.Channels; c++) { double d = Math.Abs(a.Value(x, y, c) - b.Value(x, y, c)); max = Math.Max(max, d); sum += d; n++; }
                }
                if (kind == MeshMapKind.BentNormal) { Assert.That(angle, Is.LessThan(2.0), what + " bent normal (degrees)"); continue; }
                bool rays = kind == MeshMapKind.AmbientOcclusion || kind == MeshMapKind.Thickness;
                if (!rays) { Assert.That(MeshMapBinary.Write(b), Is.EqualTo(MeshMapBinary.Write(a)), what + " " + kind + " does not use the GPU and must be identical"); continue; }
                Assert.That(max, Is.LessThanOrEqualTo(0.02), what + " " + kind + " max difference");
                Assert.That(sum / n, Is.LessThanOrEqualTo(5e-4), what + " " + kind + " mean difference");
                TestContext.WriteLine(what + " " + kind + ": max " + max.ToString("F5") + ", mean " + (sum / n).ToString("E2"));
            }
        }
        struct BakeResultPair { public MeshBakeResult Cpu, Gpu; public double MaxDispatchMilliseconds; public int Dispatches; }
        static BakeResultPair Both(MeshBakeInput input, MeshBakeSettings settings, MeshBakeInput reference = null)
        {
            var cpu = MeshBaker.Bake(input, settings, null, null, default, reference);
            var tracer = new GpuMeshBakeRayTracer(256L << 20);
            var gpu = MeshBaker.Bake(input, settings, null, null, default, reference, tracer);
            Assert.That(tracer.UploadedBytes, Is.Zero, "the GPU buffers are released after the bake");
            return new BakeResultPair { Cpu = cpu, Gpu = gpu, MaxDispatchMilliseconds = tracer.MaxDispatchMilliseconds, Dispatches = tracer.Dispatches };
        }

        [Test] public void GpuRaysMatchTheCpuReference()
        {
            var settings = new MeshBakeSettings { Width = 96, Height = 96, Maps = new[] { MeshMapKind.AmbientOcclusion, MeshMapKind.Thickness, MeshMapKind.BentNormal, MeshMapKind.Curvature, MeshMapKind.WorldNormal },
                AoMaxDistance = 0.5, ThicknessMaxDistance = 0.5, AoSamples = 48, ThicknessSamples = 48 };
            AssertParity(Both(Step(), settings), "linear falloff");
            settings.AoFalloff = MeshOcclusionFalloff.None; settings.AoIgnoreBackfaces = true;
            AssertParity(Both(Step(), settings), "no falloff, back faces ignored");
        }

        [Test] public void GpuRaysMatchWithAHighPolyAndAntialiasing()
        {
            var low = Step();
            // 高ポリ: 低ポリと同じ形を少し外へ（床・壁・天井）
            var reference = Step();
            var settings = new MeshBakeSettings { Width = 64, Height = 64, Antialiasing = 2, ReferenceFrontal = 0.02, ReferenceRear = 0.02, AoMaxDistance = 0.5, AoSamples = 16, ThicknessSamples = 16,
                Maps = new[] { MeshMapKind.AmbientOcclusion, MeshMapKind.Thickness, MeshMapKind.BentNormal, MeshMapKind.Height, MeshMapKind.TangentNormal } };
            var pair = Both(low, settings, reference);
            Assert.That(pair.Gpu.Report.MissedSamples, Is.EqualTo(pair.Cpu.Report.MissedSamples));
            AssertParity(pair, "high poly, 2×2");
        }

        [Test] public void GpuBakesAreDeterministic()
        {
            var settings = new MeshBakeSettings { Width = 64, Height = 64, Maps = new[] { MeshMapKind.AmbientOcclusion, MeshMapKind.Thickness }, AoMaxDistance = 0.5, AoSamples = 32 };
            byte[][] Run() => MeshBaker.Bake(Step(), settings, null, null, default, null, new GpuMeshBakeRayTracer(256L << 20)).Maps.Select(MeshMapBinary.Write).ToArray();
            var a = Run(); var b = Run();
            for (int i = 0; i < a.Length; i++) Assert.That(b[i], Is.EqualTo(a[i]));
        }

        [Test] public void OverTheGpuBudgetTheCpuBakesAndSaysSo()
        {
            var settings = new MeshBakeSettings { Width = 48, Height = 48, Maps = new[] { MeshMapKind.AmbientOcclusion }, AoSamples = 8 };
            var cpu = MeshBaker.Bake(Step(), settings);
            var small = MeshBaker.Bake(Step(), settings, null, null, default, null, new GpuMeshBakeRayTracer(1024));
            Assert.That(small.Report.RayBackend, Is.EqualTo("CPU"));
            Assert.That(small.Report.Diagnostics.Any(d => d.Contains("GPU path is not available") && d.Contains("budget")), Is.True);
            Assert.That(MeshMapBinary.Write(small.Maps[0]), Is.EqualTo(MeshMapBinary.Write(cpu.Maps[0])), "the CPU result, byte for byte");
        }

        [Test] public void CancelingAGpuBakeReleasesItsBuffers()
        {
            var tracer = new GpuMeshBakeRayTracer(256L << 20);
            var settings = new MeshBakeSettings { Width = 256, Height = 256, Maps = new[] { MeshMapKind.AmbientOcclusion }, AoSamples = 8 };
            var result = MeshBaker.Bake(Step(), settings, null, (f, p) => f < 0.2, default, null, tracer);
            Assert.That(result.Status, Is.EqualTo(MeshBakeStatus.Canceled)); Assert.That(result.Maps, Is.Empty);
            Assert.That(tracer.UploadedBytes, Is.Zero);
        }
    }
}
