using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        CanvasSymmetrySettings strokeCanvasSymmetry;
        internal List<Vector2> CanvasSymmetryLines(CanvasView view)
        {
            var s = stroke != null && !surfaceStroke && strokeCanvasSymmetry != null ? strokeCanvasSymmetry : CanvasSymmetryNow();
            var lines = new List<Vector2>();
            void Line(double x0, double y0, double x1, double y1) { lines.Add(view.ToGui(x0, y0)); lines.Add(view.ToGui(x1, y1)); }
            if (s.Mode == CanvasSymmetryMode.Vertical || s.Mode == CanvasSymmetryMode.Both) Line(s.CenterX, 0, s.CenterX, document.Height);
            if (s.Mode == CanvasSymmetryMode.Horizontal || s.Mode == CanvasSymmetryMode.Both) Line(0, s.CenterY, document.Width, s.CenterY);
            if (s.Mode == CanvasSymmetryMode.Radial)
            {
                foreach (var t in s.Transforms())
                {
                    t.Map(s.CenterX + 1, s.CenterY, out double x, out double y);
                    double dx = x - s.CenterX, dy = y - s.CenterY, length = double.PositiveInfinity;
                    if (dx > 1e-12) length = System.Math.Min(length, (document.Width - s.CenterX) / dx);
                    else if (dx < -1e-12) length = System.Math.Min(length, -s.CenterX / dx);
                    if (dy > 1e-12) length = System.Math.Min(length, (document.Height - s.CenterY) / dy);
                    else if (dy < -1e-12) length = System.Math.Min(length, -s.CenterY / dy);
                    Line(s.CenterX, s.CenterY, s.CenterX + dx * length, s.CenterY + dy * length);
                }
            }
            return lines;
        }
        void DrawCanvasSymmetryAxes(CanvasView view)
        {
            if (Event.current.type != EventType.Repaint || !symmetryPlaneShown) return;
            var lines = CanvasSymmetryLines(view); var old = Handles.color;
            try
            {
                Handles.color = new Color(.45f, .82f, 1, .8f);
                for (int i = 0; i < lines.Count; i += 2) Handles.DrawAAPolyLine(1.5f, lines[i], lines[i + 1]);
            }
            finally { Handles.color = old; }
        }
        void DrawRadialSymmetryAxis()
        {
            if (Event.current.type != EventType.Repaint || !symmetryPlaneShown || !preview.HasModel) return;
            RadialSymmetry? radial = StrokeSymmetryFrozen ? strokeRadial : brush.radialSymmetry3D ? CurrentRadialSymmetry : (RadialSymmetry?)null;
            if (!radial.HasValue) return;
            var s = radial.Value; float length = preview.Bounds.size.magnitude * .65f;
            var v = Vector3.Cross(s.Axis, Mathf.Abs(s.Axis.y) < .9f ? Vector3.up : Vector3.right).normalized;
            var old = Handles.color;
            GUI.BeginClip(surfaceRect);
            try
            {
                Handles.color = new Color(.45f, .82f, 1, .8f);
                var rect = new Rect(0, 0, surfaceRect.width, surfaceRect.height);
                void Line(Vector3 a, Vector3 b)
                {
                    if (preview.TryWorldToGui(rect, a, out var p) && preview.TryWorldToGui(rect, b, out var q)) Handles.DrawAAPolyLine(1.5f, p, q);
                }
                Line(s.Origin - s.Axis * length, s.Origin + s.Axis * length);
                for (int i = 0; i < s.Count; i++) Line(s.Origin, s.RotatePoint(s.Origin + v * length * .5f, i));
            }
            finally { Handles.color = old; GUI.EndClip(); }
        }
    }
}
