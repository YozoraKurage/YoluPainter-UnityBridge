using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>筆圧のカーブ（PressureCurve と、プロパティの欄で直接編集する PaintGui.CurveEditor）: 点を通る単調な 3 次エルミートが
    /// AnimationCurve のキーと接線に入り、点が単調なら曲線も単調で 0〜1 に収まる。点の追加・移動・削除の決まり、よく使う形、
    /// 以前の版のカーブ（Unity のカーブエディタで作ったもの）は編集するまでそのまま評価されること。描いた PNG はテストプロジェクトの
    /// Logs/YoluPainterSnapshots/PressureCurve に残す（見て確かめる用）。</summary>
    public sealed class PressureCurveTests
    {
        static List<Vector2> P(params float[] xy) { var list = new List<Vector2>(); for (int i = 0; i < xy.Length; i += 2) list.Add(new Vector2(xy[i], xy[i + 1])); return list; }

        /// <summary>曲線を細かく評価して、範囲・単調さ（増えるか減るかの向きが変わる回数）を見る。</summary>
        static (float min, float max, bool nondecreasing) Scan(AnimationCurve curve, int samples = 2000)
        {
            float min = float.MaxValue, max = float.MinValue, last = float.MinValue; bool up = true;
            for (int i = 0; i <= samples; i++)
            {
                float v = curve.Evaluate(i / (float)samples);
                min = Math.Min(min, v); max = Math.Max(max, v);
                if (v < last - 1e-5f) up = false; last = v;
            }
            return (min, max, up);
        }

        [Test] public void TheLinearShapeIsTheOldDefaultCurve()
        {
            var old = AnimationCurve.Linear(0, 0, 1, 1); // 以前の版の BrushState の既定
            Assert.That(PressureCurve.IsEditable(old), Is.True, "the old default is already in the editor's form");
            Assert.That(PressureCurve.Points(old), Is.EqualTo(P(0, 0, 1, 1)));
            Assert.That(PressureCurve.Matches(old, PressureCurve.Presets[0].Points), Is.True, "shown as the Linear shape");
            var linear = PressureCurve.Linear();
            for (int i = 0; i <= 100; i++) Assert.That(linear.Evaluate(i / 100f), Is.EqualTo(old.Evaluate(i / 100f)).Within(1e-6f));
        }

        [Test] public void PresetsPassThroughTheirPointsAndStayMonotoneWithinRange()
        {
            foreach (var (name, points) in PressureCurve.Presets)
            {
                var curve = PressureCurve.FromPoints(points);
                foreach (var p in points) Assert.That(curve.Evaluate(p.x), Is.EqualTo(p.y).Within(1e-5f), name);
                var (min, max, up) = Scan(curve);
                Assert.That(up, Is.True, name + " is monotone"); Assert.That(min, Is.GreaterThanOrEqualTo(-1e-6f), name); Assert.That(max, Is.LessThanOrEqualTo(1 + 1e-6f), name);
            }
            // 柔らかい: 弱い筆圧でも濃い（対角より上）。硬い: 強く押すまで薄い（下）。S 字: 真ん中で急
            Assert.That(PressureCurve.FromPoints(PressureCurve.Presets[1].Points).Evaluate(.3f), Is.EqualTo(.55f).Within(1e-5f));
            Assert.That(PressureCurve.FromPoints(PressureCurve.Presets[2].Points).Evaluate(.55f), Is.EqualTo(.3f).Within(1e-5f));
            var s = PressureCurve.FromPoints(PressureCurve.Presets[3].Points);
            Assert.That(s.Evaluate(.1f), Is.LessThan(.1f)); Assert.That(s.Evaluate(.9f), Is.GreaterThan(.9f));
        }

        [Test] public void MonotonePointsGiveAMonotoneCurveAndAnyPointsStayWithinRange()
        {
            var random = new System.Random(1234);
            for (int trial = 0; trial < 300; trial++)
            {
                int n = 2 + random.Next(PressureCurve.MaxPoints - 1);
                var xs = Enumerable.Range(0, n - 2).Select(_ => (float)(.03 + random.NextDouble() * .94)).OrderBy(x => x).ToList();
                var list = new List<Vector2> { new Vector2(0, 0) };
                foreach (var x in xs) if (x - list[list.Count - 1].x >= PressureCurve.MinGap && 1 - x >= PressureCurve.MinGap) list.Add(new Vector2(x, 0));
                list.Add(new Vector2(1, 0));
                bool monotone = trial % 2 == 0;
                var ys = Enumerable.Range(0, list.Count).Select(_ => (float)random.NextDouble()).ToList();
                if (monotone) ys.Sort();
                for (int i = 0; i < list.Count; i++) list[i] = new Vector2(list[i].x, ys[i]);
                var curve = PressureCurve.FromPoints(list);
                Assert.That(PressureCurve.IsEditable(curve), Is.True);
                var (min, max, up) = Scan(curve, 1000);
                Assert.That(min, Is.GreaterThanOrEqualTo(-1e-5f), "trial " + trial); Assert.That(max, Is.LessThanOrEqualTo(1 + 1e-5f), "trial " + trial);
                if (monotone) Assert.That(up, Is.True, "monotone points, trial " + trial);
                // 区間ごとに両端の点の値の間に収まる（行き過ぎない）
                for (int k = 0; k + 1 < list.Count; k++)
                {
                    float lo = Math.Min(list[k].y, list[k + 1].y) - 1e-5f, hi = Math.Max(list[k].y, list[k + 1].y) + 1e-5f;
                    for (int j = 0; j <= 20; j++)
                    {
                        float v = curve.Evaluate(Mathf.Lerp(list[k].x, list[k + 1].x, j / 20f));
                        Assert.That(v, Is.InRange(lo, hi), "trial " + trial + " interval " + k);
                    }
                }
            }
        }

        [Test] public void PointsAreAddedMovedAndRemovedWithinTheRules()
        {
            var points = P(0, 0, 1, 1);
            Assert.That(PressureCurve.Insert(points, new Vector2(.5f, .2f)), Is.EqualTo(1));
            Assert.That(points, Is.EqualTo(P(0, 0, .5f, .2f, 1, 1)));
            Assert.That(PressureCurve.Insert(points, new Vector2(.51f, .9f)), Is.EqualTo(-1), "too close to a point");
            Assert.That(PressureCurve.Insert(points, new Vector2(.005f, .5f)), Is.EqualTo(-1), "too close to an end");
            Assert.That(PressureCurve.Insert(points, new Vector2(.25f, 1.7f)), Is.EqualTo(1));
            Assert.That(points[1], Is.EqualTo(new Vector2(.25f, 1)), "the output stays within 0..1");
            PressureCurve.Move(points, 1, new Vector2(.9f, -.3f));
            Assert.That(points[1].x, Is.EqualTo(.5f - PressureCurve.MinGap).Within(1e-6f), "a point stays left of its right neighbour");
            Assert.That(points[1].y, Is.Zero);
            PressureCurve.Move(points, 0, new Vector2(.4f, .3f));
            Assert.That(points[0], Is.EqualTo(new Vector2(0, .3f)), "the first point moves only up and down");
            PressureCurve.Move(points, points.Count - 1, new Vector2(.2f, .6f));
            Assert.That(points[points.Count - 1], Is.EqualTo(new Vector2(1, .6f)), "the last point moves only up and down");
            Assert.That(PressureCurve.Remove(points, 0), Is.False); Assert.That(PressureCurve.Remove(points, points.Count - 1), Is.False);
            Assert.That(PressureCurve.Remove(points, 1), Is.True); Assert.That(points.Count, Is.EqualTo(3));
            var full = Enumerable.Range(0, PressureCurve.MaxPoints).Select(i => new Vector2(i / (PressureCurve.MaxPoints - 1f), .5f)).ToList();
            Assert.That(PressureCurve.Insert(full, new Vector2(.03f, .5f)), Is.EqualTo(-1), "at most " + PressureCurve.MaxPoints + " points");
            Assert.That(() => PressureCurve.FromPoints(P(0, 0)), Throws.ArgumentException);
        }

        [Test] public void AnOlderCurveEvaluatesUnchangedUntilItIsEdited()
        {
            // 以前の版で Unity のカーブエディタで作った形（接線が自由、両端が 0〜1 の外）
            var old = new AnimationCurve(new Keyframe(-.2f, -.1f, 0, 2), new Keyframe(.4f, .7f, 1.5f, .2f), new Keyframe(1.3f, 1.2f, 0, 0));
            Assert.That(PressureCurve.IsEditable(old), Is.False);
            var points = PressureCurve.Points(old);
            Assert.That(points.Select(p => p.x), Is.EqualTo(new[] { 0, .25f, .5f, .75f, 1 }), "an older curve becomes five points when it is first edited");
            foreach (var p in points) Assert.That(p.y, Is.EqualTo(Mathf.Clamp01(old.Evaluate(p.x))).Within(1e-6f));
            Assert.That(PressureCurve.IsEditable(PressureCurve.FromPoints(points)), Is.True);
            Assert.That(PressureCurve.IsEditable(AnimationCurve.EaseInOut(0, 0, 1, 1)), Is.False, "ease in/out has flat ends, not the editor's tangents");
            Assert.That(PressureCurve.Points(null), Is.EqualTo(P(0, 0, 1, 1)));
            Assert.That(PressureCurve.Points(new AnimationCurve()), Is.EqualTo(P(0, 0, 1, 1)));
        }

        // ───────── 描画（batch-gl のオフスクリーン） ─────────

        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "PressureCurve"));

        [Test] public void TheCurveRowsDrawInANarrowDockInBothLanguagesWithoutCutText()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            var rows = typeof(TexturePaintWindow).GetMethod("PressureCurveRows", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(rows, Is.Not.Null);
            try
            {
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    foreach (var (shape, curve) in new[] { ("linear", PressureCurve.Linear()), ("s-curve", PressureCurve.FromPoints(PressureCurve.Presets[3].Points)),
                        ("custom", PressureCurve.FromPoints(P(0, .1f, .2f, .5f, .6f, .55f, 1, .9f))), ("older", AnimationCurve.EaseInOut(0, 0, 1, 1)) })
                        foreach (int width in new[] { 211, 300 })
                        {
                            w.Brush.pressureCurve = curve;
                            string name = language + "-" + shape + "-" + width;
                            PaintGui.ShortenedTexts = 0;
                            // マウスはグラフの中（入力 → 出力の表示を出す）
                            OffscreenGui.RenderToPng(width, 130, () => rows.Invoke(w, new object[] { new UiRows(new Rect(0, 0, width, 130)) }), Path.Combine(Folder, name + ".png"), PaintTheme.PanelBg,
                                new Vector2(width * .45f, 80));
                            Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": a label did not fit");
                            Assert.That(ReferenceEquals(w.Brush.pressureCurve, curve), Is.True, name + ": drawing does not change the curve");
                            var texture = new Texture2D(2, 2);
                            try
                            {
                                Assert.That(texture.LoadImage(File.ReadAllBytes(Path.Combine(Folder, name + ".png"))), Is.True);
                                // 曲線の色（アクセントの青）の画素がグラフに描かれている
                                int blue = texture.GetPixels32().Count(c => c.b > 200 && c.r < 110 && c.g > 110 && c.g < 170);
                                Assert.That(blue, Is.GreaterThan(width / 2), name + ": the curve was not drawn");
                            }
                            finally { Object.DestroyImmediate(texture); }
                        }
                }
            }
            finally { Object.DestroyImmediate(w); L.OverrideLanguage(PainterLanguage.English); }
        }
    }
}
