using System.Collections.Generic;
using System.IO;
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
    /// 塗りつぶしの画像と投影の欄の見た目（batch-gl のオフスクリーンの描画）: 英語と日本語、窓の幅 980 と 1600 で、チャンネルの画像の欄（サムネイル・
    /// 名前・読み方・読み方の知らせ）と投影の欄（UV・トライプラナー・平面・球・円柱。型の上の投影は置き場の欄と 3D ビューのボックスのギズモ）が
    /// 文字を切らずに描けること、描いても文書が変わらないこと、型の上の投影では 3D ビューにギズモの軸の色が出ること、マップが無いときは理由の
    /// 知らせが出ること。描いた PNG は Logs/YoluPainterSnapshots/FillImage に残す（人が目で確かめる）。
    /// </summary>
    public sealed class FillImagePanelTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "FillImage"));
        [TearDown] public void BackToEnglish() => L.OverrideLanguage(PainterLanguage.English);

        [Test] public void EveryProjectionDrawsInBothLanguagesWithoutCutText()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh();
                w.MeshBakeSettings.Maps = new[] { MeshMapKind.Position, MeshMapKind.WorldNormal }; w.MeshBakeProgress = (t, i, p) => false;
                Assert.That(w.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), w.StatusMessage);
                var d = w.Document;
                var fill = d.AddFillLayer("Projected", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(220, 40, 40, 255) }, { PaintChannel.Roughness, new Rgba32(128, 128, 128, 255) } });
                d.ClearHistory(); w.SelectedLayer = fill.Id;
                w.SetFillImageFromAsset(fill.Id, PaintChannel.Color, "b:uv-checker");
                w.SetFillImageFromAsset(fill.Id, PaintChannel.Roughness, "b:value-noise");
                w.View = TexturePaintWindow.ViewMode.Split;
                w.SetSectionOpen("layer", true); w.SetSectionOpen("projection", true); // 塗りつぶしの層の文脈（層と投影）
                w.Preview.ViewFrom(35, 25);
                int drawn = 0;
                foreach (var mode in new[] { FillProjectionMode.Uv, FillProjectionMode.Triplanar, FillProjectionMode.Planar, FillProjectionMode.Spherical, FillProjectionMode.Cylindrical })
                {
                    w.SetProjectionMode(fill.Id, mode); w.PollGeneratorInputs();
                    foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Roughness })
                    {
                        if (mode != FillProjectionMode.Uv && mode != FillProjectionMode.Triplanar && channel == PaintChannel.Roughness) continue; // 欄の違いはチャンネルで 1 度見れば足りる
                        w.Channel = channel;
                        foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                        {
                            L.OverrideLanguage(language);
                            // 区画だけを欄の既定の幅（300）で描いた絵（見て確かめる用。同じ部品・同じ文字）
                            foreach (var (section, draw) in new (string, System.Func<Rect, float>)[] { ("fill", w.DrawFillSectionOnly), ("projection", w.DrawProjectionSectionOnly) })
                            {
                                string name = mode + "-" + channel + "-" + (language == PainterLanguage.English ? "en" : "ja") + "-" + section;
                                PaintGui.ShortenedTexts = 0; float used = 0;
                                OffscreenGui.RenderToPng(300, 640, () => used = draw(new Rect(0, 0, 300, 640)), Path.Combine(Folder, name + ".png"), PaintTheme.PanelBg);
                                Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": cut text");
                                Assert.That(used, Is.GreaterThan(40).And.LessThan(640), name + ": the section fits");
                            }
                            foreach (var (width, height) in new[] { (980, 900), (1600, 950) })
                            {
                                string name = mode + "-" + channel + "-" + (language == PainterLanguage.English ? "en" : "ja") + "-" + width;
                                long revision = d.Revision; int undo = d.UndoCount;
                                PaintGui.ShortenedTexts = 0;
                                string path = Path.Combine(Folder, name + ".png");
                                OffscreenGui.RenderWindow(w, width, height, path);
                                Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": a UI text did not fit and was shortened with …");
                                Assert.That((d.Revision, d.UndoCount), Is.EqualTo((revision, undo)), name + ": drawing changed the document");
                                Assert.That(w.LayerControlPanelRects.Keys, Has.Member("fill.image").And.Member("fill.image.colorspace").And.Member("projection.mode").And.Member("projection.tile.u").And.Member("projection.rotation"), name);
                                if (mode == FillProjectionMode.Triplanar) Assert.That(w.LayerControlPanelRects.Keys, Has.Member("projection.blend"), name);
                                if (mode != FillProjectionMode.Uv)
                                {
                                    Assert.That(w.LayerControlPanelRects.Keys, Has.Member("projection.edit").And.Member("projection.center.x").And.Member("projection.fit"), name);
                                    var image = new Texture2D(2, 2);
                                    try
                                    {
                                        Assert.That(image.LoadImage(File.ReadAllBytes(path)), Is.True);
                                        var r = w.SurfaceRect; int axis = 0;
                                        for (int y = (int)r.y; y < (int)r.yMax; y += 2)
                                            for (int x = (int)r.x; x < (int)r.xMax; x += 2)
                                            {
                                                var c = image.GetPixel(x, height - 1 - y);
                                                if (Near(c, ShapeGizmo.AxisX) || Near(c, ShapeGizmo.AxisY) || Near(c, ShapeGizmo.AxisZ)) axis++;
                                            }
                                        Assert.That(axis, Is.GreaterThan(20), name + ": the box's handles show in the axis colours");
                                    }
                                    finally { Object.DestroyImmediate(image); }
                                }
                                drawn++;
                            }
                        }
                    }
                }
                Assert.That(drawn, Is.EqualTo((2 + 2 + 1 + 1 + 1) * 2 * 2));
                // マップが無いとき: 理由の知らせ（切れない）
                w.SetProjectionMode(fill.Id, FillProjectionMode.Planar); w.Channel = PaintChannel.Color;
                w.CurrentTextureSet.MeshMaps.Clear(); w.PollGeneratorInputs(); // 焼いたマップを消す
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language); PaintGui.ShortenedTexts = 0;
                    OffscreenGui.RenderWindow(w, 980, 900, Path.Combine(Folder, "no-maps-" + (language == PainterLanguage.English ? "en" : "ja") + ".png"));
                    Assert.That(PaintGui.ShortenedTexts, Is.Zero, language + ": the reason does not fit");
                }
                Assert.That(d.GetFillImageStatus(fill.Id, PaintChannel.Color).Active, Is.False);
            }
            finally { Object.DestroyImmediate(w); }
        }
        static bool Near(Color c, Color target) => Mathf.Abs(c.r - target.r) < .08f && Mathf.Abs(c.g - target.g) < .08f && Mathf.Abs(c.b - target.b) < .08f;
    }
}
