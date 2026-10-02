using System.Collections.Generic;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ウィンドウの保存・開く・復旧 checkpoint が選択範囲を持ち運ぶ。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void TheSelectionIsSavedAndReopened()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            PaintDot(window, 200, 300);
            var d = window.Document;
            d.SetSelection(SelectionMask.Ellipse(d, d.Width / 2.0, d.Height / 2.0, d.Width / 5.0, d.Height / 7.0).Feather(4));
            byte[] expected = SelectionBinary.Write(d.Selection);
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            Assert.That(YlpStore.Load(fake.File).Files[SelectionBinary.EntryName], Is.EqualTo(expected));
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File; other.OpenProject();
                Assert.That(other.Document.Selection, Is.Not.Null, other.StatusMessage);
                Assert.That(SelectionBinary.Write(other.Document.Selection), Is.EqualTo(expected));
                Assert.That(other.IsSaved, Is.True, "restoring the selection is not an unsaved change");
                Assert.That(other.Document.CanUndo, Is.False);
            }
            finally { Close(other); }
            // 選択を外して保存すると、エントリーも無くなる
            d.ClearSelection(); window.SaveProject(false);
            Assert.That(YlpStore.Load(fake.File).Files.ContainsKey(SelectionBinary.EntryName), Is.False);
        }

        [Test] public void AnUnreadableSelectionOpensTheDocumentWithoutOneAndSaysSo()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            PaintDot(window, 200, 300); window.SaveProject(true);
            var files = new Dictionary<string, byte[]>(YlpStore.Load(fake.File).Files) { [SelectionBinary.EntryName] = new byte[] { 1, 2, 3 } };
            string path = NewYlpPath("Broken.ylp"); YlpStore.Save(path, files, null, false);
            var other = Open();
            try
            {
                UseFakeDialogs(other); other.OpenProjectAt(path);
                Assert.That(DocumentBinary.Write(other.Document), Is.EqualTo(files[YlpArchive.NativeName]), other.StatusMessage);
                Assert.That(other.Document.Selection, Is.Null);
                Assert.That(other.StatusMessage, Does.Contain("selection was not restored"));
            }
            finally { Close(other); }
        }

        [Test] public void TheRecoveryCheckpointKeepsTheSelection()
        {
            PaintDot(window, 777, 333);
            var d = window.Document; d.SetSelection(SelectionMask.Rectangle(d, 10, 20, 300, 200));
            Invoke(window, "OnLostFocus");
            var recovery = GenerationStore.Load(window.RecoveryRoot);
            Assert.That(recovery.Files[SelectionBinary.EntryName], Is.EqualTo(SelectionBinary.Write(d.Selection)));
            d.ClearSelection(); Invoke(window, "OnLostFocus");
            Assert.That(GenerationStore.Load(window.RecoveryRoot).Files.ContainsKey(SelectionBinary.EntryName), Is.False);
        }
    }
}
