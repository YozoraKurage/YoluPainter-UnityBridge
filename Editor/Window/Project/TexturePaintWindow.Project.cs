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
                Sets = textureSets.Select(s => new TextureSetDraft { Id = s.Id, Name = s.Name, Slot = s.MaterialSlot, Width = s.Document.Width, Height = s.Document.Height, CurrentWidth = s.Document.Width, CurrentHeight = s.Document.Height }).ToList(),
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
            previewDisplayCanceled = false;
            resolution = s.Resolution;
            model = s.Model;
            var prepared = s.PreparedModel?.Take(model);
            if (prepared != null)
            {
                preview.Dispose(); preview = prepared;
                ApplyPreviewFrameRate(); preview.MaterialEdits = materialEdits; preview.Shading = previewShading; BindPreviewScene();
            }
            else preview.BeginLoad(model); // null ならモデル無し
            int slotCount = preview.HasSnapshot ? Mathf.Max(1, preview.MaterialSlotCount) : 1;
            var slots = (preview.HasSnapshot ? s.Slots ?? Enumerable.Range(0, slotCount).ToArray() : new[] { 0 })
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
            if (s.BakeMeshMaps) { if (preview.IsPreparing) bakePreparedModel = preview; else if (preview.CanPaint) BakeMeshMaps(); }
        }

        /// <summary>モデル・テクスチャセット（名前・スロット・大きさ・足す・消す）・ノーマルマップの形式を変える。消すセット・大きさを変えるセットが
        /// あれば先に確かめ、断られたら何も変えない（どちらも Undo できない）。大きさを変えるセットは、ほかを何も変える前に新しい大きさの写しを
        /// 作り（<see cref="PaintDocument.Resampled"/>。予算を超えるなどで作れなければ例外で、何も変えない）、最後に文書を入れ替える。
        /// ノーマルの形式は全部のセットに入れ、それぞれの文書の Undo に入る。</summary>
        internal void ApplyProjectConfiguration(NewProjectSettings s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            s.Validate();
            if (stroke != null) throw new InvalidOperationException(L.Tr("Finish the stroke first."));
            SyncCurrentSet();
            var removed = new List<TextureSet>(); var resizes = new List<SetResize>();
            if (s.Sets != null)
            {
                foreach (var draft in s.Sets) if (draft.Id != Guid.Empty && textureSets.All(t => t.Id != draft.Id)) throw new ArgumentException("This project has no texture set " + draft.Id + ".");
                removed = textureSets.Where(t => s.Sets.All(d => d.Id != t.Id)).ToList();
                resizes = PlanResizes(s.Sets, removed);
                if (removed.Count > 0 && !Dialogs.Confirm(L.Tr("Remove texture sets?"), RemovalWarning(removed), L.Tr("Remove"), L.Tr("Cancel")))
                { message = L.Tr("The project configuration was not applied; nothing changed."); return; }
                if (resizes.Count > 0 && !Dialogs.Confirm(L.Tr("Resize texture sets?"), ResizeWarning(resizes, s.Resampling), L.Tr("Resize"), L.Tr("Cancel")))
                { message = L.Tr("The project configuration was not applied; nothing changed."); return; }
            }
            var resampled = Resample(resizes, s.Resampling, removed); // 作れなければここで例外（まだ何も変えていない）
            if (resampled.Count > 0) { FinishStroke(false); CancelToolDrag(); pathDrag = -1; document?.EndCoalescing(); }
            if (s.Model != model) SetModel(s.Model);
            if (s.Sets != null) ApplySetDrafts(s.Sets, removed);
            var notes = InstallResampled(resampled);
            foreach (var set in textureSets.ToList())
            {
                var d = set == currentSet ? document : set.Document;
                if (d.NormalSettings.FileDirection == s.NormalFormat) continue;
                if (set == currentSet) ApplyNormalSettings(d.NormalSettings.WithFileDirection(s.NormalFormat));
                else d.SetNormalSettings(d.NormalSettings.WithFileDirection(s.NormalFormat));
            }
            if (projectPath == null) SuggestSaveLocation();
            repaintPixels = true;
            message = L.Tr("Project configuration applied.") + (resampled.Count > 0 ? " " + string.Join(" ", resampled.Select(r => L.Tr("{0} is now {1} × {2}.", r.Set.Name, r.Result.Document.Width, r.Result.Document.Height))) : "")
                + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        }

        // ───────── テクスチャセットの大きさ ─────────

        /// <summary>大きさを変えるセットと新しい大きさ。</summary>
        sealed class SetResize { public TextureSet Set; public int Width, Height; }

        /// <summary>並びのうち大きさが今と違うセット（消すセットは除く）。新しい大きさは新規プロジェクトと同じ正方形の大きさだけ。</summary>
        List<SetResize> PlanResizes(List<TextureSetDraft> drafts, List<TextureSet> removed)
        {
            var plan = new List<SetResize>();
            foreach (var draft in drafts)
            {
                if (draft.Id == Guid.Empty || draft.Width == 0 && draft.Height == 0) continue;
                var set = textureSets.First(t => t.Id == draft.Id);
                if (removed.Contains(set) || draft.Width == set.Document.Width && draft.Height == set.Document.Height) continue;
                if (draft.Width != draft.Height || !NewProjectSettings.Resolutions.Contains(draft.Width))
                    throw new ArgumentException(L.Tr("A texture set's size must be one of {0}.", string.Join(", ", NewProjectSettings.Resolutions.Select(r => r + " × " + r))));
                plan.Add(new SetResize { Set = set, Width = draft.Width, Height = draft.Height });
            }
            return plan;
        }

        /// <summary>選んだ再標本化（null は自動: 面積が減るなら面積平均、ほかはバイリニア）。</summary>
        static CanvasResampling ResamplingFor(CanvasResampling? chosen, PaintDocument d, int width, int height)
            => chosen ?? ((long)width * height < (long)d.Width * d.Height ? CanvasResampling.Area : CanvasResampling.Bilinear);

        /// <summary>大きさを変えるときの確かめの文。</summary>
        static string ResizeWarning(List<SetResize> resizes, CanvasResampling? chosen)
        {
            var lines = resizes.Select(r => "• " + r.Set.Name + ": " + r.Set.Document.Width + " × " + r.Set.Document.Height + " → " + r.Width + " × " + r.Height
                + " (" + NewProjectWindow.ResamplingName(ResamplingFor(chosen, r.Set.Document, r.Width, r.Height)) + ")");
            return string.Join("\n", lines) + "\n\n" + L.Tr("Every layer, mask and the selection are resampled. The undo history of these texture sets is cleared, and the resize cannot be undone (the saved file keeps the old size until you save). Their baked mesh maps become stale.");
        }

        /// <summary>新しい大きさの写しを作る（まだ入れ替えない）。予算はプロジェクト全体: 写しの層の画素は、設定の予算から残るセット（消すセットは
        /// 数えない）の量を引いた分まで。縮めるセットから先に作る（広げるセットに残りを回す）。作れなければ例外で、何も変えない。</summary>
        List<(TextureSet Set, ResampledDocument Result)> Resample(List<SetResize> resizes, CanvasResampling? chosen, List<TextureSet> removed)
        {
            var results = new List<(TextureSet, ResampledDocument)>();
            if (resizes.Count == 0) return results;
            var bytes = textureSets.Where(t => !removed.Contains(t)).ToDictionary(t => t, t => t.Document.AllocatedBytes);
            long budget = PainterSettings.SourceBudgetBytes;
            var ordered = resizes.OrderBy(r => (double)r.Width * r.Height / ((double)r.Set.Document.Width * r.Set.Document.Height)).ToList();
            try
            {
                for (int i = 0; i < ordered.Count; i++)
                {
                    var r = ordered[i]; var d = r.Set.Document;
                    Dialogs.Progress("YoluPainter", L.Tr("Resampling {0} to {1} × {2}…", r.Set.Name, r.Width, r.Height), (i + .5f) / ordered.Count);
                    long others = bytes.Where(e => e.Key != r.Set).Sum(e => e.Value);
                    ResampledDocument result;
                    try { result = d.Resampled(r.Width, r.Height, ResamplingFor(chosen, d, r.Width, r.Height), Math.Max(0, budget - others)); }
                    catch (InvalidOperationException ex) { throw new InvalidOperationException(SetNotePrefix(r.Set) + ex.Message, ex); }
                    bytes[r.Set] = result.Document.AllocatedBytes;
                    results.Add((r.Set, result));
                }
            }
            finally { Dialogs.ClearProgress(); }
            return results;
        }

        /// <summary>作った写しをセットの文書と入れ替える: 3D のパスを今のモデルで描き直し（描けなければ再標本化した画素のまま知らせる）、
        /// そのセットの履歴を捨て（写しは履歴を持たない）、表示を作り直させる。焼いたメッシュマップは残る（大きさが違うので古いと出る）。</summary>
        List<string> InstallResampled(List<(TextureSet Set, ResampledDocument Result)> resampled)
        {
            var notes = new List<string>();
            if (resampled.Count == 0) return notes;
            SyncCurrentSet();
            foreach (var (set, result) in resampled)
            {
                var d = result.Document;
                notes.AddRange(result.Notes.Select(n => SetNotePrefix(set) + n));
                RedrawSurfacePaths(set, d, result.SurfacePathLayers, notes);
                d.ClearHistory();
                set.DisposeTextures();
                set.Document = d; set.HistoryWatched = false;
                if (set == currentSet) { document = d; LoadSet(set); } else WatchHistory(set);
            }
            DisposeThumbnails(); // 層のサムネイルは層の ID と面の版で覚えている。新しい面の版は 0 から数え直すので、古い絵が残らないように捨てる
            var budgetNote = ApplyBudgets(); if (budgetNote != null) notes.Add(budgetNote);
            repaintPixels = true; renderedRevision = -1; RepaintPanelWindowsSoon();
            return notes;
        }

        /// <summary>3D のパスで描いた層を、今のモデルで新しい大きさに描き直す。</summary>
        void RedrawSurfacePaths(TextureSet set, PaintDocument d, IReadOnlyList<Guid> layers, List<string> notes)
        {
            foreach (var id in layers)
            {
                var layer = d.GetLayer(id); var path = (Core.Paths.SurfacePath)layer.Path;
                var geometry = preview != null ? preview.Geometry : null;
                if (geometry == null || Yozolab.YoluPainter.Editor.Preview.SurfacePathRenderer.Fingerprint(geometry) != path.ModelFingerprint)
                { notes.Add(SetNotePrefix(set) + L.Tr("The path on the layer {0} was resampled, not redrawn: its model is not loaded. Redraw it in the Path section when it is.", layer.Name)); continue; }
                try { var render = Yozolab.YoluPainter.Editor.Preview.SurfacePathRenderer.Render(d, geometry, path, preview.BrushBudget); d.SetPath(id, path, render.Channels); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                { notes.Add(SetNotePrefix(set) + L.Tr("The path on the layer {0} was resampled, not redrawn: {1}", layer.Name, ex.Message)); }
            }
        }

        /// <summary>プロジェクト設定のセットの並びを入れる: 消す（確かめは済み）・名前とスロット・足す（空のセット。大きさは下書きの大きさ、0 なら開いている
        /// セットと同じ）・並びの順。大きさの変更は <see cref="InstallResampled"/> が入れる。</summary>
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
                if (ordered[i] == null) { ordered[i] = NewEmptySet(drafts[i].Slot, drafts[i].Name.Trim(), drafts[i].Width, drafts[i].Height); changed = true; }
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
            FinishStroke(false); CancelToolDrag(); CancelShapeDrag(); EndLightingDrag(true);
            previewDisplayCanceled = false; model = next;
            TryAction(() => { preview.BeginLoad(model); FitSingleSetSlot(); message = string.Join("; ", preview.Diagnostics); repaintPixels = true; });
        }
    }
}
