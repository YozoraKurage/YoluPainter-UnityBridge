using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// PSD のレイヤーのロック: lspf（ビット 0 透明部分・1 画像・2 位置・31 すべて）とレイヤーの印のビット 0（透明部分）を取り込みでネイティブの
    /// ロックにし、書き出しで同じ lspf と印を書く。ネイティブに無いビット（3 = アートボードへの自動ネストの防止、ほか）とフォルダーの閉じる
    /// 区切りの記録のロックは黙って捨てずに NotCarriedIntoExport で知らせる。すべてのロックは 0x80000000 だけで書く（psd-tools の
    /// complete の定義と同じ）。ロックは描画に関わらない（合成は同じ）。
    /// 実機の Photoshop / CLIP STUDIO では確かめていない（Adobe の仕様と psd-tools の ProtectedSetting の定義による）。
    /// </summary>
    public sealed class PsdLockTests
    {
        static readonly byte[] Composite = { 10, 20, 30, 255, 40, 50, 60, 255 };
        static byte[] Lspf(uint value) => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

        static List<PsdFixture.Record> Layers(uint? lspfA = null, byte flagsA = 8, uint? lspfB = null)
        {
            var a = PsdFixture.Raster(1, "a", 0, 0, 1, 1, new Rgba32(10, 20, 30, 255)); a.Flags = flagsA;
            var b = PsdFixture.Raster(2, "b", 1, 0, 1, 1, new Rgba32(40, 50, 60, 255));
            if (lspfA.HasValue) a.Tags.Add(new KeyValuePair<string, byte[]>("lspf", Lspf(lspfA.Value)));
            if (lspfB.HasValue) b.Tags.Add(new KeyValuePair<string, byte[]>("lspf", Lspf(lspfB.Value)));
            return new List<PsdFixture.Record> { a, b };
        }

        [TestCase(1u, (byte)8, LayerLocks.Transparency)]
        [TestCase(0u, (byte)(8 | 1), LayerLocks.Transparency)] // レイヤーの印のビット 0 だけ（古い書き手）
        [TestCase(2u, (byte)8, LayerLocks.Pixels)]
        [TestCase(4u, (byte)8, LayerLocks.Position)]
        [TestCase(0x80000000u, (byte)8, LayerLocks.All)]
        [TestCase(0x80000007u, (byte)(8 | 1), LayerLocks.All | LayerLocks.Transparency | LayerLocks.Pixels | LayerLocks.Position)]
        public void LocksAreImportedAndExportedWithTheSameBitsAndNoDiagnostics(uint lspf, byte flags, LayerLocks expected)
        {
            byte[] bytes = PsdFixture.Build(2, 1, Layers(lspf, flags), Composite);
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            Assert.That(read.Diagnostics, Is.Empty, "a lock is carried, so nothing to report");
            var native = PsdBridge.Import(read);
            var a = native.Layers.Single(l => l.Name == "a"); var b = native.Layers.Single(l => l.Name == "b");
            Assert.That((a.Locks, b.Locks), Is.EqualTo((expected, LayerLocks.None)));
            Assert.That(native.CanUndo, Is.False, "the locks are loaded, not edited");
            Assert.That(native.Composite(PaintChannel.Color), Is.EqualTo(Composite), "locks do not render");

            byte[] exported = PsdCodec.Write(PsdBridge.Export(native, PaintChannel.Color));
            var again = PsdCodec.Read(exported);
            Assert.That(again.Diagnostics, Is.Empty, PsdFixture.Show(again));
            var reread = PsdBridge.Import(again);
            // すべてのロックは 0x80000000 だけで書く（その下の個別のロックは書かない。効いているロックは同じ）
            Assert.That(reread.Layers.Select(l => (l.Name, l.Locks)), Is.EqualTo(native.Layers.Select(l => (l.Name, (l.Locks & LayerLocks.All) != 0 ? LayerLocks.All : l.Locks))));
            Assert.That(reread.Layers.Select(l => reread.EffectiveLocks(l.Id)), Is.EqualTo(native.Layers.Select(l => native.EffectiveLocks(l.Id))));
            int lspfAt = Encoding.ASCII.GetString(exported).IndexOf("lspf", StringComparison.Ordinal);
            Assert.That(lspfAt, Is.GreaterThan(0), "lspf is written for the locked layer");
            Assert.That(Encoding.ASCII.GetString(exported).IndexOf("lspf", lspfAt + 4, StringComparison.Ordinal), Is.EqualTo(-1), "and only for it");
            uint written = (uint)(exported[lspfAt + 8] << 24 | exported[lspfAt + 9] << 16 | exported[lspfAt + 10] << 8 | exported[lspfAt + 11]);
            Assert.That(written, Is.EqualTo(lspf == 0 ? 1u : (lspf & 0x80000000u) != 0 ? 0x80000000u : lspf), "the same protection bits (the flag-only transparency lock is written in both places; lock all alone)");
        }

        [Test] public void AnUnlockedDocumentWritesNoLspf()
        {
            var native = PsdBridge.Import(PsdCodec.Read(PsdFixture.Build(2, 1, Layers(), Composite)));
            var exported = PsdCodec.Write(PsdBridge.Export(native, PaintChannel.Color));
            Assert.That(Encoding.ASCII.GetString(exported).Contains("lspf"), Is.False);
            native.SetLayerLocks(native.Layers[0].Id, LayerLocks.Transparency);
            exported = PsdCodec.Write(PsdBridge.Export(native, PaintChannel.Color));
            var read = PsdCodec.Read(exported);
            Assert.That(read.Document.Layers.Single(l => l.Name == "a").Locks, Is.EqualTo(LayerLocks.Transparency));
        }

        [TestCase(8u, "Prevent auto-nesting lock (lspf bit 3)", LayerLocks.None)]
        [TestCase(8u | 2u, "Prevent auto-nesting lock (lspf bit 3)", LayerLocks.Pixels)]
        [TestCase(0x40u, "Layer lock bits 0x00000040 without a native lock (lspf)", LayerLocks.None)]
        public void LockBitsWithoutANativeLockAreReportedNotDropped(uint lspf, string mention, LayerLocks carried)
        {
            byte[] bytes = PsdFixture.Build(2, 1, Layers(lspf), Composite);
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            var note = read.Diagnostics.Single();
            Assert.That(note.Code, Is.EqualTo(PsdCodec.NotCarriedIntoExport)); Assert.That(note.Message, Does.Contain(mention));
            Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes), "the original keeps them");
            Assert.That(PsdBridge.Import(read).Layers.Single(l => l.Name == "a").Locks, Is.EqualTo(carried));
        }

        [Test] public void AFoldersLocksAreCarriedAndItsClosingDividersAreReported()
        {
            var a = PsdFixture.Raster(1, "a", 0, 0, 2, 1, new Rgba32(10, 20, 30, 255));
            var divider = PsdFixture.Divider(5); divider.Tags.Add(new KeyValuePair<string, byte[]>("lspf", Lspf(4)));
            var folder = PsdFixture.Folder(6, "folder", "pass"); folder.Tags.Add(new KeyValuePair<string, byte[]>("lspf", Lspf(0x80000000u)));
            byte[] bytes = PsdFixture.Build(2, 1, new List<PsdFixture.Record> { divider, a, folder }, new byte[] { 10, 20, 30, 255, 10, 20, 30, 255 });
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            Assert.That(read.Diagnostics.Single().Message, Does.Contain("closing divider"));
            var native = PsdBridge.Import(read);
            var group = native.Layers.Single(l => l.IsGroup);
            Assert.That(group.Locks, Is.EqualTo(LayerLocks.All));
            Assert.That(native.EffectiveLocks(native.Layers.Single(l => l.Name == "a").Id) & LayerLocks.Pixels, Is.EqualTo(LayerLocks.Pixels), "a locked folder locks its contents");
            var again = PsdCodec.Read(PsdCodec.Write(PsdBridge.Export(native, PaintChannel.Color)));
            Assert.That(again.Diagnostics, Is.Empty, "the export writes the folder's lock on the folder only");
            Assert.That(again.Document.Layers.Single().Locks, Is.EqualTo(LayerLocks.All));
        }

        [Test] public void ARepeatedOrMalformedLspfIsPreserveOnly()
        {
            var layers = Layers(1); layers[0].Tags.Add(new KeyValuePair<string, byte[]>("lspf", Lspf(2)));
            Assert.That(PsdCodec.Read(PsdFixture.Build(2, 1, layers, Composite)).Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
            var odd = Layers(); odd[0].Tags.Add(new KeyValuePair<string, byte[]>("lspf", new byte[] { 0, 0, 0, 1, 0, 0 }));
            Assert.That(PsdCodec.Read(PsdFixture.Build(2, 1, odd, Composite)).Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
        }
    }
}
