using System;
using UnityEngine;
using Yozolab.YoluPainter.Core;

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

        // 3D ビューのストロークの画面上の点。点の間は 2D と同じ曲線（StrokeCurve）で結ぶので、最新の点への区間は次の点か離した
        // ときまで待つ。previousPointer / previousPressure が描いた最後の点、surfaceBefore がその前の点、surfaceHeld が待たせている点。
        Vector2 surfaceBefore, surfaceHeld; float surfaceHeldPressure; bool surfaceHasBefore, surfaceHasHeld;
        /// <summary>3D ビューのストロークで 1 回の入力（1 区間）に置けるダブの数の上限。超えたらストロークを取り消す。</summary>
        internal const int SurfaceDabsPerEvent = 128;

        /// <summary>ブラシのストローク（2D キャンバスと 3D ビュー）: 押して始め、ドラッグで足し、離して確定する。</summary>
        void HandleBrushInput(Event e)
        {
            if(tool!=PaintTool.Brush&&e.type==EventType.MouseDown&&surfaceRect.Contains(e.mousePosition)&&e.button==0&&!e.alt){message=tool+" works on the 2D canvas. Use the brush, a selection tool or the bucket on the 3D view.";e.Use();return;}
            if(e.type==EventType.MouseDown && e.button==0 && !e.alt && (canvasRect.Contains(e.mousePosition)||surfaceRect.Contains(e.mousePosition)))
            {
                surfaceStroke=surfaceRect.Contains(e.mousePosition);
                if(surfaceStroke && !preview.CanPaint){message="This preview snapshot is not safe to paint. See its load diagnostics.";return;}
                // ほかのテクスチャセットの面では描き始めない（その面をダブルクリックするか、テクスチャセットのパネルで切り替える）
                if(surfaceStroke && preview.TryPick(surfaceRect,e.mousePosition,out var startHit) && startHit.MaterialSlot!=materialSlot){OtherSlotPressed(startHit.MaterialSlot);e.Use();Repaint();return;}
                TryAction(()=>
                {
                    RememberColor();
                    if(EditingMask) stroke=document.BeginMaskStroke(selectedLayer,GetBrush());
                    else
                    {
                        if(document.GetLayer(selectedLayer).IsGroup) throw new InvalidOperationException("A group has no pixels. Select a layer inside it to paint, or paint the group's mask.");
                        document.EnsurePixelsEditable(selectedLayer,brush.erase); // ロックで断るなら、チャンネルを有効にする前に（何も残さない）
                        if(!document.GetLayer(selectedLayer).IsChannelEnabled(channel)) document.SetChannelEnabled(selectedLayer,channel,true);
                        stroke=document.BeginStroke(selectedLayer,channel,GetBrush());
                    }
                    previousPointer=e.mousePosition; previousPressure=Pressure(e); surfaceHasBefore=surfaceHasHeld=false;
                    PaintAt(e.mousePosition,previousPressure); GUIUtility.hotControl=GUIUtility.GetControlID(FocusType.Passive);
                });
                e.Use();Repaint();
            }
            else if(stroke!=null && e.type==EventType.MouseDrag && e.button==0)
            {
                TryAction(()=>
                {
                    float pressure=Pressure(e);
                    if(surfaceStroke) AddSurfacePoint(e.mousePosition,pressure);
                    else PaintAt(e.mousePosition,pressure); // 2D は Core のストロークが曲線で結ぶ（BrushSettings.CurveInterpolation）
                });
                e.Use();Repaint();
            }
            else if(stroke!=null && (e.type==EventType.MouseUp || e.rawType==EventType.MouseUp)){FinishStroke(true);e.Use();Repaint();}
        }
        /// <summary>3D ビューのストロークの新しい点: 待たせていた点までの区間を、この点で向きを決めた曲線で描き、この点を待たせる。</summary>
        void AddSurfacePoint(Vector2 at,float pressure)
        {
            if(!surfaceHasHeld)
            {
                if(StrokeCurve.Coincident(previousPointer.x,previousPointer.y,at.x,at.y))previousPressure=pressure; // 動かない入力は筆圧だけ
                else{surfaceHeld=at;surfaceHeldPressure=pressure;surfaceHasHeld=true;}
                return;
            }
            if(StrokeCurve.Coincident(surfaceHeld.x,surfaceHeld.y,at.x,at.y)){surfaceHeldPressure=pressure;return;}
            PaintSurfaceSegment(at);
            surfaceHeld=at;surfaceHeldPressure=pressure;
        }
        /// <summary>離したとき: 待たせている最後の区間を、その先を折り返した向きで描く。</summary>
        void FinishSurfaceCurve()
        {
            if(!surfaceHasHeld)return;
            StrokeCurve.Reflect(previousPointer.x,previousPointer.y,surfaceHeld.x,surfaceHeld.y,out double x,out double y);
            PaintSurfaceSegment(new Vector2((float)x,(float)y));
            surfaceHasHeld=false;
        }
        /// <summary>previousPointer → surfaceHeld の区間（前は surfaceBefore、後は next で向きを決める）に、画面上の間隔でダブを置く。
        /// 間隔は区間の始まりの面でのブラシの直径から決め、曲線の長さで割り振る。ダブが <see cref="SurfaceDabsPerEvent"/> を超える区間は
        /// 何も描かずに断る（呼ぶ側がストロークを取り消す）。</summary>
        void PaintSurfaceSegment(Vector2 next)
        {
            Vector2 a=previousPointer,b=surfaceHeld,before=surfaceBefore;
            if(!surfaceHasBefore){StrokeCurve.Reflect(b.x,b.y,a.x,a.y,out double rx,out double ry);before=new Vector2((float)rx,(float)ry);}
            float worldRadius=Mathf.Max(.000001f,preview.Bounds.size.magnitude)*brush.radius/document.Width;
            float spacing=1;
            if(preview.TryPick(surfaceRect,a,out var previousHit))spacing=Mathf.Max(.5f,2*preview.WorldRadiusToGuiPoints(previousHit.Position,worldRadius)*brush.spacing);
            // 曲線を細かい折れ線にして長さを測り、長さで等分した位置にダブを置く
            int pieces=Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(a,b)/2),4,256);
            var points=new Vector2[pieces+1]; var lengths=new float[pieces+1]; points[0]=a;
            for(int i=1;i<=pieces;i++)
            {
                if(i==pieces)points[i]=b;
                else{StrokeCurve.Point(before.x,before.y,a.x,a.y,b.x,b.y,next.x,next.y,i/(double)pieces,out double x,out double y);points[i]=new Vector2((float)x,(float)y);}
                lengths[i]=lengths[i-1]+Vector2.Distance(points[i-1],points[i]);
            }
            float total=lengths[pieces];
            int steps=Mathf.Max(1,Mathf.CeilToInt(total/spacing));
            if(steps>SurfaceDabsPerEvent)throw new InvalidOperationException("Surface input exceeded per-event budget; stroke canceled without partial edits. Reduce brush size or move more slowly.");
            float startPressure=previousPressure;
            for(int i=1,j=1;i<=steps;i++)
            {
                float s=i==steps?total:total*i/steps;
                while(j<pieces&&lengths[j]<s)j++;
                float span=lengths[j]-lengths[j-1],f=span>0?Mathf.Clamp01((s-lengths[j-1])/span):1;
                PaintAt(i==steps?b:Vector2.Lerp(points[j-1],points[j],f),Mathf.Lerp(startPressure,surfaceHeldPressure,(j-1+f)/pieces));
            }
            surfaceBefore=a;surfaceHasBefore=true;previousPointer=b;previousPressure=surfaceHeldPressure;
        }
        float Pressure(Event e) => Mathf.Clamp01(brush.pressureCurve.Evaluate(Mathf.Clamp01(e.pressure)));
        void PaintAt(Vector2 pointer,float pressure)
        {
            if(surfaceStroke)
            {
                if(brush.pressureSize && pressure<=0)return;
                if(!surfaceRect.Contains(pointer)||!preview.TryPick(surfaceRect,pointer,out var hit)||hit.MaterialSlot!=materialSlot)return;
                float radius=Mathf.Max(.000001f,preview.Bounds.size.magnitude)*brush.radius/document.Width*(brush.pressureSize?Mathf.Max(.001f,pressure):1);
                // ストロークの間はカメラもモデルも動かないので、テクセルの見え方を覚えて、重なる次のダブで撃ち直さない。シンメトリーなら
                // 映した側のダブも合わせた 1 つのダブ（Model/TexturePaintWindow.Symmetry.cs）
                var dab=BuildStrokeSurfaceDab(hit,radius);
                if(dab.WasClipped)throw new InvalidOperationException(dab.Diagnostic);
                if(!String.IsNullOrEmpty(dab.Diagnostic))message=dab.Diagnostic;
                foreach(var pixel in dab.Pixels)stroke.ApplyPixel(pixel.X,pixel.Y,pixel.Coverage,pressure);
            }
            else
            {
                // 表示の回転・反転・拡大・パンを逆にたどって画素の座標にする（正本と手ぶれ補正・入り抜き・曲線は画素の座標で働く）
                var view=CanvasViewNow(); view.ToCanvas(pointer,out double x,out double y);
                stroke.Add(PenSample(view,x,y,pressure));
            }
            repaintPixels=true;
        }
        void FinishStroke(bool commit)
        {
            if(stroke==null)return;
            try
            {
                if(commit&&surfaceStroke)FinishSurfaceCurve(); // 2D の最後の区間は Commit が描く
                if(commit)stroke.Commit();else stroke.Cancel();
            }
            catch(Exception ex){message=ex.Message;stroke.Cancel();}
            finally{stroke.Dispose();stroke=null;GUIUtility.hotControl=0;repaintPixels=true;surfaceHasHeld=surfaceHasBefore=false;surfaceVisibility=null;}
        }
        /// <summary>今の 3D のストロークのあいだ覚えておく、テクセルの見え方（ストロークが終われば捨てる）。</summary>
        Preview.SurfaceVisibilityCache surfaceVisibility;
        internal Preview.SurfaceVisibilityCache SurfaceVisibilityForTests => surfaceVisibility;
    }
}
