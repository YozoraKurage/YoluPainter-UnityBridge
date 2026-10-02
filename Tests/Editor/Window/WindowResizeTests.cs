using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// テクスチャセットの大きさの変更（プロジェクト設定）: 今のセットでもほかのセットでも、確かめてから新しい大きさの写しに入れ替え（そのセットの
    /// 履歴だけが消え、ほかのセットの履歴は残る）、断る・予算を超えると何も変えない、3D の表示・合成器・ほかのセットの表示が新しい大きさに
    /// なる、保存して開くと新しい大きさで戻る、焼いたメッシュマップは古いと出る、3D のパスは今のモデルで描き直す（モデルが無ければ知らせる）、
    /// 足すセットの大きさ、設定の画面の描画（英日）と大きさの検め。ウィンドウは表示せずに作る（OnEnable は走る）ので batch でも動く。
    /// </summary>
    public sealed class WindowResizeTests
    {
        string folder, project; TexturePaintWindow window;
        readonly List<TexturePaintWindow> others = new List<TexturePaintWindow>();
        readonly List<string> temporary = new List<string>();

        sealed class Dialogs : IPainterDialogs
        {
            public string File = "";
            public bool ConfirmAnswer = true;
            public readonly List<string> Asked = new List<string>();
            public string LastMessage;
            public string SaveFolder(string title, string folder, string defaultName) { Asked.Add("SaveFolder"); return ""; }
            public string OpenFolder(string title, string folder) { Asked.Add("OpenFolder"); return ""; }
            public string OpenFile(string title, string folder, string extension) { Asked.Add("OpenFile"); return File; }
            public string SaveFile(string title, string folder, string defaultName, string extension) { Asked.Add("SaveFile"); return File; }
            public bool Confirm(string title, string message, string ok, string cancel) { Asked.Add("Confirm: " + title); LastMessage = message; return ConfirmAnswer; }
            public void Inform(string title, string message) { Asked.Add("Inform: " + title); }
            public void Progress(string title, string info, float progress) { }
            public void ClearProgress() { }
        }

        [SetUp] public void Create()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            project = Temp(); Directory.CreateDirectory(project);
            PainterSettings.ProjectRoot = project;
            string name = "WindowResizeTests-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", name), Is.Not.Empty); folder = "Assets/" + name;
            window = NewWindow();
        }

        [TearDown] public void Clean()
        {
            L.OverrideLanguage(PainterLanguage.English);
            foreach (var w in others.Concat(new[] { window }))
            {
                if (w == null) continue;
                string recovery = w.RecoveryRoot;
                Object.DestroyImmediate(w);
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
            }
            others.Clear(); window = null;
            AssetDatabase.DeleteAsset(folder); PainterSettings.ProjectRoot = null;
            foreach (var path in temporary) { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); }
            temporary.Clear();
        }

        string Temp(string extension = "") { string path = Path.Combine(Path.GetTempPath(), "yolupainter-resize-" + Guid.NewGuid().ToString("N") + extension); temporary.Add(path); return path; }

        TexturePaintWindow NewWindow()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            w.Dialogs = new Dialogs(); w.MeshBakeProgress = (title, info, progress) => false;
            if (window != null) others.Add(w);
            return w;
        }
        static Dialogs DialogsOf(TexturePaintWindow w) => (Dialogs)w.Dialogs;

        IReadOnlyList<TexturePaintWindow.TextureSet> NewProject(GameObject model, int resolution = 512, ProjectTemplate template = ProjectTemplate.ColorOnly)
        {
            window.CreateProject(new NewProjectSettings { Model = model, Resolution = resolution, Template = template });
            return window.TextureSets.ToList();
        }

        /// <summary>今のプロジェクトのままの設定（モデル・セットの並び・大きさ）。sizes の名前のセットだけ大きさを変える。</summary>
        NewProjectSettings Configuration(GameObject model, params (string name, int size)[] sizes)
        {
            return new NewProjectSettings
            {
                Model = model, Resolution = 512, NormalFormat = window.Document.NormalSettings.FileDirection,
                Sets = window.TextureSets.Select(s =>
                {
                    int size = sizes.Where(z => z.name == s.Name).Select(z => z.size).DefaultIfEmpty(0).First();
                    return new TextureSetDraft { Id = s.Id, Name = s.Name, Slot = s.MaterialSlot, CurrentWidth = s.Document.Width, CurrentHeight = s.Document.Height,
                        Width = size > 0 ? size : s.Document.Width, Height = size > 0 ? size : s.Document.Height };
                }).ToList(),
            };
        }

        /// <summary>層の Color を、画素ごとに違う不透明な色で置き換える（1 回の Undo）。</summary>
        static void Pattern(PaintDocument d)
        {
            var rgba = new byte[d.Width * d.Height * 4];
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++) { int i = (y * d.Width + x) * 4; rgba[i] = (byte)(x / 2); rgba[i + 1] = (byte)(y / 2); rgba[i + 2] = (byte)((x ^ y) & 255); rgba[i + 3] = 255; }
            d.ReplacePixels(d.Layers[0].Id, PaintChannel.Color, rgba, withinSelection: false);
        }
        static void Flood(PaintDocument d, Rgba32 color)
        {
            var rgba = new byte[d.Width * d.Height * 4];
            for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = color.R; rgba[i + 1] = color.G; rgba[i + 2] = color.B; rgba[i + 3] = color.A; }
            d.ReplacePixels(d.Layers[0].Id, PaintChannel.Color, rgba, withinSelection: false);
        }
        static byte[] Raw(Texture texture)
        {
            if (texture is Texture2D t) return t.GetRawTextureData<byte>().ToArray();
            var rt = (RenderTexture)texture; var active = RenderTexture.active;
            var read = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            try { RenderTexture.active = rt; read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); read.Apply(); return read.GetRawTextureData<byte>().ToArray(); }
            finally { RenderTexture.active = active; Object.DestroyImmediate(read); }
        }

        // ───────── 変える ─────────

        [Test] public void ResizingAnotherSetAsksResamplesItAndClearsOnlyItsHistory()
        {
            var model = TextureSetModels.Prefab(folder, "Pair", "Body", "Hair");
            var sets = NewProject(model); var a = sets[0]; var b = sets[1];
            Flood(a.Document, new Rgba32(9, 8, 7));
            window.SwitchTextureSet(b.Id); Pattern(b.Document); window.SwitchTextureSet(a.Id);
            var oldB = b.Document; var layerIds = oldB.Layers.Select(l => l.Id).ToList(); var dialogs = DialogsOf(window);

            var settings = Configuration(model, ("Hair", 1024)); settings.Resampling = CanvasResampling.Nearest;
            window.ApplyProjectConfiguration(settings);
            Assert.That(dialogs.Asked, Is.EqualTo(new[] { "Confirm: Resize texture sets?" }));
            Assert.That(dialogs.LastMessage, Does.Contain("Hair: 512 × 512 → 1024 × 1024 (Nearest)").And.Contain("cannot be undone"));
            Assert.That(window.TextureSets, Is.EqualTo(sets), "the same texture sets"); Assert.That(window.CurrentTextureSet, Is.SameAs(a), "the current set stays");
            Assert.That(b.Document, Is.Not.SameAs(oldB));
            Assert.That((b.Document.Width, b.Document.Height, b.Document.Id), Is.EqualTo((1024, 1024, b.Id)));
            Assert.That(b.Document.Layers.Select(l => l.Id), Is.EqualTo(layerIds));
            for (int y = 0; y < 1024; y += 37) for (int x = 0; x < 1024; x += 41)
                Assert.That(b.Document.Layers[0].GetPixel(PaintChannel.Color, x, y), Is.EqualTo(oldB.Layers[0].GetPixel(PaintChannel.Color, x / 2, y / 2)), x + "," + y);
            Assert.That(b.Document.CanUndo, Is.False, "the resized set's history is gone");
            Assert.That(a.Document.CanUndo, Is.True, "the other set's history stays"); Assert.That(window.Document, Is.SameAs(a.Document));
            Assert.That(window.IsSaved, Is.False);
            Assert.That(window.StatusMessage, Does.Contain("Hair is now 1024 × 1024"));
            Assert.That(a.Document.Undo(), Is.True); Assert.That(a.Document.Layers[0].GetPixel(PaintChannel.Color, 0, 0), Is.EqualTo(Rgba32.Transparent), "and still undoes");
        }

        [Test] public void ResizingTheCurrentSetReplacesTheDocumentAndEveryDisplayFollows()
        {
            var model = TextureSetModels.Prefab(folder, "Twin", "Body", "Hair");
            var sets = NewProject(model); var a = sets[0]; var b = sets[1];
            var red = new Rgba32(200, 10, 10); var green = new Rgba32(10, 200, 10);
            Flood(a.Document, red); window.SwitchTextureSet(b.Id); Flood(b.Document, green); window.SwitchTextureSet(a.Id);
            var layer = window.SelectedLayer;
            window.RefreshPreviewTextures();
            Assert.That(window.Compositor.Texture.width, Is.EqualTo(512));

            window.ApplyProjectConfiguration(Configuration(model, ("Body", 1024), ("Hair", 2048)));
            Assert.That(DialogsOf(window).LastMessage, Does.Contain("Body: 512 × 512 → 1024 × 1024 (Bilinear)").And.Contain("Hair: 512 × 512 → 2048 × 2048 (Bilinear)"), "automatic: bilinear to enlarge");
            Assert.That(window.Document, Is.SameAs(a.Document)); Assert.That((window.Document.Width, window.Document.Height), Is.EqualTo((1024, 1024)));
            Assert.That(window.SelectedLayer, Is.EqualTo(layer), "the same layer stays selected");
            Assert.That(window.Document.CanUndo, Is.False); Assert.That(window.Document.Undo(), Is.False, "the resize is not an undo step");
            window.RefreshPreviewTextures();
            var preview = window.Preview;
            Assert.That(window.Compositor.Texture.width, Is.EqualTo(1024), "the compositor is rebuilt at the new size");
            Assert.That(preview.ShownTexture(0), Is.SameAs(window.DisplayTexture));
            Assert.That(Raw(window.DisplayTexture), Is.EqualTo(a.Document.Composite(PaintChannel.Color)));
            Assert.That(preview.ShownTexture(1), Is.SameAs(window.SetDisplay(b))); Assert.That(preview.ShownTexture(1).width, Is.EqualTo(2048));
            Assert.That(Raw(preview.ShownTexture(1)), Is.EqualTo(b.Document.Composite(PaintChannel.Color)), "the other set's display is made again");
            var thumbnail = window.SetThumbnail(b).GetPixels32();
            Assert.That(thumbnail[16 * 32 + 16], Is.EqualTo(new Color32(10, 200, 10, 255)));

            // 縮めると自動は面積平均
            window.ApplyProjectConfiguration(Configuration(model, ("Body", 512)));
            Assert.That(DialogsOf(window).LastMessage, Does.Contain("1024 × 1024 → 512 × 512 (Area average)"));
            Assert.That(window.Document.Width, Is.EqualTo(512)); Assert.That(window.Document.Layers[0].GetPixel(PaintChannel.Color, 100, 100), Is.EqualTo(red));
        }

        [Test] public void DecliningOrGoingOverTheBudgetChangesNothing()
        {
            PainterSettings.UpdatePersonal(p => p.sourceBudgetMiB = 16);
            var model = TextureSetModels.Prefab(folder, "Tight", "Body", "Hair");
            var sets = NewProject(model); var a = sets[0];
            Pattern(a.Document);
            var document = a.Document; long revision = document.Revision; var bytes = DocumentBinary.Write(document); var dialogs = DialogsOf(window);

            dialogs.ConfirmAnswer = false;
            var settings = Configuration(model, ("Body", 1024)); settings.Sets[1].Name = "Renamed";
            window.ApplyProjectConfiguration(settings);
            Assert.That(dialogs.Asked, Is.EqualTo(new[] { "Confirm: Resize texture sets?" }));
            Assert.That(window.StatusMessage, Does.Contain("nothing changed"));
            Assert.That(a.Document, Is.SameAs(document)); Assert.That(document.Revision, Is.EqualTo(revision)); Assert.That(document.CanUndo, Is.True);
            Assert.That(sets[1].Name, Is.EqualTo("Hair"), "declining the resize also keeps the rest of the configuration");

            // 4096² の不透明な画素は 64 MiB。予算の 16 MiB を超えるので、作る途中で断り、名前の変更も含めて何も変えない
            dialogs.ConfirmAnswer = true;
            settings = Configuration(model, ("Body", 4096)); settings.Sets[1].Name = "Renamed";
            Assert.That(() => window.ApplyProjectConfiguration(settings), Throws.InvalidOperationException.With.Message.Contains("budget").And.Message.Contains("Nothing was changed"));
            Assert.That(a.Document, Is.SameAs(document)); Assert.That(DocumentBinary.Write(document), Is.EqualTo(bytes)); Assert.That(document.CanUndo, Is.True);
            Assert.That(window.Document, Is.SameAs(document)); Assert.That(sets[1].Name, Is.EqualTo("Hair"));

            Assert.That(() => window.ApplyProjectConfiguration(Configuration(model, ("Body", 1000))), Throws.ArgumentException, "only the new-project sizes");
            Assert.That(a.Document, Is.SameAs(document));
        }

        // ───────── 保存・メッシュマップ・パス・足す ─────────

        [Test] public void AResizedProjectSavesAndOpensAtItsNewSize()
        {
            var model = TextureSetModels.Prefab(folder, "Saved", "Body", "Hair");
            var sets = NewProject(model); var a = sets[0];
            Pattern(a.Document); a.Document.SetSelection(SelectionMask.Ellipse(a.Document, 200, 150, 90, 40));
            window.ApplyProjectConfiguration(Configuration(model, ("Body", 1024)));
            Assert.That(a.Document.Selection, Is.Not.Null); Assert.That(a.Document.Selection.Width, Is.EqualTo(1024), "the selection is resized with the set");
            var dialogs = DialogsOf(window); dialogs.File = Path.Combine(Temp(), "Saved.ylp"); Directory.CreateDirectory(Path.GetDirectoryName(dialogs.File));
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);

            var other = NewWindow(); DialogsOf(other).File = dialogs.File;
            other.OpenProject();
            Assert.That(other.TextureSets.Select(s => (s.Id, s.Document.Width, s.Document.Height)), Is.EqualTo(new[] { (a.Id, 1024, 1024), (sets[1].Id, 512, 512) }), other.StatusMessage);
            Assert.That(DocumentBinary.Write(other.TextureSets[0].Document), Is.EqualTo(DocumentBinary.Write(a.Document)));
            Assert.That(SelectionBinary.Write(other.TextureSets[0].Document.Selection), Is.EqualTo(SelectionBinary.Write(a.Document.Selection)));
            Assert.That(other.IsSaved, Is.True);
        }

        [Test] public void BakedMeshMapsOfAResizedSetAreStale()
        {
            var model = TextureSetModels.Prefab(folder, "Baked", "Body", "Hair");
            var sets = NewProject(model);
            window.MeshBakeUseGpu = false; window.MeshBakeSettings.Maps = new[] { MeshMapKind.Position };
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), window.StatusMessage);
            Assert.That(window.MeshMaps.Check(MeshMapKind.Position, window.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current));
            window.ApplyProjectConfiguration(Configuration(model, ("Body", 1024)));
            var check = window.MeshMaps.Check(MeshMapKind.Position, window.CurrentMeshMapExpectation());
            Assert.That(check.State, Is.EqualTo(MeshMapState.Stale));
            Assert.That(string.Join(" ", check.Reasons), Does.Contain("baked at 512×512, the document is 1024×1024"));
            Assert.That(sets[0].MeshMaps.Count, Is.EqualTo(1), "the map is kept (and not used) until it is baked again");
            window.SwitchTextureSet(sets[1].Id);
            Assert.That(window.MeshMaps.Check(MeshMapKind.Position, window.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current), "the other set's maps stay current");
        }

        [Test] public void APathOnTheModelIsDrawnAgainAtTheNewSizeOrReportedWithoutTheModel()
        {
            var model = TextureSetModels.Prefab(folder, "Path", "Body");
            NewProject(model);
            var geometry = window.Preview.Geometry; Assert.That(geometry, Is.Not.Null);
            var d = window.Document; var layer = d.Layers[0];
            var path = new SurfacePath(Guid.NewGuid(), PaintChannel.Color, SurfacePathRenderer.Fingerprint(geometry), new PathBrush { RadiusWorld = .05, Color = new Rgba32(250, 40, 10) },
                new[] { new PathPoint(0, .1, .1), new PathPoint(0, .6, .2), new PathPoint(1, .3, .5) });
            d.SetPath(layer.Id, path, SurfacePathRenderer.Render(d, geometry, path).Surface);
            Assert.That(layer.GetChannel(PaintChannel.Color).TileCount, Is.GreaterThan(0));

            window.ApplyProjectConfiguration(Configuration(model, ("Body", 1024)));
            var resized = window.Document; Assert.That(resized.Width, Is.EqualTo(1024));
            Assert.That(resized.Layers[0].Path, Is.SameAs(path));
            var expected = SurfacePathRenderer.Render(resized, geometry, path).Surface;
            Assert.That(Tiles(resized.Layers[0].Channels[PaintChannel.Color]), Is.EqualTo(Tiles(expected)), "drawn again from the path at 1024, not resampled");
            Assert.That(resized.CanUndo, Is.False, "the redraw is part of the resize, not an undo step");

            // モデルを外す設定と一緒に変えると、描き直せないので再標本化した画素のまま知らせる
            var settings = Configuration(null, ("Body", 2048));
            window.ApplyProjectConfiguration(settings);
            Assert.That(window.Document.Width, Is.EqualTo(2048)); Assert.That(window.Document.Layers[0].Path, Is.SameAs(path));
            Assert.That(window.StatusMessage, Does.Contain("resampled, not redrawn"));
            Assert.That(window.Document.Layers[0].GetChannel(PaintChannel.Color).TileCount, Is.GreaterThan(0));
        }
        static byte[] Tiles(SparseTileSurface s) => s.EnumerateTiles().SelectMany(t => BitConverter.GetBytes(t.Coord.X).Concat(BitConverter.GetBytes(t.Coord.Y)).Concat(t.Bytes)).ToArray();

        [Test] public void AnAddedSetTakesTheSizeOfItsDraft()
        {
            var model = TextureSetModels.Prefab(folder, "Grow", "Body", "Hair");
            window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Template = ProjectTemplate.ColorOnly, Slots = new[] { 0 } });
            var settings = Configuration(model);
            settings.Sets.Add(new TextureSetDraft { Name = "Hair", Slot = 1, Width = 2048, Height = 2048 });
            settings.Sets.Add(new TextureSetDraft { Name = "Extra", Slot = 2 });
            window.ApplyProjectConfiguration(settings);
            Assert.That(DialogsOf(window).Asked, Is.Empty, "adding a set is not a resize");
            Assert.That(window.TextureSets.Select(s => (s.Name, s.Document.Width)), Is.EqualTo(new[] { ("Body", 512), ("Hair", 2048), ("Extra", 512) }), "no size: like the open set");
            Assert.That(window.TextureSets[1].Document.CanUndo, Is.False);
        }

        // ───────── 設定の画面 ─────────

        /// <summary>メニューから開いた設定の画面で大きさを選び、適用を押す（窓を開くので GUI モード）。断られた予算は知らせに出て何も変えない。</summary>
        [Test] public void TheConfigurationDialogResizesThroughItsApplyButton()
        {
            if (Application.isBatchMode) Assert.Ignore("The Project Configuration window opens as a utility window; run in GUI mode (switch-daemon.sh gui).");
            var model = TextureSetModels.Prefab(folder, "Dialog", "Body", "Hair");
            var sets = NewProject(model); Pattern(sets[0].Document);
            NewProjectWindow Opened()
            {
                foreach (var w in Resources.FindObjectsOfTypeAll<NewProjectWindow>()) w.Close();
                window.ProjectConfigurationDialog();
                return Resources.FindObjectsOfTypeAll<NewProjectWindow>().Single();
            }
            var dialog = Opened();
            try
            {
                Assert.That(dialog.Configure, Is.True);
                Assert.That(dialog.Settings.Sets.Select(d => (d.Name, d.Width, d.Height, d.CurrentWidth, d.CurrentHeight)), Is.EqualTo(new[] { ("Body", 512, 512, 512, 512), ("Hair", 512, 512, 512, 512) }));
                dialog.Settings.Sets[1].Width = dialog.Settings.Sets[1].Height = 1024;
                dialog.Accept();
                Assert.That(sets[1].Document.Width, Is.EqualTo(1024), window.StatusMessage);
                Assert.That(DialogsOf(window).Asked, Is.EqualTo(new[] { "Confirm: Resize texture sets?" }));

                PainterSettings.UpdatePersonal(p => p.sourceBudgetMiB = 16);
                var document = sets[0].Document;
                dialog = Opened();
                dialog.Settings.Sets[0].Width = dialog.Settings.Sets[0].Height = 4096;
                dialog.Accept();
                Assert.That(window.StatusMessage, Does.Contain("budget"), "the refusal is shown in the status bar");
                Assert.That(sets[0].Document, Is.SameAs(document));
            }
            finally { foreach (var w in Resources.FindObjectsOfTypeAll<NewProjectWindow>()) w.Close(); }
        }

        [Test] public void TheConfigurationDialogOffersASizePerSetAndChecksIt()
        {
            var dialog = ScriptableObject.CreateInstance<NewProjectWindow>();
            try
            {
                dialog.Configure = true;
                dialog.Settings = new NewProjectSettings
                {
                    Resolution = 2048,
                    Sets = new List<TextureSetDraft>
                    {
                        new TextureSetDraft { Id = Guid.NewGuid(), Name = "Body", Slot = 0, Width = 4096, Height = 4096, CurrentWidth = 2048, CurrentHeight = 2048 },
                        new TextureSetDraft { Id = Guid.NewGuid(), Name = "Imported", Slot = 1, Width = 1000, Height = 600, CurrentWidth = 1000, CurrentHeight = 600 },
                        new TextureSetDraft { Name = "Eyes", Slot = 2, Width = 512, Height = 512 },
                    },
                };
                if (Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        string path = Path.Combine(Path.GetFullPath("Logs"), "YoluPainterSnapshots", "resize-configure-" + language + ".png");
                        OffscreenGui.RenderToPng((int)NewProjectWindow.Width, (int)NewProjectWindow.Height, () => dialog.DrawContent(new Rect(0, 0, NewProjectWindow.Width, NewProjectWindow.Height)), path, PaintTheme.PanelBg);
                        Assert.That(File.Exists(path), Is.True);
                    }
                L.OverrideLanguage(PainterLanguage.English);
                Assert.That(dialog.Settings.Sets[0].Resizes, Is.True); Assert.That(dialog.Settings.Sets[1].Resizes, Is.False); Assert.That(dialog.Settings.Sets[2].Resizes, Is.False, "a new set is made, not resized");
                var taken = dialog.TakeSettings();
                Assert.That(taken.Sets.Select(s => (s.Width, s.Height)), Is.EqualTo(new[] { (4096, 4096), (1000, 600), (512, 512) }), "an unusual size may stay as it is");
                dialog.Settings.Sets[1].Width = 800;
                Assert.That(() => dialog.TakeSettings(), Throws.ArgumentException.With.Message.Contains("size"));
                dialog.Settings.Sets[1].Width = 1000; dialog.Settings.Resampling = (CanvasResampling)9;
                Assert.That(() => dialog.TakeSettings(), Throws.TypeOf<ArgumentOutOfRangeException>());
            }
            finally { L.OverrideLanguage(PainterLanguage.English); Object.DestroyImmediate(dialog); }
        }
    }
}
