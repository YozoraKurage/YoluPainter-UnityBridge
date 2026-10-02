using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// プロジェクトをモデルに結び付ける（Substance Painter のプロジェクトと同じ考え方）: 新規プロジェクトでモデル・テクスチャセット
    /// （マテリアルのスロット）・解像度・ノーマルマップの形式・使うチャンネルを決め、プロジェクト設定で後から変えられる。モデルは
    /// 今までどおり .ylp の view.json に GUID で残り、開くと読み直す。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>まだ保存していないプロジェクトの保存先の提案（モデルと同じフォルダ、モデル名_マテリアル名）。</summary>
        string suggestedFolder, suggestedName;

        NewProjectSettings CurrentProjectSettings() => new NewProjectSettings
        {
            Model = model, MaterialSlot = materialSlot,
            Resolution = NewProjectSettings.Resolutions.Contains(document.Width) && document.Width == document.Height ? document.Width : NewProjectSettings.Resolutions.OrderBy(r => Math.Abs(r - document.Width)).First(),
            NormalFormat = document.NormalSettings.FileDirection,
        };

        /// <summary>新規プロジェクトのダイアログを開く（決めたら、未保存の作業を確かめてから作る）。</summary>
        internal void NewProjectDialog()
        {
            var initial = CurrentProjectSettings(); initial.Resolution = PainterSettings.DefaultResolution; initial.BakeMeshMaps = false;
            if (!NewProjectSettings.Resolutions.Contains(initial.Resolution)) initial.Resolution = 2048;
            NewProjectWindow.Open(initial, false, s => { if (ConfirmDiscard()) TryAction(() => CreateProject(s)); Repaint(); });
        }

        /// <summary>開いているプロジェクトの設定（モデル・テクスチャセット・ノーマルマップの形式）を変えるダイアログ。</summary>
        internal void ProjectConfigurationDialog() => NewProjectWindow.Open(CurrentProjectSettings(), true, s => { TryAction(() => ApplyProjectConfiguration(s)); Repaint(); });

        /// <summary>新しいプロジェクトを作る（未保存の作業の確認は呼ぶ側）。</summary>
        internal void CreateProject(NewProjectSettings s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            s.Validate();
            if (stroke != null) throw new InvalidOperationException(L.Tr("Finish the stroke first."));
            resolution = s.Resolution;
            CreateDocument(s.Resolution);
            var first = document.GetLayer(selectedLayer);
            foreach (var c in NewProjectSettings.Channels(s.Template)) if (!first.IsChannelEnabled(c)) document.SetChannelEnabled(first.Id, c, true);
            if (document.NormalSettings.FileDirection != s.NormalFormat) document.SetNormalSettings(document.NormalSettings.WithFileDirection(s.NormalFormat));
            document.ClearHistory(); pristineRevision = document.Revision;
            BindDocument();
            channel = PaintChannel.Color;
            model = s.Model;
            preview.Load(model); // null ならモデル無し
            materialSlot = Mathf.Clamp(s.MaterialSlot, 0, Mathf.Max(0, preview.MaterialSlotCount - 1));
            SuggestSaveLocation();
            repaintPixels = true;
            message = model != null ? L.Tr("New project for {0} ({1}).", model.name, SlotDisplayName(materialSlot)) : L.Tr("New project without a model. Choose one in Texture Set or File ▸ Project Configuration.");
            if (s.BakeMeshMaps && preview.CanPaint) BakeMeshMaps();
        }

        /// <summary>モデル・テクスチャセット・ノーマルマップの形式を変える（解像度は変えない）。ノーマルの形式は Undo できる。</summary>
        internal void ApplyProjectConfiguration(NewProjectSettings s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            s.Validate();
            if (s.Model != model) SetModel(s.Model);
            materialSlot = Mathf.Clamp(s.MaterialSlot, 0, Mathf.Max(0, preview.MaterialSlotCount - 1));
            if (document.NormalSettings.FileDirection != s.NormalFormat) ApplyNormalSettings(document.NormalSettings.WithFileDirection(s.NormalFormat));
            if (projectPath == null) SuggestSaveLocation();
            repaintPixels = true;
            message = L.Tr("Project configuration applied.");
        }

        string SlotDisplayName(int slot)
        {
            var material = preview.HasModel ? preview.SourceMaterial(slot) : null;
            return material != null ? material.name : L.Tr("slot") + " " + slot;
        }

        /// <summary>保存先の提案: Assets の中のモデルならその隣、名前は モデル名_マテリアル名。</summary>
        void SuggestSaveLocation()
        {
            suggestedFolder = null; suggestedName = null;
            string asset = model != null ? AssetDatabase.GetAssetPath(model) : null;
            if (string.IsNullOrEmpty(asset)) return;
            suggestedFolder = Path.GetDirectoryName(Path.GetFullPath(FileUtil.GetPhysicalPath(asset)));
            var material = preview.SourceMaterial(materialSlot);
            suggestedName = Sanitize(model.name + (material != null ? "_" + material.name : ""));
        }
        static string Sanitize(string name) { foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_'); return name.Trim(); }
        /// <summary>保存のダイアログの既定のフォルダと名前（開いているファイル、取り込んだ PSD、新規プロジェクトの提案、Assets の順）。</summary>
        (string folder, string name) SaveSuggestion()
        {
            if (projectPath != null) return (Path.GetDirectoryName(projectPath), Path.GetFileNameWithoutExtension(projectPath));
            if (importedPsdPath != null) return (Path.GetDirectoryName(importedPsdPath), Path.GetFileNameWithoutExtension(importedPsdPath));
            if (suggestedFolder != null) return (suggestedFolder, suggestedName ?? "Texture");
            return (Application.dataPath, "Texture");
        }
    }
}
