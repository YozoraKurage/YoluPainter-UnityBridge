using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 不透明度・合成モード・表示・移動・調整の値を続けて変える操作（スライダーのドラッグ）で、GPU に残した写し（下の合成結果、
    /// アップロードしたブロック）を使っても、毎回の結果が「写しを使わない新しい合成器で全部合成し直した結果」とバイト単位で同じこと。
    /// 写しが実際に使われたこと、予算を守ること、予算 0 や解放の後も同じ結果になることも確かめる。
    /// 文書は 1024×512・タイル 128 で、作業ブロック（512）が 2 つになる。
    /// </summary>
    [Category("GPU")]
    public sealed class GpuInteractionTests
    {
        const string ShaderName = "Hidden/YoluPainter/TileComposite";
        const int W = 1024, H = 512, T = 128;

        [SetUp] public void RequireGpu() { GpuTests.RequireWorkingShader(ShaderName); }

        static byte V(int i) => (byte)(((i % 16) + 16) % 16 * 17);

        /// <summary>タイルごとの模様（アルファは 0 か 96 以上。小さなアルファどうしの重なりは比較を丸めの検査でなくするので避ける）。</summary>
        static void Paint(PaintLayer layer, int seed, Func<int, int, bool> tileFilter = null, PaintChannel channel = PaintChannel.Color)
        {
            var surface = layer.GetChannel(channel); var bytes = new byte[T * T * 4];
            for (int ty = 0; ty < H / T; ty++) for (int tx = 0; tx < W / T; tx++)
            {
                if (tileFilter != null && !tileFilter(tx, ty)) continue;
                for (int y = 0; y < T; y++) for (int x = 0; x < T; x++)
                {
                    int gx = tx * T + x, gy = ty * T + y, p = (y * T + x) * 4;
                    bool hole = (gx / 8 + gy / 8 + seed) % 5 == 0;
                    bytes[p] = V(gx / 9 + seed); bytes[p + 1] = V(gy / 7 + seed * 3); bytes[p + 2] = V((gx + gy) / 11 + seed * 5);
                    bytes[p + 3] = hole ? (byte)0 : (byte)(seed == 0 ? 255 : 96 + (gx * 7 + gy * 3 + seed * 13) % 160);
                }
                surface.ImportTile(new TileCoord(tx, ty), bytes);
            }
        }
        static void PaintMask(RasterMask mask, int seed)
        {
            var bytes = new byte[T * T * 4];
            for (int ty = 0; ty < H / T; ty++) for (int tx = 0; tx < W / T; tx++)
            {
                if ((tx + ty + seed) % 3 == 0) continue; // 無いタイル = 何も隠さない
                for (int y = 0; y < T; y++) for (int x = 0; x < T; x++) bytes[(y * T + x) * 4 + 3] = (byte)((x * 2 + y + seed * 40) % 256);
                mask.Surface.ImportTile(new TileCoord(tx, ty), bytes);
            }
        }

        sealed class Scene
        {
            public PaintDocument Doc;
            public PaintLayer Backdrop, Fill, A, B, C, Levels, D, E, Invert, F, Hidden;
            public PaintLayer G, Hgroup, H2;
        }

        /// <summary>塗りつぶし・ラスター・通過グループ（クリッピングと調整を含む）・分離グループ（マスクと入れ子）・調整・マスク付きの最上段。</summary>
        static Scene BuildScene()
        {
            var s = new Scene { Doc = new PaintDocument(W, H, T) };
            var doc = s.Doc;
            s.Backdrop = doc.AddLayer("Backdrop"); Paint(s.Backdrop, 0);
            s.Fill = doc.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(200, 170, 90, 255) } });
            doc.SetLayerBlendMode(s.Fill.Id, LayerBlendMode.Multiply); doc.SetLayerOpacity(s.Fill.Id, .6);
            s.A = doc.AddLayer("A"); Paint(s.A, 1, (tx, ty) => tx < 6); doc.SetLayerOpacity(s.A.Id, .9);
            s.B = doc.AddLayer("B"); Paint(s.B, 2, (tx, ty) => tx > 1);
            s.C = doc.AddLayer("C"); Paint(s.C, 3); doc.SetLayerClipping(s.C.Id, true); doc.SetLayerBlendMode(s.C.Id, LayerBlendMode.Overlay); doc.SetLayerOpacity(s.C.Id, .8);
            s.Levels = doc.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels(.05, .95, 1.2, .02, .98)); doc.SetLayerOpacity(s.Levels.Id, .7);
            s.G = doc.GroupLayers(new[] { s.B.Id, s.C.Id, s.Levels.Id }, "G pass-through");
            s.D = doc.AddLayer("D"); Paint(s.D, 4, (tx, ty) => ty < 3);
            s.E = doc.AddLayer("E"); Paint(s.E, 5, (tx, ty) => (tx + ty) % 2 == 0);
            s.H2 = doc.GroupLayers(new[] { s.E.Id }, "H2 isolated multiply"); doc.SetLayerBlendMode(s.H2.Id, LayerBlendMode.Multiply);
            s.Hgroup = doc.GroupLayers(new[] { s.D.Id, s.H2.Id }, "H isolated screen"); doc.SetLayerBlendMode(s.Hgroup.Id, LayerBlendMode.Screen); doc.SetLayerOpacity(s.Hgroup.Id, .85);
            PaintMask(doc.AddLayerMask(s.Hgroup.Id), 1); doc.SetLayerMaskDensity(s.Hgroup.Id, .8);
            s.Invert = doc.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert()); doc.SetLayerOpacity(s.Invert.Id, .35);
            s.F = doc.AddLayer("F"); Paint(s.F, 6, (tx, ty) => tx >= 2 && tx < 7); PaintMask(doc.AddLayerMask(s.F.Id), 2); doc.SetLayerOpacity(s.F.Id, .75);
            s.Hidden = doc.AddLayer("Hidden"); Paint(s.Hidden, 7); doc.SetLayerVisibility(s.Hidden.Id, false);
            doc.ClearHistory();
            return s;
        }

        /// <summary>写しを使わない新しい合成器で全部合成し直した結果。</summary>
        static byte[] Fresh(PaintDocument doc, PaintChannel channel = PaintChannel.Color)
        {
            using (var fresh = new TileGpuCompositor())
            {
                fresh.Update(doc, channel);
                Assert.That(fresh.LastBelowReuseCount + fresh.LastResidentHitCount, Is.Zero, "a fresh compositor has nothing to reuse");
                return GpuTests.Read(fresh.Texture);
            }
        }

        static void AssertSameAsFresh(TileGpuCompositor live, PaintDocument doc, string context, PaintChannel channel = PaintChannel.Color)
        {
            live.Update(doc, channel);
            Assert.That(live.Backend, Does.StartWith("CPU source brush / GPU"), live.Backend);
            var actual = GpuTests.Read(live.Texture);
            var expected = Fresh(doc, channel);
            int diff = -1; for (int i = 0; i < expected.Length; i++) if (expected[i] != actual[i]) { diff = i; break; }
            Assert.That(diff, Is.EqualTo(-1), context + (diff < 0 ? "" : $": first difference at pixel {diff / 4} channel {diff % 4} (full {expected[diff]}, incremental {actual[diff]})"));
            Assert.That(live.ResidentBytes, Is.LessThanOrEqualTo(live.ResidentBudgetBytes), context + ": resident copies stay inside the budget");
            Assert.That(live.LastCpuTileCount, Is.Zero, context);
        }

        // ───────────── 1 つのレイヤーのドラッグ ─────────────

        [TestCase(true)]
        [TestCase(false)]
        public void DraggingOpacityReusesTheCompositeBelowAndMatchesAFullRecomposite(bool allowCopyTexture)
        {
            var s = BuildScene(); var doc = s.Doc;
            using (var live = new TileGpuCompositor(allowCopyTexture: allowCopyTexture))
            {
                AssertSameAsFresh(live, doc, "initial");
                GpuTests.AssertMatches(doc.Composite(PaintChannel.Color), GpuTests.Read(live.Texture), "initial vs CPU");
                Assert.That(live.BlockSize, Is.EqualTo(512));
                foreach (var (layer, name) in new[] { (s.F, "F (top)"), (s.A, "A (low)"), (s.Fill, "Fill"), (s.Invert, "Invert adjustment") })
                {
                    double[] values = { .5, .62, .41, .77 };
                    for (int i = 0; i < values.Length; i++)
                    {
                        doc.SetLayerOpacity(layer.Id, values[i]);
                        AssertSameAsFresh(live, doc, name + " opacity " + values[i]);
                        if (i >= 1)
                        {
                            Assert.That(live.LastBelowReuseCount, Is.EqualTo(live.LastBlockCount), name + ": every recomposited block starts from its copy below");
                            Assert.That(live.LastBlockCount, Is.EqualTo(2), name);
                        }
                    }
                }
                // 最上段のドラッグでは、上に何も無いので、アップロードは最上段（予算内なら 2 フレーム目から無し）だけ
                doc.SetLayerOpacity(s.F.Id, .3); live.Update(doc, PaintChannel.Color);
                doc.SetLayerOpacity(s.F.Id, .31); live.Update(doc, PaintChannel.Color);
                Assert.That(live.LastUploadCount, Is.Zero, "the dragged top layer and its mask stay on the GPU");
                Assert.That(live.LastResidentHitCount, Is.GreaterThan(0));
                GpuTests.AssertMatches(doc.Composite(PaintChannel.Color), GpuTests.Read(live.Texture), "after the drags vs CPU");
            }
        }

        /// <summary>途中のレイヤーを、すべての合成モードへ順に切り替える（毎回、下の写しから合成し直す）。</summary>
        [Test] public void EveryBlendModeOnTheEditedLayerMatchesAFullRecomposite()
        {
            var s = BuildScene(); var doc = s.Doc;
            using (var live = new TileGpuCompositor())
            {
                AssertSameAsFresh(live, doc, "initial");
                foreach (LayerBlendMode mode in Enum.GetValues(typeof(LayerBlendMode)))
                {
                    if (mode == LayerBlendMode.PassThrough) continue; // グループ専用
                    doc.SetLayerBlendMode(s.A.Id, mode);
                    AssertSameAsFresh(live, doc, "A " + mode);
                    doc.SetLayerBlendMode(s.Hgroup.Id, mode);
                    AssertSameAsFresh(live, doc, "group H " + mode);
                }
                doc.SetLayerBlendMode(s.Hgroup.Id, LayerBlendMode.PassThrough); AssertSameAsFresh(live, doc, "group H pass-through");
                GpuTests.AssertMatches(doc.Composite(PaintChannel.Color), GpuTests.Read(live.Texture), "after the modes vs CPU");
            }
        }

        [Test] public void EditsInsideGroupsMasksAdjustmentsAndFillsMatchAFullRecomposite()
        {
            var s = BuildScene(); var doc = s.Doc;
            using (var live = new TileGpuCompositor())
            {
                AssertSameAsFresh(live, doc, "initial");
                foreach (var v in new[] { .4, .55, .7 }) { doc.SetLayerOpacity(s.B.Id, v); AssertSameAsFresh(live, doc, "B (in a pass-through group) opacity " + v); }
                foreach (var m in new[] { LayerBlendMode.Multiply, LayerBlendMode.Difference, LayerBlendMode.Color }) { doc.SetLayerBlendMode(s.C.Id, m); AssertSameAsFresh(live, doc, "clipped C " + m); }
                foreach (var v in new[] { .3, .6 }) { doc.SetLayerOpacity(s.E.Id, v); AssertSameAsFresh(live, doc, "E (two groups deep) opacity " + v); }
                foreach (var v in new[] { .5, .65, .9 }) { doc.SetLayerOpacity(s.G.Id, v); AssertSameAsFresh(live, doc, "pass-through group fade " + v); }
                doc.SetLayerBlendMode(s.G.Id, LayerBlendMode.Normal); AssertSameAsFresh(live, doc, "G isolated");
                doc.SetLayerBlendMode(s.G.Id, LayerBlendMode.PassThrough); AssertSameAsFresh(live, doc, "G pass-through again");
                foreach (var v in new[] { .2, .45, 1.0 }) { doc.SetLayerMaskDensity(s.Hgroup.Id, v); AssertSameAsFresh(live, doc, "group mask density " + v); }
                doc.SetLayerMaskInverted(s.F.Id, true); AssertSameAsFresh(live, doc, "invert F's mask");
                doc.SetLayerMaskEnabled(s.F.Id, false); AssertSameAsFresh(live, doc, "disable F's mask");
                foreach (var g in new[] { .8, 1.0, 1.6, 2.2 }) { doc.SetAdjustment(s.Levels.Id, AdjustmentSettings.Levels(.05, .95, g, .02, .98)); AssertSameAsFresh(live, doc, "levels gamma " + g); }
                doc.SetAdjustment(s.Invert.Id, AdjustmentSettings.Levels(.1, .9, 1.3, 0, 1)); AssertSameAsFresh(live, doc, "adjustment type change (invert to levels)");
                foreach (var c in new[] { new Rgba32(10, 200, 30, 255), new Rgba32(250, 10, 120, 160) }) { doc.SetFillValue(s.Fill.Id, PaintChannel.Color, c); AssertSameAsFresh(live, doc, "fill value " + c); }
                doc.SetLayerVisibility(s.G.Id, false); AssertSameAsFresh(live, doc, "hide group");
                doc.SetLayerVisibility(s.G.Id, true); AssertSameAsFresh(live, doc, "show group");
                doc.SetLayerVisibility(s.Fill.Id, false); AssertSameAsFresh(live, doc, "hide fill");
                // CPU との比較は傾きが 1 以下の調整に戻してから: ガンマ 2.2 のレベル補正は暗部の傾きが大きく、前段の float と double の
                // わずかな差（半分の丸めの境目）を数段ぶんに広げる（GpuTests の注記と同じ理由）。GPU どうしの一致は上で毎回確かめている。
                doc.SetAdjustment(s.Levels.Id, AdjustmentSettings.Levels()); doc.SetAdjustment(s.Invert.Id, AdjustmentSettings.Invert());
                AssertSameAsFresh(live, doc, "adjustments back to slope 1");
                GpuTests.AssertMatches(doc.Composite(PaintChannel.Color), GpuTests.Read(live.Texture), "after the edits vs CPU");
            }
        }

        [Test] public void MovingLayersUndoRedoAndPixelEditsBelowTheCopyMatchAFullRecomposite()
        {
            var s = BuildScene(); var doc = s.Doc;
            var brush = new BrushSettings { Radius = 20, Hardness = .5, Opacity = .9, Color = new Rgba32(20, 230, 60), PressureSize = false, PressureOpacity = false };
            using (var live = new TileGpuCompositor())
            {
                AssertSameAsFresh(live, doc, "initial");
                doc.SetLayerOpacity(s.F.Id, .6); AssertSameAsFresh(live, doc, "drag F 1");
                doc.SetLayerOpacity(s.F.Id, .5); AssertSameAsFresh(live, doc, "drag F 2");
                // 下の写しより下のレイヤーへの描画: 描いたブロックの写しは無効になる。もう片方のブロックの写しはそのまま使える
                using (var stroke = doc.BeginStroke(s.Backdrop.Id, PaintChannel.Color, brush)) { stroke.Add(new BrushSample(100, 100)); stroke.Add(new BrushSample(160, 120)); stroke.Commit(); }
                AssertSameAsFresh(live, doc, "stroke on the backdrop");
                doc.SetLayerOpacity(s.F.Id, .45); AssertSameAsFresh(live, doc, "drag F after the stroke");
                doc.SetLayerOpacity(s.F.Id, .4); AssertSameAsFresh(live, doc, "drag F again");
                Assert.That(live.LastBelowReuseCount, Is.EqualTo(2));
                // 文書の外からの書き換え（履歴を消す）
                s.B.GetChannel(PaintChannel.Color).SetPixel(700, 300, new Rgba32(1, 2, 3, 255));
                AssertSameAsFresh(live, doc, "external SetPixel inside a group");
                doc.MoveLayer(s.A.Id, doc.ChildrenOf(Guid.Empty).Count - 1); AssertSameAsFresh(live, doc, "move A to the top");
                doc.MoveLayer(s.A.Id, 1); AssertSameAsFresh(live, doc, "move A back down");
                doc.MoveLayerTo(s.D.Id, Guid.Empty, 0); AssertSameAsFresh(live, doc, "move D out of its group to the bottom");
                doc.MoveLayerTo(s.F.Id, s.G.Id, 1); AssertSameAsFresh(live, doc, "move F into the pass-through group");
                doc.Ungroup(s.Hgroup.Id); AssertSameAsFresh(live, doc, "ungroup H");
                Assert.That(doc.Undo()); AssertSameAsFresh(live, doc, "undo ungroup");
                Assert.That(doc.Undo()); AssertSameAsFresh(live, doc, "undo move into the group");
                Assert.That(doc.Redo()); AssertSameAsFresh(live, doc, "redo move into the group");
                using (var stroke = doc.BeginStroke(s.E.Id, PaintChannel.Color, brush))
                {
                    stroke.Add(new BrushSample(900, 400)); AssertSameAsFresh(live, doc, "mid-stroke preview two groups deep");
                    stroke.Cancel();
                }
                AssertSameAsFresh(live, doc, "after cancel");
                Paint(s.Backdrop, 9, null, PaintChannel.Roughness);
                AssertSameAsFresh(live, doc, "switch to roughness", PaintChannel.Roughness);
                AssertSameAsFresh(live, doc, "back to colour");
                GpuTests.AssertMatches(doc.Composite(PaintChannel.Color), GpuTests.Read(live.Texture), "after the moves vs CPU");
            }
        }

        // ───────────── 予算と解放 ─────────────

        [Test] public void WithoutABudgetNothingIsKeptAndTheResultIsTheSame()
        {
            var s = BuildScene(); var doc = s.Doc;
            using (var live = new TileGpuCompositor { ResidentBudgetBytes = 0 })
            {
                AssertSameAsFresh(live, doc, "initial");
                foreach (var v in new[] { .5, .6, .7 })
                {
                    doc.SetLayerOpacity(s.A.Id, v); AssertSameAsFresh(live, doc, "A " + v);
                    Assert.That(live.ResidentBytes, Is.Zero);
                    Assert.That(live.LastBelowReuseCount + live.LastResidentHitCount, Is.Zero);
                }
            }
        }

        [Test] public void ATightBudgetEvictsOldCopiesAndStaysExact()
        {
            var s = BuildScene(); var doc = s.Doc;
            using (var live = new TileGpuCompositor())
            {
                live.ResidentBudgetBytes = 3L * 4 * 512 * 512; // ブロック 3 つぶん
                AssertSameAsFresh(live, doc, "initial");
                foreach (var layer in new[] { s.A, s.F, s.B, s.Fill, s.A })
                    foreach (var v in new[] { .5, .6 }) { doc.SetLayerOpacity(layer.Id, v); AssertSameAsFresh(live, doc, layer.Name + " " + v); }
                Assert.That(live.ResidentBytes, Is.GreaterThan(0));
            }
        }

        [Test] public void ReleasingAndIdleTrimmingDropTheCopiesWithoutChangingTheResult()
        {
            var s = BuildScene(); var doc = s.Doc;
            using (var live = new TileGpuCompositor())
            {
                AssertSameAsFresh(live, doc, "initial");
                doc.SetLayerOpacity(s.A.Id, .5); AssertSameAsFresh(live, doc, "A 1");
                doc.SetLayerOpacity(s.A.Id, .6); AssertSameAsFresh(live, doc, "A 2");
                Assert.That(live.ResidentBytes, Is.GreaterThan(0));
                live.ReleaseResidentCaches();
                Assert.That(live.ResidentBytes, Is.Zero);
                doc.SetLayerOpacity(s.A.Id, .7); AssertSameAsFresh(live, doc, "A after release");
                doc.SetLayerOpacity(s.A.Id, .8); AssertSameAsFresh(live, doc, "A copies made again");
                Assert.That(live.ResidentBytes, Is.GreaterThan(0));
                long budget = live.ResidentBudgetBytes;
                live.ResidentBudgetBytes = live.ResidentBytes - 1;
                Assert.That(live.ResidentBytes, Is.Zero, "lowering the budget below what is kept releases the copies");
                doc.SetLayerOpacity(s.A.Id, .85); AssertSameAsFresh(live, doc, "A after lowering the budget");
                Assert.That(live.ResidentBytes, Is.LessThanOrEqualTo(live.ResidentBudgetBytes));
                live.ResidentBudgetBytes = budget;
                doc.SetLayerOpacity(s.A.Id, .8); AssertSameAsFresh(live, doc, "A with the budget back");
                for (int i = 0; i <= TileGpuCompositor.IdleUpdatesBeforeRelease; i++) live.Update(doc, PaintChannel.Color);
                Assert.That(live.ResidentBytes, Is.Zero, "copies unused for a while are released");
                doc.SetLayerOpacity(s.A.Id, .9); AssertSameAsFresh(live, doc, "A after idle release");
            }
        }

        /// <summary>見えないレイヤーの変更は変更記録にタイルを残すが、ブロックの署名は変わらないので合成し直さない。</summary>
        [Test] public void BlocksWhoseInputsDidNotChangeAreNotRecomposited()
        {
            var s = BuildScene(); var doc = s.Doc;
            using (var live = new TileGpuCompositor())
            {
                AssertSameAsFresh(live, doc, "initial");
                doc.SetLayerOpacity(s.Hidden.Id, .3);
                AssertSameAsFresh(live, doc, "hidden layer opacity");
                Assert.That(live.LastUpdatedTileCount, Is.GreaterThan(0), "the journal reports the hidden layer's tiles");
                Assert.That(live.LastBlockCount, Is.Zero); Assert.That(live.LastSkippedBlockCount, Is.EqualTo(2));
            }
        }

        [Test] public void TheCpuFallbackStaysExactThroughTheSameEdits()
        {
            var s = BuildScene(); var doc = s.Doc;
            using (var cpu = new TileGpuCompositor(allowGpu: false))
            {
                void Check(string name) { cpu.Update(doc, PaintChannel.Color); Assert.That(GpuTests.ReadCpu(cpu.Texture), Is.EqualTo(doc.Composite(PaintChannel.Color)), name); }
                Check("initial");
                doc.SetLayerOpacity(s.A.Id, .5); Check("A opacity");
                doc.SetLayerBlendMode(s.Hgroup.Id, LayerBlendMode.Overlay); Check("group mode");
                doc.MoveLayer(s.A.Id, 0); Check("move");
            }
        }
    }
}
