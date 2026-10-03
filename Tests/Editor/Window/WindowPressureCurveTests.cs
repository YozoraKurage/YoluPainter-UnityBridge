using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>筆圧のカーブをプロパティの欄で直接編集する（GUI モード、本物のマウスの入力）: 押して点を足し、ドラッグで動かし、Esc で
    /// ドラッグを取り消し、右クリックと枠の外へのドラッグで消す。両端の点は横に動かない。ブラシの設定なので文書の Undo には入らない。
    /// 編集したカーブはブラシのプリセット（.json）と .ylp の brush.json に入って戻り、以前の版のファイルのカーブは同じ値のまま読める。
    /// ストロークの筆圧はこのカーブを通る。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>カーブのグラフの中の点（入力 x、出力 y、どちらも 0〜1）の GUI 座標。グラフは部品の枠の 6 点内側。</summary>
        Vector2 CurvePoint(float x, float y)
        {
            Repaint(window); window.ScrollPropertiesTo("pressure-curve"); Repaint(window);
            Assert.That(window.ToolControlScreenRects.TryGetValue("pressure-curve", out var r), Is.True, "the pressure curve was not drawn");
            var origin = window.position.position + window.rootVisualElement.worldBound.position;
            var graph = new Rect(r.x + 6 - origin.x, r.y + 6 - origin.y, r.width - 12, r.height - 12);
            return new Vector2(graph.x + x * graph.width, graph.yMax - y * graph.height);
        }
        static void MouseButton(EditorWindow w, EventType type, Vector2 position, int button)
        { EditorShaderCompiler.TolerateErrorLogsIfBroken(); w.SendEvent(new Event { type = type, mousePosition = position + w.rootVisualElement.worldBound.position, button = button, pressure = 1 }); }
        AnimationCurve CurrentCurve => window.Brush.pressureCurve;

        [Test] public void ThePressureCurveIsEditedDirectlyInThePanel()
        {
            window.Tool = TexturePaintWindow.PaintTool.Brush;
            Assert.That(PressureCurve.Matches(CurrentCurve, PressureCurve.Presets[0].Points), Is.True, "a new window starts linear");
            // 何も無いところを押すと点が足され、離すまでドラッグで動く
            var at = CurvePoint(.5f, .2f);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(PressureCurve.Points(CurrentCurve).Count, Is.EqualTo(3));
            Assert.That(CurrentCurve.Evaluate(.5f), Is.EqualTo(.2f).Within(.02f), "the curve passes through the new point");
            Mouse(window, EventType.MouseDown, CurvePoint(.5f, .2f)); Mouse(window, EventType.MouseDrag, CurvePoint(.6f, .45f)); Mouse(window, EventType.MouseDrag, CurvePoint(.6f, .7f));
            Mouse(window, EventType.MouseUp, CurvePoint(.6f, .7f));
            Assert.That(CurrentCurve.Evaluate(.6f), Is.EqualTo(.7f).Within(.02f), "dragging moves the point");
            Assert.That(window.IsStroking, Is.False, "the press did not reach the canvas");
            Assert.That(window.Document.CanUndo, Is.False, "brush settings are not document edits");
            // Esc でドラッグを取り消す
            var kept = CurrentCurve;
            Mouse(window, EventType.MouseDown, CurvePoint(.6f, .7f)); Mouse(window, EventType.MouseDrag, CurvePoint(.3f, .95f));
            Assert.That(CurrentCurve.Evaluate(.3f), Is.EqualTo(.95f).Within(.03f));
            Key(window, KeyCode.Escape);
            Assert.That(CurrentCurve, Is.SameAs(kept), "Escape puts the curve back as it was before the drag");
            Mouse(window, EventType.MouseUp, CurvePoint(.3f, .95f));
            Assert.That(CurrentCurve, Is.SameAs(kept));
            // 右クリックで消す（両端は消えない）
            MouseButton(window, EventType.MouseDown, CurvePoint(.6f, .7f), 1);
            Assert.That(PressureCurve.Points(CurrentCurve).Count, Is.EqualTo(2), "right-click removes the point");
            MouseButton(window, EventType.MouseDown, CurvePoint(1, 1), 1);
            Assert.That(PressureCurve.Points(CurrentCurve).Count, Is.EqualTo(2), "an end point cannot be removed");
            // 枠の外へドラッグして離すと消える（戻せば消えない）
            at = CurvePoint(.4f, .6f);
            Mouse(window, EventType.MouseDown, at);
            Mouse(window, EventType.MouseDrag, at + new Vector2(0, 200));
            Assert.That(PressureCurve.Points(CurrentCurve).Count, Is.EqualTo(2), "outside the box the point is shown as removed");
            Mouse(window, EventType.MouseDrag, CurvePoint(.4f, .6f));
            Assert.That(PressureCurve.Points(CurrentCurve).Count, Is.EqualTo(3), "dragged back in, it stays");
            Mouse(window, EventType.MouseDrag, at + new Vector2(0, 200)); Mouse(window, EventType.MouseUp, at + new Vector2(0, 200));
            Assert.That(PressureCurve.Points(CurrentCurve).Count, Is.EqualTo(2), "released outside, it is removed");
            // 両端の点は縦にだけ動く
            Mouse(window, EventType.MouseDown, CurvePoint(1, 1)); Mouse(window, EventType.MouseDrag, CurvePoint(.5f, .4f)); Mouse(window, EventType.MouseUp, CurvePoint(.5f, .4f));
            var points = PressureCurve.Points(CurrentCurve);
            Assert.That(points.Last().x, Is.EqualTo(1)); Assert.That(points.Last().y, Is.EqualTo(.4f).Within(.02f));
            // 線形に戻すボタン
            ClickToolControl("pressure-curve-reset");
            Assert.That(PressureCurve.Matches(CurrentCurve, PressureCurve.Presets[0].Points), Is.True);
            Assert.That(window.Document.CanUndo, Is.False);
        }

        [Test] public void TheCurveCannotBeEditedDuringAStroke()
        {
            var kept = CurrentCurve; var at = CurvePoint(.5f, .2f);
            BeginLine(300, 300);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at); // 離すとストロークが終わる（この入力でカーブは変わらない）
            Assert.That(CurrentCurve, Is.SameAs(kept), "the panel is disabled while painting");
            Assert.That(window.IsStroking, Is.False);
        }

        [Test] public void StrokesUseTheEditedPressureCurve()
        {
            var b = window.Brush; b.pressureSize = false; b.pressureOpacity = true; b.hardness = 1; b.opacity = 1; b.flow = 1; b.color = Color.black; window.Brush = b;
            byte Dot(int x, float pressure) { var at = At(window, x, 400); Mouse(window, EventType.MouseDown, at, pressure); Mouse(window, EventType.MouseUp, at, pressure); return window.Document.CompositePixel(PaintChannel.Color, x, 400).A; }
            Assert.That(Dot(200, .55f), Is.EqualTo(140).Within(1), "linear: 55 % pressure is 55 % opacity");
            window.SetPressureCurve(PressureCurve.Presets[2].Points); // 硬い: 55 % → 30 %
            Assert.That(Dot(300, .55f), Is.EqualTo(77).Within(1), "the hard curve turns 55 % pen pressure into 30 %");
            window.SetPressureCurve(PressureCurve.Presets[1].Points); // 柔らかい: 30 % → 55 %
            Assert.That(Dot(400, .3f), Is.EqualTo(140).Within(1));
        }

        [Test] public void TheEditedCurveRoundTripsThroughPresetsAndTheProjectAndOlderFilesReadTheSame()
        {
            var fake = UseFakeDialogs(window); fake.File = NewTempPath(".json");
            window.SetPressureCurve(new[] { new Vector2(0, .1f), new Vector2(.35f, .6f), new Vector2(.7f, .65f), new Vector2(1, .95f) });
            var edited = CurrentCurve;
            Invoke(window, "SavePreset");
            string saved = File.ReadAllText(fake.File);
            Assert.That(saved, Does.Contain("\"schema\": 3"), "the curve keeps the brush settings' schema");
            window.Brush = new TexturePaintWindow.BrushState();
            Invoke(window, "LoadPreset");
            Assert.That(JsonUtility.ToJson(window.Brush, true), Is.EqualTo(saved), window.StatusMessage);
            Assert.That(PressureCurve.IsEditable(CurrentCurve), Is.True);
            for (int i = 0; i <= 50; i++) Assert.That(CurrentCurve.Evaluate(i / 50f), Is.EqualTo(edited.Evaluate(i / 50f)).Within(1e-6f));

            // 以前の版のファイル（Unity のカーブエディタで作った自由な接線のカーブ）: 同じ値で読め、表示しても書き換わらない
            var older = new AnimationCurve(new Keyframe(0, 0, 0, .3f), new Keyframe(.5f, .2f, 1.8f, 1.8f), new Keyframe(1, 1, .4f, 0));
            window.Brush.pressureCurve = older;
            string olderJson = JsonUtility.ToJson(window.Brush, true);
            File.WriteAllText(fake.File, olderJson);
            window.Brush = new TexturePaintWindow.BrushState();
            Invoke(window, "LoadPreset");
            for (int i = 0; i <= 50; i++) Assert.That(CurrentCurve.Evaluate(i / 50f), Is.EqualTo(older.Evaluate(i / 50f)).Within(1e-6f), "an older curve reads the same");
            window.Tool = TexturePaintWindow.PaintTool.Brush; Repaint(window); Repaint(window);
            Assert.That(JsonUtility.ToJson(window.Brush, true), Is.EqualTo(olderJson), "drawing the panel does not rewrite an older curve");

            // .ylp の brush.json
            fake.File = NewYlpPath();
            window.SetPressureCurve(PressureCurve.Presets[3].Points);
            PaintDot(window, 300, 300); window.SaveProject(true);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File; other.OpenProject();
                Assert.That(JsonUtility.ToJson(other.Brush), Is.EqualTo(JsonUtility.ToJson(window.Brush)), other.StatusMessage);
                Assert.That(PressureCurve.Matches(other.Brush.pressureCurve, PressureCurve.Presets[3].Points), Is.True);
            }
            finally { Close(other); }
            // ブラシのプリセットを選んでも筆圧のカーブは描き手の設定として残る
            window.ApplyPreset(BuiltInBrushes.Presets[1]);
            Assert.That(PressureCurve.Matches(CurrentCurve, PressureCurve.Presets[3].Points), Is.True);
        }
    }
}
