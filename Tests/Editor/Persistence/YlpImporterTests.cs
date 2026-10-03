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
    /// <summary>.ylp の ScriptedImporter。テストごとに Assets の下へ一時フォルダを作り、実際に AssetDatabase で取り込む。.ylp は作業ファイルで、
    /// 配布先に YoluPainter があるとは限らないので、テクスチャ（マテリアルのテクスチャ欄に入るもの）を出さないことを確かめる。</summary>
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

        static void AssertNoTexture(string path, string because)
        {
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(path).OfType<Texture>(), Is.Empty, because);
            Assert.That(AssetDatabase.LoadAssetAtPath<Texture>(path), Is.Null, because);
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>(), Is.Empty, because);
        }

        [Test] public void NoTextureIsMadeAndTheMainObjectDescribesTheFile()
        {
            var d = ThreeChannelDocument();
            string path = Import("Three", d);
            AssertNoTexture(path, "a .ylp gives nothing a material texture slot accepts");
            var info = AssetDatabase.LoadMainAssetAtPath(path) as YlpImportInfo;
            Assert.That(info, Is.Not.Null); Assert.That(info, Is.SameAs(YlpImporter.LoadInfo(path)));
            Assert.That((info.width, info.height), Is.EqualTo((64, 48)));
            Assert.That(info.channels, Is.EqualTo(new[] { PaintChannel.Color, PaintChannel.Height, PaintChannel.Emission }));
            Assert.That(info.fromNativeDocument, Is.False); Assert.That(info.error, Is.Empty);
            var material = new Material(Shader.Find("Unlit/Texture"));
            try
            {
                Assert.That(() => material.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(path)), Throws.Nothing);
                Assert.That(material.mainTexture, Is.Null, "even a direct assignment finds no texture in the .ylp");
            }
            finally { Object.DestroyImmediate(material); }
        }

        [Test] public void WithoutUsableCompositesTheNativeDocumentIsRead()
        {
            var d = ThreeChannelDocument();
            string path = Import("NativeOnly", d, composites: false);
            var info = YlpImporter.LoadInfo(path);
            Assert.That(info.fromNativeDocument, Is.True);
            Assert.That(info.channels, Is.EqualTo(new[] { PaintChannel.Color, PaintChannel.Height, PaintChannel.Emission }));
            AssertNoTexture(path, "native only");

            var files = Files(d);
            files[YlpContent.CompositeName(PaintChannel.Height)] = new byte[] { 1, 2, 3, 4, 5 };
            LogAssert.Expect(LogType.Warning, new Regex("composite/Height.png is not a PNG image.*read from the native document"));
            path = Import("BrokenPng", YlpArchive.Write(files));
            Assert.That(YlpImporter.LoadInfo(path).fromNativeDocument, Is.True);

            files = Files(d);
            files[YlpContent.CompositeName(PaintChannel.Emission)] = YlpContent.EncodePng(new byte[8 * 8 * 4], 8, 8);
            LogAssert.Expect(LogType.Warning, new Regex("is 8x8 but the other composite images are 64x48|is 64x48 but the other composite images are 8x8"));
            path = Import("MixedSizes", YlpArchive.Write(files));
            Assert.That((YlpImporter.LoadInfo(path).width, YlpImporter.LoadInfo(path).height), Is.EqualTo((64, 48)));

            var empty = Import("Empty", new PaintDocument(16, 8, 32));
            Assert.That(YlpImporter.LoadInfo(empty).channels, Is.Empty); Assert.That(YlpImporter.LoadInfo(empty).error, Is.Empty);
            Assert.That((YlpImporter.LoadInfo(empty).width, YlpImporter.LoadInfo(empty).height), Is.EqualTo((16, 8)));
        }

        [Test] public void TheThumbnailIsDrawnFromTheFileAndNotStoredInTheAsset()
        {
            var d = ThreeChannelDocument();
            string path = Import("Thumb", d);
            var thumbnail = YlpImporter.LoadThumbnail(path);
            try
            {
                Assert.That(thumbnail, Is.Not.Null);
                Assert.That((thumbnail.width, thumbnail.height), Is.EqualTo((64, 48)), "small documents keep their size in the thumbnail");
            }
            finally { Object.DestroyImmediate(thumbnail); }
            var editor = UnityEditor.Editor.CreateEditor(AssetDatabase.LoadMainAssetAtPath(path));
            try
            {
                var icon = editor.RenderStaticPreview(path, new Object[0], 32, 32);
                Assert.That(icon, Is.Not.Null); Assert.That((icon.width, icon.height), Is.EqualTo((32, 32)));
                Assert.That(icon.GetPixels32().Any(p => p.a > 0), Is.True);
                Object.DestroyImmediate(icon);
                Assert.That(editor.HasPreviewGUI(), Is.True);
            }
            finally { Object.DestroyImmediate(editor); }
            AssertNoTexture(path, "the preview is not part of the asset");
            var bare = Import("NoThumb", d, composites: false);
            Assert.That(YlpImporter.LoadThumbnail(bare), Is.Null, "a file without thumbnail.png has no preview");
        }

        [Test] public void AnUnreadableFileLogsTheReasonAndKeepsTheAsset()
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
                var info = AssetDatabase.LoadMainAssetAtPath(path) as YlpImportInfo;
                Assert.That(info, Is.Not.Null, name + ": the asset still exists");
                Assert.That(info.channels, Is.Empty, name + ": the inspector reports that nothing was imported");
                Assert.That(info.error, Does.Match(reason.Substring(reason.IndexOf(": ", StringComparison.Ordinal) + 2)), name + ": and why");
                AssertNoTexture(path, name);
                Assert.That(File.ReadAllBytes(Path.GetFullPath(path)), Is.EqualTo(bytes), name + ": the file is never modified");
            }
        }

        [Test] public void ImportingDoesNotModifyTheFile()
        {
            var bytes = YlpArchive.Write(Files(ThreeChannelDocument()));
            string path = Import("Untouched", bytes);
            AssetImporter.GetAtPath(path).SaveAndReimport();
            Assert.That(File.ReadAllBytes(Path.GetFullPath(path)), Is.EqualTo(bytes));
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
                Assert.That(YlpImporter.OpenAsset(AssetDatabase.LoadMainAssetAtPath(path).GetInstanceID()), Is.True, "opening again uses the same window");
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
