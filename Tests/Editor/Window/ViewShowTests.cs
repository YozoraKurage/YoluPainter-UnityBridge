using System;
using System.Collections.Generic;
using System.IO;
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
            w.AddTextureSet(1); PutMaps(w, .5, .1); w.RefreshPreviewTextures();
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
                        }
            }
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
