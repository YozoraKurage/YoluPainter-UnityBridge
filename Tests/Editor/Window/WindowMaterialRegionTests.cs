using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>本物の窓への入力で、2D/3D バケツ・グラデーションとマテリアルの値、有効化、Undo/Redo を検証する。</summary>
    public sealed partial class WindowTests
    {
        void RegionMaterial()
        {
            var b = window.Brush;
            b.color = new Color(.8f, .1f, .2f, .8f); b.opacity = .75f;
            b.material = true; b.materialChannels = 63;
            b.materialRoughness = .25f; b.materialMetallic = .8f; b.materialHeight = .6f;
            b.materialNormalX = .3f; b.materialNormalY = -.2f; b.materialEmission = new Color(.2f, .9f, .4f, 1);
            window.Brush = b;
        }
        PaintDocument RegionClone()
        {
            var d = DocumentBinary.Read(DocumentBinary.Write(window.Document));
            d.RestoreSelection(window.Document.Selection); return d;
        }
        void RegionUndoRedo(byte[] before, byte[] after)
        {
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(before), "全チャンネルと有効化を一回で戻す");
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(after), "全チャンネルを一回でやり直す");
        }

        // IMGUI の座標を画素へ戻すと浮動小数の丸めが入るので、窓が受け取った端点で Core の参照を作る。
        GradientSettings RegionDraggedGradient(Rgba32 from, Rgba32 to)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(TexturePaintWindow);
            var a = (Vector2)type.GetField("toolStart", flags).GetValue(window);
            var b = (Vector2)type.GetField("toolCurrent", flags).GetValue(window);
            return new GradientSettings { X0 = a.x, Y0 = a.y, X1 = b.x, Y1 = b.y, From = from, To = to, Opacity = window.Brush.opacity };
        }

        [TestCase(false)] [TestCase(true)]
        public void MaterialBucketUsesThePickedChannelRegionForAllChannels(bool sampleAll)
        {
            RegionMaterial(); var d = window.Document; var l = d.GetLayer(window.SelectedLayer);
            // 今の Color の縦線が範囲を決める。他チャンネルは空でも同じ左側だけを塗る。
            for (int y = 0; y < d.Height; y++) l.GetChannel(PaintChannel.Color).SetPixel(300, y, new Rgba32(12, 23, 34));
            d.SetSelection(SelectionMask.Rectangle(d, 128, 128, 420, 400)); d.ClearHistory();
            window.WandTolerance = 0; window.WandSampleAll = sampleAll; window.Tool = TexturePaintWindow.PaintTool.Fill;
            var expected = RegionClone(); var channels = window.StrokeChannels();
            var region = SelectionMask.MagicWand(expected, sampleAll ? (Guid?)null : l.Id, PaintChannel.Color, 200, 200, 0, true);
            expected.FillMaterial(l.Id, channels, window.Brush.opacity, region);
            var before = DocumentBinary.Write(d); var at = At(window, 200, 200);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(window.StatusMessage, Does.Contain("Filled"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(DocumentBinary.Write(expected)));
            Assert.That(d.UndoCount, Is.EqualTo(1));
            foreach (var c in channels) Assert.That(l.GetPixel(c.Channel, 350, 200).A, Is.Zero, "線の右側: " + c.Channel);
            RegionUndoRedo(before, DocumentBinary.Write(d));
        }

        [TestCase(SurfaceRegionKind.Triangle)] [TestCase(SurfaceRegionKind.UvIsland)]
        [TestCase(SurfaceRegionKind.MeshPart)] [TestCase(SurfaceRegionKind.Material)]
        public void MaterialBucketClickIn3DUsesTheSamePickedRegion(SurfaceRegionKind kind)
        {
            RegionMaterial(); var d = window.Document;
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True); Repaint(window);
            var at = window.SurfaceRect.center;
            Assert.That(window.Preview.TryPick(window.SurfaceRect, at, out var hit), Is.True);
            int x = Mathf.FloorToInt(hit.UV.x * d.Width), y = Mathf.FloorToInt(hit.UV.y * d.Height);
            d.SetSelection(SelectionMask.Ellipse(d, x, y, 45, 40)); d.ClearHistory();
            window.SurfacePick = kind; window.Tool = TexturePaintWindow.PaintTool.Fill;
            var expected = RegionClone();
            var region = SurfaceRegions.Selection(expected, window.Preview.Geometry, SurfaceRegions.Region(window.Preview.Geometry, hit.TriangleIndex, kind));
            expected.FillMaterial(window.SelectedLayer, window.StrokeChannels(), window.Brush.opacity, region);
            var before = DocumentBinary.Write(d);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(window.StatusMessage, Does.Contain("Filled"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(DocumentBinary.Write(expected)), kind.ToString());
            Assert.That(d.UndoCount, Is.EqualTo(1)); RegionUndoRedo(before, DocumentBinary.Write(d));
        }

        [TestCase(false)] [TestCase(true)]
        public void GradientDragUsesMaterialValuesOrTheOriginalTwoColors(bool material)
        {
            RegionMaterial(); var b = window.Brush; b.material = material; window.Brush = b;
            var d = window.Document; d.SetSelection(SelectionMask.Rectangle(d, 100, 150, 400, 260)); d.ClearHistory();
            window.GradientTo = new Color(0, .3f, 1, 1); window.Tool = TexturePaintWindow.PaintTool.Gradient;
            var expected = RegionClone(); var to = (Color32)window.GradientTo;
            var before = DocumentBinary.Write(d); Drag(140, 200, 350, 200);
            var g = RegionDraggedGradient(window.StrokeChannels()[0].Value, new Rgba32(to.r, to.g, to.b, to.a));
            if (material) expected.GradientMaterial(window.SelectedLayer, window.StrokeChannels(), g);
            else expected.Gradient(window.SelectedLayer, PaintChannel.Color, g);
            Assert.That(window.StatusMessage, Does.Contain("Gradient"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(DocumentBinary.Write(expected)));
            Assert.That(d.UndoCount, Is.EqualTo(1)); RegionUndoRedo(before, DocumentBinary.Write(d));
        }

        [TestCase("bucket")] [TestCase("gradient")]
        public void MaterialRegionEraseRemovesAllChannels(string tool)
        {
            RegionMaterial(); var d = window.Document; var l = d.GetLayer(window.SelectedLayer);
            var region = SelectionMask.Rectangle(d, 100, 150, 400, 260);
            d.FillMaterial(l.Id, window.StrokeChannels(), region: region); d.SetSelection(region); d.ClearHistory();
            var b = window.Brush; b.erase = true; window.Brush = b;
            var expected = RegionClone(); var before = DocumentBinary.Write(d);
            if (tool == "bucket")
            {
                window.Tool = TexturePaintWindow.PaintTool.Fill; window.WandTolerance = 255;
                expected.FillMaterial(l.Id, window.StrokeChannels(), b.opacity, region, true);
                var at = At(window, 200, 200); Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            }
            else
            {
                window.Tool = TexturePaintWindow.PaintTool.Gradient;
                Drag(140, 200, 350, 200);
                expected.GradientMaterial(l.Id, window.StrokeChannels(), RegionDraggedGradient(Rgba32.Transparent, Rgba32.Transparent), erase: true);
            }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(DocumentBinary.Write(expected)));
            Assert.That(d.UndoCount, Is.EqualTo(1)); RegionUndoRedo(before, DocumentBinary.Write(d));
        }

        [TestCase("bucket")] [TestCase("gradient")]
        public void MaterialRegionToolsEditOnlyTheMaskUnderImageLocks(string tool)
        {
            RegionMaterial(); var d = window.Document; var l = d.GetLayer(window.SelectedLayer);
            d.AddLayerMask(l.Id); window.EditMask = true;
            d.SetLayerLocks(l.Id, LayerLocks.Pixels | LayerLocks.Transparency);
            d.SetSelection(SelectionMask.Rectangle(d, 100, 150, 400, 260)); d.ClearHistory();
            var before = DocumentBinary.Write(d); var expected = RegionClone();
            if (tool == "bucket")
            {
                window.Tool = TexturePaintWindow.PaintTool.Fill;
                var at = At(window, 200, 200); Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
                // 描画色のアルファ（古い形）は使う時に一度だけ不透明度へ掛かるので、期待は塗った後の不透明度で作る
                expected.FillMask(l.Id, window.Brush.opacity);
            }
            else
            {
                window.Tool = TexturePaintWindow.PaintTool.Gradient;
                Drag(140, 200, 350, 200);
                expected.GradientMask(l.Id, RegionDraggedGradient(new Rgba32(0, 0, 0), Rgba32.Transparent));
            }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(DocumentBinary.Write(expected)));
            foreach (var c in new[] { PaintChannel.Roughness, PaintChannel.Metallic, PaintChannel.Height, PaintChannel.Normal, PaintChannel.Emission })
                Assert.That(l.IsChannelEnabled(c), Is.False, c.ToString());
            Assert.That(d.UndoCount, Is.EqualTo(1)); RegionUndoRedo(before, DocumentBinary.Write(d));
        }

        [TestCase("bucket", "lock")] [TestCase("gradient", "lock")]
        [TestCase("bucket", "budget")] [TestCase("gradient", "budget")]
        public void MaterialRegionToolRefusalLeavesNoEnabledChannel(string tool, string reason)
        {
            RegionMaterial(); var d = window.Document; var l = d.GetLayer(window.SelectedLayer);
            d.SetSelection(SelectionMask.Rectangle(d, 100, 150, 400, 260));
            if (reason == "lock") d.SetLayerLocks(l.Id, LayerLocks.Pixels); else d.ActiveStrokeBudgetBytes = 64;
            d.ClearHistory(); var before = DocumentBinary.Write(d);
            if (tool == "bucket")
            {
                window.Tool = TexturePaintWindow.PaintTool.Fill; var at = At(window, 200, 200);
                Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            }
            else { window.Tool = TexturePaintWindow.PaintTool.Gradient; Drag(140, 200, 350, 200); }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.CanUndo, Is.False);
            Assert.That(window.StatusMessage, Does.Contain(reason == "lock" ? "locked" : "budget"));
        }

        [TestCase("escape")] [TestCase("focus")]
        public void CancellingAMaterialGradientDoesNotEnableOrPaintChannels(string how)
        {
            RegionMaterial(); window.Tool = TexturePaintWindow.PaintTool.Gradient;
            var d = window.Document; var before = DocumentBinary.Write(d);
            Mouse(window, EventType.MouseDown, At(window, 140, 200)); Mouse(window, EventType.MouseDrag, At(window, 350, 200));
            if (how == "escape") Key(window, KeyCode.Escape); else Invoke(window, "OnLostFocus");
            Mouse(window, EventType.MouseUp, At(window, 350, 200));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.CanUndo, Is.False);
        }

        [Test] public void The3DMaterialBucketEditsOnlyTheMask()
        {
            RegionMaterial(); var d = window.Document; var l = d.GetLayer(window.SelectedLayer);
            d.AddLayerMask(l.Id); window.EditMask = true;
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True); Repaint(window);
            var at = window.SurfaceRect.center; Assert.That(window.Preview.TryPick(window.SurfaceRect, at, out var hit), Is.True);
            int x = Mathf.FloorToInt(hit.UV.x * d.Width), y = Mathf.FloorToInt(hit.UV.y * d.Height);
            d.SetSelection(SelectionMask.Ellipse(d, x, y, 45, 40)); d.ClearHistory();
            window.Tool = TexturePaintWindow.PaintTool.Fill;
            var expected = RegionClone(); var before = DocumentBinary.Write(d);
            var region = SurfaceRegions.Selection(expected, window.Preview.Geometry, SurfaceRegions.Region(window.Preview.Geometry, hit.TriangleIndex, window.SurfacePick));
            expected.FillMask(l.Id, window.Brush.opacity, region);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(DocumentBinary.Write(expected)));
            Assert.That(d.UndoCount, Is.EqualTo(1)); RegionUndoRedo(before, DocumentBinary.Write(d));
        }

        [Test] public void MaterialSettingsAreAvailableInTheBucketAndGradientProperties()
        {
            window.Tool = TexturePaintWindow.PaintTool.Fill;
            ClickToolControl("material.toggle"); Assert.That(window.MaterialMode, Is.True);
            window.Tool = TexturePaintWindow.PaintTool.Gradient;
            ClickToolControl("material.chip.Roughness"); Assert.That(window.MaterialIncludes(PaintChannel.Roughness), Is.True);
        }
    }
}
