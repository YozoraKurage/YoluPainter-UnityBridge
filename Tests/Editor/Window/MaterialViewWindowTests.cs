using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 描画ウィンドウのマテリアル表示とマテリアルの欄（ウィンドウは開かず、batch-gl で）: 全部のテクスチャセットの塗った中身が、それぞれの
    /// スロットの複製の対応するプロパティに入る（今のセットの今のチャンネルは合成器、ほかは変わったときだけ作る CPU の合成）。欄の変更は複製だけ、
    /// 「マテリアルに反映…」は確かめて断れば何もしない・受ければ変えたものだけが元に入り Unity の Undo で戻る。モデルに無いマテリアルの変更は捨てて知らせる。
    /// </summary>
    [Category("GPU")]
    public sealed class MaterialViewWindowTests
    {
        sealed class Answers : IPainterDialogs
        {
            public bool Confirm = true; public readonly List<string> Asked = new List<string>(); public string LastMessage;
            public string SaveFolder(string title, string folder, string defaultName) => "";
            public string OpenFolder(string title, string folder) => "";
            public string OpenFile(string title, string folder, string extension) => "";
            public string SaveFile(string title, string folder, string defaultName, string extension) => "";
            bool IPainterDialogs.Confirm(string title, string message, string ok, string cancel) { Asked.Add("Confirm: " + title); LastMessage = message; return Confirm; }
            public void Inform(string title, string message) { Asked.Add("Inform: " + title); LastMessage = message; }
            public void Progress(string title, string info, float progress) { }
            public void ClearProgress() { }
        }

        readonly List<Object> made = new List<Object>();
        TexturePaintWindow w; Answers answers; string folder;

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
        }

        [Test] public void EveryTextureSetFeedsItsSlotAndUnchangedChannelsAreNotRebuilt()
        {
            var unlit = Track(new Material(Shader.Find("Unlit/Texture"))); var standard = Track(new Material(Shader.Find("Standard")));
            w.SetModel(Model(unlit, standard));
            Assert.That(w.Preview.MaterialSlotCount, Is.EqualTo(2));
            var first = w.CurrentTextureSet; var a = w.Document;
            Paint(a, PaintChannel.Color, new Rgba32(250, 10, 10));
            var second = w.AddTextureSet(1); var b = w.Document;
            Paint(b, PaintChannel.Color, new Rgba32(10, 250, 10)); Paint(b, PaintChannel.Roughness, new Rgba32(90, 90, 90));
            w.Channel = PaintChannel.Color;
            w.Shading = PreviewShading.Material; w.RefreshPreviewTextures();
            var p = w.Preview;
            Assert.That(p.RenderedMaterial(0).shader, Is.SameAs(unlit.shader)); Assert.That(p.RenderedMaterial(1).shader, Is.SameAs(standard.shader));
            bool gamma = QualitySettings.activeColorSpace == ColorSpace.Gamma;
            Assume.That(gamma, "this check reads the bound textures directly (the test project is Gamma)");
            Assert.That(p.RenderedMaterial(0).mainTexture, Is.SameAs(first.MaterialChannels[PaintChannel.Color].Texture), "the other set: its CPU composite");
            Assert.That(p.RenderedMaterial(1).GetTexture("_MainTex"), Is.SameAs(w.Compositor.Texture), "the current set's current channel: the live compositor");
            Assert.That(p.RenderedMaterial(1).GetTexture("_MetallicGlossMap"), Is.SameAs(p.MaterialPackedTexture(1, "_MetallicGlossMap")));
            Assert.That(second.MaterialChannels.Keys, Is.EquivalentTo(new[] { PaintChannel.Roughness }), "only the channels the material maps and the document uses");
            Assert.That(unlit.mainTexture, Is.Null); Assert.That(standard.GetTexture("_MetallicGlossMap"), Is.Null);
            // 何も変わらなければ作り直さない。今のチャンネル（Color）に描いても、ほかのチャンネルは作り直さない
            int builds = w.MaterialChannelBuilds;
            w.RefreshPreviewTextures(); Assert.That(w.MaterialChannelBuilds, Is.EqualTo(builds));
            Paint(b, PaintChannel.Color, new Rgba32(1, 2, 3), 7); w.RefreshPreviewTextures();
            Assert.That(w.MaterialChannelBuilds, Is.EqualTo(builds), "a Color edit leaves the Roughness composite as it is");
            Paint(b, PaintChannel.Roughness, new Rgba32(200, 200, 200), 9); w.RefreshPreviewTextures();
            Assert.That(w.MaterialChannelBuilds, Is.EqualTo(builds + 1), "a Roughness edit rebuilds it once");
            // 中立に戻すと中立のシェーダー、作ったテクスチャは放す
            w.Shading = PreviewShading.Neutral; w.RefreshPreviewTextures();
            Assert.That(p.RenderedMaterial(1).shader.name, Is.EqualTo("Hidden/YoluPainter/PreviewSurface"));
            Assert.That(second.MaterialChannels, Is.Empty); Assert.That(first.MaterialChannels, Is.Empty);
        }

        [Test] public void TheNormalOutputReachesTheMaterialEvenWithLitNormalsOff()
        {
            var standard = Track(new Material(Shader.Find("Standard")));
            w.SetModel(Model(standard));
            Paint(w.Document, PaintChannel.Normal, new Rgba32(200, 128, 200));
            w.PreviewNormals = false; w.Shading = PreviewShading.Material; w.RefreshPreviewTextures();
            Assert.That(w.PreviewNormalTexture, Is.Null, "the neutral lighting stays off");
            var bump = w.Preview.RenderedMaterial(0).GetTexture("_BumpMap");
            Assert.That(bump, Is.Not.Null); Assert.That(w.Preview.RenderedMaterial(0).IsKeywordEnabled("_NORMALMAP"), Is.True);
            Assert.That(standard.GetTexture("_BumpMap"), Is.Null);
        }

        // ───────── マテリアルの欄 ─────────

        [Test] public void ApplyAsksFirstAndWritesOnlyTheChangedValuesWithUnityUndo()
        {
            var material = MaterialAsset("Standard");
            float gloss = material.GetFloat("_Glossiness"), metal = material.GetFloat("_Metallic"); var color = material.GetColor("_Color");
            w.SetModel(Model(material));
            var props = w.MaterialProperties(material);
            Assert.That(props.Any(p => p.Name == "_Glossiness"), Is.True);
            w.EnsureMaterialInspector(material);
            Assert.That(w.MaterialInspector.Editor.target, Is.SameAs(w.MaterialInspector.Copy));
            Assert.That(w.MaterialInspector.Copy, Is.Not.SameAs(material));
            w.SetMaterialValue(material, props.Single(p => p.Name == "_Glossiness"), new Vector4(.25f, 0, 0, 0));
            w.SetMaterialValue(material, props.Single(p => p.Name == "_Color"), new Vector4(0, 0, 1, 1));
            Assert.That(material.GetFloat("_Glossiness"), Is.EqualTo(gloss), "the panel changes the preview copy only");
            w.Shading = PreviewShading.Material; w.RefreshPreviewTextures();
            Assert.That(w.Preview.RenderedMaterial(0).GetFloat("_Glossiness"), Is.EqualTo(.25f)); Assert.That(w.Preview.RenderedMaterial(0).GetColor("_Color"), Is.EqualTo(Color.blue));
            string undo = PreviewMaterialTests.UndoRecords(); int dirty = EditorUtility.GetDirtyCount(material);
            // 断れば何もしない
            answers.Confirm = false; w.ApplyMaterialEdits();
            Assert.That(answers.Asked, Is.EqualTo(new[] { "Confirm: Apply to the material?" }));
            Assert.That(answers.LastMessage, Does.Contain("_Glossiness: 0.5 → 0.25").And.Contain("_Color").And.Not.Contain("_Metallic"));
            Assert.That(material.GetFloat("_Glossiness"), Is.EqualTo(gloss)); Assert.That(EditorUtility.GetDirtyCount(material), Is.EqualTo(dirty)); Assert.That(PreviewMaterialTests.UndoRecords(), Is.EqualTo(undo));
            Assert.That(w.MaterialEdits.Count, Is.EqualTo(2)); Assert.That(w.StatusMessage, Does.Contain("Not applied"));
            // 受ければ変えたものだけが入り、印が消え、Unity の Undo で戻る
            answers.Confirm = true; w.ApplyMaterialEdits();
            Assert.That(material.GetFloat("_Glossiness"), Is.EqualTo(.25f)); Assert.That(material.GetColor("_Color"), Is.EqualTo(Color.blue)); Assert.That(material.GetFloat("_Metallic"), Is.EqualTo(metal));
            Assert.That(w.MaterialEdits.Count, Is.Zero); Assert.That(EditorUtility.IsDirty(material), Is.True);
            Assert.That(PreviewMaterialTests.UndoRecords(), Is.Not.EqualTo(undo));
            Undo.PerformUndo();
            Assert.That(material.GetFloat("_Glossiness"), Is.EqualTo(gloss)); Assert.That(material.GetColor("_Color"), Is.EqualTo(color));
            // 何も変えていなければ知らせるだけ
            answers.Asked.Clear(); w.ApplyMaterialEdits();
            Assert.That(answers.Asked, Is.EqualTo(new[] { "Inform: Cannot apply to the material" }));
        }

        [Test] public void ImportedOrBuiltInMaterialsAreRefused()
        {
            var builtin = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
            Assume.That(builtin, Is.Not.Null);
            w.SetModel(Model(builtin));
            var props = w.MaterialProperties(builtin);
            float before = builtin.GetFloat("_Glossiness");
            w.SetMaterialValue(builtin, props.Single(p => p.Name == "_Glossiness"), new Vector4(.9f, 0, 0, 0));
            w.ApplyMaterialEdits();
            Assert.That(answers.Asked, Is.EqualTo(new[] { "Inform: Cannot apply to the material" }));
            Assert.That(answers.LastMessage, Does.Contain("an imported model or a built-in asset"));
            Assert.That(builtin.GetFloat("_Glossiness"), Is.EqualTo(before));
        }

        [Test] public void ChangesOfMaterialsNotInTheModelAreDroppedAndProjectSwitchesAreAnnounced()
        {
            var kept = Track(new Material(Shader.Find("Standard"))); var gone = Track(new Material(Shader.Find("Standard")));
            w.SetModel(Model(gone, kept));
            var props = w.MaterialProperties(gone);
            w.MaterialEdits.Set(gone, props.Single(p => p.Name == "_Metallic"), new Vector4(.7f, 0, 0, 0));
            w.MaterialEdits.Set(kept, props.Single(p => p.Name == "_Metallic"), new Vector4(.4f, 0, 0, 0));
            w.ReconcileMaterialEdits();
            Assert.That(w.MaterialEdits.Count, Is.EqualTo(2));
            w.SetModel(Model(kept)); w.ReconcileMaterialEdits();
            Assert.That(w.MaterialEdits.Materials, Is.EqualTo(new[] { kept }));
            Assert.That(w.StatusMessage, Does.Contain("Discarded 1 preview-only material change"));
            typeof(TexturePaintWindow).GetMethod("CreateDocument", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(w, new object[] { 256 });
            w.ReconcileMaterialEdits();
            Assert.That(w.StatusMessage, Does.Contain("still has 1 change(s) that are only in the preview"));
        }

        // ───────── 補助 ─────────

        T Track<T>(T o) where T : Object { o.hideFlags = HideFlags.HideAndDontSave; made.Add(o); return o; }

        /// <summary>レンダラーが 1 つずつの子を持つモデル（子の順がスロットの順）。</summary>
        GameObject Model(params Material[] materials)
        {
            var root = Track(new GameObject("Material view test model"));
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

        Material MaterialAsset(string shader)
        {
            folder = "Assets/ZZ_MaterialViewWindowTests-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            AssetDatabase.CreateAsset(new Material(Shader.Find(shader)), folder + "/Source.mat"); AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<Material>(folder + "/Source.mat");
        }
    }
}
