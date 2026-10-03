using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// ステンシル（Substance Painter のステンシル。画面に重ねた画像を通して塗る）の Core: 量のモードは輝度 × α を天井に掛け（重なったダブで
    /// その量を越えない。灰色 128 は不透明度 128/255 のストロークとバイトまで同じ）、色のモードは画像の色をそのチャンネルの値として塗り（α が量）、
    /// 画像の外は塗らない（繰り返しの軸は繰り返す）。マテリアルのストロークでは色は名指したチャンネルだけ、ほかは量だけ。値の読み方は塗りつぶしの
    /// 画像と同じ。3D のダブ（ApplyPixel・ApplyDab）は画素ごとの点で読み、2D の写しと同じ値。Undo/Redo・取消で元どおり、選択範囲と透明部分の
    /// ロック・消しゴム・ワーカーのダブもそのまま効く。ミップの予算・不正な入力・写しの無い 2D は断る。
    /// </summary>
    public sealed class StencilTests
    {
        const int W = 64, H = 48, Tile = 16;

        static PaintDocument Document(out Guid layer, int w = W, int h = H, int tile = Tile)
        {
            var d = new PaintDocument(w, h, tile, 256L << 20, new Guid("5e7c11a0-0000-4000-8000-000000000001")) { ActiveStrokeBudgetBytes = 64L << 20 };
            layer = d.AddLayer("paint", new Guid("5e7c11a0-0000-4000-8000-000000000002")).Id;
            d.ClearHistory(); return d;
        }
        static ImageContent Image(int w, int h, Func<int, int, Rgba32> pixel)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var p = pixel(x, y); int o = (y * w + x) * 4; rgba[o] = p.R; rgba[o + 1] = p.G; rgba[o + 2] = p.B; rgba[o + 3] = p.A; }
            return ImageContent.FromPixels(rgba, w, h);
        }
        static StencilImage Stencil(ImageContent c, ResourceColorSpace space = ResourceColorSpace.Srgb) => new StencilImage(c, space);
        /// <summary>A hard brush that covers the whole canvas with one dab (coverage 1 everywhere, ceiling = opacity).</summary>
        static BrushSettings Covering(double opacity = 1, double flow = 1, Rgba32? color = null) => new BrushSettings
        { Radius = 200, Hardness = 1, Opacity = opacity, Flow = flow, PressureOpacity = false, PressureFlow = false, PressureSize = false, Color = color ?? new Rgba32(30, 200, 90, 255), Seed = 1 };
        static void Dab(BrushStroke s, double x = 32, double y = 24) { s.Add(new BrushSample(x, y, 1, 0)); }
        static Rgba32 Pixel(PaintDocument d, Guid id, int x, int y, PaintChannel c = PaintChannel.Color) => d.GetLayer(id).GetPixel(c, x, y);
        static readonly Rgba32 White = new Rgba32(255, 255, 255, 255), Black = new Rgba32(0, 0, 0, 255);

        // ───────── 量（マスク） ─────────

        [Test] public void AMaskLetsThePaintThroughWhereItIsWhiteAndHoldsItBackWhereItIsBlack()
        {
            var image = Image(W, H, (x, y) => x < W / 2 ? White : Black);
            var plain = Document(out var pid);
            using (var s = plain.BeginStroke(pid, PaintChannel.Color, Covering(.8))) { Dab(s); s.Commit(); }
            var d = Document(out var id);
            var b = Covering(.8); b.Stencil = new BrushStencil(Stencil(image), StencilMode.Mask, canvasToImage: StencilMapping.Translation(0, 0));
            using (var s = d.BeginStroke(id, PaintChannel.Color, b)) { Dab(s); Assert.That(s.Commit(), Is.True); }
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                Assert.That(Pixel(d, id, x, y), Is.EqualTo(x < W / 2 ? Pixel(plain, pid, x, y) : Rgba32.Transparent), x + "," + y);
        }

        [Test] public void AGreyCapsTheStrokeAtItsValueHoweverManyDabsOverlap()
        {
            // 天井に掛けるので、流量の小さいダブを何度重ねても 128/255 を越えない。不透明度 128/255 のストロークとバイトまで同じ
            var grey = Image(W, H, (x, y) => new Rgba32(128, 128, 128, 255));
            var line = Enumerable.Range(0, 30).Select(i => new BrushSample(10 + i * .5, 20 + (i % 3), 1, i * .01)).ToArray();
            BrushSettings B(double opacity) { var s = Covering(opacity, .25); s.Radius = 12; s.Hardness = .5; s.Spacing = .05; return s; }
            var plain = Document(out var pid);
            using (var s = plain.BeginStroke(pid, PaintChannel.Color, B(128 / 255.0))) { foreach (var p in line) s.Add(p); s.Commit(); }
            var d = Document(out var id);
            var b = B(1); b.Stencil = new BrushStencil(Stencil(grey), StencilMode.Mask, canvasToImage: StencilMapping.Translation(0, 0));
            using (var s = d.BeginStroke(id, PaintChannel.Color, b)) { foreach (var p in line) s.Add(p); s.Commit(); }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(DocumentBinary.Write(plain)), "a 50 % grey stencil is the 128/255 opacity, byte for byte");
            Assert.That(Pixel(d, id, 14, 21).A, Is.EqualTo(128), "built up to the stencil's amount and not past it");
        }

        [Test] public void InvertAndAlphaShapeTheMask()
        {
            // 透明な所は塗らない。反転は (1 − 輝度) × α: 透明の上の黒い形（ロゴ）を反転で塗れる
            var logo = Image(W, H, (x, y) => x < 20 ? new Rgba32(0, 0, 0, 255) : x < 40 ? new Rgba32(255, 255, 255, 0) : new Rgba32(255, 255, 255, 255));
            var d = Document(out var id);
            var b = Covering(); b.Stencil = new BrushStencil(Stencil(logo), StencilMode.Mask, invert: true, canvasToImage: StencilMapping.Translation(0, 0));
            using (var s = d.BeginStroke(id, PaintChannel.Color, b)) { Dab(s); s.Commit(); }
            Assert.That(Pixel(d, id, 5, 10).A, Is.EqualTo(255), "black, inverted, paints");
            Assert.That(Pixel(d, id, 30, 10), Is.EqualTo(Rgba32.Transparent), "transparent never paints");
            Assert.That(Pixel(d, id, 50, 10), Is.EqualTo(Rgba32.Transparent), "white, inverted, holds back");
            var e = Document(out var eid);
            b = Covering(); b.Stencil = new BrushStencil(Stencil(logo), StencilMode.Mask, canvasToImage: StencilMapping.Translation(0, 0));
            using (var s = e.BeginStroke(eid, PaintChannel.Color, b)) { Dab(s); s.Commit(); }
            Assert.That(Pixel(e, eid, 5, 10), Is.EqualTo(Rgba32.Transparent)); Assert.That(Pixel(e, eid, 30, 10), Is.EqualTo(Rgba32.Transparent)); Assert.That(Pixel(e, eid, 50, 10).A, Is.EqualTo(255));
        }

        // ───────── 色 ─────────

        static Rgba32 Varied(int x, int y) => new Rgba32((byte)(x * 13 % 256), (byte)(y * 29 % 256), (byte)((x * y + 7) % 256), 255);

        [Test] public void AColourStencilPaintsItsOwnColoursTexelForTexel()
        {
            // 画像の画素がキャンバスの画素にちょうど重なる写し（8, 4 ずらす）: 塗った画素はその画素の色そのもの。画像の外は塗らない
            var image = Image(40, 30, Varied);
            var d = Document(out var id);
            var b = Covering(color: new Rgba32(1, 2, 3, 255)); b.Stencil = new BrushStencil(Stencil(image), StencilMode.Color, canvasToImage: StencilMapping.Translation(-8, -4), colorChannels: new[] { PaintChannel.Color });
            using (var s = d.BeginStroke(id, PaintChannel.Color, b)) { Dab(s); s.Commit(); }
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                bool inside = x >= 8 && x < 48 && y >= 4 && y < 34;
                Assert.That(Pixel(d, id, x, y), Is.EqualTo(inside ? Varied(x - 8, y - 4) : Rgba32.Transparent), x + "," + y);
            }
        }

        [Test] public void AColourStencilsAlphaIsTheAmountAndTheBrushAlphaStays()
        {
            var image = Image(W, H, (x, y) => new Rgba32(200, 10, 60, (byte)(x < 20 ? 0 : x < 40 ? 128 : 255)));
            var d = Document(out var id);
            var b = Covering(color: new Rgba32(0, 0, 0, 255)); b.Stencil = new BrushStencil(Stencil(image), StencilMode.Color, canvasToImage: StencilMapping.Translation(0, 0), colorChannels: new[] { PaintChannel.Color });
            using (var s = d.BeginStroke(id, PaintChannel.Color, b)) { Dab(s); s.Commit(); }
            Assert.That(Pixel(d, id, 10, 10), Is.EqualTo(Rgba32.Transparent));
            Assert.That(Pixel(d, id, 30, 10), Is.EqualTo(new Rgba32(200, 10, 60, 128)));
            Assert.That(Pixel(d, id, 50, 10), Is.EqualTo(new Rgba32(200, 10, 60, 255)));
            // 描画色のアルファ（半透明の筆）はそのまま効く
            var e = Document(out var eid);
            b = Covering(color: new Rgba32(0, 0, 0, 102)); b.Stencil = new BrushStencil(Stencil(image), StencilMode.Color, canvasToImage: StencilMapping.Translation(0, 0), colorChannels: new[] { PaintChannel.Color });
            using (var s = e.BeginStroke(eid, PaintChannel.Color, b)) { Dab(s); s.Commit(); }
            Assert.That(Pixel(e, eid, 50, 10), Is.EqualTo(new Rgba32(200, 10, 60, 102)));
        }

        [Test] public void AutoReadsAGreyImageAsAMaskAndAColourImageAsColour()
        {
            var grey = Stencil(Image(4, 4, (x, y) => new Rgba32((byte)(x * 60), (byte)(x * 60), (byte)(x * 60), (byte)(y * 80))));
            var colour = Stencil(Image(4, 4, (x, y) => x == 3 && y == 3 ? new Rgba32(10, 11, 10, 255) : White));
            Assert.That(grey.IsGrey, Is.True); Assert.That(colour.IsGrey, Is.False, "one coloured texel is enough");
            Assert.That(new BrushStencil(grey, StencilMode.Auto).Mode, Is.EqualTo(StencilMode.Mask));
            Assert.That(new BrushStencil(colour, StencilMode.Auto).Mode, Is.EqualTo(StencilMode.Color));
            Assert.That(new BrushStencil(grey, StencilMode.Color).Mode, Is.EqualTo(StencilMode.Color), "a chosen mode is kept");
        }

        [Test] public void ColourChannelsEncodeALinearImageAndScalarChannelsTakeTheLuminance()
        {
            // 塗りつぶしの画像と同じ読み方: 色のチャンネルではデータ（リニア）の画像を sRGB に、スカラーのチャンネルは輝度、Normal はそのまま
            var image = Image(W, H, (x, y) => new Rgba32(50, 120, 230, 255));
            foreach (var (channel, space, expected) in new[]
            {
                (PaintChannel.Color, ResourceColorSpace.Srgb, new Rgba32(50, 120, 230, 255)),
                (PaintChannel.Color, ResourceColorSpace.Linear, new Rgba32(FillImageColor.Convert(FillImageConversion.LinearToSrgb, 50), FillImageColor.Convert(FillImageConversion.LinearToSrgb, 120), FillImageColor.Convert(FillImageConversion.LinearToSrgb, 230), 255)),
                (PaintChannel.Emission, ResourceColorSpace.Linear, new Rgba32(FillImageColor.Convert(FillImageConversion.LinearToSrgb, 50), FillImageColor.Convert(FillImageConversion.LinearToSrgb, 120), FillImageColor.Convert(FillImageConversion.LinearToSrgb, 230), 255)),
                (PaintChannel.Roughness, ResourceColorSpace.Srgb, new Rgba32(FillImageColor.Luminance(50, 120, 230), FillImageColor.Luminance(50, 120, 230), FillImageColor.Luminance(50, 120, 230), 255)),
                (PaintChannel.Normal, ResourceColorSpace.Linear, new Rgba32(50, 120, 230, 255)),
            })
            {
                var d = Document(out var id); d.SetChannelEnabled(id, channel, true); d.ClearHistory();
                var b = Covering(); b.Stencil = new BrushStencil(Stencil(image, space), StencilMode.Color, canvasToImage: StencilMapping.Translation(0, 0), colorChannels: new[] { channel });
                using (var s = d.BeginStroke(id, channel, b)) { Dab(s); s.Commit(); }
                Assert.That(Pixel(d, id, 20, 20, channel), Is.EqualTo(expected), channel + " " + space);
            }
        }

        // ───────── 繰り返し ─────────

        [Test] public void TilingRepeatsTheImageOnlyAlongItsAxes()
        {
            var image = Image(8, 8, (x, y) => White);
            foreach (StencilTiling tiling in Enum.GetValues(typeof(StencilTiling)))
            {
                var d = Document(out var id);
                var b = Covering(); b.Stencil = new BrushStencil(Stencil(image), StencilMode.Mask, tiling, canvasToImage: StencilMapping.Translation(0, 0));
                using (var s = d.BeginStroke(id, PaintChannel.Color, b)) { Dab(s); s.Commit(); }
                bool h = tiling == StencilTiling.Horizontal || tiling == StencilTiling.Both, v = tiling == StencilTiling.Vertical || tiling == StencilTiling.Both;
                for (int y = 0; y < H; y += 3) for (int x = 0; x < W; x += 3)
                {
                    bool painted = (x < 8 || h) && (y < 8 || v);
                    Assert.That(Pixel(d, id, x, y).A, Is.EqualTo(painted ? 255 : 0), tiling + " " + x + "," + y);
                }
            }
        }

        // ───────── マテリアル ─────────

        [Test] public void AMaterialStrokePutsTheColourIntoTheNamedChannelsAndTheAmountIntoEveryChannel()
        {
            var image = Image(W, H, (x, y) => new Rgba32((byte)(x * 4), 90, (byte)(y * 5), (byte)(y < 24 ? 255 : 128)));
            var rough = new Rgba32(200, 200, 200, 255);
            var channels = new[] { new ChannelPaint(PaintChannel.Color, new Rgba32(1, 2, 3, 255)), new ChannelPaint(PaintChannel.Roughness, rough) };
            var d = Document(out var id);
            var b = Covering(); b.Stencil = new BrushStencil(Stencil(image), StencilMode.Color, canvasToImage: StencilMapping.Translation(0, 0), colorChannels: new[] { PaintChannel.Color });
            using (var s = d.BeginMaterialStroke(id, channels, b)) { Dab(s); s.Commit(); }
            Assert.That(d.UndoCount, Is.EqualTo(1));
            Assert.That(Pixel(d, id, 10, 5), Is.EqualTo(new Rgba32(40, 90, 25, 255)), "Base Color takes the image's colour");
            Assert.That(Pixel(d, id, 10, 5, PaintChannel.Roughness), Is.EqualTo(rough), "Roughness keeps its value");
            Assert.That(Pixel(d, id, 10, 30, PaintChannel.Roughness), Is.EqualTo(new Rgba32(200, 200, 200, 128)), "through the image's alpha");
            Assert.That(Pixel(d, id, 10, 30), Is.EqualTo(new Rgba32(40, 90, 150, 128)));

            // 量のモード: 各チャンネルを同じステンシルで 1 つずつ塗ったのとバイトまで同じ
            var mask = Image(W, H, (x, y) => { byte g = (byte)((x * 7 + y * 3) % 256); return new Rgba32(g, g, g, 255); });
            BrushSettings M() { var s = Covering(.9, .5); s.Radius = 14; s.Hardness = .3; s.Stencil = new BrushStencil(Stencil(mask), StencilMode.Mask, canvasToImage: new StencilMapping(.7, .2, 3, -.1, .8, 5)); return s; }
            var line = new[] { new BrushSample(10, 10, .5, 0), new BrushSample(40, 30, 1, .1), new BrushSample(55, 12, .7, .2) };
            var m = Document(out var mid);
            using (var s = m.BeginMaterialStroke(mid, channels, M())) { foreach (var p in line) s.Add(p); s.Commit(); }
            foreach (var c in channels)
            {
                var one = Document(out var oid);
                var settings = M(); settings.Color = c.Value;
                one.SetChannelEnabled(oid, c.Channel, true);
                using (var s = one.BeginStroke(oid, c.Channel, settings)) { foreach (var p in line) s.Add(p); s.Commit(); }
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) Assert.That(Pixel(m, mid, x, y, c.Channel), Is.EqualTo(Pixel(one, oid, x, y, c.Channel)), c.Channel + " " + x + "," + y);
            }
        }

        // ───────── 3D のダブ ─────────

        [Test] public void MeshDabsReadTheStencilAtTheirOwnPointsAndMatchTheCanvasMapping()
        {
            var image = Image(W, H, Varied);
            var mapping = new StencilMapping(.9, .1, 2.5, -.15, 1.05, 1.25);
            var stencil = Stencil(image);
            // 2D の写しを使うストローク（ApplyPixel は点が無ければ写しで読む）と、同じ点を渡すストロークは同じ
            var a = Document(out var aid); var c = Document(out var cid);
            var ba = Covering(.9); ba.Stencil = new BrushStencil(stencil, StencilMode.Color, canvasToImage: mapping, colorChannels: new[] { PaintChannel.Color });
            var bc = Covering(.9); bc.Stencil = new BrushStencil(stencil, StencilMode.Color, colorChannels: new[] { PaintChannel.Color });
            using (var sa = a.BeginStroke(aid, PaintChannel.Color, ba))
            using (var sc = c.BeginStroke(cid, PaintChannel.Color, bc))
            {
                for (int y = 4; y < 40; y++) for (int x = 3; x < 50; x++)
                {
                    double cov = ((x * 5 + y * 3) % 10) / 9.0, pressure = .5 + (x % 3) * .2;
                    sa.ApplyPixel(x, y, cov, pressure);
                    double ix = mapping.XX * (x + .5) + mapping.XY * (y + .5) + mapping.X0, iy = mapping.YX * (x + .5) + mapping.YY * (y + .5) + mapping.Y0;
                    sc.ApplyPixel(x, y, cov, pressure, new StencilPoint(ix, iy, mapping.Footprint));
                }
                sa.Commit(); sc.Commit();
            }
            Assert.That(a.Composite(PaintChannel.Color), Is.EqualTo(c.Composite(PaintChannel.Color)));
            Assert.That(a.Composite(PaintChannel.Color).Any(v => v != 0), Is.True);

            // 効果のダブ（ApplyDab）も点ごとに読む: 量 0 の点は塗らない
            var e = Document(out var eid);
            var be = Covering(); be.Stencil = new BrushStencil(Stencil(Image(4, 4, (x, y) => White)), StencilMode.Mask);
            using (var s = e.BeginStroke(eid, PaintChannel.Color, be))
            {
                var pixels = new[] { new BrushPixel(5, 5, 1), new BrushPixel(6, 5, 1) };
                s.ApplyDab(pixels, 5, 5, 1, new[] { new StencilPoint(2, 2), new StencilPoint(10, 2) });
                s.Commit();
            }
            Assert.That(Pixel(e, eid, 5, 5).A, Is.EqualTo(255)); Assert.That(Pixel(e, eid, 6, 5), Is.EqualTo(Rgba32.Transparent), "off the stencil");
            // 繰り返しでも、FarAway より遠い点（カメラの後ろなど）は読まない
            var far = Document(out var fid);
            var bf = Covering(); bf.Stencil = new BrushStencil(Stencil(Image(4, 4, (x, y) => White)), StencilMode.Mask, StencilTiling.Both);
            using (var s = far.BeginStroke(fid, PaintChannel.Color, bf))
            {
                Assert.That(s.ApplyPixel(5, 5, 1, 1, new StencilPoint(-StencilImage.FarAway, -StencilImage.FarAway)), Is.False);
                Assert.That(s.ApplyPixel(6, 5, 1, 1, new StencilPoint(1e7 + .5, 2.5)), Is.True, "a repeating stencil far but not too far away");
                s.Commit();
            }
        }

        // ───────── Undo・取消・選択範囲・ロック・消しゴム・並列 ─────────

        [Test] public void UndoRedoAndCancelRestoreExactly()
        {
            var image = Image(W, H, Varied);
            var d = Document(out var id);
            var before = DocumentBinary.Write(d);
            BrushSettings B() { var s = Covering(.7, .6); s.Radius = 9; s.Stencil = new BrushStencil(Stencil(image), StencilMode.Color, canvasToImage: StencilMapping.Translation(1, 2), colorChannels: new[] { PaintChannel.Color }); return s; }
            using (var s = d.BeginStroke(id, PaintChannel.Color, B())) { s.Add(new BrushSample(10, 10, 1, 0)); s.Add(new BrushSample(50, 30, 1, .1)); s.Cancel(); }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before), "cancel"); Assert.That(d.UndoCount, Is.Zero);
            using (var s = d.BeginStroke(id, PaintChannel.Color, B())) { s.Add(new BrushSample(10, 10, 1, 0)); s.Add(new BrushSample(50, 30, 1, .1)); s.Commit(); }
            var painted = DocumentBinary.Write(d);
            Assert.That(painted, Is.Not.EqualTo(before)); Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before), "undo");
            d.Redo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(painted), "redo");
        }

        [Test] public void TheSelectionLockedTransparencyAndErasingStillApply()
        {
            var image = Image(W, H, (x, y) => x < W / 2 ? White : Black);
            BrushStencil Mask() => new BrushStencil(Stencil(image), StencilMode.Mask, canvasToImage: StencilMapping.Translation(0, 0));
            // 選択範囲の外は変えない
            var d = Document(out var id);
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 16, 48));
            var b = Covering(); b.Stencil = Mask();
            using (var s = d.BeginStroke(id, PaintChannel.Color, b)) { Dab(s); s.Commit(); }
            Assert.That(Pixel(d, id, 8, 8).A, Is.EqualTo(255)); Assert.That(Pixel(d, id, 24, 8), Is.EqualTo(Rgba32.Transparent), "outside the selection");
            // 透明部分のロック: アルファは変えず、ステンシルが通す所の色だけ
            var l = Document(out var lid);
            var surface = l.GetLayer(lid).GetChannel(PaintChannel.Color);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) surface.SetPixel(x, y, new Rgba32(10, 10, 10, (byte)(y < 10 ? 0 : 100)));
            l.ClearHistory(); l.SetLayerLocks(lid, LayerLocks.Transparency); l.ClearHistory();
            b = Covering(color: new Rgba32(250, 0, 0, 255)); b.Stencil = Mask();
            using (var s = l.BeginStroke(lid, PaintChannel.Color, b)) { Dab(s); s.Commit(); }
            Assert.That(Pixel(l, lid, 8, 20), Is.EqualTo(new Rgba32(250, 0, 0, 100)), "colour moves, alpha stays");
            Assert.That(Pixel(l, lid, 8, 5), Is.EqualTo(new Rgba32(10, 10, 10, 0)), "transparent stays as it was");
            Assert.That(Pixel(l, lid, 50, 20), Is.EqualTo(new Rgba32(10, 10, 10, 100)), "the stencil holds back");
            // 消しゴム: ステンシルが通す所だけ消す（色のモードでも量だけ）
            var e = Document(out var eid);
            var es = e.GetLayer(eid).GetChannel(PaintChannel.Color);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) es.SetPixel(x, y, new Rgba32(1, 2, 3, 255));
            e.ClearHistory();
            b = Covering(); b.Erase = true; b.Stencil = new BrushStencil(Stencil(Image(W, H, (x, y) => new Rgba32(9, 9, 200, (byte)(x < 32 ? 255 : 0)))), StencilMode.Color, canvasToImage: StencilMapping.Translation(0, 0), colorChannels: new[] { PaintChannel.Color });
            using (var s = e.BeginStroke(eid, PaintChannel.Color, b)) { Dab(s); s.Commit(); }
            Assert.That(Pixel(e, eid, 8, 8), Is.EqualTo(Rgba32.Transparent)); Assert.That(Pixel(e, eid, 50, 8), Is.EqualTo(new Rgba32(1, 2, 3, 255)));
            // マスクのストロークは量だけ（色は無い）
            var m = Document(out var mid); m.AddLayerMask(mid); m.ClearHistory();
            b = Covering(); b.Stencil = Mask();
            using (var s = m.BeginMaskStroke(mid, b)) { Dab(s); s.Commit(); }
            var maskSurface = m.GetLayer(mid).Mask.Surface;
            Assert.That(maskSurface.GetPixel(8, 8).A, Is.EqualTo(255)); Assert.That(maskSurface.GetPixel(50, 8).A, Is.Zero);
        }

        [Test] public void LargeDabsOnWorkerThreadsGiveTheSameBytes()
        {
            int degree = CoreParallelism.MaxDegreeOfParallelism;
            try
            {
                var image = Stencil(Image(97, 61, Varied));
                byte[] Run(int threads)
                {
                    CoreParallelism.MaxDegreeOfParallelism = threads;
                    var d = new PaintDocument(384, 256, 32, 256L << 20, new Guid("5e7c11a0-0000-4000-8000-000000000003")) { ActiveStrokeBudgetBytes = 64L << 20 };
                    var id = d.AddLayer("paint").Id;
                    var b = new BrushSettings { Radius = 75, Hardness = .5, Spacing = .1, Opacity = .8, Flow = .3, Seed = 5 };
                    b.Stencil = new BrushStencil(image, StencilMode.Color, StencilTiling.Both, canvasToImage: new StencilMapping(.4, .1, 3, -.05, .45, 7), colorChannels: new[] { PaintChannel.Color });
                    using (var s = d.BeginStroke(id, PaintChannel.Color, b)) { s.Add(new BrushSample(60, 60, .5, 0)); s.Add(new BrushSample(200, 110, 1, .1)); s.Add(new BrushSample(330, 190, .7, .2)); s.Commit(); }
                    return d.Composite(PaintChannel.Color);
                }
                var one = Run(1);
                Assert.That(one.Any(v => v != 0), Is.True);
                Assert.That(Run(3), Is.EqualTo(one)); Assert.That(Run(0), Is.EqualTo(one));
            }
            finally { CoreParallelism.MaxDegreeOfParallelism = degree; }
        }

        // ───────── 拒否 ─────────

        [Test] public void BudgetsAndBadInputAreRefusedWithNothingChanged()
        {
            var image = Image(64, 64, Varied);
            // 64² のミップは 32² + 16² + … + 1² = 1365 画素 = 5,460 バイト
            Assert.Throws<ResourceRefusedException>(() => new StencilImage(image, ResourceColorSpace.Srgb, 5459), "one byte short");
            Assert.That(new StencilImage(image, ResourceColorSpace.Srgb, 5460).MipBytes, Is.EqualTo(5460), "exactly the budget is allowed");
            Assert.Throws<ArgumentOutOfRangeException>(() => new StencilMapping(double.NaN, 0, 0, 0, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new StencilPoint(double.PositiveInfinity, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new StencilPoint(0, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BrushStencil(Stencil(image), (StencilMode)7));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BrushStencil(Stencil(image), StencilMode.Mask, (StencilTiling)9));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BrushStencil(Stencil(image), StencilMode.Color, colorChannels: new[] { (PaintChannel)40 }));
            Assert.Throws<ArgumentNullException>(() => new BrushStencil(null, StencilMode.Mask));

            // 2D の写しの無いステンシルでキャンバスに描くと断り、ストロークを取り消す（何も残さない）
            var d = Document(out var id);
            var before = DocumentBinary.Write(d);
            var b = Covering(); b.Stencil = new BrushStencil(Stencil(image), StencilMode.Mask);
            var stroke = d.BeginStroke(id, PaintChannel.Color, b);
            Assert.Throws<InvalidOperationException>(() => Dab(stroke));
            Assert.That(stroke.IsFinished, Is.True, "the stroke was canceled");
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.Zero);
            stroke = d.BeginStroke(id, PaintChannel.Color, b);
            Assert.Throws<InvalidOperationException>(() => stroke.ApplyPixel(3, 3, 1));
            Assert.That(stroke.IsFinished, Is.True); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            stroke = d.BeginStroke(id, PaintChannel.Color, b);
            Assert.Throws<ArgumentException>(() => stroke.ApplyDab(new[] { new BrushPixel(1, 1, 1) }, 1, 1, 1, new StencilPoint[0]));
            Assert.That(stroke.IsFinished, Is.True);
            Assert.That(d.UndoCount, Is.Zero);

            // 画素ごとに読んだステンシルの値（タイルの画素 × 12 バイト）も 1 回のストロークの予算に入る: 無しなら収まるストロークを、有りなら断って取り消す
            // （16² のタイル 12 枚: 無し 12 × (64 + 16² × 4) = 13,056 バイト、有り 12 × (64 + 16² × 16) = 49,920 バイト）
            var plain = Document(out var pid); plain.ActiveStrokeBudgetBytes = 20000;
            using (var s = plain.BeginStroke(pid, PaintChannel.Color, Covering())) { Dab(s); Assert.That(s.Commit(), Is.True); }
            var tight = Document(out var tid); tight.ActiveStrokeBudgetBytes = 20000;
            var tightBefore = DocumentBinary.Write(tight);
            var bt = Covering(); bt.Stencil = new BrushStencil(Stencil(Image(W, H, (x, y) => White)), StencilMode.Mask, StencilTiling.Both, canvasToImage: StencilMapping.Translation(0, 0));
            stroke = tight.BeginStroke(tid, PaintChannel.Color, bt);
            Assert.Throws<InvalidOperationException>(() => Dab(stroke), "over the one-stroke budget");
            Assert.That(stroke.IsFinished, Is.True);
            Assert.That(DocumentBinary.Write(tight), Is.EqualTo(tightBefore), "nothing is left of the refused stroke"); Assert.That(tight.UndoCount, Is.Zero);
        }
    }
}
