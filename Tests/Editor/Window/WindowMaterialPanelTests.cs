using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウのマテリアルの欄を、本物のマウスの入力（SendEvent）で操作する: 切り替えとスライダーはプレビューの値だけを変え、
    /// 元に戻すで戻り、マテリアルの表示の切り替え、「マテリアルに反映…」は確かめてから元に入れる（Unity の Undo で戻る）。この devcontainer の
    /// GUI モードではシェーダーが壊れているので、複製（マテリアルの表示）に入ったかはシェーダーが使えるときだけ見る（batch-gl の
    /// MaterialViewWindowTests が見る）。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void NativeShaderGUIInputChangesTheCopyAndRevertAllRestoresIt()
        {
            var shader = ShaderUtil.CreateShaderAsset("Shader \"Hidden/YoluPainter/Tests/InspectorInput\" { Properties { _Value(\"Value\", Float) = 0 } SubShader { Pass {} } CustomEditor \"Yozolab.YoluPainter.Tests.FailingInspectorGUI\" }", true);
            var material = new Material(shader); var model = new GameObject("Inspector input model") { hideFlags = HideFlags.HideAndDontSave };
            model.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx"); model.AddComponent<MeshRenderer>().sharedMaterial = material;
            try
            {
                FailingInspectorGUI.Fail = false; FailingInspectorGUI.DrawCalls = 0;
                window.SetModel(model); window.DockLayoutForTests.SetActive("material");
                // 欄に高さを渡すため、ほかのまとまりを畳む（既定の配置ではマテリアルはレイヤーと同じまとまりのタブなので、それは畳まない）
                foreach (var id in new[] { "layers", "properties" }) { var g = window.DockLayoutForTests.GroupOf(id); if (g != window.DockLayoutForTests.GroupOf("material")) g.collapsed = true; }
                Repaint(window); Repaint(window);
                Assert.That(window.MaterialInspector, Is.Not.Null);
                Assert.That(window.MaterialInspector.Problem, Is.Null);
                Assert.That(window.MaterialInspector.Editor.isVisible, Is.True);
                Assert.That(window.MaterialInspector.Editor.customShaderGUI, Is.TypeOf<FailingInspectorGUI>());
                Assert.That(FailingInspectorGUI.DrawCalls, Is.GreaterThan(0));
                Assert.That(window.MaterialControlScreenRects["material.inspector"].Contains(FailingInspectorGUI.ValueScreenRect.center), Is.True);
                Undo.IncrementCurrentGroup(); Undo.RecordObject(material, "Source GUI redo sentinel");
                material.SetFloat("_Value", 3); Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
                string undo = PreviewMaterialTests.UndoRecords();
                var rect = FailingInspectorGUI.ValueScreenRect;
                var point = new Vector2(rect.xMax - 20, rect.center.y) - HostScreenPosition(window);
                HostMouse(EventType.MouseDown, point); HostMouse(EventType.MouseUp, point);
                Key(window, KeyCode.A, EventModifiers.Control);
                window.SendEvent(new Event { type = EventType.KeyDown, character = '7' }); Key(window, KeyCode.Return); Repaint(window);
                Assert.That(window.MaterialInspector.Copy.GetFloat("_Value"), Is.EqualTo(7));
                Assert.That(window.MaterialEdits.Find(material, "_Value").value.x, Is.EqualTo(7));
                Assert.That(material.GetFloat("_Value"), Is.Zero);
                Assert.That(PreviewMaterialTests.UndoRecords(), Is.EqualTo(undo));
                ClickMaterialControl("material.revertAll");
                Assert.That(window.MaterialEdits.Count, Is.Zero); Assert.That(window.MaterialInspector.Copy.GetFloat("_Value"), Is.Zero);
                Undo.PerformRedo(); Assert.That(material.GetFloat("_Value"), Is.EqualTo(3));
            }
            finally { window.MaterialEdits.RevertAll(); window.DisposeMaterialInspector(); Object.DestroyImmediate(model); Object.DestroyImmediate(material); Object.DestroyImmediate(shader); }
        }

        Vector2 MaterialControlPoint(string id, float fx = .5f)
        {
            Repaint(window); Repaint(window);
            Assert.That(window.MaterialControlScreenRects.TryGetValue(id, out var screen), Is.True, id + " was not drawn");
            if (id == "material.shading")
            {
                var viewport = window.MaterialControlScreenRects["material.header"];
                for (int i = 0; i < 12 && !viewport.Contains(screen.center); i++)
                {
                    window.SendEvent(new Event { type = EventType.ScrollWheel, mousePosition = viewport.center - HostScreenPosition(window), delta = new Vector2(0, screen.center.y > viewport.center.y ? 3 : -3) });
                    Repaint(window); Repaint(window);
                    screen = window.MaterialControlScreenRects[id];
                }
                Assert.That(viewport.Contains(screen.center), Is.True, id + " is outside the header scroll view");
            }
            var host = new Rect(screen.position - HostScreenPosition(window), screen.size);
            var bounds = new Rect(window.rootVisualElement.worldBound.position, window.position.size);
            Assert.That(bounds.Contains(host.center), Is.True, id + " is outside the window: " + host + " (window " + bounds + ")");
            return new Vector2(host.x + host.width * fx, host.center.y);
        }
        void ClickMaterialControl(string id, float fx = .5f) { var p = MaterialControlPoint(id, fx); HostMouse(EventType.MouseDown, p); HostMouse(EventType.MouseUp, p); Repaint(window); }

        [Test] public void TheMaterialPanelChangesOnlyThePreviewAndAppliesAfterAsking()
        {
            // 元のマテリアルはメモリの上の、パッケージの中立のシェーダーのもの（[ToggleUI] _UseNormalMap と Range の _PreviewLit がある）。
            // Standard は、この devcontainer の GUI モードでは無視の設定の外でシェーダーのエラーをログする。Standard・lilToon・.mat の
            // アセットへの反映と Undo は batch-gl の MaterialViewWindowTests が確かめる
            var material = new Material(Shader.Find("Hidden/YoluPainter/PreviewSurface")) { name = "Panel source" };
            var model = new GameObject("Material panel model") { hideFlags = HideFlags.HideAndDontSave };
            model.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            model.AddComponent<MeshRenderer>().sharedMaterial = material;
            var dialogs = new FakeDialogs { ConfirmAnswer = false }; window.Dialogs = dialogs;
            try
            {
                window.SetModel(model); EditorShaderCompiler.TolerateErrorLogsIfBroken();
                // 欄に場所を空ける: ほかの高さの決まっていないパネルを畳んで、マテリアルの欄を開く
                var layout = window.DockLayoutForTests;
                foreach (var id in new[] { "layers", "properties" }) layout.GroupOf(id).collapsed = true;
                layout.SetActive("material");
                Repaint(window); Repaint(window);
                var inspector = window.MaterialInspector;
                Assert.That(inspector.Editor.target, Is.SameAs(inspector.Copy));
                Assert.That(inspector.Copy, Is.Not.SameAs(material));
                inspector.Inspect(window.MaterialEdits, (editor, properties) =>
                {
                    editor.RegisterPropertyChangeUndo("Inspector preview");
                    properties.Single(p => p.name == "_UseNormalMap").floatValue = 1;
                    properties.Single(p => p.name == "_PreviewLit").floatValue = .3f;
                });
                var slider = window.MaterialEdits.Find(material, "_PreviewLit");
                Assert.That(slider.value.x, Is.EqualTo(.3f));
                Assert.That(material.GetFloat("_PreviewLit"), Is.EqualTo(1));
                Assert.That(material.GetFloat("_UseNormalMap"), Is.Zero);
                // マテリアルの表示に切り替え（シェーダーが使えれば、複製に値が入っている）
                ClickMaterialControl("material.shading");
                Assert.That(window.Shading, Is.EqualTo(PreviewShading.Material));
                Repaint(window);
                if (window.Preview.DisplayMaterial(0) is Material copy)
                { Assert.That(copy.GetFloat("_PreviewLit"), Is.EqualTo(slider.value.x)); Assert.That(copy.GetFloat("_UseNormalMap"), Is.EqualTo(1)); }
                else Assert.That(window.Preview.MaterialReason(0), Is.Not.Null, "shown neutral, with a reason (the shaders of this editor are broken)");
                // 1 つだけ元に戻す
                window.MaterialEdits.Revert(material, "_PreviewLit");
                Repaint(window);
                Assert.That(window.MaterialEdits.Find(material, "_PreviewLit"), Is.Null);
                Assert.That(inspector.Copy.GetFloat("_PreviewLit"), Is.EqualTo(1));
                // 反映: 断れば何もしない、受ければ入って Unity の Undo で戻る
                ClickMaterialControl("material.apply");
                Assert.That(dialogs.Asked, Is.EqualTo(new[] { "Confirm: Apply to the material?" }));
                Assert.That(material.GetFloat("_UseNormalMap"), Is.Zero);
                dialogs.ConfirmAnswer = true;
                ClickMaterialControl("material.apply"); EditorShaderCompiler.TolerateErrorLogsIfBroken();
                Assert.That(material.GetFloat("_UseNormalMap"), Is.EqualTo(1)); Assert.That(material.GetFloat("_PreviewLit"), Is.EqualTo(1), "the reverted value is not applied");
                Assert.That(window.MaterialEdits.Count, Is.Zero);
                Undo.PerformUndo(); EditorShaderCompiler.TolerateErrorLogsIfBroken();
                Assert.That(material.GetFloat("_UseNormalMap"), Is.Zero);
            }
            finally { window.MaterialEdits.RevertAll(); Object.DestroyImmediate(model); Object.DestroyImmediate(material); }
        }
    }
}
