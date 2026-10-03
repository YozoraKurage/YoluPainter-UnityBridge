using System;
using Unity.Burst;

namespace Yozolab.YoluPainter.Core.Burst
{
    /// <summary>The compositor's inner loops (CpuCompositor.BlendRect / ClipRect, NormalMaps' blend and clipping per pixel) written over
    /// raw pointers so that Burst compiles them. The arithmetic is the managed code's, operation for operation and in the same order;
    /// FloatMode.Strict keeps Burst from fusing, reordering or approximating it (no FMA, no reciprocal for a division, denormals kept).
    /// Only +, −, ×, ÷, square roots, comparisons and truncation run here: the separable modes' blend colours come in a table made by
    /// managed code, and b / 255 comes in the unit table. Nothing is allocated. Called directly from managed code (Burst's direct
    /// call: with Burst disabled the same methods run as managed code).</summary>
    [BurstCompile]
    internal static unsafe class BurstKernels
    {
        const double TieMargin = .5 / 255;
        const double MinShortcutAlpha = 1e-300; // CpuCompositor.MinShortcutAlpha
        const double DegenerateLengthSquared = 1e-12; // NormalMaps
        const int Normal = 0, Overlay = 3, Hue = 20, Saturation = 21, Color = 22, Luminosity = 23, DarkerColor = 24, LighterColor = 25, PassThrough = 26;

        static byte ToByte(double value)
        {
            double v = value * 255 + 0.5;
            return v >= 255 ? (byte)255 : v > 0 ? (byte)(int)v : (byte)0;
        }

        // ───────────── colour ─────────────

        /// <summary>CpuCompositor.BlendRect. table: the separable mode's B(d, s) (null for Normal and the non-separable modes).</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void Blend(byte* res, int resStride, byte* src, int srcStride, int rows, int count, byte* mask, int maskStride, double opacity, double* factor, int mode, double* table, double* unit)
        {
            bool simple = mode == Normal || mode == PassThrough;
            for (int y = 0; y < rows; y++)
            {
                byte* r = res + (long)y * resStride, s = src + (long)y * srcStride, m = mask == null ? null : mask + (long)y * maskStride + 3;
                for (int i = 0; i < count; i++, r += 4, s += 4, m += 4)
                {
                    byte sA = s[3];
                    if (sA == 0) continue;
                    double amount = mask == null ? opacity : opacity * factor[*m];
                    if (simple && sA == 255 && amount == 1) { r[0] = s[0]; r[1] = s[1]; r[2] = s[2]; r[3] = 255; continue; }
                    double sa = unit[sA] * amount;
                    if (sa <= 0) continue;
                    byte dA = r[3];
                    if (dA == 0 && sa >= MinShortcutAlpha)
                    {
                        r[0] = s[0]; r[1] = s[1]; r[2] = s[2];
                        double v = sa * 255 + 0.5; r[3] = v >= 255 ? (byte)255 : v > 0 ? (byte)(int)v : (byte)0;
                        continue;
                    }
                    double dr = unit[r[0]], dg = unit[r[1]], db = unit[r[2]];
                    double sr = unit[s[0]], sg = unit[s[1]], sb = unit[s[2]];
                    double br, bg, bb;
                    if (simple) { br = sr; bg = sg; bb = sb; }
                    else if (table != null) { br = table[r[0] << 8 | s[0]]; bg = table[r[1] << 8 | s[1]]; bb = table[r[2] << 8 | s[2]]; }
                    else BlendRgb(mode, dr, dg, db, sr, sg, sb, out br, out bg, out bb);
                    double vr, vg, vb;
                    if (dA == 255)
                    {
                        double t = 1 - sa;
                        vr = (t * dr + sa * br) * 255 + 0.5; vg = (t * dg + sa * bg) * 255 + 0.5; vb = (t * db + sa * bb) * 255 + 0.5;
                        r[3] = 255;
                    }
                    else
                    {
                        double da = unit[dA], a = sa + da * (1 - sa);
                        double wd = (1 - sa) * da, ws = (1 - da) * sa, wb = da * sa;
                        vr = (wd * dr + ws * sr + wb * br) / a * 255 + 0.5; vg = (wd * dg + ws * sg + wb * bg) / a * 255 + 0.5;
                        vb = (wd * db + ws * sb + wb * bb) / a * 255 + 0.5; double va = a * 255 + 0.5;
                        r[3] = va >= 255 ? (byte)255 : va > 0 ? (byte)(int)va : (byte)0;
                    }
                    r[0] = vr >= 255 ? (byte)255 : vr > 0 ? (byte)(int)vr : (byte)0;
                    r[1] = vg >= 255 ? (byte)255 : vg > 0 ? (byte)(int)vg : (byte)0;
                    r[2] = vb >= 255 ? (byte)255 : vb > 0 ? (byte)(int)vb : (byte)0;
                }
            }
        }

