using System.Collections.Generic;
using System.Linq;
using System.Text;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor.LilToon
{
    /// <summary>書き出すテクスチャに期待するカラースペース。</summary>
    public enum LilToonColorSpace { Srgb, Linear, NormalMap }

    /// <summary>マップが効くための条件 1 つ（マテリアルのトグル、キーワード、シェーダーにコンパイルされた機能）と今の値。</summary>
    public sealed class LilToonCondition
    {
        /// <summary>"property"（マテリアルの値）、"keyword"（マテリアルのキーワード）、"compile"（インストール済みシェーダーの #define）。</summary>
        public string Kind { get; internal set; }
        public string Name { get; internal set; }
        public string Required { get; internal set; }
        public string Current { get; internal set; }
        public bool Satisfied { get; internal set; }
        /// <summary>確かめた場所（シェーダーのファイルなど）。</summary>
        public string Source { get; internal set; }
        public override string ToString() => Kind + " " + Name + " = " + Current + " (needs " + Required + ")" + (Satisfied ? "" : " NOT MET");
    }

    /// <summary>YoluPainter の 1 チャンネルと lilToon のプロパティの対応。</summary>
    public sealed class LilToonChannel
    {
        public PaintChannel Channel { get; internal set; }
        public bool IsMapped { get; internal set; }
        /// <summary>対応しないときの理由。</summary>
        public string UnmappedReason { get; internal set; }
        /// <summary>テクスチャのプロパティ名（例: _BumpMap）。</summary>
        public string Property { get; internal set; }
        /// <summary>シェーダーがテクスチャのどの成分をどう使うか（例: "r × _Metallic"、"r = 1 − roughness"）。</summary>
        public string Packing { get; internal set; }
        /// <summary>テクスチャに掛かる・足されるマテリアルの値（例: _BumpScale）。</summary>
        public IReadOnlyList<string> ScaleProperties { get; internal set; }
        public LilToonColorSpace ColorSpace { get; internal set; }
        /// <summary>今のマテリアルに入っているテクスチャ。無ければ null。アセットでなければ空文字。</summary>
        public string TexturePath { get; internal set; }
        public string TextureGuid { get; internal set; }
        /// <summary>今のテクスチャの インポート設定（読んだだけ）。テクスチャが無いかアセットでなければ null。</summary>
        public bool? TextureImportSrgb { get; internal set; }
        public string TextureImportType { get; internal set; }
        public IReadOnlyList<LilToonCondition> Conditions { get; internal set; } = new LilToonCondition[0];
        /// <summary>インポート設定の食い違いなど、適用を止めはしないが知らせること。</summary>
        public IReadOnlyList<string> Warnings { get; internal set; } = new string[0];
        public string Note { get; internal set; }
        /// <summary>今のマテリアルと、インストール済みのシェーダーで、このマップが表示に効くか。</summary>
        public bool IsEffective => IsMapped && Conditions.All(c => c.Satisfied);
    }

    /// <summary>lilToon のバリアント（パッケージに入っているシェーダー 1 つ）。</summary>
    public sealed class LilToonVariantInfo
    {
        public string ShaderName { get; internal set; }
        /// <summary>パッケージの Shader/ にあるファイル名（例: lts.shader）。</summary>
        public string File { get; internal set; }
        /// <summary>Standard / Tessellation / Lite / Multi / Fur / FurOnly / Gem / Refraction / FakeShadow / OutlineOnly / Overlay / InternalPass。</summary>
        public string Family { get; internal set; }
        /// <summary>Opaque / Cutout / Transparent / OnePassTransparent / TwoPassTransparent など。</summary>
        public string RenderMode { get; internal set; }
        public bool Outline { get; internal set; }
        /// <summary>チャンネルの対応を、このバージョンのシェーダーソースで確かめたバリアントか。</summary>
        public bool MappingVerified { get; internal set; }
    }

    /// <summary><see cref="LilToonAdapter.Inspect"/> の結果。読んだだけで、マテリアルにもアセットにも触れていない。</summary>
    public sealed class LilToonReport
    {
        /// <summary>マテリアルのシェーダーが、インストールされている lilToon パッケージ自身のシェーダーか。</summary>
        public bool IsLilToon { get; internal set; }
        /// <summary>バージョン・バリアント・パイプライン・プロパティ・シェーダーソースがすべて確かめられ、<see cref="Channels"/> を使ってよいか。</summary>
        public bool IsApplicable { get; internal set; }
        public string ShaderName { get; internal set; }
        public string ShaderAssetPath { get; internal set; }
        public string PackageName { get; internal set; }
        /// <summary>パッケージの package.json のバージョン。分からなければ "unknown"。</summary>
        public string Version { get; internal set; } = "unknown";
        public string RenderPipeline { get; internal set; }
        /// <summary>プロジェクトのカラースペース（Gamma / Linear）。</summary>
        public string ProjectColorSpace { get; internal set; }
        public LilToonVariantInfo Variant { get; internal set; }
        /// <summary>lilToon でない、または適用できない理由。適用できるときは空。</summary>
        public IReadOnlyList<string> Reasons => reasons;
        /// <summary>6 チャンネルすべて（適用できないときは空。推測で埋めない）。</summary>
        public IReadOnlyList<LilToonChannel> Channels { get; internal set; } = new LilToonChannel[0];

        internal readonly List<string> reasons = new List<string>();

        public LilToonChannel Channel(PaintChannel channel) => Channels.FirstOrDefault(c => c.Channel == channel);

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(IsApplicable ? "lilToon (applicable)" : IsLilToon ? "lilToon (NOT applicable)" : "not lilToon")
              .Append(": shader '").Append(ShaderName).Append("' at '").Append(ShaderAssetPath).Append("', version ").Append(Version)
              .Append(", pipeline ").Append(RenderPipeline).Append('\n');
            if (Variant != null) sb.Append("  variant ").Append(Variant.Family).Append('/').Append(Variant.RenderMode).Append(Variant.Outline ? "+Outline" : "").Append(" (").Append(Variant.File).Append(")\n");
            foreach (var r in reasons) sb.Append("  reason: ").Append(r).Append('\n');
            foreach (var c in Channels)
            {
                sb.Append("  ").Append(c.Channel).Append(": ");
                if (!c.IsMapped) { sb.Append("unmapped — ").Append(c.UnmappedReason).Append('\n'); continue; }
                sb.Append(c.Property).Append(" [").Append(c.Packing).Append(", ").Append(c.ColorSpace).Append("] texture=").Append(c.TexturePath ?? "none")
                  .Append(c.IsEffective ? " effective" : " NOT effective").Append('\n');
                foreach (var k in c.Conditions) sb.Append("      ").Append(k).Append('\n');
                foreach (var w in c.Warnings) sb.Append("      warning: ").Append(w).Append('\n');
            }
            return sb.ToString();
        }

        public override string ToString() => Describe();
    }
}
