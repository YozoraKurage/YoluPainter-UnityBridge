using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.LilToon;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>3D ビューの描き方: 中立（プレビュー用の中立のシェーダー）か、マテリアル（元のマテリアルの複製を元のシェーダーで）。</summary>
    public enum PreviewShading { Neutral, Material }

    /// <summary>塗ったチャンネルの合成を、マテリアルのテクスチャにどう詰めて渡すか。</summary>
    public enum PreviewPacking
    {
        /// <summary>塗ったままの RGBA（straight）。色として sRGB で読ませる（Color・Emission）。</summary>
        Color,
        /// <summary>r × a を灰色（アルファ 1）に。塗っていない所は 0。リニア。</summary>
        Value,
        /// <summary>1 − r × a（lilToon の平滑度は 1 − Roughness）。リニア。</summary>
        InvertedValue,
        /// <summary>Normal の出力（接空間・OpenGL の Y+・不透明、R = x・G = y・B = z・A = 1。Unity の UnpackNormal は AG の詰め方でも RG の
        /// 詰め方でも A = 1 なら x = R と読む）。リニア。</summary>
        Normal,
        /// <summary>Standard の _MetallicGlossMap: R = Metallic（r × a）、A = 1 − Roughness（r × a）。塗っていない側は元のマップか値。</summary>
        MetallicSmoothness,
    }

    /// <summary>1 チャンネルを入れるプロパティ。</summary>
    public sealed class PreviewChannelBinding
    {
        public PaintChannel Channel { get; internal set; }
        public string Property { get; internal set; }
        public PreviewPacking Packing { get; internal set; }
        /// <summary>複製の上で有効にするキーワード（Standard の _NORMALMAP など。シェーダーのインスペクターがテクスチャを入れたときに付けるもの）。</summary>
        public IReadOnlyList<string> Keywords { get; internal set; } = Array.Empty<string>();
        /// <summary>シェーダーがどう読むか（例: "_SmoothnessTex.r = 1 − Roughness"）。</summary>
        public string Reading { get; internal set; }
    }

    /// <summary>どの種類の対応で見せるか。</summary>
    public enum PreviewMaterialKind
    {
        /// <summary>元のマテリアルが無い（デモのキューブ・割り当ての無いスロット）。</summary>
        NoMaterial,
        /// <summary>シェーダーが壊れている・使えない・SRP など。中立で見せる。</summary>
        Unusable,
        /// <summary>LilToonAdapter が確かめた lilToon（版・バリアント・プロパティ・コンパイル済みの機能）。</summary>
        LilToon,
        /// <summary>確かめたビルトインの Standard。</summary>
        Standard,
        /// <summary>それ以外のシェーダー: メインのテクスチャに Color だけ。</summary>
        MainTexture,
        /// <summary>それ以外のシェーダーで、プロパティの名前と属性から流し込み先を推し量ったもの（確かめていない。欄で手で変えられる）。</summary>
        Guessed,
    }

    /// <summary>
    /// 元のマテリアルのシェーダーで 3D ビューを描くときの、チャンネル → プロパティの対応（<see cref="PreviewMaterialBindings.Resolve"/>）。
    /// 読むだけで、マテリアルには触れない。
    /// </summary>
    public sealed class PreviewMaterialBinding
    {
        public Material Source { get; internal set; }
        public PreviewMaterialKind Kind { get; internal set; }
        public string ShaderName { get; internal set; }
        /// <summary>何の対応か（例: "lilToon 2.3.4 · Standard/Opaque"、"Standard (built-in)"、"Main texture only"）。</summary>
        public string Summary { get; internal set; }
        public IReadOnlyList<PreviewChannelBinding> Channels { get; internal set; } = Array.Empty<PreviewChannelBinding>();
        /// <summary>対応の無いチャンネルと理由（そのプロパティは元のテクスチャのまま見せる）。</summary>
        public IReadOnlyList<(PaintChannel Channel, string Reason)> Unmapped { get; internal set; } = Array.Empty<(PaintChannel, string)>();
        /// <summary>対応の決め方についての知らせ（確かめていない lilToon で、メインのテクスチャだけにした理由など）。</summary>
        public IReadOnlyList<string> Remarks { get; internal set; } = Array.Empty<string>();
        /// <summary>中立で見せる理由（使えるなら null）。</summary>
        public string Unusable { get; internal set; }
        /// <summary>lilToon のときの検査の結果。</summary>
        public LilToonReport LilToon { get; internal set; }
        /// <summary>欄で手で決めた流し込み先を含むか（<see cref="PreviewMaterialBindings.WithRoutes"/>）。</summary>
        public bool HandSet { get; internal set; }
        /// <summary>対応を決めたときの元のマテリアルの状態（変わったら決め直す）。</summary>
        internal int SourceDirtyCount, ShaderId;
        public bool CanShow => Unusable == null && Source != null;
        public PreviewChannelBinding For(PaintChannel channel) => Channels.FirstOrDefault(c => c.Channel == channel);

        /// <summary>
        /// 見せ方の注意（表示している複製の値で調べる）: 使うチャンネルのうち、対応が無いもの、マテリアルのトグル・キーワード・
        /// シェーダーの設定で今は効かないもの、倍率が 0 で見えないもの、タイリングで筆の位置とずれるもの。
        /// </summary>
        public List<string> Notes(Material shown, IEnumerable<PaintChannel> used)
        {
            var notes = new List<string>();
            if (!CanShow) { if (Unusable != null) notes.Add(Unusable); return notes; }
            var usedSet = new HashSet<PaintChannel>(used ?? Enumerable.Empty<PaintChannel>());
            notes.AddRange(Remarks);
            foreach (var (c, reason) in Unmapped) if (usedSet.Contains(c)) notes.Add(L.Tr(c.ToString()) + ": " + reason);
            var material = shown != null ? shown : Source;
            foreach (var b in Channels.Where(b => usedSet.Contains(b.Channel)))
            {
                string channel = L.Tr(b.Channel.ToString());
                if (LilToon != null)
                {
                    var mapped = LilToon.Channel(b.Channel);
                    foreach (var c in mapped?.Conditions ?? Array.Empty<LilToonCondition>())
                    {
                        if (c.Kind == "property" && material.HasProperty(c.Name) && material.GetFloat(c.Name) == 0)
                            notes.Add(L.Tr("{0}: {1} is off on this material, so the painted {0} does not show.", channel, c.Name));
                        else if (c.Kind == "keyword" && !material.IsKeywordEnabled(c.Name))
                            notes.Add(L.Tr("{0}: the keyword {1} is off on this material, so the painted {0} does not show.", channel, c.Name));
                        else if (c.Kind == "compile" && !c.Satisfied)
                            notes.Add(L.Tr("{0}: lilToon's shader setting strips {1}, so the painted {0} does not show (YoluPainter does not change lilToon's settings).", channel, c.Name));
                    }
                }
                foreach (var scale in ZeroScales(b)) if (material.HasProperty(scale) && IsZero(material, scale)) notes.Add(L.Tr("{0}: {1} is 0 (black) on this material, so the painted {0} has no visible effect.", channel, scale));
                int index = material.shader != null ? material.shader.FindPropertyIndex(b.Property) : -1;
                if (index >= 0 && (material.shader.GetPropertyFlags(index) & ShaderPropertyFlags.NoScaleOffset) == 0)
                {
                    var scale = material.GetTextureScale(b.Property); var offset = material.GetTextureOffset(b.Property);
                    if (scale != Vector2.one || offset != Vector2.zero)
                        notes.Add(L.Tr("{0}: the material tiles or offsets {1} (scale {2}, offset {3}); the preview shows it like the material does, while the brush paints UV0 directly.", channel, b.Property,
                            scale.x.ToString("0.###", CultureInfo.InvariantCulture) + " × " + scale.y.ToString("0.###", CultureInfo.InvariantCulture),
                            offset.x.ToString("0.###", CultureInfo.InvariantCulture) + ", " + offset.y.ToString("0.###", CultureInfo.InvariantCulture)));
                }
            }
            return notes.Distinct().ToList();
        }

        IEnumerable<string> ZeroScales(PreviewChannelBinding b)
        {
            switch (b.Channel)
            {
                case PaintChannel.Emission: yield return "_EmissionColor"; break;
                case PaintChannel.Metallic: if (Kind == PreviewMaterialKind.LilToon) yield return "_Metallic"; break;
                case PaintChannel.Roughness: if (Kind == PreviewMaterialKind.LilToon) yield return "_Smoothness"; else if (Kind == PreviewMaterialKind.Standard) yield return "_GlossMapScale"; break;
                case PaintChannel.Height: yield return "_Parallax"; break;
                case PaintChannel.Normal: yield return "_BumpScale"; break;
            }
        }
        static bool IsZero(Material m, string property)
        {
            int i = m.shader.FindPropertyIndex(property);
            if (i < 0) return false;
            if (m.shader.GetPropertyType(i) == ShaderPropertyType.Color) { var c = m.GetColor(property); return c.r == 0 && c.g == 0 && c.b == 0; }
            return m.GetFloat(property) == 0;
        }
    }

    /// <summary>
    /// 元のマテリアルのシェーダーごとの、確かめた対応だけを返す。lilToon は <see cref="LilToonAdapter"/> が「適用できる」とした版・バリアントの
    /// <see cref="LilToonChannel"/> のとおり（似たシェーダーへ推測で当てない）。ビルトインの Standard は Unity 2022.3 の
    /// CGIncludes/UnityStandardInput.cginc（Albedo・MetallicGloss・Emission・NormalInTangentSpace・Parallax）で確かめたプロパティと
    /// キーワード。それ以外は、どのシェーダーでも意味が決まっているメインのテクスチャ（[MainTexture] か _MainTex）に Color だけ。
    /// </summary>
    public static class PreviewMaterialBindings
    {
        public const string StandardShaderName = "Standard";
        const string BuiltinExtra = "Resources/unity_builtin_extra";

        public static PreviewMaterialBinding Resolve(Material source) => Resolve(source, null);

        internal static PreviewMaterialBinding Resolve(Material source, LilToonAdapter.Options options)
        {
            var b = new PreviewMaterialBinding { Source = source };
            if (source == null) { b.Kind = PreviewMaterialKind.NoMaterial; b.Unusable = L.Tr("This material slot has no source material (the demo cube or an unassigned slot), so it is shown neutral."); return b; }
            b.SourceDirtyCount = EditorUtility.GetDirtyCount(source);
            var shader = source.shader;
            b.ShaderId = shader != null ? shader.GetInstanceID() : 0;
            b.ShaderName = shader != null ? shader.name : null;
            string broken = ShaderProblem(shader);
            if (broken != null) return Unusable(b, broken);
            if (GraphicsSettings.currentRenderPipeline != null)
                return Unusable(b, L.Tr("The project uses a scriptable render pipeline ({0}); the preview draws with the built-in pipeline, so {1} cannot be shown. Shown neutral.", GraphicsSettings.currentRenderPipeline.GetType().Name, shader.name));

            var lil = LilToonAdapter.Inspect(source, options);
            if (lil.IsApplicable)
            {
                if (LilToonVerified.TryGet(lil.Version, out var release) && release.Variants.TryGetValue(lil.ShaderName, out var variant) && variant.ForwardPass != null)
                {
                    string passProblem = ShaderProblem(Shader.Find(variant.ForwardPass));
                    if (passProblem != null) return Unusable(b, L.Tr("lilToon's forward pass {0} cannot be used: {1}", variant.ForwardPass, passProblem));
                }
                return LilToonBinding(b, lil);
            }
            if (IsVerifiedStandard(shader, out string standardProblem)) return StandardBinding(b, source);
            var remarks = new List<string>();
            if (lil.IsLilToon)
            {
                // 確かめていない lilToon には推し量りも当てない（似ているだけで当てない）。メインのテクスチャに Color だけ
                var mainOnly = MainTextureBinding(b, shader);
                remarks.Add(L.Tr("lilToon {0} is not verified for this material, so only the main texture is used: {1}", lil.Version, string.Join(" ", lil.Reasons)));
                var unmapped = new List<(PaintChannel, string)>();
                foreach (var c in (PaintChannel[])Enum.GetValues(typeof(PaintChannel)))
                    if (c != PaintChannel.Color || mainOnly.Channels.Count == 0)
                        unmapped.Add((c, c == PaintChannel.Color ? L.Tr("this shader has no main texture ([MainTexture] or _MainTex).") : L.Tr("not shown, because the mapping of this shader is not verified (only Color goes into the main texture).")));
                mainOnly.Unmapped = unmapped; mainOnly.Remarks = remarks;
                return mainOnly;
            }
            if (standardProblem != null) remarks.Add(standardProblem);
            var guessed = GuessedBinding(b, shader);
            if (guessed.Kind == PreviewMaterialKind.Guessed) remarks.Add(L.Tr("The mapping of {0} is guessed from its property names (not verified).", shader.name));
            guessed.Remarks = remarks;
            return guessed;
        }

        // ───────── それ以外: 名前と属性から推し量る（書き出しのテンプレートの Unity Standard / URP Lit・HDRP Lit の名前を含む） ─────────

        static readonly string[] NormalNames = { "_BumpMap", "_NormalMap", "_NormalTex", "_Normal" };
        static readonly string[] EmissionNames = { "_EmissionMap", "_EmissiveColorMap", "_EmissionTex", "_EmissiveMap", "_Emissive_Tex", "_EmissionColorTex" };
        static readonly string[] MetallicNames = { "_MetallicMap", "_MetallicTex", "_MetalMap", "_MetallicTexture" };
        static readonly string[] RoughnessNames = { "_RoughnessMap", "_RoughnessTex", "_RoughnessTexture" };
        static readonly string[] SmoothnessNames = { "_SmoothnessMap", "_SmoothnessTex", "_GlossMap", "_GlossinessMap", "_SmoothnessTexture" };
        static readonly string[] HeightNames = { "_ParallaxMap", "_HeightMap", "_Heightmap", "_HeightTex" };
        static readonly string[] MainNames = { "_BaseMap", "_BaseColorMap", "_AlbedoMap", "_Albedo" };
        /// <summary>テクスチャを入れたときにシェーダーのインスペクターが付けるキーワード（シェーダーにあれば複製で有効にする）。</summary>
        static readonly Dictionary<string, string> TextureKeywords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "_BumpMap", "_NORMALMAP" }, { "_NormalMap", "_NORMALMAP" }, { "_EmissionMap", "_EMISSION" }, { "_EmissiveColorMap", "_EMISSIVE_COLOR_MAP" },
            { "_MetallicGlossMap", "_METALLICGLOSSMAP" }, { "_ParallaxMap", "_PARALLAXMAP" }, { "_HeightMap", "_HEIGHTMAP" },
        };

        /// <summary>シェーダーの 2D テクスチャのプロパティ（並びの順）。</summary>
        internal static List<string> TextureProperties(Shader shader)
        {
            var list = new List<string>();
            if (shader == null) return list;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
                if (shader.GetPropertyType(i) == ShaderPropertyType.Texture && shader.GetPropertyTextureDimension(i) == TextureDimension.Tex2D && !list.Contains(shader.GetPropertyName(i)))
                    list.Add(shader.GetPropertyName(i));
            return list;
        }

        static string FirstOf(List<string> textures, string[] names) => names.FirstOrDefault(textures.Contains);

        /// <summary>名前と属性から推し量った流し込み先（Color: [MainTexture]・_MainTex・_BaseMap など、Normal: [Normal] の印か名前、Emission・
        /// Height は名前、Metallic と Roughness は _MetallicGlossMap（Standard と同じ R と A）か別々のマップ）。</summary>
        static PreviewMaterialBinding GuessedBinding(PreviewMaterialBinding b, Shader shader)
        {
            var textures = TextureProperties(shader);
            var channels = new List<PreviewChannelBinding>();
            void Add(PaintChannel c, string property, PreviewPacking packing, string reading)
                => channels.Add(new PreviewChannelBinding { Channel = c, Property = property, Packing = packing, Keywords = KeywordsFor(shader, property), Reading = reading + " " + L.Tr("(guessed from its name)") });
            string main = MainTextureProperty(shader) ?? FirstOf(textures, MainNames);
            if (main != null) channels.Add(new PreviewChannelBinding { Channel = PaintChannel.Color, Property = main, Packing = PreviewPacking.Color, Reading = main + " (sRGB)" });
            string normal = FirstOf(textures, NormalNames);
            if (normal == null)
                foreach (var t in textures) { int i = shader.FindPropertyIndex(t); if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.Normal) != 0) { normal = t; break; } }
            if (normal != null) Add(PaintChannel.Normal, normal, PreviewPacking.Normal, normal + " (normal map)");
            string emission = FirstOf(textures, EmissionNames) ?? textures.FirstOrDefault(t => t.IndexOf("emissi", StringComparison.OrdinalIgnoreCase) >= 0 && t != main);
            if (emission != null) Add(PaintChannel.Emission, emission, PreviewPacking.Color, emission + " (sRGB)");
            if (textures.Contains("_MetallicGlossMap"))
            {
                Add(PaintChannel.Metallic, "_MetallicGlossMap", PreviewPacking.MetallicSmoothness, "_MetallicGlossMap.r = Metallic");
                Add(PaintChannel.Roughness, "_MetallicGlossMap", PreviewPacking.MetallicSmoothness, "_MetallicGlossMap.a = 1 − Roughness");
            }
            else
            {
                string metallic = FirstOf(textures, MetallicNames);
                if (metallic != null) Add(PaintChannel.Metallic, metallic, PreviewPacking.Value, metallic + ".r = Metallic");
                string rough = FirstOf(textures, RoughnessNames), smooth = rough == null ? FirstOf(textures, SmoothnessNames) : null;
                if (rough != null) Add(PaintChannel.Roughness, rough, PreviewPacking.Value, rough + ".r = Roughness");
                else if (smooth != null) Add(PaintChannel.Roughness, smooth, PreviewPacking.InvertedValue, smooth + ".r = 1 − Roughness");
            }
            string height = FirstOf(textures, HeightNames);
            if (height != null) Add(PaintChannel.Height, height, PreviewPacking.Value, height + " = Height");
            b.Kind = channels.Count > (main != null ? 1 : 0) ? PreviewMaterialKind.Guessed : PreviewMaterialKind.MainTexture;
            b.Summary = b.Kind == PreviewMaterialKind.Guessed ? L.Tr("Guessed from the property names") : main != null ? L.Tr("Main texture only ({0})", main) : L.Tr("As the material is (no main texture)");
            b.Channels = channels.OrderBy(c => c.Channel).ToList();
            var unmapped = new List<(PaintChannel, string)>();
            foreach (var c in (PaintChannel[])Enum.GetValues(typeof(PaintChannel)))
                if (!channels.Any(x => x.Channel == c))
                    unmapped.Add((c, c == PaintChannel.Color ? L.Tr("this shader has no main texture ([MainTexture] or _MainTex).") : L.Tr("not shown: no property of this shader looks like it takes this channel (the mapping is not verified; set it under Channels in the Material panel).")));
            b.Unmapped = unmapped;
            return b;
        }

        static IReadOnlyList<string> KeywordsFor(Shader shader, string property)
            => TextureKeywords.TryGetValue(property, out var k) && shader.keywordSpace.keywordNames.Contains(k) ? new[] { k } : Array.Empty<string>();

        // ───────── 手で決めた流し込み先 ─────────

        /// <summary>チャンネルに選べる詰め方（Color と Emission は色、Normal は法線、値のチャンネルは値・1 − 値・Standard の R と A）。</summary>
        public static IReadOnlyList<PreviewPacking> PackingsFor(PaintChannel channel)
        {
            switch (channel)
            {
                case PaintChannel.Color: case PaintChannel.Emission: return new[] { PreviewPacking.Color };
                case PaintChannel.Normal: return new[] { PreviewPacking.Normal };
                case PaintChannel.Metallic: return new[] { PreviewPacking.Value, PreviewPacking.MetallicSmoothness };
                case PaintChannel.Roughness: return new[] { PreviewPacking.InvertedValue, PreviewPacking.Value, PreviewPacking.MetallicSmoothness };
                default: return new[] { PreviewPacking.Value, PreviewPacking.InvertedValue };
            }
        }

        /// <summary>手で決めた流し込み先が使えない理由（使えれば null。プロパティが空なら「見せない」で使える）。</summary>
        public static string RouteProblem(Shader shader, PreviewChannelRoute route)
        {
            if (route == null) return "No route.";
            if (string.IsNullOrEmpty(route.property)) return null;
            if (shader == null) return L.Tr("The material has no shader.");
            int i = shader.FindPropertyIndex(route.property);
            if (i < 0) return L.Tr("the shader {0} has no property {1}.", shader.name, route.property);
            if (shader.GetPropertyType(i) != ShaderPropertyType.Texture || shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) return L.Tr("{0} is not a 2D texture.", route.property);
            if (!PackingsFor(route.channel).Contains(route.packing)) return L.Tr("{0} cannot be packed as {1}.", L.Tr(route.channel.ToString()), route.packing);
            return null;
        }

        /// <summary>
        /// 自動の対応に、手で決めた流し込み先を重ねる（チャンネルごとに置き換え。プロパティが空のものは見せない）。使えない流し込み先（無い
        /// プロパティ・型・詰め方、ほかのチャンネルが別の詰め方で使うプロパティ）は使わず、理由を <see cref="PreviewMaterialBinding.Remarks"/> に足す。
        /// 見せられない対応（中立に戻すもの）はそのまま返す。
        /// </summary>
        public static PreviewMaterialBinding WithRoutes(PreviewMaterialBinding auto, IReadOnlyList<PreviewChannelRoute> routes)
        {
            if (auto == null || !auto.CanShow || routes == null || routes.Count == 0) return auto;
            var shader = auto.Source.shader;
            var channels = auto.Channels.ToList(); var unmapped = auto.Unmapped.ToList(); var remarks = auto.Remarks.ToList();
            bool changed = false;
            foreach (var r in routes)
            {
                if (r == null) continue;
                string channel = L.Tr(r.channel.ToString());
                string why = RouteProblem(shader, r);
                if (why != null) { remarks.Add(L.Tr("{0}: the route set by hand is not used: {1}", channel, why)); continue; }
                var clash = string.IsNullOrEmpty(r.property) ? null : channels.FirstOrDefault(c => c.Channel != r.channel && c.Property == r.property && !(c.Packing == PreviewPacking.MetallicSmoothness && r.packing == PreviewPacking.MetallicSmoothness));
                if (clash != null) { remarks.Add(L.Tr("{0}: the route set by hand is not used: {1} already takes {2} packed another way.", channel, L.Tr(clash.Channel.ToString()), r.property)); continue; }
                channels.RemoveAll(c => c.Channel == r.channel); unmapped.RemoveAll(u => u.Channel == r.channel); changed = true;
                if (string.IsNullOrEmpty(r.property)) { unmapped.Add((r.channel, L.Tr("turned off under Channels in the Material panel."))); continue; }
                channels.Add(new PreviewChannelBinding { Channel = r.channel, Property = r.property, Packing = r.packing, Keywords = KeywordsFor(shader, r.property), Reading = r.property + " (" + PackingLabel(r.packing) + ", " + L.Tr("set by hand") + ")" });
            }
            var b = new PreviewMaterialBinding
            {
                Source = auto.Source, Kind = auto.Kind, ShaderName = auto.ShaderName, Summary = changed ? auto.Summary + " · " + L.Tr("routes set by hand") : auto.Summary,
                Channels = channels.OrderBy(c => c.Channel).ToList(), Unmapped = unmapped, Remarks = remarks, LilToon = auto.LilToon, HandSet = changed,
                SourceDirtyCount = auto.SourceDirtyCount, ShaderId = auto.ShaderId,
            };
            return b;
        }

        /// <summary>詰め方の名前（欄に出す）。</summary>
        public static string PackingLabel(PreviewPacking packing)
        {
            switch (packing)
            {
                case PreviewPacking.Color: return L.TrIn("packing", "Color");
                case PreviewPacking.Value: return L.TrIn("packing", "Value");
                case PreviewPacking.InvertedValue: return L.TrIn("packing", "1 − value");
                case PreviewPacking.Normal: return L.TrIn("packing", "Normal map");
                default: return L.TrIn("packing", "Metallic (R) + smoothness (A)");
            }
        }

        /// <summary>シェーダーが使えないときの理由（使えれば null）。</summary>
        internal static string ShaderProblem(Shader shader)
        {
            if (shader == null) return L.Tr("The material has no shader.");
            if (shader.name == "Hidden/InternalErrorShader") return L.Tr("The shader is missing or failed to compile (Hidden/InternalErrorShader).");
            if (ShaderUtil.ShaderHasError(shader)) return L.Tr("The shader {0} has compile errors.", shader.name);
            if (!shader.isSupported) return L.Tr("The shader {0} is not supported on this graphics device.", shader.name);
            return null;
        }

        static PreviewMaterialBinding Unusable(PreviewMaterialBinding b, string reason) { b.Kind = PreviewMaterialKind.Unusable; b.Unusable = reason; b.Summary = L.Tr("Neutral (the source shader cannot be shown)"); return b; }

        // ───────── lilToon ─────────

        static PreviewMaterialBinding LilToonBinding(PreviewMaterialBinding b, LilToonReport report)
        {
            b.Kind = PreviewMaterialKind.LilToon; b.LilToon = report;
            var v = report.Variant;
            b.Summary = "lilToon " + report.Version + " · " + v.Family + "/" + v.RenderMode + (v.Outline ? "+Outline" : "");
            var channels = new List<PreviewChannelBinding>(); var unmapped = new List<(PaintChannel, string)>();
            foreach (var c in report.Channels)
            {
                if (!c.IsMapped) { unmapped.Add((c.Channel, c.UnmappedReason)); continue; }
                PreviewPacking packing; string reading;
                switch (c.Channel)
                {
                    case PaintChannel.Color: packing = PreviewPacking.Color; reading = c.Property + " (sRGB) × _Color"; break;
                    case PaintChannel.Emission: packing = PreviewPacking.Color; reading = c.Property + " (sRGB) × _EmissionColor"; break;
                    case PaintChannel.Roughness: packing = PreviewPacking.InvertedValue; reading = c.Property + ".r = 1 − Roughness, × _Smoothness"; break;
                    case PaintChannel.Normal: packing = PreviewPacking.Normal; reading = c.Property + " (normal map) × _BumpScale"; break;
                    case PaintChannel.Metallic: packing = PreviewPacking.Value; reading = c.Property + ".r × _Metallic"; break;
                    default: packing = PreviewPacking.Value; reading = c.Property + ".r, (r − _ParallaxOffset) × _Parallax"; break;
                }
                channels.Add(new PreviewChannelBinding { Channel = c.Channel, Property = c.Property, Packing = packing, Reading = reading });
            }
            b.Channels = channels; b.Unmapped = unmapped;
            return b;
        }

        // ───────── ビルトインの Standard ─────────

        static readonly (string name, ShaderPropertyType[] types)[] StandardProperties =
        {
            ("_Color", new[] { ShaderPropertyType.Color }), ("_MainTex", new[] { ShaderPropertyType.Texture }),
            ("_BumpMap", new[] { ShaderPropertyType.Texture }), ("_BumpScale", new[] { ShaderPropertyType.Float, ShaderPropertyType.Range }),
            ("_EmissionColor", new[] { ShaderPropertyType.Color }), ("_EmissionMap", new[] { ShaderPropertyType.Texture }),
            ("_MetallicGlossMap", new[] { ShaderPropertyType.Texture }), ("_Metallic", new[] { ShaderPropertyType.Float, ShaderPropertyType.Range }),
            ("_Glossiness", new[] { ShaderPropertyType.Float, ShaderPropertyType.Range }), ("_GlossMapScale", new[] { ShaderPropertyType.Float, ShaderPropertyType.Range }),
            ("_SmoothnessTextureChannel", new[] { ShaderPropertyType.Float, ShaderPropertyType.Range }),
            ("_ParallaxMap", new[] { ShaderPropertyType.Texture }), ("_Parallax", new[] { ShaderPropertyType.Float, ShaderPropertyType.Range }),
        };
        static readonly string[] StandardKeywords = { "_NORMALMAP", "_EMISSION", "_METALLICGLOSSMAP", "_PARALLAXMAP", "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A" };

        /// <summary>ビルトインの Standard（名前と置き場所、プロパティの名前・型・[Normal]、キーワードがあること）か。違えば理由。</summary>
        internal static bool IsVerifiedStandard(Shader shader, out string problem)
        {
            problem = null;
            if (shader == null || shader.name != StandardShaderName) return false;
            string path = AssetDatabase.GetAssetPath(shader);
            if (path != BuiltinExtra) { problem = L.Tr("A shader named Standard at {0} is not the built-in Standard shader, so its mapping is not used.", string.IsNullOrEmpty(path) ? "(memory)" : path); return false; }
            foreach (var (name, types) in StandardProperties)
            {
                int i = shader.FindPropertyIndex(name);
                if (i < 0 || !types.Contains(shader.GetPropertyType(i))) { problem = L.Tr("The built-in Standard shader in this Unity has no {0} of the expected type, so its mapping is not used.", name); return false; }
            }
            if ((shader.GetPropertyFlags(shader.FindPropertyIndex("_BumpMap")) & ShaderPropertyFlags.Normal) == 0) { problem = L.Tr("The built-in Standard shader in this Unity does not declare _BumpMap as [Normal], so its mapping is not used."); return false; }
            foreach (var k in StandardKeywords)
                if (!shader.keywordSpace.keywordNames.Contains(k)) { problem = L.Tr("The built-in Standard shader in this Unity has no keyword {0}, so its mapping is not used.", k); return false; }
            return true;
        }

        static PreviewMaterialBinding StandardBinding(PreviewMaterialBinding b, Material m)
        {
            b.Kind = PreviewMaterialKind.Standard; b.Summary = L.Tr("Standard (built-in)");
            bool albedoAlphaSmoothness = m.GetFloat("_SmoothnessTextureChannel") > .5f;
            var channels = new List<PreviewChannelBinding>
            {
                new PreviewChannelBinding { Channel = PaintChannel.Color, Property = "_MainTex", Packing = PreviewPacking.Color, Reading = "_MainTex (sRGB) × _Color" + (albedoAlphaSmoothness ? "; alpha is the smoothness (_SmoothnessTextureChannel = Albedo Alpha)" : "") },
                new PreviewChannelBinding { Channel = PaintChannel.Normal, Property = "_BumpMap", Packing = PreviewPacking.Normal, Keywords = new[] { "_NORMALMAP" }, Reading = "_BumpMap (normal map) × _BumpScale" },
                new PreviewChannelBinding { Channel = PaintChannel.Emission, Property = "_EmissionMap", Packing = PreviewPacking.Color, Keywords = new[] { "_EMISSION" }, Reading = "_EmissionMap.rgb (sRGB) × _EmissionColor (alpha is not read)" },
                new PreviewChannelBinding { Channel = PaintChannel.Metallic, Property = "_MetallicGlossMap", Packing = PreviewPacking.MetallicSmoothness, Keywords = new[] { "_METALLICGLOSSMAP" }, Reading = "_MetallicGlossMap.r = Metallic" },
                new PreviewChannelBinding { Channel = PaintChannel.Height, Property = "_ParallaxMap", Packing = PreviewPacking.Value, Keywords = new[] { "_PARALLAXMAP" }, Reading = "_ParallaxMap.g × _Parallax (UV parallax)" },
            };
            var unmapped = new List<(PaintChannel, string)>();
            if (albedoAlphaSmoothness) unmapped.Add((PaintChannel.Roughness, L.Tr("this Standard material reads the smoothness from the albedo alpha (_SmoothnessTextureChannel), not from _MetallicGlossMap.")));
            else channels.Add(new PreviewChannelBinding { Channel = PaintChannel.Roughness, Property = "_MetallicGlossMap", Packing = PreviewPacking.MetallicSmoothness, Keywords = new[] { "_METALLICGLOSSMAP" }, Reading = "_MetallicGlossMap.a = 1 − Roughness, × _GlossMapScale" });
            b.Channels = channels.OrderBy(c => c.Channel).ToList(); b.Unmapped = unmapped;
            return b;
        }

        // ───────── それ以外: メインのテクスチャに Color だけ ─────────

        /// <summary>メインのテクスチャのプロパティ（[MainTexture] の 2D テクスチャ、無ければ _MainTex）。無ければ null。</summary>
        internal static string MainTextureProperty(Shader shader)
        {
            if (shader == null) return null;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
                if (shader.GetPropertyType(i) == ShaderPropertyType.Texture && (shader.GetPropertyFlags(i) & ShaderPropertyFlags.MainTexture) != 0 && shader.GetPropertyTextureDimension(i) == TextureDimension.Tex2D)
                    return shader.GetPropertyName(i);
            int main = shader.FindPropertyIndex("_MainTex");
            return main >= 0 && shader.GetPropertyType(main) == ShaderPropertyType.Texture && shader.GetPropertyTextureDimension(main) == TextureDimension.Tex2D ? "_MainTex" : null;
        }

        static PreviewMaterialBinding MainTextureBinding(PreviewMaterialBinding b, Shader shader)
        {
            b.Kind = PreviewMaterialKind.MainTexture;
            string main = MainTextureProperty(shader);
            b.Summary = main != null ? L.Tr("Main texture only ({0})", main) : L.Tr("As the material is (no main texture)");
            b.Channels = main == null ? Array.Empty<PreviewChannelBinding>() : new[] { new PreviewChannelBinding { Channel = PaintChannel.Color, Property = main, Packing = PreviewPacking.Color, Reading = main + " (sRGB)" } };
            return b;
        }
    }
}
