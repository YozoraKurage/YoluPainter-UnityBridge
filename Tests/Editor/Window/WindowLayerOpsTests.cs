using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウでのレイヤーの複製・結合とクリップボード: キー（Ctrl+J・Ctrl+E・Ctrl+Shift+E・Ctrl+C・Ctrl+Shift+C・Ctrl+X・
    /// Ctrl+V）とメニューから、選ぶ層・1 回の Undo・見た目が変わる結合の確かめ、文字の欄の入力中は奪わない、ストロークの最中は断る、
    /// テクスチャセットと窓をまたいだペースト（違う大きさは中央）。</summary>
    public sealed partial class WindowTests
    {
        static readonly EventModifiers Ctrl = EventModifiers.Control;

        /// <summary>今のレイヤーの Color のタイル (0,0) を 1 色にする（1 回の Undo）。</summary>
        void FillTile(PaintLayer layer, Rgba32 color, int size = 64)
        {
            var d = window.Document;
            d.Fill(layer.Id, PaintChannel.Color, color, 1, SelectionMask.Rectangle(d, 0, 0, size, size));
        }

        [Test] public void CtrlJDuplicatesTheSelectedLayerAndOneUndoRemovesTheCopy()
        {
            var d = window.Document; var layer = d.Layers.Last(); PaintDot(window, 200, 200);
            int count = d.Layers.Count, undo = d.UndoCount;
            Key(window, KeyCode.J, Ctrl);
            Assert.That(d.Layers.Count, Is.EqualTo(count + 1), window.StatusMessage);
            var copy = d.GetLayer(window.SelectedLayer);
            Assert.That(copy.Id, Is.Not.EqualTo(layer.Id)); Assert.That(copy.Name, Is.EqualTo(layer.Name + " copy"));
            Assert.That(d.Layers.Last(), Is.SameAs(copy), "directly above the original");
            Assert.That(copy.GetPixel(PaintChannel.Color, 200, 200), Is.EqualTo(layer.GetPixel(PaintChannel.Color, 200, 200)));
            Assert.That(d.UndoCount, Is.EqualTo(undo + 1));
            Key(window, KeyCode.Z, Ctrl);
            Assert.That(d.Layers.Count, Is.EqualTo(count));
        }

        [Test] public void CtrlEMergesAClippedLayerIntoItsBaseWithoutAsking()
        {
            var d = window.Document; var fake = UseFakeDialogs(window);
            var baseLayer = d.Layers.Last(); FillTile(baseLayer, new Rgba32(200, 40, 40, 255));
            var clipped = d.AddLayer("shade", above: baseLayer.Id); d.SetLayerClipping(clipped.Id, true); d.SetLayerBlendMode(clipped.Id, LayerBlendMode.Multiply);
            d.Fill(clipped.Id, PaintChannel.Color, new Rgba32(90, 90, 200, 180), 1, SelectionMask.Rectangle(d, 30, 30, 100, 100));
            window.SelectedLayer = clipped.Id; var before = Snapshot(); int count = d.Layers.Count, undo = d.UndoCount;
            Key(window, KeyCode.E, Ctrl);
            Assert.That(d.Layers.Count, Is.EqualTo(count - 1), window.StatusMessage);
            Assert.That(fake.Asked, Is.Empty, "an exact merge does not ask");
            Assert.That(Snapshot(), Is.EqualTo(before), "the look is the same");
            Assert.That(window.StatusMessage, Does.Contain("looks the same"));
            Assert.That(d.GetLayer(window.SelectedLayer).Name, Is.EqualTo(baseLayer.Name), "the result is selected and keeps the base's name");
            Assert.That(d.UndoCount, Is.EqualTo(undo + 1));
        }

        [Test] public void CtrlEAsksBeforeAMergeChangesTheLookAndChangesNothingWhenDeclined()
        {
            var d = window.Document; var fake = UseFakeDialogs(window);
            FillTile(d.Layers.Last(), new Rgba32(200, 200, 200, 255));
            var shade = d.AddLayer("shade"); FillTile(shade, new Rgba32(100, 100, 255, 255)); d.SetLayerBlendMode(shade.Id, LayerBlendMode.Multiply);
            var paint = d.AddLayer("paint"); FillTile(paint, new Rgba32(255, 0, 0, 255), 16);
            window.SelectedLayer = paint.Id; var before = Snapshot(); int count = d.Layers.Count, undo = d.UndoCount;

            fake.ConfirmAnswer = false;
            Key(window, KeyCode.E, Ctrl);
            Assert.That(fake.Asked, Is.EqualTo(new[] { "Confirm: Merge Layers" }));
            Assert.That((d.Layers.Count, d.UndoCount), Is.EqualTo((count, undo)), "declined: nothing changed");
            Assert.That(Snapshot(), Is.EqualTo(before)); Assert.That(window.StatusMessage, Does.Contain("Not merged"));

            fake.ConfirmAnswer = true;
            Key(window, KeyCode.E, Ctrl);
            Assert.That(d.Layers.Count, Is.EqualTo(count - 1));
            Assert.That(window.StatusMessage, Does.Contain("256 pixels changed"), "16 × 16 red pixels are now multiplied");
            Assert.That(d.GetLayer(window.SelectedLayer).BlendMode, Is.EqualTo(LayerBlendMode.Multiply), "Photoshop's merge down keeps the lower layer's mode");
            Key(window, KeyCode.Z, Ctrl);
            Assert.That(Snapshot(), Is.EqualTo(before), "one undo");
        }

        [Test] public void CtrlEOnAGroupMergesTheGroupAndCtrlShiftEMergesWhatShows()
        {
            var d = window.Document; UseFakeDialogs(window);
            var first = d.Layers.Last(); FillTile(first, new Rgba32(10, 120, 200, 255));
            var second = d.AddLayer("second"); FillTile(second, new Rgba32(250, 250, 0, 128), 32);
            var group = d.GroupLayers(new[] { second.Id }, "group"); d.SetLayerBlendMode(group.Id, LayerBlendMode.Normal);
            var hidden = d.AddLayer("hidden"); FillTile(hidden, new Rgba32(0, 255, 0, 255)); d.SetLayerVisibility(hidden.Id, false);
            var before = Snapshot();
            window.SelectedLayer = group.Id;
            Key(window, KeyCode.E, Ctrl);
            var merged = d.GetLayer(window.SelectedLayer);
            Assert.That((merged.Name, merged.IsGroup), Is.EqualTo(("group", false)), window.StatusMessage);
            Assert.That(Snapshot(), Is.EqualTo(before));

            Key(window, KeyCode.E, Ctrl | EventModifiers.Shift);
            Assert.That(d.Layers.Select(l => l.Name), Is.EqualTo(new[] { "group", "hidden" }), "the hidden layer stays; the merged layer takes the selected layer's name");
            Assert.That(Snapshot(), Is.EqualTo(before), window.StatusMessage);
            Key(window, KeyCode.Z, Ctrl);
            Assert.That(d.Layers.Count, Is.EqualTo(3));
        }

        [Test] public void CopyCutAndPasteWithKeysGoThroughTheClipboard()
        {
            var d = window.Document; PainterClipboard.Content = null;
            var layer = d.Layers.Last(); FillTile(layer, new Rgba32(30, 60, 90, 255));
            d.SetSelection(SelectionMask.Rectangle(d, 10, 10, 30, 20));
            Key(window, KeyCode.C, Ctrl);
            var copied = PainterClipboard.Content;
            Assert.That(copied, Is.Not.Null, window.StatusMessage);
            Assert.That((copied.X, copied.Y, copied.Width, copied.Height), Is.EqualTo((10, 10, 20, 10)));
            int count = d.Layers.Count;
            Key(window, KeyCode.V, Ctrl);
            Assert.That(d.Layers.Count, Is.EqualTo(count + 1), window.StatusMessage);
            var pasted = d.GetLayer(window.SelectedLayer);
            Assert.That(pasted.GetPixel(PaintChannel.Color, 15, 15), Is.EqualTo(new Rgba32(30, 60, 90, 255)));
            Assert.That(pasted.GetPixel(PaintChannel.Color, 40, 40), Is.EqualTo(Rgba32.Transparent));
            Assert.That(d.Selection, Is.Null, "paste deselects, so V and a drag moves the pasted layer");

            window.SelectedLayer = layer.Id; d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 8, 8)); int undo = d.UndoCount;
            Key(window, KeyCode.X, Ctrl);
            Assert.That(layer.GetPixel(PaintChannel.Color, 3, 3), Is.EqualTo(Rgba32.Transparent), window.StatusMessage);
            Assert.That(PainterClipboard.Content.Width, Is.EqualTo(8));
            Assert.That(d.UndoCount, Is.EqualTo(undo + 1), "cut is one undo step");

            d.ClearSelection();
            Key(window, KeyCode.C, Ctrl | EventModifiers.Shift);
            Assert.That(PainterClipboard.Content.Source, Is.EqualTo(ClipboardSource.Composite), window.StatusMessage);
            PainterClipboard.Content = null;
        }

        [Test] public void KeysAreLeftToATextFieldAndRefusedDuringAStroke()
        {
            var d = window.Document; PainterClipboard.Content = null; PaintDot(window, 100, 100);
            int count = d.Layers.Count; string name = d.Layers.Last().Name;
            // レイヤーのパネルの一番上の行の名前をダブルクリックして名前の欄を出し、押して文字の入力にする
            Repaint(window);
            var head = window.PanelHeaderRectForTests("layers");
            Assert.That(head.width, Is.GreaterThan(0), "the layers panel is docked");
            var at = new Vector2(head.x + head.width * .55f, head.yMax + TexturePaintWindow.LayerListOffsetFor(head.width) + 15);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Repaint(window);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Key(window, KeyCode.C, Ctrl); Key(window, KeyCode.J, Ctrl);
            Assert.That(PainterClipboard.Content, Is.Null, "Ctrl+C belongs to the text field");
            Assert.That(d.Layers.Count, Is.EqualTo(count), "Ctrl+J too");
            Key(window, KeyCode.Escape); Repaint(window);
            Assert.That(d.Layers.Last().Name, Is.EqualTo(name));

            BeginLine(300, 300);
            Key(window, KeyCode.J, Ctrl);
            Assert.That(d.Layers.Count, Is.EqualTo(count), "no duplicate during a stroke");
            Assert.That(window.StatusMessage, Does.Contain("A stroke is in progress"));
            Assert.That(window.IsStroking, Is.True, "the stroke goes on");
            Mouse(window, EventType.MouseUp, At(window, 360, 300));
            Key(window, KeyCode.J, Ctrl);
            Assert.That(d.Layers.Count, Is.EqualTo(count + 1));
        }

        [Test] public void PastingWorksAcrossTextureSetsAndWindowsAndCentresOnAnotherSize()
        {
            var d = window.Document; PainterClipboard.Content = null;
            var layer = d.Layers.Last(); d.Fill(layer.Id, PaintChannel.Color, new Rgba32(5, 200, 100, 255), 1, SelectionMask.Rectangle(d, 100, 100, 140, 120));
            Key(window, KeyCode.C, Ctrl);
            Assert.That(PainterClipboard.Content, Is.Not.Null, window.StatusMessage);

            var hair = window.AddTextureSet(-1); // モデルが無い: マテリアルに結び付けないセット
            Assert.That(window.Document, Is.SameAs(hair.Document));
            Key(window, KeyCode.V, Ctrl);
            var pasted = hair.Document.GetLayer(window.SelectedLayer);
            Assert.That(pasted.GetPixel(PaintChannel.Color, 120, 110), Is.EqualTo(new Rgba32(5, 200, 100, 255)), window.StatusMessage);
            Assert.That(d.Layers.Count, Is.EqualTo(1), "the first set is untouched");

            var other = Open();
            try
            {
                other.CreateProject(new NewProjectSettings { Resolution = 512, Template = ProjectTemplate.Pbr });
                Key(other, KeyCode.V, Ctrl);
                var there = other.Document.GetLayer(other.SelectedLayer);
                Assert.That(other.StatusMessage, Does.Contain("centred"));
                int x = (512 - 40) / 2, y = (512 - 20) / 2;
                Assert.That(there.GetPixel(PaintChannel.Color, x + 1, y + 1), Is.EqualTo(new Rgba32(5, 200, 100, 255)));
            }
            finally { Close(other); PainterClipboard.Content = null; }
        }

        [Test] public void TheMenusOfferTheLayerOperationsWithTheirKeys()
        {
            var d = window.Document; var layer = d.Layers.Last();
            var edit = new PaintMenu(); Invoke(window, "EditMenu", edit);
            var texts = LayerMenuTexts(edit);
            Assert.That(texts, Does.Contain("Cut    Ctrl+X")); Assert.That(texts, Does.Contain("Copy    Ctrl+C"));
            Assert.That(texts, Does.Contain("Copy Merged    Ctrl+Shift+C"));
            Assert.That(texts, Does.Contain("Paste    Ctrl+V (disabled)").Or.Contain("Paste    Ctrl+V"));
            var layers = new PaintMenu(); Invoke(window, "LayerMenu", layers);
            texts = LayerMenuTexts(layers);
            Assert.That(texts, Does.Contain("Duplicate Layer    Ctrl+J"));
            Assert.That(texts, Does.Contain("Merge Down    Ctrl+E (disabled)"), "the bottom layer has nothing below");
            Assert.That(texts, Does.Contain("Merge Visible    Ctrl+Shift+E"));
            var group = d.GroupLayers(new[] { layer.Id }, "g"); window.SelectedLayer = group.Id;
            layers = new PaintMenu(); Invoke(window, "LayerMenu", layers);
            Assert.That(LayerMenuTexts(layers), Does.Contain("Merge Group    Ctrl+E"));
            // 項目を選ぶと同じ操作になる
            Run(layers, "Duplicate Layer    Ctrl+J");
            Assert.That(d.GetLayer(window.SelectedLayer).Name, Is.EqualTo("g copy"));
        }

        static IEnumerable LayerMenuItems(PaintMenu menu)
        {
            var field = typeof(PaintMenu).GetField("m_MenuItems", BindingFlags.NonPublic | BindingFlags.Instance) ?? typeof(PaintMenu).GetField("menuItems", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) Assert.Inconclusive("PaintMenu.menuItems is not there in this Unity version.");
            return (IEnumerable)field.GetValue(menu);
        }
        static List<string> LayerMenuTexts(PaintMenu menu)
        {
            var texts = new List<string>();
            foreach (var item in LayerMenuItems(menu))
            {
                var t = item.GetType();
                if ((bool)t.GetField("separator").GetValue(item)) { texts.Add("---"); continue; }
                bool enabled = t.GetField("func")?.GetValue(item) != null || t.GetField("func2")?.GetValue(item) != null;
                texts.Add(((GUIContent)t.GetField("content").GetValue(item)).text + (enabled ? "" : " (disabled)"));
            }
            return texts;
        }
        static void Run(PaintMenu menu, string text)
        {
            foreach (var item in LayerMenuItems(menu))
            {
                var t = item.GetType();
                if (((GUIContent)t.GetField("content").GetValue(item)).text != text) continue;
                ((PaintMenu.MenuFunction)t.GetField("func").GetValue(item))(); return;
            }
            Assert.Fail("No menu item " + text);
        }
    }
}
