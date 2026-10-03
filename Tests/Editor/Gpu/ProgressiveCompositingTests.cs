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
    /// <summary>表示の合成を時間で区切る（<see cref="TileGpuCompositor.Update(PaintDocument, PaintChannel, CompositeSchedule)"/>）。
    /// 仕事を全部流し終えた表示が区切らない合成と同じこと（CPU の経路はバイト単位、GPU の経路は今の許容の 1 段）、表示のブロックはいつも
    /// 前の絵か新しい絵のどちらかであること、合成する順（ストローク → 2D で見えている所 → 3D ビューの UV の所 → 残り、同じ組では長く
    /// 待っている順）、動かし続けたスライダーでもどのブロックも最新の値に追いつくこと、時間の予算（ブロック 1 つ分の超過まで）、予算 0・
    /// 小さな予算・切り替え・解放・Dispose で壊れないこと。時間は差し替えた時計で測る（ブロック 1 つ = 決まった時間）ので、機械の速さに
    /// 依らない。実際の時計での確かめは最後の 1 件だけ（緩い条件）。</summary>
    public sealed class ProgressiveCompositingTests
    {
        const string Shader = "Hidden/YoluPainter/TileComposite";

        /// <summary>"Gpu": GPU の経路、"Cpu": CPU で合成して表示の RenderTexture へ送る経路、"Frame": デバイスを使わない CPU の経路
        /// （全面を載せ直すので区切らない）。</summary>
        static TileGpuCompositor Make(string kind)
        {
            switch (kind)
            {
                case "Gpu":
                    GpuTests.RequireWorkingShader(Shader);
                    return new TileGpuCompositor(CompositorBackend.Gpu);
                case "Cpu":
                    if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics): the CPU path shows a CPU-side Texture2D here. Run with --batch-gl or in GUI mode.");
                    return new TileGpuCompositor(CompositorBackend.Cpu);
                default: return new TileGpuCompositor(allowGpu: false);
            }
        }
        static byte[] Read(TileGpuCompositor c) => c.Texture is RenderTexture ? GpuTests.Read(c.Texture) : GpuTests.ReadCpu(c.Texture);
        /// <summary>CPU の正本と比べる（CPU の経路はバイト単位、GPU の経路は今の許容の 1 段。GPU の許容は小さなアルファどうしの重なりの
        /// 無い文書でだけ成り立つので、<see cref="Grid"/> の文書で使う）。</summary>
        static void AssertShows(TileGpuCompositor c, byte[] expected, string context)
        {
            if (c.Path == TileGpuCompositor.CompositePath.Gpu) GpuTests.AssertMatches(expected, Read(c), context);
            else CpuCompositingTests.AssertSameBytes(expected, Read(c), context);
        }
        /// <summary>同じ経路の新しい合成器で 1 回で全部合成した表示。</summary>
        static byte[] AtOnce(string kind, PaintDocument doc, PaintChannel channel)
        {
            using (var c = Make(kind)) { c.Update(doc, channel); return Read(c); }
        }
        /// <summary>区切って合成した表示が、同じ経路で 1 回で全部合成した表示とバイト単位で同じ（どの経路でも。CPU の経路はそれが CPU の
        /// 正本そのもの）。</summary>
        static void AssertSameAsAtOnce(TileGpuCompositor c, string kind, PaintDocument doc, PaintChannel channel, string context)
        {
            var atOnce = AtOnce(kind, doc, channel);
            CpuCompositingTests.AssertSameBytes(atOnce, Read(c), context + ": the same bytes as compositing at once");
            if (kind != "Gpu") CpuCompositingTests.AssertSameBytes(doc.Composite(channel), atOnce, context + ": the CPU reference");
        }
        static void RequirePath(TileGpuCompositor c, string kind)
        {
            var expected = kind == "Gpu" ? TileGpuCompositor.CompositePath.Gpu : kind == "Cpu" ? TileGpuCompositor.CompositePath.CpuTiles : TileGpuCompositor.CompositePath.CpuFrame;
            if (c.Path != expected) Assert.Ignore("This Editor composites on another path here: " + c.Backend);
        }

        /// <summary>時計の差し替え: ブロックを 1 つ合成するたびに perBlock ms 進む。</summary>
        sealed class FakeClock
        {
            public double Now; readonly double perBlock;
            public FakeClock(TileGpuCompositor c, double perBlock) { this.perBlock = perBlock; c.ClockForTests = () => Now; c.BlockCompositedForTests = (x, y) => Now += perBlock; }
        }

        /// <summary>予定が無くなるまで Update を続ける（回数を返す）。each は毎回の後に呼ぶ。</summary>
        static int Drain(TileGpuCompositor c, PaintDocument doc, PaintChannel channel, CompositeSchedule schedule, Action<int> each = null)
        {
            for (int calls = 1; ; calls++)
            {
                bool done = c.Update(doc, channel, schedule);
                Assert.That(done, Is.EqualTo(!c.HasPendingWork));
                each?.Invoke(calls);
                if (done) return calls;
                Assert.That(calls, Is.LessThan(10000), "the work always moves on");
            }
        }

        static readonly BrushSettings Soft = new BrushSettings { Radius = 30, Hardness = .5, Opacity = .8, PressureSize = false, PressureOpacity = false };
        static readonly BrushSettings Hard = new BrushSettings { Radius = 30, Hardness = 1, Opacity = 1, PressureSize = false, PressureOpacity = false };
        static void Stroke(PaintDocument doc, Guid layer, PaintChannel channel, Rgba32 color, params (double x, double y)[] points)
        {
            var brush = Soft; brush.Color = color;
            using (var s = doc.BeginStroke(layer, channel, brush)) { foreach (var p in points) s.Add(new BrushSample(p.x, p.y)); s.Commit(); }
        }

        /// <summary>1100×700・タイル 64（作業ブロック 512 が 3×2、端で欠ける）。マスクのある塗りつぶし・分離と通過のグループ・クリッピング・
        /// 調整レイヤーと、乱数の筆の層。</summary>
        static PaintDocument Scene(out List<PaintLayer> layers, int seed = 7)
        {
            var doc = new PaintDocument(1100, 700, 64); var random = new System.Random(seed);
            layers = new List<PaintLayer>();
            var bottom = doc.AddLayer("Bottom"); layers.Add(bottom);
            var fill = doc.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(40, 160, 90, 200) }, { PaintChannel.Roughness, new Rgba32(120, 120, 120, 255) } });
            doc.SetLayerBlendMode(fill.Id, LayerBlendMode.Multiply); doc.AddLayerMask(fill.Id); doc.SetLayerMaskInverted(fill.Id, true);
            var group = doc.AddGroup("Group"); doc.SetLayerBlendMode(group.Id, LayerBlendMode.Overlay);
            var inner = doc.AddLayer("Inner"); doc.MoveLayerTo(inner.Id, group.Id, 0); layers.Add(inner);
            var pass = doc.AddGroup("Pass"); doc.SetLayerOpacity(pass.Id, .7);
            var passInner = doc.AddLayer("Pass inner"); doc.MoveLayerTo(passInner.Id, pass.Id, 0); layers.Add(passInner);
            var top = doc.AddLayer("Top"); layers.Add(top);
            var clipped = doc.AddLayer("Clipped"); doc.SetLayerClipping(clipped.Id, true); layers.Add(clipped);
            doc.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels(.1, .9, 1.4, .05, .95));
            foreach (var layer in layers)
                foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Roughness })
                {
                    if (!layer.IsChannelEnabled(channel)) doc.SetChannelEnabled(layer.Id, channel, true);
                    var color = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)(96 + random.Next(160)));
                    Stroke(doc, layer.Id, channel, color, Enumerable.Range(0, 6).Select(_ => ((double)random.Next(1100), (double)random.Next(700))).ToArray());
                }
            doc.ClearHistory();
            return doc;
        }

        /// <summary>2048×1536・タイル 128（作業ブロック 512 が 4×3）。キャンバス全面の不透明な塗りつぶし（どのブロックにも中身がある）と、
        /// 模様の層（アルファは 0 か 96 以上。GpuInteractionTests と同じく、GPU の 1 段の許容が成り立つ）。筆はこの層に描く。</summary>
        static PaintDocument Grid(out PaintLayer fill, out PaintLayer paint)
        {
            var doc = new PaintDocument(2048, 1536, 128);
            fill = doc.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(200, 80, 40, 255) } });
            paint = doc.AddLayer("Paint"); var bytes = new byte[128 * 128 * 4];
            for (int ty = 0; ty < 12; ty++) for (int tx = 0; tx < 16; tx++)
                {
                    if ((tx + ty) % 3 == 0) continue;
                    for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
                        {
                            int gx = tx * 128 + x, gy = ty * 128 + y, i = (y * 128 + x) * 4;
                            bytes[i] = (byte)(gx / 9 * 17); bytes[i + 1] = (byte)(gy / 7 * 13); bytes[i + 2] = (byte)((gx + gy) / 11 * 7);
                            bytes[i + 3] = (gx / 8 + gy / 8) % 5 == 0 ? (byte)0 : (byte)(96 + (gx * 7 + gy * 3) % 160);
                        }
                    paint.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(tx, ty), bytes);
                }
            doc.ClearHistory();
            return doc;
        }
        static (int, int)[] AllBlocks(int columns, int rows) => Enumerable.Range(0, rows).SelectMany(y => Enumerable.Range(0, columns).Select(x => (x, y))).ToArray();

        // ───────── 終わった後の表示は区切らない合成と同じ ─────────

        /// <summary>筆・不透明度・合成モード・表示・Undo/Redo・マスクの濃さ・チャンネルの切り替え・別の文書を乱数で続け、毎回の変更を
        /// 予算 0（1 回に 1 ブロック）か小さな予算で流し終えた表示が、同じ経路で区切らずに合成した表示とバイト単位で同じこと（CPU の経路は
        /// それが CPU の正本。GPU の経路と CPU の正本の 1 段の一致は、小さなアルファの重なりの無い <see cref="Grid"/> の文書で確かめる）。
        /// 途中で次の変更が来る（流し終える前に値が変わる）ことも混ぜる。</summary>
        [TestCase("Cpu"), TestCase("Gpu"), TestCase("Frame")]
        public void DrainingTheWorkEndsWithTheSamePictureAsCompositingAtOnce(string kind)
        {
            var doc = Scene(out var layers);
            using (var sliced = Make(kind))
            using (var whole = Make(kind))
            {
                var random = new System.Random(3); var channel = PaintChannel.Color;
                sliced.Update(doc, channel, new CompositeSchedule { BudgetMilliseconds = 0 });
                RequirePath(sliced, kind);
                if (kind == "Frame") Assert.That(sliced.HasPendingWork, Is.False, "the whole-frame path does not slice (it uploads the whole frame)");
                int sliceCount = 0;
                void Check(string step)
                {
                    var schedule = new CompositeSchedule { BudgetMilliseconds = random.Next(3) == 0 ? 0.5 : 0, Visible = new RectInt(random.Next(1100), random.Next(700), 200, 150) };
                    int calls = Drain(sliced, doc, channel, schedule);
                    if (calls > 1) sliceCount++;
                    whole.Update(doc, channel);
                    CpuCompositingTests.AssertSameBytes(Read(whole), Read(sliced), step + ": the same bytes as compositing at once");
                    if (kind != "Gpu") CpuCompositingTests.AssertSameBytes(doc.Composite(channel), Read(sliced), step + ": the CPU reference");
                }
                Check("first");
                for (int i = 0; i < 30; i++)
                {
                    var layer = layers[random.Next(layers.Count)];
                    switch (random.Next(8))
                    {
                        case 0: case 1:
                            var color = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)(64 + random.Next(192)));
                            double x = random.Next(1100), y = random.Next(700);
                            Stroke(doc, layer.Id, channel, color, (x, y), (x + random.Next(-300, 300), y + random.Next(-200, 200)));
                            break;
                        case 2: doc.SetLayerOpacity(layer.Id, .2 + random.NextDouble() * .8, coalesce: true); break;
                        case 3: doc.SetLayerBlendMode(layer.Id, new[] { LayerBlendMode.Normal, LayerBlendMode.Multiply, LayerBlendMode.Screen, LayerBlendMode.Overlay }[random.Next(4)]); break;
                        case 4: if (doc.CanUndo) doc.Undo(); break;
                        case 5: if (doc.CanRedo) doc.Redo(); break;
                        case 6: doc.SetLayerMaskDensity(doc.Layers.First(l => l.Name == "Fill").Id, random.NextDouble()); break;
                        default: doc.SetLayerVisibility(layer.Id, !layer.Visible); break;
                    }
                    // 半分は、流し終える前にもう 1 つ変える（古い値の仕事が残らない）
                    if (random.Next(2) == 0)
                    {
                        sliced.Update(doc, channel, new CompositeSchedule { BudgetMilliseconds = 0 });
                        doc.SetLayerOpacity(layers[random.Next(layers.Count)].Id, .1 + random.NextDouble() * .9, coalesce: true);
                    }
                    Check("edit " + i);
                    if (i == 12)
                    {
                        // 流している途中でチャンネルを替え、戻す
                        doc.SetLayerOpacity(layers[0].Id, .5); sliced.Update(doc, channel, new CompositeSchedule { BudgetMilliseconds = 0 });
                        channel = PaintChannel.Roughness; Check("Roughness"); channel = PaintChannel.Color; Check("Color again");
                    }
                }
                // 流している途中で別の文書に替える
                doc.SetLayerOpacity(layers[1].Id, .33); sliced.Update(doc, channel, new CompositeSchedule { BudgetMilliseconds = 0 });
                doc = Scene(out layers, seed: 11); Check("another document");
                if (kind != "Frame") Assert.That(sliceCount, Is.GreaterThan(10), "most changes took several updates");
            }
        }

        // ───────── 半端なブロックを出さない ─────────

        /// <summary>合成の途中で表示を読むと、ブロックごとに、もう合成したブロックは新しい絵（同じ経路で 1 回で全部合成した表示とバイト
        /// 単位で同じ）、待っているブロックは前の絵のまま。ブロックの中で新旧が混ざらない。</summary>
        [TestCase("Cpu"), TestCase("Gpu")]
        public void EveryBlockShowsEitherItsOldOrItsNewPictureNeverAMix(string kind)
        {
            var doc = Scene(out var layers);
            using (var c = Make(kind))
            {
                c.Update(doc, PaintChannel.Color); RequirePath(c, kind);
                int b = c.BlockSize, columns = (1100 + b - 1) / b, rows = (700 + b - 1) / b;
                Assert.That(columns * rows, Is.EqualTo(6));
                foreach (var (name, edit) in new (string, Action)[]
                {
                    ("top opacity", () => doc.SetLayerOpacity(layers[3].Id, .4)),
                    ("bottom stroke across the canvas", () => Stroke(doc, layers[0].Id, PaintChannel.Color, new Rgba32(250, 250, 0, 255), (20, 20), (1080, 680), (20, 680))),
                    ("hide the group", () => doc.SetLayerVisibility(doc.Layers.First(l => l.Name == "Group").Id, false)),
                })
                {
                    var before = Read(c); edit(); var after = AtOnce(kind, doc, PaintChannel.Color);
                    var done = new HashSet<(int, int)>();
                    Drain(c, doc, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 0 }, call =>
                    {
                        Assert.That(c.LastProcessedBlocks.Count, Is.EqualTo(1), name + ": budget 0 composites one block per update");
                        foreach (var block in c.LastProcessedBlocks) done.Add(block);
                        var shown = Read(c);
                        foreach (var (bx, by) in AllBlocks(columns, rows))
                        {
                            int x0 = bx * b, y0 = by * b, x1 = Math.Min(x0 + b, 1100), y1 = Math.Min(y0 + b, 700), worst = 0;
                            var reference = done.Contains((bx, by)) ? after : before;
                            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) for (int k = 0; k < 4; k++)
                                    { int i = (y * 1100 + x) * 4 + k; worst = Math.Max(worst, Math.Abs(shown[i] - reference[i])); }
                            Assert.That(worst, Is.Zero, $"{name}, update {call}: block {bx},{by} ({(done.Contains((bx, by)) ? "composited: the new picture" : "waiting: the old picture")})");
                        }
                    });
                    CpuCompositingTests.AssertSameBytes(after, Read(c), name + ": at the end");
                }
            }
        }

        // ───────── 合成する順 ─────────

        /// <summary>ストロークの所（急ぎ）→ 2D で見えている所 → 3D ビューの UV の所 → 残り（見ている所の真ん中に近い順）。</summary>
        [TestCase("Cpu"), TestCase("Gpu")]
        public void BlocksGoStrokeFirstThenTheVisibleViewThenTheModelThenTheRest(string kind)
        {
            var doc = Grid(out var fill, out var paint);
            using (var c = Make(kind))
            {
                c.Update(doc, PaintChannel.Color); RequirePath(c, kind);
                Assert.That(c.BlockSize, Is.EqualTo(512));
                var visible = new RectInt(1100, 600, 300, 300); // ブロック (2, 1) の中
                var model = new HashSet<TileCoord>(); for (int ty = 8; ty < 12; ty++) for (int tx = 0; tx < 4; tx++) model.Add(new TileCoord(tx, ty)); // ブロック (0, 2)
                var schedule = new CompositeSchedule { BudgetMilliseconds = 0, Visible = visible, ModelTiles = model };
                doc.SetLayerOpacity(fill.Id, .6); // 全部のブロック
                c.Update(doc, PaintChannel.Color, schedule);
                Assert.That(c.LastProcessedBlocks, Is.EqualTo(new[] { (2, 1) }), "what the 2D view shows first");
                // ストローク: 描いた所は、待っているほかのブロックより先に（ウィンドウはストロークの最中に急ぎの印を付ける。ストロークの
                // 最中は文書はストロークでしか変わらないので、その回に読んだ変更はストロークのもの）
                var brush = Hard; brush.Color = new Rgba32(0, 0, 0, 255);
                using (var s = doc.BeginStroke(paint.Id, PaintChannel.Color, brush))
                {
                    s.Add(new BrushSample(1800, 300)); // ブロック (3, 0)
                    c.Update(doc, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 0, Urgent = true, Visible = visible, ModelTiles = model });
                    Assert.That(c.LastProcessedBlocks, Is.EqualTo(new[] { (3, 0) }), "the stroke first (the pending opacity change in the same block goes with it)");
                    s.Commit();
                }
                var order = new List<(int, int)>();
                Drain(c, doc, PaintChannel.Color, schedule, _ => order.AddRange(c.LastProcessedBlocks));
                Assert.That(order[0], Is.EqualTo((0, 2)), "then what the model's UVs use");
                // 残り: 長く待っている順、同じなら見ている所の真ん中に近い順。確定で筆の所がまた変わったと記録されたら、そのブロックは一番新しい
                var rest = order.Skip(1).ToList();
                if (rest.Remove((3, 0))) Assert.That(order.Last(), Is.EqualTo((3, 0)), "the block that changed last waits least");
                var expected = AllBlocks(4, 3).Where(p => p != (2, 1) && p != (0, 2) && p != (3, 0))
                    .OrderBy(p => { long dx = p.Item1 * 1024 + 512 - (1100 * 2 + 300), dy = p.Item2 * 1024 + 512 - (600 * 2 + 300); return dx * dx + dy * dy; }).ToArray();
                Assert.That(rest, Is.EqualTo(expected));
                AssertShows(c, doc.Composite(PaintChannel.Color), "at the end");
            }
        }

        /// <summary>スライダーを動かし続ける（毎回の更新の前に値が変わる）: 待っているブロックは最新の値で合成され、長く待っている順なので、
        /// 1 回に 1 ブロックでもブロックの数の回数でどのブロックも新しくなる（見ている所の真ん中だけが新しくなり続けることは無い）。
        /// 止めて流し終えれば、最後の値の合成と同じ。</summary>
        [TestCase("Cpu"), TestCase("Gpu")]
        public void ASliderThatKeepsMovingLetsEveryBlockCatchUpAndEndsAtTheLastValue(string kind)
        {
            var doc = Grid(out var fill, out _);
            using (var c = Make(kind))
            {
                c.Update(doc, PaintChannel.Color); RequirePath(c, kind);
                var schedule = new CompositeSchedule { BudgetMilliseconds = 0, Visible = new RectInt(0, 0, 2048, 1536) };
                var seen = new HashSet<(int, int)>();
                for (int i = 0; i < 12; i++)
                {
                    doc.SetLayerOpacity(fill.Id, .3 + .05 * i, coalesce: true);
                    c.Update(doc, PaintChannel.Color, schedule);
                    Assert.That(c.LastProcessedBlocks.Count, Is.EqualTo(1));
                    Assert.That(seen.Add(c.LastProcessedBlocks[0]), Is.True, "update " + i + ": a block that has waited longer goes first");
                    Assert.That(c.PendingBlockCount, Is.EqualTo(11), "the change keeps every other block waiting");
                }
                Assert.That(seen.Count, Is.EqualTo(12), "every block was refreshed while the slider moved");
                doc.EndCoalescing();
                Drain(c, doc, PaintChannel.Color, schedule);
                AssertShows(c, doc.Composite(PaintChannel.Color), "the last value");
            }
        }

        // ───────── 時間の予算 ─────────

        /// <summary>差し替えた時計でブロック 1 つ = 3 ms のとき、予算 10 ms の Update は 1 回 12 ms（予算 + ブロック 1 つ）を超えず、毎回少なくとも
        /// 1 ブロック進む。予算 0・負・NaN は 1 回に 1 ブロック。ストローク（急ぎ）のブロックは予算を超えても全部その回に出す。</summary>
        [TestCase("Cpu"), TestCase("Gpu")]
        public void AnUpdateKeepsToItsBudgetWithinOneBlock(string kind)
        {
            var doc = Grid(out var fill, out var paint);
            using (var c = Make(kind))
            {
                var clock = new FakeClock(c, 3);
                c.Update(doc, PaintChannel.Color); RequirePath(c, kind);
                doc.SetLayerOpacity(fill.Id, .7);
                var perCall = new List<(double ms, int blocks)>();
                Drain(c, doc, PaintChannel.Color, null, _ => { });
                doc.SetLayerOpacity(fill.Id, .5);
                double last = clock.Now;
                int calls = Drain(c, doc, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 10 }, _ => { perCall.Add((clock.Now - last, c.LastProcessedBlocks.Count)); last = clock.Now; });
                Assert.That(perCall.Select(p => p.blocks).Sum(), Is.EqualTo(12));
                foreach (var (ms, blocks) in perCall)
                {
                    Assert.That(blocks, Is.GreaterThanOrEqualTo(1));
                    Assert.That(ms, Is.LessThanOrEqualTo(10 + 3), "at most one block past the budget");
                }
                Assert.That(calls, Is.EqualTo(3), "four blocks of 3 ms fit a 10 ms budget with one over: " + string.Join(", ", perCall));
                double value = .4;
                foreach (var budget in new[] { 0, -5, double.NaN, 1 })
                {
                    doc.SetLayerOpacity(fill.Id, value += .01);
                    int n = Drain(c, doc, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = budget }, _ => Assert.That(c.LastProcessedBlocks.Count, Is.EqualTo(1), "budget " + budget));
                    Assert.That(n, Is.EqualTo(12), "budget " + budget);
                }
                // ストロークの所は予算を超えても出す（4 ブロックを横切る線）。前の変更は先に予定に入っている（ウィンドウは描くたびに読む）
                doc.SetLayerOpacity(fill.Id, .45); c.Update(doc, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 0 });
                var brush = Hard; brush.Color = new Rgba32(255, 255, 255, 255);
                using (var s = doc.BeginStroke(paint.Id, PaintChannel.Color, brush))
                {
                    s.Add(new BrushSample(100, 1300)); s.Add(new BrushSample(1950, 1300));
                    last = clock.Now;
                    c.Update(doc, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 1, Urgent = true });
                    Assert.That(c.LastProcessedBlocks, Is.EquivalentTo(new[] { (0, 2), (1, 2), (2, 2), (3, 2) }), "the whole stroke in one update, over the budget");
                    Assert.That(clock.Now - last, Is.EqualTo(12));
                    s.Commit();
                }
                Drain(c, doc, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 1 });
                AssertShows(c, doc.Composite(PaintChannel.Color), "at the end");
            }
        }

        /// <summary>実際の時計で: 4 ms の予算の 1 回（の中央値。GC などで 1 回だけ長くなることはある）は、同じ変更を 1 回で合成するより十分に
        /// 短い（機械の負荷に左右されない緩い条件: 半分未満。1 回で 20 ms かからない変更は確かめない）。2048²・8 層の真ん中の層の不透明度（写しを取る最初の 1 回）と、その次の 1 回。</summary>
        [TestCase("Cpu"), TestCase("Gpu")]
        public void ARealBudgetSlicesALargeChange(string kind)
        {
            PaintDocument Make8()
            {
                var d = new PaintDocument(2048, 2048, 128) { SourceBudgetBytes = 1L << 30 };
                var random = new System.Random(5); var bytes = new byte[128 * 128 * 4];
                for (int i = 0; i < 8; i++)
                {
                    var l = d.AddLayer("L" + i); var surface = l.GetChannel(PaintChannel.Color);
                    for (int ty = 0; ty < 16; ty++) for (int tx = 0; tx < 16; tx++)
                    { random.NextBytes(bytes); for (int k = 3; k < bytes.Length; k += 4) bytes[k] = (byte)(i == 0 ? 255 : bytes[k] | 0x40); surface.ImportTile(new TileCoord(tx, ty), bytes); }
                    if (i > 0) d.SetLayerBlendMode(l.Id, i % 2 == 0 ? LayerBlendMode.Multiply : LayerBlendMode.Overlay);
                }
                d.ClearHistory(); return d;
            }
            var a = Make8(); var b = Make8();
            using (var sliced = Make(kind))
            using (var whole = Make(kind))
            {
                sliced.Update(a, PaintChannel.Color); whole.Update(b, PaintChannel.Color); RequirePath(sliced, kind);
                for (int round = 0; round < 2; round++)
                {
                    a.SetLayerOpacity(a.Layers[4].Id, .5 + .2 * round); b.SetLayerOpacity(b.Layers[4].Id, .5 + .2 * round);
                    whole.Update(b, PaintChannel.Color); double atOnce = whole.LastUpdateMilliseconds;
                    var times = new List<double>();
                    int calls = Drain(sliced, a, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 4 }, _ => times.Add(sliced.LastUpdateMilliseconds));
                    times.Sort(); double median = times[times.Count / 2], longest = times[times.Count - 1];
                    CpuCompositingTests.AssertSameBytes(Read(whole), Read(sliced), "round " + round + ": the same bytes as compositing at once");
                    if (kind != "Gpu") CpuCompositingTests.AssertSameBytes(a.Composite(PaintChannel.Color), Read(sliced), "round " + round + ": the CPU reference");
                    if (atOnce < 20) continue; // 1 回で済む速さなら、区切りの確かめにならない（実 GPU の 2 回目など）
                    Assert.That(calls, Is.GreaterThan(1), $"round {round}: {atOnce:F1} ms at once");
                    Assert.That(median, Is.LessThan(atOnce / 2), $"round {round}: the median update {median:F1} ms (longest {longest:F1}), {atOnce:F1} ms at once, {calls} updates");
                }
            }
        }

        // ───────── 間引いた合成（ドラッグの最中） ─────────

        /// <summary>1024²・タイル 128（作業ブロック 512 が 2×2）。不透明な塗りつぶしと、どのタイルにも模様のある層。</summary>
        static PaintDocument Square(out PaintLayer paint)
        {
            var doc = new PaintDocument(1024, 1024, 128);
            doc.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(30, 120, 200, 255) } });
            paint = doc.AddLayer("Paint"); var bytes = new byte[128 * 128 * 4];
            for (int ty = 0; ty < 8; ty++) for (int tx = 0; tx < 8; tx++)
                {
                    for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
                        {
                            int gx = tx * 128 + x, gy = ty * 128 + y, i = (y * 128 + x) * 4;
                            bytes[i] = (byte)(gx * 3 + gy); bytes[i + 1] = (byte)(gx ^ gy); bytes[i + 2] = (byte)(gy * 5); bytes[i + 3] = (byte)(96 + (gx * 7 + gy * 3) % 160);
                        }
                    paint.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(tx, ty), bytes);
                }
            doc.ClearHistory();
            return doc;
        }

        /// <summary>スライダーのドラッグ（PreviewStep 4）: 1 回で終わらない変更が続くと、待っているタイルを 4 で間引いた正確な合成で先に
        /// 全部見せる（表示の画素はどれも、その 4×4 の真ん中の画素の全解像度の合成とバイト単位で同じ）。合成し終えたブロックは全解像度。
        /// マウスを止めているあいだは作り直さず全解像度が進む。値を前に全解像度で見せた値へ戻しても、間引いた絵は残らない（署名が同じ
        /// ブロックでも送り直す）。ドラッグが終われば全解像度に揃い、置き場を手放す。写しは予算の内側。</summary>
        [Test] public void ADragShowsAnExactSampledPreviewUntilTheFullResolutionArrives()
        {
            var doc = Square(out var paint);
            using (var c = Make("Cpu"))
            {
                var clock = new FakeClock(c, 5);
                c.Update(doc, PaintChannel.Color); RequirePath(c, "Cpu");
                var drag = new CompositeSchedule { BudgetMilliseconds = 1, PreviewStep = 4 };
                doc.SetLayerOpacity(paint.Id, .5, coalesce: true);
                c.Update(doc, PaintChannel.Color, drag);
                Assert.That(c.LastPreviewed, Is.False, "the update before finished everything, so this change may fit (no preview yet)");
                Assert.That(c.LastProcessedBlocks.Count, Is.EqualTo(1)); Assert.That(c.HasPendingWork, Is.True);
                doc.SetLayerOpacity(paint.Id, .4, coalesce: true);
                c.Update(doc, PaintChannel.Color, drag);
                if (!c.LastPreviewed) Assert.Ignore("The scaled copy pass (TileComposite) cannot be used here: " + c.Backend);
                Assert.That(c.PreviewStepShown, Is.EqualTo(4));
                var full = doc.Composite(PaintChannel.Color); var shown = Read(c); var done = new HashSet<(int, int)>(c.LastProcessedBlocks);
                Assert.That(c.PreviewTileCount, Is.EqualTo(64 - 16 * done.Count), "every waiting tile shows the preview");
                for (int y = 0; y < 1024; y++)
                    for (int x = 0; x < 1024; x++)
                    {
                        bool exact = done.Contains((x / 512, y / 512));
                        int e = exact ? (y * 1024 + x) * 4 : ((y / 4 * 4 + 2) * 1024 + x / 4 * 4 + 2) * 4, a = (y * 1024 + x) * 4;
                        if (shown[a] != full[e] || shown[a + 1] != full[e + 1] || shown[a + 2] != full[e + 2] || shown[a + 3] != full[e + 3])
                            Assert.Fail($"pixel ({x}, {y}) in a {(exact ? "composited" : "waiting")} block: shown {shown[a]},{shown[a + 1]},{shown[a + 2]},{shown[a + 3]}");
                    }
                // マウスを止める: 文書は変わらないので作り直さず、時間を全解像度に回す
                int previewed = c.PreviewTileCount;
                c.Update(doc, PaintChannel.Color, drag);
                Assert.That(c.LastPreviewed, Is.False); Assert.That(c.LastProcessedBlocks.Count, Is.EqualTo(1));
                Assert.That(c.PreviewTileCount, Is.EqualTo(previewed - 16), "a block at full resolution no longer shows the preview");
                // 最初に全解像度で合成した値（0.5）へ戻す
                doc.SetLayerOpacity(paint.Id, .5, coalesce: true);
                c.Update(doc, PaintChannel.Color, drag);
                Assert.That(c.LastPreviewed, Is.True);
                Assert.That(c.ResidentBytes, Is.LessThanOrEqualTo(c.ResidentBudgetBytes));
                doc.EndCoalescing();
                Drain(c, doc, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 1 }, _ => Assert.That(c.LastPreviewed, Is.False, "no preview once the drag ends"));
                CpuCompositingTests.AssertSameBytes(doc.Composite(PaintChannel.Color), Read(c), "full resolution after the drag (no preview left over)");
                Assert.That(c.PreviewTileCount, Is.Zero); Assert.That(c.PreviewStepShown, Is.Zero);
                Assert.That(c.ResidentBytes, Is.LessThanOrEqualTo(c.ResidentBudgetBytes));
            }
        }

        /// <summary>間引いた合成を使わないとき: GPU の経路（写しが GPU にあり、構造の変更は 1 回で済む）、間引きの幅が文書の幅やタイルを
        /// 割り切らない、予算が無制限（変更が次の回へ残らない）、1 回で終わる変更、全解像度でも予算の 4 回分ほどで揃う変更。どれも表示は
        /// 正確な合成だけ。</summary>
        [TestCase("Cpu"), TestCase("Gpu")]
        public void ThePreviewIsLeftOutWhereItCannotOrNeedNotHelp(string kind)
        {
            var doc = Square(out var paint);
            using (var c = Make(kind))
            {
                var clock = new FakeClock(c, 5);
                c.Update(doc, PaintChannel.Color); RequirePath(c, kind);
                void Drag(CompositeSchedule s, string why)
                {
                    for (int i = 0; i < 4; i++) { doc.SetLayerOpacity(paint.Id, .3 + .1 * i, coalesce: true); c.Update(doc, PaintChannel.Color, s); Assert.That(c.LastPreviewed, Is.False, why); }
                    doc.EndCoalescing(); Drain(c, doc, PaintChannel.Color, s);
                    Assert.That(c.PreviewTileCount, Is.Zero, why);
                    CpuCompositingTests.AssertSameBytes(AtOnce(kind, doc, PaintChannel.Color), Read(c), why);
                }
                if (kind == "Gpu") { Drag(new CompositeSchedule { BudgetMilliseconds = 1, PreviewStep = 4 }, "the GPU path"); return; }
                Drag(new CompositeSchedule { BudgetMilliseconds = 1, PreviewStep = 3 }, "a step that does not divide the tile");
                Drag(new CompositeSchedule { PreviewStep = 4 }, "no limit: every change ends in its update");
                Drag(new CompositeSchedule { BudgetMilliseconds = 100, PreviewStep = 4 }, "four blocks of 5 ms fit 100 ms");
            }
            // 全解像度でも予算 4 回分ほどで揃う変更（ブロック 4 つ × 0.5 ms、予算 1 ms）は、間引いた絵を挟まずに全解像度で出す
            using (var c = Make(kind))
            {
                if (kind == "Gpu") return;
                var clock = new FakeClock(c, .5);
                c.Update(doc, PaintChannel.Color);
                for (int i = 0; i < 4; i++) { doc.SetLayerOpacity(paint.Id, .6 + .05 * i, coalesce: true); c.Update(doc, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 1, PreviewStep = 4 }); Assert.That(c.LastPreviewed, Is.False, "two updates of full resolution are enough"); }
                doc.EndCoalescing();
            }
            var odd = Scene(out var layers); // 1100 × 700: 8 で割り切れない
            using (var c = Make(kind))
            {
                if (kind == "Gpu") return;
                var clock = new FakeClock(c, 5);
                c.Update(odd, PaintChannel.Color);
                for (int i = 0; i < 3; i++) { odd.SetLayerOpacity(layers[3].Id, .3 + .1 * i, coalesce: true); c.Update(odd, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 1, PreviewStep = 8 }); Assert.That(c.LastPreviewed, Is.False, "1100 is not a multiple of 8"); }
            }
        }

        // ───────── 切り替え・解放 ─────────

        /// <summary>仕事が残っているときに: 別のチャンネル・別の文書に替えると、残りの仕事を捨て、表示を透明にしてから新しい絵を合成する
        /// （前の文書の絵と混ぜない）。写しの予算を 0 にしても、写しを捨てても、Dispose しても壊れない（Dispose は残りを捨てる）。</summary>
        [TestCase("Cpu"), TestCase("Gpu")]
        public void SwitchingReleasingAndDisposingDropTheRemainingWork(string kind)
        {
            var doc = Scene(out var layers); var fill = doc.Layers.First(l => l.Name == "Fill"); // キャンバス全面（6 ブロック）
            var c = Make(kind);
            try
            {
                c.Update(doc, PaintChannel.Color); RequirePath(c, kind);
                var once = new CompositeSchedule { BudgetMilliseconds = 0 };
                doc.SetLayerOpacity(fill.Id, .3); c.Update(doc, PaintChannel.Color, once);
                Assert.That(c.HasPendingWork, Is.True);
                c.Update(doc, PaintChannel.Roughness, once);
                Assert.That(c.LastCpuFullFrame, Is.True, "a channel switch starts over");
                Drain(c, doc, PaintChannel.Roughness, once); AssertSameAsAtOnce(c, kind, doc, PaintChannel.Roughness, "Roughness");

                doc.SetLayerOpacity(fill.Id, .6); c.Update(doc, PaintChannel.Roughness, once);
                Assert.That(c.HasPendingWork, Is.True);
                var other = new PaintDocument(1100, 700, 64); var only = other.AddLayer("Only");
                Stroke(other, only.Id, PaintChannel.Color, new Rgba32(10, 200, 30, 255), (100, 100), (150, 120)); // ブロック (0, 0) だけ
                c.Update(other, PaintChannel.Color, once);
                Assert.That(c.HasPendingWork, Is.False, "one block of content");
                var shown = Read(c);
                AssertSameAsAtOnce(c, kind, other, PaintChannel.Color, "another document: its one block, and transparent elsewhere (not the old document's picture)");
                Assert.That(shown.Skip(1100 * 4 * 600).Take(4 * 1100).All(v => v == 0), Is.True);

                doc.SetLayerOpacity(fill.Id, .4); c.Update(doc, PaintChannel.Color, once);
                Assert.That(c.LastCpuFullFrame, Is.True, "back to the first document: from the start");
                Drain(c, doc, PaintChannel.Color, once);
                doc.SetLayerOpacity(fill.Id, .45); c.Update(doc, PaintChannel.Color, once);
                c.ResidentBudgetBytes = 0; Drain(c, doc, PaintChannel.Color, once);
                AssertSameAsAtOnce(c, kind, doc, PaintChannel.Color, "no copies kept");
                c.ResidentBudgetBytes = 256L << 20;
                doc.SetLayerOpacity(fill.Id, .5); c.Update(doc, PaintChannel.Color, once); doc.SetLayerOpacity(fill.Id, .55); c.Update(doc, PaintChannel.Color, once);
                c.ReleaseResidentCaches(); Assert.That(c.HasPendingWork, Is.True, "releasing the copies keeps the work");
                Drain(c, doc, PaintChannel.Color, once); AssertSameAsAtOnce(c, kind, doc, PaintChannel.Color, "after releasing the copies");

                doc.SetLayerOpacity(fill.Id, .9); c.Update(doc, PaintChannel.Color, once);
                Assert.That(c.HasPendingWork, Is.True);
                c.Dispose();
                Assert.That(c.HasPendingWork, Is.False, "Dispose drops the work");
                c.Update(doc, PaintChannel.Color); AssertSameAsAtOnce(c, kind, doc, PaintChannel.Color, "used again after Dispose");
            }
            finally { c.Dispose(); }
        }
    }
}
