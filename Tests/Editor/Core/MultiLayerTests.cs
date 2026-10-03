using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 複数の層への操作（レイヤーの複数選択）: 削除・複製・表示・並べ替え（ドラッグ・1 つ上／下）・移動と変形・結合が、どれも 1 回の Undo で、
    /// 断る・失敗するときは何も変えない（予算・ロック・違うグループ）こと。選んだグループの中の層はグループと一緒に動く。
    /// 複数の層の結合は、選んだ層だけを分離のグループにまとめたように合成し、見た目の変化を独立の合成で量って確かめる。
    /// </summary>
    public sealed class MultiLayerTests
    {
        const int W = 40, H = 30, Tile = 16;
        static PaintDocument NewDocument() => new PaintDocument(W, H, Tile, 256L << 20) { ActiveStrokeBudgetBytes = 64L << 20 };
        static string Order(PaintDocument d) => string.Join(",", d.Layers.Select(l => (l.ParentId == Guid.Empty ? "" : d.GetLayer(l.ParentId).Name + "/") + l.Name));
        static PaintLayer Solid(PaintDocument d, string name, Rgba32 color, int x0, int y0, int x1, int y1)
        { var l = d.AddLayer(name); d.Fill(l.Id, PaintChannel.Color, color, 1, SelectionMask.Rectangle(d, x0, y0, x1, y1)); return l; }
        static Dictionary<PaintChannel, byte[]> Composites(PaintDocument d)
        { var all = new Dictionary<PaintChannel, byte[]>(); foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) all[c] = d.Composite(c); return all; }

        [Test] public void TopmostOfLeavesOutLayersInsideAChosenGroup()
        {
            var d = NewDocument(); var a = d.AddLayer("a"); var b = d.AddLayer("b"); var g = d.GroupLayers(new[] { a.Id }, "g");
            Assert.That(d.TopmostOf(new[] { b.Id, a.Id, g.Id, a.Id }).Select(l => l.Name), Is.EqualTo(new[] { "g", "b" }), "bottom to top, once each, a goes with g");
            Assert.That(() => d.TopmostOf(new[] { Guid.NewGuid() }), Throws.InstanceOf<KeyNotFoundException>());
        }

        [Test] public void RemovingDuplicatingAndHidingSeveralLayersAreOneUndoStepEach()
        {
            var d = NewDocument(); var a = Solid(d, "a", new Rgba32(200, 0, 0, 255), 0, 0, 10, 10); var b = Solid(d, "b", new Rgba32(0, 200, 0, 255), 5, 5, 20, 20);
            var c = d.AddLayer("c"); var g = d.GroupLayers(new[] { c.Id }, "g"); var top = d.AddLayer("top"); d.ClearHistory();
            var before = Composites(d); string order = Order(d);

            d.RemoveLayers(new[] { a.Id, g.Id, c.Id });
            Assert.That(Order(d), Is.EqualTo("b,top")); Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo(); Assert.That(Order(d), Is.EqualTo(order)); Assert.That(Composites(d), Is.EqualTo(before));
            d.Redo(); Assert.That(Order(d), Is.EqualTo("b,top")); d.Undo();

            var copies = d.DuplicateLayers(new[] { top.Id, a.Id, g.Id }, n => n + " copy");
            Assert.That(copies.Select(l => l.Name), Is.EqualTo(new[] { "a copy", "g copy", "top copy" }));
            Assert.That(Order(d), Is.EqualTo("a,a copy,b,g/c,g,g copy/c,g copy,top,top copy"), "each copy right above its original, a group with its contents");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo(); Assert.That(Order(d), Is.EqualTo(order));

            d.SetLayersVisibility(new[] { a.Id, b.Id, c.Id }, false);
            Assert.That(new[] { a.Visible, b.Visible, c.Visible, top.Visible }, Is.EqualTo(new[] { false, false, false, true }));
            Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo(); Assert.That(Composites(d), Is.EqualTo(before));
        }

        [Test] public void DuplicatingSeveralLayersKeepsNoneWhenTheSourceBudgetRunsOut()
        {
            var d = NewDocument(); var a = Solid(d, "a", new Rgba32(200, 0, 0, 255), 0, 0, W - 3, H - 3); var b = Solid(d, "b", new Rgba32(0, 200, 0, 255), 3, 3, W, H);
            d.ClearHistory();
            Assert.That(b.AllocatedBytes, Is.GreaterThan(1000), "tiles with pixels");
            d.SourceBudgetBytes = d.AllocatedBytes + a.AllocatedBytes + b.AllocatedBytes / 2; // 1 つ目の複製は入り、2 つ目は入らない
            string order = Order(d);
            Assert.That(() => d.DuplicateLayers(new[] { a.Id, b.Id }), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(Order(d), Is.EqualTo(order), "the first copy was taken back");
            Assert.That((d.UndoCount, d.RedoCount), Is.EqualTo((0, 0)));
        }

        [Test] public void MovingSeveralLayersKeepsTheirOrderAndCanGoIntoAGroup()
        {
            var d = NewDocument(); var a = d.AddLayer("a"); var b = d.AddLayer("b"); var c = d.AddLayer("c"); var e = d.AddLayer("e");
            var x = d.AddLayer("x"); var g = d.GroupLayers(new[] { x.Id }, "g"); d.ClearHistory();
            Assert.That(Order(d), Is.EqualTo("a,b,c,e,g/x,g"));
            d.MoveLayers(new[] { e.Id, a.Id }, Guid.Empty, 2); // 残り b, c, g のうち 2 番目（c）の上
            Assert.That(Order(d), Is.EqualTo("b,c,a,e,g/x,g")); Assert.That(d.UndoCount, Is.EqualTo(1));
            d.MoveLayers(new[] { b.Id, e.Id }, g.Id, 1); // g の中の x の上
            Assert.That(Order(d), Is.EqualTo("c,a,g/x,g/b,g/e,g"));
            d.MoveLayers(new[] { g.Id, x.Id, c.Id }, Guid.Empty, 0); // x は g と一緒
            Assert.That(Order(d), Is.EqualTo("c,g/x,g/b,g/e,g,a"));
            Assert.That(() => d.MoveLayers(new[] { g.Id, a.Id }, g.Id, 0), Throws.InvalidOperationException, "not into a chosen group");
            Assert.That(() => d.MoveLayers(new[] { a.Id }, Guid.Empty, 9), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(d.UndoCount, Is.EqualTo(3));
            d.Undo(); d.Undo(); d.Undo();
            Assert.That(Order(d), Is.EqualTo("a,b,c,e,g/x,g"));
        }

        [Test] public void SteppingSeveralLayersMovesEachOneStepUnlessHeldBack()
        {
            var d = NewDocument(); var a = d.AddLayer("a"); var b = d.AddLayer("b"); var c = d.AddLayer("c"); var e = d.AddLayer("e"); d.ClearHistory();
            Assert.That(d.StepLayers(new[] { a.Id, c.Id }, up: true), Is.True);
            Assert.That(Order(d), Is.EqualTo("b,a,e,c")); Assert.That(d.UndoCount, Is.EqualTo(1));
            Assert.That(d.StepLayers(new[] { e.Id, c.Id }, up: true), Is.False, "c is on top and holds e back");
            Assert.That(d.StepLayers(new[] { b.Id, e.Id }, up: false), Is.True);
            Assert.That(Order(d), Is.EqualTo("b,e,a,c"), "b stays at the bottom, e steps down");
            d.Undo(); d.Undo();
            Assert.That(Order(d), Is.EqualTo("a,b,c,e"));
        }

        [Test] public void TransformingSeveralLayersMovesThemAndTheSelectionOnceAsOneUndoStep()
        {
            var d = NewDocument(); var a = Solid(d, "a", new Rgba32(200, 0, 0, 255), 2, 2, 6, 6); var b = Solid(d, "b", new Rgba32(0, 0, 200, 255), 10, 10, 14, 14);
            var fill = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(1, 1, 1, 255) } });
            var g = d.GroupLayers(new[] { b.Id, fill.Id }, "g");
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 20, 20)); d.ClearHistory();
            Assert.That(d.TransformLayers(new[] { a.Id, g.Id }, Affine2D.Translation(5, 3)), Is.True);
            Assert.That(a.GetPixel(PaintChannel.Color, 7, 5), Is.EqualTo(new Rgba32(200, 0, 0, 255)));
            Assert.That(b.GetPixel(PaintChannel.Color, 15, 13), Is.EqualTo(new Rgba32(0, 0, 200, 255)), "the paint layer inside the chosen group moved; the fill layer has no pixels to move");
            Assert.That(d.Selection[22, 21], Is.EqualTo(255)); Assert.That(d.Selection[2, 2], Is.EqualTo(0), "the selection moved once, not twice");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo();
            Assert.That(a.GetPixel(PaintChannel.Color, 3, 3), Is.EqualTo(new Rgba32(200, 0, 0, 255))); Assert.That(d.Selection[2, 2], Is.EqualTo(255));
            Assert.That(() => d.TransformLayers(new[] { fill.Id }, Affine2D.Translation(1, 0)), Throws.InvalidOperationException, "nothing to move");
        }

        [Test] public void TransformingSeveralLayersChangesNothingWhenTheBudgetOrALockRefuses()
        {
            var d = NewDocument(); var a = Solid(d, "a", new Rgba32(200, 0, 0, 255), 0, 0, W, H); var b = Solid(d, "b", new Rgba32(0, 0, 200, 255), 0, 0, W, H);
            d.ClearHistory(); var before = Composites(d);
            // 1 つの層の巻き戻しは予算に入るが、2 つ分は入らない
            long one = a.AllocatedBytes + 64L * 6;
            d.ActiveStrokeBudgetBytes = one + one / 2;
            Assert.That(d.Transform(a.Id, Affine2D.Translation(1, 0)), Is.True, "one layer fits"); d.Undo();
            Assert.That(() => d.TransformLayers(new[] { a.Id, b.Id }, Affine2D.Translation(1, 0)), Throws.InvalidOperationException.With.Message.Contains("one-operation budget"));
            Assert.That(Composites(d), Is.EqualTo(before), "the first layer was put back");
            Assert.That(d.UndoCount, Is.EqualTo(0));
            d.ActiveStrokeBudgetBytes = 64L << 20;
            d.SetLayerLocks(b.Id, LayerLocks.Position); d.ClearHistory();
            Assert.That(() => d.TransformLayers(new[] { a.Id, b.Id }, Affine2D.Translation(1, 0)), Throws.InstanceOf<LayerLockedException>());
            Assert.That(Composites(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.EqualTo(0));
        }

        // ───────── 結合 ─────────

        [Test] public void MergingSeveralLayersOfAnIsolatedStackIsExact()
        {
            var r = new Random(5); var d = NewDocument();
            var a = Solid(d, "a", new Rgba32(200, 30, 30, 160), 0, 0, 25, 20); d.SetLayerBlendMode(a.Id, LayerBlendMode.Screen);
            var b = Solid(d, "b", new Rgba32(30, 200, 30, 200), 10, 5, 35, 28); d.SetLayerOpacity(b.Id, .6);
            var c = Solid(d, "c", new Rgba32(30, 30, 200, 255), 18, 0, 40, 12); d.SetLayerBlendMode(c.Id, LayerBlendMode.Multiply);
            var top = d.AddLayer("top"); d.ClearHistory();
            var before = Composites(d);
            var report = d.MergeLayers(new[] { c.Id, a.Id, b.Id });
            Assert.That(report.Method, Is.EqualTo(MergeMethod.Layers));
            Assert.That(report.Exact, Is.True, "the three are the whole stack below with nothing under them");
            var merged = d.GetLayer(report.ResultId);
            Assert.That((merged.Name, merged.BlendMode, merged.Opacity, merged.Clipping), Is.EqualTo(("c", LayerBlendMode.Normal, 1.0, false)), "named after the topmost, Normal at 100 %");
            Assert.That(Order(d), Is.EqualTo("c,top"));
            Assert.That(Composites(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo(); Assert.That(Order(d), Is.EqualTo("a,b,c,top")); Assert.That(Composites(d), Is.EqualTo(before));
        }

        [Test] public void MergingSeveralLayersOverABackgroundStaysWithinRoundingAndAsksWhenTheLookChanges()
        {
            var d = NewDocument();
            var bg = Solid(d, "bg", new Rgba32(120, 130, 140, 255), 0, 0, W, H);
            var a = Solid(d, "a", new Rgba32(250, 40, 40, 150), 0, 0, 30, 20);
            var b = Solid(d, "b", new Rgba32(40, 250, 40, 120), 10, 5, 40, 30);
            d.ClearHistory(); var before = Composites(d);
            var report = d.MergeLayers(new[] { a.Id, b.Id });
            Assert.That(report.MaxVisibleDifference, Is.LessThanOrEqualTo(PaintDocument.MergeRoundingTolerance), "Normal layers over a background: only the rounding of another order");
            d.Undo(); Assert.That(Composites(d), Is.EqualTo(before));
            // 選んだ層の一番下が乗算: 背景との乗算が無くなるので見た目が変わる（Photoshop と同じ）。確かめのために断る
            d.SetLayerBlendMode(a.Id, LayerBlendMode.Multiply); d.ClearHistory(); before = Composites(d);
            var ex = Assert.Throws<LayerMergeException>(() => d.MergeLayers(new[] { a.Id, b.Id }));
            Assert.That(ex.Report.ChangedPixels, Is.GreaterThan(0));
            Assert.That(Composites(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.EqualTo(0));
            var allowed = d.MergeLayers(new[] { a.Id, b.Id }, 255);
            Assert.That(allowed.ChangedPixels, Is.EqualTo(ex.Report.ChangedPixels));
        }

        [Test] public void MergingLayersClippedToOneBaseKeepsThemClipped()
        {
            var d = NewDocument();
            var baseLayer = Solid(d, "base", new Rgba32(200, 200, 200, 255), 5, 5, 30, 25);
            var c1 = Solid(d, "c1", new Rgba32(255, 0, 0, 180), 0, 0, 20, 30); d.SetLayerClipping(c1.Id, true);
            var c2 = Solid(d, "c2", new Rgba32(0, 0, 255, 120), 15, 0, 40, 30); d.SetLayerClipping(c2.Id, true);
            d.ClearHistory(); var before = Composites(d);
            var report = d.MergeLayers(new[] { c1.Id, c2.Id });
            var merged = d.GetLayer(report.ResultId);
            Assert.That(merged.Clipping, Is.True, "both were clipped to the same base");
            Assert.That(report.MaxVisibleDifference, Is.LessThanOrEqualTo(PaintDocument.MergeRoundingTolerance), "only rounding: c2 over c1, clipped, is what showed");
            Assert.That(Order(d), Is.EqualTo("base,c2"));
        }

        [Test] public void MergingSeveralLayersMeasuresTheLayersBetweenThemToo()
        {
            var d = NewDocument();
            var a = Solid(d, "a", new Rgba32(200, 0, 0, 255), 0, 0, 10, 10);
            var between = Solid(d, "between", new Rgba32(0, 200, 0, 255), 20, 20, 40, 30); d.SetLayerClipping(between.Id, true); // a にクリッピング: a の外では見えない
            var x = Solid(d, "x", new Rgba32(0, 0, 200, 255), 0, 0, 5, 5);
            d.ClearHistory();
            // 結合すると between は一番下になり、クリッピングが効かなくなって a の外に出る。その画素も量る
            var ex = Assert.Throws<LayerMergeException>(() => d.MergeLayers(new[] { a.Id, x.Id }));
            Assert.That(ex.Report.ChangedPixels, Is.EqualTo(20 * 10), "between shows outside a where it did not");
        }

        [Test] public void MergingSeveralLayersRefusesWhatItCannotDoAndChangesNothing()
        {
            var d = NewDocument(); var a = Solid(d, "a", new Rgba32(200, 0, 0, 255), 0, 0, 10, 10); var b = Solid(d, "b", new Rgba32(0, 200, 0, 255), 0, 0, 10, 10);
            var inner = d.AddLayer("inner"); var g = d.GroupLayers(new[] { inner.Id }, "g"); d.ClearHistory();
            string order = Order(d);
            Assert.That(() => d.MergeLayers(new[] { a.Id }), Throws.ArgumentException, "one layer merges down");
            Assert.That(() => d.MergeLayers(new[] { a.Id, inner.Id }), Throws.InstanceOf<LayerOpException>().With.Property("Reason").EqualTo(LayerOpRefusal.DifferentGroups));
            d.SetLayerVisibility(b.Id, false); d.ClearHistory();
            Assert.That(() => d.MergeLayers(new[] { a.Id, b.Id }), Throws.InstanceOf<LayerOpException>().With.Property("Reason").EqualTo(LayerOpRefusal.HiddenLayer));
            d.SetLayerVisibility(b.Id, true); d.ClearHistory();
            d.ActiveStrokeBudgetBytes = 64;
            Assert.That(() => d.MergeLayers(new[] { a.Id, b.Id }), Throws.InstanceOf<LayerOpException>().With.Property("Reason").EqualTo(LayerOpRefusal.OperationBudget));
            Assert.That(Order(d), Is.EqualTo(order)); Assert.That(d.UndoCount, Is.EqualTo(0));
            d.ActiveStrokeBudgetBytes = 64L << 20;
            // グループを選べば中身ごと（中の非表示の層は捨てると知らせる）
            var hidden = d.AddLayer("hidden", above: inner.Id); d.SetLayerVisibility(hidden.Id, false);
            var report = d.MergeLayers(new[] { b.Id, g.Id });
            Assert.That(report.Notes & MergeNotes.HiddenLayersDropped, Is.EqualTo(MergeNotes.HiddenLayersDropped));
            Assert.That(Order(d), Is.EqualTo("a,g"));
        }
    }
}
