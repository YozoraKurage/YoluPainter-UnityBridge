using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>パネルの配置（左右の列・順・タブのまとまり・別のウィンドウ・畳む・高さの比・列の幅）と、前の版の配置の読み替え。
    /// ウィンドウは表示しない（別のウィンドウを実際に開くのは PanelWindowTests）。</summary>
    public sealed class DockLayoutTests
    {
        static string[] In(DockLayout l, DockPlace place) => l.PanelsIn(place).ToArray();
        static DockLayout RoundTrip(DockLayout l) => DockLayout.FromJson(JsonUtility.ToJson(l));

        /// <summary>前の版（タブも別のウィンドウも無い、版の番号の無い形式）が EditorPrefs に書いた JSON を、そのまま読み替える。</summary>
        [Test] public void TheFirstFormatInEditorPrefsIsReadAsItWas()
        {
            const string firstFormat = "{\"left\":[\"layers\"],\"right\":[\"color\",\"textureSet\",\"properties\"],\"leftWidth\":280.0,\"rightWidth\":320.0,\"collapsed\":[\"color\"],\"weightIds\":[\"layers\",\"properties\"],\"weights\":[3.0,0.5]}";
            var layout = DockLayout.FromJson(firstFormat);
            Assert.That(layout.version, Is.EqualTo(DockLayout.CurrentVersion));
            Assert.That(In(layout, DockPlace.Left), Is.EqualTo(new[] { "layers", "textureSetSettings" }), "Texture Set Settings joins the Layers panel as a tab (Substance's Layers | Texture Set Settings)");
            Assert.That(layout.GroupOf("layers").Active, Is.EqualTo("layers"), "the shown tab stays");
            Assert.That(In(layout, DockPlace.Right), Is.EqualTo(new[] { "color", "textureSet", "properties", "material", "assets" }), "panels the old layout did not know are added at the end of the right column");
            Assert.That(layout.GroupOf("material").collapsed && layout.GroupOf("assets").collapsed, Is.True, "folded, so the old columns keep their heights");
            Assert.That(layout.groups.Where(g => !g.panels.Contains("layers")).All(g => g.panels.Count == 1 && !g.Floating), Is.True, "the first format had no tabs and no separate windows");
            Assert.That(layout.GroupOf("color").collapsed, Is.True); Assert.That(layout.GroupOf("layers").collapsed, Is.False);
            Assert.That(layout.Weight(layout.GroupOf("layers")), Is.EqualTo(3)); Assert.That(layout.Weight(layout.GroupOf("properties")), Is.EqualTo(.5f));
            Assert.That((layout.leftWidth, layout.rightWidth), Is.EqualTo((280f, 320f)));
            // 書き直すと新しい形式になり、もう一度読んでも同じ
            var again = RoundTrip(layout);
            Assert.That(JsonUtility.ToJson(again), Does.Contain("\"version\":" + DockLayout.CurrentVersion).And.Not.Contain("weightIds"));
            Assert.That(In(again, DockPlace.Left), Is.EqualTo(In(layout, DockPlace.Left))); Assert.That(again.GroupOf("color").collapsed, Is.True);
        }

        [Test] public void ABrokenFirstFormatIsRepaired()
        {
            const string broken = "{\"left\":[\"layers\",\"unknown\",\"layers\"],\"collapsed\":[\"x\",\"color\",\"color\"],\"leftWidth\":5.0,\"weightIds\":[\"layers\"],\"weights\":[]}";
            var layout = DockLayout.FromJson(broken);
            Assert.That(In(layout, DockPlace.Left), Is.EqualTo(new[] { "layers", "textureSetSettings" }));
            Assert.That(In(layout, DockPlace.Right), Is.EquivalentTo(new[] { "color", "textureSet", "properties", "material", "assets" }), "missing panels come back on the right");
            Assert.That(layout.GroupOf("color").collapsed, Is.True);
            Assert.That(layout.leftWidth, Is.EqualTo(DockLayout.MinWidth));
            Assert.That(layout.Weight(layout.GroupOf("layers")), Is.EqualTo(1), "mismatched weights are dropped");
            Assert.That(() => DockLayout.FromJson("not json"), Throws.ArgumentException, "DockLayoutStore falls back to the default on this");
        }

        [Test] public void ABrokenLayoutIsRepaired()
        {
            var layout = new DockLayout
            {
                groups = new List<DockGroup>
                {
                    new DockGroup { id = "a", panels = new List<string> { "layers", "unknown", "layers" }, active = "nope", place = (DockPlace)7, weight = float.NaN },
                    new DockGroup { id = "a", panels = new List<string> { "layers", "color" }, place = DockPlace.Floating, home = DockPlace.Floating, homeIndex = -3, window = new Rect(float.NaN, 0, 300, 200) },
                    null,
                    new DockGroup { id = null, panels = null },
                    new DockGroup { id = "b", panels = new List<string>() },
                },
                leftWidth = float.NaN, rightWidth = 9999,
            }.Normalized();
            Assert.That(layout.groups.Select(g => g.panels.ToArray()), Is.EqualTo(new[] { new[] { "layers", "textureSetSettings" }, new[] { "color" }, new[] { "textureSet" }, new[] { "material" }, new[] { "assets" }, new[] { "properties" } }), "unknown, duplicate and empty entries go; missing panels are added on the right (Texture Set Settings as a tab of Layers)");
            Assert.That(layout.GroupOf("material").collapsed && layout.GroupOf("assets").collapsed, Is.True);
            var first = layout.GroupOf("layers"); var floating = layout.GroupOf("color");
            Assert.That((first.active, first.place, first.weight), Is.EqualTo(("layers", DockPlace.Right, 1f)));
            Assert.That(floating.id, Is.Not.EqualTo("a"), "a duplicate group name is renamed");
            Assert.That((floating.place, floating.home, floating.homeIndex, floating.window), Is.EqualTo((DockPlace.Floating, DockPlace.Right, 0, default(Rect))));
            Assert.That(layout.groups.Select(g => g.id).Distinct().Count(), Is.EqualTo(layout.groups.Count));
            Assert.That((layout.leftWidth, layout.rightWidth), Is.EqualTo((260f, DockLayout.MaxWidth)));
            // 知らない新しい版の JSON も、わかる所だけ読む
            var future = DockLayout.FromJson("{\"version\":99,\"groups\":[{\"id\":\"x\",\"panels\":[\"layers\",\"brushes\"],\"place\":2,\"window\":{\"serializedVersion\":\"2\",\"x\":10,\"y\":20,\"width\":300,\"height\":200},\"weight\":1}],\"leftWidth\":250}");
            Assert.That(future.GroupOf("layers").Floating, Is.True); Assert.That(future.GroupOf("layers").window, Is.EqualTo(new Rect(10, 20, 300, 200)));
            Assert.That(In(future, DockPlace.Right), Is.EqualTo(new[] { "color", "textureSet", "material", "assets", "properties" }));
        }

        /// <summary>前の版の既定の配置（右の列に 1 つずつ。テクスチャセットの設定は最後に畳んで）。配置の操作の試験はこの並びで確かめる
        /// （今の既定の並びは <see cref="TheDefaultIsSubstancePaintersArrangement"/>）。</summary>
        internal static DockLayout Classic() => DockLayout.FromJson("{\"right\":[\"color\",\"textureSet\",\"layers\",\"material\",\"assets\",\"properties\",\"textureSetSettings\"],\"collapsed\":[\"material\",\"assets\",\"textureSetSettings\"]}");

        /// <summary>既定の配置は Substance Painter の並び: 左にアセット（シェルフ）とカラー、右に上からテクスチャセットの一覧、
        /// 「レイヤー｜テクスチャセットの設定｜マテリアル」のタブ（レイヤーが見えている）、プロパティ。どれも畳まない。</summary>
        [Test] public void TheDefaultIsSubstancePaintersArrangement()
        {
            var layout = DockLayout.Default();
            Assert.That(In(layout, DockPlace.Left), Is.EqualTo(new[] { "assets", "color" }));
            Assert.That(In(layout, DockPlace.Right), Is.EqualTo(new[] { "textureSet", "layers", "textureSetSettings", "material", "properties" }));
            Assert.That(layout.Column(DockPlace.Right).Select(g => g.panels.Count), Is.EqualTo(new[] { 1, 3, 1 }));
            Assert.That(layout.GroupOf("layers").Active, Is.EqualTo("layers"));
            Assert.That(layout.groups.Any(g => g.collapsed || g.Floating), Is.False);
            Assert.That(layout.groups.SelectMany(g => g.panels), Is.EquivalentTo(DockLayout.KnownPanels));
            Assert.That(JsonUtility.ToJson(RoundTrip(layout)), Is.EqualTo(JsonUtility.ToJson(layout)), "the default reads back as it is");
        }

        [Test] public void PanelsMoveWithinAndBetweenColumnsAndRoundTripAsJson()
        {
            var layout = Classic();
            Assert.That(In(layout, DockPlace.Right), Is.EqualTo(new[] { "color", "textureSet", "layers", "material", "assets", "properties", "textureSetSettings" }));
            Assert.That(layout.groups.Where(g => g.collapsed).Select(g => g.Active), Is.EqualTo(new[] { "material", "assets", "textureSetSettings" }));
            layout.MovePanel("color", DockPlace.Right, 7); // 右の列の一番下へ（抜いた分を詰める）
            Assert.That(In(layout, DockPlace.Right), Is.EqualTo(new[] { "textureSet", "layers", "material", "assets", "properties", "textureSetSettings", "color" }));
            layout.MovePanel("layers", DockPlace.Left, 0);
            Assert.That(In(layout, DockPlace.Left), Is.EqualTo(new[] { "layers" })); Assert.That(In(layout, DockPlace.Right), Is.EqualTo(new[] { "textureSet", "material", "assets", "properties", "textureSetSettings", "color" }));
            layout.MovePanel("properties", DockPlace.Left, 0);
            Assert.That(In(layout, DockPlace.Left), Is.EqualTo(new[] { "properties", "layers" }));
            layout.SetCollapsed(layout.GroupOf("color").id, true); layout.SetWeight(layout.GroupOf("layers"), 3); layout.leftWidth = 280;
            var back = RoundTrip(layout);
            Assert.That(In(back, DockPlace.Left), Is.EqualTo(In(layout, DockPlace.Left))); Assert.That(In(back, DockPlace.Right), Is.EqualTo(In(layout, DockPlace.Right)));
            Assert.That(back.GroupOf("color").collapsed, Is.True); Assert.That(back.Weight(back.GroupOf("layers")), Is.EqualTo(3)); Assert.That(back.leftWidth, Is.EqualTo(280));
            Assert.That(() => layout.MovePanel("nope", DockPlace.Left, 0), Throws.ArgumentException);
            Assert.That(() => layout.MoveGroup("nope", DockPlace.Left, 0), Throws.ArgumentException);
            Assert.That(() => layout.MoveGroup(layout.GroupOf("color").id, DockPlace.Floating, 0), Throws.ArgumentException, "floating goes through FloatGroup (it needs a window rect)");
        }

        [Test] public void TabsJoinSwitchReorderAndSplit()
        {
            var layout = Classic(); string tabs = layout.GroupOf("layers").id;
            layout.Join("properties", tabs);
            var g = layout.Group(tabs);
            Assert.That(g.panels, Is.EqualTo(new[] { "layers", "properties" })); Assert.That(g.Active, Is.EqualTo("properties"), "the dropped panel is shown");
            Assert.That(layout.Column(DockPlace.Right).Count, Is.EqualTo(6)); Assert.That(In(layout, DockPlace.Right), Is.EqualTo(new[] { "color", "textureSet", "layers", "properties", "material", "assets", "textureSetSettings" }));
            layout.SetActive("layers"); Assert.That(g.Active, Is.EqualTo("layers"));
            layout.SetCollapsed(tabs, true); layout.SetActive("properties");
            Assert.That(g.collapsed, Is.False, "showing a tab opens a folded group");
            layout.Join("color", tabs, 0); Assert.That(g.panels, Is.EqualTo(new[] { "color", "layers", "properties" }));
            layout.Join("color", tabs, 3); Assert.That(g.panels, Is.EqualTo(new[] { "layers", "properties", "color" }), "reordering inside the group");
            layout.Join("color", tabs, 1); Assert.That(g.panels, Is.EqualTo(new[] { "layers", "color", "properties" }));
            // タブを列の別の場所へ動かすと分かれる（もとのまとまりの見えるタブは残りの最初へ）
            layout.MovePanel("color", DockPlace.Left, 0);
            Assert.That(In(layout, DockPlace.Left), Is.EqualTo(new[] { "color" })); Assert.That(g.panels, Is.EqualTo(new[] { "layers", "properties" }));
            Assert.That(g.Active, Is.EqualTo("layers"));
            // まとまりごと別のまとまりへ落とすと全部がタブになる（左の列は空になって消える）
            layout.SetWeight(g, 2.5f); layout.JoinGroup(layout.GroupOf("color").id, tabs);
            Assert.That(layout.Column(DockPlace.Left), Is.Empty); Assert.That(g.panels, Is.EqualTo(new[] { "layers", "properties", "color" })); Assert.That(g.Active, Is.EqualTo("color"));
            var back = RoundTrip(layout);
            Assert.That(back.GroupOf("layers").panels, Is.EqualTo(g.panels)); Assert.That(back.GroupOf("layers").Active, Is.EqualTo("color")); Assert.That(back.Weight(back.GroupOf("layers")), Is.EqualTo(2.5f));
            Assert.That(() => layout.Join("nope", tabs), Throws.ArgumentException);
            Assert.That(() => layout.Join("color", "nope"), Throws.ArgumentException);
        }

        [Test] public void FloatingPanelsRememberWhereTheyCameFromAndDockBack()
        {
            var layout = Classic(); var at = new Rect(100, 200, 300, 400);
            var g = layout.FloatPanel("layers", at);
            Assert.That((g.place, g.home, g.homeIndex, g.window), Is.EqualTo((DockPlace.Floating, DockPlace.Right, 2, at)));
            Assert.That(In(layout, DockPlace.Right), Is.EqualTo(new[] { "color", "textureSet", "material", "assets", "properties", "textureSetSettings" }));
            Assert.That(layout.Column(DockPlace.Floating), Is.EqualTo(new[] { g }));
            var back = RoundTrip(layout);
            Assert.That(back.GroupOf("layers").Floating, Is.True); Assert.That(back.GroupOf("layers").window, Is.EqualTo(at)); Assert.That(back.GroupOf("layers").homeIndex, Is.EqualTo(2));
            Assert.That(layout.Dock(g.id), Is.True);
            Assert.That(In(layout, DockPlace.Right), Is.EqualTo(new[] { "color", "textureSet", "layers", "material", "assets", "properties", "textureSetSettings" }), "back where it was");
            Assert.That(layout.Dock(g.id), Is.False, "docking a docked group does nothing");

            // タブのまとまりごと出して、そこから 1 つだけ自分のウィンドウへ、もう 1 つを列へ
            layout.Join("properties", g.id);
            var both = layout.FloatGroup(g.id, at);
            Assert.That(both.panels, Is.EqualTo(new[] { "layers", "properties" })); Assert.That(In(layout, DockPlace.Right), Is.EqualTo(new[] { "color", "textureSet", "material", "assets", "textureSetSettings" }));
            var own = layout.FloatPanel("properties", new Rect(500, 200, 300, 400));
            Assert.That(own.Floating, Is.True); Assert.That(own, Is.Not.SameAs(both)); Assert.That(both.panels, Is.EqualTo(new[] { "layers" }));
            Assert.That((own.home, own.homeIndex), Is.EqualTo((both.home, both.homeIndex)), "a panel split from a separate window keeps that window's way home");
            layout.Join("properties", both.id); // 別のウィンドウのタブに戻す
            layout.MovePanel("properties", both.home, both.homeIndex); // 「このパネルを列に戻す」
            Assert.That(In(layout, DockPlace.Right), Is.EqualTo(new[] { "color", "textureSet", "properties", "material", "assets", "textureSetSettings" }));
            layout.MoveGroup(both.id, DockPlace.Left, 0); // 別のウィンドウを左の列へドラッグ
            Assert.That(both.Floating, Is.False); Assert.That(In(layout, DockPlace.Left), Is.EqualTo(new[] { "layers" }));

            // 左の列から出したものは左へ戻る。戻り先の列が短くなっていれば最後へ
            var left = layout.FloatPanel("layers", at);
            Assert.That(left.home, Is.EqualTo(DockPlace.Left));
            Assert.That(layout.Column(DockPlace.Left), Is.Empty, "an empty column disappears");
            layout.Dock(left.id); Assert.That(In(layout, DockPlace.Left), Is.EqualTo(new[] { "layers" }));
            var far = layout.FloatPanel("textureSet", at); far.homeIndex = 9;
            layout.Dock(far.id); Assert.That(In(layout, DockPlace.Right).Last(), Is.EqualTo("textureSet"));
        }

        /// <summary>列の一番下が畳んだまとまりのとき、その見出しの下の縁に落とすと最後に並び（タブには入らない）、見出しの中ほどならタブに入る。</summary>
        [Test] public void DroppingOnTheLowerEdgeOfAFoldedLastHeaderPutsThePanelLast()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.LayoutOverride = new Rect(0, 0, 1200, 800); LayoutShell(w);
                var layout = w.DockLayoutForTests;
                var material = layout.GroupOf("material");
                layout.MoveGroup(material.id, DockPlace.Right, layout.Column(DockPlace.Right).Count); layout.SetCollapsed(material.id, true);
                LayoutShell(w);
                int last = layout.Column(DockPlace.Right).Count - 1;
                Assert.That(layout.Column(DockPlace.Right)[last].id, Is.EqualTo(material.id));
                var head = w.HeaderRectForTests(DockPlace.Right, last);
                var edge = w.DropTargetForTests(new Vector2(head.center.x, head.yMax - 2));
                Assert.That(edge.HasValue, Is.True);
                Assert.That(edge.Value.join, Is.Null, "the lower edge of a folded header is not a tab drop");
                Assert.That((edge.Value.place, edge.Value.index), Is.EqualTo((DockPlace.Right, last + 1)), "it puts the panel after the folded group, at the end");
                var middle = w.DropTargetForTests(new Vector2(head.center.x, head.y + 10));
                Assert.That(middle.Value.join, Is.EqualTo(material.id), "the middle of the header still makes a tab");
            }
            finally { Object.DestroyImmediate(w); }
        }

        /// <summary>見出しの右クリックの項目と、ウィンドウ ▸ の項目が呼ぶ操作（バッチでは別のウィンドウは開かず、配置だけが変わる）。</summary>
        [Test] public void TheHeaderAndWindowMenusMovePanels()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.LayoutOverride = new Rect(0, 0, 1200, 800); w.DockLayoutForTests = Classic(); LayoutShell(w);
                var layout = w.DockLayoutForTests;
                string[] Texts(string panel) => w.PanelHeaderMenuItems(layout.GroupOf(panel), panel).Select(i => i.text).ToArray();
                void Run(string panel, string text) => w.PanelHeaderMenuItems(layout.GroupOf(panel), panel).Single(i => i.text == text).action();
                Assert.That(Texts("color"), Is.EqualTo(new[] { "Open in Separate Window", null, "Move to Left Column", "Fold" }));
                layout.Join("properties", layout.GroupOf("layers").id);
                Assert.That(Texts("properties"), Is.EqualTo(new[] { "Open This Panel in a Separate Window", "Open Tab Group in Separate Window", "Separate from Tab Group", null, "Move to Left Column", "Fold" }));
                Run("properties", "Separate from Tab Group");
                Assert.That(layout.PanelsIn(DockPlace.Right), Is.EqualTo(new[] { "color", "textureSet", "layers", "properties", "material", "assets", "textureSetSettings" })); Assert.That(layout.GroupOf("layers").panels.Count, Is.EqualTo(1));
                Run("layers", "Move to Left Column"); Assert.That(layout.PanelsIn(DockPlace.Left), Is.EqualTo(new[] { "layers" }));
                Assert.That(Texts("layers")[2], Is.EqualTo("Move to Right Column"));
                Run("layers", "Fold"); Assert.That(w.PanelShown("layers"), Is.False); Assert.That(Texts("layers").Last(), Is.EqualTo("Unfold"));
                w.ShowOrHidePanel("layers"); Assert.That(w.PanelShown("layers"), Is.True, "Window ▸ Layers unfolds it");
                w.ShowOrHidePanel("layers"); Assert.That(layout.GroupOf("layers").collapsed, Is.True, "and folds it again");
                // 別のウィンドウ（バッチでは開かないが、配置と位置は決まる）
                Run("color", "Open in Separate Window");
                var color = layout.GroupOf("color");
                Assert.That(color.Floating, Is.True); Assert.That(color.window.width, Is.GreaterThan(0)); Assert.That(w.PanelShown("color"), Is.True);
                Assert.That(Texts("color"), Is.EqualTo(new[] { "Use a Dockable Unity Window", "Return to the Dock" }));
                w.ToggleFloating("color"); Assert.That(layout.PanelsIn(DockPlace.Right).First(), Is.EqualTo("color"), "Window ▸ Open in Separate Window again puts it back");
                w.ToggleFloating("textureSet"); var tabs = layout.GroupOf("textureSet"); layout.Join("properties", tabs.id);
                Assert.That(Texts("properties"), Is.EqualTo(new[] { "Use a Dockable Unity Window", "Return to the Dock", "Return This Panel to the Dock", "Separate into Its Own Window" }));
                Run("properties", "Separate into Its Own Window");
                Assert.That(layout.Column(DockPlace.Floating).Count, Is.EqualTo(2));
                Assert.That(layout.GroupOf("properties").window.position, Is.EqualTo(tabs.window.position + new Vector2(32, 32)));
                w.ToggleFloating("properties"); Assert.That(layout.IsFloating("properties"), Is.False);
                w.DockAllPanels(); Assert.That(layout.Column(DockPlace.Floating), Is.Empty);
                Assert.That(layout.PanelsIn(DockPlace.Right), Is.EquivalentTo(new[] { "color", "textureSet", "properties", "material", "assets", "textureSetSettings" }));
                w.ResetDockLayout();
                Assert.That(w.DockLayoutForTests.PanelsIn(DockPlace.Left), Is.EqualTo(DockLayout.Default().PanelsIn(DockPlace.Left)), "Reset Panel Layout goes back to Substance's arrangement");
                Assert.That(w.DockLayoutForTests.PanelsIn(DockPlace.Right), Is.EqualTo(DockLayout.Default().PanelsIn(DockPlace.Right)));
                Assert.That(() => w.PanelShown("nope"), Throws.ArgumentException);
            }
            finally { Object.DestroyImmediate(w); }
        }

        [Test] public void APanelOnTheLeftOpensALeftDockAndNarrowsTheView()
        {
            Assert.That(DockLayoutStore.UseDefaults, Is.True, "tests use the default layout");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.LayoutOverride = new Rect(0, 0, 1600, 950); w.DockLayoutForTests = Classic(); // 左の列が無い配置から
                var layout = w.DockLayoutForTests;
                LayoutShell(w);
                Assert.That(layout.Column(DockPlace.Left), Is.Empty);
                layout.MovePanel("layers", DockPlace.Left, 0); layout.MovePanel("properties", DockPlace.Left, 1);
                LayoutShell(w);
                Assert.That(w.SurfaceRect.width + w.SurfaceRect.x, Is.LessThanOrEqualTo(1600 - 300 + .5f));
                if (CanDrawOffscreen)
                {
                    w.Preview.LoadDemoMesh();
                    string path = Path.Combine(Folder, "dock-left.png");
                    OffscreenGui.RenderWindow(w, 1600, 950, path);
                    Assert.That(File.Exists(path), Is.True);
                }
            }
            finally { Object.DestroyImmediate(w); }
        }

        /// <summary>タブの名前は、詰めずに全部を出すか、アイコンだけ（名前はツールチップ）。どの幅でも、英語でも日本語でも、見えているタブには名前が
        /// 入る限り名前を出し、全部が入る幅なら全部のタブに名前を出し、タブが見出しからはみ出さない。</summary>
        [Test] public void TabsShowTheirWholeNameOrOnlyTheIconAtEveryHeaderWidth()
        {
            Assume.That(PaintGui.TextWidth("Layers", PaintTheme.Header), Is.GreaterThan(10), "no font to measure with");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.DockLayoutForTests = DockLayout.Default();
                var layout = w.DockLayoutForTests;
                string tabs = layout.GroupOf("layers").id; layout.Join("properties", tabs); // レイヤー・テクスチャセットの設定・マテリアル・プロパティの 4 つ
                var panels = layout.Group(tabs).panels.ToArray();
                Assert.That(panels.Length, Is.EqualTo(4));
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    foreach (var shown in panels)
                    {
                        layout.SetActive(shown);
                        for (float width = 60; width <= 520; width += 3)
                        {
                            var (rects, names, available) = w.TabLayoutForTests(tabs, width);
                            string at = language + " " + shown + " " + width;
                            Assert.That(rects.Last().xMax - rects.First().x, Is.LessThanOrEqualTo(available + 1), at + ": the tabs fit the header");
                            var natural = panels.Select(p => 20 + PaintGui.TextWidth(TexturePaintWindow.PanelTitle(p), PaintTheme.Header) + 12).ToArray();
                            int active = System.Array.IndexOf(panels, shown);
                            for (int i = 0; i < rects.Length; i++) if (names[i]) Assert.That(rects[i].width, Is.GreaterThanOrEqualTo(44), at + ": a tab with its name is wide enough");
                            if (available - (panels.Length - 1) * 28 >= natural[active]) Assert.That(names[active], Is.True, at + ": the shown tab has its name when the others can be icons");
                            if (natural.Sum() <= available) Assert.That(names, Is.All.True, at + ": every name when all fit");
                        }
                    }
                }
            }
            finally { L.OverrideLanguage(PainterLanguage.English); Object.DestroyImmediate(w); }
        }

        static readonly string[] AllPanels = { "color", "textureSet", "layers", "properties", "material", "assets", "textureSetSettings" };

        /// <summary>1 つだけのまとまり（パネル 1 つの見出し）も、名前が入らない幅ではアイコンだけになり、名前はツールチップの先頭で読める。
        /// どの幅でも、英語でも日本語でも。</summary>
        [Test] public void ASinglePanelHeaderNamesItselfOrItsTooltipDoes()
        {
            Assume.That(PaintGui.TextWidth("Layers", PaintTheme.Header), Is.GreaterThan(10), "no font to measure with");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.DockLayoutForTests = Classic(); // どのパネルも 1 つだけのまとまり
                var layout = w.DockLayoutForTests; int iconOnly = 0;
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    foreach (var panel in AllPanels)
                    {
                        string group = layout.GroupOf(panel).id; string title = TexturePaintWindow.PanelTitle(panel);
                        Assert.That(layout.Group(group).panels, Has.Count.EqualTo(1));
                        for (float width = 30; width <= 320; width += 4)
                        {
                            var (rects, names, available) = w.TabLayoutForTests(group, width);
                            string tip = w.TabTooltipForTests(group, 0, width), at = language + " " + panel + " " + width;
                            if (!names[0]) { iconOnly++; Assert.That(tip, Does.StartWith(title + "\n"), at + ": an icon-only header names itself in its tooltip"); }
                            else Assert.That(rects[0].width, Is.GreaterThanOrEqualTo(44), at);
                        }
                    }
                }
                Assert.That(iconOnly, Is.GreaterThan(0), "the sweep includes widths where the name does not fit");
            }
            finally { L.OverrideLanguage(PainterLanguage.English); Object.DestroyImmediate(w); }
        }

        /// <summary>1 つだけのまとまりの見出しを狭い幅で描いても、名前を … で詰めない（名前が入らなければアイコンだけ）。描いた文字で確かめる（batch-gl）。
        /// 続きの「― 今の中身」は利用者の名前なので、… で詰めてよい。</summary>
        [Test] public void ASinglePanelHeaderDrawnNarrowNeverCutsItsName()
        {
            if (!CanDrawOffscreen) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon.");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.DockLayoutForTests = Classic(); var layout = w.DockLayoutForTests;
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    foreach (var panel in AllPanels)
                        foreach (int width in new[] { 40, 70, 100, 140, 200 })
                        {
                            var drawn = new List<string>(); PaintGui.TextDrawn = drawn.Add;
                            try { OffscreenGui.RenderToPng(width, 24, () => w.DrawGroupHeaderForTests(layout.GroupOf(panel).id, new Rect(0, 0, width, 24)), Path.Combine(Folder, "single-header-" + panel + "-" + language + "-" + width + ".png"), PaintTheme.PanelBg); }
                            finally { PaintGui.TextDrawn = null; }
                            string title = TexturePaintWindow.PanelTitle(panel), at = language + " " + panel + " " + width;
                            Assert.That(drawn.Where(t => !t.StartsWith(" ― ")), Has.All.EqualTo(title), at + ": the header draws its whole name or no text");
                            Assert.That(drawn.Where(t => t.Contains("…") && !t.StartsWith(" ― ")), Is.Empty, at);
                        }
                }
            }
            finally { PaintGui.TextDrawn = null; L.OverrideLanguage(PainterLanguage.English); Object.DestroyImmediate(w); }
        }

        /// <summary>タブのまとまりと、別のウィンドウに出した後の列、別のウィンドウの中身を、窓を開かずに描く（batch-gl。PNG は
        /// Logs/YoluPainterSnapshots に残して見て確かめる）。英語と日本語で。</summary>
        [Test] public void TabsAndSeparateWindowsDrawOffscreen()
        {
            if (!CanDrawOffscreen) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon.");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh();
                w.DockLayoutForTests = Classic();
                var layout = w.DockLayoutForTests;
                // 列: テクスチャセットと、レイヤー・プロパティのタブ。カラーは別のウィンドウ
                var tabs = layout.GroupOf("layers"); layout.Join("properties", tabs.id); layout.SetActive("layers");
                var color = layout.FloatPanel("color", new Rect(0, 0, 300, 260));
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    Render("dock-tabs-" + language, 1200, 800, () => OffscreenGui.RenderWindow(w, 1200, 800, Path.Combine(Folder, "dock-tabs-" + language + ".png")));
                    Render("dock-tabs-narrow-" + language, 980, 640, () => OffscreenGui.RenderWindow(w, 980, 640, Path.Combine(Folder, "dock-tabs-narrow-" + language + ".png")));
                    Render("panel-window-color-" + language, 300, 260, () => OffscreenGui.RenderToPng(300, 260, () => w.DrawFloatingGroupForTests(color.id, new Rect(0, 0, 300, 260)), Path.Combine(Folder, "panel-window-color-" + language + ".png"), PaintTheme.PanelBg));
                }
                // ドラッグの途中の落とす先の印: 見出しの上（タブにまとめる）、列の間（その位置へ）、表示域の上（別のウィンドウ）
                L.OverrideLanguage(PainterLanguage.English);
                w.LayoutOverride = new Rect(0, 0, 1200, 800); LayoutShell(w);
                var onHeader = w.PanelHeaderRectForTests("textureSet"); var below = w.PanelHeaderRectForTests("layers");
                foreach (var (name, at) in new[] { ("drag-join", new Vector2(onHeader.xMax - 30, onHeader.center.y + 2)), ("drag-insert", new Vector2(below.center.x, below.y + 40)), ("drag-float", new Vector2(500, 400)) })
                {
                    w.PretendPanelDragForTests("properties", true, at);
                    Render(name, 1200, 800, () => OffscreenGui.RenderWindow(w, 1200, 800, Path.Combine(Folder, name + ".png"), at));
                }
                w.EndPanelDragForTests();
                // タブのまとまりごと別のウィンドウへ: 列にはテクスチャセットだけ。別のウィンドウはプロパティを見せる
                layout.FloatGroup(tabs.id, new Rect(0, 0, 320, 480)); layout.SetActive("properties");
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    Render("dock-floated-tabs-" + language, 1200, 800, () => OffscreenGui.RenderWindow(w, 1200, 800, Path.Combine(Folder, "dock-floated-tabs-" + language + ".png")));
                    Render("panel-window-tabs-" + language, 320, 480, () => OffscreenGui.RenderToPng(320, 480, () => w.DrawFloatingGroupForTests(tabs.id, new Rect(0, 0, 320, 480)), Path.Combine(Folder, "panel-window-tabs-" + language + ".png"), PaintTheme.PanelBg));
                }
                // 列のパネルを全部出すと、右の列も消えて表示域が広がる
                layout.FloatPanel("textureSet", new Rect(0, 0, 300, 400)); layout.FloatPanel("material", new Rect(0, 0, 300, 400)); layout.FloatPanel("assets", new Rect(0, 0, 300, 400)); layout.FloatPanel("textureSetSettings", new Rect(0, 0, 300, 400));
                Assert.That(layout.Column(DockPlace.Right), Is.Empty);
                LayoutShell(w);
                Assert.That(w.SurfaceRect.xMax, Is.GreaterThan(1200 - 10));
            }
            finally { Object.DestroyImmediate(w); L.OverrideLanguage(PainterLanguage.English); }
        }

        static bool CanDrawOffscreen => Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots"));
        static void LayoutShell(TexturePaintWindow w) => typeof(TexturePaintWindow).GetMethod("LayoutShell", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(w, null);

        static void Render(string name, int width, int height, System.Action render)
        {
            string path = Path.Combine(Folder, name + ".png");
            render();
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, path);
                Assert.That((texture.width, texture.height), Is.EqualTo((width, height)), path);
                int colors = texture.GetPixels32().Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Take(40).Count();
                Assert.That(colors, Is.GreaterThan(20), path + ": looks empty");
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
