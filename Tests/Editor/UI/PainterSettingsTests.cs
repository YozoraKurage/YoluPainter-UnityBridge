using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Brushes;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>プロジェクトごとの設定（共有 / 個人の 2 ファイル）、検査、壊れた・新しい版のファイル、ブラシ置き場の切り替え。</summary>
    public sealed class PainterSettingsTests
    {
        string project;

        sealed class Answers : IPainterDialogs
        {
            public bool ConfirmAnswer; public string Folder = "";
            public readonly List<string> Asked = new List<string>();
            public string SaveFolder(string title, string folder, string defaultName) => Folder;
            public string OpenFolder(string title, string folder) { Asked.Add("OpenFolder"); return Folder; }
            public string OpenFile(string title, string folder, string extension) => "";
            public string SaveFile(string title, string folder, string defaultName, string extension) => "";
            public bool Confirm(string title, string message, string ok, string cancel) { Asked.Add("Confirm: " + title); return ConfirmAnswer; }
            public void Inform(string title, string message) { Asked.Add("Inform: " + title); }
            public void Progress(string title, string info, float progress) { }
            public void ClearProgress() { }
        }

        [SetUp] public void UseTemporaryProject()
        {
            project = Path.Combine(Path.GetTempPath(), "yolupainter-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(project);
            PainterSettings.ProjectRoot = project;
            PainterSettings.SystemMemoryMiBOverride = 16384; PainterSettings.GraphicsMemoryMiBOverride = 8192; // 自動の予算をマシンに依らず確かめる
        }

        [TearDown] public void RestoreProject()
        {
            PainterSettings.SystemMemoryMiBOverride = null; PainterSettings.GraphicsMemoryMiBOverride = null;
            PainterSettings.ProjectRoot = null; BrushLibrary.Personal.Folder = null; BrushLibrary.Project.Folder = null;
            if (Directory.Exists(project)) Directory.Delete(project, true);
        }

        static string P(params string[] parts) => Path.Combine(parts);
        static ImportedBrush Brush(string name) => new ImportedBrush(name, "test", new BrushSettings { Tip = new BrushTip(name, 2, 2, new byte[] { 255, 0, 0, 255 }) });

        [Test] public void WithoutFilesTheDefaultsApplyAndNothingIsWritten()
        {
            Assert.That(PainterSettings.DefaultResolution, Is.EqualTo(1024));
            Assert.That(PainterSettings.RecoveryIntervalSeconds, Is.EqualTo(15));
            Assert.That(PainterSettings.UndoBudgetBytes, Is.EqualTo(1024L << 20), "automatic on 16 GB, like GIMP's 1 GiB"); Assert.That(PainterSettings.SourceBudgetBytes, Is.EqualTo(2048L << 20)); Assert.That(PainterSettings.StrokeBudgetBytes, Is.EqualTo(512L << 20));
            Assert.That(PainterSettings.MinUndoSteps, Is.EqualTo(5));
            Assert.That(PainterSettings.BrushFolder, Is.EqualTo(P(project, "UserSettings", "YoluPainter", "Brushes")));
            Assert.That(PainterSettings.ProjectBrushFolder, Is.Null); Assert.That(BrushLibrary.Project.Enabled, Is.False);
            Assert.That(PainterSettings.ShowBundledBrushes, Is.True);
            Assert.That(PainterSettings.Warnings, Is.Empty);
            Assert.That(Directory.GetFileSystemEntries(project), Is.Empty, "reading settings creates no files");
        }

        [Test] public void SharedAndPersonalSettingsAreSavedToTheirOwnFiles()
        {
            int changed = 0; Action count = () => changed++;
            PainterSettings.Changed += count;
            try
            {
                var shared = PainterSettings.SharedSettings; shared.defaultResolution = 2048; shared.projectBrushFolder = "Art/Brushes";
                var personal = PainterSettings.PersonalSettings; personal.recoveryIntervalSeconds = 40; personal.undoBudgetMiB = 128; personal.showBundledBrushes = false;
                PainterSettings.Save(shared, personal);
                Assert.That(changed, Is.EqualTo(1));
            }
            finally { PainterSettings.Changed -= count; }
            Assert.That(PainterSettings.SharedPath, Is.EqualTo(P(project, "ProjectSettings", "Packages", "net.yozolab.yolupainter", "Settings.json")));
            Assert.That(PainterSettings.PersonalPath, Is.EqualTo(P(project, "UserSettings", "YoluPainter", "Settings.json")));
            Assert.That(File.ReadAllText(PainterSettings.SharedPath), Does.Contain("\"defaultResolution\": 2048").And.Not.Contain("undoBudget"), "personal values stay out of the shared file");
            Assert.That(File.ReadAllText(PainterSettings.PersonalPath), Does.Contain("\"undoBudgetMiB\": 128").And.Not.Contain("projectBrushFolder"));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(PainterSettings.SharedPath)).Select(Path.GetFileName), Is.EqualTo(new[] { "Settings.json" }), "no temporary file is left");

            PainterSettings.ProjectRoot = project; // 読み直す
            Assert.That(PainterSettings.DefaultResolution, Is.EqualTo(2048)); Assert.That(PainterSettings.RecoveryIntervalSeconds, Is.EqualTo(40));
            Assert.That(PainterSettings.UndoBudgetBytes, Is.EqualTo(128L << 20)); Assert.That(PainterSettings.ShowBundledBrushes, Is.False);
            Assert.That(PainterSettings.ProjectBrushFolder, Is.EqualTo(P(project, "Art", "Brushes")));
            // 写しを変えても保存しなければ反映されない
            PainterSettings.PersonalSettings.recoveryIntervalSeconds = 99;
            Assert.That(PainterSettings.RecoveryIntervalSeconds, Is.EqualTo(40));
        }

        [Test] public void InvalidValuesAreRefusedOnSaveAndRepairedOnLoad()
        {
            var personal = PainterSettings.PersonalSettings; personal.recoveryIntervalSeconds = 1;
            Assert.That(() => PainterSettings.Save(null, personal), Throws.ArgumentException.With.Message.Contains("Recovery interval"));
            var shared = PainterSettings.SharedSettings; shared.defaultResolution = 1000;
            Assert.That(() => PainterSettings.Save(shared, null), Throws.ArgumentException.With.Message.Contains("Default resolution"));
            Assert.That(File.Exists(PainterSettings.PersonalPath) || File.Exists(PainterSettings.SharedPath), Is.False, "nothing is written");

            Directory.CreateDirectory(Path.GetDirectoryName(PainterSettings.PersonalPath));
            File.WriteAllText(PainterSettings.PersonalPath, "{\"schema\":1,\"recoveryIntervalSeconds\":100000,\"sourceBudgetMiB\":1,\"undoBudgetMiB\":32}");
            PainterSettings.ProjectRoot = project;
            Assert.That(PainterSettings.RecoveryIntervalSeconds, Is.EqualTo(15)); Assert.That(PainterSettings.SourceBudgetBytes, Is.EqualTo(2048L << 20), "repaired to automatic");
            Assert.That(PainterSettings.UndoBudgetBytes, Is.EqualTo(32L << 20), "valid values in the same file are kept");
            Assert.That(PainterSettings.Warnings.Count, Is.EqualTo(2)); Assert.That(PainterSettings.Warnings, Has.All.Contains("The default is used"));
        }

        [Test] public void AutomaticBudgetsFollowTheMachineAndExplicitValuesStay()
        {
            foreach (var (ram, undo, source, stroke) in new[] { (4096, 256, 512, 128), (8192, 512, 1024, 256), (32768, 2048, 4096, 1024), (131072, 2048, 8192, 1024), (1024, 256, 256, 64) })
            {
                PainterSettings.SystemMemoryMiBOverride = ram;
                Assert.That((PainterSettings.UndoBudgetBytes >> 20, PainterSettings.SourceBudgetBytes >> 20, PainterSettings.StrokeBudgetBytes >> 20), Is.EqualTo(((long)undo, (long)source, (long)stroke)), ram + " MiB");
            }
            PainterSettings.SystemMemoryMiBOverride = 16384;
            foreach (var (vram, cache) in new[] { (512, 128), (4096, 512), (8192, 1024), (24576, 1024) })
            { PainterSettings.GraphicsMemoryMiBOverride = vram; Assert.That(PainterSettings.GpuCacheBytes >> 20, Is.EqualTo((long)cache), vram + " MiB of video memory"); }
            // 以前の版が書いた明示の値（自動が無かったころの既定 64 / 256 / 64）はそのまま使う
            Directory.CreateDirectory(Path.GetDirectoryName(PainterSettings.PersonalPath));
            File.WriteAllText(PainterSettings.PersonalPath, "{\"schema\":1,\"undoBudgetMiB\":64,\"sourceBudgetMiB\":256,\"strokeBudgetMiB\":64}");
            PainterSettings.ProjectRoot = project;
            Assert.That((PainterSettings.UndoBudgetBytes, PainterSettings.SourceBudgetBytes, PainterSettings.StrokeBudgetBytes), Is.EqualTo((64L << 20, 256L << 20, 64L << 20)));
            Assert.That(PainterSettings.MinUndoSteps, Is.EqualTo(5), "a file without the field gets the default");
            Assert.That(PainterSettings.Warnings, Is.Empty);
            var personal = PainterSettings.PersonalSettings; personal.strokeBudgetMiB = PainterSettings.Automatic; personal.minUndoSteps = 0;
            PainterSettings.Save(null, personal);
            Assert.That(PainterSettings.StrokeBudgetBytes, Is.EqualTo(512L << 20)); Assert.That(PainterSettings.MinUndoSteps, Is.EqualTo(0));
            personal.minUndoSteps = 101;
            Assert.That(() => PainterSettings.Save(null, personal), Throws.ArgumentException.With.Message.Contains("Minimum undo steps"));
            personal.minUndoSteps = 5; personal.gpuCacheMiB = 0; PainterSettings.Save(null, personal);
            Assert.That(PainterSettings.GpuCacheBytes, Is.Zero, "0 keeps no GPU copies");
            personal.minUndoSteps = 3; personal.undoBudgetMiB = -2;
            Assert.That(() => PainterSettings.Save(null, personal), Throws.ArgumentException.With.Message.Contains("automatic"));
        }

        [Test] public void BackupsToKeepDefaultsToAllAndIsRangeChecked()
        {
            Assert.That(PainterSettings.BackupsToKeep, Is.EqualTo(-1), "by default no backup is ever deleted");
            var personal = PainterSettings.PersonalSettings;
            foreach (int bad in new[] { -2, PainterSettings.MaxBackups + 1 })
            {
                personal.backupsToKeep = bad;
                Assert.That(() => PainterSettings.Save(null, personal), Throws.ArgumentException.With.Message.Contains("Backups to keep"));
            }
            personal.backupsToKeep = 0; PainterSettings.Save(null, personal);
            PainterSettings.ProjectRoot = project;
            Assert.That(PainterSettings.BackupsToKeep, Is.EqualTo(0));
        }

        /// <summary>表示の合成と CPU のスレッドの数: 既定は自動、保存して読み直せる、範囲外は保存を断り読み込みで直す。スレッドの数は
        /// Core（CoreParallelism）に入る。</summary>
        [Test] public void DisplayCompositingAndCpuThreadsAreSavedCheckedAndAppliedToTheCore()
        {
            try
            {
                Assert.That(PainterSettings.DisplayCompositing, Is.EqualTo(CompositorBackend.Automatic));
                Assert.That(PainterSettings.CpuThreads, Is.EqualTo(PainterSettings.Automatic));
                Assert.That(CoreParallelism.MaxDegreeOfParallelism, Is.Zero, "automatic: one thread per logical processor");
                var personal = PainterSettings.PersonalSettings; personal.displayCompositing = CompositorBackend.Cpu; personal.cpuThreads = 3;
                PainterSettings.Save(null, personal);
                Assert.That(File.ReadAllText(PainterSettings.PersonalPath), Does.Contain("\"displayCompositing\": 2").And.Contain("\"cpuThreads\": 3"));
                Assert.That(CoreParallelism.MaxDegreeOfParallelism, Is.EqualTo(3), "saving applies the thread limit");
                PainterSettings.ProjectRoot = project; // 読み直す
                Assert.That(PainterSettings.DisplayCompositing, Is.EqualTo(CompositorBackend.Cpu)); Assert.That(PainterSettings.CpuThreads, Is.EqualTo(3));
                Assert.That(CoreParallelism.Degree, Is.EqualTo(3));
                personal.cpuThreads = 1; PainterSettings.Save(null, personal);
                Assert.That(CoreParallelism.Degree, Is.EqualTo(1), "1 = no parallel work");
                foreach (int bad in new[] { 0, -2, PainterSettings.MaxCpuThreads + 1 })
                {
                    personal.cpuThreads = bad;
                    Assert.That(() => PainterSettings.Save(null, personal), Throws.ArgumentException.With.Message.Contains("CPU threads"), bad.ToString());
                }
                personal.cpuThreads = 1; personal.displayCompositing = (CompositorBackend)9;
                Assert.That(() => PainterSettings.Save(null, personal), Throws.ArgumentException.With.Message.Contains("Display compositing"));
                Assert.That(PainterSettings.DisplayCompositing, Is.EqualTo(CompositorBackend.Cpu), "a refused save changes nothing");

                // 知らない値（新しい版の選択肢・手で書いた値）は読み込みで自動に直し、同じファイルのほかの値は残す
                File.WriteAllText(PainterSettings.PersonalPath, "{\"schema\":1,\"displayCompositing\":7,\"cpuThreads\":0,\"recoveryIntervalSeconds\":30}");
                PainterSettings.ProjectRoot = project;
                Assert.That(PainterSettings.DisplayCompositing, Is.EqualTo(CompositorBackend.Automatic)); Assert.That(PainterSettings.CpuThreads, Is.EqualTo(PainterSettings.Automatic));
                Assert.That(PainterSettings.RecoveryIntervalSeconds, Is.EqualTo(30));
                Assert.That(PainterSettings.Warnings.Count, Is.EqualTo(2)); Assert.That(PainterSettings.Warnings, Has.Some.Contains("Display compositing 7")); Assert.That(PainterSettings.Warnings, Has.Some.Contains("CPU threads"));
                Assert.That(CoreParallelism.MaxDegreeOfParallelism, Is.Zero);
                // この項目の無い以前のファイルは自動のまま、知らせも無い
                File.WriteAllText(PainterSettings.PersonalPath, "{\"schema\":1,\"recoveryIntervalSeconds\":20}");
                PainterSettings.ProjectRoot = project;
                Assert.That((PainterSettings.DisplayCompositing, PainterSettings.CpuThreads), Is.EqualTo((CompositorBackend.Automatic, PainterSettings.Automatic)));
                Assert.That(PainterSettings.Warnings, Is.Empty);
            }
            finally { PainterSettings.ProjectRoot = project; CoreParallelism.MaxDegreeOfParallelism = 0; }
        }

        [Test] public void TheSettingsPageListsThreadChoicesUpToThisMachine()
        {
            PainterSettings.ProcessorCountOverride = 12;
            try
            {
                Assert.That(PainterSettingsProvider.ThreadChoices(), Is.EqualTo(new[] { PainterSettings.Automatic, 1, 2, 4, 8, 12 }));
                PainterSettings.ProcessorCountOverride = 1;
                Assert.That(PainterSettingsProvider.ThreadChoices(), Is.EqualTo(new[] { PainterSettings.Automatic, 1 }));
            }
            finally { PainterSettings.ProcessorCountOverride = null; }
            TileGpuCompositor.SimulatedGpuUnavailable = "no device (test)";
            try
            {
                Assert.That(PainterSettingsProvider.CompositingNote(CompositorBackend.Gpu, "no device (test)"), Does.Contain("the CPU is used"));
                Assert.That(PainterSettingsProvider.CompositingNote(CompositorBackend.Automatic, "no device (test)"), Does.Contain("the CPU"));
                Assert.That(PainterSettingsProvider.CompositingNote(CompositorBackend.Cpu, null), Does.Contain("on the CPU"));
                TileGpuCompositor.SimulatedDeviceName = "NVIDIA GeForce RTX 4080 SUPER";
                Assert.That(PainterSettingsProvider.CompositingNote(CompositorBackend.Automatic, null), Does.StartWith("Here: the GPU"));
                TileGpuCompositor.SimulatedDeviceName = "llvmpipe (LLVM 15.0.7, 256 bits)"; // ソフトウェアの描画では自動は CPU
                Assert.That(PainterSettingsProvider.CompositingNote(CompositorBackend.Automatic, null), Does.StartWith("Here: the CPU (the GPU is a software renderer"));
            }
            finally { TileGpuCompositor.SimulatedGpuUnavailable = null; TileGpuCompositor.SimulatedDeviceName = null; }
        }

        [Test] public void ABrokenFileIsKeptAsideWhenSettingsAreSaved()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PainterSettings.SharedPath));
            File.WriteAllText(PainterSettings.SharedPath, "{ not json");
            PainterSettings.ProjectRoot = project;
            Assert.That(PainterSettings.DefaultResolution, Is.EqualTo(1024));
            Assert.That(PainterSettings.Warnings.Single(), Does.Contain("could not be read").And.Contain(".broken"));
            var shared = PainterSettings.SharedSettings; shared.defaultResolution = 512;
            PainterSettings.Save(shared, null);
            Assert.That(File.ReadAllText(PainterSettings.SharedPath + ".broken"), Is.EqualTo("{ not json"), "the unreadable file is not lost");
            Assert.That(PainterSettings.DefaultResolution, Is.EqualTo(512)); Assert.That(PainterSettings.Warnings, Is.Empty);
        }

        [Test] public void AFileFromANewerVersionIsNeverOverwritten()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PainterSettings.PersonalPath));
            const string future = "{\"schema\":2,\"recoveryIntervalSeconds\":30,\"somethingNew\":true}";
            File.WriteAllText(PainterSettings.PersonalPath, future);
            PainterSettings.ProjectRoot = project;
            Assert.That(PainterSettings.PersonalIsReadOnly, Is.True); Assert.That(PainterSettings.RecoveryIntervalSeconds, Is.EqualTo(15));
            Assert.That(PainterSettings.Warnings.Single(), Does.Contain("newer YoluPainter"));
            Assert.That(() => PainterSettings.Save(null, PainterSettings.PersonalSettings), Throws.InvalidOperationException);
            PainterSettings.UpdatePersonal(p => p.brushImportFolder = "/somewhere");
            Assert.That(File.ReadAllText(PainterSettings.PersonalPath), Is.EqualTo(future));
            var shared = PainterSettings.SharedSettings; shared.defaultResolution = 256;
            PainterSettings.Save(shared, null);
            Assert.That(PainterSettings.DefaultResolution, Is.EqualTo(256), "the other file can still be saved");
        }

        [Test] public void BrushFoldersMustNotBeImportedByUnityAndSharedOnesStayInsideTheProject()
        {
            string outside = Path.Combine(Path.GetTempPath(), "elsewhere-brushes");
            Assert.That(PainterSettings.CheckProjectBrushFolder(""), Is.Null);
            Assert.That(PainterSettings.CheckProjectBrushFolder("YoluPainter/Brushes"), Is.Null);
            Assert.That(PainterSettings.CheckProjectBrushFolder("Assets/Art/Brushes~"), Is.Null, "a folder ending in ~ is not imported");
            Assert.That(PainterSettings.CheckProjectBrushFolder(outside), Does.Contain("relative"));
            Assert.That(PainterSettings.CheckProjectBrushFolder("../other/Brushes"), Does.Contain("inside the project"));
            Assert.That(PainterSettings.CheckProjectBrushFolder("Assets/Brushes"), Does.Contain("imported by Unity"));
            Assert.That(PainterSettings.CheckProjectBrushFolder("Packages/Mine/Brushes"), Does.Contain("imported by Unity"));
            Assert.That(PainterSettings.CheckProjectBrushFolder("."), Does.Contain("project folder itself"));
            Assert.That(PainterSettings.CheckPersonalBrushFolder(outside), Is.Null, "your own folder may be outside the project");
            Assert.That(PainterSettings.CheckPersonalBrushFolder("Assets/Brushes"), Does.Contain("imported by Unity"));
            Assert.That(PainterSettings.CheckPersonalBrushFolder(project), Does.Contain("project folder itself"));
            var shared = PainterSettings.SharedSettings; shared.projectBrushFolder = outside;
            Assert.That(() => PainterSettings.Save(shared, null), Throws.ArgumentException);
        }

        [Test] public void BrushLibrariesFollowTheSettings()
        {
            BrushLibrary.Personal.Add(new[] { Brush("Mine") }, "");
            Assert.That(File.Exists(Directory.GetFiles(P(project, "UserSettings", "YoluPainter", "Brushes"), "Mine-*.json").Single()));

            var shared = PainterSettings.SharedSettings; shared.projectBrushFolder = "TeamBrushes";
            PainterSettings.Save(shared, null);
            Assert.That(BrushLibrary.Project.Enabled, Is.True);
            var team = BrushLibrary.Project.Add(new[] { Brush("Team") }, "Set").Single();
            Assert.That(team.Id, Does.StartWith("project:"));
            Assert.That(Directory.GetFiles(P(project, "TeamBrushes"), "Team-*.json"), Has.Length.EqualTo(1));
            Assert.That(BrushLibrary.IsLibraryPreset(team.Id), Is.True); Assert.That(BrushLibrary.Owning(team.Id), Is.SameAs(BrushLibrary.Project));
            Assert.That(BrushTips.IdOf(team.CreateSettings()), Is.EqualTo(team.Id));
            Assert.That(BrushTips.Resolve(team.Id), Is.Not.Null);

            shared.projectBrushFolder = ""; PainterSettings.Save(shared, null);
            Assert.That(BrushLibrary.Project.Enabled, Is.False); Assert.That(BrushLibrary.Project.Presets, Is.Empty);
            Assert.That(BrushTips.Resolve(team.Id), Is.Null, "turning the shared folder off hides its brushes");
            Assert.That(() => BrushLibrary.Project.Add(new[] { Brush("x") }, ""), Throws.InvalidOperationException);
            Assert.That(Directory.Exists(P(project, "TeamBrushes")), Is.True, "turning it off deletes nothing");
        }

        [Test] public void MovingYourBrushFolderOffersToCopyAndKeepsTheOldOne()
        {
            var mine = BrushLibrary.Personal.Add(new[] { Brush("Keep") }, "").Single();
            string oldFolder = PainterSettings.BrushFolder, newFolder = Path.Combine(project + "-outside", "Brushes");
            try
            {
                var answers = new Answers { ConfirmAnswer = true };
                string notice = PainterSettingsProvider.ChangePersonalBrushFolder(newFolder, answers, out var error);
                Assert.That(error, Is.Null); Assert.That(notice, Does.Contain("copied 1"));
                Assert.That(answers.Asked, Is.EqualTo(new[] { "Confirm: Brush folder" }));
                Assert.That(PainterSettings.BrushFolder, Is.EqualTo(newFolder));
                Assert.That(Directory.GetFiles(oldFolder), Is.Not.Empty, "the old folder is left as it is");
                Assert.That(BrushTips.Resolve(mine.Id), Is.Not.Null, "the brush keeps its id in the new folder");

                answers = new Answers { ConfirmAnswer = false };
                notice = PainterSettingsProvider.ChangePersonalBrushFolder("", answers, out error);
                Assert.That(PainterSettings.BrushFolder, Is.EqualTo(oldFolder)); Assert.That(notice, Does.Not.Contain("copied"));
                Assert.That(PainterSettingsProvider.ChangePersonalBrushFolder("Assets/Brushes", answers, out error), Is.Null);
                Assert.That(error, Does.Contain("imported by Unity")); Assert.That(PainterSettings.BrushFolder, Is.EqualTo(oldFolder));
                Assert.That(PainterSettingsProvider.ChangePersonalBrushFolder("", answers, out error), Is.Null, "no change, no question");
            }
            finally { if (Directory.Exists(project + "-outside")) Directory.Delete(project + "-outside", true); }
        }

        [Test] public void PathsInsideTheProjectBecomeRelative()
        {
            Assert.That(PainterSettingsProvider.ToProjectRelativeIfInside(P(project, "A", "B")), Is.EqualTo("A/B"));
            Assert.That(PainterSettingsProvider.ToProjectRelativeIfInside(project), Is.EqualTo("."));
            string outside = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "x-other"));
            Assert.That(PainterSettingsProvider.ToProjectRelativeIfInside(outside), Is.EqualTo(outside));
            Assert.That(PainterSettingsProvider.ToProjectRelativeIfInside(project + "-sibling"), Is.EqualTo(Path.GetFullPath(project + "-sibling")), "a sibling with the same prefix is outside");
        }

        [Test] public void TheSettingsPageIsRegisteredUnderProjectSettings()
        {
            var provider = PainterSettingsProvider.Create();
            Assert.That(provider.settingsPath, Is.EqualTo("Project/YoluPainter"));
            Assert.That(provider.scope, Is.EqualTo(SettingsScope.Project));
            Assert.That(provider.keywords, Does.Contain("brush"));
        }
    }
}
