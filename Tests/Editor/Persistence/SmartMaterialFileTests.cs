using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// スマートマテリアルのファイル（.ylsmart）と .ylp の形式 5: ファイルは版付きで、新しい版・新しい manifest・知らない種類・知らない
    /// チャンネル・層と合わない smart.json・読めない層・壊れた画像・ほかの種類のファイルは理由を出して断る（黙って落とさない）。並びだけを
    /// 読む（層を読まない）。.ylp の形式 5 は resources.json にスマートマテリアル・スマートマスクの種類と resources/&lt;SHA-256&gt;.ylsmart を持ち、
    /// 書き戻すと同じバイト、ファイルが無い・別のファイル・種類が違う・壊れたもの・持てない出どころは断り、並びに無いファイルは知らないエントリと
    /// して知らせる。形式 4 のフィクスチャ（形式 5 を足す前の YoluPainter のウィンドウで保存した、セット 2 つ・リソース 2 つのもの）は形式 4 として
    /// 開け、今の形式で書き戻してもどのエントリも同じバイト。
    /// </summary>
    public sealed class SmartMaterialFileTests
    {
        static readonly YlpWriterInfo App = new YlpWriterInfo("YoluPainter", "0.0.0-test", "2022.3.22f1");
        static readonly Guid A = new Guid("0f1e2d3c-4b5a-4978-8796-a5b4c3d2e1f0");
        static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
        static string Text(byte[] b) => Encoding.UTF8.GetString(b);
        static string Fixture(string name) => PackagePaths.Physical("Tests/Editor/Persistence/Fixtures~/" + name);

        static SmartMaterial Material()
        {
            var d = new PaintDocument(32, 24, 16);
            var l = d.AddLayer("paint"); l.GetChannel(PaintChannel.Color).SetPixel(3, 4, new Rgba32(1, 2, 3, 0));
            var f = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Roughness, new Rgba32(9, 9, 9, 255) } });
            d.AddLayerMask(f.Id); d.AddFilter(f.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Dirt)));
            return d.CaptureSmartMaterial(new[] { l.Id, f.Id }, "Test material");
        }

        /// <summary>中身のエントリを変えて書き直したファイル。</summary>
        static byte[] Edited(byte[] file, Action<Dictionary<string, byte[]>> edit)
        {
            var files = SmartMaterialFile.ReadArchive(file); edit(files); return SmartMaterialFile.WriteArchive(files);
        }
        static byte[] EditInfo(byte[] file, string from, string to) => Edited(file, f =>
        {
            string info = Text(f[SmartMaterialFile.InfoName]);
            Assert.That(info.Contains(from), Is.True, from + " in " + info);
            f[SmartMaterialFile.InfoName] = Utf8(info.Replace(from, to));
        });

        [Test] public void TheFileListsWhatItIsAndReadsWithoutItsLayers()
        {
            var m = Material();
            var thumbnail = RgbaPng.Encode(new byte[4 * 4 * 4], 4, 4);
            var file = SmartMaterialFile.Write(m, App, thumbnail);
            Assert.That(SmartMaterialFile.LooksLike(file), Is.True);
            var entries = SmartMaterialFile.ReadArchive(file);
            Assert.That(entries.Keys, Is.EquivalentTo(new[] { SmartMaterialFile.InfoName, SmartMaterialFile.LayersName, SmartMaterialFile.ThumbnailName }));
            string info = Text(entries[SmartMaterialFile.InfoName]);
            Assert.That(info, Does.Contain("\"format\": 1").And.Contain("\"kind\": \"smartMaterial\"").And.Contain("\"channels\": [\"Color\", \"Roughness\"]").And.Contain("\"savedBy\""));
            var summary = SmartMaterialFile.ReadInfo(file);
            Assert.That((summary.Format, summary.Kind, summary.Name, summary.Width, summary.Height, summary.LayerCount), Is.EqualTo((1, SmartKind.Material, "Test material", 32, 24, 2)));
            Assert.That(summary.Channels, Is.EqualTo(new[] { PaintChannel.Color, PaintChannel.Roughness }));
            Assert.That(summary.Thumbnail, Is.EqualTo(thumbnail)); Assert.That(summary.SavedBy.Version, Is.EqualTo("0.0.0-test"));
            // 並びだけを読むときは層を読まない（層が壊れていても並びは読める）
            var brokenLayers = Edited(file, f => f[SmartMaterialFile.LayersName] = new byte[] { 1, 2, 3 });
            Assert.That(SmartMaterialFile.ReadInfo(brokenLayers).Name, Is.EqualTo("Test material"));
            Assert.That(() => SmartMaterialFile.Read(brokenLayers), Throws.TypeOf<InvalidDataException>().With.Message.Contains("layers cannot be read"));
            // 同じ中身はいつも同じバイト列（zip の日時を決めてある）
            Assert.That(SmartMaterialFile.Write(m, App, thumbnail), Is.EqualTo(file));
            // 知らないエントリは読み飛ばす（ファイルのバイト列はそのまま持つので、何も落とさない）
            var extra = Edited(file, f => f["future.bin"] = new byte[] { 7 });
            Assert.That(SmartMaterialFile.Read(extra).LayerCount, Is.EqualTo(2));
        }

        [Test] public void NewerFormatsUnknownValuesAndBrokenFilesAreRefusedWithTheReason()
        {
            var file = SmartMaterialFile.Write(Material(), App);
            var cases = new Dictionary<string, (byte[] bytes, string reason)>
            {
                { "a newer format", (EditInfo(file, "\"format\": 1", "\"format\": 2"), "uses format 2") },
                { "an unknown kind", (EditInfo(file, "\"smartMaterial\"", "\"smartBrush\""), "kind \"smartBrush\"") },
                { "an unknown channel", (EditInfo(file, "\"Roughness\"", "\"Sheen\""), "\"Sheen\"") },
                { "a channel twice", (EditInfo(file, "[\"Color\", \"Roughness\"]", "[\"Color\", \"Color\"]"), "twice") },
                { "another size", (EditInfo(file, "\"width\": 32", "\"width\": 33"), "does not match") },
                { "other channels", (EditInfo(file, "[\"Color\", \"Roughness\"]", "[\"Color\"]"), "does not match") },
                { "no name", (EditInfo(file, "\"name\": \"Test material\"", "\"name\": \" \""), "name") },
                { "no savedBy", (Edited(file, f => f[SmartMaterialFile.InfoName] = Utf8(Text(f[SmartMaterialFile.InfoName]).Split(new[] { ",\n  \"savedBy\"" }, StringSplitOptions.None)[0] + "\n}\n")), "savedBy") },
                { "a repin that is not a generator", (EditInfo(file, "\"repin\": []", "\"repin\": [\"" + Guid.NewGuid().ToString("D") + "\"]"), "not a generator") },
                { "a broken repin", (EditInfo(file, "\"repin\": []", "\"repin\": [\"x\"]"), "repin") },
                { "not JSON", (Edited(file, f => f[SmartMaterialFile.InfoName] = Utf8("{ nope")), "JSON") },
                { "a mask that is not one masked layer", (EditInfo(file, "\"smartMaterial\"", "\"smartMask\""), "one fill layer") },
                { "an image list in a mask file", (Edited(file, f => f[ResourceIndex.EntryName] = Utf8("{ \"resources\": [ { \"id\": \"" + A.ToString("D") + "\", \"kind\": \"smartMask\", \"name\": \"M\", \"content\": \"" + new string('a', 64) + "\", \"length\": 5 } ] }")), "not an image") },
            };
            foreach (var c in cases)
            {
                var ex = Assert.Throws<InvalidDataException>(() => SmartMaterialFile.Read(c.Value.bytes), c.Key);
                if (c.Value.reason != null) Assert.That(ex.Message, Does.Contain(c.Value.reason), c.Key);
            }
            Assert.That(Assert.Throws<InvalidDataException>(() => SmartMaterialFile.Read(EditInfo(file, "\"format\": 1", "\"format\": 2"))).Message, Does.Contain("reads up to format 1").And.Contain("0.0.0-test"));
            // 層の無いファイルは書けず、manifest に層の無いファイル（手で作ったもの）は読まない
            var noLayers = SmartMaterialFile.ReadArchive(file); noLayers.Remove(SmartMaterialFile.LayersName);
            Assert.That(() => SmartMaterialFile.WriteArchive(noLayers), Throws.ArgumentException);
            var all = new List<(string, byte[])> { ("mimetype", Encoding.ASCII.GetBytes(SmartMaterialFile.MimeType)) };
            var manifest = new StringBuilder(SmartMaterialFile.ManifestHeader).Append('\n');
            foreach (var e in noLayers.OrderBy(e => e.Key, StringComparer.Ordinal)) manifest.Append(GenerationStore.Hash(e.Value)).Append(' ').Append(e.Value.LongLength).Append(' ').Append(e.Key).Append('\n');
            all.Add((YlpArchive.ManifestName, Utf8(manifest.ToString()))); all.AddRange(noLayers.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value)));
            Assert.That(() => SmartMaterialFile.Read(StoredZip(all)), Throws.TypeOf<InvalidDataException>().With.Message.Contains(SmartMaterialFile.LayersName));
            // 新しい manifest・壊れた zip・切れたファイル・中身の違うエントリ
            Assert.That(() => SmartMaterialFile.Read(Rezip(file, "YOLUPAINTER-SMART-2")), Throws.TypeOf<InvalidDataException>().With.Message.Contains("newer YoluPainter"));
            Assert.That(() => SmartMaterialFile.Read(file.Take(file.Length / 2).ToArray()), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => SmartMaterialFile.Read(new byte[100]), Throws.TypeOf<InvalidDataException>());
            var flipped = (byte[])file.Clone(); flipped[flipped.Length / 2] ^= 0x55;
            Assert.That(() => SmartMaterialFile.Read(flipped), Throws.TypeOf<InvalidDataException>());
            // ほかの種類のファイル
            var ylp = YlpArchive.Write(new Dictionary<string, byte[]> { { YlpArchive.NativeName, DocumentBinary.Write(new PaintDocument(8, 8, 8)) } });
            Assert.That(() => SmartMaterialFile.Read(ylp), Throws.TypeOf<InvalidDataException>().With.Message.Contains("smart material file"));
            Assert.That(SmartMaterialFile.LooksLike(ylp), Is.False);
            Assert.That(() => YlpArchive.Read(file), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Not a YoluPainter file"));
        }

        /// <summary>manifest の 1 行目だけを変えた zip（中身の SHA-256 は合ったまま）。</summary>
        static byte[] Rezip(byte[] file, string header)
        {
            // 書き手は 1 行目に今の版しか書かないので、manifest のエントリを差し替えた zip を手で作る（mimetype を無圧縮で先頭に）
            var entries = SmartMaterialFile.ReadArchive(file);
            var manifest = new StringBuilder(header).Append('\n');
            foreach (var e in entries.OrderBy(e => e.Key, StringComparer.Ordinal)) manifest.Append(GenerationStore.Hash(e.Value)).Append(' ').Append(e.Value.LongLength).Append(' ').Append(e.Key).Append('\n');
            var all = new List<(string, byte[])> { ("mimetype", Encoding.ASCII.GetBytes(SmartMaterialFile.MimeType)), (YlpArchive.ManifestName, Utf8(manifest.ToString())) };
            all.AddRange(entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value)));
            return StoredZip(all);
        }
        /// <summary>無圧縮の zip。</summary>
        static byte[] StoredZip(List<(string name, byte[] data)> entries)
        {
            var output = new MemoryStream(); var central = new MemoryStream(); var w = new BinaryWriter(output); var c = new BinaryWriter(central);
            foreach (var (name, data) in entries)
            {
                uint crc = Crc(data); var n = Encoding.ASCII.GetBytes(name); uint offset = (uint)output.Position;
                w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0x21);
                w.Write(crc); w.Write((uint)data.Length); w.Write((uint)data.Length); w.Write((ushort)n.Length); w.Write((ushort)0); w.Write(n); w.Write(data);
                c.Write(0x02014b50u); c.Write((ushort)20); c.Write((ushort)20); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0x21);
                c.Write(crc); c.Write((uint)data.Length); c.Write((uint)data.Length); c.Write((ushort)n.Length); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0); c.Write(0u); c.Write(offset); c.Write(n);
            }
            w.Flush(); c.Flush(); long start = output.Position; central.Position = 0; central.CopyTo(output);
            w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)entries.Count); w.Write((ushort)entries.Count); w.Write((uint)central.Length); w.Write((uint)start); w.Write((ushort)0); w.Flush();
            return output.ToArray();
        }
        static uint Crc(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1; }
            return ~crc;
        }

        // ───────── .ylp の形式 5 ─────────

        static Dictionary<string, byte[]> Project(ProjectResources resources)
        {
            var d = new PaintDocument(32, 32, 16, id: A); d.AddLayer("L");
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { YlpFormat.ProjectName, YlpFormat.WriteProject(new YlpProjectInfo(new[] { new YlpTextureSetInfo(A, "Body", 0) }, A)) },
                { YlpFormat.SetEntry(A, YlpArchive.NativeName), DocumentBinary.Write(d) },
            };
            ResourceIndex.AddTo(files, resources);
            YlpFormat.Stamp(files, App, null);
            return files;
        }

        [Test] public void AProjectHoldsSmartMaterialsAndSmartMasksAndWritesThemBackUnchanged()
        {
            var material = Material(); var mask = BuiltInSmartMaterials.Make("edges");
            var materialFile = SmartMaterialFile.Write(material, App); var maskFile = SmartMaterialFile.Write(mask, App);
            var resources = new ProjectResources();
            resources.Add("Image", ImageContent.FromPixels(new byte[16], 2, 2), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            var m = resources.AddSmart("Mine", materialFile, material, ResourceOrigin.None, out bool added);
            Assert.That(added, Is.True);
            Assert.That(resources.AddSmart("Again", materialFile, material, ResourceOrigin.None, out bool again), Is.SameAs(m)); Assert.That(again, Is.False, "the same file is held once");
            var k = resources.AddSmart("Edges", maskFile, mask, ResourceOrigin.BuiltIn("edges", 1), out _);
            var lib = resources.AddSmart("From library", SmartMaterialFile.Write(material.Renamed("Other"), App), material.Renamed("Other"), ResourceOrigin.Library("Other.ylsmart", new string('c', 64), 10), out _);
            Assert.That(resources.Count, Is.EqualTo(4));
            Assert.That(resources.UsedBytes, Is.EqualTo(16 + m.ByteSize + k.ByteSize + lib.ByteSize));
            var files = Project(resources);
            string json = Text(files[ResourceIndex.EntryName]);
            Assert.That(json, Does.Contain("\"kind\": \"smartMaterial\"").And.Contain("\"kind\": \"smartMask\"").And.Contain("\"length\": " + materialFile.Length));
            Assert.That(files[ResourceIndex.SmartEntry(m.Hash)], Is.EqualTo(materialFile));
            Assert.That(YlpFormat.KindOf(ResourceIndex.SmartEntry(m.Hash)), Is.EqualTo(YlpFormat.EntryKind.Source));
            Assert.That(YlpFormat.KindOf("resources/" + new string('a', 63) + ".ylsmart"), Is.Null);
            var bytes = YlpArchive.Write(files);
            var opened = YlpFormat.Open(YlpArchive.Read(bytes));
            Assert.That(opened.Info.Format, Is.EqualTo(5)); Assert.That(opened.UnknownEntries, Is.Empty);
            Assert.That(opened.Resources.Select(r => (r.Kind, r.Name)), Is.EqualTo(new[] { (ResourceKind.Image, "Image"), (ResourceKind.SmartMaterial, "Mine"), (ResourceKind.SmartMask, "Edges"), (ResourceKind.SmartMaterial, "From library") }));
            var loaded = ResourceIndex.Load(opened.Files, opened.Resources);
            Assert.That(loaded.Smart.Select(s => (s.Id, s.Name, s.Hash, s.Kind, s.Origin.Kind)), Is.EqualTo(resources.Smart.Select(s => (s.Id, s.Name, s.Hash, s.Kind, s.Origin.Kind))));
            Assert.That(loaded.Smart[0].Material.LayerNames, Is.EqualTo(material.LayerNames));
            Assert.That(loaded.UsedBytes, Is.EqualTo(resources.UsedBytes));
            var back = new Dictionary<string, byte[]>(opened.Files); ResourceIndex.AddTo(back, loaded);
            foreach (var entry in opened.Files) Assert.That(back[entry.Key], Is.EqualTo(entry.Value), entry.Key);
            // 消す・名前（画像と同じ操作）
            loaded.Rename(loaded.Smart[0].Id, "Renamed"); Assert.That(loaded.Smart[0].Name, Is.EqualTo("Renamed"));
            loaded.Remove(loaded.Smart[1].Id); Assert.That(loaded.Smart.Count, Is.EqualTo(2));
            // 予算と数
            var tight = new ProjectResources { BudgetBytes = materialFile.Length };
            Assert.That(Assert.Throws<ResourceRefusedException>(() => tight.AddSmart("x", materialFile, material, ResourceOrigin.None, out _)).Refusal, Is.EqualTo(ResourceRefusal.OverBudget));
            Assert.That(tight.Count, Is.Zero);
        }

        [Test] public void AMissingOrWrongSmartFileRefusesTheProjectAndAnUnlistedOneIsReported()
        {
            var material = Material(); var file = SmartMaterialFile.Write(material, App);
            var resources = new ProjectResources(); var held = resources.AddSmart("Mine", file, material, ResourceOrigin.None, out _);
            var good = Project(resources);
            byte[] With(Action<Dictionary<string, byte[]>> edit) { var f = new Dictionary<string, byte[]>(good); edit(f); return YlpArchive.Write(f); }
            InvalidDataException Opening(byte[] ylp) => Assert.Throws<InvalidDataException>(() => { var o = YlpFormat.Open(YlpArchive.Read(ylp)); ResourceIndex.Load(o.Files, o.Resources); });
            Assert.That(Opening(With(f => f.Remove(ResourceIndex.SmartEntry(held.Hash)))).Message, Does.Contain("has no file"));
            var other = SmartMaterialFile.Write(material.Renamed("Other"), App);
            Assert.That(Opening(With(f => f[ResourceIndex.SmartEntry(held.Hash)] = other)).Message, Does.Contain("not the file"));
            var mask = SmartMaterialFile.Write(BuiltInSmartMaterials.Make("edges"), App);
            string maskHash = GenerationStore.Hash(mask);
            Assert.That(Opening(With(f =>
            {
                f.Remove(ResourceIndex.SmartEntry(held.Hash)); f[ResourceIndex.SmartEntry(maskHash)] = mask;
                f[ResourceIndex.EntryName] = Utf8(Text(f[ResourceIndex.EntryName]).Replace(held.Hash, maskHash).Replace("\"length\": " + file.Length, "\"length\": " + mask.Length));
            })).Message, Does.Contain("listed as a smart material"));
            var broken = Utf8("not a smart material at all");
            string brokenHash = GenerationStore.Hash(broken);
            Assert.That(Opening(With(f =>
            {
                f.Remove(ResourceIndex.SmartEntry(held.Hash)); f[ResourceIndex.SmartEntry(brokenHash)] = broken;
                f[ResourceIndex.EntryName] = Utf8(Text(f[ResourceIndex.EntryName]).Replace(held.Hash, brokenHash).Replace("\"length\": " + file.Length, "\"length\": " + broken.Length));
            })).Message, Does.Contain("is broken"));
            foreach (var origin in new[] { "{ \"type\": \"file\", \"path\": \"/x.ylsmart\", \"sha256\": \"" + new string('a', 64) + "\", \"length\": 1 }", "{ \"type\": \"unityAsset\", \"guid\": \"" + new string('a', 32) + "\", \"path\": \"Assets/x\" }" })
                Assert.That(Opening(With(f => f[ResourceIndex.EntryName] = Utf8(Text(f[ResourceIndex.EntryName]).Replace("{ \"type\": \"none\" }", origin)))).Message, Does.Contain("does not have"));
            Assert.That(Opening(With(f => f[ResourceIndex.EntryName] = Utf8(Text(f[ResourceIndex.EntryName]).Replace("\"length\": " + file.Length, "\"length\": 0")))).Message, Does.Contain("length"));
            // 並びに無い .ylsmart は知らないエントリ
            var opened = YlpFormat.Open(YlpArchive.Read(With(f => f[ResourceIndex.SmartEntry(maskHash)] = mask)));
            Assert.That(opened.UnknownEntries, Is.EqualTo(new[] { ResourceIndex.SmartEntry(maskHash) }));
            // 形式 4 の読み手は形式 5 を「新しい」と断る（ここでは形式の数で確かめる）
            Assert.That(YlpFormat.ReadInfo(good[YlpFormat.InfoName]).Format, Is.EqualTo(5));
        }

        // ───────── 形式 4 のフィクスチャ ─────────

        /// <summary>
        /// 形式 5 を足す前の YoluPainter のウィンドウの保存で作った形式 4 のファイル（テクスチャセット 2 つ、リソース 2 つ（内蔵の画像と自分の置き場の
        /// 画像）、置いた層・塗りつぶし・チャンネルごとの合成・マスクの Generator・グループ）: 形式 4 として開き（manifest は YLP-3）、移行はエントリを
        /// 変えず、リソースは画像として読め、正本はバイト一致で読み書きできる。今の形式で書き戻して読み直すと、どのエントリも同じバイト列。
        /// </summary>
        [Test] public void AFormatFourFileFromBeforeSmartMaterialsOpensUnchanged()
        {
            var bytes = File.ReadAllBytes(Fixture("format4.ylp"));
            var snapshot = YlpArchive.Read(bytes);
            var opened = YlpFormat.Open(snapshot);
            Assert.That(opened.Info.Format, Is.EqualTo(4)); Assert.That(opened.Upgraded, Is.True);
            Assert.That(opened.UnknownEntries, Is.Empty); Assert.That(opened.Notes, Is.Empty);
            Assert.That(opened.Files.Keys, Is.EquivalentTo(snapshot.Keys.Where(k => k != YlpFormat.InfoName)), "the step 4 → 5 moves nothing");
            foreach (var entry in opened.Files) Assert.That(entry.Value, Is.EqualTo(snapshot[entry.Key]), entry.Key);
            Assert.That(opened.Project.Sets.Select(s => (s.Name, s.MaterialSlot)), Is.EqualTo(new[] { ("Texture Set 1", 0), ("Trim", 1) }));
            var loaded = ResourceIndex.Load(opened.Files, opened.Resources);
            Assert.That(loaded.Images.Select(r => (r.Name, r.Origin.Kind)), Is.EqualTo(new[] { ("UV checker", ResourceOriginKind.BuiltIn), ("Swatch", ResourceOriginKind.Library) }));
            Assert.That(loaded.Smart, Is.Empty);
            Assert.That(loaded.Images[0].ContentHash, Is.EqualTo(BuiltInImages.Make("uv-checker").Hash), "the built-in image's pixels");
            foreach (var set in opened.Project.Sets)
            {
                var native = opened.SetFiles(set.Id)[YlpArchive.NativeName];
                var document = DocumentBinary.Read(native);
                Assert.That(BitConverter.ToInt32(native, 8), Is.EqualTo(14), "the format 4 fixture was saved with native version 14");
                var current = (byte[])native.Clone(); BitConverter.GetBytes(DocumentBinary.CurrentVersion).CopyTo(current, 8);
                Assert.That(DocumentBinary.Write(document), Is.EqualTo(current), set.Name + ": the native source reads and writes back byte for byte");
            }
            var first = DocumentBinary.Read(opened.SetFiles(opened.Project.Sets[0].Id)[YlpArchive.NativeName]);
            Assert.That(first.Layers.Select(l => l.Name), Is.SupersetOf(new[] { "UV checker", "Paint", "Group" }));
            Assert.That(first.Layers.Single(l => l.Name == "Paint").ChannelBlends.Keys, Is.EqualTo(new[] { PaintChannel.Roughness }));
            // 今の形式で書き戻す
            var write = new Dictionary<string, byte[]>(opened.Files);
            YlpFormat.Stamp(write, App, opened.Info.CreatedBy);
            var again = YlpFormat.Open(YlpArchive.Read(YlpArchive.Write(write)));
            Assert.That(again.Info.Format, Is.EqualTo(YlpFormat.Current)); Assert.That(again.Upgraded, Is.False); Assert.That(again.UnknownEntries, Is.Empty);
            foreach (var entry in opened.Files) Assert.That(again.Files[entry.Key], Is.EqualTo(entry.Value), entry.Key);
        }
    }
}
