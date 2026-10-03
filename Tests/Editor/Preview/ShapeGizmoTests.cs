using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>形のグラデーションのギズモの計算（<see cref="ShapeGizmo"/>、グラフィックスは要らない）: 正投影の試験用のカメラ（−Z から +Z を
    /// 見る、1 シーン単位 = 100 GUI 点）で、ハンドルの位置と当たり（面の四角が先）、矢印・中央の四角・輪・面の四角のドラッグの量、
    /// カメラを向いた軸は動かさないこと、回したルートの空間に戻すこと、Ctrl の 15° 刻み、大きさの下限、形の線を確かめる。</summary>
    public sealed class ShapeGizmoTests
    {
        /// <summary>正投影: ワールドの (x, y) を GUI の (200 + 100x, 200 − 100y) に。レイは z = −100 から +Z へ。</summary>
        sealed class OrthoView : IGizmoView
        {
            public bool ToGui(Vector3 world, out Vector2 gui) { gui = new Vector2(200 + 100 * world.x, 200 - 100 * world.y); return true; }
            public bool Ray(Vector2 gui, out Ray ray) { ray = new Ray(new Vector3((gui.x - 200) / 100, (200 - gui.y) / 100, -100), Vector3.forward); return true; }
        }
        static readonly IGizmoView View = new OrthoView();
        static readonly ShapeVolume Box = new ShapeVolume(GeneratorShape.Box, .5, .25, 0, 0, 0, 0, 1, 2, 3, .5);
        static Vector2 Gui(Vector3 w) { View.ToGui(w, out var g); return g; }

        [Test] public void HandlesSitWhereTheyShouldAndKnobsWinTheHit()
        {
            Assert.That(ShapeGizmo.WorldPerPoint(View, Vector3.zero), Is.EqualTo(.01f).Within(1e-5f));
            var points = ShapeGizmo.HandlePoints(Box, Vector3.zero, Quaternion.identity, View, ShapeGizmoMode.Move).ToDictionary(p => p.handle, p => p.at);
            Assert.That(points[ShapeHandle.MoveFree], Is.EqualTo(new Vector2(250, 175)));
            Assert.That(Vector2.Distance(points[ShapeHandle.MoveX], new Vector2(250 + ShapeGizmo.ArrowPoints, 175)), Is.LessThan(.01f), "arrows are a fixed length on screen");
            Assert.That(points[ShapeHandle.SizeXPos], Is.EqualTo(Gui(new Vector3(1, .25f, 0)))); Assert.That(points[ShapeHandle.SizeYNeg], Is.EqualTo(Gui(new Vector3(.5f, -.75f, 0))));
            Assert.That(points.Keys, Has.No.Member(ShapeHandle.RotateX), "rings only in the rotate mode");
            Assert.That(ShapeGizmo.Hit(Box, Vector3.zero, Quaternion.identity, View, ShapeGizmoMode.Move, new Vector2(252, 176)), Is.EqualTo(ShapeHandle.MoveFree));
            Assert.That(ShapeGizmo.Hit(Box, Vector3.zero, Quaternion.identity, View, ShapeGizmoMode.Move, new Vector2(290, 177)), Is.EqualTo(ShapeHandle.MoveX));
            Assert.That(ShapeGizmo.Hit(Box, Vector3.zero, Quaternion.identity, View, ShapeGizmoMode.Move, new Vector2(250, 150)), Is.EqualTo(ShapeHandle.MoveY));
            Assert.That(ShapeGizmo.Hit(Box, Vector3.zero, Quaternion.identity, View, ShapeGizmoMode.Move, new Vector2(400, 400)), Is.EqualTo(ShapeHandle.None));
            // 面の四角は矢印の上にあっても先に当たる（+X の面は中心から 50 点、矢印の途中）
            Assert.That(ShapeGizmo.Hit(Box, Vector3.zero, Quaternion.identity, View, ShapeGizmoMode.Move, Gui(new Vector3(1, .25f, 0)) + new Vector2(2, 1)), Is.EqualTo(ShapeHandle.SizeXPos));
            // 輪（正投影で Z の輪は円に見える）
            var rotate = ShapeGizmo.HandlePoints(Box, Vector3.zero, Quaternion.identity, View, ShapeGizmoMode.Rotate).ToDictionary(p => p.handle, p => p.at);
            Assert.That(rotate.Keys, Has.Member(ShapeHandle.RotateZ).And.No.Member(ShapeHandle.MoveX));
            var onRing = new Vector2(250, 175) + new Vector2(Mathf.Cos(1.1f), Mathf.Sin(1.1f)) * ShapeGizmo.RingPoints;
            Assert.That(ShapeGizmo.Hit(Box.WithSize(.2, .2, .2), Vector3.zero, Quaternion.identity, View, ShapeGizmoMode.Rotate, onRing), Is.EqualTo(ShapeHandle.RotateZ));
            // 球は 6 つ、平面は 0 と 1 の境の 2 つ
            Assert.That(ShapeGizmo.Knobs(Box.WithShape(GeneratorShape.Sphere), Vector3.zero, Quaternion.identity).Count, Is.EqualTo(6));
            var plane = ShapeGizmo.Knobs(Box.WithShape(GeneratorShape.Plane), Vector3.zero, Quaternion.identity);
            Assert.That(plane.Select(k => k.handle), Is.EquivalentTo(new[] { ShapeHandle.SizeYPos, ShapeHandle.SizeYNeg }));
        }

        [Test] public void DragsMoveTurnAndSizeByWhatTheMouseDid()
        {
            var root = Vector3.zero; var none = Quaternion.identity;
            // 矢印: 画面で 40 点右 = 0.4。カメラを向いた Z の矢印は動かさない
            var moved = ShapeGizmo.Drag(ShapeHandle.MoveX, Box, root, none, View, new Vector2(300, 175), new Vector2(340, 190));
            Assert.That(moved.CenterX, Is.EqualTo(.9).Within(1e-5)); Assert.That(moved.CenterY, Is.EqualTo(.25).Within(1e-6));
            Assert.That(ShapeGizmo.Drag(ShapeHandle.MoveZ, Box, root, none, View, new Vector2(250, 175), new Vector2(290, 175)), Is.EqualTo(Box), "an axis pointing at the camera");
            var free = ShapeGizmo.Drag(ShapeHandle.MoveFree, Box, root, none, View, new Vector2(250, 175), new Vector2(230, 195));
            Assert.That(free.CenterX, Is.EqualTo(.3).Within(1e-5)); Assert.That(free.CenterY, Is.EqualTo(.05).Within(1e-5)); Assert.That(free.CenterZ, Is.EqualTo(0).Within(1e-5));
            // 輪: Z の輪で 4 分の 1 周（GUI の y は下向きなので、画面で反時計回りは +Z まわりに +90°）
            var c = new Vector2(250, 175);
            var turned = ShapeGizmo.Drag(ShapeHandle.RotateZ, Box, root, none, View, c + new Vector2(50, 0), c + new Vector2(0, -50));
            Assert.That(turned.RotationZ, Is.EqualTo(90).Within(1e-3)); Assert.That(turned.RotationX, Is.EqualTo(0).Within(1e-3));
            var snapped = ShapeGizmo.Drag(ShapeHandle.RotateZ, Box, root, none, View, c + new Vector2(50, 0), c + new Vector2(50, -14), snap: true);
            Assert.That(snapped.RotationZ, Is.EqualTo(15).Within(1e-3), "atan(14/50) ≈ 15.6° snaps to 15°");
            // 面の四角: +X の面を 0.3 外へ → 幅 1.3、中心 +0.15（反対の面はそのまま）。Shift で両側（幅 1.6、中心そのまま）
            var knob = Gui(new Vector3(1, .25f, 0));
            var one = ShapeGizmo.Drag(ShapeHandle.SizeXPos, Box, root, none, View, knob, knob + new Vector2(30, 0));
            Assert.That(one.SizeX, Is.EqualTo(1.3).Within(1e-5)); Assert.That(one.CenterX, Is.EqualTo(.65).Within(1e-5));
            Assert.That(one.CenterX - one.SizeX / 2, Is.EqualTo(Box.CenterX - Box.SizeX / 2).Within(1e-5), "the −X face stays");
            var both = ShapeGizmo.Drag(ShapeHandle.SizeXPos, Box, root, none, View, knob, knob + new Vector2(30, 0), symmetric: true);
            Assert.That(both.SizeX, Is.EqualTo(1.6).Within(1e-5)); Assert.That(both.CenterX, Is.EqualTo(.5).Within(1e-6));
            var negative = ShapeGizmo.Drag(ShapeHandle.SizeYNeg, Box, root, none, View, Gui(new Vector3(.5f, -.75f, 0)), Gui(new Vector3(.5f, -1.25f, 0)));
            Assert.That(negative.SizeY, Is.EqualTo(2.5).Within(1e-5)); Assert.That(negative.CenterY, Is.EqualTo(0).Within(1e-5), "the −Y face moved down by 0.5");
            var tiny = ShapeGizmo.Drag(ShapeHandle.SizeXPos, Box, root, none, View, knob, knob - new Vector2(500, 0), symmetric: true);
            Assert.That(tiny.SizeX, Is.EqualTo(ShapeVolume.MinSize), "never below the minimum size"); Assert.That(tiny.Refusal(), Is.Null);
            // 球: 半径が変わり、中心は動かない。平面: 1 の側の境を動かすと幅が変わる
            var sphere = Box.WithShape(GeneratorShape.Sphere).WithSize(1, 1, 1);
            var bigger = ShapeGizmo.Drag(ShapeHandle.SizeYPos, sphere, root, none, View, Gui(new Vector3(.5f, .75f, 0)), Gui(new Vector3(.5f, 1f, 0)));
            Assert.That(bigger.SizeX, Is.EqualTo(1.5).Within(1e-5)); Assert.That((bigger.CenterX, bigger.CenterY), Is.EqualTo((.5, .25)));
            var plane = Box.WithShape(GeneratorShape.Plane);
            var wider = ShapeGizmo.Drag(ShapeHandle.SizeYPos, plane, root, none, View, Gui(new Vector3(.5f, 1.25f, 0)), Gui(new Vector3(.5f, 1.45f, 0)));
            Assert.That(wider.SizeY, Is.EqualTo(2.2).Within(1e-5)); Assert.That(wider.CenterY, Is.EqualTo(.35).Within(1e-5));
        }

        [Test] public void DragsAreStoredInTheRotatedRootsSpace()
        {
            // ルートが (1, 0, 0) にあり Z まわりに 90° 回っている: ルートの X はワールドの +Y
            var rootPosition = new Vector3(1, 0, 0); var rootRotation = Quaternion.Euler(0, 0, 90);
            var v = new ShapeVolume(GeneratorShape.Box, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0);
            var at = Gui(rootPosition);
            Assert.That(Vector2.Distance(ShapeGizmo.HandlePoints(v, rootPosition, rootRotation, View, ShapeGizmoMode.Move).Single(p => p.handle == ShapeHandle.MoveX).at, at + new Vector2(0, -ShapeGizmo.ArrowPoints)), Is.LessThan(.01f), "the X arrow points up the screen");
            var moved = ShapeGizmo.Drag(ShapeHandle.MoveX, v, rootPosition, rootRotation, View, at + new Vector2(0, -40), at + new Vector2(0, -60));
            Assert.That(moved.CenterX, Is.EqualTo(.2).Within(1e-5), "moved along the root's X"); Assert.That(moved.CenterY, Is.EqualTo(0).Within(1e-5));
            var free = ShapeGizmo.Drag(ShapeHandle.MoveFree, v, rootPosition, rootRotation, View, at, at + new Vector2(30, 0));
            Assert.That(free.CenterY, Is.EqualTo(-.3).Within(1e-5), "world +X is the root's −Y"); Assert.That(free.CenterX, Is.EqualTo(0).Within(1e-5));
            Assert.That(ShapeGizmo.WorldCenter(free, rootPosition, rootRotation).x, Is.EqualTo(1.3f).Within(1e-5f));
            // 形の回転はルートの空間で: ワールドの Z まわりに 90° 回すと、ルートの空間でも Z まわりに 90°（Z は共通）
            var turned = ShapeGizmo.Drag(ShapeHandle.RotateZ, v, rootPosition, rootRotation, View, at + new Vector2(50, 0), at + new Vector2(0, -50));
            Assert.That(turned.RotationZ, Is.EqualTo(90).Within(1e-3));
            Assert.That(Quaternion.Angle(ShapeGizmo.WorldRotation(turned, rootRotation), Quaternion.Euler(0, 0, 180)), Is.LessThan(.01f));
            // ワールドと形の空間の写し（重ね表示に渡す行列）
            var toShape = ShapeGizmo.WorldToShape(new ShapeVolume(GeneratorShape.Box, 0, 1, 0, 0, 0, 0, 1, 1, 1, 0), rootPosition, rootRotation);
            Assert.That(Vector3.Distance(toShape.MultiplyPoint3x4(new Vector3(0, 0, 0)), Vector3.zero), Is.LessThan(1e-5f), "the root's (0, 1, 0) is the world's (0, 0, 0)");
        }

        [Test] public void TheLinesOutlineTheShapeAndItsInnerPart()
        {
            int Count(ShapeVolume v) => ShapeGizmo.Lines(v, Vector3.zero, Quaternion.identity, View, ShapeGizmoMode.Move, ShapeHandle.None).Count;
            int hard = Count(Box.WithFalloff(0)), soft = Count(Box), full = Count(Box.WithFalloff(1));
            Assert.That(soft, Is.EqualTo(hard + 12), "the inner box where the value reaches 1");
            Assert.That(full, Is.EqualTo(hard), "no inner box when the ramp reaches the middle");
            var lines = ShapeGizmo.Lines(Box, Vector3.zero, Quaternion.identity, View, ShapeGizmoMode.Move, ShapeHandle.MoveY);
            Assert.That(lines.Any(l => l.Color == ShapeGizmo.Hover), Is.True, "the hovered arrow is highlighted");
            Assert.That(lines.SelectMany(l => l.Points).All(p => !float.IsNaN(p.x) && !float.IsNaN(p.y)), Is.True);
            Assert.That(Count(Box.WithShape(GeneratorShape.Sphere)), Is.GreaterThan(3)); Assert.That(Count(Box.WithShape(GeneratorShape.Plane)), Is.GreaterThan(12));
        }
    }
}
