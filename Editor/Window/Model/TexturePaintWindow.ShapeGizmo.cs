using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 3D ビューの形のグラデーションのギズモ（<see cref="ShapeGizmo"/>）: プロパティの欄で「3D ビューで編集」にした Generator（選んだ層の
    /// 画素かマスクのスタックにあるもの）か、選んでいる塗りつぶしの層の投影の置き場（ボックス。型の上の投影とデカール。TexturePaintWindow.FillImages.cs。
    /// Substance Painter のマニピュレーターと同じく、層を選んでいる間は出し、マスクを編集している間と Q で隠したときは出さない）の形の線とハンドルを
    /// 3D ビューに重ね、形の値をモデルの面に薄く重ねる
    /// （<see cref="IsolatedModelPreview.ShownShapeGradient"/>）。ハンドルを押した所から離すまでの変更は、スライダーと同じく
    /// <see cref="ApplyFilterSettings"/> で入れて 1 つの Undo にまとめ、描き直しも同じ流れ（文書の版が変わり、次の Repaint で合成する）。
    /// Esc・フォーカスの喪失・リロード・Play への移行ではドラッグの前に戻し、履歴にも残さない（<see cref="PaintDocument.CancelCoalescing"/>）。
    /// ストロークの最中はハンドルを掴まない。ハンドルの無い所の押下は今のツール（ブラシなど）にそのまま渡す。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] string shapeEditFilter = "";
        /// <summary>投影の置き場のハンドルを隠している（Q・投影の欄のボタン。Substance の Show/Hide manipulator）。窓の状態で、保存しない。</summary>
        [SerializeField] bool projectionHandlesHidden;
        [SerializeField] string fillGradientEditLayer = "";
        [SerializeField] PaintChannel fillGradientEditChannel;
        [SerializeField] ShapeGizmoMode shapeGizmoMode = ShapeGizmoMode.Move;
        ShapeHandle shapeDrag; ShapeVolume shapeDragStart; Vector2 shapeDragFrom; Guid shapeDragFilter, shapeDragLayer; int shapeDragControl; bool shapeDragProjection, shapeDragFillGradient; PaintChannel shapeDragFillChannel;

        /// <summary>3D ビューで編集している形のグラデーションの Generator（無ければ Guid.Empty）。窓の状態で、保存しない。編集している間は、
        /// 投影の置き場より先にこちらを出す（ギズモは 1 つ）。</summary>
        internal Guid ShapeEditFilter
        {
            get => Guid.TryParse(shapeEditFilter, out var id) ? id : Guid.Empty;
            set { if (value != ShapeEditFilter) CancelShapeDrag(); shapeEditFilter = value == Guid.Empty ? "" : value.ToString(); if (value != Guid.Empty) { fillGradientEditLayer = ""; } Repaint(); }
        }
        /// <summary>今 3D ビューに置き場のハンドルが出ている塗りつぶしの層（無ければ Guid.Empty）: 選んでいる層の投影が型の上の投影かデカールで、
        /// その層のマスクを編集しておらず、形のグラデーションを編集しておらず、ハンドルを隠していないとき。</summary>
        internal Guid ProjectionEditLayer => EditedProjection()?.Id ?? Guid.Empty;
        /// <summary>投影の置き場のハンドルを隠す（Q・投影の欄のボタン）。隠したら、ドラッグの最中なら取り消す。</summary>
        internal bool ProjectionHandlesHidden
        {
            get => projectionHandlesHidden;
            set
            {
                if (!value && (ShapeEditFilter != Guid.Empty || FillGradientEditLayer != Guid.Empty))
                { CancelShapeDrag(); shapeEditFilter = fillGradientEditLayer = ""; }
                if (value && shapeDragProjection) CancelShapeDrag();
                projectionHandlesHidden = value; Repaint();
            }
        }
        internal Guid FillGradientEditLayer
        {
            get => Guid.TryParse(fillGradientEditLayer, out var id) ? id : Guid.Empty;
            set { if (value != FillGradientEditLayer) CancelShapeDrag(); fillGradientEditLayer = value == Guid.Empty ? "" : value.ToString(); if (value != Guid.Empty) { shapeEditFilter = ""; } Repaint(); }
        }
        internal PaintChannel FillGradientEditChannel { get => fillGradientEditChannel; set { if (value != fillGradientEditChannel) CancelShapeDrag(); fillGradientEditChannel = value; } }
        GeneratorSettings EditedFillGradient()
        {
            if (FillGradientEditLayer != selectedLayer || document == null || EditingMask) return null;
            var layer = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            return layer != null && layer.FillGradients.TryGetValue(fillGradientEditChannel, out var g) ? g : null;
        }
        internal static ShapeSnap SceneShapeSnap => new ShapeSnap(EditorSnapSettings.move, EditorSnapSettings.scale, EditorSnapSettings.rotate);
        internal ShapeGizmoMode ShapeGizmoMode { get => shapeGizmoMode; set { if (shapeDrag == ShapeHandle.None) shapeGizmoMode = value; Repaint(); } }
        /// <summary>ギズモのハンドルをドラッグしている間 true。</summary>
        internal bool ShapeDragging => shapeDrag != ShapeHandle.None;

        /// <summary>編集している形のグラデーション（選んだ層の画素かマスクのスタックにあるもの）。無ければ null。</summary>
        FilterEffect EditedShapeGradient()
        {
            var id = ShapeEditFilter;
            if (id == Guid.Empty || document == null) return null;
            FilterEffect effect;
            try { effect = document.FindFilter(selectedLayer, id, out _); }
            catch (KeyNotFoundException) { return null; }
            return effect != null && effect.Settings.IsGenerator && effect.Settings.Generator.Type == GeneratorType.ShapeGradient ? effect : null;
        }
        /// <summary>
        /// 置き場のハンドルを出す投影の層: 選んでいる層が型の上に投影する塗りつぶし（デカールを含む）なら、選んでいる間ずっと（Substance Painter の
        /// マニピュレーターと同じ）。出さないのは、その層のマスクを編集している間（マスクを塗るクリックがハンドルに取られないように。Substance でも
        /// マスクを選ぶと塗るツールになり、マニピュレーターは出ない）、形のグラデーションを編集している間（そちらを出す）、Q で隠したとき。無ければ null。
        /// </summary>
        PaintLayer EditedProjection()
        {
            if (document == null || projectionHandlesHidden || EditingMask || EditedShapeGradient() != null || EditedFillGradient() != null) return null;
            var layer = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            return layer != null && layer.Kind == LayerKind.Fill && layer.Projection.ReadsMeshMaps ? layer : null;
        }
        /// <summary>ギズモが動かす形（モデルのルートの空間）: 形のグラデーションの形か、投影の置き場（球の投影は球として見せる）。無ければ false。</summary>
        bool TryGizmoVolume(out ShapeVolume volume)
        {
            var effect = EditedShapeGradient();
            if (effect != null) { volume = effect.Settings.Generator.Volume; return true; }
            var fillGradient = EditedFillGradient();
            if (fillGradient != null) { volume = fillGradient.Volume; return true; }
            var layer = EditedProjection();
            if (layer != null) { var p = layer.Projection; volume = p.Mode == FillProjectionMode.Spherical ? p.Placement.WithShape(GeneratorShape.Sphere) : p.Placement; return true; }
            volume = default; return false;
        }
        bool ShapeGizmoShown => surfaceRect.width > 0 && preview != null && preview.HasModel && TryGizmoVolume(out _);

        /// <summary>3D を描く直前に: 編集している形の値をモデルの面に重ねるか、プレビューに伝える。</summary>
        void SyncShapeOverlay()
        {
            if (preview == null) return;
            var e = ShapeGizmoShown ? EditedShapeGradient() : null;
            var settings = e?.Settings.Generator ?? (ShapeGizmoShown ? EditedFillGradient() : null);
            preview.ShownShapeGradient = settings == null ? (ShapeGradientOverlay?)null : ShapeGradientOverlay.Of(settings, preview.ModelRootPosition, preview.ModelRootRotation, CurrentMaterialGroup, ShapeOverlayTint);
        }

        /// <summary>形の線とハンドルを 3D ビューに重ねる（Repaint のとき、3D を描いた後）。マウスの下（ドラッグ中はそのハンドル）を光らせる。</summary>
        void DrawShapeGizmo(Vector2 pointer)
        {
            if (Event.current.type != EventType.Repaint || !ShapeGizmoShown) return;
            TryGizmoVolume(out var v); var view = preview.GizmoView(surfaceRect);
            Vector3 rootPosition = preview.ModelRootPosition; var rootRotation = preview.ModelRootRotation;
            var hover = shapeDrag != ShapeHandle.None ? shapeDrag : stroke == null && surfaceRect.Contains(pointer) ? ShapeGizmo.Hit(v, rootPosition, rootRotation, view, shapeGizmoMode, pointer) : ShapeHandle.None;
            var offset = (Vector3)surfaceRect.position;
            GUI.BeginClip(surfaceRect);
            var previous = Handles.color; var previousDepth = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            try
            {
                foreach (var line in ShapeGizmo.Lines(v, rootPosition, rootRotation, view, shapeGizmoMode, hover))
                {
                    var points = new Vector3[line.Points.Length];
                    for (int i = 0; i < points.Length; i++) points[i] = line.Points[i] - offset;
                    if (line.Filled) { Handles.color = line.Color; Handles.DrawAAConvexPolygon(points); continue; }
                    Handles.color = new Color(0, 0, 0, .45f * line.Color.a); Handles.DrawAAPolyLine(line.Width + 2, points); // 明るい面の上でも見える縁
                    Handles.color = line.Color; Handles.DrawAAPolyLine(line.Width, points);
                }
                foreach (var (handle, at) in ShapeGizmo.HandlePoints(v, rootPosition, rootRotation, view, shapeGizmoMode))
                {
                    var p = at - (Vector2)offset;
                    if (handle == ShapeHandle.MoveFree)
                    {
                        float s = ShapeGizmo.CenterPoints;
                        var r = new Rect(p.x - s / 2, p.y - s / 2, s, s);
                        PaintGui.Outline(r, new Color(0, 0, 0, .6f), 3, 0); PaintGui.Outline(r, hover == handle ? ShapeGizmo.Hover : Color.white, 1.5f, 0);
                    }
                    else if (handle >= ShapeHandle.SizeXPos)
                    {
                        float s = ShapeGizmo.KnobPoints; int axis = (handle - ShapeHandle.SizeXPos) / 2;
                        var r = new Rect(p.x - s / 2, p.y - s / 2, s, s);
                        EditorGUI.DrawRect(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), new Color(0, 0, 0, .7f));
                        EditorGUI.DrawRect(r, hover == handle ? ShapeGizmo.Hover : axis == 0 ? ShapeGizmo.AxisX : axis == 1 ? ShapeGizmo.AxisY : ShapeGizmo.AxisZ);
                    }
                }
            }
            finally { Handles.color = previous; Handles.zTest = previousDepth; GUI.EndClip(); }
        }

        /// <summary>ギズモの入力（ハンドルを押す・ドラッグ・離す）。扱ったら true。ハンドルの無い所の押下は扱わない（今のツールへ）。</summary>
        bool HandleShapeGizmo(Event e)
        {
            if (shapeDrag != ShapeHandle.None)
            {
                if (e.type == EventType.MouseDrag) { ApplyShapeDrag(e.mousePosition, e.shift, e.control || e.command); e.Use(); Repaint(); return true; }
                if (e.rawType == EventType.MouseUp) { EndShapeDrag(); e.Use(); Repaint(); return true; }
                return e.type == EventType.MouseDown; // ドラッグ中のほかのボタンは使わない
            }
            if (e.type != EventType.MouseDown || e.button != 0 || e.alt || stroke != null || !surfaceRect.Contains(e.mousePosition) || !ShapeGizmoShown) return false;
            var effect = EditedShapeGradient(); TryGizmoVolume(out var v);
            var handle = ShapeGizmo.Hit(v, preview.ModelRootPosition, preview.ModelRootRotation, preview.GizmoView(surfaceRect), shapeGizmoMode, e.mousePosition);
            if (handle == ShapeHandle.None) return false;
            document.EndCoalescing(); // 前の欄のドラッグにまとめない
            shapeDrag = handle; shapeDragStart = v; shapeDragFrom = e.mousePosition; shapeDragFilter = effect?.Id ?? Guid.Empty; shapeDragLayer = selectedLayer; shapeDragFillGradient = EditedFillGradient() != null; shapeDragFillChannel = fillGradientEditChannel; shapeDragProjection = effect == null && !shapeDragFillGradient;
            shapeDragControl = GUIUtility.GetControlID(FocusType.Passive); GUIUtility.hotControl = shapeDragControl;
            e.Use(); Repaint(); return true;
        }

        void ApplyShapeDrag(Vector2 mouse, bool symmetric, bool snap)
        {
            if (shapeDragFillGradient) { ApplyFillGradientDrag(mouse, symmetric, snap); return; }
            if (shapeDragProjection) { ApplyProjectionDrag(mouse, symmetric, snap); return; }
            FilterEffect effect = null;
            if (selectedLayer == shapeDragLayer) try { effect = document.FindFilter(shapeDragLayer, shapeDragFilter, out _); } catch (KeyNotFoundException) { }
            if (effect == null || !effect.Settings.IsGenerator) { EndShapeDrag(); return; } // 層や Generator が無くなった
            var g = effect.Settings.Generator;
            var next = ShapeGizmo.Drag(shapeDrag, shapeDragStart, preview.ModelRootPosition, preview.ModelRootRotation, preview.GizmoView(surfaceRect), shapeDragFrom, mouse, symmetric, snap, SceneShapeSnap);
            if (next.Equals(g.Volume)) return;
            string why = next.Refusal();
            if (why != null) { message = why; return; }
            ApplyFilterSettings(effect.Id, effect.Settings.WithGenerator(g.WithVolume(next)), coalesce: true);
        }
        /// <summary>投影の置き場のドラッグ: 押した所の形から計算し（ずれない）、層の投影に 1 回の Undo にまとめて入れる。球の投影は球として
        /// 動かし、置き場（ボックス）に戻す。</summary>
        void ApplyProjectionDrag(Vector2 mouse, bool symmetric, bool snap)
        {
            var layer = selectedLayer == shapeDragLayer ? document.Layers.FirstOrDefault(l => l.Id == shapeDragLayer && l.Kind == LayerKind.Fill) : null;
            if (layer == null) { EndShapeDrag(); return; } // 層が無くなった
            var next = ShapeGizmo.Drag(shapeDrag, shapeDragStart, preview.ModelRootPosition, preview.ModelRootRotation, preview.GizmoView(surfaceRect), shapeDragFrom, mouse, symmetric, snap, SceneShapeSnap);
            var placement = layer.Projection.WithPlacement(next);
            if (placement.Equals(layer.Projection)) return;
            string why = placement.Placement.Refusal();
            if (why != null) { message = why; return; }
            TryAction(() => document.SetFillProjection(layer.Id, placement, coalesce: true));
        }
        void ApplyFillGradientDrag(Vector2 mouse, bool symmetric, bool snap)
        {
            var layer = document.Layers.FirstOrDefault(l => l.Id == shapeDragLayer);
            if (selectedLayer != shapeDragLayer || layer == null || !layer.FillGradients.TryGetValue(shapeDragFillChannel, out var g)) { EndShapeDrag(); return; }
            var next = ShapeGizmo.Drag(shapeDrag, shapeDragStart, preview.ModelRootPosition, preview.ModelRootRotation, preview.GizmoView(surfaceRect), shapeDragFrom, mouse, symmetric, snap, SceneShapeSnap);
            if (next.Equals(g.Volume)) return;
            string why = next.Refusal(); if (why != null) { message = why; return; }
            ApplyFillGradient(layer.Id, shapeDragFillChannel, g.WithVolume(next), coalesce: true);
        }
        void EndShapeDrag()
        {
            shapeDrag = ShapeHandle.None;
            if (GUIUtility.hotControl == shapeDragControl) GUIUtility.hotControl = 0;
            document?.EndCoalescing();
        }
        /// <summary>ギズモのドラッグをやめ、ドラッグの前に戻す（履歴にも残さない）。ドラッグしていなければ何もしない。</summary>
        internal void CancelShapeDrag()
        {
            if (shapeDrag == ShapeHandle.None) return;
            shapeDrag = ShapeHandle.None;
            if (GUIUtility.hotControl == shapeDragControl) GUIUtility.hotControl = 0;
            if (document == null) return;
            try { if (document.CancelCoalescing()) { repaintPixels = true; message = L.Tr("The shape drag was cancelled; the shape is where it was."); } }
            catch (InvalidOperationException ex) { message = ex.Message; document.EndCoalescing(); }
            Repaint();
        }

        // ───────── 試験用 ─────────

        /// <summary>試験用: 編集している形のハンドルの 3D ビューでの位置（ウィンドウの GUI 座標）。見えなければ null。</summary>
        internal Vector2? ShapeHandleGui(ShapeHandle handle)
        {
            if (!ShapeGizmoShown) return null;
            TryGizmoVolume(out var v);
            foreach (var (h, at) in ShapeGizmo.HandlePoints(v, preview.ModelRootPosition, preview.ModelRootRotation, preview.GizmoView(surfaceRect), shapeGizmoMode))
                if (h == handle) return at;
            return null;
        }
        /// <summary>試験用: GUI の点の下のハンドル。</summary>
        internal ShapeHandle ShapeHandleAt(Vector2 gui) => ShapeGizmoShown && TryGizmoVolume(out var v) ? ShapeGizmo.Hit(v, preview.ModelRootPosition, preview.ModelRootRotation, preview.GizmoView(surfaceRect), shapeGizmoMode, gui) : ShapeHandle.None;
    }
}
