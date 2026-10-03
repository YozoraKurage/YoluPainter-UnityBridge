using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>レイヤーの複数選択（Photoshop・CLIP STUDIO と同じく Ctrl（Mac は Cmd）クリックで足し引き、Shift クリックで範囲）と、複数の層に
    /// 効く操作（削除・グループ化・複製・結合・表示・並べ替え・移動・ロック）。選択は窓の状態で保存しない。描く先（selectedLayer）は
    /// 1 つのままで、複数選択はそれを含む集合。selectedLayer をほかの所（新しい層・結合の結果・テクスチャセットの切り替えなど）が
    /// 変えると、集合はその 1 つに戻る。</summary>
    public sealed partial class TexturePaintWindow
    {
        readonly HashSet<Guid> layerSelection = new HashSet<Guid>();
        Guid layerSelectionActive, layerSelectionAnchor; PaintDocument layerSelectionDocument;
        /// <summary>選んだ行を修飾キー無しで押したとき: ドラッグにならなければ、離したところでその 1 つだけの選択にする。</summary>
        Guid layerCollapsePending;

        bool MultiSelectionValid => document != null && layerSelectionDocument == document && layerSelectionActive == selectedLayer && layerSelection.Count > 1;

        /// <summary>選んでいる層（描く先を含む）。下から上の順。描く先が無ければ空。</summary>
        internal IReadOnlyList<Guid> SelectedLayers
        {
            get
            {
                var result = new List<Guid>(); if (document == null) return result;
                bool multi = MultiSelectionValid;
                foreach (var l in document.Layers) if (l.Id == selectedLayer || multi && layerSelection.Contains(l.Id)) result.Add(l.Id);
                return result;
            }
        }
        internal bool IsLayerSelected(Guid id) => id == selectedLayer || MultiSelectionValid && layerSelection.Contains(id);
        /// <summary>複数選択の目印（別のウィンドウのパネルを描き直す判断に使う）。</summary>
        int LayerSelectionStamp { get { if (!MultiSelectionValid) return 0; int h = 17; foreach (var id in layerSelection.OrderBy(g => g)) h = unchecked(h * 31 + id.GetHashCode()); return h; } }

        /// <summary>選ぶ層をまとめて決める（active が描く先。ids に無ければ足す）。</summary>
        internal void SelectLayers(IEnumerable<Guid> ids, Guid active)
        {
            layerSelection.Clear(); foreach (var id in ids) if (document.Layers.Any(l => l.Id == id)) layerSelection.Add(id);
            layerSelection.Add(active);
            if (selectedLayer != active && document.Layers.FirstOrDefault(l => l.Id == active)?.Mask == null) editMask = false;
            selectedLayer = active; layerSelectionActive = active; layerSelectionDocument = document;
        }
        /// <summary>複数選択を描く先の 1 つに戻す（テクスチャセットの切り替えなど）。</summary>
        internal void ClearLayerSelection() { layerSelection.Clear(); layerSelectionDocument = null; layerCollapsePending = Guid.Empty; }

        /// <summary>Ctrl（Cmd）クリック: 選んでいなければ足して描く先に、選んでいれば外す（最後の 1 つは外さない。描く先を外したら、残りの
        /// いちばん上が描く先）。</summary>
        internal void ToggleLayerSelected(Guid id)
        {
            layerSelectionAnchor = id; // 次の Shift クリックの起点（外せなかった最後の 1 つでも）
            var current = SelectedLayers.ToList();
            if (current.Contains(id))
            {
                if (current.Count == 1) return;
                current.Remove(id);
                SelectLayers(current, id == selectedLayer ? current[current.Count - 1] : selectedLayer);
            }
            else { current.Add(id); SelectLayers(current, id); }
        }
        /// <summary>Shift クリック: 起点（最後に修飾キー無しか Ctrl で押した層）から押した層までのパネルの行を選ぶ。add（Ctrl+Shift）なら今の選択に足す。</summary>
        internal void SelectLayerRange(Guid id, bool add)
        {
            var rows = document.Layers.Reverse().Select(l => l.Id).ToList(); // パネルの行の順（上から）
            Guid anchor = rows.Contains(layerSelectionAnchor) ? layerSelectionAnchor : selectedLayer;
            int a = rows.IndexOf(anchor), b = rows.IndexOf(id); if (b < 0) return; if (a < 0) a = b;
            var range = rows.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1);
            SelectLayers(add ? SelectedLayers.Concat(range) : range, id);
            layerSelectionAnchor = anchor;
        }
        /// <summary>修飾キー無しのクリック: その層だけを選ぶ。</summary>
        void SelectSingleLayer(Guid id) { ClearLayerSelection(); selectedLayer = id; layerSelectionAnchor = id; }

        // ───────── 複数の層への操作 ─────────

        /// <summary>選んでいる層を消す（1 回の Undo）。全部の層は消さない。</summary>
        void DeleteSelectedLayer()
        {
            var ids = SelectedLayers; if (ids.Count == 0) return;
            if (ids.Count == 1) { if (document.Layers.Count < 2) return; document.RemoveLayer(ids[0]); }
            else
            {
                var members = document.TopmostOf(ids);
                int removed = document.Layers.Count(l => members.Any(m => m == l || IsInside(l, m)));
                if (removed >= document.Layers.Count) { message = L.Tr("At least one layer must remain. Keep one layer out of the selection."); return; }
                document.RemoveLayers(ids);
                message = L.Tr("Deleted {0} layers.", members.Count);
            }
            ClearLayerSelection();
            selectedLayer = document.Layers.Count > 0 ? document.Layers[document.Layers.Count - 1].Id : Guid.Empty;
        }
        bool IsInside(PaintLayer layer, PaintLayer group)
        {
            if (!group.IsGroup) return false;
            for (var id = layer.ParentId; id != Guid.Empty; id = document.GetLayer(id).ParentId) if (id == group.Id) return true;
            return false;
        }

        /// <summary>選んでいる層をグループにする（Ctrl+G、1 回の Undo）。違うグループの層どうしは断る。</summary>
        internal void GroupSelectedLayers()
        {
            var members = document.TopmostOf(SelectedLayers);
            if (members.Count == 0) { message = L.Tr("Select a layer first."); return; }
            if (members.Any(m => m.ParentId != members[0].ParentId)) { message = L.Tr("Only layers in the same group can be grouped together. Move them into one group first."); return; }
            var group = document.GroupLayers(members.Select(m => m.Id).ToList(), L.Tr("Group") + " " + (document.Layers.Count(l => l.IsGroup) + 1));
            SelectSingleLayer(group.Id);
            if (members.Count > 1) message = L.Tr("Grouped {0} layers.", members.Count);
        }

        /// <summary>選んでいるグループを解除する（Ctrl+Shift+G）。</summary>
        internal void UngroupSelectedLayer()
        {
            var active = SelectedOrNull;
            if (active == null || !active.IsGroup) { message = L.Tr("Select a group to ungroup it."); return; }
            UngroupSelected();
        }

        /// <summary>選んでいる層を兄弟の中で 1 つ上（下）へ（Ctrl+] / Ctrl+[、1 回の Undo）。</summary>
        void MoveSelectedLayer(int delta)
        {
            var ids = SelectedLayers; if (ids.Count == 0) return;
            if (ids.Count == 1)
            {
                var active = document.GetLayer(ids[0]);
                var siblings = document.ChildrenOf(active.ParentId).ToList(); int at = siblings.IndexOf(active) + delta;
                if (at < 0 || at >= siblings.Count) return;
                document.MoveLayer(active.Id, at);
            }
            else document.StepLayers(ids, delta > 0);
        }

        /// <summary>選んでいる層の表示を切り替える（Ctrl+,、1 回の Undo）: どれか見えていれば全部隠し、全部隠れていれば全部見せる（Photoshop の
        /// 「レイヤーを非表示」「レイヤーを表示」）。</summary>
        internal void ToggleSelectedVisibility()
        {
            var ids = SelectedLayers; if (ids.Count == 0) return;
            bool show = ids.All(id => !document.GetLayer(id).Visible);
            document.SetLayersVisibility(ids, show);
            message = show ? L.Tr("Showed {0} layers.", ids.Count) : L.Tr("Hid {0} layers.", ids.Count);
        }

        /// <summary>選んでいる層の下のレイヤーでのクリッピングを切り替える（Ctrl+Alt+G）。</summary>
        internal void ToggleSelectedClipping()
        {
            var active = SelectedOrNull; if (active == null) { message = L.Tr("Select a layer first."); return; }
            document.SetLayerClipping(active.Id, !active.Clipping);
        }

        /// <summary>ドラッグで落とした所へ層を移す（選んだ層をまとめて。1 回の Undo）。</summary>
        internal void DropLayers(IReadOnlyList<Guid> ids, int gap, PaintLayer into)
        {
            if (ids.Count == 1) { DropLayer(ids[0], gap, into); return; }
            var moving = document.TopmostOf(ids);
            var carried = new HashSet<PaintLayer>(document.Layers.Where(l => moving.Any(m => m == l || IsInside(l, m))));
            if (into != null)
            {
                if (carried.Contains(into)) return; // 選んだグループ（やその中）へは落とせない
                document.MoveLayers(ids, into.Id, document.ChildrenOf(into.Id).Count(l => !carried.Contains(l)));
                message = L.Tr("Moved into {0}.", into.Name); return;
            }
            int n = document.Layers.Count;
            for (int row = Math.Max(0, gap); row < n; row++) // 落とした線のすぐ下から、動かさない最初の層の上に置く
            {
                var below = document.Layers[n - 1 - row];
                if (carried.Contains(below)) continue;
                var siblings = document.ChildrenOf(below.ParentId).Where(l => !carried.Contains(l)).ToList();
                document.MoveLayers(ids, below.ParentId, siblings.IndexOf(below) + 1); return;
            }
            document.MoveLayers(ids, Guid.Empty, 0); // 一番下
        }

        // ───────── ロック ─────────

        internal static readonly (LayerLocks flag, string icon, string tip)[] LockButtons =
        {
            (LayerLocks.Transparency, "lock_transparency", "Lock transparent pixels: painting keeps each pixel's transparency and changes only its colour"),
            (LayerLocks.Pixels, "paint_brush", "Lock image pixels: the layer's pixels cannot be changed (it can still be moved and its mask painted)"),
            (LayerLocks.Position, "arrow_move", "Lock position: the layer cannot be moved or transformed"),
            (LayerLocks.All, "lock", "Lock all: pixels, position and the layer's settings"),
        };
        internal const float LockRowHeight = 22, LockButtonSize = 21;
        const float LockButtonsWidth = 4 * LockButtonSize + 3 * 2;

        /// <summary>レイヤーのパネルのロックの 4 つの切り替え（選んでいる層の全部に効く）: 既定の幅では合成モードと不透明度の行の右に、狭いときは
        /// 「ロック」の名前を付けて 2 行目に。持っているロックは押し込まれた見た目、グループやすべてのロックから効いているだけのものは枠で見せる。</summary>
        void DrawLockRow(Rect r, bool withLabel)
        {
            var ids = SelectedLayers; bool any = ids.Count > 0;
            float x = r.x, size = LockButtonSize;
            if (withLabel)
            {
                string label = L.Tr("Lock");
                float labelWidth = Mathf.Min(r.width * .35f, PaintTheme.Label.CalcSize(new GUIContent(label)).x + 6);
                PaintGui.Text(new Rect(r.x, r.y, labelWidth, r.height), label, PaintTheme.LabelDim);
                x += labelWidth;
            }
            foreach (var (flag, icon, tip) in LockButtons)
            {
                var b = PanelSpot("lock." + flag, new Rect(x, r.y + (r.height - size) / 2, size, size)); x += size + 2;
                bool own = any && ids.All(id => (document.GetLayer(id).Locks & flag) != 0);
                bool effective = any && ids.All(id => (document.EffectiveLocks(id) & flag) != 0);
                string tooltip = L.Tr(tip) + (effective && !own ? " — " + L.Tr("in effect through Lock all or a locked group") : "");
                if (PaintGui.IconButton(b, icon, tooltip, own, any, 15)) TryAction(() => ToggleLock(flag, !own));
                if (effective && !own) PaintGui.Outline(b, PaintTheme.AccentDim, 1, 4);
            }
        }

        /// <summary>選んでいる層のロックを付ける・外す（1 回の Undo）。</summary>
        internal void ToggleLock(LayerLocks flag, bool on)
        {
            var ids = SelectedLayers; if (ids.Count == 0) return;
            document.ChangeLayerLocks(ids, flag, on);
            message = on ? L.Tr("Locked: {0}.", LockName(flag)) : L.Tr("Unlocked: {0}.", LockName(flag));
        }
        static string LockName(LayerLocks flag)
        {
            switch (flag)
            {
                case LayerLocks.Transparency: return L.Tr("transparent pixels");
                case LayerLocks.Pixels: return L.Tr("image pixels");
                case LayerLocks.Position: return L.Tr("position");
                default: return L.Tr("all");
            }
        }

        /// <summary>行の右端のロックの印: すべてのロックは塗った錠、ほかの自分のロックは線の錠、グループから効いているだけなら薄い線の錠。</summary>
        bool DrawRowLock(Rect r, PaintLayer layer)
        {
            var effective = document.EffectiveLocks(layer.Id);
            if (effective == LayerLocks.None) return false;
            bool full = (layer.Locks & LayerLocks.All) != 0, own = layer.Locks != LayerLocks.None;
            PaintGui.Icon(r, full ? "lock_filled" : "lock", own ? PaintTheme.TextDim : PaintTheme.TextDisabled, 13);
            var names = LockButtons.Where(b => b.flag != LayerLocks.All && (effective & b.flag) != 0).Select(b => LockName(b.flag));
            PaintGui.Tooltip(r, (full ? L.Tr("Locked") : L.Tr("Locked: {0}", string.Join(", ", names))) + (own ? "" : " (" + L.Tr("by its group") + ")"));
            return true;
        }

        /// <summary>レイヤーメニューのロックの項目（選んでいる層の全部に効く）。</summary>
        void LockMenuItems(PaintMenu m)
        {
            var ids = SelectedLayers; bool idle = stroke == null && !toolDragging;
            foreach (var (flag, _, _) in LockButtons)
            {
                var f = flag; bool own = ids.Count > 0 && ids.All(id => (document.GetLayer(id).Locks & f) != 0);
                string text = f == LayerLocks.Transparency ? "Lock Transparent Pixels" : f == LayerLocks.Pixels ? "Lock Image Pixels" : f == LayerLocks.Position ? "Lock Position" : "Lock All";
                m.AddItem(new GUIContent(L.Tr("Lock Layers") + "/" + L.Tr(text)), own, () => { if (idle) TryAction(() => ToggleLock(f, !own)); Repaint(); });
            }
        }
    }
}
