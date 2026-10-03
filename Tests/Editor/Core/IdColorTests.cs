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
    /// <summary>
    /// ID マップの色で選ぶ（Core）: 選択範囲（<see cref="SelectionMask.FromIdColors"/>）は許容の幅の中の色のテクセルだけを選び（境目の値ちょうどは
    /// 入り、1 つ上は入らない）、焼いていないテクセルは黒でも選ばず、余白は選ぶ。読み方（8 bit・UV・16 進）。種類・大きさ・値の拒否。選択は
    /// 1 回の Undo。Generator「ID の色」（種類 6）は一覧の色の所が 1、ほかが 0（マスクにも層の画素にも。反転・合成・強さ・崩しと組める）、
    /// 色が無い・マップが無い・別のベイクにピン留めでは入力のまま理由を出し、空のテクセルは入力を通す。編集は 1 回ずつの Undo。値・数・重複・
    /// ほかの種類の色・ピンの種類の拒否、型（Normal）と作業メモリの予算の拒否。ネイティブの版（<see cref="DocumentBinary.CurrentVersion"/>、
    /// 今は 15）の往復がバイト一致、古い版での種類 6・壊れた値・途中で切れたものの拒否、ID の色の無い文書は版 13 と同じ並び。
    /// </summary>
    public sealed class IdColorTests
    {
        const int W = 64, H = 48, T = 16;
        const int Red = 0xFF0000, Green = 0x00FF00, Blue = 0x0000FF, NearBlue = 0x0800F7; // NearBlue は Blue から 8（R と B）

        /// <summary>試験の ID マップ: x &lt; 16 赤、&lt; 32 緑、&lt; 48 青（奇数の行は 8 だけずれた青）、48 以上は y &lt; 8 が余白（赤の写し）、
        /// ほかは焼いていない（値 0 = 黒）。</summary>
        static int ColourAt(int x, int y) => x < 16 ? Red : x < 32 ? Green : x < 48 ? (y % 2 == 0 ? Blue : NearBlue) : Red;
        static MeshTexelCoverage CoverageAt(int x, int y) => x < 48 ? MeshTexelCoverage.Covered : y < 8 ? MeshTexelCoverage.Padding : MeshTexelCoverage.Empty;
        static BakedMeshMap IdMap(int w = W, int h = H, MeshMapKind kind = MeshMapKind.Id, string key = "test")
            => TestMeshMaps.Make(kind, w, h, (x, y, c) => kind == MeshMapKind.Id ? (ColourAt(x, y) >> (16 - 8 * c) & 255) / 255.0 : .5, CoverageAt, key);

        static (PaintDocument d, PaintLayer fill) MaskedFill()
        {
            var d = new PaintDocument(W, H, T);
            var fill = d.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(255, 255, 255, 255) } });
            d.AddLayerMask(fill.Id); d.ClearHistory();
            return (d, fill);
        }
        static GeneratorSettings Ids(params int[] colors) => GeneratorSettings.Default(GeneratorType.IdColor).WithIdColors(colors);

        // ───────── 読み方 ─────────

        [Test] public void TexelsReadAsEightBitColoursByPixelAndUv()
        {
            var map = IdMap();
            Assert.That(IdMapColors.TryGet(map, 3, 3, out int rgb), Is.True); Assert.That(rgb, Is.EqualTo(Red));
            Assert.That(IdMapColors.TryGet(map, 40, 5, out rgb), Is.True); Assert.That(rgb, Is.EqualTo(NearBlue));
            Assert.That(IdMapColors.TryGet(map, 50, 3, out rgb), Is.True, "padding has the island's colour"); Assert.That(rgb, Is.EqualTo(Red));
            Assert.That(IdMapColors.TryGet(map, 50, 20, out _), Is.False, "an empty texel has no colour");
            Assert.That(IdMapColors.TryGet(map, -1, 0, out _), Is.False); Assert.That(IdMapColors.TryGet(map, W, 0, out _), Is.False);
            Assert.That(IdMapColors.TryGetAtUv(map, (20 + .5) / W, (7 + .5) / H, out rgb) && rgb == Green, Is.True);
            Assert.That(IdMapColors.TryGetAtUv(map, 1, 0, out rgb), Is.True, "u = 1 is the last column"); Assert.That(rgb, Is.EqualTo(Red));
            Assert.That(IdMapColors.TryGetAtUv(map, -.01, .5, out _), Is.False); Assert.That(IdMapColors.TryGetAtUv(map, double.NaN, .5, out _), Is.False);
            Assert.That(IdMapColors.Hex(NearBlue), Is.EqualTo("#0800F7"));
            Assert.That(IdMapColors.Near(Blue, NearBlue, 8), Is.True); Assert.That(IdMapColors.Near(Blue, NearBlue, 7), Is.False);
            Assert.That(() => IdMapColors.TryGet(IdMap(kind: MeshMapKind.Position), 0, 0, out _), Throws.ArgumentException.With.Message.Contains("ID map"));
        }

        // ───────── 選択範囲 ─────────

        [Test] public void TheSelectionTakesTheTexelsWithinTheToleranceAndIsOneUndo()
        {
            var d = new PaintDocument(W, H, T); d.AddLayer("L"); d.ClearHistory(); var map = IdMap();
            void Expect(SelectionMask mask, Func<int, int, bool> selected, string what)
            {
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                    Assert.That(mask[x, y], Is.EqualTo(selected(x, y) ? 255 : 0), what + " at " + x + "," + y);
            }
            Expect(SelectionMask.FromIdColors(d, map, new[] { Blue }, 7), (x, y) => x >= 32 && x < 48 && y % 2 == 0, "tolerance 7");
            Expect(SelectionMask.FromIdColors(d, map, new[] { Blue }, 8), (x, y) => x >= 32 && x < 48, "tolerance 8 takes the near blue");
            Expect(SelectionMask.FromIdColors(d, map, new[] { Red }, 0), (x, y) => x < 16 || x >= 48 && y < 8, "the padding has red too, the empty texels do not");
            Expect(SelectionMask.FromIdColors(d, map, new[] { Red, Green }, 0), (x, y) => x < 32 || x >= 48 && y < 8, "two colours");
            Expect(SelectionMask.FromIdColors(d, map, new[] { 0x000000 }, 0), (x, y) => false, "black is no colour: empty texels are never selected");
            Expect(SelectionMask.FromIdColors(d, map, new[] { 0x7F7F7F }, 255), (x, y) => CoverageAt(x, y) != MeshTexelCoverage.Empty, "the widest tolerance takes every baked texel");
            Assert.That(SelectionMask.FromIdColors(d, map, new int[0], 8).IsEmpty, Is.True);
            // 型・大きさ・値の拒否
            Assert.That(() => SelectionMask.FromIdColors(d, IdMap(kind: MeshMapKind.Curvature), new[] { Red }, 0), Throws.ArgumentException.With.Message.Contains("ID map"));
            Assert.That(() => SelectionMask.FromIdColors(d, IdMap(32, 32), new[] { Red }, 0), Throws.ArgumentException.With.Message.Contains("32×32"));
            Assert.That(() => SelectionMask.FromIdColors(d, map, new[] { Red }, -1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => SelectionMask.FromIdColors(d, map, new[] { Red }, 256), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => SelectionMask.FromIdColors(d, map, new[] { 0x1000000 }, 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => SelectionMask.FromIdColors(d, map, null, 0), Throws.ArgumentNullException);
            Assert.That(() => SelectionMask.FromIdColors(d, null, new[] { Red }, 0), Throws.ArgumentNullException);
            // 選択は 1 回の Undo（画素は変えない）
            var pixels = d.Composite(PaintChannel.Color); var green = SelectionMask.FromIdColors(d, map, new[] { Green }, 0);
            d.SetSelection(green); Assert.That(d.UndoCount, Is.EqualTo(1));
            d.SetSelection(d.Selection.Combine(SelectionMask.FromIdColors(d, map, new[] { Red }, 0), SelectionCombine.Add));
            Assert.That(d.Selection[3, 3], Is.EqualTo(255)); Assert.That(d.Selection[20, 3], Is.EqualTo(255));
            Assert.That(d.Undo(), Is.True); Assert.That(d.Selection, Is.SameAs(green));
            Assert.That(d.Undo(), Is.True); Assert.That(d.Selection, Is.Null);
            Assert.That(d.Redo(), Is.True); Assert.That(d.Selection, Is.SameAs(green));
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(pixels), "no pixel changed");
        }

        // ───────── Generator ─────────

        [Test] public void TheGeneratorShowsTheChosenPartsOnMasksAndPixels()
        {
            var (d, fill) = MaskedFill();
            var e = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.IdColor)));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Reason, Does.Contain("No mesh maps are connected"));
            var inputs = new TestGeneratorInputs().Put(IdMap()); d.GeneratorInputs = inputs;
            var status = d.GetGeneratorStatus(fill.Id, e.Id);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("No ID colours are chosen yet"));
            Assert.That(fill.Mask.EvaluateOutputRegion(0, 0, W, H).All(h => h == 0), Is.True, "without colours the input passes through");
            Assert.That(d.InactiveGenerators().Single(), Does.Contain("No ID colours"));
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Ids(Green)));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Active, Is.True);
            void Expect(Func<int, int, bool> shown, string what)
            {
                var hide = fill.Mask.EvaluateOutputRegion(0, 0, W, H);
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                    Assert.That(hide[y * W + x], Is.EqualTo(CoverageAt(x, y) == MeshTexelCoverage.Empty || shown(x, y) ? 0 : 255), what + " at " + x + "," + y);
            }
            Expect((x, y) => x >= 16 && x < 32, "green shows (multiply: visibility × 1), the rest hides; empty texels pass through");
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Ids(Red, Blue).WithIdTolerance(8)));
            Expect((x, y) => x < 16 || x >= 32 && x < 48 || x >= 48 && y < 8, "red and both blues");
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Ids(Red, Blue).WithIdTolerance(7).WithInvert(true)));
            Expect((x, y) => !(x < 16 || x >= 32 && x < 48 && y % 2 == 0 || x >= 48 && y < 8), "inverted, tolerance 7");
            // 崩し（UV の上）は削るだけ、強さ 0.5 は半分
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Ids(Green).WithNoise(1, .1, 3, GeneratorNoiseSpace.Uv)));
            var worn = fill.Mask.EvaluateOutputRegion(0, 0, W, H);
            Assert.That(Enumerable.Range(0, W * H).Where(i => i % W < 16).All(i => worn[i] == 255), Is.True, "outside green stays hidden");
            Assert.That(Enumerable.Range(0, W * H).Count(i => i % W >= 16 && i % W < 32 && worn[i] > 0), Is.GreaterThan(10), "the breakup wears green away in patches");
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Ids(Green)));
            d.SetFilterStrength(fill.Id, e.Id, .5);
            Assert.That(fill.Mask.EvaluateOutputRegion(0, 0, W, H)[5 * W + 3], Is.EqualTo(127), "half strength hides half (visibility 0.5 rounds up to 128)");
            d.SetFilterStrength(fill.Id, e.Id, 1);
            // 層の画素（Roughness に掛ける。透明の画素は RGB を保つ）
            var raster = d.AddLayer("Paint"); d.SetChannelEnabled(raster.Id, PaintChannel.Roughness, true); var rough = raster.GetChannel(PaintChannel.Roughness);
            rough.SetPixel(20, 10, new Rgba32(200, 200, 200, 255)); rough.SetPixel(5, 10, new Rgba32(200, 200, 200, 255)); rough.SetPixel(6, 10, new Rgba32(7, 8, 9, 0));
            d.AddFilter(raster.Id, FilterTarget.Content, FilterSettings.FromGenerator(Ids(Green)), new[] { PaintChannel.Roughness });
            Assert.That(raster.GetOutputPixel(PaintChannel.Roughness, 20, 10), Is.EqualTo(new Rgba32(200, 200, 200, 255)), "green: × 1");
            Assert.That(raster.GetOutputPixel(PaintChannel.Roughness, 5, 10), Is.EqualTo(new Rgba32(0, 0, 0, 255)), "red: × 0, alpha unchanged");
            Assert.That(raster.GetOutputPixel(PaintChannel.Roughness, 6, 10), Is.EqualTo(new Rgba32(7, 8, 9, 0)), "a transparent pixel keeps its RGB");
            // マップが無い・別のベイクにピン留め: 入力のまま理由を出す。今のベイクにピン留めなら効く
            inputs.Refuse(MeshMapKind.Id, "Id is stale: the model changed.");
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Reason, Does.Contain("stale"));
            Assert.That(fill.Mask.EvaluateOutputRegion(0, 0, W, H).All(h => h == 0), Is.True);
            var map = IdMap(); inputs.Put(map);
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Ids(Green).WithPin(MeshMapKind.Id, new string('a', 64))));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Reason, Does.Contain("pinned"));
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(Ids(Green).WithPin(MeshMapKind.Id, map.Provenance.ConditionKey)));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Active, Is.True);
            inputs.Put(IdMap(32, 32));
            Assert.That(d.GetGeneratorStatus(fill.Id, e.Id).Reason, Does.Contain("32×32"));
        }

        [Test] public void EditsAreUndoStepsAndBadValuesAreRefused()
        {
            var (d, fill) = MaskedFill(); d.GeneratorInputs = new TestGeneratorInputs().Put(IdMap());
            var e = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Ids(Red)));
            var red = d.Composite(PaintChannel.Color); int steps = d.UndoCount;
            var g = fill.Mask.Filters.Single().Settings.Generator;
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(g.WithIdColorAdded(Green)));
            d.SetFilterSettings(fill.Id, e.Id, FilterSettings.FromGenerator(fill.Mask.Filters.Single().Settings.Generator.WithIdColorRemoved(Red)));
            Assert.That(d.UndoCount, Is.EqualTo(steps + 2), "one step each");
            Assert.That(fill.Mask.Filters.Single().Settings.Generator.IdColors, Is.EqualTo(new[] { Green }));
            Assert.That(d.Undo(), Is.True); Assert.That(fill.Mask.Filters.Single().Settings.Generator.IdColors, Is.EqualTo(new[] { Red, Green }), "in the order they were added");
            Assert.That(d.Undo(), Is.True); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(red));
            Assert.That(d.Redo(), Is.True); Assert.That(fill.Mask.Filters.Single().Settings.Generator.IdColors, Is.EqualTo(new[] { Red, Green }));
            // 足す・外すは一覧を変えなければそのまま
            Assert.That(g.WithIdColorAdded(Red), Is.EqualTo(g)); Assert.That(g.WithIdColorRemoved(Blue), Is.EqualTo(g));
            Assert.That(g, Is.Not.EqualTo(g.WithIdTolerance(9))); Assert.That(Ids(Red, Green), Is.Not.EqualTo(Ids(Green, Red)), "the order is part of the settings");
            // 値の拒否
            Assert.That(() => Ids(Enumerable.Range(1, GeneratorSettings.MaxIdColors + 1).ToArray()), Throws.InstanceOf<ArgumentOutOfRangeException>().With.Message.Contains("32"));
            Assert.That(Ids(Enumerable.Range(1, GeneratorSettings.MaxIdColors).ToArray()).IdColors.Count, Is.EqualTo(32));
            Assert.That(() => Ids(Red, Red), Throws.ArgumentException.With.Message.Contains("twice"));
            Assert.That(() => Ids(-1), Throws.InstanceOf<ArgumentOutOfRangeException>()); Assert.That(() => Ids(0x1000000), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => g.WithIdTolerance(256), Throws.InstanceOf<ArgumentOutOfRangeException>()); Assert.That(() => g.WithIdTolerance(-1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => GeneratorSettings.Default(GeneratorType.EdgeWear).WithIdColors(new[] { Red }), Throws.ArgumentException.With.Message.Contains("ID colour generator"));
            Assert.That(() => GeneratorSettings.Default(GeneratorType.Dirt).WithIdTolerance(3), Throws.ArgumentException);
            // 読むマップとピン
            Assert.That(g.UsedMaps, Is.EqualTo(new[] { MeshMapKind.Id }));
            Assert.That(g.WithNoise(.5, .1, 0, GeneratorNoiseSpace.Model).UsedMaps, Is.EqualTo(new[] { MeshMapKind.Id, MeshMapKind.Position }));
            Assert.That(GeneratorSettings.CandidateMaps(GeneratorType.IdColor), Is.EqualTo(new[] { MeshMapKind.Id, MeshMapKind.Position }));
            Assert.That(() => g.WithPin(MeshMapKind.Curvature, new string('a', 64)), Throws.ArgumentException);
            Assert.That(g.Name, Is.EqualTo("ID color")); Assert.That(g.ToString(), Does.Contain("#FF0000").And.Contain("±8"));
            // 型（Normal は単位ベクトル）と作業メモリの予算
            var l = d.AddLayer("L");
            Assert.That(d.FilterRefusal(l.Id, FilterTarget.Content, FilterSettings.FromGenerator(g), PaintChannel.Normal), Does.Contain("unit vectors"));
            var small = new PaintDocument(W, H, T); var sl = small.AddLayer("S"); small.ClearHistory(); small.FilterWorkingBudgetBytes = 1000;
            Assert.That(() => small.AddFilter(sl.Id, FilterTarget.Content, FilterSettings.FromGenerator(g)), Throws.InvalidOperationException.With.Message.Contains("working memory"));
            Assert.That(sl.Filters, Is.Empty); Assert.That(small.UndoCount, Is.Zero);
        }

        // ───────── 保存 ─────────

        [Test] public void TheNativeVersionRoundTripsTheColoursAndOlderVersionsRefuseThem()
        {
            Assert.That(DocumentBinary.CurrentVersion, Is.GreaterThanOrEqualTo(14), "the ID colour generator needs a version after 13");
            var (d, fill) = MaskedFill();
            d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Ids(Red, NearBlue, 0x123456).WithIdTolerance(12).WithInvert(true)));
            d.AddFilter(fill.Id, FilterTarget.Content, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.IdColor).WithPin(MeshMapKind.Id, new string('c', 64))), new[] { PaintChannel.Color }, enabled: false);
            d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.EdgeWear)), strength: .4);
            var bytes = DocumentBinary.Write(d);
            Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(DocumentBinary.CurrentVersion));
            var read = DocumentBinary.Read(bytes); var rl = read.GetLayer(fill.Id);
            Assert.That(rl.Mask.Filters.Select(f => (f.Id, f.Settings, f.Strength)), Is.EqualTo(fill.Mask.Filters.Select(f => (f.Id, f.Settings, f.Strength))));
            Assert.That(rl.Filters.Select(f => (f.Id, f.Settings, f.Enabled)), Is.EqualTo(fill.Filters.Select(f => (f.Id, f.Settings, f.Enabled))));
            Assert.That(rl.Mask.Filters[0].Settings.Generator.IdColors, Is.EqualTo(new[] { Red, NearBlue, 0x123456 }));
            Assert.That(DocumentBinary.Write(read), Is.EqualTo(bytes), "write → read → write is byte-identical");
            var inputs = new TestGeneratorInputs().Put(IdMap()).Put(TestMeshMaps.Make(MeshMapKind.Curvature, W, H, (x, y, _) => .8));
            d.GeneratorInputs = inputs; read.GeneratorInputs = inputs;
            Assert.That(read.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            // 古い版には種類 6 が無い
            foreach (int older in new[] { 11, 13, DocumentBinary.CurrentVersion - 1 })
            {
                var old = (byte[])bytes.Clone(); BitConverter.GetBytes(older).CopyTo(old, 8);
                Assert.That(() => DocumentBinary.Read(old), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("Unknown generator type 6"), "version " + older);
            }
            // ID の色を持たない文書は版 13 と同じ並び（版の数だけが違う）
            var wear = new PaintDocument(W, H, T); var wl = wear.AddLayer("P"); wear.AddLayerMask(wl.Id);
            wear.AddFilter(wl.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Dirt)));
            var asThirteen = ArchiveTestUtil.AsVersion(DocumentBinary.Write(wear), "P", 13);
            Assert.That(BitConverter.ToInt32(asThirteen, 8), Is.EqualTo(13));
            Assert.That(DocumentBinary.Read(asThirteen).GetLayer(wl.Id).Mask.Filters.Single().Settings, Is.EqualTo(wear.GetLayer(wl.Id).Mask.Filters.Single().Settings));

            // 並び: Generator の段（ピン 0 個）の後に、許容の幅・色の数・色。壊れた値と途中で切れたものは断る（既定に戻さない）
            var one = new PaintDocument(W, H, T); var ol = one.AddLayer("O"); one.AddLayerMask(ol.Id);
            var og = one.AddFilter(ol.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Ids(Red, Green).WithIdTolerance(5)));
            var ob = DocumentBinary.Write(one);
            int at = IndexOf(ob, og.Id.ToByteArray()) + 16;
            int gen = at + 4 + 4 + 1 + 8 + 4 + 8 + 4 + 4 + 1 + 5 * 8; // Generator の段の頭
            int ids = gen + 4 + 4 + 3 * 8 + 1 + 8 + 8 + 4 + 4 + 4 + 8 + 4 + 3 * 8 + 1 + 4; // ピン 0 個の後
            Assert.That(BitConverter.ToInt32(ob, gen), Is.EqualTo((int)GeneratorType.IdColor));
            Assert.That((BitConverter.ToInt32(ob, ids), BitConverter.ToInt32(ob, ids + 4), BitConverter.ToInt32(ob, ids + 8), BitConverter.ToInt32(ob, ids + 12)), Is.EqualTo((5, 2, Red, Green)));
            Assert.That(ob.Length, Is.EqualTo(ids + 4 + 4 + 2 * 4 + 1), "the colours, then the layer's canvas-path flag and nothing else");
            void Refused(int offset, int value, string message)
            {
                var bad = (byte[])ob.Clone(); BitConverter.GetBytes(value).CopyTo(bad, offset);
                Assert.That(() => DocumentBinary.Read(bad), Throws.InstanceOf<InvalidDataException>().With.Message.Contains(message), message + " (" + value + ")");
            }
            Refused(ids, 256, "Invalid generator parameters"); Refused(ids, -1, "Invalid generator parameters");
            Refused(ids + 4, GeneratorSettings.MaxIdColors + 1, "Invalid ID colour count"); Refused(ids + 4, -1, "Invalid ID colour count");
            Refused(ids + 8, 0x1000000, "Invalid generator parameters"); Refused(ids + 12, Red, "Invalid generator parameters"); // 範囲の外・重複
            Refused(gen, 7, "Unknown generator type 7");
            for (int cut = 1; cut <= 4 + 4 + 8 + 1; cut += 3)
                Assert.That(() => DocumentBinary.Read(ob.Take(ob.Length - cut).ToArray()), Throws.InstanceOf<InvalidDataException>(), "cut " + cut);
            Assert.That(DocumentBinary.Read(ob).GetLayer(ol.Id).Mask.Filters.Single().Settings, Is.EqualTo(og.Settings));
            // 大きさを変えても（画素ではないので）そのまま
            Assert.That(d.Resampled(W * 2, H * 2, CanvasResampling.Bilinear).Document.GetLayer(fill.Id).Mask.Filters[0].Settings, Is.EqualTo(fill.Mask.Filters[0].Settings));
        }
        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++) { int k = 0; while (k < needle.Length && haystack[i + k] == needle[k]) k++; if (k == needle.Length) return i; }
            return -1;
        }
    }
}
