using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core.Paths
{
    /// <summary>パスの制御点: スナップショットの三角形と、その中の重心座標（A の重みは 1 − U − V）、筆圧。3D の位置は持たず、
    /// 今のスナップショット（ポーズ）の三角形から求める。</summary>
    public readonly struct PathPoint : IEquatable<PathPoint>
    {
        public readonly int Triangle;
        public readonly double U, V, Pressure;
        public PathPoint(int triangle, double u, double v, double pressure = 1)
        {
            MathUtil.RequireFinite(u, nameof(u)); MathUtil.RequireFinite(v, nameof(v)); MathUtil.RequireFinite(pressure, nameof(pressure));
            if (triangle < 0) throw new ArgumentOutOfRangeException(nameof(triangle));
            if (u < -1e-9 || v < -1e-9 || u + v > 1 + 1e-9) throw new ArgumentOutOfRangeException("barycentric", "The point must lie in its triangle.");
            if (pressure < 0 || pressure > 1) throw new ArgumentOutOfRangeException(nameof(pressure));
            Triangle = triangle; U = Math.Max(0, u); V = Math.Max(0, v); Pressure = pressure;
        }
        public bool Equals(PathPoint other) => Triangle == other.Triangle && U == other.U && V == other.V && Pressure == other.Pressure;
        public override bool Equals(object obj) => obj is PathPoint other && Equals(other);
        public override int GetHashCode() { unchecked { return ((Triangle * 397) ^ U.GetHashCode()) * 397 ^ V.GetHashCode(); } }
    }

    /// <summary>パスを描くブラシ（3D ビューの面のブラシと同じ丸い筆先）。半径はモデルの空間の長さ。</summary>
    public sealed class PathBrush : IEquatable<PathBrush>
    {
        public double RadiusWorld = .01, Hardness = .8, Spacing = .15, Opacity = 1, Flow = 1;
        public Rgba32 Color = new Rgba32(255, 255, 255);
        public bool Erase, PressureSize = true, PressureOpacity = true, PressureFlow;
        public void Validate()
        {
            foreach (var v in new[] { RadiusWorld, Hardness, Spacing, Opacity, Flow }) MathUtil.RequireFinite(v, "path brush");
            if (RadiusWorld <= 0 || RadiusWorld > 1e6) throw new ArgumentOutOfRangeException(nameof(RadiusWorld));
            if (Hardness < 0 || Hardness > 1 || Opacity < 0 || Opacity > 1 || Flow < 0 || Flow > 1) throw new ArgumentOutOfRangeException("path brush");
            if (Spacing < .01 || Spacing > 4) throw new ArgumentOutOfRangeException(nameof(Spacing));
        }
        public PathBrush Clone() => (PathBrush)MemberwiseClone();
        /// <summary>この筆で 1 つのダブを塗るときのストロークの設定（色・不透明度・流量・筆圧の割り当て）。</summary>
        public BrushSettings StrokeSettings() => new BrushSettings { Radius = 1, Hardness = Hardness, Spacing = Spacing, Opacity = Opacity, Flow = Flow, Color = Color, Erase = Erase,
            PressureSize = PressureSize, PressureOpacity = PressureOpacity, PressureFlow = PressureFlow };
        public bool Equals(PathBrush o) => o != null && RadiusWorld == o.RadiusWorld && Hardness == o.Hardness && Spacing == o.Spacing && Opacity == o.Opacity && Flow == o.Flow
            && Color == o.Color && Erase == o.Erase && PressureSize == o.PressureSize && PressureOpacity == o.PressureOpacity && PressureFlow == o.PressureFlow;
        public override bool Equals(object obj) => Equals(obj as PathBrush);
        public override int GetHashCode() => RadiusWorld.GetHashCode() ^ Color.GetHashCode();
    }

    /// <summary>
    /// 編集できる 3D の筆跡（仕様 13「3Dパスの編集契約」）。制御点は三角形と重心座標で面に結び付き、どのモデルのスナップショットに
    /// 結び付いているかを ModelFingerprint（三角形の並び・UV・スロット。ポーズでは変わらない）で持つ。層の画素はこのパスから描いた
    /// 結果で、制御点を変えると描き直す。作ったあとは変えない（変えるときは With… で新しいものを作る）ので、Undo で持てる。
    /// </summary>
    public sealed class SurfacePath
    {
        public const int AlgorithmVersion = 1;
        public const int MaxPoints = 4096;
        public Guid Id { get; }
        public PaintChannel Channel { get; }
        public string ModelFingerprint { get; }
        public PathBrush Brush { get; }
        public IReadOnlyList<PathPoint> Points { get; }

        public SurfacePath(Guid id, PaintChannel channel, string modelFingerprint, PathBrush brush, IEnumerable<PathPoint> points)
        {
            PaintLayer.ValidateChannel(channel);
            if (string.IsNullOrEmpty(modelFingerprint) || modelFingerprint.Length > 128) throw new ArgumentException("A model fingerprint is required.", nameof(modelFingerprint));
            if (brush == null) throw new ArgumentNullException(nameof(brush));
            brush.Validate();
            var list = new List<PathPoint>(points ?? throw new ArgumentNullException(nameof(points)));
            if (list.Count > MaxPoints) throw new ArgumentException("A path has at most " + MaxPoints + " points.", nameof(points));
            Id = id; Channel = channel; ModelFingerprint = modelFingerprint; Brush = brush.Clone(); Points = list.AsReadOnly();
        }
        public SurfacePath WithPoints(IEnumerable<PathPoint> points) => new SurfacePath(Id, Channel, ModelFingerprint, Brush, points);
        public SurfacePath WithBrush(PathBrush brush) => new SurfacePath(Id, Channel, ModelFingerprint, brush, Points);
    }
}
