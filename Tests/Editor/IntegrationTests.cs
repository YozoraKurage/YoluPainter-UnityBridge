using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画→Undo→ネイティブ保存→再読込→PSD をまたぐ結合シナリオ。保存系は実ファイルシステムに書く。</summary>
    public sealed class IntegrationTests
    {
        string root;

        [SetUp] public void CreateRoot()
        { root = Path.Combine(Path.GetTempPath(), "yolupainter-test-" + Guid.NewGuid().ToString("N")); }

        [TearDown] public void DeleteRoot()
        { if (Directory.Exists(root)) Directory.Delete(root, true); }

        [Test] public void SparseCanvasAllocatesOnlyTouchedTilesAndIsolatesChannels()
        {
            var doc = new PaintDocument(4096, 4096); var layer = doc.AddLayer("Sparse"); doc.ClearHistory();
            Assert.That(doc.AllocatedBytes, Is.Zero);
            using (var s = doc.BeginStroke(layer.Id, PaintChannel.Color, new BrushSettings { Radius = 4, PressureSize = false, PressureOpacity = false }))
            { s.Add(new BrushSample(2048, 2048)); s.Commit(); }
            Assert.That(doc.AllocatedBytes, Is.LessThanOrEqualTo(4L * 128 * 128 * 4));
            Assert.That(doc.CompositePixel(PaintChannel.Roughness, 2048, 2048).A, Is.EqualTo((byte)0));
            Assert.That(doc.CompositePixel(PaintChannel.Color, 2048, 2048).A, Is.GreaterThan((byte)0));
        }

        [Test] public void UndoRedoAndCancelRestoreExactNativeBytes()
        {
            var d = new PaintDocument(64, 64, 16); var l = d.AddLayer("A"); d.ClearHistory();
            var before = DocumentBinary.Write(d);
            using (var s = d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings { Radius = 8, Color = new Rgba32(90, 10, 130, 128) }))
            { s.Add(new BrushSample(30, 30)); s.Add(new BrushSample(40, 40)); s.Commit(); }
            var painted = DocumentBinary.Write(d);
            Assert.That(painted, Is.Not.EqualTo(before));
            Assert.That(d.Undo()); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            Assert.That(d.Redo()); Assert.That(DocumentBinary.Write(d), Is.EqualTo(painted));
            using (var s = d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings())) { s.Add(new BrushSample(10, 10)); s.Cancel(); }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(painted));
        }

        [Test] public void NativeArchiveKeepsAttributesAndHiddenRgb()
        {
            var d = new PaintDocument(35, 19, 16); var l = d.AddLayer("透明 日本語 🎨");
            l.GetChannel(PaintChannel.Color).SetPixel(2, 3, new Rgba32(41, 53, 67, 0));
            l.GetChannel(PaintChannel.Height).SetPixel(34, 18, new Rgba32(102, 102, 102, 192)); d.SetChannelEnabled(l.Id, PaintChannel.Height, false);
            d.SetLayerBlendMode(l.Id, LayerBlendMode.Screen); d.SetLayerOpacity(l.Id, .37); d.SetLayerVisibility(l.Id, false);
            var bytes = DocumentBinary.Write(d); var restored = DocumentBinary.Read(bytes);
            Assert.That(DocumentBinary.Write(restored), Is.EqualTo(bytes));
            Assert.That(restored.GetLayer(l.Id).GetChannel(PaintChannel.Color).GetPixel(2, 3), Is.EqualTo(new Rgba32(41, 53, 67, 0)));
            Assert.That(restored.GetLayer(l.Id).IsChannelEnabled(PaintChannel.Height), Is.False);
        }

        [Test] public void NativeReaderRefusesTruncationAndUnknownVersion()
        {
            var d = new PaintDocument(16, 16, 16); d.AddLayer("A"); var bytes = DocumentBinary.Write(d);
            Assert.That(() => DocumentBinary.Read(bytes.Take(bytes.Length - 1).ToArray()), Throws.Exception);
            bytes[8] = 127;
            Assert.That(() => DocumentBinary.Read(bytes), Throws.Exception);
        }

        [Test] public void InterruptedSaveLeavesPreviousCurrentGenerationValid()
        {
            var d = new PaintDocument(16, 16, 16); var l = d.AddLayer("A"); var bytes = DocumentBinary.Write(d);
            var first = GenerationStore.Commit(root, Files(bytes));
            foreach (string point in new[] { "file:document.utpaint", "verified", "generation-renamed", "before-pointer" })
            {
                Assert.That(() => GenerationStore.Commit(root, Files(bytes), first.Token, p => { if (p == point) throw new IOException("injected"); }), Throws.Exception, point);
                Assert.That(GenerationStore.Load(root).Token, Is.EqualTo(first.Token), point);
                Assert.That(GenerationStore.Load(root).Files["document.utpaint"], Is.EqualTo(bytes), point);
            }
            l.GetChannel(PaintChannel.Color).SetPixel(0, 0, new Rgba32(1, 2, 3));
            var next = GenerationStore.Commit(root, Files(DocumentBinary.Write(d)), first.Token);
            Assert.That(next.Token, Is.Not.EqualTo(first.Token));
            Assert.That(() => GenerationStore.Commit(root, Files(bytes), first.Token), Throws.Exception, "stale token");
            File.AppendAllText(Path.Combine(root, "generations", next.Generation, "document.utpaint"), "tamper");
            Assert.That(GenerationStore.HasExternalChange(root, next.Token));
            Assert.That(() => GenerationStore.Load(root), Throws.Exception);
        }

        [Test] public void CommittedGenerationSurvivesInterruptedAcknowledgement()
        {
            var d = new PaintDocument(16, 16, 16); d.AddLayer("A"); var files = Files(DocumentBinary.Write(d));
            Assert.That(() => GenerationStore.Commit(root, files, null, p => { if (p == "after-pointer") throw new IOException("lost ack"); }), Throws.Exception);
            Assert.That(GenerationStore.Load(root).Files["document.utpaint"], Is.EqualTo(files["document.utpaint"]));
        }

        [Test] public void PsdBridgeRoundTripsLayeredNativeDocument()
        {
            var d = new PaintDocument(32, 24, 16); var a = d.AddLayer("底 🎨"); var b = d.AddLayer("上");
            a.GetChannel(PaintChannel.Color).SetPixel(2, 3, new Rgba32(90, 80, 70, 255));
            b.GetChannel(PaintChannel.Color).SetPixel(5, 7, new Rgba32(30, 40, 50, 128));
            b.GetChannel(PaintChannel.Color).SetPixel(1, 9, new Rgba32(11, 12, 13, 0));
            var read = PsdCodec.Read(PsdCodec.Write(PsdBridge.Export(d, PaintChannel.Color)));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster));
            var imported = PsdBridge.Import(read);
            Assert.That(imported.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            Assert.That(imported.Layers[1].GetChannel(PaintChannel.Color).GetPixel(1, 9), Is.EqualTo(new Rgba32(11, 12, 13, 0)));
            d.SetLayerBlendMode(b.Id, LayerBlendMode.Multiply);
            imported = PsdBridge.Import(PsdCodec.Read(PsdCodec.Write(PsdBridge.Export(d, PaintChannel.Color))));
            Assert.That(imported.Layers[1].BlendMode, Is.EqualTo(LayerBlendMode.Multiply), "non-Normal blend is written as a mode, not flattened");
            Assert.That(imported.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(1, 2, 3) } });
            Assert.That(() => PsdBridge.Export(d, PaintChannel.Color), Throws.InvalidOperationException, "a fill layer must not be baked into pixels");
        }

        [Test] public void SaveAndPsdExportRefuseProvisionalStroke()
        {
            var d = new PaintDocument(16, 16, 16); var l = d.AddLayer("A");
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings()))
            {
                stroke.Add(new BrushSample(8, 8));
                Assert.That(() => DocumentBinary.Write(d), Throws.Exception);
                Assert.That(() => PsdBridge.Export(d, PaintChannel.Color), Throws.Exception);
                stroke.Cancel();
            }
        }

        static Dictionary<string, byte[]> Files(byte[] document)
        { return new Dictionary<string, byte[]> { { "document.utpaint", document } }; }
    }
}
