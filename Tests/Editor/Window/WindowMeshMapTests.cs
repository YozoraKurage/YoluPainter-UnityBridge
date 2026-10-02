using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウの mesh map: デモのキューブで焼いてもドキュメント・Undo・保存の印は変わらない、キャンバスの重ね表示と欄の描画、
    /// 取消で前のマップが残る、モデルが無い・予算を超えると焼かない、.ylp に保存して別のウィンドウで開くと同じバイト列で戻り、
    /// モデル・設定が違えば古いと判定して使わせない。進捗バーは差し替える（モーダルを出さない）。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>レイを減らして速く焼く（ドキュメントは既定の 1024²）。進捗は取消を押さない。</summary>
        static void QuickBake(TexturePaintWindow w)
        {
            w.MeshBakeSettings.AoSamples = 8; w.MeshBakeSettings.ThicknessSamples = 8;
            w.MeshBakeProgress = (title, info, progress) => false;
        }

        [Test] public void BakingTheDemoCubeAddsMapsWithoutTouchingTheDocument()
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            var d = window.Document; long revision = d.Revision; int undo = d.UndoCount; bool saved = window.IsSaved;
            QuickBake(window); int calls = 0; window.MeshBakeProgress = (title, info, progress) => { calls++; return false; };
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), window.StatusMessage);
            Assert.That(calls, Is.GreaterThan(0), "progress was shown");
            Assert.That(window.MeshMaps.Count, Is.EqualTo(5)); Assert.That(window.StatusMessage, Does.Contain("Baked"));
            Assert.That(d.Revision, Is.EqualTo(revision)); Assert.That(d.UndoCount, Is.EqualTo(undo)); Assert.That(window.IsSaved, Is.EqualTo(saved));
            Assert.That(window.MeshMapsSaved, Is.False, "baked maps are not in a file yet");
            var expected = window.CurrentMeshMapExpectation();
            foreach (var map in window.MeshMaps.Maps)
            {
                Assert.That((map.Width, map.Height), Is.EqualTo((d.Width, d.Height)), "the resolution follows the document");
                Assert.That(map.Provenance.Check(expected).State, Is.EqualTo(MeshMapState.Current));
            }
            window.ShowMeshMapPanel = true;
            window.MeshMapOverlay = TexturePaintWindow.MeshMapView.AmbientOcclusion; Repaint(window);
            var overlay = window.EnsureMeshMapOverlay();
            Assert.That(overlay, Is.Not.Null); Assert.That((overlay.width, overlay.height), Is.EqualTo((d.Width, d.Height)));
            window.MeshMapOverlay = TexturePaintWindow.MeshMapView.Coverage; Repaint(window);
            Assert.That(d.Revision, Is.EqualTo(revision), "the overlay is display only");
            foreach (var map in window.MeshMaps.Maps)
                Assert.That(map.Provenance.Check(window.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current), "drawing the panel's float sliders must not change the bake settings (" + map.Kind + ")");
            // 描いても（レイヤーの画素が変わっても）マップは古くならない（モデルから焼いたもの）
            PaintDot(window, 300, 300);
            Assert.That(window.MeshMaps.Check(MeshMapKind.Curvature, window.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current));
        }

        [Test] public void CancelingTheBakeKeepsThePreviousMaps()
        {
            window.Preview.LoadDemoMesh(); QuickBake(window);
            window.MeshBakeSettings.Maps = new[] { MeshMapKind.WorldNormal };
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed));
            window.MeshMaps.TryGet(MeshMapKind.WorldNormal, out var before); long revision = window.MeshMaps.Revision;
            window.MeshBakeSettings.Maps = MeshBakeSettings.AllKinds.ToArray();
            window.MeshBakeProgress = (title, info, progress) => true; // 進捗バーの取消を押す
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Canceled));
            Assert.That(window.StatusMessage, Does.Contain("canceled"));
            Assert.That(window.MeshMaps.Revision, Is.EqualTo(revision)); Assert.That(window.MeshMaps.Count, Is.EqualTo(1));
            Assert.That(window.MeshMaps.TryGet(MeshMapKind.WorldNormal, out var after) && ReferenceEquals(after, before), Is.True, "nothing half-baked replaced the map");
        }

        [Test] public void BakeIsRefusedWithoutAModelOrOverTheBudget()
        {
            QuickBake(window);
            Assert.That(window.BakeMeshMaps(), Is.Null); Assert.That(window.StatusMessage, Does.Contain("Load a model"));
            window.Preview.LoadDemoMesh();
            PainterSettings.UpdatePersonal(p => p.strokeBudgetMiB = PainterSettings.MinStrokeMiB);
            Assert.That(window.BakeMeshMaps(), Is.Null); Assert.That(window.StatusMessage, Does.Contain("budget"));
            Assert.That(window.MeshMaps.Count, Is.Zero);
        }

        [Test] public void AHighPolyChosenInThePanelIsProjectedAndReplacingItMakesMapsStale()
        {
            window.Preview.LoadDemoMesh(); QuickBake(window);
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var material = new Material(Shader.Find("Hidden/YoluPainter/PreviewSurface"));
            GameObject Make(string name, float scale)
            {
                var go = new GameObject(name); go.transform.localScale = Vector3.one * scale;
                go.AddComponent<MeshFilter>().sharedMesh = cube; go.AddComponent<MeshRenderer>().sharedMaterial = material; return go;
            }
            var high = Make("High cube", 1.02f); var other = Make("Other high cube", 1.04f);
            int dirty = UnityEditor.EditorUtility.GetDirtyCount(high), meshDirty = UnityEditor.EditorUtility.GetDirtyCount(cube);
            try
            {
                var s = window.MeshBakeSettings;
                s.Maps = new[] { MeshMapKind.TangentNormal, MeshMapKind.Height, MeshMapKind.Opacity }; s.ReferenceFrontal = 0.05; s.ReferenceRear = 0.05;
                s.ReferenceAverageNormals = false; // 立方体の頂点はどれも角なので、平均の法線は面の上で斜めになる。面の法線で投影して式と比べる
                window.HighPolyModel = high;
                Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), window.StatusMessage);
                Assert.That(window.LastMeshBakeReport.MissedSamples, Is.Zero, "every low point finds the slightly larger cube");
                window.MeshMaps.TryGet(MeshMapKind.Height, out var height); window.MeshMaps.TryGet(MeshMapKind.Opacity, out var opacity);
                Assert.That(height.Provenance.Source, Does.StartWith("Reference:"));
                double range = 0.05 * Mathf.Sqrt(3), expected = 0.5 + 0.5 * 0.01 / range; int total = 0, flat = 0;
                for (int y = 0; y < height.Height; y += 7) for (int x = 0; x < height.Width; x += 7)
                {
                    if (height.CoverageAt(x, y) != MeshTexelCoverage.Covered) continue;
                    Assert.That(opacity.RawValue(x, y), Is.EqualTo(65535)); total++;
                    if (Math.Abs(height.Value(x, y) - expected) < 2e-3) flat++;
                }
                Assert.That(total, Is.GreaterThan(100)); Assert.That(flat, Is.EqualTo(total), "the high cube is 0.01 outside every face");
                Assert.That(window.MeshMaps.Check(MeshMapKind.Height, window.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current));
                window.HighPolyModel = other;
                var check = window.MeshMaps.Check(MeshMapKind.Opacity, window.CurrentMeshMapExpectation());
                Assert.That(check.State, Is.EqualTo(MeshMapState.Stale)); Assert.That(string.Join(" ", check.Reasons), Does.Contain("high-poly reference"));
                window.HighPolyModel = null;
                Assert.That(string.Join(" ", window.MeshMaps.Check(MeshMapKind.Opacity, window.CurrentMeshMapExpectation()).Reasons), Does.Contain("none is chosen"));
                window.ShowMeshMapPanel = true; Repaint(window);
                Assert.That(UnityEditor.EditorUtility.GetDirtyCount(high), Is.EqualTo(dirty)); Assert.That(UnityEditor.EditorUtility.GetDirtyCount(cube), Is.EqualTo(meshDirty));
                Assert.That(high.transform.localScale, Is.EqualTo(Vector3.one * 1.02f));
            }
            finally { Object.DestroyImmediate(high); Object.DestroyImmediate(other); Object.DestroyImmediate(material); }
        }

        [Test] public void SavedMeshMapsComeBackAndAreCheckedAgainstTheModel()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath("Baked.ylp");
            window.Preview.LoadDemoMesh(); QuickBake(window);
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed));
            window.SaveProject(true);
            Assert.That(window.MeshMapsSaved, Is.True, window.StatusMessage); Assert.That(window.MeshMapNote, Is.Null);
            var files = YlpStore.Load(fake.File).Files;
            foreach (var kind in MeshBakeSettings.DefaultKinds) Assert.That(files.ContainsKey(MeshMapBinary.EntryName(kind)), Is.True, kind.ToString());
            var keys = window.MeshMaps.Maps.ToDictionary(m => m.Kind, m => m.Provenance.ConditionKey);
            var other = Open();
            try
            {
                UseFakeDialogs(other);
                other.OpenProjectAt(fake.File);
                Assert.That(other.MeshMaps.Count, Is.EqualTo(5), other.StatusMessage);
                Assert.That(other.StatusMessage, Does.Contain("mesh map(s) restored"));
                foreach (var map in other.MeshMaps.Maps)
                {
                    Assert.That(map.Provenance.ConditionKey, Is.EqualTo(keys[map.Kind]));
                    Assert.That(MeshMapBinary.Write(map), Is.EqualTo(files[MeshMapBinary.EntryName(map.Kind)]), "restored byte for byte");
                }
                Assert.That(other.MeshMapsSaved, Is.True);
                Assert.That(other.MeshBakeSettings.AoSamples, Is.EqualTo(8), "the panel takes the saved bake settings");
                Assert.That(other.MeshMaps.Check(MeshMapKind.AmbientOcclusion, other.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Unverified), "no model in the new window yet");
                other.Preview.LoadDemoMesh();
                Assert.That(other.MeshMaps.Check(MeshMapKind.AmbientOcclusion, other.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current));
                other.MeshBakeSettings.AoSamples = 64;
                Assert.That(other.MeshMaps.Check(MeshMapKind.AmbientOcclusion, other.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Stale), "AO settings changed");
                Assert.That(other.MeshMaps.Check(MeshMapKind.WorldNormal, other.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current));
                // 別のモデル（組み込みのキューブ）
                var source = new GameObject("Mesh map other model");
                source.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                var material = new Material(Shader.Find("Hidden/YoluPainter/PreviewSurface"));
                source.AddComponent<MeshRenderer>().sharedMaterial = material;
                try
                {
                    Assert.That(other.Preview.Load(source).CanPaint, Is.True);
                    var check = other.MeshMaps.Check(MeshMapKind.WorldNormal, other.CurrentMeshMapExpectation());
                    Assert.That(check.State, Is.EqualTo(MeshMapState.Stale)); Assert.That(string.Join(" ", check.Reasons), Does.Contain("model changed"));
                    Assert.That(other.MeshMaps.TryGetUsable(MeshMapKind.WorldNormal, other.CurrentMeshMapExpectation(), out var none, out _), Is.False); Assert.That(none, Is.Null);
                    other.ShowMeshMapPanel = true; Repaint(other); // 古いマップの警告を出す欄が描ける
                }
                finally { Object.DestroyImmediate(source); Object.DestroyImmediate(material); }
                // 新しいドキュメントにはマップを持ち越さない
                Invoke(other, "CreateDocument", 512); Invoke(other, "BindDocument");
                Assert.That(other.MeshMaps.Count, Is.Zero); Assert.That(other.MeshMapsSaved, Is.True);
            }
            finally { Close(other); }
        }
    }
}
