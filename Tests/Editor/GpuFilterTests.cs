using System;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>フィルターのある層とマスクの GPU 合成を CPU の正本と突き合わせる（許容は GpuTests と同じ 1）。GPU はフィルターを通したタイル
    /// （Core が halo 込みで評価したもの）を載せるので、差は合成のシェーダーの丸めだけ。差分の更新（ストローク・設定の変更・Undo）の後も、
    /// 全面の合成と一致し、CPU に回るタイルが無いことを確かめる。</summary>
    [Category("GPU")]
    public sealed class GpuFilterTests
    {
        [SetUp] public void RequireGpu() { GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite"); }

        static PaintDocument Scene(PaintChannel channel = PaintChannel.Color)
        {
            var d = new PaintDocument(300, 260, 32);
            var bg = d.AddLayer("bg"); var b = bg.GetChannel(channel);
            for (int y = 0; y < 260; y++) for (int x = 0; x < 300; x++)
                b.SetPixel(x, y, channel == PaintChannel.Normal ? new Rgba32(128, 128, 255, 255) : new Rgba32((byte)(x * 255 / 299), (byte)(y * 255 / 259), 128, 255));
            var l = d.AddLayer("top"); var s = l.GetChannel(channel);
            for (int y = 90; y < 150; y++) for (int x = 50; x < 210; x++)
                s.SetPixel(x, y, channel == PaintChannel.Normal ? new Rgba32(220, (byte)(60 + (x % 40) * 3), 200, 255) : new Rgba32(250, (byte)(x % 200), (byte)(y * 2 % 256), (byte)(x < 120 ? 255 : 180)));
            d.ClearHistory(); return d;
        }
        static void Check(TileGpuCompositor c, PaintDocument d, string context, PaintChannel channel = PaintChannel.Color)
        {
            c.Update(d, channel);
            Assert.That(c.Backend, Does.StartWith("CPU source brush / GPU"), c.Backend);
            GpuTests.AssertMatches(d.Composite(channel), GpuTests.Read(c.Texture), context);
            Assert.That(c.LastCpuTileCount, Is.Zero, context);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FilteredLayersAndMasksMatchTheCpuReference(bool copyTexture)
        {
            var d = Scene(); var l = d.Layers[1];
            var c = new TileGpuCompositor(allowCopyTexture: copyTexture);
            try
            {
                Check(c, d, "no filter");
                var blur = d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(20)); Check(c, d, "blur 20");
                d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Sharpen(3, 2, 2)); Check(c, d, "blur + sharpen");
                d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Noise(.4, 9, false)); Check(c, d, "+ colour noise");
                d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Levels(.1, .9, 1.6, 0, 1), strength: .5); Check(c, d, "+ levels at half strength");
                d.SetFilterSettings(l.Id, blur.Id, FilterSettings.GaussianBlur(45), coalesce: true); Check(c, d, "slider: blur 45");
                var stroke = d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings { Radius = 4, Color = new Rgba32(0, 0, 255, 255) });
                stroke.Add(new BrushSample(255, 200)); stroke.Add(new BrushSample(262, 222, 1, 1)); stroke.Commit();
                Check(c, d, "stroke far from the block where the blur reaches");
                d.AddLayerMask(l.Id); d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.Noise(.8, 3, true)); Check(c, d, "mask noise");
                d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(6)); Check(c, d, "mask noise + blur");
                d.SetLayerOpacity(l.Id, .7, coalesce: true); Check(c, d, "opacity (below copy reused)");
                d.Undo(); d.Undo(); d.Undo(); Check(c, d, "undo three");
                d.Redo(); Check(c, d, "redo");
                d.AddFilter(d.Layers[0].Id, FilterTarget.Content, FilterSettings.Normalize()); Check(c, d, "global normalize on the backdrop");
            }
            finally { c.Dispose(); }
        }

        [Test] public void FilteredFillsAndNormalBlurMatch()
        {
            var d = Scene(PaintChannel.Normal); var l = d.Layers[1];
            var c = new TileGpuCompositor();
            try
            {
                d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(12), new[] { PaintChannel.Normal });
                Check(c, d, "normal blur", PaintChannel.Normal);
                var fill = d.AddFillLayer("fill", new System.Collections.Generic.Dictionary<PaintChannel, Rgba32> { { PaintChannel.Roughness, new Rgba32(100, 100, 100, 255) } });
                d.AddFilter(fill.Id, FilterTarget.Content, FilterSettings.Noise(.5, 1, true), new[] { PaintChannel.Roughness });
                Check(c, d, "noisy fill", PaintChannel.Roughness);
            }
            finally { c.Dispose(); }
        }
    }
}
