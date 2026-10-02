using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// .ylp の中身の形式（YlpFormat、Documentation~/YLP_FORMAT.md）: ylp.json の読み書きと拒否、古い形式を開くと今の並びになること、
    /// 新しすぎる形式はどのエントリにも触れずに断ること、知らないエントリを知らせること。形式 1 のファイルは、形式を足す前の
    /// YoluPainter で作って固めたフィクスチャ（Fixtures~/format1.ylp。層 5 枚・マスク・グループ・塗りつぶし層・調整層・Normal の
    /// 設定・選択範囲・全チャンネルの合成）で確かめる。
    /// </summary>
    public sealed class YlpFormatTests
    {
        static readonly YlpWriterInfo Saver = new YlpWriterInfo("YoluPainter", "1.2.3", "2022.3.22f1");
        static string Fixture(string name) => PackagePaths.Physical("Tests/Editor/Persistence/Fixtures~/" + name);
        static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

        [Test] public void TheRecordRoundTripsWithAndWithoutTheCreator()
        {
            var read = YlpFormat.ReadInfo(YlpFormat.WriteInfo(new YlpFormatInfo(YlpFormat.Current, Saver, new YlpWriterInfo("YoluPainter", "0.0.0", "2022.3.0f1"))));
            Assert.That(read.Format, Is.EqualTo(YlpFormat.Current));
            Assert.That(read.SavedBy.ToString(), Is.EqualTo("YoluPainter 1.2.3 (Unity 2022.3.22f1)"));
            Assert.That(read.CreatedBy.Version, Is.EqualTo("0.0.0"));
            var anonymous = YlpFormat.ReadInfo(YlpFormat.WriteInfo(new YlpFormatInfo(YlpFormat.Current, Saver, null)));
            Assert.That(anonymous.CreatedBy, Is.Null, "a file first written by a version that did not record itself keeps no creator");
            var quoted = YlpFormat.ReadInfo(YlpFormat.WriteInfo(new YlpFormatInfo(2, new YlpWriterInfo("Yolu \"Painter\"\\", "1\n2", "u"), null)));
            Assert.That(quoted.SavedBy.App, Is.EqualTo("Yolu \"Painter\"\\")); Assert.That(quoted.SavedBy.Version, Is.EqualTo("1\n2"));
        }

        [Test] public void UnknownKeysAreIgnoredAndBrokenRecordsAreRefused()
        {
            var extra = YlpFormat.ReadInfo(Json("{ \"format\": 2, \"future\": [1, 2.5, {\"x\": null}, true], \"savedBy\": { \"app\": \"A\", \"version\": \"1\", \"unity\": \"u\", \"os\": \"x\" } }"));
            Assert.That(extra.SavedBy.App, Is.EqualTo("A"), "keys a later minor version adds are skipped");
            foreach (var bad in new[]
            {
                "", "[]", "{", "{ \"format\": 2 }", "{ \"format\": 1, \"savedBy\": { \"app\": \"A\", \"version\": \"1\", \"unity\": \"u\" } }",
                "{ \"format\": \"2\", \"savedBy\": { \"app\": \"A\", \"version\": \"1\", \"unity\": \"u\" } }",
                "{ \"format\": 2, \"savedBy\": { \"app\": \"\", \"version\": \"1\", \"unity\": \"u\" } }",
                "{ \"format\": 2, \"savedBy\": { \"app\": \"A\", \"version\": 1, \"unity\": \"u\" } }",
                "{ \"format\": 2, \"savedBy\": \"YoluPainter\" }",
                "{ \"format\": 2, \"format\": 3, \"savedBy\": { \"app\": \"A\", \"version\": \"1\", \"unity\": \"u\" } }",
                "{ \"format\": 2, \"savedBy\": { \"app\": \"" + new string('a', 300) + "\", \"version\": \"1\", \"unity\": \"u\" } } ",
                "{ \"format\": 2, \"savedBy\": { \"app\": \"A\", \"version\": \"1\", \"unity\": \"u\" } } trailing",
                new string('[', 40) + new string(']', 40),
            })
                Assert.That(() => YlpFormat.ReadInfo(Json(bad)), Throws.TypeOf<InvalidDataException>(), bad.Length > 60 ? bad.Substring(0, 60) : bad);
            Assert.That(() => YlpFormat.ReadInfo(new byte[] { 0x7b, 0xff, 0x7d }), Throws.TypeOf<InvalidDataException>().With.Message.Contains("UTF-8"));
            Assert.That(() => YlpFormat.ReadInfo(new byte[70 * 1024]), Throws.TypeOf<InvalidDataException>());
        }

        [Test] public void ANewerFormatIsRefusedWithWhoWroteIt()
        {
            var files = new Dictionary<string, byte[]>
            {
                { YlpFormat.InfoName, Json("{ \"format\": " + (YlpFormat.Current + 1) + ", \"savedBy\": { \"app\": \"YoluPainter\", \"version\": \"9.9.9\", \"unity\": \"6000.0.1f1\" } }") },
                { YlpArchive.NativeName, new byte[] { 1, 2, 3 } },
            };
            Assert.That(() => YlpFormat.Open(files), Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("format " + (YlpFormat.Current + 1)).And.Message.Contains("YoluPainter 9.9.9").And.Message.Contains("reads up to format " + YlpFormat.Current));
        }

        [Test] public void AStampedFileOpensAsTheCurrentFormatAndUnknownEntriesAreReported()
        {
            var files = new Dictionary<string, byte[]> { { YlpArchive.NativeName, new byte[] { 1 } }, { "future/thing.bin", new byte[] { 2 } }, { "composite/Glow.png", new byte[] { 3 } } };
            YlpFormat.Stamp(files, Saver, null);
            var opened = YlpFormat.Open(files);
            Assert.That(opened.Info.Format, Is.EqualTo(YlpFormat.Current)); Assert.That(opened.Upgraded, Is.False);
            Assert.That(opened.Files.ContainsKey(YlpFormat.InfoName), Is.False, "the record is read into Info, not passed on");
            Assert.That(opened.UnknownEntries, Is.EqualTo(new[] { "composite/Glow.png", "future/thing.bin" }), "an unknown channel's composite is unknown too");
            Assert.That(files.ContainsKey("future/thing.bin"), Is.True, "Open does not change the dictionary it was given");
            Assert.That(YlpFormat.KindOf("document.utpaint"), Is.EqualTo(YlpFormat.EntryKind.Source));
            Assert.That(YlpFormat.KindOf("selection.bin"), Is.EqualTo(YlpFormat.EntryKind.Source));
            Assert.That(YlpFormat.KindOf("view.json"), Is.EqualTo(YlpFormat.EntryKind.State));
            Assert.That(YlpFormat.KindOf("composite/Normal.png"), Is.EqualTo(YlpFormat.EntryKind.Derived));
            Assert.That(YlpFormat.KindOf("meshmap-AmbientOcclusion.bin"), Is.EqualTo(YlpFormat.EntryKind.Derived));
        }

        /// <summary>形式を足す前の YoluPainter で作ったファイル: 形式 1 として開き、正本はバイト一致で読み書きでき、合成の画像は正本から作り直したものと同じ。</summary>
        [Test] public void AFormatOneFileFromBeforeTheRecordOpensUnchanged()
        {
            var snapshot = YlpStore.Load(Fixture("format1.ylp"));
            Assert.That(snapshot.Files.ContainsKey(YlpFormat.InfoName), Is.False);
            var opened = YlpFormat.Open(snapshot.Files);
            Assert.That(opened.Info.Format, Is.EqualTo(1)); Assert.That(opened.Upgraded, Is.True);
            Assert.That(opened.Info.SavedBy, Is.Null); Assert.That(opened.Info.CreatedBy, Is.Null);
            Assert.That(opened.UnknownEntries, Is.Empty);
            var native = opened.Files[YlpArchive.NativeName];
            var document = DocumentBinary.Read(native);
            Assert.That(document.Layers.Select(l => l.Name), Is.EquivalentTo(new[] { "Paint", "Group", "Inner", "Fill", "Invert" }));
            Assert.That(document.GetLayer(document.Layers.Single(l => l.Name == "Inner").ParentId).Name, Is.EqualTo("Group"));
            Assert.That(document.Layers.Single(l => l.Name == "Paint").Mask, Is.Not.Null);
            Assert.That(document.NormalSettings.DeriveFromHeight, Is.True);
            Assert.That(DocumentBinary.Write(document), Is.EqualTo(native), "the native source reads and writes back byte for byte");
            Assert.That(SelectionBinary.Read(opened.Files[SelectionBinary.EntryName], document).IsEmpty, Is.False);
            foreach (var channel in YlpContent.UsedChannels(document))
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                try
                {
                    Assert.That(texture.LoadImage(opened.Files[YlpContent.CompositeName(channel)], false), Is.True);
                    // LoadImage は PNG を ARGB32 にするので、並びではなく画素の値で比べる
                    var pixels = texture.GetPixels32(); var rgba = new byte[pixels.Length * 4];
                    for (int i = 0; i < pixels.Length; i++) { rgba[i * 4] = pixels[i].r; rgba[i * 4 + 1] = pixels[i].g; rgba[i * 4 + 2] = pixels[i].b; rgba[i * 4 + 3] = pixels[i].a; }
                    Assert.That(rgba, Is.EqualTo(YlpContent.Image(document, channel)), channel + " composite matches the native source");
                }
                finally { Object.DestroyImmediate(texture); }
            }
        }
    }
}
