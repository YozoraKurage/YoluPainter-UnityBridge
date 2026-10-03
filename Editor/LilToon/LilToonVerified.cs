using System.Collections.Generic;
using System.Linq;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor.LilToon
{
    /// <summary>1 チャンネルの対応（バージョンごとに、インストール済みのシェーダーソースで確かめたもの）。</summary>
    internal sealed class ChannelSpec
    {
        public PaintChannel Channel;
        public string Texture, Packing, Note, Unmapped;
        public string[] Scales = new string[0], Toggles = new string[0], Keywords = new string[0], CompileFeatures = new string[0];
        public LilToonColorSpace Space;
        /// <summary>Multi: キーワードを決める元のトグル（lilMaterialUtils.SetupMultiMaterial）。食い違いは警告だけ。</summary>
        public string KeywordSourceToggle;

        public IEnumerable<string> Properties => new[] { Texture }.Concat(Scales).Concat(Toggles).Concat(KeywordSourceToggle == null ? new string[0] : new[] { KeywordSourceToggle }).Where(p => p != null);
    }

    internal sealed class VariantSpec
    {
        public string ShaderName, File, Family, RenderMode, NotVerifiedReason;
        public bool Outline;
        /// <summary>UsePass でフォワードパスを借りるシェーダー名（例: Hidden/ltspass_opaque）。自身にパスを持つなら null。</summary>
        public string ForwardPass;
        public ChannelSpec[] Channels;
        public bool Verified => Channels != null;
    }

    internal sealed class VerifiedRelease
    {
        public string Version;
        /// <summary>シェーダーの _lilToonVersion のデフォルト値（マテリアル形式のバージョン）。</summary>
        public int MaterialFormat;
        public string Pipeline;
        /// <summary>フォワードパスのソースに入っているはずのパイプラインの include。</summary>
        public string PipelineInclude;
        public Dictionary<string, VariantSpec> Variants;
    }

    /// <summary>
    /// 確かめた lilToon のバージョン。今は jp.lilxyzw.liltoon 2.3.4（Built-in RP）だけ。行番号はそのバージョンのパッケージ内のファイル。
    /// <para>Standard / Tessellation（UsePass "Hidden/ltspass_*"、lil_pass_forward.hlsl → lil_pass_forward_normal.hlsl）:</para>
    /// <list type="bullet">
    /// <item>Color: Shader/lts.shader:44-45 _Color/_MainTex、lil_common_frag.hlsl:312/349 で _MainTex をサンプリングし :357 で _Color を掛ける。</item>
    /// <item>Normal: lts.shader:123-125 _UseBumpMap/[Normal] _BumpMap/_BumpScale、lil_common_frag.hlsl:565-571（LIL_FEATURE_BumpMap と if(_UseBumpMap)）、
    /// lil_pass_forward_normal.hlsl:284-290（LIL_FEATURE_NORMAL_1ST）、lil_common_functions.hlsl:166-178 lilUnpackNormalScale（Unity のノーマルマップ形式）。</item>
    /// <item>Roughness: lts.shader:222/224/225 _UseReflection/_Smoothness/_SmoothnessTex、lil_common_frag.hlsl:1433-1437 で smoothness = _Smoothness × tex.r、
    /// perceptualRoughness = 1 − smoothness（初期値 1.0 は lil_common.hlsl:206）、:1426 の if(_UseReflection)、
    /// lil_pass_forward_normal.hlsl:522（LIL_FEATURE_REFLECTION）。</item>
    /// <item>Metallic: lts.shader:227-228 [Gamma] _Metallic/_MetallicGlossMap、lil_common_frag.hlsl:1440-1443 で metallic = _Metallic × tex.r（LIL_FEATURE_MetallicGlossMap）。</item>
    /// <item>Height: lts.shader:421-425 _UseParallax/_UsePOM/_ParallaxMap/_Parallax/_ParallaxOffset、lil_common_functions.hlsl:574-582 で (tex.r − _ParallaxOffset) × _Parallax、
    /// lil_common_frag.hlsl:293-304、lil_pass_forward_normal.hlsl:272-273（LIL_FEATURE_PARALLAX）。形状の変位ではない（lil_tessellation.hlsl はテクスチャを読まない）。</item>
    /// <item>Emission: lts.shader:345-347 _UseEmission/_EmissionColor/_EmissionMap、lil_common_frag.hlsl:1817-1833（LIL_FEATURE_EmissionMap、if(_UseEmission)）、
    /// :1861 で emissionColor.a が混ぜ具合、lil_pass_forward_normal.hlsl:556（LIL_FEATURE_EMISSION_1ST）。</item>
    /// <item>機能の #define はフォワードパスのシェーダー（例 Shader/ltspass_opaque.shader:644-701 の HLSLINCLUDE）にあり、lilToon の shader setting
    /// （Editor/lilToonSetting.cs:570-722 BuildShaderSettingString、ProjectSettings/lilToonSetting.json）で書き換わる。テクスチャ数の限られた API では
    /// 使われていないテクスチャ機能を外す（Editor/lilStartup.cs:79-86）ので、実際のソースを毎回読む。</item>
    /// </list>
    /// <para>Multi（Shader/ltsmulti.shader:647 #define LIL_MULTI）: lil_common.hlsl:29-46 で _UseBumpMap などが true に固定され、代わりにキーワード
    /// （ltsmulti.shader:734/737/740/747 の shader_feature_local）を lil_replace_keywords.hlsl:122/138/153/189 が機能に置き換える。テクスチャ機能は
    /// lil_replace_keywords.hlsl:260/274/275/287 で常に定義。キーワードは lilMaterialUtils.cs:419/422/457/459 が _Use* から決める。</para>
    /// <para>Lite（UsePass "Hidden/ltspass_lite_*"、lil_pass_forward_lite.hlsl）: Shader/ltsl.shader:23/27/28/65-67、lil_common_frag.hlsl:1869-1882 で
    /// エミッションは _UseEmission と _TriMask.b に掛かる。ノーマルマップ・リフレクション・視差（Parallax）のプロパティは無い。</para>
    /// </summary>
    internal static class LilToonVerified
    {
        public const string PackageName = "jp.lilxyzw.liltoon";
        public const string BuiltInPipeline = "BuiltIn";

        static readonly Dictionary<string, VerifiedRelease> releases = new Dictionary<string, VerifiedRelease>
        {
            { "2.3.4", Release234() },
        };

        public static IReadOnlyCollection<string> Versions => releases.Keys;
        public static bool TryGet(string version, out VerifiedRelease release) => releases.TryGetValue(version ?? "", out release);

        static ChannelSpec[] Standard() => new[]
        {
            new ChannelSpec { Channel = PaintChannel.Color, Texture = "_MainTex", Packing = "rgba × _Color (alpha is coverage)", Scales = new[] { "_Color" }, Space = LilToonColorSpace.Srgb },
            new ChannelSpec { Channel = PaintChannel.Roughness, Texture = "_SmoothnessTex", Packing = "r = 1 − roughness, × _Smoothness", Scales = new[] { "_Smoothness" }, Space = LilToonColorSpace.Linear,
                Toggles = new[] { "_UseReflection" }, CompileFeatures = new[] { "LIL_FEATURE_REFLECTION", "LIL_FEATURE_SmoothnessTex" },
                Note = "lilToon reads smoothness: perceptual roughness = 1 − _Smoothness × tex.r." },
            new ChannelSpec { Channel = PaintChannel.Metallic, Texture = "_MetallicGlossMap", Packing = "r × _Metallic", Scales = new[] { "_Metallic" }, Space = LilToonColorSpace.Linear,
                Toggles = new[] { "_UseReflection" }, CompileFeatures = new[] { "LIL_FEATURE_REFLECTION", "LIL_FEATURE_MetallicGlossMap" },
                Note = "Only the red channel is read (no smoothness in alpha). The _Metallic slider is declared [Gamma]." },
            new ChannelSpec { Channel = PaintChannel.Height, Texture = "_ParallaxMap", Packing = "(r − _ParallaxOffset) × _Parallax", Scales = new[] { "_Parallax", "_ParallaxOffset" }, Space = LilToonColorSpace.Linear,
                Toggles = new[] { "_UseParallax" }, CompileFeatures = new[] { "LIL_FEATURE_PARALLAX" },
                Note = "UV parallax (POM when _UsePOM and LIL_FEATURE_POM), not geometric displacement." },
            new ChannelSpec { Channel = PaintChannel.Normal, Texture = "_BumpMap", Packing = "tangent-space normal map, xy × _BumpScale", Scales = new[] { "_BumpScale" }, Space = LilToonColorSpace.NormalMap,
                Toggles = new[] { "_UseBumpMap" }, CompileFeatures = new[] { "LIL_FEATURE_NORMAL_1ST", "LIL_FEATURE_BumpMap" } },
            new ChannelSpec { Channel = PaintChannel.Emission, Texture = "_EmissionMap", Packing = "rgba × _EmissionColor (alpha is the blend amount)", Scales = new[] { "_EmissionColor" }, Space = LilToonColorSpace.Srgb,
                Toggles = new[] { "_UseEmission" }, CompileFeatures = new[] { "LIL_FEATURE_EMISSION_1ST", "LIL_FEATURE_EmissionMap" },
                Note = "HDR intensity lives in _EmissionColor, not in the texture." },
        };

        static ChannelSpec[] Multi() => Standard().Select(s =>
        {
            string keyword = null;
            switch (s.Channel)
            {
                case PaintChannel.Normal: keyword = "_NORMALMAP"; break;
                case PaintChannel.Roughness: case PaintChannel.Metallic: keyword = "_GLOSSYREFLECTIONS_OFF"; break;
                case PaintChannel.Height: keyword = "_PARALLAXMAP"; break;
                case PaintChannel.Emission: keyword = "_EMISSION"; break;
            }
            if (keyword == null) return s;
            return new ChannelSpec
            {
                Channel = s.Channel, Texture = s.Texture, Packing = s.Packing, Scales = s.Scales, Space = s.Space, Note = s.Note,
                Keywords = new[] { keyword }, KeywordSourceToggle = s.Toggles[0],
            };
        }).ToArray();

        static ChannelSpec[] Lite() => new[]
        {
            new ChannelSpec { Channel = PaintChannel.Color, Texture = "_MainTex", Packing = "rgba × _Color (alpha is coverage)", Scales = new[] { "_Color" }, Space = LilToonColorSpace.Srgb },
            new ChannelSpec { Channel = PaintChannel.Roughness, Unmapped = "lilToon Lite has no reflection/smoothness texture (Shader/ltsl.shader declares no _SmoothnessTex)." },
            new ChannelSpec { Channel = PaintChannel.Metallic, Unmapped = "lilToon Lite has no metallic texture (Shader/ltsl.shader declares no _MetallicGlossMap)." },
            new ChannelSpec { Channel = PaintChannel.Height, Unmapped = "lilToon Lite has no parallax (Shader/ltsl.shader declares no _ParallaxMap)." },
            new ChannelSpec { Channel = PaintChannel.Normal, Unmapped = "lilToon Lite has no normal map (Shader/ltsl.shader declares no _BumpMap)." },
            new ChannelSpec { Channel = PaintChannel.Emission, Texture = "_EmissionMap", Packing = "rgb × _EmissionColor × _TriMask.b", Scales = new[] { "_EmissionColor" }, Space = LilToonColorSpace.Srgb,
                Toggles = new[] { "_UseEmission" }, Note = "Lite also multiplies emission by the blue channel of _TriMask." },
        };

        static VerifiedRelease Release234()
        {
            var variants = new List<VariantSpec>();
            void Add(string file, string shader, string family, string mode, bool outline, string pass, ChannelSpec[] channels, string notVerified = null)
                => variants.Add(new VariantSpec { File = file, ShaderName = shader, Family = family, RenderMode = mode, Outline = outline, ForwardPass = pass, Channels = channels, NotVerifiedReason = notVerified });

            // Standard / Tessellation / Lite: Opaque / Cutout / Transparent 系の 5 種、それぞれアウトラインあり
            var modes = new[] { ("", "Opaque", "opaque"), ("_cutout", "Cutout", "cutout"), ("_trans", "Transparent", "transparent"), ("_onetrans", "OnePassTransparent", "transparent"), ("_twotrans", "TwoPassTransparent", "transparent") };
            foreach (var (suffix, mode, pass) in modes)
            {
                string modeName = mode == "Opaque" ? "" : mode;
                foreach (bool outline in new[] { false, true })
                {
                    string o = outline ? "_o" : "", oName = outline ? "Outline" : "";
                    Add("lts" + suffix + o + ".shader", suffix == "" && !outline ? "lilToon" : "Hidden/lilToon" + modeName + oName, "Standard", mode, outline, "Hidden/ltspass_" + pass, Standard());
                    Add("lts_tess" + suffix + o + ".shader", "Hidden/lilToonTessellation" + modeName + oName, "Tessellation", mode, outline, "Hidden/ltspass_tess_" + pass, Standard());
                    Add("ltsl" + suffix + o + ".shader", "Hidden/lilToonLite" + modeName + oName, "Lite", mode, outline, "Hidden/ltspass_lite_" + pass, Lite());
                }
            }
            Add("ltsmulti.shader", "_lil/lilToonMulti", "Multi", "PerMaterial(_TransparentMode)", false, null, Multi());
            Add("ltsmulti_o.shader", "Hidden/lilToonMultiOutline", "Multi", "PerMaterial(_TransparentMode)", true, null, Multi());

            // パッケージにあるが、チャンネルの対応をまだ確かめていないバリアント（別のフォワードパスを持つ）
            const string notYet = "the channel mapping of this variant has not been verified against the lilToon 2.3.4 sources";
            Add("lts_fur.shader", "Hidden/lilToonFur", "Fur", "Transparent", false, null, null, notYet + " (fur pass)");
            Add("lts_fur_cutout.shader", "Hidden/lilToonFurCutout", "Fur", "Cutout", false, null, null, notYet + " (fur pass)");
            Add("lts_fur_two.shader", "Hidden/lilToonFurTwoPass", "Fur", "TwoPassTransparent", false, null, null, notYet + " (fur pass)");
            Add("lts_furonly.shader", "_lil/[Optional] lilToonFurOnlyTransparent", "FurOnly", "Transparent", false, null, null, notYet + " (fur only)");
            Add("lts_furonly_cutout.shader", "_lil/[Optional] lilToonFurOnlyCutout", "FurOnly", "Cutout", false, null, null, notYet + " (fur only)");
            Add("lts_furonly_two.shader", "_lil/[Optional] lilToonFurOnlyTwoPass", "FurOnly", "TwoPassTransparent", false, null, null, notYet + " (fur only)");
            Add("lts_gem.shader", "Hidden/lilToonGem", "Gem", "Gem", false, null, null, notYet + " (gem pass)");
            Add("lts_ref.shader", "Hidden/lilToonRefraction", "Refraction", "Refraction", false, null, null, notYet + " (refraction)");
            Add("lts_ref_blur.shader", "Hidden/lilToonRefractionBlur", "Refraction", "RefractionBlur", false, null, null, notYet + " (refraction blur)");
            Add("lts_fakeshadow.shader", "_lil/[Optional] lilToonFakeShadow", "FakeShadow", "FakeShadow", false, null, null, notYet + " (fake shadow)");
            Add("lts_oo.shader", "_lil/[Optional] lilToonOutlineOnly", "OutlineOnly", "Opaque", true, null, null, notYet + " (outline only)");
            Add("lts_cutout_oo.shader", "_lil/[Optional] lilToonOutlineOnlyCutout", "OutlineOnly", "Cutout", true, null, null, notYet + " (outline only)");
            Add("lts_trans_oo.shader", "_lil/[Optional] lilToonOutlineOnlyTransparent", "OutlineOnly", "Transparent", true, null, null, notYet + " (outline only)");
            Add("lts_overlay.shader", "_lil/[Optional] lilToonOverlay", "Overlay", "Overlay", false, null, null, notYet + " (overlay)");
            Add("lts_overlay_one.shader", "_lil/[Optional] lilToonOverlayOnePass", "Overlay", "OverlayOnePass", false, null, null, notYet + " (overlay)");
            Add("ltsl_overlay.shader", "_lil/[Optional] lilToonLiteOverlay", "Overlay", "Overlay", false, null, null, notYet + " (lite overlay)");
            Add("ltsl_overlay_one.shader", "_lil/[Optional] lilToonLiteOverlayOnePass", "Overlay", "OverlayOnePass", false, null, null, notYet + " (lite overlay)");
            Add("ltsmulti_fur.shader", "Hidden/lilToonMultiFur", "Multi", "Fur", false, null, null, notYet + " (multi fur)");
            Add("ltsmulti_gem.shader", "Hidden/lilToonMultiGem", "Multi", "Gem", false, null, null, notYet + " (multi gem)");
            Add("ltsmulti_ref.shader", "Hidden/lilToonMultiRefraction", "Multi", "Refraction", false, null, null, notYet + " (multi refraction)");
            // マテリアルに使うものではない内部用のパス・ベイク用シェーダー
            foreach (var pass in new[] { "opaque", "cutout", "transparent", "tess_opaque", "tess_cutout", "tess_transparent", "lite_opaque", "lite_cutout", "lite_transparent", "dummy", "proponly" })
                Add("ltspass_" + pass + ".shader", "Hidden/ltspass_" + pass, "InternalPass", "-", false, null, null, "an internal lilToon pass shader, not a material shader");
            Add("ltspass_baker.shader", "Hidden/ltsother_baker", "InternalPass", "-", false, null, null, "an internal lilToon baking shader, not a material shader");
            Add("ltspass_bakeramp.shader", "Hidden/ltsother_bakeramp", "InternalPass", "-", false, null, null, "an internal lilToon baking shader, not a material shader");

            return new VerifiedRelease
            {
                Version = "2.3.4", MaterialFormat = 45, Pipeline = BuiltInPipeline, PipelineInclude = "Includes/lil_pipeline_brp.hlsl",
                Variants = variants.ToDictionary(v => v.ShaderName),
            };
        }
    }
}
