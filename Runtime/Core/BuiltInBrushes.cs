using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>A named brush: settings to start from (the user's colour and size are applied on top by the UI).</summary>
    public sealed class BrushPreset
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Category { get; private set; }
        readonly BrushSettings settings;
        public BrushPreset(string id, string name, string category, BrushSettings settings)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("A preset needs an id.", nameof(id));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            Id = id; Name = name ?? id; Category = category ?? ""; this.settings = settings.Clone();
        }
        /// <summary>A fresh copy of the preset's settings.</summary>
        public BrushSettings CreateSettings() { return settings.Clone(); }
    }

    /// <summary>The brushes that ship with the tool. Every tip and texture is generated here from deterministic noise, so no
    /// third-party artwork (and no extra licence) is involved. Generation is versioned by the ids: changing a generator
    /// changes how saved brushes that reference it look, so give a changed generator a new id instead.</summary>
    public static class BuiltInBrushes
    {
        static readonly object gate = new object();
        static Dictionary<string, BrushTip> tips;
        static List<BrushPreset> presets;

        public static IReadOnlyList<BrushPreset> Presets { get { Ensure(); return presets; } }
        public static IEnumerable<string> TipIds { get { Ensure(); return tips.Keys; } }

        /// <summary>A generated tip or texture by id ("grain", "noisy-disc", "charcoal", "bristles", "dots", "rim",
        /// "rounded-square"), or null when unknown.</summary>
        public static BrushTip Tip(string id) { Ensure(); return id != null && tips.TryGetValue(id, out var tip) ? tip : null; }
        public static BrushPreset Find(string id) { Ensure(); return presets.Find(p => p.Id == id); }

        static void Ensure()
        {
            lock (gate)
            {
                if (presets != null) return;
                var t = new Dictionary<string, BrushTip>
                {
                    { "grain", Grain(128, 7) },
                    { "noisy-disc", NoisyDisc(128, 11) },
                    { "charcoal", Charcoal(128, 80, 13) },
                    { "bristles", Bristles(32, 128, 17) },
                    { "dots", Dots(128, 19) },
                    { "rim", Rim(128, 23) },
                    { "rounded-square", RoundedSquare(96) },
                };
                tips = t;
                var p = new List<BrushPreset>();
                void Add(string id, string name, string category, Action<BrushSettings> configure)
                { var s = new BrushSettings(); configure(s); p.Add(new BrushPreset(id, name, category, s)); }
                Add("soft-round", "Soft Round", "Basic", s => { s.Radius = 24; s.Hardness = 0; s.Spacing = .1; });
                Add("hard-round", "Hard Round", "Basic", s => { s.Radius = 12; s.Hardness = .95; s.Spacing = .08; });
                Add("airbrush", "Airbrush", "Basic", s => { s.Radius = 40; s.Hardness = 0; s.Flow = .08; s.Spacing = .05; s.PressureFlow = true; s.PressureOpacity = false; s.PressureSize = false; });
                Add("pencil", "Pencil", "Drawing", s => { s.Radius = 3; s.Hardness = .6; s.Spacing = .1; s.PressureSize = false; s.Texture = t["grain"]; s.TextureDepth = .6; });
                Add("ink-pen", "Ink Pen", "Drawing", s => { s.Radius = 6; s.Hardness = 1; s.Roundness = .6; s.Angle = 35; s.Spacing = .05; s.PressureOpacity = false; });
                Add("marker", "Marker", "Drawing", s => { s.Radius = 14; s.Tip = t["rounded-square"]; s.Opacity = .7; s.Flow = .9; s.Spacing = .05; s.PressureSize = false; s.PressureOpacity = false; });
                Add("chalk", "Chalk", "Dry media", s => { s.Radius = 18; s.Tip = t["noisy-disc"]; s.SizeJitter = .2; s.AngleJitter = 1; s.Spacing = .2; s.Texture = t["grain"]; s.TextureDepth = .5; s.TextureScale = 2; });
                Add("charcoal", "Charcoal", "Dry media", s => { s.Radius = 16; s.Tip = t["charcoal"]; s.FollowDirection = true; s.AngleJitter = .1; s.Spacing = .1; s.Texture = t["grain"]; s.TextureDepth = .4; });
                Add("dry-brush", "Dry Brush", "Paint", s => { s.Radius = 20; s.Tip = t["bristles"]; s.FollowDirection = true; s.Spacing = .03; s.Flow = .6; s.OpacityJitter = .2; s.PressureOpacity = false; });
                Add("watercolor", "Watercolor Edge", "Paint", s => { s.Radius = 30; s.Tip = t["rim"]; s.Opacity = .6; s.Flow = .25; s.Spacing = .1; s.SizeJitter = .1; s.AngleJitter = 1; s.PressureOpacity = false; });
                Add("splatter", "Splatter", "Effects", s => { s.Radius = 20; s.Tip = t["dots"]; s.Scatter = 1.2; s.Count = 3; s.SizeJitter = .6; s.AngleJitter = 1; s.Spacing = .5; s.PressureSize = false; });
                Add("soft-eraser", "Soft Eraser", "Erasers", s => { s.Radius = 24; s.Hardness = 0; s.Spacing = .1; s.Erase = true; s.PressureOpacity = false; });
                Add("hard-eraser", "Hard Eraser", "Erasers", s => { s.Radius = 10; s.Hardness = .95; s.Spacing = .08; s.Erase = true; s.PressureOpacity = false; });
                presets = p;
            }
        }

        // ---- deterministic noise ----
        static double Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16;
                return (h & 0xFFFFFF) / (double)0x1000000;
            }
        }
        /// <summary>Value noise on a grid of the given period (tileable), smoothstep-interpolated, 0..1.</summary>
        static double ValueNoise(double x, double y, int period, int seed)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y); double fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            int Wrap(int v) { v %= period; return v < 0 ? v + period : v; }
            double a = Hash(Wrap(x0), Wrap(y0), seed), b = Hash(Wrap(x0 + 1), Wrap(y0), seed);
            double c = Hash(Wrap(x0), Wrap(y0 + 1), seed), d = Hash(Wrap(x0 + 1), Wrap(y0 + 1), seed);
            return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
        }
        /// <summary>Fractal value noise tileable over size pixels.</summary>
        static double Fbm(int px, int py, int size, int baseCells, int octaves, int seed)
        {
            double sum = 0, amplitude = 1, total = 0; int cells = baseCells;
            for (int o = 0; o < octaves; o++)
            {
                sum += amplitude * ValueNoise((px + 0.5) * cells / size, (py + 0.5) * cells / size, cells, seed + o * 101);
                total += amplitude; amplitude *= 0.5; cells *= 2;
            }
            return sum / total;
        }
        static byte B(double v) { return MathUtil.ToByte(MathUtil.Clamp01(v)); }
        static double Smooth(double e0, double e1, double v) { double t = MathUtil.Clamp01((v - e0) / (e1 - e0)); return t * t * (3 - 2 * t); }
        static BrushTip Make(string name, int w, int h, Func<int, int, double> f)
        {
            var a = new byte[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) a[y * w + x] = B(f(x, y));
            return new BrushTip(name, w, h, a);
        }
        static double Radial(int x, int y, int w, int h) { double u = (x + 0.5) / w * 2 - 1, v = (y + 0.5) / h * 2 - 1; return Math.Sqrt(u * u + v * v); }

        // ---- generators ----
        static BrushTip Grain(int size, int seed)
        { return Make("grain", size, size, (x, y) => Smooth(.25, .75, Fbm(x, y, size, 8, 4, seed))); }
        static BrushTip NoisyDisc(int size, int seed)
        { return Make("noisy-disc", size, size, (x, y) => (1 - Smooth(.8, 1, Radial(x, y, size, size))) * Smooth(.3, .6, Fbm(x, y, size, 6, 4, seed))); }
        static BrushTip Charcoal(int w, int h, int seed)
        { return Make("charcoal", w, h, (x, y) => (1 - Smooth(.7, 1, Radial(x, y, w, h))) * Smooth(.25, .65, Fbm(x, y, w, 10, 3, seed))); }
        static BrushTip Bristles(int w, int h, int seed)
        {
            // Bristle blobs across the tip's height (perpendicular to the stroke when it follows the direction).
            var a = new double[w * h]; var r = new Random(seed);
            for (int i = 0; i < 26; i++)
            {
                double cy = r.NextDouble() * h, ry = 1 + r.NextDouble() * 3.5, rx = w * (.25 + .2 * r.NextDouble()), cx = w / 2.0 + (r.NextDouble() - .5) * w * .3, strength = .45 + .55 * r.NextDouble();
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    double dx = (x + .5 - cx) / rx, dy = (y + .5 - cy) / ry, d = dx * dx + dy * dy;
                    if (d < 1) a[y * w + x] = Math.Max(a[y * w + x], strength * (1 - d));
                }
            }
            return Make("bristles", w, h, (x, y) => a[y * w + x]);
        }
        static BrushTip Dots(int size, int seed)
        {
            var a = new double[size * size]; var r = new Random(seed);
            for (int i = 0; i < 18; i++)
            {
                double angle = r.NextDouble() * Math.PI * 2, dist = Math.Sqrt(r.NextDouble()) * size * .4, radius = 3 + r.NextDouble() * 7;
                double cx = size / 2.0 + Math.Cos(angle) * dist, cy = size / 2.0 + Math.Sin(angle) * dist;
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                {
                    double d = Math.Sqrt((x + .5 - cx) * (x + .5 - cx) + (y + .5 - cy) * (y + .5 - cy)) / radius;
                    if (d < 1) a[y * size + x] = Math.Max(a[y * size + x], 1 - Smooth(.7, 1, d));
                }
            }
            return Make("dots", size, size, (x, y) => a[y * size + x]);
        }
        static BrushTip Rim(int size, int seed)
        {
            // A wash that is darker toward its edge, like a drying watercolour pool.
            return Make("rim", size, size, (x, y) =>
            {
                double d = Radial(x, y, size, size); if (d >= 1) return 0;
                double ring = .35 + .65 * Math.Exp(-Math.Pow((d - .86) / .07, 2));
                return ring * (1 - Smooth(.92, 1, d)) * (.85 + .15 * Fbm(x, y, size, 8, 2, seed));
            });
        }
        static BrushTip RoundedSquare(int size)
        {
            return Make("rounded-square", size, size, (x, y) =>
            {
                double u = Math.Abs((x + .5) / size * 2 - 1), v = Math.Abs((y + .5) / size * 2 - 1);
                return 1 - Smooth(.9, 1, Math.Pow(Math.Pow(u, 6) + Math.Pow(v, 6), 1 / 6.0));
            });
        }
    }
}
