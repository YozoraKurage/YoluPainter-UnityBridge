using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.LilToon;
using Object = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// lilToon アダプター（読むだけ）。テストごとに Assets の下へ一時フォルダを作り、インストール済みの lilToon シェーダーで
    /// マテリアルと小さなテクスチャを作って検査する。lilToon が無ければ Ignore。マテリアル・テクスチャ・インポート設定を変えないことも確かめる。
    /// </summary>
    public sealed class LilToonAdapterTests
    {
        string folder;
        readonly List<Object> transient = new List<Object>();

        [SetUp] public void CreateFolder()
        {
            if (Shader.Find("lilToon") == null) Assert.Ignore("lilToon is not installed in this project (Shader.Find(\"lilToon\") is null); the adapter tests need the real package.");
            // この devcontainer の GUI モードではシェーダーのコンパイルが壊れていて、lilToon のマテリアルやアセットを作るたびに
            // シェーダーのエラーがログに出る（アダプターとは無関係。アセット操作をまたぐとログの無視の設定も戻る）。
            // GPU のテストと同じく、壊れたエディタでは飛ばして batch-gl で確かめる。
            if (EditorShaderCompiler.IsBroken) Assert.Ignore("Built-in shaders fail to compile in this Editor (devcontainer GUI mode). Run the lilToon adapter tests with test-daemon.sh start --batch-gl.");
            string name = "ZZ_LilToonAdapterTests-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", name), Is.Not.Empty);
            folder = "Assets/" + name;
        }

        [TearDown] public void DeleteFolder()
        {
            foreach (var o in transient) if (o != null) Object.DestroyImmediate(o);
            transient.Clear();
            if (folder == null) return;
            AssetDatabase.DeleteAsset(folder);
            Assert.That(AssetDatabase.IsValidFolder(folder), Is.False);
            Assert.That(Directory.Exists(Path.GetFullPath(folder)), Is.False, "nothing is left in Assets");
            Assert.That(File.Exists(Path.GetFullPath(folder + ".meta")), Is.False);
            folder = null;
        }

        // ───────────── パッケージと確認済みの表 ─────────────

        static PackageInfo LilToonPackage() => PackageInfo.FindForAssetPath(AssetDatabase.GetAssetPath(Shader.Find("lilToon")));

        /// <summary>確認済みの表は、インストール済みパッケージの Shader/ にある全シェーダー（名前とファイル）と 1 対 1。</summary>
        [Test] public void TheVariantTableMatchesEveryShaderShippedByTheInstalledPackage()
        {
            var package = LilToonPackage();
            Assert.That(package, Is.Not.Null);
            Assert.That(package.name, Is.EqualTo("jp.lilxyzw.liltoon"));
            if (package.version != "2.3.4") Assert.Ignore("The verified table describes lilToon 2.3.4; installed is " + package.version + ".");
            Assert.That(LilToonVerified.TryGet("2.3.4", out var release));
            var shipped = Directory.GetFiles(Path.Combine(package.resolvedPath, "Shader"), "*.shader").ToDictionary(f => Path.GetFileName(f), f =>
            {
                var m = Regex.Match(File.ReadAllText(f), "^\\s*Shader\\s+\"(?<name>[^\"]+)\"", RegexOptions.Multiline);
                Assert.That(m.Success, f);
                return m.Groups["name"].Value;
            });
            Assert.That(release.Variants.Values.Select(v => v.File).OrderBy(x => x), Is.EqualTo(shipped.Keys.OrderBy(x => x)), "same files");
            foreach (var file in shipped) Assert.That(release.Variants[file.Value].File, Is.EqualTo(file.Key), file.Value);
            Assert.That(release.Variants.Values.Count(v => v.Verified), Is.EqualTo(32), "Standard 10 + Tessellation 10 + Lite 10 + Multi 2");
        }

        // ───────────── 確認済みのバリアント ─────────────

        [TestCase("lilToon", "lts.shader", "Standard", "Opaque", false)]
        [TestCase("Hidden/lilToonCutout", "lts_cutout.shader", "Standard", "Cutout", false)]
        [TestCase("Hidden/lilToonTransparentOutline", "lts_trans_o.shader", "Standard", "Transparent", true)]
        [TestCase("Hidden/lilToonTwoPassTransparent", "lts_twotrans.shader", "Standard", "TwoPassTransparent", false)]
        [TestCase("Hidden/lilToonTessellationCutoutOutline", "lts_tess_cutout_o.shader", "Tessellation", "Cutout", true)]
        [TestCase("_lil/lilToonMulti", "ltsmulti.shader", "Multi", "PerMaterial(_TransparentMode)", false)]
        public void VerifiedVariantsMapAllSixChannels(string shaderName, string file, string family, string mode, bool outline)
        {
            var material = MaterialAsset(shaderName, "M", m => { });
            var report = LilToonAdapter.Inspect(material);
            Assert.That(report.IsLilToon && report.IsApplicable, report.Describe());
            Assert.That(report.Reasons, Is.Empty);
            Assert.That(report.Version, Is.EqualTo(LilToonPackage().version));
            Assert.That(report.PackageName, Is.EqualTo("jp.lilxyzw.liltoon"));
            Assert.That(report.RenderPipeline, Is.EqualTo("BuiltIn"));
            Assert.That(report.ShaderName, Is.EqualTo(shaderName));
            Assert.That(new object[] { report.Variant.File, report.Variant.Family, report.Variant.RenderMode, report.Variant.Outline, report.Variant.MappingVerified },
                Is.EqualTo(new object[] { file, family, mode, outline, true }));
            Assert.That(report.Channels.Select(c => c.Channel), Is.EqualTo(Enum.GetValues(typeof(PaintChannel)).Cast<PaintChannel>()));
            Assert.That(report.Channels.All(c => c.IsMapped));
            var expected = new Dictionary<PaintChannel, (string, LilToonColorSpace)>
            {
                { PaintChannel.Color, ("_MainTex", LilToonColorSpace.Srgb) }, { PaintChannel.Roughness, ("_SmoothnessTex", LilToonColorSpace.Linear) },
                { PaintChannel.Metallic, ("_MetallicGlossMap", LilToonColorSpace.Linear) }, { PaintChannel.Height, ("_ParallaxMap", LilToonColorSpace.Linear) },
                { PaintChannel.Normal, ("_BumpMap", LilToonColorSpace.NormalMap) }, { PaintChannel.Emission, ("_EmissionMap", LilToonColorSpace.Srgb) },
            };
            foreach (var c in report.Channels) Assert.That((c.Property, c.ColorSpace), Is.EqualTo(expected[c.Channel]), c.Channel.ToString());
            Assert.That(report.Channel(PaintChannel.Roughness).Packing, Does.Contain("1 − roughness"));
            Assert.That(report.Channel(PaintChannel.Color).TexturePath, Is.Null, "no texture assigned");
        }

        [Test] public void LiteMapsColourAndEmissionAndExplainsTheRest()
        {
            var report = LilToonAdapter.Inspect(MaterialAsset("Hidden/lilToonLiteCutoutOutline", "Lite", m => m.SetFloat("_UseEmission", 1)));
            Assert.That(report.IsApplicable, report.Describe());
            Assert.That(report.Variant.Family, Is.EqualTo("Lite"));
            Assert.That(report.Channels.Where(c => c.IsMapped).Select(c => c.Channel), Is.EquivalentTo(new[] { PaintChannel.Color, PaintChannel.Emission }));
            foreach (var c in report.Channels.Where(c => !c.IsMapped)) Assert.That(c.UnmappedReason, Does.Contain("Lite"), c.Channel.ToString());
            var emission = report.Channel(PaintChannel.Emission);
            Assert.That(emission.IsEffective, "Lite emission has no compile-time switch, only _UseEmission");
            Assert.That(emission.Packing, Does.Contain("_TriMask.b"));
        }

        /// <summary>割り当てたテクスチャ（パス・GUID・インポート設定）、トグル、コンパイル済み機能が今の値どおりに出る。
        /// コンパイル済み機能の期待値は、テスト自身がフォワードパスのソースを読んで決める（lilToon の shader setting で変わるため）。</summary>
        [Test] public void AssignedTexturesTogglesAndCompiledFeaturesAreReported()
        {
            var color = TextureAsset("color", TextureImporterType.Default, true);
            var normal = TextureAsset("normal", TextureImporterType.NormalMap, false);
            var smooth = TextureAsset("smooth", TextureImporterType.Default, false);
            var metal = TextureAsset("metal", TextureImporterType.Default, true); // わざと sRGB
            var material = MaterialAsset("lilToon", "Assigned", m =>
            {
                m.SetTexture("_MainTex", color); m.SetTexture("_BumpMap", normal); m.SetTexture("_SmoothnessTex", smooth); m.SetTexture("_MetallicGlossMap", metal);
                m.SetFloat("_UseBumpMap", 1); m.SetFloat("_UseReflection", 1); m.SetFloat("_UseEmission", 0); m.SetFloat("_UseParallax", 0);
            });
            var report = LilToonAdapter.Inspect(material);
            Assert.That(report.IsApplicable, report.Describe());

            var c = report.Channel(PaintChannel.Color);
            Assert.That(c.TexturePath, Is.EqualTo(AssetDatabase.GetAssetPath(color)));
            Assert.That(c.TextureGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(c.TexturePath)));
            Assert.That(c.TextureImportSrgb, Is.True); Assert.That(c.Warnings, Is.Empty); Assert.That(c.IsEffective);

            string passSource = File.ReadAllText(Path.Combine(LilToonPackage().resolvedPath, "Shader", "ltspass_opaque.shader"));
            bool Defined(string feature) => Regex.IsMatch(passSource, "^\\s*#define\\s+" + feature + "\\s*$", RegexOptions.Multiline);

            var n = report.Channel(PaintChannel.Normal);
            Assert.That(n.TexturePath, Is.EqualTo(AssetDatabase.GetAssetPath(normal)));
            Assert.That(n.TextureImportType, Is.EqualTo("NormalMap")); Assert.That(n.Warnings, Is.Empty);
            Assert.That(n.Conditions.Single(k => k.Name == "_UseBumpMap").Satisfied);
            Assert.That(n.Conditions.Single(k => k.Name == "LIL_FEATURE_BumpMap").Satisfied, Is.EqualTo(Defined("LIL_FEATURE_BumpMap")));
            Assert.That(n.Conditions.Single(k => k.Name == "LIL_FEATURE_BumpMap").Source, Does.EndWith("ltspass_opaque.shader"));
            Assert.That(n.IsEffective, Is.EqualTo(Defined("LIL_FEATURE_NORMAL_1ST") && Defined("LIL_FEATURE_BumpMap")));

            var r = report.Channel(PaintChannel.Roughness);
            Assert.That(r.TextureImportSrgb, Is.False); Assert.That(r.Warnings, Is.Empty);
            Assert.That(r.Conditions.Single(k => k.Name == "_UseReflection").Satisfied);
            Assert.That(r.IsEffective, Is.EqualTo(Defined("LIL_FEATURE_REFLECTION") && Defined("LIL_FEATURE_SmoothnessTex")));

            var m2 = report.Channel(PaintChannel.Metallic);
            Assert.That(m2.TextureImportSrgb, Is.True);
            Assert.That(m2.Warnings.Single(), Does.Contain("expected to be linear"));

            var e = report.Channel(PaintChannel.Emission);
            Assert.That(e.TexturePath, Is.Null);
            Assert.That(e.Conditions.Single(k => k.Name == "_UseEmission").Satisfied, Is.False);
            Assert.That(e.IsEffective, Is.False);
            Assert.That(report.Channel(PaintChannel.Height).Conditions.Single(k => k.Name == "_UseParallax").Current, Is.EqualTo("0"));
        }

        [Test] public void ANormalMapImportedAsAColourTextureIsWarned()
        {
            var plain = TextureAsset("plainnormal", TextureImporterType.Default, true);
            var report = LilToonAdapter.Inspect(MaterialAsset("lilToon", "Plain", m => m.SetTexture("_BumpMap", plain)));
            Assert.That(report.Channel(PaintChannel.Normal).Warnings.Single(), Does.Contain("Normal map"));
        }

        /// <summary>Multi はトグルではなくキーワードで機能が決まる（lil_common.hlsl が _UseBumpMap を true に固定）。トグルと食い違えば警告。</summary>
        [Test] public void MultiFollowsKeywordsAndWarnsWhenTheToggleDisagrees()
        {
            var material = new Material(Shader.Find("_lil/lilToonMulti")); transient.Add(material);
            material.SetFloat("_UseBumpMap", 1); material.DisableKeyword("_NORMALMAP");
            var normal = LilToonAdapter.Inspect(material).Channel(PaintChannel.Normal);
            Assert.That(normal.Conditions.Single().Kind, Is.EqualTo("keyword"));
            Assert.That(normal.IsEffective, Is.False);
            Assert.That(normal.Warnings.Single(), Does.Contain("_UseBumpMap"));
            material.EnableKeyword("_NORMALMAP");
            normal = LilToonAdapter.Inspect(material).Channel(PaintChannel.Normal);
            Assert.That(normal.IsEffective); Assert.That(normal.Warnings, Is.Empty);
            material.SetFloat("_UseReflection", 0); material.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
            Assert.That(LilToonAdapter.Inspect(material).Channel(PaintChannel.Metallic).IsEffective, "the keyword decides; the toggle only warns");
        }

        // ───────────── 確認していないバリアント・lilToon でないもの ─────────────

        [TestCase("Hidden/lilToonGem")]
        [TestCase("Hidden/lilToonFur")]
        [TestCase("Hidden/lilToonRefraction")]
        [TestCase("_lil/[Optional] lilToonFakeShadow")]
        [TestCase("_lil/[Optional] lilToonOutlineOnly")]
        [TestCase("_lil/[Optional] lilToonOverlay")]
        [TestCase("Hidden/lilToonMultiGem")]
        [TestCase("Hidden/ltspass_opaque")]
        public void RecognisedButUnverifiedVariantsAreNotApplicable(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            Assume.That(shader, Is.Not.Null, shaderName + " is not loadable here");
            var material = new Material(shader); transient.Add(material);
            var report = LilToonAdapter.Inspect(material);
            Assert.That(report.IsLilToon, Is.True, report.Describe());
            Assert.That(report.IsApplicable, Is.False);
            Assert.That(report.Variant.MappingVerified, Is.False);
            Assert.That(report.Channels, Is.Empty, "no guessed mapping");
            Assert.That(report.Reasons.Single(), Does.Contain("recognised"));
        }

        [Test] public void NonLilToonMaterialsGiveACleanNotLilToonResult()
        {
            var cases = new List<(string, Material)> { ("null material", null) };
            foreach (var name in new[] { "Standard", "Unlit/Texture", "Unlit/Color", "Hidden/InternalErrorShader", "Universal Render Pipeline/Lit" })
            {
                var shader = Shader.Find(name);
                if (shader == null) { TestContext.WriteLine(name + " is not available in this project; skipped."); continue; }
                var m = new Material(shader); transient.Add(m); cases.Add((name, m));
            }
            cases.Add(("material whose shader asset was deleted", MaterialWithDeletedShader()));
            foreach (var (name, material) in cases)
            {
                var report = LilToonAdapter.Inspect(material);
                Assert.That(report.IsLilToon, Is.False, name);
                Assert.That(report.IsApplicable, Is.False, name);
                Assert.That(report.Channels, Is.Empty, name);
                Assert.That(report.Reasons, Is.Not.Empty, name);
                Assert.That(report.Version, Is.EqualTo("unknown"), name);
            }
        }

        /// <summary>名前もプロパティも lilToon そっくりで、lilToon のパスを UsePass で借りていても、パッケージ外のシェーダーは lilToon として扱わない。</summary>
        [Test] public void ALookalikeShaderOutsideThePackageIsNotLilToon()
        {
            var shader = ShaderAsset("Lookalike", LookalikeSource("Hidden/lilToonFake", usePass: true));
            Assert.That(shader.name, Is.EqualTo("Hidden/lilToonFake"));
            var material = MaterialAsset(shader, "Lookalike");
            var report = LilToonAdapter.Inspect(material);
            Assert.That(report.IsLilToon, Is.False, report.Describe());
            Assert.That(report.IsApplicable, Is.False);
            Assert.That(report.Channels, Is.Empty);
            Assert.That(report.Reasons.Single(), Does.Contain("not part of the installed jp.lilxyzw.liltoon package"));
        }

        /// <summary>プロパティの検査: 期待するプロパティが無い・[Normal] でない・型が違えば理由が付く。実物の lilToon では何も出ない。</summary>
        [Test] public void MissingOrMistypedExpectedPropertiesAreReported()
        {
            Assert.That(LilToonVerified.TryGet("2.3.4", out var release));
            var specs = release.Variants["lilToon"].Channels;
            var problems = new List<string>();
            foreach (var spec in specs) LilToonAdapter.CheckProperties(Shader.Find("lilToon"), spec, problems);
            Assert.That(problems, Is.Empty, string.Join("\n", problems));

            var partial = ShaderAsset("Partial", PartialSource());
            foreach (var spec in specs) LilToonAdapter.CheckProperties(partial, spec, problems);
            Assert.That(problems, Has.Some.Contains("_SmoothnessTex is missing"));
            Assert.That(problems, Has.Some.Contains("_BumpMap as [Normal]"));
            Assert.That(problems, Has.Some.Contains("Property _EmissionColor is Float"));
        }

        // ───────────── バージョン・パイプラインのゲート ─────────────

        [TestCase("2.3.5")]
        [TestCase("2.3.3")]
        [TestCase("1.10.3")]
        [TestCase("unknown")]
        public void AVersionOutsideTheVerifiedListIsNotApplicable(string version)
        {
            var material = MaterialAsset("lilToon", "Versioned", m => m.SetFloat("_UseBumpMap", 1));
            var report = LilToonAdapter.Inspect(material, new LilToonAdapter.Options { Version = version });
            Assert.That(report.IsLilToon, Is.True);
            Assert.That(report.IsApplicable, Is.False);
            Assert.That(report.Version, Is.EqualTo(version));
            Assert.That(report.Channels, Is.Empty, "no mapping is guessed for an unverified version");
            Assert.That(report.Reasons.Single(), Does.Contain("not in the verified list (2.3.4)"));
            Assert.That(LilToonAdapter.Inspect(material).IsApplicable, "the real version still passes");
        }

        [Test] public void ARenderPipelineOtherThanBuiltInIsNotApplicable()
        {
            var material = MaterialAsset("lilToon", "Pipeline", m => { });
            var report = LilToonAdapter.Inspect(material, new LilToonAdapter.Options { RenderPipeline = "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset" });
            Assert.That(report.IsLilToon && !report.IsApplicable);
            Assert.That(report.Channels, Is.Empty);
            Assert.That(report.Reasons.Single(), Does.Contain("Render pipeline"));
        }

        // ───────────── 読むだけであること ─────────────

        /// <summary>検査の前後で、マテリアル（メモリ上のシリアライズ・キーワード・dirty）、.mat と .meta、テクスチャと .meta、インポート設定が変わらない。</summary>
        [Test] public void InspectionChangesNoMaterialTextureOrImportSetting()
        {
            var color = TextureAsset("ncolor", TextureImporterType.Default, true);
            var normal = TextureAsset("nnormal", TextureImporterType.Default, true); // 警告が出る組み合わせ
            var materials = new[]
            {
                MaterialAsset("lilToon", "Pure", m => { m.SetTexture("_MainTex", color); m.SetTexture("_BumpMap", normal); m.SetFloat("_UseBumpMap", 1); }),
                MaterialAsset("_lil/lilToonMulti", "PureMulti", m => { m.SetTexture("_EmissionMap", color); m.SetFloat("_UseEmission", 1); }),
                MaterialAsset("Hidden/lilToonLite", "PureLite", m => m.SetTexture("_MainTex", color)),
            };
            AssetDatabase.SaveAssets();
            var files = materials.Select(AssetDatabase.GetAssetPath).Concat(new[] { AssetDatabase.GetAssetPath(color), AssetDatabase.GetAssetPath(normal) })
                .SelectMany(p => new[] { p, p + ".meta" }).ToDictionary(p => p, p => File.ReadAllBytes(Path.GetFullPath(p)));
            string State() => string.Join("\n", materials.Select(m => EditorJsonUtility.ToJson(m) + "|" + string.Join(",", m.shaderKeywords) + "|" + m.renderQueue + "|" +
                EditorUtility.IsDirty(m) + "|" + EditorUtility.GetDirtyCount(m))) + "\n" + string.Join("\n", new[] { color, normal }.Select(t =>
                {
                    var i = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t));
                    return i.textureType + "|" + i.sRGBTexture + "|" + EditorUtility.GetDirtyCount(i) + "|" + EditorUtility.IsDirty(t);
                }));
            string before = State();
            foreach (var m in materials)
            {
                LilToonAdapter.Inspect(m);
                LilToonAdapter.Inspect(m, new LilToonAdapter.Options { Version = "9.9.9" });
            }
            Assert.That(State(), Is.EqualTo(before));
            AssetDatabase.SaveAssets();
            foreach (var f in files) Assert.That(File.ReadAllBytes(Path.GetFullPath(f.Key)), Is.EqualTo(f.Value), f.Key);
        }

        /// <summary>アダプターのソースが、マテリアル・アセット・インポート設定・Undo を変える API を呼ばないこと（読むだけの契約を目で確かめられる形で固定する）。</summary>
        [Test] public void TheAdapterSourceCallsNoMutatingApi()
        {
            var self = PackageInfo.FindForAssetPath("Packages/net.yozolab.yolupainter/package.json");
            Assume.That(self, Is.Not.Null, "the YoluPainter package path is not resolvable here");
            var sources = Directory.GetFiles(Path.Combine(self.resolvedPath, "Editor", "LilToon"), "*.cs");
            Assert.That(sources, Is.Not.Empty);
            var forbidden = new[]
            {
                "Undo.", "SetDirty", "SaveAssets", "SaveAssetIfDirty", "ImportAsset", "SaveAndReimport", "CreateAsset", "DeleteAsset", "MoveAsset", "File.Write", "File.Delete",
                ".SetTexture(", ".SetFloat(", ".SetInt(", ".SetColor(", ".SetVector(", "EnableKeyword", "DisableKeyword", "SetKeyword", "SetShaderPassEnabled", ".shader =", ".renderQueue =",
                "sRGBTexture =", "textureType =", "SetPlatformTextureSettings",
            };
            foreach (var file in sources)
            {
                string text = File.ReadAllText(file);
                foreach (var token in forbidden) Assert.That(text, Does.Not.Contain(token), Path.GetFileName(file) + " uses " + token);
            }
        }

        // ───────────── 補助 ─────────────

        Material MaterialAsset(string shaderName, string name, Action<Material> setup)
        {
            var shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null, shaderName);
            var material = new Material(shader);
            setup(material);
            return Save(material, name);
        }

        Material MaterialAsset(Shader shader, string name) => Save(new Material(shader), name);

        Material Save(Material material, string name)
        {
            string path = folder + "/" + name + ".mat";
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        Texture2D TextureAsset(string name, TextureImporterType type, bool srgb)
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            texture.SetPixels32(Enumerable.Repeat(new Color32(128, 128, 255, 255), 16).ToArray());
            string path = folder + "/" + name + ".png";
            File.WriteAllBytes(Path.GetFullPath(path), texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = type; importer.sRGBTexture = srgb; // テスト自身のアセットの設定
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        Shader ShaderAsset(string name, string source)
        {
            string path = folder + "/" + name + ".shader";
            File.WriteAllText(Path.GetFullPath(path), source);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Assert.That(shader, Is.Not.Null, path);
            return shader;
        }

        Material MaterialWithDeletedShader()
        {
            var shader = ShaderAsset("Doomed", UnlitSource("Hidden/YoluPainterTests/Doomed", "_MainTex (\"Texture\", 2D) = \"white\" {}", ""));
            Save(new Material(shader), "Orphan");
            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(shader));
            var material = AssetDatabase.LoadAssetAtPath<Material>(folder + "/Orphan.mat");
            Assert.That(material.shader.name, Is.EqualTo("Hidden/InternalErrorShader"), "a missing shader shows as the error shader");
            return material;
        }

        static string UnlitSource(string name, string properties, string extraPass) =>
            "Shader \"" + name + "\" {\n Properties {\n" + properties + "\n }\n SubShader {\n" + extraPass + "\n  Pass { CGPROGRAM\n#pragma vertex v\n#pragma fragment f\n#include \"UnityCG.cginc\"\n" +
            "float4 v(float4 p : POSITION) : SV_POSITION { return UnityObjectToClipPos(p); }\nfixed4 f() : SV_Target { return 1; }\nENDCG }\n }\n}\n";

        /// <summary>lilToon 2.3.4 と同じ名前・型のプロパティ（_lilToonVersion = 45 も）を持つ偽物。</summary>
        static string LookalikeSource(string name, bool usePass) => UnlitSource(name, string.Join("\n", new[]
        {
            "[MainColor] _Color (\"Color\", Color) = (1,1,1,1)", "[MainTexture] _MainTex (\"Texture\", 2D) = \"white\" {}",
            "_UseBumpMap (\"Normal\", Int) = 0", "[Normal] _BumpMap (\"Normal Map\", 2D) = \"bump\" {}", "_BumpScale (\"Scale\", Range(-10,10)) = 1",
            "_UseReflection (\"Reflection\", Int) = 0", "_Smoothness (\"Smoothness\", Range(0,1)) = 1", "_SmoothnessTex (\"Smoothness\", 2D) = \"white\" {}",
            "[Gamma] _Metallic (\"Metallic\", Range(0,1)) = 0", "_MetallicGlossMap (\"Metallic\", 2D) = \"white\" {}",
            "_UseParallax (\"Parallax\", Int) = 0", "_ParallaxMap (\"Parallax Map\", 2D) = \"gray\" {}", "_Parallax (\"Parallax Scale\", Float) = 0.02", "_ParallaxOffset (\"Offset\", Float) = 0.5",
            "_UseEmission (\"Emission\", Int) = 0", "[HDR] _EmissionColor (\"Color\", Color) = (1,1,1,1)", "_EmissionMap (\"Texture\", 2D) = \"white\" {}",
            "[HideInInspector] _lilToonVersion (\"Version\", Int) = 45",
        }), usePass ? "  UsePass \"Hidden/ltspass_opaque/FORWARD\"" : "");

        /// <summary>名前は lilToon と無関係で、期待するプロパティの一部が無い・[Normal] でない・型が違うシェーダー。</summary>
        static string PartialSource() => UnlitSource("Hidden/YoluPainterTests/Partial", string.Join("\n", new[]
        {
            "_Color (\"Color\", Color) = (1,1,1,1)", "_MainTex (\"Texture\", 2D) = \"white\" {}",
            "_UseBumpMap (\"Normal\", Int) = 0", "_BumpMap (\"Normal Map\", 2D) = \"bump\" {}", "_BumpScale (\"Scale\", Float) = 1",
            "_UseEmission (\"Emission\", Int) = 0", "_EmissionColor (\"Not a colour\", Float) = 1", "_EmissionMap (\"Texture\", 2D) = \"white\" {}",
        }), "");
    }
}
