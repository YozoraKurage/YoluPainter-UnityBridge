using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>モデルに結び付いたプロジェクト（Substance Painter の New Project と Project Configuration と同じ考え方）。ウィンドウは表示
    /// せずに作る（OnEnable は走る）ので batch-gl でも動く。モデルは一時のプレハブ（Assets の下の一時フォルダ）か、シーンに置かない
    /// 一時のオブジェクト。</summary>
    public sealed class ProjectSetupTests
    {
        string folder, project; TexturePaintWindow window; GameObject sceneObject;

        [SetUp] public void Create()
        {
            project = Path.Combine(Path.GetTempPath(), "yolupainter-project-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(project);
            PainterSettings.ProjectRoot = project;
            string name = "ProjectSetupTests-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", name), Is.Not.Empty); folder = "Assets/" + name;
            window = ScriptableObject.CreateInstance<TexturePaintWindow>();
        }
        [TearDown] public void Clean()
        {
            if (window != null) UnityEngine.Object.DestroyImmediate(window);
            if (sceneObject != null) UnityEngine.Object.DestroyImmediate(sceneObject);
            AssetDatabase.DeleteAsset(folder); PainterSettings.ProjectRoot = null;
            if (Directory.Exists(project)) Directory.Delete(project, true);
        }

        /// <summary>UV が 0–1 に収まる板の一時のプレハブ（描けるモデル）。マテリアルは名前付きのもの。</summary>
        GameObject PrefabModel(string name)
        {
            var mesh = new Mesh { name = name + "Mesh", vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up }, uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }, triangles = new[] { 0, 2, 1, 0, 3, 2 } };
            mesh.RecalculateNormals(); mesh.RecalculateTangents();
            AssetDatabase.CreateAsset(mesh, folder + "/" + name + "Mesh.asset");
            var material = new Material(Shader.Find("Unlit/Texture")) { name = "Skin" };
            AssetDatabase.CreateAsset(material, folder + "/Skin.mat");
            var go = new GameObject(name); go.AddComponent<MeshFilter>().sharedMesh = mesh; go.AddComponent<MeshRenderer>().sharedMaterial = material;
            try { return PrefabUtility.SaveAsPrefabAsset(go, folder + "/" + name + ".prefab"); }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test] public void ANewProjectUsesTheTemplateChannelsResolutionNormalFormatAndModel()
        {
            var model = PrefabModel("Board");
            window.CreateProject(new NewProjectSettings { Template = ProjectTemplate.LilToon, Model = model, Materials = new[] { 7 }, Resolution = 512, NormalFormat = NormalYDirection.DirectX });
            var d = window.Document;
            Assert.That((d.Width, d.Height), Is.EqualTo((512, 512)));
            Assert.That(d.Layers.Count, Is.EqualTo(1));
            var first = d.Layers[0];
            Assert.That(Enum.GetValues(typeof(PaintChannel)).Cast<PaintChannel>().Where(first.IsChannelEnabled), Is.EquivalentTo(NewProjectSettings.Channels(ProjectTemplate.LilToon)));
            Assert.That(d.NormalSettings.FileDirection, Is.EqualTo(NormalYDirection.DirectX));
            Assert.That(d.CanUndo, Is.False, "a fresh project has no history");
            Assert.That(window.Preview.HasModel, Is.True); Assert.That(window.Preview.CanPaint, Is.True, string.Join("\n", window.Preview.Diagnostics));
            Assert.That(window.Channel, Is.EqualTo(PaintChannel.Color));
            // 保存先の提案はモデルの隣、モデル名_マテリアル名（スロットは今のモデルに合わせて詰める）
            var suggestion = ((string folder, string name))typeof(TexturePaintWindow).GetMethod("SaveSuggestion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(window, null);
            Assert.That(suggestion.folder, Is.EqualTo(Path.GetFullPath(folder)));
            Assert.That(suggestion.name, Is.EqualTo("Board_Skin"));
        }

        [Test] public void AProjectWithoutAModelPaintsIn2DAndEveryTemplateHasColor()
        {
            foreach (ProjectTemplate template in Enum.GetValues(typeof(ProjectTemplate)))
            {
                window.CreateProject(new NewProjectSettings { Template = template, Model = null, Resolution = 1024 });
                Assert.That(window.Preview.HasModel, Is.False);
                Assert.That(window.Document.Layers[0].IsChannelEnabled(PaintChannel.Color), Is.True, template.ToString());
            }
            Assert.That(() => window.CreateProject(new NewProjectSettings { Resolution = 1000 }), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => window.CreateProject(null), Throws.ArgumentNullException);
            Assert.That(window.Document.Width, Is.EqualTo(1024), "a refused project leaves the open one");
        }

        [Test] public void TheConfigurationChangesModelAndNormalFormatButNotTheCanvas()
        {
            window.CreateProject(new NewProjectSettings { Resolution = 1024 });
            var d = window.Document; var layer = d.Layers[0]; layer.GetChannel(PaintChannel.Color).SetPixel(5, 5, new Rgba32(1, 2, 3)); d.ClearHistory();
            var model = PrefabModel("Panel");
            window.ApplyProjectConfiguration(new NewProjectSettings { Model = model, NormalFormat = NormalYDirection.DirectX, Resolution = 2048 });
            Assert.That(window.Document, Is.SameAs(d)); Assert.That(d.Width, Is.EqualTo(1024), "the resolution is not changed by the configuration");
            Assert.That(window.Preview.HasModel, Is.True);
            Assert.That(d.NormalSettings.FileDirection, Is.EqualTo(NormalYDirection.DirectX));
            Assert.That(layer.GetChannel(PaintChannel.Color).GetPixel(5, 5), Is.EqualTo(new Rgba32(1, 2, 3)), "the painting stays");
            d.Undo(); Assert.That(d.NormalSettings.FileDirection, Is.EqualTo(NormalYDirection.OpenGL), "the normal format change is one undo step");
        }

        [Test] public void TheDialogDrawsAndHandsBackACopyOfItsSettings()
        {
            sceneObject = GameObject.CreatePrimitive(PrimitiveType.Cube); sceneObject.hideFlags = HideFlags.HideAndDontSave;
            var dialog = ScriptableObject.CreateInstance<NewProjectWindow>();
            NewProjectSettings got; var given = new NewProjectSettings { Model = sceneObject, Resolution = 4096, Template = ProjectTemplate.ColorOnly };
            try
            {
                dialog.Settings = given.Clone();
                if (Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                        foreach (var configure in new[] { false, true })
                        {
                            L.OverrideLanguage(language); dialog.Configure = configure;
                            string path = Path.Combine(Path.GetFullPath("Logs"), "YoluPainterSnapshots", "new-project-" + language + (configure ? "-configure" : "") + ".png");
                            OffscreenGui.RenderToPng((int)NewProjectWindow.Width, (int)NewProjectWindow.Height, () => dialog.DrawContent(new Rect(0, 0, NewProjectWindow.Width, NewProjectWindow.Height)), path, PaintTheme.PanelBg);
                            Assert.That(File.Exists(path), Is.True);
                        }
                got = dialog.TakeSettings();
                Assert.That(got, Is.Not.SameAs(dialog.Settings), "a copy: later edits in the dialog do not change it");
                Assert.That((got.Model, got.Resolution, got.Template), Is.EqualTo((sceneObject, 4096, ProjectTemplate.ColorOnly)));
                dialog.Settings.Resolution = 1000;
                Assert.That(() => dialog.TakeSettings(), Throws.TypeOf<ArgumentOutOfRangeException>());
            }
            finally { L.OverrideLanguage(PainterLanguage.English); if (dialog != null) UnityEngine.Object.DestroyImmediate(dialog); }
        }
    }
}
