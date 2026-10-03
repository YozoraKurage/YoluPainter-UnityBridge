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
    /// 形のグラデーション（Generator の種類 5）の欄: 形（ボックス・球・平面）、3D ビューで形を編集するかとギズモのモード、モデルのルートの
    /// 空間での中心・回転・大きさ（数値の欄。ギズモと同じ値）、やわらかさ、Unity のシーンの物から形を写す口（読むだけ。1 回写すだけで、
    /// 「シーンから取り直す」で写し直す）。どの変更も文書の Undo に 1 回ずつ（数値の欄のドラッグとギズモのドラッグは 1 回にまとめる）。
    /// <para>座標の基準: 読み込んだモデルのルート（ゲームオブジェクト）の位置と向き。単位はシーンの単位で、ルートの大きさ（scale）は
    /// 掛けない（3D ビューのシンメトリーの面と同じ）。焼いた Position マップはワールドの軸でルートを原点にした空間なので、文書には
    /// <see cref="SetGeneratorInputs"/> がルートの位置と向きを答える（<see cref="IGeneratorModelFrame"/>）。</para>
    /// <para>シーンの物から写すとき、シーンの物・モデルのルート・どのコンポーネントにも書き込まない（Transform と Collider の値を読むだけ。
    /// Undo への記録も SetDirty もしない）。シーンの物への参照は窓の状態で、.ylp には写した値だけが残る。</para>
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>形を写すシーンの物（窓の状態。保存しない）と、それを選んだ Generator。</summary>
        [SerializeField] GameObject shapeSceneSource;
        [SerializeField] string shapeSceneSourceFilter = "";
        const int ShapeSourcePickerId = 0x59500020;
        const float ShapeVectorLabelWidth = 72;
        /// <summary>3D ビューに重ねる値の色（不透明度は値 1 のときの濃さ）。</summary>
        static readonly Color ShapeOverlayTint = new Color(1f, .55f, .12f, .55f);

        internal static string ShapeName(GeneratorShape shape)
        {
            switch (shape)
            {
                case GeneratorShape.Box: return L.TrIn("shape gradient", "Box");
                case GeneratorShape.Sphere: return L.TrIn("shape gradient", "Sphere");
                default: return L.TrIn("shape gradient", "Plane");
            }
        }

        /// <summary>新しく足す Generator の設定。形のグラデーションは、モデルがあればその外形の中央に、幅と奥行きはモデルに合わせ、高さは
        /// 半分のボックスで始める（どこに効くかすぐ見えるように。やわらかさ 50 %）。</summary>
        GeneratorSettings NewGeneratorSettings(GeneratorType type)
        {
            var g = GeneratorSettings.Default(type);
            if (type != GeneratorType.ShapeGradient || preview == null || !preview.HasModel) return g;
            var b = preview.Bounds; var inverse = Quaternion.Inverse(preview.ModelRootRotation);
            var center = inverse * (b.center - preview.ModelRootPosition); var size = inverse * b.size;
            double Size(float s) => Math.Max(ShapeVolume.MinSize, Math.Min(ShapeVolume.MaxSize, Math.Abs(s)));
            return g.WithVolume(new ShapeVolume(GeneratorShape.Box, center.x, center.y, center.z, 0, 0, 0, Size(size.x * 1.05f), Size(size.y * .5f), Size(size.z * 1.05f), .5));
        }

        /// <summary>種類ごとの設定の欄の、形のグラデーションの行。数値の欄で変えた値は next に入れて返す（呼び手が 1 回の Undo にまとめて入れる）。
        /// 形・編集の切り替え・シーンから写すはその場で入れる。</summary>
        GeneratorSettings ShapeGradientRows(UiRows rows, FilterEffect e, GeneratorSettings next, float indent)
        {
            var g = e.Settings.Generator; var v = next.Volume;
            ChoiceDropdown(Spot("generator.shape", Indent(rows.Row(), indent)), L.TrIn("shape gradient", "Shape"), v.Shape, (GeneratorShape[])Enum.GetValues(typeof(GeneratorShape)), ShapeName,
                s => { if (s != g.Volume.Shape) ApplyFilterSettings(e.Id, e.Settings.WithGenerator(g.WithVolume(g.Volume.WithShape(s)))); },
                L.Tr("Box: 1 inside, fading to 0 at its faces. Sphere: the same toward its surface. Plane: 0 behind it to 1 in front of it, across its width."));
            // 3D ビューで編集する・ギズモのモード
            bool editing = ShapeEditFilter == e.Id;
            var row = Indent(rows.Row(24), indent); float modes = 2 * 26 + 4;
            if (PaintGui.Button(Spot("generator.shape.edit", new Rect(row.x, row.y, row.width - modes, row.height)), L.Tr("Edit in 3D View"), editing, GUI.enabled && stroke == null,
                    L.Tr("Show the shape in the 3D view with handles: drag the arrows to move it, the rings to turn it and the squares on its faces to size it (Shift: both faces). The model shows the value faintly."), "view_in_ar"))
                ShapeEditFilter = editing ? Guid.Empty : e.Id;
            if (PaintGui.IconButton(Spot("generator.shape.move", new Rect(row.xMax - modes + 4, row.y, 26, row.height)), "transform", L.Tr("Handles: move (arrows along the model's axes, the square in the view's plane)"), shapeGizmoMode == ShapeGizmoMode.Move, GUI.enabled && editing, 16))
                ShapeGizmoMode = ShapeGizmoMode.Move;
            if (PaintGui.IconButton(Spot("generator.shape.rotate", new Rect(row.xMax - 26, row.y, 26, row.height)), "3d_rotation", L.Tr("Handles: rotate (rings about the model's axes; Ctrl snaps to 15°)"), shapeGizmoMode == ShapeGizmoMode.Rotate, GUI.enabled && editing, 16))
                ShapeGizmoMode = ShapeGizmoMode.Rotate;
            if (editing && surfaceRect.width <= 0) NoteRow(rows, L.Tr("Show the 3D view (View ▸ 3D or 2D | 3D) to see and drag the shape."), NoteKind.Info, indent);
            else if (editing && (preview == null || !preview.HasModel)) NoteRow(rows, L.Tr("Load a model (or the demo cube) to see the shape on it."), NoteKind.Info, indent);

            // 置き場（モデルのルートの空間、シーンの単位）
            PaintGui.GroupLabel(Indent(rows.Row(16), indent), L.Tr("Placement"), L.Tr("In the model root's space: its position and rotation, in scene units (the root's scale is not applied). The same values as the handles in the 3D view."));
            var c = VectorRow(rows, "generator.shape.center", L.Tr("Center"), v.CenterX, v.CenterY, v.CenterZ, .01f, "0.###", -ShapeVolume.MaxCoordinate, ShapeVolume.MaxCoordinate,
                L.Tr("Where the shape's centre is, from the model root (scene units). Drag to change (Shift: ×10), click to type."), indent);
            v = v.WithCenter(c[0], c[1], c[2]);
            var r = VectorRow(rows, "generator.shape.rotation", L.TrIn("shape gradient", "Rotation"), v.RotationX, v.RotationY, v.RotationZ, 1, "0.#", -ShapeVolume.MaxAngle, ShapeVolume.MaxAngle,
                L.Tr("Euler angles in degrees, as Unity's Transform shows them (turned about Z, then X, then Y)."), indent);
            v = v.WithRotation(WrapAngle(r[0]), WrapAngle(r[1]), WrapAngle(r[2]));
            switch (v.Shape)
            {
                case GeneratorShape.Box:
                {
                    var s = VectorRow(rows, "generator.shape.size", L.TrIn("shape gradient", "Size"), v.SizeX, v.SizeY, v.SizeZ, .01f, "0.###", ShapeVolume.MinSize, ShapeVolume.MaxSize,
                        L.Tr("The box's full width along each of its own axes (scene units)."), indent);
                    v = v.WithSize(s[0], s[1], s[2]);
                    break;
                }
                case GeneratorShape.Sphere:
                {
                    double radius = KeepNumber(Spot("generator.shape.radius", Indent(rows.Row(), indent)), L.TrIn("shape gradient", "Radius"), v.SizeX / 2, .01f, "0.###", ShapeVolume.MinSize / 2, ShapeVolume.MaxSize / 2,
                        L.Tr("The sphere's radius (scene units)."));
                    v = v.WithSize(radius * 2, v.SizeY, v.SizeZ);
                    break;
                }
                default:
                {
                    double width = KeepNumber(Spot("generator.shape.width", Indent(rows.Row(), indent)), L.TrIn("shape gradient", "Width"), v.SizeY, .01f, "0.###", ShapeVolume.MinSize, ShapeVolume.MaxSize,
                        L.Tr("How far the value takes to go from 0 behind the plane to 1 in front of it (scene units). The plane faces its own +Y."));
                    v = v.WithSize(v.SizeX, width, v.SizeZ);
                    break;
                }
            }
            if (v.Shape != GeneratorShape.Plane)
                v = v.WithFalloff(PaintGui.KeepSlider(Spot("generator.shape.falloff", Indent(rows.Row(), indent)), L.TrIn("shape gradient", "Falloff"), v.Falloff, 0, 1, "0", "%",
                    L.Tr("How much of the inside fades from 1 to 0 at the boundary: 0 %: a hard edge; 100 %: the fade reaches the middle (the thinnest axis of a box)."), true, 100));

            // シーンの物から写す（読むだけ）
            PaintGui.GroupLabel(Indent(rows.Row(16), indent), L.Tr("From the Scene"), L.Tr("Copies a scene object's shape relative to the model root once: its BoxCollider or SphereCollider, or else its position, rotation and scale. The scene is only read, never changed."));
            var source = shapeSceneSourceFilter == e.Id.ToString() ? shapeSceneSource : null;
            if (PaintGui.ObjectBox(Spot("generator.shape.source", Indent(rows.Row(), indent)), ref source, ShapeSourcePickerId, true, L.Tr("None (drop an object here)"), "view_in_ar",
                    L.Tr("Pick a scene object to copy its shape from (read only)"), L.Tr("Forget the object (the copied shape stays)"), "", GUI.enabled && stroke == null))
            {
                shapeSceneSource = source; shapeSceneSourceFilter = source != null ? e.Id.ToString() : "";
                if (source != null) CopyShapeFromScene(e.Id, source);
            }
            if (PaintGui.Button(Spot("generator.shape.recopy", Indent(rows.Row(24), indent)), L.Tr("Copy from the Scene Again"), false, GUI.enabled && source != null && stroke == null,
                    L.Tr("Reads the object's shape again (after you moved it in the scene). One undo step."), "sync"))
                CopyShapeFromScene(e.Id, source);
            return next.WithVolume(v);
        }

        /// <summary>X・Y・Z の 3 つの数値の欄の行（軸の色の下線）。触っていない値は受け取った double のまま返す。</summary>
        double[] VectorRow(UiRows rows, string id, string label, double x, double y, double z, float step, string format, double min, double max, string tooltip, float indent)
        {
            var cells = PaintGui.LabeledColumns(Indent(rows.Row(), indent), label, ShapeVectorLabelWidth, 3, 3);
            var values = new[] { x, y, z };
            for (int k = 0; k < 3; k++)
            {
                values[k] = KeepNumber(Spot(id + "." + "xyz"[k], cells[k]), "", values[k], step, format, min, max, "XYZ"[k] + ": " + tooltip);
                if (Event.current.type == EventType.Repaint) PaintGui.Fill(new Rect(cells[k].x + 4, cells[k].yMax - 2.5f, cells[k].width - 8, 1.5f), k == 0 ? ShapeGizmo.AxisX : k == 1 ? ShapeGizmo.AxisY : ShapeGizmo.AxisZ);
            }
            return values;
        }
        /// <summary>double の数値の欄（<see cref="PaintGui.NumberField"/>）。触っていなければ受け取った値のまま（float に丸めた値で変えない）。</summary>
        static double KeepNumber(Rect r, string label, double value, float step, string format, double min, double max, string tooltip)
        {
            float shown = (float)value, picked = PaintGui.NumberField(r, label, shown, format, "", step, (float)min, (float)max, tooltip);
            return picked != shown ? picked : value;
        }
        /// <summary>角度を −180〜180 に（打った値・ドラッグした値）。</summary>
        static double WrapAngle(double degrees)
        {
            if (degrees >= -180 && degrees <= 180) return degrees;
            double wrapped = degrees % 360;
            return wrapped > 180 ? wrapped - 360 : wrapped <= -180 ? wrapped + 360 : wrapped;
        }

        // ───────── シーンの物から写す ─────────

        /// <summary>シーンの物から形を写せない理由（写せるなら null）: モデルがシーンのものでない（デモのキューブ・Prefab のアセットを直接
        /// 読んだ）、物がシーンに無い。</summary>
        internal static string ShapeSceneRefusal(GameObject modelRoot, GameObject source)
        {
            if (modelRoot == null) return L.Tr("The shape is placed relative to the model's root, but no model from a scene is loaded (the demo cube has no root in the scene). Load the model from the scene first.");
            if (EditorUtility.IsPersistent(modelRoot) || !modelRoot.scene.IsValid() || !modelRoot.scene.isLoaded)
                return L.Tr("The model was loaded from a Prefab asset, not from a scene, so a scene object cannot be placed relative to its root. Put the model in a scene and load that object.");
            if (source == null) return L.Tr("Choose a scene object to copy the shape from.");
            if (EditorUtility.IsPersistent(source) || !source.scene.IsValid() || !source.scene.isLoaded) return L.Tr("'{0}' is not an object in an open scene.", source.name);
            return null;
        }

        /// <summary>シーンの物の形を、モデルのルートからの相対で写した形（読むだけ）。BoxCollider・SphereCollider があれば（最初のもの）その形と
        /// 中心・大きさ（物の大きさを掛けた値）、無ければ物の位置・回転・大きさ（今の形のまま。球は大きさのいちばん大きい軸が直径）。
        /// what は何から写したか。</summary>
        internal static ShapeVolume ShapeFromScene(ShapeVolume current, Transform source, Transform root, out string what)
        {
            var inverse = Quaternion.Inverse(root.rotation); var scale = source.lossyScale; scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            var shape = current.Shape; Vector3 center, size;
            var collider = source.GetComponents<Collider>().FirstOrDefault(c => c is BoxCollider || c is SphereCollider);
            if (collider is BoxCollider box)
            {
                shape = GeneratorShape.Box; center = source.TransformPoint(box.center); what = "BoxCollider";
                size = Vector3.Scale(new Vector3(Mathf.Abs(box.size.x), Mathf.Abs(box.size.y), Mathf.Abs(box.size.z)), scale);
            }
            else if (collider is SphereCollider sphere)
            {
                shape = GeneratorShape.Sphere; center = source.TransformPoint(sphere.center); what = "SphereCollider";
                float d = 2 * Mathf.Abs(sphere.radius) * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)); size = new Vector3(d, d, d);
            }
            else
            {
                center = source.position; what = "Transform";
                size = shape == GeneratorShape.Sphere ? Vector3.one * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)) : scale;
            }
            var local = inverse * (center - root.position); var euler = (inverse * source.rotation).eulerAngles;
            double Size(float s) => Math.Max(ShapeVolume.MinSize, Math.Min(ShapeVolume.MaxSize, s));
            double Angle(float a) => a > 180 ? a - 360 : a;
            return new ShapeVolume(shape, local.x, local.y, local.z, Angle(euler.x), Angle(euler.y), Angle(euler.z), Size(size.x), Size(size.y), Size(size.z), current.Falloff);
        }

        /// <summary>選んだ層の形のグラデーションに、シーンの物の形をモデルのルートからの相対で写す（1 回の Undo）。シーンには書き込まない。
        /// 写せないときは理由を状態の欄に出して false。</summary>
        internal bool CopyShapeFromScene(Guid filterId, GameObject source)
        {
            bool done = false;
            TryAction(() =>
            {
                if (stroke != null) { message = L.Tr("Finish the stroke first."); return; }
                var effect = document.FindFilter(selectedLayer, filterId, out _);
                if (effect == null || !effect.Settings.IsGenerator || effect.Settings.Generator.Type != GeneratorType.ShapeGradient) { message = L.Tr("Select the layer with the shape gradient first."); return; }
                string why = ShapeSceneRefusal(model, source);
                if (why != null) { message = why; return; }
                var g = effect.Settings.Generator;
                var volume = ShapeFromScene(g.Volume, source.transform, model.transform, out string what);
                string refusal = volume.Refusal();
                if (refusal != null) { message = L.Tr("'{0}' cannot be used: {1}", source.name, refusal); return; }
                document.EndCoalescing(); // 前の欄のドラッグにまとめない
                ApplyFilterSettings(filterId, effect.Settings.WithGenerator(g.WithVolume(volume)));
                document.EndCoalescing();
                message = L.Tr("Copied the shape of '{0}' ({1}) relative to the model root. The scene was not changed.", source.name, what);
                done = true;
            });
            return done;
        }
    }
}
