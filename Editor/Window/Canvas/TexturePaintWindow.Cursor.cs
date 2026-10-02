using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>ブラシのカーソル: 2D キャンバスではブラシの直径の円、3D ビューではマウスの下の面での直径を画面の大きさに直した円。
    /// 白と黒の二重の線で、明るい所でも暗い所でも見える（Photoshop の「ブラシ先端」のカーソルと同じ考え方）。</summary>
    public sealed partial class TexturePaintWindow
    {
        bool ShowsBrushCursor => tool == PaintTool.Brush;

        /// <summary>キャンバスのクリップの中（view はクリップの中の座標の写し）。円なので回転・反転では変わらず、拡大率だけを掛ける。
        /// R を押して表示を回すあいだは出さない。</summary>
        void DrawCanvasBrushCursor(CanvasView view, Vector2 mouse)
        {
            if (Event.current.type != EventType.Repaint || !ShowsBrushCursor || rotateKeyHeld || canvasRotating) return;
            if (!new Rect(0, 0, canvasRect.width, canvasRect.height).Contains(mouse)) return;
            float radius = brush.radius * view.PixelSize;
            Circle(mouse, radius);
        }

        /// <summary>3D ビュー（ウィンドウの座標）。</summary>
        void DrawSurfaceBrushCursor(Vector2 mouse)
        {
            if (Event.current.type != EventType.Repaint || !ShowsBrushCursor || !preview.HasModel) return;
            if (!surfaceRect.Contains(mouse) || !preview.TryPick(surfaceRect, mouse, out var hit)) return;
            float worldRadius = Mathf.Max(.000001f, preview.Bounds.size.magnitude) * brush.radius / document.Width;
            Circle(mouse, preview.WorldRadiusToGuiPoints(hit.Position, worldRadius));
        }

        static void Circle(Vector2 center, float radius)
        {
            if (radius < 1.5f) radius = 1.5f;
            int n = Mathf.Clamp(Mathf.CeilToInt(radius * .8f), 16, 96);
            var points = new Vector3[n + 1];
            for (int i = 0; i <= n; i++) { float t = i * Mathf.PI * 2 / n; points[i] = new Vector3(center.x + Mathf.Cos(t) * radius, center.y + Mathf.Sin(t) * radius); }
            var previous = Handles.color;
            Handles.color = new Color(0, 0, 0, .55f); Handles.DrawAAPolyLine(3, points);
            Handles.color = new Color(1, 1, 1, .9f); Handles.DrawAAPolyLine(1.2f, points);
            Handles.color = previous;
        }
    }
}
