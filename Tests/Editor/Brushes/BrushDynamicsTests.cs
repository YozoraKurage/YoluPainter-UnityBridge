using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Brushes;
using Yozolab.YoluPainter.Editor;
using W = Yozolab.YoluPainter.Tests.PhotoshopBrushTests.W;
using P = Yozolab.YoluPainter.Tests.PhotoshopBrushTests;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>カラーダイナミクス・デュアルブラシ・フェード・傾き と、.abr / .pat の模様・設定の読み込み、置き場とブラシ設定の保存。
    /// 既定値では以前と 1 バイトも変わらない（BrushGolden の値は導入前の Core と照合済み）。</summary>
    public sealed class BrushDynamicsTests
    {
        static readonly Rgba32 Ink = new Rgba32(200, 60, 30, 255);
        static (PaintDocument d, PaintLayer l) Doc(int w = 96, int h = 64) { var d = new PaintDocument(w, h, 32); var l = d.AddLayer("L"); d.ClearHistory(); return (d, l); }
        static BrushSettings Hard(double radius = 6) => new BrushSettings { Radius = radius, Hardness = 1, Spacing = .25, Color = Ink, PressureSize = false, PressureOpacity = false };
        static byte[] Draw(BrushSettings s, PaintChannel channel, params BrushSample[] samples)
        {
            var (d, l) = Doc();
            if (channel != PaintChannel.Color) d.SetChannelEnabled(l.Id, channel, true);
            using (var stroke = d.BeginStroke(l.Id, channel, s)) { foreach (var p in samples) stroke.Add(p); stroke.Commit(); }
            return d.Composite(channel);
        }
        static byte[] Draw(BrushSettings s, params BrushSample[] samples) => Draw(s, PaintChannel.Color, samples);
        static BrushSample[] Line(double x0, double y0, double x1, double y1, int segments)
            => Enumerable.Range(0, segments + 1).Select(i => new BrushSample(x0 + (x1 - x0) * i / segments, y0 + (y1 - y0) * i / segments)).ToArray();
        static int A(byte[] c, int x, int y, int w = 96) => c[(y * w + x) * 4 + 3];
        static Rgba32 Px(byte[] c, int x, int y, int w = 96) { int i = (y * w + x) * 4; return new Rgba32(c[i], c[i + 1], c[i + 2], c[i + 3]); }
        static byte[] Alphas(byte[] c) => Enumerable.Range(0, c.Length / 4).Select(i => c[i * 4 + 3]).ToArray();
        static HashSet<(byte, byte, byte)> Colours(byte[] c, int minAlpha = 1)
        { var set = new HashSet<(byte, byte, byte)>(); for (int i = 0; i < c.Length; i += 4) if (c[i + 3] >= minAlpha) set.Add((c[i], c[i + 1], c[i + 2])); return set; }

        // ---------------- 既定値は以前のまま ----------------

        [Test] public void DefaultSettingsKeepEveryStrokeByteIdentical()
        {
            // 値はダイナミクス導入前の Core（HEAD 10aa5ab）と導入後の Core を mono で同じ BrushGolden に通して一致を確かめたもの。
            var expected = new Dictionary<string, string>
            {
                { "round", "ac5e5edac86c317850a9bcfba4e9aa55a24db80fdd45bf1fc74016768860b587" },
                { "soft-angled", "78ecb33e38c6b570af3146d2fd159b8048de171643c6aec5b88cd31b7f0a7c60" },
                { "tip-jitter", "96a1e34766be92eeb43dc678cdbfc87be976c85bca3f0bee7c8df6ccbc957acc" },
                { "tips-sequential", "9bc1260f70d65e6694e7795e44439fe3e0c854b50c0a7fc17cbbcabed60a1ede" },
                { "texture", "6c47590d09362aae2cf42ab5a49f3a8e23ec092939ad657fedf01a9b571dc5ab" },
                { "assist", "ad7b58803b2d39d135e6b5eb0e453fc1880202c61b5d460d5e73fb791786a48e" },
                { "erase-mask-emission", "5736ea5517d915f2d146b11ac60406f148e48ca30f1c3380ff1a8bd583b19205bfbb0ddb498333d9" },
            };
            var actual = BrushGolden.Run().ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.That(actual, Is.EquivalentTo(expected));
            var s = new BrushSettings();
            Assert.That(s.HasColorDynamics, Is.False); Assert.That(s.Dual, Is.Null); Assert.That(s.ColorPerTip, Is.True);
            Assert.That(new[] { s.FadeSize, s.FadeOpacity, s.FadeFlow }, Is.All.Zero);
            Assert.That(s.TiltSize || s.TiltOpacity || s.TiltFlow || s.TiltAngle, Is.False);
        }

        // ---------------- カラーダイナミクス ----------------

        [Test] public void HsvConversionIsTheTextbookOne()
        {
            ColorDynamics.RgbToHsv(1, .5, 0, out double h, out double s, out double v);
            Assert.That(h, Is.EqualTo(1 / 12.0).Within(1e-12)); Assert.That(s, Is.EqualTo(1)); Assert.That(v, Is.EqualTo(1));
            ColorDynamics.HsvToRgb(2 / 3.0, .5, .8, out double r, out double g, out double b);
            Assert.That(new[] { r, g, b }, Is.EqualTo(new[] { .4, .4, .8 }).Within(1e-12));
            ColorDynamics.RgbToHsv(.3, .3, .3, out h, out s, out v);
            Assert.That((h, s), Is.EqualTo((0.0, 0.0)), "grey has no hue");
        }

        [Test] public void ColourJittersStayInTheirRanges()
        {
            var random = new System.Random(1);
            // 色相 ±0.1 × 180° = ±18°。純赤からなら R = 255、片方は 0、もう片方は 255 × 6 × 0.05 = 76.5 → 77 まで
            var hue = new BrushSettings { Color = new Rgba32(255, 0, 0), HueJitter = .1 };
            var reds = Enumerable.Range(0, 500).Select(_ => ColorDynamics.Next(hue, random)).ToList();
            Assert.That(reds.All(c => c.R == 255 && Math.Min(c.G, c.B) == 0 && Math.Max(c.G, c.B) <= 77 && c.A == 255));
            Assert.That(reds.Any(c => c.G > 40) && reds.Any(c => c.B > 40), "both directions around the circle");
            // 明るさ ±0.2: 灰 128（v = 0.502）は 0.302〜0.702 → 77〜179、灰のまま
            var bright = new BrushSettings { Color = new Rgba32(128, 128, 128), BrightnessJitter = .2 };
            var greys = Enumerable.Range(0, 500).Select(_ => ColorDynamics.Next(bright, random)).ToList();
            Assert.That(greys.All(c => c.R == c.G && c.G == c.B && c.R >= 77 && c.R <= 179));
            Assert.That(greys.Min(c => c.R), Is.LessThan(90)); Assert.That(greys.Max(c => c.R), Is.GreaterThan(165));
            // 描画色/背景色 0.5: 黒から白へ半分まで
            var mix = new BrushSettings { Color = new Rgba32(0, 0, 0), SecondaryColor = new Rgba32(255, 255, 255, 255), ForegroundBackgroundJitter = .5 };
            var mixed = Enumerable.Range(0, 500).Select(_ => ColorDynamics.Next(mix, random)).ToList();
            Assert.That(mixed.All(c => c.R == c.G && c.G == c.B && c.R <= 128)); Assert.That(mixed.Max(c => c.R), Is.GreaterThan(110));
            // 純度: (255,128,128) の彩度 0.498 → +0.5 で 0.749 → (255,64,64)。-0.5 で純赤は (255,128,128)
            Assert.That(ColorDynamics.Next(new BrushSettings { Color = new Rgba32(255, 128, 128), Purity = .5 }, random), Is.EqualTo(new Rgba32(255, 64, 64)));
            Assert.That(ColorDynamics.Next(new BrushSettings { Color = new Rgba32(255, 0, 0, 90), Purity = -.5 }, random), Is.EqualTo(new Rgba32(255, 128, 128, 90)), "alpha is kept");
            // 0 の項目は乱数を引かない
            var a = new System.Random(5); var b = new System.Random(5);
            ColorDynamics.Next(new BrushSettings { Purity = .3 }, a);
            Assert.That(a.Next(), Is.EqualTo(b.Next()));
        }

        [Test] public void PerTipColoursAreDeterministicPerSeedAndNeverMoveTheDabs()
        {
            var path = Line(8, 30, 88, 34, 8);
            BrushSettings Jittery(int seed) { var s = Hard(5); s.Spacing = .3; s.Scatter = .5; s.Count = 2; s.SizeJitter = .4; s.Seed = seed; s.HueJitter = 1; s.BrightnessJitter = .3; return s; }
            var first = Draw(Jittery(3), path);
            Assert.That(Draw(Jittery(3), path), Is.EqualTo(first), "the same seed paints the same pixels");
            Assert.That(Draw(Jittery(4), path), Is.Not.EqualTo(first));
            Assert.That(Colours(first, 255).Count, Is.GreaterThan(5), "a new colour for each dab");
            var plain = Jittery(3); plain.HueJitter = 0; plain.BrightnessJitter = 0;
            Assert.That(Alphas(first), Is.EqualTo(Alphas(Draw(plain, path))), "colour jitter has its own random stream: the dabs land where they did");
        }

        [Test] public void PerStrokeColourIsOneColourAndPerTipColoursRespectTheOpacityCeiling()
        {
            var path = Line(8, 30, 88, 34, 8);
            var once = Hard(5); once.Hardness = .4; once.HueJitter = .8; once.ColorPerTip = false; once.Seed = 9;
            var c = Draw(once, path);
            Assert.That(Colours(c).Count, Is.EqualTo(1), "every painted pixel (soft edges too) has the stroke's one colour");
            Assert.That(Colours(c).Single(), Is.Not.EqualTo(((byte)200, (byte)60, (byte)30)));
            var half = Hard(5); half.Opacity = .5; half.Spacing = .1; half.HueJitter = 1;
            var h = Draw(half, path);
            Assert.That(Enumerable.Range(0, h.Length / 4).Max(i => h[i * 4 + 3]), Is.EqualTo(128), "overlapping dabs change the colour but never pass the ceiling");
            Assert.That(Colours(h, 128).Count, Is.GreaterThan(5));
        }

        [Test] public void ColourDynamicsAreIgnoredOnDataChannelsMasksAndErasing()
        {
            var path = Line(8, 30, 88, 34, 6);
            var dyn = Hard(5); dyn.Color = new Rgba32(180, 180, 180); dyn.HueJitter = 1; dyn.ForegroundBackgroundJitter = 1; dyn.SecondaryColor = new Rgba32(10, 250, 10); dyn.Purity = .5;
            var plain = Hard(5); plain.Color = dyn.Color;
            foreach (var channel in new[] { PaintChannel.Roughness, PaintChannel.Metallic, PaintChannel.Height, PaintChannel.Normal })
                Assert.That(Draw(dyn, channel, path), Is.EqualTo(Draw(plain, channel, path)), channel + " is data: the exact value is painted");
            Assert.That(Draw(dyn, PaintChannel.Emission, path), Is.Not.EqualTo(Draw(plain, PaintChannel.Emission, path)), "Emission carries colour");
            Assert.That(dyn.ForChannel(PaintChannel.Roughness).HasColorDynamics, Is.False); Assert.That(dyn.HasColorDynamics, Is.True, "the brush itself keeps them");
            byte[] Masked(BrushSettings s)
            {
                var (d, l) = Doc(); using (var st = d.BeginStroke(l.Id, PaintChannel.Color, Hard(12))) { foreach (var p in path) st.Add(p); st.Commit(); }
                d.AddLayerMask(l.Id); using (var st = d.BeginMaskStroke(l.Id, s)) { foreach (var p in path) st.Add(p); st.Commit(); }
                return d.Composite(PaintChannel.Color);
            }
            Assert.That(Masked(dyn), Is.EqualTo(Masked(plain)));
            var erase = Hard(5); erase.Erase = true; var eraseDyn = erase.Clone(); eraseDyn.HueJitter = 1; eraseDyn.ForegroundBackgroundJitter = 1;
            byte[] Erased(BrushSettings s)
            {
                var (d, l) = Doc(); using (var st = d.BeginStroke(l.Id, PaintChannel.Color, Hard(12))) { foreach (var p in path) st.Add(p); st.Commit(); }
                using (var st = d.BeginStroke(l.Id, PaintChannel.Color, s)) { foreach (var p in path) st.Add(p); st.Commit(); }
                return d.Composite(PaintChannel.Color);
            }
            Assert.That(Erased(eraseDyn), Is.EqualTo(Erased(erase)));
        }

        [Test] public void TheSurfaceBrushPaintsTheStrokesOneColour()
        {
            var (d, l) = Doc(); var s = Hard(); s.HueJitter = 1; s.Seed = 2;
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s)) { for (int x = 10; x < 40; x++) stroke.ApplyPixel(x, 20, x % 2 == 0 ? 1 : .5); stroke.Commit(); }
            var c = d.Composite(PaintChannel.Color);
            Assert.That(Colours(c).Count, Is.EqualTo(1), "mesh dabs have no tip boundary, so the colour is chosen once per stroke");
        }

        // ---------------- デュアルブラシ ----------------

        [Test] public void DualModesCombineCoverage()
        {
            Assert.That(DualBrush.Combine(DualBrushMode.Multiply, .5, .5), Is.EqualTo(.25));
            Assert.That(DualBrush.Combine(DualBrushMode.Darken, .3, .7), Is.EqualTo(.3));
            Assert.That(DualBrush.Combine(DualBrushMode.Overlay, .25, .5), Is.EqualTo(.25));
            Assert.That(DualBrush.Combine(DualBrushMode.Overlay, .75, .5), Is.EqualTo(.75));
            Assert.That(DualBrush.Combine(DualBrushMode.ColorDodge, .5, .5), Is.EqualTo(1));
            Assert.That(DualBrush.Combine(DualBrushMode.ColorBurn, .5, .5), Is.EqualTo(0));
            Assert.That(DualBrush.Combine(DualBrushMode.ColorBurn, .75, .5), Is.EqualTo(.5));
            Assert.That(DualBrush.Combine(DualBrushMode.LinearBurn, .8, .5), Is.EqualTo(.3).Within(1e-12));
            Assert.That(DualBrush.Combine(DualBrushMode.HardMix, .6, .5), Is.EqualTo(1)); Assert.That(DualBrush.Combine(DualBrushMode.HardMix, .4, .5), Is.EqualTo(0));
            Assert.That(DualBrush.Combine(DualBrushMode.Subtract, .8, .3), Is.EqualTo(.5).Within(1e-12));
            foreach (DualBrushMode mode in Enum.GetValues(typeof(DualBrushMode)))
                Assert.That(DualBrush.Combine(mode, 0, 1), Is.EqualTo(0), mode + ": nothing outside the main tip");
        }

        [Test] public void AHardDualTipMultipliesTheMainTip()
        {
            var main = Hard(10); main.Dual = new DualBrush { Radius = 4, Hardness = 1 };
            var dot = new BrushSample(20, 20);
            Assert.That(Draw(main, dot), Is.EqualTo(Draw(Hard(4), dot)), "hard 10 px × hard 4 px at the same place = the 4 px dot");
            // 柔らかい 2 つ目（硬さ 0、半径 4）: 中心から 2 px で smoothstep(0.5) = 0.5 → アルファ 128
            var soft = Hard(10); soft.Dual = new DualBrush { Radius = 4, Hardness = 0 };
            var c = Draw(soft, new BrushSample(20.5, 20.5));
            Assert.That(A(c, 20, 20), Is.EqualTo(255)); Assert.That(A(c, 22, 20), Is.EqualTo(128)); Assert.That(A(c, 25, 20), Is.EqualTo(0));
            Assert.That(Px(c, 22, 20).R, Is.EqualTo(200), "the colour is unchanged");
        }

        [Test] public void DualBrushIsDeterministicAndIndependentOfTheInputRate()
        {
            BrushSettings S(int seed) { var s = Hard(6); s.Seed = seed; s.Dual = new DualBrush { Radius = 5, Hardness = .5, Spacing = .37, Scatter = .8, Count = 2, Mode = DualBrushMode.Multiply }; return s; }
            var sparse = Draw(S(1), new BrushSample(8, 32), new BrushSample(88, 32));
            var dense = Draw(S(1), Line(8, 32, 88, 32, 50));
            Assert.That(dense, Is.EqualTo(sparse), "the dual dabs a main dab sees are fixed by arc length, not by input packets");
            Assert.That(Draw(S(2), new BrushSample(8, 32), new BrushSample(88, 32)), Is.Not.EqualTo(sparse));
            var plain = Hard(6); plain.Seed = 1;
            Assert.That(Alphas(sparse).Count(a => a > 0), Is.LessThan(Alphas(Draw(plain, new BrushSample(8, 32), new BrushSample(88, 32))).Count(a => a > 0)));
        }

        // ---------------- フェードと傾き ----------------

        [Test] public void FadeShrinksTheSizeOverTheGivenStamps()
        {
            // 半径 8・間隔 4 px: 0 番目 x=10 r=8、1 番目 14 r=6、2 番目 18 r=4、3 番目 22 r=2、4 番目から 0 → 行 32 の右端は 23
            var s = Hard(8); s.FadeSize = 4;
            var (d, l) = Doc(); long stamps;
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s)) { stroke.Add(new BrushSample(10, 32)); stroke.Add(new BrushSample(60, 32)); stroke.Commit(); stamps = stroke.StampCount; }
            var c = d.Composite(PaintChannel.Color);
            Assert.That(Enumerable.Range(0, 96).Where(x => A(c, x, 32) > 0).Max(), Is.EqualTo(23));
            Assert.That(Enumerable.Range(0, 64).Count(y => A(c, 22, y) > 0), Is.EqualTo(4), "only the 2 px dab reaches x = 22");
            Assert.That(stamps, Is.EqualTo(13), "faded stamps still count (0..48 every 4 px)");
        }

        [Test] public void FadeOpacityAndFlowHalveTheSecondStamp()
        {
            // 半径 4・間隔 8 px: x=10, 18, 26。2 段のフェードで 1 番目は半分（128）、2 番目は 0
            foreach (var flow in new[] { false, true })
            {
                var s = Hard(4); s.Spacing = 1; if (flow) s.FadeFlow = 2; else s.FadeOpacity = 2;
                var c = Draw(s, new BrushSample(10, 32), new BrushSample(40, 32));
                Assert.That((A(c, 10, 32), A(c, 18, 32), A(c, 14, 32), A(c, 26, 32)), Is.EqualTo((255, 128, 128, 0)), flow ? "flow" : "opacity");
            }
        }

        [Test] public void PenTiltIsMeasuredFromUpright()
        {
            Assert.That(PenTilt.Amount(0, 0), Is.EqualTo(0));
            Assert.That(PenTilt.Amount(Math.PI / 4, 0), Is.EqualTo(.5).Within(1e-12));
            Assert.That(PenTilt.Amount(Math.PI / 4, -Math.PI / 4), Is.EqualTo(Math.Atan(Math.Sqrt(2)) / (Math.PI / 2)).Within(1e-12), "tan²θ = tan²θx + tan²θy");
            Assert.That(PenTilt.Amount(Math.PI / 2, 0), Is.EqualTo(1));
            Assert.That(PenTilt.Azimuth(Math.PI / 4, 0), Is.EqualTo(0)); Assert.That(PenTilt.Azimuth(0, Math.PI / 4), Is.EqualTo(Math.PI / 2).Within(1e-12));
            Assert.That(PenTilt.Azimuth(-Math.PI / 4, 0), Is.EqualTo(Math.PI).Within(1e-12));
            var sample = new BrushSample(1, 2, 1, 0, 5, -5);
            Assert.That((sample.TiltX, sample.TiltY), Is.EqualTo((Math.PI / 2, -Math.PI / 2)), "clamped to ±90°");
            Assert.That(() => new BrushSample(1, 2, 1, 0, double.NaN, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test] public void TiltScalesSizeAndOpacityAndTurnsTheTip()
        {
            var tilted = Hard(8); tilted.TiltSize = true;
            Assert.That(Draw(tilted, new BrushSample(20, 20, 1, 0, Math.PI / 4, 0)), Is.EqualTo(Draw(Hard(4), new BrushSample(20, 20))), "45° from upright halves the size");
            Assert.That(Draw(tilted, new BrushSample(20, 20)), Is.EqualTo(Draw(Hard(8), new BrushSample(20, 20))), "no tilt data (a mouse) changes nothing");
            var flat = Hard(8); flat.TiltOpacity = true;
            Assert.That(Alphas(Draw(flat, new BrushSample(20, 20, 1, 0, Math.PI / 2, 0))), Is.All.Zero, "a pen lying flat paints nothing");
            var turn = Hard(8); turn.Roundness = .25; turn.TiltAngle = true;
            var vertical = Hard(8); vertical.Roundness = .25; vertical.Angle = 90;
            Assert.That(Draw(turn, new BrushSample(30, 30, 1, 0, 0, Math.PI / 4)), Is.EqualTo(Draw(vertical, new BrushSample(30, 30))), "leaning toward +Y turns the tip 90°");
        }

        // ---------------- 取消・Undo・予算・検証 ----------------

        [Test] public void UndoAndCancelRestoreExactPixelsWithEveryDynamic()
        {
            var s = Hard(6); s.HueJitter = 1; s.ForegroundBackgroundJitter = .5; s.FadeOpacity = 30; s.TiltSize = true;
            s.Dual = new DualBrush { Radius = 3, Hardness = .5, Scatter = 1, Count = 2, Mode = DualBrushMode.ColorBurn };
            var (d, l) = Doc();
            using (var st = d.BeginStroke(l.Id, PaintChannel.Color, Hard(10))) { foreach (var p in Line(5, 10, 90, 50, 4)) st.Add(p); st.Commit(); }
            var before = d.Composite(PaintChannel.Color);
            using (var st = d.BeginStroke(l.Id, PaintChannel.Color, s)) { foreach (var p in Line(8, 50, 88, 12, 9)) st.Add(new BrushSample(p.X, p.Y, 1, 0, .3, .2)); st.Cancel(); }
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before), "cancel");
            using (var st = d.BeginStroke(l.Id, PaintChannel.Color, s)) { foreach (var p in Line(8, 50, 88, 12, 9)) st.Add(new BrushSample(p.X, p.Y, 1, 0, .3, .2)); Assert.That(st.Commit(), Is.True); }
            var after = d.Composite(PaintChannel.Color);
            Assert.That(after, Is.Not.EqualTo(before));
            Assert.That(d.Undo(), Is.True); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before));
            Assert.That(d.Redo(), Is.True); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(after));
        }

        [Test] public void PerTipColoursAndDualCoverageCountTowardTheStrokeBudget()
        {
            // 1 タイル（32²）の変更前は空で 0 バイト。濃さの溜まり 4 B/画素 → 64 + 4096。ダブごとの色は 20 B/画素、デュアルは別に 4 B/画素
            var dot = new BrushSample(10, 10);
            var (d, l) = Doc(); d.ActiveStrokeBudgetBytes = 64 + 32 * 32 * 4 + 100;
            using (var st = d.BeginStroke(l.Id, PaintChannel.Color, Hard(4))) { st.Add(dot); st.Commit(); }
            Assert.That(d.CanUndo, Is.True, "the plain dot fits");
            var painted = d.Composite(PaintChannel.Color);
            foreach (var s in new[] { new BrushSettings { Radius = 4, HueJitter = 1 }, new BrushSettings { Radius = 4, Dual = new DualBrush { Radius = 3 } } })
            {
                var stroke = d.BeginStroke(l.Id, PaintChannel.Color, s);
                Assert.That(() => stroke.Add(dot), Throws.InvalidOperationException.With.Message.Contains("budget"));
                Assert.That(stroke.IsFinished, Is.True, "the stroke cancelled itself");
                Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(painted));
            }
            Assert.That(d.Undo(), Is.True); Assert.That(d.CanUndo, Is.False, "only the plain dot is in the history");
        }

        [Test] public void InvalidDynamicsAreRefusedBeforeAnyPixelChanges()
        {
            var bad = new List<Action<BrushSettings>>
            {
                s => s.HueJitter = 1.5, s => s.SaturationJitter = -.1, s => s.BrightnessJitter = double.NaN, s => s.ForegroundBackgroundJitter = 2,
                s => s.Purity = -1.01, s => s.FadeSize = -1, s => s.FadeFlow = BrushSettings.MaxFade + 1,
                s => s.Dual = new DualBrush { Radius = 0 }, s => s.Dual = new DualBrush { Count = 17 }, s => s.Dual = new DualBrush { Spacing = 0 },
                s => s.Dual = new DualBrush { Mode = (DualBrushMode)99 }, s => s.Dual = new DualBrush { Roundness = 0 }, s => s.Dual = new DualBrush { Hardness = double.PositiveInfinity },
            };
            var (d, l) = Doc(); var before = d.Composite(PaintChannel.Color);
            foreach (var change in bad)
            {
                var s = Hard(); change(s);
                Assert.That(() => d.BeginStroke(l.Id, PaintChannel.Color, s), Throws.InstanceOf<ArgumentException>());
            }
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before));
            using (var st = d.BeginStroke(l.Id, PaintChannel.Color, Hard())) st.Cancel(); // 断ったあとも新しいストロークを始められる
        }

        [Test] public void CloneCopiesTheDualBrushAndTheStrokeFreezesSettings()
        {
            var s = Hard(); s.Dual = new DualBrush { Radius = 3 }; s.HueJitter = .2; s.TiltFlow = true; s.FadeSize = 7; s.SecondaryColor = new Rgba32(1, 2, 3, 4);
            var c = s.Clone();
            Assert.That(c.Dual, Is.Not.SameAs(s.Dual)); Assert.That(c.Dual.Radius, Is.EqualTo(3));
            Assert.That((c.HueJitter, c.TiltFlow, c.FadeSize, c.SecondaryColor), Is.EqualTo((.2, true, 7, new Rgba32(1, 2, 3, 4))));
            c.Dual.Radius = 9; Assert.That(s.Dual.Radius, Is.EqualTo(3));
            var path = Line(10, 30, 80, 30, 5);
            var expected = Draw(s, path);
            var (d, l) = Doc();
            using (var st = d.BeginStroke(l.Id, PaintChannel.Color, s)) { s.Dual.Radius = 20; s.HueJitter = 1; foreach (var p in path) st.Add(p); st.Commit(); }
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(expected), "changing the brush during a stroke does not affect it");
        }

        // ---------------- Photoshop の .abr / .pat ----------------

        /// <summary>模様 1 つ（Photoshop の Pattern 構造）。rows はチャンネルごとに上の行から。</summary>
        internal static byte[] Pattern(int mode, string name, string id, int w, int h, byte[][] channels, bool rle = false, int depth = 8, byte[] palette = null)
        {
            var body = new W().I32(1).I32(mode).I16(h).I16(w).Unicode(name).U8(id.Length).Ascii(id);
            if (mode == 2) body.Bytes(palette).I32(0);
            var list = new W().I32(0).I32(0).I32(h).I32(w).I32(channels.Length);
            foreach (var plane in channels)
            {
                var data = new W();
                int rowBytes = w * depth / 8;
                if (rle)
                {
                    var rows = Enumerable.Range(0, h).Select(y => new byte[] { (byte)(rowBytes - 1) }.Concat(plane.Skip(y * rowBytes).Take(rowBytes)).ToArray()).ToArray();
                    foreach (var row in rows) data.I16(row.Length); foreach (var row in rows) data.Bytes(row);
                }
                else data.Bytes(plane);
                var array = new W().I32(depth).I32(0).I32(0).I32(h).I32(w).I16(depth).U8(rle ? 1 : 0).Bytes(data.ToArray()).ToArray();
                list.I32(1).I32(array.Length).Bytes(array);
            }
            list.I32(0).I32(0); // 利用者のマスク・シートのマスク（書かれていない）
            var l = list.ToArray();
            return body.I32(3).I32(l.Length).Bytes(l).ToArray();
        }
        static byte[] Framed(params byte[][] patterns)
        {
            var w = new W();
            foreach (var p in patterns) { w.I32(p.Length).Bytes(p); while (w.S.Length % 4 != 0) w.U8(0); }
            return w.ToArray();
        }
        static Action<W> EnumValue(string type, string value) => w => w.Ascii("enum").Key(type).Key(value);
        static Action<W> Doub(double v) => w => w.Ascii("doub").Double(v);

        [Test] public void AbrColourDualTextureAndControlsAreMapped()
        {
            var grey = Pattern(1, "Paper", "pat-1", 2, 2, new[] { new byte[] { 0, 255, 100, 200 } });
            var desc = new W().I32(16);
            P.Descriptor(desc, "null", ("Brsh", w =>
            {
                w.Ascii("VlLs").I32(2).Ascii("Objc");
                P.Descriptor(w, "brushPreset",
                    ("Nm  ", P.Text("Everything")),
                    ("Brsh", P.Obj("computedBrush", ("Dmtr", P.Unit("#Pxl", 40)), ("Hrdn", P.Unit("#Prc", 50)), ("Spcn", P.Unit("#Prc", 20)))),
                    ("useTipDynamics", P.Bool(true)), ("szVr", P.Dynamics(1, 0)), ("angleDynamics", P.Dynamics(7, 0)), ("roundnessDynamics", P.Dynamics(3, 0)),
                    ("usePaintDynamics", P.Bool(true)), ("opVr", P.Dynamics(3, 0)), ("prVr", P.Dynamics(4, 0)),
                    ("useColorDynamics", P.Bool(true)), ("clVr", P.Dynamics(0, 40)), ("H   ", P.Unit("#Prc", 20)), ("Strt", P.Unit("#Prc", 30)), ("Brgh", P.Unit("#Prc", 10)), ("purity", P.Unit("#Prc", -50)),
                    ("useDualBrush", P.Bool(true)),
                    ("dualBrush", P.Obj("dualBrush", ("useDualBrush", P.Bool(true)), ("Flip", P.Bool(false)),
                        ("Brsh", P.Obj("sampledBrush", ("Dmtr", P.Unit("#Pxl", 24)), ("Angl", P.Unit("#Ang", 30)), ("Spcn", P.Unit("#Prc", 40)), ("sampledData", P.Text("dual-tip")))),
                        ("BlnM", EnumValue("BlnM", "CBrn")), ("useScatter", P.Bool(true)), ("Cnt ", Doub(3)), ("bothAxes", P.Bool(true)), ("scatterDynamics", P.Dynamics(0, 120)))),
                    ("useTexture", P.Bool(true)), ("Txtr", P.Obj("Ptrn", ("Nm  ", P.Text("Paper")), ("Idnt", P.Text("pat-1")))),
                    ("textureDepth", P.Unit("#Prc", 60)), ("textureScale", P.Unit("#Prc", 200)), ("InvT", P.Bool(true)), ("textureBlendMode", EnumValue("BlnM", "Mltp")));
                w.Ascii("Objc");
                P.Descriptor(w, "brushPreset",
                    ("Nm  ", P.Text("Missing pattern")), ("Brsh", P.Obj("computedBrush", ("Dmtr", P.Unit("#Pxl", 10)))),
                    ("useTexture", P.Bool(true)), ("Txtr", P.Obj("Ptrn", ("Nm  ", P.Text("Paper")), ("Idnt", P.Text("other-id")))), ("textureBlendMode", EnumValue("BlnM", "Sbtr")), ("TxtC", P.Bool(true)));
            }));
            var file = new W().I16(6).I16(1).Bytes(P.Section("samp", P.Samp(1, "dual-tip", false))).Bytes(P.Section("desc", desc.ToArray())).Bytes(P.Section("patt", Framed(grey))).ToArray();
            var brushes = PhotoshopBrushReader.Read(file);
            Assert.That(brushes.Count, Is.EqualTo(2), "the dual tip is used by a preset, so it is not a brush of its own");
            var b = brushes[0]; var s = b.Settings;
            Assert.That(s.FadeSize, Is.EqualTo(25)); Assert.That(s.FollowDirection, Is.True, "angle control 7 = direction"); Assert.That(s.TiltOpacity, Is.True);
            Assert.That(b.Warnings, Has.Some.Contains("Roundness pen tilt control")); Assert.That(b.Warnings, Has.Some.Contains("Flow stylus wheel control"));
            Assert.That((s.ForegroundBackgroundJitter, s.HueJitter, s.SaturationJitter, s.BrightnessJitter, s.Purity), Is.EqualTo((.4, .2, .3, .1, -.5)));
            var dual = s.Dual;
            Assert.That(dual, Is.Not.Null); Assert.That(dual.Tip.Width, Is.EqualTo(3)); Assert.That(dual.Tip[0, 1], Is.EqualTo(255));
            Assert.That((dual.Radius, dual.Angle, dual.Spacing, dual.Mode, dual.Count, dual.Scatter), Is.EqualTo((12.0, 30.0, .4, DualBrushMode.ColorBurn, 3, 1.2)));
            // 模様: 上の行 (0, 255)、下の行 (100, 200) → 左下原点で [0,0]=100 [1,0]=200 [0,1]=0 [1,1]=255。反転して 155, 55, 255, 0
            Assert.That(s.Texture, Is.Not.Null);
            Assert.That(new[] { s.Texture[0, 0], s.Texture[1, 0], s.Texture[0, 1], s.Texture[1, 1] }, Is.EqualTo(new byte[] { 155, 55, 255, 0 }));
            Assert.That((s.TextureDepth, s.TextureScale), Is.EqualTo((.6, 2.0)));
            Assert.That(b.Warnings, Has.None.Contains("Texture"), "a grey 8-bit Multiply pattern maps exactly");
            Assert.That(b.Warnings, Has.None.Contains("Dual brush"));
            var missing = brushes[1];
            Assert.That(missing.Settings.Texture, Is.Null);
            Assert.That(missing.Warnings, Has.Some.Contains("'Paper' is not in the file"), "matched by id only, never by the name");
        }

        [Test] public void AbrPatternsThatCannotBeUsedAreReportedPerBrush()
        {
            var lab = Pattern(9, "Lab", "lab-1", 1, 1, new[] { new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 } });
            var rgb = Pattern(3, "Colour", "rgb-1", 2, 1, new[] { new byte[] { 255, 0 }, new byte[] { 0, 0 }, new byte[] { 0, 255 } }, rle: true);
            var desc = new W().I32(16);
            P.Descriptor(desc, "null", ("Brsh", w =>
            {
                w.Ascii("VlLs").I32(2).Ascii("Objc");
                P.Descriptor(w, "brushPreset", ("Nm  ", P.Text("A")), ("Brsh", P.Obj("computedBrush")), ("useTexture", P.Bool(true)), ("Txtr", P.Obj("Ptrn", ("Nm  ", P.Text("Lab")), ("Idnt", P.Text("lab-1")))));
                w.Ascii("Objc");
                P.Descriptor(w, "brushPreset", ("Nm  ", P.Text("B")), ("Brsh", P.Obj("computedBrush")), ("useTexture", P.Bool(true)), ("Txtr", P.Obj("Ptrn", ("Nm  ", P.Text("Colour")), ("Idnt", P.Text("rgb-1")))));
            }));
            var file = new W().I16(6).I16(1).Bytes(P.Section("desc", desc.ToArray())).Bytes(P.Section("patt", Framed(lab, rgb))).ToArray();
            var brushes = PhotoshopBrushReader.Read(file);
            Assert.That(brushes[0].Settings.Texture, Is.Null); Assert.That(brushes[0].Warnings, Has.Some.Contains("Lab mode are not supported"));
            var t = brushes[1].Settings.Texture;
            Assert.That(new[] { t[0, 0], t[1, 0] }, Is.EqualTo(new byte[] { 76, 29 }), "Rec. 601 grey of red and blue");
            Assert.That(brushes[1].Warnings, Has.Some.Contains("converted to grey"));
            var broken = new W().I16(6).I16(1).Bytes(P.Section("desc", desc.ToArray())).Bytes(P.Section("patt", new W().I32(9999).ToArray())).ToArray();
            Assert.That(PhotoshopBrushReader.Read(broken)[0].Warnings, Has.Some.Contains("patterns) could not be read"), "a damaged pattern section still imports the brushes");
        }

        [Test] public void PatFilesBecomeTextureBrushes()
        {
            var g = Pattern(1, "Grain", "g", 2, 2, new[] { new byte[] { 10, 20, 30, 40 } }, rle: true);
            var deep = Pattern(1, "Deep", "d", 1, 2, new[] { new byte[] { 0x80, 0x01, 0x40, 0x02 } }, depth: 16);
            var palette = new byte[768]; palette[3 * 7] = 0; palette[3 * 7 + 1] = 255; palette[3 * 7 + 2] = 0;
            var indexed = Pattern(2, "Indexed", "i", 1, 1, new[] { new byte[] { 7 } }, palette: palette);
            var cmyk = Pattern(4, "Print", "c", 1, 1, new[] { new byte[] { 0 }, new byte[] { 0 }, new byte[] { 0 }, new byte[] { 0 } });
            var file = new W().Ascii("8BPT").I16(1).I32(4).Bytes(g).Bytes(deep).Bytes(indexed).Bytes(cmyk).ToArray();
            var brushes = PhotoshopPatternReader.ReadPatBrushes(file);
            Assert.That(brushes.Select(b => b.Name), Is.EqualTo(new[] { "Grain", "Deep", "Indexed" }));
            var t = brushes[0].Settings.Texture;
            Assert.That(new[] { t[0, 0], t[1, 0], t[0, 1], t[1, 1] }, Is.EqualTo(new byte[] { 30, 40, 10, 20 }), "rows flipped to bottom-up");
            Assert.That(brushes[0].Settings.TextureDepth, Is.EqualTo(1)); Assert.That(brushes[0].Settings.Tip, Is.Null);
            Assert.That(brushes[0].Warnings, Has.Some.Contains("'Print' was skipped").And.Some.Contains("CMYK"));
            Assert.That(new[] { brushes[1].Settings.Texture[0, 0], brushes[1].Settings.Texture[0, 1] }, Is.EqualTo(new byte[] { 0x40, 0x80 }));
            Assert.That(brushes[1].Warnings, Has.Some.Contains("16-bit"));
            Assert.That(brushes[2].Settings.Texture[0, 0], Is.EqualTo(150), "palette green → (255 × 587 + 500) / 1000");
            Assert.That(() => PhotoshopPatternReader.ReadPatBrushes(new W().Ascii("8BPS").I16(1).I32(0).ToArray()), Throws.TypeOf<BrushImportException>().With.Message.Contains("8BPT"));
            Assert.That(() => PhotoshopPatternReader.ReadPatBrushes(new W().Ascii("8BPT").I16(1).I32(1).Bytes(cmyk).ToArray()), Throws.TypeOf<BrushImportException>().With.Message.Contains("CMYK"));
            Assert.That(() => PhotoshopPatternReader.ReadPatBrushes(new W().Ascii("8BPT").I16(1).I32(1).Bytes(g.Take(g.Length - 3).ToArray()).ToArray()), Throws.TypeOf<BrushImportException>().With.Message.Contains("truncated"));
            var big = Pattern(1, "Big", "b", 3000, 1, new[] { new byte[3000] });
            Assert.That(() => PhotoshopPatternReader.ReadPatBrushes(new W().Ascii("8BPT").I16(1).I32(1).Bytes(big).ToArray()), Throws.TypeOf<BrushImportException>().With.Message.Contains("2048"));
        }

        // ---------------- 置き場 ----------------

        [Test] public void TheBrushLibraryKeepsDynamicsAndReadsSchema1Files()
        {
            string project = Path.Combine(Path.GetTempPath(), "yolupainter-project-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(project);
            try
            {
                PainterSettings.ProjectRoot = project; string folder = PainterSettings.BrushFolder;
                var dualTip = new BrushTip("d", 2, 3, new byte[] { 1, 2, 3, 4, 5, 6 });
                var s = new BrushSettings { HueJitter = .25, SaturationJitter = .5, BrightnessJitter = .75, ForegroundBackgroundJitter = .125, Purity = -.5, ColorPerTip = false,
                    FadeSize = 3, FadeOpacity = 4, FadeFlow = 5, TiltSize = true, TiltAngle = true,
                    Dual = new DualBrush { Tip = dualTip, Radius = 6, Spacing = .5, Angle = 15, Roundness = .5, Scatter = 2, Count = 4, Mode = DualBrushMode.Subtract } };
                var preset = BrushLibrary.Personal.Add(new[] { new ImportedBrush("Dyn", "test", s) }, "")[0];
                BrushLibrary.Personal.Folder = null; // 読み直す
                var loaded = BrushLibrary.Personal.Presets.Single(p => p.Id == preset.Id).CreateSettings();
                Assert.That((loaded.HueJitter, loaded.SaturationJitter, loaded.BrightnessJitter, loaded.ForegroundBackgroundJitter, loaded.Purity, loaded.ColorPerTip), Is.EqualTo((.25, .5, .75, .125, -.5, false)));
                Assert.That((loaded.FadeSize, loaded.FadeOpacity, loaded.FadeFlow, loaded.TiltSize, loaded.TiltOpacity, loaded.TiltFlow, loaded.TiltAngle), Is.EqualTo((3, 4, 5, true, false, false, true)));
                var d = loaded.Dual;
                Assert.That((d.Radius, d.Spacing, d.Angle, d.Roundness, d.Scatter, d.Count, d.Mode), Is.EqualTo((6.0, .5, 15.0, .5, 2.0, 4, DualBrushMode.Subtract)));
                Assert.That(d.Tip.CopyAlpha(), Is.EqualTo(dualTip.CopyAlpha()));
                Assert.That(BrushTips.IdOf(d.Tip), Is.EqualTo(preset.Id + ":dual"), "the dual tip has an id the window can store");
                // schema 1（ダイナミクスの項目が無い）のファイル
                File.WriteAllText(Path.Combine(folder, "old-00000000.json"), "{\"schema\":1,\"name\":\"Old\",\"radius\":5,\"hardness\":0.5,\"spacing\":0.2,\"opacity\":1,\"flow\":1,\"roundness\":1,\"textureScale\":1,\"count\":1,\"pressureSize\":true}");
                BrushLibrary.Personal.Folder = null;
                var old = BrushLibrary.Personal.Presets.Single(p => p.Name == "Old").CreateSettings();
                Assert.That(old.HasColorDynamics || old.Dual != null || old.FadeSize != 0 || old.TiltSize, Is.False); Assert.That(old.ColorPerTip, Is.True);
                File.WriteAllText(Path.Combine(folder, "future-00000000.json"), "{\"schema\":3,\"name\":\"Future\"}");
                BrushLibrary.Personal.Folder = null;
                Assert.That(BrushLibrary.Personal.Presets.Any(p => p.Name == "Future"), Is.False, "a newer schema is not guessed at");
            }
            finally { PainterSettings.ProjectRoot = null; BrushLibrary.Personal.Folder = null; Directory.Delete(project, true); }
        }
    }
}
