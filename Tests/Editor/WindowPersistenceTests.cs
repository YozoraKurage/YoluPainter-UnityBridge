using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウの保存・読み込み・取り込み・書き出しを、ダイアログを差し替えて実際の経路で通す。</summary>
    public sealed partial class WindowTests
    {
        sealed class FakeDialogs : IPainterDialogs
        {
            public string Folder = "", File = "";
            public bool ConfirmAnswer = true;
            public readonly List<string> Asked = new List<string>();
            public string SaveFolder(string title, string folder, string defaultName) { Asked.Add("SaveFolder"); return Folder; }
            public string OpenFolder(string title, string folder) { Asked.Add("OpenFolder"); return Folder; }
            public string OpenFile(string title, string folder, string extension) { Asked.Add("OpenFile"); return File; }
            public string SaveFile(string title, string folder, string defaultName, string extension) { Asked.Add("SaveFile"); return File; }
            public bool Confirm(string title, string message, string ok, string cancel) { Asked.Add("Confirm: " + title); return ConfirmAnswer; }
            public void Inform(string title, string message) { Asked.Add("Inform: " + title); }
            public void Progress(string title, string info, float progress) { }
            public void ClearProgress() { }
        }

        readonly List<string> temporaryPaths = new List<string>();

        string NewTempPath(string extension = "")
        {
            string path = Path.Combine(Path.GetTempPath(), "yolupainter-window-" + Guid.NewGuid().ToString("N") + extension);
            temporaryPaths.Add(path); return path;
        }

        [TearDown] public void DeleteTemporaryPaths()
        {
            foreach (var path in temporaryPaths)
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            temporaryPaths.Clear();
        }

        FakeDialogs UseFakeDialogs(TexturePaintWindow w)
        { var fake = new FakeDialogs(); w.Dialogs = fake; return fake; }

        void PaintDot(TexturePaintWindow w, int x, int y)
        { var at = At(w, x, y); Mouse(w, EventType.MouseDown, at); Mouse(w, EventType.MouseUp, at); Assert.That(w.Document.CanUndo, Is.True); }

        static int GenerationCount(string root) => Directory.GetDirectories(Path.Combine(root, "generations")).Length;

        [Test] public void SaveAsWritesAVerifiedGenerationWithChannelPsd()
        {
            var fake = UseFakeDialogs(window); fake.Folder = NewTempPath();
            PaintDot(window, 200, 300);
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            Assert.That(window.ProjectRoot, Is.EqualTo(fake.Folder));
            var snapshot = GenerationStore.Load(fake.Folder);
            CollectionAssert.AreEquivalent(new[] { "document.utpaint", "view.json", "brush.json", "save-status.txt", "Color.psd" }, snapshot.Files.Keys);
            Assert.That(snapshot.Files["document.utpaint"], Is.EqualTo(DocumentBinary.Write(window.Document)));
            Assert.That(System.Text.Encoding.UTF8.GetString(snapshot.Files["save-status.txt"]), Is.EqualTo("PSD channels saved"));
            var psd = PsdCodec.Read(snapshot.Files["Color.psd"]);
            Assert.That(psd.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster));
            Assert.That(PsdBridge.Import(psd).Composite(PaintChannel.Color), Is.EqualTo(window.Document.Composite(PaintChannel.Color)));
        }

        [Test] public void CtrlSSavesANewGenerationAndKeepsTheOldOne()
        {
            var fake = UseFakeDialogs(window); fake.Folder = NewTempPath();
            PaintDot(window, 100, 100);
            Key(window, KeyCode.S, EventModifiers.Control); // 保存先が無いので Save As と同じくフォルダを尋ねる
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            string first = GenerationStore.Load(fake.Folder).Generation;
            PaintDot(window, 600, 600);
            Assert.That(window.IsSaved, Is.False);
            fake.Asked.Clear();
            Key(window, KeyCode.S, EventModifiers.Control);
            Assert.That(fake.Asked, Does.Not.Contain("SaveFolder"), "a project that has a folder saves without asking again");
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            Assert.That(GenerationCount(fake.Folder), Is.EqualTo(2));
            Assert.That(GenerationStore.Load(fake.Folder).Generation, Is.Not.EqualTo(first));
            Assert.That(Directory.Exists(Path.Combine(fake.Folder, "generations", first)), Is.True, "old generations are kept");
        }

        [Test] public void OpenRestoresTheSavedDocumentInAnotherWindow()
        {
            var fake = UseFakeDialogs(window); fake.Folder = NewTempPath();
            PaintDot(window, 321, 654); window.Document.SetLayerOpacity(window.Document.Layers[0].Id, .4);
            window.SaveProject(true);
            byte[] saved = DocumentBinary.Write(window.Document);
            var other = Open();
            try
            {
                var otherFake = UseFakeDialogs(other); otherFake.Folder = fake.Folder;
                other.OpenProject();
                Assert.That(DocumentBinary.Write(other.Document), Is.EqualTo(saved), other.StatusMessage);
                Assert.That(other.IsSaved, Is.True);
                Assert.That(other.ProjectRoot, Is.EqualTo(fake.Folder));
            }
            finally { Close(other); }
        }

        [Test] public void ExternalChangeBlocksNormalSaveButSaveAsStillWorks()
        {
            var fake = UseFakeDialogs(window); fake.Folder = NewTempPath();
            PaintDot(window, 50, 50); window.SaveProject(true);
            string generation = GenerationStore.Load(fake.Folder).Generation;
            System.IO.File.AppendAllText(Path.Combine(fake.Folder, "generations", generation, "Color.psd"), "edited elsewhere");
            window.CheckExternalChange();
            Assert.That(window.HasExternalConflict, Is.True);
            PaintDot(window, 900, 900);
            window.SaveProject(false);
            Assert.That(window.IsSaved, Is.False, "normal save must refuse to replace an externally changed project");
            Assert.That(GenerationCount(fake.Folder), Is.EqualTo(1));
            Assert.That(window.StatusMessage, Does.Contain("outside"));
            fake.Folder = NewTempPath();
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            Assert.That(GenerationStore.Load(fake.Folder).Files["document.utpaint"], Is.EqualTo(DocumentBinary.Write(window.Document)));
        }

        [Test] public void NonNormalBlendAsksBeforeSavingNativeOnly()
        {
            var fake = UseFakeDialogs(window); fake.Folder = NewTempPath();
            PaintDot(window, 70, 70);
            window.Document.SetLayerBlendMode(window.Document.Layers[0].Id, LayerBlendMode.Multiply);
            fake.ConfirmAnswer = false;
            window.SaveProject(true);
            Assert.That(fake.Asked, Does.Contain("Confirm: PSD projection unavailable"));
            Assert.That(System.IO.File.Exists(Path.Combine(fake.Folder, "current")), Is.False, "declining must not save anything");
            Assert.That(window.IsSaved, Is.False);
            fake.ConfirmAnswer = true;
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var files = GenerationStore.Load(fake.Folder).Files;
            Assert.That(files.ContainsKey("Color.psd"), Is.False, "a Multiply layer must not be flattened into the PSD");
            Assert.That(System.Text.Encoding.UTF8.GetString(files["save-status.txt"]), Does.StartWith("Native project saved; PSD NOT updated"));
        }

        [Test] public void OpeningAnotherProjectAsksAboutUnsavedWorkAndKeepsARecoveryCheckpoint()
        {
            var fake = UseFakeDialogs(window); fake.Folder = NewTempPath();
            window.SaveProject(true); // 空のプロジェクトを 1 つ作っておく
            PaintDot(window, 400, 400);
            var unsaved = window.Document;
            fake.ConfirmAnswer = false;
            window.OpenProject();
            Assert.That(window.Document, Is.SameAs(unsaved), "declining keeps the current work open");
            fake.ConfirmAnswer = true;
            window.OpenProject();
            Assert.That(window.Document, Is.Not.SameAs(unsaved));
            var recovery = GenerationStore.Load(window.RecoveryRoot);
            Assert.That(recovery.Files["document.utpaint"], Is.EqualTo(DocumentBinary.Write(unsaved)), "the discarded work is kept as a recovery checkpoint");
        }

        [Test] public void ImportingAnEditablePsdKeepsItsOriginalBytesForSaving()
        {
            var source = new PaintDocument(64, 48, 16); var layer = source.AddLayer("Imported");
            layer.GetChannel(PaintChannel.Color).SetPixel(5, 6, new Rgba32(10, 20, 30, 200));
            string psdPath = NewTempPath(".psd"); byte[] psdBytes = PsdCodec.Write(PsdBridge.Export(source, PaintChannel.Color));
            System.IO.File.WriteAllBytes(psdPath, psdBytes);
            var fake = UseFakeDialogs(window); fake.File = psdPath;
            window.ImportPsd();
            Assert.That(window.Document.Composite(PaintChannel.Color), Is.EqualTo(source.Composite(PaintChannel.Color)), window.StatusMessage);
            fake.Folder = NewTempPath();
            window.SaveProject(true);
            Assert.That(GenerationStore.Load(fake.Folder).Files["imported-original.psd"], Is.EqualTo(psdBytes));
            Assert.That(System.IO.File.ReadAllBytes(psdPath), Is.EqualTo(psdBytes), "the imported file itself is never rewritten");
        }

        [Test] public void ImportingAProtectedPsdExplainsAndChangesNothing()
        {
            var source = new PaintDocument(32, 32, 16); source.AddLayer("A").GetChannel(PaintChannel.Color).SetPixel(1, 1, new Rgba32(9, 9, 9));
            byte[] bytes = PsdCodec.Write(PsdBridge.Export(source, PaintChannel.Color));
            int blend = IndexOf(bytes, "norm"); Assert.That(blend, Is.GreaterThan(0)); bytes[blend] = (byte)'m'; // 未対応の合成モードにする
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
            string psdPath = NewTempPath(".psd"); System.IO.File.WriteAllBytes(psdPath, bytes);
            var before = window.Document;
            var fake = UseFakeDialogs(window); fake.File = psdPath;
            window.ImportPsd();
            Assert.That(fake.Asked, Does.Contain("Inform: PSD protected: PreserveOnly"));
            Assert.That(window.Document, Is.SameAs(before));
            Assert.That(System.IO.File.ReadAllBytes(psdPath), Is.EqualTo(bytes));
        }

        [Test] public void ExportPngWritesTheSelectedChannelComposite()
        {
            PaintDot(window, 10, 1000);
            var fake = UseFakeDialogs(window); fake.File = NewTempPath(".png");
            window.ExportPng();
            Assert.That(System.IO.File.Exists(fake.File), Is.True, window.StatusMessage);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                Assert.That(texture.LoadImage(System.IO.File.ReadAllBytes(fake.File)), Is.True);
                Assert.That(texture.width, Is.EqualTo(window.Document.Width));
                // LoadImage は PNG を ARGB32 で読むので、生バイトではなく RGBA の Color32 で比べる。
                var rgba = texture.GetPixels32().SelectMany(c => new[] { c.r, c.g, c.b, c.a }).ToArray();
                Assert.That(rgba, Is.EqualTo(window.Document.Composite(PaintChannel.Color)), "straight alpha: semi-transparent RGB must not be premultiplied");
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test] public void FocusLossWritesARecoveryCheckpoint()
        {
            PaintDot(window, 777, 333);
            Invoke(window, "OnLostFocus");
            var recovery = GenerationStore.Load(window.RecoveryRoot);
            Assert.That(recovery.Files["document.utpaint"], Is.EqualTo(DocumentBinary.Write(window.Document)));
        }

        static int IndexOf(byte[] data, string key)
        {
            var k = System.Text.Encoding.ASCII.GetBytes(key);
            for (int i = 0; i + k.Length <= data.Length; i++) if (data.Skip(i).Take(k.Length).SequenceEqual(k)) return i;
            return -1;
        }
    }
}
