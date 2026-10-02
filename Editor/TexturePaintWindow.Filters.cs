using System;
using System.Linq;
using Yozolab.YoluPainter.Core;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>非破壊のフィルター: 選んだ層の「画素へのフィルター」（チャンネルごと）と「マスクへのフィルター」を分けて並べ、追加（メニュー）・
    /// 削除・並べ替え・有効/無効・強さ・パラメーターの編集をする。どれも文書の Undo に 1 回ずつ入る（スライダーのドラッグは 1 回にまとめる）。
    /// 描いた画素は変えず、合成がフィルターを通した結果を表示する。「焼き込み」だけが画素を書き換える（1 回の Undo）。</summary>
    public sealed partial class TexturePaintWindow
    {
        bool showFilters = true;
        Guid selectedFilter;

        internal Guid SelectedFilter { get => selectedFilter; set => selectedFilter = value; }

        /// <summary>選んだ層のスタックにフィルターを足す（画素なら今のチャンネルだけに効く）。断られたら理由を状態の欄に出して null。</summary>
        internal FilterEffect AddFilter(FilterTarget target, FilterSettings settings)
        {
            FilterEffect added = null;
            TryAction(() =>
            {
                added = document.AddFilter(selectedLayer, target, settings, target == FilterTarget.Content ? new[] { channel } : null);
                selectedFilter = added.Id; repaintPixels = true;
                message = "Added " + settings.Name + (target == FilterTarget.Mask ? " to the mask" : " to the " + channel + " pixels") + " (non-destructive: the painted pixels are unchanged).";
            });
            return added;
        }
        internal void ApplyFilterSettings(Guid id, FilterSettings settings, bool coalesce = false) { TryAction(() => { document.SetFilterSettings(selectedLayer, id, settings, coalesce); repaintPixels = true; }); }
        internal void ApplyFilterStrength(Guid id, double strength, bool coalesce = false) { TryAction(() => { document.SetFilterStrength(selectedLayer, id, strength, coalesce); repaintPixels = true; }); }
        internal void ToggleFilter(Guid id, bool enabled) { TryAction(() => { document.SetFilterEnabled(selectedLayer, id, enabled); repaintPixels = true; }); }
        internal void MoveFilter(Guid id, int index) { TryAction(() => { document.MoveFilter(selectedLayer, id, index); repaintPixels = true; }); }
        internal void RemoveFilter(Guid id) { TryAction(() => { document.RemoveFilter(selectedLayer, id); if (selectedFilter == id) selectedFilter = Guid.Empty; repaintPixels = true; }); }
        internal void BakeFilters()
        {
            TryAction(() =>
            {
                if (document.BakeFilters(selectedLayer)) message = "Baked the filters into the layer's pixels (one undo step); the filter stack is now empty.";
                else message = "The layer has no filters to bake.";
                repaintPixels = true;
            });
        }

        static readonly (string label, Func<FilterSettings> make)[] FilterMenu =
        {
            ("Gaussian blur", () => FilterSettings.GaussianBlur(4)),
            ("Sharpen", () => FilterSettings.Sharpen(2, 1, 0)),
            ("Noise (monochrome)", () => FilterSettings.Noise(.25, 0, true)),
            ("Noise (colour)", () => FilterSettings.Noise(.25, 0, false)),
            ("Levels", () => FilterSettings.Levels()),
            ("Invert", FilterSettings.Invert),
            ("Normalize (whole layer)", FilterSettings.Normalize),
        };

        void DrawFilters(PaintLayer active)
        {
            GUILayout.Space(6);
            showFilters = EditorGUILayout.Foldout(showFilters, "Filters (non-destructive): " + active.Filters.Count + (active.Mask != null ? " / mask " + active.Mask.Filters.Count : ""), true);
            if (!showFilters) return;
            DrawFilterStack(active, FilterTarget.Content);
            if (active.Mask != null) DrawFilterStack(active, FilterTarget.Mask);
            if (active.Filters.Count > 0 || active.Mask != null && active.Mask.Filters.Count > 0)
                if (GUILayout.Button(new GUIContent("Bake filters into pixels", "Destructive: writes the filtered result into the layer (and mask) and empties the stacks. One undo step."))) BakeFilters();
        }

        void DrawFilterStack(PaintLayer active, FilterTarget target)
        {
            var stack = target == FilterTarget.Content ? active.Filters : active.Mask.Filters;
            GUILayout.Label(target == FilterTarget.Content ? "Layer pixels (" + channel + ")" : "Mask (hide amount, all channels)", EditorStyles.miniBoldLabel);
            for (int i = stack.Count - 1; i >= 0; i--) // 上が後（あとから掛かる）
            {
                var e = stack[i];
                GUILayout.BeginHorizontal();
                bool on = GUILayout.Toggle(e.Enabled, "", GUILayout.Width(16)); if (on != e.Enabled) ToggleFilter(e.Id, on);
                bool here = target == FilterTarget.Mask || e.AppliesTo(channel);
                string label = e.Settings.ToString() + (here ? "" : " [" + string.Join(",", e.Channels) + "]");
                if (GUILayout.Toggle(selectedFilter == e.Id, label, "Button")) selectedFilter = e.Id;
                using (new EditorGUI.DisabledScope(i >= stack.Count - 1)) if (GUILayout.Button("▲", GUILayout.Width(20))) MoveFilter(e.Id, i + 1);
                using (new EditorGUI.DisabledScope(i <= 0)) if (GUILayout.Button("▼", GUILayout.Width(20))) MoveFilter(e.Id, i - 1);
                if (GUILayout.Button("✕", GUILayout.Width(20))) { RemoveFilter(e.Id); GUIUtility.ExitGUI(); }
                GUILayout.EndHorizontal();
                if (selectedFilter == e.Id) DrawFilterParameters(e, target);
            }
            if (GUILayout.Button(target == FilterTarget.Content ? "+ Filter on pixels" : "+ Filter on mask"))
            {
                var menu = new GenericMenu(); var layerId = active.Id;
                foreach (var (label, make) in FilterMenu)
                {
                    var settings = make();
                    string why = document.FilterRefusal(layerId, target, settings, channel);
                    if (why == null) menu.AddItem(new GUIContent(label), false, () => AddFilter(target, settings));
                    else menu.AddDisabledItem(new GUIContent(label + " — " + why));
                }
                menu.ShowAsContext();
            }
        }

        void DrawFilterParameters(FilterEffect e, FilterTarget target)
        {
            var f = e.Settings; FilterSettings next = f;
            EditorGUI.indentLevel++;
            try
            {
                switch (f.Type)
                {
                    case FilterType.GaussianBlur:
                        next = f.WithRadius(EditorGUILayout.IntSlider(new GUIContent("Radius", "Reach in pixels (standard deviation ≈ radius / 3)"), f.Radius, 1, FilterSettings.MaxBlurRadius)); break;
                    case FilterType.Sharpen:
                        next = f.WithRadius(EditorGUILayout.IntSlider("Radius", f.Radius, 1, FilterSettings.MaxSharpenRadius))
                            .WithAmount(Slider("Amount", f.Amount, 0, FilterSettings.MaxSharpenAmount))
                            .WithThreshold(EditorGUILayout.IntSlider("Threshold", f.Threshold, 0, 255)); break;
                    case FilterType.Noise:
                        next = f.WithAmount(Slider("Amount", f.Amount, 0, 1)).WithSeed(EditorGUILayout.IntField("Seed", f.Seed)); break;
                    case FilterType.Levels:
                    {
                        double ib = Slider("Input black", f.InputBlack, 0, f.InputWhite - .004), iw = Slider("Input white", f.InputWhite, ib + .004, 1);
                        next = f.WithLevels(ib, iw, Slider("Gamma", f.Gamma, .1, 9.99), Slider("Output black", f.OutputBlack, 0, 1), Slider("Output white", f.OutputWhite, 0, 1)); break;
                    }
                    default: EditorGUILayout.LabelField(f.Type == FilterType.Normalize ? "Uses the whole layer (global)" : "No parameters", EditorStyles.miniLabel); break;
                }
            }
            catch (ArgumentException ex) { message = ex.Message; next = f; }
            if (!next.Equals(f)) ApplyFilterSettings(e.Id, next, coalesce: true);
            double strength = Slider("Strength", e.Strength, 0, 1);
            if (strength != e.Strength) ApplyFilterStrength(e.Id, strength, coalesce: true);
            if (target == FilterTarget.Content && !e.AppliesTo(channel)) EditorGUILayout.HelpBox("Not applied to the " + channel + " channel.", MessageType.None);
            EditorGUI.indentLevel--;
        }
        /// <summary>float のスライダー。触っていなければ文書の値（double）のまま返す（読み込んだ値を勝手に丸めて Undo を作らない）。</summary>
        static double Slider(string label, double value, double min, double max)
        {
            float shown = (float)value, picked = EditorGUILayout.Slider(label, shown, (float)min, (float)max);
            return picked != shown ? picked : value;
        }
    }
}
