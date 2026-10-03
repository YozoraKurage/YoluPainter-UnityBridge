using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Yozolab.YoluPainter.Core;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>非破壊のフィルター: 選んだ層の「画素へのフィルター」（チャンネルごと）と「マスクへのフィルター」を分けて並べ、追加（メニュー）・
    /// 削除・並べ替え・有効/無効・強さ・パラメーターの編集をする。どれも文書の Undo に 1 回ずつ入る（スライダーのドラッグは 1 回にまとめる）。
    /// 描いた画素は変えず、合成がフィルターを通した結果を表示する。「焼き込み」だけが画素を書き換える（1 回の Undo）。
    /// Generator（メッシュマップから値を作る段）も同じスタックに入る（見出しの 2 つ目のボタンで足す。設定は TexturePaintWindow.Generators.cs）。</summary>
    public sealed partial class TexturePaintWindow
    {
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
                message = target == FilterTarget.Mask ? L.Tr("Added {0} to the mask (non-destructive: the painted pixels are unchanged).", FilterName(settings))
                    : L.Tr("Added {0} to the {1} pixels (non-destructive: the painted pixels are unchanged).", FilterName(settings), L.Tr(channel.ToString()));
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
                bool keepAlpha = (document.EffectiveLocks(selectedLayer) & LayerLocks.Transparency) != 0 && document.GetLayer(selectedLayer).Filters.Count > 0;
                if (document.BakeFilters(selectedLayer)) message = L.Tr("Baked the filters into the layer's pixels (one undo step); the filter stack is now empty.")
                    + (keepAlpha ? " " + L.Tr("The transparent pixels are locked, so only the colours were baked: each pixel kept its transparency.") : "");
                else message = L.Tr("The layer has no filters to bake.");
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

        /// <summary>フィルターの表示名（追加のメニューと同じ名前を訳したもの）。</summary>
        static string FilterName(FilterSettings f)
        {
            switch (f.Type)
            {
                case FilterType.Generator: return GeneratorName(f.Generator.Type);
                case FilterType.GaussianBlur: return L.Tr("Gaussian blur");
                case FilterType.Sharpen: return L.Tr("Sharpen");
                case FilterType.Noise: return L.Tr(f.Monochrome ? "Noise (monochrome)" : "Noise (colour)");
                case FilterType.Levels: return L.Tr("Levels");
                case FilterType.Invert: return L.Tr("Invert");
                default: return L.Tr("Normalize (whole layer)");
            }
        }
        /// <summary>一覧の 1 行の文字: 名前と主な値、効かないチャンネルなら効くチャンネル、無効・強さ。</summary>
        string FilterLabel(FilterEffect e, FilterTarget target)
        {
            var f = e.Settings; var c = CultureInfo.InvariantCulture;
            string text = FilterName(f);
            switch (f.Type)
            {
                case FilterType.GaussianBlur: text += "  " + f.Radius + " px"; break;
                case FilterType.Sharpen: text += "  " + f.Radius + " px ×" + f.Amount.ToString("0.##", c); break;
                case FilterType.Noise: text += "  " + (f.Amount * 100).ToString("0", c) + "%"; break;
                case FilterType.Generator: text += GeneratorSummary(e); break;
            }
            if (target == FilterTarget.Content && !e.AppliesTo(channel)) text += "  [" + string.Join(", ", e.Channels.Select(ch => L.Tr(ch.ToString()))) + "]";
            if (e.Strength < 1) text += "  · " + Math.Round(e.Strength * 100).ToString(c) + "%";
            return text;
        }

        string FiltersTitle(PaintLayer active)
        {
            int count = active.Filters.Count + (active.Mask != null ? active.Mask.Filters.Count : 0);
            return L.Tr("Filters") + (count > 0 ? " (" + count + ")" : "");
        }

        void DrawFilters(UiRows rows, PaintLayer active)
        {
            DrawFilterStack(rows, active, FilterTarget.Content);
            if (active.Mask != null) DrawFilterStack(rows, active, FilterTarget.Mask);
            if (active.Filters.Count > 0 || active.Mask != null && active.Mask.Filters.Count > 0)
            {
                rows.Space(2);
                if (PaintGui.Button(rows.Row(24), L.Tr("Bake Filters into Pixels"), false, GUI.enabled, L.Tr("Destructive: writes the filtered result into the layer (and mask) and empties the stacks. One undo step."), "local_fire_department"))
                    BakeFilters();
            }
        }

        void DrawFilterStack(UiRows rows, PaintLayer active, FilterTarget target)
        {
            var stack = target == FilterTarget.Content ? active.Filters : active.Mask.Filters;
            var head = rows.Row(20);
            PaintGui.GroupLabel(new Rect(head.x, head.y, head.width - 52, head.height),
                target == FilterTarget.Content ? L.Tr("Layer pixels") + " · " + L.Tr(channel.ToString()) : L.Tr("Mask (all channels)"));
            var generate = new Rect(head.xMax - 48, head.y - 1, 24, head.height + 2);
            if (PaintGui.IconButton(Spot("generator.add." + target, generate), "texture", target == FilterTarget.Content ? L.Tr("Add a generator on the pixels (values from the baked mesh maps)") : L.Tr("Add a generator on the mask (where the layer shows, from the baked mesh maps)"), false, GUI.enabled, 16))
                ShowGeneratorMenu(generate, active.Id, target);
            var add = new Rect(head.xMax - 24, head.y - 1, 24, head.height + 2);
            if (PaintGui.IconButton(Spot("filter.add." + target, add), "add", target == FilterTarget.Content ? L.Tr("Add a filter on the pixels") : L.Tr("Add a filter on the mask"), false, GUI.enabled, 16))
                ShowFilterMenu(add, active.Id, target);
            if (stack.Count == 0) { PaintGui.Text(Indent(rows.Row(18), 4), L.Tr("No filters"), PaintTheme.LabelDim, PaintTheme.TextDisabled); return; }
            var items = stack.ToList(); // 押した操作で並びが変わっても、この描画は前の並びのまま終える
            for (int i = items.Count - 1; i >= 0; i--) // 上が後（あとから掛かる）
            {
                var e = items[i];
                DrawFilterRow(rows.Row(24, 2), e, i, items.Count, target);
                if (selectedFilter == e.Id) DrawFilterParameters(rows, e, target);
            }
            rows.Space(2);
        }

        void DrawFilterRow(Rect r, FilterEffect e, int index, int count, FilterTarget target)
        {
            var ev = Event.current;
            bool selected = selectedFilter == e.Id, hover = GUI.enabled && r.Contains(ev.mousePosition);
            if (selected) { PaintGui.Rounded(r, PaintTheme.AccentSoft, 3); PaintGui.Fill(new Rect(r.x, r.y + 2, 2, r.height - 4), PaintTheme.Accent); }
            else if (hover) PaintGui.Rounded(r, PaintTheme.ControlHover, 3);
            if (PaintGui.IconButton(Spot("filter." + e.Id + ".eye", new Rect(r.x + 2, r.y + 1, 22, r.height - 2)), e.Enabled ? "visibility" : "visibility_off", e.Enabled ? L.Tr("Turn the filter off") : L.Tr("Turn the filter on"), false, GUI.enabled, 15))
                ToggleFilter(e.Id, !e.Enabled);
            var name = Spot("filter." + e.Id + ".name", new Rect(r.x + 26, r.y, r.width - 26 - 3 * 22 - 2, r.height));
            PaintGui.Icon(new Rect(name.x, name.y, 14, name.height), selected ? "expand_more" : "chevron_right", PaintTheme.TextDim, 13);
            string label = FilterLabel(e, target), shown = PaintGui.Fit(label, name.width - 18, PaintTheme.Label, false); // 値を含む要約なので、詰めても数えない
            PaintGui.Text(new Rect(name.x + 16, name.y, name.width - 16, name.height), shown, PaintTheme.Label, !e.Enabled ? PaintTheme.TextDim : selected ? Color.white : PaintTheme.Text);
            PaintGui.Tooltip(name, (shown != label ? label + "\n" : "") + L.Tr("Click to show or hide the settings"));
            if (ev.type == EventType.MouseDown && ev.button == 0 && GUI.enabled && name.Contains(ev.mousePosition)) { selectedFilter = selected ? Guid.Empty : e.Id; ev.Use(); Repaint(); }
            float x = r.xMax - 3 * 22;
            if (PaintGui.IconButton(Spot("filter." + e.Id + ".up", new Rect(x, r.y + 1, 22, r.height - 2)), "expand_less", L.Tr("Move up (applied later)"), false, GUI.enabled && index < count - 1, 15)) MoveFilter(e.Id, index + 1);
            if (PaintGui.IconButton(Spot("filter." + e.Id + ".down", new Rect(x + 22, r.y + 1, 22, r.height - 2)), "expand_more", L.Tr("Move down (applied earlier)"), false, GUI.enabled && index > 0, 15)) MoveFilter(e.Id, index - 1);
            if (PaintGui.IconButton(Spot("filter." + e.Id + ".remove", new Rect(x + 44, r.y + 1, 22, r.height - 2)), "close", L.Tr("Remove the filter"), false, GUI.enabled, 14)) RemoveFilter(e.Id);
        }

        /// <summary>フィルターを足すメニューの項目: 訳した名前、足す設定、断られるならその理由（足せるなら null）。チャンネルの型・層の種類・
        /// マスクの有無で断られるものも、黙って隠さずに理由と並べる。</summary>
        internal List<(string label, FilterSettings settings, string refusal)> FilterChoices(Guid layerId, FilterTarget target)
        {
            var choices = new List<(string, FilterSettings, string)>();
            foreach (var (label, make) in FilterMenu)
            {
                var settings = make();
                string why;
                try { why = document.FilterRefusal(layerId, target, settings, channel); }
                catch (KeyNotFoundException) { why = L.Tr("no layer"); }
                choices.Add((L.Tr(label), settings, why));
            }
            return choices;
        }

        /// <summary>フィルターを足すメニュー。断られるもの（チャンネルの型・マスク）は理由を添えて押せない項目にする。</summary>
        void ShowFilterMenu(Rect at, Guid layerId, FilterTarget target)
        {
            var menu = new GenericMenu();
            foreach (var (label, settings, why) in FilterChoices(layerId, target))
            {
                if (why == null) menu.AddItem(new GUIContent(label), false, () => { AddFilter(target, settings); Repaint(); });
                else menu.AddDisabledItem(new GUIContent(label + " — " + why));
            }
            menu.DropDown(at);
        }

        void DrawFilterParameters(UiRows rows, FilterEffect e, FilterTarget target)
        {
            const float indent = 26;
            float top = rows.Row(0, 0).y;
            var f = e.Settings; FilterSettings next = f;
            try
            {
                switch (f.Type)
                {
                    case FilterType.GaussianBlur:
                        next = f.WithRadius(PaintGui.KeepIntSlider(Spot("filter.radius", Indent(rows.Row(), indent)), L.TrIn("filter", "Radius"), f.Radius, 1, FilterSettings.MaxBlurRadius, " px", L.Tr("Reach in pixels (standard deviation ≈ radius / 3)")));
                        break;
                    case FilterType.Sharpen:
                    {
                        var c = UiRows.Split(Indent(rows.Row(), indent), 2, 6);
                        int radius = PaintGui.KeepIntSlider(c[0], L.TrIn("filter", "Radius"), f.Radius, 1, FilterSettings.MaxSharpenRadius, " px");
                        double amount = PaintGui.KeepSlider(c[1], L.TrIn("filter", "Amount"), f.Amount, 0, FilterSettings.MaxSharpenAmount, "0.##", "", L.Tr("How much the edges are strengthened"));
                        int threshold = PaintGui.KeepIntSlider(Indent(rows.Row(), indent), L.TrIn("filter", "Threshold"), f.Threshold, 0, 255, "", L.Tr("Differences smaller than this (0–255) are left alone"));
                        next = f.WithRadius(radius).WithAmount(amount).WithThreshold(threshold);
                        break;
                    }
                    case FilterType.Noise:
                    {
                        var c = UiRows.Split(Indent(rows.Row(), indent), 2, 6);
                        double amount = PaintGui.KeepSlider(c[0], L.TrIn("filter", "Amount"), f.Amount, 0, 1, "0", "%", null, true, 100);
                        int seed = PaintGui.KeepIntField(c[1], L.TrIn("filter", "Seed"), f.Seed, int.MinValue, int.MaxValue, L.Tr("The same seed gives the same noise. Drag to change, click to type."));
                        next = f.WithAmount(amount).WithSeed(seed);
                        break;
                    }
                    case FilterType.Levels:
                    {
                        double ib = f.InputBlack, iw = f.InputWhite, gamma = f.Gamma, ob = f.OutputBlack, ow = f.OutputWhite;
                        if (LevelsRows(rows, ref ib, ref iw, ref gamma, ref ob, ref ow, indent)) next = f.WithLevels(ib, iw, gamma, ob, ow);
                        break;
                    }
                    case FilterType.Generator:
                        DrawGeneratorParameters(rows, e, indent); // 変えた値はその中で文書に入れる
                        break;
                    default:
                        NoteRow(rows, f.Type == FilterType.Normalize ? L.Tr("Uses the whole layer (global).") : L.Tr("No settings."), NoteKind.Plain, indent);
                        break;
                }
            }
            catch (ArgumentException ex) { message = ex.Message; next = f; }
            if (!next.Equals(f)) ApplyFilterSettings(e.Id, next, coalesce: true);
            double strength = PaintGui.KeepSlider(Spot("filter.strength", Indent(rows.Row(), indent)), L.TrIn("filter", "Strength"), e.Strength, 0, 1, "0", "%", L.Tr("How much of the filtered result is mixed in"), true, 100);
            if (strength != e.Strength) ApplyFilterStrength(e.Id, strength, coalesce: true);
            if (target == FilterTarget.Content && !e.AppliesTo(channel)) NoteRow(rows, L.Tr("Not applied to the {0} channel.", L.Tr(channel.ToString())), NoteKind.Info, indent);
            float bottom = rows.Row(0, 0).y - 4;
            var guide = rows.Row(0, 0);
            PaintGui.VLine(guide.x + 12, top, bottom, PaintTheme.Separator);
            rows.Space(4);
        }
    }
}
