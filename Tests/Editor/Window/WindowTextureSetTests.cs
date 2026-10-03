using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>テストのモデル: 並んだ板（1 枚が 1 つのマテリアルのスロット）の一時のプレハブ。</summary>
    internal static class TextureSetModels
    {
        /// <summary>folder の下に、materials の数だけ板を並べたプレハブを作る（マテリアルの名前は重なってもよい。それぞれ別のフォルダに置く）。</summary>
        public static GameObject Prefab(string folder, string name, params string[] materials)
        {
            var vertices = new List<Vector3>(); var uvs = new List<Vector2>();
            var mesh = new Mesh { name = name + "Mesh", subMeshCount = materials.Length };
            for (int i = 0; i < materials.Length; i++)
            {
                float x = i * 1.2f;
                vertices.AddRange(new[] { new Vector3(x, 0, 0), new Vector3(x + 1, 0, 0), new Vector3(x + 1, 1, 0), new Vector3(x, 1, 0) });
                uvs.AddRange(new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up });
            }
            mesh.SetVertices(vertices); mesh.SetUVs(0, uvs);
            for (int i = 0; i < materials.Length; i++) { int b = i * 4; mesh.SetTriangles(new[] { b, b + 2, b + 1, b, b + 3, b + 2 }, i); }
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, folder + "/" + name + "Mesh.asset");
            var shared = new Material[materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                string sub = AssetDatabase.CreateFolder(folder, name + "-m" + i); Assert.That(sub, Is.Not.Empty);
                var material = new Material(Shader.Find("Unlit/Texture"));
                AssetDatabase.CreateAsset(material, folder + "/" + name + "-m" + i + "/" + materials[i] + ".mat");
                shared[i] = material;
            }
            var go = new GameObject(name); go.AddComponent<MeshFilter>().sharedMesh = mesh; go.AddComponent<MeshRenderer>().sharedMaterials = shared;
            try { return PrefabUtility.SaveAsPrefabAsset(go, folder + "/" + name + ".prefab"); }
            finally { Object.DestroyImmediate(go); }
        }
    }

    /// <summary>
    /// 1 つのプロジェクトに複数のテクスチャセット（Substance Painter と同じ）: 新規プロジェクトでスロットごとのセット、切り替え（文書・選んだ層・
    /// Undo の履歴が入れ替わり、切り替えは履歴にも未保存にも入らない）、3D の表示（全部のセットがそれぞれのスロットに）、保存と開く
    /// （.ylp の形式 3）・形式 1・2 のファイル・復旧 checkpoint、書き出しの名前、セットを消す確かめ、プロジェクト全体のメモリの予算、
    /// インポーター、パネルの描画、ベイクの窓で順に焼く。ウィンドウは表示せずに作る（OnEnable は走る）ので batch でも動く。描いた PNG は
    /// テストプロジェクトの Logs/YoluPainterSnapshots/texture-sets に残す（見て確かめる用）。
    /// </summary>
    public sealed class TextureSetTests
    {
        string folder, project; TexturePaintWindow window;
        readonly List<TexturePaintWindow> others = new List<TexturePaintWindow>();
        readonly List<string> temporary = new List<string>();
        static string Snapshots => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "texture-sets"));

        sealed class Dialogs : IPainterDialogs
        {
            public string Folder = "", File = "";
            public bool ConfirmAnswer = true;
            public readonly List<string> Asked = new List<string>();
            public string SaveFolder(string title, string folder, string defaultName) { Asked.Add("SaveFolder"); return Folder; }
            public string OpenFolder(string title, string folder) { Asked.Add("OpenFolder"); return Folder; }
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
            PainterSettings.ProjectRoot = project;
            string name = "TextureSetTests-" + Guid.NewGuid().ToString("N");
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

        string Temp(string extension = "") { string path = Path.Combine(Path.GetTempPath(), "yolupainter-sets-" + Guid.NewGuid().ToString("N") + extension); temporary.Add(path); return path; }

        TexturePaintWindow NewWindow()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            w.Dialogs = new Dialogs(); w.MeshBakeProgress = (title, info, progress) => false;
            if (window != null) others.Add(w);
            return w;
        }
        static Dialogs DialogsOf(TexturePaintWindow w) => (Dialogs)w.Dialogs;

        /// <summary>モデルの全部のスロットのプロジェクト（512、テンプレートは指定のもの）。</summary>
        IReadOnlyList<TexturePaintWindow.TextureSet> NewProject(GameObject model, ProjectTemplate template = ProjectTemplate.Pbr, int resolution = 512)
        {
            window.CreateProject(new NewProjectSettings { Model = model, Resolution = resolution, Template = template });
            return window.TextureSets.ToList();
        }

        /// <summary>層の chosen チャンネルを一色で塗る（1 回の Undo）。</summary>
        static void Flood(PaintDocument d, PaintChannel channel, Rgba32 color)
        {
            var rgba = new byte[d.Width * d.Height * 4];
            for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = color.R; rgba[i + 1] = color.G; rgba[i + 2] = color.B; rgba[i + 3] = color.A; }
            var layer = d.Layers[0];
            d.Batch(() => { if (!layer.IsChannelEnabled(channel)) d.SetChannelEnabled(layer.Id, channel, true); d.ReplacePixels(layer.Id, channel, rgba, withinSelection: false); });
        }
        /// <summary>層の channel を雑音で埋める（タイルが全部、圧縮できない画素で埋まる）。</summary>
        static void Noise(PaintDocument d, PaintChannel channel, int seed)
        {
            var rgba = new byte[d.Width * d.Height * 4]; new System.Random(seed).NextBytes(rgba);
            var layer = d.Layers[0];
            d.Batch(() => { if (!layer.IsChannelEnabled(channel)) d.SetChannelEnabled(layer.Id, channel, true); d.ReplacePixels(layer.Id, channel, rgba, withinSelection: false); });
        }
        static byte[] Raw(Texture texture)
        {
            if (texture is Texture2D t) return t.GetRawTextureData<byte>().ToArray();
            var rt = (RenderTexture)texture; var active = RenderTexture.active;
            var read = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            try { RenderTexture.active = rt; read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); read.Apply(); return read.GetRawTextureData<byte>().ToArray(); }
            finally { RenderTexture.active = active; Object.DestroyImmediate(read); }
        }

        // ───────── 新規プロジェクト ─────────

        [Test] public void ANewProjectMakesOneTextureSetPerCheckedSlot()
        {
            var model = TextureSetModels.Prefab(folder, "Chara", "Body", "Hair", "Eyes");
            var sets = NewProject(model, ProjectTemplate.LilToon);
            Assert.That(sets.Select(s => (s.Name, s.MaterialSlot)), Is.EqualTo(new[] { ("Body", 0), ("Hair", 1), ("Eyes", 2) }), "every slot by default, named after its material");
            foreach (var set in sets)
            {
                Assert.That((set.Document.Width, set.Document.Height), Is.EqualTo((512, 512)));
                Assert.That(Enum.GetValues(typeof(PaintChannel)).Cast<PaintChannel>().Where(set.Document.Layers.Single().IsChannelEnabled), Is.EquivalentTo(NewProjectSettings.Channels(ProjectTemplate.LilToon)));
                Assert.That(set.Document.CanUndo, Is.False); Assert.That(set.Id, Is.EqualTo(set.Document.Id));
            }
            Assert.That(sets.Select(s => s.Document).Distinct().Count(), Is.EqualTo(3), "each set has its own document");
            Assert.That(window.CurrentTextureSet, Is.SameAs(sets[0])); Assert.That(window.Document, Is.SameAs(sets[0].Document));
            Assert.That(window.StatusMessage, Does.Contain("3 texture sets"));

            window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Slots = new[] { 2, 0 } });
            Assert.That(window.TextureSets.Select(s => (s.Name, s.MaterialSlot)), Is.EqualTo(new[] { ("Body", 0), ("Eyes", 2) }), "only the checked slots, in slot order");

            window.CreateProject(new NewProjectSettings { Model = null, Resolution = 512, Slots = new[] { 1 } });
            Assert.That(window.TextureSets.Select(s => (s.Name, s.MaterialSlot)), Is.EqualTo(new[] { ("Texture Set 1", 0) }), "without a model: one set for slot 0");

            var current = window.Document;
            Assert.That(() => window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Slots = new int[0] }), Throws.ArgumentException);
            Assert.That(window.Document, Is.SameAs(current), "a refused project leaves the open one");

            var twins = TextureSetModels.Prefab(folder, "Twins", "Skin", "Skin");
            Assert.That(NewProject(twins).Select(s => s.Name), Is.EqualTo(new[] { "Skin", "Skin 2" }), "names stay unique");
        }

        // ───────── 切り替え ─────────

        [Test] public void SwitchingSwapsTheDocumentLayerAndHistoryAndIsNeitherAnEditNorUndoable()
        {
            var sets = NewProject(TextureSetModels.Prefab(folder, "Pair", "Body", "Hair"));
            var dialogs = DialogsOf(window); dialogs.File = Path.Combine(Temp(), "Pair.ylp"); Directory.CreateDirectory(Path.GetDirectoryName(dialogs.File));
            var a = sets[0]; var b = sets[1];
            var added = a.Document.AddLayer("A2"); window.SelectedLayer = added.Id;
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            long aRevision = a.Document.Revision, bRevision = b.Document.Revision;

            Assert.That(window.SwitchTextureSet(b.Id), Is.True);
            Assert.That(window.Document, Is.SameAs(b.Document)); Assert.That(window.CurrentTextureSet, Is.SameAs(b));
            Assert.That(window.SelectedLayer, Is.EqualTo(b.Document.Layers.Single().Id));
            Assert.That(window.Document.CanUndo, Is.False, "the history is the set's own");
            Assert.That((a.Document.Revision, b.Document.Revision), Is.EqualTo((aRevision, bRevision)), "switching changes no document");
            Assert.That(window.IsSaved, Is.True, "switching is not an unsaved change");
            Assert.That(window.Document.Undo(), Is.False); Assert.That(a.Document.Layers.Count, Is.EqualTo(2), "undo in B does not reach A");
            Assert.That(window.StatusMessage, Does.Contain("Hair"));

            b.Document.AddLayer("B2");
            Assert.That(window.SwitchTextureSet(a.Id), Is.True);
            Assert.That(window.SelectedLayer, Is.EqualTo(added.Id), "the selected layer comes back with the set");
            Assert.That(window.Document.CanUndo, Is.True);
            Assert.That(window.Document.Undo(), Is.True);
            Assert.That(a.Document.Layers.Count, Is.EqualTo(1)); Assert.That(b.Document.Layers.Count, Is.EqualTo(2), "each set undoes on its own");
            Assert.That(window.SwitchTextureSet(a.Id), Is.False, "already the current set");
            Assert.That(window.IsSaved, Is.False);
            Assert.That(() => window.SwitchTextureSet(Guid.NewGuid()), Throws.ArgumentException);
        }

        // ───────── 3D ─────────

        [Test] public void EveryTextureSetIsShownOnItsOwnSlotWithItsLighting()
        {
            var sets = NewProject(TextureSetModels.Prefab(folder, "Trio", "Body", "Hair", "Eyes"));
            var colors = new[] { new Rgba32(200, 10, 10), new Rgba32(10, 200, 10), new Rgba32(10, 10, 200) };
            for (int i = 0; i < 3; i++) { Flood(sets[i].Document, PaintChannel.Color, colors[i]); Flood(sets[i].Document, PaintChannel.Roughness, new Rgba32((byte)(50 * i), 0, 0)); }
            var preview = window.Preview;
            Assert.That(preview.MaterialSlotCount, Is.EqualTo(3));
            window.RefreshPreviewTextures();
            Assert.That(preview.ShownTexture(0), Is.SameAs(window.DisplayTexture), "the current set shows the compositor's texture");
            for (int i = 1; i < 3; i++)
            {
                Assert.That(preview.ShownTexture(i), Is.SameAs(window.SetDisplay(sets[i])), "slot " + i);
                Assert.That(Raw(preview.ShownTexture(i)), Is.EqualTo(sets[i].Document.Composite(PaintChannel.Color)), "slot " + i + " shows its own set");
                Assert.That(preview.ShownNormal(i), Is.Not.Null, "slot " + i + " is lit with its own normals");
            }
            Assert.That(preview.ShownNormal(0), Is.SameAs(window.PreviewNormalTexture));
            var cached = window.SetDisplay(sets[1]);
            window.RefreshPreviewTextures();
            Assert.That(window.SetDisplay(sets[1]), Is.SameAs(cached), "an unchanged set is not composited again");

            window.Channel = PaintChannel.Roughness; window.RefreshPreviewTextures();
            Assert.That(Raw(preview.ShownTexture(2)), Is.EqualTo(sets[2].Document.Composite(PaintChannel.Roughness)), "the window's channel for every set");
            window.Channel = PaintChannel.Color;
            window.SwitchTextureSet(sets[2].Id); window.RefreshPreviewTextures();
            Assert.That(preview.ShownTexture(2), Is.SameAs(window.DisplayTexture));
            Assert.That(Raw(preview.ShownTexture(0)), Is.EqualTo(sets[0].Document.Composite(PaintChannel.Color)));
            Assert.That(Raw(window.DisplayTexture), Is.EqualTo(sets[2].Document.Composite(PaintChannel.Color)));

            window.PreviewNormals = false; window.RefreshPreviewTextures();
            Assert.That(Enumerable.Range(0, 3).Select(preview.ShownNormal), Is.All.Null, "Light with Normal Output off: no slot is lit with normals");
        }

        // ───────── 保存と開く ─────────

        [Test] public void TwoTextureSetsSaveAndOpenWithEverything()
        {
            var model = TextureSetModels.Prefab(folder, "Duo", "Body", "Hair");
            var sets = NewProject(model);
            var a = sets[0]; var b = sets[1];
            Flood(a.Document, PaintChannel.Color, new Rgba32(1, 2, 3)); a.Document.SetSelection(SelectionMask.Rectangle(a.Document, 10, 20, 100, 50));
            window.MeshBakeUseGpu = false; window.MeshBakeSettings.Maps = new[] { MeshMapKind.WorldNormal, MeshMapKind.Position };
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), window.StatusMessage);
            Assert.That((a.MeshMaps.Count, b.MeshMaps.Count), Is.EqualTo((2, 2)), "every checked set is baked");
            Assert.That(b.MeshMaps.Maps.All(m => m.Provenance.TargetSlot == 1), Is.True);
            window.SwitchTextureSet(b.Id);
            Noise(b.Document, PaintChannel.Height, 7); b.Document.SetSelection(SelectionMask.Ellipse(b.Document, 200, 200, 80, 40));
            window.RenameTextureSet(b.Id, "Hair Front");
            var dialogs = DialogsOf(window); dialogs.File = Path.Combine(Temp(), "Duo.ylp"); Directory.CreateDirectory(Path.GetDirectoryName(dialogs.File));
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);

            var raw = YlpStore.Load(dialogs.File).Files;
            var opened = YlpFormat.Open(raw);
            Assert.That(opened.Info.Format, Is.EqualTo(YlpFormat.Current)); Assert.That(opened.UnknownEntries, Is.Empty);
            Assert.That(opened.Project.Sets.Select(s => (s.Id, s.Name, s.MaterialSlot)), Is.EqualTo(new[] { (a.Id, "Body", 0), (b.Id, "Hair Front", 1) }));
            Assert.That(opened.Project.CurrentSet, Is.EqualTo(b.Id));
            Assert.That(raw.Keys.Where(k => !k.StartsWith(YlpFormat.SetsFolder, StringComparison.Ordinal)), Is.EquivalentTo(new[] { "ylp.json", "project.json", "view.json", "brush.json", "thumbnail.png" }));
            Assert.That(Encoding(raw["view.json"]), Does.Not.Contain("materialSlot"), "the slot lives in project.json");
            foreach (var set in new[] { a, b })
            {
                var files = opened.SetFiles(set.Id);
                Assert.That(files[YlpArchive.NativeName], Is.EqualTo(DocumentBinary.Write(set.Document)), set.Name);
                Assert.That(files[SelectionBinary.EntryName], Is.EqualTo(SelectionBinary.Write(set.Document.Selection)), set.Name);
                Assert.That(files.Keys.Count(k => k.StartsWith(MeshMapBinary.EntryPrefix, StringComparison.Ordinal)), Is.EqualTo(2), set.Name);
            }

            var other = NewWindow();
            DialogsOf(other).File = dialogs.File;
            other.OpenProject();
            Assert.That(other.TextureSets.Select(s => (s.Id, s.Name, s.MaterialSlot)), Is.EqualTo(new[] { (a.Id, "Body", 0), (b.Id, "Hair Front", 1) }), other.StatusMessage);
            Assert.That(other.CurrentTextureSet.Id, Is.EqualTo(b.Id), "the current set comes back");
            Assert.That(other.IsSaved, Is.True); Assert.That(other.Preview.HasModel, Is.True, "the model comes back from view.json");
            foreach (var (mine, theirs) in new[] { a, b }.Zip(other.TextureSets, (m, t) => (m, t)))
            {
                Assert.That(DocumentBinary.Write(theirs.Document), Is.EqualTo(DocumentBinary.Write(mine.Document)), mine.Name);
                Assert.That(SelectionBinary.Write(theirs.Document.Selection), Is.EqualTo(SelectionBinary.Write(mine.Document.Selection)), mine.Name);
                Assert.That(theirs.MeshMaps.Maps.Select(MeshMapBinary.Write), Is.EqualTo(mine.MeshMaps.Maps.Select(MeshMapBinary.Write)), mine.Name + " mesh maps, byte for byte");
                Assert.That(theirs.Document.CanUndo, Is.False);
            }
            Assert.That(other.MeshMaps.Count, Is.EqualTo(2)); Assert.That(other.MeshMapsSaved, Is.True);
            Assert.That(other.MeshMaps.Check(MeshMapKind.Position, other.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current), "checked against the reloaded model and the set's slot");
        }

        static string Encoding(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

        [TestCase("format1.ylp", 1)]
        [TestCase("format2.ylp", 2)]
        public void AnOlderFileOpensAsOneTextureSet(string fixture, int format)
        {
            string path = Temp(".ylp"); File.Copy(PackagePaths.Physical("Tests/Editor/Persistence/Fixtures~/" + fixture), path);
            byte[] native = YlpStore.Load(path).Files[YlpArchive.NativeName];
            // フィクスチャの正本は版 10。版 11 は Generator の段を持つ文書だけ並びが違うので、持たない文書は版の数のほかは同じに書き戻る
            Assert.That(System.BitConverter.ToInt32(native, 8), Is.EqualTo(10));
            native = (byte[])native.Clone(); System.BitConverter.GetBytes(DocumentBinary.CurrentVersion).CopyTo(native, 8);
            window.OpenProjectAt(path);
            Assert.That(window.OpenedFormat, Is.EqualTo(format), window.StatusMessage);
            var set = window.TextureSets.Single();
            Assert.That((set.Name, set.MaterialSlot, set.Id), Is.EqualTo(("Texture Set 1", 0, set.Document.Id)), "no model: the default name, slot 0 from view.json");
            Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(native));
            Assert.That(window.IsSaved, Is.True, "naming the migrated set is not an unsaved change");
            byte[] original = format == 2 ? YlpStore.Load(path).Files[YlpFormat.ImportedOriginalName] : null;
            DialogsOf(window).File = path; window.SaveProject(false);
            var saved = YlpFormat.Open(YlpStore.Load(path).Files);
            Assert.That(saved.Info.Format, Is.EqualTo(YlpFormat.Current), window.StatusMessage);
            var files = saved.SetFiles(set.Id);
            Assert.That(files[YlpArchive.NativeName], Is.EqualTo(native), "saving writes the same native source into the set's folder");
            if (original != null) Assert.That(files[YlpFormat.ImportedOriginalName], Is.EqualTo(original), "the imported PSD's original bytes are kept");
            Assert.That(YlpStore.Backups(path).Count, Is.EqualTo(1), "the older file is kept as a backup");
        }

        [Test] public void TheRecoveryCheckpointKeepsEveryTextureSet()
        {
            var sets = NewProject(TextureSetModels.Prefab(folder, "Saved", "Body", "Hair"));
            Flood(sets[0].Document, PaintChannel.Color, new Rgba32(9, 8, 7)); Flood(sets[1].Document, PaintChannel.Color, new Rgba32(1, 2, 3));
            window.SwitchTextureSet(sets[1].Id);
            window.GetType().GetMethod("OnLostFocus", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null); window.FlushRecovery();
            var checkpoint = YlpFormat.Open(GenerationStore.Load(window.RecoveryRoot).Files);
            Assert.That(checkpoint.Project.Sets.Select(s => s.Id), Is.EqualTo(sets.Select(s => s.Id)));
            Assert.That(checkpoint.Project.CurrentSet, Is.EqualTo(sets[1].Id));
            foreach (var set in sets) Assert.That(checkpoint.SetFiles(set.Id)[YlpArchive.NativeName], Is.EqualTo(DocumentBinary.Write(set.Document)), set.Name);

            // 別の窓が同じ checkpoint から戻す（ドメインのリロードと同じ道）
            var restored = NewWindow(); var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            restored.GetType().GetMethod("OnDisable", flags).Invoke(restored, null);
            string own = restored.RecoveryRoot; if (Directory.Exists(own)) Directory.Delete(own, true);
            restored.GetType().GetField("recoveryRoot", flags).SetValue(restored, window.RecoveryRoot);
            restored.GetType().GetMethod("OnEnable", flags).Invoke(restored, null);
            Assert.That(restored.StatusMessage, Does.StartWith("Recovered"));
            Assert.That(restored.TextureSets.Select(s => (s.Id, s.Name, s.MaterialSlot)), Is.EqualTo(sets.Select(s => (s.Id, s.Name, s.MaterialSlot))));
            Assert.That(restored.CurrentTextureSet.Id, Is.EqualTo(sets[1].Id));
            for (int i = 0; i < 2; i++) Assert.That(DocumentBinary.Write(restored.TextureSets[i].Document), Is.EqualTo(DocumentBinary.Write(sets[i].Document)));
            Assert.That(restored.IsSaved, Is.False, "recovered work is not a saved file");
        }

        // ───────── 書き出し ─────────

        [Test] public void ExportedImagesCarryTheTextureSetNameOnlyWhenThereAreSeveral()
        {
            var model = TextureSetModels.Prefab(folder, "Export", "Body", "Hair");
            window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Template = ProjectTemplate.ColorOnly, Slots = new[] { 0 } });
            string one = Temp(); Directory.CreateDirectory(one);
            DialogsOf(window).Folder = one; window.ExportImages();
            Assert.That(Directory.GetFiles(one).Select(Path.GetFileName), Is.EquivalentTo(new[] { "Texture_Color.png" }), window.StatusMessage);

            NewProject(model, ProjectTemplate.LilToon);
            string two = Temp(); Directory.CreateDirectory(two);
            DialogsOf(window).Folder = two; window.ExportImages();
            Assert.That(Directory.GetFiles(two).Select(Path.GetFileName), Is.EquivalentTo(new[] { "Texture_Body_Color.png", "Texture_Body_Normal.png", "Texture_Body_Emission.png", "Texture_Hair_Color.png", "Texture_Hair_Normal.png", "Texture_Hair_Emission.png" }), window.StatusMessage);

            window.RenameTextureSet(window.TextureSets[0].Id, "A/B"); window.RenameTextureSet(window.TextureSets[1].Id, "A_B");
            string clash = Temp(); Directory.CreateDirectory(clash);
            DialogsOf(window).Folder = clash; DialogsOf(window).Asked.Clear(); window.ExportImages();
            Assert.That(Directory.GetFiles(clash), Is.Empty, "two sets that would write the same file: nothing is written");
            Assert.That(DialogsOf(window).Asked, Is.Empty, "and no folder is asked for");
            Assert.That(window.StatusMessage, Does.Contain("same file"));
        }

        // ───────── 足す・消す・名前・スロット ─────────

        [Test] public void RemovingATextureSetAsksFirstAndDecliningChangesNothing()
        {
            var model = TextureSetModels.Prefab(folder, "Three", "Body", "Hair", "Eyes");
            var sets = NewProject(model);
            var dialogs = DialogsOf(window); dialogs.File = Path.Combine(Temp(), "Three.ylp"); Directory.CreateDirectory(Path.GetDirectoryName(dialogs.File));
            window.SaveProject(true); Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            window.SwitchTextureSet(sets[1].Id);
            dialogs.ConfirmAnswer = false; dialogs.Asked.Clear();
            Assert.That(window.RemoveTextureSet(sets[1].Id), Is.False);
            Assert.That(dialogs.Asked, Is.EqualTo(new[] { "Confirm: Remove texture set?" }));
            Assert.That(window.TextureSets, Is.EqualTo(sets)); Assert.That(window.CurrentTextureSet, Is.SameAs(sets[1])); Assert.That(window.IsSaved, Is.True, "declining changes nothing");

            // プロジェクト設定で消して名前も変える: 断れば、モデルを含めて何も変えない
            var configure = new NewProjectSettings { Model = null, Resolution = 512, Sets = new List<TextureSetDraft> { new TextureSetDraft { Id = sets[0].Id, Name = "Skin", Slot = 0 }, new TextureSetDraft { Id = sets[2].Id, Name = "Eyes", Slot = 2 } } };
            window.ApplyProjectConfiguration(configure);
            Assert.That(window.TextureSets, Is.EqualTo(sets)); Assert.That(sets[0].Name, Is.EqualTo("Body")); Assert.That(window.Preview.HasModel, Is.True); Assert.That(window.IsSaved, Is.True);
            Assert.That(window.StatusMessage, Does.Contain("nothing changed"));

            dialogs.ConfirmAnswer = true;
            configure.Model = model;
            window.ApplyProjectConfiguration(configure);
            Assert.That(window.TextureSets.Select(s => (s.Name, s.MaterialSlot)), Is.EqualTo(new[] { ("Skin", 0), ("Eyes", 2) }));
            Assert.That(window.CurrentTextureSet, Is.SameAs(sets[2]), "the removed current set hands over to the next one");
            Assert.That(window.IsSaved, Is.False, "removing and renaming are unsaved changes");

            Assert.That(window.RemoveTextureSet(sets[0].Id), Is.True);
            Assert.That(window.RemoveTextureSet(sets[2].Id), Is.False, "the last set stays");
            Assert.That(window.TextureSets.Single(), Is.SameAs(sets[2]));
        }

        [Test] public void AddingRenamingAndMovingTextureSets()
        {
            var model = TextureSetModels.Prefab(folder, "Grow", "Body", "Hair", "Eyes");
            window.CreateProject(new NewProjectSettings { Model = model, Resolution = 1024, Template = ProjectTemplate.LilToon, NormalFormat = NormalYDirection.DirectX, Slots = new[] { 0 } });
            var first = window.CurrentTextureSet;
            Assert.That(() => window.AddTextureSet(0), Throws.InvalidOperationException, "a slot has at most one set");
            var hair = window.AddTextureSet(1);
            Assert.That((hair.Name, hair.MaterialSlot), Is.EqualTo(("Hair", 1)));
            Assert.That(window.CurrentTextureSet, Is.SameAs(hair), "the added set becomes the current one");
            Assert.That((hair.Document.Width, hair.Document.Height), Is.EqualTo((1024, 1024)), "the same size as the open set");
            Assert.That(YlpContent.UsedChannels(hair.Document), Is.EqualTo(YlpContent.UsedChannels(first.Document)), "the same channels");
            Assert.That(hair.Document.NormalSettings.FileDirection, Is.EqualTo(NormalYDirection.DirectX));
            Assert.That(hair.Document.CanUndo, Is.False);
            Assert.That(() => window.RenameTextureSet(hair.Id, "body"), Throws.ArgumentException, "names stay unique, ignoring case");
            Assert.That(() => window.RenameTextureSet(hair.Id, " "), Throws.ArgumentException);
            window.RenameTextureSet(hair.Id, "Hair Back");
            Assert.That(() => window.SetTextureSetSlot(hair.Id, 0), Throws.InvalidOperationException);
            window.SetTextureSetSlot(hair.Id, 2);
            Assert.That(window.TextureSets.Select(s => (s.Name, s.MaterialSlot)), Is.EqualTo(new[] { ("Body", 0), ("Hair Back", 2) }));
            // プロジェクト設定: 足す（空のセット）・スロットを入れ替える
            var drafts = window.TextureSets.Select(s => new TextureSetDraft { Id = s.Id, Name = s.Name, Slot = s.MaterialSlot == 0 ? 2 : 0 }).ToList();
            drafts.Add(new TextureSetDraft { Name = "Extra", Slot = 1 });
            window.ApplyProjectConfiguration(new NewProjectSettings { Model = model, Resolution = 1024, NormalFormat = NormalYDirection.OpenGL, Sets = drafts });
            Assert.That(window.TextureSets.Select(s => (s.Name, s.MaterialSlot)), Is.EqualTo(new[] { ("Body", 2), ("Hair Back", 0), ("Extra", 1) }));
            Assert.That(window.TextureSets.All(s => s.Document.NormalSettings.FileDirection == NormalYDirection.OpenGL), Is.True, "the normal map format is the project's");
            Assert.That(() => new NewProjectSettings { Resolution = 1024, Sets = new List<TextureSetDraft> { new TextureSetDraft { Name = "A", Slot = 0 }, new TextureSetDraft { Name = "a", Slot = 1 } } }.Validate(), Throws.ArgumentException);
            Assert.That(() => new NewProjectSettings { Resolution = 1024, Sets = new List<TextureSetDraft> { new TextureSetDraft { Name = "A", Slot = 0 }, new TextureSetDraft { Name = "B", Slot = 0 } } }.Validate(), Throws.ArgumentException);
        }

        // ───────── メモリの予算 ─────────

        /// <summary>予算は設定の 1 つの値をプロジェクト全体で守る: 今のセットの文書の予算は「設定 − ほかのセットが使っている量」。</summary>
        [Test] public void TheMemoryBudgetsCoverTheWholeProject()
        {
            PainterSettings.UpdatePersonal(p => { p.sourceBudgetMiB = 16; p.undoBudgetMiB = 64; });
            var sets = NewProject(TextureSetModels.Prefab(folder, "Budget", "Body", "Hair"), ProjectTemplate.ColorOnly, 1024);
            var a = sets[0]; var b = sets[1];
            Noise(a.Document, PaintChannel.Color, 1); Noise(a.Document, PaintChannel.Roughness, 2);
            Assert.That(a.Document.AllocatedBytes, Is.EqualTo(8L << 20));
            window.SwitchTextureSet(b.Id);
            Assert.That(b.Document.SourceBudgetBytes, Is.EqualTo((16L << 20) - a.Document.AllocatedBytes), "the current set gets what the others leave");
            Assert.That(b.Document.UndoBudgetBytes, Is.EqualTo((64L << 20) - a.Document.HistoryBytes));
            Noise(b.Document, PaintChannel.Color, 3); Noise(b.Document, PaintChannel.Roughness, 4);
            Assert.That(a.Document.AllocatedBytes + b.Document.AllocatedBytes, Is.EqualTo(16L << 20), "exactly the budget");
            byte[] aBytes = DocumentBinary.Write(a.Document); int undo = b.Document.UndoCount; long allocated = b.Document.AllocatedBytes;
            var composites = Enum.GetValues(typeof(PaintChannel)).Cast<PaintChannel>().Select(c => b.Document.Composite(c)).ToList();
            Assert.That(() => Noise(b.Document, PaintChannel.Metallic, 5), Throws.InvalidOperationException.With.Message.Contains("budget"), "more than the project's budget is refused");
            // 取り消した Batch は、有効にしかけたチャンネルの空の記録（画素なし・無効）を残すことがあるので、正本のバイト列ではなく中身で比べる
            Assert.That(Enum.GetValues(typeof(PaintChannel)).Cast<PaintChannel>().Select(c => b.Document.Composite(c)), Is.EqualTo(composites));
            Assert.That(b.Document.AllocatedBytes, Is.EqualTo(allocated)); Assert.That(b.Document.UndoCount, Is.EqualTo(undo));
            Assert.That(b.Document.Layers[0].IsChannelEnabled(PaintChannel.Metallic), Is.False, "the refused edit left nothing behind");
            Assert.That(DocumentBinary.Write(a.Document), Is.EqualTo(aBytes));
            // 切り替えると入れ替わる: A の予算は B が使っている分を引いたもの
            window.SwitchTextureSet(a.Id);
            Assert.That(a.Document.SourceBudgetBytes, Is.EqualTo(a.Document.AllocatedBytes), "A is at the project's budget");
            Assert.That(() => Noise(a.Document, PaintChannel.Metallic, 6), Throws.InvalidOperationException);
            // 消したセットの分は戻る
            Assert.That(window.RemoveTextureSet(b.Id), Is.True);
            Assert.That(a.Document.SourceBudgetBytes, Is.EqualTo(16L << 20));
        }

        // ───────── インポーター ─────────

        [Test] public void TheImporterListsTheTextureSets()
        {
            var sets = NewProject(TextureSetModels.Prefab(folder, "Import", "Body", "Hair"), ProjectTemplate.ColorOnly);
            Flood(sets[1].Document, PaintChannel.Emission, new Rgba32(1, 200, 3));
            window.SwitchTextureSet(sets[1].Id);
            string asset = folder + "/Import.ylp";
            DialogsOf(window).File = Path.GetFullPath(asset);
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var info = YlpImporter.LoadInfo(asset);
            Assert.That(info, Is.Not.Null); Assert.That(info.error, Is.Empty);
            Assert.That(info.format, Is.EqualTo(YlpFormat.Current));
            Assert.That(info.textureSets.Select(s => (s.name, s.materialSlot, s.width, s.height, s.current, s.fromNativeDocument)),
                Is.EqualTo(new[] { ("Body", 0, 512, 512, false, false), ("Hair", 1, 512, 512, true, false) }));
            Assert.That(info.textureSets[0].channels, Is.EqualTo(new[] { PaintChannel.Color }));
            Assert.That(info.textureSets[1].channels, Is.EqualTo(new[] { PaintChannel.Color, PaintChannel.Emission }), "read from the set's composite images");
            Assert.That(info.channels, Is.EqualTo(info.textureSets[1].channels), "the main object describes the current set");
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(asset).OfType<Texture>(), Is.Empty, "still no texture");
        }

        // ───────── 描く ─────────

        static void RequireOffscreen()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon.");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
        }

        void RenderPanel(int width, string name)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            float height = (float)typeof(TexturePaintWindow).GetProperty("TextureSetHeight", flags).GetValue(window);
            var draw = typeof(TexturePaintWindow).GetMethod("DrawTextureSetPanel", flags);
            string path = Path.Combine(Snapshots, name + ".png");
            OffscreenGui.RenderToPng(width, Mathf.CeilToInt(height), () => draw.Invoke(window, new object[] { new Rect(0, 0, width, height) }), path, PaintTheme.PanelBg);
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, name);
                int colors = texture.GetPixels32().Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Take(100).Count();
                Assert.That(colors, Is.GreaterThan(20), name + ": the panel looks empty");
            }
            finally { Object.DestroyImmediate(texture); }
        }

        /// <summary>新規プロジェクト（スロットのチェック）とプロジェクト設定（セットの並び）のダイアログ。</summary>
        void RenderDialogs(GameObject model, string tag)
        {
            var dialog = ScriptableObject.CreateInstance<NewProjectWindow>();
            try
            {
                var current = (NewProjectSettings)typeof(TexturePaintWindow).GetMethod("CurrentProjectSettings", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
                foreach (bool configure in new[] { false, true })
                {
                    var settings = current.Clone();
                    if (!configure) { settings.Sets = null; settings.Slots = new[] { 0, 2 }; }
                    dialog.Settings = settings; dialog.Configure = configure;
                    string path = Path.Combine(Snapshots, (configure ? "configure-" : "new-project-") + tag + ".png");
                    OffscreenGui.RenderToPng((int)NewProjectWindow.Width, (int)NewProjectWindow.Height, () => dialog.DrawContent(new Rect(0, 0, NewProjectWindow.Width, NewProjectWindow.Height)), path, PaintTheme.PanelBg);
                    Assert.That(File.Exists(path), Is.True);
                    var taken = dialog.TakeSettings();
                    if (configure) Assert.That(taken.Sets.Select(d => (d.Id, d.Name, d.Slot)), Is.EqualTo(window.TextureSets.Select(t => (t.Id, t.Name, t.MaterialSlot))), "drawing does not change the drafts");
                    else Assert.That(taken.Slots, Is.EqualTo(new[] { 0, 2 }), "drawing does not change the checked slots");
                }
            }
            finally { Object.DestroyImmediate(dialog); }
        }

        [Test] public void ThePanelDrawsOneAndThreeTextureSetsInBothLanguagesAndANarrowDock()
        {
            RequireOffscreen();
            var model = TextureSetModels.Prefab(folder, "Panel", "Body", "Hair with a rather long material name", "Eyes");
            foreach (int count in new[] { 1, 3 })
            {
                window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Template = ProjectTemplate.LilToon, Slots = Enumerable.Range(0, count).ToArray() });
                var sets = window.TextureSets.ToList();
                for (int i = 0; i < sets.Count; i++) Flood(sets[i].Document, PaintChannel.Color, new Rgba32((byte)(60 + 60 * i), 90, (byte)(200 - 60 * i)));
                if (count == 3) { window.SwitchTextureSet(sets[1].Id); window.SetTextureSetSlot(sets[2].Id, 7); }
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    string tag = count + "-" + (language == PainterLanguage.English ? "en" : "ja");
                    RenderPanel(300, "panel-" + tag);
                    RenderPanel(190, "panel-narrow-" + tag);
                    if (count == 3) OffscreenGui.RenderWindow(window, 1200, 800, Path.Combine(Snapshots, "window-" + tag + ".png"));
                    if (count == 3) RenderDialogs(model, tag);
                }
                L.OverrideLanguage(PainterLanguage.English);
            }
        }

        // ───────── ベイク ─────────

        [Test] public void TheBakeWindowBakesTheCheckedTextureSetsInTurn()
        {
            var sets = NewProject(TextureSetModels.Prefab(folder, "Bake", "Body", "Hair"), ProjectTemplate.ColorOnly);
            window.MeshBakeUseGpu = false; window.MeshBakeSettings.Maps = new[] { MeshMapKind.WorldNormal, MeshMapKind.Position };
            var bake = ScriptableObject.CreateInstance<MeshBakeWindow>();
            try
            {
                bake.Attach(window);
                Assert.That(window.MeshBakeTargets().Select(t => (t.Name, t.Slot, t.Bake, t.Fixed)), Is.EqualTo(new[] { ("Body", 0, true, false), ("Hair", 1, true, false) }), "every set, checked");
                if (Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        OffscreenGui.RenderToPng(1100, 760, () => window.DrawMeshBakeWindow(new Rect(0, 0, 1100, 760), bake), Path.Combine(Snapshots, "bake-" + language + ".png"), PaintTheme.PanelBg);
                    }
                L.OverrideLanguage(PainterLanguage.English);
                Assert.That(window.StartMeshBake(), Is.Null, window.StatusMessage);
                PumpUntilDone();
                Assert.That(sets.Select(s => s.MeshMaps.Count), Is.EqualTo(new[] { 2, 2 }), window.MeshBakeOutcome);
                Assert.That(sets.Select(s => s.MeshMaps.Maps.Select(m => m.Provenance.TargetSlot).Distinct().Single()), Is.EqualTo(new[] { 0, 1 }), "each set's maps are baked for its slot");
                Assert.That(window.MeshBakeOutcome, Does.Contain("2 texture sets"));
                // チェックを外したセットは焼かない（最後の 1 つは外せない）
                window.SetMeshBakeTarget(0, false); window.SetMeshBakeTarget(1, false);
                Assert.That(window.MeshBakeTargets().Select(t => (t.Bake, t.Fixed)), Is.EqualTo(new[] { (false, false), (true, true) }));
                long bodyRevision = sets[0].MeshMaps.Revision, hairRevision = sets[1].MeshMaps.Revision;
                Assert.That(window.StartMeshBake(), Is.Null);
                PumpUntilDone();
                Assert.That(sets[0].MeshMaps.Revision, Is.EqualTo(bodyRevision), "an unchecked set is not baked");
                Assert.That(sets[1].MeshMaps.Revision, Is.Not.EqualTo(hairRevision));
                // 焼いているあいだにセットを消すと、その結果は使わない
                window.SetMeshBakeTarget(0, true);
                Assert.That(window.StartMeshBake(), Is.Null);
                Assert.That(window.RemoveTextureSet(sets[0].Id), Is.True);
                PumpUntilDone();
                Assert.That(window.MeshBakeOutcome, Does.Contain("texture set").Or.Contain("Baked"), window.StatusMessage);
                Assert.That(window.TextureSets.Single().MeshMaps.Count, Is.EqualTo(2));
            }
            finally { Object.DestroyImmediate(bake); }
        }

        void PumpUntilDone()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (window.PumpMeshBake(20)) { Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(300), "the bake did not finish"); System.Threading.Thread.Sleep(2); }
        }
    }

    /// <summary>テクスチャセットの実際のウィンドウでの入力（GUI モード）: 3D ビューでほかのセットの面には描かない、パネルで押すと切り替わり、
    /// Ctrl+Z は今のセットだけを戻す。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>.ylp（か復旧 checkpoint）の今のテクスチャセットのエントリ（sets/&lt;ID&gt;/ を取った名前）。</summary>
        static Dictionary<string, byte[]> SetFiles(IReadOnlyDictionary<string, byte[]> files)
        {
            var opened = YlpFormat.Open(files);
            return opened.SetFiles(opened.Project.CurrentSet);
        }

        /// <summary>テクスチャセットのパネルの部品の中の点（SendEvent が受け取るホストの座標。<see cref="LayerControlPoint"/> と同じ考え方）。</summary>
        void ClickTextureSetControl(string id)
        {
            Repaint(window); Repaint(window);
            Assert.That(window.TextureSetScreenRects.TryGetValue(id, out var screen), Is.True, id + " was not drawn");
            var host = new Rect(screen.position - HostScreenPosition(window), screen.size);
            HostMouse(EventType.MouseDown, host.center); HostMouse(EventType.MouseUp, host.center); Repaint(window);
        }

        string TextureSetFolder()
        {
            string name = "WindowTextureSetTests-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", name), Is.Not.Empty);
            return "Assets/" + name;
        }

        /// <summary>3D ビューの中で、slot の面が下にある点（GUI の座標）。</summary>
        Vector2 SurfacePointOnSlot(int slot)
        {
            Repaint(window);
            var r = window.SurfaceRect;
            for (int y = 1; y < 40; y++)
                for (int x = 1; x < 40; x++)
                {
                    var p = new Vector2(r.x + r.width * x / 40f, r.y + r.height * y / 40f);
                    if (window.Preview.TryPick(r, p, out var hit) && hit.MaterialSlot == slot) return p;
                }
            Assert.Fail("no face of slot " + slot + " is visible in the 3D view");
            return default;
        }

        [Test] public void PaintingOnAnotherTextureSetsFaceIsRefusedAndNamesTheSet()
        {
            string folder = TextureSetFolder();
            try
            {
                var model = TextureSetModels.Prefab(folder, "Faces", "Body", "Hair");
                window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Template = ProjectTemplate.ColorOnly });
                window.View = TexturePaintWindow.ViewMode.Model; Repaint(window);
                var sets = window.TextureSets.ToList(); var before = DocumentBinary.Write(sets[1].Document);
                var hair = SurfacePointOnSlot(1);
                Mouse(window, EventType.MouseDown, hair); Mouse(window, EventType.MouseUp, hair);
                Assert.That(window.IsStroking, Is.False);
                Assert.That(window.StatusMessage, Does.Contain("belongs to the texture set Hair"));
                Assert.That(sets[0].Document.CanUndo, Is.False, "nothing was painted"); Assert.That(DocumentBinary.Write(sets[1].Document), Is.EqualTo(before));
                var body = SurfacePointOnSlot(0);
                Mouse(window, EventType.MouseDown, body); Mouse(window, EventType.MouseUp, body);
                Assert.That(sets[0].Document.CanUndo, Is.True, "the current set's faces are painted: " + window.StatusMessage);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test] public void DoubleClickingAnotherTextureSetsFaceSwitchesToItWithoutPainting()
        {
            string folder = TextureSetFolder();
            try
            {
                var model = TextureSetModels.Prefab(folder, "Faces", "Body", "Hair");
                window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Template = ProjectTemplate.ColorOnly });
                window.View = TexturePaintWindow.ViewMode.Model; Repaint(window);
                var sets = window.TextureSets.ToList(); var hair = SurfacePointOnSlot(1);
                var offset = window.rootVisualElement.worldBound.position;
                window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = hair + offset, button = 0, clickCount = 2 });
                window.SendEvent(new Event { type = EventType.MouseUp, mousePosition = hair + offset, button = 0 });
                Assert.That(window.CurrentTextureSet.Name, Is.EqualTo("Hair"), window.StatusMessage);
                Assert.That(window.IsStroking, Is.False);
                Assert.That(sets[0].Document.CanUndo || sets[1].Document.CanUndo, Is.False, "switching paints nothing");
                Mouse(window, EventType.MouseDown, hair); Mouse(window, EventType.MouseUp, hair);
                Assert.That(sets[1].Document.CanUndo, Is.True, "now the hair is painted with a single click");
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test] public void ClickingATextureSetInThePanelSwitchesAndCtrlZUndoesOnlyThatSet()
        {
            string folder = TextureSetFolder();
            try
            {
                var model = TextureSetModels.Prefab(folder, "Click", "Body", "Hair");
                window.CreateProject(new NewProjectSettings { Model = model, Resolution = 1024, Template = ProjectTemplate.ColorOnly });
                window.View = TexturePaintWindow.ViewMode.Split; Repaint(window);
                var sets = window.TextureSets.ToList();
                PaintDot(window, 300, 300);
                ClickTextureSetControl("textureSet.1");
                Assert.That(window.CurrentTextureSet, Is.SameAs(sets[1]), window.StatusMessage);
                PaintDot(window, 600, 600);
                Key(window, KeyCode.Z, EventModifiers.Control);
                Assert.That(sets[1].Document.CanUndo, Is.False); Assert.That(sets[0].Document.CanUndo, Is.True, "Ctrl+Z undoes the current set only");
                Assert.That(sets[0].Document.CompositePixel(PaintChannel.Color, 300, 300).A, Is.GreaterThan((byte)0));
                ClickTextureSetControl("textureSet.0");
                Assert.That(window.CurrentTextureSet, Is.SameAs(sets[0]));
                Key(window, KeyCode.Z, EventModifiers.Control);
                Assert.That(sets[0].Document.CompositePixel(PaintChannel.Color, 300, 300).A, Is.Zero);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
