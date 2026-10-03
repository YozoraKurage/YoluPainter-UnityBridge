using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 3D ビューの環境（内蔵の空・プロジェクトのテクスチャ）・自己の影・トーンマッピング: 既定は前の版と同じ絵、環境の回転・明るさ・背景、
    /// 影のオン/オフ（中立の表示とマテリアル表示）、露出とトーンマッピングで画素が期待の向きに変わる。元のテクスチャ（取り込みの設定・.meta・
    /// dirty・読み取りの可否）とシーンの RenderSettings は変わらない。設定の保存と以前の値の既定。シェーダーで描くので batch-gl。
    /// </summary>
    [Category("GPU")]
    public sealed class PreviewEnvironmentTests
    {
        readonly List<Object> made = new List<Object>();
        string folder;

        [SetUp] public void RequireShaders()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
            GpuTests.RequireWorkingShader(IsolatedModelPreview.LitShaderName);
            GpuTests.RequireWorkingShader(PreviewEnvironment.ShaderName);
            GpuTests.RequireWorkingShader(PreviewShadowMap.ShaderName);
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewToneMap");
        }

        [TearDown] public void CleanUp()
        {
            foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            made.Clear();
            if (folder != null) { AssetDatabase.DeleteAsset(folder); folder = null; }
        }

        [Test] public void TheDefaultsDrawThePreviousPicture()
        {
            using (var p = Demo())
            {
                var before = Render(p);
                p.Scene = PreviewSceneSettings.Default();
                var defaults = Render(p);
                Assert.That(defaults.GetPixels32(), Is.EqualTo(before.GetPixels32()), "the default settings are the former scene");
                Assert.That(p.EnvironmentShown, Is.False); Assert.That(p.ShadowShown, Is.False); Assert.That(p.ToneMapped, Is.False);
                Assert.That(p.RenderedMaterial(0).shader.name, Is.EqualTo("Hidden/YoluPainter/PreviewSurface"), "the former neutral shader, not the lit one");
                p.Scene = new PreviewSceneSettings { shadows = true }; Render(p);
                Assert.That(p.RenderedMaterial(0).shader.name, Is.EqualTo(IsolatedModelPreview.LitShaderName), "the lit shader only while an environment or shadows are used");
                p.Scene = PreviewSceneSettings.Default();
                Assert.That(Render(p).GetPixels32(), Is.EqualTo(before.GetPixels32()), "and back");
                Assert.That(p.RenderedMaterial(0).shader.name, Is.EqualTo("Hidden/YoluPainter/PreviewSurface"));
            }
        }

        [Test] public void TheSkyLightsFromAboveAndShowsAsTheBackground()
        {
            using (var p = Demo())
            {
                var s = Dark(); s.environment = PreviewEnvironmentSource.Sky; s.environmentBackground = false;
                s.skyZenith = Color.white; s.skyHorizon = new Color(.5f, .5f, .5f, 1); s.skyGround = Color.black;
                p.Scene = s;
                var lit = Render(p);
                Assert.That(p.EnvironmentShown, Is.True); Assert.That(p.DisplayNote, Is.Null);
                var sh = p.Environment.Ambient; var up = new Color[2]; sh.Evaluate(new[] { Vector3.up, Vector3.down }, up);
                Assert.That(up[0].r, Is.GreaterThan(up[1].r + .3f), "the SH of a white zenith over a black ground is bright upwards: " + up[0] + " / " + up[1]);
                p.ViewFrom(25, 40); var top = Mean(Region(Render(p), .5f, .62f)); // 上から見ると上の面が多い
                p.ViewFrom(25, -40); var bottom = Mean(Region(Render(p), .5f, .38f));
                Assert.That(top, Is.GreaterThan(bottom + 20), "faces seen from above are lit by the sky, from below by the dark ground");
                // 背景: 空の色（明るさを掛けて）
                s.environmentBackground = true; s.environmentBlur = 0; p.ViewFrom(0, -60); // 下から見上げる
                var image = Render(p);
                Assert.That(p.BackgroundShown, Is.True);
                var corner = image.GetPixel(2, 62);
                Assert.That(corner.r, Is.GreaterThan(.5f), "looking up, the background corner is the bright sky: " + corner);
                s.environmentIntensity = .25f; var dim = Render(p).GetPixel(2, 62);
                Assert.That(dim.r, Is.LessThan(corner.r - .2f), "the brightness scales the background too");
                Assert.That(Mean(lit), Is.Not.EqualTo(Mean(Render(PreviewSceneSettings.Default(), p))));
            }
        }

        [Test] public void ATextureEnvironmentTurnsWithItsRotationAndLeavesTheAssetAlone()
        {
            // 緯度経度: +X の側（u = 0.5 の左右 1/4）だけ赤く明るい、ほかは暗い青
            var texture = LatLongAsset(256, 128, (u, v) => Mathf.Abs(Mathf.Repeat(u - .5f + .5f, 1) - .5f) < .25f ? new Color32(255, 40, 30, 255) : new Color32(10, 20, 60, 255));
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
            Assert.That(texture.isReadable, Is.False, "the GPU path is the one checked");
            string meta = Hash(AssetDatabase.GetAssetPath(texture) + ".meta"), settings = EditorJsonUtility.ToJson(importer); int dirty = EditorUtility.GetDirtyCount(texture);
            using (var p = Demo())
            {
                var s = Dark(); s.environment = PreviewEnvironmentSource.Texture; s.environmentBackground = false;
                p.Scene = s; p.EnvironmentTexture = texture;
                p.ViewFrom(-90, 0); // カメラは +X 側から −X を見る（PreviewSceneSettings.CameraAngles の Right）
                var facing = Mean(Region(Render(p), .5f, .5f), c => c.r - c.b);
                Assert.That(p.EnvironmentShown, Is.True, p.DisplayNote);
                s.environmentRotation = 180;
                var turned = Mean(Region(Render(p), .5f, .5f), c => c.r - c.b);
                Assert.That(facing, Is.GreaterThan(turned + 15), "the red side lights the face toward +X, and moves away when turned 180°: " + facing + " / " + turned);
                Assert.That(p.Environment.Bakes, Is.EqualTo(2), "baked again only because the rotation changed");
                Render(p); Assert.That(p.Environment.Bakes, Is.EqualTo(2), "unchanged settings are not baked again");
            }
            Assert.That(texture.isReadable, Is.False); Assert.That(EditorUtility.GetDirtyCount(texture), Is.EqualTo(dirty));
            Assert.That(EditorJsonUtility.ToJson(importer), Is.EqualTo(settings)); Assert.That(Hash(AssetDatabase.GetAssetPath(texture) + ".meta"), Is.EqualTo(meta));
        }

        [Test] public void TurningOnlyRotatesTheAmbientWithoutReadingItBack()
        {
            // 式: 回した SH を向き d で読むと、元の SH を R(−θ) d で読んだ値
            var random = new System.Random(7); var sh = new SphericalHarmonicsL2();
            for (int c = 0; c < 3; c++) for (int i = 0; i < 9; i++) sh[c, i] = (float)(random.NextDouble() * 2 - 1);
            foreach (float degrees in new[] { 30f, 90f, -125f, 200f })
            {
                var turned = PreviewEnvironment.RotateY(sh, degrees); var q = Quaternion.Euler(0, -degrees, 0);
                for (int k = 0; k < 20; k++)
                {
                    var d = new Vector3((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1).normalized;
                    var a = new Color[1]; var b = new Color[1]; turned.Evaluate(new[] { d }, a); sh.Evaluate(new[] { q * d }, b);
                    Assert.That(a[0].r, Is.EqualTo(b[0].r).Within(1e-4f), degrees + "° at " + d);
                }
            }
            // 焼き: 向きだけを変えたら読み戻さず回す。直に読み戻した SH と合う
            var texture = LatLongAsset(256, 128, (u, v) => u < .3f ? new Color32(255, 200, 40, 255) : new Color32(20, 30, 80, 255));
            var s = PreviewSceneSettings.Default(); s.environment = PreviewEnvironmentSource.Texture;
            using (var turnedOnly = new PreviewEnvironment()) using (var direct = new PreviewEnvironment())
            {
                Assert.That(turnedOnly.Update(s, texture), Is.True, turnedOnly.Problem);
                s.environmentRotation = 70; turnedOnly.Update(s, texture); direct.Update(s, texture);
                var x = new Color[3]; var y = new Color[3]; var dirs = new[] { Vector3.right, Vector3.forward, new Vector3(-.6f, .3f, .7f).normalized };
                turnedOnly.Ambient.Evaluate(dirs, x); direct.Ambient.Evaluate(dirs, y);
                for (int i = 0; i < 3; i++) Assert.That(Mathf.Abs(x[i].r - y[i].r) + Mathf.Abs(x[i].b - y[i].b), Is.LessThan(.03f), "rotated " + x[i] + " / read back " + y[i]);
            }
        }

        [Test] public void ShadowsDarkenWhatTheLightCannotReachInBothViews()
        {
            var material = Track(new Material(Shader.Find("Standard")));
            var model = Track(new GameObject("Shadow test"));
            void Part(Vector3 position, Vector3 scale)
            {
                var go = new GameObject("part") { hideFlags = HideFlags.HideAndDontSave }; go.transform.SetParent(model.transform, false);
                go.transform.localPosition = position; go.transform.localScale = scale;
                go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx"); go.AddComponent<MeshRenderer>().sharedMaterial = material;
            }
            Part(Vector3.zero, new Vector3(4, .1f, 4)); Part(new Vector3(0, 1.2f, 0), new Vector3(1.2f, .2f, 1.2f)); // 床と、その上の板
            using (var p = new IsolatedModelPreview())
            {
                Assert.That(p.Load(model).LoadedRendererCount, Is.EqualTo(2));
                p.SetPaintTexture(White(), -1);
                // 光は +Z の斜め上から（影は −Z の側の床に落ちる）。カメラは −Z の側の上から（影が板に隠れない）
                var s = PreviewSceneSettings.Default(); s.lightYaw = 0; s.lightPitch = 45; s.shadowSoftness = 0; p.Scene = s;
                p.ViewFrom(0, 60);
                foreach (var shading in new[] { PreviewShading.Neutral, PreviewShading.Material })
                {
                    p.Shading = shading; if (shading == PreviewShading.Material) p.SetMaterialChannels(null);
                    s.shadows = false; var open = Render(p);
                    s.shadows = true; var shadowed = Render(p);
                    Assert.That(p.ShadowShown, Is.True, p.DisplayNote);
                    if (shading == PreviewShading.Material) Assert.That(p.ShadowOverlayDrawn, Is.True);
                    // 板の縁の外側から床を見て、影の帯が暗くなった画素の数
                    int darker = 0;
                    var a = open.GetPixels32(); var b = shadowed.GetPixels32();
                    for (int i = 0; i < a.Length; i++) if (a[i].r - b[i].r > 25) darker++;
                    Assert.That(darker, Is.GreaterThan(30), shading + ": the floor under the board is in its shadow");
                    Assert.That(Mean(shadowed), Is.LessThan(Mean(open)), shading.ToString());
                }
                Assert.That(p.Shadows.Renders, Is.EqualTo(1), "the shadow map is drawn again only when the light, the bounds or the shape change");
                s.lightYaw = 40; s.lightPitch = 50; Render(p);
                Assert.That(p.Shadows.Renders, Is.EqualTo(2));
            }
        }

        [Test] public void ExposureAndToneMappingChangeOnlyThe3DPicture()
        {
            using (var p = Demo())
            {
                var s = PreviewSceneSettings.Default(); s.intensity = 3; s.ambient = new Color(.8f, .8f, .8f, 1); p.Scene = s;
                var clipped = Render(p);
                Assert.That(p.ToneMapped, Is.False);
                int saturated = clipped.GetPixels32().Count(c => c.r == 255);
                Assert.That(saturated, Is.GreaterThan(50), "a strong light clips without tone mapping");
                s.toneMapping = PreviewToneMapping.Aces; var aces = Render(p);
                Assert.That(p.ToneMapped, Is.True);
                Assert.That(aces.GetPixels32().Count(c => c.r == 255), Is.LessThan(saturated / 4), "ACES rolls the highlights off below white");
                s.toneMapping = PreviewToneMapping.Neutral; var neutral = Render(p);
                Assert.That(neutral.GetPixels32(), Is.Not.EqualTo(aces.GetPixels32()));
                s.toneMapping = PreviewToneMapping.None; s.intensity = 1; s.ambient = new Color(.5f, .5f, .5f, 1);
                var plain = Render(p); s.exposure = 1; var brighter = Render(p); s.exposure = -1; var darker = Render(p);
                Assert.That(Mean(brighter), Is.GreaterThan(Mean(plain) + 10)); Assert.That(Mean(darker), Is.LessThan(Mean(plain) - 10));
                s.exposure = 0; Render(p);
                Assert.That(p.ToneMapped, Is.False, "back to the 8-bit picture of the former version");
            }
        }

        [Test] public void TheSceneOfTheProjectIsLeftAlone()
        {
            var ambientMode = RenderSettings.ambientMode; var ambient = RenderSettings.ambientLight; var probe = RenderSettings.ambientProbe;
            var reflection = RenderSettings.customReflectionTexture; var reflectionMode = RenderSettings.defaultReflectionMode; float reflectionIntensity = RenderSettings.reflectionIntensity;
            int lights = Object.FindObjectsOfType<Light>().Length; var skybox = RenderSettings.skybox;
            var material = Track(new Material(Shader.Find("Standard")));
            var go = Track(new GameObject("Scene test")); go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx"); go.AddComponent<MeshRenderer>().sharedMaterial = material;
            using (var p = new IsolatedModelPreview())
            {
                p.Load(go); p.Shading = PreviewShading.Material; p.SetMaterialChannels(null);
                p.Scene = new PreviewSceneSettings { environment = PreviewEnvironmentSource.Sky, shadows = true, toneMapping = PreviewToneMapping.Aces, exposure = 1, environmentIntensity = 2 };
                Render(p);
                Assert.That(p.EnvironmentShown && p.ShadowShown && p.ToneMapped, Is.True, p.DisplayNote);
            }
            Assert.That(RenderSettings.ambientMode, Is.EqualTo(ambientMode)); Assert.That(RenderSettings.ambientLight, Is.EqualTo(ambient)); Assert.That(RenderSettings.ambientProbe, Is.EqualTo(probe));
            Assert.That(RenderSettings.customReflectionTexture, Is.SameAs(reflection)); Assert.That(RenderSettings.defaultReflectionMode, Is.EqualTo(reflectionMode)); Assert.That(RenderSettings.reflectionIntensity, Is.EqualTo(reflectionIntensity));
            Assert.That(RenderSettings.skybox, Is.SameAs(skybox)); Assert.That(Object.FindObjectsOfType<Light>().Length, Is.EqualTo(lights));
        }

        [Test] public void WrongTexturesAreRefusedAndTheSkyIsShownInstead()
        {
            Assert.That(PreviewEnvironment.Refusal(null), Is.Not.Null);
            var volume = Track(new Texture3D(4, 4, 4, TextureFormat.RGBA32, false));
            Assert.That(PreviewEnvironment.Refusal(volume), Does.Contain("Cubemap"));
            var rt = Track(new RenderTexture(8, 8, 0)); Assert.That(PreviewEnvironment.Refusal(rt), Does.Contain("render texture"));
            var tiny = Track(new Texture2D(4, 2)); Assert.That(PreviewEnvironment.Refusal(tiny), Does.Contain("at least"));
            Assert.That(PreviewEnvironment.AspectNote(Track(new Texture2D(64, 64))), Does.Contain("not 2:1"));
            Assert.That(TexturePaintWindow.EnvironmentTextureRefusal(Track(new Texture2D(64, 32))), Does.Contain("not a texture asset"), "a texture only in memory cannot be found again by its GUID");
            using (var p = Demo())
            {
                p.Scene = new PreviewSceneSettings { environment = PreviewEnvironmentSource.Texture }; p.EnvironmentTexture = volume;
                Render(p);
                Assert.That(p.EnvironmentShown, Is.True, "drawn with the built-in sky"); Assert.That(p.DisplayNote, Does.Contain("built-in sky"));
            }
        }

        [Test] public void TheWindowRemembersTheTextureByGuidAndFallsBackToTheSkyWhenItIsGone()
        {
            var texture = LatLongAsset(64, 32, (u, v) => new Color32(200, 200, 255, 255));
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>(); string recovery = w.RecoveryRoot;
            try
            {
                Assert.That(w.UseEnvironmentTexture(Track(new Texture2D(64, 32))), Is.False, "a texture only in memory");
                Assert.That(w.UseEnvironmentTexture(texture), Is.True, w.StatusMessage);
                Assert.That(w.PreviewScene.environment, Is.EqualTo(PreviewEnvironmentSource.Texture));
                Assert.That(w.PreviewScene.environmentTexture, Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(texture))));
                Assert.That(EditorJsonUtility.ToJson(w), Does.Contain(w.PreviewScene.environmentTexture), "kept as window state");
                w.SyncEnvironment(); Assert.That(w.Preview.EnvironmentTexture, Is.SameAs(texture));
                AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(texture));
                w.SyncEnvironment();
                Assert.That(w.PreviewScene.environment, Is.EqualTo(PreviewEnvironmentSource.Sky)); Assert.That(w.StatusMessage, Does.Contain("is gone"));
                Assert.That(w.Preview.EnvironmentTexture, Is.Null);
            }
            finally { Object.DestroyImmediate(w); if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
        }

        [Test] public void TheSettingsAreSavedAndOlderOnesGetTheFormerDefaults()
        {
            var s = new PreviewSceneSettings { environment = PreviewEnvironmentSource.Texture, environmentTexture = "0123456789abcdef0123456789abcdef", environmentRotation = 30, environmentIntensity = 1.5f,
                environmentBackground = false, environmentBlur = .7f, shadows = true, shadowSoftness = .6f, toneMapping = PreviewToneMapping.Aces, exposure = -.5f };
            var back = JsonUtility.FromJson<PreviewSceneSettings>(JsonUtility.ToJson(s));
            Assert.That(JsonUtility.ToJson(back), Is.EqualTo(JsonUtility.ToJson(s)));
            // 前の版の窓の状態（環境・影・トーンマッピングの項目が無い）
            var old = JsonUtility.FromJson<PreviewSceneSettings>("{\"lightYaw\":10,\"lightPitch\":20,\"intensity\":1.5,\"lightColor\":{\"r\":1,\"g\":1,\"b\":1,\"a\":1},\"ambient\":{\"r\":0.5,\"g\":0.5,\"b\":0.5,\"a\":1},\"background\":{\"r\":0.1,\"g\":0.1,\"b\":0.1,\"a\":1}}").Normalized();
            Assert.That((old.environment, old.shadows, old.toneMapping, old.exposure, old.environmentIntensity), Is.EqualTo((PreviewEnvironmentSource.None, false, PreviewToneMapping.None, 0f, 1f)));
            Assert.That(old.UsesToneMapping, Is.False); Assert.That(old.lightYaw, Is.EqualTo(10));
            var broken = new PreviewSceneSettings { environment = (PreviewEnvironmentSource)42, toneMapping = (PreviewToneMapping)9, exposure = float.NaN, environmentIntensity = 99, shadowSoftness = -3, environmentTexture = null }.Normalized();
            Assert.That((broken.environment, broken.toneMapping, broken.exposure, broken.environmentIntensity, broken.shadowSoftness, broken.environmentTexture), Is.EqualTo((PreviewEnvironmentSource.None, PreviewToneMapping.None, 0f, 8f, 0f, "")));
            var turn = PreviewSceneSettings.Default(); float yaw = turn.lightYaw; turn.RotateLighting(200);
            Assert.That(turn.environmentRotation, Is.EqualTo(-160).Within(1e-3)); Assert.That(Mathf.DeltaAngle(yaw + 200, turn.lightYaw), Is.EqualTo(0).Within(1e-3), "the light turns with the environment");
        }

        // ───────── 補助 ─────────

        T Track<T>(T o) where T : Object { o.hideFlags = HideFlags.HideAndDontSave; made.Add(o); return o; }
        Texture2D White() { var t = Track(new Texture2D(4, 4, TextureFormat.RGBA32, false, true)); t.SetPixels32(Enumerable.Repeat(new Color32(255, 255, 255, 255), 16).ToArray()); t.Apply(); return t; }
        IsolatedModelPreview Demo() { var p = new IsolatedModelPreview(); p.LoadDemoMesh(); p.SetPaintTexture(White(), 0); return p; }
        /// <summary>主な光と一様な環境光の無いシーン（環境だけが照らす）。</summary>
        static PreviewSceneSettings Dark() { var s = PreviewSceneSettings.Default(); s.intensity = 0; s.ambient = Color.black; s.background = Color.black; return s; }
        Texture2D Render(IsolatedModelPreview p) { var t = p.RenderStatic(64, 64); made.Add(t); return t; }
        Texture2D Render(PreviewSceneSettings s, IsolatedModelPreview p) { var keep = p.Scene; p.Scene = s; try { return Render(p); } finally { p.Scene = keep; } }
        static float Mean(Texture2D image) => (float)image.GetPixels32().Average(c => (c.r + c.g + c.b) / 3.0);
        static float Mean(Color32[] pixels) => (float)pixels.Average(c => (c.r + c.g + c.b) / 3.0);
        static float Mean(Color32[] pixels, Func<Color32, float> f) => (float)pixels.Average(c => (double)f(c));
        /// <summary>画像の (u, v) を中心とする 9 × 9 の画素。</summary>
        static Color32[] Region(Texture2D image, float u, float v)
        {
            int cx = Mathf.RoundToInt(u * image.width), cy = Mathf.RoundToInt(v * image.height); var list = new List<Color32>();
            for (int y = cy - 4; y <= cy + 4; y++) for (int x = cx - 4; x <= cx + 4; x++) list.Add(image.GetPixel(x, y));
            return list.ToArray();
        }

        static string Hash(string assetPath) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.GetFullPath(assetPath)))); }

        /// <summary>読み取りの許されていない（Read/Write オフの）緯度経度の PNG のアセット。</summary>
        Texture2D LatLongAsset(int w, int h, Func<float, float, Color32> color)
        {
            folder = "Assets/ZZ_PreviewEnvironmentTests-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) pixels[y * w + x] = color((x + .5f) / w, (y + .5f) / h);
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false); t.SetPixels32(pixels); t.Apply();
            string path = folder + "/Environment.png";
            File.WriteAllBytes(Path.GetFullPath(path), t.EncodeToPNG()); Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.isReadable = false; importer.mipmapEnabled = true; importer.wrapModeU = TextureWrapMode.Repeat; importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
