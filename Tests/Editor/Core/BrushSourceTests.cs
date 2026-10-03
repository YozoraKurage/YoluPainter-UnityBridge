using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class BrushSourceTests
    {
        static BrushSettings Settings(BrushEffect effect = BrushEffect.Clone) => new BrushSettings { Effect = effect, Radius = .5, Hardness = 1, Spacing = 1, SmudgeStrength = 1, PressureSize = false, PressureOpacity = false };
        static BrushMappedPixel M(int x, int source) => new BrushMappedPixel(new BrushPixel(x, 0, 1), new BrushSourceTap(source, 0));
        static PaintDocument Make(out PaintLayer layer, int tileSize = 4)
        {
            var d = new PaintDocument(8, 4, tileSize); layer = d.AddLayer("paint");
            for (int x = 0; x < 8; x++) layer.GetChannel(PaintChannel.Color).SetPixel(x, 0, new Rgba32((byte)(20 * x), 30, 50));
            d.ClearHistory(); return d;
        }
        [TestCase(false)] [TestCase(true)]
        public void CompositeCloneReadsFrozenVisibleStackAndRoundTripsOneUndo(bool mapped)
        {
            var d = Make(out var layer, 8);
            var top = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(90, 200, 20, 120) } });
            d.SetLayerOpacity(top.Id, .6); var mask = d.AddLayerMask(top.Id); mask.Surface.SetPixel(0, 0, new Rgba32(0, 0, 0, 130));
            var hidden = d.AddFillLayer("hidden", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(255, 0, 0) } }); d.SetLayerVisibility(hidden.Id, false);
            var target = d.AddLayer("clone"); d.ClearHistory();
            var before = DocumentBinary.Write(d); var composite = d.Composite(PaintChannel.Color);
            var settings = Settings(); settings.CloneOffsetX = -1;
            using (var stroke = d.BeginStroke(target.Id, PaintChannel.Color, settings))
            {
                stroke.UseCompositeCloneSource(); Assert.That(stroke.CloneSourceBytes, Is.GreaterThan(0));
                if (mapped) { stroke.ApplyMappedDab(new[] { M(1, 0) }); stroke.ApplyMappedDab(new[] { M(2, 1) }); }
                else { stroke.Add(new BrushSample(1.5, .5, 1, 0)); stroke.Add(new BrushSample(2.5, .5, 1, 1)); }
                stroke.Commit(); Assert.That(stroke.RollbackBytes, Is.Zero); Assert.That(stroke.CloneSourceBytes, Is.Zero);
            }
            for (int x = 1; x <= 2; x++)
            {
                int i = (x - 1) * 4; Assert.That(target.GetPixel(PaintChannel.Color, x, 0), Is.EqualTo(new Rgba32(composite[i], composite[i + 1], composite[i + 2], composite[i + 3])));
            }
            var after = DocumentBinary.Write(d); Assert.That(d.UndoCount, Is.EqualTo(1)); d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            d.Redo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(after)); Assert.That(DocumentBinary.Write(DocumentBinary.Read(after)), Is.EqualTo(after));
        }
        [TestCase(BrushEffect.Clone)] [TestCase(BrushEffect.Smudge)]
        public void MappedDabFreezesEveryReadBeforeWritesAndClonesKeepStrokeStart(BrushEffect effect)
        {
            var d = Make(out var layer); var before = DocumentBinary.Write(d);
            using (var stroke = d.BeginStroke(layer.Id, PaintChannel.Color, Settings(effect)))
            {
                stroke.ApplyMappedDab(new[] { M(1, 0), M(2, 1) });
                Assert.That(layer.GetPixel(PaintChannel.Color, 2, 0).R, Is.EqualTo(20), "先に変えた画素を同じダブで読まない");
                stroke.ApplyMappedDab(new[] { M(3, 1) });
                Assert.That(layer.GetPixel(PaintChannel.Color, 3, 0).R, Is.EqualTo(effect == BrushEffect.Clone ? 20 : 0));
                stroke.Cancel(); Assert.That(stroke.RollbackBytes, Is.Zero);
            }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.Zero);
        }
        [Test] public void MappedTapsUseIndependentPremultipliedEquationAndSelection()
        {
            var d = new PaintDocument(8, 4, 4); var layer = d.AddLayer("paint"); var p = layer.GetChannel(PaintChannel.Color);
            p.SetPixel(0, 0, new Rgba32(10, 20, 30, 255)); p.SetPixel(1, 0, new Rgba32(110, 120, 130, 85)); p.SetPixel(2, 0, new Rgba32(255, 0, 0, 0));
            using (var s = d.BeginStroke(layer.Id, PaintChannel.Color, Settings()))
            {
                s.ApplyMappedDab(new[] { new BrushMappedPixel(new BrushPixel(4, 0, 1), new BrushSourceTap(0, 0, .25), new BrushSourceTap(1, 0, .5), new BrushSourceTap(2, 0, .25)) }); s.Commit();
            }
            // a = 255/4 + 85/2 = 106.25、RGB = (10*63.75 + 110*42.5)/106.25 など。
            Assert.That(p.GetPixel(4, 0), Is.EqualTo(new Rgba32(50, 60, 70, 106)));
            var select = SelectionMask.Rectangle(d, 5, 0, 6, 1); d.SetSelection(select);
            using (var s = d.BeginStroke(layer.Id, PaintChannel.Color, Settings())) { s.ApplyMappedDab(new[] { M(5, 0), M(6, 0) }); s.Commit(); }
            Assert.That(p.GetPixel(5, 0), Is.EqualTo(p.GetPixel(0, 0))); Assert.That(p.GetPixel(6, 0), Is.EqualTo(Rgba32.Transparent));
        }
        [TestCase("source")] [TestCase("mapped")] [TestCase("chart")]
        public void SamplingBudgetsCancelPixelsAndEnabledChannels(string phase)
        {
            var d = Make(out var layer); var before = DocumentBinary.Write(d); d.ActiveStrokeBudgetBytes = phase == "source" ? 1 : 500;
            using (var s = d.BeginMaterialStroke(layer.Id, new[] { new ChannelPaint(PaintChannel.Color, default), new ChannelPaint(PaintChannel.Height, default) }, Settings()))
            {
                Assert.Throws<InvalidOperationException>(() =>
                {
                    if (phase == "source") s.UseCompositeCloneSource();
                    else if (phase == "chart") s.ApplyMappedDab(new[] { M(4, 0) }, samplingBytes: 1000);
                    else { s.ApplyMappedDab(new[] { M(1, 0) }); s.ApplyMappedDab(new[] { M(5, 1) }); }
                });
                Assert.That(s.IsFinished, Is.True); Assert.That(s.CloneSourceBytes, Is.Zero);
            }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.Zero); Assert.That(d.HasActiveStroke, Is.False);
        }
        [TestCase("duplicate")] [TestCase("nan")] [TestCase("outside")] [TestCase("no-source")]
        public void InvalidMappedInputCancelsTheEntireStroke(string kind)
        {
            var d = Make(out var layer); var before = DocumentBinary.Write(d);
            using (var s = d.BeginStroke(layer.Id, PaintChannel.Color, Settings()))
            {
                s.ApplyMappedDab(new[] { M(1, 0) });
                var invalid = kind == "nan" ? new BrushMappedPixel(new BrushPixel(2, 0, 1), new BrushSourceTap(0, 0, double.NaN))
                    : kind == "outside" ? M(2, -1) : kind == "no-source" ? new BrushMappedPixel(new BrushPixel(2, 0, 1), default) : M(2, 0);
                Assert.Catch<ArgumentException>(() => s.ApplyMappedDab(kind == "duplicate" ? new[] { invalid, invalid } : new[] { invalid }));
                Assert.That(s.IsFinished, Is.True);
            }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.HasActiveStroke, Is.False);
        }
        [Test] public void WrongSourceTypesAndLateCompositeSetupRefuseWithoutLeavingAStroke()
        {
            var d = Make(out var layer); d.AddLayerMask(layer.Id); d.ClearHistory(); var before = DocumentBinary.Write(d);
            using (var s = d.BeginMaskStroke(layer.Id, Settings())) Assert.Throws<InvalidOperationException>(() => s.UseCompositeCloneSource());
            using (var s = d.BeginStroke(layer.Id, PaintChannel.Color, Settings(BrushEffect.Blur))) Assert.Throws<InvalidOperationException>(() => s.ApplyMappedDab(new[] { M(1, 0) }));
            using (var s = d.BeginStroke(layer.Id, PaintChannel.Color, Settings())) { s.ApplyMappedDab(new[] { M(1, 0) }); Assert.Throws<InvalidOperationException>(() => s.UseCompositeCloneSource()); }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.HasActiveStroke, Is.False);
        }
        [Test] public void CompositeCloneFreezesEachMaterialChannelSeparatelyAndCancelsAll()
        {
            var d = Make(out var layer);
            var channels = new[] { PaintChannel.Color, PaintChannel.Height, PaintChannel.Normal };
            var values = channels.Select((c, i) => new ChannelPaint(c, new Rgba32((byte)(50 + i * 50), 90, 200)));
            d.AddFillLayer("fill", values.ToDictionary(c => c.Channel, c => c.Value)); var target = d.AddLayer("clone");
            var source = channels.Select(c => d.CompositePixel(c, 0, 0)).ToArray(); d.ClearHistory(); var before = DocumentBinary.Write(d);
            using (var s = d.BeginMaterialStroke(target.Id, channels.Select(c => new ChannelPaint(c, default)).ToArray(), Settings()))
            {
                s.UseCompositeCloneSource(); s.ApplyMappedDab(new[] { M(2, 0) });
                for (int i = 0; i < channels.Length; i++) Assert.That(target.GetPixel(channels[i], 2, 0), Is.EqualTo(source[i]));
                s.Cancel();
            }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.Zero);
        }

        [Test] public void MergedMappedCloneSourceGrowthRefusalReleasesTheSnapshotAndRollsBackSetup()
        {
            var d=Make(out var layer);var target=d.AddLayer("clone");d.ClearHistory();var before=DocumentBinary.Write(d);d.SourceBudgetBytes=d.AllocatedBytes;
            using(var s=d.BeginMaterialStroke(target.Id,new[]{new ChannelPaint(PaintChannel.Color,default),new ChannelPaint(PaintChannel.Height,default)},Settings()))
            {
                s.UseCompositeCloneSource();Assert.Throws<InvalidOperationException>(()=>s.ApplyMappedDab(new[]{M(4,0)}));
                Assert.That(s.IsFinished,Is.True);Assert.That(s.CloneSourceBytes,Is.Zero);
            }
            Assert.That(DocumentBinary.Write(d),Is.EqualTo(before));Assert.That(d.HasActiveStroke,Is.False);Assert.That(d.UndoCount,Is.Zero);
        }

        [Test] public void SparseCompositeCloneSnapshotScalesWithContentTilesAndReleasesItsPayload()
        {
            var d=new PaintDocument(4096,4096,32);var layer=d.AddLayer("paint");d.ActiveStrokeBudgetBytes=8192;
            using(var s=d.BeginStroke(layer.Id,PaintChannel.Color,Settings())) { s.UseCompositeCloneSource();Assert.That(s.CloneSourceBytes,Is.Zero);s.Cancel(); }
            layer.GetChannel(PaintChannel.Color).SetPixel(2048,2048,new Rgba32(210,30,70));
            using(var s=d.BeginStroke(layer.Id,PaintChannel.Color,Settings()))
            {
                s.UseCompositeCloneSource();Assert.That(s.CloneSourceBytes,Is.EqualTo(4160),"32角のRGBA8参照1枚4096バイトと名目の索引64バイト");
                Assert.That(s.RollbackBytes,Is.EqualTo(4160));s.Cancel();Assert.That(s.RollbackBytes,Is.Zero);
            }
            Assert.That(d.HasActiveStroke,Is.False);
        }
        [TestCase(BrushEffect.Clone)] [TestCase(BrushEffect.Smudge)]
        public void MappedEffectsUseTheDestinationStencilAmountAndUndoExactly(BrushEffect effect)
        {
            var d = Make(out var layer, 8); var before = DocumentBinary.Write(d);
            var image = ImageContent.FromPixels(new byte[] { 255,255,255,255, 0,0,0,255 }, 2, 1);
            var settings = Settings(effect); settings.Stencil = new BrushStencil(new StencilImage(image, ResourceColorSpace.Srgb), StencilMode.Mask);
            using (var s = d.BeginStroke(layer.Id, PaintChannel.Color, settings))
            {
                var plan = new[] { M(5, 1), M(6, 1) };
                var points = new[] { new StencilPoint(.5, .5), new StencilPoint(1.5, .5) };
                s.ApplyMappedDab(plan, stencilPoints: points); s.ApplyMappedDab(plan, stencilPoints: points); s.Commit();
            }
            Assert.That(layer.GetPixel(PaintChannel.Color, 5, 0), Is.EqualTo(new Rgba32(20,30,50)));
            Assert.That(layer.GetPixel(PaintChannel.Color, 6, 0), Is.EqualTo(new Rgba32(120,30,50)));
            Assert.That(d.UndoCount, Is.EqualTo(1)); var after = DocumentBinary.Write(d);
            d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); d.Redo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(after));
            using (var s = d.BeginStroke(layer.Id, PaintChannel.Color, settings))
            {
                s.ApplyMappedDab(new[] { M(4, 1) }, stencilPoints: new[] { new StencilPoint(.5, .5) });
                Assert.Throws<ArgumentException>(() => s.ApplyMappedDab(new[] { M(7, 1) }, stencilPoints: Array.Empty<StencilPoint>()));
                Assert.That(s.IsFinished, Is.True); Assert.That(s.RollbackBytes, Is.Zero);
            }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(after)); Assert.That(d.HasActiveStroke, Is.False);
        }
        [Test] public void CompositeCloneFreezesAnchorDrivenMasksBeforeItsOwnMaterialWrites()
        {
            var d = Make(out var layer, 8); d.SetChannelEnabled(layer.Id, PaintChannel.Height, true);
            var height = layer.GetChannel(PaintChannel.Height);
            height.SetPixel(0,0,new Rgba32(64,64,64)); height.SetPixel(1,0,new Rgba32(192,192,192));
            var anchor = d.AddAnchor(layer.Id, name: "source");
            var top = d.AddFillLayer("reader", new Dictionary<PaintChannel,Rgba32> { { PaintChannel.Color,new Rgba32(210,30,70) } });
            d.AddLayerMask(top.Id);
            d.AddFilter(top.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Anchor)
                .WithBlend(GeneratorBlend.Replace).WithAnchor(anchor.Id, PaintChannel.Height, AnchorRead.Value)));
            var color0 = d.CompositePixel(PaintChannel.Color,0,0); var color1 = d.CompositePixel(PaintChannel.Color,1,0);
            d.ClearHistory(); var before = DocumentBinary.Write(d);
            using (var s = d.BeginMaterialStroke(layer.Id, new[] { new ChannelPaint(PaintChannel.Color,default),new ChannelPaint(PaintChannel.Height,default) }, Settings()))
            {
                s.UseCompositeCloneSource(); s.ApplyMappedDab(new[] { M(1,0) });
                Assert.That(layer.GetPixel(PaintChannel.Color,1,0), Is.EqualTo(color0)); Assert.That(height.GetPixel(1,0).R, Is.EqualTo(64));
                Assert.That(d.CompositePixel(PaintChannel.Color,1,0), Is.Not.EqualTo(color1), "アンカーを通る現在の合成は描いた結果で変わる");
                s.ApplyMappedDab(new[] { M(2,1) }); s.Commit();
            }
            Assert.That(layer.GetPixel(PaintChannel.Color,2,0), Is.EqualTo(color1), "クローンは描き始める前の合成を保持する");
            Assert.That(height.GetPixel(2,0).R, Is.EqualTo(192)); Assert.That(d.UndoCount, Is.EqualTo(1));
            var after = DocumentBinary.Write(d); d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            d.Redo(); Assert.That(DocumentBinary.Write(DocumentBinary.Read(after)), Is.EqualTo(after));
        }
    }
}