        /// <summary>CpuCompositor.ClipRect: in place on the clipping group (g), its alpha kept.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void Clip(byte* g, int gStride, byte* c, int cStride, int rows, int count, byte* mask, int maskStride, double opacity, double* factor, int mode, double* table, double* unit)
        {
            bool simple = mode == Normal || mode == PassThrough;
            for (int y = 0; y < rows; y++)
            {
                byte* r = g + (long)y * gStride, s = c + (long)y * cStride, m = mask == null ? null : mask + (long)y * maskStride + 3;
                for (int i = 0; i < count; i++, r += 4, s += 4, m += 4)
                {
                    byte cA = s[3];
                    if (cA == 0 || r[3] == 0) continue;
                    double a = unit[cA] * (mask == null ? opacity : opacity * factor[*m]);
                    if (a <= 0) continue;
                    double dr = unit[r[0]], dg = unit[r[1]], db = unit[r[2]];
                    double br, bg, bb;
                    if (simple) { br = unit[s[0]]; bg = unit[s[1]]; bb = unit[s[2]]; }
                    else if (table != null) { br = table[r[0] << 8 | s[0]]; bg = table[r[1] << 8 | s[1]]; bb = table[r[2] << 8 | s[2]]; }
                    else BlendRgb(mode, dr, dg, db, unit[s[0]], unit[s[1]], unit[s[2]], out br, out bg, out bb);
                    double vr = (dr + (br - dr) * a) * 255 + 0.5, vg = (dg + (bg - dg) * a) * 255 + 0.5, vb = (db + (bb - db) * a) * 255 + 0.5;
                    r[0] = vr >= 255 ? (byte)255 : vr > 0 ? (byte)(int)vr : (byte)0;
                    r[1] = vg >= 255 ? (byte)255 : vg > 0 ? (byte)(int)vg : (byte)0;
                    r[2] = vb >= 255 ? (byte)255 : vb > 0 ? (byte)(int)vb : (byte)0;
                }
            }
        }

        /// <summary>CpuCompositor.BlendRgb for Normal and the non-separable modes (the separable ones come from the table).</summary>
        static void BlendRgb(int mode, double dr, double dg, double db, double sr, double sg, double sb, out double r, out double g, out double b)
        {
            double tr, tg, tb;
            switch (mode)
            {
                case Hue: SetSat(sr, sg, sb, Sat(dr, dg, db), out tr, out tg, out tb); SetLum(tr, tg, tb, Lum(dr, dg, db), out r, out g, out b); return;
                case Saturation: SetSat(dr, dg, db, Sat(sr, sg, sb), out tr, out tg, out tb); SetLum(tr, tg, tb, Lum(dr, dg, db), out r, out g, out b); return;
                case Color: SetLum(sr, sg, sb, Lum(dr, dg, db), out r, out g, out b); return;
                case Luminosity: SetLum(dr, dg, db, Lum(sr, sg, sb), out r, out g, out b); return;
                case DarkerColor: if (sr + sg + sb < dr + dg + db - TieMargin) { r = sr; g = sg; b = sb; } else { r = dr; g = dg; b = db; } return;
                case LighterColor: if (sr + sg + sb > dr + dg + db + TieMargin) { r = sr; g = sg; b = sb; } else { r = dr; g = dg; b = db; } return;
                default: r = sr; g = sg; b = sb; return; // Normal / PassThrough (the wrapper refuses separable modes without a table)
            }
        }
        static double Lum(double r, double g, double b) { return .3 * r + .59 * g + .11 * b; }
        static double Sat(double r, double g, double b)
        {
            double max = g > b ? g : b; if (r > max) max = r;
            double min = g < b ? g : b; if (r < min) min = r;
            return max - min;
        }
        static void SetLum(double cr, double cg, double cb, double l, out double r, out double g, out double b)
        {
            double delta = l - Lum(cr, cg, cb);
            r = cr + delta; g = cg + delta; b = cb + delta;
            double lum = Lum(r, g, b), n = g < b ? g : b, x = g > b ? g : b;
            if (r < n) n = r; if (r > x) x = r;
            if (n < 0 && lum - n > 1e-12) { r = lum + (r - lum) * lum / (lum - n); g = lum + (g - lum) * lum / (lum - n); b = lum + (b - lum) * lum / (lum - n); }
            if (x > 1 && x - lum > 1e-12) { r = lum + (r - lum) * (1 - lum) / (x - lum); g = lum + (g - lum) * (1 - lum) / (x - lum); b = lum + (b - lum) * (1 - lum) / (x - lum); }
            r = r < 0 ? 0 : r > 1 ? 1 : r; g = g < 0 ? 0 : g > 1 ? 1 : g; b = b < 0 ? 0 : b > 1 ? 1 : b;
        }
        static void SetSat(double r, double g, double b, double s, out double or, out double og, out double ob)
        {
            double max = g > b ? g : b; if (r > max) max = r;
            double min = g < b ? g : b; if (r < min) min = r;
            if (max - min <= 1e-12) { or = 0; og = 0; ob = 0; return; }
            or = r == max ? s : r == min ? 0 : (r - min) * s / (max - min);
            og = g == max ? s : g == min ? 0 : (g - min) * s / (max - min);
            ob = b == max ? s : b == min ? 0 : (b - min) * s / (max - min);
        }

