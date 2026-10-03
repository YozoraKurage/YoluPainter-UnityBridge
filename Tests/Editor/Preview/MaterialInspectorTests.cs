using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.MaterialApply;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class MaterialInspectorTests
    {
        readonly List<Object> made = new List<Object>();
        string folder;
        T Track<T>(T o) where T : Object { made.Add(o); return o; }
        string Folder
        {
            get
            {
                if (folder == null) { folder = "Assets/ZZ_MaterialInspectorTests-" + Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", folder.Substring(7)); }
                return folder;
            }
        }
        Material Source(string shader)
        {
            var s = Shader.Find(shader); Assert.That(s, Is.Not.Null, shader);
            var m = new Material(s); string path = Folder + "/Source.mat";
            AssetDatabase.CreateAsset(m, path); AssetDatabase.SaveAssets();
            return m;
        }
        [SetUp] public void English() => L.OverrideLanguage(PainterLanguage.English);
        [TearDown] public void Cleanup()
        {
            InspectorAssetWatch.Active = false;
            foreach (var o in made) if (o != null && !EditorUtility.IsPersistent(o)) Object.DestroyImmediate(o); made.Clear();
            if (folder != null) AssetDatabase.DeleteAsset(folder); folder = null;
        }

        [TestCase("Standard", "_Glossiness")]
        [TestCase("lilToon", "_Cutoff")]
        public void MaterialEditorChangesOnlyItsUnsavedCopyAndKeepsTheSourceUndo(string shader, string property)
        {
            var source = Source(shader); var edits = new PreviewMaterialEdits();
            string before = EditorJsonUtility.ToJson(source); int dirty = EditorUtility.GetDirtyCount(source);
            byte[] bytes = File.ReadAllBytes(AssetDatabase.GetAssetPath(source));
            Undo.RecordObject(source, "Source history sentinel"); source.SetFloat(property, .6f); Undo.FlushUndoRecordObjects();
            string undo = PreviewMaterialTests.UndoRecords(); before = EditorJsonUtility.ToJson(source); dirty = EditorUtility.GetDirtyCount(source);
            using (var inspector = new PreviewMaterialInspector())
            {
                inspector.Sync(source, edits);
                Assert.That(inspector.Copy.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
                Assert.That(EditorUtility.IsPersistent(inspector.Copy), Is.False);
                Assert.That(inspector.Editor.target, Is.SameAs(inspector.Copy));
                Assert.That(inspector.Inspect(edits, (editor, properties) =>
                {
                    editor.RegisterPropertyChangeUndo("Preview change");
                    properties.Single(p => p.name == property).floatValue = .2f;
                    inspector.Copy.EnableKeyword("_EMISSION");
                }), Is.True);
                Assert.That(edits.Find(source, property).Float, Is.EqualTo(.2f));
                if (shader == "Standard") Assert.That(edits.For(source).Any(e => e.kind == MaterialEditKind.Keyword), Is.True);
                Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
                Assert.That(EditorUtility.GetDirtyCount(source), Is.EqualTo(dirty));
                Assert.That(File.ReadAllBytes(AssetDatabase.GetAssetPath(source)), Is.EqualTo(bytes));
                Assert.That(PreviewMaterialTests.UndoRecords(), Is.EqualTo(undo), "the copy leaves no Unity undo record");
                edits.RevertAll(source); inspector.Sync(source, edits);
                Assert.That(inspector.Copy.GetFloat(property), Is.EqualTo(.6f));
                Assert.That(inspector.Capture(edits, true), Is.False, "revert is not recaptured from an old copy");
            }
            Undo.PerformUndo(); Assert.That(source.GetFloat(property), Is.Not.EqualTo(.6f), "the source's earlier history still works");
        }

        [TestCase("Standard", "_Glossiness")]
        [TestCase("lilToon", "_Cutoff")]
        public void InspectorChangesKeepTheSourcesPendingRedo(string shader, string property)
        {
            var source = Source(shader); var edits = new PreviewMaterialEdits();
            Undo.IncrementCurrentGroup(); Undo.RecordObject(source, "Source redo sentinel"); source.SetFloat(property, .6f); Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            string undo = PreviewMaterialTests.UndoRecords();
            using (var inspector = new PreviewMaterialInspector())
            {
                inspector.Sync(source, edits);
                inspector.Inspect(edits, (editor, properties) =>
                {
                    editor.RegisterPropertyChangeUndo("Preview change");
                    properties.Single(p => p.name == property).floatValue = .2f;
                    editor.serializedObject.Update();
                    editor.serializedObject.FindProperty("m_CustomRenderQueue").intValue = 2401;
                    editor.serializedObject.ApplyModifiedProperties();
                });
                Assert.That(inspector.Problem, Is.Null);
                Assert.That(edits.Find(source, "$RenderQueue"), Is.Not.Null, EditorJsonUtility.ToJson(inspector.Copy));
                Assert.That(edits.Find(source, "$RenderQueue").Float, Is.EqualTo(2401));
                Assert.That(PreviewMaterialTests.UndoRecords(), Is.EqualTo(undo));
                Undo.PerformRedo(); Assert.That(source.GetFloat(property), Is.EqualTo(.6f));
            }
        }

        [Test] public void TexturesTransformsKeywordsAndRenderingValuesSurviveRestoreApplyAndUndo()
        {
            var source = Source("Standard"); float before = source.GetFloat("_Glossiness");
            var texture = new Texture2D(2, 2); AssetDatabase.CreateAsset(texture, Folder + "/Texture.asset"); AssetDatabase.SaveAssets();
            var edits = new PreviewMaterialEdits();
            using (var inspector = new PreviewMaterialInspector())
            {
                inspector.Sync(source, edits);
                inspector.Inspect(edits, (editor, properties) =>
                {
                    properties.Single(p => p.name == "_Glossiness").floatValue = .15f;
                    var main = properties.Single(p => p.name == "_MainTex");
                    main.textureValue = texture; main.textureScaleAndOffset = new Vector4(2, 3, .1f, .2f);
                    inspector.Copy.EnableKeyword("_EMISSION");
                    inspector.Copy.renderQueue = 2501; inspector.Copy.enableInstancing = true; inspector.Copy.doubleSidedGI = true;
                    inspector.Copy.SetOverrideTag("RenderType", "Transparent"); inspector.Copy.SetOverrideTag("VRCFallback", "Unlit");
                    inspector.Copy.SetShaderPassEnabled("ShadowCaster", false);
                });
            }
            var restored = JsonUtility.FromJson<PreviewMaterialEdits>(JsonUtility.ToJson(edits));
            Assert.That(edits.All.Any(e => e.property == "$tag:VRCFallback"), Is.True, JsonUtility.ToJson(edits));
            using (var inspector = new PreviewMaterialInspector())
            {
                inspector.Sync(source, restored);
                Assert.That(inspector.Copy.GetTexture("_MainTex"), Is.EqualTo(texture));
                Assert.That(inspector.Copy.GetTextureScale("_MainTex"), Is.EqualTo(new Vector2(2, 3)));
                Assert.That(inspector.Copy.GetTag("VRCFallback", false), Is.EqualTo("Unlit"));
                Assert.That(inspector.Copy.IsKeywordEnabled("_EMISSION"), Is.True);
                Assert.That(inspector.Copy.GetShaderPassEnabled("ShadowCaster"), Is.False);
            }
            var plan = PreviewMaterialApply.Plan(source, restored);
            Assert.That(plan.Describe(), Does.Contain("Texture").And.Contain("$keyword:_EMISSION").And.Contain("$tag:VRCFallback"));
            Assert.That(plan.Changes.Single(c => c.Edit.kind == MaterialEditKind.RenderQueue).Describe(), Does.EndWith(" → 2501"));
            Assert.That(plan.Changes.Single(c => c.Edit.kind == MaterialEditKind.Instancing).Describe(), Does.EndWith(" → on"));
            int count = plan.Changes.Count; Assert.That(count, Is.GreaterThanOrEqualTo(8));
            Assert.That(PreviewMaterialApply.Apply(plan, restored), Is.EqualTo(count));
            Assert.That(restored.Count, Is.Zero);
            Assert.That(source.GetTexture("_MainTex"), Is.EqualTo(texture)); Assert.That(source.renderQueue, Is.EqualTo(2501));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.That(source.GetTexture("_MainTex"), Is.Null); Assert.That(source.GetFloat("_Glossiness"), Is.EqualTo(before));
            Assert.That(source.IsKeywordEnabled("_EMISSION"), Is.False); Assert.That(source.GetTag("VRCFallback", false, ""), Is.Empty);
        }

        [Test] public void NativeIntegerColorAndVectorKeepTheirValuesThroughRestoreApplyAndUndo()
        {
            var shader = Track(ShaderUtil.CreateShaderAsset("Shader \"Hidden/YoluPainter/Tests/InspectorTypes\" { Properties { _Integer(\"Integer\", Integer) = 0 _Vector(\"Vector\", Vector) = (0,0,0,0) _Color(\"Color\", Color) = (1,1,1,1) } SubShader { Pass {} } }", true));
            var source = Track(new Material(shader)); var edits = new PreviewMaterialEdits();
            var vector = new Vector4(1, 2, 3, 4); var color = new Color(.1f, .2f, .3f, .4f);
            using (var inspector = new PreviewMaterialInspector())
            {
                inspector.Sync(source, edits);
                inspector.Inspect(edits, (editor, properties) =>
                {
                    properties.Single(p => p.name == "_Integer").intValue = 16777217;
                    properties.Single(p => p.name == "_Vector").vectorValue = vector;
                    properties.Single(p => p.name == "_Color").colorValue = color;
                });
                Assert.That(inspector.Problem, Is.Null);
                Assert.That(inspector.Copy.GetInteger("_Integer"), Is.EqualTo(16777217));
                Assert.That(source.GetInteger("_Integer"), Is.Zero);
            }
            var restored = JsonUtility.FromJson<PreviewMaterialEdits>(JsonUtility.ToJson(edits));
            Assert.That(restored.Find(source, "_Integer").DescribeValue(), Is.EqualTo("16777217"));
            Assert.That(PreviewMaterialApply.Apply(PreviewMaterialApply.Plan(source, restored), restored), Is.EqualTo(3));
            Assert.That(source.GetInteger("_Integer"), Is.EqualTo(16777217));
            Assert.That(source.GetVector("_Vector"), Is.EqualTo(vector)); Assert.That(source.GetColor("_Color"), Is.EqualTo(color));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.That(source.GetInteger("_Integer"), Is.Zero); Assert.That(source.GetVector("_Vector"), Is.EqualTo(Vector4.zero));
        }

        [Test] public void ApplyRefusesAnUnsavedTextureAndAChangedKeywordOrTexture()
        {
            var source = Source("Standard"); var copy = Track(new Material(source)); var edits = new PreviewMaterialEdits();
            copy.SetTexture("_MainTex", Track(new Texture2D(2, 2))); edits.Capture(source, copy);
            var refused = PreviewMaterialApply.Plan(source, edits);
            Assert.That(refused.CanApply, Is.False); Assert.That(refused.Skipped.Single(), Does.Contain("preview-only texture"));
            copy.SetTexture("_MainTex", null); copy.EnableKeyword("_EMISSION"); edits.Capture(source, copy);
            var plan = PreviewMaterialApply.Plan(source, edits); source.EnableKeyword("_EMISSION");
            Assert.Throws<InvalidOperationException>(() => PreviewMaterialApply.Apply(plan, edits));
            Assert.That(edits.Count, Is.GreaterThan(0));
            var first = Track(new Texture2D(2, 2)); var second = Track(new Texture2D(2, 2));
            AssetDatabase.CreateAsset(first, folder + "/First.asset"); AssetDatabase.CreateAsset(second, folder + "/Second.asset");
            copy.CopyPropertiesFromMaterial(source); copy.SetTexture("_MainTex", first); edits.Capture(source, copy);
            plan = PreviewMaterialApply.Plan(source, edits); source.SetTexture("_MainTex", second);
            Assert.Throws<InvalidOperationException>(() => PreviewMaterialApply.Apply(plan, edits));
            Assert.That(source.GetTexture("_MainTex"), Is.EqualTo(second));
            var cube = Track(new Cubemap(2, TextureFormat.RGBA32, false)); AssetDatabase.CreateAsset(cube, folder + "/Cube.asset");
            edits.Find(source, "_MainTex").texture = cube; // 復元された不正な参照も、反映前に拒否する。
            var dimension = PreviewMaterialApply.Plan(source, edits);
            Assert.That(dimension.CanApply, Is.False); Assert.That(dimension.Skipped.Single(), Does.Contain("dimension"));
        }

        [Test] public void PaintedTexturesWinUntilTheirRouteIsRemoved()
        {
            GpuTests.RequireWorkingShader("Standard");
            var source = Source("Standard"); var replacement = Track(new Texture2D(2, 2)); var paintedTexture = Track(new Texture2D(2, 2));
            var edits = new PreviewMaterialEdits(); var copy = Track(new Material(source)); copy.SetTexture("_MainTex", replacement); copy.SetTextureScale("_MainTex", new Vector2(2, 2)); edits.Capture(source, copy);
            using (var view = new PreviewMaterialView(slot => source))
            {
                view.Edits = edits; view.Reset(1);
                var painted = new PreviewSlotChannels(); painted.Composites[PaintChannel.Color] = paintedTexture;
                view.Bind(0, painted);
                Assert.That(view.Display(0).GetTexture("_MainTex"), Is.Not.EqualTo(replacement), "paint keeps its route even after the inspector assigns a texture");
                Assert.That(view.Display(0).GetTextureScale("_MainTex"), Is.EqualTo(new Vector2(2, 2)));
                Assert.That(source.GetTexture("_MainTex"), Is.Null);
                view.Bind(0, null);
                Assert.That(view.Display(0).GetTexture("_MainTex"), Is.EqualTo(replacement), "with no painted channel the texture edit is visible");
            }
        }

        [Test] public void AShaderChangeIsCancelledAndAnInspectorExceptionRollsBackThePartialChange()
        {
            var source = Source("Standard"); var edits = new PreviewMaterialEdits();
            using (var inspector = new PreviewMaterialInspector())
            {
                inspector.Sync(source, edits);
                inspector.Inspect(edits, (editor, properties) => { inspector.Copy.SetFloat("_Glossiness", .2f); throw new InvalidOperationException("test inspector failure"); });
                Assert.That(inspector.Problem, Does.Contain("Other panels"));
                Assert.That(edits.Count, Is.Zero); Assert.That(inspector.Copy.GetFloat("_Glossiness"), Is.EqualTo(source.GetFloat("_Glossiness")));
                inspector.Retry(); inspector.Inspect(edits, (editor, properties) => inspector.Copy.shader = Shader.Find("Unlit/Color"));
                Assert.That(inspector.Copy.shader, Is.SameAs(source.shader)); Assert.That(edits.Count, Is.Zero);
                Assert.That(inspector.Problem, Does.Contain("Choose a shader above"));
            }
        }

        [Test] public void ReloadAndDisableDestroyTheEditorAndCopyButKeepTheSerializedEdits()
        {
            var source = Source("Standard"); var w = Track(ScriptableObject.CreateInstance<TexturePaintWindow>());
            w.EnsureMaterialInspector(source); var editor = w.MaterialInspector.Editor; var copy = w.MaterialInspector.Copy;
            w.MaterialInspector.Inspect(w.MaterialEdits, (e, properties) => copy.SetFloat("_Glossiness", .2f));
            typeof(TexturePaintWindow).GetMethod("BeforeReload", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(w, null);
            Assert.That(editor == null && copy == null, Is.True);
            Assert.That(w.MaterialInspector, Is.Null); Assert.That(w.MaterialEdits.Find(source, "_Glossiness").Float, Is.EqualTo(.2f));
            w.EnsureMaterialInspector(source); editor = w.MaterialInspector.Editor; copy = w.MaterialInspector.Copy;
            Assert.That(copy.GetFloat("_Glossiness"), Is.EqualTo(.2f));
            w.MaterialEdits.RevertAll(); Object.DestroyImmediate(w);
            Assert.That(editor == null && copy == null, Is.True);
        }

        [Test] public void ARealShaderGUIExceptionLeavesTheWindowLayoutUsableAndCanBeRetried()
        {
            if (!Application.isBatchMode) Assert.Ignore("The exception and following frame are checked offscreen in batch-gl.");
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
            var shader = Track(ShaderUtil.CreateShaderAsset("Shader \"Hidden/YoluPainter/Tests/FailingInspector\" { Properties { _Value(\"Value\", Float) = 0 } SubShader { Pass {} } CustomEditor \"Yozolab.YoluPainter.Tests.FailingInspectorGUI\" }", true));
            var source = Track(new Material(shader)); var model = Track(new GameObject("Inspector failure model"));
            model.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx"); model.AddComponent<MeshRenderer>().sharedMaterial = source;
            var w = Track(ScriptableObject.CreateInstance<TexturePaintWindow>()); w.SetModel(model); w.DockLayoutForTests.SetActive("material");
            foreach (var id in new[] { "layers", "properties" }) w.DockLayoutForTests.GroupOf(id).collapsed = true;
            FailingInspectorGUI.Fail = true; FailingInspectorGUI.DrawCalls = 0;
            try
            {
                using (new OffscreenInspectorLogs()) OffscreenGui.RenderWindow(w, 1400, 1100, Path.GetFullPath("Logs/YoluPainterSnapshots/material-inspector/failure.png"));
                Assert.That(w.MaterialInspector.Problem, Does.Contain("test ShaderGUI failure")); Assert.That(source.GetFloat("_Value"), Is.Zero);
                Assert.That(FailingInspectorGUI.DrawCalls, Is.GreaterThan(0), "the embedded ShaderGUI actually ran");
                Assert.That(w.MaterialEdits.Count, Is.Zero);
                using (new OffscreenInspectorLogs()) OffscreenGui.RenderWindow(w, 1400, 1100, Path.GetFullPath("Logs/YoluPainterSnapshots/material-inspector/after-failure.png"));
                FailingInspectorGUI.Fail = false; w.MaterialInspector.Retry();
                using (new OffscreenInspectorLogs()) OffscreenGui.RenderWindow(w, 1400, 1100, Path.GetFullPath("Logs/YoluPainterSnapshots/material-inspector/retried.png"));
                Assert.That(w.MaterialInspector.Problem, Is.Null);
            }
            finally { FailingInspectorGUI.Fail = false; w.MaterialEdits.RevertAll(); }
        }

        [TestCase("Standard")]
        [TestCase("lilToon")]
        public void DrawingTheRealInspectorDoesNotSaveOrDirtyAssetsOrPresets(string shader)
        {
            if (!Application.isBatchMode) Assert.Ignore("The real inspector is drawn offscreen in batch-gl.");
            GpuTests.RequireWorkingShader(shader);
            var source = Source(shader); var edits = new PreviewMaterialEdits();
            var assets = Resources.FindObjectsOfTypeAll<Object>().Where(EditorUtility.IsPersistent).ToDictionary(o => o.GetInstanceID(), o => EditorUtility.GetDirtyCount(o));
            string before = EditorJsonUtility.ToJson(source); string undo = PreviewMaterialTests.UndoRecords();
            InspectorAssetWatch.Saved.Clear(); InspectorAssetWatch.Imported.Clear(); InspectorAssetWatch.Active = true;
            using (var inspector = new PreviewMaterialInspector())
            {
                inspector.Sync(source, edits);
                string path = Path.GetFullPath("Logs/YoluPainterSnapshots/material-inspector/" + shader + ".png");
                using (new OffscreenInspectorLogs()) OffscreenGui.RenderToPng(420, 700, () =>
                {
                    GUI.skin = EditorGUIUtility.GetBuiltinSkin(EditorSkin.Inspector);
                    GUILayout.BeginArea(new Rect(0, 0, 420, 700));
                    try { inspector.Inspect(edits); }
                    finally { GUILayout.EndArea(); }
                }, path, PaintTheme.PanelBg);
                Assert.That(inspector.Problem, Is.Null, "the real ShaderGUI works on a HideAndDontSave material");
                Assert.That(inspector.Editor.customShaderGUI.GetType().FullName, Does.Contain(shader == "lilToon" ? "lilToon" : "Standard"));
                Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
                Assert.That(InspectorAssetWatch.Saved, Is.Empty); Assert.That(InspectorAssetWatch.Imported, Is.Empty);
                foreach (var o in Resources.FindObjectsOfTypeAll<Object>().Where(EditorUtility.IsPersistent))
                    if (assets.TryGetValue(o.GetInstanceID(), out int dirty)) Assert.That(EditorUtility.GetDirtyCount(o), Is.EqualTo(dirty), o.GetType().Name + " was dirtied");
                Assert.That(PreviewMaterialTests.UndoRecords(), Is.EqualTo(undo));
            }
        }
    }

    public sealed class FailingInspectorGUI : ShaderGUI
    {
        internal static bool Fail;
        internal static int DrawCalls;
        internal static Rect ValueScreenRect;
        public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties)
        {
            DrawCalls++;
            EditorGUILayout.BeginVertical();
            if (Fail)
            {
                EditorGUILayout.BeginScrollView(Vector2.zero);
                ((Material)editor.target).SetFloat("_Value", 1); throw new InvalidOperationException("test ShaderGUI failure");
            }
            var rect = EditorGUILayout.GetControlRect();
            editor.ShaderProperty(rect, properties.Single(p => p.name == "_Value"), "Value");
            if (Event.current.type == EventType.Repaint) ValueScreenRect = GUIUtility.GUIToScreenRect(rect);
            EditorGUILayout.EndVertical();
        }
    }

    internal sealed class InspectorAssetWatch : AssetModificationProcessor
    {
        internal static bool Active;
        internal static readonly List<string> Saved = new List<string>(), Imported = new List<string>();
        static string[] OnWillSaveAssets(string[] paths) { if (Active) Saved.AddRange(paths); return paths; }
    }
    internal sealed class InspectorImportWatch : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] from)
        { if (InspectorAssetWatch.Active) InspectorAssetWatch.Imported.AddRange(imported.Concat(deleted).Concat(moved)); }
    }
}
