using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class ModelVisibilityTests
    {
        string folder, temp; TexturePaintWindow window; GameObject model;
        sealed class Dialogs : IPainterDialogs
        {
            public string File;
            public string SaveFolder(string title, string folder, string name) => "";
            public string OpenFolder(string title, string folder) => "";
            public string OpenFile(string title, string folder, string extension) => File;
            public string SaveFile(string title, string folder, string name, string extension) => File;
            public bool Confirm(string title, string message, string ok, string cancel) => true;
            public void Inform(string title, string message) { }
            public void Progress(string title, string info, float value) { }
            public void ClearProgress() { }
        }
        [SetUp] public void Create()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            temp = Path.Combine(Path.GetTempPath(), "yolu-visibility-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp); PainterSettings.ProjectRoot = temp;
            string name = "VisibilityTests-" + Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            window = ScriptableObject.CreateInstance<TexturePaintWindow>(); window.Dialogs = new Dialogs { File = Path.Combine(temp, "view.ylp") };
            model = TextureSetModels.Prefab(folder, "Pair", "Body", "Hair");
            window.CreateProject(new NewProjectSettings { Model = model, Resolution = 512 });
        }
        [TearDown] public void Clean()
        {
            var recovery = window.RecoveryRoot; Object.DestroyImmediate(window); window = null;
            if (Directory.Exists(recovery)) Directory.Delete(recovery, true);
            AssetDatabase.DeleteAsset(folder); PainterSettings.ProjectRoot = null; Directory.Delete(temp, true);
        }
        [Test] public void AMaterialSetsEyeHidesEveryOwnedSlotAcrossRenderers()
        {
            window.CreateProject(new NewProjectSettings { Model = MaterialSetModels.Avatar(folder), Resolution = 512 });
            var set = window.TextureSets.Single(s => s.Slots.SequenceEqual(new[] { 0, 2 }));
            Assert.That(window.TextureSetSlots(set), Is.EqualTo(new[] { 0, 2 }));
            var full = window.Preview.Geometry; long revision = set.Document.Revision;
            window.SetTextureSetVisible(set.Id, false);
            for (int i = 0; i < full.Triangles.Count; i++)
                Assert.That(window.Preview.IsTriangleVisible(i), Is.EqualTo(!set.PaintsSlot(full.Triangles[i].MaterialSlot)), "triangle " + i);
            Assert.That(window.Preview.Geometry, Is.SameAs(full)); Assert.That(set.Document.Revision, Is.EqualTo(revision));
            window.SetTextureSetVisible(set.Id, true);
            Assert.That(Enumerable.Range(0, full.Triangles.Count).All(window.Preview.IsTriangleVisible), Is.True);
        }
        [Test] public void SetAndRendererVisibilityAreViewStateAndAltIsolationHasNoUndo()
        {
            var sets = window.TextureSets.ToArray(); var revisions = sets.Select(s => s.Document.Revision).ToArray();
            window.SetTextureSetVisible(sets[0].Id, false);
            Assert.That(window.Preview.IsVisible(0, 0), Is.False); Assert.That(window.Preview.IsVisible(0, 1), Is.True);
            window.SetTextureSetVisible(sets[0].Id, true, true);
            Assert.That(window.Preview.IsVisible(0, 0), Is.True); Assert.That(window.Preview.IsVisible(0, 1), Is.False);
            string key = window.Preview.Renderers.Single().Key;
            window.SetRendererVisible(key, false); Assert.That(window.Preview.AnyVisible, Is.False);
            window.SetRendererVisible(key, true, true); Assert.That(window.Preview.AnyVisible, Is.True); Assert.That(window.TextureSetVisible(sets[1].Id), Is.True);
            Assert.That(sets.Select(s => s.Document.Revision), Is.EqualTo(revisions)); Assert.That(sets.All(s => !s.Document.CanUndo), Is.True);
            Assert.That(window.CurrentTextureSet, Is.SameAs(sets[0]));
        }
        [Test] public void HidingASlotChangesRenderedPixelsAndPickingWithoutMutatingAssetsOrBake()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
            var p = window.Preview; var source = model.GetComponent<MeshFilter>().sharedMesh;
            var indices = Enumerable.Range(0, source.subMeshCount).Select(i => source.GetTriangles(i)).ToArray(); var full = p.Geometry;
            var input = window.CurrentMeshBakeInput();
            var settings = new MeshBakeSettings { Width = 16, Height = 16, Padding = 0, Maps = new[] { MeshMapKind.Position } };
            var bake = MeshMapBinary.Write(MeshBaker.Bake(input, settings).Maps.Single());
            var first = p.RenderStatic(128, 128); var pixels = first.GetPixels32(); Object.DestroyImmediate(first);
            var t = full.Triangles.First(x => x.MaterialSlot == 0); var center = (t.A + t.B + t.C) / 3; var ray = new Ray(center + t.Normal * 2, -t.Normal);
            Assert.That(p.PickingGeometry.TryRaycast(ray, out var hit), Is.True); Assert.That(hit.MaterialSlot, Is.Zero);
            window.SetTextureSetVisible(window.TextureSets[0].Id, false);
            Assert.That(p.PickingGeometry.TryRaycast(ray, out _), Is.False);
            var hidden = p.RenderStatic(128, 128); Assert.That(hidden.GetPixels32(), Is.Not.EqualTo(pixels)); Object.DestroyImmediate(hidden);
            Assert.That(window.CurrentMeshBakeInput(), Is.SameAs(input)); Assert.That(p.Geometry, Is.SameAs(full));
            Assert.That(MeshMapBinary.Write(MeshBaker.Bake(window.CurrentMeshBakeInput(), settings).Maps.Single()), Is.EqualTo(bake));
            for (int i = 0; i < indices.Length; i++) Assert.That(source.GetTriangles(i), Is.EqualTo(indices[i]));
            Assert.That(EditorUtility.IsDirty(source), Is.False);
        }
        [Test] public void HighlightDropsHiddenFacesAndRebuildsOnlyWhenVisibilityChanges()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PickHighlight");
            var p = window.Preview; p.FrameRateLimit = 0; var rect = new Rect(0, 0, 128, 128);
            var triangles = Enumerable.Range(0, p.Geometry.TriangleCount).ToArray();
            p.ShowRegion(17, triangles, Color.cyan); p.RenderCached(rect, 1);
            Assert.That(p.RegionHighlightTriangleCount, Is.EqualTo(triangles.Length)); int builds = p.RegionHighlightBuilds;
            window.SetTextureSetVisible(window.TextureSets[0].Id, false);
            int remaining = triangles.Count(p.IsTriangleVisible); Assert.That(remaining, Is.GreaterThan(0).And.LessThan(triangles.Length));
            p.ShowRegion(17, triangles, Color.cyan); p.RenderCached(rect, 1);
            Assert.That(p.RegionHighlightTriangleCount, Is.EqualTo(remaining)); Assert.That(p.RegionHighlightBuilds, Is.EqualTo(builds + 1));
            p.ShowRegion(17, triangles, Color.cyan); p.RenderCached(rect, 1);
            Assert.That(p.RegionHighlightBuilds, Is.EqualTo(builds + 1));
        }
        [Test] public void VisibilityRoundTripsInYlpAndRemainsOutsideTheSavedDocument()
        {
            var id = window.TextureSets[1].Id; var key = window.Preview.Renderers.Single().Key;
            window.SetTextureSetVisible(id, false); window.SetRendererVisible(key, false); window.SaveProject(true); Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            string path = ((Dialogs)window.Dialogs).File; var saved = YlpStore.Load(path);
            Assert.That(System.Text.Encoding.UTF8.GetString(saved.Files[YlpContent.ViewName]), Does.Contain("hiddenSets"));
            var before = saved.Files[YlpFormat.SetEntry(window.CurrentTextureSet.Id, YlpArchive.NativeName)];
            window.ShowAllModelParts(); Assert.That(window.IsSaved, Is.True, "view changes do not dirty the document");
            // 同じ保存済みパスを再度開くと Already open になるので、新しい窓で読む。
            var other = ScriptableObject.CreateInstance<TexturePaintWindow>(); other.Dialogs = window.Dialogs;
            try
            {
                other.OpenProjectAt(path); Assert.That(other.TextureSetVisible(id), Is.False); Assert.That(other.RendererVisible(key), Is.False); Assert.That(other.Preview.AnyVisible, Is.False);
                other.SaveProject(true); Assert.That(YlpStore.Load(path).Files[YlpFormat.SetEntry(other.CurrentTextureSet.Id, YlpArchive.NativeName)], Is.EqualTo(before));
            }
            finally { string recovery = other.RecoveryRoot; Object.DestroyImmediate(other); if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
        }
        [TestCase(1)] [TestCase(2)] public void VisibilityPanelAndAllHiddenNoticeDrawInBothLanguages(int language)
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
            string images = Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "visibility")); Directory.CreateDirectory(images);
            try
            {
                L.OverrideLanguage((PainterLanguage)language); window.SetTextureSetVisible(window.TextureSets[1].Id, false);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                typeof(TexturePaintWindow).GetField("meshesExpanded", flags).SetValue(window, true);
                var draw = typeof(TexturePaintWindow).GetMethod("DrawTextureSetPanel", flags);
                float height = (float)typeof(TexturePaintWindow).GetProperty("TextureSetHeight", flags).GetValue(window);
                string panel = Path.Combine(images, "panel-" + language + ".png");
                OffscreenGui.RenderToPng(360, (int)height, () => draw.Invoke(window, new object[] { new Rect(0, 0, 360, height) }), panel, PaintTheme.PanelBg);
                Assert.That(new FileInfo(panel).Length, Is.GreaterThan(1000));
                window.SetRendererVisible(window.Preview.Renderers.Single().Key, false);
                OffscreenGui.RenderWindow(window, 1200, 800, Path.Combine(images, "hidden-" + language + ".png"));
                Assert.That(window.Preview.AnyVisible, Is.False);
            }
            finally { L.OverrideLanguage(PainterLanguage.English); }
        }
        [Test] public void LegacyViewWithoutVisibilityShowsEverything()
        {
            window.SetTextureSetVisible(window.TextureSets[0].Id, false);
            window.RestoreVisibility(null); Assert.That(window.Preview.AnyVisible, Is.True); Assert.That(window.TextureSets.All(s => window.TextureSetVisible(s.Id)), Is.True);
            Assert.That(JsonUtility.FromJson<TexturePaintWindow.VisibilityState>("{}"), Is.Not.Null);
        }
        [Test] public void RendererKeysFollowTheHierarchyAndIsolatingOneHidesTheOther()
        {
            var root = new GameObject("Root"); var left = Object.Instantiate(model); left.name = "Left"; left.transform.SetParent(root.transform);
            var parent = new GameObject("Group"); parent.transform.SetParent(root.transform); var right = Object.Instantiate(model); right.name = "Right"; right.transform.SetParent(parent.transform);
            try
            {
                window.SetModel(root); var infos = window.Preview.Renderers.ToArray();
                Assert.That(infos.Select(r => string.Join("/", r.Names)), Is.EquivalentTo(new[] { "Root/Left", "Root/Group/Right" }));
                window.SetRendererVisible(infos[1].Key, true, true);
                Assert.That(window.RendererVisible(infos[0].Key), Is.False); Assert.That(window.RendererVisible(infos[1].Key), Is.True);
                Assert.That(window.Preview.IsVisible(infos[0].Index, infos[0].Slots[0]), Is.False);
                Assert.That(window.Preview.IsVisible(infos[1].Index, infos[1].Slots[0]), Is.True);
            }
            finally { Object.DestroyImmediate(root); }
        }
        [TestCase("null")] [TestCase("{\"visibility\":{\"hiddenSets\":[\"bad\"]}}")]
        [TestCase("{\"selectedChannel\":99}")]
        public void InvalidSavedViewDoesNotPreventOpeningTheNativeDocument(string view)
        {
            window.SaveProject(true); string path = ((Dialogs)window.Dialogs).File;
            var files = YlpStore.Load(path).Files.ToDictionary(p => p.Key, p => p.Value); files[YlpContent.ViewName] = System.Text.Encoding.UTF8.GetBytes(view);
            string altered = Path.Combine(temp, "altered.ylp"); YlpStore.Save(altered, files, null, false, 0);
            window.OpenProjectAt(altered);
            Assert.That(window.TextureSets.Count, Is.EqualTo(2)); Assert.That(window.Preview.AnyVisible, Is.True);
            Assert.That(window.StatusMessage, Does.Contain("view state could not be restored"));
        }
        [TestCase("id")] [TestCase("budget")] [TestCase("renderer")] public void InvalidVisibilityIsRefusedBeforeReplacingTheCurrentState(string kind)
        {
            var id = window.TextureSets[0].Id; window.SetTextureSetVisible(id, false);
            var invalid = new TexturePaintWindow.VisibilityState();
            if (kind == "id") invalid.hiddenSets.Add("not-a-guid");
            if (kind == "budget") invalid.hiddenSets.AddRange(Enumerable.Repeat(id.ToString("D"), 65));
            if (kind == "renderer") invalid.hiddenRenderers.Add("../../asset");
            Assert.That(() => window.RestoreVisibility(invalid), Throws.TypeOf<InvalidDataException>());
            Assert.That(window.TextureSetVisible(id), Is.False); Assert.That(window.Preview.IsVisible(0, 0), Is.False);
            Assert.That(() => window.SetTextureSetVisible(Guid.NewGuid(), false), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => window.SetRendererVisible("absent", false), Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }
    public sealed partial class WindowTests
    {
        void ClickVisibility(string spot, bool alt = false)
        {
            Repaint(window); var point = window.TextureSetScreenRects[spot].center - window.DockScreenOriginForTests;
            foreach (var type in new[] { EventType.MouseDown, EventType.MouseUp })
                window.SendEvent(new Event { type = type, mousePosition = point + window.rootVisualElement.worldBound.position, button = 0, modifiers = alt ? EventModifiers.Alt : EventModifiers.None });
            Repaint(window);
        }
        [Test] public void VisibilityEyeClicksAndAltIsolationKeepTheSelectedSet()
        {
            string folder = TextureSetFolder();
            try
            {
                window.CreateProject(new NewProjectSettings { Model = TextureSetModels.Prefab(folder, "Pair", "Body", "Hair"), Resolution = 512 });
                var sets = window.TextureSets.ToArray(); var current = window.CurrentTextureSet;
                Repaint(window); Assert.That(window.TextureSetScreenRects.ContainsKey("visibility.renderer.0"), Is.False, "the mesh list starts folded to leave room for other panels");
                ClickVisibility("visibility.meshes");
                ClickVisibility("visibility.set.0"); Assert.That(window.TextureSetVisible(sets[0].Id), Is.False);
                ClickVisibility("visibility.set.1", true); Assert.That(window.TextureSetVisible(sets[0].Id), Is.False); Assert.That(window.TextureSetVisible(sets[1].Id), Is.True);
                ClickVisibility("visibility.renderer.0"); Assert.That(window.Preview.AnyVisible, Is.False);
                ClickVisibility("visibility.set.0", true); Assert.That(window.Preview.AnyVisible, Is.True); Assert.That(window.TextureSetVisible(sets[1].Id), Is.False);
                Assert.That(window.CurrentTextureSet, Is.SameAs(current)); Assert.That(window.Document.CanUndo, Is.False);
                ClickVisibility("visibility.showAll"); Assert.That(window.TextureSets.All(s => window.TextureSetVisible(s.Id)), Is.True);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
        [Test] public void VisibilityChangeCancelsAnUnfinishedStrokeAndHiddenSurfaceToolsDoNothing()
        {
            window.Preview.LoadDemoMesh(); Repaint(window); var before = Snapshot(); BeginLine(200, 200);
            window.SetTextureSetVisible(window.CurrentTextureSet.Id, false); Assert.That(window.IsStroking, Is.False); Assert.That(Snapshot(), Is.EqualTo(before));
            var point = window.SurfaceRect.center; long revision = window.Document.Revision;
            foreach (var tool in new[] { TexturePaintWindow.PaintTool.Brush, TexturePaintWindow.PaintTool.Eyedropper, TexturePaintWindow.PaintTool.SelectRectangle, TexturePaintWindow.PaintTool.Fill })
            { window.Tool = tool; Click(point); Assert.That(window.Document.Revision, Is.EqualTo(revision), tool.ToString()); Assert.That(window.Document.Selection, Is.Null); }
            Assert.That(window.Document.CanUndo, Is.False);
        }
        [Test] public void VisibilityAllHiddenNoticeRestoresTheModelWithoutEditingIt()
        {
            window.Preview.LoadDemoMesh(); Repaint(window); long revision = window.Document.Revision;
            window.SetRendererVisible(window.Preview.Renderers.Single().Key, false); Repaint(window);
            Click(window.SurfaceRect.center + new Vector2(0, 14));
            Assert.That(window.Preview.AnyVisible, Is.True); Assert.That(window.Document.Revision, Is.EqualTo(revision)); Assert.That(window.Document.CanUndo, Is.False);
        }
        [Test] public void VisibilitySurvivesWindowReloadWithoutAddingHistory()
        {
            window.Preview.LoadDemoMesh(); var id = window.CurrentTextureSet.Id;
            window.SetTextureSetVisible(id, false); Invoke(window, "BeforeReload"); Invoke(window, "OnDisable"); Invoke(window, "OnEnable");
            Assert.That(window.TextureSetVisible(id), Is.False); Assert.That(window.Document.CanUndo, Is.False);
        }
    }
}
