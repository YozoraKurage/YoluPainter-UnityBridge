using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>合成の差分更新のための変更追跡（ChangeSerial / TryGetChangedTiles）、タイルのコピー、
    /// タイル単位の CPU 合成が 1 画素ずつの参照実装と一致すること。</summary>
    public sealed class ChangeTrackingTests
    {
        static BrushSettings Opaque() => new BrushSettings { Radius = 2, Hardness = 1, Color = new Rgba32(9, 80, 200), PressureSize = false, PressureOpacity = false };

        static void Dot(PaintDocument doc, Guid layer, int x, int y, PaintChannel channel = PaintChannel.Color)
        { using (var s = doc.BeginStroke(layer, channel, Opaque())) { s.ApplyPixel(x, y, 1); s.Commit(); } }

        static HashSet<TileCoord> Changed(PaintDocument doc, PaintChannel channel, long since)
        {
            var set = new HashSet<TileCoord>();
            Assert.That(doc.TryGetChangedTiles(channel, since, set), Is.True, "expected an incremental answer");
            return set;
        }

        [Test] public void StrokeUndoRedoAndCancelReportOnlyTouchedTiles()
        {
            var doc = new PaintDocument(64, 64, 16); var layer = doc.AddLayer("A").Id; doc.ClearHistory();
            long s0 = doc.ChangeSerial;
            Dot(doc, layer, 5, 5); Dot(doc, layer, 40, 50);
            CollectionAssert.AreEquivalent(new[] { new TileCoord(0, 0), new TileCoord(2, 3) }, Changed(doc, PaintChannel.Color, s0));
            Assert.That(Changed(doc, PaintChannel.Roughness, s0), Is.Empty, "other channels are untouched");

            long s1 = doc.ChangeSerial;
            Assert.That(Changed(doc, PaintChannel.Color, s1), Is.Empty);
            doc.Undo();
            CollectionAssert.AreEquivalent(new[] { new TileCoord(2, 3) }, Changed(doc, PaintChannel.Color, s1));
            long s2 = doc.ChangeSerial; doc.Redo();
            CollectionAssert.AreEquivalent(new[] { new TileCoord(2, 3) }, Changed(doc, PaintChannel.Color, s2));

            long s3 = doc.ChangeSerial;
            using (var s = doc.BeginStroke(layer, PaintChannel.Color, Opaque())) { s.ApplyPixel(20, 20, 1); s.Cancel(); }
            CollectionAssert.AreEquivalent(new[] { new TileCoord(1, 1) }, Changed(doc, PaintChannel.Color, s3), "a cancelled stroke must report the tiles it restored");
        }

        [Test] public void CompositeAffectingStructureChangesForceFullRecomposite()
        {
            var doc = new PaintDocument(32, 32, 16); var a = doc.AddLayer("A").Id; var b = doc.AddLayer("B").Id; doc.ClearHistory();
            Dot(doc, a, 3, 3);
            var edits = new (string, Action)[]
            {
                ("visibility", () => doc.SetLayerVisibility(a, false)),
                ("opacity", () => doc.SetLayerOpacity(a, .5)),
                ("blend", () => doc.SetLayerBlendMode(a, LayerBlendMode.Screen)),
                ("move", () => doc.MoveLayer(a, 1)),
                ("channel", () => doc.SetChannelEnabled(b, PaintChannel.Color, false)),
                ("add", () => doc.AddLayer("C")),
                ("remove", () => doc.RemoveLayer(b)),
                ("undo of structure", () => doc.Undo()),
                ("external pixel", () => doc.GetLayer(a).GetChannel(PaintChannel.Color).SetPixel(9, 9, new Rgba32(1, 2, 3))),
            };
            foreach (var (name, edit) in edits)
            {
                long since = doc.ChangeSerial; edit();
                Assert.That(doc.TryGetChangedTiles(PaintChannel.Color, since, new List<TileCoord>()), Is.False, name);
                Assert.That(doc.TryGetChangedTiles(PaintChannel.Color, doc.ChangeSerial, new List<TileCoord>()), Is.True, name + ": a fresh serial is incremental again");
            }
            long beforeRename = doc.ChangeSerial; doc.SetLayerName(a, "renamed");
            Assert.That(Changed(doc, PaintChannel.Color, beforeRename), Is.Empty, "renaming does not affect pixels");
        }

        [Test] public void SerialsFromElsewhereAreNotTrusted()
        {
            var doc = new PaintDocument(16, 16, 16); doc.AddLayer("A");
            Assert.That(doc.TryGetChangedTiles(PaintChannel.Color, -1, new List<TileCoord>()), Is.False);
            Assert.That(doc.TryGetChangedTiles(PaintChannel.Color, doc.ChangeSerial + 1, new List<TileCoord>()), Is.False);
        }

        [Test] public void CopyTileMatchesPixelsAndClearsReusedBuffers()
        {
            var surface = new SparseTileSurface(20, 20, 8);
            surface.SetPixel(17, 18, new Rgba32(10, 20, 30, 40));
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) surface.SetPixel(x, y, new Rgba32(7, 7, 7, 7)); // compacts to a uniform tile
            var buffer = Enumerable.Repeat((byte)0xAB, 8 * 8 * 4).ToArray();
            Assert.That(surface.CopyTile(new TileCoord(2, 2), buffer), Is.True);
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            {
                int i = (y * 8 + x) * 4; var expected = (16 + x < 20 && 16 + y < 20) ? surface.GetPixel(16 + x, 16 + y) : Rgba32.Transparent;
                Assert.That(new Rgba32(buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]), Is.EqualTo(expected), $"edge tile ({x},{y})");
            }
            Assert.That(surface.CopyTile(new TileCoord(0, 0), buffer), Is.True);
            Assert.That(buffer.All(v => v == 7), Is.True, "uniform tile fills the whole buffer");
            Assert.That(surface.CopyTile(new TileCoord(1, 1), buffer), Is.False);
            Assert.That(buffer.All(v => v == 0), Is.True, "absent tile clears the buffer");
            Assert.Throws<ArgumentException>(() => surface.CopyTile(new TileCoord(0, 0), new byte[4]));
            Assert.Throws<ArgumentOutOfRangeException>(() => surface.CopyTile(new TileCoord(3, 0), buffer));
        }

        [TestCase(37, 29, 8, 1)]
        [TestCase(64, 64, 16, 7)]
        [TestCase(50, 33, 16, 42)]
        public void TileWiseCompositeMatchesPerPixelReference(int width, int height, int tile, int seed)
        {
            var random = new Random(seed); var doc = new PaintDocument(width, height, tile);
            var modes = new[] { LayerBlendMode.Normal, LayerBlendMode.Multiply, LayerBlendMode.Screen };
            for (int l = 0; l < 5; l++)
            {
                var layer = doc.AddLayer("L" + l); var surface = layer.GetChannel(PaintChannel.Color);
                for (int n = 0; n < width * height / 3; n++)
                {
                    var c = new byte[4]; random.NextBytes(c);
                    surface.SetPixel(random.Next(width), random.Next(height), new Rgba32(c[0], c[1], c[2], n % 7 == 0 ? (byte)0 : c[3]));
                }
                doc.SetLayerBlendMode(layer.Id, modes[l % 3]); doc.SetLayerOpacity(layer.Id, Math.Round(random.NextDouble(), 3));
            }
            doc.SetLayerVisibility(doc.Layers[2].Id, false);
            doc.SetChannelEnabled(doc.Layers[3].Id, PaintChannel.Color, false);
            var reference = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                var p = doc.CompositePixel(PaintChannel.Color, x, y); int i = (y * width + x) * 4;
                reference[i] = p.R; reference[i + 1] = p.G; reference[i + 2] = p.B; reference[i + 3] = p.A;
            }
            Assert.That(doc.Composite(PaintChannel.Color), Is.EqualTo(reference));
            // An unaligned sub-region must match the same reference bytes.
            int rx = width / 3, ry = height / 4, rw = width / 2, rh = height / 2;
            var region = CpuCompositor.CompositeRegion(doc, PaintChannel.Color, rx, ry, rw, rh);
            for (int y = 0; y < rh; y++)
                Assert.That(region.Skip(y * rw * 4).Take(rw * 4), Is.EqualTo(reference.Skip(((ry + y) * width + rx) * 4).Take(rw * 4)), "row " + y);
        }
    }
}
