using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// .ylp の形式 3（テクスチャセット）: project.json の読み書きと拒否、形式 2 → 3 の移行（正本などを sets/&lt;文書の ID&gt;/ へ、view.json の
    /// スロットから 1 つのセット）、セットの正本が無い・並びに無いセットの扱い、外側の層（manifest YLP-2 だけがセットの置き場を認める）と
    /// 復旧の checkpoint（GenerationStore）のセットの置き場。フィクスチャ（形式 1・2）は YlpFormatTests。
    /// </summary>
    public sealed class YlpTextureSetFormatTests
    {
        static readonly YlpWriterInfo Saver = new YlpWriterInfo("YoluPainter", "1.2.3", "2022.3.22f1");
        static readonly Guid A = new Guid("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"), B = new Guid("11111111-2222-3333-4444-555555555555");
        static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

        static byte[] Document(Guid id, byte shade)
        {
            var d = new PaintDocument(32, 32, 16, id: id);
            d.AddLayer("L").GetChannel(PaintChannel.Color).SetPixel(3, 4, new Rgba32(shade, 2, 3));
            return DocumentBinary.Write(d);
        }

        static byte[] Project(params (Guid id, string name, int slot)[] sets) => YlpFormat.WriteProject(new YlpProjectInfo(sets.Select(s => new YlpTextureSetInfo(s.id, s.name, s.slot)), sets[0].id));

        // ───────── project.json ─────────

        [Test] public void TheProjectRecordRoundTrips()
        {
            var project = new YlpProjectInfo(new[] { new YlpTextureSetInfo(A, "Body \"skin\"", 0), new YlpTextureSetInfo(B, "髪", 3) }, B);
            var read = YlpFormat.ReadProject(YlpFormat.WriteProject(project));
            Assert.That(read.Sets.Select(s => (s.Id, s.Name, s.MaterialSlot)), Is.EqualTo(new[] { (A, "Body \"skin\"", 0), (B, "髪", 3) }), "the order is kept");
            Assert.That(read.CurrentSet, Is.EqualTo(B));
            Assert.That(Encoding.UTF8.GetString(YlpFormat.WriteProject(project)), Does.Contain("\"" + A.ToString("D") + "\""), "ids are written as lower-case GUIDs with hyphens");
            var extra = YlpFormat.ReadProject(Json("{ \"future\": 1, \"sets\": [ { \"id\": \"" + A.ToString("D") + "\", \"name\": \"X\", \"materialSlot\": 2, \"udim\": [1001] } ], \"current\": \"" + A.ToString("D") + "\" }"));
            Assert.That(extra.Sets.Single().MaterialSlot, Is.EqualTo(2), "keys a later minor version adds are skipped");
        }

        [Test] public void BrokenProjectRecordsAreRefused()
        {
            string a = A.ToString("D"), b = B.ToString("D");
            string Set(string id, string name, string slot) => "{ \"id\": " + id + ", \"name\": " + name + ", \"materialSlot\": " + slot + " }";
            string Doc(string sets, string current) => "{ \"sets\": [" + sets + "], \"current\": " + current + " }";
            var bad = new Dictionary<string, string>
            {
                { "empty", "" }, { "not an object", "[]" }, { "no sets", "{ \"current\": \"" + a + "\" }" },
                { "no set", Doc("", "\"" + a + "\"") },
                { "upper-case id", Doc(Set("\"" + a.ToUpperInvariant() + "\"", "\"X\"", "0"), "\"" + a.ToUpperInvariant() + "\"") },
                { "id without hyphens", Doc(Set("\"" + A.ToString("N") + "\"", "\"X\"", "0"), "\"" + A.ToString("N") + "\"") },
                { "empty id", Doc(Set("\"" + Guid.Empty.ToString("D") + "\"", "\"X\"", "0"), "\"" + Guid.Empty.ToString("D") + "\"") },
                { "same id twice", Doc(Set("\"" + a + "\"", "\"X\"", "0") + "," + Set("\"" + a + "\"", "\"Y\"", "1"), "\"" + a + "\"") },
                { "same slot twice", Doc(Set("\"" + a + "\"", "\"X\"", "0") + "," + Set("\"" + b + "\"", "\"Y\"", "0"), "\"" + a + "\"") },
                { "same name twice", Doc(Set("\"" + a + "\"", "\"Body\"", "0") + "," + Set("\"" + b + "\"", "\"BODY\"", "1"), "\"" + a + "\"") },
                { "current not listed", Doc(Set("\"" + a + "\"", "\"X\"", "0"), "\"" + b + "\"") },
                { "no current", "{ \"sets\": [" + Set("\"" + a + "\"", "\"X\"", "0") + "] }" },
                { "negative slot", Doc(Set("\"" + a + "\"", "\"X\"", "-1"), "\"" + a + "\"") },
                { "huge slot", Doc(Set("\"" + a + "\"", "\"X\"", "70000"), "\"" + a + "\"") },
                { "fractional slot", Doc(Set("\"" + a + "\"", "\"X\"", "1.5"), "\"" + a + "\"") },
                { "empty name", Doc(Set("\"" + a + "\"", "\"  \"", "0"), "\"" + a + "\"") },
                { "control character", Doc(Set("\"" + a + "\"", "\"a\\u0001b\"", "0"), "\"" + a + "\"") },
                { "name not a string", Doc(Set("\"" + a + "\"", "3", "0"), "\"" + a + "\"") },
                { "too many sets", Doc(string.Join(",", Enumerable.Range(0, YlpFormat.MaxTextureSets + 1).Select(i => Set("\"" + new Guid(i + 1, 0, 0, new byte[8]).ToString("D") + "\"", "\"S" + i + "\"", i.ToString()))), "\"" + new Guid(1, 0, 0, new byte[8]).ToString("D") + "\"") },
            };
            foreach (var entry in bad)
                Assert.That(() => YlpFormat.ReadProject(Json(entry.Value)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("project.json"), entry.Key);
            Assert.That(() => new YlpProjectInfo(new YlpTextureSetInfo[0], A), Throws.ArgumentException);
            Assert.That(() => new YlpTextureSetInfo(A, new string('n', 257), 0), Throws.ArgumentException);
        }

        // ───────── 開く ─────────

        [Test] public void AFormatThreeFileOpensAndUnlistedSetsAreReported()
        {
            var files = new Dictionary<string, byte[]>
            {
                { YlpFormat.ProjectName, Project((A, "Body", 0), (B, "Hair", 2)) },
                { YlpFormat.SetEntry(A, YlpArchive.NativeName), Document(A, 1) },
                { YlpFormat.SetEntry(B, YlpArchive.NativeName), Document(B, 2) },
                { YlpFormat.SetEntry(B, SelectionBinary.EntryName), new byte[] { 9 } },
                { YlpFormat.SetEntry(Guid.NewGuid(), YlpArchive.NativeName), new byte[] { 7 } },
            };
            YlpFormat.Stamp(files, Saver, null);
            var opened = YlpFormat.Open(files);
            Assert.That(opened.Upgraded, Is.False);
            Assert.That(opened.Project.Sets.Select(s => s.Name), Is.EqualTo(new[] { "Body", "Hair" }));
            Assert.That(opened.SetFiles(B).Keys, Is.EquivalentTo(new[] { YlpArchive.NativeName, SelectionBinary.EntryName }), "a set's entries without its folder");
            Assert.That(opened.UnknownEntries.Single(), Does.StartWith(YlpFormat.SetsFolder).And.Not.Contain(A.ToString("D")).And.Not.Contain(B.ToString("D")), "a folder of a set the project does not list is reported, not kept");
        }

        [Test] public void AFormatThreeFileWithoutItsProjectOrASetDocumentIsRefused()
        {
            var files = new Dictionary<string, byte[]> { { YlpFormat.SetEntry(A, YlpArchive.NativeName), Document(A, 1) } };
            YlpFormat.Stamp(files, Saver, null);
            Assert.That(() => YlpFormat.Open(files), Throws.TypeOf<InvalidDataException>().With.Message.Contains("project.json"));
            files[YlpFormat.ProjectName] = Project((A, "Body", 0), (B, "Hair", 1));
            Assert.That(() => YlpFormat.Open(files), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Hair").And.Message.Contains(YlpFormat.SetEntry(B, YlpArchive.NativeName)));
            files[YlpFormat.ProjectName] = Json("{ \"sets\": [] }");
            Assert.That(() => YlpFormat.Open(files), Throws.TypeOf<InvalidDataException>());
        }

        [Test] public void FormatTwoMovesIntoOneSetNamedAfterItsDocument()
        {
            var id = Guid.NewGuid(); byte[] native = Document(id, 5);
            var files = new Dictionary<string, byte[]>
            {
                { YlpFormat.InfoName, YlpFormat.WriteInfo(new YlpFormatInfo(2, Saver, Saver)) },
                { YlpArchive.NativeName, native }, { SelectionBinary.EntryName, new byte[] { 1 } }, { YlpFormat.ImportedOriginalName, new byte[] { 2 } },
                { "composite/Color.png", new byte[] { 3 } }, { "composite/Glow.png", new byte[] { 4 } }, { MeshMapBinary.EntryName(MeshMapKind.Position), new byte[] { 5 } },
                { YlpFormat.ViewName, Json("{\n    \"modelAssetGuid\": \"abc\",\n    \"materialSlot\": 3,\n    \"selectedChannel\": 1\n}") },
                { YlpFormat.BrushName, new byte[] { 6 } }, { YlpFormat.ThumbnailName, new byte[] { 7 } }, { "future.bin", new byte[] { 8 } },
            };
            var opened = YlpFormat.Open(files);
            Assert.That(opened.Info.Format, Is.EqualTo(2)); Assert.That(opened.Upgraded, Is.True);
            var set = opened.Project.Sets.Single();
            Assert.That(set.Id, Is.EqualTo(id), "the set is named after the document's ID, so the same file always migrates the same way");
            Assert.That(set.MaterialSlot, Is.EqualTo(3)); Assert.That(set.Name, Is.EqualTo(YlpFormat.MigratedSetName)); Assert.That(opened.Project.CurrentSet, Is.EqualTo(id));
            Assert.That(opened.SetFiles(id).Keys, Is.EquivalentTo(new[] { YlpArchive.NativeName, SelectionBinary.EntryName, YlpFormat.ImportedOriginalName, "composite/Color.png", "composite/Glow.png", MeshMapBinary.EntryName(MeshMapKind.Position) }));
            Assert.That(opened.SetFiles(id)[YlpArchive.NativeName], Is.SameAs(native), "moved, not rewritten");
            Assert.That(opened.Files.Keys.Where(k => !k.StartsWith(YlpFormat.SetsFolder, StringComparison.Ordinal)), Is.EquivalentTo(new[] { YlpFormat.ProjectName, YlpFormat.ViewName, YlpFormat.BrushName, YlpFormat.ThumbnailName, "future.bin" }));
            Assert.That(opened.UnknownEntries, Is.EqualTo(new[] { "future.bin", YlpFormat.SetEntry(id, "composite/Glow.png") }), "unknown entries are still reported after the move");
            Assert.That(files.ContainsKey(YlpArchive.NativeName), Is.True, "Open does not change the dictionary it was given");
            // view.json が読めなければスロット 0 にして知らせる（正本は開く）
            files[YlpFormat.ViewName] = Json("{ \"materialSlot\": \"three\" }");
            var fallback = YlpFormat.Open(files);
            Assert.That(fallback.Project.Sets.Single().MaterialSlot, Is.Zero); Assert.That(fallback.Notes.Single(), Does.Contain("view.json"));
            files.Remove(YlpFormat.ViewName);
            Assert.That(YlpFormat.Open(files).Project.Sets.Single().MaterialSlot, Is.Zero, "no view.json: slot 0");
            files[YlpArchive.NativeName] = new byte[] { 1, 2, 3 };
            Assert.That(() => YlpFormat.Open(files), Throws.TypeOf<InvalidDataException>(), "a document whose header cannot be read is refused, not renamed");
        }

        [Test] public void TheDocumentIdComesFromTheHeaderOfEveryVersion()
        {
            var id = Guid.NewGuid();
            var bytes = Document(id, 1);
            Assert.That(DocumentBinary.ReadId(bytes), Is.EqualTo(id));
            Assert.That(DocumentBinary.Read(bytes).Id, Is.EqualTo(id));
            foreach (int version in new[] { 1, DocumentBinary.CurrentVersion })
            {
                var copy = (byte[])bytes.Clone(); BitConverter.GetBytes(version).CopyTo(copy, 8);
                Assert.That(DocumentBinary.ReadId(copy), Is.EqualTo(id), "version " + version);
            }
            var newer = (byte[])bytes.Clone(); BitConverter.GetBytes(DocumentBinary.CurrentVersion + 1).CopyTo(newer, 8);
            Assert.That(() => DocumentBinary.ReadId(newer), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => DocumentBinary.ReadId(new byte[10]), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => DocumentBinary.ReadId(Encoding.ASCII.GetBytes("NOTPAINT").Concat(new byte[20]).ToArray()), Throws.TypeOf<InvalidDataException>());
        }

        // ───────── 外側の層・復旧 ─────────

        [Test] public void OnlyTheNewManifestAllowsTextureSetFolders()
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { YlpFormat.SetEntry(A, YlpArchive.NativeName), Document(A, 1) },
                { YlpFormat.SetEntry(A, "composite/Color.png"), new byte[] { 1, 2 } },
                { YlpFormat.ProjectName, Project((A, "Body", 0)) },
            };
            var bytes = YlpArchive.Write(files);
            var read = YlpArchive.Read(bytes);
            Assert.That(read.Keys, Is.EquivalentTo(files.Keys), "a set's document counts as the native document");
            // 同じ中身を YLP-1 の manifest で書いたもの（どの版の YoluPainter も書かない）は、セットの置き場を名前で断る
            var z = new ZipBuilder();
            z.Entries.Add(ZipBuilder.Stored("mimetype", Encoding.ASCII.GetBytes(YlpArchive.MimeType)));
            var ordered = files.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
            z.Entries.Add(ZipBuilder.Deflated(YlpArchive.ManifestName, Encoding.UTF8.GetBytes(YlpArchive.ManifestHeaderV1 + "\n" + string.Concat(ordered.Select(f => GenerationStore.Hash(f.Value) + " " + f.Value.Length + " " + f.Key + "\n")))));
            foreach (var f in ordered) z.Entries.Add(ZipBuilder.Stored(f.Key, f.Value));
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unsafe entry name"));
            // セットの置き場の書き方の違い・入れ子・空の名前は断る
            string id = A.ToString("D");
            foreach (var name in new[] { "sets/" + id.ToUpperInvariant() + "/x.bin", "sets/" + A.ToString("N") + "/x.bin", "sets/" + id + "/", "sets/" + id + "/sub/x.bin", "sets/" + id + "/sets/" + id + "/x.bin",
                "sets/" + id + "/../x", "sets/" + id + "//x", "sets/x.bin", "sets/" + id + "/.hidden", "Sets/" + id + "/x.bin", "sets/" + Guid.Empty.ToString("D") + "/x.bin" })
            {
                var with = new Dictionary<string, byte[]>(files) { [name] = new byte[] { 1 } };
                Assert.That(() => YlpArchive.Write(with), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unsafe entry name"), name);
            }
            Assert.That(() => YlpArchive.Write(new Dictionary<string, byte[]> { { YlpFormat.SetEntry(A, "notes.txt"), new byte[1] } }), Throws.ArgumentException, "no native document anywhere");
        }

        [Test] public void RecoveryGenerationsKeepTextureSetFolders()
        {
            string root = Path.Combine(Path.GetTempPath(), "yolupainter-sets-" + Guid.NewGuid().ToString("N"));
            try
            {
                var files = new Dictionary<string, byte[]>
                {
                    { YlpFormat.ProjectName, Project((A, "Body", 0), (B, "Hair", 1)) },
                    { YlpFormat.SetEntry(A, YlpArchive.NativeName), Document(A, 1) }, { YlpFormat.SetEntry(B, YlpArchive.NativeName), Document(B, 2) },
                    { YlpFormat.SetEntry(B, SelectionBinary.EntryName), new byte[] { 4, 5 } },
                };
                YlpFormat.Stamp(files, Saver, null);
                var first = GenerationStore.Commit(root, files);
                var loaded = GenerationStore.Load(root);
                Assert.That(loaded.Files.Keys, Is.EquivalentTo(files.Keys));
                foreach (var f in files) Assert.That(loaded.Files[f.Key], Is.EqualTo(f.Value), f.Key);
                Assert.That(YlpFormat.Open(loaded.Files).Project.Sets.Count, Is.EqualTo(2));
                var next = GenerationStore.Commit(root, files, first.Token);
                Assert.That(GenerationStore.Load(root).Token, Is.EqualTo(next.Token));
                Assert.That(() => GenerationStore.Commit(root, new Dictionary<string, byte[]> { { "sets/" + A.ToString("N") + "/document.utpaint", new byte[1] } }, next.Token), Throws.Exception, "a folder that is not a set is refused");
                Assert.That(() => GenerationStore.Commit(root, new Dictionary<string, byte[]> { { YlpFormat.SetEntry(A, "selection.bin"), new byte[1] } }, next.Token), Throws.ArgumentException, "a generation needs a native document");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
