using System.Linq;
using Yozolab.YoluPainter.Core;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>2D キャンバス: 表示（拡大・パン・回転・左右反転。写しは CanvasView、操作は TexturePaintWindow.CanvasView.cs）と、キャンバスと 3D ビューへの入力の振り分け。</summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>OnGUI が文書を表示のために合成した回数（Repaint のときだけ合成する。テストがそれを確かめる）。</summary>
        internal int CompositeCount { get; private set; }

        void DrawCanvas()
        {
            EditorGUI.DrawRect(canvasRect,PaintTheme.CanvasBg);
            var pointer=Event.current.mousePosition-canvasRect.position; // クリップの中の座標（クリップに入る前に取る）
            GUI.BeginClip(canvasRect);
            var view=CanvasViewInClip(); var image=view.Image;
            // 回っている・反転しているときは、画像（と重ねるテクスチャ）を GL の行列で回す（CanvasView.ImageMatrix）。線と印は写した座標で描く
            bool turned=!view.AxisAligned&&Event.current.type==EventType.Repaint;
            if(turned){GL.PushMatrix();GL.modelview=GL.modelview*view.ImageMatrix();}
            try
            {
                if(DisplayTexture!=null) EditorGUI.DrawTextureTransparent(image,DisplayTexture,ScaleMode.StretchToFill);
                DrawMeshMapOverlay(image);
                if(document.Selection!=null){EnsureSelectionOverlay(); GUI.DrawTexture(image,selectionOverlay,ScaleMode.StretchToFill,true);}
            }
            finally{if(turned)GL.PopMatrix();}
            DrawUvWireframe(view); // 今のテクスチャセットの UV（Canvas/TexturePaintWindow.UvWireframe.cs）
            if(toolDragging&&Event.current.type==EventType.Repaint) DrawToolPreview(view);
            else if(tool==PaintTool.Move&&!toolDragging&&Event.current.type==EventType.Repaint) DrawTransformHandles(view);
            if(Event.current.type==EventType.Repaint) DrawCanvasPathMarkers(view);
            DrawCanvasBrushCursor(view,pointer);
            GUI.EndClip();
            if(rotateKeyHeld||canvasRotating) EditorGUIUtility.AddCursorRect(canvasRect,MouseCursor.RotateArrow);
        }
        /// <summary>GUI の座標をキャンバスの画素座標（左下原点、範囲外も返す）に。表示の回転・反転・拡大・パンを逆にたどる。</summary>
        Vector2 CanvasPoint(Vector2 pointer)=>CanvasViewNow().ToCanvas(pointer);
        void HandleCanvasInput(Event e)
        {
            if((canvasRect.width>0||canvasRotating)&&HandleCanvasRotateInput(e))return; // R ＋ ドラッグ・Shift ＋ 中ボタンのドラッグで表示を回す
            if(preview.HasModel && stroke==null && preview.HandleNavigation(surfaceRect,e)){Repaint();return;}
            if(canvasRect.Contains(e.mousePosition))
            {
                // ホイールの拡大はポインタの下の画素を動かさない
                if(e.type==EventType.ScrollWheel){ZoomCanvasView(canvasZoom*Mathf.Exp(-e.delta.y*.07f),e.mousePosition);e.Use();return;}
                if(e.type==EventType.MouseDrag && e.button==2){canvasPan+=e.delta;e.Use();Repaint();return;}
            }
            if(stroke==null&&HandleToolInput(e))return;
            if(stroke==null&&HandlePathTool(e))return;
            if(stroke==null&&HandleSurfaceTool(e))return;
            HandleBrushInput(e);
        }
    }
}
