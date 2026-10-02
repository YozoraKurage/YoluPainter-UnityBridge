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
        Vector2 MaterialControlPoint(string id, float fx = .5f)
        {
            Repaint(window); Repaint(window);
            Assert.That(window.MaterialControlScreenRects.TryGetValue(id, out var screen), Is.True, id + " was not drawn");
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
                // [ToggleUI] _UseNormalMap（0 → 1、キーワードなし）
                window.MaterialFilter = "_UseNormalMap";
                ClickMaterialControl("material.prop._UseNormalMap", .05f);
                var toggle = window.MaterialEdits.Find(material, "_UseNormalMap");
                Assert.That(toggle, Is.Not.Null, "the click changed the preview value (" + window.StatusMessage + ")");
                Assert.That((toggle.value.x, toggle.keyword), Is.EqualTo((1f, (string)null)));
                Assert.That(material.GetFloat("_UseNormalMap"), Is.Zero);
                // スライダー（_PreviewLit: Range 0–1、既定 1）を 3 割の所までドラッグ
                window.MaterialFilter = "_PreviewLit";
                var a = MaterialControlPoint("material.prop._PreviewLit", .6f); var b = MaterialControlPoint("material.prop._PreviewLit", .3f);
                HostMouse(EventType.MouseDown, a); HostMouse(EventType.MouseDrag, Vector2.Lerp(a, b, .5f)); HostMouse(EventType.MouseDrag, b); HostMouse(EventType.MouseUp, b); Repaint(window);
                var slider = window.MaterialEdits.Find(material, "_PreviewLit");
                Assert.That(slider, Is.Not.Null); Assert.That(slider.value.x, Is.EqualTo(.3f).Within(.05f));
                Assert.That(material.GetFloat("_PreviewLit"), Is.EqualTo(1), "the material itself is unchanged");
                // マテリアルの表示に切り替え（シェーダーが使えれば、複製に値が入っている）
                ClickMaterialControl("material.shading");
                Assert.That(window.Shading, Is.EqualTo(PreviewShading.Material));
                Repaint(window);
                if (window.Preview.DisplayMaterial(0) is Material copy)
                { Assert.That(copy.GetFloat("_PreviewLit"), Is.EqualTo(slider.value.x)); Assert.That(copy.GetFloat("_UseNormalMap"), Is.EqualTo(1)); }
                else Assert.That(window.Preview.MaterialReason(0), Is.Not.Null, "shown neutral, with a reason (the shaders of this editor are broken)");
                // 1 つだけ元に戻す
                ClickMaterialControl("material.revert._PreviewLit");
                Assert.That(window.MaterialEdits.Find(material, "_PreviewLit"), Is.Null);
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
