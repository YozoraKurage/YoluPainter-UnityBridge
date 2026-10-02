using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>表示の合成の CPU の経路（Project Settings ▸ YoluPainter ▸ Display compositing = CPU、または GPU が使えないとき）。
    /// 表示が CPU の正本（PaintDocument.Composite）とも、以前の CPU の代わりの経路（変わったタイルを 1 枚ずつ CompositeRegion で合成し、
    /// 全面を毎回載せ直す）ともバイト単位で同じこと、変わったタイルだけを表示へ送ること、CompositeRegion をタイルごとではなく長方形ごとに
    /// 呼ぶこと、GPU を選んでも使えなければ CPU に落ちてそう示すこと。表示の RenderTexture の経路はグラフィックスデバイスが要る
    /// （シェーダーは要らない。CopyTexture が無い環境の描き込みの経路だけ TileComposite が要る）。</summary>
    public sealed class CpuCompositingTests
    {
        [TearDown] public void StopSimulating() { TileGpuCompositor.SimulatedGpuUnavailable = null; }

        /// <summary>以前（33f9f9c まで）の CPU の代わりの経路の写し: 変更記録のタイルを 1 枚ずつ CompositeRegion で合成して全面の画素に
        /// 書き、全面を表示に載せ直していた。</summary>
        sealed class OldCpuFallback
        {
            byte[] pixels; PaintDocument last; PaintChannel lastChannel; long serial = -1;
            public byte[] Update(PaintDocument doc, PaintChannel channel)
            {
                var dirty = new HashSet<TileCoord>();
                bool incremental = pixels != null && ReferenceEquals(doc, last) && channel == lastChannel && doc.TryGetChangedTiles(channel, serial, dirty);
                if (!incremental) pixels = doc.Composite(channel);
                else
                    foreach (var coord in dirty)
                    {
                        int t = doc.TileSize, x = coord.X * t, y = coord.Y * t, w = Math.Min(t, doc.Width - x), h = Math.Min(t, doc.Height - y);
                        var region = CpuCompositor.CompositeRegion(doc, channel, x, y, w, h);
                        for (int row = 0; row < h; row++) Buffer.BlockCopy(region, row * w * 4, pixels, ((y + row) * doc.Width + x) * 4, w * 4);
                    }
                last = doc; lastChannel = channel; serial = doc.ChangeSerial;
                return (byte[])pixels.Clone();
            }
        }

        static byte[] Read(TileGpuCompositor c) => c.Texture is RenderTexture ? GpuTests.Read(c.Texture) : GpuTests.ReadCpu(c.Texture);

        /// <summary>バイト単位で同じか（NUnit の Is.EqualTo は大きな配列で 1 要素ずつ箱に入れて比べ、1100×700 の比較 1 回に数秒かかる）。
        /// 違えば最初の画素を示す。</summary>
        internal static void AssertSameBytes(byte[] expected, byte[] actual, string context)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), context + ": length");
            if (expected.AsSpan().SequenceEqual(actual)) return;
            int at = 0; while (expected[at] == actual[at]) at++;
            Assert.Fail($"{context}: first difference at pixel {at / 4} channel {at % 4} (expected {expected[at]}, got {actual[at]})");
        }

        /// <summary>CPU を選んだ合成器（表示は RenderTexture）。グラフィックスデバイスや写す手段が無ければスキップ。</summary>
        static TileGpuCompositor CpuToRenderTexture(PaintDocument doc, bool allowCopyTexture = true)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("No graphics device (-nographics): the CPU path shows a CPU-side Texture2D here. Run with --batch-gl or in GUI mode.");
            var c = new TileGpuCompositor(CompositorBackend.Cpu, allowCopyTexture);
            c.Update(doc, PaintChannel.Color);
            if (c.Path != TileGpuCompositor.CompositePath.CpuTiles) { var backend = c.Backend; c.Dispose(); Assert.Ignore("No way to copy into a render texture here: " + backend); }
            return c;
        }

        // ───────── 長方形へのまとめ ─────────

        static HashSet<TileCoord> Tiles(params (int x, int y)[] coords) => new HashSet<TileCoord>(coords.Select(c => new TileCoord(c.x, c.y)));
        static HashSet<TileCoord> Covered(List<TileGpuCompositor.TileRect> rects)
        {
            var all = new HashSet<TileCoord>();
            foreach (var r in rects) for (int y = r.Y0; y < r.Y1; y++) for (int x = r.X0; x < r.X1; x++) Assert.That(all.Add(new TileCoord(x, y)), Is.True, "rectangles overlap at " + x + "," + y);
            return all;
        }

        [Test] public void ChangedTilesAreGroupedIntoRectangles()
        {
            Assert.That(TileGpuCompositor.CoverTiles(Tiles(), 8, 8), Is.Empty);
            Assert.That(TileGpuCompositor.CoverTiles(Tiles((-1, 0), (8, 2), (3, 9)), 8, 8), Is.Empty, "tiles outside the canvas are ignored");
            var one = TileGpuCompositor.CoverTiles(Tiles((3, 4)), 8, 8);
            Assert.That(one.Single().ToString(), Is.EqualTo("[3,4)x[4,5)"));
            // 2×3 のかたまり（1 回の呼び出し）。同じ番号が重なっていても 1 枚
            var block = TileGpuCompositor.CoverTiles(new[] { new TileCoord(1, 1), new TileCoord(2, 1), new TileCoord(1, 2), new TileCoord(2, 2), new TileCoord(1, 3), new TileCoord(2, 3), new TileCoord(2, 2) }, 8, 8);
            Assert.That(block.Single().ToString(), Is.EqualTo("[1,3)x[1,4)"));
            // 斜めの線（ストローク）: 外接長方形の余りが多いので、変わったタイルだけを覆う
            var diagonal = Tiles((0, 0), (1, 0), (1, 1), (2, 1), (2, 2), (3, 2), (3, 3), (4, 3), (4, 4), (5, 4));
            var rects = TileGpuCompositor.CoverTiles(diagonal, 8, 8);
            Assert.That(Covered(rects).SetEquals(diagonal), Is.True, string.Join(" ", rects));
            Assert.That(rects.Count, Is.EqualTo(5), "one run per row: " + string.Join(" ", rects));
            // L の字（余り 1 枚 ≤ 呼び出し 1 回ぶん）は外接長方形 1 つにまとめる
            var l = TileGpuCompositor.CoverTiles(Tiles((0, 0), (1, 0), (0, 1)), 8, 8);
            Assert.That(l.Single().ToString(), Is.EqualTo("[0,2)x[0,2)"));
            // 離れた 2 か所は別々
            var apart = TileGpuCompositor.CoverTiles(Tiles((0, 0), (7, 7)), 8, 8);
            Assert.That(apart.Count, Is.EqualTo(2)); Assert.That(Covered(apart).SetEquals(Tiles((0, 0), (7, 7))), Is.True);
            // 同じ幅の行は縦につなぐ。幅が違う行は別の長方形
            var stairs = Tiles((0, 0), (1, 0), (2, 0), (3, 0), (0, 1), (1, 1), (2, 1), (3, 1), (0, 2), (1, 2), (6, 2), (7, 2));
            var steps = TileGpuCompositor.CoverTiles(stairs, 8, 8);
            Assert.That(Covered(steps).SetEquals(stairs), Is.True, string.Join(" ", steps));
            Assert.That(steps.Select(r => r.ToString()), Is.EquivalentTo(new[] { "[0,4)x[0,2)", "[0,2)x[2,3)", "[6,8)x[2,3)" }));
            // 乱数の集合でも、覆うのは集合と同じか（外接長方形 1 つのときは）それを含む長方形
            var random = new System.Random(5);
            for (int i = 0; i < 200; i++)
            {
                var set = new HashSet<TileCoord>(); int n = random.Next(1, 30);
                for (int k = 0; k < n; k++) set.Add(new TileCoord(random.Next(0, 9), random.Next(0, 7)));
                var cover = TileGpuCompositor.CoverTiles(set, 9, 7); var covered = Covered(cover);
                Assert.That(covered.IsSupersetOf(set), Is.True);
                if (cover.Count > 1) Assert.That(covered.SetEquals(set), Is.True, "several rectangles cover only the changed tiles");
            }
        }

        // ───────── 表示の画素 ─────────

        /// <summary>筆・Undo・Redo・構造の変更・マスク・塗りつぶし・調整・グループ・クリッピング・チャンネルの切り替え・別の文書を乱数で
        /// 続け、毎回 4 つ（CPU を選んだ表示の RenderTexture、デバイスを使わない Texture2D、以前の経路、正本）が同じか確かめる。
        /// 文書は 1100×700・タイル 64 で、作業ブロック 512（8×8 タイル）が端で欠ける。</summary>
        [Test] public void TheCpuPathsShowExactlyWhatTheOldFallbackAndTheReferenceShow()
        {
            var doc = Scene(1100, 700, 64, out var layers);
            var old = new OldCpuFallback();
            using (var frame = new TileGpuCompositor(allowGpu: false))
            using (var tiles = CpuToRenderTexture(doc))
            {
                var random = new System.Random(11); var channel = PaintChannel.Color; int partial = 0, blockSends = 0;
                void Check(string step)
                {
                    var serial = doc.ChangeSerial;
                    tiles.Update(doc, channel); frame.Update(doc, channel);
                    var expected = doc.Composite(channel);
                    AssertSameBytes(expected, old.Update(doc, channel), step + ": the old fallback (the copy is wrong)");
                    AssertSameBytes(expected, GpuTests.ReadCpu(frame.Texture), step + ": CPU-side Texture2D");
                    AssertSameBytes(expected, GpuTests.Read(tiles.Texture), step + ": render texture with changed tiles");
                    Assert.That(frame.Path, Is.EqualTo(TileGpuCompositor.CompositePath.CpuFrame)); Assert.That(tiles.Path, Is.EqualTo(TileGpuCompositor.CompositePath.CpuTiles));
                    if (!tiles.LastCpuFullFrame)
                    {
                        Assert.That(tiles.LastSentTileCount, Is.EqualTo(tiles.LastUpdatedTileCount), step + ": only the changed tiles are sent");
                        if (tiles.LastUpdatedTileCount > 0) partial++;
                    }
                    if (tiles.LastSentTileCount > tiles.LastCpuCompositeCalls && tiles.LastUpdatedTileCount >= 64) blockSends++;
                }
                Check("first");
                var brush = new BrushSettings { Radius = 20, Hardness = .6, Opacity = .9, Color = new Rgba32(30, 200, 120), PressureSize = false, PressureOpacity = false };
                for (int i = 0; i < 40; i++)
                {
                    var layer = layers[random.Next(layers.Count)];
                    switch (random.Next(9))
                    {
                        case 0: case 1: case 2:
                            brush.Radius = 4 + random.Next(60); brush.Color = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)(64 + random.Next(192)));
                            using (var s = doc.BeginStroke(layer.Id, channel, brush))
                            {
                                double x = random.Next(doc.Width), y = random.Next(doc.Height);
                                for (int k = 0; k < 4; k++) s.Add(new BrushSample(x += random.Next(-120, 121), y += random.Next(-80, 81)));
                                if (random.Next(5) == 0) { Check("mid-stroke " + i); s.Cancel(); } else s.Commit();
                            }
                            break;
                        case 3: doc.SetLayerOpacity(layer.Id, .2 + random.NextDouble() * .8); break;
                        case 4: doc.SetLayerBlendMode(layer.Id, new[] { LayerBlendMode.Normal, LayerBlendMode.Multiply, LayerBlendMode.Screen, LayerBlendMode.Overlay, LayerBlendMode.Difference }[random.Next(5)]); break;
                        case 5: if (doc.CanUndo) doc.Undo(); break;
                        case 6: if (doc.CanRedo) doc.Redo(); break;
                        case 7:
                            // ブロック 1 つを丸ごと覆う大きな筆（ブロック単位で送る経路）
                            using (var s = doc.BeginStroke(layers[0].Id, channel, new BrushSettings { Radius = 380, Hardness = 1, Color = new Rgba32(90, 40, 200, 255), PressureSize = false, PressureOpacity = false }))
                            { s.Add(new BrushSample(256, 256)); s.Commit(); }
                            break;
                        default: doc.SetLayerVisibility(layer.Id, !layer.Visible); break;
                    }
                    Check("edit " + i);
                    if (i == 20) { channel = PaintChannel.Roughness; Check("Roughness"); channel = PaintChannel.Color; Check("Color again"); }
                }
                var other = Scene(1100, 700, 64, out _);
                tiles.Update(other, channel); AssertSameBytes(other.Composite(channel), GpuTests.Read(tiles.Texture), "another document of the same size");
                Assert.That(tiles.LastCpuFullFrame, Is.True);
                Assert.That(partial, Is.GreaterThan(10), "most edits were sent tile by tile");
                Assert.That(blockSends, Is.GreaterThan(0), "a whole changed block went in one piece");
            }
        }

        /// <summary>マスク・塗りつぶし・調整・分離と通過のグループ・クリッピングのある文書（CPU の正本の式をそのまま使うので、GPU の経路の
        /// 入れ子の段の上限も無い）。</summary>
        static PaintDocument Scene(int w, int h, int tile, out List<PaintLayer> layers)
        {
            var doc = new PaintDocument(w, h, tile);
            var random = new System.Random(w * 31 + h);
            layers = new List<PaintLayer>();
            var bottom = doc.AddLayer("Bottom"); layers.Add(bottom);
            var fill = doc.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(40, 160, 90, 200) }, { PaintChannel.Roughness, new Rgba32(100, 100, 100, 255) } });
            doc.SetLayerBlendMode(fill.Id, LayerBlendMode.Multiply); doc.AddLayerMask(fill.Id); doc.SetLayerMaskInverted(fill.Id, true);
            var group = doc.AddGroup("Group"); doc.SetLayerBlendMode(group.Id, LayerBlendMode.Overlay);
            var inner = doc.AddLayer("Inner"); doc.MoveLayerTo(inner.Id, group.Id, 0); layers.Add(inner);
            var pass = doc.AddGroup("Pass"); doc.SetLayerOpacity(pass.Id, .7);
            var passInner = doc.AddLayer("Pass inner"); doc.MoveLayerTo(passInner.Id, pass.Id, 0); layers.Add(passInner);
            var top = doc.AddLayer("Top"); layers.Add(top);
            var clipped = doc.AddLayer("Clipped"); doc.SetLayerClipping(clipped.Id, true); layers.Add(clipped);
            doc.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels(.1, .9, 1.4, .05, .95));
            var brush = new BrushSettings { Radius = 30, Hardness = .5, Opacity = .8, PressureSize = false, PressureOpacity = false };
            foreach (var layer in layers)
                foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Roughness })
                {
                    if (!layer.IsChannelEnabled(channel)) doc.SetChannelEnabled(layer.Id, channel, true);
                    brush.Color = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)(96 + random.Next(160)));
                    using (var s = doc.BeginStroke(layer.Id, channel, brush))
                    { for (int k = 0; k < 6; k++) s.Add(new BrushSample(random.Next(w), random.Next(h))); s.Commit(); }
                }
            doc.ClearHistory();
            return doc;
        }

        /// <summary>タイルごとではなく、変わったタイルをまとめた長方形ごとに CompositeRegion を 1 回呼び、送るのは変わったタイルだけ。
        /// 構造の変更で全部のタイルが変わったら全面を 1 回で合成する。デバイスを使わない Texture2D の経路でも呼び出しの数は同じ。</summary>
        [Test] public void ChangedTilesAreCompositedPerRectangleAndOnlyThoseAreSent()
        {
            var brush = new BrushSettings { Radius = 5, Hardness = 1, Color = new Rgba32(255, 0, 0), PressureSize = false, PressureOpacity = false };
            foreach (bool renderTexture in new[] { false, true })
            {
                var doc = new PaintDocument(512, 512, 64); // 8×8 タイル、作業ブロック 1 つ
                var layer = doc.AddLayer("Paint");
                var bytes = new byte[64 * 64 * 4]; new System.Random(3).NextBytes(bytes);
                for (int ty = 0; ty < 8; ty++) for (int tx = 0; tx < 8; tx++) layer.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(tx, ty), bytes);
                doc.ClearHistory();
                using (var c = renderTexture ? CpuToRenderTexture(doc) : new TileGpuCompositor(allowGpu: false))
                {
                    if (!renderTexture) c.Update(doc, PaintChannel.Color);
                    Assert.That(c.LastCpuFullFrame, Is.True); Assert.That(c.LastCpuCompositeCalls, Is.EqualTo(1));
                    Assert.That(c.LastSentTileCount, Is.EqualTo(64), "the first update sends everything");
                    void Edit(string name, Action edit)
                    {
                        long since = doc.ChangeSerial; edit();
                        var changed = new HashSet<TileCoord>(); Assert.That(doc.TryGetChangedTiles(PaintChannel.Color, since, changed), Is.True);
                        c.Update(doc, PaintChannel.Color);
                        AssertSameBytes(doc.Composite(PaintChannel.Color), Read(c), name);
                        if (changed.Count == 64) { Assert.That(c.LastCpuFullFrame, Is.True, name); Assert.That(c.LastCpuCompositeCalls, Is.EqualTo(1), name); return; }
                        Assert.That(c.LastCpuFullFrame, Is.False, name);
                        Assert.That(c.LastCpuCompositeCalls, Is.EqualTo(TileGpuCompositor.CoverTiles(changed, 8, 8).Count), name + ": one call per rectangle");
                        if (renderTexture) Assert.That(c.LastSentTileCount, Is.EqualTo(changed.Count), name + ": only the changed tiles are sent");
                    }
                    void Line(double x0, double y0, double x1, double y1) { using (var s = doc.BeginStroke(layer.Id, PaintChannel.Color, brush)) { s.Add(new BrushSample(x0, y0)); s.Add(new BrushSample(x1, y1)); s.Commit(); } }
                    Edit("a line across three tiles", () => Line(80, 100, 240, 100));
                    Assert.That(c.LastCpuCompositeCalls, Is.EqualTo(1), "three tiles, one call");
                    Edit("a diagonal", () => Line(20, 20, 490, 490));
                    Assert.That(c.LastCpuCompositeCalls, Is.LessThan(c.LastUpdatedTileCount), "fewer calls than tiles");
                    Edit("two far corners", () => { Line(10, 10, 12, 12); Line(500, 500, 502, 502); });
                    Edit("undo", () => doc.Undo());
                    Edit("opacity (every tile)", () => doc.SetLayerOpacity(layer.Id, .5));
                    Edit("nothing", () => { });
                    Assert.That(c.LastCpuCompositeCalls, Is.Zero); Assert.That(c.LastSentTileCount, Is.Zero);
                }
            }
        }

        /// <summary>CopyTexture の無い環境の写し方（TileComposite の写しのパスで描き込む）でも同じ画素。</summary>
        [Test, Category("GPU")] public void TheDrawCopyPathShowsTheSamePixels()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            var doc = Scene(700, 520, 32, out var layers);
            using (var c = CpuToRenderTexture(doc, allowCopyTexture: false))
            {
                Assert.That(c.Backend, Does.Contain("(draw copy)"));
                AssertSameBytes(doc.Composite(PaintChannel.Color), GpuTests.Read(c.Texture), "first");
                using (var s = doc.BeginStroke(layers[1].Id, PaintChannel.Color, new BrushSettings { Radius = 40, Color = new Rgba32(10, 20, 250, 200), PressureSize = false, PressureOpacity = false }))
                { s.Add(new BrushSample(500, 300)); s.Add(new BrushSample(690, 510)); s.Commit(); }
                c.Update(doc, PaintChannel.Color);
                Assert.That(c.LastCpuFullFrame, Is.False);
                AssertSameBytes(doc.Composite(PaintChannel.Color), GpuTests.Read(c.Texture), "stroke into the partial edge tiles");
            }
        }

        /// <summary>CPU で合成しても表示は RenderTexture なので、Normal の出力は GPU のパスのまま（Height の合成器も CPU）。</summary>
        [Test, Category("GPU")] public void TheNormalOutputStaysOnTheGpuWhenCompositingOnTheCpu()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/NormalOutput");
            var doc = new PaintDocument(300, 260, 32);
            var height = doc.AddLayer("Height"); doc.SetChannelEnabled(height.Id, PaintChannel.Height, true);
            var normal = doc.AddLayer("Normal"); doc.SetChannelEnabled(normal.Id, PaintChannel.Normal, true);
            for (int y = 0; y < 260; y++) for (int x = 0; x < 300; x++)
            {
                height.GetChannel(PaintChannel.Height).SetPixel(x, y, new Rgba32((byte)((x * x + 3 * y) % 256), 0, 0, 255));
                if ((x / 7 + y / 5) % 3 != 0) normal.GetChannel(PaintChannel.Normal).SetPixel(x, y, new Rgba32((byte)(x % 256), (byte)(y % 256), 200, 255));
            }
            doc.SetNormalSettings(new NormalSettings(true, 6, HeightEdgeMode.Wrap, NormalYDirection.OpenGL));
            using (var c = new TileGpuCompositor(CompositorBackend.Cpu))
            using (var view = new NormalOutputView(heightBackend: CompositorBackend.Cpu))
            {
                c.Update(doc, PaintChannel.Normal); view.Update(doc, c.Texture);
                Assert.That(c.Path, Is.EqualTo(TileGpuCompositor.CompositePath.CpuTiles), c.Backend);
                Assert.That(view.UsedGpu, Is.True, view.Backend);
                Assert.That(view.HeightCompositor.Path, Is.EqualTo(TileGpuCompositor.CompositePath.CpuTiles), view.HeightCompositor.Backend);
                // 合成は正確（CPU の正本そのもの）なので、出力のパスの丸め 1 段だけ
                GpuTests.AssertMatches(NormalMaps.Output(doc), GpuTests.Read(view.Texture), "CPU composites + GPU output");
                height.GetChannel(PaintChannel.Height).SetPixel(150, 130, new Rgba32(255, 0, 0, 255));
                c.Update(doc, PaintChannel.Normal); view.Update(doc, c.Texture);
                Assert.That(view.HeightCompositor.LastSentTileCount, Is.EqualTo(1), "only the changed Height tile is sent");
                GpuTests.AssertMatches(NormalMaps.Output(doc), GpuTests.Read(view.Texture), "after a Height edit");
            }
        }

        // ───────── 選択と、使えないときの代わり ─────────

        [Test] public void CpuIsUsedWhenChosenAndGpuFallsBackToCpuWhenItCannotBeUsed()
        {
            var doc = Scene(300, 200, 32, out _);
            using (var chosen = new TileGpuCompositor(CompositorBackend.Cpu))
            {
                chosen.Update(doc, PaintChannel.Color);
                Assert.That(chosen.Path, Is.Not.EqualTo(TileGpuCompositor.CompositePath.Gpu), "CPU is never the GPU path, even where the GPU works");
                Assert.That(chosen.FellBackToCpu, Is.False, "chosen, not a fallback");
                Assert.That(chosen.Backend, Does.StartWith("CPU compositor (chosen in Project Settings > YoluPainter)").And.Not.Contain("GPU tiled"));
                AssertSameBytes(doc.Composite(PaintChannel.Color), Read(chosen), "chosen CPU");
            }
            TileGpuCompositor.SimulatedGpuUnavailable = "simulated for the test";
            Assert.That(TileGpuCompositor.GpuCompositingAvailable(out string reason), Is.False); Assert.That(reason, Is.EqualTo("simulated for the test"));
            foreach (var choice in new[] { CompositorBackend.Gpu, CompositorBackend.Automatic })
                using (var c = new TileGpuCompositor(choice))
                {
                    c.Update(doc, PaintChannel.Color);
                    Assert.That(c.Path, Is.Not.EqualTo(TileGpuCompositor.CompositePath.Gpu), choice.ToString());
                    Assert.That(c.FellBackToCpu, Is.True, choice + ": the window shows \"CPU compositing (GPU unavailable)\"");
                    Assert.That(c.Backend, Does.StartWith("CPU composite fallback: simulated for the test").And.Not.Contain("GPU tiled"), choice.ToString());
                    AssertSameBytes(doc.Composite(PaintChannel.Color), Read(c), choice.ToString());
                }
            Assert.That(() => new TileGpuCompositor((CompositorBackend)7), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        /// <summary>GPU が使える環境では、GPU を選べば（自動でも）今までどおり GPU で合成する。</summary>
        [Test, Category("GPU")] public void GpuAndAutomaticCompositeOnTheGpuWhereItWorks()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            Assert.That(TileGpuCompositor.ResolveAutomatic(out _), Is.EqualTo(CompositorBackend.Gpu));
            var doc = Scene(300, 200, 32, out _);
            foreach (var choice in new[] { CompositorBackend.Gpu, CompositorBackend.Automatic })
                using (var c = new TileGpuCompositor(choice))
                {
                    c.Update(doc, PaintChannel.Color);
                    Assert.That(c.Path, Is.EqualTo(TileGpuCompositor.CompositePath.Gpu), c.Backend);
                    Assert.That(c.FellBackToCpu, Is.False); Assert.That(c.Backend, Does.StartWith("CPU source brush / GPU tiled"));
                    Assert.That(c.Preference, Is.EqualTo(choice));
                }
        }
    }
}
