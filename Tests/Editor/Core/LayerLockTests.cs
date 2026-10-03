using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// レイヤーのロック（Photoshop と同じ 4 つ）: 透明部分のロックでは、ブラシ（2D と 3D のダブ）・バケツ・グラデーション・画像の置き換え・
    /// フィルターの焼き込みで各画素のアルファが変わらず色だけが変わり（source-atop）、透明な画素は RGB まで変わらない。消しゴム・カット・
    /// パス・選んだ所だけの移動は断る。画像のロックは画素を変える操作を全部断る（層ごとの整数の移動とマスクは許す）。位置のロックは移動と
    /// 変形を断る。すべてのロックは層の設定も断る（名前・表示・並び・複製・削除は許す）。グループのロックは中身に効く。断ったときは
    /// 何も変えない。ロックの変更は 1 回の Undo。保存（ネイティブ版 12）と、ロックの無い文書が版 11 と同じ並びであること、知らない印の拒否。
    /// </summary>
    public sealed class LayerLockTests
    {
        const int W = 40, H = 24, Tile = 16;
        static PaintDocument NewDocument() => new PaintDocument(W, H, Tile, 256L << 20) { ActiveStrokeBudgetBytes = 64L << 20 };

        /// <summary>アルファがまちまちの層: 左から 10 列ずつ alpha 0（RGB 10,20,30 を隠し持つ）・64・128・255、色は列ごとに違う。</summary>
        static PaintLayer Varied(PaintDocument d, string name = "varied")
        {
            var l = d.AddLayer(name); var s = l.GetChannel(PaintChannel.Color);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                int band = x / 10;
                s.SetPixel(x, y, band == 0 ? new Rgba32(10, 20, 30, 0) : new Rgba32((byte)(100 + x), (byte)(50 + y), 200, (byte)(band == 1 ? 64 : band == 2 ? 128 : 255)));
            }
            d.ClearHistory(); return l;
        }
        static Rgba32[] Pixels(PaintLayer l, PaintChannel c = PaintChannel.Color)
        { var p = new Rgba32[W * H]; for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) p[y * W + x] = l.GetPixel(c, x, y); return p; }
        static byte Mix(byte from, byte to, double t) { double v = (from / 255.0 + (to / 255.0 - from / 255.0) * t) * 255 + .5; return v >= 255 ? (byte)255 : v > 0 ? (byte)(int)v : (byte)0; }
        static BrushSettings Hard(Rgba32 color, double opacity = 1, bool erase = false) => new BrushSettings { Radius = 80, Hardness = 1, Color = color, Opacity = opacity, Flow = 1, PressureOpacity = false, PressureSize = false, Erase = erase };
        static void Dab(PaintDocument d, PaintLayer l, BrushSettings b) { using (var s = d.BeginStroke(l.Id, PaintChannel.Color, b)) { s.Add(new BrushSample(W / 2.0, H / 2.0)); s.Commit(); } }

        /// <summary>各画素: アルファは前と同じ、透明な画素は前とそっくり同じ、ほかは色が expected。</summary>
        static void AssertAlphaKept(Rgba32[] before, Rgba32[] after, Func<Rgba32, int, Rgba32> expected, string what)
        {
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i].A == 0) { Assert.That(after[i], Is.EqualTo(before[i]), what + ": a transparent pixel keeps its RGB at " + i); continue; }
                Assert.That(after[i].A, Is.EqualTo(before[i].A), what + ": alpha at " + i);
                var e = expected(before[i], i);
                Assert.That(after[i], Is.EqualTo(new Rgba32(e.R, e.G, e.B, before[i].A)), what + ": colour at " + i);
            }
        }
        static void AssertRefused(PaintDocument d, LayerLocks expected, TestDelegate edit, string what)
        {
            var composite = d.Composite(PaintChannel.Color); int undo = d.UndoCount; long revision = d.Revision;
            var ex = Assert.Throws<LayerLockedException>(edit, what);
            Assert.That(ex.Lock, Is.EqualTo(expected), what);
            Assert.That(ex.Reason, Is.EqualTo(LayerOpRefusal.Locked));
            Assert.That(d.HasActiveStroke, Is.False, what + ": no stroke left open");
            Assert.That((d.UndoCount, d.Revision), Is.EqualTo((undo, revision)), what + ": nothing recorded");
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(composite), what + ": nothing changed");
        }

        // ───────── 透明部分のロック ─────────

        [Test] public void TransparencyLockKeepsEveryAlphaWhileBrushFillGradientAndReplaceChangeOnlyColour()
        {
            var d = NewDocument(); var l = Varied(d);
            d.SetLayerLocks(l.Id, LayerLocks.Transparency); d.ClearHistory();
            var before = Pixels(l);

            Dab(d, l, Hard(new Rgba32(250, 0, 0)));
            AssertAlphaKept(before, Pixels(l), (p, i) => new Rgba32(250, 0, 0), "brush at full opacity");
            d.Undo(); Assert.That(Pixels(l), Is.EqualTo(before));

            Dab(d, l, Hard(new Rgba32(0, 0, 0), .5));
            AssertAlphaKept(before, Pixels(l), (p, i) => new Rgba32(Mix(p.R, 0, .5), Mix(p.G, 0, .5), Mix(p.B, 0, .5)), "brush at half opacity (source-atop)");
            d.Undo();

            // 塗りの色のアルファも寄せる量に入る
            Dab(d, l, Hard(new Rgba32(0, 255, 0, 128)));
            AssertAlphaKept(before, Pixels(l), (p, i) => new Rgba32(Mix(p.R, 0, 128 / 255.0), Mix(p.G, 255, 128 / 255.0), Mix(p.B, 0, 128 / 255.0)), "brush colour with alpha");
            d.Undo();

            Assert.That(d.Fill(l.Id, PaintChannel.Color, new Rgba32(0, 0, 255), .25), Is.True);
            AssertAlphaKept(before, Pixels(l), (p, i) => new Rgba32(Mix(p.R, 0, .25), Mix(p.G, 0, .25), Mix(p.B, 255, .25)), "bucket");
            d.Undo();

            var g = new GradientSettings { X0 = 0, Y0 = 0, X1 = W, Y1 = 0, From = new Rgba32(255, 255, 255), To = new Rgba32(0, 0, 0) };
            Assert.That(d.Gradient(l.Id, PaintChannel.Color, g), Is.True);
            // 不透明なグラデーションは、ロックの無い層ではその色そのものになる。その色が、アルファはそのままで入る
            var reference = NewDocument(); var r = reference.AddLayer("r"); reference.Gradient(r.Id, PaintChannel.Color, g);
            AssertAlphaKept(before, Pixels(l), (p, i) => r.GetPixel(PaintChannel.Color, i % W, i / W), "gradient");
            d.Undo();

            // 画像の置き換え（プラグインの ReplaceLayerPixels）: 画像の色、層のアルファ。画像の透明な画素は層をそのまま
            var image = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++) { image[i * 4] = 7; image[i * 4 + 1] = 8; image[i * 4 + 2] = 9; image[i * 4 + 3] = (byte)(i % 3 == 0 ? 0 : 40); }
            Assert.That(d.ReplacePixels(l.Id, PaintChannel.Color, image), Is.True);
            AssertAlphaKept(before, Pixels(l), (p, i) => i % 3 == 0 ? p : new Rgba32(7, 8, 9), "replaced image");
            d.Undo();
            Assert.That(Pixels(l), Is.EqualTo(before));
        }

        [Test] public void TransparencyLockAppliesToMeshDabsAndAPartialSelection()
        {
            var d = NewDocument(); var l = Varied(d);
            d.SetLayerLocks(l.Id, LayerLocks.Transparency); var before = Pixels(l);
            // 3D ビューのブラシは面のダブの覆いを ApplyPixel で入れる
            using (var s = d.BeginStroke(l.Id, PaintChannel.Color, Hard(new Rgba32(255, 255, 0))))
            {
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) s.ApplyPixel(x, y, .5);
                s.Commit();
            }
            AssertAlphaKept(before, Pixels(l), (p, i) => new Rgba32(Mix(p.R, 255, .5), Mix(p.G, 255, .5), Mix(p.B, 0, .5)), "mesh dabs at half coverage");
            d.Undo();
            // 選択範囲の割合も寄せる量に入る（丸めは 1 回）: 縁がぼける楕円の選択
            var ellipse = SelectionMask.Ellipse(d, 20, 12, 14.3, 9.7);
            Assert.That(Enumerable.Range(0, W * H).Count(i => ellipse[i % W, i / W] > 0 && ellipse[i % W, i / W] < 255), Is.GreaterThan(10), "the test needs partly selected pixels");
            d.SetSelection(ellipse);
            Dab(d, l, Hard(new Rgba32(0, 0, 0)));
            AssertAlphaKept(before, Pixels(l), (p, i) => { double t = ellipse[i % W, i / W] / 255.0; return new Rgba32(Mix(p.R, 0, t), Mix(p.G, 0, t), Mix(p.B, 0, t)); }, "partly selected brush");
        }

        [Test] public void TransparencyLockRefusesErasingCuttingPathsAndPartialMovesButMovesTheWholeLayer()
        {
            var d = NewDocument(); var l = Varied(d);
            d.SetLayerLocks(l.Id, LayerLocks.Transparency);
            AssertRefused(d, LayerLocks.Transparency, () => d.BeginStroke(l.Id, PaintChannel.Color, Hard(new Rgba32(0, 0, 0), erase: true)), "erase stroke");
            AssertRefused(d, LayerLocks.Transparency, () => d.Fill(l.Id, PaintChannel.Color, new Rgba32(0, 0, 0), 1, null, erase: true), "erase fill");
            AssertRefused(d, LayerLocks.Transparency, () => d.CutPixels(l.Id, PaintChannel.Color), "cut");
            AssertRefused(d, LayerLocks.Transparency, () => d.EnsurePixelsEditable(l.Id, erase: true), "the window's check before an erase");
            var path = new CanvasPath(Guid.NewGuid(), PaintChannel.Color, new PathBrush { RadiusWorld = 2, Hardness = 1, Spacing = .1, Color = new Rgba32(1, 2, 3) }, new[] { new CanvasPoint(2, 2), new CanvasPoint(30, 20) });
            AssertRefused(d, LayerLocks.Transparency, () => d.SetCanvasPath(l.Id, path), "a path redraws the layer");
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 20, 10));
            AssertRefused(d, LayerLocks.Transparency, () => d.Transform(l.Id, Affine2D.Translation(3, 0)), "moving a selected part");
            d.ClearSelection();
            Assert.That(() => d.EnsurePixelsEditable(l.Id), Throws.Nothing, "painting is allowed");
            // 層ごとの移動・変形は位置の操作なので許す（Photoshop と同じ）
            var before = l.GetPixel(PaintChannel.Color, 25, 5);
            Assert.That(d.Transform(l.Id, Affine2D.Translation(3, 2)), Is.True);
            Assert.That(l.GetPixel(PaintChannel.Color, 28, 7), Is.EqualTo(before));
            Assert.That(d.Transform(l.Id, Affine2D.FromParts(20, 12, 0, 0, 30, 1, 1)), Is.True, "a whole-layer rotation too");
        }

        // ───────── 画像のロック ─────────

        [Test] public void PixelLockRefusesEveryPixelEditAndChangesNothing()
        {
            var d = NewDocument(); var below = Varied(d, "below"); var l = Varied(d);
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Invert()); // 非破壊の効果は画像のロックでも足せる（下で確かめる）
            var fill = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(9, 9, 9, 255) } }, above: below.Id);
            d.SetLayerVisibility(fill.Id, false); d.MoveLayer(fill.Id, 0); // 一番下へ（l の下は below のまま）
            d.SetLayerLocks(l.Id, LayerLocks.Pixels); d.SetLayerLocks(fill.Id, LayerLocks.Pixels);
            AssertRefused(d, LayerLocks.Pixels, () => d.BeginStroke(l.Id, PaintChannel.Color, Hard(new Rgba32(1, 1, 1))), "stroke");
            AssertRefused(d, LayerLocks.Pixels, () => d.BeginStroke(l.Id, PaintChannel.Roughness, Hard(new Rgba32(1, 1, 1))), "a stroke into another channel");
            AssertRefused(d, LayerLocks.Pixels, () => d.Fill(l.Id, PaintChannel.Color, new Rgba32(1, 1, 1)), "bucket");
            AssertRefused(d, LayerLocks.Pixels, () => d.Gradient(l.Id, PaintChannel.Color, new GradientSettings { X1 = 5 }), "gradient");
            AssertRefused(d, LayerLocks.Pixels, () => d.ReplacePixels(l.Id, PaintChannel.Color, new byte[W * H * 4]), "plug-in image");
            AssertRefused(d, LayerLocks.Pixels, () => d.CutPixels(l.Id, PaintChannel.Color), "cut");
            AssertRefused(d, LayerLocks.Pixels, () => d.BakeFilters(l.Id), "baking filters");
            AssertRefused(d, LayerLocks.Pixels, () => d.Transform(l.Id, Affine2D.FromParts(20, 12, 0, 0, 0, 2, 2)), "scaling");
            AssertRefused(d, LayerLocks.Pixels, () => d.Transform(l.Id, Affine2D.Translation(.5, 0)), "a move by half a pixel resamples");
            AssertRefused(d, LayerLocks.Pixels, () => d.MergeDown(l.Id), "merging it down");
            AssertRefused(d, LayerLocks.Pixels, () => d.SetFillValue(fill.Id, PaintChannel.Color, new Rgba32(1, 2, 3, 255)), "a fill layer's value");
            var path = new CanvasPath(Guid.NewGuid(), PaintChannel.Color, new PathBrush { RadiusWorld = 2, Hardness = 1, Spacing = .1 }, new[] { new CanvasPoint(2, 2) });
            AssertRefused(d, LayerLocks.Pixels, () => d.SetCanvasPath(l.Id, path), "path");
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 20, 10));
            AssertRefused(d, LayerLocks.Pixels, () => d.Transform(l.Id, Affine2D.Translation(1, 0)), "moving a selected part leaves a hole");
            d.ClearSelection();
            // 許すもの: 層ごとの整数の移動（画素はそのまま写る）、マスク、非破壊の効果、設定、名前、表示、複製、削除
            var before = Pixels(l);
            Assert.That(d.Transform(l.Id, Affine2D.Translation(2, 1)), Is.True);
            Assert.That(l.GetPixel(PaintChannel.Color, 32, 6), Is.EqualTo(before[5 * W + 30]));
            d.AddLayerMask(l.Id);
            using (var s = d.BeginMaskStroke(l.Id, Hard(new Rgba32(0, 0, 0)))) { s.Add(new BrushSample(5, 5)); s.Commit(); }
            Assert.That(l.Mask.Surface.TileCount, Is.GreaterThan(0), "the mask can be painted (Photoshop)");
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(1));
            d.SetLayerOpacity(l.Id, .5); d.SetLayerName(l.Id, "renamed"); d.SetLayerVisibility(l.Id, false); d.SetLayerVisibility(l.Id, true);
            var copy = d.DuplicateLayer(l.Id);
            Assert.That(copy.Locks, Is.EqualTo(LayerLocks.Pixels), "a copy keeps the locks");
            d.RemoveLayer(copy.Id);
        }

        [Test] public void PositionLockRefusesMovesAndTransformsButNotPainting()
        {
            var d = NewDocument(); var l = Varied(d); var other = Varied(d, "other");
            d.SetLayerLocks(l.Id, LayerLocks.Position);
            AssertRefused(d, LayerLocks.Position, () => d.Transform(l.Id, Affine2D.Translation(1, 0)), "move");
            AssertRefused(d, LayerLocks.Position, () => d.Transform(l.Id, Affine2D.FromParts(20, 12, 0, 0, 90, 1, 1)), "rotate");
            AssertRefused(d, LayerLocks.Position, () => d.TransformLayers(new[] { other.Id, l.Id }, Affine2D.Translation(1, 0)), "moving it with another layer: neither moves");
            Dab(d, l, Hard(new Rgba32(1, 2, 3)));
            Assert.That(l.GetPixel(PaintChannel.Color, 20, 12), Is.EqualTo(new Rgba32(1, 2, 3, 255)), "painting is allowed");
        }

        // ───────── すべてのロック・グループ ─────────

        [Test] public void LockAllRefusesTheLayersSettingsButKeepsNameVisibilityOrderCopiesAndDeleting()
        {
            var d = NewDocument(); var a = Varied(d, "a"); var l = Varied(d);
            d.AddLayerMask(l.Id); var blur = d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(1));
            var adjustment = d.AddAdjustmentLayer("adj", AdjustmentSettings.Invert());
            d.SetLayerLocks(l.Id, LayerLocks.All); d.SetLayerLocks(adjustment.Id, LayerLocks.All);
            AssertRefused(d, LayerLocks.All, () => d.SetLayerOpacity(l.Id, .5), "opacity");
            AssertRefused(d, LayerLocks.All, () => d.SetLayerBlendMode(l.Id, LayerBlendMode.Multiply), "blend mode");
            AssertRefused(d, LayerLocks.All, () => d.SetLayerClipping(l.Id, true), "clipping");
            AssertRefused(d, LayerLocks.All, () => d.SetChannelEnabled(l.Id, PaintChannel.Roughness, true), "channels");
            AssertRefused(d, LayerLocks.All, () => d.SetLayerMaskDensity(l.Id, .5), "mask density");
            AssertRefused(d, LayerLocks.All, () => d.SetLayerMaskInverted(l.Id, true), "mask inversion");
            AssertRefused(d, LayerLocks.All, () => d.SetLayerMaskEnabled(l.Id, false), "mask on/off");
            AssertRefused(d, LayerLocks.All, () => d.RemoveLayerMask(l.Id), "removing the mask");
            AssertRefused(d, LayerLocks.All, () => d.BeginMaskStroke(l.Id, Hard(new Rgba32(0, 0, 0))), "painting the mask");
            AssertRefused(d, LayerLocks.All, () => d.FillMask(l.Id), "filling the mask");
            AssertRefused(d, LayerLocks.All, () => d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Invert()), "adding a filter");
            AssertRefused(d, LayerLocks.All, () => d.RemoveFilter(l.Id, blur.Id), "removing a filter");
            AssertRefused(d, LayerLocks.All, () => d.SetFilterStrength(l.Id, blur.Id, .5), "a filter's strength");
            AssertRefused(d, LayerLocks.All, () => d.BeginStroke(l.Id, PaintChannel.Color, Hard(new Rgba32(1, 1, 1))), "painting");
            AssertRefused(d, LayerLocks.All, () => d.Transform(l.Id, Affine2D.Translation(1, 0)), "moving");
            AssertRefused(d, LayerLocks.All, () => d.SetAdjustment(adjustment.Id, AdjustmentSettings.Levels(.1, .9, 1, 0, 1)), "an adjustment's settings");
            Assert.That(d.EffectiveLocks(l.Id), Is.EqualTo(LayerLocks.All | LayerLocks.Transparency | LayerLocks.Pixels | LayerLocks.Position));
            // 許すもの
            d.SetLayerName(l.Id, "named"); d.SetLayerVisibility(l.Id, false); d.SetLayerVisibility(l.Id, true);
            d.MoveLayer(l.Id, 0); d.MoveLayer(l.Id, 1);
            var g = d.GroupLayers(new[] { l.Id }, "g"); d.Ungroup(g.Id);
            var copy = d.DuplicateLayer(l.Id); Assert.That(copy.Locks, Is.EqualTo(LayerLocks.All));
            d.RemoveLayer(copy.Id); d.RemoveLayer(l.Id);
            Assert.That(d.Layers.Select(x => x.Name), Is.EqualTo(new[] { "a", "adj" }));
        }

        [Test] public void AGroupsLocksApplyToItsContentsAndTheRefusalNamesTheGroup()
        {
            var d = NewDocument(); var l = Varied(d, "child"); var outside = Varied(d, "outside");
            var g = d.GroupLayers(new[] { l.Id }, "locked group");
            d.SetLayerLocks(g.Id, LayerLocks.Pixels);
            Assert.That(d.EffectiveLocks(l.Id), Is.EqualTo(LayerLocks.Pixels)); Assert.That(l.Locks, Is.EqualTo(LayerLocks.None));
            var ex = Assert.Throws<LayerLockedException>(() => d.BeginStroke(l.Id, PaintChannel.Color, Hard(new Rgba32(1, 1, 1))));
            Assert.That((ex.LockedBy, ex.LockedByName, ex.LayerId), Is.EqualTo((g.Id, "locked group", l.Id)));
            Assert.That(ex.Message, Does.Contain("locked group"));
            Dab(d, outside, Hard(new Rgba32(1, 1, 1)));
            // グループの透明部分のロックも中身に効く
            d.SetLayerLocks(g.Id, LayerLocks.Transparency); var before = Pixels(l);
            Dab(d, l, Hard(new Rgba32(0, 0, 0)));
            AssertAlphaKept(before, Pixels(l), (p, i) => new Rgba32(0, 0, 0), "inherited transparency lock");
            // 外へ出せば効かない
            d.MoveLayerTo(l.Id, Guid.Empty, 0);
            Assert.That(d.EffectiveLocks(l.Id), Is.EqualTo(LayerLocks.None));
        }

        [Test] public void ChangingLocksIsOneUndoStepForManyLayersAndUnknownFlagsAreRefused()
        {
            var d = NewDocument(); var a = d.AddLayer("a"); var b = d.AddLayer("b"); var c = d.AddLayer("c");
            d.SetLayerLocks(c.Id, LayerLocks.Position); d.ClearHistory(); long revision = d.Revision;
            d.ChangeLayerLocks(new[] { a.Id, b.Id, c.Id }, LayerLocks.Transparency, true);
            Assert.That(new[] { a.Locks, b.Locks, c.Locks }, Is.EqualTo(new[] { LayerLocks.Transparency, LayerLocks.Transparency, LayerLocks.Transparency | LayerLocks.Position }));
            Assert.That(d.UndoCount, Is.EqualTo(1)); Assert.That(d.Revision, Is.GreaterThan(revision), "a saved state changed");
            d.ChangeLayerLocks(new[] { a.Id, c.Id }, LayerLocks.Transparency | LayerLocks.Position, false);
            Assert.That(new[] { a.Locks, b.Locks, c.Locks }, Is.EqualTo(new[] { LayerLocks.None, LayerLocks.Transparency, LayerLocks.None }));
            d.Undo(); d.Undo();
            Assert.That(new[] { a.Locks, b.Locks, c.Locks }, Is.EqualTo(new[] { LayerLocks.None, LayerLocks.None, LayerLocks.Position }));
            d.Redo();
            Assert.That(b.Locks, Is.EqualTo(LayerLocks.Transparency));
            int undo = d.UndoCount;
            d.ChangeLayerLocks(new[] { b.Id }, LayerLocks.Transparency, true);
            Assert.That(d.UndoCount, Is.EqualTo(undo), "no change, no undo step");
            Assert.That(() => d.SetLayerLocks(a.Id, (LayerLocks)16), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => d.ChangeLayerLocks(new[] { a.Id }, (LayerLocks)32, true), Throws.InstanceOf<ArgumentOutOfRangeException>());
            using (var s = d.BeginStroke(a.Id, PaintChannel.Color, Hard(new Rgba32(1, 1, 1))))
                Assert.That(() => d.SetLayerLocks(a.Id, LayerLocks.Pixels), Throws.InvalidOperationException, "not during a stroke");
        }

        // ───────── 結合・焼き込み ─────────

        [Test] public void MergingRefusesLockedPixelsAndKeepsAClippingBasesTransparencyLock()
        {
            var d = NewDocument(); var baseLayer = Varied(d, "base");
            var clipped = d.AddLayer("shade"); clipped.GetChannel(PaintChannel.Color);
            d.Fill(clipped.Id, PaintChannel.Color, new Rgba32(0, 0, 0, 128)); d.SetLayerClipping(clipped.Id, true);
            var top = d.AddLayer("top"); d.Fill(top.Id, PaintChannel.Color, new Rgba32(255, 0, 0, 255), 1, SelectionMask.Rectangle(d, 0, 0, 5, 5));
            d.SetLayerLocks(baseLayer.Id, LayerLocks.Transparency);
            // クリッピングの基へ当てる結合は基のアルファを変えない: 行い、結果は透明部分のロックを保つ
            var composite = d.Composite(PaintChannel.Color);
            var report = d.MergeDown(clipped.Id);
            Assert.That(report.Method, Is.EqualTo(MergeMethod.IntoClippingBase));
            var merged = d.GetLayer(report.ResultId);
            Assert.That(merged.Locks, Is.EqualTo(LayerLocks.Transparency));
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(composite));
            // 透明部分のロックの層へ普通に重ねる結合はアルファが変わるので断る
            AssertRefused(d, LayerLocks.Transparency, () => d.MergeDown(top.Id), "merge down onto a transparency-locked layer");
            d.SetLayerLocks(merged.Id, LayerLocks.Pixels);
            AssertRefused(d, LayerLocks.Pixels, () => d.MergeDown(top.Id), "into a pixel-locked layer");
            d.SetLayerLocks(merged.Id, LayerLocks.None); d.SetLayerLocks(top.Id, LayerLocks.All);
            AssertRefused(d, LayerLocks.All, () => d.MergeDown(top.Id), "a fully locked upper layer");
            AssertRefused(d, LayerLocks.All, () => d.MergeVisible("all"), "merge visible with a locked layer");
            var g = d.GroupLayers(new[] { top.Id }, "g");
            AssertRefused(d, LayerLocks.All, () => d.MergeGroup(g.Id), "a group holding a locked layer");
            AssertRefused(d, LayerLocks.All, () => d.MergeLayers(new[] { merged.Id, g.Id }), "merging several with a locked one");
        }

        [Test] public void BakingFiltersKeepsAlphaUnderTheTransparencyLockAndIsRefusedUnderThePixelLock()
        {
            var d = NewDocument(); var l = Varied(d);
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(2));
            var output = new Rgba32[W * H]; for (int i = 0; i < output.Length; i++) output[i] = l.GetOutputPixel(PaintChannel.Color, i % W, i / W);
            d.SetLayerLocks(l.Id, LayerLocks.Transparency); var before = Pixels(l);
            Assert.That(d.BakeFilters(l.Id), Is.True);
            Assert.That(l.Filters, Is.Empty);
            AssertAlphaKept(before, Pixels(l), (p, i) => output[i].A == 0 ? p : output[i], "baked blur keeps each pixel's alpha");
            d.Undo();
            d.SetLayerLocks(l.Id, LayerLocks.Pixels);
            AssertRefused(d, LayerLocks.Pixels, () => d.BakeFilters(l.Id), "content bake under the pixel lock");
            // マスクだけの焼き込みは画像のロックでも行う（マスクは描ける）
            var m = d.AddLayer("masked"); d.AddLayerMask(m.Id); d.AddFilter(m.Id, FilterTarget.Mask, FilterSettings.Invert());
            d.SetLayerLocks(m.Id, LayerLocks.Pixels);
            Assert.That(d.BakeFilters(m.Id), Is.True);
            d.SetLayerLocks(m.Id, LayerLocks.All);
            AssertRefused(d, LayerLocks.All, () => d.AddFilter(m.Id, FilterTarget.Mask, FilterSettings.Invert()), "a mask filter under Lock All");
        }

        // ───────── 保存 ─────────

        /// <summary>1 つ目の層の属性の 1 バイト（版 12: ビット 0 クリッピング、ビット 1 ロックが続く）の位置。</summary>
        static int AttributeByte(PaintLayer first) => 8 + 4 + 16 + 12 + ArchiveTestUtil.NormalSettingsBytes + 4 + 16 + 4 + System.Text.Encoding.UTF8.GetByteCount(first.Name) + 1 + 8 + 4;

        [Test] public void Version12SavesLocksAndADocumentWithoutLocksIsLaidOutAsVersion11()
        {
            var d = NewDocument(); var a = Varied(d, "a"); var b = d.AddLayer("b"); var g = d.GroupLayers(new[] { b.Id }, "g");
            var fill = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(1, 2, 3, 255) } });
            d.SetLayerClipping(fill.Id, true);
            d.SetLayerLocks(a.Id, LayerLocks.Transparency | LayerLocks.Position); d.SetLayerLocks(g.Id, LayerLocks.All); d.SetLayerLocks(fill.Id, LayerLocks.Pixels);
            var bytes = DocumentBinary.Write(d);
            Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(12)); Assert.That(DocumentBinary.CurrentVersion, Is.EqualTo(12));
            var read = DocumentBinary.Read(bytes);
            Assert.That(read.Layers.Select(l => (l.Name, l.Locks, l.Clipping)), Is.EqualTo(d.Layers.Select(l => (l.Name, l.Locks, l.Clipping))));
            Assert.That(read.CanUndo, Is.False, "the locks are loaded, not edited");
            Assert.That(DocumentBinary.Write(read), Is.EqualTo(bytes), "byte for byte");
            Assert.That(read.EffectiveLocks(b.Id) & LayerLocks.Pixels, Is.EqualTo(LayerLocks.Pixels), "the group's lock reaches its contents after loading");
            Assert.That(bytes[AttributeByte(a)], Is.EqualTo(2)); Assert.That(BitConverter.ToInt32(bytes, AttributeByte(a) + 1), Is.EqualTo((int)(LayerLocks.Transparency | LayerLocks.Position)));

            // ロックを外すと版 11 と同じ並び（版の数だけが違う）: 版 11 として読める
            d.SetLayerLocks(a.Id, LayerLocks.None); d.SetLayerLocks(g.Id, LayerLocks.None); d.SetLayerLocks(fill.Id, LayerLocks.None);
            var plain = DocumentBinary.Write(d);
            Assert.That(plain.Length, Is.EqualTo(bytes.Length - 3 * 4), "one int per locked layer, nothing else");
            var v11 = (byte[])plain.Clone(); BitConverter.GetBytes(11).CopyTo(v11, 8);
            var old = DocumentBinary.Read(v11);
            Assert.That(old.Layers.Select(l => (l.Name, l.Locks, l.Clipping)), Is.EqualTo(d.Layers.Select(l => (l.Name, l.Locks, l.Clipping))));
            Assert.That(DocumentBinary.Write(old), Is.EqualTo(plain));
            // 版 11 に版 12 のロックの印は無い（古い読み手は版の数で断る）
            var v11Locked = (byte[])bytes.Clone(); BitConverter.GetBytes(11).CopyTo(v11Locked, 8);
            Assert.That(() => DocumentBinary.Read(v11Locked), Throws.InstanceOf<InvalidDataException>());
        }

        [Test] public void UnknownAttributeOrLockBitsAreRefusedInsteadOfDropped()
        {
            var d = NewDocument(); var l = d.AddLayer("L"); d.SetLayerLocks(l.Id, LayerLocks.Position);
            var bytes = DocumentBinary.Write(d); int at = AttributeByte(l);
            Assert.That(bytes[at], Is.EqualTo(2));
            var unknownAttribute = (byte[])bytes.Clone(); unknownAttribute[at] = 2 | 4;
            Assert.That(() => DocumentBinary.Read(unknownAttribute), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("Unknown layer attribute flags"));
            var unknownLock = (byte[])bytes.Clone(); BitConverter.GetBytes((int)LayerLocks.Position | 16).CopyTo(unknownLock, at + 1);
            Assert.That(() => DocumentBinary.Read(unknownLock), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("Unknown layer lock flags"));
            var empty = (byte[])bytes.Clone(); BitConverter.GetBytes(0).CopyTo(empty, at + 1);
            Assert.That(() => DocumentBinary.Read(empty), Throws.InstanceOf<InvalidDataException>(), "the locks follow only when there are some");
            var high = (byte[])bytes.Clone(); BitConverter.GetBytes(int.MinValue).CopyTo(high, at + 1);
            Assert.That(() => DocumentBinary.Read(high), Throws.InstanceOf<InvalidDataException>());
        }

        [Test] public void ResizingKeepsTheLocksAndChangesLockedLayersToo()
        {
            var d = NewDocument(); var l = Varied(d); d.SetLayerLocks(l.Id, LayerLocks.All);
            d.AddFilter(d.AddLayer("other").Id, FilterTarget.Content, FilterSettings.GaussianBlur(2));
            var resized = d.Resampled(W * 2, H * 2, CanvasResampling.Nearest).Document;
            Assert.That(resized.GetLayer(l.Id).Locks, Is.EqualTo(LayerLocks.All));
            Assert.That(resized.GetLayer(l.Id).GetPixel(PaintChannel.Color, 60, 10), Is.EqualTo(l.GetPixel(PaintChannel.Color, 30, 5)), "the image size changes every layer (Photoshop's Image Size)");
        }
    }
}
