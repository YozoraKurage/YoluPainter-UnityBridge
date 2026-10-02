using System;
using System.Collections.Generic;
using System.Linq;
using Yozolab.YoluPainter.Core;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>ブラシ以外のツールのキャンバス入力（バケツ・マジックワンド・スポイト・グラデーション・矩形/楕円/投げ縄選択）。</summary>
    public sealed partial class TexturePaintWindow
    {
        int wandTolerance = 32; bool wandContiguous = true, wandSampleAll;
        Color gradientTo = new Color(0, 0, 0, 0); GradientShape gradientShape;
        // ドラッグで形を決めるツール（グラデーション・矩形/楕円/投げ縄選択）の途中の状態。キャンバスの画素座標（左下原点）
        bool toolDragging; Vector2 toolStart, toolCurrent; readonly List<Vector2> lassoPoints = new List<Vector2>();
        internal int WandTolerance { get => wandTolerance; set => wandTolerance = Mathf.Clamp(value, 0, 255); }
        internal bool WandContiguous { get => wandContiguous; set => wandContiguous = value; }
        internal bool WandSampleAll { get => wandSampleAll; set => wandSampleAll = value; }
        internal Color GradientTo { get => gradientTo; set => gradientTo = value; }
        void DrawToolPreview(Rect image)
        {
            Handles.color=new Color(1,1,1,.9f);
            Vector2 a=ToGui(image,toolStart),b=ToGui(image,toolCurrent);
            switch(tool)
            {
                case PaintTool.Gradient: Handles.DrawAAPolyLine(2,a,b); break;
                case PaintTool.SelectRectangle: Handles.DrawAAPolyLine(1.5f,new Vector3(a.x,a.y),new Vector3(b.x,a.y),new Vector3(b.x,b.y),new Vector3(a.x,b.y),new Vector3(a.x,a.y)); break;
                case PaintTool.SelectEllipse:
                {
                    var c=(a+b)/2; var r=new Vector2(Mathf.Abs(b.x-a.x)/2,Mathf.Abs(b.y-a.y)/2); var points=new Vector3[49];
                    for(int i=0;i<points.Length;i++){float t=i/48f*Mathf.PI*2;points[i]=new Vector3(c.x+Mathf.Cos(t)*r.x,c.y+Mathf.Sin(t)*r.y);}
                    Handles.DrawAAPolyLine(1.5f,points); break;
                }
                case PaintTool.Lasso: if(lassoPoints.Count>1)Handles.DrawAAPolyLine(1.5f,lassoPoints.Select(p=>(Vector3)ToGui(image,p)).ToArray()); break;
                case PaintTool.Move:
                {
                    if(moveBounds==null)break;
                    var m=moveBounds.Value; var t=DragTransform();
                    var quad=new[]{(m.x0,m.y0),(m.x1,m.y0),(m.x1,m.y1),(m.x0,m.y1),(m.x0,m.y0)}.Select(c=>{var q=t.Apply(c.Item1,c.Item2);return (Vector3)ToGui(image,new Vector2((float)q.x,(float)q.y));}).ToArray();
                    Handles.DrawAAPolyLine(1.5f,quad);
                    break;
                }
            }
        }
        void CancelToolDrag(){toolDragging=false;lassoPoints.Clear();moveBounds=null;}
        /// <summary>ブラシ以外のツールのキャンバス入力。2D キャンバスだけで働く。</summary>
        bool HandleToolInput(Event e)
        {
            if(tool==PaintTool.Brush||tool==PaintTool.Path)return false; // パスは HandlePathTool が受け持つ
            if(e.type==EventType.MouseDown&&e.button==0&&!e.alt&&canvasRect.Contains(e.mousePosition))
            {
                var p=CanvasPoint(e.mousePosition);
                switch(tool)
                {
                    case PaintTool.Fill: TryAction(()=>BucketFill(p)); break;
                    case PaintTool.MagicWand: TryAction(()=>ApplySelection(Wand(p),CombineOf(e))); break;
                    case PaintTool.Move: TryAction(()=>BeginMove(p,e.mousePosition)); break;
                    case PaintTool.Eyedropper: TryAction(()=>PickColor(p)); break;
                    default: toolDragging=true;toolStart=toolCurrent=p;lassoPoints.Clear();lassoPoints.Add(p);GUIUtility.hotControl=GUIUtility.GetControlID(FocusType.Passive); break;
                }
                e.Use();Repaint();return true;
            }
            if(toolDragging&&e.type==EventType.MouseDrag&&e.button==0)
            {
                toolCurrent=CanvasPoint(e.mousePosition); toolShift=e.shift;
                if(tool==PaintTool.Lasso&&(lassoPoints.Count==0||Vector2.Distance(lassoPoints[lassoPoints.Count-1],toolCurrent)>=1))lassoPoints.Add(toolCurrent);
                e.Use();Repaint();return true;
            }
            if(toolDragging&&(e.type==EventType.MouseUp||e.rawType==EventType.MouseUp))
            {
                toolCurrent=CanvasPoint(e.mousePosition); toolShift=e.shift; var mode=CombineOf(e);
                TryAction(()=>FinishToolDrag(mode));
                CancelToolDrag();GUIUtility.hotControl=0;e.Use();Repaint();return true;
            }
            return false;
        }
        /// <summary>スポイト: 選んだ層（「全レイヤー」なら合成）の、今のチャンネルの色をブラシの色にする。</summary>
        internal void PickColor(Vector2 p)
        {
            if(p.x<0||p.y<0||p.x>=document.Width||p.y>=document.Height)return;
            int x=Mathf.FloorToInt(p.x),y=Mathf.FloorToInt(p.y);
            var layer=document.Layers.FirstOrDefault(l=>l.Id==selectedLayer);
            var c=wandSampleAll||layer==null||layer.IsGroup?document.CompositePixel(channel,x,y):layer.GetOutputPixel(channel,x,y);
            if(c.A==0){message=L.Tr("Nothing to pick there (transparent).");return;}
            brush.color=new Color(c.R/255f,c.G/255f,c.B/255f,1);
            message=L.Tr("Picked")+" R "+c.R+" G "+c.G+" B "+c.B+".";
        }
        SelectionMask Wand(Vector2 p)
        {
            int x=Mathf.Clamp(Mathf.FloorToInt(p.x),0,document.Width-1),y=Mathf.Clamp(Mathf.FloorToInt(p.y),0,document.Height-1);
            return SelectionMask.MagicWand(document,wandSampleAll?(Guid?)null:selectedLayer,channel,x,y,wandTolerance,wandContiguous);
        }
        /// <summary>バケツ: マジックワンドと同じ条件で範囲を求め（今の選択範囲の内側に限る）、ブラシの色と不透明度で塗る。マスク編集中はマスクを塗る。</summary>
        internal void BucketFill(Vector2 p)
        {
            if(p.x<0||p.y<0||p.x>=document.Width||p.y>=document.Height)return;
            var layer=document.GetLayer(selectedLayer);
            if(!EditingMask&&layer.Kind!=LayerKind.Raster)throw new InvalidOperationException("Fill paints pixels: select a paint layer, or edit the layer's mask.");
            if(!EditingMask&&!layer.IsChannelEnabled(channel))document.SetChannelEnabled(selectedLayer,channel,true);
            var region=Wand(p); var b=GetBrush();
            bool changed=EditingMask?document.FillMask(selectedLayer,b.Opacity,region,reveal:b.Erase):document.Fill(selectedLayer,channel,b.Color,b.Opacity,region,b.Erase);
            message=changed?"Filled.":"Nothing to fill there.";repaintPixels=true;
        }
        void FinishToolDrag(SelectionCombine mode)
        {
            Vector2 a=toolStart,b=toolCurrent; bool click=Vector2.Distance(a,b)<1.5f;
            switch(tool)
            {
                case PaintTool.Gradient:
                {
                    if(click)return;
                    var layer=document.GetLayer(selectedLayer);
                    if(layer.Kind!=LayerKind.Raster)throw new InvalidOperationException("A gradient paints pixels: select a paint layer.");
                    if(!layer.IsChannelEnabled(channel))document.SetChannelEnabled(selectedLayer,channel,true);
                    var c=GetBrush().Color; var to=(Color32)gradientTo;
                    document.Gradient(selectedLayer,channel,new GradientSettings{Shape=gradientShape,X0=a.x,Y0=a.y,X1=b.x,Y1=b.y,From=c,To=new Rgba32(to.r,to.g,to.b,to.a),Opacity=brush.opacity});
                    message="Gradient applied.";repaintPixels=true;break;
                }
                case PaintTool.SelectRectangle:
                    if(click&&mode==SelectionCombine.Replace){document.ClearSelection();message="Deselected.";break;}
                    ApplySelection(SelectionMask.Rectangle(document,Mathf.RoundToInt(Mathf.Min(a.x,b.x)),Mathf.RoundToInt(Mathf.Min(a.y,b.y)),Mathf.RoundToInt(Mathf.Max(a.x,b.x)),Mathf.RoundToInt(Mathf.Max(a.y,b.y))),mode);break;
                case PaintTool.SelectEllipse:
                    if(click&&mode==SelectionCombine.Replace){document.ClearSelection();message="Deselected.";break;}
                    ApplySelection(SelectionMask.Ellipse(document,(a.x+b.x)/2,(a.y+b.y)/2,Mathf.Abs(b.x-a.x)/2,Mathf.Abs(b.y-a.y)/2),mode);break;
                case PaintTool.Move:
                {
                    if(moveMode==MoveMode.Move){var d=MoveDelta(); if(d!=Vector2Int.zero)MoveBy(d.x,d.y); break;}
                    var t=DragTransform(); if(t.IsIdentity)break;
                    if(Math.Abs(t.Determinant)<1e-6)throw new InvalidOperationException("That would scale to nothing; drag the handle less far.");
                    RequireMovableLayer();
                    bool changed=document.Transform(selectedLayer,t,resampling:moveResampling);
                    message=!changed?"Nothing changed.":moveMode==MoveMode.Rotate?"Rotated "+DragAngle().ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"°.":"Scaled.";
                    repaintPixels=true;break;
                }
                case PaintTool.Lasso:
                    if(lassoPoints.Count<3){if(mode==SelectionCombine.Replace){document.ClearSelection();message="Deselected.";}break;}
                    ApplySelection(SelectionMask.Polygon(document,lassoPoints.Select(p=>((double)p.x,(double)p.y)).ToList()),mode);break;
            }
        }
    }
}
