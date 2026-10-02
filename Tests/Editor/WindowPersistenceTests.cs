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
            public string LastFolder, LastName;
            public string SaveFile(string title, string folder, string defaultName, string extension) { Asked.Add("SaveFile"); LastFolder = folder; LastName = defaultName; return File; }
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

        /// <summary>保存先の .ylp のパス（一時フォルダの中。フォルダごと後片付けする）。</summary>
        string NewYlpPath(string name = "Art.ylp")
        { string folder = NewTempPath(); Directory.CreateDirectory(folder); return Path.Combine(folder, name); }

        [Test] public void SaveAsWritesAVerifiedYlpWithComposites()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            PaintDot(window, 200, 300);
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            Assert.That(window.ProjectPath, Is.EqualTo(fake.File));
            var snapshot = YlpStore.Load(fake.File);
            CollectionAssert.AreEquivalent(new[] { "document.utpaint", "view.json", "brush.json", "composite/Color.png", "thumbnail.png" }, snapshot.Files.Keys);
            Assert.That(snapshot.Files["document.utpaint"], Is.EqualTo(DocumentBinary.Write(window.Document)));
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                Assert.That(texture.LoadImage(snapshot.Files["composite/Color.png"]), Is.True);
                Assert.That(texture.GetPixels32().SelectMany(c => new[] { c.r, c.g, c.b, c.a }).ToArray(), Is.EqualTo(window.Document.Composite(PaintChannel.Color)), "the composite is the exact straight RGBA8 result");
                Assert.That(texture.LoadImage(snapshot.Files["thumbnail.png"]), Is.True); Assert.That(Math.Max(texture.width, texture.height), Is.LessThanOrEqualTo(256));
            }
            finally { Object.DestroyImmediate(texture); }
            Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(fake.File)).Select(Path.GetFileName), Is.EqualTo(new[] { "Art.ylp" }), "no temporary, lock or backup files for a new file");
        }

        [Test] public void SaveAsAddsTheExtensionWhenItIsMissing()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath("NoExtension");
            window.SaveProject(true);
            Assert.That(window.ProjectPath, Is.EqualTo(fake.File + ".ylp"), window.StatusMessage);
            Assert.That(System.IO.File.Exists(fake.File + ".ylp"), Is.True);
        }

        [Test] public void CtrlSSavesAgainAndKeepsThePreviousVersionAsABackup()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            PaintDot(window, 100, 100);
            Key(window, KeyCode.S, EventModifiers.Control); // 保存先が無いので Save As と同じく尋ねる
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            byte[] first = DocumentBinary.Write(window.Document);
            PaintDot(window, 600, 600);
            Assert.That(window.IsSaved, Is.False);
            fake.Asked.Clear();
            Key(window, KeyCode.S, EventModifiers.Control);
            Assert.That(fake.Asked, Does.Not.Contain("SaveFile"), "a document that has a file saves without asking again");
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            Assert.That(window.StatusMessage, Does.Contain("previous version is kept"));
            var backups = YlpStore.Backups(fake.File);
            Assert.That(backups.Count, Is.EqualTo(1));
            Assert.That(YlpStore.Load(backups[0]).Files["document.utpaint"], Is.EqualTo(first), "the backup is the previous version");
            Assert.That(YlpStore.Load(fake.File).Files["document.utpaint"], Is.EqualTo(DocumentBinary.Write(window.Document)));
        }

        [Test] public void OpenRestoresTheSavedDocumentInAnotherWindow()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            PaintDot(window, 321, 654); window.Document.SetLayerOpacity(window.Document.Layers[0].Id, .4);
            window.SaveProject(true);
            byte[] saved = DocumentBinary.Write(window.Document);
            var other = Open();
            try
            {
                var otherFake = UseFakeDialogs(other); otherFake.File = fake.File;
                other.OpenProject();
                Assert.That(DocumentBinary.Write(other.Document), Is.EqualTo(saved), other.StatusMessage);
                Assert.That(other.IsSaved, Is.True);
                Assert.That(other.ProjectPath, Is.EqualTo(fake.File));
                Assert.That(other.StatusMessage, Does.StartWith("Opened Art.ylp"));
                var before = other.Document; other.OpenProjectAt(fake.File);
                Assert.That(other.Document, Is.SameAs(before), "opening the file that is already open and unchanged does nothing");
            }
            finally { Close(other); }
        }

        [Test] public void OpenFileInWindowLoadsTheFile()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            PaintDot(window, 500, 500); window.SaveProject(true);
            byte[] saved = DocumentBinary.Write(window.Document);
            CreateDocumentForTest(window);
            var opened = TexturePaintWindow.OpenFileInWindow(fake.File);
            Assert.That(DocumentBinary.Write(opened.Document), Is.EqualTo(saved), opened.StatusMessage);
            if (opened != window) Close(opened);
        }

        [Test] public void ExternalChangeBlocksNormalSaveButSaveAsStillWorks()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            PaintDot(window, 50, 50); window.SaveProject(true);
            var other = new PaintDocument(16, 16, 16); other.AddLayer("x");
            byte[] outside = YlpArchive.Write(new Dictionary<string, byte[]> { { "document.utpaint", DocumentBinary.Write(other) } });
            System.IO.File.WriteAllBytes(fake.File, outside); // 外のアプリやバージョン管理が書き換えた
            window.CheckExternalChange();
            Assert.That(window.HasExternalConflict, Is.True);
            PaintDot(window, 900, 900);
            window.SaveProject(false);
            Assert.That(window.IsSaved, Is.False, "normal save must refuse to replace an externally changed file");
            Assert.That(System.IO.File.ReadAllBytes(fake.File), Is.EqualTo(outside));
            Assert.That(window.StatusMessage, Does.Contain("outside"));
            fake.File = NewYlpPath("Copy.ylp");
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            Assert.That(YlpStore.Load(fake.File).Files["document.utpaint"], Is.EqualTo(DocumentBinary.Write(window.Document)));
        }

        [Test] public void SavingOverAnotherFileKeepsItAsABackupOrAsksWhenBackupsAreOff()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath("Other.ylp");
            window.SaveProject(true);
            byte[] otherBytes = System.IO.File.ReadAllBytes(fake.File);
            CreateDocumentForTest(window); PaintDot(window, 10, 10);
            window.SaveProject(true); // 同じ名前を選んだ（OS のダイアログが置き換えを確認済み）
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            Assert.That(System.IO.File.ReadAllBytes(YlpStore.Backups(fake.File).Single()), Is.EqualTo(otherBytes), "the replaced file is kept");

            var personal = PainterSettings.PersonalSettings; personal.backupsToKeep = 0; PainterSettings.Save(null, personal);
            byte[] current = System.IO.File.ReadAllBytes(fake.File);
            CreateDocumentForTest(window); PaintDot(window, 20, 20);
            fake.ConfirmAnswer = false;
            window.SaveProject(true);
            Assert.That(fake.Asked, Does.Contain("Confirm: Replace file?"));
            Assert.That(System.IO.File.ReadAllBytes(fake.File), Is.EqualTo(current), "declining leaves the file alone");
            Assert.That(window.IsSaved, Is.False);
        }

        [Test] public void ExportPsdWritesTheChannelOrExplainsWhyItCannot()
        {
            var fake = UseFakeDialogs(window); fake.File = NewTempPath(".psd");
            PaintDot(window, 70, 70);
            window.ExportPsd();
            Assert.That(System.IO.File.Exists(fake.File), Is.True, window.StatusMessage);
            var psd = PsdCodec.Read(System.IO.File.ReadAllBytes(fake.File));
            Assert.That(psd.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster));
            Assert.That(PsdBridge.Import(psd).Composite(PaintChannel.Color), Is.EqualTo(window.Document.Composite(PaintChannel.Color)));

            // 半透明の Fill レイヤーは PSD では表せない。平らにせず、保存先も尋ねずに理由を示す（不透明な Fill や描画モードなどの対応範囲は PSD のテストが受け持つ）
            window.Document.AddFillLayer("Fill", new System.Collections.Generic.Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(1, 2, 3, 128) } });
            fake.File = NewTempPath(".psd"); fake.Asked.Clear();
            window.ExportPsd();
            Assert.That(fake.Asked, Is.EqualTo(new[] { "Inform: PSD export unavailable" }), "a translucent fill layer is not flattened, and no file is asked for");
            Assert.That(System.IO.File.Exists(fake.File), Is.False);
            // .ylp はそのまま保存でき、開き直しても同じ
            fake.File = NewYlpPath();
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            Assert.That(YlpStore.Load(fake.File).Files["document.utpaint"], Is.EqualTo(DocumentBinary.Write(window.Document)));
        }

        [Test] public void OpeningAnotherProjectAsksAboutUnsavedWorkAndKeepsARecoveryCheckpoint()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            window.SaveProject(true); // 空のファイルを 1 つ作っておく
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
            fake.File = NewYlpPath();
            window.SaveProject(true);
            Assert.That(YlpStore.Load(fake.File).Files["imported-original.psd"], Is.EqualTo(psdBytes));
            Assert.That(System.IO.File.ReadAllBytes(psdPath), Is.EqualTo(psdBytes), "the imported file itself is never rewritten");
        }

        static void CreateDocumentForTest(TexturePaintWindow w) { Invoke(w, "CreateDocument", 256); Invoke(w, "BindDocument"); Repaint(w); }

        [Test] public void AnImportedPsdIsSavedAsAYlpNextToItAndExportedBackOnlyWhenConfirmed()
        {
            var source = new PaintDocument(32, 32, 16); source.AddLayer("Imported").GetChannel(PaintChannel.Color).SetPixel(3, 4, new Rgba32(40, 50, 60, 255));
            string folder = NewTempPath(); Directory.CreateDirectory(folder);
            string psdPath = Path.Combine(folder, "Chara.psd"); byte[] psdBytes = PsdCodec.Write(PsdBridge.Export(source, PaintChannel.Color));
            System.IO.File.WriteAllBytes(psdPath, psdBytes);
            var fake = UseFakeDialogs(window); fake.File = psdPath;
            window.ImportPsd();
            Assert.That(window.Document.Composite(PaintChannel.Color), Is.EqualTo(source.Composite(PaintChannel.Color)), window.StatusMessage);
            fake.File = ""; // 保存先を尋ねられたところで取り消す
            window.SaveProject(true);
            Assert.That(fake.LastFolder, Is.EqualTo(folder), "the .ylp is suggested next to the PSD");
            Assert.That(fake.LastName, Is.EqualTo("Chara"));
            fake.File = Path.Combine(folder, "Chara.ylp");
            window.SaveProject(true);
            Assert.That(YlpStore.Load(fake.File).Files["imported-original.psd"], Is.EqualTo(psdBytes));
            // PSD として書き出す。取り込み元を上書きしそうなら確かめる
            fake.File = psdPath; fake.ConfirmAnswer = false; fake.Asked.Clear();
            window.ExportPsd();
            Assert.That(fake.Asked, Does.Contain("Confirm: Overwrite the imported PSD?"));
            Assert.That(fake.LastName, Is.EqualTo("Chara"), "the PSD is named after the document");
            Assert.That(System.IO.File.ReadAllBytes(psdPath), Is.EqualTo(psdBytes), "declining leaves the PSD alone");
            fake.File = Path.Combine(folder, "Chara_edit.psd");
            window.ExportPsd();
            Assert.That(PsdCodec.Read(System.IO.File.ReadAllBytes(fake.File)).Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), window.StatusMessage);
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
