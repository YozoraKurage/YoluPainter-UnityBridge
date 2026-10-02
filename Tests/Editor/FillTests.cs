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
    /// <summary>Fill レイヤー: 値を正本にして全面を覆う。画素を確保しない。マスクで範囲を決める。</summary>
    public sealed class FillTests
    {
        static readonly Rgba32 Green = new Rgba32(30, 180, 60, 255);
        static Dictionary<PaintChannel, Rgba32> Values(PaintChannel channel, Rgba32 value) => new Dictionary<PaintChannel, Rgba32> { { channel, value } };
        static BrushSettings Hard(bool erase = false) => new BrushSettings { Radius = 1, Hardness = 1, PressureSize = false, PressureOpacity = false, Erase = erase };

        [Test] public void FillCoversTheWholeCanvasWithoutAllocatingPixels()
        {
            var doc = new PaintDocument(4096, 4096); var fill = doc.AddFillLayer("F", Values(PaintChannel.Color, Green));
            Assert.That(doc.AllocatedBytes, Is.Zero);
            Assert.That(doc.CompositePixel(PaintChannel.Color, 0, 0), Is.EqualTo(Green));
            Assert.That(doc.CompositePixel(PaintChannel.Color, 4095, 4095), Is.EqualTo(Green));
            Assert.That(doc.CompositePixel(PaintChannel.Roughness, 10, 10).A, Is.Zero, "channels without a value stay empty");
            Assert.That(fill.Kind, Is.EqualTo(LayerKind.Fill)); Assert.That(fill.Channels, Is.Empty);
            Assert.That(fill.EnumerateContentTiles(PaintChannel.Color).Count(), Is.EqualTo(32 * 32));
        }

        [Test] public void FillTilesAreGeneratedWithZeroPaddingAtTheEdges()
        {
            var doc = new PaintDocument(20, 12, 8); var fill = doc.AddFillLayer("F", Values(PaintChannel.Color, Green));
            var buffer = Enumerable.Repeat((byte)7, 8 * 8 * 4).ToArray();
            Assert.That(fill.CopyTile(PaintChannel.Color, new TileCoord(2, 1), buffer), Is.True);
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            {
                int i = (y * 8 + x) * 4; bool inside = 16 + x < 20 && 8 + y < 12;
                Assert.That(new Rgba32(buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]), Is.EqualTo(inside ? Green : Rgba32.Transparent), $"({x},{y})");
            }
            Assert.That(fill.CopyTile(PaintChannel.Metallic, new TileCoord(0, 0), buffer), Is.False);
            Assert.That(buffer.All(b => b == 0), Is.True);
        }

        [Test] public void AnInvertedMaskLimitsTheFillToWhereItIsPainted()
        {
            var doc = new PaintDocument(16, 16, 8); var fill = doc.AddFillLayer("Metal", Values(PaintChannel.Metallic, new Rgba32(255, 255, 255)));
            doc.AddLayerMask(fill.Id); doc.SetLayerMaskInverted(fill.Id, true);
            Assert.That(doc.CompositePixel(PaintChannel.Metallic, 5, 5).A, Is.Zero, "an inverted empty mask hides the fill everywhere");
            using (var s = doc.BeginMaskStroke(fill.Id, Hard())) { s.ApplyPixel(5, 5, 1); s.Commit(); }
            Assert.That(doc.CompositePixel(PaintChannel.Metallic, 5, 5), Is.EqualTo(new Rgba32(255, 255, 255)));
            Assert.That(doc.CompositePixel(PaintChannel.Metallic, 6, 5).A, Is.Zero);
        }

        [Test] public void FillLayersCannotBePaintedDirectly()
        {
            var doc = new PaintDocument(16, 16, 8); var fill = doc.AddFillLayer("F", Values(PaintChannel.Color, Green));
            Assert.Throws<InvalidOperationException>(() => doc.BeginStroke(fill.Id, PaintChannel.Color, Hard()));
            Assert.Throws<InvalidOperationException>(() => fill.GetChannel(PaintChannel.Color));
            Assert.Throws<InvalidOperationException>(() => doc.SetFillValue(doc.AddLayer("raster").Id, PaintChannel.Color, Green));
        }

        [Test] public void FillValueChangesAreUndoableAndTrackedPerChannel()
        {
            var doc = new PaintDocument(32, 16, 16); var fill = doc.AddFillLayer("F", Values(PaintChannel.Color, Green)); doc.ClearHistory();
            var red = new Rgba32(200, 0, 0);
            long since = doc.ChangeSerial;
            doc.SetFillValue(fill.Id, PaintChannel.Color, red);
            Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(red));
            var changed = new HashSet<TileCoord>(); doc.TryGetChangedTiles(PaintChannel.Color, since, changed);
            CollectionAssert.AreEquivalent(new[] { new TileCoord(0, 0), new TileCoord(1, 0) }, changed, "a fill value covers every canvas tile");
            var other = new HashSet<TileCoord>(); doc.TryGetChangedTiles(PaintChannel.Roughness, since, other);
            Assert.That(other, Is.Empty, "changing one channel's value does not touch the others");
            doc.SetFillValue(fill.Id, PaintChannel.Roughness, new Rgba32(128, 128, 128));
            doc.SetChannelEnabled(fill.Id, PaintChannel.Roughness, false);
            Assert.That(doc.CompositePixel(PaintChannel.Roughness, 1, 1).A, Is.Zero, "a disabled channel keeps its value but shows nothing");
            doc.SetFillValue(fill.Id, PaintChannel.Color, null);
            Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1).A, Is.Zero);
            doc.Undo(); Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(red));
            doc.Undo(); doc.Undo(); doc.Undo(); Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(Green));
            Assert.That(fill.FillValues.ContainsKey(PaintChannel.Roughness), Is.False);
        }

        [TestCase(5)] [TestCase(23)]
        public void CompositeWithFillsMatchesPerPixelReference(int seed)
        {
            var random = new Random(seed); var doc = new PaintDocument(41, 37, 16);
            var raster = doc.AddLayer("R").GetChannel(PaintChannel.Color);
            for (int n = 0; n < 600; n++) { var c = new byte[4]; random.NextBytes(c); raster.SetPixel(random.Next(41), random.Next(37), new Rgba32(c[0], c[1], c[2], c[3])); }
            var multiply = doc.AddFillLayer("Multiply", Values(PaintChannel.Color, new Rgba32(200, 120, 40, 180)));
            doc.SetLayerBlendMode(multiply.Id, LayerBlendMode.Multiply); doc.SetLayerOpacity(multiply.Id, .7);
            var screen = doc.AddFillLayer("Screen", Values(PaintChannel.Color, new Rgba32(10, 60, 220, 90)));
            doc.SetLayerBlendMode(screen.Id, LayerBlendMode.Screen);
            var mask = doc.AddLayerMask(screen.Id);
            for (int n = 0; n < 300; n++) mask.Surface.SetPixel(random.Next(41), random.Next(37), new Rgba32(0, 0, 0, (byte)random.Next(256)));
            doc.SetLayerMaskInverted(screen.Id, true); doc.SetLayerMaskDensity(screen.Id, .8);
            var reference = new byte[41 * 37 * 4];
            for (int y = 0; y < 37; y++) for (int x = 0; x < 41; x++)
            { var p = doc.CompositePixel(PaintChannel.Color, x, y); int i = (y * 41 + x) * 4; reference[i] = p.R; reference[i + 1] = p.G; reference[i + 2] = p.B; reference[i + 3] = p.A; }
            Assert.That(doc.Composite(PaintChannel.Color), Is.EqualTo(reference));
        }

        [Test] public void NativeArchiveRoundTripsFillLayers()
        {
            var doc = new PaintDocument(16, 16, 8);
            var fill = doc.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, Green }, { PaintChannel.Height, new Rgba32(90, 90, 90) } });
            doc.SetChannelEnabled(fill.Id, PaintChannel.Height, false);
            doc.AddLayerMask(fill.Id); using (var s = doc.BeginMaskStroke(fill.Id, Hard())) { s.ApplyPixel(3, 3, 1); s.Commit(); }
            doc.AddLayer("Paint").GetChannel(PaintChannel.Color).SetPixel(1, 1, new Rgba32(1, 2, 3));
            var bytes = DocumentBinary.Write(doc); var restored = DocumentBinary.Read(bytes);
            Assert.That(DocumentBinary.Write(restored), Is.EqualTo(bytes));
            var r = restored.GetLayer(fill.Id);
            Assert.That(r.Kind, Is.EqualTo(LayerKind.Fill));
            Assert.That(r.FillValues[PaintChannel.Color], Is.EqualTo(Green));
            Assert.That(r.IsChannelEnabled(PaintChannel.Height), Is.False); Assert.That(r.FillValues[PaintChannel.Height], Is.EqualTo(new Rgba32(90, 90, 90)));
            Assert.That(restored.Composite(PaintChannel.Color), Is.EqualTo(doc.Composite(PaintChannel.Color)));
        }

        [Test] public void NativeReaderStillLoadsVersion2Archives()
        {
            // 版 2: 種類と Fill の値が無く、各レイヤーの最後にマスクのブロックがある。
            var layerId = Guid.NewGuid(); var tile = new byte[8 * 8 * 4]; tile[3] = 255; var maskTile = new byte[8 * 8 * 4]; maskTile[3] = 255;
            byte[] v2;
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8))
            {
                w.Write(Encoding.ASCII.GetBytes("DOTPAINT")); w.Write(2); w.Write(Guid.NewGuid().ToByteArray());
                w.Write(16); w.Write(16); w.Write(8); w.Write(1);
                w.Write(layerId.ToByteArray()); w.Write(1); w.Write((byte)'L');
                w.Write(true); w.Write(1.0); w.Write((int)LayerBlendMode.Normal);
                w.Write(1); w.Write((int)PaintChannel.Color); w.Write(true); w.Write(1); w.Write(0); w.Write(0); w.Write(tile.Length); w.Write(tile);
                w.Write(true); w.Write(true); w.Write(false); w.Write(1.0); w.Write(1); w.Write(0); w.Write(0); w.Write(maskTile.Length); w.Write(maskTile);
                w.Flush(); v2 = stream.ToArray();
            }
            var doc = DocumentBinary.Read(v2);
            var layer = doc.GetLayer(layerId);
            Assert.That(layer.Kind, Is.EqualTo(LayerKind.Raster)); Assert.That(layer.Mask, Is.Not.Null);
            Assert.That(doc.CompositePixel(PaintChannel.Color, 0, 0).A, Is.Zero, "the version 2 mask hides the pixel");
        }

        [Test] public void PsdProjectionRefusesFillLayers()
        {
            var doc = new PaintDocument(16, 16, 8); doc.AddFillLayer("F", Values(PaintChannel.Color, Green));
            Assert.Throws<InvalidOperationException>(() => PsdBridge.Export(doc, PaintChannel.Color));
        }
    }
}
