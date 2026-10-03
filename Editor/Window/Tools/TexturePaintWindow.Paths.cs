using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>編集できる筆跡（Path ツール）: 3D ビューでモデルを、または 2D キャンバスをクリックすると制御点を足し（選んだ層にパスが無ければ
    /// 新しい層を作る）、点をドラッグすると動かし、Delete で最後の点を消す。どの操作も描き直して 1 回の Undo。3D のパスはモデルの面に、2D のパスは
    /// 画素の座標に結び付き、1 つの層はどちらか一方だけを持つ。パスで描かれた層は手で塗れない（ラスタライズで外す）。</summary>
    public sealed partial class TexturePaintWindow
    {
        int pathDrag = -1; Vector2 pathDragGui; bool pathDragOnCanvas;
        internal const float PathGrabPoints = 8;

        /// <summary>今のブラシをパスの筆に。3D では半径をモデルの大きさに合わせ、2D では画素のまま。</summary>
        PathBrush CurrentPathBrush(bool canvas=false)
        {
            var c=GetBrush();
            return new PathBrush{ RadiusWorld=canvas?Mathf.Max(.01f,brush.radius):Mathf.Max(.000001f,preview.Bounds.size.magnitude)*brush.radius/document.Width, Hardness=brush.hardness, Spacing=brush.spacing,
                Opacity=brush.opacity, Flow=brush.flow, Color=c.Color, Erase=brush.erase, PressureSize=brush.pressureSize, PressureOpacity=brush.pressureOpacity, PressureFlow=brush.pressureFlow };
        }

        bool HandlePathTool(Event e)
        {
            if(tool!=PaintTool.Path)return false;
            if(e.type==EventType.MouseDown&&e.button==0&&!e.alt)
            {
                if(canvasRect.Contains(e.mousePosition)){var at=e.mousePosition;TryAction(()=>BeginCanvasPathEdit(at));e.Use();Repaint();return true;}
                if(!surfaceRect.Contains(e.mousePosition))return false;
                var pointer=e.mousePosition;
                TryAction(()=>BeginPathEdit(pointer));
                e.Use();Repaint();return true;
            }
            if(pathDrag>=0&&e.type==EventType.MouseDrag){pathDragGui=e.mousePosition;e.Use();Repaint();return true;}
            if(pathDrag>=0&&(e.type==EventType.MouseUp||e.rawType==EventType.MouseUp))
            {
                int index=pathDrag; var at=e.mousePosition; bool onCanvas=pathDragOnCanvas; pathDrag=-1; GUIUtility.hotControl=0;
                TryAction(()=>{if(onCanvas)MoveCanvasPathPoint(index,at);else MovePathPoint(index,at);});
                e.Use();Repaint();return true;
            }
            return false;
        }

        /// <summary>クリック: 近くの制御点を掴むか、面の点を足す。</summary>
        internal void BeginPathEdit(Vector2 pointer)
        {
            if(!preview.CanPaint){message="Load a complete model (or the demo cube) to draw paths on it.";return;}
            if(EditingMask){message="Paths draw layer pixels; turn off mask painting first.";return;}
            var layer=document.GetLayer(selectedLayer);
            if(layer.Path is CanvasPath){message="This layer has a canvas path; edit it on the 2D canvas, or rasterize it.";return;}
            var existing=layer.Path as SurfacePath;
            if(existing!=null)
                for(int i=0;i<existing.Points.Count;i++)
                {
                    var p=SurfacePathRenderer.Position(preview.Geometry,existing.Points[i],out _);
                    if(preview.TryWorldToGui(surfaceRect,p,out var g)&&Vector2.Distance(g,pointer)<=PathGrabPoints){pathDrag=i;pathDragOnCanvas=false;pathDragGui=pointer;GUIUtility.hotControl=GUIUtility.GetControlID(FocusType.Passive);return;}
                }
            if(!preview.TryPick(surfaceRect,pointer,out var hit)){message="Nothing of the model under the pointer.";return;}
            if(!PaintsSlot(hit.MaterialSlot)){OtherSlotPressed(hit.MaterialSlot);return;}
            var point=SurfacePathRenderer.PointOf(hit);
            SurfacePath path;
            if(existing==null)
            {
                path=new SurfacePath(Guid.NewGuid(),channel,SurfacePathRenderer.Fingerprint(preview.Geometry),CurrentPathBrush(),new[]{point},brush.material?StrokeChannels():null);
                var render = SurfacePathRenderer.Render(document, preview.Geometry, path, preview.BrushBudget);
                var created = document.AddPathLayer("Path", path, render.Channels, layer.Id);
                selectedLayer = created.Id; message = L.Tr("Added path point {0}.", 1); repaintPixels = true; return;
            }
            else path=existing.WithPoints(existing.Points.Concat(new[]{point}));
            ApplyPath(layer.Id,path,"Added path point "+path.Points.Count+".");
        }

        /// <summary>2D キャンバスのクリック: 近くの制御点を掴むか、その画素の座標に点を足す。</summary>
        internal void BeginCanvasPathEdit(Vector2 pointer)
        {
            if(EditingMask){message="Paths draw layer pixels; turn off mask painting first.";return;}
            var layer=document.GetLayer(selectedLayer);
            if(layer.Path is SurfacePath){message="This layer has a path on the model; edit it in the 3D view, or rasterize it.";return;}
            var existing=layer.Path as CanvasPath;
            if(existing!=null)
                for(int i=0;i<existing.Points.Count;i++)
                    if(Vector2.Distance(CanvasPathGui(existing.Points[i]),pointer)<=PathGrabPoints){pathDrag=i;pathDragOnCanvas=true;pathDragGui=pointer;GUIUtility.hotControl=GUIUtility.GetControlID(FocusType.Passive);return;}
            var at=CanvasPoint(pointer);
            if(at.x<0||at.y<0||at.x>document.Width||at.y>document.Height){message="Click inside the canvas to add a path point.";return;}
            var point=new CanvasPoint(at.x,at.y);
            CanvasPath path;
            if(existing==null)
            {
                path=new CanvasPath(Guid.NewGuid(),channel,CurrentPathBrush(true),new[]{point},brush.material?StrokeChannels():null);
                var created = document.AddPathLayer("Path", path, CanvasPathRenderer.RenderChannels(document, path), layer.Id);
                selectedLayer = created.Id; message = L.Tr("Added path point {0}.", 1); repaintPixels = true; return;
            }
            else path=existing.WithPoints(existing.Points.Concat(new[]{point}));
            ApplyCanvasPath(layer.Id,path,"Added path point "+path.Points.Count+".");
        }

        internal void MoveCanvasPathPoint(int index,Vector2 pointer)
        {
            if(!(document.GetLayer(selectedLayer).Path is CanvasPath path)||index<0||index>=path.Points.Count)return;
            var at=CanvasPoint(pointer); var points=path.Points.ToArray();
            points[index]=new CanvasPoint(Mathf.Clamp(at.x,0,document.Width),Mathf.Clamp(at.y,0,document.Height),points[index].Pressure);
            ApplyCanvasPath(selectedLayer,path.WithPoints(points),"Moved path point "+(index+1)+".");
        }

        /// <summary>2D のパスの点の GUI 座標（ウィンドウの座標）。</summary>
        Vector2 CanvasPathGui(CanvasPoint p)=>CanvasViewNow().ToGui((float)p.X,(float)p.Y);

        void ApplyCanvasPath(Guid layerId,CanvasPath path,string done)
        {
            document.SetCanvasPath(layerId,path);
            message=done;repaintPixels=true;
        }

        internal void MovePathPoint(int index,Vector2 pointer)
        {
            var layer=document.GetLayer(selectedLayer);
            if(!(layer.Path is SurfacePath path)||index<0||index>=path.Points.Count)return;
            if(!preview.TryPick(surfaceRect,pointer,out var hit)||!PaintsSlot(hit.MaterialSlot)){message="Drop the point on the model (this texture set's material slot).";return;}
            var points=path.Points.ToArray(); points[index]=SurfacePathRenderer.PointOf(hit,points[index].Pressure);
            ApplyPath(layer.Id,path.WithPoints(points),"Moved path point "+(index+1)+".");
        }

        internal void RemoveLastPathPoint()
        {
            var layer=document.GetLayer(selectedLayer);
            if(layer.Path is CanvasPath canvasPath){if(canvasPath.Points.Count>0)ApplyCanvasPath(layer.Id,canvasPath.WithPoints(canvasPath.Points.Take(canvasPath.Points.Count-1)),"Removed the last path point.");return;}
            if(!(layer.Path is SurfacePath path)||path.Points.Count==0)return;
            ApplyPath(layer.Id,path.WithPoints(path.Points.Take(path.Points.Count-1)),"Removed the last path point.");
        }

        void ApplyPath(Guid layerId,SurfacePath path,string done)
        {
            var render=SurfacePathRenderer.Render(document,preview.Geometry,path,preview.BrushBudget);
            document.SetPath(layerId,path,render.Channels);
            message=done+(render.Gaps>0?" "+render.Gaps+" sample(s) could not be projected onto the surface and were skipped.":"");repaintPixels=true;
        }

        void PathSection(UiRows rows)
        {
            if (!ToolSection(rows, "path", L.Tr("Path"), "conversion_path")) return;
            PaintGui.Paragraph(rows, L.Tr("Click the model in the 3D view or the 2D canvas to add points; drag a point to move it; Delete removes the last point. The layer is redrawn from the path each time."));
            var layer = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            string rasterizeTip = L.Tr("Keep the pixels and remove the path, so the layer can be painted");
            if (layer?.Path is CanvasPath canvasPath)
            {
                PaintGui.Paragraph(rows, L.Tr("Canvas path: {0} point(s) on {1}", canvasPath.Points.Count, string.Join(", ", canvasPath.Paints.Select(m => L.Tr(m.Channel.ToString())))), PaintTheme.TextDim);
                var c = UiRows.Split(rows.Row(24), 2, 6);
                if (PaintGui.FitButton(Mark("path.use-brush", c[0]), L.TrIn("path", "Use Brush"), false, stroke == null, L.Tr("Redraw the path with the current brush")))
                    TryAction(() => ApplyCanvasPath(layer.Id, canvasPath.WithBrush(CurrentPathBrush(true)).WithMaterial(brush.material ? StrokeChannels() : null), L.Tr("Path redrawn with the current brush.")));
                if (PaintGui.FitButton(c[1], L.TrIn("path", "Rasterize"), false, true, rasterizeTip)) TryAction(() => RasterizePath(layer.Id));
                rows.Space(4);
                return;
            }
            if (layer?.Path is SurfacePath surfacePath)
            {
                PaintGui.Paragraph(rows, L.Tr("Path on the model: {0} point(s) on {1}", surfacePath.Points.Count, string.Join(", ", surfacePath.Paints.Select(m => L.Tr(m.Channel.ToString())))), PaintTheme.TextDim);
                bool bound = preview.Geometry != null && SurfacePathRenderer.Fingerprint(preview.Geometry) == surfacePath.ModelFingerprint;
                if (!bound) PaintGui.Notice(rows, L.Tr("This path was drawn on another model snapshot (different triangles or UVs). Load that model to edit it, or rasterize it."), "warning", PaintTheme.Warning);
                var c = UiRows.Split(rows.Row(24), 3, 6);
                if (PaintGui.FitButton(Mark("path.use-brush", c[0]), L.TrIn("path", "Use Brush"), false, bound && stroke == null, L.Tr("Redraw the path with the current brush")))
                    TryAction(() => ApplyPath(layer.Id, surfacePath.WithBrush(CurrentPathBrush()).WithMaterial(brush.material ? StrokeChannels() : null), L.Tr("Path redrawn with the current brush.")));
                if (PaintGui.FitButton(c[1], L.TrIn("path", "Redraw"), false, bound && stroke == null, L.Tr("Redraw on the current pose")))
                    TryAction(() => ApplyPath(layer.Id, surfacePath, L.Tr("Path redrawn.")));
                if (PaintGui.FitButton(c[2], L.TrIn("path", "Rasterize"), false, true, rasterizeTip)) TryAction(() => RasterizePath(layer.Id));
            }
            rows.Space(4);
        }

        void RasterizePath(Guid layerId) { document.Rasterize(layerId); message = L.Tr("Rasterized: the layer keeps its pixels and can be painted."); }

        /// <summary>選んだ層のパスの制御点と線を 3D ビューに重ねる（Repaint のとき）。</summary>
        void DrawPathMarkers()
        {
            if(tool!=PaintTool.Path||preview==null||preview.Geometry==null)return;
            var layer=document.Layers.FirstOrDefault(l=>l.Id==selectedLayer); var path=layer?.Path as SurfacePath;
            if(path==null||path.Points.Count==0||path.Points.Any(p=>p.Triangle>=preview.Geometry.TriangleCount))return;
            var gui=new Vector3[path.Points.Count]; bool all=true;
            for(int i=0;i<gui.Length;i++){if(preview.TryWorldToGui(surfaceRect,SurfacePathRenderer.Position(preview.Geometry,path.Points[i],out _),out var g))gui[i]=g;else all=false;}
            if(pathDrag>=0&&!pathDragOnCanvas&&pathDrag<gui.Length)gui[pathDrag]=pathDragGui;
            Handles.color=new Color(1,.8f,.2f,.9f);
            if(all&&gui.Length>1)Handles.DrawAAPolyLine(2,gui);
            foreach(var g in gui)EditorGUI.DrawRect(new Rect(g.x-3,g.y-3,6,6),new Color(1,.8f,.2f,1));
        }

        /// <summary>選んだ層の 2D のパスの制御点と線をキャンバスに重ねる（Repaint のとき。view はクリップの中の座標の写し）。</summary>
        void DrawCanvasPathMarkers(CanvasView view)
        {
            if(tool!=PaintTool.Path)return;
            var layer=document.Layers.FirstOrDefault(l=>l.Id==selectedLayer);
            if(!(layer?.Path is CanvasPath path)||path.Points.Count==0)return;
            var gui=new Vector3[path.Points.Count];
            for(int i=0;i<gui.Length;i++)gui[i]=view.ToGui((float)path.Points[i].X,(float)path.Points[i].Y);
            if(pathDrag>=0&&pathDragOnCanvas&&pathDrag<gui.Length)gui[pathDrag]=pathDragGui-canvasRect.position;
            Handles.color=new Color(1,.8f,.2f,.9f);
            if(gui.Length>1)Handles.DrawAAPolyLine(2,gui);
            foreach(var g in gui)EditorGUI.DrawRect(new Rect(g.x-3,g.y-3,6,6),new Color(1,.8f,.2f,1));
        }
    }
}
