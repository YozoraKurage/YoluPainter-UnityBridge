using System;
using System.Collections.Generic;
using System.Linq;

namespace Yozolab.YoluPainter.Core
{
    public readonly struct GradientStop : IEquatable<GradientStop>
    {
        public readonly double Position, Midpoint;
        public readonly Rgba32 Color;
        public GradientStop(double position, Rgba32 color, double midpoint = .5) { Position = position; Color = new Rgba32(color.R, color.G, color.B, 255); Midpoint = midpoint; }
        public bool Equals(GradientStop other) => Position == other.Position && Midpoint == other.Midpoint && Color == other.Color;
        public override bool Equals(object obj) => obj is GradientStop s && Equals(s);
        public override int GetHashCode() => Position.GetHashCode() ^ Midpoint.GetHashCode() ^ Color.GetHashCode();
    }
    public readonly struct GradientOpacityStop : IEquatable<GradientOpacityStop>
    {
        public readonly double Position, Opacity, Midpoint;
        public GradientOpacityStop(double position, double opacity, double midpoint = .5) { Position = position; Opacity = opacity; Midpoint = midpoint; }
        public bool Equals(GradientOpacityStop other) => Position == other.Position && Opacity == other.Opacity && Midpoint == other.Midpoint;
        public override bool Equals(object obj) => obj is GradientOpacityStop s && Equals(s);
        public override int GetHashCode() => Position.GetHashCode() ^ Opacity.GetHashCode() ^ Midpoint.GetHashCode();
    }
    public readonly struct GradientCurvePoint : IEquatable<GradientCurvePoint>
    {
        public readonly double X, Y;
        public GradientCurvePoint(double x, double y) { X = x; Y = y; }
        public bool Equals(GradientCurvePoint other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is GradientCurvePoint p && Equals(p);
        public override int GetHashCode() => X.GetHashCode() ^ Y.GetHashCode();
    }
    public enum GradientPreset { BlackWhite, WhiteBlack, ForegroundBackground, ForegroundTransparent, WarmCool }

    /// <summary>Immutable gradient: independent RGB and opacity stops, relative segment midpoints, and a bounded PCHIP value curve.
    /// RGB interpolates stored sRGB components (Photoshop Classic in an sRGB working space), without premultiplication; opacity is
    /// independent, so zero alpha retains its RGB. Scalar channels and masks use stored-value luminance, without sRGB decoding.
    /// Beyond the first/last stop the end value is held. Midpoints map their segment position to half the interpolation weight.</summary>
    public sealed class GradientRamp : IEquatable<GradientRamp>
    {
        public const int MaxStops = 32, MaxCurvePoints = 16;
        public const double MinGap = .0001, MinCurveGap = .02;
        public static readonly GradientRamp Default = new GradientRamp(new[] { new GradientStop(0, new Rgba32(0, 0, 0, 255)), new GradientStop(1, new Rgba32(255, 255, 255, 255)) },
            new[] { new GradientOpacityStop(0, 1), new GradientOpacityStop(1, 1) });
        public IReadOnlyList<GradientStop> Colors { get; }
        public IReadOnlyList<GradientOpacityStop> Opacities { get; }
        public IReadOnlyList<GradientCurvePoint> Curve { get; }
        readonly double[] tangents;
        public long ByteSize => 64 + Colors.Count * 24L + Opacities.Count * 24L + Curve.Count * 24L;

        public GradientRamp(IEnumerable<GradientStop> colors, IEnumerable<GradientOpacityStop> opacities, IEnumerable<GradientCurvePoint> curve = null)
        {
            var c = Bounded(colors, MaxStops, "colour stops"); var a = Bounded(opacities, MaxStops, "opacity stops");
            var p = Bounded(curve ?? new[] { new GradientCurvePoint(0, 0), new GradientCurvePoint(1, 1) }, MaxCurvePoints, "curve points");
            ValidatePositions(c.Select(s => s.Position).ToArray(), MinGap); ValidatePositions(a.Select(s => s.Position).ToArray(), MinGap);
            foreach (var s in c) Midpoint(s.Midpoint); foreach (var s in a) { Unit(s.Opacity); Midpoint(s.Midpoint); }
            ValidatePositions(p.Select(s => s.X).ToArray(), MinCurveGap - 1e-6);
            if (p[0].X != 0 || p[p.Length - 1].X != 1) throw new ArgumentException("The value curve must start at 0 and end at 1.");
            foreach (var v in p) Unit(v.Y);
            Colors = Array.AsReadOnly(c); Opacities = Array.AsReadOnly(a); Curve = Array.AsReadOnly(p); tangents = Tangents(p);
        }
        static T[] Bounded<T>(IEnumerable<T> source, int max, string name)
        {
            if (source == null) throw new ArgumentNullException(name);
            var list = new List<T>(); foreach (var item in source) { if (list.Count == max) throw new ArgumentOutOfRangeException(name, "Gradient point budget exceeded."); list.Add(item); }
            if (list.Count < 2) throw new ArgumentException("A gradient needs at least two " + name + "."); return list.ToArray();
        }
        static void Unit(double v) { MathUtil.RequireFinite(v, "gradient"); if (v < 0 || v > 1) throw new ArgumentOutOfRangeException("gradient", "Gradient values must be 0..1."); }
        static void Midpoint(double v) { Unit(v); if (v < .01 || v > .99) throw new ArgumentOutOfRangeException("midpoint", "Gradient midpoints must be .01.. .99."); }
        static void ValidatePositions(double[] p, double gap)
        {
            for (int i = 0; i < p.Length; i++) { Unit(p[i]); if (i > 0 && p[i] - p[i - 1] < gap) throw new ArgumentException("Gradient points must be ordered and separated."); }
        }
        public GradientRamp WithColors(IEnumerable<GradientStop> colors) => new GradientRamp(colors, Opacities, Curve);
        public GradientRamp WithOpacities(IEnumerable<GradientOpacityStop> opacity) => new GradientRamp(Colors, opacity, Curve);
        public GradientRamp WithCurve(IEnumerable<GradientCurvePoint> curve) => new GradientRamp(Colors, Opacities, curve);
        static double Clamp(double x) => x <= 0 ? 0 : x >= 1 ? 1 : x;
        static double Weight(double x, double a, double b, double middle)
        {
            double t = Clamp((x - a) / (b - a));
            return t <= middle ? .5 * t / middle : .5 + .5 * (t - middle) / (1 - middle);
        }
        public double CurveValue(double input)
        {
            MathUtil.RequireFinite(input, nameof(input)); double x = Clamp(input); int k = 1;
            while (k < Curve.Count - 1 && Curve[k].X < x) k++;
            var a = Curve[k - 1]; var b = Curve[k]; double h = b.X - a.X, t = (x - a.X) / h, t2 = t * t, t3 = t2 * t;
            return Clamp((2 * t3 - 3 * t2 + 1) * a.Y + (t3 - 2 * t2 + t) * h * tangents[k - 1] + (-2 * t3 + 3 * t2) * b.Y + (t3 - t2) * h * tangents[k]);
        }
        public Rgba32 Evaluate(double input, bool scalar = false)
        {
            return SampleStops(CurveValue(input), scalar);
        }
        /// <summary>Samples the stop positions without the value curve (the editor strip and new stops use this space).</summary>
        public Rgba32 SampleStops(double position, bool scalar = false)
        {
            MathUtil.RequireFinite(position, nameof(position)); double x = Clamp(position); int c = 1, a = 1;
            while (c < Colors.Count - 1 && Colors[c].Position < x) c++;
            while (a < Opacities.Count - 1 && Opacities[a].Position < x) a++;
            var c0 = Colors[c - 1]; var c1 = Colors[c]; var a0 = Opacities[a - 1]; var a1 = Opacities[a];
            double t = Weight(x, c0.Position, c1.Position, c0.Midpoint), u = Weight(x, a0.Position, a1.Position, a0.Midpoint);
            double r = c0.Color.R + (c1.Color.R - c0.Color.R) * t, g = c0.Color.G + (c1.Color.G - c0.Color.G) * t, b = c0.Color.B + (c1.Color.B - c0.Color.B) * t;
            if (scalar) r = g = b = .2126 * r + .7152 * g + .0722 * b;
            return new Rgba32((byte)Math.Floor(r + .5), (byte)Math.Floor(g + .5), (byte)Math.Floor(b + .5), MathUtil.ToByte(a0.Opacity + (a1.Opacity - a0.Opacity) * u));
        }
        static double[] Tangents(GradientCurvePoint[] p)
        {
            int n = p.Length; var m = new double[n]; var h = new double[n - 1]; var d = new double[n - 1];
            for (int k = 0; k < n - 1; k++) { h[k] = p[k + 1].X - p[k].X; d[k] = (p[k + 1].Y - p[k].Y) / h[k]; }
            if (n == 2) { m[0] = m[1] = d[0]; return m; }
            for (int k = 1; k < n - 1; k++) if (d[k - 1] * d[k] > 0) { double w1 = 2 * h[k] + h[k - 1], w2 = h[k] + 2 * h[k - 1]; m[k] = (w1 + w2) / (w1 / d[k - 1] + w2 / d[k]); }
            m[0] = Edge(h[0], h[1], d[0], d[1]); m[n - 1] = Edge(h[n - 2], h[n - 3], d[n - 2], d[n - 3]); return m;
        }
        static double Edge(double h0, double h1, double d0, double d1)
        { double m = ((2 * h0 + h1) * d0 - h0 * d1) / (h0 + h1); return Math.Sign(m) != Math.Sign(d0) ? 0 : Math.Sign(d0) != Math.Sign(d1) && Math.Abs(m) > Math.Abs(3 * d0) ? 3 * d0 : m; }
        public static GradientRamp Preset(GradientPreset preset, Rgba32 foreground, Rgba32 background)
        {
            Rgba32 black = new Rgba32(0, 0, 0, 255), white = new Rgba32(255, 255, 255, 255); Rgba32 first, last;
            switch (preset)
            {
                case GradientPreset.BlackWhite: return Default;
                case GradientPreset.WhiteBlack: first = white; last = black; break;
                case GradientPreset.ForegroundBackground: first = foreground; last = background; break;
                case GradientPreset.ForegroundTransparent: first = last = foreground; break;
                case GradientPreset.WarmCool: first = new Rgba32(255, 110, 40, 255); last = new Rgba32(40, 110, 255, 255); break;
                default: throw new ArgumentOutOfRangeException(nameof(preset));
            }
            return new GradientRamp(new[] { new GradientStop(0, first), new GradientStop(1, last) }, new[] { new GradientOpacityStop(0, 1), new GradientOpacityStop(1, preset == GradientPreset.ForegroundTransparent ? 0 : 1) });
        }
        public bool Equals(GradientRamp other) => other != null && Colors.SequenceEqual(other.Colors) && Opacities.SequenceEqual(other.Opacities) && Curve.SequenceEqual(other.Curve);
        public override bool Equals(object obj) => Equals(obj as GradientRamp);
        public override int GetHashCode() => Colors.Count ^ Opacities.Count << 8 ^ Curve.Count << 16 ^ Colors[0].GetHashCode();
    }
}
