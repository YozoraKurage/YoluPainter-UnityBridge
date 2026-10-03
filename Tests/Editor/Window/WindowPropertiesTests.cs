using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>プロパティの欄（選んでいる物だけを出す）と、テクスチャセットの設定・オプションバーの対称と手ぶれ補正を、本物のウィンドウに
    /// マウスの入力を流して操作する（GUI モード）: タブの切り替え、大見出しと小見出しの開閉、小見出しの「既定に戻す」、ドックの見出しの
    /// 「プロパティ ― ブラシ」、Anchor の行、オプションバーの切り替えと ▾、テクスチャセットの設定のタブ、開閉の記憶の保存。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void TheTabStripSwitchesBetweenTheBrushAndItsMaterial()
        {
            window.SelectTool(TexturePaintWindow.PaintTool.Brush); Repaint(window);
            Assert.That(window.LastPropertyContext, Is.EqualTo(PropertyContext.Brush));
            Assert.That(window.LastPropertyTab, Is.EqualTo(TexturePaintWindow.TabBrush), "the Brush tab first");
            Assert.That(window.ToolControlScreenRects.ContainsKey("spacing"), Is.True);
            Assert.That(window.ToolControlScreenRects.ContainsKey("material.toggle"), Is.False, "the material is on its own tab");
            ClickToolControl("propertyTab." + TexturePaintWindow.TabMaterial); Repaint(window);
            Assert.That(window.LastPropertyTab, Is.EqualTo(TexturePaintWindow.TabMaterial));
            Assert.That(window.ToolControlScreenRects.ContainsKey("material.toggle"), Is.True);
            Assert.That(window.ToolControlScreenRects.ContainsKey("spacing"), Is.False, "only the shown tab's controls are there");
            // タブは文脈ごとに覚える: ほかのツールの後でブラシに戻ってもマテリアル
            window.SelectTool(TexturePaintWindow.PaintTool.Move); Repaint(window);
            Assert.That(window.LastPropertyContext, Is.EqualTo(PropertyContext.Move)); Assert.That(window.LastPropertyTab, Is.Null, "the move tool has no tabs");
            window.SelectTool(TexturePaintWindow.PaintTool.Brush); Repaint(window);
            Assert.That(window.LastPropertyTab, Is.EqualTo(TexturePaintWindow.TabMaterial));
            ClickToolControl("propertyTab." + TexturePaintWindow.TabBrush); Repaint(window);
            Assert.That(window.LastPropertyTab, Is.EqualTo(TexturePaintWindow.TabBrush));
            Assert.That(window.Document.UndoCount, Is.Zero);
        }

        /// <summary>小見出しは「ブラシ」の中（一段下）。押すと開き、右端のボタンでそのまとまりだけを既定に戻す。大見出しを閉じると小見出しも隠れる。</summary>
        [Test] public void SubsectionsOpenInsideTheBrushAndResetTheirOwnValues()
        {
            window.SelectTool(TexturePaintWindow.PaintTool.Brush); Repaint(window);
            Assert.That(window.LastPropertySections, Is.EqualTo(new[] { "brush", "brush-jitter", "brush-texture", "brush-dual", "brush-color", "brush-fade" }));
            Assert.That(window.SectionHeaderRects["brush"].sub, Is.False); Assert.That(window.SectionHeaderRects["brush-jitter"].sub, Is.True);
            Assert.That(window.SectionHeaderRects["brush-jitter"].rect.x, Is.GreaterThan(window.SectionHeaderRects["brush"].rect.x + 8), "one level in");
            Assert.That(window.SectionOpenNow("brush-jitter"), Is.False, "the details start closed");
            ClickToolControl("section.brush-jitter", .3f);
            Assert.That(window.SectionOpenNow("brush-jitter"), Is.True);
            window.Brush.sizeJitter = .5f; window.Brush.scatter = 3; window.Brush.radius = 33;
            ClickToolControl("section.brush-jitter", .98f); // 右端の「既定に戻す」
            Assert.That((window.Brush.sizeJitter, window.Brush.scatter), Is.EqualTo((0f, 0f)), window.StatusMessage);
            Assert.That(window.Brush.radius, Is.EqualTo(33), "only that group");
            Assert.That(window.SectionOpenNow("brush-jitter"), Is.True, "the reset button does not fold the section");
            ClickToolControl("section.brush", .3f); Repaint(window);
            Assert.That(window.SectionOpenNow("brush"), Is.False);
            Assert.That(window.LastPropertySections, Is.EqualTo(new[] { "brush" }), "the subsections hide with their section");
            ClickToolControl("section.brush", .3f); Repaint(window);
            Assert.That(window.LastPropertySections, Does.Contain("brush-jitter"));
        }

        [Test] public void ThePropertiesHeaderNamesTheSelectedThing()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            window.SelectTool(TexturePaintWindow.PaintTool.Brush); Repaint(window);
            Assert.That(window.PropertyContextTitle(), Is.EqualTo("Brush"));
            d.AddLayerMask(layer.Id); window.EditMask = true; Repaint(window);
            Assert.That(window.PropertyContextTitle(), Is.EqualTo("Brush · Layer mask"));
            window.EditMask = false;
            var blur = window.AddFilter(FilterTarget.Content, FilterSettings.GaussianBlur(2)); Repaint(window);
            Assert.That(window.PropertyContextTitle(), Is.EqualTo("Filter"));
            Assert.That(window.LastPropertySections, Is.EqualTo(new[] { "effect" }));
            window.SelectedFilter = Guid.Empty;
            window.SelectedLayer = d.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels()).Id; Repaint(window);
            Assert.That(window.PropertyContextTitle(), Is.EqualTo("Adjustment Layer: Levels"));
            window.SelectTool(TexturePaintWindow.PaintTool.Lasso); Repaint(window);
            Assert.That(window.PropertyContextTitle(), Is.EqualTo("Lasso"), "a tool that does not paint shows its own settings on any layer");
        }

        /// <summary>Anchor の行（レイヤーの重なりの子の行）を押すとプロパティの欄は Anchor で、名前を打って変えられ（1 回の Undo）、行の × で外す。</summary>
        [Test] public void AnAnchorRowShowsTheAnchorInPropertiesWhereItIsRenamedAndRemoved()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            var anchor = window.AddAnchorTo(AnchorPlacement.Layer);
            Assert.That(anchor, Is.Not.Null, window.StatusMessage);
            window.SelectTool(TexturePaintWindow.PaintTool.Brush); Repaint(window);
            Assert.That(window.LastPropertyContext, Is.EqualTo(PropertyContext.Brush), "adding an anchor keeps the brush in Properties");
            var row = LayerPanelPoint("anchor." + anchor.Id, .5f); SendHost(EventType.MouseDown, row); SendHost(EventType.MouseUp, row); Repaint(window);
            Assert.That(window.LastPropertyContext, Is.EqualTo(PropertyContext.Anchor));
            Assert.That(window.PropertyContextTitle(), Is.EqualTo("Anchor"));
            int steps = d.UndoCount;
            var field = LayerControlPoint("anchor.name", .5f); HostMouse(EventType.MouseDown, field); HostMouse(EventType.MouseUp, field); Repaint(window);
            window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.End });
            for (int i = 0; i < anchor.Name.Length + 2; i++) window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Backspace });
            foreach (char c in "Wear base") window.SendEvent(new Event { type = EventType.KeyDown, character = c });
            window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return }); Repaint(window);
            Assert.That(d.GetLayer(layer.Id).Anchor.Name, Is.EqualTo("Wear base"), window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "renaming is one undo step");
            var remove = LayerPanelPoint("anchor." + anchor.Id + ".remove"); SendHost(EventType.MouseDown, remove); SendHost(EventType.MouseUp, remove); Repaint(window);
            Assert.That(d.GetLayer(layer.Id).Anchor, Is.Null);
            Assert.That(window.LastPropertyContext, Is.EqualTo(PropertyContext.Brush), "the removed anchor is no longer selected");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(d.GetLayer(layer.Id).Anchor?.Name, Is.EqualTo("Wear base"));
        }

        [Test] public void TheOptionsBarTurnsSymmetryAndTheStabilizerOnAndOpensTheirSettings()
        {
            window.SelectTool(TexturePaintWindow.PaintTool.Brush);
            var opened = new System.Collections.Generic.List<OptionPopupKind>();
            window.OpenOptionPopupOverride = (kind, at) => opened.Add(kind);
            ClickToolControl("options.symmetry"); Assert.That(window.Symmetry, Is.True);
            ClickToolControl("options.symmetry"); Assert.That(window.AnySymmetry, Is.False);
            ClickToolControl("options.symmetry.more"); Assert.That(opened, Is.EqualTo(new[] { OptionPopupKind.Symmetry }));
            ClickToolControl("options.stabilizer"); Assert.That(window.Stabilizer, Is.EqualTo(20));
            ClickToolControl("options.stabilizer.length", .5f); Assert.That(window.Stabilizer, Is.EqualTo(50).Within(3), "the slider sets the string length");
            ClickToolControl("options.stabilizer"); Assert.That(window.Stabilizer, Is.Zero);
            ClickToolControl("options.stabilizer"); Assert.That(window.Stabilizer, Is.EqualTo(50).Within(3), "back to the last length");
            ClickToolControl("options.stabilizer.more"); Assert.That(opened.Last(), Is.EqualTo(OptionPopupKind.Stabilizer));
            // 小さな窓の中身（プロパティの欄にあった手ぶれ補正・入り抜き）
            ClickToolControl("symmetry"); Assert.That(window.Symmetry, Is.True, "the popup's mirror switch");
            Assert.That(window.Document.UndoCount, Is.Zero, "settings are not document edits");
        }

        [Test] public void TheTextureSetSettingsTabHoldsTheNormalOutputAndTheMeshMaps()
        {
            var layers = window.PanelHeaderRectForTests("layers"); Repaint(window);
            Assert.That(window.PanelShown("layers"), Is.True); Assert.That(window.PanelShown("textureSetSettings"), Is.False, "Layers is the shown tab");
            ShowTextureSetSettings();
            Assert.That(window.PanelShown("textureSetSettings"), Is.True);
            Assert.That(window.LayerControlScreenRects.Keys, Has.Member("normal.derive").And.No.Member("meshmap.bake"), "the mesh maps start folded");
            ClickToolControl("section.mesh-maps", .3f); Repaint(window);
            Assert.That(window.ShowMeshMapPanel, Is.True); Assert.That(window.LayerControlScreenRects.Keys, Has.Member("meshmap.bake"));
            int steps = window.Document.UndoCount;
            ClickLayerControl("normal.derive");
            Assert.That(window.Document.NormalSettings.DeriveFromHeight, Is.True); Assert.That(window.Document.UndoCount, Is.EqualTo(steps + 1));
            Assert.That(layers.width, Is.GreaterThan(0));
        }

        /// <summary>開いた・閉じた欄とタブは描き手ごとに覚える（EditorPrefs）。試験は既定に固定しているので、ここだけ一時的に保存させて元に戻す。</summary>
        [Test] public void OpeningASectionIsRememberedForTheNextWindow()
        {
            string before = EditorPrefs.GetString(PropertySectionStore.Key, null); bool had = EditorPrefs.HasKey(PropertySectionStore.Key);
            DockLayoutStore.UseDefaults = false;
            try
            {
                EditorPrefs.DeleteKey(PropertySectionStore.Key);
                window.SelectTool(TexturePaintWindow.PaintTool.Brush); Repaint(window);
                ClickToolControl("section.brush-dual", .3f);
                ClickToolControl("propertyTab." + TexturePaintWindow.TabMaterial);
                var saved = PropertySectionStore.Load();
                Assert.That(saved.Open["brush-dual"], Is.True);
                Assert.That(saved.Tabs[PropertyContext.Brush], Is.EqualTo(TexturePaintWindow.TabMaterial));
            }
            finally
            {
                DockLayoutStore.UseDefaults = true;
                if (had) EditorPrefs.SetString(PropertySectionStore.Key, before); else EditorPrefs.DeleteKey(PropertySectionStore.Key);
            }
        }

        /// <summary>2 行のスライダー（Substance と同じ: 1 行目に名前と値の箱、2 行目に全幅の溝）: 溝のドラッグは 1 回の Undo、値の箱を押すと
        /// 数を打てて Enter で決まり（1 回の Undo）、Esc ではやめる。</summary>
        [Test] public void TheTwoLineSliderDragsTypesAndIsOneUndoStepEach()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer); d.AddLayerMask(layer.Id);
            window.EditMask = true; window.SetPropertyTab(PropertyContext.Brush, TexturePaintWindow.TabMask); Repaint(window);
            Assert.That(window.LayerControlPanelRects["mask.density"].height, Is.EqualTo(PaintGui.SliderRowHeight), "the slider takes two lines");
            int steps = d.UndoCount;
            DragLayerControl("mask.density", .95f, .5f);
            Assert.That(layer.Mask.Density, Is.EqualTo(.5).Within(.03)); Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "the drag on the groove is one undo step");
            // 値の箱（1 行目の右）を押して打つ
            var center = LayerControlPoint("mask.density", .5f); var full = window.LayerControlScreenRects["mask.density"];
            var box = new Vector2(center.x + full.width / 2 - 8, center.y - full.height / 2 + 7);
            HostMouse(EventType.MouseDown, box); HostMouse(EventType.MouseUp, box); Repaint(window);
            Assert.That(layer.Mask.Density, Is.EqualTo(.5).Within(.03), "pressing the box does not move the value");
            void Type(string text) { foreach (char ch in text) window.SendEvent(new Event { type = EventType.KeyDown, character = ch }); }
            window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.End });
            for (int i = 0; i < 6; i++) window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Backspace });
            Type("25"); // 今の値を消して 25
            window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return }); Repaint(window);
            Assert.That(layer.Mask.Density, Is.EqualTo(.25).Within(.001), "the typed value: " + window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 2), "typing is one more undo step");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(layer.Mask.Density, Is.EqualTo(.5).Within(.03));
            // Esc ではやめる
            box = new Vector2(LayerControlPoint("mask.density", .5f).x + full.width / 2 - 8, box.y);
            HostMouse(EventType.MouseDown, box); HostMouse(EventType.MouseUp, box); Repaint(window);
            Type("9"); window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape }); Repaint(window);
            Assert.That(layer.Mask.Density, Is.EqualTo(.5).Within(.03), "Escape leaves the value");
        }

        /// <summary>筆圧で変えるスライダー（サイズ・流量・不透明度）の 2 行目の右のペンのボタンで、筆圧に従わせるかを切り替える（値は変えない）。</summary>
        [Test] public void ThePenButtonsBesideSizeFlowAndOpacityTurnPressureOnAndOff()
        {
            window.SelectTool(TexturePaintWindow.PaintTool.Brush); Repaint(window);
            var b = window.Brush; float radius = b.radius, flow = b.flow, opacity = b.opacity;
            bool size = b.pressureSize, flowPressure = b.pressureFlow, opacityPressure = b.pressureOpacity;
            ClickToolControl("brush.size.pen"); ClickToolControl("brush.flow.pen"); ClickToolControl("brush.opacity.pen");
            Assert.That((b.pressureSize, b.pressureFlow, b.pressureOpacity), Is.EqualTo((!size, !flowPressure, !opacityPressure)));
            Assert.That((b.radius, b.flow, b.opacity), Is.EqualTo((radius, flow, opacity)), "the pen buttons do not move the sliders");
            ClickToolControl("brush.size", .5f);
            float w = window.ToolControlScreenRects["brush.size"].width, groove = w - 28; // ペンのボタンと間の分だけ溝が短い
            Assert.That(b.radius * 2, Is.EqualTo(1 + 255 * Mathf.Clamp01(w * .5f / groove)).Within(3), "the groove ends before the pen button");
        }

        /// <summary>アルファのタブ: 先端の一覧の見本を押すとその先端に替わり、選んだ見本に枠。円形に戻せる。</summary>
        [Test] public void TheAlphaTabPicksATipFromItsList()
        {
            window.SelectTool(TexturePaintWindow.PaintTool.Brush); Repaint(window);
            ClickToolControl("propertyTab." + TexturePaintWindow.TabAlpha); Repaint(window);
            Assert.That(window.LastPropertyTab, Is.EqualTo(TexturePaintWindow.TabAlpha));
            Assert.That(window.ToolControlScreenRects.ContainsKey("alpha.hardness"), Is.True);
            Assert.That(window.ToolControlScreenRects.ContainsKey("spacing"), Is.False, "the brush values stay on the Brush tab");
            string tip = window.TipChoices().First(t => t.id.StartsWith("builtin:", StringComparison.Ordinal)).id;
            ClickToolControl("alpha.tip." + tip);
            Assert.That(window.Brush.tipId, Is.EqualTo(tip), window.StatusMessage);
            ClickToolControl("alpha.tip.");
            Assert.That(window.Brush.tipId, Is.EqualTo(""), "the round tip");
            Assert.That(window.Document.UndoCount, Is.Zero);
        }
    }
}
