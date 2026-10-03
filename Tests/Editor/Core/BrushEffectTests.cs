using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class BrushEffectTests
    {
        const int W = 19, H = 13;
        static PaintDocument Make(out Guid id, int tile = 16, int w = W, int h = H)
        {
            var d = new PaintDocument(w, h, tile, 256L << 20); var layer = d.AddLayer("paint"); id = layer.Id;
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
            {
                var s = layer.GetChannel(c);
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    s.SetPixel(x, y, new Rgba32((byte)((x * 31 + (int)c * 7) % 256), (byte)((y * 47 + (int)c) % 256), (byte)((x * 19 + y * 7) % 256), (byte)((x + y) % 5 == 0 ? 0 : (x + y) % 3 == 0 ? 120 : 255)));
            }
            d.ClearHistory(); return d;
        }
        static Rgba32[] Read(PaintDocument d, Guid id, PaintChannel c = PaintChannel.Color)
        { var s = d.GetLayer(id).GetChannel(c); return Enumerable.Range(0, d.Width * d.Height).Select(i => s.GetPixel(i % d.Width, i / d.Width)).ToArray(); }
        static BrushSettings Settings(BrushEffect e) => new BrushSettings { Effect = e, BlurRadius = 2, SmudgeStrength = .6, Radius = 3, Hardness = .6, Spacing = .2, Opacity = .7, Flow = .6, PressureSize = false, PressureOpacity = true, PressureFlow = true, CloneOffsetX = -2.25, CloneOffsetY = .5 };
        static byte B(double v) => (byte)Math.Max(0, Math.Min(255, Math.Floor(v + .5)));
        // 独立した参照: 全画面の配列、窓内を直接足すぼかし、4 点の補間。Core のフィルター・合成を呼ばない。
        static Rgba32 Weighted(Rgba32[] p, double[] weights)
        {
            double a = 0, r = 0, g = 0, b = 0;
            for (int i = 0; i < p.Length; i++) { double v = p[i].A * weights[i]; a += v; r += p[i].R * v; g += p[i].G * v; b += p[i].B * v; }
            return a == 0 ? Rgba32.Transparent : new Rgba32(B(r / a), B(g / a), B(b / a), B(a));
        }
        static Rgba32 Blur(Rgba32[] image, int x, int y, int radius)
        {
            var p = new List<Rgba32>();
            for (int yy = Math.Max(0, y - radius); yy <= Math.Min(H - 1, y + radius); yy++)
                for (int xx = Math.Max(0, x - radius); xx <= Math.Min(W - 1, x + radius); xx++) p.Add(image[yy * W + xx]);
            return Weighted(p.ToArray(), Enumerable.Repeat(1.0 / p.Count, p.Count).ToArray());
        }
        static Rgba32 Bilinear(Rgba32[] image, double x, double y)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y); double fx = x - ix, fy = y - iy;
            Rgba32 At(int xx, int yy) => image[Math.Min(H - 1, yy) * W + Math.Min(W - 1, xx)];
            if (fx == 0 && fy == 0) return At(ix, iy);
            return Weighted(new[] { At(ix, iy), At(ix + 1, iy), At(ix, iy + 1), At(ix + 1, iy + 1) }, new[] { (1 - fx) * (1 - fy), fx * (1 - fy), (1 - fx) * fy, fx * fy });
        }
        static Rgba32 Mix(Rgba32 start, Rgba32 source, double amount, bool clone, bool locked)
        {
            if (locked)
            {
                double q = amount * source.A / 255;
                return start.A == 0 ? start : new Rgba32(B(start.R + (source.R - start.R) * q), B(start.G + (source.G - start.G) * q), B(start.B + (source.B - start.B) * q), start.A);
            }
            double sa = source.A / 255.0 * amount, ba = start.A / 255.0;
            double u = clone ? ba * (1 - sa) : ba * (1 - amount), a = u + sa;
            if (a <= 0 || B(a * 255) == 0) return new Rgba32(start.R, start.G, start.B, 0);
            return new Rgba32(B((start.R * u + source.R * sa) / a), B((start.G * u + source.G * sa) / a), B((start.B * u + source.B * sa) / a), B(a * 255));
        }
        static void ReferenceDab(Rgba32[] original, ref Rgba32[] live, float[] wash, BrushSettings s, List<BrushPixel> pixels, double dx, double dy, double pressure, SelectionMask selection, bool locked)
        {
            var frame = (Rgba32[])live.Clone(); var next = (Rgba32[])live.Clone();
            foreach (var p in pixels)
            {
                int i = p.Y * W + p.X; double selected = selection == null ? 1 : selection.Coverage(p.X, p.Y);
                if (selected == 0 || (locked && original[i].A == 0)) continue;
                double ceiling = s.Opacity * pressure, flow = p.Coverage * s.Flow * pressure * (s.Effect == BrushEffect.Smudge ? s.SmudgeStrength : 1);
                double a = wash[i] >= ceiling ? wash[i] : wash[i] + (ceiling - wash[i]) * Math.Min(1, flow); wash[i] = (float)a;
                Rgba32 source;
                if (s.Effect == BrushEffect.Blur) { if (original[i].A == 0) continue; source = Blur(frame, p.X, p.Y, s.BlurRadius); }
                else
                {
                    double x = p.X + dx, y = p.Y + dy;
                    if (x < 0 || y < 0 || x > W - 1 || y > H - 1) continue;
                    source = Bilinear(s.Effect == BrushEffect.Clone ? original : frame, x, y);
                }
                // クローンの半選択は source-over の結果を選択量で戻す（既存のブラシと同じ）。
                if (s.Effect == BrushEffect.Clone && selected < 1 && !locked)
                {
                    var painted = Mix(original[i], source, a, true, false);
                    next[i] = Weighted(new[] { original[i], painted }, new[] { 1 - selected, selected });
                    if (next[i].A == 0) next[i] = new Rgba32(original[i].R, original[i].G, original[i].B, 0);
                }
                else next[i] = Mix(original[i], source, a * selected, s.Effect == BrushEffect.Clone, locked);
            }
            live = next;
        }
        static List<BrushPixel> Pixels(int step) => Enumerable.Range(0, W * H).Select(i => new BrushPixel(i % W, i / W, .2 + ((i + step) % 7) / 10.0)).ToList();

        [TestCase(BrushEffect.Blur, false)] [TestCase(BrushEffect.Smudge, false)] [TestCase(BrushEffect.Clone, false)]
        [TestCase(BrushEffect.Blur, true)] [TestCase(BrushEffect.Smudge, true)] [TestCase(BrushEffect.Clone, true)]
        public void DabsEqualIndependentPremultipliedReferenceAndRespectSelectionAndAlphaLock(BrushEffect effect, bool locked)
        {
            var d = Make(out var id); var s = Settings(effect);
            var selection = SelectionMask.Ellipse(d, 9.2, 6.1, 8, 5.8); d.SetSelection(selection);
            if (locked) d.SetLayerLocks(id, LayerLocks.Transparency);
            d.ClearHistory(); var original = Read(d, id); var expected = (Rgba32[])original.Clone(); var wash = new float[W * H];
            using (var stroke = d.BeginStroke(id, PaintChannel.Color, s))
            {
                if (effect == BrushEffect.Smudge) Assert.That(stroke.ApplyDab(Pixels(0), 5, 5), Is.False, "最初は拾うだけ");
                for (int n = 0; n < 3; n++)
                {
                    var pixels = Pixels(n); double dx = effect == BrushEffect.Smudge ? -1.25 : s.CloneOffsetX, dy = effect == BrushEffect.Smudge ? -.5 : s.CloneOffsetY;
                    ReferenceDab(original, ref expected, wash, s, pixels, dx, dy, .8, selection, locked);
                    stroke.ApplyDab(pixels, 6.25 + n * 1.25, 5.5 + n * .5, .8);
                    Assert.That(Read(d, id), Is.EqualTo(expected), "ダブ " + n);
                }
                Assert.That(stroke.Commit(), Is.True);
            }
            Assert.That(d.UndoCount, Is.EqualTo(1)); d.Undo(); Assert.That(Read(d, id), Is.EqualTo(original)); d.Redo(); Assert.That(Read(d, id), Is.EqualTo(expected));
            var saved = DocumentBinary.Write(d); var loaded = DocumentBinary.Read(saved); Assert.That(Read(loaded, id), Is.EqualTo(expected));
        }

        [TestCase(BrushEffect.Blur)] [TestCase(BrushEffect.Smudge)] [TestCase(BrushEffect.Clone)]
        public void SamplingAndDabsAreIdenticalAcrossTileSizesAndThreadCounts(BrushEffect effect)
        {
            var field = typeof(BrushStroke).GetField("ParallelDabPixels", BindingFlags.Static | BindingFlags.NonPublic);
            int old = CoreParallelism.MaxDegreeOfParallelism, threshold = (int)field.GetValue(null);
            try
            {
                field.SetValue(null, 1); Rgba32[] expected = null;
                foreach (int tile in new[] { 4, 16, 32 }) foreach (int threads in new[] { 1, 4 })
                {
                    CoreParallelism.MaxDegreeOfParallelism = threads; var d = Make(out var id, tile, 75, 57); var s = Settings(effect); s.Radius = 22; s.Spacing = .08;
                    using (var stroke = d.BeginStroke(id, PaintChannel.Color, s))
                    { stroke.Add(new BrushSample(25.5, 28.5, 1, 0)); stroke.Add(new BrushSample(44.5, 31.5, 1, 1)); stroke.Commit(); }
                    var pixels = Read(d, id); if (expected == null) expected = pixels; else Assert.That(pixels, Is.EqualTo(expected), "タイル " + tile + " スレッド " + threads);
                }
            }
            finally { CoreParallelism.MaxDegreeOfParallelism = old; field.SetValue(null, threshold); }
        }
        [TestCase(BrushEffect.Blur)] [TestCase(BrushEffect.Smudge)] [TestCase(BrushEffect.Clone)]
        public void MaterialChannelsComputeFromTheirOwnPixelsAndCancelTogether(BrushEffect effect)
        {
            var d = Make(out var id); var channels = ((PaintChannel[])Enum.GetValues(typeof(PaintChannel))).Select(c => new ChannelPaint(c, new Rgba32(255, 0, 0))).ToArray();
            var originals = channels.Select(c => Read(d, id, c.Channel)).ToArray(); var s = Settings(effect);
            var outputs = new List<Rgba32[]>();
            using (var stroke = d.BeginMaterialStroke(id, channels, s))
            { stroke.Add(new BrushSample(5, 5, 1, 0)); stroke.Add(new BrushSample(12, 7, 1, 1)); stroke.Commit(); }
            foreach (var c in channels)
            {
                var single = Make(out var sid);
                using (var stroke = single.BeginStroke(sid, c.Channel, s)) { stroke.Add(new BrushSample(5, 5, 1, 0)); stroke.Add(new BrushSample(12, 7, 1, 1)); stroke.Commit(); }
                Assert.That(Read(d, id, c.Channel), Is.EqualTo(Read(single, sid, c.Channel))); outputs.Add(Read(d, id, c.Channel));
            }
            d.Undo(); for (int i = 0; i < channels.Length; i++) Assert.That(Read(d, id, channels[i].Channel), Is.EqualTo(originals[i]));
            d.Redo(); for (int i = 0; i < channels.Length; i++) Assert.That(Read(d, id, channels[i].Channel), Is.EqualTo(outputs[i]));
            var saved = DocumentBinary.Write(d);
            using (var stroke = d.BeginMaterialStroke(id, channels, s)) { stroke.Add(new BrushSample(5, 5, 1, 0)); stroke.Add(new BrushSample(12, 7, 1, 1)); stroke.Cancel(); }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(saved));
        }
        [TestCase(BrushEffect.Blur)] [TestCase(BrushEffect.Smudge)] [TestCase(BrushEffect.Clone)]
        public void BudgetsAndInvalidInputsCancelAllChannelsWithoutAnUndo(BrushEffect effect)
        {
            var d = Make(out var id); var saved = DocumentBinary.Write(d); var s = Settings(effect);
            var channels = new[] { new ChannelPaint(PaintChannel.Color, default), new ChannelPaint(PaintChannel.Height, default) };
            d.ActiveStrokeBudgetBytes = 10;
            using (var stroke = d.BeginMaterialStroke(id, channels, s))
            {
                Assert.Throws<InvalidOperationException>(() => { stroke.Add(new BrushSample(5, 5, 1, 0)); stroke.Add(new BrushSample(12, 7, 1, 1)); });
                Assert.That(stroke.IsFinished, Is.True);
            }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(saved)); Assert.That(d.UndoCount, Is.Zero);
            d.ActiveStrokeBudgetBytes = 4L << 20;
            using (var stroke = d.BeginStroke(id, PaintChannel.Color, s))
            {
                stroke.Add(new BrushSample(5, 5, 1, 0)); stroke.Add(new BrushSample(12, 7, 1, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => stroke.ApplyDab(new[] { new BrushPixel(5, 5, 2) }, 5, 5));
            }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(saved)); Assert.That(d.UndoCount, Is.Zero);
        }
        [TestCase(BrushEffect.Blur)] [TestCase(BrushEffect.Smudge)] [TestCase(BrushEffect.Clone)]
        public void PixelLocksRefuseContentButAllowMaskAndAllLocksRefuseMask(BrushEffect effect)
        {
            var d = Make(out var id); d.AddLayerMask(id); d.SetLayerLocks(id, LayerLocks.Pixels); var saved = DocumentBinary.Write(d); var s = Settings(effect);
            Assert.Throws<LayerLockedException>(() => d.BeginStroke(id, PaintChannel.Color, s)); Assert.That(DocumentBinary.Write(d), Is.EqualTo(saved));
            using (var stroke = d.BeginMaskStroke(id, s)) { stroke.Add(new BrushSample(5, 5, 1, 0)); stroke.Add(new BrushSample(12, 7, 1, 1)); stroke.Cancel(); }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(saved));
            d.SetLayerLocks(id, LayerLocks.All); Assert.Throws<LayerLockedException>(() => d.BeginMaskStroke(id, s));
        }
        [Test] public void InvalidEffectTypesAndParametersAreRefusedBeforeAnyMutation()
        {
            var d = Make(out var id); var saved = DocumentBinary.Write(d);
            foreach (var s in new[] { new BrushSettings { Effect = (BrushEffect)77 }, new BrushSettings { BlurRadius = 0 }, new BrushSettings { BlurRadius = 65 }, new BrushSettings { SmudgeStrength = double.NaN }, new BrushSettings { CloneOffsetX = double.PositiveInfinity }, new BrushSettings { Effect = BrushEffect.Blur, Erase = true } })
                Assert.Catch<ArgumentException>(() => d.BeginStroke(id, PaintChannel.Color, s));
            var fill = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(40, 40, 40) } });
            Assert.Throws<InvalidOperationException>(() => d.BeginStroke(fill.Id, PaintChannel.Color, Settings(BrushEffect.Blur)));
            d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(saved));
        }
        [TestCase(BrushEffect.Blur)] [TestCase(BrushEffect.Smudge)] [TestCase(BrushEffect.Clone)]
        public void MaskPixelsUseTheSameEffectAndKeepOneUndo(BrushEffect effect)
        {
            var d = Make(out var id); var mask = d.AddLayerMask(id); var channel = d.GetLayer(id).GetChannel(PaintChannel.Color);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            { var value = new Rgba32(0, 0, 0, (byte)((x * 29 + y * 17) % 256)); mask.Surface.SetPixel(x, y, value); channel.SetPixel(x, y, value); }
            d.ClearHistory(); var before = DocumentBinary.Write(d); var settings = Settings(effect);
            using (var stroke = d.BeginMaskStroke(id, settings)) { stroke.Add(new BrushSample(5, 5, 1, 0)); stroke.Add(new BrushSample(12, 7, 1, 1)); stroke.Commit(); }
            var expected = Enumerable.Range(0, W * H).Select(n => mask.Surface.GetPixel(n % W, n / W)).ToArray();
            Assert.That(d.UndoCount, Is.EqualTo(1)); d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); d.Redo();
            using (var stroke = d.BeginStroke(id, PaintChannel.Color, settings)) { stroke.Add(new BrushSample(5, 5, 1, 0)); stroke.Add(new BrushSample(12, 7, 1, 1)); stroke.Commit(); }
            Assert.That(Read(d, id), Is.EqualTo(expected), "マスクにも色ではなく同じ画素演算が効く");
        }
        [TestCase(BrushEffect.Blur)] [TestCase(BrushEffect.Smudge)] [TestCase(BrushEffect.Clone)]
        public void ASourceGrowthRefusalRollsBackPixelsAndChannelsEnabledByTheStroke(BrushEffect effect)
        {
            var d = new PaintDocument(32, 32, 8); var layer = d.AddLayer("paint"); var pixels = layer.GetChannel(PaintChannel.Color);
            var bytes = Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray(); pixels.ImportTile(new TileCoord(0, 0), bytes);
            d.ClearHistory(); var saved = DocumentBinary.Write(d); d.SourceBudgetBytes = d.AllocatedBytes;
            var s = Settings(effect); s.Radius = 6; s.CloneOffsetX = -4; s.CloneOffsetY = 0;
            using (var stroke = d.BeginMaterialStroke(layer.Id, new[] { new ChannelPaint(PaintChannel.Color, default), new ChannelPaint(PaintChannel.Height, default) }, s))
                Assert.Throws<InvalidOperationException>(() => { stroke.Add(new BrushSample(5, 5, 1, 0)); stroke.Add(new BrushSample(9, 5, 1, 1)); stroke.Commit(); });
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(saved)); Assert.That(d.UndoCount, Is.Zero);
        }
        [Test] public void CloneReadsTheStrokeStartWhenSourceAndDestinationOverlap()
        {
            var d = new PaintDocument(10, 1, 4); var l = d.AddLayer("paint"); var s = l.GetChannel(PaintChannel.Color);
            for (int x = 0; x < 10; x++) s.SetPixel(x, 0, new Rgba32((byte)(x * 20), 0, 0));
            var original = Read(d, l.Id); d.ClearHistory();
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings { Effect = BrushEffect.Clone, Radius = .5, Hardness = 1, Spacing = 1, PressureSize = false, PressureOpacity = false, CloneOffsetX = -1 }))
            { stroke.Add(new BrushSample(1.5, .5, 1, 0)); stroke.Add(new BrushSample(8.5, .5, 1, 1)); stroke.Commit(); }
            for (int x = 1; x < 9; x++) Assert.That(s.GetPixel(x, 0), Is.EqualTo(original[x - 1]), "書いた画素から再度写さない");
        }
        [Test] public void BlurLeavesZeroAlphaRgbAndIgnoresItsColorInTheAverage()
        {
            var d = new PaintDocument(3, 1, 2); var l = d.AddLayer("paint"); var p = l.GetChannel(PaintChannel.Color);
            p.SetPixel(0, 0, new Rgba32(255, 17, 99, 0)); p.SetPixel(1, 0, new Rgba32(10, 20, 30, 255)); p.SetPixel(2, 0, new Rgba32(10, 20, 30, 255));
            using (var s = d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings { Effect = BrushEffect.Blur, BlurRadius = 1 }))
            { s.ApplyDab(new[] { new BrushPixel(0, 0, 1), new BrushPixel(1, 0, 1) }, 1, 0); s.Commit(); }
            Assert.That(p.GetPixel(0, 0), Is.EqualTo(new Rgba32(255, 17, 99, 0))); Assert.That(p.GetPixel(1, 0), Is.EqualTo(new Rgba32(10, 20, 30, 170)));
        }
    }
}
