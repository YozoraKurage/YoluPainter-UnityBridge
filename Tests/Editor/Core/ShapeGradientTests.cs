using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>形のグラデーション（Generator の種類 5、Core）: 焼いた Position マップを由来の境界箱でモデルの空間に戻し、ボックス・球・平面の
    /// 値を作る。形ごとの値・やわらかさ・回転・大きさ・境界が別に組んだ参照の式どおり、回転は Unity の Quaternion.Euler と同じ行列、モデルの
    /// ルートの位置と向き（IGeneratorModelFrame）で形がモデルに付いて行く、レベル・反転・崩し・合成・強さと組み合わせても決まった値で
    /// スレッドの数やブロックの大きさによらない、マップが無い・大きさが違う・ルートが分からないときは入力のまま理由を出す、Undo と
    /// ドラッグのまとめと取消（CancelCoalescing）、壊れた値と別の種類の形の拒否、ネイティブ版 13 の往復と古い版・知らない形の拒否。</summary>
    public sealed class ShapeGradientTests
    {
        const int W = 64, H = 48, T = 16;
        static readonly double[] Min = { -1, -2, -.5 }, Max = { 1, 2, 1.5 };
        [TearDown] public void ResetThreads() { CoreParallelism.MaxDegreeOfParallelism = 0; }

        static byte B(double v) { double x = v * 255 + .5; return x >= 255 ? (byte)255 : x > 0 ? (byte)(int)x : (byte)0; }
        static double C01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
        static double Levels(double b, double low, double high, double softness, bool invert)
        {
            double t = C01((b - low) / (high - low));
            if (softness > 0) t += softness * (t * t * (3 - 2 * t) - t);
            return invert ? 1 - t : t;
        }

        /// <summary>テクセルが表す点（Position マップの 0〜1、丸める前）: x は横、y は縦、z は x と y から作った段々。</summary>
        static double[] Raw(int x, int y) => new[] { x / (double)(W - 1), y / (double)(H - 1), (x * 7 + y * 3) % 23 / 22.0 };
        static BakedMeshMap Positions(Func<int, int, MeshTexelCoverage> coverage = null, int w = W, int h = H)
            => TestMeshMaps.Make(MeshMapKind.Position, w, h, (x, y, c) => Raw(x, y)[c], coverage, "test", Min, Max);
        /// <summary>マップが持つ点（16 bit に丸めた値を境界箱で戻したもの。スナップショットの空間）。</summary>
        static double[] Point(int x, int y)
        {
            var r = Raw(x, y); var p = new double[3];
            for (int c = 0; c < 3; c++) p[c] = Min[c] + TestMeshMaps.Q(r[c]) * (Max[c] - Min[c]);
            return p;
        }

        // ───────── 参照の式（Core とは別の組み方: 軸ごとの回転を掛け合わせる） ─────────

        static double[,] Mul(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) r[i, j] += a[i, k] * b[k, j];
            return r;
        }
        /// <summary>Unity の Quaternion.Euler の順（Z、X、Y の順に回す: Ry · Rx · Rz）。</summary>
        static double[,] Rot(double ax, double ay, double az)
        {
            double a = ax * Math.PI / 180, b = ay * Math.PI / 180, c = az * Math.PI / 180;
            var rx = new double[,] { { 1, 0, 0 }, { 0, Math.Cos(a), -Math.Sin(a) }, { 0, Math.Sin(a), Math.Cos(a) } };
            var ry = new double[,] { { Math.Cos(b), 0, Math.Sin(b) }, { 0, 1, 0 }, { -Math.Sin(b), 0, Math.Cos(b) } };
            var rz = new double[,] { { Math.Cos(c), -Math.Sin(c), 0 }, { Math.Sin(c), Math.Cos(c), 0 }, { 0, 0, 1 } };
            return Mul(Mul(ry, rx), rz);
        }
        /// <summary>ルートの空間の点での形の値。</summary>
        static double RefBase(ShapeVolume v, double[] p)
        {
            var r = Rot(v.RotationX, v.RotationY, v.RotationZ);
            double[] d = { p[0] - v.CenterX, p[1] - v.CenterY, p[2] - v.CenterZ }, l = new double[3];
            for (int i = 0; i < 3; i++) for (int k = 0; k < 3; k++) l[i] += r[k, i] * d[k];
            double inset, band;
            switch (v.Shape)
            {
                case GeneratorShape.Box:
                {
                    double[] h = { v.SizeX / 2, v.SizeY / 2, v.SizeZ / 2 };
                    inset = Enumerable.Range(0, 3).Min(i => h[i] - Math.Abs(l[i])); band = v.Falloff * h.Min();
                    break;
                }
                case GeneratorShape.Sphere:
                    inset = v.SizeX / 2 - Math.Sqrt(l[0] * l[0] + l[1] * l[1] + l[2] * l[2]); band = v.Falloff * v.SizeX / 2;
                    break;
                default: return C01(.5 + l[1] / v.SizeY);
            }
            return inset <= 0 ? 0 : band <= 0 || inset >= band ? 1 : inset / band;
        }

        static GeneratorSettings Shape(ShapeVolume v) => GeneratorSettings.Default(GeneratorType.ShapeGradient).WithVolume(v).WithBlend(GeneratorBlend.Replace);

        static (PaintDocument d, PaintLayer fill) MaskedFill()
        {
            var d = new PaintDocument(W, H, T);
            var fill = d.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(255, 255, 255, 255) }, { PaintChannel.Roughness, new Rgba32(102, 102, 102, 255) } });
            d.AddLayerMask(fill.Id); d.ClearHistory();
            return (d, fill);
        }

        /// <summary>モデルのルートの位置と向きを答える試験用の入力。</summary>
        sealed class FramedInputs : IGeneratorInputs, IGeneratorModelFrame
        {
            readonly Dictionary<MeshMapKind, BakedMeshMap> maps = new Dictionary<MeshMapKind, BakedMeshMap>();
            GeneratorModelFrame frame = GeneratorModelFrame.Identity; bool throws;
            public long Revision { get; private set; } = 1;
            public FramedInputs Put(BakedMeshMap map) { maps[map.Kind] = map; Revision++; return this; }
            public FramedInputs Frame(GeneratorModelFrame f) { frame = f; throws = false; Revision++; return this; }
            public FramedInputs Throw() { throws = true; Revision++; return this; }
            public GeneratorModelFrame ModelFrame => throws ? throw new InvalidOperationException("no root here") : frame;
            public bool TryGetMap(MeshMapKind kind, out BakedMeshMap map, out string reason)
            {
                if (maps.TryGetValue(kind, out map)) { reason = null; return true; }
                reason = kind + " has not been baked."; return false;
            }
        }

        // ───────── 形ごとの値 ─────────

        [Test] public void EachShapeGivesItsValueWithFalloffRotationAndSize()
        {
            var (d, fill) = MaskedFill();
            d.GeneratorInputs = new TestGeneratorInputs().Put(Positions());
            var e = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Shape(ShapeVolume.Default)));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Active, Is.True);
            var cases = new[]
            {
                new ShapeVolume(GeneratorShape.Box, .1, -.3, .4, 0, 0, 30, 1.2, 2.5, 1, .25),
                new ShapeVolume(GeneratorShape.Box, -.2, .1, .5, 25, -40, 10, .9, 3, 1.3, 1),
                new ShapeVolume(GeneratorShape.Box, 0, 0, .5, 0, 0, 0, 1, 2, 3, 0), // 硬い縁
                new ShapeVolume(GeneratorShape.Sphere, -.2, .5, .5, 10, 20, 30, 2.4, 1, 1, .5), // y・z の大きさは使わない
                new ShapeVolume(GeneratorShape.Sphere, 0, 0, .5, 0, 0, 0, 3, 7, 9, 1),
                new ShapeVolume(GeneratorShape.Plane, 0, .4, 0, 0, 0, -20, 3, 1.5, 3, .3),
                new ShapeVolume(GeneratorShape.Plane, .2, 0, .3, 90, 0, 0, 1, 1.2, 1, 0), // 法線が +Z を向く
            };
            foreach (var v in cases)
            {
                d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Shape(v)));
                var hide = fill.Mask.EvaluateOutputRegion(0, 0, W, H);
                int exact = 0; var seen = new HashSet<byte>();
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        var p = Point(x, y); double b = RefBase(v, p);
                        Assert.That(v.ValueAt(p[0], p[1], p[2]), Is.EqualTo(b).Within(1e-9), v + " ValueAt " + x + "," + y);
                        byte expected = (byte)(255 - B(b)), got = hide[y * W + x];
                        Assert.That((int)got, Is.EqualTo((int)expected).Within(1), v + " at " + x + "," + y);
                        if (got == expected) exact++;
                        seen.Add(got);
                    }
                Assert.That(exact, Is.GreaterThan(W * H * 99 / 100), v + ": only a rounding boundary may differ");
                Assert.That(seen, Has.Member((byte)255), v + ": the test points reach outside");
                if (v.Falloff < 1) Assert.That(seen, Has.Member((byte)0), v + ": the test points reach the full inside");
                if (v.Falloff == 0 && v.Shape != GeneratorShape.Plane) Assert.That(seen.All(h => h == 0 || h == 255), Is.True, v + ": a hard edge");
                else Assert.That(seen.Count, Is.GreaterThan(8), v + ": a ramp");
            }
            // 境界: 中は 1、面の上と外は 0。やわらかさ 1 の球は中心から縁へ直線
            var sphere = new ShapeVolume(GeneratorShape.Sphere, 0, 0, 0, 0, 0, 0, 2, 2, 2, 1);
            Assert.That(sphere.ValueAt(0, 0, 0), Is.EqualTo(1)); Assert.That(sphere.ValueAt(.5, 0, 0), Is.EqualTo(.5).Within(1e-12));
            Assert.That(sphere.ValueAt(0, 1, 0), Is.Zero, "on the surface"); Assert.That(sphere.ValueAt(0, 0, 1.01), Is.Zero);
            var box = new ShapeVolume(GeneratorShape.Box, 0, 0, 0, 0, 0, 0, 2, 1, 4, .5); // 帯 = 0.5 · 0.5（いちばん薄い軸の半分）
            Assert.That(box.ValueAt(0, 0, 0), Is.EqualTo(1)); Assert.That(box.ValueAt(.7, 0, 1.7), Is.EqualTo(1)); Assert.That(box.ValueAt(.9, 0, 1.9), Is.EqualTo(.4).Within(1e-12), "a corner: the nearest face");
            Assert.That(box.ValueAt(0, .4, 0), Is.EqualTo(.4).Within(1e-12), "0.1 inside the face, band 0.25");
            Assert.That(box.ValueAt(.95, 0, 0), Is.EqualTo(.2).Within(1e-12), "the band is the same width at every face");
            Assert.That(box.ValueAt(1, 0, 0), Is.Zero); Assert.That(box.ValueAt(0, 0, -2.5), Is.Zero);
            var plane = new ShapeVolume(GeneratorShape.Plane, 0, 1, 0, 0, 0, 0, 1, 2, 1, 0);
            Assert.That(plane.ValueAt(5, 1, -3), Is.EqualTo(.5)); Assert.That(plane.ValueAt(0, 2, 0), Is.EqualTo(1)); Assert.That(plane.ValueAt(0, 0, 0), Is.Zero);
            Assert.That(plane.ValueAt(0, 1.5, 0), Is.EqualTo(.75).Within(1e-12));
            // 回転: Y で 90° 回した箱は、x と z の幅を入れ替えた箱と同じ値
            var turned = new ShapeVolume(GeneratorShape.Box, .1, .2, .3, 0, 90, 0, .6, 1, 1.4, .3);
            var swapped = new ShapeVolume(GeneratorShape.Box, .1, .2, .3, 0, 0, 0, 1.4, 1, .6, .3);
            for (int y = 0; y < H; y += 3) for (int x = 0; x < W; x += 3) { var p = Point(x, y); Assert.That(turned.ValueAt(p[0], p[1], p[2]), Is.EqualTo(swapped.ValueAt(p[0], p[1], p[2])).Within(1e-9)); }
            // 大きさ: 2 倍にした形の 2 倍の位置は同じ値
            var small = new ShapeVolume(GeneratorShape.Box, 0, 0, 0, 15, 25, 35, .5, .7, .9, .4);
            var large = small.WithSize(1, 1.4, 1.8);
            for (int i = 0; i < 50; i++) { double x = Math.Sin(i) * .4, y = Math.Cos(i * 1.3) * .5, z = Math.Sin(i * .7) * .6; Assert.That(large.ValueAt(2 * x, 2 * y, 2 * z), Is.EqualTo(small.ValueAt(x, y, z)).Within(1e-9)); }
        }

        [Test] public void TheRotationIsUnitysEulerAndTheFrameIsTheQuaternionsMatrix()
        {
            foreach (var (x, y, z) in new[] { (0.0, 0.0, 0.0), (30.0, 0.0, 0.0), (0.0, 45.0, 0.0), (0.0, 0.0, 60.0), (10.0, 20.0, 30.0), (-75.0, 130.0, -200.0), (90.0, 90.0, 90.0) })
            {
                var core = new ShapeVolume(GeneratorShape.Box, 0, 0, 0, x, y, z, 1, 1, 1, 0).RotationMatrix();
                var q = Quaternion.Euler((float)x, (float)y, (float)z); var unity = Matrix4x4.Rotate(q);
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Assert.That(core[i * 3 + j], Is.EqualTo(unity[i, j]).Within(2e-6), "Euler (" + x + ", " + y + ", " + z + ") [" + i + "," + j + "]");
                var frame = new GeneratorModelFrame(1, 2, 3, q.x * 2, q.y * 2, q.z * 2, q.w * 2).RotationMatrix(); // 長さは揃える
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Assert.That(frame[i * 3 + j], Is.EqualTo(unity[i, j]).Within(2e-6), "frame [" + i + "," + j + "]");
            }
            Assert.That(() => new GeneratorModelFrame(0, 0, 0, 0, 0, 0, 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new GeneratorModelFrame(double.NaN, 0, 0, 0, 0, 0, 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test] public void TheShapeFollowsTheModelRootAndPassesThroughWhenItIsUnknown()
        {
            var (d, fill) = MaskedFill();
            var inputs = new FramedInputs().Put(Positions()); d.GeneratorInputs = inputs;
            var v = new ShapeVolume(GeneratorShape.Box, .2, .1, .3, 0, 0, 20, .9, 1.6, 1.1, .5);
            var e = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Shape(v)));
            var atRoot = fill.Mask.EvaluateOutputRegion(0, 0, W, H);
            // ルートが (0.3, −0.2, 0.1) にあり、Y で 90° 回っている: マップの点 p はルートの空間で R0ᵀ (p − t0)
            double s = Math.Sqrt(.5);
            long serial = d.ChangeSerial; var changed = new HashSet<TileCoord>();
            inputs.Frame(new GeneratorModelFrame(.3, -.2, .1, 0, s, 0, s));
            Assert.That(d.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True); Assert.That(changed, Is.Not.Empty, "a moved root redraws the shape's layer");
            var moved = fill.Mask.EvaluateOutputRegion(0, 0, W, H);
            Assert.That(moved, Is.Not.EqualTo(atRoot));
            var r0 = Rot(0, 90, 0);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var p = Point(x, y); double[] q = { p[0] - .3, p[1] + .2, p[2] - .1 }, root = new double[3];
                    for (int i = 0; i < 3; i++) for (int k = 0; k < 3; k++) root[i] += r0[k, i] * q[k];
                    Assert.That((int)moved[y * W + x], Is.EqualTo(255 - B(RefBase(v, root))).Within(1), x + "," + y);
                }
            // 同じ値の枠を答え直しても描き直さない
            serial = d.ChangeSerial; changed.Clear();
            inputs.Frame(new GeneratorModelFrame(.3, -.2, .1, 0, s, 0, s));
            Assert.That(d.RefreshGeneratorInputs(), Is.False);
            // 分からない・読めないときは入力のまま（黒くしない）と理由。ほかの種類の Generator は枠を使わない
            var plain = fill.Mask.EvaluateOutputRegion(0, 0, W, H).Select(_ => (byte)0).ToArray();
            inputs.Frame(null);
            var status = d.GetGeneratorStatus(fill.Id, e.Id);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("model root"));
            Assert.That(fill.Mask.EvaluateOutputRegion(0, 0, W, H), Is.EqualTo(plain), "passes the mask through");
            Assert.That(d.InactiveGenerators().Single(), Does.Contain("Shape gradient has no effect"));
            inputs.Throw();
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Reason, Does.Contain("no root here"));
            inputs.Put(TestMeshMaps.Make(MeshMapKind.Curvature, W, H, (x, y, _) => .9));
            var wear = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.EdgeWear)));
            Assert.That(d.GetGeneratorStatus(fill.Id, wear.Id).Active, Is.True, "edge wear does not need the root");
            // 入力が枠を答えない（IGeneratorModelFrame でない）ときはルートを原点とする
            d.RemoveFilter(fill.Id, wear.Id);
            d.GeneratorInputs = new TestGeneratorInputs().Put(Positions());
            Assert.That(fill.Mask.EvaluateOutputRegion(0, 0, W, H), Is.EqualTo(atRoot));
        }

        // ───────── ほかのパラメーターとの組み合わせ ─────────

        [Test] public void LevelsInvertBreakupBlendAndStrengthCombineAndTheBytesDoNotDependOnThreads()
        {
            var v = new ShapeVolume(GeneratorShape.Sphere, -.1, .3, .4, 0, 0, 0, 2.6, 1, 1, .8);
            var shaped = Shape(v).WithLevels(.2, .8, .5).WithInvert(true);
            var (d, fill) = MaskedFill();
            d.GeneratorInputs = new TestGeneratorInputs().Put(Positions());
            var e = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(shaped));
            var hide = fill.Mask.EvaluateOutputRegion(0, 0, W, H);
            for (int y = 0; y < H; y += 2) for (int x = 0; x < W; x += 2)
                Assert.That((int)hide[y * W + x], Is.EqualTo(255 - B(Levels(RefBase(v, Point(x, y)), .2, .8, .5, true))).Within(1), "levels " + x + "," + y);
            // 崩し（モデルの上）は削るだけ: t (1 − 0.7) 〜 t
            var broken = shaped.WithNoise(.7, .1, 11, GeneratorNoiseSpace.Model);
            Assert.That(broken.UsedMaps, Is.EqualTo(new[] { MeshMapKind.Position }), "the breakup reads the same map");
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(broken));
            var worn = fill.Mask.EvaluateOutputRegion(0, 0, W, H);
            int lower = 0;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                double t = Levels(RefBase(v, Point(x, y)), .2, .8, .5, true);
                int got = 255 - worn[y * W + x];
                Assert.That(got, Is.LessThanOrEqualTo(B(t) + 1).And.GreaterThanOrEqualTo(B(t * .3) - 1), x + "," + y);
                if (got < B(t) - 3) lower++;
            }
            Assert.That(lower, Is.GreaterThan(100), "the breakup wears patches away");
            // スレッドの数・ブロックの大きさによらない
            byte[] Again(int threads, int block)
            {
                CoreParallelism.MaxDegreeOfParallelism = threads;
                var (d2, f2) = MaskedFill(); d2.FilterBlockPixels = block;
                d2.GeneratorInputs = new TestGeneratorInputs().Put(Positions());
                d2.AddFilter(f2.Id, FilterTarget.Mask, FilterSettings.FromGenerator(broken));
                var region = f2.Mask.EvaluateOutputRegion(0, 0, W, H);
                var tiled = new byte[W * H]; for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) tiled[y * W + x] = f2.Mask.OutputHideAt(x, y);
                Assert.That(tiled, Is.EqualTo(region), "the cached tiles equal the one-piece evaluation");
                return region;
            }
            Assert.That(Again(1, 256), Is.EqualTo(worn)); Assert.That(Again(3, 16), Is.EqualTo(worn)); Assert.That(Again(0, 32), Is.EqualTo(worn));
            // 合成と強さ（層の画素: Roughness 102 に掛ける。半分の強さ）
            var raster = d.AddLayer("Paint"); d.SetChannelEnabled(raster.Id, PaintChannel.Roughness, true); var rough = raster.GetChannel(PaintChannel.Roughness);
            rough.SetPixel(10, 10, new Rgba32(200, 200, 200, 255)); rough.SetPixel(40, 30, new Rgba32(7, 8, 9, 0));
            var multiply = d.AddFilter(raster.Id, FilterTarget.Content, FilterSettings.FromGenerator(Shape(v).WithBlend(GeneratorBlend.Multiply)), new[] { PaintChannel.Roughness }, strength: .5);
            double sIn = 200 / 255.0, g = RefBase(v, Point(10, 10));
            Assert.That((int)raster.GetOutputPixel(PaintChannel.Roughness, 10, 10).R, Is.EqualTo(B(sIn + .5 * (sIn * g - sIn))).Within(1));
            Assert.That(raster.GetOutputPixel(PaintChannel.Roughness, 40, 30), Is.EqualTo(new Rgba32(7, 8, 9, 0)), "a transparent pixel keeps its RGB");
        }

        [Test] public void MissingOrMismatchedMapsAndEmptyTexelsPassTheInputThrough()
        {
            var (d, fill) = MaskedFill();
            var e = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Shape(ShapeVolume.Default.WithSize(6, 9, 6))));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Reason, Does.Contain("No mesh maps are connected"));
            Assert.That(fill.Mask.EvaluateOutputRegion(0, 0, W, H).All(h => h == 0), Is.True, "nothing turns black or hidden");
            var inputs = new TestGeneratorInputs(); d.GeneratorInputs = inputs;
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Reason, Does.Contain("Position has not been baked"));
            inputs.Put(Positions(w: 32, h: 32));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Reason, Does.Contain("baked at 32×32"));
            Assert.That(fill.Mask.EvaluateOutputRegion(0, 0, W, H).All(h => h == 0), Is.True);
            inputs.Put(Positions(coverage: (x, y) => x < 8 ? MeshTexelCoverage.Empty : MeshTexelCoverage.Covered));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Active, Is.True);
            var hide = fill.Mask.EvaluateOutputRegion(0, 0, W, H);
            Assert.That(hide[20 * W + 3], Is.Zero, "an empty texel keeps the input");
            Assert.That(hide[20 * W + 30], Is.Zero, "inside the big box: shown");
            Assert.That(d.InactiveGenerators(), Is.Empty);
        }

        // ───────── Undo・取消・拒否 ─────────

        [Test] public void DragsCoalesceUndoAndCancelAndBadValuesAreRefused()
        {
            var (d, fill) = MaskedFill();
            d.GeneratorInputs = new TestGeneratorInputs().Put(Positions());
            var start = ShapeVolume.Default.WithSize(1.5, 2, 1);
            var e = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Shape(start)));
            var before = d.Composite(PaintChannel.Color); int steps = d.UndoCount;
            for (int i = 1; i <= 6; i++) d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Shape(start.WithCenter(.05 * i, 0, 0))), coalesce: true);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "one drag, one step");
            var dragged = d.Composite(PaintChannel.Color);
            Assert.That(dragged, Is.Not.EqualTo(before));
            d.EndCoalescing();
            Assert.That(d.Undo(), Is.True); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before), "undoes to before the drag");
            Assert.That(d.Redo(), Is.True); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(dragged));
            // 取り消したドラッグは履歴に残らない（やり直しも増えない）
            d.EndCoalescing(); steps = d.UndoCount; int redo = d.RedoCount; long bytes = d.HistoryBytes;
            for (int i = 1; i <= 4; i++) d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Shape(start.WithRotation(0, 10 * i, 0))), coalesce: true);
            Assert.That(d.CancelCoalescing(), Is.True);
            Assert.That(fill.Mask.Filters.Single().Settings.Generator.Volume, Is.EqualTo(start.WithCenter(.05 * 6, 0, 0)), "back to the end of the kept drag");
            Assert.That((d.UndoCount, d.RedoCount, d.HistoryBytes), Is.EqualTo((steps, redo, bytes)));
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(dragged));
            Assert.That(d.CancelCoalescing(), Is.False, "nothing open: nothing changes");
            Assert.That(d.UndoCount, Is.EqualTo(steps));
            // ストロークの最中は変えない
            var stroke = d.BeginMaskStroke(fill.Id, new BrushSettings { Radius = 4, Color = new Rgba32(0, 0, 0, 255) });
            Assert.That(() => d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Shape(start))), Throws.InvalidOperationException);
            stroke.Cancel();

            // 値の範囲と種類
            var g = GeneratorSettings.Default(GeneratorType.ShapeGradient);
            Assert.That(g.Volume, Is.EqualTo(ShapeVolume.Default)); Assert.That(g.UsedMaps, Is.EqualTo(new[] { MeshMapKind.Position }));
            Assert.That(GeneratorSettings.CandidateMaps(GeneratorType.ShapeGradient), Is.EqualTo(new[] { MeshMapKind.Position }));
            foreach (var bad in new[]
            {
                ShapeVolume.Default.WithSize(0, 1, 1), ShapeVolume.Default.WithSize(1, -1, 1), ShapeVolume.Default.WithSize(1, 1, 2e6),
                ShapeVolume.Default.WithCenter(double.NaN, 0, 0), ShapeVolume.Default.WithCenter(0, 2e6, 0), ShapeVolume.Default.WithRotation(0, 0, 400),
                ShapeVolume.Default.WithRotation(double.PositiveInfinity, 0, 0), ShapeVolume.Default.WithFalloff(1.5), ShapeVolume.Default.WithFalloff(-.1),
                ShapeVolume.Default.WithShape((GeneratorShape)7),
            })
            {
                Assert.That(bad.Refusal(), Is.Not.Null, bad.ToString());
                Assert.That(() => g.WithVolume(bad), Throws.InstanceOf<ArgumentOutOfRangeException>(), bad.ToString());
            }
            Assert.That(g.WithVolume(ShapeVolume.Default.WithShape(GeneratorShape.Sphere).WithSize(2, 1e-6, 1e6)).Volume.SizeX, Is.EqualTo(2), "unused sizes only need to be in range");
            Assert.That(() => GeneratorSettings.Default(GeneratorType.EdgeWear).WithVolume(ShapeVolume.Default.WithSize(2, 2, 2)), Throws.ArgumentException.With.Message.Contains("shape gradient"));
            Assert.That(() => GeneratorSettings.FromValues(GeneratorType.Dirt, 0, 1, 0, false, 0, .05, 0, GeneratorNoiseSpace.Model, GeneratorBlend.Multiply, .5, 1, 0, 1, 0, false, null, ShapeVolume.Default.WithFalloff(.2)),
                Throws.ArgumentException);
            Assert.That(() => g.WithPin(MeshMapKind.Curvature, new string('a', 64)), Throws.ArgumentException, "it reads only the Position map");
            // 型（Normal は単位ベクトル）と作業メモリの予算は他の Generator と同じく断る
            var l = d.AddLayer("L");
            Assert.That(d.FilterRefusal(l.Id, FilterTarget.Content, FilterSettings.FromGenerator(g), PaintChannel.Normal), Does.Contain("unit vectors"));
            var small = new PaintDocument(W, H, T); var sl = small.AddLayer("S"); small.ClearHistory(); small.FilterWorkingBudgetBytes = 1000;
            Assert.That(() => small.AddFilter(sl.Id, FilterTarget.Content, FilterSettings.FromGenerator(g)), Throws.InvalidOperationException.With.Message.Contains("working memory"));
            Assert.That(sl.Filters, Is.Empty); Assert.That(small.UndoCount, Is.Zero);
        }

        // ───────── 保存 ─────────

        [Test] public void NativeVersion13RoundTripsTheShapeAndRefusesOlderVersionsAndUnknownShapes()
        {
            var (d, fill) = MaskedFill();
            var box = new ShapeVolume(GeneratorShape.Box, .1, -.2, .3, 12.5, -45, 170, .7, 1.9, 2.2, .35);
            var sphere = new ShapeVolume(GeneratorShape.Sphere, 0, .5, 0, 0, 0, 0, 1.2, 3, 4, 0);
            var plane = new ShapeVolume(GeneratorShape.Plane, 0, 0, -.25, 90, 0, -30, 5, .4, 6, .75);
            d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Shape(box).WithLevels(.1, .9, .5)));
            d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Shape(plane).WithBlend(GeneratorBlend.Multiply).WithNoise(.4, .2, -7, GeneratorNoiseSpace.Model)), strength: .7);
            d.AddFilter(fill.Id, FilterTarget.Content, FilterSettings.FromGenerator(Shape(sphere).WithInvert(true).WithPin(MeshMapKind.Position, new string('c', 64))), new[] { PaintChannel.Roughness }, enabled: false);
            d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.EdgeWear)));
            var bytes = DocumentBinary.Write(d);
            Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(DocumentBinary.CurrentVersion)); Assert.That(DocumentBinary.CurrentVersion, Is.GreaterThanOrEqualTo(13), "the shape gradient came with version 13");
            var read = DocumentBinary.Read(bytes); var rl = read.GetLayer(fill.Id);
            Assert.That(rl.Mask.Filters.Select(f => (f.Id, f.Settings, f.Strength)), Is.EqualTo(fill.Mask.Filters.Select(f => (f.Id, f.Settings, f.Strength))));
            Assert.That(rl.Filters.Select(f => (f.Id, f.Settings, f.Enabled)), Is.EqualTo(fill.Filters.Select(f => (f.Id, f.Settings, f.Enabled))));
            Assert.That(rl.Mask.Filters[1].Settings.Generator.Volume, Is.EqualTo(plane), "the unused values of the plane are kept too");
            Assert.That(DocumentBinary.Write(read), Is.EqualTo(bytes), "write → read → write is byte-identical");
            var inputs = new TestGeneratorInputs().Put(Positions()).Put(TestMeshMaps.Make(MeshMapKind.Curvature, W, H, (x, y, _) => .8));
            d.GeneratorInputs = inputs; read.GeneratorInputs = inputs;
            Assert.That(read.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));

            // 古い版: 形のグラデーション（種類 5）は版 12（ロック）以前には無い
            foreach (int older in new[] { 11, 12 })
            {
                var old = (byte[])bytes.Clone(); BitConverter.GetBytes(older).CopyTo(old, 8);
                Assert.That(() => DocumentBinary.Read(old), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("Unknown generator type 5"), "version " + older);
            }
            // 形のグラデーションもロックも持たない文書は版 11 と同じ並び（版の数だけが違う）
            var wear = new PaintDocument(W, H, T); var wl = wear.AddLayer("P"); wear.AddLayerMask(wl.Id);
            wear.AddFilter(wl.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Dirt)));
            var asEleven = ArchiveTestUtil.AsVersion(DocumentBinary.Write(wear), "P", 11);
            Assert.That(DocumentBinary.Read(asEleven).GetLayer(wl.Id).Mask.Filters.Single().Settings, Is.EqualTo(wear.GetLayer(wl.Id).Mask.Filters.Single().Settings));

            // 知らない形・壊れた値・途中で切れたものは断る（既定に戻さない）
            var one = new PaintDocument(W, H, T); var ol = one.AddLayer("O"); one.AddLayerMask(ol.Id);
            var og = one.AddFilter(ol.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Shape(box)));
            var ob = DocumentBinary.Write(one);
            int at = IndexOf(ob, og.Id.ToByteArray()) + 16;
            int gen = at + 4 + 4 + 1 + 8 + 4 + 8 + 4 + 4 + 1 + 5 * 8; // Generator の段の頭
            int shape = gen + 4 + 4 + 3 * 8 + 1 + 8 + 8 + 4 + 4 + 4 + 8 + 4 + 3 * 8 + 1 + 4; // ピン 0 個の後
            Assert.That(BitConverter.ToInt32(ob, gen), Is.EqualTo((int)GeneratorType.ShapeGradient));
            Assert.That(BitConverter.ToInt32(ob, shape), Is.EqualTo((int)GeneratorShape.Box));
            Assert.That(BitConverter.ToDouble(ob, shape + 4), Is.EqualTo(box.CenterX)); Assert.That(BitConverter.ToDouble(ob, shape + 4 + 9 * 8), Is.EqualTo(box.Falloff));
            Assert.That(ob.Length, Is.EqualTo(shape + 4 + 10 * 8 + 1), "the volume, then the layer's canvas-path flag and nothing else");
            void Refused(int offset, byte[] value, string message)
            {
                var bad = (byte[])ob.Clone(); value.CopyTo(bad, offset);
                Assert.That(() => DocumentBinary.Read(bad), Throws.InstanceOf<InvalidDataException>().With.Message.Contains(message), message);
            }
            Refused(shape, BitConverter.GetBytes(3), "Unknown generator shape 3");
            Refused(shape, BitConverter.GetBytes(-1), "Unknown generator shape -1");
            Refused(shape + 4 + 6 * 8, BitConverter.GetBytes(0.0), "Invalid generator parameters"); // 大きさ 0
            Refused(shape + 4 + 3 * 8, BitConverter.GetBytes(double.NaN), "Invalid generator parameters"); // 回転 NaN
            Refused(shape + 4 + 9 * 8, BitConverter.GetBytes(2.0), "Invalid generator parameters"); // やわらかさ 2
            for (int cut = 1; cut <= 4 + 10 * 8 + 1; cut += 7)
                Assert.That(() => DocumentBinary.Read(ob.Take(ob.Length - cut).ToArray()), Throws.InstanceOf<InvalidDataException>(), "cut " + cut);
            Assert.That(DocumentBinary.Read(ob).GetLayer(ol.Id).Mask.Filters.Single().Settings, Is.EqualTo(og.Settings));
            // 大きさを変えても（画素ではないので）そのまま
            Assert.That(d.Resampled(W * 2, H * 2, CanvasResampling.Bilinear).Document.GetLayer(fill.Id).Mask.Filters[0].Settings, Is.EqualTo(fill.Mask.Filters[0].Settings));
        }
        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++) { int k = 0; while (k < needle.Length && haystack[i + k] == needle[k]) k++; if (k == needle.Length) return i; }
            throw new AssertionException("not found");
        }
    }
}
