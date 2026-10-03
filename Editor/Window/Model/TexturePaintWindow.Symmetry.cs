using UnityEditor;
using Yozolab.YoluPainter.Core;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>対称の設定はブラシ状態、1つのストロークで凍結して使う。重複画素は最大覆いで塗る。</summary>
    public sealed partial class TexturePaintWindow
    {
        bool symmetry { get => brush.symmetry3D; set => brush.symmetry3D = value; }
        SymmetryAxis symmetryAxis { get => brush.symmetryAxis; set => brush.symmetryAxis = value; }
        float symmetryOffset { get => brush.symmetryOffset; set => brush.symmetryOffset = value; }
        bool symmetryPlaneShown { get => brush.symmetryAxesShown; set => brush.symmetryAxesShown = value; }
        MirrorPlane? strokeMirror;
        RadialSymmetry? strokeRadial;
        bool strokeIgnoreVisibility;
        internal bool Symmetry { get => symmetry; set { symmetry = value; Repaint(); } }
        internal SymmetryAxis SymmetryAxis { get => symmetryAxis; set { symmetryAxis = value; Repaint(); } }
        /// <summary>対称の面のモデルのルートからのずれ（面の法線の向き、シーンの単位）。</summary>
        internal float SymmetryOffset { get => symmetryOffset; set { symmetryOffset = float.IsNaN(value) || float.IsInfinity(value) ? 0 : Mathf.Clamp(value, -MaxSymmetryOffset, MaxSymmetryOffset); Repaint(); } }
        internal bool SymmetryPlaneShown { get => symmetryPlaneShown; set { symmetryPlaneShown = value; Repaint(); } }
        internal const float MaxSymmetryOffset = 1000;
        /// <summary>今の設定の対称の面（プレビューの空間）。</summary>
        internal MirrorPlane CurrentSymmetryPlane => preview.SymmetryPlane(symmetryAxis, symmetryOffset);
        /// <summary>試験用: 直前の 3D のダブで、映した側がどうなったか（シンメトリーを使っていなければ null）。</summary>
        internal MirrorOutcome? LastMirrorOutcome { get; private set; }

        /// <summary>3D のストロークの 1 つのダブ（PaintAt から）。ストロークの初めのダブで、テクセルの見え方の記憶を作り、対称の面を決める。
        /// シンメトリーなら、映した側のダブを画素ごとに大きいほうの覆いで合わせた 1 つのダブにする（二重に塗らない）。</summary>
        SurfaceDabResult BuildStrokeSurfaceDab(SurfaceHit hit, float radius)
        {
            if (surfaceVisibility == null)
            {
                surfaceVisibility = new SurfaceVisibilityCache();
                strokeMirror = symmetry ? CurrentSymmetryPlane : (MirrorPlane?)null;
                strokeRadial = brush.radialSymmetry3D ? CurrentRadialSymmetry : (RadialSymmetry?)null;
                strokeIgnoreVisibility = brush.symmetryIgnoreVisibility;
            }
            if (!strokeMirror.HasValue && !strokeRadial.HasValue) { LastMirrorOutcome = null; return preview.BuildSurfaceDabs(hit, radius, document.Width, document.Height, brush.hardness, surfaceVisibility); }
            var dab = SurfaceRadialSymmetry.Build(preview.Geometry, hit, strokeMirror, strokeRadial, strokeIgnoreVisibility,
                radius, document.Width, document.Height, preview.CameraPosition, brush.hardness, preview.BrushBudget, surfaceVisibility);
            LastMirrorOutcome = dab.Outcome;
            string note = MirrorNote(dab.Outcome);
            if (note != null) message = note;
            return dab.Result;
        }

        static string MirrorNote(MirrorOutcome outcome)
        {
            switch (outcome)
            {
                case MirrorOutcome.NoSurface: return L.Tr("Symmetry: some copies have no surface nearby and were skipped.");
                case MirrorOutcome.OtherSlot: return L.Tr("Symmetry: some copies are on another texture set and were skipped.");
                case MirrorOutcome.Hidden: return L.Tr("Symmetry: some copies cannot be seen from this view and were skipped. Enable Ignore visibility to paint hidden symmetry copies.");
                default: return null;
            }
        }

        /// <summary>対称の面を 3D ビューに見せるか、プレビューに伝える（3D を描く直前に）。ストロークの間はそのストロークの面。</summary>
        void SyncSymmetryPlane()
        {
            if (preview == null) return;
            bool show = (StrokeSymmetryFrozen ? strokeMirror.HasValue : symmetry) && symmetryPlaneShown && preview.HasModel;
            preview.ShownSymmetryPlane = !show ? (MirrorPlane?)null : StrokeHasPlane ? strokeMirror : CurrentSymmetryPlane;
        }

        /// <summary>今の 3D のストロークが面を決めた後か（初めのダブで見え方の記憶と一緒に決める。ストロークが終われば記憶は捨てる）。</summary>
        bool StrokeSymmetryFrozen => stroke != null && surfaceStroke && surfaceVisibility != null;
        bool StrokeHasPlane => StrokeSymmetryFrozen && strokeMirror.HasValue;

        /// <summary>モデルの外形の中央を通る面にするずれ（今の軸で）。</summary>
        float BoundsCenterOffset() => preview.HasModel ? preview.SymmetryPlane(symmetryAxis, 0).SignedDistance(preview.Bounds.center) : 0;

        // ───────── プロパティの欄・3D ビューの見出し ─────────

        void SymmetrySection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-symmetry", L.Tr("Symmetry"), "flip")) return;
            bool on = PaintGui.FitToggle(Mark("symmetry", rows.Row()), L.Tr("Mirror 3D strokes"), symmetry,
                L.Tr("Reflect dabs across the model root's plane, within the active texture set."));
            if (on != symmetry) Symmetry = on;

            var row = rows.Row();
            PaintGui.Text(new Rect(row.x, row.y, LabelColumn, row.height), PaintGui.Fit(L.Tr("Axis"), LabelColumn - 6, PaintTheme.Label), PaintTheme.Label);
            var axes = UiRows.Split(new Rect(row.x + LabelColumn, row.y, row.width - LabelColumn, row.height), 3, 4);
            for (int i = 0; i < 3; i++)
            {
                var axis = (SymmetryAxis)i;
                if (PaintGui.Button(Mark("symmetry-" + axis, axes[i]), axis.ToString(), symmetryAxis == axis, true, L.Tr("The plane is perpendicular to the model's local {0} axis", axis.ToString()))) SymmetryAxis = axis;
            }
            float offset = PaintGui.NumberField(Mark("symmetry-offset", rows.Row()), L.Tr("Center"), symmetryOffset, "0.####", " m", .001f, -MaxSymmetryOffset, MaxSymmetryOffset,
                L.Tr("Where the plane is: the distance from the model's origin along the axis, in scene units. Drag to change it (Shift: ×10), or click to type."));
            if (offset != symmetryOffset) SymmetryOffset = offset;
            var c = UiRows.Split(rows.Row(), 2, 6);
            if (PaintGui.FitButton(Mark("symmetry-origin", c[0]), L.Tr("Model origin"), false, symmetryOffset != 0, L.Tr("Put the plane through the model's origin (its root object)"))) SymmetryOffset = 0;
            if (PaintGui.FitButton(Mark("symmetry-bounds", c[1]), L.Tr("Bounds center"), false, preview.HasModel, L.Tr("Put the plane through the middle of the model's bounding box"))) SymmetryOffset = BoundsCenterOffset();
            bool radial = PaintGui.FitToggle(Mark("symmetry-radial", rows.Row()), L.Tr("Radial 3D strokes"), brush.radialSymmetry3D);
            if (radial != brush.radialSymmetry3D) RadialSymmetry3D = radial;
            var ra = UiRows.Split(rows.Row(), 3, 4);
            for (int i = 0; i < 3; i++) if (PaintGui.Button(Mark("symmetry-radial-" + (SymmetryAxis)i, ra[i]), ((SymmetryAxis)i).ToString(), brush.radialSymmetryAxis == (SymmetryAxis)i)) RadialSymmetryAxis = (SymmetryAxis)i;
            brush.radialSymmetryCount = PaintGui.FitIntSlider(Mark("symmetry-count", rows.SliderRow()), L.Tr("Copies"), brush.radialSymmetryCount, 2, 16);
            bool ignore = PaintGui.FitToggle(Mark("symmetry-visibility", rows.Row()), L.Tr("Ignore visibility"), brush.symmetryIgnoreVisibility,
                L.Tr("Symmetry copies paint back-facing and occluded surfaces. The original dab still paints visible surfaces only."));
            if (ignore != brush.symmetryIgnoreVisibility) SymmetryIgnoreVisibility = ignore;
            bool shown = PaintGui.FitToggle(Mark("symmetry-plane", rows.Row()), L.Tr("Show symmetry axes"), symmetryPlaneShown);
            if (shown != symmetryPlaneShown) SymmetryPlaneShown = shown;
            PaintGui.Paragraph(rows, L.Tr("Copies stay on this texture set. Mirror and radial symmetry combine up to N × 2 copies; overlaps use the largest coverage."), PaintTheme.TextDim);
            PaintGui.Paragraph(rows, L.Tr("2D canvas symmetry"), PaintTheme.TextDim);
            var modes = new[] { CanvasSymmetryMode.None, CanvasSymmetryMode.Vertical, CanvasSymmetryMode.Horizontal, CanvasSymmetryMode.Both, CanvasSymmetryMode.Radial };
            var names = new[] { L.Tr("Off"), L.Tr("Vertical"), L.Tr("Horizontal"), L.Tr("Both"), L.TrIn("Symmetry", "Radial") };
            for (int j = 0; j < 2; j++)
            {
                var buttons = UiRows.Split(rows.Row(), j == 0 ? 3 : 2, 4);
                for (int i = 0; i < buttons.Length; i++)
                {
                    int k = j == 0 ? i : i + 3;
                    if (PaintGui.Button(Mark("symmetry-2d-" + modes[k], buttons[i]), names[k], brush.canvasSymmetry == modes[k])) brush.canvasSymmetry = modes[k];
                }
            }
            brush.canvasSymmetryX = PaintGui.NumberField(Mark("symmetry-2d-x", rows.Row()), L.Tr("Center X"), brush.canvasSymmetryX * document.Width, "0.##", " px", 1, 0, document.Width) / document.Width;
            brush.canvasSymmetryY = PaintGui.NumberField(Mark("symmetry-2d-y", rows.Row()), L.Tr("Center Y"), brush.canvasSymmetryY * document.Height, "0.##", " px", 1, 0, document.Height) / document.Height;
            brush.canvasSymmetryCount = PaintGui.FitIntSlider(Mark("symmetry-2d-count", rows.SliderRow()), L.Tr("Copies"), brush.canvasSymmetryCount, 2, 16);
            if (PaintGui.FitButton(Mark("symmetry-2d-center", rows.Row()), L.Tr("Canvas center"))) brush.canvasSymmetryX = brush.canvasSymmetryY = .5f;
            PaintGui.Paragraph(rows, L.Tr("Axes use document pixels, independent of canvas view rotation and flip. Smudge and Clone cannot be combined with symmetry."), PaintTheme.TextDim);
            rows.Space(4);
        }

        /// <summary>3D ビューの見出しの、シンメトリーの切り替え（ストロークの間は変えられない）。</summary>
        void DrawSymmetryHeaderToggle(Rect r)
        {
            if (PaintGui.IconButton(Mark("symmetry-header", r), "flip", L.Tr("Symmetry: mirror 3D brush strokes across the model's plane (set the axis with the options bar's symmetry menu)"), symmetry, stroke == null, 16))
                Symmetry = !symmetry;
        }

        // ───────── 映した側のカーソル ─────────

        /// <summary>マウスの下のダブを映した所に、カーソルの円を出す（カメラから見えない所なら薄く。塗られない側だと分かるように）。</summary>
        void DrawMirroredBrushCursor(Vector2 mouse)
        {
            if (Event.current.type != EventType.Repaint || !ShowsBrushCursor || !HasSurfaceSymmetry || !preview.HasModel || (stroke != null && !surfaceStroke)) return;
            if (!surfaceRect.Contains(mouse) || !preview.TryPick(surfaceRect, mouse, out var hit) || hit.MaterialSlot != materialSlot) return;
            var geometry = preview.Geometry;
            float radius = Mathf.Max(.000001f, preview.Bounds.size.magnitude) * brush.radius / document.Width;
            MirrorPlane? mirror = StrokeSymmetryFrozen ? strokeMirror : symmetry ? CurrentSymmetryPlane : (MirrorPlane?)null;
            RadialSymmetry? radial = StrokeSymmetryFrozen ? strokeRadial : brush.radialSymmetry3D ? CurrentRadialSymmetry : (RadialSymmetry?)null;
            var positions = new System.Collections.Generic.List<Vector3> { hit.Position };
            for (int i = 0; i < (radial?.Count ?? 1); i++) for (int m = 0; m < (mirror.HasValue ? 2 : 1); m++)
            {
                if (i == 0 && m == 0) continue;
                var p = m == 0 ? hit.Position : mirror.Value.Reflect(hit.Position);
                var n = m == 0 ? hit.Normal : mirror.Value.ReflectDirection(hit.Normal);
                if (radial.HasValue) { p = radial.Value.RotatePoint(p, i); n = radial.Value.RotateDirection(n, i); }
                bool duplicate = false; foreach (var old in positions) if ((old - p).magnitude <= radius * SurfaceSymmetry.OnPlaneFraction) { duplicate = true; break; }
                if (duplicate) continue; positions.Add(p);
                if (!geometry.TryFindClosestPoint(p, SurfaceSymmetry.SearchDistance(radius, geometry.Bounds), n,
                    SurfaceSymmetry.MaxClosestPointNodeVisits, out var found, out _) || found.MaterialSlot != materialSlot || found.RendererIndex != hit.RendererIndex) continue;
                if (preview.TryWorldToGui(surfaceRect, found.Position, out var at)) MirrorCircle(at, preview.WorldRadiusToGuiPoints(found.Position, radius), SeenFromCamera(geometry, found));
            }
        }

        /// <summary>面の上の点がカメラから見えるか（表向きで、手前に別の面が無い）。</summary>
        bool SeenFromCamera(SurfaceGeometry geometry, SurfaceHit point)
        {
            var camera = preview.CameraPosition; var toPoint = point.Position - camera; float distance = toPoint.magnitude;
            if (distance <= 0 || Vector3.Dot(point.Normal, -toPoint) <= 0) return false;
            float epsilon = Mathf.Max(1e-7f, geometry.Bounds.size.magnitude * 1e-5f);
            if (!geometry.TryRaycast(new Ray(camera, toPoint / distance), out var first, false, distance + epsilon)) return true;
            return first.TriangleIndex == point.TriangleIndex || first.Distance >= distance - epsilon;
        }

        static void MirrorCircle(Vector2 center, float radius, bool seen)
        {
            if (radius < 1.5f) radius = 1.5f;
            int n = Mathf.Clamp(Mathf.CeilToInt(radius * .8f), 16, 96);
            var points = new Vector3[n + 1];
            for (int i = 0; i <= n; i++) { float t = i * Mathf.PI * 2 / n; points[i] = new Vector3(center.x + Mathf.Cos(t) * radius, center.y + Mathf.Sin(t) * radius); }
            var previous = Handles.color; float alpha = seen ? 1 : .4f;
            Handles.color = new Color(0, 0, 0, .5f * alpha); Handles.DrawAAPolyLine(3, points);
            Handles.color = new Color(.45f, .82f, 1f, .95f * alpha); Handles.DrawAAPolyLine(1.2f, points);
            Handles.color = previous;
        }
    }
}
