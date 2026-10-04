using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// メッシュマップのベイクの窓（<see cref="MeshBakeWindow"/>）と、窓から走らせるベイク。窓は表示せず（batch-gl で動く）、中身を
    /// オフスクリーンで PNG に描き、ベイクは <see cref="TexturePaintWindow.PumpMeshBake"/> を回して進める（EditorApplication.update の代わり）。
    /// <list type="bullet">
    /// <item>どの項目・状態（モデル無し・ベイク前・高ポリ・ベイク済み・古い・ベイク中）も、英語・日本語、最小と広い大きさで描け、UI の文字を詰めない。</item>
    /// <item>描くだけでは設定が変わらない（欄の範囲の外の値・float に丸められる値も）。</item>
    /// <item>窓から焼いた結果は <see cref="TexturePaintWindow.BakeMeshMaps"/> と同じバイト列（CPU と GPU）。</item>
    /// <item>取消・拒否（モデル無し・ポーズの変更中・ベイク中・マップ無し・予算）・焼いている間の条件の変化では、前のマップが残る。</item>
    /// <item>持ち主を閉じると、走っているベイクは止まり（別のスレッドが終わるまで待つ）、窓も閉じる。参照を失った窓の持ち主の決まり。</item>
    /// </list>
    /// 描いた PNG はテストプロジェクトの Logs/YoluPainterSnapshots/mesh-bake に残す（見て確かめる用。リポジトリには入れない）。
    /// </summary>
    public sealed class MeshBakeWindowTests
    {
        /// <summary>焼く条件はウィンドウの状態としてシリアライズされ、ドメインのリロード（ここでは状態の書き出しと読み戻しで代える）で既定に戻らない。</summary>
        [Test] public void TheBakeSettingsSurviveTheWindowsSerialization()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>(); var copy = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                var s = w.MeshBakeSettings;
                s.Padding = 7; s.Antialiasing = 3; s.AoSamples = 33; s.AoMaxDistance = 0.25; s.Maps = new[] { Yozolab.YoluPainter.Core.MeshMaps.MeshMapKind.Curvature };
                s.Occluders = Yozolab.YoluPainter.Core.MeshMaps.MeshOccluders.TargetSlotOnly; s.ReferenceMatchByName = true;
                string json = UnityEditor.EditorJsonUtility.ToJson(w);
                Assert.That(json, Does.Contain("meshBakeSettings"));
                UnityEditor.EditorJsonUtility.FromJsonOverwrite(json, copy);
                var r = copy.MeshBakeSettings;
                Assert.That((r.Padding, r.Antialiasing, r.AoSamples, r.AoMaxDistance, r.Occluders, r.ReferenceMatchByName), Is.EqualTo((7, 3, 33, 0.25, Yozolab.YoluPainter.Core.MeshMaps.MeshOccluders.TargetSlotOnly, true)));
                Assert.That(r.Maps, Is.EqualTo(new[] { Yozolab.YoluPainter.Core.MeshMaps.MeshMapKind.Curvature }));
                Assert.That(r.KindKey(Yozolab.YoluPainter.Core.MeshMaps.MeshMapKind.AmbientOcclusion), Is.EqualTo(s.KindKey(Yozolab.YoluPainter.Core.MeshMaps.MeshMapKind.AmbientOcclusion)), "the same conditions after the round trip");
            }
            finally
            {
                foreach (var x in new[] { w, copy }) { string recovery = x.RecoveryRoot; Object.DestroyImmediate(x); if (System.IO.Directory.Exists(recovery)) System.IO.Directory.Delete(recovery, true); }
            }
        }

        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "mesh-bake"));
        /// <summary>窓の最小の大きさと、広い大きさ。</summary>
        static readonly (int Width, int Height)[] Sizes = { ((int)MeshBakeWindow.MinWidth, (int)MeshBakeWindow.MinHeight), (1100, 760) };

        TexturePaintWindow owner; MeshBakeWindow bake; string settingsRoot;
        readonly List<Object> made = new List<Object>();

        [SetUp] public void Open()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            // 設定は一時プロジェクトに置く（予算の拒否のテストがテストプロジェクトの UserSettings に書かないように）
            settingsRoot = Path.Combine(Path.GetTempPath(), "yolupainter-bake-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(settingsRoot);
            PainterSettings.ProjectRoot = settingsRoot;
            owner = ScriptableObject.CreateInstance<TexturePaintWindow>();
            owner.MeshBakeProgress = (title, info, progress) => false;
            bake = ScriptableObject.CreateInstance<MeshBakeWindow>();
            bake.Attach(owner);
        }

        [TearDown] public void Close()
        {
            L.OverrideLanguage(PainterLanguage.English);
            if (bake != null) Object.DestroyImmediate(bake);
            if (owner != null)
            {
                string recovery = owner.RecoveryRoot;
                Object.DestroyImmediate(owner);
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
            }
            foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            made.Clear();
            PainterSettings.ProjectRoot = null;
            if (Directory.Exists(settingsRoot)) Directory.Delete(settingsRoot, true);
        }

        // ───────── 描く ─────────

        [Test] public void EveryPageAndStateDrawsInBothLanguagesWithoutShortenedText()
        {
            RequireOffscreen();
            foreach (var (state, setup) in States())
            {
                setup();
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    foreach (int page in Pages)
                    {
                        bake.Page = page;
                        foreach (var (width, height) in Sizes)
                        {
                            string name = state + "-" + (language == PainterLanguage.English ? "en" : "ja") + "-" + PageName(page) + "-" + width;
                            int before = PaintGui.ShortenedTexts;
                            Draw(width, height, name);
                            Assert.That(PaintGui.ShortenedTexts - before, Is.Zero, name + ": a UI text did not fit and was shortened with …");
                        }
                    }
                }
            }
            CancelAndWait();
        }

        /// <summary>描くだけでは設定が変わらない: 欄の部品は float で範囲もあるが、設定は double で、保存したマップから戻した値は範囲の外のこともある。
        /// 触っていない値を丸めて書き戻すと、描くたびにマップが「設定が変わった」と古くなる。</summary>
        [Test] public void DrawingTheWindowLeavesTheSettingsAlone()
        {
            RequireOffscreen();
            owner.Preview.LoadDemoMesh();
            var s = owner.MeshBakeSettings; double third = 1.0 / 3;
            s.AoSamples = 600; s.ThicknessSamples = 700; s.AoMaxDistance = third; s.ThicknessMaxDistance = 2 * third; s.CurvatureRadius = third / 10;
            s.AoSpreadDegrees = 90 + third; s.ThicknessSpreadDegrees = 45 + third; s.ReferenceFrontal = .3 + third / 100; s.ReferenceRear = third / 1000; s.Padding = 3;
            s.Maps = new[] { MeshMapKind.Position, MeshMapKind.Id };
            string Key() => string.Join("|", MeshBakeSettings.AllKinds.Select(s.KindKey)) + "|" + s.SourceKey("high") + "|" + s.Padding + "|" + s.Antialiasing + "|" + string.Join(",", s.Maps) + "|" + owner.MeshBakeUseGpu;
            string before = Key();
            foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
            {
                L.OverrideLanguage(language);
                foreach (int page in Pages) { bake.Page = page; Draw(Sizes[1].Width, Sizes[1].Height, "unchanged-" + language + "-" + PageName(page)); }
            }
            Assert.That(Key(), Is.EqualTo(before), "drawing the window changed the bake settings");
            Assert.That(owner.MeshMaps.Count, Is.Zero); Assert.That(owner.IsBakingMeshMaps, Is.False);
        }

        // ───────── 設定は持ち主のもの ─────────

        [Test] public void TheWindowEditsTheOwnersSettingsInPlace()
        {
            UseSmallDocument(256);
            owner.Preview.LoadDemoMesh();
            var s = owner.MeshBakeSettings;
            Assert.That(s.Maps, Is.EqualTo(MeshBakeSettings.DefaultKinds), "the window starts from the owner's checked maps");
            owner.SetMeshBakeKind(MeshMapKind.Opacity, true); owner.SetMeshBakeKind(MeshMapKind.Position, false);
            Assert.That(owner.MeshBakeSettings, Is.SameAs(s), "the window does not copy the settings");
            Assert.That(s.Maps, Is.EqualTo(new[] { MeshMapKind.WorldNormal, MeshMapKind.AmbientOcclusion, MeshMapKind.Curvature, MeshMapKind.Thickness, MeshMapKind.Opacity }), "kept in the list order");
            s.Maps = new[] { MeshMapKind.Curvature };
            owner.SetMeshBakeKind(MeshMapKind.Curvature, false);
            Assert.That(s.Maps, Is.EqualTo(new[] { MeshMapKind.Curvature }), "the last checked map stays checked");
            // 窓で変えた値で焼いたマップの由来に入り、変えると古くなる（プロパティの欄・保存と同じ値）
            s.CurvatureRadius = .05;
            Assert.That(owner.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), owner.StatusMessage);
            Assert.That(owner.MeshMaps.TryGet(MeshMapKind.Curvature, out var map), Is.True);
            Assert.That(map.Provenance.SettingsKey, Is.EqualTo(s.KindKey(MeshMapKind.Curvature)));
            s.CurvatureRadius = .06;
            Assert.That(owner.MeshMaps.Check(MeshMapKind.Curvature, owner.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Stale));
            bake.Page = (int)MeshMapKind.Curvature; Assert.That(bake.Page, Is.EqualTo((int)MeshMapKind.Curvature));
            Assert.That(owner.MeshBakeTargets().Single().Slots, Is.EqualTo(new[] { 0 }), "one texture set: the slots of the open document's material");
        }

        // ───────── 窓から焼く ─────────

        [TestCase(false)]
        [TestCase(true)]
        public void BakingFromTheWindowGivesTheSameMapsAsBakeMeshMaps(bool gpu)
        {
            if (gpu)
            {
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics). Run with test-daemon.sh start --batch-gl.");
                if (EditorShaderCompiler.IsBroken) Assert.Ignore("Built-in shaders fail to compile in this Editor (devcontainer GUI mode). Use --batch-gl.");
                string why = GpuMeshBakeRayTracer.Unavailable();
                if (why != null) Assert.Ignore("GPU mesh-map baking is unavailable here: " + why);
            }
            UseSmallDocument(256);
            owner.Preview.LoadDemoMesh();
            var s = owner.MeshBakeSettings;
            s.Maps = new[] { MeshMapKind.WorldNormal, MeshMapKind.AmbientOcclusion, MeshMapKind.Curvature, MeshMapKind.Thickness, MeshMapKind.Id, MeshMapKind.BentNormal };
            s.AoSamples = 16; s.ThicknessSamples = 16; s.Antialiasing = 2; s.Padding = 4;
            owner.MeshBakeUseGpu = gpu;
            var d = owner.Document; long revision = d.Revision; int undo = d.UndoCount;
            Assert.That(owner.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), owner.StatusMessage);
            var modal = owner.MeshMaps.Maps.ToDictionary(m => m.Kind, MeshMapBinary.Write);
            string modalBackend = owner.LastMeshBakeReport.RayBackend;
            Assert.That(modalBackend.StartsWith("GPU", StringComparison.Ordinal), Is.EqualTo(gpu), modalBackend);
            owner.MeshMaps.Clear();

            Assert.That(owner.StartMeshBake(), Is.Null, owner.StatusMessage);
            Assert.That(owner.IsBakingMeshMaps, Is.True);
            PumpUntilDone();
            Assert.That(owner.MeshMaps.Count, Is.EqualTo(s.Maps.Length), owner.StatusMessage);
            Assert.That(owner.LastMeshBakeReport.RayBackend, Is.EqualTo(modalBackend));
            foreach (var map in owner.MeshMaps.Maps)
                Assert.That(MeshMapBinary.Write(map), Is.EqualTo(modal[map.Kind]), map.Kind + ": the window's bake differs from BakeMeshMaps");
            Assert.That(owner.StatusMessage, Does.Contain("Baked"));
            Assert.That(owner.MeshBakeOutcome, Does.Contain("Baked 6 map(s)"));
            Assert.That((d.Revision, d.UndoCount), Is.EqualTo((revision, undo)), "baking does not touch the document or its undo");
            Assert.That(owner.MeshMapsSaved, Is.False, "baked maps are not in a file yet");
            foreach (var map in owner.MeshMaps.Maps) Assert.That(map.Provenance.Check(owner.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current));
        }

        [Test] public void CancelingAWindowBakeKeepsThePreviousMaps()
        {
            UseSmallDocument(256);
            owner.Preview.LoadDemoMesh();
            var s = owner.MeshBakeSettings; s.Maps = new[] { MeshMapKind.WorldNormal };
            Assert.That(owner.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed));
            owner.MeshMaps.TryGet(MeshMapKind.WorldNormal, out var before); long revision = owner.MeshMaps.Revision;
            s.Maps = MeshBakeSettings.AllKinds.ToArray(); s.AoSamples = 512; s.ThicknessSamples = 512; s.Antialiasing = 4; owner.MeshBakeUseGpu = false;
            Assert.That(owner.StartMeshBake(), Is.Null);
            owner.CancelMeshBake();
            Assert.That(owner.RunningMeshBake.Canceling, Is.True);
            PumpUntilDone();
            Assert.That(owner.IsBakingMeshMaps, Is.False);
            Assert.That(owner.StatusMessage, Does.Contain("canceled")); Assert.That(owner.MeshBakeOutcome, Does.Contain("canceled"));
            Assert.That(owner.MeshMaps.Revision, Is.EqualTo(revision)); Assert.That(owner.MeshMaps.Count, Is.EqualTo(1));
            Assert.That(owner.MeshMaps.TryGet(MeshMapKind.WorldNormal, out var after) && ReferenceEquals(after, before), Is.True, "nothing half-baked replaced the map");
            Assert.That(owner.StartMeshBake(), Is.Null, "a new bake can start after the cancel");
            CancelAndWait();
        }

        [Test] public void TheWindowRefusesForTheSameReasonsAsBakeMeshMaps()
        {
            // モデルが無い
            Assert.That(owner.MeshBakeRefusal(), Does.Contain("No model"));
            Assert.That(owner.StartMeshBake(), Does.Contain("No model")); Assert.That(owner.IsBakingMeshMaps, Is.False);
            Assert.That(owner.BakeMeshMaps(), Is.Null); Assert.That(owner.StatusMessage, Does.Contain("No model"));
            owner.Preview.LoadDemoMesh();
            Assert.That(owner.MeshBakeRefusal(), Is.Null);
            // ポーズの変更中（スライダーを離すまでスナップショットは古い）
            var pending = typeof(TexturePaintWindow).GetField("posePending", BindingFlags.NonPublic | BindingFlags.Instance);
            pending.SetValue(owner, true);
            Assert.That(owner.StartMeshBake(), Does.Contain("pose")); Assert.That(owner.BakeMeshMaps(), Is.Null); Assert.That(owner.StatusMessage, Does.Contain("pose"));
            pending.SetValue(owner, false);
            // 焼くマップが無い（保存したマップから戻した設定などで）
            owner.MeshBakeSettings.Maps = new MeshMapKind[0];
            Assert.That(owner.StartMeshBake(), Does.Contain("No map is checked"));
            owner.MeshBakeSettings.Maps = new[] { MeshMapKind.AmbientOcclusion }; owner.MeshBakeSettings.AoSamples = 512; owner.MeshBakeUseGpu = false;
            // ベイク中はもう 1 つ始めない（窓からも、呼んだ所で待つベイクからも）
            Assert.That(owner.StartMeshBake(), Is.Null);
            Assert.That(owner.StartMeshBake(), Does.Contain("already"));
            Assert.That(owner.BakeMeshMaps(), Is.Null); Assert.That(owner.StatusMessage, Does.Contain("already"));
            CancelAndWait();
            // 予算を超える: 始めたベイクが何も割り当てずに断り、窓の下の帯に理由が出る
            PainterSettings.UpdatePersonal(p => p.strokeBudgetMiB = PainterSettings.MinStrokeMiB);
            owner.MeshBakeSettings.Maps = MeshBakeSettings.DefaultKinds.ToArray();
            Assert.That(owner.StartMeshBake(), Is.Null);
            PumpUntilDone();
            Assert.That(owner.MeshBakeOutcome, Does.Contain("budget")); Assert.That(owner.StatusMessage, Does.Contain("budget"));
            Assert.That(owner.MeshMaps.Count, Is.Zero);
        }

        [Test] public void AChangeWhileBakingDiscardsTheResult()
        {
            UseSmallDocument(128);
            owner.Preview.LoadDemoMesh();
            owner.MeshBakeSettings.Maps = new[] { MeshMapKind.WorldNormal, MeshMapKind.AmbientOcclusion }; owner.MeshBakeSettings.AoSamples = 8;
            var set = owner.CurrentTextureSet;
            // マテリアルのスロット（焼いた結果は始めたときのスロットのもの）
            Assert.That(owner.StartMeshBake(), Is.Null);
            set.Bind(0, new[] { 1 });
            PumpUntilDone();
            Assert.That(owner.MeshMaps.Count, Is.Zero); Assert.That(owner.MeshBakeOutcome, Does.Contain("material slot"));
            set.Bind(0, new[] { 0 });
            // モデルのスナップショット（読み込み直し・ポーズ）
            Assert.That(owner.StartMeshBake(), Is.Null);
            owner.Preview.LoadDemoMesh();
            PumpUntilDone();
            Assert.That(owner.MeshMaps.Count, Is.Zero); Assert.That(owner.MeshBakeOutcome, Does.Contain("model"));
            // 焼いている間の設定の変更は結果を捨てない（由来に始めたときの設定が残り、今の設定とは違うので古いと出る）
            Assert.That(owner.StartMeshBake(), Is.Null);
            owner.MeshBakeSettings.AoSamples = 9;
            PumpUntilDone();
            Assert.That(owner.MeshMaps.Count, Is.EqualTo(2), owner.MeshBakeOutcome);
            Assert.That(owner.MeshMaps.Check(MeshMapKind.AmbientOcclusion, owner.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Stale));
            Assert.That(owner.MeshMaps.Check(MeshMapKind.WorldNormal, owner.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current));
            // ドキュメントを替えると、走っているベイクは止まり、マップは持ち越さない
            Assert.That(owner.StartMeshBake(), Is.Null);
            var job = owner.RunningMeshBake;
            UseSmallDocument(128);
            Assert.That(owner.IsBakingMeshMaps, Is.False); Assert.That(job.Work.IsCompleted, Is.True, "the worker thread has finished");
            Assert.That(owner.StatusMessage, Does.Contain("document changed")); Assert.That(owner.MeshMaps.Count, Is.Zero);
        }

        // ───────── 持ち主 ─────────

        [Test] public void ClosingTheOwnerStopsTheBakeAndClosesTheWindow()
        {
            UseSmallDocument(256);
            owner.Preview.LoadDemoMesh();
            owner.MeshBakeSettings.Maps = MeshBakeSettings.AllKinds.ToArray(); owner.MeshBakeSettings.AoSamples = 512; owner.MeshBakeSettings.Antialiasing = 4;
            Assert.That(owner.StartMeshBake(), Is.Null);
            var job = owner.RunningMeshBake;
            Assert.That(MeshBakeWindow.For(owner), Is.SameAs(bake));
            Assert.That(bake.EnsureOwner(false), Is.True);
            string recovery = owner.RecoveryRoot;
            Object.DestroyImmediate(owner); owner = null; // OnDisable: 取り消して、別のスレッドが終わるまで待つ
            if (Directory.Exists(recovery)) Directory.Delete(recovery, true);
            Assert.That(job.Work.IsCompleted, Is.True, "the bake thread was stopped and awaited");
            Assert.That(job.Canceling, Is.True);
            Assert.That(bake.EnsureOwner(false), Is.False);
            Assert.That(bake == null, Is.True, "the bake window closed with its owner");
        }

        [Test] public void AWindowThatLostItsOwnerReconnectsOnlyToTheOnlyPainter()
        {
            var a = owner; var b = ScriptableObject.CreateInstance<TexturePaintWindow>(); made.Add(b);
            try
            {
                Assert.That(MeshBakeWindow.ChooseOwner(a, true, null, out var chosen), Is.EqualTo(MeshBakeWindow.Link.Attached)); Assert.That(chosen, Is.SameAs(a));
                Assert.That(MeshBakeWindow.ChooseOwner(null, true, new[] { a }, out chosen), Is.EqualTo(MeshBakeWindow.Link.Close), "the owner closed during this session");
                Assert.That(MeshBakeWindow.ChooseOwner(null, false, new[] { a }, out chosen), Is.EqualTo(MeshBakeWindow.Link.Adopted)); Assert.That(chosen, Is.SameAs(a));
                Assert.That(MeshBakeWindow.ChooseOwner(null, false, new[] { a, b }, out chosen), Is.EqualTo(MeshBakeWindow.Link.Close), "which document to bake for is ambiguous");
                Assert.That(MeshBakeWindow.ChooseOwner(null, false, new TexturePaintWindow[0], out chosen), Is.EqualTo(MeshBakeWindow.Link.Close));
                // 実際の窓: 参照を失った（再起動・レイアウトの復元の後と同じ）。ペイントの窓が 2 つ以上あるので決まらず閉じる
                bake.ForgetOwnerForTests();
                Assert.That(bake.EnsureOwner(false), Is.False);
                Assert.That(bake == null, Is.True);
            }
            finally { string recovery = b != null ? b.RecoveryRoot : null; if (b != null) Object.DestroyImmediate(b); if (recovery != null && Directory.Exists(recovery)) Directory.Delete(recovery, true); }
        }

        // ───────── 状態 ─────────

        static readonly int[] Pages = new[] { MeshBakeWindow.CommonPage }.Concat(MeshBakeSettings.AllKinds.Select(k => (int)k)).ToArray();
        static string PageName(int page) => page == MeshBakeWindow.CommonPage ? "common" : ((MeshMapKind)page).ToString();

        /// <summary>描く状態: モデル無し（理由を出す）、ベイク前、高ポリ、ベイク済み（全部が最新）、古い、ベイク中（進み具合と取消）。</summary>
        IEnumerable<(string, Action)> States()
        {
            yield return ("no-model", () => { });
            yield return ("ready", () => { UseSmallDocument(128); owner.Preview.LoadDemoMesh(); });
            yield return ("high-poly", () =>
            {
                var material = new Material(Shader.Find("Hidden/YoluPainter/PreviewSurface")); made.Add(material);
                var high = new GameObject("High cube") { hideFlags = HideFlags.HideAndDontSave }; made.Add(high); high.transform.localScale = Vector3.one * 1.02f;
                high.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx"); high.AddComponent<MeshRenderer>().sharedMaterial = material;
                owner.HighPolyModel = high;
            });
            yield return ("baked", () =>
            {
                owner.MeshBakeSettings.Maps = MeshBakeSettings.AllKinds.ToArray(); owner.MeshBakeSettings.AoSamples = 4; owner.MeshBakeSettings.ThicknessSamples = 4;
                Assert.That(owner.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), owner.StatusMessage);
                Assert.That(owner.LastMeshBakeReport, Is.Not.Null);
            });
            yield return ("stale", () => { owner.MeshBakeSettings.Padding = 3; owner.MeshBakeSettings.AoSamples = 5; owner.HighPolyModel = null; });
            yield return ("baking", () =>
            {
                owner.MeshBakeSettings.AoSamples = 512; owner.MeshBakeSettings.Antialiasing = 4; owner.MeshBakeUseGpu = false;
                Assert.That(owner.StartMeshBake(), Is.Null);
                owner.PumpMeshBake(1);
            });
        }

        // ───────── 補助 ─────────

        static void RequireOffscreen()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
        }

        void Draw(int width, int height, string name)
        {
            string path = Path.Combine(Folder, name + ".png");
            OffscreenGui.RenderToPng(width, height, () => owner.DrawMeshBakeWindow(new Rect(0, 0, width, height), bake), path, PaintTheme.PanelBg);
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, name);
                int colors = texture.GetPixels32().Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Take(100).Count();
                Assert.That(colors, Is.GreaterThan(30), name + ": the window looks empty");
            }
            finally { Object.DestroyImmediate(texture); }
        }

        /// <summary>小さいドキュメントにする（ベイクの時間を短くする。新しいドキュメントと同じ道: マップは持ち越さない）。</summary>
        void UseSmallDocument(int size)
        {
            var type = typeof(TexturePaintWindow); var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            type.GetMethod("CreateDocument", flags).Invoke(owner, new object[] { size });
            type.GetMethod("BindDocument", flags).Invoke(owner, null);
        }

        /// <summary>EditorApplication.update の代わりにベイクを進め、終わるまで待つ。</summary>
        void PumpUntilDone(double seconds = 300)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (owner.PumpMeshBake(20))
            {
                if (clock.Elapsed.TotalSeconds > seconds) { CancelAndWait(); Assert.Fail("The bake did not finish in " + seconds + " s."); }
                System.Threading.Thread.Sleep(2);
            }
        }

        void CancelAndWait() { if (owner == null || !owner.IsBakingMeshMaps) return; owner.CancelMeshBake(); PumpUntilDone(); }
    }
}
