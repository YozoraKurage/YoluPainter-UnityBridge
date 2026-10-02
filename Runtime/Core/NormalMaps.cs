using System;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Y direction of a tangent-space normal file. OpenGL (Y+, green = +V) is Unity's convention; DirectX (Y−) has
    /// the green channel inverted.</summary>
    public enum NormalYDirection { OpenGL = 0, DirectX = 1 }

    /// <summary>What the height derivative reads past the canvas edge: the edge texel (Clamp) or the opposite edge (Wrap, for
    /// textures that tile).</summary>
    public enum HeightEdgeMode { Clamp = 0, Wrap = 1 }

    /// <summary>Immutable document settings of the Normal channel's output (algorithm version <see cref="AlgorithmVersion"/>).
    /// The Normal channel always holds tangent-space normals in Unity's convention (OpenGL, Y+): R = +U (right), G = +V (up,
    /// the canvas is bottom-left origin), B = out of the surface, byte c means c / 255 × 2 − 1.</summary>
    public sealed class NormalSettings : IEquatable<NormalSettings>
    {
        public const int AlgorithmVersion = 1;
        public const double MaxStrength = 256;
        /// <summary>No derivation, strength 4, clamped edges, OpenGL files.</summary>
        public static readonly NormalSettings Default = new NormalSettings(false, 4, HeightEdgeMode.Clamp, NormalYDirection.OpenGL);

        /// <summary>Adds the normal derived from the Height channel under the painted Normal layers in the output.</summary>
        public bool DeriveFromHeight { get; private set; }
        /// <summary>Texels of rise for the full height range (0 → 1): the derived slope per texel is Strength × Δheight.
        /// Negative values turn bumps into dents.</summary>
        public double Strength { get; private set; }
        public HeightEdgeMode Edges { get; private set; }
        /// <summary>Y direction of the Normal images exported as files for other tools (Export Images / PNG / PSD). The .ylp
        /// composite that Unity imports and the preview always use Unity's OpenGL direction.</summary>
        public NormalYDirection FileDirection { get; private set; }

        public NormalSettings(bool deriveFromHeight, double strength, HeightEdgeMode edges, NormalYDirection fileDirection)
        {
            DeriveFromHeight = deriveFromHeight; Strength = strength; Edges = edges; FileDirection = fileDirection;
            Validate();
        }
        public NormalSettings WithDerive(bool value) { return new NormalSettings(value, Strength, Edges, FileDirection); }
        public NormalSettings WithStrength(double value) { return new NormalSettings(DeriveFromHeight, value, Edges, FileDirection); }
        public NormalSettings WithEdges(HeightEdgeMode value) { return new NormalSettings(DeriveFromHeight, Strength, value, FileDirection); }
        public NormalSettings WithFileDirection(NormalYDirection value) { return new NormalSettings(DeriveFromHeight, Strength, Edges, value); }

        void Validate()
        {
            MathUtil.RequireFinite(Strength, nameof(Strength));
            if (Strength < -MaxStrength || Strength > MaxStrength) throw new ArgumentOutOfRangeException(nameof(Strength), "Strength must be within ±" + MaxStrength + ".");
            if (!Enum.IsDefined(typeof(HeightEdgeMode), Edges)) throw new ArgumentOutOfRangeException(nameof(Edges));
            if (!Enum.IsDefined(typeof(NormalYDirection), FileDirection)) throw new ArgumentOutOfRangeException(nameof(FileDirection));
        }
        public bool Equals(NormalSettings other)
        {
            return other != null && DeriveFromHeight == other.DeriveFromHeight && Strength.Equals(other.Strength) && Edges == other.Edges && FileDirection == other.FileDirection;
        }
        public override bool Equals(object obj) { return Equals(obj as NormalSettings); }
        public override int GetHashCode() { unchecked { return (DeriveFromHeight ? 1 : 0) ^ Strength.GetHashCode() * 31 ^ (int)Edges * 7 ^ (int)FileDirection * 13; } }
    }

    /// <summary>Tangent-space normal arithmetic for the Normal channel: layer composition as unit vectors, and the Normal
    /// output (the derived Height → Normal under the painted Normal composite). The GPU (TileComposite.shader,
    /// NormalOutput.shader) uses the same formulas in the same order.
    /// <list type="bullet">
    /// <item>Bytes decode as c / 255 × 2 − 1 and every decoded vector is normalized before use. A vector shorter than 1e-6
    /// becomes flat (0, 0, 1).</item>
    /// <item>Layer composition keeps the W3C source-over structure of <see cref="CpuCompositor"/>, with vectors instead of
    /// colours: with t = source alpha × opacity × mask and da = backdrop alpha, the result is
    /// normalize((1 − t)·da·below + (1 − da)·t·over + da·t·B(below, over)) and alpha t + da(1 − t). Partial coverage is a
    /// renormalized weighted average (nlerp), never a byte lerp left unnormalized.</item>
    /// <item>B is Reoriented Normal Mapping (Barré-Brisebois and Hill 2012) for Overlay — the detail is rotated onto the
    /// surface below, the vector counterpart of Photoshop's overlay trick for detail normals — and the layer's own normal
    /// (replace) for Normal, PassThrough and every other mode, which have no vector meaning.</item>
    /// <item>Adjustment layers keep their encoded-space formulas in this channel (not vector-aware); the output renormalizes.</item>
    /// <item>The output flattens the composite onto a flat normal by its alpha (unpainted = flat (128, 128, 255)), takes the
    /// derived normal as the base and the painted normal as the detail of RNM, and is opaque.</item>
    /// <item>Height → Normal reads the Height composite as height = R × A (over 0, the same as an unpainted Height texel)
    /// and differentiates it with a Sobel 3×3 kernel divided by 8 (a ramp of slope s per texel gives exactly s) in texel
    /// units, Y up: n = normalize(−Strength·∂h/∂x, −Strength·∂h/∂y, 1). Mesh texel density (UV distortion), UV island
    /// borders and padding are not taken into account: the kernel reads whatever the neighbouring texels hold.</item>
    /// </list></summary>
    public static class NormalMaps
    {
        /// <summary>Default working-memory limit of <see cref="Output"/> (the output frame plus its row bands).</summary>
        public const long DefaultWorkingBudgetBytes = 256L * 1024 * 1024;
        /// <summary>Squared length below which a vector has no direction (it becomes flat).</summary>
        const double DegenerateLengthSquared = 1e-12;

        /// <summary>Layer blend modes with a vector meaning in the Normal channel. Others composite as Normal (replace).</summary>
        public static bool IsVectorMode(LayerBlendMode mode)
        { return mode == LayerBlendMode.Normal || mode == LayerBlendMode.PassThrough || mode == LayerBlendMode.Overlay; }
        /// <summary>Overlay combines the layer as detail (RNM) on what is below; every other mode replaces.</summary>
        public static bool IsDetail(LayerBlendMode mode) { return mode == LayerBlendMode.Overlay; }

        // ───────────── vectors ─────────────

        internal static void Normalize(ref double x, ref double y, ref double z)
        {
            double l2 = x * x + y * y + z * z;
            if (l2 < DegenerateLengthSquared) { x = 0; y = 0; z = 1; return; }
            double l = Math.Sqrt(l2); x /= l; y /= l; z /= l;
        }
        /// <summary>Unit vector of an encoded pixel (alpha is ignored).</summary>
        public static void Decode(Rgba32 c, out double x, out double y, out double z)
        {
            x = c.R / 255.0 * 2 - 1; y = c.G / 255.0 * 2 - 1; z = c.B / 255.0 * 2 - 1;
            Normalize(ref x, ref y, ref z);
        }
        /// <summary>Normalizes and encodes a vector (round half up, as <see cref="MathUtil.ToByte"/>).</summary>
        public static Rgba32 Encode(double x, double y, double z, byte alpha)
        {
            Normalize(ref x, ref y, ref z);
            return new Rgba32(MathUtil.ToByte(x * .5 + .5), MathUtil.ToByte(y * .5 + .5), MathUtil.ToByte(z * .5 + .5), alpha);
        }
        /// <summary>Reoriented Normal Mapping: the detail d rotated onto the base b (both unit, tangent space). Identity for a
        /// flat detail or a flat base. A base pointing straight inward (z = −1) has no frame; the result is then the base.</summary>
        public static void Rnm(double bx, double by, double bz, double dx, double dy, double dz, out double x, out double y, out double z)
        {
            double tx = bx, ty = by, tz = bz + 1, ux = -dx, uy = -dy, uz = dz;
            if (tz <= 1e-6) { x = bx; y = by; z = bz; return; }
            double k = (tx * ux + ty * uy + tz * uz) / tz;
            x = tx * k - ux; y = ty * k - uy; z = tz * k - uz;
        }
        static void Combine(LayerBlendMode mode, double bx, double by, double bz, double sx, double sy, double sz, out double x, out double y, out double z)
        {
            if (IsDetail(mode)) Rnm(bx, by, bz, sx, sy, sz, out x, out y, out z);
            else { x = sx; y = sy; z = sz; }
        }

        // ───────────── layer composition (the Normal channel's counterparts of CpuCompositor's) ─────────────

        /// <summary>Source-over of one layer pixel onto the backdrop as unit vectors (see the class summary).</summary>
        public static Rgba32 Blend(Rgba32 below, Rgba32 over, double opacity = 1, LayerBlendMode mode = LayerBlendMode.Normal)
        {
            MathUtil.RequireFinite(opacity, nameof(opacity));
            if (opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
            if (!Enum.IsDefined(typeof(LayerBlendMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            return BlendUnchecked(below, over, opacity, mode);
        }
        internal static Rgba32 BlendUnchecked(Rgba32 below, Rgba32 over, double opacity, LayerBlendMode mode)
        {
            double t = over.A / 255.0 * opacity, da = below.A / 255.0;
            if (t <= 0) return below; // 透明な画素は下（その RGB も）をそのまま返す
            Decode(below, out double bx, out double by, out double bz); Decode(over, out double sx, out double sy, out double sz);
            Combine(mode, bx, by, bz, sx, sy, sz, out double cx, out double cy, out double cz);
            double wb = (1 - t) * da, ws = (1 - da) * t, wc = da * t;
            return Encode(wb * bx + ws * sx + wc * cx, wb * by + ws * sy + wc * cy, wb * bz + ws * sz + wc * cz, MathUtil.ToByte(t + da * (1 - t)));
        }
        /// <summary>A clipped layer onto its clipping group: normalize((1 − t)·group + t·B(group, clipped)), t = clipped alpha ×
        /// amount. The group keeps its alpha.</summary>
        public static Rgba32 ClipOnto(Rgba32 group, Rgba32 clipped, double amount, LayerBlendMode mode)
        {
            double t = clipped.A / 255.0 * amount;
            if (t <= 0 || group.A == 0) return group;
            Decode(group, out double gx, out double gy, out double gz); Decode(clipped, out double sx, out double sy, out double sz);
            Combine(mode, gx, gy, gz, sx, sy, sz, out double cx, out double cy, out double cz);
            return Encode((1 - t) * gx + t * cx, (1 - t) * gy + t * cy, (1 - t) * gz + t * cz, group.A);
        }
        /// <summary>Pass-through group fade: alpha-weighted average of the backdrop and the group's result, renormalized.</summary>
        internal static Rgba32 Fade(Rgba32 backdrop, Rgba32 inner, double amount)
        {
            if (amount >= 1) return inner;
            if (amount <= 0) return backdrop;
            double ba = backdrop.A / 255.0 * (1 - amount), ia = inner.A / 255.0 * amount, a = ba + ia;
            if (a <= 0) return Rgba32.Transparent;
            Decode(backdrop, out double bx, out double by, out double bz); Decode(inner, out double ix, out double iy, out double iz);
            return Encode(ba * bx + ia * ix, ba * by + ia * iy, ba * bz + ia * iz, MathUtil.ToByte(a));
        }

        // ───────────── output ─────────────

        /// <summary>The painted normal of a composite pixel on a flat surface: normalize(a·n + (1 − a)·(0, 0, 1)).</summary>
        static void Flatten(byte r, byte g, byte b, byte alpha, out double x, out double y, out double z)
        {
            if (alpha == 0) { x = 0; y = 0; z = 1; return; }
            Decode(new Rgba32(r, g, b), out x, out y, out z);
            double a = alpha / 255.0;
            x *= a; y *= a; z = z * a + (1 - a);
            Normalize(ref x, ref y, ref z);
        }

        /// <summary>True when the document has a Normal output without any Normal layer: Height → Normal is on and a layer
        /// uses the Height channel.</summary>
        public static bool DerivesNormal(PaintDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (!document.NormalSettings.DeriveFromHeight) return false;
            foreach (var layer in document.Layers) if (layer.IsChannelEnabled(PaintChannel.Height)) return true;
            return false;
        }

        /// <summary>Payload bytes <see cref="Output"/> allocates: the output frame, one band of Normal composite rows and, with
        /// Height → Normal, one band of Height rows with a row of halo on each side (bytes and heights). Managed overhead, the
        /// compositor's per-layer tile buffers and the single edge rows the edge rule reads (at most 2 × 4 × width bytes per
        /// band) are additional.</summary>
        public static long WorkingBytes(PaintDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            long w = document.Width, band = Math.Min(document.TileSize, document.Height);
            long bytes = 4 * w * document.Height + 4 * w * band;
            if (document.NormalSettings.DeriveFromHeight) bytes += (4 + 8) * w * (band + 2);
            return bytes;
        }

        /// <summary>The Normal output for Unity (OpenGL Y+, bottom-left rows, opaque): the painted Normal composite flattened
        /// onto flat, with the normal derived from Height as its base when that is on. This is what the .ylp composite,
        /// the preview and file exports (<see cref="FileOutput"/>) are made of. Refuses (before allocating) when
        /// <see cref="WorkingBytes"/> exceeds maxWorkingBytes.</summary>
        public static byte[] Output(PaintDocument document, long maxWorkingBytes = DefaultWorkingBudgetBytes)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            long needed = WorkingBytes(document);
            if (needed > maxWorkingBytes)
                throw new InvalidOperationException("The Normal output needs " + (needed >> 20) + " MiB of working memory, above its " + (maxWorkingBytes >> 20) + " MiB budget. Nothing was computed.");
            return Evaluate(document, document.NormalSettings, true, document.NormalSettings.DeriveFromHeight);
        }

        /// <summary><see cref="Output"/> in the document's <see cref="NormalSettings.FileDirection"/>: for files meant for other
        /// tools. DirectX inverts green (y → −y, exactly 255 − G).</summary>
        public static byte[] FileOutput(PaintDocument document, long maxWorkingBytes = DefaultWorkingBudgetBytes)
        {
            var bytes = Output(document, maxWorkingBytes);
            if (document.NormalSettings.FileDirection == NormalYDirection.DirectX) FlipGreen(bytes);
            return bytes;
        }
        /// <summary>Inverts the green byte of every RGBA pixel in place (OpenGL ⇔ DirectX).</summary>
        public static void FlipGreen(byte[] rgba)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            for (int i = 1; i < rgba.Length; i += 4) rgba[i] = (byte)(255 - rgba[i]);
        }

        /// <summary>The normal derived from a height channel alone (OpenGL Y+, opaque), with the given settings' strength and
        /// edges whether or not the document has derivation switched on. Only the Height channel is a height: any other source
        /// is refused (Roughness and Metallic are scalars too, but not heights).</summary>
        public static byte[] DeriveFromHeight(PaintDocument document, PaintChannel source, NormalSettings settings, long maxWorkingBytes = DefaultWorkingBudgetBytes)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            PaintLayer.ValidateChannel(source);
            if (source != PaintChannel.Height) throw new InvalidOperationException("Height → Normal reads a height map; the " + source + " channel is not one. Only the Height channel can be derived into a normal.");
            long needed = 4L * document.Width * document.Height + (4 + 8) * (long)document.Width * (Math.Min(document.TileSize, document.Height) + 2);
            if (needed > maxWorkingBytes)
                throw new InvalidOperationException("Height → Normal needs " + (needed >> 20) + " MiB of working memory, above its " + (maxWorkingBytes >> 20) + " MiB budget. Nothing was computed.");
            return Evaluate(document, settings, false, true);
        }

        /// <summary>The output computed from full-frame composites (bottom-left rows, straight RGBA): what the GPU output pass
        /// does with the two composite textures. heightComposite may be null when settings do not derive.</summary>
        public static byte[] OutputFromComposites(byte[] normalComposite, byte[] heightComposite, int width, int height, NormalSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException("size");
            long length = 4L * width * height;
            if (normalComposite == null || normalComposite.LongLength != length) throw new ArgumentException("Normal composite must be width × height × 4 bytes.", nameof(normalComposite));
            bool derive = settings.DeriveFromHeight;
            if (derive && (heightComposite == null || heightComposite.LongLength != length)) throw new ArgumentException("Height composite must be width × height × 4 bytes.", nameof(heightComposite));
            var output = new byte[length];
            var heights = derive ? new double[3 * width] : null;
            for (int y = 0; y < height; y++)
            {
                if (derive)
                    for (int r = 0; r < 3; r++)
                    {
                        int row = EdgeRow(y + r - 1, height, settings.Edges);
                        for (int x = 0; x < width; x++) { int i = (row * width + x) * 4; heights[r * width + x] = HeightOf(heightComposite[i], heightComposite[i + 3]); }
                    }
                Row(normalComposite, y * width * 4, heights, 0, width, settings, derive, true, output, y * width * 4);
            }
            return output;
        }

        static int EdgeRow(int row, int height, HeightEdgeMode edges)
        {
            if (row < 0) return edges == HeightEdgeMode.Wrap ? height - 1 : 0;
            if (row >= height) return edges == HeightEdgeMode.Wrap ? 0 : height - 1;
            return row;
        }
        /// <summary>Height of a composite texel: R × A over 0 (an unpainted texel is 0).</summary>
        static double HeightOf(byte r, byte a) { return r * a / 65025.0; }

        /// <summary>Band-wise evaluation: Normal and Height composites one band of rows at a time (Height with a row of halo
        /// on each side), so nothing full-frame is held except the output.</summary>
        static byte[] Evaluate(PaintDocument document, NormalSettings settings, bool painted, bool derive)
        {
            int w = document.Width, h = document.Height, band = Math.Min(document.TileSize, h);
            var output = new byte[checked(w * h * 4)];
            var heights = derive ? new double[w * (band + 2)] : null;
            for (int y0 = 0; y0 < h; y0 += band)
            {
                int rows = Math.Min(band, h - y0);
                byte[] normal = painted ? CpuCompositor.CompositeRegion(document, PaintChannel.Normal, 0, y0, w, rows) : null;
                if (derive) LoadHeights(document, settings.Edges, y0, rows, heights);
                for (int r = 0; r < rows; r++)
                    Row(normal, r * w * 4, heights, r * w, w, settings, derive, painted, output, (y0 + r) * w * 4);
            }
            return output;
        }
        /// <summary>heights[(r + 1) × w + x] = height of row y0 + r for r = −1 … rows (edge rows by the edge rule).</summary>
        static void LoadHeights(PaintDocument document, HeightEdgeMode edges, int y0, int rows, double[] heights)
        {
            int w = document.Width, h = document.Height, a = Math.Max(0, y0 - 1), b = Math.Min(h, y0 + rows + 1);
            var inner = CpuCompositor.CompositeRegion(document, PaintChannel.Height, 0, a, w, b - a);
            for (int r = -1; r <= rows; r++)
            {
                int row = EdgeRow(y0 + r, h, edges);
                byte[] source = inner; int start = (row - a) * w * 4;
                if (row < a || row >= b) { source = CpuCompositor.CompositeRegion(document, PaintChannel.Height, 0, row, w, 1); start = 0; }
                for (int x = 0; x < w; x++) heights[(r + 1) * w + x] = HeightOf(source[start + x * 4], source[start + x * 4 + 3]);
            }
        }
        /// <summary>One output row. heights holds the rows below, at and above it at heightStart, heightStart + w and
        /// heightStart + 2w.</summary>
        static void Row(byte[] normal, int normalStart, double[] heights, int heightStart, int w, NormalSettings settings, bool derive, bool painted, byte[] output, int outputStart)
        {
            bool wrap = settings.Edges == HeightEdgeMode.Wrap; double s = settings.Strength;
            for (int x = 0; x < w; x++)
            {
                double px = 0, py = 0, pz = 1;
                if (painted) { int i = normalStart + x * 4; Flatten(normal[i], normal[i + 1], normal[i + 2], normal[i + 3], out px, out py, out pz); }
                double nx = px, ny = py, nz = pz;
                if (derive)
                {
                    int xl = x > 0 ? x - 1 : wrap ? w - 1 : 0, xr = x < w - 1 ? x + 1 : wrap ? 0 : w - 1;
                    int below = heightStart, center = heightStart + w, above = heightStart + 2 * w;
                    double gx = (heights[above + xr] + 2 * heights[center + xr] + heights[below + xr] - heights[above + xl] - 2 * heights[center + xl] - heights[below + xl]) / 8;
                    double gy = (heights[above + xl] + 2 * heights[above + x] + heights[above + xr] - heights[below + xl] - 2 * heights[below + x] - heights[below + xr]) / 8;
                    double hx = -s * gx, hy = -s * gy, hz = 1;
                    Normalize(ref hx, ref hy, ref hz);
                    Rnm(hx, hy, hz, px, py, pz, out nx, out ny, out nz);
                }
                var c = Encode(nx, ny, nz, 255);
                int o = outputStart + x * 4;
                output[o] = c.R; output[o + 1] = c.G; output[o + 2] = c.B; output[o + 3] = 255;
            }
        }
    }
}
