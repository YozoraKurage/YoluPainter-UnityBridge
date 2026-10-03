using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>Generator（Core）: 合成したメッシュマップで値が式どおり（エッジの摩耗・汚れ・位置の勾配・厚み・向き、レベル・反転・合成・強さ）、
    /// 崩しのノイズが決定的でモデルの上では UV の継ぎ目で途切れない、マップが無い・古い・大きさが違う・別のベイク（ピン）・空のテクセルでは
    /// 入力をそのまま通して理由を出す（黒くしない）、マップが変わると Generator のある層だけを「変わった」と答えて古いタイルを使わない、
    /// Undo/Redo とスライダーのまとめ、型・層の種類・予算の拒否、焼き込み（マップが無ければ断る）、ネイティブ版 11 の保存と版 10 の読み込み・
    /// 知らない値の拒否、PSD は焼くまで断る、スレッドの数によらない、大きさの変更で設定がそのまま残る。</summary>
    public sealed class GeneratorTests
    {
        const int W = 64, H = 48, T = 16;
        [TearDown] public void ResetThreads() { CoreParallelism.MaxDegreeOfParallelism = 0; }

        static byte B(double v) { double x = v * 255 + .5; return x >= 255 ? (byte)255 : x > 0 ? (byte)(int)x : (byte)0; }
        static double C01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
        /// <summary>レベル・やわらかさ・反転（Core と同じ式・同じ順の演算）。</summary>
        static double Levels(double b, double low, double high, double softness, bool invert)
        {
            double t = C01((b - low) / (high - low));
            if (softness > 0) t += softness * (t * t * (3 - 2 * t) - t);
            return invert ? 1 - t : t;
        }
        static GeneratorSettings Plain(GeneratorType type) => GeneratorSettings.Default(type).WithLevels(0, 1, 0).WithNoise(0, .05, 0, GeneratorNoiseSpace.Model).WithBlend(GeneratorBlend.Replace);

        /// <summary>白い塗りつぶしの層とマスク（隠す量 0）。</summary>
        static (PaintDocument d, PaintLayer fill) MaskedFill(int w = W, int h = H)
        {
            var d = new PaintDocument(w, h, T);
            var fill = d.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(255, 255, 255, 255) }, { PaintChannel.Roughness, new Rgba32(102, 102, 102, 255) } });
            d.AddLayerMask(fill.Id); d.ClearHistory();
            return (d, fill);
        }
        static BakedMeshMap Curvature(Func<int, int, double> c, int w = W, int h = H, string key = "test", Func<int, int, MeshTexelCoverage> coverage = null)
            => TestMeshMaps.Make(MeshMapKind.Curvature, w, h, (x, y, _) => c(x, y), coverage, key);

        // ───────────── 式どおりの値 ─────────────

        [Test] public void EdgeWearFollowsTheConvexCurvatureThroughTheLevels()
        {
            var (d, fill) = MaskedFill();
            var inputs = new TestGeneratorInputs().Put(Curvature((x, y) => x / (double)(W - 1)));
            d.GeneratorInputs = inputs;
            var g = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Plain(GeneratorType.EdgeWear)));
            Assert.That(d.GetGeneratorStatus(fill.Id, g.Id).Active, Is.True);
            for (int x = 0; x < W; x++)
            {
                double b = C01(2 * (TestMeshMaps.Q(x / (double)(W - 1)) - .5));
                Assert.That(fill.Mask.OutputHideAt(x, 7), Is.EqualTo((byte)(255 - B(b))), "x " + x);
            }
            // 低・高・やわらかさ・反転
            var shaped = Plain(GeneratorType.EdgeWear).WithLevels(.2, .7, 1).WithInvert(true);
            d.SetFilterSettings(fill.Id, g.Id, FilterSettings.FromGenerator(shaped));
            var region = fill.Mask.EvaluateOutputRegion(0, 0, W, H);
            for (int x = 0; x < W; x++)
            {
                double b = C01(2 * (TestMeshMaps.Q(x / (double)(W - 1)) - .5));
                byte expected = (byte)(255 - B(Levels(b, .2, .7, 1, true)));
                Assert.That(fill.Mask.OutputHideAt(x, 30), Is.EqualTo(expected), "shaped x " + x);
                Assert.That(region[30 * W + x], Is.EqualTo(expected), "one-piece reference x " + x);
            }
            // 合成: 見える度合い（1 − 隠す量）が Generator の値
            var px = d.CompositePixel(PaintChannel.Color, 50, 3);
            Assert.That(px.A, Is.EqualTo(B(1 - fill.Mask.OutputHideAt(50, 3) / 255.0)).Within(1));
        }

        [Test] public void DirtMixesOcclusionAndCavitiesByTheBalance()
        {
            var (d, fill) = MaskedFill();
            var ao = TestMeshMaps.Make(MeshMapKind.AmbientOcclusion, W, H, (x, y, _) => x / (double)(W - 1));
            var curv = Curvature((x, y) => y / (double)(H - 1));
            d.GeneratorInputs = new TestGeneratorInputs().Put(ao).Put(curv);
            var settings = Plain(GeneratorType.Dirt).WithBalance(.3).WithLevels(.1, .9, .5).WithBlend(GeneratorBlend.Multiply);
            Assert.That(settings.UsedMaps, Is.EqualTo(new[] { MeshMapKind.AmbientOcclusion, MeshMapKind.Curvature }));
            d.AddFilter(fill.Id, FilterTarget.Content, FilterSettings.FromGenerator(settings), new[] { PaintChannel.Roughness });
            for (int y = 0; y < H; y += 5)
                for (int x = 0; x < W; x += 3)
                {
                    double b = 0; b += (1 - .3) * (1 - TestMeshMaps.Q(x / (double)(W - 1))); b += .3 * C01(2 * (.5 - TestMeshMaps.Q(y / (double)(H - 1))));
                    double v = Levels(b, .1, .9, .5, false);
                    byte r = B(MathRef.Unit(102) * v);
                    Assert.That(fill.GetOutputPixel(PaintChannel.Roughness, x, y), Is.EqualTo(new Rgba32(r, r, r, 255)), x + "," + y);
                }
            Assert.That(fill.GetOutputPixel(PaintChannel.Color, 5, 5), Is.EqualTo(new Rgba32(255, 255, 255, 255)), "applied only to its channel");
            // 片方だけを読む釣り合い
            Assert.That(Plain(GeneratorType.Dirt).WithBalance(0).UsedMaps, Is.EqualTo(new[] { MeshMapKind.AmbientOcclusion }));
            Assert.That(Plain(GeneratorType.Dirt).WithBalance(1).UsedMaps, Is.EqualTo(new[] { MeshMapKind.Curvature }));
        }

        [Test] public void PositionThicknessAndDirectionReadTheirMaps()
        {
            var (d, fill) = MaskedFill();
            var position = TestMeshMaps.Make(MeshMapKind.Position, W, H, (x, y, c) => c == 0 ? x / (double)(W - 1) : c == 1 ? y / (double)(H - 1) : .5);
            var thickness = TestMeshMaps.Make(MeshMapKind.Thickness, W, H, (x, y, _) => (x + y) / (double)(W + H - 2));
            // 法線は xy 平面で x とともに回る（n = (cos θ, sin θ, 0)）
            Func<int, double> theta = x => x / (double)(W - 1) * Math.PI;
            var normal = TestMeshMaps.Make(MeshMapKind.WorldNormal, W, H, (x, y, c) => c == 0 ? Math.Cos(theta(x)) * .5 + .5 : c == 1 ? Math.Sin(theta(x)) * .5 + .5 : .5);
            d.GeneratorInputs = new TestGeneratorInputs().Put(position).Put(thickness).Put(normal);
            var g = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Plain(GeneratorType.PositionGradient).WithAxis(1)));
            for (int y = 0; y < H; y += 7) Assert.That(fill.Mask.OutputHideAt(9, y), Is.EqualTo((byte)(255 - B(TestMeshMaps.Q(y / (double)(H - 1))))), "position y " + y);
            d.SetFilterSettings(fill.Id, g.Id, FilterSettings.FromGenerator(Plain(GeneratorType.Thickness)));
            for (int x = 0; x < W; x += 7) Assert.That(fill.Mask.OutputHideAt(x, 11), Is.EqualTo((byte)(255 - B(TestMeshMaps.Q((x + 11) / (double)(W + H - 2))))), "thickness x " + x);
            d.SetFilterSettings(fill.Id, g.Id, FilterSettings.FromGenerator(Plain(GeneratorType.Direction).WithDirection(0, 2, 0)));
            for (int x = 0; x < W; x += 3)
            {
                double nx = TestMeshMaps.Q(Math.Cos(theta(x)) * .5 + .5) * 2 - 1, ny = TestMeshMaps.Q(Math.Sin(theta(x)) * .5 + .5) * 2 - 1, nz = TestMeshMaps.Q(.5) * 2 - 1;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz), dot = (nx * 0 + ny * 1 + nz * 0) / len;
                Assert.That(fill.Mask.OutputHideAt(x, 20), Is.EqualTo((byte)(255 - B(C01((dot + 1) * .5)))), "direction x " + x);
            }
            Assert.That(fill.Mask.OutputHideAt(W / 2, 0), Is.LessThan(5), "facing the direction: shown");
            // ベントノーマルを選ぶと、そのマップが無いので効かない（理由を出す）
            d.SetFilterSettings(fill.Id, g.Id, FilterSettings.FromGenerator(Plain(GeneratorType.Direction).WithBentNormal(true)));
            var status = d.GetGeneratorStatus(fill.Id, g.Id);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("BentNormal"));
            Assert.That(fill.Mask.OutputHideAt(W / 2, 0), Is.Zero, "passes the empty mask through");
        }

        [Test] public void BlendsAndStrengthCombineWithTheStageInput()
        {
            var (d, fill) = MaskedFill();
            var thickness = TestMeshMaps.Make(MeshMapKind.Thickness, W, H, (x, y, _) => x / (double)(W - 1));
            d.GeneratorInputs = new TestGeneratorInputs().Put(thickness);
            var e = d.AddFilter(fill.Id, FilterTarget.Content, FilterSettings.FromGenerator(Plain(GeneratorType.Thickness)), new[] { PaintChannel.Roughness });
            double s = MathRef.Unit(102);
            foreach (GeneratorBlend blend in Enum.GetValues(typeof(GeneratorBlend)))
                foreach (double strength in new[] { 1, .35 })
                {
                    d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Plain(GeneratorType.Thickness).WithBlend(blend)));
                    d.SetFilterStrength(fill.Id, e.Id, strength);
                    for (int x = 0; x < W; x += 9)
                    {
                        double v = TestMeshMaps.Q(x / (double)(W - 1)), c;
                        switch (blend)
                        {
                            case GeneratorBlend.Multiply: c = s * v; break;
                            case GeneratorBlend.Replace: c = v; break;
                            case GeneratorBlend.Screen: c = 1 - (1 - s) * (1 - v); break;
                            case GeneratorBlend.Max: c = Math.Max(s, v); break;
                            case GeneratorBlend.Min: c = Math.Min(s, v); break;
                            case GeneratorBlend.Add: c = Math.Min(1, s + v); break;
                            default: c = Math.Max(0, s - v); break;
                        }
                        byte expected = B(strength >= 1 ? c : s + (c - s) * strength);
                        Assert.That(fill.GetOutputPixel(PaintChannel.Roughness, x, 2).R, Is.EqualTo(expected), blend + " " + strength + " x " + x);
                    }
                }
            // 透明な画素の RGB と不透明度はそのまま
            var raster = d.AddLayer("Paint"); var color = raster.GetChannel(PaintChannel.Color);
            color.SetPixel(3, 3, new Rgba32(10, 20, 30, 0)); color.SetPixel(40, 3, new Rgba32(10, 20, 30, 128));
            d.AddFilter(raster.Id, FilterTarget.Content, FilterSettings.FromGenerator(Plain(GeneratorType.Thickness)), new[] { PaintChannel.Color });
            Assert.That(raster.GetOutputPixel(PaintChannel.Color, 3, 3), Is.EqualTo(new Rgba32(10, 20, 30, 0)));
            byte g40 = B(TestMeshMaps.Q(40 / (double)(W - 1)));
            Assert.That(raster.GetOutputPixel(PaintChannel.Color, 40, 3), Is.EqualTo(new Rgba32(g40, g40, g40, 128)));
            Assert.That(raster.GetPixel(PaintChannel.Color, 40, 3), Is.EqualTo(new Rgba32(10, 20, 30, 128)), "the painted source is unchanged");
        }

        [Test] public void BreakupNoiseIsDeterministicAndContinuousOnTheModel()
        {
            // 位置が左右で鏡写し（x と W−1−x が同じ位置）: UV の継ぎ目の両側が同じ点を表す
            var position = TestMeshMaps.Make(MeshMapKind.Position, W, H, (x, y, c) => c == 0 ? Math.Min(x, W - 1 - x) / (double)(W - 1) : c == 1 ? y / (double)(H - 1) : .25);
            var curv = Curvature((x, y) => .75);
            byte[] Run(GeneratorSettings g)
            {
                var (d, fill) = MaskedFill();
                d.GeneratorInputs = new TestGeneratorInputs().Put(position).Put(curv);
                d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(g));
                return fill.Mask.EvaluateOutputRegion(0, 0, W, H);
            }
            var model = Plain(GeneratorType.EdgeWear).WithNoise(.6, .08, 7, GeneratorNoiseSpace.Model);
            var a = Run(model); var again = Run(model);
            Assert.That(again, Is.EqualTo(a), "the same seed gives the same breakup");
            Assert.That(a.Distinct().Count(), Is.GreaterThan(10), "the breakup varies");
            // 崩しは削るだけ: 値 t = 0.5 は t (1 − 0.6) 〜 t のあいだ（隠す量では 255 − B(0.5) 〜 255 − B(0.2)）
            Assert.That(a.Min(), Is.GreaterThanOrEqualTo((byte)(255 - B(TestMeshMaps.Q(.75) * 2 - 1))));
            Assert.That(a.Max(), Is.LessThanOrEqualTo((byte)(255 - B((TestMeshMaps.Q(.75) * 2 - 1) * .4))));
            Assert.That(a.Count(v => v > 255 - B(.5) + 20), Is.GreaterThan(50), "some patches are worn away");
            Assert.That(Run(model.WithNoise(.6, .08, 8, GeneratorNoiseSpace.Model)), Is.Not.EqualTo(a), "another seed");
            for (int y = 0; y < H; y++) for (int x = 0; x < W / 2; x++) Assert.That(a[y * W + x], Is.EqualTo(a[y * W + W - 1 - x]), "the same point on the model, " + x + "," + y);
            var uv = Run(Plain(GeneratorType.EdgeWear).WithNoise(.6, .08, 7, GeneratorNoiseSpace.Uv));
            int differ = 0; for (int y = 0; y < H; y++) for (int x = 0; x < W / 2; x++) if (uv[y * W + x] != uv[y * W + W - 1 - x]) differ++;
            Assert.That(differ, Is.GreaterThan(100), "UV-space noise is placed on the texture, not the model");
            // 量 0 なら崩さない（位置のマップも読まない）
            Assert.That(Plain(GeneratorType.EdgeWear).UsedMaps, Is.EqualTo(new[] { MeshMapKind.Curvature }));
            Assert.That(model.UsedMaps, Is.EqualTo(new[] { MeshMapKind.Curvature, MeshMapKind.Position }));
            Assert.That(Run(Plain(GeneratorType.EdgeWear)).Distinct().Single(), Is.EqualTo((byte)(255 - B(C01(2 * (TestMeshMaps.Q(.75) - .5))))));
            // 平らな所（0）には、崩しても何も出ない
            var flat = Curvature((x, y) => .5);
            var (fd, ff) = MaskedFill(); fd.GeneratorInputs = new TestGeneratorInputs().Put(position).Put(flat);
            fd.AddFilter(ff.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Plain(GeneratorType.EdgeWear).WithNoise(1, .08, 7, GeneratorNoiseSpace.Model)));
            Assert.That(ff.Mask.EvaluateOutputRegion(0, 0, W, H).Distinct().Single(), Is.EqualTo((byte)255));
            // スレッドの数によらない
            CoreParallelism.MaxDegreeOfParallelism = 1; var one = Run(model);
            CoreParallelism.MaxDegreeOfParallelism = 3; var three = Run(model);
            Assert.That(one, Is.EqualTo(a)); Assert.That(three, Is.EqualTo(a));
        }

        // ───────────── 欠けた入力 ─────────────

        [Test] public void MissingStaleOrMismatchedMapsPassTheInputThroughWithAReason()
        {
            var (d, fill) = MaskedFill();
            var stroke = d.BeginMaskStroke(fill.Id, new BrushSettings { Radius = 6, Hardness = 1, Color = new Rgba32(0, 0, 0, 255) });
            stroke.Add(new BrushSample(6, 20)); stroke.Commit();
            Assert.That(fill.Mask.Surface.GetPixel(5, 20).A, Is.GreaterThan(200), "painted hidden");
            var plain = d.Composite(PaintChannel.Color);
            var e = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Plain(GeneratorType.EdgeWear)));
            // 何もつながっていない
            var status = d.GetGeneratorStatus(fill.Id, e.Id);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("No mesh maps are connected"));
            Assert.That(status.Maps.Single().Kind, Is.EqualTo(MeshMapKind.Curvature)); Assert.That(status.Maps.Single().Usable, Is.False);
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(plain), "passes the mask through: not black, not hidden");
            Assert.That(d.InactiveGenerators().Single(), Does.Contain("'Fill' (mask): Edge wear has no effect"));
            // 古い（口が理由を返す）
            var inputs = new TestGeneratorInputs().Refuse(MeshMapKind.Curvature, "Curvature is stale: baked at 32×32, the document is 64×48.");
            d.GeneratorInputs = inputs;
            status = d.GetGeneratorStatus(fill.Id, e.Id);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("stale"));
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(plain));
            // 大きさが違う（口が使えると言っても文書が断る）
            inputs.Put(Curvature((x, y) => 1, 32, 32));
            status = d.GetGeneratorStatus(fill.Id, e.Id);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("baked at 32×32"));
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(plain));
            // 使えるマップ: 空のテクセル（x < 10）は入力のまま、ほかは Generator の値
            inputs.Put(Curvature((x, y) => 1, coverage: (x, y) => x < 10 ? MeshTexelCoverage.Empty : x < 14 ? MeshTexelCoverage.Padding : MeshTexelCoverage.Covered));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Active, Is.True); Assert.That(d.InactiveGenerators(), Is.Empty);
            Assert.That(fill.Mask.OutputHideAt(5, 40), Is.Zero, "empty texel: the input");
            Assert.That(fill.Mask.OutputHideAt(5, 20), Is.EqualTo(fill.Mask.Surface.GetPixel(5, 20).A), "empty texel keeps what was painted");
            Assert.That(fill.Mask.OutputHideAt(12, 40), Is.Zero, "padding is data: curvature 1 shows the layer");
            Assert.That(fill.Mask.OutputHideAt(30, 40), Is.Zero);
            Assert.That(fill.Mask.Surface.GetPixel(11, 20).A, Is.GreaterThan(0));
            Assert.That(fill.Mask.OutputHideAt(11, 20), Is.Zero, "Replace ignores what was painted");
            // 口が例外を投げても合成は止まらない（理由になる）
            d.GeneratorInputs = new ThrowingInputs();
            status = d.GetGeneratorStatus(fill.Id, e.Id);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("failed"));
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(plain));
        }
        sealed class ThrowingInputs : IGeneratorInputs
        {
            public long Revision => 1;
            public bool TryGetMap(MeshMapKind kind, out BakedMeshMap map, out string reason) => throw new InvalidOperationException("broken provider");
        }

        [Test] public void APinnedGeneratorReadsOnlyThatBake()
        {
            var (d, fill) = MaskedFill();
            var first = Curvature((x, y) => 1, key: "radius=0.02"); var second = Curvature((x, y) => 1, key: "radius=0.05");
            Assert.That(first.Provenance.ConditionKey, Is.Not.EqualTo(second.Provenance.ConditionKey));
            var inputs = new TestGeneratorInputs().Put(first); d.GeneratorInputs = inputs;
            var pinned = Plain(GeneratorType.EdgeWear).WithPin(MeshMapKind.Curvature, first.Provenance.ConditionKey);
            var e = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(pinned.WithBlend(GeneratorBlend.Subtract)));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Active, Is.True);
            Assert.That(fill.Mask.OutputHideAt(30, 30), Is.EqualTo(255), "visibility 1 − 1");
            inputs.Put(second); // 別の条件で焼き直した
            var status = d.GetGeneratorStatus(fill.Id, e.Id);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("pinned"));
            Assert.That(status.Maps.Single().Available, Is.SameAs(second)); Assert.That(status.Maps.Single().Pin, Is.EqualTo(first.Provenance.ConditionKey));
            Assert.That(fill.Mask.OutputHideAt(30, 30), Is.Zero, "the other bake is not read");
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(e.Settings.Generator.WithPin(MeshMapKind.Curvature, second.Provenance.ConditionKey)));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Active, Is.True);
            Assert.That(fill.Mask.OutputHideAt(30, 30), Is.EqualTo(255));
            d.Undo();
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Active, Is.False, "undoing the re-pin pins the first bake again");
            // ピンは型が読む種類だけ・鍵は 64 桁の 16 進
            Assert.That(() => pinned.WithPin(MeshMapKind.Thickness, first.Provenance.ConditionKey), Throws.ArgumentException);
            Assert.That(() => pinned.WithPin(MeshMapKind.Curvature, "ABC"), Throws.ArgumentException);
            Assert.That(pinned.WithPin(MeshMapKind.Curvature, null).Pins, Is.Empty);
        }

        // ───────────── 変更の伝播 ─────────────

        [Test] public void NewMapsRedrawOnlyTheGeneratorLayers()
        {
            var d = new PaintDocument(W, H, T);
            var plain = d.AddLayer("Plain"); plain.GetChannel(PaintChannel.Color).SetPixel(60, 40, new Rgba32(1, 2, 3, 255));
            var worn = d.AddLayer("Worn"); var s = worn.GetChannel(PaintChannel.Color);
            for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++) s.SetPixel(x, y, new Rgba32(200, 100, 50, 255));
            d.ClearHistory();
            var inputs = new TestGeneratorInputs().Put(Curvature((x, y) => .8)); d.GeneratorInputs = inputs;
            d.AddFilter(worn.Id, FilterTarget.Content, FilterSettings.FromGenerator(Plain(GeneratorType.EdgeWear).WithBlend(GeneratorBlend.Multiply)), new[] { PaintChannel.Color });
            var before = d.Composite(PaintChannel.Color); var stamp = worn.OutputStamp(PaintChannel.Color, 0, 0, 1, 1);
            long serial = d.ChangeSerial; var changed = new HashSet<TileCoord>();
            Assert.That(d.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True); Assert.That(changed, Is.Empty);
            inputs.Touch(); // 版だけ上がって中身は同じ
            Assert.That(d.RefreshGeneratorInputs(), Is.False);
            Assert.That(d.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True); Assert.That(changed, Is.Empty, "nothing the generators read changed");
            inputs.Put(Curvature((x, y) => .6)); // 焼き直した
            Assert.That(d.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True);
            Assert.That(changed, Is.EquivalentTo(new[] { new TileCoord(0, 0) }), "only the generator layer's tiles, not the plain layer's");
            Assert.That(worn.OutputStamp(PaintChannel.Color, 0, 0, 1, 1), Is.Not.EqualTo(stamp), "cached tiles of the old maps are not valid");
            var after = d.Composite(PaintChannel.Color);
            Assert.That(after, Is.Not.EqualTo(before));
            Assert.That(after, Is.EqualTo(Reference(d)), "the cached path equals a fresh evaluation");
            // マップを消すと入力のまま（変わったと答える）
            serial = d.ChangeSerial; changed.Clear();
            inputs.Refuse(MeshMapKind.Curvature, "Curvature has not been baked.");
            Assert.That(d.RefreshGeneratorInputs(), Is.True);
            Assert.That(d.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True); Assert.That(changed, Has.Member(new TileCoord(0, 0)));
            Assert.That(worn.GetOutputPixel(PaintChannel.Color, 3, 3), Is.EqualTo(new Rgba32(200, 100, 50, 255)));
            // 別の口（別のテクスチャセットから移した文書など）
            serial = d.ChangeSerial; changed.Clear();
            d.GeneratorInputs = new TestGeneratorInputs().Put(Curvature((x, y) => 1));
            Assert.That(d.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True); Assert.That(changed, Has.Member(new TileCoord(0, 0)));
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(Reference(d)));
        }
        /// <summary>キャッシュを通らない参照: 層ごとに一括評価した画素を同じ合成に通す代わりに、新しい文書へ読み直して合成する。</summary>
        static byte[] Reference(PaintDocument d)
        {
            var copy = DocumentBinary.Read(DocumentBinary.Write(d)); copy.GeneratorInputs = d.GeneratorInputs;
            return copy.Composite(PaintChannel.Color);
        }

        // ───────────── Undo・拒否・予算 ─────────────

        [Test] public void EditsAreUndoableAndSliderDragsCoalesce()
        {
            var (d, fill) = MaskedFill();
            d.GeneratorInputs = new TestGeneratorInputs().Put(Curvature((x, y) => x / (double)(W - 1)));
            var plain = d.Composite(PaintChannel.Color);
            var e = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.EdgeWear).WithNoise(0, .05, 0, GeneratorNoiseSpace.Model)));
            var added = d.Composite(PaintChannel.Color);
            Assert.That(added, Is.Not.EqualTo(plain));
            int steps = d.UndoCount;
            for (int i = 1; i <= 10; i++) d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(e.Settings.Generator.WithLevels(.02 * i, .5, .5)), coalesce: true);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "one drag, one step");
            var dragged = d.Composite(PaintChannel.Color);
            d.EndCoalescing();
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(e.Settings.Generator.WithInvert(true)), coalesce: true);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 2));
            d.Undo(); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(dragged));
            d.Undo(); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(added), "the drag undoes to before its first move");
            d.Undo(); Assert.That(fill.Mask.Filters, Is.Empty); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(plain));
            d.Redo(); Assert.That(fill.Mask.Filters.Single().Id, Is.EqualTo(e.Id)); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(added));
            // ストロークを取り消すと前の表示のまま
            var stroke = d.BeginMaskStroke(fill.Id, new BrushSettings { Radius = 5, Color = new Rgba32(0, 0, 0, 255) });
            stroke.Add(new BrushSample(30, 20)); stroke.Add(new BrushSample(40, 22, 1, 1)); stroke.Cancel();
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(added));
        }

        [Test] public void WrongTypesLayersAndBudgetsAreRefusedAndLeaveNothing()
        {
            var d = new PaintDocument(W, H, T); var l = d.AddLayer("L"); d.AddLayerMask(l.Id); d.ClearHistory();
            var g = FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.EdgeWear));
            Assert.That(d.FilterRefusal(l.Id, FilterTarget.Content, g, PaintChannel.Normal), Does.Contain("unit vectors"));
            Assert.That(() => d.AddFilter(l.Id, FilterTarget.Content, g, new[] { PaintChannel.Normal }), Throws.InvalidOperationException.With.Message.Contains("unit vectors"));
            Assert.That(d.FilterRefusal(l.Id, FilterTarget.Content, g, PaintChannel.Height), Is.Null);
            Assert.That(d.FilterRefusal(l.Id, FilterTarget.Mask, g), Is.Null);
            var added = d.AddFilter(l.Id, FilterTarget.Content, g);
            Assert.That(added.Channels, Is.EqualTo(new[] { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Metallic, PaintChannel.Height, PaintChannel.Emission }.OrderBy(c => c)), "every channel that accepts it");
            d.Undo();
            var adjust = d.AddAdjustmentLayer("A", AdjustmentSettings.Invert());
            Assert.That(() => d.AddFilter(adjust.Id, FilterTarget.Content, g), Throws.InvalidOperationException.With.Message.Contains("Adjustment"));
            var group = d.AddGroup("G");
            Assert.That(() => d.AddFilter(group.Id, FilterTarget.Content, g), Throws.InvalidOperationException);
            d.ClearHistory(); int steps = d.UndoCount;
            // 作業メモリの予算（ブロック 1 つの評価で約 64 × 48 × 4 バイト）
            var small = new PaintDocument(W, H, T); var sl = small.AddLayer("S"); small.ClearHistory();
            small.FilterWorkingBudgetBytes = 1000;
            Assert.That(() => small.AddFilter(sl.Id, FilterTarget.Content, g), Throws.InvalidOperationException.With.Message.Contains("working memory"));
            Assert.That(sl.Filters, Is.Empty); Assert.That(small.UndoCount, Is.Zero);
            // 1 つのスタックは 32 段まで
            for (int i = 0; i < PaintDocument.MaxFiltersPerStack; i++) d.AddFilter(l.Id, FilterTarget.Mask, g);
            Assert.That(() => d.AddFilter(l.Id, FilterTarget.Mask, g), Throws.InvalidOperationException.With.Message.Contains("at most"));
            Assert.That(l.Mask.Filters.Count, Is.EqualTo(PaintDocument.MaxFiltersPerStack));
            // 値の範囲
            var e = GeneratorSettings.Default(GeneratorType.EdgeWear);
            Assert.That(() => e.WithLevels(.5, .5, 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => e.WithLevels(-.1, .5, 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => e.WithNoise(.5, 0, 0, GeneratorNoiseSpace.Model), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => e.WithBalance(.2), Throws.ArgumentException, "balance belongs to dirt");
            Assert.That(() => e.WithAxis(0), Throws.ArgumentException);
            Assert.That(() => GeneratorSettings.Default(GeneratorType.Direction).WithDirection(0, 0, 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => e.WithBlend((GeneratorBlend)99), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => FilterSettings.FromValues(FilterType.Generator, 0, 0, 0, 0, false, 0, 1, 1, 0, 1), Throws.ArgumentException, "a generator stage needs its settings");
        }

        // ───────────── 焼き込み・保存・PSD ─────────────

        [Test] public void BakingWritesTheGeneratedMaskAndRefusesWithoutMaps()
        {
            var (d, fill) = MaskedFill();
            var e = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.EdgeWear)));
            int steps = d.UndoCount;
            Assert.That(() => d.BakeFilters(fill.Id), Throws.InvalidOperationException.With.Message.Contains("cannot be baked"));
            Assert.That(d.UndoCount, Is.EqualTo(steps)); Assert.That(fill.Mask.Filters.Single().Id, Is.EqualTo(e.Id)); Assert.That(fill.Mask.Surface.TileCount, Is.Zero);
            // 切ってあれば、焼くと消える（効きは無いので画素はそのまま）
            d.SetFilterEnabled(fill.Id, e.Id, false);
            Assert.That(d.BakeFilters(fill.Id), Is.True); Assert.That(fill.Mask.Surface.TileCount, Is.Zero);
            d.Undo(); d.Undo();
            d.GeneratorInputs = new TestGeneratorInputs().Put(Curvature((x, y) => x / (double)(W - 1)))
                .Put(TestMeshMaps.Make(MeshMapKind.Position, W, H, (x, y, c) => (x * 3 + y * 5 + c) % 17 / 16.0));
            var shown = d.Composite(PaintChannel.Color); var hide = fill.Mask.EvaluateOutputRegion(0, 0, W, H);
            steps = d.UndoCount;
            Assert.That(d.BakeFilters(fill.Id), Is.True);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1)); Assert.That(fill.Mask.Filters, Is.Empty);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) Assert.That(fill.Mask.Surface.GetPixel(x, y).A, Is.EqualTo(hide[y * W + x]));
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(shown), "baking keeps what is shown");
            d.Undo(); Assert.That(fill.Mask.Filters.Single().Id, Is.EqualTo(e.Id)); Assert.That(fill.Mask.Surface.TileCount, Is.Zero);
        }

        [Test] public void NativeVersion11RoundTripsGeneratorsAndRefusesUnknownValues()
        {
            var (d, fill) = MaskedFill();
            var curv = Curvature((x, y) => x / (double)(W - 1));
            var full = GeneratorSettings.FromValues(GeneratorType.Dirt, .1, .8, .3, true, .4, .07, -12345, GeneratorNoiseSpace.Uv, GeneratorBlend.Min, .25, 1, 0, 1, 0, false,
                new[] { new KeyValuePair<MeshMapKind, string>(MeshMapKind.Curvature, curv.Provenance.ConditionKey) });
            var dirt = d.AddFilter(fill.Id, FilterTarget.Content, FilterSettings.FromGenerator(full), new[] { PaintChannel.Roughness, PaintChannel.Color }, strength: .6);
            var dir = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Direction).WithDirection(.3, -2, 5).WithBentNormal(true)), enabled: false);
            d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(3));
            d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.PositionGradient).WithAxis(2)));
            d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Thickness).WithBlend(GeneratorBlend.Screen)));
            var bytes = DocumentBinary.Write(d);
            Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(DocumentBinary.CurrentVersion)); Assert.That(DocumentBinary.CurrentVersion, Is.GreaterThanOrEqualTo(11), "Generator stages came with version 11 (12 added layer locks, 13 the shape gradient)");
            var read = DocumentBinary.Read(bytes); var rl = read.GetLayer(fill.Id);
            Assert.That(rl.Filters.Select(f => (f.Id, f.Settings, f.Enabled, f.Strength, string.Join(",", f.Channels))), Is.EqualTo(fill.Filters.Select(f => (f.Id, f.Settings, f.Enabled, f.Strength, string.Join(",", f.Channels)))));
            Assert.That(rl.Mask.Filters.Select(f => (f.Id, f.Settings, f.Enabled)), Is.EqualTo(fill.Mask.Filters.Select(f => (f.Id, f.Settings, f.Enabled))));
            Assert.That(rl.Filters[0].Settings.Generator.Pins[MeshMapKind.Curvature], Is.EqualTo(curv.Provenance.ConditionKey));
            Assert.That(DocumentBinary.Write(read), Is.EqualTo(bytes), "write → read → write is byte-identical");
            Assert.That(read.CanUndo, Is.False);
            // 開いた文書はマップがつながるまで入力のまま、つなげば元と同じ
            Assert.That(read.GetGeneratorStatus(fill.Id, dirt.Id).Reason, Does.Contain("No mesh maps are connected"));
            var inputs = new TestGeneratorInputs().Put(curv).Put(TestMeshMaps.Make(MeshMapKind.AmbientOcclusion, W, H, (x, y, _) => y / (double)(H - 1)))
                .Put(TestMeshMaps.Make(MeshMapKind.Position, W, H, (x, y, c) => .5)).Put(TestMeshMaps.Make(MeshMapKind.Thickness, W, H, (x, y, _) => .3));
            d.GeneratorInputs = inputs; read.GeneratorInputs = inputs;
            foreach (PaintChannel c in new[] { PaintChannel.Color, PaintChannel.Roughness }) Assert.That(read.Composite(c), Is.EqualTo(d.Composite(c)), c.ToString());

            // 版 10 として読む: Generator の無い文書はそのまま、Generator（種類 6）は断る
            var noGen = new PaintDocument(W, H, T); var nl = noGen.AddLayer("P"); nl.GetChannel(PaintChannel.Color).SetPixel(1, 2, new Rgba32(3, 4, 5, 6));
            noGen.AddFilter(nl.Id, FilterTarget.Content, FilterSettings.Invert());
            var v10 = DocumentBinary.Write(noGen); BitConverter.GetBytes(10).CopyTo(v10, 8);
            Assert.That(DocumentBinary.Read(v10).Composite(PaintChannel.Color), Is.EqualTo(noGen.Composite(PaintChannel.Color)));
            var v10Gen = (byte[])bytes.Clone(); BitConverter.GetBytes(10).CopyTo(v10Gen, 8);
            Assert.That(() => DocumentBinary.Read(v10Gen), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("Unknown filter type 6"));
            Assert.That(DocumentBinary.Read(ArchiveTestUtil.AsVersion(DocumentBinary.Write(noGenPlain()), "P", 9)).Layers.Count, Is.EqualTo(1));

            // 知らない値は断る（落とさない）: マスクの Generator 1 つの文書で、段の頭から数えた位置を書き換える
            var one = new PaintDocument(W, H, T); var ol = one.AddLayer("O"); one.AddLayerMask(ol.Id);
            var og = one.AddFilter(ol.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.EdgeWear).WithPin(MeshMapKind.Curvature, curv.Provenance.ConditionKey)));
            var ob = DocumentBinary.Write(one);
            int at = IndexOf(ob, og.Id.ToByteArray()) + 16; // 種類
            int gen = at + 4 + 4 + 1 + 8 + 4 + 8 + 4 + 4 + 1 + 5 * 8; // Generator の段の頭（マスクの段にチャンネルは無い）
            Assert.That(BitConverter.ToInt32(ob, at), Is.EqualTo((int)FilterType.Generator)); Assert.That(BitConverter.ToInt32(ob, gen), Is.EqualTo((int)GeneratorType.EdgeWear));
            void Refused(int offset, int value, string message)
            {
                var bad = (byte[])ob.Clone(); BitConverter.GetBytes(value).CopyTo(bad, offset);
                Assert.That(() => DocumentBinary.Read(bad), Throws.InstanceOf<InvalidDataException>().With.Message.Contains(message), message);
            }
            Refused(gen, 99, "Unknown generator type 99");
            Refused(gen + 4, 2, "Generator algorithm version 2");
            int space = gen + 4 + 4 + 3 * 8 + 1 + 8 + 8 + 4;
            Refused(space, 7, "Unknown generator noise space 7");
            Refused(space + 4, 42, "Unknown generator blend 42");
            int pins = space + 4 + 4 + 8 + 4 + 3 * 8 + 1;
            Assert.That(BitConverter.ToInt32(ob, pins), Is.EqualTo(1));
            Refused(pins + 4, 77, "Unknown mesh map kind 77");
            Refused(pins + 4, (int)MeshMapKind.Thickness, "cannot be pinned");
            Refused(pins, 9, "Invalid generator pins count");
            Refused(gen + 12, unchecked((int)0x7ff80000), "Invalid generator parameters"); // low の上位 4 バイトで NaN に
            Assert.That(() => DocumentBinary.Read(ob.Take(ob.Length - 5).ToArray()), Throws.InstanceOf<InvalidDataException>());
            Assert.That(DocumentBinary.Read(ob).GetLayer(ol.Id).Mask.Filters.Single().Settings, Is.EqualTo(og.Settings));
        }
        static PaintDocument noGenPlain() { var d = new PaintDocument(W, H, T); d.AddLayer("P").GetChannel(PaintChannel.Color).SetPixel(1, 1, new Rgba32(9, 9, 9, 9)); return d; }
        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++) { int k = 0; while (k < needle.Length && haystack[i + k] == needle[k]) k++; if (k == needle.Length) return i; }
            throw new AssertionException("not found");
        }

        [Test] public void PsdExportRefusesGeneratorsUntilBakedAndResizingKeepsThem()
        {
            var (d, fill) = MaskedFill();
            d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Plain(GeneratorType.EdgeWear)));
            Assert.That(() => PsdBridge.Export(d, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("generators"));
            d.GeneratorInputs = new TestGeneratorInputs().Put(Curvature((x, y) => x / (double)(W - 1)));
            var shown = d.Composite(PaintChannel.Color);
            var resized = d.Resampled(W * 2, H * 2, CanvasResampling.Bilinear).Document;
            Assert.That(resized.GetLayer(fill.Id).Mask.Filters.Single().Settings, Is.EqualTo(fill.Mask.Filters.Single().Settings), "nothing in pixels to scale");
            resized.GeneratorInputs = d.GeneratorInputs;
            Assert.That(resized.InactiveGenerators().Single(), Does.Contain("baked at 64×48"), "maps of the old size are not stretched");
            d.BakeFilters(fill.Id);
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(shown));
            Assert.That(PsdBridge.Export(d, PaintChannel.Color).Layers.Count, Is.EqualTo(1));
        }
    }

    /// <summary>Core の MathUtil.ByteUnit と同じ値（b / 255.0）。</summary>
    internal static class MathRef { public static double Unit(int b) => b / 255.0; }
}
