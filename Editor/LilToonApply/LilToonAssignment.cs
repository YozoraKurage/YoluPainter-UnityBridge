using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.LilToon;

namespace Yozolab.YoluPainter.Editor.LilToonApply
{
    /// <summary>割り当て 1 件: チャンネルを lilToon 用に変換した PNG と、マテリアルに入れる値。</summary>
    public sealed class LilToonAssignmentItem
    {
        public PaintChannel Channel { get; internal set; }
        public string Property { get; internal set; }
        /// <summary>書き出す PNG（Assets からのパス）。</summary>
        public string AssetPath { get; internal set; }
        public bool NewFile { get; internal set; }
        /// <summary>画素の変換の説明（例: "r = 1 − roughness"）。</summary>
        public string Conversion { get; internal set; }
        public LilToonColorSpace ColorSpace { get; internal set; }
        /// <summary>1 にするトグル（今の値）。</summary>
        public IReadOnlyList<(string name, float current)> Toggles { get; internal set; } = new (string, float)[0];
        /// <summary>有効にするキーワード（lilToonMulti）。</summary>
        public IReadOnlyList<string> Keywords { get; internal set; } = new string[0];
        /// <summary>適用はするが、このままでは表示に効かない・設定が食い違う、といった知らせ。</summary>
        public IReadOnlyList<string> Warnings { get; internal set; } = new string[0];
    }

    /// <summary>何を書き出し、マテリアルの何を変えるかの一覧。作るだけでは何も変えない。</summary>
    public sealed class LilToonAssignmentPlan
    {
        public Material Material { get; internal set; }
        public string MaterialPath { get; internal set; }
        public LilToonReport Report { get; internal set; }
        public string Folder { get; internal set; }
        public bool CreatesFolder { get; internal set; }
        public IReadOnlyList<LilToonAssignmentItem> Items { get; internal set; } = new LilToonAssignmentItem[0];
        /// <summary>使っているが lilToon のこのバリアントに対応先が無いチャンネル（割り当てない）。</summary>
        public IReadOnlyList<string> Skipped { get; internal set; } = new string[0];
        /// <summary>割り当てられない理由。あれば何もしない。</summary>
        public IReadOnlyList<string> Refusals { get; internal set; } = new string[0];
        public bool CanApply => Refusals.Count == 0 && Items.Count > 0;

