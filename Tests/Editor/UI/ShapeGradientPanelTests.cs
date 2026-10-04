using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 形のグラデーションの欄と 3D ビューのギズモを、batch-gl でオフスクリーンに描く: ボックス・球・平面と移動・回転のモードを、英語と日本語、
    /// 最小（980×640）と大きい（1600×950）ウィンドウで例外なく描け、UI の文字が欄に収まり（… で詰めた文字が 0）、欄の部品（形・編集・
    /// 中心の X・回転・大きさ・シーンから）が描かれ、3D ビューに値の色（橙）とギズモの軸の色が出ること。描いても文書は変わらない。
    /// 描いた PNG はテストプロジェクトの Logs/YoluPainterSnapshots/ShapeGradient に残す（見て確かめる用）。
    /// </summary>
    public sealed class ShapeGradientPanelTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "ShapeGradient"));
        [TearDown] public void BackToEnglish() => L.OverrideLanguage(PainterLanguage.English);

        [Test] public void EveryShapeAndModeDrawsInBothLanguagesWithTheGizmoAndTheOverlay()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh();
                w.MeshBakeSettings.Maps = new[] { MeshMapKind.Position }; w.MeshBakeProgress = (t, i, p) => false;
                Assert.That(w.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), w.StatusMessage);
                var d = w.Document;
                var fill = d.AddFillLayer("Graded", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(40, 120, 220, 255) } });
                d.AddLayerMask(fill.Id); d.ClearHistory(); w.SelectedLayer = fill.Id;
                var gen = w.AddGenerator(FilterTarget.Mask, GeneratorType.ShapeGradient);
                Assert.That(gen, Is.Not.Null, w.StatusMessage);
                w.SelectedFilter = gen.Id; w.View = TexturePaintWindow.ViewMode.Split;
                w.SetSectionOpen("effect", true);
                w.Preview.ViewFrom(35, 25);
                var start = gen.Settings.Generator.Volume;
                int drawn = 0;
                foreach (var shape in new[] { GeneratorShape.Box, GeneratorShape.Sphere, GeneratorShape.Plane })
                    foreach (var mode in new[] { ShapeGizmoMode.Move, ShapeGizmoMode.Rotate })
                    {
                        var current = d.FindFilter(fill.Id, gen.Id, out _);
                        var volume = start.WithShape(shape).WithRotation(0, shape == GeneratorShape.Plane ? 0 : 20, shape == GeneratorShape.Plane ? 25 : 0);
                        d.SetFilterSettings(fill.Id, gen.Id, current.Settings.WithGenerator(current.Settings.Generator.WithVolume(volume)));
                        w.ShapeGizmoMode = mode;
                        foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                        {
                            L.OverrideLanguage(language);
                            foreach (var (width, height) in new[] { (980, 640), (1600, 950) })
                            {
                                string name = shape + "-" + mode + "-" + (language == PainterLanguage.English ? "en" : "ja") + "-" + width;
                                long revision = d.Revision; int undo = d.UndoCount;
                                PaintGui.ShortenedTexts = 0;
                                string path = Path.Combine(Folder, name + ".png");
                                OffscreenGui.RenderWindow(w, width, height, path);
                                Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": a UI text did not fit and was shortened with …");
                                Assert.That((d.Revision, d.UndoCount), Is.EqualTo((revision, undo)), name + ": drawing changed the document");
                                Assert.That(w.LayerControlPanelRects.Keys, Has.Member("generator.shape").And.Member("generator.shape.edit").And.Member("generator.shape.center.x")
                                    .And.Member("generator.shape.rotation.z").And.Member("generator.shape.source").And.Member("generator.shape.recopy"), name);
                                Assert.That(w.Preview.ShapeOverlayDrawn, Is.True, name + ": the value is overlaid");
                                var image = new Texture2D(2, 2);
                                try
                                {
                                    Assert.That(image.LoadImage(File.ReadAllBytes(path)), Is.True);
                                    var r = w.SurfaceRect; int orange = 0, axis = 0;
                                    for (int y = (int)r.y; y < (int)r.yMax; y += 2)
                                        for (int x = (int)r.x; x < (int)r.xMax; x += 2)
                                        {
                                            var c = image.GetPixel(x, height - 1 - y);
                                            if (c.r > .55f && c.g > .25f && c.g < .7f && c.b < .35f) orange++;
                                            if (Near(c, ShapeGizmo.AxisX) || Near(c, ShapeGizmo.AxisY) || Near(c, ShapeGizmo.AxisZ)) axis++;
                                        }
                                    Assert.That(orange, Is.GreaterThan(30), name + ": the overlay or the outline shows in the 3D view");
                                    Assert.That(axis, Is.GreaterThan(20), name + ": the handles show in the axis colours");
                                }
                                finally { Object.DestroyImmediate(image); }
                                drawn++;
                            }
                        }
                    }
                Assert.That(drawn, Is.EqualTo(3 * 2 * 2 * 2));
            }
            finally { Object.DestroyImmediate(w); }
        }
        static bool Near(Color c, Color target) => Mathf.Abs(c.r - target.r) < .08f && Mathf.Abs(c.g - target.g) < .08f && Mathf.Abs(c.b - target.b) < .08f;
    }
}
