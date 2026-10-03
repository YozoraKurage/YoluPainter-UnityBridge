using System;
using System.Collections.Generic;
using System.Linq;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>書き出す画像 1 枚の中身の種類。</summary>
    public enum ExportImageKind
    {
        /// <summary>Color の合成そのまま（straight RGBA。アルファは透明度）。</summary>
        BaseColor,
        /// <summary>Emission の合成を、アルファを掛けた RGB と不透明に（シェーダーは RGB だけを読むので、柔らかい縁が明るく残らないように）。</summary>
        Emission,
        /// <summary>Unity 向けの法線（<see cref="NormalMaps.Output"/>: OpenGL の向き、不透明、塗っていない所は平ら、Height → Normal 込み）。</summary>
        Normal,
        /// <summary>R・G・B・A をそれぞれ <see cref="ExportScalar"/> から詰める（Metallic と Smoothness、MaskMap など）。</summary>
        Packed,
    }

    /// <summary>詰める 1 つの値。値のチャンネルは「r × a」（塗っていないテクセルは 0）で、lilToon の割り当てと 3D ビューのマテリアル表示と同じ。</summary>
    public enum ExportScalar
    {
        Zero,
        One,
        Metallic,
        Roughness,
        /// <summary>1 − Roughness。</summary>
        Smoothness,
        Height,
        /// <summary>焼いた AO（メッシュマップ）。無いテクセルと、AO を渡されないときは 1（遮蔽なし）。</summary>
        Occlusion,
    }

    /// <summary>テンプレートの画像 1 枚。</summary>
    public sealed class ExportImage
    {
        public string Suffix { get; }
        public ExportImageKind Kind { get; }
        public ExportScalar R { get; }
        public ExportScalar G { get; }
        public ExportScalar B { get; }
        public ExportScalar A { get; }
        /// <summary>Unity の取り込み: sRGB の色か（そうでなければリニア）。</summary>
        public bool Srgb => Kind == ExportImageKind.BaseColor || Kind == ExportImageKind.Emission;

        ExportImage(string suffix, ExportImageKind kind, ExportScalar r, ExportScalar g, ExportScalar b, ExportScalar a)
        { Suffix = suffix; Kind = kind; R = r; G = g; B = b; A = a; }
        public static ExportImage Of(string suffix, ExportImageKind kind) => new ExportImage(suffix, kind, ExportScalar.Zero, ExportScalar.Zero, ExportScalar.Zero, ExportScalar.One);
        public static ExportImage Pack(string suffix, ExportScalar r, ExportScalar g, ExportScalar b, ExportScalar a) => new ExportImage(suffix, ExportImageKind.Packed, r, g, b, a);

        /// <summary>この画像が読むもの（チャンネルと AO）。</summary>
        public IEnumerable<ExportScalar> Scalars => Kind == ExportImageKind.Packed ? new[] { R, G, B, A } : Array.Empty<ExportScalar>();
    }

    /// <summary>書き出しのテンプレート（Substance Painter の Export の Output Template）: どの画像に、どのチャンネルをどう詰めるか。</summary>
    public sealed class ExportTemplate
    {
        public string Id { get; }
        public string Name { get; }
        public IReadOnlyList<ExportImage> Images { get; }
        ExportTemplate(string id, string name, params ExportImage[] images) { Id = id; Name = name; Images = images; }

        /// <summary>Unity の Built-in Standard（Metallic）と URP Lit: どちらも _MetallicGlossMap の R が Metallic、A が Smoothness、
        /// _OcclusionMap と _ParallaxMap は G を読む（灰色で全部に入れる）。</summary>
        public static readonly ExportTemplate UnityStandard = new ExportTemplate("unity-standard", "Unity Standard / URP Lit",
            ExportImage.Of("Albedo", ExportImageKind.BaseColor),
            ExportImage.Pack("MetallicSmoothness", ExportScalar.Metallic, ExportScalar.Metallic, ExportScalar.Metallic, ExportScalar.Smoothness),
            ExportImage.Of("Normal", ExportImageKind.Normal),
            ExportImage.Pack("Height", ExportScalar.Height, ExportScalar.Height, ExportScalar.Height, ExportScalar.One),
            ExportImage.Pack("Occlusion", ExportScalar.Occlusion, ExportScalar.Occlusion, ExportScalar.Occlusion, ExportScalar.One),
            ExportImage.Of("Emission", ExportImageKind.Emission));

        /// <summary>HDRP Lit: _MaskMap は R Metallic・G AO・B ディテールマスク（全部 1）・A Smoothness。_HeightMap は R を読む。</summary>
        public static readonly ExportTemplate UnityHdrp = new ExportTemplate("unity-hdrp", "HDRP Lit",
            ExportImage.Of("BaseColor", ExportImageKind.BaseColor),
            ExportImage.Pack("MaskMap", ExportScalar.Metallic, ExportScalar.Occlusion, ExportScalar.One, ExportScalar.Smoothness),
            ExportImage.Of("Normal", ExportImageKind.Normal),
            ExportImage.Pack("Height", ExportScalar.Height, ExportScalar.Height, ExportScalar.Height, ExportScalar.One),
            ExportImage.Of("Emission", ExportImageKind.Emission));

        /// <summary>lilToon: 割り当て（LilToonAssignment）と同じ値の画像（平滑度は 1 − Roughness、Metallic は灰色）。</summary>
        public static readonly ExportTemplate LilToon = new ExportTemplate("liltoon", "lilToon",
            ExportImage.Of("Main", ExportImageKind.BaseColor),
            ExportImage.Of("Normal", ExportImageKind.Normal),
            ExportImage.Pack("Smoothness", ExportScalar.Smoothness, ExportScalar.Smoothness, ExportScalar.Smoothness, ExportScalar.One),
            ExportImage.Pack("Metallic", ExportScalar.Metallic, ExportScalar.Metallic, ExportScalar.Metallic, ExportScalar.One),
            ExportImage.Of("Emission", ExportImageKind.Emission));

        public static readonly IReadOnlyList<ExportTemplate> BuiltIn = new[] { UnityStandard, UnityHdrp, LilToon };
    }

    /// <summary>テンプレートの画像を、文書から作る。</summary>
    public static class ExportTemplates
    {
        /// <summary>どれかのレイヤーが使っているチャンネル（Height → Normal が有効で Height を使っていれば Normal も）。</summary>
        public static bool Uses(PaintDocument document, PaintChannel channel)
            => document.Layers.Any(l => l.IsChannelEnabled(channel)) || channel == PaintChannel.Normal && NormalMaps.DerivesNormal(document);

        /// <summary>使っていない値のチャンネルの既定（Smoothness は Unity の Standard の既定と同じ 0.5、ほかは 0）。</summary>
        public static byte DefaultValue(ExportScalar scalar) => scalar == ExportScalar.Smoothness ? (byte)128 : scalar == ExportScalar.One || scalar == ExportScalar.Occlusion ? (byte)255 : (byte)0;

        static PaintChannel? SourceChannel(ExportScalar scalar)
        {
            switch (scalar)
            {
                case ExportScalar.Metallic: return PaintChannel.Metallic;
                case ExportScalar.Roughness: case ExportScalar.Smoothness: return PaintChannel.Roughness;
                case ExportScalar.Height: return PaintChannel.Height;
                default: return null;
            }
        }

        /// <summary>
        /// 画像を書き出すか: その画像が読むものが 1 つでもあるとき（チャンネルを使っている・AO がある）。0 と 1 だけの画像や、使っていない
        /// チャンネルだけの画像は書かない（既定の値で埋めた画像を置かない）。
        /// </summary>
        public static bool ShouldWrite(PaintDocument document, ExportImage image, bool hasOcclusion)
        {
            switch (image.Kind)
            {
                case ExportImageKind.BaseColor: return Uses(document, PaintChannel.Color);
                case ExportImageKind.Emission: return Uses(document, PaintChannel.Emission);
                case ExportImageKind.Normal: return Uses(document, PaintChannel.Normal);
                default:
                    return image.Scalars.Any(s => s == ExportScalar.Occlusion ? hasOcclusion : SourceChannel(s) is PaintChannel c && Uses(document, c));
            }
        }

        /// <summary>
        /// 画像（straight RGBA8、左下が原点、文書の大きさ）を作る。occlusion は焼いた AO（テクセルごとに 0〜255、無いテクセルは 255）か null。
        /// 使っていないチャンネルの値は <see cref="DefaultValue"/>。
        /// </summary>
        public static byte[] Build(PaintDocument document, ExportImage image, byte[] occlusion = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (image == null) throw new ArgumentNullException(nameof(image));
            int n = checked(document.Width * document.Height);
            if (occlusion != null && occlusion.Length != n) throw new ArgumentException("The occlusion must have one value per texel.", nameof(occlusion));
            switch (image.Kind)
            {
                case ExportImageKind.BaseColor: return document.Composite(PaintChannel.Color);
                case ExportImageKind.Normal: return NormalMaps.Output(document);
                case ExportImageKind.Emission:
                {
                    var emission = document.Composite(PaintChannel.Emission);
                    for (int i = 0; i < emission.Length; i += 4)
                    {
                        int a = emission[i + 3];
                        emission[i] = (byte)((emission[i] * a + 127) / 255); emission[i + 1] = (byte)((emission[i + 1] * a + 127) / 255);
                        emission[i + 2] = (byte)((emission[i + 2] * a + 127) / 255); emission[i + 3] = 255;
                    }
                    return emission;
                }
            }
            // 詰める画像: 使うチャンネルの合成を 1 回ずつ取り、テクセルごとに値を詰める
            var composites = new Dictionary<PaintChannel, byte[]>();
            foreach (var scalar in image.Scalars)
                if (SourceChannel(scalar) is PaintChannel c && !composites.ContainsKey(c) && Uses(document, c)) composites[c] = document.Composite(c);
            var output = new byte[n * 4];
            var planes = new[] { image.R, image.G, image.B, image.A };
            for (int p = 0; p < 4; p++)
            {
                var scalar = planes[p];
                if (scalar == ExportScalar.Occlusion && occlusion != null) { for (int i = 0; i < n; i++) output[i * 4 + p] = occlusion[i]; continue; }
                if (!(SourceChannel(scalar) is PaintChannel c) || !composites.TryGetValue(c, out var source))
                {
                    byte value = DefaultValue(scalar);
                    for (int i = 0; i < n; i++) output[i * 4 + p] = value;
                    continue;
                }
                bool invert = scalar == ExportScalar.Smoothness;
                for (int i = 0; i < n; i++)
                {
                    int v = (source[i * 4] * source[i * 4 + 3] + 127) / 255;
                    output[i * 4 + p] = (byte)(invert ? 255 - v : v);
                }
            }
            return output;
        }
    }
}
