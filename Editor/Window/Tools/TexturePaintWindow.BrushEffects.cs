using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        bool IsBrushTool => tool == PaintTool.Brush || tool == PaintTool.Blur || tool == PaintTool.Smudge || tool == PaintTool.Clone;
        BrushEffect CurrentBrushEffect => tool == PaintTool.Blur ? BrushEffect.Blur : tool == PaintTool.Smudge ? BrushEffect.Smudge : tool == PaintTool.Clone ? BrushEffect.Clone : BrushEffect.Paint;
        PaintDocument cloneDocument;
        Guid cloneLayer;
        bool cloneMask, cloneOffsetValid, cloneOnSurface;
        Vector2 cloneSource, cloneOffset, cloneStrokeOffset;
        int cloneWidth, cloneHeight;
        SurfaceHit cloneSurfaceSource, cloneSurfaceDestination, cloneStrokeDestination;
        SurfaceHit? effectPreviousHit;
        SurfaceGeometry cloneGeometry;
        internal bool HasSurfaceCloneSource => HasCloneSource && cloneOnSurface;
        // テクスチャセットのマテリアル集合への移行時は、この関数を共通の問い合わせに置き換える。
        bool IsEffectSurfaceTarget(SurfaceHit hit) => PaintsSlot(hit.MaterialSlot);

        internal bool HasCloneSource => ReferenceEquals(cloneDocument, document) && cloneWidth == document.Width && cloneHeight == document.Height && cloneLayer == selectedLayer && cloneMask == EditingMask && (!cloneOnSurface || (ReferenceEquals(cloneGeometry, preview.Geometry) && preview.IsVisible(cloneSurfaceSource.RendererIndex, cloneSurfaceSource.MaterialSlot)));
        internal Vector2 CloneSource => cloneSource;
        internal Vector2 CloneOffset => cloneOffset;

        bool HandleCloneSourceInput(Event e)
        {
            if (tool != PaintTool.Clone || stroke != null || e.type != EventType.MouseDown || e.button != 0 || !e.alt) return false;
            bool set = false;
            if (surfaceRect.Contains(e.mousePosition))
            {
                if (preview.TryPick(surfaceRect, e.mousePosition, out var hit) && IsEffectSurfaceTarget(hit))
                { cloneSurfaceSource = hit; cloneGeometry = preview.Geometry; cloneOnSurface = true; cloneSource = hit.UV * new Vector2(document.Width, document.Height); set = true; }
                else message = L.Tr("Set the clone source on a surface of the active texture set.");
            }
            else if (canvasRect.Contains(e.mousePosition))
            {
                var p = CanvasPoint(e.mousePosition);
                if (p.x >= 0 && p.y >= 0 && p.x < document.Width && p.y < document.Height)
                { cloneSource = new Vector2(Mathf.Floor(p.x) + .5f, Mathf.Floor(p.y) + .5f); cloneOnSurface = false; set = true; }
            }
            else return false;
            if (set)
            {
                cloneDocument = document; cloneLayer = selectedLayer; cloneMask = EditingMask; cloneWidth = document.Width; cloneHeight = document.Height; cloneOffsetValid = false;
                message = L.Tr("Clone source set. Alt-click sets a new source in the same view.");
            }
            e.Use(); Repaint(); return true;
        }
        bool PrepareBrushEffectStroke(Vector2 pointer, bool onSurface)
        {
            if ((onSurface ? HasSurfaceSymmetry : brush.canvasSymmetry != CanvasSymmetryMode.None) && (tool == PaintTool.Smudge || tool == PaintTool.Clone))
            { message = L.Tr("Smudge and Clone need a separate source and motion for each symmetry copy. Turn off symmetry to use them."); return false; }
            if (tool != PaintTool.Clone) return true;
            if (!HasCloneSource || cloneOnSurface != onSurface)
            { message = L.Tr("Alt-click this view to set a clone source on this layer or mask first."); return false; }
            if (onSurface)
            {
                if (!preview.TryPick(surfaceRect, pointer, out var hit) || !IsEffectSurfaceTarget(hit)) return false;
                cloneStrokeDestination = brush.cloneAligned && cloneOffsetValid ? cloneSurfaceDestination : hit;
            }
            else
            {
                var p = CanvasPoint(pointer); p = new Vector2(Mathf.Floor(p.x) + .5f, Mathf.Floor(p.y) + .5f);
                cloneStrokeOffset = brush.cloneAligned && cloneOffsetValid ? cloneOffset : cloneSource - p;
            }
            return true;
        }
        void BeginBrushEffectStroke()
        {
            effectPreviousHit = null;
            if (tool == PaintTool.Clone && brush.cloneAllLayers && !EditingMask)
            {
                stroke.UseCompositeCloneSource();
            }
        }
        void EndBrushEffectStroke() { effectPreviousHit = null; }
        void CommitCloneAlignment()
        {
            if (surfaceStroke) cloneSurfaceDestination = cloneStrokeDestination; else cloneOffset = cloneStrokeOffset;
            cloneOffsetValid = true;
        }
        void ApplySurfaceEffect(SurfaceDabResult dab, SurfaceHit hit, float pressure)
        {
            try { ApplySurfaceEffectCore(dab, hit, pressure); }
            catch (InvalidOperationException ex) { throw new InvalidOperationException(L.Tr(ex.Message), ex); }
        }
        void ApplySurfaceEffectCore(SurfaceDabResult dab, SurfaceHit hit, float pressure)
        {
            if (tool == PaintTool.Blur)
            {
                var pixels = new List<BrushPixel>(dab.Pixels.Count);
                foreach (var p in dab.Pixels) pixels.Add(new BrushPixel(p.X, p.Y, p.Coverage));
                stroke.ApplyDab(pixels, hit.UV.x * document.Width, hit.UV.y * document.Height, pressure, SurfaceStencilPoints(dab, hit)); return;
            }
            SurfaceHit destination, source;
            if (tool == PaintTool.Smudge)
            {
                if (!effectPreviousHit.HasValue) { effectPreviousHit = hit; return; }
                source = effectPreviousHit.Value; effectPreviousHit = hit; destination = hit;
                if ((source.Position - hit.Position).sqrMagnitude < 1e-20f) return;
            }
            else { source = cloneSurfaceSource; destination = cloneStrokeDestination; }
            // 面の来歴と全画素の参照を予算に数え、書く前にまとめて凍結する。
            long bytes = (long)dab.Pixels.Count * 28;
            float radius = Mathf.Max(.000001f, preview.Bounds.size.magnitude) * brush.radius / document.Width;
            float reach = radius * 2 + (source.Position - destination.Position).magnitude * (tool == PaintTool.Smudge ? 2 : 0)
                + (hit.Position - destination.Position).magnitude * 2;
            long available = document.ActiveStrokeBudgetBytes - stroke.RollbackBytes - bytes
                - (long)dab.Pixels.Count * (160 + stroke.TargetCount * 4 + (strokeStencilFrame.HasValue ? 24 : 0));
            if (available <= 0) throw new InvalidOperationException(L.Tr("Surface sampling exceeded its memory budget. Stroke canceled."));
            var destChart = preview.PickingGeometry.BuildSamplingChart(destination, reach, maxTriangles: preview.BrushBudget.MaxTriangles, maxBytes: available);
            bytes += destChart.NominalBytes; available -= destChart.NominalBytes;
            SurfaceGeometry.SamplingChart sourceChart; Vector2 offset;
            if (tool == PaintTool.Smudge)
            {
                sourceChart = destChart;
                if (!destChart.TryCoordinates(source, out offset))
                { message = L.Tr("Smudge picked up again on a disconnected surface."); return; }
            }
            else
            {
                Vector3 tangent = Vector3.ProjectOnPlane(Vector3.right, destination.Normal);
                if (tangent.sqrMagnitude < 1e-12f) tangent = Vector3.ProjectOnPlane(Vector3.up, destination.Normal);
                tangent = Quaternion.FromToRotation(destination.Normal, source.Normal) * tangent;
                sourceChart = preview.PickingGeometry.BuildSamplingChart(source, reach, tangent, preview.BrushBudget.MaxTriangles, available);
                bytes += sourceChart.NominalBytes; offset = Vector2.zero;
            }
            var plan = new List<BrushMappedPixel>();
            var stencilPoints = strokeStencilFrame.HasValue ? new List<StencilPoint>() : null;
            double footprint = stencilPoints == null ? 0 : SurfaceStencilFootprint(hit);
            foreach (var p in dab.Pixels)
            {
                if (!destChart.TryCoordinates(p, out var point)) throw new InvalidOperationException(L.Tr("Surface sampling could not reach this dab. Start a new stroke."));
                if (!sourceChart.TrySample(point + offset, new BrushPixel(p.X, p.Y, p.Coverage), document.Width, document.Height, out var mapped)) continue;
                plan.Add(mapped); stencilPoints?.Add(SurfaceStencilPoint(p.Position, footprint));
            }
            stroke.ApplyMappedDab(plan, pressure, bytes, stencilPoints);
        }
        void DrawCloneSource(CanvasView view)
        {
            if (tool != PaintTool.Clone || !HasCloneSource) return;
            var p = view.ToGui(cloneSource.x, cloneSource.y);
            var old = Handles.color; Handles.color = PaintTheme.Accent;
            Handles.DrawAAPolyLine(2, new Vector3(p.x - 6, p.y), new Vector3(p.x + 6, p.y));
            Handles.DrawAAPolyLine(2, new Vector3(p.x, p.y - 6), new Vector3(p.x, p.y + 6));
            Handles.color = old;
        }
        void DrawSurfaceCloneSource()
        {
            if (tool != PaintTool.Clone || !HasSurfaceCloneSource || !preview.TryWorldToGui(surfaceRect, cloneSurfaceSource.Position, out var p)) return;
            if (!surfaceRect.Contains(p)) return;
            var old = Handles.color; Handles.color = PaintTheme.Accent;
            Handles.DrawAAPolyLine(2, new Vector3(p.x - 6, p.y), new Vector3(p.x + 6, p.y));
            Handles.DrawAAPolyLine(2, new Vector3(p.x, p.y - 6), new Vector3(p.x, p.y + 6)); Handles.color = old;
        }
        void BrushEffectSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-effect", tool == PaintTool.Blur ? L.Tr("Blur Brush") : tool == PaintTool.Smudge ? L.Tr("Smudge") : L.Tr("Clone Stamp"), tool == PaintTool.Clone ? "content_copy" : "blur_on")) return;
            if (tool == PaintTool.Blur)
            {
                brush.blurRadius = PaintGui.FitIntSlider(Mark("blur-radius", rows.SliderRow()), L.Tr("Blur radius"), brush.blurRadius, 1, 64, " px");
                PaintGui.Paragraph(rows, L.Tr("Alpha-weighted box blur of the current layer or mask. Transparent pixels keep their RGB. Opacity, flow and pressure set the strength."), PaintTheme.TextDim);
            }
            else if (tool == PaintTool.Smudge)
            {
                brush.smudgeStrength = PercentSlider(Mark("smudge-strength", rows.SliderRow()), L.Tr("Smudge strength"), brush.smudgeStrength, 0, 1);
                PaintGui.Paragraph(rows, L.Tr("Each dab pulls pixels from the previous dab. In 3D it follows connected faces across UV seams and mirrored islands. Disconnected surfaces start a new pickup."), PaintTheme.TextDim);
            }
            else
            {
                bool aligned = PaintGui.FitToggle(Mark("clone-aligned", rows.Row()), L.Tr("Aligned"), brush.cloneAligned);
                if (aligned != brush.cloneAligned) { brush.cloneAligned = aligned; cloneOffsetValid = false; }
                if (EditingMask) PaintGui.ValueBox(rows.Row(), L.Tr("Sample"), L.Tr("Current layer"), LabelColumn);
                else brush.cloneAllLayers = PaintGui.FitToggle(Mark("clone-all-layers", rows.Row()), L.Tr("Sample all visible layers"), brush.cloneAllLayers);
                PaintGui.Paragraph(rows, L.Tr("Alt-click sets the source in the 2D or 3D view. Aligned keeps the relation between strokes; off restarts at the source. All visible layers samples the channel composite before the stroke."), PaintTheme.TextDim);
                PaintGui.Notice(rows, HasCloneSource ? L.Tr("Clone source ready") : L.Tr("Set the source with Alt-click"), "target", PaintTheme.TextDim);
            }
            rows.Space(4);
        }
    }
}