        // ───────────── Normal channel (NormalMaps) ─────────────

        static void Normalize(ref double x, ref double y, ref double z)
        {
            double l2 = x * x + y * y + z * z;
            if (l2 < DegenerateLengthSquared) { x = 0; y = 0; z = 1; return; }
            double l = Math.Sqrt(l2); x /= l; y /= l; z /= l;
        }
        static void Decode(byte* p, out double x, out double y, out double z)
        {
            x = p[0] / 255.0 * 2 - 1; y = p[1] / 255.0 * 2 - 1; z = p[2] / 255.0 * 2 - 1;
            Normalize(ref x, ref y, ref z);
        }
        static void Encode(double x, double y, double z, byte* p)
        {
            Normalize(ref x, ref y, ref z);
            p[0] = ToByte(x * .5 + .5); p[1] = ToByte(y * .5 + .5); p[2] = ToByte(z * .5 + .5);
        }
        static void Combine(int mode, double bx, double by, double bz, double sx, double sy, double sz, out double x, out double y, out double z)
        {
            if (mode == Overlay)
            {
                // NormalMaps.Rnm
                double tx = bx, ty = by, tz = bz + 1, ux = -sx, uy = -sy, uz = sz;
                if (tz <= 1e-6) { x = bx; y = by; z = bz; return; }
                double k = (tx * ux + ty * uy + tz * uz) / tz;
                x = tx * k - ux; y = ty * k - uy; z = tz * k - uz;
            }
            else { x = sx; y = sy; z = sz; }
        }

        /// <summary>NormalMaps.BlendUnchecked per pixel.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void NormalBlend(byte* res, int resStride, byte* src, int srcStride, int rows, int count, byte* mask, int maskStride, double opacity, double* factor, int mode)
        {
            for (int y = 0; y < rows; y++)
            {
                byte* r = res + (long)y * resStride, s = src + (long)y * srcStride, m = mask == null ? null : mask + (long)y * maskStride + 3;
                for (int i = 0; i < count; i++, r += 4, s += 4, m += 4)
                {
                    double amount = mask == null ? opacity : opacity * factor[*m];
                    double t = s[3] / 255.0 * amount, da = r[3] / 255.0;
                    if (t <= 0) continue;
                    Decode(r, out double bx, out double by, out double bz); Decode(s, out double sx, out double sy, out double sz);
                    Combine(mode, bx, by, bz, sx, sy, sz, out double cx, out double cy, out double cz);
                    double wb = (1 - t) * da, ws = (1 - da) * t, wc = da * t;
                    double ex = wb * bx + ws * sx + wc * cx, ey = wb * by + ws * sy + wc * cy, ez = wb * bz + ws * sz + wc * cz;
                    byte alpha = ToByte(t + da * (1 - t));
                    Encode(ex, ey, ez, r); r[3] = alpha;
                }
            }
        }

        /// <summary>NormalMaps.ClipOnto per pixel (the group keeps its alpha).</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void NormalClip(byte* g, int gStride, byte* c, int cStride, int rows, int count, byte* mask, int maskStride, double opacity, double* factor, int mode)
        {
            for (int y = 0; y < rows; y++)
            {
                byte* r = g + (long)y * gStride, s = c + (long)y * cStride, m = mask == null ? null : mask + (long)y * maskStride + 3;
                for (int i = 0; i < count; i++, r += 4, s += 4, m += 4)
                {
                    double amount = mask == null ? opacity : opacity * factor[*m];
                    double t = s[3] / 255.0 * amount;
                    if (t <= 0 || r[3] == 0) continue;
                    Decode(r, out double gx, out double gy, out double gz); Decode(s, out double sx, out double sy, out double sz);
                    Combine(mode, gx, gy, gz, sx, sy, sz, out double cx, out double cy, out double cz);
                    Encode((1 - t) * gx + t * cx, (1 - t) * gy + t * cy, (1 - t) * gz + t * cz, r);
                }
            }
        }
    }
}
