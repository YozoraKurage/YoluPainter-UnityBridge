using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// プロジェクトをモデルに結び付ける（Substance Painter のプロジェクトと同じ考え方）: 新規プロジェクトでモデル・テクスチャセット
    /// （マテリアルのスロットごと。チェックで選ぶ）・解像度・ノーマルマップの形式・使うチャンネルを決め、プロジェクト設定で後から変えられる
    /// （モデル・テクスチャセットの名前・スロット・足す・消す・ノーマルマップの形式）。モデルは今までどおり .ylp の view.json に GUID で残り、
    /// 開くと読み直す。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>まだ保存していないプロジェクトの保存先の提案（モデルと同じフォルダ、モデル名_マテリアル名）。</summary>
        string suggestedFolder, suggestedName;

        NewProjectSettings CurrentProjectSettings()
        {
            SyncCurrentSet();
            return new NewProjectSettings
            {
                Model = model,
                Resolution = NewProjectSettings.Resolutions.Contains(document.Width) && document.Width == document.Height ? document.Width : NewProjectSettings.Resolutions.OrderBy(r => Math.Abs(r - document.Width)).First(),
                NormalFormat = document.NormalSettings.FileDirection,
                Sets = textureSets.Select(s => new TextureSetDraft { Id = s.Id, Name = s.Name, Slot = s.MaterialSlot }).ToList(),
            };
        }

        /// <summary>新規プロジェクトのダイアログを開く（決めたら、未保存の作業を確かめてから作る）。</summary>
        internal void NewProjectDialog()
        {
            var initial = CurrentProjectSettings(); initial.Resolution = PainterSettings.DefaultResolution; initial.BakeMeshMaps = false; initial.Sets = null; initial.Slots = null;
            if (!NewProjectSettings.Resolutions.Contains(initial.Resolution)) initial.Resolution = 2048;
            NewProjectWindow.Open(initial, false, s => { if (ConfirmDiscard()) TryAction(() => CreateProject(s)); Repaint(); });
        }

        /// <summary>開いているプロジェクトの設定（モデル・テクスチャセット・ノーマルマップの形式）を変えるダイアログ。</summary>
        internal void ProjectConfigurationDialog() => NewProjectWindow.Open(CurrentProjectSettings(), true, s => { TryAction(() => ApplyProjectConfiguration(s)); Repaint(); });

        /// <summary>新しいプロジェクトを作る（未保存の作業の確認は呼ぶ側）。モデルの選んだスロット（既定は全部。モデルが無ければスロット 0）が、
        /// それぞれテンプレートと解像度のテクスチャセットになる。最初のセットが今のセット。</summary>
        internal void CreateProject(NewProjectSettings s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            s.Validate();
            if (stroke != null) throw new InvalidOperationException(L.Tr("Finish the stroke first."));
            resolution = s.Resolution;
            model = s.Model;
            preview.Load(model); // null ならモデル無し
            int slotCount = preview.HasModel ? Mathf.Max(1, preview.MaterialSlotCount) : 1;
            var slots = (preview.HasModel ? s.Slots ?? Enumerable.Range(0, slotCount).ToArray() : new[] { 0 })
                .Select(slot => Mathf.Clamp(slot, 0, slotCount - 1)).Distinct().OrderBy(slot => slot).Take(YlpFormat.MaxTextureSets).ToList();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sets = new List<TextureSet>();
            foreach (int slot in slots)
            {
                var d = new PaintDocument(s.Resolution, s.Resolution, 128, PainterSettings.UndoBudgetBytes);
                var first = d.AddLayer(L.Tr("Layer") + " 1");
                foreach (var c in NewProjectSettings.Channels(s.Template)) if (!first.IsChannelEnabled(c)) d.SetChannelEnabled(first.Id, c, true);
                if (d.NormalSettings.FileDirection != s.NormalFormat) d.SetNormalSettings(d.NormalSettings.WithFileDirection(s.NormalFormat));
                d.ClearHistory();
                string name = BaseSetName(slot), unique = name;
                for (int n = 2; names.Contains(unique); n++) unique = name + " " + n;
                names.Add(unique);
                sets.Add(new TextureSet(d.Id, unique, slot, d) { SelectedLayer = first.Id, PristineRevision = d.Revision });
            }
            FinishStroke(false); CancelToolDrag();
            ReplaceProject(sets, sets[0]); BindDocument();
            ForgetProjectFile();
            channel = PaintChannel.Color;
            SuggestSaveLocation();
            repaintPixels = true;
            message = model == null ? L.Tr("New project without a model. Choose one in Texture Set or File ▸ Project Configuration.")
                : sets.Count == 1 ? L.Tr("New project for {0} ({1}).", model.name, SlotDisplayName(materialSlot))
                : L.Tr("New project for {0} with {1} texture sets.", model.name, sets.Count);
            if (s.BakeMeshMaps && preview.CanPaint) BakeMeshMaps();
        }

        /// <summary>モデル・テクスチャセット（名前・スロット・足す・消す）・ノーマルマップの形式を変える（解像度は変えない）。消すセットがあれば
        /// 先に確かめ、断られたら何も変えない（セットを消すのは Undo できない）。ノーマルの形式は全部のセットに入れ、それぞれの文書の Undo に入る。</summary>
        internal void ApplyProjectConfiguration(NewProjectSettings s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            s.Validate();
            if (stroke != null) throw new InvalidOperationException(L.Tr("Finish the stroke first."));
            SyncCurrentSet();
            var removed = new List<TextureSet>();
            if (s.Sets != null)
            {
                foreach (var draft in s.Sets) if (draft.Id != Guid.Empty && textureSets.All(t => t.Id != draft.Id)) throw new ArgumentException("This project has no texture set " + draft.Id + ".");
                removed = textureSets.Where(t => s.Sets.All(d => d.Id != t.Id)).ToList();
                if (removed.Count > 0 && !Dialogs.Confirm(L.Tr("Remove texture sets?"), RemovalWarning(removed), L.Tr("Remove"), L.Tr("Cancel")))
                { message = L.Tr("The project configuration was not applied; nothing changed."); return; }
            }
            if (s.Model != model) SetModel(s.Model);
            if (s.Sets != null) ApplySetDrafts(s.Sets, removed);
            foreach (var set in textureSets.ToList())
            {
                var d = set == currentSet ? document : set.Document;
                if (d.NormalSettings.FileDirection == s.NormalFormat) continue;
                if (set == currentSet) ApplyNormalSettings(d.NormalSettings.WithFileDirection(s.NormalFormat));
                else d.SetNormalSettings(d.NormalSettings.WithFileDirection(s.NormalFormat));
            }
            if (projectPath == null) SuggestSaveLocation();
            repaintPixels = true;
            message = L.Tr("Project configuration applied.");
        }

        /// <summary>プロジェクト設定のセットの並びを入れる: 消す（確かめは済み）・名前とスロット・足す（空のセット）・並びの順。</summary>
        void ApplySetDrafts(List<TextureSetDraft> drafts, List<TextureSet> removed)
        {
            RemoveSets(removed);
            bool changed = removed.Count > 0;
            var ordered = new List<TextureSet>();
            foreach (var draft in drafts)
            {
                if (draft.Id == Guid.Empty) { ordered.Add(null); continue; }
                var set = textureSets.First(t => t.Id == draft.Id);
                string name = draft.Name.Trim();
                if (set.Name != name) { set.Name = name; changed = true; }
                if (set.MaterialSlot != draft.Slot) { set.MaterialSlot = draft.Slot; if (set == currentSet) materialSlot = draft.Slot; changed = true; }
                ordered.Add(set);
            }
            for (int i = 0; i < drafts.Count; i++)
                if (ordered[i] == null) { ordered[i] = NewEmptySet(drafts[i].Slot, drafts[i].Name.Trim()); changed = true; }
            if (!ordered.SequenceEqual(textureSets)) { textureSets.Clear(); textureSets.AddRange(ordered); changed = true; }
            if (!changed) return;
            setsRevision++;
            var note = ApplyBudgets(); if (note != null) message = note;
            repaintPixels = true; RepaintPanelWindowsSoon();
        }

        string SlotDisplayName(int slot)
        {
            var material = preview.HasModel ? preview.SourceMaterial(slot) : null;
            return material != null ? material.name : L.Tr("slot") + " " + slot;
        }

        /// <summary>保存先の提案: Assets の中のモデルならその隣、名前は モデル名_マテリアル名（テクスチャセットが複数ならモデル名）。</summary>
        void SuggestSaveLocation()
        {
            suggestedFolder = null; suggestedName = null;
            string asset = model != null ? AssetDatabase.GetAssetPath(model) : null;
            if (string.IsNullOrEmpty(asset)) return;
            suggestedFolder = Path.GetDirectoryName(Path.GetFullPath(FileUtil.GetPhysicalPath(asset)));
            var material = textureSets.Count == 1 ? preview.SourceMaterial(materialSlot) : null;
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

        void LoadDemoCube() { model = null; preview.LoadDemoMesh(); FitSingleSetSlot(); repaintPixels = true; message = L.Tr("Loaded the tool's seam-test cube. No scene or source asset changed."); }

        /// <summary>テクスチャセットが 1 つなら、そのスロットを読み込んだモデルのスロットの範囲に寄せる（前からのふるまい）。複数のセットの
        /// スロットは変えない（モデルに無いスロットのセットはパネルに印が出て、3D に見えないだけ）。</summary>
        void FitSingleSetSlot()
        {
            if (textureSets.Count != 1) return;
            int fitted = Mathf.Clamp(materialSlot, 0, Mathf.Max(0, preview.MaterialSlotCount - 1));
            if (fitted != materialSlot) { materialSlot = fitted; setsRevision++; }
        }

        const int ModelPickerId = 0x59500001;
        /// <summary>モデルの選択（オブジェクトピッカーで選んだとき）。</summary>
        void HandleModelPicker(Event e)
        {
            if (e.type != EventType.ExecuteCommand || EditorGUIUtility.GetObjectPickerControlID() != ModelPickerId) return;
            if (e.commandName != "ObjectSelectorClosed" && e.commandName != "ObjectSelectorUpdated") return;
            if (EditorGUIUtility.GetObjectPickerObject() is GameObject picked && picked != model) SetModel(picked);
            e.Use();
        }
        internal void SetModel(GameObject next)
        {
            model = next;
            TryAction(() => { preview.Load(model); FitSingleSetSlot(); message = string.Join("; ", preview.Diagnostics); repaintPixels = true; });
        }
    }
}
