using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>キャンバスの大きさの変更（<see cref="PaintDocument.Resampled"/>）: 最近傍・バイリニア・面積平均の値（整数倍の拡大で画素が
    /// そのまま複製される、半分への縮小、透明画素の RGB）、層の種類・全チャンネル・マスク・選択範囲・構造の写し、画素の単位の値
    /// （フィルターの半径・Height → Normal の強さ・2D のパス）の縮尺と上限、3D のパスの層の知らせ、予算や上限で断ると元が変わらないこと、
    /// 型の拒否、保存の往復（8192 も読み戻せる）、スレッドの数に依らないこと。</summary>
    public sealed class ResizeTests
    {
        static readonly CanvasResampling[] Methods = { CanvasResampling.Nearest, CanvasResampling.Bilinear, CanvasResampling.Area };

        static PaintDocument Doc(int w, int h, out PaintLayer layer, int tile = 8)
        { var d = new PaintDocument(w, h, tile); layer = d.AddLayer("L"); d.ClearHistory(); return d; }
        static SparseTileSurface Color(PaintLayer l) => l.GetChannel(PaintChannel.Color);
        /// <summary>画素ごとに違う色。透明でも RGB を持つ画素と半透明の画素を含む。</summary>
        static Rgba32 Pattern(int x, int y) => new Rgba32((byte)(x * 7 + 1), (byte)(y * 5 + 2), (byte)((x ^ y) * 3), (byte)((x + y) % 3 == 0 ? 0 : (x + y) % 3 == 1 ? 128 : 255));
        static void Paint(PaintDocument d, SparseTileSurface s, Func<int, int, Rgba32> color)
        { for (int y = 0; y < s.Height; y++) for (int x = 0; x < s.Width; x++) s.SetPixel(x, y, color(x, y)); d.ClearHistory(); }
        static Rgba32 At(PaintDocument d, int x, int y, PaintChannel c = PaintChannel.Color) => d.Layers[0].GetPixel(c, x, y);
        /// <summary>面のタイル（座標と画素）を 1 つのバイト列に。</summary>
        static byte[] Tiles(SparseTileSurface s) => s.EnumerateTiles().SelectMany(t => BitConverter.GetBytes(t.Coord.X).Concat(BitConverter.GetBytes(t.Coord.Y)).Concat(t.Bytes)).ToArray();

        // ───────── 値 ─────────

        [Test] public void WholeNumberEnlargementsRepeatEveryPixelExactlyWithNearestAndArea()
        {
            var d = Doc(16, 16, out var layer); Paint(d, Color(layer), Pattern);
            foreach (var method in new[] { CanvasResampling.Nearest, CanvasResampling.Area })
                foreach (int factor in new[] { 2, 4 })
                {
                    var r = d.Resampled(16 * factor, 16 * factor, method).Document;
                    for (int y = 0; y < r.Height; y++) for (int x = 0; x < r.Width; x++)
                        Assert.That(At(r, x, y), Is.EqualTo(Pattern(x / factor, y / factor)), method + " ×" + factor + " at " + x + "," + y);
                }
            foreach (var method in Methods)
            {
                var same = d.Resampled(16, 16, method).Document;
                Assert.That(DocumentBinary.Write(same), Is.EqualTo(DocumentBinary.Write(d)), method + ": the same size is an exact copy");
            }
        }

        [Test] public void NearestHalvingKeepsTheUpperRightPixelOfEachBlock()
        {
            var d = Doc(32, 32, out var layer); Paint(d, Color(layer), Pattern);
            var r = d.Resampled(16, 16, CanvasResampling.Nearest).Document;
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) Assert.That(At(r, x, y), Is.EqualTo(Pattern(2 * x + 1, 2 * y + 1)), x + "," + y);
        }

        [Test] public void AreaShrinkingAveragesPremultipliedAndKeepsTheColourUnderZeroAlpha()
        {
            var red = new Rgba32(255, 0, 0); var hidden = new Rgba32(10, 20, 30, 0);
            // 2 × 2 の区画ごとに意味のある組み合わせ（左下から x の順）
            var blocks = new[]
            {
                new[] { red, Rgba32.Transparent, Rgba32.Transparent, Rgba32.Transparent },              // 赤 1/4: 透明は暗くしない
                new[] { new Rgba32(255, 255, 255), new Rgba32(255, 255, 255), new Rgba32(0, 0, 0), new Rgba32(0, 0, 0) }, // 灰色 127.5 → 128
                new[] { hidden, hidden, hidden, hidden },                                               // 隠れた色はそのまま
                new[] { hidden, Rgba32.Transparent, hidden, Rgba32.Transparent },                       // 透明の RGB は（アルファの重み無しで）平均
                new[] { new Rgba32(200, 0, 0, 1), Rgba32.Transparent, Rgba32.Transparent, Rgba32.Transparent }, // α 0.25 は 0 に丸まり、見えている赤を隠れた色に混ぜない
            };
            var d = Doc(blocks.Length * 2, 2, out var layer);
            for (int b = 0; b < blocks.Length; b++) for (int k = 0; k < 4; k++) Color(layer).SetPixel(b * 2 + k % 2, k / 2, blocks[b][k]);
            d.ClearHistory();
            var r = d.Resampled(blocks.Length, 1, CanvasResampling.Area).Document;
            Assert.That(At(r, 0, 0), Is.EqualTo(new Rgba32(255, 0, 0, 64)));
            Assert.That(At(r, 1, 0), Is.EqualTo(new Rgba32(128, 128, 128)));
            Assert.That(At(r, 2, 0), Is.EqualTo(hidden));
            Assert.That(At(r, 3, 0), Is.EqualTo(new Rgba32(5, 10, 15, 0)));
            Assert.That(At(r, 4, 0), Is.EqualTo(Rgba32.Transparent));
            Assert.That(DocumentBinary.Write(d.Resampled(blocks.Length, 1, CanvasResampling.Bilinear).Document), Is.EqualTo(DocumentBinary.Write(r)), "halving with bilinear averages the same 2 × 2 blocks");

            // 4 分の 1: 面積平均は 16 画素の平均、バイリニアは真ん中の 2 × 2 だけを読む（だから縮小は面積平均）
            var e = Doc(8, 8, out var single); Color(single).SetPixel(0, 0, new Rgba32(255, 255, 255)); e.ClearHistory();
            Assert.That(At(e.Resampled(2, 2, CanvasResampling.Area).Document, 0, 0), Is.EqualTo(new Rgba32(255, 255, 255, 16)), "255 / 16 = 15.9 → 16");
            Assert.That(At(e.Resampled(2, 2, CanvasResampling.Bilinear).Document, 0, 0), Is.EqualTo(Rgba32.Transparent), "bilinear skips the corner pixel");
        }

        [Test] public void BilinearEnlargementInterpolatesPremultipliedAndRepeatsTheEdge()
        {
            var red = new Rgba32(255, 0, 0); var hiddenBlue = new Rgba32(0, 0, 255, 0);
            var d = Doc(2, 1, out var layer, tile: 2); Color(layer).SetPixel(0, 0, new Rgba32(0, 0, 0)); Color(layer).SetPixel(1, 0, new Rgba32(255, 255, 255)); d.ClearHistory();
            var r = d.Resampled(4, 1, CanvasResampling.Bilinear).Document;
            Assert.That(Enumerable.Range(0, 4).Select(x => At(r, x, 0).R), Is.EqualTo(new byte[] { 0, 64, 191, 255 }), "centres at −0.25, 0.25, 0.75, 1.25: the edge pixel repeats outside the canvas");
            Assert.That(Enumerable.Range(0, 4).Select(x => At(r, x, 0).A), Is.All.EqualTo(255), "no transparent fringe at the canvas edge");

            Color(layer).SetPixel(0, 0, red); Color(layer).SetPixel(1, 0, hiddenBlue); d.ClearHistory();
            r = d.Resampled(4, 1, CanvasResampling.Bilinear).Document;
            Assert.That(At(r, 0, 0), Is.EqualTo(red));
            Assert.That(At(r, 1, 0), Is.EqualTo(new Rgba32(255, 0, 0, 191)), "a transparent neighbour lowers alpha but does not tint");
            Assert.That(At(r, 2, 0), Is.EqualTo(new Rgba32(255, 0, 0, 64)));
            Assert.That(At(r, 3, 0), Is.EqualTo(hiddenBlue), "a pixel read only from a transparent pixel keeps its colour");
        }

        [Test] public void UniformTransparentColourSurvivesEveryMethodAndSize()
        {
            var hidden = new Rgba32(200, 100, 50, 0);
            var d = Doc(64, 64, out var layer, tile: 16); Paint(d, Color(layer), (x, y) => hidden);
            foreach (var method in Methods)
                foreach (int size in new[] { 16, 48, 128 })
                {
                    var r = d.Resampled(size, size, method).Document;
                    for (int y = 0; y < size; y += 7) for (int x = 0; x < size; x += 5) Assert.That(At(r, x, y), Is.EqualTo(hidden), method + " " + size);
                    Assert.That(r.Layers[0].Channels[PaintChannel.Color].AllocatedBytes, Is.EqualTo(4L * (size / 16) * (size / 16)), method + " " + size + ": every tile stays uniform (four bytes)");
                }
        }

        [Test] public void NormalAveragesAreRenormalizedButASingleOrUniformSourceIsCopied()
        {
            var d = Doc(4, 4, out var layer, tile: 4); d.SetChannelEnabled(layer.Id, PaintChannel.Normal, true); d.ClearHistory();
            var normal = layer.GetChannel(PaintChannel.Normal);
            var tilted = NormalMaps.Encode(1, 0, 1, 255); var flat = new Rgba32(128, 128, 255);
            Paint(d, normal, (x, y) => x == 0 ? tilted : x == 1 ? flat : new Rgba32(200, 200, 200)); // 右半分は単位でないベクトル
            var r = d.Resampled(2, 2, CanvasResampling.Area).Document;
            NormalMaps.Decode(r.Layers[0].GetPixel(PaintChannel.Normal, 0, 0), out var nx, out var ny, out var nz);
            var raw = r.Layers[0].GetPixel(PaintChannel.Normal, 0, 0);
            double length = Math.Sqrt(Math.Pow(raw.R / 127.5 - 1, 2) + Math.Pow(raw.G / 127.5 - 1, 2) + Math.Pow(raw.B / 127.5 - 1, 2));
            Assert.That(length, Is.EqualTo(1).Within(.01), "the stored average is a unit vector again");
            Assert.That(nx, Is.GreaterThan(.3).And.LessThan(.75)); Assert.That(ny, Is.EqualTo(0).Within(.01));
            Assert.That(r.Layers[0].GetPixel(PaintChannel.Normal, 1, 1), Is.EqualTo(new Rgba32(200, 200, 200)), "four equal pixels give that pixel, not a renormalized one");
        }

        // ───────── 写すもの ─────────

        [Test] public void EveryLayerKindChannelMaskAndTheSelectionAreCarriedAndTheOriginalIsUntouched()
        {
            var d = new PaintDocument(32, 16, 8);
            var paint = d.AddLayer("Paint"); d.SetChannelEnabled(paint.Id, PaintChannel.Roughness, true); d.SetChannelEnabled(paint.Id, PaintChannel.Height, true); d.SetChannelEnabled(paint.Id, PaintChannel.Height, false);
            Paint(d, paint.GetChannel(PaintChannel.Color), Pattern);
            paint.GetChannel(PaintChannel.Roughness).SetPixel(3, 3, new Rgba32(90, 90, 90));
            paint.GetChannel(PaintChannel.Height).SetPixel(5, 5, new Rgba32(40, 40, 40)); // 無効にしたチャンネルの画素も残る
            d.AddLayerMask(paint.Id); paint.Mask.Surface.SetPixel(1, 1, new Rgba32(0, 0, 0, 200));
            d.SetLayerMaskInverted(paint.Id, true); d.SetLayerMaskDensity(paint.Id, .5);
            d.SetLayerOpacity(paint.Id, .7); d.SetLayerBlendMode(paint.Id, LayerBlendMode.Multiply);
            var fill = d.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(9, 8, 7) }, { PaintChannel.Metallic, new Rgba32(5, 5, 5) } });
            d.SetChannelEnabled(fill.Id, PaintChannel.Metallic, false); d.SetLayerVisibility(fill.Id, false); d.SetLayerClipping(fill.Id, true);
            var group = d.AddGroup("Group"); var adjustment = d.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels(.1, .9), new[] { PaintChannel.Color });
            d.MoveLayerTo(adjustment.Id, group.Id, 0);
            d.SetSelection(SelectionMask.Rectangle(d, 4, 4, 12, 10));
            d.ClearHistory();
            var before = DocumentBinary.Write(d); long revision = d.Revision; var selection = d.Selection;

            var result = d.Resampled(64, 32, CanvasResampling.Nearest); var r = result.Document;
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before), "the original document is not changed"); Assert.That(d.Selection, Is.SameAs(selection));
            Assert.That((r.Width, r.Height, r.TileSize, r.Id), Is.EqualTo((64, 32, 8, d.Id)));
            Assert.That(r.Layers.Select(l => (l.Id, l.Name, l.Kind, l.ParentId, l.Visible, l.Opacity, l.BlendMode, l.Clipping)), Is.EqualTo(d.Layers.Select(l => (l.Id, l.Name, l.Kind, l.ParentId, l.Visible, l.Opacity, l.BlendMode, l.Clipping))));
            foreach (var pair in d.Layers.Zip(r.Layers, (a, b) => (a, b)))
            {
                Assert.That(pair.b.EnabledChannels, Is.EqualTo(pair.a.EnabledChannels), pair.a.Name);
                Assert.That(pair.b.Channels.Keys.OrderBy(c => c), Is.EqualTo(pair.a.Channels.Keys.OrderBy(c => c)), pair.a.Name);
                Assert.That(pair.b.FillValues, Is.EquivalentTo(pair.a.FillValues), pair.a.Name);
            }
            Assert.That(r.Layers.Single(l => l.Id == adjustment.Id).Adjustment, Is.EqualTo(adjustment.Adjustment));
            var p = r.Layers[0];
            Assert.That(p.GetPixel(PaintChannel.Color, 9, 5), Is.EqualTo(Pattern(4, 2)));
            Assert.That(p.GetPixel(PaintChannel.Roughness, 7, 7), Is.EqualTo(new Rgba32(90, 90, 90))); Assert.That(p.GetPixel(PaintChannel.Roughness, 8, 7), Is.EqualTo(Rgba32.Transparent));
            Assert.That(p.GetPixel(PaintChannel.Height, 11, 11), Is.EqualTo(new Rgba32(40, 40, 40)));
            Assert.That((p.Mask.Enabled, p.Mask.Inverted, p.Mask.Density), Is.EqualTo((true, true, .5)));
            Assert.That(p.Mask.Surface.GetPixel(3, 3), Is.EqualTo(new Rgba32(0, 0, 0, 200))); Assert.That(p.Mask.Surface.GetPixel(4, 3), Is.EqualTo(Rgba32.Transparent));
            Assert.That(r.Selection[8, 8], Is.EqualTo(255)); Assert.That(r.Selection[23, 19], Is.EqualTo(255)); Assert.That(r.Selection[24, 19], Is.EqualTo(0)); Assert.That(r.Selection[7, 8], Is.EqualTo(0));
            Assert.That(r.CanUndo || r.CanRedo, Is.False, "the copy has no history"); Assert.That(r.Revision, Is.GreaterThan(revision));
            Assert.That(r.NormalSettings, Is.EqualTo(d.NormalSettings.WithStrength(8)), "the Height → Normal strength (slope per texel) doubles with the size");
            Assert.That(result.Notes, Is.Empty); Assert.That(result.SurfacePathLayers, Is.Empty);
            Assert.That(r.Composite(PaintChannel.Color), Is.EqualTo(Doc2x(d.Composite(PaintChannel.Color), 32, 16)), "the composite of an exact 2× copy is the 2× composite");
            var reread = DocumentBinary.Read(DocumentBinary.Write(r));
            Assert.That(DocumentBinary.Write(reread), Is.EqualTo(DocumentBinary.Write(r)), "the copy saves and reads back byte for byte");
        }
        static byte[] Doc2x(byte[] rgba, int w, int h)
        {
            var result = new byte[rgba.Length * 4];
            for (int y = 0; y < h * 2; y++) for (int x = 0; x < w * 2; x++) Array.Copy(rgba, ((y / 2) * w + x / 2) * 4, result, (y * w * 2 + x) * 4, 4);
            return result;
        }

        [Test] public void FilterRadiiAndNormalStrengthFollowTheScaleWithinTheirLimits()
        {
            var d = Doc(64, 64, out var layer); d.AddLayerMask(layer.Id);
            var blur = d.AddFilter(layer.Id, FilterTarget.Content, FilterSettings.GaussianBlur(3), new[] { PaintChannel.Color }, enabled: false, strength: .4);
            var sharpen = d.AddFilter(layer.Id, FilterTarget.Content, FilterSettings.Sharpen(40, 1.5, 3));
            var noise = d.AddFilter(layer.Id, FilterTarget.Content, FilterSettings.Noise(.3, 7));
            var levels = d.AddFilter(layer.Id, FilterTarget.Content, FilterSettings.Levels(.1, .8));
            var maskBlur = d.AddFilter(layer.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(200));
            d.SetNormalSettings(d.NormalSettings.WithDerive(true).WithStrength(200));
            d.ClearHistory();

            var up = d.Resampled(128, 128, CanvasResampling.Bilinear); var u = up.Document.Layers[0];
            Assert.That(u.Filters.Select(f => (f.Id, f.Enabled, f.Strength)), Is.EqualTo(layer.Filters.Select(f => (f.Id, f.Enabled, f.Strength))));
            Assert.That(u.Filters[0].Channels, Is.EqualTo(new[] { PaintChannel.Color }));
            Assert.That(u.Filters.Select(f => f.Settings.Radius), Is.EqualTo(new[] { 6, 64, 0, 0 }), "blur 3 → 6, sharpen 40 → 64 (the largest)");
            Assert.That(u.Filters[1].Settings.Amount, Is.EqualTo(1.5)); Assert.That(u.Filters[1].Settings.Threshold, Is.EqualTo(3));
            Assert.That(u.Filters[2].Settings, Is.EqualTo(noise.Settings), "noise keeps its seed"); Assert.That(u.Filters[3].Settings, Is.EqualTo(levels.Settings));
            Assert.That(u.Mask.Filters.Single().Id, Is.EqualTo(maskBlur.Id)); Assert.That(u.Mask.Filters.Single().Settings.Radius, Is.EqualTo(256));
            Assert.That(up.Document.NormalSettings.Strength, Is.EqualTo(256)); Assert.That(up.Document.NormalSettings.DeriveFromHeight, Is.True);
            Assert.That(up.Notes.Count, Is.EqualTo(3), string.Join("\n", up.Notes));
            string joined = string.Join("\n", up.Notes);
            Assert.That(up.Notes.Any(n => n.Contains("Sharpen") && n.Contains("80 px")), Is.True, joined);
            Assert.That(up.Notes.Any(n => n.Contains("(mask)") && n.Contains("400 px")), Is.True, joined);
            Assert.That(up.Notes.Any(n => n.Contains("Height → Normal") && n.Contains("400")), Is.True, joined);

            var down = d.Resampled(16, 16, CanvasResampling.Area); var w = down.Document.Layers[0];
            Assert.That(w.Filters.Select(f => f.Settings.Radius), Is.EqualTo(new[] { 1, 10, 0, 0 }), "3/4 = 0.75 → 1, 40/4 = 10");
            Assert.That(w.Mask.Filters.Single().Settings.Radius, Is.EqualTo(50)); Assert.That(down.Document.NormalSettings.Strength, Is.EqualTo(50));
            Assert.That(down.Notes, Is.Empty);
            Assert.That(blur.Settings.Radius, Is.EqualTo(3), "the original's filters stay");

            // 縮尺した半径の合計が上限（512）を超えるなら、何も作らずに断る
            var e = Doc(64, 64, out var stacked);
            for (int i = 0; i < 3; i++) e.AddFilter(stacked.Id, FilterTarget.Content, FilterSettings.GaussianBlur(150), new[] { PaintChannel.Color });
            var bytes = DocumentBinary.Write(e);
            Assert.That(() => e.Resampled(128, 128, CanvasResampling.Bilinear), Throws.InvalidOperationException.With.Message.Contains("'L'").And.Message.Contains("512").And.Message.Contains("Nothing was changed"));
            Assert.That(DocumentBinary.Write(e), Is.EqualTo(bytes));
        }

        [Test] public void ACanvasPathIsScaledAndDrawnAgainAndASurfacePathIsListedForTheCaller()
        {
            var d = Doc(64, 64, out var layer);
            var brush = new PathBrush { RadiusWorld = 3, Color = new Rgba32(10, 200, 30) };
            var path = new CanvasPath(Guid.NewGuid(), PaintChannel.Color, brush, new[] { new CanvasPoint(8, 8), new CanvasPoint(40, 20, .5), new CanvasPoint(50, 50) });
            d.SetCanvasPath(layer.Id, path);
            var model = d.AddLayer("Model"); var surface = new SurfacePath(Guid.NewGuid(), PaintChannel.Color, "fingerprint", new PathBrush(), new[] { new PathPoint(0, .2, .3) });
            var drawn = new PaintDocument(64, 64, 8).AddLayer("x").GetChannel(PaintChannel.Color); drawn.SetPixel(10, 10, new Rgba32(1, 2, 3));
            d.SetPath(model.Id, surface, drawn); d.ClearHistory();

            var result = d.Resampled(128, 128, CanvasResampling.Bilinear); var r = result.Document;
            var scaled = (CanvasPath)r.Layers[0].Path;
            Assert.That(scaled.Id, Is.EqualTo(path.Id)); Assert.That(scaled.Brush.RadiusWorld, Is.EqualTo(6));
            Assert.That(scaled.Points.Select(q => (q.X, q.Y, q.Pressure)), Is.EqualTo(new[] { (16.0, 16.0, 1.0), (80.0, 40.0, .5), (100.0, 100.0, 1.0) }));
            var expected = CanvasPathRenderer.Render(r, scaled);
            Assert.That(Tiles(r.Layers[0].Channels[PaintChannel.Color]), Is.EqualTo(Tiles(expected)), "the path channel is the scaled path drawn at the new size");
            Assert.That(expected.TileCount, Is.GreaterThan(0));
            Assert.That(r.Layers[1].Path, Is.SameAs(surface), "a path on the model is UV-bound and kept");
            Assert.That(result.SurfacePathLayers, Is.EqualTo(new[] { model.Id }));
            Assert.That(r.Layers[1].GetPixel(PaintChannel.Color, 21, 21).A, Is.GreaterThan(0), "its pixels are resampled until the caller draws it again");
            Assert.That(path.Brush.RadiusWorld, Is.EqualTo(3)); Assert.That(((CanvasPath)d.Layers[0].Path).Points[1].X, Is.EqualTo(40));

            // 描き直しが 1 回の操作の予算を超えるなら、パスの層の名前を添えて断る（元は変わらない）
            var bytes = DocumentBinary.Write(d); d.ActiveStrokeBudgetBytes = 1024;
            Assert.That(() => d.Resampled(128, 128, CanvasResampling.Bilinear), Throws.InvalidOperationException.With.Message.Contains("The path on 'L' cannot be drawn at 128×128").And.Message.Contains("Nothing was changed"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes));
        }

        // ───────── 断る ─────────

        [Test] public void GoingOverTheLayerPixelBudgetRefusesAndLeavesTheDocumentAsItWas()
        {
            var d = Doc(64, 64, out var layer); var random = new Random(3);
            Paint(d, Color(layer), (x, y) => new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            d.AddLayer("Top"); d.SetLayerOpacity(d.Layers[1].Id, .5);
            var bytes = DocumentBinary.Write(d); int undo = d.UndoCount; long revision = d.Revision;
            Assert.That(() => d.Resampled(128, 128, CanvasResampling.Bilinear, sourceBudgetBytes: 128 * 128 * 4 - 1),
                Throws.InvalidOperationException.With.Message.Contains("budget").And.Message.Contains("Nothing was changed"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes)); Assert.That((d.UndoCount, d.Revision), Is.EqualTo((undo, revision)));
            Assert.That(d.Resampled(128, 128, CanvasResampling.Bilinear, sourceBudgetBytes: 128 * 128 * 4).Document.AllocatedBytes, Is.EqualTo(128 * 128 * 4), "exactly the budget fits");
            Assert.That(() => d.Resampled(128, 128, CanvasResampling.Bilinear, sourceBudgetBytes: -1), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test] public void InvalidSizesMethodsAndAnActiveStrokeAreRefused()
        {
            var d = Doc(16, 16, out var layer);
            foreach (var (w, h) in new[] { (0, 16), (16, 0), (PaintDocument.MaxNativeSide + 1, 16), (16, -5) })
                Assert.That(() => d.Resampled(w, h, CanvasResampling.Area), Throws.TypeOf<ArgumentOutOfRangeException>(), w + "×" + h);
            Assert.That(() => d.Resampled(32, 32, (CanvasResampling)7), Throws.TypeOf<ArgumentOutOfRangeException>());
            using (var stroke = d.BeginStroke(layer.Id, PaintChannel.Color, new BrushSettings { Radius = 2 }))
            {
                stroke.Add(new BrushSample(4, 4, 1));
                Assert.That(() => d.Resampled(32, 32, CanvasResampling.Area), Throws.InvalidOperationException);
                stroke.Cancel();
            }
        }

        // ───────── 保存・並列 ─────────

        [Test] public void OddSizesKeepZeroPaddingAnd8192SavesAndReadsBack()
        {
            var d = Doc(30, 20, out var layer); Paint(d, Color(layer), Pattern);
            foreach (var method in Methods)
                foreach (var (w, h) in new[] { (45, 10), (13, 37), (30, 20) })
                {
                    var r = d.Resampled(w, h, method).Document;
                    Assert.That(DocumentBinary.Write(DocumentBinary.Read(DocumentBinary.Write(r))), Is.EqualTo(DocumentBinary.Write(r)), method + " " + w + "×" + h + " (Read refuses non-zero edge padding)");
                }
            var big = Doc(512, 512, out var corner, tile: 128); Color(corner).SetPixel(0, 0, new Rgba32(1, 2, 3)); Color(corner).SetPixel(511, 511, new Rgba32(4, 5, 6, 7)); big.ClearHistory();
            var huge = big.Resampled(PaintDocument.MaxNativeSide, PaintDocument.MaxNativeSide, CanvasResampling.Nearest).Document;
            var read = DocumentBinary.Read(DocumentBinary.Write(huge));
            Assert.That((read.Width, read.Height), Is.EqualTo((8192, 8192)));
            Assert.That(read.Layers[0].GetPixel(PaintChannel.Color, 15, 15), Is.EqualTo(new Rgba32(1, 2, 3))); Assert.That(read.Layers[0].GetPixel(PaintChannel.Color, 16, 16), Is.EqualTo(Rgba32.Transparent));
            Assert.That(read.Layers[0].GetPixel(PaintChannel.Color, 8176, 8176), Is.EqualTo(new Rgba32(4, 5, 6, 7)));
        }

        [Test] public void TheBytesDoNotDependOnTheNumberOfThreads()
        {
            var d = Doc(96, 64, out var layer, tile: 16); Paint(d, Color(layer), Pattern);
            d.SetChannelEnabled(layer.Id, PaintChannel.Normal, true); Paint(d, layer.GetChannel(PaintChannel.Normal), (x, y) => NormalMaps.Encode(Math.Sin(x * .3), Math.Cos(y * .2), 1, (byte)(x * 2)));
            int saved = CoreParallelism.MaxDegreeOfParallelism;
            try
            {
                foreach (var method in Methods)
                {
                    var outputs = new List<byte[]>();
                    foreach (int degree in new[] { 1, 3, 0 }) { CoreParallelism.MaxDegreeOfParallelism = degree; outputs.Add(DocumentBinary.Write(d.Resampled(150, 40, method).Document)); }
                    Assert.That(outputs[1], Is.EqualTo(outputs[0]), method + ": 3 threads"); Assert.That(outputs[2], Is.EqualTo(outputs[0]), method + ": default");
                }
            }
            finally { CoreParallelism.MaxDegreeOfParallelism = saved; }
        }
    }
}
