using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// デカール（Core）: 塗りつぶしの層の投影の種類「デカール」の画素が、この試験の中で別に組んだ参照（箱での切り抜き・奥行きの縁・面の向きの
    /// 縁・画像の外の透明・形の画像のアルファ・値のチャンネル・1 回の丸め）と 1 段以内で一致する（大半はバイト一致）。箱の外・裏向き・空のテクセルは
    /// 透明、置けない（マップが無い・大きさが違う・ルートが分からない）ときは全部透明で理由（全面に値を出さない）。動かしたときは前後の箱の届く
    /// タイルだけが「変わった」になり、届かないタイルは評価しない。Undo / ドラッグのまとめと取り消し、型・範囲・ロック・予算の拒否、複製・大きさの
    /// 変更・スマートマテリアル・統合（置けないデカールは断る）、並列の数とタイルの大きさによらないバイト、正本の版の往復（バイト一致）と古い版での
    /// 拒否、デカールの無い文書は版 16 と同じ並び、PSD は画素の層として書いて知らせる。
    /// </summary>
    public sealed class DecalTests
    {
        const int W = 48, H = 40, T = 16;
        static readonly double[] Min = { -1, -2, -.5 }, Max = { 1, 2, 1.5 };
        static readonly Rgba32 White = new Rgba32(255, 255, 255, 255);
        [TearDown] public void ResetThreads() { CoreParallelism.MaxDegreeOfParallelism = 0; }

        // ───────── 場面（FillProjectionTests と同じ型: 横・縦に滑らかで奥行きが波打つ面、右上の角は空、法線は場所で大きく向きを変える） ─────────

        static double[] Raw(int x, int y) => new[] { .1 + .8 * x / (W - 1.0), .05 + .9 * y / (H - 1.0), .5 + .35 * Math.Sin(x * .21 + y * .13) };
        static bool Empty(int x, int y) => x > W - 6 && y > H - 5;
        static MeshTexelCoverage Coverage(int x, int y) => Empty(x, y) ? MeshTexelCoverage.Empty : MeshTexelCoverage.Covered;
        static BakedMeshMap Positions(int w = W, int h = H) => TestMeshMaps.Make(MeshMapKind.Position, w, h, (x, y, c) => Raw(x, y)[c], Coverage, "test", Min, Max);
        static double[] Normal(int x, int y)
        {
            double a = x * .13 - 1.1, b = y * .11 - .9; double nx = Math.Sin(a) * Math.Cos(b), ny = Math.Sin(b), nz = Math.Cos(a) * Math.Cos(b);
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz); return new[] { nx / l, ny / l, nz / l };
        }
        static BakedMeshMap Normals(int w = W, int h = H) => TestMeshMaps.Make(MeshMapKind.WorldNormal, w, h, (x, y, c) => Normal(x, y)[c] * .5 + .5, Coverage);

        /// <summary>位置で決まる色の画像。透明（RGB あり）・半透明・不透明が混ざる。</summary>
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
        /// <summary>不透明な灰色の画像（Height など）。</summary>
        static ImageContent Grey(int w, int h)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int o = (y * w + x) * 4; rgba[o] = rgba[o + 1] = rgba[o + 2] = (byte)((x * 7 + y * 13) % 256); rgba[o + 3] = 255; }
            return ImageContent.FromPixels(rgba, w, h);
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

        static readonly ShapeVolume Placement = new ShapeVolume(GeneratorShape.Box, 0, 0, .5, 0, 0, 0, 1.2, 2.4, 1, 0);

        sealed class Scene
        {
            public PaintDocument Doc; public ProjectResources Resources; public FramedInputs Inputs; public PaintLayer Decal; public ImageResource Image;
        }
        /// <summary>灰色の下地の層の上に、Color に画像を持つデカール。</summary>
        static Scene Make(ImageContent picture = null, bool maps = true, int w = W, int h = H, int tile = T, FillProjection projection = null)
        {
            var s = new Scene { Resources = new ProjectResources(), Doc = new PaintDocument(w, h, tile), Inputs = new FramedInputs() };
            s.Image = s.Resources.Add("Logo", picture ?? Picture(40, 32), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            s.Doc.ImageResources = s.Resources;
            if (maps) s.Inputs.Put(Positions(w, h)).Put(Normals(w, h));
            s.Doc.GeneratorInputs = s.Inputs;
            s.Doc.AddFillLayer("Under", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(90, 90, 90, 255) }, { PaintChannel.Roughness, new Rgba32(200, 200, 200, 255) } });
            s.Decal = s.Doc.AddFillLayer("Decal", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, White } });
            s.Doc.SetFillImage(s.Decal.Id, PaintChannel.Color, s.Image.Id);
            s.Doc.SetFillProjection(s.Decal.Id, projection ?? FillProjection.DecalAt(Placement));
            s.Doc.ClearHistory();
            return s;
        }

        // ───────── 参照（Core とは別の組み方。FillProjectionTests の参照と同じ決まりで、デカールの式を足したもの） ─────────

        static byte B(double v) { double x = v * 255 + .5; return x >= 255 ? (byte)255 : x > 0 ? (byte)(int)x : (byte)0; }
        static double[,] Mul(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) r[i, j] += a[i, k] * b[k, j];
            return r;
        }
        static double[,] Rot(double ax, double ay, double az)
        {
            double a = ax * Math.PI / 180, b = ay * Math.PI / 180, c = az * Math.PI / 180;
            var rx = new double[,] { { 1, 0, 0 }, { 0, Math.Cos(a), -Math.Sin(a) }, { 0, Math.Sin(a), Math.Cos(a) } };
            var ry = new double[,] { { Math.Cos(b), 0, Math.Sin(b) }, { 0, 1, 0 }, { -Math.Sin(b), 0, Math.Cos(b) } };
            var rz = new double[,] { { Math.Cos(c), -Math.Sin(c), 0 }, { Math.Sin(c), Math.Cos(c), 0 }, { 0, 0, 1 } };
            return Mul(Mul(ry, rx), rz);
        }
        static double[] TransposeTimes(double[,] r, double[] v) { var o = new double[3]; for (int i = 0; i < 3; i++) for (int k = 0; k < 3; k++) o[i] += r[k, i] * v[k]; return o; }
        static double[,] Identity3 => new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        /// <summary>画像のミップ（文書の決まりどおり: 段ごとに半分、面積平均、プリマルチプライド）と、UV の変換・繰り返しで読む標本。</summary>
        sealed class Mips
        {
            readonly List<Rgba32[]> levels = new List<Rgba32[]>(); readonly List<int> ws = new List<int>(), hs = new List<int>(); readonly FillProjection p;
            public Mips(ImageContent image, FillProjection projection)
            {
                p = projection; int w = image.Width, h = image.Height; var level = new Rgba32[w * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) level[y * w + x] = image.GetPixel(x, y);
                levels.Add(level); ws.Add(w); hs.Add(h);
                while (w > 1 || h > 1)
                {
                    int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2); var next = new Rgba32[nw * nh]; var prev = level; int pw = w;
                    for (int j = 0; j < nh; j++) for (int i = 0; i < nw; i++)
                    {
                        var xs = Span(i, w, nw); var ys = Span(j, h, nh); var list = new List<(double, Rgba32)>();
                        foreach (int yy in ys) foreach (int xx in xs) list.Add((1.0 / (xs.Length * ys.Length), prev[yy * pw + xx]));
                        next[j * nw + i] = Combine(list, 1);
                    }
                    level = next; w = nw; h = nh; levels.Add(level); ws.Add(w); hs.Add(h);
                }
            }
            static int[] Span(int i, int n, int halved) => n == 1 ? new[] { 0 } : i == halved - 1 && n % 2 == 1 ? new[] { 2 * i, 2 * i + 1, 2 * i + 2 } : new[] { 2 * i, 2 * i + 1 };
            Rgba32 Texel(int k, int x, int y)
            {
                int w = ws[k], h = hs[k];
                if (p.Wrap == FillWrap.None) return x < 0 || y < 0 || x >= w || y >= h ? Rgba32.Transparent : levels[k][y * w + x];
                if (p.Wrap == FillWrap.Clamp) return levels[k][Math.Max(0, Math.Min(h - 1, y)) * w + Math.Max(0, Math.Min(w - 1, x))];
                return levels[k][((y % h) + h) % h * w + ((x % w) + w) % w];
            }
            void Bilinear(int k, double u, double v, double weight, List<(double, Rgba32)> acc)
            {
                int iu = (int)Math.Floor(u), iv = (int)Math.Floor(v); double fx = u - iu, fy = v - iv;
                for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                    acc.Add((weight * (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy), Texel(k, iu + dx, iv + dy)));
            }
            /// <summary>(s, t) とピクセルあたりの微分（平面の投影の値）を、UV の変換を通して足跡で読む。</summary>
            public List<(double, Rgba32)> Sample(double s, double t, (double s, double t) dx, (double s, double t) dy)
            {
                double a = -p.Rotation * Math.PI / 180;
                (double, double) Transform(double ss, double tt) { double ds = ss - .5, dt = tt - .5; return (p.TileU * (Math.Cos(a) * ds - Math.Sin(a) * dt + .5) + p.OffsetU, p.TileV * (Math.Sin(a) * ds + Math.Cos(a) * dt + .5) + p.OffsetV); }
                (double, double) Linear(double ds, double dt) => (ws[0] * p.TileU * (Math.Cos(a) * ds - Math.Sin(a) * dt), hs[0] * p.TileV * (Math.Sin(a) * ds + Math.Cos(a) * dt));
                var (sp, tp) = Transform(s, t); var (cx, cy) = Linear(dx.s, dx.t); var (ex, ey) = Linear(dy.s, dy.t);
                double rho = Math.Max(Math.Sqrt(cx * cx + cy * cy), Math.Sqrt(ex * ex + ey * ey));
                var acc = new List<(double, Rgba32)>(); int last = levels.Count - 1;
                if (!(rho > 1) || last == 0) { Bilinear(0, sp * ws[0] - .5, tp * hs[0] - .5, 1, acc); return acc; }
                double lod = Math.Log(rho, 2);
                if (lod >= last) { Bilinear(last, sp * ws[last] - .5, tp * hs[last] - .5, 1, acc); return acc; }
                int k = (int)Math.Floor(lod); double f = lod - k;
                Bilinear(k, sp * ws[k] - .5, tp * hs[k] - .5, 1 - f, acc);
                Bilinear(k + 1, sp * ws[k + 1] - .5, tp * hs[k + 1] - .5, f, acc);
                return acc;
            }
        }
        /// <summary>重み付きの画素の平均（プリマルチプライド）。アルファは scale 倍してから 1 回だけ丸める。全部同じならその画素、アルファが 0 なら
        /// 透明な画素の色の平均。</summary>
        static Rgba32 Combine(List<(double w, Rgba32 c)> list, double scale)
        {
            var used = list.Where(e => e.w > 0).ToList();
            if (used.Count == 0) return Rgba32.Transparent;
            if (used.All(e => e.c == used[0].c)) return new Rgba32(used[0].c.R, used[0].c.G, used[0].c.B, B(used[0].c.A / 255.0 * scale));
            double a = used.Sum(e => e.w * e.c.A);
            if (!(a > 0))
            {
                double zw = used.Sum(e => e.w);
                return new Rgba32(B(used.Sum(e => e.w * e.c.R) / zw / 255), B(used.Sum(e => e.w * e.c.G) / zw / 255), B(used.Sum(e => e.w * e.c.B) / zw / 255), 0);
            }
            if (scale == 1 && B(a / 255) == 0) // ミップを作るときの決まり（アルファが 0 に丸まれば透明な画素の色）
            {
                var clear = used.Where(e => e.c.A == 0).ToList(); double zw = clear.Sum(e => e.w);
                return zw > 0 ? new Rgba32(B(clear.Sum(e => e.w * e.c.R) / zw / 255), B(clear.Sum(e => e.w * e.c.G) / zw / 255), B(clear.Sum(e => e.w * e.c.B) / zw / 255), 0) : Rgba32.Transparent;
            }
            return new Rgba32(B(used.Sum(e => e.w * e.c.A * e.c.R) / a / 255), B(used.Sum(e => e.w * e.c.A * e.c.G) / a / 255), B(used.Sum(e => e.w * e.c.A * e.c.B) / a / 255), B(a / 255 * scale));
        }
        static double Alpha(List<(double w, Rgba32 c)> list)
        {
            var used = list.Where(e => e.w > 0).ToList();
            if (used.Count == 0) return 0;
            return used.All(e => e.c == used[0].c) ? used[0].c.A : used.Sum(e => e.w * e.c.A);
        }

        /// <summary>デカールの参照: テクセルの置き場の空間の点・覆い（箱・奥行き・面の向き）・形・自分の画像か値。</summary>
        sealed class Reference
        {
            readonly FillProjection p; readonly BakedMeshMap pos, nor; readonly double[,] rootRot, placeRot; readonly double[] rootPos;
            public Reference(FillProjection projection, BakedMeshMap positions, BakedMeshMap normals, double[,] rootRotation, double[] rootPosition)
            { p = projection; pos = positions; nor = normals; rootRot = rootRotation; rootPos = rootPosition; placeRot = Rot(p.Placement.RotationX, p.Placement.RotationY, p.Placement.RotationZ); }
            double[] L(int x, int y)
            {
                var p0 = new double[3];
                for (int c = 0; c < 3; c++) p0[c] = Min[c] + pos.RawValue(x, y, c) / 65535.0 * (Max[c] - Min[c]);
                var root = TransposeTimes(rootRot, new[] { p0[0] - rootPos[0], p0[1] - rootPos[1], p0[2] - rootPos[2] });
                var v = p.Placement;
                return TransposeTimes(placeRot, new[] { root[0] - v.CenterX, root[1] - v.CenterY, root[2] - v.CenterZ });
            }
            bool Covered(int x, int y) => x >= 0 && y >= 0 && x < pos.Width && y < pos.Height && pos.CoverageAt(x, y) != MeshTexelCoverage.Empty;
            (double s, double t) Base(double[] l) => (l[0] / p.Placement.SizeX + .5, l[1] / p.Placement.SizeY + .5);
            (double s, double t) Diff((double s, double t) at, double[] l, int x, int y, int dx, int dy)
            {
                (int, int, int)? best = null; double bestD = double.PositiveInfinity; double[] bestL = null;
                foreach (int side in new[] { 1, -1 })
                {
                    int nx = x + dx * side, ny = y + dy * side;
                    if (!Covered(nx, ny)) continue;
                    var q = L(nx, ny); double d = (q[0] - l[0]) * (q[0] - l[0]) + (q[1] - l[1]) * (q[1] - l[1]) + (q[2] - l[2]) * (q[2] - l[2]);
                    if (d < bestD) { bestD = d; best = (nx, ny, side); bestL = q; }
                }
                if (best == null) return (0, 0);
                var b = Base(bestL); return (best.Value.Item3 * (b.s - at.s), best.Value.Item3 * (b.t - at.t));
            }
            /// <summary>覆い c（0 なら透明）。</summary>
            public double Cover(int x, int y, out double[] l)
            {
                l = null;
                if (!Covered(x, y) || nor.CoverageAt(x, y) == MeshTexelCoverage.Empty) return 0;
                l = L(x, y); var v = p.Placement;
                if (Math.Abs(l[0]) > v.SizeX / 2 || Math.Abs(l[1]) > v.SizeY / 2 || Math.Abs(l[2]) > v.SizeZ / 2) return 0;
                double d = Math.Abs(l[2]) / (v.SizeZ / 2), band = 1 - p.DepthHardness, c = band <= 0 || d <= 1 - band ? 1 : (1 - d) / band;
                var raw = new double[3]; for (int k = 0; k < 3; k++) raw[k] = nor.RawValue(x, y, k) / 65535.0 * 2 - 1;
                var n = TransposeTimes(placeRot, TransposeTimes(rootRot, raw)); double length = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (!(length > 1e-9)) return 0;
                double angle = Math.Acos(Math.Max(-1, Math.Min(1, -n[2] / length))) * 180 / Math.PI, theta = p.BackfaceAngle, bandB = (1 - p.BackfaceHardness) * theta;
                if (angle > theta) return 0;
                if (bandB > 0 && angle > theta - bandB) c *= (theta - angle) / bandB;
                return c > 0 ? c : 0;
            }
            public Rgba32 Pixel(int x, int y, Mips own, Mips shape, Rgba32 value)
            {
                double c = Cover(x, y, out var l);
                if (!(c > 0)) return Rgba32.Transparent;
                var at = Base(l); var dx = Diff(at, l, x, y, 1, 0); var dy = Diff(at, l, x, y, 0, 1);
                if (shape != null) { c *= Alpha(shape.Sample(at.s, at.t, dx, dy)) / 255; if (!(c > 0)) return Rgba32.Transparent; }
                if (own == null) return new Rgba32(value.R, value.G, value.B, B(value.A / 255.0 * c));
                return Combine(own.Sample(at.s, at.t, dx, dy), c);
            }
        }

        static Rgba32 At(byte[] rgba, int x, int y, int w = W) { int o = (y * w + x) * 4; return new Rgba32(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]); }

        static void AssertMatches(PaintLayer layer, PaintChannel channel, Func<int, int, Rgba32> expected, string what, int minimumExactPercent = 97)
        {
            var got = layer.EvaluateOutputRegion(channel, 0, 0, W, H);
            int exact = 0, compared = 0, shown = 0;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                var e = expected(x, y); var g = At(got, x, y);
                compared++; if (e.A > 0) shown++;
                if (g == e) { exact++; continue; }
                Assert.That(Math.Abs(g.A - e.A), Is.LessThanOrEqualTo(1), what + " alpha at " + x + "," + y + ": " + g + " vs " + e);
                if (g.A > 0 || e.A > 0) Assert.That(Math.Max(Math.Abs(g.R - e.R), Math.Max(Math.Abs(g.G - e.G), Math.Abs(g.B - e.B))), Is.LessThanOrEqualTo(1), what + " at " + x + "," + y + ": " + g + " vs " + e);
            }
            Assert.That(exact * 100, Is.GreaterThanOrEqualTo(compared * minimumExactPercent), what + ": " + exact + " of " + compared + " exact (only floating-point rounding may differ)");
            Assert.That(shown, Is.GreaterThan(0), what + ": the case shows something (otherwise it proves nothing)");
        }

        // ───────── 画素 ─────────

        [Test] public void DecalPixelsMatchTheReferenceForPlacementsCullingAndWraps()
        {
            var picture = Picture(40, 32, 11);
            var s = Make(picture);
            double cosHalf = Math.Cos(Math.PI / 12); // Y で 30° の四元数
            var frames = new[] { (GeneratorModelFrame.Identity, Identity3, new double[3]), (new GeneratorModelFrame(.3, -.2, .1, 0, Math.Sin(Math.PI / 12), 0, cosHalf), Rot(0, 30, 0), new[] { .3, -.2, .1 }) };
            var placements = new[]
            {
                Placement,
                new ShapeVolume(GeneratorShape.Box, .1, -.2, .4, 10, -20, 5, .9, 1.7, 1.3, 0),
                new ShapeVolume(GeneratorShape.Box, -.1, .3, .5, 0, 180, 0, 1.4, 2, .8, 0), // 反対側から（もう半分の面が表）
            };
            var cullings = new[] { (FillProjection.DefaultDepthHardness, FillProjection.DefaultBackfaceAngle, FillProjection.DefaultBackfaceHardness), (1.0, 90.0, 1.0), (0.0, 120.0, 0.0), (1.0, 180.0, 1.0) };
            foreach (var (frame, rotation, position) in frames)
            {
                s.Inputs.Frame(frame); s.Doc.RefreshGeneratorInputs();
                foreach (var v in placements)
                    foreach (var (depth, angle, edge) in cullings)
                        foreach (var p in new[]
                        {
                            FillProjection.DecalAt(v).WithCulling(depth, angle, edge),
                            FillProjection.DecalAt(v).WithCulling(depth, angle, edge).WithWrap(FillWrap.Repeat).WithTiles(2, 1.5).WithRotation(20),
                            FillProjection.DecalAt(v).WithCulling(depth, angle, edge).WithWrap(FillWrap.Clamp).WithTiles(.7, .8).WithOffset(.1, -.05),
                        })
                        {
                            s.Doc.SetFillProjection(s.Decal.Id, p);
                            var reference = new Reference(p, s.Inputs.TryGetMap(MeshMapKind.Position, out var pm, out _) ? pm : null, s.Inputs.TryGetMap(MeshMapKind.WorldNormal, out var nm, out _) ? nm : null, rotation, position);
                            var mips = new Mips(picture, p);
                            AssertMatches(s.Decal, PaintChannel.Color, (x, y) => reference.Pixel(x, y, mips, null, White), p + " root " + position[0]);
                        }
            }
        }

        [Test] public void OutsideTheBoxTurnedAwayFacesAndEmptyTexelsAreTransparentAndTheImageShowsOnce()
        {
            // 不透明な 1 色の画像・硬い縁: 箱の中で表を向いた所は画像そのもの（画像の外は透明なので、箱の端だけ半分ほど薄い）
            var rgba = new byte[8 * 8 * 4]; for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = 200; rgba[i + 1] = 30; rgba[i + 2] = 60; rgba[i + 3] = 255; }
            var s = Make(ImageContent.FromPixels(rgba, 8, 8), projection: FillProjection.DecalAt(Placement).WithCulling(1, 90, 1));
            var reference = new Reference(s.Decal.Projection, Positions(), Normals(), Identity3, new double[3]);
            var output = s.Decal.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            int inside = 0, outside = 0, culled = 0, empty = 0;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                var g = At(output, x, y); double c = reference.Cover(x, y, out var l);
                if (Empty(x, y)) { Assert.That(g, Is.EqualTo(Rgba32.Transparent), "an empty texel"); empty++; continue; }
                bool inBox = Math.Abs(l[0]) <= .6 && Math.Abs(l[1]) <= 1.2 && Math.Abs(l[2]) <= .5;
                if (!(c > 0))
                {
                    Assert.That(g, Is.EqualTo(Rgba32.Transparent), (inBox ? "turned away" : "outside the box") + " at " + x + "," + y);
                    if (inBox) culled++; else outside++;
                    continue;
                }
                Assert.That(g.A, Is.GreaterThan(0), "inside, facing the image, at " + x + "," + y);
                Assert.That((g.R, g.G, g.B), Is.EqualTo(((byte)200, (byte)30, (byte)60)), "the image's colour");
                if (g.A == 255) inside++;
            }
            Assert.That(inside, Is.GreaterThan(10)); Assert.That(outside, Is.GreaterThan(10)); Assert.That(culled, Is.GreaterThan(10), "the scene has faces turned away inside the box"); Assert.That(empty, Is.GreaterThan(0));
            // 箱を 180° 回すと、表と裏が入れ替わる
            s.Doc.SetFillProjection(s.Decal.Id, s.Decal.Projection.WithPlacement(Placement.WithRotation(0, 180, 0)));
            var flipped = s.Decal.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                if (At(output, x, y).A == 255) Assert.That(At(flipped, x, y).A, Is.Zero, "a face that showed from −Z is turned away from +Z at " + x + "," + y);
            // 180° と硬い縁: 面の向きでは切らない（箱の中は全部）
            s.Doc.SetFillProjection(s.Decal.Id, s.Decal.Projection.WithCulling(1, 180, 1));
            var all = s.Decal.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            int shownAll = Enumerable.Range(0, W * H).Count(i => all[i * 4 + 3] > 0), shownBefore = Enumerable.Range(0, W * H).Count(i => output[i * 4 + 3] > 0);
            Assert.That(shownAll, Is.GreaterThan(shownBefore)); Assert.That(shownAll, Is.EqualTo(Enumerable.Range(0, W * H).Count(i => all[i * 4 + 3] > 0)));
        }

        [Test] public void EveryChannelShowsInTheShapeOfTheFirstImageAndAValueOnlyDecalIsItsBox()
        {
            var logo = Picture(40, 32, 5);
            var s = Make(logo, projection: FillProjection.DecalAt(Placement).WithCulling(.6, 100, .7));
            var height = s.Resources.Add("Height", Grey(20, 16), ResourceOrigin.None, ResourceColorSpace.Linear, out _);
            var rough = new Rgba32(40, 40, 40, 255);
            s.Doc.SetFillValue(s.Decal.Id, PaintChannel.Roughness, rough);
            s.Doc.SetFillImage(s.Decal.Id, PaintChannel.Height, height.Id);
            var p = s.Decal.Projection; var reference = new Reference(p, Positions(), Normals(), Identity3, new double[3]);
            var shape = new Mips(logo, p);
            Assert.That(s.Decal.HasEvaluatedOutput(PaintChannel.Roughness), Is.True, "a decal's value channel is evaluated");
            AssertMatches(s.Decal, PaintChannel.Color, (x, y) => reference.Pixel(x, y, shape, null, White), "Color: its own alpha is the shape");
            AssertMatches(s.Decal, PaintChannel.Roughness, (x, y) => reference.Pixel(x, y, null, shape, rough), "Roughness: the value in the shape");
            AssertMatches(s.Decal, PaintChannel.Height, (x, y) => reference.Pixel(x, y, new Mips(Grey(20, 16), p), shape, White), "Height: an opaque image cut to the shape");
            // 合成: 下地の Roughness（200）の上で、形の所だけが 40 に寄る
            var under = s.Doc.Composite(PaintChannel.Roughness); var output = s.Decal.EvaluateOutputRegion(PaintChannel.Roughness, 0, 0, W, H);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                if (output[(y * W + x) * 4 + 3] == 0) Assert.That(At(under, x, y), Is.EqualTo(new Rgba32(200, 200, 200, 255)), "nothing outside the decal");
            // Color の画像を外すと、形は次のチャンネル（Height）の画像
            s.Doc.SetFillImage(s.Decal.Id, PaintChannel.Color, null);
            var heightShape = new Mips(Grey(20, 16), p);
            AssertMatches(s.Decal, PaintChannel.Roughness, (x, y) => reference.Pixel(x, y, null, heightShape, rough), "the shape is now the Height image (opaque)");
            AssertMatches(s.Decal, PaintChannel.Color, (x, y) => reference.Pixel(x, y, null, heightShape, White), "Color is its value in that shape");
            // 画像が 1 つも無いデカールは箱（覆い）そのもの
            s.Doc.SetFillImage(s.Decal.Id, PaintChannel.Height, null);
            AssertMatches(s.Decal, PaintChannel.Roughness, (x, y) => reference.Pixel(x, y, null, null, rough), "no image: the box");
            // 透明な値は何も出さない（評価もしない）
            s.Doc.SetFillValue(s.Decal.Id, PaintChannel.Roughness, new Rgba32(40, 40, 40, 0));
            Assert.That(s.Decal.OutputMayCover(PaintChannel.Roughness, 0, 0, 3, 3), Is.False);
        }

        // ───────── 置けない・画像が無い ─────────

        [Test] public void ADecalThatCannotBePlacedIsTransparentEverywhereWithAReason()
        {
            var s = Make();
            Assert.That(s.Doc.GetDecalProblem(s.Decal.Id), Is.Null);
            var placed = s.Doc.Composite(PaintChannel.Color);
            s.Doc.SetLayerVisibility(s.Decal.Id, false); var bare = s.Doc.Composite(PaintChannel.Color); s.Doc.SetLayerVisibility(s.Decal.Id, true);
            Assert.That(placed, Is.Not.EqualTo(bare), "the decal shows");
            void NotShown(string why, string expected)
            {
                string problem = s.Doc.GetDecalProblem(s.Decal.Id);
                Assert.That(problem, Does.Contain(expected), why);
                var output = s.Decal.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
                Assert.That(output.All(b => b == 0), Is.True, why + ": transparent everywhere, never the value spread over the texture set");
                Assert.That(s.Doc.Composite(PaintChannel.Color), Is.EqualTo(bare), why);
                Assert.That(s.Doc.InactiveFillImages(), Has.Some.Contains("'Decal' (decal)").And.Some.Contains(expected), why);
                Assert.That(s.Decal.OutputMayCover(PaintChannel.Color, 0, 0, 3, 3), Is.False, why + ": nothing to evaluate");
            }
            s.Inputs.Remove(MeshMapKind.WorldNormal); NotShown("no normal map (the facing needs it)", "WorldNormal");
            s.Inputs.Put(Normals()).Remove(MeshMapKind.Position); NotShown("no position map", "Position");
            s.Inputs.Put(Positions(W / 2, H / 2)); NotShown("a map of another size", "24×20");
            s.Inputs.Put(Positions()).Unknown(); NotShown("the model root is not known", "model root");
            // 置けないデカールは統合（焼き込み）を断る
            var paint = s.Doc.AddLayer("Paint"); s.Doc.MoveLayer(paint.Id, 0); s.Doc.MoveLayer(s.Decal.Id, s.Doc.Layers.Count - 1);
            int steps = s.Doc.UndoCount;
            Assert.That(() => s.Doc.MergeDown(s.Decal.Id), Throws.InvalidOperationException.With.Message.Contains("decal cannot be baked"));
            Assert.That(s.Doc.UndoCount, Is.EqualTo(steps));
            s.Inputs.Frame(GeneratorModelFrame.Identity);
            Assert.That(s.Doc.GetDecalProblem(s.Decal.Id), Is.Null); Assert.That(s.Doc.Composite(PaintChannel.Color), Is.EqualTo(placed), "placed again, the same pixels");
            Assert.That(s.Doc.InactiveFillImages(), Is.Empty);
            // リソースに無い画像: その画像のチャンネルは値（形は箱）を出し、理由を言う
            var gone = Make(); gone.Doc.ImageResources = new ProjectResources();
            var status = gone.Doc.GetFillImageStatus(gone.Decal.Id, PaintChannel.Color);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("no image resource"));
            var reference = new Reference(gone.Decal.Projection, Positions(), Normals(), Identity3, new double[3]);
            AssertMatches(gone.Decal, PaintChannel.Color, (x, y) => reference.Pixel(x, y, null, null, White), "a missing image shows the value inside the box");
            Assert.That(gone.Doc.InactiveFillImages().Single(), Does.Contain("'Decal' (Color)"));
            Assert.That(gone.Doc.MissingFillImages().Single(), Does.Contain("'Decal' (Color)"));
        }

        // ───────── 変わったタイル・届くタイル ─────────

        static HashSet<TileCoord> CoveredTiles(PaintLayer layer, PaintChannel channel, int w = W, int h = H, int tile = T)
        {
            var output = layer.EvaluateOutputRegion(channel, 0, 0, w, h); var set = new HashSet<TileCoord>();
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (output[(y * w + x) * 4 + 3] > 0) set.Add(new TileCoord(x / tile, y / tile));
            return set;
        }

        [Test] public void MovingADecalReportsOnlyTheTilesItMayCoverAndFarTilesAreNotEvaluated()
        {
            // 64×64・タイル 8 の文書（8 × 8 タイル）に、小さなデカール
            const int S = 64, Tile = 8;
            var small = new ShapeVolume(GeneratorShape.Box, -.4, -1.1, .5, 0, 0, 0, .3, .5, 1.6, 0);
            var s = Make(w: S, h: S, tile: Tile, projection: FillProjection.DecalAt(small).WithCulling(1, 180, 1));
            var before = CoveredTiles(s.Decal, PaintChannel.Color, S, S, Tile);
            Assert.That(before.Count, Is.InRange(1, 20), "a small decal covers a few tiles of 64");
            // 届かないタイルは評価しない（出力も透明）
            for (int ty = 0; ty < S / Tile; ty++) for (int tx = 0; tx < S / Tile; tx++)
            {
                bool may = s.Decal.OutputMayCover(PaintChannel.Color, tx, ty, tx + 1, ty + 1);
                if (before.Contains(new TileCoord(tx, ty))) Assert.That(may, Is.True, "a covered tile may be covered");
            }
            int skipped = Enumerable.Range(0, 64).Count(i => !s.Decal.OutputMayCover(PaintChannel.Color, i % 8, i / 8, i % 8 + 1, i / 8 + 1));
            Assert.That(skipped, Is.GreaterThan(32), "most tiles are known to be out of reach");
            s.Doc.Composite(PaintChannel.Color);
            long serial = s.Doc.ChangeSerial;
            var moved = small.WithCenter(.3, 1.0, .5);
            s.Doc.SetFillProjection(s.Decal.Id, s.Decal.Projection.WithPlacement(moved));
            var after = CoveredTiles(s.Decal, PaintChannel.Color, S, S, Tile);
            var changed = new HashSet<TileCoord>();
            Assert.That(s.Doc.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True);
            Assert.That(changed, Is.SupersetOf(before).And.SupersetOf(after), "every tile it left or reached is reported");
            Assert.That(changed.Count, Is.LessThan(S / Tile * (S / Tile) / 2), "not the whole canvas");
            var other = new HashSet<TileCoord>(); Assert.That(s.Doc.TryGetChangedTiles(PaintChannel.Metallic, serial, other), Is.True);
            Assert.That(other, Is.Empty, "a channel without a value is not touched");
            // 差分の合成（変わったタイルだけ作り直す）が、全部を合成し直したものと同じ
            var full = s.Doc.Composite(PaintChannel.Color);
            var copy = DocumentBinary.Read(DocumentBinary.Write(s.Doc)); copy.ImageResources = s.Resources; copy.GeneratorInputs = s.Inputs;
            Assert.That(copy.Composite(PaintChannel.Color), Is.EqualTo(full));
            // Undo も同じだけ報告する
            serial = s.Doc.ChangeSerial; s.Doc.Undo(); changed.Clear();
            Assert.That(s.Doc.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True);
            Assert.That(changed, Is.SupersetOf(before).And.SupersetOf(after));
            Assert.That(s.Decal.Projection.Placement, Is.EqualTo(small));
            // フィルターのあるデカールは、広がりがあるので全面を報告する
            s.Doc.AddFilter(s.Decal.Id, FilterTarget.Content, FilterSettings.GaussianBlur(3), new[] { PaintChannel.Color });
            serial = s.Doc.ChangeSerial; changed.Clear();
            s.Doc.SetFillProjection(s.Decal.Id, s.Decal.Projection.WithPlacement(moved));
            Assert.That(s.Doc.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True);
            Assert.That(changed.Count, Is.EqualTo(64));
        }

        [Test] public void TheBytesDoNotDependOnThreadsBlocksOrTileSize()
        {
            byte[] reference = null;
            foreach (var (threads, tile, block) in new[] { (1, 16, 256), (0, 16, 256), (0, 8, 16), (3, 32, 64) })
            {
                CoreParallelism.MaxDegreeOfParallelism = threads;
                var s = Make(tile: tile, projection: FillProjection.DecalAt(new ShapeVolume(GeneratorShape.Box, .1, -.2, .4, 10, -20, 5, .9, 1.7, 1.3, 0)));
                s.Doc.FilterBlockPixels = block;
                s.Doc.SetFillValue(s.Decal.Id, PaintChannel.Roughness, new Rgba32(10, 10, 10, 200));
                var bytes = s.Doc.Composite(PaintChannel.Color).Concat(s.Doc.Composite(PaintChannel.Roughness)).ToArray();
                if (reference == null) reference = bytes; else Assert.That(bytes, Is.EqualTo(reference), threads + " threads, tile " + tile + ", block " + block);
            }
        }

        [Test] public void TheCoverageGridIsTheDecalsReachWithoutItsImage()
        {
            var s = Make(projection: FillProjection.DecalAt(Placement).WithCulling(.5, 110, .4));
            var reference = new Reference(s.Decal.Projection, Positions(), Normals(), Identity3, new double[3]);
            var grid = s.Doc.DecalCoverage(s.Decal.Id, W, H);
            int differ = 0, shown = 0;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                byte e = B(reference.Cover(x, y, out _)); if (e > 0) shown++;
                if (Math.Abs(grid[y * W + x] - e) > 1) differ++;
            }
            Assert.That(differ, Is.Zero); Assert.That(shown, Is.GreaterThan(0));
            var half = s.Doc.DecalCoverage(s.Decal.Id, W / 2, H / 2);
            for (int y = 0; y < H / 2; y++) for (int x = 0; x < W / 2; x++) Assert.That(half[y * (W / 2) + x], Is.EqualTo(grid[(y * 2 + 1) * W + x * 2 + 1]), "each cell reads the texel at its centre");
            Assert.That(() => s.Doc.DecalCoverage(s.Decal.Id, W + 1, H), Throws.InstanceOf<ArgumentOutOfRangeException>());
            var plain = s.Doc.AddFillLayer("Plain");
            Assert.That(() => s.Doc.DecalCoverage(plain.Id, 4, 4), Throws.ArgumentException);
            s.Inputs.Remove(MeshMapKind.Position);
            Assert.That(s.Doc.DecalCoverage(s.Decal.Id, W, H), Is.Null, "not placed");
        }

        // ───────── 編集・Undo・拒否 ─────────

        [Test] public void DecalEditsAreUndoableAndDragsCoalesceOrCancel()
        {
            var s = Make(); var d = s.Doc;
            var start = d.Composite(PaintChannel.Color); var p = s.Decal.Projection;
            foreach (double x in new[] { .02, .05, .1, .15 }) d.SetFillProjection(s.Decal.Id, s.Decal.Projection.WithPlacement(Placement.WithCenter(x, 0, .5)), coalesce: true);
            d.EndCoalescing();
            Assert.That(d.UndoCount, Is.EqualTo(1), "one drag, one step");
            var moved = d.Composite(PaintChannel.Color);
            Assert.That(moved, Is.Not.EqualTo(start));
            foreach (double angle in new[] { 80.0, 70, 60 }) d.SetFillProjection(s.Decal.Id, s.Decal.Projection.WithCulling(.8, angle, .8), coalesce: true);
            Assert.That(d.CancelCoalescing(), Is.True);
            Assert.That(s.Decal.Projection.BackfaceAngle, Is.EqualTo(FillProjection.DefaultBackfaceAngle)); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(moved));
            d.Undo(); Assert.That(s.Decal.Projection, Is.EqualTo(p)); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(start));
            d.Redo(); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(moved));
            // デカールから平面へ（全面の投影）と戻す
            d.SetFillProjection(s.Decal.Id, s.Decal.Projection.WithMode(FillProjectionMode.Planar));
            Assert.That(s.Decal.IsDecal, Is.False); Assert.That(s.Decal.Projection.BackfaceAngle, Is.EqualTo(FillProjection.DefaultBackfaceAngle), "only a decal culls");
            Assert.That(s.Decal.HasEvaluatedOutput(PaintChannel.Color), Is.True);
            d.Undo(); Assert.That(s.Decal.IsDecal, Is.True); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(moved));
            // 値・画像を変えても 1 回ずつ
            int steps = d.UndoCount;
            d.SetFillValue(s.Decal.Id, PaintChannel.Metallic, new Rgba32(255, 255, 255, 255));
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1)); Assert.That(s.Decal.HasEvaluatedOutput(PaintChannel.Metallic), Is.True);
            d.Undo(); Assert.That(s.Decal.FillValues.ContainsKey(PaintChannel.Metallic), Is.False);
        }

        [Test] public void CullingOutOfRangeOrOutsideADecalWrongKindsLocksAndBudgetsAreRefused()
        {
            var s = Make(); var d = s.Doc; long revision = d.Revision;
            Assert.That(() => FillProjection.Default.WithCulling(.5, 90, .5), Throws.InstanceOf<ArgumentOutOfRangeException>().With.Message.Contains("Only a decal"));
            var decal = FillProjection.DecalAt(Placement);
            Assert.That(() => decal.WithCulling(-.1, 90, 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => decal.WithCulling(1, 200, 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => decal.WithCulling(1, 90, double.NaN), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => decal.WithWrap((FillWrap)3), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => decal.WithPlacement(Placement.WithSize(0, 1, 1)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(decal.WithMode(FillProjectionMode.Triplanar).WithMode(FillProjectionMode.Decal).BackfaceAngle, Is.EqualTo(FillProjection.DefaultBackfaceAngle));
            var raster = d.AddLayer("Paint"); d.ClearHistory(); revision = d.Revision;
            Assert.That(() => d.SetFillProjection(raster.Id, decal), Throws.InvalidOperationException);
            Assert.That(() => d.GetDecalProblem(raster.Id), Throws.ArgumentException);
            // ロック: デカールにする・動かすのは画素の中身を変える（画像の無い塗りつぶしでも）
            var plain = d.AddFillLayer("Plain", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, White } }); d.ClearHistory();
            foreach (var locks in new[] { LayerLocks.Pixels, LayerLocks.Transparency, LayerLocks.All })
            {
                d.SetLayerLocks(plain.Id, locks); d.SetLayerLocks(s.Decal.Id, locks);
                Assert.That(() => d.SetFillProjection(plain.Id, decal), Throws.TypeOf<LayerLockedException>(), locks + ": making a decal");
                Assert.That(() => d.SetFillProjection(s.Decal.Id, s.Decal.Projection.WithPlacement(Placement.WithCenter(.1, 0, .5))), Throws.TypeOf<LayerLockedException>(), locks + ": moving it");
                d.SetLayerLocks(plain.Id, LayerLocks.None); d.SetLayerLocks(s.Decal.Id, LayerLocks.None);
            }
            Assert.That(plain.IsDecal, Is.False); Assert.That(s.Decal.Projection.Placement, Is.EqualTo(Placement));
            // 予算: ミップの予算に入らない画像はデカールにも差さない
            var big = s.Resources.Add("Big", Picture(64, 64, 9), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            d.FillImageCacheBudgetBytes = 1000;
            Assert.That(() => d.SetFillImage(s.Decal.Id, PaintChannel.Roughness, big.Id), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.OverBudget));
            Assert.That(s.Decal.HasFillImage(PaintChannel.Roughness), Is.False);
            // 後から下げた予算: そのチャンネルは値（箱の中）と理由、ほかのチャンネルの形にもならない
            d.FillImageCacheBudgetBytes = 100;
            Assert.That(d.GetFillImageStatus(s.Decal.Id, PaintChannel.Color).Reason, Does.Contain("budget"));
            var reference = new Reference(s.Decal.Projection, Positions(), Normals(), Identity3, new double[3]);
            AssertMatches(s.Decal, PaintChannel.Color, (x, y) => reference.Pixel(x, y, null, null, White), "over the budget: the value in the box");
        }

        // ───────── 複製・大きさ・スマートマテリアル・統合 ─────────

        [Test] public void DuplicateResizeSmartMaterialAndMergeKeepOrBakeTheDecal()
        {
            var s = Make(); var d = s.Doc;
            var composite = d.Composite(PaintChannel.Color);
            var copy = d.DuplicateLayer(s.Decal.Id);
            Assert.That(copy.IsDecal, Is.True); Assert.That(copy.Projection, Is.EqualTo(s.Decal.Projection));
            Assert.That(copy.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H), Is.EqualTo(s.Decal.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H)));
            d.Undo();
            var resized = d.Resampled(W * 2, H * 2, CanvasResampling.Bilinear).Document;
            Assert.That(resized.GetLayer(s.Decal.Id).Projection, Is.EqualTo(s.Decal.Projection), "the placement is in the model's space, whatever the size");
            // スマートマテリアル: 層ごと運び、置いた先でも同じデカール
            var smart = d.CaptureSmartMaterial(new[] { s.Decal.Id }, "Sticker", s.Resources);
            var target = new PaintDocument(W, H, T) { ImageResources = s.Resources, GeneratorInputs = s.Inputs };
            var placed = target.PlaceSmartMaterial(smart);
            var placedLayer = target.Layers.Single(l => l.Kind == LayerKind.Fill);
            Assert.That(placedLayer.IsDecal, Is.True); Assert.That(placedLayer.Projection, Is.EqualTo(s.Decal.Projection));
            Assert.That(placedLayer.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H), Is.EqualTo(s.Decal.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H)));
            // 統合: 下の層へ焼き込むと、見た目は同じ画素
            var report = d.MergeDown(s.Decal.Id);
            Assert.That(d.GetLayer(report.ResultId).Kind, Is.Not.EqualTo(LayerKind.Group));
            Assert.That(report.MaxVisibleDifference, Is.LessThanOrEqualTo(1));
            var after = d.Composite(PaintChannel.Color);
            for (int i = 0; i < after.Length; i++) Assert.That(Math.Abs(after[i] - composite[i]), Is.LessThanOrEqualTo(1));
        }

        // ───────── 正本 ─────────

        static byte[] AtVersion(byte[] native, int version) { var bytes = (byte[])native.Clone(); BitConverter.GetBytes(version).CopyTo(bytes, 8); return bytes; }
        static int Find(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++) { int k = 0; while (k < needle.Length && haystack[i + k] == needle[k]) k++; if (k == needle.Length) return i; }
            throw new AssertionException("not found");
        }

        [Test] public void TheNativeArchiveRoundTripsADecalAndOlderVersionsRefuseIt()
        {
            var s = Make(projection: FillProjection.DecalAt(new ShapeVolume(GeneratorShape.Box, .1, -.2, .4, 10, -20, 5, .9, 1.7, 1.3, 0)).WithCulling(.25, 75, .5).WithTiles(1.5, 1).WithRotation(15));
            s.Doc.SetFillValue(s.Decal.Id, PaintChannel.Roughness, new Rgba32(30, 30, 30, 255));
            var valueOnly = s.Doc.AddFillLayer("Value decal", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Metallic, White } });
            s.Doc.SetFillProjection(valueOnly.Id, FillProjection.DecalAt(Placement)); // 画像の無いデカールも残す
            var bytes = DocumentBinary.Write(s.Doc);
            Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(DocumentBinary.CurrentVersion));
            var back = DocumentBinary.Read(bytes);
            Assert.That(DocumentBinary.Write(back), Is.EqualTo(bytes), "read and written back byte for byte");
            Assert.That(back.GetLayer(s.Decal.Id).Projection, Is.EqualTo(s.Decal.Projection));
            Assert.That(back.GetLayer(valueOnly.Id).Projection, Is.EqualTo(valueOnly.Projection)); Assert.That(back.GetLayer(valueOnly.Id).FillImages, Is.Empty);
            back.ImageResources = s.Resources; back.GeneratorInputs = s.Inputs;
            foreach (var c in new[] { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Metallic }) Assert.That(back.Composite(c), Is.EqualTo(s.Doc.Composite(c)), c + ": the same pixels as before saving");
            // デカールを足す前の版（塗りつぶしの画像の版 16）: デカールの種類（5）も、画像の外を透明にする（2）も知らない
            Assert.That(() => DocumentBinary.Read(AtVersion(bytes, 16)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unknown fill projection"), "version 16");
            var planar = Make(projection: FillProjection.Default.WithMode(FillProjectionMode.Planar).WithWrap(FillWrap.None));
            var planarBytes = DocumentBinary.Write(planar.Doc);
            Assert.That(() => DocumentBinary.Read(AtVersion(planarBytes, 16)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unknown fill wrap"));
            Assert.That(DocumentBinary.Read(planarBytes).GetLayer(planar.Decal.Id).Projection.Wrap, Is.EqualTo(FillWrap.None));
            // 壊した間引き: 範囲の外・NaN・途中で切れたもの
            int block = Find(bytes, s.Image.Id.ToByteArray()) - 8, projection = block + 8 + 16, culling = projection + 12 + 15 * 8;
            Assert.That(BitConverter.ToInt32(bytes, projection + 4), Is.EqualTo((int)FillProjectionMode.Decal));
            Assert.That(BitConverter.ToDouble(bytes, culling), Is.EqualTo(.25)); Assert.That(BitConverter.ToDouble(bytes, culling + 8), Is.EqualTo(75)); Assert.That(BitConverter.ToDouble(bytes, culling + 16), Is.EqualTo(.5));
            void Refused(string why, Action<byte[]> tamper) { var b = (byte[])bytes.Clone(); tamper(b); Assert.That(() => DocumentBinary.Read(b), Throws.TypeOf<InvalidDataException>(), why); }
            Refused("depth hardness 2", b => BitConverter.GetBytes(2.0).CopyTo(b, culling));
            Refused("back-face angle 200", b => BitConverter.GetBytes(200.0).CopyTo(b, culling + 8));
            Refused("NaN hardness", b => BitConverter.GetBytes(double.NaN).CopyTo(b, culling + 16));
            Refused("an unknown mode", b => BitConverter.GetBytes(6).CopyTo(b, projection + 4));
            Refused("an unknown wrap", b => BitConverter.GetBytes(3).CopyTo(b, projection + 8));
            Assert.That(() => DocumentBinary.Read(bytes.Take(culling + 12).ToArray()), Throws.TypeOf<InvalidDataException>(), "truncated");
        }

        [Test] public void ADocumentWithoutDecalsIsLaidOutAsVersion16()
        {
            var r = new ProjectResources(); var image = r.Add("Image", Picture(16, 16), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            var d = new PaintDocument(W, H, T) { ImageResources = r };
            var fill = d.AddFillLayer("Projected", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, White } });
            d.SetFillImage(fill.Id, PaintChannel.Color, image.Id);
            d.SetFillProjection(fill.Id, FillProjection.Default.WithMode(FillProjectionMode.Triplanar).WithTiles(2, 2));
            d.AddLayer("Paint");
            var bytes = DocumentBinary.Write(d);
            Assert.That(DocumentBinary.Write(DocumentBinary.Read(AtVersion(bytes, 16))), Is.EqualTo(bytes), "version 16 bytes read as they are and written back as the current version (only the number differs)");
        }

        // ───────── PSD ─────────

        [Test] public void PsdExportWritesTheDecalAsPixelsAndSaysWhatIsNotCarried()
        {
            var s = Make();
            s.Doc.SetFillValue(s.Decal.Id, PaintChannel.Roughness, new Rgba32(30, 30, 30, 255));
            var notes = new List<PsdDiagnostic>();
            var psd = PsdBridge.Export(s.Doc, PaintChannel.Color, notes);
            var layer = psd.Layers.Single(l => l.Name == "Decal");
            Assert.That(layer.PixelsRgba.Length, Is.EqualTo(W * H * 4));
            var output = s.Decal.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                int o = ((H - 1 - y) * W + x) * 4; // PSD は上から
                Assert.That(new Rgba32(layer.PixelsRgba[o], layer.PixelsRgba[o + 1], layer.PixelsRgba[o + 2], layer.PixelsRgba[o + 3]), Is.EqualTo(At(output, x, y)));
            }
            Assert.That(notes.Single(n => n.Code == PsdCodec.NotCarriedIntoExport).Message, Does.Contain("Decal 'Decal' (Color)").And.Contain("placement"));
            notes.Clear(); PsdBridge.Export(s.Doc, PaintChannel.Roughness, notes);
            Assert.That(notes.Single(n => n.Code == PsdCodec.NotCarriedIntoExport).Message, Does.Contain("its value"));
            Assert.That(() => PsdBridge.Export(s.Doc, PaintChannel.Color), Throws.InvalidOperationException, "the export without notes refuses what it cannot carry");
        }
    }
}
