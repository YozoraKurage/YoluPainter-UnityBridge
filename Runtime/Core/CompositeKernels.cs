using System;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>One rectangle for the compositor's inner loops: Rows × Count pixels of Below (read and written) combined with the
    /// pixels of Over, each image with its own first byte and row stride (RGBA8, rows from the lowest). The amount of a pixel is
    /// Opacity, or Opacity × MaskFactor[alpha of the mask pixel] when Mask is not null (the mask pixel's alpha is at
    /// MaskOffset + 3 + row × MaskStride + 4 × column). Separable modes carry Table (B(d, s) at d &lt;&lt; 8 | s, the doubles
    /// <see cref="CpuCompositor.BlendRgb"/> gives); Unit is b / 255 for every byte.</summary>
    public struct CompositeRect
    {
        public byte[] Below; public int BelowOffset, BelowStride;
        public byte[] Over; public int OverOffset, OverStride;
        public int Rows, Count;
        public byte[] Mask; public int MaskOffset, MaskStride;
        public double Opacity; public double[] MaskFactor;
        public LayerBlendMode Mode; public double[] Table;
        public double[] Unit;
    }

    /// <summary>Optional replacement of the compositor's inner loops (for example compiled by Burst, in an assembly of its own that
    /// registers itself here when it loads). A kernel must give exactly the bytes of the managed loop it replaces; a kernel returns
    /// false for a case it does not handle, and the managed loop runs instead. Without a registered kernel (<see cref="Current"/>
    /// null) everything runs as managed code, so the core needs no other assembly. Kernels are called from several worker threads at
    /// once (each on its own rectangles); they must not keep the arrays after returning.</summary>
    public abstract class CompositeKernels
    {
        static CompositeKernels current;
        /// <summary>The kernels compositing uses (read once at the start of each call). Null: the managed loops.</summary>
        public static CompositeKernels Current
        {
            get { return System.Threading.Volatile.Read(ref current); }
            set { System.Threading.Volatile.Write(ref current, value); }
        }
        /// <summary>A short name for diagnostics (for example the display backend text).</summary>
        public abstract string Name { get; }
        /// <summary>Called on the calling thread before the workers start (for example to compile on first use).</summary>
        public virtual void Prepare() { }
        /// <summary>CpuCompositor's colour blend (source-over with the blend colour) of Over onto Below.</summary>
        public virtual bool Blend(ref CompositeRect rect) { return false; }
        /// <summary>CpuCompositor.ClipOnto of Over (the clipped layer) onto Below (the clipping group); Below's alpha is kept.</summary>
        public virtual bool Clip(ref CompositeRect rect) { return false; }
        /// <summary>NormalMaps' blend of Over onto Below (the Normal channel).</summary>
        public virtual bool NormalBlend(ref CompositeRect rect) { return false; }
        /// <summary>NormalMaps.ClipOnto of Over onto Below (the Normal channel).</summary>
        public virtual bool NormalClip(ref CompositeRect rect) { return false; }
    }
}
