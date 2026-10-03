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
    /// モデルの差し替え（Substance Painter のメッシュの読み直し）: 確かめてから 1 回で入れ、断れば何も変えない。テクスチャセットはマテリアルに
    /// （識別子 → 名前）付け直し、描いた画素はそのまま（同じ UV なら見た目も同じ、違えば知らせる）。新しいモデルに無いマテリアルのセットは残して
    /// 知らせ、セットの無い新しいマテリアルには足すかを尋ねる。3D のパスは新しいメッシュに描き直すか、できなければ画素にする（ロックなら残す）。
    /// 焼いたメッシュマップは古くなる。保存して開く・同じアセットの読み直し・プロジェクト設定の画面の対応・大きさの変更と一緒には入れないこと。
    /// ウィンドウは表示せずに作るので batch でも動く。
    /// </summary>
    public sealed class ModelChangeTests
    {
        string folder, project; TexturePaintWindow window;
        readonly List<TexturePaintWindow> others = new List<TexturePaintWindow>();
        readonly List<string> temporary = new List<string>();
        static string Snapshots => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "model-change"));

        sealed class Dialogs : IPainterDialogs
        {
            public string File = "";
            public bool ConfirmAnswer = true;
            /// <summary>Confirm の答えの順（空なら ConfirmAnswer）。</summary>
            public readonly Queue<bool> Answers = new Queue<bool>();
            public readonly List<string> Asked = new List<string>();
            public readonly List<string> Messages = new List<string>();
            public string SaveFolder(string title, string folder, string defaultName) { Asked.Add("SaveFolder"); return ""; }
            public string OpenFolder(string title, string folder) { Asked.Add("OpenFolder"); return ""; }
            public string OpenFile(string title, string folder, string extension) { Asked.Add("OpenFile"); return File; }
            public string SaveFile(string title, string folder, string defaultName, string extension) { Asked.Add("SaveFile"); return File; }
            public bool Confirm(string title, string message, string ok, string cancel) { Asked.Add("Confirm: " + title); Messages.Add(message); return Answers.Count > 0 ? Answers.Dequeue() : ConfirmAnswer; }
            public void Inform(string title, string message) { Asked.Add("Inform: " + title); }
            public void Progress(string title, string info, float progress) { }
            public void ClearProgress() { }
        }

        [SetUp] public void Create()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            project = Temp(); Directory.CreateDirectory(project);
            PainterSettings.ProjectRoot = project;
            string name = "ModelChangeTests-" + Guid.NewGuid().ToString("N");
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

        string Temp(string extension = "") { string path = Path.Combine(Path.GetTempPath(), "yolupainter-modelchange-" + Guid.NewGuid().ToString("N") + extension); temporary.Add(path); return path; }

        TexturePaintWindow NewWindow()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            w.Dialogs = new Dialogs(); w.MeshBakeProgress = (title, info, progress) => false; w.MeshBakeUseGpu = false;
            if (window != null) others.Add(w);
            return w;
        }
        Dialogs DialogsOf(TexturePaintWindow w) => (Dialogs)w.Dialogs;

        IReadOnlyList<TexturePaintWindow.TextureSet> NewProject(GameObject model)
        {
            window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Template = ProjectTemplate.ColorOnly });
            return window.TextureSets.ToList();
        }

        /// <summary>層の Color を、画素ごとに違う不透明な色で置き換える（1 回の Undo）。</summary>
        static void Pattern(PaintDocument d, int seed)
        {
            var rgba = new byte[d.Width * d.Height * 4];
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++) { int i = (y * d.Width + x) * 4; rgba[i] = (byte)(x + seed); rgba[i + 1] = (byte)(y * 3); rgba[i + 2] = (byte)((x ^ y) + seed); rgba[i + 3] = 255; }
            d.ReplacePixels(d.Layers[0].Id, PaintChannel.Color, rgba, withinSelection: false);
        }

        static string Slots(TexturePaintWindow.TextureSet s) => string.Join(",", s.Slots);
        static byte[][] Natives(IEnumerable<TexturePaintWindow.TextureSet> sets) => sets.Select(s => DocumentBinary.Write(s.Document)).ToArray();

        // ───────── マテリアルとセット ─────────

        [Test] public void AModelWithTheSameMaterialsAndUvsKeepsEveryPixelAndItsLook()
        {
            var sets = NewProject(MaterialSetModels.Avatar(folder));
            for (int i = 0; i < sets.Count; i++) Pattern(sets[i].Document, i * 40);
            var before = Natives(sets); var undo = sets.Select(s => s.Document.UndoCount).ToArray();
            var copy = MaterialSetModels.Avatar(folder, "Copy");
            var dialogs = DialogsOf(window);
            window.SetModel(copy);
            Assert.That(dialogs.Asked, Is.EqualTo(new[] { "Confirm: Change the model?" }), "every material has a set: nothing to add");
            Assert.That(dialogs.Messages[0], Does.Contain("Avatar → Copy").And.Contain("Skin: material Skin, same UVs, so it looks the same").And.Contain("cannot be undone"));
            Assert.That(window.Model, Is.SameAs(copy)); Assert.That(window.TextureSets, Is.EqualTo(sets));
            Assert.That(sets.Select(Slots), Is.EqualTo(new[] { "0,2", "1", "3,4", "5,6" }));
            Assert.That(sets[0].Material.AssetGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(folder + "/Copy-Skin/Skin.mat")), "the set now points at the new model's material");
            Assert.That(Natives(sets), Is.EqualTo(before), "not one pixel changes");
            Assert.That(sets.Select(s => s.Document.UndoCount), Is.EqualTo(undo), "the history is kept and nothing was added to it");
            Assert.That(window.IsSaved, Is.False, "changing the model is an unsaved change");
            window.RefreshPreviewTextures();
            Assert.That(window.Preview.ShownTexture(0), Is.SameAs(window.DisplayTexture)); Assert.That(window.Preview.ShownTexture(2), Is.SameAs(window.DisplayTexture));
            Assert.That(window.StatusMessage, Does.StartWith("Changed the model to Copy."));
        }

        [Test] public void DecliningChangesNothing()
        {
            var model = MaterialSetModels.Avatar(folder);
            var sets = NewProject(model); Pattern(sets[0].Document, 3);
            var dialogs = DialogsOf(window); dialogs.File = Path.Combine(Temp(), "Keep.ylp"); Directory.CreateDirectory(Path.GetDirectoryName(dialogs.File));
            window.SaveProject(true); Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var geometry = window.Preview.Geometry; var keys = sets.Select(s => s.Material).ToList(); var before = Natives(sets);
            dialogs.ConfirmAnswer = false;
            window.SetModel(MaterialSetModels.Avatar(folder, "Other", bodyOffset: new Vector3(0, 0, 5)));
            Assert.That(window.Model, Is.SameAs(model)); Assert.That(window.Preview.Geometry, Is.SameAs(geometry), "the loaded snapshot is not even reloaded");
            Assert.That(sets.Select(s => s.Material), Is.EqualTo(keys)); Assert.That(sets.Select(Slots), Is.EqualTo(new[] { "0,2", "1", "3,4", "5,6" }));
            Assert.That(Natives(sets), Is.EqualTo(before)); Assert.That(window.IsSaved, Is.True);
            Assert.That(window.StatusMessage, Does.Contain("nothing changed"));
        }

        [Test] public void MaterialsGoneAndNewAreReportedAndNewOnesAreAskedFor([Values(false, true)] bool add)
        {
            var sets = NewProject(MaterialSetModels.Avatar(folder));
            for (int i = 0; i < sets.Count; i++) Pattern(sets[i].Document, i);
            var before = Natives(sets);
            var skin = MaterialSetModels.MaterialAsset(folder, "New", "Skin"); var hair = MaterialSetModels.MaterialAsset(folder, "New", "Hair"); var eyes = MaterialSetModels.MaterialAsset(folder, "New", "Eyes");
            Rect Low = new Rect(.05f, .05f, .4f, .4f);
            var next = MaterialSetModels.Prefab(folder, "New",
                new MaterialSetModels.Part { Name = "Body", Materials = new[] { skin }, Uvs = new[] { Low } },
                new MaterialSetModels.Part { Name = "Hair", Materials = new[] { hair }, Uvs = new[] { Low } },
                new MaterialSetModels.Part { Name = "Eyes", Materials = new[] { eyes }, Uvs = new[] { Low } });
            var dialogs = DialogsOf(window); dialogs.Answers.Enqueue(true); dialogs.Answers.Enqueue(add);
            window.SetModel(next);
            Assert.That(dialogs.Asked, Is.EqualTo(new[] { "Confirm: Change the model?", "Confirm: Add texture sets?" }));
            Assert.That(dialogs.Messages[0], Does.Contain("Cloth: not in the new model").And.Contain("Unassigned: not in the new model").And.Contain("Materials of the new model without a texture set: Eyes"));
            Assert.That(dialogs.Messages[0], Does.Contain("Skin: material Skin, the UVs differ"), "the Head's half of the skin is gone");
            Assert.That(dialogs.Messages[1], Does.Contain("Eyes"));
            Assert.That(window.TextureSets.Take(4), Is.EqualTo(sets), "every set is kept");
            Assert.That(sets.Select(Slots), Is.EqualTo(new[] { "0", "", "1", "" }));
            Assert.That(Natives(sets), Is.EqualTo(before), "and every pixel, also of the sets the new model does not have");
            Assert.That(sets[1].Material.Name, Is.EqualTo("Cloth"), "a set not in the model keeps its material, to find it again in a later model");
            if (add)
            {
                var eyesSet = window.TextureSets.Last();
                Assert.That((window.TextureSets.Count, eyesSet.Name, Slots(eyesSet)), Is.EqualTo((5, "Eyes", "2")));
                Assert.That(eyesSet.Document.CanUndo, Is.False);
            }
            else Assert.That(window.TextureSets.Count, Is.EqualTo(4));
            Assert.That(window.StatusMessage, Does.Contain("2 texture set(s) are not in the new model"));
            // 元のマテリアルを持つモデルに戻せば、また付く（鍵が残っているので）
            dialogs.Answers.Clear(); dialogs.ConfirmAnswer = true;
            window.SetModel(MaterialSetModels.Avatar(folder, "Again"));
            Assert.That(sets.Select(Slots), Is.EqualTo(new[] { "0,2", "1", "3,4", "5,6" }), "matched by name on a model that has them again");
        }

        [Test] public void OtherUvsKeepThePixelsAndSaySo()
        {
            var sets = NewProject(MaterialSetModels.Avatar(folder)); Pattern(sets[0].Document, 7);
            var before = Natives(sets);
            window.SetModel(MaterialSetModels.Avatar(folder, "Moved", bodySkinUv: new Rect(.05f, .05f, .2f, .2f)));
            var text = DialogsOf(window).Messages[0];
            Assert.That(text, Does.Contain("Skin: material Skin, the UVs differ").And.Contain("the pixels stay as they are, but they will look different on the model"));
            Assert.That(text, Does.Contain("Cloth: material Cloth, same UVs"));
            Assert.That(Natives(sets), Is.EqualTo(before));
            Assert.That(window.StatusMessage, Does.Contain("1 texture set(s) have other UVs; their pixels are unchanged"));
        }

        // ───────── 3D のパス ─────────

        /// <summary>今のセット（Skin）の Body の板の上に 2 点のパスの層を作る（描いた画素ごと）。</summary>
        PaintLayer AddPath(string name)
        {
            var d = window.Document; var geometry = window.Preview.Geometry;
            int triangle = Enumerable.Range(0, geometry.TriangleCount).First(i => geometry.Triangles[i].MaterialSlot == 0);
            var brush = new PathBrush { RadiusWorld = .08, Color = new Rgba32(250, 30, 30) };
            var path = new SurfacePath(Guid.NewGuid(), PaintChannel.Color, SurfacePathRenderer.Fingerprint(geometry), brush, new[] { new PathPoint(triangle, .2, .2), new PathPoint(triangle, .6, .3) });
            var layer = d.AddLayer(name);
            if (!layer.IsChannelEnabled(PaintChannel.Color)) d.SetChannelEnabled(layer.Id, PaintChannel.Color, true);
            d.SetPath(layer.Id, path, SurfacePathRenderer.Render(d, geometry, path).Surface);
            Assert.That(layer.GetChannel(PaintChannel.Color).TileCount, Is.GreaterThan(0));
            return layer;
        }
        static byte[] Pixels(PaintLayer layer) => layer.GetChannel(PaintChannel.Color).EnumerateTiles().SelectMany(t => BitConverter.GetBytes(t.Coord.X).Concat(BitConverter.GetBytes(t.Coord.Y)).Concat(t.Bytes)).ToArray();

        [Test] public void PathsAreRedrawnOnTheNewMeshOrKeptAsPixelsAndUndoTogether()
        {
            var sets = NewProject(MaterialSetModels.Avatar(folder));
            var redraw = AddPath("Redrawn"); var far = AddPath("Far"); var locked = AddPath("Locked");
            var d = window.Document;
            d.SetLayerLocks(locked.Id, LayerLocks.All);
            var oldPrint = ((SurfacePath)redraw.Path).ModelFingerprint; var pathId = ((SurfacePath)redraw.Path).Id;
            var redrawPixels = Pixels(redraw); var lockedPixels = Pixels(locked); int undo = d.UndoCount;
            // 同じ形で Body の Skin の UV だけ動いたモデル: パスはその UV に描き直せる
            window.SetModel(MaterialSetModels.Avatar(folder, "NewUv", bodySkinUv: new Rect(.05f, .05f, .2f, .2f)));
            var text = DialogsOf(window).Messages[0];
            Assert.That(text, Does.Contain("Redrawn: redrawn on the new mesh").And.Contain("Locked: left as it is, bound to the old model, because the layer is locked"));
            var rebound = (SurfacePath)redraw.Path;
            Assert.That(rebound.ModelFingerprint, Is.EqualTo(SurfacePathRenderer.Fingerprint(window.Preview.Geometry)).And.Not.EqualTo(oldPrint));
            Assert.That(rebound.Id, Is.EqualTo(pathId), "the same path, bound to the new mesh");
            Assert.That(Pixels(redraw), Is.Not.EqualTo(redrawPixels), "redrawn into the smaller UV square");
            var newRender = SurfacePathRenderer.Render(d, window.Preview.Geometry, rebound).Surface;
            Assert.That(Pixels(redraw), Is.EqualTo(newRender.EnumerateTiles().SelectMany(t => BitConverter.GetBytes(t.Coord.X).Concat(BitConverter.GetBytes(t.Coord.Y)).Concat(t.Bytes)).ToArray()), "exactly what the path draws on the new mesh");
            Assert.That(((SurfacePath)locked.Path).ModelFingerprint, Is.EqualTo(oldPrint)); Assert.That(Pixels(locked), Is.EqualTo(lockedPixels));
            Assert.That(d.UndoCount, Is.EqualTo(undo + 1), "one undo step for the set's paths");
            Assert.That(d.Undo(), Is.True);
            Assert.That(((SurfacePath)redraw.Path).ModelFingerprint, Is.EqualTo(oldPrint)); Assert.That(Pixels(redraw), Is.EqualTo(redrawPixels), "undo brings back the old path and its pixels");

            // Body が遠くへ動いたモデル: 置けるところが無いパスは画素にする（画素は変えない）
            d.Redo(); d.SetLayerLocks(locked.Id, LayerLocks.None);
            var farPixels = Pixels(far);
            window.SetModel(MaterialSetModels.Avatar(folder, "Far", bodyOffset: new Vector3(100, 0, 0)));
            text = DialogsOf(window).Messages.Last();
            Assert.That(text, Does.Contain("Far: kept as pixels (rasterized), because point 1 has no surface of its material within"));
            Assert.That(far.Path, Is.Null, "rasterized"); Assert.That(Pixels(far), Is.EqualTo(farPixels), "the pixels stay");
            Assert.That(window.StatusMessage, Does.Contain("kept as pixels"));
        }

        // ───────── メッシュマップ ─────────

        [Test] public void BakedMeshMapsTurnStaleUnlessTheMeshIsTheSame()
        {
            var sets = NewProject(MaterialSetModels.Avatar(folder));
            window.MeshBakeSettings.Maps = new[] { MeshMapKind.Position };
            foreach (var set in sets.Skip(1)) window.SetMeshBakeTarget(set.Id, false);
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), window.StatusMessage);
            Assert.That(sets[0].MeshMaps.Check(MeshMapKind.Position, window.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current));
            window.SetModel(MaterialSetModels.Avatar(folder, "Same"));
            Assert.That(DialogsOf(window).Messages.Last(), Does.Not.Contain("become stale"), "another asset with the same mesh data");
            Assert.That(sets[0].MeshMaps.Check(MeshMapKind.Position, window.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current));
            window.SetModel(MaterialSetModels.Avatar(folder, "Moved", bodyOffset: new Vector3(0, 1, 0)));
            Assert.That(DialogsOf(window).Messages.Last(), Does.Contain("1 baked mesh map(s) become stale"));
            Assert.That(sets[0].MeshMaps.Check(MeshMapKind.Position, window.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Stale), "kept, and reported stale");
            Assert.That(sets[0].MeshMaps.Count, Is.EqualTo(1));
        }

        // ───────── 保存・読み直し・プロジェクト設定 ─────────

        [Test] public void SavingAfterAChangeOpensOnTheNewModel()
        {
            var sets = NewProject(MaterialSetModels.Avatar(folder)); Pattern(sets[0].Document, 9);
            var next = MaterialSetModels.Avatar(folder, "Next", bodySkinUv: new Rect(.1f, .1f, .3f, .3f));
            window.SetModel(next);
            string path = Path.Combine(Temp(), "Next.ylp"); Directory.CreateDirectory(Path.GetDirectoryName(path));
            DialogsOf(window).File = path; window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var other = NewWindow(); other.OpenProjectAt(path);
            Assert.That(other.Model, Is.SameAs(next), other.StatusMessage);
            Assert.That(other.TextureSets.Select(Slots), Is.EqualTo(sets.Select(Slots)));
            Assert.That(other.TextureSets.Select(s => s.Material), Is.EqualTo(sets.Select(s => s.Material)));
            Assert.That(DocumentBinary.Write(other.TextureSets[0].Document), Is.EqualTo(DocumentBinary.Write(sets[0].Document)));
        }

        [Test] public void ReloadingTheSameAssetPicksUpItsEditedMesh()
        {
            var model = MaterialSetModels.Avatar(folder);
            var sets = NewProject(model); Pattern(sets[0].Document, 1);
            var before = Natives(sets);
            // 書き出し直した FBX の代わりに、メッシュのアセットの UV をその場で変える（窓のスナップショットは古いまま）
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(folder + "/Avatar-Body.asset");
            var uv = mesh.uv; for (int i = 0; i < 4; i++) uv[i] *= .5f; mesh.uv = uv; EditorUtility.SetDirty(mesh); AssetDatabase.SaveAssets();
            var settings = (NewProjectSettings)typeof(TexturePaintWindow).GetMethod("CurrentProjectSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(window, null);
            window.ApplyProjectConfiguration(settings);
            Assert.That(DialogsOf(window).Asked, Is.Empty, "the same model without Reload: nothing to change");
            settings.ReloadModel = true;
            window.ApplyProjectConfiguration(settings);
            Assert.That(DialogsOf(window).Asked, Is.EqualTo(new[] { "Confirm: Reload the model?" }));
            Assert.That(DialogsOf(window).Messages[0], Does.Contain("Reload Avatar from its asset").And.Contain("Skin: material Skin, the UVs differ"));
            Assert.That(window.Model, Is.SameAs(model)); Assert.That(Natives(sets), Is.EqualTo(before));
            Assert.That(window.Preview.Geometry.Triangles.Where(t => t.MaterialSlot == 0).SelectMany(t => new[] { t.UvA, t.UvB, t.UvC }).Max(u => u.x), Is.LessThan(.3f), "the edited UVs are loaded");
        }

        [Test] public void TheConfigurationDialogRematchesTheSetsAndItsChoiceIsApplied()
        {
            var model = MaterialSetModels.Avatar(folder);
            var sets = NewProject(model);
            var next = MaterialSetModels.Prefab(folder, "Swap",
                new MaterialSetModels.Part { Name = "Hair", Materials = new[] { MaterialSetModels.MaterialAsset(folder, "Swap", "Hair") }, Uvs = new[] { new Rect(.05f, .05f, .4f, .4f) } },
                new MaterialSetModels.Part { Name = "Body", Materials = new[] { MaterialSetModels.MaterialAsset(folder, "Swap", "Skin"), MaterialSetModels.MaterialAsset(folder, "Swap", "Cloth") }, Uvs = new[] { new Rect(.05f, .05f, .4f, .4f), new Rect(.55f, .05f, .4f, .4f) } });
            var dialog = ScriptableObject.CreateInstance<NewProjectWindow>();
            try
            {
                var settings = (NewProjectSettings)typeof(TexturePaintWindow).GetMethod("CurrentProjectSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(window, null);
                dialog.Settings = settings; dialog.Configure = true; dialog.OriginalModel = model;
                Assert.That(dialog.Preview.MaterialGroups.Count, Is.EqualTo(4), "the dialog shows the project's model first");
                Assert.That(dialog.Settings.Sets.Select(d => d.Material), Is.EqualTo(new[] { 0, 1, 2, 3 }));
                dialog.Settings.Model = next;
                Assert.That(dialog.Preview.MaterialGroups.Select(g => g.Name), Is.EqualTo(new[] { "Hair", "Skin", "Cloth" }));
                Assert.That(dialog.Settings.Sets.Select(d => (d.Name, d.Material)), Is.EqualTo(new[] { ("Skin", 1), ("Cloth", 2), ("Hair", 0), ("Unassigned", -1) }), "matched by name in the new model's order");
                if (Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        OffscreenGui.RenderToPng((int)NewProjectWindow.Width, (int)NewProjectWindow.Height, () => dialog.DrawContent(new Rect(0, 0, NewProjectWindow.Width, NewProjectWindow.Height)), Path.Combine(Snapshots, "configure-" + language + ".png"), PaintTheme.PanelBg);
                    }
                L.OverrideLanguage(PainterLanguage.English);
                // 手で選び直す: Cloth のセットを Skin に、Skin のセットを Cloth に
                dialog.Settings.Sets[0].Material = 2; dialog.Settings.Sets[1].Material = 1;
                window.ApplyProjectConfiguration(dialog.TakeSettings());
            }
            finally { L.OverrideLanguage(PainterLanguage.English); Object.DestroyImmediate(dialog); }
            Assert.That(DialogsOf(window).Messages[0], Does.Contain("Skin: material Cloth").And.Contain("Cloth: material Skin").And.Contain("Unassigned: not in the new model"));
            Assert.That(sets.Select(s => (s.Name, Slots(s))), Is.EqualTo(new[] { ("Skin", "2"), ("Cloth", "1"), ("Hair", "0"), ("Unassigned", "") }), "the dialog's choice wins over the names");
            Assert.That(window.Model.name, Is.EqualTo("Swap"));
            Assert.That(window.StatusMessage, Does.Contain("Project configuration applied"));
        }

        [Test] public void AModelChangeAndAResizeAreNotAppliedTogether()
        {
            var model = MaterialSetModels.Avatar(folder); var sets = NewProject(model);
            var settings = (NewProjectSettings)typeof(TexturePaintWindow).GetMethod("CurrentProjectSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(window, null);
            settings.Model = MaterialSetModels.Avatar(folder, "Other"); settings.Sets[0].Width = settings.Sets[0].Height = 1024;
            window.ApplyProjectConfiguration(settings);
            Assert.That(DialogsOf(window).Asked, Is.Empty); Assert.That(window.Model, Is.SameAs(model)); Assert.That(sets[0].Document.Width, Is.EqualTo(512));
            Assert.That(window.StatusMessage, Does.Contain("two steps").And.Contain("nothing changed"));
        }

        /// <summary>確かめの文の英日（日本語に英語の文が残らない）。描いた文は Logs/YoluPainterSnapshots/model-change に残す（見て確かめる用）。</summary>
        [Test] public void TheConfirmationListsTheChangesInBothLanguages()
        {
            var sets = NewProject(MaterialSetModels.Avatar(folder));
            AddPath("Path");
            var skin = MaterialSetModels.MaterialAsset(folder, "Lang", "Skin"); var eyes = MaterialSetModels.MaterialAsset(folder, "Lang", "Eyes");
            var next = MaterialSetModels.Prefab(folder, "Lang",
                new MaterialSetModels.Part { Name = "Body", Materials = new[] { skin }, Uvs = new[] { new Rect(.05f, .05f, .2f, .4f) } },
                new MaterialSetModels.Part { Name = "Eyes", Materials = new[] { eyes }, Uvs = new[] { new Rect(.05f, .05f, .4f, .4f) } });
            Directory.CreateDirectory(Snapshots);
            foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
            {
                L.OverrideLanguage(language);
                using (var plan = window.PlanModelChange(next))
                {
                    string text = window.ModelChangeText(plan);
                    File.WriteAllText(Path.Combine(Snapshots, "confirm-" + language + ".txt"), text);
                    Assert.That(plan.Sets.Select(s => s.Group), Is.EqualTo(new[] { 0, -1, -1, -1 }));
                    Assert.That(plan.Unused.Select(g => g.Name), Is.EqualTo(new[] { "Eyes" }));
                    if (language == PainterLanguage.Japanese)
                    {
                        Assert.That(text, Does.Contain("テクスチャセット").And.Contain("Undo"));
                        Assert.That(text, Does.Not.Contain("not in the new model").And.Not.Contain("the UVs differ").And.Not.Contain("cannot be undone"), "no English sentence is left");
                    }
                    else Assert.That(text, Does.Contain("Path: redrawn on the new mesh").And.Contain("Materials of the new model without a texture set: Eyes"));
                }
            }
            L.OverrideLanguage(PainterLanguage.English);
            Assert.That(window.Model.name, Is.EqualTo("Avatar"), "planning changes nothing");
        }
    }
}
