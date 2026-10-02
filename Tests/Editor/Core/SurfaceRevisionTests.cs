using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>面の書き換え番号（SparseTileSurface.Revision / TileRevision）。合成器の GPU の写しの検証に使うので、画素が変わる
    /// 経路すべてで番号が進み、通知を聞く人がいなくても数え、文書の変更記録はそのまま動くこと。</summary>
    public sealed class SurfaceRevisionTests
    {
        static readonly BrushSettings Brush = new BrushSettings { Radius = 3, Hardness = 1, Color = new Rgba32(10, 200, 30), PressureSize = false, PressureOpacity = false };

        [Test] public void EverySurfaceHasItsOwnId()
        {
            var doc = new PaintDocument(64, 64, 16); var a = doc.AddLayer("A"); var b = doc.AddLayer("B");
            long id = a.GetChannel(PaintChannel.Color).Id;
            Assert.That(a.GetChannel(PaintChannel.Color).Id, Is.EqualTo(id));
            Assert.That(b.GetChannel(PaintChannel.Color).Id, Is.Not.EqualTo(id));
            Assert.That(a.GetChannel(PaintChannel.Roughness).Id, Is.Not.EqualTo(id), "each channel has its own surface");
        }

        [Test] public void EveryPixelPathAdvancesTheRevisionOfTheTilesItTouches()
        {
            var doc = new PaintDocument(64, 64, 16); var layer = doc.AddLayer("A"); doc.ClearHistory();
            var surface = layer.GetChannel(PaintChannel.Color);
            Assert.That(surface.Revision, Is.Zero); Assert.That(surface.TileRevision(new TileCoord(0, 0)), Is.Zero);
            long last = surface.Revision;
            void Advanced(string path, TileCoord coord)
            {
                Assert.That(surface.Revision, Is.GreaterThan(last), path);
                Assert.That(surface.TileRevision(coord), Is.EqualTo(surface.Revision), path + ": the tile carries the latest revision");
                last = surface.Revision;
            }
            using (var s = doc.BeginStroke(layer.Id, PaintChannel.Color, Brush)) { s.Add(new BrushSample(8, 8)); Advanced("stroke preview", new TileCoord(0, 0)); s.Commit(); }
            Assert.That(doc.Undo()); Advanced("undo", new TileCoord(0, 0));
            Assert.That(doc.Redo()); Advanced("redo", new TileCoord(0, 0));
            using (var s = doc.BeginStroke(layer.Id, PaintChannel.Color, Brush)) { s.Add(new BrushSample(40, 40)); s.Cancel(); }
            Advanced("cancelled stroke restores", new TileCoord(2, 2));
            surface.SetPixel(50, 10, new Rgba32(1, 2, 3, 4)); Advanced("SetPixel", new TileCoord(3, 0));
            surface.ImportTile(new TileCoord(1, 3), new byte[16 * 16 * 4].Fill(77)); Advanced("ImportTile", new TileCoord(1, 3));
            doc.Transform(layer.Id, Affine2D.Translation(16, 0)); Advanced("transform", new TileCoord(2, 3));
            Assert.That(surface.HasTile(new TileCoord(2, 3)), Is.True);
            long before = surface.TileRevision(new TileCoord(2, 3));
            surface.Clear(); Assert.That(surface.TileRevision(new TileCoord(2, 3)), Is.GreaterThan(before), "Clear touches every tile it removes");
            Assert.That(surface.MaxTileRevision(0, 0, 4, 4), Is.EqualTo(surface.Revision));
            Assert.That(surface.MaxTileRevision(0, 1, 1, 2), Is.Zero, "a tile never touched");
        }

        [Test] public void TheDocumentJournalStillWorks()
        {
            var doc = new PaintDocument(64, 64, 16); var layer = doc.AddLayer("A"); doc.ClearHistory();
            long serial = doc.ChangeSerial, revision = layer.GetChannel(PaintChannel.Color).Revision;
            using (var s = doc.BeginStroke(layer.Id, PaintChannel.Color, Brush)) { s.Add(new BrushSample(40, 8)); s.Commit(); }
            var changed = new List<TileCoord>();
            Assert.That(doc.TryGetChangedTiles(PaintChannel.Color, serial, changed));
            Assert.That(changed, Does.Contain(new TileCoord(2, 0)));
            Assert.That(layer.GetChannel(PaintChannel.Color).Revision, Is.GreaterThan(revision));
        }

        [Test] public void MasksCountLikeLayers()
        {
            var doc = new PaintDocument(64, 64, 16); var layer = doc.AddLayer("A"); var mask = doc.AddLayerMask(layer.Id); doc.ClearHistory();
            using (var s = doc.BeginMaskStroke(layer.Id, Brush)) { s.Add(new BrushSample(20, 20)); s.Commit(); }
            Assert.That(mask.Surface.TileRevision(new TileCoord(1, 1)), Is.GreaterThan(0));
        }

        /// <summary>通知を誰が付け替えても数え漏れない（番号は面そのものが持つ）。</summary>
        [Test] public void CountingDoesNotDependOnTheNotification()
        {
            var surface = new SparseTileSurface(32, 32, 16);
            surface.SetPixel(1, 1, new Rgba32(9, 9, 9, 9));
            Assert.That(surface.Revision, Is.EqualTo(1), "no listener at all");
            var field = typeof(SparseTileSurface).GetField("TileChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            int heard = 0; field.SetValue(surface, (Action<TileCoord>)(c => heard++));
            surface.SetPixel(20, 20, new Rgba32(9, 9, 9, 9));
            Assert.That(surface.Revision, Is.EqualTo(2)); Assert.That(heard, Is.EqualTo(1));
            Assert.That(surface.TileRevision(new TileCoord(1, 1)), Is.EqualTo(2));
        }
    }

    static class ByteArrayExtensions
    {
        public static byte[] Fill(this byte[] bytes, byte value) { for (int i = 0; i < bytes.Length; i++) bytes[i] = value; return bytes; }
    }
}
