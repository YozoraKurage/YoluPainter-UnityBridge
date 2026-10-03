using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// グループ（分離・通過・クリッピング・マスク・入れ子）の GPU 合成を CPU の正本（CpuCompositor）と突き合わせる。
    /// 許容は GpuTests と同じ 1。合成を重ねると前段の丸めの差が次段に入るので、背景は不透明にし、アルファの小さい半透明どうしを
    /// 重ねない（アルファが小さいと色の重みがアルファの 1 の差で大きく変わり、比較が丸めの検査でなくなる）。
    /// どの場合も CPU に回るタイルが無いこと（LastCpuTileCount == 0）も確かめる。
    /// </summary>
    [Category("GPU")]
    public sealed class GpuGroupTests
    {
        const string ShaderName = "Hidden/YoluPainter/TileComposite";

        static byte V(int i) => (byte)(i <= 0 ? 0 : i >= 15 ? 255 : i * 17);
        static Rgba32 Backdrop(int x, int y) => new Rgba32(V(x / 3), V(y / 3), V((x + y) / 6), 255);
        static Rgba32 PatternA(int x, int y) => (x + 2 * y) % 7 == 0 ? new Rgba32(0, 0, 0, 0) : new Rgba32(V(15 - x / 3), V((x * 3 + y) % 16), V(y / 3), (byte)(x < 20 ? 255 : 160));
        static Rgba32 PatternB(int x, int y) => y % 5 == 0 ? new Rgba32(0, 0, 0, 0) : new Rgba32(V((x + y) % 16), V(x / 3), V(15 - y / 3), (byte)(y < 24 ? 200 : 255));
        static Rgba32 PatternC(int x, int y) => new Rgba32(V(y % 16), V(15 - (x + y) % 16), V(x % 16), (byte)((x / 8 + y / 8) % 2 == 0 ? 255 : 128));

        static PaintLayer Raster(PaintDocument doc, string name, Func<int, int, Rgba32> pattern, int width = 48, int height = 48)
        {
            var layer = doc.AddLayer(name); var surface = layer.GetChannel(PaintChannel.Color);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { var c = pattern(x, y); if (c.A != 0) surface.SetPixel(x, y, c); }
            return layer;
        }

        static void PaintMask(PaintDocument doc, Guid id, Func<int, int, byte> hide, int size = 48)
        {
            var mask = doc.AddLayerMask(id);
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { byte h = hide(x, y); if (h != 0) mask.Surface.SetPixel(x, y, new Rgba32(0, 0, 0, h)); }
        }

        /// <summary>新しい GPU 合成器で全タイルを合成し、CPU の正本と比べる。CPU に回ったタイルが無いことも確かめる。</summary>
        static TileGpuCompositor Check(PaintDocument doc, string context, bool allowCopyTexture = true, PaintChannel channel = PaintChannel.Color)
        {
            var compositor = new TileGpuCompositor(allowCopyTexture: allowCopyTexture);
            try
            {
                compositor.Update(doc, channel);
                Assert.That(compositor.Backend, Does.StartWith("CPU source brush / GPU"), compositor.Backend);
                GpuTests.AssertMatches(doc.Composite(channel), GpuTests.Read(compositor.Texture), context);
                Assert.That(compositor.LastCpuTileCount, Is.Zero, context + ": no tile is composited on the CPU");
                return compositor;
            }
            catch { compositor.Dispose(); throw; }
        }

        [SetUp] public void RequireGpu() { GpuTests.RequireWorkingShader(ShaderName); }

        // ───────────── 分離グループ・通過グループ ─────────────

        [TestCase(LayerBlendMode.Normal, 1.0)]
        [TestCase(LayerBlendMode.Multiply, .7)]
        [TestCase(LayerBlendMode.Screen, .55)]
        [TestCase(LayerBlendMode.Overlay, 1.0)]
        public void AnIsolatedGroupCompositesItsContentsFromTransparentThenBlendsLikeALayer(LayerBlendMode mode, double opacity)
        {
            var doc = new PaintDocument(48, 48, 16);
            Raster(doc, "Backdrop", Backdrop);
            var a = Raster(doc, "A", PatternA); var b = Raster(doc, "B", PatternB);
            doc.SetLayerBlendMode(b.Id, LayerBlendMode.Screen); doc.SetLayerOpacity(b.Id, .8);
            var group = doc.GroupLayers(new[] { a.Id, b.Id }, "Isolated");
            doc.SetLayerBlendMode(group.Id, mode); doc.SetLayerOpacity(group.Id, opacity);
            using (Check(doc, "isolated " + mode)) { }
        }

        [TestCase(1.0)]
        [TestCase(.6)]
        [TestCase(0.0001)]
        public void APassThroughGroupCompositesOntoTheBackdropThenFades(double opacity)
        {
            var doc = new PaintDocument(48, 48, 16);
            Raster(doc, "Backdrop", Backdrop);
            var a = Raster(doc, "A", PatternA); var b = Raster(doc, "B", PatternB);
            doc.SetLayerBlendMode(a.Id, LayerBlendMode.Multiply); doc.SetLayerOpacity(b.Id, .7);
            var group = doc.GroupLayers(new[] { a.Id, b.Id }, "Pass");
            Assert.That(group.BlendMode, Is.EqualTo(LayerBlendMode.PassThrough));
            doc.SetLayerOpacity(group.Id, opacity);
            using (var c = Check(doc, "pass-through " + opacity))
                Assert.That(c.NestedRenderTextureCount, Is.EqualTo(opacity >= 1 ? 0 : 2), "full opacity without a mask composites in place; otherwise one level for the fade");
        }

        /// <summary>調整レイヤーは、通過グループの中では下（グループの外）にも効き、分離グループの中では中身にだけ効く。
        /// 両者が CPU でも違う結果になることを確かめたうえで、どちらも GPU と一致すること。</summary>
        [Test] public void AnAdjustmentAffectsTheBackdropOnlyThroughAPassThroughGroup()
        {
            byte[] Build(LayerBlendMode groupMode, double opacity, out PaintDocument doc)
            {
                doc = new PaintDocument(48, 48, 16);
                Raster(doc, "Backdrop", Backdrop);
                var a = Raster(doc, "A", (x, y) => x < 24 ? PatternA(x, y) : new Rgba32(0, 0, 0, 0));
                var adjust = doc.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels(.1, .85, 1.4, .05, .95));
                doc.SetLayerOpacity(adjust.Id, .8);
                var invert = doc.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert()); doc.SetLayerBlendMode(invert.Id, LayerBlendMode.Multiply); doc.SetLayerOpacity(invert.Id, .5);
                var group = doc.GroupLayers(new[] { a.Id, adjust.Id, invert.Id }, "G");
                doc.SetLayerBlendMode(group.Id, groupMode); doc.SetLayerOpacity(group.Id, opacity);
                return doc.Composite(PaintChannel.Color);
            }
            var pass = Build(LayerBlendMode.PassThrough, 1, out var passDoc);
            var isolated = Build(LayerBlendMode.Normal, 1, out var isolatedDoc);
            Assert.That(pass, Is.Not.EqualTo(isolated), "the reference distinguishes the two");
            using (Check(passDoc, "adjustment in pass-through (in place)")) { }
            using (Check(isolatedDoc, "adjustment in isolated group")) { }
            Build(LayerBlendMode.PassThrough, .65, out var fadedDoc);
            using (Check(fadedDoc, "adjustment in faded pass-through")) { }
        }

        [Test] public void MasksOnGroupsHideTheirResult()
        {
            var doc = new PaintDocument(48, 48, 16);
            Raster(doc, "Backdrop", Backdrop);
            var a = Raster(doc, "A", PatternA); var b = Raster(doc, "B", PatternB); var c = Raster(doc, "C", PatternC);
            var isolated = doc.GroupLayers(new[] { a.Id }, "Isolated"); doc.SetLayerBlendMode(isolated.Id, LayerBlendMode.Multiply);
            PaintMask(doc, isolated.Id, (x, y) => (byte)(x * 5)); doc.SetLayerMaskDensity(isolated.Id, .8);
            var pass = doc.GroupLayers(new[] { b.Id }, "Pass"); doc.SetLayerOpacity(pass.Id, .9);
            PaintMask(doc, pass.Id, (x, y) => (byte)(y < 16 ? 0 : y * 4)); doc.SetLayerMaskInverted(pass.Id, true);
            var whole = doc.GroupLayers(new[] { c.Id }, "Pass at full opacity, masked");
            PaintMask(doc, whole.Id, (x, y) => (byte)((x + y) % 3 == 0 ? 255 : 40));
            using (var compositor = Check(doc, "group masks"))
                Assert.That(compositor.NestedRenderTextureCount, Is.EqualTo(2), "one level, reused by the three groups (a masked pass-through group fades)");
        }

        // ───────────── クリッピング ─────────────

        [Test] public void AGroupCanBeAClipBaseAndClippedLayersStayInsideIt()
        {
            var doc = new PaintDocument(48, 48, 16);
            Raster(doc, "Backdrop", Backdrop);
            var a = Raster(doc, "A", (x, y) => x > 6 && x < 40 ? PatternA(x, y) : new Rgba32(0, 0, 0, 0));
            var group = doc.GroupLayers(new[] { a.Id }, "Base group"); // 通過だが、クリッピングがあるので分離（Normal）として扱う
            doc.SetLayerOpacity(group.Id, .85);
            var clipped = Raster(doc, "Clipped", PatternC); doc.SetLayerClipping(clipped.Id, true);
            doc.SetLayerBlendMode(clipped.Id, LayerBlendMode.Overlay); doc.SetLayerOpacity(clipped.Id, .9);
            var adjust = doc.AddAdjustmentLayer("Clipped invert", AdjustmentSettings.Invert()); doc.SetLayerClipping(adjust.Id, true); doc.SetLayerOpacity(adjust.Id, .4);
            Assert.That(CpuCompositor.Plan(doc, PaintChannel.Color)[1].PassesThrough, Is.False, "a group with clipped layers is isolated");
            using (Check(doc, "group as clip base")) { }
        }

        [Test] public void AClippedGroupCompositesInIsolationAndClipsOntoItsBase()
        {
            var doc = new PaintDocument(48, 48, 16);
            Raster(doc, "Backdrop", Backdrop);
            var baseLayer = Raster(doc, "Base", (x, y) => y > 4 && y < 44 ? PatternB(x, y) : new Rgba32(0, 0, 0, 0));
            var a = Raster(doc, "A", PatternA); var c = Raster(doc, "C", PatternC);
            doc.SetLayerBlendMode(c.Id, LayerBlendMode.Multiply);
            var group = doc.GroupLayers(new[] { a.Id, c.Id }, "Clipped group");
            doc.SetLayerClipping(group.Id, true); doc.SetLayerBlendMode(group.Id, LayerBlendMode.Screen); doc.SetLayerOpacity(group.Id, .75);
            PaintMask(doc, group.Id, (x, y) => (byte)(x < 24 ? 0 : 180));
            Assert.That(CpuCompositor.Plan(doc, PaintChannel.Color)[1].ClipEntries.Single().Base, Is.EqualTo(group));
            using (Check(doc, "clipped group")) { }
            doc.SetLayerBlendMode(group.Id, LayerBlendMode.PassThrough);
            using (Check(doc, "clipped pass-through group (composited as Normal)")) { }
        }

        // ───────────── 合成モード ─────────────

        /// <summary>すべての合成モードを、分離グループの合成と、通過グループの中身（フェードあり）で 1 段ずつ確かめる。
        /// 分離グループの中身は不透明なレイヤー 1 枚（透明の上に置くだけなので丸めは入らない）。</summary>
        [Test] public void EveryBlendModeMatchesThroughGroups()
        {
            foreach (LayerBlendMode mode in Enum.GetValues(typeof(LayerBlendMode)))
                foreach (var path in new[] { "isolated", "inside pass-through" })
                {
                    if (mode == LayerBlendMode.PassThrough && path != "isolated") continue;
                    var doc = new PaintDocument(32, 32, 16);
                    Raster(doc, "Backdrop", Backdrop, 32, 32);
                    var over = Raster(doc, "Over", (x, y) => new Rgba32(V(y / 2), V(15 - x / 2), V((x * 3 + y) % 16), (byte)(y < 8 ? 0 : 255)), 32, 32);
                    var group = doc.GroupLayers(new[] { over.Id }, "G");
                    if (path == "isolated") { doc.SetLayerBlendMode(group.Id, mode); doc.SetLayerOpacity(group.Id, .85); }
                    else { doc.SetLayerBlendMode(over.Id, mode); doc.SetLayerOpacity(group.Id, .6); }
                    using (Check(doc, mode + " / " + path)) { }
                }
        }

        // ───────────── 入れ子と段の上限 ─────────────

        [Test] public void NestedGroupsFourLevelsDeepMatch()
        {
            var doc = new PaintDocument(48, 48, 16);
            Raster(doc, "Backdrop", Backdrop);
            var a = Raster(doc, "A", PatternA); var b = Raster(doc, "B", PatternB); var c = Raster(doc, "C", PatternC);
            var adjust = doc.AddAdjustmentLayer("HSL", AdjustmentSettings.HueSaturation(40, -.2, .05)); doc.SetLayerOpacity(adjust.Id, .7);
            var g4 = doc.GroupLayers(new[] { c.Id }, "G4 isolated multiply masked"); doc.SetLayerBlendMode(g4.Id, LayerBlendMode.Multiply);
            PaintMask(doc, g4.Id, (x, y) => (byte)(x * 4));
            var g3 = doc.GroupLayers(new[] { b.Id, g4.Id, adjust.Id }, "G3 pass-through whole");
            var g2 = doc.GroupLayers(new[] { a.Id, g3.Id }, "G2 isolated screen"); doc.SetLayerBlendMode(g2.Id, LayerBlendMode.Screen); doc.SetLayerOpacity(g2.Id, .9);
            var g1 = doc.GroupLayers(new[] { g2.Id }, "G1 pass-through faded"); doc.SetLayerOpacity(g1.Id, .8);
            Assert.That(doc.DepthOf(c.Id), Is.EqualTo(4));
            Assert.That(TileGpuCompositor.LevelsNeeded(CpuCompositor.Plan(doc, PaintChannel.Color)), Is.EqualTo(3), "G1 fades (1), G2 isolated (2), G3 in place, G4 isolated (3)");
            using (var compositor = Check(doc, "four levels"))
                Assert.That(compositor.NestedRenderTextureCount, Is.EqualTo(6), "three levels of one ping-pong pair each, made on demand");
        }

        static PaintDocument Nested(int depth, int tileSize = 16, int size = 48)
        {
            var doc = new PaintDocument(size, size, tileSize);
            Raster(doc, "Backdrop", (x, y) => x < 40 ? Backdrop(x, y) : new Rgba32(0, 0, 0, 0));
            var inner = Raster(doc, "Inner", (x, y) => x < 30 ? PatternA(x, y) : new Rgba32(0, 0, 0, 0));
            Guid current = inner.Id;
            for (int i = 0; i < depth; i++)
            {
                var g = doc.GroupLayers(new[] { current }, "G" + i);
                doc.SetLayerBlendMode(g.Id, i % 2 == 0 ? LayerBlendMode.Normal : LayerBlendMode.Screen); doc.SetLayerOpacity(g.Id, .92);
                current = g.Id;
            }
            Raster(doc, "Outside", (x, y) => x >= 32 ? PatternB(x, y) : new Rgba32(0, 0, 0, 0));
            return doc;
        }

        [Test] public void NestingUpToTheLimitStaysOnTheGpuWithBoundedRenderTextures()
        {
            var doc = Nested(TileGpuCompositor.MaxNestedLevels);
            Assert.That(TileGpuCompositor.LevelsNeeded(CpuCompositor.Plan(doc, PaintChannel.Color)), Is.EqualTo(TileGpuCompositor.MaxNestedLevels));
            using (var compositor = Check(doc, "at the limit"))
            {
                Assert.That(compositor.NestedLevelLimit, Is.EqualTo(TileGpuCompositor.MaxNestedLevels));
                Assert.That(compositor.NestedRenderTextureCount, Is.EqualTo(2 * TileGpuCompositor.MaxNestedLevels));
                Assert.That(compositor.Backend, Does.Not.Contain("exceed"));
            }
        }

        [Test] public void NestingBeyondTheLimitFallsBackToTheCpuForTheTilesItTouches()
        {
            var doc = Nested(TileGpuCompositor.MaxNestedLevels + 1);
            using (var compositor = new TileGpuCompositor())
            {
                compositor.Update(doc, PaintChannel.Color);
                GpuTests.AssertMatches(doc.Composite(PaintChannel.Color), GpuTests.Read(compositor.Texture), "beyond the limit");
                Assert.That(compositor.Backend, Does.StartWith("CPU source brush / GPU").And.Contain("exceed the GPU limit of " + TileGpuCompositor.MaxNestedLevels));
                Assert.That(compositor.LastCpuTileCount, Is.GreaterThan(0));
                Assert.That(compositor.LastCpuTileCount, Is.LessThan(compositor.LastUpdatedTileCount), "tiles the deep group does not touch stay on the GPU");
                Assert.That(compositor.NestedRenderTextureCount, Is.Zero, "no render textures are made for levels beyond the limit");
            }
        }

        /// <summary>大きなタイルでは、作業タイルの予算（32 MiB）で段数が減り、それを超える入れ子は CPU に回る。</summary>
        [Test] public void LargeTilesLowerTheLevelLimitByTheMemoryBudget()
        {
            var doc = Nested(3, 1024, 64);
            using (var compositor = new TileGpuCompositor())
            {
                compositor.Update(doc, PaintChannel.Color);
                Assert.That(compositor.NestedLevelLimit, Is.EqualTo(2), "32 MiB / (4 render textures × 1024² × 4 bytes)");
                Assert.That(compositor.Backend, Does.Contain("exceed the GPU limit of 2"));
                Assert.That(compositor.NestedRenderTextureCount, Is.Zero);
                GpuTests.AssertMatches(doc.Composite(PaintChannel.Color), GpuTests.Read(compositor.Texture), "large tiles");
            }
        }

        // ───────────── 差分更新・描き込みコピー・CPU 代替 ─────────────

        [TestCase(true)]
        [TestCase(false)]
        public void IncrementalUpdatesWithGroupEditsMatch(bool allowCopyTexture)
        {
            var doc = new PaintDocument(80, 48, 16);
            Raster(doc, "Backdrop", Backdrop, 80, 48);
            var a = Raster(doc, "A", (x, y) => x < 40 ? PatternA(x, y) : new Rgba32(0, 0, 0, 0), 80, 48);
            var b = Raster(doc, "B", (x, y) => x >= 24 && x < 64 ? PatternB(x, y) : new Rgba32(0, 0, 0, 0), 80, 48);
            var c = Raster(doc, "C", (x, y) => x >= 48 ? PatternC(x, y) : new Rgba32(0, 0, 0, 0), 80, 48);
            var g = doc.GroupLayers(new[] { a.Id, b.Id }, "G");
            doc.ClearHistory();
            var brush = new BrushSettings { Radius = 3, Hardness = .6, Opacity = .9, Color = new Rgba32(30, 200, 120), PressureSize = false, PressureOpacity = false };
            using (var compositor = new TileGpuCompositor(allowCopyTexture: allowCopyTexture))
            {
                void Step(string name, int? maxTiles = null)
                {
                    compositor.Update(doc, PaintChannel.Color);
                    GpuTests.AssertMatches(doc.Composite(PaintChannel.Color), GpuTests.Read(compositor.Texture), name);
                    Assert.That(compositor.LastCpuTileCount, Is.Zero, name + ": no tile on the CPU");
                    if (maxTiles.HasValue) Assert.That(compositor.LastUpdatedTileCount, Is.LessThanOrEqualTo(maxTiles.Value), name + ": tiles recomposited");
                }
                Step("initial");
                if (allowCopyTexture) Assert.That(compositor.Backend, Does.Not.Contain("draw copy")); else Assert.That(compositor.Backend, Does.Contain("draw copy"));
                Step("no change", 0);
                using (var s = doc.BeginStroke(a.Id, PaintChannel.Color, brush)) { s.Add(new BrushSample(8, 8)); s.Add(new BrushSample(12, 8)); s.Commit(); }
                Step("stroke inside the group", 1);
                doc.SetLayerOpacity(g.Id, .5); Step("group opacity (fade)");
                doc.SetLayerBlendMode(g.Id, LayerBlendMode.Multiply); Step("group mode isolated");
                doc.SetLayerVisibility(g.Id, false); Step("hide group");
                doc.SetLayerVisibility(g.Id, true); Step("show group");
                doc.MoveLayerTo(c.Id, g.Id, 0); Step("move a layer into the group");
                doc.MoveLayerTo(a.Id, Guid.Empty, 0); Step("move a layer out of the group");
                var h = doc.GroupLayers(new[] { b.Id }, "H"); doc.SetLayerBlendMode(h.Id, LayerBlendMode.Screen); doc.SetLayerOpacity(h.Id, .8);
                Step("nested group");
                doc.SetLayerClipping(h.Id, true); Step("nested group clipped to the layer below it");
                doc.AddLayerMask(g.Id);
                using (var s = doc.BeginMaskStroke(g.Id, brush)) { s.Add(new BrushSample(60, 30)); s.Commit(); }
                Step("group mask stroke");
                doc.SetLayerBlendMode(g.Id, LayerBlendMode.PassThrough); Step("group back to pass-through");
                var adjust = doc.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert()); doc.SetLayerOpacity(adjust.Id, .5);
                doc.MoveLayerTo(adjust.Id, g.Id, doc.ChildrenOf(g.Id).Count); Step("adjustment inside the pass-through group");
                doc.Ungroup(g.Id); Step("ungroup");
                Assert.That(doc.Undo(), Is.True); Step("undo ungroup");
                Assert.That(doc.Redo(), Is.True); Step("redo ungroup");
                Assert.That(doc.Undo(), Is.True); Step("undo again");
                using (var s = doc.BeginStroke(c.Id, PaintChannel.Color, brush)) { s.Add(new BrushSample(70, 40)); Step("mid-stroke preview inside the group", 2); s.Cancel(); }
                Step("after cancel", 2);
            }
        }

        [Test] public void TheCpuFallbackStillCompositesGroups()
        {
            var doc = Nested(TileGpuCompositor.MaxNestedLevels + 2);
            var pass = doc.GroupLayers(new[] { doc.Layers.Last().Id }, "Pass"); doc.SetLayerOpacity(pass.Id, .5);
            using (var compositor = new TileGpuCompositor(allowGpu: false))
            {
                compositor.Update(doc, PaintChannel.Color);
                Assert.That(compositor.Backend, Does.StartWith("CPU composite fallback"));
                Assert.That(GpuTests.ReadCpu(compositor.Texture), Is.EqualTo(doc.Composite(PaintChannel.Color)));
            }
        }

        [Test] public void TheOldCpuTilePathForGroupsStillMatches()
        {
            var doc = Nested(4);
            using (var compositor = new TileGpuCompositor { CompositeGroupsOnCpu = true })
            {
                compositor.Update(doc, PaintChannel.Color);
                Assert.That(compositor.LastCpuTileCount, Is.GreaterThan(0));
                GpuTests.AssertMatches(doc.Composite(PaintChannel.Color), GpuTests.Read(compositor.Texture), "groups on the CPU tile path");
            }
        }
    }
}
