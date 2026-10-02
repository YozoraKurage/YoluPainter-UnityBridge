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
    public sealed class TexturePaintWindow : EditorWindow
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
        string projectRoot, projectToken, recoveryToken, message = "G0/G1 prototype: CPU source brush, GPU compositor; no production validation yet";
        long renderedRevision = -1, savedRevision = -1, recoveredRevision = -1;
        bool repaintPixels = true, surfaceStroke, externalConflict, editMask;
        Vector2 previousPointer, layerScroll, brushScroll, canvasPan;
        string brushImportFolder = "";
        float canvasZoom = 1, previousPressure = 1;
        int materialSlot, resolution = 1024;
        bool showDynamics;
        double lastRecovery, lastExternalCheck;
        byte[] importedOriginal;
        Rect canvasRect, surfaceRect;

        [MenuItem("YozoLab/YoluPainter (Prototype)")]
        public static void Open() => GetWindow<TexturePaintWindow>("Texture Painter");

        // EditMode テスト用の参照口。入力は SendEvent で本物の経路を通す。
        internal PaintDocument Document => document;
        internal bool IsStroking => stroke != null;
        internal IsolatedModelPreview Preview => preview;
        internal Rect SurfaceRect => surfaceRect;
        internal string StatusMessage => message;
        internal string RecoveryRoot => recoveryRoot;
        internal string ProjectRoot => projectRoot;
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
            if (document==null) CreateDocument(resolution);
            selectedLayer=document.Layers.Count>0?document.Layers[document.Layers.Count-1].Id:Guid.Empty;
            BindDocument();
            if (model!=null) TryAction(()=>preview.Load(model));
            EditorApplication.update+=Tick;
            AssemblyReloadEvents.beforeAssemblyReload+=BeforeReload;
            EditorApplication.playModeStateChanged+=PlayModeChanged;
        }
        void BindDocument()
        {
            document.HistoryTrimming += bytes => message="Undo budget reached; dropping "+(bytes/1024)+" KiB of oldest history. Current source remains intact.";
            repaintPixels=true; renderedRevision=-1; recoveredRevision=-1;
        }
        void CreateDocument(int size)
        {
            document=new PaintDocument(size,size,128,64L*1024*1024);
            selectedLayer=document.AddLayer("Paint 1").Id; document.ClearHistory();
            projectRoot=null; projectToken=null; savedRevision=-1; importedOriginal=null; externalConflict=false; canvasZoom=1; canvasPan=Vector2.zero;
        }
        void OnLostFocus() { FinishStroke(false); preview?.CancelNavigation(); SaveRecovery(); }
        void BeforeReload() { FinishStroke(false); preview?.CancelNavigation(); SaveRecovery(); }
        void PlayModeChanged(PlayModeStateChange state) { if(state==PlayModeStateChange.ExitingEditMode){ FinishStroke(false); SaveRecovery(); } }
        void OnDisable()
        {
            FinishStroke(false); preview?.CancelNavigation(); SaveRecovery();
            EditorApplication.update-=Tick; AssemblyReloadEvents.beforeAssemblyReload-=BeforeReload; EditorApplication.playModeStateChanged-=PlayModeChanged;
            compositor?.Dispose(); preview?.Dispose(); compositor=null; preview=null;
        }
        void Tick()
        {
            if(document==null) return;
            if(stroke==null && document.Revision!=recoveredRevision && EditorApplication.timeSinceStartup-lastRecovery>15) SaveRecovery();
            if(!String.IsNullOrEmpty(projectRoot) && EditorApplication.timeSinceStartup-lastExternalCheck>3) CheckExternalChange();
        }
        internal void CheckExternalChange()
        {
            if(String.IsNullOrEmpty(projectRoot)) return;
            lastExternalCheck=EditorApplication.timeSinceStartup;
            externalConflict=GenerationStore.HasExternalChange(projectRoot,projectToken);
            if(externalConflict) message="Saved files changed externally. Normal save is blocked; use Save As or explicitly reopen after reviewing local edits.";
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
                TryAction(()=> { compositor.Update(document,channel); preview.SetPaintTexture(compositor.Texture, materialSlot); });
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
            if(GUILayout.Button("Export PNG",EditorStyles.toolbarButton,GUILayout.Width(78))) ExportPng();
            GUILayout.Space(8);
            using(new EditorGUI.DisabledScope(!document.CanUndo)) if(GUILayout.Button("Undo",EditorStyles.toolbarButton,GUILayout.Width(45))) document.Undo();
            using(new EditorGUI.DisabledScope(!document.CanRedo)) if(GUILayout.Button("Redo",EditorStyles.toolbarButton,GUILayout.Width(45))) document.Redo();
            if(GUILayout.Button("Demo cube",EditorStyles.toolbarButton,GUILayout.Width(75)))TryAction(()=>{model=null;preview.LoadDemoMesh();materialSlot=0;repaintPixels=true;message="Loaded tool-owned seam-test cube. No scene or source assets changed.";});
            GUILayout.FlexibleSpace(); GUILayout.Label(document.Revision==savedRevision?"Saved":"Unsaved",EditorStyles.miniLabel);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            var next=(GameObject)EditorGUILayout.ObjectField("Preview model",model,typeof(GameObject),true,GUILayout.MinWidth(260));
            if(next!=model){ model=next; TryAction(()=>{preview.Load(model); materialSlot=Mathf.Clamp(materialSlot,0,Mathf.Max(0,preview.MaterialSlotCount-1)); message=String.Join("; ",preview.Diagnostics); repaintPixels=true;}); }
            int slot=EditorGUILayout.IntField("Material slot",materialSlot,GUILayout.Width(210));
            if(slot!=materialSlot){materialSlot=Mathf.Clamp(slot,0,Mathf.Max(0,preview.MaterialSlotCount-1)); repaintPixels=true;}
            var nextChannel=(PaintChannel)EditorGUILayout.EnumPopup(channel,GUILayout.Width(105));
            if(nextChannel!=channel){channel=nextChannel; repaintPixels=true; message=channel==PaintChannel.Normal?"Normal channel stores RGBA values; specialized tangent-normal composition is not implemented.":"Painting only the selected channel; other channels remain unchanged.";}
            GUILayout.EndHorizontal();
        }
        void DrawBrush(Rect rect)
        {
            GUILayout.BeginArea(rect,EditorStyles.helpBox); brushScroll=GUILayout.BeginScrollView(brushScroll);
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
                    foreach(var preset in BrushTips.BundledPresets){var p=preset;menu.AddItem(new GUIContent(p.Category+"/"+p.Name),brush.presetId==p.Id,()=>ApplyPreset(p));}
                    foreach(var preset in BrushTips.ImportedPresets){var p=preset;menu.AddItem(new GUIContent("Imported/"+p.Category+"/"+p.Name),brush.presetId==p.Id,()=>ApplyPreset(p));}
                    if(BundledBrushSets.LoadWarnings.Count>0)menu.AddDisabledItem(new GUIContent("Some bundled brushes could not be loaded (see Console)"));
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
            GUILayout.Space(12);
            GUILayout.Label("Input",EditorStyles.boldLabel);
            GUILayout.Label("LMB: paint\nAlt / RMB: orbit 3D\nMMB: pan 2D / 3D\nWheel: zoom\nEsc: cancel stroke\nCtrl/Cmd Z: undo\nCtrl/Cmd Shift Z: redo\nFocus loss: cancel active stroke",EditorStyles.wordWrappedMiniLabel);
            GUILayout.Space(12);
            EditorGUILayout.HelpBox("Prototype uses one IMGUI pressure path. Tablet response, HiDPI and latency still require Unity/device tests. No-pressure input uses Unity's fixed fallback.",MessageType.Info);
            if(GUILayout.Button("Show implementation limits")) Dialogs.Inform("G0/G1 limits","CPU source brush; bounded GPU tile compositor. Static readable meshes. Selected channel only. Native generation save and restricted RGB8 PSD. Full adjustment layers, groups, masks, editable surface paths, pose/BlendShape, mesh-map generators and lilToon parity remain unfinished. See Documentation~/STATUS.md in the package.");
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }
        void DrawLayers(Rect rect)
        {
            GUILayout.BeginArea(rect,EditorStyles.helpBox);
            GUILayout.Label("Layers (top first)",EditorStyles.boldLabel);
            using(new EditorGUI.DisabledScope(stroke!=null))
            {
                GUILayout.BeginHorizontal();
                if(GUILayout.Button("+")) selectedLayer=document.AddLayer("Paint "+(document.Layers.Count+1)).Id;
                if(GUILayout.Button("+ Fill")) selectedLayer=document.AddFillLayer("Fill "+(document.Layers.Count+1),new Dictionary<PaintChannel,Rgba32>{{channel,GetBrush().Color}}).Id;
                if(GUILayout.Button("+ Adjust"))
                {
                    var menu=new GenericMenu();
                    menu.AddItem(new GUIContent("Invert"),false,()=>selectedLayer=document.AddAdjustmentLayer("Invert",AdjustmentSettings.Invert()).Id);
                    menu.AddItem(new GUIContent("Levels"),false,()=>selectedLayer=document.AddAdjustmentLayer("Levels",AdjustmentSettings.Levels()).Id);
                    menu.AddItem(new GUIContent("Hue / Saturation"),false,()=>selectedLayer=document.AddAdjustmentLayer("Hue / Saturation",AdjustmentSettings.HueSaturation()).Id);
                    menu.ShowAsContext();
                }
                using(new EditorGUI.DisabledScope(document.Layers.Count<2)) if(GUILayout.Button("−")){document.RemoveLayer(selectedLayer);selectedLayer=document.Layers[document.Layers.Count-1].Id;}
                GUILayout.EndHorizontal();
                layerScroll=GUILayout.BeginScrollView(layerScroll);
                for(int i=document.Layers.Count-1;i>=0;i--)
                {
                    var layer=document.Layers[i]; GUILayout.BeginHorizontal();
                    bool visible=GUILayout.Toggle(layer.Visible,"",GUILayout.Width(18)); if(visible!=layer.Visible) document.SetLayerVisibility(layer.Id,visible);
                    if(GUILayout.Toggle(selectedLayer==layer.Id,(document.IsEffectivelyClipped(i)?"↳ ":"")+(layer.Kind==LayerKind.Fill?"[Fill] ":layer.Kind==LayerKind.Adjustment?"[Adj] ":"")+layer.Name,"Button")) selectedLayer=layer.Id;
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
                var active=document.Layers.FirstOrDefault(l=>l.Id==selectedLayer);
                if(active!=null)
                {
                    string name=EditorGUILayout.DelayedTextField("Name",active.Name);if(name!=active.Name)document.SetLayerName(active.Id,name);
                    float opacity=EditorGUILayout.Slider("Opacity",(float)active.Opacity,0,1);if(Math.Abs(opacity-active.Opacity)>.00001)document.SetLayerOpacity(active.Id,opacity,coalesce:true);
                    var blend=(LayerBlendMode)EditorGUILayout.EnumPopup("Blend",active.BlendMode);if(blend!=active.BlendMode)document.SetLayerBlendMode(active.Id,blend);
                    bool clipping=EditorGUILayout.Toggle("Clip to layer below",active.Clipping);if(clipping!=active.Clipping)document.SetLayerClipping(active.Id,clipping);
                    bool enabled=EditorGUILayout.Toggle("Channel enabled",active.IsChannelEnabled(channel));if(enabled!=active.IsChannelEnabled(channel))TryAction(()=>document.SetChannelEnabled(active.Id,channel,enabled));
                    GUILayout.BeginHorizontal();int index=document.Layers.ToList().FindIndex(l=>l.Id==active.Id);
                    using(new EditorGUI.DisabledScope(index>=document.Layers.Count-1)) if(GUILayout.Button("Up"))document.MoveLayer(active.Id,index+1);
                    using(new EditorGUI.DisabledScope(index<=0)) if(GUILayout.Button("Down"))document.MoveLayer(active.Id,index-1);
                    GUILayout.EndHorizontal();
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
            if(compositor.Texture!=null) EditorGUI.DrawTextureTransparent(image,compositor.Texture,ScaleMode.StretchToFill);
            GUI.EndClip();
        }
        void HandleCanvasInput(Event e)
        {
            if(preview.HasModel && stroke==null && preview.HandleNavigation(surfaceRect,e)){Repaint();return;}
            if(canvasRect.Contains(e.mousePosition))
            {
                if(e.type==EventType.ScrollWheel){canvasZoom=Mathf.Clamp(canvasZoom*Mathf.Exp(-e.delta.y*.07f),.2f,16);e.Use();Repaint();return;}
                if(e.type==EventType.MouseDrag && e.button==2){canvasPan+=e.delta;e.Use();Repaint();return;}
            }
            if(e.type==EventType.MouseDown && e.button==0 && !e.alt && (canvasRect.Contains(e.mousePosition)||surfaceRect.Contains(e.mousePosition)))
            {
                surfaceStroke=surfaceRect.Contains(e.mousePosition);
                if(surfaceStroke && !preview.CanPaint){message="This preview snapshot is not safe to paint. See its load diagnostics.";return;}
                TryAction(()=>
                {
                    if(EditingMask) stroke=document.BeginMaskStroke(selectedLayer,GetBrush());
                    else
                    {
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
            Seed=brush.randomSeedPerStroke ? seeds.Next() : 0 };
        /// <summary>プリセットの設定を今のブラシに写す。色は今のまま残す（チャンネルの値として選んだものだから）。</summary>
        internal void ApplyPreset(Core.BrushPreset preset)
        {
            var s=preset.CreateSettings(); var color=brush.color; var curve=brush.pressureCurve;
            brush=new BrushState{ presetId=preset.Id, presetName=preset.Name, radius=(float)s.Radius, hardness=(float)s.Hardness, spacing=(float)s.Spacing, opacity=(float)s.Opacity, flow=(float)s.Flow,
                color=color, pressureCurve=curve, pressureSize=s.PressureSize, pressureOpacity=s.PressureOpacity, pressureFlow=s.PressureFlow, erase=s.Erase,
                tipId=BrushTips.IdOf(s), textureId=BrushTips.IdOf(s.Texture), angle=(float)s.Angle, roundness=(float)s.Roundness, followDirection=s.FollowDirection,
                sizeJitter=(float)s.SizeJitter, angleJitter=(float)s.AngleJitter, roundnessJitter=(float)s.RoundnessJitter, opacityJitter=(float)s.OpacityJitter, flowJitter=(float)s.FlowJitter,
                scatter=(float)s.Scatter, count=s.Count, textureDepth=(float)s.TextureDepth, textureScale=(float)s.TextureScale };
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
            if(e.keyCode==KeyCode.Escape && stroke!=null){FinishStroke(false);e.Use();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.Z){if(e.shift)document.Redo();else document.Undo();e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.S){SaveProject(e.shift);e.Use();}
        }
        bool ConfirmDiscard() => document.Revision==savedRevision || Dialogs.Confirm("Keep current work?","Current work has unsaved changes. A native recovery checkpoint will be kept before opening another document.","Continue","Cancel") && SaveRecovery();
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
        internal void SaveProject(bool saveAs)
        {
            if(stroke!=null)return;
            string target=projectRoot;
            if(saveAs||String.IsNullOrEmpty(target)){target=Dialogs.SaveFolder("Choose a NEW project folder",String.IsNullOrEmpty(projectRoot)?Application.dataPath:projectRoot,"TexturePaint");if(String.IsNullOrEmpty(target))return;}
            string expected=target==projectRoot?projectToken:null;
            TryAction(()=>
            {
                Dialogs.Progress("Texture Painter","Freezing native source and preparing validated save generation. Input is paused.",.1f);
                var files=new Dictionary<string,byte[]>{{"document.utpaint",DocumentBinary.Write(document)}};
                var state=new ViewState{modelAssetGuid=model==null?"":AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(model)),materialSlot=materialSlot,selectedChannel=(int)channel};
                files.Add("view.json",System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(state,true)));
                files.Add("brush.json",System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(brush,true)));
                if(importedOriginal!=null)files.Add("imported-original.psd",importedOriginal);
                string psdStatus="PSD channels saved";
                try
                {
                    foreach(PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
                        if(document.Layers.Any(l=>l.IsChannelEnabled(c))) files.Add(c+".psd",PsdCodec.Write(PsdBridge.Export(document,c)));
                }
                catch(Exception ex)
                {
                    foreach(var key in files.Keys.Where(k=>k.EndsWith(".psd")&&k!="imported-original.psd").ToArray())files.Remove(key);
                    psdStatus="Native project saved; PSD NOT updated: "+ex.Message;
                    if(!Dialogs.Confirm("PSD projection unavailable",ex.Message+"\nSave the lossless native project only? Existing saved generations remain intact.","Save native only","Cancel"))return;
                }
                files.Add("save-status.txt",System.Text.Encoding.UTF8.GetBytes(psdStatus));
                var saved=GenerationStore.Commit(target,files,expected);
                projectRoot=target;projectToken=saved.Token;savedRevision=document.Revision;externalConflict=false;message=psdStatus+" / "+saved.Generation;
            });
            Dialogs.ClearProgress();
        }
        internal void OpenProject()
        {
            string path=Dialogs.OpenFolder("Open YoluPainter project folder",projectRoot??Application.dataPath);if(String.IsNullOrEmpty(path)||!ConfirmDiscard())return;
            TryAction(()=>
            {
                var snapshot=GenerationStore.Load(path);var next=DocumentBinary.Read(snapshot.Files["document.utpaint"]);
                document=next;BindDocument();selectedLayer=document.Layers.Count>0?document.Layers[document.Layers.Count-1].Id:Guid.Empty;
                projectRoot=path;projectToken=snapshot.Token;savedRevision=document.Revision;externalConflict=false;
                importedOriginal=snapshot.Files.TryGetValue("imported-original.psd",out var original)?original:null;
                var notes=new List<string>();
                if(snapshot.Files.TryGetValue("brush.json",out var preset)){brush=ReadBrushState(System.Text.Encoding.UTF8.GetString(preset));var missing=MissingTipNote();if(missing!=null)notes.Add(missing);}
                if(snapshot.Files.TryGetValue("view.json",out var view))
                {
                    var state=JsonUtility.FromJson<ViewState>(System.Text.Encoding.UTF8.GetString(view));materialSlot=state.materialSlot;channel=(PaintChannel)state.selectedChannel;
                    var loaded=AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(state.modelAssetGuid));
                    if(loaded!=null){model=loaded;preview.Load(model);materialSlot=Mathf.Clamp(materialSlot,0,Mathf.Max(0,preview.MaterialSlotCount-1));}else notes.Add("Model asset is unavailable; assign it explicitly.");
                }
                message="Verified generation loaded: "+snapshot.Generation+(notes.Count>0?". "+String.Join(" ",notes):"");
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
                document=next;if(document.Layers.Count==0)document.AddLayer("Paint 1");BindDocument();selectedLayer=document.Layers.Last().Id;projectRoot=null;projectToken=null;savedRevision=-1;
                importedOriginal=result.CopyOriginalBytes();channel=PaintChannel.Color;message="Imported supported RGB8 raster subset. Original bytes retained; saves go to a separate native project folder.";
            });
        }
        internal void ExportPng()
        {
            string path=Dialogs.SaveFile("Export selected channel PNG",Application.dataPath,channel+".png","png");if(String.IsNullOrEmpty(path))return;
            TryAction(()=>
            {
                var texture=new Texture2D(document.Width,document.Height,TextureFormat.RGBA32,false,true);
                try{texture.LoadRawTextureData(document.Composite(channel));texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());message="Exported "+channel+" PNG. No material was changed; ICC and normal-map packing are not applied.";}
                finally{DestroyImmediate(texture);}
            });
        }
        void SavePreset(){string p=Dialogs.SaveFile("Save brush",Application.dataPath,"brush","json");if(!String.IsNullOrEmpty(p))TryAction(()=>File.WriteAllText(p,JsonUtility.ToJson(brush,true)));}
        /// <summary>ブラシのファイルを取り込み、プロジェクトのライブラリに入れて 1 つ目を選ぶ。対応していない設定は
        /// 取り込み後に一覧で知らせる（黙って捨てない）。</summary>
        internal void ImportBrushes()
        {
            string path=Dialogs.OpenFile("Import brushes",brushImportFolder,BrushImport.Extensions);if(String.IsNullOrEmpty(path))return;
            brushImportFolder=Path.GetDirectoryName(path);
            TryAction(()=>
            {
                IReadOnlyList<Core.Brushes.ImportedBrush> brushes;
                try{brushes=BrushImport.ReadFile(path);}
                catch(Core.Brushes.BrushImportException ex){message="Brush import failed: "+ex.Message;Dialogs.Inform("Brush import failed",Path.GetFileName(path)+"\n\n"+ex.Message);return;}
                var added=BrushLibrary.Add(brushes,BrushImport.PrettyName(Path.GetFileName(path)));
                if(added.Count>0)ApplyPreset(added[0]);
                var notes=brushes.SelectMany(b=>b.Warnings.Select(w=>(b.Name,w))).GroupBy(x=>x.w).Select(g=>g.Key+(g.Count()>1?" ("+g.Count()+" brushes)":" ("+g.First().Name+")")).ToList();
                message="Imported "+added.Count+" brush"+(added.Count==1?"":"es")+" from "+Path.GetFileName(path)+(notes.Count>0?"; "+notes.Count+" unsupported setting(s) left out.":".");
                if(notes.Count>0)Dialogs.Inform("Brush import notes",String.Join("\n",notes.Take(30))+(notes.Count>30?"\n… and "+(notes.Count-30)+" more.":"")+"\n\nThe brushes were imported without these settings.");
            });
        }
        internal void DeleteImportedBrush()
        {
            if(!BrushLibrary.IsLibraryPreset(brush.presetId))return;
            if(!Dialogs.Confirm("Delete imported brush","Delete \""+brush.presetName+"\" from this project's brush library? The original file is not touched.","Delete","Cancel"))return;
            TryAction(()=>{BrushLibrary.Remove(brush.presetId);ApplyPreset(BuiltInBrushes.Presets[0]);message="Deleted the imported brush.";});
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
