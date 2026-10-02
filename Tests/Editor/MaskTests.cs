using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>レイヤーのラスターマスク: 隠す・見せる・反転・濃度、Undo、予算、変更追跡、合成、保存形式。</summary>
    public sealed class MaskTests
    {
        static readonly Rgba32 Red = new Rgba32(220, 20, 30);

        /// <summary>16x16（タイル 8）で、Color を全面不透明の赤にしたレイヤー 1 枚。</summary>
        static PaintDocument FullRedLayer(out Guid layer)
        {
            var doc = new PaintDocument(16, 16, 8); var l = doc.AddLayer("Paint"); layer = l.Id;
            var surface = l.GetChannel(PaintChannel.Color);
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) surface.SetPixel(x, y, Red);
            doc.ClearHistory(); return doc;
        }

        static BrushSettings Hard(bool erase = false) => new BrushSettings { Radius = 1, Hardness = 1, Color = new Rgba32(255, 0, 255), PressureSize = false, PressureOpacity = false, Erase = erase };

        static void MaskPixel(PaintDocument doc, Guid layer, int x, int y, bool erase = false)
        { using (var s = doc.BeginMaskStroke(layer, Hard(erase))) { s.ApplyPixel(x, y, 1); s.Commit(); } }

        static byte Alpha(PaintDocument doc, int x, int y) => doc.CompositePixel(PaintChannel.Color, x, y).A;

        [Test] public void MaskHidesAndInvertsAccordingToDensityAndEnabled()
        {
            var doc = FullRedLayer(out var layer); doc.AddLayerMask(layer);
            Assert.That(Alpha(doc, 3, 3), Is.EqualTo(255), "a new mask reveals everything");
            MaskPixel(doc, layer, 3, 3);
            Assert.That(Alpha(doc, 3, 3), Is.EqualTo(0)); Assert.That(Alpha(doc, 10, 10), Is.EqualTo(255));
            Assert.That(doc.CompositePixel(PaintChannel.Color, 10, 10), Is.EqualTo(Red), "colour of revealed pixels is untouched");
            doc.SetLayerMaskDensity(layer, .5);
            Assert.That(Alpha(doc, 3, 3), Is.EqualTo(128));
            doc.SetLayerMaskInverted(layer, true);
            Assert.That(Alpha(doc, 3, 3), Is.EqualTo(255)); Assert.That(Alpha(doc, 10, 10), Is.EqualTo(128));
            doc.SetLayerMaskEnabled(layer, false);
            Assert.That(Alpha(doc, 3, 3), Is.EqualTo(255)); Assert.That(Alpha(doc, 10, 10), Is.EqualTo(255));
        }

        [Test] public void MaskStrokesChangeOnlyTheMaskAndUndoExactly()
        {
            var doc = FullRedLayer(out var layer); var mask = doc.AddLayerMask(layer); doc.ClearHistory();
            var colorTiles = doc.GetLayer(layer).GetChannel(PaintChannel.Color).EnumerateTiles().Select(t => t.Bytes).ToArray();
            MaskPixel(doc, layer, 5, 5);
            Assert.That(mask.Surface.TileCount, Is.EqualTo(1));
            Assert.That(mask.Surface.GetPixel(5, 5), Is.EqualTo(new Rgba32(0, 0, 0, 255)), "the brush colour is ignored; only the hide amount is stored");
            Assert.That(doc.GetLayer(layer).GetChannel(PaintChannel.Color).EnumerateTiles().Select(t => t.Bytes), Is.EqualTo(colorTiles));
            Assert.That(doc.Undo(), Is.True); Assert.That(mask.Surface.TileCount, Is.Zero); Assert.That(Alpha(doc, 5, 5), Is.EqualTo(255));
            Assert.That(doc.Redo(), Is.True); Assert.That(Alpha(doc, 5, 5), Is.EqualTo(0));
            MaskPixel(doc, layer, 5, 5, erase: true);
            Assert.That(Alpha(doc, 5, 5), Is.EqualTo(255), "erasing the mask reveals");
            using (var s = doc.BeginMaskStroke(layer, Hard())) { s.ApplyPixel(9, 9, 1); s.Cancel(); }
            Assert.That(Alpha(doc, 9, 9), Is.EqualTo(255), "a cancelled mask stroke leaves nothing");
        }

        [Test] public void AddingAndRemovingAMaskIsUndoable()
        {
            var doc = FullRedLayer(out var layer); var mask = doc.AddLayerMask(layer); MaskPixel(doc, layer, 2, 2);
            doc.SetLayerMaskDensity(layer, .75);
            doc.RemoveLayerMask(layer);
            Assert.That(doc.GetLayer(layer).Mask, Is.Null); Assert.That(Alpha(doc, 2, 2), Is.EqualTo(255));
            doc.Undo();
            Assert.That(doc.GetLayer(layer).Mask, Is.SameAs(mask)); Assert.That(mask.Density, Is.EqualTo(.75));
            Assert.That(Alpha(doc, 2, 2), Is.EqualTo(64));
            Assert.Throws<InvalidOperationException>(() => doc.AddLayerMask(layer), "one mask per layer");
            doc.Undo(); doc.Undo(); doc.Undo(); // density, mask stroke, add mask
            Assert.That(doc.GetLayer(layer).Mask, Is.Null);
            Assert.Throws<InvalidOperationException>(() => doc.BeginMaskStroke(layer, Hard()));
        }

        [Test] public void MaskPixelsCountAgainstTheSourceBudget()
        {
            var doc = FullRedLayer(out var layer); doc.AddLayerMask(layer);
            long before = doc.AllocatedBytes;
            MaskPixel(doc, layer, 1, 1);
            Assert.That(doc.AllocatedBytes, Is.EqualTo(before + 8 * 8 * 4));
            doc.SourceBudgetBytes = doc.AllocatedBytes; // もう 1 タイルも増やせない
            Assert.Throws<InvalidOperationException>(() => MaskPixel(doc, layer, 12, 12));
            Assert.That(Alpha(doc, 12, 12), Is.EqualTo(255), "a refused mask stroke is rolled back");
        }

        [Test] public void MaskChangesAreTrackedForEveryChannelTheLayerHas()
        {
            var doc = new PaintDocument(32, 32, 16); var layer = doc.AddLayer("A");
            layer.GetChannel(PaintChannel.Color).SetPixel(1, 1, Red); layer.GetChannel(PaintChannel.Roughness).SetPixel(20, 20, Red);
            doc.AddLayerMask(layer.Id); doc.ClearHistory();
            long since = doc.ChangeSerial; MaskPixel(doc, layer.Id, 20, 3);
            foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Roughness })
            {
                var changed = new HashSet<TileCoord>(); Assert.That(doc.TryGetChangedTiles(channel, since, changed), Is.True);
                CollectionAssert.AreEquivalent(new[] { new TileCoord(1, 0) }, changed, channel.ToString());
            }
            var metallic = new HashSet<TileCoord>(); doc.TryGetChangedTiles(PaintChannel.Metallic, since, metallic);
            Assert.That(metallic, Is.Empty, "a channel the layer does not have cannot change");
            since = doc.ChangeSerial; doc.SetLayerMaskInverted(layer.Id, true);
            var color = new HashSet<TileCoord>(); doc.TryGetChangedTiles(PaintChannel.Color, since, color);
            CollectionAssert.AreEquivalent(new[] { new TileCoord(0, 0) }, color, "a mask parameter only affects the layer's own pixels");
        }

        [TestCase(3)] [TestCase(17)]
        public void TileWiseCompositeWithMasksMatchesPerPixelReference(int seed)
        {
            var random = new Random(seed); var doc = new PaintDocument(45, 38, 16);
            for (int l = 0; l < 4; l++)
            {
                var layer = doc.AddLayer("L" + l); var surface = layer.GetChannel(PaintChannel.Color);
                for (int n = 0; n < 500; n++) { var c = new byte[4]; random.NextBytes(c); surface.SetPixel(random.Next(45), random.Next(38), new Rgba32(c[0], c[1], c[2], c[3])); }
                var mask = doc.AddLayerMask(layer.Id);
                for (int n = 0; n < 200; n++) mask.Surface.SetPixel(random.Next(45), random.Next(38), new Rgba32(0, 0, 0, (byte)random.Next(256)));
                doc.SetLayerMaskDensity(layer.Id, Math.Round(random.NextDouble(), 3));
                doc.SetLayerMaskInverted(layer.Id, l % 2 == 1);
                doc.SetLayerBlendMode(layer.Id, (LayerBlendMode)(l % 3));
            }
            doc.SetLayerMaskEnabled(doc.Layers[2].Id, false);
            var reference = new byte[45 * 38 * 4];
            for (int y = 0; y < 38; y++) for (int x = 0; x < 45; x++)
            { var p = doc.CompositePixel(PaintChannel.Color, x, y); int i = (y * 45 + x) * 4; reference[i] = p.R; reference[i + 1] = p.G; reference[i + 2] = p.B; reference[i + 3] = p.A; }
            Assert.That(doc.Composite(PaintChannel.Color), Is.EqualTo(reference));
        }

        [Test] public void NativeArchiveRoundTripsMasks()
        {
            var doc = FullRedLayer(out var layer); doc.AddLayerMask(layer); MaskPixel(doc, layer, 4, 6);
            doc.SetLayerMaskInverted(layer, true); doc.SetLayerMaskDensity(layer, .3); doc.SetLayerMaskEnabled(layer, false);
            doc.AddLayer("No mask");
            var bytes = DocumentBinary.Write(doc); var restored = DocumentBinary.Read(bytes);
            Assert.That(DocumentBinary.Write(restored), Is.EqualTo(bytes));
            var mask = restored.GetLayer(layer).Mask;
            Assert.That(mask.Inverted, Is.True); Assert.That(mask.Density, Is.EqualTo(.3)); Assert.That(mask.Enabled, Is.False);
            Assert.That(mask.Surface.GetPixel(4, 6).A, Is.EqualTo(255));
            Assert.That(restored.Layers[1].Mask, Is.Null);
            Assert.That(restored.CanUndo, Is.False, "a freshly read document has no history");
        }

        [Test] public void NativeReaderStillLoadsVersion1Archives()
        {
            // 版 1: マスクのブロックが無い。手で組み立てて、今の読み込みで開けることを確かめる。
            var id = Guid.NewGuid(); var layerId = Guid.NewGuid();
            var tile = new byte[8 * 8 * 4]; tile[0] = 10; tile[1] = 20; tile[2] = 30; tile[3] = 255;
            byte[] v1;
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8))
            {
                w.Write(Encoding.ASCII.GetBytes("DOTPAINT")); w.Write(1); w.Write(id.ToByteArray());
                w.Write(16); w.Write(16); w.Write(8); w.Write(1);
                w.Write(layerId.ToByteArray()); var name = Encoding.UTF8.GetBytes("old"); w.Write(name.Length); w.Write(name);
                w.Write(true); w.Write(1.0); w.Write((int)LayerBlendMode.Normal);
                w.Write(1); w.Write((int)PaintChannel.Color); w.Write(true); w.Write(1); w.Write(0); w.Write(0); w.Write(tile.Length); w.Write(tile);
                w.Flush(); v1 = stream.ToArray();
            }
            var doc = DocumentBinary.Read(v1);
            Assert.That(doc.Id, Is.EqualTo(id)); Assert.That(doc.GetLayer(layerId).Name, Is.EqualTo("old"));
            Assert.That(doc.GetLayer(layerId).Mask, Is.Null);
            Assert.That(doc.CompositePixel(PaintChannel.Color, 0, 0), Is.EqualTo(new Rgba32(10, 20, 30, 255)));
        }

        [Test] public void NativeReaderRefusesMaskTilesWithColour()
        {
            var doc = FullRedLayer(out var layer); doc.AddLayerMask(layer); MaskPixel(doc, layer, 0, 0);
            var bytes = DocumentBinary.Write(doc);
            // マスクのタイルはアーカイブの末尾（版 8 からはその後にパスの有無の 1 バイト、版 9 からはさらにフィルターの有無の 1 バイト）にある。その最初の画素の R を 0 以外にする。
            int maskTileStart = bytes.Length - 2 - 8 * 8 * 4;
            Assert.That(bytes[maskTileStart + 3], Is.EqualTo(255)); bytes[maskTileStart] = 1;
            Assert.Throws<InvalidDataException>(() => DocumentBinary.Read(bytes));
        }

        [Test] public void PsdProjectionRoundTripsMasksAndRefusesInversion()
        {
            var doc = FullRedLayer(out var layer); doc.AddLayerMask(layer);
            MaskPixel(doc, layer, 1, 1); doc.SetLayerMaskDensity(layer, 128 / 255.0);
            foreach (bool enabled in new[] { true, false })
            {
                doc.SetLayerMaskEnabled(layer, enabled);
                var read = Yozolab.YoluPainter.Core.Psd.PsdCodec.Read(Yozolab.YoluPainter.Core.Psd.PsdCodec.Write(PsdBridge.Export(doc, PaintChannel.Color)));
                Assert.That(read.Mode, Is.EqualTo(Yozolab.YoluPainter.Core.Psd.PsdCompatibilityMode.EditableRaster));
                var imported = PsdBridge.Import(read); var mask = imported.Layers[0].Mask;
                Assert.That(mask.Enabled, Is.EqualTo(enabled)); Assert.That(mask.Density, Is.EqualTo(128 / 255.0));
                Assert.That(mask.Surface.GetPixel(1, 1).A, Is.EqualTo(255)); Assert.That(mask.Surface.GetPixel(2, 2).A, Is.EqualTo(0));
                Assert.That(imported.Composite(PaintChannel.Color), Is.EqualTo(doc.Composite(PaintChannel.Color)));
            }
            doc.SetLayerMaskInverted(layer, true);
            Assert.Throws<InvalidOperationException>(() => PsdBridge.Export(doc, PaintChannel.Color), "PSD has no non-destructive inversion; it is not baked into the pixels");
        }
    }
}
