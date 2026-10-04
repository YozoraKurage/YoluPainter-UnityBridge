using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>各ビューの独立した表示: 実際に表示へ渡す画素、セット切替、窓の状態の保存復元、破棄、英日での上部中央配置。</summary>
    [Category("GPU")]
    public sealed class ViewShowTests
    {
        readonly List<TexturePaintWindow> made = new List<TexturePaintWindow>();
        readonly List<string> recoveries = new List<string>();
        TexturePaintWindow w;
        [SetUp] public void Open()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
            w = Make();
        }
        TexturePaintWindow Make()
        {
            var window = ScriptableObject.CreateInstance<TexturePaintWindow>(); made.Add(window); recoveries.Add(window.RecoveryRoot);
            window.LayoutOverride = new Rect(0, 0, 1400, 900);
            typeof(TexturePaintWindow).GetMethod("LayoutShell", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
            return window;
        }
        [TearDown] public void Close()
        {
            foreach (var window in made)
            {
                if (window == null) continue;
                Object.DestroyImmediate(window);
            }
            foreach (string recovery in recoveries)
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
            made.Clear(); recoveries.Clear(); L.OverrideLanguage(PainterLanguage.English);
        }

        [Test] public void DisplayedChannelsAndMasksKeepTheirPixelsAndLeavePaintingAlone()
        {
            var d = w.Document;
            var fill = d.AddFillLayer("Channels", new Dictionary<PaintChannel, Rgba32>
            {
                { PaintChannel.Color, new Rgba32(40, 60, 90) },
                { PaintChannel.Roughness, new Rgba32(200, 200, 200) },
                { PaintChannel.Normal, new Rgba32(128, 128, 255) }
            });
            w.Channel = PaintChannel.Color; w.SelectedLayer = fill.Id;
            var saved = DocumentBinary.Write(d);
            Assert.That(w.ShowChannelIn2D(PaintChannel.Roughness), Is.True);
            w.RefreshPreviewTextures();
            Assert.That(Pixels(w.CanvasDisplayTexture)[0], Is.EqualTo(200));
            Assert.That(w.Channel, Is.EqualTo(PaintChannel.Color));
            Assert.That(w.ModelShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.Material));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(saved), "view state does not edit or serialize into the document");
            int builds = w.MaterialChannelBuilds;
            w.RefreshPreviewTextures(); Assert.That(w.MaterialChannelBuilds, Is.EqualTo(builds), "unchanged channels reuse the existing cache");
            Assert.That(w.ShowChannelIn2D(PaintChannel.Normal), Is.True);
            w.RefreshPreviewTextures();
            var normal = Pixels(w.CanvasDisplayTexture);
            Assert.That(normal[0], Is.EqualTo(128).Within(1)); Assert.That(normal[2], Is.EqualTo(255).Within(1));
            w.ShowMaterialIn2D(); w.RefreshPreviewTextures();
            Assert.That(w.CanvasDisplayTexture, Is.SameAs(w.DisplayTexture));
            d.AddLayerMask(fill.Id); d.SetLayerMaskInverted(fill.Id, true);
            Assert.That(w.ShowMaskIn2D(), Is.True); w.RefreshPreviewTextures();
            Assert.That(Pixels(w.CanvasDisplayTexture)[0], Is.Zero);
            d.Undo(); w.RefreshPreviewTextures();
            Assert.That(Pixels(w.CanvasDisplayTexture)[0], Is.EqualTo(255), "the mask view follows document Undo");
            var copy = w.CanvasDisplayTexture;
            w.SelectedLayer = Guid.Empty; w.RefreshPreviewTextures();
            Assert.That(w.CanvasShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.Material), "a missing mask returns this view to its composite");
            Object.DestroyImmediate(w); Assert.That(copy == null, Is.True, "the mask display copy is destroyed with its owner");
        }

        [Test] public void EachViewOwnsItsMapCopyAcrossSetsAndWindowStateRestore()
        {
            w.Preview.LoadDemoMesh();
            PutMaps(w, .25, .75);
            Assert.That(w.ShowMeshMapIn3D(MeshMapKind.AmbientOcclusion), Is.True);
            Assert.That(w.ShowMeshMapIn2D(MeshMapKind.Curvature), Is.True);
            w.RefreshPreviewTextures();
            var canvas = w.CanvasDisplayTexture; var model = w.Preview.ShownUnlitTexture(0);
            Assert.That(canvas, Is.Not.SameAs(model));
            Assert.That(Pixels(canvas)[0], Is.EqualTo(191).Within(1));
            Assert.That(Pixels(model)[0], Is.EqualTo(64).Within(1));
            w.RefreshPreviewTextures();
            Assert.That(w.CanvasDisplayTexture, Is.SameAs(canvas)); Assert.That(Pixels(model)[0], Is.EqualTo(64).Within(1));
            string json = EditorJsonUtility.ToJson(w);
            var restored = Make(); EditorJsonUtility.FromJsonOverwrite(json, restored);
            restored.Preview.LoadDemoMesh(); PutMaps(restored, .25, .75); restored.RefreshPreviewTextures();
            Assert.That((restored.CanvasShow, restored.CanvasShowMap), Is.EqualTo((TexturePaintWindow.ModelShowKind.MeshMap, MeshMapKind.Curvature)));
            Assert.That((restored.ModelShow, restored.ModelShowMap), Is.EqualTo((TexturePaintWindow.ModelShowKind.MeshMap, MeshMapKind.AmbientOcclusion)));
            Assert.That(Pixels(restored.CanvasDisplayTexture)[0], Is.EqualTo(191).Within(1));
            var first = w.CurrentTextureSet;
            w.AddTextureSet(-1); PutMaps(w, .5, .1); w.RefreshPreviewTextures();
            Assert.That(Pixels(w.CanvasDisplayTexture)[0], Is.EqualTo(26).Within(1), "equal map revisions from another set do not reuse the first set's pixels");
            w.SwitchTextureSet(first.Id); w.RefreshPreviewTextures();
            Assert.That(Pixels(w.CanvasDisplayTexture)[0], Is.EqualTo(191).Within(1));
            Assert.That(w.ShowMeshMapIn2D(MeshMapKind.Thickness), Is.False, "an unbaked map cannot replace the selected display");
            Assert.That(w.CanvasShowMap, Is.EqualTo(MeshMapKind.Curvature));
            Object.DestroyImmediate(w); Assert.That(canvas == null, Is.True, "the separate map copy is destroyed with its owner");
        }

        static byte[] Pixels(Texture texture) => texture is Texture2D ? GpuTests.ReadCpu(texture) : GpuTests.Read(texture);

        static void PutMaps(TexturePaintWindow window, double ao, double curvature)
        {
            window.MeshMaps.Put(new[]
            {
                TestMeshMaps.Make(MeshMapKind.AmbientOcclusion, window.Document.Width, window.Document.Height, (x, y, c) => ao),
                TestMeshMaps.Make(MeshMapKind.Curvature, window.Document.Width, window.Document.Height, (x, y, c) => curvature)
            });
        }

        [Test] public void BothViewHeadersDrawNearTheirCentersInEnglishAndJapanese()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen pictures are checked in batch-gl.");
            w.Preview.LoadDemoMesh(); PutMaps(w, .25, .75);
            w.ShowChannelIn3D(PaintChannel.Roughness); w.ShowMeshMapIn2D(MeshMapKind.Curvature);
            string folder = Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "view-show"));
            foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
            {
                L.OverrideLanguage(language);
                w.ShowChannelIn3D(PaintChannel.Roughness); w.ShowMeshMapIn2D(MeshMapKind.Curvature);
                foreach (int width in new[] { 980, 1600 })
                    foreach (var view in new[] { TexturePaintWindow.ViewMode.Split, TexturePaintWindow.ViewMode.Canvas, TexturePaintWindow.ViewMode.Model })
                        foreach (bool swap in view == TexturePaintWindow.ViewMode.Split ? new[] { false, true } : new[] { false })
                        {
                            w.View = view; w.ViewsSwapped = swap;
                            OffscreenGui.RenderWindow(w, width, 800, Path.Combine(folder, language + "-" + width + "-" + view + "-" + swap + ".png"));
                            Check(w.canvasShowButtonForTests, w.CanvasRect, "2D"); Check(w.modelShowButtonForTests, w.SurfaceRect, "3D");
                            if (w.CanvasRect.width > 0)
                                Assert.That(Pixels(w.CanvasDisplayTexture)[0], Is.EqualTo(191).Within(1), "a 2D view restored after 3D-only shows its selected map in the first picture");
                            if (w.surfaceHeaderLabelForTests.width > 0)
                                Assert.That(w.surfaceHeaderLabelForTests.xMax, Is.LessThanOrEqualTo(w.modelShowButtonForTests.x), "the model label ends before the display dropdown");
                            string at = language + " " + width + " " + view + " " + swap;
                            CheckNothingIsCut(w.canvasShowButtonForTests, w.CanvasShowName(), w.canvasHeaderLabelForTests, w.canvasHeaderTextForTests, "2D " + at);
                            CheckNothingIsCut(w.modelShowButtonForTests, w.ModelShowName(), w.surfaceHeaderLabelForTests, w.surfaceHeaderTextForTests, "3D " + at);
                        }
            }
        }
        /// <summary>長いモデル名・テクスチャセット名のとき、見出しは名前を丸ごと隠さず、名前の部分だけを … で詰める（3D は「3D · 長い名前…」、2D は
        /// 「2D · 長い名前… · チャンネル  拡大率」）。1 つのビューだけを広く見せた画面では必ず詰めた名前が出て、分割した狭い画面でも描いた文字は
        /// 見出しの規則（ViewHeaderText）と同じで、収まる。英日で。</summary>
        [Test] public void LongModelAndTextureSetNamesAreCutInsideTheirOwnPlaceOnly()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen pictures are checked in batch-gl.");
            string longModel = "A very long model name that cannot possibly fit in the header of the 3D view", longSet = "A very long texture set name that cannot possibly fit in the header of the 2D view";
            var go = new GameObject(longModel) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                var material = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave };
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                w.CreateProject(new NewProjectSettings { Model = go, Resolution = 512 });
                w.AddTextureSet(-1); w.RenameTextureSet(w.CurrentTextureSet.Id, longSet);
                Assert.That(w.Model, Is.SameAs(go)); Assert.That(w.TextureSets.Count, Is.GreaterThan(1));
                string folder = Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "view-show")); int cut3D = 0, cut2D = 0;
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    foreach (int width in new[] { 980, 1600 })
                        foreach (var view in new[] { TexturePaintWindow.ViewMode.Model, TexturePaintWindow.ViewMode.Canvas, TexturePaintWindow.ViewMode.Split })
                        {
                            w.View = view; w.ViewsSwapped = false;
                            OffscreenGui.RenderWindow(w, width, 800, Path.Combine(folder, "long-names-" + language + "-" + width + "-" + view + ".png"));
                            string at = language + " " + width + " " + view;
                            Assume.That(PaintGui.TextWidth("Material", PaintTheme.Label), Is.GreaterThan(10), "no font to measure with");
                            string zoom = Mathf.RoundToInt(w.CanvasZoom * 100) + "%", tail = " · " + L.Tr(w.Channel.ToString()) + "  " + zoom;
                            if (w.SurfaceRect.width > 0)
                            {
                                var label = w.surfaceHeaderLabelForTests;
                                Assert.That(w.surfaceHeaderTextForTests, Is.EqualTo(TexturePaintWindow.ViewHeaderText(label.width, "3D · ", longModel, "", "3D")), at + ": the 3D header follows the header rule");
                                bool room = label.width >= PaintGui.TextWidth("3D · ", PaintTheme.LabelDim) + TexturePaintWindow.ViewHeaderUserNameMin + 4; // 名前を読める幅が残っている
                                if (room) { cut3D++; Assert.That(w.surfaceHeaderTextForTests, Does.StartWith("3D · A very").And.EndWith("…"), at + ": the long model name is cut, not dropped"); }
                                else Assert.That(w.surfaceHeaderTextForTests, Is.Null.Or.EqualTo("3D"), at + ": with no room for a readable name, the short form");
                            }
                            if (w.CanvasRect.width > 0)
                            {
                                var label = w.canvasHeaderLabelForTests;
                                Assert.That(w.canvasHeaderTextForTests, Is.EqualTo(TexturePaintWindow.ViewHeaderText(label.width, "2D · ", longSet, tail, "2D  " + zoom, "2D")), at + ": the 2D header follows the header rule");
                                bool room = label.width >= PaintGui.TextWidth("2D · ", PaintTheme.LabelDim) + PaintGui.TextWidth(tail, PaintTheme.LabelDim) + TexturePaintWindow.ViewHeaderUserNameMin + 4;
                                if (room) { cut2D++; Assert.That(w.canvasHeaderTextForTests, Does.StartWith("2D · A very").And.EndWith("…" + tail), at + ": the long set name is cut, the channel and zoom stay"); }
                                else Assert.That(w.canvasHeaderTextForTests, Is.Null.Or.EqualTo("2D  " + zoom).Or.EqualTo("2D"), at + ": with no room for a readable name, a short form");
                            }
                        }
                }
                Assert.That(cut3D, Is.GreaterThan(0), "a wide screen shows the long model name cut with an ellipsis");
                Assert.That(cut2D, Is.GreaterThan(0), "a wide screen shows the long texture set name cut with an ellipsis");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>狭い画面（分割して両側にドックがある）でも、表示の切り替えは名前つき・目のアイコン＋▾・出さない、のどれかで、中途半端な幅にならない。
        /// 出さないときも、2D・3D のどちらの「見せるもの」もメニュー（View ▸ 2D View Shows、Model ▸ 3D View Shows）から選べる。</summary>
        [Test] public void ANarrowViewDropsTheDropdownButTheMenusStillChooseWhatEachViewShows()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen pictures are checked in batch-gl.");
            w.Preview.LoadDemoMesh(); PutMaps(w, .25, .75);
            string folder = Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "view-show"));
            int hidden = 0, iconOnly = 0;
            foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
            {
                L.OverrideLanguage(language);
                foreach (int width in new[] { 420, 520, 640, 760, 980 })
                {
                    w.View = TexturePaintWindow.ViewMode.Split; w.ViewsSwapped = false;
                    OffscreenGui.RenderWindow(w, width, 800, Path.Combine(folder, "narrow-" + language + "-" + width + ".png"));
                    foreach (var (button, view, name) in new[] { (w.canvasShowButtonForTests, w.CanvasRect, w.CanvasShowName()), (w.modelShowButtonForTests, w.SurfaceRect, w.ModelShowName()) })
                    {
                        if (view.width <= 0) continue;
                        float whole = Mathf.Max(86, PaintGui.TextWidth(name + " ▾", PaintTheme.Label) + 20);
                        string at = language + " " + width + ": " + button;
                        Assert.That(button.width, Is.Zero.Or.EqualTo(40).Within(.01).Or.EqualTo(whole).Within(.01), at + ": whole, icon-only or not drawn");
                        if (button.width <= 0) hidden++; else if (button.width < whole) iconOnly++;
                    }
                }
            }
            Assert.That(hidden + iconOnly, Is.GreaterThan(0), "the sweep reaches widths where the dropdown loses its name");
            // 隠れても、どちらのビューも見せるものをメニューで選べる
            L.OverrideLanguage(PainterLanguage.English);
            var view3D = new PaintMenu(); typeof(TexturePaintWindow).GetMethod("ModelMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(w, new object[] { view3D });
            var view2D = new PaintMenu(); typeof(TexturePaintWindow).GetMethod("ViewMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(w, new object[] { view2D });
            foreach (var (menu, prefix, choices) in new[] { (view3D, "3D View Shows/", w.ModelShowChoices()), (view2D, "2D View Shows/", w.CanvasShowChoices()) })
            {
                var texts = menu.Entries.Where(e => !e.separator && !e.heading).Select(e => e.content.text).ToList();
                foreach (var choice in choices) Assert.That(texts.Any(t => t.StartsWith(prefix + choice.path)), Is.True, "the menu has " + prefix + choice.path);
            }
            var roughness = view2D.Entries.First(e => e.content.text.StartsWith("2D View Shows/Channel/Roughness") && e.enabled);
            roughness.Run();
            Assert.That((w.CanvasShow, w.CanvasShowChannel), Is.EqualTo((TexturePaintWindow.ModelShowKind.Channel, PaintChannel.Roughness)), "the View menu changes what the 2D view shows");
            Assert.That(w.ModelShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.Material), "and leaves the 3D view alone");
        }

        /// <summary>表示の切り替えは名前の全部が入る幅か、目のアイコン＋▾だけ（入らないほど狭ければ出さない）で、… で詰めない。見出しの文字は
        /// 全部が入るか、出さない（短い形か）。</summary>
        static void CheckNothingIsCut(Rect button, string name, Rect label, string drawn, string at)
        {
            Assume.That(PaintGui.TextWidth("Material", PaintTheme.Label), Is.GreaterThan(10), "no font to measure with");
            if (button.width > 0)
                Assert.That(button.width, Is.EqualTo(40).Within(.01).Or.GreaterThanOrEqualTo(PaintGui.TextWidth(name + " ▾", PaintTheme.Label) + 20 - .01), at + ": the display dropdown is whole or icon-only, never cut");
            if (!string.IsNullOrEmpty(drawn))
                Assert.That(PaintGui.TextWidth(drawn, PaintTheme.LabelDim), Is.LessThanOrEqualTo(label.width + .01), at + ": the header text '" + drawn + "' is whole or not drawn");
            Assert.That(drawn ?? "", Does.Not.Contain("…"), at);
        }

        static void Check(Rect button, Rect view, string name)
        {
            if (view.width <= 0) { Assert.That(button.width, Is.Zero, name + " is hidden"); return; }
            Assert.That(button.width, Is.GreaterThan(0), name);
            Assert.That(button.xMin, Is.GreaterThanOrEqualTo(view.xMin)); Assert.That(button.xMax, Is.LessThanOrEqualTo(view.xMax));
            if (view.width > 500) Assert.That(button.center.x, Is.EqualTo(view.center.x).Within(1), name + " at the center when there is room");
        }
    }
}
