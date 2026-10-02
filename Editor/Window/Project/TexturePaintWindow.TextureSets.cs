using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// テクスチャセット（Substance Painter と同じ）: プロジェクトはモデルとテクスチャセットの並びで、セットはそれぞれ 1 つのマテリアルの
    /// スロットを、自分の文書（レイヤー・解像度・チャンネル・Normal の設定・選択範囲・Undo の履歴）で描く。焼いたメッシュマップと保存の状態も
    /// セットごと。描いているチャンネルはウィンドウで 1 つ。
    /// <list type="bullet">
    /// <item>ウィンドウの document・selectedLayer・materialSlot・editMask は「今のセット」のもので、セットを切り替えるとき（ストロークと
    /// ツールのドラッグを終わらせてから）今の値をセットに戻し（<see cref="SyncCurrentSet"/>）、別のセットの値を入れる（<see cref="LoadSet"/>）。
    /// 保存の版・メッシュマップなどは今のセットのものをそのまま読み書きする（下のプロパティ）。切り替えは履歴に入らない。</item>
    /// <item>未保存かどうかは全部のセットの文書の版と、セットの並び（足す・消す・名前・スロット）の版で決める。</item>
    /// <item>3D ビューは全部のセットを、それぞれのスロットに今のチャンネルで見せる。今のセットは合成器（GPU）、ほかのセットは CPU の
    /// 合成から作ったテクスチャ（文書の版とチャンネルで覚え、変わったときだけ作り直す。長辺 <see cref="SetPreviewMaxSize"/> まで）。</item>
    /// <item>メモリの予算はプロジェクト全体のもの（<see cref="ApplyBudgets"/>）。</item>
    /// </list>
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>プロジェクトの中の 1 つのテクスチャセット。今のセットの文書・選んだ層・スロットはウィンドウの側が持ち、
        /// <see cref="TextureSets"/> で読むときに戻してある。</summary>
        internal sealed class TextureSet
        {
            public Guid Id { get; }
            public string Name { get; internal set; }
            /// <summary>描くマテリアルのスロット（プレビューで平らにした番号）。</summary>
            public int MaterialSlot { get; internal set; }
            public PaintDocument Document { get; internal set; }
            public MeshMapSet MeshMaps { get; } = new MeshMapSet();
            internal Guid SelectedLayer; internal bool EditMask;
            /// <summary>保存した・作った直後・復旧 checkpoint を書いたときの文書の版。</summary>
            internal long SavedRevision = -1, PristineRevision = -1, RecoveredRevision = -1;
            /// <summary>PSD から取り込んだときの原本のバイト列（.ylp の sets/&lt;ID&gt;/imported-original.psd）。</summary>
            internal byte[] ImportedOriginal;
            internal long SavedMeshMapRevision, SavingMeshMapRevision = -1;
            internal string MeshMapNote; internal MeshBakeReport LastMeshBakeReport;
            internal bool HistoryWatched;
            // 表示（今のセットでないあいだの 3D ビュー・照明と、パネルのサムネイル）。保存にも履歴にも入らない
            internal Texture2D Display, Lighting, Thumbnail;
            internal string DisplayKey, LightingKey; internal long ThumbnailRevision = long.MinValue; internal PaintDocument ThumbnailFor;
            /// <summary>マテリアル表示に渡すチャンネルの CPU の合成（<see cref="ChannelTexture"/>）。表示だけ。</summary>
            internal sealed class ChannelCache { public Texture2D Texture; public PaintDocument Document; public long Revision, Serial; public int Width, Height; }
            internal readonly Dictionary<PaintChannel, ChannelCache> MaterialChannels = new Dictionary<PaintChannel, ChannelCache>();
            internal void DisposeMaterialChannels()
            {
                foreach (var c in MaterialChannels.Values) if (c.Texture != null) UnityEngine.Object.DestroyImmediate(c.Texture);
                MaterialChannels.Clear();
            }

            public TextureSet(Guid id, string name, int materialSlot, PaintDocument document)
            {
                if (id == Guid.Empty) throw new ArgumentException("A texture set needs an ID.", nameof(id));
                YlpFormat.CheckSetName(name);
                Id = id; Name = name; MaterialSlot = materialSlot; Document = document ?? throw new ArgumentNullException(nameof(document));
                SavedMeshMapRevision = MeshMaps.Revision;
            }
            internal void DisposeTextures()
            {
                foreach (var t in new[] { Display, Lighting, Thumbnail }) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                Display = Lighting = Thumbnail = null; DisplayKey = LightingKey = null; ThumbnailFor = null; ThumbnailRevision = long.MinValue;
                DisposeMaterialChannels();
            }
        }

        readonly List<TextureSet> textureSets = new List<TextureSet>();
        TextureSet currentSet;
        /// <summary>セットの並び（足す・消す・名前・スロット）の版と、保存した・作った直後・復旧 checkpoint を書いたときの版。</summary>
        long setsRevision, savedSetsRevision = -1, pristineSetsRevision, recoveredSetsRevision = -1;
        /// <summary>ほかのセットの 3D の表示に使うテクスチャの長辺の上限（大きい文書は縮めて見せる。VRAM を 1 セット 16 MiB までにする）。</summary>
        internal const int SetPreviewMaxSize = 2048;
        const int SetThumbnailSize = 32;

        // 今のセットの保存の状態・メッシュマップ（セットが持つ）
        long savedRevision { get => currentSet.SavedRevision; set => currentSet.SavedRevision = value; }
        /// <summary>作った直後で何も手を加えていない文書の版（New / 最初に開いたとき）。捨てても失うものが無いので確かめない。</summary>
        long pristineRevision { get => currentSet.PristineRevision; set => currentSet.PristineRevision = value; }
        long recoveredRevision { get => currentSet.RecoveredRevision; set => currentSet.RecoveredRevision = value; }
        byte[] importedOriginal => currentSet.ImportedOriginal;
        MeshMapSet meshMaps => currentSet.MeshMaps;
        long savedMeshMapRevision { get => currentSet.SavedMeshMapRevision; set => currentSet.SavedMeshMapRevision = value; }
        long savingMeshMapRevision { get => currentSet.SavingMeshMapRevision; set => currentSet.SavingMeshMapRevision = value; }
        string meshMapNote { get => currentSet.MeshMapNote; set => currentSet.MeshMapNote = value; }
        MeshBakeReport lastMeshBakeReport { get => currentSet.LastMeshBakeReport; set => currentSet.LastMeshBakeReport = value; }

        /// <summary>テクスチャセットの並び（今のセットの値を戻してから返す）。</summary>
        internal IReadOnlyList<TextureSet> TextureSets { get { SyncCurrentSet(); return textureSets; } }
        internal TextureSet CurrentTextureSet { get { SyncCurrentSet(); return currentSet; } }

        /// <summary>ウィンドウが持つ今のセットの値をセットに戻す（全部のセットを見る前に）。</summary>
        void SyncCurrentSet()
        {
            if (currentSet == null) return;
            if (document != null) currentSet.Document = document;
            currentSet.SelectedLayer = selectedLayer; currentSet.MaterialSlot = materialSlot; currentSet.EditMask = editMask;
        }

        /// <summary>セットを今のセットにする（ウィンドウの値に入れ、表示を作り直させる）。文書・履歴・版は変えない。</summary>
        void LoadSet(TextureSet set)
        {
            currentSet = set; document = set.Document; materialSlot = set.MaterialSlot; editMask = set.EditMask;
            selectedLayer = document.Layers.Any(l => l.Id == set.SelectedLayer) ? set.SelectedLayer : document.Layers.Count > 0 ? document.Layers[document.Layers.Count - 1].Id : Guid.Empty;
            if (editMask && document.Layers.FirstOrDefault(l => l.Id == selectedLayer)?.Mask == null) editMask = false;
            WatchHistory(set);
            repaintPixels = true; renderedRevision = -1; lightingRevision = -1; overlayRevision = -1;
            selectedFilter = Guid.Empty; renamingLayer = Guid.Empty; layerScroll = Vector2.zero;
        }

        /// <summary>プロジェクトのセットを入れ替える（新規・開く・取り込み・復旧）。走っているベイクは前のプロジェクトのものなので止める。</summary>
        void ReplaceProject(IList<TextureSet> next, TextureSet current)
        {
            if (next == null || next.Count == 0 || !next.Contains(current)) throw new ArgumentException("A project needs its current texture set.");
            if (AbandonMeshBake()) message = L.Tr("The mesh-map bake was stopped because the document changed.");
            meshBakeOutcome = null; meshBakeSkipped.Clear(); meshMapView = MeshMapView.None;
            foreach (var set in textureSets) if (!next.Contains(set)) set.DisposeTextures();
            textureSets.Clear(); textureSets.AddRange(next);
            currentSet = null; LoadSet(current);
            materialEditsNoticePending = true; // マテリアルの欄の未反映の変更（プロジェクトには入らない）が残っていれば、開いた後に知らせる
        }

        /// <summary>セットの並びの版を 0 にする（saved: 開いたファイルと同じ / false: 新しいプロジェクト）。</summary>
        void ResetSetsBaseline(bool saved) { setsRevision = 0; pristineSetsRevision = 0; savedSetsRevision = saved ? 0 : -1; recoveredSetsRevision = -1; }

        /// <summary>何も変えていないか（全部のセットが保存した版か作った直後の版で、セットの並びも同じ）。捨てるときに確かめない条件。</summary>
        bool ProjectUnchanged()
        {
            if (currentSet == null) return true;
            SyncCurrentSet();
            return (setsRevision == savedSetsRevision || setsRevision == pristineSetsRevision) && textureSets.All(s => s.Document.Revision == s.SavedRevision || s.Document.Revision == s.PristineRevision);
        }

        /// <summary>Undo の予算で古い履歴を捨てたことを知らせる（文書ごとに 1 度だけつなぐ）。</summary>
        void WatchHistory(TextureSet set)
        {
            if (set == null || set.HistoryWatched) return;
            set.HistoryWatched = true; var d = set.Document;
            d.HistoryTrimming += bytes => message = "Undo budget reached; dropping " + (bytes / 1024) + " KiB of the oldest history (the newest " + d.MinimumUndoSteps + " steps are always kept). Current source remains intact.";
        }

        /// <summary>project.json の中身（今のセットの値を戻してから呼ぶ）。</summary>
        YlpProjectInfo ProjectInfo() => new YlpProjectInfo(textureSets.Select(s => new YlpTextureSetInfo(s.Id, s.Name, s.MaterialSlot)), currentSet.Id);

        // ───────── 名前 ─────────

        /// <summary>スロットのマテリアルの名前（モデルが無い・割り当てが無ければ null）。</summary>
        string SlotMaterialName(int slot)
        {
            var material = preview != null && preview.HasModel ? preview.SourceMaterial(slot) : null;
            return material != null && !string.IsNullOrWhiteSpace(material.name) ? material.name.Trim() : null;
        }
        /// <summary>セットの既定の名前: マテリアルの名前、無ければ「テクスチャセット n」（n はスロット + 1）。</summary>
        string BaseSetName(int slot)
        {
            string name = SlotMaterialName(slot) ?? L.Tr("Texture Set") + " " + (slot + 1);
            return name.Length > YlpFormat.MaxTextureSetNameLength ? name.Substring(0, YlpFormat.MaxTextureSetNameLength) : name;
        }
        /// <summary>今のプロジェクトのほかのセットと重ならない既定の名前（except は数えない）。</summary>
        string DefaultSetName(int slot, TextureSet except) => UniqueSetName(BaseSetName(slot), except);
        /// <summary>wanted が今のプロジェクトのほかのセットの名前と（大文字小文字を区別せずに）重なれば「 2」「 3」…を足す。</summary>
        string UniqueSetName(string wanted, TextureSet except)
        {
            var taken = new HashSet<string>(textureSets.Where(s => s != except).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(wanted)) return wanted;
            for (int n = 2; ; n++) { string name = wanted + " " + n; if (!taken.Contains(name)) return name; }
        }

        // ───────── 切り替え・足す・消す・名前とスロット ─────────

        /// <summary>今のテクスチャセットを替える。描いているストロークは取り消し、ツールのドラッグも終わらせる（取り残さない）。切り替えは履歴に
        /// 入らず、未保存の状態も変えない。替えたら true。</summary>
        internal bool SwitchTextureSet(Guid id)
        {
            var next = textureSets.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("This project has no texture set " + id + ".", nameof(id));
            if (next == currentSet) return false;
            FinishStroke(false); CancelToolDrag(); pathDrag = -1;
            document?.EndCoalescing();
            SyncCurrentSet();
            LoadSet(next);
            var note = ApplyBudgets();
            message = note ?? L.Tr("Texture set: {0}.", next.Name);
            Repaint(); RepaintPanelWindowsSoon();
            return true;
        }

        /// <summary>空のテクスチャセットを足して今のセットにする: 今のセットと同じ大きさ・同じ使うチャンネル・同じ Normal の設定の、空のレイヤー 1 枚。</summary>
        internal TextureSet AddTextureSet(int slot, string name = null)
        {
            if (stroke != null) throw new InvalidOperationException(L.Tr("Finish the stroke first."));
            SyncCurrentSet();
            if (textureSets.Count >= YlpFormat.MaxTextureSets) throw new InvalidOperationException(L.Tr("A project has at most {0} texture sets.", YlpFormat.MaxTextureSets));
            if (slot < 0 || slot > YlpFormat.MaxMaterialSlot) throw new ArgumentOutOfRangeException(nameof(slot));
            var owner = textureSets.FirstOrDefault(s => s.MaterialSlot == slot);
            if (owner != null) throw new InvalidOperationException(L.Tr("Material slot {0} already has the texture set {1}.", slot, owner.Name));
            if (name != null) { YlpFormat.CheckSetName(name); if (UniqueSetName(name, null) != name) throw new ArgumentException(L.Tr("Another texture set is already named {0}.", name), nameof(name)); }
            var set = NewEmptySet(slot, name ?? DefaultSetName(slot, null));
            textureSets.Add(set); setsRevision++;
            SwitchTextureSet(set.Id);
            message = L.Tr("Added the texture set {0} (material slot {1}).", set.Name, slot);
            return set;
        }

        /// <summary>今のセットに合わせた空のセット（履歴なし。作った直後の版を覚える）。</summary>
        TextureSet NewEmptySet(int slot, string name)
        {
            var like = document;
            var d = new PaintDocument(like.Width, like.Height, like.TileSize, PainterSettings.UndoBudgetBytes);
            var layer = d.AddLayer(L.Tr("Layer") + " 1");
            foreach (var c in YlpContent.UsedChannels(like)) if (!layer.IsChannelEnabled(c)) d.SetChannelEnabled(layer.Id, c, true);
            if (!d.NormalSettings.Equals(like.NormalSettings)) d.SetNormalSettings(like.NormalSettings);
            d.ClearHistory();
            return new TextureSet(d.Id, name, slot, d) { SelectedLayer = layer.Id, PristineRevision = d.Revision };
        }

        /// <summary>テクスチャセットを消す（その作業も消え、Undo できない）。確かめて、断られたら何も変えない。最後の 1 つは消せない。消したら true。</summary>
        internal bool RemoveTextureSet(Guid id)
        {
            if (stroke != null) { message = L.Tr("Finish the stroke first."); return false; }
            SyncCurrentSet();
            var set = textureSets.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("This project has no texture set " + id + ".", nameof(id));
            if (textureSets.Count == 1) { message = L.Tr("A project keeps at least one texture set."); return false; }
            if (!Dialogs.Confirm(L.Tr("Remove texture set?"), RemovalWarning(new[] { set }), L.Tr("Remove"), L.Tr("Cancel"))) { message = L.Tr("The texture set was not removed."); return false; }
            RemoveSets(new[] { set });
            message = L.Tr("Removed the texture set {0}.", set.Name);
            return true;
        }

        /// <summary>消すセットの確かめの文（その作業が消え、Undo できない）。</summary>
        static string RemovalWarning(IEnumerable<TextureSet> sets)
            => L.Tr("{0} will be removed with all of its layers, history and baked mesh maps. This cannot be undone (the saved file keeps it until you save).", string.Join(", ", sets.Select(s => s.Name)));

        /// <summary>セットを消す（確かめは呼ぶ側）。今のセットを消したら、並びの次（無ければ前）を今のセットにする。</summary>
        void RemoveSets(IEnumerable<TextureSet> removed)
        {
            var gone = removed.ToList();
            if (gone.Count == 0) return;
            if (gone.Contains(currentSet))
            {
                int at = textureSets.IndexOf(currentSet);
                var next = textureSets.Skip(at + 1).Concat(textureSets.Take(at).Reverse()).First(s => !gone.Contains(s));
                SwitchTextureSet(next.Id);
            }
            foreach (var set in gone) { textureSets.Remove(set); set.DisposeTextures(); meshBakeSkipped.Remove(set.Id); }
            setsRevision++;
            var note = ApplyBudgets(); if (note != null) message = note;
            repaintPixels = true; Repaint(); RepaintPanelWindowsSoon();
        }

        /// <summary>セットの名前を変える（1〜256 文字、ほかのセットと重ならない）。</summary>
        internal void RenameTextureSet(Guid id, string name)
        {
            var set = textureSets.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("This project has no texture set " + id + ".", nameof(id));
            name = name?.Trim();
            YlpFormat.CheckSetName(name);
            if (UniqueSetName(name, set) != name) throw new ArgumentException(L.Tr("Another texture set is already named {0}.", name), nameof(name));
            if (set.Name == name) return;
            set.Name = name; setsRevision++; Repaint();
        }

        /// <summary>セットが描くマテリアルのスロットを変える（ほかのセットのスロットとは重ならない）。焼いたメッシュマップはスロットが違うので古くなる。</summary>
        internal void SetTextureSetSlot(Guid id, int slot)
        {
            SyncCurrentSet();
            var set = textureSets.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("This project has no texture set " + id + ".", nameof(id));
            if (slot < 0 || slot > YlpFormat.MaxMaterialSlot) throw new ArgumentOutOfRangeException(nameof(slot));
            var owner = textureSets.FirstOrDefault(s => s != set && s.MaterialSlot == slot);
            if (owner != null) throw new InvalidOperationException(L.Tr("Material slot {0} already has the texture set {1}.", slot, owner.Name));
            if (set.MaterialSlot == slot) return;
            set.MaterialSlot = slot; if (set == currentSet) materialSlot = slot;
            setsRevision++; repaintPixels = true; Repaint();
        }

        /// <summary>スロットを描くセット（無ければ null）。</summary>
        TextureSet SetOfSlot(int slot) { SyncCurrentSet(); return textureSets.FirstOrDefault(s => s.MaterialSlot == slot); }

        /// <summary>3D ビューで今のセットのスロットでない面を押したときの知らせ。</summary>
        string OtherSlotNote(int slot)
        {
            var owner = SetOfSlot(slot);
            return owner != null ? L.Tr("This face belongs to the texture set {0}. Switch to it in the Texture Set panel to paint it.", owner.Name)
                : L.Tr("This face uses material slot {0}, which has no texture set in this project (File ▸ Project Configuration adds one).", slot);
        }

        void RepaintPanelWindowsSoon() { foreach (var w in PanelWindows) if (w != null) w.Repaint(); RepaintMeshBakeWindow(); }

        // ───────── 3D の表示 ─────────

        /// <summary>全部のセットを、それぞれのスロットに今のチャンネルで見せる（今のセットは合成器の表示、ほかは CPU の合成）。</summary>
        void ShowTextureSets()
        {
            if (preview == null) return;
            var textures = new Dictionary<int, Texture>();
            textures[materialSlot] = DisplayTexture;
            if (preview.HasModel)
                foreach (var set in textureSets)
                    if (set != currentSet && set.MaterialSlot < preview.MaterialSlotCount && !textures.ContainsKey(set.MaterialSlot)) textures[set.MaterialSlot] = SetDisplay(set);
            preview.SetPaintTextures(textures);
        }

        /// <summary>ほかのセットの 3D の表示（今のチャンネルの合成、Normal の出力を見せているなら出力）。版とチャンネルが同じあいだは作り直さない。</summary>
        internal Texture2D SetDisplay(TextureSet set)
        {
            var d = set.Document; bool output = channel == PaintChannel.Normal && showNormalOutput;
            string key = d.Revision + "|" + channel + "|" + output + "|" + d.Width + "x" + d.Height;
            if (set.Display != null && set.DisplayKey == key) return set.Display;
            set.Display = Upload(set.Display, output ? NormalMaps.Output(d) : d.Composite(channel), d.Width, d.Height, "YoluPainter texture set " + set.Name);
            set.DisplayKey = key;
            return set.Display;
        }

        /// <summary>ほかのセットの照明のノーマルマップ（Normal を使っていなければ null）。</summary>
        Texture2D SetLighting(TextureSet set)
        {
            var d = set.Document;
            if (!YlpContent.UsedChannels(d).Contains(PaintChannel.Normal)) { if (set.Lighting != null) { DestroyImmediate(set.Lighting); set.Lighting = null; set.LightingKey = null; } return null; }
            string key = d.Revision + "|" + d.Width + "x" + d.Height;
            if (set.Lighting != null && set.LightingKey == key) return set.Lighting;
            set.Lighting = Upload(set.Lighting, NormalMaps.Output(d), d.Width, d.Height, "YoluPainter texture set normals " + set.Name);
            set.LightingKey = key;
            return set.Lighting;
        }

        /// <summary>左下原点の straight RGBA8 を、長辺 <see cref="SetPreviewMaxSize"/> までに縮めてテクスチャに載せる（大きさが同じなら使い回す）。</summary>
        static Texture2D Upload(Texture2D texture, byte[] rgba, int width, int height, string name)
        {
            var (pixels, w, h) = YlpContent.Shrink(rgba, width, height, SetPreviewMaxSize);
            if (texture == null || texture.width != w || texture.height != h)
            {
                if (texture != null) DestroyImmediate(texture);
                texture = new Texture2D(w, h, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            }
            texture.name = name;
            texture.LoadRawTextureData(pixels); texture.Apply(false, false);
            return texture;
        }

        /// <summary>今のセットのサムネイルを作り直す間隔の下限（秒。スライダーのドラッグなどで毎回作り直さない）。</summary>
        const double SetThumbnailInterval = .4;
        double setThumbnailBuilt;
        /// <summary>今のセットのサムネイルが古いまま待っているか（Tick が間隔の後に描き直させる）。</summary>
        bool SetThumbnailStale => currentSet?.Thumbnail != null && document != null && (currentSet.ThumbnailRevision != document.Revision || !ReferenceEquals(currentSet.ThumbnailFor, document));

        /// <summary>パネルのサムネイル（Color の合成を点で拾った 32 × 32。描いているあいだと、前に作ってから間もないあいだは作り直さない）。</summary>
        internal Texture2D SetThumbnail(TextureSet set)
        {
            var d = set == currentSet ? document : set.Document;
            if (set.Thumbnail != null && (set.ThumbnailRevision == d.Revision && ReferenceEquals(set.ThumbnailFor, d) || stroke != null
                || set == currentSet && EditorApplication.timeSinceStartup - setThumbnailBuilt < SetThumbnailInterval)) return set.Thumbnail;
            if (set == currentSet) setThumbnailBuilt = EditorApplication.timeSinceStartup;
            if (set.Thumbnail == null) set.Thumbnail = new Texture2D(SetThumbnailSize, SetThumbnailSize, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            float scale = Mathf.Max(d.Width, d.Height) / (float)SetThumbnailSize;
            float ox = (SetThumbnailSize - d.Width / scale) / 2, oy = (SetThumbnailSize - d.Height / scale) / 2;
            var pixels = new Color32[SetThumbnailSize * SetThumbnailSize];
            for (int j = 0; j < SetThumbnailSize; j++)
                for (int i = 0; i < SetThumbnailSize; i++)
                {
                    int x = Mathf.FloorToInt((i + .5f - ox) * scale), y = Mathf.FloorToInt((j + .5f - oy) * scale);
                    if (x < 0 || y < 0 || x >= d.Width || y >= d.Height) continue;
                    var c = d.CompositePixel(PaintChannel.Color, x, y); pixels[j * SetThumbnailSize + i] = new Color32(c.R, c.G, c.B, c.A);
                }
            set.Thumbnail.SetPixels32(pixels); set.Thumbnail.Apply(false, false);
            set.ThumbnailRevision = d.Revision; set.ThumbnailFor = d;
            return set.Thumbnail;
        }

        void DisposeTextureSetTextures() { foreach (var set in textureSets) set.DisposeTextures(); }
    }
}
