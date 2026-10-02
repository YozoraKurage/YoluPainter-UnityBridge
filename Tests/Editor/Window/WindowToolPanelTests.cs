using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>プロパティの欄のツールの設定を、本物のウィンドウにマウスの入力を流して操作する（GUI モード）: スライダーはブラシだけを
    /// 変え、選択範囲の変更と変形のボタンはそれぞれ 1 回の Undo になり、数値の欄はドラッグで値が変わる。ツールのセクションは
    /// Unity の標準の部品の区画（LegacySection）を使わない。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>覚えた部品の位置（ウィンドウの GUI の座標。Mouse に渡す）。見えるところまでプロパティの欄を送ってから測る。</summary>
        Vector2 ToolControlPoint(string id, float fx = .5f)
        {
            Repaint(window); window.ScrollPropertiesTo(id); Repaint(window);
            Assert.That(window.ToolControlScreenRects.TryGetValue(id, out var r), Is.True, id + " was not drawn");
            // 画面の座標から窓の左上（position）とタブの高さ（rootVisualElement.worldBound、21 px）を引くと GUI の座標（GUI モードで実測）
            var at = new Vector2(r.x + r.width * fx, r.center.y) - window.position.position - window.rootVisualElement.worldBound.position;
            Assert.That(at.y, Is.InRange(0f, window.position.height - 24), id + " is outside the window; the Properties panel did not scroll to it");
            return at;
        }
        void ClickToolControl(string id, float fx = .5f) { var at = ToolControlPoint(id, fx); Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at); }

        void ToolPanelDot(int x, int y) { var at = At(window, x, y); Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at); }

        [Test] public void ToolSectionsDrawWithThePaintKitAndNoLegacySection()
        {
            var field = typeof(TexturePaintWindow).GetField("legacyHeights", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) Assert.Pass("LegacySection is gone from the Properties panel altogether.");
            var legacy = (Dictionary<string, float>)field.GetValue(window);
            var exceptions = new List<string>();
            Application.LogCallback onLog = (text, stack, type) => { if (type == LogType.Exception) exceptions.Add(text); };
            Application.logMessageReceived += onLog;
            try
            {
                window.SetToolSectionsOpen(true);
                window.Document.SetSelection(SelectionMask.All(window.Document));
                window.Tool = TexturePaintWindow.PaintTool.Eyedropper; // ツールのセクションが無いツール
                Repaint(window); Repaint(window);
                var baseline = new HashSet<string>(legacy.Keys);
                foreach (TexturePaintWindow.PaintTool tool in Enum.GetValues(typeof(TexturePaintWindow.PaintTool)))
                {
                    window.Tool = tool; Repaint(window); Repaint(window); // 2 回目は測った高さで描き直す
                    Assert.That(legacy.Keys.Except(baseline), Is.Empty, tool + " drew a tool section with Unity's controls");
                    Assert.That(legacy.Keys.Intersect(TexturePaintWindow.ToolSectionKeys), Is.Empty, tool.ToString());
                }
                Assert.That(window.ToolControlScreenRects.Keys, Is.SupersetOf(new[] { "spacing", "texture", "radius", "grow", "flip-horizontal", "offset-x", "apply-transform" }),
                    "every tool panel was drawn with its controls");
            }
            finally { Application.logMessageReceived -= onLog; }
            Assert.That(exceptions, Is.Empty, "drawing the tool panels threw");
        }

        [Test] public void ThePanelSpacingSliderChangesTheBrushAndNothingElse()
        {
            window.Tool = TexturePaintWindow.PaintTool.Brush;
            var start = ToolControlPoint("spacing", .75f);
            Mouse(window, EventType.MouseDown, start);
            Assert.That(window.Brush.spacing, Is.EqualTo(.7525f).Within(.01f), "pressing sets the value under the pointer (1–100 %)");
            Mouse(window, EventType.MouseDrag, ToolControlPoint("spacing", .25f));
            Mouse(window, EventType.MouseUp, ToolControlPoint("spacing", .25f));
            Assert.That(window.Brush.spacing, Is.EqualTo(.2575f).Within(.01f), "dragging follows the pointer");
            Assert.That(window.IsStroking, Is.False, "the press did not reach the canvas");
            Assert.That(window.Document.CanUndo, Is.False, "brush settings are not document edits");
            Assert.That(window.GetBrush().Spacing, Is.EqualTo(window.Brush.spacing).Within(1e-6), "the next stroke uses it");
        }

        [Test] public void ThePanelModifySelectionButtonsAreOneUndoStepEach()
        {
            var d = window.Document;
            window.Tool = TexturePaintWindow.PaintTool.SelectRectangle;
            ClickToolControl("grow");
            Assert.That(d.Selection, Is.Null); Assert.That(d.CanUndo, Is.False, "Grow is disabled without a selection");
            Drag(100, 100, 200, 200);
            Assert.That(d.Selection, Is.Not.Null, window.StatusMessage);
            ClickToolControl("radius", .05f); // 0–200 px → 10 px
            int radius = window.SelectionRadius;
            Assert.That(radius, Is.InRange(9, 11));
            int steps = d.UndoCount;
            ClickToolControl("grow");
            Assert.That(window.StatusMessage, Does.Contain("Grow by " + radius + " px"));
            Assert.That(d.Selection[100 - radius + 1, 150], Is.EqualTo(255)); Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(d.Selection[100 - radius + 1, 150], Is.Zero, "one undo takes the growth back");
            Assert.That(d.Selection[150, 150], Is.EqualTo(255));
        }

        [Test] public void ThePanelFlipButtonAndNumericFieldsTransformTheLayer()
        {
            var d = window.Document;
            ToolPanelDot(100, 500); ToolPanelDot(300, 520); // 中心について左右が非対称
            window.Tool = TexturePaintWindow.PaintTool.Move;
            int steps = d.UndoCount;
            ClickToolControl("flip-horizontal");
            Assert.That(window.StatusMessage, Is.EqualTo("Flipped horizontally."));
            Assert.That(d.CompositePixel(PaintChannel.Color, 300, 500).A, Is.GreaterThan((byte)0), "the left dot is now on the right");
            Assert.That(d.CompositePixel(PaintChannel.Color, 100, 500).A, Is.Zero);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            // 数値の欄: 左右にドラッグすると 1 px ごとに 1 変わる。動かさずに離せば入力になる（ここでは使わない）
            var x = ToolControlPoint("offset-x");
            Mouse(window, EventType.MouseDown, x);
            Mouse(window, EventType.MouseDrag, x + new Vector2(10, 0));
            Mouse(window, EventType.MouseDrag, x + new Vector2(20, 0));
            Mouse(window, EventType.MouseUp, x + new Vector2(20, 0));
            Assert.That(window.MoveOffset.x, Is.EqualTo(20).Within(.001f));
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "editing the numbers changes nothing yet");
            ClickToolControl("apply-transform");
            Assert.That(window.StatusMessage, Is.EqualTo("Transformed."));
            Assert.That(window.MoveOffset, Is.EqualTo(Vector2.zero), "the numbers reset after applying");
            Assert.That(d.CompositePixel(PaintChannel.Color, 320, 500).A, Is.GreaterThan((byte)0), "moved 20 px right");
            Assert.That(d.CompositePixel(PaintChannel.Color, 290, 500).A, Is.Zero);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 2));
            d.Undo();
            Assert.That(d.CompositePixel(PaintChannel.Color, 300, 500).A, Is.GreaterThan((byte)0), "one undo takes the numeric move back");
        }
    }
}
