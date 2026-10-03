using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Tri = System.ValueTuple<double, double, double, double, double, double>;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ポリゴン塗りつぶしの Core（<see cref="TriangleFill"/>）と、三角形の和集合の選択範囲（<see cref="SelectionMask.FromTriangles"/>）。
    /// 足した三角形の和集合を Fill / FillMask したものとバイトまで同じ（足す順・分け方・重ねて足すことに依らない）、選択範囲の内側だけ、
    /// ロック、1 回の Undo、取り消し、保存と読み戻し、型と予算で断る。</summary>
    public sealed class TriangleFillTests
    {
        static readonly Rgba32 Red = new Rgba32(220, 40, 30, 255);

        /// <summary>書き直す前の FromTriangles（サンプルごとに 3 本の辺を見る）の写し。新しい早道付きの版と 1 ビットも違わないことを確かめる。</summary>
        static byte[,] FrozenFromTriangles(int width, int height, IReadOnlyList<Tri> triangles)
        {
            var result = new byte[width, height];
            if (triangles.Count == 0) return result;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var t in triangles)
            {
                minX = Math.Min(minX, Math.Min(t.Item1, Math.Min(t.Item3, t.Item5))); maxX = Math.Max(maxX, Math.Max(t.Item1, Math.Max(t.Item3, t.Item5)));
                minY = Math.Min(minY, Math.Min(t.Item2, Math.Min(t.Item4, t.Item6))); maxY = Math.Max(maxY, Math.Max(t.Item2, Math.Max(t.Item4, t.Item6)));
            }
            int x0 = Math.Max(0, (int)Math.Floor(minX)), y0 = Math.Max(0, (int)Math.Floor(minY));
            int x1 = Math.Min(width, (int)Math.Ceiling(maxX) + 1), y1 = Math.Min(height, (int)Math.Ceiling(maxY) + 1);
            if (x0 >= x1 || y0 >= y1) return result;
            int w = x1 - x0, h = y1 - y0; var samples = new ushort[w * h];
            foreach (var t in triangles)
            {
                double ax = t.Item1, ay = t.Item2, bx = t.Item3, by = t.Item4, cx = t.Item5, cy = t.Item6;
                double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                if (Math.Abs(area) < 1e-12) continue;
                if (area < 0) { double sx = bx, sy = by; bx = cx; by = cy; cx = sx; cy = sy; }
                int tx0 = Math.Max(x0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), tx1 = Math.Min(x1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))) + 1);
                int ty0 = Math.Max(y0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), ty1 = Math.Min(y1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))) + 1);
                for (int py = ty0; py < ty1; py++) for (int px = tx0; px < tx1; px++)
                {
                    int bits = 0;
                    for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
                    {
                        double x = px + (sx + .5) / 4, y = py + (sy + .5) / 4;
                        if (In(ax, ay, bx, by, x, y) && In(bx, by, cx, cy, x, y) && In(cx, cy, ax, ay, x, y)) bits |= 1 << (sy * 4 + sx);
                    }
                    if (bits != 0) samples[(py - y0) * w + px - x0] |= (ushort)bits;
                }
            }
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
            {
                int bits = samples[(y - y0) * w + x - x0], count = 0;
                while (bits != 0) { count += bits & 1; bits >>= 1; }
                result[x, y] = (byte)((count * 255 + 8) / 16);
            }
            return result;
            bool In(double ax, double ay, double bx, double by, double x, double y)
            {
                double e = (bx - ax) * (y - ay) - (by - ay) * (x - ax);
                if (e != 0) return e > 0;
                return by > ay || (by == ay && bx < ax);
            }
        }

        /// <summary>試験の三角形: 乱数（画素の境目・サンプルの上・1/8 の格子に頂点を置いたものを混ぜる）、共有辺の扇、キャンバスの外へ
        /// はみ出すもの、とても大きな座標、細いもの、つぶれたもの。</summary>
        static List<Tri> Triangles(int seed, int size, int count)
        {
            var random = new Random(seed); var list = new List<Tri>();
            double P(int kind)
            {
                switch (kind)
                {
                    case 0: return random.Next(-4, size + 4);                         // 画素の角
                    case 1: return random.Next(0, size) + .125 + .25 * random.Next(4);  // サンプルの上
                    case 2: return random.Next(0, size * 8) / 8.0;                      // 1/8 の格子
                    default: return random.NextDouble() * (size + 8) - 4;
                }
            }
            for (int i = 0; i < count; i++) { int k = random.Next(4); list.Add((P(k), P(k), P(k), P(k), P(k), P(k))); }
            // 共有辺の扇（中心から）
            double c = size / 2.0;
            for (int i = 0; i < 12; i++)
            {
                double a0 = i * Math.PI / 6, a1 = (i + 1) * Math.PI / 6, r = size * .4;
                list.Add((c, c, c + Math.Cos(a0) * r, c + Math.Sin(a0) * r, c + Math.Cos(a1) * r, c + Math.Sin(a1) * r));
            }
            list.Add((-1e6, -1e6, 1e6, -1e6, 0, 1e6));   // 全部を覆う巨大なもの（早道の余裕が座標に合わせて大きくなる）
            list.Add((3, 3, 3.0001, size - 3, 3.0002, 3)); // 細いもの
            list.Add((5, 5, 9, 9, 13, 13));                // つぶれたもの
            return list;
        }

        [Test] public void FromTrianglesMatchesTheSampleBySampleRasterBitForBit()
        {
            foreach (var (size, tile, seed) in new[] { (37, 8, 1), (64, 16, 2), (130, 32, 3) })
            {
                var doc = new PaintDocument(size, size, tile);
                var all = Triangles(seed, size, 60);
                // 巨大なものを除いた組と、全部の組（巨大なものは全部を覆う）
                foreach (var set in new[] { all.Take(all.Count - 3).ToList(), all.Skip(all.Count - 2).ToList(), all })
                {
                    var expected = FrozenFromTriangles(size, size, set); var mask = SelectionMask.FromTriangles(doc, set);
                    for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) Assert.That(mask[x, y], Is.EqualTo(expected[x, y]), $"size {size} seed {seed} ({x},{y})");
                }
                // 1 つずつ（辺の上・頂点の上のサンプルの決まりが三角形ごとに同じ）
                foreach (var t in all.Take(30))
                {
                    var one = new List<Tri> { t }; var expected = FrozenFromTriangles(size, size, one); var mask = SelectionMask.FromTriangles(doc, one);
                    for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) Assert.That(mask[x, y], Is.EqualTo(expected[x, y]), $"triangle {t} ({x},{y})");
                }
            }
        }

        [Test] public void FromTrianglesDoesNotDependOnTheThreadCount()
        {
            var doc = new PaintDocument(200, 200, 32); var set = Triangles(7, 200, 200).Take(212).ToList(); // 巨大なもの（全部を覆う）は除く
            int saved = CoreParallelism.MaxDegreeOfParallelism;
            try
            {
                CoreParallelism.MaxDegreeOfParallelism = 1; var one = SelectionMask.FromTriangles(doc, set);
                foreach (int degree in new[] { 2, 3, 0 })
                {
                    CoreParallelism.MaxDegreeOfParallelism = degree; var many = SelectionMask.FromTriangles(doc, set);
                    for (int y = 0; y < 200; y++) for (int x = 0; x < 200; x++) Assert.That(many[x, y], Is.EqualTo(one[x, y]), $"degree {degree} ({x},{y})");
                }
            }
            finally { CoreParallelism.MaxDegreeOfParallelism = saved; }
        }

        // ───── TriangleFill ─────

        /// <summary>色の付いた画素（透明な画素の RGB も）を散らした文書と、その写し。</summary>
        static PaintDocument Painted(out Guid layer, int size = 48, int tile = 16, int seed = 5)
        {
            var doc = new PaintDocument(size, size, tile); layer = doc.AddLayer("L").Id; var surface = doc.GetLayer(layer).GetChannel(PaintChannel.Color);
            var random = new Random(seed);
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                if (random.Next(3) > 0) surface.SetPixel(x, y, new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)(random.Next(2) == 0 ? 0 : random.Next(256))));
            doc.ClearHistory(); return doc;
        }

        static byte[] Pixels(PaintDocument doc, Guid layer, PaintChannel? channel = PaintChannel.Color)
        {
            var l = doc.GetLayer(layer); var surface = channel.HasValue ? l.GetChannel(channel.Value) : l.Mask.Surface;
            var result = new byte[doc.Width * doc.Height * 4];
            for (int y = 0; y < doc.Height; y++) for (int x = 0; x < doc.Width; x++)
            { var p = surface.GetPixel(x, y); int o = (y * doc.Width + x) * 4; result[o] = p.R; result[o + 1] = p.G; result[o + 2] = p.B; result[o + 3] = p.A; }
            return result;
        }

        static List<Tri> Island(double x0, double y0, double x1, double y1) => new List<Tri> { (x0, y0, x1, y0, x1, y1), (x0, y0, x1, y1, x0, y1) };

        /// <summary>A と B（B は A と辺を共有する）を、分けて・逆の順に・重ねて足しても、和集合を Fill したものと同じ。</summary>
        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(false, false, true)]
        public void AddingRegionsInAnyOrderMatchesFillingTheirUnion(bool erase, bool keepAlpha, bool withSelection)
        {
            var a = Island(3.3, 4.7, 20.2, 30.6); var b = Island(20.2, 4.7, 41.9, 30.6); // x = 20.2 の辺を共有（画素の真ん中を通る）
            b.Add((10.5, 31, 40.25, 44.875, 2, 46));
            foreach (var order in new[] { new[] { a, b }, new[] { b, a }, new[] { a, a, b, b, a }, new[] { a.Concat(b).ToList() } })
            {
                var reference = Painted(out var layer); var doc = Painted(out var layer2);
                if (keepAlpha) { reference.SetLayerLocks(layer, LayerLocks.Transparency); doc.SetLayerLocks(layer2, LayerLocks.Transparency); }
                if (withSelection) { reference.SetSelection(SelectionMask.Ellipse(reference, 24, 20, 17, 11)); doc.SetSelection(SelectionMask.Ellipse(doc, 24, 20, 17, 11)); }
                reference.Fill(layer, PaintChannel.Color, Red, .6, SelectionMask.FromTriangles(reference, a.Concat(b).ToList()), erase);
                var fill = doc.BeginTriangleFill(layer2, PaintChannel.Color, Red, .6, erase);
                foreach (var part in order) fill.Add(part);
                Assert.That(fill.Stroke.Commit(), Is.True);
                Assert.That(Pixels(doc, layer2), Is.EqualTo(Pixels(reference, layer)), $"erase {erase} keepAlpha {keepAlpha} selection {withSelection} order {order.Length}");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AMaskFillMatchesFillMaskOfTheUnion(bool reveal)
        {
            var a = Island(1.5, 1.5, 12.25, 30); var b = Island(12.25, 1.5, 30.75, 30);
            var reference = new PaintDocument(32, 32, 8); var layer = reference.AddLayer("L").Id; reference.AddLayerMask(layer);
            var doc = new PaintDocument(32, 32, 8); var layer2 = doc.AddLayer("L").Id; doc.AddLayerMask(layer2);
            // 半分隠したマスクから
            reference.FillMask(layer, .5, SelectionMask.Rectangle(reference, 0, 0, 16, 32)); doc.FillMask(layer2, .5, SelectionMask.Rectangle(doc, 0, 0, 16, 32));
            reference.FillMask(layer, .7, SelectionMask.FromTriangles(reference, a.Concat(b).ToList()), reveal);
            var fill = doc.BeginMaskTriangleFill(layer2, .7, reveal); fill.Add(b); fill.Add(a); fill.Stroke.Commit();
            Assert.That(Pixels(doc, layer2, null), Is.EqualTo(Pixels(reference, layer, null)));
        }

        /// <summary>共有辺の画素: 2 回に分けて足しても、辺の上の画素は内側と同じに塗られる（継ぎ目が無い）。被覆率を足し合わせる塗り方
        /// （0.5 と 0.5 で 0.75）にしていないこと。</summary>
        [Test] public void TheSharedEdgeLeavesNoSeamWhenAddedSeparately()
        {
            var doc = new PaintDocument(32, 32, 8); var layer = doc.AddLayer("L").Id;
            var fill = doc.BeginTriangleFill(layer, PaintChannel.Color, Red);
            fill.Add(new List<Tri> { (2, 2, 29.5, 2, 2, 29.5) }); // 斜めの辺が画素を半分に切る
            fill.Add(new List<Tri> { (29.5, 2, 29.5, 29.5, 2, 29.5) });
            fill.Stroke.Commit();
            var s = doc.GetLayer(layer).GetChannel(PaintChannel.Color);
            for (int y = 2; y < 29; y++) for (int x = 2; x < 29; x++) Assert.That(s.GetPixel(x, y), Is.EqualTo(Red), $"({x},{y})");
            Assert.That(s.GetPixel(29, 10).A, Is.InRange(1, 254), "the outer edge stays anti-aliased");
        }

        [Test] public void OneUndoStepRestoresExactlyAndRedoReapplies()
        {
            var doc = Painted(out var layer); var before = Pixels(doc, layer); int steps = doc.UndoCount;
            var fill = doc.BeginTriangleFill(layer, PaintChannel.Color, Red);
            Assert.That(doc.HasActiveStroke, Is.True); Assert.That(doc.CanUndo, Is.False);
            Assert.Throws<InvalidOperationException>(() => doc.AddLayer("during"), "nothing else edits the document during the fill");
            Assert.That(fill.Add(Island(2, 2, 20, 20)), Is.True); Assert.That(fill.Add(Island(22, 22, 40, 40)), Is.True);
            Assert.That(fill.Add(Island(2, 2, 20, 20)), Is.False, "adding a region again changes nothing");
            var painted = Pixels(doc, layer); Assert.That(painted, Is.Not.EqualTo(before));
            fill.Stroke.Commit();
            Assert.That(doc.HasActiveStroke, Is.False); Assert.That(doc.UndoCount, Is.EqualTo(steps + 1), "one undo step for the whole drag");
            Assert.That(Pixels(doc, layer), Is.EqualTo(painted));
            doc.Undo(); Assert.That(Pixels(doc, layer), Is.EqualTo(before));
            doc.Redo(); Assert.That(Pixels(doc, layer), Is.EqualTo(painted));
            Assert.Throws<InvalidOperationException>(() => fill.Add(Island(0, 0, 5, 5)), "a finished fill takes nothing more");
        }

        [Test] public void CancelPutsEveryPixelBackWithoutHistory()
        {
            var doc = Painted(out var layer); var before = Pixels(doc, layer); int steps = doc.UndoCount; long revision = doc.Revision;
            var fill = doc.BeginTriangleFill(layer, PaintChannel.Color, Red, 1, erase: true);
            fill.Add(Island(0, 0, 48, 48));
            Assert.That(doc.Revision, Is.GreaterThan(revision), "the display sees the fill while it runs");
            fill.Stroke.Cancel();
            Assert.That(Pixels(doc, layer), Is.EqualTo(before)); Assert.That(doc.UndoCount, Is.EqualTo(steps)); Assert.That(doc.HasActiveStroke, Is.False);
            // 何も塗らずに確定しても履歴は増えない
            var empty = doc.BeginTriangleFill(layer, PaintChannel.Color, Red); empty.Add(new List<Tri> { (60, 60, 70, 60, 60, 70) });
            Assert.That(empty.Stroke.Commit(), Is.False); Assert.That(doc.UndoCount, Is.EqualTo(steps));
        }

        [Test] public void TheFilledPixelsSurviveSaveAndLoad()
        {
            var doc = Painted(out var layer);
            var fill = doc.BeginTriangleFill(layer, PaintChannel.Color, Red, .8); fill.Add(Island(1.2, 3.4, 30.1, 25.6)); fill.Stroke.Commit();
            var restored = DocumentBinary.Read(DocumentBinary.Write(doc));
            Assert.That(Pixels(restored, layer), Is.EqualTo(Pixels(doc, layer)));
            Assert.That(restored.Composite(PaintChannel.Color), Is.EqualTo(doc.Composite(PaintChannel.Color)));
        }

        [Test] public void LocksRefuseOrKeepAlphaLikeFill()
        {
            var doc = Painted(out var layer); var before = Pixels(doc, layer);
            foreach (var (locks, erase) in new[] { (LayerLocks.Pixels, false), (LayerLocks.All, false), (LayerLocks.Transparency, true) })
            {
                doc.SetLayerLocks(layer, locks);
                var refused = Assert.Throws<LayerLockedException>(() => doc.BeginTriangleFill(layer, PaintChannel.Color, Red, 1, erase), locks.ToString());
                Assert.That(refused.Lock, Is.EqualTo(locks)); Assert.That(refused.Message, Does.Contain("Nothing was changed"));
                Assert.That(doc.HasActiveStroke, Is.False); Assert.That(Pixels(doc, layer), Is.EqualTo(before));
            }
            // マスクは画像のロックでは塗れ、すべてのロックでは断る（Photoshop・FillMask と同じ）
            doc.SetLayerLocks(layer, LayerLocks.None); doc.AddLayerMask(layer); doc.SetLayerLocks(layer, LayerLocks.Pixels);
            var mask = doc.BeginMaskTriangleFill(layer); mask.Add(Island(0, 0, 10, 10)); mask.Stroke.Commit();
            Assert.That(doc.GetLayer(layer).Mask.Surface.GetPixel(5, 5).A, Is.EqualTo(255));
            doc.SetLayerLocks(layer, LayerLocks.All);
            Assert.Throws<LayerLockedException>(() => doc.BeginMaskTriangleFill(layer));
            // グループのロックも効く
            doc.SetLayerLocks(layer, LayerLocks.None); var group = doc.GroupLayers(new[] { layer }, "G"); doc.SetLayerLocks(group.Id, LayerLocks.Pixels);
            Assert.That(Assert.Throws<LayerLockedException>(() => doc.BeginTriangleFill(layer, PaintChannel.Color, Red)).LockedBy, Is.EqualTo(group.Id));
        }

        [Test] public void WrongTargetsAndInputsAreRefusedWithoutChanges()
        {
            var doc = new PaintDocument(32, 32, 8); var paint = doc.AddLayer("P");
            var fillLayer = doc.AddFillLayer("F", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, Red } });
            var group = doc.AddGroup("G"); var adjustment = doc.AddAdjustmentLayer("A", AdjustmentSettings.Invert());
            foreach (var id in new[] { fillLayer.Id, group.Id, adjustment.Id })
                Assert.Throws<InvalidOperationException>(() => doc.BeginTriangleFill(id, PaintChannel.Color, Red));
            Assert.Throws<InvalidOperationException>(() => doc.BeginTriangleFill(paint.Id, PaintChannel.Roughness, Red), "a disabled channel");
            Assert.Throws<InvalidOperationException>(() => doc.BeginMaskTriangleFill(paint.Id), "no mask");
            Assert.Throws<ArgumentOutOfRangeException>(() => doc.BeginTriangleFill(paint.Id, PaintChannel.Color, Red, 1.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => doc.BeginTriangleFill(paint.Id, PaintChannel.Color, Red, double.NaN));
            Assert.That(doc.HasActiveStroke, Is.False);
            // 有限でない座標は、その前に足した分ごと取り消す
            var fill = doc.BeginTriangleFill(paint.Id, PaintChannel.Color, Red); fill.Add(Island(0, 0, 10, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => fill.Add(new List<Tri> { (0, 0, double.PositiveInfinity, 0, 0, 5) }));
            Assert.That(fill.IsFinished, Is.True); Assert.That(doc.HasActiveStroke, Is.False);
            Assert.That(paint.GetChannel(PaintChannel.Color).TileCount, Is.Zero);
            Assert.That(() => doc.BeginTriangleFill(paint.Id, PaintChannel.Color, Red).Add(null), Throws.ArgumentNullException);
        }

        [Test] public void OverTheStrokeBudgetTheWholeFillIsCancelled()
        {
            var doc = Painted(out var layer, 256, 32); var before = Pixels(doc, layer); int steps = doc.UndoCount;
            doc.ActiveStrokeBudgetBytes = 200 * 1024; // 32² のタイルの写し（4 KiB）と被覆のビット（2 KiB）で 30 枚ほど
            var fill = doc.BeginTriangleFill(layer, PaintChannel.Color, Red);
            fill.Add(Island(0, 0, 60, 60));
            Assert.That(Pixels(doc, layer), Is.Not.EqualTo(before));
            var refused = Assert.Throws<InvalidOperationException>(() => fill.Add(Island(0, 0, 256, 256)));
            Assert.That(refused.Message, Does.Contain("budget"));
            Assert.That(fill.IsFinished, Is.True); Assert.That(doc.HasActiveStroke, Is.False);
            Assert.That(Pixels(doc, layer), Is.EqualTo(before), "the part added before the refusal is gone too");
            Assert.That(doc.UndoCount, Is.EqualTo(steps));
        }

        [Test] public void FullyCoveredTilesKeepNoSampleMemory()
        {
            var doc = new PaintDocument(256, 256, 32); var layer = doc.AddLayer("L").Id;
            var fill = doc.BeginTriangleFill(layer, PaintChannel.Color, Red);
            fill.Add(Island(0, 0, 256, 256));
            Assert.That(fill.CoveredTileCount, Is.EqualTo(64));
            Assert.That(fill.Stroke.RollbackBytes, Is.LessThan(64 * (64 + 32 * 32 * 2)), "no sample masks were kept for full tiles");
            fill.Stroke.Commit();
            Assert.That(doc.GetLayer(layer).GetChannel(PaintChannel.Color).GetPixel(255, 255), Is.EqualTo(Red));
        }

        [Test] public void TheResultDoesNotDependOnTheThreadCount()
        {
            int saved = CoreParallelism.MaxDegreeOfParallelism; byte[] one = null;
            try
            {
                foreach (int degree in new[] { 1, 2, 3, 0 })
                {
                    CoreParallelism.MaxDegreeOfParallelism = degree;
                    var doc = Painted(out var layer, 200, 16, 9); doc.SetSelection(SelectionMask.Ellipse(doc, 100, 100, 90, 70));
                    var fill = doc.BeginTriangleFill(layer, PaintChannel.Color, Red, .7); fill.Add(Triangles(11, 200, 80).Take(92).ToList()); fill.Stroke.Commit();
                    var pixels = Pixels(doc, layer);
                    if (one == null) one = pixels; else Assert.That(pixels, Is.EqualTo(one), "degree " + degree);
                }
            }
            finally { CoreParallelism.MaxDegreeOfParallelism = saved; }
        }
    }
}
