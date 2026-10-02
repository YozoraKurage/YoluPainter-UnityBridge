using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウがプロジェクトごとの設定（既定の大きさ・メモリ予算・ブラシの置き場）に従うこと。</summary>
    /// <summary>設定ページの OnGUI を実際の IMGUI イベントで描かせる入れ物（Project Settings のウィンドウはテストから
    /// 中身の例外を取り出せないため）。</summary>
    internal sealed class SettingsPageHost : EditorWindow
    {
        public SettingsProvider Provider; public Exception Error; public int Drawn;
        void OnGUI()
        {
            if (Provider == null) return;
            try { Provider.OnGUI(""); Drawn++; }
            catch (ExitGUIException) { throw; }
            catch (Exception ex) { Error = ex; }
        }
    }

    public sealed partial class WindowTests
    {
        [Test] public void TheSettingsPageDrawsWithAndWithoutProblems()
        {
            var host = EditorWindow.CreateWindow<SettingsPageHost>();
            try
            {
                host.position = new Rect(60, 60, 800, 600);
                host.Provider = PainterSettingsProvider.Create(); host.Provider.OnActivate("", null);
                Repaint(host);
                Assert.That(host.Error, Is.Null, host.Error?.ToString()); Assert.That(host.Drawn, Is.GreaterThan(0)); // Layout と Repaint
                int drawn = host.Drawn;
                // 壊れた共有設定、共有のブラシ置き場あり、の状態でも描ける
                Directory.CreateDirectory(Path.GetDirectoryName(PainterSettings.SharedPath));
                File.WriteAllText(PainterSettings.SharedPath, "{ broken");
                Directory.CreateDirectory(Path.GetDirectoryName(PainterSettings.PersonalPath));
                File.WriteAllText(PainterSettings.PersonalPath, "{\"schema\":1,\"brushFolder\":\"Mine~\"}");
                host.Provider.OnActivate("", null);
                var shared = PainterSettings.SharedSettings; shared.projectBrushFolder = "Team"; PainterSettings.Save(shared, null);
                host.Provider.OnActivate("", null);
                Repaint(host);
                Assert.That(host.Error, Is.Null, host.Error?.ToString()); Assert.That(host.Drawn, Is.GreaterThan(drawn));
                Assert.That(PainterSettings.BrushFolder, Does.EndWith("Mine~"));
            }
            finally { host.Close(); }
        }

        [Test] public void ANewWindowUsesTheProjectDefaultsAndFollowsBudgetChanges()
        {
            var shared = PainterSettings.SharedSettings; shared.defaultResolution = 512;
            var personal = PainterSettings.PersonalSettings; personal.undoBudgetMiB = 8; personal.sourceBudgetMiB = 64; personal.strokeBudgetMiB = 16; personal.minUndoSteps = 3;
            PainterSettings.Save(shared, personal);
            var other = Open();
            try
            {
                var d = other.Document;
                Assert.That(d.Width, Is.EqualTo(512));
                Assert.That((d.UndoBudgetBytes, d.SourceBudgetBytes, d.ActiveStrokeBudgetBytes), Is.EqualTo((8L << 20, 64L << 20, 16L << 20)));
                Assert.That(d.MinimumUndoSteps, Is.EqualTo(3));
                personal.sourceBudgetMiB = 128; PainterSettings.Save(null, personal);
                Assert.That(other.Document.SourceBudgetBytes, Is.EqualTo(128L << 20), "an open window applies changed budgets");
                Assert.That(window.Document.SourceBudgetBytes, Is.EqualTo(128L << 20), "to every open window");
            }
            finally { Close(other); }
        }

        [Test] public void ImportAsksWhereToStoreWhenTheProjectSharesABrushFolder()
        {
            var shared = PainterSettings.SharedSettings; shared.projectBrushFolder = "TeamBrushes"; PainterSettings.Save(shared, null);
            var fake = UseFakeDialogs(window); fake.File = NewTempPath(".gbr");
            File.WriteAllBytes(fake.File, GimpBrushTests.Gbr(2, 2, new byte[] { 255, 255, 255, 255 }, name: "Team tip"));
            fake.ConfirmAnswer = true;
            window.ImportBrushes();
            Assert.That(fake.Asked, Does.Contain("Confirm: Import brushes"));
            Assert.That(BrushLibrary.Project.Presets.Single().Name, Is.EqualTo("Team tip"));
            Assert.That(window.Brush.presetId, Does.StartWith("project:"));
            Assert.That(PainterSettings.BrushImportFolder, Is.EqualTo(Path.GetDirectoryName(fake.File)), "the import folder is remembered for next time");
            fake.ConfirmAnswer = false;
            window.ImportBrushes();
            Assert.That(BrushLibrary.Personal.Presets.Single().Name, Is.EqualTo("Team tip"));
            fake.Asked.Clear(); fake.ConfirmAnswer = true; window.ApplyPreset(BrushLibrary.Project.Presets[0]);
            window.DeleteImportedBrush();
            Assert.That(fake.Asked.Single(), Is.EqualTo("Confirm: Delete imported brush"));
            Assert.That(BrushLibrary.Project.Presets, Is.Empty); Assert.That(BrushLibrary.Personal.Presets.Count, Is.EqualTo(1), "only the chosen library is touched");
        }

        [Test] public void WithoutASharedFolderImportDoesNotAsk()
        {
            var fake = UseFakeDialogs(window); fake.File = NewTempPath(".gbr");
            File.WriteAllBytes(fake.File, GimpBrushTests.Gbr(1, 1, new byte[] { 255 }, name: "Solo"));
            window.ImportBrushes();
            Assert.That(fake.Asked, Is.EqualTo(new[] { "OpenFile" }));
            Assert.That(BrushLibrary.Personal.Presets.Single().Name, Is.EqualTo("Solo"));
        }
    }
}
