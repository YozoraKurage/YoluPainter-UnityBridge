using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// レイヤーの結合（下のレイヤーと結合・グループを結合・表示レイヤーを結合）: 結合の前後で文書の合成がバイト一致になる組み合わせ
    /// （クリッピングの基へ・孤立した重なりの一番下・孤立したグループ・表示レイヤー。Normal は単位ベクトルの量子化の往復で 1 段）を、
    /// ランダムな画素・合成モード・不透明度・マスク・クリッピング・効果・グループで確かめ、見た目が変わる組み合わせは断る（変えない）か、
    /// 許せば変わった画素の数を正直に知らせることを、独立に合成し直して確かめる。断る理由・予算・Undo/Redo・保存復元も。
    /// 比べ方は報告と同じ: 前後とも完全に透明な画素は RGB を問わず同じとみなす。
    /// </summary>
    public sealed class LayerMergeTests
    {
        const int W = 41, H = 35, Tile = 16;
        static readonly PaintChannel[] Used = { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Normal };
        static readonly LayerBlendMode[] Modes = ((LayerBlendMode[])Enum.GetValues(typeof(LayerBlendMode))).Where(m => m != LayerBlendMode.PassThrough).ToArray();

        static PaintDocument NewDocument() => new PaintDocument(W, H, Tile, 256L << 20) { ActiveStrokeBudgetBytes = 64L << 20 };

        /// <summary>層の使うチャンネルにばらばらの画素を置く: 不透明・半透明・アルファ 0 で RGB を持つ画素が混ざる。</summary>
        static void Scatter(Random r, PaintLayer layer, int count)
        {
            foreach (var c in Used)
            {
                var s = layer.GetChannel(c);
                for (int n = 0; n < count; n++)
                {
                    var b = new byte[4]; r.NextBytes(b); int kind = r.Next(4);
                    byte a = kind == 0 ? (byte)255 : kind == 1 ? (byte)0 : b[3];
                    s.SetPixel(r.Next(W), r.Next(H), new Rgba32(b[0], b[1], b[2], a));
                }
            }
        }
        static PaintLayer Paint(PaintDocument d, Random r, string name, bool decorate = true)
        {
            var l = d.AddLayer(name); Scatter(r, l, 300);
            if (decorate) Decorate(d, r, l);
            return l;
        }
        /// <summary>合成モード・不透明度・マスク（反転・濃度）・効果（ぼかし）をランダムに付ける。</summary>
        static void Decorate(PaintDocument d, Random r, PaintLayer l)
        {
            d.SetLayerBlendMode(l.Id, r.Next(3) == 0 ? LayerBlendMode.Normal : Modes[r.Next(Modes.Length)]);
            d.SetLayerOpacity(l.Id, r.Next(3) == 0 ? 1 : .2 + .8 * r.NextDouble());
            if (r.Next(2) == 0) AddRandomMask(d, r, l);
            if (l.Kind == LayerKind.Raster && r.Next(3) == 0) d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(1), new[] { PaintChannel.Color });
        }
        static void AddRandomMask(PaintDocument d, Random r, PaintLayer l)
        {
            var m = d.AddLayerMask(l.Id);
            for (int n = 0; n < 200; n++) m.Surface.SetPixel(r.Next(W), r.Next(H), new Rgba32(0, 0, 0, (byte)r.Next(256)));
            if (r.Next(3) == 0) d.SetLayerMaskInverted(l.Id, true);
            d.SetLayerMaskDensity(l.Id, .5 + .5 * r.NextDouble());
        }
        static Rgba32 RandomColor(Random r) { var b = new byte[4]; r.NextBytes(b); return new Rgba32(b[0], b[1], b[2], b[3]); }

        static Dictionary<PaintChannel, byte[]> Composites(PaintDocument d)
        { var all = new Dictionary<PaintChannel, byte[]>(); foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) all[c] = d.Composite(c); return all; }
        /// <summary>報告と同じ比べ方で数え直す（前後とも完全に透明なら同じ）。</summary>
        static (long changed, int max, Dictionary<PaintChannel, long> byChannel, int visible) Diff(Dictionary<PaintChannel, byte[]> before, Dictionary<PaintChannel, byte[]> after)
        {
            long changed = 0; int max = 0, visible = 0; var byChannel = new Dictionary<PaintChannel, long>();
            foreach (var c in before.Keys)
            {
                var a = after[c]; var b = before[c]; long n = 0;
                for (int o = 0; o < a.Length; o += 4)
                {
                    if (a[o + 3] == 0 && b[o + 3] == 0) continue;
                    int d = 0; for (int k = 0; k < 4; k++) d = Math.Max(d, Math.Abs(a[o + k] - b[o + k]));
                    if (d == 0) continue;
                    n++; max = Math.Max(max, d);
                    // 見える差: アルファと、アルファを掛けた色（四捨五入）
                    double v = Math.Abs(a[o + 3] - b[o + 3]);
                    for (int k = 0; k < 3; k++) v = Math.Max(v, Math.Floor(Math.Abs(a[o + k] * a[o + 3] - b[o + k] * b[o + 3]) / 255.0 + .5));
                    visible = Math.Max(visible, (int)v);
                }
                if (n > 0) { byChannel[c] = n; changed += n; }
            }
            return (changed, max, byChannel, visible);
        }
        static void AssertReportIsHonest(LayerMergeReport report, Dictionary<PaintChannel, byte[]> before, PaintDocument d)
        {
            var (changed, max, byChannel, visible) = Diff(before, Composites(d));
            Assert.That(report.ChangedPixels, Is.EqualTo(changed), "the report counts the same changed pixels as an independent full composite");
            Assert.That(report.MaxDifference, Is.EqualTo(max));
            Assert.That(report.MaxVisibleDifference, Is.EqualTo(visible));
            Assert.That(report.ChangedByChannel.OrderBy(e => e.Key).ToArray(), Is.EqualTo(byChannel.OrderBy(e => e.Key).ToArray()));
        }

        // ───────── バイト一致の組み合わせ ─────────

        [Test] public void AClippedLayerMergedIntoItsBaseLeavesTheCompositeExactly([Range(1, 8)] int seed)
        {
            var r = new Random(seed); var d = NewDocument();
            Paint(d, r, "background", decorate: seed % 2 == 0);
            var baseLayer = Paint(d, r, "base");
            PaintLayer clip;
            switch (seed % 4)
            {
                case 0: clip = d.AddAdjustmentLayer("adjust", AdjustmentSettings.HueSaturation(40, .3, -.2)); break;
                case 1: clip = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, RandomColor(r) }, { PaintChannel.Roughness, RandomColor(r) }, { PaintChannel.Normal, RandomColor(r) } }); break;
                default: clip = Paint(d, r, "clip", decorate: false); break;
            }
            Decorate(d, r, clip); d.SetLayerClipping(clip.Id, true);
            var second = Paint(d, r, "clip 2"); d.SetLayerClipping(second.Id, true);
            Paint(d, r, "top");
            var before = Composites(d); var bytes = DocumentBinary.Write(d); int undo = d.UndoCount;

            var report = d.MergeDown(clip.Id, tolerance: 0);
            Assert.That(report.Method, Is.EqualTo(MergeMethod.IntoClippingBase));
            Assert.That(report.Exact, Is.True, "changed " + report.ChangedPixels + " by " + report.MaxDifference);
            Assert.That(report.ComparedPixels, Is.GreaterThan(0));
            AssertReportIsHonest(report, before, d);
            var result = d.GetLayer(report.ResultId);
            Assert.That((result.Name, result.BlendMode, result.Opacity, result.Clipping), Is.EqualTo((baseLayer.Name, baseLayer.BlendMode, baseLayer.Opacity, baseLayer.Clipping)), "the result keeps the base's attributes");
            Assert.That(result.Mask != null, Is.EqualTo(baseLayer.Mask != null));
            Assert.That(result.Filters, Is.Empty, "the base's filters are baked");
            Assert.That(d.IsEffectivelyClipped(d.Layers.ToList().IndexOf(d.GetLayer(second.Id))), Is.True, "the next clipped layer clips to the result");
            Assert.That(d.Layers.Any(l => l.Id == clip.Id || l.Id == baseLayer.Id), Is.False);
            Assert.That(d.UndoCount, Is.EqualTo(undo + 1), "one undo step");

            var after = DocumentBinary.Write(d);
            Assert.That(d.Undo(), Is.True); Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes), "undo brings the layers back as they were");
            Assert.That(d.Redo(), Is.True); Assert.That(DocumentBinary.Write(d), Is.EqualTo(after));
            var restored = DocumentBinary.Read(after);
            foreach (var c in Used) Assert.That(restored.Composite(c), Is.EqualTo(d.Composite(c)), "saved and opened: " + c);
        }

        [Test] public void MergingDownAtTheBottomOfAnIsolatedStackIsExactExceptNormalRounding([Range(1, 8)] int seed)
        {
            var r = new Random(seed); var d = NewDocument();
            var lower = Paint(d, r, "lower");
            d.SetLayerOpacity(lower.Id, .3 + .6 * r.NextDouble()); // 普通でない下の層（ここでは不透明度）でも、下に何も無ければ見た目を保てる
            var upper = Paint(d, r, "upper");
            Paint(d, r, "top");
            var before = Composites(d);

            var report = d.MergeDown(upper.Id);
            Assert.That(report.Method, Is.EqualTo(MergeMethod.Isolated));
            AssertReportIsHonest(report, before, d);
            Assert.That(report.ChangedByChannel.Keys, Is.SubsetOf(new[] { PaintChannel.Normal }), "colour and scalar channels are exact");
            Assert.That(report.MaxDifference, Is.LessThanOrEqualTo(1));
            var result = d.GetLayer(report.ResultId);
            Assert.That((result.BlendMode, result.Opacity, result.Mask, result.Name), Is.EqualTo((LayerBlendMode.Normal, 1.0, (RasterMask)null, lower.Name)));
            TestContext.WriteLine("seed " + seed + ": Normal changed " + report.ChangedPixels + " of " + report.ComparedPixels);
        }

        [Test] public void MergingAnIsolatedGroupLeavesTheCompositeExactlyInEveryChannel([Range(1, 6)] int seed)
        {
            var r = new Random(seed); var d = NewDocument();
            Paint(d, r, "background", decorate: seed % 2 == 0);
            var a = Paint(d, r, "a"); var aClip = Paint(d, r, "a clip"); d.SetLayerClipping(aClip.Id, true);
            var fill = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, RandomColor(r) }, { PaintChannel.Normal, RandomColor(r) } }); Decorate(d, r, fill);
            var adjust = d.AddAdjustmentLayer("levels", AdjustmentSettings.Levels(.1, .9, 1.3, 0, 1));
            var n1 = Paint(d, r, "n1"); var n2 = Paint(d, r, "n2");
            var nested = d.GroupLayers(new[] { n1.Id, n2.Id }, "nested"); // 通過
            var hidden = Paint(d, r, "hidden"); d.SetLayerVisibility(hidden.Id, false);
            var group = d.GroupLayers(new[] { a.Id, aClip.Id, fill.Id, adjust.Id, nested.Id, hidden.Id }, "group");
            d.SetLayerBlendMode(group.Id, seed % 3 == 0 ? LayerBlendMode.Normal : Modes[r.Next(Modes.Length)]);
            d.SetLayerOpacity(group.Id, .4 + .6 * r.NextDouble());
            if (seed % 2 == 1) AddRandomMask(d, r, group);
            var over = Paint(d, r, "clipped to the group"); d.SetLayerClipping(over.Id, true);
            Paint(d, r, "top");
            var before = Composites(d); var bytes = DocumentBinary.Write(d);

            var report = d.MergeGroup(group.Id, tolerance: 0);
            Assert.That(report.Method, Is.EqualTo(MergeMethod.Group));
            Assert.That(report.Exact, Is.True, "changed " + report.ChangedPixels + " by " + report.MaxDifference);
            AssertReportIsHonest(report, before, d);
            Assert.That(report.Notes & MergeNotes.HiddenLayersDropped, Is.EqualTo(MergeNotes.HiddenLayersDropped));
            var result = d.GetLayer(report.ResultId);
            Assert.That((result.Name, result.BlendMode, result.Opacity, result.Kind), Is.EqualTo((group.Name, group.BlendMode, group.Opacity, LayerKind.Raster)));
            Assert.That(d.Layers.Any(l => l.ParentId == group.Id || l.Id == group.Id), Is.False, "the group and its contents are gone");
            Assert.That(d.Undo(), Is.True); Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes));
        }

        [Test] public void MergeVisibleKeepsTheLookAndTheHiddenLayers([Range(1, 6)] int seed)
        {
            var r = new Random(seed); var d = NewDocument();
            var hiddenBottom = Paint(d, r, "hidden bottom"); d.SetLayerVisibility(hiddenBottom.Id, false);
            Paint(d, r, "a");
            var b = Paint(d, r, "b"); var bClip = Paint(d, r, "b clip"); d.SetLayerClipping(bClip.Id, true);
            var hiddenBase = Paint(d, r, "hidden base"); d.SetLayerVisibility(hiddenBase.Id, false);
            var orphan = Paint(d, r, "clipped to hidden"); d.SetLayerClipping(orphan.Id, true);
            var g1 = Paint(d, r, "g1"); var g2 = Paint(d, r, "g2"); var gHidden = Paint(d, r, "g hidden"); d.SetLayerVisibility(gHidden.Id, false);
            var group = d.GroupLayers(new[] { g1.Id, g2.Id, gHidden.Id }, "group"); if (seed % 2 == 0) d.SetLayerBlendMode(group.Id, LayerBlendMode.Screen);
            d.AddAdjustmentLayer("invert", AdjustmentSettings.Invert(), new[] { PaintChannel.Roughness });
            var before = Composites(d);

            var report = d.MergeVisible("merged", anchor: b.Id);
            Assert.That(report.Method, Is.EqualTo(MergeMethod.Visible));
            AssertReportIsHonest(report, before, d);
            Assert.That(report.ChangedByChannel.Keys, Is.SubsetOf(new[] { PaintChannel.Normal }), "colour and scalar channels are exact");
            Assert.That(report.MaxDifference, Is.LessThanOrEqualTo(1));
            var result = d.GetLayer(report.ResultId);
            Assert.That((result.Name, result.ParentId, result.BlendMode, result.Opacity), Is.EqualTo(("b", Guid.Empty, LayerBlendMode.Normal, 1.0)), "named after the selected layer, which was merged");
            foreach (var kept in new[] { hiddenBottom, hiddenBase, orphan, gHidden }) Assert.That(d.Layers.Contains(kept), Is.True, kept.Name + " shows nothing and stays");
            Assert.That(d.Layers.Contains(group), Is.True, "the group stays while it still holds a hidden layer");
            Assert.That(gHidden.ParentId, Is.EqualTo(group.Id));
            d.ValidateStructure();
            Assert.That(d.Layers.Contains(g1) || d.Layers.Contains(b), Is.False);
        }

        [Test] public void APassThroughGroupBecomesIsolatedAndTheLookChangeIsRefusedOrReported()
        {
            var d = NewDocument();
            var red = d.AddLayer("red"); red.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(0, 0), Uniform(new Rgba32(200, 30, 30, 255)));
            var multiply = d.AddLayer("multiply"); multiply.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(0, 0), Uniform(new Rgba32(128, 128, 128, 255)));
            d.SetLayerBlendMode(multiply.Id, LayerBlendMode.Multiply);
            var group = d.GroupLayers(new[] { multiply.Id }, "pass through");
            var before = Composites(d); var bytes = DocumentBinary.Write(d); int undo = d.UndoCount, redo = d.RedoCount;

            var refused = Assert.Throws<LayerMergeException>(() => d.MergeGroup(group.Id));
            Assert.That(refused.Reason, Is.EqualTo(LayerOpRefusal.AppearanceChanges));
            Assert.That(refused.Report.ChangedPixels, Is.EqualTo(Tile * Tile), "the whole tile changes: multiply had nothing below it inside the isolated result");
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes), "refused: nothing changed");
            Assert.That((d.UndoCount, d.RedoCount), Is.EqualTo((undo, redo)));

            var report = d.MergeGroup(group.Id, tolerance: 255);
            AssertReportIsHonest(report, before, d);
            Assert.That(d.GetLayer(report.ResultId).BlendMode, Is.EqualTo(LayerBlendMode.Normal), "pass through becomes Normal");
        }

        static byte[] Uniform(Rgba32 c) { var t = new byte[Tile * Tile * 4]; for (int i = 0; i < t.Length; i += 4) { t[i] = c.R; t[i + 1] = c.G; t[i + 2] = c.B; t[i + 3] = c.A; } return t; }

        // ───────── 下の層の上に重ねる（Photoshop の下のレイヤーと結合） ─────────

        [Test] public void MergingOntoAnOpaqueLowerLayerIsExactWhateverTheUpperMode([Range(1, 6)] int seed)
        {
            var r = new Random(seed); var d = NewDocument();
            Paint(d, r, "background");
            var lower = d.AddFillLayer("opaque", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(90, 160, 40, 255) }, { PaintChannel.Roughness, new Rgba32(30, 0, 0, 255) } });
            var upper = Paint(d, r, "upper");
            var before = Composites(d); before.Remove(PaintChannel.Normal); // 不透明な下の層は Color と Roughness だけ

            // Normal は下の層に無いので、上の層は透明の上に重ねてから下の画素に重ねることになる（Overlay の RNM などで変わり得る）
            var report = d.MergeDown(upper.Id, tolerance: 255);
            Assert.That(report.Method, Is.EqualTo(MergeMethod.OntoLowerLayer));
            Assert.That(report.ChangedByChannel.Keys, Is.SubsetOf(new[] { PaintChannel.Normal }));
            var after = Composites(d); after.Remove(PaintChannel.Normal);
            Assert.That(Diff(before, after).changed, Is.Zero);
            var result = d.GetLayer(report.ResultId);
            Assert.That((result.Kind, result.Name), Is.EqualTo((LayerKind.Raster, "opaque")), "the fill layer becomes pixels");
        }

        [Test] public void OverlappingSemiTransparentLayersRoundAndTheReportSaysSo([Range(1, 4)] int seed)
        {
            var r = new Random(seed); var d = NewDocument();
            var background = d.AddLayer("background"); for (int ty = 0; ty < 3; ty++) for (int tx = 0; tx < 3; tx++) background.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(tx, ty), Edge(Uniform(new Rgba32(250, 240, 230, 255)), tx, ty));
            var lower = Paint(d, r, "lower", decorate: false); var upper = Paint(d, r, "upper", decorate: false);
            var before = Composites(d);
            var report = d.MergeDown(upper.Id, tolerance: 255);
            Assert.That(report.Method, Is.EqualTo(MergeMethod.OntoLowerLayer));
            AssertReportIsHonest(report, before, d);
            Assert.That(report.MaxVisibleDifference, Is.LessThanOrEqualTo(PaintDocument.MergeRoundingTolerance), "Normal layers over an opaque background differ only by rounding");
            TestContext.WriteLine("seed " + seed + ": " + report.ChangedPixels + " of " + report.ComparedPixels + " changed, max " + report.MaxDifference + ", visible " + report.MaxVisibleDifference + " (" + string.Join(", ", report.ChangedByChannel.Select(e => e.Key + " " + e.Value)) + ")");
        }
        static byte[] Edge(byte[] tile, int tx, int ty)
        {
            for (int y = 0; y < Tile; y++) for (int x = 0; x < Tile; x++) if (tx * Tile + x >= W || ty * Tile + y >= H) for (int k = 0; k < 4; k++) tile[(y * Tile + x) * 4 + k] = 0;
            return tile;
        }

        [Test] public void ALowerLayerInAnotherModeKeepsItsModeAndTheChangeIsRefusedUnlessAllowed()
        {
            var d = NewDocument();
            var background = d.AddLayer("background"); background.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(0, 0), Uniform(new Rgba32(200, 200, 200, 255)));
            var lower = d.AddLayer("shade"); lower.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(0, 0), Uniform(new Rgba32(100, 100, 255, 255)));
            d.SetLayerBlendMode(lower.Id, LayerBlendMode.Multiply);
            var upper = d.AddLayer("paint"); upper.GetChannel(PaintChannel.Color).SetPixel(3, 3, new Rgba32(255, 0, 0, 255));
            var before = Composites(d); var bytes = DocumentBinary.Write(d);

            var refused = Assert.Throws<LayerMergeException>(() => d.MergeDown(upper.Id));
            Assert.That(refused.Report.ChangedPixels, Is.EqualTo(1), "the red pixel would be multiplied with the background");
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes));

            var report = d.MergeDown(upper.Id, tolerance: 255);
            Assert.That(report.Method, Is.EqualTo(MergeMethod.OntoLowerLayer));
            Assert.That(d.GetLayer(report.ResultId).BlendMode, Is.EqualTo(LayerBlendMode.Multiply), "Photoshop's merge down keeps the lower layer's mode");
            AssertReportIsHonest(report, before, d);
        }

        [Test] public void AnAdjustmentMergedDownIsAppliedToTheLayerBelowWithoutAddingChannels()
        {
            var d = NewDocument(); var r = new Random(7);
            var lower = d.AddLayer("lower"); var s = lower.GetChannel(PaintChannel.Color);
            for (int n = 0; n < 400; n++) s.SetPixel(r.Next(W), r.Next(H), RandomColor(r));
            var invert = d.AddAdjustmentLayer("invert", AdjustmentSettings.Invert()); // 全チャンネルに効く
            var before = Composites(d);
            var report = d.MergeDown(invert.Id, tolerance: 0);
            Assert.That(report.Exact, Is.True, "a plain layer at the bottom takes the adjustment exactly");
            AssertReportIsHonest(report, before, d);
            var result = d.GetLayer(report.ResultId);
            Assert.That(result.Channels.Keys, Is.EqualTo(new[] { PaintChannel.Color }), "no empty surfaces for the channels the adjustment had nothing to change in");
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                Assert.That(result.GetPixel(PaintChannel.Color, x, y), Is.EqualTo(invert.Adjustment.Composite(lower.GetPixel(PaintChannel.Color, x, y), 1, invert.BlendMode)), "the adjustment applied to the pixel below");
        }

        [Test] public void MergedPixelsDoNotDependOnTheNumberOfThreads()
        {
            int saved = CoreParallelism.MaxDegreeOfParallelism; var results = new List<byte[]>();
            try
            {
                foreach (int degree in new[] { 1, 3, 0 })
                {
                    CoreParallelism.MaxDegreeOfParallelism = degree;
                    var r = new Random(11); var d = new PaintDocument(W, H, Tile, 256L << 20, new Guid("8a0b3c2d-0000-4000-8000-000000000001"));
                    var a = d.AddLayer("a", new Guid("8a0b3c2d-0000-4000-8000-000000000002")); Scatter(r, a, 300);
                    var b = d.AddLayer("b", new Guid("8a0b3c2d-0000-4000-8000-000000000003")); Scatter(r, b, 300); d.SetLayerBlendMode(b.Id, LayerBlendMode.Overlay); d.SetLayerOpacity(b.Id, .6);
                    var g = d.GroupLayers(new[] { a.Id, b.Id }, "g", new Guid("8a0b3c2d-0000-4000-8000-000000000004")); d.SetLayerBlendMode(g.Id, LayerBlendMode.Multiply);
                    d.MergeGroup(g.Id, 0, new Guid("8a0b3c2d-0000-4000-8000-000000000005"));
                    results.Add(DocumentBinary.Write(d));
                }
            }
            finally { CoreParallelism.MaxDegreeOfParallelism = saved; }
            Assert.That(results[1], Is.EqualTo(results[0])); Assert.That(results[2], Is.EqualTo(results[0]));
        }

        // ───────── 断る・予算・巻き戻し ─────────

        [Test] public void MergesAreRefusedWithAReasonAndChangeNothing()
        {
            var d = NewDocument(); var r = new Random(5);
            var bottom = Paint(d, r, "bottom", false);
            var adjust = d.AddAdjustmentLayer("adjust", AdjustmentSettings.Invert());
            var overAdjust = Paint(d, r, "over adjust", false);
            var inner = Paint(d, r, "inner", false); var group = d.GroupLayers(new[] { inner.Id }, "group");
            var overGroup = Paint(d, r, "over group", false);
            var hidden = Paint(d, r, "hidden", false); d.SetLayerVisibility(hidden.Id, false);
            var empty = d.AddGroup("empty");
            var bytes = DocumentBinary.Write(d); int undo = d.UndoCount;
            void Refused(TestDelegate merge, LayerOpRefusal reason)
            {
                var ex = Assert.Throws<LayerOpException>(merge); Assert.That(ex.Reason, Is.EqualTo(reason), ex.Message);
                Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes)); Assert.That(d.UndoCount, Is.EqualTo(undo));
            }
            Refused(() => d.MergeDown(bottom.Id), LayerOpRefusal.NoLayerBelow);
            Refused(() => d.MergeDown(overAdjust.Id), LayerOpRefusal.LayerBelowIsAdjustment);
            Refused(() => d.MergeDown(overGroup.Id), LayerOpRefusal.LayerBelowIsGroup);
            Refused(() => d.MergeDown(group.Id), LayerOpRefusal.IsGroup);
            Refused(() => d.MergeDown(hidden.Id), LayerOpRefusal.HiddenLayer);
            Refused(() => d.MergeGroup(bottom.Id), LayerOpRefusal.NotGroup);
            Refused(() => d.MergeGroup(empty.Id), LayerOpRefusal.EmptyGroup);
            Assert.That(d.MergeDownRefusal(overAdjust.Id), Is.EqualTo(LayerOpRefusal.LayerBelowIsAdjustment));
            Assert.That(d.MergeDownRefusal(overGroup.Id), Is.EqualTo(LayerOpRefusal.LayerBelowIsGroup));
            Assert.That(d.MergeDownRefusal(adjust.Id), Is.Null, "an adjustment layer can be merged into the paint layer below (it is applied to it)");

            var blank = NewDocument(); var only = blank.AddLayer("only"); blank.SetLayerVisibility(only.Id, false);
            Assert.That(Assert.Throws<LayerOpException>(() => blank.MergeVisible("m")).Reason, Is.EqualTo(LayerOpRefusal.NothingVisible));

            var stroke = d.BeginStroke(overGroup.Id, PaintChannel.Color, new BrushSettings());
            Assert.That(() => d.MergeDown(overGroup.Id), Throws.InvalidOperationException, "not during a stroke");
            stroke.Cancel();
            Assert.That(() => d.MergeDown(overGroup.Id, tolerance: 256), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test] public void AMergedLayerLargerThanTheOneOperationBudgetIsRefusedBeforeAnythingChanges()
        {
            var r = new Random(9); var d = NewDocument();
            var lower = Paint(d, r, "lower", false); var upper = Paint(d, r, "upper", false);
            var bytes = DocumentBinary.Write(d);
            d.ActiveStrokeBudgetBytes = 3000;
            var ex = Assert.Throws<LayerOpException>(() => d.MergeDown(upper.Id));
            Assert.That(ex.Reason, Is.EqualTo(LayerOpRefusal.OperationBudget));
            Assert.That(ex.Limit, Is.EqualTo(3000));
            Assert.That(Assert.Throws<LayerOpException>(() => d.MergeVisible("m")).Reason, Is.EqualTo(LayerOpRefusal.OperationBudget));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes)); Assert.That(d.CanUndo, Is.False);
        }

        [Test] public void AMergeThatDoesNotFitTheSourceBudgetIsUndoneWhileSwapping()
        {
            // 塗りつぶしの層は画素を持たない（0 バイト）。結合の結果は画素なので、予算いっぱいの文書では入れ替えの確かめで断られる
            var d = NewDocument();
            d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(10, 20, 30, 255) } });
            var dot = d.AddLayer("dot"); dot.GetChannel(PaintChannel.Color).SetPixel(1, 1, new Rgba32(255, 0, 0, 255));
            d.SourceBudgetBytes = d.AllocatedBytes;
            var bytes = DocumentBinary.Write(d);
            Assert.That(() => d.MergeDown(dot.Id), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes)); Assert.That(d.CanUndo, Is.False);
        }

        [Test] public void AGeneratorWithoutMapsIsNotBakedByAMerge()
        {
            var d = NewDocument(); var r = new Random(2);
            Paint(d, r, "lower", false); var upper = Paint(d, r, "upper", false);
            d.AddFilter(upper.Id, FilterTarget.Content, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.EdgeWear)), new[] { PaintChannel.Roughness });
            var bytes = DocumentBinary.Write(d);
            Assert.That(() => d.MergeDown(upper.Id), Throws.InvalidOperationException.With.Message.Contains("cannot be baked"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes));
        }

        [Test] public void TheMergedLayerHasNoFiltersPathOrDisabledPixelsAndSaysSo()
        {
            var d = NewDocument(); var r = new Random(4);
            Paint(d, r, "lower", false); var upper = Paint(d, r, "upper", false);
            d.AddFilter(upper.Id, FilterTarget.Content, FilterSettings.GaussianBlur(1), new[] { PaintChannel.Color });
            d.SetChannelEnabled(upper.Id, PaintChannel.Roughness, false);
            var report = d.MergeDown(upper.Id, tolerance: 255);
            Assert.That(report.Notes, Is.EqualTo(MergeNotes.EffectsBaked | MergeNotes.DisabledChannelsDropped));
            var result = d.GetLayer(report.ResultId);
            Assert.That(result.Filters, Is.Empty);
            Assert.That(result.IsChannelEnabled(PaintChannel.Roughness), Is.True, "the lower layer's Roughness stays");
        }
    }
}
