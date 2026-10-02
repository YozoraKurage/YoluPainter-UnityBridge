using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>編集できる 3D の筆跡（Path ツール）: 3D ビューでモデルをクリックすると制御点を足し（選んだ層にパスが無ければ新しい層を作る）、
    /// 点をドラッグすると動かし、Delete で最後の点を消す。どの操作も描き直して 1 回の Undo。パスで描かれた層は手で塗れない（ラスタライズで外す）。</summary>
    public sealed partial class TexturePaintWindow
    {
        int pathDrag = -1; Vector2 pathDragGui;
        internal const float PathGrabPoints = 8;

        PathBrush CurrentPathBrush()
        {
            var c=GetBrush();
            return new PathBrush{ RadiusWorld=Mathf.Max(.000001f,preview.Bounds.size.magnitude)*brush.radius/document.Width, Hardness=brush.hardness, Spacing=brush.spacing,
                Opacity=brush.opacity, Flow=brush.flow, Color=c.Color, Erase=brush.erase, PressureSize=brush.pressureSize, PressureOpacity=brush.pressureOpacity, PressureFlow=brush.pressureFlow };
        }

        bool HandlePathTool(Event e)
        {
            if(tool!=PaintTool.Path)return false;
            if(e.type==EventType.MouseDown&&e.button==0&&!e.alt)
            {
                if(canvasRect.Contains(e.mousePosition)){message="The path tool works on the 3D view: click the model to add points, drag a point to move it.";e.Use();return true;}
                if(!surfaceRect.Contains(e.mousePosition))return false;
                var pointer=e.mousePosition;
                TryAction(()=>BeginPathEdit(pointer));
                e.Use();Repaint();return true;
            }
            if(pathDrag>=0&&e.type==EventType.MouseDrag){pathDragGui=e.mousePosition;e.Use();Repaint();return true;}
            if(pathDrag>=0&&(e.type==EventType.MouseUp||e.rawType==EventType.MouseUp))
            {
                int index=pathDrag; var at=e.mousePosition; pathDrag=-1; GUIUtility.hotControl=0;
                TryAction(()=>MovePathPoint(index,at));
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
            if(layer.Path!=null)
                for(int i=0;i<layer.Path.Points.Count;i++)
                {
                    var p=SurfacePathRenderer.Position(preview.Geometry,layer.Path.Points[i],out _);
                    if(preview.TryWorldToGui(surfaceRect,p,out var g)&&Vector2.Distance(g,pointer)<=PathGrabPoints){pathDrag=i;pathDragGui=pointer;GUIUtility.hotControl=GUIUtility.GetControlID(FocusType.Passive);return;}
                }
            if(!preview.TryPick(surfaceRect,pointer,out var hit)){message="Nothing of the model under the pointer.";return;}
            if(hit.MaterialSlot!=materialSlot){message="That part uses material slot "+hit.MaterialSlot+"; this document paints slot "+materialSlot+".";return;}
            var point=SurfacePathRenderer.PointOf(hit);
            SurfacePath path;
            if(layer.Path==null)
            {
                var created=document.AddLayer("Path",null,layer.Id); selectedLayer=created.Id; layer=created;
                if(!layer.IsChannelEnabled(channel))document.SetChannelEnabled(layer.Id,channel,true);
                path=new SurfacePath(Guid.NewGuid(),channel,SurfacePathRenderer.Fingerprint(preview.Geometry),CurrentPathBrush(),new[]{point});
            }
            else path=layer.Path.WithPoints(layer.Path.Points.Concat(new[]{point}));
            ApplyPath(layer.Id,path,"Added path point "+path.Points.Count+".");
        }

        internal void MovePathPoint(int index,Vector2 pointer)
        {
            var layer=document.GetLayer(selectedLayer);
            if(layer.Path==null||index<0||index>=layer.Path.Points.Count)return;
            if(!preview.TryPick(surfaceRect,pointer,out var hit)||hit.MaterialSlot!=materialSlot){message="Drop the point on the model (this material slot).";return;}
            var points=layer.Path.Points.ToArray(); points[index]=SurfacePathRenderer.PointOf(hit,points[index].Pressure);
            ApplyPath(layer.Id,layer.Path.WithPoints(points),"Moved path point "+(index+1)+".");
        }

        internal void RemoveLastPathPoint()
        {
            var layer=document.GetLayer(selectedLayer);
            if(layer.Path==null||layer.Path.Points.Count==0)return;
            ApplyPath(layer.Id,layer.Path.WithPoints(layer.Path.Points.Take(layer.Path.Points.Count-1)),"Removed the last path point.");
        }

        void ApplyPath(Guid layerId,SurfacePath path,string done)
        {
            var render=SurfacePathRenderer.Render(document,preview.Geometry,path,preview.BrushBudget);
            document.SetPath(layerId,path,render.Surface);
            message=done+(render.Gaps>0?" "+render.Gaps+" sample(s) could not be projected onto the surface and were skipped.":"");repaintPixels=true;
        }

        void DrawPathSettings()
        {
            EditorGUILayout.LabelField("Click the model in the 3D view to add points; drag a point to move it; Delete removes the last point. The layer is redrawn from the path each time.",EditorStyles.wordWrappedMiniLabel);
            var layer=document.Layers.FirstOrDefault(l=>l.Id==selectedLayer);
            if(layer?.Path==null)return;
            EditorGUILayout.LabelField("Path: "+layer.Path.Points.Count+" point(s) on "+layer.Path.Channel,EditorStyles.miniLabel);
            bool bound=preview.Geometry!=null&&SurfacePathRenderer.Fingerprint(preview.Geometry)==layer.Path.ModelFingerprint;
            if(!bound)EditorGUILayout.HelpBox("This path was drawn on another model snapshot (different triangles or UVs). Load that model to edit it, or rasterize it.",MessageType.Warning);
            GUILayout.BeginHorizontal();
            using(new EditorGUI.DisabledScope(!bound||stroke!=null))
            {
                if(GUILayout.Button(new GUIContent("Use brush","Redraw the path with the current brush")))TryAction(()=>ApplyPath(layer.Id,layer.Path.WithBrush(CurrentPathBrush()),"Path redrawn with the current brush."));
                if(GUILayout.Button(new GUIContent("Redraw","Redraw on the current pose")))TryAction(()=>ApplyPath(layer.Id,layer.Path,"Path redrawn."));
            }
            if(GUILayout.Button(new GUIContent("Rasterize","Keep the pixels and remove the path, so the layer can be painted")))TryAction(()=>{document.Rasterize(layer.Id);message="Rasterized: the layer keeps its pixels and can be painted.";});
            GUILayout.EndHorizontal();
        }

        /// <summary>選んだ層のパスの制御点と線を 3D ビューに重ねる（Repaint のとき）。</summary>
        void DrawPathMarkers()
        {
            if(tool!=PaintTool.Path||preview==null||preview.Geometry==null)return;
            var layer=document.Layers.FirstOrDefault(l=>l.Id==selectedLayer); var path=layer?.Path;
            if(path==null||path.Points.Count==0||path.Points.Any(p=>p.Triangle>=preview.Geometry.TriangleCount))return;
            var gui=new Vector3[path.Points.Count]; bool all=true;
            for(int i=0;i<gui.Length;i++){if(preview.TryWorldToGui(surfaceRect,SurfacePathRenderer.Position(preview.Geometry,path.Points[i],out _),out var g))gui[i]=g;else all=false;}
            if(pathDrag>=0&&pathDrag<gui.Length)gui[pathDrag]=pathDragGui;
            Handles.color=new Color(1,.8f,.2f,.9f);
            if(all&&gui.Length>1)Handles.DrawAAPolyLine(2,gui);
            foreach(var g in gui)EditorGUI.DrawRect(new Rect(g.x-3,g.y-3,6,6),new Color(1,.8f,.2f,1));
        }
    }
}
