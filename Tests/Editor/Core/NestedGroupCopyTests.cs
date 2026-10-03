using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>CompositeRegions の入れ子の写し（CompositeJob.Resume / Captures）: グループの中で写しを取っても、取った写しからグループの中身を
    /// 始めても、出力は透明から全部を合成したものとバイト単位で同じ。入れ子の深い文書（分離・通過・マスク・不透明度・クリッピング・調整・
    /// 塗りつぶし・フィルター・チャンネルごとの合成）で、写しの組・領域・並列度を変えて確かめる。同じ配列への取り直し（同じグループ）と、
    /// 正しくない指定を断ることも。</summary>
    [TestFixture(KernelChoice.Managed)]
    [TestFixture(KernelChoice.Registered)]
    public sealed class NestedGroupCopyTests
    {
        readonly KernelChoice choice;
        public NestedGroupCopyTests(KernelChoice choice) { this.choice = choice; }
        int savedDegree; KernelScope kernels;
        [SetUp] public void SaveDegree() { savedDegree = CoreParallelism.MaxDegreeOfParallelism; kernels = KernelScope.Use(choice); }
        [TearDown] public void RestoreDegree() { CoreParallelism.MaxDegreeOfParallelism = savedDegree; kernels?.Dispose(); }

        internal static readonly PaintChannel[] Channels = { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Normal };

        static void RandomTile(Random rnd, byte[] b, int tile, int tx, int ty, int w, int h)
        {
            int kind = rnd.Next(5);
            if (kind == 0)
            {
                var u = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)(rnd.Next(3) == 0 ? 255 : rnd.Next(256)));
                for (int k = 0; k < b.Length; k += 4) { b[k] = u.R; b[k + 1] = u.G; b[k + 2] = u.B; b[k + 3] = u.A; }
            }
            else
            {
                rnd.NextBytes(b);
                // 小さなアルファ（RGB が残る透明に近い画素）も入れる
                if (kind <= 2) for (int k = 3; k < b.Length; k += 4) b[k] = (byte)(b[k] < 70 ? 0 : b[k] < 80 ? 1 : b[k] > 190 ? 255 : b[k]);
            }
            for (int y = 0; y < tile; y++) for (int x = 0; x < tile; x++) if (tx * tile + x >= w || ty * tile + y >= h) { int o = (y * tile + x) * 4; b[o] = b[o + 1] = b[o + 2] = b[o + 3] = 0; }
        }

        /// <summary>入れ子の深い文書: 各段に 1〜5 項目（グループは maxDepth 段まで）、ラスター（Color・Roughness・Normal の一部のタイル）・
        /// 塗りつぶし・調整、通過と分離のグループ、合成モード・不透明度・チャンネルごとの合成・クリッピング・非表示・マスク・フィルター。</summary>
        internal static PaintDocument NestedDocument(Random rnd, int w, int h, int tile, int maxDepth = 3)
        {
            var d = new PaintDocument(w, h, tile);
            var b = new byte[tile * tile * 4];
            var modes = ((LayerBlendMode[])Enum.GetValues(typeof(LayerBlendMode))).Where(m => m != LayerBlendMode.PassThrough).ToArray();
            int counter = 0;
            void Level(Guid parent, int depth)
            {
                int count = 1 + rnd.Next(depth == 0 ? 5 : 4);
                for (int i = 0; i < count; i++)
                {
                    PaintLayer l; int kind = rnd.Next(10);
                    if (kind < (depth < maxDepth ? 3 : 0)) l = d.AddGroup("g" + counter++);
                    else if (kind == 3)
                        l = d.AddAdjustmentLayer("a" + counter++, rnd.Next(2) == 0 ? AdjustmentSettings.Invert()
                            : AdjustmentSettings.Levels(rnd.NextDouble() * .3, .7 + rnd.NextDouble() * .3, .5 + rnd.NextDouble(), rnd.NextDouble() * .2, .8 + rnd.NextDouble() * .2), new[] { PaintChannel.Color, PaintChannel.Roughness });
                    else if (kind == 4)
                        l = d.AddFillLayer("f" + counter++, new Dictionary<PaintChannel, Rgba32>
                        {
                            { PaintChannel.Color, new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)(rnd.Next(2) == 0 ? 255 : rnd.Next(256))) },
                            { PaintChannel.Normal, new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)) },
                        });
                    else
                    {
                        l = d.AddLayer("l" + counter++);
                        foreach (var ch in Channels)
                        {
                            if (rnd.Next(4) == 0) continue;
                            if (ch != PaintChannel.Color) d.SetChannelEnabled(l.Id, ch, true);
                            var s = l.GetChannel(ch);
                            for (int ty = 0; ty * tile < h; ty++) for (int tx = 0; tx * tile < w; tx++) { if (rnd.Next(3) == 0) continue; RandomTile(rnd, b, tile, tx, ty, w, h); s.ImportTile(new TileCoord(tx, ty), b); }
                        }
                    }
                    d.MoveLayerTo(l.Id, parent, d.ChildrenOf(parent).Count(c => c.Id != l.Id));
                    if (l.IsGroup)
                    {
                        Level(l.Id, depth + 1);
                        if (rnd.Next(2) == 0) d.SetLayerBlendMode(l.Id, modes[rnd.Next(modes.Length)]);
                    }
                    else d.SetLayerBlendMode(l.Id, rnd.Next(2) == 0 ? LayerBlendMode.Normal : modes[rnd.Next(modes.Length)]);
                    if (rnd.Next(3) == 0) d.SetLayerOpacity(l.Id, rnd.Next(4) == 0 ? .003921 : .2 + .8 * rnd.NextDouble());
                    if (rnd.Next(6) == 0) d.SetLayerClipping(l.Id, true);
                    if (rnd.Next(12) == 0) d.SetLayerVisibility(l.Id, false);
                    if (rnd.Next(10) == 0) d.SetChannelOpacity(l.Id, PaintChannel.Roughness, rnd.NextDouble());
                    if (rnd.Next(10) == 0) d.SetChannelBlendMode(l.Id, PaintChannel.Color, l.IsGroup && rnd.Next(2) == 0 ? LayerBlendMode.PassThrough : modes[rnd.Next(modes.Length)]);
                    if (rnd.Next(4) == 0)
                    {
                        var m = d.AddLayerMask(l.Id);
                        for (int ty = 0; ty * tile < h; ty++) for (int tx = 0; tx * tile < w; tx++)
                        {
                            if (rnd.Next(2) == 0) continue;
                            Array.Clear(b, 0, b.Length); byte u = (byte)rnd.Next(256); bool uniform = rnd.Next(3) == 0;
                            for (int y = 0; y < tile; y++) for (int x = 0; x < tile; x++) if (tx * tile + x < w && ty * tile + y < h) b[(y * tile + x) * 4 + 3] = uniform ? u : (byte)rnd.Next(256);
                            m.Surface.ImportTile(new TileCoord(tx, ty), b);
                        }
                        if (rnd.Next(3) == 0) d.SetLayerMaskInverted(l.Id, true);
                        if (rnd.Next(3) == 0) d.SetLayerMaskDensity(l.Id, rnd.NextDouble());
                        if (rnd.Next(6) == 0) d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(1 + rnd.Next(3)));
                    }
                    if (!l.IsGroup && l.Kind != LayerKind.Adjustment && rnd.Next(8) == 0)
                        d.AddFilter(l.Id, FilterTarget.Content, rnd.Next(2) == 0 ? FilterSettings.GaussianBlur(1 + rnd.Next(4)) : FilterSettings.Noise(.3, rnd.Next(100), rnd.Next(2) == 0));
                }
            }
            Level(Guid.Empty, 0);
            d.ClearHistory();
            return d;
        }

        /// <summary>計画の中のグループ（クリッピングの基の項目のグループ）の位置。</summary>
        internal static List<int[]> GroupPaths(IReadOnlyList<CpuCompositor.StackEntry> plan, int[] prefix = null)
        {
            prefix = prefix ?? new int[0];
            var list = new List<int[]>();
            for (int i = 0; i < plan.Count; i++)
                if (plan[i].Base.IsGroup)
                {
                    var p = prefix.Concat(new[] { i }).ToArray();
                    list.Add(p); list.AddRange(GroupPaths(plan[i].Children, p));
                }
            return list;
        }
        static IReadOnlyList<CpuCompositor.StackEntry> ChildrenAt(IReadOnlyList<CpuCompositor.StackEntry> plan, int[] path)
        {
            foreach (int i in path) plan = plan[i].Children;
            return plan;
        }
        static bool IsPrefix(int[] prefix, int[] path) => prefix.Length < path.Length && prefix.SequenceEqual(path.Take(prefix.Length));

        /// <summary>写しを取って、それから始めても、どの領域・並列度でも透明から全部を合成した結果と同じ。最上段の写し（Start / CaptureAt）とも
        /// 組み合わせる。</summary>
        [Test] public void CapturingAndResumingInsideGroupsGiveTheFullCompositeBytes()
        {
            var rnd = new Random(20261004);
            int documents = 0, resumes = 0, captures = 0;
            for (int c = 0; c < 60; c++)
            {
                var d = NestedDocument(rnd, 20 + rnd.Next(90), 20 + rnd.Next(90), new[] { 8, 16, 32 }[rnd.Next(3)]);
                CoreParallelism.MaxDegreeOfParallelism = c % 3 == 0 ? 1 : c % 3 == 1 ? 3 : 0;
                foreach (var ch in Channels)
                {
                    var plan = CpuCompositor.Plan(d, ch);
                    var groups = GroupPaths(plan);
                    if (groups.Count == 0) continue;
                    documents++;
                    var full = d.Composite(ch);
                    int W = d.Width, H = d.Height, bytes = W * H * 4;
                    // 1. 透明から全部を合成しながら、グループの一部で写しを取る（最上段も）
                    var taken = new List<CpuCompositor.NestedCopy>();
                    foreach (var g in groups) if (rnd.Next(3) != 0) taken.Add(new CpuCompositor.NestedCopy(g, rnd.Next(ChildrenAt(plan, g).Count + 1), new byte[bytes], 0, W * 4));
                    int rootAt = rnd.Next(plan.Count + 1); var root = new byte[bytes];
                    var first = new CpuCompositor.CompositeJob(0, 0, W, H, new byte[bytes], 0, rootAt, root) { Captures = taken };
                    CpuCompositor.CompositeRegions(d, ch, new[] { first });
                    CpuCompositingTests.AssertSameBytes(full, first.Pixels, "document " + c + " " + ch + ": capturing on the way");
                    captures += taken.Count;
                    // 2. 写しから始める: 最上段は rootAt から（届くグループだけ）、浅いグループから、外の写しの中に入るものは除く
                    for (int round = 0; round < 3; round++)
                    {
                        int start = round == 0 ? 0 : rootAt;
                        var chosen = new List<CpuCompositor.NestedCopy>();
                        foreach (var t in taken.OrderBy(t => t.Group.Count))
                        {
                            var path = t.Group.ToArray();
                            if (path[0] < start || rnd.Next(4) == 0) continue;
                            if (chosen.Any(o => IsPrefix(o.Group.ToArray(), path) && path[o.Group.Count] < o.Index)) continue;
                            chosen.Add(t);
                        }
                        // 領域に分けて（写しの配列は全面の並びのまま、領域の位置から読む）
                        int x0 = rnd.Next(W), y0 = rnd.Next(H);
                        var rects = new List<(int x, int y, int w, int h)> { (0, 0, x0, y0), (x0, 0, W - x0, y0), (0, y0, x0, H - y0), (x0, y0, W - x0, H - y0) }.Where(r => r.w > 0 && r.h > 0).ToList();
                        if (round == 2) rects = new List<(int, int, int, int)> { (0, 0, W, H) };
                        var jobs = new List<CpuCompositor.CompositeJob>();
                        foreach (var (x, y, w, h) in rects)
                        {
                            var resume = chosen.Select(o => new CpuCompositor.NestedCopy(o.Group, o.Index, o.Pixels, (y * W + x) * 4, W * 4)).ToList();
                            jobs.Add(start == 0 ? new CpuCompositor.CompositeJob(x, y, w, h, new byte[w * h * 4]) { Resume = resume }
                                : new CpuCompositor.CompositeJob(x, y, w, h, new byte[w * h * 4], start, root, (y * W + x) * 4, W * 4) { Resume = resume });
                        }
                        CpuCompositor.CompositeRegions(d, ch, jobs);
                        foreach (var j in jobs)
                            for (int row = 0; row < j.Height; row++)
                                if (!full.AsSpan(((j.Y + row) * W + j.X) * 4, j.Width * 4).SequenceEqual(j.Pixels.AsSpan(row * j.Width * 4, j.Width * 4)))
                                    Assert.Fail("document " + c + " " + ch + " round " + round + ": region " + j.X + "," + j.Y + " " + j.Width + "x" + j.Height + " row " + row + " differs (resumed " + string.Join(" ", chosen.Select(o => "[" + string.Join(",", o.Group) + "]@" + o.Index)) + ")");
                        resumes += chosen.Count;
                    }
                }
            }
            Assert.That(documents, Is.GreaterThan(60)); Assert.That(resumes, Is.GreaterThan(140)); Assert.That(captures, Is.GreaterThan(140));
        }

        /// <summary>同じグループの写しから始めて、同じ配列へもっと上の添え字で取り直す（読んでから同じ行へ書く）。取り直した写しから始めても同じ。</summary>
        [Test] public void ACopyIsRetakenInPlaceHigherInTheSameGroup()
        {
            var rnd = new Random(77);
            int checkedCount = 0;
            for (int c = 0; c < 45; c++)
            {
                var d = NestedDocument(rnd, 30 + rnd.Next(60), 30 + rnd.Next(60), 16);
                CoreParallelism.MaxDegreeOfParallelism = c % 2 == 0 ? 3 : 0;
                var ch = Channels[rnd.Next(Channels.Length)];
                var plan = CpuCompositor.Plan(d, ch);
                var groups = GroupPaths(plan);
                if (groups.Count == 0) continue;
                var g = groups[rnd.Next(groups.Count)]; int n = ChildrenAt(plan, g).Count;
                int low = rnd.Next(n + 1), high = low + rnd.Next(n - low + 1);
                int W = d.Width, H = d.Height; var full = d.Composite(ch);
                var copy = new byte[W * H * 4];
                var take = new CpuCompositor.CompositeJob(0, 0, W, H, new byte[W * H * 4]) { Captures = new[] { new CpuCompositor.NestedCopy(g, low, copy, 0, W * 4) } };
                CpuCompositor.CompositeRegions(d, ch, new[] { take });
                var retake = new CpuCompositor.CompositeJob(0, 0, W, H, new byte[W * H * 4])
                { Resume = new[] { new CpuCompositor.NestedCopy(g, low, copy, 0, W * 4) }, Captures = new[] { new CpuCompositor.NestedCopy(g, high, copy, 0, W * 4) } };
                CpuCompositor.CompositeRegions(d, ch, new[] { retake });
                CpuCompositingTests.AssertSameBytes(full, retake.Pixels, "document " + c + ": resumed and retaken in place");
                var fresh = new byte[W * H * 4];
                CpuCompositor.CompositeRegions(d, ch, new[] { new CpuCompositor.CompositeJob(0, 0, W, H, new byte[W * H * 4]) { Captures = new[] { new CpuCompositor.NestedCopy(g, high, fresh, 0, W * 4) } } });
                CpuCompositingTests.AssertSameBytes(fresh, copy, "document " + c + ": the retaken copy is the copy taken from scratch");
                var again = new CpuCompositor.CompositeJob(0, 0, W, H, new byte[W * H * 4]) { Resume = new[] { new CpuCompositor.NestedCopy(g, high, copy, 0, W * 4) } };
                CpuCompositor.CompositeRegions(d, ch, new[] { again });
                CpuCompositingTests.AssertSameBytes(full, again.Pixels, "document " + c + ": from the retaken copy");
                checkedCount++;
            }
            Assert.That(checkedCount, Is.GreaterThan(20));
        }

        [Test] public void BadNestedCopiesAreRefused()
        {
            var d = new PaintDocument(32, 32, 16);
            var a = d.AddLayer("a"); var b = d.AddLayer("b"); var inner = d.AddLayer("inner");
            var g = d.GroupLayers(new[] { b.Id, inner.Id }, "g");
            var deep = d.AddLayer("deep"); d.MoveLayerTo(deep.Id, g.Id, 2); var g2 = d.GroupLayers(new[] { deep.Id }, "g2");
            foreach (var l in new[] { a, b, inner, deep }) using (var s = d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings { Radius = 8, Color = new Rgba32(200, 10, 10, 255), PressureSize = false, PressureOpacity = false })) { s.Add(new BrushSample(10, 10)); s.Commit(); }
            // 計画: [a, g[b, inner, g2[deep]]]
            var plan = CpuCompositor.Plan(d, PaintChannel.Color);
            Assert.That(plan.Count, Is.EqualTo(2)); Assert.That(plan[1].Children.Count, Is.EqualTo(3));
            byte[] Px() => new byte[32 * 32 * 4];
            CpuCompositor.NestedCopy Copy(int[] path, int index, byte[] pixels = null, int offset = 0, int stride = 128) => new CpuCompositor.NestedCopy(path, index, pixels ?? Px(), offset, stride);
            void Refused(string why, CpuCompositor.CompositeJob job, params CpuCompositor.CompositeJob[] more)
            { Assert.That(() => CpuCompositor.CompositeRegions(d, PaintChannel.Color, new[] { job }.Concat(more).ToList()), Throws.InstanceOf<ArgumentException>(), why); }
            Refused("not a group", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Captures = new[] { Copy(new[] { 0 }, 0) } });
            Refused("outside the plan", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Captures = new[] { Copy(new[] { 5 }, 0) } });
            Refused("index past the children", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Captures = new[] { Copy(new[] { 1 }, 4) } });
            Refused("negative index", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Resume = new[] { Copy(new[] { 1 }, -1) } });
            Refused("below Start", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px(), 2, -1, null) { Captures = new[] { Copy(new[] { 1 }, 1) } });
            Refused("inside the resumed part", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Resume = new[] { Copy(new[] { 1 }, 3) }, Captures = new[] { Copy(new[] { 1, 2 }, 0) } });
            Refused("two resumes of a group", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Resume = new[] { Copy(new[] { 1 }, 1), Copy(new[] { 1 }, 2) } });
            Refused("two captures of a group", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Captures = new[] { Copy(new[] { 1 }, 1), Copy(new[] { 1 }, 2) } });
            Refused("a capture below the resume", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Resume = new[] { Copy(new[] { 1 }, 2) }, Captures = new[] { Copy(new[] { 1 }, 1) } });
            var pixels = Px();
            Refused("capture into the job's pixels", new CpuCompositor.CompositeJob(0, 0, 32, 32, pixels) { Captures = new[] { Copy(new[] { 1 }, 1, pixels) } });
            Refused("resume from the job's pixels", new CpuCompositor.CompositeJob(0, 0, 32, 32, pixels) { Resume = new[] { Copy(new[] { 1 }, 1, pixels) } });
            var shared = Px();
            Refused("a capture shared by two jobs", new CpuCompositor.CompositeJob(0, 0, 32, 16, Px()) { Captures = new[] { Copy(new[] { 1 }, 1, shared) } },
                new CpuCompositor.CompositeJob(0, 16, 32, 16, Px()) { Captures = new[] { Copy(new[] { 1 }, 1, shared, 16 * 128) } });
            Refused("another job's capture as a resume", new CpuCompositor.CompositeJob(0, 0, 32, 16, Px()) { Captures = new[] { Copy(new[] { 1 }, 1, shared) } },
                new CpuCompositor.CompositeJob(0, 16, 32, 16, Px()) { Resume = new[] { Copy(new[] { 1 }, 1, shared, 16 * 128) } });
            Refused("in place on another group", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Resume = new[] { Copy(new[] { 1 }, 1, shared) }, Captures = new[] { Copy(new[] { 1, 2 }, 1, shared) } });
            Refused("in place with another stride", new CpuCompositor.CompositeJob(0, 0, 16, 16, Px()) { Resume = new[] { Copy(new[] { 1 }, 1, shared, 0, 64) }, Captures = new[] { Copy(new[] { 1 }, 2, shared, 0, 128) } });
            Refused("rows outside the array", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Resume = new[] { Copy(new[] { 1 }, 1, new byte[100]) } });
            Refused("stride too short", new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Captures = new[] { Copy(new[] { 1 }, 1, null, 0, 64) } });
            // 受けるもの: 同じグループの同じ配列での取り直し、深いグループの写し、外の写しの上の中の写し
            var copy = Px();
            Assert.That(() => CpuCompositor.CompositeRegions(d, PaintChannel.Color, new[] { new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Captures = new[] { Copy(new[] { 1 }, 1, copy), Copy(new[] { 1, 2 }, 1) } } }), Throws.Nothing);
            Assert.That(() => CpuCompositor.CompositeRegions(d, PaintChannel.Color, new[] { new CpuCompositor.CompositeJob(0, 0, 32, 32, Px()) { Resume = new[] { Copy(new[] { 1 }, 1, copy) }, Captures = new[] { Copy(new[] { 1 }, 3, copy), Copy(new[] { 1, 2 }, 0) } } }), Throws.Nothing);
        }
    }
}
