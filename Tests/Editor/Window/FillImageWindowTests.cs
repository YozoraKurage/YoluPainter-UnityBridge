using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 塗りつぶしの画像と投影をウィンドウの側から（表示しないウィンドウ。batch でも GUI でも回る）: 全部のテクスチャセットの文書がプロジェクトの
    /// リソースを読む、.ylp に保存して開く・復旧で画像の参照と投影が戻り合成が同じ（.ylp の形式は 4 のまま、正本は今の版）、使っている画像は
    /// リソースから消せない（層の名前を答える）、リソースに無い画像を読む層は開いたときに知らせて値を見せ参照は残す、PSD の書き出しは画素の層と
    /// 知らせ、投影できない画像は書き出しの前に確かめ保存の知らせに添える。
    /// </summary>
    public sealed class FillImageWindowTests
    {
        TexturePaintWindow window; Answers dialogs;
        readonly List<string> temporary = new List<string>();
        readonly List<TexturePaintWindow> others = new List<TexturePaintWindow>();

        sealed class Answers : IPainterDialogs
        {
            public string Folder = "", File = "";
            public bool ConfirmAnswer = true;
            public readonly List<string> Asked = new List<string>();
            public string SaveFolder(string title, string folder, string defaultName) { Asked.Add("SaveFolder"); return Folder; }
            public string OpenFolder(string title, string folder) { Asked.Add("OpenFolder"); return Folder; }
            public string OpenFile(string title, string folder, string extensions) { Asked.Add("OpenFile"); return File; }
            public string SaveFile(string title, string folder, string defaultName, string extension) { Asked.Add("SaveFile"); return File; }
            public bool Confirm(string title, string message, string ok, string cancel) { Asked.Add("Confirm: " + title); return ConfirmAnswer; }
            public void Inform(string title, string message) { Asked.Add("Inform: " + title); }
            public void Progress(string title, string info, float progress) { }
            public void ClearProgress() { }
        }

        [SetUp] public void Create()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            string project = Temp(); Directory.CreateDirectory(project); PainterSettings.ProjectRoot = project;
            window = NewWindow();
            window.CreateProject(new NewProjectSettings { Model = null, Resolution = 512, Template = ProjectTemplate.Pbr });
        }
        [TearDown] public void Clean()
        {
            foreach (var w in others.Concat(new[] { window }))
            {
                if (w == null) continue;
                string recovery = w.RecoveryRoot; Object.DestroyImmediate(w);
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
            }
            others.Clear(); window = null; PainterSettings.ProjectRoot = null;
            foreach (var path in temporary) { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); }
            temporary.Clear();
        }
        string Temp(string extension = "") { string path = Path.Combine(Path.GetTempPath(), "yolupainter-fillimage-" + Guid.NewGuid().ToString("N") + extension); temporary.Add(path); return path; }
        TexturePaintWindow NewWindow()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            var own = new Answers(); w.Dialogs = own;
            if (window != null) others.Add(w); else dialogs = own;
            return w;
        }

        /// <summary>内蔵の画像 2 つを取り込み、Color と Roughness に画像を持つ塗りつぶしの層を作る（UV、タイル 2、回転 30°）。</summary>
        PaintLayer ProjectedFill(out ImageResource checker, out ImageResource noise)
        {
            checker = window.ImportBuiltInImage("uv-checker"); noise = window.ImportBuiltInImage("value-noise");
            var d = window.Document;
            var fill = d.AddFillLayer("Projected", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(200, 30, 30, 255) } });
            window.SelectedLayer = fill.Id;
            window.SetFillImage(fill.Id, PaintChannel.Color, checker.Id);
            window.SetFillImage(fill.Id, PaintChannel.Roughness, noise.Id);
            d.SetFillProjection(fill.Id, fill.Projection.WithTiles(2, 2).WithRotation(30));
            Assert.That(d.ImageResources, Is.SameAs(window.ImageResources), "the window connects its documents to the project's resources");
            Assert.That(d.GetFillImageStatus(fill.Id, PaintChannel.Color).Active, Is.True);
            Assert.That(d.GetFillImageStatus(fill.Id, PaintChannel.Roughness).Conversion, Is.EqualTo(FillImageConversion.None), "value noise is linear data");
            Assert.That(window.StatusMessage, Does.Contain("Value noise").And.Contain("Roughness"), "the status says what the channel reads");
            return fill;
        }

        [Test] public void SaveOpenAndRecoverKeepTheImagesAndTheProjection()
        {
            var fill = ProjectedFill(out var checker, out var noise); var d = window.Document;
            var color = d.Composite(PaintChannel.Color); var rough = d.Composite(PaintChannel.Roughness);
            dialogs.File = Temp(".ylp"); window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var saved = YlpFormat.Open(YlpStore.Load(dialogs.File).Files);
            Assert.That(saved.Info.Format, Is.EqualTo(YlpFormat.Current), "the current .ylp format (the images are the project's resources; they add no format of their own)");
            var other = NewWindow(); other.OpenProjectAt(dialogs.File);
            Assert.That(other.StatusMessage, Does.StartWith("Opened"));
            var opened = other.Document.GetLayer(fill.Id);
            Assert.That(opened.FillImages[PaintChannel.Color], Is.EqualTo(checker.Id)); Assert.That(opened.FillImages[PaintChannel.Roughness], Is.EqualTo(noise.Id));
            Assert.That(opened.Projection, Is.EqualTo(fill.Projection));
            Assert.That(other.Document.ImageResources, Is.SameAs(other.ImageResources));
            Assert.That(other.Document.Composite(PaintChannel.Color), Is.EqualTo(color)); Assert.That(other.Document.Composite(PaintChannel.Roughness), Is.EqualTo(rough));
            Assert.That(other.MissingFillImageNote(), Is.Null);
            // 復旧の checkpoint から別の窓が戻す
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            window.GetType().GetMethod("OnLostFocus", flags).Invoke(window, null);
            var restored = NewWindow();
            restored.GetType().GetMethod("OnDisable", flags).Invoke(restored, null);
            string own = restored.RecoveryRoot; if (Directory.Exists(own)) Directory.Delete(own, true);
            restored.GetType().GetField("recoveryRoot", flags).SetValue(restored, window.RecoveryRoot);
            restored.GetType().GetMethod("OnEnable", flags).Invoke(restored, null);
            Assert.That(restored.StatusMessage, Does.StartWith("Recovered"));
            Assert.That(restored.Document.GetLayer(fill.Id).Projection, Is.EqualTo(fill.Projection));
            Assert.That(restored.Document.Composite(PaintChannel.Color), Is.EqualTo(color));
        }

        [Test] public void AnImageInUseCannotBeRemovedAndAMissingImageIsReportedWhenOpening()
        {
            var fill = ProjectedFill(out var checker, out _); var d = window.Document;
            var ex = Assert.Throws<ResourceRefusedException>(() => window.RemoveResource(checker.Id));
            Assert.That(ex.Refusal, Is.EqualTo(ResourceRefusal.InUse)); Assert.That(ex.Message, Does.Contain("'Projected' (Color)"));
            Assert.That(dialogs.Asked, Is.Empty, "refused before asking");
            Assert.That(window.ImageResources.Count, Is.EqualTo(2));
            // 外せば消せる（Undo で戻った参照は、リソースに無いことを知らせて値を見せる）
            d.SetFillImage(fill.Id, PaintChannel.Color, null);
            Assert.That(window.RemoveResource(checker.Id), Is.True);
            d.Undo();
            Assert.That(window.MissingFillImageNote(), Does.Contain("'Projected' (Color)"));
            // そのまま保存したファイルを開く: 開くのは断らず、参照を残して知らせる
            dialogs.File = Temp(".ylp"); window.SaveProject(true);
            var other = NewWindow(); other.OpenProjectAt(dialogs.File);
            Assert.That(other.StatusMessage, Does.StartWith("Opened").And.Contain("read an image this project does not have"));
            var opened = other.Document.GetLayer(fill.Id);
            Assert.That(opened.FillImages[PaintChannel.Color], Is.EqualTo(checker.Id), "the reference is kept");
            Assert.That(other.Document.CompositePixel(PaintChannel.Color, 100, 100), Is.EqualTo(new Rgba32(200, 30, 30, 255)), "the fill value shows");
        }

        [Test] public void PsdExportWritesPixelsAndTellsWhatTheFileDoesNotKeep()
        {
            ProjectedFill(out _, out _);
            dialogs.File = Temp(".psd"); window.Channel = PaintChannel.Color;
            window.ExportPsd();
            Assert.That(File.Exists(dialogs.File), Is.True, window.StatusMessage);
            Assert.That(dialogs.Asked, Has.Member("Inform: PSD exported with notes"));
            Assert.That(window.StatusMessage, Does.Contain("'Projected' (Color)").And.Contain("projection"));
            var read = PsdCodec.Read(File.ReadAllBytes(dialogs.File));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster));
            var layer = read.Document.Layers.Single(l => l.Name == "Projected");
            Assert.That(layer.IsFill, Is.False, "written as pixels");
        }

        [Test] public void AnImageThatCannotBeProjectedIsConfirmedBeforeExportingAndNotedWhenSaving()
        {
            var fill = ProjectedFill(out _, out _); var d = window.Document;
            d.SetFillProjection(fill.Id, fill.Projection.WithMode(FillProjectionMode.Triplanar)); // マップが無い
            dialogs.Folder = Temp(); Directory.CreateDirectory(dialogs.Folder);
            dialogs.ConfirmAnswer = false;
            window.ExportImages();
            Assert.That(dialogs.Asked, Is.EqualTo(new[] { "Confirm: Fill images not projected" }));
            Assert.That(Directory.GetFiles(dialogs.Folder), Is.Empty); Assert.That(window.StatusMessage, Does.Contain("cannot be projected"));
            dialogs.Asked.Clear(); dialogs.ConfirmAnswer = true;
            window.ExportImages();
            Assert.That(dialogs.Asked.First(), Is.EqualTo("Confirm: Fill images not projected"));
            Assert.That(Directory.GetFiles(dialogs.Folder), Is.Not.Empty, window.StatusMessage);
            dialogs.File = Temp(".ylp"); window.SaveProject(true);
            Assert.That(window.StatusMessage, Does.Contain("2 fill image channel(s) are not projected"));
        }
    }
}
