using System;
using System.Globalization;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>The shape of a shape gradient (<see cref="GeneratorType.ShapeGradient"/>). Values are stored in the native format;
    /// append only.</summary>
    public enum GeneratorShape { Box = 0, Sphere = 1, Plane = 2 }

    /// <summary>
    /// The volume of a shape gradient: a box, sphere or plane placed in the model root's space (the root object's position and
    /// rotation, in scene units; the root's scale is not applied, like the symmetry plane), and the base value b (0..1) it gives a
    /// point of that space. Rotation is in Euler degrees in Unity's order (Z, then X, then Y: R = Ry · Rx · Rz, the matrix of
    /// <c>Quaternion.Euler(x, y, z)</c>); a point p of the root's space is at l = Rᵀ (p − center) in the shape's space.
    /// <list type="bullet">
    /// <item>Box: Size is the full width along each of the shape's axes (half widths h = Size / 2). The inset d = min over the axes of
    /// (hᵢ − |lᵢ|) is the distance to the nearest face from inside; band = Falloff · min hᵢ. b = 0 where d ≤ 0 (outside, or on a
    /// face), 1 where d ≥ band, d / band between: the shape fades from 1 inside to 0 at its boundary over a band of the same width
    /// all round (Falloff 0: a hard edge, 1: the ramp reaches the middle of the thinnest axis).</item>
    /// <item>Sphere: SizeX is the diameter (SizeY and SizeZ are kept, unused); d = SizeX / 2 − |l|, band = Falloff · SizeX / 2, the
    /// same ramp.</item>
    /// <item>Plane: the plane goes through the centre facing the shape's +Y; SizeY is the width of the ramp (SizeX, SizeZ and Falloff
    /// are kept, unused): b = clamp(0.5 + l_y / SizeY), 0 at SizeY / 2 behind the plane and 1 at SizeY / 2 in front of it.</item>
    /// </list>
    /// Unused values are kept so that switching the shape back restores them. Values are checked by
    /// <see cref="GeneratorSettings"/> (coordinates within ±<see cref="MaxCoordinate"/>, angles within ±<see cref="MaxAngle"/>,
    /// sizes <see cref="MinSize"/>..<see cref="MaxSize"/>, Falloff 0..1).
    /// </summary>
    public readonly struct ShapeVolume : IEquatable<ShapeVolume>
    {
        public const double MaxCoordinate = 1e6, MinSize = 1e-6, MaxSize = 1e6, MaxAngle = 360;
        /// <summary>A unit box at the root with half of it fading (what a generator of another type stores).</summary>
        public static readonly ShapeVolume Default = new ShapeVolume(GeneratorShape.Box, 0, 0, 0, 0, 0, 0, 1, 1, 1, .5);

        public GeneratorShape Shape { get; }
        public double CenterX { get; }
        public double CenterY { get; }
        public double CenterZ { get; }
        /// <summary>Euler degrees (Unity's order: Z, X, Y).</summary>
        public double RotationX { get; }
        public double RotationY { get; }
        public double RotationZ { get; }
        public double SizeX { get; }
        public double SizeY { get; }
        public double SizeZ { get; }
        /// <summary>Box and sphere: the fraction of the half width (box: of the thinnest axis; sphere: of the radius) over which the
        /// value fades from 1 to 0 at the boundary.</summary>
        public double Falloff { get; }

        public ShapeVolume(GeneratorShape shape, double centerX, double centerY, double centerZ, double rotationX, double rotationY, double rotationZ,
            double sizeX, double sizeY, double sizeZ, double falloff)
        {
            Shape = shape; CenterX = centerX; CenterY = centerY; CenterZ = centerZ; RotationX = rotationX; RotationY = rotationY; RotationZ = rotationZ;
            SizeX = sizeX; SizeY = sizeY; SizeZ = sizeZ; Falloff = falloff;
        }

        public ShapeVolume WithShape(GeneratorShape shape) => new ShapeVolume(shape, CenterX, CenterY, CenterZ, RotationX, RotationY, RotationZ, SizeX, SizeY, SizeZ, Falloff);
        public ShapeVolume WithCenter(double x, double y, double z) => new ShapeVolume(Shape, x, y, z, RotationX, RotationY, RotationZ, SizeX, SizeY, SizeZ, Falloff);
        public ShapeVolume WithRotation(double x, double y, double z) => new ShapeVolume(Shape, CenterX, CenterY, CenterZ, x, y, z, SizeX, SizeY, SizeZ, Falloff);
        public ShapeVolume WithSize(double x, double y, double z) => new ShapeVolume(Shape, CenterX, CenterY, CenterZ, RotationX, RotationY, RotationZ, x, y, z, Falloff);
        public ShapeVolume WithFalloff(double falloff) => new ShapeVolume(Shape, CenterX, CenterY, CenterZ, RotationX, RotationY, RotationZ, SizeX, SizeY, SizeZ, falloff);

        /// <summary>Why the values cannot be used, or null.</summary>
        public string Refusal()
        {
            if (!Enum.IsDefined(typeof(GeneratorShape), Shape)) return "Unknown generator shape " + (int)Shape + ".";
            foreach (double v in new[] { CenterX, CenterY, CenterZ, RotationX, RotationY, RotationZ, SizeX, SizeY, SizeZ, Falloff })
                if (double.IsNaN(v) || double.IsInfinity(v)) return "The shape's values must be finite.";
            var c = CultureInfo.InvariantCulture;
            if (Math.Abs(CenterX) > MaxCoordinate || Math.Abs(CenterY) > MaxCoordinate || Math.Abs(CenterZ) > MaxCoordinate)
                return "The shape's centre must be within ±" + MaxCoordinate.ToString(c) + " scene units of the model root.";
            if (Math.Abs(RotationX) > MaxAngle || Math.Abs(RotationY) > MaxAngle || Math.Abs(RotationZ) > MaxAngle)
                return "The shape's rotation angles must be within ±" + MaxAngle.ToString(c) + "°.";
            foreach (double s in new[] { SizeX, SizeY, SizeZ })
                if (!(s >= MinSize && s <= MaxSize)) return "The shape's sizes must be " + MinSize.ToString(c) + " to " + MaxSize.ToString(c) + " scene units.";
            if (Falloff < 0 || Falloff > 1) return "The shape's falloff must be 0..1.";
            return null;
        }

        /// <summary>The rotation as a row-major 3 × 3 matrix (R = Ry · Rx · Rz, Unity's <c>Quaternion.Euler</c>).</summary>
        public double[] RotationMatrix()
        {
            const double ToRadians = Math.PI / 180;
            double ca = Math.Cos(RotationX * ToRadians), sa = Math.Sin(RotationX * ToRadians);
            double cb = Math.Cos(RotationY * ToRadians), sb = Math.Sin(RotationY * ToRadians);
            double cc = Math.Cos(RotationZ * ToRadians), sc = Math.Sin(RotationZ * ToRadians);
            return new[]
            {
                cb * cc + sb * sa * sc, -cb * sc + sb * sa * cc, sb * ca,
                ca * sc, ca * cc, -sa,
                -sb * cc + cb * sa * sc, sb * sc + cb * sa * cc, cb * ca,
            };
        }

        /// <summary>The base value b at a point of the model root's space (see the type's summary).</summary>
        public double ValueAt(double x, double y, double z)
        {
            var r = RotationMatrix(); double px = x - CenterX, py = y - CenterY, pz = z - CenterZ;
            double lx = r[0] * px + r[3] * py + r[6] * pz, ly = r[1] * px + r[4] * py + r[7] * pz, lz = r[2] * px + r[5] * py + r[8] * pz;
            return new ShapeEvaluator(this).Value(lx, ly, lz);
        }

        public bool Equals(ShapeVolume other)
        {
            return Shape == other.Shape && CenterX == other.CenterX && CenterY == other.CenterY && CenterZ == other.CenterZ && RotationX == other.RotationX && RotationY == other.RotationY
                && RotationZ == other.RotationZ && SizeX == other.SizeX && SizeY == other.SizeY && SizeZ == other.SizeZ && Falloff == other.Falloff;
        }
        public override bool Equals(object obj) => obj is ShapeVolume v && Equals(v);
        public override int GetHashCode()
        {
            unchecked { return ((int)Shape * 397) ^ CenterX.GetHashCode() ^ (CenterY.GetHashCode() * 7) ^ (CenterZ.GetHashCode() * 13) ^ (SizeX.GetHashCode() * 31) ^ (RotationY.GetHashCode() * 17) ^ Falloff.GetHashCode(); }
        }
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} at ({1:0.###}, {2:0.###}, {3:0.###}) rotated ({4:0.#}, {5:0.#}, {6:0.#}) size ({7:0.###}, {8:0.###}, {9:0.###}) falloff {10:0.##}",
                Shape, CenterX, CenterY, CenterZ, RotationX, RotationY, RotationZ, SizeX, SizeY, SizeZ, Falloff);
        }
    }

    /// <summary>The base value of a volume in the shape's own space (precomputed half widths and band; read-only, shared by workers).</summary>
    internal sealed class ShapeEvaluator
    {
        readonly GeneratorShape shape; readonly double hx, hy, hz, band, invWidth;

        internal ShapeEvaluator(ShapeVolume v)
        {
            shape = v.Shape; hx = v.SizeX / 2; hy = v.SizeY / 2; hz = v.SizeZ / 2; invWidth = 1 / v.SizeY;
            band = shape == GeneratorShape.Sphere ? v.Falloff * hx : v.Falloff * Math.Min(hx, Math.Min(hy, hz));
        }

        /// <summary>b at the point (lx, ly, lz) of the shape's space.</summary>
        internal double Value(double lx, double ly, double lz)
        {
            double d;
            switch (shape)
            {
                case GeneratorShape.Box:
                {
                    double ax = lx < 0 ? -lx : lx, ay = ly < 0 ? -ly : ly, az = lz < 0 ? -lz : lz;
                    d = hx - ax; double dy = hy - ay, dz = hz - az;
                    if (dy < d) d = dy; if (dz < d) d = dz;
                    break;
                }
                case GeneratorShape.Sphere: d = hx - Math.Sqrt(lx * lx + ly * ly + lz * lz); break;
                default:
                {
                    double t = .5 + ly * invWidth;
                    return t <= 0 ? 0 : t >= 1 ? 1 : t;
                }
            }
            if (d <= 0) return 0;
            return band <= 0 || d >= band ? 1 : d / band;
        }
    }

    /// <summary>
    /// Where the model root is in the space of the baked Position map ("SnapshotWorld": world axes, origin at the root when the
    /// model was loaded): its position and rotation (a unit quaternion). Shape gradients are placed in the root's space, so the
    /// document needs it to read the Position map back into that space. Immutable.
    /// </summary>
    public sealed class GeneratorModelFrame : IEquatable<GeneratorModelFrame>
    {
        /// <summary>The root at the origin with no rotation (what documents use when their inputs do not say).</summary>
        public static readonly GeneratorModelFrame Identity = new GeneratorModelFrame(0, 0, 0, 0, 0, 0, 1);

        public double PositionX { get; }
        public double PositionY { get; }
        public double PositionZ { get; }
        public double RotationX { get; }
        public double RotationY { get; }
        public double RotationZ { get; }
        public double RotationW { get; }

        /// <param name="qx">The rotation as a quaternion (x, y, z, w); it is normalized. A zero or non-finite quaternion is refused.</param>
        public GeneratorModelFrame(double px, double py, double pz, double qx, double qy, double qz, double qw)
        {
            foreach (double v in new[] { px, py, pz, qx, qy, qz, qw }) MathUtil.RequireFinite(v, "frame");
            double length = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (!(length > 1e-9)) throw new ArgumentOutOfRangeException("rotation", "The model root's rotation must not be a zero quaternion.");
            PositionX = px; PositionY = py; PositionZ = pz; RotationX = qx / length; RotationY = qy / length; RotationZ = qz / length; RotationW = qw / length;
        }

        /// <summary>The rotation as a row-major 3 × 3 matrix.</summary>
        public double[] RotationMatrix()
        {
            double x = RotationX, y = RotationY, z = RotationZ, w = RotationW;
            return new[]
            {
                1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
                2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
                2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y),
            };
        }

        public bool Equals(GeneratorModelFrame other)
        {
            return other != null && PositionX == other.PositionX && PositionY == other.PositionY && PositionZ == other.PositionZ
                && RotationX == other.RotationX && RotationY == other.RotationY && RotationZ == other.RotationZ && RotationW == other.RotationW;
        }
        public override bool Equals(object obj) => Equals(obj as GeneratorModelFrame);
        public override int GetHashCode() { unchecked { return PositionX.GetHashCode() ^ (PositionY.GetHashCode() * 7) ^ (RotationW.GetHashCode() * 31) ^ (RotationY.GetHashCode() * 17); } }
    }

    /// <summary>Implemented by <see cref="IGeneratorInputs"/> that know where the model root is (the editor's texture-set inputs).
    /// Inputs that do not implement it give <see cref="GeneratorModelFrame.Identity"/>. Read together with the maps whenever the
    /// inputs' revision changes, so a change of the frame must also change <see cref="IGeneratorInputs.Revision"/>.</summary>
    public interface IGeneratorModelFrame
    {
        /// <summary>The frame, or null when it is not known (shape gradients then pass their input through with a reason).</summary>
        GeneratorModelFrame ModelFrame { get; }
    }
}