        /// <summary>確認ダイアログに出す文。</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            if (Refusals.Count > 0) { sb.Append("Cannot assign:\n"); foreach (var r in Refusals) sb.Append("• ").Append(r).Append('\n'); return sb.ToString(); }
            var v = Report.Variant;
            sb.Append("Material: ").Append(MaterialPath).Append("\nlilToon ").Append(Report.Version).Append(" — ").Append(v.Family).Append('/').Append(v.RenderMode).Append(v.Outline ? "+Outline" : "").Append(" (").Append(v.File).Append(")\n\n");
            sb.Append(CreatesFolder ? "Creates the folder " : "Writes into ").Append(Folder).Append(":\n");
            foreach (var i in Items) sb.Append("• ").Append(Path.GetFileName(i.AssetPath)).Append(i.NewFile ? " (new" + (i.ColorSpace == LilToonColorSpace.NormalMap ? ", imported as Normal map" : i.ColorSpace == LilToonColorSpace.Srgb ? ", sRGB" : ", linear") + ")" : " (replaced; import settings kept)").Append(" — ").Append(i.Conversion).Append('\n');
            sb.Append("\nChanges on the material (Unity's Undo can revert them):\n");
            foreach (var i in Items)
            {
                sb.Append("• ").Append(i.Property).Append(" ← ").Append(Path.GetFileName(i.AssetPath)).Append('\n');
                foreach (var t in i.Toggles) sb.Append("• ").Append(t.name).Append(": ").Append(t.current.ToString(CultureInfo.InvariantCulture)).Append(" → 1\n");
                foreach (var k in i.Keywords) sb.Append("• keyword ").Append(k).Append(" → enabled\n");
            }
            var warnings = Items.SelectMany(i => i.Warnings).ToList();
            if (warnings.Count > 0) { sb.Append("\nNotes:\n"); foreach (var w in warnings) sb.Append("• ").Append(w).Append('\n'); }
            if (Skipped.Count > 0) { sb.Append("\nNot assigned:\n"); foreach (var s in Skipped) sb.Append("• ").Append(s).Append('\n'); }
            sb.Append("\nNothing else on the material, the model or lilToon's settings is changed.");
            return sb.ToString();
        }
    }

    /// <summary>
    /// ドキュメントのチャンネルを lilToon のマテリアルに割り当てる。<see cref="LilToonAdapter.Inspect"/> が「適用できる」と確かめた
    /// バージョン・バリアントだけを対象にし（似たシェーダーへ推測で当てない）、lilToon がテクスチャをどう読むかに合わせて変換した
    /// PNG を書き出してから、確認の後でマテリアルに入れる。読み込み専用のアダプター（Editor/LilToon）とは分けて置く。
    /// </summary>
    public static class LilToonAssignment
    {
        /// <summary>割り当ての計画を作る（何も変えない）。folder は Assets の中のフォルダ（無ければ作る計画になる）。</summary>
        public static LilToonAssignmentPlan Plan(PaintDocument document, Material material, string folder, string stem) => Plan(document, material, folder, stem, null);

        internal static LilToonAssignmentPlan Plan(PaintDocument document, Material material, string folder, string stem, LilToonAdapter.Options options)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var plan = new LilToonAssignmentPlan { Material = material, Folder = folder };
            var refusals = new List<string>();
            LilToonAssignmentPlan Refuse(params string[] reasons) { refusals.AddRange(reasons); plan.Refusals = refusals; return plan; }
            if (material == null) return Refuse("This material slot has no source material (the demo cube or an unassigned slot).");
            string path = AssetDatabase.GetAssetPath(material);
            plan.MaterialPath = path;
            if (string.IsNullOrEmpty(path)) return Refuse("The material is not an asset, so a change could not be saved.");
            if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                return Refuse("The material is part of '" + path + "' (an imported model), which Unity re-creates on import. Extract the materials first (the model's Materials tab), then assign to the extracted material.");
            if (document.HasActiveStroke) return Refuse("Finish the stroke first.");
            var report = LilToonAdapter.Inspect(material, options);
            plan.Report = report;
            if (!report.IsApplicable) return Refuse(report.Reasons.Count > 0 ? report.Reasons.ToArray() : new[] { "lilToon is not applicable to this material." });
            if (string.IsNullOrEmpty(folder) || !(folder == "Assets" || folder.StartsWith("Assets/", StringComparison.Ordinal)) || folder.Contains(".."))
                return Refuse("The textures must go into a folder inside Assets so the material can use them.");
            if (string.IsNullOrEmpty(stem) || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return Refuse("Invalid file name stem.");
            plan.CreatesFolder = !AssetDatabase.IsValidFolder(folder);

            LilToonVerified.TryGet(report.Version, out var release);
            var variant = release.Variants[report.ShaderName];
            var used = YlpContent.UsedChannels(document);
            var items = new List<LilToonAssignmentItem>(); var skipped = new List<string>();
            foreach (var channel in used)
            {
                var mapped = report.Channel(channel);
                if (mapped == null || !mapped.IsMapped) { skipped.Add(channel + ": " + (mapped?.UnmappedReason ?? "no mapping")); continue; }
                var spec = variant.Channels.First(c => c.Channel == channel);
                string asset = folder + "/" + stem + "_lilToon_" + channel + ".png";
                var warnings = new List<string>();
                foreach (var condition in mapped.Conditions.Where(c => c.Kind == "compile" && !c.Satisfied))
                    warnings.Add(channel + ": lilToon's shader setting strips " + condition.Name + ", so " + spec.Texture + " will not show until it is enabled in lilToon (YoluPainter does not change lilToon's settings).");
                foreach (var scale in spec.Scales) warnings.AddRange(ZeroScale(material, channel, scale));
                bool exists = File.Exists(Path.GetFullPath(asset));
                if (exists && AssetImporter.GetAtPath(asset) is TextureImporter importer)
                {
                    if (spec.Space == LilToonColorSpace.NormalMap && importer.textureType != TextureImporterType.NormalMap) warnings.Add(Path.GetFileName(asset) + " exists and is imported as " + importer.textureType + "; lilToon reads " + spec.Texture + " as a Normal map. Its import settings are left as they are.");
                    else if (spec.Space != LilToonColorSpace.NormalMap && importer.sRGBTexture != (spec.Space == LilToonColorSpace.Srgb)) warnings.Add(Path.GetFileName(asset) + " exists and is imported as " + (importer.sRGBTexture ? "sRGB" : "linear") + "; " + channel + " expects " + (spec.Space == LilToonColorSpace.Srgb ? "sRGB" : "linear") + ". Its import settings are left as they are.");
                }
                var toggles = spec.Toggles.Concat(spec.KeywordSourceToggle == null ? new string[0] : new[] { spec.KeywordSourceToggle }).Distinct()
                    .Select(t => (t, material.GetFloat(t))).Where(t => t.Item2 != 1).ToList();
                items.Add(new LilToonAssignmentItem
                {
                    Channel = channel, Property = spec.Texture, AssetPath = asset, NewFile = !exists, ColorSpace = spec.Space, Conversion = ConversionOf(channel),
                    Toggles = toggles, Keywords = spec.Keywords.Where(k => !material.IsKeywordEnabled(k)).ToList(), Warnings = warnings,
                });
            }
            if (items.Count == 0) refusals.Add("No channel in use maps onto this lilToon variant" + (skipped.Count > 0 ? " (" + string.Join("; ", skipped) + ")" : "") + ".");
            plan.Items = items; plan.Skipped = skipped; plan.Refusals = refusals;
            return plan;
        }

        static IEnumerable<string> ZeroScale(Material material, PaintChannel channel, string scale)
        {
            if (scale == "_EmissionColor") { var c = material.GetColor(scale); if (c.r == 0 && c.g == 0 && c.b == 0) yield return channel + ": _EmissionColor is black, so the emission map has no visible effect until it is raised."; yield break; }
            if (scale == "_Color" || scale == "_ParallaxOffset") yield break;
            if (material.GetFloat(scale) == 0) yield return channel + ": " + scale + " is 0, so the map has no visible effect until it is raised.";
        }

        static string ConversionOf(PaintChannel channel)
        {
            switch (channel)
            {
                case PaintChannel.Color: return "RGBA as painted";
                case PaintChannel.Emission: return "RGBA as painted (alpha is lilToon's blend amount)";
                case PaintChannel.Roughness: return "r = 1 − roughness (lilToon reads smoothness)";
                case PaintChannel.Normal: return "tangent-space normal output (vector composite, Height → Normal when on); unpainted areas are flat";
                default: return "r = value; unpainted areas are 0";
            }
        }

        /// <summary>チャンネルの合成結果（straight RGBA）を lilToon が読む形の画素にする。データのチャンネルは塗っていない所（アルファ 0）を
        /// 0 とみなして値 × アルファで平らにし、Roughness は 1 − r（lilToon は平滑度を読む）、Normal は平らな法線 (128, 128, 255) の上に重ねる。</summary>
        internal static byte[] Convert(PaintChannel channel, byte[] rgba)
        {
            if (channel == PaintChannel.Color || channel == PaintChannel.Emission) return (byte[])rgba.Clone();
            var output = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int a = rgba[i + 3];
                if (channel == PaintChannel.Normal)
                {
                    output[i] = (byte)((rgba[i] * a + 128 * (255 - a) + 127) / 255); output[i + 1] = (byte)((rgba[i + 1] * a + 128 * (255 - a) + 127) / 255);
                    output[i + 2] = (byte)((rgba[i + 2] * a + 255 * (255 - a) + 127) / 255); output[i + 3] = 255;
                    continue;
                }
                int v = (rgba[i] * a + 127) / 255;
                if (channel == PaintChannel.Roughness) v = 255 - v;
                output[i] = output[i + 1] = output[i + 2] = (byte)v; output[i + 3] = 255;
            }
            return output;
        }

        /// <summary>計画どおりに PNG を書き出し（新しく作るファイルだけ取り込み設定を決める）、マテリアルに入れる。マテリアルの変更は
        /// Unity の Undo に 1 つで記録する。書き出したファイルは Undo では消えない。変更後のマテリアルを調べ直した結果を返す。</summary>
        public static LilToonReport Apply(LilToonAssignmentPlan plan, PaintDocument document) => Apply(plan, document, null);

        internal static LilToonReport Apply(LilToonAssignmentPlan plan, PaintDocument document, LilToonAdapter.Options options)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!plan.CanApply) throw new InvalidOperationException(string.Join(" ", plan.Refusals.Count > 0 ? plan.Refusals : new[] { "Nothing to assign." }));
            if (document.HasActiveStroke) throw new InvalidOperationException("Finish the stroke first.");
            var material = plan.Material;
            // 確認の間に変わっていないこと（シェーダーの差し替えなど）を確かめ直す
            var check = LilToonAdapter.Inspect(material, options);
            if (!check.IsApplicable || check.ShaderName != plan.Report.ShaderName || check.Version != plan.Report.Version)
                throw new InvalidOperationException("The material changed since the plan was made; nothing was changed. Plan again.");
            if (plan.CreatesFolder) CreateFolder(plan.Folder);
            foreach (var item in plan.Items)
            {
                bool isNew = !File.Exists(Path.GetFullPath(item.AssetPath));
                // Normal は Unity 向けの出力（ベクトルで合成・正規化、Height → Normal 込み、不透明・OpenGL）をそのまま使う
                var pixels = item.Channel == PaintChannel.Normal ? YlpContent.Image(document, PaintChannel.Normal) : Convert(item.Channel, document.Composite(item.Channel));
                File.WriteAllBytes(Path.GetFullPath(item.AssetPath), YlpContent.EncodePng(pixels, document.Width, document.Height));
                AssetDatabase.ImportAsset(item.AssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                if (!isNew || !(AssetImporter.GetAtPath(item.AssetPath) is TextureImporter importer)) continue;
                if (item.ColorSpace == LilToonColorSpace.NormalMap) importer.textureType = TextureImporterType.NormalMap;
                else { importer.sRGBTexture = item.ColorSpace == LilToonColorSpace.Srgb; importer.alphaIsTransparency = item.Channel == PaintChannel.Color; }
                importer.SaveAndReimport();
            }
            Undo.RecordObject(material, "YoluPainter: assign textures to lilToon");
            foreach (var item in plan.Items)
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(item.AssetPath);
                if (texture == null) throw new InvalidOperationException("Unity did not import " + item.AssetPath + " as a texture.");
                material.SetTexture(item.Property, texture);
                foreach (var toggle in item.Toggles) material.SetFloat(toggle.name, 1);
                foreach (var keyword in item.Keywords) material.EnableKeyword(keyword);
            }
            EditorUtility.SetDirty(material);
            return LilToonAdapter.Inspect(material, options);
        }

        static void CreateFolder(string folder)
        {
            string parent = "Assets";
            foreach (var part in folder.Split('/').Skip(1))
            {
                string next = parent + "/" + part;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(parent, part);
                parent = next;
            }
        }
    }
}
