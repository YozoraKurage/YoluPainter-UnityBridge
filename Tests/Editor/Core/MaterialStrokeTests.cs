using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// マテリアルで塗る（1 回のストロークで複数のチャンネル）: どのチャンネルも、そのチャンネルだけを同じ設定・同じ乱数の種で塗ったときと
    /// バイトまで同じ（筆先・ゆらぎ・散布・ダブごとの色・デュアルブラシ・紙の質感・手ぶれ補正・入り抜き・曲線・選択範囲・面のダブ・並列の
    /// ダブ）。1 回の Undo/Redo で全部、取消・例外で全部が戻り、ストロークが有効にしたチャンネルも戻る。巻き戻しの予算は全部のチャンネルの
    /// 合計。ロック（透明部分・画像・すべて）・消しゴム・断る相手は 1 チャンネルのストロークと同じ。
    /// </summary>
    public sealed class MaterialStrokeTests
    {
        const int W = 72, H = 56, Tile = 16;
        static readonly PaintChannel[] All = (PaintChannel[])Enum.GetValues(typeof(PaintChannel));

        static byte V(int i) => (byte)(((i % 16) + 16) % 16 * 17);
        /// <summary>同じ中身の文書: 層 "paint" に Color・Roughness・Normal があり（Height・Metallic・Emission は無い）、アルファはまちまちで
        /// アルファ 0 の画素も RGB を持つ。下に不透明の下地の層。</summary>
        static PaintDocument Document(out Guid paint) => Document(out paint, W, H, Tile);
        static PaintDocument Document(out Guid paint, int W, int H, int Tile)
        {
            // ID は決まった値（同じ中身の文書をバイトで比べるため）
            var d = new PaintDocument(W, H, Tile, 256L << 20, new Guid("6d8f0c2e-0000-4000-8000-000000000001")) { ActiveStrokeBudgetBytes = 64L << 20 };
            var back = d.AddLayer("back", new Guid("6d8f0c2e-0000-4000-8000-000000000002")).GetChannel(PaintChannel.Color);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) back.SetPixel(x, y, new Rgba32(V(x / 4), V(y / 4), 128, 255));
            var l = d.AddLayer("paint", new Guid("6d8f0c2e-0000-4000-8000-000000000003"));
            foreach (var c in new[] { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Normal })
            {
                var s = l.GetChannel(c);
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                {
                    if ((x + y) % 5 == 0) continue;
                    byte a = (byte)((x / 9) % 4 == 0 ? 0 : (x / 9) % 4 == 1 ? 90 : (x / 9) % 4 == 2 ? 180 : 255);
                    s.SetPixel(x, y, new Rgba32(V(x + (int)c), V(y * 3), V(x + y), a));
                }
            }
            d.ClearHistory(); paint = l.Id; return d;
        }
        static readonly ChannelPaint[] Material =
        {
            new ChannelPaint(PaintChannel.Color, new Rgba32(220, 40, 30, 255)),
            new ChannelPaint(PaintChannel.Roughness, new Rgba32(200, 200, 200, 255)),
            new ChannelPaint(PaintChannel.Metallic, new Rgba32(255, 255, 255, 230)),
            new ChannelPaint(PaintChannel.Height, new Rgba32(90, 90, 90, 255)),
            new ChannelPaint(PaintChannel.Normal, new Rgba32(170, 128, 233, 255)),
            new ChannelPaint(PaintChannel.Emission, new Rgba32(10, 250, 120, 255)),
        };
        static Rgba32[] Pixels(PaintDocument d, Guid id, PaintChannel c)
        { var l = d.GetLayer(id); int w = d.Width, h = d.Height; var p = new Rgba32[w * h]; for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) p[y * w + x] = l.GetPixel(c, x, y); return p; }

        static readonly BrushSample[] Line =
        {
            new BrushSample(6.5, 10.25, .3, 0), new BrushSample(20, 18, .9, .1), new BrushSample(33.3, 30.6, .6, .2),
            new BrushSample(47, 33, 1, .3), new BrushSample(60.5, 44.2, .5, .4), new BrushSample(66, 47, .8, .5),
        };
        static void Feed(BrushStroke s, IEnumerable<BrushSample> samples) { foreach (var p in samples) s.Add(p); }

        /// <summary>Brush variants that exercise every per-pixel path of the stroke.</summary>
        static IEnumerable<TestCaseData> Brushes()
        {
            BrushSettings Basic() => new BrushSettings { Radius = 7, Hardness = .4, Spacing = .2, Opacity = .85, Flow = .45, Seed = 1234 };
            yield return new TestCaseData((Func<BrushSettings>)Basic).SetName("Round soft brush, pressure");
            yield return new TestCaseData((Func<BrushSettings>)(() => { var s = Basic(); s.HueJitter = .5; s.BrightnessJitter = .3; s.ForegroundBackgroundJitter = .4; s.SecondaryColor = new Rgba32(0, 0, 255, 200); s.ColorPerTip = true; return s; }))
                .SetName("Colour dynamics per tip (Color and Emission only)");
            yield return new TestCaseData((Func<BrushSettings>)(() => { var s = Basic(); s.Purity = .5; s.ColorPerTip = false; s.SizeJitter = .4; s.Scatter = .6; s.Count = 3; s.OpacityJitter = .3; return s; }))
                .SetName("Jitter, scatter and one colour per stroke");
            yield return new TestCaseData((Func<BrushSettings>)(() =>
            {
                var s = Basic(); s.Dual = new DualBrush { Radius = 3, Spacing = .3, Scatter = .5, Count = 2, Mode = DualBrushMode.Overlay };
                s.Texture = new BrushTip("grain", 4, 4, Enumerable.Range(0, 16).Select(i => (byte)(i * 16)).ToArray()); s.TextureDepth = .7; s.TextureScale = 2;
                s.Tip = new BrushTip("cross", 3, 3, new byte[] { 0, 255, 0, 255, 128, 255, 0, 255, 0 }); s.Angle = 30; s.Roundness = .6; s.FollowDirection = true; return s;
            })).SetName("Sampled tip, dual brush and paper texture");
            yield return new TestCaseData((Func<BrushSettings>)(() => { var s = Basic(); s.Stabilizer = 4; s.TaperIn = 10; s.TaperOut = 12; s.CurveInterpolation = true; s.FadeFlow = 30; return s; }))
                .SetName("Stabilizer, tapers, curve and fade");
        }

        /// <summary>The material stroke, and for comparison each channel painted alone with the same settings and value.</summary>
        static void AssertSameAsOneChannelStrokes(Func<BrushSettings> brush, Action<PaintDocument> prepare, Action<BrushStroke> feed, IReadOnlyList<ChannelPaint> material, string what, Func<(PaintDocument, Guid)> make = null)
        {
            make = make ?? (() => (Document(out var g), g));
            var (multi, id) = make(); prepare?.Invoke(multi); int steps = multi.UndoCount;
            using (var s = multi.BeginMaterialStroke(id, material, brush())) { feed(s); Assert.That(s.Commit(), Is.True, what); }
            foreach (var m in material)
            {
                var (single, sid) = make(); prepare?.Invoke(single);
                if (!single.GetLayer(sid).IsChannelEnabled(m.Channel)) { single.SetChannelEnabled(sid, m.Channel, true); }
                var b = brush(); b.Color = m.Value;
                using (var s = single.BeginStroke(sid, m.Channel, b)) { feed(s); s.Commit(); }
                Assert.That(Pixels(multi, id, m.Channel), Is.EqualTo(Pixels(single, sid, m.Channel)), what + ": " + m.Channel);
                Assert.That(multi.Composite(m.Channel), Is.EqualTo(single.Composite(m.Channel)), what + ": composite of " + m.Channel);
            }
            Assert.That(multi.UndoCount, Is.EqualTo(steps + 1), what + ": one undo step");
        }

        [TestCaseSource(nameof(Brushes))]
        public void EveryChannelGetsTheBytesOfAStrokeOfThatChannelAlone(Func<BrushSettings> brush)
        {
            AssertSameAsOneChannelStrokes(brush, null, s => Feed(s, Line), Material, "2D stroke");
        }

        [Test] public void TheSameHoldsWithASelectionAndForMeshDabs()
        {
            BrushSettings B() => new BrushSettings { Radius = 9, Hardness = .7, Opacity = .9, Flow = .6, Seed = 77, HueJitter = .4 };
            // 選択範囲（ぼかした楕円）の中だけ、選んだ割合で
            void Select(PaintDocument d) => d.SetSelection(SelectionMask.Ellipse(d, 36, 28, 26, 18).Feather(4));
            AssertSameAsOneChannelStrokes(B, Select, s => Feed(s, Line), Material, "with a selection");
            // 3D ビューのダブ（呼ぶ側が被覆率を出す ApplyPixel）
            void Mesh(BrushStroke s)
            {
                for (int k = 0; k < 3; k++) for (int y = 5; y < 50; y++) for (int x = 4 + 15 * k; x < 30 + 15 * k; x++)
                    s.ApplyPixel(x, y, ((x * 7 + y * 3 + k) % 11) / 10.0, .4 + .2 * k);
            }
            AssertSameAsOneChannelStrokes(B, null, Mesh, Material, "mesh dabs");
            AssertSameAsOneChannelStrokes(B, Select, Mesh, Material, "mesh dabs with a selection");
        }

        [Test] public void LargeDabsOnWorkerThreadsGiveTheSameBytes()
        {
            // 外接の箱が 128² 以上のダブは、ストロークが持ったタイルをワーカーで塗る（BrushStroke.ParallelDabPixels）
            int degree = CoreParallelism.MaxDegreeOfParallelism;
            try
            {
                BrushSettings B() => new BrushSettings { Radius = 75, Hardness = .5, Spacing = .1, Opacity = .8, Flow = .3, Seed = 5, HueJitter = .3, ColorPerTip = true };
                var line = new[] { new BrushSample(60, 60, .5, 0), new BrushSample(200, 110, 1, .1), new BrushSample(330, 190, .7, .2) };
                foreach (int d in new[] { 1, 3, 0 })
                {
                    CoreParallelism.MaxDegreeOfParallelism = d;
                    AssertSameAsOneChannelStrokes(B, null, s => Feed(s, line), Material, "degree " + d, () => (Document(out var g, 384, 256, 32), g));
                }
            }
            finally { CoreParallelism.MaxDegreeOfParallelism = degree; }
        }

        [Test] public void OneChannelThroughTheMaterialStrokeIsTheOneChannelStroke()
        {
            var b = new BrushSettings { Radius = 8, Hardness = .6, Opacity = .7, Flow = .5, Seed = 3, Color = new Rgba32(1, 2, 3, 200) };
            var a = Document(out var ida); var c = Document(out var idc);
            using (var s = a.BeginMaterialStroke(ida, new[] { new ChannelPaint(PaintChannel.Roughness, b.Color) }, b.Clone())) { Feed(s, Line); s.Commit(); }
            using (var s = c.BeginStroke(idc, PaintChannel.Roughness, b.Clone())) { Feed(s, Line); s.Commit(); }
            Assert.That(DocumentBinary.Write(a), Is.EqualTo(DocumentBinary.Write(c)), "the whole document, byte for byte");
            Assert.That(a.HistoryBytes, Is.EqualTo(c.HistoryBytes), "the same undo cost");
        }

        // ───────────── undo, cancel, budget ─────────────

        [Test] public void OneUndoTakesBackEveryChannelAndTheChannelsTheStrokeSwitchedOn()
        {
            var d = Document(out var id); var layer = d.GetLayer(id);
            var before = DocumentBinary.Write(d);
            Assert.That(layer.IsChannelEnabled(PaintChannel.Metallic), Is.False);
            var b = new BrushSettings { Radius = 6, Opacity = 1, Flow = 1, Hardness = 1, PressureOpacity = false, Seed = 9 };
            using (var s = d.BeginMaterialStroke(id, Material, b))
            {
                Assert.That(layer.IsChannelEnabled(PaintChannel.Metallic), Is.True, "switched on for the stroke");
                Feed(s, Line); Assert.That(s.Commit(), Is.True);
            }
            Assert.That(d.UndoCount, Is.EqualTo(1), "switching on and painting are one step");
            var painted = DocumentBinary.Write(d);
            foreach (var c in All) Assert.That(layer.IsChannelEnabled(c), Is.True, c.ToString());
            Assert.That(layer.GetPixel(PaintChannel.Metallic, 20, 18), Is.EqualTo(new Rgba32(255, 255, 255, 230)));
            Assert.That(d.Undo(), Is.True);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before), "undo leaves the document as it was, without empty channel surfaces");
            Assert.That(layer.TryGetChannel(PaintChannel.Metallic, out _), Is.False);
            Assert.That(d.Redo(), Is.True);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(painted), "redo brings every channel back");
            // 2 回目: 戻して、やり直して、もう一度。同じ面が付け直されている（作り直した面に戻すと画素が消える）
            d.Undo(); d.Redo();
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(painted));
            Assert.That(d.Composite(PaintChannel.Metallic).Any(v => v != 0), Is.True, "the redone pixels are in the layer's surface");
            // 保存して開いても同じ
            Assert.That(DocumentBinary.Write(DocumentBinary.Read(painted)), Is.EqualTo(painted));
        }

        [Test] public void RedoingASwitchedOnChannelKeepsLaterStrokesOnIt()
        {
            // 前からの不具合: SetChannelEnabled のやり直しが面を作り直し、その後のストロークのやり直しが外れた面へ画素を戻していた
            var d = Document(out var id);
            d.SetChannelEnabled(id, PaintChannel.Metallic, true);
            using (var s = d.BeginStroke(id, PaintChannel.Metallic, new BrushSettings { Radius = 5, Color = new Rgba32(200, 200, 200, 255) })) { Feed(s, Line); s.Commit(); }
            var painted = d.Composite(PaintChannel.Metallic);
            d.Undo(); d.Undo(); d.Redo(); d.Redo();
            Assert.That(d.Composite(PaintChannel.Metallic), Is.EqualTo(painted));
        }

        [TestCase("cancel")] [TestCase("dispose")] [TestCase("bad input")] [TestCase("commit without change")]
        public void ACancelledStrokeLeavesNothing(string how)
        {
            var d = Document(out var id); var before = DocumentBinary.Write(d); long revision = d.Revision;
            var b = new BrushSettings { Radius = 6, Seed = 2 };
            var s = d.BeginMaterialStroke(id, Material, b);
            if (how == "commit without change") { s.ApplyPixel(0, 0, 0); Assert.That(s.Commit(), Is.False); }
            else
            {
                Feed(s, Line.Take(4));
                if (how == "cancel") s.Cancel();
                else if (how == "dispose") s.Dispose();
                else Assert.Throws<ArgumentException>(() => s.Add(new BrushSample(10, 10, 1, -1)), "time going back cancels the stroke");
            }
            Assert.That(d.HasActiveStroke, Is.False);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before), how + ": every channel and the switched-on ones are back");
            Assert.That(d.UndoCount, Is.Zero); Assert.That(d.Revision, Is.GreaterThan(revision), "a change was announced");
            foreach (var c in new[] { PaintChannel.Metallic, PaintChannel.Height, PaintChannel.Emission }) Assert.That(d.GetLayer(id).TryGetChannel(c, out _), Is.False, c.ToString());
            s.Dispose();
        }

        [Test] public void TheRollbackBudgetCountsEveryChannelTogether()
        {
            var b = new BrushSettings { Radius = 10, Hardness = 1, Seed = 1 };
            // 1 チャンネルなら収まる予算で、3 チャンネルは断る（何も残さない）
            var one = Document(out var id1);
            long single;
            using (var s = one.BeginStroke(id1, PaintChannel.Color, b.Clone())) { Feed(s, Line); single = s.RollbackBytes; s.Commit(); }
            var d = Document(out var id); d.ActiveStrokeBudgetBytes = single; var before = DocumentBinary.Write(d);
            var three = Material.Where(m => m.Channel == PaintChannel.Color || m.Channel == PaintChannel.Roughness || m.Channel == PaintChannel.Height).ToArray();
            var stroke = d.BeginMaterialStroke(id, three, b.Clone());
            var ex = Assert.Throws<InvalidOperationException>(() => Feed(stroke, Line));
            Assert.That(ex.Message, Does.Contain("rollback budget"));
            Assert.That(d.HasActiveStroke, Is.False);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before), "refused safely: nothing changed, Height not left switched on");
            // 合計: 共有の被覆率（タイルごとに 64 + 16²×4）と、チャンネルごとの写し（Color と Roughness は中身がある、Height は空）
            d.ActiveStrokeBudgetBytes = 64L << 20;
            using (var s = d.BeginMaterialStroke(id, three, b.Clone()))
            {
                Feed(s, Line);
                long expected = 0; int tiles = 0;
                foreach (var coord in TilesTouched(s, d, id)) { tiles++; expected += 64 + Tile * Tile * 4 + CopyBytes(d, id, PaintChannel.Color, coord, before) + CopyBytes(d, id, PaintChannel.Roughness, coord, before); }
                Assert.That(s.RollbackBytes, Is.EqualTo(expected), tiles + " tiles");
                Assert.That(s.RollbackBytes, Is.GreaterThan(single));
                s.Cancel();
            }
        }
        /// <summary>The tiles a stroke on the layer has taken over (where any channel changed or was copied): every tile the dabs reached.</summary>
        static IEnumerable<TileCoord> TilesTouched(BrushStroke s, PaintDocument d, Guid id)
        {
            var field = typeof(BrushStroke).GetField("strokeTiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var dict = (System.Collections.IDictionary)field.GetValue(s);
            return dict.Keys.Cast<TileCoord>().ToList();
        }
        /// <summary>The payload of the channel's tile before the stroke (0 where it had none).</summary>
        static long CopyBytes(PaintDocument d, Guid id, PaintChannel c, TileCoord coord, byte[] saved)
        {
            var original = DocumentBinary.Read(saved).GetLayer(id);
            if (!original.TryGetChannel(c, out var surface) || !surface.HasTile(coord)) return 0;
            var bytes = new byte[Tile * Tile * 4]; surface.CopyTile(coord, bytes);
            bool uniform = true; for (int i = 4; i < bytes.Length && uniform; i++) uniform = bytes[i] == bytes[i % 4];
            return uniform ? 4 : bytes.Length;
        }

        // ───────────── locks, erase, refusals ─────────────

        [Test] public void LockedTransparentPixelsAreKeptPerChannel()
        {
            BrushSettings B() => new BrushSettings { Radius = 9, Hardness = .5, Opacity = .9, Flow = .5, Seed = 4 };
            void Lock(PaintDocument d) => d.SetLayerLocks(d.Layers[1].Id, LayerLocks.Transparency);
            AssertSameAsOneChannelStrokes(B, Lock, s => Feed(s, Line), Material, "transparency locked");
            var doc = Document(out var id); Lock(doc);
            var before = All.ToDictionary(c => c, c => Pixels(doc, id, c));
            using (var s = doc.BeginMaterialStroke(id, Material, B())) { Feed(s, Line); s.Commit(); }
            foreach (var c in All)
            {
                var after = Pixels(doc, id, c);
                for (int i = 0; i < after.Length; i++)
                {
                    Assert.That(after[i].A, Is.EqualTo(before[c][i].A), c + ": alpha kept at " + i);
                    if (before[c][i].A == 0) Assert.That(after[i], Is.EqualTo(before[c][i]), c + ": a transparent pixel keeps its RGB at " + i);
                }
            }
            Assert.That(doc.GetLayer(id).TryGetChannel(PaintChannel.Metallic, out var metallic) && metallic.TileCount > 0, Is.False, "an empty channel stays empty under the lock");
            // 消しゴムはアルファを減らすので断る（何も変えない）
            var saved = DocumentBinary.Write(doc);
            var eraser = B(); eraser.Erase = true;
            var ex = Assert.Throws<LayerLockedException>(() => doc.BeginMaterialStroke(id, Material, eraser));
            Assert.That(ex.Lock, Is.EqualTo(LayerLocks.Transparency));
            Assert.That(DocumentBinary.Write(doc), Is.EqualTo(saved));
        }

        [TestCase(LayerLocks.Pixels)] [TestCase(LayerLocks.All)]
        public void LockedImagePixelsRefuseTheStrokeBeforeAnyChannelIsSwitchedOn(LayerLocks locks)
        {
            var d = Document(out var id); d.SetLayerLocks(id, locks);
            var before = DocumentBinary.Write(d); int undo = d.UndoCount;
            var ex = Assert.Throws<LayerLockedException>(() => d.BeginMaterialStroke(id, Material, new BrushSettings()));
            Assert.That(ex.Lock, Is.EqualTo(locks));
            Assert.That((d.HasActiveStroke, d.UndoCount), Is.EqualTo((false, undo)));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
        }

        [Test] public void EraseErasesEveryListedChannelLikeAnEraserOnEach()
        {
            BrushSettings B() => new BrushSettings { Radius = 8, Hardness = .3, Opacity = .8, Flow = .7, Seed = 6, Erase = true };
            var listed = Material.Where(m => m.Channel != PaintChannel.Metallic).ToArray();
            AssertSameAsOneChannelStrokes(B, null, s => Feed(s, Line), listed, "erase");
            var d = Document(out var id); var color = Pixels(d, id, PaintChannel.Color); var normal = Pixels(d, id, PaintChannel.Normal);
            using (var s = d.BeginMaterialStroke(id, listed, B())) { Feed(s, Line); s.Commit(); }
            int at = 30 * W + 33;
            Assert.That(color[at].A, Is.GreaterThan(0)); Assert.That(normal[at].A, Is.GreaterThan(0));
            Assert.That(Pixels(d, id, PaintChannel.Color)[at].A, Is.LessThan(color[at].A), "Color erased under the line");
            Assert.That(Pixels(d, id, PaintChannel.Normal)[at].A, Is.LessThan(normal[at].A), "Normal erased under the line");
            Assert.That(d.GetLayer(id).TryGetChannel(PaintChannel.Height, out var height) ? height.TileCount : 0, Is.Zero, "erasing an empty channel leaves it empty");
        }

        [Test] public void RefusalsChangeNothing()
        {
            var d = Document(out var id);
            var fill = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(1, 2, 3, 255) } });
            var adjust = d.AddAdjustmentLayer("adjust", AdjustmentSettings.Invert());
            var group = d.AddGroup("group");
            d.ClearHistory();
            var before = DocumentBinary.Write(d); long revision = d.Revision;
            var b = new BrushSettings();
            void Refused<T>(TestDelegate what, string name) where T : Exception
            {
                Assert.Throws<T>(what, name);
                Assert.That(d.HasActiveStroke, Is.False, name);
                Assert.That(DocumentBinary.Write(d), Is.EqualTo(before), name + ": nothing changed");
                Assert.That((d.UndoCount, d.Revision), Is.EqualTo((0, revision)), name);
            }
            Refused<InvalidOperationException>(() => d.BeginMaterialStroke(fill.Id, Material, b), "fill layer");
            Refused<InvalidOperationException>(() => d.BeginMaterialStroke(adjust.Id, Material, b), "adjustment layer");
            Refused<InvalidOperationException>(() => d.BeginMaterialStroke(group.Id, Material, b), "group");
            Refused<ArgumentException>(() => d.BeginMaterialStroke(id, new ChannelPaint[0], b), "no channel");
            Refused<ArgumentException>(() => d.BeginMaterialStroke(id, new[] { Material[1], Material[1] }, b), "a channel twice");
            Refused<ArgumentOutOfRangeException>(() => d.BeginMaterialStroke(id, new[] { new ChannelPaint((PaintChannel)42, default) }, b), "unknown channel");
            Refused<ArgumentOutOfRangeException>(() => d.BeginMaterialStroke(id, Material, new BrushSettings { Radius = -1 }), "invalid brush");
            Refused<ArgumentNullException>(() => d.BeginMaterialStroke(id, null, b), "null list");
            Refused<InvalidOperationException>(() => d.Batch(() => d.BeginMaterialStroke(id, Material, b)), "inside a batch");
            using (var s = d.BeginStroke(id, PaintChannel.Color, b))
                Assert.Throws<InvalidOperationException>(() => d.BeginMaterialStroke(id, Material, b), "another stroke is open");
        }

        [Test] public void APathLayerIsRefused()
        {
            var d = new PaintDocument(W, H, Tile);
            var layer = d.AddLayer("path");
            d.SetCanvasPath(layer.Id, new Core.Paths.CanvasPath(Guid.NewGuid(), PaintChannel.Color, new Core.Paths.PathBrush { RadiusWorld = 4, Hardness = 1, Spacing = .2, Color = new Rgba32(255, 0, 0, 255) },
                new[] { new Core.Paths.CanvasPoint(5, 5), new Core.Paths.CanvasPoint(40, 30) }));
            var before = DocumentBinary.Write(d);
            Assert.Throws<InvalidOperationException>(() => d.BeginMaterialStroke(layer.Id, Material, new BrushSettings()));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
        }
    }
}
