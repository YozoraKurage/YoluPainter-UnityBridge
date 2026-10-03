using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Yozolab.YoluPainter.Core;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>非破壊のフィルター: 選んだ層の「画素へのフィルター」（チャンネルごと）と「マスクへのフィルター」。一覧はレイヤーの重なりの
    /// 子の行（Panels/TexturePaintWindow.EffectRows.cs）で、追加（メニュー）・削除・並べ替え・有効/無効・選ぶ。選んだ段の強さ・パラメーターは
    /// プロパティの欄（<see cref="DrawFilterParameters"/>）。どれも文書の Undo に 1 回ずつ入る（スライダーのドラッグは 1 回にまとめる）。
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

        /// <summary>選んだ効果の段の設定（プロパティの欄の「フィルター: …」「Generator: …」。段はレイヤーの重なりの子の行で選ぶ）。</summary>
        void DrawFilterParameters(UiRows rows, FilterEffect e, FilterTarget target)
        {
            const float indent = 0; // 欄の字下げは見出しの段が決める（前は欄の中の一覧の行の下に字下げして描いた）
            var f = e.Settings; FilterSettings next = f;
            try
            {
                switch (f.Type)
                {
                    case FilterType.GaussianBlur:
                        next = f.WithRadius(PaintGui.KeepIntSlider(Spot("filter.radius", Indent(rows.SliderRow(), indent)), L.TrIn("filter", "Radius"), f.Radius, 1, FilterSettings.MaxBlurRadius, " px", L.Tr("Reach in pixels (standard deviation ≈ radius / 3)")));
                        break;
                    case FilterType.Sharpen:
                    {
                        int radius = PaintGui.KeepIntSlider(Indent(rows.SliderRow(), indent), L.TrIn("filter", "Radius"), f.Radius, 1, FilterSettings.MaxSharpenRadius, " px");
                        double amount = PaintGui.KeepSlider(Indent(rows.SliderRow(), indent), L.TrIn("filter", "Amount"), f.Amount, 0, FilterSettings.MaxSharpenAmount, "0.##", "", L.Tr("How much the edges are strengthened"));
                        int threshold = PaintGui.KeepIntSlider(Indent(rows.SliderRow(), indent), L.TrIn("filter", "Threshold"), f.Threshold, 0, 255, "", L.Tr("Differences smaller than this (0–255) are left alone"));
                        next = f.WithRadius(radius).WithAmount(amount).WithThreshold(threshold);
                        break;
                    }
                    case FilterType.Noise:
                    {
                        double amount = PaintGui.KeepSlider(Indent(rows.SliderRow(), indent), L.TrIn("filter", "Amount"), f.Amount, 0, 1, "0", "%", null, true, 100);
                        int seed = PaintGui.KeepIntField(Indent(rows.Row(), indent), L.TrIn("filter", "Seed"), f.Seed, int.MinValue, int.MaxValue, L.Tr("The same seed gives the same noise. Drag to change, click to type."));
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
            double strength = PaintGui.KeepSlider(Spot("filter.strength", Indent(rows.SliderRow(), indent)), L.TrIn("filter", "Strength"), e.Strength, 0, 1, "0", "%", L.Tr("How much of the filtered result is mixed in"), true, 100);
            if (strength != e.Strength) ApplyFilterStrength(e.Id, strength, coalesce: true);
            if (target == FilterTarget.Content && !e.AppliesTo(channel)) NoteRow(rows, L.Tr("Not applied to the {0} channel.", L.Tr(channel.ToString())), NoteKind.Info, indent);
            rows.Space(4);
        }
    }
}
