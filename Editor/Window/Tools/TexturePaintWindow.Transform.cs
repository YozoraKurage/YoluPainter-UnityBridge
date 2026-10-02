using System;
using System.Linq;
using Yozolab.YoluPainter.Core;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>移動ツール（Move / Transform）: 整数画素の移動・自由変形のハンドル・数値での変形。</summary>
    public sealed partial class TexturePaintWindow
    {
        // 移動ツール: ドラッグ開始時に動かすものの範囲（プレビューの枠）と、数値で変形する値
        (int x0, int y0, int x1, int y1)? moveBounds;
        float moveAngle; Vector2 moveScale = new Vector2(100, 100), moveOffset; Resampling moveResampling;
        internal float MoveAngle { get => moveAngle; set => moveAngle = value; }
        internal Vector2 MoveScale { get => moveScale; set => moveScale = value; }
        internal Vector2 MoveOffset { get => moveOffset; set => moveOffset = value; }
        internal Resampling MoveResampling { get => moveResampling; set => moveResampling = value; }
        /// <summary>移動・変形できる層か確かめる（画素を持つのはペイントの層だけ）。</summary>
        PaintLayer RequireMovableLayer()
        {
            var layer=document.GetLayer(selectedLayer);
            if(layer.IsGroup)throw new InvalidOperationException("A group has no pixels to move. Select a layer inside it.");
            if(layer.Kind!=LayerKind.Raster)throw new InvalidOperationException("Only paint layers can be moved or transformed ("+layer.Kind+" layers have no pixels).");
            return layer;
        }
        void BeginMove(Vector2 p,Vector2 pointer)
        {
            RequireMovableLayer();
            moveBounds=document.TransformBounds(selectedLayer);
            if(moveBounds==null){message=document.Selection!=null?"Nothing to move inside the selection on this layer.":"Nothing to move on this layer.";return;}
            moveMode=HitTransformHandle(moveBounds.Value,pointer,out moveAnchor,out moveHandle,out moveAxes);
            toolDragging=true;toolStart=toolCurrent=p;toolShift=false;GUIUtility.hotControl=GUIUtility.GetControlID(FocusType.Passive);
        }
        // 自由変形: 角は拡大縮小（Shift で縦横比を保つ）、辺の中点は片方向、角の外側は回転（Shift で 15° 刻み）、それ以外は移動
        internal enum MoveMode { Move, Scale, Rotate }
        MoveMode moveMode; Vector2 moveAnchor, moveHandle; int moveAxes; bool toolShift;
        internal const float HandleHitPoints=6, RotateReachPoints=26, MinHandleBoxPoints=36;
        (int x0,int y0,int x1,int y1)? handleBounds; long handleRevision=-1; Guid handleLayer; SelectionMask handleSelection;
        Vector2 CanvasToWindow(Vector2 p)=>CanvasViewNow().ToGui(p);
        /// <summary>ハンドルを出せる大きさか（小さい範囲では、どこを掴んでも移動にする）。画面の上の辺の長さで見る（回転では変わらない）。</summary>
        bool HandlesUsable((int x0,int y0,int x1,int y1) b)
        {
            var view=CanvasViewNow();
            if(view.AxisAligned)
            {
                var a=view.ToGui(new Vector2(b.x0,b.y0)); var c=view.ToGui(new Vector2(b.x1,b.y1));
                return Mathf.Abs(c.x-a.x)>=MinHandleBoxPoints&&Mathf.Abs(c.y-a.y)>=MinHandleBoxPoints;
            }
            return (b.x1-b.x0)*view.PixelSize>=MinHandleBoxPoints&&(b.y1-b.y0)*view.PixelSize>=MinHandleBoxPoints;
        }
        static Vector2[] HandlePoints((int x0,int y0,int x1,int y1) b)
        {
            float cx=(b.x0+b.x1)*.5f,cy=(b.y0+b.y1)*.5f;
            return new[]{new Vector2(b.x0,b.y0),new Vector2(b.x1,b.y0),new Vector2(b.x1,b.y1),new Vector2(b.x0,b.y1),new Vector2(cx,b.y0),new Vector2(b.x1,cy),new Vector2(cx,b.y1),new Vector2(b.x0,cy)};
        }
        MoveMode HitTransformHandle((int x0,int y0,int x1,int y1) b,Vector2 pointer,out Vector2 anchor,out Vector2 handle,out int axes)
        {
            anchor=handle=Vector2.zero; axes=0;
            if(!HandlesUsable(b))return MoveMode.Move;
            var points=HandlePoints(b);
            for(int i=0;i<points.Length;i++)
            {
                if(Vector2.Distance(CanvasToWindow(points[i]),pointer)>HandleHitPoints)continue;
                handle=points[i]; anchor=new Vector2(b.x0+b.x1-handle.x,b.y0+b.y1-handle.y);
                axes=i<4?3:(i==5||i==7)?1:2; return MoveMode.Scale;
            }
            if(InsideTransformBox(b,pointer))return MoveMode.Move;
            for(int i=0;i<4;i++) if(Vector2.Distance(CanvasToWindow(points[i]),pointer)<=RotateReachPoints) return MoveMode.Rotate;
            return MoveMode.Move;
        }
        /// <summary>動かすものの範囲の内側か（表示が回っていれば画素の座標で見る）。</summary>
        bool InsideTransformBox((int x0,int y0,int x1,int y1) b,Vector2 pointer)
        {
            var view=CanvasViewNow();
            if(view.AxisAligned)
            {
                var lo=view.ToGui(new Vector2(b.x0,b.y1)); var hi=view.ToGui(new Vector2(b.x1,b.y0)); // ウィンドウでは y が下向き
                return Rect.MinMaxRect(lo.x,lo.y,hi.x,hi.y).Contains(pointer);
            }
            var p=view.ToCanvas(pointer);
            return p.x>=b.x0&&p.x<b.x1&&p.y>b.y0&&p.y<=b.y1;
        }
        float DragAngle()
        {
            var b=moveBounds.Value; var c=new Vector2((b.x0+b.x1)*.5f,(b.y0+b.y1)*.5f);
            float angle=(Mathf.Atan2(toolCurrent.y-c.y,toolCurrent.x-c.x)-Mathf.Atan2(toolStart.y-c.y,toolStart.x-c.x))*Mathf.Rad2Deg;
            angle=Mathf.Repeat(angle+180,360)-180;
            return toolShift?Mathf.Round(angle/15)*15:angle;
        }
        /// <summary>今のドラッグが表す変形（移動は整数画素）。</summary>
        internal Affine2D DragTransform()
        {
            if(moveBounds==null)return Affine2D.Identity;
            var b=moveBounds.Value;
            switch(moveMode)
            {
                case MoveMode.Scale:
                {
                    double sx=(moveAxes&1)!=0&&moveHandle.x!=moveAnchor.x?(toolCurrent.x-moveAnchor.x)/(moveHandle.x-moveAnchor.x):1;
                    double sy=(moveAxes&2)!=0&&moveHandle.y!=moveAnchor.y?(toolCurrent.y-moveAnchor.y)/(moveHandle.y-moveAnchor.y):1;
                    if(toolShift&&moveAxes==3){double m=Math.Max(Math.Abs(sx),Math.Abs(sy));sx=m*(sx<0?-1:1);sy=m*(sy<0?-1:1);}
                    return Affine2D.FromParts(moveAnchor.x,moveAnchor.y,0,0,0,sx,sy);
                }
                case MoveMode.Rotate:
                {
                    double degrees=DragAngle(),cx=(b.x0+b.x1)/2.0,cy=(b.y0+b.y1)/2.0;
                    if(Math.Abs(Math.IEEERemainder(degrees,90))<1e-9&&Math.Abs(Math.IEEERemainder(degrees,180))>1e-9){cx=Math.Round(cx);cy=Math.Round(cy);}
                    return Affine2D.FromParts(cx,cy,0,0,degrees,1,1);
                }
                default: { var d=MoveDelta(); return Affine2D.Translation(d.x,d.y); }
            }
        }
        /// <summary>移動ツールで、動かすものの範囲と掴めるハンドルを見せる（範囲はドキュメントの版・層・選択範囲が変わったときだけ求め直す）。</summary>
        void DrawTransformHandles(CanvasView view)
        {
            if(handleRevision!=document.Revision||handleLayer!=selectedLayer||!ReferenceEquals(handleSelection,document.Selection))
            {
                handleRevision=document.Revision; handleLayer=selectedLayer; handleSelection=document.Selection;
                var layer=document.Layers.FirstOrDefault(l=>l.Id==selectedLayer);
                handleBounds=layer!=null&&layer.Kind==LayerKind.Raster?document.TransformBounds(selectedLayer):null;
            }
            if(handleBounds==null)return;
            var b=handleBounds.Value;
            Handles.color=new Color(1,1,1,.6f);
            Handles.DrawAAPolyLine(1f,view.ToGui(b.x0,b.y0),view.ToGui(b.x1,b.y0),view.ToGui(b.x1,b.y1),view.ToGui(b.x0,b.y1),view.ToGui(b.x0,b.y0));
            if(!HandlesUsable(b))return;
            foreach(var h in HandlePoints(b)){var g=view.ToGui(h);EditorGUI.DrawRect(new Rect(g.x-3,g.y-3,6,6),new Color(1,1,1,.9f));}
        }
        Vector2Int MoveDelta()=>new Vector2Int(Mathf.RoundToInt(toolCurrent.x-toolStart.x),Mathf.RoundToInt(toolCurrent.y-toolStart.y));
        /// <summary>選んだ層（選択範囲があればその画素と選択範囲）を整数画素だけ動かす。全チャンネルとマスクが一緒に動く。1 回の Undo。</summary>
        internal void MoveBy(int dx,int dy)
        {
            RequireMovableLayer();
            bool changed=document.Transform(selectedLayer,Affine2D.Translation(dx,dy));
            message=changed?"Moved by ("+dx+", "+dy+") px.":"Nothing to move.";repaintPixels=true;
        }
        /// <summary>動かすもの（選択範囲があればその中）の中心を軸に、拡大縮小（負は反転）・回転してからずらす。1 回の Undo。
        /// 90° の倍数の回転では軸を画素の格子に合わせ、画素がそのまま写るようにする。</summary>
        internal void TransformSelected(double dx,double dy,double degrees,double sx,double sy,string done)
        {
            RequireMovableLayer();
            var bounds=document.TransformBounds(selectedLayer);
            if(bounds==null){message=document.Selection!=null?"Nothing to transform inside the selection on this layer.":"Nothing to transform on this layer.";return;}
            var b=bounds.Value; double cx=(b.x0+b.x1)/2.0,cy=(b.y0+b.y1)/2.0;
            if(Math.Abs(Math.IEEERemainder(degrees,90))<1e-9&&Math.Abs(Math.IEEERemainder(degrees,180))>1e-9){cx=Math.Round(cx);cy=Math.Round(cy);}
            bool changed=document.Transform(selectedLayer,Affine2D.FromParts(cx,cy,dx,dy,degrees,sx,sy),resampling:moveResampling);
            message=changed?done:"Nothing changed.";repaintPixels=true;
        }
        internal void ApplyNumericTransform()
        {
            if(moveScale.x==0||moveScale.y==0)throw new InvalidOperationException("Scale must not be 0%.");
            TransformSelected(moveOffset.x,moveOffset.y,moveAngle,moveScale.x/100.0,moveScale.y/100.0,"Transformed.");
            moveAngle=0;moveScale=new Vector2(100,100);moveOffset=Vector2.zero;
        }
    }
}
