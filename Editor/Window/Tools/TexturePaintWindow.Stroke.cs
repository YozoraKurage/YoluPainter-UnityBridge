using System;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>手ぶれ補正（引っ張る糸）と入り抜き の欄。2D キャンバスのストロークに効く（3D ビューのブラシは面の上のダブで描くので効かない）。</summary>
    public sealed partial class TexturePaintWindow
    {
        void StrokeAssistSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-stroke", L.Tr("Stabilizer & Taper"), "ink_stroke")) return;
            brush.stabilizer = PaintGui.FitSlider(rows.Row(), L.Tr("Stabilizer"), brush.stabilizer, 0, 100, "0", " px",
                L.Tr("The brush trails the pointer by this string length, smoothing shaky lines (Krita's / Lazy Nezumi's pulled string). The line is finished to the pointer when you lift. 2D canvas only."));
            var c = UiRows.Split(rows.Row(), 2, 6);
            brush.taperIn = PaintGui.FitSlider(c[0], L.Tr("Taper in"), brush.taperIn, 0, 500, "0", " px", L.Tr("The brush grows from nothing over this much stroke length."));
            brush.taperOut = PaintGui.FitSlider(c[1], L.Tr("Taper out"), brush.taperOut, 0, 500, "0", " px", L.Tr("The brush shrinks to nothing over the last this much stroke length. That part appears when the stroke ends."));
            rows.Space(4);
        }

        internal float Stabilizer { get => brush.stabilizer; set => brush.stabilizer = value; }

        /// <summary>ブラシのストローク（2D キャンバスと 3D ビュー）: 押して始め、ドラッグで足し、離して確定する。</summary>
        void HandleBrushInput(Event e)
        {
            if(tool!=PaintTool.Brush&&e.type==EventType.MouseDown&&surfaceRect.Contains(e.mousePosition)&&e.button==0&&!e.alt){message=tool+" works on the 2D canvas. Use the brush, a selection tool or the bucket on the 3D view.";e.Use();return;}
            if(e.type==EventType.MouseDown && e.button==0 && !e.alt && (canvasRect.Contains(e.mousePosition)||surfaceRect.Contains(e.mousePosition)))
            {
                surfaceStroke=surfaceRect.Contains(e.mousePosition);
                if(surfaceStroke && !preview.CanPaint){message="This preview snapshot is not safe to paint. See its load diagnostics.";return;}
                TryAction(()=>
                {
                    RememberColor();
                    if(EditingMask) stroke=document.BeginMaskStroke(selectedLayer,GetBrush());
                    else
                    {
                        if(document.GetLayer(selectedLayer).IsGroup) throw new InvalidOperationException("A group has no pixels. Select a layer inside it to paint, or paint the group's mask.");
                        if(!document.GetLayer(selectedLayer).IsChannelEnabled(channel)) document.SetChannelEnabled(selectedLayer,channel,true);
                        stroke=document.BeginStroke(selectedLayer,channel,GetBrush());
                    }
                    previousPointer=e.mousePosition; previousPressure=Pressure(e);
                    PaintAt(e.mousePosition,previousPressure); GUIUtility.hotControl=GUIUtility.GetControlID(FocusType.Passive);
                });
                e.Use();Repaint();
            }
            else if(stroke!=null && e.type==EventType.MouseDrag && e.button==0)
            {
                TryAction(()=>
                {
                    float pressure=Pressure(e);
                    if(surfaceStroke)
                    {
                        float worldRadius=Mathf.Max(.000001f,preview.Bounds.size.magnitude)*brush.radius/document.Width;
                        float spacing=1;
                        if(preview.TryPick(surfaceRect,previousPointer,out var previousHit))spacing=Mathf.Max(.5f,2*preview.WorldRadiusToGuiPoints(previousHit.Position,worldRadius)*brush.spacing);
                        int steps=Mathf.Max(1,Mathf.CeilToInt(Vector2.Distance(previousPointer,e.mousePosition)/spacing));
                        if(steps>128)throw new InvalidOperationException("Surface input exceeded per-event budget; stroke canceled without partial edits. Reduce brush size or move more slowly.");
                        for(int i=1;i<=steps;i++) PaintAt(Vector2.Lerp(previousPointer,e.mousePosition,i/(float)steps),Mathf.Lerp(previousPressure,pressure,i/(float)steps));
                    }
                    else PaintAt(e.mousePosition,pressure);
                    previousPointer=e.mousePosition; previousPressure=pressure;
                });
                e.Use();Repaint();
            }
            else if(stroke!=null && (e.type==EventType.MouseUp || e.rawType==EventType.MouseUp)){FinishStroke(true);e.Use();Repaint();}
        }
        float Pressure(Event e) => Mathf.Clamp01(brush.pressureCurve.Evaluate(Mathf.Clamp01(e.pressure)));
        void PaintAt(Vector2 pointer,float pressure)
        {
            if(surfaceStroke)
            {
                if(brush.pressureSize && pressure<=0)return;
                if(!surfaceRect.Contains(pointer)||!preview.TryPick(surfaceRect,pointer,out var hit)||hit.MaterialSlot!=materialSlot)return;
                float radius=Mathf.Max(.000001f,preview.Bounds.size.magnitude)*brush.radius/document.Width*(brush.pressureSize?Mathf.Max(.001f,pressure):1);
                var dab=preview.BuildSurfaceDabs(hit,radius,document.Width,document.Height,brush.hardness);
                if(dab.WasClipped)throw new InvalidOperationException(dab.Diagnostic);
                if(!String.IsNullOrEmpty(dab.Diagnostic))message=dab.Diagnostic;
                foreach(var pixel in dab.Pixels)stroke.ApplyPixel(pixel.X,pixel.Y,pixel.Coverage,pressure);
            }
            else
            {
                var image=ImageRect();
                stroke.Add(PenSample((pointer.x-image.x)/image.width*document.Width,(1-(pointer.y-image.y)/image.height)*document.Height,pressure));
            }
            repaintPixels=true;
        }
        void FinishStroke(bool commit)
        {
            if(stroke==null)return;
            try{if(commit)stroke.Commit();else stroke.Cancel();}catch(Exception ex){message=ex.Message;stroke.Cancel();}
            finally{stroke.Dispose();stroke=null;GUIUtility.hotControl=0;repaintPixels=true;}
        }
    }
}
