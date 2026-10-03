using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// .ylp の形式 4（プロジェクトのリソース）: resources.json の往復（出どころ 5 種）と拒否、リソースを持つファイルの往復（同じ中身は 1 つの PNG、
    /// manifest は YLP-3）、並びにある画素が無い・壊れた・大きさの違うリソースはファイルごと断る（黙って落とさない）、並びに無い画素は知らない
    /// エントリとして知らせる、YLP-2 の manifest はリソースの置き場を認めない、復旧の checkpoint もリソースを持つ。形式 4 を足す前の
    /// YoluPainter のウィンドウで作った形式 3 のフィクスチャ（Fixtures~/format3.ylp。テクスチャセット 2 つ）を開いて、正本・選択範囲・
    /// メッシュマップ・合成を確かめ、形式 4 で書き戻すとエントリはバイト一致。
    /// </summary>
    public sealed class YlpResourceFormatTests
    {
        static readonly YlpWriterInfo Saver = new YlpWriterInfo("YoluPainter", "1.2.3", "2022.3.22f1");
        static readonly Guid A = new Guid("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
        static readonly Guid R1 = new Guid("aaaaaaaa-0000-4000-8000-000000000001"), R2 = new Guid("aaaaaaaa-0000-4000-8000-000000000002"), R3 = new Guid("aaaaaaaa-0000-4000-8000-000000000003");
        static string Fixture(string name) => PackagePaths.Physical("Tests/Editor/Persistence/Fixtures~/" + name);
        static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

        static ImageContent Image(int w, int h, int seed)
        {
            var rgba = new byte[w * h * 4]; new System.Random(seed).NextBytes(rgba);
            for (int i = 3; i < rgba.Length; i += 12) rgba[i] = 0;
            return ImageContent.FromPixels(rgba, w, h);
        }

        static ResourceOrigin[] Origins() => new[]
        {
            ResourceOrigin.None,
            ResourceOrigin.UnityAsset("0123456789abcdef0123456789abcdef", "Assets/Textures/Scratches \"old\".png", "4c1f0d8e8f1d2c3b4a5968778695a4b3", true),
            ResourceOrigin.File("/home/someone/images/wood.png", new string('a', 64), 12345),
            ResourceOrigin.Library("Wood grain.png", new string('b', 64), 678),
            ResourceOrigin.BuiltIn("uv-checker", 1),
        };

        // ───────── resources.json ─────────

        [Test] public void TheResourceListRoundTripsEveryOrigin()
        {
            var origins = Origins();
            var entries = origins.Select((o, i) => new YlpResourceEntry(new Guid(i + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11), ResourceKind.Image, "Image " + i + (i == 1 ? " \"quoted\" 画像" : ""),
                ImageContent.ComputeHash(new byte[] { (byte)i, 0, 0, 0 }, 1, 1), 1 + i, 2 + i, (ResourceColorSpace)(i % 3), o)).ToList();
            var read = ResourceIndex.Read(ResourceIndex.Write(entries));
            Assert.That(read.Count, Is.EqualTo(entries.Count));
            for (int i = 0; i < entries.Count; i++)
            {
                var a = entries[i]; var b = read[i];
                Assert.That((b.Id, b.Kind, b.Name, b.Content, b.Width, b.Height, b.ColorSpace), Is.EqualTo((a.Id, a.Kind, a.Name, a.Content, a.Width, a.Height, a.ColorSpace)), "entry " + i);
                Assert.That((b.Origin.Kind, b.Origin.AssetGuid, b.Origin.Path, b.Origin.SourceStamp, b.Origin.SourceLength, b.Origin.BuiltInKey, b.Origin.BuiltInVersion, b.Origin.ReadThroughGpu),
                    Is.EqualTo((a.Origin.Kind, a.Origin.AssetGuid, a.Origin.Path, a.Origin.SourceStamp, a.Origin.SourceLength, a.Origin.BuiltInKey, a.Origin.BuiltInVersion, a.Origin.ReadThroughGpu)), "origin " + i);
            }
            Assert.That(ResourceIndex.Read(ResourceIndex.Write(new YlpResourceEntry[0])), Is.Empty);
            string hash = new string('c', 64);
            var extra = ResourceIndex.Read(Json("{ \"future\": 1, \"resources\": [ { \"id\": \"" + R1.ToString("D") + "\", \"kind\": \"image\", \"name\": \"X\", \"content\": \"" + hash + "\", \"width\": 2, \"height\": 3, \"tags\": [\"a\"] } ] }"));
            Assert.That(extra.Single().Origin.Kind, Is.EqualTo(ResourceOriginKind.None), "no origin is the embedded copy only");
            Assert.That(extra.Single().ColorSpace, Is.EqualTo(ResourceColorSpace.Unspecified)); Assert.That(extra.Single().Width, Is.EqualTo(2), "unknown keys are skipped");
        }

        [Test] public void BrokenResourceListsAreRefused()
        {
            string id = R1.ToString("D"), hash = new string('c', 64);
            string Entry(string fields) => "{ \"resources\": [ { " + fields + " } ] }";
            string Good(string replace = null, string with = null)
            {
                string fields = "\"id\": \"" + id + "\", \"kind\": \"image\", \"name\": \"X\", \"content\": \"" + hash + "\", \"width\": 2, \"height\": 3, \"origin\": { \"type\": \"builtIn\", \"key\": \"grid\", \"version\": 1 }";
                return Entry(replace == null ? fields : fields.Replace(replace, with));
            }
            Assert.That(ResourceIndex.Read(Json(Good())).Single().Origin.BuiltInKey, Is.EqualTo("grid"));
            var bad = new Dictionary<string, string>
            {
                { "empty", "" }, { "not an object", "[]" }, { "no list", "{}" }, { "an item that is not an object", "{ \"resources\": [ 1 ] }" },
                { "no id", Good("\"id\": \"" + id + "\", ", "") }, { "upper-case id", Good(id, id.ToUpperInvariant()) }, { "empty id", Good(id, Guid.Empty.ToString("D")) },
                { "an unknown kind", Good("\"image\"", "\"futureKind\"") }, { "no name", Good("\"name\": \"X\", ", "") }, { "a blank name", Good("\"X\"", "\"  \"") },
                { "a short hash", Good(hash, "abc") }, { "an upper-case hash", Good(hash, hash.ToUpperInvariant()) },
                { "width 0", Good("\"width\": 2", "\"width\": 0") }, { "height 8193", Good("\"height\": 3", "\"height\": 8193") }, { "a text width", Good("\"width\": 2", "\"width\": \"2\"") },
                { "an unknown colour space", Good("\"width\": 2", "\"colorSpace\": \"aces\", \"width\": 2") },
                { "an unknown origin type", Good("\"builtIn\"", "\"cloud\"") }, { "an origin that is not an object", Good("{ \"type\": \"builtIn\", \"key\": \"grid\", \"version\": 1 }", "\"grid\"") },
                { "a built-in version 0", Good("\"version\": 1", "\"version\": 0") }, { "a built-in key with capitals", Good("\"grid\"", "\"Grid\"") },
                { "a unity asset without a guid", Good("{ \"type\": \"builtIn\", \"key\": \"grid\", \"version\": 1 }", "{ \"type\": \"unityAsset\", \"path\": \"Assets/x.png\" }") },
                { "a unity guid with hyphens", Good("{ \"type\": \"builtIn\", \"key\": \"grid\", \"version\": 1 }", "{ \"type\": \"unityAsset\", \"guid\": \"" + id + "\", \"path\": \"Assets/x.png\" }") },
                { "a file without a hash", Good("{ \"type\": \"builtIn\", \"key\": \"grid\", \"version\": 1 }", "{ \"type\": \"file\", \"path\": \"/x.png\", \"length\": 1 }") },
                { "a library path", Good("{ \"type\": \"builtIn\", \"key\": \"grid\", \"version\": 1 }", "{ \"type\": \"library\", \"file\": \"../x.png\", \"sha256\": \"" + hash + "\", \"length\": 1 }") },
                { "a negative length", Good("{ \"type\": \"builtIn\", \"key\": \"grid\", \"version\": 1 }", "{ \"type\": \"file\", \"path\": \"/x.png\", \"sha256\": \"" + hash + "\", \"length\": -1 }") },
                { "the same id twice", "{ \"resources\": [ " + string.Join(",", Enumerable.Repeat("{ \"id\": \"" + id + "\", \"kind\": \"image\", \"name\": \"X\", \"content\": \"" + hash + "\", \"width\": 2, \"height\": 3 }", 2)) + " ] }" },
                { "too many", "{ \"resources\": [ " + string.Join(",", Enumerable.Range(0, ProjectResources.MaxResources + 1).Select(i => "{ \"id\": \"" + new Guid(i + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1).ToString("D") + "\", \"kind\": \"image\", \"name\": \"X\", \"content\": \"" + hash + "\", \"width\": 2, \"height\": 3 }")) + " ] }" },
            };
            foreach (var b in bad) Assert.That(() => ResourceIndex.Read(Json(b.Value)), Throws.TypeOf<InvalidDataException>(), b.Key);
            Assert.That(() => ResourceIndex.Read(Json(Good("\"image\"", "\"futureKind\""))), Throws.TypeOf<InvalidDataException>().With.Message.Contains("does not know"), "a later kind is refused, not dropped");
        }

        // ───────── 形式 4 のファイル ─────────

        static byte[] Document(Guid id)
        {
            var d = new PaintDocument(32, 32, 16, id: id);
            d.AddLayer("L").GetChannel(PaintChannel.Color).SetPixel(3, 4, new Rgba32(9, 2, 3));
            return DocumentBinary.Write(d);
        }

        /// <summary>1 つのセットと、3 つのリソース（2 つ目と 3 つ目は同じ中身）を持つ中身。</summary>
        static (Dictionary<string, byte[]> files, ProjectResources resources) Project()
        {
            var resources = new ProjectResources();
            var a = Image(5, 3, 1); var b = Image(7, 2, 2);
            resources.Restore(R1, "Scratches", a, Origins()[1], ResourceColorSpace.Srgb);
            resources.Restore(R2, "Mask", b, Origins()[3], ResourceColorSpace.Linear);
            resources.Restore(R3, "Mask again", ImageContent.FromPixels(b.CopyPixels(), 7, 2), Origins()[4], ResourceColorSpace.Unspecified);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { YlpFormat.ProjectName, YlpFormat.WriteProject(new YlpProjectInfo(new[] { new YlpTextureSetInfo(A, "Body", 0) }, A)) },
                { YlpFormat.SetEntry(A, YlpArchive.NativeName), Document(A) },
            };
            ResourceIndex.AddTo(files, resources);
            YlpFormat.Stamp(files, Saver, null);
            return (files, resources);
        }

        static string ManifestHeader(byte[] ylp)
        {
            using (var zip = new ZipArchive(new MemoryStream(ylp), ZipArchiveMode.Read))
            using (var reader = new StreamReader(zip.GetEntry(YlpArchive.ManifestName).Open())) return reader.ReadLine();
        }

        [Test] public void AFileWithResourcesRoundTripsAndKeepsEachContentOnce()
        {
            var (files, resources) = Project();
            Assert.That(resources.Images[1].Content, Is.SameAs(resources.Images[2].Content), "equal pixels are held once");
            Assert.That(files.Keys.Count(k => k.StartsWith(ResourceIndex.Folder, StringComparison.Ordinal)), Is.EqualTo(2), "one PNG per distinct content");
            Assert.That(YlpFormat.KindOf(ResourceIndex.EntryName), Is.EqualTo(YlpFormat.EntryKind.Source));
            Assert.That(YlpFormat.KindOf(ResourceIndex.ContentEntry(resources.Images[0].ContentHash)), Is.EqualTo(YlpFormat.EntryKind.Source));
            foreach (var name in new[] { "resources/x.png", "resources/" + new string('A', 64) + ".png", "resources/" + new string('a', 64) + ".bin", YlpFormat.SetEntry(A, "resources.json") })
                Assert.That(YlpFormat.KindOf(name), Is.Null, name);
            var bytes = YlpArchive.Write(files);
            Assert.That(ManifestHeader(bytes), Is.EqualTo("YOLUPAINTER-YLP-3"));
            var opened = YlpFormat.Open(YlpArchive.Read(bytes));
            Assert.That(opened.Info.Format, Is.EqualTo(YlpFormat.Current)); Assert.That(opened.Upgraded, Is.False); Assert.That(opened.UnknownEntries, Is.Empty);
            Assert.That(opened.Resources.Select(r => (r.Id, r.Name)), Is.EqualTo(new[] { (R1, "Scratches"), (R2, "Mask"), (R3, "Mask again") }), "the order is kept");
            var loaded = ResourceIndex.Load(opened.Files, opened.Resources);
            Assert.That(loaded.Images.Select(r => (r.Id, r.Name, r.ContentHash, r.ColorSpace, r.Origin.Kind)), Is.EqualTo(resources.Images.Select(r => (r.Id, r.Name, r.ContentHash, r.ColorSpace, r.Origin.Kind))));
            for (int i = 0; i < 3; i++) Assert.That(loaded.Images[i].Content.CopyPixels(), Is.EqualTo(resources.Images[i].Content.CopyPixels()), "pixels of " + i + ", the RGB under zero alpha included");
            Assert.That(loaded.Images[1].Content, Is.SameAs(loaded.Images[2].Content), "read once, shared");
            Assert.That(loaded.UsedBytes, Is.EqualTo(resources.UsedBytes));
            // 書き戻すと同じバイト（PNG は読んだものをそのまま）
            var again = new Dictionary<string, byte[]>(opened.Files);
            ResourceIndex.AddTo(again, loaded);
            foreach (var entry in opened.Files) Assert.That(again[entry.Key], Is.EqualTo(entry.Value), entry.Key);
            // リソースの無いプロジェクトは resources.json を書かない
            var none = new Dictionary<string, byte[]>(); ResourceIndex.AddTo(none, new ProjectResources());
            Assert.That(none, Is.Empty);
        }

        [Test] public void MissingBrokenOrResizedResourcesRefuseTheFile()
        {
            var (files, resources) = Project();
            string first = ResourceIndex.ContentEntry(resources.Images[0].ContentHash), shared = ResourceIndex.ContentEntry(resources.Images[1].ContentHash);
            // 並びにある画素が無い: 開くのを断る（どのリソースかを言う）
            var missing = new Dictionary<string, byte[]>(files); missing.Remove(shared);
            Assert.That(() => YlpFormat.Open(YlpArchive.Read(YlpArchive.Write(missing))), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Mask").And.Message.Contains("no pixels"));
            // 名前（ハッシュ）と違う画素: manifest は合っていても、読むときに断る
            var swapped = new Dictionary<string, byte[]>(files) { [first] = Image(5, 3, 99).EncodePng() };
            var opened = YlpFormat.Open(YlpArchive.Read(YlpArchive.Write(swapped)));
            Assert.That(() => ResourceIndex.Load(opened.Files, opened.Resources), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Scratches").And.Message.Contains("broken"));
            var garbage = new Dictionary<string, byte[]>(files) { [first] = new byte[] { 1, 2, 3 } };
            opened = YlpFormat.Open(garbage);
            Assert.That(() => ResourceIndex.Load(opened.Files, opened.Resources), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Scratches").And.Message.Contains("Not a PNG"));
            // 並びの大きさと画素の大きさが違う
            var list = ResourceIndex.Read(files[ResourceIndex.EntryName]).ToList();
            list[0] = new YlpResourceEntry(list[0].Id, list[0].Kind, list[0].Name, list[0].Content, 3, 5, list[0].ColorSpace, list[0].Origin);
            var resized = new Dictionary<string, byte[]>(files) { [ResourceIndex.EntryName] = ResourceIndex.Write(list) };
            opened = YlpFormat.Open(resized);
            Assert.That(() => ResourceIndex.Load(opened.Files, opened.Resources), Throws.TypeOf<InvalidDataException>().With.Message.Contains("3 × 5"));
            // 壊れた並び
            var brokenList = new Dictionary<string, byte[]>(files) { [ResourceIndex.EntryName] = Json("{ \"resources\": 1 }") };
            Assert.That(() => YlpFormat.Open(brokenList), Throws.TypeOf<InvalidDataException>().With.Message.Contains(ResourceIndex.EntryName));
            // 並びに無い画素と、リソースの置き場の知らない名前は、知らないエントリとして知らせる（保存すると残らない）
            string stray = ResourceIndex.ContentEntry(Image(2, 2, 7).Hash);
            var extra = new Dictionary<string, byte[]>(files) { [stray] = Image(2, 2, 7).EncodePng(), ["resources/notes.txt"] = new byte[] { 1 } };
            Assert.That(YlpFormat.Open(YlpArchive.Read(YlpArchive.Write(extra))).UnknownEntries, Is.EquivalentTo(new[] { "resources/notes.txt", stray }));
        }

        [Test] public void OnlyTheNewestManifestAllowsTheResourceFolder()
        {
            var (files, _) = Project();
            // 同じ中身を YLP-2 の manifest で書いたもの（どの版の YoluPainter も書かない）は、リソースの置き場を名前で断る
            var z = new ZipBuilder();
            z.Entries.Add(ZipBuilder.Stored("mimetype", Encoding.ASCII.GetBytes(YlpArchive.MimeType)));
            var ordered = files.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
            z.Entries.Add(ZipBuilder.Deflated(YlpArchive.ManifestName, Encoding.UTF8.GetBytes(YlpArchive.ManifestHeaderV2 + "\n" + string.Concat(ordered.Select(f => GenerationStore.Hash(f.Value) + " " + f.Value.Length + " " + f.Key + "\n")))));
            foreach (var f in ordered) z.Entries.Add(ZipBuilder.Stored(f.Key, f.Value));
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unsafe entry name").And.Message.Contains("resources/"));
            foreach (var name in new[] { "resources/", "resources/sub/x.png", "resources/composite/Color.png", "resources/../x", "resources/.hidden", "Resources/x.png", YlpFormat.SetEntry(A, "resources/x.png") })
            {
                var with = new Dictionary<string, byte[]>(files) { [name] = new byte[] { 1 } };
                Assert.That(() => YlpArchive.Write(with), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unsafe entry name"), name);
            }
        }

        [Test] public void RecoveryCheckpointsKeepResources()
        {
            string root = Path.Combine(Path.GetTempPath(), "yolupainter-resources-" + Guid.NewGuid().ToString("N"));
            try
            {
                var (files, resources) = Project();
                GenerationStore.Commit(root, files);
                var loaded = GenerationStore.Load(root);
                Assert.That(loaded.Files.Keys, Is.EquivalentTo(files.Keys));
                var opened = YlpFormat.Open(loaded.Files);
                var back = ResourceIndex.Load(opened.Files, opened.Resources);
                Assert.That(back.Images.Select(r => r.ContentHash), Is.EqualTo(resources.Images.Select(r => r.ContentHash)));
                Assert.That(() => GenerationStore.Commit(Path.Combine(root, "other"), new Dictionary<string, byte[]>(files) { ["resources/sub/x.png"] = new byte[1] }), Throws.TypeOf<InvalidDataException>());
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // ───────── 形式 3 のフィクスチャ ─────────

        /// <summary>
        /// 形式 4 を足す前の YoluPainter のウィンドウの保存で作った形式 3 のファイル（テクスチャセット 2 つ、層・マスク・グループ・塗りつぶし・調整・
        /// フィルター・Generator、選択範囲、焼いたメッシュマップ、合成）: 形式 3 として開き（manifest は YLP-2）、移行はエントリを変えず、正本・選択範囲は
        /// バイト一致で読み書きでき、メッシュマップはそのセットのスロットのものとして読め、合成は正本から作ったものと画素で一致する。今の形式で書き戻して
        /// 読み直すと、どのエントリも同じバイト列。
        /// </summary>
        [Test] public void AFormatThreeFileFromBeforeResourcesOpensUnchanged()
        {
            var bytes = File.ReadAllBytes(Fixture("format3.ylp"));
            Assert.That(ManifestHeader(bytes), Is.EqualTo(YlpArchive.ManifestHeaderV2));
            var snapshot = YlpArchive.Read(bytes);
            var opened = YlpFormat.Open(snapshot);
            Assert.That(opened.Info.Format, Is.EqualTo(3)); Assert.That(opened.Upgraded, Is.True);
            Assert.That(opened.Info.SavedBy.App, Is.EqualTo("YoluPainter")); Assert.That(opened.Info.CreatedBy, Is.Not.Null);
            Assert.That(opened.UnknownEntries, Is.Empty); Assert.That(opened.Notes, Is.Empty); Assert.That(opened.Resources, Is.Empty);
            Assert.That(opened.Files.Keys, Is.EquivalentTo(snapshot.Keys.Where(k => k != YlpFormat.InfoName)), "the step 3 → 4 moves nothing");
            foreach (var entry in opened.Files) Assert.That(entry.Value, Is.EqualTo(snapshot[entry.Key]), entry.Key);
            Assert.That(opened.Project.Sets.Select(s => (s.Name, s.MaterialSlot)), Is.EqualTo(new[] { ("Body", 0), ("Hair Front", 1) }));
            Assert.That(opened.Project.CurrentSet, Is.EqualTo(opened.Project.Sets[0].Id));
            foreach (var set in opened.Project.Sets)
            {
                var files = opened.SetFiles(set.Id);
                var native = files[YlpArchive.NativeName];
                var document = DocumentBinary.Read(native);
                Assert.That(document.Id, Is.EqualTo(set.Id), "new sets take the document's ID");
                Assert.That(DocumentBinary.Write(document), Is.EqualTo(AtCurrentVersion(native)), set.Name + ": the native source reads and writes back byte for byte");
                Assert.That(SelectionBinary.Write(SelectionBinary.Read(files[SelectionBinary.EntryName], document)), Is.EqualTo(files[SelectionBinary.EntryName]), set.Name + " selection");
                var maps = files.Where(f => f.Key.StartsWith(MeshMapBinary.EntryPrefix, StringComparison.Ordinal)).Select(f => MeshMapBinary.Read(f.Value)).ToList();
                Assert.That(maps.Select(m => m.Kind), Is.EquivalentTo(new[] { MeshMapKind.WorldNormal, MeshMapKind.Position }), set.Name + " mesh maps");
                Assert.That(maps.Select(m => m.Provenance.TargetSlot), Is.All.EqualTo(set.MaterialSlot));
                bool generator = document.Layers.Any(l => l.Filters.Any(f => f.Settings.Type == FilterType.Generator) || l.Mask != null && l.Mask.Filters.Any(f => f.Settings.Type == FilterType.Generator));
                Assert.That(generator, Is.EqualTo(set.Name == "Hair Front"), "the second set holds a Generator stage (native version 11)");
                // Generator の段はメッシュマップを入力にしないと元を通すので、その段が変える Color はここでは比べない
                AssertCompositesMatch(files, document, generator ? new[] { PaintChannel.Color } : new PaintChannel[0]);
            }
            var body = DocumentBinary.Read(opened.SetFiles(opened.Project.Sets[0].Id)[YlpArchive.NativeName]);
            Assert.That(body.Layers.Select(l => l.Name), Is.SupersetOf(new[] { "Group", "Inner", "Fill", "Invert" }));
            // 今の形式で書き戻す
            var write = new Dictionary<string, byte[]>(opened.Files);
            YlpFormat.Stamp(write, Saver, opened.Info.CreatedBy);
            var rewritten = YlpArchive.Write(write);
            Assert.That(ManifestHeader(rewritten), Is.EqualTo(YlpArchive.ManifestHeader));
            var again = YlpFormat.Open(YlpArchive.Read(rewritten));
            Assert.That(again.Info.Format, Is.EqualTo(YlpFormat.Current)); Assert.That(again.Upgraded, Is.False); Assert.That(again.UnknownEntries, Is.Empty);
            foreach (var entry in opened.Files) Assert.That(again.Files[entry.Key], Is.EqualTo(entry.Value), entry.Key);
        }

        /// <summary>フィクスチャの正本（版 11）を今の版の数にしたもの（版の数のほかは同じ並びの間だけ使える。並びが変わったらここを直す）。</summary>
        static byte[] AtCurrentVersion(byte[] native)
        {
            Assert.That(BitConverter.ToInt32(native, 8), Is.EqualTo(11), "the format 3 fixture was saved with native version 11");
            var bytes = (byte[])native.Clone(); BitConverter.GetBytes(DocumentBinary.CurrentVersion).CopyTo(bytes, 8);
            return bytes;
        }

        static void AssertCompositesMatch(Dictionary<string, byte[]> files, PaintDocument document, PaintChannel[] skip)
        {
            foreach (var channel in YlpContent.UsedChannels(document).Except(skip))
            {
                Assert.That(files.ContainsKey(YlpContent.CompositeName(channel)), Is.True, channel + " composite");
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                try
                {
                    Assert.That(texture.LoadImage(files[YlpContent.CompositeName(channel)], false), Is.True);
                    var pixels = texture.GetPixels32(); var rgba = new byte[pixels.Length * 4];
                    for (int i = 0; i < pixels.Length; i++) { rgba[i * 4] = pixels[i].r; rgba[i * 4 + 1] = pixels[i].g; rgba[i * 4 + 2] = pixels[i].b; rgba[i * 4 + 3] = pixels[i].a; }
                    Assert.That(rgba, Is.EqualTo(YlpContent.Image(document, channel)), channel + " composite matches the native source");
                }
                finally { Object.DestroyImmediate(texture); }
            }
        }
    }
}
