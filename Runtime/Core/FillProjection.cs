using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>How a fill layer's images are laid onto the texture set (Substance Painter's fill Projection). Values are stored in
    /// the native format; append only. <see cref="Decal"/> (native version 17) makes the layer a decal: a planar projection cropped to
    /// its box, cut by depth and by faces turned away, transparent everywhere else in every channel.</summary>
    public enum FillProjectionMode { Uv = 0, Triplanar = 1, Planar = 2, Spherical = 3, Cylindrical = 4, Decal = 5 }

    /// <summary>What a projected image does outside its 0..1 square: repeats, continues its edge pixels, or (<see cref="None"/>, native
    /// version 17) is transparent there (Substance Painter's UV Wrap None). Values are stored in the native format; append only.</summary>
    public enum FillWrap { Repeat = 0, Clamp = 1, None = 2 }

    /// <summary>How an image resource's stored values are turned into a channel's values (<see cref="FillImageColor.ConversionFor"/>).</summary>
    public enum FillImageConversion
    {
        /// <summary>The values are used as stored.</summary>
        None = 0,
        /// <summary>A data image (linear) in a colour channel: each colour value is encoded to sRGB (IEC 61966-2-1).</summary>
        LinearToSrgb = 1,
    }

    /// <summary>
    /// How a fill layer's images (one per channel, <see cref="PaintLayer.FillImages"/>) are laid onto the texture set. One per fill
    /// layer; every image channel of the layer uses it. Immutable; checked when made.
    /// <para>Per pixel (x, y) of a W × H texture set, with the image's level-0 size w × h:</para>
    /// <list type="number">
    /// <item>Base coordinates (s, t):
    /// <list type="bullet">
    /// <item>Uv: (s, t) = ((x + 0.5) / W, (y + 0.5) / H), the pixel centre in the UV square.</item>
    /// <item>The other modes read the texel's point on the model from the baked Position map (back to the snapshot space with the bake's
    /// bounding box, then into the model root's space with the document's <see cref="GeneratorModelFrame"/>, as the shape gradient does)
    /// and place it in <see cref="Placement"/>'s space: l = Rᵀ (p − centre), R the placement's rotation (Unity's Euler order),
    /// S = its sizes. Planar: (s, t) = (l_x / S_x + 0.5, l_y / S_y + 0.5), the image on the placement's −Z face seen from −Z (projected
    /// through the whole model; the back shows it mirrored). Spherical: s = atan2(l_x, −l_z) / 2π + 0.5 (0.5 faces −Z, the seam is at
    /// +Z), t = asin(l_y / |l|) / π + 0.5 (the centre itself reads (0.5, 0.5)); sizes are not used. Cylindrical: s as spherical about
    /// the placement's Y axis, t = l_y / S_y + 0.5. Triplanar: three planar projections along the placement's axes, each read the
    /// right way round from outside the face the normal points to (+X: (l_z, l_y), −X: (−l_z, l_y), +Y: (l_x, l_z), −Y: (−l_x, l_z),
    /// +Z: (−l_x, l_y), −Z: (l_x, l_y), each divided by the sizes of those axes and + 0.5), mixed by the weights
    /// wᵢ = max(0, |nᵢ| − (1 − <see cref="BlendWidth"/>) · maxⱼ |nⱼ|) / Σ, n the baked world normal (WorldNormal map) turned into the
    /// placement's space: BlendWidth 0 takes only the axis the surface faces most, 1 mixes in proportion to |nᵢ|. Decal: (s, t) as
    /// planar, but only inside the box (|l_x| ≤ S_x / 2, |l_y| ≤ S_y / 2, |l_z| ≤ S_z / 2), weighted by the coverage c = d · b: the depth
    /// weight d = 1 for |l_z| / (S_z / 2) ≤ 1 − w_d and (1 − |l_z| / (S_z / 2)) / w_d up to the box's face, w_d = 1 −
    /// <see cref="DepthHardness"/> (0: a hard cut at the face); the facing weight b from the angle α between the baked world normal (in the
    /// placement's space) and −Z, the side the image is seen from: b = 1 for α ≤ θ − w_b, (θ − α) / w_b up to θ and 0 beyond, θ =
    /// <see cref="BackfaceAngle"/>, w_b = (1 − <see cref="BackfaceHardness"/>) · θ (θ = 180° with hardness 1 keeps every face). Outside the
    /// box, where c = 0, and on texels without a position or normal the decal is transparent (0, 0, 0, 0) in every channel.</item>
    /// </list></item>
    /// <item>The UV transform (all modes): (s′, t′) = Tiles ⊙ (R(−Rotation) ((s, t) − 0.5) + 0.5) + Offset, so the image turns
    /// counter-clockwise by <see cref="Rotation"/> degrees about the square's centre, then repeats Tiles times from the origin and
    /// shifts by Offset (in image squares).</item>
    /// <item>Sampling: texel coordinates (s′ w − 0.5, t′ h − 0.5), <see cref="Wrap"/> per axis (None: a texel outside the image reads
    /// as transparent black), bilinear over premultiplied alpha.
    /// Minified pixels read a mipmap (each level halves the one before with an area average over premultiplied alpha; see
    /// <see cref="FillImageColor"/>) chosen from the pixel's footprint ρ (the longer of the two columns of ∂(s′ w, t′ h)/∂(x, y), from the
    /// UV transform in Uv mode and from the neighbouring texels' points elsewhere, taking the neighbour on each axis that is nearer on
    /// the model): ρ ≤ 1 reads level 0, otherwise levels ⌊log₂ ρ⌋ and the next mixed by the fraction (trilinear). No anisotropic
    /// filtering. A pixel read only from equal texels is that texel; a result whose alpha rounds to 0 keeps the weighted average colour
    /// of the transparent texels it read (transparent pixels keep RGB). Rounded half up to 8 bits once.</item>
    /// </list>
    /// Pixels whose Position (or, for triplanar, normal) texel is empty keep the channel's fill value, and so does the whole layer while
    /// a map it needs is missing, stale or of another size, or the image is not in the project
    /// (<see cref="PaintDocument.GetFillImageStatus"/> says why). A decal instead is transparent while it cannot be placed (a map is missing,
    /// stale or of another size, the model root is not known): it never spreads its value over the texture set.
    /// <para>Decal channels: every channel with a value is evaluated, not only those with an image. The decal's shape is the alpha σ of
    /// its shape image, the image of the first channel in channel order that has one (<see cref="PaintChannel.Color"/> first), sampled like
    /// the others; without one (or while it cannot be read) σ = 1, the whole box. A channel's pixel is its own colour (its image's sample,
    /// or its value; the value too where its image cannot be read) with alpha a · σ · c, a its own alpha (the shape channel's own alpha is
    /// σ, taken once), rounded once.</para>
    /// Formulas are this tool's native definitions; they are not claimed to match Substance Painter's projections (Planar with Shape crop,
    /// Depth culling and Backface culling is the nearest to a decal).
    /// </summary>
    public sealed class FillProjection : IEquatable<FillProjection>
    {
        public const double MinTiles = 1e-3, MaxTiles = 1e4, MaxOffset = 1e4, MaxRotation = 360, MaxBackfaceAngle = 180;
        /// <summary>A decal's culling when it is made: a soft depth edge, faces turned more than 90° from the image's side hidden with a soft edge.</summary>
        public const double DefaultDepthHardness = .8, DefaultBackfaceAngle = 90, DefaultBackfaceHardness = .8;
        /// <summary>Version of the formulas above. Stored with the projection; a reader refuses versions it does not implement.</summary>
        public const int AlgorithmVersion = 1;
        /// <summary>A unit box at the model root (the placement a new layer starts with before the window fits it to the model).</summary>
        public static readonly ShapeVolume DefaultPlacement = new ShapeVolume(GeneratorShape.Box, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0);
        /// <summary>UV, repeated once, no offset or rotation.</summary>
        public static readonly FillProjection Default = new FillProjection(FillProjectionMode.Uv, FillWrap.Repeat, 1, 1, 0, 0, 0, .3, DefaultPlacement);
        /// <summary>A decal at a placement: the image once (not repeated, transparent outside it) with the default culling.</summary>
        public static FillProjection DecalAt(ShapeVolume placement) => Default.WithMode(FillProjectionMode.Decal).WithWrap(FillWrap.None).WithPlacement(placement);

        public FillProjectionMode Mode { get; }
        public FillWrap Wrap { get; }
        /// <summary>How many times the image repeats across the projected square on each axis (a fraction enlarges it).</summary>
        public double TileU { get; }
        public double TileV { get; }
        /// <summary>Shift in image squares (after Tiles).</summary>
        public double OffsetU { get; }
        public double OffsetV { get; }
        /// <summary>Degrees the image turns counter-clockwise about the projected square's centre.</summary>
        public double Rotation { get; }
        /// <summary>Triplanar: 0 = hard switch to the axis the surface faces most, 1 = mixed in proportion to the normal's components.</summary>
        public double BlendWidth { get; }
        /// <summary>Where the projection is in the model root's space (centre, rotation in Euler degrees, sizes in scene units): the box
        /// the 3D view's gizmo moves. Always a <see cref="GeneratorShape.Box"/> with falloff 0; the Uv mode does not use it (kept).</summary>
        public ShapeVolume Placement { get; }
        /// <summary>Decal: 0 fades the decal from the box's middle plane (Z = 0) to its −Z and +Z faces, 1 cuts it hard at them. The
        /// culling of a projection that is not a decal is always the default.</summary>
        public double DepthHardness { get; }
        /// <summary>Decal: faces whose normal is turned more than this many degrees from the side the image is seen from (−Z) are hidden
        /// (0..180; 180 with hardness 1 keeps every face).</summary>
        public double BackfaceAngle { get; }
        /// <summary>Decal: 0 fades faces out from facing the image's side to <see cref="BackfaceAngle"/>, 1 cuts them hard at it.</summary>
        public double BackfaceHardness { get; }

        public FillProjection(FillProjectionMode mode, FillWrap wrap, double tileU, double tileV, double offsetU, double offsetV, double rotation, double blendWidth, ShapeVolume placement)
            : this(mode, wrap, tileU, tileV, offsetU, offsetV, rotation, blendWidth, placement, DefaultDepthHardness, DefaultBackfaceAngle, DefaultBackfaceHardness) { }
        public FillProjection(FillProjectionMode mode, FillWrap wrap, double tileU, double tileV, double offsetU, double offsetV, double rotation, double blendWidth, ShapeVolume placement,
            double depthHardness, double backfaceAngle, double backfaceHardness)
        {
            Mode = mode; Wrap = wrap; TileU = tileU; TileV = tileV; OffsetU = offsetU; OffsetV = offsetV; Rotation = rotation; BlendWidth = blendWidth; Placement = placement;
            DepthHardness = depthHardness; BackfaceAngle = backfaceAngle; BackfaceHardness = backfaceHardness;
            string why = Refusal(); if (why != null) throw new ArgumentOutOfRangeException("projection", why);
        }

        FillProjection With(FillProjectionMode mode, FillWrap wrap, double tileU, double tileV, double offsetU, double offsetV, double rotation, double blendWidth, ShapeVolume placement)
            => new FillProjection(mode, wrap, tileU, tileV, offsetU, offsetV, rotation, blendWidth, placement, DepthHardness, BackfaceAngle, BackfaceHardness);
        /// <summary>Another mode. Leaving the decal drops its culling (only a decal has one); a new decal starts with the default culling.</summary>
        public FillProjection WithMode(FillProjectionMode mode)
            => mode == FillProjectionMode.Decal ? With(mode, Wrap, TileU, TileV, OffsetU, OffsetV, Rotation, BlendWidth, Placement)
                : new FillProjection(mode, Wrap, TileU, TileV, OffsetU, OffsetV, Rotation, BlendWidth, Placement);
        public FillProjection WithWrap(FillWrap wrap) => With(Mode, wrap, TileU, TileV, OffsetU, OffsetV, Rotation, BlendWidth, Placement);
        public FillProjection WithTiles(double u, double v) => With(Mode, Wrap, u, v, OffsetU, OffsetV, Rotation, BlendWidth, Placement);
        public FillProjection WithOffset(double u, double v) => With(Mode, Wrap, TileU, TileV, u, v, Rotation, BlendWidth, Placement);
        public FillProjection WithRotation(double degrees) => With(Mode, Wrap, TileU, TileV, OffsetU, OffsetV, degrees, BlendWidth, Placement);
        public FillProjection WithBlendWidth(double value) => With(Mode, Wrap, TileU, TileV, OffsetU, OffsetV, Rotation, value, Placement);
        /// <summary>Another placement (its shape and falloff are set to a box with falloff 0).</summary>
        public FillProjection WithPlacement(ShapeVolume value)
            => With(Mode, Wrap, TileU, TileV, OffsetU, OffsetV, Rotation, BlendWidth, new ShapeVolume(GeneratorShape.Box, value.CenterX, value.CenterY, value.CenterZ,
                value.RotationX, value.RotationY, value.RotationZ, value.SizeX, value.SizeY, value.SizeZ, 0));
        /// <summary>Another decal culling (refused on a projection that is not a decal).</summary>
        public FillProjection WithCulling(double depthHardness, double backfaceAngle, double backfaceHardness)
            => new FillProjection(Mode, Wrap, TileU, TileV, OffsetU, OffsetV, Rotation, BlendWidth, Placement, depthHardness, backfaceAngle, backfaceHardness);

        /// <summary>True for the modes that read the baked mesh maps (all but Uv).</summary>
        public bool ReadsMeshMaps => Mode != FillProjectionMode.Uv;
        /// <summary>True for the decal.</summary>
        public bool IsDecal => Mode == FillProjectionMode.Decal;
        /// <summary>The mesh maps the mode reads, in a fixed order (none for Uv; the decal reads the normal for its facing).</summary>
        public IReadOnlyList<MeshMapKind> UsedMaps
            => Mode == FillProjectionMode.Uv ? new MeshMapKind[0] : Mode == FillProjectionMode.Triplanar || Mode == FillProjectionMode.Decal ? new[] { MeshMapKind.Position, MeshMapKind.WorldNormal } : new[] { MeshMapKind.Position };

        /// <summary>Why the values cannot be used, or null.</summary>
        public string Refusal()
        {
            var c = CultureInfo.InvariantCulture;
            if (!Enum.IsDefined(typeof(FillProjectionMode), Mode)) return "Unknown projection " + (int)Mode + ".";
            if (!Enum.IsDefined(typeof(FillWrap), Wrap)) return "Unknown wrap " + (int)Wrap + ".";
            foreach (double v in new[] { TileU, TileV, OffsetU, OffsetV, Rotation, BlendWidth, DepthHardness, BackfaceAngle, BackfaceHardness }) if (double.IsNaN(v) || double.IsInfinity(v)) return "The projection's values must be finite.";
            if (!(TileU >= MinTiles && TileU <= MaxTiles && TileV >= MinTiles && TileV <= MaxTiles)) return "Tiling must be " + MinTiles.ToString(c) + " to " + MaxTiles.ToString(c) + ".";
            if (Math.Abs(OffsetU) > MaxOffset || Math.Abs(OffsetV) > MaxOffset) return "The offset must be within ±" + MaxOffset.ToString(c) + ".";
            if (Math.Abs(Rotation) > MaxRotation) return "The rotation must be within ±" + MaxRotation.ToString(c) + "°.";
            if (BlendWidth < 0 || BlendWidth > 1) return "The blend width must be 0..1.";
            if (DepthHardness < 0 || DepthHardness > 1 || BackfaceHardness < 0 || BackfaceHardness > 1) return "A decal's hardness must be 0..1.";
            if (BackfaceAngle < 0 || BackfaceAngle > MaxBackfaceAngle) return "A decal's back-face angle must be 0..180°.";
            if (!IsDecal && (DepthHardness != DefaultDepthHardness || BackfaceAngle != DefaultBackfaceAngle || BackfaceHardness != DefaultBackfaceHardness)) return "Only a decal culls by depth and facing.";
            if (Placement.Shape != GeneratorShape.Box || Placement.Falloff != 0) return "A projection's placement is a box without falloff.";
            return Placement.Refusal();
        }

        public bool Equals(FillProjection other)
        {
            return other != null && Mode == other.Mode && Wrap == other.Wrap && TileU == other.TileU && TileV == other.TileV && OffsetU == other.OffsetU && OffsetV == other.OffsetV
                && Rotation == other.Rotation && BlendWidth == other.BlendWidth && Placement.Equals(other.Placement)
                && DepthHardness == other.DepthHardness && BackfaceAngle == other.BackfaceAngle && BackfaceHardness == other.BackfaceHardness;
        }
        public override bool Equals(object obj) => Equals(obj as FillProjection);
        public override int GetHashCode() { unchecked { return ((int)Mode * 397) ^ ((int)Wrap << 4) ^ TileU.GetHashCode() ^ (TileV.GetHashCode() * 7) ^ (OffsetU.GetHashCode() * 13) ^ (Rotation.GetHashCode() * 31) ^ (Placement.GetHashCode() * 3) ^ (BackfaceAngle.GetHashCode() * 17); } }
        public override string ToString()
            => string.Format(CultureInfo.InvariantCulture, "{0} {1} tiles ({2:0.###}, {3:0.###}) offset ({4:0.###}, {5:0.###}) rotation {6:0.#} blend {7:0.##} at {8}", Mode, Wrap, TileU, TileV, OffsetU, OffsetV, Rotation, BlendWidth, Placement)
                + (IsDecal ? string.Format(CultureInfo.InvariantCulture, " depth hardness {0:0.##} back faces {1:0.#}° hardness {2:0.##}", DepthHardness, BackfaceAngle, BackfaceHardness) : "");
    }

    /// <summary>How an image's values are read into a channel, as Substance Painter does. The data channels (Roughness, Metallic, Height,
    /// Normal) take the image's values as stored whatever its <see cref="ResourceColorSpace"/>: a data image keeps its values even when it
    /// was imported into Unity as sRGB. The colour channels (Color, Emission) hold sRGB-encoded colour, so an image marked as data
    /// (linear) is encoded to sRGB there; colour (sRGB) and unspecified images (image files) are used as stored. Scalar channels
    /// (Roughness, Metallic, Height) take the Rec. 709 luminance of the stored colour, ⌊(2126 R + 7152 G + 722 B + 5000) / 10000⌋, into
    /// R = G = B, so grey images are unchanged. Alpha is never converted.</summary>
    public static class FillImageColor
    {
        public static bool IsColorChannel(PaintChannel channel) => channel == PaintChannel.Color || channel == PaintChannel.Emission;
        public static bool IsScalarChannel(PaintChannel channel) => channel == PaintChannel.Roughness || channel == PaintChannel.Metallic || channel == PaintChannel.Height;

        public static FillImageConversion ConversionFor(ResourceColorSpace space, PaintChannel channel)
            => IsColorChannel(channel) && space == ResourceColorSpace.Linear ? FillImageConversion.LinearToSrgb : FillImageConversion.None;
        /// <summary>The scalar channels read the luminance into R, G and B.</summary>
        public static bool UsesLuminance(PaintChannel channel) => IsScalarChannel(channel);

        static readonly byte[] toSrgb = MakeTable();
        /// <summary>The conversion of one 8-bit colour value (alpha is not converted).</summary>
        public static byte Convert(FillImageConversion conversion, byte value) => conversion == FillImageConversion.LinearToSrgb ? toSrgb[value] : value;
        internal static byte[] Table(FillImageConversion conversion) => conversion == FillImageConversion.LinearToSrgb ? toSrgb : null;
        static byte[] MakeTable()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                t[i] = MathUtil.ToByte(c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055);
            }
            return t;
        }
        /// <summary>The luminance the scalar channels read (integer Rec. 709 weights, exact for grey).</summary>
        public static byte Luminance(byte r, byte g, byte b) => (byte)((2126 * r + 7152 * g + 722 * b + 5000) / 10000);
    }

    /// <summary>What a fill layer's image channel shows now: the image it reads (or why it cannot), how its values are converted, and
    /// whether the projection has an effect (<see cref="Active"/>). When it is not active the channel shows its fill value.</summary>
    public sealed class FillImageStatus
    {
        public Guid ResourceId { get; }
        /// <summary>The resource, or null when the project has no image with that ID (or no resources are connected).</summary>
        public ImageResource Image { get; }
        public bool Active { get; }
        /// <summary>Why the channel shows its fill value instead of the image, or null.</summary>
        public string Reason { get; }
        public FillImageConversion Conversion { get; }
        public bool Luminance { get; }
        /// <summary>The mipmap levels (1 = only the image itself) and the bytes they hold beyond the image (0 when not bound).</summary>
        public int MipLevels { get; }
        public long MipBytes { get; }
        internal FillImageStatus(Guid id, ImageResource image, string reason, FillImageConversion conversion, bool luminance, int levels, long bytes)
        { ResourceId = id; Image = image; Reason = reason; Active = reason == null; Conversion = conversion; Luminance = luminance; MipLevels = levels; MipBytes = bytes; }
    }

    /// <summary>A fill layer's images: per channel, the ID of a project image resource whose projected pixels replace the channel's
    /// fill value (which stays as the value shown where the image cannot be used). The projection is per layer.</summary>
    public sealed partial class PaintLayer
    {
        readonly Dictionary<PaintChannel, Guid> fillImages = new Dictionary<PaintChannel, Guid>();
        ReadOnlyDictionary<PaintChannel, Guid> fillImagesView;
        /// <summary>Fill layers: the image resource each image channel reads (a channel here always has a fill value too). Empty for
        /// other kinds.</summary>
        public IReadOnlyDictionary<PaintChannel, Guid> FillImages => fillImagesView ?? (fillImagesView = new ReadOnlyDictionary<PaintChannel, Guid>(fillImages));
        /// <summary>Fill layers: how the images are laid onto the texture set. <see cref="FillProjection.Default"/> for other kinds.</summary>
        public FillProjection Projection { get; internal set; } = FillProjection.Default;
        /// <summary>Changes (to a value unique within the document) whenever what the fill's channels show may change: a value, an image,
        /// the projection, or the image a resource ID resolves to. Evaluated tiles are stamped with it.</summary>
        public long FillRevision { get; internal set; }
        public bool HasFillImage(PaintChannel channel) => fillImages.ContainsKey(channel);
        internal void SetFillImageInternal(PaintChannel channel, Guid? id) { if (id.HasValue) fillImages[channel] = id.Value; else fillImages.Remove(channel); }
        /// <summary>True when the layer's pixels in the channel are evaluated (by its filters, or a fill's projection) rather than read
        /// as stored: the compositors then read <see cref="CopyOutputTile"/> and its <see cref="OutputStamp"/>.</summary>
        public bool HasEvaluatedOutput(PaintChannel channel) => HasActiveFilters(channel) || HasFillGradient(channel) || IsProjectedFill(channel);
        /// <summary>A fill layer whose projection is a decal (<see cref="FillProjectionMode.Decal"/>).</summary>
        public bool IsDecal => Kind == LayerKind.Fill && Projection.IsDecal;
        /// <summary>A fill channel whose pixels come from the projection: a channel with an image, and every channel with a value of a decal.</summary>
        internal bool IsProjectedFill(PaintChannel channel) => Kind == LayerKind.Fill && (fillImages.ContainsKey(channel) || Projection.IsDecal && fillValues.ContainsKey(channel));
        /// <summary>A fill whose projection reads the baked mesh maps (and has an image to project, or is a decal).</summary>
        internal bool ReadsMeshMapsForFill => Kind == LayerKind.Fill && (fillGradients.Count > 0 || (fillImages.Count > 0 || Projection.IsDecal) && Projection.ReadsMeshMaps);
    }
}
