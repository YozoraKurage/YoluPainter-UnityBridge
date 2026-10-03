using System;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>YoluPainter のクリップボード: エディタのセッションのあいだだけメモリに持つ画素（ファイルにも OS のクリップボードにも
    /// 書かない）。窓・テクスチャセットをまたいで貼れる。スクリプトのコンパイル（ドメインのリロード）で空になる。Unity には画像を OS の
    /// クリップボードとやり取りする API が無い（EditorGUIUtility.systemCopyBuffer は文字だけ）ので、ほかのアプリとは受け渡さない。</summary>
    internal static class PainterClipboard
    {
        internal static PixelClipboard Content { get; set; }
        /// <summary>写せる大きさの上限: 個人の設定の 1 回の操作の予算（貼るときに作る画素も同じ予算で断る）。</summary>
        internal static long LimitBytes => PainterSettings.StrokeBudgetBytes;
    }

    /// <summary>レイヤーの複製・結合とクリップボード（コピー・結合してコピー・カット・新しいレイヤーとして貼り付け）。キー・メニュー・
    /// 右クリックから呼ぶ。操作は Core（PaintDocument.LayerOps.cs / Merge.cs）にあり、ここは選ぶ層・確かめ・知らせだけ。</summary>
    public sealed partial class TexturePaintWindow
    {
        internal enum LayerCommand { None, Duplicate, MergeDown, MergeVisible, Copy, CopyMerged, Cut, Paste }

        /// <summary>Ctrl（Mac は Cmd）とのキー: J 複製、E 下と結合（グループならグループを結合）、Shift+E 表示を結合、C コピー、
        /// Shift+C 結合してコピー、X カット、V 貼り付け。Alt との組は Photoshop で別の意味なので受けない。</summary>
        static LayerCommand LayerCommandOf(Event e)
        {
            if (!(e.control || e.command) || e.alt) return LayerCommand.None;
            switch (e.keyCode)
            {
                case KeyCode.J: return e.shift ? LayerCommand.None : LayerCommand.Duplicate;
                case KeyCode.E: return e.shift ? LayerCommand.MergeVisible : LayerCommand.MergeDown;
                case KeyCode.C: return e.shift ? LayerCommand.CopyMerged : LayerCommand.Copy;
                case KeyCode.X: return e.shift ? LayerCommand.None : LayerCommand.Cut;
                case KeyCode.V: return e.shift ? LayerCommand.None : LayerCommand.Paste;
                default: return LayerCommand.None;
            }
        }

        /// <summary>レイヤーの操作を 1 つ行う。ストロークやドラッグの最中は断る（知らせるだけで何も変えない）。</summary>
        internal void RunLayerCommand(LayerCommand command)
        {
            if (stroke != null || toolDragging) { message = L.Tr("Finish the stroke first."); return; }
            try
            {
                switch (command)
                {
                    case LayerCommand.Duplicate: DuplicateSelectedLayer(); break;
                    case LayerCommand.MergeDown: MergeDownSelected(); break;
                    case LayerCommand.MergeVisible: MergeVisibleLayers(); break;
                    case LayerCommand.Copy: CopyPixels(false); break;
                    case LayerCommand.CopyMerged: CopyPixels(true); break;
                    case LayerCommand.Cut: CutPixels(); break;
                    case LayerCommand.Paste: PasteClipboard(); break;
                }
            }
            catch (LayerOpException ex) { message = RefusalText(ex); }
            catch (Exception ex) { message = L.Tr(ex.Message); Debug.LogWarning("Texture Painter: " + ex.Message); }
            Repaint();
        }

        PaintLayer SelectedOrNull => document.Layers.FirstOrDefault(l => l.Id == selectedLayer);

        // ───────── 複製 ─────────

        /// <summary>選んでいる層（グループなら中身ごと）を複製し、元のすぐ上に置いて選ぶ。選択範囲があっても層の全体（Photoshop の Ctrl+J は
        /// 選択範囲があると「選択範囲をコピーしたレイヤー」になるが、ここではいつも複製。選んだ所だけなら Ctrl+C → Ctrl+V）。</summary>
        internal void DuplicateSelectedLayer()
        {
            var active = SelectedOrNull; if (active == null) { message = L.Tr("Select a layer first."); return; }
            var copy = document.DuplicateLayer(active.Id, L.Tr("{0} copy", active.Name));
            selectedLayer = copy.Id; editMask = false;
            message = L.Tr("Duplicated {0}.", active.Name);
        }

        // ───────── 結合 ─────────

        /// <summary>下のレイヤーと結合（選んでいるのがグループならグループを結合）。見た目が丸めの 1 段より大きく変わるなら確かめる。</summary>
        internal void MergeDownSelected()
        {
            var active = SelectedOrNull; if (active == null) { message = L.Tr("Select a layer first."); return; }
            if (active.IsGroup) { MergeGroupSelected(); return; }
            RunMerge(tolerance => document.MergeDown(active.Id, tolerance));
        }

        internal void MergeGroupSelected()
        {
            var active = SelectedOrNull; if (active == null) { message = L.Tr("Select a layer first."); return; }
            RunMerge(tolerance => document.MergeGroup(active.Id, tolerance));
        }

        /// <summary>表示レイヤーを結合: 見えている層を 1 枚にする（見えていない層は残す）。結合した層は選んでいる層の名前（それが結合されるなら）。</summary>
        internal void MergeVisibleLayers()
        {
            RunMerge(tolerance => document.MergeVisible(L.Tr("Merged"), SelectedOrNull?.Id, tolerance));
        }

        /// <summary>結合して、結果の層を選ぶ。丸め（1 段）を超えて見た目が変わるときは、変わる画素の数と最大の差を見せて確かめ、
        /// 断られたら何も変えない。</summary>
        void RunMerge(Func<int, LayerMergeReport> merge)
        {
            LayerMergeReport report; bool asked = false;
            try { report = merge(PaintDocument.MergeRoundingTolerance); }
            catch (LayerMergeException ex)
            {
                var r = ex.Report;
                string channels = string.Join(", ", r.ChangedByChannel.Keys.Select(c => L.Tr(c.ToString())));
                if (!Dialogs.Confirm(L.Tr("Merge Layers"),
                        L.Tr("Merging changes how the image looks: {0} pixels change by up to {1} levels ({2}). Photoshop changes the look in the same way, for example when the lower layer has another blend mode or a lower opacity, or when layers are clipped to the merged one. Undo brings the layers back.", r.ChangedPixels.ToString("N0"), r.MaxVisibleDifference, channels),
                        L.Tr("Merge Anyway"), L.Tr("Cancel")))
                { message = L.Tr("Not merged: the look would change."); return; }
                report = merge(255); asked = true;
            }
            selectedLayer = report.ResultId; editMask = false;
            string result = document.GetLayer(report.ResultId).Name;
            message = (report.Exact ? L.Tr("Merged into {0}. The image looks the same.", result)
                : asked ? L.Tr("Merged into {0}. {1} pixels changed by up to {2} levels.", result, report.ChangedPixels.ToString("N0"), report.MaxVisibleDifference)
                : L.Tr("Merged into {0}. {1} pixels differ by rounding (up to {2} levels).", result, report.ChangedPixels.ToString("N0"), report.MaxVisibleDifference)) + NotesText(report.Notes);
        }

        static string NotesText(MergeNotes notes)
        {
            string text = "";
            if ((notes & MergeNotes.EffectsBaked) != 0) text += " " + L.Tr("Filters and generators were baked into the pixels.");
            if ((notes & MergeNotes.PathsRasterized) != 0) text += " " + L.Tr("Paths became plain pixels.");
            if ((notes & MergeNotes.HiddenLayersDropped) != 0) text += " " + L.Tr("Hidden layers inside the group were dropped (Undo brings them back).");
            if ((notes & MergeNotes.DisabledChannelsDropped) != 0) text += " " + L.Tr("Pixels of switched-off channels were dropped.");
            return text;
        }

        // ───────── クリップボード ─────────

        /// <summary>今の層の今のチャンネル（マスクを編集中ならマスク）か、合成（merged）の、選択範囲の中（無ければ全体）を写す。</summary>
        internal void CopyPixels(bool merged)
        {
            PixelClipboard copied;
            if (merged) copied = document.CopyMerged(channel, PainterClipboard.LimitBytes);
            else
            {
                var active = SelectedOrNull; if (active == null) { message = L.Tr("Select a layer first."); return; }
                bool mask = editMask && active.Mask != null;
                copied = document.CopyPixels(active.Id, channel, mask, PainterClipboard.LimitBytes);
            }
            PainterClipboard.Content = copied;
            message = copied.Source == ClipboardSource.Composite ? L.Tr("Copied {0} × {1} pixels of the composite.", copied.Width, copied.Height)
                : copied.Source == ClipboardSource.Mask ? L.Tr("Copied {0} × {1} pixels of the mask.", copied.Width, copied.Height)
                : L.Tr("Copied {0} × {1} pixels.", copied.Width, copied.Height);
        }

        /// <summary>写してから消す（1 回の Undo）。マスクを編集中ならマスクを（消すと見えるようになる）。</summary>
        internal void CutPixels()
        {
            var active = SelectedOrNull; if (active == null) { message = L.Tr("Select a layer first."); return; }
            bool mask = editMask && active.Mask != null;
            var copied = document.CutPixels(active.Id, channel, mask, PainterClipboard.LimitBytes);
            PainterClipboard.Content = copied;
            message = L.Tr("Cut {0} × {1} pixels.", copied.Width, copied.Height);
        }

        /// <summary>クリップボードの画素を、選んでいる層の上の新しいペイントのレイヤーとして今のチャンネルに貼り、選ぶ（選択範囲は解除、
        /// 1 回の Undo）。同じ大きさの文書なら同じ位置、違えば中央（はみ出す分は切って知らせる）。そのまま V（移動ツール）で動かせる。</summary>
        internal void PasteClipboard()
        {
            var clip = PainterClipboard.Content;
            if (clip == null) { message = L.Tr("The clipboard is empty. Copy pixels first (Ctrl+C)."); return; }
            if (clip.ByteSize > PainterClipboard.LimitBytes) { message = L.Tr("The clipboard holds {0} MiB, more than the one-operation budget of {1} MiB. Nothing was pasted.", clip.ByteSize >> 20, PainterClipboard.LimitBytes >> 20); return; }
            var result = document.PasteAsLayer(clip, channel, L.Tr("Layer") + " " + (document.Layers.Count + 1), AboveSelected());
            selectedLayer = result.Layer.Id; editMask = false;
            message = result.Centered ? L.Tr("Pasted as {0}, centred (the copy came from a {1} × {2} canvas).", result.Layer.Name, clip.DocumentWidth, clip.DocumentHeight)
                : L.Tr("Pasted as {0}.", result.Layer.Name);
            if (result.ClippedPixels > 0) message += " " + L.Tr("{0} pixels outside the canvas were cut off.", result.ClippedPixels.ToString("N0"));
        }

        /// <summary>Core が断った理由の、窓の言葉。</summary>
        static string RefusalText(LayerOpException ex)
        {
            switch (ex.Reason)
            {
                case LayerOpRefusal.NoLayerBelow: return L.Tr("There is no layer below in the same group to merge into.");
                case LayerOpRefusal.LayerBelowIsGroup: return L.Tr("The layer below is a group. Merge the group first (Ctrl+E with the group selected).");
                case LayerOpRefusal.LayerBelowIsAdjustment: return L.Tr("The layer below is an adjustment layer, which has no pixels to merge into.");
                case LayerOpRefusal.HiddenLayer: return L.Tr("Hidden layers are not merged. Show both layers first.");
                case LayerOpRefusal.IsGroup: case LayerOpRefusal.NotGroup: return L.Tr("Select a group to merge it.");
                case LayerOpRefusal.EmptyGroup: return L.Tr("The group is empty: there is nothing to merge.");
                case LayerOpRefusal.NothingVisible: return L.Tr("No layer shows anything: there is nothing to merge.");
                case LayerOpRefusal.NoPixels: return L.Tr("This layer has no pixels to copy. Copy Merged (Ctrl+Shift+C) copies what shows.");
                case LayerOpRefusal.NothingToCopy: return L.Tr("There is nothing to copy here.");
                case LayerOpRefusal.ClipboardTooLarge: return L.Tr("The copy needs {0} MiB, more than the clipboard limit of {1} MiB (the one-operation budget in Project Settings ▸ YoluPainter). Nothing was copied.", (ex.Bytes + (1 << 20) - 1) >> 20, ex.Limit >> 20);
                case LayerOpRefusal.OperationBudget: return L.Tr("The result needs more than the one-operation budget of {0} MiB (Project Settings ▸ YoluPainter). Nothing was changed.", ex.Limit >> 20);
                case LayerOpRefusal.PathLayer: return L.Tr("This layer is drawn by a path. Rasterize it to cut from it.");
                case LayerOpRefusal.NotPaintLayer: return L.Tr("A fill layer cannot be cut. Copy it, or paint on its mask.");
                default: return L.Tr(ex.Message);
            }
        }

        // ───────── メニュー ─────────

        /// <summary>編集メニューのクリップボードの項目。</summary>
        void ClipboardMenuItems(UnityEditor.GenericMenu m)
        {
            var active = SelectedOrNull; bool idle = stroke == null && !toolDragging;
            bool mask = editMask && active?.Mask != null;
            bool copyable = active != null && (mask || active.Kind == LayerKind.Raster || active.Kind == LayerKind.Fill);
            bool cuttable = active != null && (mask || active.Kind == LayerKind.Raster && active.Path == null);
            Item(m, "Cut", () => RunLayerCommand(LayerCommand.Cut), idle && cuttable, keys: "Ctrl+X");
            Item(m, "Copy", () => RunLayerCommand(LayerCommand.Copy), idle && copyable, keys: "Ctrl+C");
            Item(m, "Copy Merged", () => RunLayerCommand(LayerCommand.CopyMerged), idle, keys: "Ctrl+Shift+C");
            Item(m, "Paste", () => RunLayerCommand(LayerCommand.Paste), idle && PainterClipboard.Content != null, keys: "Ctrl+V");
        }

        /// <summary>レイヤーメニュー（と右クリック）の複製・結合の項目。結合できない理由があれば使えない項目にする。</summary>
        void LayerOpMenuItems(UnityEditor.GenericMenu m)
        {
            var active = SelectedOrNull; bool idle = stroke == null && !toolDragging;
            Item(m, "Duplicate Layer", () => RunLayerCommand(LayerCommand.Duplicate), idle && active != null, keys: "Ctrl+J");
            if (active != null && active.IsGroup) Item(m, "Merge Group", () => RunLayerCommand(LayerCommand.MergeDown), idle && document.MergeGroupRefusal(active.Id) == null, keys: "Ctrl+E");
            else Item(m, "Merge Down", () => RunLayerCommand(LayerCommand.MergeDown), idle && active != null && document.MergeDownRefusal(active.Id) == null, keys: "Ctrl+E");
            Item(m, "Merge Visible", () => RunLayerCommand(LayerCommand.MergeVisible), idle && document.MergeVisibleRefusal() == null, keys: "Ctrl+Shift+E");
        }
    }
}
