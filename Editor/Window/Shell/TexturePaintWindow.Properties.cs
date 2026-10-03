using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// プロパティの欄の枠組み（Substance Painter と同じく、選んでいる 1 つの物の中身だけを出す）: 今の文脈（<see cref="PropertyContextNow"/>）の
    /// 欄を振り分けの表（<see cref="PropertyRoutes"/>。Shell/TexturePaintWindow.PropertyRoutes.cs）から取り、タブが 2 つ以上あれば頭にタブの帯、
    /// その下に大見出し（全幅の帯）と小見出し（一段下の細い線）で縦に積み、はみ出したらスクロールする。欄の中身を描く関数は自分の見出しを
    /// <see cref="ToolSection"/> / <see cref="Section(UiRows, string, string, string)"/> で描き、その見た目と字下げは今描いている表の行の段で決まる。
    /// 開閉とタブは描き手ごとに覚える（<see cref="PropertySectionStore"/>）。
    /// まだ自前の部品に移していない中身は <see cref="LegacySection"/> で包む（Unity の標準の部品のまま、測った高さの区画に描く）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        Vector2 propertiesScroll; float propertiesContentHeight = 200;
        readonly Dictionary<string, float> legacyHeights = new Dictionary<string, float>();
        readonly Dictionary<string, bool> sectionOpen = new Dictionary<string, bool>();
        /// <summary>文脈ごとに見ているタブ（<see cref="PropertyRoute.Tab"/>）。</summary>
        readonly Dictionary<PropertyContext, string> propertyTabs = new Dictionary<PropertyContext, string>();
        /// <summary>今描いている表の行（その中の見出しの段と字下げを決める）。表の外で描くとき（ポップアップ・テクスチャセットの設定）は null。</summary>
        PropertyRoute drawingRoute;
        /// <summary>表の行の側で見出しを描いたか（中身の関数が同じキーで呼ぶ見出しは描き直さない）。</summary>
        bool drawingRouteHeaderDrawn;
        bool sectionMemoryLoaded;
        const float PropertyTabStripHeight = 30;

        /// <summary>試験用: 最後に描いたプロパティの欄の文脈・タブ・大見出しと小見出しのキー（描いた順）。</summary>
        internal PropertyContext LastPropertyContext { get; private set; }
        internal string LastPropertyTab { get; private set; }
        internal readonly List<string> LastPropertySections = new List<string>();

        void DrawPropertiesPanel(Rect r)
        {
            var marks = BeginMarks("properties");
            try { DrawPropertiesPanelInner(r); }
            finally { EndMarks(marks); }
        }

        void DrawPropertiesPanelInner(Rect r)
        {
            LoadSectionMemory();
            BeginSpots("properties");
            PaintGui.Fill(r, PaintTheme.PanelBg);
            var (context, routes, tabs, tab) = PropertyRoutesNow();
            if (tabs.Count > 1)
            {
                tab = DrawPropertyTabs(new Rect(r.x, r.y, r.width, PropertyTabStripHeight), context, tabs, tab);
                r = new Rect(r.x, r.y + PropertyTabStripHeight, r.width, Mathf.Max(0, r.height - PropertyTabStripHeight));
            }
            var view = new Rect(0, 0, PaintGui.ScrollContentWidth(r, propertiesContentHeight), Mathf.Max(propertiesContentHeight, r.height));
            propertiesScroll.y = Mathf.Clamp(propertiesScroll.y, 0, Mathf.Max(0, propertiesContentHeight - r.height));
            if (PaintGui.Scrollbar(r, ref propertiesScroll, propertiesContentHeight, 14)) Repaint();
            PaintGui.BeginScroll(r, propertiesScroll);
            var rows = new UiRows(new Rect(0, 0, view.width, 1e6f), 0);
            DrawPropertyBody(rows, context, routes, tabs, tab);
            // 中身の高さは Layout でも測る（描き直しの前の Layout で、はみ出すか＝スクロールの印の幅が決まり、最初の描画から同じ幅で描ける）
            var measuring = Event.current.type;
            if ((measuring == EventType.Repaint || measuring == EventType.Layout) && Mathf.Abs(rows.Used - propertiesContentHeight) > .5f) { propertiesContentHeight = rows.Used; if (measuring == EventType.Repaint) Repaint(); }
            PaintGui.EndScroll();
        }

        /// <summary>テスト用: プロパティの欄を、スクロールせずに area の幅で上から描く（タブの帯も）。使った高さ。</summary>
        internal float DrawPropertiesOnly(Rect area)
        {
            var marks = BeginMarks("properties");
            try { return DrawPropertiesOnlyInner(area); }
            finally { EndMarks(marks); }
        }
        float DrawPropertiesOnlyInner(Rect area)
        {
            LoadSectionMemory();
            BeginSpots("properties");
            PaintGui.Fill(area, PaintTheme.PanelBg);
            var (context, routes, tabs, tab) = PropertyRoutesNow();
            float top = 0;
            if (tabs.Count > 1) { tab = DrawPropertyTabs(new Rect(area.x, area.y, area.width, PropertyTabStripHeight), context, tabs, tab); top = PropertyTabStripHeight; }
            var rows = new UiRows(new Rect(area.x, area.y + top, area.width, 1e6f), 0);
            DrawPropertyBody(rows, context, routes, tabs, tab);
            return top + rows.Used;
        }

        /// <summary>テスト用: 今の文脈のタブ（タブが 1 つ以下なら空）。</summary>
        internal List<string> PropertyTabsNow() { var tabs = PropertyRoutesNow().tabs; return tabs.Count > 1 ? tabs : new List<string>(); }

        (PropertyContext context, List<PropertyRoute> routes, List<string> tabs, string tab) PropertyRoutesNow()
        {
            var context = PropertyContextNow();
            var routes = RoutesFor(context);
            var tabs = PropertyTabsOf(routes);
            return (context, routes, tabs, ActivePropertyTab(context, tabs));
        }

        /// <summary>タブの帯。押されたら覚えて、見えるタブを返す。</summary>
        string DrawPropertyTabs(Rect strip, PropertyContext context, List<string> tabs, string tab)
        {
            int index = tabs.IndexOf(tab);
            int chosen = PaintGui.TabStrip(TabSpot(strip, tabs), tabs.Select(PropertyTabTitle).ToArray(), tabs.Select(PropertyTabIcon).ToArray(), index);
            if (chosen == index) return tab;
            SetPropertyTab(context, tabs[chosen]); propertiesScroll = Vector2.zero; Repaint();
            return tabs[chosen];
        }

        /// <summary>見えるタブの欄を描く（無ければ何を選べば出るかの説明）。</summary>
        void DrawPropertyBody(UiRows rows, PropertyContext context, List<PropertyRoute> routes, List<string> tabs, string tab)
        {
            if (Event.current.type == EventType.Repaint) { LastPropertyContext = context; LastPropertyTab = tab; LastPropertySections.Clear(); }
            int drawn = DrawPropertyRoutes(rows, routes.Where(x => tabs.Count <= 1 || x.Tab == tab));
            if (drawn == 0) NothingToShow(rows, context);
            rows.Indent = 0;
            rows.Space(8);
        }

        /// <summary>表の行を順に描く。小見出しは親の大見出しが閉じていれば描かない。描いた行の数。</summary>
        int DrawPropertyRoutes(UiRows rows, IEnumerable<PropertyRoute> routes)
        {
            int drawn = 0;
            foreach (var route in routes)
            {
                if (route.Parent != null && !SectionIsOpen(route.Parent, !SectionClosedAtFirst(route.Parent))) continue;
                DrawPropertyRoute(rows, route);
                drawn++;
            }
            rows.Indent = 0;
            return drawn;
        }

        /// <summary>表の 1 行を描く: 表に題があれば見出しを描いてから中身を、無ければ中身の関数が自分の見出しを描く。</summary>
        void DrawPropertyRoute(UiRows rows, PropertyRoute route)
        {
            var previous = drawingRoute; bool previousDrawn = drawingRouteHeaderDrawn;
            drawingRoute = route; drawingRouteHeaderDrawn = false;
            try
            {
                if (route.Title != null)
                {
                    if (!Section(rows, route.Key, route.Title(), route.IconOf?.Invoke() ?? route.Icon)) return;
                    drawingRouteHeaderDrawn = true;
                }
                route.Draw(rows);
                rows.Space(route.Parent == null ? 4 : 2);
            }
            finally { drawingRoute = previous; drawingRouteHeaderDrawn = previousDrawn; rows.Indent = 0; }
        }

        /// <summary>折りたためるセクションの見出し（開いていれば true）。key ごとに開閉を覚える（既定は開。表で「初めは閉じる」とした欄は閉）。
        /// 見た目と字下げは今描いている表の行の段: 大見出しは全幅の帯、小見出しは一段下の細い線。表の外（ポップアップ・テクスチャセットの設定）は
        /// 大見出し。表の側で同じキーの見出しを描いていれば、描かずに true（中身の関数の見出しを表の題で置き換える）。</summary>
        bool Section(UiRows rows, string key, string title, string icon = null)
        {
            var route = drawingRoute != null && drawingRoute.Key == key ? drawingRoute : null;
            if (route != null && drawingRouteHeaderDrawn) return true;
            if (route != null && route.ClosedAtFirst && !sectionOpen.ContainsKey(key)) sectionOpen[key] = false;
            bool open = !sectionOpen.TryGetValue(key, out var stored) || stored;
            bool sub = route != null && route.Parent != null;
            bool next; Rect header;
            rows.Indent = 0;
            float indent = SectionIndentFor(rows.Width);
            if (sub)
            {
                header = rows.IndentedRow(PaintTheme.Padding + indent, 20, 3);
                next = PaintGui.SubsectionHeader(header, title, open, route.Reset, route.Reset != null ? L.Tr("Back to the default values of {0}", title) : null);
                rows.Indent = indent + Mathf.Round(indent * PaintTheme.SubsectionIndent / PaintTheme.SectionIndent);
            }
            else
            {
                header = rows.FullRow(PanelHeaderHeight, 5);
                next = PaintGui.SectionHeader(header, title, open, icon, route?.Reset, route?.Reset != null ? L.Tr("Back to the default values of {0}", title) : null);
                rows.Indent = indent;
            }
            if (Event.current.type == EventType.Repaint) { SectionHeaderRects[key] = (header, sub); Mark("section." + key, header); if (drawingRoute != null) LastPropertySections.Add(key); }
            if (next != open) { sectionOpen[key] = next; SaveSectionMemory(); }
            else sectionOpen[key] = open;
            return next;
        }

        /// <summary>大見出しの中身の字下げ（帯の ▸/▾ の右、文字の列に合わせる）。狭い欄（最小のドックの幅）では、ラベルを詰めないよう浅くする
        /// （幅 211 で 4 px、291 以上で <see cref="PaintTheme.SectionIndent"/>）。</summary>
        internal static float SectionIndentFor(float rowsWidth)
        {
            float t = Mathf.InverseLerp(211 - 2 * PaintTheme.Padding, 291 - 2 * PaintTheme.Padding, rowsWidth);
            return Mathf.Round(Mathf.Lerp(4, PaintTheme.SectionIndent, t));
        }
        /// <summary>試験用: key の欄が今開いているか（覚えていなければ初めの開閉）。</summary>
        internal bool SectionOpenNow(string key) => SectionIsOpen(key, !SectionClosedAtFirst(key));
        /// <summary>試験用: 最後に描いた見出しの矩形（欄の中の座標）と、小見出しか。</summary>
        internal readonly Dictionary<string, (Rect rect, bool sub)> SectionHeaderRects = new Dictionary<string, (Rect, bool)>();

        /// <summary>まだ Unity の標準の部品（GUILayout / EditorGUILayout）で描くセクション。前に描いたときに測った高さの区画に描く。
        /// オフスクリーンの描画（バッチモード）には標準のスタイルが無いので、そこでは描かない。</summary>
        void LegacySection(UiRows rows, string key, string title, Action draw)
        {
            if (!Section(rows, key, title)) return;
            if (!EditorStylesReady) { PaintGui.Text(rows.Row(20), "(" + L.Tr("not converted yet; drawn with Unity's controls") + ")", PaintTheme.LabelSmall); return; }
            float height = legacyHeights.TryGetValue(key, out var h) ? h : 120;
            var area = rows.Row(height, 8);
            GUILayout.BeginArea(area);
            try
            {
                draw();
                GUILayout.Space(1);
                if (Event.current.type == EventType.Repaint)
                {
                    float measured = Mathf.Max(20, GUILayoutUtility.GetLastRect().yMax + 2);
                    if (Mathf.Abs(measured - height) > .5f) { legacyHeights[key] = measured; Repaint(); }
                }
            }
            finally { GUILayout.EndArea(); }
        }

        static bool EditorStylesReady { get { try { return EditorStyles.boldLabel != null; } catch (NullReferenceException) { return false; } } }

        /// <summary>テスト用: 今の文脈のプロパティの欄（見ているタブか tab）を area に描く（ドックと同じ部品と幅。タブの帯は描かない）。使った高さ。</summary>
        internal float DrawPropertySectionsOnly(Rect area, string tab = null)
        {
            LoadSectionMemory();
            BeginSpots("properties");
            PaintGui.Fill(area, PaintTheme.PanelBg);
            var context = PropertyContextNow(); var routes = RoutesFor(context); var tabs = PropertyTabsOf(routes);
            string shown = tab ?? ActivePropertyTab(context, tabs);
            var rows = new UiRows(area, 0);
            if (DrawPropertyRoutes(rows, routes.Where(x => tabs.Count <= 1 || x.Tab == shown)) == 0) NothingToShow(rows, context);
            rows.Indent = 0;
            return rows.Used;
        }

        // ───────── タブ ─────────

        static List<string> PropertyTabsOf(IEnumerable<PropertyRoute> routes) => routes.Where(r => r.Tab != null).Select(r => r.Tab).Distinct().ToList();

        /// <summary>文脈で見ているタブ（覚えたタブが今は無ければ最初のタブ）。</summary>
        string ActivePropertyTab(PropertyContext context, List<string> tabs)
        {
            if (tabs.Count == 0) return null;
            return propertyTabs.TryGetValue(context, out var t) && tabs.Contains(t) ? t : tabs[0];
        }
        internal void SetPropertyTab(PropertyContext context, string tab) { propertyTabs[context] = tab; SaveSectionMemory(); }

        /// <summary>試験用: タブの帯の矩形（"propertyTab." + タブ）を覚える。</summary>
        Rect TabSpot(Rect strip, List<string> tabs)
        {
            if (Event.current.type == EventType.Repaint)
            {
                float w = (strip.width - 4) / tabs.Count;
                for (int i = 0; i < tabs.Count; i++) ToolControlScreenRects["propertyTab." + tabs[i]] = GUIUtility.GUIToScreenRect(new Rect(Mathf.Round(strip.x + 2 + i * w), strip.y + 2, Mathf.Round(w) - 1, strip.height - 3)); // 欄のスクロールの外
            }
            return strip;
        }

        /// <summary>出す欄が無いとき（スポイトなど）: 何を選ぶと出るかを書く。</summary>
        void NothingToShow(UiRows rows, PropertyContext context)
        {
            rows.Space(6);
            rows.Indent = 0;
            string text = context == PropertyContext.Eyedropper || context == PropertyContext.PolygonFill
                ? L.Tr("This tool's settings are in the options bar above the views.")
                : L.Tr("Nothing to set here. Select a layer, a fill, a filter or a generator in the Layers panel, or a painting tool.");
            NoteRow(rows, text, NoteKind.Info);
        }

        // ───────── 開閉の記憶 ─────────

        void LoadSectionMemory()
        {
            if (sectionMemoryLoaded) return;
            sectionMemoryLoaded = true;
            var saved = PropertySectionStore.Load();
            foreach (var kv in saved.Open) if (!sectionOpen.ContainsKey(kv.Key)) sectionOpen[kv.Key] = kv.Value;
            foreach (var kv in saved.Tabs) if (!propertyTabs.ContainsKey(kv.Key)) propertyTabs[kv.Key] = kv.Value;
        }
        void SaveSectionMemory() => PropertySectionStore.Save(sectionOpen, propertyTabs);
    }

    /// <summary>
    /// プロパティの欄（とテクスチャセットの設定）の見出しの開閉と、文脈ごとのタブの置き場（EditorPrefs に JSON。描き手の好みなので
    /// プロジェクトには入れない）。前の版は開閉を窓の中でだけ覚えていた（保存しない）ので、読み替える保存は無い。キーは前の版と同じで、
    /// 無くなった欄のキーだけ <see cref="Renamed"/> で今の欄に読み替える。テストは保存も読み込みもしない（<see cref="DockLayoutStore.UseDefaults"/>）。
    /// </summary>
    internal static class PropertySectionStore
    {
        internal const string Key = "Yozolab.YoluPainter.PropertySections";
        /// <summary>保存の形式。1 が最初（開いた欄・閉じた欄のキーの並びと、文脈ごとのタブ）。</summary>
        public const int CurrentVersion = 1;
        /// <summary>前の版のキー → 今のキー（欄を作り直したもの）。フィルターの一覧の欄は、選んだフィルター・Generator の欄になった。</summary>
        internal static readonly Dictionary<string, string> Renamed = new Dictionary<string, string> { { "filters", "effect" } };

        [Serializable] sealed class Saved { public int version; public List<string> open = new List<string>(), closed = new List<string>(), tabContexts = new List<string>(), tabs = new List<string>(); }

        internal sealed class Memory
        {
            public readonly Dictionary<string, bool> Open = new Dictionary<string, bool>();
            public readonly Dictionary<PropertyContext, string> Tabs = new Dictionary<PropertyContext, string>();
        }

        public static Memory Load()
        {
            if (DockLayoutStore.UseDefaults) return new Memory();
            try { return FromJson(EditorPrefs.GetString(Key, "")); }
            catch (ArgumentException) { return new Memory(); }
        }

        public static void Save(Dictionary<string, bool> open, Dictionary<PropertyContext, string> tabs)
        {
            if (DockLayoutStore.UseDefaults) return;
            EditorPrefs.SetString(Key, ToJson(open, tabs));
        }

        /// <summary>保存の JSON を読む（空なら何も覚えていない）。知らない版はわかる所だけ読む。読めない JSON は ArgumentException。</summary>
        internal static Memory FromJson(string json)
        {
            var memory = new Memory();
            if (string.IsNullOrEmpty(json)) return memory;
            Saved saved;
            try { saved = JsonUtility.FromJson<Saved>(json); }
            catch (Exception ex) when (!(ex is ArgumentException)) { throw new ArgumentException("Unreadable property section memory", nameof(json), ex); }
            if (saved == null) return memory;
            string Now(string key) => key != null && Renamed.TryGetValue(key, out var renamed) ? renamed : key;
            foreach (var key in saved.closed ?? new List<string>()) if (!string.IsNullOrEmpty(key)) memory.Open[Now(key)] = false;
            foreach (var key in saved.open ?? new List<string>()) if (!string.IsNullOrEmpty(key)) memory.Open[Now(key)] = true;
            var contexts = saved.tabContexts ?? new List<string>(); var tabs = saved.tabs ?? new List<string>();
            for (int i = 0; i < Math.Min(contexts.Count, tabs.Count); i++)
                if (Enum.TryParse(contexts[i], out PropertyContext c) && Enum.IsDefined(typeof(PropertyContext), c) && !string.IsNullOrEmpty(tabs[i])) memory.Tabs[c] = tabs[i];
            return memory;
        }

        internal static string ToJson(Dictionary<string, bool> open, Dictionary<PropertyContext, string> tabs)
        {
            var saved = new Saved { version = CurrentVersion };
            foreach (var kv in open.OrderBy(k => k.Key, StringComparer.Ordinal)) (kv.Value ? saved.open : saved.closed).Add(kv.Key);
            foreach (var kv in tabs.OrderBy(k => (int)k.Key)) { saved.tabContexts.Add(kv.Key.ToString()); saved.tabs.Add(kv.Value); }
            return JsonUtility.ToJson(saved);
        }
    }
}
