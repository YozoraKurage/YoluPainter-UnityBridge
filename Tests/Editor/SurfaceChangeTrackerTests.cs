using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>面の書き換え番号（SurfaceChangeTracker）。合成器の GPU の写しの検証に使うので、画素が変わる経路すべてで番号が進み、
    /// 文書の変更記録はそのまま動くこと。</summary>
    public sealed class SurfaceChangeTrackerTests
    {
        static readonly BrushSettings Brush = new BrushSettings { Radius = 3, Hardness = 1, Color = new Rgba32(10, 200, 30), PressureSize = false, PressureOpacity = false };

        [Test] public void OneTrackerPerSurfaceWithDistinctIds()
        {
            var doc = new PaintDocument(64, 64, 16); var a = doc.AddLayer("A"); var b = doc.AddLayer("B");
            var ta = SurfaceChangeTracker.For(a.GetChannel(PaintChannel.Color));
            Assert.That(SurfaceChangeTracker.For(a.GetChannel(PaintChannel.Color)), Is.SameAs(ta));
            Assert.That(SurfaceChangeTracker.For(b.GetChannel(PaintChannel.Color)).Id, Is.Not.EqualTo(ta.Id));
            Assert.That(SurfaceChangeTracker.For(a.GetChannel(PaintChannel.Roughness)).Id, Is.Not.EqualTo(ta.Id), "each channel has its own surface");
            Assert.That(() => SurfaceChangeTracker.For(null), Throws.ArgumentNullException);
        }

        [Test] public void EveryPixelPathAdvancesTheRevisionOfTheTilesItTouches()
        {
            var doc = new PaintDocument(64, 64, 16); var layer = doc.AddLayer("A"); doc.ClearHistory();
            var surface = layer.GetChannel(PaintChannel.Color);
            var t = SurfaceChangeTracker.For(surface);
            Assert.That(t.Revision, Is.Zero); Assert.That(t.TileRevision(new TileCoord(0, 0)), Is.Zero);
            long last = t.Revision;
            void Advanced(string path, TileCoord coord)
            {
                Assert.That(t.Revision, Is.GreaterThan(last), path);
                Assert.That(t.TileRevision(coord), Is.EqualTo(t.Revision), path + ": the tile carries the latest revision");
                last = t.Revision;
            }
            using (var s = doc.BeginStroke(layer.Id, PaintChannel.Color, Brush)) { s.Add(new BrushSample(8, 8)); Advanced("stroke preview", new TileCoord(0, 0)); s.Commit(); }
            Assert.That(doc.Undo()); Advanced("undo", new TileCoord(0, 0));
            Assert.That(doc.Redo()); Advanced("redo", new TileCoord(0, 0));
            using (var s = doc.BeginStroke(layer.Id, PaintChannel.Color, Brush)) { s.Add(new BrushSample(40, 40)); s.Cancel(); }
            Advanced("cancelled stroke restores", new TileCoord(2, 2));
            surface.SetPixel(50, 10, new Rgba32(1, 2, 3, 4)); Advanced("SetPixel", new TileCoord(3, 0));
            surface.ImportTile(new TileCoord(1, 3), new byte[16 * 16 * 4].Fill(77)); Advanced("ImportTile", new TileCoord(1, 3));
            long before = t.TileRevision(new TileCoord(0, 0));
            surface.Clear(); Assert.That(t.TileRevision(new TileCoord(0, 0)), Is.GreaterThan(before), "Clear touches every tile it removes");
            last = t.Revision;
            Assert.That(t.MaxTileRevision(0, 0, 4, 4), Is.EqualTo(t.Revision));
            Assert.That(t.MaxTileRevision(0, 1, 1, 2), Is.Zero, "a tile never touched since tracking began");
        }

        [Test] public void TheDocumentJournalStillWorksAfterTrackingBegins()
        {
            var doc = new PaintDocument(64, 64, 16); var layer = doc.AddLayer("A"); doc.ClearHistory();
            SurfaceChangeTracker.For(layer.GetChannel(PaintChannel.Color));
            long serial = doc.ChangeSerial;
            using (var s = doc.BeginStroke(layer.Id, PaintChannel.Color, Brush)) { s.Add(new BrushSample(40, 8)); s.Commit(); }
            var changed = new List<TileCoord>();
            Assert.That(doc.TryGetChangedTiles(PaintChannel.Color, serial, changed));
            Assert.That(changed, Does.Contain(new TileCoord(2, 0)));
        }

        [Test] public void MasksAreTrackedLikeLayers()
        {
            var doc = new PaintDocument(64, 64, 16); var layer = doc.AddLayer("A"); var mask = doc.AddLayerMask(layer.Id); doc.ClearHistory();
            var t = SurfaceChangeTracker.For(mask.Surface);
            using (var s = doc.BeginMaskStroke(layer.Id, Brush)) { s.Add(new BrushSample(20, 20)); s.Commit(); }
            Assert.That(t.TileRevision(new TileCoord(1, 1)), Is.GreaterThan(0));
        }

        /// <summary>通知が後から付け替えられたら、世代を進めて付け直す（その間の書き換えは数えられないので、使う側は全部変わったとみなす）。</summary>
        [Test] public void AReplacedNotificationIsDetectedAndReattached()
        {
            var doc = new PaintDocument(64, 64, 16); var layer = doc.AddLayer("A"); doc.ClearHistory();
            var surface = layer.GetChannel(PaintChannel.Color);
            var t = SurfaceChangeTracker.For(surface);
            var field = typeof(SparseTileSurface).GetField("TileChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            var original = (Action<TileCoord>)field.GetValue(surface);
            field.SetValue(surface, (Action<TileCoord>)(c => original(c)));
            Assert.That(SurfaceChangeTracker.For(surface), Is.SameAs(t));
            Assert.That(t.Generation, Is.EqualTo(1));
            long before = t.Revision;
            surface.SetPixel(1, 1, new Rgba32(9, 9, 9, 9));
            Assert.That(t.Revision, Is.GreaterThan(before), "counting resumes after reattaching");
        }
    }

    static class ByteArrayExtensions
    {
        public static byte[] Fill(this byte[] bytes, byte value) { for (int i = 0; i < bytes.Length; i++) bytes[i] = value; return bytes; }
    }
}
