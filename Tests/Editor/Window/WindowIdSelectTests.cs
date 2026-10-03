using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ID マップの色で選ぶ（GUI モード、本物の窓に SendEvent）: Shift+W でツールになり、2D キャンバスのクリックと 3D ビューのクリックで、
    /// その所の ID の色の画素が選択範囲になる（Core の FromIdColors と同じ画素。UV アイランドの分け方どおり）、Shift で追加・Ctrl で削除、
    /// 1 回の Undo/Redo。ID マップが無い・古い・部品の無い所・モデルの外では何も選ばずに理由を出す。Generator「ID の色」のスポイト: 欄の
    /// ボタンで入り、クリックで色を足し（ID マップのベイクにピン留め）、Ctrl+クリックで外し、どれも 1 回の Undo、欄の × で外す、Esc・
    /// ツールの持ち替え・Generator が無くなると抜け、別のベイクにピン留めした Generator には足さない。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>デモのキューブを読み、ID マップだけを焼く（ドキュメントは既定の 1024²）。</summary>
        SurfaceGeometry IdCube(MeshIdSource source = MeshIdSource.UvIsland)
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            window.MeshBakeSettings.Maps = new[] { MeshMapKind.Id }; window.MeshBakeSettings.IdSource = source;
            window.MeshBakeProgress = (title, info, progress) => false;
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), window.StatusMessage);
            Repaint(window);
            return window.Preview.Geometry;
        }
        BakedMeshMap BakedIdMap() { Assert.That(window.MeshMaps.TryGet(MeshMapKind.Id, out var map), Is.True); return map; }
        void ClickWith(Vector2 at, EventModifiers modifiers)
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            foreach (var type in new[] { EventType.MouseDown, EventType.MouseUp })
                window.SendEvent(new Event { type = type, mousePosition = at + window.rootVisualElement.worldBound.position, button = 0, pressure = 1, modifiers = modifiers });
        }
        static byte[] MaskBytes(SelectionMask mask, int width, int height)
        {
            var bytes = new byte[width * height];
            if (mask != null) for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bytes[y * width + x] = mask[x, y];
            return bytes;
        }
        int IdColourAtPixel(Vector2Int p) { Assert.That(IdMapColors.TryGet(BakedIdMap(), p.x, p.y, out int rgb), Is.True, "no ID colour at " + p); return rgb; }
        /// <summary>今の選択範囲が、その色（許容の幅は窓の値）の FromIdColors と画素ごとに同じか。</summary>
        void AssertSelectionIs(params int[] colours)
        {
            var d = window.Document;
            Assert.That(MaskBytes(d.Selection, d.Width, d.Height), Is.EqualTo(MaskBytes(SelectionMask.FromIdColors(d, BakedIdMap(), colours, window.IdSelectTolerance), d.Width, d.Height)));
        }

        [Test] public void ShiftWChoosesTheToolAndClicksSelectThePartsColourOn2DAnd3D()
        {
            var g = IdCube(); var d = window.Document; var index = new SurfaceRegionIndex(g);
            Key(window, KeyCode.W, EventModifiers.Shift); Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.IdSelect));
            Key(window, KeyCode.W); Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.MagicWand), "W alone stays the magic wand");
            Key(window, KeyCode.W, EventModifiers.Shift); Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.IdSelect));
            var p0 = UvCentroidPixel(g, 0); int c0 = IdColourAtPixel(p0); int steps = d.UndoCount; long revision = d.Revision; var pixels = Snapshot();
            Click(At(window, p0.x, p0.y));
            Assert.That(window.StatusMessage, Does.Contain("Selected ID colour " + IdMapColors.Hex(c0)));
            AssertSelectionIs(c0);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "one undo"); Assert.That(Snapshot(), Is.EqualTo(pixels), "no pixel changes");
            // 選んだのは三角形 0 の UV アイランドだけ（ほかのアイランドの三角形の真ん中は選ばれない）
            for (int t = 0; t < g.TriangleCount; t++)
            {
                var p = UvCentroidPixel(g, t);
                Assert.That(d.Selection[p.x, p.y], Is.EqualTo(index.Key(t, SurfaceRegionKind.UvIsland) == index.Key(0, SurfaceRegionKind.UvIsland) ? 255 : 0), "triangle " + t);
            }
            Key(window, KeyCode.Z, EventModifiers.Control); Assert.That(d.Selection, Is.Null);
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift); AssertSelectionIs(c0);
            // Shift で足す・Ctrl で引く（ほかの選択ツールと同じ）
            int other = Enumerable.Range(0, g.TriangleCount).First(t => index.Key(t, SurfaceRegionKind.UvIsland) != index.Key(0, SurfaceRegionKind.UvIsland));
            var p1 = UvCentroidPixel(g, other); int c1 = IdColourAtPixel(p1); Assert.That(c1, Is.Not.EqualTo(c0));
            ClickWith(At(window, p1.x, p1.y), EventModifiers.Shift); AssertSelectionIs(c0, c1);
            ClickWith(At(window, p0.x, p0.y), EventModifiers.Control); AssertSelectionIs(c1);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 3));
            // 3D ビュー: 当たった面の UV のテクセルの色
            d.ClearSelection();
            var center = window.SurfaceRect.center;
            Assert.That(window.Preview.TryPick(window.SurfaceRect, center, out var hit), Is.True);
            Assert.That(IdMapColors.TryGetAtUv(BakedIdMap(), hit.UV.x, hit.UV.y, out int c3), Is.True);
            Click(center);
            AssertSelectionIs(c3);
            var hp = UvCentroidPixel(g, hit.TriangleIndex); Assert.That(d.Selection[hp.x, hp.y], Is.EqualTo(255), "the clicked face's island");
            Assert.That(d.Revision, Is.Not.EqualTo(revision));
        }

        [Test] public void WithoutAUsableIdMapOrAPartTheToolSaysWhyAndSelectsNothing()
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True); var d = window.Document;
            window.Tool = TexturePaintWindow.PaintTool.IdSelect; Repaint(window); int steps = d.UndoCount;
            Click(At(window, 200, 200));
            Assert.That(window.StatusMessage, Does.Contain("no usable ID map")); Assert.That(d.Selection, Is.Null); Assert.That(d.UndoCount, Is.EqualTo(steps));
            var g = IdCube(MeshIdSource.UvIsland); window.Tool = TexturePaintWindow.PaintTool.IdSelect; Repaint(window);
            // 部品の無い所（アイランドの間）とモデルの外
            Click(At(window, 341, 256)); Assert.That(window.StatusMessage, Does.Contain("No part there"));
            Click(window.SurfaceRect.min + new Vector2(4, 4)); Assert.That(window.StatusMessage, Does.Contain("Nothing of the model"));
            Assert.That(d.Selection, Is.Null); Assert.That(d.UndoCount, Is.EqualTo(steps));
            // 設定が変わって古くなった ID マップは使わない
            window.MeshBakeSettings.IdSource = MeshIdSource.MeshPart;
            var p0 = UvCentroidPixel(g, 0);
            Click(At(window, p0.x, p0.y)); Assert.That(window.StatusMessage, Does.Contain("stale")); Assert.That(d.Selection, Is.Null);
            window.MeshBakeSettings.IdSource = MeshIdSource.UvIsland;
            Click(At(window, p0.x, p0.y)); Assert.That(d.Selection, Is.Not.Null, "current again");
        }

        /// <summary>マスクに Generator「ID の色」を足した塗りつぶし層を選び、欄を開く。</summary>
        FilterEffect IdColorGenerator()
        {
            var d = window.Document;
            var fill = d.AddFillLayer("Parts", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(230, 40, 40, 255) } });
            d.AddLayerMask(fill.Id); d.ClearHistory(); window.SelectedLayer = fill.Id;
            var gen = window.AddGenerator(FilterTarget.Mask, GeneratorType.IdColor);
            Assert.That(gen, Is.Not.Null, window.StatusMessage);
            window.SelectedFilter = gen.Id; OpenLayerPanels();
            return gen;
        }
        IReadOnlyList<int> GeneratorColours(FilterEffect gen) => window.Document.FindFilter(window.SelectedLayer, gen.Id, out _).Settings.Generator.IdColors;

        [Test] public void TheGeneratorEyedropperAddsAndTakesOutColoursAndPinsTheBake()
        {
            var g = IdCube(); var d = window.Document; var index = new SurfaceRegionIndex(g);
            var gen = IdColorGenerator(); var layer = d.GetLayer(window.SelectedLayer);
            Assert.That(d.GetGeneratorStatus(layer.Id, gen.Id).Reason, Does.Contain("No ID colours"));
            int steps = d.UndoCount;
            ClickLayerControl("generator.id.pick"); Assert.That(window.IdColorPicking, Is.True);
            var p0 = UvCentroidPixel(g, 0); int c0 = IdColourAtPixel(p0);
            int other = Enumerable.Range(0, g.TriangleCount).First(t => index.Key(t, SurfaceRegionKind.UvIsland) != index.Key(0, SurfaceRegionKind.UvIsland));
            var p1 = UvCentroidPixel(g, other); int c1 = IdColourAtPixel(p1);
            Click(At(window, p0.x, p0.y));
            Assert.That(GeneratorColours(gen), Is.EqualTo(new[] { c0 })); Assert.That(window.StatusMessage, Does.Contain("Added " + IdMapColors.Hex(c0)));
            var settings = d.FindFilter(layer.Id, gen.Id, out _).Settings.Generator;
            Assert.That(settings.Pins[MeshMapKind.Id], Is.EqualTo(BakedIdMap().Provenance.ConditionKey), "the colours belong to this bake");
            Assert.That(window.IsStroking, Is.False); Assert.That(d.Selection, Is.Null, "picking does not select");
            Click(At(window, p1.x, p1.y)); Assert.That(GeneratorColours(gen), Is.EqualTo(new[] { c0, c1 }));
            Click(At(window, p1.x, p1.y)); Assert.That(window.StatusMessage, Does.Contain("already")); Assert.That(GeneratorColours(gen), Is.EqualTo(new[] { c0, c1 }));
            // マスクに効く: 足した 2 つのアイランドが見え、ほかは隠れる
            Assert.That(d.GetGeneratorStatus(layer.Id, gen.Id).Active, Is.True);
            int hidden = Enumerable.Range(0, g.TriangleCount).First(t => { var k = index.Key(t, SurfaceRegionKind.UvIsland); return k != index.Key(0, SurfaceRegionKind.UvIsland) && k != index.Key(other, SurfaceRegionKind.UvIsland); });
            Assert.That(layer.Mask.OutputHideAt(p0.x, p0.y), Is.Zero); Assert.That(layer.Mask.OutputHideAt(p1.x, p1.y), Is.Zero);
            var ph = UvCentroidPixel(g, hidden); Assert.That(layer.Mask.OutputHideAt(ph.x, ph.y), Is.EqualTo(255));
            // Ctrl+クリックで外す。どれも 1 回の Undo
            ClickWith(At(window, p0.x, p0.y), EventModifiers.Control); Assert.That(GeneratorColours(gen), Is.EqualTo(new[] { c1 }));
            Assert.That(d.UndoCount, Is.EqualTo(steps + 3));
            Key(window, KeyCode.Z, EventModifiers.Control); Assert.That(GeneratorColours(gen), Is.EqualTo(new[] { c0, c1 }));
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift); Assert.That(GeneratorColours(gen), Is.EqualTo(new[] { c1 }));
            // 3D ビューのクリックも取れる
            var center = window.SurfaceRect.center;
            Assert.That(window.Preview.TryPick(window.SurfaceRect, center, out var hit), Is.True);
            Assert.That(IdMapColors.TryGetAtUv(BakedIdMap(), hit.UV.x, hit.UV.y, out int c3), Is.True);
            Click(center);
            Assert.That(GeneratorColours(gen), Does.Contain(c3));
            // Esc で抜ける（以後のクリックは取らない）
            Key(window, KeyCode.Escape); Assert.That(window.IdColorPicking, Is.False); Assert.That(window.StatusMessage, Does.Contain("Stopped picking"));
            // 欄の × で外す（1 回の Undo）
            int before = GeneratorColours(gen).Count; steps = d.UndoCount;
            ClickLayerControl("generator.id.remove.0");
            Assert.That(GeneratorColours(gen).Count, Is.EqualTo(before - 1)); Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            // ツールを持ち替えると抜ける
            ClickLayerControl("generator.id.pick"); Assert.That(window.IdColorPicking, Is.True);
            Key(window, KeyCode.M); Assert.That(window.IdColorPicking, Is.False);
            // 欄のボタンをもう一度押すと抜ける
            ClickLayerControl("generator.id.pick"); ClickLayerControl("generator.id.pick"); Assert.That(window.IdColorPicking, Is.False);
        }

        [Test] public void TheEyedropperRefusesAnotherPinnedBakeAndStopsWhenTheGeneratorGoes()
        {
            var g = IdCube(); var d = window.Document;
            var gen = IdColorGenerator(); var layer = d.GetLayer(window.SelectedLayer);
            var pinned = gen.Settings.Generator.WithPin(MeshMapKind.Id, new string('a', 64));
            window.ApplyFilterSettings(gen.Id, gen.Settings.WithGenerator(pinned)); Repaint(window);
            ClickLayerControl("generator.id.pick");
            var p0 = UvCentroidPixel(g, 0); int steps = d.UndoCount;
            Click(At(window, p0.x, p0.y));
            Assert.That(window.StatusMessage, Does.Contain("another ID bake")); Assert.That(GeneratorColours(gen), Is.Empty); Assert.That(d.UndoCount, Is.EqualTo(steps));
            window.RemoveFilter(gen.Id); Repaint(window);
            Click(At(window, p0.x, p0.y));
            Assert.That(window.IdColorPicking, Is.False); Assert.That(window.StatusMessage, Does.Contain("no longer selected"));
            Assert.That(d.Layers.Contains(layer), Is.True);
        }
    }
}
