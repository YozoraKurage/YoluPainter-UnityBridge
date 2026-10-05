using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.LiveLink;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// Live Link の元の絵: Color の流し込み先に入っている元のテクスチャを、原本のファイル（PNG・TGA・JPG。圧縮の無い本当の値）・Unity が取り込んだ絵
    /// （PSD など、またはインポートが絵を変える設定）・GPU を通して（アセットでない絵）から読み、元のテクスチャ・インポート設定・マテリアルを変えずに、
    /// 元の絵の印を持つスタンドアロンへだけ送る。スタンドアロンが元の絵を入れたセットを返すまで、表示を置き換えない。試験のアセットは試験が作った
    /// フォルダの中だけで、終わりに消す。
    /// </summary>
    public sealed class LiveLinkOriginalsTests
    {
        static int s_counter;
        readonly List<Object> owned = new List<Object>();
        readonly List<string> folders = new List<string>();
        ulong server;
        LiveLinkSession session;

        [SetUp]
        public void Require()
        {
            Assert.That(LiveLinkBridge.Problem, Is.Null, "the bridge library must load on the Linux and Windows editors");
            L.OverrideLanguage(PainterLanguage.English);
        }

        [TearDown]
        public void CleanUp()
        {
            session?.Dispose(); session = null;
            LiveLinkTestServer.Stop(server); server = 0;
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
            foreach (var f in folders) AssetDatabase.DeleteAsset(f);
            folders.Clear();
            L.OverrideLanguage(null);
        }

        T Own<T>(T o) where T : Object { owned.Add(o); return o; }

        static string UniqueName() => "ylp-unity-originals-" + Process.GetCurrentProcess().Id + "-" + (++s_counter);

        static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("needs a graphics device (the textures are read through a RenderTexture)");
            if (EditorShaderCompiler.IsBroken) Assert.Ignore("Built-in shaders fail to compile in this Editor (devcontainer GUI mode). Use test-daemon.sh start --batch-gl.");
        }

        void Pump(Func<bool> done, string what, double seconds = 15)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.Elapsed.TotalSeconds > seconds) Assert.Fail("timed out waiting for " + what + " (" + session?.StatusText + ")");
                session?.Tick();
                Thread.Sleep(5);
            }
        }

        string NewFolder()
        {
            string folder = "Assets/YoluPainterOriginals-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder)), Is.Not.Empty);
            folders.Add(folder);
            return folder;
        }

        /// <summary>画素 (x, y)（y は下から）が [x * 50, y * 80, (x + y) * 20, 255]、(1, 1) は RGB を持つ透明な画素の、4 × 3 の絵。</summary>
        static byte[] Picture(int w = 4, int h = 3)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bool clear = x == 1 && y == 1;
                    rgba[o] = clear ? (byte)9 : (byte)(x * 50); rgba[o + 1] = clear ? (byte)8 : (byte)(y * 80); rgba[o + 2] = clear ? (byte)7 : (byte)((x + y) * 20); rgba[o + 3] = clear ? (byte)0 : (byte)255;
                }
            return rgba;
        }

        /// <summary>TGA を組む（真色の 24 ビットか 32 ビット。RLE か無圧縮。<paramref name="topFirst"/> は、ファイルの行が上からか）。</summary>
        static byte[] Tga(byte[] rgba, int w, int h, int depth, bool rle, bool topFirst)
        {
            var header = new byte[18];
            header[2] = (byte)(rle ? 10 : 2); header[12] = (byte)w; header[13] = (byte)(w >> 8); header[14] = (byte)h; header[15] = (byte)(h >> 8);
            header[16] = (byte)depth; header[17] = (byte)((depth == 32 ? 8 : 0) | (topFirst ? 0x20 : 0));
            int bpp = depth / 8;
            var pixels = new List<byte[]>();
            for (int fileRow = 0; fileRow < h; fileRow++)
            {
                int y = topFirst ? h - 1 - fileRow : fileRow;
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    pixels.Add(bpp == 4 ? new[] { rgba[o + 2], rgba[o + 1], rgba[o], rgba[o + 3] } : new[] { rgba[o + 2], rgba[o + 1], rgba[o] });
                }
            }
            var body = new List<byte>();
            if (!rle) foreach (var p in pixels) body.AddRange(p);
            else
            {
                int i = 0;
                while (i < pixels.Count)
                {
                    int run = 1;
                    while (i + run < pixels.Count && run < 128 && pixels[i + run].SequenceEqual(pixels[i])) run++;
                    if (run >= 2) { body.Add((byte)(0x80 | (run - 1))); body.AddRange(pixels[i]); i += run; continue; }
                    int raw = 1;
                    while (i + raw < pixels.Count && raw < 128 && (i + raw + 1 >= pixels.Count || !pixels[i + raw].SequenceEqual(pixels[i + raw + 1]))) raw++;
                    body.Add((byte)(raw - 1));
                    for (int k = 0; k < raw; k++) body.AddRange(pixels[i + k]);
                    i += raw;
                }
            }
            return header.Concat(body).ToArray();
        }

        /// <summary>8 ビットの RGBA の PSD（結合した絵だけ。無圧縮）。</summary>
        static byte[] Psd(byte[] rgba, int w, int h)
        {
            var bytes = new List<byte>();
            void U16(int v) { bytes.Add((byte)(v >> 8)); bytes.Add((byte)v); }
            void U32(int v) { bytes.Add((byte)(v >> 24)); bytes.Add((byte)(v >> 16)); bytes.Add((byte)(v >> 8)); bytes.Add((byte)v); }
            bytes.AddRange(new byte[] { (byte)'8', (byte)'B', (byte)'P', (byte)'S' }); U16(1); bytes.AddRange(new byte[6]);
            U16(4); U32(h); U32(w); U16(8); U16(3);
            U32(0); U32(0); U32(0); U16(0);
            for (int c = 0; c < 4; c++)
                for (int fileRow = 0; fileRow < h; fileRow++)
                    for (int x = 0; x < w; x++) bytes.Add(rgba[((h - 1 - fileRow) * w + x) * 4 + c]);
            return bytes.ToArray();
        }

        /// <summary>PNG の IHDR の直後にチャンクを足す（CRC を付ける）。</summary>
        static byte[] WithChunk(byte[] png, string type, byte[] body)
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; table[n] = c; }
            byte[] Be(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            var typeAndBody = System.Text.Encoding.ASCII.GetBytes(type).Concat(body).ToArray();
            uint crc = 0xFFFFFFFFu;
            foreach (var b in typeAndBody) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            var chunk = Be((uint)body.Length).Concat(typeAndBody).Concat(Be(crc ^ 0xFFFFFFFFu)).ToArray();
            int afterIhdr = 8 + 4 + 4 + 13 + 4;
            return png.Take(afterIhdr).Concat(chunk).Concat(png.Skip(afterIhdr)).ToArray();
        }

        /// <summary>ファイルを書いて取り込み、インポート設定を決める（<paramref name="configure"/>）。</summary>
        Texture2D Import(string folder, string file, byte[] bytes, Action<TextureImporter> configure, out string path)
        {
            path = folder + "/" + file;
            File.WriteAllBytes(path, bytes);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.mipmapEnabled = false; importer.alphaIsTransparency = false; importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.npotScale = TextureImporterNPOTScale.None; importer.isReadable = false; importer.sRGBTexture = true;
            configure?.Invoke(importer);
            importer.SaveAndReimport();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.That(texture, Is.Not.Null, path);
            return texture;
        }

        // ───────── TGA の読み ─────────

        [Test]
        public void TgaIsReadExactlyInEveryRowOrderDepthAndCompression()
        {
            var picture = Picture(5, 4);
            foreach (int depth in new[] { 24, 32 })
                foreach (bool rle in new[] { false, true })
                    foreach (bool top in new[] { false, true })
                    {
                        var expected = (byte[])picture.Clone();
                        if (depth == 24) for (int i = 3; i < expected.Length; i += 4) expected[i] = 255; // 24 ビットは不透明
                        var read = TgaReader.Decode(Tga(picture, 5, 4, depth, rle, top), 8192, out int w, out int h);
                        Assert.That(read, Is.Not.Null, depth + " " + rle + " " + top);
                        Assert.That((w, h), Is.EqualTo((5, 4)));
                        if (depth == 32) Assert.That(read, Is.EqualTo(expected), "透明な画素の RGB も、行の向きも: " + depth + " rle=" + rle + " top=" + top);
                        else
                        {
                            // 24 ビットは RGB が同じ（透明な画素の RGB が残る）
                            for (int p = 0; p < read.Length; p += 4) { Assert.That(read[p], Is.EqualTo(picture[p])); Assert.That(read[p + 3], Is.EqualTo(255)); }
                        }
                    }
        }

        [Test]
        public void TgaThatCannotBeReadExactlyIsLeftToUnitysImportedPicture()
        {
            var picture = Picture();
            var good = Tga(picture, 4, 3, 32, false, false);
            Assert.That(TgaReader.Decode(good, 8192, out _, out _), Is.Not.Null);
            Assert.That(TgaReader.Decode(good, 3, out _, out _), Is.Null, "辺の上限を超える");
            Assert.That(TgaReader.Decode(good.Take(good.Length - 1).ToArray(), 8192, out _, out _), Is.Null, "途中で終わっている");
            Assert.That(TgaReader.Decode(null, 8192, out _, out _), Is.Null);
            var gray = (byte[])good.Clone(); gray[2] = 3; Assert.That(TgaReader.Decode(gray, 8192, out _, out _), Is.Null, "グレー");
            var mapped = (byte[])good.Clone(); mapped[2] = 1; Assert.That(TgaReader.Decode(mapped, 8192, out _, out _), Is.Null, "カラーマップ");
            var sixteen = (byte[])good.Clone(); sixteen[16] = 16; Assert.That(TgaReader.Decode(sixteen, 8192, out _, out _), Is.Null, "16 ビット");
            var noAlphaBits = (byte[])good.Clone(); noAlphaBits[17] = 0; Assert.That(TgaReader.Decode(noAlphaBits, 8192, out _, out _), Is.Null, "アルファの桁が 0 の 32 ビット");
            var rightFirst = (byte[])good.Clone(); rightFirst[17] |= 0x10; Assert.That(TgaReader.Decode(rightFirst, 8192, out _, out _), Is.Null, "右から");
            var rleTooLong = Tga(picture, 4, 3, 32, true, false); rleTooLong[18] = 0xFF; // 画素の数を超える繰り返し
            Assert.That(TgaReader.Decode(rleTooLong, 8192, out _, out _), Is.Null);
        }

        // ───────── 原本のファイルから読む ─────────

        [Test]
        public void APngIsReadFromItsFileExactlyAndNothingAboutTheAssetChanges()
        {
            string folder = NewFolder();
            var picture = Picture(8, 6);
            var png = RgbaPng.Encode(picture, 8, 6);
            var texture = Import(folder, "Body.png", png, i => i.sRGBTexture = true, out string path);
            string meta = Convert.ToBase64String(File.ReadAllBytes(path + ".meta")), stamp = UnityTextureReader.Stamp(path);
            int dirty = EditorUtility.GetDirtyCount(texture);
            var loaded = LiveLinkOriginals.Load(texture);
            Assert.That(loaded.State, Is.EqualTo(LiveLinkOriginalState.Image), loaded.Reason);
            Assert.That(loaded.Read, Is.EqualTo(LiveLinkOriginalRead.File));
            Assert.That(loaded.Compressed, Is.False);
            Assert.That((loaded.Width, loaded.Height), Is.EqualTo((8, 6)));
            Assert.That(loaded.Pixels, Is.EqualTo(picture), "圧縮の無い本当の値（透明な画素の RGB も）");
            Assert.That(loaded.Srgb, Is.EqualTo(PlayerSettings.colorSpace == ColorSpace.Gamma || ((TextureImporter)AssetImporter.GetAtPath(path)).sRGBTexture));
            // 元のテクスチャ・インポート設定・ファイルを変えない
            Assert.That(texture.isReadable, Is.False);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(png));
            Assert.That(Convert.ToBase64String(File.ReadAllBytes(path + ".meta")), Is.EqualTo(meta));
            Assert.That(UnityTextureReader.Stamp(path), Is.EqualTo(stamp));
            Assert.That(EditorUtility.GetDirtyCount(texture), Is.EqualTo(dirty));
        }

        [Test]
        public void ALinearTextureIsSentAsLinear()
        {
            string folder = NewFolder();
            var texture = Import(folder, "Mask.png", RgbaPng.Encode(Picture(), 4, 3), i => i.sRGBTexture = false, out _);
            var loaded = LiveLinkOriginals.Load(texture);
            Assert.That(loaded.Read, Is.EqualTo(LiveLinkOriginalRead.File));
            // ガンマの色空間のプロジェクトは、スタンドアロンの Color も画素をそのまま使うので、sRGB として送る
            Assert.That(loaded.Srgb, Is.EqualTo(PlayerSettings.colorSpace == ColorSpace.Gamma));
        }

        [Test]
        public void ATgaIsReadFromItsFileAndAgreesWithUnitysOwnImport()
        {
            string folder = NewFolder();
            var picture = Picture(6, 4);
            foreach (bool top in new[] { false, true })
            {
                var texture = Import(folder, "Hair" + top + ".tga", Tga(picture, 6, 4, 32, true, top), i => i.isReadable = true, out _);
                var loaded = LiveLinkOriginals.Load(texture);
                Assert.That(loaded.State, Is.EqualTo(LiveLinkOriginalState.Image), loaded.Reason);
                Assert.That(loaded.Read, Is.EqualTo(LiveLinkOriginalRead.File), "TGA も原本のファイルから");
                Assert.That(loaded.Pixels, Is.EqualTo(picture));
                // Unity の取り込みと、行の向き・色の並びが同じ（読み違えていない）
                var imported = UnityTextureReader.Read(texture).Content.CopyPixels();
                for (int p = 0; p < picture.Length; p += 4)
                    if (picture[p + 3] != 0) Assert.That(new[] { loaded.Pixels[p], loaded.Pixels[p + 1], loaded.Pixels[p + 2], loaded.Pixels[p + 3] }, Is.EqualTo(new[] { imported[p], imported[p + 1], imported[p + 2], imported[p + 3] }), "画素 " + p / 4 + " top=" + top);
            }
        }

        [Test]
        public void AJpegIsReadFromItsFileThroughUnitysDecoder()
        {
            string folder = NewFolder();
            var source = Own(new Texture2D(16, 16, TextureFormat.RGB24, false, false));
            source.SetPixels32(Enumerable.Range(0, 256).Select(i => new Color32((byte)(i % 16 * 16), (byte)(i / 16 * 16), 90, 255)).ToArray());
            var jpg = source.EncodeToJPG(95);
            var texture = Import(folder, "Photo.jpg", jpg, i => i.isReadable = true, out _);
            var loaded = LiveLinkOriginals.Load(texture);
            Assert.That(loaded.State, Is.EqualTo(LiveLinkOriginalState.Image), loaded.Reason);
            Assert.That(loaded.Read, Is.EqualTo(LiveLinkOriginalRead.File));
            Assert.That((loaded.Width, loaded.Height), Is.EqualTo((16, 16)));
            var imported = UnityTextureReader.Read(texture).Content.CopyPixels();
            for (int i = 0; i < imported.Length; i++) Assert.That(Math.Abs(loaded.Pixels[i] - imported[i]), Is.LessThanOrEqualTo(8), "byte " + i);
        }

        // ───────── 取り込み済みの絵から読む ─────────

        [Test]
        public void APsdIsReadFromTheImportedTextureNotItsFile()
        {
            string folder = NewFolder();
            var picture = Picture();
            var texture = Import(folder, "Layered.psd", Psd(picture, 4, 3), i => i.isReadable = true, out string path);
            string meta = Convert.ToBase64String(File.ReadAllBytes(path + ".meta"));
            var loaded = LiveLinkOriginals.Load(texture);
            Assert.That(loaded.State, Is.EqualTo(LiveLinkOriginalState.Image), loaded.Reason);
            Assert.That(loaded.Read, Is.EqualTo(LiveLinkOriginalRead.Imported), "PSD は原本のファイルでなく、取り込み済みの絵（CPU が読める値）から");
            Assert.That(loaded.Compressed, Is.False);
            Assert.That((loaded.Width, loaded.Height), Is.EqualTo((4, 3)));
            for (int p = 0; p < picture.Length; p += 4)
                if (picture[p + 3] != 0) Assert.That(new[] { loaded.Pixels[p], loaded.Pixels[p + 1], loaded.Pixels[p + 2], loaded.Pixels[p + 3] }, Is.EqualTo(new[] { picture[p], picture[p + 1], picture[p + 2], picture[p + 3] }), "画素 " + p / 4);
            Assert.That(Convert.ToBase64String(File.ReadAllBytes(path + ".meta")), Is.EqualTo(meta));
        }

        [Test]
        public void ATextureThatIsNotReadableOnTheCpuIsReadThroughTheGpuAndNothingIsMadeReadable()
        {
            RequireGraphics();
            string folder = NewFolder();
            var texture = Import(folder, "Layered.psd", Psd(Picture(), 4, 3), i => i.isReadable = false, out string path);
            string meta = Convert.ToBase64String(File.ReadAllBytes(path + ".meta"));
            var loaded = LiveLinkOriginals.Load(texture);
            Assert.That(loaded.State, Is.EqualTo(LiveLinkOriginalState.Image), loaded.Reason);
            Assert.That(loaded.Read, Is.EqualTo(LiveLinkOriginalRead.Gpu));
            Assert.That(texture.isReadable, Is.False, "読めるようにしない");
            Assert.That(Convert.ToBase64String(File.ReadAllBytes(path + ".meta")), Is.EqualTo(meta), "インポート設定を変えない");
        }

        [Test]
        public void AnImportThatChangesThePixelsIsNotReadFromTheFile()
        {
            string folder = NewFolder();
            var picture = Picture();
            // アルファを取り込まない設定: 取り込んだ絵のアルファは不透明で、ファイルの画素とは違う
            var texture = Import(folder, "NoAlpha.png", RgbaPng.Encode(picture, 4, 3), i => { i.alphaSource = TextureImporterAlphaSource.None; i.isReadable = true; }, out _);
            var loaded = LiveLinkOriginals.Load(texture);
            Assert.That(loaded.Read, Is.Not.EqualTo(LiveLinkOriginalRead.File), "取り込みが絵を変えるときは、ファイルでなく取り込んだ絵");
            Assert.That(loaded.State, Is.EqualTo(LiveLinkOriginalState.Image), loaded.Reason);
            Assert.That(loaded.Pixels[(1 * 4 + 1) * 4 + 3], Is.EqualTo(255), "Unity が見せている絵と同じ（アルファ無し）");
            Assert.That(LiveLinkOriginals.ImportKeepsPixels(null), Is.False);
        }

        [Test]
        public void AnImportThatFillsTransparentColorIsNotReadFromTheFile()
        {
            string folder = NewFolder();
            var picture = Picture();
            // 「アルファを透明として扱う」: Unity は完全に透明な画素の RGB を周りの色で埋める（取り込んだ絵のその画素は、ファイルの (9, 8, 7) でない）
            var texture = Import(folder, "Dilated.png", RgbaPng.Encode(picture, 4, 3), i => { i.alphaIsTransparency = true; i.isReadable = true; }, out string path);
            Assert.That(LiveLinkOriginals.ImportKeepsPixels((TextureImporter)AssetImporter.GetAtPath(path)), Is.False);
            var loaded = LiveLinkOriginals.Load(texture);
            Assert.That(loaded.State, Is.EqualTo(LiveLinkOriginalState.Image), loaded.Reason);
            Assert.That(loaded.Read, Is.Not.EqualTo(LiveLinkOriginalRead.File), "取り込みが透明な画素の色を変えるときは、ファイルでなく取り込んだ絵");
            var imported = UnityTextureReader.Read(texture).Content.CopyPixels();
            int clear = (1 * 4 + 1) * 4;
            Assert.That(imported.Skip(clear).Take(3), Is.Not.EqualTo(new byte[] { 9, 8, 7 }), "Unity は埋めている（この試験の前提）");
            Assert.That(loaded.Pixels.Skip(clear).Take(3), Is.EqualTo(imported.Skip(clear).Take(3)), "透明な画素の RGB も、Unity が見せている絵と同じ");
            // 設定が絵を変えなければ、ファイル
            var plain = Import(folder, "Plain.png", RgbaPng.Encode(picture, 4, 3), null, out string plainPath);
            Assert.That(LiveLinkOriginals.ImportKeepsPixels((TextureImporter)AssetImporter.GetAtPath(plainPath)), Is.True);
            Assert.That(LiveLinkOriginals.Load(plain).Read, Is.EqualTo(LiveLinkOriginalRead.File));
        }

        [Test]
        public void APngWhoseGammaUnityAppliesIsNotReadFromTheFile()
        {
            string folder = NewFolder();
            var picture = Picture();
            var png = RgbaPng.Encode(picture, 4, 3);
            var withGamma = WithChunk(png, "gAMA", new byte[] { 0, 1, 0x86, 0xA0 }); // 100000 = ガンマ 1.0（sRGB 相当の 45455 ではない）
            Assert.That(LiveLinkOriginals.PngHasGamma(png), Is.False);
            Assert.That(LiveLinkOriginals.PngHasGamma(withGamma), Is.True);
            Assert.That(LiveLinkOriginals.PngHasGamma(null), Is.False);
            Assert.That(LiveLinkOriginals.PngHasGamma(png.Take(20).ToArray()), Is.False, "途中で切れた PNG");
            // 「PNG のガンマを無視」が偽: Unity は取り込みで画素の値を補正する → 取り込んだ絵から読む
            var applied = Import(folder, "Gamma.png", withGamma, i => { i.ignorePngGamma = false; i.isReadable = true; }, out _);
            var loaded = LiveLinkOriginals.Load(applied);
            Assert.That(loaded.State, Is.EqualTo(LiveLinkOriginalState.Image), loaded.Reason);
            Assert.That(loaded.Read, Is.Not.EqualTo(LiveLinkOriginalRead.File), "ガンマを補正する取り込みは、ファイルでなく取り込んだ絵");
            var imported = UnityTextureReader.Read(applied).Content.CopyPixels();
            Assert.That(imported, Is.Not.EqualTo(picture), "Unity は補正している（この試験の前提）");
            for (int p = 0; p < picture.Length; p += 4)
                if (picture[p + 3] != 0) Assert.That(loaded.Pixels.Skip(p).Take(4), Is.EqualTo(imported.Skip(p).Take(4)), "画素 " + p / 4);
            // 「PNG のガンマを無視」が真なら、補正しないので、ファイル
            var ignored = Import(folder, "GammaIgnored.png", withGamma, i => { i.ignorePngGamma = true; i.isReadable = true; }, out _);
            var kept = LiveLinkOriginals.Load(ignored);
            Assert.That(kept.Read, Is.EqualTo(LiveLinkOriginalRead.File));
            Assert.That(kept.Pixels, Is.EqualTo(picture));
        }

        [Test]
        public void ACompressedTextureIsMarkedCompressed()
        {
            RequireGraphics();
            string folder = NewFolder();
            var picture = Picture(8, 8);
            var texture = Import(folder, "Compressed.png", RgbaPng.Encode(picture, 8, 8), i =>
            {
                i.textureType = TextureImporterType.Sprite; // ファイルの画素をそのまま絵にしない設定 → 取り込んだ絵から読む
                i.textureCompression = TextureImporterCompression.Compressed; i.crunchedCompression = false; i.isReadable = false;
            }, out _);
            var loaded = LiveLinkOriginals.Load(texture);
            Assert.That(loaded.Read, Is.Not.EqualTo(LiveLinkOriginalRead.File));
            Assert.That(loaded.Compressed, Is.EqualTo(UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat)));
        }

        // ───────── アセットでない絵・読めない絵 ─────────

        [Test]
        public void AnUnsavedTextureIsReadThroughTheGpu()
        {
            RequireGraphics();
            var t = Own(new Texture2D(8, 8, TextureFormat.RGBA32, false, false) { name = "unsaved" });
            t.SetPixels32(Enumerable.Repeat(new Color32(200, 30, 10, 255), 64).ToArray());
            t.Apply(false, true);
            var loaded = LiveLinkOriginals.Load(t);
            Assert.That(loaded.State, Is.EqualTo(LiveLinkOriginalState.Image), loaded.Reason);
            Assert.That(loaded.Read, Is.EqualTo(LiveLinkOriginalRead.Gpu));
            Assert.That(loaded.Pixels.Take(4), Is.EqualTo(new byte[] { 200, 30, 10, 255 }));
            Assert.That((loaded.Width, loaded.Height, loaded.Pixels.Length), Is.EqualTo((8, 8, 256)));
        }

        [Test]
        public void ATextureThatCannotBeSentGivesItsStateAndNoPixels()
        {
            var tooWide = Own(new RenderTexture(LiveLinkOriginals.MaxEdge + 1, 1, 0));
            tooWide.Create();
            var large = LiveLinkOriginals.Load(tooWide);
            Assert.That(large.State, Is.EqualTo(LiveLinkOriginalState.TooLarge));
            Assert.That(large.Pixels, Is.Null);
            Assert.That((large.Width, large.Height), Is.EqualTo((LiveLinkOriginals.MaxEdge + 1, 1)));
            var volume = Own(new Texture3D(2, 2, 2, TextureFormat.RGBA32, false));
            Assert.That(LiveLinkOriginals.Load(volume).State, Is.EqualTo(LiveLinkOriginalState.Unreadable));
            Assert.That(LiveLinkOriginals.Load(null).State, Is.EqualTo(LiveLinkOriginalState.Unreadable));
        }

        // ───────── 送る（ブリッジの中の自己診断のスタンドアロン） ─────────

        void Connect(ulong features)
        {
            string name = UniqueName();
            server = LiveLinkTestServer.Start(name, 128, 64);
            Assert.That(server, Is.Not.EqualTo(0UL));
            Assert.That(LiveLinkTestServer.Configure(server, new Version(0, 1, 0), null, features), Is.True);
            session = LiveLinkSession.Start(name);
            Pump(() => session.Status == LiveLinkStatus.Connected, "the connection");
        }

        /// <summary>RGBA を r | g &lt;&lt; 8 | b &lt;&lt; 16 | a &lt;&lt; 24 に詰める（自己診断のスタンドアロンの答えと同じ）。</summary>
        static uint Pack(byte r, byte g, byte b, byte a) => (uint)r | (uint)g << 8 | (uint)b << 16 | (uint)a << 24;

        GameObject Model(Texture mainTexture, out Material material, out MeshRenderer renderer)
        {
            var root = Own(new GameObject("LiveLinkOriginalsModel"));
            root.transform.position = new Vector3(800, 800, 800);
            var cube = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
            cube.transform.SetParent(root.transform, false);
            material = Own(new Material(Shader.Find("Standard")) { name = "OriginalsBody" });
            material.SetTexture("_MainTex", mainTexture);
            renderer = cube.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return root;
        }

        Texture2D Solid(Color32 color, int size = 16)
        {
            var t = Own(new Texture2D(size, size, TextureFormat.RGBA32, false, false) { name = "solid" });
            t.SetPixels32(Enumerable.Repeat(color, size * size).ToArray());
            t.Apply(false, true);
            return t;
        }

        /// <summary>テクスチャごとに 1 つのキューブ（それぞれ別のマテリアル。_MainTex に入れる）を持つモデル。</summary>
        GameObject ModelOf(params Texture[] textures)
        {
            var root = Own(new GameObject("LiveLinkOriginalsModel"));
            root.transform.position = new Vector3(800, 800, 800);
            for (int i = 0; i < textures.Length; i++)
            {
                var cube = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
                cube.transform.SetParent(root.transform, false);
                cube.transform.localPosition = new Vector3(i * 2, 0, 0);
                var material = Own(new Material(Shader.Find("Standard")) { name = "OriginalsBody" + i });
                material.SetTexture("_MainTex", textures[i]);
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            return root;
        }

        /// <summary>セッションを更新せずに（セッション自身の送りを進めずに）、自己診断のスタンドアロンが受けるのを待つ。</summary>
        static void WaitFor(Func<bool> done, string what, double seconds = 15)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.Elapsed.TotalSeconds > seconds) Assert.Fail("timed out waiting for " + what);
                Thread.Sleep(5);
            }
        }

        const long OnePicture = 16 * 16 * 4;

        Color32[] Colors => new[] { new Color32(200, 30, 10, 255), new Color32(10, 200, 30, 255), new Color32(30, 10, 200, 255) };

        [Test]
        public void TheDisplayIsNotReplacedUntilTheStandaloneReturnsTheSetWithTheOriginal()
        {
            RequireGraphics();
            var original = Solid(new Color32(200, 30, 10, 255));
            var root = Model(original, out var material, out var renderer);
            int materialDirty = EditorUtility.GetDirtyCount(material);
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            Assert.That(session.CommonFeatures & LiveLinkBridge.FeatureOriginalTextures, Is.Not.EqualTo(0UL));
            Assert.That(session.SendModel(root), Is.Null);
            Assert.That(session.OriginalsPending, Is.True, "送る列ができる");
            Assert.That(renderer.HasPropertyBlock(), Is.False, "送っただけでは、表示を置き換えない");
            // 元の絵が揃うまで、スタンドアロンはセットを出さない: 表示が置き換わったとき（セットが返ったとき）は、もう元の絵が届いている
            var clock = Stopwatch.StartNew();
            while (session.Display.AppliedCount == 0)
            {
                Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(15), "the set with the original never came back");
                session.Tick();
                if (session.Display.AppliedCount > 0) break;
                Thread.Sleep(5);
            }
            var stats = LiveLinkTestServer.Stats(server);
            Assert.That((stats.originals, stats.held_sets), Is.EqualTo((1u, 0u)), "元の絵が揃ってから出た");
            Assert.That(session.OriginalsSent, Is.EqualTo(1));
            // 返ったセットの Color は元の絵（見た目が変わらない）。流し込み先に当たった絵を読む
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block, 0);
            var shown = block.GetTexture("_MainTex");
            Assert.That(shown, Is.Not.Null);
            var center = LiveLinkTestHelpers.CenterPixel(shown);
            Assert.That(new[] { center.r, center.g, center.b, center.a }, Is.EqualTo(new byte[] { 200, 30, 10, 255 }));
            // 受けた元の絵: GPU を通して（アセットでない絵）、sRGB、大きさ
            Assert.That(LiveLinkTestServer.Original(server, 0, "_MainTex", out var o), Is.True);
            Assert.That((o.state, o.read, o.compressed, o.srgb, o.width, o.height), Is.EqualTo((0u, 2u, 0u, 1u, 16u, 16u)));
            Assert.That(o.corner, Is.EqualTo(Pack(200, 30, 10, 255)));
            // 元のテクスチャとマテリアルを変えない
            Assert.That(material.GetTexture("_MainTex"), Is.SameAs(original));
            Assert.That(EditorUtility.GetDirtyCount(material), Is.EqualTo(materialDirty));
            Assert.That(original.isReadable, Is.False);
            // 切ると、元のテクスチャのまま
            session.Dispose(); session = null;
            Assert.That(renderer.HasPropertyBlock(), Is.False);
        }

        [Test]
        public void NothingIsReadOrSentToAStandaloneWithoutTheMark()
        {
            RequireGraphics();
            var root = Model(Solid(new Color32(1, 2, 3, 255)), out _, out var renderer);
            Connect(LiveLinkBridge.FeatureMaterialValues);
            Assert.That(session.CommonFeatures & LiveLinkBridge.FeatureOriginalTextures, Is.EqualTo(0UL));
            Assert.That(session.SendModel(root), Is.Null);
            Assert.That(session.OriginalsPending, Is.False, "印が無ければ、列も作らない（読みもしない）");
            // 元の絵を待たない古いスタンドアロンは、今までどおり、すぐセットを返す
            Pump(() => session.Display.AppliedCount > 0, "the display of the standalone's set");
            var stats = LiveLinkTestServer.Stats(server);
            Assert.That((stats.originals, stats.held_sets), Is.EqualTo((0u, 0u)));
            Assert.That((session.OriginalsSent, session.OriginalsDeclined), Is.EqualTo((0, 0)));
            Assert.That(LiveLinkBridge.OriginalSend(session.Handle, 0, "_MainTex", LiveLinkOriginalState.Unreadable, LiveLinkOriginalRead.File, false, 16, 16, true, null), Is.EqualTo(0), "ブリッジも、印の無い相手へは送らない");
            Assert.That(renderer.HasPropertyBlock(), Is.True);
        }

        [Test]
        public void ATextureTooLargeToSendStillLetsTheSetOutWithItsState()
        {
            RequireGraphics();
            var wide = Own(new RenderTexture(LiveLinkOriginals.MaxEdge + 8, 4, 0) { name = "wide" });
            wide.Create();
            var root = Model(wide, out _, out _);
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount > 0, "the set released without an image");
            Assert.That(LiveLinkTestServer.Original(server, 0, "_MainTex", out var o), Is.True);
            Assert.That((o.state, o.width, o.height, o.center), Is.EqualTo(((uint)LiveLinkOriginalState.TooLarge, (uint)(LiveLinkOriginals.MaxEdge + 8), 4u, 0u)));
            Assert.That((session.OriginalsSent, session.OriginalsDeclined), Is.EqualTo((0, 1)));
        }

        [Test]
        public void PicturesPastTheBudgetOfOneSendAreDeclinedWithTheirState()
        {
            RequireGraphics();
            var root = Model(Solid(new Color32(10, 20, 30, 255), 32), out _, out _);
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            Assert.That(session.SendModel(root), Is.Null);
            // 予算が足りない送り（32 × 32 × 4 = 4096 バイトに 100 バイト）: 絵は送らず、予算を超えた様子だけを送る
            var sender = new LiveLinkOriginals.Sender(session.Model, LiveLinkOriginals.Plan(session.Model), 100);
            var report = sender.Pump(session.Handle);
            Assert.That((report.Images, report.Declined), Is.EqualTo((0, 1)));
            Pump(() => LiveLinkTestServer.Stats(server).originals >= 1, "the declined original");
            Assert.That(LiveLinkTestServer.Original(server, 0, "_MainTex", out var o), Is.True);
            Assert.That((o.state, o.width, o.height), Is.EqualTo(((uint)LiveLinkOriginalState.OverBudget, 32u, 32u)));
        }

        [Test]
        public void AnAssetTextureIsSentFromItsFileThroughTheLink()
        {
            RequireGraphics();
            string folder = NewFolder();
            var picture = Picture(8, 6);
            var texture = Import(folder, "Body.png", RgbaPng.Encode(picture, 8, 6), null, out string path);
            string meta = Convert.ToBase64String(File.ReadAllBytes(path + ".meta")), stamp = UnityTextureReader.Stamp(path);
            var root = Model(texture, out var material, out _);
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount > 0, "the set with the original");
            Assert.That(LiveLinkTestServer.Original(server, 0, "_MainTex", out var o), Is.True);
            Assert.That((o.state, o.read, o.compressed, o.width, o.height), Is.EqualTo((0u, 0u, 0u, 8u, 6u)), "原本のファイルから");
            Assert.That(o.corner, Is.EqualTo(Pack(picture[0], picture[1], picture[2], picture[3])));
            Assert.That(Convert.ToBase64String(File.ReadAllBytes(path + ".meta")), Is.EqualTo(meta));
            Assert.That(UnityTextureReader.Stamp(path), Is.EqualTo(stamp));
            Assert.That(material.GetTexture("_MainTex"), Is.SameAs(texture));
        }

        // ───────── 複数の絵・分けて送る・止める ─────────

        [Test]
        public void OneBudgetIsSharedByEveryPictureOfASend()
        {
            RequireGraphics();
            var colors = Colors;
            var root = ModelOf(Solid(colors[0]), Solid(colors[1]), Solid(colors[2]));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            Assert.That(session.SendModel(root), Is.Null);
            session.OriginalsPendingLimit = 0; // セッション自身の送りは止めておく（この試験が自分で送る）
            var plan = LiveLinkOriginals.Plan(session.Model);
            Assert.That(plan.Select(j => j.Material), Is.EqualTo(new[] { 0, 1, 2 }));
            // 3 枚で予算は 2 枚ぶんと少し: 1 枚目と 2 枚目は送り、予算の残りでは足りない 3 枚目は様子だけ（枚ごとに残りから引く）
            var sender = new LiveLinkOriginals.Sender(session.Model, plan, 2 * OnePicture + 10);
            var report = sender.Pump(session.Handle, 1e9);
            Assert.That((report.Images, report.Declined, report.Bytes, sender.Done), Is.EqualTo((2, 1, 2 * OnePicture, true)));
            Assert.That((sender.Images, sender.Declined, sender.Bytes), Is.EqualTo((2, 1, 2 * OnePicture)));
            WaitFor(() => LiveLinkTestServer.Stats(server).originals >= 3, "the three originals");
            for (int m = 0; m < 3; m++)
            {
                Assert.That(LiveLinkTestServer.Original(server, m, "_MainTex", out var o), Is.True, "material " + m);
                Assert.That(o.state, Is.EqualTo(m < 2 ? (uint)LiveLinkOriginalState.Image : (uint)LiveLinkOriginalState.OverBudget), "material " + m);
                Assert.That((o.width, o.height), Is.EqualTo((16u, 16u)));
            }
        }

        [Test]
        public void TheTimeBudgetSplitsASendAcrossPumps()
        {
            RequireGraphics();
            var colors = Colors;
            var root = ModelOf(Solid(colors[0]), Solid(colors[1]), Solid(colors[2]));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            Assert.That(session.SendModel(root), Is.Null);
            session.OriginalsPendingLimit = 0;
            // 時間が足りなければ、最初の 1 枚は必ず送り、残りは次の Pump へ
            var split = new LiveLinkOriginals.Sender(session.Model, LiveLinkOriginals.Plan(session.Model));
            for (int i = 1; i <= 3; i++)
            {
                var r = split.Pump(session.Handle, 0);
                Assert.That(r.Images + r.Declined, Is.EqualTo(1), "Pump " + i);
                Assert.That(split.Done, Is.EqualTo(i == 3), "Pump " + i);
            }
            // 時間が十分なら 1 回で全部
            var whole = new LiveLinkOriginals.Sender(session.Model, LiveLinkOriginals.Plan(session.Model));
            Assert.That(whole.Pump(session.Handle, 1e9).Images, Is.EqualTo(3));
            Assert.That(whole.Done, Is.True);
            // セッションの更新ごとの送りも、時間が足りなければ 1 回の更新で 1 枚
            session.OriginalsPumpMs = 0; session.OriginalsPendingLimit = LiveLinkOriginals.PendingLimit;
            Assert.That(session.SendModel(root), Is.Null);
            for (int i = 1; i <= 3; i++)
            {
                session.Tick();
                Assert.That(session.OriginalsSent, Is.EqualTo(i), "Tick " + i);
                Assert.That(session.OriginalsPending, Is.EqualTo(i < 3), "Tick " + i);
            }
        }

        [Test]
        public void NothingIsReadWhileTheStandaloneIsBehindOnWhatWasSent()
        {
            RequireGraphics();
            var colors = Colors;
            var root = ModelOf(Solid(colors[0]), Solid(colors[1]));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            session.OriginalsPendingLimit = 0; // 積んだ命令が上限以上: 次を読まない
            Assert.That(session.SendModel(root), Is.Null);
            for (int i = 0; i < 5; i++) { session.Tick(); Thread.Sleep(5); }
            Assert.That(session.OriginalsSent + session.OriginalsDeclined, Is.EqualTo(0));
            Assert.That(session.OriginalsPending, Is.True, "送りは残る（捨てない）");
            Assert.That(LiveLinkTestServer.Stats(server).originals, Is.EqualTo(0u));
            var sender = new LiveLinkOriginals.Sender(session.Model, LiveLinkOriginals.Plan(session.Model));
            var held = sender.Pump(session.Handle, 1e9, 0);
            Assert.That((held.Images, held.Declined, sender.Done), Is.EqualTo((0, 0, false)), "上限を超えている間は読まない");
            // 上限を戻すと、続きから送る
            session.OriginalsPendingLimit = LiveLinkOriginals.PendingLimit;
            Pump(() => !session.OriginalsPending, "the queue to finish");
            Assert.That(session.OriginalsSent, Is.EqualTo(2));
        }

        [Test]
        public void TexturesSharedByMaterialsAreReadOnceAndSentForEveryMaterial()
        {
            RequireGraphics();
            var colors = Colors;
            var atlas = Solid(colors[0]);
            var other = Solid(colors[1]);
            var root = ModelOf(atlas, other, atlas); // 材料 0 と 2 が同じ絵
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            Assert.That(session.SendModel(root), Is.Null);
            session.OriginalsPendingLimit = 0;
            var plan = LiveLinkOriginals.Plan(session.Model);
            Assert.That(plan.Select(j => j.TextureId).Distinct().Count(), Is.EqualTo(2));
            var sender = new LiveLinkOriginals.Sender(session.Model, plan);
            var report = sender.Pump(session.Handle, 1e9);
            Assert.That(sender.Reads, Is.EqualTo(2), "同じ絵は 1 回だけ読む（間に別の絵があっても、同じ絵の枠は隣にまとめる）");
            Assert.That((report.Images, report.Bytes), Is.EqualTo((3, 3 * OnePicture)), "送りは枠ごと（予算も枠ごとに引く）");
            WaitFor(() => LiveLinkTestServer.Stats(server).originals >= 3, "the three originals");
            foreach (int m in new[] { 0, 2 })
            {
                Assert.That(LiveLinkTestServer.Original(server, m, "_MainTex", out var o), Is.True);
                Assert.That(o.corner, Is.EqualTo(Pack(200, 30, 10, 255)), "material " + m);
            }
            Assert.That(LiveLinkTestServer.Original(server, 1, "_MainTex", out var second), Is.True);
            Assert.That(second.corner, Is.EqualTo(Pack(10, 200, 30, 255)));
            // 予算は共有の絵でも枠ごと: 2 枠ぶんと少しなら、3 枠目は様子だけ
            var tight = new LiveLinkOriginals.Sender(session.Model, plan, 2 * OnePicture + 10);
            var r = tight.Pump(session.Handle, 1e9);
            Assert.That((r.Images, r.Declined), Is.EqualTo((2, 1)));
        }

        [Test]
        public void ASenderStopsAndEmptiesItsQueueWhenTheStandaloneHasNoMarkOrTheBridgeRefuses()
        {
            RequireGraphics();
            var root = ModelOf(Solid(Colors[0]), Solid(Colors[1]));
            // 印が無くなった（つながり直したスタンドアロンが元の絵の印を持たない）: 何も送らず、列を空にする（失敗の知らせもしない）
            Connect(LiveLinkBridge.FeatureMaterialValues);
            Assert.That(session.SendModel(root), Is.Null);
            var sender = new LiveLinkOriginals.Sender(session.Model, LiveLinkOriginals.Plan(session.Model));
            var r = sender.Pump(session.Handle, 1e9);
            Assert.That((r.Images, r.Declined, r.Problem, sender.Done), Is.EqualTo((0, 0, (string)null, true)));
            Assert.That(LiveLinkTestServer.Stats(server).originals, Is.EqualTo(0u));
            session.Dispose(); session = null;
            LiveLinkTestServer.Stop(server); server = 0;

            // ブリッジが断った（存在しないマテリアルの番号）: 理由を返し、続きは送らずに列を空にする
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            Assert.That(session.SendModel(root), Is.Null);
            session.OriginalsPendingLimit = 0;
            var jobs = new List<LiveLinkOriginals.Job>
            {
                new LiveLinkOriginals.Job { Material = 99, Property = "_MainTex", Width = 16, Height = 16, TextureId = 1 },
                new LiveLinkOriginals.Job { Material = 0, Property = "_MainTex", Width = 16, Height = 16, TextureId = 2 },
            };
            var refused = new LiveLinkOriginals.Sender(session.Model, jobs);
            var rr = refused.Pump(session.Handle, 1e9);
            Assert.That(rr.Problem, Does.Contain("refused"));
            Assert.That((rr.Images, rr.Declined, refused.Done), Is.EqualTo((0, 0, true)), "断られたら、続きの枠は送らない");
            Thread.Sleep(100);
            Assert.That(LiveLinkTestServer.Stats(server).originals, Is.EqualTo(0u));
        }

        [Test]
        public void SendingTheModelAgainDropsTheOldQueueAndStartsTheNewOne()
        {
            RequireGraphics();
            var colors = Colors;
            var root = ModelOf(Solid(colors[0]), Solid(colors[1]), Solid(colors[2]));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            session.OriginalsPumpMs = 0; // 1 回の更新で 1 枚
            Assert.That(session.SendModel(root), Is.Null);
            int firstGeneration = session.Model.Generation;
            session.Tick();
            Assert.That((session.OriginalsSent, session.OriginalsPending), Is.EqualTo((1, true)), "古い送りは途中");
            // 送りの途中でモデルを送り直す: 古い列（残り 2 枚）は捨てて、新しい世代の列（3 枚）を最初から
            Assert.That(session.SendModel(root), Is.Null);
            Assert.That(session.Model.Generation, Is.Not.EqualTo(firstGeneration));
            Assert.That(session.OriginalsPending, Is.True);
            Pump(() => !session.OriginalsPending, "the new queue to finish");
            Assert.That(session.OriginalsSent, Is.EqualTo(1 + 3), "古い列の残りは送らず、新しい列は 3 枚そろって送る");
            for (int m = 0; m < 3; m++) Assert.That(LiveLinkTestServer.Original(server, m, "_MainTex", out var o) && o.state == (uint)LiveLinkOriginalState.Image, Is.True, "material " + m);
        }

        [Test]
        public void AModelThatCannotBeSentLeavesNoQueueOfTheOldOne()
        {
            RequireGraphics();
            var root = ModelOf(Solid(Colors[0]), Solid(Colors[1]));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            session.OriginalsPendingLimit = 0;
            Assert.That(session.SendModel(root), Is.Null);
            Assert.That(session.OriginalsPending, Is.True);
            // 描画するメッシュが無くなって送り直せない: 古い列を残さない（新しいモデルの世代と合わない列が残ったままにならない）
            foreach (Transform child in root.transform) child.gameObject.SetActive(false);
            Assert.That(session.SendModel(root), Is.Not.Null);
            Assert.That(session.Model, Is.Null);
            Assert.That(session.OriginalsPending, Is.False);
            session.OriginalsPendingLimit = LiveLinkOriginals.PendingLimit;
            for (int i = 0; i < 5; i++) { session.Tick(); Thread.Sleep(5); }
            Assert.That(session.OriginalsSent + session.OriginalsDeclined, Is.EqualTo(0));
            Assert.That(LiveLinkTestServer.Stats(server).originals, Is.EqualTo(0u));
        }

        [Test]
        public void ClosingTheModelCancelsTheQueue()
        {
            RequireGraphics();
            var colors = Colors;
            var root = ModelOf(Solid(colors[0]), Solid(colors[1]), Solid(colors[2]));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            session.OriginalsPumpMs = 0;
            Assert.That(session.SendModel(root), Is.Null);
            session.Tick();
            Assert.That((session.OriginalsSent, session.OriginalsPending), Is.EqualTo((1, true)));
            WaitFor(() => LiveLinkTestServer.Stats(server).originals >= 1, "the first original");
            uint before = LiveLinkTestServer.Stats(server).originals;
            session.CloseModel();
            Assert.That(session.OriginalsPending, Is.False);
            for (int i = 0; i < 10; i++) { session.Tick(); Thread.Sleep(5); }
            Assert.That(session.OriginalsSent, Is.EqualTo(1), "閉じたあとは送らない");
            Assert.That(LiveLinkTestServer.Stats(server).originals, Is.EqualTo(before));
        }

        [Test]
        public void DisposingTheSessionCancelsTheQueue()
        {
            RequireGraphics();
            var colors = Colors;
            var root = ModelOf(Solid(colors[0]), Solid(colors[1]), Solid(colors[2]));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            session.OriginalsPumpMs = 0;
            Assert.That(session.SendModel(root), Is.Null);
            session.Tick();
            Assert.That((session.OriginalsSent, session.OriginalsPending), Is.EqualTo((1, true)));
            WaitFor(() => LiveLinkTestServer.Stats(server).originals >= 1, "the first original");
            uint before = LiveLinkTestServer.Stats(server).originals;
            var gone = session;
            gone.Dispose();
            Assert.That(gone.OriginalsPending, Is.False);
            Assert.That(gone.Status, Is.EqualTo(LiveLinkStatus.Closed));
            for (int i = 0; i < 10; i++) { gone.Tick(); Thread.Sleep(5); }
            Assert.That(gone.OriginalsSent, Is.EqualTo(1));
            Assert.That(LiveLinkTestServer.Stats(server).originals, Is.EqualTo(before));
        }

        [Test]
        public void ALostConnectionCancelsTheQueue()
        {
            RequireGraphics();
            var root = ModelOf(Solid(Colors[0]), Solid(Colors[1]));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            session.OriginalsPendingLimit = 0; // 送りが残ったまま、つながりが切れる
            Assert.That(session.SendModel(root), Is.Null);
            Assert.That(session.OriginalsPending, Is.True);
            LiveLinkTestServer.Stop(server); server = 0;
            Pump(() => !session.OriginalsPending, "the queue dropped after the connection ended");
            Assert.That(session.Status, Is.Not.EqualTo(LiveLinkStatus.Connected));
            Assert.That(session.OriginalsSent + session.OriginalsDeclined, Is.EqualTo(0));
        }
    }

    /// <summary>試験の道具: テクスチャの真ん中の画素（GPU から。変換しない）。</summary>
    static class LiveLinkTestHelpers
    {
        public static Color32 CenterPixel(Texture t)
        {
            var request = AsyncGPUReadback.Request(t, 0);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False, "readback of " + t.name);
            return request.GetData<Color32>().ToArray()[t.height / 2 * t.width + t.width / 2];
        }
    }
}
