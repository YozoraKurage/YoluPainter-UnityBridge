using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>書き出しのテンプレート（Core の <see cref="ExportTemplate"/>）: 全部のテクスチャセットを、Unity の Standard / URP・HDRP・lilToon が読む
    /// 詰め合わせの PNG にしてフォルダへ書く。名前は &lt;名前&gt;[_&lt;セット名&gt;]_&lt;画像&gt;.png。書き出しのパディングを掛け、Assets の中なら新しく作った
    /// テクスチャにだけ取り込み設定（sRGB/リニア、法線はノーマルマップ）を付ける。既にあるテクスチャの取り込み設定と、マテリアルは変えない。</summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>書き出す画像の一覧（セット・画像・ファイルの名前）。読むものが無い画像は入らない。</summary>
        internal List<(TextureSet set, ExportImage image, string name)> PlanTemplateExport(ExportTemplate template, string stem)
        {
            SyncCurrentSet();
            var planned = new List<(TextureSet, ExportImage, string)>();
            foreach (var set in textureSets)
            {
                bool hasOcclusion = TemplateOcclusion(set) != null;
                foreach (var image in template.Images)
                    if (ExportTemplates.ShouldWrite(set.Document, image, hasOcclusion)) planned.Add((set, image, stem + SetFileSuffix(set) + "_" + image.Suffix + ".png"));
            }
            return planned;
        }

        /// <summary>テクスチャセットの焼いた AO（今の条件で焼いたものだけ。Generator と同じ口）を、テクセルごとに 0〜255 にしたもの。無ければ null。</summary>
        internal byte[] TemplateOcclusion(TextureSet set)
        {
            var d = set.Document;
            if (d.GeneratorInputs == null || !d.GeneratorInputs.TryGetMap(MeshMapKind.AmbientOcclusion, out var map, out _) || map == null) return null;
            if (map.Width != d.Width || map.Height != d.Height) return null;
            var values = new byte[d.Width * d.Height];
            for (int y = 0; y < d.Height; y++)
                for (int x = 0; x < d.Width; x++)
                    values[y * d.Width + x] = map.CoverageAt(x, y) == MeshTexelCoverage.Empty ? (byte)255 : (byte)Mathf.Clamp(Mathf.RoundToInt(map.Value(x, y) * 255), 0, 255);
            return values;
        }

        /// <summary>メニューから: フォルダを選び、置き換えるファイルを確かめてから書く。</summary>
        internal void ExportWithTemplate(ExportTemplate template)
        {
            string stem = projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : "Texture";
            var planned = PlanTemplateExport(template, stem);
            if (planned.Count == 0) { message = L.Tr("Nothing to export for {0}: no layer uses a channel it reads.", template.Name); return; }
            var clash = planned.GroupBy(t => t.name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (clash != null) { message = L.Tr("Nothing was exported: texture sets {0} would write the same file {1}. Rename one in File ▸ Project Configuration.", string.Join(", ", clash.Select(t => t.set.Name).Distinct()), clash.Key); return; }
            if (!ConfirmInactiveGenerators(planned.Select(t => t.set).Distinct().Select(set => (set.Name, set.Document)))) return;
            string folder = Dialogs.OpenFolder(L.Tr("Export images for {0} into folder", template.Name), projectPath != null ? Path.GetDirectoryName(projectPath) : Application.dataPath);
            if (string.IsNullOrEmpty(folder)) return;
            var existing = planned.Where(t => File.Exists(Path.Combine(folder, t.name))).Select(t => t.name).ToList();
            if (existing.Count > 0 && !Dialogs.Confirm("Replace images?", "These files will be replaced:\n" + string.Join("\n", existing), "Replace", "Cancel")) return;
            TryAction(() => message = ExportTemplateTo(folder, template, stem));
        }

        /// <summary>フォルダへ書く（ダイアログ無し。テストもこれを呼ぶ）。ステータスの文を返す。</summary>
        internal string ExportTemplateTo(string folder, ExportTemplate template, string stem)
        {
            var planned = PlanTemplateExport(template, stem);
            var notes = new List<string>(); var occlusions = new Dictionary<TextureSet, byte[]>();
            Directory.CreateDirectory(folder);
            foreach (var (set, image, name) in planned)
            {
                string path = Path.Combine(folder, name); bool isNew = !File.Exists(path); var d = set.Document;
                if (!occlusions.TryGetValue(set, out var occlusion)) occlusions[set] = occlusion = TemplateOcclusion(set);
                var pixels = PadForExport(set, ExportTemplates.Build(d, image, occlusion), notes);
                File.WriteAllBytes(path, YlpContent.EncodePng(pixels, d.Width, d.Height));
                string asset = AssetPathOf(Path.GetFullPath(path));
                if (asset == null) continue;
                AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceUpdate);
                if (!isNew || !(AssetImporter.GetAtPath(asset) is TextureImporter importer)) continue;
                if (image.Kind == ExportImageKind.Normal) importer.textureType = TextureImporterType.NormalMap;
                else { importer.sRGBTexture = image.Srgb; importer.alphaIsTransparency = image.Kind == ExportImageKind.BaseColor; }
                importer.SaveAndReimport();
            }
            bool withoutOcclusion = template.Images.Any(i => i.Scalars.Contains(ExportScalar.Occlusion)) && occlusions.Values.Any(o => o == null);
            return L.Tr("Exported {0} image(s) for {1} to {2}.", planned.Count, template.Name, folder)
                + (withoutOcclusion ? " " + L.Tr("A texture set has no current AO bake, so its occlusion is white (no occlusion).") : "")
                + (notes.Count > 0 ? " " + string.Join(" ", notes) : "") + " " + L.Tr("No material was changed.");
        }

        void ExportTemplateMenuItems(PaintMenu m)
        {
            foreach (var template in ExportTemplate.BuiltIn)
            {
                var t = template;
                m.AddItem(new GUIContent(L.Tr("Export") + "/" + L.Tr("Images for {0}…", t.Name)), false, () => TryAction(() => ExportWithTemplate(t)));
            }
        }
    }
}
