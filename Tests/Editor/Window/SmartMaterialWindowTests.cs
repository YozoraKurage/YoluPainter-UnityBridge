using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// スマートマテリアルとスマートマスクをウィンドウの側から（表示しないウィンドウ。batch でも GUI でも回る）: 選んだ層・マスクを保存すると
    /// このプロジェクトと自分の置き場の両方に入る、置くのは 1 回の Undo、.ylp（形式 5）の保存と開く・復旧の checkpoint、自分の置き場の一覧（壊れた
    /// ファイルも理由を付けて並べ、置こうとすると断る）、内蔵を取り込む（2 回目は同じもの）・消すときの確かめ、セットに無いチャンネルの知らせ、
    /// 予算で断ると何も変えない（そのために入れた画像も戻す）、スマートマスクは層のマスクに置き、すべてのロックでは断る。
    /// </summary>
    public sealed class SmartMaterialWindowTests
    {
        string project; TexturePaintWindow window; Dialogs dialogs;
        readonly List<string> temporary = new List<string>();
        readonly List<TexturePaintWindow> others = new List<TexturePaintWindow>();

        sealed class Dialogs : IPainterDialogs
        {
            public string File = ""; public bool ConfirmAnswer = true;
            public readonly List<string> Asked = new List<string>();
            public string SaveFolder(string title, string folder, string defaultName) { Asked.Add("SaveFolder"); return ""; }
            public string OpenFolder(string title, string folder) { Asked.Add("OpenFolder"); return ""; }
            public string OpenFile(string title, string folder, string extension) { Asked.Add("OpenFile"); return File; }
            public string SaveFile(string title, string folder, string defaultName, string extension) { Asked.Add("SaveFile"); return File; }
            public bool Confirm(string title, string message, string ok, string cancel) { Asked.Add("Confirm: " + title); return ConfirmAnswer; }
            public void Inform(string title, string message) { Asked.Add("Inform: " + title); }
            public void Progress(string title, string info, float progress) { }
            public void ClearProgress() { }
        }

        [SetUp] public void Create()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            project = Temp(); Directory.CreateDirectory(project);
            PainterSettings.ProjectRoot = project; // 自分の置き場と設定は一時のプロジェクトに
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

        string Temp(string extension = "") { string path = Path.Combine(Path.GetTempPath(), "yolupainter-smart-" + Guid.NewGuid().ToString("N") + extension); temporary.Add(path); return path; }
        TexturePaintWindow NewWindow()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            var own = new Dialogs(); w.Dialogs = own;
            if (window != null) others.Add(w); else dialogs = own;
            return w;
        }
        static string Key(SmartResource r) => "p:" + r.Id.ToString("D");

        /// <summary>塗りつぶし（値 3 つ・マスクと Generator）と画素の層を、グループにまとめたもの。グループを選ぶ。</summary>
        PaintLayer Rust()
        {
            var d = window.Document;
            var fill = d.AddFillLayer("Rust", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(120, 60, 30, 255) }, { PaintChannel.Roughness, new Rgba32(220, 220, 220, 255) }, { PaintChannel.Metallic, new Rgba32(0, 0, 0, 255) } });
            d.AddLayerMask(fill.Id); d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Dirt)));
            var spots = d.AddLayer("Spots"); d.Fill(spots.Id, PaintChannel.Color, new Rgba32(30, 20, 10, 255), 1, SelectionMask.Rectangle(d, 10, 10, 60, 40));
            var group = d.GroupLayers(new[] { fill.Id, spots.Id }, "Rusty");
            window.SelectedLayer = group.Id;
            return group;
        }

        [Test] public void SavingPutsItIntoTheProjectAndTheLibraryAndPlacingIsOneUndoStep()
        {
            var group = Rust(); var d = window.Document; int undoBefore = d.UndoCount; long revisionBefore = d.Revision;
            var saved = window.SaveSmartMaterial();
            Assert.That(saved, Is.Not.Null, window.StatusMessage);
            Assert.That(saved.Name, Is.EqualTo("Rusty")); Assert.That(saved.Material.LayerCount, Is.EqualTo(3));
            Assert.That(window.StatusMessage, Does.Contain("in this project and in My Library"));
            var files = Directory.GetFiles(PainterSettings.LibraryFolder, "*" + SmartMaterialFile.Extension);
            Assert.That(files.Select(Path.GetFileName), Is.EqualTo(new[] { "Rusty.ylsmart" }));
            Assert.That(File.ReadAllBytes(files[0]), Is.EqualTo(saved.FileBytes()), "the same file in both places");
            Assert.That(saved.Origin.Kind, Is.EqualTo(ResourceOriginKind.Library)); Assert.That(saved.Origin.Path, Is.EqualTo("Rusty.ylsmart"));
            Assert.That(SmartMaterialFile.ReadInfo(saved.FileBytes()).Thumbnail, Is.Not.Null, "the file carries its thumbnail");
            Assert.That(window.SelectedAsset, Is.EqualTo(Key(saved)));
            Assert.That(window.IsSaved, Is.False, "saving a smart material changes the project");
            Assert.That((d.UndoCount, d.Revision), Is.EqualTo((undoBefore, revisionBefore)), "saving does not change the document");
            int layers = d.Layers.Count, undo = d.UndoCount;
            window.SelectedLayer = d.Layers[0].Id;
            var r = window.PlaceSmartAsset(Key(saved));
            Assert.That(r, Is.Not.Null, window.StatusMessage);
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 3)); Assert.That(d.UndoCount, Is.EqualTo(undo + 1));
            Assert.That(window.SelectedLayer, Is.EqualTo(r.LayerId)); Assert.That(d.GetLayer(r.LayerId).Name, Is.EqualTo("Rusty"));
            Assert.That(d.GetLayer(r.LayerId).ParentId, Is.EqualTo(Guid.Empty));
            Assert.That(d.Layers.ToList().IndexOf(d.GetLayer(r.LayerId)), Is.EqualTo(3), "above the selected (bottom) layer");
            Assert.That(d.Undo(), Is.True); Assert.That(d.Layers.Count, Is.EqualTo(layers));
            Assert.That(d.Redo(), Is.True); Assert.That(d.Layers.Count, Is.EqualTo(layers + 3));
            // もう一度保存すると、別のもの（置き場のファイルは名前に番号。前のファイルは上書きしない）
            window.SelectedLayer = group.Id;
            var again = window.SaveSmartMaterial();
            Assert.That(again, Is.Not.SameAs(saved)); Assert.That(window.ImageResources.Smart.Count, Is.EqualTo(2));
            Assert.That(Directory.GetFiles(PainterSettings.LibraryFolder, "*" + SmartMaterialFile.Extension).Select(Path.GetFileName).OrderBy(n => n), Is.EqualTo(new[] { "Rusty 2.ylsmart", "Rusty.ylsmart" }));
            Assert.That(File.ReadAllBytes(files[0]), Is.EqualTo(saved.FileBytes()), "the first file is kept");
        }

        [Test] public void SmartMaterialsSaveOpenAndRecoverWithTheProject()
        {
            Rust(); var material = window.SaveSmartMaterial();
            window.SelectedLayer = window.Document.Layers.First(l => l.Name == "Rust").Id;
            var mask = window.SaveSmartMask();
            Assert.That(mask.Kind, Is.EqualTo(SmartKind.Mask)); Assert.That(mask.Name, Is.EqualTo("Rust mask"));
            var builtIn = window.ImportSmartAsset("b:dirty-paint");
            dialogs.File = Temp(".ylp"); window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var saved = YlpFormat.Open(YlpStore.Load(dialogs.File).Files);
            Assert.That(saved.Info.Format, Is.EqualTo(5));
            Assert.That(saved.Resources.Select(e => e.Kind), Is.EqualTo(new[] { ResourceKind.SmartMaterial, ResourceKind.SmartMask, ResourceKind.SmartMaterial }));
            var other = NewWindow(); other.OpenProjectAt(dialogs.File);
            Assert.That(other.ImageResources.Smart.Select(s => (s.Id, s.Name, s.Hash, s.Kind, s.Origin.Kind)), Is.EqualTo(window.ImageResources.Smart.Select(s => (s.Id, s.Name, s.Hash, s.Kind, s.Origin.Kind))));
            Assert.That(other.IsSaved, Is.True, "opening is not an edit");
            // 開いた先で置ける（.ylp だけで、自分の置き場が無くても）
            foreach (var f in Directory.GetFiles(PainterSettings.LibraryFolder)) File.Delete(f);
            int before = other.Document.Layers.Count;
            Assert.That(other.PlaceSmartAsset(Key(other.ImageResources.Smart[0])), Is.Not.Null, other.StatusMessage);
            Assert.That(other.Document.Layers.Count, Is.EqualTo(before + 3));
            // 復旧の checkpoint（フォーカスを失ったとき）から別の窓が戻す
            window.GetType().GetMethod("OnLostFocus", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null); window.FlushRecovery();
            window.ImportSmartAsset("b:edges");
            window.GetType().GetMethod("OnLostFocus", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null); window.FlushRecovery();
            var restored = NewWindow(); var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            restored.GetType().GetMethod("OnDisable", flags).Invoke(restored, null);
            string own = restored.RecoveryRoot; if (Directory.Exists(own)) Directory.Delete(own, true);
            restored.GetType().GetField("recoveryRoot", flags).SetValue(restored, window.RecoveryRoot);
            restored.GetType().GetMethod("OnEnable", flags).Invoke(restored, null);
            Assert.That(restored.StatusMessage, Does.StartWith("Recovered"));
            Assert.That(restored.ImageResources.Smart.Select(s => s.Hash), Is.EqualTo(window.ImageResources.Smart.Select(s => s.Hash)));
            Assert.That(builtIn.Origin.BuiltInKey, Is.EqualTo("dirty-paint")); Assert.That(material, Is.Not.Null);
        }

        [Test] public void TheLibraryListsItsFilesAndABrokenOneIsShownButNotPlaced()
        {
            Rust(); window.SaveSmartMaterial();
            string folder = PainterSettings.LibraryFolder;
            File.WriteAllBytes(Path.Combine(folder, "Broken.ylsmart"), new byte[] { 1, 2, 3 });
            var newer = SmartMaterialFile.ReadArchive(File.ReadAllBytes(Path.Combine(folder, "Rusty.ylsmart")));
            newer[SmartMaterialFile.InfoName] = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(newer[SmartMaterialFile.InfoName]).Replace("\"format\": 1", "\"format\": 9"));
            File.WriteAllBytes(Path.Combine(folder, "Future.ylsmart"), SmartMaterialFile.WriteArchive(newer));
            var items = window.SmartLibraryItems();
            Assert.That(items.Select(i => i.FileName), Is.EqualTo(new[] { "Broken.ylsmart", "Future.ylsmart", "Rusty.ylsmart" }));
            Assert.That(items[0].Problem, Is.Not.Null); Assert.That(items[1].Problem, Does.Contain("format 9")); Assert.That(items[2].Info.Name, Is.EqualTo("Rusty"));
            int layers = window.Document.Layers.Count;
            Assert.That(window.PlaceSmartAsset("l:Future.ylsmart"), Is.Null);
            Assert.That(window.StatusMessage, Does.Contain("format 9")); Assert.That(window.Document.Layers.Count, Is.EqualTo(layers));
            Assert.That(window.PlaceSmartAsset("l:Rusty.ylsmart"), Is.Not.Null, window.StatusMessage);
            Assert.That(window.ImageResources.Smart.Count, Is.EqualTo(1), "placing from the library does not import it");
            // 取り込むと「この プロジェクトに入っている」印のもとになる出どころ
            var imported = window.ImportSmartAsset("l:Rusty.ylsmart");
            Assert.That(imported, Is.SameAs(window.ImageResources.Smart.Single()), "the same file is held once");
        }

        [Test] public void BuiltInsImportOnceAndRemovingAsksFirst()
        {
            var a = window.ImportSmartAsset("b:rusty-metal"); var b = window.ImportSmartAsset("b:rusty-metal");
            Assert.That(b, Is.SameAs(a)); Assert.That(window.ImageResources.Smart.Count, Is.EqualTo(1));
            Assert.That(a.Name, Is.EqualTo("Rusty metal")); Assert.That((a.Origin.BuiltInKey, a.Origin.BuiltInVersion), Is.EqualTo(("rusty-metal", 1)));
            dialogs.ConfirmAnswer = false;
            Assert.That(window.RemoveSmartResource(a.Id), Is.False); Assert.That(window.ImageResources.Smart.Count, Is.EqualTo(1));
            dialogs.ConfirmAnswer = true;
            window.SelectedLayer = window.Document.Layers[0].Id; window.PlaceSmartAsset(Key(a));
            int layers = window.Document.Layers.Count;
            Assert.That(window.RemoveSmartResource(a.Id), Is.True); Assert.That(window.ImageResources.Smart, Is.Empty);
            Assert.That(window.Document.Layers.Count, Is.EqualTo(layers), "placed layers stay");
            Assert.That(dialogs.Asked.Count(q => q.StartsWith("Confirm")), Is.EqualTo(2));
        }

        [Test] public void ChannelsTheSetDoesNotUseAreSwitchedOffAndSaid()
        {
            window.CreateProject(new NewProjectSettings { Model = null, Resolution = 512, Template = ProjectTemplate.LilToon });
            window.SelectedLayer = window.Document.Layers[0].Id;
            var r = window.PlaceSmartAsset("b:rusty-metal");
            Assert.That(r, Is.Not.Null, window.StatusMessage);
            Assert.That(r.SwitchedOff, Is.EqualTo(new[] { PaintChannel.Roughness, PaintChannel.Metallic }));
            Assert.That(window.StatusMessage, Does.Contain("does not use Roughness, Metallic"));
            Assert.That(YlpContent.UsedChannels(window.Document), Is.EqualTo(new[] { PaintChannel.Color, PaintChannel.Normal, PaintChannel.Emission }), "the set's channels do not grow");
            Assert.That(window.StatusMessage, Does.Contain("mesh maps are baked"), "no maps: the generators pass through and the window says so");
            Assert.That(window.Document.GetLayer(r.LayerId).Name, Is.EqualTo("Rusty metal"));
        }

        /// <summary>画像を読む塗りつぶしの層（取り込んだ内蔵の画像）。</summary>
        PaintLayer CheckerFill(TexturePaintWindow w)
        {
            var image = w.ImportBuiltInImage("uv-checker");
            var d = w.Document;
            var fill = d.AddFillLayer("Checker", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(128, 128, 128, 255) } });
            d.SetFillImage(fill.Id, PaintChannel.Color, image.Id);
            w.SelectedLayer = fill.Id;
            return fill;
        }

        [Test] public void AFillLayersImageTravelsWithItIntoAnotherProject()
        {
            var fill = CheckerFill(window); var image = window.ImageResources.Images.Single();
            var saved = window.SaveSmartMaterial();
            Assert.That(saved.Material.Images.Select(i => (i.Id, i.Content.Hash)), Is.EqualTo(new[] { (image.Id, image.ContentHash) }), "the .ylsmart holds the image");
            var expected = window.Document.Composite(PaintChannel.Color);
            // 別の窓の新しいプロジェクトへ、自分の置き場のファイルから置く: 画像がプロジェクトに入り、層はそれを読む
            var other = NewWindow(); other.CreateProject(new NewProjectSettings { Model = null, Resolution = 512, Template = ProjectTemplate.Pbr });
            other.SelectedLayer = other.Document.Layers[0].Id;
            var r = other.PlaceSmartAsset("l:Checker.ylsmart");
            Assert.That(r, Is.Not.Null, other.StatusMessage);
            var brought = other.ImageResources.Images.Single();
            Assert.That((brought.Id, brought.ContentHash), Is.EqualTo((image.Id, image.ContentHash)), "the image comes in under its own ID (free there)");
            Assert.That(other.Document.GetLayer(r.LayerId).FillImages[PaintChannel.Color], Is.EqualTo(brought.Id));
            Assert.That(other.Document.MissingFillImages(), Is.Empty);
            Assert.That(other.Document.Composite(PaintChannel.Color), Is.EqualTo(expected), "the same picture");
            Assert.That(other.Document.Undo(), Is.True);
            Assert.That(other.ImageResources.Images.Count, Is.EqualTo(1), "the image stays in the project (resources are not in the undo history)");
            // 同じ画素を別の ID で持つプロジェクト: 2 つ目を作らず、置いた層はそちらの ID を読む
            var third = NewWindow(); third.CreateProject(new NewProjectSettings { Model = null, Resolution = 512, Template = ProjectTemplate.Pbr });
            var mine = third.ImportBuiltInImage("uv-checker");
            Assert.That(mine.Id, Is.Not.EqualTo(image.Id));
            third.SelectedLayer = third.Document.Layers[0].Id;
            var r3 = third.PlaceSmartAsset("l:Checker.ylsmart");
            Assert.That(third.ImageResources.Images.Single().Id, Is.EqualTo(mine.Id));
            Assert.That(third.Document.GetLayer(r3.LayerId).FillImages[PaintChannel.Color], Is.EqualTo(mine.Id));
            Assert.That(third.Document.Composite(PaintChannel.Color), Is.EqualTo(expected));
            Assert.That(fill, Is.Not.Null);
        }

        [Test] public void OverTheBudgetNothingChangesAndImagesBroughtForItAreTakenBack()
        {
            // 画像を読む塗りつぶしの層と画素の層を 1 つのスマートマテリアルに保存する
            var fill = CheckerFill(window); var source = window.Document;
            var heavy = source.AddLayer("heavy"); source.Fill(heavy.Id, PaintChannel.Color, new Rgba32(1, 2, 3, 255));
            window.SelectLayers(new[] { heavy.Id, fill.Id }, heavy.Id);
            var saved = window.SaveSmartMaterial("Heavy");
            Assert.That(saved.Material.Images.Count, Is.EqualTo(1)); Assert.That(saved.Material.PixelBytes, Is.GreaterThan(0));
            // 画像を持たない新しいプロジェクトで、層の画素の予算の残りを 0 にして置く: 断られ、そのために入れた画像も戻る
            var other = NewWindow(); other.CreateProject(new NewProjectSettings { Model = null, Resolution = 512, Template = ProjectTemplate.Pbr });
            var d = other.Document; other.SelectedLayer = d.Layers[0].Id; d.ClearHistory();
            int layers = d.Layers.Count; long revision = d.Revision; long resourceRevision = other.ImageResources.Revision;
            d.SourceBudgetBytes = d.AllocatedBytes;
            Assert.That(other.PlaceSmartAsset("l:Heavy.ylsmart"), Is.Null);
            Assert.That(other.StatusMessage, Does.Contain("Nothing was changed"));
            Assert.That((d.Layers.Count, d.UndoCount, d.Revision), Is.EqualTo((layers, 0, revision)));
            Assert.That(other.ImageResources.Images, Is.Empty, "the image brought for it is taken back");
            Assert.That(other.ImageResources.Revision, Is.GreaterThan(resourceRevision), "(it was added, then removed)");
            // 予算があれば、画像はプロジェクトに入り、層が置かれる
            d.SourceBudgetBytes = long.MaxValue / 4;
            Assert.That(other.PlaceSmartAsset("l:Heavy.ylsmart"), Is.Not.Null, other.StatusMessage);
            Assert.That(other.ImageResources.Images.Single().ContentHash, Is.EqualTo(window.ImageResources.Images.Single().ContentHash));
        }

        [Test] public void ASmartMaskGoesOnTheLayersMaskAndLockAllRefusesIt()
        {
            var d = window.Document; var target = d.Layers[0]; window.SelectedLayer = target.Id;
            var r = window.PlaceSmartAsset("b:edges");
            Assert.That(r, Is.Not.Null, window.StatusMessage);
            Assert.That(d.GetLayer(target.Id).Mask, Is.Not.Null); Assert.That(window.EditMask, Is.True);
            Assert.That(d.GetLayer(target.Id).Mask.Filters.Single().Settings.Generator.Type, Is.EqualTo(GeneratorType.EdgeWear));
            Assert.That(window.StatusMessage, Does.Contain("as its mask"));
            var other = window.PlaceSmartAsset("b:cavities", null, target.Id);
            Assert.That(other.ReplacedMask, Is.True); Assert.That(window.StatusMessage, Does.Contain("replacing its mask"));
            Assert.That(d.Undo(), Is.True); Assert.That(d.GetLayer(target.Id).Mask.Filters.Single().Settings.Generator.Type, Is.EqualTo(GeneratorType.EdgeWear));
            d.SetLayerLocks(target.Id, LayerLocks.All); int undo = d.UndoCount;
            Assert.That(window.PlaceSmartAsset("b:cavities"), Is.Null);
            Assert.That(window.StatusMessage, Does.Contain("locked")); Assert.That(d.UndoCount, Is.EqualTo(undo));
            // マスクの無い層からは保存しない
            var bare = d.AddLayer("bare"); window.SelectedLayer = bare.Id;
            Assert.That(window.SaveSmartMask(), Is.Null); Assert.That(window.StatusMessage, Does.Contain("has no mask"));
        }

        [Test] public void EveryBuiltInNameAndDescriptionIsTranslated()
        {
            var catalog = PoCatalog.Load("ja");
            foreach (var e in BuiltInSmartMaterials.All)
                foreach (var text in new[] { e.Name, e.Description })
                    Assert.That(catalog.TryGetValue(text, out var ja) && !string.IsNullOrEmpty(ja), Is.True, e.Key + ": " + text);
            foreach (var kind in new[] { "Smart Materials", "Smart Masks" }) Assert.That(catalog.ContainsKey(kind), Is.True, kind);
        }
    }
}
