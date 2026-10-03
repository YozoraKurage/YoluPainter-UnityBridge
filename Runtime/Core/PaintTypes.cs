using System;

namespace Yozolab.YoluPainter.Core
{
    public enum PaintChannel { Color, Roughness, Metallic, Height, Normal, Emission }
    /// <summary>レイヤーの合成モード（Photoshop の分類と並び）。値は保存形式に入るので、並べ替えず末尾に足す。</summary>
    public enum LayerBlendMode
    {
        Normal, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, LinearDodge, LinearBurn,
        HardLight, SoftLight, VividLight, LinearLight, PinLight, HardMix, Difference, Exclusion, Subtract, Divide,
        Hue, Saturation, Color, Luminosity, DarkerColor, LighterColor,
        /// <summary>Groups only: the children composite into what is below as if they were not grouped.</summary>
        PassThrough,
    }

    /// <summary>Unassociated (straight) RGBA8. Alpha is linear coverage; RGB is stored without color conversion.</summary>
    public readonly struct Rgba32 : IEquatable<Rgba32>
    {
        public readonly byte R, G, B, A;
        public Rgba32(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }
        public static Rgba32 Transparent { get { return new Rgba32(0, 0, 0, 0); } }
        public bool Equals(Rgba32 other) { return R == other.R && G == other.G && B == other.B && A == other.A; }
        public override bool Equals(object obj) { return obj is Rgba32 && Equals((Rgba32)obj); }
        public override int GetHashCode() { return R | (G << 8) | (B << 16) | (A << 24); }
        public static bool operator ==(Rgba32 a, Rgba32 b) { return a.Equals(b); }
        public static bool operator !=(Rgba32 a, Rgba32 b) { return !a.Equals(b); }
        public override string ToString() { return string.Format("RGBA({0},{1},{2},{3})", R, G, B, A); }
    }

    public readonly struct TileCoord : IEquatable<TileCoord>, IComparable<TileCoord>
    {
        public readonly int X, Y;
        public TileCoord(int x, int y) { X = x; Y = y; }
        public bool Equals(TileCoord other) { return X == other.X && Y == other.Y; }
        public override bool Equals(object obj) { return obj is TileCoord && Equals((TileCoord)obj); }
        public override int GetHashCode() { unchecked { return (X * 397) ^ Y; } }
        public int CompareTo(TileCoord other) { int y = Y.CompareTo(other.Y); return y != 0 ? y : X.CompareTo(other.X); }
        public override string ToString() { return X + "," + Y; }
    }

