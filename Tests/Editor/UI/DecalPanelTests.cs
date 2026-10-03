using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// デカールの見た目（batch-gl のオフスクリーンの描画）: デモのキューブに置いたデカール（透明な縁の中の円のロゴ）が 3D ビューに出て、置き場の
    /// ギズモの軸の色が出ること、2D キャンバスに届く範囲が重なること、英語と日本語・窓の幅 980 と 1600 で投影の欄（デカールの間引き・置き場）と
    /// 塗りつぶしの欄が文字を切らずに描けること、描いても文書が変わらないこと、マップが無いときの理由とベイクのボタンも切れないこと。描いた PNG は
    /// Logs/YoluPainterSnapshots/Decal に残す（人が目で確かめる）。
    /// </summary>
    public sealed class DecalPanelTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "Decal"));
        [TearDown] public void BackToEnglish() => L.OverrideLanguage(PainterLanguage.English);

        /// <summary>透明な縁の中の、横長の楕円に星を抜いたロゴ（左が赤・右が黄、縁は 1 テクセルで滑らかに消える）。</summary>
        static ImageContent Logo()
        {
            const int w = 256, h = 128; var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4; double dx = (x + .5 - w / 2.0) / (w * .45), dy = (y + .5 - h / 2.0) / (h * .45);
                double edge = (1 - Math.Sqrt(dx * dx + dy * dy)) * h * .45; // 楕円の縁までのおよその距離（テクセル）
                double angle = Math.Atan2(dy, dx * 2), star = .32 + .14 * Math.Cos(5 * angle), hole = (star - Math.Sqrt(dx * dx * 4 + dy * dy) * .5) * h * .9;
                double alpha = Math.Max(0, Math.Min(1, edge + .5)) * (1 - Math.Max(0, Math.Min(1, hole + .5)));
                rgba[o] = 240; rgba[o + 1] = (byte)(40 + x * 200 / (w - 1)); rgba[o + 2] = 30; rgba[o + 3] = (byte)Math.Round(alpha * 255);
            }
            return ImageContent.FromPixels(rgba, w, h);
        }

        [Test] public void ADecalOnTheDemoCubeDrawsInBothLanguagesWithoutCutText()
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
                w.View = TexturePaintWindow.ViewMode.Split;
                w.SetSectionOpen("layer", true); w.SetSectionOpen("mask", false); w.SetSectionOpen("filters", false); w.SetSectionOpen("projection", true);
                w.Preview.ViewFrom(35, 25);
                Directory.CreateDirectory(Folder);
                OffscreenGui.RenderWindow(w, 1600, 950, Path.Combine(Folder, "before.png")); // 3D ビューの矩形を決める
                var logo = w.ImageResources.Add("Logo", Logo(), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
                var decal = d.GetLayer(w.PlaceDecal(logo.Id, w.SurfaceRect.center));
                d.SetFillValue(decal.Id, PaintChannel.Roughness, new Rgba32(30, 30, 30, 255));
                d.ClearHistory();
                Assert.That(d.GetDecalProblem(decal.Id), Is.Null);
                int drawn = 0;
                foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Roughness })
                {
                    w.Channel = channel;
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        string lang = language == PainterLanguage.English ? "en" : "ja";
                        foreach (var (section, draw) in new (string, System.Func<Rect, float>)[] { ("fill", w.DrawFillSectionOnly), ("projection", w.DrawProjectionSectionOnly) })
                        {
                            string name = channel + "-" + lang + "-" + section;
                            PaintGui.ShortenedTexts = 0; float used = 0;
                            OffscreenGui.RenderToPng(300, 760, () => used = draw(new Rect(0, 0, 300, 760)), Path.Combine(Folder, name + ".png"), PaintTheme.PanelBg);
                            Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": cut text");
                            Assert.That(used, Is.GreaterThan(40).And.LessThan(760), name + ": the section fits");
                        }
                        foreach (var (width, height) in new[] { (980, 900), (1600, 950) })
                        {
                            string name = channel + "-" + lang + "-" + width, path = Path.Combine(Folder, name + ".png");
                            long revision = d.Revision; int undo = d.UndoCount;
                            PaintGui.ShortenedTexts = 0;
                            OffscreenGui.RenderWindow(w, width, height, path);
                            Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": a UI text did not fit and was shortened with …");
                            Assert.That((d.Revision, d.UndoCount), Is.EqualTo((revision, undo)), name + ": drawing changed the document");
                            Assert.That(w.LayerControlPanelRects.Keys, Has.Member("decal.depth").And.Member("decal.angle").And.Member("decal.angle.edge").And.Member("projection.edit").And.Member("projection.center.x"), name);
                            if (channel == PaintChannel.Color)
                            {
                                var (axis, logoColour) = Count(path, height, w.SurfaceRect);
                                Assert.That(axis, Is.GreaterThan(20), name + ": the box's handles show in the axis colours");
                                Assert.That(logoColour, Is.GreaterThan(30), name + ": the logo shows on the cube");
                            }
                            drawn++;
                        }
                    }
                }
                Assert.That(drawn, Is.EqualTo(2 * 2 * 2));
                // 3D ビューだけを大きく（人が目で確かめる絵）
                L.OverrideLanguage(PainterLanguage.English); w.Channel = PaintChannel.Color;
                w.View = TexturePaintWindow.ViewMode.Model;
                OffscreenGui.RenderWindow(w, 1400, 900, Path.Combine(Folder, "cube-3d.png"));
                w.View = TexturePaintWindow.ViewMode.Canvas; w.MeshMapOverlay = TexturePaintWindow.MeshMapView.None;
                OffscreenGui.RenderWindow(w, 1400, 900, Path.Combine(Folder, "canvas-2d.png"));
                // マップが無いとき: 理由とベイクのボタン（切れない）
                w.View = TexturePaintWindow.ViewMode.Split;
                w.CurrentTextureSet.MeshMaps.Clear(); w.PollGeneratorInputs();
                Assert.That(d.GetDecalProblem(decal.Id), Is.Not.Null);
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language); PaintGui.ShortenedTexts = 0;
                    string lang = language == PainterLanguage.English ? "en" : "ja";
                    OffscreenGui.RenderWindow(w, 980, 900, Path.Combine(Folder, "no-maps-" + lang + ".png"));
                    Assert.That(PaintGui.ShortenedTexts, Is.Zero, language + ": the reason does not fit");
                    Assert.That(w.LayerControlPanelRects.Keys, Has.Member("decal.bake"), language + ": the bake button shows");
                    float used = 0;
                    OffscreenGui.RenderToPng(300, 760, () => used = w.DrawProjectionSectionOnly(new Rect(0, 0, 300, 760)), Path.Combine(Folder, "no-maps-" + lang + "-projection.png"), PaintTheme.PanelBg);
                    Assert.That(PaintGui.ShortenedTexts, Is.Zero, language + ": the projection section without maps");
                    Assert.That(used, Is.GreaterThan(40).And.LessThan(760));
                }
            }
            finally { Object.DestroyImmediate(w); }
        }

        /// <summary>3D ビューの中の、ギズモの軸の色の画素と、ロゴの色（赤みが強く青の少ない）の画素の数。</summary>
        static (int axis, int logo) Count(string path, int height, Rect r)
        {
            var image = new Texture2D(2, 2);
            try
            {
                Assert.That(image.LoadImage(File.ReadAllBytes(path)), Is.True);
                int axis = 0, logo = 0;
                for (int y = (int)r.y; y < (int)r.yMax; y += 2)
                    for (int x = (int)r.x; x < (int)r.xMax; x += 2)
                    {
                        var c = image.GetPixel(x, height - 1 - y);
                        if (Near(c, ShapeGizmo.AxisX) || Near(c, ShapeGizmo.AxisY) || Near(c, ShapeGizmo.AxisZ)) axis++;
                        else if (c.r > .45f && c.b < c.r * .45f && c.g < c.r * 1.05f) logo++;
                    }
                return (axis, logo);
            }
            finally { Object.DestroyImmediate(image); }
        }
        static bool Near(Color c, Color target) => Mathf.Abs(c.r - target.r) < .08f && Mathf.Abs(c.g - target.g) < .08f && Mathf.Abs(c.b - target.b) < .08f;
    }
}
