using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.LilToon;
using Yozolab.YoluPainter.Editor.LilToonApply;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 3D プレビューのマテリアル表示: 元のマテリアルの複製を元のシェーダーで描き、塗ったチャンネルを確かめた対応のプロパティにだけ
    /// （読み方に詰め直して）入れ、ほかは元のまま。壊れたシェーダーは中立に戻す。元のマテリアル・テクスチャ（ファイル・dirty・Undo）は
    /// 変わらない。シェーダーで描くので batch-gl。
    /// </summary>
    [Category("GPU")]
    public sealed class PreviewMaterialTests
    {
        readonly List<Object> made = new List<Object>();
        string folder;

        [SetUp] public void RequireShaders()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
            GpuTests.RequireWorkingShader(PreviewMaterialView.PackShaderName);
        }

        [TearDown] public void CleanUp()
        {
            foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            made.Clear();
            if (folder != null) { AssetDatabase.DeleteAsset(folder); folder = null; }
        }

        // ───────── 対応 ─────────

        [Test] public void StandardTakesThePaintedMapsAndKeepsTheRest()
        {
            var occlusion = Solid(new Color32(90, 90, 90, 255));
            var material = Track(new Material(Shader.Find("Standard")));
            material.SetTexture("_OcclusionMap", occlusion); material.SetFloat("_Metallic", .3f); material.SetFloat("_Glossiness", .7f);
            var keywordsBefore = material.shaderKeywords.OrderBy(k => k).ToArray();
            using (var preview = Loaded(material))
            {
                var b = preview.MaterialBinding(0);
                Assert.That(b.Kind, Is.EqualTo(PreviewMaterialKind.Standard), b.Unusable);
                Assert.That(b.Channels.Select(c => (c.Channel, c.Property)), Is.EquivalentTo(new[]
                {
                    (PaintChannel.Color, "_MainTex"), (PaintChannel.Normal, "_BumpMap"), (PaintChannel.Emission, "_EmissionMap"),
                    (PaintChannel.Metallic, "_MetallicGlossMap"), (PaintChannel.Roughness, "_MetallicGlossMap"), (PaintChannel.Height, "_ParallaxMap"),
                }));
                var color = Solid(new Color32(200, 40, 30, 255)); var normal = Solid(new Color32(128, 200, 220, 255)); var emission = Solid(new Color32(10, 250, 10, 255));
                var metal = Solid(new Color32(200, 0, 0, 128)); var rough = Solid(new Color32(60, 0, 0, 255));
                var painted = new PreviewSlotChannels { NormalOutput = normal };
                painted.Composites[PaintChannel.Color] = color; painted.Composites[PaintChannel.Emission] = emission;
                painted.Composites[PaintChannel.Metallic] = metal; painted.Composites[PaintChannel.Roughness] = rough;
                preview.Shading = PreviewShading.Material;
                preview.SetMaterialChannels(new Dictionary<int, PreviewSlotChannels> { { 0, painted } });
                var shown = preview.RenderedMaterial(0);
                Assert.That(preview.MaterialReason(0), Is.Null);
                Assert.That(shown, Is.Not.SameAs(material), "a copy is drawn");
                Assert.That(shown.shader, Is.SameAs(material.shader));
                Assert.That(shown.GetTexture("_MainTex"), Is.SameAs(QualitySettings.activeColorSpace == ColorSpace.Gamma ? color : preview.MaterialPackedTexture(0, "_MainTex")));
                Assert.That(shown.GetTexture("_BumpMap"), Is.SameAs(normal));
                Assert.That(shown.GetTexture("_OcclusionMap"), Is.SameAs(occlusion), "a property without a painted channel keeps the material's texture");
                Assert.That(shown.GetTexture("_ParallaxMap"), Is.SameAs(material.GetTexture("_ParallaxMap")), "Height is not used by this document");
                foreach (var k in new[] { "_NORMALMAP", "_EMISSION", "_METALLICGLOSSMAP" }) Assert.That(shown.IsKeywordEnabled(k), k + " on the copy (as Standard's inspector does when the map is set)");
                Assert.That(shown.IsKeywordEnabled("_PARALLAXMAP"), Is.False);
                // _MetallicGlossMap: R = Metallic × α（200 × 128 / 255 → 100）、A = 1 − Roughness（255 − 60）
                var packed = Read(preview.MaterialPackedTexture(0, "_MetallicGlossMap"));
                Assert.That(packed.Select(p => (p.r, p.a)).Distinct(), Is.EqualTo(new[] { ((byte)100, (byte)195) }));
                Assert.That(material.shaderKeywords.OrderBy(k => k), Is.EqualTo(keywordsBefore), "the source material keeps its keywords");
                Assert.That(material.GetTexture("_MainTex"), Is.Null);
            }
        }

        [Test] public void StandardKeepsTheUnpaintedHalfOfTheMetallicGlossMap()
        {
            var material = Track(new Material(Shader.Find("Standard")));
            material.SetFloat("_Metallic", 0.2f); material.SetFloat("_Glossiness", 0.6f);
            using (var preview = Loaded(material))
            {
                var painted = new PreviewSlotChannels(); painted.Composites[PaintChannel.Metallic] = Solid(new Color32(255, 0, 0, 255));
                preview.Shading = PreviewShading.Material;
                preview.SetMaterialChannels(new Dictionary<int, PreviewSlotChannels> { { 0, painted } });
                var p = Read(preview.MaterialPackedTexture(0, "_MetallicGlossMap")).Distinct().Single();
                Assert.That(p.r, Is.EqualTo(255)); Assert.That(p.a, Is.EqualTo(153).Within(1), "no Roughness painted: the smoothness stays the material's _Glossiness (0.6)");
                material.SetFloat("_SmoothnessTextureChannel", 1);
                var b = PreviewMaterialBindings.Resolve(material);
                Assert.That(b.For(PaintChannel.Roughness), Is.Null, "smoothness from the albedo alpha: Roughness has nowhere to go");
                Assert.That(b.Unmapped.Single().Channel, Is.EqualTo(PaintChannel.Roughness));
            }
        }

        [Test] public void LilToonFollowsTheVerifiedMappingAndPacksTheDataChannels()
        {
            if (Shader.Find("lilToon") == null) Assert.Ignore("lilToon is not installed in this project.");
            var shadow = Solid(new Color32(1, 2, 3, 255));
            var material = Track(new Material(Shader.Find("lilToon")));
            material.SetTexture("_ShadowColorTex", shadow);
            using (var preview = Loaded(material))
            {
                var b = preview.MaterialBinding(0);
                Assert.That(b.Kind, Is.EqualTo(PreviewMaterialKind.LilToon), string.Join(" ", b.Remarks) + b.Unusable);
                Assert.That(b.Summary, Does.StartWith("lilToon 2.3.4"));
                Assert.That(b.Channels.Select(c => (c.Channel, c.Property, c.Packing)), Is.EquivalentTo(new[]
                {
                    (PaintChannel.Color, "_MainTex", PreviewPacking.Color), (PaintChannel.Roughness, "_SmoothnessTex", PreviewPacking.InvertedValue),
                    (PaintChannel.Metallic, "_MetallicGlossMap", PreviewPacking.Value), (PaintChannel.Height, "_ParallaxMap", PreviewPacking.Value),
                    (PaintChannel.Normal, "_BumpMap", PreviewPacking.Normal), (PaintChannel.Emission, "_EmissionMap", PreviewPacking.Color),
                }));
                // 塗っていない所・半透明・不透明の値。lilToon への割り当て（LilToonAssignment.Convert）と同じ画素になる
                var rough = Pixels(new[] { new Color32(200, 9, 9, 0), new Color32(200, 9, 9, 128), new Color32(60, 9, 9, 255), new Color32(255, 9, 9, 255) });
                var metal = Pixels(new[] { new Color32(255, 0, 0, 0), new Color32(255, 0, 0, 64), new Color32(10, 0, 0, 255), new Color32(128, 0, 0, 200) });
                var painted = new PreviewSlotChannels(); painted.Composites[PaintChannel.Roughness] = rough; painted.Composites[PaintChannel.Metallic] = metal;
                painted.Composites[PaintChannel.Color] = Solid(new Color32(30, 60, 200, 255));
                preview.Shading = PreviewShading.Material;
                preview.SetMaterialChannels(new Dictionary<int, PreviewSlotChannels> { { 0, painted } });
                var shown = preview.RenderedMaterial(0);
                Assert.That(shown.shader, Is.SameAs(material.shader)); Assert.That(preview.MaterialReason(0), Is.Null);
                Assert.That(shown.GetTexture("_SmoothnessTex"), Is.SameAs(preview.MaterialPackedTexture(0, "_SmoothnessTex")));
                Assert.That(shown.GetTexture("_ShadowColorTex"), Is.SameAs(shadow), "an unmapped property keeps the material's texture");
                Assert.That(shown.GetTexture("_BumpMap"), Is.SameAs(material.GetTexture("_BumpMap")), "Normal is not painted here");
                Assert.That(shown.shaderKeywords.OrderBy(k => k), Is.EqualTo(material.shaderKeywords.OrderBy(k => k)), "lilToon (not Multi) keeps its keywords");
                Assert.That(Bytes(Read(preview.MaterialPackedTexture(0, "_SmoothnessTex"))), Is.EqualTo(LilToonAssignment.Convert(PaintChannel.Roughness, RawBytes(rough))));
                Assert.That(Bytes(Read(preview.MaterialPackedTexture(0, "_MetallicGlossMap"))), Is.EqualTo(LilToonAssignment.Convert(PaintChannel.Metallic, RawBytes(metal))));
                // _UseBumpMap が切れているので、塗った Normal が見えないことを知らせる
                Assert.That(b.Notes(shown, new[] { PaintChannel.Normal }), Has.Some.Contains("_UseBumpMap"));
            }
        }

        [Test] public void OtherShadersGetOnlyColourInTheMainTexture()
        {
            var material = Track(new Material(Shader.Find("Unlit/Texture")));
            var original = Solid(new Color32(1, 1, 1, 255)); material.mainTexture = original;
            using (var preview = Loaded(material))
            {
                var b = preview.MaterialBinding(0);
                Assert.That(b.Kind, Is.EqualTo(PreviewMaterialKind.MainTexture));
                Assert.That(b.Channels.Single().Property, Is.EqualTo("_MainTex"));
                Assert.That(b.Notes(null, new[] { PaintChannel.Color, PaintChannel.Normal }).Single(), Does.Contain("not verified"), "the unused channels are not reported, Normal is");
                var color = Solid(new Color32(220, 40, 30, 255));
                var painted = new PreviewSlotChannels { NormalOutput = Solid(new Color32(128, 128, 255, 255)) }; painted.Composites[PaintChannel.Color] = color;
                preview.Shading = PreviewShading.Material;
                preview.SetMaterialChannels(new Dictionary<int, PreviewSlotChannels> { { 0, painted } });
                Assert.That(preview.RenderedMaterial(0).mainTexture, Is.SameAs(QualitySettings.activeColorSpace == ColorSpace.Gamma ? color : preview.MaterialPackedTexture(0, "_MainTex")));
                Assert.That(material.mainTexture, Is.SameAs(original));
                // 中立の表示に戻すと中立のシェーダー、マテリアルの表示ではその色がそのまま見える（照明の無いシェーダー）
                var lit = preview.RenderStatic(64, 64); var center = lit.GetPixel(32, 32);
                Assert.That(center.r, Is.EqualTo(220 / 255f).Within(.02f)); Assert.That(center.g, Is.EqualTo(40 / 255f).Within(.02f));
                preview.Shading = PreviewShading.Neutral;
                Assert.That(preview.RenderedMaterial(0).shader.name, Is.EqualTo("Hidden/YoluPainter/PreviewSurface"));
                preview.SetPaintTextures(new Dictionary<int, Texture> { { 0, color } });
                var neutral = preview.RenderStatic(64, 64);
                Assert.That(neutral.GetPixel(32, 32), Is.Not.EqualTo(center), "the neutral view lights the colour, the material view shows the shader's own look");
                Object.DestroyImmediate(lit); Object.DestroyImmediate(neutral);
            }
        }

        [Test] public void ABrokenShaderFallsBackToNeutralWithTheReason()
        {
            // ShaderLab の構文の誤り（取り込むと ShaderUtil.ShaderHasError が true になる。HLSL の誤りは描くときまで分からないことがある）
            var broken = ShaderUtil.CreateShaderAsset("Shader \"Hidden/YoluPainterTests/Broken\" { Properties { _MainTex (\"T\", 2D) = \"white\" {} } SubShader { Pass { this is not ShaderLab } } }", false);
            Assert.That(ShaderUtil.ShaderHasError(broken), Is.True, "the fixture shader must be broken");
            made.Add(broken);
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                var material = Track(new Material(broken));
                using (var preview = Loaded(material))
                {
                    var painted = new PreviewSlotChannels(); painted.Composites[PaintChannel.Color] = Solid(new Color32(9, 9, 9, 255));
                    preview.Shading = PreviewShading.Material;
                    preview.SetMaterialChannels(new Dictionary<int, PreviewSlotChannels> { { 0, painted } });
                    Assert.That(preview.MaterialBinding(0).Kind, Is.EqualTo(PreviewMaterialKind.Unusable));
                    Assert.That(preview.MaterialReason(0), Does.Contain("compile errors"));
                    Assert.That(preview.RenderedMaterial(0).shader.name, Is.EqualTo("Hidden/YoluPainter/PreviewSurface"), "drawn neutral");
                    Assert.That(preview.DisplayMaterial(0), Is.Null);
                }
                var orphan = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
                Assert.That(PreviewMaterialBindings.Resolve(orphan).Unusable, Does.Contain("InternalErrorShader"));
                Assert.That(PreviewMaterialBindings.Resolve(null).Kind, Is.EqualTo(PreviewMaterialKind.NoMaterial));
            }
            finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false; }
        }

        [Test] public void TheDemoCubeHasNoMaterialAndStaysNeutral()
        {
            using (var preview = new IsolatedModelPreview())
            {
                preview.LoadDemoMesh();
                preview.Shading = PreviewShading.Material;
                preview.SetMaterialChannels(null);
                Assert.That(preview.MaterialReason(0), Does.Contain("no source material"));
                Assert.That(preview.RenderedMaterial(0).shader.name, Is.EqualTo("Hidden/YoluPainter/PreviewSurface"));
            }
        }

        // ───────── 欄の値（複製だけ） ─────────

        [Test] public void EditsGoIntoTheCopyOnlyAndRevertBack()
        {
            var shader = ShaderUtil.CreateShaderAsset("Shader \"Hidden/YoluPainterTests/Toggles\" { Properties {\n" +
                "[Toggle(YP_TEST_ON)] _UseThing (\"Use thing\", Float) = 0\n[ToggleOff] _Glow (\"Glow\", Float) = 1\n[HideInInspector] _Secret (\"Secret\", Float) = 3\n" +
                "_Amount (\"Amount\", Range(0, 2)) = 0.5\n[IntRange] _Steps (\"Steps\", Range(0, 8)) = 2\n[Enum(Off, 0, Front, 1, Back, 2)] _Cull (\"Cull\", Float) = 2\n" +
                "_Tint (\"Tint\", Color) = (1, 1, 1, 1)\n_Offset (\"Offset\", Vector) = (0, 0, 0, 0)\n_MainTex (\"Texture\", 2D) = \"white\" {}\n}\n" +
                " SubShader { Pass { CGPROGRAM\n#pragma vertex v\n#pragma fragment f\n#pragma shader_feature_local YP_TEST_ON\n#pragma shader_feature_local _GLOW_OFF\n#include \"UnityCG.cginc\"\n" +
                "fixed4 _Tint;\nfloat4 v(float4 p : POSITION) : SV_POSITION { return UnityObjectToClipPos(p); }\nfixed4 f() : SV_Target { return _Tint; }\nENDCG } } }", false);
            made.Add(shader);
            var infos = MaterialPropertyInfo.Describe(shader);
            MaterialPropertyInfo P(string n) => infos.Single(i => i.Name == n);
            Assert.That(P("_Secret").Hidden, Is.True);
            Assert.That((P("_UseThing").Control, P("_UseThing").Keyword, P("_UseThing").KeywordWhenOff), Is.EqualTo((MaterialPropertyControl.Toggle, "YP_TEST_ON", false)));
            Assert.That((P("_Glow").Control, P("_Glow").Keyword, P("_Glow").KeywordWhenOff), Is.EqualTo((MaterialPropertyControl.Toggle, "_GLOW_OFF", true)));
            Assert.That((P("_Amount").Control, P("_Amount").Range), Is.EqualTo((MaterialPropertyControl.Slider, new Vector2(0, 2))));
            Assert.That(P("_Steps").Control, Is.EqualTo(MaterialPropertyControl.IntSlider));
            Assert.That(P("_Cull").Options.Select(o => o.label), Is.EqualTo(new[] { "Off", "Front", "Back" }));
            Assert.That(P("_Tint").Control, Is.EqualTo(MaterialPropertyControl.Color)); Assert.That(P("_Offset").Control, Is.EqualTo(MaterialPropertyControl.Vector));

            var material = Track(new Material(shader));
            var edits = new PreviewMaterialEdits();
            using (var preview = Loaded(material))
            {
                preview.MaterialEdits = edits; preview.Shading = PreviewShading.Material;
                preview.SetMaterialChannels(null);
                var copy = preview.RenderedMaterial(0);
                Assert.That(copy.shader, Is.SameAs(shader));
                edits.Set(material, P("_UseThing"), new Vector4(1, 0, 0, 0));
                edits.Set(material, P("_Amount"), new Vector4(1.5f, 0, 0, 0));
                edits.Set(material, P("_Tint"), new Vector4(1, 0, 0, 1));
                preview.SetMaterialChannels(null);
                Assert.That(copy.GetFloat("_UseThing"), Is.EqualTo(1)); Assert.That(copy.IsKeywordEnabled("YP_TEST_ON"), Is.True, "[Toggle(KEY)] sets its keyword on the copy");
                Assert.That(copy.GetFloat("_Amount"), Is.EqualTo(1.5f)); Assert.That(copy.GetColor("_Tint"), Is.EqualTo(Color.red));
                Assert.That(material.GetFloat("_UseThing"), Is.Zero); Assert.That(material.IsKeywordEnabled("YP_TEST_ON"), Is.False);
                Assert.That(material.GetFloat("_Amount"), Is.EqualTo(.5f)); Assert.That(material.GetColor("_Tint"), Is.EqualTo(Color.white));
                Assert.That(edits.Count, Is.EqualTo(3));
                // 元と同じ値に戻すと印も外れる。1 つずつ・全部を元に戻せる
                edits.Set(material, P("_Amount"), new Vector4(.5f, 0, 0, 0)); Assert.That(edits.Find(material, "_Amount"), Is.Null);
                Assert.That(edits.Revert(material, "_Tint"), Is.True);
                preview.SetMaterialChannels(null);
                Assert.That(copy.GetColor("_Tint"), Is.EqualTo(Color.white)); Assert.That(copy.GetFloat("_UseThing"), Is.EqualTo(1));
                Assert.That(edits.RevertAll(material), Is.EqualTo(1));
                preview.SetMaterialChannels(null);
                Assert.That(copy.GetFloat("_UseThing"), Is.Zero); Assert.That(copy.IsKeywordEnabled("YP_TEST_ON"), Is.False);
                // 元のマテリアルを外で変えると、複製は元に合わせ直す（欄の変更は重ねたまま）
                edits.Set(material, P("_Amount"), new Vector4(2, 0, 0, 0));
                material.SetColor("_Tint", Color.blue); EditorUtility.SetDirty(material);
                preview.SetMaterialChannels(null);
                Assert.That(copy.GetColor("_Tint"), Is.EqualTo(Color.blue)); Assert.That(copy.GetFloat("_Amount"), Is.EqualTo(2));
            }
            Assert.That(() => edits.Set(material, P("_MainTex"), Vector4.one), Throws.ArgumentException, "textures are not edited");
        }

        [Test] public void LilToonMultiTogglesSetTheVerifiedKeywordOnTheCopy()
        {
            if (Shader.Find("_lil/lilToonMulti") == null) Assert.Ignore("lilToon is not installed in this project.");
            var material = Track(new Material(Shader.Find("_lil/lilToonMulti")));
            var report = LilToonAdapter.Inspect(material);
            Assert.That(report.IsApplicable, report.Describe());
            var info = MaterialPropertyInfo.Describe(material.shader, report).Single(p => p.Name == "_UseBumpMap");
            Assert.That((info.Control, info.Keyword), Is.EqualTo((MaterialPropertyControl.Toggle, "_NORMALMAP")));
            var plain = MaterialPropertyInfo.Describe(Shader.Find("lilToon"), LilToonAdapter.Inspect(Track(new Material(Shader.Find("lilToon"))))).Single(p => p.Name == "_UseBumpMap");
            Assert.That((plain.Control, plain.Keyword), Is.EqualTo((MaterialPropertyControl.Toggle, (string)null)), "lilToon (not Multi) has no keyword for it");
            var edits = new PreviewMaterialEdits();
            using (var preview = Loaded(material))
            {
                preview.MaterialEdits = edits; preview.Shading = PreviewShading.Material;
                edits.Set(material, info, new Vector4(1, 0, 0, 0));
                preview.SetMaterialChannels(null);
                Assert.That(preview.RenderedMaterial(0).IsKeywordEnabled("_NORMALMAP"), Is.True);
                Assert.That(material.IsKeywordEnabled("_NORMALMAP"), Is.False);
            }
        }

        // ───────── 元のアセットに触れない ─────────

        [Test] public void TheSourceAssetsStayByteForByteTheSame()
        {
            folder = "Assets/ZZ_PreviewMaterialTests-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            var texturePath = folder + "/Albedo.png";
            File.WriteAllBytes(Path.GetFullPath(texturePath), Solid(new Color32(10, 20, 30, 255)).EncodeToPNG());
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
            var material = new Material(Shader.Find("Standard")); material.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            AssetDatabase.CreateAsset(material, folder + "/Source.mat"); AssetDatabase.SaveAssets();
            material = AssetDatabase.LoadAssetAtPath<Material>(folder + "/Source.mat");
            var files = new[] { folder + "/Source.mat", folder + "/Source.mat.meta", texturePath, texturePath + ".meta" };
            string Hashes() { using (var sha = SHA256.Create()) return string.Join(",", files.Select(f => BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.GetFullPath(f)))))); }
            string State() => EditorJsonUtility.ToJson(material) + "|" + string.Join(",", material.shaderKeywords) + "|" + EditorUtility.GetDirtyCount(material) + "|" + EditorUtility.IsDirty(material)
                + "|" + EditorUtility.GetDirtyCount(material.mainTexture) + "|" + UndoRecords();
            string hashes = Hashes(), state = State();
            var edits = new PreviewMaterialEdits();
            using (var preview = Loaded(material))
            {
                preview.MaterialEdits = edits; preview.Shading = PreviewShading.Material;
                var infos = MaterialPropertyInfo.Describe(material.shader);
                edits.Set(material, infos.Single(p => p.Name == "_Glossiness"), new Vector4(.1f, 0, 0, 0));
                edits.Set(material, infos.Single(p => p.Name == "_Color"), new Vector4(0, 1, 0, 1));
                var painted = new PreviewSlotChannels { NormalOutput = Solid(new Color32(128, 128, 255, 255)) };
                painted.Composites[PaintChannel.Color] = Solid(new Color32(200, 0, 0, 255)); painted.Composites[PaintChannel.Roughness] = Solid(new Color32(80, 0, 0, 255));
                preview.SetMaterialChannels(new Dictionary<int, PreviewSlotChannels> { { 0, painted } });
                Object.DestroyImmediate(preview.RenderStatic(64, 64));
                preview.Shading = PreviewShading.Neutral; preview.Shading = PreviewShading.Material;
                preview.SetMaterialChannels(new Dictionary<int, PreviewSlotChannels> { { 0, painted } });
                Assert.That(State(), Is.EqualTo(state), "the material in memory, its dirty count and Unity's Undo are untouched");
            }
            AssetDatabase.SaveAssets();
            Assert.That(Hashes(), Is.EqualTo(hashes), "the .mat, the texture and their .meta files are unchanged");
            Assert.That(State(), Is.EqualTo(state));
        }

        /// <summary>プレビューの描画のコード（Editor/Preview）に、元のアセット・Undo を変える API が無いこと。元に入れるのは Editor/MaterialApply だけ。</summary>
        [Test] public void ThePreviewSourceCallsNoAssetOrUndoApi()
        {
            var self = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/net.yozolab.yolupainter/package.json");
            Assume.That(self, Is.Not.Null);
            foreach (var file in Directory.GetFiles(Path.Combine(self.resolvedPath, "Editor", "Preview"), "*.cs"))
            {
                string text = File.ReadAllText(file);
                foreach (var token in new[] { "Undo.", "SetDirty", "SaveAssets", "ImportAsset", "SaveAndReimport", "CreateAsset", "DeleteAsset", "File.Write", "sRGBTexture =", "textureType =" })
                    Assert.That(text, Does.Not.Contain(token), Path.GetFileName(file) + " uses " + token);
            }
        }

        // ───────── 補助 ─────────

        T Track<T>(T o) where T : Object { o.hideFlags = HideFlags.HideAndDontSave; made.Add(o); return o; }

        IsolatedModelPreview Loaded(Material material)
        {
            var go = Track(new GameObject("Preview material test model"));
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            var preview = new IsolatedModelPreview();
            var report = preview.Load(go);
            Assert.That(report.LoadedRendererCount, Is.EqualTo(1), report.ToString());
            return preview;
        }

        Texture2D Solid(Color32 c) => Pixels(Enumerable.Repeat(c, 16).ToArray());
        Texture2D Pixels(Color32[] pixels)
        {
            int n = (int)Math.Sqrt(pixels.Length); Assert.That(n * n, Is.EqualTo(pixels.Length));
            var t = Track(new Texture2D(n, n, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point });
            t.SetPixels32(pixels); t.Apply(false, false); return t;
        }
        static byte[] RawBytes(Texture2D t) => t.GetRawTextureData();
        static byte[] Bytes(Color32[] pixels) => pixels.SelectMany(p => new[] { p.r, p.g, p.b, p.a }).ToArray();

        static Color32[] Read(RenderTexture rt)
        {
            Assert.That(rt, Is.Not.Null, "no packed texture");
            var previous = RenderTexture.active; var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            try { RenderTexture.active = rt; t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false); t.Apply(); return t.GetPixels32(); }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(t); }
        }

        /// <summary>Unity の Undo の記録の数（UnityEditor.Undo.GetRecords は internal）。</summary>
        internal static string UndoRecords()
        {
            var get = typeof(Undo).GetMethod("GetRecords", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(List<string>), typeof(List<string>) }, null);
            Assume.That(get, Is.Not.Null, "UnityEditor.Undo.GetRecords is not available in this Unity");
            var undo = new List<string>(); var redo = new List<string>();
            get.Invoke(null, new object[] { undo, redo });
            return undo.Count + "/" + redo.Count + ":" + string.Join(";", undo);
        }
    }
}
