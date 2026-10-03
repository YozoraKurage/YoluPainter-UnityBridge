using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>並べる表示の入れ替えと境目の割合（窓の状態とレイアウト）、2D キャンバスの UV のワイヤーフレーム（辺の作り方とオフスクリーンの描画）。
    /// 窓に入力を送る試験は WindowSplitViewTests.cs（GUI モード）。</summary>
    public sealed class SplitViewTests
    {
        static SurfaceTriangle Tri(Vector2 a, Vector2 b, Vector2 c, int slot = 0)
            => new SurfaceTriangle(new Vector3(a.x, a.y, 0), new Vector3(b.x, b.y, 0), new Vector3(c.x, c.y, 0), a, b, c, 0, slot);

        [Test] public void UvEdgesShareTheEdgesOfAQuadAndKeepSeamsApart()
        {
            Vector2 p00 = new Vector2(0, 0), p10 = new Vector2(.5f, 0), p01 = new Vector2(0, .5f), p11 = new Vector2(.5f, .5f);
            // 対角線を共有する 2 枚: 4 辺 + 対角線 = 5 本（共有の辺は向きが逆でも 1 本）
            var quad = new[] { Tri(p00, p10, p11), Tri(p00, p11, p01) };
            var edges = TexturePaintWindow.UvEdges(quad, 0, 100, out bool truncated);
            Assert.That(truncated, Is.False);
            Assert.That(edges.Length, Is.EqualTo(10), "5 edges, 2 points each");
            // 3D では同じ辺でも UV が別の所にある（継ぎ目）なら別の辺。スロットの違う三角形は入らない。つぶれた辺は描かない
            var seam = Tri(new Vector2(.6f, 0), new Vector2(.6f, .5f), new Vector2(.9f, .5f));
            var other = Tri(p00, p10, p11, slot: 1);
            var degenerate = Tri(new Vector2(.2f, .8f), new Vector2(.2f, .8f), new Vector2(.4f, .9f));
            edges = TexturePaintWindow.UvEdges(quad.Concat(new[] { seam, other, degenerate }).ToArray(), 0, 100, out truncated);
            Assert.That(edges.Length, Is.EqualTo((5 + 3 + 1) * 2), "the seam triangle adds 3, the degenerate one 1 (its other two edges are the same edge), slot 1 adds none");
            Assert.That(TexturePaintWindow.UvEdges(quad, 1, 100, out _), Is.Empty, "no triangle of slot 1 in the quad");
        }

        [Test] public void TooManyUvEdgesAreRefusedRatherThanCut()
        {
            var quad = new[] { Tri(Vector2.zero, Vector2.right, Vector2.one), Tri(Vector2.zero, Vector2.one, Vector2.up) };
            Assert.That(TexturePaintWindow.UvEdges(quad, 0, 4, out bool truncated), Is.Null);
            Assert.That(truncated, Is.True, "5 edges over a budget of 4 draw nothing (a partial wireframe would look like missing UVs)");
            Assert.That(TexturePaintWindow.UvEdges(quad, 0, 5, out truncated).Length, Is.EqualTo(10)); Assert.That(truncated, Is.False);
        }

        /// <summary>入れ替え・境目の割合・UV の表示はウィンドウの状態で、スクリプトのコンパイル（ここでは書き出しと読み戻し）で戻らない。
        /// 割合は 0.15〜0.85 に収める（壊れた値は半分）。</summary>
        [Test] public void TheSplitAndTheWireframeSurviveTheWindowsSerialization()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>(); var copy = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                Assert.That((copy.ViewsSwapped, copy.SplitRatio, copy.ShowUvWireframe), Is.EqualTo((false, .5f, true)), "a new window: 2D on the left, half and half, UVs shown");
                w.ViewsSwapped = true; w.SplitRatio = .3f; w.ShowUvWireframe = false;
                UnityEditor.EditorJsonUtility.FromJsonOverwrite(UnityEditor.EditorJsonUtility.ToJson(w), copy);
                Assert.That((copy.ViewsSwapped, copy.SplitRatio, copy.ShowUvWireframe), Is.EqualTo((true, .3f, false)));
                w.SplitRatio = .01f; Assert.That(w.SplitRatio, Is.EqualTo(.15f));
                w.SplitRatio = 2; Assert.That(w.SplitRatio, Is.EqualTo(.85f));
                w.SplitRatio = float.NaN; Assert.That(w.SplitRatio, Is.EqualTo(.5f));
            }
            finally
            {
                foreach (var x in new[] { w, copy }) { string recovery = x.RecoveryRoot; Object.DestroyImmediate(x); if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
            }
        }

        // ───────── オフスクリーンの描画（batch-gl） ─────────

        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "split-view"));
        static void RequireOffscreen()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon.");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
        }
        static Color32[] Render(TexturePaintWindow w, string name, out int width, out int height)
        {
            width = 1200; height = 800; string path = Path.Combine(Folder, name + ".png");
            OffscreenGui.RenderWindow(w, width, height, path);
            var texture = new Texture2D(2, 2);
            try { Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, name); return texture.GetPixels32(); }
            finally { Object.DestroyImmediate(texture); }
        }

        /// <summary>入れ替えると 3D ビューが左・2D キャンバスが右になり、各ビューの幅は入れ替える前のまま。境目はどちらのビューにも入らない。
        /// 3D ビューの見出しのモデル名は、表示の切り替えと入れ替えのボタンの後から書く。</summary>
        [Test] public void SwappingPutsThe3DViewOnTheLeftAndKeepsEachViewsWidth()
        {
            RequireOffscreen();
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh(); w.View = TexturePaintWindow.ViewMode.Split; w.SplitRatio = .4f;
                Render(w, "split-straight", out _, out _);
                var canvas = w.CanvasRect; var surface = w.SurfaceRect; var handle = w.SplitHandleRect;
                Assert.That(canvas.xMax, Is.LessThanOrEqualTo(handle.x)); Assert.That(handle.xMax, Is.LessThanOrEqualTo(surface.x));
                Assert.That(canvas.width / (canvas.width + surface.width), Is.EqualTo(.4f).Within(.01f));
                w.ViewsSwapped = true;
                Render(w, "split-swapped", out _, out _);
                Assert.That(w.SurfaceRect.xMax, Is.LessThanOrEqualTo(w.SplitHandleRect.x)); Assert.That(w.SplitHandleRect.xMax, Is.LessThanOrEqualTo(w.CanvasRect.x));
                Assert.That((w.CanvasRect.width, w.SurfaceRect.width), Is.EqualTo((canvas.width, surface.width)), "each view keeps its width");
                Assert.That(w.surfaceHeaderLabelForTests.x, Is.GreaterThanOrEqualTo(w.viewModeButtonsEndForTests), "the model name starts after the view buttons");
            }
            finally { Object.DestroyImmediate(w); }
        }

        /// <summary>UV のワイヤーフレームは 2D キャンバスの上だけに描かれ、切れば消える（文書は変わらない）。表示を回しても、辺は回した画像の上に乗る
        /// （ある辺の中点の画素が、切ったときと違う色になる）。</summary>
        [Test] public void TheUvWireframeIsDrawnOnTheCanvasOnlyWhenShown()
        {
            RequireOffscreen();
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh(); w.View = TexturePaintWindow.ViewMode.Canvas;
                long revision = w.Document.Revision;
                foreach (float angle in new[] { 0f, 30f })
                {
                    if (angle != 0) Assert.That(w.RotateCanvasView(angle), Is.True);
                    w.ShowUvWireframe = false; var off = Render(w, "uv-off-" + angle, out int width, out int height);
                    w.ShowUvWireframe = true; var on = Render(w, "uv-on-" + angle, out _, out _);
                    var canvas = w.CanvasRect; int inside = 0, outside = 0;
                    for (int i = 0; i < on.Length; i++)
                    {
                        if (on[i].Equals(off[i])) continue;
                        float gx = i % width, gy = height - 1 - i / width; // PNG は下の行から
                        var at = new Vector2(gx + .5f, gy + .5f);
                        if (canvas.Contains(at)) inside++; else if (!w.uvWireframeToggleForTests.Contains(at)) outside++; // 見出しの切り替えのボタンは変わってよい
                    }
                    Assert.That(inside, Is.GreaterThan(200), angle + "°: the wireframe changes the canvas");
                    Assert.That(outside, Is.Zero, angle + "°: nothing outside the canvas (but the header's toggle) changes");
                    // 辺の中点（画素の座標）の GUI の位置で、色が違う
                    var edges = w.CurrentUvEdges(); Assert.That(edges, Is.Not.Null.And.Not.Empty);
                    var view = w.CanvasViewNow(); int differing = 0, probed = 0;
                    for (int e = 0; e + 1 < edges.Length && probed < 12; e += 2)
                    {
                        var mid = (edges[e] + edges[e + 1]) * .5f;
                        var gui = view.ToGui(mid.x * w.Document.Width, mid.y * w.Document.Height);
                        if (!canvas.Contains(gui)) continue;
                        probed++; int px = Mathf.FloorToInt(gui.x), py = height - 1 - Mathf.FloorToInt(gui.y);
                        bool near = false;
                        for (int dy = -1; dy <= 1 && !near; dy++) for (int dx = -1; dx <= 1 && !near; dx++)
                        { int k = (py + dy) * width + px + dx; near = k >= 0 && k < on.Length && !on[k].Equals(off[k]); }
                        if (near) differing++;
                    }
                    Assert.That(probed, Is.GreaterThan(3), angle + "°: some edge midpoints are on the canvas");
                    Assert.That(differing, Is.EqualTo(probed), angle + "°: every probed edge midpoint is drawn where the view maps it");
                }
                Assert.That(w.Document.Revision, Is.EqualTo(revision), "the wireframe is not part of the document");
            }
            finally { Object.DestroyImmediate(w); }
        }
    }
}
