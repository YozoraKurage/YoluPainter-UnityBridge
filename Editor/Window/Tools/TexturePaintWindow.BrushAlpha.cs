using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 描くツールの「アルファ」のタブ（Substance Painter のブラシの ALPHA）: 今の先端の見本と名前、先端の形の設定（硬さ・真円率）、
    /// 先端の一覧（円形・内蔵・同梱の Krita の先端・取り込んだブラシの先端。押すとその先端に替える）と、ブラシの取り込み。
    /// 先端はブラシのプリセットと一緒に来るのは今までどおりで、ここではプリセットを替えずに先端だけを選び直せる（大きさ・流量などはそのまま）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>先端の一覧の 1 つの格子の大きさ（と間）。</summary>
        internal const float AlphaCell = 40, AlphaCellGap = 4;

        /// <summary>先端の一覧のまとまり（題と、ID・名前の並び）。取り込んだブラシや同梱の表示の設定が変わったときだけ作り直す。</summary>
        sealed class TipGroup { public string Title; public List<(string id, string name)> Tips = new List<(string, string)>(); }
        List<TipGroup> tipGroups; string tipGroupsKey;

        void AlphaSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-alpha", L.Tr("Alpha"), "shapes")) return;
            bool round = string.IsNullOrEmpty(brush.tipId), missing = !round && BrushTips.ResolveRef(brush.tipId) == null;
            // 今の先端: 見本・名前・出どころ
            var head = rows.Row(48, 6);
            DrawTipCell(new Rect(head.x, head.y, 48, 48), brush.tipId, false, false);
            var groups = TipGroups();
            string name = round ? L.Tr("Round (hardness)") : missing ? L.Tr("Missing") + ": " + brush.tipId
                : groups.SelectMany(g => g.Tips).Where(t => t.id == brush.tipId).Select(t => t.name).FirstOrDefault() ?? brush.tipId;
            string from = round || missing ? null : groups.FirstOrDefault(g => g.Tips.Any(t => t.id == brush.tipId))?.Title;
            float tx = head.x + 56, tw = head.width - 56;
            PaintGui.Text(new Rect(tx, head.y + 6, tw, 18), PaintGui.Fit(name, tw, PaintTheme.LabelBold, false), PaintTheme.LabelBold, missing ? PaintTheme.Warning : PaintTheme.Text);
            if (from != null) PaintGui.Text(new Rect(tx, head.y + 26, tw, 16), PaintGui.Fit(from, tw, PaintTheme.LabelSmall, false), PaintTheme.LabelSmall);
            PaintGui.Tooltip(head, name + (from != null ? "\n" + from : ""));
            // 形の設定
            brush.hardness = PercentSlider(Mark("alpha.hardness", rows.SliderRow()), L.Tr("Hardness"), brush.hardness, 0, 1, L.Tr("Hardness of the round tip (an image tip keeps its own edge)"), round);
            brush.roundness = PercentSlider(Mark("alpha.roundness", rows.SliderRow()), L.Tr("Roundness"), brush.roundness, .01f, 1, L.Tr("Squashes the tip along its angle (100% keeps its shape)"));
            if (missing) NoteRow(rows, L.Tr("This tip is not in this Unity project; a round tip is used instead."), NoteKind.Warning);
            // 一覧
            foreach (var group in groups)
            {
                if (group.Tips.Count == 0) continue;
                PaintGui.GroupLabel(rows.Row(16), group.Title);
                int columns = Mathf.Max(1, Mathf.FloorToInt((rows.Width + AlphaCellGap) / (AlphaCell + AlphaCellGap)));
                for (int start = 0; start < group.Tips.Count; start += columns)
                {
                    var line = rows.Row(AlphaCell, AlphaCellGap);
                    for (int k = 0; k < columns && start + k < group.Tips.Count; k++)
                    {
                        var (id, tipName) = group.Tips[start + k];
                        var cell = Mark("alpha.tip." + id, new Rect(line.x + k * (AlphaCell + AlphaCellGap), line.y, AlphaCell, AlphaCell));
                        bool hover = GUI.enabled && cell.Contains(Event.current.mousePosition);
                        DrawTipCell(cell, id, id == (brush.tipId ?? ""), hover);
                        PaintGui.Tooltip(cell, tipName);
                        if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover) { SetTip(id); Event.current.Use(); }
                    }
                }
            }
            rows.Space(2);
            if (PaintGui.Button(Mark("alpha.import", rows.Row(24)), L.Tr("Import Brushes…"), false, GUI.enabled, L.Tr("Photoshop (.abr), GIMP (.gbr / .gih / .vbr) or a PNG; their tips are added to this list"), "import")) ImportBrushes();
            rows.Space(4);
        }

        /// <summary>先端を替える（プリセットの他の値はそのまま。円形は空の ID）。</summary>
        internal void SetTip(string id)
        {
            brush.tipId = id ?? "";
            message = string.IsNullOrEmpty(brush.tipId) ? L.Tr("The brush uses the round tip.") : L.Tr("The brush uses the tip {0}.", brush.tipId);
            Repaint();
        }

        /// <summary>先端の一覧（円形と内蔵 → 同梱（設定で見せるとき）→ 取り込んだブラシの置き場ごと）。同じ ID は最初の 1 つだけ。</summary>
        internal List<(string group, string id, string name)> TipChoices() => TipGroups().SelectMany(g => g.Tips.Select(t => (g.Title, t.id, t.name))).ToList();

        List<TipGroup> TipGroups()
        {
            string key = L.LocaleCode + "|" + PainterSettings.ShowBundledBrushes + "|" + string.Join(",", BrushLibrary.All.Select(l => l.Presets.Count));
            if (tipGroups != null && key == tipGroupsKey) return tipGroups;
            var seen = new HashSet<string>(); var groups = new List<TipGroup>();
            var builtIn = new TipGroup { Title = L.TrIn("brush", "Built-in") };
            builtIn.Tips.Add(("", L.Tr("Round (hardness)"))); seen.Add("");
            foreach (var id in BuiltInBrushes.TipIds) if (seen.Add("builtin:" + id)) builtIn.Tips.Add(("builtin:" + id, id));
            groups.Add(builtIn);
            void FromPresets(string title, IEnumerable<BrushPreset> presets)
            {
                var g = new TipGroup { Title = title };
                foreach (var p in presets)
                {
                    string id = BrushTips.IdOf(p.CreateSettings());
                    if (!string.IsNullOrEmpty(id) && seen.Add(id)) g.Tips.Add((id, p.Name));
                }
                groups.Add(g);
            }
            if (PainterSettings.ShowBundledBrushes) FromPresets(L.Tr("Bundled (Krita)"), BundledBrushSets.Presets);
            foreach (var library in BrushLibrary.All) FromPresets(library.MenuName, library.Presets);
            tipGroups = groups; tipGroupsKey = key;
            return groups;
        }

        static Texture2D roundTipThumbnail;
        /// <summary>先端の見本（白地に黒。円形は手で作る）。選んでいれば青い枠、マウスが乗れば薄い枠。</summary>
        static void DrawTipCell(Rect r, string id, bool selected, bool hover)
        {
            if (Event.current.type == EventType.Repaint)
            {
                var texture = string.IsNullOrEmpty(id) ? RoundTipThumbnail() : BrushTips.Thumbnail(id);
                PaintGui.Rounded(r, Color.white, 3);
                if (texture != null) GUI.DrawTexture(new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4), texture, ScaleMode.ScaleToFit, true, 0, Color.white, 0, 2);
                else PaintGui.Icon(r, "warning", PaintTheme.Warning, 16); // 見つからない先端
            }
            if (selected) PaintGui.Outline(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), PaintTheme.Accent, 2, 4);
            else if (hover) PaintGui.Outline(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), PaintTheme.AccentDim, 1, 4);
        }

        static Texture2D RoundTipThumbnail()
        {
            if (roundTipThumbnail != null) return roundTipThumbnail;
            const int size = 48;
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(size / 2f, size / 2f)) / (size / 2f);
                byte c = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01((d - .55f) / .4f)); // やわらかい縁の円
                pixels[y * size + x] = new Color32(c, c, c, 255);
            }
            t.SetPixels32(pixels); t.Apply(false, false);
            return roundTipThumbnail = t;
        }
    }
}
