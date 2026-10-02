using System;

namespace Yozolab.YoluPainter.Core
{
    public enum PaintChannel { Color, Roughness, Metallic, Height, Normal, Emission }
    public enum LayerBlendMode { Normal, Multiply, Screen }

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

    /// <summary>Pixel-space sample: origin bottom-left, integer pixel centers are at n + 0.5. Time must not decrease.</summary>
    public readonly struct BrushSample
    {
        public readonly double X, Y, Pressure, Time;
        public BrushSample(double x, double y, double pressure = 1, double time = 0)
        {
            MathUtil.RequireFinite(x, nameof(x)); MathUtil.RequireFinite(y, nameof(y));
            MathUtil.RequireFinite(pressure, nameof(pressure)); MathUtil.RequireFinite(time, nameof(time));
            X = x; Y = y; Pressure = MathUtil.Clamp01(pressure); Time = time;
        }
    }

    /// <summary>How a brush with several tips picks one for each dab.</summary>
    public enum TipSelection { Random = 0, Sequential = 1 }

    public sealed class BrushSettings
    {
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

        public BrushSettings Clone()
        {
            return new BrushSettings { Radius = Radius, Hardness = Hardness, Spacing = Spacing, Opacity = Opacity,
                Flow = Flow, Color = Color, PressureSize = PressureSize, PressureOpacity = PressureOpacity, PressureFlow = PressureFlow, Erase = Erase,
                Tip = Tip, Tips = Tips == null ? null : (BrushTip[])Tips.Clone(), TipSelection = TipSelection, Angle = Angle, Roundness = Roundness, FollowDirection = FollowDirection,
                SizeJitter = SizeJitter, AngleJitter = AngleJitter, RoundnessJitter = RoundnessJitter, OpacityJitter = OpacityJitter, FlowJitter = FlowJitter,
                Scatter = Scatter, Count = Count, Seed = Seed, Texture = Texture, TextureDepth = TextureDepth, TextureScale = TextureScale };
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
            if (Roundness < 0.01 || Roundness > 1) throw new ArgumentOutOfRangeException(nameof(Roundness));
            foreach (double v in new[] { SizeJitter, AngleJitter, RoundnessJitter, OpacityJitter, FlowJitter, TextureDepth })
                if (v < 0 || v > 1) throw new ArgumentOutOfRangeException("jitter/texture depth", "Must be 0..1.");
            if (Scatter < 0 || Scatter > 10) throw new ArgumentOutOfRangeException(nameof(Scatter));
            if (Count < 1 || Count > 16) throw new ArgumentOutOfRangeException(nameof(Count));
            if (TextureScale < 0.05 || TextureScale > 64) throw new ArgumentOutOfRangeException(nameof(TextureScale));
            if (Tips != null && (Tips.Length > 256 || Array.IndexOf(Tips, null) >= 0)) throw new ArgumentException("Tips must hold 1..256 non-null tips.", nameof(Tips));
            if (!Enum.IsDefined(typeof(TipSelection), TipSelection)) throw new ArgumentOutOfRangeException(nameof(TipSelection));
        }
    }

    internal static class MathUtil
    {
        internal static double Clamp01(double value) { return Math.Max(0, Math.Min(1, value)); }
        internal static byte ToByte(double value) { return (byte)Math.Max(0, Math.Min(255, Math.Floor(value * 255 + 0.5))); }
        internal static void RequireFinite(double value, string name)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(name, "Must be finite."); }
    }
}
