using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 塗りつぶしレイヤーのチャンネルごとの画像（Core）: 色空間（Substance と同じく、データのチャンネルは色空間に関わらずそのまま、色のチャンネルは
    /// データ（リニア）の画像だけ sRGB に、スカラーのチャンネルは輝度）、並列の数・ブロックの大きさ・タイルの大きさによらないバイト、リソースの差し替え・
    /// 色空間の変更で描き直す（変わったタイルの報告）、使っているリソースは消せない、無い ID は塗りつぶしの値と理由、Undo / Redo とドラッグの
    /// まとめと取り消し、値を消すと画像も消える、型・予算・ロックの拒否、複製・大きさの変更・統合、正本の版 15 の往復（バイト一致）と拒否、
    /// 画像の無い文書は版 13 と同じ並び、PSD の書き出し（画素の層として書いて NotCarriedIntoExport で知らせる）。
    /// </summary>
    public sealed class FillImageTests
    {
        const int W = 40, H = 24, T = 16;
        static readonly Rgba32 Value = new Rgba32(60, 70, 80, 255);
        [TearDown] public void ResetThreads() { CoreParallelism.MaxDegreeOfParallelism = 0; }

        static ImageContent Picture(int w, int h, int seed)
        {
            var rgba = new byte[w * h * 4]; new Random(seed).NextBytes(rgba);
            for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] < 40) rgba[i] = 0; else if (rgba[i] > 180) rgba[i] = 255; // 透明（RGB あり）と不透明を混ぜる
            return ImageContent.FromPixels(rgba, w, h);
        }
        static (PaintDocument d, ProjectResources r, PaintLayer fill, ImageResource image) Scene(ImageContent picture = null, ResourceColorSpace space = ResourceColorSpace.Srgb, PaintChannel channel = PaintChannel.Color, int tile = T)
        {
            var r = new ProjectResources(); var image = r.Add("Picture", picture ?? Picture(W, H, 1), ResourceOrigin.None, space, out _);
            var d = new PaintDocument(W, H, tile) { ImageResources = r };
            var fill = d.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { channel, Value } });
            d.SetFillImage(fill.Id, channel, image.Id);
            d.ClearHistory();
            return (d, r, fill, image);
        }
        static Rgba32 At(byte[] rgba, int x, int y, int w = W) { int o = (y * w + x) * 4; return new Rgba32(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]); }

        // ───────── 色空間 ─────────

        static double LinearToSrgb(double c) => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        static byte B(double v) { double x = v * 255 + .5; return x >= 255 ? (byte)255 : x > 0 ? (byte)(int)x : (byte)0; }

        /// <summary>Substance Painter と同じ: データのチャンネル（Roughness・Metallic・Height・Normal）は画像の色空間に関わらず値をそのまま、
        /// 色のチャンネルはデータ（リニア）の画像だけ sRGB にする。スカラーのチャンネルは輝度。</summary>
        [Test] public void DataChannelsUseTheStoredValuesColorChannelsEncodeDataImagesAndScalarsTakeTheLuminance()
        {
            var picture = Picture(W, H, 2);
            foreach (PaintChannel channel in Enum.GetValues(typeof(PaintChannel)))
                foreach (ResourceColorSpace space in Enum.GetValues(typeof(ResourceColorSpace)))
                {
                    var (d, _, fill, _) = Scene(picture, space, channel);
                    var status = d.GetFillImageStatus(fill.Id, channel);
                    bool color = channel == PaintChannel.Color || channel == PaintChannel.Emission, scalar = channel == PaintChannel.Roughness || channel == PaintChannel.Metallic || channel == PaintChannel.Height;
                    var expected = color && space == ResourceColorSpace.Linear ? FillImageConversion.LinearToSrgb : FillImageConversion.None;
                    Assert.That(status.Conversion, Is.EqualTo(expected), channel + " " + space);
                    Assert.That(status.Luminance, Is.EqualTo(scalar));
                    var output = fill.EvaluateOutputRegion(channel, 0, 0, W, H);
                    for (int y = 0; y < H; y += 3) for (int x = 0; x < W; x += 2)
                    {
                        var p = picture.GetPixel(x, y);
                        Func<byte, byte> convert = v => expected == FillImageConversion.LinearToSrgb ? B(LinearToSrgb(v / 255.0)) : v;
                        byte r = convert(p.R), g = convert(p.G), b = convert(p.B);
                        if (scalar) { byte l = (byte)((2126 * r + 7152 * g + 722 * b + 5000) / 10000); r = g = b = l; }
                        Assert.That(At(output, x, y), Is.EqualTo(new Rgba32(r, g, b, p.A)), channel + " " + space + " at " + x + "," + y + " (alpha never converted, transparent RGB kept)");
                    }
                }
            // 灰色の画像はスカラーのチャンネルでも同じ値（輝度の整数の重みは足して 1）: sRGB で取り込んだ灰色のデータの画像も、保存した値のまま
            for (int v = 0; v < 256; v++) Assert.That(FillImageColor.Luminance((byte)v, (byte)v, (byte)v), Is.EqualTo((byte)v));
            Assert.That(FillImageColor.Convert(FillImageConversion.LinearToSrgb, 50), Is.EqualTo(B(LinearToSrgb(50 / 255.0))));
            Assert.That(FillImageColor.Convert(FillImageConversion.None, 7), Is.EqualTo((byte)7));
        }

        [Test] public void ChangingAResourcesColorSpaceOrContentRedrawsTheLayersThatReadIt()
        {
            var (d, r, fill, image) = Scene(space: ResourceColorSpace.Srgb, channel: PaintChannel.Color);
            d.SetFillImage(fill.Id, PaintChannel.Roughness, image.Id);
            var before = d.Composite(PaintChannel.Color); var roughBefore = d.Composite(PaintChannel.Roughness);
            long serial = d.ChangeSerial; var changed = new HashSet<TileCoord>();
            r.SetColorSpace(image.Id, ResourceColorSpace.Linear); // データとして読む: 色のチャンネルでは sRGB にする
            Assert.That(d.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True);
            Assert.That(changed.Count, Is.EqualTo(3 * 2), "every tile of the layer is redrawn");
            Assert.That(d.Composite(PaintChannel.Color), Is.Not.EqualTo(before));
            Assert.That(d.GetFillImageStatus(fill.Id, PaintChannel.Color).Conversion, Is.EqualTo(FillImageConversion.LinearToSrgb));
            Assert.That(d.Composite(PaintChannel.Roughness), Is.EqualTo(roughBefore), "a data channel reads the stored values whatever the colour space");
            // 出どころから更新（同じ ID で中身の差し替え）
            serial = d.ChangeSerial; changed.Clear();
            var next = Picture(W, H, 77);
            r.ReplaceContent(image.Id, next, null);
            Assert.That(d.TryGetChangedTiles(PaintChannel.Roughness, serial, changed), Is.True); Assert.That(changed, Is.Not.Empty);
            var output = fill.EvaluateOutputRegion(PaintChannel.Roughness, 0, 0, W, H);
            var p = next.GetPixel(3, 4); byte l = FillImageColor.Luminance(p.R, p.G, p.B);
            Assert.That(At(output, 3, 4), Is.EqualTo(new Rgba32(l, l, l, p.A)), "the new pixels");
            // 名前を変えるだけなら描き直さない
            serial = d.ChangeSerial; changed.Clear();
            r.Rename(image.Id, "Renamed");
            Assert.That(d.TryGetChangedTiles(PaintChannel.Roughness, serial, changed), Is.True); Assert.That(changed, Is.Empty);
        }

        // ───────── 決定性 ─────────

        [Test] public void TheBytesDoNotDependOnThreadsBlocksOrTileSize()
        {
            var picture = Picture(37, 29, 4);
            byte[] reference = null;
            foreach (int tile in new[] { 8, 16, 64 })
                foreach (int threads in new[] { 1, 0 })
                    foreach (int block in new[] { 8, 64, 4096 })
                    {
                        CoreParallelism.MaxDegreeOfParallelism = threads;
                        var (d, _, fill, _) = Scene(picture, tile: tile);
                        d.FilterBlockPixels = block;
                        d.SetFillProjection(fill.Id, FillProjection.Default.WithTiles(2.7, 1.9).WithRotation(17).WithOffset(.13, .4));
                        var composite = d.Composite(PaintChannel.Color);
                        if (reference == null) reference = composite;
                        else Assert.That(composite, Is.EqualTo(reference), "tile " + tile + ", threads " + threads + ", block " + block);
                    }
        }

        // ───────── リソース ─────────

        [Test] public void AResourceInUseCannotBeRemovedAndAMissingIdShowsTheFillValue()
        {
            var (d, r, fill, image) = Scene();
            r.AddUsageProbe(id => { var users = d.LayersUsingResource(id); return users.Count == 0 ? null : string.Join(", ", users); });
            Assert.That(d.LayersUsingResource(image.Id), Is.EqualTo(new[] { "'Fill' (Color)" }));
            var ex = Assert.Throws<ResourceRefusedException>(() => r.Remove(image.Id));
            Assert.That(ex.Refusal, Is.EqualTo(ResourceRefusal.InUse)); Assert.That(ex.Message, Does.Contain("'Fill' (Color)"));
            Assert.That(r.Count, Is.EqualTo(1));
            // 画像を外すと消せる。Undo で戻った ID は、もうプロジェクトに無い: 塗りつぶしの値と理由（ID は残る）
            d.SetFillImage(fill.Id, PaintChannel.Color, null);
            r.Remove(image.Id);
            d.Undo();
            Assert.That(fill.FillImages[PaintChannel.Color], Is.EqualTo(image.Id), "the reference is kept, not dropped");
            var status = d.GetFillImageStatus(fill.Id, PaintChannel.Color);
            Assert.That(status.Active, Is.False); Assert.That(status.Image, Is.Null); Assert.That(status.Reason, Does.Contain(image.Id.ToString()));
            Assert.That(d.MissingFillImages().Single(), Does.Contain("'Fill' (Color)").And.Contain(image.Id.ToString()));
            var output = fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            for (int i = 0; i < output.Length; i += 4) Assert.That(At(output, i / 4 % W, i / 4 / W), Is.EqualTo(Value));
            // 同じ ID が戻れば画像になる（別のリソースの集まりを差す）
            var back = new ProjectResources(); back.Restore(image.Id, "Back", Picture(W, H, 1), ResourceOrigin.None, ResourceColorSpace.Srgb);
            d.ImageResources = back;
            Assert.That(d.GetFillImageStatus(fill.Id, PaintChannel.Color).Active, Is.True);
            Assert.That(d.MissingFillImages(), Is.Empty);
        }

        // ───────── 編集と Undo ─────────

        [Test] public void ImageProjectionAndValueEditsAreUndoableStepsAndDragsCoalesceOrCancel()
        {
            var r = new ProjectResources(); var a = r.Add("A", Picture(W, H, 1), ResourceOrigin.None, ResourceColorSpace.Srgb, out _); var b = r.Add("B", Picture(8, 8, 2), ResourceOrigin.None, ResourceColorSpace.Linear, out _);
            var d = new PaintDocument(W, H, T) { ImageResources = r };
            var fill = d.AddFillLayer("Fill"); d.ClearHistory();
            var empty = d.Composite(PaintChannel.Metallic);
            d.SetFillImage(fill.Id, PaintChannel.Metallic, a.Id);
            Assert.That(fill.FillValues[PaintChannel.Metallic], Is.EqualTo(PaintDocument.DefaultFillImageFallback(PaintChannel.Metallic)), "a channel without a value gets the default fallback");
            Assert.That(fill.IsChannelEnabled(PaintChannel.Metallic), Is.True);
            var withA = d.Composite(PaintChannel.Metallic);
            d.SetFillImage(fill.Id, PaintChannel.Metallic, b.Id);
            var withB = d.Composite(PaintChannel.Metallic);
            Assert.That(withB, Is.Not.EqualTo(withA));
            Assert.That(d.UndoCount, Is.EqualTo(2));
            d.Undo(); Assert.That(d.Composite(PaintChannel.Metallic), Is.EqualTo(withA)); Assert.That(fill.FillImages[PaintChannel.Metallic], Is.EqualTo(a.Id));
            d.Undo(); Assert.That(fill.HasFillImage(PaintChannel.Metallic), Is.False); Assert.That(fill.FillValues.ContainsKey(PaintChannel.Metallic), Is.False, "the added fallback goes with it");
            Assert.That(d.Composite(PaintChannel.Metallic), Is.EqualTo(empty));
            d.Redo(); d.Redo(); Assert.That(d.Composite(PaintChannel.Metallic), Is.EqualTo(withB));
            // スライダーのドラッグ: 1 回にまとまり、取り消すと前に戻って履歴にも残らない
            int steps = d.UndoCount;
            foreach (double t in new[] { 1.2, 1.5, 2, 3 }) d.SetFillProjection(fill.Id, fill.Projection.WithTiles(t, t), coalesce: true);
            d.EndCoalescing();
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1)); Assert.That(fill.Projection.TileU, Is.EqualTo(3));
            var tiled = d.Composite(PaintChannel.Metallic);
            foreach (double o in new[] { .1, .2, .3 }) d.SetFillProjection(fill.Id, fill.Projection.WithOffset(o, 0), coalesce: true);
            Assert.That(d.CancelCoalescing(), Is.True);
            Assert.That(fill.Projection.OffsetU, Is.EqualTo(0)); Assert.That(d.UndoCount, Is.EqualTo(steps + 1)); Assert.That(d.Composite(PaintChannel.Metallic), Is.EqualTo(tiled));
            d.Undo(); Assert.That(fill.Projection, Is.EqualTo(FillProjection.Default)); Assert.That(d.Composite(PaintChannel.Metallic), Is.EqualTo(withB));
            // 値を消すと画像も（同じ 1 回）。Undo で両方戻る
            d.SetFillValue(fill.Id, PaintChannel.Metallic, null);
            Assert.That(fill.HasFillImage(PaintChannel.Metallic), Is.False);
            d.Undo(); Assert.That(fill.FillImages[PaintChannel.Metallic], Is.EqualTo(b.Id)); Assert.That(d.Composite(PaintChannel.Metallic), Is.EqualTo(withB));
            // 値を変えても画像のあるチャンネルの見た目は同じ（値は使えないときの代わり）
            d.SetFillValue(fill.Id, PaintChannel.Metallic, new Rgba32(1, 2, 3, 255));
            Assert.That(d.Composite(PaintChannel.Metallic), Is.EqualTo(withB));
        }

        [Test] public void WrongKindsMissingResourcesBudgetsAndLocksAreRefusedWithoutChangingAnything()
        {
            var (d, r, fill, image) = Scene();
            var raster = d.AddLayer("Paint"); d.ClearHistory(); long revision = d.Revision;
            Assert.That(() => d.SetFillImage(raster.Id, PaintChannel.Color, image.Id), Throws.InvalidOperationException);
            Assert.That(() => d.SetFillProjection(raster.Id, FillProjection.Default.WithTiles(2, 2)), Throws.InvalidOperationException);
            Assert.That(() => d.SetFillImage(fill.Id, PaintChannel.Color, Guid.NewGuid()), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.Unknown));
            Assert.That(() => d.SetFillImage(fill.Id, PaintChannel.Color, Guid.Empty), Throws.ArgumentException);
            Assert.That(() => d.SetFillImage(fill.Id, (PaintChannel)99, image.Id), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new FillProjection(FillProjectionMode.Uv, FillWrap.Repeat, 0, 1, 0, 0, 0, 0, FillProjection.DefaultPlacement), Throws.InstanceOf<ArgumentOutOfRangeException>(), "tiles 0");
            Assert.That(() => FillProjection.Default.WithTiles(double.NaN, 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => FillProjection.Default.WithRotation(400), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => FillProjection.Default.WithOffset(2e4, 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => FillProjection.Default.WithBlendWidth(1.5), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => FillProjection.Default.WithMode((FillProjectionMode)9), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => FillProjection.Default.WithPlacement(FillProjection.DefaultPlacement.WithSize(0, 1, 1)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            // 予算: 40×24 の画像のミップ（20×12 + 10×6 + 5×3 + 2×1 + 1×1）を持てない予算では断る
            var big = r.Add("Big", Picture(64, 64, 9), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            d.FillImageCacheBudgetBytes = 1000;
            var over = Assert.Throws<ResourceRefusedException>(() => d.SetFillImage(fill.Id, PaintChannel.Color, big.Id));
            Assert.That(over.Refusal, Is.EqualTo(ResourceRefusal.OverBudget)); Assert.That(fill.FillImages[PaintChannel.Color], Is.EqualTo(image.Id));
            // 後から下げた予算: 使えない理由を出して塗りつぶしの値
            d.FillImageCacheBudgetBytes = 100;
            var status = d.GetFillImageStatus(fill.Id, PaintChannel.Color);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("budget"));
            Assert.That(At(fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H), 1, 1), Is.EqualTo(Value));
            d.FillImageCacheBudgetBytes = PaintDocument.DefaultFillImageCacheBudgetBytes;
            Assert.That(d.GetFillImageStatus(fill.Id, PaintChannel.Color).Active, Is.True);
            // リソースがつながっていない文書
            var lone = new PaintDocument(W, H, T); var lonely = lone.AddFillLayer("F", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, Value } });
            Assert.That(() => lone.SetFillImage(lonely.Id, PaintChannel.Color, image.Id), Throws.InvalidOperationException.With.Message.Contains("No image resources"));
            // ロック: 画像ピクセル・透明ピクセル・すべて
            foreach (var locks in new[] { LayerLocks.Pixels, LayerLocks.Transparency, LayerLocks.All })
            {
                d.SetLayerLocks(fill.Id, locks);
                Assert.That(() => d.SetFillImage(fill.Id, PaintChannel.Color, null), Throws.TypeOf<LayerLockedException>(), locks + " image");
                Assert.That(() => d.SetFillProjection(fill.Id, fill.Projection.WithTiles(2, 2)), Throws.TypeOf<LayerLockedException>(), locks + " projection");
                d.SetLayerLocks(fill.Id, LayerLocks.None);
            }
            d.SetLayerLocks(fill.Id, LayerLocks.Position);
            d.SetFillProjection(fill.Id, fill.Projection.WithTiles(2, 2)); // 位置のロックは画素の中身を止めない
            Assert.That(fill.Projection.TileU, Is.EqualTo(2));
        }

        // ───────── 複製・大きさ・統合 ─────────

        [Test] public void DuplicateResizeAndMergeKeepOrRasterizeTheProjectedImage()
        {
            var (d, r, fill, image) = Scene();
            d.SetFillProjection(fill.Id, FillProjection.Default.WithTiles(2, 1.5).WithRotation(10));
            var composite = d.Composite(PaintChannel.Color);
            var copy = d.DuplicateLayer(fill.Id);
            Assert.That(copy.FillImages[PaintChannel.Color], Is.EqualTo(image.Id)); Assert.That(copy.Projection, Is.EqualTo(fill.Projection));
            Assert.That(copy.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H), Is.EqualTo(fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H)));
            d.Undo();
            // 大きさ: 画像と投影はそのまま（UV の空間で決まるので、新しい大きさで投影し直す）
            var resized = d.Resampled(W * 2, H * 2, CanvasResampling.Bilinear).Document;
            var big = resized.GetLayer(fill.Id);
            Assert.That(big.FillImages[PaintChannel.Color], Is.EqualTo(image.Id)); Assert.That(big.Projection, Is.EqualTo(fill.Projection));
            Assert.That(resized.ImageResources, Is.SameAs(r)); Assert.That(resized.GetFillImageStatus(fill.Id, PaintChannel.Color).Active, Is.True);
            // 統合: 下のペイントの層へ塗りつぶしを統合すると、見た目は同じ画素になる
            var paint = d.AddLayer("Under", above: null); d.MoveLayer(paint.Id, 0);
            var report = d.MergeDown(fill.Id);
            Assert.That(d.GetLayer(report.ResultId).Kind, Is.EqualTo(LayerKind.Raster));
            Assert.That(report.MaxVisibleDifference, Is.LessThanOrEqualTo(1), "the merged pixels are the projected image");
            var after = d.Composite(PaintChannel.Color);
            for (int i = 0; i < after.Length; i++) Assert.That(Math.Abs(after[i] - composite[i]), Is.LessThanOrEqualTo(1));
        }

        // ───────── 正本 ─────────

        static byte[] AtVersion(byte[] native, int version) { var bytes = (byte[])native.Clone(); BitConverter.GetBytes(version).CopyTo(bytes, 8); return bytes; }

        [Test] public void TheNativeArchiveRoundTripsImagesAndProjectionByteForByte()
        {
            var (d, r, fill, image) = Scene();
            var second = r.Add("Second", Picture(9, 9, 5), ResourceOrigin.None, ResourceColorSpace.Linear, out _);
            d.SetFillImage(fill.Id, PaintChannel.Height, second.Id);
            var p = new FillProjection(FillProjectionMode.Triplanar, FillWrap.Clamp, 2.5, .75, -.25, 3, -45, .6, new ShapeVolume(GeneratorShape.Box, .1, -.2, .3, 10, 20, -30, .5, 1.5, 2.5, 0));
            d.SetFillProjection(fill.Id, p);
            d.SetChannelEnabled(fill.Id, PaintChannel.Height, false);
            var other = d.AddFillLayer("Projection only", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, Value } });
            d.SetFillProjection(other.Id, FillProjection.Default.WithMode(FillProjectionMode.Planar)); // 画像が無くても投影は残す
            var bytes = DocumentBinary.Write(d);
            Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(DocumentBinary.CurrentVersion));
            var back = DocumentBinary.Read(bytes);
            Assert.That(DocumentBinary.Write(back), Is.EqualTo(bytes), "read and written back byte for byte");
            var f = back.GetLayer(fill.Id);
            Assert.That(f.FillImages.OrderBy(e => e.Key), Is.EqualTo(new[] { new KeyValuePair<PaintChannel, Guid>(PaintChannel.Color, image.Id), new KeyValuePair<PaintChannel, Guid>(PaintChannel.Height, second.Id) }));
            Assert.That(f.Projection, Is.EqualTo(p)); Assert.That(f.IsChannelEnabled(PaintChannel.Height), Is.False);
            Assert.That(back.GetLayer(other.Id).Projection.Mode, Is.EqualTo(FillProjectionMode.Planar)); Assert.That(back.GetLayer(other.Id).FillImages, Is.Empty);
            // 読んだ文書はリソースを知らない: 理由を出して塗りつぶしの値。つなげば画像
            Assert.That(back.GetFillImageStatus(fill.Id, PaintChannel.Color).Active, Is.False);
            back.ImageResources = r;
            back.SetFillProjection(fill.Id, FillProjection.Default.WithTiles(1.5, 2)); d.SetFillProjection(fill.Id, FillProjection.Default.WithTiles(1.5, 2)); // UV（マップの要らない投影）で比べる
            Assert.That(back.GetFillImageStatus(fill.Id, PaintChannel.Color).Active, Is.True);
            Assert.That(back.GetLayer(fill.Id).EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H), Is.EqualTo(fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H)), "the same pixels as before saving");
        }

        [Test] public void ADocumentWithoutFillImagesIsLaidOutAsVersion15AndOlderOrUnknownLayoutsAreRefused()
        {
            var d = new PaintDocument(W, H, T);
            d.AddFillLayer("Plain fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, Value } }); d.AddLayer("Paint");
            var plain = DocumentBinary.Write(d);
            Assert.That(BitConverter.ToInt32(plain, 8), Is.EqualTo(DocumentBinary.CurrentVersion));
            foreach (int older in new[] { 15, 14, 13 })
                Assert.That(DocumentBinary.Write(DocumentBinary.Read(AtVersion(plain, older))), Is.EqualTo(plain), "version " + older + " bytes are read as they are and written back as the current version (only the number differs)");
            Assert.That(() => DocumentBinary.Read(AtVersion(plain, DocumentBinary.CurrentVersion + 1)), Throws.TypeOf<InvalidDataException>());
            var (s, r, fill, image) = Scene();
            var bytes = DocumentBinary.Write(s);
            foreach (int older in new[] { 15, 14 })
                Assert.That(() => DocumentBinary.Read(AtVersion(bytes, older)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("attribute flags"), "version " + older + " has no bit 3");
            // 壊した塊: 種類・モード・繰り返し・版・範囲・重なり・値の無いチャンネル・空の ID・途中で切れたもの
            int block = Find(bytes, image.Id.ToByteArray()) - 8; // 数（int）と最初のチャンネル（int）の位置
            Assert.That(BitConverter.ToInt32(bytes, block), Is.EqualTo(1)); Assert.That(BitConverter.ToInt32(bytes, block + 4), Is.EqualTo((int)PaintChannel.Color));
            int projection = block + 8 + 16;
            void Refused(string why, Action<byte[]> tamper) { var b = (byte[])bytes.Clone(); tamper(b); Assert.That(() => DocumentBinary.Read(b), Throws.TypeOf<InvalidDataException>(), why); }
            Refused("unknown algorithm version", b => BitConverter.GetBytes(2).CopyTo(b, projection));
            Refused("unknown mode", b => BitConverter.GetBytes(6).CopyTo(b, projection + 4)); // 5 はデカール（版 17）
            Refused("unknown wrap", b => BitConverter.GetBytes(3).CopyTo(b, projection + 8)); // 2 は画像の外を透明に（版 17）
            Refused("tiles 0", b => BitConverter.GetBytes(0.0).CopyTo(b, projection + 12));
            Refused("NaN rotation", b => BitConverter.GetBytes(double.NaN).CopyTo(b, projection + 12 + 4 * 8));
            Refused("placement size 0", b => BitConverter.GetBytes(0.0).CopyTo(b, projection + 12 + 12 * 8));
            Refused("a channel without a fill value", b => BitConverter.GetBytes((int)PaintChannel.Height).CopyTo(b, block + 4));
            Refused("an unknown channel", b => BitConverter.GetBytes(9).CopyTo(b, block + 4));
            Refused("an empty resource ID", b => Array.Clear(b, block + 8, 16));
            Refused("too many images", b => BitConverter.GetBytes(7).CopyTo(b, block));
            Assert.That(() => DocumentBinary.Read(bytes.Take(projection + 20).ToArray()), Throws.TypeOf<InvalidDataException>(), "truncated");
            // 画像の印をペイントの層に付けたもの
            var withPaint = new PaintDocument(W, H, T); withPaint.AddLayer("Paint"); var paintBytes = DocumentBinary.Write(withPaint);
            int attributes = Find(paintBytes, System.Text.Encoding.UTF8.GetBytes("Paint")) + 5 + 1 + 8 + 4; // 名前・表示・不透明度・合成モードの後
            Assert.That(paintBytes[attributes], Is.EqualTo(0));
            paintBytes[attributes] = 8; // 塗りつぶしの画像の印
            Assert.That(() => DocumentBinary.Read(paintBytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Only fill layers"));
        }
        static int Find(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++) { int k = 0; while (k < needle.Length && haystack[i + k] == needle[k]) k++; if (k == needle.Length) return i; }
            throw new AssertionException("not found");
        }

        // ───────── PSD ─────────

        [Test] public void PsdExportWritesTheProjectedPixelsAsARasterLayerAndSaysWhatIsNotCarried()
        {
            var (d, r, fill, image) = Scene(picture: Picture(W, H, 6));
            d.SetFillProjection(fill.Id, FillProjection.Default.WithTiles(2, 2));
            var expected = fill.EvaluateOutputRegion(PaintChannel.Color, 0, 0, W, H);
            Assert.That(() => PsdBridge.Export(d, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("'Fill' (Color)"), "the export without notes refuses instead of dropping silently");
            var notes = new List<Yozolab.YoluPainter.Core.Psd.PsdDiagnostic>();
            var psd = PsdBridge.Export(d, PaintChannel.Color, notes);
            Assert.That(notes.Single().Code, Is.EqualTo(Yozolab.YoluPainter.Core.Psd.PsdCodec.NotCarriedIntoExport));
            Assert.That(notes.Single().Message, Does.Contain("Picture").And.Contain("Uv"));
            var layer = psd.Layers.Single();
            Assert.That(layer.IsFill, Is.False); Assert.That((layer.Width, layer.Height), Is.EqualTo((W, H)));
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                int o = ((H - 1 - y) * W + x) * 4; // PSD は上から
                Assert.That(new Rgba32(layer.PixelsRgba[o], layer.PixelsRgba[o + 1], layer.PixelsRgba[o + 2], layer.PixelsRgba[o + 3]), Is.EqualTo(At(expected, x, y)));
            }
            var read = Yozolab.YoluPainter.Core.Psd.PsdCodec.Read(Yozolab.YoluPainter.Core.Psd.PsdCodec.Write(psd));
            var imported = PsdBridge.Import(read);
            Assert.That(imported.Layers.Single().Kind, Is.EqualTo(LayerKind.Raster), "the PSD comes back as a paint layer");
            Assert.That(imported.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            // ほかのチャンネル（値だけ）は今までどおり単色の塗りつぶしで、知らせは無い
            d.SetFillValue(fill.Id, PaintChannel.Roughness, new Rgba32(90, 90, 90, 255));
            notes.Clear();
            var rough = PsdBridge.Export(d, PaintChannel.Roughness, notes);
            Assert.That(notes, Is.Empty); Assert.That(rough.Layers.Single().IsFill, Is.True);
        }
    }
}
