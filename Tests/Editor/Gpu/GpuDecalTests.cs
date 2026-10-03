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
    /// <summary>デカールの表示の合成: GPU の合成（許容は GpuTests と同じ 1）も、CPU を選んだ表示（バイト一致）も CPU の正本と同じ。置く・動かす
    /// （ドラッグのまとめ）・間引き・Undo・マップが使えない（何も出ない）・焼き直し・画像を外す（箱に値）は、変更の記録だけで描き直される。動かした
    /// ときは前後の箱の届くタイルだけを作り直す（全面は作り直さない）。</summary>
    [Category("GPU")]
    public sealed class GpuDecalTests
    {
        const int W = 320, H = 256, T = 32;

        /// <summary>透明な縁の中の不透明な円（縁の近くは半透明）。</summary>
        static ImageContent Logo(int w, int h)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4; double dx = (x + .5 - w / 2.0) / (h / 2.0), dy = (y + .5 - h / 2.0) / (h / 2.0), r = dx * dx + dy * dy;
                rgba[o] = (byte)(x * 255 / (w - 1)); rgba[o + 1] = 60; rgba[o + 2] = (byte)(y * 255 / (h - 1)); rgba[o + 3] = (byte)(r < .5 ? 255 : r < .8 ? 140 : 0);
            }
            return ImageContent.FromPixels(rgba, w, h);
        }
        static BakedMeshMap Position(double phase = 0) => TestMeshMaps.Make(MeshMapKind.Position, W, H, (x, y, c) => c == 0 ? x / (double)(W - 1) : c == 1 ? y / (double)(H - 1) : .5 + .2 * Math.Sin(x * .03 + phase),
            (x, y) => x < 6 ? MeshTexelCoverage.Empty : MeshTexelCoverage.Covered);
        /// <summary>ほとんどの面が −Z（画像を見る側）を向き、場所で少しずつ傾く。</summary>
        static BakedMeshMap Normal() => TestMeshMaps.Make(MeshMapKind.WorldNormal, W, H, (x, y, c) => c == 0 ? .5 + .35 * Math.Sin(x * .02) : c == 1 ? .5 + .35 * Math.Cos(y * .03) : .15);

        static PaintDocument Scene(out PaintLayer decal, out ProjectResources resources, out ImageResource image)
        {
            var d = new PaintDocument(W, H, T);
            var bg = d.AddLayer("bg"); var b = bg.GetChannel(PaintChannel.Color);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) b.SetPixel(x, y, new Rgba32((byte)(x * 255 / (W - 1)), 90, (byte)(y * 255 / (H - 1)), 255));
            resources = new ProjectResources(); image = resources.Add("Logo", Logo(64, 40), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            d.ImageResources = resources;
            decal = d.AddFillLayer("decal", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(30, 200, 90, 255) } });
            d.SetFillImage(decal.Id, PaintChannel.Color, image.Id);
            d.SetFillProjection(decal.Id, FillProjection.DecalAt(new ShapeVolume(GeneratorShape.Box, .3, .4, .5, 0, 0, 10, .3, .2, .5, 0)));
            d.ClearHistory();
            return d;
        }

        static void Changes(PaintDocument d, PaintLayer decal, TestGeneratorInputs inputs, Action<string, bool> check)
        {
            check("placed", false);
            var v = decal.Projection.Placement;
            d.SetFillProjection(decal.Id, decal.Projection.WithPlacement(v.WithCenter(.35, .45, .5)), coalesce: true);
            d.SetFillProjection(decal.Id, decal.Projection.WithPlacement(v.WithCenter(.4, .5, .5)), coalesce: true); check("dragged", true);
            d.EndCoalescing();
            d.SetFillProjection(decal.Id, decal.Projection.WithCulling(.2, 40, .3)); check("culled harder", true);
            d.Undo(); check("undo", true);
            d.SetFillProjection(decal.Id, decal.Projection.WithPlacement(v.WithCenter(.7, .2, .5).WithRotation(0, 0, -30))); check("moved far and turned", true);
            inputs.Refuse(MeshMapKind.WorldNormal, "stale: test"); check("stale normal: nothing", false);
            inputs.Put(Normal()).Put(Position(.7)); check("baked again", false);
            d.SetFillImage(decal.Id, PaintChannel.Color, null); check("image removed: the value in the box", false);
            d.Undo(); check("image back", false);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void GpuCompositingMatchesTheCpuReferenceThroughEveryChange(bool copyTexture)
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            var c = new TileGpuCompositor(allowCopyTexture: copyTexture);
            try
            {
                var d = Scene(out var decal, out var resources, out var image); var inputs = new TestGeneratorInputs().Put(Position()).Put(Normal()); d.GeneratorInputs = inputs;
                int tiles = (W / T) * (H / T);
                Changes(d, decal, inputs, (step, local) =>
                {
                    c.Update(d, PaintChannel.Color);
                    Assert.That(c.Backend, Does.StartWith("CPU source brush / GPU"), c.Backend);
                    GpuTests.AssertMatches(d.Composite(PaintChannel.Color), GpuTests.Read(c.Texture), step);
                    Assert.That(c.LastCpuTileCount, Is.Zero, step);
                    if (local) Assert.That(c.LastUpdatedTileCount, Is.GreaterThan(0).And.LessThan(tiles / 2), step + ": only the tiles the box reached before and after");
                });
            }
            finally { c.Dispose(); }
        }

        [Test] public void TheCpuDisplayPathShowsExactlyTheReferenceThroughEveryChange()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var d = Scene(out var decal, out var resources, out var image); var inputs = new TestGeneratorInputs().Put(Position()).Put(Normal()); d.GeneratorInputs = inputs;
            int tiles = (W / T) * (H / T);
            using (var c = new TileGpuCompositor(CompositorBackend.Cpu))
                Changes(d, decal, inputs, (step, local) =>
                {
                    c.Update(d, PaintChannel.Color);
                    var shown = c.Texture is RenderTexture ? GpuTests.Read(c.Texture) : GpuTests.ReadCpu(c.Texture);
                    CpuCompositingTests.AssertSameBytes(d.Composite(PaintChannel.Color), shown, step);
                    if (local) Assert.That(c.LastUpdatedTileCount, Is.GreaterThan(0).And.LessThan(tiles / 2), step + ": only the tiles the box reached before and after");
                });
        }
    }
}
