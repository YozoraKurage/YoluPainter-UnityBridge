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

        [Test] public void StructureChangesReportOnlyTheAffectedLayersTiles()
        {
            var doc = new PaintDocument(64, 64, 16); var a = doc.AddLayer("A").Id; var b = doc.AddLayer("B").Id; doc.ClearHistory();
            Dot(doc, a, 3, 3); Dot(doc, b, 40, 40); Dot(doc, b, 40, 40, PaintChannel.Roughness);
            var aTiles = new[] { new TileCoord(0, 0) }; var bTiles = new[] { new TileCoord(2, 2) };
            var edits = new (string, Action, TileCoord[], TileCoord[])[]
            {
                // 名前, 操作, Color で変わったと報告されるべきタイル, Roughness で報告されるべきタイル
                ("visibility", () => doc.SetLayerVisibility(a, false), aTiles, new TileCoord[0]),
                ("opacity", () => doc.SetLayerOpacity(a, .5), aTiles, new TileCoord[0]),
                ("blend", () => doc.SetLayerBlendMode(b, LayerBlendMode.Screen), bTiles, bTiles),
                ("move", () => doc.MoveLayer(a, 1), aTiles, new TileCoord[0]),
                ("channel enable is per channel", () => doc.SetChannelEnabled(b, PaintChannel.Roughness, false), new TileCoord[0], bTiles),
                ("add empty layer", () => doc.AddLayer("C"), new TileCoord[0], new TileCoord[0]),
                ("remove", () => doc.RemoveLayer(b), bTiles, bTiles),
                ("undo of remove", () => doc.Undo(), bTiles, bTiles),
                ("external pixel", () => doc.GetLayer(a).GetChannel(PaintChannel.Color).SetPixel(9, 9, new Rgba32(1, 2, 3)), aTiles, new TileCoord[0]),
                ("external clear", () => doc.GetLayer(b).GetChannel(PaintChannel.Color).Clear(), bTiles, new TileCoord[0]),
                ("rename", () => doc.SetLayerName(a, "renamed"), new TileCoord[0], new TileCoord[0]),
            };
            foreach (var (name, edit, color, roughness) in edits)
            {
                long since = doc.ChangeSerial; edit();
                CollectionAssert.AreEquivalent(color, Changed(doc, PaintChannel.Color, since), name + " (Color)");
                CollectionAssert.AreEquivalent(roughness, Changed(doc, PaintChannel.Roughness, since), name + " (Roughness)");
            }
        }

        /// <summary>A clipping mark has no effect on the bottom layer. Moving a marked layer to the bottom (or another layer under it)
        /// changes where it shows, so its tiles are reported both ways — also when the layer itself did not move.</summary>
        [Test] public void AClippingMarkThatStartsOrStopsApplyingReportsTheLayersTiles()
        {
            var doc = new PaintDocument(64, 32, 16);
            var b = doc.AddLayer("B"); var a = doc.AddLayer("A");
            Dot(doc, b.Id, 20, 5); Dot(doc, a.Id, 3, 3); // B in tile (1, 0), A in tile (0, 0)
            doc.SetLayerClipping(a.Id, true); doc.ClearHistory();
            void Step(string name, Action edit)
            {
                var before = doc.Composite(PaintChannel.Color); long since = doc.ChangeSerial; edit();
                var after = doc.Composite(PaintChannel.Color); var changed = Changed(doc, PaintChannel.Color, since);
                for (int i = 0; i < after.Length; i += 4)
                    if (after[i] != before[i] || after[i + 1] != before[i + 1] || after[i + 2] != before[i + 2] || after[i + 3] != before[i + 3])
                    {
                        int p = i / 4; var tile = new TileCoord(p % 64 / 16, p / 64 / 16);
                        Assert.That(changed, Does.Contain(tile), name + ": the composite changed in tile " + tile + " but it was not reported");
                    }
                Assert.That(after, Is.Not.EqualTo(before), name + ": the step changes the composite");
            }
            Step("B moves above A: A is the bottom layer and shows unclipped", () => doc.MoveLayer(b.Id, 1));
            Step("undo: A is clipped to B again", () => doc.Undo());
            Step("redo", () => doc.Redo());
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
