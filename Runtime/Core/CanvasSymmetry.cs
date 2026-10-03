using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    public enum CanvasSymmetryMode { None, Vertical, Horizontal, Both, Radial }

    /// <summary>文書の画素座標での対称。中心はキャンバスの縁からの画素座標（画素の中心は整数 + 0.5）。</summary>
    public sealed class CanvasSymmetrySettings
    {
        public CanvasSymmetryMode Mode;
        public double CenterX, CenterY;
        public int Count = 2;
        public bool Enabled => Mode != CanvasSymmetryMode.None;
        public CanvasSymmetrySettings Clone() => (CanvasSymmetrySettings)MemberwiseClone();
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(CanvasSymmetryMode), Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
            MathUtil.RequireFinite(CenterX, nameof(CenterX)); MathUtil.RequireFinite(CenterY, nameof(CenterY));
            if (Math.Abs(CenterX) > 10000000 || Math.Abs(CenterY) > 10000000) throw new ArgumentOutOfRangeException("symmetry center");
            if (Count < 2 || Count > 16) throw new ArgumentOutOfRangeException(nameof(Count), "Radial symmetry needs 2..16 copies.");
        }
        public IReadOnlyList<CanvasSymmetryTransform> Transforms()
        {
            Validate();
            var result = new List<CanvasSymmetryTransform> { new CanvasSymmetryTransform(CenterX, CenterY, 1, 0, 0, 1) };
            if (Mode == CanvasSymmetryMode.Vertical || Mode == CanvasSymmetryMode.Both) result.Add(new CanvasSymmetryTransform(CenterX, CenterY, -1, 0, 0, 1));
            if (Mode == CanvasSymmetryMode.Horizontal || Mode == CanvasSymmetryMode.Both) result.Add(new CanvasSymmetryTransform(CenterX, CenterY, 1, 0, 0, -1));
            if (Mode == CanvasSymmetryMode.Both) result.Add(new CanvasSymmetryTransform(CenterX, CenterY, -1, 0, 0, -1));
            if (Mode == CanvasSymmetryMode.Radial) for (int i = 1; i < Count; i++)
            {
                // 四分の一周の倍数は画素中心に正確に戻る。
                double c, s;
                if (4 * i % Count == 0)
                {
                    int q = 4 * i / Count; c = q == 2 ? -1 : 0; s = q == 1 ? 1 : q == 3 ? -1 : 0;
                }
                else { double a = 2 * Math.PI * i / Count; c = Math.Cos(a); s = Math.Sin(a); }
                result.Add(new CanvasSymmetryTransform(CenterX, CenterY, c, -s, s, c));
            }
            return result;
        }
    }

    /// <summary>中心のまわりの直交変換。逆写しは転置なので筆先の覆いを補間せず評価できる。</summary>
    public readonly struct CanvasSymmetryTransform
    {
        readonly double cx, cy, a, b, c, d;
        internal CanvasSymmetryTransform(double cx, double cy, double a, double b, double c, double d)
        { this.cx = cx; this.cy = cy; this.a = a; this.b = b; this.c = c; this.d = d; }
        public void Map(double x, double y, out double u, out double v)
        { x -= cx; y -= cy; u = cx + a * x + b * y; v = cy + c * x + d * y; }
        public void Inverse(double u, double v, out double x, out double y)
        { u -= cx; v -= cy; x = cx + a * u + c * v; y = cy + b * u + d * v; }
    }

    public sealed partial class BrushSettings
    {
        public CanvasSymmetrySettings CanvasSymmetry;
        void ValidateCanvasSymmetry()
        {
            CanvasSymmetry?.Validate();
            if (CanvasSymmetry != null && CanvasSymmetry.Enabled && (Effect == BrushEffect.Smudge || Effect == BrushEffect.Clone))
                throw new InvalidOperationException("Smudge and Clone need a separate source and motion for each symmetry copy. Turn off symmetry to use them.");
        }
    }
}
