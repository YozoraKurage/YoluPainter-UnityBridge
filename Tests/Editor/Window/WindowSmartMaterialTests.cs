using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>スマートマテリアルとスマートマスクを本物の入力（SendEvent）で置く: アセットのパネルの種類を切り替えて選び「置く」・ダブルクリック・
    /// 取り込むのアイコン、Ctrl+Z / Ctrl+Shift+Z、置いた後の Escape・フォーカスを失うことは何も取り消さない。パネルから層の一覧へのドラッグ
    /// （行の間・グループの中・グループの中の行の間）、スマートマスクは落とした行の層のマスクへ、キャンバスへのドラッグは選んだ層の上。ストロークの
    /// 間は受け付けない。</summary>
    public sealed partial class WindowTests
    {
        void OpenSmartAssets(TexturePaintWindow.AssetSource source, TexturePaintWindow.AssetKind kind)
        {
            OpenAssetsPanel();
            ClickAsset("source." + source);
            window.AssetPanelKind = kind; Repaint(window);
        }

        /// <summary>アセットのパネルから引いている（DragAndDrop に鍵を入れた）ものを、ホストの座標の点に落とす。</summary>
        DragAndDropVisualMode DropAsset(string key, Vector2 host, bool perform = true)
        {
            DragAndDrop.PrepareStartDrag(); DragAndDrop.SetGenericData("YoluPainter.Asset", key); DragAndDrop.objectReferences = new UnityEngine.Object[0];
            try
            {
                EditorShaderCompiler.TolerateErrorLogsIfBroken();
                window.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = host });
                var mode = DragAndDrop.visualMode;
                Repaint(window); // 落とす先の印を描く（例外なく）
                if (perform) window.SendEvent(new Event { type = EventType.DragPerform, mousePosition = host });
                return mode;
            }
            finally { DragAndDrop.SetGenericData("YoluPainter.Asset", null); Repaint(window); }
        }

        [Test] public void TheAssetsPanelPlacesSmartMaterialsAndMasksAndUndoes()
        {
            var d = window.Document; int layers = d.Layers.Count;
            OpenSmartAssets(TexturePaintWindow.AssetSource.BuiltIn, TexturePaintWindow.AssetKind.SmartMaterials);
            Assert.That(window.AssetScreenRects.Keys.Where(k => k.StartsWith("cell.", StringComparison.Ordinal)), Is.EquivalentTo(new[] { "cell.b:rusty-metal", "cell.b:dirty-paint", "cell.b:worn-edges" }), "only smart materials are listed");
            ClickAsset("cell.b:rusty-metal");
            ClickAsset("place");
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 4), window.StatusMessage);
            var group = d.GetLayer(window.SelectedLayer);
            Assert.That(group.IsGroup, Is.True); Assert.That(group.Name, Is.EqualTo("Rusty metal"));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(d.Layers.Count, Is.EqualTo(layers), "Ctrl+Z removes the placed layers");
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 4), "Ctrl+Shift+Z puts them back");
            // ダブルクリックでも置く（取り込まない）
            ClickAsset("cell.b:worn-edges", 2);
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 7), window.StatusMessage);
            Assert.That(window.ImageResources.Smart, Is.Empty, "placing does not import");
            // 置いた後の Escape・フォーカスを失うことは、何も取り消さない
            Key(window, KeyCode.Escape);
            window.GetType().GetMethod("OnLostFocus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(window, null);
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 7));
            // 取り込むのアイコン: このプロジェクトに入り、このプロジェクトの一覧に出る
            ClickAsset("cell.b:dirty-paint");
            ClickAsset("import");
            Assert.That(window.ImageResources.Smart.Select(s => s.Origin.BuiltInKey), Is.EqualTo(new[] { "dirty-paint" }), window.StatusMessage);
            ClickAsset("source." + TexturePaintWindow.AssetSource.Project);
            Assert.That(window.AssetScreenRects.ContainsKey("cell.p:" + window.ImageResources.Smart[0].Id.ToString("D")), Is.True);
            // スマートマスク: 選んだ層のマスクへ（ダブルクリック）
            var target = d.Layers[0]; window.SelectedLayer = target.Id;
            window.AssetPanelKind = TexturePaintWindow.AssetKind.SmartMasks;
            ClickAsset("source." + TexturePaintWindow.AssetSource.BuiltIn);
            int undo = d.UndoCount;
            ClickAsset("cell.b:edges", 2);
            Assert.That(d.GetLayer(target.Id).Mask, Is.Not.Null, window.StatusMessage); Assert.That(d.UndoCount, Is.EqualTo(undo + 1));
            Assert.That(window.EditMask, Is.True);
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(d.GetLayer(target.Id).Mask, Is.Null);
        }

        [Test] public void DroppingOnTheLayerListPlacesBetweenRowsInsideAGroupOrOnAMask()
        {
            foreach (var group in window.DockLayoutForTests.Column(DockPlace.Right)) if (!group.panels.Contains("layers")) group.collapsed = true; // 一覧を高く
            var d = window.Document; var a = d.Layers[0];
            var b = d.AddLayer("B"); var b2 = d.AddLayer("B2"); var g = d.GroupLayers(new[] { b.Id, b2.Id }, "G"); var c = d.AddLayer("C"); d.ClearHistory();
            window.SelectedLayer = c.Id; Repaint(window);
            string Order() => string.Join(",", d.Layers.Select(l => (l.ParentId == Guid.Empty ? "" : d.GetLayer(l.ParentId).Name + "/") + l.Name));
            Assert.That(Order(), Is.EqualTo("Layer 1,G/B,G/B2,G,C")); // 一覧では C, G, B2, B, Layer 1
            // C の行の上の端（一番上の線）: 一番上に
            Assert.That(DropAsset("b:worn-edges", LayerPanelPoint("row." + c.Id, .7f, .1f)), Is.EqualTo(DragAndDropVisualMode.Copy));
            Assert.That(Order(), Is.EqualTo("Layer 1,G/B,G/B2,G,C,Chipped paint edges/Paint,Chipped paint edges/Chipped edges,Chipped paint edges"), window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo();
            // グループの行の中ほど: その中の一番上
            DropAsset("b:worn-edges", LayerPanelPoint("row." + g.Id, .7f, .5f));
            var placed = d.GetLayer(window.SelectedLayer);
            Assert.That(placed.ParentId, Is.EqualTo(g.Id)); Assert.That(d.ChildrenOf(g.Id).Last(), Is.SameAs(placed));
            d.Undo();
            // グループの中の行（B）の上の端の線: グループの中の B と B2 の間
            DropAsset("b:worn-edges", LayerPanelPoint("row." + b.Id, .7f, .1f));
            placed = d.GetLayer(window.SelectedLayer);
            Assert.That(d.ChildrenOf(g.Id).Select(l => l.Name), Is.EqualTo(new[] { "B", placed.Name, "B2" }));
            Assert.That(window.LastSmartDropPlacement.ParentId, Is.EqualTo(g.Id));
            d.Undo();
            Assert.That(Order(), Is.EqualTo("Layer 1,G/B,G/B2,G,C"));
            // 一番下の線はスクロール領域の中に描かれ、一番下へ置ける。
            DropAsset("b:worn-edges",LayerPanelPoint("row."+a.Id,.7f,1f)+Vector2.up);
            placed=d.GetLayer(window.SelectedLayer);Assert.That(d.Layers.ToList().IndexOf(placed),Is.LessThan(d.Layers.ToList().IndexOf(a)));
            Assert.That(window.LastSmartDropPlacement.ParentId,Is.EqualTo(Guid.Empty));d.Undo();
            // スマートマスクは落とした行の層のマスクへ（選んでいる層でなくても）
            DropAsset("b:cavities", LayerPanelPoint("row." + a.Id, .7f, .5f));
            Assert.That(d.GetLayer(a.Id).Mask, Is.Not.Null, window.StatusMessage); Assert.That(window.LastSmartDropLayer, Is.EqualTo(a.Id));
            Assert.That(window.SelectedLayer, Is.EqualTo(a.Id)); Assert.That(d.UndoCount, Is.EqualTo(1));
            Assert.That(d.GetLayer(c.Id).Mask, Is.Null);
        }

        [Test] public void DroppingOnTheCanvasPlacesAboveTheSelectedLayerAndAStrokeRefusesDrops()
        {
            var d = window.Document; var bottom = d.Layers[0]; var top = d.AddLayer("top"); d.ClearHistory();
            window.SelectedLayer = bottom.Id;
            var canvas = At(window, 512, 512) + window.rootVisualElement.worldBound.position;
            DropAsset("b:dirty-paint", canvas);
            var placed = d.GetLayer(window.SelectedLayer);
            Assert.That(placed.Name, Is.EqualTo("Dirty paint"), window.StatusMessage);
            Assert.That(d.Layers.ToList().IndexOf(placed), Is.LessThan(d.Layers.ToList().IndexOf(top)), "above the selected layer, below the one above it");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            // ストロークの間は層の一覧も受け付けない
            window.SelectedLayer = top.Id; Repaint(window);
            var row = LayerPanelPoint("row." + top.Id, .7f, .1f);
            BeginLine(300, 300);
            Assert.That(DropAsset("b:rusty-metal", row), Is.Not.EqualTo(DragAndDropVisualMode.Copy), "the drop is not taken during a stroke");
            Key(window, KeyCode.Escape);
            Assert.That(d.UndoCount, Is.EqualTo(1), "nothing was placed during the stroke");
        }
    }
}
