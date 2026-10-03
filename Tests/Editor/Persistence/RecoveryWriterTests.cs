using System;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class RecoveryWriterTests
    {
        string root;
        [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "yolupainter-writer-" + Guid.NewGuid().ToString("N")); }
        [TearDown] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        static PaintDocument Document()
        {
            var d = new PaintDocument(32, 32, 16); var layer = d.AddLayer("Paint");
            layer.GetChannel(PaintChannel.Color).SetPixel(1, 2, new Rgba32(20, 30, 40, 255)); return d;
        }
        RecoveryWriter.Request Capture(PaintDocument d, ProjectResources resources = null) => new RecoveryWriter.Request
        {
            Sets = new[] { new RecoveryWriter.Set { Id = d.Id, Document = d.CaptureSnapshot() } },
            Project = new YlpProjectInfo(new[] { new YlpTextureSetInfo(d.Id, "Set", 0) }, d.Id),
            Resources = ResourceIndex.Capture(resources ?? new ProjectResources()), Info = System.Text.Encoding.UTF8.GetBytes("{}"),
            Writer = new YlpWriterInfo("Test", "1", "2022.3"), Keep = 3, StorageRoot = root
        };
        [Test] public void PaintingDuringBackgroundWriteKeepsTheCapturedNativeAndResources()
        {
            var d = Document(); var resources = new ProjectResources();
            var image = resources.Add("Image", ImageContent.FromPixels(new byte[] { 1, 2, 3, 255 }, 1, 1), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            byte[] expected = DocumentBinary.Write(d); var request = Capture(d, resources);
            using (var started = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
            {
                var writer = new RecoveryWriter(root, null) { FaultInjection = stage => { if (stage == "snapshot") { started.Set(); if (!release.Wait(5000)) throw new TimeoutException(); } } };
                try
                {
                    writer.Submit(request); Assert.That(started.Wait(5000), Is.True);
                    d.Layers[0].GetChannel(PaintChannel.Color).SetPixel(1, 2, new Rgba32(90, 80, 70, 255));
                    resources.Rename(image.Id, "Changed"); resources.ReplaceContent(image.Id, ImageContent.FromPixels(new byte[] { 90, 80, 70, 255 }, 1, 1), ResourceOrigin.None);
                    release.Set(); writer.Wait(); Assert.That(writer.TakeResult().Error, Is.Null);
                    var saved = YlpFormat.Open(GenerationStore.Load(root).Files);
                    Assert.That(saved.SetFiles(d.Id)[YlpArchive.NativeName], Is.EqualTo(expected));
                    var restored = ResourceIndex.Load(saved.Files, saved.Resources).Images.Single();
                    Assert.That(restored.Name, Is.EqualTo("Image")); Assert.That(restored.Content.CopyPixels(), Is.EqualTo(new byte[] { 1, 2, 3, 255 }));
                }
                finally { release.Set(); writer.Wait(); }
            }
        }
        [Test] public void OnlyOneWriterRunsAndOnlyTheLatestPendingSnapshotIsWritten()
        {
            var d = Document(); int writes = 0, active = 0, maximum = 0;
            using (var started = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
            {
                var writer = new RecoveryWriter(root, null) { FaultInjection = stage =>
                {
                    if (stage == "snapshot") { maximum = Math.Max(maximum, Interlocked.Increment(ref active)); if (Interlocked.Increment(ref writes) == 1) { started.Set(); if (!release.Wait(5000)) throw new TimeoutException(); } }
                    if (stage == "after-pointer") Interlocked.Decrement(ref active);
                } };
                try
                {
                    writer.Submit(Capture(d)); Assert.That(started.Wait(5000), Is.True);
                    d.SetLayerName(d.Layers[0].Id, "Middle"); writer.Submit(Capture(d));
                    d.SetLayerName(d.Layers[0].Id, "Latest"); writer.Submit(Capture(d));
                    byte[] expected = DocumentBinary.Write(d); release.Set(); writer.Wait();
                    Assert.That(writes, Is.EqualTo(2)); Assert.That(maximum, Is.EqualTo(1));
                    Assert.That(Directory.GetDirectories(Path.Combine(root, "generations")).Length, Is.EqualTo(2));
                    Assert.That(GenerationStore.Load(root).Files[YlpFormat.SetEntry(d.Id, YlpArchive.NativeName)], Is.EqualTo(expected));
                    Assert.That(writer.TakeResult().Error, Is.Null); Assert.That(writer.TakeResult().Error, Is.Null);
                    Assert.That(writer.TakeResult(), Is.Null);
                }
                finally { release.Set(); writer.Wait(); }
            }
        }
        [TestCase("snapshot")] [TestCase("before-pointer")] [TestCase("after-pointer")]
        public void BackgroundFailureIsReportedAndTheNextRequestCanRetry(string failure)
        {
            var d = Document(); var writer = new RecoveryWriter(root, null);
            writer.Submit(Capture(d)); writer.Wait(); Assert.That(writer.TakeResult().Error, Is.Null);
            var previous = GenerationStore.Load(root);
            int failed = 0; writer.FaultInjection = stage => { if (stage == failure && Interlocked.Increment(ref failed) == 1) throw new IOException("Injected write failure"); };
            d.SetLayerName(d.Layers[0].Id, "Changed"); writer.Submit(Capture(d)); writer.Wait();
            Assert.That(writer.TakeResult().Error, Is.TypeOf<IOException>());
            if (failure != "after-pointer") Assert.That(GenerationStore.Load(root).Token, Is.EqualTo(previous.Token));
            writer.Submit(Capture(d)); writer.Wait(); Assert.That(writer.TakeResult().Error, Is.Null);
            Assert.That(GenerationStore.Load(root).Files[YlpFormat.SetEntry(d.Id, YlpArchive.NativeName)], Is.EqualTo(DocumentBinary.Write(d)));
        }
        [Test] public void ExternalCurrentChangesAreNotAdoptedForAnOverwriteRetry()
        {
            var d = Document(); var writer = new RecoveryWriter(root, null);
            writer.Submit(Capture(d)); writer.Wait(); Assert.That(writer.TakeResult().Error, Is.Null);
            var external = GenerationStore.Load(root); external = GenerationStore.Commit(root, external.Files, external.Token, shareContents: true);
            d.SetLayerName(d.Layers[0].Id, "Changed");
            for (int i = 0; i < 2; i++)
            { writer.Submit(Capture(d)); writer.Wait(); Assert.That(writer.TakeResult().Error, Is.Not.Null); Assert.That(GenerationStore.Load(root).Token, Is.EqualTo(external.Token)); }
        }
        [Test] public void SmartSnapshotKeepsOwnedBytesAndMetadataAfterItsSourceIsChanged()
        {
            var d = Document(); var material = d.CaptureSmartMaterial(d.Layers.Select(l => l.Id), "Smart");
            var original = SmartMaterialFile.Write(material, new YlpWriterInfo("Test", "1", "2022.3"));
            var expected = (byte[])original.Clone(); var resources = new ProjectResources();
            var smart = resources.AddSmart("Smart", original, material, ResourceOrigin.None, out _);
            var snapshot = ResourceIndex.Capture(resources);
            original[0] ^= 127; resources.Rename(smart.Id, "Changed"); resources.Remove(smart.Id);
            var files = new System.Collections.Generic.Dictionary<string, byte[]>(); ResourceIndex.AddTo(files, snapshot);
            Assert.That(files[ResourceIndex.SmartEntry(smart.Hash)], Is.EqualTo(expected));
            Assert.That(ResourceIndex.Read(files[ResourceIndex.EntryName]).Single().Name, Is.EqualTo("Smart"));
        }

    }
}
