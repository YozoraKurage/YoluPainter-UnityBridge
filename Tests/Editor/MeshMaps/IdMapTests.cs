using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// ID マップのベイク（Core。ID の色で選ぶための土台）: 色の並び（<see cref="IdPalette"/>）がどの 2 色も段差以上離れ、灰色・黒を使わず、
    /// 決まった色になること。元ごとの部品の分け方（マテリアルスロット・メッシュ・メッシュの塊・UV アイランド）が、ポリゴン塗りつぶしの範囲の
    /// 索引（<see cref="SurfaceRegionIndex"/>）・スロット・レンダラーと同じ（焼き込まない三角形を通ってつながる塊も）で、部品ごとに 1 色・
    /// 部品どうしは別の色。頂点カラーは補間せず三角形ごと（多数決、3 つとも違えば値の小さい色）で、アンチエイリアスでも混ぜない。高ポリの
    /// 部品の色は低ポリの色と重ならない。スレッドの数によらず同じバイト列。前の版の ID マップだけが古くなる（ほかの種類は古くならない）。
    /// 分け方の作業メモリは見積もりに入り、予算を超えれば何も割り当てずに断る。保存の往復でバイト一致。
    /// </summary>
    public sealed class IdMapTests
    {
        [SetUp] public void TolerateBrokenShaderCompiler() { EditorShaderCompiler.TolerateErrorLogsIfBroken(); }

        static int Chebyshev(int a, int b) => Math.Max(Math.Abs((a >> 16 & 255) - (b >> 16 & 255)), Math.Max(Math.Abs((a >> 8 & 255) - (b >> 8 & 255)), Math.Abs((a & 255) - (b & 255))));

        // ───────── 色の並び ─────────

        [Test] public void PaletteColoursAreDistinctSeparatedNeverGreyAndFixed()
        {
            Assert.That(new[] { 1, 6, 7, 24, 25, 60, 61, 120, 4080, 4081 }.Select(IdPalette.MinimumSeparation), Is.EqualTo(new[] { 255, 255, 127, 127, 85, 85, 63, 63, 17, 15 }));
            foreach (int n in Enumerable.Range(0, 301).Concat(new[] { 4080, 4081 }))
            {
                var colors = IdPalette.Colors(n);
                Assert.That(colors.Length, Is.EqualTo(n));
                Assert.That(IdPalette.Colors(n), Is.EqualTo(colors), "the same count gives the same colours");
                int separation = IdPalette.MinimumSeparation(n), q = IdPalette.Levels(n);
                var levels = Enumerable.Range(0, q).Select(i => IdPalette.Level(i, q)).ToArray();
                foreach (int c in colors)
                {
                    Assert.That(c, Is.InRange(0, 0xFFFFFF));
                    Assert.That((c >> 16 & 255) == (c >> 8 & 255) && (c >> 8 & 255) == (c & 255), Is.False, n + ": grey (or black/white) " + IdMapColors.Hex(c));
                    Assert.That(levels, Has.Member(c >> 16 & 255).And.Member(c >> 8 & 255).And.Member(c & 255), "on the lattice");
                }
                int min = int.MaxValue;
                for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) min = Math.Min(min, Chebyshev(colors[i], colors[j]));
                if (n > 1) Assert.That(min, Is.GreaterThanOrEqualTo(separation), n + " colours");
            }
            Assert.That(IdPalette.Colors(6).Select(IdMapColors.Hex), Is.EquivalentTo(new[] { "#0000FF", "#00FF00", "#00FFFF", "#FF0000", "#FF00FF", "#FFFF00" }), "six parts: the six pure colours");
            var many = IdPalette.Colors(100000);
            Assert.That(many.Distinct().Count(), Is.EqualTo(100000));
            Assert.That(IdPalette.Levels(256 * 256 * 256 - 256), Is.EqualTo(256));
            Assert.That(() => IdPalette.Levels(256 * 256 * 256 - 255), Throws.InstanceOf<ArgumentOutOfRangeException>());
            // 続く番号（いちばん小さい三角形が近い部品）の色は格子の上で離れている: 25〜60 部品では隣の番号どうしが段差の 2 倍以上、
            // 24 部品では半分より多くの組が 0 と 255 ほど違う
            foreach (int n in new[] { 25, 60 })
            {
                var c = IdPalette.Colors(n);
                Assert.That(Enumerable.Range(0, n - 1).Min(i => Chebyshev(c[i], c[i + 1])), Is.GreaterThanOrEqualTo(2 * IdPalette.MinimumSeparation(n)), n + " parts");
            }
            var first = IdPalette.Colors(24);
            Assert.That(Enumerable.Range(0, 23).Count(i => Chebyshev(first[i], first[i + 1]) >= 255), Is.GreaterThan(12), "consecutive parts get far-apart colours");
        }

        // ───────── 部品の分け方 ─────────

        /// <summary>
        /// 部品の模型: 2×2 の四角形の板（8 三角形）を 4 枚。板 k は 3D で x = 3k に置き、UV では左の列と右の列を別の島にする（3D では
        /// 真ん中の辺を共有するのでメッシュの塊は 1 つ、UV アイランドは 2 つ）。板 0・1・2 はスロット 0（レンダラーは 0・0・1）、板 3 は
        /// スロット 1（レンダラー 1）。板 1 と板 2 は、UV の面積の無い三角形 2 つ（焼き込まない）で 3D の辺をつなぐ（メッシュの塊は 1 つ）。
        /// 三角形の順は決まった乱数で混ぜる（番号の付け方によらないことを見る）。
        /// </summary>
        internal static SurfaceGeometry Pieces()
        {
            var tris = new List<SurfaceTriangle>();
            for (int k = 0; k < 4; k++)
            {
                int slot = k == 3 ? 1 : 0, renderer = k >= 2 ? 1 : 0;
                Vector3 V(int x, int y) => new Vector3(3 * k + x, y, 0);
                for (int i = 0; i < 2; i++)
                {
                    var origin = new Vector2(0.05f + 0.22f * k + 0.11f * i, 0.1f + (k % 2) * 0.4f);
                    for (int j = 0; j < 2; j++)
                    {
                        Vector2 U(int x, int y) => origin + new Vector2((x - i) * 0.1f, y * 0.1f);
                        tris.Add(new SurfaceTriangle(V(i, j), V(i, j + 1), V(i + 1, j), U(i, j), U(i, j + 1), U(i + 1, j), renderer, slot));
                        tris.Add(new SurfaceTriangle(V(i + 1, j), V(i, j + 1), V(i + 1, j + 1), U(i + 1, j), U(i, j + 1), U(i + 1, j + 1), renderer, slot));
                    }
                }
            }
            var sliver = new Vector2(0.99f, 0.99f);
            tris.Add(new SurfaceTriangle(new Vector3(5, 0, 0), new Vector3(6, 0, 0), new Vector3(5, 1, 0), sliver, sliver, sliver, 0, 0));
            tris.Add(new SurfaceTriangle(new Vector3(6, 0, 0), new Vector3(6, 1, 0), new Vector3(5, 1, 0), sliver, sliver, sliver, 1, 0));
            var random = new System.Random(7);
            return new SurfaceGeometry(tris.OrderBy(_ => random.Next()).ToList());
        }
        internal static Vector2 UvCentroid(SurfaceTriangle t) => (t.UvA + t.UvB + t.UvC) / 3;
        static bool HasUvArea(SurfaceTriangle t) => Mathf.Abs((t.UvB.x - t.UvA.x) * (t.UvC.y - t.UvA.y) - (t.UvC.x - t.UvA.x) * (t.UvB.y - t.UvA.y)) > 1e-9f;

        static MeshBakeResult BakeId(MeshBakeInput input, MeshIdSource source, int size = 128, int slot = -1, int antialiasing = 1, MeshBakeInput high = null, MeshBakeBudget budget = null)
        {
            var result = MeshBaker.Bake(input, new MeshBakeSettings { Width = size, Height = size, TargetSlot = slot, Padding = 2, Antialiasing = antialiasing, Maps = new[] { MeshMapKind.Id }, IdSource = source },
                budget, null, default, high);
            Assert.That(result.Status, Is.EqualTo(MeshBakeStatus.Completed));
            return result;
        }
        static int ColourAt(BakedMeshMap map, Vector2 uv)
        {
            Assert.That(IdMapColors.TryGetAtUv(map, uv.x, uv.y, out int rgb), Is.True, "no colour at " + uv);
            return rgb;
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        public void EverySourceSplitsThePartsLikeThePolygonFill(int slot)
        {
            var g = Pieces(); var input = TexturePaintWindow.BuildMeshBakeInput(g); var index = new SurfaceRegionIndex(g);
            var receivers = Enumerable.Range(0, g.TriangleCount).Where(t => HasUvArea(g.Triangles[t]) && (slot < 0 || g.Triangles[t].MaterialSlot == slot)).ToList();
            Assert.That(receivers.Count, Is.EqualTo(slot < 0 ? 32 : slot == 0 ? 24 : 8));
            var sources = new (MeshIdSource source, Func<int, long> key, int parts)[]
            {
                (MeshIdSource.UvIsland, t => index.Key(t, SurfaceRegionKind.UvIsland), slot < 0 ? 8 : slot == 0 ? 6 : 2),
                (MeshIdSource.MeshPart, t => index.Key(t, SurfaceRegionKind.MeshPart), slot < 0 ? 3 : slot == 0 ? 2 : 1),
                (MeshIdSource.MaterialSlot, t => g.Triangles[t].MaterialSlot, slot < 0 ? 2 : 1),
                (MeshIdSource.Mesh, t => g.Triangles[t].RendererIndex, slot < 0 ? 2 : slot == 0 ? 2 : 1),
            };
            foreach (var (source, key, parts) in sources)
            {
                var result = BakeId(input, source, slot: slot); var map = result.Maps[0];
                Assert.That(result.Report.IdParts, Is.EqualTo(parts), source + ": parts");
                Assert.That(map.Provenance.SettingsKey, Is.EqualTo("source=" + source + ";algorithm=" + MeshBakeSettings.IdAlgorithm));
                var colourOf = receivers.ToDictionary(t => t, t => ColourAt(map, UvCentroid(g.Triangles[t])));
                foreach (int a in receivers)
                    foreach (int b in receivers)
                        Assert.That(colourOf[a] == colourOf[b], Is.EqualTo(key(a) == key(b)), source + ": triangles " + a + " and " + b);
                Assert.That(colourOf.Values.Distinct().Count(), Is.EqualTo(parts), source.ToString());
                Assert.That(colourOf.Values.All(c => IdPalette.Colors(parts).Contains(c)), Is.True, source + ": palette colours");
                Assert.That(result.Report.Diagnostics.Any(d => d.Contains("ID map: " + parts + " part(s)") && d.Contains("at least " + IdPalette.MinimumSeparation(parts))), Is.True, string.Join(" | ", result.Report.Diagnostics));
                // 焼いたテクセルの色は、どれも部品の色（余白も写しなので同じ色の組）
                var allowed = new HashSet<int>(colourOf.Values);
                for (int y = 0; y < map.Height; y++) for (int x = 0; x < map.Width; x++)
                    if (IdMapColors.TryGet(map, x, y, out int rgb)) Assert.That(allowed.Contains(rgb), Is.True, source + ": a texel of no part's colour " + IdMapColors.Hex(rgb));
            }
            // 板 1 と板 2 は焼き込まない細い三角形でつながっている: メッシュの塊は 1 つ（索引も同じ）
            int piece1 = receivers.FirstOrDefault(t => g.Triangles[t].A.x >= 3 && g.Triangles[t].A.x <= 5), piece2 = receivers.FirstOrDefault(t => g.Triangles[t].A.x >= 6 && g.Triangles[t].A.x <= 8);
            if (slot <= 0) Assert.That(index.Key(piece1, SurfaceRegionKind.MeshPart), Is.EqualTo(index.Key(piece2, SurfaceRegionKind.MeshPart)), "joined through the sliver");
        }

        [Test] public void TheIndexAndTheBakeShareOnePartitionOnTheDemoCube()
        {
            using (var preview = new IsolatedModelPreview())
            {
                Assert.That(preview.LoadDemoMesh().CanPaint, Is.True);
                var g = preview.Geometry; var index = new SurfaceRegionIndex(g);
                var uvs = g.Triangles.SelectMany(t => new[] { t.UvA.x, t.UvA.y, t.UvB.x, t.UvB.y, t.UvC.x, t.UvC.y }).ToArray();
                var uv = MeshRegions.UvIslands(uvs, g.Triangles.Select(t => t.MaterialSlot).ToArray(), out int islands);
                Assert.That(islands, Is.GreaterThan(1));
                for (int t = 0; t < g.TriangleCount; t++)
                    for (int u = 0; u < g.TriangleCount; u++)
                        Assert.That(uv[t] == uv[u], Is.EqualTo(index.Key(t, SurfaceRegionKind.UvIsland) == index.Key(u, SurfaceRegionKind.UvIsland)));
                // 索引は SurfaceRegions.Region（呼ぶたびに辺の表を作る前の実装）とも同じ
                for (int t = 0; t < g.TriangleCount; t++)
                    foreach (var kind in new[] { SurfaceRegionKind.UvIsland, SurfaceRegionKind.MeshPart })
                        Assert.That(index.Region(t, kind), Is.EqualTo(SurfaceRegions.Region(g, t, kind)), kind + " of " + t);
            }
        }

        // ───────── 頂点カラー ─────────

        [Test] public void VertexColoursArePerTriangleAndNeverBlended()
        {
            // 2 つの四角形（4 三角形）。角の色: (赤, 赤, 青) → 赤、(赤, 緑, 青) → 値の小さい青、(0.5 の灰, 0.5 の灰, 黄) → 灰、(白, 白, 白) → 白
            var corners = new List<float>(); var uvs = new List<float>(); var colors = new List<float>();
            void Tri(Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc, Color ca, Color cb, Color cc)
            {
                foreach (var (p, u, col) in new[] { (a, ua, ca), (b, ub, cb), (c, uc, cc) })
                { corners.AddRange(new[] { p.x, p.y, p.z }); uvs.Add(u.x); uvs.Add(u.y); colors.AddRange(new[] { col.r, col.g, col.b, col.a }); }
            }
            var grey = new Color(.5f, .5f, .5f, .2f);
            Tri(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector2(.05f, .05f), new Vector2(.05f, .45f), new Vector2(.45f, .05f), Color.red, Color.red, Color.blue);
            Tri(new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector2(.45f, .05f), new Vector2(.05f, .45f), new Vector2(.45f, .45f), Color.red, Color.green, Color.blue);
            Tri(new Vector3(2, 0, 0), new Vector3(2, 0, 1), new Vector3(3, 0, 0), new Vector2(.55f, .55f), new Vector2(.55f, .95f), new Vector2(.95f, .55f), grey, grey, Color.yellow);
            Tri(new Vector3(3, 0, 0), new Vector3(2, 0, 1), new Vector3(3, 0, 1), new Vector2(.95f, .55f), new Vector2(.55f, .95f), new Vector2(.95f, .95f), Color.white, Color.white, Color.white);
            var input = new MeshBakeInput(corners.ToArray(), null, uvs.ToArray(), new int[4], colors: colors.ToArray());
            int[] expected = { 0xFF0000, 0x0000FF, 0x808080, 0xFFFFFF };
            foreach (int aa in new[] { 1, 4 })
            {
                var result = BakeId(input, MeshIdSource.VertexColor, 64, 0, aa); var map = result.Maps[0];
                Assert.That(result.Report.IdParts, Is.Zero);
                var uvList = uvs;
                for (int t = 0; t < 4; t++)
                {
                    var centroid = new Vector2((uvList[t * 6] + uvList[t * 6 + 2] + uvList[t * 6 + 4]) / 3, (uvList[t * 6 + 1] + uvList[t * 6 + 3] + uvList[t * 6 + 5]) / 3);
                    Assert.That(IdMapColors.Hex(ColourAt(map, centroid)), Is.EqualTo(IdMapColors.Hex(expected[t])), aa + "×" + aa + ": triangle " + t);
                }
                // どのテクセルも 4 つの色のどれか（三角形の境目でも混ぜない）
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                    if (IdMapColors.TryGet(map, x, y, out int rgb)) Assert.That(expected, Has.Member(rgb), aa + "×" + aa + " at " + x + "," + y);
            }
            // 頂点カラーの無いメッシュは白で、そう知らせる
            var plain = BakeId(new MeshBakeInput(corners.ToArray(), null, uvs.ToArray(), new int[4]), MeshIdSource.VertexColor, 32, 0);
            Assert.That(ColourAt(plain.Maps[0], new Vector2(.2f, .2f)), Is.EqualTo(0xFFFFFF));
            Assert.That(plain.Report.Diagnostics, Has.Some.Contains("no vertex colours"));
        }

        // ───────── 高ポリ ─────────

        [Test] public void HighPolyPartsGetColoursOfTheirOwn()
        {
            // 低ポリ: 床（スロット 0）。高ポリ: 床の少し上の左右 2 枚（スロット 0 と 1、レンダラー 0 と 1）。右の外は高ポリに当たらない
            MeshBakeInput Quads(params (float x0, float x1, float y, int slot, float u0, float u1)[] quads)
            {
                var c = new List<float>(); var n = new List<float>(); var u = new List<float>(); var s = new List<int>(); var r = new List<int>();
                foreach (var q in quads)
                {
                    void V(float x, float z, float uu, float vv) { c.AddRange(new[] { x, q.y, z }); n.AddRange(new[] { 0f, 1f, 0f }); u.Add(uu); u.Add(vv); }
                    V(q.x0, 0, q.u0, .05f); V(q.x0, 1, q.u0, .95f); V(q.x1, 0, q.u1, .05f); s.Add(q.slot); r.Add(q.slot);
                    V(q.x1, 0, q.u1, .05f); V(q.x0, 1, q.u0, .95f); V(q.x1, 1, q.u1, .95f); s.Add(q.slot); r.Add(q.slot);
                }
                return new MeshBakeInput(c.ToArray(), n.ToArray(), u.ToArray(), s.ToArray(), renderers: r.ToArray(), rendererNames: new[] { "A", "B" });
            }
            var low = Quads((0, 1, 0, 0, .05f, .95f));
            var high = Quads((-.1f, .5f, .005f, 0, 0, 0), (.5f, .9f, .005f, 1, 0, 0));
            foreach (var source in new[] { MeshIdSource.MaterialSlot, MeshIdSource.Mesh, MeshIdSource.MeshPart })
            {
                var result = BakeId(low, source, 64, 0, 1, high); var map = result.Maps[0];
                Assert.That(result.Report.IdParts, Is.EqualTo(3), source + ": one low part and two high parts");
                var palette = IdPalette.Colors(3);
                int left = ColourAt(map, new Vector2(.2f, .5f)), right = ColourAt(map, new Vector2(.7f, .5f)), missed = ColourAt(map, new Vector2(.93f, .5f));
                Assert.That(new[] { missed, left, right }, Is.EqualTo(palette), source + ": the low part first, then the high parts in order");
            }
            // UV アイランドはいつも低ポリの島（高ポリの部品を数えない）
            var islands = BakeId(low, MeshIdSource.UvIsland, 64, 0, 1, high);
            Assert.That(islands.Report.IdParts, Is.EqualTo(1));
            Assert.That(ColourAt(islands.Maps[0], new Vector2(.2f, .5f)), Is.EqualTo(ColourAt(islands.Maps[0], new Vector2(.93f, .5f))));
        }

        // ───────── 決定性・古さ・予算・保存 ─────────

        [Test] public void IdBakesAreTheSameBytesWhateverTheThreads()
        {
            var input = TexturePaintWindow.BuildMeshBakeInput(Pieces());
            foreach (var source in (MeshIdSource[])Enum.GetValues(typeof(MeshIdSource)))
            {
                byte[] Run(int threads) => MeshMapBinary.Write(MeshBaker.Bake(input, new MeshBakeSettings { Width = 96, Height = 96, TargetSlot = -1, Antialiasing = 2, Maps = new[] { MeshMapKind.Id }, IdSource = source },
                    new MeshBakeBudget { MaxDegreeOfParallelism = threads }).Maps[0]);
                var one = Run(1);
                Assert.That(Run(0), Is.EqualTo(one), source + ": all threads"); Assert.That(Run(3), Is.EqualTo(one), source + ": three threads");
                // 保存の往復（.ylp の meshmap-Id.bin）
                var read = MeshMapBinary.Read(one);
                Assert.That(MeshMapBinary.Write(read), Is.EqualTo(one), source + ": write → read → write");
                Assert.That(read.Provenance.SettingsKey, Does.Contain("source=" + source));
            }
        }

        [Test] public void OnlyIdMapsOfTheOlderColouringGoStale()
        {
            var s = new MeshBakeSettings { Padding = 4, IdSource = MeshIdSource.MaterialSlot };
            var old = TestMeshMaps.Make(MeshMapKind.Id, 16, 16, (x, y, c) => .5, settingsKey: "source=MaterialSlot");
            var ao = TestMeshMaps.Make(MeshMapKind.AmbientOcclusion, 16, 16, (x, y, c) => .5, settingsKey: s.KindKey(MeshMapKind.AmbientOcclusion));
            var fresh = TestMeshMaps.Make(MeshMapKind.Id, 16, 16, (x, y, c) => .5, settingsKey: s.KindKey(MeshMapKind.Id));
            MeshMapExpectation Expect(BakedMeshMap m) => new MeshMapExpectation { MeshHash = m.Provenance.MeshHash, TopologyHash = m.Provenance.TopologyHash, Width = 16, Height = 16, TargetSlot = 0, Settings = s };
            var check = old.Provenance.Check(Expect(old));
            Assert.That(check.State, Is.EqualTo(MeshMapState.Stale));
            Assert.That(check.Reasons.Single(), Does.Contain("bake settings changed").And.Contain("source=MaterialSlot → source=MaterialSlot;algorithm=2"));
            Assert.That(ao.Provenance.Check(Expect(ao)).State, Is.EqualTo(MeshMapState.Current), "other kinds keep their bakes");
            Assert.That(fresh.Provenance.Check(Expect(fresh)).State, Is.EqualTo(MeshMapState.Current));
            // 保存したマップの設定を開いたときに戻す: 新しい元は戻り、知らない元（新しい版のもの）と版の数は読み飛ばす
            s.ApplyKindKey(MeshMapKind.Id, "source=MeshPart;algorithm=2", 4); Assert.That(s.IdSource, Is.EqualTo(MeshIdSource.MeshPart));
            s.ApplyKindKey(MeshMapKind.Id, "source=SomethingNewer;algorithm=3", 4); Assert.That(s.IdSource, Is.EqualTo(MeshIdSource.MeshPart));
            Assert.That(s.KindKey(MeshMapKind.Id), Is.EqualTo("source=MeshPart;algorithm=2"));
            // 型: 知らない元は断る
            var bad = new MeshBakeSettings { IdSource = (MeshIdSource)5 };
            Assert.That(() => bad.Validate(), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test] public void ThePartitionsWorkingMemoryIsBudgetedAndRefusedUpFront()
        {
            var input = TexturePaintWindow.BuildMeshBakeInput(Pieces());
            var bySlot = new MeshBakeSettings { Width = 64, Height = 64, TargetSlot = -1, Maps = new[] { MeshMapKind.Id }, IdSource = MeshIdSource.MaterialSlot };
            var byPart = bySlot.Clone(); byPart.IdSource = MeshIdSource.MeshPart;
            var byIsland = bySlot.Clone(); byIsland.IdSource = MeshIdSource.UvIsland;
            long slotBytes = MeshBaker.EstimateBytes(input, bySlot);
            Assert.That(MeshBaker.EstimateBytes(input, byPart) - slotBytes, Is.EqualTo(MeshRegions.EstimateBytes(input.TriangleCount)));
            Assert.That(MeshBaker.EstimateBytes(input, byIsland), Is.EqualTo(MeshBaker.EstimateBytes(input, byPart)));
            Assert.That(() => MeshBaker.Bake(input, byPart, new MeshBakeBudget { MaxBytes = MeshBaker.EstimateBytes(input, byPart) - 1 }),
                Throws.InstanceOf<MeshBakeRefusedException>().With.Message.Contains("budget"));
            Assert.That(MeshBaker.Bake(input, byPart, new MeshBakeBudget { MaxBytes = MeshBaker.EstimateBytes(input, byPart) }).Status, Is.EqualTo(MeshBakeStatus.Completed));
            // 分け方の型の拒否
            Assert.That(() => MeshRegions.UvIslands(new float[5], new int[1], out _), Throws.ArgumentException);
            Assert.That(() => MeshRegions.MeshParts(new float[9], new int[2], out _), Throws.ArgumentException);
            Assert.That(() => IdPalette.Levels(-1), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }
    }
}
