using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// プロパティの欄のツールの設定に使う部品（<see cref="PaintGui"/> の続き）: 小見出し、収まらない文字を … で詰める版の
    /// スライダー・チェック・ボタン・ドロップダウン、数値の欄（ドラッグで増減・クリックで入力）、値の表示、折り返す説明文。
    /// 筆圧のカーブの欄は PaintGui.Curve.cs（<see cref="CurveEditor"/>）。
    /// 文字を詰めたら <see cref="ShortenedTexts"/> を数える（テストが、英語でも日本語でも最小のウィンドウで 0 であることを確かめる）。
    /// </summary>
    internal static partial class PaintGui
    {
        /// <summary>UI の文字が欄に収まらず、末尾を … にした回数（ツールチップに全文を出す）。利用者のデータ（ブラシの名前など）は数えない。</summary>
        internal static int ShortenedTexts;
        /// <summary>最後に詰めた UI の文字（新しいものから最大 8 つ。テストの失敗の知らせに、どの文字が収まらなかったかを出す）。</summary>
        internal static readonly LinkedList<string> LastShortenedTexts = new LinkedList<string>();
        internal static string ShortenedTextsSummary => string.Join(" | ", LastShortenedTexts);

        /// <summary>width に収まる文字。収まらなければ末尾を … にする（count なら <see cref="ShortenedTexts"/> を数える）。</summary>
        public static string Fit(string text, float width, GUIStyle style, bool count = true)
        {
            if (string.IsNullOrEmpty(text) || TextWidth(text, style) <= width) return text;
            if (count)
            {
                ShortenedTexts++;
                LastShortenedTexts.AddFirst(text + " (" + Mathf.RoundToInt(width) + " px)");
                while (LastShortenedTexts.Count > 8) LastShortenedTexts.RemoveLast();
            }
            int lo = 0, hi = text.Length - 1;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (TextWidth(text.Substring(0, mid) + "…", style) <= width) lo = mid; else hi = mid - 1; }
            return text.Substring(0, lo).TrimEnd() + "…";
        }
        public static float TextWidth(string text, GUIStyle style) => string.IsNullOrEmpty(text) ? 0 : style.CalcSize(new GUIContent(text)).x;
        static string Joined(string full, string tooltip) => string.IsNullOrEmpty(tooltip) ? full : full + "\n" + tooltip;

        /// <summary>セクションの中の小見出し（小さな文字と、右へ伸びる細い線）。</summary>
        public static void GroupLabel(Rect r, string text, string tooltip = null)
        {
            string shown = Fit(text, r.width, PaintTheme.LabelSmall);
            Text(r, shown, PaintTheme.LabelSmall);
            float x = r.x + TextWidth(shown, PaintTheme.LabelSmall) + 6;
            if (x < r.xMax) HLine(x, r.xMax, Mathf.Round(r.center.y), PaintTheme.Separator);
            Tooltip(r, shown != text ? Joined(text, tooltip) : tooltip);
        }

        /// <summary><see cref="Slider"/> の、名前が値に重なるときは名前を … で詰める版（値の幅は min・max・今の値の広いほうで見るので、
        /// 動かしても詰め方が揺れない）。</summary>
        public static float FitSlider(Rect r, string label, float value, float min, float max, string format = "0.##", string suffix = "", string tooltip = null, bool enabled = true, float trackInset = 0)
        {
            float valueWidth = Mathf.Max(ValueWidth(min, format, suffix), ValueWidth(max, format, suffix), ValueWidth(value, format, suffix));
            string shown = Fit(label, r.width - 14 - valueWidth - 8, PaintTheme.Label);
            return Slider(r, shown, value, min, max, format, suffix, shown != label ? Joined(label, tooltip) : tooltip, enabled, trackInset);
        }
        public static int FitIntSlider(Rect r, string label, int value, int min, int max, string suffix = "", string tooltip = null, bool enabled = true)
            => Mathf.RoundToInt(FitSlider(r, label, value, min, max, "0", suffix, tooltip, enabled));
        static float ValueWidth(float v, string format, string suffix) => TextWidth(v.ToString(format, CultureInfo.InvariantCulture) + suffix, PaintTheme.Value);

        /// <summary><see cref="Toggle"/> の、名前を … で詰める版。</summary>
        public static bool FitToggle(Rect r, string label, bool value, string tooltip = null, bool enabled = true)
        {
            string shown = Fit(label, r.width - 23, PaintTheme.Label);
            return Toggle(r, shown, value, shown != label ? Joined(label, tooltip) : tooltip, enabled);
        }

        /// <summary><see cref="Button"/> の、文字を … で詰める版。</summary>
        public static bool FitButton(Rect r, string text, bool primary = false, bool enabled = true, string tooltip = null)
        {
            string shown = Fit(text, r.width - 10, PaintTheme.LabelCenter);
            return Button(r, shown, primary, enabled, shown != text ? Joined(text, tooltip) : tooltip);
        }

        /// <summary><see cref="Dropdown"/> の、名前と値を … で詰める版。名前の幅は labelWidth（0 なら文字の幅）。値が利用者のデータ
        /// （ブラシの名前など）なら valueIsData（詰めても数えない）。</summary>
        public static void FitDropdown(Rect r, string label, string value, Action<Rect> open, string tooltip = null, bool enabled = true, float labelWidth = 0, bool valueIsData = false)
        {
            float lw = 0; string shownLabel = label;
            if (!string.IsNullOrEmpty(label))
            {
                lw = labelWidth > 0 ? labelWidth : TextWidth(label, PaintTheme.Label) + 10;
                shownLabel = Fit(label, lw - 6, PaintTheme.Label);
            }
            string shownValue = Fit(value, r.width - lw - 28, PaintTheme.Label, !valueIsData);
            bool shortened = shownLabel != label || shownValue != value;
            Dropdown(r, shownLabel, shownValue, open, shortened ? Joined((string.IsNullOrEmpty(label) ? "" : label + ": ") + value, tooltip) : tooltip, enabled, lw);
        }

        /// <summary>読むだけの値（名前は左、値は平らな箱の中）。値が利用者のデータなら valueIsData。</summary>
        public static void ValueBox(Rect r, string label, string value, float labelWidth, Color? valueColor = null, string tooltip = null, bool valueIsData = false)
        {
            string shownLabel = Fit(label, labelWidth - 6, PaintTheme.Label);
            Text(new Rect(r.x, r.y, labelWidth, r.height), shownLabel, PaintTheme.Label);
            var box = new Rect(r.x + labelWidth, r.y, r.width - labelWidth, r.height);
            Rounded(box, new Color(PaintTheme.ControlBg.r, PaintTheme.ControlBg.g, PaintTheme.ControlBg.b, .55f), 3);
            string shownValue = Fit(value, box.width - 14, PaintTheme.Label, !valueIsData);
            Text(new Rect(box.x + 7, box.y, box.width - 14, box.height), shownValue, PaintTheme.Label, valueColor ?? PaintTheme.TextDim);
            Tooltip(r, shownLabel != label || shownValue != value ? Joined(label + ": " + value, tooltip) : tooltip);
        }

        // ───────── 数値の欄 ─────────
        static float s_scrubStartX, s_scrubStartValue; static bool s_scrubbed;

        /// <summary>
        /// 数値の欄（Photoshop の変形の X・Y・W・H のもの）。左右にドラッグすると 1 px ごとに step 増減し（Shift で 10 倍）、動かさずに
        /// 離すと数値を打てる（Enter で決める、Esc でやめる）。値は min..max に収める（既定は制限なし）。スライダーと違って範囲の
        /// 決まらない値（ずらす量・倍率・回す角度・ダブの数）に使う。
        /// </summary>
        public static float NumberField(Rect r, string label, float value, string format = "0.##", string suffix = "", float step = 1, float min = float.MinValue, float max = float.MaxValue, string tooltip = null, bool enabled = true)
        {
            int id = GUIUtility.GetControlID(FocusType.Keyboard, r);
            if (s_editingId == id) return EditNumber(r, id, value, min, max);
            enabled &= GUI.enabled;
            bool hover = enabled && Hover(r);
            if (enabled)
                switch (E.GetTypeForControl(id))
                {
                    case EventType.MouseDown:
                        if (E.button == 0 && r.Contains(E.mousePosition))
                        { GUIUtility.hotControl = id; GUIUtility.keyboardControl = 0; s_scrubStartX = E.mousePosition.x; s_scrubStartValue = value; s_scrubbed = false; E.Use(); }
                        break;
                    case EventType.MouseDrag:
                        if (GUIUtility.hotControl == id)
                        {
                            float dx = E.mousePosition.x - s_scrubStartX;
                            if (s_scrubbed || Mathf.Abs(dx) >= 3)
                            {
                                s_scrubbed = true;
                                float next = Mathf.Clamp(s_scrubStartValue + Mathf.Round(dx) * step * (E.shift ? 10 : 1), min, max);
                                if (next != value) { value = next; GUI.changed = true; }
                            }
                            E.Use();
                        }
                        break;
                    case EventType.MouseUp:
                        if (GUIUtility.hotControl == id)
                        {
                            GUIUtility.hotControl = 0; E.Use();
                            if (!s_scrubbed && r.Contains(E.mousePosition)) { s_editingId = id; s_editingText = value.ToString(format, CultureInfo.InvariantCulture); s_editFocus = true; }
                        }
                        break;
                }
            string valueText = value.ToString(format, CultureInfo.InvariantCulture) + suffix;
            float valueWidth = TextWidth(valueText, PaintTheme.Value);
            string shown = Fit(label, r.width - 14 - valueWidth - 8, PaintTheme.Label);
            if (Repainting)
            {
                bool active = GUIUtility.hotControl == id;
                Rounded(r, active ? PaintTheme.ControlActive : hover ? PaintTheme.ControlHover : PaintTheme.ControlBg, 3);
                Outline(r, hover || active ? PaintTheme.AccentDim : PaintTheme.Border, 1, 3);
                var inner = new Rect(r.x + 7, r.y, r.width - 14, r.height);
                if (!string.IsNullOrEmpty(shown)) Text(inner, shown, PaintTheme.Label, enabled ? PaintTheme.TextDim : PaintTheme.TextDisabled);
                Text(inner, valueText, PaintTheme.Value, enabled ? PaintTheme.Text : PaintTheme.TextDisabled);
            }
            Tooltip(r, shown != label ? Joined(label, tooltip) : tooltip);
            return value;
        }
        public static int IntField(Rect r, string label, int value, int min, int max, string suffix = "", string tooltip = null, bool enabled = true)
            => Mathf.RoundToInt(NumberField(r, label, value, "0", suffix, 1, min, max, tooltip, enabled));

        // ───────── 説明文 ─────────

        /// <summary>折り返す説明文を、要る高さの行に描く。</summary>
        public static void Paragraph(UiRows rows, string text, Color? color = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (Repainting) TextDrawn?.Invoke(text);
            var lines = WrapLines(text, rows.Width, PaintTheme.Wrap);
            float lh = LineHeight(PaintTheme.Wrap);
            DrawLines(rows.Row(lines.Length * lh, 6), lines, lh, color);
        }

        /// <summary>アイコン付きの知らせ（注意・情報）。文字は折り返す。</summary>
        public static void Notice(UiRows rows, string text, string icon, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (Repainting) TextDrawn?.Invoke(text);
            var lines = WrapLines(text, rows.Width - 22, PaintTheme.Wrap);
            float lh = LineHeight(PaintTheme.Wrap);
            var r = rows.Row(Mathf.Max(16, lines.Length * lh), 6);
            Icon(new Rect(r.x, r.y, 16, Mathf.Min(16, r.height)), icon, color, 14);
            DrawLines(new Rect(r.x + 22, r.y, r.width - 22, r.height), lines, lh, color);
        }

        static void DrawLines(Rect r, string[] lines, float lineHeight, Color? color)
        {
            if (!Repainting) return;
            var style = PaintTheme.Wrap; bool wrap = style.wordWrap; style.wordWrap = false;
            try { for (int i = 0; i < lines.Length; i++) Text(new Rect(r.x, r.y + i * lineHeight, r.width + 4, lineHeight), lines[i], style, color); }
            finally { style.wordWrap = wrap; }
        }

        static float LineHeight(GUIStyle style) => Mathf.Ceil(Mathf.Max(style.lineHeight, style.CalcSize(new GUIContent("Agあ")).y));

        static readonly Dictionary<(string, int, int), string[]> s_wrapped = new Dictionary<(string, int, int), string[]>();
        // 行頭に来てはいけない文字（閉じ括弧・句読点・小書きのかな・長音）と、行末に来てはいけない文字（開き括弧）
        const string NoLineStart = "、。，．）」』】〕〉》！？：；・ー…ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ)]},.!?:;%";
        const string NoLineEnd = "（「『【〔〈《([{";
        static bool IsWide(char c) => c >= 0x3000 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF || c >= 0xFF00 && c <= 0xFFEF;

        /// <summary>
        /// width に収まるよう折り返した行。英語は空白で折り返し、日本語（かな・漢字・全角の記号）は文字の間でも折り返す（IMGUI の
        /// wordWrap は空白でしか折らないので、日本語の文が空白の前でまとめて次の行へ送られて右が大きく空く）。閉じ括弧・句読点・
        /// 小書きのかなは行頭に、開き括弧は行末に来ないよう前後の文字に付ける。改行の無い空白（U+00A0。例: "10 px"）では折らない。
        /// </summary>
        public static string[] WrapLines(string text, float width, GUIStyle style)
        {
            var key = (text, Mathf.RoundToInt(width), style.fontSize);
            if (s_wrapped.TryGetValue(key, out var cached)) return cached;
            var lines = new List<string>(); bool wrap = style.wordWrap; style.wordWrap = false; // 1 行の幅を測る
            try
            {
                foreach (var paragraph in text.Split('\n'))
                {
                    string line = "";
                    foreach (var (unit, spaced) in WrapUnits(paragraph))
                    {
                        string candidate = line.Length == 0 ? unit : line + (spaced ? " " : "") + unit;
                        if (line.Length > 0 && TextWidth(candidate.Replace('\u00A0', ' '), style) > width) { lines.Add(line); line = unit; }
                        else line = candidate;
                    }
                    lines.Add(line);
                }
            }
            finally { style.wordWrap = wrap; }
            var result = lines.Select(l => l.Replace('\u00A0', ' ')).ToArray();
            if (s_wrapped.Count > 512) s_wrapped.Clear();
            s_wrapped[key] = result;
            return result;
        }

        /// <summary>折り返してよい位置で区切った単位（と、その前に空白があったか）。</summary>
        static List<(string unit, bool spaced)> WrapUnits(string text)
        {
            var units = new List<(string, bool)>(); var current = new StringBuilder(); bool spaced = false, glue = false;
            void Flush() { if (current.Length > 0) { units.Add((current.ToString(), spaced)); current.Clear(); spaced = false; } }
            foreach (char c in text)
            {
                if (c == ' ') { Flush(); spaced = units.Count > 0; glue = false; continue; }
                bool wide = IsWide(c);
                if (NoLineStart.IndexOf(c) >= 0 && !spaced)
                {
                    // 前の単位に付ける（今の単位が空なら前の単位の末尾へ）
                    if (current.Length > 0) current.Append(c);
                    else if (units.Count > 0) { var last = units[units.Count - 1]; units[units.Count - 1] = (last.Item1 + c, last.Item2); }
                    else current.Append(c);
                    continue;
                }
                if (wide && !glue) Flush();
                else if (!wide && current.Length > 0 && IsWide(current[current.Length - 1]) && !glue) Flush();
                current.Append(c);
                glue = NoLineEnd.IndexOf(c) >= 0;
                if (wide && !glue) Flush();
            }
            Flush();
            return units;
        }
    }
}
