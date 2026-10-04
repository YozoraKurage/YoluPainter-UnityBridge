using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Where an anchor point is placed. Values are stored in the native format; append only.</summary>
    public enum AnchorPlacement
    {
        /// <summary>On a layer: the layer stack's result through that layer, in every channel (see <see cref="AnchorPoint"/>).</summary>
        Layer = 0,
        /// <summary>On a layer's mask: how much the mask lets its layer show (after its filters, on/off, invert and density).</summary>
        Mask = 1,
    }

    /// <summary>What an anchor generator (<see cref="GeneratorType.Anchor"/>) reads from a layer anchor's channel. A mask anchor always gives
    /// how much its layer shows. Values are stored in the native format; append only.</summary>
    public enum AnchorRead
    {
        /// <summary>The channel's value over black: value × coverage (the value of a scalar channel; the luminance 0.3 R + 0.59 G + 0.11 B of
        /// a colour channel), the way exported scalar maps pack it.</summary>
        Value = 0,
        /// <summary>The coverage (alpha) of the stack there: 1 where something opaque is painted, 0 where nothing is.</summary>
        Coverage = 1,
    }

    /// <summary>
    /// A named connection point in the layer stack (Substance Painter's anchor point). Generators of layers above it can read what it holds
    /// (<see cref="GeneratorType.Anchor"/>), so a lower layer's painted Height can drive an upper layer's mask, for example.
    /// <list type="bullet">
    /// <item>On a layer (<see cref="AnchorPlacement.Layer"/>): the stack's result right after that layer at its level, in each channel: the
    /// layers at or below it composited as the document composites them (the layer itself with its filters, mask, opacity and blend mode),
    /// as if nothing above it existed (layers clipped to it are not included). Inside a pass-through group the result continues from what is
    /// below the group; inside an isolated group (any other blend mode, or a group clipped to the layer below) it starts from transparent.
    /// The groups it is in do not apply their own opacity, mask or blend to it. A hidden layer contributes nothing (the anchor then holds
    /// what is below it).</item>
    /// <item>On a mask (<see cref="AnchorPlacement.Mask"/>): how much the mask lets its layer show at each pixel (1 − the filtered hide
    /// amount, with the mask's on/off, invert and density), everywhere on the canvas.</item>
    /// </list>
    /// Only layers above an anchor (later in <see cref="PaintDocument.Layers"/>) can read it; a reference that is not below its reader (after
    /// the layers were moved) or whose anchor is gone passes its input through and says why (<see cref="PaintDocument.AnchorIssues"/>).
    /// An anchor belongs to its layer (or its layer's mask) and to one texture set's document; its ID is stable across undo, saving and
    /// resizing. Edits go through <see cref="PaintDocument"/> and are undoable.
    /// </summary>
    public sealed class AnchorPoint
    {
        /// <summary>The longest name (characters).</summary>
        public const int MaxNameLength = 128;
        public Guid Id { get; }
        public string Name { get; internal set; }
        public AnchorPlacement Placement { get; }
        internal AnchorPoint(Guid id, string name, AnchorPlacement placement)
        {
            if (id == Guid.Empty) throw new ArgumentException("An anchor needs a non-empty ID.", nameof(id));
            if (!Enum.IsDefined(typeof(AnchorPlacement), placement)) throw new ArgumentOutOfRangeException(nameof(placement));
            CheckName(name);
            Id = id; Name = name; Placement = placement;
        }
        /// <summary>Refuses a name that is empty, only spaces or longer than <see cref="MaxNameLength"/>.</summary>
        public static void CheckName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An anchor needs a name.", nameof(name));
            if (name.Length > MaxNameLength) throw new ArgumentException("An anchor's name is at most " + MaxNameLength + " characters.", nameof(name));
        }
        public override string ToString() { return Name + (Placement == AnchorPlacement.Mask ? " (mask)" : ""); }
    }

    /// <summary>An anchor and the layer it is on (for a mask anchor, the layer whose mask it is on).</summary>
    public sealed class AnchorInfo
    {
        public AnchorPoint Anchor { get; }
        public PaintLayer Layer { get; }
        internal AnchorInfo(AnchorPoint anchor, PaintLayer layer) { Anchor = anchor; Layer = layer; }
    }

    /// <summary>Why an anchor reference has no effect now.</summary>
    public enum AnchorIssueKind
    {
        /// <summary>The generator has no anchor chosen yet.</summary>
        NotChosen = 0,
        /// <summary>The anchor it reads is gone (removed, or its layer or mask was deleted or merged).</summary>
        Missing = 1,
        /// <summary>The anchor is not below the reading layer (the layers were moved), or on the reading layer itself.</summary>
        NotBelow = 2,
    }

    /// <summary>An anchor generator that passes its input through because its reference is not usable, and why.</summary>
    public sealed class AnchorIssue
    {
        /// <summary>The layer whose stack holds the generator.</summary>
        public Guid LayerId { get; }
        public Guid FilterId { get; }
        public FilterTarget Target { get; }
        /// <summary>The anchor it refers to (Guid.Empty when none is chosen).</summary>
        public Guid AnchorId { get; }
        public AnchorIssueKind Kind { get; }
        /// <summary>The reason in English (names the layers and the anchor).</summary>
        public string Reason { get; }
        /// <summary>The dependency graph's own refusal for <see cref="AnchorIssueKind.NotBelow"/> (null otherwise).</summary>
        public string GraphReason { get; }
        internal AnchorIssue(Guid layerId, Guid filterId, FilterTarget target, Guid anchorId, AnchorIssueKind kind, string reason, string graphReason)
        { LayerId = layerId; FilterId = filterId; Target = target; AnchorId = anchorId; Kind = kind; Reason = reason; GraphReason = graphReason; }
        public override string ToString() { return Kind + ": " + Reason; }
    }

    /// <summary>How one anchor generator stage is resolved now (rebuilt when the document changes). Internal: the filter engine and the
    /// change tracking read it.</summary>
    internal sealed class AnchorBinding
    {
        public Guid FilterId; public FilterEffect Effect; public PaintLayer Reader; public FilterTarget Target;
        /// <summary>The anchor ID the stage names (Guid.Empty: none); <see cref="Anchor"/> is null when it is not found.</summary>
        public Guid AnchorRefId;
        public AnchorPoint Anchor; public PaintLayer Host;
        public PaintChannel Channel; public AnchorRead Read;
        public bool Valid; public AnchorIssueKind Kind; public string Reason, GraphReason;
        public bool Mask { get { return Anchor != null && Anchor.Placement == AnchorPlacement.Mask; } }
        /// <summary>The anchor's value key: the channel read for a layer anchor, −1 for a mask anchor.</summary>
        public int Key { get { return Mask ? -1 : (int)Channel; } }
    }

    /// <summary>The anchor's pixels over the tiles an evaluation needs (read-only; the filter engine's workers share it): the composite
    /// tiles of a layer anchor or the filtered hide amounts of a mask anchor, and the base value <see cref="Value"/> the anchor generator
    /// reads.</summary>
    internal sealed class AnchorSample
    {
        readonly int tileSize, tx0, ty0, columns, rows; readonly byte[][] tiles;
        readonly bool mask; readonly double[] factor; readonly int mode;
        /// <param name="tiles">columns × rows tiles from (tx0, ty0), row by row; null where nothing is needed (or nothing is there for a
        /// layer anchor: transparent).</param>
        AnchorSample(int tileSize, int tx0, int ty0, int columns, int rows, byte[][] tiles, bool mask, double[] factor, int mode)
        { this.tileSize = tileSize; this.tx0 = tx0; this.ty0 = ty0; this.columns = columns; this.rows = rows; this.tiles = tiles; this.mask = mask; this.factor = factor; this.mode = mode; }

        internal static AnchorSample ForLayer(int tileSize, int tx0, int ty0, int columns, int rows, byte[][] tiles, PaintChannel channel, AnchorRead read)
        {
            var type = FilterSettings.ValueTypeOf(channel);
            int mode = read == AnchorRead.Coverage ? 0 : type == GraphValueType.Color ? 2 : 1;
            return new AnchorSample(tileSize, tx0, ty0, columns, rows, tiles, false, null, mode);
        }
        internal static AnchorSample ForMask(int tileSize, int tx0, int ty0, int columns, int rows, byte[][] tiles, RasterMask mask)
        {
            var factor = new double[256]; for (int h = 0; h < 256; h++) factor[h] = mask.Factor((byte)h); // the same values Factor gives per pixel
            return new AnchorSample(tileSize, tx0, ty0, columns, rows, tiles, true, factor, 0);
        }

        /// <summary>The base value b (0..1) at a pixel the sample covers.</summary>
        internal double Value(int x, int y)
        {
            int tx = x / tileSize - tx0, ty = y / tileSize - ty0;
            if (tx < 0 || ty < 0 || tx >= columns || ty >= rows) throw new InvalidOperationException("The anchor sample does not cover pixel (" + x + ", " + y + ").");
            var t = tiles[ty * columns + tx];
            if (mask) return factor[t == null ? 0 : t[((y % tileSize) * tileSize + x % tileSize) * 4 + 3]];
            if (t == null) return 0;
            int o = ((y % tileSize) * tileSize + x % tileSize) * 4; var unit = MathUtil.ByteUnit;
            double a = unit[t[o + 3]];
            switch (mode)
            {
                case 0: return a;
                case 1: return unit[t[o]] * a;
                default: return (.3 * unit[t[o]] + .59 * unit[t[o + 1]] + .11 * unit[t[o + 2]]) * a;
            }
        }
    }
}
