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
    /// 設定・選択範囲・全チャンネルの合成）、形式 2 は同じ中身に ylp.json と取り込んだ PSD の原本を足したもの（Fixtures~/format2.ylp）で
    /// 確かめる。どちらも形式 3（テクスチャセット）では 1 つのセットとして開き、書き戻しても正本はバイト一致。
    /// </summary>
    public sealed class YlpFormatTests
    {
        static readonly YlpWriterInfo Saver = new YlpWriterInfo("YoluPainter", "1.2.3", "2022.3.22f1");
        static string Fixture(string name) => PackagePaths.Physical("Tests/Editor/Persistence/Fixtures~/" + name);
        /// <summary>フィクスチャの正本（版 10）を今の版の数にしたもの。版 11 は Generator の段を持つ文書だけ並びが違うので、持たない文書は
        /// 版の数のほかはバイト一致で書き戻る。</summary>
        static byte[] AtCurrentVersion(byte[] native)
        {
            Assert.That(BitConverter.ToInt32(native, 8), Is.EqualTo(10), "the fixtures were saved with native version 10");
            var bytes = (byte[])native.Clone(); BitConverter.GetBytes(DocumentBinary.CurrentVersion).CopyTo(bytes, 8);
            return bytes;
        }
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
            var id = Guid.NewGuid();
            var files = new Dictionary<string, byte[]>
            {
                { YlpFormat.ProjectName, YlpFormat.WriteProject(new YlpProjectInfo(new[] { new YlpTextureSetInfo(id, "Body", 0) }, id)) },
                { YlpFormat.SetEntry(id, YlpArchive.NativeName), new byte[] { 1 } }, { "future/thing.bin", new byte[] { 2 } }, { YlpFormat.SetEntry(id, "composite/Glow.png"), new byte[] { 3 } },
                { YlpArchive.NativeName, new byte[] { 4 } },
            };
            YlpFormat.Stamp(files, Saver, null);
            var opened = YlpFormat.Open(files);
            Assert.That(opened.Info.Format, Is.EqualTo(YlpFormat.Current)); Assert.That(opened.Upgraded, Is.False);
            Assert.That(opened.Files.ContainsKey(YlpFormat.InfoName), Is.False, "the record is read into Info, not passed on");
            Assert.That(opened.UnknownEntries, Is.EqualTo(new[] { YlpArchive.NativeName, "future/thing.bin", YlpFormat.SetEntry(id, "composite/Glow.png") }),
                "an unknown channel's composite is unknown too, and so is a document where format 3 does not keep one");
            Assert.That(files.ContainsKey("future/thing.bin"), Is.True, "Open does not change the dictionary it was given");
            string set = YlpFormat.SetFolder(id);
            Assert.That(YlpFormat.KindOf(YlpFormat.ProjectName), Is.EqualTo(YlpFormat.EntryKind.Source), "the list of texture sets cannot be lost");
            Assert.That(YlpFormat.KindOf(set + "document.utpaint"), Is.EqualTo(YlpFormat.EntryKind.Source));
            Assert.That(YlpFormat.KindOf(set + "selection.bin"), Is.EqualTo(YlpFormat.EntryKind.Source));
            Assert.That(YlpFormat.KindOf(set + "imported-original.psd"), Is.EqualTo(YlpFormat.EntryKind.Source));
            Assert.That(YlpFormat.KindOf("view.json"), Is.EqualTo(YlpFormat.EntryKind.State));
            Assert.That(YlpFormat.KindOf(set + "composite/Normal.png"), Is.EqualTo(YlpFormat.EntryKind.Derived));
            Assert.That(YlpFormat.KindOf(set + "meshmap-AmbientOcclusion.bin"), Is.EqualTo(YlpFormat.EntryKind.Derived));
            Assert.That(YlpFormat.KindOf("thumbnail.png"), Is.EqualTo(YlpFormat.EntryKind.Derived));
            foreach (var formerlyAtTheRoot in new[] { "document.utpaint", "selection.bin", "composite/Normal.png", "meshmap-AmbientOcclusion.bin", set + "view.json", set + "sub/x.bin" })
                Assert.That(YlpFormat.KindOf(formerlyAtTheRoot), Is.Null, formerlyAtTheRoot);
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
            var files = OneTextureSet(opened);
            var native = files[YlpArchive.NativeName];
            var document = DocumentBinary.Read(native);
            Assert.That(opened.Project.Sets.Single().Id, Is.EqualTo(document.Id), "the one texture set is named after the document");
            Assert.That(document.Layers.Select(l => l.Name), Is.EquivalentTo(new[] { "Paint", "Group", "Inner", "Fill", "Invert" }));
            Assert.That(document.GetLayer(document.Layers.Single(l => l.Name == "Inner").ParentId).Name, Is.EqualTo("Group"));
            Assert.That(document.Layers.Single(l => l.Name == "Paint").Mask, Is.Not.Null);
            Assert.That(document.NormalSettings.DeriveFromHeight, Is.True);
            Assert.That(DocumentBinary.Write(document), Is.EqualTo(AtCurrentVersion(native)), "the native source reads and writes back byte for byte");
            Assert.That(SelectionBinary.Read(files[SelectionBinary.EntryName], document).IsEmpty, Is.False);
            AssertCompositesMatch(files, document);
            AssertWritesBackAsTheCurrentFormat(opened);
        }

        /// <summary>形式 2 のファイル（形式 1 と同じ中身に ylp.json と取り込んだ PSD の原本）: 1 つのテクスチャセットとして開き、正本・選択範囲・
        /// PSD の原本はセットの下、view.json とサムネイルは根に残る。</summary>
        [Test] public void AFormatTwoFileOpensAsOneTextureSet()
        {
            var snapshot = YlpStore.Load(Fixture("format2.ylp"));
            var opened = YlpFormat.Open(snapshot.Files);
            Assert.That(opened.Info.Format, Is.EqualTo(2)); Assert.That(opened.Upgraded, Is.True);
            Assert.That(opened.Info.SavedBy.App, Is.EqualTo("YoluPainter")); Assert.That(opened.Info.CreatedBy, Is.Not.Null);
            Assert.That(opened.UnknownEntries, Is.Empty); Assert.That(opened.Notes, Is.Empty);
            var files = OneTextureSet(opened);
            Assert.That(files.Keys, Is.SupersetOf(new[] { YlpArchive.NativeName, SelectionBinary.EntryName, YlpContent.ImportedOriginalName }));
            Assert.That(files[YlpContent.ImportedOriginalName], Is.EqualTo(snapshot.Files[YlpContent.ImportedOriginalName]), "the imported PSD's original bytes move with the set");
            Assert.That(opened.Files.ContainsKey(YlpContent.ViewName) && opened.Files.ContainsKey(YlpContent.ThumbnailName), Is.True, "view.json and the thumbnail stay at the root");
            var native = files[YlpArchive.NativeName];
            Assert.That(native, Is.EqualTo(snapshot.Files[YlpArchive.NativeName]));
            var document = DocumentBinary.Read(native);
            Assert.That(opened.Project.Sets.Single().Id, Is.EqualTo(document.Id));
            Assert.That(DocumentBinary.Write(document), Is.EqualTo(AtCurrentVersion(native)), "the native source reads and writes back byte for byte");
            Assert.That(SelectionBinary.Write(SelectionBinary.Read(files[SelectionBinary.EntryName], document)), Is.EqualTo(files[SelectionBinary.EntryName]));
            AssertCompositesMatch(files, document);
            AssertWritesBackAsTheCurrentFormat(opened);
        }

        /// <summary>移行した 1 つのテクスチャセット（スロットは view.json の 0、名前は移行の仮の名前、今のセット）のエントリ。</summary>
        static Dictionary<string, byte[]> OneTextureSet(YlpOpened opened)
        {
            var set = opened.Project.Sets.Single();
            Assert.That(set.MaterialSlot, Is.Zero); Assert.That(set.Name, Is.EqualTo(YlpFormat.MigratedSetName)); Assert.That(opened.Project.CurrentSet, Is.EqualTo(set.Id));
            Assert.That(opened.Files.Keys.Where(k => !k.StartsWith(YlpFormat.SetsFolder, StringComparison.Ordinal)), Is.EquivalentTo(new[] { YlpFormat.ProjectName, YlpFormat.ViewName, YlpFormat.BrushName, YlpFormat.ThumbnailName }));
            return opened.SetFiles(set.Id);
        }

        /// <summary>移した中身を今の形式で書いて読み直すと、同じセット・同じバイト列（保存しても正本は変わらない）。</summary>
        static void AssertWritesBackAsTheCurrentFormat(YlpOpened opened)
        {
            var files = new Dictionary<string, byte[]>(opened.Files);
            YlpFormat.Stamp(files, Saver, opened.Info.CreatedBy);
            var again = YlpFormat.Open(YlpArchive.Read(YlpArchive.Write(files)));
            Assert.That(again.Info.Format, Is.EqualTo(YlpFormat.Current)); Assert.That(again.Upgraded, Is.False); Assert.That(again.UnknownEntries, Is.Empty);
            Assert.That(again.Project.Sets.Select(s => (s.Id, s.Name, s.MaterialSlot)), Is.EqualTo(opened.Project.Sets.Select(s => (s.Id, s.Name, s.MaterialSlot))));
            foreach (var entry in opened.Files) Assert.That(again.Files[entry.Key], Is.EqualTo(entry.Value), entry.Key);
        }

        static void AssertCompositesMatch(Dictionary<string, byte[]> files, PaintDocument document)
        {
            foreach (var channel in YlpContent.UsedChannels(document))
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                try
                {
                    Assert.That(texture.LoadImage(files[YlpContent.CompositeName(channel)], false), Is.True);
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
