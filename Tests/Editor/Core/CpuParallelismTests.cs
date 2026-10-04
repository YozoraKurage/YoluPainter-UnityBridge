using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>CPU の並列化（CoreParallelism）: 合成・Normal の出力・大きなダブのブラシ・塗りつぶし・グラデーション・変形・自動選択・
    /// フィルター・選択範囲の変更が、スレッド数 1・2・3・既定のどれでもバイト一致すること。合成はタイルの経路を画素ごとの参照
    /// （CompositePixel）とも突き合わせる。予算で断るときに何も残らないこと、取消でバイト一致に戻ること、変更の記録が同じタイルを
    /// 指すこと。設定の検証。</summary>
    public sealed class CpuParallelismTests
    {
        static readonly int[] Degrees = { 1, 2, 3, 0 };

        static T WithDegree<T>(int degree, Func<T> run)
        {
            int saved = CoreParallelism.MaxDegreeOfParallelism;
            CoreParallelism.MaxDegreeOfParallelism = degree;
            try { return run(); }
            finally { CoreParallelism.MaxDegreeOfParallelism = saved; }
        }
        /// <summary>run under every degree; every result must equal the one-thread result.</summary>
        static void SameForEveryDegree(Func<string> run, string what)
        {
            string single = WithDegree(1, run);
            foreach (int degree in Degrees.Skip(1))
                Assert.That(WithDegree(degree, run), Is.EqualTo(single), what + " with " + (degree == 0 ? "the default number of" : degree.ToString()) + " threads");
        }
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
        static string Hash(SparseTileSurface surface) => string.Join(";", surface.EnumerateTiles().Select(t => t.Coord + ":" + Hash(t.Bytes)));
        static string Hash(SelectionMask mask)
        {
            var amounts = new byte[mask.TileSize * mask.TileSize];
            return string.Join(";", mask.Tiles.Select(c => { mask.CopyTile(c, amounts); return c + ":" + Hash(amounts); }));
        }
        static void Import(SparseTileSurface s, int width, int height, int tile, Random rnd, double share, bool opaque = false)
        {
            var b = new byte[tile * tile * 4];
            for (int ty = 0; ty * tile < height; ty++) for (int tx = 0; tx * tile < width; tx++)
            {
                if (rnd.NextDouble() >= share) continue;
                rnd.NextBytes(b);
                for (int y = 0; y < tile; y++) for (int x = 0; x < tile; x++)
                {
                    int o = (y * tile + x) * 4;
                    if (tx * tile + x >= width || ty * tile + y >= height) { b[o] = b[o + 1] = b[o + 2] = b[o + 3] = 0; continue; }
                    if (opaque) b[o + 3] = 255; else if (b[o + 3] < 60) b[o + 3] = 0; // 透明でも RGB を持つ画素を含める
                }
                s.ImportTile(new TileCoord(tx, ty), b);
            }
        }

        /// <summary>A stack with every kind of entry: rasters in all blend modes, opacity, masks (one filtered), clipping, a fill
        /// layer, levels / hue-saturation / invert adjustments, a pass-through and an isolated group with clipped layers inside,
        /// a blurred layer, and Normal and Height layers.</summary>
        static PaintDocument Stack(int width = 300, int height = 200, int tile = 32)
        {
            var d = new PaintDocument(width, height, tile); var rnd = new Random(12);
            var modes = ((LayerBlendMode[])Enum.GetValues(typeof(LayerBlendMode))).Where(m => m != LayerBlendMode.PassThrough).ToArray();
            var bottom = d.AddLayer("bottom"); Import(bottom.GetChannel(PaintChannel.Color), width, height, tile, rnd, 1, opaque: true);
            d.SetChannelEnabled(bottom.Id, PaintChannel.Normal, true); Import(bottom.GetChannel(PaintChannel.Normal), width, height, tile, rnd, .8);
            d.SetChannelEnabled(bottom.Id, PaintChannel.Height, true); Import(bottom.GetChannel(PaintChannel.Height), width, height, tile, rnd, .9);
            var pass = d.AddGroup("pass"); var isolated = d.AddGroup("isolated"); d.SetLayerBlendMode(isolated.Id, LayerBlendMode.Multiply); d.SetLayerOpacity(isolated.Id, .8);
            for (int i = 0; i < modes.Length; i++)
            {
                var l = d.AddLayer("l" + i); Import(l.GetChannel(PaintChannel.Color), width, height, tile, rnd, .5);
                d.SetLayerBlendMode(l.Id, modes[i]); d.SetLayerOpacity(l.Id, .3 + .7 * rnd.NextDouble());
                if (i % 3 == 0) { d.SetChannelEnabled(l.Id, PaintChannel.Normal, true); Import(l.GetChannel(PaintChannel.Normal), width, height, tile, rnd, .4); }
                if (i % 4 == 1) { var m = d.AddLayerMask(l.Id); Import(m.Surface, width, height, tile, rnd, .5); if (i % 8 == 1) d.SetLayerMaskInverted(l.Id, true); }
                if (i % 5 == 2) d.SetLayerClipping(l.Id, true);
                if (i % 6 == 3) d.MoveLayerTo(l.Id, pass.Id, 0);
                if (i % 6 == 4) d.MoveLayerTo(l.Id, isolated.Id, 0);
            }
            d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(20, 90, 200, 70) } });
            var levels = d.AddAdjustmentLayer("levels", AdjustmentSettings.Levels(.1, .9, 1.4, .05, .95)); d.SetLayerOpacity(levels.Id, .7);
            var hue = d.AddAdjustmentLayer("hue", AdjustmentSettings.HueSaturation(40, .3, -.1), new[] { PaintChannel.Color });
            var mask = d.AddLayerMask(hue.Id); Import(mask.Surface, width, height, tile, rnd, .6);
            d.AddFilter(hue.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(3));
            var inverted = d.AddAdjustmentLayer("invert", AdjustmentSettings.Invert(), new[] { PaintChannel.Color }); d.SetLayerOpacity(inverted.Id, .25); d.SetLayerClipping(inverted.Id, true);
            var blurred = d.AddLayer("blurred"); Import(blurred.GetChannel(PaintChannel.Color), width, height, tile, rnd, .3); d.AddFilter(blurred.Id, FilterTarget.Content, FilterSettings.GaussianBlur(5));
            d.SetNormalSettings(d.NormalSettings.WithDerive(true).WithStrength(6));
            d.ClearHistory(); return d;
        }

        [Test] public void CompositesAndTheNormalOutputAreTheSameWithAnyNumberOfThreads()
        {
            SameForEveryDegree(() =>
            {
                var d = Stack(); // 毎回作り直す（フィルターのキャッシュも空から）
                var parts = new List<string>();
                foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Normal, PaintChannel.Height }) parts.Add(Hash(d.Composite(channel)));
                parts.Add(Hash(CpuCompositor.CompositeRegion(d, PaintChannel.Color, 17, 9, 201, 133)));
                parts.Add(Hash(CpuCompositor.CompositeRegion(d, PaintChannel.Color, 64, 32, 32, 32)));
                parts.Add(Hash(CpuCompositor.CompositeRegion(d, PaintChannel.Color, 5, 150, 290, 1)));
                parts.Add(Hash(NormalMaps.Output(d)));
                return string.Join(",", parts);
            }, "composite");
        }

        [Test] public void TheTileCompositorMatchesThePerPixelReferenceWithEveryThread()
        {
            var d = Stack(); var rnd = new Random(5);
            foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Normal })
            {
                var full = WithDegree(0, () => d.Composite(channel));
                for (int k = 0; k < 400; k++)
                {
                    int x = rnd.Next(d.Width), y = rnd.Next(d.Height), o = (y * d.Width + x) * 4;
                    Assert.That(new Rgba32(full[o], full[o + 1], full[o + 2], full[o + 3]), Is.EqualTo(d.CompositePixel(channel, x, y)), channel + " " + x + "," + y);
                }
            }
        }

        static BrushSample[] Path(double x0, double y0, double length, int n)
        {
            var s = new BrushSample[n + 1];
            for (int i = 0; i <= n; i++) { double t = i / (double)n; s[i] = new BrushSample(x0 + length * t, y0 + 40 * Math.Sin(t * 7), .4 + .6 * Math.Abs(Math.Sin(t * 4)), i * .01, .3 * Math.Sin(t * 3), .2); }
            return s;
        }
        static IEnumerable<KeyValuePair<string, BrushSettings>> BigBrushes()
        {
            var ink = new Rgba32(200, 60, 30, 255);
            yield return new KeyValuePair<string, BrushSettings>("round", new BrushSettings { Radius = 70, Hardness = .6, Spacing = .1, Color = ink });
            yield return new KeyValuePair<string, BrushSettings>("soft", new BrushSettings { Radius = 90, Hardness = 0, Spacing = .05, Flow = .3, Opacity = .8, PressureFlow = true, Color = ink });
            yield return new KeyValuePair<string, BrushSettings>("tip", new BrushSettings { Radius = 75, Spacing = .1, Color = ink, Tip = BuiltInBrushes.Tip("charcoal"), Angle = 25, FollowDirection = true, Roundness = .7 });
            yield return new KeyValuePair<string, BrushSettings>("texture", new BrushSettings { Radius = 70, Spacing = .1, Color = ink, Texture = BuiltInBrushes.Tip("grain"), TextureDepth = .8, TextureScale = 2 });
            yield return new KeyValuePair<string, BrushSettings>("dynamics", new BrushSettings
            {
                Radius = 70, Spacing = .1, Color = ink, SizeJitter = .3, Scatter = .2, Count = 2, Seed = 3, HueJitter = .2, ColorPerTip = true, CurveInterpolation = true, TaperIn = 30, TaperOut = 40,
                Dual = new DualBrush { Radius = 12, Spacing = .3, Hardness = .4, Scatter = .5, Count = 2, Mode = DualBrushMode.Overlay },
            });
            yield return new KeyValuePair<string, BrushSettings>("erase", new BrushSettings { Radius = 80, Hardness = .5, Spacing = .1, Erase = true, Opacity = .7 });
        }
        /// <summary>A stroke on a layer that already has pixels (some tiles uniform, some full), with a feathered selection.</summary>
        static PaintDocument Canvas(out PaintLayer layer, bool selection)
        {
            var d = new PaintDocument(600, 360, 64); var rnd = new Random(8);
            var bg = d.AddLayer("bg"); Import(bg.GetChannel(PaintChannel.Color), 600, 360, 64, rnd, 1, opaque: true);
            layer = d.AddLayer("paint"); Import(layer.GetChannel(PaintChannel.Color), 600, 360, 64, rnd, .4);
            layer.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(3, 2), Enumerable.Repeat((byte)90, 64 * 64 * 4).ToArray());
            if (selection) d.SetSelection(SelectionMask.Ellipse(d, 300, 180, 250, 150).Feather(12));
            d.ClearHistory(); return d;
        }

        [Test] public void LargeDabsGiveTheSameStrokeWithAnyNumberOfThreads()
        {
            foreach (var brush in BigBrushes())
                foreach (bool selection in new[] { false, true })
                    SameForEveryDegree(() =>
                    {
                        var d = Canvas(out var layer, selection); var surface = layer.GetChannel(PaintChannel.Color);
                        long serial = d.ChangeSerial; string log;
                        using (var stroke = d.BeginStroke(layer.Id, PaintChannel.Color, brush.Value.Clone()))
                        {
                            foreach (var p in Path(40, 170, 520, 60)) stroke.Add(p);
                            log = stroke.StampCount + "/" + stroke.ChangedTileCount + "/" + stroke.RollbackBytes;
                            Assert.That(stroke.Commit(), Is.True);
                        }
                        var changed = new List<TileCoord>(); Assert.That(d.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True);
                        string after = Hash(surface);
                        Assert.That(d.Undo(), Is.True); string undone = Hash(surface);
                        Assert.That(d.Redo(), Is.True); Assert.That(Hash(surface), Is.EqualTo(after), "redo");
                        return log + "|" + string.Join(" ", changed.Distinct().OrderBy(c => c)) + "|" + after + "|" + undone;
                    }, brush.Key + (selection ? " in a selection" : ""));
        }

        [Test] public void ALargeDabStrokeOnAMaskIsTheSameWithAnyNumberOfThreads()
        {
            SameForEveryDegree(() =>
            {
                var d = Canvas(out var layer, false); var mask = d.AddLayerMask(layer.Id); d.ClearHistory();
                using (var stroke = d.BeginMaskStroke(layer.Id, new BrushSettings { Radius = 85, Hardness = .3, Spacing = .08, Flow = .5 }))
                { foreach (var p in Path(30, 150, 540, 50)) stroke.Add(p); stroke.Commit(); }
                return Hash(mask.Surface) + Hash(d.Composite(PaintChannel.Color));
            }, "mask stroke");
        }

        [Test] public void CancellingOrRefusingALargeDabStrokeLeavesTheExactPixels()
        {
            foreach (int degree in Degrees)
            {
                var d = Canvas(out var layer, false); var surface = layer.GetChannel(PaintChannel.Color);
                string before = Hash(surface); long allocated = d.AllocatedBytes;
                WithDegree(degree, () =>
                {
                    using (var stroke = d.BeginStroke(layer.Id, PaintChannel.Color, new BrushSettings { Radius = 90, Spacing = .1, Hardness = .5 }))
                    { foreach (var p in Path(40, 170, 520, 60)) stroke.Add(p); stroke.Cancel(); }
                    return 0;
                });
                Assert.That(Hash(surface), Is.EqualTo(before), "cancel, " + degree + " threads");
                Assert.That(d.UndoCount, Is.Zero);
                // 途中で止まる予算: 何枚かのタイルを持った後で断られ、全部が元に戻る
                d.ActiveStrokeBudgetBytes = 64 * 64 * 4 * 6;
                var refused = WithDegree(degree, () =>
                {
                    var stroke = d.BeginStroke(layer.Id, PaintChannel.Color, new BrushSettings { Radius = 90, Spacing = .1, Hardness = .5 });
                    try { foreach (var p in Path(40, 170, 520, 60)) stroke.Add(p); stroke.Commit(); return null; }
                    catch (InvalidOperationException e) { return e.Message; }
                });
                Assert.That(refused, Does.Contain("budget"), degree + " threads");
                Assert.That(d.HasActiveStroke, Is.False);
                Assert.That(Hash(surface), Is.EqualTo(before), "refused, " + degree + " threads");
                Assert.That(d.AllocatedBytes, Is.EqualTo(allocated));
            }
        }

        [Test] public void RegionEditsAreTheSameWithAnyNumberOfThreads()
        {
            SameForEveryDegree(() =>
            {
                var d = Canvas(out var layer, false); var rnd = new Random(4); var parts = new List<string>();
                var surface = layer.GetChannel(PaintChannel.Color);
                d.Fill(layer.Id, PaintChannel.Color, new Rgba32(10, 200, 30, 180), .6); parts.Add(Hash(surface));
                d.SetSelection(SelectionMask.Ellipse(d, 250, 150, 200, 120).Feather(6));
                d.Fill(layer.Id, PaintChannel.Color, new Rgba32(200, 20, 30, 255), 1, null, erase: true); parts.Add(Hash(surface));
                d.Gradient(layer.Id, PaintChannel.Color, new GradientSettings { Shape = GradientShape.Radial, X0 = 200, Y0 = 100, X1 = 500, Y1 = 300, From = new Rgba32(255, 0, 0, 255), To = new Rgba32(0, 0, 255, 40), Opacity = .9 }); parts.Add(Hash(surface));
                var image = new byte[600 * 360 * 4]; rnd.NextBytes(image);
                d.ReplacePixels(layer.Id, PaintChannel.Color, image); parts.Add(Hash(surface));
                d.AddLayerMask(layer.Id); d.FillMask(layer.Id, .7); parts.Add(Hash(layer.Mask.Surface));
                d.Transform(layer.Id, Affine2D.FromParts(300, 180, 20, -10, 17, 1.3, .8)); parts.Add(Hash(surface) + Hash(layer.Mask.Surface) + Hash(d.Selection));
                d.ClearSelection();
                d.Transform(layer.Id, Affine2D.FromParts(300, 180, 0, 0, 90, -1, 1)); parts.Add(Hash(surface));
                d.Transform(layer.Id, Affine2D.FromParts(300, 180, 3, 3, 0, 1.7, 1.7), resampling: Resampling.Nearest); parts.Add(Hash(surface));
                while (d.Undo()) { }
                parts.Add(Hash(surface));
                return string.Join(",", parts);
            }, "fill, gradient, replace, mask fill and transform");
        }

        [Test] public void RefusedRegionEditsChangeNothingWithAnyNumberOfThreads()
        {
            foreach (int degree in Degrees)
            {
                var d = Canvas(out var layer, false); var surface = layer.GetChannel(PaintChannel.Color); string before = Hash(surface);
                d.ActiveStrokeBudgetBytes = 64 * 64 * 4 * 5; // 数枚で尽きる
                var fill = WithDegree(degree, () => Assert.Throws<InvalidOperationException>(() => d.Fill(layer.Id, PaintChannel.Color, new Rgba32(1, 2, 3, 255))));
                Assert.That(fill.Message, Does.Contain("budget"));
                Assert.That(Hash(surface), Is.EqualTo(before), "fill, " + degree + " threads");
                // 巻き戻しの見積もりは通り、書き込みの途中で面の予算が尽きる（回すと新しいタイルに広がる）
                d.ActiveStrokeBudgetBytes = 64L << 20; d.SourceBudgetBytes = d.AllocatedBytes + 64 * 64 * 4 * 2;
                var transform = WithDegree(degree, () => { try { d.Transform(layer.Id, Affine2D.FromParts(300, 180, 0, 0, 33, 1, 1)); return null; } catch (InvalidOperationException e) { return e.Message; } });
                Assert.That(transform, Does.Contain("Source tile payload budget"), degree + " threads");
                Assert.That(Hash(surface), Is.EqualTo(before), "transform, " + degree + " threads");
                Assert.That(d.UndoCount, Is.Zero);
            }
        }

        [Test] public void SelectionsAndTheMagicWandAreTheSameWithAnyNumberOfThreads()
        {
            SameForEveryDegree(() =>
            {
                var d = Stack(); var parts = new List<string>(); var raster = d.Layers.First(l => l.Kind == LayerKind.Raster);
                // 層の画素を基準に連続／全体、合成を基準に連続／全体
                parts.Add(Hash(SelectionMask.MagicWand(d, raster.Id, PaintChannel.Color, 50, 50, 90, false)));
                parts.Add(Hash(SelectionMask.MagicWand(d, raster.Id, PaintChannel.Color, 50, 50, 200, true)));
                parts.Add(Hash(SelectionMask.MagicWand(d, null, PaintChannel.Color, 120, 80, 120, false)));
                parts.Add(Hash(SelectionMask.MagicWand(d, null, PaintChannel.Color, 120, 80, 160, true)));
                var e = SelectionMask.Ellipse(d, 150, 100, 120, 70);
                parts.Add(Hash(e)); parts.Add(Hash(e.Grow(9))); parts.Add(Hash(e.Shrink(7))); parts.Add(Hash(e.Feather(11))); parts.Add(Hash(e.Border(5)));
                parts.Add(Hash(SelectionMask.Polygon(d, new[] { (10.5, 10.0), (280.0, 40.0), (150.0, 190.0), (60.0, 120.0) })));
                return string.Join(",", parts);
            }, "selections");
        }

        [Test] public void TheContiguousMagicWandSelectsTheConnectedPixelsOnly()
        {
            // 走査線の塗りつぶしに替えた: 4 近傍でつながる所だけ（斜めは越えない）、穴は残し、外接矩形の外は選ばない
            var d = new PaintDocument(40, 30, 8); var layer = d.AddLayer("L"); var s = layer.GetChannel(PaintChannel.Color);
            var ink = new Rgba32(0, 0, 0, 255);
            for (int y = 5; y < 20; y++) for (int x = 5; x < 25; x++) if (!(x >= 10 && x < 13 && y >= 9 && y < 12)) s.SetPixel(x, y, ink); // 穴のある四角
            for (int y = 3; y < 28; y++) s.SetPixel(30, y, ink); // 縦の線（つながっていない）
            s.SetPixel(25, 20, ink); // 斜めにだけ接する画素
            for (int x = 14; x < 22; x++) s.SetPixel(x, 20, ink); // 下に突き出た行（四角とつながる）
            var wand = SelectionMask.MagicWand(d, layer.Id, PaintChannel.Color, 6, 6, 0, true);
            for (int y = 0; y < 30; y++) for (int x = 0; x < 40; x++)
            {
                bool expected = x >= 5 && x < 25 && y >= 5 && y < 20 && !(x >= 10 && x < 13 && y >= 9 && y < 12) || y == 20 && x >= 14 && x < 22;
                Assert.That(wand[x, y], Is.EqualTo(expected ? (byte)255 : (byte)0), x + "," + y);
            }
        }

        [Test] public void AnOpaquePixelLaidOverWithNormalIsTheFormulaResultForEveryBackdrop()
        {
            // 合成とブラシは「不透明な画素を通常・量 1 で重ねる」と上の画素をそのまま置く近道を使う。W3C の source-over の式を
            // 丸めた結果と、下の不透明度は全部・下の成分は間引き・上の成分は全部の組で一致する（全 256³ 通りは計測用の環境で確かめた）
            byte ToByte(double v) => (byte)Math.Max(0, Math.Min(255, Math.Floor(v * 255 + 0.5)));
            var below = Enumerable.Range(0, 256).Where(v => v % 17 == 0 || v <= 2 || v >= 253 || v == 127 || v == 128).ToArray();
            int bad = 0;
            for (int da = 0; da < 256; da++) foreach (int d in below) for (int s = 0; s < 256; s++)
            {
                double sa = 1, dA = da / 255.0, a = sa + dA * (1 - sa), dr = d / 255.0, sr = s / 255.0;
                byte expected = ToByte(((1 - sa) * dA * dr + (1 - dA) * sa * sr + dA * sa * sr) / a);
                var r = CpuCompositor.Blend(new Rgba32((byte)d, (byte)(255 - d), 0, (byte)da), new Rgba32((byte)s, (byte)(255 - s), 9, 255));
                if (r.R != expected || r.A != ToByte(a) || r.G != 255 - s || r.B != 9) bad++;
            }
            Assert.That(bad, Is.Zero);
        }

        [Test] public void TheSettingRejectsNegativeCountsAndZeroMeansEveryProcessor()
        {
            int saved = CoreParallelism.MaxDegreeOfParallelism;
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => CoreParallelism.MaxDegreeOfParallelism = -1);
                CoreParallelism.MaxDegreeOfParallelism = 0; Assert.That(CoreParallelism.Degree, Is.EqualTo(Math.Max(1, Environment.ProcessorCount)));
                CoreParallelism.MaxDegreeOfParallelism = 3; Assert.That(CoreParallelism.Degree, Is.EqualTo(3));
            }
            finally { CoreParallelism.MaxDegreeOfParallelism = saved; }
        }
    }

    /// <summary>CPU の処理時間（明示して走らせる: --filter 'Yozolab.YoluPainter.Tests.CpuTimings'）。4096²・タイル 128、各 3 回をログに出す。
    /// 1 スレッドと既定のスレッド数の両方。ブラシは 4096² の空の層に長さ 2000 px・250 点の線（半径 4 / 32 / 128、間隔 0.1、硬さ 0.8）。
    /// 合成は全面がランダムな不透明の 1 層 / 10 層（モードと不透明度を混ぜる）、塗りつぶしは空の層の全面、変形は全面の層の 15° 回転。</summary>
    [Explicit("Timing measurement")]
    public sealed class CpuTimings
    {
        static PaintDocument Doc(int layers)
        {
            var d = new PaintDocument(4096, 4096, 128) { SourceBudgetBytes = 8L << 30, ActiveStrokeBudgetBytes = 4L << 30, UndoBudgetBytes = 4L << 30 };
            var rnd = new Random(1); var b = new byte[128 * 128 * 4];
            var modes = new[] { LayerBlendMode.Normal, LayerBlendMode.Multiply, LayerBlendMode.Screen, LayerBlendMode.Overlay, LayerBlendMode.SoftLight, LayerBlendMode.ColorDodge, LayerBlendMode.Hue, LayerBlendMode.Difference, LayerBlendMode.LinearLight, LayerBlendMode.Normal };
            for (int i = 0; i < layers; i++)
            {
                var l = d.AddLayer("bg" + i); var s = l.GetChannel(PaintChannel.Color);
                for (int ty = 0; ty < 32; ty++) for (int tx = 0; tx < 32; tx++) { rnd.NextBytes(b); for (int k = 3; k < b.Length; k += 4) b[k] = (byte)(i == 0 ? 255 : b[k] | 0x40); s.ImportTile(new TileCoord(tx, ty), b); }
                if (i > 0) { d.SetLayerBlendMode(l.Id, modes[i % modes.Length]); d.SetLayerOpacity(l.Id, .5 + .05 * i); }
            }
            d.ClearHistory(); return d;
        }
        static string Measure(string name, Func<PaintDocument> setup, Action<PaintDocument> run)
        {
            var times = new List<string>();
            foreach (int degree in new[] { 1, 0 })
            {
                int saved = CoreParallelism.MaxDegreeOfParallelism; CoreParallelism.MaxDegreeOfParallelism = degree;
                try
                {
                    var runs = new List<double>();
                    for (int r = 0; r < 3; r++)
                    {
                        var d = setup(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        var sw = Stopwatch.StartNew(); run(d); runs.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    times.Add((degree == 0 ? CoreParallelism.Degree + " threads " : "1 thread ") + string.Join(" / ", runs.Select(t => t.ToString("F0"))) + " ms");
                }
                finally { CoreParallelism.MaxDegreeOfParallelism = saved; }
            }
            return name + ": " + string.Join(", ", times);
        }
        static void Stroke(PaintDocument d, double radius)
        {
            var layer = d.Layers[d.Layers.Count - 1];
            using (var stroke = d.BeginStroke(layer.Id, PaintChannel.Color, new BrushSettings { Radius = radius, Hardness = .8, Spacing = .1, Color = new Rgba32(200, 60, 30, 255) }))
            {
                for (int i = 0; i <= 250; i++) { double t = i / 250.0; stroke.Add(new BrushSample(300 + 2000 * t, 1000 + 300 * Math.Sin(t * 9), .3 + .7 * Math.Abs(Math.Sin(t * 5)), i * .008)); }
                stroke.Commit();
            }
        }

        [Test] public void MeasureCpuTimes()
        {
            var lines = new List<string> { "CPU threads " + Environment.ProcessorCount };
            foreach (double r in new[] { 4.0, 32, 128 }) lines.Add(Measure("brush r" + r, () => { var d = Doc(0); d.AddLayer("paint"); d.ClearHistory(); return d; }, d => Stroke(d, r)));
            lines.Add(Measure("composite 1 layer", () => Doc(1), d => d.Composite(PaintChannel.Color)));
            lines.Add(Measure("composite 10 layers", () => Doc(10), d => d.Composite(PaintChannel.Color)));
            lines.Add(Measure("16 single tiles, 10 layers", () => Doc(10), d => { for (int i = 0; i < 16; i++) CpuCompositor.CompositeRegion(d, PaintChannel.Color, (8 + i % 4) * 128, (10 + i / 4) * 128, 128, 128); }));
            lines.Add(Measure("fill", () => { var d = Doc(0); d.AddLayer("f"); d.ClearHistory(); return d; }, d => d.Fill(d.Layers[0].Id, PaintChannel.Color, new Rgba32(10, 200, 30, 255), .7)));
            lines.Add(Measure("rotate 15°", () => Doc(1), d => d.Transform(d.Layers[0].Id, Affine2D.FromParts(2048, 2048, 0, 0, 15, 1, 1))));
            TestContext.WriteLine("CPU timings:\n" + string.Join("\n", lines));
        }
    }
}
