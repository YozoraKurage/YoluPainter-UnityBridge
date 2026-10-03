using System;
using System.Collections.Generic;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>The CPU compositor's fast span code (shortcuts for transparent and opaque backdrops, blend tables for the separable
    /// modes, the clipping span, tiles read in place, per-worker loading) gives exactly the bytes of the formulas as they were before
    /// (<see cref="FrozenCompositor"/>): on random documents with every mode, odd opacities (subnormal ones included), masks, uniform
    /// tiles, clipping, pass-through and isolated groups, adjustments and filters; and for every below/over byte pair at chosen source
    /// alphas. Also CompositeRegions: starting from a backdrop and capturing on the way give the same bytes as compositing from
    /// transparent, and bad jobs are refused.</summary>
    public sealed class CompositorExactnessTests
    {
        int savedDegree;
        [SetUp] public void SaveDegree() { savedDegree = CoreParallelism.MaxDegreeOfParallelism; }
        [TearDown] public void RestoreDegree() { CoreParallelism.MaxDegreeOfParallelism = savedDegree; }

        static readonly double[] OddOpacities = { 1, 1, .5, .7, 1e-305, 4.9e-324, .003921, .99999999, 0 };

        static void Tile(Random rnd, byte[] b, int tile, int tx, int ty, int w, int h)
        {
            int kind = rnd.Next(6);
            if (kind == 0)
            {
                var u = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)(rnd.Next(3) == 0 ? 255 : rnd.Next(3) == 0 ? 0 : rnd.Next(256)));
                for (int k = 0; k < b.Length; k += 4) { b[k] = u.R; b[k + 1] = u.G; b[k + 2] = u.B; b[k + 3] = u.A; } // a uniform tile
            }
            else
            {
                rnd.NextBytes(b);
                if (kind <= 3) for (int k = 3; k < b.Length; k += 4) b[k] = (byte)(b[k] < 80 ? 0 : b[k] > 180 ? 255 : b[k]);
                else if (kind == 4) for (int k = 3; k < b.Length; k += 4) b[k] = 255;
            }
            for (int y = 0; y < tile; y++) for (int x = 0; x < tile; x++) if (tx * tile + x >= w || ty * tile + y >= h) { int o = (y * tile + x) * 4; b[o] = b[o + 1] = b[o + 2] = b[o + 3] = 0; }
        }
        /// <summary>A random document: rasters in three channels, fills, adjustments, groups (some pass-through), clipping, hidden
        /// layers, masks (uniform tiles, inverted, odd densities, blurred), content filters and every blend mode.</summary>
        internal static PaintDocument RandomDocument(Random rnd, int maxSide = 96)
        {
            int tile = new[] { 8, 16, 32 }[rnd.Next(3)];
            int w = 16 + rnd.Next(maxSide - 15), h = 16 + rnd.Next(maxSide - 15);
            var d = new PaintDocument(w, h, tile);
            var channels = new[] { PaintChannel.Color, PaintChannel.Normal, PaintChannel.Height };
            var b = new byte[tile * tile * 4];
            var groups = new List<PaintLayer>();
            var modes = (LayerBlendMode[])Enum.GetValues(typeof(LayerBlendMode));
            int n = 1 + rnd.Next(12);
            for (int i = 0; i < n; i++)
            {
                PaintLayer l; int kind = rnd.Next(10);
                Guid? above = d.Layers.Count > 0 && rnd.Next(3) == 0 ? d.Layers[rnd.Next(d.Layers.Count)].Id : (Guid?)null;
                if (kind == 0) l = d.AddGroup("g" + i, above: above);
                else if (kind == 1)
                    l = d.AddAdjustmentLayer("a" + i, rnd.Next(3) == 0 ? AdjustmentSettings.Invert() : rnd.Next(2) == 0
                        ? AdjustmentSettings.Levels(rnd.NextDouble() * .3, .7 + rnd.NextDouble() * .3, .5 + rnd.NextDouble(), rnd.NextDouble() * .2, .8 + rnd.NextDouble() * .2)
                        : AdjustmentSettings.HueSaturation(rnd.Next(-180, 180), rnd.NextDouble() * 2 - 1, rnd.NextDouble() - .5), new[] { PaintChannel.Color }, above: above);
                else if (kind == 2)
                    l = d.AddFillLayer("f" + i, new Dictionary<PaintChannel, Rgba32>
                    {
                        { PaintChannel.Color, new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)(rnd.Next(3) == 0 ? 255 : rnd.Next(256))) },
                        { PaintChannel.Normal, new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)) },
                    }, above: above);
                else
                {
                    l = d.AddLayer("l" + i, above: above);
                    foreach (var ch in channels)
                    {
                        if (rnd.Next(3) == 0) continue;
                        if (ch != PaintChannel.Color) d.SetChannelEnabled(l.Id, ch, true);
                        var s = l.GetChannel(ch);
                        for (int ty = 0; ty * tile < h; ty++) for (int tx = 0; tx * tile < w; tx++) { if (rnd.Next(3) == 0) continue; Tile(rnd, b, tile, tx, ty, w, h); s.ImportTile(new TileCoord(tx, ty), b); }
                    }
                }
                if (l.IsGroup) groups.Add(l);
                else if (groups.Count > 0 && rnd.Next(2) == 0) { var g = groups[rnd.Next(groups.Count)]; try { d.MoveLayerTo(l.Id, g.Id, 0); } catch (InvalidOperationException) { } catch (ArgumentException) { } }
                var mode = modes[rnd.Next(modes.Length)];
                if (mode != LayerBlendMode.PassThrough || l.IsGroup) d.SetLayerBlendMode(l.Id, mode);
                if (rnd.Next(3) == 0) d.SetLayerOpacity(l.Id, rnd.Next(2) == 0 ? OddOpacities[rnd.Next(OddOpacities.Length)] : rnd.NextDouble());
                if (rnd.Next(4) == 0) d.SetLayerClipping(l.Id, true);
                if (rnd.Next(8) == 0) d.SetLayerVisibility(l.Id, false);
                if (rnd.Next(4) == 0)
                {
                    var m = d.AddLayerMask(l.Id);
                    for (int ty = 0; ty * tile < h; ty++) for (int tx = 0; tx * tile < w; tx++)
                    {
                        if (rnd.Next(2) == 0) continue;
                        Array.Clear(b, 0, b.Length); byte u = (byte)rnd.Next(256); bool uniform = rnd.Next(3) == 0;
                        for (int y = 0; y < tile; y++) for (int x = 0; x < tile; x++) if (tx * tile + x < w && ty * tile + y < h) b[(y * tile + x) * 4 + 3] = uniform ? u : (byte)rnd.Next(256);
                        m.Surface.ImportTile(new TileCoord(tx, ty), b);
                    }
                    if (rnd.Next(3) == 0) d.SetLayerMaskInverted(l.Id, true);
                    if (rnd.Next(3) == 0) d.SetLayerMaskDensity(l.Id, rnd.Next(4) == 0 ? 1e-306 : rnd.NextDouble());
                    if (rnd.Next(5) == 0) d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(1 + rnd.Next(4)));
                }
                if (!l.IsGroup && l.Kind != LayerKind.Adjustment && rnd.Next(6) == 0)
                    d.AddFilter(l.Id, FilterTarget.Content, rnd.Next(2) == 0 ? FilterSettings.GaussianBlur(1 + rnd.Next(5)) : FilterSettings.Noise(.3, rnd.Next(100), rnd.Next(2) == 0));
            }
            d.ClearHistory();
            return d;
        }

        [Test] public void RegionsMatchTheFrozenFormulasOnRandomDocuments()
        {
            var rnd = new Random(20261003);
            for (int c = 0; c < 36; c++)
            {
                var d = RandomDocument(rnd);
                CoreParallelism.MaxDegreeOfParallelism = c % 3 == 0 ? 1 : c % 3 == 1 ? 3 : 0;
                foreach (var ch in new[] { PaintChannel.Color, PaintChannel.Normal, PaintChannel.Height })
                {
                    CpuCompositingTests.AssertSameBytes(FrozenCompositor.Region(d, ch, 0, 0, d.Width, d.Height), d.Composite(ch), "document " + c + " " + ch);
                    int x = rnd.Next(d.Width), y = rnd.Next(d.Height), w = 1 + rnd.Next(d.Width - x), h = 1 + rnd.Next(d.Height - y);
                    CpuCompositingTests.AssertSameBytes(FrozenCompositor.Region(d, ch, x, y, w, h), CpuCompositor.CompositeRegion(d, ch, x, y, w, h), "document " + c + " " + ch + " region");
                }
                var p = d.CompositePixel(PaintChannel.Color, d.Width / 2, d.Height / 3);
                var e = FrozenCompositor.Region(d, PaintChannel.Color, d.Width / 2, d.Height / 3, 1, 1);
                Assert.That(new[] { p.R, p.G, p.B, p.A }, Is.EqualTo(e), "document " + c + ": the per-pixel reference");
            }
        }

        /// <summary>Every (below, over) byte pair of one channel at several source alphas, below alphas (transparent, opaque, mixed),
        /// amounts (subnormal ones too) and modes, as a layer and as a clipped layer, against the frozen formulas. These are the inputs
        /// where the span code takes its shortcuts (a transparent or opaque backdrop, an opaque source, the table lookups).</summary>
        [Test] public void EveryBytePairMatchesTheFrozenFormulas()
        {
            const int S = 512, T = 64; // (x & 255, y & 255) = (below, over); the quadrant picks the source alpha
            var sourceAlphas = new byte[] { 1, 128, 254, 255 };
            foreach (string belowAlpha in new[] { "0", "255", "mixed" })
            {
                var d = new PaintDocument(S, S, T);
                var bottom = d.AddLayer("below"); var top = d.AddLayer("over");
                var below = new Rgba32[S * S]; var over = new Rgba32[S * S];
                var bt = new byte[T * T * 4]; var tt = new byte[T * T * 4];
                for (int ty = 0; ty < S / T; ty++) for (int tx = 0; tx < S / T; tx++)
                {
                    for (int y = 0; y < T; y++) for (int x = 0; x < T; x++)
                    {
                        int px = tx * T + x, py = ty * T + y, dv = px & 255, sv = py & 255, o = (y * T + x) * 4;
                        byte dA = belowAlpha == "0" ? (byte)0 : belowAlpha == "255" ? (byte)255 : (byte)((dv * 37 + sv * 11) & 255);
                        below[py * S + px] = new Rgba32((byte)dv, (byte)(255 - dv), (byte)(dv ^ 0x5A), dA);
                        over[py * S + px] = new Rgba32((byte)sv, (byte)(sv ^ 0xA5), (byte)(255 - sv), sourceAlphas[(px >> 8) | (py >> 8) << 1]);
                        var p = below[py * S + px]; bt[o] = p.R; bt[o + 1] = p.G; bt[o + 2] = p.B; bt[o + 3] = p.A;
                        p = over[py * S + px]; tt[o] = p.R; tt[o + 1] = p.G; tt[o + 2] = p.B; tt[o + 3] = p.A;
                    }
                    bottom.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(tx, ty), bt);
                    top.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(tx, ty), tt);
                }
                d.ClearHistory();
                foreach (bool clip in new[] { false, true })
                {
                    d.SetLayerClipping(top.Id, clip);
                    foreach (var mode in new[] { LayerBlendMode.Normal, LayerBlendMode.Multiply, LayerBlendMode.Overlay, LayerBlendMode.Hue, LayerBlendMode.DarkerColor })
                        foreach (double amount in new[] { 1, .7, 4.9e-324 })
                        {
                            d.SetLayerBlendMode(top.Id, mode); d.SetLayerOpacity(top.Id, amount);
                            var actual = d.Composite(PaintChannel.Color);
                            for (int i = 0; i < S * S; i++)
                            {
                                var e = clip ? FrozenCompositor.Blend(Rgba32.Transparent, FrozenCompositor.ClipOnto(below[i], over[i], amount, mode), 1, LayerBlendMode.Normal)
                                             : FrozenCompositor.Blend(FrozenCompositor.Blend(Rgba32.Transparent, below[i], 1, LayerBlendMode.Normal), over[i], amount, mode);
                                int o = i * 4;
                                if (actual[o] != e.R || actual[o + 1] != e.G || actual[o + 2] != e.B || actual[o + 3] != e.A)
                                    Assert.Fail($"{(clip ? "clipped " : "")}{mode} amount {amount:R} below alpha {belowAlpha}: below {below[i]} over {over[i]} expected {e}, got ({actual[o]}, {actual[o + 1]}, {actual[o + 2]}, {actual[o + 3]})");
                            }
                        }
                }
            }
        }

        /// <summary>A job that captures below entry k gets the composite with the entries from k up hidden; a job that starts at k from
        /// that capture (in its pixels, or read from a backdrop array with an offset and a stride) gets the whole composite; several jobs
        /// in one call (overlapping, different starts) are the same as one by one; the bytes do not depend on the thread count.</summary>
        [Test] public void JobsStartFromABackdropAndCaptureOnTheWay()
        {
            var rnd = new Random(77);
            for (int c = 0; c < 24; c++)
            {
                var d = RandomDocument(rnd, 160);
                foreach (var ch in new[] { PaintChannel.Color, PaintChannel.Normal })
                {
                    var plan = CpuCompositor.Plan(d, ch); int count = plan.Count;
                    for (int r = 0; r < 3; r++)
                    {
                        CoreParallelism.MaxDegreeOfParallelism = r == 0 ? 1 : 0;
                        int x = rnd.Next(d.Width), y = rnd.Next(d.Height), w = 1 + rnd.Next(d.Width - x), h = 1 + rnd.Next(d.Height - y), k = rnd.Next(count + 1);
                        string at = $"document {c} {ch} ({x}, {y}, {w}×{h}) k {k}";
                        var whole = CpuCompositor.CompositeRegion(d, ch, x, y, w, h);
                        var pixels = new byte[w * h * 4 + 8]; rnd.NextBytes(pixels); // start 0: what is in it does not matter
                        var capture = new byte[w * h * 4];
                        CpuCompositor.CompositeRegions(d, ch, new[] { new CpuCompositor.CompositeJob(x, y, w, h, pixels, 0, k, capture) });
                        CpuCompositingTests.AssertSameBytes(whole, Head(pixels, whole.Length), at + ": from transparent with a capture");
                        var hidden = new List<PaintLayer>();
                        for (int i = k; i < count; i++) { hidden.Add(plan[i].Base); d.SetLayerVisibility(plan[i].Base.Id, false); }
                        var lower = CpuCompositor.CompositeRegion(d, ch, x, y, w, h);
                        foreach (var l in hidden) d.SetLayerVisibility(l.Id, true);
                        CpuCompositingTests.AssertSameBytes(lower, capture, at + ": the capture is the composite of the entries below k");
                        var again = (byte[])capture.Clone();
                        CpuCompositor.CompositeRegions(d, ch, new[] { new CpuCompositor.CompositeJob(x, y, w, h, again, k) });
                        CpuCompositingTests.AssertSameBytes(whole, again, at + ": from the capture in the pixels");
                        if (k == 0) continue; // from transparent there is no backdrop to read
                        // the backdrop in a bigger array, with a stride
                        int stride = w * 4 + 12, offset = 20; var backdrop = new byte[offset + h * stride];
                        for (int row = 0; row < h; row++) Buffer.BlockCopy(capture, row * w * 4, backdrop, offset + row * stride, w * 4);
                        var fromBackdrop = new byte[w * h * 4];
                        CpuCompositor.CompositeRegions(d, ch, new[] { new CpuCompositor.CompositeJob(x, y, w, h, fromBackdrop, k, backdrop, offset, stride) });
                        CpuCompositingTests.AssertSameBytes(whole, fromBackdrop, at + ": from a backdrop array");
                    }
                    // several jobs at once
                    var jobs = new List<CpuCompositor.CompositeJob>(); var expected = new List<byte[]>();
                    for (int j = 0; j < 5; j++)
                    {
                        int x = rnd.Next(d.Width), y = rnd.Next(d.Height), w = 1 + rnd.Next(d.Width - x), h = 1 + rnd.Next(d.Height - y), k = rnd.Next(count + 1);
                        var below = new byte[w * h * 4];
                        CpuCompositor.CompositeRegions(d, ch, new[] { new CpuCompositor.CompositeJob(x, y, w, h, new byte[w * h * 4], 0, k, below) });
                        jobs.Add(new CpuCompositor.CompositeJob(x, y, w, h, below, k)); expected.Add(CpuCompositor.CompositeRegion(d, ch, x, y, w, h));
                    }
                    CpuCompositor.CompositeRegions(d, ch, jobs);
                    for (int j = 0; j < jobs.Count; j++) CpuCompositingTests.AssertSameBytes(expected[j], jobs[j].Pixels, $"document {c} {ch}: job {j} of several");
                }
            }
        }
        static byte[] Head(byte[] a, int n) { var r = new byte[n]; Buffer.BlockCopy(a, 0, r, 0, n); return r; }

        [Test] public void BadJobsAreRefused()
        {
            var d = new PaintDocument(64, 48, 16);
            d.AddLayer("a").GetChannel(PaintChannel.Color).SetPixel(3, 4, new Rgba32(1, 2, 3, 255)); d.AddLayer("b").GetChannel(PaintChannel.Color).SetPixel(5, 6, new Rgba32(4, 5, 6, 128));
            Assert.That(CpuCompositor.Plan(d, PaintChannel.Color).Count, Is.EqualTo(2));
            var ok = new byte[16 * 16 * 4];
            void Refused<T>(string why, params CpuCompositor.CompositeJob[] jobs) where T : Exception
            { Assert.That(() => CpuCompositor.CompositeRegions(d, PaintChannel.Color, jobs), Throws.InstanceOf<T>(), why); }
            Assert.That(() => CpuCompositor.CompositeRegions(null, PaintChannel.Color, new CpuCompositor.CompositeJob[0]), Throws.TypeOf<ArgumentNullException>());
            Assert.That(() => CpuCompositor.CompositeRegions(d, PaintChannel.Color, null), Throws.TypeOf<ArgumentNullException>());
            Assert.That(() => CpuCompositor.CompositeRegions(d, (PaintChannel)99, new CpuCompositor.CompositeJob[0]), Throws.TypeOf<ArgumentOutOfRangeException>());
            Refused<ArgumentNullException>("a null job", new CpuCompositor.CompositeJob[] { null });
            Refused<ArgumentOutOfRangeException>("outside the document", new CpuCompositor.CompositeJob(56, 0, 16, 16, ok));
            Refused<ArgumentException>("pixels too short", new CpuCompositor.CompositeJob(0, 0, 16, 16, new byte[16 * 16 * 4 - 1]));
            Refused<ArgumentException>("no pixels", new CpuCompositor.CompositeJob(0, 0, 16, 16, null));
            Refused<ArgumentException>("the same pixels twice", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok), new CpuCompositor.CompositeJob(16, 0, 16, 16, ok));
            Refused<ArgumentException>("pixels as the capture", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, 0, 1, ok));
            Refused<ArgumentOutOfRangeException>("start past the plan", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, 3));
            Refused<ArgumentOutOfRangeException>("a negative start", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, -1));
            Refused<ArgumentOutOfRangeException>("a capture below the start", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, 1, 0, new byte[ok.Length]));
            Refused<ArgumentOutOfRangeException>("a capture past the plan", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, 0, 3, new byte[ok.Length]));
            Refused<ArgumentException>("a capture index without an array", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, 0, 1, null));
            Refused<ArgumentException>("an array without a capture index", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, 0, -1, new byte[ok.Length]));
            Refused<ArgumentException>("a backdrop for a job from transparent", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, 0, new byte[ok.Length], 0, 64));
            Refused<ArgumentException>("a backdrop that is another job's pixels", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, 1, new byte[ok.Length], 0, 64), new CpuCompositor.CompositeJob(16, 0, 16, 16, new byte[ok.Length], 1, ok, 0, 64));
            Refused<ArgumentOutOfRangeException>("a backdrop too short", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, 1, new byte[ok.Length - 1], 0, 64));
            Refused<ArgumentOutOfRangeException>("a backdrop stride shorter than a row", new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, 1, new byte[ok.Length], 0, 60));
            // nothing is written when a job is refused
            var untouched = new byte[16 * 16 * 4];
            Refused<ArgumentOutOfRangeException>("one bad job refuses the call", new CpuCompositor.CompositeJob(0, 0, 16, 16, untouched), new CpuCompositor.CompositeJob(0, 0, 16, 16, ok, 9));
            Assert.That(Array.TrueForAll(untouched, v => v == 0), Is.True);
            // empty regions and no jobs are fine
            CpuCompositor.CompositeRegions(d, PaintChannel.Color, new CpuCompositor.CompositeJob[0]);
            CpuCompositor.CompositeRegions(d, PaintChannel.Color, new[] { new CpuCompositor.CompositeJob(5, 5, 0, 0, new byte[0]) });
        }
    }
}
