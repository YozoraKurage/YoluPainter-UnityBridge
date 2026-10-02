using System;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ブラシエンジン: ストローク内の濃さの溜まり方、画像の筆先・回転・潰し・進行方向、ゆらぎと散布、紙の質感、内蔵ブラシ。</summary>
    public sealed class BrushTests
    {
        static readonly Rgba32 Black = new Rgba32(0, 0, 0, 255);
        static PaintDocument Canvas(int size = 64) { var d = new PaintDocument(size, size, 16); d.AddLayer("Paint"); d.ClearHistory(); return d; }
        static Guid L(PaintDocument d) => d.Layers[0].Id;
        static byte A(PaintDocument d, int x, int y) => d.CompositePixel(PaintChannel.Color, x, y).A;
        static BrushSettings Fixed(Action<BrushSettings> configure = null)
        {
            var s = new BrushSettings { Radius = 8, Hardness = 1, Color = Black, PressureSize = false, PressureOpacity = false, Spacing = .1 };
            configure?.Invoke(s); return s;
        }
        static void Stroke(PaintDocument d, BrushSettings s, params (double x, double y)[] points)
        {
            using (var stroke = d.BeginStroke(L(d), PaintChannel.Color, s))
            {
                double t = 0; foreach (var p in points) stroke.Add(new BrushSample(p.x, p.y, 1, t += .01));
                stroke.Commit();
            }
        }

        [Test] public void OverlappingDabsNeverExceedTheStrokesOpacity()
        {
            var d = Canvas();
            Stroke(d, Fixed(s => s.Opacity = .5), (10, 32), (54, 32)); // 何十個も重なる
            Assert.That(A(d, 32, 32), Is.EqualTo(128), "capped at opacity 0.5");
            Stroke(d, Fixed(s => s.Opacity = .5), (32, 10), (32, 54));
            Assert.That(A(d, 32, 32), Is.EqualTo(192), "a new stroke builds on the previous result: 128/255 + 0.5 x (1 - 128/255) = 191.5/255");
        }

        [Test] public void LowFlowBuildsUpTowardTheOpacityCeiling()
        {
            var d = Canvas();
            using (var s = d.BeginStroke(L(d), PaintChannel.Color, Fixed(b => { b.Flow = .25; b.Opacity = .8; })))
            {
                s.ApplyPixel(5, 5, 1); Assert.That(A(d, 5, 5), Is.EqualTo(51), "0.8 x 0.25");
                s.ApplyPixel(5, 5, 1); Assert.That(A(d, 5, 5), Is.EqualTo(89), "0.2 + (0.8 - 0.2) x 0.25 = 0.35");
                for (int i = 0; i < 60; i++) s.ApplyPixel(5, 5, 1);
                Assert.That(A(d, 5, 5), Is.EqualTo(204), "converges on the 0.8 ceiling");
                s.Commit();
            }
            Assert.That(d.Undo(), Is.True); Assert.That(A(d, 5, 5), Is.Zero);
        }

        [Test] public void ATipImageIsPlacedUprightAndRotatesCounterClockwise()
        {
            // 上半分だけ埋まった筆先: 角度 0 では中心より上（Y が大きい側）だけ塗れる。90° 回すと左側になる。
            var alpha = new byte[16 * 16]; for (int y = 8; y < 16; y++) for (int x = 0; x < 16; x++) alpha[y * 16 + x] = 255;
            var tip = new BrushTip("upper half", 16, 16, alpha);
            var d = Canvas();
            using (var s = d.BeginStroke(L(d), PaintChannel.Color, Fixed(b => b.Tip = tip))) { s.Add(new BrushSample(32, 32)); s.Commit(); }
            Assert.That(A(d, 32, 36), Is.EqualTo(255)); Assert.That(A(d, 32, 28), Is.Zero);
            var r = Canvas();
            using (var s = r.BeginStroke(L(r), PaintChannel.Color, Fixed(b => { b.Tip = tip; b.Angle = 90; }))) { s.Add(new BrushSample(32, 32)); s.Commit(); }
            Assert.That(A(r, 28, 32), Is.EqualTo(255), "rotated 90° the filled half points left"); Assert.That(A(r, 36, 32), Is.Zero);
        }

        [Test] public void RoundnessSquashesAndFollowDirectionTurnsTheTip()
        {
            var d = Canvas();
            using (var s = d.BeginStroke(L(d), PaintChannel.Color, Fixed(b => b.Roundness = .25))) { s.Add(new BrushSample(32, 32)); s.Commit(); }
            Assert.That(A(d, 38, 32), Is.EqualTo(255), "full width along the tip's horizontal axis");
            Assert.That(A(d, 32, 36), Is.Zero, "a quarter height across it");
            // 縦に動くストロークで進行方向に合わせると、潰れた筆先は縦向きの線になる（横に細い）。
            var f = Canvas();
            Stroke(f, Fixed(b => { b.Roundness = .25; b.FollowDirection = true; }), (32, 10), (32, 54));
            Assert.That(A(f, 32, 40), Is.EqualTo(255)); Assert.That(A(f, 36, 40), Is.Zero);
        }

        [Test] public void JitterAndScatterAreDeterministicPerSeedAndStayInRange()
        {
            BrushSettings Wild(int seed) => Fixed(b => { b.Seed = seed; b.Scatter = 1; b.Count = 4; b.SizeJitter = .8; b.AngleJitter = 1; b.OpacityJitter = .9; b.FlowJitter = .5; b.Spacing = .5; });
            byte[] Paint(int seed) { var d = Canvas(128); Stroke(d, Wild(seed), (40, 64), (88, 64)); return d.Composite(PaintChannel.Color); }
            Assert.That(Paint(7), Is.EqualTo(Paint(7)), "same seed, same pixels");
            Assert.That(Paint(7), Is.Not.EqualTo(Paint(8)), "different seed, different scatter");
            var pixels = Paint(3);
            for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
                if (pixels[(y * 128 + x) * 4 + 3] > 0)
                    Assert.That(y >= 64 - 24 && y <= 64 + 24 && x >= 40 - 24 && x <= 88 + 24, Is.True, $"({x},{y}) is beyond scatter + radius");
        }

        [Test] public void APaperTextureModulatesCoverage()
        {
            var stripes = new byte[4 * 4]; for (int y = 0; y < 4; y++) for (int x = 0; x < 2; x++) stripes[y * 4 + x] = 255; // 2 列おきの縞
            var texture = new BrushTip("stripes", 4, 4, stripes);
            var d = Canvas();
            Stroke(d, Fixed(b => { b.Texture = texture; b.TextureDepth = 1; b.Hardness = 1; }), (16, 32), (48, 32));
            Assert.That(A(d, 32, 32), Is.EqualTo(255), "x=32 is on a full stripe");
            Assert.That(A(d, 34, 32), Is.Zero, "x=34 is on an empty stripe");
            var half = Canvas();
            Stroke(half, Fixed(b => { b.Texture = texture; b.TextureDepth = .5; }), (16, 32), (48, 32));
            Assert.That(A(half, 34, 32), Is.EqualTo(128), "depth 0.5 caps empty stripes at half, however many dabs overlap");
        }

        [Test] public void TheRoundTipIsUnchangedByTheNewEngine()
        {
            // 角度 0・丸さ 1・ゆらぎ無しの丸ブラシは、1 回のダブが従来の式（硬さの smoothstep）そのもの。
            var d = Canvas();
            using (var s = d.BeginStroke(L(d), PaintChannel.Color, Fixed(b => { b.Hardness = .5; b.Radius = 10; }))) { s.Add(new BrushSample(32.3, 31.7)); s.Commit(); }
            for (int y = 20; y < 44; y++) for (int x = 20; x < 44; x++)
            {
                double dist = Math.Sqrt((x + .5 - 32.3) * (x + .5 - 32.3) + (y + .5 - 31.7) * (y + .5 - 31.7)) / 10, expected = 0;
                if (dist <= 1) { expected = 1; if (dist > .5) { double t = (1 - dist) / .5; expected = t * t * (3 - 2 * t); } }
                Assert.That(A(d, x, y), Is.EqualTo((byte)Math.Floor(expected * 255 + .5)), $"({x},{y})");
            }
        }

        [Test] public void BrushTipsValidateTheirInput()
        {
            Assert.Throws<ArgumentException>(() => new BrushTip("bad", 4, 4, new byte[15]));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BrushTip("huge", BrushTip.MaxSize + 1, 1, new byte[BrushTip.MaxSize + 1]));
            var source = new byte[] { 0, 255, 255, 0 }; var tip = new BrushTip("copy", 2, 2, source); source[0] = 99;
            Assert.That(tip[0, 0], Is.Zero, "the tip copies its input");
            Assert.That(tip.Sample(-.1, .5), Is.Zero); Assert.That(tip.SampleTiled(-1.5, 2.5), Is.EqualTo(tip.SampleTiled(.5, .5)).Within(1e-9), "tiled sampling wraps");
            Assert.Throws<ArgumentOutOfRangeException>(() => Fixed(b => b.Count = 0).Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => Fixed(b => b.Roundness = 0).Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => Fixed(b => b.SizeJitter = 1.5).Validate());
        }

        [Test] public void EveryBuiltInBrushPaintsDeterministically()
        {
            Assert.That(BuiltInBrushes.Presets.Count, Is.GreaterThanOrEqualTo(12));
            Assert.That(BuiltInBrushes.Presets.Select(p => p.Id).Distinct().Count(), Is.EqualTo(BuiltInBrushes.Presets.Count), "ids are unique");
            foreach (var preset in BuiltInBrushes.Presets)
            {
                byte[] Paint()
                {
                    var d = Canvas(128); var settings = preset.CreateSettings(); settings.Color = Black;
                    if (settings.Erase) { var surface = d.Layers[0].GetChannel(PaintChannel.Color); for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++) surface.SetPixel(x, y, Black); d.ClearHistory(); }
                    using (var s = d.BeginStroke(L(d), PaintChannel.Color, settings))
                    { s.Add(new BrushSample(30, 60, .8, 0)); s.Add(new BrushSample(70, 70, 1, .01)); s.Add(new BrushSample(100, 62, .6, .02)); s.Commit(); }
                    return d.Composite(PaintChannel.Color);
                }
                var first = Paint();
                Assert.That(first, Is.EqualTo(Paint()), preset.Id + " is deterministic");
                bool changed = preset.CreateSettings().Erase ? first.Where((b, i) => i % 4 == 3).Any(a => a < 255) : first.Where((b, i) => i % 4 == 3).Any(a => a > 0);
                Assert.That(changed, Is.True, preset.Id + " leaves a mark");
            }
            Assert.That(BuiltInBrushes.Tip("grain"), Is.SameAs(BuiltInBrushes.Tip("grain")), "generated tips are cached");
            Assert.That(BuiltInBrushes.Tip("missing"), Is.Null);
            var copy = BuiltInBrushes.Find("chalk").CreateSettings(); copy.Radius = 99;
            Assert.That(BuiltInBrushes.Find("chalk").CreateSettings().Radius, Is.EqualTo(18), "presets hand out copies");
        }
    }
}
