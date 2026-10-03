using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>3D ビューの擬似的なシーン: 既定は前の版の中立の照明と同じ値、光のプリセット・強さ・背景が中立とマテリアルの両方の絵を変え、
    /// カメラのプリセットは向きを決めてモデルに合わせる。シーンの RenderSettings とライトには触れない。シェーダーで描くので batch-gl。</summary>
    [Category("GPU")]
    public sealed class PreviewSceneTests
    {
        readonly List<Object> made = new List<Object>();
        [SetUp] public void RequireShader() { GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface"); }
        [TearDown] public void CleanUp() { foreach (var o in made) if (o != null) Object.DestroyImmediate(o); made.Clear(); }

        static float Mean(Texture2D image) => image.GetPixels32().Average(p => (p.r + p.g + p.b) / 3f);
        Texture2D Render(IsolatedModelPreview p) { var t = p.RenderStatic(96, 96); made.Add(t); return t; }
        Texture2D White() { var t = new Texture2D(4, 4, TextureFormat.RGBA32, false, true); t.SetPixels32(Enumerable.Repeat(new Color32(255, 255, 255, 255), 16).ToArray()); t.Apply(); made.Add(t); return t; }

        [Test] public void TheDefaultSceneIsTheFormerNeutralLighting()
        {
            var d = PreviewSceneSettings.Default();
            Assert.That(Vector3.Distance(d.LightDirection, new Vector3(-0.3f, 0.65f, -0.7f).normalized), Is.LessThan(1e-5f));
            using (var p = new IsolatedModelPreview())
            {
                p.LoadDemoMesh(); Render(p);
                var m = p.RenderedMaterial(0);
                Assert.That(Vector3.Distance(m.GetVector("_PreviewLightDir"), new Vector3(-0.3f, 0.65f, -0.7f).normalized), Is.LessThan(1e-5f));
                Assert.That(Vector4.Distance(m.GetVector("_PreviewLight"), new Vector4(.65f, .65f, .65f, 1)), Is.LessThan(1e-5f), "0.65 × the lit amount, as the shader had it");
                Assert.That(Vector4.Distance(m.GetVector("_PreviewAmbient"), new Vector4(.35f, .35f, .35f, 1)), Is.LessThan(1e-5f), "plus 0.35, as the shader had it");
            }
        }

        [Test] public void LightPresetsIntensityAndBackgroundChangeBothViews()
        {
            using (var p = new IsolatedModelPreview())
            {
                p.LoadDemoMesh(); p.SetPaintTexture(White(), 0);
                var scene = PreviewSceneSettings.Default(); p.Scene = scene;
                var basis = Render(p);
                scene.Apply(PreviewLightPreset.Rim, p.CameraYaw, p.CameraPitch);
                var rim = Render(p);
                Assert.That(Mathf.Abs(Mean(rim) - Mean(basis)), Is.GreaterThan(4f), "a light from behind darkens the faces the camera sees");
                scene.Apply(PreviewLightPreset.Default, p.CameraYaw, p.CameraPitch); scene.intensity = 0;
                Assert.That(Mean(Render(p)), Is.LessThan(Mean(basis) - 10), "no direct light: only the ambient");
                scene.intensity = 1; scene.background = new Color(1, 0, 1, 1);
                var corner = Render(p).GetPixel(0, 95);
                Assert.That((corner.r, corner.g, corner.b), Is.EqualTo((1f, 0f, 1f)));
            }
            var material = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave }; made.Add(material);
            var go = new GameObject("Scene test model") { hideFlags = HideFlags.HideAndDontSave }; made.Add(go);
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx"); go.AddComponent<MeshRenderer>().sharedMaterial = material;
            using (var p = new IsolatedModelPreview())
            {
                p.Load(go); p.Shading = PreviewShading.Material; p.SetMaterialChannels(null);
                var scene = PreviewSceneSettings.Default(); p.Scene = scene;
                var lit = Render(p);
                scene.intensity = 0; scene.ambient = Color.black;
                Assert.That(Mean(Render(p)), Is.LessThan(Mean(lit) - 10), "the material view is lit by the same light");
            }
        }

        [Test] public void CameraPresetsFrameTheModelFromTheirSide()
        {
            using (var p = new IsolatedModelPreview())
            {
                p.LoadDemoMesh();
                foreach (PreviewCameraView view in System.Enum.GetValues(typeof(PreviewCameraView)))
                {
                    p.ViewFrom(view);
                    var (yaw, pitch) = PreviewSceneSettings.CameraAngles(view);
                    Assert.That((p.CameraYaw, p.CameraPitch), Is.EqualTo((yaw, pitch)), view.ToString());
                }
                p.ViewFrom(PreviewCameraView.Front);
                Assert.That(p.TryWorldToGui(new Rect(0, 0, 100, 100), new Vector3(0, 0, .5f), out var near), Is.True);
                Assert.That(p.TryWorldToGui(new Rect(0, 0, 100, 100), p.Bounds.center, out var center), Is.True);
                Assert.That(Vector2.Distance(center, new Vector2(50, 50)), Is.LessThan(1f), "framed on the model");
            }
            // 光のプリセットは今のカメラの向きから: 正面（yaw 0）のカメラで「左から」は −X から
            var s = PreviewSceneSettings.Default();
            s.Apply(PreviewLightPreset.Left, 0, 0); Assert.That(s.LightDirection.x, Is.LessThan(-.8f));
            s.Apply(PreviewLightPreset.Right, 0, 0); Assert.That(s.LightDirection.x, Is.GreaterThan(.8f));
            s.Apply(PreviewLightPreset.Rim, 0, 0); Assert.That(s.LightDirection.z, Is.GreaterThan(.8f), "behind the model, on the far side from a camera at −Z");
            s.Apply(PreviewLightPreset.View, 0, 0); Assert.That(s.LightDirection.z, Is.LessThan(-.6f));
            s.Apply(PreviewLightPreset.Above, 0, 0); Assert.That(s.LightDirection.y, Is.GreaterThan(.95f));
            var broken = new PreviewSceneSettings { lightYaw = float.NaN, lightPitch = 500, intensity = float.PositiveInfinity }.Normalized();
            Assert.That((broken.lightYaw, broken.lightPitch, broken.intensity), Is.EqualTo((PreviewSceneSettings.Default().lightYaw, 89f, 1f)));
        }

        [Test] public void TheSceneLeavesRenderSettingsAndSceneLightsAlone()
        {
            var ambient = RenderSettings.ambientLight; var mode = RenderSettings.ambientMode; int lights = Object.FindObjectsOfType<Light>().Length;
            using (var p = new IsolatedModelPreview())
            {
                p.LoadDemoMesh();
                p.Scene = new PreviewSceneSettings { intensity = 2, lightColor = Color.red, ambient = Color.green, background = Color.blue };
                Render(p);
            }
            Assert.That(RenderSettings.ambientLight, Is.EqualTo(ambient)); Assert.That(RenderSettings.ambientMode, Is.EqualTo(mode));
            Assert.That(Object.FindObjectsOfType<Light>().Length, Is.EqualTo(lights));
        }
    }
}
