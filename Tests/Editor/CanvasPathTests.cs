using System;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>編集できる 2D の筆跡: 制御点を曲線で結んでなぞった画素がパスと一緒に変わり（1 回の Undo）、同じパスは同じ画素、パスの層は
    /// 手で塗れない。版 10 に保存し、版 9 も読む。</summary>
    public sealed class CanvasPathTests
    {
        const int Size = 64;
        static PathBrush Brush(double radius = 3) => new PathBrush { RadiusWorld = radius, Hardness = 1, Spacing = .1, Color = new Rgba32(200, 30, 30), PressureSize = false, PressureOpacity = false };
        static CanvasPath Path(params (double x, double y)[] points) => new CanvasPath(Guid.NewGuid(), PaintChannel.Color, Brush(), points.Select(p => new CanvasPoint(p.x, p.y)));
        static (PaintDocument d, PaintLayer l) Doc() { var d = new PaintDocument(Size, Size, 16); var l = d.AddLayer("path"); d.ClearHistory(); return (d, l); }
        static byte A(PaintLayer l, int x, int y) => l.GetChannel(PaintChannel.Color).GetPixel(x, y).A;

        [Test] public void TwoPointsDrawTheSameLineAsAStroke()
        {
            var (d, l) = Doc();
            d.SetCanvasPath(l.Id, Path((10.5, 20.5), (50.5, 40.5)));
            var reference = new PaintDocument(Size, Size, 16); var r = reference.AddLayer("r");
            var settings = Brush().StrokeSettings(); settings.Radius = 3;
            using (var s = reference.BeginStroke(r.Id, PaintChannel.Color, settings)) { s.Add(new BrushSample(10.5, 20.5)); s.Add(new BrushSample(50.5, 40.5)); s.Commit(); }
            var a = d.Composite(PaintChannel.Color); var b = reference.Composite(PaintChannel.Color);
            int max = a.Select((v, i) => Math.Abs(v - b[i])).Max();
            Assert.That(max, Is.LessThanOrEqualTo(1), "a straight path is the stroke between its points (rounding of the subdivided input only)");
            Assert.That(A(l, 30, 30), Is.EqualTo(255)); Assert.That(A(l, 30, 50), Is.EqualTo(0));
        }

        [Test] public void TheCurvePassesThroughEveryPointAndBends()
        {
            var (d, l) = Doc();
            d.SetCanvasPath(l.Id, Path((8.5, 8.5), (32.5, 50.5), (56.5, 8.5)));
            foreach (var (x, y) in new[] { (8, 8), (32, 50), (56, 8) }) Assert.That(A(l, x, y), Is.EqualTo(255), "point " + x + "," + y);
            Assert.That(A(l, 32, 8), Is.EqualTo(0), "the chord between the ends is not drawn");
            // centripetal Catmull-Rom は頂点の近くで折れ線より外へ膨らむ（直線 (8,8)-(32,50) の中点 (20,29) の外側に線がある）
            Assert.That(Enumerable.Range(14, 6).Any(x => A(l, x, 33) > 0) || Enumerable.Range(20, 20).Any(x => A(l, x, 50) > 0), Is.True);
        }

        [Test] public void EditingRedrawsAndUndoBringsBackPathAndPixelsTogether()
        {
            var (d, l) = Doc();
            var first = Path((10.5, 10.5), (50.5, 10.5));
            d.SetCanvasPath(l.Id, first);
            var firstPixels = d.Composite(PaintChannel.Color);
            var moved = first.WithPoints(new[] { new CanvasPoint(10.5, 50.5), first.Points[1] });
            d.SetCanvasPath(l.Id, moved);
            Assert.That(A(l, 11, 50), Is.EqualTo(255)); Assert.That(A(l, 11, 10), Is.EqualTo(0), "the old line is gone");
            Assert.That(d.UndoCount, Is.EqualTo(2));
            d.Undo();
            Assert.That(l.Path, Is.SameAs(first)); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(firstPixels));
            d.Redo(); Assert.That(l.Path, Is.SameAs(moved));
            // 同じパスは同じ画素
            var (e, m) = Doc(); e.SetCanvasPath(m.Id, moved);
            Assert.That(e.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
        }

        [Test] public void PathLayersRefuseOtherEditsUntilRasterized()
        {
            var (d, l) = Doc();
            d.SetCanvasPath(l.Id, Path((10.5, 10.5), (50.5, 10.5)));
            Assert.That(() => d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings()), Throws.InvalidOperationException.With.Message.Contains("drawn by a path"));
            d.SetChannelEnabled(l.Id, PaintChannel.Roughness, true);
            Assert.That(() => d.SetCanvasPath(l.Id, new CanvasPath(Guid.NewGuid(), PaintChannel.Roughness, Brush(), new[] { new CanvasPoint(1, 1) })), Throws.InvalidOperationException.With.Message.Contains("keeps its channel"));
            var surface = new SurfacePath(Guid.NewGuid(), PaintChannel.Color, "model", new PathBrush(), new PathPoint[0]);
            Assert.That(() => d.SetPath(l.Id, surface, new SparseTileSurface(Size, Size, 16)), Throws.InvalidOperationException.With.Message.Contains("kind of its path"));
            var other = d.AddLayer("other");
            Assert.That(() => d.SetCanvasPath(other.Id, new CanvasPath(Guid.NewGuid(), PaintChannel.Metallic, Brush(), new CanvasPoint[0])), Throws.InvalidOperationException.With.Message.Contains("Enable"));
            d.Rasterize(l.Id);
            using (var s = d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings())) s.Cancel();
        }

        [Test] public void BadPointsBrushesAndBudgetsAreRefusedWithoutChanges()
        {
            Assert.That(() => new CanvasPoint(double.NaN, 0), Throws.TypeOf<ArgumentException>().Or.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new CanvasPoint(2e6, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new CanvasPoint(0, 0, 1.5), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new CanvasPath(Guid.NewGuid(), PaintChannel.Color, Brush(), Enumerable.Repeat(new CanvasPoint(1, 1), EditablePath.MaxPointCount + 1)), Throws.ArgumentException);
            Assert.That(() => new CanvasPath(Guid.NewGuid(), PaintChannel.Color, Brush(5000), new CanvasPoint[0]), Throws.TypeOf<ArgumentOutOfRangeException>());
            var (d, l) = Doc();
            d.ActiveStrokeBudgetBytes = 2048;
            var before = d.Composite(PaintChannel.Color);
            Assert.That(() => d.SetCanvasPath(l.Id, Path((1, 1), (63, 63), (1, 63))), Throws.InvalidOperationException);
            Assert.That(l.Path, Is.Null); Assert.That(d.CanUndo, Is.False); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before));
        }

        [Test] public void SavedAsVersion10AndVersion9StillReads()
        {
            var (d, l) = Doc();
            var path = new CanvasPath(Guid.NewGuid(), PaintChannel.Color, Brush(2.5), new[] { new CanvasPoint(5.25, 6.5, .75), new CanvasPoint(40, 30.125, .5), new CanvasPoint(60, 2) });
            d.SetCanvasPath(l.Id, path);
            var bytes = DocumentBinary.Write(d);
            Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(10));
            var restored = DocumentBinary.Read(bytes);
            var p = (CanvasPath)restored.Layers[0].Path;
            Assert.That((p.Id, p.Channel), Is.EqualTo((path.Id, path.Channel)));
            Assert.That(p.Brush, Is.EqualTo(path.Brush)); Assert.That(p.Points, Is.EqualTo(path.Points));
            Assert.That(restored.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            Assert.That(DocumentBinary.Write(restored), Is.EqualTo(bytes));
            // 描き直しても同じ画素（保存した画素はパスの結果そのもの）
            restored.SetCanvasPath(restored.Layers[0].Id, p);
            Assert.That(restored.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            // 知らないアルゴリズムの版は断る（点 3 個 × 24、個数 4、筆 48、チャンネル 4、ID 16 の前）
            var bad = (byte[])bytes.Clone(); BitConverter.GetBytes(2).CopyTo(bad, bytes.Length - (3 * 24 + 4 + 48 + 4 + 16 + 4));
            Assert.That(() => DocumentBinary.Read(bad), Throws.TypeOf<System.IO.InvalidDataException>().With.Message.Contains("Canvas path algorithm version 2"));
            // 版 9（2D のパスの欄が無い）も読む
            var plain = new PaintDocument(Size, Size, 16); var pl = plain.AddLayer("P"); pl.GetChannel(PaintChannel.Color).SetPixel(3, 4, new Rgba32(1, 2, 3));
            var v9 = DocumentBinary.Read(ArchiveTestUtil.AsVersion(DocumentBinary.Write(plain), "P", 9));
            Assert.That(v9.Composite(PaintChannel.Color), Is.EqualTo(plain.Composite(PaintChannel.Color))); Assert.That(v9.Layers[0].Path, Is.Null);
        }
    }
}
