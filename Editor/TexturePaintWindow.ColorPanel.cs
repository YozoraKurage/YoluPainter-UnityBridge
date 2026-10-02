using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// ドックのカラーパネル（Photoshop のカラー、CLIP STUDIO のカラーサークルと使用色の履歴に当たる）。カラーとエミッションでは彩度×明度の
    /// 四角と色相の帯、16 進の欄、使った色の履歴。ラフネス・メタリック・ハイトでは値の帯。ノーマルでは向きをプロパティで選ぶ案内。
    /// 色相は描き手の操作で決めた値を覚え、彩度や明度が 0 になっても失わない。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] bool dockColorOpen = true;
        [SerializeField] List<Color> recentColors = new List<Color>();
        const int MaxRecentColors = 16;
        float pickHue, pickSat, pickVal; Color pickedFor = new Color(-1, -1, -1, -1);
        Texture2D svTexture, hueTexture; float svTextureHue = -1;

        /// <summary>彩度×明度の四角の高さ（低い画面では小さく）。</summary>
        float SvHeight => Mathf.Clamp((dockRect.height - 480) * .5f, 72, 128);
        float ColorPanelHeight => channel == PaintChannel.Color || channel == PaintChannel.Emission ? 8 + SvHeight + 6 + 22 + 6 + 16 + 8 : 70;

        void DrawColorPanel(Rect r)
        {
            var rows = new UiRows(r, 8);
            if (channel == PaintChannel.Normal)
            {
                PaintGui.Text(rows.Row(36), L.Tr("The Normal channel paints a direction. Choose it in Properties ▸ Normal."), PaintTheme.Wrap);
                return;
            }
            if (channel == PaintChannel.Roughness || channel == PaintChannel.Metallic || channel == PaintChannel.Height)
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
            var sv = new Rect(area.x, area.y, area.width - 26, area.height);
            var hue = new Rect(sv.xMax + 8, area.y, 18, area.height);
            DrawSvSquare(sv); DrawHueBar(hue);
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
            if (channel != PaintChannel.Color && channel != PaintChannel.Emission || EditingMask || brush.erase) return;
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
            foreach (var t in new[] { svTexture, hueTexture, greyRamp }) if (t != null) DestroyImmediate(t);
            svTexture = hueTexture = greyRamp = null; svTextureHue = -1;
        }

        /// <summary>テスト用。</summary>
        internal IReadOnlyList<Color> RecentColors => recentColors;
        internal void PickColorAt(Rect square, Vector2 p) => PickSv(square, p);
    }
}
