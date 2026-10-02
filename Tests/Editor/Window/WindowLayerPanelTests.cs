using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウのプロパティの欄（レイヤー・レイヤーマスク・フィルター・ノーマル・メッシュマップ・ポーズ）を、本物のマウスの入力
    /// （SendEvent）で操作する: マスクに描く・濃度のドラッグが 1 回の Undo・反転・削除、フィルターの無効・選択・強さのドラッグ・並べ替え・削除、
    /// Height → Normal と強さのドラッグ、平らな値、メッシュマップの「ベイク…」がベイクの窓を開くこと、BlendShape のスライダーを離した
    /// ときに 1 回だけ焼き直すこと。欄は部品が見える所までスクロールして押す。どの欄も LegacySection（Unity の標準の部品）を通らない。</summary>
    public sealed partial class WindowTests
    {
        static readonly string[] LayerPanelSections = { "layer", "mask", "filters", "normal", "mesh-maps", "pose" };

        void OpenLayerPanels()
        {
            var open = (Dictionary<string, bool>)typeof(TexturePaintWindow).GetField("sectionOpen", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            foreach (var key in LayerPanelSections) open[key] = true;
            Repaint(window);
        }

        /// <summary>
        /// プロパティの欄の部品 id を見える所までスクロールし、その中の点を SendEvent の座標（タブを含むホストの座標）で返す。fx は左から
        /// 何割の所か。部品が覚える画面の座標は GUIClip を外してホストの画面の位置を足したものなので、ホストの画面の位置
        /// （EditorWindow の m_Parent の screenPosition、内部）を引けば、SendEvent がそのまま受け取る座標になる（タブの高さの扱いを推測しない）。
        /// </summary>
        Vector2 LayerControlPoint(string id, float fx = .5f)
        {
            Repaint(window);
            Assert.That(window.LayerControlPanelRects.TryGetValue(id, out var inPanel), Is.True, id + " was not drawn");
            typeof(TexturePaintWindow).GetField("propertiesScroll", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, new Vector2(0, Mathf.Max(0, inPanel.y - 40)));
            Repaint(window); Repaint(window);
            var screen = window.LayerControlScreenRects[id];
            var host = new Rect(screen.position - HostScreenPosition(window), screen.size);
            var bounds = new Rect(window.rootVisualElement.worldBound.position, window.position.size);
            Assert.That(bounds.Contains(host.center), Is.True, id + " is outside the window: " + host + " (window " + bounds + ")");
            return new Vector2(host.x + host.width * fx, host.center.y);
        }
        static Vector2 HostScreenPosition(EditorWindow w)
        {
            var parent = typeof(EditorWindow).GetField("m_Parent", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(w);
            return ((Rect)parent.GetType().GetProperty("screenPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(parent)).position;
        }
        /// <summary>ホストの座標でマウスの入力を送る（<see cref="Mouse"/> はウィンドウの GUI の座標を受け取ってタブの高さを足す）。</summary>
        void HostMouse(EventType type, Vector2 host)
        { EditorShaderCompiler.TolerateErrorLogsIfBroken(); window.SendEvent(new Event { type = type, mousePosition = host, button = 0, pressure = 1 }); }
        void ClickLayerControl(string id, float fx = .5f) { var p = LayerControlPoint(id, fx); HostMouse(EventType.MouseDown, p); HostMouse(EventType.MouseUp, p); Repaint(window); }
        /// <summary>スライダーを from から to（左から何割）へドラッグして離す。</summary>
        void DragLayerControl(string id, float from, float to)
        {
            var a = LayerControlPoint(id, from); var b = new Vector2(a.x + (to - from) * window.LayerControlScreenRects[id].width, a.y);
            HostMouse(EventType.MouseDown, a); HostMouse(EventType.MouseDrag, Vector2.Lerp(a, b, .5f)); HostMouse(EventType.MouseDrag, b); HostMouse(EventType.MouseUp, b);
            Repaint(window);
        }

        [Test] public void TheLayerPanelsDrawWithoutLegacySections()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer); d.AddLayerMask(layer.Id);
            window.AddFilter(FilterTarget.Content, FilterSettings.GaussianBlur(3));
            window.Channel = PaintChannel.Normal;
            OpenLayerPanels(); Repaint(window);
            var legacy = (Dictionary<string, float>)typeof(TexturePaintWindow).GetField("legacyHeights", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            Assert.That(legacy.Keys.Intersect(LayerPanelSections), Is.Empty, "a layer or channel section went through LegacySection (Unity's standard controls)");
            foreach (var id in new[] { "layer.channel", "mask.paint", "mask.density", "normal.derive", "meshmap.bake" })
                Assert.That(window.LayerControlScreenRects.ContainsKey(id), Is.True, id + " was not drawn with the kit");
        }

        [Test] public void TheMaskSectionPaintsOnTheMaskAndADensityDragIsOneUndoStep()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            OpenLayerPanels();
            ClickLayerControl("mask.add");
            Assert.That(layer.Mask, Is.Not.Null, window.StatusMessage); Assert.That(window.EditMask, Is.True, "a new mask is the one being painted");
            ClickLayerControl("mask.paint");
            Assert.That(window.EditMask, Is.False);
            int steps = d.UndoCount;
            DragLayerControl("mask.density", .95f, .5f);
            Assert.That(layer.Mask.Density, Is.EqualTo(.5).Within(.03)); Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "one drag, one undo step");
            ClickLayerControl("mask.invert");
            Assert.That(layer.Mask.Inverted, Is.True);
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(layer.Mask.Inverted, Is.False);
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(layer.Mask.Density, Is.EqualTo(1), "undo restores the density before the drag");
            ClickLayerControl("mask.delete");
            Assert.That(layer.Mask, Is.Null);
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(layer.Mask, Is.Not.Null);
        }

        [Test] public void FilterRowsTurnOffSelectReorderAndRemoveWithTheirButtons()
        {
            var d = window.Document; var layer = RedSquare();
            var blur = window.AddFilter(FilterTarget.Content, FilterSettings.GaussianBlur(4)); var invert = window.AddFilter(FilterTarget.Content, FilterSettings.Invert());
            window.SelectedFilter = Guid.Empty; OpenLayerPanels();
            ClickLayerControl("filter." + blur.Id + ".eye");
            Assert.That(layer.Filters.Single(f => f.Id == blur.Id).Enabled, Is.False);
            ClickLayerControl("filter." + blur.Id + ".name");
            Assert.That(window.SelectedFilter, Is.EqualTo(blur.Id), "the name opens the settings");
            int steps = d.UndoCount;
            DragLayerControl("filter.strength", .95f, .5f);
            Assert.That(layer.Filters.Single(f => f.Id == blur.Id).Strength, Is.EqualTo(.5).Within(.03)); Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            ClickLayerControl("filter." + invert.Id + ".down");
            Assert.That(layer.Filters.First().Id, Is.EqualTo(invert.Id), "down applies it earlier");
            ClickLayerControl("filter." + blur.Id + ".remove");
            Assert.That(layer.Filters.Select(f => f.Id), Is.EqualTo(new[] { invert.Id }));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(layer.Filters.Count, Is.EqualTo(2));
        }

        [Test] public void NormalAndMeshMapControlsWorkThroughTheirWidgets()
        {
            var d = window.Document;
            window.Channel = PaintChannel.Normal; OpenLayerPanels();
            int steps = d.UndoCount;
            ClickLayerControl("normal.derive");
            Assert.That(d.NormalSettings.DeriveFromHeight, Is.True); Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            Assert.That(window.StatusMessage, Does.Contain("Height → Normal on"));
            DragLayerControl("normal.strength", .52f, .75f);
            Assert.That(d.NormalSettings.Strength, Is.GreaterThan(NormalSettings.Default.Strength)); Assert.That(d.UndoCount, Is.EqualTo(steps + 2), "the strength drag is one step");
            window.SetBrushNormal(1, 0);
            ClickLayerControl("normal.flat");
            Assert.That(window.GetBrush().Color, Is.EqualTo(new Rgba32(128, 128, 255)));
            // メッシュマップ: 欄の「ベイク…」はモデルが無くても押せて、ベイクの窓を開く（窓は焼けない理由を出す）。焼くマップのチェックと
            // 設定は窓の側（WindowMeshMapTests・MeshBakeWindowTests）
            window.Channel = PaintChannel.Color; Repaint(window);
            ClickLayerControl("meshmap.bake");
            var bake = MeshBakeWindow.For(window);
            try
            {
                Assert.That(bake, Is.Not.Null, "Bake… opens the bake window");
                Assert.That(window.MeshBakeRefusal(), Does.Contain("Load a model"));
                Assert.That(window.MeshMaps.Count, Is.Zero); Assert.That(window.IsBakingMeshMaps, Is.False);
            }
            finally { if (bake != null) bake.Close(); }
        }

        [Test] public void ABlendShapeSliderReappliesThePoseOnceWhenReleased()
        {
            var root = new GameObject("WindowPanelPoseSource") { hideFlags = HideFlags.HideAndDontSave };
            var bone = new GameObject("bone") { hideFlags = HideFlags.HideAndDontSave }; bone.transform.SetParent(root.transform, false);
            var mesh = new Mesh { vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) }, uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }, triangles = new[] { 0, 2, 1, 1, 2, 3 } };
            mesh.boneWeights = new BoneWeight[4].Select4(new BoneWeight { boneIndex0 = 0, weight0 = 1 }); mesh.bindposes = new[] { Matrix4x4.identity };
            var delta = new Vector3[4]; delta[0] = new Vector3(0, 0, -.5f); mesh.AddBlendShapeFrame("dent", 100, delta, null, null); mesh.RecalculateNormals();
            var skin = root.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; skin.bones = new[] { bone.transform }; skin.rootBone = bone.transform;
            try
            {
                Assert.That(window.Preview.Load(root).CanPaint, Is.True);
                OpenLayerPanels();
                var shape = window.Preview.BlendShapes[0]; string id = "pose.shape." + shape.Label;
                int revision = window.Preview.SnapshotRevision;
                var a = LayerControlPoint(id, .05f); var b = new Vector2(a.x + .8f * window.LayerControlScreenRects[id].width, a.y);
                HostMouse(EventType.MouseDown, a); HostMouse(EventType.MouseDrag, b); Repaint(window);
                Assert.That(window.Preview.GetBlendShapeWeight(shape), Is.GreaterThan(50));
                Assert.That(window.Preview.SnapshotRevision, Is.EqualTo(revision), "not re-baked while the slider is held");
                HostMouse(EventType.MouseUp, b); Repaint(window); Repaint(window);
                Assert.That(window.Preview.SnapshotRevision, Is.EqualTo(revision + 1), "re-baked once on release: " + window.StatusMessage);
                Assert.That(skin.GetBlendShapeWeight(0), Is.Zero, "the model itself is unchanged");
                ClickLayerControl("pose.reset"); Repaint(window);
                Assert.That(window.Preview.GetBlendShapeWeight(shape), Is.Zero);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }
    }
}
