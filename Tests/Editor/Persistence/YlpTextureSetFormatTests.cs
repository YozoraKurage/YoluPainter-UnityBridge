using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

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

        static byte[] Project(params (Guid id, string name, int slot)[] sets) => YlpFormat.WriteProject(new YlpProjectInfo(sets.Select(s => new YlpTextureSetInfo(s.id, s.name, YlpMaterialRef.PendingSlot(s.slot))), sets[0].id));
        const string Guid1 = "0123456789abcdef0123456789abcdef", Guid2 = "fedcba9876543210fedcba9876543210";

        // ───────── project.json ─────────

        [Test] public void TheProjectRecordRoundTripsEveryKindOfMaterial()
        {
            Guid c = Guid.NewGuid(), d = Guid.NewGuid();
            var materials = new[] { YlpMaterialRef.Material("Body \"skin\" 肌", Guid1, -7349123456789012345), YlpMaterialRef.PendingSlot(3), YlpMaterialRef.UnassignedSlots, YlpMaterialRef.Material("") };
            var project = new YlpProjectInfo(new[] { new YlpTextureSetInfo(A, "Body \"skin\"", materials[0]), new YlpTextureSetInfo(B, "髪", materials[1]),
                new YlpTextureSetInfo(c, "Unassigned", materials[2]), new YlpTextureSetInfo(d, "Scene", materials[3]) }, B);
            var read = YlpFormat.ReadProject(YlpFormat.WriteProject(project));
            Assert.That(read.Sets.Select(s => (s.Id, s.Name)), Is.EqualTo(new[] { (A, "Body \"skin\""), (B, "髪"), (c, "Unassigned"), (d, "Scene") }), "the order is kept");
            Assert.That(read.Sets.Select(s => s.Material), Is.EqualTo(materials), "names, GUIDs, 64-bit local file IDs, the unassigned slots and slot numbers come back");
            Assert.That(read.CurrentSet, Is.EqualTo(B));
            string json = Encoding.UTF8.GetString(YlpFormat.WriteProject(project));
            Assert.That(json, Does.Contain("\"" + A.ToString("D") + "\""), "ids are written as lower-case GUIDs with hyphens");
            Assert.That(json, Does.Contain("\"material\": { \"slot\": 3 }").And.Contain("\"material\": { \"unassigned\": true }").And.Contain("\"fileId\": -7349123456789012345").And.Not.Contain("materialSlot"));
            var extra = YlpFormat.ReadProject(Json("{ \"future\": 1, \"sets\": [ { \"id\": \"" + A.ToString("D") + "\", \"name\": \"X\", \"material\": { \"name\": \"M\", \"tint\": 1 }, \"udim\": [1001] } ], \"current\": \"" + A.ToString("D") + "\" }"));
            Assert.That(extra.Sets.Single().Material, Is.EqualTo(YlpMaterialRef.Material("M")), "keys a later minor version adds are skipped");
            // 識別子の無い同じ名前のマテリアルは 2 つあってよい（シーンの中で作った別のマテリアル）
            Assert.That(() => new YlpProjectInfo(new[] { new YlpTextureSetInfo(A, "X", YlpMaterialRef.Material("Body")), new YlpTextureSetInfo(B, "Y", YlpMaterialRef.Material("Body")) }, A), Throws.Nothing);
            Assert.That(() => new YlpProjectInfo(new[] { new YlpTextureSetInfo(A, "X", YlpMaterialRef.Material("Body", Guid1, 5)), new YlpTextureSetInfo(B, "Y", YlpMaterialRef.Material("Other", Guid1, 5)) }, A),
                Throws.ArgumentException.With.Message.Contains("same material"), "one material asset has one texture set");
        }

        [Test] public void BrokenProjectRecordsAreRefused()
        {
            string a = A.ToString("D"), b = B.ToString("D");
            string Set(string id, string name, string slot) => "{ \"id\": " + id + ", \"name\": " + name + ", \"material\": { \"slot\": " + slot + " } }";
            string Material(string id, string name, string material) => "{ \"id\": " + id + ", \"name\": " + name + ", \"material\": " + material + " }";
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
                { "the slot number of format 5", Doc("{ \"id\": \"" + a + "\", \"name\": \"X\", \"materialSlot\": 0 }", "\"" + a + "\"") },
                { "no material", Doc("{ \"id\": \"" + a + "\", \"name\": \"X\" }", "\"" + a + "\"") },
                { "material not an object", Doc(Material("\"" + a + "\"", "\"X\"", "\"Body\""), "\"" + a + "\"") },
                { "empty material", Doc(Material("\"" + a + "\"", "\"X\"", "{ }"), "\"" + a + "\"") },
                { "two kinds", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"name\": \"Body\", \"slot\": 0 }"), "\"" + a + "\"") },
                { "unassigned and a name", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"unassigned\": true, \"name\": \"Body\" }"), "\"" + a + "\"") },
                { "unassigned not a bool", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"unassigned\": 1 }"), "\"" + a + "\"") },
                { "unassigned false alone", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"unassigned\": false }"), "\"" + a + "\"") },
                { "material name not a string", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"name\": 3 }"), "\"" + a + "\"") },
                { "material name with a control character", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"name\": \"a\\u0002\" }"), "\"" + a + "\"") },
                { "material name too long", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"name\": \"" + new string('m', 257) + "\" }"), "\"" + a + "\"") },
                { "guid without fileId", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"name\": \"Body\", \"guid\": \"" + Guid1 + "\" }"), "\"" + a + "\"") },
                { "fileId without guid", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"name\": \"Body\", \"fileId\": 2100000 }"), "\"" + a + "\"") },
                { "upper-case guid", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"name\": \"Body\", \"guid\": \"" + Guid1.ToUpperInvariant() + "\", \"fileId\": 1 }"), "\"" + a + "\"") },
                { "short guid", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"name\": \"Body\", \"guid\": \"0123\", \"fileId\": 1 }"), "\"" + a + "\"") },
                { "fractional fileId", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"name\": \"Body\", \"guid\": \"" + Guid1 + "\", \"fileId\": 1.5 }"), "\"" + a + "\"") },
                { "same material asset twice", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"name\": \"Body\", \"guid\": \"" + Guid2 + "\", \"fileId\": 4 }") + "," + Material("\"" + b + "\"", "\"Y\"", "{ \"name\": \"Skin\", \"guid\": \"" + Guid2 + "\", \"fileId\": 4 }"), "\"" + a + "\"") },
                { "unassigned twice", Doc(Material("\"" + a + "\"", "\"X\"", "{ \"unassigned\": true }") + "," + Material("\"" + b + "\"", "\"Y\"", "{ \"unassigned\": true }"), "\"" + a + "\"") },
                { "too many sets", Doc(string.Join(",", Enumerable.Range(0, YlpFormat.MaxTextureSets + 1).Select(i => Set("\"" + new Guid(i + 1, 0, 0, new byte[8]).ToString("D") + "\"", "\"S" + i + "\"", i.ToString()))), "\"" + new Guid(1, 0, 0, new byte[8]).ToString("D") + "\"") },
            };
            foreach (var entry in bad)
                Assert.That(() => YlpFormat.ReadProject(Json(entry.Value)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("project.json"), entry.Key);
            Assert.That(() => new YlpProjectInfo(new YlpTextureSetInfo[0], A), Throws.ArgumentException);
            Assert.That(() => new YlpTextureSetInfo(A, new string('n', 257), YlpMaterialRef.PendingSlot(0)), Throws.ArgumentException);
            Assert.That(() => new YlpTextureSetInfo(A, "X", null), Throws.ArgumentNullException);
            Assert.That(() => YlpMaterialRef.PendingSlot(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => YlpMaterialRef.Material("Body", "XYZ"), Throws.ArgumentException);
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
            Assert.That(set.Material, Is.EqualTo(YlpMaterialRef.PendingSlot(3)), "the slot number waits for a model to name its material"); Assert.That(set.Name, Is.EqualTo(YlpFormat.MigratedSetName)); Assert.That(opened.Project.CurrentSet, Is.EqualTo(id));
            Assert.That(opened.SetFiles(id).Keys, Is.EquivalentTo(new[] { YlpArchive.NativeName, SelectionBinary.EntryName, YlpFormat.ImportedOriginalName, "composite/Color.png", "composite/Glow.png", MeshMapBinary.EntryName(MeshMapKind.Position) }));
            Assert.That(opened.SetFiles(id)[YlpArchive.NativeName], Is.SameAs(native), "moved, not rewritten");
            Assert.That(opened.Files.Keys.Where(k => !k.StartsWith(YlpFormat.SetsFolder, StringComparison.Ordinal)), Is.EquivalentTo(new[] { YlpFormat.ProjectName, YlpFormat.ViewName, YlpFormat.BrushName, YlpFormat.ThumbnailName, "future.bin" }));
            Assert.That(opened.UnknownEntries, Is.EqualTo(new[] { "future.bin", YlpFormat.SetEntry(id, "composite/Glow.png") }), "unknown entries are still reported after the move");
            Assert.That(files.ContainsKey(YlpArchive.NativeName), Is.True, "Open does not change the dictionary it was given");
            // view.json が読めなければスロット 0 にして知らせる（正本は開く）
            files[YlpFormat.ViewName] = Json("{ \"materialSlot\": \"three\" }");
            var fallback = YlpFormat.Open(files);
            Assert.That(fallback.Project.Sets.Single().Material, Is.EqualTo(YlpMaterialRef.PendingSlot(0))); Assert.That(fallback.Notes.Single(), Does.Contain("view.json"));
            files.Remove(YlpFormat.ViewName);
            Assert.That(YlpFormat.Open(files).Project.Sets.Single().Material, Is.EqualTo(YlpMaterialRef.PendingSlot(0)), "no view.json: slot 0");
            files[YlpArchive.NativeName] = new byte[] { 1, 2, 3 };
            Assert.That(() => YlpFormat.Open(files), Throws.TypeOf<InvalidDataException>(), "a document whose header cannot be read is refused, not renamed");
        }

        /// <summary>6 → 7（形式 5 のファイルも同じ段を通る）: project.json の materialSlot は、まだマテリアルに結び付けていないスロットの番号になる（どのマテリアルかはモデルを読んだ
        /// 窓が決める）。ほかのエントリは変えない。形式 5 の並びの壊れ（同じスロット 2 回・番号でない）は、移行でも断る。</summary>
        [Test] public void FormatFiveSlotNumbersBecomeSlotsWaitingForAModel()
        {
            string a = A.ToString("D"), b = B.ToString("D");
            var files = new Dictionary<string, byte[]>
            {
                { YlpFormat.InfoName, YlpFormat.WriteInfo(new YlpFormatInfo(5, Saver, Saver)) },
                { YlpFormat.ProjectName, Json("{ \"sets\": [ { \"id\": \"" + a + "\", \"name\": \"Skin\", \"materialSlot\": 0 }, { \"id\": \"" + b + "\", \"name\": \"Hair\", \"materialSlot\": 2 } ], \"current\": \"" + b + "\" }") },
                { YlpFormat.SetEntry(A, YlpArchive.NativeName), Document(A, 1) }, { YlpFormat.SetEntry(B, YlpArchive.NativeName), Document(B, 2) },
            };
            var opened = YlpFormat.Open(files);
            Assert.That(opened.Info.Format, Is.EqualTo(5)); Assert.That(opened.Upgraded, Is.True); Assert.That(opened.Notes, Is.Empty);
            Assert.That(opened.Project.Sets.Select(s => (s.Id, s.Name, s.Material)), Is.EqualTo(new[] { (A, "Skin", YlpMaterialRef.PendingSlot(0)), (B, "Hair", YlpMaterialRef.PendingSlot(2)) }));
            Assert.That(opened.Project.CurrentSet, Is.EqualTo(B));
            Assert.That(opened.Files[YlpFormat.SetEntry(A, YlpArchive.NativeName)], Is.SameAs(files[YlpFormat.SetEntry(A, YlpArchive.NativeName)]), "the documents are not rewritten");
            Assert.That(Encoding.UTF8.GetString(opened.Files[YlpFormat.ProjectName]), Does.Contain("\"material\": { \"slot\": 2 }").And.Not.Contain("materialSlot"), "project.json is in the current form");
            // 書き戻すと今の形式で、読み直しても同じ
            var write = new Dictionary<string, byte[]>(opened.Files); YlpFormat.Stamp(write, Saver, opened.Info.CreatedBy);
            var again = YlpFormat.Open(write);
            Assert.That(again.Info.Format, Is.EqualTo(YlpFormat.Current)); Assert.That(again.Project.Sets.Select(s => s.Material), Is.EqualTo(opened.Project.Sets.Select(s => s.Material)));
            files[YlpFormat.ProjectName] = Json("{ \"sets\": [ { \"id\": \"" + a + "\", \"name\": \"X\", \"materialSlot\": 1 }, { \"id\": \"" + b + "\", \"name\": \"Y\", \"materialSlot\": 1 } ], \"current\": \"" + a + "\" }");
            Assert.That(() => YlpFormat.Open(files), Throws.TypeOf<InvalidDataException>().With.Message.Contains("same material"), "two sets of one slot");
            files[YlpFormat.ProjectName] = Json("{ \"sets\": [ { \"id\": \"" + a + "\", \"name\": \"X\", \"materialSlot\": \"0\" } ], \"current\": \"" + a + "\" }");
            Assert.That(() => YlpFormat.Open(files), Throws.TypeOf<InvalidDataException>().With.Message.Contains("materialSlot"));
            Assert.That(YlpFormat.ReadProjectOfAnyFormat(Json("{ \"sets\": [ { \"id\": \"" + a + "\", \"name\": \"X\", \"materialSlot\": 4 } ], \"current\": \"" + a + "\" }")).Sets.Single().Material,
                Is.EqualTo(YlpMaterialRef.PendingSlot(4)), "the importer's list reads either form");
        }

        /// <summary>
        /// マテリアルごとのセットを足す前（0.2.0 の 42eef75）の YoluPainter のウィンドウの保存で作った形式 5 のファイル（Fixtures~/format5-shared-materials.ylp。1 つのメッシュの 3 つのサブメッシュ
        /// Skin・Skin（同じマテリアル）・Hair の 3 つのセット、スマートマテリアル 1 つ、セットごとのメッシュマップ 2 枚）: 形式 5 として開き、
        /// スロットの番号はまだ結び付けていない番号になり、ほかのエントリは変わらない。メッシュマップは版 2 で読め、スロットは 1 つ。
        /// 今の形式で書き戻して読み直すと、project.json のほかは同じバイト列。
        /// </summary>
        [Test] public void AFormatFiveFileFromBeforeMaterialSetsOpens()
        {
            var bytes = File.ReadAllBytes(Yozolab.YoluPainter.Editor.PackagePaths.Physical("Tests/Editor/Persistence/Fixtures~/format5-shared-materials.ylp"));
            var snapshot = YlpArchive.Read(bytes);
            var opened = YlpFormat.Open(snapshot);
            Assert.That(opened.Info.Format, Is.EqualTo(5)); Assert.That(opened.Upgraded, Is.True); Assert.That(opened.UnknownEntries, Is.Empty); Assert.That(opened.Notes, Is.Empty);
            Assert.That(opened.Project.Sets.Select(s => (s.Name, s.Material)), Is.EqualTo(new[] { ("Skin", YlpMaterialRef.PendingSlot(0)), ("Skin 2", YlpMaterialRef.PendingSlot(1)), ("Hair", YlpMaterialRef.PendingSlot(2)) }));
            Assert.That(opened.Files.Keys, Is.EquivalentTo(snapshot.Keys.Where(k => k != YlpFormat.InfoName)), "the steps 5 → 7 move nothing");
            foreach (var entry in opened.Files) if (entry.Key != YlpFormat.ProjectName) Assert.That(entry.Value, Is.EqualTo(snapshot[entry.Key]), entry.Key);
            Assert.That(opened.Resources.Single().IsSmart, Is.True, "the smart material of format 5");
            foreach (var set in opened.Project.Sets)
            {
                var files = opened.SetFiles(set.Id);
                var native = files[YlpArchive.NativeName];
                var document = DocumentBinary.Read(native);
                var current = (byte[])native.Clone(); BitConverter.GetBytes(DocumentBinary.CurrentVersion).CopyTo(current, 8);
                Assert.That(DocumentBinary.Write(document), Is.EqualTo(current), set.Name + ": the native source reads and writes back byte for byte");
                var maps = files.Where(f => f.Key.StartsWith(MeshMapBinary.EntryPrefix, StringComparison.Ordinal)).Select(f => (version: BitConverter.ToInt32(f.Value, 8), map: MeshMapBinary.Read(f.Value))).ToList();
                Assert.That(maps.Select(m => m.map.Kind), Is.EquivalentTo(new[] { MeshMapKind.WorldNormal, MeshMapKind.Position }), set.Name);
                Assert.That(maps.Select(m => m.version), Is.All.EqualTo(2), "mesh maps of format 5 are version 2");
                Assert.That(maps.Select(m => m.map.Provenance.TargetSlots), Is.All.EqualTo(new[] { set.Material.Slot }), set.Name + ": one slot each");
                var rewritten = MeshMapBinary.Read(MeshMapBinary.Write(maps[0].map));
                Assert.That(rewritten.Provenance.ConditionKey, Is.EqualTo(maps[0].map.Provenance.ConditionKey), "a one-slot bake keeps its condition key, so it does not turn stale");
            }
            var write = new Dictionary<string, byte[]>(opened.Files);
            YlpFormat.Stamp(write, Saver, opened.Info.CreatedBy);
            var again = YlpFormat.Open(YlpArchive.Read(YlpArchive.Write(write)));
            Assert.That(again.Info.Format, Is.EqualTo(YlpFormat.Current)); Assert.That(again.Upgraded, Is.False); Assert.That(again.UnknownEntries, Is.Empty);
            foreach (var entry in opened.Files) Assert.That(again.Files[entry.Key], Is.EqualTo(entry.Value), entry.Key);
        }

        /// <summary>
        /// 形式 7 を足す前（0.2.0 の 9904157）の YoluPainter のウィンドウの保存で作った形式 6 のファイル（Fixtures~/format6.ylp。Body（Skin・Cloth）と
        /// Head（Skin）のスロットごとの 3 つのセット Skin・Cloth・Skin 2、ブラシのリソース 1 つ、セットごとの Position のメッシュマップ）: 形式 6 として
        /// 開き、スロットの番号はまだ結び付けていない番号になり、ほかのエントリは変わらない（ブラシのリソースも読める）。今の形式で書き戻して
        /// 読み直すと、project.json のほかは同じバイト列。
        /// </summary>
        [Test] public void AFormatSixFileFromBeforeMaterialSetsOpens()
        {
            var bytes = File.ReadAllBytes(Yozolab.YoluPainter.Editor.PackagePaths.Physical("Tests/Editor/Persistence/Fixtures~/format6.ylp"));
            var snapshot = YlpArchive.Read(bytes);
            var opened = YlpFormat.Open(snapshot);
            Assert.That(opened.Info.Format, Is.EqualTo(6)); Assert.That(opened.Upgraded, Is.True); Assert.That(opened.UnknownEntries, Is.Empty); Assert.That(opened.Notes, Is.Empty);
            Assert.That(opened.Project.Sets.Select(s => (s.Name, s.Material)), Is.EqualTo(new[] { ("Skin", YlpMaterialRef.PendingSlot(0)), ("Cloth", YlpMaterialRef.PendingSlot(1)), ("Skin 2", YlpMaterialRef.PendingSlot(2)) }));
            Assert.That(opened.Files.Keys, Is.EquivalentTo(snapshot.Keys.Where(k => k != YlpFormat.InfoName)), "the step 6 → 7 moves nothing");
            foreach (var entry in opened.Files) if (entry.Key != YlpFormat.ProjectName) Assert.That(entry.Value, Is.EqualTo(snapshot[entry.Key]), entry.Key);
            Assert.That(opened.Resources.Select(r => (r.Kind, r.Name)), Is.EqualTo(new[] { (ResourceKind.Brush, "Fixture brush") }), "the brush resource of format 6");
            foreach (var set in opened.Project.Sets)
            {
                var files = opened.SetFiles(set.Id);
                var native = files[YlpArchive.NativeName];
                var document = DocumentBinary.Read(native);
                Assert.That(document.Id, Is.EqualTo(set.Id));
                var map = MeshMapBinary.Read(files[MeshMapBinary.EntryName(MeshMapKind.Position)]);
                Assert.That(map.Provenance.TargetSlots, Is.EqualTo(new[] { set.Material.Slot }), set.Name + ": one slot");
            }
            var write = new Dictionary<string, byte[]>(opened.Files);
            YlpFormat.Stamp(write, Saver, opened.Info.CreatedBy);
            var again = YlpFormat.Open(YlpArchive.Read(YlpArchive.Write(write)));
            Assert.That(again.Info.Format, Is.EqualTo(YlpFormat.Current)); Assert.That(again.Upgraded, Is.False); Assert.That(again.UnknownEntries, Is.Empty);
            foreach (var entry in opened.Files) Assert.That(again.Files[entry.Key], Is.EqualTo(entry.Value), entry.Key);
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
