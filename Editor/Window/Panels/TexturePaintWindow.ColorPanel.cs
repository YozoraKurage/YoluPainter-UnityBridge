using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// ドックのカラーパネル（Photoshop のカラー、CLIP STUDIO のカラーサークルと使用色の履歴に当たる）。カラーとエミッションでは彩度×明度の
    /// 四角と色相の帯（または色相の円とその中の四角。右上の切り替えで選び、描き手ごとに覚える）、16 進の欄、使った色の履歴。ラフネス・メタリック・ハイトでは値の帯。ノーマルでは向きをプロパティで選ぶ案内。
    /// 色相は描き手の操作で決めた値を覚え、彩度や明度が 0 になっても失わない。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] List<Color> recentColors = new List<Color>();
        const int MaxRecentColors = 16;
        float pickHue, pickSat, pickVal; Color pickedFor = new Color(-1, -1, -1, -1);
        Texture2D svTexture, hueTexture, hueRing; float svTextureHue = -1;

        const string ColorWheelKey = "Yozolab.YoluPainter.ColorWheel";
        bool? colorWheel;
        /// <summary>色相の円で選ぶか（false なら四角と色相の帯）。描き手ごとに EditorPrefs に覚える。</summary>
        internal bool ColorWheel
        {
            get => colorWheel ?? (colorWheel = EditorPrefs.GetBool(ColorWheelKey, false)).Value;
            set { colorWheel = value; EditorPrefs.SetBool(ColorWheelKey, value); Repaint(); }
        }

        /// <summary>彩度×明度の四角の高さ（低い画面では小さく）。円のときは円の直径。</summary>
        float SvHeight => ColorWheel ? Mathf.Clamp((colorColumnHeight - 480) * .6f, 120, 196) : Mathf.Clamp((colorColumnHeight - 480) * .5f, 72, 128);
        float ColorPanelHeight => ColorPanelPicksColor ? 8 + SvHeight + 6 + 22 + 6 + 16 + 8 : 70;
        /// <summary>色のパネルが色を選ぶか（Color・Emission のとき。マテリアルで塗るときはいつも描画色 = Color の値。ほかのチャンネルの値は
        /// ブラシの欄の「マテリアル」の節）。</summary>
        bool ColorPanelPicksColor => brush.material || channel == PaintChannel.Color || channel == PaintChannel.Emission;
        /// <summary>色相の円の太さ（半径に対する割合）。</summary>
        internal const float RingThickness = .17f;

        void DrawColorPanel(Rect r)
        {
            var rows = new UiRows(r, 8);
            if (!brush.material && channel == PaintChannel.Normal)
            {
                PaintGui.Text(rows.Row(36), L.Tr("The Normal channel paints a direction. Choose it in Properties ▸ Normal."), PaintTheme.Wrap);
                return;
            }
            if (!brush.material && (channel == PaintChannel.Roughness || channel == PaintChannel.Metallic || channel == PaintChannel.Height))
            {
                var row = rows.Row(24);
                float value = PaintGui.Slider(row, L.Tr("Value"), brush.color.r * 255, 0, 255, "0", "", L.Tr("The value the brush paints (0–255)")) / 255;
                if (Mathf.Abs(value - brush.color.r) > 1e-5f) brush.color = new Color(value, value, value, brush.color.a);
                var strip = rows.Row(10);
                if (Event.current.type == EventType.Repaint) GUI.DrawTexture(strip, GreyRamp(), ScaleMode.StretchToFill, false);
                return;
            }
            SyncHsvFromBrush();
            var area = rows.Row(SvHeight, 6);
            var toggle = new Rect(area.xMax - 22, area.y, 22, 22);
            if (ColorWheel)
            {
                var wheel = WheelRect(area);
                DrawHueRing(wheel); DrawSvSquare(WheelSquare(wheel));
            }
            else
            {
                var sv = new Rect(area.x, area.y, area.width - 26 - 26, area.height);
                var hue = new Rect(sv.xMax + 8, area.y, 18, area.height);
                DrawSvSquare(sv); DrawHueBar(hue);
            }
            if (PaintGui.IconButton(toggle, ColorWheel ? "color_square" : "target", L.Tr(ColorWheel ? "Square and hue bar" : "Hue wheel"))) ColorWheel = !ColorWheel;
            // 16 進と使った色
            var line = rows.Row(22, 6);
            var cells = UiRows.Split(line, 2, 6);
            string hex = ColorUtility.ToHtmlStringRGB(brush.color);
            string typed = PaintGui.TextField(new Rect(cells[0].x, cells[0].y, cells[0].width, cells[0].height), "#" + hex, L.Tr("Hex color (#RRGGBB)"));
            if (typed != "#" + hex && ColorUtility.TryParseHtmlString(typed.StartsWith("#") ? typed : "#" + typed, out var parsed)) SetBrushColor(new Color(parsed.r, parsed.g, parsed.b, brush.color.a));
            float alpha = PaintGui.Slider(cells[1], "A", brush.color.a * 100, 0, 100, "0", "%", L.Tr("Alpha of the brush color")) / 100;
            if (Mathf.Abs(alpha - brush.color.a) > 1e-5f) brush.color = new Color(brush.color.r, brush.color.g, brush.color.b, alpha);
            DrawRecentColors(rows.Row(16));
        }

        void SyncHsvFromBrush()
        {
            if (brush.color == pickedFor) return;
            Color.RGBToHSV(brush.color, out float h, out float s, out float v);
            // 灰色や黒では色相（と彩度）が決まらないので、前の値を残す
            if (s > 1e-4f && v > 1e-4f) pickHue = h;
            if (v > 1e-4f) pickSat = s;
            pickVal = v; pickedFor = brush.color;
        }

        void SetBrushColor(Color c) { brush.color = c; pickedFor = new Color(-1, -1, -1, -1); Repaint(); }

        void DrawSvSquare(Rect r)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive, r); var e = Event.current;
            if (GUI.enabled)
                switch (e.GetTypeForControl(id))
                {
                    case EventType.MouseDown: if (e.button == 0 && r.Contains(e.mousePosition)) { GUIUtility.hotControl = id; PickSv(r, e.mousePosition); e.Use(); } break;
                    case EventType.MouseDrag: if (GUIUtility.hotControl == id) { PickSv(r, e.mousePosition); e.Use(); } break;
                    case EventType.MouseUp: if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); } break;
                }
            if (e.type != EventType.Repaint) return;
            GUI.DrawTexture(r, SvTexture(pickHue), ScaleMode.StretchToFill, false);
            PaintGui.Outline(r, PaintTheme.Border, 1, 0);
            var at = new Vector2(r.x + pickSat * r.width, r.y + (1 - pickVal) * r.height);
            PaintGui.Outline(new Rect(at.x - 6, at.y - 6, 12, 12), Color.black, 2, 6);
            PaintGui.Outline(new Rect(at.x - 5, at.y - 5, 10, 10), Color.white, 1.5f, 5);
        }

        void PickSv(Rect r, Vector2 p)
        {
            SyncHsvFromBrush();
            pickSat = Mathf.Clamp01((p.x - r.x) / r.width); pickVal = Mathf.Clamp01(1 - (p.y - r.y) / r.height);
            var c = Color.HSVToRGB(pickHue, pickSat, pickVal); c.a = brush.color.a;
            brush.color = c; pickedFor = c; Repaint();
        }

        void DrawHueBar(Rect r)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive, r); var e = Event.current;
            if (GUI.enabled)
                switch (e.GetTypeForControl(id))
                {
                    case EventType.MouseDown: if (e.button == 0 && r.Contains(e.mousePosition)) { GUIUtility.hotControl = id; PickHue(r, e.mousePosition); e.Use(); } break;
                    case EventType.MouseDrag: if (GUIUtility.hotControl == id) { PickHue(r, e.mousePosition); e.Use(); } break;
                    case EventType.MouseUp: if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); } break;
                }
            if (e.type != EventType.Repaint) return;
            GUI.DrawTexture(r, HueTexture(), ScaleMode.StretchToFill, false);
            PaintGui.Outline(r, PaintTheme.Border, 1, 0);
            float y = r.y + (1 - pickHue) * r.height;
            PaintGui.Outline(new Rect(r.x - 2, y - 3, r.width + 4, 6), Color.white, 1.5f, 2);
        }

        /// <summary>円の外接の正方形（領域の中央。右上の切り替えのボタンの分を空ける）。</summary>
        internal static Rect WheelRect(Rect area)
        {
            float size = Mathf.Min(area.height, area.width - 2 * 26);
            return new Rect(area.center.x - size * .5f, area.y + (area.height - size) * .5f, size, size);
        }
        /// <summary>円の中の彩度×明度の四角（円の内側に収める）。</summary>
        internal static Rect WheelSquare(Rect wheel)
        {
            float inner = wheel.width * .5f * (1 - RingThickness) - 4, side = Mathf.Floor(inner * Mathf.Sqrt(2));
            return new Rect(Mathf.Round(wheel.center.x - side * .5f), Mathf.Round(wheel.center.y - side * .5f), side, side);
        }
        /// <summary>円の上の点の色相（真上が赤、時計回り）。</summary>
        internal static float HueAt(Rect wheel, Vector2 p)
        {
            var d = p - wheel.center;
            float angle = Mathf.Atan2(d.x, -d.y) / (2 * Mathf.PI); // 真上が 0、時計回りに増える
            return angle < 0 ? angle + 1 : angle;
        }
        internal static bool InRing(Rect wheel, Vector2 p)
        {
            float r = wheel.width * .5f, d = Vector2.Distance(p, wheel.center);
            return d <= r + 2 && d >= r * (1 - RingThickness) - 2;
        }

        void DrawHueRing(Rect r)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive, r); var e = Event.current;
            if (GUI.enabled)
                switch (e.GetTypeForControl(id))
                {
                    case EventType.MouseDown: if (e.button == 0 && InRing(r, e.mousePosition)) { GUIUtility.hotControl = id; SetHue(HueAt(r, e.mousePosition)); e.Use(); } break;
                    case EventType.MouseDrag: if (GUIUtility.hotControl == id) { SetHue(HueAt(r, e.mousePosition)); e.Use(); } break;
                    case EventType.MouseUp: if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); } break;
                }
            if (e.type != EventType.Repaint) return;
            GUI.DrawTexture(r, HueRing(), ScaleMode.StretchToFill, true);
            float radius = r.width * .5f * (1 - RingThickness * .5f), a = pickHue * 2 * Mathf.PI;
            var at = new Vector2(r.center.x + Mathf.Sin(a) * radius, r.center.y - Mathf.Cos(a) * radius);
            float m = r.width * RingThickness * .42f;
            PaintGui.Outline(new Rect(at.x - m, at.y - m, 2 * m, 2 * m), Color.black, 2, m);
            PaintGui.Outline(new Rect(at.x - m + 1, at.y - m + 1, 2 * m - 2, 2 * m - 2), Color.white, 1.5f, m - 1);
        }

        void SetHue(float hue)
        {
            SyncHsvFromBrush(); // 描く前に呼ばれても、今のブラシの彩度と明度を保つ
            pickHue = Mathf.Repeat(hue, 1);
            var c = Color.HSVToRGB(pickHue, pickSat, pickVal); c.a = brush.color.a;
            brush.color = c; pickedFor = c; Repaint();
        }

        /// <summary>色相の円の絵（白い背景に描かないよう、輪の外と内は透明。縁は 1 画素ぶんぼかす）。</summary>
        Texture2D HueRing()
        {
            if (hueRing != null) return hueRing;
            const int size = 256;
            hueRing = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size]; float outer = size * .5f, inner = outer * (1 - RingThickness);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // テクスチャは下から上なので、画面の y（上が 0）に直してから角度を求める
                    float dx = x + .5f - outer, dy = (size - 1 - y) + .5f - outer, d = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(outer - d) * Mathf.Clamp01(d - inner);
                    float hue = Mathf.Atan2(dx, -dy) / (2 * Mathf.PI); if (hue < 0) hue += 1;
                    Color32 c = Color.HSVToRGB(hue, 1, 1); c.a = (byte)Mathf.RoundToInt(alpha * 255);
                    pixels[y * size + x] = c;
                }
            hueRing.SetPixels32(pixels); hueRing.Apply(false, false);
            return hueRing;
        }

        void PickHue(Rect r, Vector2 p)
        {
            pickHue = Mathf.Clamp01(1 - (p.y - r.y) / r.height);
            var c = Color.HSVToRGB(pickHue, pickSat, pickVal); c.a = brush.color.a;
            brush.color = c; pickedFor = c; Repaint();
        }

        void DrawRecentColors(Rect r)
        {
            float size = r.height; int columns = Mathf.Max(1, Mathf.FloorToInt((r.width + 3) / (size + 3)));
            for (int i = 0; i < recentColors.Count && i < columns; i++)
            {
                var cell = new Rect(r.x + i * (size + 3), r.y, size, size); var c = recentColors[i];
                PaintGui.Rounded(cell, new Color(c.r, c.g, c.b, 1), 2); PaintGui.Outline(cell, PaintTheme.Border, 1, 2);
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && cell.Contains(Event.current.mousePosition) && GUI.enabled) { SetBrushColor(c); Event.current.Use(); }
                PaintGui.Tooltip(cell, "#" + ColorUtility.ToHtmlStringRGBA(c));
            }
            if (recentColors.Count == 0) PaintGui.Text(r, L.Tr("Colors you paint with appear here."), PaintTheme.LabelSmall);
        }

        /// <summary>描き始めたブラシの色を使った色の履歴の先頭に足す（カラーとエミッションのとき。同じ色は前へ移す）。</summary>
        void RememberColor()
        {
            if (brush.material ? !MaterialIncludes(PaintChannel.Color) : channel != PaintChannel.Color && channel != PaintChannel.Emission) return;
            if (EditingMask || brush.erase) return;
            var c = brush.color;
            recentColors.RemoveAll(x => Mathf.Abs(x.r - c.r) < .002f && Mathf.Abs(x.g - c.g) < .002f && Mathf.Abs(x.b - c.b) < .002f && Mathf.Abs(x.a - c.a) < .002f);
            recentColors.Insert(0, c);
            if (recentColors.Count > MaxRecentColors) recentColors.RemoveRange(MaxRecentColors, recentColors.Count - MaxRecentColors);
        }

        Texture2D SvTexture(float hue)
        {
            const int size = 64;
            if (svTexture == null) svTexture = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            if (Mathf.Abs(svTextureHue - hue) < 1e-5f) return svTexture;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) pixels[y * size + x] = Color.HSVToRGB(hue, x / (size - 1f), y / (size - 1f));
            svTexture.SetPixels32(pixels); svTexture.Apply(false, false); svTextureHue = hue;
            return svTexture;
        }

        Texture2D HueTexture()
        {
            if (hueTexture != null) return hueTexture;
            const int size = 128;
            hueTexture = new Texture2D(1, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size];
            for (int y = 0; y < size; y++) pixels[y] = Color.HSVToRGB(y / (size - 1f), 1, 1);
            hueTexture.SetPixels32(pixels); hueTexture.Apply(false, false);
            return hueTexture;
        }

        Texture2D greyRamp;
        Texture2D GreyRamp()
        {
            if (greyRamp != null) return greyRamp;
            greyRamp = new Texture2D(256, 1, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[256]; for (int i = 0; i < 256; i++) pixels[i] = new Color32((byte)i, (byte)i, (byte)i, 255);
            greyRamp.SetPixels32(pixels); greyRamp.Apply(false, false);
            return greyRamp;
        }

        void DisposeColorPanel()
        {
            foreach (var t in new[] { svTexture, hueTexture, hueRing, greyRamp }) if (t != null) DestroyImmediate(t);
            svTexture = hueTexture = hueRing = greyRamp = null; svTextureHue = -1;
        }

        /// <summary>テスト用。</summary>
        internal IReadOnlyList<Color> RecentColors => recentColors;
        internal void PickColorAt(Rect square, Vector2 p) => PickSv(square, p);
        internal void PickHueAt(Rect wheel, Vector2 p) => SetHue(HueAt(wheel, p));
        internal float PickedHue => pickHue;
    }
}
