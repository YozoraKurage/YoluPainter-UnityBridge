using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// スマートマテリアルを表示せずに描いて確かめる（batch-gl。PNG はテストプロジェクトの Logs/YoluPainterSnapshots/smart に残す。見て確かめる用）:
    /// <list type="bullet">
    /// <item>デモのキューブにメッシュマップを焼き、内蔵のスマートマテリアル 3 つを置いた窓（3D ビュー）と、白い塗りつぶしに内蔵のスマートマスク 4 つを
    /// 置いた窓を描く。どの Generator も効いていて（理由が無い）、置く前の絵と違う。ピンを付けた Generator を保存し、別の条件で焼き直してから置くと、
    /// 今のベイクに付け直される。</item>
    /// <item>アセットのパネルのスマートマテリアル・スマートマスクの種類を、出どころごと（このプロジェクトの空と保存したもの・自分の置き場の空と
    /// ファイルあり（読めないファイルを含む）・内蔵・Unity のアセット）に英語と日本語で、既定のドックの幅では UI の文字を … で詰めず、最も狭い
    /// ドックでも描けること。</item>
    /// </list>
    /// </summary>
    public sealed class SmartMaterialOffscreenTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "smart"));
        static readonly int[] PanelWidths = { 299, 285 };
        const int NarrowestPanel = 220 - 1;
        [TearDown] public void BackToEnglish() => L.OverrideLanguage(PainterLanguage.English);

        static void RequireGraphics()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
        }

        static Texture2D Render(TexturePaintWindow w, string name, int width = 1400, int height = 900)
        {
            string path = Path.Combine(Folder, name + ".png");
            OffscreenGui.RenderWindow(w, width, height, path);
            var t = new Texture2D(2, 2); Assert.That(t.LoadImage(File.ReadAllBytes(path)), Is.True, name); return t;
        }
        /// <summary>3D ビューの中の画素の色（GUI の矩形、PNG は下から上の行）。</summary>
        static Color32[] Region(Texture2D t, Rect gui)
        {
            int x0 = Mathf.Clamp(Mathf.RoundToInt(gui.x), 0, t.width - 1), x1 = Mathf.Clamp(Mathf.RoundToInt(gui.xMax), 1, t.width);
            int y0 = Mathf.Clamp(t.height - Mathf.RoundToInt(gui.yMax), 0, t.height - 1), y1 = Mathf.Clamp(t.height - Mathf.RoundToInt(gui.y), 1, t.height);
            return t.GetPixels32().Where((c, i) => i % t.width >= x0 && i % t.width < x1 && i / t.width >= y0 && i / t.width < y1).ToArray();
        }

        [Test] public void BuiltInsOnTheDemoCubeHaveEveryGeneratorWorkingAndPinsFollowTheBake()
        {
            RequireGraphics();
            string project = Path.Combine(Path.GetTempPath(), "yolupainter-smart-cube-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(project); PainterSettings.ProjectRoot = project;
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            string recovery = w.RecoveryRoot;
            try
            {
                w.Preview.LoadDemoMesh(); w.View = TexturePaintWindow.ViewMode.Model;
                w.MeshBakeSettings.AoSamples = 16; w.MeshBakeSettings.ThicknessSamples = 4; w.MeshBakeProgress = (t, i, p) => false;
                w.MeshBakeSettings.Maps = new[] { MeshMapKind.Curvature, MeshMapKind.Position, MeshMapKind.AmbientOcclusion, MeshMapKind.WorldNormal };
                Assert.That(w.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), w.StatusMessage);
                var d = w.Document; var base0 = d.Layers[0]; w.SelectedLayer = base0.Id;
                var plain = Render(w, "cube-plain");
                var plainView = Region(plain, w.SurfaceRect);
                foreach (var entry in BuiltInSmartMaterials.All.Where(e => e.Kind == SmartKind.Material))
                {
                    w.SelectedLayer = base0.Id;
                    var r = w.PlaceSmartAsset("b:" + entry.Key);
                    Assert.That(r, Is.Not.Null, w.StatusMessage);
                    Assert.That(r.InactiveGenerators, Is.Empty, entry.Key + ": every generator reads the baked maps (" + string.Join(" ", r.InactiveGenerators) + ")");
                    var t = Render(w, "cube-" + entry.Key);
                    var view = Region(t, w.SurfaceRect);
                    Assert.That(view.Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Count(), Is.GreaterThan(30), entry.Key + ": the cube shows the material's pattern");
                    Assert.That(view.Zip(plainView, (a, b) => Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b)).Count(x => x > 30), Is.GreaterThan(view.Length / 20), entry.Key + ": it changed the cube");
                    Object.DestroyImmediate(t);
                    Assert.That(d.Undo(), Is.True);
                }
                // スマートマスク: 白い塗りつぶしの層に置く（黒い下地の上）
                d.Fill(base0.Id, PaintChannel.Color, new Rgba32(20, 20, 24, 255));
                var white = d.AddFillLayer("White", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(240, 240, 240, 255) } });
                foreach (var entry in BuiltInSmartMaterials.All.Where(e => e.Kind == SmartKind.Mask))
                {
                    var r = w.PlaceSmartAsset("b:" + entry.Key, null, white.Id);
                    Assert.That(r, Is.Not.Null, w.StatusMessage); Assert.That(r.InactiveGenerators, Is.Empty, entry.Key);
                    var t = Render(w, "cube-mask-" + entry.Key);
                    var view = Region(t, w.SurfaceRect);
                    int light = view.Count(c => c.r > 150), dark = view.Count(c => c.r < 90 && c.g < 90);
                    if (entry.Key == "cavities") Assert.That(light, Is.LessThan(view.Length / 200), "the cube has no cavities or occluded corners, so nothing shows");
                    else Assert.That(light, Is.GreaterThan(view.Length / 200), entry.Key + ": some of the cube shows the white layer");
                    Assert.That(dark, Is.GreaterThan(view.Length / 200), entry.Key + ": some of the cube hides it");
                    Object.DestroyImmediate(t);
                }
                // ピン: 今のベイクに固定した Generator（AO を読むくぼみ）を保存し、AO を別の条件で焼き直してから置くと、新しいベイクに付け直す
                Assert.That(w.PlaceSmartAsset("b:cavities", null, white.Id), Is.Not.Null, w.StatusMessage);
                var stage = white.Mask.Filters.Single();
                var status = d.GetGeneratorStatus(white.Id, stage.Id);
                var pinned = stage.Settings.Generator.WithoutPins();
                foreach (var use in status.Maps) pinned = pinned.WithPin(use.Kind, use.Map.Provenance.ConditionKey);
                d.SetFilterSettings(white.Id, stage.Id, stage.Settings.WithGenerator(pinned));
                w.SelectedLayer = white.Id;
                var saved = w.SaveSmartMaterial();
                Assert.That(saved.Material.Repin.Count, Is.EqualTo(1));
                w.MeshBakeSettings.AoSamples = 24; // AO の条件が変わる → 鍵が変わる
                Assert.That(w.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), w.StatusMessage);
                Assert.That(d.GetGeneratorStatus(white.Id, stage.Id).Active, Is.False, "the old pin no longer reads the new bake");
                var placed = w.PlaceSmartAsset("p:" + saved.Id.ToString("D"));
                Assert.That((placed.Pinned, placed.NotPinned), Is.EqualTo((1, 0)), w.StatusMessage);
                var copy = d.GetLayer(placed.LayerId).Mask.Filters.Single();
                Assert.That(d.GetGeneratorStatus(placed.LayerId, copy.Id).Active, Is.True, "pinned to the new bake");
                Assert.That(copy.Settings.Generator.Pins.Values, Is.Not.EquivalentTo(pinned.Pins.Values));
                Assert.That(w.StatusMessage, Does.Contain("pinned to this texture set's bake"));
                Object.DestroyImmediate(plain);
            }
            finally
            {
                Object.DestroyImmediate(w);
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
                PainterSettings.ProjectRoot = null;
                if (Directory.Exists(project)) Directory.Delete(project, true);
            }
        }

        [Test] public void EverySourceOfSmartMaterialsDrawsInBothLanguagesWithoutShortenedText()
        {
            RequireGraphics();
            string project = Path.Combine(Path.GetTempPath(), "yolupainter-smart-panel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(project); PainterSettings.ProjectRoot = project;
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            string recovery = w.RecoveryRoot;
            try
            {
                var d = w.Document;
                var states = new List<(string name, Action setup)>
                {
                    ("project-empty", () => { w.AssetPanelKind = TexturePaintWindow.AssetKind.SmartMaterials; w.AssetPanelSource = TexturePaintWindow.AssetSource.Project; w.SelectedAsset = null; }),
                    ("project-masks-empty", () => { w.AssetPanelKind = TexturePaintWindow.AssetKind.SmartMasks; }),
                    ("library-empty", () => { w.AssetPanelKind = TexturePaintWindow.AssetKind.SmartMaterials; w.AssetPanelSource = TexturePaintWindow.AssetSource.Library; }),
                    ("unity", () => { w.AssetPanelSource = TexturePaintWindow.AssetSource.Unity; }),
                    ("builtin-materials", () => { w.AssetPanelSource = TexturePaintWindow.AssetSource.BuiltIn; w.SelectedAsset = "b:rusty-metal"; }),
                    ("builtin-masks", () => { w.AssetPanelKind = TexturePaintWindow.AssetKind.SmartMasks; w.SelectedAsset = "b:edges"; }),
                    ("project", () =>
                    {
                        var fill = d.AddFillLayer("Weathered paint", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(60, 110, 70, 255) } });
                        d.AddLayerMask(fill.Id); d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Dirt)));
                        w.SelectedLayer = fill.Id; w.SaveSmartMaterial(); w.SaveSmartMask(); w.ImportSmartAsset("b:dirty-paint");
                        w.AssetPanelKind = TexturePaintWindow.AssetKind.SmartMaterials; w.AssetPanelSource = TexturePaintWindow.AssetSource.Project;
                        w.SelectedAsset = "p:" + w.ImageResources.Smart[0].Id.ToString("D");
                    }),
                    ("project-brushes", () => { w.SaveShelfBrush("Example brush"); w.AssetPanelKind = TexturePaintWindow.AssetKind.Brushes; w.AssetPanelSource = TexturePaintWindow.AssetSource.Project; w.SelectedAsset = "p:" + w.ImageResources.Brushes[0].Id; }),
                    ("project-materials", () => { var held = w.SaveShelfMaterial("Example material"); w.AssetPanelKind = TexturePaintWindow.AssetKind.Materials; w.SelectedAsset = "p:" + held.Id; }),
                    ("project-all-kinds", () => { w.ImportBuiltInImage("uv-checker"); w.AssetPanelKind = TexturePaintWindow.AssetKind.All; }),
                    ("library", () =>
                    {
                        File.WriteAllBytes(Path.Combine(PainterSettings.LibraryFolder, "Broken.ylsmart"), new byte[] { 1, 2, 3 });
                        RefreshListings(w);
                        w.AssetPanelKind = TexturePaintWindow.AssetKind.SmartMaterials; w.AssetPanelSource = TexturePaintWindow.AssetSource.Library; w.SelectedAsset = "l:Broken.ylsmart";
                    }),
                    ("search-nothing", () => { w.AssetPanelSource = TexturePaintWindow.AssetSource.BuiltIn; w.AssetPanelSearch = "zzz"; }),
                };
                foreach (var (state, setup) in states)
                {
                    setup();
                    var resourcesBefore=(w.ImageResources.Count,w.ImageResources.Revision);int undoBefore=d.UndoCount;
                    for (int i = 0; i < 4; i++) Draw(w, PanelWidths[0], 520, "warm-up"); // サムネイルは 1 回の描画で 3 つずつ作る
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        string lang = language == PainterLanguage.English ? "en" : "ja";
                        foreach (int width in PanelWidths)
                        {
                            int before = PaintGui.ShortenedTexts;
                            Draw(w, width, 520, "panel-" + state + "-" + lang + "-" + width);
                            Assert.That(PaintGui.ShortenedTexts - before, Is.Zero, state + " " + lang + " " + width + ": a UI text did not fit and was shortened with …");
                        }
                        Draw(w, NarrowestPanel, 360, "panel-" + state + "-" + lang + "-narrowest");
                    }
                    Assert.That((w.ImageResources.Count,w.ImageResources.Revision),Is.EqualTo(resourcesBefore),state+": drawing changes no resources");
                    Assert.That(d.UndoCount,Is.EqualTo(undoBefore),state+": drawing adds no history");
                }
                // パネルを開いた窓全体
                w.AssetPanelSearch = ""; w.AssetPanelSource = TexturePaintWindow.AssetSource.BuiltIn; w.AssetPanelKind = TexturePaintWindow.AssetKind.SmartMaterials;
                var layout = w.DockLayoutForTests; foreach (var id in new[] { "properties", "material" }) layout.GroupOf(id).collapsed = true;
                layout.SetActive("assets");
                var windowResourcesBefore=(w.ImageResources.Count,w.ImageResources.Revision);
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    Object.DestroyImmediate(Render(w, "window-smart-" + language));
                }
                Assert.That((w.ImageResources.Count,w.ImageResources.Revision),Is.EqualTo(windowResourcesBefore),"drawing changes nothing");
            }
            finally
            {
                Object.DestroyImmediate(w);
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
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
                Assert.That(texture.GetPixels32().Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Take(10).Count(), Is.GreaterThan(5), name + " looks empty");
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
