using System;
using UnityEditor;

namespace Yozolab.YoluPainter.Core.Burst
{
    /// <summary>The Burst-compiled inner loops registered as <see cref="CompositeKernels.Current"/> when the editor loads (this assembly is
    /// only compiled where the com.unity.burst package is present; without it the core's managed loops run). Checks every rectangle
    /// against its arrays before handing raw pointers to Burst, pins the arrays with fixed for the call only and allocates nothing.
    /// Burst's own switch (Jobs ▸ Burst ▸ Enable Compilation, BurstCompiler.Options.EnableBurstCompilation) is honoured on every call:
    /// with it off the same kernel methods run as managed code.</summary>
    internal sealed unsafe class BurstCompositeKernels : CompositeKernels
    {
        [InitializeOnLoadMethod]
        static void Register() { if (!(Current is BurstCompositeKernels)) Current = new BurstCompositeKernels(); }

        public override string Name => "Burst";

        bool prepared;
        /// <summary>The first call compiles the kernels (synchronously); do it here, on the calling thread, before the workers.</summary>
        public override void Prepare()
        {
            if (prepared) return;
            BurstKernels.Blend(null, 0, null, 0, 0, 0, null, 0, 1, null, 0, null, null);
            BurstKernels.Clip(null, 0, null, 0, 0, 0, null, 0, 1, null, 0, null, null);
            BurstKernels.NormalBlend(null, 0, null, 0, 0, 0, null, 0, 1, null, 0);
            BurstKernels.NormalClip(null, 0, null, 0, 0, 0, null, 0, 1, null, 0);
            prepared = true;
        }

        static bool Separable(LayerBlendMode mode) { return mode >= LayerBlendMode.Multiply && mode <= LayerBlendMode.Divide; }
        /// <summary>The rectangle's rows of an image fit in its array (else a bug in the caller: refuse rather than read out of bounds).</summary>
        static void CheckImage(byte[] array, int offset, int stride, int rows, int count, string name)
        {
            if (array == null || offset < 0 || stride < count * 4 && rows > 1 || (long)offset + (long)(rows - 1) * stride + count * 4L > array.Length)
                throw new ArgumentOutOfRangeException(name, "The rectangle is outside its array.");
        }
        static bool Check(ref CompositeRect q, bool colour)
        {
            if (q.Rows <= 0 || q.Count <= 0) return false;
            CheckImage(q.Below, q.BelowOffset, q.BelowStride, q.Rows, q.Count, "Below");
            CheckImage(q.Over, q.OverOffset, q.OverStride, q.Rows, q.Count, "Over");
            if (q.Mask != null)
            {
                CheckImage(q.Mask, q.MaskOffset, q.MaskStride, q.Rows, q.Count, "Mask");
                if (q.MaskFactor == null || q.MaskFactor.Length < 256) throw new ArgumentException("A mask needs 256 factors.", nameof(q));
            }
            if (colour)
            {
                if (q.Unit == null || q.Unit.Length < 256) throw new ArgumentException("The unit table needs 256 values.", nameof(q));
                if (q.Table != null && q.Table.Length < 65536) throw new ArgumentException("A blend table needs 65536 values.", nameof(q));
            }
            return true;
        }

        public override bool Blend(ref CompositeRect q)
        {
            if (Separable(q.Mode) && q.Table == null) return false; // 表の無い分離モードは管理側で
            if (!Check(ref q, true)) return true;
            fixed (byte* below = q.Below, over = q.Over, mask = q.Mask)
            fixed (double* factor = q.MaskFactor, table = q.Table, unit = q.Unit)
                BurstKernels.Blend(below + q.BelowOffset, q.BelowStride, over + q.OverOffset, q.OverStride, q.Rows, q.Count,
                    mask == null ? null : mask + q.MaskOffset, q.MaskStride, q.Opacity, factor, (int)q.Mode, table, unit);
            return true;
        }
        public override bool Clip(ref CompositeRect q)
        {
            if (Separable(q.Mode) && q.Table == null) return false;
            if (!Check(ref q, true)) return true;
            fixed (byte* below = q.Below, over = q.Over, mask = q.Mask)
            fixed (double* factor = q.MaskFactor, table = q.Table, unit = q.Unit)
                BurstKernels.Clip(below + q.BelowOffset, q.BelowStride, over + q.OverOffset, q.OverStride, q.Rows, q.Count,
                    mask == null ? null : mask + q.MaskOffset, q.MaskStride, q.Opacity, factor, (int)q.Mode, table, unit);
            return true;
        }
        public override bool NormalBlend(ref CompositeRect q)
        {
            if (!Check(ref q, false)) return true;
            fixed (byte* below = q.Below, over = q.Over, mask = q.Mask)
            fixed (double* factor = q.MaskFactor)
                BurstKernels.NormalBlend(below + q.BelowOffset, q.BelowStride, over + q.OverOffset, q.OverStride, q.Rows, q.Count,
                    mask == null ? null : mask + q.MaskOffset, q.MaskStride, q.Opacity, factor, (int)q.Mode);
            return true;
        }
        public override bool NormalClip(ref CompositeRect q)
        {
            if (!Check(ref q, false)) return true;
            fixed (byte* below = q.Below, over = q.Over, mask = q.Mask)
            fixed (double* factor = q.MaskFactor)
                BurstKernels.NormalClip(below + q.BelowOffset, q.BelowStride, over + q.OverOffset, q.OverStride, q.Rows, q.Count,
                    mask == null ? null : mask + q.MaskOffset, q.MaskStride, q.Opacity, factor, (int)q.Mode);
            return true;
        }
    }
}
