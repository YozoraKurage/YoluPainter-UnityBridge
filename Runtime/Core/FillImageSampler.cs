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
    /// Where each tile of a Position map has covered texels, and the box (in the map's raw 16-bit values) that holds them: lets a decal
    /// skip the tiles its box cannot reach without reading their texels. Immutable; made once per map and tile size.
    /// </summary>
    internal sealed class PositionTileBounds
    {
        internal readonly BakedMeshMap Map; internal readonly int TileSize, Columns, Rows;
        /// <summary>Per tile (row-major from the bottom): min x, y, z, max x, y, z of the covered texels; Any false when none is covered.</summary>
        readonly ushort[] box; readonly bool[] any;

        PositionTileBounds(BakedMeshMap map, int tileSize)
        {
            Map = map; TileSize = tileSize; Columns = (map.Width + tileSize - 1) / tileSize; Rows = (map.Height + tileSize - 1) / tileSize;
            box = new ushort[Columns * Rows * 6]; any = new bool[Columns * Rows];
            int width = map.Width, height = map.Height; var data = map.Data; var coverage = map.Coverage;
            CoreParallelism.For(Rows, CoreParallelism.Degree, ty =>
            {
                for (int tx = 0; tx < Columns; tx++)
                {
                    int lx = ushort.MaxValue, ly = ushort.MaxValue, lz = ushort.MaxValue, hx = 0, hy = 0, hz = 0; bool covered = false;
                    for (int y = ty * tileSize, y1 = Math.Min(height, y + tileSize); y < y1; y++)
                        for (int x = tx * tileSize, x1 = Math.Min(width, x + tileSize), i = y * width + x; x < x1; x++, i++)
                        {
                            if (coverage[i] == 0) continue;
                            covered = true; int vx = data[i * 3], vy = data[i * 3 + 1], vz = data[i * 3 + 2];
                            if (vx < lx) lx = vx; if (vx > hx) hx = vx; if (vy < ly) ly = vy; if (vy > hy) hy = vy; if (vz < lz) lz = vz; if (vz > hz) hz = vz;
                        }
                    int t = ty * Columns + tx; any[t] = covered;
                    if (covered) { box[t * 6] = (ushort)lx; box[t * 6 + 1] = (ushort)ly; box[t * 6 + 2] = (ushort)lz; box[t * 6 + 3] = (ushort)hx; box[t * 6 + 4] = (ushort)hy; box[t * 6 + 5] = (ushort)hz; }
                }
            });
        }
        internal static PositionTileBounds Of(BakedMeshMap positions, int tileSize) => new PositionTileBounds(positions, tileSize);

        /// <summary>True when tile (tx, ty) has covered texels whose box, taken by the affine map l = m · v + k into a placement's space,
        /// can meet the box |l_i| ≤ half_i (a conservative test: the image of the tile's box is bounded by its centre ± Σ |m_ij| e_j).</summary>
        internal bool Meets(int tx, int ty, double[] m, double[] k, double hx, double hy, double hz)
        {
            if (tx < 0 || ty < 0 || tx >= Columns || ty >= Rows) return false;
            int t = ty * Columns + tx; if (!any[t]) return false;
            double cx = (box[t * 6] + (double)box[t * 6 + 3]) * .5, cy = (box[t * 6 + 1] + (double)box[t * 6 + 4]) * .5, cz = (box[t * 6 + 2] + (double)box[t * 6 + 5]) * .5;
            double ex = (box[t * 6 + 3] - (double)box[t * 6]) * .5, ey = (box[t * 6 + 4] - (double)box[t * 6 + 1]) * .5, ez = (box[t * 6 + 5] - (double)box[t * 6 + 2]) * .5;
            for (int i = 0; i < 3; i++)
            {
                double centre = m[i * 3] * cx + m[i * 3 + 1] * cy + m[i * 3 + 2] * cz + k[i];
                double reach = Math.Abs(m[i * 3]) * ex + Math.Abs(m[i * 3 + 1]) * ey + Math.Abs(m[i * 3 + 2]) * ez;
                double half = i == 0 ? hx : i == 1 ? hy : hz;
                if (Math.Abs(centre) - reach > half * (1 + 1e-9) + 1e-12) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// A fill channel's image bound to its projection, mipmap and (for the model projections) the mesh maps it reads: evaluates pixels
    /// (<see cref="FillProjection"/> has the formulas). Read-only; workers share it. When it cannot project (<see cref="Reason"/>),
    /// every pixel is the channel's fill value. A decal's channel (<see cref="BindDecal"/>) has its own image or only its value, the
    /// decal's shape image and its coverage; it is transparent everywhere while the decal cannot be placed.
    /// </summary>
    internal sealed class FillImageSampler
    {
        const double InvTwoPi = 1 / (2 * Math.PI), InvPi = 1 / Math.PI, InvLn2 = 1.4426950408889634, ToDegrees = 180 / Math.PI;
        /// <summary>Why the channel shows its fill value instead of the image, or null when it projects. A placed decal's channel whose
        /// image cannot be read has the reason and draws its value inside the decal.</summary>
        internal readonly string Reason;
        internal readonly Rgba32 Fallback;
        internal readonly ImageMipChain Mips;
        /// <summary>A decal's shape image read by a channel that is not the shape channel (null: the channel is the shape, or there is none).</summary>
        internal readonly ImageMipChain Shape;
        readonly FillProjectionMode mode; readonly bool clamp, none; readonly int width, height;
        // デカール: 置けたか（マップがそろったか）、箱の半分の大きさ、奥行きと裏向きの縁の幅、タイルの外接箱
        readonly bool decal, placed; readonly double halfX, halfY, halfZ, depthBand, backAngle, backBand; readonly PositionTileBounds tileBounds;
        // UV 変換 (s′, t′) = A (s, t) + b
        readonly double a00, a01, a10, a11, b0, b1;
        // Uv: 段 0 のテクセル座標 = (ux, uy, uc)·(x + .5, y + .5, 1)、(vx, vy, vc) も同じ。足跡は一定
        readonly double ux, uy, uc, vx, vy, vc, uvRho;
        // モデルの投影: Position の 16 bit の値 → 置き場の空間（m · v + k）、ワールドの法線 → 置き場の空間（nm）
        readonly ushort[] position, normal; readonly byte[] positionCoverage, normalCoverage;
        readonly double[] m, k, nm; readonly double invSx, invSy, invSz, keep;
        readonly int w0, h0;

        internal bool Active => Reason == null;
        /// <summary>A decal's channel (placed or not).</summary>
        internal bool IsDecal => decal;
        /// <summary>A decal that could be placed (its maps and the model root are known).</summary>
        internal bool Placed => placed;
        /// <summary>False when nothing it writes has alpha (inactive with a transparent fill value; a decal not placed, or with neither an
        /// image nor a visible value).</summary>
        internal bool MayCover => decal ? placed && (Mips != null || Fallback.A > 0) : Active || Fallback != Rgba32.Transparent;

        FillImageSampler(string reason, Rgba32 fallback, bool decal = false) { Reason = reason; Fallback = fallback; this.decal = decal; }

        FillImageSampler(FillProjection p, ImageMipChain mips, Rgba32 fallback, int width, int height, BakedMeshMap positions, BakedMeshMap normals, GeneratorModelFrame frame,
            string reason = null, ImageMipChain shape = null, PositionTileBounds bounds = null)
        {
            Fallback = fallback; Mips = mips; mode = p.Mode; clamp = p.Wrap == FillWrap.Clamp; none = p.Wrap == FillWrap.None; this.width = width; this.height = height;
            Reason = reason; Shape = shape;
            if (p.IsDecal)
            {
                decal = placed = true; tileBounds = bounds;
                halfX = p.Placement.SizeX * .5; halfY = p.Placement.SizeY * .5; halfZ = p.Placement.SizeZ * .5;
                depthBand = 1 - p.DepthHardness; backAngle = p.BackfaceAngle; backBand = (1 - p.BackfaceHardness) * p.BackfaceAngle;
            }
            var reference = mips ?? shape; // UV の変換の段 0 の大きさ（値だけのチャンネルは形の画像の、それも無ければ使わない）
            w0 = reference?.Widths[0] ?? 1; h0 = reference?.Heights[0] ?? 1;
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
            PlacementTransform(v, positions, frame, out m, out k, out nm);
        }

        /// <summary>A sampler that writes the fill value everywhere, with the reason.</summary>
        internal static FillImageSampler Constant(string reason, Rgba32 fallback) => new FillImageSampler(reason ?? "The image cannot be used.", fallback);

        /// <summary>A decal's channel: its image's mipmap (or null: the value, with ownReason when the image cannot be read), the shape
        /// image's mipmap when another channel holds the shape (null: this channel is the shape, or there is none), its value and the
        /// resolved maps. A decal whose maps are missing, of another size or whose model root is not known is transparent everywhere, with
        /// the reason.</summary>
        internal static FillImageSampler BindDecal(FillProjection p, ImageMipChain own, string ownReason, ImageMipChain shape, Rgba32 value, int width, int height,
            IReadOnlyList<BakedMeshMap> maps, IReadOnlyList<string> mapReasons, GeneratorModelFrame frame, Func<BakedMeshMap, PositionTileBounds> bounds)
        {
            string why = PlacementProblem(p, width, height, maps, mapReasons, frame);
            if (why != null) return new FillImageSampler(why, value, decal: true);
            var positions = maps[(int)MeshMapKind.Position];
            return new FillImageSampler(p, own, value, width, height, positions, maps[(int)MeshMapKind.WorldNormal], frame, ownReason, shape, bounds?.Invoke(positions));
        }

        /// <summary>Why the model projection cannot be placed now (a map it reads is missing or of another size, the model root is not
        /// known), or null.</summary>
        internal static string PlacementProblem(FillProjection p, int width, int height, IReadOnlyList<BakedMeshMap> maps, IReadOnlyList<string> mapReasons, GeneratorModelFrame frame)
        {
            if (!p.ReadsMeshMaps) return null;
            foreach (var kind in p.UsedMaps)
            {
                var map = maps == null || (int)kind >= maps.Count ? null : maps[(int)kind];
                if (map == null)
                {
                    string why = mapReasons != null && (int)kind < mapReasons.Count ? mapReasons[(int)kind] : null;
                    return "The " + p.Mode + " projection needs the " + kind + " map: " + (why ?? kind + " map is not available.");
                }
                if (map.Width != width || map.Height != height) return kind + " map is " + map.Width + "×" + map.Height + ", the texture set " + width + "×" + height + ". Bake it again.";
            }
            if (frame == null) return "Where the model root is is not known, so the projection cannot be placed on the model (load the model).";
            return null;
        }

        /// <summary>Adds the tiles a decal at projection p may cover (its box meets the tile's covered positions; depth and facing are
        /// not looked at) to into. False when it cannot be told (the maps are not there): the caller then assumes every tile.</summary>
        internal static bool AddDecalTiles(FillProjection p, BakedMeshMap positions, GeneratorModelFrame frame, PositionTileBounds bounds, ICollection<TileCoord> into)
        {
            if (positions == null || frame == null || bounds == null || !ReferenceEquals(bounds.Map, positions)) return false;
            PlacementTransform(p.Placement, positions, frame, out var m, out var k, out _);
            double hx = p.Placement.SizeX * .5, hy = p.Placement.SizeY * .5, hz = p.Placement.SizeZ * .5;
            for (int ty = 0; ty < bounds.Rows; ty++) for (int tx = 0; tx < bounds.Columns; tx++) if (bounds.Meets(tx, ty, m, k, hx, hy, hz)) into.Add(new TileCoord(tx, ty));
            return true;
        }

        /// <summary>The affine map from a Position map's raw 16-bit values to a placement's space (l = m · v + k) and the rotation that
        /// takes a world normal there (nm): snapshot space (the bake's bounding box) → the model root's space (frame) → the placement's.</summary>
        static void PlacementTransform(ShapeVolume v, BakedMeshMap positions, GeneratorModelFrame frame, out double[] m, out double[] k, out double[] nm)
        {
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

        /// <summary>True when the channel can have pixels in the tiles [tx0, tx1) × [ty0, ty1) (a decal: only where its box meets the
        /// tiles' positions).</summary>
        internal bool MayCoverTiles(int tx0, int ty0, int tx1, int ty1, bool externalValue = false)
        {
            if (!(externalValue && decal ? placed : MayCover)) return false;
            if (!decal || tileBounds == null) return true;
            for (int ty = Math.Max(0, ty0); ty < Math.Min(tileBounds.Rows, ty1); ty++)
                for (int tx = Math.Max(0, tx0); tx < Math.Min(tileBounds.Columns, tx1); tx++)
                    if (tileBounds.Meets(tx, ty, m, k, halfX, halfY, halfZ)) return true;
            return false;
        }

        /// <summary>Binds the projection to the mipmap and the resolved maps (indexed by <see cref="MeshMapKind"/>), or a constant sampler with
        /// the reason when a map it uses is missing (mapReasons), of another size, or the model root is not known.</summary>
        internal static FillImageSampler Bind(FillProjection p, ImageMipChain mips, Rgba32 fallback, int width, int height, IReadOnlyList<BakedMeshMap> maps, IReadOnlyList<string> mapReasons, GeneratorModelFrame frame)
        {
            BakedMeshMap positions = null, normals = null;
            if (p.ReadsMeshMaps)
            {
                string why = PlacementProblem(p, width, height, maps, mapReasons, frame);
                if (why != null) return Constant(why, fallback);
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
            if (decal) return placed ? DecalPixel(x, y) : Rgba32.Transparent;
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

        /// <summary>A placed decal's pixel: transparent outside its box, where it is culled and on texels without a position or normal;
        /// inside, the channel's colour with its alpha times the shape and the coverage.</summary>
        internal Rgba32 ApplyDecalToValue(int x, int y, Rgba32 value) => placed ? DecalPixel(x, y, value) : Rgba32.Transparent;

        Rgba32 DecalPixel(int x, int y, Rgba32? externalValue = null)
        {
            if (!DecalPoint(x, y, out double lx, out double ly, out double lz, out double cover)) return Rgba32.Transparent;
            double s = lx * invSx + .5, t = ly * invSy + .5, dsx = 0, dtx = 0, dsy = 0, dty = 0;
            if (Mips != null || Shape != null)
            {
                // 足跡（平面と同じく、軸ごとにモデルの上で近いほうの隣との差）
                int ix = Neighbor(x, y, 1, 0, lx, ly, lz, out int sx, out double ax0, out double ay0, out double az0);
                int iy = Neighbor(x, y, 0, 1, lx, ly, lz, out int sy, out double bx0, out double by0, out double bz0);
                if (ix >= 0) { dsx = sx * (ax0 * invSx + .5 - s); dtx = sx * (ay0 * invSy + .5 - t); }
                if (iy >= 0) { dsy = sy * (bx0 * invSx + .5 - s); dty = sy * (by0 * invSy + .5 - t); }
            }
            if (Shape != null)
            {
                var shape = new Acc(); SampleAt(Shape, s, t, dsx, dtx, dsy, dty, 1, ref shape);
                cover *= shape.Alpha / 255;
                if (!(cover > 0)) return Rgba32.Transparent;
            }
            if (externalValue.HasValue || Mips == null)
            {
                var value = externalValue ?? Fallback;
                return new Rgba32(value.R, value.G, value.B, MathUtil.ToByte(value.A / 255.0 * cover));
            }
            var acc = new Acc(); SampleAt(Mips, s, t, dsx, dtx, dsy, dty, 1, ref acc);
            return acc.Resolve(cover);
        }

        /// <summary>A placed decal's texel: its point in the placement's space and the coverage c (box, depth, facing; 0..1). False where
        /// c = 0 (outside the box, culled, no position or normal).</summary>
        bool DecalPoint(int x, int y, out double lx, out double ly, out double lz, out double cover)
        {
            int i = y * width + x; lx = ly = lz = cover = 0;
            if (positionCoverage[i] == 0 || normalCoverage[i] == 0) return false;
            Point(i, out lx, out ly, out lz);
            if (Math.Abs(lx) > halfX || Math.Abs(ly) > halfY || Math.Abs(lz) > halfZ) return false;
            // 奥行き: 箱の真ん中の面から −Z・+Z の面へ
            double d = Math.Abs(lz) / halfZ; cover = depthBand <= 0 || d <= 1 - depthBand ? 1 : (1 - d) / depthBand;
            // 面の向き: 置き場の空間の法線と −Z（画像を見る側）のなす角
            double rnx = normal[i * 3] / 65535.0 * 2 - 1, rny = normal[i * 3 + 1] / 65535.0 * 2 - 1, rnz = normal[i * 3 + 2] / 65535.0 * 2 - 1;
            double nx = nm[0] * rnx + nm[1] * rny + nm[2] * rnz, ny = nm[3] * rnx + nm[4] * rny + nm[5] * rnz, nz = nm[6] * rnx + nm[7] * rny + nm[8] * rnz;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(length > 1e-9)) return false; // 法線の無いテクセル
            double facing = -nz / length, angle = Math.Acos(facing < -1 ? -1 : facing > 1 ? 1 : facing) * ToDegrees;
            if (angle > backAngle) return false;
            if (backBand > 0 && angle > backAngle - backBand) cover *= (backAngle - angle) / backBand;
            return cover > 0;
        }
        /// <summary>A placed decal's coverage (0..1: its box, depth and facing, not its image) at texel (x, y); 0 when it is not placed.</summary>
        internal double DecalCoverage(int x, int y) => decal && placed && DecalPoint(x, y, out _, out _, out _, out double cover) ? cover : 0;

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
        void SampleAt(double s, double t, double dsx, double dtx, double dsy, double dty, double weight, ref Acc acc) => SampleAt(Mips, s, t, dsx, dtx, dsy, dty, weight, ref acc);
        void SampleAt(ImageMipChain chain, double s, double t, double dsx, double dtx, double dsy, double dty, double weight, ref Acc acc)
        {
            int cw = chain.Widths[0], ch = chain.Heights[0];
            double sp = a00 * s + a01 * t + b0, tp = a10 * s + a11 * t + b1;
            double cx = cw * (a00 * dsx + a01 * dtx), cy = ch * (a10 * dsx + a11 * dtx), ex = cw * (a00 * dsy + a01 * dty), ey = ch * (a10 * dsy + a11 * dty);
            double rho = Math.Max(Math.Sqrt(cx * cx + cy * cy), Math.Sqrt(ex * ex + ey * ey));
            Sample(chain, sp * cw - .5, tp * ch - .5, rho, weight, ref acc);
        }

        /// <summary>Level-0 texel coordinates (u0, v0) with the footprint ρ: bilinear at level 0, or trilinear between the mipmap levels.</summary>
        void Sample(double u0, double v0, double rho, double weight, ref Acc acc) => Sample(Mips, u0, v0, rho, weight, ref acc);
        void Sample(ImageMipChain chain, double u0, double v0, double rho, double weight, ref Acc acc)
        {
            int last = chain.Levels - 1;
            if (!(rho > 1) || last == 0) { Bilinear(chain, 0, u0, v0, weight, ref acc); return; }
            double lod = Math.Log(rho) * InvLn2;
            if (lod >= last) { Bilinear(chain, last, Level(chain, last, u0, true), Level(chain, last, v0, false), weight, ref acc); return; }
            int k = (int)lod; double f = lod - k;
            Bilinear(chain, k, Level(chain, k, u0, true), Level(chain, k, v0, false), weight * (1 - f), ref acc);
            if (f > 0) Bilinear(chain, k + 1, Level(chain, k + 1, u0, true), Level(chain, k + 1, v0, false), weight * f, ref acc);
        }
        static double Level(ImageMipChain chain, int level, double c0, bool horizontal)
            => level == 0 ? c0 : (c0 + .5) * (horizontal ? chain.Widths[level] / (double)chain.Widths[0] : chain.Heights[level] / (double)chain.Heights[0]) - .5;

        void Bilinear(ImageMipChain chain, int level, double u, double v, double weight, ref Acc acc)
        {
            int w = chain.Widths[level], h = chain.Heights[level];
            double fu = Math.Floor(u), fv = Math.Floor(v), fx = u - fu, fy = v - fv;
            int iu = (int)fu, iv = (int)fv;
            for (int dy = 0; dy < 2; dy++)
            {
                double wy = dy == 0 ? 1 - fy : fy; if (wy <= 0) continue;
                int ty = Wrap(iv + dy, h);
                for (int dx = 0; dx < 2; dx++)
                {
                    double wgt = weight * wy * (dx == 0 ? 1 - fx : fx); if (wgt <= 0) continue;
                    int tx = Wrap(iu + dx, w);
                    if (tx < 0 || ty < 0) { acc.Add(wgt, 0, 0, 0, 0); continue; } // 画像の外（Wrap None）: 透明な黒
                    chain.Read(level, tx, ty, out int r, out int g, out int b, out int a);
                    acc.Add(wgt, r, g, b, a);
                }
            }
        }
        /// <summary>The texel index on an axis of n texels, or −1 outside the image when the wrap is None.</summary>
        int Wrap(int i, int n)
        {
            if (none) return i < 0 || i >= n ? -1 : i;
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
            /// <summary>The weighted alpha (0..255, not rounded; 0 when nothing was read).</summary>
            public double Alpha => count == 0 ? 0 : same ? (first >> 24 & 0xff) : a;
            /// <summary>The straight colour with its alpha times scale (0..1), rounded once: the colour where the alpha rounds to 0 too
            /// (the read texels' colour; transparent pixels keep RGB).</summary>
            public Rgba32 Resolve(double scale)
            {
                if (count == 0) return Rgba32.Transparent;
                if (same) return new Rgba32((byte)first, (byte)(first >> 8), (byte)(first >> 16), MathUtil.ToByte((first >> 24 & 0xff) / 255.0 * scale));
                if (!(a > 0)) return zw > 0 ? new Rgba32(MathUtil.ToByte(zr / zw / 255), MathUtil.ToByte(zg / zw / 255), MathUtil.ToByte(zb / zw / 255), 0) : Rgba32.Transparent;
                return new Rgba32(MathUtil.ToByte(r / a / 255), MathUtil.ToByte(g / a / 255), MathUtil.ToByte(b / a / 255), MathUtil.ToByte(a / 255 * scale));
            }
        }
    }
}
