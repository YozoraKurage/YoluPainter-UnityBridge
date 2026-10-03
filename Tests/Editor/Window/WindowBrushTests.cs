using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウのブラシ選択・取り込み・削除と、brush.json での保存復元。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>個人のブラシ置き場（一時プロジェクトの UserSettings の下。ウィンドウのテストは毎回一時プロジェクトで動く）。</summary>
        static string UseTemporaryBrushLibrary() => PainterSettings.BrushFolder;

        [Test] public void ImportingABrushFileStoresItAndSelectsTheFirstBrush()
        {
            UseTemporaryBrushLibrary();
            var fake = UseFakeDialogs(window); fake.File = NewTempPath(".gbr");
            File.WriteAllBytes(fake.File, GimpBrushTests.Gbr(2, 2, new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0 }, bytes: 4, name: "Colour tip"));
            var color = window.Brush.color;
            window.ImportBrushes();
            Assert.That(window.StatusMessage, Does.Contain("Imported 1 brush from"));
            Assert.That(fake.Asked, Does.Contain("Inform: Brush import notes"), "the colour → coverage reduction is reported");
            Assert.That(BrushLibrary.Personal.Presets.Single().Name, Is.EqualTo("Colour tip"));
            Assert.That(window.Brush.presetId, Is.EqualTo(BrushLibrary.Personal.Presets[0].Id));
            Assert.That(window.Brush.color, Is.EqualTo(color), "applying a preset keeps the chosen value");
            var tip = window.GetBrush().Tip;
            Assert.That(tip, Is.Not.Null); Assert.That(tip[0, 1], Is.EqualTo(255));
            PaintDot(window, 300, 300);
        }

        [Test] public void AFailedImportExplainsWhyAndChangesNothing()
        {
            UseTemporaryBrushLibrary();
            var fake = UseFakeDialogs(window); fake.File = NewTempPath(".sut"); File.WriteAllBytes(fake.File, new byte[64]);
            string before = JsonUtility.ToJson(window.Brush);
            window.ImportBrushes();
            Assert.That(fake.Asked, Does.Contain("Inform: Brush import failed"));
            Assert.That(window.StatusMessage, Does.Contain("Clip Studio"));
            Assert.That(BrushLibrary.Personal.Presets, Is.Empty);
            Assert.That(JsonUtility.ToJson(window.Brush), Is.EqualTo(before));
            fake.File = ""; fake.Asked.Clear();
            window.ImportBrushes();
            Assert.That(fake.Asked, Is.EqualTo(new[] { "OpenFile" }), "cancelling the file panel does nothing else");
        }

        [Test] public void DeletingAnImportedBrushAsksFirst()
        {
            string folder = UseTemporaryBrushLibrary();
            var preset = BrushLibrary.Personal.Add(new[] { new Core.Brushes.ImportedBrush("Mine", "test", new BrushSettings { Tip = new BrushTip("t", 2, 2, new byte[] { 255, 255, 255, 255 }) }) }, "")[0];
            window.ApplyPreset(preset);
            var fake = UseFakeDialogs(window); fake.ConfirmAnswer = false;
            window.DeleteImportedBrush();
            Assert.That(BrushLibrary.Personal.Presets.Count, Is.EqualTo(1), "declined");
            fake.ConfirmAnswer = true;
            window.DeleteImportedBrush();
            Assert.That(BrushLibrary.Personal.Presets, Is.Empty);
            Assert.That(Directory.GetFiles(folder), Is.Empty);
            Assert.That(window.Brush.presetId, Is.EqualTo(BuiltInBrushes.Presets[0].Id), "the window moves to a brush that still exists");
            fake.Asked.Clear(); window.DeleteImportedBrush();
            Assert.That(fake.Asked, Is.Empty, "built-in brushes cannot be deleted");
        }

        [Test] public void TheChosenBrushIsSavedAndRestoredWithTheProject()
        {
            var bundled = BundledBrushSets.Presets.First(p => p.CreateSettings().Tips != null);
            window.ApplyPreset(bundled);
            var b = window.Brush; b.sizeJitter = .25f; b.count = 4; b.angle = 33; b.textureId = "builtin:" + BuiltInBrushes.TipIds.First(); b.textureDepth = .5f;
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            PaintDot(window, 200, 200);
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File;
                other.OpenProject();
                var r = other.Brush;
                Assert.That(r.presetId, Is.EqualTo(bundled.Id)); Assert.That(r.tipId, Is.EqualTo(bundled.Id));
                Assert.That((r.sizeJitter, r.count, r.angle, r.textureId, r.textureDepth), Is.EqualTo((.25f, 4, 33f, b.textureId, .5f)));
                var s = other.GetBrush();
                Assert.That(s.Tips, Is.EqualTo(bundled.CreateSettings().Tips), "the bundled hose is found again by id");
                Assert.That(s.Texture, Is.SameAs(BrushTips.Resolve(b.textureId)));
            }
            finally { Close(other); }
        }

        [Test] public void ABrushWhoseTipIsMissingInThisProjectSaysSoAndPaintsRound()
        {
            UseTemporaryBrushLibrary();
            var b = window.Brush; b.tipId = "library:from-another-project-0000"; b.textureId = "";
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            PaintDot(window, 120, 120);
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File;
                other.OpenProject();
                Assert.That(other.StatusMessage, Does.Contain("Opened").And.Contain("library:from-another-project-0000").And.Contain("not available"));
                Assert.That(other.Brush.tipId, Is.EqualTo("library:from-another-project-0000"), "the reference is kept, not rewritten");
                Assert.That(other.GetBrush().Tip, Is.Null); Assert.That(other.GetBrush().Tips, Is.Null);
            }
            finally { Close(other); }
        }
    }
}
