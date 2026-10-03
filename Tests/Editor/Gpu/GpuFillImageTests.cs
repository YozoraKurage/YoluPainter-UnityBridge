using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>画像を投影する塗りつぶしの層の表示の合成: GPU の合成（許容は GpuTests と同じ 1）も、CPU を選んだ表示（バイト一致）も CPU の正本と
    /// 同じ。投影の変更・Undo・リソースの差し替えと色空間の変更・メッシュマップの焼き直しは、変更の記録（TryGetChangedTiles）だけで描き直される。
    /// 画素を作るのは Core（CPU）で、GPU は評価したタイルを載せるだけ。表示の合成を時間で区切ると、1 回の Update で層の全部を評価しない。</summary>
    [Category("GPU")]
    public sealed class GpuFillImageTests
    {
        const int W = 300, H = 260, T = 32;

        static ImageContent Picture(int w, int h, int seed)
        {
            var rgba = new byte[w * h * 4]; var rnd = new System.Random(seed);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = (byte)(x * 255 / (w - 1)); rgba[o + 1] = (byte)(y * 255 / (h - 1)); rgba[o + 2] = (byte)rnd.Next(256); rgba[o + 3] = 255; // 不透明（GPU の許容 1 は重なりの無いアルファで成り立つ）
            }
            return ImageContent.FromPixels(rgba, w, h);
        }
        static BakedMeshMap Position(double phase = 0) => TestMeshMaps.Make(MeshMapKind.Position, W, H, (x, y, c) => c == 0 ? x / (double)(W - 1) : c == 1 ? y / (double)(H - 1) : .5 + .3 * Math.Sin(x * .03 + phase),
            (x, y) => x < 6 ? MeshTexelCoverage.Empty : MeshTexelCoverage.Covered);
        static BakedMeshMap Normal() => TestMeshMaps.Make(MeshMapKind.WorldNormal, W, H, (x, y, c) => c == 0 ? .5 + .4 * Math.Sin(x * .02) : c == 1 ? .5 + .4 * Math.Cos(y * .03) : .8);

        static PaintDocument Scene(out PaintLayer fill, out ProjectResources resources, out ImageResource image)
        {
            var d = new PaintDocument(W, H, T);
            var bg = d.AddLayer("bg"); var b = bg.GetChannel(PaintChannel.Color);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) b.SetPixel(x, y, new Rgba32((byte)(x * 255 / (W - 1)), 90, (byte)(y * 255 / (H - 1)), 255));
            resources = new ProjectResources(); image = resources.Add("Picture", Picture(64, 48, 1), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            d.ImageResources = resources;
            fill = d.AddFillLayer("projected", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(30, 200, 90, 255) } });
            d.SetFillImage(fill.Id, PaintChannel.Color, image.Id);
            d.SetLayerOpacity(fill.Id, .8);
            d.ClearHistory();
            return d;
        }

        void Changes(PaintDocument d, PaintLayer fill, ProjectResources resources, ImageResource image, TestGeneratorInputs inputs, Action<string> check)
        {
            check("UV");
            d.SetFillProjection(fill.Id, fill.Projection.WithTiles(3, 2).WithRotation(20)); check("tiled and turned");
            d.SetFillProjection(fill.Id, fill.Projection.WithMode(FillProjectionMode.Triplanar).WithPlacement(new ShapeVolume(GeneratorShape.Box, .5, .5, .5, 0, 0, 0, .5, .5, .5, 0)));
            check("triplanar");
            d.SetFillProjection(fill.Id, fill.Projection.WithBlendWidth(.9), coalesce: true); d.SetFillProjection(fill.Id, fill.Projection.WithBlendWidth(.2), coalesce: true); check("slider");
            d.Undo(); check("undo");
            inputs.Put(Position(1.2)); check("rebaked position");
            inputs.Refuse(MeshMapKind.WorldNormal, "stale: test"); check("stale normal: the fill value");
            inputs.Put(Normal()); check("baked again");
            resources.SetColorSpace(image.Id, ResourceColorSpace.Linear); check("read as data");
            resources.ReplaceContent(image.Id, Picture(40, 40, 7), null); check("updated from the source");
            d.SetFillImage(fill.Id, PaintChannel.Color, null); check("image removed");
            d.Undo(); check("image back");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void GpuCompositingMatchesTheCpuReferenceThroughEveryChange(bool copyTexture)
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            var c = new TileGpuCompositor(allowCopyTexture: copyTexture);
            try
            {
                var d = Scene(out var fill, out var resources, out var image); var inputs = new TestGeneratorInputs().Put(Position()).Put(Normal()); d.GeneratorInputs = inputs;
                Changes(d, fill, resources, image, inputs, step =>
                {
                    c.Update(d, PaintChannel.Color);
                    Assert.That(c.Backend, Does.StartWith("CPU source brush / GPU"), c.Backend);
                    GpuTests.AssertMatches(d.Composite(PaintChannel.Color), GpuTests.Read(c.Texture), step);
                    Assert.That(c.LastCpuTileCount, Is.Zero, step);
                });
            }
            finally { c.Dispose(); }
        }

        [Test] public void TheCpuDisplayPathShowsExactlyTheReferenceThroughEveryChange()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var d = Scene(out var fill, out var resources, out var image); var inputs = new TestGeneratorInputs().Put(Position()).Put(Normal()); d.GeneratorInputs = inputs;
            using (var c = new TileGpuCompositor(CompositorBackend.Cpu))
                Changes(d, fill, resources, image, inputs, step =>
                {
                    c.Update(d, PaintChannel.Color);
                    var shown = c.Texture is RenderTexture ? GpuTests.Read(c.Texture) : GpuTests.ReadCpu(c.Texture);
                    CpuCompositingTests.AssertSameBytes(d.Composite(PaintChannel.Color), shown, step);
                });
        }

        /// <summary>1 回の Update の時間の予算が小さければ、投影する層の全部は評価しない（一緒に評価するのは作業者の数の 2 倍のブロックまで。
        /// 試験は作業者 4 人）。続けて流し終えた表示は、一度に合成した表示とバイト単位で同じ。</summary>
        [Test] public void ATimeBudgetedUpdateDoesNotEvaluateTheWholeProjectedLayerAtOnce()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            const int Size = 1024;
            var d = new PaintDocument(Size, Size, 64) { FilterBlockPixels = 128 };
            var resources = new ProjectResources(); var image = resources.Add("Picture", Picture(256, 256, 3), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            d.ImageResources = resources;
            var fill = d.AddFillLayer("projected", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(30, 200, 90, 255) } });
            d.SetFillImage(fill.Id, PaintChannel.Color, image.Id);
            d.SetFillProjection(fill.Id, fill.Projection.WithTiles(5, 5).WithRotation(30));
            int blocks = (Size / 128) * (Size / 128);
            CoreParallelism.MaxDegreeOfParallelism = 4;
            try
            {
            using (var c = new TileGpuCompositor(CompositorBackend.Cpu))
            {
                long before = d.FilterEvaluatedBlocks;
                c.Update(d, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 0 });
                long first = d.FilterEvaluatedBlocks - before;
                Assert.That(c.HasPendingWork, Is.True, "work is left for the next update");
                Assert.That(first, Is.LessThan(blocks), "one update evaluated " + first + " of " + blocks + " blocks");
                Assert.That(first, Is.LessThanOrEqualTo(blocks / 2), "the display block's 16 filter blocks, in batches of at most 8 blocks around each request (some outside the display block)");
                for (int i = 0; i < 10000 && c.HasPendingWork; i++) c.Update(d, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 0 });
                Assert.That(c.HasPendingWork, Is.False);
                var shown = c.Texture is RenderTexture ? GpuTests.Read(c.Texture) : GpuTests.ReadCpu(c.Texture);
                CpuCompositingTests.AssertSameBytes(d.Composite(PaintChannel.Color), shown, "sliced");
                Assert.That(d.FilterEvaluatedBlocks - before, Is.EqualTo(blocks), "each block evaluated once (the reference composite reads the cache)");
            }
            }
            finally { CoreParallelism.MaxDegreeOfParallelism = 0; }
        }
    }
}
