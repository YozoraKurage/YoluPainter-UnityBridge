using System;
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
    /// <summary>描画ウィンドウの mesh map: デモのキューブで焼いてもドキュメント・Undo・保存の印は変わらない、キャンバスの重ね表示と欄の描画、
    /// 取消で前のマップが残る、モデルが無い・予算を超えると焼かない、.ylp に保存して別のウィンドウで開くと同じバイト列で戻り、
    /// モデル・設定が違えば古いと判定して使わせない。進捗バーは差し替える（モーダルを出さない）。ベイクの窓: プロパティの欄の「ベイク…」で
    /// 開き、一覧・チェック・スライダー・ボタンを本物のマウスの入力で押すと持ち主の設定が変わってその設定で焼ける、ストロークの最中は断って
    /// 理由を出す、持ち主を閉じると窓も閉じて走っているベイクが止まる（描画と CPU・GPU の一致は batch-gl の MeshBakeWindowTests）。</summary>
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
            Assert.That(window.BakeMeshMaps(), Is.Null); Assert.That(window.StatusMessage, Does.Contain("No model"));
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
            var files = SetFiles(YlpStore.Load(fake.File).Files);
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

        // ───────── ベイクの窓 ─────────

        /// <summary>ベイクの窓の部品 id の中の点（SendEvent の座標）。fx は左から何割の所か。</summary>
        static Vector2 BakeControlPoint(MeshBakeWindow w, string id, float fx = .5f)
        {
            Repaint(w); Repaint(w);
            Assert.That(w.ControlScreenRects.TryGetValue(id, out var screen), Is.True, id + " was not drawn");
            var host = new Rect(screen.position - HostScreenPosition(w), screen.size);
            return new Vector2(host.x + host.width * fx, host.center.y);
        }
        static void BakeMouse(MeshBakeWindow w, EventType type, Vector2 at)
        { EditorShaderCompiler.TolerateErrorLogsIfBroken(); w.SendEvent(new Event { type = type, mousePosition = at, button = 0 }); }
        static void BakeClick(MeshBakeWindow w, string id, float fx = .5f)
        { var p = BakeControlPoint(w, id, fx); BakeMouse(w, EventType.MouseDown, p); BakeMouse(w, EventType.MouseUp, p); Repaint(w); }
        /// <summary>スライダーを from から to（左から何割）へドラッグして離す。</summary>
        static void BakeDrag(MeshBakeWindow w, string id, float from, float to)
        {
            var a = BakeControlPoint(w, id, from); var b = new Vector2(a.x + (to - from) * w.ControlScreenRects[id].width, a.y);
            BakeMouse(w, EventType.MouseDown, a); BakeMouse(w, EventType.MouseDrag, Vector2.Lerp(a, b, .5f)); BakeMouse(w, EventType.MouseDrag, b); BakeMouse(w, EventType.MouseUp, b);
            Repaint(w);
        }
        /// <summary>EditorApplication.update の代わりに窓からのベイクを進め、終わるまで待つ。</summary>
        static void PumpBake(TexturePaintWindow w)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (w.PumpMeshBake(20)) { Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(300), "the bake did not finish"); System.Threading.Thread.Sleep(2); }
        }

        [Test] public void TheBakeWindowOpensFromThePanelAndItsWidgetsChangeTheOwnersSettings()
        {
            Invoke(window, "CreateDocument", 256); Invoke(window, "BindDocument");
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True); QuickBake(window);
            OpenLayerPanels(); ShowTextureSetSettings();
            ClickLayerControl("meshmap.bake");
            var bake = MeshBakeWindow.For(window);
            Assert.That(bake, Is.Not.Null, "Bake… in the panel opens the bake window");
            try
            {
                Assert.That(window.OpenMeshBakeWindow(), Is.SameAs(bake), "one bake window per painter (3D ▸ Bake Mesh Maps… brings it to the front)");
                bake.position = new Rect(80, 80, MeshBakeWindow.DefaultWidth, MeshBakeWindow.DefaultHeight);
                // 一覧の項目を押すと右にその設定、スライダーは持ち主の設定を変える
                BakeClick(bake, "bake.page." + MeshMapKind.AmbientOcclusion, .6f);
                Assert.That(bake.Page, Is.EqualTo((int)MeshMapKind.AmbientOcclusion));
                BakeDrag(bake, "bake.ao.rays", .5f, .1f);
                Assert.That(window.MeshBakeSettings.AoSamples, Is.InRange(35, 75), "the rays slider sets the owner's AO samples");
                // チェックで焼くものを選ぶ（一覧のチェックと、右の「このマップをベイク」）
                BakeClick(bake, "bake.kind." + MeshMapKind.Opacity);
                Assert.That(window.MeshBakeSettings.Includes(MeshMapKind.Opacity), Is.True);
                BakeClick(bake, "bake.include");
                Assert.That(window.MeshBakeSettings.Includes(MeshMapKind.AmbientOcclusion), Is.False);
                // 共通の設定
                BakeClick(bake, "bake.page.common");
                Assert.That(bake.Page, Is.EqualTo(MeshBakeWindow.CommonPage));
                BakeDrag(bake, "bake.padding", .25f, .5f);
                Assert.That(window.MeshBakeSettings.Padding, Is.InRange(28, 36));
                // 焼く: 窓の中で進み、チェックしたものが焼ける
                long revision = window.Document.Revision;
                BakeClick(bake, "bake.start");
                Assert.That(window.IsBakingMeshMaps, Is.True, window.StatusMessage);
                Repaint(bake); Assert.That(bake.ControlScreenRects.ContainsKey("bake.cancel"), Is.True, "the bake button turns into Cancel while baking");
                PumpBake(window);
                Assert.That(window.MeshMaps.Maps.Select(m => m.Kind), Is.EquivalentTo(window.MeshBakeSettings.Maps), window.StatusMessage);
                Assert.That(window.MeshMaps.TryGet(MeshMapKind.AmbientOcclusion, out _), Is.False);
                window.MeshMaps.TryGet(MeshMapKind.Opacity, out var opacity);
                Assert.That(opacity.Provenance.Padding, Is.EqualTo(window.MeshBakeSettings.Padding));
                Assert.That(window.Document.Revision, Is.EqualTo(revision));
                Repaint(bake); Repaint(window);
                // Esc で閉じる（次の更新で）
                Key(bake, KeyCode.Escape);
                Assert.That(bake.Closing, Is.True);
                Assert.That(MeshBakeWindow.For(window), Is.Null);
            }
            finally { if (bake != null) bake.Close(); }
        }

        [Test] public void AStrokeRefusesTheBakeAndTheBakeWindowSaysWhy()
        {
            window.Preview.LoadDemoMesh(); QuickBake(window);
            var bake = window.OpenMeshBakeWindow();
            try
            {
                Repaint(bake);
                Assert.That(window.MeshBakeRefusal(), Is.Null);
                BeginLine(400, 400);
                Assert.That(window.MeshBakeRefusal(), Does.Contain("stroke"));
                Repaint(bake); // 窓は下の帯に理由を出し、ベイクのボタンを押せなくする
                Assert.That(window.StartMeshBake(), Does.Contain("stroke"));
                Assert.That(window.BakeMeshMaps(), Is.Null);
                Assert.That(window.IsBakingMeshMaps, Is.False); Assert.That(window.MeshMaps.Count, Is.Zero);
                Key(window, KeyCode.Escape);
                Assert.That(window.IsStroking, Is.False);
                Assert.That(window.MeshBakeRefusal(), Is.Null);
            }
            finally { bake.Close(); }
        }

        [Test] public void ClosingThePainterClosesItsBakeWindowAndStopsTheBake()
        {
            window.Preview.LoadDemoMesh(); QuickBake(window);
            var s = window.MeshBakeSettings; s.Maps = MeshBakeSettings.AllKinds.ToArray(); s.AoSamples = 512; s.Antialiasing = 4;
            var bake = window.OpenMeshBakeWindow();
            Repaint(bake);
            Assert.That(window.StartMeshBake(), Is.Null, window.StatusMessage);
            var job = window.RunningMeshBake;
            Close(window); window = null; // OnDisable: 取り消して、別のスレッドが終わるまで待つ
            Assert.That(job.Work.IsCompleted, Is.True, "the bake thread was stopped and awaited");
            Assert.That(bake.EnsureOwner(false), Is.False, "OnInspectorUpdate makes the same check 10 times a second");
            Assert.That(bake == null, Is.True, "the bake window closed with its painter");
        }
    }
}
