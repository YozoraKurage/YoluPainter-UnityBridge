using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>The CPU reference compositor's per-pixel formulas as they were before the compositor's inner loops were made faster
    /// (CpuCompositor at 89ad3f3: BlendUnchecked, BlendRgb with Separable and the W3C SetLum / SetSat, ClipOnto → MixRgb, Fade, and
    /// EvaluatePixel over the plan), kept here unchanged so that tests can show the fast span code and the reference still give the
    /// same bytes. It reads the document through public API only (Plan, GetOutputPixel, the mask factor, the adjustment's Apply);
    /// NormalMaps' public Blend / ClipOnto stand for the Normal channel (the speed-up did not change NormalMaps). Slow on purpose: one
    /// pixel at a time.</summary>
    internal static class FrozenCompositor
    {
        static readonly double[] Unit = MakeUnit();
        static double[] MakeUnit() { var t = new double[256]; for (int i = 0; i < 256; i++) t[i] = i / 255.0; return t; }
        static byte ToByte(double value) { double v = value * 255 + 0.5; return v >= 255 ? (byte)255 : v > 0 ? (byte)(int)v : (byte)0; }

        public static Rgba32 Blend(Rgba32 destination, Rgba32 source, double opacity, LayerBlendMode mode)
        {
            if (source.A == 255 && opacity == 1 && (mode == LayerBlendMode.Normal || mode == LayerBlendMode.PassThrough)) return source;
            var unit = Unit;
            double sa = unit[source.A] * opacity, da = unit[destination.A];
            if (sa <= 0) return destination;
            double a = sa + da * (1 - sa);
            if (a <= 0) return Rgba32.Transparent;
            double dr = unit[destination.R], dg = unit[destination.G], db = unit[destination.B];
            double sr = unit[source.R], sg = unit[source.G], sb = unit[source.B];
            double br, bg, bb;
            if (mode == LayerBlendMode.Normal || mode == LayerBlendMode.PassThrough) { br = sr; bg = sg; bb = sb; }
            else BlendRgb(mode, dr, dg, db, sr, sg, sb, out br, out bg, out bb);
            double wd = (1 - sa) * da, ws = (1 - da) * sa, wb = da * sa;
            return new Rgba32(ToByte((wd * dr + ws * sr + wb * br) / a), ToByte((wd * dg + ws * sg + wb * bg) / a),
                ToByte((wd * db + ws * sb + wb * bb) / a), ToByte(a));
        }
        public static void BlendRgb(LayerBlendMode mode, double dr, double dg, double db, double sr, double sg, double sb, out double r, out double g, out double b)
        {
            switch (mode)
            {
                case LayerBlendMode.Normal: case LayerBlendMode.PassThrough: r = sr; g = sg; b = sb; return;
                case LayerBlendMode.Hue: SetLum(SetSat(sr, sg, sb, Sat(dr, dg, db)), Lum(dr, dg, db), out r, out g, out b); return;
                case LayerBlendMode.Saturation: SetLum(SetSat(dr, dg, db, Sat(sr, sg, sb)), Lum(dr, dg, db), out r, out g, out b); return;
                case LayerBlendMode.Color: SetLum((sr, sg, sb), Lum(dr, dg, db), out r, out g, out b); return;
                case LayerBlendMode.Luminosity: SetLum((dr, dg, db), Lum(sr, sg, sb), out r, out g, out b); return;
                case LayerBlendMode.DarkerColor: if (sr + sg + sb < dr + dg + db - TieMargin) { r = sr; g = sg; b = sb; } else { r = dr; g = dg; b = db; } return;
                case LayerBlendMode.LighterColor: if (sr + sg + sb > dr + dg + db + TieMargin) { r = sr; g = sg; b = sb; } else { r = dr; g = dg; b = db; } return;
                default: r = Separable(mode, dr, sr); g = Separable(mode, dg, sg); b = Separable(mode, db, sb); return;
            }
        }
        const double TieMargin = .5 / 255;
        static double Separable(LayerBlendMode mode, double d, double s)
        {
            double v;
            switch (mode)
            {
                case LayerBlendMode.Multiply: v = d * s; break;
                case LayerBlendMode.Screen: v = d + s - d * s; break;
                case LayerBlendMode.Overlay: v = d <= .5 ? 2 * d * s : 1 - 2 * (1 - d) * (1 - s); break;
                case LayerBlendMode.Darken: v = Math.Min(d, s); break;
                case LayerBlendMode.Lighten: v = Math.Max(d, s); break;
                case LayerBlendMode.ColorDodge: v = Dodge(d, s); break;
                case LayerBlendMode.ColorBurn: v = Burn(d, s); break;
                case LayerBlendMode.LinearDodge: v = d + s; break;
                case LayerBlendMode.LinearBurn: v = d + s - 1; break;
                case LayerBlendMode.HardLight: v = s <= .5 ? 2 * d * s : 1 - 2 * (1 - d) * (1 - s); break;
                case LayerBlendMode.SoftLight: v = s <= .5 ? d - (1 - 2 * s) * d * (1 - d) : d + (2 * s - 1) * (Math.Sqrt(d) - d); break;
                case LayerBlendMode.VividLight: v = s <= .5 ? Burn(d, 2 * s) : Dodge(d, 2 * s - 1); break;
                case LayerBlendMode.LinearLight: v = d + 2 * s - 1; break;
                case LayerBlendMode.PinLight: v = s <= .5 ? Math.Min(d, 2 * s) : Math.Max(d, 2 * s - 1); break;
                case LayerBlendMode.HardMix: v = d + s >= 1 - TieMargin ? 1 : 0; break;
                case LayerBlendMode.Difference: v = Math.Abs(d - s); break;
                case LayerBlendMode.Exclusion: v = d + s - 2 * d * s; break;
                case LayerBlendMode.Subtract: v = d - s; break;
                case LayerBlendMode.Divide: v = s <= 0 ? (d <= 0 ? 0 : 1) : d / s; break;
                default: v = s; break;
            }
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }
        static double Dodge(double d, double s) { return d <= 0 ? 0 : s >= 1 ? 1 : Math.Min(1, d / (1 - s)); }
        static double Burn(double d, double s) { return d >= 1 ? 1 : s <= 0 ? 0 : 1 - Math.Min(1, (1 - d) / s); }
        static double Lum(double r, double g, double b) { return .3 * r + .59 * g + .11 * b; }
        static double Sat(double r, double g, double b) { return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)); }
        static void SetLum((double r, double g, double b) c, double l, out double r, out double g, out double b)
        {
            double delta = l - Lum(c.r, c.g, c.b);
            r = c.r + delta; g = c.g + delta; b = c.b + delta;
            double lum = Lum(r, g, b), n = Math.Min(r, Math.Min(g, b)), x = Math.Max(r, Math.Max(g, b));
            if (n < 0 && lum - n > 1e-12) { r = lum + (r - lum) * lum / (lum - n); g = lum + (g - lum) * lum / (lum - n); b = lum + (b - lum) * lum / (lum - n); }
            if (x > 1 && x - lum > 1e-12) { r = lum + (r - lum) * (1 - lum) / (x - lum); g = lum + (g - lum) * (1 - lum) / (x - lum); b = lum + (b - lum) * (1 - lum) / (x - lum); }
            r = Clamp01(r); g = Clamp01(g); b = Clamp01(b);
        }
        static (double r, double g, double b) SetSat(double r, double g, double b, double s)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max - min <= 1e-12) return (0, 0, 0);
            double Scale(double c) { return c == max ? s : c == min ? 0 : (c - min) * s / (max - min); }
            return (Scale(r), Scale(g), Scale(b));
        }
        static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        public static Rgba32 ClipOnto(Rgba32 group, Rgba32 clipped, double amount, LayerBlendMode mode)
        {
            double t = Unit[clipped.A] * amount;
            if (t <= 0 || group.A == 0) return group;
            return MixRgb(group, clipped, t, mode);
        }
        static Rgba32 MixRgb(Rgba32 below, Rgba32 over, double amount, LayerBlendMode mode)
        {
            var unit = Unit;
            double dr = unit[below.R], dg = unit[below.G], db = unit[below.B];
            BlendRgb(mode, dr, dg, db, unit[over.R], unit[over.G], unit[over.B], out double r, out double g, out double b);
            return new Rgba32(ToByte(dr + (r - dr) * amount), ToByte(dg + (g - dg) * amount), ToByte(db + (b - db) * amount), below.A);
        }
        static Rgba32 Fade(Rgba32 backdrop, Rgba32 inner, double amount)
        {
            if (amount >= 1) return inner;
            if (amount <= 0) return backdrop;
            var unit = Unit;
            double ba = unit[backdrop.A] * (1 - amount), ia = unit[inner.A] * amount, a = ba + ia;
            if (a <= 0) return Rgba32.Transparent;
            return new Rgba32(ToByte((unit[backdrop.R] * ba + unit[inner.R] * ia) / a), ToByte((unit[backdrop.G] * ba + unit[inner.G] * ia) / a),
                ToByte((unit[backdrop.B] * ba + unit[inner.B] * ia) / a), ToByte(a));
        }
        /// <summary>NormalMaps.Fade (internal) through its public Decode / Encode.</summary>
        static Rgba32 NormalFade(Rgba32 backdrop, Rgba32 inner, double amount)
        {
            if (amount >= 1) return inner;
            if (amount <= 0) return backdrop;
            double ba = backdrop.A / 255.0 * (1 - amount), ia = inner.A / 255.0 * amount, a = ba + ia;
            if (a <= 0) return Rgba32.Transparent;
            NormalMaps.Decode(backdrop, out double bx, out double by, out double bz); NormalMaps.Decode(inner, out double ix, out double iy, out double iz);
            return NormalMaps.Encode(ba * bx + ia * ix, ba * by + ia * iy, ba * bz + ia * iz, ToByte(a));
        }
        /// <summary>AdjustmentSettings.Composite with the frozen MixRgb.</summary>
        static Rgba32 Adjust(AdjustmentSettings settings, Rgba32 below, double amount, LayerBlendMode mode)
        {
            if (amount <= 0 || below.A == 0) return below;
            return MixRgb(below, settings.Apply(below), amount, mode);
        }

        static LayerBlendMode ModeOf(PaintLayer layer) { return layer.BlendMode == LayerBlendMode.PassThrough ? LayerBlendMode.Normal : layer.BlendMode; }
        static Rgba32 StackBlend(bool normal, Rgba32 below, Rgba32 over, double amount, LayerBlendMode mode)
        { return normal ? NormalMaps.Blend(below, over, amount, mode) : Blend(below, over, amount, mode); }
        static Rgba32 StackClip(bool normal, Rgba32 group, Rgba32 clipped, double amount, LayerBlendMode mode)
        { return normal ? NormalMaps.ClipOnto(group, clipped, amount, mode) : ClipOnto(group, clipped, amount, mode); }
        static Rgba32 StackFade(bool normal, Rgba32 backdrop, Rgba32 inner, double amount)
        { return normal ? NormalFade(backdrop, inner, amount) : Fade(backdrop, inner, amount); }

        static Rgba32 EvaluatePixel(IReadOnlyList<CpuCompositor.StackEntry> plan, Rgba32 backdrop, PaintChannel channel, int x, int y)
        {
            Rgba32 result = backdrop; bool normal = channel == PaintChannel.Normal;
            foreach (var entry in plan)
            {
                var layer = entry.Base;
                double amount = layer.Opacity * (layer.Mask == null ? 1 : layer.Mask.FactorAt(x, y));
                if (layer.Kind == LayerKind.Adjustment) { result = Adjust(layer.Adjustment, result, amount, layer.BlendMode); continue; }
                if (entry.PassesThrough) { result = StackFade(normal, result, EvaluatePixel(entry.Children, result, channel, x, y), amount); continue; }
                Rgba32 group = layer.IsGroup ? EvaluatePixel(entry.Children, Rgba32.Transparent, channel, x, y) : layer.GetOutputPixel(channel, x, y);
                foreach (var clip in entry.ClipEntries)
                {
                    var c = clip.Base;
                    double clipAmount = c.Opacity * (c.Mask == null ? 1 : c.Mask.FactorAt(x, y));
                    if (c.Kind == LayerKind.Adjustment) group = Adjust(c.Adjustment, group, clipAmount, c.BlendMode);
                    else group = StackClip(normal, group, c.IsGroup ? EvaluatePixel(clip.Children, Rgba32.Transparent, channel, x, y) : c.GetOutputPixel(channel, x, y), clipAmount, ModeOf(c));
                }
                result = StackBlend(normal, result, group, amount, ModeOf(layer));
            }
            return result;
        }

        /// <summary>The region composited one pixel at a time with the frozen formulas (row-major RGBA8 from the lowest row).</summary>
        public static byte[] Region(PaintDocument document, PaintChannel channel, int x, int y, int width, int height)
        {
            var plan = CpuCompositor.Plan(document, channel); var bytes = new byte[width * height * 4];
            for (int j = 0; j < height; j++)
                for (int i = 0; i < width; i++)
                {
                    var p = EvaluatePixel(plan, Rgba32.Transparent, channel, x + i, y + j); int o = (j * width + i) * 4;
                    bytes[o] = p.R; bytes[o + 1] = p.G; bytes[o + 2] = p.B; bytes[o + 3] = p.A;
                }
            return bytes;
        }
    }
}
