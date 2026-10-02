using System;

namespace Yozolab.YoluPainter.Core
{
    public enum AdjustmentType { Invert = 0, Levels = 1, HueSaturation = 2 }

    /// <summary>Immutable parameters of a point adjustment (each output pixel depends only on the same input pixel).
    /// Formulas are this tool's native definitions in stored (encoded) RGB, version AlgorithmVersion. They are not claimed
    /// to match Photoshop's or CLIP STUDIO's adjustments of the same name. Alpha is never changed.</summary>
    public sealed class AdjustmentSettings : IEquatable<AdjustmentSettings>
    {
        public const int AlgorithmVersion = 1;
        public AdjustmentType Type { get; private set; }
        // Levels: input range, gamma (midtone), output range. All in 0..1 except gamma.
        public double InputBlack { get; private set; }
        public double InputWhite { get; private set; }
        public double Gamma { get; private set; }
        public double OutputBlack { get; private set; }
        public double OutputWhite { get; private set; }
        // HueSaturation: hue rotation in degrees (-180..180), saturation and lightness (-1..1).
        public double Hue { get; private set; }
        public double Saturation { get; private set; }
        public double Lightness { get; private set; }

        private AdjustmentSettings(AdjustmentType type) { Type = type; InputWhite = 1; Gamma = 1; OutputWhite = 1; }

        public static AdjustmentSettings Invert() { return new AdjustmentSettings(AdjustmentType.Invert); }
        public static AdjustmentSettings Levels(double inputBlack = 0, double inputWhite = 1, double gamma = 1, double outputBlack = 0, double outputWhite = 1)
        {
            var s = new AdjustmentSettings(AdjustmentType.Levels)
            { InputBlack = inputBlack, InputWhite = inputWhite, Gamma = gamma, OutputBlack = outputBlack, OutputWhite = outputWhite };
            s.Validate(); return s;
        }
        public static AdjustmentSettings HueSaturation(double hue = 0, double saturation = 0, double lightness = 0)
        {
            var s = new AdjustmentSettings(AdjustmentType.HueSaturation) { Hue = hue, Saturation = saturation, Lightness = lightness };
            s.Validate(); return s;
        }

        public void Validate()
        {
            foreach (double v in new[] { InputBlack, InputWhite, Gamma, OutputBlack, OutputWhite, Hue, Saturation, Lightness }) MathUtil.RequireFinite(v, "adjustment");
            if (!Enum.IsDefined(typeof(AdjustmentType), Type)) throw new ArgumentOutOfRangeException(nameof(Type));
            if (InputBlack < 0 || InputWhite > 1 || InputWhite - InputBlack < 1.0 / 255) throw new ArgumentOutOfRangeException("input range", "Levels input range must be inside 0..1 and at least one step wide.");
            if (OutputBlack < 0 || OutputBlack > 1 || OutputWhite < 0 || OutputWhite > 1) throw new ArgumentOutOfRangeException("output range");
            if (Gamma < 0.1 || Gamma > 9.99) throw new ArgumentOutOfRangeException(nameof(Gamma), "Gamma must be 0.1..9.99.");
            if (Hue < -180 || Hue > 180 || Saturation < -1 || Saturation > 1 || Lightness < -1 || Lightness > 1) throw new ArgumentOutOfRangeException("hue/saturation/lightness");
        }

        /// <summary>Hue/saturation only makes sense for colour data; applying it to roughness or height would silently
        /// corrupt scalar values (the spec forbids applying a colour adjustment to Roughness).</summary>
        public bool AppliesTo(PaintChannel channel)
        { return Type != AdjustmentType.HueSaturation || channel == PaintChannel.Color || channel == PaintChannel.Emission; }

        /// <summary>The adjusted colour. Alpha is copied unchanged.</summary>
        public Rgba32 Apply(Rgba32 c)
        {
            switch (Type)
            {
                case AdjustmentType.Invert: return new Rgba32((byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B), c.A);
                case AdjustmentType.Levels: return new Rgba32(Level(c.R), Level(c.G), Level(c.B), c.A);
                default:
                    double r, g, b; HueSaturationLightness(c.R / 255.0, c.G / 255.0, c.B / 255.0, out r, out g, out b);
                    return new Rgba32(MathUtil.ToByte(r), MathUtil.ToByte(g), MathUtil.ToByte(b), c.A);
            }
        }
        byte Level(byte component)
        {
            double v = (component / 255.0 - InputBlack) / (InputWhite - InputBlack);
            v = Math.Max(0, Math.Min(1, v));
            v = Math.Pow(v, 1 / Gamma);
            return MathUtil.ToByte(OutputBlack + v * (OutputWhite - OutputBlack));
        }
        void HueSaturationLightness(double r, double g, double b, out double ro, out double go, out double bo)
        {
            // RGB → HSL
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0, d = max - min;
            if (d > 1e-12)
            {
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h /= 6;
            }
            h = h + Hue / 360; h -= Math.Floor(h);
            s = Math.Max(0, Math.Min(1, s * (1 + Saturation)));
            l = Lightness >= 0 ? l + (1 - l) * Lightness : l * (1 + Lightness);
            // HSL → RGB
            if (s <= 0) { ro = go = bo = l; return; }
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            ro = HueToRgb(p, q, h + 1.0 / 3); go = HueToRgb(p, q, h); bo = HueToRgb(p, q, h - 1.0 / 3);
        }
        static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        /// <summary>Composite step of an adjustment layer: the adjusted colour is combined with the colour below by the
        /// blend mode, then mixed back by amount (opacity × mask). Alpha stays the alpha below — an adjustment never makes
        /// a partially transparent pixel more opaque — and fully transparent pixels are left as they are.</summary>
        public Rgba32 Composite(Rgba32 below, double amount, LayerBlendMode mode)
        {
            // 完全に透明な画素は見えないので変えない（何も無いタイルを飛ばす最適化とも一致する）。
            if (amount <= 0 || below.A == 0) return below;
            return CpuCompositor.MixRgb(below, Apply(below), amount, mode);
        }

        public bool Equals(AdjustmentSettings other)
        {
            return other != null && Type == other.Type && InputBlack == other.InputBlack && InputWhite == other.InputWhite && Gamma == other.Gamma
                && OutputBlack == other.OutputBlack && OutputWhite == other.OutputWhite && Hue == other.Hue && Saturation == other.Saturation && Lightness == other.Lightness;
        }
        public override bool Equals(object obj) { return Equals(obj as AdjustmentSettings); }
        public override int GetHashCode() { unchecked { return ((int)Type * 397) ^ InputBlack.GetHashCode() ^ (Gamma.GetHashCode() * 31) ^ (Hue.GetHashCode() * 17) ^ Saturation.GetHashCode(); } }
    }
}
