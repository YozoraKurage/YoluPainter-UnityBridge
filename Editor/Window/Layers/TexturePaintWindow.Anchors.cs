using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// Anchor（Substance Painter のアンカーポイント。<see cref="AnchorPoint"/>）: 層かマスクに置く・名前を変える・外す（レイヤーのメニューと
    /// 右クリック、プロパティの「層」と「レイヤーマスク」の欄の行）、読む側は Generator の種類「Anchor」（その欄で、どの Anchor・チャンネル・
    /// 読み方を選び、状態と理由を見て、置いた層へ移る）。レイヤーの一覧のサムネイルに印。並べ替え・削除・結合などで読む段が使えなくなったら、
    /// その操作の知らせに理由を添える（黙って壊さない。編集そのものは断らず、Undo で戻る。Substance と同じ）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>Generator の欄で選べるチャンネル（Normal は 1 画素 1 値ではないので無い）。</summary>
        static readonly PaintChannel[] AnchorChannels = { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Metallic, PaintChannel.Height, PaintChannel.Emission };

        // ───────── 置く・外す ─────────

        /// <summary>選んだ層（かそのマスク）に Anchor を置く（1 回の Undo）。名前は層の名前（同じ名前があれば番号を足す）。</summary>
        internal AnchorPoint AddAnchorTo(AnchorPlacement placement)
        {
            AnchorPoint added = null;
            TryAction(() =>
            {
                var layer = document.GetLayer(selectedLayer);
                string name = UniqueAnchorName(placement == AnchorPlacement.Mask ? L.Tr("{0} (mask)", layer.Name) : layer.Name);
                added = document.AddAnchor(layer.Id, placement, name);
                message = placement == AnchorPlacement.Mask
                    ? L.Tr("Put the anchor \"{0}\" on the mask: generators on the layers above can read how much this layer shows (Generator ▸ Anchor).", added.Name)
                    : L.Tr("Put the anchor \"{0}\" on the layer: generators on the layers above can read the stack up to here (Generator ▸ Anchor).", added.Name);
            });
            return added;
        }
        string UniqueAnchorName(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? L.Tr("Anchor") : name.Trim();
            if (name.Length > AnchorPoint.MaxNameLength - 4) name = name.Substring(0, AnchorPoint.MaxNameLength - 4);
            var taken = new HashSet<string>(document.Anchors.Select(a => a.Anchor.Name));
            if (!taken.Contains(name)) return name;
            for (int i = 2; ; i++) if (!taken.Contains(name + " " + i)) return name + " " + i;
        }
        internal void RemoveAnchorOf(AnchorPoint anchor)
        {
            TryAction(() => { document.RemoveAnchor(anchor.Id); message = L.Tr("Removed the anchor \"{0}\" (undo brings it back).", anchor.Name); repaintPixels = true; });
        }
        internal void RenameAnchor(AnchorPoint anchor, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == anchor.Name) return;
            TryAction(() => document.RenameAnchor(anchor.Id, name.Trim()));
        }

        /// <summary>レイヤーのメニュー（と右クリック）の Anchor の項目。</summary>
        void AnchorMenuItems(PaintMenu m)
        {
            var active = SelectedOrNull; bool idle = stroke == null && !toolDragging;
            if (active?.Anchor == null) Item(m, "Add Anchor", () => AddAnchorTo(AnchorPlacement.Layer), idle && active != null);
            else { var a = active.Anchor; Item(m, "Remove Anchor", () => RemoveAnchorOf(a), idle); }
            if (active?.Mask == null) return;
            if (active.Mask.Anchor == null) Item(m, "Add Anchor to Mask", () => AddAnchorTo(AnchorPlacement.Mask), idle);
            else { var a = active.Mask.Anchor; Item(m, "Remove Mask Anchor", () => RemoveAnchorOf(a), idle); }
        }

        /// <summary>プロパティの「層」（placement Layer）・「レイヤーマスク」（Mask）の欄の Anchor の行: 無ければ置くボタン、あれば名前の欄・
        /// 読む段の数・外すボタン。</summary>
        void DrawAnchorRow(UiRows rows, PaintLayer active, AnchorPlacement placement)
        {
            var anchor = placement == AnchorPlacement.Layer ? active.Anchor : active.Mask?.Anchor;
            string key = placement == AnchorPlacement.Layer ? "anchor" : "mask.anchor";
            bool idle = GUI.enabled && stroke == null;
            if (anchor == null)
            {
                if (PaintGui.Button(Spot(key + ".add", rows.Row()), placement == AnchorPlacement.Layer ? L.Tr("Add Anchor") : L.Tr("Add Anchor to Mask"), false, idle,
                        placement == AnchorPlacement.Layer
                            ? L.Tr("Name the stack's result up to this layer, so generators on the layers above can read it (a lower layer's Height can drive an upper layer's wear, for example)")
                            : L.Tr("Name this mask, so generators on the layers above can read how much this layer shows"), "anchor"))
                    AddAnchorTo(placement);
                return;
            }
            var row = rows.Row();
            PaintGui.Icon(new Rect(row.x, row.y, 18, row.height), "anchor", PaintTheme.Accent, 15);
            var readers = document.AnchorReaders(anchor.Id);
            string count = L.Tr("read by {0}", readers.Count);
            float countWidth = PaintGui.TextWidth(count, PaintTheme.LabelDim) + 6;
            var nameRect = new Rect(row.x + 22, row.y, Math.Max(40, row.width - 22 - countWidth - 30), row.height);
            string name = PaintGui.TextField(Spot(key + ".name", nameRect), anchor.Name, L.Tr("The anchor's name: generators above list it by this name"));
            if (name != anchor.Name) RenameAnchor(anchor, name);
            var countRect = new Rect(nameRect.xMax + 4, row.y, countWidth, row.height);
            PaintGui.Text(countRect, count, PaintTheme.LabelDim, PaintTheme.TextDim);
            PaintGui.Tooltip(countRect, readers.Count == 0 ? L.Tr("No generator reads this anchor yet: add Generator ▸ Anchor on a layer above.")
                : L.Tr("Read by:") + "\n" + string.Join("\n", readers.Select(r => r.layer.Name + (r.target == FilterTarget.Mask ? " (" + L.Tr("mask") + ")" : ""))));
            if (PaintGui.IconButton(Spot(key + ".remove", new Rect(row.xMax - 24, row.y, 24, row.height)), "delete", L.Tr("Remove the anchor (generators that read it pass their input through until you undo)"), false, idle, 16))
                RemoveAnchorOf(anchor);
        }

        /// <summary>レイヤーの一覧のサムネイル（マスクのサムネイル）の左上の Anchor の印。</summary>
        void AnchorBadge(Rect r, AnchorPoint anchor)
        {
            PaintGui.Rounded(r, PaintTheme.PanelBg, 3);
            PaintGui.Icon(r, "anchor", PaintTheme.Accent, r.width - 1);
            PaintGui.Tooltip(r, L.Tr("Anchor \"{0}\"", anchor.Name));
        }

        // ───────── 読む側（Generator の欄） ─────────

        /// <summary>層の Generator を足すときに読む Anchor: その層から読めるうちで一番上（すぐ下の結果）。無ければ空。</summary>
        Guid NearestAnchorBelow(Guid layerId) => document.AnchorsReadableFrom(layerId).Select(a => a.Anchor.Id).LastOrDefault();

        /// <summary>Generator の欄の Anchor の行: 読む Anchor（読めるものと、今読んでいるもの）、チャンネル、読み方（マスクの Anchor では使わない）、
        /// 置いた層へ移る。選んだ値はその場で文書に入れる（1 回の Undo）。</summary>
        void AnchorRows(UiRows rows, FilterEffect e, float indent)
        {
            var g = e.Settings.Generator;
            var choices = new List<Guid> { Guid.Empty };
            choices.AddRange(document.AnchorsReadableFrom(selectedLayer).Select(a => a.Anchor.Id));
            if (g.AnchorId != Guid.Empty && !choices.Contains(g.AnchorId)) choices.Add(g.AnchorId);
            var current = document.FindAnchor(g.AnchorId);
            // 名前は利用者のデータ（長ければ詰めて、ツールチップに全部）
            PaintGui.FitDropdown(Spot("generator.anchor", Indent(rows.Row(), indent)), L.Tr("Anchor"), AnchorChoiceName(g.AnchorId), at =>
            {
                var menu = new PaintMenu();
                foreach (var id in choices)
                {
                    var choice = id;
                    menu.AddItem(new GUIContent(AnchorChoiceName(choice)), choice == g.AnchorId, () =>
                    {
                        if (choice != g.AnchorId) TryAction(() => { document.SetGeneratorAnchor(selectedLayer, e.Id, choice, g.AnchorChannel, g.AnchorRead); repaintPixels = true; });
                        Repaint();
                    });
                }
                menu.DropDown(at);
            }, L.Tr("The anchor point this generator reads: one on a layer below this one (the stack up to there) or on a lower layer's mask")
                + (current != null ? "\n" + L.Tr("On the layer \"{0}\"", current.Layer.Name) : ""), GUI.enabled, DropdownLabelWidth, valueIsData: true);
            bool mask = current != null && current.Anchor.Placement == AnchorPlacement.Mask;
            ChoiceDropdown(Spot("generator.anchorChannel", Indent(rows.Row(), indent)), L.Tr("Channel"), g.AnchorChannel, AnchorChannels, c => L.Tr(c.ToString()),
                c => { if (c != g.AnchorChannel) TryAction(() => { document.SetGeneratorAnchor(selectedLayer, e.Id, g.AnchorId, c, g.AnchorRead); repaintPixels = true; }); },
                mask ? L.Tr("A mask anchor has one value: how much its layer shows") : L.Tr("Which channel of the stack to read (Height: the painted relief)"), !mask);
            ChoiceDropdown(Spot("generator.anchorRead", Indent(rows.Row(), indent)), L.Tr("Read"), g.AnchorRead, (AnchorRead[])Enum.GetValues(typeof(AnchorRead)), AnchorReadName,
                r => { if (r != g.AnchorRead) TryAction(() => { document.SetGeneratorAnchor(selectedLayer, e.Id, g.AnchorId, g.AnchorChannel, r); repaintPixels = true; }); },
                L.Tr("Value: the channel's value where something is painted (0 where nothing is; colours by brightness). Coverage: how opaque the stack is there."), !mask);
            if (current != null && PaintGui.Button(Spot("generator.anchorGo", Indent(rows.Row(22), indent)), L.Tr("Select the Anchor's Layer"), false, GUI.enabled && stroke == null,
                    L.Tr("Select the layer the anchor is on"), "anchor"))
            {
                SelectSingleLayer(current.Layer.Id); editMask = mask; Repaint();
            }
        }
        string AnchorChoiceName(Guid id)
        {
            if (id == Guid.Empty) return L.TrIn("anchor", "None");
            var info = document.FindAnchor(id);
            if (info == null) return L.Tr("(missing anchor)");
            return info.Anchor.Placement == AnchorPlacement.Mask && !info.Anchor.Name.EndsWith(")") ? L.Tr("{0} (mask)", info.Anchor.Name) : info.Anchor.Name; // 層の名前はツールチップに
        }
        static string AnchorReadName(AnchorRead read) => read == AnchorRead.Coverage ? L.TrIn("anchor", "Coverage") : L.TrIn("anchor", "Value");

        /// <summary>一覧の 1 行の要約に添える: 読む Anchor の名前、使えなければその印。</summary>
        string AnchorSummary(FilterEffect e)
        {
            var g = e.Settings.Generator; var info = document.FindAnchor(g.AnchorId);
            string text = "  · " + (g.AnchorId == Guid.Empty ? L.Tr("no anchor") : info == null ? L.Tr("(missing anchor)") : info.Anchor.Name);
            if (e.IsActive && !StageStatus(e).Active) text += "  · " + L.Tr("not usable");
            return text;
        }

        /// <summary>選んだ層の Generator の段の状態（Anchor の参照も見る）。段が選んだ層に無ければ設定だけの状態。</summary>
        GeneratorStatus StageStatus(FilterEffect e)
        {
            var layer = SelectedOrNull;
            if (layer != null && document.FindFilter(layer.Id, e.Id, out _) != null) return document.GetGeneratorStatus(layer.Id, e.Id);
            return document.GetGeneratorStatus(e.Settings.Generator);
        }

        // ───────── 使えなくなった参照を知らせる ─────────

        PaintDocument anchorIssuesDocument; long anchorIssuesRevision = -1; HashSet<Guid> anchorIssuesKnown = new HashSet<Guid>();

        /// <summary>操作の後: 新しく使えなくなった Anchor の参照（並べ替えで上下が逆・消えた）があれば、知らせに理由を添える。文書が変わった
        /// ときだけ見る。</summary>
        void NoteNewAnchorIssues()
        {
            if (document == null || stroke != null) return;
            if (ReferenceEquals(anchorIssuesDocument, document) && anchorIssuesRevision == document.Revision) return;
            var issues = document.AnchorIssues();
            bool same = ReferenceEquals(anchorIssuesDocument, document);
            anchorIssuesDocument = document; anchorIssuesRevision = document.Revision;
            var fresh = same ? issues.Where(i => !anchorIssuesKnown.Contains(i.FilterId) && i.Kind != AnchorIssueKind.NotChosen).ToList() : new List<AnchorIssue>();
            anchorIssuesKnown = new HashSet<Guid>(issues.Select(i => i.FilterId));
            if (fresh.Count == 0) return;
            string note = L.Tr("{0} anchor generator(s) now pass their input through: {1}", fresh.Count, fresh[0].Reason);
            message = string.IsNullOrEmpty(message) ? note : message + " " + note;
        }
    }
}
