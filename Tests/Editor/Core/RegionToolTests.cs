using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>選択範囲（矩形・楕円・投げ縄・マジックワンド・反転・組み合わせ）、塗りつぶし、グラデーション、マスクの塗りつぶし、
    /// 選択範囲の内側だけに効くストローク。どれも 1 回の Undo で戻り、予算を超えたら何も変えない。</summary>
    public sealed class RegionToolTests
    {
        static PaintDocument Doc(out PaintLayer layer, int size = 32, int tile = 8)
        { var d = new PaintDocument(size, size, tile); layer = d.AddLayer("L"); d.ClearHistory(); return d; }
        static Rgba32 Px(PaintDocument d, PaintLayer l, int x, int y) => l.GetPixel(PaintChannel.Color, x, y);

        [Test] public void RectangleEllipseAndPolygonSelectWhatTheyCover()
        {
            var d = Doc(out _);
            var rect = SelectionMask.Rectangle(d, 4, 6, 10, 9);
            Assert.That(rect[4, 6], Is.EqualTo(255)); Assert.That(rect[9, 8], Is.EqualTo(255));
            Assert.That(rect[10, 8], Is.EqualTo(0), "the right edge is exclusive"); Assert.That(rect[3, 6], Is.EqualTo(0));
            Assert.That(SelectionMask.Rectangle(d, 10, 9, 4, 6)[5, 7], Is.EqualTo(255), "corners in any order");
            var ellipse = SelectionMask.Ellipse(d, 16, 16, 8, 4);
            Assert.That(ellipse[15, 15], Is.EqualTo(255)); Assert.That(ellipse[16, 22], Is.EqualTo(0));
            Assert.That(Enumerable.Range(0, 32).Any(x => ellipse[x, 16] > 0 && ellipse[x, 16] < 255), Is.True, "the edge is anti-aliased");
            var triangle = SelectionMask.Polygon(d, new List<(double, double)> { (0, 0), (32, 0), (0, 32) });
            double area = 0; for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) area += triangle[x, y] / 255.0;
            Assert.That(area, Is.EqualTo(32 * 32 / 2.0).Within(4), "the covered area matches the triangle");
            Assert.That(SelectionMask.Polygon(d, new List<(double, double)> { (0, 0), (1, 1) }).IsEmpty, Is.True);
        }

        [Test] public void SelectionsInvertAndCombine()
        {
            var d = Doc(out _);
            var a = SelectionMask.Rectangle(d, 0, 0, 16, 32); var b = SelectionMask.Rectangle(d, 8, 0, 24, 32);
            Assert.That(a.Combine(b, SelectionCombine.Add)[20, 3], Is.EqualTo(255));
            Assert.That(a.Combine(b, SelectionCombine.Subtract)[10, 3], Is.EqualTo(0)); Assert.That(a.Combine(b, SelectionCombine.Subtract)[4, 3], Is.EqualTo(255));
            Assert.That(a.Combine(b, SelectionCombine.Intersect)[4, 3], Is.EqualTo(0)); Assert.That(a.Combine(b, SelectionCombine.Intersect)[12, 3], Is.EqualTo(255));
            Assert.That(a.Combine(b, SelectionCombine.Replace), Is.SameAs(b));
            var inverted = a.Invert();
            Assert.That(inverted[4, 4], Is.EqualTo(0)); Assert.That(inverted[30, 30], Is.EqualTo(255));
            Assert.That(SelectionMask.All(d).Invert().IsEmpty, Is.True);
            Assert.That(() => a.Combine(SelectionMask.All(new PaintDocument(16, 16, 8)), SelectionCombine.Add), Throws.ArgumentException);
        }

        [Test] public void TheSelectionIsUndoableAndAnEmptyOneDeselects()
        {
            var d = Doc(out var layer);
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 8, 8));
            Assert.That(d.Selection, Is.Not.Null);
            d.SetSelection(SelectionMask.None(d));
            Assert.That(d.Selection, Is.Null, "nothing selected = no selection (brushes keep working)");
            d.Undo(); Assert.That(d.Selection[2, 2], Is.EqualTo(255));
            d.Undo(); Assert.That(d.Selection, Is.Null);
            Assert.That(layer.Channels[PaintChannel.Color].TileCount, Is.EqualTo(0), "selecting never changes pixels");
            Assert.That(() => d.SetSelection(SelectionMask.All(new PaintDocument(16, 16, 8))), Throws.ArgumentException);
        }

        [Test] public void FillCoversTheSelectionOnlyAndIsOneUndoStep()
        {
            var d = Doc(out var layer);
            Assert.That(d.Fill(layer.Id, PaintChannel.Color, new Rgba32(200, 0, 0)), Is.True);
            Assert.That(Px(d, layer, 31, 31), Is.EqualTo(new Rgba32(200, 0, 0)), "without a selection the whole layer");
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 8, 8));
            d.Fill(layer.Id, PaintChannel.Color, new Rgba32(0, 0, 255), .5);
            Assert.That(Px(d, layer, 2, 2), Is.EqualTo(CpuCompositor.Blend(new Rgba32(200, 0, 0), new Rgba32(0, 0, 255), .5)));
            Assert.That(Px(d, layer, 20, 20), Is.EqualTo(new Rgba32(200, 0, 0)), "outside the selection nothing changes");
            int steps = d.UndoCount;
            d.Undo(); Assert.That(Px(d, layer, 2, 2), Is.EqualTo(new Rgba32(200, 0, 0)));
            Assert.That(d.UndoCount, Is.EqualTo(steps - 1));
            d.Fill(layer.Id, PaintChannel.Color, new Rgba32(0, 0, 0), 1, erase: true);
            Assert.That(Px(d, layer, 2, 2).A, Is.EqualTo(0)); Assert.That(Px(d, layer, 20, 20).A, Is.EqualTo(255), "erase inside the selection only");
            Assert.That(d.Fill(layer.Id, PaintChannel.Color, new Rgba32(0, 0, 0), 1, erase: true), Is.False, "nothing left to change");
        }

        [Test] public void AHalfSelectedPixelChangesHalfway()
        {
            var d = Doc(out var layer);
            var half = SelectionMask.Ellipse(d, 16, 16, 6.5, 6.5);
            int ex = Enumerable.Range(16, 16).First(x => half[x, 16] > 60 && half[x, 16] < 200);
            d.SetSelection(half);
            d.Fill(layer.Id, PaintChannel.Color, new Rgba32(255, 255, 255));
            Assert.That(Px(d, layer, ex, 16).A, Is.EqualTo(half[ex, 16]).Within(1), "a partly selected pixel gets that much of the fill");
            // ストロークも、何度重ねても選択量を超えて変わらない
            var e = Doc(out var paint); e.SetSelection(SelectionMask.Ellipse(e, 16, 16, 6.5, 6.5));
            var brush = new BrushSettings { Radius = 20, Hardness = 1, Color = new Rgba32(0, 0, 0), PressureSize = false, PressureOpacity = false, Spacing = .05 };
            using (var s = e.BeginStroke(paint.Id, PaintChannel.Color, brush))
            { for (int i = 0; i < 20; i++) s.Add(new BrushSample(16 + (i % 2), 16, 1, i)); s.Commit(); }
            Assert.That(paint.GetPixel(PaintChannel.Color, ex, 16).A, Is.EqualTo(half[ex, 16]).Within(1));
            Assert.That(paint.GetPixel(PaintChannel.Color, 1, 1).A, Is.EqualTo(0), "outside the selection the brush does nothing");
            Assert.That(paint.GetPixel(PaintChannel.Color, 16, 16).A, Is.EqualTo(255));
        }

        [Test] public void GradientsRunBetweenTheirEndsAndFadeWithoutDarkening()
        {
            var d = Doc(out var layer, size: 64, tile: 16);
            var g = new GradientSettings { X0 = 0, Y0 = 0, X1 = 64, Y1 = 0, From = new Rgba32(255, 0, 0), To = new Rgba32(0, 0, 255) };
            d.Gradient(layer.Id, PaintChannel.Color, g);
            Assert.That(Px(d, layer, 0, 5).R, Is.GreaterThan(245)); Assert.That(Px(d, layer, 63, 5).B, Is.GreaterThan(245));
            Assert.That(Px(d, layer, 32, 5).R, Is.EqualTo(Px(d, layer, 32, 40).R), "a horizontal gradient is constant down a column");
            var e = Doc(out var fade, size: 64, tile: 16);
            e.Gradient(fade.Id, PaintChannel.Color, new GradientSettings { X0 = 0, Y0 = 0, X1 = 64, Y1 = 0, From = new Rgba32(0, 200, 0), To = Rgba32.Transparent });
            var mid = Px(e, fade, 32, 1);
            Assert.That(mid.G, Is.EqualTo(200), "fading to transparent keeps the colour (premultiplied interpolation)");
            Assert.That(mid.A, Is.InRange(120, 135));
            var r = Doc(out var radial, size: 64, tile: 16);
            r.Gradient(radial.Id, PaintChannel.Color, new GradientSettings { Shape = GradientShape.Radial, X0 = 32, Y0 = 32, X1 = 32, Y1 = 64, From = new Rgba32(255, 255, 255), To = new Rgba32(0, 0, 0) });
            Assert.That(Px(r, radial, 32, 32).R, Is.GreaterThan(245), "the centre pixel (measured at its centre) is almost the start colour"); Assert.That(Px(r, radial, 32, 16), Is.EqualTo(Px(r, radial, 16, 32)), "radial is symmetric");
            Assert.That(() => r.Gradient(radial.Id, PaintChannel.Color, new GradientSettings { Opacity = 2 }), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        /// <summary>全体のマジックワンドは、タイルを覚える読み手を使うので並列に計算しない（並列にしたら GUI モードで時々壊れた、2026-10-02）。
        /// 合成結果を基準にして（タイルごとの合成が重く、スレッドが重なりやすい）タイルの多い文書で 20 回回し、画素ごとの直接の判定と
        /// 一致することを確かめる。並列に戻すと 20 回中 4 回壊れた（2 台目、32 スレッド）。</summary>
        [Test] public void TheGlobalMagicWandIsStableOnManyTiles()
        {
            var d = new PaintDocument(512, 512, 16); var layer = d.AddLayer("L"); var s = layer.GetChannel(PaintChannel.Color);
            var buf = new byte[16 * 16 * 4];
            for (int ty = 0; ty < 32; ty++) for (int tx = 0; tx < 32; tx++)
            {
                for (int i = 0; i < 256; i++) { byte v = (byte)((i * 7 + tx * 3 + ty * 5) % 3 == 0 ? 100 : 40); buf[i * 4] = v; buf[i * 4 + 1] = v; buf[i * 4 + 2] = v; buf[i * 4 + 3] = 255; }
                s.ImportTile(new TileCoord(tx, ty), buf);
            }
            d.ClearHistory();
            for (int run = 0; run < 20; run++)
            {
                var global = SelectionMask.MagicWand(d, null, PaintChannel.Color, 0, 0, 0, contiguous: false);
                byte seed = layer.GetPixel(PaintChannel.Color, 0, 0).R;
                for (int y = 0; y < 512; y += 7) for (int x = 0; x < 512; x += 7)
                    Assert.That(global[x, y], Is.EqualTo(layer.GetPixel(PaintChannel.Color, x, y).R == seed ? 255 : 0), run + ": " + x + "," + y);
            }
        }

        [Test] public void TheMagicWandFindsConnectedOrAllMatchingPixels()
        {
            var d = Doc(out var layer);
            var s = layer.GetChannel(PaintChannel.Color);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) s.SetPixel(x, y, x < 10 || x > 20 ? new Rgba32(100, 100, 100) : new Rgba32(10, 10, 10));
            s.SetPixel(25, 25, new Rgba32(104, 98, 100));
            var connected = SelectionMask.MagicWand(d, layer.Id, PaintChannel.Color, 2, 2, 0, contiguous: true);
            Assert.That(connected[5, 5], Is.EqualTo(255)); Assert.That(connected[25, 5], Is.EqualTo(0), "the other side is not connected");
            var global = SelectionMask.MagicWand(d, layer.Id, PaintChannel.Color, 2, 2, 0, contiguous: false);
            Assert.That(global[25, 5], Is.EqualTo(255)); Assert.That(global[25, 25], Is.EqualTo(0), "a slightly different pixel needs tolerance");
            Assert.That(SelectionMask.MagicWand(d, layer.Id, PaintChannel.Color, 2, 2, 4, contiguous: false)[25, 25], Is.EqualTo(255));
            var composite = SelectionMask.MagicWand(d, null, PaintChannel.Color, 15, 15, 0, contiguous: true);
            Assert.That(composite[15, 3], Is.EqualTo(255)); Assert.That(composite[5, 3], Is.EqualTo(0));
            // バケツ塗り = マジックワンドの範囲を塗る（今の選択範囲の内側に限る）
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 32, 16));
            d.Fill(layer.Id, PaintChannel.Color, new Rgba32(255, 0, 0), region: connected);
            Assert.That(Px(d, layer, 5, 5), Is.EqualTo(new Rgba32(255, 0, 0)));
            Assert.That(Px(d, layer, 5, 25), Is.EqualTo(new Rgba32(100, 100, 100)), "the region is limited to the selection");
            Assert.That(() => SelectionMask.MagicWand(d, layer.Id, PaintChannel.Color, 40, 2, 0, true), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test] public void MasksCanBeFilledAndRevealed()
        {
            var d = Doc(out var layer); layer.GetChannel(PaintChannel.Color).SetPixel(3, 3, new Rgba32(9, 9, 9)); d.AddLayerMask(layer.Id); d.ClearHistory();
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 8, 8));
            d.FillMask(layer.Id);
            Assert.That(d.CompositePixel(PaintChannel.Color, 3, 3).A, Is.EqualTo(0), "the filled mask hides the selected area");
            d.FillMask(layer.Id, .5, reveal: true);
            Assert.That(layer.Mask.Surface.GetPixel(3, 3).A, Is.EqualTo(128).Within(1));
            d.Undo(); d.Undo();
            Assert.That(layer.Mask.Surface.TileCount, Is.EqualTo(0));
        }

        [Test] public void RegionEditsRefuseWhatTheyCannotPaintAndRollBackOnBudget()
        {
            var d = Doc(out var layer);
            var fill = d.AddFillLayer("F"); var group = d.AddGroup("G");
            Assert.That(() => d.Fill(fill.Id, PaintChannel.Color, new Rgba32(1, 2, 3)), Throws.InvalidOperationException);
            Assert.That(() => d.Fill(group.Id, PaintChannel.Color, new Rgba32(1, 2, 3)), Throws.InvalidOperationException);
            Assert.That(() => d.Fill(layer.Id, PaintChannel.Roughness, new Rgba32(1, 2, 3)), Throws.InvalidOperationException, "the channel must be enabled first");
            Assert.That(() => d.FillMask(layer.Id), Throws.InvalidOperationException, "no mask");
            // 予算を超えたら途中まで書いたタイルも元に戻し、Undo も積まない
            var big = new PaintDocument(64, 64, 16); var target = big.AddLayer("T"); big.ClearHistory();
            var s = target.GetChannel(PaintChannel.Color);
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) s.SetPixel(x, y, new Rgba32((byte)x, (byte)y, 7)); // 一様でないタイル
            big.ClearHistory(); var before = big.Composite(PaintChannel.Color);
            big.ActiveStrokeBudgetBytes = 3000; // タイル 1 枚の変更前（1024 バイト×... ）より少し大きい程度
            Assert.That(() => big.Fill(target.Id, PaintChannel.Color, new Rgba32(255, 0, 0)), Throws.InvalidOperationException);
            Assert.That(big.Composite(PaintChannel.Color), Is.EqualTo(before), "the fill is rolled back entirely");
            Assert.That(big.CanUndo, Is.False);
        }
    }
}
