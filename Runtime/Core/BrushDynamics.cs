using System;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>デュアルブラシの合わせ方。主の筆先の被覆率 a に、2 つ目の筆先が溜めた被覆率 b を合わせる（Photoshop の
    /// デュアルブラシの描画モードの名前と同じ並び。式はレイヤーの合成モードを被覆率 0〜1 に当てたもの）。どのモードも
    /// a = 0 なら 0 で、主の筆先の外は塗らない。値は保存形式に入るので、並べ替えず末尾に足す。</summary>
    public enum DualBrushMode { Multiply = 0, Darken = 1, Overlay = 2, ColorDodge = 3, ColorBurn = 4, LinearBurn = 5, HardMix = 6, Subtract = 7 }

    /// <summary>デュアルブラシ（2 つ目の筆先）。主の筆先と同じ道筋に自分の間隔・散布・数でダブを置き、ストロークの間
    /// 画素ごとの最大の被覆率を溜める。主のダブは、そのダブの位置（線の長さ）までに置かれた 2 つ目のダブの溜まりと
    /// <see cref="Mode"/> で合わせた被覆率で塗る。大きさは筆圧・ゆらぎ・入り抜きの影響を受けない（Photoshop と同じ）。</summary>
    public sealed class DualBrush
    {
        /// <summary>Sampled tip, or null for the round tip shaped by Hardness.</summary>
        public BrushTip Tip;
        /// <summary>Canvas pixels.</summary>
        public double Radius = 8;
        public double Hardness = 1;
        /// <summary>Interval as a fraction of the dual tip's diameter.</summary>
        public double Spacing = .25;
        /// <summary>Degrees, counter-clockwise (canvas Y up).</summary>
        public double Angle;
        public double Roundness = 1;
        /// <summary>Random offset in dual diameters along both axes.</summary>
        public double Scatter;
        public int Count = 1;
        public DualBrushMode Mode;

        public DualBrush Clone() { return (DualBrush)MemberwiseClone(); }
        public void Validate()
        {
            foreach (double v in new[] { Radius, Hardness, Spacing, Angle, Roundness, Scatter }) MathUtil.RequireFinite(v, "dual brush");
            if (Radius < .5 || Radius > 65536) throw new ArgumentOutOfRangeException(nameof(Radius), "The dual brush radius must be 0.5..65536 pixels.");
            if (Hardness < 0 || Hardness > 1) throw new ArgumentOutOfRangeException(nameof(Hardness));
            if (Spacing < .01 || Spacing > 4) throw new ArgumentOutOfRangeException(nameof(Spacing));
            if (Roundness < .01 || Roundness > 1) throw new ArgumentOutOfRangeException(nameof(Roundness));
            if (Scatter < 0 || Scatter > 10) throw new ArgumentOutOfRangeException(nameof(Scatter));
            if (Count < 1 || Count > 16) throw new ArgumentOutOfRangeException(nameof(Count));
            if (!Enum.IsDefined(typeof(DualBrushMode), Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        /// <summary>主の被覆率 main（0〜1）と 2 つ目の溜まり dual（0〜1）を合わせた被覆率（0〜1）。main = 0 なら必ず 0。</summary>
        public static double Combine(DualBrushMode mode, double main, double dual)
        {
            if (main <= 0) return 0;
            double r;
            switch (mode)
            {
                case DualBrushMode.Multiply: r = main * dual; break;
                case DualBrushMode.Darken: r = Math.Min(main, dual); break;
                case DualBrushMode.Overlay: r = main < .5 ? 2 * main * dual : 1 - 2 * (1 - main) * (1 - dual); break;
                case DualBrushMode.ColorDodge: r = dual >= 1 ? 1 : Math.Min(1, main / (1 - dual)); break;
                case DualBrushMode.ColorBurn: r = main >= 1 ? 1 : dual <= 0 ? 0 : 1 - Math.Min(1, (1 - main) / dual); break;
                case DualBrushMode.LinearBurn: r = main + dual - 1; break;
                case DualBrushMode.HardMix: r = main + dual >= 1 ? 1 : 0; break;
                case DualBrushMode.Subtract: r = main - dual; break;
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
            return Math.Max(0, Math.Min(1, r));
        }
    }

    /// <summary>ペンの傾き。<see cref="BrushSample.TiltX"/> / TiltY は直立からの角度（ラジアン）を X 軸・Y 軸の向きに分けたもの
    /// （Unity の Event.tilt と Windows のペン入力の tiltX / tiltY と同じ考え方）。2 つを合わせた直立からの傾きは
    /// tan²θ = tan²θx + tan²θy。</summary>
    public static class PenTilt
    {
        public const double MaxAngle = Math.PI / 2;
        /// <summary>直立 0 〜 寝かせきって 1。傾きの情報が無い入力（マウス）は 0。</summary>
        public static double Amount(double tiltX, double tiltY)
        {
            double ax = Math.Abs(tiltX), ay = Math.Abs(tiltY);
            if (ax <= 0 && ay <= 0) return 0;
            if (ax >= MaxAngle - 1e-9 || ay >= MaxAngle - 1e-9) return 1;
            double tx = Math.Tan(ax), ty = Math.Tan(ay);
            return Math.Min(1, Math.Atan(Math.Sqrt(tx * tx + ty * ty)) / MaxAngle);
        }
        /// <summary>ペンが倒れている向き（ラジアン、キャンバスの X 軸から反時計回り）。傾きが無ければ 0。</summary>
        public static double Azimuth(double tiltX, double tiltY)
        {
            if (tiltX == 0 && tiltY == 0) return 0;
            return Math.Atan2(Math.Tan(Math.Max(-MaxAngle + 1e-9, Math.Min(MaxAngle - 1e-9, tiltY))), Math.Tan(Math.Max(-MaxAngle + 1e-9, Math.Min(MaxAngle - 1e-9, tiltX))));
        }
    }

    /// <summary>カラーダイナミクス。色はファイルに入っている値のまま（エンコードされた空間、Photoshop と同じ）で、HSV で動かす。
    /// 決まった順: 描画色/背景色のゆらぎ → 純度 → 色相 → 彩度 → 明るさ。0 の項目は乱数を引かない。</summary>
    public static class ColorDynamics
    {
        /// <summary>次のダブ（またはストローク）の色。アルファは描画色と背景色の間で混ぜるだけで、HSV では動かさない。</summary>
        public static Rgba32 Next(BrushSettings s, Random random)
        {
            Rgba32 c = s.Color;
            if (s.ForegroundBackgroundJitter > 0)
            {
                double t = s.ForegroundBackgroundJitter * random.NextDouble(); Rgba32 b = s.SecondaryColor;
                c = new Rgba32(Mix(c.R, b.R, t), Mix(c.G, b.G, t), Mix(c.B, b.B, t), Mix(c.A, b.A, t));
            }
            if (s.Purity == 0 && s.HueJitter <= 0 && s.SaturationJitter <= 0 && s.BrightnessJitter <= 0) return c;
            RgbToHsv(c.R / 255.0, c.G / 255.0, c.B / 255.0, out double h, out double sat, out double v);
            if (s.Purity > 0) sat += (1 - sat) * s.Purity; else if (s.Purity < 0) sat *= 1 + s.Purity;
            if (s.HueJitter > 0) { h += s.HueJitter * (random.NextDouble() * 2 - 1) * .5; h -= Math.Floor(h); }
            if (s.SaturationJitter > 0) sat += s.SaturationJitter * (random.NextDouble() * 2 - 1);
            if (s.BrightnessJitter > 0) v += s.BrightnessJitter * (random.NextDouble() * 2 - 1);
            HsvToRgb(h, MathUtil.Clamp01(sat), MathUtil.Clamp01(v), out double r, out double g, out double bl);
            return new Rgba32(MathUtil.ToByte(r), MathUtil.ToByte(g), MathUtil.ToByte(bl), c.A);
        }
        static byte Mix(byte a, byte b, double t) { return MathUtil.ToByte((a + (b - a) * t) / 255.0); }
        /// <summary>h は 0〜1（1 周）。灰色は h = 0, s = 0。</summary>
        public static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max; s = max <= 0 ? 0 : d / max; h = 0;
            if (d <= 0) return;
            if (max == r) h = (g - b) / d; else if (max == g) h = 2 + (b - r) / d; else h = 4 + (r - g) / d;
            h /= 6; if (h < 0) h += 1;
        }
        public static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
        {
            h = (h - Math.Floor(h)) * 6; int i = (int)Math.Floor(h) % 6; double f = h - Math.Floor(h);
            double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
        }
    }

    public sealed partial class BrushSettings
    {
        public const int MaxFade = 10000;
        // --- Colour dynamics (Color / Emission only; see ForChannel). 0 = none. Deterministic for a given Seed. ---
        /// <summary>The background colour for ForegroundBackgroundJitter.</summary>
        public Rgba32 SecondaryColor = new Rgba32(0, 0, 0, 255);
        /// <summary>Each dab mixes toward SecondaryColor by a random 0..this.</summary>
        public double ForegroundBackgroundJitter;
        /// <summary>Hue moves by up to ±this × 180°; saturation and brightness (HSV) by up to ±this.</summary>
        public double HueJitter, SaturationJitter, BrightnessJitter;
        /// <summary>-1 (grey) .. 0 (unchanged) .. 1 (fully saturated), applied before the jitters.</summary>
        public double Purity;
        /// <summary>True: a new colour for every dab (Photoshop's "Apply Per Tip"). False: one colour for the whole stroke.</summary>
        public bool ColorPerTip = true;
        // --- Dual brush (null = off). ---
        public DualBrush Dual;
        // --- Fade: the value falls linearly from full to 0 over this many stamps (spacing steps) of the stroke. 0 = off. ---
        public int FadeSize, FadeOpacity, FadeFlow;
        // --- Pen tilt: size / opacity / flow are multiplied by 1 − PenTilt.Amount (upright = full); TiltAngle adds the
        // direction the pen leans to the tip angle. Input without tilt (mouse) is upright, so these change nothing for it. ---
        public bool TiltSize, TiltOpacity, TiltFlow, TiltAngle;

        public bool HasColorDynamics { get { return ForegroundBackgroundJitter > 0 || HueJitter > 0 || SaturationJitter > 0 || BrightnessJitter > 0 || Purity != 0; } }
        /// <summary>カラーダイナミクスが意味を持つチャンネル（色を持つもの）。Roughness・Metallic・Height はデータ、Normal はベクトル
        /// なので、色相や描画色/背景色で値を揺らすとデータを壊すだけになる。</summary>
        public static bool CarriesColor(PaintChannel channel) { return channel == PaintChannel.Color || channel == PaintChannel.Emission; }

        /// <summary>A copy for painting on channel (null = a layer mask). Colour dynamics are dropped where the channel carries
        /// data rather than colour (and on masks): such strokes paint the Value exactly as without dynamics.</summary>
        public BrushSettings ForChannel(PaintChannel? channel)
        {
            var copy = Clone(); copy.PaintedChannel = channel; // ステンシルの色を受けるチャンネルかを、ストロークが面ごとに見る（BrushStencil.cs）
            if (channel == null || !CarriesColor(channel.Value))
            { copy.ForegroundBackgroundJitter = 0; copy.HueJitter = 0; copy.SaturationJitter = 0; copy.BrightnessJitter = 0; copy.Purity = 0; }
            return copy;
        }

        BrushSettings WithDynamicsOf(BrushSettings source)
        {
            SecondaryColor = source.SecondaryColor; ForegroundBackgroundJitter = source.ForegroundBackgroundJitter; HueJitter = source.HueJitter;
            SaturationJitter = source.SaturationJitter; BrightnessJitter = source.BrightnessJitter; Purity = source.Purity; ColorPerTip = source.ColorPerTip;
            Dual = source.Dual == null ? null : source.Dual.Clone();
            FadeSize = source.FadeSize; FadeOpacity = source.FadeOpacity; FadeFlow = source.FadeFlow;
            TiltSize = source.TiltSize; TiltOpacity = source.TiltOpacity; TiltFlow = source.TiltFlow; TiltAngle = source.TiltAngle;
            return this;
        }
        void ValidateDynamics()
        {
            foreach (double v in new[] { ForegroundBackgroundJitter, HueJitter, SaturationJitter, BrightnessJitter })
            {
                MathUtil.RequireFinite(v, "colour dynamics");
                if (v < 0 || v > 1) throw new ArgumentOutOfRangeException("colour dynamics", "Colour jitters must be 0..1.");
            }
            MathUtil.RequireFinite(Purity, nameof(Purity));
            if (Purity < -1 || Purity > 1) throw new ArgumentOutOfRangeException(nameof(Purity), "Purity must be -1..1.");
            foreach (int f in new[] { FadeSize, FadeOpacity, FadeFlow })
                if (f < 0 || f > MaxFade) throw new ArgumentOutOfRangeException("fade", "Fade lengths must be 0.." + MaxFade + " stamps.");
            if (Dual != null) Dual.Validate();
        }
    }
}
