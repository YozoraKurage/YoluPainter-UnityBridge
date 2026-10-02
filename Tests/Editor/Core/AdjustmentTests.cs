using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>調整レイヤー（反転・レベル補正・色相/彩度/明度）と、スライダー操作の Undo のまとめ。</summary>
    public sealed class AdjustmentTests
    {
        static PaintDocument OnePixel(Rgba32 color, out Guid layer)
        {
            var doc = new PaintDocument(16, 16, 8); var l = doc.AddLayer("Paint"); layer = l.Id;
            l.GetChannel(PaintChannel.Color).SetPixel(2, 2, color); doc.ClearHistory(); return doc;
        }
        static Rgba32 At(PaintDocument doc) => doc.CompositePixel(PaintChannel.Color, 2, 2);

        [Test] public void FormulasProduceTheDocumentedValues()
        {
            Assert.That(AdjustmentSettings.Invert().Apply(new Rgba32(10, 20, 30, 128)), Is.EqualTo(new Rgba32(245, 235, 225, 128)));
            var levels = AdjustmentSettings.Levels(.2, .8, 2, 0, 1);
            Assert.That(levels.Apply(new Rgba32(127, 0, 255, 77)), Is.EqualTo(new Rgba32(180, 0, 255, 77)), "(0.498-0.2)/0.6 = 0.4967, ^(1/2) = 0.7048");
            Assert.That(AdjustmentSettings.Levels(0, 1, 1, .2, .6).Apply(new Rgba32(255, 0, 128, 255)), Is.EqualTo(new Rgba32(153, 51, 102, 255)));
            Assert.That(AdjustmentSettings.HueSaturation(120).Apply(new Rgba32(255, 0, 0, 9)), Is.EqualTo(new Rgba32(0, 255, 0, 9)));
            Assert.That(AdjustmentSettings.HueSaturation(0, -1).Apply(new Rgba32(255, 0, 0, 255)), Is.EqualTo(new Rgba32(128, 128, 128, 255)));
            Assert.That(AdjustmentSettings.HueSaturation(0, 0, 1).Apply(new Rgba32(40, 90, 200, 255)), Is.EqualTo(new Rgba32(255, 255, 255, 255)));
            Assert.That(AdjustmentSettings.HueSaturation(0, 0, -1).Apply(new Rgba32(40, 90, 200, 255)), Is.EqualTo(new Rgba32(0, 0, 0, 255)));
            Assert.Throws<ArgumentOutOfRangeException>(() => AdjustmentSettings.Levels(.5, .5));
            Assert.Throws<ArgumentOutOfRangeException>(() => AdjustmentSettings.HueSaturation(181));
        }

        [Test] public void AnAdjustmentChangesColourBelowButNeverAlpha()
        {
            var doc = OnePixel(new Rgba32(100, 50, 25, 128), out _);
            doc.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert());
            Assert.That(At(doc), Is.EqualTo(new Rgba32(155, 205, 230, 128)));
            Assert.That(doc.CompositePixel(PaintChannel.Color, 9, 9), Is.EqualTo(Rgba32.Transparent), "transparent pixels stay transparent");
        }

        [Test] public void OpacityMaskAndBlendModeMixTheAdjustmentBack()
        {
            var doc = OnePixel(new Rgba32(100, 100, 100, 255), out _);
            var adj = doc.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert());
            doc.SetLayerOpacity(adj.Id, .5);
            Assert.That(At(doc), Is.EqualTo(new Rgba32(128, 128, 128, 255)), "100 + (155-100)*0.5 = 127.5");
            doc.SetLayerOpacity(adj.Id, 1); doc.SetLayerBlendMode(adj.Id, LayerBlendMode.Multiply);
            Assert.That(At(doc), Is.EqualTo(new Rgba32(61, 61, 61, 255)), "100/255 * 155/255 * 255 = 60.8");
            doc.SetLayerBlendMode(adj.Id, LayerBlendMode.Normal);
            doc.AddLayerMask(adj.Id); using (var s = doc.BeginMaskStroke(adj.Id, new BrushSettings { Radius = 1, Hardness = 1, PressureSize = false, PressureOpacity = false })) { s.ApplyPixel(2, 2, 1); s.Commit(); }
            Assert.That(At(doc), Is.EqualTo(new Rgba32(100, 100, 100, 255)), "the mask hides the adjustment here");
        }

        [Test] public void HueSaturationCannotTargetScalarChannels()
        {
            var doc = new PaintDocument(16, 16, 8);
            Assert.Throws<InvalidOperationException>(() => doc.AddAdjustmentLayer("bad", AdjustmentSettings.HueSaturation(30), new[] { PaintChannel.Roughness }));
            var hsl = doc.AddAdjustmentLayer("hsl", AdjustmentSettings.HueSaturation(30));
            CollectionAssert.AreEquivalent(new[] { PaintChannel.Color, PaintChannel.Emission }, hsl.EnabledChannels);
            Assert.Throws<InvalidOperationException>(() => doc.SetChannelEnabled(hsl.Id, PaintChannel.Height, true));
            var levels = doc.AddAdjustmentLayer("levels", AdjustmentSettings.Levels(0, 1, 2));
            Assert.That(levels.IsChannelEnabled(PaintChannel.Roughness), Is.True);
            Assert.Throws<InvalidOperationException>(() => doc.SetAdjustment(levels.Id, AdjustmentSettings.HueSaturation(10)), "Roughness is enabled");
            doc.SetChannelEnabled(levels.Id, PaintChannel.Roughness, false); doc.SetChannelEnabled(levels.Id, PaintChannel.Metallic, false);
            doc.SetChannelEnabled(levels.Id, PaintChannel.Height, false); doc.SetChannelEnabled(levels.Id, PaintChannel.Normal, false);
            Assert.DoesNotThrow(() => doc.SetAdjustment(levels.Id, AdjustmentSettings.HueSaturation(10)));
            Assert.Throws<InvalidOperationException>(() => doc.BeginStroke(levels.Id, PaintChannel.Color, new BrushSettings()));
        }

        [Test] public void AdjustmentChangesAreUndoableAndCoverTheCanvas()
        {
            var doc = OnePixel(new Rgba32(100, 100, 100, 255), out _);
            var adj = doc.AddAdjustmentLayer("L", AdjustmentSettings.Levels()); doc.ClearHistory();
            long since = doc.ChangeSerial;
            doc.SetAdjustment(adj.Id, AdjustmentSettings.Levels(0, 1, 1, 0, .5));
            Assert.That(At(doc), Is.EqualTo(new Rgba32(50, 50, 50, 255)));
            var changed = new HashSet<TileCoord>(); doc.TryGetChangedTiles(PaintChannel.Color, since, changed);
            Assert.That(changed.Count, Is.EqualTo(4), "an adjustment covers every canvas tile");
            doc.Undo(); Assert.That(At(doc), Is.EqualTo(new Rgba32(100, 100, 100, 255)));
        }

        [Test] public void SliderEditsCoalesceIntoOneUndoStepUntilTheRunEnds()
        {
            var doc = OnePixel(new Rgba32(100, 100, 100, 255), out var layer);
            doc.SetLayerOpacity(layer, .9, coalesce: true); doc.SetLayerOpacity(layer, .7, coalesce: true); doc.SetLayerOpacity(layer, .5, coalesce: true);
            Assert.That(doc.UndoCount, Is.EqualTo(1));
            doc.Undo(); Assert.That(doc.GetLayer(layer).Opacity, Is.EqualTo(1), "one undo returns to the value before the drag");
            doc.Redo(); Assert.That(doc.GetLayer(layer).Opacity, Is.EqualTo(.5));
            doc.SetLayerOpacity(layer, .4, coalesce: true); Assert.That(doc.UndoCount, Is.EqualTo(2), "undo/redo ends a run");
            doc.EndCoalescing(); doc.SetLayerOpacity(layer, .3, coalesce: true); Assert.That(doc.UndoCount, Is.EqualTo(3), "EndCoalescing ends a run");
            doc.SetLayerName(layer, "x"); doc.SetLayerOpacity(layer, .2, coalesce: true); Assert.That(doc.UndoCount, Is.EqualTo(5), "another edit ends a run");
            doc.SetLayerOpacity(layer, .1); doc.SetLayerOpacity(layer, .05); Assert.That(doc.UndoCount, Is.EqualTo(7), "without coalesce every edit is its own step");
            var mask = doc.AddLayerMask(layer); doc.SetLayerMaskDensity(layer, .5, coalesce: true); doc.SetLayerMaskDensity(layer, .25, coalesce: true);
            doc.SetLayerOpacity(layer, .6, coalesce: true);
            Assert.That(doc.UndoCount, Is.EqualTo(10), "a different target starts a new run");
            doc.Undo(); doc.Undo(); Assert.That(mask.Density, Is.EqualTo(1));
        }

        [TestCase(2)] [TestCase(29)]
        public void CompositeWithAdjustmentsMatchesPerPixelReference(int seed)
        {
            var random = new Random(seed); var doc = new PaintDocument(41, 35, 16);
            var raster = doc.AddLayer("R").GetChannel(PaintChannel.Color);
            for (int n = 0; n < 700; n++) { var c = new byte[4]; random.NextBytes(c); raster.SetPixel(random.Next(41), random.Next(35), new Rgba32(c[0], c[1], c[2], c[3])); }
            var hsl = doc.AddAdjustmentLayer("HSL", AdjustmentSettings.HueSaturation(73, -.4, .2));
            doc.SetLayerOpacity(hsl.Id, .8);
            var mask = doc.AddLayerMask(hsl.Id); for (int n = 0; n < 200; n++) mask.Surface.SetPixel(random.Next(41), random.Next(35), new Rgba32(0, 0, 0, (byte)random.Next(256)));
            var levels = doc.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels(.1, .9, 1.7, .05, .95));
            doc.SetLayerBlendMode(levels.Id, LayerBlendMode.Screen);
            doc.AddFillLayer("F", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(30, 60, 90, 40) } });
            doc.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert());
            var reference = new byte[41 * 35 * 4];
            for (int y = 0; y < 35; y++) for (int x = 0; x < 41; x++)
            { var p = doc.CompositePixel(PaintChannel.Color, x, y); int i = (y * 41 + x) * 4; reference[i] = p.R; reference[i + 1] = p.G; reference[i + 2] = p.B; reference[i + 3] = p.A; }
            Assert.That(doc.Composite(PaintChannel.Color), Is.EqualTo(reference));
        }

        [Test] public void NativeArchiveRoundTripsAdjustmentsAndReadsVersion3()
        {
            var doc = OnePixel(new Rgba32(100, 100, 100, 255), out _);
            var hsl = doc.AddAdjustmentLayer("HSL", AdjustmentSettings.HueSaturation(-45, .3, -.2));
            doc.SetChannelEnabled(hsl.Id, PaintChannel.Emission, false);
            doc.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels(.1, .9, 2.5, .2, .8), new[] { PaintChannel.Roughness });
            var bytes = DocumentBinary.Write(doc); var restored = DocumentBinary.Read(bytes);
            Assert.That(DocumentBinary.Write(restored), Is.EqualTo(bytes));
            Assert.That(restored.Layers[1].Adjustment, Is.EqualTo(AdjustmentSettings.HueSaturation(-45, .3, -.2)));
            CollectionAssert.AreEquivalent(new[] { PaintChannel.Color }, restored.Layers[1].EnabledChannels);
            CollectionAssert.AreEquivalent(new[] { PaintChannel.Roughness }, restored.Layers[2].EnabledChannels);
            Assert.That(restored.Composite(PaintChannel.Color), Is.EqualTo(doc.Composite(PaintChannel.Color)));

            // 版 3 は「各レイヤーのクリッピングの 1 バイト（版 5）」と「調整のブロック（版 4）」が無い並び。
            // 1 レイヤーの文書からクリッピングのバイトを抜いて版 3 にする。
            var plain = new PaintDocument(16, 16, 8); plain.AddFillLayer("F", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(9, 9, 9, 9) } });
            Assert.That(DocumentBinary.Read(AsVersion3(DocumentBinary.Write(plain), "F")).Composite(PaintChannel.Color), Is.EqualTo(plain.Composite(PaintChannel.Color)));
            var adjusted = new PaintDocument(16, 16, 8); adjusted.AddAdjustmentLayer("A", AdjustmentSettings.Invert());
            Assert.Throws<InvalidDataException>(() => DocumentBinary.Read(AsVersion3(DocumentBinary.Write(adjusted), "A")), "a version 3 archive cannot contain adjustment layers");
        }

        static byte[] AsVersion3(byte[] current, string layerName) => ArchiveTestUtil.AsVersion(current, layerName, 3);

        [Test] public void UnknownAdjustmentAlgorithmVersionsAreRefused()
        {
            var doc = new PaintDocument(16, 16, 8); doc.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert());
            var bytes = DocumentBinary.Write(doc);
            // 調整レイヤーの種類（int）の直後がアルゴリズム版。種類 2 の直後の「Fill 値の数 0」を飛ばした位置を探す。
            int type = FindAdjustmentBlock(bytes); BitConverter.GetBytes(AdjustmentSettings.AlgorithmVersion + 1).CopyTo(bytes, type + 4);
            Assert.Throws<InvalidDataException>(() => DocumentBinary.Read(bytes));
        }

        static int FindAdjustmentBlock(byte[] bytes)
        {
            // 種類 = 2、親グループ ID（最上位なので 0 が 16 バイト）、Fill の値の数 = 0、調整の種類 = 0（反転）、アルゴリズム版 = 1 が続く並びを探す。
            var pattern = new List<byte>(BitConverter.GetBytes((int)LayerKind.Adjustment)); pattern.AddRange(new byte[16]);
            foreach (int v in new[] { 0, (int)AdjustmentType.Invert, AdjustmentSettings.AlgorithmVersion }) pattern.AddRange(BitConverter.GetBytes(v));
            for (int i = 0; i + pattern.Count <= bytes.Length; i++)
                if (bytes.Skip(i).Take(pattern.Count).SequenceEqual(pattern)) return i + 24; // 調整の種類の位置
            throw new InvalidOperationException("adjustment block not found");
        }

        /// <summary>PSD に書けるのは、PSD の刻みにちょうど乗る調整だけ。間の値は丸めずに断る（往復の詳細は PsdAdjustmentTests）。</summary>
        [Test] public void PsdProjectionWritesExactAdjustmentsAndRefusesSettingsBetweenPsdSteps()
        {
            var doc = OnePixel(new Rgba32(1, 2, 3, 255), out _); doc.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert());
            Assert.That(PsdBridge.Export(doc, PaintChannel.Color).Layers[0].Adjustment.Type, Is.EqualTo(AdjustmentType.Invert));
            var between = OnePixel(new Rgba32(1, 2, 3, 255), out _); between.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels(0.3, 1));
            Assert.That(() => PsdBridge.Export(between, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("between them"));
        }
    }
}
