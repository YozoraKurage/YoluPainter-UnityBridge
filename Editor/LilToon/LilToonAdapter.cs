using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Yozolab.YoluPainter.Editor.LilToon
{
    /// <summary>
    /// マテリアルが実際にどの lilToon（バージョン・バリアント・パイプライン・コンパイル済みの機能）かを調べ、YoluPainter のチャンネルと
    /// lilToon のテクスチャプロパティの対応を返す。読むだけで、マテリアル・テクスチャ・インポート設定・Undo・dirty に触れない。
    /// lilToon にはコンパイル時に依存せず、シェーダーアセットの置き場所（パッケージ）、名前、プロパティ、ソースで判定する。
    /// 確かめたバージョン・バリアント以外は「適用できない」と返し、似た名前から推測しない。
    /// </summary>
    public static class LilToonAdapter
    {
        /// <summary>テスト用の差し替え口（バージョンやパイプラインの判定結果を偽る）。パッケージは書き換えない。</summary>
        internal sealed class Options
        {
            public string Version;
            public string RenderPipeline;
        }

        public static LilToonReport Inspect(Material material) => Inspect(material, null);

        internal static LilToonReport Inspect(Material material, Options options)
        {
            var report = new LilToonReport
            {
                RenderPipeline = options?.RenderPipeline ?? CurrentPipeline(),
                ProjectColorSpace = PlayerSettings.colorSpace.ToString(),
            };
            if (material == null) return NotLilToon(report, "No material.");
            Shader shader = material.shader;
            if (shader == null) return NotLilToon(report, "The material has no shader.");
            report.ShaderName = shader.name;
            if (shader.name == "Hidden/InternalErrorShader") return NotLilToon(report, "The material's shader is missing or failed to compile (Hidden/InternalErrorShader).");
            string path = AssetDatabase.GetAssetPath(shader);
            report.ShaderAssetPath = path;

            PackageInfo package = string.IsNullOrEmpty(path) ? null : PackageInfo.FindForAssetPath(path);
            if (package == null || package.name != LilToonVerified.PackageName)
            {
                bool looksLike = shader.name.IndexOf("liltoon", StringComparison.OrdinalIgnoreCase) >= 0 || shader.name.StartsWith("_lil/", StringComparison.Ordinal);
                if (path.EndsWith(".lilcontainer", StringComparison.OrdinalIgnoreCase))
                    return NotLilToon(report, "A lilToon-based custom shader (.lilcontainer) at '" + path + "'; custom shaders are not verified.");
                if (looksLike)
                    return NotLilToon(report, "The shader name looks like lilToon, but the shader asset ('" + (string.IsNullOrEmpty(path) ? "built-in or in memory" : path) +
                        "') is not part of the installed " + LilToonVerified.PackageName + " package; it is not treated as lilToon.");
                return NotLilToon(report, "Not a lilToon shader.");
            }

            report.IsLilToon = true;
            report.PackageName = package.name;
            report.Version = options?.Version ?? (string.IsNullOrEmpty(package.version) ? "unknown" : package.version);
            if (!LilToonVerified.TryGet(report.Version, out var release))
                return NotApplicable(report, "lilToon " + report.Version + " is not in the verified list (" + string.Join(", ", LilToonVerified.Versions) + ").");

            release.Variants.TryGetValue(shader.name, out var variant);
            if (variant != null)
                report.Variant = new LilToonVariantInfo { ShaderName = variant.ShaderName, File = variant.File, Family = variant.Family, RenderMode = variant.RenderMode, Outline = variant.Outline, MappingVerified = variant.Verified };
            if (report.RenderPipeline != release.Pipeline)
                return NotApplicable(report, "Render pipeline " + report.RenderPipeline + " is not verified for lilToon " + release.Version + " (verified: " + release.Pipeline + ").");
            if (variant == null)
                return NotApplicable(report, "Unknown lilToon " + release.Version + " variant '" + shader.name + "'.");
            if (Path.GetFileName(path) != variant.File)
                return NotApplicable(report, "The shader '" + shader.name + "' is in '" + Path.GetFileName(path) + "', but lilToon " + release.Version + " ships it as '" + variant.File + "'.");
            if (!variant.Verified)
                return NotApplicable(report, "lilToon variant '" + shader.name + "' is recognised, but " + variant.NotVerifiedReason + ".");

            int formatIndex = shader.FindPropertyIndex("_lilToonVersion");
            float format = formatIndex < 0 ? -1 : shader.GetPropertyDefaultFloatValue(formatIndex);
            if (formatIndex < 0 || (int)format != release.MaterialFormat)
                return NotApplicable(report, "The shader's _lilToonVersion default is " + (formatIndex < 0 ? "missing" : format.ToString()) + "; lilToon " + release.Version + " uses " + release.MaterialFormat + ".");

            var problems = new List<string>();
            foreach (var spec in variant.Channels.Where(c => c.Unmapped == null)) CheckProperties(shader, spec, problems);
            if (problems.Count > 0) return NotApplicable(report, problems.ToArray());

            HashSet<string> features = ReadCompiledFeatures(shader, path, package, variant, release, problems, out string featureSource);
            if (problems.Count > 0) return NotApplicable(report, problems.ToArray());

            report.Channels = Enum.GetValues(typeof(PaintChannel)).Cast<PaintChannel>()
                .Select(channel => BuildChannel(material, variant.Channels.FirstOrDefault(c => c.Channel == channel), channel, features, featureSource)).ToList();
            report.IsApplicable = true;
            return report;
        }

        static LilToonReport NotLilToon(LilToonReport report, string reason) { report.reasons.Add(reason); return report; }
        static LilToonReport NotApplicable(LilToonReport report, params string[] reasons) { report.reasons.AddRange(reasons); report.IsApplicable = false; return report; }

        static string CurrentPipeline()
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            return asset == null ? LilToonVerified.BuiltInPipeline : asset.GetType().FullName;
        }

        /// <summary>プロパティが、確かめた種類でシェーダーに宣言されているか。</summary>
        internal static void CheckProperties(Shader shader, ChannelSpec spec, List<string> problems)
        {
            Expect(shader, spec.Texture, problems, ShaderPropertyType.Texture);
            if (spec.Space == LilToonColorSpace.NormalMap)
            {
                int i = shader.FindPropertyIndex(spec.Texture);
                if (i >= 0 && (shader.GetPropertyFlags(i) & ShaderPropertyFlags.Normal) == 0) problems.Add("The shader does not declare " + spec.Texture + " as [Normal].");
            }
            foreach (var scale in spec.Scales)
                Expect(shader, scale, problems, scale == "_Color" || scale == "_EmissionColor" ? new[] { ShaderPropertyType.Color } : new[] { ShaderPropertyType.Float, ShaderPropertyType.Range });
            foreach (var toggle in spec.Toggles.Concat(spec.KeywordSourceToggle == null ? new string[0] : new[] { spec.KeywordSourceToggle }))
                Expect(shader, toggle, problems, ShaderPropertyType.Float, ShaderPropertyType.Range, ShaderPropertyType.Int);
        }

        static void Expect(Shader shader, string property, List<string> problems, params ShaderPropertyType[] types)
        {
            int i = shader.FindPropertyIndex(property);
            if (i < 0) { problems.Add("Expected property " + property + " is missing from the shader."); return; }
            var type = shader.GetPropertyType(i);
            if (!types.Contains(type)) { problems.Add("Property " + property + " is " + type + ", expected " + string.Join("/", types) + "."); return; }
            if (type == ShaderPropertyType.Texture && shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) problems.Add("Property " + property + " is not a 2D texture.");
        }

        static readonly Regex usePass = new Regex("UsePass\\s+\"(?<shader>[^\"]+)/(?<pass>[^\"/]+)\"", RegexOptions.Compiled);
        static readonly Regex hlslInclude = new Regex("HLSLINCLUDE(?<body>.*?)ENDHLSL", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex featureDefine = new Regex("^\\s*#define\\s+(?<name>LIL_FEATURE_\\w+)\\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>フォワードパスのソースを読み、コンパイルされている機能の #define を集める。Multi はキーワードで機能が決まるので、
        /// キーワードの宣言と置き換え表があることを確かめ、テクスチャ機能（常に定義）を返す。</summary>
        static HashSet<string> ReadCompiledFeatures(Shader shader, string path, PackageInfo package, VariantSpec variant, VerifiedRelease release, List<string> problems, out string source)
        {
            var features = new HashSet<string>(StringComparer.Ordinal);
            source = path;
            string text = ReadPackageFile(path, package, problems);
            if (text == null) return features;
            if (variant.ForwardPass != null)
            {
                var forward = usePass.Matches(text).Cast<Match>().FirstOrDefault(m => m.Groups["pass"].Value == "FORWARD");
                if (forward == null) { problems.Add("'" + path + "' has no UsePass for its FORWARD pass."); return features; }
                string passName = forward.Groups["shader"].Value;
                if (passName != variant.ForwardPass) { problems.Add("'" + path + "' uses '" + passName + "' for FORWARD; lilToon " + release.Version + " uses '" + variant.ForwardPass + "'."); return features; }
                Shader pass = Shader.Find(passName);
                string passPath = pass == null ? null : AssetDatabase.GetAssetPath(pass);
                if (pass == null || string.IsNullOrEmpty(passPath) || PackageInfo.FindForAssetPath(passPath)?.name != LilToonVerified.PackageName)
                { problems.Add("The pass shader '" + passName + "' is not found in the lilToon package."); return features; }
                source = passPath;
                text = ReadPackageFile(passPath, package, problems);
                if (text == null) return features;
                foreach (Match block in hlslInclude.Matches(text))
                    foreach (Match define in featureDefine.Matches(block.Groups["body"].Value)) features.Add(define.Groups["name"].Value);
            }
            else
            {
                // Multi: _UseBumpMap などは true に固定され、キーワードが機能を決める
                if (!Regex.IsMatch(text, "^\\s*#define\\s+LIL_MULTI\\s*$", RegexOptions.Multiline)) { problems.Add("'" + path + "' does not define LIL_MULTI."); return features; }
                if (!text.Contains("#include \"Includes/lil_replace_keywords.hlsl\"")) { problems.Add("'" + path + "' does not include lil_replace_keywords.hlsl."); return features; }
                foreach (var keyword in variant.Channels.SelectMany(c => c.Keywords).Distinct())
                    if (!Regex.IsMatch(text, "#pragma\\s+shader_feature_local\\s+" + Regex.Escape(keyword) + "\\s*$", RegexOptions.Multiline))
                        problems.Add("'" + path + "' does not declare the keyword " + keyword + ".");
                string replacePath = package.assetPath + "/Shader/Includes/lil_replace_keywords.hlsl";
                string replace = ReadPackageFile(replacePath, package, problems);
                if (replace == null) return features;
                var expected = new Dictionary<string, string> { { "_NORMALMAP", "LIL_FEATURE_NORMAL_1ST" }, { "_GLOSSYREFLECTIONS_OFF", "LIL_FEATURE_REFLECTION" }, { "_PARALLAXMAP", "LIL_FEATURE_PARALLAX" }, { "_EMISSION", "LIL_FEATURE_EMISSION_1ST" } };
                foreach (var pair in expected)
                    if (!Regex.IsMatch(replace, "#if defined\\(" + Regex.Escape(pair.Key) + "\\)\\s*#define\\s+" + pair.Value + "\\b"))
                        problems.Add("lil_replace_keywords.hlsl does not map " + pair.Key + " to " + pair.Value + ".");
                foreach (Match define in featureDefine.Matches(replace)) features.Add(define.Groups["name"].Value);
                foreach (var needed in new[] { "LIL_FEATURE_BumpMap", "LIL_FEATURE_SmoothnessTex", "LIL_FEATURE_MetallicGlossMap", "LIL_FEATURE_EmissionMap" })
                    if (!features.Contains(needed)) problems.Add("lil_replace_keywords.hlsl does not define " + needed + ".");
                source = replacePath;
            }
            if (!text.Contains("#include \"" + release.PipelineInclude + "\""))
                problems.Add("'" + source + "' is not generated for " + release.Pipeline + " (no #include \"" + release.PipelineInclude + "\").");
            return features;
        }

        static string ReadPackageFile(string assetPath, PackageInfo package, List<string> problems)
        {
            try
            {
                string full = assetPath.StartsWith(package.assetPath + "/", StringComparison.Ordinal)
                    ? Path.Combine(package.resolvedPath, assetPath.Substring(package.assetPath.Length + 1))
                    : Path.GetFullPath(assetPath);
                return File.ReadAllText(full);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                problems.Add("Cannot read '" + assetPath + "' to verify the compiled features (" + ex.Message + ").");
                return null;
            }
        }

        static LilToonChannel BuildChannel(Material material, ChannelSpec spec, PaintChannel channel, HashSet<string> features, string featureSource)
        {
            if (spec == null) return new LilToonChannel { Channel = channel, UnmappedReason = "No verified mapping." };
            if (spec.Unmapped != null) return new LilToonChannel { Channel = channel, UnmappedReason = spec.Unmapped };
            var result = new LilToonChannel
            {
                Channel = channel, IsMapped = true, Property = spec.Texture, Packing = spec.Packing, ScaleProperties = spec.Scales, ColorSpace = spec.Space, Note = spec.Note,
            };
            var conditions = new List<LilToonCondition>();
            var warnings = new List<string>();
            foreach (var toggle in spec.Toggles)
            {
                float value = material.GetFloat(toggle);
                conditions.Add(new LilToonCondition { Kind = "property", Name = toggle, Required = "on (≠ 0)", Current = value.ToString(System.Globalization.CultureInfo.InvariantCulture), Satisfied = value != 0, Source = "material" });
            }
            foreach (var keyword in spec.Keywords)
            {
                bool on = material.IsKeywordEnabled(keyword);
                conditions.Add(new LilToonCondition { Kind = "keyword", Name = keyword, Required = "enabled", Current = on ? "enabled" : "disabled", Satisfied = on, Source = "material" });
                if (spec.KeywordSourceToggle != null && (material.GetFloat(spec.KeywordSourceToggle) != 0) != on)
                    warnings.Add(spec.KeywordSourceToggle + " is " + material.GetFloat(spec.KeywordSourceToggle) + " but " + keyword + " is " + (on ? "enabled" : "disabled") + "; lilToonMulti follows the keyword (lilToon's inspector sets it from the toggle).");
            }
            foreach (var feature in spec.CompileFeatures)
            {
                bool defined = features.Contains(feature);
                conditions.Add(new LilToonCondition
                {
                    Kind = "compile", Name = feature, Required = "defined", Current = defined ? "defined" : "not defined (stripped by lilToon's shader setting)", Satisfied = defined, Source = featureSource,
                });
            }
            result.Conditions = conditions;

            Texture texture = material.GetTexture(spec.Texture);
            if (texture != null)
            {
                string texturePath = AssetDatabase.GetAssetPath(texture);
                result.TexturePath = texturePath ?? "";
                if (!string.IsNullOrEmpty(texturePath))
                {
                    result.TextureGuid = AssetDatabase.AssetPathToGUID(texturePath);
                    if (AssetImporter.GetAtPath(texturePath) is TextureImporter importer)
                    {
                        result.TextureImportSrgb = importer.sRGBTexture;
                        result.TextureImportType = importer.textureType.ToString();
                        if (spec.Space == LilToonColorSpace.NormalMap && importer.textureType != TextureImporterType.NormalMap)
                            warnings.Add(spec.Texture + " is imported as " + importer.textureType + "; lilToon decodes it as a Unity normal map (Texture Type: Normal map).");
                        else if (spec.Space == LilToonColorSpace.Srgb && !importer.sRGBTexture)
                            warnings.Add(spec.Texture + " is imported as linear; it holds colour and is expected to be sRGB.");
                        else if (spec.Space == LilToonColorSpace.Linear && importer.sRGBTexture)
                            warnings.Add(spec.Texture + " is imported as sRGB; it holds data (" + spec.Packing + ") and is expected to be linear.");
                    }
                }
                if (!(texture is Texture2D)) warnings.Add(spec.Texture + " holds a " + texture.GetType().Name + ", not a Texture2D asset.");
            }
            result.Warnings = warnings;
            return result;
        }
    }
}
