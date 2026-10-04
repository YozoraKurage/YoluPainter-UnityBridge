using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.LilToon;
using Yozolab.YoluPainter.Editor.LilToonApply;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// lilToon への割り当て。確かめたバージョン・バリアントだけに、lilToon の読み方に合わせて変換した PNG を書き出し、マテリアルの
    /// テクスチャ・トグル・キーワードを 1 つの Undo で変える。計画を作るだけでは何も変えず、適用できないものは理由を示して断る。
    /// 本物の lilToon が要る（無ければ、またはシェーダーの壊れたエディタでは Ignore）。
    /// </summary>
    public sealed class LilToonAssignmentTests
    {
        string folder;

        [SetUp] public void CreateFolder()
        {
            if (Shader.Find("lilToon") == null) Assert.Ignore("lilToon is not installed in this project; the assignment tests need the real package.");
            if (EditorShaderCompiler.IsBroken) Assert.Ignore("Built-in shaders fail to compile in this Editor (devcontainer GUI mode); creating lilToon materials logs those errors. Use test-daemon.sh start --batch-gl.");
            string name = "ZZ_LilToonAssignmentTests-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", name), Is.Not.Empty);
            folder = "Assets/" + name;
        }

        [TearDown] public void DeleteFolder()
        {
            if (folder == null) return;
            AssetDatabase.DeleteAsset(folder);
            Assert.That(Directory.Exists(Path.GetFullPath(folder)), Is.False, "nothing is left in Assets");
            folder = null;
        }

        static PaintDocument Painted(params PaintChannel[] channels)
        {
            var d = new PaintDocument(8, 8, 8); var layer = d.AddLayer("L");
            foreach (var c in channels)
            {
                d.SetChannelEnabled(layer.Id, c, true);
                var value = c == PaintChannel.Roughness ? new Rgba32(64, 64, 64) : c == PaintChannel.Normal ? new Rgba32(200, 128, 230) : new Rgba32(30, 120, 220);
                layer.GetChannel(c).SetPixel(2, 3, value);
            }
            d.ClearHistory(); return d;
        }

        Material MaterialAsset(string shaderName, string name)
        {
            var material = new Material(Shader.Find(shaderName));
            string path = folder + "/" + name + ".mat";
            AssetDatabase.CreateAsset(material, path); AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        static Color32 PixelOf(string assetPath, int x, int y)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try { Assert.That(t.LoadImage(File.ReadAllBytes(Path.GetFullPath(assetPath))), Is.True); return t.GetPixels32()[y * t.width + x]; }
            finally { Object.DestroyImmediate(t); }
        }

        [Test] public void AssignsConvertedTexturesAndTurnsTheMapsOnAsOneUndo()
        {
            var all = (PaintChannel[])Enum.GetValues(typeof(PaintChannel));
            var d = Painted(all);
            var material = MaterialAsset("lilToon", "Body");
            float bumpBefore = material.GetFloat("_UseBumpMap");
            var plan = LilToonAssignment.Plan(d, material, folder + "/Out", "Doc");
            Assert.That(plan.CanApply, Is.True, plan.Describe());
            Assert.That(plan.CreatesFolder, Is.True);
            Assert.That(plan.Items.Select(i => i.Channel), Is.EquivalentTo(all));
            Assert.That(plan.Describe(), Does.Contain("_SmoothnessTex").And.Contain("lilToon 2.3.4").And.Contain("1 − roughness"));
            Assert.That(AssetDatabase.IsValidFolder(folder + "/Out"), Is.False, "planning changes nothing");
            Assert.That(material.GetTexture("_BumpMap"), Is.Null);

            var after = LilToonAssignment.Apply(plan, d);
            Assert.That(after.IsApplicable, Is.True);
            foreach (var item in plan.Items)
                Assert.That(AssetDatabase.GetAssetPath(material.GetTexture(item.Property)), Is.EqualTo(item.AssetPath), item.Property);
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(folder + "/Out/Doc_lilToon_Normal.png")).textureType, Is.EqualTo(TextureImporterType.NormalMap));
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(folder + "/Out/Doc_lilToon_Roughness.png")).sRGBTexture, Is.False);
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(folder + "/Out/Doc_lilToon_Color.png")).sRGBTexture, Is.True);
            Assert.That(PixelOf(folder + "/Out/Doc_lilToon_Roughness.png", 2, 3).r, Is.EqualTo(255 - 64), "lilToon reads smoothness = 1 − roughness");
            Assert.That(PixelOf(folder + "/Out/Doc_lilToon_Roughness.png", 0, 0).r, Is.EqualTo(255), "unpainted = roughness 0 = smoothness 1 (lilToon without a map)");
            Assert.That(PixelOf(folder + "/Out/Doc_lilToon_Normal.png", 0, 0), Is.EqualTo(new Color32(128, 128, 255, 255)), "unpainted normal is flat");
            foreach (var toggle in new[] { "_UseBumpMap", "_UseReflection", "_UseParallax", "_UseEmission" }) Assert.That(material.GetFloat(toggle), Is.EqualTo(1), toggle);

            Undo.PerformUndo();
            Assert.That(material.GetTexture("_BumpMap"), Is.Null, "Unity's Undo reverts the material");
            Assert.That(material.GetFloat("_UseBumpMap"), Is.EqualTo(bumpBefore));
        }

        [Test] public void TheNormalMapIsTheNormalOutputIncludingHeightToNormal()
        {
            var d = Painted(PaintChannel.Height); // Normal のレイヤーは無い
            d.SetNormalSettings(new NormalSettings(true, 4, HeightEdgeMode.Clamp, NormalYDirection.OpenGL));
            var material = MaterialAsset("lilToon", "Body");
            var plan = LilToonAssignment.Plan(d, material, folder, "H");
            Assert.That(plan.Items.Select(i => i.Channel), Does.Contain(PaintChannel.Normal), "a derived normal is assigned even without Normal layers");
            LilToonAssignment.Apply(plan, d);
            var expected = YlpContent.Image(d, PaintChannel.Normal);
            var px = PixelOf(folder + "/H_lilToon_Normal.png", 3, 3); int i0 = (3 * 8 + 3) * 4; // 盛り上がりの隣（中心は左右対称で傾かない）
            Assert.That(new[] { px.r, px.g, px.b, px.a }, Is.EqualTo(new[] { expected[i0], expected[i0 + 1], expected[i0 + 2], expected[i0 + 3] }), "the same pixels as the .ylp's Normal texture");
            Assert.That(px.r != 128 || px.g != 128, Is.True, "the height bump tilts the normal");
        }

        [Test] public void MultiTurnsItsKeywordsOn()
        {
            var d = Painted(PaintChannel.Normal, PaintChannel.Emission);
            var material = MaterialAsset("_lil/lilToonMulti", "Multi");
            var plan = LilToonAssignment.Plan(d, material, folder, "M");
            Assert.That(plan.CanApply, Is.True, plan.Describe());
            Assert.That(plan.Items.SelectMany(i => i.Keywords), Does.Contain("_NORMALMAP").And.Contain("_EMISSION"));
            LilToonAssignment.Apply(plan, d);
            Assert.That(material.IsKeywordEnabled("_NORMALMAP") && material.IsKeywordEnabled("_EMISSION"), Is.True);
        }

        [Test] public void ExistingTexturesKeepTheirImportSettingsAndAreReported()
        {
            var d = Painted(PaintChannel.Roughness);
            var material = MaterialAsset("lilToon", "Body");
            string path = folder + "/R_lilToon_Roughness.png";
            File.WriteAllBytes(Path.GetFullPath(path), YlpContent.EncodePng(new byte[8 * 8 * 4], 8, 8));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path); importer.sRGBTexture = true; importer.SaveAndReimport(); // テスト自身のアセット
            var plan = LilToonAssignment.Plan(d, material, folder, "R");
            Assert.That(plan.Items.Single(i => i.Channel == PaintChannel.Roughness).NewFile, Is.False);
            Assert.That(plan.Items.Single(i => i.Channel == PaintChannel.Roughness).Warnings, Has.Some.Contains("import settings are left as they are"));
            LilToonAssignment.Apply(plan, d);
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(path)).sRGBTexture, Is.True, "an existing texture's import settings are not changed");
            Assert.That(PixelOf(path, 2, 3).r, Is.EqualTo(255 - 64), "the pixels are replaced");
        }

        [Test] public void RefusesWhatItCannotSafelyChange()
        {
            var d = Painted(PaintChannel.Color);
            Assert.That(LilToonAssignment.Plan(d, null, folder, "X").Refusals, Has.Some.Contains("no source material"));
            var standard = MaterialAsset("Standard", "Std");
            Assert.That(LilToonAssignment.Plan(d, standard, folder, "X").Refusals, Has.Some.Contains("Not a lilToon shader"));
            var lil = MaterialAsset("lilToon", "Body");
            Assert.That(LilToonAssignment.Plan(d, lil, folder, "X", new LilToonAdapter.Options { Version = "9.9.9" }).Refusals, Has.Some.Contains("not in the verified list"), "only verified versions");
            Assert.That(LilToonAssignment.Plan(d, lil, "Packages/x", "X").Refusals, Has.Some.Contains("inside Assets"));
            Assert.That(LilToonAssignment.Plan(new PaintDocument(8, 8, 8), lil, folder, "X").Refusals, Has.Some.Contains("No channel in use"));
            var inMemory = new Material(Shader.Find("lilToon"));
            try { Assert.That(LilToonAssignment.Plan(d, inMemory, folder, "X").Refusals, Has.Some.Contains("not an asset")); }
            finally { Object.DestroyImmediate(inMemory); }
            var layer = d.Layers[0];
            using (var s = d.BeginStroke(layer.Id, PaintChannel.Color, new BrushSettings()))
            {
                Assert.That(LilToonAssignment.Plan(d, lil, folder, "X").Refusals, Has.Some.Contains("A stroke is in progress"));
                s.Cancel();
            }
            var refused = LilToonAssignment.Plan(d, standard, folder, "X");
            Assert.That(() => LilToonAssignment.Apply(refused, d), Throws.InvalidOperationException);
            Assert.That(Directory.GetFiles(Path.GetFullPath(folder), "*.png"), Is.Empty, "nothing was written");
        }

        [Test] public void APlanIsCheckedAgainBeforeApplying()
        {
            var d = Painted(PaintChannel.Color);
            var material = MaterialAsset("lilToon", "Body");
            var plan = LilToonAssignment.Plan(d, material, folder, "X");
            material.shader = Shader.Find("Standard"); // 確認の間に別のシェーダーに変わった
            Assert.That(() => LilToonAssignment.Apply(plan, d), Throws.InvalidOperationException.With.Message.Contains("changed since the plan"));
            Assert.That(Directory.GetFiles(Path.GetFullPath(folder), "*.png"), Is.Empty);
        }

        [Test] public void ConversionMatchesWhatLilToonReads()
        {
            var px = new byte[] { 64, 0, 0, 255, 64, 0, 0, 128, 9, 9, 9, 0 };
            Assert.That(LilToonAssignment.Convert(PaintChannel.Roughness, px), Is.EqualTo(new byte[] { 191, 191, 191, 255, 223, 223, 223, 255, 255, 255, 255, 255 }), "1 − value × alpha");
            Assert.That(LilToonAssignment.Convert(PaintChannel.Metallic, px), Is.EqualTo(new byte[] { 64, 64, 64, 255, 32, 32, 32, 255, 0, 0, 0, 255 }));
            var n = new byte[] { 255, 128, 128, 255, 255, 128, 128, 0 };
            Assert.That(LilToonAssignment.Convert(PaintChannel.Normal, n), Is.EqualTo(new byte[] { 255, 128, 128, 255, 128, 128, 255, 255 }), "unpainted normal is flat");
            Assert.That(LilToonAssignment.Convert(PaintChannel.Color, px), Is.EqualTo(px));
        }
    }
}
