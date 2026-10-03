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
        bool cloneMask, cloneOffsetValid;
        Vector2 cloneSource, cloneOffset, cloneStrokeOffset;
        int cloneWidth, cloneHeight;
        long effectSurfaceIsland = -1;
        internal bool HasCloneSource => ReferenceEquals(cloneDocument, document) && cloneWidth == document.Width && cloneHeight == document.Height && cloneLayer == selectedLayer && cloneMask == EditingMask;
        internal Vector2 CloneSource => cloneSource;
        internal Vector2 CloneOffset => cloneOffset;

        bool HandleCloneSourceInput(Event e)
        {
            if (tool != PaintTool.Clone || stroke != null || e.type != EventType.MouseDown || e.button != 0 || !e.alt) return false;
            if (surfaceRect.Contains(e.mousePosition)) message = L.Tr("Clone Stamp uses the 2D canvas. A UV offset cannot identify the source across 3D seams.");
            else if (canvasRect.Contains(e.mousePosition))
            {
                var p = CanvasPoint(e.mousePosition);
                if (p.x >= 0 && p.y >= 0 && p.x < document.Width && p.y < document.Height)
                {
                    // 画素の中心に固定する（クリックの小数部分でクローンがぼけない）。
                    cloneSource = new Vector2(Mathf.Floor(p.x) + .5f, Mathf.Floor(p.y) + .5f);
                    cloneDocument = document; cloneLayer = selectedLayer; cloneMask = EditingMask; cloneWidth = document.Width; cloneHeight = document.Height; cloneOffsetValid = false;
                    message = L.Tr("Clone source set. Paint to copy the current layer; Alt-click sets a new source.");
                }
            }
            else return false;
            e.Use(); Repaint(); return true;
        }
        bool PrepareBrushEffectStroke(Vector2 pointer, bool onSurface)
        {
            if (onSurface && tool == PaintTool.Clone) { message = L.Tr("Clone Stamp uses the 2D canvas. A UV offset cannot identify the source across 3D seams."); return false; }
            if ((onSurface ? HasSurfaceSymmetry : brush.canvasSymmetry != CanvasSymmetryMode.None) && (tool == PaintTool.Smudge || tool == PaintTool.Clone))
            { message = L.Tr("Smudge and Clone need a separate source and motion for each symmetry copy. Turn off symmetry to use them."); return false; }
            if (tool == PaintTool.Clone)
            {
                if (!HasCloneSource) { message = L.Tr("Alt-click the 2D canvas to set a clone source on this layer or mask first."); return false; }
                var p = CanvasPoint(pointer);
                p = new Vector2(Mathf.Floor(p.x) + .5f, Mathf.Floor(p.y) + .5f);
                cloneStrokeOffset = brush.cloneAligned && cloneOffsetValid ? cloneOffset : cloneSource - p;
            }
            return true;
        }
        void ApplySurfaceEffect(SurfaceDabResult dab, SurfaceHit hit, float pressure)
        {
            if (tool == PaintTool.Smudge)
            {
                long island = RegionIndex().Key(hit.TriangleIndex, SurfaceRegionKind.UvIsland);
                if (island != effectSurfaceIsland) { stroke.ResetEffectDirection(); effectSurfaceIsland = island; }
            }
            var pixels = new List<BrushPixel>(dab.Pixels.Count);
            foreach (var p in dab.Pixels) pixels.Add(new BrushPixel(p.X, p.Y, p.Coverage));
            stroke.ApplyDab(pixels, hit.UV.x * document.Width, hit.UV.y * document.Height, pressure);
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
        void BrushEffectSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-effect", tool == PaintTool.Blur ? L.Tr("Blur Brush") : tool == PaintTool.Smudge ? L.Tr("Smudge") : L.Tr("Clone Stamp"), tool == PaintTool.Clone ? "content_copy" : "blur_on")) return;
            if (tool == PaintTool.Blur)
            {
                brush.blurRadius = PaintGui.FitIntSlider(Mark("blur-radius", rows.Row()), L.Tr("Blur radius"), brush.blurRadius, 1, 64, " px");
                PaintGui.Paragraph(rows, L.Tr("Alpha-weighted box blur of the current layer or mask. Transparent pixels keep their RGB. Opacity, flow and pressure set the strength."), PaintTheme.TextDim);
            }
            else if (tool == PaintTool.Smudge)
            {
                brush.smudgeStrength = PercentSlider(Mark("smudge-strength", rows.Row()), L.Tr("Smudge strength"), brush.smudgeStrength, 0, 1);
                PaintGui.Paragraph(rows, L.Tr("Each dab pulls pixels from the previous dab. In 3D this follows UV motion within one island; crossing a seam starts a new pickup. Mirror symmetry is unavailable."), PaintTheme.TextDim);
            }
            else
            {
                bool aligned = PaintGui.FitToggle(Mark("clone-aligned", rows.Row()), L.Tr("Aligned"), brush.cloneAligned);
                if (aligned != brush.cloneAligned) { brush.cloneAligned = aligned; cloneOffsetValid = false; }
                PaintGui.ValueBox(rows.Row(), L.Tr("Sample"), L.Tr("Current layer"), LabelColumn);
                PaintGui.Paragraph(rows, L.Tr("Alt-click sets the source on this layer or mask. Aligned keeps the offset between strokes; off restarts at the source each stroke. 2D canvas only."), PaintTheme.TextDim);
                PaintGui.Notice(rows, HasCloneSource ? L.Tr("Clone source ready") : L.Tr("Set the source with Alt-click"), "target", PaintTheme.TextDim);
            }
            rows.Space(4);
        }
    }
}
