using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>Generator のある層とマスクの表示の合成: GPU の合成（許容は GpuTests と同じ 1）も、CPU を選んだ表示（バイト一致）も、CPU の正本と
    /// 同じになること。メッシュマップを焼き直す・消す・別の口に替えると、合成器は変更の記録（TryGetChangedTiles）だけで Generator の層を
    /// 描き直し、全面を合成し直したものと同じになること（古いマップのタイルを使わない）。マップを読むのは Core（CPU）なので、GPU は
    /// Generator を通したタイルを載せるだけ。</summary>
    [Category("GPU")]
    public sealed class GpuGeneratorTests
    {
        const int W = 300, H = 260, T = 32;

        static PaintDocument Scene(out PaintLayer fill, out PaintLayer paint)
        {
            var d = new PaintDocument(W, H, T);
            var bg = d.AddLayer("bg"); var b = bg.GetChannel(PaintChannel.Color);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) b.SetPixel(x, y, new Rgba32((byte)(x * 255 / (W - 1)), (byte)(y * 255 / (H - 1)), 128, 255));
            paint = d.AddLayer("paint"); var p = paint.GetChannel(PaintChannel.Color);
            for (int y = 90; y < 150; y++) for (int x = 50; x < 210; x++) p.SetPixel(x, y, new Rgba32(250, (byte)(x % 200), (byte)(y * 2 % 256), (byte)(x < 120 ? 255 : 180)));
            fill = d.AddFillLayer("wear", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(30, 200, 90, 255) } });
            d.AddLayerMask(fill.Id); d.ClearHistory();
            return d;
        }
        static BakedMeshMap Curvature(double phase) => TestMeshMaps.Make(MeshMapKind.Curvature, W, H, (x, y, _) => .5 + .5 * System.Math.Sin(x * .05 + phase) * System.Math.Cos(y * .04),
            (x, y) => x < 6 ? MeshTexelCoverage.Empty : MeshTexelCoverage.Covered);
        static BakedMeshMap Position() => TestMeshMaps.Make(MeshMapKind.Position, W, H, (x, y, c) => c == 0 ? x / (double)(W - 1) : c == 1 ? y / (double)(H - 1) : .3);

        [TestCase(true)]
        [TestCase(false)]
        public void GpuCompositingMatchesTheCpuReferenceThroughMapChanges(bool copyTexture)
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            var c = new TileGpuCompositor(allowCopyTexture: copyTexture);
            try
            {
                var d = Scene(out var fill, out var paint); var inputs = new TestGeneratorInputs().Put(Curvature(0)).Put(Position()); d.GeneratorInputs = inputs;
                void Check(string step)
                {
                    c.Update(d, PaintChannel.Color);
                    Assert.That(c.Backend, Does.StartWith("CPU source brush / GPU"), c.Backend);
                    GpuTests.AssertMatches(d.Composite(PaintChannel.Color), GpuTests.Read(c.Texture), step);
                    Assert.That(c.LastCpuTileCount, Is.Zero, step);
                }
                Check("plain");
                var wear = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.EdgeWear))); Check("mask generator");
                d.AddFilter(paint.Id, FilterTarget.Content, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.PositionGradient).WithBlend(GeneratorBlend.Multiply)), new[] { PaintChannel.Color }); Check("content generator");
                var before = GpuTests.Read(c.Texture);
                inputs.Put(Curvature(1.3)); Check("rebaked curvature");
                Assert.That(GpuTests.Read(c.Texture), Is.Not.EqualTo(before), "the rebake is shown");
                inputs.Refuse(MeshMapKind.Curvature, "Curvature is stale: test."); Check("stale curvature: passes through");
                inputs.Put(Curvature(2.1)); Check("baked again");
                d.SetFilterSettings(fill.Id, wear.Id, wear.Settings.WithGenerator(wear.Settings.Generator.WithLevels(.1, .5, 1).WithInvert(true)), coalesce: true); Check("slider");
                d.Undo(); Check("undo");
                d.GeneratorInputs = new TestGeneratorInputs().Put(Curvature(.4)); Check("other inputs");
            }
            finally { c.Dispose(); }
        }

        [Test] public void TheCpuDisplayPathShowsExactlyTheReferenceThroughMapChanges()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var d = Scene(out var fill, out var paint); var inputs = new TestGeneratorInputs().Put(Curvature(0)).Put(Position()); d.GeneratorInputs = inputs;
            using (var c = new TileGpuCompositor(CompositorBackend.Cpu))
            {
                void Check(string step)
                {
                    c.Update(d, PaintChannel.Color);
                    var shown = c.Texture is RenderTexture ? GpuTests.Read(c.Texture) : GpuTests.ReadCpu(c.Texture);
                    CpuCompositingTests.AssertSameBytes(d.Composite(PaintChannel.Color), shown, step);
                }
                Check("plain");
                d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.EdgeWear))); Check("mask generator");
                d.AddFilter(paint.Id, FilterTarget.Content, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.PositionGradient)), new[] { PaintChannel.Color }); Check("content generator");
                var before = d.Composite(PaintChannel.Color);
                inputs.Put(Curvature(1.3)); Check("rebaked");
                Assert.That(d.Composite(PaintChannel.Color), Is.Not.EqualTo(before), "the rebake changes what is shown");
                inputs.Refuse(MeshMapKind.Curvature, "missing"); Check("missing");
                inputs.Put(Curvature(2.1)); Check("back");
            }
        }
    }
}
