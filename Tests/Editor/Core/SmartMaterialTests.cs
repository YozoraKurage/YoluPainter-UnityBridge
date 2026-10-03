using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// スマートマテリアルとスマートマスク（Core）: 選んだ層を取り出して .ylsmart に書き、読み直して別の文書に置くと、層の組（種類・名前・合成・
    /// チャンネルごとの合成・不透明度・表示・クリッピング・グループ・ロック・塗りつぶしの値・調整・マスクとその画素・フィルターと Generator）が
    /// 同じで、合成がバイトまで同じ。大きさの違うテクスチャセットへは文書ごとの再標本化（Resampled）と同じ画素。ピンは書かず、置いた先の
    /// マップに付け直す（無ければ付けずに理由）。リソースを参照する層の画像を一緒に持ち、置くと予算の中でプロジェクトに入れる。1 回の Undo、
    /// セットに無いチャンネルは値を残してオフ、層が 2 つ以上ならグループにまとめる、置き場所（グループの中・一番下）、タイルの大きさの違う文書へは
    /// 画素をそのまま。拒否: 予算・層の数・フィルターの予算・種類の違い・すべてのロック・マスクの無い層。
    /// モデルの上のパスは画素になる。内蔵のものは決まったバイト列。
    /// </summary>
    public sealed class SmartMaterialTests
    {
        const int W = ChannelBlendTestsSize.W, H = ChannelBlendTestsSize.H, Tile = 16;
        static readonly YlpWriterInfo App = new YlpWriterInfo("YoluPainter", "0.0.0-test", "2022.3.22f1");
        static readonly PaintChannel[] All = (PaintChannel[])Enum.GetValues(typeof(PaintChannel));

        static SmartMaterial RoundTrip(SmartMaterial m) => SmartMaterialFile.Read(SmartMaterialFile.Write(m, App));
        /// <summary>ファイルの全部のエントリを展開した文字（中に書かれた文字列を探す）。</summary>
        static string AllText(byte[] file)
        {
            using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(file)))
                return string.Concat(zip.Entries.Select(e => { using (var s = e.Open()) using (var m = new MemoryStream()) { s.CopyTo(m); return System.Text.Encoding.UTF8.GetString(m.ToArray()); } }));
        }

        /// <summary>層の、ID によらない中身（親は並びの中の位置、効果は設定と強さとチャンネル、画素はタイルのバイト列）。</summary>
        static List<string> Describe(PaintDocument d, IEnumerable<PaintLayer> layers, Guid root = default)
        {
            var list = layers.ToList(); var lines = new List<string>();
            foreach (var l in list)
            {
                string parent = l.ParentId == Guid.Empty || l.ParentId == root ? "-" : list.FindIndex(p => p.Id == l.ParentId).ToString();
                var s = new System.Text.StringBuilder();
                s.Append(l.Kind).Append('|').Append(l.Name).Append('|').Append(l.Visible).Append('|').Append(l.Opacity.ToString("R")).Append('|').Append(l.BlendMode)
                 .Append("|clip ").Append(l.Clipping).Append("|locks ").Append(l.Locks).Append("|parent ").Append(parent);
                foreach (var c in All) if (l.IsChannelEnabled(c)) s.Append("|on ").Append(c);
                foreach (var b in l.ChannelBlends.OrderBy(b => b.Key)) s.Append("|blend ").Append(b.Key).Append(b.Value);
                foreach (var v in l.FillValues.OrderBy(v => v.Key)) s.Append("|fill ").Append(v.Key).Append(v.Value);
                if (l.Adjustment != null) s.Append("|adj ").Append(l.Adjustment.Type).Append(l.Adjustment.Gamma.ToString("R"));
                foreach (var ch in l.Channels.OrderBy(c => c.Key)) s.Append("|tiles ").Append(ch.Key).Append(Tiles(ch.Value));
                foreach (var f in l.Filters) s.Append("|filter ").Append(Effect(f));
                if (l.Mask != null)
                {
                    s.Append("|mask ").Append(l.Mask.Enabled).Append(l.Mask.Inverted).Append(l.Mask.Density.ToString("R")).Append(Tiles(l.Mask.Surface));
                    foreach (var f in l.Mask.Filters) s.Append("|maskfilter ").Append(Effect(f));
                }
                s.Append("|path ").Append(l.Path == null ? "none" : l.Path.GetType().Name);
                lines.Add(s.ToString());
            }
            return lines;
        }
        static string Tiles(SparseTileSurface s) => string.Join(";", s.EnumerateTiles().Select(t => t.Coord + "=" + GenerationStore.Hash(t.Bytes).Substring(0, 12)));
        static string Effect(FilterEffect f) => f.Settings.Type + "/" + (f.Settings.IsGenerator ? Generator(f.Settings.Generator) : f.Settings.Radius + "," + f.Settings.Amount.ToString("R") + "," + f.Settings.Seed)
            + "/" + f.Enabled + "/" + f.Strength.ToString("R") + "/" + string.Join(",", f.Channels);
        static string Generator(GeneratorSettings g) => string.Join(",", new object[] { g.Type, g.Low, g.High, g.Softness, g.Invert, g.NoiseAmount, g.NoiseScale, g.NoiseSeed, g.NoiseSpace, g.Blend, g.Balance, g.Axis,
            g.DirectionX, g.DirectionY, g.DirectionZ, g.UseBentNormal, g.Volume.Shape, g.Volume.CenterX, g.Volume.SizeX });

        static void AssertSameComposite(PaintDocument expected, PaintDocument actual, string what)
        {
            foreach (var c in All) Assert.That(actual.Composite(c), Is.EqualTo(expected.Composite(c)), what + ": " + c);
        }

        /// <summary>ChannelBlendTests の文書（グループ・通過・クリッピング・調整・マスク・チャンネルごとの合成）に、塗りつぶし（値 2 つ・マスクと
        /// Generator・フィルター）とロックと隠した層を足したもの。</summary>
        static PaintDocument Rich(IGeneratorInputs inputs = null)
        {
            var d = ChannelBlendTests.Build(true);
            var fill = d.AddFillLayer("Paint", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(40, 90, 160, 255) }, { PaintChannel.Roughness, new Rgba32(140, 140, 140, 255) } });
            d.AddLayerMask(fill.Id);
            for (int x = 0; x < W; x++) d.GetLayer(fill.Id).Mask.Surface.SetPixel(x, (x * 3) % H, new Rgba32(0, 0, 0, 120));
            d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.EdgeWear).WithNoise(0, .05, 0, GeneratorNoiseSpace.Model)));
            d.AddFilter(fill.Id, FilterTarget.Content, FilterSettings.GaussianBlur(2), new[] { PaintChannel.Color });
            d.SetLayerLocks(fill.Id, LayerLocks.Pixels);
            var hidden = d.AddLayer("hidden"); hidden.GetChannel(PaintChannel.Color).SetPixel(3, 4, new Rgba32(1, 2, 3, 4)); d.SetLayerVisibility(hidden.Id, false);
            if (inputs != null) d.GeneratorInputs = inputs;
            d.ClearHistory();
            return d;
        }
        static TestGeneratorInputs Maps(int w, int h, string key) => Maps(w, h, key, out _);
        /// <summary>曲率・位置・AO・ワールドの法線（上向きの面ほど上を向く）のマップ。curvatureKey はそのベイクの条件の鍵。</summary>
        static TestGeneratorInputs Maps(int w, int h, string key, out string curvatureKey)
        {
            var curvature = TestMeshMaps.Make(MeshMapKind.Curvature, w, h, (x, y, _) => (x + y) / (double)(w + h), null, key);
            curvatureKey = curvature.Provenance.ConditionKey;
            return new TestGeneratorInputs().Put(curvature)
                .Put(TestMeshMaps.Make(MeshMapKind.Position, w, h, (x, y, c) => c == 0 ? x / (double)w : c == 1 ? y / (double)h : .5, null, key))
                .Put(TestMeshMaps.Make(MeshMapKind.AmbientOcclusion, w, h, (x, y, _) => 1 - (x % 7) / 7.0, null, key))
                .Put(TestMeshMaps.Make(MeshMapKind.WorldNormal, w, h, (x, y, c) => c == 1 ? y / (double)h : .5, null, key));
        }

        // ───────── 往復 ─────────

        [Test] public void SavingAllLayersAndPlacingThemIntoAnEmptyDocumentGivesTheSameLayersAndComposite()
        {
            var inputs = Maps(W, H, "a");
            var source = Rich(inputs);
            var material = RoundTrip(source.CaptureSmartMaterial(source.Layers.Select(l => l.Id), "Everything"));
            Assert.That(material.Kind, Is.EqualTo(SmartKind.Material));
            Assert.That(material.LayerCount, Is.EqualTo(source.Layers.Count));
            Assert.That((material.Width, material.Height), Is.EqualTo((W, H)));
            Assert.That(material.Channels, Is.EquivalentTo(All.Where(c => source.Layers.Any(l => l.IsChannelEnabled(c)))));
            var target = new PaintDocument(W, H, Tile) { GeneratorInputs = inputs };
            var result = target.PlaceSmartMaterial(material);
            Assert.That(result.Resampled, Is.False); Assert.That(result.SwitchedOff, Is.Empty); Assert.That(result.InactiveGenerators, Is.Empty);
            Assert.That(target.UndoCount, Is.EqualTo(1), "one undo step");
            // 上の層がいくつもあるので、名前のグループに入る
            var group = target.GetLayer(result.LayerId);
            Assert.That(group.IsGroup, Is.True); Assert.That(group.Name, Is.EqualTo("Everything")); Assert.That(group.BlendMode, Is.EqualTo(LayerBlendMode.PassThrough));
            var placed = target.Layers.Where(l => l != group).ToList();
            foreach (var l in placed.Where(l => source.Layers.Any(s => s.ParentId == Guid.Empty && s.Name == l.Name))) Assert.That(l.ParentId, Is.EqualTo(group.Id), l.Name);
            // 一番上の階層の層は、置いた先ではまとめのグループの中（それを根として比べる）
            Assert.That(Describe(target, placed, group.Id), Is.EqualTo(Describe(source, source.Layers)));
            Assert.That(placed.Select(l => l.Id).Intersect(source.Layers.Select(l => l.Id)), Is.Empty, "new IDs");
            AssertSameComposite(source, target, "placed");
            Assert.That(target.Undo(), Is.True); Assert.That(target.Layers, Is.Empty, "undo removes everything it placed");
            Assert.That(target.Redo(), Is.True); Assert.That(target.Layers.Count, Is.EqualTo(source.Layers.Count + 1));
            AssertSameComposite(source, target, "redo");
        }

        [Test] public void APartOfTheStackKeepsItsOrderAndAChosenGroupBringsItsContents()
        {
            var source = Rich();
            var group = source.Layers.First(l => l.IsGroup); var top = source.Layers.First(l => l.Name == "top");
            var material = RoundTrip(source.CaptureSmartMaterial(new[] { top.Id, group.Id, source.ChildrenOf(group.Id)[0].Id }, "Part"));
            Assert.That(material.LayerNames, Is.EqualTo(new[] { "a", "b", "group", "top" }), "the group's contents come along once, in order");
            Assert.That(material.TopLevelCount, Is.EqualTo(2));
            // 1 つの層（グループ）だけなら、そのまま置く
            var single = RoundTrip(source.CaptureSmartMaterial(new[] { group.Id }, "Just the group"));
            var target = new PaintDocument(W, H, Tile); var under = target.AddLayer("under"); target.ClearHistory();
            var r = target.PlaceSmartMaterial(single, SmartPlacement.Above(target, under.Id));
            Assert.That(target.GetLayer(r.LayerId).Name, Is.EqualTo("group"), "one top layer is placed as it is");
            Assert.That(target.Layers.Select(l => l.Name), Is.EqualTo(new[] { "under", "a", "b", "group" }));
        }

        [Test] public void PlacingGoesIntoAGroupAtAPositionOrAtTheBottom()
        {
            var source = Rich(); var material = source.CaptureSmartMaterial(new[] { source.Layers.First(l => l.Name == "back").Id }, "Back");
            var d = new PaintDocument(W, H, Tile);
            var a = d.AddLayer("a"); var b = d.AddLayer("b"); var g = d.GroupLayers(new[] { a.Id, b.Id }, "g"); var top = d.AddLayer("top"); d.ClearHistory();
            var inside = d.PlaceSmartMaterial(material, new SmartPlacement { ParentId = g.Id, Position = 1 });
            Assert.That(d.GetLayer(inside.LayerId).ParentId, Is.EqualTo(g.Id));
            Assert.That(d.ChildrenOf(g.Id).Select(l => l.Name), Is.EqualTo(new[] { "a", "back", "b" }));
            var bottom = d.PlaceSmartMaterial(material, new SmartPlacement { ParentId = Guid.Empty, Position = 0 });
            Assert.That(d.Layers[0].Id, Is.EqualTo(bottom.LayerId)); Assert.That(d.ChildrenOf(Guid.Empty)[0].Id, Is.EqualTo(bottom.LayerId));
            var onTop = d.PlaceSmartMaterial(material);
            Assert.That(d.Layers.Last().Id, Is.EqualTo(onTop.LayerId));
            d.ValidateStructure();
            Assert.That(() => d.PlaceSmartMaterial(material, new SmartPlacement { ParentId = top.Id }), Throws.ArgumentException, "only into a group");
        }

        // ───────── 大きさの違うセット ─────────

        [Test] public void AnotherSizeIsTheWholeDocumentResampledLikeChangingTheCanvasSize()
        {
            var source = Rich();
            var material = RoundTrip(source.CaptureSmartMaterial(source.Layers.Select(l => l.Id), "Resized"));
            foreach (var (w, h) in new[] { (W * 2, H * 2), (W / 2, H / 2), (W * 3 / 2, H) })
            {
                var target = new PaintDocument(w, h, Tile);
                var result = target.PlaceSmartMaterial(material);
                Assert.That(result.Resampled, Is.True);
                var how = ImageResampling.Automatic(W, H, w, h);
                var expected = source.Resampled(w, h, how).Document;
                var placed = target.Layers.Where(l => l.Id != result.LayerId).ToList();
                Assert.That(Describe(target, placed, result.LayerId), Is.EqualTo(Describe(expected, expected.Layers)), w + "×" + h);
                AssertSameComposite(expected, target, w + "×" + h);
            }
        }

        // ───────── ピンの付け直し ─────────

        [Test] public void PinsAreNotSavedAndArePinnedAgainToTheMapsWhereItIsPlaced()
        {
            var here = Maps(W, H, "here", out string hereKey);
            var source = Rich(here);
            var fill = source.Layers.First(l => l.Name == "Paint"); var stage = fill.Mask.Filters.Single();
            var pinnedHere = stage.Settings.Generator.WithPin(MeshMapKind.Curvature, hereKey);
            source.SetFilterSettings(fill.Id, stage.Id, stage.Settings.WithGenerator(pinnedHere));
            Assert.That(source.GetGeneratorStatus(fill.Id, stage.Id).Active, Is.True);
            var bytes = SmartMaterialFile.Write(source.CaptureSmartMaterial(new[] { fill.Id }, "Pinned"), App);
            Assert.That(AllText(bytes).Contains(hereKey), Is.False, "the bake's key is not in the file");
            var material = SmartMaterialFile.Read(bytes);
            Assert.That(material.Repin.Count, Is.EqualTo(1));
            // ほかの条件で焼いたマップのある文書: そのマップに付け直す
            var there = Maps(W, H, "there", out string thereKey);
            var target = new PaintDocument(W, H, Tile) { GeneratorInputs = there };
            var r = target.PlaceSmartMaterial(material);
            Assert.That((r.Pinned, r.NotPinned), Is.EqualTo((1, 0)));
            var placed = target.GetLayer(r.LayerId).Mask.Filters.Single().Settings.Generator;
            Assert.That(placed.Pins.Keys, Is.EquivalentTo(placed.UsedMaps));
            Assert.That(placed.Pins[MeshMapKind.Curvature], Is.EqualTo(thereKey));
            Assert.That(target.GetGeneratorStatus(r.LayerId, target.GetLayer(r.LayerId).Mask.Filters.Single().Id).Active, Is.True);
            // マップの無い文書: 付けずに理由を出し、置くのは断らない（入力をそのまま通す）
            var bare = new PaintDocument(W, H, Tile);
            var r2 = bare.PlaceSmartMaterial(material);
            Assert.That((r2.Pinned, r2.NotPinned), Is.EqualTo((0, 1)));
            Assert.That(bare.GetLayer(r2.LayerId).Mask.Filters.Single().Settings.Generator.Pins, Is.Empty);
            Assert.That(r2.InactiveGenerators, Is.Not.Empty); Assert.That(r2.Notes.Any(n => n.Contains("pinned")), Is.True, string.Join(" | ", r2.Notes));
            // ピンの無かった Generator は、ピンの無いまま
            var unpinned = Rich(here); var plain = unpinned.Layers.First(l => l.Name == "Paint");
            var m2 = unpinned.CaptureSmartMaterial(new[] { plain.Id }, "Follows");
            Assert.That(m2.Repin, Is.Empty);
            var t2 = new PaintDocument(W, H, Tile) { GeneratorInputs = there }; var r3 = t2.PlaceSmartMaterial(m2);
            Assert.That(r3.Pinned, Is.Zero); Assert.That(t2.GetLayer(r3.LayerId).Mask.Filters.Single().Settings.Generator.Pins, Is.Empty);
        }

        // ───────── リソースを持つ層 ─────────

        [Test] public void FillImagesComeAlongAndArePlacedUnderTheIdTheyGetInTheProject()
        {
            var resources = new ProjectResources();
            var picture = resources.Add("Scratches", ImageContent.FromPixels(Enumerable.Range(0, 8 * 4 * 4).Select(i => (byte)(i * 7 | 1)).ToArray(), 8, 4), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            var other = resources.Add("Unused", ImageContent.FromPixels(new byte[16], 2, 2), ResourceOrigin.None, ResourceColorSpace.Linear, out _);
            var source = new PaintDocument(W, H, Tile) { ImageResources = resources };
            var fill = source.AddFillLayer("Painted", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(90, 90, 90, 255) }, { PaintChannel.Roughness, new Rgba32(140, 140, 140, 255) } });
            source.SetFillImage(fill.Id, PaintChannel.Color, picture.Id);
            source.SetFillImage(fill.Id, PaintChannel.Roughness, picture.Id); // 同じ画像を 2 つのチャンネルで: 1 つだけ持つ
            source.SetFillProjection(fill.Id, FillProjection.Default.WithTiles(3, 2).WithOffset(.25, .5).WithRotation(30));
            var material = RoundTrip(source.CaptureSmartMaterial(new[] { fill.Id }, "With image", resources));
            Assert.That(material.Images.Select(i => (i.Id, i.Name, i.Content.Hash, i.ColorSpace)), Is.EqualTo(new[] { (picture.Id, "Scratches", picture.ContentHash, ResourceColorSpace.Srgb) }), "only the image the layer uses");
            var expected = source.Composite(PaintChannel.Color);
            Assert.That(expected.Distinct().Count(), Is.GreaterThan(4), "the image shows");
            // 同じ画素を別の ID で持つプロジェクト: そのリソースを使い（2 つ目を作らない）、置いた層はその ID を読む
            var holding = new ProjectResources(); var same = holding.Add("Mine", picture.Content, ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            var ids = SmartImages.AddTo(holding, material);
            Assert.That(ids[picture.Id], Is.EqualTo(same.Id)); Assert.That(holding.Count, Is.EqualTo(1));
            var target = new PaintDocument(W, H, Tile) { ImageResources = holding };
            var r = target.PlaceSmartMaterial(material, new SmartPlacement { ResourceIds = ids });
            var placed = target.GetLayer(r.LayerId);
            Assert.That(placed.FillImages.Values, Is.All.EqualTo(same.Id)); Assert.That(placed.Projection, Is.EqualTo(fill.Projection));
            Assert.That(target.MissingFillImages(), Is.Empty);
            Assert.That(target.Composite(PaintChannel.Color), Is.EqualTo(expected), "the same picture through the other ID");
            Assert.That(target.Undo(), Is.True); Assert.That(target.Layers, Is.Empty);
            // 持たないプロジェクト: その ID のまま入る
            var project = new ProjectResources();
            var ids2 = SmartImages.AddTo(project, material);
            Assert.That(ids2[picture.Id], Is.EqualTo(picture.Id)); Assert.That(project.Images.Single().ContentHash, Is.EqualTo(picture.ContentHash));
            var t2 = new PaintDocument(W, H, Tile) { ImageResources = project };
            var r2 = t2.PlaceSmartMaterial(material, new SmartPlacement { ResourceIds = ids2 });
            Assert.That(t2.GetLayer(r2.LayerId).FillImages.Values, Is.All.EqualTo(picture.Id)); Assert.That(t2.Composite(PaintChannel.Color), Is.EqualTo(expected));
            // 大きさの違う文書へも画像と投影はそのまま（UV の空間で決まる）
            var t3 = new PaintDocument(W * 2, H * 2, Tile) { ImageResources = holding };
            var r3 = t3.PlaceSmartMaterial(material, new SmartPlacement { ResourceIds = ids });
            Assert.That(r3.Resampled, Is.True); Assert.That(t3.GetLayer(r3.LayerId).FillImages.Values, Is.All.EqualTo(same.Id));
            // 予算を超えるなら何も入れない
            var tight = new ProjectResources { BudgetBytes = picture.Content.ByteSize - 1 };
            Assert.That(() => SmartImages.AddTo(tight, material), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.OverBudget));
            Assert.That(tight.Count, Is.Zero);
            // プロジェクトに無い画像を参照していれば、持たずに知らせる（ほかの参照の口も同じ: 試験の関数で）
            var missing = source.CaptureSmartMaterial(new[] { fill.Id }, "Missing", resources, l => new[] { Guid.NewGuid() });
            Assert.That(missing.Images, Is.Empty); Assert.That(missing.Notes.Single(), Does.Contain("does not have"));
            Assert.That(other, Is.Not.Null);
            // 見本のタイルのプレビューも画像を読む（画像の無い塗りつぶしの値の灰色だけにはならない）
            var preview = SmartPreview.Render(material, 64);
            Assert.That(Enumerable.Range(0, 64 * 64).Where(i => preview[i * 4 + 3] != 0).Select(i => preview[i * 4]).Distinct().Count(), Is.GreaterThan(20));
        }

        // ───────── チャンネル ─────────

        [Test] public void ChannelsTheTextureSetDoesNotUseArePlacedSwitchedOffWithTheirValuesKept()
        {
            var material = BuiltInSmartMaterials.Make("rusty-metal");
            Assert.That(material.Channels, Is.EqualTo(new[] { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Metallic }));
            var d = new PaintDocument(W, H, Tile);
            var r = d.PlaceSmartMaterial(material, new SmartPlacement { Channels = new[] { PaintChannel.Color, PaintChannel.Normal, PaintChannel.Emission } });
            Assert.That(r.SwitchedOff, Is.EqualTo(new[] { PaintChannel.Roughness, PaintChannel.Metallic }));
            foreach (var l in d.Layers.Where(l => l.Kind == LayerKind.Fill))
            {
                Assert.That(l.IsChannelEnabled(PaintChannel.Color), Is.True, l.Name);
                Assert.That(l.IsChannelEnabled(PaintChannel.Roughness) || l.IsChannelEnabled(PaintChannel.Metallic), Is.False, l.Name);
                Assert.That(l.FillValues.Keys, Is.EquivalentTo(new[] { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Metallic }), l.Name + ": the values are kept");
            }
            Assert.That(d.Composite(PaintChannel.Roughness).All(b => b == 0), Is.True, "nothing shows in a switched-off channel");
            // 何も渡さなければ全部オン
            var all = new PaintDocument(W, H, Tile); Assert.That(all.PlaceSmartMaterial(material).SwitchedOff, Is.Empty);
        }

        // ───────── 拒否 ─────────

        [Test] public void OverTheBudgetOrTheLayerLimitOrAnotherTileSizeNothingChanges()
        {
            var source = new PaintDocument(W, H, Tile); var paint = source.AddLayer("paint");
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) paint.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32((byte)x, (byte)y, 9, 255));
            var material = source.CaptureSmartMaterial(new[] { paint.Id }, "Pixels");
            Assert.That(material.HasPixels, Is.True); Assert.That(material.PixelBytes, Is.GreaterThan(0));
            var d = new PaintDocument(W, H, Tile); var keep = d.AddLayer("keep"); d.ClearHistory();
            d.SourceBudgetBytes = material.PixelBytes - 1;
            long revision = d.Revision;
            var ex = Assert.Throws<SmartRefusedException>(() => d.PlaceSmartMaterial(material));
            Assert.That(ex.Reason, Is.EqualTo(SmartRefusal.Budget)); Assert.That(ex.Bytes, Is.EqualTo(material.PixelBytes));
            Assert.That((d.Layers.Count, d.UndoCount, d.Revision), Is.EqualTo((1, 0, revision)), "nothing changed");
            // 大きさが違う（再標本化の途中で予算を超える）
            var big = new PaintDocument(W * 2, H * 2, Tile) { SourceBudgetBytes = material.PixelBytes };
            Assert.That(Assert.Throws<SmartRefusedException>(() => big.PlaceSmartMaterial(material)).Reason, Is.EqualTo(SmartRefusal.Budget));
            Assert.That(big.Layers, Is.Empty);
            // 層の数
            var many = new PaintDocument(8, 8, 8);
            for (int i = 0; i < PaintDocument.MaxLayers - 1; i++) many.AddLayer("l" + i);
            many.ClearHistory();
            var two = Rich().CaptureSmartMaterial(new[] { Rich().Layers[0].Id }, "x");
            var small = BuiltInSmartMaterials.Make("worn-edges");
            Assert.That(Assert.Throws<SmartRefusedException>(() => many.PlaceSmartMaterial(small)).Reason, Is.EqualTo(SmartRefusal.LayerLimit));
            Assert.That(many.Layers.Count, Is.EqualTo(PaintDocument.MaxLayers - 1));
            // タイルの大きさが違っても、同じ大きさなら画素はそのまま
            var other = new PaintDocument(W, H, 32);
            var r = other.PlaceSmartMaterial(material);
            Assert.That(r.Resampled, Is.False); Assert.That(other.Composite(PaintChannel.Color), Is.EqualTo(source.Composite(PaintChannel.Color)));
            // 種類
            var mask = BuiltInSmartMaterials.Make("edges");
            Assert.That(Assert.Throws<SmartRefusedException>(() => d.PlaceSmartMaterial(mask)).Reason, Is.EqualTo(SmartRefusal.WrongKind));
            Assert.That(Assert.Throws<SmartRefusedException>(() => d.ApplySmartMask(material, keep.Id)).Reason, Is.EqualTo(SmartRefusal.WrongKind));
            // 何も選んでいない・マスクが無い
            Assert.That(Assert.Throws<SmartRefusedException>(() => source.CaptureSmartMaterial(new Guid[0], "none")).Reason, Is.EqualTo(SmartRefusal.NothingToSave));
            Assert.That(Assert.Throws<SmartRefusedException>(() => source.CaptureSmartMask(paint.Id, "none")).Reason, Is.EqualTo(SmartRefusal.NoMask));
            Assert.That(() => source.CaptureSmartMaterial(new[] { paint.Id }, " "), Throws.ArgumentException);
            Assert.That(two, Is.Not.Null);
        }

        [Test] public void AFilterTheDocumentRefusesIsRefusedBeforeAnythingChanges()
        {
            var source = new PaintDocument(W, H, Tile); var l = source.AddLayer("blurred");
            l.GetChannel(PaintChannel.Color).SetPixel(1, 1, new Rgba32(9, 9, 9, 255));
            source.AddFilter(l.Id, FilterTarget.Content, FilterSettings.GaussianBlur(FilterSettings.MaxBlurRadius), new[] { PaintChannel.Color });
            var material = source.CaptureSmartMaterial(new[] { l.Id }, "Blur");
            var d = new PaintDocument(W, H, Tile) { FilterBlockPixels = Tile };
            d.FilterWorkingBudgetBytes = 1024; // この文書は大きなぼかしを断る
            var ex = Assert.Throws<SmartRefusedException>(() => d.PlaceSmartMaterial(material));
            Assert.That(ex.Reason, Is.EqualTo(SmartRefusal.Filters)); Assert.That(d.Layers, Is.Empty);
        }

        // ───────── スマートマスク ─────────

        [Test] public void ASmartMaskReplacesTheLayersMaskInOneUndoStepAndRefusesLockAll()
        {
            var inputs = Maps(W, H, "m");
            var source = Rich(inputs); var fill = source.Layers.First(l => l.Name == "Paint");
            source.SetLayerMaskInverted(fill.Id, true); source.SetLayerMaskDensity(fill.Id, .8);
            var mask = RoundTrip(source.CaptureSmartMask(fill.Id, "Edges here"));
            Assert.That(mask.Kind, Is.EqualTo(SmartKind.Mask)); Assert.That(mask.Channels, Is.Empty); Assert.That(mask.LayerCount, Is.EqualTo(1));
            var d = new PaintDocument(W, H, Tile) { GeneratorInputs = inputs };
            var target = d.AddFillLayer("Target", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(200, 10, 10, 255) } });
            var old = d.AddLayerMask(target.Id); old.Surface.SetPixel(2, 2, new Rgba32(0, 0, 0, 255)); d.ClearHistory();
            var before = d.Composite(PaintChannel.Color);
            var r = d.ApplySmartMask(mask, target.Id);
            Assert.That(r.ReplacedMask, Is.True); Assert.That(d.UndoCount, Is.EqualTo(1));
            var applied = d.GetLayer(target.Id).Mask;
            Assert.That((applied.Enabled, applied.Inverted, applied.Density), Is.EqualTo((true, true, .8)));
            Assert.That(Tiles(applied.Surface), Is.EqualTo(Tiles(fill.Mask.Surface)));
            Assert.That(applied.Filters.Select(Effect), Is.EqualTo(fill.Mask.Filters.Select(Effect)));
            Assert.That(applied.Filters.Single().Id, Is.Not.EqualTo(fill.Mask.Filters.Single().Id));
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) Assert.That(applied.OutputHideAt(x, y), Is.EqualTo(fill.Mask.OutputHideAt(x, y)), x + "," + y);
            Assert.That(d.Undo(), Is.True); Assert.That(d.GetLayer(target.Id).Mask, Is.SameAs(old)); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before));
            Assert.That(d.Redo(), Is.True); Assert.That(d.GetLayer(target.Id).Mask, Is.SameAs(applied));
            // マスクの無い層には足す
            var bare = d.AddLayer("bare"); var r2 = d.ApplySmartMask(mask, bare.Id);
            Assert.That(r2.ReplacedMask, Is.False); Assert.That(d.GetLayer(bare.Id).Mask, Is.Not.Null);
            // すべてのロック（グループのものも）では断る
            var locked = d.AddLayer("locked"); var g = d.GroupLayers(new[] { locked.Id }, "g"); d.SetLayerLocks(g.Id, LayerLocks.All);
            int undo = d.UndoCount;
            Assert.Throws<LayerLockedException>(() => d.ApplySmartMask(mask, locked.Id));
            Assert.That(d.GetLayer(locked.Id).Mask, Is.Null); Assert.That(d.UndoCount, Is.EqualTo(undo));
            // 予算
            var tight = new PaintDocument(W, H, Tile); var t = tight.AddLayer("t"); tight.ClearHistory(); tight.SourceBudgetBytes = tight.AllocatedBytes;
            Assert.That(Assert.Throws<SmartRefusedException>(() => tight.ApplySmartMask(mask, t.Id)).Reason, Is.EqualTo(SmartRefusal.Budget));
            Assert.That(tight.GetLayer(t.Id).Mask, Is.Null);
            // 別の大きさ: マスクの画素は再標本化
            var twice = new PaintDocument(W * 2, H * 2, Tile); var u = twice.AddLayer("u");
            var r3 = twice.ApplySmartMask(mask, u.Id);
            Assert.That(r3.Resampled, Is.True);
            var resampledSource = source.Resampled(W * 2, H * 2, ImageResampling.Automatic(W, H, W * 2, H * 2)).Document.GetLayer(fill.Id).Mask;
            Assert.That(Tiles(twice.GetLayer(u.Id).Mask.Surface), Is.EqualTo(Tiles(resampledSource.Surface)));
        }

        // ───────── パス・内蔵 ─────────

        [Test] public void APathOnTheModelBecomesPixelsAndA2DPathStays()
        {
            var d = new PaintDocument(W, H, Tile); var l = d.AddLayer("on model"); d.SetChannelEnabled(l.Id, PaintChannel.Color, true);
            var rendered = new SparseTileSurface(W, H, Tile); rendered.SetPixel(5, 5, new Rgba32(10, 20, 30, 255));
            d.SetPath(l.Id, new SurfacePath(Guid.NewGuid(), PaintChannel.Color, "model-fingerprint", new PathBrush(), new[] { new PathPoint(0, .2, .2, 1) }), rendered);
            var c = d.AddLayer("on canvas");
            d.SetCanvasPath(c.Id, new CanvasPath(Guid.NewGuid(), PaintChannel.Color, new PathBrush { RadiusWorld = 3 }, new[] { new CanvasPoint(4, 4, 1), new CanvasPoint(30, 20, 1) }));
            var material = RoundTrip(d.CaptureSmartMaterial(new[] { l.Id, c.Id }, "Paths"));
            Assert.That(material.Notes, Is.Empty, "notes are not stored");
            var captured = d.CaptureSmartMaterial(new[] { l.Id, c.Id }, "Paths");
            Assert.That(captured.Notes.Single(), Does.Contain("plain pixels"));
            var t = new PaintDocument(W, H, Tile); t.PlaceSmartMaterial(material);
            var placed = t.Layers.Where(x => !x.IsGroup).ToList();
            Assert.That(placed[0].Path, Is.Null); Assert.That(placed[0].GetPixel(PaintChannel.Color, 5, 5), Is.EqualTo(new Rgba32(10, 20, 30, 255)));
            Assert.That(placed[1].Path, Is.InstanceOf<CanvasPath>());
            Assert.That(t.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
        }

        [Test] public void BuiltInsAreFixedAndPlaceIntoAnySize()
        {
            var keys = BuiltInSmartMaterials.All.Select(e => e.Key).ToList();
            Assert.That(keys, Is.Unique); Assert.That(keys.Intersect(BuiltInImages.All.Select(e => e.Key)), Is.Empty, "keys do not clash with the built-in images");
            foreach (var entry in BuiltInSmartMaterials.All)
            {
                var a = BuiltInSmartMaterials.Make(entry.Key); var b = BuiltInSmartMaterials.Make(entry.Key);
                Assert.That(a.Kind, Is.EqualTo(entry.Kind)); Assert.That(a.Name, Is.EqualTo(entry.Name));
                Assert.That(a.FragmentBytes(), Is.EqualTo(b.FragmentBytes()), entry.Key + ": the same bytes every time");
                Assert.That(SmartMaterialFile.Write(a, App), Is.EqualTo(SmartMaterialFile.Write(b, App)), entry.Key + ": the same file every time");
                Assert.That(a.HasPixels, Is.False, entry.Key + ": no image is shipped"); Assert.That(a.GeneratorCount, Is.GreaterThan(0));
                var read = RoundTrip(a);
                Assert.That(read.FragmentBytes(), Is.EqualTo(a.FragmentBytes()), entry.Key + ": round trip");
                foreach (var (w, h) in new[] { (64, 64), (100, 60) })
                {
                    var inputs = Maps(w, h, "x");
                    var d = new PaintDocument(w, h, 128) { GeneratorInputs = inputs };
                    var target = d.AddFillLayer("base", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(128, 128, 128, 255) } });
                    var r = entry.Kind == SmartKind.Material ? d.PlaceSmartMaterial(read) : d.ApplySmartMask(read, target.Id);
                    Assert.That(r.Resampled, Is.True);
                    Assert.That(d.Composite(PaintChannel.Color).Distinct().Count(), Is.GreaterThan(2), entry.Key + " " + w + "×" + h + ": it shows something");
                }
            }
        }

        [Test] public void ThePreviewShowsTheGeneratorsOnTheSwatchAndChangesNothing()
        {
            foreach (var entry in BuiltInSmartMaterials.All)
            {
                var m = BuiltInSmartMaterials.Make(entry.Key);
                var a = SmartPreview.Render(m, 64); var b = SmartPreview.Render(m, 64);
                Assert.That(a, Is.EqualTo(b), entry.Key + ": deterministic");
                Assert.That(a.Length, Is.EqualTo(64 * 64 * 4));
                Assert.That(a[3], Is.Zero, "outside the swatch is transparent"); Assert.That(a[(32 * 64 + 20) * 4 + 3], Is.EqualTo(255), "inside is opaque");
                var colours = Enumerable.Range(0, 64 * 64).Where(i => a[i * 4 + 3] != 0).Select(i => a[i * 4] << 16 | a[i * 4 + 1] << 8 | a[i * 4 + 2]).Distinct().Count();
                Assert.That(colours, Is.GreaterThan(40), entry.Key + ": shaded and with the generators' pattern");
            }
            // 生成の段の効き: 縁のマスクは縁を明るく、くぼみのマスクは十字の溝を明るく
            byte Grey(byte[] p, int x, int y) => p[(y * 64 + x) * 4 + 1];
            var edges = SmartPreview.Render(BuiltInSmartMaterials.Make("edges"), 64); var cavities = SmartPreview.Render(BuiltInSmartMaterials.Make("cavities"), 64);
            Assert.That(Grey(cavities, 32, 16), Is.GreaterThan(Grey(cavities, 16, 16) + 40), "the groove shows in the cavity mask");
            Assert.That(Grey(edges, 32, 16), Is.LessThan(Grey(cavities, 32, 16)), "edges are not cavities");
            // 画素を持つもの（大きな文書から）も、元を変えずに縮めて描く
            var d = new PaintDocument(256, 256, 128); var l = d.AddLayer("p");
            for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++) l.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32((byte)x, (byte)y, 0, 255));
            var pixels = d.CaptureSmartMaterial(new[] { l.Id }, "Ramp"); long before = pixels.PixelBytes;
            var p = SmartPreview.Render(pixels, 64);
            Assert.That(p[(32 * 64 + 50) * 4], Is.GreaterThan(p[(32 * 64 + 14) * 4]), "the ramp goes left to right");
            Assert.That(pixels.PixelBytes, Is.EqualTo(before));
        }

        /// <summary>内蔵のスマートマテリアルの断片のバイト列の SHA-256（変えたら版を上げてここを直す）。正本の版の数（8〜11 バイト目）は 0 にして
        /// から数える: 正本の版が上がっても、使わない機能の並びは変わらない（YLP_FORMAT の決まり）ので、中身が同じなら同じ値。</summary>
        [Test] public void BuiltInFragmentsHaveTheirPinnedHashes()
        {
            var hashes = BuiltInSmartMaterials.All.ToDictionary(e => e.Key, e =>
            {
                var b = BuiltInSmartMaterials.Make(e.Key).FragmentBytes();
                Assert.That(BitConverter.ToInt32(b, 8), Is.EqualTo(DocumentBinary.CurrentVersion), e.Key + ": written at the current native version");
                for (int i = 8; i < 12; i++) b[i] = 0; return GenerationStore.Hash(b);
            });
            Assert.That(hashes, Is.EqualTo(PinnedBuiltIns), string.Join("\n", hashes.Select(h => h.Key + " " + h.Value)));
        }
        static readonly Dictionary<string, string> PinnedBuiltIns = new Dictionary<string, string>
        {
            { "rusty-metal", "d797d40b454e5a69173de5ae6fa613170625927b08c6db89805b61502412eb46" },
            { "dirty-paint", "63aa9ab0f5d199cdd7af7aa81b6eaf9a4e34257793c9f338ab094728576b61e9" },
            { "worn-edges", "1434213ce7621dc5844bea538e804c593b80fb8c20da00c898f97e682204b83a" },
            { "edges", "0c19b9ce0c47fcc0cb5b2bb78e465d55d33eb3a969e24489a8474cc709a9d6cc" },
            { "cavities", "144ce00178447be6a0a5e5f0c783c60bb4c38ac2e612fe57d5085fbc21dbf00f" },
            { "facing-up", "39ba2719f3a059f3836299c7591cd755ddd22c901fd8546c2111c3de54ab06f8" },
            { "bottom-to-top", "2b4599b56a088e508df939cc7c3479ff905bd3d27b24c7ec5219617c9d56cff6" },
        };
    }

    /// <summary>ChannelBlendTests の文書の大きさ（その試験の定数は private）。</summary>
    static class ChannelBlendTestsSize { public const int W = 40, H = 32; }
}
