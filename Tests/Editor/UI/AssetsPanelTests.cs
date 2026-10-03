using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// アセットのパネルを、窓を開かずに描く（batch-gl。PNG はテストプロジェクトの Logs/YoluPainterSnapshots/assets-panel に残す）: 4 つの出どころ
    /// （このプロジェクトの空・リソースあり（出どころが変わった知らせ・消えた印）・Unity のプロジェクト・自分の置き場の空とファイルあり・内蔵）を
    /// 英語と日本語で、既定のドックの幅では UI の文字を … で詰めず、最も狭いドックでも描けること。パネルを開いた窓全体も描く。欄は自前の部品だけで描く。
    /// </summary>
    public sealed class AssetsPanelTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "assets-panel"));
        static readonly int[] PanelWidths = { 299, 285 };
        const int NarrowestPanel = 220 - 1;
        static readonly Regex Standard = new Regex(@"\bEditorGUILayout\.|\bGUILayout\.|\bEditorGUI\.|\bEditorStyles\.", RegexOptions.Compiled);

        [TearDown] public void CleanUp() => L.OverrideLanguage(PainterLanguage.English);

        [Test] public void ThePanelUsesOnlyThePaintKit()
        {
            string path = Path.Combine(PackagePaths.Physical("Editor"), "Window/Panels/TexturePaintWindow.AssetsPanel.cs");
            var found = File.ReadAllLines(path).Select((l, i) => (code: l.Split(new[] { "//" }, StringSplitOptions.None)[0], line: i + 1)).Where(x => Standard.IsMatch(x.code)).Select(x => x.line).ToList();
            Assert.That(found, Is.Empty, "Unity's standard controls are used on lines " + string.Join(", ", found));
        }

        [Test] public void EverySourceDrawsInBothLanguagesWithoutShortenedText()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            string project = Path.Combine(Path.GetTempPath(), "yolupainter-assets-panel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(project); PainterSettings.ProjectRoot = project;
            string assets = "Assets/YoluPainterAssetsPanelTests-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(assets));
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            string recovery = w.RecoveryRoot;
            try
            {
                // Unity のプロジェクトのテクスチャ（検索で、この試験のものだけを出す）
                string texturePath = assets + "/PanelTestBricks.png";
                var rgba = new byte[64 * 64 * 4]; for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = (byte)(i / 256); rgba[i + 1] = 90; rgba[i + 2] = 60; rgba[i + 3] = 255; }
                File.WriteAllBytes(Path.GetFullPath(texturePath), RgbaPng.Encode(rgba, 64, 64));
                AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath); importer.isReadable = true; importer.SaveAndReimport();
                var states = new List<(string name, Action setup)>
                {
                    ("project-empty", () => { w.AssetPanelSource = TexturePaintWindow.AssetSource.Project; w.SelectedAsset = null; }),
                    ("builtin", () => { w.AssetPanelSource = TexturePaintWindow.AssetSource.BuiltIn; w.SelectedAsset = "b:uv-checker"; }),
                    ("project-resources", () =>
                    {
                        w.ImportBuiltInImage("uv-checker"); w.ImportBuiltInImage("value-noise");
                        var bricks = w.ImportUnityTexture(AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath));
                        string file = Path.Combine(project, "gone.png"); File.WriteAllBytes(file, RgbaPng.Encode(rgba, 64, 64));
                        var gone = w.ImportImageFile(file); w.RenameResource(gone.Id, "Logo (file removed)"); File.Delete(file);
                        rgba[0] = 255; File.WriteAllBytes(Path.GetFullPath(texturePath), RgbaPng.Encode(rgba, 64, 64)); AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
                        w.CheckResourceSources(); // Bricks は変わった・Logo は消えた
                        w.AssetPanelSource = TexturePaintWindow.AssetSource.Project; w.SelectedAsset = "p:" + bricks.Id.ToString("D");
                    }),
                    ("unity", () => { w.AssetPanelSource = TexturePaintWindow.AssetSource.Unity; w.AssetPanelSearch = "PanelTestBricks"; w.SelectedAsset = "u:" + AssetDatabase.AssetPathToGUID(texturePath); }),
                    ("library-empty", () => { w.AssetPanelSearch = ""; w.AssetPanelSource = TexturePaintWindow.AssetSource.Library; w.SelectedAsset = null; }),
                    ("library", () => { w.AddResourceToLibrary(w.ImageResources.Images[0].Id); w.AddResourceToLibrary(w.ImageResources.Images[1].Id); RefreshListings(w); w.AssetPanelSource = TexturePaintWindow.AssetSource.Library; }),
                    ("search-nothing", () => { w.AssetPanelSource = TexturePaintWindow.AssetSource.Project; w.AssetPanelSearch = "zzz"; }),
                };
                foreach (var (state, setup) in states)
                {
                    setup();
                    Draw(w, PanelWidths[0], 520, "warm-up"); // サムネイルは 1 回の描画で 3 つずつ作るので、先に作っておく
                    Draw(w, PanelWidths[0], 520, "warm-up");
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        string lang = language == PainterLanguage.English ? "en" : "ja";
                        foreach (int width in PanelWidths)
                        {
                            int before = PaintGui.ShortenedTexts;
                            Draw(w, width, 520, state + "-" + lang + "-" + width);
                            Assert.That(PaintGui.ShortenedTexts - before, Is.Zero, state + " " + lang + " " + width + ": a UI text did not fit and was shortened with …");
                        }
                        Draw(w, NarrowestPanel, 360, state + "-" + lang + "-narrowest");
                    }
                }
                // パネルを開いた窓全体
                w.AssetPanelSearch = ""; w.AssetPanelSource = TexturePaintWindow.AssetSource.Project;
                var layout = w.DockLayoutForTests; foreach (var id in new[] { "layers", "properties", "material" }) layout.GroupOf(id).collapsed = true;
                layout.SetActive("assets");
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    string path = Path.Combine(Folder, "window-assets-" + language + ".png");
                    OffscreenGui.RenderWindow(w, 1400, 900, path);
                    Assert.That(File.Exists(path), Is.True);
                }
                Assert.That(w.ImageResources.Count, Is.EqualTo(4), "drawing changes nothing");
            }
            finally
            {
                Object.DestroyImmediate(w);
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
                AssetDatabase.DeleteAsset(assets);
                PainterSettings.ProjectRoot = null;
                if (Directory.Exists(project)) Directory.Delete(project, true);
            }
        }

        static void RefreshListings(TexturePaintWindow w) => typeof(TexturePaintWindow).GetMethod("RefreshAssetPanel", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(w, null);

        static void Draw(TexturePaintWindow w, int width, int height, string name)
        {
            var draw = typeof(TexturePaintWindow).GetMethod("DrawAssetsPanel", BindingFlags.NonPublic | BindingFlags.Instance);
            string path = Path.Combine(Folder, name + ".png");
            OffscreenGui.RenderToPng(width, height, () => draw.Invoke(w, new object[] { new Rect(0, 0, width, height) }), path, PaintTheme.PanelBg);
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, path);
                Assert.That((texture.width, texture.height), Is.EqualTo((width, height)));
                Assert.That(texture.GetPixels32().Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Take(10).Count(), Is.GreaterThan(5), name + " looks empty");
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
