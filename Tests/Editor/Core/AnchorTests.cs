using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>Anchor（Core）: 層の Anchor が読む値が「その層までの合成」とバイト一致（値 × 覆い・色は輝度・覆い、グループの通過と分離、
    /// 上のクリッピングを含まない、隠した層）、マスクの Anchor が見える度合い、下の層だけを読める（依存のグラフの理由）、並べ替え・削除・
    /// グループへの出し入れで参照が上下逆・消えたら入力のまま通して理由を出し Undo で戻る、Anchor の追加・名前・削除・参照の Undo/Redo、
    /// 下の層の変更で読む層が描き直される（変更の記録はチャンネルをまたぎ halo だけ広がり、Anchor より上の読む層の出力は下の Anchor に
    /// 戻らない、連鎖も追う、キャッシュに古い値が残らない）、ネイティブの今の版の往復と古い版・壊れた値・自分の層の参照の拒否（消えた・上下逆の
    /// 参照はそのまま開く）、作業メモリとキャッシュの予算、複製・スマートマテリアル・大きさの変更で Anchor と参照が運ばれる、スレッドの数に
    /// よらない。</summary>
    public sealed class AnchorTests
    {
        const int W = 128, H = 96, T = 16;
        [TearDown] public void ResetThreads() { CoreParallelism.MaxDegreeOfParallelism = 0; }

        static byte B(double v) { double x = v * 255 + .5; return x >= 255 ? (byte)255 : x > 0 ? (byte)(int)x : (byte)0; }
        static double U(int b) => b / 255.0;
        static Dictionary<PaintChannel, Rgba32> Values(params (PaintChannel c, Rgba32 v)[] values) => values.ToDictionary(e => e.c, e => e.v);
        static Rgba32 Grey(int v, int a = 255) => new Rgba32((byte)v, (byte)v, (byte)v, (byte)a);

        static GeneratorSettings Reader(Guid anchor, PaintChannel channel = PaintChannel.Height, AnchorRead read = AnchorRead.Value)
            => GeneratorSettings.Default(GeneratorType.Anchor).WithBlend(GeneratorBlend.Replace).WithAnchor(anchor, channel, read);

        /// <summary>The base value the anchor generator reads from a composite pixel (the core's expression, in the same order).</summary>
        static double Value(Rgba32 p, PaintChannel channel, AnchorRead read)
        {
            double a = U(p.A);
            if (read == AnchorRead.Coverage) return a;
            if (channel == PaintChannel.Color || channel == PaintChannel.Emission) return (.3 * U(p.R) + .59 * U(p.G) + .11 * U(p.B)) * a;
            return U(p.R) * a;
        }

        /// <summary>Base fill (Height 128, Color grey), a painted detail layer (Height and Color with varying alpha, opacity, a blend mode and a
        /// mask) with an anchor, a cover layer above it; a probe on top whose mask reads the anchor.</summary>
        static PaintDocument Scene(out PaintLayer detail, out AnchorPoint anchor, out PaintLayer probe, out FilterEffect stage, PaintChannel channel = PaintChannel.Height, AnchorRead read = AnchorRead.Value)
        {
            var d = new PaintDocument(W, H, T);
            var bottom = d.AddFillLayer("Base", Values((PaintChannel.Height, Grey(128)), (PaintChannel.Color, new Rgba32(90, 100, 110, 255))));
            d.AddLayerMask(bottom.Id); d.FillMask(bottom.Id, 1, SelectionMask.Rectangle(d, 64, 0, 128, 96)); // 右半分は下地が無い（覆いが変わる）
            detail = d.AddLayer("Detail");
            var height = detail.GetChannel(PaintChannel.Height); var color = detail.GetChannel(PaintChannel.Color);
            for (int y = 8; y < 70; y++)
                for (int x = 10; x < 100; x++)
                {
                    height.SetPixel(x, y, Grey((x * 7 + y * 3) % 256, (x * 11 + y * 5) % 256));
                    color.SetPixel(x, y, new Rgba32((byte)(x * 2), (byte)(y * 3), (byte)((x + y) % 256), (byte)((x * 13) % 256)));
                }
            d.SetLayerOpacity(detail.Id, .8); d.SetLayerBlendMode(detail.Id, LayerBlendMode.Overlay);
            d.AddLayerMask(detail.Id); d.FillMask(detail.Id, .4, SelectionMask.Rectangle(d, 0, 0, 40, 40));
            anchor = d.AddAnchor(detail.Id, AnchorPlacement.Layer, "Details");
            var cover = d.AddLayer("Cover"); cover.GetChannel(PaintChannel.Height); d.Fill(cover.Id, PaintChannel.Height, Grey(255), 1, SelectionMask.Rectangle(d, 60, 30, 120, 90));
            probe = d.AddFillLayer("Probe", Values((PaintChannel.Color, new Rgba32(255, 255, 255, 255))));
            d.AddLayerMask(probe.Id);
            stage = d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Reader(anchor.Id, channel, read)));
            d.ClearHistory();
            return d;
        }

        /// <summary>A fresh copy (nothing cached) of the document.</summary>
        static PaintDocument Fresh(PaintDocument d) => DocumentBinary.Read(DocumentBinary.Write(d));

        /// <summary>The reference for an anchor on a top-level layer: a fresh copy with every layer above it removed, composited.</summary>
        static byte[] Below(PaintDocument d, Guid host, PaintChannel channel, Action<PaintDocument> adjust = null)
        {
            var copy = Fresh(d);
            int at = copy.Layers.ToList().FindIndex(l => l.Id == host);
            foreach (var l in copy.Layers.Skip(at + 1).Where(l => l.ParentId == Guid.Empty).ToList()) copy.RemoveLayer(l.Id);
            adjust?.Invoke(copy);
            return copy.Composite(channel);
        }

        static void AssertProbe(PaintDocument d, PaintLayer probe, byte[] reference, PaintChannel channel, AnchorRead read, string step)
        {
            var region = probe.Mask.EvaluateOutputRegion(0, 0, W, H);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 4;
                    byte expected = (byte)(255 - B(Value(new Rgba32(reference[o], reference[o + 1], reference[o + 2], reference[o + 3]), channel, read)));
                    if (probe.Mask.OutputHideAt(x, y) != expected || region[y * W + x] != expected)
                        Assert.Fail(step + ": pixel " + x + "," + y + " hides " + probe.Mask.OutputHideAt(x, y) + " (one piece " + region[y * W + x] + "), expected " + expected);
                }
        }

        // ───────────── 読む値 ─────────────

        [Test] public void ALayerAnchorHoldsTheStackThroughItsLayerByteForByte()
        {
            var d = Scene(out var detail, out var anchor, out var probe, out var stage);
            Assert.That(d.GetGeneratorStatus(probe.Id, stage.Id).Active, Is.True, d.GetGeneratorStatus(probe.Id, stage.Id).Reason);
            Assert.That(d.AnchorIssues(), Is.Empty);
            var height = Below(d, detail.Id, PaintChannel.Height);
            Assert.That(height.Where((b, i) => i % 4 == 3).Distinct().Count(), Is.GreaterThan(10), "the reference has varying coverage");
            AssertProbe(d, probe, height, PaintChannel.Height, AnchorRead.Value, "Height value");
            d.SetGeneratorAnchor(probe.Id, stage.Id, anchor.Id, PaintChannel.Height, AnchorRead.Coverage);
            AssertProbe(d, probe, height, PaintChannel.Height, AnchorRead.Coverage, "Height coverage");
            d.SetGeneratorAnchor(probe.Id, stage.Id, anchor.Id, PaintChannel.Color, AnchorRead.Value);
            AssertProbe(d, probe, Below(d, detail.Id, PaintChannel.Color), PaintChannel.Color, AnchorRead.Value, "Color luminance");
            // 合成: 見える度合いがマスクの値（参照の 1 画素ずつの式と一致）
            var composite = d.Composite(PaintChannel.Color);
            for (int y = 0; y < H; y += 7) for (int x = 0; x < W; x += 5)
                {
                    int o = (y * W + x) * 4;
                    Assert.That(new Rgba32(composite[o], composite[o + 1], composite[o + 2], composite[o + 3]), Is.EqualTo(d.CompositePixel(PaintChannel.Color, x, y)), x + "," + y);
                }
            Assert.That(composite, Is.EqualTo(Fresh(d).Composite(PaintChannel.Color)), "a fresh copy composites the same");
        }

        [Test] public void TheAnchorFollowsGroupsAndClippingAsTheStackDoes()
        {
            var d = new PaintDocument(W, H, T);
            d.AddFillLayer("Bottom", Values((PaintChannel.Height, Grey(60))));
            var a = d.AddLayer("A"); a.GetChannel(PaintChannel.Height); d.Fill(a.Id, PaintChannel.Height, Grey(200, 160), 1, SelectionMask.Rectangle(d, 0, 0, 70, 96));
            var host = d.AddLayer("Host"); host.GetChannel(PaintChannel.Height); d.Fill(host.Id, PaintChannel.Height, Grey(30, 200), 1, SelectionMask.Rectangle(d, 40, 20, 128, 80));
            d.SetLayerBlendMode(host.Id, LayerBlendMode.Multiply);
            var clip = d.AddLayer("Clip"); clip.GetChannel(PaintChannel.Height); d.Fill(clip.Id, PaintChannel.Height, Grey(255), 1, SelectionMask.Rectangle(d, 0, 0, 128, 96));
            d.SetLayerClipping(clip.Id, true);
            var group = d.GroupLayers(new[] { a.Id, host.Id, clip.Id }, "G");
            d.SetLayerOpacity(group.Id, .3); d.AddLayerMask(group.Id); d.FillMask(group.Id, 1, SelectionMask.Rectangle(d, 0, 0, 64, 96));
            var anchor = d.AddAnchor(host.Id);
            var probe = d.AddFillLayer("Probe", Values((PaintChannel.Color, new Rgba32(255, 255, 255, 255)))); d.AddLayerMask(probe.Id);
            var stage = d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Reader(anchor.Id)));
            Assert.That(d.GetGeneratorStatus(probe.Id, stage.Id).Active, Is.True);
            // 参照: グループの不透明度とマスクは掛けず、上のクリッピングは含めない
            byte[] Reference(bool isolated) => Below(d, group.Id, PaintChannel.Height, c =>
            {
                c.RemoveLayer(clip.Id); c.SetLayerOpacity(group.Id, 1); c.RemoveLayerMask(group.Id);
                if (isolated) c.RemoveLayer(c.Layers[0].Id); // 分離: グループの外（下）は入らない
            });
            Assert.That(group.BlendMode, Is.EqualTo(LayerBlendMode.PassThrough));
            AssertProbe(d, probe, Reference(false), PaintChannel.Height, AnchorRead.Value, "pass-through group continues the stack below");
            d.SetLayerBlendMode(group.Id, LayerBlendMode.Normal);
            AssertProbe(d, probe, Reference(true), PaintChannel.Height, AnchorRead.Value, "isolated group starts from transparent");
            // クリッピングされた層の Anchor: 下地と下のクリッピングまで
            d.SetLayerBlendMode(group.Id, LayerBlendMode.PassThrough);
            d.RemoveAnchor(anchor.Id);
            var top = d.AddLayer("Top", above: clip.Id); top.GetChannel(PaintChannel.Height); d.Fill(top.Id, PaintChannel.Height, Grey(0), 1, SelectionMask.Rectangle(d, 0, 0, 128, 96));
            d.SetLayerClipping(top.Id, true);
            var onClip = d.AddAnchor(clip.Id);
            d.SetGeneratorAnchor(probe.Id, stage.Id, onClip.Id, PaintChannel.Height, AnchorRead.Value);
            AssertProbe(d, probe, Below(d, group.Id, PaintChannel.Height, c => { c.RemoveLayer(top.Id); c.SetLayerOpacity(group.Id, 1); c.RemoveLayerMask(group.Id); }), PaintChannel.Height, AnchorRead.Value, "clipped layer");
            // 隠した層: 何も足さない（下だけ）
            d.SetLayerVisibility(clip.Id, false);
            AssertProbe(d, probe, Below(d, group.Id, PaintChannel.Height, c => { c.RemoveLayer(top.Id); c.RemoveLayer(clip.Id); c.SetLayerOpacity(group.Id, 1); c.RemoveLayerMask(group.Id); }), PaintChannel.Height, AnchorRead.Value, "hidden layer");
        }

        [Test] public void AMaskAnchorGivesHowMuchItsLayerShows()
        {
            var d = new PaintDocument(W, H, T);
            var host = d.AddFillLayer("Host", Values((PaintChannel.Color, new Rgba32(10, 20, 30, 255))));
            var mask = d.AddLayerMask(host.Id);
            d.FillMask(host.Id, .7, SelectionMask.Ellipse(d, 50, 40, 30, 20));
            d.AddFilter(host.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(3));
            d.SetLayerMaskDensity(host.Id, .6); d.SetLayerMaskInverted(host.Id, true);
            var anchor = d.AddAnchor(host.Id, AnchorPlacement.Mask);
            Assert.That(anchor.Name, Is.EqualTo("Host (mask)"));
            var probe = d.AddFillLayer("Probe", Values((PaintChannel.Color, new Rgba32(255, 255, 255, 255)))); d.AddLayerMask(probe.Id);
            var stage = d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Reader(anchor.Id, PaintChannel.Color, AnchorRead.Coverage)));
            void Check(string step)
            {
                for (int y = 0; y < H; y += 3) for (int x = 0; x < W; x += 3)
                        Assert.That(probe.Mask.OutputHideAt(x, y), Is.EqualTo((byte)(255 - B(mask.Factor(mask.OutputHideAt(x, y))))), step + " " + x + "," + y);
            }
            Check("filtered, inverted, density");
            d.SetLayerMaskEnabled(host.Id, false); Check("mask off: shows everywhere");
            Assert.That(probe.Mask.OutputHideAt(50, 40), Is.Zero);
            d.Undo(); Check("undo");
            d.FillMask(host.Id, 1, SelectionMask.Rectangle(d, 100, 0, 128, 20)); Check("painted mask");
        }

        // ───────────── 下の層だけ・理由 ─────────────

        [Test] public void OnlyLayersAboveAnAnchorCanReadItAndTheGraphSaysWhy()
        {
            var d = Scene(out var detail, out var anchor, out var probe, out var stage);
            Assert.That(d.AnchorReferenceRefusal(probe.Id, anchor.Id), Is.Null);
            Assert.That(d.AnchorReferenceRefusal(detail.Id, anchor.Id), Does.Contain("own layer"));
            var bottom = d.Layers[0];
            Assert.That(d.AnchorReferenceRefusal(bottom.Id, anchor.Id), Does.Contain("not below"));
            Assert.That(d.AnchorReferenceRefusal(probe.Id, Guid.NewGuid()), Does.Contain("gone"));
            Assert.That(d.AnchorsReadableFrom(probe.Id).Select(a => a.Anchor.Id), Is.EqualTo(new[] { anchor.Id }));
            Assert.That(d.AnchorsReadableFrom(bottom.Id), Is.Empty);
            // 選べない参照は断り、何も変えない
            var low = d.AddFilter(bottom.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Anchor)));
            int undo = d.UndoCount;
            Assert.That(() => d.SetGeneratorAnchor(bottom.Id, low.Id, anchor.Id, PaintChannel.Height, AnchorRead.Value), Throws.InvalidOperationException.With.Message.Contains("not below"));
            Assert.That(d.UndoCount, Is.EqualTo(undo)); Assert.That(bottom.Mask.Filters.Single().Settings.Generator.AnchorId, Is.EqualTo(Guid.Empty));
            var notChosen = d.AnchorIssues().Single();
            Assert.That(notChosen.Kind, Is.EqualTo(AnchorIssueKind.NotChosen)); Assert.That(d.GetGeneratorStatus(bottom.Id, low.Id).Active, Is.False);
            // 型: Normal は 1 画素 1 値ではない
            Assert.That(() => GeneratorSettings.Default(GeneratorType.Anchor).WithAnchor(anchor.Id, PaintChannel.Normal, AnchorRead.Value), Throws.ArgumentException.With.Message.Contains("unit vectors"));
            Assert.That(() => GeneratorSettings.Default(GeneratorType.EdgeWear).WithAnchor(anchor.Id, PaintChannel.Height, AnchorRead.Value), Throws.ArgumentException);
            Assert.That(FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Anchor)).RefusalFor(PaintChannel.Normal), Is.Not.Null, "not on the Normal channel either");
            // 同じ層の段に、その層の Anchor を書いた段は（グラフが）断る
            var self = d.AddFilter(detail.Id, FilterTarget.Content, FilterSettings.FromGenerator(Reader(anchor.Id)), new[] { PaintChannel.Height });
            var issue = d.AnchorIssues().Single(i => i.FilterId == self.Id);
            Assert.That(issue.Kind, Is.EqualTo(AnchorIssueKind.NotBelow)); Assert.That(issue.GraphReason, Does.Contain("lower layer"));
            Assert.That(d.GetGeneratorStatus(detail.Id, self.Id).Reason, Does.Contain("own layer"));
            Assert.That(d.InactiveGenerators().Count, Is.EqualTo(2), "listed before exports");
            Assert.That(() => d.BakeFilters(detail.Id), Throws.InvalidOperationException.With.Message.Contains("cannot be baked"));
        }

        [Test] public void MovingDeletingAndGroupingPassTheInputThroughWithAReasonAndUndoRestores()
        {
            var d = Scene(out var detail, out var anchor, out var probe, out var stage);
            var good = probe.Mask.EvaluateOutputRegion(0, 0, W, H);
            Assert.That(good.Distinct().Count(), Is.GreaterThan(10));
            void PassesThrough(string step, AnchorIssueKind kind)
            {
                var issue = d.AnchorIssues().Single();
                Assert.That(issue.Kind, Is.EqualTo(kind), step); Assert.That(issue.FilterId, Is.EqualTo(stage.Id));
                Assert.That(d.GetGeneratorStatus(probe.Id, stage.Id).Active, Is.False, step);
                Assert.That(probe.Mask.EvaluateOutputRegion(0, 0, W, H), Is.All.EqualTo((byte)0), step + ": the empty mask passes through");
                Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(Fresh(d).Composite(PaintChannel.Color)), step + ": no stale tile");
            }
            void Works(string step)
            {
                Assert.That(d.AnchorIssues(), Is.Empty, step);
                Assert.That(probe.Mask.EvaluateOutputRegion(0, 0, W, H), Is.EqualTo(good), step);
                Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(Fresh(d).Composite(PaintChannel.Color)), step + ": no stale tile");
            }
            d.MoveLayer(detail.Id, d.Layers.Count - 1); // 読む層より上へ
            PassesThrough("moved above the reader", AnchorIssueKind.NotBelow);
            Assert.That(d.AnchorIssues().Single().GraphReason, Does.Contain("lower layer"));
            Assert.That(d.AnchorIssues().Single().Reason, Does.Contain("not below").And.Contain("Detail").And.Contain("Probe"));
            d.Undo(); Works("undo the move");
            d.RemoveLayer(detail.Id); PassesThrough("layer deleted", AnchorIssueKind.Missing);
            d.Undo(); Works("undo the delete");
            d.RemoveAnchor(anchor.Id); PassesThrough("anchor removed", AnchorIssueKind.Missing);
            d.Undo(); Works("undo the removal");
            // グループに入れても下のまま（通過のグループは外の続き）
            var g = d.GroupLayers(new[] { detail.Id }, "G"); Works("grouped (pass through)");
            d.MoveLayerTo(probe.Id, g.Id, 0); PassesThrough("reader moved into the group below the anchor", AnchorIssueKind.NotBelow);
            d.Undo(); Works("undo");
            d.Undo(); Works("ungrouped by undo");
            d.Redo(); d.Redo(); PassesThrough("redo", AnchorIssueKind.NotBelow);
        }

        // ───────────── Undo/Redo ─────────────

        [Test] public void AnchorEditsAreUndoableStepsAndKeepTheirId()
        {
            var d = new PaintDocument(W, H, T);
            var l = d.AddLayer("L"); d.ClearHistory();
            var a = d.AddAnchor(l.Id, name: "Height details");
            Assert.That(d.UndoCount, Is.EqualTo(1)); Assert.That(l.Anchor, Is.SameAs(a)); Assert.That(d.FindAnchor(a.Id).Layer, Is.SameAs(l));
            Assert.That(() => d.AddAnchor(l.Id), Throws.InvalidOperationException.With.Message.Contains("already has an anchor"));
            Assert.That(() => d.AddAnchor(l.Id, AnchorPlacement.Mask), Throws.InvalidOperationException.With.Message.Contains("no mask"));
            Assert.That(() => d.AddAnchor(d.AddLayer("M").Id, name: " "), Throws.ArgumentException);
            Assert.That(() => d.AddAnchor(d.Layers.Last().Id, name: new string('x', AnchorPoint.MaxNameLength + 1)), Throws.ArgumentException);
            d.Undo(); // M を足した
            d.Undo(); Assert.That(l.Anchor, Is.Null); Assert.That(d.Anchors, Is.Empty);
            d.Redo(); Assert.That(l.Anchor.Id, Is.EqualTo(a.Id));
            d.RenameAnchor(a.Id, "Wear source"); Assert.That(a.Name, Is.EqualTo("Wear source"));
            d.Undo(); Assert.That(a.Name, Is.EqualTo("Height details")); d.Redo();
            var probe = d.AddFillLayer("P", Values((PaintChannel.Color, new Rgba32(1, 2, 3, 255)))); d.AddLayerMask(probe.Id);
            var stage = d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Anchor)));
            d.SetGeneratorAnchor(probe.Id, stage.Id, a.Id, PaintChannel.Roughness, AnchorRead.Coverage);
            Assert.That(d.AnchorReaders(a.Id).Single().stage.Id, Is.EqualTo(stage.Id));
            d.Undo(); Assert.That(probe.Mask.Filters.Single().Settings.Generator.AnchorId, Is.EqualTo(Guid.Empty));
            d.Redo(); var g = probe.Mask.Filters.Single().Settings.Generator;
            Assert.That((g.AnchorId, g.AnchorChannel, g.AnchorRead), Is.EqualTo((a.Id, PaintChannel.Roughness, AnchorRead.Coverage)));
            d.AddLayerMask(l.Id); var m = d.AddAnchor(l.Id, AnchorPlacement.Mask, "L mask");
            Assert.That(d.Anchors.Select(i => i.Anchor.Id), Is.EqualTo(new[] { a.Id, m.Id }), "bottom to top, layer before mask");
            d.RemoveLayerMask(l.Id); Assert.That(d.FindAnchor(m.Id), Is.Null, "the mask anchor goes with its mask");
            d.Undo(); Assert.That(d.FindAnchor(m.Id).Anchor, Is.SameAs(m));
            d.SetLayerLocks(l.Id, LayerLocks.All);
            Assert.That(() => d.RenameAnchor(a.Id, "x"), Throws.Nothing, "renaming changes nothing drawn");
            Assert.That(() => d.RemoveAnchor(a.Id), Throws.InstanceOf<LayerLockedException>());
        }

        // ───────────── 変更の記録 ─────────────

        [Test] public void ChangesBelowTheAnchorRedrawTheReaderAcrossChannelsWithinItsHalo()
        {
            var d = Scene(out var detail, out var anchor, out var probe, out var stage);
            d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(20)); // Anchor の後のぼかし: 変化は 2 タイルまで広がる
            d.Composite(PaintChannel.Color);
            long since = d.ChangeSerial;
            d.Fill(detail.Id, PaintChannel.Height, Grey(255), 1, SelectionMask.Rectangle(d, 20, 20, 22, 22)); // タイル (1, 1)
            var changed = new HashSet<TileCoord>();
            Assert.That(d.TryGetChangedTiles(PaintChannel.Color, since, changed), Is.True);
            var expected = new HashSet<TileCoord>();
            for (int y = 0; y <= 3; y++) for (int x = 0; x <= 3; x++) expected.Add(new TileCoord(x, y));
            Assert.That(changed, Is.EquivalentTo(expected), "the Height change reaches the Color channel through the reader, grown by its 20 px blur");
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(Fresh(d).Composite(PaintChannel.Color)), "the reader was evaluated again (no stale cached tile)");
            // 読む層自身の Color を変えても、ほかへは広がらない（Anchor は Height を読む）
            since = d.ChangeSerial; changed.Clear();
            d.SetFillValue(probe.Id, PaintChannel.Color, new Rgba32(200, 10, 10, 255));
            d.TryGetChangedTiles(PaintChannel.Height, since, changed);
            Assert.That(changed, Is.Empty, "the reader's own Color does not change Height");
            // Anchor の上の層の Height は Anchor の値を変えない: 読む段の出力は同じバイト
            var before = probe.Mask.EvaluateOutputRegion(0, 0, W, H);
            var cover = d.Layers.First(l => l.Name == "Cover");
            d.Fill(cover.Id, PaintChannel.Height, Grey(0), 1, SelectionMask.Rectangle(d, 0, 0, 128, 96));
            Assert.That(probe.Mask.EvaluateOutputRegion(0, 0, W, H), Is.EqualTo(before));
            // Undo も同じ
            d.Undo(); d.Undo(); d.Undo();
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(Fresh(d).Composite(PaintChannel.Color)), "after undo");
        }

        [Test] public void AReaderAboveAnAnchorDoesNotFeedTheAnchorsBelowIt()
        {
            // Height を読み Height に書く層（ぼかし付き）: 自分の出力の変化が、下の Anchor の読む値に戻って広がり続けない
            var d = new PaintDocument(512, 512, 16);
            var paint = d.AddLayer("Paint"); paint.GetChannel(PaintChannel.Height);
            var a = d.AddAnchor(paint.Id);
            var blend = d.AddFillLayer("Blend", Values((PaintChannel.Height, Grey(100))));
            d.AddFilter(blend.Id, FilterTarget.Content, FilterSettings.FromGenerator(Reader(a.Id).WithBlend(GeneratorBlend.Add)), new[] { PaintChannel.Height });
            d.AddFilter(blend.Id, FilterTarget.Content, FilterSettings.GaussianBlur(16), new[] { PaintChannel.Height });
            d.Composite(PaintChannel.Height);
            long since = d.ChangeSerial;
            d.Fill(paint.Id, PaintChannel.Height, Grey(255), 1, SelectionMask.Rectangle(d, 250, 250, 252, 252)); // タイル (15, 15)
            var changed = new HashSet<TileCoord>(); d.TryGetChangedTiles(PaintChannel.Height, since, changed);
            Assert.That(changed.Count, Is.EqualTo(9), "the tile and the blur's one-tile halo, not the whole 32 × 32 tiles");
            Assert.That(d.Composite(PaintChannel.Height), Is.EqualTo(Fresh(d).Composite(PaintChannel.Height)));
        }

        [Test] public void ChainsOfAnchorsAreFollowed()
        {
            var d = new PaintDocument(W, H, T);
            d.AddFillLayer("Base", Values((PaintChannel.Height, Grey(40))));
            var l1 = d.AddLayer("L1"); l1.GetChannel(PaintChannel.Height);
            var a1 = d.AddAnchor(l1.Id);
            var l2 = d.AddFillLayer("L2", Values((PaintChannel.Height, Grey(50)), (PaintChannel.Roughness, Grey(10))));
            d.AddFilter(l2.Id, FilterTarget.Content, FilterSettings.FromGenerator(Reader(a1.Id).WithBlend(GeneratorBlend.Add)), new[] { PaintChannel.Height });
            var a2 = d.AddAnchor(l2.Id);
            var probe = d.AddFillLayer("Probe", Values((PaintChannel.Color, new Rgba32(255, 255, 255, 255)))); d.AddLayerMask(probe.Id);
            d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Reader(a2.Id)));
            d.Composite(PaintChannel.Color); long since = d.ChangeSerial;
            d.Fill(l1.Id, PaintChannel.Height, Grey(255, 128), 1, SelectionMask.Rectangle(d, 70, 50, 74, 54)); // タイル (4, 3)
            var changed = new HashSet<TileCoord>(); d.TryGetChangedTiles(PaintChannel.Color, since, changed);
            Assert.That(changed, Is.EquivalentTo(new[] { new TileCoord(4, 3) }), "L1 → L2's Height → anchor 2 → the probe's mask (Color)");
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(Fresh(d).Composite(PaintChannel.Color)));
            Assert.That(probe.Mask.OutputHideAt(72, 52), Is.Not.EqualTo(probe.Mask.OutputHideAt(10, 10)), "the change shows through two anchors");
        }

        // ───────────── 保存 ─────────────

        [Test] public void NativeVersionRoundTripsAnchorsAndRefusesBrokenOnes()
        {
            var d = Scene(out var detail, out var anchor, out var probe, out var stage, PaintChannel.Emission, AnchorRead.Coverage);
            var host = d.Layers.First(l => l.Name == "Cover"); d.AddLayerMask(host.Id); var maskAnchor = d.AddAnchor(host.Id, AnchorPlacement.Mask, "Cover mask ✓");
            var second = d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Reader(maskAnchor.Id).WithBlend(GeneratorBlend.Multiply)), strength: .5);
            var bytes = DocumentBinary.Write(d);
            Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(DocumentBinary.CurrentVersion));
            var read = DocumentBinary.Read(bytes);
            Assert.That(read.Anchors.Select(i => (i.Anchor.Id, i.Anchor.Name, i.Anchor.Placement, i.Layer.Id)), Is.EqualTo(d.Anchors.Select(i => (i.Anchor.Id, i.Anchor.Name, i.Anchor.Placement, i.Layer.Id))));
            Assert.That(read.GetLayer(probe.Id).Mask.Filters.Select(f => f.Settings), Is.EqualTo(probe.Mask.Filters.Select(f => f.Settings)));
            Assert.That(DocumentBinary.Write(read), Is.EqualTo(bytes), "write → read → write is byte-identical");
            Assert.That(read.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            Assert.That(read.CanUndo, Is.False);

            // Anchor の版 20 は勾配の導入後もそのまま読む。Anchor の導入前と偽ると印・種類 7 を断る。
            var anchorVersion = (byte[])bytes.Clone(); BitConverter.GetBytes(20).CopyTo(anchorVersion, 8);
            Assert.That(DocumentBinary.Write(DocumentBinary.Read(anchorVersion)), Is.EqualTo(bytes), "版20のAnchorを現行版へ上げても編集情報を保つ");
            int before = 19;
            var plain = new PaintDocument(W, H, T); var pl = plain.AddLayer("P"); pl.GetChannel(PaintChannel.Color).SetPixel(3, 4, new Rgba32(5, 6, 7, 8));
            Assert.That(DocumentBinary.Read(ArchiveTestUtil.AsVersion(DocumentBinary.Write(plain), "P", before)).Composite(PaintChannel.Color), Is.EqualTo(plain.Composite(PaintChannel.Color)));
            var asBefore = (byte[])bytes.Clone(); BitConverter.GetBytes(before).CopyTo(asBefore, 8);
            Assert.That(() => DocumentBinary.Read(asBefore), Throws.InstanceOf<InvalidDataException>());
            var onlyType7 = new PaintDocument(W, H, T); var ol = onlyType7.AddLayer("O"); onlyType7.AddLayerMask(ol.Id);
            onlyType7.AddFilter(ol.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Anchor)));
            var t7 = DocumentBinary.Write(onlyType7); BitConverter.GetBytes(before).CopyTo(t7, 8);
            Assert.That(() => DocumentBinary.Read(t7), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("Unknown generator type 7"));

            // 壊れた値: Anchor の印・空の ID・同じ ID 2 つ・知らないチャンネルと読み方・Normal
            var one = new PaintDocument(W, H, T); var l = one.AddLayer("A"); var a = one.AddAnchor(l.Id, name: "N");
            var ob = DocumentBinary.Write(one);
            int at = IndexOf(ob, a.Id.ToByteArray());
            Assert.That(ob[at - 1], Is.EqualTo(1), "the anchor flags precede the ID");
            void Refused(byte[] bad, string message) => Assert.That(() => DocumentBinary.Read(bad), Throws.InstanceOf<InvalidDataException>().With.Message.Contains(message), message);
            var flags = (byte[])ob.Clone(); flags[at - 1] = 4; Refused(flags, "Unknown anchor flags 4");
            flags = (byte[])ob.Clone(); flags[at - 1] = 0; Refused(flags, "Unknown anchor flags 0");
            flags = (byte[])ob.Clone(); flags[at - 1] = 2; Refused(flags, "no mask");
            var empty = (byte[])ob.Clone(); Array.Clear(empty, at, 16); Refused(empty, "has no ID");
            var noName = (byte[])ob.Clone(); BitConverter.GetBytes(0).CopyTo(noName, at + 16); Refused(noName.Take(at + 20).ToArray(), "Invalid anchor");
            var two = new PaintDocument(W, H, T); var t1 = two.AddLayer("A"); var t2 = two.AddLayer("B"); var ta = two.AddAnchor(t1.Id, name: "N"); var tb = two.AddAnchor(t2.Id, name: "N");
            var tw = DocumentBinary.Write(two); ta.Id.ToByteArray().CopyTo(tw, IndexOf(tw, tb.Id.ToByteArray()));
            Refused(tw, "Duplicate anchor ID");
            var gen = DocumentBinary.Write(onlyType7);
            int ref0 = gen.Length - 1 - 4 - 4 - 16; // 層の終わり（2D パスの印 1 バイト）の前が Generator の Anchor の欄（ID・チャンネル・読み方）
            Assert.That(gen.Skip(ref0).Take(16), Is.All.EqualTo((byte)0), "no anchor chosen: an empty ID");
            Assert.That(BitConverter.ToInt32(gen, ref0 + 16), Is.EqualTo((int)PaintChannel.Height));
            void RefusedAt(int offset, int value, string message) { var bad = (byte[])gen.Clone(); BitConverter.GetBytes(value).CopyTo(bad, offset); Refused(bad, message); }
            RefusedAt(ref0 + 16, 9, "Unknown channel 9");
            RefusedAt(ref0 + 16, (int)PaintChannel.Normal, "unit vectors");
            RefusedAt(ref0 + 20, 5, "Unknown anchor read 5");
            // 自分の層の Anchor を読む段（どの編集でも作れない）は断る
            var selfDoc = new PaintDocument(W, H, T); var sl = selfDoc.AddLayer("S"); selfDoc.AddLayerMask(sl.Id); var sa = selfDoc.AddAnchor(sl.Id);
            selfDoc.AddFilter(sl.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Reader(sa.Id)));
            Refused(DocumentBinary.Write(selfDoc), "its own layer");
            // 消えた・上下逆の参照（利用者の操作で作れる）はそのまま開き、理由を出す
            var broken = Scene(out var bd, out var ba, out var bp, out var bs);
            broken.MoveLayer(bd.Id, broken.Layers.Count - 1);
            var reopened = DocumentBinary.Read(DocumentBinary.Write(broken));
            Assert.That(reopened.AnchorIssues().Single().Kind, Is.EqualTo(AnchorIssueKind.NotBelow));
            broken.RemoveLayer(bd.Id);
            Assert.That(DocumentBinary.Read(DocumentBinary.Write(broken)).AnchorIssues().Single().Kind, Is.EqualTo(AnchorIssueKind.Missing));
        }
        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++) { int k = 0; while (k < needle.Length && haystack[i + k] == needle[k]) k++; if (k == needle.Length) return i; }
            throw new AssertionException("not found");
        }

        // ───────────── 予算 ─────────────

        [Test] public void TheWorkingBudgetCountsAnchorTilesAndTheCacheStaysWithinItsBudget()
        {
            var d = Scene(out var detail, out var anchor, out var probe, out var stage);
            var expected = probe.Mask.EvaluateOutputRegion(0, 0, W, H);
            Assert.That(d.AnchorCacheBytes, Is.GreaterThan(0));
            // キャッシュの予算: 0 なら持たずに毎回作る（同じバイト）、小さければその内側
            d.AnchorCacheBudgetBytes = 0; Assert.That(d.AnchorCacheBytes, Is.Zero);
            var fresh = Fresh(d); fresh.AnchorCacheBudgetBytes = 0;
            Assert.That(fresh.GetLayer(probe.Id).Mask.EvaluateOutputRegion(0, 0, W, H), Is.EqualTo(expected));
            Assert.That(fresh.AnchorCacheBytes, Is.Zero);
            var small = Fresh(d); small.AnchorCacheBudgetBytes = 3L * T * T * 4;
            small.Composite(PaintChannel.Color);
            Assert.That(small.AnchorCacheBytes, Is.LessThanOrEqualTo(3L * T * T * 4));
            Assert.That(small.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            Assert.That(() => d.AnchorCacheBudgetBytes = -1, Throws.InstanceOf<ArgumentOutOfRangeException>());
            // 作業メモリ: Anchor の段は評価のあいだタイルを持つので、その分も数えて予算を超える段は足す前に断る
            var tight = new PaintDocument(512, 512, 16) { FilterBlockPixels = 256 };
            var host = tight.AddLayer("H"); var a = tight.AddAnchor(host.Id);
            var reader = tight.AddFillLayer("R", Values((PaintChannel.Color, new Rgba32(1, 1, 1, 255)))); tight.AddLayerMask(reader.Id);
            tight.AddFilter(reader.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(4));
            Assert.That(() => tight.FilterWorkingBudgetBytes = 1, Throws.InstanceOf<ArgumentOutOfRangeException>().With.Message.Contains("MiB per block"));
            // 今の段がちょうど入る予算（受け付ける一番小さい値）にする
            long low = 1, high = PaintDocument.DefaultFilterWorkingBudgetBytes;
            while (low < high) { long mid = (low + high) / 2; try { tight.FilterWorkingBudgetBytes = mid; high = mid; } catch (ArgumentOutOfRangeException) { low = mid + 1; } }
            tight.FilterWorkingBudgetBytes = low;
            Assert.That(() => tight.AddFilter(reader.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Reader(a.Id)), index: 0),
                Throws.InvalidOperationException.With.Message.Contains("working memory"), "the anchor tiles do not fit the budget");
            Assert.That(reader.Mask.Filters.Count, Is.EqualTo(1), "nothing was added");
            tight.FilterWorkingBudgetBytes = PaintDocument.DefaultFilterWorkingBudgetBytes;
            Assert.That(() => tight.AddFilter(reader.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Reader(a.Id)), index: 0), Throws.Nothing);
        }

        // ───────────── 運ぶ ─────────────

        [Test] public void DuplicatesSmartMaterialsAndResizingCarryAnchorsAndTheirReferences()
        {
            var d = Scene(out var detail, out var anchor, out var probe, out var stage);
            var shown = d.Composite(PaintChannel.Color);
            // 複製: Anchor と読む段の組を丸ごと写すと、写しは写しの Anchor を読む
            var group = d.GroupLayers(new[] { detail.Id, d.Layers.First(l => l.Name == "Cover").Id, probe.Id }, "Set");
            d.SetLayerBlendMode(group.Id, LayerBlendMode.Normal); // 分離: 中の Anchor はグループの中だけ（写しも同じ値になる）
            var copy = d.DuplicateLayer(group.Id);
            var copyDetail = d.Layers.Last(l => l.Name == "Detail" && l.Id != detail.Id); var copyProbe = d.Layers.Last(l => l.Name == "Probe" && l.Id != probe.Id);
            Assert.That(copyDetail.Anchor.Id, Is.Not.EqualTo(anchor.Id)); Assert.That(copyDetail.Anchor.Name, Is.EqualTo(anchor.Name));
            Assert.That(copyProbe.Mask.Filters.Single().Settings.Generator.AnchorId, Is.EqualTo(copyDetail.Anchor.Id));
            Assert.That(d.AnchorIssues(), Is.Empty);
            Assert.That(copyProbe.Mask.EvaluateOutputRegion(0, 0, W, H), Is.EqualTo(probe.Mask.EvaluateOutputRegion(0, 0, W, H)), "the copy reads its own anchor (the same stack below)");
            d.MoveLayer(copy.Id, 0); // 写しを下へ: 写しの中の参照は写しの中なので、そのまま使える
            Assert.That(d.AnchorIssues(), Is.Empty);
            d.Undo(); d.Undo(); d.Undo(); d.Undo();
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(shown));
            // スマートマテリアル: 選んだ層の中の参照は置いた Anchor へ
            var smart = d.CaptureSmartMaterial(new[] { detail.Id, probe.Id }, "Wear by height");
            var placed = d.PlaceSmartMaterial(smart);
            var placedDetail = d.GetLayer(placed.Layers.First(id => d.GetLayer(id).Name == "Detail")); var placedProbe = d.GetLayer(placed.Layers.First(id => d.GetLayer(id).Name == "Probe"));
            Assert.That(placedProbe.Mask.Filters.Single().Settings.Generator.AnchorId, Is.EqualTo(placedDetail.Anchor.Id));
            Assert.That(placedDetail.Anchor.Id, Is.Not.EqualTo(anchor.Id).And.Not.EqualTo(Guid.Empty));
            Assert.That(d.AnchorIssues(), Is.Empty, string.Join("\n", d.AnchorIssues()));
            d.Undo();
            // 大きさの変更: 同じ ID のまま、読む段も同じ参照で、全面を描き直したものと同じ
            var resized = d.Resampled(W * 2, H * 2, CanvasResampling.Nearest).Document;
            Assert.That(resized.Anchors.Select(i => i.Anchor.Id), Is.EqualTo(d.Anchors.Select(i => i.Anchor.Id)));
            Assert.That(resized.AnchorIssues(), Is.Empty);
            Assert.That(resized.Composite(PaintChannel.Color), Is.EqualTo(Fresh(resized).Composite(PaintChannel.Color)));
        }

        [Test] public void MergingKeepsTheAnchorsWhoseValueItKeeps()
        {
            var d = new PaintDocument(W, H, T);
            d.AddFillLayer("Base", Values((PaintChannel.Height, Grey(60))));
            var lower = d.AddLayer("Lower"); lower.GetChannel(PaintChannel.Height); d.Fill(lower.Id, PaintChannel.Height, Grey(200, 180), 1, SelectionMask.Rectangle(d, 0, 0, 80, 96));
            d.AddLayerMask(lower.Id); d.FillMask(lower.Id, .5, SelectionMask.Rectangle(d, 0, 0, 30, 30));
            var upper = d.AddLayer("Upper"); upper.GetChannel(PaintChannel.Height); d.Fill(upper.Id, PaintChannel.Height, Grey(20, 150), 1, SelectionMask.Rectangle(d, 40, 20, 128, 70));
            var onLower = d.AddAnchor(lower.Id); var onUpper = d.AddAnchor(upper.Id); var onMask = d.AddAnchor(lower.Id, AnchorPlacement.Mask);
            var probe = d.AddFillLayer("Probe", Values((PaintChannel.Color, new Rgba32(255, 255, 255, 255)))); d.AddLayerMask(probe.Id);
            var readsUpper = d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Reader(onUpper.Id)));
            var readsLower = d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Reader(onLower.Id).WithBlend(GeneratorBlend.Multiply)), enabled: false);
            var readsMask = d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Reader(onMask.Id).WithBlend(GeneratorBlend.Multiply)));
            var before = probe.Mask.EvaluateOutputRegion(0, 0, W, H);
            var report = d.MergeDown(upper.Id);
            var merged = d.Layers.Single(l => l.Name == "Lower");
            Assert.That(merged.Anchor, Is.SameAs(onUpper), "the stack through the merged layer is the stack through the upper one");
            Assert.That(merged.Mask.Anchor, Is.SameAs(onMask), "the kept mask keeps its anchor");
            Assert.That(d.AnchorIssues().Select(i => (i.FilterId, i.Kind)), Is.EqualTo(new[] { (readsLower.Id, AnchorIssueKind.Missing) }), "the lower layer's own stack is gone");
            var after = probe.Mask.EvaluateOutputRegion(0, 0, W, H);
            Assert.That(after.Select((v, i) => Math.Abs(v - before[i])).Max(), Is.LessThanOrEqualTo(PaintDocument.MergeRoundingTolerance), "the readers that still read see the same values, but for the merge's rounding");
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(Fresh(d).Composite(PaintChannel.Color)));
            d.Undo();
            Assert.That(lower.Anchor, Is.SameAs(onLower)); Assert.That(upper.Anchor, Is.SameAs(onUpper)); Assert.That(d.AnchorIssues(), Is.Empty);
            // グループの結合: グループの Anchor は結果へ
            var g = d.GroupLayers(new[] { lower.Id, upper.Id }, "G"); d.SetLayerBlendMode(g.Id, LayerBlendMode.Normal);
            var onGroup = d.AddAnchor(g.Id);
            d.SetGeneratorAnchor(probe.Id, readsUpper.Id, onGroup.Id, PaintChannel.Height, AnchorRead.Value);
            d.SetFilterEnabled(probe.Id, readsMask.Id, false); // 中の層のマスクの Anchor は結合で無くなる（理由は下で確かめる）
            before = probe.Mask.EvaluateOutputRegion(0, 0, W, H);
            d.MergeGroup(g.Id);
            Assert.That(d.Layers.Single(l => l.Name == "G").Anchor, Is.SameAs(onGroup));
            after = probe.Mask.EvaluateOutputRegion(0, 0, W, H);
            Assert.That(after.Select((v, i) => Math.Abs(v - before[i])).Max(), Is.LessThanOrEqualTo(PaintDocument.MergeRoundingTolerance));
            Assert.That(d.AnchorIssues().Select(i => i.Kind), Is.All.EqualTo(AnchorIssueKind.Missing), "the anchors inside the group are gone, with a reason");
        }

        [Test] public void TheReducedPreviewOfAReaderIsTheFullCompositeSampled()
        {
            var d = Scene(out _, out _, out var probe, out _);
            d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(3));
            var full = Fresh(d).Composite(PaintChannel.Color);
            const int step = 4; int gw = W / step, gh = H / step;
            var sampled = new byte[gw * gh * 4];
            CpuCompositor.CompositeSampledRegions(d, PaintChannel.Color, step, new[] { new CpuCompositor.CompositeJob(0, 0, gw, gh, sampled) });
            for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++)
                {
                    int s = (y * gw + x) * 4, f = ((y * step + step / 2) * W + x * step + step / 2) * 4;
                    Assert.That(new[] { sampled[s], sampled[s + 1], sampled[s + 2], sampled[s + 3] }, Is.EqualTo(new[] { full[f], full[f + 1], full[f + 2], full[f + 3] }), x + "," + y);
                }
        }

        [Test] public void TheBytesDoNotDependOnTheNumberOfThreads()
        {
            var d = Scene(out _, out _, out var probe, out _);
            d.AddFilter(probe.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(5));
            var many = d.Composite(PaintChannel.Color);
            CoreParallelism.MaxDegreeOfParallelism = 1;
            var copy = Fresh(d);
            Assert.That(copy.Composite(PaintChannel.Color), Is.EqualTo(many));
        }
    }
}
