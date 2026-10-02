using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>クリッピング: 下地の内側にだけ描かれ、下地と 1 つのまとまりとして合成される（Photoshop の既定と同じ考え方）。</summary>
    public sealed class ClippingTests
    {
        static readonly Rgba32 Blue = new Rgba32(0, 0, 255, 255), Red = new Rgba32(200, 0, 0, 255), Green = new Rgba32(0, 255, 0, 255);

        /// <summary>16x16（タイル 8）。背景（全面の青）、下地（(2,2) だけ）、その上にクリッピングするレイヤー。</summary>
        static PaintDocument Stack(Rgba32 basePixel, Rgba32 clippedPixel, out Guid background, out Guid baseLayer, out Guid clipped)
        {
            var doc = new PaintDocument(16, 16, 8);
            var bg = doc.AddLayer("Background"); background = bg.Id;
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) bg.GetChannel(PaintChannel.Color).SetPixel(x, y, Blue);
            var b = doc.AddLayer("Base"); baseLayer = b.Id; b.GetChannel(PaintChannel.Color).SetPixel(2, 2, basePixel);
            var c = doc.AddLayer("Clipped"); clipped = c.Id;
            c.GetChannel(PaintChannel.Color).SetPixel(2, 2, clippedPixel); c.GetChannel(PaintChannel.Color).SetPixel(9, 9, clippedPixel);
            doc.SetLayerClipping(clipped, true); doc.ClearHistory(); return doc;
        }
        static Rgba32 At(PaintDocument doc, int x = 2, int y = 2) => doc.CompositePixel(PaintChannel.Color, x, y);

        [Test] public void AClippedLayerShowsOnlyInsideTheBase()
        {
            var doc = Stack(Red, Green, out _, out _, out var clipped);
            Assert.That(At(doc), Is.EqualTo(Green));
            Assert.That(At(doc, 9, 9), Is.EqualTo(Blue), "outside the base the clipped pixel is invisible");
            doc.SetLayerClipping(clipped, false);
            Assert.That(At(doc, 9, 9), Is.EqualTo(Green), "released, it shows everywhere");
            doc.Undo(); Assert.That(At(doc, 9, 9), Is.EqualTo(Blue));
        }

        [Test] public void TheGroupKeepsTheBasesAlphaLikePhotoshop()
        {
            // 下地のアルファ 128 の上に不透明な緑をクリッピング: まとまり = (緑, 128)。それを青の上に置く。
            var doc = Stack(new Rgba32(200, 0, 0, 128), Green, out _, out _, out _);
            var expected = CpuCompositor.Blend(Blue, new Rgba32(0, 255, 0, 128));
            Assert.That(At(doc), Is.EqualTo(expected));
            Assert.That(At(doc), Is.EqualTo(new Rgba32(0, 128, 127, 255)));
        }

        [Test] public void ClippedBlendModesSeeOnlyTheBase()
        {
            // 乗算のクリッピングは下地の色とだけ掛け合わされ、背景の青には効かない。
            var doc = Stack(Red, new Rgba32(128, 128, 128, 255), out _, out _, out var clipped);
            doc.SetLayerBlendMode(clipped, LayerBlendMode.Multiply);
            Assert.That(At(doc), Is.EqualTo(new Rgba32(100, 0, 0, 255)), "200 x 128/255 = 100.4");
        }

        [Test] public void TheBasesOpacityMaskAndVisibilityApplyToTheWholeGroup()
        {
            var doc = Stack(Red, Green, out _, out var baseLayer, out var clipped);
            doc.SetLayerOpacity(baseLayer, .5);
            Assert.That(At(doc), Is.EqualTo(CpuCompositor.Blend(Blue, Green, .5)));
            doc.SetLayerOpacity(baseLayer, 1);
            doc.AddLayerMask(baseLayer); using (var s = doc.BeginMaskStroke(baseLayer, new BrushSettings { Radius = 1, Hardness = 1, PressureSize = false, PressureOpacity = false })) { s.ApplyPixel(2, 2, 1); s.Commit(); }
            Assert.That(At(doc), Is.EqualTo(Blue), "the base mask hides the clipped layer too");
            doc.SetLayerMaskEnabled(baseLayer, false);
            doc.SetLayerVisibility(baseLayer, false);
            Assert.That(At(doc), Is.EqualTo(Blue), "a hidden base hides its clipped layers");
            doc.SetLayerVisibility(baseLayer, true); doc.SetLayerOpacity(clipped, .5);
            Assert.That(At(doc), Is.EqualTo(new Rgba32(100, 128, 0, 255)), "the clipped layer's own opacity mixes it into the base");
        }

        [Test] public void ConsecutiveClippedLayersShareTheBaseAndTheBottomLayerCannotClip()
        {
            var doc = Stack(Red, Green, out var background, out _, out _);
            var second = doc.AddLayer("Second"); second.GetChannel(PaintChannel.Color).SetPixel(2, 2, new Rgba32(255, 255, 255, 128)); second.GetChannel(PaintChannel.Color).SetPixel(9, 9, new Rgba32(255, 255, 255, 255));
            doc.SetLayerClipping(second.Id, true);
            Assert.That(At(doc), Is.EqualTo(new Rgba32(128, 255, 128, 255)), "green, then half white, inside the base");
            Assert.That(At(doc, 9, 9), Is.EqualTo(Blue), "the second clipped layer is clipped to the same base");
            doc.SetLayerClipping(background, true);
            Assert.That(doc.IsEffectivelyClipped(0), Is.False);
            Assert.That(At(doc, 5, 5), Is.EqualTo(Blue), "the bottom layer ignores its clipping flag");
        }

        [Test] public void ClippedAdjustmentsAndFillsAffectOnlyTheBase()
        {
            var doc = Stack(Red, Green, out _, out _, out var clipped);
            doc.SetLayerVisibility(clipped, false);
            var invert = doc.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert()); doc.SetLayerClipping(invert.Id, true);
            Assert.That(At(doc), Is.EqualTo(new Rgba32(55, 255, 255, 255)), "the base is inverted");
            Assert.That(At(doc, 9, 9), Is.EqualTo(Blue), "the background is not");
            doc.SetLayerVisibility(invert.Id, false);
            var fill = doc.AddFillLayer("Tint", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(255, 255, 0, 255) } });
            doc.SetLayerClipping(fill.Id, true);
            Assert.That(At(doc), Is.EqualTo(new Rgba32(255, 255, 0, 255)), "a clipped fill colours exactly the base");
            Assert.That(At(doc, 9, 9), Is.EqualTo(Blue));
        }

        [Test] public void LayersClippedToAnAdjustmentHaveNothingToClipTo()
        {
            var doc = new PaintDocument(16, 16, 8); var bg = doc.AddLayer("bg"); bg.GetChannel(PaintChannel.Color).SetPixel(1, 1, Red);
            doc.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels(0, 1, 1, 0, .5));
            var c = doc.AddLayer("c"); c.GetChannel(PaintChannel.Color).SetPixel(1, 1, Green); doc.SetLayerClipping(c.Id, true);
            Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(100, 0, 0, 255)), "the adjustment still applies; the clipped layer is hidden");
        }

        [Test] public void MovingTheBaseMarksTheClippedLayersTiles()
        {
            var doc = Stack(Red, Green, out _, out var baseLayer, out _);
            long since = doc.ChangeSerial;
            doc.MoveLayer(baseLayer, 0); // 背景が下地になる: 緑は (9,9) にも出るようになる
            var changed = new HashSet<TileCoord>(); doc.TryGetChangedTiles(PaintChannel.Color, since, changed);
            Assert.That(changed, Does.Contain(new TileCoord(1, 1)), "the clipped layer's tile at (9,9) changes visibility");
            Assert.That(At(doc, 9, 9), Is.EqualTo(Green));
        }

        [TestCase(4)] [TestCase(31)]
        public void CompositeWithClippingMatchesPerPixelReference(int seed)
        {
            var random = new Random(seed); var doc = new PaintDocument(41, 33, 16);
            for (int l = 0; l < 7; l++)
            {
                var layer = doc.AddLayer("L" + l); var surface = layer.GetChannel(PaintChannel.Color);
                for (int n = 0; n < 350; n++) { var c = new byte[4]; random.NextBytes(c); surface.SetPixel(random.Next(41), random.Next(33), new Rgba32(c[0], c[1], c[2], c[3])); }
                doc.SetLayerBlendMode(layer.Id, (LayerBlendMode)(l % 3)); doc.SetLayerOpacity(layer.Id, .4 + .6 * random.NextDouble());
                if (l % 3 != 0) doc.SetLayerClipping(layer.Id, true);
                if (l == 4) { var m = doc.AddLayerMask(layer.Id); for (int n = 0; n < 150; n++) m.Surface.SetPixel(random.Next(41), random.Next(33), new Rgba32(0, 0, 0, (byte)random.Next(256))); }
            }
            var hsl = doc.AddAdjustmentLayer("clipped HSL", AdjustmentSettings.HueSaturation(40, .2, -.1)); doc.SetLayerClipping(hsl.Id, true);
            var fill = doc.AddFillLayer("clipped fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(10, 200, 90, 120) } }); doc.SetLayerClipping(fill.Id, true);
            var reference = new byte[41 * 33 * 4];
            for (int y = 0; y < 33; y++) for (int x = 0; x < 41; x++)
            { var p = doc.CompositePixel(PaintChannel.Color, x, y); int i = (y * 41 + x) * 4; reference[i] = p.R; reference[i + 1] = p.G; reference[i + 2] = p.B; reference[i + 3] = p.A; }
            Assert.That(doc.Composite(PaintChannel.Color), Is.EqualTo(reference));
        }

        [Test] public void NativeArchiveRoundTripsClippingAndReadsVersion4()
        {
            var doc = Stack(Red, Green, out _, out _, out var clipped);
            var bytes = DocumentBinary.Write(doc); var restored = DocumentBinary.Read(bytes);
            Assert.That(DocumentBinary.Write(restored), Is.EqualTo(bytes));
            Assert.That(restored.GetLayer(clipped).Clipping, Is.True);
            Assert.That(restored.Composite(PaintChannel.Color), Is.EqualTo(doc.Composite(PaintChannel.Color)));
            Assert.That(restored.CanUndo, Is.False);

            // 版 4 には各レイヤーのクリッピングの 1 バイトが無い。クリッピングの無い文書を書き、そのバイトを抜いて版 4 として読む。
            var plain = new PaintDocument(16, 16, 8); var layer = plain.AddLayer("p"); layer.GetChannel(PaintChannel.Color).SetPixel(3, 3, Red);
            var current = DocumentBinary.Write(plain);
            foreach (int version in new[] { 4, 5 })
                Assert.That(DocumentBinary.Read(ArchiveTestUtil.AsVersion(current, "p", version)).Composite(PaintChannel.Color), Is.EqualTo(plain.Composite(PaintChannel.Color)), "version " + version);
        }

        [Test] public void PsdProjectionRoundTripsClipping()
        {
            var doc = Stack(Red, Green, out _, out _, out var clipped);
            var read = Yozolab.YoluPainter.Core.Psd.PsdCodec.Read(Yozolab.YoluPainter.Core.Psd.PsdCodec.Write(PsdBridge.Export(doc, PaintChannel.Color)));
            Assert.That(read.Mode, Is.EqualTo(Yozolab.YoluPainter.Core.Psd.PsdCompatibilityMode.EditableRaster));
            var imported = PsdBridge.Import(read);
            Assert.That(imported.Layers.Select(l => l.Clipping), Is.EqualTo(new[] { false, false, true }));
            Assert.That(imported.Composite(PaintChannel.Color), Is.EqualTo(doc.Composite(PaintChannel.Color)), "the clipped pixel at 9,9 stays outside the base");
        }
    }
}
