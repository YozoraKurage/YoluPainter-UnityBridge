using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// マテリアルの欄を、窓を開かずに描く（batch-gl。PNG はテストプロジェクトの Logs/YoluPainterSnapshots/material-panel に残す）: モデルが無い・
    /// Standard・lilToon（変えた値と注意あり）・メインのテクスチャだけのシェーダーを英語と日本語で、既定のドックの幅では
    /// 見出しの文字を … で詰めず、最も狭いドックでも描けること。複製の MaterialEditor とマテリアル表示の窓全体も描く。
    /// </summary>
    public sealed class MaterialPanelTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "material-panel"));
        /// <summary>既定のドック（300）から枠の 1 を引いた幅と、それより少し狭い幅。</summary>
        static readonly int[] PanelWidths = { 299, 285 };
        const int NarrowestPanel = 220 - 1;

        readonly List<Object> made = new List<Object>();

        [TearDown] public void CleanUp() { foreach (var o in made) if (o != null) Object.DestroyImmediate(o); made.Clear(); L.OverrideLanguage(PainterLanguage.English); }

        [Test] public void EveryStateDrawsInBothLanguagesWithoutShortenedText()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            string recovery = w.RecoveryRoot;
            try
            {
                foreach (var (state, setup) in States(w))
                {
                    setup();
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        string lang = language == PainterLanguage.English ? "en" : "ja";
                        foreach (int width in PanelWidths)
                        {
                            int before = PaintGui.ShortenedTexts;
                            Draw(w, width, 640, state + "-" + lang + "-" + width);
                            if (state == "standard" || state == "chosen-shader")
                                Assert.That(w.Preview.MaterialBinding(0).Summary, Does.Contain(L.Tr("Standard (built-in)")), "the cached binding follows the current UI language");
                            Assert.That(PaintGui.ShortenedTexts - before, Is.Zero, state + " " + lang + " " + width + ": a UI text did not fit and was shortened with …");
                        }
                        Draw(w, NarrowestPanel, 420, state + "-" + lang + "-narrowest");
                    }
                }
                // マテリアルの表示にした窓全体（3D ビューと欄）
                w.DockLayoutForTests.SetActive("material");
                w.Shading = PreviewShading.Material;
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    string path = Path.Combine(Folder, "window-material-" + language + ".png");
                    using (new OffscreenInspectorLogs()) OffscreenGui.RenderWindow(w, 1400, 900, path);
                    Assert.That(File.Exists(path), Is.True);
                }
                Assert.That(w.MaterialInspector.Editor.target, Is.SameAs(w.MaterialInspector.Copy));
                Assert.That(w.MaterialInspector.Problem, Is.Null);
            }
            finally
            {
                if (w != null) { w.MaterialEdits.RevertAll(); Object.DestroyImmediate(w); }
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
            }
        }

        /// <summary>擬似的なシーンの小さな窓の中身（モデルあり・なし、英語と日本語）。文字を詰めない。</summary>
        [Test] public void TheScenePopupDrawsInBothLanguagesWithoutShortenedText()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            string recovery = w.RecoveryRoot;
            try
            {
                foreach (string state in new[] { "empty", "model", "display" })
                {
                    if (state == "model") w.Preview.LoadDemoMesh();
                    // 右の列（環境・影・トーンマッピング。Model/TexturePaintWindow.Display3D.cs）を全部使った状態
                    if (state == "display") { w.SetEnvironment(PreviewEnvironmentSource.Sky); w.PreviewScene.shadows = true; w.SetToneMapping(PreviewToneMapping.Aces); w.PreviewScene.exposure = .5f; }
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        int before = PaintGui.ShortenedTexts;
                        string path = Path.Combine(Folder, "scene-" + state + "-" + language + ".png");
                        OffscreenGui.RenderToPng((int)TexturePaintWindow.ScenePanelWidth, (int)TexturePaintWindow.ScenePanelHeight,
                            () => w.DrawScenePanel(new Rect(0, 0, TexturePaintWindow.ScenePanelWidth, TexturePaintWindow.ScenePanelHeight)), path, PaintTheme.PanelBg);
                        Assert.That(PaintGui.ShortenedTexts - before, Is.Zero, language + ": a UI text did not fit");
                    }
                }
                // 設定はウィンドウの状態として残る（.ylp には入らない）
                w.ApplyLightPreset(PreviewLightPreset.Rim);
                Assert.That(UnityEditor.EditorJsonUtility.ToJson(w), Does.Contain("previewScene").And.Contain("lightYaw"));
                Assert.That(w.Preview.Scene, Is.SameAs(w.PreviewScene));
                w.ResetPreviewScene();
                Assert.That(w.PreviewScene.lightYaw, Is.EqualTo(PreviewSceneSettings.Default().lightYaw));
                Assert.That((w.PreviewScene.environment, w.PreviewScene.shadows, w.PreviewScene.toneMapping), Is.EqualTo((PreviewEnvironmentSource.None, false, PreviewToneMapping.None)), "Reset Scene resets the environment, the shadows and the tone mapping too");
            }
            finally
            {
                Object.DestroyImmediate(w);
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
            }
        }

        static void LoadModel(TexturePaintWindow w, GameObject model)
            => w.CreateProject(new NewProjectSettings { Model = model, Resolution = 512 });

        IEnumerable<(string, Action)> States(TexturePaintWindow w)
        {
            yield return ("no-model", () => { });
            yield return ("demo-cube", () => w.Preview.LoadDemoMesh());
            yield return ("standard", () => { LoadModel(w, Model(new Material(Shader.Find("Standard")))); Paint(w.Document, PaintChannel.Roughness); });
            yield return ("unlit", () => LoadModel(w, Model(new Material(Shader.Find("Unlit/Texture")))));
            if (Shader.Find("lilToon") != null)
                yield return ("liltoon", () =>
                {
                    var material = new Material(Shader.Find("lilToon"));
                    LoadModel(w, Model(material)); Paint(w.Document, PaintChannel.Normal); Paint(w.Document, PaintChannel.Emission);
                    w.Document.AddFillLayer("Base", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(200, 120, 80) } });
                    var info = w.MaterialProperties(material).First(p => p.Name == "_Cutoff");
                    w.SetMaterialValue(material, info, new Vector4(.25f, 0, 0, 0));
                    w.Shading = PreviewShading.Material; w.RefreshPreviewTextures();
                });
            // 見せるマテリアルをシェーダーから作り、チャンネルの流し込み先を開いて 1 つを手で指定（Model/TexturePaintWindow.PreviewMaterial.cs）
            yield return ("chosen-shader", () =>
            {
                LoadModel(w, Model(new Material(Shader.Find("Unlit/Texture")))); w.Document.AddLayer("Height"); Paint(w.Document, PaintChannel.Height);
                Assert.That(w.UsePreviewShader(Shader.Find("Standard")), Is.True, w.StatusMessage);
                Assert.That(w.SetChannelRoute(PaintChannel.Height, "_DetailMask", PreviewPacking.Value), Is.True, w.StatusMessage);
                w.MaterialRoutesOpen = true; w.Shading = PreviewShading.Material; w.RefreshPreviewTextures();
            });
        }

        GameObject Model(Material material)
        {
            material.hideFlags = HideFlags.HideAndDontSave; made.Add(material);
            var go = new GameObject("Material panel test model") { hideFlags = HideFlags.HideAndDontSave }; made.Add(go);
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        static void Paint(PaintDocument d, PaintChannel channel)
        {
            var layer = d.Layers[d.Layers.Count - 1];
            if (!layer.IsChannelEnabled(channel)) d.SetChannelEnabled(layer.Id, channel, true);
            layer.GetChannel(channel).SetPixel(3, 3, new Rgba32(180, 120, 200));
        }

        static void Draw(TexturePaintWindow w, int width, int height, string name)
        {
            var draw = typeof(TexturePaintWindow).GetMethod("DrawMaterialPanel", BindingFlags.NonPublic | BindingFlags.Instance);
            string path = Path.Combine(Folder, name + ".png");
            using (new OffscreenInspectorLogs()) OffscreenGui.RenderToPng(width, height, () => draw.Invoke(w, new object[] { new Rect(0, 0, width, height) }), path, PaintTheme.PanelBg);
            Assert.That(File.Exists(path), Is.True, name);
        }
    }
}
