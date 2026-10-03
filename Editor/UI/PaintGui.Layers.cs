using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// プロパティの欄のレイヤー・チャンネル側（レイヤーの詳細・マスク・フィルター・ノーマルの出力・メッシュマップ・ポーズ）で使う部品
    /// （<see cref="PaintGui"/> の続き）。収まらない文字は <see cref="Fit"/> で … にする（<see cref="ShortenedTexts"/> に数える）。
    /// 値を持つ部品は、利用者が触らなければ受け取った値をそのまま返す（double の値を float に丸めて「変わった」としない）。どれも
    /// GUI.enabled が false のあいだ（描画中のストロークなど）は入力を受けない。
    /// </summary>
    internal static partial class PaintGui
    {
        /// <summary>押すたびに入・切が替わる文字のボタン（入は青。「マスクに描く」など）。</summary>
        public static bool ToggleButton(Rect r, string text, bool on, string tooltip = null, string icon = null, bool enabled = true)
        {
            bool clicked = Clickable(r, out bool pressed, out bool hover, enabled);
            enabled &= GUI.enabled;
            Color bg = on ? (enabled ? PaintTheme.AccentDim : PaintTheme.ControlHover) : pressed ? PaintTheme.ControlActive : hover ? PaintTheme.ControlHover : PaintTheme.PanelHeader;
            Rounded(r, bg, 4);
            if (!on) Outline(r, PaintTheme.Border, 1, 4);
            var color = !enabled ? PaintTheme.TextDisabled : on ? Color.white : PaintTheme.Text;
            string shown = Fit(text, r.width - (icon != null ? 34 : 10), PaintTheme.Label);
            if (icon != null)
            {
                float w = TextWidth(shown, PaintTheme.Label) + 22;
                var start = new Rect(r.center.x - w / 2, r.y, 18, r.height);
                Icon(start, icon, color, 15);
                Text(new Rect(start.xMax + 4, r.y, Mathf.Max(0, r.xMax - start.xMax - 6), r.height), shown, PaintTheme.Label, color);
            }
            else Text(r, shown, PaintTheme.LabelCenter, color);
            Tooltip(r, shown != text ? text + (string.IsNullOrEmpty(tooltip) ? "" : "\n" + tooltip) : tooltip);
            if (clicked) { GUI.changed = true; return !on; }
            return on;
        }

        /// <summary>左に名前を描き、残りを n 等分した rect を返す（「入力 [黒] [白]」のような行）。</summary>
        public static Rect[] LabeledColumns(Rect r, string label, float labelWidth, int n, float gap = 4)
        {
            string shown = Fit(label, labelWidth - 6, PaintTheme.Label);
            Text(new Rect(r.x, r.y, labelWidth - 4, r.height), shown, PaintTheme.Label, GUI.enabled ? PaintTheme.Text : PaintTheme.TextDisabled);
            if (shown != label) Tooltip(new Rect(r.x, r.y, labelWidth - 4, r.height), label);
            return UiRows.Split(new Rect(r.x + labelWidth, r.y, r.width - labelWidth, r.height), n, gap);
        }

        /// <summary>double の値のスライダー（<see cref="FitSlider"/>）。触っていなければ受け取った値のまま返す（float に丸めた値や欄の
        /// 範囲で切った値で、文書や保存した条件を黙って変えない）。scale は表示の倍率（0〜1 を 0〜100 % で見せるなど）。</summary>
        public static double KeepSlider(Rect r, string label, double value, double min, double max, string format = "0.##", string suffix = "", string tooltip = null, bool enabled = true, double scale = 1, bool labelIsData = false)
        {
            enabled &= GUI.enabled;
            float lo = (float)(min * scale), hi = (float)(max * scale);
            float shown = Mathf.Clamp((float)(value * scale), lo, hi);
            if (labelIsData && !string.IsNullOrEmpty(label))
            {
                // 利用者のデータの名前（BlendShape など）は、詰めても数えない（FitSlider と同じ幅で先に詰めておく）
                float valueWidth = Mathf.Max(TextWidth(lo.ToString(format, CultureInfo.InvariantCulture) + suffix, PaintTheme.Value), TextWidth(hi.ToString(format, CultureInfo.InvariantCulture) + suffix, PaintTheme.Value), TextWidth(shown.ToString(format, CultureInfo.InvariantCulture) + suffix, PaintTheme.Value));
                string fitted = Fit(label, r.width - 14 - valueWidth - 8, PaintTheme.Label, false);
                if (fitted != label) { tooltip = label + (string.IsNullOrEmpty(tooltip) ? "" : "\n" + tooltip); label = fitted; }
            }
            float picked = FitSlider(r, label, shown, lo, hi, format, suffix, tooltip, enabled);
            return picked != shown ? picked / scale : value;
        }

        /// <summary>整数のスライダー（触っていなければ受け取った値のまま。欄の範囲の外の値も、触らなければ切らない）。</summary>
        public static int KeepIntSlider(Rect r, string label, int value, int min, int max, string suffix = "", string tooltip = null, bool enabled = true)
        {
            enabled &= GUI.enabled;
            int shown = Mathf.Clamp(value, min, max), picked = FitIntSlider(r, label, shown, min, max, suffix, tooltip, enabled);
            return picked != shown ? picked : value;
        }

        /// <summary>整数の欄（<see cref="IntField(Rect, string, int, int, int, string, string, bool)"/>）。中で float を通るので、2^24 を
        /// 超える値は触らなければそのまま返す（丸めた値で「変わった」としない）。</summary>
        public static int KeepIntField(Rect r, string label, int value, int min, int max, string tooltip = null, bool enabled = true)
        {
            int picked = IntField(r, label, value, min, max, "", tooltip, enabled & GUI.enabled);
            return picked != Mathf.RoundToInt((float)value) ? picked : value;
        }

        /// <summary>絞り込みの欄（打つたびに効く）。空のあいだは placeholder を薄く出す。</summary>
        public static string SearchField(Rect r, string text, string placeholder, string tooltip = null)
        {
            Rounded(r, PaintTheme.ControlBg, 3);
            Outline(r, Hover(r) ? PaintTheme.AccentDim : PaintTheme.Border, 1, 3);
            Icon(new Rect(r.x + 3, r.y, 18, r.height), "search", PaintTheme.TextDim, 14);
            var field = new Rect(r.x + 20, r.y + 1, r.width - 22, r.height - 2);
            if (string.IsNullOrEmpty(text)) Text(new Rect(field.x + 6, field.y, field.width - 6, field.height), Fit(placeholder, field.width - 8, PaintTheme.LabelDim), PaintTheme.LabelDim, PaintTheme.TextDisabled);
            var next = GUI.TextField(field, text ?? "", PaintTheme.Field);
            Tooltip(r, tooltip);
            return next;
        }

        /// <summary>
        /// アセット（やシーンのオブジェクト）を選ぶ欄: 名前の箱（押すと選ぶ）、選ぶボタン（Unity のオブジェクトピッカー）、外すボタン。
        /// 箱へのドラッグ＆ドロップでも選べる。ピッカーの結果は ExecuteCommand で届くので、この欄を描いているあいだに受け取る
        /// （pickerId で見分ける）。選び直したら true（value が新しい値）。
        /// </summary>
        public static bool ObjectBox<T>(Rect r, ref T value, int pickerId, bool allowSceneObjects, string emptyText, string icon, string pickTooltip, string clearTooltip, string searchFilter = "", bool enabled = true) where T : Object
        {
            var e = E; bool changed = false; enabled &= GUI.enabled;
            bool clearable = value != null;
            var box = new Rect(r.x, r.y, r.width - 26 * (clearable ? 2 : 1), r.height);
            bool dropping = enabled && (e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && box.Contains(e.mousePosition);
            if (Clickable(box, out bool pressed, out bool hover, enabled)) EditorGUIUtility.ShowObjectPicker<T>(value, allowSceneObjects, searchFilter, pickerId);
            Rounded(box, pressed ? PaintTheme.ControlActive : hover ? PaintTheme.ControlHover : PaintTheme.ControlBg, 3);
            Outline(box, hover ? PaintTheme.AccentDim : PaintTheme.Border, 1, 3);
            Icon(new Rect(box.x + 2, box.y, 20, box.height), icon, PaintTheme.TextDim, 15);
            string name = value != null ? Fit(value.name, box.width - 30, PaintTheme.Label, false) : Fit(emptyText, box.width - 30, PaintTheme.Label);
            Text(new Rect(box.x + 24, box.y, box.width - 28, box.height), name, PaintTheme.Label, !enabled ? PaintTheme.TextDisabled : value != null ? PaintTheme.Text : PaintTheme.TextDim);
            Tooltip(box, value != null ? value.name + "\n" + pickTooltip : pickTooltip);
            if (IconButton(new Rect(box.xMax + 2, r.y, 24, r.height), "target", pickTooltip, false, enabled, 16)) EditorGUIUtility.ShowObjectPicker<T>(value, allowSceneObjects, searchFilter, pickerId);
            if (clearable && IconButton(new Rect(box.xMax + 28, r.y, 24, r.height), "close", clearTooltip, false, enabled, 15)) { value = null; changed = true; }
            if (dropping)
            {
                var dropped = DragAndDrop.objectReferences.OfType<T>().FirstOrDefault(o => allowSceneObjects || EditorUtility.IsPersistent(o));
                if (dropped != null)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                    if (e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); if (dropped != value) { value = dropped; changed = true; } }
                    e.Use();
                }
            }
            if (e.type == EventType.ExecuteCommand && (e.commandName == "ObjectSelectorUpdated" || e.commandName == "ObjectSelectorClosed") && EditorGUIUtility.GetObjectPickerControlID() == pickerId)
            {
                var picked = EditorGUIUtility.GetObjectPickerObject() as T;
                if (picked != value) { value = picked; changed = true; }
                e.Use();
            }
            if (changed) GUI.changed = true;
            return changed;
        }

        /// <summary>小さな丸（凡例・状態の印）。</summary>
        public static void Dot(Rect r, Color c, float size = 8) => Rounded(new Rect(r.center.x - size / 2, r.center.y - size / 2, size, size), c, size / 2);
    }
}
