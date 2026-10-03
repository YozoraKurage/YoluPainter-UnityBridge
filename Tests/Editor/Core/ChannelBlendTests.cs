using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// レイヤーのチャンネルごとの合成モードと不透明度（Substance Painter の層のチャンネルごとの合成）: 設定があるチャンネルでは層の値を置き換え、
    /// ほかのチャンネルは層の値のまま。どのチャンネルの合成も、そのチャンネルでの値を層の値にした文書とバイトまで同じ（グループ・通過・
    /// クリッピング・調整・マスク・Normal のベクトル合成）。1 回の Undo（スライダーはまとめる）、すべてのロックで断る、型と値の拒否、変更の
    /// 記録はそのチャンネルだけ。結合・複製・大きさの変更で残る。保存（ネイティブ版 14）と、設定の無い文書が版 12 と同じ並びであること、
    /// 壊れた並びの拒否。PSD はチャンネルごとの実際のモードと不透明度で書く。
    /// </summary>
    public sealed class ChannelBlendTests
    {
        const int W = 40, H = 32, Tile = 16;
        internal static readonly PaintChannel[] Painted = { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Height, PaintChannel.Normal };
        static byte V(int i) => (byte)(((i % 16) + 16) % 16 * 17);
        static Guid Id(int n) => new Guid("4b1d0000-0000-4000-8000-" + n.ToString("D12"));

        static void Fill(PaintLayer l, Func<int, int, PaintChannel, Rgba32> pixel, params PaintChannel[] only)
        {
            foreach (var c in only.Length > 0 ? only : Painted)
            {
                var s = l.GetChannel(c);
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { var p = pixel(x, y, c); if (p.A != 0) s.SetPixel(x, y, p); }
            }
        }
        static Rgba32 Opaque(int x, int y, PaintChannel c) => new Rgba32(V(x / 3 + (int)c), V(y / 2), V((x + y) / 4), 255);
        static Rgba32 Partial(int x, int y, PaintChannel c) => (x + 2 * y) % 7 == 0 ? Rgba32.Transparent : new Rgba32(V(15 - x / 3), V(x + y + (int)c), V(y / 3), (byte)(x < 20 ? 220 : 130));
        static Rgba32 Spots(int x, int y, PaintChannel c) => (x / 5 + y / 5) % 2 == 0 ? new Rgba32(V(y + (int)c * 3), V(x), 200, 180) : Rgba32.Transparent;

        /// <summary>
        /// 下地・通過のグループ（Screen の a、b）・グループにクリッピングした層・レベル補正・マスク付きの上の層。with が真なら層ごとの値に
        /// チャンネルごとの設定を足す。flatten が与えられれば、チャンネルごとの設定を付けず、そのチャンネルでの値を層の値にする（比べる相手）。
        /// </summary>
        internal static PaintDocument Build(bool with, PaintChannel? flatten = null)
        {
            var d = new PaintDocument(W, H, Tile, 256L << 20, Id(1));
            Fill(d.AddLayer("back", Id(2)), Opaque);
            var a = d.AddLayer("a", Id(3)); Fill(a, Partial);
            var b = d.AddLayer("b", Id(4)); Fill(b, Spots);
            var g = d.GroupLayers(new[] { a.Id, b.Id }, "group", Id(5));
            var clip = d.AddLayer("clip", Id(6)); Fill(clip, (x, y, c) => Spots(y, x, c));
            var adjust = d.AddAdjustmentLayer("levels", AdjustmentSettings.Levels(.1, .9, 1.4, 0, 1), null, Id(7));
            var top = d.AddLayer("top", Id(8)); Fill(top, (x, y, c) => Partial(y, x, c));
            d.AddLayerMask(top.Id); for (int x = 0; x < W; x++) d.GetLayer(top.Id).Mask.Surface.SetPixel(x, x % H, new Rgba32(0, 0, 0, 200));
            d.SetLayerClipping(clip.Id, true);
            var plain = new Dictionary<Guid, (LayerBlendMode, double)>
            {
                { a.Id, (LayerBlendMode.Screen, .9) }, { b.Id, (LayerBlendMode.Normal, .8) }, { g.Id, (LayerBlendMode.PassThrough, 1) },
                { clip.Id, (LayerBlendMode.Normal, 1) }, { adjust.Id, (LayerBlendMode.Normal, .7) }, { top.Id, (LayerBlendMode.Normal, 1) },
            };
            var own = new Dictionary<Guid, Dictionary<PaintChannel, ChannelBlend>>
            {
                { g.Id, new Dictionary<PaintChannel, ChannelBlend> { { PaintChannel.Roughness, new ChannelBlend(LayerBlendMode.Screen, .7) }, { PaintChannel.Height, new ChannelBlend(null, .5) } } },
                { a.Id, new Dictionary<PaintChannel, ChannelBlend> { { PaintChannel.Color, new ChannelBlend(LayerBlendMode.Multiply, null) }, { PaintChannel.Normal, new ChannelBlend(LayerBlendMode.Overlay, null) } } },
                { clip.Id, new Dictionary<PaintChannel, ChannelBlend> { { PaintChannel.Roughness, new ChannelBlend(null, .4) } } },
                { adjust.Id, new Dictionary<PaintChannel, ChannelBlend> { { PaintChannel.Height, new ChannelBlend(LayerBlendMode.Overlay, .6) } } },
                { top.Id, new Dictionary<PaintChannel, ChannelBlend> { { PaintChannel.Normal, new ChannelBlend(LayerBlendMode.Normal, .5) }, { PaintChannel.Roughness, new ChannelBlend(LayerBlendMode.Darken, 1) } } },
            };
            foreach (var entry in plain)
            {
                var (mode, opacity) = entry.Value;
                if (flatten.HasValue && own.TryGetValue(entry.Key, out var o) && o.TryGetValue(flatten.Value, out var b2)) { mode = b2.Mode ?? mode; opacity = b2.Opacity ?? opacity; }
                d.SetLayerBlendMode(entry.Key, mode); d.SetLayerOpacity(entry.Key, opacity);
            }
            if (with && !flatten.HasValue) foreach (var entry in own) foreach (var c in entry.Value) d.SetChannelBlend(entry.Key, c.Key, c.Value);
            d.ClearHistory(); return d;
        }

        [Test] public void EachChannelCompositesWithItsOwnSettingAndTheOthersWithTheLayers()
        {
            var with = Build(true); var without = Build(false);
            foreach (var c in Painted)
            {
                var expected = Build(false, c).Composite(c);
                Assert.That(with.Composite(c), Is.EqualTo(expected), c + ": the same bytes as the layers set to this channel's values");
                for (int y = 0; y < H; y += 3) for (int x = 0; x < W; x += 5)
                {
                    int i = (y * W + x) * 4;
                    Assert.That(with.CompositePixel(c, x, y), Is.EqualTo(new Rgba32(expected[i], expected[i + 1], expected[i + 2], expected[i + 3])), c + ": the per-pixel reference agrees at " + x + "," + y);
                }
            }
            Assert.That(with.Composite(PaintChannel.Metallic), Is.EqualTo(without.Composite(PaintChannel.Metallic)));
            Assert.That(with.Composite(PaintChannel.Roughness), Is.Not.EqualTo(without.Composite(PaintChannel.Roughness)), "the settings do change the channel");
            // 表示用の経路（バックドロップから・縮小）も同じ式を通る
            var jobs = new[] { new CpuCompositor.CompositeJob(0, 0, W, H, new byte[W * H * 4]) };
            CpuCompositor.CompositeRegions(with, PaintChannel.Roughness, jobs);
            Assert.That(jobs[0].Pixels, Is.EqualTo(with.Composite(PaintChannel.Roughness)));
        }

        [Test] public void InTheNormalChannelOverlayAddsDetailWhileColorStaysNormal()
        {
            // よくある使い方: 色は普通に重ね、Normal は下の法線に細部として足す（RNM）
            var d = new PaintDocument(16, 16, 16); var back = d.AddLayer("back"); var top = d.AddLayer("top");
            foreach (var l in new[] { back, top }) foreach (var c in new[] { PaintChannel.Color, PaintChannel.Normal }) l.GetChannel(c);
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
            {
                back.GetChannel(PaintChannel.Normal).SetPixel(x, y, NormalMaps.Encode(.4, 0, 1, 255)); back.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32(10, 20, 30, 255));
                top.GetChannel(PaintChannel.Normal).SetPixel(x, y, NormalMaps.Encode(0, .3, 1, 255)); top.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32(200, 100, 50, 255));
            }
            d.SetChannelBlendMode(top.Id, PaintChannel.Normal, LayerBlendMode.Overlay);
            Assert.That(d.CompositePixel(PaintChannel.Color, 3, 3), Is.EqualTo(new Rgba32(200, 100, 50, 255)), "Color: Normal mode replaces");
            Assert.That(d.CompositePixel(PaintChannel.Normal, 3, 3), Is.EqualTo(NormalMaps.Blend(NormalMaps.Encode(.4, 0, 1, 255), NormalMaps.Encode(0, .3, 1, 255), 1, LayerBlendMode.Overlay)), "Normal: RNM detail");
            Assert.That(d.CompositePixel(PaintChannel.Normal, 3, 3), Is.Not.EqualTo(NormalMaps.Encode(0, .3, 1, 255)));
        }

        [Test] public void ChangesAreOneUndoStepSliderDragsMergeAndClearingFollowsTheLayer()
        {
            var d = Build(false); var top = Id(8);
            d.SetChannelBlendMode(top, PaintChannel.Roughness, LayerBlendMode.Multiply);
            Assert.That(d.UndoCount, Is.EqualTo(1));
            Assert.That(d.GetLayer(top).BlendModeIn(PaintChannel.Roughness), Is.EqualTo(LayerBlendMode.Multiply));
            Assert.That(d.GetLayer(top).OpacityIn(PaintChannel.Roughness), Is.EqualTo(1), "the opacity still follows the layer");
            Assert.That(d.GetLayer(top).BlendMode, Is.EqualTo(LayerBlendMode.Normal), "the layer's own mode is unchanged");
            foreach (var o in new[] { .9, .7, .5 }) d.SetChannelOpacity(top, PaintChannel.Roughness, o, coalesce: true);
            Assert.That(d.UndoCount, Is.EqualTo(2), "one drag, one step");
            d.EndCoalescing();
            d.SetLayerOpacity(top, .3);
            Assert.That(d.GetLayer(top).OpacityIn(PaintChannel.Roughness), Is.EqualTo(.5), "the channel's own opacity replaces the layer's");
            Assert.That(d.GetLayer(top).OpacityIn(PaintChannel.Color), Is.EqualTo(.3));
            var set = d.Composite(PaintChannel.Roughness);
            d.SetChannelBlend(top, PaintChannel.Roughness, default);
            Assert.That(d.GetLayer(top).HasChannelBlends, Is.False, "an empty setting follows the layer again");
            d.Undo(); Assert.That(d.Composite(PaintChannel.Roughness), Is.EqualTo(set));
            d.Undo(); d.Undo(); Assert.That(d.GetLayer(top).ChannelBlendOf(PaintChannel.Roughness), Is.EqualTo(new ChannelBlend(LayerBlendMode.Multiply, null)), "the drag undoes to before its first move");
            d.Undo(); Assert.That(d.GetLayer(top).HasChannelBlends, Is.False);
            Assert.That(d.Composite(PaintChannel.Roughness), Is.EqualTo(Build(false).Composite(PaintChannel.Roughness)));
            d.Redo(); d.Redo(); Assert.That(d.GetLayer(top).ChannelBlendOf(PaintChannel.Roughness), Is.EqualTo(new ChannelBlend(LayerBlendMode.Multiply, .5)));
            int steps = d.UndoCount; d.SetChannelOpacity(top, PaintChannel.Roughness, .5);
            Assert.That(d.UndoCount, Is.EqualTo(steps), "the same value records nothing");
            d.SetChannelOpacity(top, PaintChannel.Roughness, null); d.SetChannelBlendMode(top, PaintChannel.Roughness, null);
            Assert.That(d.GetLayer(top).ChannelBlends, Is.Empty);
        }

        [Test] public void WrongValuesAndLockAllAreRefusedWithNothingChanged()
        {
            var d = Build(false); var top = Id(8); var group = Id(5);
            var before = DocumentBinary.Write(d); int undo = d.UndoCount;
            void Refused<T>(TestDelegate edit, string what) where T : Exception
            {
                Assert.Throws<T>(edit, what);
                Assert.That(DocumentBinary.Write(d), Is.EqualTo(before), what); Assert.That(d.UndoCount, Is.EqualTo(undo), what);
            }
            Refused<ArgumentException>(() => d.SetChannelBlendMode(top, PaintChannel.Color, LayerBlendMode.PassThrough), "pass through on a layer");
            Refused<ArgumentOutOfRangeException>(() => d.SetChannelBlendMode(top, PaintChannel.Color, (LayerBlendMode)99), "unknown mode");
            Refused<ArgumentOutOfRangeException>(() => d.SetChannelOpacity(top, PaintChannel.Color, 1.5), "opacity above 1");
            Refused<ArgumentOutOfRangeException>(() => d.SetChannelOpacity(top, PaintChannel.Color, -.1), "negative opacity");
            Refused<ArgumentOutOfRangeException>(() => d.SetChannelOpacity(top, PaintChannel.Color, double.NaN), "NaN");
            Refused<ArgumentOutOfRangeException>(() => d.SetChannelOpacity(top, (PaintChannel)42, .5), "unknown channel");
            Refused<KeyNotFoundException>(() => d.SetChannelOpacity(Guid.NewGuid(), PaintChannel.Color, .5), "unknown layer");
            d.SetLayerLocks(top, LayerLocks.All); before = DocumentBinary.Write(d); undo = d.UndoCount;
            Refused<LayerLockedException>(() => d.SetChannelOpacity(top, PaintChannel.Color, .5), "Lock All");
            d.SetLayerLocks(top, LayerLocks.Pixels | LayerLocks.Transparency | LayerLocks.Position);
            Assert.DoesNotThrow(() => d.SetChannelOpacity(top, PaintChannel.Color, .5), "the other locks keep attributes editable, as the layer's opacity");
            Assert.DoesNotThrow(() => d.SetChannelBlendMode(group, PaintChannel.Color, LayerBlendMode.Normal), "a group can be isolated in one channel");
            Assert.That(d.GetLayer(group).BlendModeIn(PaintChannel.Color), Is.EqualTo(LayerBlendMode.Normal));
            Assert.DoesNotThrow(() => d.SetChannelBlendMode(group, PaintChannel.Color, LayerBlendMode.PassThrough));
        }

        [Test] public void OnlyThatChannelsTilesAreReportedChanged()
        {
            var d = Build(false); long since = d.ChangeSerial;
            d.SetChannelOpacity(Id(3), PaintChannel.Roughness, .2);
            var rough = new HashSet<TileCoord>(); Assert.That(d.TryGetChangedTiles(PaintChannel.Roughness, since, rough), Is.True);
            Assert.That(rough, Is.Not.Empty);
            foreach (var c in new[] { PaintChannel.Color, PaintChannel.Height, PaintChannel.Normal })
            {
                var other = new HashSet<TileCoord>(); d.TryGetChangedTiles(c, since, other);
                Assert.That(other.Except(ClippedTiles(d, c)), Is.Empty, c + ": only clipped layers are re-marked in the other channels (as for any layer setting)");
            }
        }
        static IEnumerable<TileCoord> ClippedTiles(PaintDocument d, PaintChannel c) => d.Layers.Where(l => l.Clipping).SelectMany(l => l.EnumerateContentTiles(c));

        [Test] public void MergesDuplicatesAndResizingKeepTheSettings()
        {
            // 下へ結合: 結果は下の層のチャンネルごとの設定を持ち、見た目は変わらない（変われば LayerMergeException）。上の層は下の層が
            // Multiply の Roughness に画素を持たない（持てば Photoshop の下へ結合と同じく見た目が変わり、断られる）
            var d = new PaintDocument(W, H, Tile);
            var back = d.AddLayer("back"); Fill(back, Opaque);
            var lower = d.AddLayer("lower"); Fill(lower, Partial); var upper = d.AddLayer("upper"); Fill(upper, Spots, PaintChannel.Color, PaintChannel.Height);
            d.SetChannelBlend(lower.Id, PaintChannel.Roughness, new ChannelBlend(LayerBlendMode.Multiply, .6));
            var copy = d.GetLayer(d.DuplicateLayer(lower.Id).Id);
            Assert.That(copy.ChannelBlends, Is.EquivalentTo(lower.ChannelBlends), "a duplicate copies them");
            d.RemoveLayer(copy.Id);
            var expected = Painted.ToDictionary(c => c, c => d.Composite(c));
            var report = d.MergeDown(upper.Id);
            var result = d.GetLayer(report.ResultId);
            Assert.That(result.ChannelBlends, Is.EquivalentTo(new Dictionary<PaintChannel, ChannelBlend> { { PaintChannel.Roughness, new ChannelBlend(LayerBlendMode.Multiply, .6) } }));
            foreach (var c in Painted) Assert.That(d.Composite(c).Zip(expected[c], (a, b) => Math.Abs(a - b)).Max(), Is.LessThanOrEqualTo(LayerMergeReport_Tolerance), c.ToString());
            d.Undo();
            // グループを結合: 分離のグループの設定を持つ（通過は Normal に）
            var g = d.GroupLayers(new[] { lower.Id, upper.Id }, "g");
            d.SetLayerBlendMode(g.Id, LayerBlendMode.Normal); d.SetChannelBlend(g.Id, PaintChannel.Normal, new ChannelBlend(LayerBlendMode.Overlay, .9)); d.SetChannelBlend(g.Id, PaintChannel.Color, new ChannelBlend(LayerBlendMode.PassThrough, null));
            var merged = d.GetLayer(d.MergeGroup(g.Id, tolerance: 255).ResultId);
            Assert.That(merged.ChannelBlendOf(PaintChannel.Normal), Is.EqualTo(new ChannelBlend(LayerBlendMode.Overlay, .9)));
            Assert.That(merged.ChannelBlendOf(PaintChannel.Color), Is.EqualTo(new ChannelBlend(LayerBlendMode.Normal, null)), "pass through becomes Normal, as for the group's own mode");
            d.Undo(); d.Undo();
            // 大きさの変更
            var resized = d.Resampled(W * 2, H * 2, CanvasResampling.Nearest).Document;
            Assert.That(resized.GetLayer(lower.Id).ChannelBlends, Is.EquivalentTo(lower.ChannelBlends));
            Assert.That(resized.GetLayer(upper.Id).ChannelBlends, Is.EquivalentTo(upper.ChannelBlends));
        }
        const int LayerMergeReport_Tolerance = PaintDocument.MergeRoundingTolerance;

        // ───────────── 保存 ─────────────

        [Test] public void Version14SavesTheSettingsAndADocumentWithoutThemIsLaidOutAsVersion13()
        {
            var d = Build(true);
            var bytes = DocumentBinary.Write(d);
            Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(DocumentBinary.CurrentVersion));
            var read = DocumentBinary.Read(bytes);
            Assert.That(read.CanUndo, Is.False);
            foreach (var l in d.Layers) Assert.That(read.GetLayer(l.Id).ChannelBlends, Is.EquivalentTo(l.ChannelBlends), l.Name);
            foreach (var c in Painted) Assert.That(read.Composite(c), Is.EqualTo(d.Composite(c)), c.ToString());
            Assert.That(DocumentBinary.Write(read), Is.EqualTo(bytes), "byte for byte");
            // 設定の無い文書は版 13 と同じ並び（版の数だけが違う）。形のグラデーションも無ければ版 12 とも同じ。どちらとしても読める
            var plain = DocumentBinary.Write(Build(false));
            foreach (int older in new[] { 13, 12 })
            {
                var asOlder = (byte[])plain.Clone(); BitConverter.GetBytes(older).CopyTo(asOlder, 8);
                Assert.That(DocumentBinary.Write(DocumentBinary.Read(asOlder)), Is.EqualTo(plain), "read as version " + older);
            }
            Assert.That(plain.Length, Is.EqualTo(bytes.Length - Build(true).Layers.Sum(l => l.ChannelBlends.Count == 0 ? 0 : 1 + l.ChannelBlends.Values.Sum(b => 5 + (b.Mode.HasValue ? 4 : 0) + (b.Opacity.HasValue ? 8 : 0)))),
                "a count byte per layer with settings, and per setting a channel, a parts byte and the parts");
            // 版 12・13 の正本に設定の印は無い
            foreach (int older in new[] { 13, 12 })
            {
                var old = (byte[])bytes.Clone(); BitConverter.GetBytes(older).CopyTo(old, 8);
                Assert.That(() => DocumentBinary.Read(old), Throws.InstanceOf<InvalidDataException>(), "version " + older);
            }
        }

        /// <summary>1 層の文書（層 "L"、ロック無し）の属性の 1 バイトの位置と、その後に続く設定の並び。</summary>
        static int AttributeByte() => 8 + 4 + 16 + 12 + ArchiveTestUtil.NormalSettingsBytes + 4 + 16 + 4 + 1 + 1 + 8 + 4;

        [Test] public void BrokenSettingsAreRefusedInsteadOfDropped()
        {
            var d = new PaintDocument(16, 16, 8); var l = d.AddLayer("L");
            d.SetChannelBlend(l.Id, PaintChannel.Roughness, new ChannelBlend(LayerBlendMode.Multiply, .25));
            var bytes = DocumentBinary.Write(d); int at = AttributeByte();
            Assert.That(bytes[at], Is.EqualTo(4), "attribute bit 2");
            Assert.That(bytes[at + 1], Is.EqualTo(1), "one setting");
            Assert.That(BitConverter.ToInt32(bytes, at + 2), Is.EqualTo((int)PaintChannel.Roughness));
            Assert.That(bytes[at + 6], Is.EqualTo(3), "mode and opacity");
            Assert.That(BitConverter.ToInt32(bytes, at + 7), Is.EqualTo((int)LayerBlendMode.Multiply));
            Assert.That(BitConverter.ToDouble(bytes, at + 11), Is.EqualTo(.25));
            byte[] With(Action<byte[]> change) { var b = (byte[])bytes.Clone(); change(b); return b; }
            void Refused(byte[] b, string what, string message = null)
            {
                var c = Throws.InstanceOf<InvalidDataException>();
                Assert.That(() => DocumentBinary.Read(b), message == null ? c : c.With.Message.Contains(message), what);
            }
            Refused(With(b => b[at + 1] = 0), "an empty list is never written", "count");
            Refused(With(b => b[at + 1] = 7), "more entries than channels", "count");
            Refused(With(b => BitConverter.GetBytes(42).CopyTo(b, at + 2)), "unknown channel", "channel");
            Refused(With(b => b[at + 6] = 0), "no parts", "parts");
            Refused(With(b => b[at + 6] = 7), "unknown part bit", "parts");
            Refused(With(b => BitConverter.GetBytes(99).CopyTo(b, at + 7)), "unknown mode", "blend mode");
            Refused(With(b => BitConverter.GetBytes((int)LayerBlendMode.PassThrough).CopyTo(b, at + 7)), "pass through on a paint layer", "groups only");
            Refused(With(b => BitConverter.GetBytes(1.5).CopyTo(b, at + 11)), "opacity above 1", "opacity");
            Refused(With(b => BitConverter.GetBytes(double.NaN).CopyTo(b, at + 11)), "NaN opacity", "opacity");
            Refused(bytes.Take(at + 9).ToArray(), "truncated");
            // 同じチャンネルが 2 回
            d.SetChannelBlend(l.Id, PaintChannel.Height, new ChannelBlend(null, .5));
            bytes = DocumentBinary.Write(d);
            Assert.That(bytes[at + 1], Is.EqualTo(2));
            int second = at + 2 + 4 + 1 + 4 + 8;
            Assert.That(BitConverter.ToInt32(bytes, second), Is.EqualTo((int)PaintChannel.Height));
            Refused(With(b => BitConverter.GetBytes((int)PaintChannel.Roughness).CopyTo(b, second)), "a channel twice", "duplicate");
        }

        // ───────────── PSD ─────────────

        [Test] public void PsdIsWrittenWithEachChannelsOwnModeAndOpacity()
        {
            var d = new PaintDocument(W, H, Tile);
            var back = d.AddLayer("back"); Fill(back, Opaque);
            var top = d.AddLayer("top"); Fill(top, Partial);
            d.SetLayerOpacity(top.Id, .9);
            d.SetChannelBlend(top.Id, PaintChannel.Roughness, new ChannelBlend(LayerBlendMode.Multiply, .5));
            d.SetChannelBlend(top.Id, PaintChannel.Height, new ChannelBlend(LayerBlendMode.Screen, null));
            foreach (var (c, mode, opacity) in new[] { (PaintChannel.Color, LayerBlendMode.Normal, 230), (PaintChannel.Roughness, LayerBlendMode.Multiply, 128), (PaintChannel.Height, LayerBlendMode.Screen, 230) })
            {
                var dto = PsdBridge.Export(d, c);
                var layer = dto.Layers[0];
                Assert.That((layer.Name, layer.BlendMode, (int)layer.Opacity), Is.EqualTo(("top", mode, opacity)), c.ToString());
                var bytes = PsdCodec.Write(dto);
                var read = PsdCodec.Read(bytes);
                Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), c.ToString());
                Assert.That(read.Diagnostics, Is.Empty, c + ": the layers recomposite to the stored merged image");
                // 取り込みは今までどおり 1 チャンネル（Color）に層の値で入る。そのチャンネルの合成が元のチャンネルの合成と同じ
                var imported = PsdBridge.Import(read);
                Assert.That(imported.GetLayer(imported.Layers[1].Id).HasChannelBlends, Is.False);
                Assert.That(imported.Composite(PaintChannel.Color), Is.EqualTo(Rounded(d, c)), c.ToString());
            }
        }
        /// <summary>The channel's composite with opacities rounded to 1/255 (what PSD stores).</summary>
        static byte[] Rounded(PaintDocument d, PaintChannel c)
        {
            var copy = DocumentBinary.Read(DocumentBinary.Write(d));
            foreach (var l in copy.Layers)
            {
                double o = Math.Round(l.OpacityIn(c) * 255) / 255;
                copy.SetLayerBlendMode(l.Id, l.BlendModeIn(c)); copy.SetLayerOpacity(l.Id, o);
                copy.SetChannelBlend(l.Id, c, default);
            }
            return copy.Composite(c);
        }
    }
}
