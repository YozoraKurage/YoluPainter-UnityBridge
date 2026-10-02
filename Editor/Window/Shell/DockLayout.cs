using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// パネルの配置（Photoshop・CLIP STUDIO のドックと同じ考え方）: 左と右の列に、どのパネルをどの順で置くか、列の幅、畳んだパネル、
    /// 高さの決まっていないパネル（レイヤー・プロパティ）の高さの比。描き手ごとの好みとして EditorPrefs に JSON で覚える。
    /// </summary>
    [Serializable]
    internal sealed class DockLayout
    {
        public static readonly string[] KnownPanels = { "color", "textureSet", "layers", "properties" };
        public const float MinWidth = 220, MaxWidth = 560;
        public List<string> left = new List<string>();
        public List<string> right = new List<string>(KnownPanels);
        public float leftWidth = 260, rightWidth = 300;
        public List<string> collapsed = new List<string>();
        public List<string> weightIds = new List<string>();
        public List<float> weights = new List<float>();

        public static DockLayout Default() => new DockLayout();

        /// <summary>壊れた・古い配置を直す: 知らないパネルを除き、無いパネルは右の列の最後に足し、重複を消し、幅を範囲に収める。</summary>
        public DockLayout Normalized()
        {
            var seen = new HashSet<string>();
            left = (left ?? new List<string>()).Where(id => KnownPanels.Contains(id) && seen.Add(id)).ToList();
            right = (right ?? new List<string>()).Where(id => KnownPanels.Contains(id) && seen.Add(id)).ToList();
            foreach (var id in KnownPanels) if (!seen.Contains(id)) right.Add(id);
            collapsed = (collapsed ?? new List<string>()).Where(id => KnownPanels.Contains(id)).Distinct().ToList();
            weightIds = weightIds ?? new List<string>(); weights = weights ?? new List<float>();
            if (weightIds.Count != weights.Count) { weightIds.Clear(); weights.Clear(); }
            leftWidth = Mathf.Clamp(float.IsNaN(leftWidth) ? 260 : leftWidth, MinWidth, MaxWidth);
            rightWidth = Mathf.Clamp(float.IsNaN(rightWidth) ? 300 : rightWidth, MinWidth, MaxWidth);
            return this;
        }

        public float Weight(string id) { int i = weightIds.IndexOf(id); return i < 0 ? 1 : Mathf.Max(.05f, weights[i]); }
        public void SetWeight(string id, float weight)
        {
            weight = Mathf.Clamp(weight, .05f, 20); int i = weightIds.IndexOf(id);
            if (i < 0) { weightIds.Add(id); weights.Add(weight); } else weights[i] = weight;
        }
        public bool IsCollapsed(string id) => collapsed.Contains(id);
        public void SetCollapsed(string id, bool value) { collapsed.Remove(id); if (value) collapsed.Add(id); }

        /// <summary>パネルを列（左か右）の index 番目へ動かす（同じ列の中の移動も）。</summary>
        public void Move(string id, bool toLeft, int index)
        {
            if (!KnownPanels.Contains(id)) throw new ArgumentException("Unknown panel " + id, nameof(id));
            var target = toLeft ? left : right;
            int old = target.IndexOf(id);
            left.Remove(id); right.Remove(id);
            if (old >= 0 && old < index) index--; // 同じ列で下へ動かすときは、抜いた分だけ詰める
            target.Insert(Mathf.Clamp(index, 0, target.Count), id);
        }
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
            try { return (JsonUtility.FromJson<DockLayout>(json) ?? DockLayout.Default()).Normalized(); }
            catch (ArgumentException) { return DockLayout.Default(); }
        }
        public static void Save(DockLayout layout) { if (!UseDefaults) EditorPrefs.SetString(Key, JsonUtility.ToJson(layout)); }
        public static void Reset() { if (!UseDefaults) EditorPrefs.DeleteKey(Key); }
    }
}
