using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// プロパティの欄のフィルターの区画の Generator（batch-gl でオフスクリーンに描く）: 5 種類それぞれの設定と、読むマップの状態（使える・
    /// 無い・別のベイクにピン留め）を、英語でも日本語でも、最小（980×640）と大きい（1600×950）ウィンドウで例外なく描け、UI の文字が欄に
    /// 収まり（… で詰めた文字が 0）、部品が描かれていること。描いても文書は変わらない（触っていない値を書き戻さない）。描いた PNG は
    /// テストプロジェクトの Logs/YoluPainterSnapshots/Generators に残す（見て確かめる用）。
    /// </summary>
    public sealed class GeneratorPanelTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "Generators"));
        [TearDown] public void BackToEnglish() => L.OverrideLanguage(PainterLanguage.English);

        [Test] public void EveryGeneratorAndMapStateDrawsInBothLanguagesWithoutCutText()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh();
                w.MeshBakeSettings.AoSamples = 4; w.MeshBakeSettings.ThicknessSamples = 4; w.MeshBakeProgress = (t, i, p) => false;
                w.MeshBakeSettings.Maps = new[] { MeshMapKind.Curvature, MeshMapKind.Position, MeshMapKind.WorldNormal };
                Assert.That(w.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), w.StatusMessage);
                var d = w.Document;
                var fill = d.AddFillLayer("Worn", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(40, 120, 220, 255) } });
                d.AddLayerMask(fill.Id); d.ClearHistory();
                w.SelectedLayer = fill.Id;
                w.SetSectionOpen("layer", false); w.SetSectionOpen("mask", false); w.SetSectionOpen("filters", true);
                int drawn = 0;
                foreach (GeneratorType type in Enum.GetValues(typeof(GeneratorType)))
                {
                    var gen = w.AddGenerator(FilterTarget.Mask, type);
                    Assert.That(gen, Is.Not.Null, w.StatusMessage);
                    w.SelectedFilter = gen.Id;
                    var states = new List<string> { "maps" };
                    if (type == GeneratorType.EdgeWear) states.Add("pinned-other-bake");
                    foreach (var state in states)
                    {
                        if (state == "pinned-other-bake")
                            d.SetFilterSettings(fill.Id, gen.Id, gen.Settings.WithGenerator(gen.Settings.Generator.WithPin(MeshMapKind.Curvature, new string('a', 64))));
                        var status = d.GetGeneratorStatus(fill.Id, gen.Id);
                        Assert.That(status.Active, Is.EqualTo(state == "maps" && (type == GeneratorType.EdgeWear || type == GeneratorType.PositionGradient || type == GeneratorType.Direction || type == GeneratorType.ShapeGradient)), type + " " + state + ": " + status.Reason);
                        foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                        {
                            L.OverrideLanguage(language);
                            foreach (var (width, height) in new[] { (980, 640), (1600, 950) })
                            {
                                string name = type + "-" + state + "-" + (language == PainterLanguage.English ? "en" : "ja") + "-" + width;
                                long revision = d.Revision; int undo = d.UndoCount;
                                PaintGui.ShortenedTexts = 0;
                                Render(w, width, height, name);
                                Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": a UI text did not fit and was shortened with …");
                                Assert.That((d.Revision, d.UndoCount), Is.EqualTo((revision, undo)), name + ": drawing changed the document");
                                Assert.That(w.LayerControlPanelRects.Keys, Has.Member("generator.pin").And.Member("generator.low").And.Member("generator.blend").And.Member("generator.add.Mask"), name);
                                if (!status.Active) Assert.That(w.LayerControlPanelRects.Keys, Has.Member("generator.bake"), name + ": the way to the bake window");
                                drawn++;
                            }
                            // 区画だけを欄の既定の幅（300）で描いた絵（見て確かめる用。同じ部品・同じ文字）
                            {
                                string name = type + "-" + state + "-" + (language == PainterLanguage.English ? "en" : "ja") + "-section";
                                PaintGui.ShortenedTexts = 0; float used = 0;
                                OffscreenGui.RenderToPng(300, 900, () => used = w.DrawFilterSectionOnly(new Rect(0, 0, 300, 900)), Path.Combine(Folder, name + ".png"), PaintTheme.PanelBg);
                                Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": cut text");
                                Assert.That(used, Is.GreaterThan(200).And.LessThan(900), name + ": the section fits the picture");
                            }
                        }
                    }
                    d.RemoveFilter(fill.Id, gen.Id);
                }
                Assert.That(drawn, Is.EqualTo((Enum.GetValues(typeof(GeneratorType)).Length + 1) * 2 * 2));
            }
            finally { Object.DestroyImmediate(w); }
        }

        static void Render(TexturePaintWindow w, int width, int height, string name)
        {
            string path = Path.Combine(Folder, name + ".png");
            OffscreenGui.RenderWindow(w, width, height, path);
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, name);
                int colors = texture.GetPixels32().Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Take(200).Count();
                Assert.That(colors, Is.GreaterThan(50), name + ": the window looks empty");
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
