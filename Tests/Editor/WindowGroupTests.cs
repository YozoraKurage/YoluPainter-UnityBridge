using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウでのグループ: グループには描けないこと（Undo を積まない）、中の層には描けること、.ylp での保存復元。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void PaintingOnAGroupIsRefusedWithoutAnUndoStepButItsContentsCanBePainted()
        {
            var d = window.Document; var child = d.Layers[d.Layers.Count - 1];
            var group = d.GroupLayers(new[] { child.Id }, "G"); d.ClearHistory();
            window.SelectedLayer = group.Id;
            var at = At(window, 300, 300);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(window.StatusMessage, Does.Contain("A group has no pixels"));
            Assert.That(d.UndoCount, Is.EqualTo(0), "nothing was recorded, not even enabling a channel on the group");
            Assert.That(window.IsStroking, Is.False);
            window.SelectedLayer = child.Id;
            PaintDot(window, 300, 300);
            Assert.That(d.CompositePixel(PaintChannel.Color, 300, 300).A, Is.GreaterThan(0), "a layer inside a pass-through group shows");
        }

        [Test] public void GroupsSurviveSavingAndOpeningAYlp()
        {
            var d = window.Document; var child = d.Layers[d.Layers.Count - 1];
            PaintDot(window, 100, 100);
            var group = d.GroupLayers(new[] { child.Id }, "Folder"); d.SetLayerBlendMode(group.Id, LayerBlendMode.Multiply); d.SetLayerOpacity(group.Id, .6);
            var fake = UseFakeDialogs(window); string folder = NewTempPath(); Directory.CreateDirectory(folder); fake.File = Path.Combine(folder, "Grouped.ylp");
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File;
                other.OpenProject();
                var g = other.Document.GetLayer(group.Id);
                Assert.That(g.IsGroup, Is.True); Assert.That(g.BlendMode, Is.EqualTo(LayerBlendMode.Multiply));
                Assert.That(other.Document.GetLayer(child.Id).ParentId, Is.EqualTo(group.Id));
                Assert.That(DocumentBinary.Write(other.Document), Is.EqualTo(DocumentBinary.Write(d)));
            }
            finally { Close(other); }
        }
    }
}
