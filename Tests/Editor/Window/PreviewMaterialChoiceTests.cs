using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// テクスチャセットごとに、マテリアル表示で見せるもの（元のマテリアル・プロジェクトのマテリアル・シェーダーから作るマテリアル）と、
    /// チャンネルの流し込み先（確かめた対応・名前からの推し量り・手で決めたもの）。どれもプレビューの複製だけで、元のマテリアル・選んだ
    /// マテリアルのアセット（ファイル・dirty・Undo）は変わらない。消えたアセット・シェーダーは元のマテリアルに戻して知らせる。選びは窓の状態で、
    /// .ylp には入らない。1 つのチャンネル・メッシュマップだけの照明なしの見せ方も。窓は開かず、シェーダーで描くので batch-gl。
    /// </summary>
    [Category("GPU")]
    public sealed class PreviewMaterialChoiceTests
    {
        sealed class Answers : IPainterDialogs
        {
            public bool Confirm = true; public readonly List<string> Asked = new List<string>(); public string LastMessage; public string SavePath = "";
            public string SaveFolder(string title, string folder, string defaultName) => "";
            public string OpenFolder(string title, string folder) => "";
            public string OpenFile(string title, string folder, string extension) => "";
            public string SaveFile(string title, string folder, string defaultName, string extension) => SavePath;
            bool IPainterDialogs.Confirm(string title, string message, string ok, string cancel) { Asked.Add("Confirm: " + title); LastMessage = message; return Confirm; }
            public void Inform(string title, string message) { Asked.Add("Inform: " + title); LastMessage = message; }
            public void Progress(string title, string info, float progress) { }
            public void ClearProgress() { }
        }

        readonly List<Object> made = new List<Object>();
        TexturePaintWindow w; Answers answers; string folder; readonly List<string> temp = new List<string>();

        [SetUp] public void Open()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
            GpuTests.RequireWorkingShader(PreviewMaterialView.PackShaderName);
            w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            answers = new Answers(); w.Dialogs = answers;
        }

        [TearDown] public void Close()
        {
            string recovery = w != null ? w.RecoveryRoot : null;
            if (w != null) { w.MaterialEdits.RevertAll(); Object.DestroyImmediate(w); }
            if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
            foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            made.Clear();
            if (folder != null) { AssetDatabase.DeleteAsset(folder); folder = null; }
            foreach (var path in temp) if (File.Exists(path)) File.Delete(path);
            temp.Clear();
        }

        // ───────── 選び ─────────

        [Test] public void AProjectMaterialIsShownAsACopyWithThePaintedMapsAndNoAssetChanges()
        {
            var own = MaterialAsset("Own", "Unlit/Texture"); var chosen = MaterialAsset("Chosen", "Standard");
            var files = new[] { folder + "/Own.mat", folder + "/Own.mat.meta", folder + "/Chosen.mat", folder + "/Chosen.mat.meta" };
            string Hashes() { using (var sha = SHA256.Create()) return string.Join(",", files.Select(f => BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.GetFullPath(f)))))); }
            string State() => string.Join("|", new[] { own, chosen }.Select(m => EditorJsonUtility.ToJson(m) + string.Join(",", m.shaderKeywords) + EditorUtility.GetDirtyCount(m) + EditorUtility.IsDirty(m))) + "|" + PreviewMaterialTests.UndoRecords();
            w.SetModel(Model(own));
            Paint(w.Document, PaintChannel.Color, new Rgba32(230, 30, 20)); Paint(w.Document, PaintChannel.Roughness, new Rgba32(60, 60, 60)); Paint(w.Document, PaintChannel.Normal, new Rgba32(200, 128, 220));
            w.Document.AddFillLayer("Base", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(230, 30, 20) } });
            string hashes = Hashes(), state = State(); long revision = w.Document.Revision;

            Assert.That(w.UsePreviewMaterial(chosen), Is.True, w.StatusMessage);
            w.Channel = PaintChannel.Color; w.Shading = PreviewShading.Material; w.RefreshPreviewTextures();
            var p = w.Preview; var shown = p.RenderedMaterial(0);
            Assert.That(shown.shader, Is.SameAs(chosen.shader)); Assert.That(shown, Is.Not.SameAs(chosen), "a copy is drawn, never the asset");
            Assert.That(p.ViewMaterial(0), Is.SameAs(chosen)); Assert.That(p.SourceMaterial(0), Is.SameAs(own), "the slot's own material stays the source (lilToon assignment writes there)");
            var b = p.MaterialBinding(0);
            Assert.That(b.Kind, Is.EqualTo(PreviewMaterialKind.Standard)); Assert.That(b.Source, Is.SameAs(chosen));
            Assert.That(shown.GetTexture("_MetallicGlossMap"), Is.SameAs(p.MaterialPackedTexture(0, "_MetallicGlossMap")));
            Assert.That(shown.GetTexture("_BumpMap"), Is.Not.Null); Assert.That(shown.IsKeywordEnabled("_NORMALMAP"), Is.True);
            if (QualitySettings.activeColorSpace == ColorSpace.Gamma) Assert.That(shown.GetTexture("_MainTex"), Is.SameAs(w.Compositor.Texture), "the live composite of the painted channel");
            Assert.That(w.PanelMaterial, Is.SameAs(chosen), "the Material panel shows the chosen material");
            Assert.That(w.MaterialChoiceLabel(), Does.Contain("Chosen"));
            var image = p.RenderStatic(64, 64); made.Add(image);
            var center = image.GetPixel(32, 32);
            Assert.That(center.r, Is.GreaterThan(center.g + .15f), "the painted red shows through the chosen Standard material: " + center);
            Assert.That(w.Document.Revision, Is.EqualTo(revision), "choosing is not a document change");

            Assert.That(w.UseOriginalMaterial(), Is.True); w.RefreshPreviewTextures();
            Assert.That(p.RenderedMaterial(0).shader, Is.SameAs(own.shader)); Assert.That(w.MaterialChoice(), Is.Null, "nothing left to remember");
            Assert.That(State(), Is.EqualTo(state), "the materials in memory, their dirty counts and Unity's Undo are untouched");
            AssetDatabase.SaveAssets();
            Assert.That(Hashes(), Is.EqualTo(hashes), "the .mat files and their .meta files are unchanged");
        }

        [Test] public void AShaderMakesAPreviewOnlyMaterialThatEvenTheDemoCubeCanUse()
        {
            w.Preview.LoadDemoMesh();
            Paint(w.Document, PaintChannel.Color, new Rgba32(20, 200, 40));
            w.Shading = PreviewShading.Material; w.RefreshPreviewTextures();
            Assert.That(w.Preview.MaterialBinding(0).CanShow, Is.False, "the demo cube has no material of its own");
            var standard = Shader.Find("Standard");
            Assert.That(w.UsePreviewShader(standard), Is.True, w.StatusMessage); w.RefreshPreviewTextures();
            var madeMaterial = w.PanelMaterial;
            Assert.That(madeMaterial, Is.Not.Null); Assert.That(madeMaterial.shader, Is.SameAs(standard));
            Assert.That(EditorUtility.IsPersistent(madeMaterial), Is.False, "made in memory for the preview only");
            Assert.That(w.Preview.RenderedMaterial(0).shader, Is.SameAs(standard)); Assert.That(w.Preview.MaterialBinding(0).Kind, Is.EqualTo(PreviewMaterialKind.Standard));
            // マテリアルの欄の変更は作ったマテリアルの複製に効き、「マテリアルに反映…」は断る
            w.SetMaterialValue(madeMaterial, w.MaterialProperties(madeMaterial).Single(x => x.Name == "_Glossiness"), new Vector4(.2f, 0, 0, 0));
            w.RefreshPreviewTextures();
            Assert.That(w.Preview.RenderedMaterial(0).GetFloat("_Glossiness"), Is.EqualTo(.2f));
            w.ApplyMaterialEdits();
            Assert.That(answers.Asked, Is.EqualTo(new[] { "Inform: Cannot apply to the material" })); Assert.That(answers.LastMessage, Does.Contain("made from a shader"));
            // 別のシェーダーにすると前に作ったものと、その欄の変更は捨てる
            Assert.That(w.UsePreviewShader(Shader.Find("Unlit/Texture")), Is.True);
            Assert.That(madeMaterial == null, Is.True, "the former preview material is destroyed"); Assert.That(w.MaterialEdits.Count, Is.Zero);
            var second = w.PanelMaterial;
            Object.DestroyImmediate(w); w = null;
            Assert.That(second == null, Is.True, "closing the window destroys the made material");
        }

        // ───────── 流し込み先 ─────────

        [Test] public void RoutesSetByHandReplaceTheAutomaticOnesAndBadOnesAreRefused()
        {
            var standard = Track(new Material(Shader.Find("Standard")));
            w.SetModel(Model(standard));
            Paint(w.Document, PaintChannel.Height, new Rgba32(200, 200, 200)); Paint(w.Document, PaintChannel.Normal, new Rgba32(200, 128, 220)); Paint(w.Document, PaintChannel.Color, new Rgba32(200, 20, 20));
            w.Shading = PreviewShading.Material; w.RefreshPreviewTextures();
            var p = w.Preview; long revision = w.Document.Revision; bool canUndo = w.Document.CanUndo;
            Assert.That(p.MaterialBinding(0).For(PaintChannel.Height).Property, Is.EqualTo("_ParallaxMap"));

            Assert.That(w.SetChannelRoute(PaintChannel.Height, "_DetailMask", PreviewPacking.Value), Is.True, w.StatusMessage);
            Assert.That(w.SetChannelRoute(PaintChannel.Normal, ""), Is.True);
            w.RefreshPreviewTextures();
            var b = p.MaterialBinding(0);
            Assert.That(b.HandSet, Is.True); Assert.That(b.For(PaintChannel.Height).Property, Is.EqualTo("_DetailMask"));
            Assert.That(b.For(PaintChannel.Normal), Is.Null); Assert.That(b.Unmapped.Single(u => u.Channel == PaintChannel.Normal).Reason, Does.Contain("turned off"));
            var shown = p.RenderedMaterial(0);
            Assert.That(shown.GetTexture("_DetailMask"), Is.SameAs(p.MaterialPackedTexture(0, "_DetailMask"))); Assert.That(shown.GetTexture("_DetailMask"), Is.Not.Null);
            Assert.That(shown.GetTexture("_ParallaxMap"), Is.Null, "Height left the parallax map"); Assert.That(shown.GetTexture("_BumpMap"), Is.Null);
            Assert.That(shown.IsKeywordEnabled("_NORMALMAP"), Is.False);
            var packed = Read(p.MaterialPackedTexture(0, "_DetailMask"));
            Assert.That(packed.Max(c => c.r), Is.EqualTo(200), "Height packed as value × alpha");

            // 使えない流し込み先は断る（選びは変わらない）
            Assert.That(w.SetChannelRoute(PaintChannel.Color, "_Nope"), Is.False); Assert.That(w.StatusMessage, Does.Contain("has no property"));
            Assert.That(w.SetChannelRoute(PaintChannel.Color, "_MainTex", PreviewPacking.Value), Is.False); Assert.That(w.StatusMessage, Does.Contain("cannot be packed"));
            Assert.That(w.SetChannelRoute(PaintChannel.Metallic, "_Color", PreviewPacking.Value), Is.False); Assert.That(w.StatusMessage, Does.Contain("not a 2D texture"));
            Assert.That(w.MaterialChoice().routes.Count, Is.EqualTo(2));
            // ほかのチャンネルが別の詰め方で使うプロパティは、使わずに理由を残す
            Assert.That(w.SetChannelRoute(PaintChannel.Color, "_DetailMask", PreviewPacking.Color), Is.True); w.RefreshPreviewTextures();
            b = p.MaterialBinding(0);
            Assert.That(b.For(PaintChannel.Color).Property, Is.EqualTo("_MainTex")); Assert.That(b.Remarks, Has.Some.Contains("already takes"));

            w.ClearChannelRoutes(); w.RefreshPreviewTextures();
            b = p.MaterialBinding(0);
            Assert.That(b.HandSet, Is.False); Assert.That(b.For(PaintChannel.Height).Property, Is.EqualTo("_ParallaxMap")); Assert.That(b.For(PaintChannel.Normal).Property, Is.EqualTo("_BumpMap"));
            Assert.That(w.MaterialChoice(), Is.Null);
            Assert.That(w.Document.Revision, Is.EqualTo(revision)); Assert.That(w.Document.CanUndo, Is.EqualTo(canUndo), "routes are view state, not in the document's Undo");
            Assert.That(standard.GetTexture("_DetailMask"), Is.Null); Assert.That(standard.GetTexture("_ParallaxMap"), Is.Null);
        }

        [Test] public void OtherShadersGetGuessedRoutesButUnverifiedLilToonDoesNot()
        {
            var bumped = Track(new Material(Shader.Find("Legacy Shaders/Bumped Diffuse")));
            var b = PreviewMaterialBindings.Resolve(bumped);
            Assert.That(b.Kind, Is.EqualTo(PreviewMaterialKind.Guessed));
            Assert.That(b.Channels.Select(c => (c.Channel, c.Property, c.Packing)), Is.EquivalentTo(new[] { (PaintChannel.Color, "_MainTex", PreviewPacking.Color), (PaintChannel.Normal, "_BumpMap", PreviewPacking.Normal) }));
            Assert.That(b.Remarks, Has.Some.Contains("guessed"));
            Assert.That(b.Notes(null, new[] { PaintChannel.Emission }), Has.Some.Contains("not verified"));
            var unlit = Track(new Material(Shader.Find("Unlit/Texture")));
            Assert.That(PreviewMaterialBindings.Resolve(unlit).Kind, Is.EqualTo(PreviewMaterialKind.MainTexture), "only a main texture: as before");
            // 手で決めた流し込み先は推し量りに重なる
            var routed = PreviewMaterialBindings.WithRoutes(b, new[] { new PreviewChannelRoute(PaintChannel.Normal, "", PreviewPacking.Normal) });
            Assert.That(routed.For(PaintChannel.Normal), Is.Null); Assert.That(routed.Kind, Is.EqualTo(PreviewMaterialKind.Guessed));
        }

        [Test] public void LilToonChosenAsAMaterialOrAShaderUsesTheVerifiedMappingOnly()
        {
            var lilShader = Shader.Find("lilToon");
            Assume.That(lilShader, Is.Not.Null, "lilToon is not installed in this project");
            var standard = Track(new Material(Shader.Find("Standard")));
            w.SetModel(Model(standard));
            Paint(w.Document, PaintChannel.Color, new Rgba32(200, 120, 80));
            var lil = MaterialAsset("Lil", "lilToon");
            Assert.That(w.UsePreviewMaterial(lil), Is.True); w.Shading = PreviewShading.Material; w.RefreshPreviewTextures();
            var b = w.Preview.MaterialBinding(0);
            Assert.That(b.Source, Is.SameAs(lil));
            Assert.That(b.Kind, Is.EqualTo(PreviewMaterialKind.LilToon).Or.EqualTo(PreviewMaterialKind.MainTexture), "verified lilToon, or only the main texture with the reason");
            Assert.That(b.Kind, Is.Not.EqualTo(PreviewMaterialKind.Guessed), "lilToon is never guessed");
            if (b.Kind == PreviewMaterialKind.LilToon) Assert.That(b.LilToon.IsApplicable, Is.True);
            Assert.That(w.UsePreviewShader(lilShader), Is.True); w.RefreshPreviewTextures();
            b = w.Preview.MaterialBinding(0);
            Assert.That(b.Kind, Is.Not.EqualTo(PreviewMaterialKind.Guessed)); Assert.That(w.Preview.RenderedMaterial(0).shader, Is.SameAs(lilShader).Or.Property("name").StartsWith("Hidden/YoluPainter"));
        }

        // ───────── 消えたもの・型・保存 ─────────

        [Test] public void AGoneAssetOrShaderFallsBackToTheOwnMaterialWithANotice()
        {
            var own = Track(new Material(Shader.Find("Standard")));
            var chosen = MaterialAsset("Chosen", "Unlit/Color");
            w.SetModel(Model(own));
            Assert.That(w.UsePreviewMaterial(chosen), Is.True); w.Shading = PreviewShading.Material; w.RefreshPreviewTextures();
            Assert.That(w.Preview.RenderedMaterial(0).shader.name, Is.EqualTo("Unlit/Color"));
            AssetDatabase.DeleteAsset(folder + "/Chosen.mat");
            w.RefreshPreviewTextures();
            Assert.That(w.StatusMessage, Does.Contain("is gone")); Assert.That(w.MaterialChoice(), Is.Null);
            Assert.That(w.Preview.RenderedMaterial(0).shader, Is.SameAs(own.shader));
            Assert.That(w.UsePreviewShader(Shader.Find("Unlit/Texture")), Is.True);
            w.MaterialChoice().shaderName = "YoluPainter Tests/No Such Shader";
            w.RefreshPreviewTextures();
            Assert.That(w.StatusMessage, Does.Contain("is gone")); Assert.That(w.MaterialChoice(), Is.Null);
            Assert.That(w.Preview.RenderedMaterial(0).shader, Is.SameAs(own.shader));
        }

        [Test] public void WrongKindsAreRefused()
        {
            var standard = Track(new Material(Shader.Find("Standard")));
            w.SetModel(Model(standard));
            var texture = Track(new Texture2D(2, 2));
            Assert.That(TexturePaintWindow.PreviewMaterialRefusal(texture), Does.Contain("not a material"));
            Assert.That(TexturePaintWindow.PreviewMaterialRefusal(null), Is.Not.Null);
            Assert.That(w.UsePreviewMaterial(Track(new Material(Shader.Find("Unlit/Color")))), Is.False, "a material only in memory cannot be found again");
            Assert.That(w.StatusMessage, Does.Contain("not a material asset"));
            Assert.That(w.UsePreviewShader(Shader.Find("Hidden/YoluPainter/PreviewSurface")), Is.False); Assert.That(w.StatusMessage, Does.Contain("hidden"));
            Assert.That(w.UsePreviewShader(null), Is.False);
            Assert.That(w.MaterialChoice(), Is.Null);
            var choices = TexturePaintWindow.PreviewShaderChoices();
            Assert.That(choices.Select(c => c.name), Has.Member("Standard")); Assert.That(choices.Any(c => c.name.StartsWith("Hidden/")), Is.False);
        }

        [Test] public void ChoicesAreWindowStateThatSurvivesReloadAndStayOutOfTheYlp()
        {
            var own = Track(new Material(Shader.Find("Unlit/Texture")));
            var chosen = MaterialAsset("Chosen", "Standard");
            w.SetModel(Model(own));
            Paint(w.Document, PaintChannel.Color, new Rgba32(200, 100, 50));
            Assert.That(w.UsePreviewMaterial(chosen), Is.True); Assert.That(w.SetChannelRoute(PaintChannel.Height, "_DetailMask", PreviewPacking.Value), Is.True);
            string json = EditorJsonUtility.ToJson(w);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(chosen, out string guid, out long _);
            Assert.That(json, Does.Contain("previewMaterialChoices").And.Contain(guid).And.Contain("_DetailMask"));
            // スクリプトの読み直し（窓のシリアライズ）をまたぐ
            w.UseOriginalMaterial(); w.ClearChannelRoutes();
            Assert.That(w.MaterialChoice(), Is.Null);
            EditorJsonUtility.FromJsonOverwrite(json, w);
            w.Shading = PreviewShading.Material; w.RefreshPreviewTextures();
            Assert.That(w.Preview.MaterialBinding(0).Source, Is.SameAs(chosen)); Assert.That(w.Preview.MaterialBinding(0).For(PaintChannel.Height).Property, Is.EqualTo("_DetailMask"));
            // .ylp には入らない（同じ文書を選びあり・なしで保存して、中身の名前と大きさが同じ）
            string withChoice = SaveYlp(), entriesWith = Entries(withChoice);
            w.UseOriginalMaterial(); w.ClearChannelRoutes();
            string without = SaveYlp();
            Assert.That(entriesWith, Is.EqualTo(Entries(without)));
            using (var zip = ZipFile.OpenRead(withChoice))
                foreach (var entry in zip.Entries)
                    using (var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8))
                        Assert.That(reader.ReadToEnd(), Does.Not.Contain(guid).And.Not.Contain("_DetailMask"), entry.FullName);
            // この項目の無い以前の窓の状態は、元のマテリアル・自動の流し込み
            var fresh = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                string old = System.Text.RegularExpressions.Regex.Replace(EditorJsonUtility.ToJson(fresh), "\"previewMaterialChoices\":\\[[^\\]]*\\],?", "");
                EditorJsonUtility.FromJsonOverwrite(old, fresh);
                Assert.That(fresh.MaterialChoice(), Is.Null);
            }
            finally { string r = fresh.RecoveryRoot; Object.DestroyImmediate(fresh); if (Directory.Exists(r)) Directory.Delete(r, true); }
        }

        // ───────── 照明なしの見せ方 ─────────

        [Test] public void TheUnlitViewShowsChannelAndMeshMapValuesAsTheyAre()
        {
            w.Preview.LoadDemoMesh();
            w.Document.AddFillLayer("Rough", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Roughness, new Rgba32(200, 200, 200) }, { PaintChannel.Color, new Rgba32(40, 90, 200) } });
            w.RefreshPreviewTextures();
            var p = w.Preview;
            var lit = Center(p);
            Assert.That(w.ShowChannelIn3D(PaintChannel.Roughness), Is.True); w.RefreshPreviewTextures();
            Assert.That(p.ShowsUnlit, Is.True); Assert.That(p.ShownUnlitTexture(0), Is.Not.Null);
            var rough = Center(p);
            Assert.That(rough.r, Is.EqualTo(200 / 255f).Within(.02f)); Assert.That(rough.g, Is.EqualTo(200 / 255f).Within(.02f)); Assert.That(rough.b, Is.EqualTo(200 / 255f).Within(.02f));
            // 照明を変えても照明なしの絵は変わらない
            w.PreviewScene.intensity = 0; w.PreviewScene.ambient = Color.black; w.RefreshPreviewTextures();
            Assert.That(Center(p), Is.EqualTo(rough));
            Assert.That(w.ShowChannelIn3D(PaintChannel.Color), Is.True); w.RefreshPreviewTextures();
            var color = Center(p);
            Assert.That(color.r, Is.EqualTo(40 / 255f).Within(.02f)); Assert.That(color.b, Is.EqualTo(200 / 255f).Within(.02f));
            // 使っていないチャンネルは暗い灰色、マテリアルに戻すと照明あり
            Assert.That(w.ShowChannelIn3D(PaintChannel.Emission), Is.True); Assert.That(w.StatusMessage, Does.Contain("does not use"));
            w.RefreshPreviewTextures(); Assert.That(Center(p).r, Is.LessThan(.25f));
            Assert.That(w.ShowMaterialIn3D(), Is.True); w.RefreshPreviewTextures();
            Assert.That(p.ShowsUnlit, Is.False); Assert.That(Center(p), Is.Not.EqualTo(color));
            // メッシュマップ: 焼いていなければ断る。焼いたものはその値
            Assert.That(w.ShowMeshMapIn3D(MeshMapKind.AmbientOcclusion), Is.False); Assert.That(w.StatusMessage, Does.Contain("not baked"));
            w.MeshMaps.Put(new[] { TestMeshMaps.Make(MeshMapKind.AmbientOcclusion, w.Document.Width, w.Document.Height, (x, y, c) => .25) });
            Assert.That(w.ShowMeshMapIn3D(MeshMapKind.AmbientOcclusion), Is.True); w.RefreshPreviewTextures();
            var ao = Center(p);
            Assert.That(ao.r, Is.EqualTo(.25f).Within(.03f), "AO 0.25 as a grey value: " + ao);
            // 順に回す: C は使っているチャンネル（Color → Roughness）→ マテリアル、Shift+B はメッシュマップ → マテリアル
            w.ShowMaterialIn3D();
            w.CycleChannelIn3D(); Assert.That((w.ModelShow, w.ModelShowChannel), Is.EqualTo((TexturePaintWindow.ModelShowKind.Channel, PaintChannel.Color)));
            w.CycleChannelIn3D(); Assert.That((w.ModelShow, w.ModelShowChannel), Is.EqualTo((TexturePaintWindow.ModelShowKind.Channel, PaintChannel.Roughness)));
            w.CycleChannelIn3D(); Assert.That(w.ModelShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.Material));
            w.CycleMeshMapIn3D(); Assert.That(w.ModelShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.MeshMap));
            w.CycleMeshMapIn3D(); Assert.That(w.ModelShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.Material));
            // マスク: マスクの無い層では断る
            Assert.That(w.ShowMaskIn3D(), Is.False);
            var layer = w.Document.Layers[w.Document.Layers.Count - 1]; w.SelectedLayer = layer.Id; w.Document.AddLayerMask(layer.Id);
            Assert.That(w.ShowMaskIn3D(), Is.True); w.RefreshPreviewTextures();
            Assert.That(Center(p).r, Is.GreaterThan(.95f), "an empty mask shows the whole layer: white");
        }

        // ───────── 補助 ─────────

        Color Center(IsolatedModelPreview p) { var image = p.RenderStatic(64, 64); made.Add(image); return image.GetPixel(32, 32); }

        T Track<T>(T o) where T : Object { o.hideFlags = HideFlags.HideAndDontSave; made.Add(o); return o; }

        GameObject Model(params Material[] materials)
        {
            var root = Track(new GameObject("Material choice test model"));
            for (int i = 0; i < materials.Length; i++)
            {
                var child = new GameObject("Part " + i) { hideFlags = HideFlags.HideAndDontSave };
                child.transform.SetParent(root.transform, false); child.transform.localPosition = new Vector3(i * 1.5f, 0, 0);
                child.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                child.AddComponent<MeshRenderer>().sharedMaterial = materials[i];
            }
            return root;
        }

        static void Paint(PaintDocument d, PaintChannel channel, Rgba32 value, int at = 5)
        {
            var layer = d.Layers[d.Layers.Count - 1];
            if (!layer.IsChannelEnabled(channel)) d.SetChannelEnabled(layer.Id, channel, true);
            layer.GetChannel(channel).SetPixel(at, at, value);
        }

        Material MaterialAsset(string name, string shader)
        {
            if (folder == null)
            {
                folder = "Assets/ZZ_PreviewMaterialChoiceTests-" + Guid.NewGuid().ToString("N");
                AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            }
            AssetDatabase.CreateAsset(new Material(Shader.Find(shader)), folder + "/" + name + ".mat"); AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<Material>(folder + "/" + name + ".mat");
        }

        string SaveYlp()
        {
            string path = Path.Combine(Path.GetTempPath(), "YoluPainterChoice-" + Guid.NewGuid().ToString("N") + ".ylp");
            temp.Add(path); answers.SavePath = path;
            w.SaveProject(true);
            Assert.That(File.Exists(path), Is.True, w.StatusMessage);
            return path;
        }

        static string Entries(string path)
        {
            using (var zip = ZipFile.OpenRead(path)) return string.Join(",", zip.Entries.Select(e => e.FullName).OrderBy(s => s));
        }

        static Color32[] Read(RenderTexture rt)
        {
            Assert.That(rt, Is.Not.Null, "no packed texture");
            var previous = RenderTexture.active; var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            try { RenderTexture.active = rt; t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false); return t.GetPixels32(); }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(t); }
        }
    }
}
