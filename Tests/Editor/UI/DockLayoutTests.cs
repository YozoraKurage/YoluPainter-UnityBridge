using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>パネルの配置（左右の列・順・畳む・高さの比・列の幅）。ウィンドウは表示しない。</summary>
    public sealed class DockLayoutTests
    {
        [Test] public void ABrokenOrOldLayoutIsRepaired()
        {
            var layout = new DockLayout { left = new System.Collections.Generic.List<string> { "layers", "unknown", "layers" }, right = null, collapsed = new System.Collections.Generic.List<string> { "x", "color", "color" }, leftWidth = 5, rightWidth = float.NaN, weightIds = new System.Collections.Generic.List<string> { "layers" }, weights = new System.Collections.Generic.List<float>() }.Normalized();
            Assert.That(layout.left, Is.EqualTo(new[] { "layers" }));
            Assert.That(layout.right, Is.EquivalentTo(new[] { "color", "textureSet", "properties" }), "missing panels come back on the right");
            Assert.That(layout.collapsed, Is.EqualTo(new[] { "color" }));
            Assert.That(layout.leftWidth, Is.EqualTo(DockLayout.MinWidth)); Assert.That(layout.rightWidth, Is.EqualTo(300));
            Assert.That(layout.weightIds, Is.Empty, "mismatched weights are dropped");
        }

        [Test] public void PanelsMoveWithinAndBetweenColumnsAndRoundTripAsJson()
        {
            var layout = DockLayout.Default();
            layout.Move("color", false, 4); // 右の列の一番下へ（抜いた分を詰める）
            Assert.That(layout.right, Is.EqualTo(new[] { "textureSet", "layers", "properties", "color" }));
            layout.Move("layers", true, 0);
            Assert.That(layout.left, Is.EqualTo(new[] { "layers" })); Assert.That(layout.right, Is.EqualTo(new[] { "textureSet", "properties", "color" }));
            layout.Move("properties", true, 0);
            Assert.That(layout.left, Is.EqualTo(new[] { "properties", "layers" }));
            layout.SetCollapsed("color", true); layout.SetWeight("layers", 3); layout.leftWidth = 280;
            var back = JsonUtility.FromJson<DockLayout>(JsonUtility.ToJson(layout)).Normalized();
            Assert.That(back.left, Is.EqualTo(layout.left)); Assert.That(back.right, Is.EqualTo(layout.right));
            Assert.That(back.IsCollapsed("color"), Is.True); Assert.That(back.Weight("layers"), Is.EqualTo(3)); Assert.That(back.leftWidth, Is.EqualTo(280));
            Assert.That(() => layout.Move("nope", true, 0), Throws.ArgumentException);
        }

        [Test] public void APanelOnTheLeftOpensALeftDockAndNarrowsTheView()
        {
            Assert.That(DockLayoutStore.UseDefaults, Is.True, "tests use the default layout");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.LayoutOverride = new Rect(0, 0, 1600, 950);
                var layout = w.DockLayoutForTests;
                typeof(TexturePaintWindow).GetMethod("LayoutShell", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(w, null);
                float before = w.SurfaceRect.xMax - 44; // 表示域の右端 - ツールの帯
                layout.Move("layers", true, 0); layout.Move("properties", true, 1);
                typeof(TexturePaintWindow).GetMethod("LayoutShell", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(w, null);
                Assert.That(w.SurfaceRect.width + w.SurfaceRect.x, Is.LessThanOrEqualTo(1600 - 300 + .5f));
                if (Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    w.Preview.LoadDemoMesh();
                    string path = Path.Combine(Path.GetFullPath("Logs"), "YoluPainterSnapshots", "dock-left.png");
                    OffscreenGui.RenderWindow(w, 1600, 950, path);
                    Assert.That(File.Exists(path), Is.True);
                }
            }
            finally { Object.DestroyImmediate(w); }
        }
    }
}
