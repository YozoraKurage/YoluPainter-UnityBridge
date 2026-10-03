using System;
using System.Collections.Generic;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary><see cref="CpuCompositor.CompositeSampledRegions"/> (the composite on a grid that keeps one pixel of every step × step
    /// square, for the display's reduced preview): every sample is exactly the full composite at (sx·step + step/2, sy·step + step/2) —
    /// on random documents with every mode, masks, uniform tiles, clipping, groups, adjustments and filters (evaluated at full size before
    /// sampling), in the colour and Normal channels, from a sampled backdrop and with captures, at several thread counts. Bad steps and
    /// regions are refused. Runs with the managed loops, the registered kernels and the kernels with Burst switched off.</summary>
    [TestFixture(KernelChoice.Managed)]
    [TestFixture(KernelChoice.Registered)]
    [TestFixture(KernelChoice.RegisteredWithBurstOff)]
    public sealed class SampledCompositeTests
    {
        readonly KernelChoice choice;
        public SampledCompositeTests(KernelChoice choice) { this.choice = choice; }
        int savedDegree; KernelScope kernels;
        [SetUp] public void SaveDegree() { savedDegree = CoreParallelism.MaxDegreeOfParallelism; kernels = KernelScope.Use(choice); }
        [TearDown] public void RestoreDegree() { CoreParallelism.MaxDegreeOfParallelism = savedDegree; kernels?.Dispose(); }

        /// <summary>The samples of a full-size composite (w × h) at step.</summary>
        static byte[] Sample(byte[] full, int w, int h, int step)
        {
            int sw = w / step, sh = h / step; var s = new byte[sw * sh * 4];
            for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++) Buffer.BlockCopy(full, ((y * step + step / 2) * w + x * step + step / 2) * 4, s, (y * sw + x) * 4, 4);
            return s;
        }
        /// <summary>A random document whose tile size, width and height the step divides.</summary>
        static PaintDocument Document(Random rnd, int step)
        {
            for (; ; )
            {
                var d = CompositorExactnessTests.RandomDocument(rnd, 160);
                if (d.TileSize % step == 0 && d.Width % step == 0 && d.Height % step == 0) return d;
            }
        }

        [Test] public void EverySampleIsTheFullCompositeAtItsPixel()
        {
            var rnd = new Random(1003);
            for (int c = 0; c < 30; c++)
            {
                int step = new[] { 2, 4, 8 }[c % 3];
                var d = Document(rnd, step);
                CoreParallelism.MaxDegreeOfParallelism = c % 4 == 0 ? 1 : c % 4 == 1 ? 3 : 0;
                int sw = d.Width / step, sh = d.Height / step;
                foreach (var ch in new[] { PaintChannel.Color, PaintChannel.Normal, PaintChannel.Height })
                {
                    string at = $"document {c} {ch} {d.Width}×{d.Height} tile {d.TileSize} step {step}";
                    var expected = Sample(d.Composite(ch), d.Width, d.Height, step);
                    var whole = new byte[sw * sh * 4];
                    CpuCompositor.CompositeSampledRegions(d, ch, step, new[] { new CpuCompositor.CompositeJob(0, 0, sw, sh, whole) });
                    CpuCompositingTests.AssertSameBytes(expected, whole, at + ": the whole grid");
                    // a region, from a sampled backdrop below entry k, capturing on the way
                    var plan = CpuCompositor.Plan(d, ch); int k = rnd.Next(plan.Count + 1);
                    int x = rnd.Next(sw), y = rnd.Next(sh), w = 1 + rnd.Next(sw - x), h = 1 + rnd.Next(sh - y);
                    var fullCapture = new byte[d.Width * d.Height * 4];
                    CpuCompositor.CompositeRegions(d, ch, new[] { new CpuCompositor.CompositeJob(0, 0, d.Width, d.Height, new byte[fullCapture.Length], 0, k, fullCapture) });
                    var below = Sample(fullCapture, d.Width, d.Height, step);
                    var region = new byte[w * h * 4]; var capture = new byte[w * h * 4];
                    if (k == 0) CpuCompositor.CompositeSampledRegions(d, ch, step, new[] { new CpuCompositor.CompositeJob(x, y, w, h, region, 0, 0, capture) });
                    else CpuCompositor.CompositeSampledRegions(d, ch, step, new[] { new CpuCompositor.CompositeJob(x, y, w, h, region, k, below, (y * sw + x) * 4, sw * 4, k, capture) });
                    var expectedRegion = new byte[w * h * 4]; var expectedBelow = new byte[w * h * 4];
                    for (int row = 0; row < h; row++)
                    {
                        Buffer.BlockCopy(expected, ((y + row) * sw + x) * 4, expectedRegion, row * w * 4, w * 4);
                        Buffer.BlockCopy(below, ((y + row) * sw + x) * 4, expectedBelow, row * w * 4, w * 4);
                    }
                    CpuCompositingTests.AssertSameBytes(expectedRegion, region, at + $": region ({x}, {y}, {w}×{h}) from a sampled backdrop below {k}");
                    CpuCompositingTests.AssertSameBytes(expectedBelow, capture, at + ": the capture is the sample of the composite below k");
                }
            }
        }

        [Test] public void BadStepsAndRegionsAreRefused()
        {
            var d = new PaintDocument(64, 48, 16); d.AddLayer("a").GetChannel(PaintChannel.Color).SetPixel(3, 4, new Rgba32(1, 2, 3, 255));
            var ok = new byte[16 * 12 * 4];
            Assert.That(() => CpuCompositor.CompositeSampledRegions(null, PaintChannel.Color, 2, new CpuCompositor.CompositeJob[0]), Throws.TypeOf<ArgumentNullException>());
            foreach (int step in new[] { 0, -2, 3, 32 })
                Assert.That(() => CpuCompositor.CompositeSampledRegions(d, PaintChannel.Color, step, new[] { new CpuCompositor.CompositeJob(0, 0, 1, 1, ok) }), Throws.TypeOf<ArgumentOutOfRangeException>(), "step " + step);
            var odd = new PaintDocument(66, 48, 16);
            Assert.That(() => CpuCompositor.CompositeSampledRegions(odd, PaintChannel.Color, 4, new[] { new CpuCompositor.CompositeJob(0, 0, 1, 1, ok) }), Throws.TypeOf<ArgumentOutOfRangeException>(), "the width is not a multiple of the step");
            Assert.That(() => CpuCompositor.CompositeSampledRegions(d, PaintChannel.Color, 4, new[] { new CpuCompositor.CompositeJob(8, 0, 9, 12, ok) }), Throws.TypeOf<ArgumentOutOfRangeException>(), "outside the 16 × 12 grid");
            Assert.That(() => CpuCompositor.CompositeSampledRegions(d, PaintChannel.Color, 4, new[] { new CpuCompositor.CompositeJob(0, 0, 16, 12, new byte[16 * 12 * 4 - 1]) }), Throws.InstanceOf<ArgumentException>(), "pixels too short");
            CpuCompositor.CompositeSampledRegions(d, PaintChannel.Color, 4, new[] { new CpuCompositor.CompositeJob(0, 0, 16, 12, ok) });
            Assert.That(new[] { ok[0], ok[1], ok[2], ok[3] }, Is.EqualTo(new byte[] { 0, 0, 0, 0 }), "(3, 4) is not a sample at step 4 (samples at 2, 6, …)");
            CpuCompositor.CompositeSampledRegions(d, PaintChannel.Color, 1, new[] { new CpuCompositor.CompositeJob(0, 0, 16, 12, ok) });
            CpuCompositingTests.AssertSameBytes(CpuCompositor.CompositeRegion(d, PaintChannel.Color, 0, 0, 16, 12), ok, "step 1 is CompositeRegions");
        }
    }
}
