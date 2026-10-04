using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core.Paths
{
    /// <summary>2D のパスの制御点: キャンバスの画素の座標（左下が原点、画素の中心は +0.5）と筆圧。</summary>
    public readonly struct CanvasPoint : IEquatable<CanvasPoint>
    {
        /// <summary>座標の範囲（キャンバスの外にはみ出した点も持てるが、桁の外れた値は断る）。</summary>
        public const double Limit = 1e6;
        public readonly double X, Y, Pressure;
        public CanvasPoint(double x, double y, double pressure = 1)
        {
            MathUtil.RequireFinite(x, nameof(x)); MathUtil.RequireFinite(y, nameof(y)); MathUtil.RequireFinite(pressure, nameof(pressure));
            if (Math.Abs(x) > Limit || Math.Abs(y) > Limit) throw new ArgumentOutOfRangeException("point", "A path point must lie within ±" + Limit + " px.");
            if (pressure < 0 || pressure > 1) throw new ArgumentOutOfRangeException(nameof(pressure));
            X = x; Y = y; Pressure = pressure;
        }
        public bool Equals(CanvasPoint other) => X == other.X && Y == other.Y && Pressure == other.Pressure;
        public override bool Equals(object obj) => obj is CanvasPoint other && Equals(other);
        public override int GetHashCode() { unchecked { return (X.GetHashCode() * 397) ^ Y.GetHashCode(); } }
    }

    /// <summary>
    /// 編集できる 2D の筆跡（GIMP のパスを筆でなぞる操作を、なぞった結果ではなくパスのまま持つもの）。制御点は画素の座標で、
    /// モデルに依らない。層の画素はこのパスから描いた結果（<see cref="CanvasPathRenderer"/>）。筆の半径は画素。
    /// </summary>
    public sealed class CanvasPath : EditablePath
    {
        public const int AlgorithmVersion = 1;
        public IReadOnlyList<CanvasPoint> Points { get; }
        public override int PointCount => Points.Count;

        public CanvasPath(Guid id, PaintChannel channel, PathBrush brush, IEnumerable<CanvasPoint> points, IEnumerable<ChannelPaint> material = null)
        {
            PaintLayer.ValidateChannel(channel);
            if (brush == null) throw new ArgumentNullException(nameof(brush));
            brush.Validate();
            if (brush.RadiusWorld > 4096) throw new ArgumentOutOfRangeException(nameof(brush), "A canvas path's brush radius is at most 4096 px.");
            var list = new List<CanvasPoint>(points ?? throw new ArgumentNullException(nameof(points)));
            if (list.Count > MaxPointCount) throw new ArgumentException("A path has at most " + MaxPointCount + " points.", nameof(points));
            Id = id; Channel = channel; Brush = brush.Clone(); Material = CopyMaterial(material); Points = list.AsReadOnly();
        }
        public CanvasPath WithPoints(IEnumerable<CanvasPoint> points) => new CanvasPath(Id, Channel, Brush, points, Material);
        public CanvasPath WithBrush(PathBrush brush) => new CanvasPath(Id, Channel, brush, Points, Material);
        public CanvasPath WithMaterial(IEnumerable<ChannelPaint> material) => new CanvasPath(Id, Channel, Brush, Points, material);
    }

    /// <summary>
    /// <see cref="CanvasPath"/> を描く: 制御点を centripetal Catmull-Rom（3D のパスと同じ曲線）で結び、細かく刻んだ折れ線を普通の
    /// 2D のストローク（丸い筆先、間隔・筆圧の補間はストロークのもの）に流す。文書の選択範囲は使わない（描き直しが選択に
    /// 左右されないように）。同じパスと大きさなら同じ画素になる。
    /// </summary>
    public static class CanvasPathRenderer
    {
        /// <summary>曲線の 1 区間を刻む細かさ（筆の間隔の何分の 1 か）と、1 本のパスで刻む点の上限。</summary>
        const double SubdivisionPerSpacing = 4;
        public const int MaxSamples = 4000000;

        public static SparseTileSurface Render(PaintDocument document, CanvasPath path)
            => RenderChannels(document, path)[path.Material == null ? path.Channel : path.Material[0].Channel];

        /// <summary>組全体を同じ入力で描く。作業文書のソースとストロークの予算は組の合計。</summary>
        public static IReadOnlyDictionary<PaintChannel, SparseTileSurface> RenderChannels(PaintDocument document, CanvasPath path)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (path == null) throw new ArgumentNullException(nameof(path));
            var brush = path.Brush;
            var scratch = new PaintDocument(document.Width, document.Height, document.TileSize, 0) { SourceBudgetBytes = document.SourceBudgetBytes, ActiveStrokeBudgetBytes = document.ActiveStrokeBudgetBytes };
            var layer = scratch.AddLayer("path");
            if (!layer.IsChannelEnabled(path.Channel)) scratch.SetChannelEnabled(layer.Id, path.Channel, true);
            var settings = brush.StrokeSettings(); settings.Radius = brush.RadiusWorld;
            using (var stroke = path.Material == null ? scratch.BeginStroke(layer.Id, path.Channel, settings) : scratch.BeginMaterialStroke(layer.Id, path.Material, settings))
            {
                var points = path.Points; int n = points.Count;
                if (n > 0) stroke.Add(new BrushSample(points[0].X, points[0].Y, points[0].Pressure));
                double step = Math.Max(.01, brush.RadiusWorld * 2 * brush.Spacing) / SubdivisionPerSpacing;
                long samples = 0;
                for (int s = 0; s + 1 < n; s++)
                {
                    var p0 = points[Math.Max(0, s - 1)]; var p1 = points[s]; var p2 = points[s + 1]; var p3 = points[Math.Min(n - 1, s + 2)];
                    double chord = Math.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y));
                    int substeps = Math.Max(1, (int)Math.Ceiling(chord / step));
                    samples += substeps;
                    if (samples > MaxSamples) throw new InvalidOperationException("The path is too long for its brush spacing.");
                    for (int k = 1; k <= substeps; k++)
                    {
                        double t = k / (double)substeps;
                        CatmullRom(p0, p1, p2, p3, t, out double x, out double y);
                        stroke.Add(new BrushSample(x, y, p1.Pressure + (p2.Pressure - p1.Pressure) * t));
                    }
                }
                stroke.Commit();
            }
            var rendered = new Dictionary<PaintChannel, SparseTileSurface>();
            foreach (var m in path.Paints) rendered.Add(m.Channel, layer.GetChannel(m.Channel));
            return rendered;
        }

        /// <summary>centripetal Catmull-Rom（α = 0.5）の p1 → p2 の区間の t。重なった点では直線に戻る。</summary>
        internal static void CatmullRom(CanvasPoint p0, CanvasPoint p1, CanvasPoint p2, CanvasPoint p3, double t, out double x, out double y)
        {
            double Knot(CanvasPoint a, CanvasPoint b) => Math.Max(1e-6, Math.Sqrt(Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y))));
            double t0 = 0, t1 = t0 + Knot(p0, p1), t2 = t1 + Knot(p1, p2), t3 = t2 + Knot(p2, p3);
            double u = t1 + (t2 - t1) * t;
            double L(double a, double b, double ta, double tb, out double other, double c, double d) { other = (tb - u) / (tb - ta) * c + (u - ta) / (tb - ta) * d; return (tb - u) / (tb - ta) * a + (u - ta) / (tb - ta) * b; }
            double a1x = L(p0.X, p1.X, t0, t1, out double a1y, p0.Y, p1.Y);
            double a2x = L(p1.X, p2.X, t1, t2, out double a2y, p1.Y, p2.Y);
            double a3x = L(p2.X, p3.X, t2, t3, out double a3y, p2.Y, p3.Y);
            double b1x = L(a1x, a2x, t0, t2, out double b1y, a1y, a2y);
            double b2x = L(a2x, a3x, t1, t3, out double b2y, a2y, a3y);
            x = L(b1x, b2x, t1, t2, out y, b1y, b2y);
        }
    }
}
