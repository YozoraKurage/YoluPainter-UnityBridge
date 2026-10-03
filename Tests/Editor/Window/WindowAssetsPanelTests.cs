using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>アセットのパネルを本物のマウスの入力（SendEvent）で操作する: 出どころの切り替え、内蔵の画像を選んで取り込む（ボタンと
    /// ダブルクリック）、このプロジェクトのリソースを選んで置く、Ctrl+Z / Ctrl+Shift+Z、名前の検索、格子からキャンバスへのドラッグで置く、
    /// 取り込みの後で Escape・フォーカスを失ってもリソースは残る（ストロークではないので取り消すものが無い）。</summary>
    public sealed partial class WindowTests
    {
        Vector2 AssetControlPoint(string id)
        {
            Repaint(window); Repaint(window);
            Assert.That(window.AssetScreenRects.TryGetValue(id, out var screen), Is.True, id + " was not drawn (drawn: " + string.Join(", ", window.AssetScreenRects.Keys.Take(30)) + ")");
            var host = new Rect(screen.position - HostScreenPosition(window), screen.size);
            var bounds = new Rect(window.rootVisualElement.worldBound.position, window.position.size);
            Assert.That(bounds.Contains(host.center), Is.True, id + " is outside the window: " + host + " (window " + bounds + ")");
            return host.center;
        }
        void ClickAsset(string id, int clicks = 1)
        {
            var p = AssetControlPoint(id);
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = p, button = 0, clickCount = clicks });
            window.SendEvent(new Event { type = EventType.MouseUp, mousePosition = p, button = 0, clickCount = clicks });
            Repaint(window);
        }

        void OpenAssetsPanel()
        {
            var layout = window.DockLayoutForTests;
            foreach (var id in new[] { "layers", "properties", "material" }) layout.GroupOf(id).collapsed = true;
            layout.SetActive("assets");
            Repaint(window);
        }

        [Test] public void TheAssetsPanelImportsPlacesAndUndoes()
        {
            OpenAssetsPanel();
            ClickAsset("source." + TexturePaintWindow.AssetSource.BuiltIn);
            Assert.That(window.AssetPanelSource, Is.EqualTo(TexturePaintWindow.AssetSource.BuiltIn));
            ClickAsset("cell.b:uv-checker");
            Assert.That(window.SelectedAsset, Is.EqualTo("b:uv-checker"));
            ClickAsset("import");
            Assert.That(window.ImageResources.Count, Is.EqualTo(1), window.StatusMessage);
            var checker = window.ImageResources.Images.Single();
            Assert.That(checker.Origin.BuiltInKey, Is.EqualTo("uv-checker"));
            Assert.That(window.IsSaved, Is.False);
            // ダブルクリックでも取り込む
            ClickAsset("cell.b:grid", 2);
            Assert.That(window.ImageResources.Images.Select(r => r.Origin.BuiltInKey), Is.EqualTo(new[] { "uv-checker", "grid" }));
            // このプロジェクト: 選んで置く（1 回の Undo）
            ClickAsset("source." + TexturePaintWindow.AssetSource.Project);
            ClickAsset("cell.p:" + checker.Id.ToString("D"));
            var d = window.Document; int layers = d.Layers.Count;
            ClickAsset("place");
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 1), window.StatusMessage);
            var placed = d.GetLayer(window.SelectedLayer);
            Assert.That(placed.Name, Is.EqualTo(checker.Name));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(d.Layers.Count, Is.EqualTo(layers), "Ctrl+Z removes the placed layer");
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 1), "Ctrl+Shift+Z puts it back");
            // 名前で探す: 合わないものは出ない
            window.AssetPanelSearch = "grid"; Repaint(window); Repaint(window);
            Assert.That(window.AssetScreenRects.Keys.Where(k => k.StartsWith("cell.", StringComparison.Ordinal)), Is.EquivalentTo(new[] { "cell.p:" + window.ImageResources.Images[1].Id.ToString("D") }));
            window.AssetPanelSearch = "";
            // リソースの出し入れはストロークではないので、Escape やフォーカスを失っても残る
            Key(window, KeyCode.Escape);
            window.GetType().GetMethod("OnLostFocus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(window, null);
            Assert.That(window.ImageResources.Count, Is.EqualTo(2));
        }

        [Test] public void DraggingAnAssetOntoTheCanvasPlacesIt()
        {
            OpenAssetsPanel();
            ClickAsset("source." + TexturePaintWindow.AssetSource.BuiltIn);
            var d = window.Document; int layers = d.Layers.Count;
            // 格子からのドラッグ（Unity の DragAndDrop に入れたものを、キャンバスの上で落とす）
            var start = AssetControlPoint("cell.b:linear-gradient");
            window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = start, button = 0, clickCount = 1 });
            DragAndDrop.PrepareStartDrag(); DragAndDrop.SetGenericData("YoluPainter.Asset", "b:linear-gradient"); DragAndDrop.objectReferences = new UnityEngine.Object[0];
            var canvas = At(window, 512, 512) + window.rootVisualElement.worldBound.position;
            window.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = canvas });
            Assert.That(DragAndDrop.visualMode, Is.EqualTo(DragAndDropVisualMode.Copy));
            window.SendEvent(new Event { type = EventType.DragPerform, mousePosition = canvas });
            DragAndDrop.SetGenericData("YoluPainter.Asset", null);
            Assert.That(window.ImageResources.Images.Single().Origin.BuiltInKey, Is.EqualTo("linear-gradient"), window.StatusMessage);
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 1), "imported and placed");
            Assert.That(d.Undo(), Is.True); Assert.That(d.Layers.Count, Is.EqualTo(layers), "placing is one undo step; the resource stays");
            Assert.That(window.ImageResources.Count, Is.EqualTo(1));
        }
    }
}
