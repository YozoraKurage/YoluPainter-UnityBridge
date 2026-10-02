using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 3D ビューのシンメトリー（Substance Painter の Symmetry のミラー）: ブラシの 3D のダブを、モデルのローカルの軸に直交する面で映した
    /// 所にも塗る（<see cref="SurfaceSymmetry"/>）。設定はウィンドウの状態（ブラシのプリセットではない。モデルの性質で、ブラシを
    /// 替えても続くもの）。ストロークの初めのダブで面を決め、ストロークの間は変えない。2D キャンバスのストロークは映さない（UV の
    /// 配置は左右対称とは限らない）。3D ビューには面を薄く見せ（切り替えられる）、マウスの下のダブを映した所にカーソルの円を出す。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] bool symmetry;
        [SerializeField] SymmetryAxis symmetryAxis = SymmetryAxis.X;
        [SerializeField] float symmetryOffset;
        [SerializeField] bool symmetryPlaneShown = true;
        /// <summary>今のストロークで使う対称の面（初めのダブで決める。シンメトリーが切れていれば null）。</summary>
        MirrorPlane? strokeMirror;

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
            }
            if (!strokeMirror.HasValue) { LastMirrorOutcome = null; return preview.BuildSurfaceDabs(hit, radius, document.Width, document.Height, brush.hardness, surfaceVisibility); }
            var dab = preview.BuildSymmetricSurfaceDabs(hit, strokeMirror.Value, radius, document.Width, document.Height, brush.hardness, surfaceVisibility);
            LastMirrorOutcome = dab.Outcome;
            string note = MirrorNote(dab.Outcome);
            if (note != null) message = note;
            return dab.Result;
        }

        static string MirrorNote(MirrorOutcome outcome)
        {
            switch (outcome)
            {
                case MirrorOutcome.NoSurface: return L.Tr("Symmetry: no surface of the model at the mirrored point; only this side was painted.");
                case MirrorOutcome.OtherSlot: return L.Tr("Symmetry: the mirrored point is on another texture set; only this side was painted.");
                case MirrorOutcome.Hidden: return L.Tr("Symmetry: the mirrored side cannot be seen from this view and was not painted. Turn the model so that both sides are visible.");
                default: return null;
            }
        }

        /// <summary>対称の面を 3D ビューに見せるか、プレビューに伝える（3D を描く直前に）。ストロークの間はそのストロークの面。</summary>
        void SyncSymmetryPlane()
        {
            if (preview == null) return;
            bool show = symmetry && symmetryPlaneShown && preview.HasModel;
            preview.ShownSymmetryPlane = !show ? (MirrorPlane?)null : StrokeHasPlane ? strokeMirror : CurrentSymmetryPlane;
        }

        /// <summary>今の 3D のストロークが面を決めた後か（初めのダブで見え方の記憶と一緒に決める。ストロークが終われば記憶は捨てる）。</summary>
        bool StrokeHasPlane => stroke != null && surfaceStroke && surfaceVisibility != null && strokeMirror.HasValue;

        /// <summary>モデルの外形の中央を通る面にするずれ（今の軸で）。</summary>
        float BoundsCenterOffset() => preview.HasModel ? preview.SymmetryPlane(symmetryAxis, 0).SignedDistance(preview.Bounds.center) : 0;

        // ───────── プロパティの欄・3D ビューの見出し ─────────

        void SymmetrySection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-symmetry", L.Tr("Symmetry"), "flip")) return;
            bool on = PaintGui.FitToggle(Mark("symmetry", rows.Row()), L.Tr("Mirror 3D strokes"), symmetry,
                L.Tr("Each dab painted in the 3D view is painted again at its mirror image across the plane below (like Substance Painter's mirror symmetry). 2D canvas strokes are not mirrored."));
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
            bool shown = PaintGui.FitToggle(Mark("symmetry-plane", rows.Row()), L.Tr("Show the plane"), symmetryPlaneShown, L.Tr("Shows the symmetry plane faintly in the 3D view"));
            if (shown != symmetryPlaneShown) SymmetryPlaneShown = shown;
            PaintGui.Paragraph(rows, L.Tr("The mirrored dab lands on the nearest surface of this texture set. Like every 3D dab, it only paints what the camera can see."), PaintTheme.TextDim);
            rows.Space(4);
        }

        /// <summary>3D ビューの見出しの、シンメトリーの切り替え（ストロークの間は変えられない）。</summary>
        void DrawSymmetryHeaderToggle(Rect r)
        {
            if (PaintGui.IconButton(Mark("symmetry-header", r), "flip", L.Tr("Symmetry: mirror 3D brush strokes across the model's plane (set the axis in the Brush tool's Symmetry section)"), symmetry, stroke == null, 16))
                Symmetry = !symmetry;
        }

        // ───────── 映した側のカーソル ─────────

        /// <summary>マウスの下のダブを映した所に、カーソルの円を出す（カメラから見えない所なら薄く。塗られない側だと分かるように）。</summary>
        void DrawMirroredBrushCursor(Vector2 mouse)
        {
            if (Event.current.type != EventType.Repaint || !ShowsBrushCursor || !symmetry || !preview.HasModel || (stroke != null && !surfaceStroke)) return;
            if (!surfaceRect.Contains(mouse) || !preview.TryPick(surfaceRect, mouse, out var hit) || hit.MaterialSlot != materialSlot) return;
            var geometry = preview.Geometry;
            float worldRadius = Mathf.Max(.000001f, preview.Bounds.size.magnitude) * brush.radius / document.Width;
            var plane = StrokeHasPlane ? strokeMirror.Value : CurrentSymmetryPlane;
            var mirrored = plane.Reflect(hit.Position);
            if ((mirrored - hit.Position).magnitude <= worldRadius * SurfaceSymmetry.OnPlaneFraction) return;
            if (!geometry.TryFindClosestPoint(mirrored, SurfaceSymmetry.SearchDistance(worldRadius, geometry.Bounds), plane.ReflectDirection(hit.Normal),
                    SurfaceSymmetry.MaxClosestPointNodeVisits, out var found, out _) || found.MaterialSlot != materialSlot) return;
            if (!preview.TryWorldToGui(surfaceRect, found.Position, out var at)) return;
            MirrorCircle(at, preview.WorldRadiusToGuiPoints(found.Position, worldRadius), SeenFromCamera(geometry, found));
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
