using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// レイヤーの複製（塗り・Fill・調整・パス・グループを中身ごと、属性と効果のスタックとパスごと、新しい ID、元と独立、1 回の Undo、
    /// 保存して開いても同じ、予算の拒否）と、クリップボード（選択範囲の中を写す・部分的な選択はプリマルチプライドで混ぜる・透明画素の
    /// RGB・マスクは灰色・結合してコピー・カットは 1 回の Undo・同じ大きさなら同じ位置に、違う大きさなら中央に貼ってはみ出しを知らせる・
    /// 型と大きさと予算の拒否）。
    /// </summary>
    public sealed class LayerOpsTests
    {
        const int W = 41, H = 35, Tile = 16;
        static PaintDocument NewDocument(int w = W, int h = H) => new PaintDocument(w, h, Tile, 256L << 20) { ActiveStrokeBudgetBytes = 64L << 20 };
        static Rgba32 RandomColor(Random r) { var b = new byte[4]; r.NextBytes(b); return new Rgba32(b[0], b[1], b[2], b[3]); }
        static void Scatter(Random r, SparseTileSurface s, int count, int w = W, int h = H)
        {
            for (int n = 0; n < count; n++)
            {
                var c = RandomColor(r); int kind = r.Next(3);
                s.SetPixel(r.Next(w), r.Next(h), kind == 0 ? new Rgba32(c.R, c.G, c.B, 0) : kind == 1 ? new Rgba32(c.R, c.G, c.B, 255) : c);
            }
        }
        static Dictionary<PaintChannel, byte[]> Composites(PaintDocument d)
        { var all = new Dictionary<PaintChannel, byte[]>(); foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) all[c] = d.Composite(c); return all; }
        static byte[] Pixels(PaintDocument d, PaintLayer l, PaintChannel c)
        {
            var bytes = new byte[d.Width * d.Height * 4];
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++) { var p = l.GetPixel(c, x, y); int o = (y * d.Width + x) * 4; bytes[o] = p.R; bytes[o + 1] = p.G; bytes[o + 2] = p.B; bytes[o + 3] = p.A; }
            return bytes;
        }

        // ───────── 複製 ─────────

        /// <summary>塗りの層: 2 つのチャンネル（片方は無効）、マスク（反転・濃度・マスクのフィルター）、効果のスタック、2D のパス。</summary>
        static PaintLayer RichPaintLayer(PaintDocument d, Random r)
        {
            var l = d.AddLayer("rich");
            Scatter(r, l.GetChannel(PaintChannel.Color), 300); Scatter(r, l.GetChannel(PaintChannel.Roughness), 200);
            d.SetChannelEnabled(l.Id, PaintChannel.Roughness, false);
            d.SetLayerBlendMode(l.Id, LayerBlendMode.Overlay); d.SetLayerOpacity(l.Id, .7); d.SetLayerClipping(l.Id, true);
            var m = d.AddLayerMask(l.Id); for (int n = 0; n < 100; n++) m.Surface.SetPixel(r.Next(W), r.Next(H), new Rgba32(0, 0, 0, (byte)r.Next(256)));
            d.SetLayerMaskInverted(l.Id, true); d.SetLayerMaskDensity(l.Id, .6);
            d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(1));
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(2), new[] { PaintChannel.Color });
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Invert(), new[] { PaintChannel.Color }, strength: .5);
            return l;
        }

        [Test] public void DuplicatingAPaintLayerCopiesEverythingUnderNewIdsAndAboveTheOriginal()
        {
            var r = new Random(1); var d = NewDocument();
            var below = d.AddLayer("below"); Scatter(r, below.GetChannel(PaintChannel.Color), 200);
            var original = RichPaintLayer(d, r);
            var path = new CanvasPath(Guid.NewGuid(), PaintChannel.Color, new PathBrush { RadiusWorld = 4, Color = new Rgba32(10, 200, 30) }, new[] { new CanvasPoint(5, 5, 1), new CanvasPoint(30, 20, 1) });
            d.SetCanvasPath(original.Id, path);
            var top = d.AddLayer("top");
            d.ClearHistory(); var bytes = DocumentBinary.Write(d);

            var copy = d.DuplicateLayer(original.Id, "rich copy");
            Assert.That(d.Layers.Select(l => l.Name), Is.EqualTo(new[] { "below", "rich", "rich copy", "top" }), "directly above the original");
            Assert.That(copy.Id, Is.Not.EqualTo(original.Id));
            Assert.That((copy.Kind, copy.Visible, copy.Opacity, copy.BlendMode, copy.Clipping, copy.ParentId), Is.EqualTo((original.Kind, original.Visible, original.Opacity, original.BlendMode, original.Clipping, original.ParentId)));
            Assert.That(copy.EnabledChannels, Is.EqualTo(original.EnabledChannels)); Assert.That(copy.Channels.Keys, Is.EquivalentTo(original.Channels.Keys));
            foreach (var c in original.Channels.Keys) Assert.That(Pixels(d, copy, c), Is.EqualTo(Pixels(d, original, c)), c + " pixels, the RGB of transparent pixels included");
            Assert.That((copy.Mask.Enabled, copy.Mask.Inverted, copy.Mask.Density), Is.EqualTo((original.Mask.Enabled, original.Mask.Inverted, original.Mask.Density)));
            Assert.That(copy.Mask, Is.Not.SameAs(original.Mask));
            Assert.That(copy.Filters.Select(f => (f.Settings, f.Enabled, f.Strength)), Is.EqualTo(original.Filters.Select(f => (f.Settings, f.Enabled, f.Strength))));
            Assert.That(copy.Mask.Filters.Select(f => f.Settings), Is.EqualTo(original.Mask.Filters.Select(f => f.Settings)));
            var originalIds = original.Filters.Select(f => f.Id).Concat(original.Mask.Filters.Select(f => f.Id)).ToList();
            Assert.That(copy.Filters.Select(f => f.Id).Concat(copy.Mask.Filters.Select(f => f.Id)).Intersect(originalIds), Is.Empty, "effects get new IDs");
            Assert.That(copy.Path, Is.InstanceOf<CanvasPath>()); Assert.That(copy.Path.Id, Is.Not.EqualTo(original.Path.Id), "the path gets a new ID");
            Assert.That(((CanvasPath)copy.Path).Points, Is.EqualTo(path.Points));
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
                for (int y = 0; y < H; y += 3) for (int x = 0; x < W; x += 3)
                    Assert.That(copy.GetOutputPixel(c, x, y), Is.EqualTo(original.GetOutputPixel(c, x, y)), "the effects give the same output");

            // 保存して開いても同じ（ID の重なりは読み手が断るので、読めること自体が別の ID の確かめ）
            var saved = DocumentBinary.Write(d); var restored = DocumentBinary.Read(saved);
            Assert.That(DocumentBinary.Write(restored), Is.EqualTo(saved));
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) Assert.That(restored.Composite(c), Is.EqualTo(d.Composite(c)));

            // 元と独立: 片方を変えても、もう片方は変わらない
            var originalColor = Pixels(d, original, PaintChannel.Color);
            d.Rasterize(copy.Id);
            d.Fill(copy.Id, PaintChannel.Color, new Rgba32(1, 2, 3, 255));
            d.FillMask(copy.Id, 1);
            d.SetFilterEnabled(copy.Id, copy.Filters[0].Id, false);
            Assert.That(Pixels(d, original, PaintChannel.Color), Is.EqualTo(originalColor));
            Assert.That(original.Mask.Surface.TileCount, Is.LessThan(copy.Mask.Surface.TileCount));
            Assert.That(original.Filters[0].Enabled, Is.True); Assert.That(original.Path, Is.Not.Null);

            // 1 回の Undo
            while (d.UndoCount > 1) d.Undo();
            Assert.That(d.Undo(), Is.True); Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes), "one undo removes the copy");
            Assert.That(d.Redo(), Is.True); Assert.That(d.Layers.Count, Is.EqualTo(4));
        }

        [Test] public void DuplicatingAGroupCopiesItsContentsAndLooksTheSameWhenTheOriginalIsHidden()
        {
            var r = new Random(2); var d = NewDocument();
            var bg = d.AddLayer("bg"); Scatter(r, bg.GetChannel(PaintChannel.Color), 400);
            var a = d.AddLayer("a"); Scatter(r, a.GetChannel(PaintChannel.Color), 300); Scatter(r, a.GetChannel(PaintChannel.Normal), 300);
            var clip = d.AddLayer("clip"); Scatter(r, clip.GetChannel(PaintChannel.Color), 300); d.SetLayerClipping(clip.Id, true); d.SetLayerBlendMode(clip.Id, LayerBlendMode.Multiply);
            var fill = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(20, 90, 200, 120) }, { PaintChannel.Roughness, new Rgba32(40, 0, 0, 255) } });
            d.AddLayerMask(fill.Id); d.FillMask(fill.Id, 1, SelectionMask.Ellipse(d, 20, 17, 12, 9));
            var adjust = d.AddAdjustmentLayer("levels", AdjustmentSettings.Levels(.1, .8, 1.2, 0, 1));
            var n1 = d.AddLayer("n1"); Scatter(r, n1.GetChannel(PaintChannel.Color), 200);
            var nested = d.GroupLayers(new[] { n1.Id }, "nested");
            var group = d.GroupLayers(new[] { a.Id, clip.Id, fill.Id, adjust.Id, nested.Id }, "group");
            d.SetLayerBlendMode(group.Id, LayerBlendMode.Screen); d.SetLayerOpacity(group.Id, .8);
            d.AddLayer("top");
            var before = Composites(d); int count = d.Layers.Count; d.ClearHistory();

            var copy = d.DuplicateLayer(group.Id, "group copy");
            Assert.That(d.Layers.Count, Is.EqualTo(count + 7));
            var copied = d.Layers.Where(l => l == copy || IsInside(d, l, copy)).ToList();
            Assert.That(copied.Select(l => l.Name), Is.EqualTo(new[] { "a", "clip", "fill", "levels", "n1", "nested", "group copy" }), "the contents keep their names and order");
            var originals = d.Layers.Where(l => l == group || IsInside(d, l, group)).ToList();
            Assert.That(copied.Select(l => l.Id).Intersect(originals.Select(l => l.Id)), Is.Empty, "every copied layer has a new ID");
            Assert.That(d.Layers.ToList().IndexOf(copy), Is.EqualTo(d.Layers.ToList().IndexOf(group) + 7), "the copy's block sits directly above the original group");
            Assert.That(d.DepthOf(copied[4].Id), Is.EqualTo(2), "nesting is copied");
            Assert.That(copied[2].FillValues, Is.EqualTo(fill.FillValues)); Assert.That(copied[3].Adjustment, Is.EqualTo(adjust.Adjustment));
            d.ValidateStructure();

            d.SetLayerVisibility(group.Id, false);
            foreach (var c in before.Keys) Assert.That(d.Composite(c), Is.EqualTo(before[c]), "the copy alone looks like the original: " + c);

            var saved = DocumentBinary.Write(d);
            Assert.That(DocumentBinary.Write(DocumentBinary.Read(saved)), Is.EqualTo(saved));
            d.Undo(); d.Undo();
            Assert.That(d.Layers.Count, Is.EqualTo(count)); Assert.That(d.CanUndo, Is.False);
        }
        static bool IsInside(PaintDocument d, PaintLayer l, PaintLayer group)
        { for (var p = l.ParentId; p != Guid.Empty; p = d.GetLayer(p).ParentId) if (p == group.Id) return true; return false; }

        [Test] public void DuplicatingIsRefusedWhenThePixelsDoNotFitTheSourceBudget()
        {
            var r = new Random(3); var d = NewDocument();
            var l = d.AddLayer("l"); Scatter(r, l.GetChannel(PaintChannel.Color), 500);
            d.SourceBudgetBytes = d.AllocatedBytes + 100; var bytes = DocumentBinary.Write(d); d.ClearHistory();
            Assert.That(() => d.DuplicateLayer(l.Id), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes)); Assert.That(d.CanUndo, Is.False);
            var stroke = d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings());
            Assert.That(() => d.DuplicateLayer(l.Id), Throws.InvalidOperationException, "not during a stroke");
            stroke.Cancel();
            Assert.That(() => d.DuplicateLayer(l.Id, newId: l.Id), Throws.ArgumentException, "IDs stay unique");
        }

        // ───────── コピー・カット・ペースト ─────────

        [Test] public void CopyTakesTheSelectionWithPremultipliedPartialAmountsAndKeepsTransparentRgb()
        {
            var d = NewDocument(); var l = d.AddLayer("l"); var s = l.GetChannel(PaintChannel.Color);
            var hidden = new Rgba32(200, 100, 50, 0); var half = new Rgba32(10, 220, 30, 128); var solid = new Rgba32(1, 2, 3, 255);
            s.SetPixel(10, 10, hidden); s.SetPixel(11, 10, half); s.SetPixel(12, 10, solid); s.SetPixel(30, 30, solid);
            d.SetSelection(SelectionMask.Polygon(d, new[] { (9.0, 9.0), (13.0, 9.0), (13.0, 11.0), (9.0, 11.0) })); // 10..12 の 3 画素だけ全部入る
            var copied = d.CopyPixels(l.Id, PaintChannel.Color);
            Assert.That((copied.X, copied.Y, copied.Width, copied.Height), Is.EqualTo((10, 10, 3, 1)), "trimmed to the pixels with a value inside the selection");
            Assert.That(copied.GetPixel(0, 0), Is.EqualTo(hidden), "fully selected: exact, the RGB of a transparent pixel too");
            Assert.That(copied.GetPixel(1, 0), Is.EqualTo(half)); Assert.That(copied.GetPixel(2, 0), Is.EqualTo(solid));
            Assert.That((copied.Source, copied.Channel, copied.DocumentWidth, copied.DocumentHeight), Is.EqualTo((ClipboardSource.Layer, PaintChannel.Color, W, H)));

            // 部分的な選択: ReplacePixels と同じく、透明に向かってプリマルチプライドで混ぜる（色は保ち、アルファが減る）
            d.SetSelection(SelectionMask.Ellipse(d, 12.5, 10.5, 1.2, 1.2));
            var faded = d.CopyPixels(l.Id, PaintChannel.Color);
            for (int y = 0; y < faded.Height; y++) for (int x = 0; x < faded.Width; x++)
            {
                int px = faded.X + x, py = faded.Y + y; double a = d.Selection.Coverage(px, py); var p = s.GetPixel(px, py); var got = faded.GetPixel(x, y);
                if (a >= 1) Assert.That(got, Is.EqualTo(p));
                else if (a <= 0 || p.A == 0) Assert.That(got, Is.EqualTo(Rgba32.Transparent));
                else { Assert.That((got.R, got.G, got.B), Is.EqualTo((p.R, p.G, p.B))); Assert.That(got.A, Is.EqualTo((byte)Math.Floor(p.A * a + .5)).Within(1)); }
            }
            Assert.That(faded.Width, Is.LessThanOrEqualTo(3));
        }

        [Test] public void PasteMakesANewLayerInPlaceAndOneUndoTakesItAway()
        {
            var r = new Random(4); var d = NewDocument(); var l = d.AddLayer("l"); Scatter(r, l.GetChannel(PaintChannel.Roughness), 400);
            var copied = d.CopyPixels(l.Id, PaintChannel.Roughness);
            var sel = SelectionMask.Rectangle(d, 0, 0, 5, 5); d.SetSelection(sel); d.ClearHistory(); var bytes = DocumentBinary.Write(d);

            var pasted = d.PasteAsLayer(copied, PaintChannel.Roughness, "pasted", above: l.Id);
            Assert.That(pasted.Centered, Is.False); Assert.That(pasted.ClippedPixels, Is.Zero);
            Assert.That(d.Layers.Last(), Is.SameAs(pasted.Layer)); Assert.That(pasted.Layer.Name, Is.EqualTo("pasted"));
            Assert.That(Pixels(d, pasted.Layer, PaintChannel.Roughness), Is.EqualTo(Pixels(d, l, PaintChannel.Roughness)), "the same pixels at the same place, transparent RGB included");
            Assert.That(pasted.Layer.Channels.Keys, Is.EqualTo(new[] { PaintChannel.Roughness }));
            Assert.That(d.Selection, Is.Null, "paste deselects, so the move tool moves the new layer");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            Assert.That(d.Undo(), Is.True);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes)); Assert.That(d.Selection, Is.SameAs(sel), "undo brings the selection back");
            Assert.That(d.Redo(), Is.True); Assert.That(d.Layers.Count, Is.EqualTo(2));
            var saved = DocumentBinary.Write(d);
            Assert.That(DocumentBinary.Read(saved).Composite(PaintChannel.Roughness), Is.EqualTo(d.Composite(PaintChannel.Roughness)));
        }

        [Test] public void PastingIntoADocumentOfAnotherSizeCentersAndCutsOffWhatFallsOutside()
        {
            var big = NewDocument(64, 48); var l = big.AddLayer("l"); var s = l.GetChannel(PaintChannel.Color);
            for (int y = 10; y < 40; y++) for (int x = 2; x < 62; x++) s.SetPixel(x, y, new Rgba32((byte)x, (byte)y, 7, (byte)(x * 4)));
            var copied = big.CopyPixels(l.Id, PaintChannel.Color);
            Assert.That((copied.X, copied.Y, copied.Width, copied.Height), Is.EqualTo((2, 10, 60, 30)));

            var small = NewDocument(40, 40); small.AddLayer("base");
            var pasted = small.PasteAsLayer(copied, PaintChannel.Color);
            Assert.That(pasted.Centered, Is.True);
            Assert.That((pasted.X, pasted.Y), Is.EqualTo(((40 - 60) / 2, (40 - 30) / 2)));
            long expected = 0;
            for (int y = 0; y < 30; y++) for (int x = 0; x < 60; x++) { int px = pasted.X + x; if ((px < 0 || px >= 40) && copied.GetPixel(x, y) != Rgba32.Transparent) expected++; }
            Assert.That(pasted.ClippedPixels, Is.EqualTo(expected)); Assert.That(expected, Is.GreaterThan(0));
            Assert.That(pasted.Layer.GetPixel(PaintChannel.Color, 0, 5), Is.EqualTo(copied.GetPixel(-pasted.X, 0)));
            Assert.That(pasted.Layer.GetPixel(PaintChannel.Color, 39, 34), Is.EqualTo(copied.GetPixel(39 - pasted.X, 29)));
        }

        [Test] public void AMaskCopiesAsGreyAndCuttingItReveals()
        {
            var d = NewDocument(); var l = d.AddLayer("l"); l.GetChannel(PaintChannel.Color).SetPixel(3, 3, new Rgba32(9, 9, 9, 255));
            var m = d.AddLayerMask(l.Id); m.Surface.SetPixel(5, 5, new Rgba32(0, 0, 0, 200));
            d.SetSelection(SelectionMask.Rectangle(d, 4, 4, 8, 8)); d.ClearHistory();
            var copied = d.CopyPixels(l.Id, PaintChannel.Color, fromMask: true);
            Assert.That(copied.Source, Is.EqualTo(ClipboardSource.Mask));
            Assert.That((copied.X, copied.Y, copied.Width, copied.Height), Is.EqualTo((4, 4, 4, 4)), "an unpainted mask is white, so the whole selection is copied");
            Assert.That(copied.GetPixel(1, 1), Is.EqualTo(new Rgba32(55, 55, 55, 255)), "hide 200 is grey 55 (white shows)");
            Assert.That(copied.GetPixel(0, 0), Is.EqualTo(new Rgba32(255, 255, 255, 255)));

            var cut = d.CutPixels(l.Id, PaintChannel.Color, fromMask: true);
            Assert.That(cut.GetPixels(), Is.EqualTo(copied.GetPixels()));
            Assert.That(m.Surface.GetPixel(5, 5).A, Is.Zero, "cutting a mask reveals");
            Assert.That(l.GetPixel(PaintChannel.Color, 3, 3).A, Is.EqualTo(255), "the layer's pixels are untouched");
            Assert.That(d.UndoCount, Is.EqualTo(1)); d.Undo(); Assert.That(m.Surface.GetPixel(5, 5).A, Is.EqualTo(200));
        }

        [Test] public void CutCopiesAndErasesTheSelectionAsOneUndoStep()
        {
            var d = NewDocument(); var l = d.AddLayer("l"); var s = l.GetChannel(PaintChannel.Color);
            for (int y = 0; y < 20; y++) for (int x = 0; x < 20; x++) s.SetPixel(x, y, new Rgba32(100, 150, 200, 255));
            s.SetPixel(2, 2, new Rgba32(50, 60, 70, 0));
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 10, 10)); d.ClearHistory(); var bytes = DocumentBinary.Write(d);
            var cut = d.CutPixels(l.Id, PaintChannel.Color);
            Assert.That((cut.X, cut.Y, cut.Width, cut.Height), Is.EqualTo((0, 0, 10, 10)));
            Assert.That(cut.GetPixel(2, 2), Is.EqualTo(new Rgba32(50, 60, 70, 0)), "the cut keeps the RGB of transparent pixels");
            Assert.That(s.GetPixel(5, 5), Is.EqualTo(Rgba32.Transparent), "erased completely, RGB too");
            Assert.That(s.GetPixel(15, 15).A, Is.EqualTo(255), "outside the selection stays");
            Assert.That(d.UndoCount, Is.EqualTo(1)); d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes));
        }

        [Test] public void CopyMergedTakesTheCompositeInsideTheSelection()
        {
            var r = new Random(6); var d = NewDocument();
            var a = d.AddLayer("a"); Scatter(r, a.GetChannel(PaintChannel.Color), 500);
            var b = d.AddLayer("b"); Scatter(r, b.GetChannel(PaintChannel.Color), 500); d.SetLayerBlendMode(b.Id, LayerBlendMode.Screen); d.SetLayerOpacity(b.Id, .6);
            var copied = d.CopyMerged(PaintChannel.Color);
            var composite = d.Composite(PaintChannel.Color);
            for (int y = 0; y < copied.Height; y++) for (int x = 0; x < copied.Width; x++)
            {
                int o = ((copied.Y + y) * W + copied.X + x) * 4;
                Assert.That(copied.GetPixel(x, y), Is.EqualTo(new Rgba32(composite[o], composite[o + 1], composite[o + 2], composite[o + 3])));
            }
            Assert.That(copied.Source, Is.EqualTo(ClipboardSource.Composite));
        }

        [Test] public void CopyCutAndPasteRefuseWithAReasonAndChangeNothing()
        {
            var d = NewDocument(); var empty = d.AddLayer("empty");
            var fill = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(1, 2, 3, 255) } });
            var adjust = d.AddAdjustmentLayer("adjust", AdjustmentSettings.Invert());
            var group = d.AddGroup("group");
            var pathLayer = d.AddLayer("path");
            d.SetCanvasPath(pathLayer.Id, new CanvasPath(Guid.NewGuid(), PaintChannel.Color, new PathBrush { RadiusWorld = 3, Color = new Rgba32(255, 0, 0) }, new[] { new CanvasPoint(5, 5, 1), new CanvasPoint(20, 5, 1) }));
            d.ClearHistory(); var bytes = DocumentBinary.Write(d);
            LayerOpRefusal Reason(TestDelegate op) => Assert.Throws<LayerOpException>(op).Reason;
            Assert.That(Reason(() => d.CopyPixels(empty.Id, PaintChannel.Color)), Is.EqualTo(LayerOpRefusal.NothingToCopy));
            Assert.That(Reason(() => d.CopyPixels(adjust.Id, PaintChannel.Color)), Is.EqualTo(LayerOpRefusal.NoPixels));
            Assert.That(Reason(() => d.CopyPixels(group.Id, PaintChannel.Color)), Is.EqualTo(LayerOpRefusal.NoPixels));
            Assert.That(Reason(() => d.CutPixels(fill.Id, PaintChannel.Color)), Is.EqualTo(LayerOpRefusal.NotPaintLayer));
            Assert.That(Reason(() => d.CutPixels(pathLayer.Id, PaintChannel.Color)), Is.EqualTo(LayerOpRefusal.PathLayer));
            var tooLarge = Assert.Throws<LayerOpException>(() => d.CopyPixels(fill.Id, PaintChannel.Color, maxBytes: 1000));
            Assert.That((tooLarge.Reason, tooLarge.Limit), Is.EqualTo((LayerOpRefusal.ClipboardTooLarge, 1000L)));
            Assert.That(d.CopyPixels(fill.Id, PaintChannel.Color).Width, Is.EqualTo(W), "a fill layer copies its value everywhere");
            Assert.That(() => d.CopyPixels(empty.Id, PaintChannel.Color, fromMask: true), Throws.InvalidOperationException, "no mask");

            var copy = d.CopyPixels(pathLayer.Id, PaintChannel.Color);
            d.ActiveStrokeBudgetBytes = 100;
            Assert.That(Reason(() => d.PasteAsLayer(copy, PaintChannel.Color)), Is.EqualTo(LayerOpRefusal.OperationBudget));
            d.ActiveStrokeBudgetBytes = 64L << 20;
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes)); Assert.That(d.CanUndo, Is.False, "nothing was recorded");
            var stroke = d.BeginStroke(empty.Id, PaintChannel.Color, new BrushSettings());
            Assert.That(() => d.PasteAsLayer(copy, PaintChannel.Color), Throws.InvalidOperationException, "not during a stroke");
            Assert.That(() => d.CutPixels(empty.Id, PaintChannel.Color), Throws.InvalidOperationException);
            stroke.Cancel();
            Assert.That(() => new PixelClipboard(10, 10, 5, 5, 6, 1, new byte[24]), Throws.InstanceOf<ArgumentOutOfRangeException>(), "the rectangle must lie inside its document");
            Assert.That(() => new PixelClipboard(10, 10, 0, 0, 2, 2, new byte[15]), Throws.ArgumentException);
        }
    }
}
