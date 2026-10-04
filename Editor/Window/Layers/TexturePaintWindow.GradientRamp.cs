using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        int rampSelected; bool rampAlpha; string rampSelectionKey = "", rampCurveKey = "";
        int rampCurveControl;
        bool GradientDraftActive => PaintGui.GradientDragging || rampCurveControl != 0 && GUIUtility.hotControl == rampCurveControl;
        AnimationCurve rampCurveDraft; GradientRamp rampCurveBase;
        void CancelGradientDrafts()
        {
            if (rampCurveControl != 0 && GUIUtility.hotControl == rampCurveControl) PaintGui.CancelCurveEditor();
            rampCurveControl = 0; rampCurveKey = ""; rampCurveDraft = null; rampCurveBase = null; PaintGui.CancelGradientEditor();
        }
        internal static string RampPresetName(GradientPreset p)
        {
            switch (p)
            {
                case GradientPreset.BlackWhite: return L.Tr("Black to white");
                case GradientPreset.WhiteBlack: return L.Tr("White to black");
                case GradientPreset.ForegroundBackground: return L.Tr("Foreground to background");
                case GradientPreset.ForegroundTransparent: return L.Tr("Foreground to transparent");
                default: return L.Tr("Warm to cool");
            }
        }
        /// <summary>The one property-panel entry for stops, midpoints, presets and the value curve (generator and direct fill).
        /// Stop and curve drags stay in the editor draft until release, so Escape does not create an undo step.</summary>
        GeneratorSettings GradientRampRows(UiRows rows, string key, GeneratorSettings next, Action<GeneratorSettings> apply, Func<GeneratorSettings> current, bool scalar, float indent = 0, bool optional = true)
        {
            if (key != rampSelectionKey) { rampSelected = 0; rampAlpha = false; rampSelectionKey = key; }
            var ramp = next.Ramp;
            if (optional)
            {
                bool enabled = PaintGui.FitToggle(Spot("gradient.enabled", Indent(rows.Row(), indent)), L.Tr("Color / Value Gradient"), ramp != null,
                    L.Tr("Map the shape's value through color or value stops and independent opacity stops. Off keeps the original scalar generator.")
                    + "\n" + L.Tr("Colours mix in sRGB; data channels use the stops' luminance; opacity is separate."));
                if (enabled != (ramp != null)) { ramp = enabled ? GradientRamp.Default : null; next = next.WithRamp(ramp); }
            }
            if (ramp == null) return next;
            var capturedDocument = document; var capturedLayer = selectedLayer;
            void Commit(GradientRamp picked)
            {
                if (document != capturedDocument || selectedLayer != capturedLayer || stroke != null) return;
                var g = current(); if (g == null) return;
                document.EndCoalescing(); apply(g.WithRamp(picked)); document.EndCoalescing(); Repaint();
            }
            if (PaintGui.Button(Spot("gradient.presets", Indent(rows.Row(), indent)), L.Tr("Gradient Presets…")))
            {
                var menu = new PaintMenu();
                foreach (GradientPreset p in Enum.GetValues(typeof(GradientPreset)))
                { var preset = p; menu.AddItem(new GUIContent(RampPresetName(p)), false, () => { var fg = (Color32)brush.color; var bg = (Color32)brush.secondaryColor; Commit(GradientRamp.Preset(preset, new Rgba32(fg.r, fg.g, fg.b, fg.a), new Rgba32(bg.r, bg.g, bg.b, bg.a))); }); }
                menu.ShowAsContext();
            }
            ramp = PaintGui.GradientEditor(Spot("gradient.stops", Indent(rows.Row(76), indent)), ramp, ref rampSelected, ref rampAlpha, scalar);
            int count = rampAlpha ? ramp.Opacities.Count : ramp.Colors.Count; rampSelected = Math.Max(0, Math.Min(count - 1, rampSelected));
            double position = rampAlpha ? ramp.Opacities[rampSelected].Position : ramp.Colors[rampSelected].Position;
            double min = rampSelected == 0 ? 0 : (rampAlpha ? ramp.Opacities[rampSelected - 1].Position : ramp.Colors[rampSelected - 1].Position) + GradientRamp.MinGap * 1.001;
            double max = rampSelected == count - 1 ? 1 : (rampAlpha ? ramp.Opacities[rampSelected + 1].Position : ramp.Colors[rampSelected + 1].Position) - GradientRamp.MinGap * 1.001;
            double at = PaintGui.KeepSlider(Spot("gradient.position", Indent(rows.Row(), indent)), L.Tr("Stop Position"), position, min, max, "0.##", "%", null, true, 100);
            if (rampAlpha)
            {
                var list = ramp.Opacities.ToArray(); var s = list[rampSelected];
                double value = PaintGui.KeepSlider(Spot("gradient.opacity", Indent(rows.Row(), indent)), L.Tr("Stop Opacity"), s.Opacity, 0, 1, "0.##", "%", null, true, 100);
                double mid = rampSelected == count - 1 ? s.Midpoint : PaintGui.KeepSlider(Spot("gradient.midpoint", Indent(rows.Row(), indent)), L.Tr("Segment Midpoint"), s.Midpoint, .01, .99, "0.##", "%", null, true, 100);
                if (at != s.Position || value != s.Opacity || mid != s.Midpoint) { list[rampSelected] = new GradientOpacityStop(at, value, mid); ramp = ramp.WithOpacities(list); }
            }
            else
            {
                var list = ramp.Colors.ToArray(); var s = list[rampSelected]; var color = s.Color;
                var row = Indent(rows.Row(), indent);
                if (scalar)
                {
                    double value = PaintGui.KeepSlider(Spot("gradient.value", row), L.Tr("Stop Value"), (.2126 * color.R + .7152 * color.G + .0722 * color.B) / 255, 0, 1, "0.###", "");
                    if (value != (.2126 * color.R + .7152 * color.G + .0722 * color.B) / 255) { byte v = (byte)Mathf.RoundToInt((float)value * 255); color = new Rgba32(v, v, v, 255); }
                }
                else
                {
                    int stop = rampSelected;
                    PaintGui.ColorSwatch(Spot("gradient.color", row), new Color32(color.R, color.G, color.B, 255), c =>
                    {
                        if (document != capturedDocument || selectedLayer != capturedLayer || stroke != null) return;
                        var g = current(); if (g?.Ramp == null || stop >= g.Ramp.Colors.Count) return; var stops = g.Ramp.Colors.ToArray(); var prior = stops[stop]; var b = (Color32)c;
                        stops[stop] = new GradientStop(prior.Position, new Rgba32(b.r, b.g, b.b, 255), prior.Midpoint); Commit(g.Ramp.WithColors(stops));
                    }, false, L.Tr("Choose this stop's color"));
                }
                double mid = rampSelected == count - 1 ? s.Midpoint : PaintGui.KeepSlider(Spot("gradient.midpoint", Indent(rows.Row(), indent)), L.Tr("Segment Midpoint"), s.Midpoint, .01, .99, "0.##", "%", null, true, 100);
                if (at != s.Position || color != s.Color || mid != s.Midpoint) { list[rampSelected] = new GradientStop(at, color, mid); ramp = ramp.WithColors(list); }
            }
            if (PaintGui.Button(Spot("gradient.remove", Indent(rows.Row(), indent)), L.Tr("Remove Stop"), false, GUI.enabled && count > 2))
                ramp = rampAlpha ? ramp.WithOpacities(ramp.Opacities.Where((_, i) => i != rampSelected)) : ramp.WithColors(ramp.Colors.Where((_, i) => i != rampSelected));
            PaintGui.GroupLabel(Indent(rows.Row(16), indent), L.Tr("Value Curve"));
            var curveRamp = ramp;
            if (PaintGui.Button(Spot("gradient.curve.presets", Indent(rows.Row(), indent)), L.Tr("Curve Presets…")))
            {
                var menu = new PaintMenu();
                for (int i = 0; i < PressureCurve.Presets.Length; i++) { int k = i; menu.AddItem(new GUIContent(CurvePresetTitle(k)), false, () => Commit(curveRamp.WithCurve(PressureCurve.Presets[k].Points.Select(p => new GradientCurvePoint(p.x, p.y))))); }
                menu.ShowAsContext();
            }
            if (rampCurveKey != key || !Equals(rampCurveBase, ramp)) { rampCurveDraft = PressureCurve.FromPoints(ramp.Curve.Select(p => new Vector2((float)p.X, (float)p.Y)).ToList()); rampCurveBase = ramp; rampCurveKey = key; }
            var beforeCurve = rampCurveDraft;
            rampCurveDraft = PaintGui.CurveEditor(Spot("gradient.curve", Indent(rows.Row(PressureCurveHeight), indent)), rampCurveDraft,
                L.Tr("Shape value in, gradient position out. Click to add a point, drag to move, right-click to remove. Escape cancels."));
            if (!ReferenceEquals(beforeCurve, rampCurveDraft) && GUIUtility.hotControl != 0) rampCurveControl = GUIUtility.hotControl;
            if (GUIUtility.hotControl == 0 && (!ReferenceEquals(beforeCurve, rampCurveDraft) || !PressureCurve.Matches(rampCurveDraft, ramp.Curve.Select(p => new Vector2((float)p.X, (float)p.Y)).ToArray())))
            {
                var changed = ramp.WithCurve(PressureCurve.Points(rampCurveDraft).Select(p => new GradientCurvePoint(p.x, p.y)));
                if (!changed.Equals(ramp)) ramp = changed;
                rampCurveBase = ramp;
            }
            return next.WithRamp(ramp);
        }
    }
}
