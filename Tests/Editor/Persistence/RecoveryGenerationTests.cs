using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class RecoveryGenerationTests
    {
        string root;
        [SetUp] public void CreateRoot() => root = Path.Combine(Path.GetTempPath(), "yolupainter-recovery-store-" + Guid.NewGuid().ToString("N"));
        [TearDown] public void DeleteRoot() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        static Dictionary<string, byte[]> Files(int value) => new Dictionary<string, byte[]> { ["document.utpaint"] = new byte[] { (byte)value }, ["unchanged.bin"] = new byte[16384] };
        GenerationSnapshot Commit(int value, string token = null, int keep = 3, Action<string> fault = null) => GenerationStore.Commit(root, Files(value), token, fault, keep, true);
        string Pointer(string name) => File.ReadAllText(Path.Combine(root, name)).Trim();
        string[] Generations() => Directory.GetDirectories(Path.Combine(root, "generations")).Select(Path.GetFileName).ToArray();

        [TestCase(2)] [TestCase(3)] [TestCase(5)] public void OnlyGenerationsBeyondTheChosenCountAreRemoved(int keep)
        {
            GenerationSnapshot saved = null; string previous = null;
            for (int i = 0; i < 9; i++) { previous = saved?.Generation; saved = Commit(i, saved?.Token, keep); }
            Assert.That(Generations(), Has.Length.EqualTo(keep));
            Assert.That(Generations(), Does.Contain(saved.Generation).And.Contain(previous));
            Assert.That(Pointer("current"), Is.EqualTo(saved.Generation)); Assert.That(Pointer("previous"), Is.EqualTo(previous));
            Assert.That(GenerationStore.Load(root).Files["document.utpaint"], Is.EqualTo(new byte[] { 8 }));
            Assert.That(Directory.GetFiles(Path.Combine(root, "contents")), Has.Length.EqualTo(keep + 1), "one shared unchanged file plus retained versions");
        }
        [Test] public void AnInvalidRetentionCountWritesNothing()
        {
            Assert.That(() => Commit(0, keep: 1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(Directory.Exists(root), Is.False);
        }
        [Test] public void AnUnchangedSetWritesNoContentAgain()
        {
            var a = new PaintDocument(64, 64, 16); a.AddLayer("A");
            var b = new PaintDocument(64, 64, 16); b.AddLayer("B");
            var files = new Dictionary<string, byte[]> { [YlpFormat.SetEntry(a.Id, "document.utpaint")] = DocumentBinary.Write(a), [YlpFormat.SetEntry(b.Id, "document.utpaint")] = DocumentBinary.Write(b) };
            var first = GenerationStore.Commit(root, files, shareContents: true, generationsToKeep: 2);
            string untouched = Path.Combine(root, "contents", GenerationStore.Hash(files[YlpFormat.SetEntry(b.Id, "document.utpaint")]) + ".bin");
            var sentinel = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(untouched, sentinel);
            a.GetLayer(a.Layers[0].Id).GetChannel(PaintChannel.Color).SetPixel(1, 2, new Rgba32(1, 2, 3));
            byte[] changed = DocumentBinary.Write(a); files[YlpFormat.SetEntry(a.Id, "document.utpaint")] = changed;
            var second = GenerationStore.Commit(root, files, first.Token, generationsToKeep: 2, shareContents: true);
            Assert.That(second.WrittenContentBytes, Is.EqualTo(changed.LongLength)); Assert.That(second.ReusedContentFiles, Is.EqualTo(1));
            Assert.That(File.GetLastWriteTimeUtc(untouched), Is.EqualTo(sentinel));
            Assert.That(Directory.GetFiles(Path.Combine(root, "contents")), Has.Length.EqualTo(3));
            var third = GenerationStore.Commit(root, files, second.Token, generationsToKeep: 2, shareContents: true);
            Assert.That(third.WrittenContentBytes, Is.Zero); Assert.That(third.ReusedContentFiles, Is.EqualTo(2));
            Assert.That(GenerationStore.Load(root).Files, Is.EquivalentTo(files));
        }
        [TestCase("prune-generation:")] [TestCase("prune-content:")] public void CleanupFailureDoesNotTurnACommittedSaveIntoFailure(string point)
        {
            var first = Commit(1, keep: 2); var second = Commit(2, first.Token, 2); bool injected = false;
            var third = Commit(3, second.Token, 2, p => { if (p.StartsWith(point, StringComparison.Ordinal)) { injected = true; throw new IOException("cleanup refused"); } });
            Assert.That(injected, Is.True); Assert.That(GenerationStore.Load(root).Token, Is.EqualTo(third.Token));
            Assert.That(Generations(), Does.Contain(third.Generation).And.Contain(second.Generation));
            var fourth = Commit(4, third.Token, 2);
            Assert.That(Generations(), Has.Length.EqualTo(2)); Assert.That(GenerationStore.Load(root).Token, Is.EqualTo(fourth.Token));
            Assert.That(Directory.GetFiles(Path.Combine(root, "contents")), Has.Length.EqualTo(3));
        }
        [TestCase("file:document.utpaint")] [TestCase("verified")] [TestCase("generation-renamed")] [TestCase("before-pointer")]
        public void SharedContentSaveFailureKeepsCurrentAndPrevious(string point)
        {
            var first = Commit(1); var second = Commit(2, first.Token);
            Assert.That(() => Commit(3, second.Token, 2, p => { if (p == point) throw new IOException("interrupted"); }), Throws.TypeOf<IOException>());
            Assert.That(GenerationStore.Load(root).Token, Is.EqualTo(second.Token));
            Assert.That(Pointer("previous"), Is.EqualTo(first.Generation));
            Assert.That(Generations(), Does.Contain(first.Generation).And.Contain(second.Generation));
        }
        [Test] public void StagingFoldersAndTheirSharedContentRemainProtected()
        {
            var first = Commit(1); string staged = Path.Combine(root, ".staging-protected"); Directory.CreateDirectory(staged);
            byte[] orphan = new byte[] { 42 }; string hash = GenerationStore.Hash(orphan);
            File.WriteAllBytes(Path.Combine(root, "contents", hash + ".bin"), orphan);
            File.WriteAllText(Path.Combine(staged, "manifest.sha256"), "DOTPAINT-MANIFEST-2\n" + hash + " 1 document.utpaint\n");
            var next = Commit(2, first.Token, 2); next = Commit(3, next.Token, 2);
            Assert.That(Directory.Exists(staged), Is.True); Assert.That(File.Exists(Path.Combine(root, "contents", hash + ".bin")), Is.True);
            File.Delete(Path.Combine(staged, "manifest.sha256"));
            var again = Commit(4, next.Token, 2); Assert.That(again.Token, Is.EqualTo(GenerationStore.Load(root).Token));
            Assert.That(File.Exists(Path.Combine(root, "contents", hash + ".bin")), Is.True, "an incomplete staging folder prevents content collection");
        }
        [Test] public void OldFlatGenerationsLoadAndRemainAsPreviousAfterSharingStarts()
        {
            var first = GenerationStore.Commit(root, Files(1));
            Assert.That(GenerationStore.Load(root).Files, Is.EquivalentTo(Files(1)));
            var second = Commit(2, first.Token, 2);
            Assert.That(File.Exists(Path.Combine(root, "generations", first.Generation, "document.utpaint")), Is.True);
            Assert.That(Pointer("previous"), Is.EqualTo(first.Generation));
            Commit(3, second.Token, 2); Assert.That(Directory.Exists(Path.Combine(root, "generations", first.Generation)), Is.False);
        }
        [Test] public void TamperedSharedContentIsRefusedBeforeAnotherSave()
        {
            var first = Commit(1);
            File.WriteAllBytes(Path.Combine(root, "contents", GenerationStore.Hash(new byte[] { 1 }) + ".bin"), new byte[] { 2 });
            Assert.That(() => GenerationStore.Load(root), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => Commit(3, first.Token), Throws.TypeOf<IOException>());
            Assert.That(Pointer("current"), Is.EqualTo(first.Generation));
        }
        [Test] public void SmallMetadataReadsAreVerifiedAndBoundedInBothLayouts()
        {
            var files = Files(1); files["recovery.json"] = Encoding.UTF8.GetBytes("{}");
            var first = GenerationStore.Commit(root, files);
            Assert.That(GenerationStore.ReadFile(root, "recovery.json", 32), Is.EqualTo(files["recovery.json"]));
            GenerationStore.Commit(root, files, first.Token, shareContents: true);
            Assert.That(GenerationStore.ReadFile(root, "recovery.json", 32), Is.EqualTo(files["recovery.json"]));
            Assert.That(() => GenerationStore.ReadFile(root, "recovery.json", 1), Throws.TypeOf<InvalidDataException>());
            Assert.That(GenerationStore.ReadFile(root, "missing.json", 32), Is.Null);
        }
        [Test] public void ASharedPointerCannotEscapeTheContentFolder()
        {
            var first = Commit(1); string manifest = Path.Combine(root, "generations", first.Generation, "manifest.sha256");
            File.WriteAllText(manifest, "DOTPAINT-MANIFEST-2\n" + new string('.', 64) + " 1 document.utpaint\n");
            Assert.That(() => GenerationStore.Load(root), Throws.TypeOf<InvalidDataException>());
        }
    }
}
