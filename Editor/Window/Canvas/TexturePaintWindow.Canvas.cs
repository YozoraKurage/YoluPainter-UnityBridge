using System.Linq;
using Yozolab.YoluPainter.Core;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>2D キャンバス: 表示（拡大・パン）と、キャンバスと 3D ビューへの入力の振り分け。</summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>OnGUI が文書を表示のために合成した回数（Repaint のときだけ合成する。テストがそれを確かめる）。</summary>
        internal int CompositeCount { get; private set; }

        Rect ImageRect()
        {
            float fit=Mathf.Min(canvasRect.width/document.Width,canvasRect.height/document.Height)*canvasZoom;
            float width=document.Width*fit,height=document.Height*fit;
            return new Rect(canvasRect.center.x-width*.5f+canvasPan.x,canvasRect.center.y-height*.5f+canvasPan.y,width,height);
        }
        void DrawCanvas()
        {
            EditorGUI.DrawRect(canvasRect,PaintTheme.CanvasBg);
            var pointer=Event.current.mousePosition-canvasRect.position; // クリップの中の座標（クリップに入る前に取る）
            GUI.BeginClip(canvasRect);
            var image=ImageRect(); image.position-=canvasRect.position;
            if(DisplayTexture!=null) EditorGUI.DrawTextureTransparent(image,DisplayTexture,ScaleMode.StretchToFill);
            DrawMeshMapOverlay(image);
            if(document.Selection!=null){EnsureSelectionOverlay(); GUI.DrawTexture(image,selectionOverlay,ScaleMode.StretchToFill,true);}
            if(toolDragging&&Event.current.type==EventType.Repaint) DrawToolPreview(image);
            else if(tool==PaintTool.Move&&!toolDragging&&Event.current.type==EventType.Repaint) DrawTransformHandles(image);
            if(Event.current.type==EventType.Repaint) DrawCanvasPathMarkers(image);
            DrawCanvasBrushCursor(image,pointer);
            GUI.EndClip();
        }
        Vector2 ToGui(Rect image,Vector2 p)=>new Vector2(image.x+p.x/document.Width*image.width,image.y+(1-p.y/document.Height)*image.height);
        /// <summary>GUI の座標をキャンバスの画素座標（左下原点、範囲外も返す）に。</summary>
        Vector2 CanvasPoint(Vector2 pointer){var image=ImageRect();return new Vector2((pointer.x-image.x)/image.width*document.Width,(1-(pointer.y-image.y)/image.height)*document.Height);}
        void HandleCanvasInput(Event e)
        {
            if(preview.HasModel && stroke==null && preview.HandleNavigation(surfaceRect,e)){Repaint();return;}
            if(canvasRect.Contains(e.mousePosition))
            {
                if(e.type==EventType.ScrollWheel){canvasZoom=Mathf.Clamp(canvasZoom*Mathf.Exp(-e.delta.y*.07f),.2f,16);e.Use();Repaint();return;}
                if(e.type==EventType.MouseDrag && e.button==2){canvasPan+=e.delta;e.Use();Repaint();return;}
            }
            if(stroke==null&&HandleToolInput(e))return;
            if(stroke==null&&HandlePathTool(e))return;
            if(stroke==null&&HandleSurfaceTool(e))return;
            HandleBrushInput(e);
        }
    }
}
