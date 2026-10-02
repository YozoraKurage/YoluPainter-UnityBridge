using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 筆圧のカーブの点（<see cref="PaintGui.CurveEditor"/> が編集する形）。点を通る単調な 3 次エルミート（Fritsch–Carlson の制限を
    /// 付けた PCHIP）で結び、AnimationCurve のキーと接線にそのまま入れる（Unity の AnimationCurve は重みの無いキーの間を、
    /// 接線を傾きとする 3 次エルミートで結ぶので、Evaluate がこの曲線になる）。点が単調なら曲線も単調で、どの区間も両端の点の
    /// 値の間に収まる（点が 0〜1 なら曲線も 0〜1）。両端の点は入力 0 と 1 に固定（縦にだけ動く）。
    /// 保存の形は AnimationCurve のまま（brush.json・ブラシのプリセット・.ylp の brush.json）。この部品で作っていないカーブ
    /// （以前の版で Unity のカーブエディタで編集したもの）は、編集するまでそのまま評価し、編集を始めたときに 0, ¼, ½, ¾, 1 の
    /// 値を点にして作り直す。
    /// </summary>
    internal static class PressureCurve
    {
        /// <summary>点の数の上限と、隣の点との入力の間隔の最小。</summary>
        public const int MaxPoints = 16;
        public const float MinGap = .02f;

        public static AnimationCurve Linear() => FromPoints(new[] { new Vector2(0, 0), new Vector2(1, 1) });

        /// <summary>カーブの編集できる点。この部品で作ったカーブならキーそのもの、そうでなければ 5 点で写したもの。</summary>
        public static List<Vector2> Points(AnimationCurve curve)
        {
            var keys = curve?.keys;
            if (keys == null || keys.Length == 0) return new List<Vector2> { new Vector2(0, 0), new Vector2(1, 1) };
            if (IsEditable(curve)) { var own = new List<Vector2>(); foreach (var k in keys) own.Add(new Vector2(k.time, k.value)); return own; }
            var sampled = new List<Vector2>();
            for (int i = 0; i <= 4; i++) { float t = i / 4f; sampled.Add(new Vector2(t, Mathf.Clamp01(curve.Evaluate(t)))); }
            return sampled;
        }

        /// <summary>この部品の形のカーブか: 2〜<see cref="MaxPoints"/> 個のキーが 0〜1 の中で、入力 0 と 1 を両端に、間隔
        /// <see cref="MinGap"/> 以上で並び、接線が <see cref="FromPoints"/> の接線と同じ。</summary>
        public static bool IsEditable(AnimationCurve curve)
        {
            var keys = curve?.keys;
            if (keys == null || keys.Length < 2 || keys.Length > MaxPoints) return false;
            if (keys[0].time != 0 || keys[keys.Length - 1].time != 1) return false;
            var points = new List<Vector2>();
            for (int i = 0; i < keys.Length; i++)
            {
                var k = keys[i];
                if (k.value < 0 || k.value > 1 || k.weightedMode != WeightedMode.None) return false;
                if (i > 0 && k.time - keys[i - 1].time < MinGap - 1e-6f) return false;
                points.Add(new Vector2(k.time, k.value));
            }
            return SameTangents(keys, Tangents(points));
        }

        /// <summary>キーの接線が tangents と同じか。0〜1 の外へ向かう接線（最初のキーの入り・最後のキーの出）は評価に効かないので
        /// 見ない（AnimationCurve.Linear はそこを 0 にする）。</summary>
        static bool SameTangents(Keyframe[] keys, float[] tangents)
        {
            for (int i = 0; i < keys.Length; i++)
                if (i > 0 && Mathf.Abs(keys[i].inTangent - tangents[i]) > 1e-4f || i < keys.Length - 1 && Mathf.Abs(keys[i].outTangent - tangents[i]) > 1e-4f) return false;
            return true;
        }

        /// <summary>点（入力の順、両端は 0 と 1）を通る単調な 3 次エルミートのカーブ。</summary>
        public static AnimationCurve FromPoints(IList<Vector2> points)
        {
            if (points == null || points.Count < 2) throw new ArgumentException("A pressure curve needs at least two points.", nameof(points));
            var tangents = Tangents(points);
            var keys = new Keyframe[points.Count];
            for (int i = 0; i < keys.Length; i++) keys[i] = new Keyframe(points[i].x, points[i].y, tangents[i], tangents[i]);
            return new AnimationCurve(keys);
        }

        /// <summary>各点の傾き（PCHIP: 内側は隣の 2 区間の傾きの重み付き調和平均で、向きが変わる点では 0。端は 3 点の式で、向きが
        /// 逆なら 0、行き過ぎるなら区間の傾きの 3 倍まで）。これで各区間が単調になり、両端の点の値の間に収まる。</summary>
        public static float[] Tangents(IList<Vector2> p)
        {
            int n = p.Count; var m = new float[n];
            var h = new double[n - 1]; var d = new double[n - 1];
            for (int k = 0; k < n - 1; k++) { h[k] = Math.Max(1e-6, p[k + 1].x - p[k].x); d[k] = (p[k + 1].y - p[k].y) / h[k]; }
            if (n == 2) { m[0] = m[1] = (float)d[0]; return m; }
            for (int k = 1; k < n - 1; k++)
            {
                if (d[k - 1] * d[k] <= 0) { m[k] = 0; continue; }
                double w1 = 2 * h[k] + h[k - 1], w2 = h[k] + 2 * h[k - 1];
                m[k] = (float)((w1 + w2) / (w1 / d[k - 1] + w2 / d[k]));
            }
            m[0] = (float)Edge(h[0], h[1], d[0], d[1]);
            m[n - 1] = (float)Edge(h[n - 2], h[n - 3], d[n - 2], d[n - 3]);
            return m;
        }
        static double Edge(double h0, double h1, double d0, double d1)
        {
            double m = ((2 * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
            if (Math.Sign(m) != Math.Sign(d0)) return 0;
            if (Math.Sign(d0) != Math.Sign(d1) && Math.Abs(m) > Math.Abs(3 * d0)) return 3 * d0;
            return m;
        }

        /// <summary>点を足す（入力の順の位置に）。足した位置、足せなければ（上限・隣に近すぎる・端）-1。</summary>
        public static int Insert(List<Vector2> points, Vector2 at)
        {
            if (points.Count >= MaxPoints) return -1;
            float x = at.x;
            int i = 1;
            while (i < points.Count && points[i].x < x) i++;
            if (i >= points.Count || x - points[i - 1].x < MinGap || points[i].x - x < MinGap) return -1;
            points.Insert(i, new Vector2(x, Mathf.Clamp01(at.y)));
            return i;
        }

        /// <summary>点を動かす。縦は 0〜1、横は隣の点の間（<see cref="MinGap"/> を空ける）。両端の点は横に動かない。</summary>
        public static void Move(List<Vector2> points, int index, Vector2 to)
        {
            int last = points.Count - 1;
            float x = index == 0 ? 0 : index == last ? 1 : Mathf.Clamp(to.x, points[index - 1].x + MinGap, points[index + 1].x - MinGap);
            points[index] = new Vector2(x, Mathf.Clamp01(to.y));
        }

        /// <summary>点を消す（両端は消せない）。</summary>
        public static bool Remove(List<Vector2> points, int index)
        {
            if (index <= 0 || index >= points.Count - 1) return false;
            points.RemoveAt(index); return true;
        }

        /// <summary>カーブが points のカーブと同じか（キーと接線を比べる）。</summary>
        public static bool Matches(AnimationCurve curve, IList<Vector2> points)
        {
            var keys = curve?.keys; if (keys == null || keys.Length != points.Count) return false;
            for (int i = 0; i < keys.Length; i++)
                if (Mathf.Abs(keys[i].time - points[i].x) > 1e-5f || Mathf.Abs(keys[i].value - points[i].y) > 1e-5f) return false;
            return SameTangents(keys, Tangents(points));
        }

        /// <summary>よく使う形（名前は訳さない英語。描く側が訳す）。</summary>
        public static readonly (string Name, Vector2[] Points)[] Presets =
        {
            ("Linear", new[] { new Vector2(0, 0), new Vector2(1, 1) }),
            ("Soft", new[] { new Vector2(0, 0), new Vector2(.3f, .55f), new Vector2(1, 1) }),
            ("Hard", new[] { new Vector2(0, 0), new Vector2(.55f, .3f), new Vector2(1, 1) }),
            ("S-curve", new[] { new Vector2(0, 0), new Vector2(.3f, .15f), new Vector2(.7f, .85f), new Vector2(1, 1) }),
        };
    }

    internal static partial class PaintGui
    {
        // 編集中のカーブ（ドラッグの間だけ）: 部品の ID、点、動かしている点、元のカーブ（Esc で戻す）、外へ出したか（離すと消す）
        static int s_curveId, s_curveIndex = -1; static List<Vector2> s_curvePoints; static AnimationCurve s_curveOriginal; static bool s_curveOutside, s_curveInserted;
        /// <summary>点に届く距離（GUI の点）と、外へドラッグして消すときの枠からの距離。</summary>
        const float CurveGrab = 7, CurveRemoveMargin = 16;

        /// <summary>
        /// 筆圧のカーブを直接編集する小さなグラフ（CLIP STUDIO の筆圧の設定や Photoshop・Krita の筆圧のカーブのもの）。横が入力
        /// （ペンの筆圧）、縦が出力（ブラシが使う筆圧）。何も無いところを押すと点を足してそのまま動かせ、点をドラッグで動かし、
        /// 右クリックか、枠の外へドラッグして離すと消す（両端の点は横に動かず、消せない）。ドラッグ中の Esc で元に戻す。カーソルの
        /// 位置の入力→出力を % で出す。GUI が止められているあいだ（ストローク中など）は動かさない。変えたら新しいカーブを返す
        /// （GUI.changed も立てる）。カーブの形は <see cref="PressureCurve"/>。
        /// </summary>
        public static AnimationCurve CurveEditor(Rect r, AnimationCurve curve, string tooltip = null, bool enabled = true)
        {
            int id = GUIUtility.GetControlID("YoluPainterCurve".GetHashCode(), FocusType.Passive, r);
            enabled &= GUI.enabled;
            var graph = new Rect(r.x + 6, r.y + 6, r.width - 12, r.height - 12);
            bool dragging = GUIUtility.hotControl == id && s_curveId == id && s_curvePoints != null;
            if (!dragging && s_curveId == id && GUIUtility.hotControl != id) { s_curveId = 0; s_curvePoints = null; } // ほかで終わったドラッグの後始末
            var points = dragging ? s_curvePoints : PressureCurve.Points(curve);
            Vector2 mouse = E.mousePosition;
            int hover = enabled && !dragging ? Hit(points, graph, mouse) : -1;
            if (enabled)
                switch (E.GetTypeForControl(id))
                {
                    case EventType.MouseDown:
                        if (!r.Contains(mouse)) break;
                        if (E.button == 1)
                        {
                            if (hover > 0 && hover < points.Count - 1) { var list = new List<Vector2>(points); PressureCurve.Remove(list, hover); curve = Changed(list); }
                            E.Use(); break;
                        }
                        if (E.button != 0) break;
                        if (E.clickCount == 2 && hover > 0 && hover < points.Count - 1 && !s_curveInserted)
                        { var list = new List<Vector2>(points); PressureCurve.Remove(list, hover); curve = Changed(list); E.Use(); break; }
                        {
                            var list = new List<Vector2>(points); int index = hover; s_curveInserted = false;
                            if (index < 0) { index = PressureCurve.Insert(list, ToCurve(graph, mouse)); s_curveInserted = index >= 0; }
                            if (index >= 0)
                            {
                                s_curveOriginal = curve; s_curveId = id; s_curvePoints = list; s_curveIndex = index; s_curveOutside = false;
                                GUIUtility.hotControl = id; GUIUtility.keyboardControl = 0;
                                if (s_curveInserted) curve = Changed(list);
                            }
                            E.Use();
                        }
                        break;
                    case EventType.ContextClick:
                        if (r.Contains(mouse)) E.Use(); // 右クリックは点を消すのに使ったので、ほかのメニューを開かない
                        break;
                    case EventType.MouseDrag:
                        if (!dragging) break;
                        {
                            bool middle = s_curveIndex > 0 && s_curveIndex < s_curvePoints.Count - 1;
                            var outer = new Rect(r.x - CurveRemoveMargin, r.y - CurveRemoveMargin, r.width + 2 * CurveRemoveMargin, r.height + 2 * CurveRemoveMargin);
                            s_curveOutside = middle && !outer.Contains(mouse);
                            if (!s_curveOutside) PressureCurve.Move(s_curvePoints, s_curveIndex, ToCurve(graph, mouse));
                            curve = Changed(Shown(s_curvePoints));
                            E.Use();
                        }
                        break;
                    case EventType.MouseUp:
                        if (!dragging) break;
                        if (s_curveOutside) { PressureCurve.Remove(s_curvePoints, s_curveIndex); curve = Changed(s_curvePoints); }
                        GUIUtility.hotControl = 0; s_curveId = 0; s_curvePoints = null; s_curveIndex = -1; s_curveOutside = false;
                        E.Use();
                        break;
                }
            if (dragging && E.type == EventType.KeyDown && E.keyCode == KeyCode.Escape)
            {
                curve = s_curveOriginal; GUI.changed = true; dragging = false;
                GUIUtility.hotControl = 0; s_curveId = 0; s_curvePoints = null; s_curveIndex = -1; s_curveOutside = false;
                E.Use();
            }
            if (Repainting) DrawCurve(r, graph, curve, dragging ? Shown(s_curvePoints) : points, dragging ? s_curveIndex : hover, dragging && s_curveOutside, enabled, mouse);
            Tooltip(r, tooltip);
            return curve;
        }

        /// <summary>ドラッグで外へ出した点を除いた点。</summary>
        static List<Vector2> Shown(List<Vector2> points)
        {
            if (!s_curveOutside) return points;
            var list = new List<Vector2>(points); list.RemoveAt(s_curveIndex); return list;
        }
        static AnimationCurve Changed(IList<Vector2> points) { GUI.changed = true; return PressureCurve.FromPoints(points); }
        static Vector2 ToCurve(Rect graph, Vector2 p) => new Vector2(Mathf.Clamp01((p.x - graph.x) / Mathf.Max(1, graph.width)), Mathf.Clamp01((graph.yMax - p.y) / Mathf.Max(1, graph.height)));
        static Vector2 ToGraph(Rect graph, Vector2 c) => new Vector2(graph.x + c.x * graph.width, graph.yMax - c.y * graph.height);
        static int Hit(List<Vector2> points, Rect graph, Vector2 mouse)
        {
            int best = -1; float bestDistance = CurveGrab;
            for (int i = 0; i < points.Count; i++) { float d = Vector2.Distance(ToGraph(graph, points[i]), mouse); if (d <= bestDistance) { best = i; bestDistance = d; } }
            return best;
        }

        static void DrawCurve(Rect r, Rect graph, AnimationCurve curve, List<Vector2> points, int active, bool removing, bool enabled, Vector2 mouse)
        {
            bool inside = enabled && r.Contains(mouse);
            Rounded(r, PaintTheme.ControlBg, 3);
            var grid = new Color(1, 1, 1, .06f);
            for (int i = 1; i < 4; i++)
            {
                VLine(Mathf.Round(graph.x + graph.width * i / 4), graph.y, graph.yMax, grid);
                HLine(graph.x, graph.xMax, Mathf.Round(graph.y + graph.height * i / 4), grid);
            }
            // 線形の目安（点線の対角）
            var guide = new Color(1, 1, 1, .12f);
            for (float x = 0; x < graph.width; x += 4) Fill(new Rect(graph.x + x, graph.yMax - (x + 1) / graph.width * graph.height - .5f, 1.5f, 1.5f), guide);
            // 入力の位置の目安（カーソルの縦線）
            float probe = -1;
            if (active >= 0 && active < points.Count) probe = points[active].x;
            else if (inside && graph.Contains(mouse)) probe = ToCurve(graph, mouse).x;
            if (probe >= 0) VLine(Mathf.Round(graph.x + probe * graph.width), graph.y, graph.yMax, new Color(1, 1, 1, .14f));
            // 曲線: 列ごとに前の列の高さから今の列の高さまでを細い縦の帯で塗る（GUI の切り抜きが効く描き方で、つながった線になる）
            var line = enabled ? PaintTheme.Accent : PaintTheme.TextDisabled;
            int columns = Mathf.Max(2, Mathf.RoundToInt(graph.width));
            float previous = float.NaN;
            for (int i = 0; i <= columns; i++)
            {
                float t = i / (float)columns, y = graph.yMax - Mathf.Clamp01(curve != null ? curve.Evaluate(t) : t) * graph.height;
                float top = float.IsNaN(previous) ? y : Mathf.Min(previous, y), bottom = float.IsNaN(previous) ? y : Mathf.Max(previous, y);
                Fill(new Rect(graph.x + t * graph.width - .75f, top - .75f, 1.5f, bottom - top + 1.5f), line);
                previous = y;
            }
            // 点（動かしている点・マウスの乗った点は大きく。外へ出して消す点は赤の枠だけ）
            for (int i = 0; i < points.Count; i++)
            {
                var at = ToGraph(graph, points[i]); bool hot = i == active && !removing; float size = hot ? 9 : 7;
                var box = new Rect(Mathf.Round(at.x - size / 2), Mathf.Round(at.y - size / 2), size, size);
                Rounded(box, !enabled ? PaintTheme.TextDisabled : hot ? Color.white : PaintTheme.Text, 2);
                Outline(box, hot ? PaintTheme.Accent : PaintTheme.Border, 1, 2);
            }
            if (removing && s_curvePoints != null && s_curveIndex >= 0 && s_curveIndex < s_curvePoints.Count)
            {
                var at = ToGraph(graph, s_curvePoints[s_curveIndex]);
                Outline(new Rect(Mathf.Round(at.x - 4.5f), Mathf.Round(at.y - 4.5f), 9, 9), PaintTheme.Error, 1, 2);
            }
            // 入力 → 出力（%）。曲線の通らない隅に出す
            if (probe >= 0 && !removing)
            {
                float output = active >= 0 && active < points.Count ? points[active].y : Mathf.Clamp01(curve != null ? curve.Evaluate(probe) : probe);
                string text = Mathf.RoundToInt(probe * 100) + "% → " + Mathf.RoundToInt(output * 100) + "%";
                float w = TextWidth(text, PaintTheme.LabelSmall) + 8;
                bool upperLeftBusy = (curve != null ? curve.Evaluate(.2f) : .2f) > .55f;
                var label = upperLeftBusy ? new Rect(graph.xMax - w, graph.yMax - 15, w, 14) : new Rect(graph.x, graph.y, w, 14);
                Rounded(label, new Color(PaintTheme.ControlBg.r, PaintTheme.ControlBg.g, PaintTheme.ControlBg.b, .85f), 2);
                Text(new Rect(label.x + 4, label.y, label.width - 4, label.height), text, PaintTheme.LabelSmall, PaintTheme.Text);
            }
            Outline(r, inside || active >= 0 ? PaintTheme.AccentDim : PaintTheme.Border, 1, 3);
        }
    }
}
