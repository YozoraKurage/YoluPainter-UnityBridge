using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// プロパティの欄の振り分け（Substance Painter と同じく、選んでいる 1 つの物だけを出す）: 文脈の決まり（効果の段 → 描かないツール →
    /// 塗りつぶし・調整・グループの層 → 描くツール）、文脈ごとに出る欄とタブの一覧（振り分けの表のとおり）、表の形（小見出しの親、キーの重複）、
    /// 小見出しの「既定に戻す」、開閉の記憶の保存と読み替え、効果の段と Anchor の行（層の行の下の並び・落とす先・選ぶ・有効・並べ替え・消すと Undo）、
    /// オプションバーの対称と手ぶれ補正の切り替え。窓は開かない（バッチでも回る）。
    /// </summary>
    public sealed class PropertyRoutesTests
    {
        TexturePaintWindow w;
        [SetUp] public void Make() { w = ScriptableObject.CreateInstance<TexturePaintWindow>(); }
        [TearDown] public void Drop() { if (w != null) Object.DestroyImmediate(w); w = null; }

        static readonly TexturePaintWindow.PaintTool[] Tools = (TexturePaintWindow.PaintTool[])Enum.GetValues(typeof(TexturePaintWindow.PaintTool));

        /// <summary>今の文脈の欄のキー（タブがあれば「タブ:キー」）を表の順に。</summary>
        string[] Sections()
        {
            var routes = w.RoutesFor(w.PropertyContextNow());
            return routes.Select(r => (r.Tab != null ? r.Tab + ":" : "") + r.Key).ToArray();
        }

        // ───────── 文脈の決まり ─────────

        [Test] public void TheContextFollowsSubstancesRulesInOrder()
        {
            var d = w.Document; var paint = d.Layers[0];
            var fill = d.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(1, 2, 3) } });
            var adjustment = d.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels());
            var group = d.GroupLayers(new[] { paint.Id }, "Group");
            d.AddLayerMask(fill.Id);
            var expected = new Dictionary<TexturePaintWindow.PaintTool, PropertyContext>
            {
                { TexturePaintWindow.PaintTool.Brush, PropertyContext.Brush }, { TexturePaintWindow.PaintTool.Blur, PropertyContext.BrushEffect }, { TexturePaintWindow.PaintTool.Smudge, PropertyContext.BrushEffect },
                { TexturePaintWindow.PaintTool.Clone, PropertyContext.BrushEffect }, { TexturePaintWindow.PaintTool.Fill, PropertyContext.Bucket }, { TexturePaintWindow.PaintTool.Gradient, PropertyContext.Gradient },
                { TexturePaintWindow.PaintTool.PolygonFill, PropertyContext.PolygonFill }, { TexturePaintWindow.PaintTool.SelectRectangle, PropertyContext.Selection }, { TexturePaintWindow.PaintTool.SelectEllipse, PropertyContext.Selection },
                { TexturePaintWindow.PaintTool.Lasso, PropertyContext.Selection }, { TexturePaintWindow.PaintTool.MagicWand, PropertyContext.Selection }, { TexturePaintWindow.PaintTool.IdSelect, PropertyContext.IdSelect },
                { TexturePaintWindow.PaintTool.Move, PropertyContext.Move }, { TexturePaintWindow.PaintTool.Path, PropertyContext.Path }, { TexturePaintWindow.PaintTool.Eyedropper, PropertyContext.Eyedropper },
            };
            Assert.That(expected.Keys, Is.EquivalentTo(Tools), "every tool has a context");
            bool Paints(TexturePaintWindow.PaintTool t) => (int)expected[t] <= (int)PropertyContext.PolygonFill;
            foreach (var tool in Tools)
            {
                w.Tool = tool;
                // ペイントの層: そのツール
                w.SelectedLayer = paint.Id; w.EditMask = false;
                Assert.That(w.PropertyContextNow(), Is.EqualTo(expected[tool]), tool + " on a paint layer");
                // 塗りつぶし・調整・グループの層の中身: 描くツールならその層、描かないツールはツール
                foreach (var (layer, context) in new[] { (fill, PropertyContext.FillLayer), (adjustment, PropertyContext.Adjustment), (group, PropertyContext.Group) })
                {
                    w.SelectedLayer = layer.Id;
                    Assert.That(w.PropertyContextNow(), Is.EqualTo(Paints(tool) ? context : expected[tool]), tool + " on " + layer.Name);
                }
                // 塗りつぶしの層のマスクに描く: 描くツールの文脈
                w.SelectedLayer = fill.Id; w.EditMask = true;
                Assert.That(w.PropertyContextNow(), Is.EqualTo(expected[tool]), tool + " on the fill's mask");
                w.EditMask = false;
            }
            // 効果の段を選ぶと、どのツールでもその段
            w.SelectedLayer = fill.Id;
            var blur = d.AddFilter(fill.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(2));
            w.SelectedFilter = blur.Id;
            foreach (var tool in Tools) { w.Tool = tool; Assert.That(w.PropertyContextNow(), Is.EqualTo(PropertyContext.Effect), tool + " with a filter selected"); }
            Assert.That(w.SelectedEffect(), Is.EqualTo((blur, FilterTarget.Mask)));
            // ほかの層の段は選んでいないのと同じ（選んだ層のスタックにある段だけ）
            w.SelectedLayer = paint.Id;
            Assert.That(w.SelectedEffect().effect, Is.Null); Assert.That(w.PropertyContextNow(), Is.Not.EqualTo(PropertyContext.Effect));
            // ツールを選び直すと段の選択を外し、欄はそのツールに（Photoshop の流れ）
            w.SelectedLayer = fill.Id; w.SelectedFilter = blur.Id;
            w.SelectTool(TexturePaintWindow.PaintTool.SelectRectangle);
            Assert.That(w.SelectedFilter, Is.EqualTo(Guid.Empty)); Assert.That(w.PropertyContextNow(), Is.EqualTo(PropertyContext.Selection));
        }

        // ───────── 文脈ごとの欄（振り分けの表） ─────────

        [Test] public void EachContextShowsTheSectionsOfTheTable()
        {
            var d = w.Document; var paint = d.Layers[0]; w.SelectedLayer = paint.Id;
            string[] Brush = { "brush:brush", "brush:brush-jitter", "brush:brush-texture", "brush:brush-dual", "brush:brush-color", "brush:brush-fade", "alpha:brush-alpha", "stencil:brush-stencil", "material:brush-material" };
            w.Tool = TexturePaintWindow.PaintTool.Brush; Assert.That(Sections(), Is.EqualTo(Brush));
            Assert.That(w.PropertyTabsNow(), Is.EqualTo(new[] { TexturePaintWindow.TabBrush, TexturePaintWindow.TabAlpha, TexturePaintWindow.TabStencil, TexturePaintWindow.TabMaterial }), "Substance's Brush | Alpha | Stencil | Material tabs");
            w.Tool = TexturePaintWindow.PaintTool.Blur;
            Assert.That(Sections(), Is.EqualTo(new[] { "brush:brush-effect", "brush:brush", "brush:brush-jitter", "brush:brush-texture", "brush:brush-dual", "brush:brush-fade", "alpha:brush-alpha", "stencil:brush-stencil", "material:brush-material" }), "no colour dynamics for pixel effects");
            var table = new (TexturePaintWindow.PaintTool tool, string[] keys)[]
            {
                (TexturePaintWindow.PaintTool.Fill, new[] { "surface-pick", "brush-material" }),
                (TexturePaintWindow.PaintTool.PolygonFill, new[] { "surface-pick", "brush-material" }),
                (TexturePaintWindow.PaintTool.Gradient, new[] { "brush-material" }),
                (TexturePaintWindow.PaintTool.SelectRectangle, new[] { "surface-pick", "selection-modify" }),
                (TexturePaintWindow.PaintTool.MagicWand, new[] { "surface-pick", "selection-modify" }),
                (TexturePaintWindow.PaintTool.IdSelect, new[] { "id-map", "selection-modify" }),
                (TexturePaintWindow.PaintTool.Move, new[] { "move" }),
                (TexturePaintWindow.PaintTool.Path, new[] { "path", "brush-material" }),
                (TexturePaintWindow.PaintTool.Eyedropper, new string[0]),
            };
            foreach (var (tool, keys) in table) { w.Tool = tool; Assert.That(Sections(), Is.EqualTo(keys), tool.ToString()); Assert.That(w.PropertyTabsNow(), Is.Empty, tool + " has no tabs"); }
            // マスクに描く: マテリアルの代わりにマスクのタブ
            d.AddLayerMask(paint.Id); w.EditMask = true; w.Tool = TexturePaintWindow.PaintTool.Brush;
            Assert.That(Sections(), Is.EqualTo(Brush.Take(8).Append("mask:mask").ToArray()), "the alpha and the stencil also paint the mask; the material does not");
            w.EditMask = false;
            // Normal のチャンネル: 法線を傾きで選ぶ欄（ブラシのタブ）
            w.Channel = PaintChannel.Normal;
            Assert.That(Sections(), Does.Contain("brush:brush-normal")); w.Channel = PaintChannel.Color;
            // 層の種類
            var fill = d.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(1, 2, 3) } });
            w.SelectedLayer = fill.Id; Assert.That(Sections(), Is.EqualTo(new[] { "layer" }), "a plain fill (no image, default projection)");
            d.SetFillProjection(fill.Id, FillProjection.Default.WithMode(FillProjectionMode.Triplanar));
            Assert.That(Sections(), Is.EqualTo(new[] { "layer", "projection" }), "a projected fill shows its projection");
            w.SelectedLayer = d.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert()).Id; Assert.That(Sections(), Is.EqualTo(new[] { "layer" }));
            w.SelectedLayer = d.GroupLayers(new[] { paint.Id }, "G").Id; Assert.That(Sections(), Is.EqualTo(new[] { "layer" }));
            // 効果の段
            w.SelectedLayer = fill.Id; w.SelectedFilter = d.AddFilter(fill.Id, FilterTarget.Content, FilterSettings.Invert(), new[] { PaintChannel.Color }).Id;
            Assert.That(Sections(), Is.EqualTo(new[] { "effect" }));
        }

        /// <summary>表の形: 小見出しの親は同じ文脈・同じタブの前の行にある大見出し、同じ文脈とタブにキーの重複が無い、どの行も描く関数を持つ。
        /// 欄を足す担当が壊しやすい所。</summary>
        [Test] public void TheTableIsWellFormed()
        {
            var routes = w.PropertyRoutes;
            Assert.That(routes.All(r => r.Draw != null && !string.IsNullOrEmpty(r.Key) && r.Contexts != null && r.Contexts.Length > 0 || r.Contexts.Length == 0), Is.True);
            foreach (PropertyContext context in Enum.GetValues(typeof(PropertyContext)))
            {
                var inContext = routes.Where(r => Array.IndexOf(r.Contexts, context) >= 0).ToList();
                foreach (var group in inContext.GroupBy(r => r.Tab + "|" + r.Key))
                    Assert.That(group.Select(r => r.When == null).Count(always => always), Is.LessThanOrEqualTo(1), context + ": " + group.Key + " is routed twice");
                for (int i = 0; i < inContext.Count; i++)
                {
                    var r = inContext[i];
                    if (r.Parent == null) continue;
                    Assert.That(inContext.Take(i).Any(p => p.Key == r.Parent && p.Tab == r.Tab && p.Parent == null), Is.True, context + ": " + r.Key + " has no major section " + r.Parent + " before it");
                    // 小見出しは親の中に見える: 同じタブで手前にある最後の大見出しが親（間に別の大見出しを挟むと、その下に見えてしまう）
                    var lastMajor = inContext.Take(i).LastOrDefault(p => p.Tab == r.Tab && p.Parent == null);
                    Assert.That(lastMajor?.Key, Is.EqualTo(r.Parent), context + ": " + r.Key + " would show under " + lastMajor?.Key);
                    Assert.That(r.Title, Is.Null.Or.Not.Null); // 小見出しの題は中身の関数か表のどちらでも
                }
            }
            // 開閉の初め: 細かい設定（小見出し）は閉じて始める
            foreach (var key in new[] { "brush-jitter", "brush-texture", "brush-dual", "brush-color", "brush-fade" })
                Assert.That(routes.Where(r => r.Key == key).All(r => r.ClosedAtFirst && r.Parent == "brush" && r.Reset != null), Is.True, key);
        }

        [Test] public void ResettingASubsectionBringsBackTheDefaultsOfThatGroupOnly()
        {
            var b = w.Brush; var d = new TexturePaintWindow.BrushState();
            b.sizeJitter = .4f; b.scatter = 2; b.count = 5; b.textureDepth = .7f; b.dualEnabled = true; b.dualCount = 3; b.hueJitter = .5f; b.colorPerTip = false; b.fadeSize = 9; b.tiltAngle = true;
            b.radius = 40; b.spacing = .5f;
            w.ResetBrushGroup("brush-jitter");
            Assert.That((b.sizeJitter, b.scatter, b.count), Is.EqualTo((d.sizeJitter, d.scatter, d.count)));
            Assert.That(b.textureDepth, Is.EqualTo(.7f), "other groups are kept");
            w.ResetBrushGroup("brush-texture"); Assert.That(b.textureDepth, Is.EqualTo(d.textureDepth));
            w.ResetBrushGroup("brush-dual"); Assert.That((b.dualEnabled, b.dualCount), Is.EqualTo((d.dualEnabled, d.dualCount)));
            w.ResetBrushGroup("brush-color"); Assert.That((b.hueJitter, b.colorPerTip), Is.EqualTo((d.hueJitter, d.colorPerTip)));
            w.ResetBrushGroup("brush-fade"); Assert.That((b.fadeSize, b.tiltAngle), Is.EqualTo((d.fadeSize, d.tiltAngle)));
            Assert.That((b.radius, b.spacing), Is.EqualTo((40f, .5f)), "the main brush values are not part of a subsection");
            Assert.That(() => w.ResetBrushGroup("brush"), Throws.ArgumentException);
            Assert.That(w.Document.UndoCount, Is.Zero, "brush settings are not document edits");
        }

        // ───────── 開閉の記憶 ─────────

        [Test] public void TheSectionMemoryRoundTripsAndReadsRenamedKeys()
        {
            var open = new Dictionary<string, bool> { { "brush", true }, { "brush-jitter", false }, { "mesh-maps", true } };
            var tabs = new Dictionary<PropertyContext, string> { { PropertyContext.Brush, TexturePaintWindow.TabMaterial } };
            string json = PropertySectionStore.ToJson(open, tabs);
            Assert.That(json, Does.Contain("\"version\":" + PropertySectionStore.CurrentVersion));
            var back = PropertySectionStore.FromJson(json);
            Assert.That(back.Open, Is.EquivalentTo(open)); Assert.That(back.Tabs, Is.EquivalentTo(tabs));
            // 前の欄のキー（フィルターの一覧）は今の欄（選んだ効果の段）に読み替える。知らない文脈とタブの無い組は捨てる
            var old = PropertySectionStore.FromJson("{\"open\":[\"filters\",\"layer\"],\"closed\":[\"pose\"],\"tabContexts\":[\"Brush\",\"NoSuchContext\",\"Effect\"],\"tabs\":[\"mask\",\"x\"]}");
            Assert.That(old.Open, Is.EquivalentTo(new Dictionary<string, bool> { { "effect", true }, { "layer", true }, { "pose", false } }));
            Assert.That(old.Tabs, Is.EquivalentTo(new Dictionary<PropertyContext, string> { { PropertyContext.Brush, "mask" } }));
            Assert.That(PropertySectionStore.FromJson("").Open, Is.Empty, "nothing saved yet: the defaults");
            Assert.That(PropertySectionStore.FromJson("{\"version\":99,\"open\":[\"brush-dual\"],\"future\":[1,2]}").Open["brush-dual"], Is.True, "a newer version is read as far as it is understood");
            Assert.That(() => PropertySectionStore.FromJson("not json"), Throws.ArgumentException);
            Assert.That(DockLayoutStore.UseDefaults, Is.True, "tests do not read or write the person's EditorPrefs");
            Assert.That(PropertySectionStore.Load().Open, Is.Empty);
        }

        // ───────── 効果の段の行 ─────────

        [Test] public void EffectRowsFollowTheirLayerAndKeepTheDropTargetsOfTheLayerRows()
        {
            var d = w.Document; var bottom = d.Layers[0];
            var top = d.AddLayer("Top"); d.AddLayerMask(top.Id);
            var blur = d.AddFilter(top.Id, FilterTarget.Content, FilterSettings.GaussianBlur(3), new[] { PaintChannel.Color });
            var invert = d.AddFilter(top.Id, FilterTarget.Content, FilterSettings.Invert(), new[] { PaintChannel.Color });
            var levels = d.AddFilter(top.Id, FilterTarget.Mask, FilterSettings.Levels());
            var rows = w.LayerListRows();
            Assert.That(rows.Select(r => r.IsEffect ? r.Effect.Id.ToString() : r.Layer.Name), Is.EqualTo(new[] { "Top", invert.Id.ToString(), blur.Id.ToString(), levels.Id.ToString(), bottom.Name }),
                "under the layer, the pixel stack from the last applied, then the mask stack");
            Assert.That(rows.Select(r => r.Height), Is.EqualTo(new[] { 30f, TexturePaintWindow.EffectRowHeight, TexturePaintWindow.EffectRowHeight, TexturePaintWindow.EffectRowHeight, 30f }));
            Assert.That(rows.Select(r => r.Y), Is.EqualTo(new[] { 0f, 30f, 52f, 74f, 96f }));
            Assert.That(rows[3].Target, Is.EqualTo(FilterTarget.Mask));
            // 落とす先: 効果の段の行の上はその層の下（次の層の行の上の線）
            Assert.That(w.LayerDropTargetForTests(10), Is.EqualTo((0, (PaintLayer)null)), "upper half of the top row");
            Assert.That(w.LayerDropTargetForTests(60), Is.EqualTo((1, (PaintLayer)null)), "on an effect row: below its layer");
            Assert.That(w.LayerDropTargetForTests(100), Is.EqualTo((1, (PaintLayer)null)), "upper half of the bottom row");
            Assert.That(w.LayerDropTargetForTests(125), Is.EqualTo((2, (PaintLayer)null)), "lower half of the bottom row");
            Assert.That(w.LayerDropTargetForTests(500), Is.EqualTo((2, (PaintLayer)null)), "below the list");
        }

        /// <summary>Anchor（Substance のアンカーポイント）も層の子の行: 層の Anchor は層の段の上、マスクの Anchor はマスクの段の上。押すと
        /// プロパティの欄は Anchor（名前・外す）、外すと選びも外れ、Undo で戻る。落とす先は効果の行と同じくその層の下。</summary>
        [Test] public void AnchorRowsSitWithTheEffectsAndSelectingOneShowsTheAnchor()
        {
            var d = w.Document; var bottom = d.Layers[0];
            var top = d.AddLayer("Top"); d.AddLayerMask(top.Id);
            var blur = d.AddFilter(top.Id, FilterTarget.Content, FilterSettings.GaussianBlur(3), new[] { PaintChannel.Color });
            var levels = d.AddFilter(top.Id, FilterTarget.Mask, FilterSettings.Levels());
            var layerAnchor = d.AddAnchor(top.Id, AnchorPlacement.Layer, "Top result");
            var maskAnchor = d.AddAnchor(top.Id, AnchorPlacement.Mask, "Top mask");
            d.ClearHistory();
            var rows = w.LayerListRows();
            string Name(TexturePaintWindow.LayerListRow r) => r.IsAnchor ? "anchor:" + r.Anchor.Name : r.IsEffect ? "effect:" + r.Effect.Id : r.Layer.Name;
            Assert.That(rows.Select(Name), Is.EqualTo(new[] { "Top", "anchor:Top result", "effect:" + blur.Id, "anchor:Top mask", "effect:" + levels.Id, bottom.Name }));
            Assert.That(rows.Select(r => r.LastChild), Is.EqualTo(new[] { false, false, false, false, true, false }), "the guide line stops at the last child row");
            Assert.That(rows[3].Target, Is.EqualTo(FilterTarget.Mask));
            Assert.That(w.LayerDropTargetForTests(rows[1].Y + 5), Is.EqualTo((1, (PaintLayer)null)), "on an anchor row: below its layer");
            // 選ぶ: プロパティの欄は Anchor（どのツールでも。効果の段と同じ）
            w.SelectedLayer = bottom.Id;
            w.SelectEffect(top.Id, maskAnchor.Id);
            Assert.That((w.SelectedLayer, w.PropertyContextNow()), Is.EqualTo((top.Id, PropertyContext.Anchor)));
            Assert.That(w.SelectedAnchor(), Is.EqualTo((maskAnchor, AnchorPlacement.Mask)));
            Assert.That(Sections(), Is.EqualTo(new[] { "anchor" }));
            w.Tool = TexturePaintWindow.PaintTool.Move; Assert.That(w.PropertyContextNow(), Is.EqualTo(PropertyContext.Anchor), "a selected anchor wins over the tool, like an effect");
            Assert.That(w.PropertyContextTitle(), Does.Contain("Anchor"));
            // ほかの層の Anchor は選んでいないのと同じ
            w.SelectedLayer = bottom.Id; Assert.That(w.SelectedAnchor().anchor, Is.Null);
            // 外すと選びも外れ、Undo で戻る
            w.SelectEffect(top.Id, layerAnchor.Id);
            w.RemoveAnchorOf(layerAnchor);
            Assert.That(d.GetLayer(top.Id).Anchor, Is.Null); Assert.That(w.SelectedAnchor().anchor, Is.Null); Assert.That(w.PropertyContextNow(), Is.Not.EqualTo(PropertyContext.Anchor));
            Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo(); Assert.That(d.GetLayer(top.Id).Anchor?.Name, Is.EqualTo("Top result"));
        }

        [Test] public void EffectRowOperationsAreOneUndoStepEach()
        {
            var d = w.Document; var a = d.Layers[0]; var b = d.AddLayer("B");
            var blur = d.AddFilter(a.Id, FilterTarget.Content, FilterSettings.GaussianBlur(3), new[] { PaintChannel.Color });
            var invert = d.AddFilter(a.Id, FilterTarget.Content, FilterSettings.Invert(), new[] { PaintChannel.Color });
            d.ClearHistory();
            w.SelectedLayer = b.Id; w.EditMask = false;
            w.SelectEffect(a.Id, blur.Id);
            Assert.That((w.SelectedLayer, w.SelectedFilter, w.PropertyContextNow()), Is.EqualTo((a.Id, blur.Id, PropertyContext.Effect)), "choosing a row selects its layer and the effect");
            FilterEffect Find(FilterEffect e) => a.Filters.Single(f => f.Id == e.Id); // 段は値で置き換わる（変えるたびに新しい段）
            w.SetEffectEnabled(a.Id, blur.Id, false); Assert.That(Find(blur).Enabled, Is.False);
            w.MoveEffect(a.Id, invert.Id, 0); Assert.That(a.Filters.Select(f => f.Id), Is.EqualTo(new[] { invert.Id, blur.Id }));
            w.RemoveEffect(a.Id, blur.Id); Assert.That(a.Filters.Select(f => f.Id), Is.EqualTo(new[] { invert.Id }));
            Assert.That(w.SelectedFilter, Is.EqualTo(Guid.Empty), "the removed effect is no longer selected"); Assert.That(w.PropertyContextNow(), Is.EqualTo(PropertyContext.Brush));
            Assert.That(d.UndoCount, Is.EqualTo(3));
            d.Undo(); Assert.That(a.Filters.Count, Is.EqualTo(2));
            d.Undo(); Assert.That(a.Filters.Select(f => f.Id), Is.EqualTo(new[] { blur.Id, invert.Id }));
            d.Undo(); Assert.That(a.Filters.Single(f => f.Id == blur.Id).Enabled, Is.True);
            // 型で断られる段は足さない（ノーマルのチャンネルのシャープ）。断りは状態の欄に
            w.Channel = PaintChannel.Normal; int steps = d.UndoCount;
            Assert.That(w.AddFilter(FilterTarget.Content, FilterSettings.Sharpen(2, 1, 0)), Is.Null);
            Assert.That(d.UndoCount, Is.EqualTo(steps)); Assert.That(w.StatusMessage, Does.Contain("normals"));
        }

        // ───────── オプションバー ─────────

        [Test] public void TheOptionsBarTogglesEverySymmetryAndTheStabilizerAndRemembersThem()
        {
            Assert.That(w.AnySymmetry, Is.False);
            w.ToggleAllSymmetry(); Assert.That((w.Symmetry, w.RadialSymmetry3D, w.Brush.canvasSymmetry), Is.EqualTo((true, false, CanvasSymmetryMode.None)), "nothing remembered: the 3D mirror");
            w.RadialSymmetry3D = true; w.Brush.canvasSymmetry = CanvasSymmetryMode.Both;
            w.ToggleAllSymmetry(); Assert.That(w.AnySymmetry, Is.False, "every kind goes off");
            w.ToggleAllSymmetry(); Assert.That((w.Symmetry, w.RadialSymmetry3D, w.Brush.canvasSymmetry), Is.EqualTo((true, true, CanvasSymmetryMode.Both)), "the same kinds come back");
            w.Symmetry = false; w.RadialSymmetry3D = false;
            w.ToggleAllSymmetry(); Assert.That(w.AnySymmetry, Is.False, "2D alone is on, so it goes off");
            w.ToggleAllSymmetry(); Assert.That((w.Symmetry, w.Brush.canvasSymmetry), Is.EqualTo((false, CanvasSymmetryMode.Both)));
            // 手ぶれ補正: 切ると 0、入れ直すと前の長さ
            w.Stabilizer = 35; w.SetStabilizerOn(false); Assert.That(w.Stabilizer, Is.Zero);
            w.SetStabilizerOn(true); Assert.That(w.Stabilizer, Is.EqualTo(35));
            w.SetStabilizerOn(false); w.SetStabilizerOn(true); Assert.That(w.Stabilizer, Is.EqualTo(35));
            Assert.That(w.Document.UndoCount, Is.Zero, "settings are not document edits");
            Assert.That(w.OptionPopupSize(OptionPopupKind.Symmetry).x, Is.EqualTo(TexturePaintWindow.OptionPopupWidth));
        }

        /// <summary>2 行のスライダーの値の箱は、欄のいちばん狭い幅（ドック 220 の欄の中身）でも、広い値（−180°・256 px・100%・0.###・
        /// 9.99）を切らずに収め、名前に 40 px 以上を残す。箱の幅は min・max の広いほうで決まるので、値を動かしても揺れない。</summary>
        [Test] public void TheTwoLineSliderKeepsItsNumberWholeInTheNarrowestPanel()
        {
            float narrowest = 220 - 1 - 8 - 2 * PaintTheme.Padding - TexturePaintWindow.SectionIndentFor(220 - 1 - 8 - 2 * PaintTheme.Padding) - 22; // 小見出しの中の字下げまで
            var r = new Rect(0, 0, narrowest, PaintGui.SliderRowHeight);
            foreach (var text in new[] { "-180°", "256 px", "100%", "0.004", "9.99", "-10", "500 px" })
            {
                float textWidth = PaintGui.TextWidth(text, PaintTheme.Value);
                var box = PaintGui.TwoLineValueBox(r, textWidth);
                Assert.That(box.width, Is.GreaterThanOrEqualTo(textWidth + 8), text + ": the number fits its box");
                Assert.That(box.x, Is.GreaterThanOrEqualTo(40), text + ": the label keeps room");
                Assert.That(box.xMax, Is.EqualTo(r.xMax), text + ": the box is on the right of the first line");
                Assert.That(box.yMax, Is.LessThan(r.y + PaintGui.SliderRowHeight / 2 + 2), text + ": on the first line");
            }
            Assert.That(PaintGui.SliderRowHeight, Is.GreaterThanOrEqualTo(30), "two lines");
        }

        /// <summary>アルファのタブの先端の一覧: 円形と内蔵の先端が先頭で、同じ ID は 1 回だけ。選ぶと先端だけが替わる（大きさなどはそのまま）。</summary>
        [Test] public void TheAlphaListStartsWithTheRoundAndBuiltInTipsAndPickingOneChangesOnlyTheTip()
        {
            var tips = w.TipChoices();
            Assert.That(tips[0].id, Is.EqualTo(""), "the round tip first");
            Assert.That(tips.Select(t => t.id), Is.Unique);
            Assert.That(tips.Where(t => t.id.StartsWith("builtin:", StringComparison.Ordinal)).Select(t => t.id.Substring(8)), Is.EquivalentTo(BuiltInBrushes.TipIds));
            w.Brush.radius = 21; w.Brush.flow = .4f;
            string tip = tips.First(t => t.id.StartsWith("builtin:", StringComparison.Ordinal)).id;
            w.SetTip(tip);
            Assert.That((w.Brush.tipId, w.Brush.radius, w.Brush.flow), Is.EqualTo((tip, 21f, .4f)));
            Assert.That(w.GetBrush().Tip, Is.SameAs(BrushTips.Resolve(tip)), "the stroke uses it");
            w.SetTip(""); Assert.That(w.GetBrush().Tip, Is.Null, "back to the round tip");
            Assert.That(w.Document.UndoCount, Is.Zero, "brush settings are not document edits");
            // 一覧の見出しは言語を替えると訳し直す（前は一覧を言語の設定で覚えていて、試験と描き手の一時の言語では英語のまま残った）
            try
            {
                L.OverrideLanguage(PainterLanguage.Japanese);
                Assert.That(w.TipChoices()[0].group, Is.EqualTo(L.TrIn("brush", "Built-in")).And.Not.EqualTo("Built-in"));
            }
            finally { L.OverrideLanguage(PainterLanguage.English); }
            Assert.That(w.TipChoices()[0].group, Is.EqualTo("Built-in"));
        }
    }
}
