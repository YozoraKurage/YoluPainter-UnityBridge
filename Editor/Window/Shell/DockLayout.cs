using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>パネルのまとまりの置き場所: 左の列、右の列、または別のウィンドウ（<see cref="PainterPanelWindow"/>）。</summary>
    internal enum DockPlace { Right, Left, Floating }

    /// <summary>
    /// パネルのまとまり（タブでまとめたパネル。1 つだけならタブの無い見出し）。畳む・高さの比はまとまりの単位。別のウィンドウに出したものは
    /// そのウィンドウの位置と、閉じたときに戻る列と位置を持つ。
    /// </summary>
    [Serializable]
    internal sealed class DockGroup
    {
        /// <summary>まとまりを指す名前（別のウィンドウがどのまとまりを映すかを覚えるのに使う）。</summary>
        public string id;
        public List<string> panels = new List<string>();
        /// <summary>見えているタブ。</summary>
        public string active;
        public DockPlace place;
        public bool collapsed;
        public float weight = 1;
        /// <summary>別のウィンドウの位置（スクリーン座標。大きさが 0 なら持ち主の窓の脇に出す）。</summary>
        public Rect window;
        /// <summary>別のウィンドウを閉じたときに戻る列と、その列の何番目か。</summary>
        public DockPlace home; public int homeIndex;

        public string Active => panels.Contains(active) ? active : panels.FirstOrDefault();
        public bool Floating => place == DockPlace.Floating;
    }

    /// <summary>
    /// パネルの配置（Photoshop・CLIP STUDIO のドックと同じ考え方）: パネルのまとまり（タブ）の並び、それぞれの置き場所（左の列・右の列・
    /// 別のウィンドウ）、列の幅、畳んだまとまり、高さの決まっていないまとまりの高さの比。描き手ごとの好みとして EditorPrefs に JSON で覚える。
    /// 列の中の順は <see cref="groups"/> の中の順。
    /// </summary>
    [Serializable]
    internal sealed class DockLayout
    {
        public static readonly string[] KnownPanels = { "color", "textureSet", "layers", "properties" };
        public const float MinWidth = 220, MaxWidth = 560;
        /// <summary>保存の形式。0 は最初の形式（列ごとのパネルの ID の並びと、パネルごとの畳み・高さの比。<see cref="DockLayoutV0"/>）。</summary>
        public const int CurrentVersion = 1;
        public int version = CurrentVersion;
        public List<DockGroup> groups = new List<DockGroup>();
        public float leftWidth = 260, rightWidth = 300;

        /// <summary>既定の配置: 右の列にカラー・テクスチャセット・レイヤー・プロパティを 1 つずつ。</summary>
        public static DockLayout Default() => new DockLayout { groups = KnownPanels.Select(id => Single(id, DockPlace.Right)).ToList() };

        static DockGroup Single(string id, DockPlace place) => new DockGroup { id = NewId(), panels = new List<string> { id }, active = id, place = place, home = place == DockPlace.Left ? DockPlace.Left : DockPlace.Right };
        static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>EditorPrefs の JSON を読む。最初の形式（版の番号が無い）は読み替える。読めない JSON は ArgumentException。</summary>
        public static DockLayout FromJson(string json)
        {
            var probe = JsonUtility.FromJson<VersionProbe>(json);
            if (probe == null) return Default();
            if (probe.version <= 0) return FromV0(JsonUtility.FromJson<DockLayoutV0>(json));
            return (JsonUtility.FromJson<DockLayout>(json) ?? Default()).Normalized(); // 新しい版は、わかる所だけ読んで直す
        }
        [Serializable] sealed class VersionProbe { public int version; }

        /// <summary>最初の形式の読み替え: 列のパネルを 1 つずつのまとまりにし、パネルごとの畳み・高さの比をまとまりに移す。</summary>
        static DockLayout FromV0(DockLayoutV0 old)
        {
            var layout = new DockLayout { groups = new List<DockGroup>(), leftWidth = old.leftWidth, rightWidth = old.rightWidth };
            bool weightsFit = old.weightIds != null && old.weights != null && old.weightIds.Count == old.weights.Count;
            void Add(List<string> ids, DockPlace place)
            {
                foreach (var id in ids ?? new List<string>())
                {
                    var g = Single(id, place); g.collapsed = old.collapsed != null && old.collapsed.Contains(id);
                    int i = weightsFit ? old.weightIds.IndexOf(id) : -1; if (i >= 0) g.weight = old.weights[i];
                    layout.groups.Add(g);
                }
            }
            Add(old.left, DockPlace.Left); Add(old.right, DockPlace.Right);
            // 列に無かったパネルも、畳み・高さの比を持ったまま右の列の最後へ（前の版の直し方と同じ）
            var placed = new HashSet<string>(layout.groups.SelectMany(g => g.panels));
            Add(KnownPanels.Where(id => !placed.Contains(id)).ToList(), DockPlace.Right);
            return layout.Normalized();
        }

        /// <summary>壊れた・古い配置を直す: 知らないパネルと重複を除き、空のまとまりを消し、無いパネルは右の列の最後に足す。見えるタブ・
        /// 置き場所・戻り先・名前の重複・高さの比・ウィンドウの位置・幅を正しい範囲に収める。</summary>
        public DockLayout Normalized()
        {
            var seenPanels = new HashSet<string>(); var seenIds = new HashSet<string>();
            groups = (groups ?? new List<DockGroup>()).Where(g => g != null).ToList();
            foreach (var g in groups)
            {
                g.panels = (g.panels ?? new List<string>()).Where(id => KnownPanels.Contains(id) && seenPanels.Add(id)).ToList();
                g.active = g.Active;
                if (!Enum.IsDefined(typeof(DockPlace), g.place)) g.place = DockPlace.Right;
                if (g.home != DockPlace.Left) g.home = DockPlace.Right; // 戻り先は列だけ
                g.homeIndex = Mathf.Max(0, g.homeIndex);
                g.weight = float.IsNaN(g.weight) || float.IsInfinity(g.weight) || g.weight <= 0 ? 1 : Mathf.Clamp(g.weight, .05f, 20);
                if (!Finite(g.window) || g.window.width < 0 || g.window.height < 0) g.window = default;
                if (string.IsNullOrEmpty(g.id) || !seenIds.Add(g.id)) { g.id = NewId(); seenIds.Add(g.id); }
            }
            groups.RemoveAll(g => g.panels.Count == 0);
            foreach (var id in KnownPanels) if (!seenPanels.Contains(id)) groups.Add(Single(id, DockPlace.Right));
            leftWidth = Mathf.Clamp(float.IsNaN(leftWidth) ? 260 : leftWidth, MinWidth, MaxWidth);
            rightWidth = Mathf.Clamp(float.IsNaN(rightWidth) ? 300 : rightWidth, MinWidth, MaxWidth);
            version = CurrentVersion;
            return this;
        }
        static bool Finite(Rect r) => !(float.IsNaN(r.x) || float.IsNaN(r.y) || float.IsNaN(r.width) || float.IsNaN(r.height) || float.IsInfinity(r.x) || float.IsInfinity(r.y) || float.IsInfinity(r.width) || float.IsInfinity(r.height));

        // ───────── 読む ─────────

        /// <summary>列（左か右）のまとまりを上から順に。Floating なら別のウィンドウのまとまり。</summary>
        public List<DockGroup> Column(DockPlace place) => groups.Where(g => g.place == place).ToList();
        /// <summary>列のパネル（見えていないタブも）を上から順に。</summary>
        public List<string> PanelsIn(DockPlace place) => Column(place).SelectMany(g => g.panels).ToList();
        public DockGroup GroupOf(string panel) => groups.FirstOrDefault(g => g.panels.Contains(panel)) ?? throw new ArgumentException("Unknown panel " + panel, nameof(panel));
        public DockGroup Group(string id) => groups.FirstOrDefault(g => g.id == id) ?? throw new ArgumentException("Unknown panel group " + id, nameof(id));
        public bool HasGroup(string id) => groups.Any(g => g.id == id);
        public bool IsFloating(string panel) => GroupOf(panel).Floating;

        // ───────── 畳む・高さの比・見えるタブ ─────────

        public float Weight(DockGroup g) => Mathf.Max(.05f, g.weight);
        public void SetWeight(DockGroup g, float weight) => g.weight = Mathf.Clamp(weight, .05f, 20);
        public void SetCollapsed(string groupId, bool value) => Group(groupId).collapsed = value;
        /// <summary>そのタブを見えるようにする（畳んでいたまとまりは開く）。</summary>
        public void SetActive(string panel) { var g = GroupOf(panel); g.active = panel; g.collapsed = false; }

        // ───────── 動かす ─────────

        /// <summary>まとまりごと列（左か右）の index 番目（列の中のまとまりの数え方）へ動かす（同じ列の中の移動も、別のウィンドウからの戻しも）。</summary>
        public void MoveGroup(string groupId, DockPlace column, int index)
        {
            if (column == DockPlace.Floating) throw new ArgumentException("Use Float to put a group in a separate window.", nameof(column));
            var g = Group(groupId);
            var before = Column(column); int old = before.IndexOf(g);
            groups.Remove(g);
            if (old >= 0 && old < index) index--; // 同じ列で下へ動かすときは、抜いた分だけ詰める
            g.place = column; g.home = column;
            Insert(g, column, index);
        }

        /// <summary>パネルだけを列の index 番目へ動かす。タブでまとめていたら抜き出して、1 つだけのまとまりにする（タブを分ける）。</summary>
        public void MovePanel(string panel, DockPlace column, int index)
        {
            var from = GroupOf(panel);
            if (from.panels.Count == 1) { MoveGroup(from.id, column, index); return; }
            if (column == DockPlace.Floating) throw new ArgumentException("Use Float to put a panel in a separate window.", nameof(column));
            Detach(from, panel);
            Insert(Single(panel, column), column, index);
        }

        /// <summary>パネルを別のまとまりのタブにする（tab 番目へ。負なら最後）。同じまとまりの中なら並べ替え。見えるタブはそのパネルになる。</summary>
        public void Join(string panel, string targetGroupId, int tab = -1)
        {
            var target = Group(targetGroupId); var from = GroupOf(panel);
            if (from == target)
            {
                int old = target.panels.IndexOf(panel); target.panels.Remove(panel);
                if (tab < 0 || tab > target.panels.Count + 1) tab = target.panels.Count; else if (old < tab) tab--;
                target.panels.Insert(Mathf.Clamp(tab, 0, target.panels.Count), panel);
            }
            else
            {
                Detach(from, panel);
                target.panels.Insert(tab < 0 ? target.panels.Count : Mathf.Clamp(tab, 0, target.panels.Count), panel);
            }
            target.active = panel;
        }

        /// <summary>まとまりのパネルを全部、別のまとまりのタブにする（見えていたタブが見えるタブになる）。</summary>
        public void JoinGroup(string groupId, string targetGroupId, int tab = -1)
        {
            var g = Group(groupId); var target = Group(targetGroupId);
            if (g == target) return;
            string shown = g.Active;
            groups.Remove(g);
            int at = tab < 0 ? target.panels.Count : Mathf.Clamp(tab, 0, target.panels.Count);
            target.panels.InsertRange(at, g.panels);
            target.active = shown;
        }

        /// <summary>まとまりを別のウィンドウに出す。戻り先は今の列と位置（別のウィンドウのままなら前の戻り先）。</summary>
        public DockGroup FloatGroup(string groupId, Rect window)
        {
            var g = Group(groupId);
            if (!g.Floating) { g.home = g.place; g.homeIndex = Column(g.place).IndexOf(g); }
            g.place = DockPlace.Floating; g.window = window; g.collapsed = false;
            return g;
        }

        /// <summary>パネルだけを別のウィンドウに出す（タブでまとめていたら抜き出す）。戻り先はもとのまとまりのすぐ下。</summary>
        public DockGroup FloatPanel(string panel, Rect window)
        {
            var from = GroupOf(panel);
            if (from.panels.Count == 1) return FloatGroup(from.id, window);
            var g = Single(panel, DockPlace.Floating);
            if (from.Floating) { g.home = from.home; g.homeIndex = from.homeIndex; }
            else { g.home = from.place; g.homeIndex = Column(from.place).IndexOf(from) + 1; }
            Detach(from, panel);
            g.window = window;
            groups.Add(g);
            return g;
        }

        /// <summary>別のウィンドウのまとまりを戻り先の列へ戻す（列が短くなっていれば最後へ）。別のウィンドウでなければ何もしない（false）。</summary>
        public bool Dock(string groupId)
        {
            var g = Group(groupId);
            if (!g.Floating) return false;
            groups.Remove(g);
            g.place = g.home == DockPlace.Left ? DockPlace.Left : DockPlace.Right;
            Insert(g, g.place, g.homeIndex);
            return true;
        }

        void Detach(DockGroup from, string panel)
        {
            from.panels.Remove(panel);
            if (from.panels.Count == 0) groups.Remove(from);
            else if (from.active == panel) from.active = from.panels[0];
        }

        /// <summary>列の index 番目（列の中のまとまりの数え方）の前に入れる。index が列の長さ以上なら列の最後のまとまりの後ろ。</summary>
        void Insert(DockGroup g, DockPlace column, int index)
        {
            var col = Column(column);
            index = Mathf.Clamp(index, 0, col.Count);
            int at = index < col.Count ? groups.IndexOf(col[index]) : col.Count > 0 ? groups.IndexOf(col[col.Count - 1]) + 1 : groups.Count;
            groups.Insert(at, g);
        }
    }

    /// <summary>最初の形式（版の番号の無い JSON）。読み替えにだけ使う。</summary>
    [Serializable]
    internal sealed class DockLayoutV0
    {
        public List<string> left, right, collapsed, weightIds;
        public List<float> weights;
        public float leftWidth = 260, rightWidth = 300;
    }

    /// <summary>配置の置き場（EditorPrefs）。テストは既定の配置に固定する（描き手の配置で窓の大きさが変わらないように）。</summary>
    internal static class DockLayoutStore
    {
        const string Key = "Yozolab.YoluPainter.DockLayout";
        /// <summary>true のあいだは保存も読み込みもせず既定の配置を使う（テスト）。</summary>
        public static bool UseDefaults;
        public static DockLayout Load()
        {
            if (UseDefaults) return DockLayout.Default();
            string json = EditorPrefs.GetString(Key, "");
            if (string.IsNullOrEmpty(json)) return DockLayout.Default();
            try { return DockLayout.FromJson(json); }
            catch (ArgumentException) { return DockLayout.Default(); }
        }
        public static void Save(DockLayout layout) { if (!UseDefaults) EditorPrefs.SetString(Key, JsonUtility.ToJson(layout)); }
        public static void Reset() { if (!UseDefaults) EditorPrefs.DeleteKey(Key); }
    }
}
