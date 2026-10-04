using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>テストのモデル: マテリアルを共有するメッシュ（アバターのように体と頭が別のレンダラーで同じマテリアル、1 つのレンダラーの
    /// 2 つのサブメッシュが同じマテリアル、マテリアルの無いレンダラー 2 つ）。</summary>
    internal static class MaterialSetModels
    {
        /// <summary>メッシュ 1 つ分: 名前とサブメッシュごとのマテリアル（null は無し）と UV の矩形（u0, v0, u1, v1）。</summary>
        internal sealed class Part { public string Name; public Material[] Materials; public Rect[] Uvs; public Vector3 Offset; }

        /// <summary>
        /// Avatar: Body（Skin・Cloth）、Head（Skin）、Hair（Hair・Hair）、Prop（無し）、Prop2（無し）。平らにしたスロットは
        /// 0 Body/Skin・1 Body/Cloth・2 Head/Skin・3 Hair/Hair・4 Hair/Hair・5 Prop・6 Prop2、マテリアルの組は Skin [0, 2]・Cloth [1]・
        /// Hair [3, 4]・Unassigned [5, 6]。同じマテリアルのスロットは UV の別の所（Skin は下半分と上半分）。
        /// </summary>
        public static GameObject Avatar(string folder, string name = "Avatar", Rect? bodySkinUv = null, Vector3 bodyOffset = default)
        {
            var skin = MaterialAsset(folder, name, "Skin"); var cloth = MaterialAsset(folder, name, "Cloth"); var hair = MaterialAsset(folder, name, "Hair");
            Rect Low(float u = 0) => new Rect(.05f + u, .05f, .4f, .4f); Rect High(float u = 0) => new Rect(.05f + u, .55f, .4f, .4f);
            return Prefab(folder, name,
                new Part { Name = "Body", Materials = new[] { skin, cloth }, Uvs = new[] { bodySkinUv ?? Low(), Low(.5f) }, Offset = bodyOffset },
                new Part { Name = "Head", Materials = new[] { skin }, Uvs = new[] { High() } },
                new Part { Name = "Hair", Materials = new[] { hair, hair }, Uvs = new[] { Low(), High(.5f) } },
                new Part { Name = "Prop", Materials = new Material[] { null }, Uvs = new[] { Low() } },
                new Part { Name = "Prop2", Materials = new Material[] { null }, Uvs = new[] { High(.5f) } });
        }

        /// <summary>1 つのメッシュの 3 つのサブメッシュに、materials の 3 つのマテリアル（同じものを 2 回入れてもよい）。形式 5 のフィクスチャを
        /// 作ったモデルと同じ形（Skin・Skin・Hair）。</summary>
        public static GameObject ThreeSlots(string folder, string name, params Material[] materials)
            => Prefab(folder, name, new Part { Name = name + "Mesh", Materials = materials, Uvs = Enumerable.Range(0, materials.Length).Select(i => new Rect(.1f * i, 0, .3f, .5f)).ToArray() });

        public static Material MaterialAsset(string folder, string prefix, string name)
        {
            string sub = folder + "/" + prefix + "-" + name;
            if (!AssetDatabase.IsValidFolder(sub)) Assert.That(AssetDatabase.CreateFolder(folder, prefix + "-" + name), Is.Not.Empty);
            var material = new Material(Shader.Find("Unlit/Texture"));
            AssetDatabase.CreateAsset(material, sub + "/" + name + ".mat");
            return material;
        }

        public static GameObject Prefab(string folder, string name, params Part[] parts)
        {
            var root = new GameObject(name);
            try
            {
                int index = 0;
                foreach (var part in parts)
                {
                    var vertices = new List<Vector3>(); var uvs = new List<Vector2>();
                    var mesh = new Mesh { name = name + "-" + part.Name, subMeshCount = part.Materials.Length };
                    for (int s = 0; s < part.Materials.Length; s++, index++)
                    {
                        float x = index * 1.2f; var r = part.Uvs[s]; var o = part.Offset;
                        vertices.AddRange(new[] { new Vector3(x, 0, 0) + o, new Vector3(x + 1, 0, 0) + o, new Vector3(x + 1, 1, 0) + o, new Vector3(x, 1, 0) + o });
                        uvs.AddRange(new[] { new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax) });
                    }
                    mesh.SetVertices(vertices); mesh.SetUVs(0, uvs);
                    for (int s = 0; s < part.Materials.Length; s++) { int b = s * 4; mesh.SetTriangles(new[] { b, b + 2, b + 1, b, b + 3, b + 2 }, s); }
                    mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
                    AssetDatabase.CreateAsset(mesh, folder + "/" + name + "-" + part.Name + ".asset");
                    var child = new GameObject(part.Name); child.transform.SetParent(root.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = mesh; child.AddComponent<MeshRenderer>().sharedMaterials = part.Materials;
                }
                return PrefabUtility.SaveAsPrefabAsset(root, folder + "/" + name + ".prefab");
            }
            finally { Object.DestroyImmediate(root); }
        }
    }

    /// <summary>
    /// テクスチャセットはマテリアルごと（Substance Painter と同じ）: 同じマテリアルを使う 2 つのレンダラー・同じレンダラーの 2 つのサブメッシュが
    /// 1 つのセットになり、両方に見せる・両方を焼く。マテリアルの無いスロットは 1 つの Unassigned。セットの鍵（GUID と localFileId、名前）の
    /// 保存と照合（識別子 → 名前）、形式 5 のファイル（スロットの番号）を開いて同じセットにし、2 つが同じマテリアルに落ちれば片方を
    /// 「モデルに無い」で残して知らせる。新規プロジェクトとプロジェクト設定の一覧の描画（英日）。ウィンドウは表示せずに作るので batch でも動く。
    /// </summary>
    public sealed class MaterialSetTests
    {
        string folder, project; TexturePaintWindow window;
        readonly List<TexturePaintWindow> others = new List<TexturePaintWindow>();
        readonly List<string> temporary = new List<string>();
        static string Snapshots => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "material-sets"));

        sealed class Dialogs : IPainterDialogs
        {
            public string File = "";
            public bool ConfirmAnswer = true;
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
            PainterSettings.ProjectRoot = project;
            string name = "MaterialSetTests-" + Guid.NewGuid().ToString("N");
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

        string Temp(string extension = "") { string path = Path.Combine(Path.GetTempPath(), "yolupainter-materialsets-" + Guid.NewGuid().ToString("N") + extension); temporary.Add(path); return path; }

        TexturePaintWindow NewWindow()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            w.Dialogs = new Dialogs(); w.MeshBakeProgress = (title, info, progress) => false;
            if (window != null) others.Add(w);
            return w;
        }
        static Dialogs DialogsOf(TexturePaintWindow w) => (Dialogs)w.Dialogs;

        static void Flood(PaintDocument d, Rgba32 color)
        {
            var rgba = new byte[d.Width * d.Height * 4];
            for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = color.R; rgba[i + 1] = color.G; rgba[i + 2] = color.B; rgba[i + 3] = color.A; }
            d.ReplacePixels(d.Layers[0].Id, PaintChannel.Color, rgba, withinSelection: false);
        }

        // ───────── マテリアルごとのセット ─────────

        [Test] public void MeshesThatShareAMaterialAreOneTextureSet()
        {
            var model = MaterialSetModels.Avatar(folder);
            window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Template = ProjectTemplate.ColorOnly });
            var groups = window.Preview.MaterialGroups;
            Assert.That(window.Preview.MaterialSlotCount, Is.EqualTo(7));
            Assert.That(groups.Select(g => (g.Name, string.Join(",", g.Slots), string.Join(",", g.Meshes), g.Unassigned)), Is.EqualTo(new[]
            {
                ("Skin", "0,2", "Body,Head", false), ("Cloth", "1", "Body", false), ("Hair", "3,4", "Hair", false), ("Unassigned", "5,6", "Prop,Prop2", true),
            }), "two renderers with one material, two submeshes with one material, and the slots without a material are one group each");
            var sets = window.TextureSets.ToList();
            Assert.That(sets.Select(s => (s.Name, string.Join(",", s.Slots))), Is.EqualTo(new[] { ("Skin", "0,2"), ("Cloth", "1"), ("Hair", "3,4"), ("Unassigned", "5,6") }));
            Assert.That(sets[0].Material.Name, Is.EqualTo("Skin")); Assert.That(sets[0].Material.AssetGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(folder + "/Avatar-Skin/Skin.mat")));
            Assert.That(sets[3].Material, Is.EqualTo(YlpMaterialRef.UnassignedSlots));
            Assert.That(new[] { 0, 1, 2, 3, 4, 5, 6 }.Select(window.PaintsSlot), Is.EqualTo(new[] { true, false, true, false, false, false, false }), "the current set paints both meshes of its material");
            Assert.That(window.Preview.Geometry.Triangles.Select(t => t.Material).Distinct().OrderBy(m => m), Is.EqualTo(new[] { 0, 1, 2, 3 }), "every triangle knows its material group");
            Assert.That(window.CurrentUvEdges().Length / 2, Is.EqualTo(10), "the UV wireframe of both Skin quads (5 edges each)");

            // 3D の表示: 今のセットの表示は両方のスロットに、ほかのセットも両方のスロットに
            Flood(sets[0].Document, new Rgba32(200, 120, 90));
            window.RefreshPreviewTextures();
            var preview = window.Preview;
            Assert.That(preview.ShownTexture(0), Is.SameAs(window.DisplayTexture)); Assert.That(preview.ShownTexture(2), Is.SameAs(window.DisplayTexture));
            Assert.That(preview.ShownTexture(1), Is.Not.SameAs(window.DisplayTexture));
            window.SwitchTextureSet(sets[1].Id); window.RefreshPreviewTextures();
            Assert.That(preview.ShownTexture(1), Is.SameAs(window.DisplayTexture));
            var skinDisplay = window.SetDisplay(sets[0]);
            Assert.That(preview.ShownTexture(0), Is.SameAs(skinDisplay)); Assert.That(preview.ShownTexture(2), Is.SameAs(skinDisplay), "another set's composite goes to every slot of its material");
            Assert.That(preview.ShownTexture(3), Is.SameAs(preview.ShownTexture(4)));
            Assert.That(preview.ShownTexture(5), Is.SameAs(preview.ShownTexture(6)), "the unassigned slots share one texture set");

            // 書き出しのパディングの範囲は両方のメッシュの UV
            var coverage = window.ExportCoverage(sets[0]);
            Assert.That(coverage[100 * 512 + 100] && coverage[400 * 512 + 100], Is.True, "the UVs of Body and Head");
            Assert.That(coverage[100 * 512 + 400], Is.False, "Cloth's UVs are another set");
        }

        [Test] public void BakingATextureSetBakesEveryMeshOfItsMaterial()
        {
            var model = MaterialSetModels.Avatar(folder);
            window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Template = ProjectTemplate.ColorOnly });
            window.MeshBakeUseGpu = false; window.MeshBakeSettings.Maps = new[] { MeshMapKind.WorldNormal, MeshMapKind.Position };
            var sets = window.TextureSets.ToList();
            foreach (var set in sets.Skip(1)) window.SetMeshBakeTarget(set.Id, false);
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), window.StatusMessage);
            var map = sets[0].MeshMaps.Maps.First();
            bool Baked(float u, float v) { var c = map.CoverageAt((int)(u * 512), (int)(v * 512)); return c == MeshTexelCoverage.Covered || c == MeshTexelCoverage.Overlap; }
            Assert.That(Baked(.25f, .25f), Is.True, "Body's skin");
            Assert.That(Baked(.25f, .75f), Is.True, "Head's skin, another renderer of the same material");
            Assert.That(Baked(.75f, .25f), Is.False, "Body's cloth is another material");
            Assert.That(map.Provenance.TargetSlots, Is.EqualTo(new[] { 0, 2 }));
            Assert.That(sets[0].MeshMaps.Check(map.Kind, window.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current));
            // マテリアルの無いスロットの組も 1 つのセットとして焼く
            window.SetMeshBakeTarget(sets[3].Id, true); window.SetMeshBakeTarget(sets[0].Id, false); window.SwitchTextureSet(sets[3].Id);
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), window.StatusMessage);
            Assert.That(sets[3].MeshMaps.Maps.First().Provenance.TargetSlots, Is.EqualTo(new[] { 5, 6 }));
            Assert.That(sets[3].MeshMaps.Maps.First().CoverageAt(384, 384), Is.Not.EqualTo(MeshTexelCoverage.Empty), "Prop2's quad");
        }

        // ───────── 鍵の保存と照合 ─────────

        [Test] public void SavingAndOpeningKeepsTheMaterialsAndMatchesThemByIdentityThenName()
        {
            var model = MaterialSetModels.Avatar(folder);
            window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Template = ProjectTemplate.ColorOnly });
            var sets = window.TextureSets.ToList();
            window.RenameTextureSet(sets[0].Id, "Face and body");
            Flood(sets[0].Document, new Rgba32(10, 20, 30));
            string path = Path.Combine(Temp(), "Avatar.ylp"); Directory.CreateDirectory(Path.GetDirectoryName(path));
            DialogsOf(window).File = path; window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            string json = System.Text.Encoding.UTF8.GetString(YlpStore.Load(path).Files[YlpFormat.ProjectName]);
            Assert.That(json, Does.Contain("\"material\": { \"name\": \"Skin\", \"guid\": \"" + AssetDatabase.AssetPathToGUID(folder + "/Avatar-Skin/Skin.mat") + "\"").And.Contain("\"unassigned\": true"));

            var other = NewWindow(); other.OpenProjectAt(path);
            Assert.That(other.TextureSets.Select(s => (s.Name, string.Join(",", s.Slots))), Is.EqualTo(new[] { ("Face and body", "0,2"), ("Cloth", "1"), ("Hair", "3,4"), ("Unassigned", "5,6") }), other.StatusMessage);
            Assert.That(other.IsSaved, Is.True);

            // マテリアルの名前を変えても、識別子で同じマテリアルに付く
            Assert.That(AssetDatabase.RenameAsset(folder + "/Avatar-Skin/Skin.mat", "Skin renamed"), Is.Empty);
            var renamed = NewWindow(); renamed.OpenProjectAt(path);
            Assert.That(renamed.TextureSets[0].Slots, Is.EqualTo(new[] { 0, 2 }), "matched by GUID and local file ID: " + renamed.StatusMessage);
            // 識別子が合わなければ名前で（別のプレハブの同じ名前のマテリアル）
            var copy = MaterialSetModels.Avatar(folder, "Copy");
            var byName = NewWindow(); byName.OpenProjectAt(path); byName.SetModel(copy);
            Assert.That(byName.TextureSets.Select(s => string.Join(",", s.Slots)), Is.EqualTo(new[] { "0,2", "1", "3,4", "5,6" }), "another prefab's materials of the same names: " + byName.StatusMessage);
            // 名前も識別子も合わないマテリアルのセットはモデルに無い（鍵は残る）
            var stranger = MaterialSetModels.ThreeSlots(folder, "Stranger", MaterialSetModels.MaterialAsset(folder, "S", "Metal"), MaterialSetModels.MaterialAsset(folder, "S", "Glass"), null);
            byName.SetModel(stranger);
            Assert.That(byName.TextureSets.Take(4).Select(s => string.Join(",", s.Slots)), Is.EqualTo(new[] { "", "", "", "2" }), "only the unassigned slots match");
            Assert.That(byName.TextureSets.Skip(4).Select(s => s.Name), Is.EqualTo(new[] { "Metal", "Glass" }), "the new materials got sets (the test's dialogs say yes)");
            Assert.That(byName.TextureSets[1].Material.Name, Is.EqualTo("Cloth"), "the key is kept for the next model");
        }

        /// <summary>
        /// 形式 5 のフィクスチャ（スロット 0・1・2 の 3 つのセット Skin・Skin 2・Hair）を、そのスロットの並びのモデルで開く: 3 つのマテリアルが
        /// 別なら同じ 3 つのセット。スロット 0 と 1 が同じマテリアルなら、Skin はそのマテリアル、Skin 2 は捨ても混ぜもせず「モデルに無い」で残して
        /// 知らせる（番号の鍵のまま保存し、別のマテリアルを選べる）。
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void AFormatFiveFileOpensOnTheMaterialsOfItsSlots(bool twins)
        {
            var a = MaterialSetModels.MaterialAsset(folder, "F5", "Skin");
            var b = twins ? a : MaterialSetModels.MaterialAsset(folder, "F5", "Back");
            var c = MaterialSetModels.MaterialAsset(folder, "F5", "Hair");
            var model = MaterialSetModels.ThreeSlots(folder, "Fixture", a, b, c);
            // フィクスチャの view.json のモデルの GUID を、このモデルに替えた写し（manifest は書き直す）
            var files = YlpArchive.Read(File.ReadAllBytes(PackagePaths.Physical("Tests/Editor/Persistence/Fixtures~/format5-shared-materials.ylp")));
            files[YlpFormat.ViewName] = System.Text.Encoding.UTF8.GetBytes("{ \"modelAssetGuid\": \"" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(model)) + "\", \"selectedChannel\": 0 }");
            string path = Temp(".ylp"); File.WriteAllBytes(path, YlpArchive.Write(files));
            window.OpenProjectAt(path);
            Assert.That(window.OpenedFormat, Is.EqualTo(5), window.StatusMessage);
            var sets = window.TextureSets.ToList();
            Assert.That(sets.Select(s => s.Name), Is.EqualTo(new[] { "Skin", "Skin 2", "Hair" }));
            if (!twins)
            {
                Assert.That(sets.Select(s => string.Join(",", s.Slots)), Is.EqualTo(new[] { "0", "1", "2" }), "each slot's material: the same sets as before");
                Assert.That(sets.Select(s => s.Material.Name), Is.EqualTo(new[] { "Skin", "Back", "Hair" }), "the slot numbers became material keys");
            }
            else
            {
                Assert.That(sets.Select(s => string.Join(",", s.Slots)), Is.EqualTo(new[] { "0,1", "", "2" }), "Skin takes the shared material");
                Assert.That(sets[1].Material, Is.EqualTo(YlpMaterialRef.PendingSlot(1)), "Skin 2 keeps its slot number, so nothing is lost");
                Assert.That(window.StatusMessage, Does.Contain("Skin and Skin 2 both painted").And.Contain("not shown on the model"));
                Assert.That(DocumentBinary.Write(sets[1].Document), Is.EqualTo(DocumentBinary.Read(YlpFormat.Open(files).SetFiles(sets[1].Id)[YlpArchive.NativeName]) is PaintDocument d ? DocumentBinary.Write(d) : null),
                    "its pixels are kept");
            }
            // 保存して開くと同じ
            DialogsOf(window).File = path; window.SaveProject(false);
            var again = NewWindow(); again.OpenProjectAt(path);
            Assert.That(again.OpenedFormat, Is.EqualTo(YlpFormat.Current), again.StatusMessage);
            Assert.That(again.TextureSets.Select(s => (s.Name, string.Join(",", s.Slots), s.Material)), Is.EqualTo(window.TextureSets.Select(s => (s.Name, string.Join(",", s.Slots), s.Material))));
        }

        // ───────── 一覧の描画 ─────────

        [Test] public void TheNewProjectAndConfigurationListsShowMaterialsWithTheirMeshesInBothLanguages()
        {
            var model = MaterialSetModels.Avatar(folder);
            window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Template = ProjectTemplate.ColorOnly });
            var dialog = ScriptableObject.CreateInstance<NewProjectWindow>();
            try
            {
                foreach (bool configure in new[] { false, true })
                {
                    var settings = (NewProjectSettings)typeof(TexturePaintWindow).GetMethod("CurrentProjectSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(window, null);
                    if (!configure) { settings.Sets = null; settings.Materials = new[] { 0, 2, 3 }; }
                    dialog.Settings = settings; dialog.Configure = configure;
                    Assert.That(dialog.Preview.MaterialGroups.Count, Is.EqualTo(4));
                    if (Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                        foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                        {
                            L.OverrideLanguage(language);
                            string png = Path.Combine(Snapshots, (configure ? "configure-" : "new-project-") + language + ".png");
                            OffscreenGui.RenderToPng((int)NewProjectWindow.Width, (int)NewProjectWindow.Height, () => dialog.DrawContent(new Rect(0, 0, NewProjectWindow.Width, NewProjectWindow.Height)), png, PaintTheme.PanelBg);
                            Assert.That(File.Exists(png), Is.True);
                        }
                    L.OverrideLanguage(PainterLanguage.English);
                    var taken = dialog.TakeSettings();
                    if (configure) Assert.That(taken.Sets.Select(d => d.Material), Is.EqualTo(new[] { 0, 1, 2, 3 }), "each set's material group");
                    else Assert.That(taken.Materials, Is.EqualTo(new[] { 0, 2, 3 }));
                }
                // 新規: 選んだマテリアルだけがセットになる
                window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Materials = new[] { 2, 0 } });
                Assert.That(window.TextureSets.Select(s => s.Name), Is.EqualTo(new[] { "Skin", "Hair" }));
            }
            finally { L.OverrideLanguage(PainterLanguage.English); Object.DestroyImmediate(dialog); }
        }
    }

    /// <summary>マテリアルごとのセットの実際の入力（GUI モード）: 同じマテリアルの 2 つのメッシュのどちらに描いても同じセットに入り、Ctrl+Z で戻る。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void PaintingOnEitherMeshOfASharedMaterialPaintsOneTextureSet()
        {
            string folder = TextureSetFolder();
            try
            {
                var model = MaterialSetModels.Avatar(folder);
                window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512, Template = ProjectTemplate.ColorOnly });
                window.View = TexturePaintWindow.ViewMode.Model; Repaint(window);
                var sets = window.TextureSets.ToList(); var skin = sets[0];
                Assert.That(window.CurrentTextureSet, Is.SameAs(skin));
                var cloth = DocumentBinary.Write(sets[1].Document);
                var head = SurfacePointOnSlot(2);
                Mouse(window, EventType.MouseDown, head); Mouse(window, EventType.MouseUp, head);
                Assert.That(skin.Document.CanUndo, Is.True, "Head is a mesh of the Skin set: " + window.StatusMessage);
                int afterHead = skin.Document.Layers[0].GetChannel(PaintChannel.Color).TileCount;
                Assert.That(afterHead, Is.GreaterThan(0));
                var body = SurfacePointOnSlot(0);
                Mouse(window, EventType.MouseDown, body); Mouse(window, EventType.MouseUp, body);
                Assert.That(skin.Document.UndoCount, Is.EqualTo(2), "Body is the same set");
                var clothFace = SurfacePointOnSlot(1);
                Mouse(window, EventType.MouseDown, clothFace); Mouse(window, EventType.MouseUp, clothFace);
                Assert.That(window.StatusMessage, Does.Contain("belongs to the texture set Cloth"));
                Assert.That(DocumentBinary.Write(sets[1].Document), Is.EqualTo(cloth), "the other material's set is not painted");
                Key(window, KeyCode.Z, EventModifiers.Control); Key(window, KeyCode.Z, EventModifiers.Control);
                Assert.That(skin.Document.CanUndo, Is.False, "both strokes undo in the one set");
                Assert.That(skin.Document.Layers[0].GetChannel(PaintChannel.Color).TileCount, Is.Zero);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
