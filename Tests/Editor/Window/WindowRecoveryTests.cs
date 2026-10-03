using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed partial class WindowTests
    {
        [Test] public void APristineWindowDeletesItsRecoveryOnClose()
        {
            Invoke(window, "OnLostFocus"); string root = window.RecoveryRoot;
            Assert.That(Directory.Exists(root), Is.True);
            window.Close(); window = null;
            Assert.That(Directory.Exists(root), Is.False);
        }
        [Test] public void ASavedWindowDeletesItsRecoveryAndKeepsTheYlpOnClose()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath(); PaintDot(window, 200, 200);
            window.SaveProject(true); Invoke(window, "OnLostFocus"); string root = window.RecoveryRoot;
            Assert.That(Directory.Exists(root), Is.True); Assert.That(window.IsSaved, Is.True);
            window.Close(); window = null;
            Assert.That(Directory.Exists(root), Is.False); Assert.That(File.Exists(fake.File), Is.True);
        }
        [TestCase(false)] [TestCase(true)] public void ASavedWindowKeepsRecoveryIfItsYlpWasRemovedOrChanged(bool changed)
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath(); PaintDot(window, 200, 200); window.SaveProject(true);
            Invoke(window, "SaveRecovery"); string root = window.RecoveryRoot;
            if (changed) File.AppendAllText(fake.File,"changed"); else File.Delete(fake.File);
            window.Close(); window = null;
            Assert.That(Directory.Exists(root), Is.True, "a missing or externally changed .ylp cannot replace the local checkpoint");
            Assert.That(RecoveryCatalog.List().Any(e => e.Root == root), Is.True); Directory.Delete(root,true);
        }
        [Test] public void AnUnsavedClosedWindowIsListedAndCanBeRecovered()
        {
            PaintDot(window, 200, 200); byte[] expected = DocumentBinary.Write(window.Document); string root = window.RecoveryRoot;
            window.Close(); window = null;
            var entry = RecoveryCatalog.List().Single(e => e.Root == root);
            Assert.That(entry.Bytes, Is.GreaterThan(0)); Assert.That(entry.UpdatedUtc, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1))); Assert.That(entry.Title, Is.Not.Empty);
            window = Open(); Assert.That(window.OpenRecoveryAt(entry.Root), Is.True, window.StatusMessage);
            Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(expected)); Assert.That(window.IsSaved, Is.False);
            Assert.That(window.Document.CanUndo, Is.False, "recovery returns source, not transient history");
            Assert.That(RecoveryCatalog.List().Any(e => e.Root == root), Is.False, "an open window owns this checkpoint");
        }
        [Test] public void DiscardingAClosedCheckpointAsksAndKeepsItsSavedFile()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath(); window.SaveProject(true); PaintDot(window, 200, 200);
            string root = window.RecoveryRoot; window.Close(); window = null;
            var entry = RecoveryCatalog.List().Single(e => e.Root == root);
            fake.ConfirmAnswer = false; Assert.That(RecoveryCatalog.Discard(entry, fake), Is.False); Assert.That(Directory.Exists(root), Is.True);
            fake.ConfirmAnswer = true; Assert.That(RecoveryCatalog.Discard(entry, fake), Is.True);
            Assert.That(Directory.Exists(root), Is.False); Assert.That(File.Exists(fake.File), Is.True);
        }
        [Test] public void AnOpenCheckpointCannotBeDiscardedOrTakenByAnotherWindow()
        {
            PaintDot(window, 200, 200); Invoke(window, "SaveRecovery"); string root = window.RecoveryRoot;
            var other = Open();
            try
            {
                Assert.That(other.OpenRecoveryAt(root), Is.False);
                Assert.That(() => RecoveryCatalog.Discard(new RecoveryCatalog.Entry { Root = root }, UseFakeDialogs(other)), Throws.TypeOf<IOException>());
                Assert.That(Directory.Exists(root), Is.True);
            }
            finally { Close(other); }
        }
        [Test] public void DecliningToReplaceUnsavedWorkKeepsBothProjects()
        {
            var abandoned = Open(); string root = abandoned.RecoveryRoot;
            try { PaintDot(abandoned, 200, 200); abandoned.Close(); abandoned = null;
                PaintDot(window, 400, 400); var original = window.Document;
                UseFakeDialogs(window).ConfirmAnswer = false;
                Assert.That(window.OpenRecoveryAt(root), Is.False); Assert.That(window.Document, Is.SameAs(original)); Assert.That(Directory.Exists(root), Is.True);
            }
            finally { Close(abandoned); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        [Test] public void CorruptRecoveryDoesNotReplaceTheOpenDocument()
        {
            var abandoned = Open(); string root = abandoned.RecoveryRoot;
            try
            {
                PaintDot(abandoned, 200, 200); abandoned.Close(); abandoned = null;
                var snapshot = GenerationStore.Load(root);
                byte[] native = snapshot.Files.First(e => e.Key.EndsWith("/document.utpaint", StringComparison.Ordinal)).Value;
                File.WriteAllBytes(Path.Combine(root, "contents", GenerationStore.Hash(native) + ".bin"), new byte[] { 1 });
                var original = window.Document;
                Assert.That(window.OpenRecoveryAt(root), Is.False); Assert.That(window.Document, Is.SameAs(original)); Assert.That(Directory.Exists(root), Is.True);
            }
            finally { Close(abandoned); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        [TestCase(false)] [TestCase(true)] public void ReloadRestoresSavedAndPristineBaselinesForCleanup(bool saved)
        {
            if (saved) { PaintDot(window, 200, 200); var fake = UseFakeDialogs(window); fake.File = NewYlpPath(); window.SaveProject(true); }
            Invoke(window, "BeforeReload"); string root = window.RecoveryRoot;
            Invoke(window, "OnDisable"); Invoke(window, "OnEnable");
            Assert.That(window.RecoveryRoot, Is.EqualTo(root)); Assert.That(Directory.Exists(root), Is.True);
            Assert.That(window.IsSaved, Is.EqualTo(saved));
            window.Close(); window = null; Assert.That(Directory.Exists(root), Is.False);
        }
        [TestCase("OnLostFocus")] [TestCase("BeforeReload")] [TestCase("PlayModeChanged")]
        public void LifecycleCheckpointsRemainRecoverable(string notification)
        {
            PaintDot(window, 200, 200);
            if (notification == "PlayModeChanged") Invoke(window, notification, UnityEditor.PlayModeStateChange.ExitingEditMode);
            else Invoke(window, notification);
            string root = window.RecoveryRoot; byte[] expected = DocumentBinary.Write(window.Document);
            Invoke(window, "OnDisable"); Invoke(window, "OnEnable");
            Assert.That(window.RecoveryRoot, Is.EqualTo(root)); Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(expected));
            Assert.That(window.IsSaved, Is.False);
        }
        [Test] public void OlderFlatRecoveryCanBeOpenedFromTheList()
        {
            var d = new PaintDocument(32, 32, 16); d.AddLayer("Legacy");
            string root = RecoveryCatalog.NewRoot();
            try
            {
                GenerationStore.Commit(root, new System.Collections.Generic.Dictionary<string, byte[]> { ["document.utpaint"] = DocumentBinary.Write(d) });
                Assert.That(RecoveryCatalog.List().Single(e => e.Root == root).Problem, Is.Null);
                Assert.That(window.OpenRecoveryAt(root), Is.True, window.StatusMessage);
                Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(DocumentBinary.Write(d))); Assert.That(window.IsSaved, Is.False);
            }
            finally { Close(window); window = null; if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        [Test] public void StorageWarningDoesNotDeleteClosedCheckpoints()
        {
            PainterSettings.UpdatePersonal(p => p.recoveryWarningMiB = 1);
            string root = RecoveryCatalog.NewRoot(); Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "interrupted.bin"), new byte[2 << 20]);
            try
            {
                Invoke(window, "CheckRecoveryStorage"); Assert.That(window.StatusMessage, Does.Contain("above the 1 MiB"));
                Assert.That(File.Exists(Path.Combine(root, "interrupted.bin")), Is.True);
                Assert.That(RecoveryCatalog.List().Single(e => e.Root == root).Problem, Is.Not.Null, "interrupted-only work is visible and can be explicitly discarded");
            }
            finally { Directory.Delete(root, true); }
        }
        [Test] public void RecoveryMenuWindowShowsTheClosedCheckpointList()
        {
            window.ShowRecovery(); var browser = Resources.FindObjectsOfTypeAll<RecoveryBrowser>().Single();
            try { browser.SendEvent(new Event { type = EventType.Repaint }); Assert.That(browser.titleContent.text, Is.EqualTo("Recovery checkpoints")); }
            finally { browser.Close(); }
        }
        [Test] public void AStaleDiscardSelectionDoesNotDeleteANewerCheckpoint()
        {
            string root = RecoveryCatalog.NewRoot();
            try
            {
                var files = new System.Collections.Generic.Dictionary<string, byte[]> { ["document.utpaint"] = new byte[] { 1 } };
                var first = GenerationStore.Commit(root, files); var entry = RecoveryCatalog.List().Single(e => e.Root == root);
                GenerationStore.Commit(root, files, first.Token);
                Assert.That(() => RecoveryCatalog.Discard(entry, UseFakeDialogs(window)), Throws.TypeOf<IOException>());
                Assert.That(Directory.Exists(root), Is.True);
            }
            finally { Directory.Delete(root, true); }
        }
        [Test] public void RecoveryUndoAndCancelReuseTheOriginalContent()
        {
            Invoke(window, "SaveRecovery"); string root = window.RecoveryRoot; byte[] before = DocumentBinary.Write(window.Document);
            PaintDot(window, 200, 200); Invoke(window, "SaveRecovery");
            window.Document.Undo(); Invoke(window, "SaveRecovery");
            Assert.That(window.LastRecoveryWrittenBytes, Is.Zero); Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(before));
            BeginLine(300, 300); Key(window, KeyCode.Escape); Invoke(window, "SaveRecovery");
            Assert.That(GenerationStore.Load(root).Files[YlpFormat.SetEntry(window.Document.Id, "document.utpaint")], Is.EqualTo(before));
        }
    }
}
