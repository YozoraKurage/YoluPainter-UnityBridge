using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// テクスチャセット（Substance Painter と同じ）: プロジェクトはモデルとテクスチャセットの並びで、セットはそれぞれモデルの中のマテリアル 1 つを、
    /// 自分の文書（レイヤー・解像度・チャンネル・Normal の設定・選択範囲・Undo の履歴）で描く。そのマテリアルを使うスロット（レンダラー ×
    /// サブメッシュ）を全部受け持つ（同じ Material のオブジェクトなら別のメッシュでも 1 つ。マテリアルの無いスロットは 1 つの Unassigned）。
    /// 焼いたメッシュマップと保存の状態もセットごと。描いているチャンネルはウィンドウで 1 つ。
    /// <list type="bullet">
    /// <item>ウィンドウの document・selectedLayer・editMask は「今のセット」のもので、セットを切り替えるとき（ストロークと
    /// ツールのドラッグを終わらせてから）今の値をセットに戻し（<see cref="SyncCurrentSet"/>）、別のセットの値を入れる（<see cref="LoadSet"/>）。
    /// 保存の版・メッシュマップなどは今のセットのものをそのまま読み書きする（下のプロパティ）。切り替えは履歴に入らない。</item>
    /// <item>セットが持つのはマテリアルの鍵（<see cref="YlpMaterialRef"/>。保存する）で、読み込んだモデルのどのスロットかは、モデルを読む・セットの
    /// 並びが変わるたびに <see cref="ResolveSetMaterials"/> が鍵から求める（識別子 → Unassigned → 名前 → まだ結び付けていないスロットの番号の順）。
    /// スロットを比べるのは <see cref="PaintsSlot"/>（今のセットがそのスロットを受け持つか）と <see cref="TextureSet.PaintsSlot"/> に寄せる。</item>
    /// <item>未保存かどうかは全部のセットの文書の版と、セットの並び（足す・消す・名前・マテリアル）の版で決める。</item>
    /// <item>3D ビューは全部のセットを、それぞれのスロットに今のチャンネルで見せる。今のセットは合成器（GPU）、ほかのセットは CPU の
    /// 合成から作ったテクスチャ（文書の版とチャンネルで覚え、変わったときだけ作り直す。長辺 <see cref="SetPreviewMaxSize"/> まで）。</item>
    /// <item>メモリの予算はプロジェクト全体のもの（<see cref="ApplyBudgets"/>）。</item>
    /// </list>
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>プロジェクトの中の 1 つのテクスチャセット。今のセットの文書・選んだ層はウィンドウの側が持ち、
        /// <see cref="TextureSets"/> で読むときに戻してある。</summary>
        internal sealed class TextureSet
        {
            public Guid Id { get; }
            public string Name { get; internal set; }
            /// <summary>描くマテリアル（保存する鍵。モデルのどのスロットかは <see cref="Slots"/>）。</summary>
            public YlpMaterialRef Material { get; internal set; }
            /// <summary>読み込んだモデルでのマテリアルの組（<see cref="IsolatedModelPreview.MaterialGroups"/> の番号。モデルに無い・モデルが無ければ −1）。</summary>
            public int MaterialGroup { get; internal set; } = -1;
            int[] slots = new int[0];
            /// <summary>このセットが受け持つスロット（平らにした番号、昇順）。モデルに無い・モデルが無ければ空。</summary>
            public IReadOnlyList<int> Slots => slots;
            /// <summary>このセットがスロットを受け持つか（面がこのセットのものか）。</summary>
            public bool PaintsSlot(int slot) => Array.BinarySearch(slots, slot) >= 0;
            /// <summary>読み込んだモデルにこのセットのマテリアルがあるか。</summary>
            public bool InModel => slots.Length > 0;
            /// <summary>代表のスロット（マテリアルの表示の対応など、マテリアルが同じなら同じになるものに使う。無ければ −1）。</summary>
            internal int FirstSlot => slots.Length > 0 ? slots[0] : -1;
            internal void Bind(int group, IEnumerable<int> groupSlots) { MaterialGroup = group; slots = groupSlots == null ? new int[0] : groupSlots.OrderBy(s => s).ToArray(); }
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

            public TextureSet(Guid id, string name, YlpMaterialRef material, PaintDocument document)
            {
                if (id == Guid.Empty) throw new ArgumentException("A texture set needs an ID.", nameof(id));
                YlpFormat.CheckSetName(name);
                Id = id; Name = name; Material = material ?? throw new ArgumentNullException(nameof(material)); Document = document ?? throw new ArgumentNullException(nameof(document));
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
        /// <summary>セットの並び（足す・消す・名前・マテリアル）の版と、保存した・作った直後・復旧 checkpoint を書いたときの版。</summary>
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
            currentSet.SelectedLayer = selectedLayer; currentSet.EditMask = editMask;
        }

        /// <summary>今のセットがこのスロット（平らにした番号）を受け持つか。3D ビューで押した面・パス・選択・塗りつぶしが今のセットのものかは
        /// これで問う（同じマテリアルのスロットが全部 true）。</summary>
        internal bool PaintsSlot(int slot) => currentSet != null && currentSet.PaintsSlot(slot);
        /// <summary>今のセットが受け持つスロット（モデルに無ければ空）。</summary>
        internal IReadOnlyList<int> CurrentSlots => currentSet != null ? currentSet.Slots : (IReadOnlyList<int>)new int[0];
        /// <summary>今のセットのマテリアルの組（<see cref="SurfaceTriangle.Material"/>。モデルに無ければ −1）。</summary>
        internal int CurrentMaterialGroup => currentSet != null ? currentSet.MaterialGroup : -1;
        /// <summary>今のセットの代表のスロット（マテリアルの表示の対応など。無ければ −1）。</summary>
        int CurrentFirstSlot => currentSet != null ? currentSet.FirstSlot : -1;

        /// <summary>セットを今のセットにする（ウィンドウの値に入れ、表示を作り直させる）。文書・履歴・版は変えない。</summary>
        void LoadSet(TextureSet set)
        {
            currentSet = set; document = set.Document; editMask = set.EditMask;
            ClearLayerSelection(); // 複数選択はセットごとに持たない（切り替えたら空）
            selectedLayer = document.Layers.Any(l => l.Id == set.SelectedLayer) ? set.SelectedLayer : document.Layers.Count > 0 ? document.Layers[document.Layers.Count - 1].Id : Guid.Empty;
            if (editMask && document.Layers.FirstOrDefault(l => l.Id == selectedLayer)?.Mask == null) editMask = false;
            WatchHistory(set);
            repaintPixels = true; renderedRevision = -1; lightingRevision = -1; overlayRevision = -1;
            selectedFilter = Guid.Empty; renamingLayer = Guid.Empty; layerScroll = Vector2.zero;
            ConnectGeneratorInputs(); // セットの文書の Generator に、そのセットの焼いたマップを渡す（TexturePaintWindow.Generators.cs）
        }

        /// <summary>プロジェクトのセットを入れ替える（新規・開く・取り込み・復旧）。走っているベイクは前のプロジェクトのものなので止める。</summary>
        void ReplaceProject(IList<TextureSet> next, TextureSet current)
        {
            if (next == null || next.Count == 0 || !next.Contains(current)) throw new ArgumentException("A project needs its current texture set.");
            DetachRecoveryForProjectChange();
            visibility = new VisibilityState(); appliedVisibility = null;
            if (AbandonMeshBake()) message = L.Tr("The mesh-map bake was stopped because the document changed.");
            meshBakeOutcome = null; meshBakeSkipped.Clear(); meshMapView = MeshMapView.None;
            foreach (var set in textureSets) if (!next.Contains(set)) set.DisposeTextures();
            textureSets.Clear(); textureSets.AddRange(next);
            ImageResources.Clear(); // プロジェクトのリソースも入れ替わる（開く・戻すときは呼ぶ側が読んだものを入れる。TexturePaintWindow.Resources.cs）
            currentSet = null; LoadSet(current);
            ResolveSetMaterials(refineKeys: false); // 読み込んであるモデルに結び付ける（開く・戻すときはモデルを読んだ後にもう一度。鍵はそのとき）
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
        YlpProjectInfo ProjectInfo() => new YlpProjectInfo(textureSets.Select(s => new YlpTextureSetInfo(s.Id, s.Name, s.Material)), currentSet.Id);

        // ───────── 名前 ─────────

        /// <summary>マテリアルの組の既定の名前: マテリアルの名前（マテリアルの無い組は Unassigned。空の名前なら「テクスチャセット n」）。</summary>
        static string BaseSetName(PreviewMaterialGroup group)
        {
            string name = group == null ? null : group.Unassigned ? L.Tr(PreviewMaterialGroup.UnassignedName) : group.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Any(c => c < 0x20 || c == 0x7f)) name = L.Tr("Texture Set") + " " + ((group?.Index ?? 0) + 1);
            return name.Length > YlpFormat.MaxTextureSetNameLength ? name.Substring(0, YlpFormat.MaxTextureSetNameLength) : name;
        }
        /// <summary>モデルが無いときのセットの既定の名前（「テクスチャセット n」）。</summary>
        static string NumberedSetName(int n) => L.Tr("Texture Set") + " " + n;
        /// <summary>今のプロジェクトのほかのセットと重ならない既定の名前（except は数えない）。</summary>
        string DefaultSetName(PreviewMaterialGroup group, TextureSet except) => UniqueSetName(BaseSetName(group), except);
        /// <summary>wanted が今のプロジェクトのほかのセットの名前と（大文字小文字を区別せずに）重なれば「 2」「 3」…を足す。</summary>
        string UniqueSetName(string wanted, TextureSet except)
        {
            var taken = new HashSet<string>(textureSets.Where(s => s != except).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(wanted)) return wanted;
            for (int n = 2; ; n++) { string name = wanted + " " + n; if (!taken.Contains(name)) return name; }
        }

        // ───────── マテリアル ─────────

        /// <summary>モデルのマテリアルの組を指す鍵（保存する）: マテリアルの名前と、アセットなら GUID と localFileId。マテリアルの無い組は Unassigned。</summary>
        internal static YlpMaterialRef MaterialKeyOf(PreviewMaterialGroup group)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));
            if (group.Unassigned) return YlpMaterialRef.UnassignedSlots;
            string name = KeyName(group.Material.name);
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(group.Material, out string guid, out long fileId) && guid != null && guid.Length == 32 && guid.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
                return YlpMaterialRef.Material(name, guid, fileId);
            return YlpMaterialRef.Material(name);
        }
        /// <summary>鍵に入れるマテリアルの名前（制御文字を除き、256 文字まで）。照合も同じ形で比べる。</summary>
        static string KeyName(string name)
        {
            var clean = new string((name ?? "").Where(c => c >= 0x20 && c != 0x7f).ToArray());
            return clean.Length > YlpFormat.MaxTextureSetNameLength ? clean.Substring(0, YlpFormat.MaxTextureSetNameLength) : clean;
        }
        /// <summary>組のマテリアルの識別子（GUID:localFileId。アセットでなければ null）。</summary>
        static string IdentityOf(PreviewMaterialGroup group)
            => group != null && !group.Unassigned && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(group.Material, out string guid, out long fileId) && !string.IsNullOrEmpty(guid)
                ? guid + ":" + fileId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        /// <summary>
        /// セットの鍵を、読み込んだモデルのマテリアルの組に結び付ける（<see cref="TextureSet.Slots"/>）。照合の順は、識別子（GUID と localFileId）→
        /// Unassigned → 名前（大文字小文字まで同じ、次に大文字小文字を区別せず）→ まだ結び付けていないスロットの番号（そのスロットのマテリアル）。
        /// 1 つの組は 1 つのセットだけが持ち、後から同じ組に落ちたセットは「モデルに無い」のまま残して知らせる（黙って捨てたり混ぜたりしない）。
        /// 本物のモデルなら、スロットの番号で結び付いたセットの鍵をそのマテリアルの鍵にする（デモキューブでは番号のまま）。モデルが無ければ全部が
        /// モデルに無い。結び付きが変わったセットの数を返す。refineKeys が false なら鍵を変えない（プロジェクトを入れ替えた直後は、まだ前のモデルが
        /// 読み込んであるので）。
        /// </summary>
        internal int ResolveSetMaterials(List<string> notes = null, bool refineKeys = true)
        {
            // マテリアルの組は形の準備（別スレッド）の前に決まるので、準備中でも結び付ける
            var groups = preview != null && preview.HasSnapshot ? preview.MaterialGroups : (IReadOnlyList<PreviewMaterialGroup>)new PreviewMaterialGroup[0];
            var plan = MatchMaterials(textureSets.Select(t => t.Material).ToList(), groups, notes, textureSets.Select(t => t.Name).ToList());
            int changed = 0;
            for (int i = 0; i < textureSets.Count; i++)
            {
                var set = textureSets[i]; int group = plan[i];
                var slots = group >= 0 ? groups[group].Slots : null;
                if (set.MaterialGroup != group || !set.Slots.SequenceEqual(slots ?? new int[0])) changed++;
                set.Bind(group, slots);
                // 番号で結び付いたセットは、本物のモデルのマテリアルの鍵にする（保存の形が変わるだけで、作業は変わらないので未保存にしない）
                if (refineKeys && group >= 0 && set.Material.IsPendingSlot && model != null)
                {
                    var key = MaterialKeyOf(groups[group]);
                    if (key.ExclusiveKey == null || textureSets.All(o => o == set || o.Material.ExclusiveKey != key.ExclusiveKey)) set.Material = key;
                }
            }
            if (changed > 0) { repaintPixels = true; renderedRevision = -1; uvEdgesGeometry = null; modelTiles = null; }
            return changed;
        }

        /// <summary>鍵の並びを組に照合する（<see cref="ResolveSetMaterials"/> の決まり）。結果は鍵ごとの組の番号（無ければ −1）。names は知らせに使う。</summary>
        internal static int[] MatchMaterials(IReadOnlyList<YlpMaterialRef> keys, IReadOnlyList<PreviewMaterialGroup> groups, List<string> notes = null, IReadOnlyList<string> names = null)
        {
            var result = Enumerable.Repeat(-1, keys.Count).ToArray();
            var taken = new bool[groups.Count];
            var identities = groups.Select(IdentityOf).ToArray();
            void Take(int key, int group) { result[key] = group; taken[group] = true; }
            int Free(Func<int, bool> fits) { for (int g = 0; g < groups.Count; g++) if (!taken[g] && fits(g)) return g; return -1; }
            // 1. 識別子  2. Unassigned  3. 名前（大文字小文字まで）  4. 名前（区別しない）
            for (int k = 0; k < keys.Count; k++)
                if (keys[k].Identity != null) { int g = Free(i => identities[i] == keys[k].Identity); if (g >= 0) Take(k, g); }
            for (int k = 0; k < keys.Count; k++)
                if (result[k] < 0 && keys[k].Unassigned) { int g = Free(i => groups[i].Unassigned); if (g >= 0) Take(k, g); }
            for (int k = 0; k < keys.Count; k++)
                if (result[k] < 0 && keys[k].IsMaterial) { int g = Free(i => !groups[i].Unassigned && KeyName(groups[i].Name) == keys[k].Name); if (g >= 0) Take(k, g); }
            for (int k = 0; k < keys.Count; k++)
                if (result[k] < 0 && keys[k].IsMaterial) { int g = Free(i => !groups[i].Unassigned && string.Equals(KeyName(groups[i].Name), keys[k].Name, StringComparison.OrdinalIgnoreCase)); if (g >= 0) Take(k, g); }
            // 5. まだ結び付けていないスロットの番号: そのスロットのマテリアル。1 つだけのセットなら、モデルの範囲に寄せる（前からのふるまい）
            int slotCount = groups.Sum(g => g.Slots.Count);
            for (int k = 0; k < keys.Count; k++)
            {
                if (result[k] >= 0 || !keys[k].IsPendingSlot || slotCount == 0) continue;
                int slot = keys.Count == 1 ? Math.Min(keys[k].Slot, slotCount - 1) : keys[k].Slot;
                int g = groups.FirstOrDefault(x => x.Slots.Contains(slot))?.Index ?? -1;
                if (g < 0) continue;
                if (!taken[g]) { Take(k, g); continue; }
                int owner = Array.IndexOf(result, g);
                notes?.Add(L.Tr("{0} and {1} both painted material slots of {2}; {1} is kept, but it is not shown on the model. Choose its material in File ▸ Project Configuration.",
                    names != null && owner >= 0 ? names[owner] : "?", names != null ? names[k] : "?", groups[g].Unassigned ? L.Tr(PreviewMaterialGroup.UnassignedName) : groups[g].Name));
            }
            return result;
        }

        // ───────── 切り替え・足す・消す・名前とマテリアル ─────────

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

        /// <summary>空のテクスチャセットを足して今のセットにする: 今のセットと同じ大きさ・同じ使うチャンネル・同じ Normal の設定の、空のレイヤー 1 枚。
        /// material は読み込んだモデルのマテリアルの組の番号（ほかのセットが持っていないもの）。−1 ならモデルのどのマテリアルにも結び付けない
        /// セット（モデルに無いスロットの番号を仮の鍵にする。後でプロジェクト設定でマテリアルを選ぶ）。</summary>
        internal TextureSet AddTextureSet(int material, string name = null)
        {
            if (stroke != null) throw new InvalidOperationException(L.Tr("Finish the stroke first."));
            SyncCurrentSet();
            if (textureSets.Count >= YlpFormat.MaxTextureSets) throw new InvalidOperationException(L.Tr("A project has at most {0} texture sets.", YlpFormat.MaxTextureSets));
            var groups = preview != null && preview.HasModel ? preview.MaterialGroups : (IReadOnlyList<PreviewMaterialGroup>)new PreviewMaterialGroup[0];
            if (material < -1 || material >= groups.Count) throw new ArgumentOutOfRangeException(nameof(material));
            var group = material >= 0 ? groups[material] : null;
            var owner = group != null ? textureSets.FirstOrDefault(s => s.MaterialGroup == material) : null;
            if (owner != null) throw new InvalidOperationException(L.Tr("The material {0} already has the texture set {1}.", BaseSetName(group), owner.Name));
            if (name != null) { YlpFormat.CheckSetName(name); if (UniqueSetName(name, null) != name) throw new ArgumentException(L.Tr("Another texture set is already named {0}.", name), nameof(name)); }
            var key = group != null ? MaterialKeyOf(group) : YlpMaterialRef.PendingSlot(UnboundPendingSlot());
            var set = NewEmptySet(key, name ?? (group != null ? DefaultSetName(group, null) : UniqueSetName(NumberedSetName(textureSets.Count + 1), null)));
            textureSets.Add(set); setsRevision++;
            ResolveSetMaterials();
            SwitchTextureSet(set.Id);
            message = group != null ? L.Tr("Added the texture set {0} (material {1}).", set.Name, BaseSetName(group)) : L.Tr("Added the texture set {0}; choose its material in File ▸ Project Configuration.", set.Name);
            return set;
        }

        /// <summary>今のセットに合わせた空のセット（履歴なし。作った直後の版を覚える）。大きさは width × height（0 なら今のセットと同じ）。</summary>
        TextureSet NewEmptySet(YlpMaterialRef material, string name, int width = 0, int height = 0)
        {
            var like = document;
            var d = new PaintDocument(width > 0 ? width : like.Width, height > 0 ? height : like.Height, like.TileSize, PainterSettings.UndoBudgetBytes);
            var layer = d.AddLayer(L.Tr("Layer") + " 1");
            foreach (var c in YlpContent.UsedChannels(like)) if (!layer.IsChannelEnabled(c)) d.SetChannelEnabled(layer.Id, c, true);
            if (!d.NormalSettings.Equals(like.NormalSettings)) d.SetNormalSettings(like.NormalSettings);
            d.ClearHistory();
            return new TextureSet(d.Id, name, material, d) { SelectedLayer = layer.Id, PristineRevision = d.Revision };
        }

        /// <summary>まだどのセットも持っていないスロットの番号（モデルが無いときに足すセットの仮の鍵）。</summary>
        int FreePendingSlot(IEnumerable<YlpMaterialRef> also = null)
        {
            var used = new HashSet<int>(textureSets.Select(t => t.Material).Concat(also ?? Enumerable.Empty<YlpMaterialRef>()).Where(m => m.IsPendingSlot).Select(m => m.Slot));
            int slot = 0; while (used.Contains(slot)) slot++;
            return slot;
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

        /// <summary>セットが描くマテリアルを、読み込んだモデルのマテリアルの組に替える（ほかのセットの組とは重ならない）。焼いたメッシュマップは
        /// スロットが違うので古くなる。</summary>
        internal void SetTextureSetMaterial(Guid id, int material)
        {
            SyncCurrentSet();
            var set = textureSets.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("This project has no texture set " + id + ".", nameof(id));
            if (preview == null || !preview.HasModel || material < 0 || material >= preview.MaterialGroups.Count) throw new ArgumentOutOfRangeException(nameof(material));
            var group = preview.MaterialGroups[material];
            var owner = textureSets.FirstOrDefault(s => s != set && s.MaterialGroup == material);
            if (owner != null) throw new InvalidOperationException(L.Tr("The material {0} already has the texture set {1}.", BaseSetName(group), owner.Name));
            if (set.MaterialGroup == material) return;
            set.Material = MaterialKeyOf(group); set.Bind(material, group.Slots);
            setsRevision++; repaintPixels = true; renderedRevision = -1; Repaint();
        }

        /// <summary>スロットを描くセット（無ければ null）。</summary>
        TextureSet SetOfSlot(int slot) { SyncCurrentSet(); return textureSets.FirstOrDefault(s => s.PaintsSlot(slot)); }

        /// <summary>3D ビューで今のセットのスロットでない面を押したときの知らせ。</summary>
        string OtherSlotNote(int slot)
        {
            var owner = SetOfSlot(slot);
            int group = preview != null ? preview.MaterialGroupOfSlot(slot) : -1;
            string material = group >= 0 ? BaseSetName(preview.MaterialGroups[group]) : L.Tr("slot {0}", slot);
            return owner != null ? L.Tr("This face belongs to the texture set {0}. Double-click it, or switch in the Texture Set panel, to paint it.", owner.Name)
                : L.Tr("This face uses the material {0}, which has no texture set in this project (File ▸ Project Configuration adds one).", material);
        }

        /// <summary>3D ビューで今のセットのスロットでない面を押したとき: ダブルクリックならその面のセットに切り替えて true（描きはしない）、
        /// それ以外は知らせを出して false。</summary>
        bool OtherSlotPressed(int slot)
        {
            var owner = SetOfSlot(slot); var e = Event.current;
            if (owner != null && e != null && e.clickCount >= 2)
            {
                if (SwitchTextureSet(owner.Id)) message = L.Tr("Texture set: {0}.", owner.Name);
                return true;
            }
            message = OtherSlotNote(slot); return false;
        }

        void RepaintPanelWindowsSoon() { foreach (var w in PanelWindows) if (w != null) w.Repaint(); RepaintMeshBakeWindow(); }

        // ───────── 3D の表示 ─────────

        /// <summary>全部のセットを、それぞれのスロット（マテリアルを使う全部のスロット）に今のチャンネルで見せる（今のセットは合成器の表示、
        /// ほかは CPU の合成）。</summary>
        void ShowTextureSets()
        {
            if (preview == null) return;
            var textures = new Dictionary<int, Texture>();
            foreach (int slot in CurrentSlots) textures[slot] = DisplayTexture;
            if (preview.HasModel)
                foreach (var set in textureSets)
                {
                    if (set == currentSet || !set.InModel) continue;
                    // 作り直しは表示の準備の予算の内側だけ（大きなモデルの準備中に止まらない）。作れなければ前の表示のまま
                    string key = SetDisplayKey(set.Document);
                    Texture display = set.Display != null && set.DisplayKey == key || AdmitPreviewDisplayWork() ? SetDisplay(set) : set.Display;
                    if (display == null) continue;
                    foreach (int slot in set.Slots) if (!textures.ContainsKey(slot)) textures[slot] = display;
                }
            preview.SetPaintTextures(textures);
        }

        /// <summary>ほかのセットの 3D の表示（今のチャンネルの合成、Normal の出力を見せているなら出力）。版とチャンネルが同じあいだは作り直さない。</summary>
        internal Texture2D SetDisplay(TextureSet set)
        {
            var d = set.Document; bool output = channel == PaintChannel.Normal && showNormalOutput;
            string key = SetDisplayKey(d);
            if (set.Display != null && set.DisplayKey == key) return set.Display;
            set.Display = Upload(set.Display, output ? NormalMaps.Output(d) : d.Composite(channel), d.Width, d.Height, "YoluPainter texture set " + set.Name);
            set.DisplayKey = key;
            return set.Display;
        }

        string SetDisplayKey(PaintDocument d) => d.Revision + "|" + channel + "|" + (channel == PaintChannel.Normal && showNormalOutput) + "|" + d.Width + "x" + d.Height;
        static string SetLightingKey(PaintDocument d) => d.Revision + "|" + d.Width + "x" + d.Height;

        /// <summary>ほかのセットの照明のノーマルマップ（Normal を使っていなければ null）。</summary>
        Texture2D SetLighting(TextureSet set)
        {
            var d = set.Document;
            if (!YlpContent.UsedChannels(d).Contains(PaintChannel.Normal)) { if (set.Lighting != null) { DestroyImmediate(set.Lighting); set.Lighting = null; set.LightingKey = null; } return null; }
            string key = SetLightingKey(d);
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
