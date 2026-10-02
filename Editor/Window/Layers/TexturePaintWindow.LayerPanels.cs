using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// プロパティの欄のうち、選んだレイヤー（LayerSections: レイヤー・レイヤーマスク・フィルター）と、チャンネル・テクスチャセット
    /// （ChannelSections: ノーマルの出力・メッシュマップのベイク・ポーズ）。部品は <see cref="PaintGui"/>、文字は <see cref="L"/> で訳す。
    /// 行は 22 px、左右の余白は 8 px（<see cref="UiRows"/>）。描画中のストロークのあいだは、外枠が GUI.enabled を切っているので触れない。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>行の左の名前の列（「入力 [黒] [白]」など）と、ドロップダウンの名前の列の幅。</summary>
        const float PropertyLabelWidth = 56, DropdownLabelWidth = 104;
        /// <summary>セクションの中身の終わりの余白。</summary>
        const float SectionGap = 6;

        /// <summary>このファイルたちの部品の画面上の矩形と、プロパティの欄（スクロールの中身）での矩形。Repaint のたびに覚え直す。
        /// テストが欄をスクロールして、その部品を本物のマウスの入力で押すため。</summary>
        internal readonly Dictionary<string, Rect> LayerControlScreenRects = new Dictionary<string, Rect>(), LayerControlPanelRects = new Dictionary<string, Rect>();
        Rect Spot(string id, Rect r)
        {
            if (Event.current.type == EventType.Repaint) { LayerControlScreenRects[id] = GUIUtility.GUIToScreenRect(r); LayerControlPanelRects[id] = r; }
            return r;
        }

        void LayerSections(UiRows rows)
        {
            if (Event.current.type == EventType.Repaint) { LayerControlScreenRects.Clear(); LayerControlPanelRects.Clear(); }
            var active = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            if (active == null) return;
            if (Section(rows, "layer", LayerKindName(active) + ": " + active.Name, LayerKindIcon(active))) { DrawLayerDetails(rows, active); rows.Space(SectionGap); }
            if (Section(rows, "mask", L.Tr("Layer mask"), "vignette")) { DrawMask(rows, active); rows.Space(SectionGap); }
            if (Section(rows, "filters", FiltersTitle(active), "auto_awesome")) { DrawFilters(rows, active); rows.Space(SectionGap); }
        }

        void ChannelSections(UiRows rows)
        {
            if (channel == PaintChannel.Normal || channel == PaintChannel.Height)
                if (Section(rows, "normal", L.Tr("Normal"), "3d_rotation")) { DrawNormalPanel(rows); rows.Space(SectionGap); }
            if (Section(rows, "mesh-maps", MeshMapsTitle(), "grid_on", openByDefault: false)) { DrawMeshMapPanel(rows); rows.Space(SectionGap); }
            if (preview != null && preview.HasSkinnedMeshes)
            {
                if (Section(rows, "pose", L.Tr("Pose & BlendShapes"), "accessibility", openByDefault: false)) { DrawPosePanel(rows); rows.Space(SectionGap); }
                ApplyPendingPose();
            }
        }

        /// <summary>初めて描くときの開閉を決められる <see cref="Section(UiRows, string, string, string)"/>（長い欄は閉じて始める）。</summary>
        bool Section(UiRows rows, string key, string title, string icon, bool openByDefault)
        {
            if (!openByDefault && !sectionOpen.ContainsKey(key)) sectionOpen[key] = false;
            return Section(rows, key, title, icon);
        }
        bool SectionIsOpen(string key, bool openByDefault) => sectionOpen.TryGetValue(key, out var open) ? open : openByDefault;

        static string LayerKindName(PaintLayer layer)
            => layer.IsGroup ? L.Tr("Group") : layer.Kind == LayerKind.Fill ? L.Tr("Fill Layer") : layer.Kind == LayerKind.Adjustment ? L.Tr("Adjustment Layer") : layer.Path != null ? L.Tr("Path") : L.Tr("Layer");
        static string LayerKindIcon(PaintLayer layer)
            => layer.IsGroup ? "folder" : layer.Kind == LayerKind.Fill ? "format_color_fill" : layer.Kind == LayerKind.Adjustment ? "tune" : layer.Path != null ? "conversion_path" : "brush";
        static string AdjustmentName(AdjustmentType type) => type == AdjustmentType.Levels ? "Levels" : type == AdjustmentType.HueSaturation ? "Hue / Saturation" : "Invert";
        static bool IsScalarChannel(PaintChannel c) => c == PaintChannel.Roughness || c == PaintChannel.Metallic || c == PaintChannel.Height;

        enum NoteKind { Plain, Info, Warning }
        /// <summary>折り返す説明・知らせの行（高さは文の長さで決まる。<see cref="PaintGui.Paragraph"/>・<see cref="PaintGui.Notice"/>）。
        /// indent だけ右に寄せる（フィルターの設定の中など）。</summary>
        static void NoteRow(UiRows rows, string text, NoteKind kind = NoteKind.Plain, float indent = 0)
        {
            if (string.IsNullOrEmpty(text)) return;
            var target = rows; UiRows inner = null;
            if (indent > 0)
            {
                var at = rows.Row(0, 0);
                target = inner = new UiRows(new Rect(at.x - PaintTheme.Padding + indent, at.y, rows.Width + 2 * PaintTheme.Padding - indent, 1e6f), 0);
            }
            switch (kind)
            {
                case NoteKind.Info: PaintGui.Notice(target, text, "info", PaintTheme.TextDim); break;
                case NoteKind.Warning: PaintGui.Notice(target, text, "warning", PaintTheme.Warning); break;
                default: PaintGui.Paragraph(target, text); break;
            }
            if (inner != null) rows.Space(inner.Used);
        }
        static Rect Indent(Rect r, float by) => new Rect(r.x + by, r.y, r.width - by, r.height);

        /// <summary>名前（左の列）と値の箱のドロップダウン。押すと values のメニューを箱の下に開く。</summary>
        void ChoiceDropdown<T>(Rect r, string label, T value, T[] values, Func<T, string> name, Action<T> changed, string tooltip = null, bool enabled = true)
        {
            PaintGui.FitDropdown(r, label, name(value), at =>
            {
                var menu = new GenericMenu();
                foreach (var v in values) { var item = v; menu.AddItem(new GUIContent(name(item)), Equals(item, value), () => { changed(item); Repaint(); }); }
                menu.DropDown(at);
            }, tooltip, enabled && GUI.enabled, DropdownLabelWidth);
        }

        void DrawLayerDetails(UiRows rows, PaintLayer active)
        {
            if (active.IsGroup)
                NoteRow(rows, active.BlendMode == LayerBlendMode.PassThrough ? L.Tr("Pass through: the contents blend with the layers below as if they were not grouped.") : L.Tr("Isolated: the contents are composited together first, then blended."));
            else
            {
                bool on = active.IsChannelEnabled(channel);
                bool next = PaintGui.FitToggle(Spot("layer.channel", rows.Row()), L.Tr("Paint this channel") + " (" + L.Tr(channel.ToString()) + ")", on, L.Tr("Off: this layer is left out of the selected channel (what it holds there is kept)."));
                if (next != on) TryAction(() => document.SetChannelEnabled(active.Id, channel, next));
            }
            if (active.Kind == LayerKind.Fill) DrawFill(rows, active);
            if (active.Kind == LayerKind.Adjustment) DrawAdjustment(rows, active);
        }

        void DrawFill(UiRows rows, PaintLayer active)
        {
            PaintGui.GroupLabel(rows.Row(16), L.Tr("Fill Value") + " · " + L.Tr(channel.ToString()));
            if (active.FillValues.TryGetValue(channel, out var value))
            {
                var row = rows.Row();
                var main = new Rect(row.x, row.y, row.width - 28, row.height);
                if (IsScalarChannel(channel))
                {
                    double v = PaintGui.KeepSlider(Spot("fill.value", main), L.Tr("Value"), value.R / 255.0, 0, 1, "0.###");
                    if (v != value.R / 255.0)
                    {
                        byte b = (byte)Mathf.RoundToInt((float)v * 255);
                        var next = new Rgba32(b, b, b, value.A);
                        if (next != value) TryAction(() => document.SetFillValue(active.Id, channel, next, coalesce: true));
                    }
                }
                else
                {
                    PaintGui.Text(new Rect(main.x, main.y, PropertyLabelWidth, main.height), L.Tr("Color"), PaintTheme.Label, GUI.enabled ? PaintTheme.Text : PaintTheme.TextDisabled);
                    var doc = document; var id = active.Id; var ch = channel;
                    PaintGui.ColorSwatch(new Rect(main.x + PropertyLabelWidth, main.y + 1, main.width - PropertyLabelWidth, main.height - 2), new Color32(value.R, value.G, value.B, value.A),
                        c => SetFillColorFromPicker(doc, id, ch, c), true, L.Tr("Click to choose the fill color"), GUI.enabled);
                }
                if (PaintGui.IconButton(Spot("fill.remove", new Rect(row.xMax - 24, row.y, 24, row.height)), "delete", L.Tr("Remove this channel's value (the fill then leaves the channel as it is)"), false, GUI.enabled, 16))
                    TryAction(() => document.SetFillValue(active.Id, channel, null));
            }
            else if (PaintGui.Button(Spot("fill.add", rows.Row()), L.Tr("Add a Value for This Channel"), false, GUI.enabled, L.Tr("The fill starts with the brush color"), "add"))
                TryAction(() => document.SetFillValue(active.Id, channel, GetBrush().Color));
            NoteRow(rows, L.Tr("A fill covers the whole canvas. Paint its mask to choose where it shows."));
        }

        /// <summary>色の選択（別のウィンドウ）から。選んでいるあいだに文書やレイヤーが替わっていたら何もしない。ドラッグは 1 回の Undo に
        /// まとめる（このウィンドウでマウスを離したときに区切る。前の ColorField と同じ）。</summary>
        void SetFillColorFromPicker(PaintDocument doc, Guid id, PaintChannel ch, Color c)
        {
            if (doc != document || stroke != null || !document.Layers.Any(l => l.Id == id && l.Kind == LayerKind.Fill)) return;
            var c32 = (Color32)c;
            TryAction(() => document.SetFillValue(id, ch, new Rgba32(c32.r, c32.g, c32.b, c32.a), coalesce: true));
            Repaint();
        }

        void DrawAdjustment(UiRows rows, PaintLayer active)
        {
            var a = active.Adjustment;
            PaintGui.GroupLabel(rows.Row(16), L.Tr(AdjustmentName(a.Type)));
            AdjustmentSettings next = a;
            try
            {
                switch (a.Type)
                {
                    case AdjustmentType.Levels:
                    {
                        double ib = a.InputBlack, iw = a.InputWhite, gamma = a.Gamma, ob = a.OutputBlack, ow = a.OutputWhite;
                        if (LevelsRows(rows, ref ib, ref iw, ref gamma, ref ob, ref ow, 0)) next = AdjustmentSettings.Levels(ib, iw, gamma, ob, ow);
                        break;
                    }
                    case AdjustmentType.HueSaturation:
                    {
                        double hue = PaintGui.KeepSlider(Spot("adjust.hue", rows.Row()), L.TrIn("adjustment", "Hue"), a.Hue, -180, 180, "0", "°");
                        double sat = PaintGui.KeepSlider(rows.Row(), L.TrIn("adjustment", "Saturation"), a.Saturation, -1, 1, "0", "", null, true, 100);
                        double light = PaintGui.KeepSlider(rows.Row(), L.TrIn("adjustment", "Lightness"), a.Lightness, -1, 1, "0", "", null, true, 100);
                        if (hue != a.Hue || sat != a.Saturation || light != a.Lightness) next = AdjustmentSettings.HueSaturation(hue, sat, light);
                        break;
                    }
                    default: NoteRow(rows, L.Tr("Inverts the colour of everything below.")); break;
                }
            }
            catch (ArgumentException ex) { message = ex.Message; next = a; }
            if (!next.Equals(a)) TryAction(() => document.SetAdjustment(active.Id, next, coalesce: true));
            NoteRow(rows, L.Tr("Applies to the layers below."));
            if (!a.AppliesTo(channel)) NoteRow(rows, L.Tr("{0} does not apply to the {1} channel.", L.Tr(AdjustmentName(a.Type)), L.Tr(channel.ToString())), NoteKind.Info);
        }

        /// <summary>レベル補正の行（調整レイヤーとフィルターで共通）: 入力の黒・白、ガンマ、出力の黒・白。黒と白は入れ違わない（入力は
        /// 0.004 以上離す）。触った値だけを変え、変わったら true。</summary>
        static bool LevelsRows(UiRows rows, ref double ib, ref double iw, ref double gamma, ref double ob, ref double ow, float indent)
        {
            double ib0 = ib, iw0 = iw, g0 = gamma, ob0 = ob, ow0 = ow;
            var c = PaintGui.LabeledColumns(Indent(rows.Row(), indent), L.TrIn("levels", "Input"), PropertyLabelWidth, 2);
            ib = PaintGui.KeepSlider(c[0], L.TrIn("levels", "Black"), ib, 0, 1, "0.###", "", L.Tr("Input values at or below this become black"));
            iw = PaintGui.KeepSlider(c[1], L.TrIn("levels", "White"), iw, 0, 1, "0.###", "", L.Tr("Input values at or above this become white"));
            if (ib != ib0) ib = Math.Max(0, Math.Min(ib, iw - .004));
            if (iw != iw0) iw = Math.Min(1, Math.Max(iw, ib + .004));
            gamma = PaintGui.KeepSlider(PaintGui.LabeledColumns(Indent(rows.Row(), indent), L.TrIn("levels", "Gamma"), PropertyLabelWidth, 1)[0], null, gamma, .1, 9.99, "0.00", "", L.Tr("Midtones: above 1 brightens, below 1 darkens"));
            c = PaintGui.LabeledColumns(Indent(rows.Row(), indent), L.TrIn("levels", "Output"), PropertyLabelWidth, 2);
            ob = PaintGui.KeepSlider(c[0], L.TrIn("levels", "Black"), ob, 0, 1, "0.###");
            ow = PaintGui.KeepSlider(c[1], L.TrIn("levels", "White"), ow, 0, 1, "0.###");
            return ib != ib0 || iw != iw0 || gamma != g0 || ob != ob0 || ow != ow0;
        }

        bool EditingMask => editMask && document.Layers.Any(l => l.Id == selectedLayer && l.Mask != null);

        void DrawMask(UiRows rows, PaintLayer active)
        {
            var mask = active.Mask;
            if (mask == null)
            {
                if (PaintGui.Button(Spot("mask.add", rows.Row()), L.Tr("Add Layer Mask"), false, GUI.enabled, L.Tr("A mask hides parts of the layer; it is shared by all channels"), "add"))
                    TryAction(() => { document.AddLayerMask(active.Id); editMask = true; });
                return;
            }
            bool paint = PaintGui.ToggleButton(Spot("mask.paint", rows.Row(24)), L.Tr("Paint on Mask"), editMask, L.Tr("Brush strokes go to the mask: paint hides, erase reveals"), "brush", GUI.enabled);
            if (paint != editMask) { editMask = paint; Repaint(); }
            double density = PaintGui.KeepSlider(Spot("mask.density", rows.Row()), L.TrIn("mask", "Density"), mask.Density, 0, 1, "0", "%", L.Tr("How strongly the mask hides (0 %: as if there were no mask)"), true, 100);
            if (density != mask.Density) TryAction(() => document.SetLayerMaskDensity(active.Id, density, coalesce: true));
            var row = rows.Row();
            var cols = UiRows.Split(new Rect(row.x, row.y, row.width - 28, row.height), 2, 6);
            bool maskOn = PaintGui.FitToggle(Spot("mask.enabled", cols[0]), L.TrIn("mask", "Enabled"), mask.Enabled, L.Tr("Off: the layer shows as if it had no mask (the mask is kept)"));
            if (maskOn != mask.Enabled) TryAction(() => document.SetLayerMaskEnabled(active.Id, maskOn));
            bool inverted = PaintGui.FitToggle(Spot("mask.invert", cols[1]), L.TrIn("mask", "Invert"), mask.Inverted, L.Tr("Swap what the mask hides and reveals"));
            if (inverted != mask.Inverted) TryAction(() => document.SetLayerMaskInverted(active.Id, inverted));
            if (PaintGui.IconButton(Spot("mask.delete", new Rect(row.xMax - 24, row.y, 24, row.height)), "delete", L.Tr("Delete Layer Mask"), false, GUI.enabled, 16))
                TryAction(() => { document.RemoveLayerMask(active.Id); editMask = false; });
            NoteRow(rows, L.Tr("One mask for all channels."));
        }
    }
}
