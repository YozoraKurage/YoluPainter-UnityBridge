using System;
using System.Collections.Generic;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>How the gizmo projects: model-preview space to GUI points and back (the preview's camera; tests use their own).</summary>
    public interface IGizmoView
    {
        /// <summary>Where a point of the preview's space is seen (GUI points), or false behind the camera.</summary>
        bool ToGui(Vector3 world, out Vector2 gui);
        /// <summary>The camera ray through a GUI point.</summary>
        bool Ray(Vector2 gui, out Ray ray);
    }

    /// <summary>What the gizmo's drags change: Move shows the move arrows and the free-move square, Rotate the rings. The size
    /// knobs on the shape's faces are shown in both.</summary>
    public enum ShapeGizmoMode { Move = 0, Rotate = 1 }

    /// <summary>What the 3D view overlays for a shape gradient (<see cref="IsolatedModelPreview.ShownShapeGradient"/>): the shape in
    /// the preview's space, its value's parameters (the same as <see cref="ShapeVolume"/>, then the generator's levels and invert)
    /// and the material slot whose faces get the tint.</summary>
    public readonly struct ShapeGradientOverlay
    {
        public readonly Matrix4x4 WorldToShape; public readonly GeneratorShape Shape; public readonly Vector3 Half;
        public readonly float Band, InverseWidth, Low, High, Softness; public readonly bool Invert; public readonly int Slot; public readonly Color Tint;

        ShapeGradientOverlay(Matrix4x4 worldToShape, GeneratorShape shape, Vector3 half, float band, float inverseWidth, float low, float high, float softness, bool invert, int slot, Color tint)
        {
            WorldToShape = worldToShape; Shape = shape; Half = half; Band = band; InverseWidth = inverseWidth; Low = low; High = high; Softness = softness; Invert = invert; Slot = slot; Tint = tint;
        }

        /// <summary>The overlay of a shape gradient's settings placed with the model root's pose.</summary>
        public static ShapeGradientOverlay Of(GeneratorSettings g, Vector3 rootPosition, Quaternion rootRotation, int slot, Color tint)
        {
            var v = g.Volume; var half = new Vector3((float)v.SizeX / 2, (float)v.SizeY / 2, (float)v.SizeZ / 2);
            float band = v.Shape == GeneratorShape.Sphere ? (float)v.Falloff * half.x : (float)v.Falloff * Mathf.Min(half.x, Mathf.Min(half.y, half.z));
            return new ShapeGradientOverlay(ShapeGizmo.WorldToShape(v, rootPosition, rootRotation), v.Shape, half, band, (float)(1 / v.SizeY),
                (float)g.Low, (float)g.High, (float)g.Softness, g.Invert, slot, tint);
        }

        internal void Apply(Material material)
        {
            material.SetMatrix("_ShapeFromWorld", WorldToShape);
            material.SetVector("_ShapeHalf", new Vector4(Half.x, Half.y, Half.z, (int)Shape));
            material.SetVector("_ShapeParams", new Vector4(Band, InverseWidth, 0, 0));
            material.SetVector("_ShapeLevels", new Vector4(Low, High, Softness, Invert ? 1 : 0));
            material.SetVector("_Tint", new Vector4(Tint.r, Tint.g, Tint.b, Tint.a));
        }
    }

    /// <summary>A part of the shape gizmo that can be dragged.</summary>
    public enum ShapeHandle { None, MoveX, MoveY, MoveZ, MoveFree, RotateX, RotateY, RotateZ, SizeXPos, SizeXNeg, SizeYPos, SizeYNeg, SizeZPos, SizeZNeg }

    /// <summary>One polyline of the gizmo in GUI points, with its colour and width (drawn by the window with Handles.DrawAAPolyLine),
    /// or a filled convex polygon (arrow heads; Handles.DrawAAConvexPolygon).</summary>
    public readonly struct GizmoLine
    {
        public readonly Vector3[] Points; public readonly Color Color; public readonly float Width; public readonly bool Filled;
        public GizmoLine(Vector3[] points, Color color, float width, bool filled = false) { Points = points; Color = color; Width = width; Filled = filled; }
    }

    /// <summary>
    /// The 3D gizmo of a shape gradient (<see cref="ShapeVolume"/>): its outline, handles, hit tests and drags, in the preview's
    /// space. The volume is in the model root's space (root position and rotation, scene units), so the gizmo places it with the
    /// root's pose. Its own handles (not Unity's Handles.PositionHandle and friends): those need SceneView-style Layout passes,
    /// Camera.current and handle shaders, while this view is drawn by a PreviewRenderUtility into GUI rectangles; here everything is
    /// projected with the preview's camera and drawn as GUI polylines.
    /// <list type="bullet">
    /// <item>Move arrows go along the model root's axes; dragging moves the centre along that axis (closest point between the mouse
    /// ray and the axis line). The square at the centre moves it in the plane facing the camera.</item>
    /// <item>Rings turn the shape about the model root's axes through its centre (the angle between where the mouse ray meets the
    /// ring's plane at the start and now; Ctrl snaps to 15°). A ring seen edge-on turns by the mouse movement along it instead.</item>
    /// <item>Size knobs sit on the shape's faces along its own axes. Dragging one moves that face and keeps the opposite one (the
    /// centre follows); with Shift both faces move and the centre stays. A sphere's knobs change its radius (centre fixed); a
    /// plane has knobs only at its 0 and 1 boundaries, which change the ramp's width.</item>
    /// </list>
    /// Every drag is computed from the volume and mouse position where it started, so it does not drift.
    /// </summary>
    public static class ShapeGizmo
    {
        /// <summary>Lengths on screen (GUI points): move arrows, ring radius, how near a handle a press grabs it, knob and centre square sizes.</summary>
        public const float ArrowPoints = 90, RingPoints = 75, GrabPoints = 7, KnobPoints = 10, CenterPoints = 13;
        public const float SnapDegrees = 15;
        public static readonly Color AxisX = new Color(.96f, .3f, .25f), AxisY = new Color(.45f, .88f, .25f), AxisZ = new Color(.25f, .52f, 1f);
        public static readonly Color Hover = new Color(1f, .85f, .2f), Outline = new Color(1f, .62f, .2f, .95f), Inner = new Color(1f, .62f, .2f, .45f);

        public static Quaternion Rotation(ShapeVolume v) => Quaternion.Euler((float)v.RotationX, (float)v.RotationY, (float)v.RotationZ);
        public static Vector3 WorldCenter(ShapeVolume v, Vector3 rootPosition, Quaternion rootRotation) => rootPosition + rootRotation * new Vector3((float)v.CenterX, (float)v.CenterY, (float)v.CenterZ);
        public static Quaternion WorldRotation(ShapeVolume v, Quaternion rootRotation) => rootRotation * Rotation(v);
        /// <summary>Preview space → the shape's space (rotation and translation only; sizes stay in scene units).</summary>
        public static Matrix4x4 WorldToShape(ShapeVolume v, Vector3 rootPosition, Quaternion rootRotation)
            => Matrix4x4.TRS(WorldCenter(v, rootPosition, rootRotation), WorldRotation(v, rootRotation), Vector3.one).inverse;
        static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;
        static Color AxisColor(int i) => i == 0 ? AxisX : i == 1 ? AxisY : AxisZ;

        /// <summary>The scene length of one GUI point at that point (0 when it cannot be seen).</summary>
        public static float WorldPerPoint(IGizmoView view, Vector3 at)
        {
            if (!view.ToGui(at, out var g) || !view.Ray(g, out var r0) || !view.Ray(g + new Vector2(10, 0), out var r1)) return 0;
            var plane = new Plane(r0.direction, at);
            return plane.Raycast(r1, out float t) ? Vector3.Distance(r1.GetPoint(t), at) / 10 : 0;
        }

        // ───────── 部品の位置 ─────────

        /// <summary>The handles shown in the mode, with the GUI point that stands for each (knob and square centres, arrow tips).</summary>
        public static List<(ShapeHandle handle, Vector2 at)> HandlePoints(ShapeVolume v, Vector3 rootPosition, Quaternion rootRotation, IGizmoView view, ShapeGizmoMode mode)
        {
            var list = new List<(ShapeHandle, Vector2)>();
            var c = WorldCenter(v, rootPosition, rootRotation); float unit = WorldPerPoint(view, c);
            if (unit <= 0 || !view.ToGui(c, out var cg)) return list;
            foreach (var (handle, world) in VisibleKnobs(v, rootPosition, rootRotation, view)) if (view.ToGui(world, out var g)) list.Add((handle, g));
            if (mode == ShapeGizmoMode.Move)
            {
                list.Add((ShapeHandle.MoveFree, cg));
                for (int i = 0; i < 3; i++) if (ArrowVisible(view, c, rootRotation * Axis(i)) && view.ToGui(c + rootRotation * Axis(i) * ArrowPoints * unit, out var tip)) list.Add((ShapeHandle.MoveX + i, tip));
            }
            else
                for (int i = 0; i < 3; i++)
                {
                    var ring = Ring(view, c, rootRotation * Axis(i), RingPoints * unit, 48);
                    if (ring.Count > 0) list.Add((ShapeHandle.RotateX + i, ring[0]));
                }
            return list;
        }

        /// <summary>The size knobs in the preview's space: a box's six face centres, a sphere's six points on its surface along its
        /// axes, a plane's two boundaries (0 behind, 1 in front).</summary>
        public static List<(ShapeHandle handle, Vector3 world)> Knobs(ShapeVolume v, Vector3 rootPosition, Quaternion rootRotation)
        {
            var c = WorldCenter(v, rootPosition, rootRotation); var q = WorldRotation(v, rootRotation); var list = new List<(ShapeHandle, Vector3)>();
            float[] half = { (float)v.SizeX / 2, (float)v.SizeY / 2, (float)v.SizeZ / 2 };
            for (int i = 0; i < 3; i++)
            {
                if (v.Shape == GeneratorShape.Plane && i != 1) continue;
                float h = v.Shape == GeneratorShape.Sphere ? half[0] : half[i];
                list.Add((ShapeHandle.SizeXPos + 2 * i, c + q * Axis(i) * h));
                list.Add((ShapeHandle.SizeXNeg + 2 * i, c - q * Axis(i) * h));
            }
            return list;
        }

        /// <summary>The knobs whose axis does not point at the camera (those would sit on the centre and could not be dragged).</summary>
        static IEnumerable<(ShapeHandle handle, Vector3 world)> VisibleKnobs(ShapeVolume v, Vector3 rootPosition, Quaternion rootRotation, IGizmoView view)
        {
            var q = WorldRotation(v, rootRotation);
            foreach (var (handle, world) in Knobs(v, rootPosition, rootRotation))
            {
                var axis = q * Axis((handle - ShapeHandle.SizeXPos) / 2);
                if (!view.ToGui(world, out var g) || !view.Ray(g, out var ray) || Mathf.Abs(Vector3.Dot(axis, ray.direction)) > FacingCamera) continue;
                yield return (handle, world);
            }
        }
        /// <summary>|cos| between an axis and the view ray above which a handle along it is not shown (about 14°).</summary>
        const float FacingCamera = .97f;
        static bool ArrowVisible(IGizmoView view, Vector3 c, Vector3 axis)
            => view.ToGui(c, out var g) && view.Ray(g, out var ray) && Mathf.Abs(Vector3.Dot(axis, ray.direction)) <= FacingCamera;

        /// <summary>The handle under the GUI point, or None: size knobs first, then (Move) the centre square and the arrows or
        /// (Rotate) the rings; the nearest within <see cref="GrabPoints"/>.</summary>
        public static ShapeHandle Hit(ShapeVolume v, Vector3 rootPosition, Quaternion rootRotation, IGizmoView view, ShapeGizmoMode mode, Vector2 mouse)
        {
            var c = WorldCenter(v, rootPosition, rootRotation); float unit = WorldPerPoint(view, c);
            if (unit <= 0 || !view.ToGui(c, out var cg)) return ShapeHandle.None;
            ShapeHandle best = ShapeHandle.None; float bestDistance = float.MaxValue;
            foreach (var (handle, world) in VisibleKnobs(v, rootPosition, rootRotation, view))
                if (view.ToGui(world, out var g)) { float d = Vector2.Distance(g, mouse); if (d <= KnobPoints / 2 + 2 && d < bestDistance) { best = handle; bestDistance = d; } }
            if (best != ShapeHandle.None) return best;
            if (mode == ShapeGizmoMode.Move)
            {
                if (Mathf.Abs(mouse.x - cg.x) <= CenterPoints / 2 + 2 && Mathf.Abs(mouse.y - cg.y) <= CenterPoints / 2 + 2) return ShapeHandle.MoveFree;
                for (int i = 0; i < 3; i++)
                {
                    if (!ArrowVisible(view, c, rootRotation * Axis(i)) || !view.ToGui(c + rootRotation * Axis(i) * ArrowPoints * unit, out var tip)) continue;
                    float d = DistanceToSegment(mouse, cg, tip);
                    if (d <= GrabPoints && Vector2.Distance(mouse, cg) > CenterPoints / 2 && d < bestDistance) { best = ShapeHandle.MoveX + i; bestDistance = d; }
                }
            }
            else
                for (int i = 0; i < 3; i++)
                {
                    var ring = Ring(view, c, rootRotation * Axis(i), RingPoints * unit, 64);
                    for (int k = 0; k + 1 < ring.Count; k++)
                    {
                        float d = DistanceToSegment(mouse, ring[k], ring[k + 1]);
                        if (d <= GrabPoints && d < bestDistance) { best = ShapeHandle.RotateX + i; bestDistance = d; }
                    }
                }
            return best;
        }

        // ───────── ドラッグ ─────────

        /// <summary>The volume after dragging the handle from one GUI point to another (both from the start of the drag). symmetric:
        /// size knobs move both faces (Shift). snap: rings turn in <see cref="SnapDegrees"/> steps (Ctrl). The result is not checked
        /// against the volume's limits beyond keeping sizes in range; a drag the view cannot resolve (an axis pointing at the
        /// camera) leaves the volume as it was.</summary>
        public static ShapeVolume Drag(ShapeHandle handle, ShapeVolume start, Vector3 rootPosition, Quaternion rootRotation, IGizmoView view, Vector2 from, Vector2 to, bool symmetric = false, bool snap = false)
        {
            var c0 = WorldCenter(start, rootPosition, rootRotation); var q0 = WorldRotation(start, rootRotation);
            switch (handle)
            {
                case ShapeHandle.MoveX: case ShapeHandle.MoveY: case ShapeHandle.MoveZ:
                {
                    var a = rootRotation * Axis(handle - ShapeHandle.MoveX);
                    if (!AxisDelta(view, c0, a, from, to, out float delta)) return start;
                    return WithWorldCenter(start, c0 + a * delta, rootPosition, rootRotation);
                }
                case ShapeHandle.MoveFree:
                {
                    if (!view.Ray(from, out var r0) || !view.Ray(to, out var r1)) return start;
                    var plane = new Plane(-r0.direction, c0);
                    if (!plane.Raycast(r0, out float t0) || !plane.Raycast(r1, out float t1)) return start;
                    return WithWorldCenter(start, c0 + (r1.GetPoint(t1) - r0.GetPoint(t0)), rootPosition, rootRotation);
                }
                case ShapeHandle.RotateX: case ShapeHandle.RotateY: case ShapeHandle.RotateZ:
                {
                    var a = rootRotation * Axis(handle - ShapeHandle.RotateX);
                    float angle = RingAngle(view, c0, a, RingPoints * WorldPerPoint(view, c0), from, to);
                    if (snap) angle = Mathf.Round(angle / SnapDegrees) * SnapDegrees;
                    if (angle == 0) return start;
                    var local = Quaternion.Inverse(rootRotation) * (Quaternion.AngleAxis(angle, a) * q0);
                    var e = local.eulerAngles;
                    return start.WithRotation(Signed(e.x), Signed(e.y), Signed(e.z));
                }
                case ShapeHandle.None: return start;
                default:
                {
                    int axis = (handle - ShapeHandle.SizeXPos) / 2; float sign = (handle - ShapeHandle.SizeXPos) % 2 == 0 ? 1 : -1;
                    var b = q0 * Axis(axis);
                    if (!AxisDelta(view, c0, b, from, to, out float delta)) return start;
                    double outward = sign * delta;
                    switch (start.Shape)
                    {
                        case GeneratorShape.Sphere:
                        {
                            double d = Clamp(start.SizeX + 2 * outward);
                            return start.WithSize(d, start.SizeY, start.SizeZ);
                        }
                        default:
                        {
                            double[] size = { start.SizeX, start.SizeY, start.SizeZ };
                            double next = Clamp(size[axis] + (symmetric ? 2 * outward : outward)), grown = next - size[axis];
                            size[axis] = next;
                            var sized = start.WithSize(size[0], size[1], size[2]);
                            if (symmetric) return sized;
                            return WithWorldCenter(sized, c0 + b * (float)(sign * grown / 2), rootPosition, rootRotation); // 反対の面は動かない
                        }
                    }
                }
            }
        }
        static double Clamp(double size) => size < ShapeVolume.MinSize ? ShapeVolume.MinSize : size > ShapeVolume.MaxSize ? ShapeVolume.MaxSize : size;
        static double Signed(float degrees) => degrees > 180 ? degrees - 360 : degrees;
        static ShapeVolume WithWorldCenter(ShapeVolume v, Vector3 world, Vector3 rootPosition, Quaternion rootRotation)
        {
            var local = Quaternion.Inverse(rootRotation) * (world - rootPosition);
            return v.WithCenter(local.x, local.y, local.z);
        }

        /// <summary>How far along the line through c with unit direction a the mouse moved (the closest points of the start and the
        /// current mouse rays to the line). False when the line points nearly at the camera.</summary>
        static bool AxisDelta(IGizmoView view, Vector3 c, Vector3 a, Vector2 from, Vector2 to, out float delta)
        {
            delta = 0;
            if (!view.Ray(from, out var r0) || !view.Ray(to, out var r1)) return false;
            if (!LineParameter(r0, c, a, out float s0) || !LineParameter(r1, c, a, out float s1)) return false;
            delta = s1 - s0; return true;
        }
        static bool LineParameter(Ray ray, Vector3 c, Vector3 a, out float s)
        {
            var w = c - ray.origin; float b = Vector3.Dot(a, ray.direction), denominator = 1 - b * b;
            s = 0;
            if (denominator < 1e-3f) return false;
            s = (b * Vector3.Dot(ray.direction, w) - Vector3.Dot(a, w)) / denominator;
            return true;
        }
        /// <summary>The angle (degrees, about a) the mouse turned the ring: between the points where the start and current rays meet
        /// the ring's plane; for a ring seen nearly edge-on, the mouse movement along the ring's tangent on screen.</summary>
        static float RingAngle(IGizmoView view, Vector3 c, Vector3 a, float radius, Vector2 from, Vector2 to)
        {
            if (!view.Ray(from, out var r0) || !view.Ray(to, out var r1)) return 0;
            var plane = new Plane(a, c);
            if (Mathf.Abs(Vector3.Dot(r0.direction, a)) > .2f && plane.Raycast(r0, out float t0) && plane.Raycast(r1, out float t1))
            {
                Vector3 v0 = r0.GetPoint(t0) - c, v1 = r1.GetPoint(t1) - c;
                if (v0.sqrMagnitude > 1e-12f && v1.sqrMagnitude > 1e-12f) return Vector3.SignedAngle(v0, v1, a);
                return 0;
            }
            // 横から見た輪: 押した所にいちばん近い輪の点の、画面の上の接線に沿って動いた量
            var ring = Ring(view, c, a, radius, 64);
            if (ring.Count < 2 || radius <= 0) return 0;
            int nearest = 0; float best = float.MaxValue;
            for (int k = 0; k < ring.Count; k++) { float d = Vector2.Distance(ring[k], from); if (d < best) { best = d; nearest = k; } }
            var tangent = ring[Mathf.Min(nearest + 1, ring.Count - 1)] - ring[Mathf.Max(nearest - 1, 0)];
            if (tangent.sqrMagnitude < 1e-6f) return 0;
            float along = Vector2.Dot(to - from, tangent.normalized);
            return along / RingPoints * Mathf.Rad2Deg;
        }

        // ───────── 描く線 ─────────

        /// <summary>A circle in the plane through c perpendicular to a (unit), in GUI points (closed; the points behind the camera
        /// are left out).</summary>
        public static List<Vector2> Ring(IGizmoView view, Vector3 c, Vector3 a, float radius, int segments)
        {
            var list = new List<Vector2>(segments + 1);
            var u = Vector3.Cross(a, Mathf.Abs(a.y) < .9f ? Vector3.up : Vector3.right).normalized; var w = Vector3.Cross(a, u);
            for (int k = 0; k <= segments; k++)
            {
                float t = k * Mathf.PI * 2 / segments;
                if (view.ToGui(c + (u * Mathf.Cos(t) + w * Mathf.Sin(t)) * radius, out var g)) list.Add(g);
            }
            return list;
        }

        /// <summary>The lines to draw: the shape's outline (and, with a falloff, the inner shape where the value reaches 1, fainter),
        /// a plane's two boundaries and the arrow toward 1, then the handles of the mode with the hovered one highlighted.</summary>
        public static List<GizmoLine> Lines(ShapeVolume v, Vector3 rootPosition, Quaternion rootRotation, IGizmoView view, ShapeGizmoMode mode, ShapeHandle hover)
        {
            var lines = new List<GizmoLine>();
            var c = WorldCenter(v, rootPosition, rootRotation); var q = WorldRotation(v, rootRotation); float unit = WorldPerPoint(view, c);
            void Polyline(IList<Vector3> world, Color color, float width)
            {
                // カメラの後ろの点で線を切る
                var part = new List<Vector3>();
                foreach (var p in world)
                {
                    if (view.ToGui(p, out var g)) { part.Add(g); continue; }
                    if (part.Count > 1) lines.Add(new GizmoLine(part.ToArray(), color, width));
                    part.Clear();
                }
                if (part.Count > 1) lines.Add(new GizmoLine(part.ToArray(), color, width));
            }
            void Segment(Vector3 a, Vector3 b, Color color, float width)
            {
                var pts = new Vector3[9]; for (int k = 0; k < pts.Length; k++) pts[k] = Vector3.Lerp(a, b, k / 8f);
                Polyline(pts, color, width);
            }
            void Box(Vector3 half, Color color, float width)
            {
                for (int i = 0; i < 3; i++)
                {
                    int j = (i + 1) % 3, k = (i + 2) % 3;
                    foreach (float sj in new[] { -1f, 1f }) foreach (float sk in new[] { -1f, 1f })
                    {
                        var o = Axis(j) * half[j] * sj + Axis(k) * half[k] * sk;
                        Segment(c + q * (o - Axis(i) * half[i]), c + q * (o + Axis(i) * half[i]), color, width);
                    }
                }
            }
            void Circle(Vector3 axis, float radius, Color color, float width)
            {
                var u = Vector3.Cross(axis, Mathf.Abs(axis.y) < .9f ? Vector3.up : Vector3.right).normalized; var w = Vector3.Cross(axis, u);
                var pts = new Vector3[65]; for (int k = 0; k < pts.Length; k++) { float t = k * Mathf.PI * 2 / 64; pts[k] = c + (u * Mathf.Cos(t) + w * Mathf.Sin(t)) * radius; }
                Polyline(pts, color, width);
            }
            float hx = (float)v.SizeX / 2, hy = (float)v.SizeY / 2, hz = (float)v.SizeZ / 2;
            switch (v.Shape)
            {
                case GeneratorShape.Box:
                {
                    Box(new Vector3(hx, hy, hz), Outline, 2);
                    float band = (float)v.Falloff * Mathf.Min(hx, Mathf.Min(hy, hz));
                    if (band > 0 && hx - band > 0 && hy - band > 0 && hz - band > 0) Box(new Vector3(hx - band, hy - band, hz - band), Inner, 1);
                    break;
                }
                case GeneratorShape.Sphere:
                {
                    for (int i = 0; i < 3; i++) Circle(q * Axis(i), hx, Outline, i == 1 ? 2 : 1.5f);
                    if (view.ToGui(c, out var cg) && view.Ray(cg, out var toCenter)) Circle(toCenter.direction, hx, Outline, 2); // 外形
                    float inner = hx - (float)v.Falloff * hx;
                    if (inner > 0 && v.Falloff > 0 && view.ToGui(c, out cg) && view.Ray(cg, out toCenter)) Circle(toCenter.direction, inner, Inner, 1);
                    break;
                }
                default:
                {
                    var face = new[] { new Vector3(-hx, 0, -hz), new Vector3(hx, 0, -hz), new Vector3(hx, 0, hz), new Vector3(-hx, 0, hz), new Vector3(-hx, 0, -hz) };
                    var mid = new List<Vector3>(); var back = new List<Vector3>(); var front = new List<Vector3>();
                    foreach (var p in face) { mid.Add(c + q * p); back.Add(c + q * (p - Vector3.up * hy)); front.Add(c + q * (p + Vector3.up * hy)); }
                    for (int k = 0; k < 4; k++) { Segment(mid[k], mid[k + 1], Outline, 2); Segment(back[k], back[k + 1], Inner, 1); Segment(front[k], front[k + 1], Inner, 1); }
                    var normal = q * Vector3.up;
                    Segment(c, c + normal * hy, Outline, 2);
                    if (unit > 0) { var side = q * Vector3.right * 6 * unit; Segment(c + normal * hy, c + normal * hy * .8f + side, Outline, 2); Segment(c + normal * hy, c + normal * hy * .8f - side, Outline, 2); }
                    break;
                }
            }
            if (unit <= 0) return lines;
            if (mode == ShapeGizmoMode.Move)
            {
                Vector3 toCamera = view.ToGui(c, out var cg0) && view.Ray(cg0, out var r0) ? -r0.direction : Vector3.back;
                for (int i = 0; i < 3; i++)
                {
                    var a = rootRotation * Axis(i); var tip = c + a * ArrowPoints * unit; bool hot = hover == ShapeHandle.MoveX + i; var color = hot ? Hover : AxisColor(i);
                    if (!ArrowVisible(view, c, a)) continue;
                    Segment(c, tip - a * 12 * unit, color, hot ? 4.5f : 3f);
                    // 矢じり: 画面を向いた三角形（塗る）
                    var side = Vector3.Cross(a, toCamera).normalized * 5.5f * unit;
                    if (view.ToGui(tip, out var t0) && view.ToGui(tip - a * 15 * unit + side, out var t1) && view.ToGui(tip - a * 15 * unit - side, out var t2))
                        lines.Add(new GizmoLine(new Vector3[] { t0, t1, t2 }, color, 0, true));
                }
            }
            else
                for (int i = 0; i < 3; i++)
                {
                    var ring = Ring(view, c, rootRotation * Axis(i), RingPoints * unit, 64);
                    if (ring.Count > 1) lines.Add(new GizmoLine(ring.ConvertAll(p => (Vector3)p).ToArray(), hover == ShapeHandle.RotateX + i ? Hover : AxisColor(i), hover == ShapeHandle.RotateX + i ? 4.5f : 3f));
                }
            return lines;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a; float l = ab.sqrMagnitude;
            float t = l > 0 ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l) : 0;
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
