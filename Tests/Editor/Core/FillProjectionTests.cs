using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 塗りつぶしレイヤーの画像の投影（Core）: UV・トライプラナー・平面・球・円柱の画素が、この試験の中で別に組んだ参照（ミップの作り方・
    /// 双線形と三線形・足跡・投影の式・UV の変換・トライプラナーの重み）と 1 段以内で一致する（大半はバイト一致）、UV の等倍はバイト一致、
    /// 縮小はミップで均され（市松を細かく繰り返すと灰色）、オフセットを少し動かしても値が跳ばない、繰り返し / 端で止める、モデルのルートの
    /// 位置と向きに付いて行く、マップが無い・大きさが違う・ルートが分からない・テクセルが空のときは塗りつぶしの値のまま理由を出す、
    /// マップが変わると層が「変わった」になる。
    /// </summary>
    public sealed class FillProjectionTests
    {
        const int W = 48, H = 40, T = 16;
        static readonly double[] Min = { -1, -2, -.5 }, Max = { 1, 2, 1.5 };
        static readonly Rgba32 FillValue = new Rgba32(10, 200, 30, 255);
        [TearDown] public void ResetThreads() { CoreParallelism.MaxDegreeOfParallelism = 0; }

        // ───────── 場面 ─────────

        /// <summary>テクセルが表す点（Position マップの 0〜1）: 横・縦に滑らかに変わり、奥行きは波打つ。右上の角は空（UV の島の外）。</summary>
        static double[] Raw(int x, int y) => new[] { .1 + .8 * x / (W - 1.0), .05 + .9 * y / (H - 1.0), .5 + .35 * Math.Sin(x * .21 + y * .13) };
        static bool Empty(int x, int y) => x > W - 6 && y > H - 5;
        static MeshTexelCoverage Coverage(int x, int y) => Empty(x, y) ? MeshTexelCoverage.Empty : MeshTexelCoverage.Covered;
        static BakedMeshMap Positions(int w = W, int h = H) => TestMeshMaps.Make(MeshMapKind.Position, w, h, (x, y, c) => Raw(x, y)[c], Coverage, "test", Min, Max);
        /// <summary>法線（ワールド）: 場所でゆっくり向きが変わる単位ベクトル（n × 0.5 + 0.5 で入れる）。</summary>
        static double[] Normal(int x, int y)
        {
            double a = x * .13 - 1.1, b = y * .11 - .9; double nx = Math.Sin(a) * Math.Cos(b), ny = Math.Sin(b), nz = Math.Cos(a) * Math.Cos(b);
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz); return new[] { nx / l, ny / l, nz / l };
        }
        static BakedMeshMap Normals(int w = W, int h = H) => TestMeshMaps.Make(MeshMapKind.WorldNormal, w, h, (x, y, c) => Normal(x, y)[c] * .5 + .5, Coverage);

        /// <summary>位置で決まる色の画像（透明な所も RGB を持つ）。</summary>
        static ImageContent Picture(int w, int h, int seed = 3)
        {
            var rgba = new byte[w * h * 4]; var rnd = new Random(seed);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = (byte)(x * 255 / Math.Max(1, w - 1)); rgba[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1)); rgba[o + 2] = (byte)rnd.Next(256);
                rgba[o + 3] = (byte)((x + y) % 7 == 0 ? 0 : (x * 3 + y) % 5 == 0 ? 128 : 255);
            }
            return ImageContent.FromPixels(rgba, w, h);
        }

        sealed class Scene
        {
            public PaintDocument Doc; public ProjectResources Resources; public PaintLayer Fill; public ImageResource Image; public FramedInputs Inputs;
        }
        static Scene Make(ImageContent picture, ResourceColorSpace space = ResourceColorSpace.Srgb, PaintChannel channel = PaintChannel.Color, bool maps = true, int w = W, int h = H, int tile = T)
        {
            var s = new Scene { Resources = new ProjectResources(), Doc = new PaintDocument(w, h, tile) };
            s.Image = s.Resources.Add("Picture", picture, ResourceOrigin.None, space, out _);
            s.Doc.ImageResources = s.Resources;
            s.Inputs = new FramedInputs();
            if (maps) s.Inputs.Put(Positions(w, h)).Put(Normals(w, h));
            s.Doc.GeneratorInputs = s.Inputs;
            s.Fill = s.Doc.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { channel, FillValue } });
            s.Doc.SetFillImage(s.Fill.Id, channel, s.Image.Id);
            return s;
        }

        sealed class FramedInputs : IGeneratorInputs, IGeneratorModelFrame
        {
            readonly Dictionary<MeshMapKind, BakedMeshMap> maps = new Dictionary<MeshMapKind, BakedMeshMap>();
            GeneratorModelFrame frame = GeneratorModelFrame.Identity; bool unknown;
            public long Revision { get; private set; } = 1;
            public FramedInputs Put(BakedMeshMap map) { maps[map.Kind] = map; Revision++; return this; }
            public FramedInputs Remove(MeshMapKind kind) { maps.Remove(kind); Revision++; return this; }
            public FramedInputs Frame(GeneratorModelFrame f) { frame = f; unknown = false; Revision++; return this; }
            public FramedInputs Unknown() { unknown = true; Revision++; return this; }
            public GeneratorModelFrame ModelFrame => unknown ? null : frame;
            public bool TryGetMap(MeshMapKind kind, out BakedMeshMap map, out string reason)
            {
                if (maps.TryGetValue(kind, out map)) { reason = null; return true; }
                reason = kind + " has not been baked."; return false;
            }
        }

        // ───────── 参照（Core とは別の組み方） ─────────

        static double C01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
        static byte B(double v) { double x = v * 255 + .5; return x >= 255 ? (byte)255 : x > 0 ? (byte)(int)x : (byte)0; }
        static double[,] Mul(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) r[i, j] += a[i, k] * b[k, j];
            return r;
        }
        /// <summary>Unity の Quaternion.Euler の順（Ry · Rx · Rz）。</summary>
        static double[,] Rot(double ax, double ay, double az)
        {
            double a = ax * Math.PI / 180, b = ay * Math.PI / 180, c = az * Math.PI / 180;
            var rx = new double[,] { { 1, 0, 0 }, { 0, Math.Cos(a), -Math.Sin(a) }, { 0, Math.Sin(a), Math.Cos(a) } };
            var ry = new double[,] { { Math.Cos(b), 0, Math.Sin(b) }, { 0, 1, 0 }, { -Math.Sin(b), 0, Math.Cos(b) } };
            var rz = new double[,] { { Math.Cos(c), -Math.Sin(c), 0 }, { Math.Sin(c), Math.Cos(c), 0 }, { 0, 0, 1 } };
            return Mul(Mul(ry, rx), rz);
        }
        static double[] TransposeTimes(double[,] r, double[] v) { var o = new double[3]; for (int i = 0; i < 3; i++) for (int k = 0; k < 3; k++) o[i] += r[k, i] * v[k]; return o; }

        /// <summary>試験の参照: 画像のミップ（文書の決まりどおりに組む）と、画素ごとの投影と標本化。</summary>
        sealed class Reference
        {
            readonly List<Rgba32[]> levels = new List<Rgba32[]>(); readonly List<int> ws = new List<int>(), hs = new List<int>();
            readonly FillProjection p; readonly BakedMeshMap pos, nor; readonly double[,] rootRot, placeRot; readonly double[] rootPos;
            public Reference(ImageContent image, Func<Rgba32, Rgba32> convert, FillProjection projection, BakedMeshMap positions, BakedMeshMap normals, double[,] rootRotation, double[] rootPosition)
            {
                p = projection; pos = positions; nor = normals; rootRot = rootRotation; rootPos = rootPosition;
                placeRot = Rot(p.Placement.RotationX, p.Placement.RotationY, p.Placement.RotationZ);
                int w = image.Width, h = image.Height; var level = new Rgba32[w * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) level[y * w + x] = convert(image.GetPixel(x, y));
                levels.Add(level); ws.Add(w); hs.Add(h);
                while (w > 1 || h > 1)
                {
                    int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2); var next = new Rgba32[nw * nh]; var prev = level; int pw = w;
                    for (int j = 0; j < nh; j++) for (int i = 0; i < nw; i++)
                    {
                        var xs = Span(i, w, nw); var ys = Span(j, h, nh); var list = new List<(double, Rgba32)>();
                        foreach (int yy in ys) foreach (int xx in xs) list.Add((1.0 / (xs.Length * ys.Length), prev[yy * pw + xx]));
                        next[j * nw + i] = Combine(list, Rgba32.Transparent);
                    }
                    level = next; w = nw; h = nh; levels.Add(level); ws.Add(w); hs.Add(h);
                }
            }
            static int[] Span(int i, int n, int halved)
            {
                if (n == 1) return new[] { 0 };
                return i == halved - 1 && n % 2 == 1 ? new[] { 2 * i, 2 * i + 1, 2 * i + 2 } : new[] { 2 * i, 2 * i + 1 };
            }
            /// <summary>重み付きの画素の平均（プリマルチプライド。全部同じならその画素、アルファが 0 に丸まれば透明な画素の色の平均）。</summary>
            public static Rgba32 Combine(List<(double w, Rgba32 c)> list, Rgba32 fallback)
            {
                var used = list.Where(e => e.w > 0).ToList();
                if (used.Count == 0) return fallback;
                if (used.All(e => e.c == used[0].c)) return used[0].c;
                double a = used.Sum(e => e.w * e.c.A);
                byte alpha = B(a / 255);
                if (alpha == 0)
                {
                    var clear = used.Where(e => e.c.A == 0).ToList(); double zw = clear.Sum(e => e.w);
                    return zw > 0 ? new Rgba32(B(clear.Sum(e => e.w * e.c.R) / zw / 255), B(clear.Sum(e => e.w * e.c.G) / zw / 255), B(clear.Sum(e => e.w * e.c.B) / zw / 255), 0) : Rgba32.Transparent;
                }
                return new Rgba32(B(used.Sum(e => e.w * e.c.A * e.c.R) / a / 255), B(used.Sum(e => e.w * e.c.A * e.c.G) / a / 255), B(used.Sum(e => e.w * e.c.A * e.c.B) / a / 255), alpha);
            }
            int Wrap(int i, int n) => p.Wrap == FillWrap.Clamp ? Math.Max(0, Math.Min(n - 1, i)) : ((i % n) + n) % n;
            void Bilinear(int k, double u, double v, double weight, List<(double, Rgba32)> acc)
            {
                int iu = (int)Math.Floor(u), iv = (int)Math.Floor(v); double fx = u - iu, fy = v - iv;
                for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                        acc.Add((weight * (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy), levels[k][Wrap(iv + dy, hs[k]) * ws[k] + Wrap(iu + dx, ws[k])]));
            }
            /// <summary>UV の変換の後の (s′, t′) を、足跡 ρ（段 0 のテクセル）で読む。</summary>
            void Sample(double sp, double tp, double rho, double weight, List<(double, Rgba32)> acc)
            {
                int last = levels.Count - 1;
                if (!(rho > 1) || last == 0) { Bilinear(0, sp * ws[0] - .5, tp * hs[0] - .5, weight, acc); return; }
                double lod = Math.Log(rho, 2);
                if (lod >= last) { Bilinear(last, sp * ws[last] - .5, tp * hs[last] - .5, weight, acc); return; }
                int k = (int)Math.Floor(lod); double f = lod - k;
                Bilinear(k, sp * ws[k] - .5, tp * hs[k] - .5, weight * (1 - f), acc);
                Bilinear(k + 1, sp * ws[k + 1] - .5, tp * hs[k + 1] - .5, weight * f, acc);
            }
            /// <summary>(s, t) → (s′, t′): 中心のまわりに −Rotation 回してから Tiles 倍、Offset。</summary>
            (double, double) Transform(double s, double t)
            {
                double a = -p.Rotation * Math.PI / 180, ds = s - .5, dt = t - .5;
                double rs = Math.Cos(a) * ds - Math.Sin(a) * dt + .5, rt = Math.Sin(a) * ds + Math.Cos(a) * dt + .5;
                return (p.TileU * rs + p.OffsetU, p.TileV * rt + p.OffsetV);
            }
            /// <summary>微小な (ds, dt) の UV の変換による像（段 0 のテクセル）。</summary>
            (double, double) Linear(double ds, double dt)
            {
                double a = -p.Rotation * Math.PI / 180;
                return (ws[0] * p.TileU * (Math.Cos(a) * ds - Math.Sin(a) * dt), hs[0] * p.TileV * (Math.Sin(a) * ds + Math.Cos(a) * dt));
            }
            void SampleWithFootprint(double s, double t, (double s, double t) dx, (double s, double t) dy, double weight, List<(double, Rgba32)> acc)
            {
                var (sp, tp) = Transform(s, t); var (cx, cy) = Linear(dx.s, dx.t); var (ex, ey) = Linear(dy.s, dy.t);
                Sample(sp, tp, Math.Max(Math.Sqrt(cx * cx + cy * cy), Math.Sqrt(ex * ex + ey * ey)), weight, acc);
            }
            /// <summary>置き場の空間の点（マップの点 → スナップショットの空間 → ルートの空間 → 置き場の空間）。</summary>
            double[] L(int x, int y)
            {
                var p0 = new double[3];
                for (int c = 0; c < 3; c++) p0[c] = Min[c] + pos.RawValue(x, y, c) / 65535.0 * (Max[c] - Min[c]);
                var root = TransposeTimes(rootRot, new[] { p0[0] - rootPos[0], p0[1] - rootPos[1], p0[2] - rootPos[2] });
                var v = p.Placement;
                return TransposeTimes(placeRot, new[] { root[0] - v.CenterX, root[1] - v.CenterY, root[2] - v.CenterZ });
            }
            bool Covered(int x, int y) => x >= 0 && y >= 0 && x < pos.Width && y < pos.Height && pos.CoverageAt(x, y) != MeshTexelCoverage.Empty;
            (double s, double t) Base(FillProjectionMode mode, double[] l, int axis, bool positive)
            {
                var v = p.Placement;
                switch (mode)
                {
                    case FillProjectionMode.Planar: return (l[0] / v.SizeX + .5, l[1] / v.SizeY + .5);
                    case FillProjectionMode.Spherical:
                    {
                        double r = Math.Sqrt(l[0] * l[0] + l[1] * l[1] + l[2] * l[2]);
                        return r > 0 ? (Math.Atan2(l[0], -l[2]) / (2 * Math.PI) + .5, Math.Asin(C01((l[1] / r + 1) / 2) * 2 - 1) / Math.PI + .5) : (.5, .5);
                    }
                    case FillProjectionMode.Cylindrical: return (Math.Atan2(l[0], -l[2]) / (2 * Math.PI) + .5, l[1] / v.SizeY + .5);
                    default: // トライプラナーの面: 外から見て正しい向き
                        if (axis == 0) return ((positive ? l[2] : -l[2]) / v.SizeZ + .5, l[1] / v.SizeY + .5);
                        if (axis == 1) return ((positive ? l[0] : -l[0]) / v.SizeX + .5, l[2] / v.SizeZ + .5);
                        return ((positive ? -l[0] : l[0]) / v.SizeX + .5, l[1] / v.SizeY + .5);
                }
            }
            /// <summary>足跡の差分の隣: 両側のうち覆われていてモデルの上で近いほう（同じなら前）。</summary>
            (int x, int y, int sign)? Neighbor(int x, int y, int dx, int dy, double[] l)
            {
                (int, int, int)? best = null; double bestD = double.PositiveInfinity;
                foreach (int side in new[] { 1, -1 })
                {
                    int nx = x + dx * side, ny = y + dy * side;
                    if (!Covered(nx, ny)) continue;
                    var q = L(nx, ny); double d = (q[0] - l[0]) * (q[0] - l[0]) + (q[1] - l[1]) * (q[1] - l[1]) + (q[2] - l[2]) * (q[2] - l[2]);
                    if (d < bestD) { bestD = d; best = (nx, ny, side); }
                }
                return best;
            }
            (double s, double t) Diff(FillProjectionMode mode, (double s, double t) at, (int x, int y, int sign)? n, int axis, bool positive)
            {
                if (n == null) return (0, 0);
                var b = Base(mode, L(n.Value.x, n.Value.y), axis, positive); double ds = b.s - at.s;
                if (mode == FillProjectionMode.Spherical || mode == FillProjectionMode.Cylindrical) ds -= Math.Round(ds);
                return (n.Value.sign * ds, n.Value.sign * (b.t - at.t));
            }
            public Rgba32 Pixel(int x, int y, Rgba32 fallback)
            {
                var acc = new List<(double, Rgba32)>();
                if (p.Mode == FillProjectionMode.Uv)
                {
                    SampleWithFootprint((x + .5) / W, (y + .5) / H, (1.0 / W, 0), (0, 1.0 / H), 1, acc);
                    return Combine(acc, fallback);
                }
                if (!Covered(x, y)) return fallback;
                var l = L(x, y); var nxN = Neighbor(x, y, 1, 0, l); var nyN = Neighbor(x, y, 0, 1, l);
                if (p.Mode != FillProjectionMode.Triplanar)
                {
                    var at = Base(p.Mode, l, 0, true);
                    SampleWithFootprint(at.s, at.t, Diff(p.Mode, at, nxN, 0, true), Diff(p.Mode, at, nyN, 0, true), 1, acc);
                    return Combine(acc, fallback);
                }
                var raw = new double[3]; for (int c = 0; c < 3; c++) raw[c] = nor.RawValue(x, y, c) / 65535.0 * 2 - 1;
                var n = TransposeTimes(placeRot, TransposeTimes(rootRot, raw));
                double most = n.Max(Math.Abs), keep = 1 - p.BlendWidth;
                var weights = n.Select(c => Math.Max(0, Math.Abs(c) - keep * most)).ToArray(); double total = weights.Sum();
                if (!(total > 0)) { weights = n.Select(c => Math.Abs(c) >= most ? 1.0 : 0).ToArray(); total = weights.Sum(); }
                for (int axis = 0; axis < 3; axis++)
                {
                    if (weights[axis] <= 0) continue;
                    bool positive = n[axis] >= 0; var at = Base(p.Mode, l, axis, positive);
                    SampleWithFootprint(at.s, at.t, Diff(p.Mode, at, nxN, axis, positive), Diff(p.Mode, at, nyN, axis, positive), weights[axis] / total, acc);
                }
                return Combine(acc, fallback);
            }
        }

        static double[,] Identity3 => new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        static void AssertMatches(PaintLayer fill, PaintChannel channel, Reference reference, string what, int minimumExactPercent = 97)
        {
            var got = fill.EvaluateOutputRegion(channel, 0, 0, W, H);
            int exact = 0, compared = 0;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                var e = reference.Pixel(x, y, FillValue); int o = (y * W + x) * 4;
                var g = new Rgba32(got[o], got[o + 1], got[o + 2], got[o + 3]);
                compared++;
                if (g == e) { exact++; continue; }
                Assert.That(Math.Abs(g.A - e.A), Is.LessThanOrEqualTo(1), what + " alpha at " + x + "," + y + ": " + g + " vs " + e);
                Assert.That(Math.Max(Math.Abs(g.R - e.R), Math.Max(Math.Abs(g.G - e.G), Math.Abs(g.B - e.B))), Is.LessThanOrEqualTo(1), what + " at " + x + "," + y + ": " + g + " vs " + e);
            }
            Assert.That(exact * 100, Is.GreaterThanOrEqualTo(compared * minimumExactPercent), what + ": " + exact + " of " + compared + " exact (only floating-point rounding may differ)");
        }

        // ───────── 投影ごとの値 ─────────

        [Test] public void UvAtTheSameSizeIsTheImageByteForByteWithTransparentRgb()
        {
            var picture = Picture(W, H);
            var s = Make(picture, maps: false);
            var output = s.Fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            Assert.That(output, Is.EqualTo(picture.CopyPixels()), "one texel per pixel: the stored bytes, transparent pixels' RGB included");
            var tile = new byte[T * T * 4];
            Assert.That(s.Fill.CopyTile(PaintChannel.Color, new TileCoord(1, 1), tile), Is.True);
            for (int y = 0; y < T; y++) for (int x = 0; x < T; x++)
                Assert.That(new Rgba32(tile[(y * T + x) * 4], tile[(y * T + x) * 4 + 1], tile[(y * T + x) * 4 + 2], tile[(y * T + x) * 4 + 3]), Is.EqualTo(picture.GetPixel(T + x, T + y)));
            Assert.That(s.Fill.GetPixel(PaintChannel.Color, 5, 7), Is.EqualTo(picture.GetPixel(5, 7)));
            var status = s.Doc.GetFillImageStatus(s.Fill.Id, PaintChannel.Color);
            Assert.That(status.Active, Is.True); Assert.That(status.Image, Is.SameAs(s.Image)); Assert.That(status.Reason, Is.Null);
            Assert.That(status.MipLevels, Is.EqualTo(6), "48×40 → 24×20 → 12×10 → 6×5 → 3×2 → 1×1");
            // 合成: 不透明な画素はそのまま（透明の上に Normal で 1 枚）
            var composite = s.Doc.Composite(PaintChannel.Color);
            for (int i = 0; i < composite.Length; i += 4) if (output[i + 3] == 255) Assert.That(composite.Skip(i).Take(4), Is.EqualTo(output.Skip(i).Take(4)));
        }

        [Test] public void UvTilingOffsetRotationAndWrapMatchTheReference()
        {
            var picture = Picture(32, 24);
            var s = Make(picture, maps: false);
            var cases = new[]
            {
                FillProjection.Default.WithTiles(2, 3),
                FillProjection.Default.WithTiles(.5, .75).WithOffset(.3, -.2),
                FillProjection.Default.WithRotation(30).WithTiles(1.5, 1.5),
                FillProjection.Default.WithRotation(-90).WithOffset(.25, .5).WithWrap(FillWrap.Clamp).WithTiles(1.3, .8),
                FillProjection.Default.WithTiles(7, 5).WithRotation(12), // 縮小（三線形）
                FillProjection.Default.WithTiles(40, 40), // 最後の段まで
            };
            foreach (var p in cases)
            {
                s.Doc.SetFillProjection(s.Fill.Id, p);
                AssertMatches(s.Fill, PaintChannel.Color, new Reference(picture, c => c, p, null, null, Identity3, new double[3]), p.ToString());
            }
        }

        [Test] public void ModelProjectionsMatchTheReferenceWithARotatedRootAndPlacement()
        {
            var picture = Picture(40, 32, 11);
            var s = Make(picture);
            double cosHalf = Math.Cos(Math.PI / 12); // Y で 30° の四元数
            var frames = new[] { (GeneratorModelFrame.Identity, Identity3, new double[3]), (new GeneratorModelFrame(.3, -.2, .1, 0, Math.Sin(Math.PI / 12), 0, cosHalf), Rot(0, 30, 0), new[] { .3, -.2, .1 }) };
            var placements = new[]
            {
                new ShapeVolume(GeneratorShape.Box, .1, -.2, .3, 0, 0, 0, 1.6, 2.4, 1.2, 0),
                new ShapeVolume(GeneratorShape.Box, -.2, .3, .4, 20, -35, 10, .9, 1.7, 1.3, 0),
            };
            var modes = new[] { FillProjectionMode.Planar, FillProjectionMode.Spherical, FillProjectionMode.Cylindrical, FillProjectionMode.Triplanar };
            foreach (var (frame, rotation, position) in frames)
            {
                s.Inputs.Frame(frame); s.Doc.RefreshGeneratorInputs(); // 合成の入り口と同じく、入力を読み直してから評価する
                foreach (var v in placements)
                    foreach (var mode in modes)
                        foreach (var p in new[] { FillProjection.Default.WithMode(mode).WithPlacement(v), FillProjection.Default.WithMode(mode).WithPlacement(v).WithTiles(3, 2).WithRotation(25).WithOffset(.1, .2).WithBlendWidth(.7) })
                        {
                            s.Doc.SetFillProjection(s.Fill.Id, p);
                            AssertMatches(s.Fill, PaintChannel.Color, new Reference(picture, c => c, p, s.Inputs.TryGetMap(MeshMapKind.Position, out var pm, out _) ? pm : null,
                                s.Inputs.TryGetMap(MeshMapKind.WorldNormal, out var nm, out _) ? nm : null, rotation, position), mode + " " + p + " root " + position[0]);
                        }
            }
        }

        [Test] public void TriplanarBlendWidthZeroTakesOneAxisAndOneMixesByTheNormal()
        {
            var picture = Picture(32, 32, 5);
            var s = Make(picture);
            var v = new ShapeVolume(GeneratorShape.Box, 0, 0, .5, 0, 0, 0, 1, 1, 1, 0);
            foreach (double width in new[] { 0, .3, 1 })
            {
                var p = FillProjection.Default.WithMode(FillProjectionMode.Triplanar).WithPlacement(v).WithBlendWidth(width);
                s.Doc.SetFillProjection(s.Fill.Id, p);
                AssertMatches(s.Fill, PaintChannel.Color, new Reference(picture, c => c, p, s.Inputs.TryGetMap(MeshMapKind.Position, out var pm, out _) ? pm : null,
                    s.Inputs.TryGetMap(MeshMapKind.WorldNormal, out var nm, out _) ? nm : null, Identity3, new double[3]), "blend width " + width);
            }
        }

        // ───────── 縮小・繰り返し ─────────

        [Test] public void MinificationReadsTheMipmapsSoAFineCheckerTurnsGreyAndOffsetsDoNotJump()
        {
            // 1 画素の白黒の市松を 12 回繰り返す（文書の 1 画素に画像の 8 × 8 テクセル）
            int size = 32; var rgba = new byte[size * size * 4];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { byte c = (byte)((x + y) % 2 == 0 ? 255 : 0); int o = (y * size + x) * 4; rgba[o] = rgba[o + 1] = rgba[o + 2] = c; rgba[o + 3] = 255; }
            var checker = ImageContent.FromPixels(rgba, size, size);
            var s = Make(checker, maps: false);
            s.Doc.SetFillProjection(s.Fill.Id, FillProjection.Default.WithTiles(12, 12));
            var a = s.Fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            for (int i = 0; i < a.Length; i += 4) Assert.That((int)a[i], Is.InRange(120, 135), "an average, not a black or white texel (no aliasing) at " + i / 4);
            // オフセットを 1/100 テクセルずつ動かしても、値はほとんど変わらない（ちらつかない）
            s.Doc.SetFillProjection(s.Fill.Id, FillProjection.Default.WithTiles(12, 12).WithOffset(.0003, .0002));
            var b = s.Fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            int worst = 0; for (int i = 0; i < a.Length; i += 4) worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
            Assert.That(worst, Is.LessThanOrEqualTo(3), "a tiny offset changes a minified pixel only a little");
            // 拡大（Tiles < 1）は段 0 の双線形: 隣どうしの差は小さい
            s.Doc.SetFillProjection(s.Fill.Id, FillProjection.Default.WithTiles(.25, .25));
            var status = s.Doc.GetFillImageStatus(s.Fill.Id, PaintChannel.Color);
            Assert.That(status.MipLevels, Is.EqualTo(6)); Assert.That(status.MipBytes, Is.EqualTo((16 * 16 + 8 * 8 + 4 * 4 + 2 * 2 + 1) * 4L));
        }

        [Test] public void RepeatWrapsAroundTheImageAndClampContinuesItsEdge()
        {
            var picture = Picture(16, 16, 9);
            var s = Make(picture, maps: false);
            s.Doc.SetFillProjection(s.Fill.Id, FillProjection.Default.WithTiles(1, 1).WithOffset(.5, 0));
            var repeated = s.Fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            s.Doc.SetFillProjection(s.Fill.Id, FillProjection.Default.WithTiles(1, 1).WithOffset(.5, 0).WithWrap(FillWrap.Clamp));
            var clamped = s.Fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            // 右の端の列: 繰り返しは画像の左の列へ戻り、端で止めるは右の列のまま
            int x = W - 1, y = H / 2;
            Assert.That(repeated, Is.Not.EqualTo(clamped));
            var right = picture.GetPixel(15, 0);
            int o = (y * W + x) * 4;
            Assert.That(clamped[o], Is.EqualTo(right.R).Within(40), "clamp keeps the right edge's colour (R grows to the right)");
            Assert.That(repeated[o], Is.LessThan(clamped[o]), "repeat comes back round to the image's middle (smaller R)");
        }

        // ───────── マップが無い・古い・ルート ─────────

        [Test] public void MissingMapsOtherSizesAndAnUnknownRootShowTheFillValueWithAReason()
        {
            var picture = Picture(16, 16);
            var s = Make(picture);
            s.Doc.SetFillProjection(s.Fill.Id, FillProjection.Default.WithMode(FillProjectionMode.Triplanar));
            Assert.That(s.Doc.GetFillImageStatus(s.Fill.Id, PaintChannel.Color).Active, Is.True);
            s.Inputs.Remove(MeshMapKind.WorldNormal);
            var status = s.Doc.GetFillImageStatus(s.Fill.Id, PaintChannel.Color);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("WorldNormal"));
            var output = s.Fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            for (int i = 0; i < output.Length; i += 4) Assert.That(new Rgba32(output[i], output[i + 1], output[i + 2], output[i + 3]), Is.EqualTo(FillValue), "the fill value, not black");
            Assert.That(s.Doc.InactiveFillImages().Single(), Does.Contain("'Fill' (Color)").And.Contain("WorldNormal"));
            s.Doc.SetFillProjection(s.Fill.Id, FillProjection.Default.WithMode(FillProjectionMode.Planar)); // 平面は Position だけ
            Assert.That(s.Doc.GetFillImageStatus(s.Fill.Id, PaintChannel.Color).Active, Is.True);
            s.Inputs.Put(Positions(W / 2, H / 2));
            Assert.That(s.Doc.GetFillImageStatus(s.Fill.Id, PaintChannel.Color).Reason, Does.Contain("Position").And.Contain("24×20"));
            s.Inputs.Put(Positions()).Unknown();
            Assert.That(s.Doc.GetFillImageStatus(s.Fill.Id, PaintChannel.Color).Reason, Does.Contain("model root"));
            s.Inputs.Frame(GeneratorModelFrame.Identity);
            Assert.That(s.Doc.InactiveFillImages(), Is.Empty);
            // 空のテクセル（UV の島の外）は塗りつぶしの値
            output = s.Fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            int at = ((H - 1) * W + W - 1) * 4;
            Assert.That(new Rgba32(output[at], output[at + 1], output[at + 2], output[at + 3]), Is.EqualTo(FillValue));
            // UV はマップを読まない
            s.Inputs.Remove(MeshMapKind.Position);
            s.Doc.SetFillProjection(s.Fill.Id, FillProjection.Default);
            Assert.That(s.Doc.GetFillImageStatus(s.Fill.Id, PaintChannel.Color).Active, Is.True);
        }

        [Test] public void ANewBakeOrAMovedRootMarksTheLayerChangedAndTheProjectionFollowsTheRoot()
        {
            var picture = Picture(24, 24, 2);
            var s = Make(picture);
            var p = FillProjection.Default.WithMode(FillProjectionMode.Planar).WithPlacement(new ShapeVolume(GeneratorShape.Box, 0, 0, .5, 0, 0, 0, 1.5, 2, 1, 0));
            s.Doc.SetFillProjection(s.Fill.Id, p);
            var before = s.Doc.Composite(PaintChannel.Color);
            long serial = s.Doc.ChangeSerial; var changed = new HashSet<TileCoord>();
            // ルートを x に 0.2 動かし、置き場も同じだけ（ルートの空間で −0.2）動かすと、同じ絵
            s.Inputs.Frame(new GeneratorModelFrame(.2, 0, 0, 0, 0, 0, 1));
            Assert.That(s.Doc.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True);
            Assert.That(changed.Count, Is.EqualTo(3 * 3), "a moved root redraws every tile of the projected layer");
            Assert.That(s.Doc.Composite(PaintChannel.Color), Is.Not.EqualTo(before));
            s.Doc.SetFillProjection(s.Fill.Id, p.WithPlacement(p.Placement.WithCenter(-.2, 0, .5)));
            var moved = s.Doc.Composite(PaintChannel.Color);
            int differ = 0; for (int i = 0; i < moved.Length; i++) if (Math.Abs(moved[i] - before[i]) > 1) differ++;
            Assert.That(differ, Is.EqualTo(0), "the projection is placed in the root's space");
        }
    }
}
