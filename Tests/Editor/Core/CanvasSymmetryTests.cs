using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class CanvasSymmetryTests
    {
        static PaintDocument Make(out Guid id, int side = 64) { var d = new PaintDocument(side, side, 16, 256L << 20); id = d.AddLayer("paint").Id; d.ClearHistory(); return d; }
        static BrushSettings Brush(CanvasSymmetryMode mode, int count = 4) => new BrushSettings { Radius = 4, Hardness = .6, PressureSize = false, PressureOpacity = false,
            Flow = .5, CanvasSymmetry = new CanvasSymmetrySettings { Mode = mode, CenterX = 32, CenterY = 32, Count = count } };
        static void Dot(PaintDocument d, Guid id, BrushSettings b, double x = 20.5, double y = 23.5) { using (var s = d.BeginStroke(id, PaintChannel.Color, b)) { s.Add(new BrushSample(x, y)); s.Commit(); } }
        static byte[] Bytes(PaintDocument d, Guid id) => d.Composite(PaintChannel.Color);

        [TestCase(CanvasSymmetryMode.Vertical, 2)] [TestCase(CanvasSymmetryMode.Horizontal, 2)] [TestCase(CanvasSymmetryMode.Both, 4)] [TestCase(CanvasSymmetryMode.Radial, 7)]
        public void TransformsHaveTheRequestedCentersAndReturnByTheirInverse(CanvasSymmetryMode mode, int count)
        {
            var s = new CanvasSymmetrySettings { Mode = mode, CenterX = 11.25, CenterY = 7.75, Count = count };
            var t = s.Transforms(); Assert.That(t.Count, Is.EqualTo(mode == CanvasSymmetryMode.Radial ? count : mode == CanvasSymmetryMode.Both ? 4 : 2));
            for (int i = 0; i < t.Count; i++)
            {
                t[i].Map(18.5, 2.5, out double x, out double y);
                if (mode == CanvasSymmetryMode.Radial) { double a = 2 * Math.PI * i / count; Assert.That(x, Is.EqualTo(11.25 + Math.Cos(a) * 7.25 + Math.Sin(a) * 5.25).Within(1e-12)); Assert.That(y, Is.EqualTo(7.75 + Math.Sin(a) * 7.25 - Math.Cos(a) * 5.25).Within(1e-12)); }
                else if (i == 1 && mode != CanvasSymmetryMode.Horizontal) Assert.That(x, Is.EqualTo(4));
                else if (i == 1) Assert.That(y, Is.EqualTo(13));
                t[i].Inverse(x, y, out double ox, out double oy); Assert.That(ox, Is.EqualTo(18.5).Within(1e-12)); Assert.That(oy, Is.EqualTo(2.5).Within(1e-12));
            }
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(7)] [TestCase(16)]
        public void RadialFootprintsMatchAnalyticalCirclesAndUseMaximumCoverage(int count)
        {
            var d = Make(out var id); var b = Brush(CanvasSymmetryMode.Radial, count); Dot(d, id, b);
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            {
                double largest = 0;
                for (int i = 0; i < count; i++)
                {
                    double a = 2 * Math.PI * i / count, cx = 32 - 11.5 * Math.Cos(a) + 8.5 * Math.Sin(a), cy = 32 - 11.5 * Math.Sin(a) - 8.5 * Math.Cos(a);
                    double r = Math.Sqrt((x + .5 - cx) * (x + .5 - cx) + (y + .5 - cy) * (y + .5 - cy)) / 4;
                    double coverage = r > 1 ? 0 : r <= .6 ? 1 : Math.Pow((1 - r) / .4, 2) * (3 - 2 * (1 - r) / .4);
                    largest = Math.Max(largest, coverage);
                }
                int alpha = (int)Math.Floor(largest * .5 * 255 + .5);
                Assert.That(d.GetLayer(id).GetPixel(PaintChannel.Color, x, y).A, Is.EqualTo(alpha).Within(1), x + "," + y);
            }
        }
        [TestCase(CanvasSymmetryMode.Vertical)] [TestCase(CanvasSymmetryMode.Horizontal)] [TestCase(CanvasSymmetryMode.Both)]
        public void AsymmetricTipsAreReflectedWithTheirShape(CanvasSymmetryMode mode)
        {
            var single = Make(out var a); var symmetric = Make(out var b);
            var settings = Brush(CanvasSymmetryMode.None); settings.Tip = new BrushTip("asymmetric", 3, 2, new byte[] { 0, 80, 255, 255, 20, 0 }); settings.Angle = 31; settings.Roundness = .7;
            Dot(single, a, settings); settings.CanvasSymmetry.Mode = mode; Dot(symmetric, b, settings);
            var source = Bytes(single, a); var result = Bytes(symmetric, b);
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            {
                int Alpha(int px, int py) => source[(py * 64 + px) * 4 + 3]; int expected = Alpha(x, y);
                if (mode != CanvasSymmetryMode.Horizontal) expected = Math.Max(expected, Alpha(63 - x, y));
                if (mode != CanvasSymmetryMode.Vertical) expected = Math.Max(expected, Alpha(x, 63 - y));
                if (mode == CanvasSymmetryMode.Both) expected = Math.Max(expected, Alpha(63 - x, 63 - y));
                Assert.That(result[(y * 64 + x) * 4 + 3], Is.EqualTo(expected).Within(1));
            }
        }
        [TestCase(CanvasSymmetryMode.Both)] [TestCase(CanvasSymmetryMode.Radial)]
        public void CoincidentCopiesPaintOnceAndSettingsAreFrozen(CanvasSymmetryMode mode)
        {
            var single = Make(out var a); var symmetric = Make(out var b); var settings = Brush(mode, 16); settings.Hardness = 1;
            var plain = Brush(CanvasSymmetryMode.None); plain.Hardness = 1; Dot(single, a, plain, 32, 32);
            using (var s = symmetric.BeginStroke(b, PaintChannel.Color, settings)) { settings.CanvasSymmetry.Mode = CanvasSymmetryMode.None; settings.CanvasSymmetry.CenterX = 0; s.Add(new BrushSample(32, 32)); s.Commit(); }
            Assert.That(Bytes(symmetric, b), Is.EqualTo(Bytes(single, a))); Assert.That(symmetric.UndoCount, Is.EqualTo(1));
            var after = DocumentBinary.Write(symmetric); symmetric.Undo(); Assert.That(Bytes(symmetric, b), Is.All.Zero); symmetric.Redo(); Assert.That(DocumentBinary.Write(symmetric), Is.EqualTo(after));
        }
        [Test] public void MaterialChannelsSelectionMaskAndLocksKeepTheirContracts()
        {
            var d = Make(out var id); var b = Brush(CanvasSymmetryMode.Both); b.Flow = 1;
            d.SetSelection(SelectionMask.Rectangle(d, 32, 0, 64, 64)); d.ClearHistory();
            using (var s = d.BeginMaterialStroke(id, new[] { new ChannelPaint(PaintChannel.Color, new Rgba32(200, 20, 10, 255)), new ChannelPaint(PaintChannel.Roughness, new Rgba32(90, 90, 90, 255)) }, b)) { s.Add(new BrushSample(20.5, 23.5)); s.Commit(); }
            Assert.That(d.GetLayer(id).GetPixel(PaintChannel.Color, 20, 23).A, Is.Zero); Assert.That(d.GetLayer(id).GetPixel(PaintChannel.Color, 43, 23).R, Is.EqualTo(200)); Assert.That(d.GetLayer(id).GetPixel(PaintChannel.Roughness, 43, 23).R, Is.EqualTo(90));
            Assert.That(d.UndoCount, Is.EqualTo(1)); d.Undo(); Assert.That(Bytes(d, id), Is.All.Zero); d.Redo();
            d.SetLayerLocks(id, LayerLocks.Pixels); d.ClearHistory(); var before = DocumentBinary.Write(d);
            Assert.Throws<LayerLockedException>(() => d.BeginStroke(id, PaintChannel.Color, b)); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            d.SetLayerLocks(id, LayerLocks.None); d.AddLayerMask(id); d.ClearHistory();
            b.Erase = false; using (var s = d.BeginMaskStroke(id, b)) { s.Add(new BrushSample(20.5, 23.5)); s.Commit(); }
            Assert.That(d.GetLayer(id).Mask.FactorAt(43, 23), Is.LessThan(1)); Assert.That(d.UndoCount, Is.EqualTo(1));
        }
        [TestCase(false)] [TestCase(true)]
        public void CancellationAndBudgetRefusalRestoreEveryChannel(bool budget)
        {
            var d = Make(out var id); var before = DocumentBinary.Write(d); var b = Brush(CanvasSymmetryMode.Both);
            if (budget) d.ActiveStrokeBudgetBytes = 4096;
            using (var s = d.BeginMaterialStroke(id, new[] { new ChannelPaint(PaintChannel.Color, new Rgba32(200, 20, 10, 255)), new ChannelPaint(PaintChannel.Height, new Rgba32(90, 90, 90, 255)) }, b))
            {
                if (budget) { Assert.Throws<InvalidOperationException>(() => s.Add(new BrushSample(20.5, 23.5))); Assert.That(s.IsFinished, Is.True); }
                else { s.Add(new BrushSample(20.5, 23.5)); Assert.That(s.RollbackBytes, Is.GreaterThan(0)); s.Cancel(); }
                Assert.That(s.RollbackBytes, Is.Zero);
            }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.Zero);
        }
        [Test] public void WorkBudgetIsRejectedBeforeAllocatingCoverage()
        {
            var d = Make(out var id, 2048); var b = Brush(CanvasSymmetryMode.Radial, 16); b.Radius = 2000;
            using (var s = d.BeginStroke(id, PaintChannel.Color, b)) { Assert.That(Assert.Throws<InvalidOperationException>(() => s.Add(new BrushSample(1024, 1024))).Message, Does.Contain("work budget")); Assert.That(s.RollbackBytes, Is.Zero); }
            Assert.That(d.UndoCount, Is.Zero); Assert.That(d.HasActiveStroke, Is.False);
        }
        [TestCase(BrushEffect.Smudge)] [TestCase(BrushEffect.Clone)]
        public void EffectsThatNeedACopySourceAreRefusedBeforeChangingChannels(BrushEffect effect)
        {
            var d = Make(out var id); var b = Brush(CanvasSymmetryMode.Vertical); b.Effect = effect; var before = DocumentBinary.Write(d);
            Assert.That(Assert.Throws<InvalidOperationException>(() => d.BeginStroke(id, PaintChannel.Color, b)).Message, Does.Contain("separate source")); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
        }
        [TestCase(0)] [TestCase(1)] [TestCase(17)]
        public void InvalidCountsAreRejected(int count) { var b = Brush(CanvasSymmetryMode.Radial, count); Assert.Throws<ArgumentOutOfRangeException>(() => b.Validate()); }
        [Test] public void InvalidModesAndNonfiniteCentersAreRejected()
        { var b = Brush((CanvasSymmetryMode)99); Assert.Throws<ArgumentOutOfRangeException>(() => b.Validate()); b.CanvasSymmetry.Mode = CanvasSymmetryMode.Vertical; b.CanvasSymmetry.CenterX = double.NaN; Assert.Throws<ArgumentOutOfRangeException>(() => b.Validate()); }
        [Test] public void BlurReadsOneFrozenFrameForAllOverlappingCopies()
        {
            var d = Make(out var id); var reference = Make(out var rid);
            foreach (var pair in new[] { (d, id), (reference, rid) }) for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) pair.Item1.GetLayer(pair.Item2).GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32((byte)(x % 2 * 200), (byte)(y % 3 * 90), 50, 255));
            d.ClearHistory(); reference.ClearHistory(); var settings = Brush(CanvasSymmetryMode.Both); settings.Radius = 8; settings.Hardness = 1; settings.Effect = BrushEffect.Blur;
            Dot(d, id, settings, 29, 30); settings.CanvasSymmetry = null;
            var pixels = new List<BrushPixel>();
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            { bool inside = false; foreach (int cx in new[] { 29, 35 }) foreach (int cy in new[] { 30, 34 }) if ((x + .5 - cx) * (x + .5 - cx) + (y + .5 - cy) * (y + .5 - cy) <= 64) inside = true; if (inside) pixels.Add(new BrushPixel(x, y, 1)); }
            using (var stroke = reference.BeginStroke(rid, PaintChannel.Color, settings)) { stroke.ApplyDab(pixels, 29, 30); stroke.Commit(); }
            Assert.That(Bytes(d, id), Is.EqualTo(Bytes(reference, rid))); Assert.That(d.UndoCount, Is.EqualTo(1));
        }
        [Test] public void DualBrushCopiesFollowTheSameAxes()
        {
            var d = Make(out var id); var b = Brush(CanvasSymmetryMode.Both); b.Dual = new DualBrush { Radius = 3, Hardness = 1, Mode = DualBrushMode.Multiply };
            Dot(d, id, b); var p = Bytes(d, id); Assert.That(p.Any(v => v != 0), Is.True);
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) Assert.That(p[(y * 64 + x) * 4 + 3], Is.EqualTo(p[(y * 64 + 63 - x) * 4 + 3]));
        }
        [Test] public void AShiftedFractionalMirrorAxisUsesPixelCoordinates()
        {
            var d=Make(out var id); var b=Brush(CanvasSymmetryMode.Vertical); b.CanvasSymmetry.CenterX=23.25; b.Hardness=1;
            Dot(d,id,b,15.5,20.5);
            for(int y=0;y<64;y++)for(int x=0;x<64;x++)
            {
                double dy=y+.5-20.5,dx=x+.5-15.5,mx=x+.5-31;
                bool inside=dx*dx+dy*dy<=16 || mx*mx+dy*dy<=16;
                Assert.That(d.GetLayer(id).GetPixel(PaintChannel.Color,x,y).A,Is.EqualTo(inside?128:0));
            }
        }
        [Test] public void TransparencyLockPreservesEachChannelsAlphaOnSymmetryCopies()
        {
            var d=Make(out var id); var layer=d.GetLayer(id);
            foreach(var channel in new[]{PaintChannel.Color,PaintChannel.Height})
                for(int y=0;y<64;y++)for(int x=0;x<64;x++)layer.GetChannel(channel).SetPixel(x,y,new Rgba32(10,20,30,(byte)(x%3*80)));
            var before=d.Composite(PaintChannel.Color); d.SetLayerLocks(id,LayerLocks.Transparency); d.ClearHistory();
            using(var stroke=d.BeginMaterialStroke(id,new[]{new ChannelPaint(PaintChannel.Color,new Rgba32(230,210,190)),new ChannelPaint(PaintChannel.Height,new Rgba32(200,200,200))},Brush(CanvasSymmetryMode.Both)))
            {stroke.Add(new BrushSample(20.5,23.5));stroke.Commit();}
            var after=d.Composite(PaintChannel.Color);
            for(int i=0;i<before.Length;i+=4) {Assert.That(after[i+3],Is.EqualTo(before[i+3]));if(before[i+3]==0)Assert.That(layer.GetPixel(PaintChannel.Color,i/4%64,i/4/64),Is.EqualTo(new Rgba32(10,20,30,0)));}
            for(int y=0;y<64;y++)for(int x=0;x<64;x++)Assert.That(layer.GetPixel(PaintChannel.Height,x,y).A,Is.EqualTo(x%3*80));
            Assert.That(layer.GetPixel(PaintChannel.Height,20,23).R,Is.Not.EqualTo(10));
            Assert.That(after,Is.Not.EqualTo(before)); Assert.That(d.UndoCount,Is.EqualTo(1));
        }
        [TestCase("Fill")] [TestCase("Group")] [TestCase("Adjustment")]
        public void NonRasterLayerTypesRejectSymmetryWithoutChangingTheDocument(string kind)
        {
            var d=Make(out _); var layer=kind=="Fill"?d.AddFillLayer("fill",new Dictionary<PaintChannel,Rgba32>{{PaintChannel.Color,new Rgba32(20,30,40)}}):kind=="Group"?d.AddGroup("group"):d.AddAdjustmentLayer("adjust",AdjustmentSettings.Invert());
            d.ClearHistory();var before=DocumentBinary.Write(d); Assert.Throws<InvalidOperationException>(()=>d.BeginStroke(layer.Id,PaintChannel.Color,Brush(CanvasSymmetryMode.Radial)));
            Assert.That(DocumentBinary.Write(d),Is.EqualTo(before)); Assert.That(d.HasActiveStroke,Is.False);
        }
    }
}
