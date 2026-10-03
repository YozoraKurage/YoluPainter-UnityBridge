using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 3D ビューは変わったときだけ描く: 何も変えなければ何度頼んでも描かず前の絵を返し、絵に効く入力（大きさ・画素の倍率・カメラ・照明と背景・
    /// 渡すテクスチャ・描き方・照明の有無・マテリアル表示の中身・重ね表示・モデル・試験用の描画の後）のどれを変えても 1 回描く。描く回数の上限は
    /// 描かなかった変更を後で必ず描く。前の絵は描き直した絵と同じ画素。シェーダーで描くので batch-gl。
    /// </summary>
    [Category("GPU")]
    public sealed class PreviewRenderCacheTests
    {
        readonly List<Object> made = new List<Object>();
        static readonly Rect View = new Rect(0, 0, 160, 120);
        double now;

        [SetUp] public void RequireShader() { GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface"); now = 100; }
        [TearDown] public void CleanUp() { foreach (var o in made) if (o != null) Object.DestroyImmediate(o); made.Clear(); }

        IsolatedModelPreview Demo(int limit = 0)
        {
            var p = new IsolatedModelPreview { FrameRateLimit = limit };
            p.Clock = () => now;
            Assert.That(p.LoadDemoMesh().CanPaint, Is.True);
            return p;
        }
        Texture2D Solid(Color32 c) { var t = new Texture2D(4, 4, TextureFormat.RGBA32, false, true); t.SetPixels32(Enumerable.Repeat(c, 16).ToArray()); t.Apply(); made.Add(t); return t; }

        [Test] public void NothingChangedDrawsNothing()
        {
            using (var p = Demo())
            {
                var first = p.RenderCached(View, 1);
                Assert.That(p.RenderCount, Is.EqualTo(1));
                for (int i = 0; i < 20; i++) { now += 1; Assert.That(p.RenderCached(View, 1), Is.SameAs(first)); }
                Assert.That(p.RenderCount, Is.EqualTo(1), "20 more requests reuse the picture");
                Assert.That(p.WantsRepaint(), Is.False);
            }
        }

        [Test] public void EveryInputOfThePictureDrawsItOnceMore()
        {
            using (var p = Demo())
            {
                var scene = PreviewSceneSettings.Default(); p.Scene = scene;
                var rect = View; float ppp = 1;
                var g = GeneratorSettings.Default(GeneratorType.ShapeGradient).WithVolume(new ShapeVolume(GeneratorShape.Box, .5, 0, 0, 0, 0, 0, 1.02, 2, 2, 0));
                var changes = new List<(string, Action)>
                {
                    ("the size of the view", () => rect = new Rect(0, 0, 200, 120)),
                    ("the pixels per point", () => ppp = 2),
                    ("the camera", () => p.ViewFrom(60, 20)),
                    ("the light direction", () => scene.lightYaw += 10),
                    ("the light colour", () => scene.lightColor = Color.red),
                    ("the ambient", () => scene.ambient = Color.blue),
                    ("the background", () => scene.background = Color.green),
                    ("another scene object", () => p.Scene = scene = PreviewSceneSettings.Default()),
                    ("the painted texture", () => p.SetPaintTexture(Solid(new Color32(200, 10, 10, 255)), 0)),
                    ("the textures per slot", () => p.SetPaintTextures(new Dictionary<int, Texture> { { 0, Solid(new Color32(10, 200, 10, 255)) } })),
                    ("the normal map", () => p.SetNormalTexture(Solid(new Color32(128, 128, 255, 255)), 0)),
                    ("the normal maps per slot", () => p.SetNormalTextures(null)),
                    ("the lighting switch", () => p.LitPreview = false),
                    ("the shading", () => p.Shading = PreviewShading.Material),
                    ("the material view's contents", () => p.SetMaterialChannels(null)),
                    ("the Material panel's values", () => p.MaterialEdits = new PreviewMaterialEdits()),
                    ("the symmetry plane", () => p.ShownSymmetryPlane = p.SymmetryPlane(SymmetryAxis.X, 0)),
                    ("the symmetry plane's place", () => p.ShownSymmetryPlane = p.SymmetryPlane(SymmetryAxis.X, .2f)),
                    ("the shape gradient", () => p.ShownShapeGradient = ShapeGradientOverlay.Of(g, p.ModelRootPosition, p.ModelRootRotation, 0, Color.yellow)),
                    ("the polygon fill region", () => p.ShowRegion(7, new[] { 0, 1 }, Color.cyan)),
                    ("another region", () => p.ShowRegion(8, new[] { 2 }, Color.cyan)),
                    ("the region's colour", () => p.ShowRegion(8, new[] { 2 }, Color.magenta)),
                    ("hiding the region", () => p.HideRegion()),
                    ("hiding the overlays", () => { p.ShownSymmetryPlane = null; p.ShownShapeGradient = null; }),
                    ("a told change", () => p.InvalidateRender()),
                    ("the model", () => p.LoadDemoMesh()),
                    ("a test render into the same target", () => Object.DestroyImmediate(p.RenderStatic(64, 64))),
                };
                p.RenderCached(rect, ppp);
                foreach (var (name, change) in changes)
                {
                    int before = p.RenderCount; now += 1;
                    change();
                    p.RenderCached(rect, ppp);
                    Assert.That(p.RenderCount, Is.EqualTo(before + 1), name + " draws the 3D view again");
                    now += 1; p.RenderCached(rect, ppp);
                    Assert.That(p.RenderCount, Is.EqualTo(before + 1), name + ": and only once");
                }
            }
        }

        /// <summary>3D の表示の入力（環境・影・トーンマッピング・照明なしの見せ方・スロットごとに選んだマテリアルと流し込み先・環境のテクスチャ）も
        /// 1 回ずつ描かせ、同じ値を入れ直しても描かない。トーンマッピングを当てた絵も貼り直せる。</summary>
        [Test] public void TheDisplayInputsDrawItOnceMoreAndRepeatsDoNot()
        {
            GpuTests.RequireWorkingShader(PreviewEnvironment.ShaderName); GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewToneMap");
            using (var p = Demo())
            {
                var scene = PreviewSceneSettings.Default(); p.Scene = scene;
                var material = new Material(Shader.Find("Unlit/Texture")); made.Add(material);
                var routes = new List<PreviewChannelRoute>();
                var latLong = Solid(new Color32(90, 120, 200, 255));
                var unlit = new Dictionary<int, Texture> { { 0, Solid(new Color32(1, 2, 3, 255)) } };
                var changes = new List<(string, Action, Action)>
                {
                    ("the environment", () => scene.environment = PreviewEnvironmentSource.Sky, null),
                    ("its rotation", () => scene.environmentRotation = 40, null),
                    ("its brightness", () => scene.environmentIntensity = 2, null),
                    ("its background switch", () => scene.environmentBackground = !scene.environmentBackground, null),
                    ("the background blur", () => scene.environmentBlur = .8f, null),
                    ("the sky colour", () => scene.skyZenith = Color.red, null),
                    ("the shadows", () => scene.shadows = true, null),
                    ("their softness", () => scene.shadowSoftness = .9f, null),
                    ("the tone mapping", () => scene.toneMapping = PreviewToneMapping.Aces, null),
                    ("the exposure", () => scene.exposure = 1, null),
                    ("the environment texture", () => { scene.environment = PreviewEnvironmentSource.Texture; p.EnvironmentTexture = latLong; }, () => p.EnvironmentTexture = latLong),
                    ("the unlit view", () => p.SetUnlitTextures(unlit), null),
                    ("back to the shading", () => p.SetUnlitTextures(null), null),
                    ("a chosen material", () => p.SetMaterialChoice(0, material, routes), () => p.SetMaterialChoice(0, material, routes)),
                    ("a route added to the same list", () => { routes.Add(new PreviewChannelRoute(PaintChannel.Color, "", PreviewPacking.Color)); p.SetMaterialChoice(0, material, routes); }, () => p.SetMaterialChoice(0, material, routes)),
                    ("the own material again", () => p.SetMaterialChoice(0, null, null), () => p.SetMaterialChoice(0, null, null)),
                };
                p.RenderCached(View, 1);
                foreach (var (name, change, repeat) in changes)
                {
                    int before = p.RenderCount; now += 1;
                    change(); p.RenderCached(View, 1);
                    Assert.That(p.RenderCount, Is.EqualTo(before + 1), name + " draws the 3D view again");
                    now += 1; repeat?.Invoke(); var again = p.RenderCached(View, 1);
                    Assert.That(p.RenderCount, Is.EqualTo(before + 1), name + ": the same value again draws nothing");
                    Assert.That(again, Is.Not.Null);
                }
                Assert.That(p.ToneMapped, Is.True, "the reused picture above was the tone-mapped one");
            }
        }

        [Test] public void TheFrameRateLimitHoldsChangesBackAndDrawsThemLater()
        {
            using (var p = Demo(10))
            {
                var picture = p.RenderCached(View, 1); Assert.That(p.RenderCount, Is.EqualTo(1));
                now += .05; p.ViewFrom(80, 10);
                Assert.That(p.RenderCached(View, 1), Is.SameAs(picture)); Assert.That(p.RenderCount, Is.EqualTo(1), "within 1/10 s the change waits");
                Assert.That(p.RenderDeferred, Is.True); Assert.That(p.WantsRepaint(), Is.False, "not yet");
                now += .06;
                Assert.That(p.WantsRepaint(), Is.True, "the held change asks for a repaint when its time comes");
                p.RenderCached(View, 1);
                Assert.That(p.RenderCount, Is.EqualTo(2)); Assert.That(p.RenderDeferred, Is.False); Assert.That(p.WantsRepaint(), Is.False);
                // 大きさが変わったら待たない
                now += .01; p.RenderCached(new Rect(0, 0, 100, 100), 1);
                Assert.That(p.RenderCount, Is.EqualTo(3), "a new size is drawn at once");
                // 上限なし
                p.FrameRateLimit = 0; now += .001; p.ViewFrom(10, 10); p.RenderCached(new Rect(0, 0, 100, 100), 1);
                Assert.That(p.RenderCount, Is.EqualTo(4));
            }
        }

        [Test] public void TheReusedPictureIsTheDrawnPicture()
        {
            using (var p = Demo())
            {
                p.SetPaintTexture(Solid(new Color32(220, 120, 30, 255)), 0);
                var first = Read(p.RenderCached(View, 1));
                now += 1; var reused = Read(p.RenderCached(View, 1));
                Assert.That(reused, Is.EqualTo(first));
                p.InvalidateRender(); now += 1; var drawn = Read(p.RenderCached(View, 1));
                Assert.That(p.RenderCount, Is.EqualTo(2)); Assert.That(drawn, Is.EqualTo(first), "drawing again gives the same picture");
                Assert.That(p.CompilingShaders, Is.False, "the neutral view never waits for a shader compile");
            }
        }

        static byte[] Read(Texture texture)
        {
            var rt = (RenderTexture)texture; var previous = RenderTexture.active;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            try { RenderTexture.active = rt; t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false); return t.GetRawTextureData(); }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(t); }
        }
    }
}
