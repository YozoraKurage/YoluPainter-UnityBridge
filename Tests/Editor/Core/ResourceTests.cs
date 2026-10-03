using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Shelf;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// プロジェクトのリソース（Core の Shelf）: 中身のハッシュ（画素と大きさの SHA-256、固定の値で確かめる）、PNG の往復（透明画素の RGB・
    /// 決定性・Unity の復号器との一致・ほかの書き手の PNG）と壊れた PNG の拒否、予算・数・大きさ・使用中の拒否（何も変えない）、同じ中身の
    /// まとめ、中身の差し替えと通知の順、画像の再標本化がテクスチャセットの大きさの変更と同じバイト、内蔵の画像のハッシュ。
    /// </summary>
    public sealed class ResourceTests
    {
        static byte[] Random(int w, int h, int seed, bool transparentRgb = true)
        {
            var rgba = new byte[w * h * 4]; new System.Random(seed).NextBytes(rgba);
            if (transparentRgb) for (int i = 3; i < rgba.Length; i += 16) rgba[i] = 0; // 透明画素（RGB は残っている）
            return rgba;
        }
        static ImageContent Image(int w, int h, int seed) => ImageContent.FromPixels(Random(w, h, seed), w, h);

        // ───────── 中身とハッシュ ─────────

        [Test] public void TheContentHashIsOfThePixelsAndTheSize()
        {
            Assert.That(ImageContent.ComputeHash(new byte[] { 1, 2, 3, 4 }, 1, 1), Is.EqualTo("3fcb40264043a21cd06a5493244d8c2f8cf81615dee6e76530808e5237f46393"), "SHA-256 of \"YLPRGBA8\", width, height (LE) and the pixels");
            var eight = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            Assert.That(ImageContent.ComputeHash(eight, 2, 1), Is.EqualTo("ccb356db910387c805149d4132a1134743ff04da9ed37e286ab9af8b9408b0ba"));
            Assert.That(ImageContent.ComputeHash(eight, 1, 2), Is.EqualTo("11c869da794d1f61bf40498e59f6cca9fc1699dec447d5dee157c718a77c3e49"), "the same bytes in another shape are another image");
            var pixels = Random(7, 5, 1);
            var a = ImageContent.FromPixels(pixels, 7, 5);
            pixels[0] ^= 1;
            Assert.That(a.CopyPixels()[0], Is.Not.EqualTo(pixels[0]), "FromPixels copies");
            Assert.That(ImageContent.FromPixels(pixels, 7, 5).Hash, Is.Not.EqualTo(a.Hash), "one bit changes the hash");
            Assert.That(ImageContent.IsHash(a.Hash), Is.True);
            Assert.That(a.GetPixel(6, 4), Is.EqualTo(new Rgba32(a.CopyPixels()[(4 * 7 + 6) * 4], a.CopyPixels()[(4 * 7 + 6) * 4 + 1], a.CopyPixels()[(4 * 7 + 6) * 4 + 2], a.CopyPixels()[(4 * 7 + 6) * 4 + 3])));
            Assert.That(() => ImageContent.FromPixels(new byte[8193 * 4], 8193, 1), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.TooLarge));
            Assert.That(() => ImageContent.FromPixels(new byte[0], 0, 1), Throws.TypeOf<ResourceRefusedException>());
            Assert.That(() => ImageContent.FromPixels(new byte[12], 2, 2), Throws.ArgumentException);
        }

        // ───────── PNG ─────────

        [Test] public void PngRoundTripsExactlyAndTheSamePixelsMakeTheSameBytes()
        {
            foreach (var (w, h) in new[] { (1, 1), (3, 2), (17, 9), (128, 64) })
            {
                var rgba = Random(w, h, w * 31 + h);
                var png = RgbaPng.Encode(rgba, w, h);
                Assert.That(RgbaPng.LooksLikePng(png), Is.True);
                Assert.That(RgbaPng.ReadSize(png), Is.EqualTo((w, h)));
                var (back, bw, bh) = RgbaPng.Decode(png);
                Assert.That((bw, bh), Is.EqualTo((w, h)));
                Assert.That(back, Is.EqualTo(rgba), w + "×" + h + ": every byte, the RGB under zero alpha included");
                Assert.That(RgbaPng.Encode(rgba, w, h), Is.EqualTo(png), "deterministic");
            }
            // 滑らかな画像は縮む（行ごとのフィルターが効いている）
            var smooth = new byte[256 * 256 * 4];
            for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++) { int o = (y * 256 + x) * 4; smooth[o] = (byte)x; smooth[o + 1] = (byte)y; smooth[o + 2] = (byte)(x ^ y); smooth[o + 3] = 255; }
            Assert.That(RgbaPng.Encode(smooth, 256, 256).Length, Is.LessThan(smooth.Length / 4));
        }

        /// <summary>Unity の復号器（libpng）が同じ値に読み、Unity の書いた PNG（RGBA・RGB）をこちらが同じ値に読む（ほかの実装との突き合わせ）。</summary>
        [Test] public void PngAgreesWithUnitysCodec()
        {
            const int w = 33, h = 21;
            var rgba = Random(w, h, 5);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var rgb = new Texture2D(w, h, TextureFormat.RGB24, false, true);
            try
            {
                Assert.That(texture.LoadImage(RgbaPng.Encode(rgba, w, h), false), Is.True);
                Assert.That((texture.width, texture.height), Is.EqualTo((w, h)));
                Assert.That(Bytes(texture.GetPixels32()), Is.EqualTo(rgba), "Unity reads our PNG to the same values, bottom row first");
                var unityPng = texture.EncodeToPNG();
                Assert.That(RgbaPng.Decode(unityPng).rgba, Is.EqualTo(rgba), "we read Unity's RGBA PNG");
                var colors = new Color32[w * h]; for (int i = 0; i < colors.Length; i++) colors[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], 255);
                rgb.SetPixels32(colors); rgb.Apply();
                Assert.That(RgbaPng.Decode(rgb.EncodeToPNG()).rgba, Is.EqualTo(Bytes(colors)), "and an RGB one (alpha 255)");
            }
            finally { Object.DestroyImmediate(texture); Object.DestroyImmediate(rgb); }
        }
        static byte[] Bytes(Color32[] colors)
        {
            var b = new byte[colors.Length * 4];
            for (int i = 0; i < colors.Length; i++) { b[i * 4] = colors[i].r; b[i * 4 + 1] = colors[i].g; b[i * 4 + 2] = colors[i].b; b[i * 4 + 3] = colors[i].a; }
            return b;
        }

        [Test] public void BrokenOrUnsupportedPngIsRefusedWithAReason()
        {
            var good = RgbaPng.Encode(Random(4, 3, 9), 4, 3);
            void Refused(byte[] bytes, string reason, string what) => Assert.That(() => RgbaPng.Decode(bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains(reason), what);
            Refused(new byte[] { 1, 2, 3 }, "signature", "not a PNG");
            var crc = (byte[])good.Clone(); crc[20] ^= 0xff; Refused(crc, "CRC", "a changed IHDR byte");
            Refused(good.Take(good.Length - 20).ToArray(), "", "truncated");
            Refused(Chunked(Ihdr(9000, 1, 8, 6, 0)), "8192", "too large: refused before inflating");
            Refused(Chunked(Ihdr(4, 4, 16, 6, 0)), "8-bit", "16-bit");
            Refused(Chunked(Ihdr(4, 4, 8, 3, 0)), "palette", "palette");
            Refused(Chunked(Ihdr(4, 4, 8, 6, 1)), "Interlaced", "interlaced");
            Refused(Chunked(Ihdr(1, 1, 8, 6, 0), ("ABCD", new byte[0])), "critical chunk ABCD", "an unknown critical chunk");
            // 頭は 1×1 なのに、展開すると多い（爆弾は宣言の大きさで止める）
            Refused(Chunked(Ihdr(1, 1, 8, 6, 0), ("IDAT", Zlib(new byte[1000]))), "longer than", "more data than the header promises");
            Refused(Chunked(Ihdr(2, 2, 8, 6, 0), ("IDAT", Zlib(new byte[5]))), "shorter than", "less data");
            var zlib = Zlib(new byte[5]); zlib[zlib.Length - 1] ^= 1;
            Refused(Chunked(Ihdr(1, 1, 8, 6, 0), ("IDAT", zlib)), "Adler-32", "a wrong checksum");
            // 補助のチャンク（tEXt など）は読み飛ばし、値はそのまま
            var withText = Chunked(Ihdr(1, 1, 8, 6, 0), ("tEXt", Encoding.ASCII.GetBytes("Comment\0hi")), ("IDAT", Zlib(new byte[] { 0, 9, 8, 7, 0 })));
            Assert.That(RgbaPng.Decode(withText).rgba, Is.EqualTo(new byte[] { 9, 8, 7, 0 }), "the RGB of a transparent pixel is read as stored");
            // 灰色・灰色とアルファ・RGB
            Assert.That(RgbaPng.Decode(Chunked(Ihdr(2, 1, 8, 0, 0), ("IDAT", Zlib(new byte[] { 0, 5, 6 })))).rgba, Is.EqualTo(new byte[] { 5, 5, 5, 255, 6, 6, 6, 255 }));
            Assert.That(RgbaPng.Decode(Chunked(Ihdr(1, 1, 8, 4, 0), ("IDAT", Zlib(new byte[] { 0, 5, 0 })))).rgba, Is.EqualTo(new byte[] { 5, 5, 5, 0 }));
            Assert.That(RgbaPng.Decode(Chunked(Ihdr(1, 2, 8, 2, 0), ("IDAT", Zlib(new byte[] { 0, 1, 2, 3, 0, 4, 5, 6 })))).rgba, Is.EqualTo(new byte[] { 4, 5, 6, 255, 1, 2, 3, 255 }), "the file is top-down, memory bottom-up");
        }

        static (string, byte[]) Ihdr(int w, int h, byte depth, byte colour, byte interlace)
        {
            var b = new byte[13]; Be(b, 0, (uint)w); Be(b, 4, (uint)h); b[8] = depth; b[9] = colour; b[12] = interlace; return ("IHDR", b);
        }
        static void Be(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
        /// <summary>CRC の正しいチャンクを並べた PNG（最後に IEND）。</summary>
        static byte[] Chunked(params (string type, byte[] data)[] chunks)
        {
            var s = new MemoryStream(); s.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            foreach (var (type, data) in chunks.Concat(new[] { ("IEND", new byte[0]) }))
            {
                var head = new byte[8]; Be(head, 0, (uint)data.Length); Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
                s.Write(head, 0, 8); s.Write(data, 0, data.Length);
                var crcInput = head.Skip(4).Concat(data).ToArray(); var tail = new byte[4]; Be(tail, 0, Crc(crcInput)); s.Write(tail, 0, 4);
            }
            return s.ToArray();
        }
        static uint Crc(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1; }
            return ~crc;
        }
        static byte[] Zlib(byte[] raw)
        {
            var s = new MemoryStream(); s.WriteByte(0x78); s.WriteByte(0x9C);
            using (var d = new System.IO.Compression.DeflateStream(s, System.IO.Compression.CompressionLevel.Optimal, true)) d.Write(raw, 0, raw.Length);
            uint a = 1, b = 0; foreach (byte x in raw) { a = (a + x) % 65521; b = (b + a) % 65521; }
            var tail = new byte[4]; Be(tail, 0, b << 16 | a); s.Write(tail, 0, 4);
            return s.ToArray();
        }

        [Test] public void AResourcePngIsCheckedAgainstItsName()
        {
            var content = Image(6, 4, 3);
            var png = content.EncodePng();
            Assert.That(content.EncodePng(), Is.SameAs(png), "made once");
            var back = ImageContent.FromPng(png, content.Hash);
            Assert.That(back.Hash, Is.EqualTo(content.Hash)); Assert.That(back.EncodePng(), Is.SameAs(png), "the bytes read are the bytes written back");
            var other = Image(6, 4, 4);
            Assert.That(() => ImageContent.FromPng(other.EncodePng(), content.Hash), Throws.TypeOf<InvalidDataException>().With.Message.Contains("do not match"));
        }

        // ───────── プロジェクトのリソース ─────────

        [Test] public void EqualContentIsKeptOnceAndCountedOnce()
        {
            var resources = new ProjectResources();
            var changes = new List<ResourceChange>(); resources.Changed += changes.Add;
            var a = Image(16, 8, 1);
            var first = resources.Add("Scratches", a, ResourceOrigin.None, ResourceColorSpace.Srgb, out bool added);
            Assert.That(added, Is.True); Assert.That(resources.UsedBytes, Is.EqualTo(16 * 8 * 4));
            var again = resources.Add("Scratches copy", ImageContent.FromPixels(a.CopyPixels(), 16, 8), ResourceOrigin.BuiltIn("grid", 1), ResourceColorSpace.Linear, out added);
            Assert.That(added, Is.False); Assert.That(again, Is.SameAs(first), "equal pixels are the same resource");
            Assert.That(first.Name, Is.EqualTo("Scratches")); Assert.That(first.Origin.Kind, Is.EqualTo(ResourceOriginKind.None), "the first one's name and origin stay");
            Assert.That(resources.Count, Is.EqualTo(1)); Assert.That(resources.UsedBytes, Is.EqualTo(16 * 8 * 4));
            Assert.That(changes.Select(c => c.Kind), Is.EqualTo(new[] { ResourceChangeKind.Added }), "adding equal content changes nothing");
            Assert.That(resources.TryGetImage(first.Id, out var found) && found == first, Is.True);
            Assert.That(resources.FindByHash(a.Hash), Is.SameAs(first));
            Assert.That(() => resources.Get(Guid.NewGuid()), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.Unknown));
            Assert.That(() => resources.Add(" ", Image(1, 1, 2), null, ResourceColorSpace.Srgb, out _), Throws.ArgumentException, "names are checked");
        }

        [Test] public void BudgetAndCountRefusalsChangeNothing()
        {
            var resources = new ProjectResources { BudgetBytes = 1000 };
            var changes = new List<ResourceChange>(); resources.Changed += changes.Add;
            var small = resources.Add("small", Image(10, 10, 1), null, ResourceColorSpace.Srgb, out _); // 400 bytes
            long revision = resources.Revision;
            var big = Image(16, 10, 2); // 640 bytes: 400 + 640 > 1000
            Assert.That(resources.AddRefusal(big, out var why), Does.Contain("budget")); Assert.That(why, Is.EqualTo(ResourceRefusal.OverBudget));
            Assert.That(() => resources.Add("big", big, null, ResourceColorSpace.Srgb, out _), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.OverBudget));
            Assert.That((resources.Count, resources.UsedBytes, resources.Revision, changes.Count), Is.EqualTo((1, 400L, revision, 1)), "a refused add changes nothing");
            Assert.That(() => resources.ReplaceContent(small.Id, Image(20, 20, 3), null), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.OverBudget), "1600 bytes even after freeing 400");
            Assert.That(small.Revision, Is.Zero); Assert.That(resources.UsedBytes, Is.EqualTo(400));
            resources.ReplaceContent(small.Id, Image(15, 15, 4), null); // 900: fits once the old 400 are freed
            Assert.That(resources.UsedBytes, Is.EqualTo(900)); Assert.That(small.Revision, Is.EqualTo(1));
            resources.BudgetBytes = 100;
            Assert.That(resources.Count, Is.EqualTo(1), "lowering the budget keeps what is there");

            var many = new ProjectResources { BudgetBytes = long.MaxValue };
            for (int i = 0; i < ProjectResources.MaxResources; i++) many.Add("r" + i, ImageContent.FromPixels(BitConverter.GetBytes(i), 1, 1), null, ResourceColorSpace.Srgb, out _);
            Assert.That(() => many.Add("one more", ImageContent.FromPixels(BitConverter.GetBytes(-1), 1, 1), null, ResourceColorSpace.Srgb, out _),
                Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.TooMany));
            Assert.That(many.Add("dup", ImageContent.FromPixels(BitConverter.GetBytes(7), 1, 1), null, ResourceColorSpace.Srgb, out bool added), Is.SameAs(many.Images[7]), "equal content is still found at the limit");
            Assert.That(added, Is.False);
        }

        [Test] public void RemovingAsksTheUsageProbesAndReplacingKeepsTheId()
        {
            var resources = new ProjectResources();
            var changes = new List<ResourceChange>(); resources.Changed += changes.Add;
            var a = resources.Add("A", Image(4, 4, 1), null, ResourceColorSpace.Srgb, out _);
            var b = resources.Add("B", Image(4, 4, 2), null, ResourceColorSpace.Srgb, out _);
            Func<Guid, string> probe = id => id == a.Id ? "layer \"Decal\"" : null;
            resources.AddUsageProbe(probe);
            Assert.That(() => resources.Remove(a.Id), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.InUse).And.Message.Contains("Decal"));
            Assert.That(resources.Count, Is.EqualTo(2));
            // 中身を差し替えても ID は同じ。同じ中身になった 2 つの資源は画素を分け合う
            var bPixels = b.Content;
            resources.ReplaceContent(a.Id, ImageContent.FromPixels(bPixels.CopyPixels(), 4, 4), ResourceOrigin.BuiltIn("grid", 2));
            Assert.That(a.Content, Is.SameAs(b.Content), "shared, not a second copy"); Assert.That(resources.UsedBytes, Is.EqualTo(64));
            Assert.That(a.Origin.BuiltInVersion, Is.EqualTo(2)); Assert.That(a.Revision, Is.EqualTo(1));
            resources.Rename(a.Id, "A2"); resources.SetColorSpace(a.Id, ResourceColorSpace.Linear);
            resources.RemoveUsageProbe(probe);
            resources.Remove(a.Id);
            Assert.That(resources.UsedBytes, Is.EqualTo(64), "B still holds the shared pixels");
            resources.Remove(b.Id);
            Assert.That(resources.UsedBytes, Is.Zero);
            Assert.That(changes.Select(c => c.Kind), Is.EqualTo(new[] { ResourceChangeKind.Added, ResourceChangeKind.Added, ResourceChangeKind.ContentReplaced, ResourceChangeKind.Renamed, ResourceChangeKind.ColorSpaceChanged, ResourceChangeKind.Removed, ResourceChangeKind.Removed }));
            Assert.That(changes.Where(c => c.Kind != ResourceChangeKind.Added).Select(c => c.Id).Take(4), Is.All.EqualTo(a.Id));
            Assert.That(resources.Revision, Is.EqualTo(changes.Count), "every change bumps the revision once");

            var other = new ProjectResources(); other.Add("X", Image(2, 2, 5), null, ResourceColorSpace.Srgb, out _);
            resources.ResetTo(other);
            Assert.That(resources.Count, Is.EqualTo(1)); Assert.That(other.Count, Is.Zero); Assert.That(changes.Last().Kind, Is.EqualTo(ResourceChangeKind.Reset));
            resources.Clear(); Assert.That((resources.Count, resources.UsedBytes), Is.EqualTo((0, 0L)));
        }

        // ───────── 再標本化 ─────────

        /// <summary>画像の再標本化は、同じ画像を層に持つ文書の大きさを変えたとき（PaintDocument.Resampled）と同じバイト。</summary>
        [Test] public void ResamplingMatchesTheTextureSetResize([Values(CanvasResampling.Nearest, CanvasResampling.Bilinear, CanvasResampling.Area)] CanvasResampling resampling)
        {
            const int w = 150, h = 90;
            var rgba = Random(w, h, 11);
            for (int i = 0; i < 300; i++) { rgba[i * 4] = 10; rgba[i * 4 + 1] = 20; rgba[i * 4 + 2] = 30; rgba[i * 4 + 3] = 0; } // 透明の帯
            var d = new PaintDocument(w, h);
            var layer = d.AddLayer("L"); d.SetChannelEnabled(layer.Id, PaintChannel.Color, true); d.ReplacePixels(layer.Id, PaintChannel.Color, rgba, withinSelection: false);
            foreach (var (tw, th) in new[] { (300, 180), (61, 37), (150, 45), (512, 64) })
            {
                var copy = d.Resampled(tw, th, resampling).Document; var target = copy.Layers.Single();
                var raw = new byte[tw * th * 4];
                Assert.That(target.TryGetChannel(PaintChannel.Color, out var surface), Is.True);
                foreach (var coord in surface.EnumerateTileCoordinates())
                {
                    var tile = new byte[copy.TileSize * copy.TileSize * 4];
                    if (!surface.CopyTile(coord, tile)) continue;
                    int cw = Math.Min(copy.TileSize, tw - coord.X * copy.TileSize), ch = Math.Min(copy.TileSize, th - coord.Y * copy.TileSize);
                    for (int y = 0; y < ch; y++) Buffer.BlockCopy(tile, y * copy.TileSize * 4, raw, ((coord.Y * copy.TileSize + y) * tw + coord.X * copy.TileSize) * 4, cw * 4);
                }
                var mine = ImageResampling.Resample(rgba, w, h, tw, th, resampling);
                Assert.That(mine, Is.EqualTo(raw), resampling + " " + tw + "×" + th + ": the layer's own pixels, transparent RGB included");
            }
            Assert.That(ImageResampling.Resample(rgba, w, h, w, h, resampling), Is.EqualTo(rgba));
            Assert.That(ImageResampling.Automatic(10, 10, 20, 20), Is.EqualTo(CanvasResampling.Bilinear));
            Assert.That(ImageResampling.Automatic(10, 10, 20, 5), Is.EqualTo(CanvasResampling.Area));
        }

        [Test] public void ResamplingDoesNotDependOnTheThreadCount()
        {
            var rgba = Random(301, 77, 3);
            byte[] first = null;
            foreach (int threads in new[] { 1, 2, 3, 0 })
            {
                using (new CoreThreadsScope(threads))
                {
                    var r = ImageResampling.Resample(rgba, 301, 77, 1024, 300, CanvasResampling.Bilinear);
                    if (first == null) first = r; else Assert.That(r, Is.EqualTo(first), threads + " threads");
                }
            }
        }

        sealed class CoreThreadsScope : IDisposable
        {
            readonly int previous;
            public CoreThreadsScope(int threads) { previous = CoreParallelism.MaxDegreeOfParallelism; CoreParallelism.MaxDegreeOfParallelism = threads; }
            public void Dispose() => CoreParallelism.MaxDegreeOfParallelism = previous;
        }

        // ───────── 内蔵 ─────────

        /// <summary>内蔵の画像は整数の計算だけで作り、同じ鍵と版はいつも同じ画素（ハッシュを固定して確かめる。画素を変えたら版を上げる）。</summary>
        [Test] public void BuiltInImagesAreAlwaysTheSamePixels()
        {
            var pinned = new Dictionary<string, string>
            {
                { "uv-checker", "b144d286da48901dcc8ae19f756c6d805bfd51d6baecc37b1b6228450e5e3135" },
                { "grid", "695b96a0bb2f1175b7b39b7c859192a13ea55c771b331f15da64025a5a7a5bf3" },
                { "linear-gradient", "4bfa97c27611dcfaa2753638e8ddfaec47384e8ff80bcc1f7293c13d29b55763" },
                { "radial-gradient", "71f96a29003ad60d093c5d19f809e32afeb429ad2a1028550382437851a7e9b8" },
                { "value-noise", "10746b22b1b9a4b85fcc484d5507251a4cc644eb5b2c87b3673a49706931fe48" },
            };
            var report = new StringBuilder();
            foreach (var entry in BuiltInImages.All)
            {
                var content = BuiltInImages.Make(entry.Key);
                Assert.That((content.Width, content.Height), Is.EqualTo((entry.Width, entry.Height)));
                Assert.That(BuiltInImages.Make(entry.Key).Hash, Is.EqualTo(content.Hash), entry.Key + " twice");
                report.Append(entry.Key).Append(' ').Append(content.Hash).Append('\n');
                Assert.That(pinned.ContainsKey(entry.Key), Is.True, entry.Key + " has no pinned hash");
            }
            Assert.That(BuiltInImages.All.Select(e => e.Key).Distinct().Count(), Is.EqualTo(BuiltInImages.All.Count));
            var mismatches = BuiltInImages.All.Where(e => pinned[e.Key] != BuiltInImages.Make(e.Key).Hash).Select(e => e.Key).ToList();
            Assert.That(mismatches, Is.Empty, "built-in pixels changed (raise the version):\n" + report);
            var grid = BuiltInImages.Make("grid");
            Assert.That(grid.GetPixel(0, 0), Is.EqualTo(new Rgba32(255, 255, 255, 255))); Assert.That(grid.GetPixel(10, 10), Is.EqualTo(new Rgba32(255, 255, 255, 0)), "transparent with white RGB");
            Assert.That(BuiltInImages.Make("linear-gradient").GetPixel(0, 5).R, Is.Zero); Assert.That(BuiltInImages.Make("linear-gradient").GetPixel(1023, 5).R, Is.EqualTo(255));
            Assert.That(() => BuiltInImages.Make("nope"), Throws.TypeOf<ResourceRefusedException>());
            Assert.That(BuiltInImages.IsKey("uv-checker"), Is.True); Assert.That(BuiltInImages.IsKey("UV"), Is.False);
        }
    }
}
