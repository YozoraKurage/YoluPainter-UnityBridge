using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>
    /// An image's mipmap as a fill channel reads it: level 0 is the resource's own pixels (read through the channel's conversion and
    /// luminance, <see cref="FillImageColor"/>), every further level halves the one before (⌊n / 2⌋, at least 1) and is stored converted.
    /// A texel of level k + 1 is the area average of the texels 2i and 2i + 1 of level k on each axis (and 2i + 2 too for the last texel
    /// of an odd side), over premultiplied alpha with the codebase's resampling rule: equal texels give that texel, an alpha that
    /// rounds to 0 keeps the plain average colour of the transparent texels. Built once per content, conversion and luminance and
    /// shared by every layer that reads them (immutable; workers read it).
    /// </summary>
    internal sealed class ImageMipChain
    {
        internal readonly int Levels;
        internal readonly int[] Widths, Heights;
        /// <summary>Level 0: the content's pixels as stored (not converted); further levels converted.</summary>
        internal readonly byte[][] Data;
        /// <summary>Level 0's colour conversion table (null: none) and whether it reads the luminance.</summary>
        readonly byte[] lut; readonly bool luma;
        /// <summary>Bytes of the levels beyond 0 (what the cache holds; level 0 is the resource's own copy).</summary>
        internal readonly long Bytes;
        internal readonly string Hash; internal readonly FillImageConversion Conversion;

        ImageMipChain(ImageContent content, FillImageConversion conversion, bool luminance)
        {
            Hash = content.Hash; Conversion = conversion; lut = FillImageColor.Table(conversion); luma = luminance;
            Levels = LevelCount(content.Width, content.Height);
            Widths = new int[Levels]; Heights = new int[Levels]; Data = new byte[Levels][];
            Widths[0] = content.Width; Heights[0] = content.Height; Data[0] = content.Pixels;
            for (int k = 1; k < Levels; k++)
            {
                Widths[k] = Math.Max(1, Widths[k - 1] / 2); Heights[k] = Math.Max(1, Heights[k - 1] / 2);
                Data[k] = Halve(k - 1); Bytes += Data[k].LongLength;
            }
        }

        internal static ImageMipChain Build(ImageContent content, FillImageConversion conversion, bool luminance) => new ImageMipChain(content, conversion, luminance);

        internal static int LevelCount(int width, int height)
        {
            int n = 1;
            while (width > 1 || height > 1) { width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); n++; }
            return n;
        }
        /// <summary>The bytes the levels beyond 0 of a width × height image take.</summary>
        internal static long ExtraBytes(int width, int height)
        {
            long sum = 0;
            while (width > 1 || height > 1) { width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); sum += (long)width * height * 4; }
            return sum;
        }

        /// <summary>One texel of a level as the channel reads it (converted).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void Read(int level, int x, int y, out int r, out int g, out int b, out int a)
        {
            var d = Data[level]; int o = (y * Widths[level] + x) * 4;
            r = d[o]; g = d[o + 1]; b = d[o + 2]; a = d[o + 3];
            if (level != 0) return;
            if (lut != null) { r = lut[r]; g = lut[g]; b = lut[b]; }
            if (luma) r = g = b = FillImageColor.Luminance((byte)r, (byte)g, (byte)b);
        }

        /// <summary>Where texel i of the halved axis reads from: two texels, three for the last texel of an odd side, one for a side of 1.</summary>
        static void Span(int i, int n, int halved, out int start, out int count)
        {
            if (n == 1) { start = 0; count = 1; return; }
            start = 2 * i; count = i == halved - 1 && (n & 1) == 1 ? 3 : 2;
        }

        byte[] Halve(int level)
        {
            int w = Widths[level], h = Heights[level], nw = Widths[level + 1], nh = Heights[level + 1];
            var result = new byte[(long)nw * nh * 4];
            CoreParallelism.For(nh, CoreParallelism.Degree, j =>
            {
                Span(j, h, nh, out int y0, out int ny);
                for (int i = 0; i < nw; i++)
                {
                    Span(i, w, nw, out int x0, out int nx);
                    double weight = 1.0 / (nx * ny), a = 0, cr = 0, cg = 0, cb = 0, zw = 0, zr = 0, zg = 0, zb = 0;
                    bool same = true; int fr = 0, fg = 0, fb = 0, fa = 0;
                    for (int v = 0; v < ny; v++)
                        for (int u = 0; u < nx; u++)
                        {
                            Read(level, x0 + u, y0 + v, out int r, out int g, out int b, out int al);
                            if (u == 0 && v == 0) { fr = r; fg = g; fb = b; fa = al; }
                            else if (same && (r != fr || g != fg || b != fb || al != fa)) same = false;
                            if (al == 0) { zw += weight; zr += weight * r; zg += weight * g; zb += weight * b; continue; }
                            double k = weight * al; a += k; cr += k * r; cg += k * g; cb += k * b;
                        }
                    int o = (j * nw + i) * 4;
                    if (same) { result[o] = (byte)fr; result[o + 1] = (byte)fg; result[o + 2] = (byte)fb; result[o + 3] = (byte)fa; continue; }
                    byte alpha = MathUtil.ToByte(a / 255);
                    if (alpha == 0)
                    {
                        if (zw > 0) { result[o] = MathUtil.ToByte(zr / zw / 255); result[o + 1] = MathUtil.ToByte(zg / zw / 255); result[o + 2] = MathUtil.ToByte(zb / zw / 255); }
                        continue; // alpha 0（透明でも、読んだ透明画素の色の平均を残す）
                    }
                    result[o] = MathUtil.ToByte(cr / a / 255); result[o + 1] = MathUtil.ToByte(cg / a / 255); result[o + 2] = MathUtil.ToByte(cb / a / 255); result[o + 3] = alpha;
                }
            });
            return result;
        }
    }

    /// <summary>
    /// A fill channel's image bound to its projection, mipmap and (for the model projections) the mesh maps it reads: evaluates pixels
    /// (<see cref="FillProjection"/> has the formulas). Read-only; workers share it. When it cannot project (<see cref="Reason"/>),
    /// every pixel is the channel's fill value.
    /// </summary>
    internal sealed class FillImageSampler
    {
        const double InvTwoPi = 1 / (2 * Math.PI), InvPi = 1 / Math.PI, InvLn2 = 1.4426950408889634;
        /// <summary>Why the channel shows its fill value instead of the image, or null when it projects.</summary>
        internal readonly string Reason;
        internal readonly Rgba32 Fallback;
        internal readonly ImageMipChain Mips;
        readonly FillProjectionMode mode; readonly bool clamp; readonly int width, height;
        // UV 変換 (s′, t′) = A (s, t) + b
        readonly double a00, a01, a10, a11, b0, b1;
        // Uv: 段 0 のテクセル座標 = (ux, uy, uc)·(x + .5, y + .5, 1)、(vx, vy, vc) も同じ。足跡は一定
        readonly double ux, uy, uc, vx, vy, vc, uvRho;
        // モデルの投影: Position の 16 bit の値 → 置き場の空間（m · v + k）、ワールドの法線 → 置き場の空間（nm）
        readonly ushort[] position, normal; readonly byte[] positionCoverage, normalCoverage;
        readonly double[] m, k, nm; readonly double invSx, invSy, invSz, keep;
        readonly int w0, h0;

        internal bool Active => Reason == null;
        /// <summary>False when nothing it writes has alpha (inactive with a transparent fill value).</summary>
        internal bool MayCover => Active || Fallback != Rgba32.Transparent;

        FillImageSampler(string reason, Rgba32 fallback) { Reason = reason; Fallback = fallback; }

        FillImageSampler(FillProjection p, ImageMipChain mips, Rgba32 fallback, int width, int height, BakedMeshMap positions, BakedMeshMap normals, GeneratorModelFrame frame)
        {
            Fallback = fallback; Mips = mips; mode = p.Mode; clamp = p.Wrap == FillWrap.Clamp; this.width = width; this.height = height;
            w0 = mips.Widths[0]; h0 = mips.Heights[0];
            double phi = -p.Rotation * Math.PI / 180, c = Math.Cos(phi), sn = Math.Sin(phi);
            a00 = p.TileU * c; a01 = -p.TileU * sn; b0 = p.TileU * (.5 - .5 * c + .5 * sn) + p.OffsetU;
            a10 = p.TileV * sn; a11 = p.TileV * c; b1 = p.TileV * (.5 - .5 * sn - .5 * c) + p.OffsetV;
            ux = w0 * a00 / width; uy = w0 * a01 / height; uc = w0 * b0 - .5;
            vx = h0 * a10 / width; vy = h0 * a11 / height; vc = h0 * b1 - .5;
            uvRho = Math.Max(Math.Sqrt(ux * ux + vx * vx), Math.Sqrt(uy * uy + vy * vy));
            if (mode == FillProjectionMode.Uv) return;
            var v = p.Placement; invSx = 1 / v.SizeX; invSy = 1 / v.SizeY; invSz = 1 / v.SizeZ; keep = 1 - p.BlendWidth;
            position = positions.Data; positionCoverage = positions.Coverage;
            if (normals != null) { normal = normals.Data; normalCoverage = normals.Coverage; }
            // p = min + v · extent / 65535（スナップショットの空間）→ ルートの空間 R0ᵀ (p − t0) → 置き場の空間 Rsᵀ (… − c)。形のグラデーションと同じ組み方
            var prov = positions.Provenance; var r0 = frame.RotationMatrix(); var rs = v.RotationMatrix();
            var a = new double[9]; // A = Rsᵀ R0ᵀ
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) a[i * 3 + j] = rs[i] * r0[j * 3] + rs[3 + i] * r0[j * 3 + 1] + rs[6 + i] * r0[j * 3 + 2];
            m = new double[9]; k = new double[3]; nm = a;
            double[] min = { prov.BoundsMin(0), prov.BoundsMin(1), prov.BoundsMin(2) }, t0 = { frame.PositionX, frame.PositionY, frame.PositionZ }, center = { v.CenterX, v.CenterY, v.CenterZ };
            var rootCenter = new double[3];
            for (int q = 0; q < 3; q++) rootCenter[q] = r0[q] * t0[0] + r0[3 + q] * t0[1] + r0[6 + q] * t0[2] + center[q];
            for (int i = 0; i < 3; i++)
            {
                double offset = 0;
                for (int j = 0; j < 3; j++) { m[i * 3 + j] = a[i * 3 + j] * ((prov.BoundsMax(j) - prov.BoundsMin(j)) / 65535); offset += a[i * 3 + j] * min[j]; }
                k[i] = offset - (rs[i] * rootCenter[0] + rs[3 + i] * rootCenter[1] + rs[6 + i] * rootCenter[2]);
            }
        }

        /// <summary>A sampler that writes the fill value everywhere, with the reason.</summary>
        internal static FillImageSampler Constant(string reason, Rgba32 fallback) => new FillImageSampler(reason ?? "The image cannot be used.", fallback);

        /// <summary>Binds the projection to the mipmap and the resolved maps (indexed by <see cref="MeshMapKind"/>), or a constant sampler with
        /// the reason when a map it uses is missing (mapReasons), of another size, or the model root is not known.</summary>
        internal static FillImageSampler Bind(FillProjection p, ImageMipChain mips, Rgba32 fallback, int width, int height, IReadOnlyList<BakedMeshMap> maps, IReadOnlyList<string> mapReasons, GeneratorModelFrame frame)
        {
            BakedMeshMap positions = null, normals = null;
            if (p.ReadsMeshMaps)
            {
                foreach (var kind in p.UsedMaps)
                {
                    var map = maps == null || (int)kind >= maps.Count ? null : maps[(int)kind];
                    if (map == null)
                    {
                        string why = mapReasons != null && (int)kind < mapReasons.Count ? mapReasons[(int)kind] : null;
                        return Constant("The " + p.Mode + " projection needs the " + kind + " map: " + (why ?? kind + " map is not available."), fallback);
                    }
                    if (map.Width != width || map.Height != height) return Constant(kind + " map is " + map.Width + "×" + map.Height + ", the texture set " + width + "×" + height + ". Bake it again.", fallback);
                }
                if (frame == null) return Constant("Where the model root is is not known, so the projection cannot be placed on the model (load the model).", fallback);
                positions = maps[(int)MeshMapKind.Position];
                if (p.Mode == FillProjectionMode.Triplanar) normals = maps[(int)MeshMapKind.WorldNormal];
            }
            return new FillImageSampler(p, mips, fallback, width, height, positions, normals, frame);
        }

        // ───────── 画素 ─────────

        /// <summary>Rows [rowFrom, rowTo) of the rect at (x0, y0) of width w into buf (row r at r · stride · 4), straight RGBA8.</summary>
        internal void FillRows(int x0, int y0, int w, int rowFrom, int rowTo, byte[] buf, int stride)
        {
            for (int row = rowFrom; row < rowTo; row++)
            {
                int y = y0 + row, o = row * stride * 4;
                for (int c = 0; c < w; c++, o += 4)
                {
                    var p = Pixel(x0 + c, y);
                    buf[o] = p.R; buf[o + 1] = p.G; buf[o + 2] = p.B; buf[o + 3] = p.A;
                }
            }
        }

        /// <summary>The channel's pixel (x, y) of the texture set.</summary>
        internal Rgba32 Pixel(int x, int y)
        {
            if (Reason != null) return Fallback;
            var acc = new Acc();
            if (mode == FillProjectionMode.Uv)
            {
                double px = x + .5, py = y + .5;
                Sample(ux * px + uy * py + uc, vx * px + vy * py + vc, uvRho, 1, ref acc);
                return acc.Resolve(Fallback);
            }
            int i = y * width + x;
            if (positionCoverage[i] == 0) return Fallback; // UV の島の外: 入力（塗りつぶしの値）のまま
            Point(i, out double lx, out double ly, out double lz);
            // 足跡の差分に使う隣（軸ごとに、モデルの上で近いほう）とその点
            int ix = Neighbor(x, y, 1, 0, lx, ly, lz, out int sx, out double ax0, out double ay0, out double az0);
            int iy = Neighbor(x, y, 0, 1, lx, ly, lz, out int sy, out double bx0, out double by0, out double bz0);
            if (mode != FillProjectionMode.Triplanar)
            {
                Project(mode, lx, ly, lz, out double s, out double t);
                double dsx = 0, dtx = 0, dsy = 0, dty = 0;
                bool around = mode != FillProjectionMode.Planar; // 経度の継ぎ目（s が 1 つ跳ぶ）をまたぐ差は近い向きで
                if (ix >= 0) { Project(mode, ax0, ay0, az0, out double s1, out double t1); dsx = s1 - s; if (around) dsx -= Math.Round(dsx); dsx *= sx; dtx = sx * (t1 - t); }
                if (iy >= 0) { Project(mode, bx0, by0, bz0, out double s1, out double t1); dsy = s1 - s; if (around) dsy -= Math.Round(dsy); dsy *= sy; dty = sy * (t1 - t); }
                SampleAt(s, t, dsx, dtx, dsy, dty, 1, ref acc);
                return acc.Resolve(Fallback);
            }
            if (normalCoverage[i] == 0) return Fallback;
            double rnx = normal[i * 3] / 65535.0 * 2 - 1, rny = normal[i * 3 + 1] / 65535.0 * 2 - 1, rnz = normal[i * 3 + 2] / 65535.0 * 2 - 1;
            double nx = nm[0] * rnx + nm[1] * rny + nm[2] * rnz, ny = nm[3] * rnx + nm[4] * rny + nm[5] * rnz, nz = nm[6] * rnx + nm[7] * rny + nm[8] * rnz;
            double ax = nx < 0 ? -nx : nx, ay = ny < 0 ? -ny : ny, az = nz < 0 ? -nz : nz, most = Math.Max(ax, Math.Max(ay, az));
            if (!(most > 1e-9)) return Fallback; // 法線の無いテクセル
            double cut = keep * most, wx = Math.Max(0, ax - cut), wy = Math.Max(0, ay - cut), wz = Math.Max(0, az - cut), total = wx + wy + wz;
            if (!(total > 0)) { wx = ax >= most ? 1 : 0; wy = ay >= most ? 1 : 0; wz = az >= most ? 1 : 0; total = wx + wy + wz; } // 幅 0 で同じ大きさ
            for (int axis = 0; axis < 3; axis++)
            {
                double weight = (axis == 0 ? wx : axis == 1 ? wy : wz) / total;
                if (weight <= 0) continue;
                bool positive = axis == 0 ? nx >= 0 : axis == 1 ? ny >= 0 : nz >= 0;
                Face(axis, positive, lx, ly, lz, out double s, out double t);
                double dsx = 0, dtx = 0, dsy = 0, dty = 0;
                if (ix >= 0) { Face(axis, positive, ax0, ay0, az0, out double s1, out double t1); dsx = sx * (s1 - s); dtx = sx * (t1 - t); }
                if (iy >= 0) { Face(axis, positive, bx0, by0, bz0, out double s1, out double t1); dsy = sy * (s1 - s); dty = sy * (t1 - t); }
                SampleAt(s, t, dsx, dtx, dsy, dty, weight, ref acc);
            }
            return acc.Resolve(Fallback);
        }

        void Point(int i, out double lx, out double ly, out double lz)
        {
            double px = position[i * 3], py = position[i * 3 + 1], pz = position[i * 3 + 2];
            lx = m[0] * px + m[1] * py + m[2] * pz + k[0]; ly = m[3] * px + m[4] * py + m[5] * pz + k[1]; lz = m[6] * px + m[7] * py + m[8] * pz + k[2];
        }
        /// <summary>The neighbour along (dx, dy) used for the footprint: the covered one of the two sides nearer on the model (sign +1
        /// forward, −1 backward), or −1 when neither side is covered.</summary>
        int Neighbor(int x, int y, int dx, int dy, double lx, double ly, double lz, out int sign, out double px, out double py, out double pz)
        {
            int best = -1; double bestDistance = double.PositiveInfinity; sign = 0; px = py = pz = 0;
            for (int side = 1; side >= -1; side -= 2)
            {
                int nx = x + dx * side, ny = y + dy * side;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                int j = ny * width + nx;
                if (positionCoverage[j] == 0) continue;
                Point(j, out double qx, out double qy, out double qz);
                double d = (qx - lx) * (qx - lx) + (qy - ly) * (qy - ly) + (qz - lz) * (qz - lz);
                if (d < bestDistance) { bestDistance = d; best = j; sign = side; px = qx; py = qy; pz = qz; }
            }
            return best;
        }

        void Project(FillProjectionMode projection, double lx, double ly, double lz, out double s, out double t)
        {
            switch (projection)
            {
                case FillProjectionMode.Planar: s = lx * invSx + .5; t = ly * invSy + .5; return;
                case FillProjectionMode.Spherical:
                {
                    double r = Math.Sqrt(lx * lx + ly * ly + lz * lz);
                    if (!(r > 0)) { s = .5; t = .5; return; }
                    s = Math.Atan2(lx, -lz) * InvTwoPi + .5;
                    double q = ly / r; t = Math.Asin(q < -1 ? -1 : q > 1 ? 1 : q) * InvPi + .5;
                    return;
                }
                default: s = Math.Atan2(lx, -lz) * InvTwoPi + .5; t = ly * invSy + .5; return; // Cylindrical
            }
        }
        /// <summary>The triplanar face of an axis, read the right way round from outside the side the normal points to.</summary>
        void Face(int axis, bool positive, double lx, double ly, double lz, out double s, out double t)
        {
            switch (axis)
            {
                case 0: s = (positive ? lz : -lz) * invSz + .5; t = ly * invSy + .5; return;
                case 1: s = (positive ? lx : -lx) * invSx + .5; t = lz * invSz + .5; return;
                default: s = (positive ? -lx : lx) * invSx + .5; t = ly * invSy + .5; return;
            }
        }

        /// <summary>Samples base coordinates (s, t) with their derivatives per pixel through the UV transform.</summary>
        void SampleAt(double s, double t, double dsx, double dtx, double dsy, double dty, double weight, ref Acc acc)
        {
            double sp = a00 * s + a01 * t + b0, tp = a10 * s + a11 * t + b1;
            double cx = w0 * (a00 * dsx + a01 * dtx), cy = h0 * (a10 * dsx + a11 * dtx), ex = w0 * (a00 * dsy + a01 * dty), ey = h0 * (a10 * dsy + a11 * dty);
            double rho = Math.Max(Math.Sqrt(cx * cx + cy * cy), Math.Sqrt(ex * ex + ey * ey));
            Sample(sp * w0 - .5, tp * h0 - .5, rho, weight, ref acc);
        }

        /// <summary>Level-0 texel coordinates (u0, v0) with the footprint ρ: bilinear at level 0, or trilinear between the mipmap levels.</summary>
        void Sample(double u0, double v0, double rho, double weight, ref Acc acc)
        {
            int last = Mips.Levels - 1;
            if (!(rho > 1) || last == 0) { Bilinear(0, u0, v0, weight, ref acc); return; }
            double lod = Math.Log(rho) * InvLn2;
            if (lod >= last) { Bilinear(last, Level(last, u0, true), Level(last, v0, false), weight, ref acc); return; }
            int k = (int)lod; double f = lod - k;
            Bilinear(k, Level(k, u0, true), Level(k, v0, false), weight * (1 - f), ref acc);
            if (f > 0) Bilinear(k + 1, Level(k + 1, u0, true), Level(k + 1, v0, false), weight * f, ref acc);
        }
        double Level(int level, double c0, bool horizontal)
            => level == 0 ? c0 : (c0 + .5) * (horizontal ? Mips.Widths[level] / (double)w0 : Mips.Heights[level] / (double)h0) - .5;

        void Bilinear(int level, double u, double v, double weight, ref Acc acc)
        {
            int w = Mips.Widths[level], h = Mips.Heights[level];
            double fu = Math.Floor(u), fv = Math.Floor(v), fx = u - fu, fy = v - fv;
            int iu = (int)fu, iv = (int)fv;
            for (int dy = 0; dy < 2; dy++)
            {
                double wy = dy == 0 ? 1 - fy : fy; if (wy <= 0) continue;
                int ty = Wrap(iv + dy, h);
                for (int dx = 0; dx < 2; dx++)
                {
                    double wgt = weight * wy * (dx == 0 ? 1 - fx : fx); if (wgt <= 0) continue;
                    Mips.Read(level, Wrap(iu + dx, w), ty, out int r, out int g, out int b, out int a);
                    acc.Add(wgt, r, g, b, a);
                }
            }
        }
        int Wrap(int i, int n)
        {
            if (clamp) return i < 0 ? 0 : i >= n ? n - 1 : i;
            i %= n; return i < 0 ? i + n : i;
        }

        /// <summary>Weighted texels, premultiplied, with the transparent ones' colours apart.</summary>
        struct Acc
        {
            double a, r, g, b, zw, zr, zg, zb; int count; int first; bool same;
            public void Add(double w, int cr, int cg, int cb, int ca)
            {
                int packed = cr | cg << 8 | cb << 16 | ca << 24;
                if (count++ == 0) { first = packed; same = true; } else if (packed != first) same = false;
                if (ca == 0) { zw += w; zr += w * cr; zg += w * cg; zb += w * cb; return; }
                double k = w * ca; a += k; r += k * cr; g += k * cg; b += k * cb;
            }
            public Rgba32 Resolve(Rgba32 fallback)
            {
                if (count == 0) return fallback;
                if (same) return new Rgba32((byte)first, (byte)(first >> 8), (byte)(first >> 16), (byte)(first >> 24));
                byte alpha = MathUtil.ToByte(a / 255);
                if (alpha == 0) return zw > 0 ? new Rgba32(MathUtil.ToByte(zr / zw / 255), MathUtil.ToByte(zg / zw / 255), MathUtil.ToByte(zb / zw / 255), 0) : Rgba32.Transparent;
                return new Rgba32(MathUtil.ToByte(r / a / 255), MathUtil.ToByte(g / a / 255), MathUtil.ToByte(b / a / 255), alpha);
            }
        }
    }
}
