using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor.Preview;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>Single IMGUI input path: no duplicate pointer/mouse event subscription.</summary>
    public sealed partial class TexturePaintWindow : EditorWindow
    {
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
        long renderedRevision = -1;
        bool repaintPixels = true, surfaceStroke, externalConflict, editMask;
        Vector2 previousPointer, layerScroll;
        float previousPressure = 1;
        /// <summary>キャンバスでの左ボタンの働き。</summary>
        internal enum PaintTool { Brush, Fill, Gradient, SelectRectangle, SelectEllipse, Lasso, MagicWand, Move, Path, Eyedropper, PolygonFill, IdSelect }
        PaintTool tool;
        int materialSlot, resolution = 1024;
        double lastRecovery, lastExternalCheck;
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
        /// <summary>全部のテクスチャセットとセットの並びが、開いた/保存したファイルと同じか。</summary>
        internal bool IsSaved
        {
            get
            {
                if (document == null || currentSet == null) return false;
                SyncCurrentSet();
                return setsRevision == savedSetsRevision && textureSets.All(s => s.Document.Revision == s.SavedRevision);
            }
        }
        internal bool HasExternalConflict => externalConflict;
        internal PaintChannel Channel { get => channel; set { channel = value; repaintPixels = true; } }
        /// <summary>true のあいだ、ストロークは選択レイヤーの画素ではなくマスクに入る。</summary>
        internal bool EditMask { get => editMask; set => editMask = value; }
        internal Guid SelectedLayer { get => selectedLayer; set => selectedLayer = value; }
        /// <summary>モーダルダイアログの差し替え口（テスト用）。</summary>
        internal IPainterDialogs Dialogs { get; set; } = EditorPainterDialogs.Instance;
        /// <summary>PaintAt の 2D 写像の逆。ピクセル中心 (x+0.5, y+0.5) の GUI 座標を返す（表示の回転・反転・拡大・パン込み）。</summary>
        internal Vector2 PixelToGui(int x, int y) => CanvasViewNow().ToGui(x + .5f, y + .5f);

        void OnEnable()
        {
            minSize = new Vector2(980,640); wantsMouseMove = true; wantsMouseEnterLeaveWindow = true; L.LanguageChanged += Repaint; PainterToolIcons.Changed += Repaint;
            compositor = CreateCompositor(); preview = new IsolatedModelPreview(); ApplyPreviewFrameRate(); // 3D を描く回数の上限（Model/TexturePaintWindow.RedrawRate.cs）
            if (materialEdits == null) materialEdits = new PreviewMaterialEdits();
            materialEdits.Touch(); preview.MaterialEdits = materialEdits; preview.Shading = previewShading; BindPreviewScene();
            if (String.IsNullOrEmpty(recoveryRoot)) recoveryRoot=Path.GetFullPath(Path.Combine("Library","YoluPainter","recovery-"+Guid.NewGuid().ToString("N")));
            try
            {
                if (File.Exists(Path.Combine(recoveryRoot,"current")))
                {
                    var snapshot=GenerationStore.Load(recoveryRoot); var recovered=YlpFormat.Open(snapshot.Files);
                    var sets=ReadTextureSets(recovered); var recoveredResources=ResourceIndex.Load(recovered.Files,recovered.Resources);
                    ReplaceProject(sets,sets.First(s=>s.Id==recovered.Project.CurrentSet)); AdoptResources(recoveredResources);
                    ResetSetsBaseline(false); recoveryToken=snapshot.Token; projectCreatedBy=recovered.Info.CreatedBy;
                    var recoveryNotes=new List<string>();
                    foreach(var set in sets)RestoreSavedSelection(set,recovered.SetFiles(set.Id),recoveryNotes);
                    message="Recovered native source from the last durable checkpoint. Unsaved edits after that checkpoint may be missing."+(recoveryNotes.Count>0?" "+String.Join(" ",recoveryNotes):"");
                }
            }
            catch (Exception ex) { message="Recovery was not loaded: "+ex.Message; }
            resolution=PainterSettings.DefaultResolution;
            if (document==null) CreateDocument(resolution);
            BindDocument();
            if (model!=null) TryAction(()=>preview.Load(model));
            EditorApplication.update+=Tick; PainterSettings.Changed+=SettingsChanged; EditorApplication.projectChanged+=OnUnityProjectChanged; HookResources();
            AssemblyReloadEvents.beforeAssemblyReload+=BeforeReload;
            EditorApplication.playModeStateChanged+=PlayModeChanged;
        }
        /// <summary>設定のメモリ予算をドキュメントに入れる。今の画素がすでに予算を超えているときは画素を捨てず、予算を今の量まで
        /// 広げてそう知らせる。</summary>
        /// <returns>予算を広げたときの知らせ。問題なければ null。</returns>
        /// <remarks>予算はプロジェクト全体のもの。描けるのは今のテクスチャセットだけなので、今のセットの文書に「設定 − ほかのセットが使って
        /// いる量」（レイヤーの画素と Undo の履歴）を入れる。ほかのセットは描いていないあいだ増えないので、セットを切り替える・足す・消す
        /// ときに入れ直せば全体が設定を超えない（Undo の最小段数はセットごとに残る。1 回の操作の予算は同時に 1 つしか走らないのでそのまま）。</remarks>
        internal string ApplyBudgets()
        {
            if(document==null||stroke!=null)return null;
            if(compositor!=null)compositor.ResidentBudgetBytes=PainterSettings.GpuCacheBytes;
            long otherSource=0,otherHistory=0;
            foreach(var set in textureSets) if(set!=currentSet&&set.Document!=null){otherSource+=set.Document.AllocatedBytes;otherHistory+=set.Document.HistoryBytes;}
            document.MinimumUndoSteps=PainterSettings.MinUndoSteps; document.UndoBudgetBytes=Math.Max(0,PainterSettings.UndoBudgetBytes-otherHistory); document.ActiveStrokeBudgetBytes=PainterSettings.StrokeBudgetBytes;
            long budget=PainterSettings.SourceBudgetBytes, source=budget-otherSource;
            if(source>=document.AllocatedBytes){document.SourceBudgetBytes=source;return null;}
            document.SourceBudgetBytes=document.AllocatedBytes;
            if(otherSource>0) return "This project already holds "+((document.AllocatedBytes+otherSource)>>20)+" MiB of layer pixels ("+(otherSource>>20)+" MiB in the other texture sets), above the "+(budget>>20)+" MiB budget in Project Settings > YoluPainter; nothing more can be added to this texture set until the budget is raised.";
            return "This document already holds "+(document.AllocatedBytes>>20)+" MiB of layer pixels, above the "+(source>>20)+" MiB budget in Project Settings > YoluPainter; nothing more can be added until the budget is raised.";
        }
        void SettingsChanged(){var note=ApplyBudgets();var resourceNote=ApplyResourceBudget();if(note!=null||resourceNote!=null)message=note??resourceNote;libraryListing=null;ApplyCompositorSettings();ApplyPreviewFrameRate();Repaint();}
        internal static void OpenSettings()=>SettingsService.OpenProjectSettings(PainterSettingsProvider.Path);
        /// <summary>今のテクスチャセットの文書を新しく結び付けたとき（新しいプロジェクト・開く・取り込み）: 予算を入れ、表示を作り直し、
        /// メッシュマップ（前の文書のもの）を捨てる。</summary>
        void BindDocument()
        {
            SyncCurrentSet();
            var budgetNote=ApplyBudgets(); if(budgetNote!=null)message=budgetNote;
            WatchHistory(currentSet);
            repaintPixels=true; renderedRevision=-1; recoveredRevision=-1;
            ClearMeshMaps();
        }
        /// <summary>size × size の空のレイヤー 1 枚の、テクスチャセットが 1 つのプロジェクトにする（スロットは今のまま）。ファイルとは結び付かない。</summary>
        void CreateDocument(int size)
        {
            var next=new PaintDocument(size,size,128,PainterSettings.UndoBudgetBytes);
            int slot=currentSet!=null?materialSlot:0;
            var set=new TextureSet(next.Id,BaseSetName(slot),slot,next);
            ReplaceProject(new[]{set},set);
            ApplyBudgets();
            selectedLayer=document.AddLayer(L.Tr("Layer")+" 1").Id; document.ClearHistory(); pristineRevision=document.Revision;
            ForgetProjectFile();
        }
        /// <summary>今のプロジェクトをファイルから切り離す（新規・取り込み）。</summary>
        void ForgetProjectFile()
        {
            projectPath=null; projectToken=null; savedRevision=-1; importedPsdPath=null; externalConflict=false; ResetCanvasView(); NewProjectRecord();
            ResetSetsBaseline(false);
        }
        void OnLostFocus() { FinishStroke(false); CancelShapeDrag(); EndLightingDrag(true); CancelToolDrag(); ReleaseCanvasViewInput(); preview?.CancelNavigation(); SaveRecovery(); }
        void BeforeReload() { FinishStroke(false); CancelShapeDrag(); EndLightingDrag(true); preview?.CancelNavigation(); SaveRecovery(); }
        void PlayModeChanged(PlayModeStateChange state) { if(state==PlayModeStateChange.ExitingEditMode){ FinishStroke(false); CancelShapeDrag(); SaveRecovery(); } }
        void OnDisable()
        {
            FinishStroke(false); CancelShapeDrag(); preview?.CancelNavigation(); SaveRecovery();
            EditorApplication.update-=Tick; PainterSettings.Changed-=SettingsChanged; EditorApplication.projectChanged-=OnUnityProjectChanged; UnhookResources(); DisposeAssetThumbnails(); L.LanguageChanged-=Repaint; PainterToolIcons.Changed-=Repaint; AssemblyReloadEvents.beforeAssemblyReload-=BeforeReload; EditorApplication.playModeStateChanged-=PlayModeChanged;
            DisposeNormalOutput(); DisposeLighting(); DisposeMeshMaps(); DisposeThumbnails(); DisposeColorPanel(); DisposeTextureSetTextures(); DisposeMaterialChannelTextures(); DisposeModelShowTextures(); compositor?.Dispose(); preview?.Dispose(); compositor=null; preview=null;
            if(selectionOverlay!=null){DestroyImmediate(selectionOverlay);selectionOverlay=null;overlayFor=null;}
        }
        void Tick()
        {
            if(document==null) return;
            TickAssetsPanel(); // アセットのパネルが出たら、リソースの出どころを確かめる（TexturePaintWindow.AssetsPanel.cs）
            if(stroke==null && EditorApplication.timeSinceStartup-lastRecovery>PainterSettings.RecoveryIntervalSeconds && !RecoveryIsCurrent()) SaveRecovery();
            if(!String.IsNullOrEmpty(projectPath) && EditorApplication.timeSinceStartup-lastExternalCheck>3) CheckExternalChange();
            // テクスチャセットのサムネイル（間隔を置いて作り直すので、描き直しを 1 度だけ頼む。パネルが見えていなければそれきり）
            if(stroke==null && SetThumbnailStale && document.Revision!=thumbnailRepaintAsked && EditorApplication.timeSinceStartup-setThumbnailBuilt>SetThumbnailInterval){thumbnailRepaintAsked=document.Revision;Repaint();}
            // 描いていないあいだは GPU の写しを手放す（Update が来ないと合成器は古い写しを捨てられない）
            if(compositor!=null && compositor.ResidentBytes>0 && EditorApplication.timeSinceStartup-lastComposite>GpuCacheIdleSeconds) compositor.ReleaseResidentCaches();
            RebuildCompositorIfPending(); // 「表示の合成」の設定が変わったのをストロークの終わりまで待っていたら
            WatchSourceMaterials(); ReconcileMaterialEdits(); RepaintPreviewIfWanted();
        }
        long thumbnailRepaintAsked=-1;
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
            HandleModelPicker(e); HandleEnvironmentPicker(e); // 環境のテクスチャを選ぶ窓（Model/TexturePaintWindow.Display3D.cs）
            HandleKeys(e);
            if(e.type==EventType.KeyDown&&HandleToolKeys(e))return;
            // 合成（GPU への転送と合成、Normal の出力、3D のプレビューの更新）は描くときだけ。入力のイベント（ストローク中の MouseDrag
            // など）のたびに合成すると 1 回の処理が重くなり、OS がマウスの移動をまとめて届く点がまばらになる。表示の前には必ず
            // Repaint が来るので、その間の変更はまとめて 1 回で合成する。
            if(e.type==EventType.Repaint) CountRepaint(); // 描き直しの回数（RedrawRate.cs）
            if(e.type==EventType.Repaint && DisplayNeedsCompositing) RefreshDisplayForFrame(); // 時間で区切る。残りは次の描画へ（Compositing.cs）
            LayoutShell();
            SyncPolygonFillHover(); // ポインタの下の範囲の強調を、ツール・モデル・範囲の種類に合わせる（Tools/TexturePaintWindow.PolygonFill.cs）
            PaintGui.Fill(WindowRect,PaintTheme.WindowBg);
            if(canvasRect.width>0) DrawCanvas();
            if(surfaceRect.width>0 && e.type==EventType.Repaint)
            {
                PaintGui.Fill(surfaceRect,PaintTheme.CanvasBg); SyncSymmetryPlane(); SyncShapeOverlay(); SyncEnvironment(); preview.Render(surfaceRect); NoteDisplayProblems(); DrawPathMarkers(); DrawShapeGizmo(pointerAtStart);
                // 3D の描画（PreviewRenderUtility）の後はイベントのマウスの位置が (0,0) になっているので、外枠のマウスの乗った見た目のために戻す
                if(Event.current!=null) Event.current.mousePosition=pointerAtStart;
            }
            DrawShell();
            if(surfaceRect.width>0){DrawSurfaceBrushCursor(pointerAtStart);DrawMirroredBrushCursor(pointerAtStart);} // 3D の描画の後に GUI の状態を戻してから重ねる
            HandleCanvasInput(e);
        }

        /// <summary>合成し直して、2D の表示と 3D のプレビュー（全部のテクスチャセットとその照明）に入れる（テストが呼ぶ。表示の合成を全部終える）。</summary>
        internal void RefreshPreviewTextures() => RefreshPreviewTextures(null);
        /// <summary>schedule の時間の中で合成する（Repaint から。null なら全部）。</summary>
        void RefreshPreviewTextures(CompositeSchedule schedule)
        {
            TryAction(()=> { compositor.Update(document,channel,schedule); UpdateNormalOutput(); ShowTextureSets(); });
            TryAction(UpdatePreviewLighting);
            TryAction(ShowMaterialChannels);
            TryAction(ShowModelShowTextures); // 1 つのチャンネル・メッシュマップだけの見せ方（Model/TexturePaintWindow.ModelShow.cs）
            lastComposite=EditorApplication.timeSinceStartup; CompositeCount++;
            renderedRevision=document.Revision; repaintPixels=false;
        }

        internal BrushState Brush { get => brush; set => brush = value; }
        internal PaintTool Tool { get => tool; set { CancelToolDrag(); if (value != tool) EndIdColorPick(); tool = value; } }

        void TryAction(Action action)
        {
            try{action();}catch(Exception ex){if(stroke!=null)FinishStroke(false);message=ex is LayerOpException refused?RefusalText(refused):ex.Message;Debug.LogWarning("Texture Painter: "+ex.Message);}
        }
    }
}
