using System;
using System.Threading.Tasks;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Select &gt; Grow / Shrink / Border / Feather / Sharpen, after GIMP's operations (app/operations/gimpoperationgrow.c,
    /// gimpoperationshrink.c, app/gegl/gimp-gegl-apply-operation.c). Each returns a new selection; this one is not changed.
    /// Grow and Shrink are grey-scale morphology (the largest / smallest amount within a circle of the radius), so soft edges
    /// stay soft. Work is limited to the bounding box of the selected tiles plus the radius, and rows (or columns) run in
    /// parallel; every row is computed independently, so the result does not depend on the scheduling.</summary>
    public sealed partial class SelectionMask
    {
        /// <summary>The largest radius Grow / Shrink / Border / Feather accept (their cost grows with the radius).</summary>
        public const int MaxModifyRadius = 200;

        /// <summary>Every pixel takes the largest amount within a circle of the radius (GIMP's Grow). Outside the canvas
        /// counts as unselected.</summary>
        public SelectionMask Grow(int radius)
        {
            CheckRadius(radius);
            if (radius == 0 || IsEmpty) return this;
            var r = Window(radius); if (r == null) return this;
            var w = r.Value;
            return FromDense(Morphology(Padded(w, radius, false), w.width, w.height, radius, true), w);
        }

        /// <summary>Every pixel takes the smallest amount within a circle of the radius (GIMP's Shrink). With edgeLock the
        /// selection continues outside the canvas (it does not shrink away from the canvas edges).</summary>
        public SelectionMask Shrink(int radius, bool edgeLock = false)
        {
            CheckRadius(radius);
            if (radius == 0 || IsEmpty) return this;
            var r = Window(0); if (r == null) return this;
            var w = r.Value;
            return FromDense(Morphology(Padded(w, radius, edgeLock), w.width, w.height, radius, false), w);
        }

        /// <summary>A band around the selection's edge: Grow(radius) minus Shrink(radius + 1), as GIMP's "smooth" border style
        /// (radius 1 keeps the selection minus Shrink(1), GIMP's special case).</summary>
        public SelectionMask Border(int radius, bool edgeLock = false)
        {
            CheckRadius(radius);
            if (radius == 0 || IsEmpty) return None(Width, Height, TileSize);
            var r = Window(radius); if (r == null) return this;
            var w = r.Value;
            byte[] grown = radius == 1 ? Dense(w) : Morphology(Padded(w, radius, false), w.width, w.height, radius, true);
            int inner = radius == 1 ? 1 : radius + 1;
            byte[] shrunk = Morphology(Padded(w, inner, edgeLock), w.width, w.height, inner, false);
            for (int i = 0; i < grown.Length; i++) grown[i] = (byte)Math.Max(0, grown[i] - shrunk[i]);
            return FromDense(grown, w);
        }

        /// <summary>Gaussian blur of the amounts with a standard deviation of radius / 3.5 (GIMP's Feather and its constant).
        /// Outside the canvas is unselected, or with edgeLock continues the edge pixels. Small radii use the exact kernel;
        /// larger ones three box blurs, which approximate the Gaussian (GIMP's GEGL uses another approximation).</summary>
        public SelectionMask Feather(double radius, bool edgeLock = false)
        {
            MathUtil.RequireFinite(radius, nameof(radius));
            if (radius < 0 || radius > MaxModifyRadius) throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be 0.." + MaxModifyRadius + ".");
            double sigma = radius / 3.5;
            if (sigma < .05 || IsEmpty) return this;
            int[] boxes = sigma < 2 ? null : BoxesForGauss(sigma, 3);
            double[] kernel = boxes == null ? GaussianKernel(sigma) : null;
            int reach = boxes == null ? kernel.Length / 2 : (boxes[0] + boxes[1] + boxes[2] - 3) / 2;
            var r = Window(reach + 1); if (r == null) return this;
            var w = r.Value;
            int pad = reach, pw = w.width + 2 * pad, ph = w.height + 2 * pad;
            var padded = Padded(w, pad, edgeLock, replicate: true);
            var a = new float[pw * ph]; for (int i = 0; i < a.Length; i++) a[i] = padded[i];
            var b = new float[pw * ph];
            if (kernel != null) { Convolve(a, b, pw, ph, kernel, true); Convolve(b, a, pw, ph, kernel, false); }
            else foreach (int size in boxes) { Box(a, b, pw, ph, size / 2, true); Box(b, a, pw, ph, size / 2, false); }
            var result = new byte[w.width * w.height];
            for (int y = 0; y < w.height; y++) for (int x = 0; x < w.width; x++)
                result[y * w.width + x] = (byte)Math.Max(0, Math.Min(255, Math.Floor(a[(y + pad) * pw + x + pad] + .5)));
            return FromDense(result, w);
        }

        /// <summary>Hard edges: amounts of at least half become fully selected, the rest unselected (GIMP's Sharpen).</summary>
        public SelectionMask Sharpen() { return FromTiles(Width, Height, TileSize, this, null, (a, _) => a >= 128 ? (byte)255 : (byte)0, false); }

        static void CheckRadius(int radius)
        { if (radius < 0 || radius > MaxModifyRadius) throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be 0.." + MaxModifyRadius + "."); }

        static SelectionMask None(int width, int height, int tileSize) { return new SelectionMask(width, height, tileSize); }

        struct Rect { public int x, y, width, height; }

        /// <summary>The bounding box of the selected tiles grown by margin, clipped to the canvas; null when empty.</summary>
        Rect? Window(int margin)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, t = TileSize;
            foreach (var c in Tiles) { x0 = Math.Min(x0, c.X * t); y0 = Math.Min(y0, c.Y * t); x1 = Math.Max(x1, Math.Min(Width, (c.X + 1) * t)); y1 = Math.Max(y1, Math.Min(Height, (c.Y + 1) * t)); }
            if (x0 == int.MaxValue) return null;
            x0 = Math.Max(0, x0 - margin); y0 = Math.Max(0, y0 - margin); x1 = Math.Min(Width, x1 + margin); y1 = Math.Min(Height, y1 + margin);
            return new Rect { x = x0, y = y0, width = x1 - x0, height = y1 - y0 };
        }

        /// <summary>The amounts inside the window, row-major from its bottom row.</summary>
        byte[] Dense(Rect w)
        {
            var dense = new byte[w.width * w.height]; int t = TileSize; var amounts = new byte[t * t];
            foreach (var c in Tiles)
            {
                int tx0 = Math.Max(w.x, c.X * t), tx1 = Math.Min(w.x + w.width, (c.X + 1) * t), ty0 = Math.Max(w.y, c.Y * t), ty1 = Math.Min(w.y + w.height, (c.Y + 1) * t);
                if (tx0 >= tx1 || ty0 >= ty1) continue;
                CopyTile(c, amounts);
                for (int y = ty0; y < ty1; y++) for (int x = tx0; x < tx1; x++) dense[(y - w.y) * w.width + x - w.x] = amounts[(y - c.Y * t) * t + x - c.X * t];
            }
            return dense;
        }

        /// <summary>The window with pad pixels around it. Pixels of the canvas outside the window are unselected (the window
        /// holds every selected pixel); outside the canvas they are 255 with edgeLock (or the nearest edge pixel with
        /// replicate), else 0.</summary>
        byte[] Padded(Rect w, int pad, bool edgeLock, bool replicate = false)
        {
            var dense = Dense(w); int pw = w.width + 2 * pad, ph = w.height + 2 * pad;
            var p = new byte[pw * ph];
            Parallel.For(0, ph, py =>
            {
                int cy = w.y + py - pad; bool outY = cy < 0 || cy >= Height;
                for (int px = 0; px < pw; px++)
                {
                    int cx = w.x + px - pad; byte v;
                    if (!outY && cx >= 0 && cx < Width)
                    {
                        int dx = cx - w.x, dy = cy - w.y;
                        v = dx >= 0 && dy >= 0 && dx < w.width && dy < w.height ? dense[dy * w.width + dx] : (byte)0;
                    }
                    else if (!edgeLock) v = 0;
                    else if (!replicate) v = 255;
                    else
                    {
                        int ex = Math.Max(0, Math.Min(Width - 1, cx)), ey = Math.Max(0, Math.Min(Height - 1, cy));
                        int dx = ex - w.x, dy = ey - w.y;
                        v = dx >= 0 && dy >= 0 && dx < w.width && dy < w.height ? dense[dy * w.width + dx] : (byte)0;
                    }
                    p[py * pw + px] = v;
                }
            });
            return p;
        }

        /// <summary>Grey-scale dilation (max) or erosion (min) by GIMP's circle: for each output pixel, over horizontal offsets
        /// dx the extreme of the column at dx across ±circle[dx] rows. Input is padded by radius on each side.</summary>
        static byte[] Morphology(byte[] padded, int width, int height, int radius, bool max)
        {
            int pw = width + 2 * radius;
            var circle = new int[2 * radius + 1];
            for (int i = 0; i <= 2 * radius; i++)
            {
                double t = i == radius ? 0 : Math.Abs(i - radius) - .5;
                circle[i] = (int)Math.Round(Math.Sqrt(radius * (double)radius - t * t), MidpointRounding.ToEven);
            }
            var output = new byte[width * height];
            Parallel.For(0, height, () => new byte[(radius + 1) * pw], (y, _, columns) =>
            {
                int centre = (y + radius) * pw;
                Buffer.BlockCopy(padded, centre, columns, 0, pw);
                for (int j = 1; j <= radius; j++)
                {
                    int above = centre + j * pw, below = centre - j * pw, row = j * pw, previous = row - pw;
                    for (int x = 0; x < pw; x++)
                    {
                        byte a = padded[above + x], b = padded[below + x], c = columns[previous + x];
                        columns[row + x] = max ? Math.Max(c, Math.Max(a, b)) : Math.Min(c, Math.Min(a, b));
                    }
                }
                for (int x = 0; x < width; x++)
                {
                    byte v = max ? (byte)0 : (byte)255;
                    for (int i = 0; i <= 2 * radius; i++)
                    {
                        byte c = columns[circle[i] * pw + x + i];
                        if (max ? c > v : c < v) { v = c; if (max ? v == 255 : v == 0) break; }
                    }
                    output[y * width + x] = v;
                }
                return columns;
            }, _ => { });
            return output;
        }

        static double[] GaussianKernel(double sigma)
        {
            int reach = Math.Max(1, (int)Math.Ceiling(3 * sigma));
            var k = new double[2 * reach + 1]; double sum = 0;
            for (int i = -reach; i <= reach; i++) sum += k[i + reach] = Math.Exp(-i * i / (2 * sigma * sigma));
            for (int i = 0; i < k.Length; i++) k[i] /= sum;
            return k;
        }

        static void Convolve(float[] src, float[] dst, int w, int h, double[] kernel, bool horizontal)
        {
            int reach = kernel.Length / 2;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                double s = 0;
                for (int i = -reach; i <= reach; i++)
                {
                    int xx = horizontal ? Math.Max(0, Math.Min(w - 1, x + i)) : x, yy = horizontal ? y : Math.Max(0, Math.Min(h - 1, y + i));
                    s += src[yy * w + xx] * kernel[i + reach];
                }
                dst[y * w + x] = (float)s;
                }
            });
        }

        /// <summary>Box widths whose three passes approximate a Gaussian of sigma (W. Jarosz / P. Kovesi).</summary>
        static int[] BoxesForGauss(double sigma, int n)
        {
            double ideal = Math.Sqrt(12 * sigma * sigma / n + 1);
            int wl = (int)Math.Floor(ideal); if (wl % 2 == 0) wl--;
            int wu = wl + 2;
            double mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4);
            int m = (int)Math.Round(mIdeal);
            var sizes = new int[n]; for (int i = 0; i < n; i++) sizes[i] = i < m ? wl : wu;
            return sizes;
        }

        /// <summary>Running-sum box blur of half-width r along rows or columns (the window is clamped at the array's edge,
        /// which only affects the padding).</summary>
        static void Box(float[] src, float[] dst, int w, int h, int r, bool horizontal)
        {
            int lines = horizontal ? h : w, length = horizontal ? w : h, step = horizontal ? 1 : w;
            double scale = 1.0 / (2 * r + 1);
            Parallel.For(0, lines, line =>
            {
                int start = horizontal ? line * w : line;
                double sum = 0;
                for (int i = -r; i <= r; i++) sum += src[start + Math.Max(0, Math.Min(length - 1, i)) * step];
                for (int i = 0; i < length; i++)
                {
                    dst[start + i * step] = (float)(sum * scale);
                    int add = Math.Min(length - 1, i + r + 1), remove = Math.Max(0, i - r);
                    sum += src[start + add * step] - src[start + remove * step];
                }
            });
        }

        SelectionMask FromDense(byte[] dense, Rect w)
        {
            var mask = new SelectionMask(Width, Height, TileSize); int t = TileSize; var rgba = new byte[t * t * 4];
            for (int ty = w.y / t; ty <= (w.y + w.height - 1) / t; ty++)
                for (int tx = w.x / t; tx <= (w.x + w.width - 1) / t; tx++)
                {
                    Array.Clear(rgba, 0, rgba.Length); bool any = false;
                    int x0 = Math.Max(w.x, tx * t), x1 = Math.Min(w.x + w.width, (tx + 1) * t), y0 = Math.Max(w.y, ty * t), y1 = Math.Min(w.y + w.height, (ty + 1) * t);
                    for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                    {
                        byte a = dense[(y - w.y) * w.width + x - w.x]; if (a == 0) continue;
                        rgba[((y - ty * t) * t + x - tx * t) * 4 + 3] = a; any = true;
                    }
                    if (any) mask.surface.ImportTile(new TileCoord(tx, ty), rgba);
                }
            return mask;
        }
    }
}