    /// <summary>A detached full-sized tile, row-major RGBA, with zero padding outside the canvas.</summary>
    public sealed class TileData
    {
        public TileCoord Coord { get; private set; }
        public byte[] Bytes { get; private set; }
        public TileData(TileCoord coord, byte[] bytes) { Coord = coord; Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes)); }
    }

    /// <summary>Pixel-space sample: origin bottom-left, integer pixel centers are at n + 0.5. Time must not decrease.
    /// TiltX / TiltY are the pen's tilt in radians from upright along the canvas X and Y axes (0 = upright or no tilt data,
    /// clamped to ±π/2); see <see cref="PenTilt"/>.</summary>
    public readonly struct BrushSample
    {
        public readonly double X, Y, Pressure, Time, TiltX, TiltY;
        public BrushSample(double x, double y, double pressure = 1, double time = 0) : this(x, y, pressure, time, 0, 0) { }
        public BrushSample(double x, double y, double pressure, double time, double tiltX, double tiltY)
        {
            MathUtil.RequireFinite(x, nameof(x)); MathUtil.RequireFinite(y, nameof(y));
            MathUtil.RequireFinite(pressure, nameof(pressure)); MathUtil.RequireFinite(time, nameof(time));
            MathUtil.RequireFinite(tiltX, nameof(tiltX)); MathUtil.RequireFinite(tiltY, nameof(tiltY));
            X = x; Y = y; Pressure = MathUtil.Clamp01(pressure); Time = time;
            TiltX = Math.Max(-PenTilt.MaxAngle, Math.Min(PenTilt.MaxAngle, tiltX)); TiltY = Math.Max(-PenTilt.MaxAngle, Math.Min(PenTilt.MaxAngle, tiltY));
        }
    }

    /// <summary>How a brush with several tips picks one for each dab.</summary>
    public enum TipSelection { Random = 0, Sequential = 1 }

    public enum BrushEffect { Paint, Blur, Smudge, Clone }

    public sealed partial class BrushSettings
    {
        public const double MaxStrokeAssist = 10000;
        public double Radius = 16;
        public double Hardness = 0.8;
        /// <summary>Stamp interval as fraction of the nominal diameter. Independent of input packet size and pressure.</summary>
        public double Spacing = 0.15;
        public double Opacity = 1;
        public double Flow = 1;
        public Rgba32 Color = new Rgba32(255, 255, 255, 255);
        public bool PressureSize = true;
        public bool PressureOpacity = true;
        public bool PressureFlow;
        public bool Erase;
        public BrushEffect Effect;
        public int BlurRadius = 3;
        public double SmudgeStrength = .5;
        // クローンの相対位置はストロークだけの設定。窓の写し元は文書の画素とは別に管理する。
        public double CloneOffsetX, CloneOffsetY;

        // --- Tip shape. A null Tip is the procedural round tip shaped by Hardness. ---
        /// <summary>Sampled tip image, or null for the round tip. Its larger side spans the brush diameter.</summary>
        public BrushTip Tip;
        /// <summary>Several tips used in turn (GIMP image hoses, CLIP STUDIO tip arrays). When set it replaces Tip.</summary>
        public BrushTip[] Tips;
        public TipSelection TipSelection;
        /// <summary>Tip rotation in degrees (counter-clockwise, canvas Y up).</summary>
        public double Angle;
        /// <summary>Squash of the tip across its rotated vertical axis (1 = no squash).</summary>
        public double Roundness = 1;
        /// <summary>Adds the stroke direction to Angle (for bristle and calligraphy tips).</summary>
        public bool FollowDirection;
        // --- Per-dab randomness (0 = none). Deterministic for a given Seed. ---
        public double SizeJitter, AngleJitter, RoundnessJitter, OpacityJitter, FlowJitter;
        /// <summary>Random offset of each dab, in brush diameters along both axes.</summary>
        public double Scatter;
        /// <summary>Dabs placed at each spacing step (useful with Scatter).</summary>
        public int Count = 1;
        public int Seed;
        // --- Paper texture: multiplies coverage by a tiled grain in canvas pixels. ---
        public BrushTip Texture;
        /// <summary>0 = texture has no effect, 1 = coverage fully follows the texture.</summary>
        public double TextureDepth;
        /// <summary>Canvas pixels per texture pixel (2 = the texture is drawn twice as large).</summary>
        public double TextureScale = 1;
        // --- Stroke assistance (canvas pixels; 0 = off). ---
        /// <summary>Stabilizer string length: the brush follows the pointer only once it is farther than this, staying this far
        /// behind (Krita's / Lazy Nezumi's "pulled string"). Jitter shorter than the string is smoothed out; on commit the line
        /// is drawn on to the last input point. Independent of the input rate.</summary>
        public double Stabilizer;
        /// <summary>Taper in / out (入り・抜き): the brush size grows from 0 over the first TaperIn pixels of stroke length and
        /// shrinks to 0 over the last TaperOut pixels. Dabs within TaperOut of the latest point wait until the stroke continues
        /// or ends (the end is only known then).</summary>
        public double TaperIn, TaperOut;
        /// <summary>Joins the path points (after the stabilizer) with a centripetal Catmull-Rom curve (<see cref="StrokeCurve"/>)
        /// instead of straight segments, so sparse input (a fast hand, a busy editor) still draws a round line. A segment is drawn
        /// once the point after it arrives, and the last one on commit; pressure and tilt change linearly along each segment.
        /// Off by default (straight segments, byte-identical to before); the paint window turns it on.</summary>
        public bool CurveInterpolation;

        public BrushSettings Clone()
        {
            return new BrushSettings { Radius = Radius, Hardness = Hardness, Spacing = Spacing, Opacity = Opacity,
                Flow = Flow, Color = Color, PressureSize = PressureSize, PressureOpacity = PressureOpacity, PressureFlow = PressureFlow, Erase = Erase, Effect = Effect, BlurRadius = BlurRadius, SmudgeStrength = SmudgeStrength, CloneOffsetX = CloneOffsetX, CloneOffsetY = CloneOffsetY,
                Tip = Tip, Tips = Tips == null ? null : (BrushTip[])Tips.Clone(), TipSelection = TipSelection, Angle = Angle, Roundness = Roundness, FollowDirection = FollowDirection,
                SizeJitter = SizeJitter, AngleJitter = AngleJitter, RoundnessJitter = RoundnessJitter, OpacityJitter = OpacityJitter, FlowJitter = FlowJitter,
                Scatter = Scatter, Count = Count, Seed = Seed, Texture = Texture, TextureDepth = TextureDepth, TextureScale = TextureScale,
                Stabilizer = Stabilizer, TaperIn = TaperIn, TaperOut = TaperOut, CurveInterpolation = CurveInterpolation }.WithDynamicsOf(this);
        }
        public void Validate()
        {
            MathUtil.RequireFinite(Radius, nameof(Radius)); MathUtil.RequireFinite(Hardness, nameof(Hardness));
            MathUtil.RequireFinite(Spacing, nameof(Spacing)); MathUtil.RequireFinite(Opacity, nameof(Opacity)); MathUtil.RequireFinite(Flow, nameof(Flow));
            if (Radius <= 0 || Radius > 65536) throw new ArgumentOutOfRangeException(nameof(Radius));
            if (Hardness < 0 || Hardness > 1) throw new ArgumentOutOfRangeException(nameof(Hardness));
            if (Spacing < 0.01 || Spacing > 4) throw new ArgumentOutOfRangeException(nameof(Spacing));
            if (Opacity < 0 || Opacity > 1) throw new ArgumentOutOfRangeException(nameof(Opacity));
            if (Flow < 0 || Flow > 1) throw new ArgumentOutOfRangeException(nameof(Flow));
            foreach (double v in new[] { Angle, Roundness, SizeJitter, AngleJitter, RoundnessJitter, OpacityJitter, FlowJitter, Scatter, TextureDepth, TextureScale })
                MathUtil.RequireFinite(v, "brush dynamics");
            foreach (double v in new[] { Stabilizer, TaperIn, TaperOut })
            { MathUtil.RequireFinite(v, "stroke assistance"); if (v < 0 || v > MaxStrokeAssist) throw new ArgumentOutOfRangeException("stroke assistance", "Stabilizer and tapers must be 0.." + MaxStrokeAssist + " pixels."); }
            if (Roundness < 0.01 || Roundness > 1) throw new ArgumentOutOfRangeException(nameof(Roundness));
            foreach (double v in new[] { SizeJitter, AngleJitter, RoundnessJitter, OpacityJitter, FlowJitter, TextureDepth })
                if (v < 0 || v > 1) throw new ArgumentOutOfRangeException("jitter/texture depth", "Must be 0..1.");
            if (Scatter < 0 || Scatter > 10) throw new ArgumentOutOfRangeException(nameof(Scatter));
            if (Count < 1 || Count > 16) throw new ArgumentOutOfRangeException(nameof(Count));
            if (TextureScale < 0.05 || TextureScale > 64) throw new ArgumentOutOfRangeException(nameof(TextureScale));
            if (Tips != null && (Tips.Length > 256 || Array.IndexOf(Tips, null) >= 0)) throw new ArgumentException("Tips must hold 1..256 non-null tips.", nameof(Tips));
            if (!Enum.IsDefined(typeof(TipSelection), TipSelection)) throw new ArgumentOutOfRangeException(nameof(TipSelection));
            ValidateDynamics();
            if (!Enum.IsDefined(typeof(BrushEffect), Effect)) throw new ArgumentOutOfRangeException(nameof(Effect));
            if (BlurRadius < 1 || BlurRadius > 64) throw new ArgumentOutOfRangeException(nameof(BlurRadius));
            MathUtil.RequireFinite(SmudgeStrength, nameof(SmudgeStrength));
            if (SmudgeStrength < 0 || SmudgeStrength > 1) throw new ArgumentOutOfRangeException(nameof(SmudgeStrength));
            foreach (double v in new[] { CloneOffsetX, CloneOffsetY }) { MathUtil.RequireFinite(v, "clone offset"); if (Math.Abs(v) > 10000000) throw new ArgumentOutOfRangeException("clone offset"); }
            if (Effect != BrushEffect.Paint && Erase) throw new ArgumentException("Pixel effect brushes cannot erase.");
        }
    }

    internal static class MathUtil
    {
        internal static double Clamp01(double value) { return Math.Max(0, Math.Min(1, value)); }
        /// <summary>Round half up to 0..255: floor(value × 255 + 0.5) clamped. Written with comparisons instead of
        /// Math.Floor / Min / Max (which the editor's Mono calls instead of inlining); the result is the same for every double,
        /// infinities and NaN (0) included.</summary>
        internal static byte ToByte(double value)
        {
            double v = value * 255 + 0.5;
            return v >= 255 ? (byte)255 : v > 0 ? (byte)(int)v : (byte)0; // (int) of a non-negative value is its floor
        }
        /// <summary>b / 255.0 for every byte b (the same doubles as the division; a load instead of a divide).</summary>
        internal static readonly double[] ByteUnit = MakeByteUnit();
        static double[] MakeByteUnit() { var t = new double[256]; for (int i = 0; i < 256; i++) t[i] = i / 255.0; return t; }
        internal static void RequireFinite(double value, string name)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(name, "Must be finite."); }
    }

    /// <summary>How many threads the CPU paths of the core may use at once: compositing (<see cref="CpuCompositor"/>), the Normal
    /// output, fills and gradients, transforms, the magic wand, filters, selection edits and large brush dabs. Every pixel is
    /// computed from its own inputs independently of the scheduling, so any value gives the same bytes; 1 runs everything on the
    /// calling thread. The document stays single-threaded for its callers: an operation returns only after its workers finish,
    /// and workers never change the document's structure, history or budgets. Read at the start of each operation (a change does
    /// not affect one that is running). Mesh baking has its own limit (MeshBakeBudget).</summary>
    public static class CoreParallelism
    {
        static int maxDegree;
        /// <summary>0 (the default) = one per logical processor (<see cref="Environment.ProcessorCount"/>); n &gt; 0 = at most n.</summary>
        public static int MaxDegreeOfParallelism
        {
            get { return maxDegree; }
            set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Use 0 for one thread per processor, or a positive count."); maxDegree = value; }
        }
        /// <summary>The number of threads an operation starting now may use (at least 1).</summary>
        public static int Degree { get { int d = maxDegree; return d > 0 ? d : Math.Max(1, Environment.ProcessorCount); } }

        /// <summary>Parallel.For options that keep to <see cref="Degree"/>.</summary>
        internal static System.Threading.Tasks.ParallelOptions Options() { return new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Degree }; }

        /// <summary>Runs body(i) for every i in [0, count): on the calling thread when degree (or count) is 1, otherwise on up to
        /// degree workers that each take the next index in turn. Each body must only write what belongs to its index. An exception
        /// stops the workers from taking more indices and is rethrown as it is (not wrapped in an AggregateException); with several,
        /// which one is rethrown is not defined.</summary>
        internal static void For(int count, int degree, Action<int> body)
        {
            if (count <= 0) return;
            degree = Math.Min(degree, count);
            if (degree <= 1) { for (int i = 0; i < count; i++) body(i); return; }
            int next = -1; Exception failure = null;
            System.Threading.Tasks.Parallel.For(0, degree, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = degree }, _ =>
            {
                for (int i; (i = System.Threading.Interlocked.Increment(ref next)) < count;)
                {
                    if (System.Threading.Volatile.Read(ref failure) != null) return;
                    try { body(i); }
                    catch (Exception e) { System.Threading.Interlocked.CompareExchange(ref failure, e, null); return; }
                }
            });
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        /// <summary><see cref="For"/> that also tells the body which worker runs it: body(worker, i) with worker in [0, workers), and
        /// no two bodies with the same worker run at once, so a worker can own a buffer for the whole run. The same exception
        /// rules as For.</summary>
        internal static void ForWorkers(int count, int workers, Action<int, int> body)
        {
            if (count <= 0) return;
            workers = Math.Min(workers, count);
            if (workers <= 1) { for (int i = 0; i < count; i++) body(0, i); return; }
            int next = -1; Exception failure = null;
            System.Threading.Tasks.Parallel.For(0, workers, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
            {
                for (int i; (i = System.Threading.Interlocked.Increment(ref next)) < count;)
                {
                    if (System.Threading.Volatile.Read(ref failure) != null) return;
                    try { body(worker, i); }
                    catch (Exception e) { System.Threading.Interlocked.CompareExchange(ref failure, e, null); return; }
                }
            });
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
