using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Built-in non-destructive stages of a filter stack. Values are stored in the native format; append only.
    /// <see cref="Generator"/> is a stage that makes values from the texture set's baked mesh maps (<see cref="GeneratorSettings"/>)
    /// instead of filtering its input; it sits in the same ordered stack as the filters.</summary>
    public enum FilterType { GaussianBlur = 0, Sharpen = 1, Noise = 2, Levels = 3, Invert = 4, Normalize = 5, Generator = 6 }

    /// <summary>Which stack of a layer a filter belongs to: the layer's own pixels (per channel), or its raster mask (one scalar
    /// shared by all channels). The two are kept and shown apart.</summary>
    public enum FilterTarget { Content = 0, Mask = 1 }

    /// <summary>What an output pixel of a filter depends on: the same input pixel (Point), the input pixels within
    /// <see cref="FilterSettings.HaloPixels"/> (Neighborhood), or the whole stage input of the document (Global).</summary>
    public enum FilterLocality { Point = 0, Neighborhood = 1, Global = 2 }

    /// <summary>Immutable parameters of one built-in filter, with its declarations: accepted value types (input type = output
    /// type for every built-in), locality and halo, algorithm version and precision.
    /// <para>Precision: every stage reads and writes straight RGBA8 and rounds half up once per stage (like one layer of the
    /// compositor). Blurs work on alpha-premultiplied integers (colour × alpha, alpha × 255) with exact integer box sums and one
    /// round-half-up division per pass, so the result does not depend on where a tile or block starts. Formulas are this tool's
    /// native definitions in stored (encoded) values; they are not claimed to match Photoshop's, CLIP STUDIO's or Substance's
    /// filters of the same name.</para>
    /// <list type="bullet">
    /// <item>GaussianBlur (Radius 1..256): three successive box blurs (horizontal then vertical each) whose radii add up to Radius,
    /// so the blur reaches exactly Radius pixels (standard deviation ≈ Radius / 3). Canvas edges repeat the edge pixel. Alpha is
    /// blurred too (coverage spreads up to Radius pixels). On the Normal channel the premultiplied encoded average is decoded,
    /// renormalized and encoded again (a renormalized vector average).</item>
    /// <item>Sharpen (unsharp mask; Radius 1..64, Amount 0..5, Threshold 0..255 levels): c + Amount × (c − blur(c)) per colour
    /// component where |c − blur(c)| ≥ Threshold; blur as GaussianBlur. Alpha and fully transparent pixels are unchanged.</item>
    /// <item>Noise (Amount 0..1, Seed, Monochrome): c + Amount × 127.5 × u with u ∈ [−1, 1] from an integer hash of
    /// (seed, x, y, component); the same canvas pixel always gets the same u. Monochrome uses one u for R, G and B. Alpha is
    /// unchanged.</item>
    /// <item>Levels: the same formula as <see cref="AdjustmentSettings.Levels"/> (input range, gamma, output range) per colour
    /// component (a range remap on scalar channels). Alpha is unchanged.</item>
    /// <item>Invert: 255 − c per colour component. Alpha is unchanged.</item>
    /// <item>Normalize (global): stretches the smallest and largest colour component of all pixels with alpha &gt; 0 of the
    /// stage input (the whole document) to 0 and 255. A change anywhere changes every output pixel.</item>
    /// <item>Generator (point): see <see cref="GeneratorSettings"/>; its parameters are <see cref="Generator"/>.</item>
    /// </list>
    /// On a mask stack the stored hide amount is filtered as an opaque grey image (value = hide amount, 255 = hidden).</summary>
    public sealed class FilterSettings : IEquatable<FilterSettings>
    {
        public const int MaxBlurRadius = 256;
        public const int MaxSharpenRadius = 64;
        public const double MaxSharpenAmount = 5;

        public FilterType Type { get; private set; }
        /// <summary>GaussianBlur and Sharpen: the reach of the blur in pixels.</summary>
        public int Radius { get; private set; }
        /// <summary>Sharpen: 0..5 (1 = 100 %). Noise: 0..1.</summary>
        public double Amount { get; private set; }
        /// <summary>Sharpen: the smallest difference (in 8-bit levels) that is sharpened.</summary>
        public int Threshold { get; private set; }
        public int Seed { get; private set; }
        public bool Monochrome { get; private set; }
        public double InputBlack { get; private set; }
        public double InputWhite { get; private set; }
        public double Gamma { get; private set; }
        public double OutputBlack { get; private set; }
        public double OutputWhite { get; private set; }
        /// <summary>The generator's parameters when <see cref="Type"/> is <see cref="FilterType.Generator"/>; null otherwise.</summary>
        public GeneratorSettings Generator { get; private set; }

        private FilterSettings(FilterType type) { Type = type; InputWhite = 1; Gamma = 1; OutputWhite = 1; }

        public static FilterSettings GaussianBlur(int radius) { return Checked(new FilterSettings(FilterType.GaussianBlur) { Radius = radius }); }
        public static FilterSettings Sharpen(int radius = 2, double amount = 1, int threshold = 0)
        { return Checked(new FilterSettings(FilterType.Sharpen) { Radius = radius, Amount = amount, Threshold = threshold }); }
        public static FilterSettings Noise(double amount = .25, int seed = 0, bool monochrome = true)
        { return Checked(new FilterSettings(FilterType.Noise) { Amount = amount, Seed = seed, Monochrome = monochrome }); }
        public static FilterSettings Levels(double inputBlack = 0, double inputWhite = 1, double gamma = 1, double outputBlack = 0, double outputWhite = 1)
        { return Checked(new FilterSettings(FilterType.Levels) { InputBlack = inputBlack, InputWhite = inputWhite, Gamma = gamma, OutputBlack = outputBlack, OutputWhite = outputWhite }); }
        public static FilterSettings Invert() { return new FilterSettings(FilterType.Invert); }
        public static FilterSettings Normalize() { return new FilterSettings(FilterType.Normalize); }
        /// <summary>A generator stage with these parameters.</summary>
        public static FilterSettings FromGenerator(GeneratorSettings generator)
        {
            if (generator == null) throw new ArgumentNullException(nameof(generator));
            return Checked(new FilterSettings(FilterType.Generator) { Generator = generator });
        }
        /// <summary>The same generator stage with other parameters (the type of generator may change).</summary>
        public FilterSettings WithGenerator(GeneratorSettings generator)
        {
            if (Type != FilterType.Generator) throw new InvalidOperationException(Type + " is not a generator.");
            return FromGenerator(generator);
        }
        /// <summary>Settings of any type from stored values (loaders). Parameters a type does not use must be at their defaults.</summary>
        public static FilterSettings FromValues(FilterType type, int radius, double amount, int threshold, int seed, bool monochrome,
            double inputBlack, double inputWhite, double gamma, double outputBlack, double outputWhite)
        {
            return Checked(new FilterSettings(type) { Radius = radius, Amount = amount, Threshold = threshold, Seed = seed, Monochrome = monochrome,
                InputBlack = inputBlack, InputWhite = inputWhite, Gamma = gamma, OutputBlack = outputBlack, OutputWhite = outputWhite });
        }
        public FilterSettings WithRadius(int value) { var s = Copy(); s.Radius = value; return Checked(s); }
        public FilterSettings WithAmount(double value) { var s = Copy(); s.Amount = value; return Checked(s); }
        public FilterSettings WithThreshold(int value) { var s = Copy(); s.Threshold = value; return Checked(s); }
        public FilterSettings WithSeed(int value) { var s = Copy(); s.Seed = value; return Checked(s); }
        public FilterSettings WithMonochrome(bool value) { var s = Copy(); s.Monochrome = value; return Checked(s); }
        public FilterSettings WithLevels(double inputBlack, double inputWhite, double gamma, double outputBlack, double outputWhite)
        { var s = Copy(); s.InputBlack = inputBlack; s.InputWhite = inputWhite; s.Gamma = gamma; s.OutputBlack = outputBlack; s.OutputWhite = outputWhite; return Checked(s); }
        FilterSettings Copy()
        {
            return new FilterSettings(Type) { Radius = Radius, Amount = Amount, Threshold = Threshold, Seed = Seed, Monochrome = Monochrome,
                InputBlack = InputBlack, InputWhite = InputWhite, Gamma = Gamma, OutputBlack = OutputBlack, OutputWhite = OutputWhite, Generator = Generator };
        }
        static FilterSettings Checked(FilterSettings s) { s.Validate(); return s; }

        void Validate()
        {
            if (!Enum.IsDefined(typeof(FilterType), Type)) throw new ArgumentOutOfRangeException(nameof(Type));
            if ((Type == FilterType.Generator) != (Generator != null)) throw new ArgumentException(Type == FilterType.Generator ? "A generator stage needs its generator settings." : "Generator settings belong to a generator stage.", nameof(Generator));
            foreach (double v in new[] { Amount, InputBlack, InputWhite, Gamma, OutputBlack, OutputWhite }) MathUtil.RequireFinite(v, "filter");
            bool blur = Type == FilterType.GaussianBlur, sharpen = Type == FilterType.Sharpen, noise = Type == FilterType.Noise, levels = Type == FilterType.Levels;
            if (blur && (Radius < 1 || Radius > MaxBlurRadius)) throw new ArgumentOutOfRangeException(nameof(Radius), "Blur radius must be 1.." + MaxBlurRadius + " pixels.");
            if (sharpen && (Radius < 1 || Radius > MaxSharpenRadius)) throw new ArgumentOutOfRangeException(nameof(Radius), "Sharpen radius must be 1.." + MaxSharpenRadius + " pixels.");
            if (!blur && !sharpen && Radius != 0) throw new ArgumentException(Type + " has no radius.", nameof(Radius));
            if (sharpen && (Amount < 0 || Amount > MaxSharpenAmount)) throw new ArgumentOutOfRangeException(nameof(Amount), "Sharpen amount must be 0.." + MaxSharpenAmount + ".");
            if (noise && (Amount < 0 || Amount > 1)) throw new ArgumentOutOfRangeException(nameof(Amount), "Noise amount must be 0..1.");
            if (!sharpen && !noise && Amount != 0) throw new ArgumentException(Type + " has no amount.", nameof(Amount));
            if (sharpen ? Threshold < 0 || Threshold > 255 : Threshold != 0) throw new ArgumentOutOfRangeException(nameof(Threshold), "Threshold must be 0..255 (sharpen only).");
            if (!noise && (Seed != 0 || Monochrome)) throw new ArgumentException("Seed and monochrome belong to noise.");
            if (levels)
            {
                if (InputBlack < 0 || InputWhite > 1 || InputWhite - InputBlack < 1.0 / 255) throw new ArgumentOutOfRangeException("input range", "Levels input range must be inside 0..1 and at least one step wide.");
                if (OutputBlack < 0 || OutputBlack > 1 || OutputWhite < 0 || OutputWhite > 1) throw new ArgumentOutOfRangeException("output range");
                if (Gamma < 0.1 || Gamma > 9.99) throw new ArgumentOutOfRangeException(nameof(Gamma), "Gamma must be 0.1..9.99.");
            }
            else if (InputBlack != 0 || InputWhite != 1 || Gamma != 1 || OutputBlack != 0 || OutputWhite != 1) throw new ArgumentException("Input/output ranges and gamma belong to levels.");
        }

        /// <summary>Version of this type's formula. Stored with every filter; a reader refuses versions it does not implement.</summary>
        public int AlgorithmVersion { get { return AlgorithmVersionOf(Type); } }
        public static int AlgorithmVersionOf(FilterType type) { return 1; }
        public FilterLocality Locality
        {
            get { return Type == FilterType.Normalize ? FilterLocality.Global : HaloPixels > 0 ? FilterLocality.Neighborhood : FilterLocality.Point; }
        }
        /// <summary>True for a generator stage (it reads the texture set's mesh maps).</summary>
        public bool IsGenerator { get { return Type == FilterType.Generator; } }
        /// <summary>True for an anchor generator (it reads an anchor point below its layer, <see cref="GeneratorType.Anchor"/>).</summary>
        public bool ReadsAnchor { get { return Type == FilterType.Generator && Generator.Type == GeneratorType.Anchor; } }
        /// <summary>How far (in pixels) an input pixel can influence output pixels (0 for point and global filters).</summary>
        public int HaloPixels { get { return Type == FilterType.GaussianBlur || Type == FilterType.Sharpen ? Radius : 0; } }
        /// <summary>True when output alpha can be non-zero where the input alpha is zero (within the halo). Only the blur spreads
        /// coverage; every other filter keeps alpha as it is.</summary>
        public bool ExpandsCoverage { get { return Type == FilterType.GaussianBlur; } }
        /// <summary>On a mask (an opaque grey image), true when a region that is 0 everywhere within the halo stays 0.</summary>
        internal bool PreservesZero
        {
            get
            {
                switch (Type)
                {
                    // Normalize: 0 が 1 画素でもあれば最小は 0 で、0 は 0 に写る（全部が同じ値なら何も変えない）
                    case FilterType.GaussianBlur: case FilterType.Sharpen: case FilterType.Normalize: return true;
                    case FilterType.Levels: return OutputBlack == 0;
                    // 見える度合い 1（隠す量 0）は、最大・加算・スクリーンでは 1 のまま（マップが無い所・使えないときは入力のまま）
                    case FilterType.Generator: return Generator.Blend == GeneratorBlend.Max || Generator.Blend == GeneratorBlend.Add || Generator.Blend == GeneratorBlend.Screen;
                    default: return false; // Invert, Noise
                }
            }
        }

        /// <summary>The value type of a channel: colour (Color, Emission), scalar (Roughness, Metallic, Height; masks are scalar
        /// too) or tangent-space normal.</summary>
        public static GraphValueType ValueTypeOf(PaintChannel channel)
        {
            switch (channel)
            {
                case PaintChannel.Color: case PaintChannel.Emission: return GraphValueType.Color;
                case PaintChannel.Normal: return GraphValueType.TangentNormal;
                default: return GraphValueType.Scalar;
            }
        }
        /// <summary>True when the filter is defined for the value type. Every built-in filter outputs the type it reads.</summary>
        public bool Accepts(GraphValueType type)
        {
            switch (type)
            {
                case GraphValueType.Color: return true;
                case GraphValueType.Scalar: return !(Type == FilterType.Noise && !Monochrome);
                default: return Type == FilterType.GaussianBlur;
            }
        }
        /// <summary>Why the filter cannot be applied to the channel, or null when it can.</summary>
        public string RefusalFor(PaintChannel channel)
        {
            PaintLayer.ValidateChannel(channel);
            var type = ValueTypeOf(channel);
            if (Accepts(type)) return null;
            if (type == GraphValueType.TangentNormal && Type == FilterType.Generator)
                return "A generator makes one value per pixel, and the " + channel + " channel holds unit vectors. Put the generator on a mask, or on Height and derive the normals from it.";
            if (type == GraphValueType.TangentNormal)
                return Name + " has no meaning for tangent-space normals (the " + channel + " channel holds unit vectors); only the blur is defined there, and it renormalizes.";
            return "Colour noise would give the scalar " + channel + " channel different R, G and B values; use monochrome noise.";
        }
        /// <summary>Why the filter cannot be applied to a mask (a 0..1 scalar), or null when it can.</summary>
        public string RefusalForMask() { return Accepts(GraphValueType.Scalar) ? null : "A mask is one scalar; use monochrome noise."; }
        /// <summary>Short display name.</summary>
        public string Name
        {
            get
            {
                switch (Type)
                {
                    case FilterType.GaussianBlur: return "Gaussian blur";
                    case FilterType.Noise: return Monochrome ? "Noise (mono)" : "Noise (colour)";
                    case FilterType.Generator: return Generator.Name;
                    default: return Type.ToString();
                }
            }
        }

        public bool Equals(FilterSettings other)
        {
            return other != null && Type == other.Type && Radius == other.Radius && Amount == other.Amount && Threshold == other.Threshold && Seed == other.Seed
                && Monochrome == other.Monochrome && InputBlack == other.InputBlack && InputWhite == other.InputWhite && Gamma == other.Gamma
                && OutputBlack == other.OutputBlack && OutputWhite == other.OutputWhite && Equals(Generator, other.Generator);
        }
        public override bool Equals(object obj) { return Equals(obj as FilterSettings); }
        public override int GetHashCode() { unchecked { return ((int)Type * 397) ^ Radius ^ (Amount.GetHashCode() * 31) ^ (Seed * 17) ^ Threshold ^ Gamma.GetHashCode() ^ (Generator == null ? 0 : Generator.GetHashCode()); } }
        public override string ToString()
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            switch (Type)
            {
                case FilterType.GaussianBlur: return string.Format(c, "{0} r{1}", Name, Radius);
                case FilterType.Sharpen: return string.Format(c, "{0} r{1} ×{2:0.##} t{3}", Name, Radius, Amount, Threshold);
                case FilterType.Noise: return string.Format(c, "{0} {1:0.##} seed {2}", Name, Amount, Seed);
                case FilterType.Levels: return string.Format(c, "{0} {1:0.###}–{2:0.###} γ{3:0.##} → {4:0.###}–{5:0.###}", Name, InputBlack, InputWhite, Gamma, OutputBlack, OutputWhite);
                case FilterType.Generator: return Generator.ToString();
                default: return Name;
            }
        }
    }

    /// <summary>One entry of a filter stack: a stable id, the settings, whether it is on, its strength (0..1: the result is mixed
    /// with the stage input by this amount) and, on a layer's content stack, the channels it applies to (it is refused at
    /// creation for a channel whose value type it does not accept). Immutable; edits go through <see cref="PaintDocument"/> and
    /// are undoable.</summary>
    public sealed class FilterEffect
    {
        static readonly PaintChannel[] None = new PaintChannel[0];
        public Guid Id { get; private set; }
        public FilterSettings Settings { get; private set; }
        public bool Enabled { get; private set; }
        public double Strength { get; private set; }
        /// <summary>Content stack: the channels the filter applies to (sorted). Empty on a mask stack.</summary>
        public IReadOnlyList<PaintChannel> Channels { get; private set; }

        internal FilterEffect(Guid id, FilterSettings settings, bool enabled, double strength, IEnumerable<PaintChannel> channels)
        {
            if (id == Guid.Empty) throw new ArgumentException("Filter ID must not be empty.", nameof(id));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            MathUtil.RequireFinite(strength, nameof(strength));
            if (strength < 0 || strength > 1) throw new ArgumentOutOfRangeException(nameof(strength), "Strength must be 0..1.");
            Id = id; Enabled = enabled; Strength = strength;
            if (channels == null) { Channels = None; return; }
            var list = new List<PaintChannel>();
            foreach (var c in channels) { PaintLayer.ValidateChannel(c); if (list.Contains(c)) throw new ArgumentException("Duplicate filter channel " + c + ".", nameof(channels)); list.Add(c); }
            list.Sort(); Channels = new ReadOnlyCollection<PaintChannel>(list);
        }
        public bool AppliesTo(PaintChannel channel) { foreach (var c in Channels) if (c == channel) return true; return false; }
        /// <summary>True when the filter changes the output: on, with a strength above zero.</summary>
        public bool IsActive { get { return Enabled && Strength > 0; } }
        internal FilterEffect With(FilterSettings settings = null, bool? enabled = null, double? strength = null, IEnumerable<PaintChannel> channels = null)
        { return new FilterEffect(Id, settings ?? Settings, enabled ?? Enabled, strength ?? Strength, channels ?? Channels); }
        public override string ToString() { return Settings + (Enabled ? "" : " (off)") + (Strength < 1 ? " " + Math.Round(Strength * 100) + "%" : ""); }
    }

    /// <summary>Validity stamp of a filtered output: the filter-stack revision and the revision of the inputs the output reads.
    /// Equal stamps mean equal output.</summary>
    public readonly struct FilterStamp : IEquatable<FilterStamp>
    {
        /// <summary>Maps: the document's generator-input revision when the stack has a generator (its output also depends on the
        /// texture set's mesh maps), 0 otherwise. Anchors: when the stack has anchor generators, the sum of the versions of what they read
        /// over the tiles concerned (each version only ever grows, so the sum changes whenever any of them does), 0 otherwise.</summary>
        public readonly long Filters, Input, Maps, Anchors;
        public FilterStamp(long filters, long input) : this(filters, input, 0, 0) { }
        public FilterStamp(long filters, long input, long maps) : this(filters, input, maps, 0) { }
        public FilterStamp(long filters, long input, long maps, long anchors) { Filters = filters; Input = input; Maps = maps; Anchors = anchors; }
        public bool Equals(FilterStamp other) { return Filters == other.Filters && Input == other.Input && Maps == other.Maps && Anchors == other.Anchors; }
        public override bool Equals(object obj) { return obj is FilterStamp && Equals((FilterStamp)obj); }
        public override int GetHashCode() { unchecked { return ((Filters.GetHashCode() * 397 ^ Input.GetHashCode()) * 31 ^ Maps.GetHashCode()) * 17 ^ Anchors.GetHashCode(); } }
        public override string ToString() { return (Maps == 0 ? Filters + ":" + Input : Filters + ":" + Input + ":m" + Maps) + (Anchors == 0 ? "" : ":a" + Anchors); }
    }
}
