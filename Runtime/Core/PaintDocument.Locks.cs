using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Layer locks, as in Photoshop's Layers panel. Saved with the document (native version 12) and read from / written to
    /// PSD (lspf). A group's locks apply to everything inside it (<see cref="PaintDocument.EffectiveLocks(Guid)"/>). Values are
    /// stored, so they are never renumbered.</summary>
    [Flags]
    public enum LayerLocks
    {
        None = 0,
        /// <summary>Lock transparent pixels: an edit of the layer's pixels (brush in 2D and 3D, fill, gradient, image replacement,
        /// baking filters) keeps every pixel's alpha and changes only its colour; a fully transparent pixel does not change at all
        /// (its stored RGB included). Erasing and cutting (which only remove alpha) are refused, and so are moving or transforming
        /// only the selected part and drawing the layer from a path. Moving or transforming the whole layer is a change of position
        /// and stays allowed (Photoshop).</summary>
        Transparency = 1,
        /// <summary>Lock image pixels: every edit of the layer's pixels (or of a fill layer's value) is refused, as is merging into or
        /// from the layer. Moving the whole layer by whole pixels (which copies every pixel as it is) stays allowed, and so does
        /// painting its mask (Photoshop).</summary>
        Pixels = 2,
        /// <summary>Lock position: moving and transforming are refused.</summary>
        Position = 4,
        /// <summary>Lock all: the three above and the layer's attributes (opacity, blend mode, clipping, channels, mask, filters and
        /// generators, fill value, adjustment, path). The name, visibility, place in the stack, grouping, duplicating and deleting
        /// stay allowed.</summary>
        All = 8,
    }

    /// <summary>A layer operation refused by a lock (<see cref="LayerOpRefusal.Locked"/>). Nothing was changed.</summary>
    public sealed class LayerLockedException : LayerOpException
    {
        /// <summary>The layer the operation was for.</summary>
        public Guid LayerId { get; }
        /// <summary>The layer whose lock refused it: the layer itself, or a group it is in.</summary>
        public Guid LockedBy { get; }
        public string LockedByName { get; }
        /// <summary>The lock that refused it: Transparency, Pixels, Position or All.</summary>
        public LayerLocks Lock { get; }
        internal LayerLockedException(PaintLayer layer, PaintLayer holder, LayerLocks lockFlag, string message)
            : base(LayerOpRefusal.Locked, message)
        { LayerId = layer.Id; LockedBy = holder.Id; LockedByName = holder.Name; Lock = lockFlag; }
    }

    public sealed partial class PaintLayer
    {
        /// <summary>The layer's own locks. A group's locks also apply to its contents (<see cref="PaintDocument.EffectiveLocks(Guid)"/>).</summary>
        public LayerLocks Locks { get; internal set; }
    }

    /// <summary>Layer locks: setting them (one undo step) and the checks the editing operations make before they change anything.</summary>
    public sealed partial class PaintDocument
    {
        /// <summary>Every lock flag this version knows. A saved document with any other bit is refused.</summary>
        public const LayerLocks KnownLocks = LayerLocks.Transparency | LayerLocks.Pixels | LayerLocks.Position | LayerLocks.All;

        /// <summary>The locks in force on a layer: its own and those of every group it is in, with All also giving Transparency, Pixels and
        /// Position.</summary>
        public LayerLocks EffectiveLocks(Guid id) => EffectiveLocks(GetLayer(id));
        internal LayerLocks EffectiveLocks(PaintLayer layer)
        {
            var locks = LayerLocks.None;
            for (var l = layer; ;)
            {
                locks |= l.Locks;
                if (l.ParentId == Guid.Empty) break;
                var parent = layers.Find(x => x.Id == l.ParentId); if (parent == null) break;
                l = parent;
            }
            if ((locks & LayerLocks.All) != 0) locks |= LayerLocks.Transparency | LayerLocks.Pixels | LayerLocks.Position;
            return locks;
        }

        /// <summary>Sets a layer's own locks, as one undo step.</summary>
        public void SetLayerLocks(Guid id, LayerLocks locks)
        {
            EnsureNoStroke(); RequireKnownLocks(locks); var layer = GetLayer(id);
            var old = layer.Locks; if (old == locks) return;
            Execute(new DelegateCommand(() => layer.Locks = locks, () => layer.Locks = old, 64));
        }

        /// <summary>Turns lock flags on or off on several layers (the other flags of each stay), as one undo step.</summary>
        public void ChangeLayerLocks(IEnumerable<Guid> ids, LayerLocks flags, bool on)
        {
            EnsureNoStroke(); if (ids == null) throw new ArgumentNullException(nameof(ids)); RequireKnownLocks(flags);
            var targets = new List<PaintLayer>(); foreach (var id in ids) { var l = GetLayer(id); if (!targets.Contains(l)) targets.Add(l); }
            var before = new LayerLocks[targets.Count]; var after = new LayerLocks[targets.Count]; bool changes = false;
            for (int i = 0; i < targets.Count; i++)
            {
                before[i] = targets[i].Locks; after[i] = on ? before[i] | flags : before[i] & ~flags;
                changes |= after[i] != before[i];
            }
            if (!changes) return;
            Execute(new DelegateCommand(() => { for (int i = 0; i < targets.Count; i++) targets[i].Locks = after[i]; },
                () => { for (int i = 0; i < targets.Count; i++) targets[i].Locks = before[i]; }, 64 + 16L * targets.Count));
        }

        static void RequireKnownLocks(LayerLocks locks)
        { if ((locks & ~KnownLocks) != 0) throw new ArgumentOutOfRangeException(nameof(locks), "Unknown layer lock flags: " + (int)locks + "."); }

        /// <summary>For loaders (native, PSD, resampling): sets a layer's own locks without history and without checks. Call it after
        /// everything else of the document is loaded (the locks would refuse the loader's own edits).</summary>
        internal void SetLocksForLoad(PaintLayer layer, LayerLocks locks) { RequireKnownLocks(locks); layer.Locks = locks; }

        // ───────────── checks ─────────────

        /// <summary>Throws <see cref="LayerLockedException"/> when the layer's pixels cannot be edited now: its image pixels or everything
        /// are locked (on the layer or a group it is in), or the edit removes alpha (erase, cut) and its transparent pixels are locked.
        /// The paint window calls it before switching a channel on for a stroke, so a refused stroke leaves nothing behind.</summary>
        public void EnsurePixelsEditable(Guid layerId, bool erase = false) => RefuseLockedPixels(GetLayer(layerId), erase);

        internal void RefuseLockedPixels(PaintLayer layer, bool erase)
        {
            var locks = EffectiveLocks(layer);
            if ((locks & LayerLocks.All) != 0) throw Locked(layer, LayerLocks.All);
            if ((locks & LayerLocks.Pixels) != 0) throw Locked(layer, LayerLocks.Pixels);
            if (erase && (locks & LayerLocks.Transparency) != 0) throw Locked(layer, LayerLocks.Transparency);
        }
        /// <summary>Edits that keep the alpha of every pixel (or would make no sense with it kept, like a path that redraws the layer)
        /// are refused with this when the transparent pixels are locked.</summary>
        internal void RefuseLockedTransparency(PaintLayer layer)
        { if ((EffectiveLocks(layer) & LayerLocks.Transparency) != 0) throw Locked(layer, (EffectiveLocks(layer) & LayerLocks.All) != 0 ? LayerLocks.All : LayerLocks.Transparency); }
        /// <summary>Attributes (opacity, blend mode, clipping, channels, mask, filters, fill value, adjustment, path) under Lock All.</summary>
        internal void RefuseLockedAttributes(PaintLayer layer)
        { if ((EffectiveLocks(layer) & LayerLocks.All) != 0) throw Locked(layer, LayerLocks.All); }
        /// <summary>Moving or transforming the layer's pixels: refused under Lock Position and Lock All; under Lock Image Pixels only a
        /// move of the whole layer by whole pixels is allowed; under Lock Transparent Pixels only the whole layer moves.</summary>
        internal void RefuseLockedTransform(PaintLayer layer, Affine2D transform, SelectionMask region)
        {
            var locks = EffectiveLocks(layer);
            if ((locks & LayerLocks.All) != 0) throw Locked(layer, LayerLocks.All);
            if ((locks & LayerLocks.Position) != 0) throw Locked(layer, LayerLocks.Position);
            bool wholePixelMove = transform.A == 1 && transform.B == 0 && transform.C == 0 && transform.D == 1 && transform.Tx == Math.Round(transform.Tx) && transform.Ty == Math.Round(transform.Ty);
            if ((locks & LayerLocks.Pixels) != 0 && (region != null || !wholePixelMove)) throw Locked(layer, LayerLocks.Pixels);
            if ((locks & LayerLocks.Transparency) != 0 && region != null) throw Locked(layer, LayerLocks.Transparency);
        }
        /// <summary>True when edits of the layer's pixels keep each pixel's alpha (Lock Transparent Pixels on it or a group it is in).</summary>
        internal bool KeepsAlpha(PaintLayer layer) => (EffectiveLocks(layer) & LayerLocks.Transparency) != 0;

        /// <summary>The refusal, naming the layer (or group) that holds the lock.</summary>
        LayerLockedException Locked(PaintLayer layer, LayerLocks lockFlag)
        {
            var holder = layer;
            for (var l = layer; l != null; l = l.ParentId == Guid.Empty ? null : layers.Find(x => x.Id == l.ParentId))
                if ((l.Locks & (lockFlag | LayerLocks.All)) != 0) { holder = l; break; }
            string who = holder == layer ? "'" + layer.Name + "'" : "'" + layer.Name + "' (in the locked group '" + holder.Name + "')";
            string what;
            switch (lockFlag)
            {
                case LayerLocks.Transparency: what = " has its transparent pixels locked: this would change the alpha of its pixels. Unlock transparent pixels to erase, cut, draw a path or move a selected part."; break;
                case LayerLocks.Pixels: what = " has its image pixels locked. Unlock it to change its pixels."; break;
                case LayerLocks.Position: what = " has its position locked. Unlock it to move or transform it."; break;
                default: what = " is locked. Unlock it to change it."; break;
            }
            return new LayerLockedException(layer, holder, lockFlag, who + what + " Nothing was changed.");
        }

        // ───────────── pixel rules ─────────────

        /// <summary>Paint laid over a pixel whose alpha is locked: the colour moves towards the paint's by amount × the paint's alpha
        /// (source-atop, in straight colour), the alpha stays; a fully transparent pixel does not change at all (its stored RGB is kept).</summary>
        internal static Rgba32 PaintKeepingAlpha(Rgba32 start, Rgba32 paint, double amount)
        {
            if (start.A == 0) return start;
            double a = amount * paint.A / 255.0;
            if (!(a > 0)) return start;
            if (a > 1) a = 1;
            return new Rgba32(Mix(start.R, paint.R, a), Mix(start.G, paint.G, a), Mix(start.B, paint.B, a), start.A);
        }
        /// <summary>A replacement (an image, a filter's output) of a pixel whose alpha is locked: the replacement's colour by amount where
        /// it has any alpha, with the pixel's own alpha. A transparent pixel, or a transparent replacement (no colour), leaves it as it is.</summary>
        internal static Rgba32 ReplaceKeepingAlpha(Rgba32 start, Rgba32 image, double amount)
            => image.A == 0 ? start : PaintKeepingAlpha(start, new Rgba32(image.R, image.G, image.B, 255), amount);
        static byte Mix(byte from, byte to, double t) => MathUtil.ToByte(MathUtil.ByteUnit[from] + (MathUtil.ByteUnit[to] - MathUtil.ByteUnit[from]) * t);
    }
}
