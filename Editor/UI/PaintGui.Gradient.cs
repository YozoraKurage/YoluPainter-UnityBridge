using System;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    internal static partial class PaintGui
    {
        static int gradientControl, gradientIndex;
        static bool gradientAlpha, gradientMiddle;
        static GradientRamp gradientDraft, gradientOriginal;
        internal static bool GradientDragging => gradientControl != 0 && GUIUtility.hotControl == gradientControl;
        internal static void CancelGradientEditor()
        { if (gradientControl != 0 && GUIUtility.hotControl == gradientControl) GUIUtility.hotControl = 0; gradientControl = 0; gradientDraft = gradientOriginal = null; }
        /// <summary>Independent stop rows and midpoint diamonds. A drag is buffered until release; Escape leaves document/history
        /// untouched. A blank stop-row click adds a stop; right-click removes (at least two remain).</summary>
        public static GradientRamp GradientEditor(Rect r, GradientRamp ramp, ref int selected, ref bool alpha, bool scalar)
        {
            int id = GUIUtility.GetControlID("YoluPainterGradient".GetHashCode(), FocusType.Passive, r);
            bool dragging = GUIUtility.hotControl == id && gradientControl == id;
            if (!dragging && gradientControl == id) { gradientDraft = gradientOriginal = null; gradientControl = 0; }
            var shown = dragging ? gradientDraft : ramp; var e = Event.current; var mouse = e.mousePosition;
            float left = r.x + 7, width = Math.Max(1, r.width - 14);
            double Position() => Mathf.Clamp01((mouse.x - left) / width);
            if (GUI.enabled)
            {
                if (e.type == EventType.MouseDown && r.Contains(mouse) && (e.button == 0 || e.button == 1))
                {
                    bool a = mouse.y < r.y + 25; bool mid = a ? mouse.y > r.y + 13 : mouse.y < r.y + 61;
                    if (mouse.y >= r.y + 25 && mouse.y <= r.y + 47) return ramp;
                    int hit = -1; double best = 8 / width;
                    int count = a ? shown.Opacities.Count : shown.Colors.Count;
                    for (int k = 0; k < count - (mid ? 1 : 0); k++)
                    {
                        double p = a ? shown.Opacities[k].Position : shown.Colors[k].Position;
                        if (mid) { double end = a ? shown.Opacities[k + 1].Position : shown.Colors[k + 1].Position; p += (end - p) * (a ? shown.Opacities[k].Midpoint : shown.Colors[k].Midpoint); }
                        double d = Math.Abs(Position() - p); if (d < best) { best = d; hit = k; }
                    }
                    if (e.button == 1)
                    {
                        if (!mid && hit >= 0 && count > 2) ramp = a ? shown.WithOpacities(shown.Opacities.Where((_, k) => k != hit)) : shown.WithColors(shown.Colors.Where((_, k) => k != hit));
                        e.Use(); return ramp;
                    }
                    if (hit < 0 && !mid && count < GradientRamp.MaxStops)
                    {
                        double p = Position(); var positions = a ? shown.Opacities.Select(s => s.Position) : shown.Colors.Select(s => s.Position);
                        if (positions.All(x => Math.Abs(x - p) >= GradientRamp.MinGap))
                        {
                            var sampled = shown.SampleStops(p, scalar);
                            shown = a ? shown.WithOpacities(shown.Opacities.Concat(new[] { new GradientOpacityStop(p, sampled.A / 255.0) }).OrderBy(s => s.Position))
                                : shown.WithColors(shown.Colors.Concat(new[] { new GradientStop(p, sampled) }).OrderBy(s => s.Position));
                            hit = a ? shown.Opacities.ToList().FindIndex(s => s.Position == p) : shown.Colors.ToList().FindIndex(s => s.Position == p);
                        }
                    }
                    if (hit >= 0)
                    {
                        selected = hit; alpha = a; gradientIndex = hit; gradientAlpha = a; gradientMiddle = mid;
                        gradientOriginal = ramp; gradientDraft = shown; gradientControl = id; GUIUtility.hotControl = id; GUIUtility.keyboardControl = 0;
                    }
                    e.Use();
                }
                else if (dragging && e.type == EventType.MouseDrag)
                {
                    int k = gradientIndex; double p = Position();
                    if (gradientAlpha)
                    {
                        var list = gradientDraft.Opacities.ToArray(); var s = list[k];
                        double at = gradientMiddle ? s.Position : Math.Max(k == 0 ? 0 : list[k - 1].Position + GradientRamp.MinGap * 1.001, Math.Min(k == list.Length - 1 ? 1 : list[k + 1].Position - GradientRamp.MinGap * 1.001, p));
                        double mid = gradientMiddle ? Math.Max(.01, Math.Min(.99, (p - s.Position) / (list[k + 1].Position - s.Position))) : s.Midpoint;
                        list[k] = new GradientOpacityStop(at, s.Opacity, mid); gradientDraft = gradientDraft.WithOpacities(list);
                    }
                    else
                    {
                        var list = gradientDraft.Colors.ToArray(); var s = list[k];
                        double at = gradientMiddle ? s.Position : Math.Max(k == 0 ? 0 : list[k - 1].Position + GradientRamp.MinGap * 1.001, Math.Min(k == list.Length - 1 ? 1 : list[k + 1].Position - GradientRamp.MinGap * 1.001, p));
                        double mid = gradientMiddle ? Math.Max(.01, Math.Min(.99, (p - s.Position) / (list[k + 1].Position - s.Position))) : s.Midpoint;
                        list[k] = new GradientStop(at, s.Color, mid); gradientDraft = gradientDraft.WithColors(list);
                    }
                    e.Use();
                }
                else if (dragging && e.rawType == EventType.MouseUp)
                {
                    bool outside = mouse.y < r.y - 16 || mouse.y > r.yMax + 16;
                    if (outside && !gradientMiddle)
                    {
                        int k = gradientIndex;
                        if (gradientAlpha && gradientDraft.Opacities.Count > 2) gradientDraft = gradientDraft.WithOpacities(gradientDraft.Opacities.Where((_, i) => i != k));
                        else if (!gradientAlpha && gradientDraft.Colors.Count > 2) gradientDraft = gradientDraft.WithColors(gradientDraft.Colors.Where((_, i) => i != k));
                    }
                    ramp = gradientDraft; GUIUtility.hotControl = 0; gradientControl = 0; gradientDraft = gradientOriginal = null; GUI.changed = true; e.Use();
                }
                else if (dragging && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                { ramp = gradientOriginal; GUIUtility.hotControl = 0; gradientControl = 0; gradientDraft = gradientOriginal = null; e.Use(); }
            }
            shown = gradientControl == id ? gradientDraft : ramp;
            if (e.type == EventType.Repaint)
            {
                Checker(new Rect(left, r.y + 27, width, 20), 5);
                for (int k = 0; k < 128; k++)
                { var c = shown.SampleStops(k / 127.0, scalar); Fill(new Rect(left + width * k / 128, r.y + 27, width / 128 + 1, 20), new Color32(c.R, c.G, c.B, c.A)); }
                void Mark(double p, float y, Color color, bool picked, bool diamond)
                {
                    var box = new Rect(left + (float)p * width - 4, r.y + y - 4, 8, 8);
                    if (diamond)
                    {
                        for (int row = -4; row <= 4; row++)
                        {
                            int half = 4 - Math.Abs(row); var center = box.center;
                            Fill(new Rect(center.x - half, center.y + row, half * 2 + 1, 1), Color.black);
                            if (half > 0) Fill(new Rect(center.x - half + 1, center.y + row, half * 2 - 1, 1), color);
                        }
                    }
                    else { Fill(box, color); Outline(box, picked ? Color.yellow : Color.black, picked ? 2 : 1, 0); }
                }
                for (int k = 0; k < shown.Colors.Count; k++)
                {
                    var s = shown.Colors[k]; Mark(s.Position, 67, new Color32(s.Color.R, s.Color.G, s.Color.B, 255), !alpha && selected == k, false);
                    if (k + 1 < shown.Colors.Count) Mark(s.Position + (shown.Colors[k + 1].Position - s.Position) * s.Midpoint, 54, Color.gray, false, true);
                }
                for (int k = 0; k < shown.Opacities.Count; k++)
                {
                    var s = shown.Opacities[k]; Mark(s.Position, 7, new Color((float)s.Opacity, (float)s.Opacity, (float)s.Opacity), alpha && selected == k, false);
                    if (k + 1 < shown.Opacities.Count) Mark(s.Position + (shown.Opacities[k + 1].Position - s.Position) * s.Midpoint, 20, Color.gray, false, true);
                }
            }
            Tooltip(r, L.Tr("Top: opacity stops. Bottom: color or value stops. Click to add, drag to move, right-click or drag outside to remove. Small diamonds move the midpoint. Escape cancels a drag."));
            return ramp;
        }
    }
}
