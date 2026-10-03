using System;
using System.Collections.Generic;
using System.Linq;

namespace Yozolab.YoluPainter.Core.Shelf
{
    /// <summary>
    /// YoluPainter's own images ("Built-in" in the Assets panel): made by code, so nothing is shipped as a file and the same key
    /// and version always make the same pixels (integer arithmetic only; the tests pin their content hashes). A version is raised
    /// when an image's pixels change, so a project that took the older copy can tell (its origin records the version).
    /// </summary>
    public static class BuiltInImages
    {
        public sealed class Entry
        {
            public string Key { get; }
            public int Version { get; }
            /// <summary>The English name (the UI translates it).</summary>
            public string Name { get; }
            public int Width { get; }
            public int Height { get; }
            public ResourceColorSpace ColorSpace { get; }
            internal readonly Func<int, int, byte[]> Make;
            internal Entry(string key, int version, string name, int size, ResourceColorSpace colorSpace, Func<int, int, byte[]> make)
            { Key = key; Version = version; Name = name; Width = Height = size; ColorSpace = colorSpace; Make = make; }
        }

        const int Size = 1024;
        public static readonly IReadOnlyList<Entry> All = new[]
        {
            new Entry("uv-checker", 1, "UV checker", Size, ResourceColorSpace.Srgb, Checker),
            new Entry("grid", 1, "Grid lines", Size, ResourceColorSpace.Srgb, Grid),
            new Entry("linear-gradient", 1, "Linear gradient", Size, ResourceColorSpace.Linear, Linear),
            new Entry("radial-gradient", 1, "Radial gradient", Size, ResourceColorSpace.Linear, Radial),
            new Entry("value-noise", 1, "Value noise", Size, ResourceColorSpace.Linear, Noise),
        };

        public static bool IsKey(string key) => key != null && key.Length >= 1 && key.Length <= 64 && key.All(c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-');
        public static bool TryGet(string key, out Entry entry) { entry = All.FirstOrDefault(e => e.Key == key); return entry != null; }

        /// <summary>The pixels of a built-in image (made each time; callers keep the content).</summary>
        public static ImageContent Make(string key)
        {
            if (!TryGet(key, out var entry)) throw new ResourceRefusedException(ResourceRefusal.Unknown, "No built-in image \"" + key + "\".");
            return ImageContent.Adopt(entry.Make(entry.Width, entry.Height), entry.Width, entry.Height);
        }

        static byte[] Image(int w, int h, Func<int, int, Rgba32> pixel)
        {
            var rgba = new byte[w * h * 4];
            CoreParallelism.For(h, CoreParallelism.Degree, y =>
            {
                for (int x = 0; x < w; x++) { var c = pixel(x, y); int o = (y * w + x) * 4; rgba[o] = c.R; rgba[o + 1] = c.G; rgba[o + 2] = c.B; rgba[o + 3] = c.A; }
            });
            return rgba;
        }

        /// <summary>8 × 8 cells: the column picks a hue, the rows alternate light and dark, and the bottom-left cell is white so the
        /// orientation shows. Thin dark lines between cells.</summary>
        static byte[] Checker(int w, int h)
        {
            var hues = new[] { new Rgba32(230, 80, 80), new Rgba32(230, 150, 60), new Rgba32(220, 210, 70), new Rgba32(110, 200, 90), new Rgba32(70, 190, 190), new Rgba32(80, 130, 230), new Rgba32(150, 100, 220), new Rgba32(220, 100, 180) };
            return Image(w, h, (x, y) =>
            {
                int cell = w / 8, cx = x / cell, cy = y / cell;
                if (x % cell == 0 || y % cell == 0) return new Rgba32(30, 30, 30);
                if (cx == 0 && cy == 0) return new Rgba32(255, 255, 255);
                var c = hues[cx % 8];
                return (cx + cy) % 2 == 0 ? c : new Rgba32((byte)(c.R / 2), (byte)(c.G / 2), (byte)(c.B / 2));
            });
        }

        /// <summary>White lines 2 pixels wide every 64 pixels on transparent (the RGB under zero alpha is white too, so filtering does
        /// not darken the lines).</summary>
        static byte[] Grid(int w, int h) => Image(w, h, (x, y) => x % 64 < 2 || y % 64 < 2 ? new Rgba32(255, 255, 255, 255) : new Rgba32(255, 255, 255, 0));

        /// <summary>Black on the left to white on the right.</summary>
        static byte[] Linear(int w, int h) => Image(w, h, (x, y) => { byte v = (byte)((x * 255 + (w - 1) / 2) / (w - 1)); return new Rgba32(v, v, v); });

        /// <summary>White in the centre to black at the inscribed circle and beyond (the distance by integer square root).</summary>
        static byte[] Radial(int w, int h) => Image(w, h, (x, y) =>
        {
            long dx = 2 * x + 1 - w, dy = 2 * y + 1 - h, radius = Math.Min(w, h); // in half pixels
            long d = ISqrt(dx * dx + dy * dy);
            byte v = d >= radius ? (byte)0 : (byte)(255 - (d * 255 + radius / 2) / radius);
            return new Rgba32(v, v, v);
        });

        /// <summary>Four octaves of value noise on a lattice of 8, 16, 32 and 64 cells (smoothstep between hashed corners), tiling.</summary>
        static byte[] Noise(int w, int h) => Image(w, h, (x, y) =>
        {
            long sum = 0, weight = 0;
            for (int octave = 0, cells = 8, amplitude = 8; octave < 4; octave++, cells *= 2, amplitude /= 2)
            {
                long fx = (long)x * cells * 256 / w, fy = (long)y * cells * 256 / h; // 8-bit fraction
                int x0 = (int)(fx >> 8), y0 = (int)(fy >> 8); long tx = Smooth(fx & 255), ty = Smooth(fy & 255);
                long a = Lattice(x0 % cells, y0 % cells, octave), b = Lattice((x0 + 1) % cells, y0 % cells, octave);
                long c = Lattice(x0 % cells, (y0 + 1) % cells, octave), d = Lattice((x0 + 1) % cells, (y0 + 1) % cells, octave);
                long top = a * (65536 - tx) + b * tx, bottom = c * (65536 - tx) + d * tx; // × 65536
                long value = (top * (65536 - ty) + bottom * ty) >> 32;          // 0..255
                sum += value * amplitude; weight += amplitude;
            }
            byte v = (byte)((sum + weight / 2) / weight);
            return new Rgba32(v, v, v);
        });

        /// <summary>3t² − 2t³ of an 8-bit fraction, as a 16-bit fraction.</summary>
        static long Smooth(long t) => (3 * t * t * 256 - 2 * t * t * t) * 65536 / (256L * 256 * 256);
        static long Lattice(int x, int y, int octave)
        {
            uint n = (uint)(x * 374761393 + y * 668265263 + octave * 1442695041);
            n = (n ^ (n >> 13)) * 1274126177u; n ^= n >> 16;
            return n & 255;
        }
        static long ISqrt(long v)
        {
            if (v <= 0) return 0;
            long r = (long)Math.Sqrt(v);
            while (r * r > v) r--;
            while ((r + 1) * (r + 1) <= v) r++;
            return r;
        }
    }
}
