using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>非破壊フィルター（Core）: 小さなカーネルの手計算の値、タイル・ブロックの評価と一括評価のバイト一致（タイル境界をまたぐ halo）、
    /// 変更追跡の halo、値の型とチャンネルの拒否、作業メモリと halo の予算、Undo/Redo とスライダーのまとめ、焼き込み、ネイティブ版 9 の
    /// 保存と版 8 の読み込み、知らない種類・版の拒否、PSD 書き出しの拒否、正本を変えないこと。</summary>
    public sealed class FilterTests
    {
        static PaintDocument Doc(int w = 64, int h = 48, int tile = 16) { var d = new PaintDocument(w, h, tile); return d; }

        static PaintLayer Random(PaintDocument d, int seed, string name = "R", PaintChannel channel = PaintChannel.Color)
        {
            var l = d.AddLayer(name); var s = l.GetChannel(channel); var rnd = new Random(seed);
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++)
                if (rnd.Next(4) > 0) s.SetPixel(x, y, new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
            d.ClearHistory(); return l;
        }

        /// <summary>全タイルを CopyOutputTile で集めた画像。</summary>
        static byte[] Tiled(PaintLayer l, PaintDocument d, PaintChannel channel = PaintChannel.Color)
        {
            var result = new byte[d.Width * d.Height * 4]; var tile = new byte[d.TileSize * d.TileSize * 4];
            foreach (var c in d.EnumerateCanvasTiles())
            {
                l.CopyOutputTile(channel, c, tile);
                for (int y = 0; y < d.TileSize && c.Y * d.TileSize + y < d.Height; y++)
                    for (int x = 0; x < d.TileSize && c.X * d.TileSize + x < d.Width; x++)
                        for (int k = 0; k < 4; k++) result[((c.Y * d.TileSize + y) * d.Width + c.X * d.TileSize + x) * 4 + k] = tile[(y * d.TileSize + x) * 4 + k];
            }
            return result;
        }

        // ───────────── 手計算 ─────────────

        [Test] public void BlurRadiusOneIsAThreeByThreeBox()
        {
            var d = Doc(32, 32); var l = d.AddLayer("dot"); l.GetChannel(PaintChannel.Color).SetPixel(10, 10, new Rgba32(255, 255, 255, 255)); d.ClearHistory();
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(1), new[] { PaintChannel.Color });
            // 前乗算 65025 → 横 (65025 + 1) / 3 = 21675 → 縦 (21675 + 1) / 3 = 7225、アルファ (7225 + 127) / 255 = 28、色 255
            for (int y = 8; y <= 12; y++) for (int x = 8; x <= 12; x++)
            {
                bool inside = Math.Abs(x - 10) <= 1 && Math.Abs(y - 10) <= 1;
                Assert.That(l.GetOutputPixel(PaintChannel.Color, x, y), Is.EqualTo(inside ? new Rgba32(255, 255, 255, 28) : Rgba32.Transparent), x + "," + y);
            }
            Assert.That(l.GetPixel(PaintChannel.Color, 10, 10), Is.EqualTo(new Rgba32(255, 255, 255, 255)), "the source is unchanged");
            Assert.That(d.CompositePixel(PaintChannel.Color, 11, 11), Is.EqualTo(new Rgba32(255, 255, 255, 28)));
        }

        [Test] public void BlurRadiusTwoIsATriangleAndKeepsHiddenColourWhereNothingSpreads()
        {
            var d = Doc(32, 32); var l = d.AddLayer("dot"); var s = l.GetChannel(PaintChannel.Color);
            s.SetPixel(10, 10, new Rgba32(255, 0, 0, 255)); s.SetPixel(25, 25, new Rgba32(7, 8, 9, 0)); d.ClearHistory();
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(2), new[] { PaintChannel.Color });
            // 2 回の半径 1 の箱: 1 次元の重み 1,2,3,2,1 / 9。中心 (65025·3/9 の 2 次元) = 横 21675 → 横 (0+21675+21675... は丸めを順に追う
            // 1 回目: 横 21675 (x 9..11)、縦 7225 (9..11 × 9..11)。2 回目: 中心 横 (7225·3 + 1) / 3 = 7225、縦 7225 → a = 28
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 10, 10), Is.EqualTo(new Rgba32(255, 0, 0, 28)));
            // 角 (12, 12): 横 (7225 + 1) / 3 = 2408、縦 (2408 + 1) / 3 = 803 → a = (803 + 127) / 255 = 3
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 12, 12), Is.EqualTo(new Rgba32(255, 0, 0, 3)));
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 13, 10).A, Is.Zero, "the blur reaches exactly its radius");
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 25, 25), Is.EqualTo(new Rgba32(7, 8, 9, 0)), "a transparent pixel nothing spreads into keeps its colour");
        }

        [Test] public void SharpenMatchesTheHandComputedUnsharpMask()
        {
            var d = Doc(32, 32); var l = d.AddLayer("grey"); var s = l.GetChannel(PaintChannel.Color);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) s.SetPixel(x, y, new Rgba32(100, 100, 100, 255));
            s.SetPixel(10, 10, new Rgba32(200, 200, 200, 255)); d.ClearHistory();
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Sharpen(1, 1, 0), new[] { PaintChannel.Color });
            // 中心のぼかし: 横 (100+200+100)·255 = 102000 → 34000、縦 (25500 + 34000 + 25500 + 1) / 3 = 28333 → 28333·255/65025 = 111.11
            // 中心 200 + (200 − 111.11) = 288.9 → 255。周りの 8 画素 100 + (100 − 111.11) = 88.89 → 89。
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 10, 10), Is.EqualTo(new Rgba32(255, 255, 255, 255)));
            foreach (var (x, y) in new[] { (11, 10), (9, 9), (11, 11), (10, 9) }) Assert.That(l.GetOutputPixel(PaintChannel.Color, x, y), Is.EqualTo(new Rgba32(89, 89, 89, 255)), x + "," + y);
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 12, 10), Is.EqualTo(new Rgba32(100, 100, 100, 255)));
            d.SetFilterSettings(l.Id, l.Filters[0].Id, FilterSettings.Sharpen(1, 1, 12));
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 11, 10), Is.EqualTo(new Rgba32(100, 100, 100, 255)), "a difference of 11.1 levels is below the threshold of 12");
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 10, 10).R, Is.EqualTo(255));
        }

        [Test] public void PointFiltersUseTheirFormulasAndKeepAlpha()
        {
            var d = Doc(16, 16, 16); var l = d.AddLayer("p"); var s = l.GetChannel(PaintChannel.Color);
            s.SetPixel(1, 1, new Rgba32(10, 128, 250, 77)); d.ClearHistory();
            var inv = d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Invert(), new[] { PaintChannel.Color });
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(245, 127, 5, 77)));
            d.SetFilterStrength(l.Id, inv.Id, .5);
            // 同じアルファは色だけを線形に混ぜる: 10 + (245 − 10)·0.5 = 127.5 → 128
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(128, 128, 128, 77)));
            d.RemoveFilter(l.Id, inv.Id);
            var levels = FilterSettings.Levels(.1, .9, 1.5, .05, .95);
            d.AddFilter(l.Id, FilterTarget.Content, levels, new[] { PaintChannel.Color });
            var expected = AdjustmentSettings.Levels(.1, .9, 1.5, .05, .95).Apply(new Rgba32(10, 128, 250, 77));
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 1, 1), Is.EqualTo(expected), "the same formula as the Levels adjustment");
        }

        [Test] public void NoiseIsDeterministicPerSeedAndPosition()
        {
            var d = Doc(32, 32, 16); var l = d.AddLayer("g"); var s = l.GetChannel(PaintChannel.Color);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) s.SetPixel(x, y, new Rgba32(128, 128, 128, 200));
            d.ClearHistory();
            var n = d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Noise(.5, 7, true), new[] { PaintChannel.Color });
            var a = l.EvaluateOutputRegion(PaintChannel.Color, 0, 0, 32, 32);
            Assert.That(Tiled(l, d), Is.EqualTo(a));
            for (int i = 0; i < a.Length; i += 4) { Assert.That(a[i], Is.EqualTo(a[i + 1])); Assert.That(a[i], Is.EqualTo(a[i + 2])); Assert.That(a[i + 3], Is.EqualTo(200)); Assert.That(Math.Abs(a[i] - 128), Is.LessThanOrEqualTo(64)); }
            Assert.That(a.Where((v, i) => i % 4 == 0).Distinct().Count(), Is.GreaterThan(20), "it is noise");
            d.SetFilterSettings(l.Id, n.Id, FilterSettings.Noise(.5, 8, true));
            Assert.That(l.EvaluateOutputRegion(PaintChannel.Color, 0, 0, 32, 32), Is.Not.EqualTo(a), "another seed");
            d.SetFilterSettings(l.Id, n.Id, FilterSettings.Noise(.5, 7, true));
            Assert.That(l.EvaluateOutputRegion(PaintChannel.Color, 0, 0, 32, 32), Is.EqualTo(a), "the same seed gives the same noise");
            d.SetFilterSettings(l.Id, n.Id, FilterSettings.Noise(0, 7, true));
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 5, 5), Is.EqualTo(new Rgba32(128, 128, 128, 200)));
        }

        [Test] public void NormalizeStretchesTheWholeLayer()
        {
            var d = Doc(32, 32, 16); var l = d.AddLayer("n"); var s = l.GetChannel(PaintChannel.Color);
            s.SetPixel(1, 1, new Rgba32(50, 60, 70, 255)); s.SetPixel(30, 30, new Rgba32(150, 100, 90, 128)); s.SetPixel(20, 2, new Rgba32(0, 0, 0, 0)); d.ClearHistory();
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Normalize(), new[] { PaintChannel.Color });
            // 最小 50・最大 150（アルファ 0 の画素は数えない）→ (v − 50) / 100
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(0, 26, 51, 255)));
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 30, 30), Is.EqualTo(new Rgba32(255, 128, 102, 128)));
            Assert.That(l.Filters[0].Settings.Locality, Is.EqualTo(FilterLocality.Global));
        }

        // ───────────── タイルとブロック ─────────────

        [Test] public void TilesBlocksAndOnePieceEvaluationAreByteIdenticalAcrossTileBorders()
        {
            var d = Doc(70, 54, 16); var l = Random(d, 11);
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(9), new[] { PaintChannel.Color });
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Sharpen(5, 2, 3), new[] { PaintChannel.Color });
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Noise(.3, 5, false), new[] { PaintChannel.Color });
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Normalize(), new[] { PaintChannel.Color });
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(23), new[] { PaintChannel.Color }, strength: .6);
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Levels(.2, .8, .7, .1, 1), new[] { PaintChannel.Color });
            var whole = l.EvaluateOutputRegion(PaintChannel.Color, 0, 0, 70, 54);
            foreach (int block in new[] { 16, 32, 512 })
            {
                d.FilterBlockPixels = block; d.FilterCacheBudgetBytes = 0; d.FilterCacheBudgetBytes = PaintDocument.DefaultFilterCacheBudgetBytes;
                Assert.That(Tiled(l, d), Is.EqualTo(whole), "blocks of " + block);
            }
            // 部分領域（タイルの途中から）も同じ
            var part = l.EvaluateOutputRegion(PaintChannel.Color, 13, 7, 30, 40);
            for (int y = 0; y < 40; y++) for (int x = 0; x < 30; x++) for (int k = 0; k < 4; k++)
                Assert.That(part[(y * 30 + x) * 4 + k], Is.EqualTo(whole[((y + 7) * 70 + x + 13) * 4 + k]));
            // 合成もタイル単位と画素単位で一致し、ぼかしで広がったアルファも出る
            var composite = d.Composite(PaintChannel.Color);
            Assert.That(d.CompositePixel(PaintChannel.Color, 33, 17), Is.EqualTo(new Rgba32(composite[(17 * 70 + 33) * 4], composite[(17 * 70 + 33) * 4 + 1], composite[(17 * 70 + 33) * 4 + 2], composite[(17 * 70 + 33) * 4 + 3])));
            Assert.That(d.FilterEvaluatedBlocks, Is.GreaterThan(0));
        }

        [Test] public void BlurSpreadsIntoEmptyTilesAndTheCompositorsSeeIt()
        {
            var d = Doc(64, 64, 16); var l = d.AddLayer("dot"); l.GetChannel(PaintChannel.Color).SetPixel(15, 15, new Rgba32(0, 0, 255, 255)); d.ClearHistory();
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(6));
            Assert.That(l.EnumerateContentTiles(PaintChannel.Color), Is.EquivalentTo(new[] { new TileCoord(0, 0), new TileCoord(1, 0), new TileCoord(0, 1), new TileCoord(1, 1) }));
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 18, 18).A, Is.GreaterThan(0), "the blur crosses into the next (empty) tile");
            var composite = d.Composite(PaintChannel.Color);
            Assert.That(composite[(18 * 64 + 18) * 4 + 3], Is.EqualTo(l.GetOutputPixel(PaintChannel.Color, 18, 18).A));
        }

        [Test] public void MaskFiltersFeatherAndInvertTheHideAmount()
        {
            var d = Doc(48, 48, 16); var l = d.AddLayer("red"); var s = l.GetChannel(PaintChannel.Color);
            for (int y = 0; y < 48; y++) for (int x = 0; x < 48; x++) s.SetPixel(x, y, new Rgba32(255, 0, 0, 255));
            var mask = d.AddLayerMask(l.Id); mask.Surface.SetPixel(20, 20, new Rgba32(0, 0, 0, 255)); d.ClearHistory();
            var blur = d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(1));
            Assert.That(blur.Channels, Is.Empty);
            Assert.That(mask.OutputHideAt(21, 21), Is.EqualTo(28), "the 3 × 3 box of a full hide (255 / 9 = 28.3)");
            Assert.That(d.CompositePixel(PaintChannel.Color, 21, 21).A, Is.EqualTo(MathUtilByte(1 - 28 / 255.0)));
            var tile = new byte[16 * 16 * 4]; mask.CopyOutputTile(new TileCoord(1, 1), tile);
            Assert.That(tile[((21 - 16) * 16 + 5) * 4 + 3], Is.EqualTo(28)); Assert.That(tile[((21 - 16) * 16 + 5) * 4], Is.Zero, "masks keep RGB zero");
            d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.Invert());
            Assert.That(mask.IsNeutral, Is.False);
            Assert.That(d.CompositePixel(PaintChannel.Color, 40, 40).A, Is.Zero, "an inverted empty mask hides everything");
            Assert.That(mask.Surface.GetPixel(20, 20).A, Is.EqualTo(255), "the painted mask is unchanged");
        }
        static byte MathUtilByte(double v) => (byte)Math.Max(0, Math.Min(255, Math.Floor(v * 255 + .5)));

        [Test] public void FillLayersCanBeFilteredWithoutAllocatingPixels()
        {
            var d = Doc(32, 32, 16);
            var f = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Roughness, new Rgba32(128, 128, 128, 255) } });
            d.AddFilter(f.Id, FilterTarget.Content, FilterSettings.Noise(.4, 3, true), new[] { PaintChannel.Roughness });
            var r = d.Composite(PaintChannel.Roughness);
            Assert.That(r.Where((v, i) => i % 4 == 0).Distinct().Count(), Is.GreaterThan(10));
            Assert.That(d.AllocatedBytes, Is.Zero);
            Assert.That(() => d.BakeFilters(f.Id), Throws.InvalidOperationException.With.Message.Contains("no pixels"));
        }

        // ───────────── 変更追跡 ─────────────

        static HashSet<TileCoord> Changed(PaintDocument d, long since, PaintChannel channel = PaintChannel.Color)
        { var set = new HashSet<TileCoord>(); Assert.That(d.TryGetChangedTiles(channel, since, set), Is.True); return set; }

        [Test] public void ASourceChangeReportsEveryTileWithinTheHalo()
        {
            var d = Doc(128, 128, 16); var plain = Random(d, 1, "plain"); var l = Random(d, 2, "blurred");
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(20));
            long since = d.ChangeSerial;
            plain.GetChannel(PaintChannel.Color).SetPixel(56, 56, new Rgba32(1, 2, 3, 4));
            Assert.That(Changed(d, since), Is.EquivalentTo(new[] { new TileCoord(3, 3) }), "an unfiltered layer reports its own tile");
            since = d.ChangeSerial;
            l.GetChannel(PaintChannel.Color).SetPixel(56, 56, new Rgba32(1, 2, 3, 4));
            var changed = Changed(d, since);
            Assert.That(changed.Count, Is.EqualTo(25), "a radius of 20 reaches 2 tiles of 16 on each side");
            Assert.That(changed.Contains(new TileCoord(1, 1)) && changed.Contains(new TileCoord(5, 5)), Is.True);
            Assert.That(Changed(d, since, PaintChannel.Roughness).Count, Is.Zero, "other channels are not affected");
            // 全体の統計を使うフィルター: どこを変えても出力の全タイル
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Normalize());
            since = d.ChangeSerial;
            l.GetChannel(PaintChannel.Color).SetPixel(1, 1, new Rgba32(9, 9, 9, 9));
            Assert.That(Changed(d, since), Is.SupersetOf(l.EnumerateContentTiles(PaintChannel.Color)));
            Assert.That(Changed(d, since).Count, Is.EqualTo(64));
        }

        [Test] public void FilterEditsMarkTheGrownRegionAndIncrementalDisplayMatchesTheReference()
        {
            var d = Doc(96, 96, 16); Random(d, 3, "below"); var l = d.AddLayer("dots"); var s = l.GetChannel(PaintChannel.Color);
            s.SetPixel(40, 40, new Rgba32(255, 255, 0, 255)); d.ClearHistory();
            var compositor = new TileGpuCompositor(allowGpu: false);
            try
            {
                compositor.Update(d, PaintChannel.Color);
                long since = d.ChangeSerial;
                var blur = d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(17));
                Assert.That(Changed(d, since), Is.SupersetOf(l.EnumerateContentTiles(PaintChannel.Color)));
                compositor.Update(d, PaintChannel.Color);
                Assert.That(GpuTests.ReadCpu(compositor.Texture), Is.EqualTo(d.Composite(PaintChannel.Color)), "after adding");
                var stroke = d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings { Radius = 2, Color = new Rgba32(0, 255, 255, 255) });
                stroke.Add(new BrushSample(63.5, 63.5)); stroke.Add(new BrushSample(64.5, 66, 1, 1)); stroke.Commit();
                compositor.Update(d, PaintChannel.Color);
                Assert.That(GpuTests.ReadCpu(compositor.Texture), Is.EqualTo(d.Composite(PaintChannel.Color)), "after a stroke at a tile corner");
                d.SetFilterSettings(l.Id, blur.Id, FilterSettings.GaussianBlur(3), coalesce: true);
                compositor.Update(d, PaintChannel.Color);
                Assert.That(GpuTests.ReadCpu(compositor.Texture), Is.EqualTo(d.Composite(PaintChannel.Color)), "after shrinking the radius");
                d.Undo(); d.Undo();
                compositor.Update(d, PaintChannel.Color);
                Assert.That(GpuTests.ReadCpu(compositor.Texture), Is.EqualTo(d.Composite(PaintChannel.Color)), "after undo");
                d.AddLayerMask(l.Id); d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.Noise(.9, 2, true));
                compositor.Update(d, PaintChannel.Color);
                Assert.That(GpuTests.ReadCpu(compositor.Texture), Is.EqualTo(d.Composite(PaintChannel.Color)), "after a mask filter");
            }
            finally { compositor.Dispose(); }
        }

        // ───────────── 型・予算の拒否 ─────────────

        [Test] public void FiltersAreRefusedForChannelsWhoseValueTypeTheyDoNotAccept()
        {
            var d = Doc(); var l = d.AddLayer("l");
            Assert.That(() => d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Sharpen(), new[] { PaintChannel.Normal }), Throws.InvalidOperationException.With.Message.Contains("normals"));
            Assert.That(() => d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Levels(), new[] { PaintChannel.Normal }), Throws.InvalidOperationException);
            Assert.That(() => d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Noise(.2, 0, false), new[] { PaintChannel.Roughness }), Throws.InvalidOperationException.With.Message.Contains("monochrome"));
            Assert.That(() => d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.Invert()), Throws.InvalidOperationException.With.Message.Contains("no mask"));
            d.AddLayerMask(l.Id);
            Assert.That(() => d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.Noise(.2, 0, false)), Throws.InvalidOperationException);
            Assert.That(() => d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.Invert(), new[] { PaintChannel.Color }), Throws.ArgumentException);
            var adj = d.AddAdjustmentLayer("adj", AdjustmentSettings.Invert()); var g = d.AddGroup("g");
            Assert.That(() => d.AddFilter(adj.Id, FilterTarget.Content, FilterSettings.Invert()), Throws.InvalidOperationException);
            Assert.That(() => d.AddFilter(g.Id, FilterTarget.Content, FilterSettings.Invert()), Throws.InvalidOperationException);
            Assert.That(d.UndoCount, Is.EqualTo(4), "refusals leave no history (only the layer, mask, adjustment and group)");
            // 既定のチャンネル: 受け付けるものすべて。色のノイズは Color と Emission だけ
            var colourNoise = d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Noise(.2, 0, false));
            Assert.That(colourNoise.Channels, Is.EqualTo(new[] { PaintChannel.Color, PaintChannel.Emission }));
            Assert.That(() => d.SetFilterChannels(l.Id, colourNoise.Id, new[] { PaintChannel.Height }), Throws.InvalidOperationException);
            Assert.That(() => d.SetFilterSettings(l.Id, d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(2), new[] { PaintChannel.Normal }).Id, FilterSettings.Sharpen()), Throws.InvalidOperationException);
            Assert.That(() => FilterSettings.GaussianBlur(0), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => FilterSettings.FromValues(FilterType.Invert, 3, 0, 0, 0, false, 0, 1, 1, 0, 1), Throws.ArgumentException, "parameters a type does not use");
        }

        [Test] public void TheNormalBlurRenormalizes()
        {
            var d = Doc(32, 32, 16); var l = d.AddLayer("n"); var s = l.GetChannel(PaintChannel.Normal);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) s.SetPixel(x, y, x < 16 ? new Rgba32(255, 128, 128, 255) : new Rgba32(128, 255, 128, 255));
            d.ClearHistory();
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(3), new[] { PaintChannel.Normal });
            for (int x = 12; x < 20; x++)
            {
                var c = l.GetOutputPixel(PaintChannel.Normal, x, 10);
                double nx = c.R / 255.0 * 2 - 1, ny = c.G / 255.0 * 2 - 1, nz = c.B / 255.0 * 2 - 1;
                Assert.That(Math.Sqrt(nx * nx + ny * ny + nz * nz), Is.EqualTo(1).Within(.012), "unit length at " + x);
            }
            var mid = l.GetOutputPixel(PaintChannel.Normal, 16, 10);
            Assert.That(mid.R, Is.InRange(160, 240)); Assert.That(mid.G, Is.InRange(200, 250), "a renormalized average between +X and +Y (the edge is between 15 and 16)");
        }

        [Test] public void HaloAndWorkingMemoryBudgetsRefuseBeforeAnythingChanges()
        {
            var d = Doc(1024, 1024, 128); var l = d.AddLayer("l");
            Assert.That(() => FilterSettings.GaussianBlur(257), Throws.InstanceOf<ArgumentOutOfRangeException>());
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(256)); d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(256));
            Assert.That(() => d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(1)), Throws.InvalidOperationException.With.Message.Contains("512"), "the stack would reach more than 512 pixels");
            Assert.That(() => d.FilterWorkingBudgetBytes = 1 << 20, Throws.InstanceOf<ArgumentOutOfRangeException>(), "cannot drop below what the stack needs");
            while (l.Filters.Count > 0) d.RemoveFilter(l.Id, l.Filters[0].Id); d.ClearHistory();
            d.FilterWorkingBudgetBytes = 8L << 20;
            Assert.That(() => d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(200)), Throws.InvalidOperationException.With.Message.Contains("working memory"));
            Assert.That(l.Filters, Is.Empty); Assert.That(d.UndoCount, Is.Zero);
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(16));
            Assert.That(() => l.EvaluateOutputRegion(PaintChannel.Color, 0, 0, 1024, 1024), Throws.InvalidOperationException.With.Message.Contains("budget"), "a one-piece evaluation larger than the budget is refused");
            Assert.That(() => d.FilterBlockPixels = 4096, Throws.InvalidOperationException);
            for (int i = 0; i < PaintDocument.MaxFiltersPerStack - 1; i++) d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Invert());
            Assert.That(() => d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Invert()), Throws.InvalidOperationException);
        }

        [Test] public void TheCacheStaysWithinItsBudgetAndZeroBudgetIsStillCorrect()
        {
            var d = Doc(256, 256, 16); var l = Random(d, 4);
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(5));
            var reference = l.EvaluateOutputRegion(PaintChannel.Color, 0, 0, 256, 256);
            d.FilterCacheBudgetBytes = 64 * 1024; d.FilterBlockPixels = 32;
            Assert.That(Tiled(l, d), Is.EqualTo(reference));
            Assert.That(d.FilterCacheBytes, Is.LessThanOrEqualTo(64 * 1024 + 4 * 16 * 16 * 4));
            d.FilterCacheBudgetBytes = 0;
            Assert.That(Tiled(l, d), Is.EqualTo(reference)); Assert.That(d.FilterCacheBytes, Is.Zero);
        }

        // ───────────── Undo ─────────────

        [Test] public void AddMoveToggleRemoveUndoAndRedoExactly()
        {
            var d = Doc(48, 48, 16); var l = Random(d, 5);
            var plain = d.Composite(PaintChannel.Color);
            var blur = d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(3)); var afterBlur = d.Composite(PaintChannel.Color);
            var inv = d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Levels(0, 1, 2.5, 0, 1)); var both = d.Composite(PaintChannel.Color);
            d.MoveFilter(l.Id, inv.Id, 0); var moved = d.Composite(PaintChannel.Color);
            Assert.That(moved, Is.Not.EqualTo(both), "order matters (a gamma before or after the blur)");
            Assert.That(l.Filters.Select(f => f.Id), Is.EqualTo(new[] { inv.Id, blur.Id }));
            d.SetFilterEnabled(l.Id, inv.Id, false); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(afterBlur));
            d.RemoveFilter(l.Id, blur.Id); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(plain));
            Assert.That(d.UndoCount, Is.EqualTo(5));
            d.Undo(); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(afterBlur));
            d.Undo(); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(moved));
            d.Undo(); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(both));
            d.Undo(); d.Undo(); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(plain)); Assert.That(l.Filters, Is.Empty);
            d.Redo(); d.Redo(); d.Redo(); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(moved));
            Assert.That(l.GetChannel(PaintChannel.Color).EnumerateTiles().Count(), Is.EqualTo(9), "the source is the same layer throughout");
        }

        [Test] public void SliderDragsCoalesceIntoOneUndoStep()
        {
            var d = Doc(32, 32, 16); var l = Random(d, 6);
            var b = d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(2)); var start = d.Composite(PaintChannel.Color);
            int steps = d.UndoCount;
            for (int r = 3; r < 9; r++) d.SetFilterSettings(l.Id, b.Id, FilterSettings.GaussianBlur(r), coalesce: true);
            d.EndCoalescing();
            for (int i = 1; i <= 5; i++) d.SetFilterStrength(l.Id, b.Id, 1 - i / 10.0, coalesce: true);
            d.EndCoalescing();
            Assert.That(d.UndoCount, Is.EqualTo(steps + 2));
            d.Undo(); Assert.That(l.Filters[0].Strength, Is.EqualTo(1)); Assert.That(l.Filters[0].Settings.Radius, Is.EqualTo(8));
            d.Undo(); Assert.That(l.Filters[0].Settings.Radius, Is.EqualTo(2)); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(start));
        }

        [Test] public void BakeWritesTheFilteredPixelsAsOneUndoStep()
        {
            var d = Doc(48, 48, 16); var l = Random(d, 7); d.AddLayerMask(l.Id);
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(4)); d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.Levels(0, 1, 1, .2, 1));
            var shown = d.Composite(PaintChannel.Color); var source = d.Composite(PaintChannel.Color);
            int steps = d.UndoCount;
            Assert.That(d.BakeFilters(l.Id), Is.True);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            Assert.That(l.Filters, Is.Empty); Assert.That(l.Mask.Filters, Is.Empty);
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(shown), "baking does not change what is shown");
            Assert.That(l.Mask.Surface.GetPixel(30, 30).A, Is.EqualTo(51), "the mask holds the filtered hide amount");
            d.Undo();
            Assert.That(l.Filters.Count, Is.EqualTo(1)); Assert.That(l.Mask.Filters.Count, Is.EqualTo(1)); Assert.That(l.Mask.Surface.GetPixel(30, 30).A, Is.Zero);
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(shown));
            d.ActiveStrokeBudgetBytes = 1024;
            Assert.That(() => d.BakeFilters(l.Id), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(l.Filters.Count, Is.EqualTo(1)); Assert.That(d.UndoCount, Is.EqualTo(steps));
            Assert.That(d.BakeFilters(d.AddLayer("empty").Id), Is.False);
        }

        // ───────────── 保存・PSD ─────────────

        [Test] public void NativeVersion9RoundTripsFiltersAndReadsVersion8()
        {
            var d = Doc(48, 48, 16); var l = Random(d, 8); d.AddLayerMask(l.Id);
            var a = d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Sharpen(3, 1.25, 4), new[] { PaintChannel.Color, PaintChannel.Height });
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Noise(.3, 42, false), new[] { PaintChannel.Color }, enabled: false, strength: .4);
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Levels(.1, .9, 2, 0, .8), new[] { PaintChannel.Color });
            d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(5));
            var bytes = DocumentBinary.Write(d);
            Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(DocumentBinary.CurrentVersion)); Assert.That(DocumentBinary.CurrentVersion, Is.GreaterThanOrEqualTo(9));
            var read = DocumentBinary.Read(bytes); var rl = read.GetLayer(l.Id);
            Assert.That(rl.Filters.Select(f => (f.Id, f.Settings, f.Enabled, f.Strength, string.Join(",", f.Channels))), Is.EqualTo(l.Filters.Select(f => (f.Id, f.Settings, f.Enabled, f.Strength, string.Join(",", f.Channels)))));
            Assert.That(rl.Mask.Filters.Single().Settings, Is.EqualTo(FilterSettings.GaussianBlur(5)));
            Assert.That(read.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            Assert.That(read.CanUndo, Is.False);
            Assert.That(DocumentBinary.Write(read), Is.EqualTo(bytes), "write → read → write is byte-identical");
            // 版 8（フィルターの無い 1 レイヤーの文書）
            var plain = Doc(32, 32, 16); Random(plain, 9, "P");
            var v8 = ArchiveTestUtil.AsVersion(DocumentBinary.Write(plain), "P", 8);
            Assert.That(BitConverter.ToInt32(v8, 8), Is.EqualTo(8));
            Assert.That(DocumentBinary.Read(v8).Composite(PaintChannel.Color), Is.EqualTo(plain.Composite(PaintChannel.Color)));
            Assert.That(DocumentBinary.Read(ArchiveTestUtil.AsVersion(DocumentBinary.Write(plain), "P", 7)).Composite(PaintChannel.Color), Is.EqualTo(plain.Composite(PaintChannel.Color)));
        }

        [Test] public void UnknownFilterTypesVersionsAndInvalidChannelsAreRefusedNotDropped()
        {
            var d = Doc(32, 32, 16); var l = d.AddLayer("L");
            var f = d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(2), new[] { PaintChannel.Normal });
            var bytes = DocumentBinary.Write(d);
            int at = IndexOf(bytes, f.Id.ToByteArray()) + 16;
            var type = (byte[])bytes.Clone(); BitConverter.GetBytes(99).CopyTo(type, at);
            Assert.That(() => DocumentBinary.Read(type), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("Unknown filter type"));
            var version = (byte[])bytes.Clone(); BitConverter.GetBytes(2).CopyTo(version, at + 4);
            Assert.That(() => DocumentBinary.Read(version), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("algorithm version"));
            var sharpen = (byte[])bytes.Clone(); BitConverter.GetBytes((int)FilterType.Sharpen).CopyTo(sharpen, at);
            Assert.That(() => DocumentBinary.Read(sharpen), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("normals"), "a sharpen on Normal is refused as at creation");
            Assert.That(() => DocumentBinary.Read(bytes.Take(bytes.Length - 3).ToArray()), Throws.InstanceOf<InvalidDataException>());
            Assert.That(DocumentBinary.Read(bytes).GetLayer(l.Id).Filters.Single().Id, Is.EqualTo(f.Id));
        }
        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++) { int k = 0; while (k < needle.Length && haystack[i + k] == needle[k]) k++; if (k == needle.Length) return i; }
            throw new AssertionException("not found");
        }

        [Test] public void PsdExportRefusesFilteredLayersUntilBaked()
        {
            var d = Doc(32, 32, 16); var l = Random(d, 10);
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Invert(), enabled: false);
            Assert.That(() => PsdBridge.Export(d, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("filters"), "even a disabled filter is not dropped silently");
            d.SetFilterEnabled(l.Id, l.Filters[0].Id, true);
            d.BakeFilters(l.Id);
            var psd = PsdBridge.Export(d, PaintChannel.Color);
            Assert.That(psd.Layers.Count, Is.EqualTo(1));
        }

        [Test] public void TheSourceAndItsHiddenColourAreNeverChanged()
        {
            var d = Doc(48, 48, 16); var l = Random(d, 12);
            var before = l.GetChannel(PaintChannel.Color).EnumerateTiles().Select(t => t.Bytes).ToArray();
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(7)); d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Invert());
            d.Composite(PaintChannel.Color); Tiled(l, d);
            Assert.That(l.GetChannel(PaintChannel.Color).EnumerateTiles().Select(t => t.Bytes).ToArray(), Is.EqualTo(before));
        }
    }
}
