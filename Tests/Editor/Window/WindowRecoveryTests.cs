using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed partial class WindowTests
    {
        [Test] public void FocusRecoveryReturnsWhileWritingAndKeepsLaterPaintingDirty()
        {
            PaintDot(window, 200, 200); byte[] expected = DocumentBinary.Write(window.Document);
            using (var started = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
            {
                window.RecoveryFaultInjection = stage => { if (stage == "snapshot") { started.Set(); if (!release.Wait(5000)) throw new TimeoutException(); } };
                try
                {
                    Invoke(window, "OnLostFocus"); Assert.That(started.Wait(5000), Is.True);
                    PaintDot(window, 400, 400); release.Set();
                    Assert.That(window.FlushRecovery(), Is.False, "the completed snapshot must not mark later edits recovered");
                    Assert.That(GenerationStore.Load(window.RecoveryRoot).Files[YlpFormat.SetEntry(window.Document.Id, YlpArchive.NativeName)], Is.EqualTo(expected));
                    Invoke(window, "OnLostFocus"); Assert.That(window.FlushRecovery(), Is.True);
                    Assert.That(GenerationStore.Load(window.RecoveryRoot).Files[YlpFormat.SetEntry(window.Document.Id, YlpArchive.NativeName)], Is.EqualTo(DocumentBinary.Write(window.Document)));
                }
                finally { release.Set(); window.RecoveryFaultInjection = null; window.FlushRecovery(); }
            }
        }
        [TestCase("BeforeReload")] [TestCase("PlayModeChanged")] [TestCase("Close")] [TestCase("Replace")]
        public void LifecycleWaitsForRunningRecoveryAndWritesTheLatestSnapshot(string lifecycle)
        {
            PaintDot(window, 200, 200); string root = window.RecoveryRoot; var id = window.Document.Id;
            using (var started = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
            {
                int writes = 0;
                window.RecoveryFaultInjection = stage => { if (stage == "snapshot" && Interlocked.Increment(ref writes) == 1) { started.Set(); if (!release.Wait(5000)) throw new TimeoutException(); } };
                try
                {
                    Invoke(window, "OnLostFocus"); Assert.That(started.Wait(5000), Is.True);
                    PaintDot(window, 400, 400); byte[] expected = DocumentBinary.Write(window.Document);
                    var releasing = Task.Run(() => { Thread.Sleep(100); release.Set(); });
                    if (lifecycle == "Close") { window.Close(); window = null; }
                    else if (lifecycle == "Replace") { UseFakeDialogs(window); window.CreateProject(new NewProjectSettings { Resolution = 512 }); }
                    else if (lifecycle == "PlayModeChanged") Invoke(window, lifecycle, UnityEditor.PlayModeStateChange.ExitingEditMode);
                    else Invoke(window, lifecycle);
                    releasing.GetAwaiter().GetResult();
                    Assert.That(writes, Is.EqualTo(2));
                    Assert.That(GenerationStore.Load(root).Files[YlpFormat.SetEntry(id, YlpArchive.NativeName)], Is.EqualTo(expected));
                    Assert.That(Directory.GetDirectories(root, ".staging-*").Length, Is.Zero);
                }
                finally { release.Set(); if (window != null) { window.RecoveryFaultInjection = null; window.FlushRecovery(); } }
            }
        }
        [Test] public void ABackgroundRecoveryFailureIsVisibleAndRetriesOnNextFocusLoss()
        {
            PaintDot(window, 200, 200); int failures = 0;
            window.RecoveryFaultInjection = stage => { if (stage == "before-pointer" && Interlocked.Increment(ref failures) == 1) throw new IOException("Injected write failure"); };
            try
            {
                Invoke(window, "OnLostFocus"); Assert.That(window.FlushRecovery(), Is.False);
                Assert.That(window.StatusMessage, Does.Contain("Recovery checkpoint failed"));
                Invoke(window, "OnLostFocus"); Assert.That(window.FlushRecovery(), Is.True);
                Assert.That(window.StatusMessage, Does.Contain("working again"));
                Assert.That(GenerationStore.Load(window.RecoveryRoot).Files[YlpFormat.SetEntry(window.Document.Id, YlpArchive.NativeName)], Is.EqualTo(DocumentBinary.Write(window.Document)));
            }
            finally { window.RecoveryFaultInjection = null; window.FlushRecovery(); }
        }
        [TestCase("BeforeReload")] [TestCase("PlayModeChanged")] [TestCase("Close")] [TestCase("Replace")]
        public void LifecycleRetriesAFailingRunningWriteBeforeLeavingTheDocument(string lifecycle)
        {
            PaintDot(window, 200, 200); string root = window.RecoveryRoot; var id = window.Document.Id;
            byte[] expected = DocumentBinary.Write(window.Document); int attempts = 0;
            using (var started = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
            {
                window.RecoveryFaultInjection = stage =>
                {
                    if (stage == "snapshot" && Interlocked.Increment(ref attempts) == 1) { started.Set(); if (!release.Wait(5000)) throw new TimeoutException(); }
                    if (stage == "before-pointer" && attempts == 1) throw new IOException("Injected write failure");
                };
                try
                {
                    Invoke(window, "OnLostFocus"); Assert.That(started.Wait(5000), Is.True);
                    var releasing = Task.Run(() => { Thread.Sleep(100); release.Set(); });
                    if (lifecycle == "Close") { window.Close(); window = null; }
                    else if (lifecycle == "Replace") { UseFakeDialogs(window); window.CreateProject(new NewProjectSettings { Resolution = 512 }); }
                    else if (lifecycle == "PlayModeChanged") Invoke(window, lifecycle, UnityEditor.PlayModeStateChange.ExitingEditMode);
                    else Invoke(window, lifecycle);
                    releasing.GetAwaiter().GetResult(); Assert.That(attempts, Is.EqualTo(2));
                    Assert.That(GenerationStore.Load(root).Files[YlpFormat.SetEntry(id, YlpArchive.NativeName)], Is.EqualTo(expected));
                }
                finally { release.Set(); if (window != null) { window.RecoveryFaultInjection = null; window.FlushRecovery(); } }
            }
        }
        [Test] public void APristineWindowDeletesItsRecoveryOnClose()
        {
            Invoke(window, "OnLostFocus"); window.FlushRecovery(); string root = window.RecoveryRoot;
            Assert.That(Directory.Exists(root), Is.True);
            window.Close(); window = null;
            Assert.That(Directory.Exists(root), Is.False);
        }
        [Test] public void ASavedWindowDeletesItsRecoveryAndKeepsTheYlpOnClose()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath(); PaintDot(window, 200, 200);
            window.SaveProject(true); Invoke(window, "OnLostFocus"); window.FlushRecovery(); string root = window.RecoveryRoot;
            Assert.That(Directory.Exists(root), Is.True); Assert.That(window.IsSaved, Is.True);
            window.Close(); window = null;
            Assert.That(Directory.Exists(root), Is.False); Assert.That(File.Exists(fake.File), Is.True);
        }
        [TestCase(false)] [TestCase(true)] public void ASavedWindowKeepsRecoveryIfItsYlpWasRemovedOrChanged(bool changed)
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath(); PaintDot(window, 200, 200); window.SaveProject(true);
            Invoke(window, "SaveRecoveryAndWait"); string root = window.RecoveryRoot;
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
            PaintDot(window, 200, 200); Invoke(window, "SaveRecoveryAndWait"); string root = window.RecoveryRoot;
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
                // 通知も裏で容量を数える。前から走っていた確認を戻してから、この入力を測る。
                var field = window.GetType().GetField("recoveryStorageTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                ((Task<long>)field.GetValue(window))?.GetAwaiter().GetResult(); Invoke(window, "PollRecovery");
                Invoke(window, "CheckRecoveryStorage"); ((Task<long>)field.GetValue(window)).GetAwaiter().GetResult(); Invoke(window, "PollRecovery");
                Assert.That(window.StatusMessage, Does.Contain("above the 1 MiB"));
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
            Invoke(window, "SaveRecoveryAndWait"); string root = window.RecoveryRoot; byte[] before = DocumentBinary.Write(window.Document);
            PaintDot(window, 200, 200); Invoke(window, "SaveRecoveryAndWait");
            window.Document.Undo(); Invoke(window, "SaveRecoveryAndWait");
            Assert.That(window.LastRecoveryWrittenBytes, Is.Zero); Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(before));
            BeginLine(300, 300); Key(window, KeyCode.Escape); Invoke(window, "SaveRecoveryAndWait");
            Assert.That(GenerationStore.Load(root).Files[YlpFormat.SetEntry(window.Document.Id, "document.utpaint")], Is.EqualTo(before));
        }
    }
}
