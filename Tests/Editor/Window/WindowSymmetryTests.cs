using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>3D ビューのシンメトリーを本物のウィンドウで（GUI モード、SendEvent）: 左右対称の箱を正面（カメラは対称の面の上）から見て
    /// 片側をクリックすると反対側の対応するテクセルにも同じ濃さが入り、1 回の Undo で両側が戻る。対称の面の近くでは重なりを二重に
    /// 塗らない。見出しの切り替えで切ると片側だけ。映した側が見えない・別のスロット・予算超えなら塗らず（予算はストロークごと取り消す）、
    /// Esc で両側とも残らない。設定はブラシの状態として保存する。</summary>
    public sealed partial class WindowTests
    {
        GameObject symmetricSource; Mesh symmetricMesh; Material[] symmetricMaterials;

        /// <summary>左右対称の箱（<see cref="SymmetricBox"/>）をシーンに置かずに作って読み込み、正面から見る（カメラは x = 0 の面の上）。</summary>
        void LoadSymmetricBox(int leftCells = 1, int leftSlot = 0, float yaw = 0)
        {
            symmetricMesh = SymmetricBox.Mesh(SymmetricBox.Triangles(1, leftCells, leftSlot));
            symmetricSource = new GameObject("Symmetric box source") { hideFlags = HideFlags.HideAndDontSave };
            symmetricSource.AddComponent<MeshFilter>().sharedMesh = symmetricMesh;
            var shader = Shader.Find("Hidden/YoluPainter/PreviewSurface");
            symmetricMaterials = Enumerable.Range(0, symmetricMesh.subMeshCount).Select(i => new Material(shader) { hideFlags = HideFlags.HideAndDontSave }).ToArray();
            symmetricSource.AddComponent<MeshRenderer>().sharedMaterials = symmetricMaterials;
            var report = window.Preview.Load(symmetricSource);
            Assert.That(report.CanPaint, Is.True, string.Join("; ", report.Diagnostics));
            window.View = TexturePaintWindow.ViewMode.Model;
            Repaint(window);
            window.Preview.ViewFrom(yaw, 0);
            var b = window.Brush; b.radius = 24; b.hardness = .5f; b.flow = 1; b.opacity = 1; b.spacing = .1f; b.pressureSize = false; b.pressureOpacity = false; b.pressureFlow = false;
            b.color = new Color(.9f, .2f, .1f, 1); window.Brush = b;
            Repaint(window);
        }

        [TearDown] public void DestroySymmetricBox()
        {
            if (symmetricSource != null) Object.DestroyImmediate(symmetricSource);
            if (symmetricMesh != null) Object.DestroyImmediate(symmetricMesh);
            if (symmetricMaterials != null) foreach (var m in symmetricMaterials) Object.DestroyImmediate(m);
            symmetricSource = null; symmetricMesh = null; symmetricMaterials = null;
        }

        void Click(Vector2 at) { Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at); }
        Vector2 SurfacePoint(float dx, float dy) { Repaint(window); return window.SurfaceRect.center + new Vector2(dx, dy); }
        static byte AlphaAt(byte[] rgba, int width, int x, int y) => rgba[(y * width + x) * 4 + 3];

        /// <summary>どの画素も、映した画素（W − 1 − x、同じ行）と濃さが ±1 以内で同じ。塗った画素の数を返す。</summary>
        int AssertMirroredCanvas(byte[] rgba, string what)
        {
            int w = window.Document.Width, h = window.Document.Height, painted = 0, worst = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int a = AlphaAt(rgba, w, x, y), m = AlphaAt(rgba, w, w - 1 - x, y);
                if (a > 0) painted++;
                worst = Math.Max(worst, Math.Abs(a - m));
            }
            Assert.That(worst, Is.LessThanOrEqualTo(1), what + ": a texel and its mirror differ by " + worst);
            return painted;
        }
        int PaintedOn(byte[] rgba, bool left)
        {
            int w = window.Document.Width, h = window.Document.Height, count = 0;
            for (int y = 0; y < h; y++) for (int x = left ? 0 : w / 2; x < (left ? w / 2 : w); x++) if (AlphaAt(rgba, w, x, y) > 0) count++;
            return count;
        }

        [Test] public void ASymmetricClickPaintsTheMirroredTexelsAndOneUndoRestoresBothSides()
        {
            LoadSymmetricBox();
            window.Symmetry = true;
            var before = Snapshot();
            Click(SurfacePoint(-60, 12));
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            Assert.That(window.LastMirrorOutcome, Is.EqualTo(MirrorOutcome.Painted), window.StatusMessage);
            var after = Snapshot();
            int painted = AssertMirroredCanvas(after, "one click");
            Assert.That(PaintedOn(after, true), Is.GreaterThan(30)); Assert.That(PaintedOn(after, false), Is.EqualTo(PaintedOn(after, true)).Within(2));
            Assert.That(painted, Is.GreaterThan(60));
            Assert.That(window.Document.UndoCount, Is.EqualTo(1));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(Snapshot(), Is.EqualTo(before), "one undo removes both sides");
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(Snapshot(), Is.EqualTo(after), "redo brings both back");

            // ドラッグのストロークも 1 回の Undo
            Mouse(window, EventType.MouseDown, SurfacePoint(-80, -40));
            Mouse(window, EventType.MouseDrag, SurfacePoint(-60, -30));
            Mouse(window, EventType.MouseDrag, SurfacePoint(-40, -36));
            Mouse(window, EventType.MouseUp, SurfacePoint(-40, -36));
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            AssertMirroredCanvas(Snapshot(), "a dragged stroke");
            Assert.That(window.Document.UndoCount, Is.EqualTo(2));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(Snapshot(), Is.EqualTo(after));
        }

        [Test] public void NearThePlaneTheOverlapIsPaintedOnceNotTwice()
        {
            LoadSymmetricBox();
            var b = window.Brush; b.hardness = 1; b.flow = .5f; window.Brush = b; // 1 回で半分、2 回なら 3/4
            var before = Snapshot();
            Click(SurfacePoint(-8, 0)); var left = Snapshot(); Key(window, KeyCode.Z, EventModifiers.Control);
            Click(SurfacePoint(8, 0)); var right = Snapshot(); Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(Snapshot(), Is.EqualTo(before));
            window.Symmetry = true;
            Click(SurfacePoint(-8, 0)); var both = Snapshot();
            Assert.That(window.LastMirrorOutcome, Is.EqualTo(MirrorOutcome.Painted), window.StatusMessage);
            int w = window.Document.Width, h = window.Document.Height, overlap = 0, single = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int l = AlphaAt(left, w, x, y), r = AlphaAt(right, w, x, y), s = AlphaAt(both, w, x, y);
                Assert.That(Math.Abs(s - Math.Max(l, r)), Is.LessThanOrEqualTo(1), "the larger of the two dabs at " + x + "," + y + " (" + l + ", " + r + ", " + s + ")");
                if (l > 0 && r > 0) overlap++;
                if (l > 0) single = Math.Max(single, l);
            }
            Assert.That(overlap, Is.GreaterThan(30), "the two dabs overlap across the plane");
            Assert.That(single, Is.InRange(120, 135), "one application of a 50% flow");
        }

        [Test] public void TheHeaderToggleTurnsSymmetryOnAndOff()
        {
            LoadSymmetricBox();
            Assert.That(window.Symmetry, Is.False, "off by default");
            ClickHeader("symmetry-header");
            Assert.That(window.Symmetry, Is.True);
            Click(SurfacePoint(-60, 12));
            var first = Snapshot();
            Assert.That(PaintedOn(first, false), Is.GreaterThan(30), "both sides while on");
            ClickHeader("symmetry-header");
            Assert.That(window.Symmetry, Is.False);
            Click(SurfacePoint(-60, -60));
            Assert.That(window.LastMirrorOutcome, Is.Null);
            var second = Snapshot();
            Assert.That(PaintedOn(second, true), Is.GreaterThan(PaintedOn(first, true)), "the new dab landed on the left");
            Assert.That(PaintedOn(second, false), Is.EqualTo(PaintedOn(first, false)), "off: nothing new on the other side");
        }

        void ClickHeader(string id)
        {
            Repaint(window);
            Assert.That(window.ToolControlScreenRects.TryGetValue(id, out var r), Is.True, id + " was not drawn");
            var at = r.center - window.position.position - window.rootVisualElement.worldBound.position;
            Click(at); Repaint(window);
        }

        /// <summary>オプションバーの対称の小さな窓（前はプロパティの欄のブラシの中）で、軸・中心・面を決める。</summary>
        [Test] public void TheSymmetryPopupSetsTheAxisCenterAndPlane()
        {
            LoadSymmetricBox();
            window.SetToolSectionsOpen(true);
            ClickToolControl("symmetry");
            Assert.That(window.Symmetry, Is.True);
            Repaint(window);
            Assert.That(window.Preview.ShownSymmetryPlane.HasValue, Is.True, "the plane is shown while symmetry is on");
            ClickToolControl("symmetry-Y");
            Assert.That(window.SymmetryAxis, Is.EqualTo(SymmetryAxis.Y));
            Repaint(window);
            Assert.That(Vector3.Distance(window.Preview.ShownSymmetryPlane.Value.Normal, Vector3.up), Is.LessThan(1e-6f));
            window.SymmetryAxis = SymmetryAxis.X; window.SymmetryOffset = .25f;
            ClickToolControl("symmetry-origin");
            Assert.That(window.SymmetryOffset, Is.Zero);
            window.SymmetryOffset = .25f;
            ClickToolControl("symmetry-bounds");
            Assert.That(window.SymmetryOffset, Is.EqualTo(window.Preview.Bounds.center.x).Within(1e-6f), "the bounds center of the box is x = 0");
            ClickToolControl("symmetry-plane");
            Assert.That(window.SymmetryPlaneShown, Is.False);
            Repaint(window);
            Assert.That(window.Preview.ShownSymmetryPlane.HasValue, Is.False, "hidden on request");
            ClickToolControl("symmetry");
            Assert.That(window.Symmetry, Is.False);
            Assert.That(window.Document.UndoCount, Is.Zero, "settings are not document edits");
        }

        [Test] public void AMirrorTheCameraCannotSeeIsNotPaintedAndTheStatusSaysSo()
        {
            LoadSymmetricBox(yaw: -90); // +X の側から見る
            window.Symmetry = true;
            Click(SurfacePoint(0, 0));
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            Assert.That(window.LastMirrorOutcome, Is.EqualTo(MirrorOutcome.Hidden));
            Assert.That(window.StatusMessage, Does.Contain("cannot be seen"));
            var after = Snapshot();
            Assert.That(PaintedOn(after, false), Is.GreaterThan(30), "this side");
            Assert.That(PaintedOn(after, true), Is.Zero, "the mirrored side (u below 0.5) stays empty");
            Assert.That(window.Document.UndoCount, Is.EqualTo(1));
        }

        [Test] public void AMirrorOnAnotherTextureSetsSlotIsNotPainted()
        {
            LoadSymmetricBox(leftSlot: 1);
            window.Symmetry = true;
            Click(SurfacePoint(60, 12));
            Assert.That(window.LastMirrorOutcome, Is.EqualTo(MirrorOutcome.OtherSlot), window.StatusMessage);
            Assert.That(window.StatusMessage, Does.Contain("another texture set"));
            var after = Snapshot();
            Assert.That(PaintedOn(after, false), Is.GreaterThan(30)); Assert.That(PaintedOn(after, true), Is.Zero);
        }

        [Test] public void EscapeCancelsBothSidesOfAStroke()
        {
            LoadSymmetricBox();
            window.Symmetry = true;
            var before = Snapshot();
            Mouse(window, EventType.MouseDown, SurfacePoint(-60, 12));
            Mouse(window, EventType.MouseDrag, SurfacePoint(-40, 12));
            Mouse(window, EventType.MouseDrag, SurfacePoint(-20, 20));
            Assert.That(window.IsStroking, Is.True);
            Key(window, KeyCode.Escape);
            Assert.That(window.IsStroking, Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before), "nothing remains on either side");
            Assert.That(window.Document.UndoCount, Is.Zero);
            Assert.That(window.SurfaceVisibilityForTests, Is.Null, "the stroke's caches are gone");
        }

        [Test] public void AMirroredDabOverTheBudgetCancelsTheWholeStroke()
        {
            LoadSymmetricBox(leftCells: 48); // 映した左だけ細かい（面ごとに 4608 枚）
            // 面の上で半径 0.12（箱は 2）: 右の粗い面なら三角形は数枚、左の細かい面では 100 枚を超える
            var b = window.Brush; b.radius = .12f * window.Document.Width / window.Preview.Bounds.size.magnitude; window.Brush = b;
            window.Preview.BrushBudget.MaxTriangles = 64;
            var before = Snapshot();
            Click(SurfacePoint(60, 12));
            Assert.That(window.Document.UndoCount, Is.EqualTo(1), "without symmetry the coarse side fits the budget: " + window.StatusMessage);
            Key(window, KeyCode.Z, EventModifiers.Control);
            window.Symmetry = true;
            Mouse(window, EventType.MouseDown, SurfacePoint(60, 12));
            Assert.That(window.IsStroking, Is.False, "the refused dab cancels the stroke");
            Mouse(window, EventType.MouseUp, SurfacePoint(60, 12));
            Assert.That(window.StatusMessage, Does.Contain("budget"));
            Assert.That(Snapshot(), Is.EqualTo(before), "no partial edit on either side");
            Assert.That(window.Document.UndoCount, Is.Zero);
        }

        [Test] public void AStrokeKeepsThePlaneItStartedWith()
        {
            LoadSymmetricBox();
            window.Symmetry = true;
            Mouse(window, EventType.MouseDown, SurfacePoint(-80, 30));
            Mouse(window, EventType.MouseDrag, SurfacePoint(-60, 30));
            window.Symmetry = false; window.SymmetryOffset = .3f; // 描いている間に変わっても（UI は止まっているが）、このストロークは初めの面のまま
            Mouse(window, EventType.MouseDrag, SurfacePoint(-40, 34));
            Mouse(window, EventType.MouseUp, SurfacePoint(-40, 34));
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            AssertMirroredCanvas(Snapshot(), "the whole stroke mirrored across x = 0");
            Assert.That(window.Document.UndoCount, Is.EqualTo(1));
        }

        [Test] public void SymmetryIsPartOfTheBrushWithoutChangingTheNativeVersion()
        {
            string before = JsonUtility.ToJson(window.Brush);
            window.Symmetry = true; window.SymmetryAxis = SymmetryAxis.Z; window.SymmetryOffset = .125f; window.SymmetryPlaneShown = false;
            Assert.That(JsonUtility.ToJson(window.Brush), Is.Not.EqualTo(before));
            var state = new SerializedObject(window).FindProperty("brush");
            Assert.That(state.FindPropertyRelative("symmetry3D").boolValue, Is.True);
            Assert.That(state.FindPropertyRelative("symmetryAxis").enumValueIndex, Is.EqualTo((int)SymmetryAxis.Z));
            Assert.That(state.FindPropertyRelative("symmetryOffset").floatValue, Is.EqualTo(.125f));
            Assert.That(state.FindPropertyRelative("symmetryAxesShown").boolValue, Is.False);
            window.SymmetryOffset = float.NaN; Assert.That(window.SymmetryOffset, Is.Zero);
            window.SymmetryOffset = 1e9f; Assert.That(window.SymmetryOffset, Is.EqualTo(TexturePaintWindow.MaxSymmetryOffset));
        }
    }
}
