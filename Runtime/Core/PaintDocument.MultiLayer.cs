using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Operations on several layers at once (a multiple selection in the Layers panel, like Photoshop's and CLIP STUDIO's). Each is
    /// one undo step and changes nothing when it is refused or fails. A layer inside a group that is also chosen goes with the group
    /// (<see cref="TopmostOf"/>). Merging several layers is <see cref="MergeLayers"/> (PaintDocument.Merge.cs); moving them together is
    /// <see cref="TransformLayers"/> (PaintDocument.Transform.cs); grouping them is <see cref="GroupLayers"/>.</summary>
    public sealed partial class PaintDocument
    {
        /// <summary>The layers an operation on several layers works on: each chosen layer once, without those inside a chosen group (they
        /// go with it), bottom to top. Unknown IDs throw.</summary>
        public IReadOnlyList<PaintLayer> TopmostOf(IEnumerable<Guid> ids)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            var chosen = new HashSet<PaintLayer>(); foreach (var id in ids) chosen.Add(GetLayer(id));
            var result = new List<PaintLayer>();
            foreach (var l in layers)
            {
                if (!chosen.Contains(l)) continue;
                bool inside = false;
                foreach (var c in chosen) if (c.IsGroup && c != l && IsDescendant(l, c)) { inside = true; break; }
                if (!inside) result.Add(l);
            }
            return result.AsReadOnly();
        }

        /// <summary>Removes several layers (a group with everything in it), as one undo step.</summary>
        public void RemoveLayers(IEnumerable<Guid> ids)
        {
            EnsureNoStroke(); var members = TopmostOf(ids);
            if (members.Count == 0) return;
            var removed = new HashSet<PaintLayer>(); long bytes = 0;
            foreach (var m in members) foreach (var l in Block(m)) if (removed.Add(l)) bytes += l.AllocatedBytes;
            var before = SnapshotStructure();
            layers.RemoveAll(removed.Contains);
            var after = SnapshotStructure(); RestoreStructure(before);
            Execute(SwapStructure(before, after, 128 + bytes));
        }

        /// <summary>Duplicates several layers (<see cref="DuplicateLayer"/>: each copy directly above its original), as one undo step. name
        /// gives each copy's name from the original's (null keeps it). The copies count against <see cref="SourceBudgetBytes"/> together:
        /// when one does not fit, none is kept. Returns the copies, bottom to top.</summary>
        public IReadOnlyList<PaintLayer> DuplicateLayers(IEnumerable<Guid> ids, Func<string, string> name = null)
        {
            EnsureNoStroke(); var members = TopmostOf(ids);
            var copies = new List<PaintLayer>();
            if (members.Count == 0) return copies.AsReadOnly();
            Batch(() => { foreach (var m in members) copies.Add(DuplicateLayer(m.Id, name?.Invoke(m.Name))); });
            return copies.AsReadOnly();
        }

        /// <summary>Shows or hides several layers (every chosen one, groups and layers inside them alike), as one undo step.</summary>
        public void SetLayersVisibility(IEnumerable<Guid> ids, bool visible)
        {
            EnsureNoStroke(); if (ids == null) throw new ArgumentNullException(nameof(ids));
            var targets = new List<PaintLayer>(); foreach (var id in ids) { var l = GetLayer(id); if (l.Visible != visible && !targets.Contains(l)) targets.Add(l); }
            if (targets.Count == 0) return;
            Batch(() => { foreach (var l in targets) SetLayerVisibility(l.Id, visible); });
        }

        /// <summary>Moves several layers (groups with their contents) into a group (Guid.Empty: the top level) at a position among that
        /// group's other children (0 = bottom; the chosen layers do not count), keeping their order, as one undo step. Layers from different
        /// groups end up next to each other. A group cannot be moved into itself or into a chosen group.</summary>
        public void MoveLayers(IEnumerable<Guid> ids, Guid parentId, int position)
        {
            EnsureNoStroke(); var members = TopmostOf(ids);
            if (members.Count == 0) return;
            if (parentId != Guid.Empty)
            {
                var parent = GetLayer(parentId);
                if (!parent.IsGroup) throw new ArgumentException("Layers can only be moved into groups.", nameof(parentId));
                foreach (var m in members)
                    if (m == parent || IsDescendant(parent, m)) throw new InvalidOperationException("A group cannot be moved into itself.");
            }
            var blocks = new List<PaintLayer>(); foreach (var m in members) blocks.AddRange(Block(m));
            var moving = new HashSet<PaintLayer>(blocks);
            int others = 0; foreach (var l in layers) if (l.ParentId == parentId && !moving.Contains(l)) others++;
            if (position < 0 || position > others) throw new ArgumentOutOfRangeException(nameof(position));
            var before = SnapshotStructure();
            layers.RemoveAll(moving.Contains);
            var siblings = new List<PaintLayer>(); foreach (var l in layers) if (l.ParentId == parentId) siblings.Add(l);
            int insertAt = position < siblings.Count ? SubtreeStart(layers.IndexOf(siblings[position]))
                : parentId == Guid.Empty ? layers.Count : layers.IndexOf(GetLayer(parentId)); // グループの記録のすぐ下 = 子の一番上
            foreach (var m in members) m.ParentId = parentId;
            layers.InsertRange(insertAt, blocks);
            var after = SnapshotStructure(); RestoreStructure(before);
            if (SameStructure(before, after)) return;
            Execute(StructureCommand(before, after, 64, blocks));
        }

        /// <summary>Moves each chosen layer one step up (or down) among the siblings of its group, as one undo step (Photoshop's Ctrl+] /
        /// Ctrl+[ with several layers). A layer at the top (bottom) of its group stays, and so does one held back by a chosen layer that
        /// stays. Returns false when nothing moved.</summary>
        public bool StepLayers(IEnumerable<Guid> ids, bool up)
        {
            EnsureNoStroke(); var members = new List<PaintLayer>(TopmostOf(ids));
            if (members.Count == 0) return false;
            var chosen = new HashSet<PaintLayer>(members);
            var before = SnapshotStructure(); var moved = new List<PaintLayer>();
            if (up) members.Reverse(); // 上へは上から、下へは下から
            foreach (var m in members)
            {
                var siblings = new List<PaintLayer>(); foreach (var l in layers) if (l.ParentId == m.ParentId) siblings.Add(l);
                int at = siblings.IndexOf(m), to = at + (up ? 1 : -1);
                if (to < 0 || to >= siblings.Count || chosen.Contains(siblings[to])) continue;
                MoveSubtreeInternal(m, m.ParentId, to);
                moved.AddRange(Block(m));
            }
            var after = SnapshotStructure(); RestoreStructure(before);
            if (SameStructure(before, after)) return false;
            Execute(StructureCommand(before, after, 64, moved));
            return true;
        }
    }
}
