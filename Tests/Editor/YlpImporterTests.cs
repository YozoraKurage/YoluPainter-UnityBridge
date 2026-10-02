using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>.ylp の ScriptedImporter。テストごとに Assets の下へ一時フォルダを作り、実際に AssetDatabase で取り込む。</summary>
    public sealed class YlpImporterTests
    {
        string folder;

        [SetUp] public void CreateFolder()
        {
            string name = "YlpImporterTests-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", name), Is.Not.Empty);
            folder = "Assets/" + name;
        }

        [TearDown] public void DeleteFolder()
        {
            if (folder == null) return;
            AssetDatabase.DeleteAsset(folder);
            Assert.That(AssetDatabase.IsValidFolder(folder), Is.False);
            Assert.That(Directory.Exists(Path.GetFullPath(folder)), Is.False, "nothing is left in Assets");
            Assert.That(File.Exists(Path.GetFullPath(folder + ".meta")), Is.False);
            folder = null;
        }

        static void Paint(PaintDocument d, Guid layer, PaintChannel channel, Rgba32 color, double x0, double y0, double x1, double y1)
        {
            var settings = new BrushSettings { Color = color, Radius = 6, Hardness = .4, PressureSize = false, PressureOpacity = false };
            using (var s = d.BeginStroke(layer, channel, settings)) { s.Add(new BrushSample(x0, y0, 1, 0)); s.Add(new BrushSample(x1, y1, 1, .1)); s.Commit(); }
        }

        /// <summary>Color と Height と Emission を使う、上下で違う絵のドキュメント（左下原点の向きの取り違えが分かるように）。</summary>
        static PaintDocument ThreeChannelDocument(int width = 64, int height = 48, byte shade = 200)
        {
            var d = new PaintDocument(width, height, 32);
            var layer = d.AddLayer("L").Id;
            d.SetChannelEnabled(layer, PaintChannel.Height, true);
            d.SetChannelEnabled(layer, PaintChannel.Emission, true);
            Paint(d, layer, PaintChannel.Color, new Rgba32(shade, 40, 10), 8, 8, width - 10, 12);
            Paint(d, layer, PaintChannel.Height, new Rgba32(90, 120, shade), 10, height - 10, width / 2, height - 14);
            Paint(d, layer, PaintChannel.Emission, new Rgba32(10, 250, 30), width / 2, height / 2, width / 2 + 4, height / 2);
            return d;
        }

        static Dictionary<string, byte[]> Files(PaintDocument d, bool composites = true)
        {
            var files = composites ? YlpContent.Composites(d) : new Dictionary<string, byte[]>(StringComparer.Ordinal);
            files[YlpArchive.NativeName] = DocumentBinary.Write(d);
            return files;
        }

        string Import(string name, byte[] bytes)
        {
            string path = folder + "/" + name + ".ylp";
            File.WriteAllBytes(Path.GetFullPath(path), bytes);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return path;
        }

        string Import(string name, PaintDocument d, bool composites = true) => Import(name, YlpArchive.Write(Files(d, composites)));

        /// <summary>フィールドを直接変えた設定を .meta に書いて取り込み直す（インスペクターは SerializedObject 経由で dirty にする）。</summary>
        static void Reimport(YlpImporter importer) { EditorUtility.SetDirty(importer); importer.SaveAndReimport(); }

        static Texture2D TextureAt(string path, PaintChannel channel) => YlpImporter.LoadTextures(path).TryGetValue(channel, out var t) ? t : null;

        static byte[] Bytes(Texture2D texture)
        {
            var pixels = texture.GetPixels32(0);
            var rgba = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++) { rgba[i * 4] = pixels[i].r; rgba[i * 4 + 1] = pixels[i].g; rgba[i * 4 + 2] = pixels[i].b; rgba[i * 4 + 3] = pixels[i].a; }
            return rgba;
        }

        static void AssertSamePixels(Texture2D texture, PaintDocument d, PaintChannel channel)
        {
            Assert.That(texture, Is.Not.Null, channel + " texture");
            Assert.That(texture.width, Is.EqualTo(d.Width)); Assert.That(texture.height, Is.EqualTo(d.Height));
            Assert.That(Bytes(texture), Is.EqualTo(d.Composite(channel)), channel + " pixels equal the core composite");
        }

        [Test] public void EachChannelBecomesASubAssetAndColorIsTheMainObject()
        {
            var d = ThreeChannelDocument();
            string path = Import("Paint", d);
            var main = AssetDatabase.LoadMainAssetAtPath(path) as Texture2D;
            Assert.That(main, Is.Not.Null, "the main object is a texture, so the file can be dropped on a texture slot");
            Assert.That(AssetDatabase.GetMainAssetTypeAtPath(path), Is.EqualTo(typeof(Texture2D)));
            Assert.That(main.name, Is.EqualTo("Paint"), "Unity names the main object after the file");
            var textures = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Texture2D>().ToList();
            Assert.That(textures.Count, Is.EqualTo(3));
            CollectionAssert.AreEquivalent(new[] { "Paint Height", "Paint Emission" }, AssetDatabase.LoadAllAssetRepresentationsAtPath(path).Select(o => o.name), "the other channels are visible sub-assets; the import info is hidden");
            var info = YlpImporter.LoadInfo(path);
            Assert.That(info.channels, Is.EqualTo(new[] { PaintChannel.Color, PaintChannel.Height, PaintChannel.Emission }), "channels in enum order");
            Assert.That(info.width, Is.EqualTo(64)); Assert.That(info.height, Is.EqualTo(48));
            Assert.That(info.fromNativeDocument, Is.False, "the composites are used as they are");
            Assert.That(info.error, Is.Empty);
            Assert.That(TextureAt(path, PaintChannel.Color), Is.SameAs(main));
        }

        [Test] public void WithoutColorTheFirstChannelInEnumOrderIsTheMainObject()
        {
            var d = new PaintDocument(32, 32, 32);
            var layer = d.AddLayer("L").Id;
            d.SetChannelEnabled(layer, PaintChannel.Height, true);
            d.SetChannelEnabled(layer, PaintChannel.Metallic, true);
            d.SetChannelEnabled(layer, PaintChannel.Color, false);
            Paint(d, layer, PaintChannel.Metallic, new Rgba32(255, 255, 255), 4, 4, 20, 20);
            string path = Import("Data", d);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(path), Is.SameAs(TextureAt(path, PaintChannel.Metallic)));
            Assert.That(YlpImporter.LoadInfo(path).channels, Is.EqualTo(new[] { PaintChannel.Metallic, PaintChannel.Height }));
            Assert.That(TextureAt(path, PaintChannel.Color), Is.Null, "unused channels make no texture");
            Assert.That(TextureAt(path, PaintChannel.Height), Is.Not.Null);
        }

        [Test] public void PixelsEqualTheCoreCompositeExactly()
        {
            var d = ThreeChannelDocument();
            string path = Import("Exact", d);
            foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Height, PaintChannel.Emission }) AssertSamePixels(TextureAt(path, channel), d, channel);
            var color = d.Composite(PaintChannel.Color);
            Assert.That(Enumerable.Range(0, color.Length / 4).Any(i => color[i * 4 + 3] > 0 && color[i * 4 + 3] < 255), Is.True, "the soft brush leaves partial alpha, which must not be premultiplied");
        }

        [Test] public void TransparentPixelsKeepTheirRgbAndNothingIsPremultiplied()
        {
            var d = ThreeChannelDocument(4, 2);
            var rgba = new byte[]
            {
                10, 20, 30, 0,    200, 100, 50, 0,   255, 255, 255, 1,  0, 0, 0, 0,     // 下の行（左下原点）
                90, 180, 45, 128, 1, 2, 3, 255,      250, 5, 128, 64,   77, 66, 55, 254, // 上の行
            };
            var files = Files(d);
            files[YlpContent.CompositeName(PaintChannel.Color)] = YlpContent.EncodePng(rgba, 4, 2);
            files[YlpContent.CompositeName(PaintChannel.Height)] = YlpContent.EncodePng(rgba, 4, 2);
            string path = Import("Straight", YlpArchive.Write(files));
            Assert.That(Bytes(TextureAt(path, PaintChannel.Color)), Is.EqualTo(rgba), "sRGB texture: the PNG bytes as they are");
            Assert.That(Bytes(TextureAt(path, PaintChannel.Height)), Is.EqualTo(rgba), "linear texture: the PNG bytes as they are");
        }

        [Test] public void ColorAndEmissionAreSrgbAndDataChannelsAreLinear()
        {
            string path = Import("Space", ThreeChannelDocument());
            var color = TextureAt(path, PaintChannel.Color); var emission = TextureAt(path, PaintChannel.Emission); var height = TextureAt(path, PaintChannel.Height);
            Assert.That(color.isDataSRGB, Is.True);
            Assert.That(emission.isDataSRGB, Is.True);
            Assert.That(height.isDataSRGB, Is.False);
            foreach (var t in new[] { color, emission, height }) Assert.That(t.format, Is.EqualTo(TextureFormat.RGBA32));
        }

        [Test] public void MaterialsKeepTheirReferencesAndSeeNewPixelsAfterTheFileIsRewritten()
        {
            var first = ThreeChannelDocument(shade: 200);
            string path = Import("Linked", first);
            // 主オブジェクト（Color）とサブアセット（Height）をそれぞれ参照するマテリアル
            var materials = new Dictionary<PaintChannel, string> { { PaintChannel.Color, folder + "/LinkedColor.mat" }, { PaintChannel.Height, folder + "/LinkedHeight.mat" } };
            var before = new Dictionary<PaintChannel, long>();
            foreach (var entry in materials)
            {
                var material = new Material(Shader.Find("Unlit/Texture")) { mainTexture = TextureAt(path, entry.Key) };
                AssetDatabase.CreateAsset(material, entry.Value);
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material.mainTexture, out string guid, out long localId), Is.True);
                Assert.That(guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(path)));
                before.Add(entry.Key, localId);
            }
            AssetDatabase.SaveAssets();

            var second = ThreeChannelDocument(shade: 30);
            foreach (var channel in materials.Keys) Assert.That(second.Composite(channel), Is.Not.EqualTo(first.Composite(channel)));
            Import("Linked", second);

            foreach (var entry in materials)
            {
                Resources.UnloadAsset(AssetDatabase.LoadAssetAtPath<Material>(entry.Value)); // ディスクのマテリアルから参照を解決し直す
                var texture = AssetDatabase.LoadAssetAtPath<Material>(entry.Value).mainTexture as Texture2D;
                Assert.That(texture, Is.Not.Null, entry.Key + ": the reference survives the reimport");
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out string guid, out long localId), Is.True);
                Assert.That(guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(path)));
                Assert.That(localId, Is.EqualTo(before[entry.Key]), entry.Key + ": the sub-asset identifier is the channel name, so its file ID is stable");
                Assert.That(texture, Is.SameAs(TextureAt(path, entry.Key)));
                AssertSamePixels(texture, second, entry.Key);
            }
        }

        [Test] public void ImporterSettingsTakeEffect()
        {
            var d = ThreeChannelDocument();
            string path = Import("Settings", d);
            var color = TextureAt(path, PaintChannel.Color);
            Assert.That(color.mipmapCount, Is.GreaterThan(1), "mip maps by default");
            Assert.That(color.filterMode, Is.EqualTo(FilterMode.Bilinear)); Assert.That(color.wrapMode, Is.EqualTo(TextureWrapMode.Repeat)); Assert.That(color.anisoLevel, Is.EqualTo(1));
            Assert.That(color.isReadable, Is.True);

            var importer = (YlpImporter)AssetImporter.GetAtPath(path);
            importer.generateMipMaps = false; importer.filterMode = FilterMode.Point; importer.wrapMode = TextureWrapMode.Clamp; importer.anisoLevel = 4;
            importer.compression = YlpImporter.TextureCompression.Compressed;
            Reimport(importer);
            foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Height, PaintChannel.Emission })
            {
                var t = TextureAt(path, channel);
                Assert.That(t.mipmapCount, Is.EqualTo(1), channel + " has no mip maps");
                Assert.That(t.format, Is.EqualTo(YlpImporter.CompressedFormat), channel + " is compressed");
                Assert.That(t.isDataSRGB, Is.EqualTo(YlpContent.IsColor(channel)), channel + " keeps its colour space");
                Assert.That(t.filterMode, Is.EqualTo(FilterMode.Point)); Assert.That(t.wrapMode, Is.EqualTo(TextureWrapMode.Clamp)); Assert.That(t.anisoLevel, Is.EqualTo(4));
            }

            importer = (YlpImporter)AssetImporter.GetAtPath(path);
            Assert.That(importer.compression, Is.EqualTo(YlpImporter.TextureCompression.Compressed), "the settings are kept in the .meta");
            importer.compression = YlpImporter.TextureCompression.None; importer.generateMipMaps = true;
            Reimport(importer);
            AssertSamePixels(TextureAt(path, PaintChannel.Color), d, PaintChannel.Color);
            Assert.That(TextureAt(path, PaintChannel.Color).mipmapCount, Is.GreaterThan(1));
        }

        [Test] public void CompressionThatCannotApplyWarnsAndKeepsTheUncompressedPixels()
        {
            var d = ThreeChannelDocument(30, 30);
            string path = Import("Odd", d);
            var importer = (YlpImporter)AssetImporter.GetAtPath(path);
            importer.compression = YlpImporter.TextureCompression.Compressed;
            foreach (var name in new[] { "Odd Color", "Odd Height", "Odd Emission" })
                LogAssert.Expect(LogType.Warning, new Regex(name + " is kept uncompressed: BC7 needs a width and height that are multiples of 4 \\(the image is 30x30\\)"));
            Reimport(importer);
            foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Height, PaintChannel.Emission })
            {
                var t = TextureAt(path, channel);
                Assert.That(t.format, Is.EqualTo(TextureFormat.RGBA32), channel + " stays uncompressed");
                AssertSamePixels(t, d, channel);
            }
        }

        [Test] public void WithoutCompositesTheTexturesAreBuiltFromTheNativeDocument()
        {
            var d = ThreeChannelDocument();
            Assert.That(YlpArchive.Read(YlpArchive.Write(Files(d, false))).Keys, Is.EqualTo(new[] { YlpArchive.NativeName }));
            string path = Import("NativeOnly", d, composites: false);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(path), Is.SameAs(TextureAt(path, PaintChannel.Color)));
            Assert.That(YlpImporter.LoadInfo(path).fromNativeDocument, Is.True);
            foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Height, PaintChannel.Emission }) AssertSamePixels(TextureAt(path, channel), d, channel);
            Assert.That(TextureAt(path, PaintChannel.Height).isDataSRGB, Is.False);
        }

        [Test] public void ADocumentWithoutLayersStillGivesATransparentColorTexture()
        {
            var d = new PaintDocument(16, 8, 32);
            string path = Import("Empty", d);
            var main = AssetDatabase.LoadMainAssetAtPath(path) as Texture2D;
            Assert.That(main, Is.SameAs(TextureAt(path, PaintChannel.Color)));
            Assert.That(Bytes(main).All(b => b == 0), Is.True);
            Assert.That(main.width, Is.EqualTo(16)); Assert.That(main.height, Is.EqualTo(8));
        }

        [Test] public void AnUnusableCompositeIsRebuiltFromTheNativeDocumentWithAWarning()
        {
            var d = ThreeChannelDocument();
            var files = Files(d);
            files[YlpContent.CompositeName(PaintChannel.Height)] = new byte[] { 1, 2, 3, 4, 5 };
            LogAssert.Expect(LogType.Warning, new Regex("composite/Height.png is not a PNG image.*rebuilt from the native document"));
            string path = Import("BrokenPng", YlpArchive.Write(files));
            foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Height, PaintChannel.Emission }) AssertSamePixels(TextureAt(path, channel), d, channel);

            files = Files(d);
            files[YlpContent.CompositeName(PaintChannel.Emission)] = YlpContent.EncodePng(new byte[8 * 8 * 4], 8, 8);
            LogAssert.Expect(LogType.Warning, new Regex("composite/Emission.png is 8x8 but composite/Color.png is 64x48"));
            path = Import("MixedSizes", YlpArchive.Write(files));
            AssertSamePixels(TextureAt(path, PaintChannel.Emission), d, PaintChannel.Emission);
            Assert.That(YlpImporter.LoadInfo(path).fromNativeDocument, Is.True);
        }

        [Test] public void AnUnreadableFileLogsTheReasonAndKeepsAPlaceholder()
        {
            var valid = YlpArchive.Write(Files(ThreeChannelDocument()));
            var cases = new[]
            {
                ("Garbage", Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray(), "YoluPainter could not import .*Garbage\\.ylp: Not a YoluPainter file"),
                ("Truncated", valid.Take(valid.Length / 2).ToArray(), "YoluPainter could not import .*Truncated\\.ylp: .*not a readable zip archive"),
                ("Tampered", Tamper(valid), "YoluPainter could not import .*Tampered\\.ylp: Checksum mismatch: composite/Color\\.png"),
            };
            foreach (var (name, bytes, reason) in cases)
            {
                LogAssert.Expect(LogType.Error, new Regex(reason));
                string path = null;
                Assert.DoesNotThrow(() => path = Import(name, bytes), name);
                var main = AssetDatabase.LoadMainAssetAtPath(path) as Texture2D;
                Assert.That(main, Is.Not.Null, name + ": the asset still exists");
                Assert.That(main.width, Is.EqualTo(1));
                var info = YlpImporter.LoadInfo(path);
                Assert.That(info.channels, Is.Empty, name + ": the inspector reports that nothing was imported");
                Assert.That(info.error, Does.Match(reason.Substring(reason.IndexOf(": ", StringComparison.Ordinal) + 2)), name + ": and why");
                Assert.That(YlpImporter.LoadTextures(path), Is.Empty);
                Assert.That(File.ReadAllBytes(Path.GetFullPath(path)), Is.EqualTo(bytes), name + ": the file is never modified");
            }
        }

        /// <summary>無圧縮で入っている合成済み PNG の中ほどの 1 バイトを変える（zip としては読めるが中身が manifest と合わない）。</summary>
        static byte[] Tamper(byte[] ylp)
        {
            var bytes = (byte[])ylp.Clone();
            var name = System.Text.Encoding.ASCII.GetBytes(YlpContent.CompositeName(PaintChannel.Color));
            for (int i = 0; i + name.Length < bytes.Length; i++)
                if (bytes.Skip(i).Take(name.Length).SequenceEqual(name)) { bytes[i + name.Length + 40] ^= 0xFF; return bytes; }
            throw new InvalidOperationException("composite entry not found");
        }

        [Test] public void ImportingDoesNotModifyTheFile()
        {
            var bytes = YlpArchive.Write(Files(ThreeChannelDocument()));
            string path = Import("Untouched", bytes);
            var importer = (YlpImporter)AssetImporter.GetAtPath(path);
            importer.compression = YlpImporter.TextureCompression.Compressed; Reimport(importer);
            Assert.That(File.ReadAllBytes(Path.GetFullPath(path)), Is.EqualTo(bytes));
        }

        [Test] public void OpenAssetLeavesOtherAssetsToUnity()
        {
            var material = new Material(Shader.Find("Unlit/Texture"));
            AssetDatabase.CreateAsset(material, folder + "/Other.mat");
            Assert.That(YlpImporter.OpenAsset(material.GetInstanceID()), Is.False);
            Assert.That(YlpImporter.OpenAsset(0), Is.False);
            Assert.That(YlpImporter.IsYlp("Assets/a.YLP"), Is.True);
            Assert.That(YlpImporter.IsYlp("Assets/a.ylp.png"), Is.False);
        }

        [Test, Category("Window")] public void OpenAssetOpensAYlpInThePainter()
        {
            if (Application.isBatchMode) Assert.Ignore("Opening the painter window needs a non-batch Editor (test-daemon.sh start in GUI mode).");
            if (Resources.FindObjectsOfTypeAll<TexturePaintWindow>().Length > 0) Assert.Ignore("A painter window is already open; this test only checks a freshly opened one.");
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            string project = Path.Combine(Path.GetTempPath(), "yolupainter-ylp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(project); PainterSettings.ProjectRoot = project;
            string path = Import("Open", ThreeChannelDocument());
            try
            {
                Assert.That(YlpImporter.OpenAsset(AssetDatabase.LoadMainAssetAtPath(path).GetInstanceID()), Is.True);
                Assert.That(Resources.FindObjectsOfTypeAll<TexturePaintWindow>().Length, Is.EqualTo(1), "double-clicking a .ylp opens the painter");
                var sub = TextureAt(path, PaintChannel.Height);
                Assert.That(YlpImporter.OpenAsset(sub.GetInstanceID()), Is.True, "a channel texture inside the .ylp opens the same file");
                Assert.That(Resources.FindObjectsOfTypeAll<TexturePaintWindow>().Length, Is.EqualTo(1), "the existing window is reused");
            }
            finally
            {
                foreach (var w in Resources.FindObjectsOfTypeAll<TexturePaintWindow>())
                {
                    string recovery = w.RecoveryRoot; w.Close();
                    if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
                }
                EditorShaderCompiler.TolerateErrorLogsIfBroken();
                PainterSettings.ProjectRoot = null;
                if (Directory.Exists(project)) Directory.Delete(project, true);
            }
        }
    }
}
