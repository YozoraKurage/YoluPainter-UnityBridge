using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;
using Yozolab.YoluPainter.Editor.Preview;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>Single IMGUI input path: no duplicate pointer/mouse event subscription.</summary>
    public sealed partial class TexturePaintWindow : EditorWindow
    {
        /// <summary>ウィンドウのブラシ設定。brush.json とブラシプリセットのファイルにそのまま JSON で書く。
        /// schema 2 で筆先・ゆらぎ・紙の質感を、schema 3 でダイナミクス（TexturePaintWindow.BrushDynamics.cs）を足した
        /// （古い schema のファイルも読める。足した項目は既定値になる）。</summary>
        [Serializable] internal sealed partial class BrushState
        {
            public int schema = 3;
            public string presetId = "", presetName = "Custom";
            public float radius = 16, hardness = .8f, spacing = .15f, opacity = 1, flow = 1;
            public Color color = new Color(.2f,.6f,1,1);
            public bool pressureSize = true, pressureOpacity = true, pressureFlow, erase;
            public AnimationCurve pressureCurve = AnimationCurve.Linear(0,0,1,1);
            // schema 2
            public string tipId = "", textureId = "";
            public float angle, roundness = 1, sizeJitter, angleJitter, roundnessJitter, opacityJitter, flowJitter, scatter, textureDepth, textureScale = 1;
            public int count = 1;
            public bool followDirection, randomSeedPerStroke = true;
            // 手ぶれ補正と入り抜き（キャンバスの画素、0 で無し）。無い版のファイルは 0 として読む
            public float stabilizer, taperIn, taperOut;
        }
        [Serializable] sealed class ViewState { public string modelAssetGuid; public int materialSlot; public int selectedChannel; }
        [SerializeField] string recoveryRoot;
        [SerializeField] GameObject model;
        [SerializeField] BrushState brush = new BrushState();
        PaintDocument document;
        Guid selectedLayer;
        PaintChannel channel;
        BrushStroke stroke;
        TileGpuCompositor compositor;
        IsolatedModelPreview preview;
        string projectPath, projectToken, recoveryToken, message = "";
        long renderedRevision = -1, savedRevision = -1, recoveredRevision = -1;
        bool repaintPixels = true, surfaceStroke, externalConflict, editMask;
        Vector2 previousPointer, layerScroll, brushScroll, canvasPan;
        float canvasZoom = 1, previousPressure = 1;
        /// <summary>キャンバスでの左ボタンの働き。</summary>
        internal enum PaintTool { Brush, Fill, Gradient, SelectRectangle, SelectEllipse, Lasso, MagicWand, Move, Path, Eyedropper }
        PaintTool tool;
        int wandTolerance = 32; bool wandContiguous = true, wandSampleAll;
        Color gradientTo = new Color(0, 0, 0, 0); GradientShape gradientShape;
        // 移動ツール: ドラッグ開始時に動かすものの範囲（プレビューの枠）と、数値で変形する値
        (int x0, int y0, int x1, int y1)? moveBounds;
        float moveAngle; Vector2 moveScale = new Vector2(100, 100), moveOffset; Resampling moveResampling;
        // ドラッグで形を決めるツール（グラデーション・矩形/楕円/投げ縄選択）の途中の状態。キャンバスの画素座標（左下原点）
        bool toolDragging; Vector2 toolStart, toolCurrent; readonly List<Vector2> lassoPoints = new List<Vector2>();
        Texture2D selectionOverlay; SelectionMask overlayFor;
        int materialSlot, resolution = 1024;
        bool showDynamics;
        double lastRecovery, lastExternalCheck;
        byte[] importedOriginal;
        /// <summary>このドキュメントを取り込んだ PSD のパス（取り込んでからまだ .ylp に保存していなければ保存先の提案に使う）。</summary>
        string importedPsdPath;
        Rect canvasRect, surfaceRect;

        [MenuItem("YozoLab/YoluPainter (Prototype)")]
        public static void Open() => GetWindow<TexturePaintWindow>("Texture Painter");

        // EditMode テスト用の参照口。入力は SendEvent で本物の経路を通す。
        internal PaintDocument Document => document;
        internal bool IsStroking => stroke != null;
        internal IsolatedModelPreview Preview => preview;
        internal TileGpuCompositor Compositor => compositor;
        internal Rect SurfaceRect => surfaceRect;
        internal string StatusMessage => message;
        internal string RecoveryRoot => recoveryRoot;
        /// <summary>開いている .ylp の絶対パス。まだ保存していなければ null。</summary>
        internal string ProjectPath => projectPath;
        internal bool IsSaved => document != null && document.Revision == savedRevision;
        internal bool HasExternalConflict => externalConflict;
        internal PaintChannel Channel { get => channel; set { channel = value; repaintPixels = true; } }
        /// <summary>true のあいだ、ストロークは選択レイヤーの画素ではなくマスクに入る。</summary>
        internal bool EditMask { get => editMask; set => editMask = value; }
        internal Guid SelectedLayer { get => selectedLayer; set => selectedLayer = value; }
        /// <summary>モーダルダイアログの差し替え口（テスト用）。</summary>
        internal IPainterDialogs Dialogs { get; set; } = EditorPainterDialogs.Instance;
        /// <summary>PaintAt の 2D 写像の逆。ピクセル中心 (x+0.5, y+0.5) の GUI 座標を返す。</summary>
        internal Vector2 PixelToGui(int x, int y)
        {
            var image = ImageRect();
            return new Vector2(image.x + (x + .5f) / document.Width * image.width, image.y + (1 - (y + .5f) / document.Height) * image.height);
        }

        void OnEnable()
        {
            minSize = new Vector2(980,640); wantsMouseMove = true; L.LanguageChanged += Repaint; PainterToolIcons.Changed += Repaint;
            compositor = new TileGpuCompositor(); preview = new IsolatedModelPreview();
            if (String.IsNullOrEmpty(recoveryRoot)) recoveryRoot=Path.GetFullPath(Path.Combine("Library","YoluPainter","recovery-"+Guid.NewGuid().ToString("N")));
            try
            {
                if (File.Exists(Path.Combine(recoveryRoot,"current")))
                {
                    var snapshot=GenerationStore.Load(recoveryRoot); document=DocumentBinary.Read(snapshot.Files["document.utpaint"]); recoveryToken=snapshot.Token;
                    var recoveryNotes=new List<string>(); RestoreSavedSelection(snapshot.Files,recoveryNotes);
                    message="Recovered native source from the last durable checkpoint. Unsaved edits after that checkpoint may be missing."+(recoveryNotes.Count>0?" "+String.Join(" ",recoveryNotes):"");
                }
            }
            catch (Exception ex) { message="Recovery was not loaded: "+ex.Message; }
            resolution=PainterSettings.DefaultResolution;
            if (document==null) CreateDocument(resolution);
            selectedLayer=document.Layers.Count>0?document.Layers[document.Layers.Count-1].Id:Guid.Empty;
            BindDocument();
            if (model!=null) TryAction(()=>preview.Load(model));
            EditorApplication.update+=Tick; PainterSettings.Changed+=SettingsChanged;
            AssemblyReloadEvents.beforeAssemblyReload+=BeforeReload;
            EditorApplication.playModeStateChanged+=PlayModeChanged;
        }
        /// <summary>設定のメモリ予算をドキュメントに入れる。今の画素がすでに予算を超えているときは画素を捨てず、予算を今の量まで
        /// 広げてそう知らせる。</summary>
        /// <returns>予算を広げたときの知らせ。問題なければ null。</returns>
        internal string ApplyBudgets()
        {
            if(document==null||stroke!=null)return null;
            if(compositor!=null)compositor.ResidentBudgetBytes=PainterSettings.GpuCacheBytes;
            document.MinimumUndoSteps=PainterSettings.MinUndoSteps; document.UndoBudgetBytes=PainterSettings.UndoBudgetBytes; document.ActiveStrokeBudgetBytes=PainterSettings.StrokeBudgetBytes;
            long source=PainterSettings.SourceBudgetBytes;
            if(source>=document.AllocatedBytes){document.SourceBudgetBytes=source;return null;}
            document.SourceBudgetBytes=document.AllocatedBytes;
            return "This document already holds "+(document.AllocatedBytes>>20)+" MiB of layer pixels, above the "+(source>>20)+" MiB budget in Project Settings > YoluPainter; nothing more can be added until the budget is raised.";
        }
        void SettingsChanged(){var note=ApplyBudgets();if(note!=null)message=note;Repaint();}
        internal static void OpenSettings()=>SettingsService.OpenProjectSettings(PainterSettingsProvider.Path);
        void BindDocument()
        {
            var budgetNote=ApplyBudgets(); if(budgetNote!=null)message=budgetNote;
            document.HistoryTrimming += bytes => message="Undo budget reached; dropping "+(bytes/1024)+" KiB of the oldest history (the newest "+document.MinimumUndoSteps+" steps are always kept). Current source remains intact.";
            repaintPixels=true; renderedRevision=-1; recoveredRevision=-1;
            ClearMeshMaps();
        }
        void CreateDocument(int size)
        {
            document=new PaintDocument(size,size,128,PainterSettings.UndoBudgetBytes);
            ApplyBudgets();
            selectedLayer=document.AddLayer(L.Tr("Layer")+" 1").Id; document.ClearHistory(); pristineRevision=document.Revision;
            projectPath=null; projectToken=null; savedRevision=-1; importedOriginal=null; importedPsdPath=null; externalConflict=false; canvasZoom=1; canvasPan=Vector2.zero;
        }
        void OnLostFocus() { FinishStroke(false); CancelToolDrag(); preview?.CancelNavigation(); SaveRecovery(); }
        void BeforeReload() { FinishStroke(false); preview?.CancelNavigation(); SaveRecovery(); }
        void PlayModeChanged(PlayModeStateChange state) { if(state==PlayModeStateChange.ExitingEditMode){ FinishStroke(false); SaveRecovery(); } }
        void OnDisable()
        {
            FinishStroke(false); preview?.CancelNavigation(); SaveRecovery();
            EditorApplication.update-=Tick; PainterSettings.Changed-=SettingsChanged; L.LanguageChanged-=Repaint; PainterToolIcons.Changed-=Repaint; AssemblyReloadEvents.beforeAssemblyReload-=BeforeReload; EditorApplication.playModeStateChanged-=PlayModeChanged;
            DisposeNormalOutput(); DisposeLighting(); DisposeMeshMaps(); DisposeThumbnails(); compositor?.Dispose(); preview?.Dispose(); compositor=null; preview=null;
            if(selectionOverlay!=null){DestroyImmediate(selectionOverlay);selectionOverlay=null;overlayFor=null;}
        }
        void Tick()
        {
            if(document==null) return;
            if(stroke==null && document.Revision!=recoveredRevision && EditorApplication.timeSinceStartup-lastRecovery>PainterSettings.RecoveryIntervalSeconds) SaveRecovery();
            if(!String.IsNullOrEmpty(projectPath) && EditorApplication.timeSinceStartup-lastExternalCheck>3) CheckExternalChange();
            // 描いていないあいだは GPU の写しを手放す（Update が来ないと合成器は古い写しを捨てられない）
            if(compositor!=null && compositor.ResidentBytes>0 && EditorApplication.timeSinceStartup-lastComposite>GpuCacheIdleSeconds) compositor.ReleaseResidentCaches();
        }
        internal const double GpuCacheIdleSeconds=120;
        double lastComposite;
        internal void CheckExternalChange()
        {
            if(String.IsNullOrEmpty(projectPath)) return;
            lastExternalCheck=EditorApplication.timeSinceStartup;
            externalConflict=YlpStore.HasExternalChange(projectPath,projectToken);
            if(externalConflict) message="The saved file changed outside this window. Normal save is blocked; use Save As or explicitly reopen after reviewing local edits.";
        }
        void OnGUI()
        {
            if(document==null) return;
            var e=Event.current; var pointerAtStart=e.mousePosition; // 途中のクリップや 3D の描画の後でも同じ位置を使う
            if(e.type==EventType.MouseMove) Repaint(); // マウスの乗った部品の見た目
            // スライダーのドラッグ中の変更は 1 つの Undo にまとめる。離したところで区切る。
            if(e.rawType==EventType.MouseUp) document.EndCoalescing();
            HandleModelPicker(e);
            HandleKeys(e);
            if(e.type==EventType.KeyDown&&HandleToolKeys(e))return;
            if(repaintPixels || renderedRevision!=document.Revision)
            {
                TryAction(()=> { compositor.Update(document,channel); UpdateNormalOutput(); preview.SetPaintTexture(DisplayTexture, materialSlot); });
                TryAction(UpdatePreviewLighting);
                lastComposite=EditorApplication.timeSinceStartup;
                renderedRevision=document.Revision; repaintPixels=false;
            }
            LayoutShell();
            PaintGui.Fill(WindowRect,PaintTheme.WindowBg);
            if(canvasRect.width>0) DrawCanvas();
            if(surfaceRect.width>0 && e.type==EventType.Repaint){ PaintGui.Fill(surfaceRect,PaintTheme.CanvasBg); preview.Render(surfaceRect); DrawPathMarkers(); }
            DrawShell();
            if(surfaceRect.width>0) DrawSurfaceBrushCursor(pointerAtStart); // 3D の描画の後に GUI の状態を戻してから重ねる
            HandleCanvasInput(e);
        }
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
        /// <summary>選ばれていない所を暗く覆う表示用のテクスチャ（選択範囲が変わったときだけ作り直す）。</summary>
        void EnsureSelectionOverlay()
        {
            var selection=document.Selection;
            if(ReferenceEquals(selection,overlayFor)&&selectionOverlay!=null)return;
            if(selectionOverlay==null||selectionOverlay.width!=document.Width||selectionOverlay.height!=document.Height)
            {
                if(selectionOverlay!=null)DestroyImmediate(selectionOverlay);
                selectionOverlay=new Texture2D(document.Width,document.Height,TextureFormat.RGBA32,false,true){hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point};
            }
            var pixels=new Color32[document.Width*document.Height]; int tile=document.TileSize; var amounts=new byte[tile*tile];
            for(int i=0;i<pixels.Length;i++)pixels[i]=new Color32(0,0,0,110);
            foreach(var coord in selection.Tiles)
            {
                selection.CopyTile(coord,amounts);
                int w=Math.Min(tile,document.Width-coord.X*tile),h=Math.Min(tile,document.Height-coord.Y*tile);
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)pixels[(coord.Y*tile+y)*document.Width+coord.X*tile+x]=new Color32(0,0,0,(byte)(110*(255-amounts[y*tile+x])/255));
            }
            selectionOverlay.SetPixels32(pixels);selectionOverlay.Apply(false,false);overlayFor=selection;
        }
        Vector2 ToGui(Rect image,Vector2 p)=>new Vector2(image.x+p.x/document.Width*image.width,image.y+(1-p.y/document.Height)*image.height);
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
        /// <summary>GUI の座標をキャンバスの画素座標（左下原点、範囲外も返す）に。</summary>
        Vector2 CanvasPoint(Vector2 pointer){var image=ImageRect();return new Vector2((pointer.x-image.x)/image.width*document.Width,(1-(pointer.y-image.y)/image.height)*document.Height);}
        static SelectionCombine CombineOf(Event e)=>e.shift&&(e.control||e.command)?SelectionCombine.Intersect:e.shift?SelectionCombine.Add:(e.control||e.command)?SelectionCombine.Subtract:SelectionCombine.Replace;
        /// <summary>新しい形を今の選択範囲と組み合わせて選択範囲にする（Shift 追加、Ctrl 削除、Shift+Ctrl 交差）。</summary>
        internal void ApplySelection(SelectionMask shape,SelectionCombine mode)
        {
            var current=document.Selection;
            SelectionMask next=mode==SelectionCombine.Replace||current==null?(mode==SelectionCombine.Subtract?null:shape):current.Combine(shape,mode);
            document.SetSelection(next);
            message=document.Selection==null?"Nothing selected.":"Selection: "+mode+".";
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
        Vector2 CanvasToWindow(Vector2 p){var image=ImageRect();return new Vector2(image.x+p.x/document.Width*image.width,image.y+(1-p.y/document.Height)*image.height);}
        /// <summary>ハンドルを出せる大きさか（小さい範囲では、どこを掴んでも移動にする）。</summary>
        bool HandlesUsable((int x0,int y0,int x1,int y1) b)
        {
            var a=CanvasToWindow(new Vector2(b.x0,b.y0)); var c=CanvasToWindow(new Vector2(b.x1,b.y1));
            return Mathf.Abs(c.x-a.x)>=MinHandleBoxPoints&&Mathf.Abs(c.y-a.y)>=MinHandleBoxPoints;
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
            var lo=CanvasToWindow(new Vector2(b.x0,b.y1)); var hi=CanvasToWindow(new Vector2(b.x1,b.y0)); // ウィンドウでは y が下向き
            var box=Rect.MinMaxRect(lo.x,lo.y,hi.x,hi.y);
            if(box.Contains(pointer))return MoveMode.Move;
            for(int i=0;i<4;i++) if(Vector2.Distance(CanvasToWindow(points[i]),pointer)<=RotateReachPoints) return MoveMode.Rotate;
            return MoveMode.Move;
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
        void DrawTransformHandles(Rect image)
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
            Vector2 a=ToGui(image,new Vector2(b.x0,b.y0)),c=ToGui(image,new Vector2(b.x1,b.y1));
            Handles.DrawAAPolyLine(1f,new Vector3(a.x,a.y),new Vector3(c.x,a.y),new Vector3(c.x,c.y),new Vector3(a.x,c.y),new Vector3(a.x,a.y));
            if(!HandlesUsable(b))return;
            foreach(var h in HandlePoints(b)){var g=ToGui(image,h);EditorGUI.DrawRect(new Rect(g.x-3,g.y-3,6,6),new Color(1,1,1,.9f));}
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
        internal enum SelectionModifyKind { Grow, Shrink, Border, Feather, Sharpen }
        int selectionRadius = 5; bool selectionEdgeLock;
        internal int SelectionRadius { get => selectionRadius; set => selectionRadius = Mathf.Clamp(value, 0, SelectionMask.MaxModifyRadius); }
        internal bool SelectionEdgeLock { get => selectionEdgeLock; set => selectionEdgeLock = value; }
        /// <summary>選択範囲を変える（GIMP の Select メニューと同じ考え方）。1 回の Undo。</summary>
        internal void ModifySelection(SelectionModifyKind kind)
        {
            var current=document.Selection;
            if(current==null){message="Nothing is selected.";return;}
            int r=selectionRadius;
            SelectionMask next;
            switch(kind)
            {
                case SelectionModifyKind.Grow: next=current.Grow(r); break;
                case SelectionModifyKind.Shrink: next=current.Shrink(r,selectionEdgeLock); break;
                case SelectionModifyKind.Border: next=current.Border(r,selectionEdgeLock); break;
                case SelectionModifyKind.Feather: next=current.Feather(r,selectionEdgeLock); break;
                default: next=current.Sharpen(); break;
            }
            document.SetSelection(next);
            message=document.Selection==null?kind+": nothing is left selected.":kind==SelectionModifyKind.Sharpen?"Selection sharpened.":kind+" by "+r+" px.";
        }
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
            if(tool!=PaintTool.Brush&&e.type==EventType.MouseDown&&surfaceRect.Contains(e.mousePosition)&&e.button==0&&!e.alt){message=tool+" works on the 2D canvas. Use the brush, a selection tool or the bucket on the 3D view.";e.Use();return;}
            if(e.type==EventType.MouseDown && e.button==0 && !e.alt && (canvasRect.Contains(e.mousePosition)||surfaceRect.Contains(e.mousePosition)))
            {
                surfaceStroke=surfaceRect.Contains(e.mousePosition);
                if(surfaceStroke && !preview.CanPaint){message="This preview snapshot is not safe to paint. See its load diagnostics.";return;}
                TryAction(()=>
                {
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
        static readonly System.Random seeds = new System.Random();
        internal BrushState Brush { get => brush; set => brush = value; }
        internal PaintTool Tool { get => tool; set { CancelToolDrag(); tool = value; } }
        internal int WandTolerance { get => wandTolerance; set => wandTolerance = Mathf.Clamp(value, 0, 255); }
        internal bool WandContiguous { get => wandContiguous; set => wandContiguous = value; }
        internal bool WandSampleAll { get => wandSampleAll; set => wandSampleAll = value; }
        internal Color GradientTo { get => gradientTo; set => gradientTo = value; }
        internal float MoveAngle { get => moveAngle; set => moveAngle = value; }
        internal Vector2 MoveScale { get => moveScale; set => moveScale = value; }
        internal Vector2 MoveOffset { get => moveOffset; set => moveOffset = value; }
        internal Resampling MoveResampling { get => moveResampling; set => moveResampling = value; }
        static BrushState ReadBrushState(string json)
        {
            var b=JsonUtility.FromJson<BrushState>(json);
            if(b==null||b.schema<1||b.schema>3||b.pressureCurve==null)throw new InvalidDataException("Unsupported brush settings");
            if(b.schema<2){b.roundness=1;b.textureScale=1;b.count=1;b.randomSeedPerStroke=true;b.schema=2;}
            UpgradeBrushState(b);
            return b;
        }
        internal BrushSettings GetBrush() { var s = NewBrushSettings(); BrushTips.Apply(s, brush.tipId);
            ApplyBrushDynamics(s);
            return s; }
        BrushSettings NewBrushSettings() => new BrushSettings { Radius=brush.radius, Hardness=brush.hardness, Spacing=brush.spacing, Opacity=brush.opacity, Flow=brush.flow,
            Color=new Rgba32((byte)Mathf.RoundToInt(brush.color.r*255),(byte)Mathf.RoundToInt(brush.color.g*255),(byte)Mathf.RoundToInt(brush.color.b*255),(byte)Mathf.RoundToInt(brush.color.a*255)),
            PressureSize=brush.pressureSize,PressureOpacity=brush.pressureOpacity,PressureFlow=brush.pressureFlow,Erase=brush.erase,
            Texture=BrushTips.Resolve(brush.textureId), Angle=brush.angle, Roundness=brush.roundness, FollowDirection=brush.followDirection,
            SizeJitter=brush.sizeJitter, AngleJitter=brush.angleJitter, RoundnessJitter=brush.roundnessJitter, OpacityJitter=brush.opacityJitter, FlowJitter=brush.flowJitter,
            Scatter=brush.scatter, Count=brush.count, TextureDepth=brush.textureDepth, TextureScale=brush.textureScale,
            Seed=brush.randomSeedPerStroke ? seeds.Next() : 0, Stabilizer=brush.stabilizer, TaperIn=brush.taperIn, TaperOut=brush.taperOut };
        /// <summary>プリセットの設定を今のブラシに写す。色は今のまま残す（チャンネルの値として選んだものだから）。</summary>
        internal void ApplyPreset(Core.BrushPreset preset)
        {
            var s=preset.CreateSettings(); var color=brush.color; var curve=brush.pressureCurve; var assist=(brush.stabilizer,brush.taperIn,brush.taperOut);
            var secondary=brush.secondaryColor;
            brush=new BrushState{ presetId=preset.Id, presetName=preset.Name, radius=(float)s.Radius, hardness=(float)s.Hardness, spacing=(float)s.Spacing, opacity=(float)s.Opacity, flow=(float)s.Flow,
                color=color, pressureCurve=curve, pressureSize=s.PressureSize, pressureOpacity=s.PressureOpacity, pressureFlow=s.PressureFlow, erase=s.Erase,
                tipId=BrushTips.IdOf(s), textureId=BrushTips.IdOf(s.Texture), angle=(float)s.Angle, roundness=(float)s.Roundness, followDirection=s.FollowDirection,
                sizeJitter=(float)s.SizeJitter, angleJitter=(float)s.AngleJitter, roundnessJitter=(float)s.RoundnessJitter, opacityJitter=(float)s.OpacityJitter, flowJitter=(float)s.FlowJitter,
                scatter=(float)s.Scatter, count=s.Count, textureDepth=(float)s.TextureDepth, textureScale=(float)s.TextureScale,
                stabilizer=assist.Item1, taperIn=assist.Item2, taperOut=assist.Item3 }; // 補正と入り抜きは描き手の設定として残す
            CopyPresetDynamics(s,secondary);
        }
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
        void HandleKeys(Event e)
        {
            if(e.type!=EventType.KeyDown)return;
            if(e.keyCode==KeyCode.Escape && toolDragging){CancelToolDrag();GUIUtility.hotControl=0;e.Use();Repaint();}
            else if(e.keyCode==KeyCode.Escape && stroke!=null){FinishStroke(false);e.Use();}
            else if(stroke==null && !toolDragging && tool==PaintTool.Move && GUIUtility.keyboardControl==0 && !(e.control||e.command) && ArrowDelta(e.keyCode)!=Vector2Int.zero)
            {var d=ArrowDelta(e.keyCode)*(e.shift?10:1);TryAction(()=>MoveBy(d.x,d.y));e.Use();Repaint();}
            else if(stroke==null && tool==PaintTool.Path && GUIUtility.keyboardControl==0 && (e.keyCode==KeyCode.Delete||e.keyCode==KeyCode.Backspace)){TryAction(RemoveLastPathPoint);e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.A){document.SetSelection(SelectionMask.All(document));e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.D){document.ClearSelection();e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.shift && e.keyCode==KeyCode.I){if(document.Selection!=null)document.SetSelection(document.Selection.Invert());e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.Z){if(e.shift)document.Redo();else document.Undo();e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.S){SaveProject(e.shift);e.Use();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.O){OpenProject();e.Use();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.N){NewProjectDialog();e.Use();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.Alpha0){canvasZoom=1;canvasPan=Vector2.zero;e.Use();Repaint();}
        }
        static Vector2Int ArrowDelta(KeyCode key)=>key==KeyCode.LeftArrow?Vector2Int.left:key==KeyCode.RightArrow?Vector2Int.right:key==KeyCode.UpArrow?Vector2Int.up:key==KeyCode.DownArrow?Vector2Int.down:Vector2Int.zero;
        /// <summary>作った直後で何も手を加えていないドキュメントの版（New / 最初に開いたとき）。捨てても失うものが無いので確かめない。</summary>
        long pristineRevision=-1;
        bool ConfirmDiscard() => document.Revision==savedRevision || document.Revision==pristineRevision || Dialogs.Confirm("Keep current work?","Current work has unsaved changes. A native recovery checkpoint will be kept before opening another document.","Continue","Cancel") && SaveRecovery();
        bool SaveRecovery()
        {
            if(document==null||stroke!=null||document.Revision==recoveredRevision)return true;
            try
            {
                var files=new Dictionary<string,byte[]>{{"document.utpaint",DocumentBinary.Write(document)}};
                if(document.Selection!=null)files.Add(SelectionBinary.EntryName,SelectionBinary.Write(document.Selection));
                var snapshot=GenerationStore.Commit(recoveryRoot,files,recoveryToken); recoveryToken=snapshot.Token;
                recoveredRevision=document.Revision;lastRecovery=EditorApplication.timeSinceStartup;return true;
            }
            catch(Exception ex){message="Recovery checkpoint failed: "+ex.Message;return false;}
        }
        /// <summary>保存した選択範囲を戻す（履歴も版も増やさない）。読めなければ選択なしで開き、そのことを知らせる（文書は開ける）。</summary>
        void RestoreSavedSelection(IReadOnlyDictionary<string,byte[]> files,List<string> notes)
        {
            if(!files.TryGetValue(SelectionBinary.EntryName,out var bytes))return;
            try{document.RestoreSelection(SelectionBinary.Read(bytes,document));}
            catch(InvalidDataException ex){notes.Add("The saved selection was not restored ("+ex.Message+"); nothing is selected, and saving will leave it out.");}
        }
        /// <summary>.ylp に保存する。上書きは開いた/保存した時点から外で変わっていないときだけで、直前の版は
        /// &lt;名前&gt;.ylp-backups~ に退避する（保持数は設定）。Assets の中のファイルなら保存後に取り込み直して、Project ウィンドウの情報とサムネイルを更新する。</summary>
        internal void SaveProject(bool saveAs)
        {
            if(stroke!=null)return;
            string target=projectPath;
            if(saveAs||String.IsNullOrEmpty(target))
            {
                var (suggestFolder,suggestName)=SaveSuggestion();
                target=Dialogs.SaveFile("Save YoluPainter file",suggestFolder,suggestName,"ylp");
                if(String.IsNullOrEmpty(target))return;
                if(!target.EndsWith(YlpArchive.Extension,StringComparison.OrdinalIgnoreCase))target+=YlpArchive.Extension;
                target=Path.GetFullPath(target);
            }
            bool sameFile=projectPath!=null&&String.Equals(target,projectPath,PainterSettings.PathComparison);
            int keep=PainterSettings.BackupsToKeep;
            // 別のファイルを上書きするとき: 退避するなら元の版は残るので尋ねない。退避しない設定なら元の版が消えるので確かめる。
            if(!sameFile&&File.Exists(target)&&keep==0&&!Dialogs.Confirm("Replace file?",Path.GetFileName(target)+" already exists and backups are turned off in Project Settings > YoluPainter. Replace it? The old file will be gone.","Replace","Cancel"))return;
            TryAction(()=>
            {
                Dialogs.Progress("YoluPainter","Freezing native source and writing a verified .ylp. Input is paused.",.1f);
                var files=YlpContent.Composites(document);
                files.Add(YlpArchive.NativeName,DocumentBinary.Write(document));
                var state=new ViewState{modelAssetGuid=model==null?"":AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(model)),materialSlot=materialSlot,selectedChannel=(int)channel};
                files.Add(YlpContent.ViewName,System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(state,true)));
                files.Add(YlpContent.BrushName,System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(brush,true)));
                if(importedOriginal!=null)files.Add(YlpContent.ImportedOriginalName,importedOriginal);
                AddMeshMapFiles(files);
                if(document.Selection!=null)files.Add(SelectionBinary.EntryName,SelectionBinary.Write(document.Selection));
                var saved=YlpStore.Save(target,files,sameFile?projectToken:null,!sameFile,keep);
                projectPath=saved.Path;projectToken=saved.Token;savedRevision=document.Revision;externalConflict=false;MeshMapsWereSaved();
                message="Saved "+Path.GetFileName(saved.Path)+(saved.Backup!=null?"; the previous version is kept in "+Path.GetFileName(Path.GetDirectoryName(saved.Backup))+".":".");
                string asset=AssetPathOf(saved.Path);
                if(asset!=null)AssetDatabase.ImportAsset(asset,ImportAssetOptions.ForceUpdate);
            });
            Dialogs.ClearProgress();
        }
        /// <summary>Unity プロジェクトの Assets の中なら "Assets/..." の形、外なら null。</summary>
        static string AssetPathOf(string fullPath)
        {
            string assets=Path.GetFullPath(Application.dataPath).TrimEnd('/','\\')+Path.DirectorySeparatorChar;
            return fullPath.StartsWith(assets,PainterSettings.PathComparison)?"Assets/"+fullPath.Substring(assets.Length).Replace('\\','/'):null;
        }
        internal void OpenProject()
        {
            string path=Dialogs.OpenFile("Open YoluPainter file",projectPath!=null?Path.GetDirectoryName(projectPath):Application.dataPath,"ylp");
            OpenProjectAt(path);
        }
        /// <summary>.ylp を YoluPainter のウィンドウで開く（ダブルクリックなど）。未保存の作業があれば確認する。</summary>
        internal static TexturePaintWindow OpenFileInWindow(string path)
        {
            var w=GetWindow<TexturePaintWindow>("Texture Painter"); w.Show(); w.Focus(); w.OpenProjectAt(path); return w;
        }
        internal void OpenProjectAt(string path)
        {
            if(String.IsNullOrEmpty(path))return;
            path=Path.GetFullPath(path);
            if(projectPath!=null&&String.Equals(path,projectPath,PainterSettings.PathComparison)&&document.Revision==savedRevision&&!externalConflict){message="Already open: "+Path.GetFileName(path);return;}
            if(!ConfirmDiscard())return;
            TryAction(()=>
            {
                var snapshot=YlpStore.Load(path);var next=DocumentBinary.Read(snapshot.Files[YlpArchive.NativeName]);
                document=next;BindDocument();selectedLayer=document.Layers.Count>0?document.Layers[document.Layers.Count-1].Id:Guid.Empty;
                projectPath=snapshot.Path;projectToken=snapshot.Token;savedRevision=document.Revision;externalConflict=false;
                importedOriginal=snapshot.Files.TryGetValue(YlpContent.ImportedOriginalName,out var original)?original:null;
                var notes=new List<string>();
                var budgetNote=ApplyBudgets(); if(budgetNote!=null)notes.Add(budgetNote);
                if(snapshot.Files.TryGetValue(YlpContent.BrushName,out var preset)){brush=ReadBrushState(System.Text.Encoding.UTF8.GetString(preset));var missing=MissingTipNote();if(missing!=null)notes.Add(missing);}
                if(snapshot.Files.TryGetValue(YlpContent.ViewName,out var view))
                {
                    var state=JsonUtility.FromJson<ViewState>(System.Text.Encoding.UTF8.GetString(view));materialSlot=state.materialSlot;channel=(PaintChannel)state.selectedChannel;
                    var loaded=AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(state.modelAssetGuid));
                    if(loaded!=null){model=loaded;preview.Load(model);materialSlot=Mathf.Clamp(materialSlot,0,Mathf.Max(0,preview.MaterialSlotCount-1));}else if(!String.IsNullOrEmpty(state.modelAssetGuid))notes.Add("Model asset is unavailable; assign it explicitly.");
                }
                LoadMeshMapFiles(snapshot.Files,notes);
                RestoreSavedSelection(snapshot.Files,notes);
                message="Opened "+Path.GetFileName(path)+" (verified)"+(notes.Count>0?". "+String.Join(" ",notes):"");
            });
        }
        internal void ImportPsd()
        {
            string path=Dialogs.OpenFile("Inspect/import RGB8 PSD",Application.dataPath,"psd");if(String.IsNullOrEmpty(path))return;
            TryAction(()=>
            {
                if(new FileInfo(path).Length>128L*1024*1024)throw new InvalidOperationException("PSD exceeds 128 MiB prototype input budget.");
                var result=PsdCodec.Read(File.ReadAllBytes(path));
                if(result.Mode!=PsdCompatibilityMode.EditableRaster){Dialogs.Inform("PSD protected: "+result.Mode,String.Join("\n",result.Diagnostics.Select(d=>d.ToString()))+"\nOriginal file was not modified. Unsupported features cannot be edited here.");return;}
                var next=PsdBridge.Import(result);if(!ConfirmDiscard())return;
                document=next;if(document.Layers.Count==0)document.AddLayer(L.Tr("Layer")+" 1");BindDocument();selectedLayer=document.Layers.Last().Id;projectPath=null;projectToken=null;savedRevision=-1;
                importedOriginal=result.CopyOriginalBytes();importedPsdPath=Path.GetFullPath(path);channel=PaintChannel.Color;
                message="Imported "+Path.GetFileName(path)+". Save keeps it as a .ylp (with the original PSD inside); Export PSD writes a PSD. The PSD itself is never rewritten.";
                // 編集できる取り込みでも、書き出す PSD に含まれない情報や合成結果の差などの注意があれば一覧で見せる（黙って捨てない）
                var notes=result.Diagnostics.Select(d=>d.ToString()).ToList();
                if(notes.Count>0)Dialogs.Inform("PSD imported with notes",String.Join("\n",notes.Take(40))+(notes.Count>40?"\n… and "+(notes.Count-40)+" more.":"")+"\n\nThe original PSD bytes are kept inside the .ylp when you save.");
            });
        }
        /// <summary>選んだチャンネルを PSD に書き出す。PSD で表せないもの（通常以外の合成・マスク・Fill・調整・クリッピングなど）が
        /// あれば、平らにせずに理由を示して書かない。</summary>
        internal void ExportPsd()
        {
            byte[] bytes;
            try{bytes=PsdCodec.Write(PsdBridge.Export(document,channel));}
            catch(Exception ex){message="PSD export unavailable: "+ex.Message;Dialogs.Inform("PSD export unavailable",ex.Message+"\n\nNothing was written. The .ylp keeps everything losslessly.");return;}
            string source=projectPath??importedPsdPath;
            string stem=source!=null?Path.GetFileNameWithoutExtension(source):"Texture";
            string path=Dialogs.SaveFile("Export selected channel PSD",source!=null?Path.GetDirectoryName(source):Application.dataPath,channel==PaintChannel.Color?stem:stem+"_"+channel,"psd");if(String.IsNullOrEmpty(path))return;
            // 取り込み元の PSD を上書きするときは確かめる（原本のバイト列は .ylp に残るが、外の PSD そのものは置き換わる）
            if(importedPsdPath!=null&&String.Equals(Path.GetFullPath(path),importedPsdPath,PainterSettings.PathComparison)&&!Dialogs.Confirm("Overwrite the imported PSD?",Path.GetFileName(path)+" is the PSD this document was imported from. Replace it with the exported PSD?"+(importedOriginal!=null?" Its original bytes stay inside the .ylp once you save.":""),"Replace","Cancel"))return;
            TryAction(()=>{File.WriteAllBytes(path,bytes);message="Exported "+channel+" PSD. No material was changed."+NormalExportNote(channel==PaintChannel.Normal,psd:true);});
        }
        /// <summary>使っている全チャンネルを &lt;名前&gt;_&lt;チャンネル&gt;.png としてフォルダに書き出す。既存のファイルを置き換えるときは
        /// 確かめる。Assets の中なら取り込み直し、新しく作ったテクスチャにだけ色空間（Color/Emission は sRGB、他はリニア）を設定する。
        /// 既にあるテクスチャの取り込み設定は変えず、合っていなければ知らせる。マテリアルには割り当てない。</summary>
        internal void ExportImages()
        {
            var channels=YlpContent.UsedChannels(document);
            if(channels.Count==0){message="Nothing to export: no layer uses any channel.";return;}
            string folder=Dialogs.OpenFolder("Export images into folder",projectPath!=null?Path.GetDirectoryName(projectPath):Application.dataPath);if(String.IsNullOrEmpty(folder))return;
            string stem=projectPath!=null?Path.GetFileNameWithoutExtension(projectPath):"Texture";
            var targets=channels.Select(c=>(channel:c,path:Path.Combine(folder,stem+"_"+c+".png"))).ToList();
            var existing=targets.Where(t=>File.Exists(t.path)).Select(t=>Path.GetFileName(t.path)).ToList();
            if(existing.Count>0&&!Dialogs.Confirm("Replace images?","These files will be replaced:\n"+String.Join("\n",existing),"Replace","Cancel"))return;
            TryAction(()=>
            {
                var created=new List<(PaintChannel channel,string asset)>(); var notes=new List<string>();
                foreach(var t in targets)
                {
                    bool isNew=!File.Exists(t.path);
                    File.WriteAllBytes(t.path,YlpContent.EncodePng(YlpContent.FileImage(document,t.channel),document.Width,document.Height));
                    string asset=AssetPathOf(Path.GetFullPath(t.path));
                    if(asset==null)continue;
                    AssetDatabase.ImportAsset(asset,ImportAssetOptions.ForceUpdate);
                    var importer=AssetImporter.GetAtPath(asset) as TextureImporter; if(importer==null)continue;
                    bool srgb=YlpContent.IsColor(t.channel);
                    if(isNew){importer.sRGBTexture=srgb;importer.alphaIsTransparency=t.channel==PaintChannel.Color;importer.SaveAndReimport();created.Add((t.channel,asset));}
                    else if(importer.sRGBTexture!=srgb)notes.Add(Path.GetFileName(t.path)+" is imported as "+(importer.sRGBTexture?"sRGB":"linear")+" but "+t.channel+" is "+(srgb?"colour (sRGB)":"data (linear)")+"; its import settings were left as they are.");
                }
                message="Exported "+targets.Count+" image(s) to "+folder+"."+(notes.Count>0?" "+String.Join(" ",notes):"")+" No material was changed."+NormalExportNote(channels.Contains(PaintChannel.Normal));
            });
        }
        internal void ExportPng()
        {
            string path=Dialogs.SaveFile("Export selected channel PNG",Application.dataPath,channel+".png","png");if(String.IsNullOrEmpty(path))return;
            TryAction(()=>
            {
                var texture=new Texture2D(document.Width,document.Height,TextureFormat.RGBA32,false,true);
                try{texture.LoadRawTextureData(YlpContent.FileImage(document,channel));texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());message="Exported "+channel+" PNG. No material was changed; ICC is not applied."+NormalExportNote(channel==PaintChannel.Normal);}
                finally{DestroyImmediate(texture);}
            });
        }
        void SavePreset(){string p=Dialogs.SaveFile("Save brush",Application.dataPath,"brush","json");if(!String.IsNullOrEmpty(p))TryAction(()=>File.WriteAllText(p,JsonUtility.ToJson(brush,true)));}
        /// <summary>ブラシのファイルを取り込み、プロジェクトのライブラリに入れて 1 つ目を選ぶ。対応していない設定は
        /// 取り込み後に一覧で知らせる（黙って捨てない）。</summary>
        internal void ImportBrushes()
        {
            string path=Dialogs.OpenFile("Import brushes",PainterSettings.BrushImportFolder,BrushImport.Extensions);if(String.IsNullOrEmpty(path))return;
            TryAction(()=>PainterSettings.UpdatePersonal(p=>p.brushImportFolder=Path.GetDirectoryName(path)));
            TryAction(()=>
            {
                IReadOnlyList<Core.Brushes.ImportedBrush> brushes;
                try{brushes=BrushImport.ReadFile(path);}
                catch(Core.Brushes.BrushImportException ex){message="Brush import failed: "+ex.Message;Dialogs.Inform("Brush import failed",Path.GetFileName(path)+"\n\n"+ex.Message);return;}
                // 共有の置き場があるときは、どちらに入れるかを尋ねる（共有に入れたものはバージョン管理で全員に渡る）。
                var library=BrushLibrary.Project.Enabled&&Dialogs.Confirm("Import brushes","Store the imported brushes in the project's shared brush folder (shared through version control) or only for you?\n\nShared: "+BrushLibrary.Project.Folder+"\nOnly you: "+BrushLibrary.Personal.Folder,"Shared with the project","Only for me")?BrushLibrary.Project:BrushLibrary.Personal;
                var added=library.Add(brushes,BrushImport.PrettyName(Path.GetFileName(path)));
                if(added.Count>0)ApplyPreset(added[0]);
                var notes=brushes.SelectMany(b=>b.Warnings.Select(w=>(b.Name,w))).GroupBy(x=>x.w).Select(g=>g.Key+(g.Count()>1?" ("+g.Count()+" brushes)":" ("+g.First().Name+")")).ToList();
                message="Imported "+added.Count+" brush"+(added.Count==1?"":"es")+" from "+Path.GetFileName(path)+(notes.Count>0?"; "+notes.Count+" unsupported setting(s) left out.":".");
                if(notes.Count>0)Dialogs.Inform("Brush import notes",String.Join("\n",notes.Take(30))+(notes.Count>30?"\n… and "+(notes.Count-30)+" more.":"")+"\n\nThe brushes were imported without these settings.");
            });
        }
        internal void DeleteImportedBrush()
        {
            if(!BrushLibrary.IsLibraryPreset(brush.presetId))return;
            string where=BrushLibrary.Owning(brush.presetId)==BrushLibrary.Project?"the project's shared brush folder (this affects everyone after you commit)":"your brush folder";
            if(!Dialogs.Confirm("Delete imported brush","Delete \""+brush.presetName+"\" from "+where+"? The original file is not touched.","Delete","Cancel"))return;
            TryAction(()=>{BrushLibrary.Owning(brush.presetId).Remove(brush.presetId);ApplyPreset(BuiltInBrushes.Presets[0]);message="Deleted the imported brush.";});
        }
        void LoadPreset(){string p=Dialogs.OpenFile("Load brush",Application.dataPath,"json");if(!String.IsNullOrEmpty(p))TryAction(()=>{if(new FileInfo(p).Length>65536)throw new InvalidDataException("Preset too large");var b=ReadBrushState(File.ReadAllText(p));var previous=brush;brush=b;try{GetBrush().Validate();}catch{brush=previous;throw;}message=MissingTipNote()??"Brush preset loaded.";});}
        /// <summary>保存されたブラシの筆先・紙の質感がこの Unity プロジェクトに無いときの知らせ（取り込んだブラシはプロジェクトの
        /// UserSettings にあるので、別のプロジェクトや別の人の環境では見つからない）。見つからない筆先は丸い筆先で描き、ID は残す。</summary>
        string MissingTipNote()
        {
            var missing=new List<string>();
            if(!String.IsNullOrEmpty(brush.tipId)&&BrushTips.ResolveRef(brush.tipId)==null)missing.Add("tip "+brush.tipId);
            if(!String.IsNullOrEmpty(brush.textureId)&&BrushTips.ResolveRef(brush.textureId)==null)missing.Add("texture "+brush.textureId);
            return missing.Count==0?null:"Brush "+String.Join(" and ",missing)+" is not available in this Unity project; painting uses a round tip / no texture instead.";
        }
        void TryAction(Action action)
        {
            try{action();}catch(Exception ex){if(stroke!=null)FinishStroke(false);message=ex.Message;Debug.LogWarning("Texture Painter: "+ex.Message);}
        }
    }
}
