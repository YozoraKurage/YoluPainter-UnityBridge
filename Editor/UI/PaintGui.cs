using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// ペイントソフトの部品（IMGUI、絶対座標）。背景・枠・塗りは自前で描き、Unity の標準のボタンやスライダーの見た目は使わない。
    /// 文字はここでは訳さない（呼ぶ側が <see cref="L.Tr(string)"/> で訳したものを渡す）。マウスの乗った見た目には、ウィンドウの
    /// wantsMouseMove と MouseMove での Repaint が要る。
    /// </summary>
    internal static partial class PaintGui
    {
        static Event E => Event.current;
        static bool Repainting => E.type == EventType.Repaint;
        static bool Hover(Rect r) => r.Contains(E.mousePosition) && GUI.enabled;

        public static void Fill(Rect r, Color c) { if (Repainting) EditorGUI.DrawRect(r, c); }
        public static void Rounded(Rect r, Color c, float radius = 4) { if (Repainting) GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, c, 0, radius); }
        public static void Outline(Rect r, Color c, float width = 1, float radius = 4) { if (Repainting) GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, c, width, radius); }
        public static void HLine(float x0, float x1, float y, Color c) => Fill(new Rect(x0, y, x1 - x0, 1), c);
        public static void VLine(float x, float y0, float y1, Color c) => Fill(new Rect(x, y0, 1, y1 - y0), c);

        /// <summary>アイコンを rect の中央に size の大きさで、色を付けて描く。無いアイコンは名前の頭文字。</summary>
        public static void Icon(Rect r, string name, Color color, float size = 20)
        {
            if (!Repainting) return;
            var icon = PaintIcons.Get(name);
            var at = new Rect(r.center.x - size / 2, r.center.y - size / 2, size, size);
            if (icon != null) GUI.DrawTexture(at, icon, ScaleMode.ScaleToFit, true, 0, color, 0, 0);
            else { var s = new GUIStyle(PaintTheme.LabelCenter); s.normal.textColor = color; GUI.Label(at, string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant(), s); }
        }

        public static void Text(Rect r, string text, GUIStyle style = null, Color? color = null)
        {
            if (!Repainting || string.IsNullOrEmpty(text)) return;
            style = style ?? PaintTheme.Label;
            if (color.HasValue) { var c = style.normal.textColor; style.normal.textColor = color.Value; GUI.Label(r, text, style); style.normal.textColor = c; }
            else GUI.Label(r, text, style);
        }

        /// <summary>ツールチップだけを rect に付ける（見た目は描かない）。</summary>
        public static void Tooltip(Rect r, string tooltip) { if (!string.IsNullOrEmpty(tooltip)) GUI.Label(r, new GUIContent("", tooltip), GUIStyle.none); }

        /// <summary>押されたら true（離したとき、rect の中なら）。</summary>
        static bool Clickable(Rect r, out bool pressed, out bool hover, bool enabled = true)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive, r);
            enabled &= GUI.enabled;
            hover = enabled && Hover(r); pressed = GUIUtility.hotControl == id;
            if (!enabled) return false;
            switch (E.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (E.button == 0 && r.Contains(E.mousePosition)) { GUIUtility.hotControl = id; E.Use(); pressed = true; }
                    break;
                case EventType.MouseDrag: if (GUIUtility.hotControl == id) E.Use(); break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; E.Use(); return r.Contains(E.mousePosition); }
                    break;
            }
            return false;
        }

        /// <summary>アイコンだけのボタン（ツール・パネルの操作）。selected は押し込まれた見た目（選んでいるツール）。</summary>
        public static bool IconButton(Rect r, string icon, string tooltip, bool selected = false, bool enabled = true, float iconSize = 20)
        {
            bool clicked = Clickable(r, out bool pressed, out bool hover, enabled);
            if (selected) Rounded(r, PaintTheme.AccentDim, 4);
            else if (pressed) Rounded(r, PaintTheme.ControlActive, 4);
            else if (hover) Rounded(r, PaintTheme.ControlHover, 4);
            Icon(r, icon, !enabled ? PaintTheme.TextDisabled : selected ? Color.white : hover ? Color.white : PaintTheme.Text, iconSize);
            Tooltip(r, tooltip);
            return clicked;
        }

        /// <summary>ツールのアイコン（<see cref="PainterToolIcons"/>。描き手やプラグインの絵は色を付けずに描く）を描く。</summary>
        public static void ToolIcon(Rect r, string toolId, bool selected, Color tint, float size = 20)
        {
            if (!Repainting) return;
            var icon = PainterToolIcons.Get(toolId, selected);
            if (icon.Texture == null) { Icon(r, null, tint, size); return; }
            var at = new Rect(r.center.x - size / 2, r.center.y - size / 2, size, size);
            GUI.DrawTexture(at, icon.Texture, ScaleMode.ScaleToFit, true, 0, icon.Tint ? tint : (GUI.enabled ? Color.white : new Color(1, 1, 1, .4f)), 0, 0);
        }

        /// <summary>ツールの帯のボタン。右クリックで onContext（アイコンの差し替えなど）。</summary>
        public static bool ToolButton(Rect r, string toolId, string tooltip, bool selected, Action<Rect> onContext = null)
        {
            if (onContext != null && GUI.enabled && r.Contains(E.mousePosition) && (E.type == EventType.ContextClick || E.type == EventType.MouseDown && E.button == 1)) { onContext(r); E.Use(); }
            bool clicked = Clickable(r, out bool pressed, out bool hover);
            if (selected) Rounded(r, PaintTheme.AccentDim, 4);
            else if (pressed) Rounded(r, PaintTheme.ControlActive, 4);
            else if (hover) Rounded(r, PaintTheme.ControlHover, 4);
            ToolIcon(r, toolId, selected, selected || hover ? Color.white : PaintTheme.Text, 22);
            Tooltip(r, tooltip);
            return clicked;
        }

        /// <summary>文字のボタン（ダイアログの OK・取消など）。primary は青。</summary>
        public static bool Button(Rect r, string text, bool primary = false, bool enabled = true, string tooltip = null, string icon = null)
        {
            bool clicked = Clickable(r, out bool pressed, out bool hover, enabled);
            Color bg = !enabled ? PaintTheme.ControlBg : primary ? (pressed ? PaintTheme.AccentDim : hover ? PaintTheme.SliderFillHover : PaintTheme.Accent) : pressed ? PaintTheme.ControlActive : hover ? PaintTheme.ControlHover : PaintTheme.PanelHeader;
            Rounded(r, bg, 4);
            if (!primary) Outline(r, PaintTheme.Border, 1, 4);
            var color = enabled ? (primary ? Color.white : PaintTheme.Text) : PaintTheme.TextDisabled;
            if (icon != null)
            {
                float w = PaintTheme.Label.CalcSize(new GUIContent(text)).x + 22;
                var start = new Rect(r.center.x - w / 2, r.y, 18, r.height);
                Icon(start, icon, color, 16);
                Text(new Rect(start.xMax + 4, r.y, r.width, r.height), text, PaintTheme.Label, color);
            }
            else Text(r, text, PaintTheme.LabelCenter, color);
            Tooltip(r, tooltip);
            return clicked;
        }

        /// <summary>パネルの見出し（折りたためる）。開閉の状態を返す。</summary>
        public static bool SectionHeader(Rect r, string title, bool open, string icon = null)
        {
            bool clicked = Clickable(r, out _, out bool hover);
            Fill(r, hover ? PaintTheme.ControlHover : PaintTheme.PanelHeader);
            HLine(r.x, r.xMax, r.yMax - 1, PaintTheme.Border);
            Icon(new Rect(r.x + 4, r.y, 16, r.height), open ? "expand_more" : "chevron_right", PaintTheme.TextDim, 16);
            float x = r.x + 22;
            if (icon != null) { Icon(new Rect(x, r.y, 16, r.height), icon, PaintTheme.TextDim, 15); x += 20; }
            Text(new Rect(x, r.y, r.width - x + r.x, r.height), title, PaintTheme.Header);
            return clicked ? !open : open;
        }

        /// <summary>折りたたまない見出し（ドックのパネル名）。</summary>
        public static void PanelTitle(Rect r, string title, string icon = null)
        {
            Fill(r, PaintTheme.PanelHeader);
            HLine(r.x, r.xMax, r.yMax - 1, PaintTheme.Border);
            float x = r.x + 8;
            if (icon != null) { Icon(new Rect(x, r.y, 16, r.height), icon, PaintTheme.TextDim, 15); x += 20; }
            Text(new Rect(x, r.y, r.xMax - x, r.height), title, PaintTheme.Header);
        }

        // ───────── スライダー ─────────
        static int s_editingId; static string s_editingText; static bool s_editFocus;

        /// <summary>
        /// 値が中に出るスライダー（Substance Painter のもの）。押した位置の値になり、ドラッグで動かす。ダブルクリックで数値を打てる
        /// （Enter で決める、Esc でやめる）。label は左、値は右に重ねて描く。
        /// </summary>
        public static float Slider(Rect r, string label, float value, float min, float max, string format = "0.##", string suffix = "", string tooltip = null, bool enabled = true)
        {
            int id = GUIUtility.GetControlID(FocusType.Keyboard, r);
            enabled &= GUI.enabled; // ストロークの間など、GUI が止められているときは動かさない
            if (s_editingId == id) return EditNumber(r, id, value, min, max);
            bool hover = enabled && Hover(r);
            float t = max > min ? Mathf.Clamp01((value - min) / (max - min)) : 0;
            if (enabled)
                switch (E.GetTypeForControl(id))
                {
                    case EventType.MouseDown:
                        if (E.button == 0 && r.Contains(E.mousePosition))
                        {
                            if (E.clickCount == 2) { s_editingId = id; s_editingText = value.ToString(format, CultureInfo.InvariantCulture); s_editFocus = true; E.Use(); return value; }
                            GUIUtility.hotControl = id; GUIUtility.keyboardControl = 0; E.Use();
                            value = ValueAt(r, min, max); GUI.changed = true;
                        }
                        break;
                    case EventType.MouseDrag:
                        if (GUIUtility.hotControl == id) { value = ValueAt(r, min, max); GUI.changed = true; E.Use(); }
                        break;
                    case EventType.MouseUp:
                        if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; E.Use(); }
                        break;
                }
            if (Repainting)
            {
                bool active = GUIUtility.hotControl == id;
                Rounded(r, PaintTheme.ControlBg, 3);
                // 0 をまたぐ範囲（角度 −180〜180 など）は 0 の位置から塗る（0 が半分まで塗られて見えないように）
                float from = min < 0 && max > 0 ? Mathf.Clamp01(-min / (max - min)) : 0;
                var fill = new Rect(r.x + r.width * Mathf.Min(from, t), r.y, Mathf.Max(0, r.width * Mathf.Abs(t - from)), r.height);
                if (fill.width > 0) Rounded(fill, !enabled ? PaintTheme.ControlHover : active || hover ? PaintTheme.SliderFillHover : PaintTheme.SliderFill, 3);
                Outline(r, hover || active ? PaintTheme.AccentDim : PaintTheme.Border, 1, 3);
                var inner = new Rect(r.x + 7, r.y, r.width - 14, r.height);
                var color = enabled ? PaintTheme.Text : PaintTheme.TextDisabled;
                if (!string.IsNullOrEmpty(label)) Text(inner, label, PaintTheme.Label, color);
                Text(inner, value.ToString(format, CultureInfo.InvariantCulture) + suffix, PaintTheme.Value, color);
            }
            Tooltip(r, tooltip);
            return value;
        }
        public static int IntSlider(Rect r, string label, int value, int min, int max, string suffix = "", string tooltip = null, bool enabled = true)
            => Mathf.RoundToInt(Slider(r, label, value, min, max, "0", suffix, tooltip, enabled));

        static float ValueAt(Rect r, float min, float max) => Mathf.Lerp(min, max, Mathf.Clamp01((E.mousePosition.x - r.x) / Mathf.Max(1, r.width)));

        static float EditNumber(Rect r, int id, float value, float min, float max)
        {
            string name = "yp-number-" + id;
            Rounded(r, PaintTheme.ControlBg, 3); Outline(r, PaintTheme.Accent, 1, 3);
            if (E.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == name)
            {
                if (E.keyCode == KeyCode.Return || E.keyCode == KeyCode.KeypadEnter)
                {
                    if (float.TryParse(s_editingText, NumberStyles.Float, CultureInfo.InvariantCulture, out float typed)) { value = Mathf.Clamp(typed, min, max); GUI.changed = true; }
                    s_editingId = 0; GUIUtility.keyboardControl = 0; E.Use(); return value;
                }
                if (E.keyCode == KeyCode.Escape) { s_editingId = 0; GUIUtility.keyboardControl = 0; E.Use(); return value; }
            }
            GUI.SetNextControlName(name);
            s_editingText = GUI.TextField(new Rect(r.x + 2, r.y + 1, r.width - 4, r.height - 2), s_editingText ?? "", PaintTheme.Field);
            if (s_editFocus) { GUI.FocusControl(name); s_editFocus = false; }
            else if (E.type == EventType.MouseDown && !r.Contains(E.mousePosition)) s_editingId = 0; // ほかを押したらやめる
            return value;
        }

        // ───────── チェック・ドロップダウン・入力欄・色 ─────────

        public static bool Toggle(Rect r, string label, bool value, string tooltip = null, bool enabled = true)
        {
            if (Clickable(r, out _, out bool hover, enabled)) { value = !value; GUI.changed = true; }
            var box = new Rect(r.x, r.center.y - 8, 16, 16);
            Rounded(box, value ? (enabled ? PaintTheme.Accent : PaintTheme.ControlHover) : PaintTheme.ControlBg, 3);
            if (!value) Outline(box, hover ? PaintTheme.AccentDim : PaintTheme.Separator, 1, 3);
            if (value) Icon(box, "check", Color.white, 14);
            Text(new Rect(box.xMax + 7, r.y, r.width - 23, r.height), label, PaintTheme.Label, enabled ? PaintTheme.Text : PaintTheme.TextDisabled);
            Tooltip(r, tooltip);
            return value;
        }

        /// <summary>選んでいる値を出す箱。押すと open が呼ばれる（GenericMenu を rect の下に開く）。label があれば左に。</summary>
        public static void Dropdown(Rect r, string label, string value, Action<Rect> open, string tooltip = null, bool enabled = true, float labelWidth = 0)
        {
            var box = r;
            if (!string.IsNullOrEmpty(label))
            {
                float lw = labelWidth > 0 ? labelWidth : Mathf.Min(r.width * .42f, PaintTheme.Label.CalcSize(new GUIContent(label)).x + 10);
                Text(new Rect(r.x, r.y, lw, r.height), label, PaintTheme.Label, enabled ? PaintTheme.Text : PaintTheme.TextDisabled);
                box = new Rect(r.x + lw, r.y, r.width - lw, r.height);
            }
            if (Clickable(box, out bool pressed, out bool hover, enabled)) open?.Invoke(box);
            Rounded(box, pressed ? PaintTheme.ControlActive : hover ? PaintTheme.ControlHover : PaintTheme.ControlBg, 3);
            Outline(box, PaintTheme.Border, 1, 3);
            Text(new Rect(box.x + 7, box.y, box.width - 26, box.height), value, PaintTheme.Label, enabled ? PaintTheme.Text : PaintTheme.TextDisabled);
            Icon(new Rect(box.xMax - 20, box.y, 18, box.height), "arrow_drop_down", PaintTheme.TextDim, 18);
            Tooltip(box, tooltip);
        }

        /// <summary>列挙型のドロップダウン（names は表示名。順は values と同じ）。</summary>
        public static T EnumDropdown<T>(Rect r, string label, T value, T[] values, Func<T, string> name, Action<T> changed, bool enabled = true, float labelWidth = 0)
        {
            Dropdown(r, label, name(value), box =>
            {
                var menu = new GenericMenu();
                foreach (var v in values) { var item = v; menu.AddItem(new GUIContent(name(item)), Equals(item, value), () => changed(item)); }
                menu.DropDown(box);
            }, null, enabled, labelWidth);
            return value;
        }

        static readonly System.Collections.Generic.Dictionary<string, string> s_textBuffers = new System.Collections.Generic.Dictionary<string, string>();

        /// <summary>文字の入力欄。打っている間は値を返さず、Enter か、ほかを押してフォーカスが外れたときに確定する（Esc でやめる）。
        /// Unity の EditorGUI を使わない（バッチモードのオフスクリーンの描画では標準のスタイルが無いため）。</summary>
        public static string TextField(Rect r, string text, string tooltip = null)
        {
            text = text ?? "";
            int id = GUIUtility.GetControlID("YoluPainterText".GetHashCode(), FocusType.Passive, r);
            string name = "yp-text-" + id;
            bool focused = GUI.GetNameOfFocusedControl() == name;
            string result = text;
            if (focused && E.type == EventType.KeyDown && (E.keyCode == KeyCode.Return || E.keyCode == KeyCode.KeypadEnter))
            { if (s_textBuffers.TryGetValue(name, out var typed)) result = typed; s_textBuffers.Remove(name); GUIUtility.keyboardControl = 0; E.Use(); GUI.changed = true; return result; }
            if (focused && E.type == EventType.KeyDown && E.keyCode == KeyCode.Escape) { s_textBuffers.Remove(name); GUIUtility.keyboardControl = 0; E.Use(); return text; }
            if (!focused && s_textBuffers.TryGetValue(name, out var pending)) { s_textBuffers.Remove(name); if (pending != text) { result = pending; GUI.changed = true; } } // フォーカスが外れたら確定
            Rounded(r, PaintTheme.ControlBg, 3);
            Outline(r, focused ? PaintTheme.Accent : Hover(r) ? PaintTheme.AccentDim : PaintTheme.Border, 1, 3);
            string shown = focused && s_textBuffers.TryGetValue(name, out var buffer) ? buffer : text;
            GUI.SetNextControlName(name);
            string edited = GUI.TextField(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), shown, PaintTheme.Field);
            if (GUI.GetNameOfFocusedControl() == name) s_textBuffers[name] = edited;
            Tooltip(r, tooltip);
            return result;
        }

        /// <summary>色の見本。押すと Unity の色選択が開き、決まるたびに changed が呼ばれる。</summary>
        /// <param name="hdr">HDR の色（1 を超える値。Unity の色選択を HDR で開く）。</param>
        public static void ColorSwatch(Rect r, Color color, Action<Color> changed, bool showAlpha = true, string tooltip = null, bool enabled = true, bool hdr = false)
        {
            if (Clickable(r, out _, out bool hover, enabled)) OpenColorPicker(color, changed, showAlpha, hdr);
            if (Repainting)
            {
                // 透明が分かるように市松の上に描く
                var half = new Rect(r.x, r.y, r.width / 2, r.height);
                Rounded(r, Color.white, 3);
                Fill(new Rect(r.x + r.width / 2, r.y, r.width / 4, r.height / 2), new Color(.75f, .75f, .75f)); Fill(new Rect(r.x + r.width * .75f, r.y + r.height / 2, r.width / 4, r.height / 2), new Color(.75f, .75f, .75f));
                Rounded(half, new Color(color.r, color.g, color.b, 1), 3);
                Fill(new Rect(r.x + r.width / 2, r.y, r.width / 2, r.height), color);
                Outline(r, hover ? PaintTheme.Accent : PaintTheme.Border, 1, 3);
            }
            Tooltip(r, tooltip);
        }

        static MethodInfo s_pickerShow;
        static void OpenColorPicker(Color color, Action<Color> changed, bool showAlpha, bool hdr = false)
        {
            // UnityEditor.ColorPicker.Show(Action<Color>, Color, bool showAlpha, bool hdr)（internal）
            if (s_pickerShow == null)
            {
                var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.ColorPicker");
                s_pickerShow = type?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "Show" && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Action<Color>), typeof(Color), typeof(bool), typeof(bool) }));
            }
            if (s_pickerShow != null) s_pickerShow.Invoke(null, new object[] { (Action<Color>)(c => { changed(c); }), color, showAlpha, hdr });
            else Debug.LogWarning("YoluPainter: Unity's color picker could not be opened.");
        }

        /// <summary>メニューバー: 項目の名前を並べ、押した項目の rect で open を呼ぶ（GenericMenu を下に開く）。</summary>
        public static void MenuBar(Rect r, string[] titles, Action<int, Rect> open)
        {
            Fill(r, PaintTheme.MenuBg);
            HLine(r.x, r.xMax, r.yMax - 1, PaintTheme.Border);
            float x = r.x + 6;
            for (int i = 0; i < titles.Length; i++)
            {
                float w = PaintTheme.Menu.CalcSize(new GUIContent(titles[i])).x + 18;
                var item = new Rect(x, r.y + 2, w, r.height - 4);
                if (Clickable(item, out bool pressed, out bool hover)) open(i, new Rect(item.x, r.yMax - 2, item.width, 0));
                if (pressed || hover) Rounded(item, PaintTheme.ControlHover, 3);
                Text(item, titles[i], PaintTheme.Menu);
                x += w;
            }
        }

        /// <summary>スクロールする領域の始まり（GUI のスキンに頼らない。バッチモードのオフスクリーンの描画では GUI.BeginScrollView が
        /// スキンの無さで落ちる）。中は (0, 0) 始まりの座標で描き、<see cref="EndScroll"/> で閉じる。マウスの位置も中の座標になる。</summary>
        public static void BeginScroll(Rect viewport, Vector2 scroll) => GUI.BeginClip(viewport, -scroll, Vector2.zero, false);
        public static void EndScroll() => GUI.EndClip();

        /// <summary>透明を表す市松（cell の大きさ）。</summary>
        public static void Checker(Rect r, float cell = 4)
        {
            if (!Repainting) return;
            EditorGUI.DrawRect(r, new Color(.42f, .42f, .44f));
            var dark = new Color(.30f, .30f, .32f);
            for (float y = 0; y < r.height; y += cell)
                for (float x = ((int)(y / cell) % 2) * cell; x < r.width; x += cell * 2)
                    EditorGUI.DrawRect(new Rect(r.x + x, r.y + y, Mathf.Min(cell, r.width - x), Mathf.Min(cell, r.height - y)), dark);
        }

        /// <summary>縦の帯の区切り。</summary>
        public static void StripSeparator(Rect r) => HLine(r.x + 6, r.xMax - 6, r.center.y, PaintTheme.Separator);
    }

    /// <summary>縦に行を積むだけの配置の補助（パネルの中身）。</summary>
    internal sealed class UiRows
    {
        readonly Rect area; float y;
        public UiRows(Rect area, float top = 6) { this.area = area; y = area.y + top; }
        public float Width => area.width - 2 * PaintTheme.Padding;
        public float Used => y - area.y;
        public Rect Row(float height = PaintTheme.RowHeight, float gap = 4) { var r = new Rect(area.x + PaintTheme.Padding, y, Width, height); y += height + gap; return r; }
        public Rect FullRow(float height, float gap = 0) { var r = new Rect(area.x, y, area.width, height); y += height + gap; return r; }
        public void Space(float h) => y += h;
        /// <summary>行を幅で n 等分した rect（間は gap）。</summary>
        public static Rect[] Split(Rect r, int n, float gap = 4)
        {
            var result = new Rect[n]; float w = (r.width - gap * (n - 1)) / n;
            for (int i = 0; i < n; i++) result[i] = new Rect(r.x + i * (w + gap), r.y, w, r.height);
            return result;
        }
    }
}
