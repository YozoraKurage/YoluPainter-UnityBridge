using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Built-in generators: values made from the texture set's baked mesh maps and parameters. Values are stored in the
    /// native format; append only.</summary>
    public enum GeneratorType { EdgeWear = 0, Dirt = 1, PositionGradient = 2, Thickness = 3, Direction = 4, ShapeGradient = 5, IdColor = 6 }

    /// <summary>How a generator's value g is combined with its stage input s. On a mask s is the visibility (1 = the layer shows,
    /// 1 − the stored hide amount), on layer pixels the channel value. Values are stored in the native format; append only.</summary>
    public enum GeneratorBlend { Multiply = 0, Replace = 1, Screen = 2, Max = 3, Min = 4, Add = 5, Subtract = 6 }

    /// <summary>Where the breakup noise is placed: on the model (3D value noise at the baked Position, so it is continuous across UV
    /// seams; needs the Position map) or in UV space (needs no map; jumps where UV islands meet).</summary>
    public enum GeneratorNoiseSpace { Model = 0, Uv = 1 }

    /// <summary>
    /// Immutable parameters of one generator. A generator is a stage of a layer's filter stack (<see cref="FilterType.Generator"/>):
    /// it computes a value g in 0..1 per pixel from baked mesh maps of the document's texture set (read through
    /// <see cref="IGeneratorInputs"/>) and combines it with the stage input. Formulas, per pixel, with map values in 0..1:
    /// <list type="number">
    /// <item>Base value b:
    /// <list type="bullet">
    /// <item>EdgeWear (Curvature, 0.5 = flat): b = clamp(2 (c − 0.5)), the convex part.</item>
    /// <item>Dirt (AmbientOcclusion and Curvature): b = (1 − Balance) (1 − ao) + Balance · clamp(2 (0.5 − c)): occlusion and the
    /// concave part. Balance 0 reads only the AO map, 1 only the curvature map.</item>
    /// <item>PositionGradient (Position, normalized to the model's bounding box): b = the position along <see cref="Axis"/>, 0 at the
    /// box's minimum, 1 at its maximum.</item>
    /// <item>Thickness (Thickness): b = t (0 = thin, 1 = at or beyond the bake's maximum distance).</item>
    /// <item>Direction (WorldNormal, or BentNormal when <see cref="UseBentNormal"/>): b = (n · d + 1) / 2 with n the renormalized
    /// world normal and d the normalized <see cref="DirectionX"/>, Y, Z: 1 facing d, 0.5 at right angles, 0 facing away.</item>
    /// <item>ShapeGradient (Position): the texel's point on the model, read back from the Position map with the bake's bounding box
    /// (p = min + v (max − min)) and moved into the model root's space with the document's <see cref="GeneratorModelFrame"/>, gets
    /// the base value of <see cref="Volume"/> (a box, sphere or plane; see <see cref="ShapeVolume"/>).</item>
    /// <item>IdColor (Id): b = 1 where the ID map's colour (8-bit RGB, see <see cref="IdMapColors"/>) is within <see cref="IdTolerance"/>
    /// (largest channel difference) of one of <see cref="IdColors"/>, else 0 (Substance Painter's colour selection as a mask). Without
    /// colours the stage passes its input through and says so.</item>
    /// </list></item>
    /// <item>Levels: t = clamp((b − Low) / (High − Low)), then t + Softness (t² (3 − 2t) − t) (0 = linear ramp, 1 = smoothstep),
    /// then 1 − t when <see cref="Invert"/>.</item>
    /// <item>Breakup: g = t (1 − <see cref="NoiseAmount"/> m), so the noise only wears the result away in patches (where t is 0 it
    /// stays 0; amount 1 removes it where m = 1). m = smoothstep(clamp((n − 0.3) / 0.4)) stretches n, fractal value noise
    /// (<see cref="NoiseOctaves"/> octaves, each twice the frequency and half the weight of the one before, normalized to 0..1, so
    /// mostly 0.3..0.7) at q / <see cref="NoiseScale"/>, seeded by <see cref="NoiseSeed"/>. Model space: q = the baked Position mapped
    /// back to the bounding box and divided by its diagonal (isotropic; the scale is a fraction of the diagonal, like the bake
    /// distances). UV space: q = (u, v, 0) of the pixel centre. Without breakup g = t.</item>
    /// <item>Combine (<see cref="Blend"/>) with the stage input s and mix by the effect's strength: out = s + strength (combine(s, g) − s),
    /// rounded half up to 8 bits once. Mask stages work in visibility (s = 1 − hide); layer pixels per colour component with alpha
    /// unchanged, and fully transparent pixels keep their RGB.</item>
    /// </list>
    /// Where a map it reads has no data (texels the bake left Empty: outside every UV island and its padding), the stage passes its
    /// input through. When a map it needs is missing, stale, unverified, of another size or other than the bake it is pinned to,
    /// the whole stage passes its input through and <see cref="PaintDocument.GetGeneratorStatus(GeneratorSettings)"/> says why; it never reads a missing
    /// map as black. Formulas are this tool's native definitions; they are not claimed to match Substance Painter's generators.
    /// </summary>
    public sealed class GeneratorSettings : IEquatable<GeneratorSettings>
    {
        public const double MinLevelRange = 0.001, MinNoiseScale = 0.001, MaxNoiseScale = 1, MaxDirectionComponent = 1e6;
        public const int NoiseOctaves = 4;
        /// <summary>IdColor: at most this many colours (the native format bounds the list).</summary>
        public const int MaxIdColors = 32;
        static readonly IReadOnlyList<int> NoColors = Array.AsReadOnly(new int[0]);
        const double DefaultBalance = .5; const int DefaultAxis = 1;
        static readonly IReadOnlyDictionary<MeshMapKind, string> NoPins = new ReadOnlyDictionary<MeshMapKind, string>(new Dictionary<MeshMapKind, string>());

        public GeneratorType Type { get; private set; }
        /// <summary>Levels: base values at or below Low give 0, at or above High give 1 (before <see cref="Invert"/>).</summary>
        public double Low { get; private set; }
        public double High { get; private set; }
        /// <summary>0 = linear ramp between Low and High, 1 = smoothstep.</summary>
        public double Softness { get; private set; }
        public bool Invert { get; private set; }
        /// <summary>Breakup: how much (0..1) of the result the noise wears away where it is strongest.</summary>
        public double NoiseAmount { get; private set; }
        /// <summary>Breakup: the size of the noise's largest features, as a fraction of the bounding-box diagonal (Model) or of the
        /// UV square (Uv).</summary>
        public double NoiseScale { get; private set; }
        public int NoiseSeed { get; private set; }
        public GeneratorNoiseSpace NoiseSpace { get; private set; }
        public GeneratorBlend Blend { get; private set; }
        /// <summary>Dirt: 0 = ambient occlusion only, 1 = cavities (concave curvature) only.</summary>
        public double Balance { get; private set; }
        /// <summary>PositionGradient: 0 = X, 1 = Y, 2 = Z (the bake's snapshot space: world axes, origin at the model's root).</summary>
        public int Axis { get; private set; }
        /// <summary>Direction: the direction the generator looks for (any length above zero; normalized when evaluated).</summary>
        public double DirectionX { get; private set; }
        public double DirectionY { get; private set; }
        public double DirectionZ { get; private set; }
        /// <summary>Direction: read the bent normal (unoccluded directions) instead of the world normal.</summary>
        public bool UseBentNormal { get; private set; }
        /// <summary>Maps that must come from one particular bake: kind → that bake's <see cref="MeshMapProvenance.ConditionKey"/>.
        /// A kind without a pin uses whatever map of that kind is current for the texture set (a rebake under other conditions is
        /// used as soon as it exists). A pinned kind refuses any other bake, so the node says exactly which result it reads.</summary>
        public IReadOnlyDictionary<MeshMapKind, string> Pins { get; private set; }
        /// <summary>ShapeGradient: the shape and where it is in the model root's space. Other types keep <see cref="ShapeVolume.Default"/>.</summary>
        public ShapeVolume Volume { get; private set; }
        /// <summary>IdColor: the ID colours (0xRRGGBB) that give 1, in the order they were added; no repeats. Other types keep none.</summary>
        public IReadOnlyList<int> IdColors { get; private set; }
        /// <summary>IdColor: how far (largest 8-bit channel difference, 0..255) a texel's ID colour may be from a listed colour. Other types keep
        /// <see cref="IdMapColors.DefaultTolerance"/>.</summary>
        public int IdTolerance { get; private set; }

        GeneratorSettings(GeneratorType type)
        {
            Type = type; High = 1; NoiseScale = .05; Balance = DefaultBalance; Axis = DefaultAxis; DirectionY = 1; Pins = NoPins; Volume = ShapeVolume.Default;
            IdColors = NoColors; IdTolerance = IdMapColors.DefaultTolerance;
        }

        /// <summary>The settings a newly added generator of the type starts with.</summary>
        public static GeneratorSettings Default(GeneratorType type)
        {
            var g = new GeneratorSettings(type);
            switch (type)
            {
                case GeneratorType.EdgeWear: g.Low = .04; g.High = .3; g.Softness = .5; g.NoiseAmount = .6; break;
                case GeneratorType.Dirt: g.Low = .15; g.High = .6; g.Softness = .5; g.NoiseAmount = .4; break;
                case GeneratorType.Direction: g.Low = .6; g.High = .95; g.Softness = .5; g.NoiseAmount = .3; break;
                default: break; // PositionGradient, Thickness, ShapeGradient, IdColor: the map (the shape's value, the match) as it is
            }
            return Checked(g);
        }

        /// <summary>Settings from stored values (loaders). Parameters the type does not use must be at their defaults.</summary>
        public static GeneratorSettings FromValues(GeneratorType type, double low, double high, double softness, bool invert, double noiseAmount, double noiseScale, int noiseSeed,
            GeneratorNoiseSpace noiseSpace, GeneratorBlend blend, double balance, int axis, double directionX, double directionY, double directionZ, bool useBentNormal,
            IEnumerable<KeyValuePair<MeshMapKind, string>> pins)
            => FromValues(type, low, high, softness, invert, noiseAmount, noiseScale, noiseSeed, noiseSpace, blend, balance, axis, directionX, directionY, directionZ, useBentNormal, pins, ShapeVolume.Default);
        /// <summary>Settings from stored values with the shape gradient's volume (<see cref="ShapeVolume.Default"/> for other types).</summary>
        public static GeneratorSettings FromValues(GeneratorType type, double low, double high, double softness, bool invert, double noiseAmount, double noiseScale, int noiseSeed,
            GeneratorNoiseSpace noiseSpace, GeneratorBlend blend, double balance, int axis, double directionX, double directionY, double directionZ, bool useBentNormal,
            IEnumerable<KeyValuePair<MeshMapKind, string>> pins, ShapeVolume volume)
            => FromValues(type, low, high, softness, invert, noiseAmount, noiseScale, noiseSeed, noiseSpace, blend, balance, axis, directionX, directionY, directionZ, useBentNormal, pins, volume,
                null, IdMapColors.DefaultTolerance);
        /// <summary>Settings from stored values with the ID colours and their tolerance (none and the default for other types).</summary>
        public static GeneratorSettings FromValues(GeneratorType type, double low, double high, double softness, bool invert, double noiseAmount, double noiseScale, int noiseSeed,
            GeneratorNoiseSpace noiseSpace, GeneratorBlend blend, double balance, int axis, double directionX, double directionY, double directionZ, bool useBentNormal,
            IEnumerable<KeyValuePair<MeshMapKind, string>> pins, ShapeVolume volume, IEnumerable<int> idColors, int idTolerance)
        {
            var g = new GeneratorSettings(type)
            {
                Low = low, High = high, Softness = softness, Invert = invert, NoiseAmount = noiseAmount, NoiseScale = noiseScale, NoiseSeed = noiseSeed, NoiseSpace = noiseSpace,
                Blend = blend, Balance = balance, Axis = axis, DirectionX = directionX, DirectionY = directionY, DirectionZ = directionZ, UseBentNormal = useBentNormal, Volume = volume,
                IdColors = ColorList(idColors), IdTolerance = idTolerance,
            };
            g.Pins = PinTable(pins);
            return Checked(g);
        }

        public GeneratorSettings WithLevels(double low, double high, double softness) { var g = Copy(); g.Low = low; g.High = high; g.Softness = softness; return Checked(g); }
        public GeneratorSettings WithInvert(bool value) { var g = Copy(); g.Invert = value; return Checked(g); }
        public GeneratorSettings WithNoise(double amount, double scale, int seed, GeneratorNoiseSpace space)
        { var g = Copy(); g.NoiseAmount = amount; g.NoiseScale = scale; g.NoiseSeed = seed; g.NoiseSpace = space; return Checked(g); }
        public GeneratorSettings WithBlend(GeneratorBlend value) { var g = Copy(); g.Blend = value; return Checked(g); }
        public GeneratorSettings WithBalance(double value) { var g = Copy(); g.Balance = value; return Checked(g); }
        public GeneratorSettings WithAxis(int value) { var g = Copy(); g.Axis = value; return Checked(g); }
        public GeneratorSettings WithDirection(double x, double y, double z) { var g = Copy(); g.DirectionX = x; g.DirectionY = y; g.DirectionZ = z; return Checked(g); }
        public GeneratorSettings WithBentNormal(bool value) { var g = Copy(); g.UseBentNormal = value; return Checked(g); }
        /// <summary>ShapeGradient: another shape or placement.</summary>
        public GeneratorSettings WithVolume(ShapeVolume value) { var g = Copy(); g.Volume = value; return Checked(g); }
        /// <summary>IdColor: another list of colours (0xRRGGBB, no repeats, at most <see cref="MaxIdColors"/>).</summary>
        public GeneratorSettings WithIdColors(IEnumerable<int> colors) { var g = Copy(); g.IdColors = ColorList(colors); return Checked(g); }
        /// <summary>IdColor: the colour added at the end (unchanged if it is listed already).</summary>
        public GeneratorSettings WithIdColorAdded(int rgb) => IdColors.Contains(rgb) ? Checked(Copy()) : WithIdColors(IdColors.Concat(new[] { rgb }));
        /// <summary>IdColor: the colour taken out (unchanged if it is not listed).</summary>
        public GeneratorSettings WithIdColorRemoved(int rgb) => WithIdColors(IdColors.Where(c => c != rgb));
        /// <summary>IdColor: another tolerance (0..255).</summary>
        public GeneratorSettings WithIdTolerance(int value) { var g = Copy(); g.IdTolerance = value; return Checked(g); }
        /// <summary>Pins the kind to one bake (its condition key), or follows the current map again when key is null.</summary>
        public GeneratorSettings WithPin(MeshMapKind kind, string key)
        {
            var pins = new Dictionary<MeshMapKind, string>(); foreach (var p in Pins) pins[p.Key] = p.Value;
            if (key == null) pins.Remove(kind); else pins[kind] = key;
            var g = Copy(); g.Pins = PinTable(pins); return Checked(g);
        }
        /// <summary>Follows the current maps for every kind.</summary>
        public GeneratorSettings WithoutPins() { var g = Copy(); g.Pins = NoPins; return Checked(g); }

        GeneratorSettings Copy()
        {
            return new GeneratorSettings(Type)
            {
                Low = Low, High = High, Softness = Softness, Invert = Invert, NoiseAmount = NoiseAmount, NoiseScale = NoiseScale, NoiseSeed = NoiseSeed, NoiseSpace = NoiseSpace,
                Blend = Blend, Balance = Balance, Axis = Axis, DirectionX = DirectionX, DirectionY = DirectionY, DirectionZ = DirectionZ, UseBentNormal = UseBentNormal, Pins = Pins,
                Volume = Volume, IdColors = IdColors, IdTolerance = IdTolerance,
            };
        }
        static IReadOnlyList<int> ColorList(IEnumerable<int> colors)
        {
            if (colors == null) return NoColors;
            var list = colors.ToList();
            return list.Count == 0 ? NoColors : list.AsReadOnly();
        }
        static IReadOnlyDictionary<MeshMapKind, string> PinTable(IEnumerable<KeyValuePair<MeshMapKind, string>> pins)
        {
            if (pins == null) return NoPins;
            var table = new Dictionary<MeshMapKind, string>();
            foreach (var p in pins)
            {
                if (table.ContainsKey(p.Key)) throw new ArgumentException("A map kind is pinned twice.", nameof(pins));
                table.Add(p.Key, p.Value);
            }
            return table.Count == 0 ? NoPins : new ReadOnlyDictionary<MeshMapKind, string>(table);
        }
        static GeneratorSettings Checked(GeneratorSettings g) { g.Validate(); return g; }

        void Validate()
        {
            if (!Enum.IsDefined(typeof(GeneratorType), Type)) throw new ArgumentOutOfRangeException(nameof(Type), "Unknown generator type " + (int)Type + ".");
            foreach (double v in new[] { Low, High, Softness, NoiseAmount, NoiseScale, Balance, DirectionX, DirectionY, DirectionZ }) MathUtil.RequireFinite(v, "generator");
            if (Low < 0 || Low > 1 || High < 0 || High > 1 || High - Low < MinLevelRange)
                throw new ArgumentOutOfRangeException("levels", "Generator levels need 0 ≤ low < high ≤ 1 with high − low at least " + MinLevelRange.ToString(CultureInfo.InvariantCulture) + ".");
            if (Softness < 0 || Softness > 1) throw new ArgumentOutOfRangeException(nameof(Softness), "Softness must be 0..1.");
            if (NoiseAmount < 0 || NoiseAmount > 1) throw new ArgumentOutOfRangeException(nameof(NoiseAmount), "Breakup amount must be 0..1.");
            if (NoiseScale < MinNoiseScale || NoiseScale > MaxNoiseScale) throw new ArgumentOutOfRangeException(nameof(NoiseScale), "Breakup scale must be " + MinNoiseScale.ToString(CultureInfo.InvariantCulture) + "..1.");
            if (!Enum.IsDefined(typeof(GeneratorNoiseSpace), NoiseSpace)) throw new ArgumentOutOfRangeException(nameof(NoiseSpace));
            if (!Enum.IsDefined(typeof(GeneratorBlend), Blend)) throw new ArgumentOutOfRangeException(nameof(Blend), "Unknown generator blend " + (int)Blend + ".");
            if (Type == GeneratorType.Dirt) { if (Balance < 0 || Balance > 1) throw new ArgumentOutOfRangeException(nameof(Balance), "Balance must be 0..1."); }
            else if (Balance != DefaultBalance) throw new ArgumentException("Balance belongs to the dirt generator.", nameof(Balance));
            if (Type == GeneratorType.PositionGradient) { if (Axis < 0 || Axis > 2) throw new ArgumentOutOfRangeException(nameof(Axis), "Axis must be 0 (X), 1 (Y) or 2 (Z)."); }
            else if (Axis != DefaultAxis) throw new ArgumentException("The axis belongs to the position gradient.", nameof(Axis));
            if (Type == GeneratorType.Direction)
            {
                if (Math.Abs(DirectionX) > MaxDirectionComponent || Math.Abs(DirectionY) > MaxDirectionComponent || Math.Abs(DirectionZ) > MaxDirectionComponent)
                    throw new ArgumentOutOfRangeException("direction", "Direction components must be within ±" + MaxDirectionComponent.ToString(CultureInfo.InvariantCulture) + ".");
                if (DirectionX * DirectionX + DirectionY * DirectionY + DirectionZ * DirectionZ < 1e-12) throw new ArgumentOutOfRangeException("direction", "The direction must not be zero.");
            }
            else if (DirectionX != 0 || DirectionY != 1 || DirectionZ != 0 || UseBentNormal) throw new ArgumentException("The direction belongs to the direction generator.", "direction");
            if (Type == GeneratorType.ShapeGradient) { string why = Volume.Refusal(); if (why != null) throw new ArgumentOutOfRangeException("volume", why); }
            else if (!Volume.Equals(ShapeVolume.Default)) throw new ArgumentException("The shape belongs to the shape gradient.", "volume");
            if (Type == GeneratorType.IdColor)
            {
                if (IdColors.Count > MaxIdColors) throw new ArgumentOutOfRangeException(nameof(IdColors), "At most " + MaxIdColors + " ID colours.");
                foreach (int c in IdColors) if (c < 0 || c > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(IdColors), "ID colours are 0xRRGGBB (0–0xFFFFFF).");
                if (IdColors.Distinct().Count() != IdColors.Count) throw new ArgumentException("An ID colour is listed twice.", nameof(IdColors));
                if (IdTolerance < 0 || IdTolerance > IdMapColors.MaxTolerance) throw new ArgumentOutOfRangeException(nameof(IdTolerance), "The tolerance must be 0–" + IdMapColors.MaxTolerance + ".");
            }
            else if (IdColors.Count != 0 || IdTolerance != IdMapColors.DefaultTolerance) throw new ArgumentException("ID colours belong to the ID colour generator.", nameof(IdColors));
            var candidates = CandidateMaps(Type);
            foreach (var pin in Pins)
            {
                if (!candidates.Contains(pin.Key)) throw new ArgumentException(Name + " never reads the " + pin.Key + " map, so it cannot be pinned.", nameof(Pins));
                if (!IsConditionKey(pin.Value)) throw new ArgumentException("A pin is the 64 lower-case hexadecimal digits of a bake's condition key.", nameof(Pins));
            }
        }
        static bool IsConditionKey(string key)
        {
            if (key == null || key.Length != 64) return false;
            foreach (char c in key) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }

        /// <summary>Version of the type's formula. Stored with every generator; a reader refuses versions it does not implement.</summary>
        public int AlgorithmVersion { get { return AlgorithmVersionOf(Type); } }
        public static int AlgorithmVersionOf(GeneratorType type) { return 1; }

        /// <summary>Every map kind the type can read (whatever its parameters): what it may be pinned to.</summary>
        public static IReadOnlyList<MeshMapKind> CandidateMaps(GeneratorType type)
        {
            switch (type)
            {
                case GeneratorType.EdgeWear: return new[] { MeshMapKind.Curvature, MeshMapKind.Position };
                case GeneratorType.Dirt: return new[] { MeshMapKind.AmbientOcclusion, MeshMapKind.Curvature, MeshMapKind.Position };
                case GeneratorType.PositionGradient: return new[] { MeshMapKind.Position };
                case GeneratorType.Thickness: return new[] { MeshMapKind.Thickness, MeshMapKind.Position };
                case GeneratorType.ShapeGradient: return new[] { MeshMapKind.Position };
                case GeneratorType.IdColor: return new[] { MeshMapKind.Id, MeshMapKind.Position };
                default: return new[] { MeshMapKind.WorldNormal, MeshMapKind.BentNormal, MeshMapKind.Position };
            }
        }
        /// <summary>The maps these settings read, in a fixed order: the base value's maps, then Position when the breakup is placed on
        /// the model. Every one must be usable for the generator to have an effect.</summary>
        public IReadOnlyList<MeshMapKind> UsedMaps
        {
            get
            {
                var kinds = new List<MeshMapKind>(3);
                switch (Type)
                {
                    case GeneratorType.EdgeWear: kinds.Add(MeshMapKind.Curvature); break;
                    case GeneratorType.Dirt:
                        if (Balance < 1) kinds.Add(MeshMapKind.AmbientOcclusion);
                        if (Balance > 0) kinds.Add(MeshMapKind.Curvature);
                        break;
                    case GeneratorType.PositionGradient: kinds.Add(MeshMapKind.Position); break;
                    case GeneratorType.Thickness: kinds.Add(MeshMapKind.Thickness); break;
                    case GeneratorType.ShapeGradient: kinds.Add(MeshMapKind.Position); break;
                    case GeneratorType.IdColor: kinds.Add(MeshMapKind.Id); break;
                    default: kinds.Add(UseBentNormal ? MeshMapKind.BentNormal : MeshMapKind.WorldNormal); break;
                }
                if (NoiseAmount > 0 && NoiseSpace == GeneratorNoiseSpace.Model && !kinds.Contains(MeshMapKind.Position)) kinds.Add(MeshMapKind.Position);
                return kinds.AsReadOnly();
            }
        }

        /// <summary>Short display name.</summary>
        public string Name
        {
            get
            {
                switch (Type)
                {
                    case GeneratorType.EdgeWear: return "Edge wear";
                    case GeneratorType.Dirt: return "Dirt";
                    case GeneratorType.PositionGradient: return "Position gradient";
                    case GeneratorType.Thickness: return "Thickness";
                    case GeneratorType.ShapeGradient: return "Shape gradient";
                    case GeneratorType.IdColor: return "ID color";
                    default: return "Direction";
                }
            }
        }

        public bool Equals(GeneratorSettings other)
        {
            if (other == null || Type != other.Type || Low != other.Low || High != other.High || Softness != other.Softness || Invert != other.Invert || NoiseAmount != other.NoiseAmount
                || NoiseScale != other.NoiseScale || NoiseSeed != other.NoiseSeed || NoiseSpace != other.NoiseSpace || Blend != other.Blend || Balance != other.Balance || Axis != other.Axis
                || DirectionX != other.DirectionX || DirectionY != other.DirectionY || DirectionZ != other.DirectionZ || UseBentNormal != other.UseBentNormal || Pins.Count != other.Pins.Count
                || !Volume.Equals(other.Volume) || IdTolerance != other.IdTolerance || !IdColors.SequenceEqual(other.IdColors)) return false;
            foreach (var p in Pins) if (!other.Pins.TryGetValue(p.Key, out var key) || key != p.Value) return false;
            return true;
        }
        public override bool Equals(object obj) { return Equals(obj as GeneratorSettings); }
        public override int GetHashCode()
        {
            unchecked { return ((int)Type * 397) ^ Low.GetHashCode() ^ (High.GetHashCode() * 7) ^ (NoiseAmount.GetHashCode() * 31) ^ (NoiseSeed * 17) ^ ((int)Blend << 8) ^ Pins.Count ^ (Volume.GetHashCode() * 3) ^ (IdColors.Count << 20) ^ (IdTolerance << 12); }
        }
        public override string ToString()
        {
            var c = CultureInfo.InvariantCulture;
            return string.Format(c, "{0} {1:0.###}–{2:0.###}{3} {4}", Name, Low, High, Invert ? " inverted" : "", Blend) + (Pins.Count > 0 ? " pinned" : "")
                + (Type == GeneratorType.IdColor ? " " + string.Join(",", IdColors.Select(IdMapColors.Hex)) + " ±" + IdTolerance : "");
        }
    }

    /// <summary>
    /// The texture set's baked mesh maps as a document's generators see them. The editor gives each document one (the maps live
    /// with the texture set, outside the document). <see cref="Revision"/> must change whenever an answer of
    /// <see cref="TryGetMap"/> may have changed (a bake, a removed or loaded map, another model or high poly, other bake settings,
    /// another slot); the document asks for it when it composites and reads the maps again only then.
    /// </summary>
    public interface IGeneratorInputs
    {
        long Revision { get; }
        /// <summary>The map of the kind that generators may use for this document now, or false with the reason (missing, stale,
        /// unverified...). Only maps baked under the current conditions may be returned.</summary>
        bool TryGetMap(MeshMapKind kind, out BakedMeshMap map, out string reason);
    }

    /// <summary>One map a generator reads, as the document resolves it now.</summary>
    public sealed class GeneratorMapUse
    {
        public MeshMapKind Kind { get; }
        /// <summary>The map the generator reads, or null when it cannot (see <see cref="Reason"/>).</summary>
        public BakedMeshMap Map { get; }
        /// <summary>Why the map cannot be used, or null.</summary>
        public string Reason { get; }
        /// <summary>The condition key the generator is pinned to for this kind, or null when it follows the current map.</summary>
        public string Pin { get; }
        /// <summary>The map the texture set has now when it is not the one the generator reads (stale, or another bake than the pin).</summary>
        public BakedMeshMap Available { get; }
        internal GeneratorMapUse(MeshMapKind kind, BakedMeshMap map, string reason, string pin, BakedMeshMap available)
        { Kind = kind; Map = map; Reason = reason; Pin = pin; Available = available; }
        public bool Usable { get { return Map != null; } }
    }

    /// <summary>What a generator reads and whether it has an effect: <see cref="Active"/> when every map it uses is usable.
    /// Otherwise the stage passes its input through and <see cref="Reason"/> says why.</summary>
    public sealed class GeneratorStatus
    {
        public IReadOnlyList<GeneratorMapUse> Maps { get; }
        public bool Active { get; }
        public string Reason { get; }
        internal GeneratorStatus(IReadOnlyList<GeneratorMapUse> maps, string extra)
        {
            Maps = maps; var reasons = new List<string>();
            foreach (var m in maps) if (m.Reason != null) reasons.Add(m.Reason);
            if (extra != null) reasons.Add(extra);
            Active = reasons.Count == 0; Reason = Active ? null : string.Join(" ", reasons);
        }
    }

    /// <summary>A generator bound to the maps it reads, ready to evaluate pixels (read-only; workers share it).</summary>
    internal sealed class BoundGenerator
    {
        readonly GeneratorSettings g;
        readonly int width, height;
        readonly ushort[] primary, secondary, position; readonly byte[] primaryCoverage, secondaryCoverage, positionCoverage;
        readonly int axis; readonly double dx, dy, dz, balance, low, range, softness, amount, sx, sy, sz, invW, invH;
        readonly bool noise, uvNoise, invert; readonly uint[] octaveSeeds;
        // ShapeGradient: the Position map's 16-bit values → the shape's space (l = shapeMatrix · v + shapeOffset), and the shape
        readonly double[] shapeMatrix, shapeOffset; readonly ShapeEvaluator shape;
        // IdColor: the colours and the tolerance
        readonly int[] idColors; readonly int idTolerance;

        BoundGenerator(GeneratorSettings g, int width, int height, BakedMeshMap primary, BakedMeshMap secondary, BakedMeshMap position, GeneratorModelFrame frame)
        {
            this.g = g; this.width = width; this.height = height;
            if (primary != null) { this.primary = primary.Data; primaryCoverage = primary.Coverage; }
            if (secondary != null) { this.secondary = secondary.Data; secondaryCoverage = secondary.Coverage; }
            balance = g.Balance; axis = g.Axis; low = g.Low; range = g.High - g.Low; softness = g.Softness; invert = g.Invert; amount = g.NoiseAmount;
            double len = Math.Sqrt(g.DirectionX * g.DirectionX + g.DirectionY * g.DirectionY + g.DirectionZ * g.DirectionZ);
            dx = g.DirectionX / len; dy = g.DirectionY / len; dz = g.DirectionZ / len;
            noise = amount > 0; uvNoise = g.NoiseSpace == GeneratorNoiseSpace.Uv;
            invW = 1.0 / width / g.NoiseScale; invH = 1.0 / height / g.NoiseScale;
            if (noise && !uvNoise)
            {
                this.position = position.Data; positionCoverage = position.Coverage;
                var p = position.Provenance; double ex = p.BoundsMax(0) - p.BoundsMin(0), ey = p.BoundsMax(1) - p.BoundsMin(1), ez = p.BoundsMax(2) - p.BoundsMin(2);
                double diag = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                sx = ex / diag / g.NoiseScale / 65535; sy = ey / diag / g.NoiseScale / 65535; sz = ez / diag / g.NoiseScale / 65535;
            }
            else if (g.Type == GeneratorType.PositionGradient || g.Type == GeneratorType.ShapeGradient) { this.position = position.Data; positionCoverage = position.Coverage; }
            if (g.Type == GeneratorType.ShapeGradient)
            {
                // p = min + v · extent / 65535（スナップショットの空間）→ ルートの空間 R0ᵀ (p − t0) → 形の空間 Rsᵀ (… − c)。まとめて 1 つのアフィン写像に
                var p = position.Provenance; var v = g.Volume; var r0 = frame.RotationMatrix(); var rs = v.RotationMatrix();
                var a = new double[9]; // A = Rsᵀ R0ᵀ
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) a[i * 3 + j] = rs[i] * r0[j * 3] + rs[3 + i] * r0[j * 3 + 1] + rs[6 + i] * r0[j * 3 + 2];
                shapeMatrix = new double[9]; shapeOffset = new double[3];
                double[] min = { p.BoundsMin(0), p.BoundsMin(1), p.BoundsMin(2) }, t0 = { frame.PositionX, frame.PositionY, frame.PositionZ }, c = { v.CenterX, v.CenterY, v.CenterZ };
                var rootCenter = new double[3]; // R0ᵀ t0 + c
                for (int k = 0; k < 3; k++) rootCenter[k] = r0[k] * t0[0] + r0[3 + k] * t0[1] + r0[6 + k] * t0[2] + c[k];
                for (int i = 0; i < 3; i++)
                {
                    double offset = 0;
                    for (int j = 0; j < 3; j++) { shapeMatrix[i * 3 + j] = a[i * 3 + j] * ((p.BoundsMax(j) - p.BoundsMin(j)) / 65535); offset += a[i * 3 + j] * min[j]; }
                    shapeOffset[i] = offset - (rs[i] * rootCenter[0] + rs[3 + i] * rootCenter[1] + rs[6 + i] * rootCenter[2]);
                }
                shape = new ShapeEvaluator(v);
            }
            if (g.Type == GeneratorType.IdColor) { idColors = g.IdColors.ToArray(); idTolerance = g.IdTolerance; }
            octaveSeeds = new uint[GeneratorSettings.NoiseOctaves];
            uint seed = FilterEngine.Hash((uint)g.NoiseSeed ^ 0x9e3779b9U);
            for (int o = 0; o < octaveSeeds.Length; o++) octaveSeeds[o] = FilterEngine.Hash(seed + (uint)o * 0x85ebca6bU);
        }

        /// <summary>Binds the settings to resolved maps (indexed by <see cref="MeshMapKind"/>), or null with the reason when a map it
        /// uses is not there, has another size, is another bake than its pin, or the model has no extent for a model-space breakup.</summary>
        internal static BoundGenerator Bind(GeneratorSettings g, IReadOnlyList<BakedMeshMap> maps, GeneratorModelFrame frame, int width, int height, out string reason)
        {
            reason = null;
            foreach (var kind in g.UsedMaps)
            {
                var map = maps == null || (int)kind >= maps.Count ? null : maps[(int)kind];
                if (map == null) { reason = kind + " map is not available."; return null; }
                if (map.Width != width || map.Height != height) { reason = kind + " map is " + map.Width + "×" + map.Height + ", the document " + width + "×" + height + "."; return null; }
                if (g.Pins.TryGetValue(kind, out var pin) && map.Provenance.ConditionKey != pin) { reason = kind + " map is another bake than the one this generator is pinned to."; return null; }
            }
            BakedMeshMap Get(MeshMapKind k) => maps[(int)k];
            BakedMeshMap primary = null, secondary = null, position = null;
            switch (g.Type)
            {
                case GeneratorType.EdgeWear: primary = Get(MeshMapKind.Curvature); break;
                case GeneratorType.Dirt:
                    if (g.Balance < 1) primary = Get(MeshMapKind.AmbientOcclusion);
                    if (g.Balance > 0) secondary = Get(MeshMapKind.Curvature);
                    break;
                case GeneratorType.PositionGradient: position = Get(MeshMapKind.Position); break;
                case GeneratorType.Thickness: primary = Get(MeshMapKind.Thickness); break;
                case GeneratorType.ShapeGradient:
                    position = Get(MeshMapKind.Position);
                    if (frame == null) { reason = "Where the model root is is not known, so the shape cannot be placed on the model (load the model)."; return null; }
                    break;
                case GeneratorType.IdColor:
                    primary = Get(MeshMapKind.Id);
                    if (g.IdColors.Count == 0) { reason = "No ID colours are chosen yet: pick parts with the eyedropper on the 2D canvas or the 3D view."; return null; }
                    break;
                default: primary = Get(g.UseBentNormal ? MeshMapKind.BentNormal : MeshMapKind.WorldNormal); break;
            }
            if (g.NoiseAmount > 0 && g.NoiseSpace == GeneratorNoiseSpace.Model)
            {
                position = Get(MeshMapKind.Position); var p = position.Provenance; double diag = 0;
                for (int a = 0; a < 3; a++) { double e = p.BoundsMax(a) - p.BoundsMin(a); diag += e * e; }
                if (!(diag > 0)) { reason = "The Position map's bounding box has no size, so the breakup cannot be placed on the model; use UV space."; return null; }
            }
            return new BoundGenerator(g, width, height, primary, secondary, position, frame);
        }

        /// <summary>The generator's value g (0..1) at the pixel, or false where a map it reads has no data (Empty texel).</summary>
        internal bool TryValue(int x, int y, out double value)
        {
            int i = y * width + x; double b;
            value = 0;
            switch (g.Type)
            {
                case GeneratorType.EdgeWear:
                    if (primaryCoverage[i] == 0) return false;
                    b = Clamp01(2 * (primary[i] / 65535.0 - .5));
                    break;
                case GeneratorType.Dirt:
                    b = 0;
                    if (primary != null) { if (primaryCoverage[i] == 0) return false; b += (1 - balance) * (1 - primary[i] / 65535.0); }
                    if (secondary != null) { if (secondaryCoverage[i] == 0) return false; b += balance * Clamp01(2 * (.5 - secondary[i] / 65535.0)); }
                    break;
                case GeneratorType.PositionGradient:
                    if (positionCoverage[i] == 0) return false;
                    b = position[i * 3 + axis] / 65535.0;
                    break;
                case GeneratorType.Thickness:
                    if (primaryCoverage[i] == 0) return false;
                    b = primary[i] / 65535.0;
                    break;
                case GeneratorType.IdColor:
                {
                    if (primaryCoverage[i] == 0) return false;
                    int rgb = IdMapColors.Rgb(primary, i); b = 0;
                    foreach (int c in idColors) if (IdMapColors.Near(rgb, c, idTolerance)) { b = 1; break; }
                    break;
                }
                case GeneratorType.ShapeGradient:
                {
                    if (positionCoverage[i] == 0) return false;
                    double vx = position[i * 3], vy = position[i * 3 + 1], vz = position[i * 3 + 2]; var m = shapeMatrix; var k = shapeOffset;
                    b = shape.Value(m[0] * vx + m[1] * vy + m[2] * vz + k[0], m[3] * vx + m[4] * vy + m[5] * vz + k[1], m[6] * vx + m[7] * vy + m[8] * vz + k[2]);
                    break;
                }
                default:
                {
                    if (primaryCoverage[i] == 0) return false;
                    double nx = primary[i * 3] / 65535.0 * 2 - 1, ny = primary[i * 3 + 1] / 65535.0 * 2 - 1, nz = primary[i * 3 + 2] / 65535.0 * 2 - 1;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    double dot = len > 1e-9 ? (nx * dx + ny * dy + nz * dz) / len : 0;
                    b = Clamp01((dot + 1) * .5);
                    break;
                }
            }
            double t = Clamp01((b - low) / range);
            if (softness > 0) t += softness * (t * t * (3 - 2 * t) - t);
            if (invert) t = 1 - t;
            if (noise)
            {
                double qx, qy, qz;
                if (uvNoise) { qx = (x + .5) * invW; qy = (y + .5) * invH; qz = 0; }
                else
                {
                    if (positionCoverage[i] == 0) return false;
                    qx = position[i * 3] * sx; qy = position[i * 3 + 1] * sy; qz = position[i * 3 + 2] * sz;
                }
                double m = Clamp01((Fractal(qx, qy, qz, octaveSeeds) - .3) / .4);
                t *= 1 - amount * (m * m * (3 - 2 * m));
            }
            value = t;
            return true;
        }

        static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        /// <summary>Fractal value noise in 0..1: octaves of trilinearly interpolated lattice values (quintic fade), each at twice the
        /// frequency and half the weight of the one before, divided by the total weight.</summary>
        internal static double Fractal(double x, double y, double z, uint[] seeds)
        {
            double sum = 0, weight = 1, total = 0;
            for (int o = 0; o < seeds.Length; o++)
            {
                sum += weight * Value(x, y, z, seeds[o]); total += weight;
                x *= 2; y *= 2; z *= 2; weight *= .5;
            }
            return sum / total;
        }
        static double Value(double x, double y, double z, uint seed)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
            int ix = (int)fx, iy = (int)fy, iz = (int)fz;
            double u = Fade(x - fx), v = Fade(y - fy), w = Fade(z - fz);
            uint hx0 = FilterEngine.Hash(seed ^ (uint)ix), hx1 = FilterEngine.Hash(seed ^ (uint)(ix + 1));
            double c00 = Lerp(Lattice(hx0, iy, iz), Lattice(hx1, iy, iz), u), c10 = Lerp(Lattice(hx0, iy + 1, iz), Lattice(hx1, iy + 1, iz), u);
            double c01 = Lerp(Lattice(hx0, iy, iz + 1), Lattice(hx1, iy, iz + 1), u), c11 = Lerp(Lattice(hx0, iy + 1, iz + 1), Lattice(hx1, iy + 1, iz + 1), u);
            return Lerp(Lerp(c00, c10, v), Lerp(c01, c11, v), w);
        }
        static double Lattice(uint hx, int y, int z) { return (FilterEngine.Hash(FilterEngine.Hash(hx ^ (uint)y) ^ (uint)z) >> 8) * (1.0 / 16777215); }
        static double Fade(double t) { return t * t * t * (t * (t * 6 - 15) + 10); }
        static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        /// <summary>combine(s, g) for the blend, then mixed with s by strength.</summary>
        internal static double Combine(GeneratorBlend blend, double s, double v, double strength)
        {
            double c;
            switch (blend)
            {
                case GeneratorBlend.Multiply: c = s * v; break;
                case GeneratorBlend.Replace: c = v; break;
                case GeneratorBlend.Screen: c = 1 - (1 - s) * (1 - v); break;
                case GeneratorBlend.Max: c = s > v ? s : v; break;
                case GeneratorBlend.Min: c = s < v ? s : v; break;
                case GeneratorBlend.Add: c = s + v > 1 ? 1 : s + v; break;
                default: c = s - v < 0 ? 0 : s - v; break;
            }
            return strength >= 1 ? c : s + (c - s) * strength;
        }
    }
}
