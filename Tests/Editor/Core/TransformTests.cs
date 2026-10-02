using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>変形（移動・回転・拡大縮小・反転）: 画素の行き先、1 画素に重なるときのバイト一致（透明画素の色も）、補間、選択範囲だけを
    /// 持ち上げて選択範囲も一緒に動くこと、全チャンネルとマスクが一緒に動くこと、1 回の Undo、変更追跡、予算超過で何も変えないこと、
    /// 型の拒否、保存の往復。</summary>
    public sealed class TransformTests
    {
        const int Size = 32;
        static PaintDocument Doc(out PaintLayer layer, int tile = 8)
        { var d = new PaintDocument(Size, Size, tile); layer = d.AddLayer("L"); d.ClearHistory(); return d; }
        static SparseTileSurface Color(PaintLayer l) => l.GetChannel(PaintChannel.Color);
        /// <summary>画素ごとに違う色。透明でも RGB を持つ画素と半透明の画素を含む。</summary>
        static Rgba32 Pattern(int x, int y) => new Rgba32((byte)(x * 7 + 1), (byte)(y * 5 + 2), (byte)((x ^ y) * 3), (byte)((x + y) % 3 == 0 ? 0 : (x + y) % 3 == 1 ? 128 : 255));
        static void Paint(PaintDocument d, PaintLayer l, int x0, int y0, int x1, int y1, Func<int, int, Rgba32> color)
        { for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) Color(l).SetPixel(x, y, color(x, y)); d.ClearHistory(); }
        static Rgba32[,] Snapshot(SparseTileSurface s)
        { var r = new Rgba32[Size, Size]; for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) r[x, y] = s.GetPixel(x, y); return r; }

        [Test] public void AWholePixelMoveIsByteExactIncludingTheColourOfTransparentPixels()
        {
            var d = Doc(out var layer); Paint(d, layer, 0, 0, Size, Size, Pattern);
            Assert.That(d.Transform(layer.Id, Affine2D.Translation(5, -3)), Is.True);
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
            {
                int sx = x - 5, sy = y + 3;
                var expected = sx < 0 || sy >= Size ? Rgba32.Transparent : Pattern(sx, sy);
                Assert.That(layer.GetPixel(PaintChannel.Color, x, y), Is.EqualTo(expected), x + "," + y);
            }
            Assert.That(d.UndoCount, Is.EqualTo(1));
        }

        [Test] public void QuarterTurnsAndFlipsCopyEveryPixelExactly()
        {
            var d = Doc(out var layer); Paint(d, layer, 0, 0, Size, Size, Pattern);
            d.Transform(layer.Id, Affine2D.FromParts(Size / 2.0, Size / 2.0, 0, 0, 90, 1, 1));
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
                Assert.That(layer.GetPixel(PaintChannel.Color, Size - 1 - y, x), Is.EqualTo(Pattern(x, y)), "90° counter-clockwise " + x + "," + y);
            d.Undo();
            d.Transform(layer.Id, Affine2D.FromParts(Size / 2.0, Size / 2.0, 0, 0, 0, -1, 1));
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
                Assert.That(layer.GetPixel(PaintChannel.Color, Size - 1 - x, y), Is.EqualTo(Pattern(x, y)), "horizontal flip " + x + "," + y);
            d.Undo();
            Assert.That(d.Transform(layer.Id, Affine2D.FromParts(Size / 2.0, Size / 2.0, 0, 0, 360, 1, 1)), Is.False, "a full turn changes nothing and records nothing");
            Assert.That(d.UndoCount, Is.EqualTo(0));
        }

        [Test] public void ScalingUpUsesNearestOrPremultipliedBilinear()
        {
            var red = new Rgba32(255, 0, 0); var blue = new Rgba32(0, 0, 255);
            var d = Doc(out var layer); Paint(d, layer, 4, 4, 6, 8, (x, y) => x == 4 ? red : blue);
            d.Transform(layer.Id, Affine2D.FromParts(4, 4, 0, 0, 0, 2, 2), resampling: Resampling.Nearest);
            for (int y = 4; y < 12; y++) { for (int x = 4; x < 6; x++) Assert.That(layer.GetPixel(PaintChannel.Color, x, y), Is.EqualTo(red)); for (int x = 6; x < 8; x++) Assert.That(layer.GetPixel(PaintChannel.Color, x, y), Is.EqualTo(blue)); }
            Assert.That(layer.GetPixel(PaintChannel.Color, 8, 4).A, Is.EqualTo(0)); Assert.That(layer.GetPixel(PaintChannel.Color, 4, 12).A, Is.EqualTo(0));
            d.Undo();
            d.Transform(layer.Id, Affine2D.FromParts(4, 4, 0, 0, 0, 2, 2));
            var between = layer.GetPixel(PaintChannel.Color, 5, 6);
            Assert.That(between.A, Is.EqualTo(255)); Assert.That(between.R, Is.GreaterThan(0)); Assert.That(between.B, Is.GreaterThan(0), "red and blue blend inside");
            var edge = layer.GetPixel(PaintChannel.Color, 4, 4);
            Assert.That(edge.A, Is.GreaterThan(0).And.LessThan(255), "the outer edge fades out");
            Assert.That(edge, Is.EqualTo(new Rgba32(255, 0, 0, edge.A)), "premultiplied: fading to transparent keeps the colour (no dark fringe)");
        }

        [Test] public void WithASelectionOnlySelectedPixelsMoveAndTheSelectionMovesWithThem()
        {
            var red = new Rgba32(200, 0, 0);
            var d = Doc(out var layer); Paint(d, layer, 0, 0, Size, Size, (x, y) => red);
            var old = SelectionMask.Rectangle(d, 0, 0, 8, 8); d.SetSelection(old); d.ClearHistory();
            Assert.That(d.Transform(layer.Id, Affine2D.Translation(16, 16)), Is.True);
            Assert.That(layer.GetPixel(PaintChannel.Color, 3, 3).A, Is.EqualTo(0), "lifted pixels leave transparency");
            Assert.That(layer.GetPixel(PaintChannel.Color, 20, 20), Is.EqualTo(red));
            Assert.That(layer.GetPixel(PaintChannel.Color, 12, 3), Is.EqualTo(red), "unselected pixels stay");
            Assert.That(d.Selection[20, 20], Is.EqualTo(255)); Assert.That(d.Selection[3, 3], Is.EqualTo(0), "the selection follows the pixels");
            Assert.That(d.UndoCount, Is.EqualTo(1), "pixels and selection are one step");
            d.Undo();
            Assert.That(layer.GetPixel(PaintChannel.Color, 3, 3), Is.EqualTo(red)); Assert.That(d.Selection, Is.SameAs(old));
            d.Redo();
            Assert.That(layer.GetPixel(PaintChannel.Color, 3, 3).A, Is.EqualTo(0)); Assert.That(d.Selection[20, 20], Is.EqualTo(255));
            d.Undo();
            Assert.That(d.Transform(layer.Id, Affine2D.Translation(4, 0), SelectionMask.Rectangle(d, 0, 0, 4, 4)), Is.True);
            Assert.That(d.Selection, Is.SameAs(old), "an explicit region does not move the document's selection");
            Assert.That(layer.GetPixel(PaintChannel.Color, 1, 1).A, Is.EqualTo(0)); Assert.That(layer.GetPixel(PaintChannel.Color, 5, 1), Is.EqualTo(red));
        }

        [Test] public void AHalfSelectedPixelLeavesHalfBehindAndCarriesHalf()
        {
            var green = new Rgba32(0, 200, 0);
            var d = Doc(out var layer); Paint(d, layer, 2, 2, 3, 3, (x, y) => green);
            var half = SelectionMask.Polygon(d, new List<(double, double)> { (2, 2), (3, 2), (2, 3) }); // 画素 (2,2) の半分
            Assert.That(half[2, 2], Is.InRange(1, 254));
            d.Transform(layer.Id, Affine2D.Translation(10, 0), half);
            int amount = half[2, 2];
            Assert.That(layer.GetPixel(PaintChannel.Color, 2, 2), Is.EqualTo(new Rgba32(0, 200, 0, (byte)(255 - amount))));
            Assert.That(layer.GetPixel(PaintChannel.Color, 12, 2), Is.EqualTo(new Rgba32(0, 200, 0, (byte)amount)));
        }

        [Test] public void EveryChannelAndTheMaskMoveTogether()
        {
            var d = Doc(out var layer);
            d.SetChannelEnabled(layer.Id, PaintChannel.Roughness, true); d.AddLayerMask(layer.Id);
            Color(layer).SetPixel(1, 1, new Rgba32(10, 20, 30));
            layer.GetChannel(PaintChannel.Roughness).SetPixel(1, 1, new Rgba32(90, 90, 90));
            layer.Mask.Surface.SetPixel(1, 1, new Rgba32(0, 0, 0, 200));
            d.ClearHistory();
            d.Transform(layer.Id, Affine2D.Translation(3, 4));
            Assert.That(layer.GetPixel(PaintChannel.Color, 4, 5), Is.EqualTo(new Rgba32(10, 20, 30)));
            Assert.That(layer.GetPixel(PaintChannel.Roughness, 4, 5), Is.EqualTo(new Rgba32(90, 90, 90)));
            Assert.That(layer.Mask.Surface.GetPixel(4, 5), Is.EqualTo(new Rgba32(0, 0, 0, 200)));
            Assert.That(layer.Mask.Surface.GetPixel(1, 1), Is.EqualTo(Rgba32.Transparent), "the mask reveals where it left");
            d.Undo();
            Assert.That(layer.Mask.Surface.GetPixel(1, 1).A, Is.EqualTo(200)); Assert.That(layer.GetPixel(PaintChannel.Roughness, 1, 1).R, Is.EqualTo(90));
            d.Transform(layer.Id, Affine2D.Translation(3, 4), includeMask: false);
            Assert.That(layer.Mask.Surface.GetPixel(1, 1).A, Is.EqualTo(200), "includeMask: false leaves the mask");
            Assert.That(layer.GetPixel(PaintChannel.Color, 4, 5), Is.EqualTo(new Rgba32(10, 20, 30)));
        }

        [Test] public void TheChangedTilesAreReportedAndTheCompositeFollows()
        {
            var d = Doc(out var layer); Paint(d, layer, 0, 0, 4, 4, (x, y) => new Rgba32(50, 60, 70));
            long since = d.ChangeSerial; var changed = new HashSet<TileCoord>();
            d.Transform(layer.Id, Affine2D.Translation(20, 20));
            Assert.That(d.TryGetChangedTiles(PaintChannel.Color, since, changed), Is.True);
            Assert.That(changed, Does.Contain(new TileCoord(0, 0))); Assert.That(changed, Does.Contain(new TileCoord(2, 2)));
            Assert.That(d.Composite(PaintChannel.Color)[(21 * Size + 21) * 4 + 3], Is.EqualTo(255));
        }

        [Test] public void OverBudgetChangesNothingAndRecordsNothing()
        {
            var d = Doc(out var layer); Paint(d, layer, 0, 0, Size, Size, Pattern);
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 16, 16)); d.ClearHistory();
            var before = Snapshot(Color(layer)); var selection = d.Selection;
            d.ActiveStrokeBudgetBytes = 2000;
            Assert.That(() => d.Transform(layer.Id, Affine2D.Translation(9, 9)), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(Snapshot(Color(layer)), Is.EqualTo(before)); Assert.That(d.Selection, Is.SameAs(selection)); Assert.That(d.UndoCount, Is.EqualTo(0));

            var small = Doc(out var corner); Paint(small, corner, 0, 0, 8, 8, Pattern);
            small.SourceBudgetBytes = small.AllocatedBytes;
            var one = Snapshot(Color(corner));
            Assert.That(() => small.Transform(corner.Id, Affine2D.FromParts(0, 0, 0, 0, 0, 3, 3)), Throws.InvalidOperationException.With.Message.Contains("budget"),
                "scaling spreads one tile over nine, more than the pixel budget allows");
            Assert.That(Snapshot(Color(corner)), Is.EqualTo(one)); Assert.That(small.UndoCount, Is.EqualTo(0));
        }

        [Test] public void OnlyPaintLayersWithAnInvertibleTransformAreAccepted()
        {
            var d = Doc(out var layer); Paint(d, layer, 0, 0, 4, 4, (x, y) => new Rgba32(9, 9, 9));
            var fill = d.AddFillLayer("F", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(1, 1, 1) } });
            var group = d.AddGroup("G");
            Assert.That(() => d.Transform(fill.Id, Affine2D.Translation(1, 0)), Throws.InvalidOperationException);
            Assert.That(() => d.Transform(group.Id, Affine2D.Translation(1, 0)), Throws.InvalidOperationException.With.Message.Contains("group"));
            Assert.That(() => d.Transform(layer.Id, Affine2D.FromParts(0, 0, 0, 0, 0, 0, 1)), Throws.InvalidOperationException.With.Message.Contains("zero"));
            Assert.That(() => d.Transform(layer.Id, new Affine2D(1, 0, 0, 1, double.NaN, 0)), Throws.ArgumentException);
            Assert.That(() => Affine2D.FromParts(0, 0, 0, 0, double.PositiveInfinity, 1, 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => d.Transform(layer.Id, Affine2D.Translation(1, 0), resampling: (Resampling)7), Throws.InstanceOf<ArgumentOutOfRangeException>());
            var stroke = d.BeginStroke(layer.Id, PaintChannel.Color, new BrushSettings());
            Assert.That(() => d.Transform(layer.Id, Affine2D.Translation(1, 0)), Throws.InvalidOperationException, "not during a stroke");
            stroke.Cancel();
            Assert.That(d.Transform(layer.Id, Affine2D.Identity), Is.False);
            var blank = d.AddLayer("B"); d.ClearHistory();
            Assert.That(d.Transform(blank.Id, Affine2D.Translation(3, 0)), Is.False, "nothing to move");
            Assert.That(d.UndoCount, Is.EqualTo(0));
        }

        [Test] public void TheBoundsCoverTheContentWithinTheSelection()
        {
            var d = Doc(out var layer);
            Assert.That(d.TransformBounds(layer.Id), Is.Null);
            Paint(d, layer, 3, 5, 10, 7, (x, y) => new Rgba32(1, 1, 1));
            d.AddLayerMask(layer.Id); layer.Mask.Surface.SetPixel(20, 21, new Rgba32(0, 0, 0, 9)); d.ClearHistory();
            Assert.That(d.TransformBounds(layer.Id), Is.EqualTo(((int, int, int, int)?)(3, 5, 21, 22)), "the mask counts");
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 6, 6));
            Assert.That(d.TransformBounds(layer.Id), Is.EqualTo(((int, int, int, int)?)(3, 5, 6, 6)));
        }

        [Test] public void TheMovedPixelsAndSelectionSurviveSavingAndUndoAfterReopenIsEmpty()
        {
            var d = Doc(out var layer); Paint(d, layer, 0, 0, Size, Size, Pattern);
            d.Transform(layer.Id, Affine2D.FromParts(16, 16, 2, 1, 27, 0.8, 1.3));
            var bytes = DocumentBinary.Write(d); var restored = DocumentBinary.Read(bytes);
            Assert.That(restored.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            Assert.That(DocumentBinary.Write(restored), Is.EqualTo(bytes));
            Assert.That(restored.UndoCount, Is.EqualTo(0));
        }
    }
}
