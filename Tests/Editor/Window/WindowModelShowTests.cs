using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 描画ウィンドウ: 3D ビューで見せるものをキーで切り替える（C: チャンネルを順に、Shift+B: メッシュマップを順に、Shift+C: マテリアル）。
    /// B は筆のまま。窓が使ったキーとしてショートカットのガードに知らせる。ストロークの最中は断って知らせ、見出しにボタンがある。
    /// 見せるマテリアルの選びも、ストロークの最中は断り、文書の Undo では戻らない。
    /// </summary>
    public sealed partial class WindowTests
    {
        [Test] public void TheShowDropdownStaysAtThe3DViewsLeftAndNamesUnavailableMaps()
        {
            window.Preview.LoadDemoMesh();
            window.View = TexturePaintWindow.ViewMode.Model; Repaint(window);
            Assert.That(window.modelShowButtonForTests.x, Is.GreaterThanOrEqualTo(window.SurfaceRect.x));
            Assert.That(window.modelShowButtonForTests.x, Is.LessThan(window.SurfaceRect.center.x));
            var items = window.ModelShowChoices();
            var maps = items.Where(x => x.path.StartsWith("Mesh Map/")).ToList();
            Assert.That(maps.Count, Is.EqualTo(System.Enum.GetValues(typeof(MeshMapKind)).Length));
            Assert.That(maps.All(x => x.reason != null && x.path.Contains("not baked")), Is.True);
            Assert.That(window.ModelShowMenu().Build().Count(x => x.Label.Contains("not baked") && !x.Enabled), Is.EqualTo(maps.Count), "unavailable maps stay visible even when none are baked");
            Assert.That(items.Single(x => x.path.StartsWith("Channel/Layer mask")).reason, Does.Contain("no mask"));
            items.Single(x => x.path == "Channel/Roughness (not used)").choose();
            Assert.That(window.ModelShowName(), Is.EqualTo("Roughness"));
            window.MeshMaps.Put(new[] { TestMeshMaps.Make(MeshMapKind.AmbientOcclusion, window.Document.Width, window.Document.Height, (x, y, c) => .5) });
            var ao = window.ModelShowChoices().Single(x => x.path == "Mesh Map/Ambient occlusion");
            Assert.That(ao.reason, Is.Null); ao.choose(); Assert.That(window.ModelShowName(), Is.EqualTo("AO (mesh map)"));
            window.ShowMaterialIn3D(); window.View = TexturePaintWindow.ViewMode.Split; Repaint(window); BeginLine(200, 200);
            Assert.That(window.ModelShowChoices().All(x => x.reason != null && x.reason.Contains("Finish the stroke")), Is.True);
            window.ModelShowChoices().First().choose(); Assert.That(window.IsStroking, Is.True);
            Key(window, KeyCode.Escape);
            window.View = TexturePaintWindow.ViewMode.Split;
            foreach (bool swap in new[] { false, true })
            {
                window.ViewsSwapped = swap; Repaint(window);
                float expected = Mathf.Max(window.SurfaceRect.x + 8, window.viewModeButtonsEndForTests + 8);
                Assert.That(window.modelShowButtonForTests.x, Is.EqualTo(expected).Within(.1f));
                Assert.That(window.modelShowButtonForTests.xMax, Is.LessThanOrEqualTo(window.SurfaceRect.xMax));
            }
            var button = window.modelShowButtonForTests;
            Mouse(window, EventType.MouseDown, button.center); Mouse(window, EventType.MouseUp, button.center);
            Assert.That(PaintMenuSession.Current, Is.Not.Null);
            var popup = PaintMenuSession.Current.Windows[0];
            int roughness = popup.Nodes.FindIndex(x => x.Label.StartsWith("Roughness"));
            Assert.That(roughness, Is.GreaterThanOrEqualTo(0));
            int steps = popup.Nodes.Take(roughness + 1).Count(x => x.Enabled && !x.Heading && !x.Separator);
            for (int i = 0; i < steps; i++) popup.Key(new Event { type = EventType.KeyDown, keyCode = KeyCode.DownArrow });
            popup.Key(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return });
            Assert.That(window.ModelShowName(), Is.EqualTo("Roughness")); Assert.That(PaintMenuSession.Current, Is.Null);
        }

        [Test] public void TheShowKeysSwitchWhatThe3DViewShowsAndWaitForTheStroke()
        {
            window.Preview.LoadDemoMesh();
            var d = window.Document;
            d.AddFillLayer("Rough", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Roughness, new Rgba32(200, 200, 200) } });
            Repaint(window);
            var used = YlpContent.UsedChannels(d);
            Assert.That(used, Has.Member(PaintChannel.Roughness));
            var scope = (IPainterShortcutScope)window;

            Key(window, KeyCode.C);
            Assert.That((window.ModelShow, window.ModelShowChannel), Is.EqualTo((TexturePaintWindow.ModelShowKind.Channel, used[0])));
            Assert.That(scope.TookKey(KeyCode.C, EventModifiers.None), Is.True, "the guard is told the painter used C");
            for (int i = 1; i < used.Count; i++) Key(window, KeyCode.C);
            Assert.That(window.ModelShowChannel, Is.EqualTo(used[used.Count - 1]));
            Key(window, KeyCode.C);
            Assert.That(window.ModelShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.Material), "after the last channel comes the material again");
            Repaint(window);
            Assert.That(window.Preview.ShowsUnlit, Is.False);

            Key(window, KeyCode.B, EventModifiers.Shift);
            Assert.That(window.ModelShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.Material)); Assert.That(window.StatusMessage, Does.Contain("No mesh maps"));
            window.MeshMaps.Put(new[] { TestMeshMaps.Make(MeshMapKind.AmbientOcclusion, d.Width, d.Height, (x, y, c) => .5) });
            Key(window, KeyCode.B, EventModifiers.Shift);
            Assert.That((window.ModelShow, window.ModelShowMap), Is.EqualTo((TexturePaintWindow.ModelShowKind.MeshMap, MeshMapKind.AmbientOcclusion)));
            Repaint(window);
            Assert.That(window.Preview.ShowsUnlit, Is.True, "the unlit view is passed to the 3D view");
            Key(window, KeyCode.C, EventModifiers.Shift);
            Assert.That(window.ModelShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.Material));
            Key(window, KeyCode.C);
            window.Tool = TexturePaintWindow.PaintTool.Eyedropper; Key(window, KeyCode.B);
            Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.Brush), "B stays the brush");
            Assert.That(window.ModelShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.Channel), "and leaves what the 3D view shows");

            // ストロークの最中は断って知らせる（キーは窓が使う）。描いているストロークはそのまま
            BeginLine(300, 300);
            Key(window, KeyCode.C, EventModifiers.Shift);
            Assert.That(window.IsStroking, Is.True); Assert.That(window.ModelShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.Channel));
            Assert.That(window.StatusMessage, Does.Contain("Finish the stroke first"));
            Key(window, KeyCode.Escape);
            Assert.That(window.IsStroking, Is.False);
            Key(window, KeyCode.C, EventModifiers.Shift);
            Assert.That(window.ModelShow, Is.EqualTo(TexturePaintWindow.ModelShowKind.Material));

            // 見出しのボタン
            Repaint(window);
            Assert.That(window.modelShowButtonForTests.width, Is.GreaterThan(0));
            Assert.That(window.modelShowButtonForTests.xMax, Is.LessThanOrEqualTo(window.SurfaceRect.xMax));
        }

        [Test] public void CtrlRightDragTurnsTheLightingAndEscapePutsItBack()
        {
            window.Preview.LoadDemoMesh(); Repaint(window);
            var s = window.PreviewScene; float yaw = s.lightYaw, environment = s.environmentRotation, camera = window.Preview.CameraYaw;
            var at = window.SurfaceRect.center + window.rootVisualElement.worldBound.position;
            void Send(EventType type, Vector2 delta = default) => window.SendEvent(new Event { type = type, mousePosition = at, delta = delta, button = 1, modifiers = EventModifiers.Control });
            Send(EventType.MouseDown); Assert.That(window.IsRotatingLighting, Is.True);
            Send(EventType.MouseDrag, new Vector2(40, 0)); Send(EventType.MouseUp);
            Assert.That(window.IsRotatingLighting, Is.False);
            Assert.That(Mathf.DeltaAngle(environment + 20, s.environmentRotation), Is.EqualTo(0).Within(1e-3), "40 pixels turn the environment by 20°");
            Assert.That(Mathf.DeltaAngle(yaw + 20, s.lightYaw), Is.EqualTo(0).Within(1e-3), "and the light with it");
            Assert.That(window.Preview.CameraYaw, Is.EqualTo(camera), "the camera does not orbit");
            Assert.That(window.Preview.Scene, Is.SameAs(s));
            float turnedYaw = s.lightYaw, turnedEnvironment = s.environmentRotation;
            Send(EventType.MouseDown); Send(EventType.MouseDrag, new Vector2(-60, 0));
            Key(window, KeyCode.Escape);
            Assert.That(window.IsRotatingLighting, Is.False);
            Assert.That((s.lightYaw, s.environmentRotation), Is.EqualTo((turnedYaw, turnedEnvironment)), "Esc puts the lighting back where the drag started");
            // 右ドラッグだけ（Ctrl なし）は今までどおりカメラを回す
            window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = at, button = 1 });
            window.SendEvent(new Event { type = EventType.MouseDrag, mousePosition = at, delta = new Vector2(30, 0), button = 1 });
            window.SendEvent(new Event { type = EventType.MouseUp, mousePosition = at, button = 1 });
            Assert.That(window.Preview.CameraYaw, Is.Not.EqualTo(camera)); Assert.That(s.lightYaw, Is.EqualTo(turnedYaw));
        }

        [Test] public void ThePreviewMaterialWaitsForTheStrokeAndIsNotPartOfTheDocumentUndo()
        {
            string folder = "Assets/ZZ_WindowModelShowTests-" + System.Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            try
            {
                AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), folder + "/Chosen.mat"); AssetDatabase.SaveAssets();
                var chosen = AssetDatabase.LoadAssetAtPath<Material>(folder + "/Chosen.mat");
                window.Preview.LoadDemoMesh(); Repaint(window);
                BeginLine(200, 200);
                Assert.That(window.UsePreviewMaterial(chosen), Is.False); Assert.That(window.StatusMessage, Does.Contain("Finish the stroke first"));
                Assert.That(window.SetChannelRoute(PaintChannel.Color, ""), Is.False);
                Assert.That(window.MaterialChoice(), Is.Null);
                Mouse(window, EventType.MouseUp, At(window, 260, 200));
                Assert.That(window.Document.CanUndo, Is.True);
                Assert.That(window.UsePreviewMaterial(chosen), Is.True, window.StatusMessage);
                Key(window, KeyCode.Z, EventModifiers.Control);
                Assert.That(window.Document.CanUndo, Is.False, "the stroke is undone");
                Assert.That(window.MaterialChoice().source, Is.EqualTo(PreviewMaterialSource.Material), "the choice is view state, not undone with the document");
                Repaint(window);
                Assert.That(window.Preview.ViewMaterial(0), Is.SameAs(chosen));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
