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
        /// schema 2 で筆先・ゆらぎ・紙の質感を足した（schema 1 のファイルも読める。足した項目は既定値になる）。</summary>
        [Serializable] internal sealed class BrushState
        {
            public int schema = 2;
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
        string projectPath, projectToken, recoveryToken, message = "G0/G1 prototype: CPU source brush, GPU compositor; no production validation yet";
        long renderedRevision = -1, savedRevision = -1, recoveredRevision = -1;
        bool repaintPixels = true, surfaceStroke, externalConflict, editMask;
        Vector2 previousPointer, layerScroll, brushScroll, canvasPan;
        float canvasZoom = 1, previousPressure = 1;
        /// <summary>キャンバスでの左ボタンの働き。</summary>
        internal enum PaintTool { Brush, Fill, Gradient, SelectRectangle, SelectEllipse, Lasso, MagicWand, Move }
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
            minSize = new Vector2(980,640);
            compositor = new TileGpuCompositor(); preview = new IsolatedModelPreview();
            if (String.IsNullOrEmpty(recoveryRoot)) recoveryRoot=Path.GetFullPath(Path.Combine("Library","YoluPainter","recovery-"+Guid.NewGuid().ToString("N")));
            try
            {
                if (File.Exists(Path.Combine(recoveryRoot,"current")))
                {
                    var snapshot=GenerationStore.Load(recoveryRoot); document=DocumentBinary.Read(snapshot.Files["document.utpaint"]); recoveryToken=snapshot.Token;
                    message="Recovered native source from the last durable checkpoint. Unsaved edits after that checkpoint may be missing.";
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
        }
        void CreateDocument(int size)
        {
            document=new PaintDocument(size,size,128,PainterSettings.UndoBudgetBytes);
            ApplyBudgets();
            selectedLayer=document.AddLayer("Paint 1").Id; document.ClearHistory(); pristineRevision=document.Revision;
            projectPath=null; projectToken=null; savedRevision=-1; importedOriginal=null; importedPsdPath=null; externalConflict=false; canvasZoom=1; canvasPan=Vector2.zero;
        }
        void OnLostFocus() { FinishStroke(false); CancelToolDrag(); preview?.CancelNavigation(); SaveRecovery(); }
        void BeforeReload() { FinishStroke(false); preview?.CancelNavigation(); SaveRecovery(); }
        void PlayModeChanged(PlayModeStateChange state) { if(state==PlayModeStateChange.ExitingEditMode){ FinishStroke(false); SaveRecovery(); } }
        void OnDisable()
        {
            FinishStroke(false); preview?.CancelNavigation(); SaveRecovery();
            EditorApplication.update-=Tick; PainterSettings.Changed-=SettingsChanged; AssemblyReloadEvents.beforeAssemblyReload-=BeforeReload; EditorApplication.playModeStateChanged-=PlayModeChanged;
            DisposeNormalOutput(); compositor?.Dispose(); preview?.Dispose(); compositor=null; preview=null;
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
            // スライダーのドラッグ中の変更は 1 つの Undo にまとめる。離したところで区切る。
            if(Event.current.rawType==EventType.MouseUp) document.EndCoalescing();
            HandleKeys(Event.current);
            using(new EditorGUI.DisabledScope(stroke!=null)) DrawToolbar();
            if(repaintPixels || renderedRevision!=document.Revision)
            {
                TryAction(()=> { compositor.Update(document,channel); UpdateNormalOutput(); preview.SetPaintTexture(DisplayTexture, materialSlot); });
                lastComposite=EditorApplication.timeSinceStartup;
                renderedRevision=document.Revision; repaintPixels=false;
            }
            Rect main=new Rect(0,48,position.width,position.height-102);
            Rect left=new Rect(0,main.y,220,main.height), right=new Rect(position.width-235,main.y,235,main.height);
            DrawBrush(left); DrawLayers(right);
            float centerWidth=position.width-455;
            canvasRect=new Rect(224,main.y+22,(centerWidth-12)*.5f,main.height-28);
            surfaceRect=new Rect(canvasRect.xMax+8,canvasRect.y,canvasRect.width,canvasRect.height);
            GUI.Label(new Rect(canvasRect.x,main.y,canvasRect.width,20),"2D / "+channel+(EditingMask?" — painting MASK (erase = reveal)":" (bottom-left UV origin)"),EditorStyles.boldLabel);
            GUI.Label(new Rect(surfaceRect.x,main.y,surfaceRect.width,20),"3D / isolated static mesh",EditorStyles.boldLabel);
            DrawCanvas();
            if(Event.current.type==EventType.Repaint) preview.Render(surfaceRect);
            if(!preview.HasModel) GUI.Label(surfaceRect,"Assign a readable static mesh object above\nNo source prefab is instantiated",EditorStyles.centeredGreyMiniLabel);
            HandleCanvasInput(Event.current);
            GUI.Label(new Rect(8,position.height-50,position.width-16,20),$"{document.Width} × {document.Height} | Tiles {document.AllocatedBytes/1048576.0:F2} MiB | History {document.HistoryBytes/1048576.0:F2} MiB | {compositor.Backend}",EditorStyles.miniLabel);
            EditorGUI.HelpBox(new Rect(8,position.height-29,position.width-16,25),message,externalConflict?MessageType.Warning:MessageType.None);
        }
        void DrawToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            resolution=EditorGUILayout.IntPopup(resolution,new[]{"256","512","1024","2048","4096"},new[]{256,512,1024,2048,4096},GUILayout.Width(65));
            if(GUILayout.Button("New",EditorStyles.toolbarButton,GUILayout.Width(38)) && ConfirmDiscard()) { CreateDocument(resolution); BindDocument(); }
            if(GUILayout.Button("Open",EditorStyles.toolbarButton,GUILayout.Width(42))) OpenProject();
            if(GUILayout.Button("Save",EditorStyles.toolbarButton,GUILayout.Width(42))) SaveProject(false);
            if(GUILayout.Button("Save As",EditorStyles.toolbarButton,GUILayout.Width(55))) SaveProject(true);
            if(GUILayout.Button("Import PSD",EditorStyles.toolbarButton,GUILayout.Width(78))) ImportPsd();
            if(GUILayout.Button(new GUIContent("Export Images","Write every channel in use as PNG into a folder"),EditorStyles.toolbarButton,GUILayout.Width(90))) ExportImages();
            if(GUILayout.Button(new GUIContent("lilToon…","Write lilToon-ready textures and assign them to this slot's lilToon material (asks first)"),EditorStyles.toolbarButton,GUILayout.Width(62))) TryAction(AssignToLilToon);
            if(GUILayout.Button(new GUIContent("Export PNG","Write the selected channel as one PNG"),EditorStyles.toolbarButton,GUILayout.Width(78))) ExportPng();
            if(GUILayout.Button("Export PSD",EditorStyles.toolbarButton,GUILayout.Width(78))) ExportPsd();
            GUILayout.Space(8);
            using(new EditorGUI.DisabledScope(!document.CanUndo)) if(GUILayout.Button("Undo",EditorStyles.toolbarButton,GUILayout.Width(45))) document.Undo();
            using(new EditorGUI.DisabledScope(!document.CanRedo)) if(GUILayout.Button("Redo",EditorStyles.toolbarButton,GUILayout.Width(45))) document.Redo();
            if(GUILayout.Button("Demo cube",EditorStyles.toolbarButton,GUILayout.Width(75)))TryAction(()=>{model=null;preview.LoadDemoMesh();materialSlot=0;repaintPixels=true;message="Loaded tool-owned seam-test cube. No scene or source assets changed.";});
            GUILayout.FlexibleSpace(); GUILayout.Label(document.Revision==savedRevision?"Saved":"Unsaved",EditorStyles.miniLabel);
            if(GUILayout.Button(new GUIContent("Settings","Project Settings > YoluPainter (shared with the project / only for you)"),EditorStyles.toolbarButton,GUILayout.Width(60))) OpenSettings();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            var next=(GameObject)EditorGUILayout.ObjectField("Preview model",model,typeof(GameObject),true,GUILayout.MinWidth(260));
            if(next!=model){ model=next; TryAction(()=>{preview.Load(model); materialSlot=Mathf.Clamp(materialSlot,0,Mathf.Max(0,preview.MaterialSlotCount-1)); message=String.Join("; ",preview.Diagnostics); repaintPixels=true;}); }
            int slot=EditorGUILayout.IntField("Material slot",materialSlot,GUILayout.Width(210));
            if(slot!=materialSlot){materialSlot=Mathf.Clamp(slot,0,Mathf.Max(0,preview.MaterialSlotCount-1)); repaintPixels=true;}
            var nextChannel=(PaintChannel)EditorGUILayout.EnumPopup(channel,GUILayout.Width(105));
            if(nextChannel!=channel){channel=nextChannel; repaintPixels=true; message=channel==PaintChannel.Normal?"Normal channel: layers composite as unit normals (Overlay adds detail, other modes replace). Height → Normal is in the left panel.":"Painting only the selected channel; other channels remain unchanged.";}
            GUILayout.EndHorizontal();
        }
        void DrawBrush(Rect rect)
        {
            GUILayout.BeginArea(rect,EditorStyles.helpBox); brushScroll=GUILayout.BeginScrollView(brushScroll);
            GUILayout.Label("Tool",EditorStyles.boldLabel);
            using(new EditorGUI.DisabledScope(stroke!=null))
            {
                var tools=new[]{new GUIContent("Brush"),new GUIContent("Fill","Bucket fill (B)"),new GUIContent("Grad","Gradient"),new GUIContent("Rect","Rectangle selection"),new GUIContent("Ellipse","Ellipse selection"),new GUIContent("Lasso","Lasso selection"),new GUIContent("Wand","Magic wand"),new GUIContent("Move","Move the layer, or the selected pixels (drag, arrow keys). Rotate, scale and flip below.")};
                var nextTool=(PaintTool)GUILayout.SelectionGrid((int)tool,tools,4,EditorStyles.miniButton);
                if(nextTool!=tool)Tool=nextTool;
                if(tool==PaintTool.Fill||tool==PaintTool.MagicWand)
                {
                    wandTolerance=EditorGUILayout.IntSlider("Tolerance",wandTolerance,0,255);
                    wandContiguous=EditorGUILayout.Toggle("Contiguous",wandContiguous);
                    wandSampleAll=EditorGUILayout.Toggle(new GUIContent("Sample all layers","Use the composite instead of the selected layer"),wandSampleAll);
                }
                if(tool==PaintTool.Gradient)
                {
                    gradientShape=(GradientShape)EditorGUILayout.EnumPopup("Shape",gradientShape);
                    gradientTo=EditorGUILayout.ColorField(new GUIContent("To","The colour at the end (the start is the brush value)"),gradientTo);
                }
                if(tool==PaintTool.Move) DrawMoveSettings();
                if(tool==PaintTool.Fill||(tool>=PaintTool.SelectRectangle&&tool<=PaintTool.MagicWand)) DrawSurfacePick();
                if(tool>=PaintTool.SelectRectangle&&tool<=PaintTool.MagicWand) EditorGUILayout.LabelField("Shift: add · Ctrl: subtract · Shift+Ctrl: intersect",EditorStyles.miniLabel);
                GUILayout.BeginHorizontal();
                if(GUILayout.Button(new GUIContent("All","Select all (Ctrl+A)"),EditorStyles.miniButtonLeft))document.SetSelection(SelectionMask.All(document));
                using(new EditorGUI.DisabledScope(document.Selection==null))
                {
                    if(GUILayout.Button(new GUIContent("None","Deselect (Ctrl+D)"),EditorStyles.miniButtonMid))document.ClearSelection();
                    if(GUILayout.Button(new GUIContent("Invert","Invert the selection (Ctrl+Shift+I)"),EditorStyles.miniButtonRight))document.SetSelection(document.Selection.Invert());
                }
                GUILayout.EndHorizontal();
                DrawSelectionModify();
            }
            GUILayout.Space(6);
            GUILayout.Label("Brush",EditorStyles.boldLabel);
            using(new EditorGUI.DisabledScope(stroke!=null))
            {
                GUILayout.BeginHorizontal();
                var thumb=BrushTips.Thumbnail(brush.tipId);
                GUILayout.Label(thumb!=null?new GUIContent(thumb):new GUIContent("●"),GUILayout.Width(40),GUILayout.Height(40));
                if(GUILayout.Button(brush.presetName,EditorStyles.popup,GUILayout.Height(20)))
                {
                    var menu=new GenericMenu();
                    foreach(var preset in BuiltInBrushes.Presets){var p=preset;menu.AddItem(new GUIContent(p.Category+"/"+p.Name),brush.presetId==p.Id,()=>ApplyPreset(p));}
                    if(PainterSettings.ShowBundledBrushes)
                    {
                        foreach(var preset in BundledBrushSets.Presets){var p=preset;menu.AddItem(new GUIContent(p.Category+"/"+p.Name),brush.presetId==p.Id,()=>ApplyPreset(p));}
                        if(BundledBrushSets.LoadWarnings.Count>0)menu.AddDisabledItem(new GUIContent("Some bundled brushes could not be loaded (see Console)"));
                    }
                    foreach(var library in BrushLibrary.All)
                        foreach(var preset in library.Presets){var p=preset;menu.AddItem(new GUIContent(library.MenuName+"/"+(String.IsNullOrEmpty(p.Category)?"":p.Category+"/")+p.Name),brush.presetId==p.Id,()=>ApplyPreset(p));}
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Brush settings…"),false,OpenSettings);
                    menu.ShowAsContext();
                }
                GUILayout.EndHorizontal();
                if(channel==PaintChannel.Roughness||channel==PaintChannel.Metallic||channel==PaintChannel.Height)
                {float scalar=EditorGUILayout.Slider("Scalar value",brush.color.r,0,1);brush.color=new Color(scalar,scalar,scalar,brush.color.a);}
                else brush.color=EditorGUILayout.ColorField("Value",brush.color);
                brush.radius=EditorGUILayout.Slider("Radius px",brush.radius,.5f,128);
                brush.hardness=EditorGUILayout.Slider("Hardness",brush.hardness,0,1);
                brush.spacing=EditorGUILayout.Slider("Spacing",brush.spacing,.01f,1);
                brush.opacity=EditorGUILayout.Slider("Opacity",brush.opacity,0,1);
                brush.flow=EditorGUILayout.Slider("Flow",brush.flow,0,1);
                brush.erase=EditorGUILayout.Toggle("Erase",brush.erase);
                brush.pressureSize=EditorGUILayout.Toggle("Pressure size",brush.pressureSize);
                brush.pressureOpacity=EditorGUILayout.Toggle("Pressure opacity",brush.pressureOpacity);
                brush.pressureFlow=EditorGUILayout.Toggle("Pressure flow",brush.pressureFlow);
                brush.pressureCurve=EditorGUILayout.CurveField("Pressure curve",brush.pressureCurve);
                DrawStrokeAssist();
                showDynamics=EditorGUILayout.Foldout(showDynamics,"Tip & dynamics",true);
                if(showDynamics)
                {
                    EditorGUILayout.LabelField("Tip",string.IsNullOrEmpty(brush.tipId)?"Round (hardness)":BrushTips.ResolveRef(brush.tipId)==null?"Missing: "+brush.tipId+" (round tip used)":brush.tipId);
                    brush.angle=EditorGUILayout.Slider("Angle",brush.angle,-180,180);
                    brush.roundness=EditorGUILayout.Slider("Roundness",brush.roundness,.01f,1);
                    brush.followDirection=EditorGUILayout.Toggle("Follow direction",brush.followDirection);
                    brush.sizeJitter=EditorGUILayout.Slider("Size jitter",brush.sizeJitter,0,1);
                    brush.angleJitter=EditorGUILayout.Slider("Angle jitter",brush.angleJitter,0,1);
                    brush.roundnessJitter=EditorGUILayout.Slider("Roundness jitter",brush.roundnessJitter,0,1);
                    brush.opacityJitter=EditorGUILayout.Slider("Opacity jitter",brush.opacityJitter,0,1);
                    brush.flowJitter=EditorGUILayout.Slider("Flow jitter",brush.flowJitter,0,1);
                    brush.scatter=EditorGUILayout.Slider("Scatter",brush.scatter,0,10);
                    brush.count=EditorGUILayout.IntSlider("Count",brush.count,1,16);
                    EditorGUILayout.LabelField("Texture",string.IsNullOrEmpty(brush.textureId)?"None":BrushTips.ResolveRef(brush.textureId)==null?"Missing: "+brush.textureId+" (not used)":brush.textureId);
                    if(!string.IsNullOrEmpty(brush.textureId))
                    {
                        brush.textureDepth=EditorGUILayout.Slider("Texture depth",brush.textureDepth,0,1);
                        brush.textureScale=EditorGUILayout.Slider("Texture scale",brush.textureScale,.05f,16);
                    }
                }
                if(GUILayout.Button("Save brush preset")) SavePreset();
                if(GUILayout.Button("Load brush preset")) LoadPreset();
                if(GUILayout.Button(new GUIContent("Import brushes…","Photoshop .abr, GIMP .gbr / .gih / .vbr, or a PNG tip (dark = paint). Stored in this project's UserSettings."))) ImportBrushes();
                if(BrushLibrary.IsLibraryPreset(brush.presetId)&&GUILayout.Button("Delete imported brush")) DeleteImportedBrush();
            }
            DrawNormalPanel();
            GUILayout.Space(12);
            GUILayout.Label("Input",EditorStyles.boldLabel);
            GUILayout.Label("LMB: use the tool (brush also on 3D)\nAlt / RMB: orbit 3D\nMMB: pan 2D / 3D\nWheel: zoom\nEsc: cancel stroke or drag\nCtrl/Cmd Z: undo · Shift: redo\nCtrl/Cmd A: select all · D: deselect\nCtrl/Cmd Shift I: invert selection\nFocus loss: cancel active stroke",EditorStyles.wordWrappedMiniLabel);
            GUILayout.Space(12);
            EditorGUILayout.HelpBox("Prototype uses one IMGUI pressure path. Tablet response, HiDPI and latency still require Unity/device tests. No-pressure input uses Unity's fixed fallback.",MessageType.Info);
            if(GUILayout.Button("Show implementation limits")) Dialogs.Inform("G0/G1 limits","CPU source brush; bounded GPU tile compositor. Readable static and skinned meshes (skinned ones posed on a copy; Humanoid clips not yet). Selected channel only. .ylp save, PSD with RGB8 layers, groups, masks and three adjustment types. Editable surface paths, Generators/Filters/Anchors and an exact lilToon look in the preview remain unfinished. See Documentation~/STATUS.md in the package.");
            DrawPosePanel();
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }
        void DrawLayers(Rect rect)
        {
            GUILayout.BeginArea(rect,EditorStyles.helpBox);
            GUILayout.Label("Layers (top first)",EditorStyles.boldLabel);
            using(new EditorGUI.DisabledScope(stroke!=null))
            {
                GUILayout.BeginHorizontal();
                // 新しいレイヤーは選択中のレイヤーのすぐ上（同じグループの中）に作る
                Guid? above=document.Layers.Any(l=>l.Id==selectedLayer)?selectedLayer:(Guid?)null;
                if(GUILayout.Button("+")) selectedLayer=document.AddLayer("Paint "+(document.Layers.Count+1),above:above).Id;
                if(GUILayout.Button("+ Fill")) selectedLayer=document.AddFillLayer("Fill "+(document.Layers.Count+1),new Dictionary<PaintChannel,Rgba32>{{channel,GetBrush().Color}},above:above).Id;
                if(GUILayout.Button("+ Adjust"))
                {
                    var menu=new GenericMenu();
                    menu.AddItem(new GUIContent("Invert"),false,()=>selectedLayer=document.AddAdjustmentLayer("Invert",AdjustmentSettings.Invert(),above:above).Id);
                    menu.AddItem(new GUIContent("Levels"),false,()=>selectedLayer=document.AddAdjustmentLayer("Levels",AdjustmentSettings.Levels(),above:above).Id);
                    menu.AddItem(new GUIContent("Hue / Saturation"),false,()=>selectedLayer=document.AddAdjustmentLayer("Hue / Saturation",AdjustmentSettings.HueSaturation(),above:above).Id);
                    menu.ShowAsContext();
                }
                if(GUILayout.Button(new GUIContent("+ Group","Put the selected layer into a new group"))) TryAction(()=>selectedLayer=document.GroupLayers(new[]{selectedLayer},"Group "+(document.Layers.Count(l=>l.IsGroup)+1)).Id);
                using(new EditorGUI.DisabledScope(document.Layers.Count<2)) if(GUILayout.Button(new GUIContent("−","Delete the selected layer (a group is deleted with its contents)"))){document.RemoveLayer(selectedLayer);selectedLayer=document.Layers.Count>0?document.Layers[document.Layers.Count-1].Id:Guid.Empty;}
                GUILayout.EndHorizontal();
                layerScroll=GUILayout.BeginScrollView(layerScroll);
                for(int i=document.Layers.Count-1;i>=0;i--)
                {
                    var layer=document.Layers[i]; GUILayout.BeginHorizontal();
                    GUILayout.Space(12*document.DepthOf(layer.Id));
                    bool visible=GUILayout.Toggle(layer.Visible,"",GUILayout.Width(18)); if(visible!=layer.Visible) document.SetLayerVisibility(layer.Id,visible);
                    string kind=layer.IsGroup?"▾ ":layer.Kind==LayerKind.Fill?"[Fill] ":layer.Kind==LayerKind.Adjustment?"[Adj] ":"";
                    if(GUILayout.Toggle(selectedLayer==layer.Id,(document.IsEffectivelyClipped(i)?"↳ ":"")+kind+layer.Name,"Button")) selectedLayer=layer.Id;
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
                var active=document.Layers.FirstOrDefault(l=>l.Id==selectedLayer);
                if(active!=null)
                {
                    string name=EditorGUILayout.DelayedTextField("Name",active.Name);if(name!=active.Name)document.SetLayerName(active.Id,name);
                    float opacity=EditorGUILayout.Slider("Opacity",(float)active.Opacity,0,1);if(Math.Abs(opacity-active.Opacity)>.00001)document.SetLayerOpacity(active.Id,opacity,coalesce:true);
                    var blend=(LayerBlendMode)EditorGUILayout.EnumPopup(new GUIContent("Blend"),active.BlendMode,e=>active.IsGroup||(LayerBlendMode)e!=LayerBlendMode.PassThrough,false);if(blend!=active.BlendMode)TryAction(()=>document.SetLayerBlendMode(active.Id,blend));
                    bool clipping=EditorGUILayout.Toggle("Clip to layer below",active.Clipping);if(clipping!=active.Clipping)document.SetLayerClipping(active.Id,clipping);
                    if(!active.IsGroup){bool enabled=EditorGUILayout.Toggle("Channel enabled",active.IsChannelEnabled(channel));if(enabled!=active.IsChannelEnabled(channel))TryAction(()=>document.SetChannelEnabled(active.Id,channel,enabled));}
                    var siblings=document.ChildrenOf(active.ParentId);int position=siblings.ToList().IndexOf(active);
                    GUILayout.BeginHorizontal();
                    using(new EditorGUI.DisabledScope(position>=siblings.Count-1)) if(GUILayout.Button("Up"))document.MoveLayer(active.Id,position+1);
                    using(new EditorGUI.DisabledScope(position<=0)) if(GUILayout.Button("Down"))document.MoveLayer(active.Id,position-1);
                    var groupBelow=position>0&&siblings[position-1].IsGroup?siblings[position-1]:null;
                    using(new EditorGUI.DisabledScope(groupBelow==null)) if(GUILayout.Button(new GUIContent("Into ▾","Move into the group below (on top of its contents)")))document.MoveLayerTo(active.Id,groupBelow.Id,document.ChildrenOf(groupBelow.Id).Count);
                    using(new EditorGUI.DisabledScope(active.ParentId==Guid.Empty)) if(GUILayout.Button(new GUIContent("Out","Move out of the group, just above it")))
                    {
                        var parent=document.GetLayer(active.ParentId);var outer=document.ChildrenOf(parent.ParentId).ToList();
                        document.MoveLayerTo(active.Id,parent.ParentId,outer.IndexOf(parent)+1);
                    }
                    GUILayout.EndHorizontal();
                    if(active.IsGroup)
                    {
                        EditorGUILayout.HelpBox(active.BlendMode==LayerBlendMode.PassThrough?"Pass through: the contents blend with the layers below as if they were not grouped.":"Isolated: the contents are composited together first, then blended with "+active.BlendMode+".",MessageType.None);
                        if(GUILayout.Button("Ungroup (keep contents)"))
                        {
                            var first=document.ChildrenOf(active.Id).LastOrDefault();
                            document.Ungroup(active.Id); selectedLayer=first!=null?first.Id:(document.Layers.Count>0?document.Layers[document.Layers.Count-1].Id:Guid.Empty);
                            GUIUtility.ExitGUI(); // 消えたグループのまま下の欄を描かない
                        }
                    }
                    if(active.Kind==LayerKind.Fill) DrawFill(active);
                    if(active.Kind==LayerKind.Adjustment) DrawAdjustment(active);
                    DrawMask(active);
                }
            }
            GUILayout.EndArea();
        }
        void DrawFill(PaintLayer active)
        {
            GUILayout.Space(6);
            GUILayout.Label("Fill value ("+channel+")",EditorStyles.miniBoldLabel);
            if(active.FillValues.TryGetValue(channel,out var value))
            {
                Rgba32 next;
                if(channel==PaintChannel.Roughness||channel==PaintChannel.Metallic||channel==PaintChannel.Height)
                {byte v=(byte)Mathf.RoundToInt(EditorGUILayout.Slider("Value",value.R/255f,0,1)*255);next=new Rgba32(v,v,v,value.A);}
                else{var c=EditorGUILayout.ColorField("Value",new Color32(value.R,value.G,value.B,value.A));var c32=(Color32)c;next=new Rgba32(c32.r,c32.g,c32.b,c32.a);}
                if(next!=value) document.SetFillValue(active.Id,channel,next,coalesce:true);
                if(GUILayout.Button("Remove value for "+channel)) document.SetFillValue(active.Id,channel,null);
            }
            else if(GUILayout.Button("Add value for "+channel)) document.SetFillValue(active.Id,channel,GetBrush().Color);
            EditorGUILayout.HelpBox("A fill covers the whole canvas. Paint its mask to choose where it shows.",MessageType.None);
        }
        void DrawAdjustment(PaintLayer active)
        {
            GUILayout.Space(6);
            var a=active.Adjustment;
            GUILayout.Label("Adjustment: "+a.Type+" (applies to the layers below)",EditorStyles.miniBoldLabel);
            AdjustmentSettings next=a;
            switch(a.Type)
            {
                case AdjustmentType.Levels:
                {
                    float ib=EditorGUILayout.Slider("Input black",(float)a.InputBlack,0,(float)a.InputWhite-.004f);
                    float iw=EditorGUILayout.Slider("Input white",(float)a.InputWhite,ib+.004f,1);
                    float gamma=EditorGUILayout.Slider("Gamma",(float)a.Gamma,.1f,9.99f);
                    float ob=EditorGUILayout.Slider("Output black",(float)a.OutputBlack,0,1);
                    float ow=EditorGUILayout.Slider("Output white",(float)a.OutputWhite,0,1);
                    next=AdjustmentSettings.Levels(ib,iw,gamma,ob,ow); break;
                }
                case AdjustmentType.HueSaturation:
                {
                    float hue=EditorGUILayout.Slider("Hue",(float)a.Hue,-180,180);
                    float sat=EditorGUILayout.Slider("Saturation",(float)a.Saturation,-1,1);
                    float light=EditorGUILayout.Slider("Lightness",(float)a.Lightness,-1,1);
                    next=AdjustmentSettings.HueSaturation(hue,sat,light); break;
                }
                default: EditorGUILayout.LabelField("Inverts the colour of everything below."); break;
            }
            if(!next.Equals(a)) TryAction(()=>document.SetAdjustment(active.Id,next,coalesce:true));
            if(!a.AppliesTo(channel)) EditorGUILayout.HelpBox(a.Type+" does not apply to the "+channel+" channel.",MessageType.None);
        }
        bool EditingMask => editMask && document.Layers.Any(l => l.Id == selectedLayer && l.Mask != null);
        void DrawMask(PaintLayer active)
        {
            GUILayout.Space(6);
            GUILayout.Label("Mask (shared by all channels)",EditorStyles.miniBoldLabel);
            var mask=active.Mask;
            if(mask==null)
            {
                if(GUILayout.Button("Add mask")){document.AddLayerMask(active.Id);editMask=true;}
                return;
            }
            editMask=GUILayout.Toggle(editMask,"Paint on mask (paint hides, erase reveals)");
            bool maskEnabled=EditorGUILayout.Toggle("Mask enabled",mask.Enabled);if(maskEnabled!=mask.Enabled)document.SetLayerMaskEnabled(active.Id,maskEnabled);
            bool inverted=EditorGUILayout.Toggle("Invert mask",mask.Inverted);if(inverted!=mask.Inverted)document.SetLayerMaskInverted(active.Id,inverted);
            float density=EditorGUILayout.Slider("Mask density",(float)mask.Density,0,1);if(Math.Abs(density-mask.Density)>.00001)document.SetLayerMaskDensity(active.Id,density,coalesce:true);
            if(GUILayout.Button("Remove mask")){document.RemoveLayerMask(active.Id);editMask=false;}
        }
        Rect ImageRect()
        {
            float fit=Mathf.Min(canvasRect.width/document.Width,canvasRect.height/document.Height)*canvasZoom;
            float width=document.Width*fit,height=document.Height*fit;
            return new Rect(canvasRect.center.x-width*.5f+canvasPan.x,canvasRect.center.y-height*.5f+canvasPan.y,width,height);
        }
        void DrawCanvas()
        {
            EditorGUI.DrawRect(canvasRect,new Color(.16f,.16f,.16f));
            GUI.BeginClip(canvasRect);
            var image=ImageRect(); image.position-=canvasRect.position;
            if(DisplayTexture!=null) EditorGUI.DrawTextureTransparent(image,DisplayTexture,ScaleMode.StretchToFill);
            if(document.Selection!=null){EnsureSelectionOverlay(); GUI.DrawTexture(image,selectionOverlay,ScaleMode.StretchToFill,true);}
            if(toolDragging&&Event.current.type==EventType.Repaint) DrawToolPreview(image);
            else if(tool==PaintTool.Move&&!toolDragging&&Event.current.type==EventType.Repaint) DrawTransformHandles(image);
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
            if(tool==PaintTool.Brush)return false;
            if(e.type==EventType.MouseDown&&e.button==0&&!e.alt&&canvasRect.Contains(e.mousePosition))
            {
                var p=CanvasPoint(e.mousePosition);
                switch(tool)
                {
                    case PaintTool.Fill: TryAction(()=>BucketFill(p)); break;
                    case PaintTool.MagicWand: TryAction(()=>ApplySelection(Wand(p),CombineOf(e))); break;
                    case PaintTool.Move: TryAction(()=>BeginMove(p,e.mousePosition)); break;
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
        void DrawSelectionModify()
        {
            using(new EditorGUI.DisabledScope(document.Selection==null))
            {
                GUILayout.BeginHorizontal();
                selectionRadius=Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Modify (px)","Radius for Grow, Shrink, Border and Feather (as GIMP's Select menu)"),selectionRadius),0,SelectionMask.MaxModifyRadius);
                selectionEdgeLock=GUILayout.Toggle(selectionEdgeLock,new GUIContent("Edge lock","Selected areas continue outside the canvas (Shrink, Border and Feather do not pull away from the canvas edge)"),EditorStyles.miniButton,GUILayout.Width(70));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if(GUILayout.Button(new GUIContent("Grow","Largest amount within a circle of the radius"),EditorStyles.miniButtonLeft))TryAction(()=>ModifySelection(SelectionModifyKind.Grow));
                if(GUILayout.Button(new GUIContent("Shrink","Smallest amount within a circle of the radius"),EditorStyles.miniButtonMid))TryAction(()=>ModifySelection(SelectionModifyKind.Shrink));
                if(GUILayout.Button(new GUIContent("Border","A band around the edge: Grow minus Shrink"),EditorStyles.miniButtonMid))TryAction(()=>ModifySelection(SelectionModifyKind.Border));
                if(GUILayout.Button(new GUIContent("Feather","Soften the edge (Gaussian blur, σ = radius / 3.5)"),EditorStyles.miniButtonMid))TryAction(()=>ModifySelection(SelectionModifyKind.Feather));
                if(GUILayout.Button(new GUIContent("Sharpen","Hard edge: at least half selected becomes fully selected"),EditorStyles.miniButtonRight))TryAction(()=>ModifySelection(SelectionModifyKind.Sharpen));
                GUILayout.EndHorizontal();
            }
        }
        void DrawMoveSettings()
        {
            EditorGUILayout.LabelField("Drag or arrow keys (Shift: 10 px) move the layer and its mask, or the selected pixels with the selection.",EditorStyles.wordWrappedMiniLabel);
            GUILayout.BeginHorizontal();
            if(GUILayout.Button(new GUIContent("Flip H","Mirror left-right about the centre"),EditorStyles.miniButtonLeft))TryAction(()=>TransformSelected(0,0,0,-1,1,"Flipped horizontally."));
            if(GUILayout.Button(new GUIContent("Flip V","Mirror top-bottom about the centre"),EditorStyles.miniButtonMid))TryAction(()=>TransformSelected(0,0,0,1,-1,"Flipped vertically."));
            if(GUILayout.Button(new GUIContent("+90°","Rotate 90° counter-clockwise"),EditorStyles.miniButtonMid))TryAction(()=>TransformSelected(0,0,90,1,1,"Rotated 90° counter-clockwise."));
            if(GUILayout.Button(new GUIContent("-90°","Rotate 90° clockwise"),EditorStyles.miniButtonRight))TryAction(()=>TransformSelected(0,0,-90,1,1,"Rotated 90° clockwise."));
            GUILayout.EndHorizontal();
            moveAngle=EditorGUILayout.FloatField(new GUIContent("Rotate (°)","Counter-clockwise, about the centre of what moves"),moveAngle);
            moveScale=EditorGUILayout.Vector2Field(new GUIContent("Scale (%)","Negative flips"),moveScale);
            moveOffset=EditorGUILayout.Vector2Field("Offset (px)",moveOffset);
            moveResampling=(Resampling)EditorGUILayout.EnumPopup(new GUIContent("Resampling","Bilinear smooths, Nearest keeps hard pixels. Whole-pixel moves, 90° turns and flips copy pixels exactly either way."),moveResampling);
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("Apply"))TryAction(ApplyNumericTransform);
            if(GUILayout.Button("Reset",GUILayout.Width(60))){moveAngle=0;moveScale=new Vector2(100,100);moveOffset=Vector2.zero;}
            GUILayout.EndHorizontal();
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
            if(b==null||b.schema<1||b.schema>2||b.pressureCurve==null)throw new InvalidDataException("Unsupported brush settings");
            if(b.schema<2){b.roundness=1;b.textureScale=1;b.count=1;b.randomSeedPerStroke=true;b.schema=2;}
            return b;
        }
        internal BrushSettings GetBrush() { var s = NewBrushSettings(); BrushTips.Apply(s, brush.tipId); return s; }
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
            brush=new BrushState{ presetId=preset.Id, presetName=preset.Name, radius=(float)s.Radius, hardness=(float)s.Hardness, spacing=(float)s.Spacing, opacity=(float)s.Opacity, flow=(float)s.Flow,
                color=color, pressureCurve=curve, pressureSize=s.PressureSize, pressureOpacity=s.PressureOpacity, pressureFlow=s.PressureFlow, erase=s.Erase,
                tipId=BrushTips.IdOf(s), textureId=BrushTips.IdOf(s.Texture), angle=(float)s.Angle, roundness=(float)s.Roundness, followDirection=s.FollowDirection,
                sizeJitter=(float)s.SizeJitter, angleJitter=(float)s.AngleJitter, roundnessJitter=(float)s.RoundnessJitter, opacityJitter=(float)s.OpacityJitter, flowJitter=(float)s.FlowJitter,
                scatter=(float)s.Scatter, count=s.Count, textureDepth=(float)s.TextureDepth, textureScale=(float)s.TextureScale,
                stabilizer=assist.Item1, taperIn=assist.Item2, taperOut=assist.Item3 }; // 補正と入り抜きは描き手の設定として残す
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
                stroke.Add(new BrushSample((pointer.x-image.x)/image.width*document.Width,(1-(pointer.y-image.y)/image.height)*document.Height,pressure,EditorApplication.timeSinceStartup));
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
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.A){document.SetSelection(SelectionMask.All(document));e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.D){document.ClearSelection();e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.shift && e.keyCode==KeyCode.I){if(document.Selection!=null)document.SetSelection(document.Selection.Invert());e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.Z){if(e.shift)document.Redo();else document.Undo();e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.S){SaveProject(e.shift);e.Use();}
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
                var snapshot=GenerationStore.Commit(recoveryRoot,files,recoveryToken); recoveryToken=snapshot.Token;
                recoveredRevision=document.Revision;lastRecovery=EditorApplication.timeSinceStartup;return true;
            }
            catch(Exception ex){message="Recovery checkpoint failed: "+ex.Message;return false;}
        }
        /// <summary>.ylp に保存する。上書きは開いた/保存した時点から外で変わっていないときだけで、直前の版は
        /// &lt;名前&gt;.ylp-backups~ に退避する（保持数は設定）。Assets の中のファイルなら保存後に取り込み直してテクスチャを更新する。</summary>
        internal void SaveProject(bool saveAs)
        {
            if(stroke!=null)return;
            string target=projectPath;
            if(saveAs||String.IsNullOrEmpty(target))
            {
                string suggestFolder=projectPath!=null?Path.GetDirectoryName(projectPath):importedPsdPath!=null?Path.GetDirectoryName(importedPsdPath):Application.dataPath;
                string suggestName=projectPath!=null?Path.GetFileNameWithoutExtension(projectPath):importedPsdPath!=null?Path.GetFileNameWithoutExtension(importedPsdPath):"Texture";
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
                var saved=YlpStore.Save(target,files,sameFile?projectToken:null,!sameFile,keep);
                projectPath=saved.Path;projectToken=saved.Token;savedRevision=document.Revision;externalConflict=false;
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
                document=next;if(document.Layers.Count==0)document.AddLayer("Paint 1");BindDocument();selectedLayer=document.Layers.Last().Id;projectPath=null;projectToken=null;savedRevision=-1;
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
