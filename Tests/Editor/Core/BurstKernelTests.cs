using System;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>The Burst-compiled compositing kernels (Runtime/Burst, registered when the com.unity.burst package is installed) give
    /// exactly the bytes of the core's managed loops, with Burst on and with Burst compilation switched off: for every below/over byte
    /// pair at chosen source alphas in every blend mode, as a layer and as a clipped layer, with subnormal amounts (a kernel that
    /// flushed subnormals to zero would differ), and for random vectors in the Normal channel (square roots). Ignored where the package
    /// is not installed (the managed loops then run, which CompositorExactnessTests covers).</summary>
    public sealed class BurstKernelTests
    {
        int savedDegree;
        [SetUp] public void Save() { savedDegree = CoreParallelism.MaxDegreeOfParallelism; }
        [TearDown] public void Restore() { CoreParallelism.MaxDegreeOfParallelism = savedDegree; }

        static void RequireBurst()
        {
            if (!KernelScope.BurstInstalled) Assert.Ignore("The Burst package is not installed here; the managed loops run.");
        }
        static byte[] CompositeWith(KernelChoice choice, PaintDocument d, PaintChannel channel)
        { using (KernelScope.Use(choice)) return d.Composite(channel); }
        static void AssertAllSame(PaintDocument d, PaintChannel channel, string context)
        {
            var managed = CompositeWith(KernelChoice.Managed, d, channel);
            PixelAssert.SameBytes(managed, CompositeWith(KernelChoice.Registered, d, channel), context + ": Burst");
            PixelAssert.SameBytes(managed, CompositeWith(KernelChoice.RegisteredWithBurstOff, d, channel), context + ": Burst switched off");
        }

        [Test] public void TheBurstKernelsAreRegisteredWhereBurstIsInstalled()
        {
            RequireBurst();
            Assert.That(KernelScope.Registered, Is.Not.Null);
            Assert.That(KernelScope.Registered.Name, Is.EqualTo("Burst"));
            Assert.That(CompositeKernels.Current, Is.SameAs(KernelScope.Registered), "a test left other kernels behind");
        }

        /// <summary>(x &amp; 255, y &amp; 255) = (below, over) in one channel (the others vary with them); the quadrant picks the source
        /// alpha. Every mode × amount × below alpha × clipped or not.</summary>
        [Test] public void EveryModeAndBytePairGivesTheManagedBytes()
        {
            RequireBurst();
            const int S = 512, T = 64;
            var sourceAlphas = new byte[] { 1, 128, 254, 255 };
            var modes = (LayerBlendMode[])Enum.GetValues(typeof(LayerBlendMode));
            foreach (string belowAlpha in new[] { "0", "255", "mixed" })
            {
                var d = new PaintDocument(S, S, T);
                var bottom = d.AddLayer("below"); var top = d.AddLayer("over");
                var bt = new byte[T * T * 4]; var tt = new byte[T * T * 4];
                for (int ty = 0; ty < S / T; ty++) for (int tx = 0; tx < S / T; tx++)
                {
                    for (int y = 0; y < T; y++) for (int x = 0; x < T; x++)
                    {
                        int px = tx * T + x, py = ty * T + y, dv = px & 255, sv = py & 255, o = (y * T + x) * 4;
                        bt[o] = (byte)dv; bt[o + 1] = (byte)(255 - dv); bt[o + 2] = (byte)(dv ^ 0x5A);
                        bt[o + 3] = belowAlpha == "0" ? (byte)0 : belowAlpha == "255" ? (byte)255 : (byte)((dv * 37 + sv * 11) & 255);
                        tt[o] = (byte)sv; tt[o + 1] = (byte)(sv ^ 0xA5); tt[o + 2] = (byte)(255 - sv); tt[o + 3] = sourceAlphas[(px >> 8) | (py >> 8) << 1];
                    }
                    bottom.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(tx, ty), bt);
                    top.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(tx, ty), tt);
                }
                d.ClearHistory();
                foreach (bool clip in new[] { false, true })
                {
                    d.SetLayerClipping(top.Id, clip);
                    foreach (var mode in modes)
                    {
                        if (mode == LayerBlendMode.PassThrough) continue; // groups only
                        foreach (double amount in new[] { 1, .7, 1e-305, 4.9e-324 })
                        {
                            d.SetLayerBlendMode(top.Id, mode); d.SetLayerOpacity(top.Id, amount);
                            AssertAllSame(d, PaintChannel.Color, $"{(clip ? "clipped " : "")}{mode} amount {amount:R} below alpha {belowAlpha}");
                        }
                    }
                }
            }
        }

        /// <summary>Random vectors in the Normal channel (blending renormalizes with square roots; Overlay is the RNM detail blend), as
        /// layers, clipped layers and under a mask, at several amounts.</summary>
        [Test] public void TheNormalChannelGivesTheManagedBytes()
        {
            RequireBurst();
            var rnd = new Random(11);
            var d = new PaintDocument(384, 256, 64);
            var layers = new PaintLayer[3];
            for (int i = 0; i < layers.Length; i++)
            {
                layers[i] = d.AddLayer("n" + i); d.SetChannelEnabled(layers[i].Id, PaintChannel.Normal, true);
                var b = new byte[64 * 64 * 4];
                for (int ty = 0; ty < 4; ty++) for (int tx = 0; tx < 6; tx++)
                {
                    rnd.NextBytes(b);
                    for (int k = 3; k < b.Length; k += 4) b[k] = i == 0 ? (byte)255 : (byte)(b[k] < 40 ? 0 : b[k]);
                    layers[i].GetChannel(PaintChannel.Normal).ImportTile(new TileCoord(tx, ty), b);
                }
            }
            var m = d.AddLayerMask(layers[2].Id); var mb = new byte[64 * 64 * 4];
            for (int k = 3; k < mb.Length; k += 4) mb[k] = (byte)rnd.Next(256);
            m.Surface.ImportTile(new TileCoord(1, 1), mb);
            d.ClearHistory();
            foreach (bool clip in new[] { false, true })
            {
                d.SetLayerClipping(layers[2].Id, clip);
                foreach (var mode in new[] { LayerBlendMode.Normal, LayerBlendMode.Overlay, LayerBlendMode.Multiply })
                    foreach (double amount in new[] { 1, .6, 1e-305, 4.9e-324 })
                    {
                        d.SetLayerBlendMode(layers[1].Id, mode); d.SetLayerBlendMode(layers[2].Id, mode); d.SetLayerOpacity(layers[2].Id, amount);
                        AssertAllSame(d, PaintChannel.Normal, $"Normal channel {(clip ? "clipped " : "")}{mode} amount {amount:R}");
                    }
            }
        }

        /// <summary>The rectangles split into row bands (few tiles) and whole tiles (many) give the same bytes with Burst, on the
        /// calling thread and on several workers.</summary>
        [Test] public void ThreadCountsAndBandsGiveTheSameBytes()
        {
            RequireBurst();
            var rnd = new Random(5);
            for (int c = 0; c < 8; c++)
            {
                var d = CompositorExactnessTests.RandomDocument(rnd, 200);
                foreach (int degree in new[] { 1, 3, 0 })
                {
                    CoreParallelism.MaxDegreeOfParallelism = degree;
                    AssertAllSame(d, PaintChannel.Color, $"document {c} threads {degree}");
                    int x = rnd.Next(d.Width), y = rnd.Next(d.Height), w = 1 + rnd.Next(d.Width - x), h = 1 + rnd.Next(d.Height - y);
                    byte[] Region(KernelChoice k) { using (KernelScope.Use(k)) return CpuCompositor.CompositeRegion(d, PaintChannel.Color, x, y, w, h); }
                    PixelAssert.SameBytes(Region(KernelChoice.Managed), Region(KernelChoice.Registered), $"document {c} threads {degree} region");
                }
            }
        }
    }
}
