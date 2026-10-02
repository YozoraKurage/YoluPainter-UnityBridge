using System;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>合成モードの色の式（手計算の値）、非分離モードの性質、保存往復、タイル合成と 1 画素ずつの参照の一致。</summary>
    public sealed class BlendModeTests
    {
        static double B(LayerBlendMode mode, double d, double s)
        {
            CpuCompositor.BlendRgb(mode, d, d, d, s, s, s, out double r, out double g, out double b);
            Assert.That(g, Is.EqualTo(r).Within(1e-12)); Assert.That(b, Is.EqualTo(r).Within(1e-12));
            return r;
        }

        [TestCase(LayerBlendMode.Normal, .3, .8, .8)]
        [TestCase(LayerBlendMode.Multiply, .5, .5, .25)]
        [TestCase(LayerBlendMode.Screen, .5, .5, .75)]
        [TestCase(LayerBlendMode.Overlay, .2, .8, .32)]
        [TestCase(LayerBlendMode.Overlay, .8, .2, .68)]
        [TestCase(LayerBlendMode.Darken, .3, .6, .3)]
        [TestCase(LayerBlendMode.Lighten, .3, .6, .6)]
        [TestCase(LayerBlendMode.ColorDodge, .25, .5, .5)]
        [TestCase(LayerBlendMode.ColorDodge, .5, .5, 1)]
        [TestCase(LayerBlendMode.ColorDodge, 0, 1, 0)]
        [TestCase(LayerBlendMode.ColorBurn, .75, .5, .5)]
        [TestCase(LayerBlendMode.ColorBurn, .5, .5, 0)]
        [TestCase(LayerBlendMode.ColorBurn, 1, 0, 1)]
        [TestCase(LayerBlendMode.LinearDodge, .3, .4, .7)]
        [TestCase(LayerBlendMode.LinearDodge, .6, .6, 1)]
        [TestCase(LayerBlendMode.LinearBurn, .6, .6, .2)]
        [TestCase(LayerBlendMode.LinearBurn, .2, .3, 0)]
        [TestCase(LayerBlendMode.HardLight, .2, .8, .68)]
        [TestCase(LayerBlendMode.HardLight, .8, .2, .32)]
        [TestCase(LayerBlendMode.SoftLight, .25, .75, .375)]
        [TestCase(LayerBlendMode.SoftLight, .25, .25, .15625)]
        [TestCase(LayerBlendMode.SoftLight, .25, .5, .25)]
        [TestCase(LayerBlendMode.VividLight, .5, .25, 0)]
        [TestCase(LayerBlendMode.VividLight, .25, .75, .5)]
        [TestCase(LayerBlendMode.LinearLight, .5, .75, 1)]
        [TestCase(LayerBlendMode.LinearLight, .5, .6, .7)]
        [TestCase(LayerBlendMode.PinLight, .5, .1, .2)]
        [TestCase(LayerBlendMode.PinLight, .5, .9, .8)]
        [TestCase(LayerBlendMode.PinLight, .5, .5, .5)]
        [TestCase(LayerBlendMode.Difference, .2, .7, .5)]
        [TestCase(LayerBlendMode.Exclusion, .5, .5, .5)]
        [TestCase(LayerBlendMode.Exclusion, .2, .5, .5)]
        [TestCase(LayerBlendMode.Subtract, .7, .2, .5)]
        [TestCase(LayerBlendMode.Subtract, .2, .7, 0)]
        [TestCase(LayerBlendMode.Divide, .25, .5, .5)]
        [TestCase(LayerBlendMode.Divide, .5, 0, 1)]
        [TestCase(LayerBlendMode.Divide, 0, 0, 0)]
        [TestCase(LayerBlendMode.Divide, .5, .25, 1)]
        public void SeparableModesFollowTheirFormulas(LayerBlendMode mode, double below, double over, double expected)
        {
            Assert.That(B(mode, below, over), Is.EqualTo(expected).Within(1e-12));
        }

        [Test] public void HardMixThresholdsTheSumOfTheBytes()
        {
            Assert.That(B(LayerBlendMode.HardMix, 128 / 255.0, 127 / 255.0), Is.EqualTo(1), "128 + 127 = 255");
            Assert.That(B(LayerBlendMode.HardMix, 128 / 255.0, 126 / 255.0), Is.EqualTo(0));
            Assert.That(B(LayerBlendMode.HardMix, 0, 1), Is.EqualTo(1));
        }

        static double Lum(double r, double g, double b) => .3 * r + .59 * g + .11 * b;

        [Test] public void NonSeparableModesKeepWhatTheyPromise()
        {
            // Color: 下の明るさに上の色相と彩度
            CpuCompositor.BlendRgb(LayerBlendMode.Color, .5, .5, .5, .9, .2, .1, out double r, out double g, out double b);
            Assert.That(Lum(r, g, b), Is.EqualTo(.5).Within(1e-9)); Assert.That(r, Is.GreaterThan(g)); Assert.That(g, Is.GreaterThan(b));
            // Luminosity: 上の明るさに下の色
            CpuCompositor.BlendRgb(LayerBlendMode.Luminosity, .8, .3, .2, .25, .25, .25, out r, out g, out b);
            Assert.That(Lum(r, g, b), Is.EqualTo(.25).Within(1e-9)); Assert.That(r, Is.GreaterThan(g));
            // Hue: 無彩色の上は下を同じ明るさの灰色にする / Saturation: 無彩色の上は下の彩度を 0 に
            CpuCompositor.BlendRgb(LayerBlendMode.Hue, .8, .3, .2, .5, .5, .5, out r, out g, out b);
            Assert.That(new[] { r, g, b }, Is.EqualTo(new[] { Lum(.8, .3, .2), Lum(.8, .3, .2), Lum(.8, .3, .2) }).Within(1e-9));
            CpuCompositor.BlendRgb(LayerBlendMode.Saturation, .8, .3, .2, .9, .9, .9, out r, out g, out b);
            Assert.That(r, Is.EqualTo(g).Within(1e-9)); Assert.That(g, Is.EqualTo(b).Within(1e-9));
            // Hue: 上の色相、下の彩度と明るさ。赤い下に青い上 → 青みで明るさは下のまま
            CpuCompositor.BlendRgb(LayerBlendMode.Hue, .8, .2, .2, .1, .1, .9, out r, out g, out b);
            Assert.That(b, Is.GreaterThan(r)); Assert.That(Lum(r, g, b), Is.EqualTo(Lum(.8, .2, .2)).Within(1e-9));
            // 結果は常に 0..1
            var random = new Random(3);
            foreach (var mode in new[] { LayerBlendMode.Hue, LayerBlendMode.Saturation, LayerBlendMode.Color, LayerBlendMode.Luminosity })
                for (int i = 0; i < 2000; i++)
                {
                    CpuCompositor.BlendRgb(mode, random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble(), out r, out g, out b);
                    Assert.That(new[] { r, g, b }, Has.All.InRange(0.0, 1.0), mode.ToString());
                }
        }

        [Test] public void DarkerAndLighterColorPickAWholeColourAndKeepTheBaseOnATie()
        {
            CpuCompositor.BlendRgb(LayerBlendMode.DarkerColor, .9, .1, .1, .3, .3, .3, out double r, out double g, out double b);
            Assert.That(new[] { r, g, b }, Is.EqualTo(new[] { .3, .3, .3 }), "sum .9 < 1.1: the whole blend colour, not per channel");
            CpuCompositor.BlendRgb(LayerBlendMode.LighterColor, .9, .1, .1, .3, .3, .3, out r, out g, out b);
            Assert.That(new[] { r, g, b }, Is.EqualTo(new[] { .9, .1, .1 }));
            CpuCompositor.BlendRgb(LayerBlendMode.DarkerColor, .6, .3, 0, .3, .3, .3, out r, out g, out b);
            Assert.That(new[] { r, g, b }, Is.EqualTo(new[] { .6, .3, 0 }), "a tie keeps the base");
            // 8 bit の和が同じ（51+255+153 = 255+204+0）なら、浮動小数の丸めに関係なく同点として下を残す
            foreach (var mode in new[] { LayerBlendMode.DarkerColor, LayerBlendMode.LighterColor })
            {
                CpuCompositor.BlendRgb(mode, 51 / 255.0, 255 / 255.0, 153 / 255.0, 255 / 255.0, 204 / 255.0, 0, out r, out g, out b);
                Assert.That(new[] { r, g, b }, Is.EqualTo(new[] { 51 / 255.0, 1, 153 / 255.0 }), mode.ToString());
            }
        }

        [Test] public void PartialAlphaUsesTheBlendOnlyWhereBothOverlap()
        {
            // 下が透明なら上の色そのもの、上が半透明なら W3C の source-over
            var over = new Rgba32(200, 100, 50, 255);
            Assert.That(CpuCompositor.Blend(Rgba32.Transparent, over, 1, LayerBlendMode.Multiply), Is.EqualTo(over));
            var below = new Rgba32(100, 200, 255, 255);
            var half = CpuCompositor.Blend(below, over, .5, LayerBlendMode.Difference);
            Assert.That(half.A, Is.EqualTo(255));
            Assert.That(half.R, Is.EqualTo((byte)Math.Round((100 + Math.Abs(100 - 200)) / 2.0)));
            Assert.That(() => CpuCompositor.Blend(below, over, 1, (LayerBlendMode)99), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        static PaintDocument Scene(LayerBlendMode mode)
        {
            var doc = new PaintDocument(24, 24, 8);
            var below = doc.AddLayer("Below"); var over = doc.AddLayer("Over"); var clip = doc.AddLayer("Clip");
            var adjust = doc.AddAdjustmentLayer("Adjust", AdjustmentSettings.HueSaturation(40, .3, .1));
            for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++)
            {
                below.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32((byte)(x * 11), (byte)(y * 11), (byte)((x + y) * 5), (byte)(x < 3 ? 100 : 255)));
                if (y > 2) over.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32((byte)(255 - y * 10), (byte)(x * 10), 128, (byte)(y * 10)));
                if ((x + y) % 2 == 0) clip.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32(30, (byte)(x * 10), (byte)(y * 10), 200));
            }
            doc.SetLayerBlendMode(over.Id, mode); doc.SetLayerOpacity(over.Id, .7);
            doc.SetLayerClipping(clip.Id, true); doc.SetLayerBlendMode(clip.Id, mode);
            doc.SetLayerBlendMode(adjust.Id, mode); doc.SetLayerOpacity(adjust.Id, .6);
            return doc;
        }

        [Test] public void EveryModeCompositesTheSameByTileAndByPixelAndSurvivesSaving()
        {
            foreach (LayerBlendMode mode in Enum.GetValues(typeof(LayerBlendMode)))
            {
                var doc = Scene(mode);
                var tiles = doc.Composite(PaintChannel.Color);
                for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++)
                {
                    var p = CpuCompositor.CompositePixel(doc, PaintChannel.Color, x, y); int i = (y * 24 + x) * 4;
                    Assert.That(new[] { tiles[i], tiles[i + 1], tiles[i + 2], tiles[i + 3] }, Is.EqualTo(new[] { p.R, p.G, p.B, p.A }), mode + " at " + x + "," + y);
                }
                var reread = DocumentBinary.Read(DocumentBinary.Write(doc));
                Assert.That(reread.Layers.Select(l => l.BlendMode), Is.EqualTo(doc.Layers.Select(l => l.BlendMode)), mode.ToString());
                Assert.That(reread.Composite(PaintChannel.Color), Is.EqualTo(tiles), mode.ToString());
            }
        }

        [Test] public void TheStoredValuesOfTheFirstModesNeverChange()
        {
            // 保存形式に入る値。既存のファイルの意味が変わらないこと。
            Assert.That((int)LayerBlendMode.Normal, Is.EqualTo(0)); Assert.That((int)LayerBlendMode.Multiply, Is.EqualTo(1)); Assert.That((int)LayerBlendMode.Screen, Is.EqualTo(2));
            Assert.That(Enum.GetValues(typeof(LayerBlendMode)).Length, Is.EqualTo(26));
            Assert.That((int)LayerBlendMode.LighterColor, Is.EqualTo(25));
        }

        [Test] public void ChangingTheModeIsOneUndoStepAndPsdProjectionStillRefusesIt()
        {
            var doc = new PaintDocument(16, 16, 8); var layer = doc.AddLayer("A");
            layer.GetChannel(PaintChannel.Color).SetPixel(1, 1, new Rgba32(10, 20, 30)); doc.ClearHistory();
            doc.SetLayerBlendMode(layer.Id, LayerBlendMode.SoftLight);
            Assert.That(doc.Undo(), Is.True); Assert.That(doc.Layers[0].BlendMode, Is.EqualTo(LayerBlendMode.Normal));
            Assert.That(doc.Redo(), Is.True); Assert.That(doc.Layers[0].BlendMode, Is.EqualTo(LayerBlendMode.SoftLight));
            Assert.That(() => doc.SetLayerBlendMode(layer.Id, (LayerBlendMode)26), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => PsdBridge.Export(doc, PaintChannel.Color), Throws.InvalidOperationException, "the PSD writer does not map blend modes yet; it must not flatten them");
        }
    }
}
