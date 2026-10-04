using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// ID マップの色で選ぶ（Substance Painter の Color Selection）: 今のテクスチャセットの焼いた ID マップの、クリックした所の色を取る。
    /// <list type="bullet">
    /// <item>ツール「ID の色で選択」（Shift+W。W のマジックワンドの仲間）: 2D キャンバスはその画素、3D ビューはその面の UV のテクセルの色から
    /// 許容の幅の中の色の画素を選択範囲にする（<see cref="SelectionMask.FromIdColors"/>。Shift で追加・Ctrl で削除・Shift+Ctrl で交差は
    /// ほかの選択ツールと同じ。1 回の Undo）。</item>
    /// <item>Generator「ID の色」のスポイト: 欄のボタンで入れ、2D キャンバスか 3D ビューのクリックで、そこの色を Generator の色の一覧に足す
    /// （Ctrl を押していれば外す）。色はその ID マップのベイクにだけ意味があるので、足すときに ID マップをそのベイクにピン留めする
    /// （焼き直して色が変わったら、黙って別の部品を選ばずに理由を出す）。ほかのベイクにピン留めした Generator には足さない。Esc か
    /// ボタンかツールの持ち替えで抜ける。1 回の取り・外しが 1 回の Undo。</item>
    /// </list>
    /// ID マップが無い・古い（モデル・大きさ・スロット・ベイクの設定が今と違う）ときは、何も選ばずに理由を出す。読むのはマップだけで、
    /// 元のモデル・マテリアル・テクスチャには触れない。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] int idSelectTolerance = IdMapColors.DefaultTolerance;
        /// <summary>ID の色で選ぶときの許容の幅（8 bit のチャンネルの差の最大、0〜255）。</summary>
        internal int IdSelectTolerance { get => idSelectTolerance; set => idSelectTolerance = Mathf.Clamp(value, 0, IdMapColors.MaxTolerance); }

        /// <summary>スポイトを入れた Generator（層とスタックの段）。入れていなければ Guid.Empty。窓の状態で、保存しない。</summary>
        Guid idPickLayer, idPickFilter;
        internal bool IdColorPicking => idPickFilter != Guid.Empty;
        internal Guid IdColorPickFilter => idPickFilter;

        /// <summary>Generator「ID の色」のスポイトを入れる（同じ Generator ならそのまま）。</summary>
        internal void BeginIdColorPick(Guid filter)
        {
            idPickLayer = selectedLayer; idPickFilter = filter;
            message = L.Tr("Picking ID colours");
            Repaint();
        }
        internal void EndIdColorPick(string note = null)
        {
            if (!IdColorPicking) return;
            idPickLayer = Guid.Empty; idPickFilter = Guid.Empty;
            if (note != null) message = note;
            Repaint(); RepaintPanelWindowsSoon();
        }

        /// <summary>今のテクスチャセットの ID マップ（今の条件で焼いたもの）。無い・古いときは null と理由。</summary>
        internal BakedMeshMap UsableIdMap(out string reason)
        {
            if (meshMaps.TryGetUsable(MeshMapKind.Id, CurrentMeshMapExpectation(), out var map, out var why)) { reason = null; return map; }
            reason = L.Tr("There is no usable ID map for this texture set: {0}", why);
            return null;
        }

        /// <summary>ポインタの下の ID の色。2D キャンバスはその画素のテクセル、3D ビューは当たった面の UV のテクセル（今のセットの面だけ）。
        /// 取れなければ false と理由（別のセットの面のときは知らせを出し、理由は null）。</summary>
        internal bool TryIdColorUnder(Vector2 pointer, bool onSurface, BakedMeshMap map, out int rgb, out string why)
        {
            rgb = 0; why = null;
            if (onSurface)
            {
                if (preview == null || !preview.HasModel) { why = L.Tr("No model in the 3D view."); return false; }
                if (!preview.TryPick(surfaceRect, pointer, out var hit)) { why = L.Tr("Nothing of the model under the pointer."); return false; }
                if (!PaintsSlot(hit.MaterialSlot)) { OtherSlotPressed(hit.MaterialSlot); return false; }
                if (!IdMapColors.TryGetAtUv(map, hit.UV.x, hit.UV.y, out rgb)) { why = L.Tr("The ID map has no colour where that face lies in UV."); return false; }
                return true;
            }
            var p = CanvasPoint(pointer);
            if (p.x < 0 || p.y < 0 || p.x >= document.Width || p.y >= document.Height) { why = L.Tr("Outside the canvas."); return false; }
            if (!IdMapColors.TryGet(map, Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), out rgb)) { why = L.Tr("No part there: the ID map has no colour outside the UV islands and their padding."); return false; }
            return true;
        }

        /// <summary>ID の色で選ぶツールと Generator のスポイトの、2D キャンバスと 3D ビューの左クリック（とスポイトの Esc）。扱ったら true。</summary>
        bool HandleIdColorInput(Event e)
        {
            bool picking = IdColorPicking;
            if (!picking && tool != PaintTool.IdSelect) return false;
            if (picking && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && GUIUtility.keyboardControl == 0)
            { EndIdColorPick(L.Tr("Stopped picking ID colours.")); NoteTookKey(e); e.Use(); return true; }
            if (e.type != EventType.MouseDown || e.button != 0 || e.alt || stroke != null || toolDragging) return false;
            bool onSurface = surfaceRect.Contains(e.mousePosition);
            if (!onSurface && !canvasRect.Contains(e.mousePosition)) return false;
            var pointer = e.mousePosition; var mode = CombineOf(e); bool remove = e.control || e.command;
            TryAction(() => { if (picking) PickIdColorForGenerator(pointer, onSurface, remove); else SelectByIdColor(pointer, onSurface, mode); });
            e.Use(); Repaint(); return true;
        }

        /// <summary>ポインタの下の ID の色の画素を、今の選択範囲と組み合わせて選択範囲にする（1 回の Undo）。</summary>
        internal void SelectByIdColor(Vector2 pointer, bool onSurface, SelectionCombine mode)
        {
            var map = UsableIdMap(out var reason);
            if (map == null) { message = reason; return; }
            if (!TryIdColorUnder(pointer, onSurface, map, out int rgb, out var why)) { if (why != null) message = why; return; }
            ApplySelection(SelectionMask.FromIdColors(document, map, new[] { rgb }, idSelectTolerance), mode);
            message = document.Selection == null ? L.Tr("Nothing selected.") : L.Tr("Selected ID colour {0} ± {1} ({2}).", IdMapColors.Hex(rgb), idSelectTolerance, mode);
        }

        /// <summary>スポイトを入れた Generator（今の文書の選んだ層にまだあるもの）。無くなっていればスポイトを抜いて null。</summary>
        FilterEffect PickedIdGenerator()
        {
            FilterEffect effect = null;
            if (IdColorPicking && idPickLayer == selectedLayer && selectedFilter == idPickFilter && document.Layers.Any(l => l.Id == idPickLayer))
                effect = document.FindFilter(idPickLayer, idPickFilter, out _);
            if (effect == null || !effect.Settings.IsGenerator || effect.Settings.Generator.Type != GeneratorType.IdColor) { EndIdColorPick(L.Tr("Stopped picking ID colours: the generator is no longer selected.")); return null; }
            return effect;
        }

        /// <summary>スポイト: ポインタの下の ID の色を Generator の一覧に足す（remove なら外す）。足すときは ID マップをこのベイクにピン留めする。</summary>
        internal void PickIdColorForGenerator(Vector2 pointer, bool onSurface, bool remove)
        {
            var effect = PickedIdGenerator(); if (effect == null) return;
            var map = UsableIdMap(out var reason);
            if (map == null) { message = reason; return; }
            var g = effect.Settings.Generator; string key = map.Provenance.ConditionKey;
            if (g.Pins.TryGetValue(MeshMapKind.Id, out var pin) && pin != key)
            { message = L.Tr("This generator reads another ID bake (Only this bake)."); return; }
            if (!TryIdColorUnder(pointer, onSurface, map, out int rgb, out var why)) { if (why != null) message = why; return; }
            GeneratorSettings next;
            if (remove)
            {
                if (!g.IdColors.Contains(rgb)) { message = L.Tr("{0} is not in the list.", IdMapColors.Hex(rgb)); return; }
                next = g.WithIdColorRemoved(rgb);
            }
            else
            {
                if (g.IdColors.Contains(rgb)) { message = L.Tr("{0} is already in the list.", IdMapColors.Hex(rgb)); return; }
                if (g.IdColors.Count >= GeneratorSettings.MaxIdColors) { message = L.Tr("A generator holds at most {0} ID colours.", GeneratorSettings.MaxIdColors); return; }
                next = g.WithIdColorAdded(rgb).WithPin(MeshMapKind.Id, key);
            }
            document.EndCoalescing(); // 欄のスライダーのドラッグにまとめない
            ApplyFilterSettings(effect.Id, effect.Settings.WithGenerator(next));
            message = remove ? L.Tr("Took {0} out of the ID colours.", IdMapColors.Hex(rgb)) : L.Tr("Added {0} to the ID colours.", IdMapColors.Hex(rgb));
            RepaintPanelWindowsSoon();
        }

        /// <summary>ベイクの窓を開いて ID にチェックを入れ、ID の設定を見せる。</summary>
        internal void OpenIdBake()
        {
            SetMeshBakeKind(MeshMapKind.Id, true);
            var w = OpenMeshBakeWindow();
            if (w != null) w.Page = (int)MeshMapKind.Id;
        }

        // ───────── オプションバーとプロパティの欄 ─────────

        /// <summary>オプションバーの「ID の色で選択」の部品。next は次の部品の矩形を返す（幅を受け取る）、fit は文字の幅の矩形。</summary>
        void IdSelectOptions(Func<float, Rect> next, Func<string, Rect> fit)
        {
            idSelectTolerance = PaintGui.IntSlider(Mark("idselect-tolerance", next(160)), L.Tr("Tolerance"), idSelectTolerance, 0, IdMapColors.MaxTolerance, "",
                L.Tr("How far (largest 8-bit channel difference) a pixel's ID colour may be from the clicked one. Baked ID colours of up to 4080 parts differ by 17 or more."));
            if (UsableIdMap(out _) == null)
            {
                if (PaintGui.Button(Mark("idselect-bake", next(PaintGui.TextWidth(L.Tr("Bake ID Map…"), PaintTheme.Label) + 40)), L.Tr("Bake ID Map…"), true, stroke == null,
                        L.Tr("Open the bake window with the ID map checked"), "local_fire_department"))
                    TryAction(OpenIdBake);
                var t = L.Tr("No usable ID map yet"); PaintGui.Text(fit(t), t, PaintTheme.LabelDim, PaintTheme.Warning);
            }
            if (PaintGui.IconButton(next(28), "select_all", L.Tr("Select All (Ctrl+A)"))) document.SetSelection(SelectionMask.All(document));
            if (PaintGui.IconButton(next(28), "deselect", L.Tr("Deselect (Ctrl+D)"), false, document.Selection != null)) document.ClearSelection();
            if (PaintGui.IconButton(next(28), "invert_colors", L.Tr("Inverse (Ctrl+Shift+I)"), false, document.Selection != null)) document.SetSelection(document.Selection.Invert());
        }

        /// <summary>プロパティの欄の「ID マップ」: 今の ID マップの状態（最新・古い・無い と理由）、色の元、焼く口。</summary>
        void IdMapSection(UiRows rows)
        {
            if (!ToolSection(rows, "id-map", L.Tr("ID Map"), "palette")) return;
            var map = UsableIdMap(out var reason);
            if (map != null) NoteRow(rows, L.Tr("Colours from: {0}", IdSourceFromKey(map.Provenance.SettingsKey)), NoteKind.Info);
            else NoteRow(rows, reason, NoteKind.Warning);
            if (PaintGui.Button(Mark("idselect-bake-panel", rows.Row(26, 4)), map != null ? L.Tr("Bake ID Map Again…") : L.Tr("Bake ID Map…"), map == null, stroke == null && meshBakeJob == null,
                    L.Tr("Open the bake window with the ID map checked"), "local_fire_department"))
                TryAction(OpenIdBake);
            IdColorAssignmentRows(rows, Repaint);
            rows.Space(4);
        }

        /// <summary>Generator「ID の色」の行: 色の一覧（見本・16 進・外す）、スポイト、許容の幅。外す・スポイトはその場で入れ（1 回の Undo）、
        /// 許容の幅は next に入れて返す（呼び手がドラッグを 1 回の Undo にまとめる）。</summary>
        GeneratorSettings IdColorRows(UiRows rows, FilterEffect e, GeneratorSettings next, float indent)
        {
            var g = e.Settings.Generator;
            PaintGui.GroupLabel(Indent(rows.Row(16), indent), L.Tr("ID Colors"), L.Tr("Texels of the ID map with one of these colours (within the tolerance) get 1, the rest 0."));
            if (g.IdColors.Count == 0) NoteRow(rows, L.Tr("No colours"), NoteKind.Info, indent);
            for (int i = 0; i < g.IdColors.Count; i++)
            {
                int rgb = g.IdColors[i];
                var row = Spot("generator.id.color." + i, Indent(rows.Row(20, 2), indent));
                var swatch = new Rect(row.x, row.y + 2, 34, row.height - 4);
                PaintGui.Rounded(swatch, new Color((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f, 1), 3); PaintGui.Outline(swatch, PaintTheme.Border, 1, 3);
                PaintGui.Text(new Rect(swatch.xMax + 8, row.y, row.width - 34 - 8 - 28, row.height), IdMapColors.Hex(rgb), PaintTheme.Label);
                if (PaintGui.IconButton(Spot("generator.id.remove." + i, new Rect(row.xMax - 24, row.y, 24, row.height)), "close", L.Tr("Take this colour out"), false, GUI.enabled && stroke == null, 15))
                    ApplyFilterSettings(e.Id, e.Settings.WithGenerator(g.WithIdColorRemoved(rgb)));
            }
            bool picking = IdColorPicking && idPickFilter == e.Id;
            if (PaintGui.Button(Spot("generator.id.pick", Indent(rows.Row(24), indent)), picking ? L.Tr("Picking… (Esc stops)") : L.Tr("Pick from ID Map"), picking, GUI.enabled && stroke == null,
                    L.Tr("Then click parts on the 2D canvas or the 3D view: each click adds the ID colour there (Ctrl+click takes it out). The generator then reads only this ID bake."), "colorize"))
            {
                if (picking) EndIdColorPick(L.Tr("Stopped picking ID colours.")); else BeginIdColorPick(e.Id);
            }
            return next.WithIdTolerance(PaintGui.KeepIntSlider(Spot("generator.id.tolerance", Indent(rows.SliderRow(), indent)), L.Tr("Tolerance"), next.IdTolerance, 0, IdMapColors.MaxTolerance, "",
                L.Tr("How far (largest 8-bit channel difference) a texel's ID colour may be from a listed one. Baked ID colours of up to 4080 parts differ by 17 or more.")));
        }

        /// <summary>由来の設定の文字列（"source=MeshPart;algorithm=2"）の元の名前。</summary>
        static string IdSourceFromKey(string key)
        {
            foreach (var part in (key ?? "").Split(';'))
                if (part.StartsWith("source=", StringComparison.Ordinal) && Enum.TryParse(part.Substring(7), false, out MeshIdSource source) && Enum.IsDefined(typeof(MeshIdSource), source))
                    return IdSourceName(source);
            return L.Tr("unknown");
        }
    }
}
