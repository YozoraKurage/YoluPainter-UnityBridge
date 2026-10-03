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
                DrawDecalOverlay(image); // 選んだデカールの届く範囲（Layers/TexturePaintWindow.Decals.cs）
                if(document.Selection!=null){EnsureSelectionOverlay(); GUI.DrawTexture(image,selectionOverlay,ScaleMode.StretchToFill,true);}
            }
            finally{if(turned)GL.PopMatrix();}
            DrawCanvasSymmetryAxes(view);
            DrawStencilOverlay(new Rect(0,0,canvasRect.width,canvasRect.height)); // 画面に貼り付いたステンシル（Tools/TexturePaintWindow.Stencil.cs）
            DrawUvWireframe(view); // 今のテクスチャセットの UV（Canvas/TexturePaintWindow.UvWireframe.cs）
            DrawPolygonFillOutline(view); // ポリゴン塗りつぶしのポインタの下の範囲の輪郭（Tools/TexturePaintWindow.PolygonFill.cs）
            if(toolDragging&&Event.current.type==EventType.Repaint) DrawToolPreview(view);
            else if(tool==PaintTool.Move&&!toolDragging&&Event.current.type==EventType.Repaint) DrawTransformHandles(view);
            if(Event.current.type==EventType.Repaint) DrawCanvasPathMarkers(view);
            DrawCanvasBrushCursor(view,pointer);
            GUI.EndClip();
            DrawCompositingBadge(); // 縮小表示の印（Compositing.cs）
            if(rotateKeyHeld||canvasRotating) EditorGUIUtility.AddCursorRect(canvasRect,MouseCursor.RotateArrow);
        }
        /// <summary>GUI の座標をキャンバスの画素座標（左下原点、範囲外も返す）に。表示の回転・反転・拡大・パンを逆にたどる。</summary>
        Vector2 CanvasPoint(Vector2 pointer)=>CanvasViewNow().ToCanvas(pointer);
        void HandleCanvasInput(Event e)
        {
            if(HandleStencilInput(e))return; // T ＋ ドラッグでステンシルを動かす（2D と 3D。Tools/TexturePaintWindow.Stencil.cs）
            if((canvasRect.width>0||canvasRotating)&&HandleCanvasRotateInput(e))return; // R ＋ ドラッグ・Shift ＋ 中ボタンのドラッグで表示を回す
            if(HandleShapeGizmo(e))return; // 3D ビューの形のグラデーションのハンドル（Model/TexturePaintWindow.ShapeGizmo.cs）
            if(HandleResourceDrop(e))return; // アセットのパネル・Project ウィンドウから落とした画像をレイヤーとして置く
            if(stroke==null && HandleLightingDrag(e))return; // Ctrl+右ドラッグで環境と光を回す（Model/TexturePaintWindow.Display3D.cs）
            if(HandleCloneSourceInput(e))return;
            if(preview.HasModel && stroke==null && preview.HandleNavigation(surfaceRect,e)){Repaint();return;}
            if(canvasRect.Contains(e.mousePosition))
            {
                // ホイールの拡大はポインタの下の画素を動かさない
                if(e.type==EventType.ScrollWheel){ZoomCanvasView(canvasZoom*Mathf.Exp(-e.delta.y*.07f),e.mousePosition);e.Use();return;}
                if(e.type==EventType.MouseDrag && e.button==2){canvasPan+=e.delta;e.Use();Repaint();return;}
            }
            if(HandleIdColorInput(e))return; // ID の色で選ぶ・Generator「ID の色」のスポイト（2D と 3D。Tools/TexturePaintWindow.IdSelect.cs）
            if(HandlePolygonFillInput(e))return; // ポリゴン塗りつぶし（2D と 3D。ドラッグの最中も）
            if(stroke==null&&HandleToolInput(e))return;
            if(stroke==null&&HandlePathTool(e))return;
            if(stroke==null&&HandleSurfaceTool(e))return;
            HandleBrushInput(e);
        }
    }
}
