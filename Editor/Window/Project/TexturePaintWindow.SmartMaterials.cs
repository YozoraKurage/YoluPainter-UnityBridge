using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// スマートマテリアルとスマートマスク（Substance Painter のスマートマテリアル・スマートマスク。Core は <see cref="SmartMaterial"/>）: 選んだ層・
    /// 層のマスクから保存する（このプロジェクトと自分の置き場の両方へ）、アセットのパネルの 3 つの出どころ（このプロジェクト・自分の置き場・内蔵）から
    /// 選んだ層の上・層の一覧の落とした所・キャンバスへ置く（スマートマスクは層のマスクへ）、取り込む・自分の置き場へ入れる・消す。
    /// <list type="bullet">
    /// <item>置くのは今のテクスチャセットの文書の 1 回の Undo。予算・層の数・フィルターの予算を超えるなら何も変えずに断る（先にプロジェクトへ入れた
    /// 画像も戻す）。セットが使っていないチャンネルの値は、オフにして置いて知らせる。メッシュマップが無い・古いときも置く（Generator はいつもどおり
    /// 入力を通して理由を出す）。</item>
    /// <item>保存・取り込み・消すはプロジェクトのもの（リソースと同じく文書の Undo の履歴に入らない。未保存にはなる）。</item>
    /// </list>
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>保存するファイルに入れるサムネイルの大きさ（パネルは 64 で見せる）。</summary>
        const int SmartThumbnailSize = 128;

        // ───────── 保存 ─────────

        /// <summary>レイヤー ▸ スマートマテリアルとして保存: 選んでいる層（複数ならそれぞれ、グループなら中身ごと）を、このプロジェクトと自分の置き場へ。
        /// 名前は一番上の層の名前。</summary>
        internal SmartResource SaveSmartMaterial(string name = null)
        {
            if (stroke != null || toolDragging) { message = L.Tr("Finish the stroke first."); return null; }
            var members = document.TopmostOf(SelectedLayers);
            if (members.Count == 0) { message = L.Tr("Select the layers to save as a smart material first."); return null; }
            try { return KeepSmart(document.CaptureSmartMaterial(members.Select(l => l.Id), name ?? members[members.Count - 1].Name, ImageResources)); }
            catch (SmartRefusedException ex) { message = SmartRefusalText(ex); return null; }
        }

        /// <summary>レイヤー ▸ マスクをスマートマスクとして保存: 選んでいる層のマスク（画素・設定・フィルターと Generator）を。</summary>
        internal SmartResource SaveSmartMask(string name = null)
        {
            if (stroke != null || toolDragging) { message = L.Tr("Finish the stroke first."); return null; }
            var layer = SelectedOrNull;
            if (layer == null) { message = L.Tr("Select a layer first."); return null; }
            if (layer.Mask == null) { message = L.Tr("{0} has no mask to save as a smart mask.", layer.Name); return null; }
            try { return KeepSmart(document.CaptureSmartMask(layer.Id, name ?? L.Tr("{0} mask", layer.Name))); }
            catch (SmartRefusedException ex) { message = SmartRefusalText(ex); return null; }
        }

        /// <summary>できたスマートマテリアルをファイルにして、このプロジェクト（.ylp と一緒に運ぶ）と自分の置き場（ほかのプロジェクトで使う）に入れる。
        /// プロジェクトの予算を先に確かめ、断るなら置き場にも書かない。置き場に書けなければ、プロジェクトのものだけにして知らせる。</summary>
        SmartResource KeepSmart(SmartMaterial material)
        {
            if (smartSaveTask != null) { message = L.Tr("A smart material is already being saved."); return null; }
            var writer = YlpContent.Writer;
            if (material.PixelBytes + material.Images.Sum(i => i.Content.ByteSize) >= 8L * 1024 * 1024)
            {
                savingSmart = material; smartSaveCancellation = new CancellationTokenSource();
                var token = smartSaveCancellation.Token;
                smartSaveTask = Task.Run(() => EncodeSmart(material, writer, token), token);
                message = L.Tr("Saving the smart material {0}…", material.Name); Repaint(); return null;
            }
            return KeepSmartBytes(material, EncodeSmart(material, writer, CancellationToken.None));
        }

        static byte[] EncodeSmart(SmartMaterial material, YlpWriterInfo writer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var preview = SmartPreview.Render(material, SmartThumbnailSize);
            token.ThrowIfCancellationRequested();
            return SmartMaterialFile.Write(material, writer, RgbaPng.Encode(preview, SmartThumbnailSize, SmartThumbnailSize), token, sphereThumbnail: true);
        }

        Task<byte[]> smartSaveTask; SmartMaterial savingSmart; CancellationTokenSource smartSaveCancellation;
        internal bool SmartSavePending => smartSaveTask != null;
        internal void CancelSmartSave()
        {
            if (smartSaveTask == null) return;
            smartSaveCancellation.Cancel();
            // 完了しても取消済みの結果をプロジェクトや置き場へ反映しない。
            var abandoned = smartSaveTask; var cancellation = smartSaveCancellation;
            abandoned.ContinueWith(t => { var ignored = t.Exception; cancellation.Dispose(); }, TaskScheduler.Default);
            smartSaveTask = null; savingSmart = null; smartSaveCancellation = null;
            message = L.Tr("Saving the smart material was cancelled."); Repaint();
        }
        internal void TickSmartSave()
        {
            if (smartSaveTask == null || !smartSaveTask.IsCompleted || stroke != null) return;
            var task = smartSaveTask; var material = savingSmart;
            smartSaveTask = null; savingSmart = null; smartSaveCancellation.Dispose(); smartSaveCancellation = null;
            if (task.IsCanceled) return;
            if (task.IsFaulted) { message = task.Exception.GetBaseException().Message; Repaint(); return; }
            TryAction(() => KeepSmartBytes(material, task.Result));
        }

        SmartResource KeepSmartBytes(SmartMaterial material, byte[] bytes)
        {
            RefreshArchiveRoom();
            string refusal = ImageResources.FindSmartByHash(GenerationStore.Hash(bytes)) == null ? ImageResources.AddSmartRefusal(SmartResource.EstimateBytes(bytes.LongLength, material), out _) : null;
            if (refusal != null) { message = L.Tr("{0} was not saved: {1}", material.Name, refusal); return null; }
            ImageResources.CheckSmartArchiveRoom(bytes);
            string file = null, libraryNote = null;
            try { file = ResourceLibraryFolder.AddSmart(PainterSettings.LibraryFolder, material.Name, bytes, out _); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { libraryNote = L.Tr("It could not be put into My Library ({0}).", ex.Message); }
            smartListing = null;
            var origin = file != null ? ResourceOrigin.Library(file, GenerationStore.Hash(bytes), bytes.LongLength) : ResourceOrigin.None;
            var held = ImageResources.AddSmart(material.Name, bytes, material, origin, out _);
            assetSource = AssetSource.Project; selectedAsset = AssetKey(AssetSource.Project, held.Id.ToString("D"));
            if (assetKind != AssetKind.All) assetKind = material.Kind == SmartKind.Mask ? AssetKind.SmartMasks : AssetKind.SmartMaterials;
            bool mask = material.Kind == SmartKind.Mask;
            message = (file != null
                    ? mask ? L.Tr("Saved {0} as a smart mask in this project and in My Library ({1}).", material.Name, file) : L.Tr("Saved {0} as a smart material ({1} layers) in this project and in My Library ({2}).", material.Name, material.LayerCount, file)
                    : mask ? L.Tr("Saved {0} as a smart mask in this project.", material.Name) : L.Tr("Saved {0} as a smart material ({1} layers) in this project.", material.Name, material.LayerCount))
                + (libraryNote != null ? " " + libraryNote : "") + (material.Notes.Count > 0 ? " " + string.Join(" ", material.Notes) : "");
            Repaint();
            return held;
        }

        // ───────── 出どころ ─────────

        /// <summary>自分の置き場のファイルを読んだもの（ファイルの長さと時刻が同じなら読み直さない）。</summary>
        readonly Dictionary<string, (long length, DateTime time, SmartMaterial material)> librarySmart = new Dictionary<string, (long, DateTime, SmartMaterial)>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, SmartMaterial> builtInSmart = new Dictionary<string, SmartMaterial>(StringComparer.Ordinal);

        /// <summary>アセットの鍵がスマートマテリアル・スマートマスクのものか（種類も）。</summary>
        internal bool TrySmartKind(string key, out SmartKind kind)
        {
            kind = default;
            if (!TryParseAssetKey(key, out var source, out string id)) return false;
            switch (source)
            {
                case AssetSource.Project: if (Guid.TryParse(id, out var guid) && ImageResources.TryGetSmart(guid, out var held)) { kind = held.Kind; return true; } return false;
                case AssetSource.Library:
                    if (id.EndsWith(ResourceLibraryFolder.MaterialExtension, StringComparison.OrdinalIgnoreCase)) { kind = SmartKind.Material; return true; }
                    if (!id.EndsWith(SmartMaterialFile.Extension, StringComparison.OrdinalIgnoreCase)) return false;
                    var item = SmartLibraryItems().FirstOrDefault(i => string.Equals(i.FileName, id, StringComparison.OrdinalIgnoreCase));
                    if (item?.Info != null) kind = item.Info.Kind;
                    return true;
                case AssetSource.Unity:
                    var info = AssetDatabase.LoadAssetAtPath<SmartMaterialImportInfo>(AssetDatabase.GUIDToAssetPath(id));
                    if (info == null) return false; kind = info.mask ? SmartKind.Mask : SmartKind.Material; return true;
                case AssetSource.BuiltIn: if (BuiltInSmartMaterials.TryGet(id, out var entry)) { kind = entry.Kind; return true; } return false;
                default: return false;
            }
        }

        /// <summary>アセットの鍵のスマートマテリアル（このプロジェクトのもの・自分の置き場のファイル・内蔵）と、見せる名前。</summary>
        internal SmartMaterial SmartAsset(string key, out string name)
        {
            if (!TryParseAssetKey(key, out var source, out string id)) throw new ArgumentException("Not an asset: " + key, nameof(key));
            switch (source)
            {
                case AssetSource.Project:
                    var held = ImageResources.GetSmart(Guid.Parse(id)); name = held.Name; return held.Material;
                case AssetSource.Unity:
                    var unity = SmartMaterialFile.Read(ResourceLibraryFolder.ReadFile(FileUtil.GetPhysicalPath(AssetDatabase.GUIDToAssetPath(id)))); name = unity.Name; return unity;
                case AssetSource.Library:
                {
                    string path = ResourceLibraryFolder.Resolve(PainterSettings.LibraryFolder, id);
                    if (!File.Exists(path)) throw new ResourceRefusedException(ResourceRefusal.Unknown, L.Tr("{0} is no longer in your library.", id));
                    var info = new FileInfo(path);
                    if (!librarySmart.TryGetValue(path, out var cached) || cached.length != info.Length || cached.time != info.LastWriteTimeUtc)
                    {
                        SmartMaterial read;
                        try { read = SmartMaterialFile.Read(ResourceLibraryFolder.ReadFile(path)); }
                        catch (InvalidDataException ex) { throw new InvalidDataException(L.Tr("{0} cannot be used: {1}", id, ex.Message), ex); }
                        librarySmart[path] = cached = (info.Length, info.LastWriteTimeUtc, read);
                    }
                    name = Path.GetFileNameWithoutExtension(id); return cached.material;
                }
                case AssetSource.BuiltIn:
                {
                    if (!BuiltInSmartMaterials.TryGet(id, out var entry)) throw new ResourceRefusedException(ResourceRefusal.Unknown, "No built-in smart material \"" + id + "\".");
                    if (!builtInSmart.TryGetValue(id, out var made)) builtInSmart[id] = made = BuiltInSmartMaterials.Make(id);
                    name = L.Tr(entry.Name); return made;
                }
                default: throw new ArgumentException("Smart materials do not come from " + source + ".", nameof(key));
            }
        }

        // ───────── 置く ─────────

        /// <summary>
        /// アセットの鍵のスマートマテリアルを置く（<paramref name="where"/> が無ければ選んでいる層の上）か、スマートマスクを層のマスクに置く
        /// （<paramref name="maskLayer"/> が無ければ選んでいる層）。断られたら何も変えずに知らせ、null を返す。
        /// </summary>
        internal SmartPlaceResult PlaceSmartAsset(string key, SmartPlacement where = null, Guid? maskLayer = null)
        {
            if (stroke != null || toolDragging) { message = L.Tr("Finish the stroke first."); return null; }
            SmartMaterial material; string name;
            try { material = SmartAsset(key, out name); }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is ResourceRefusedException || ex is UnauthorizedAccessException) { message = ex.Message; return null; }
            if (material.Kind == SmartKind.Mask) return ApplySmartMaskTo(material, name, maskLayer ?? selectedLayer);
            if (where == null && document.Layers.Any(l => l.Id == selectedLayer)) where = SmartPlacement.Above(document, selectedLayer);
            return PlaceSmartMaterialInto(material, name, where);
        }

        SmartPlaceResult PlaceSmartMaterialInto(SmartMaterial material, string name, SmartPlacement where)
        {
            where = where ?? new SmartPlacement();
            var before = new HashSet<Guid>(ImageResources.Images.Select(i => i.Id));
            try
            {
                RefreshArchiveRoom();
                var ids = SmartImages.AddTo(ImageResources, material);
                var used = YlpContent.UsedChannels(document);
                where.Channels = used.Count > 0 ? used : null; where.GroupName = name; where.ResourceIds = ids;
                var result = document.PlaceSmartMaterial(material, where);
                selectedLayer = result.LayerId; editMask = false; repaintPixels = true;
                message = (material.TopLevelCount > 1 ? L.Tr("Placed {0} as a group of {1} layers.", name, material.LayerCount) : L.Tr("Placed {0}.", name)) + PlacedNotes(material, result);
                Repaint();
                return result;
            }
            catch (Exception ex) when (ex is SmartRefusedException || ex is ResourceRefusedException || ex is LayerOpException)
            {
                // 置くのを断られたら、そのために入れた画像も戻す（何も変えない）
                foreach (var image in ImageResources.Images.Where(i => !before.Contains(i.Id)).ToList()) ImageResources.Remove(image.Id);
                message = ex is SmartRefusedException refused ? SmartRefusalText(refused) : ex is LayerOpException op ? RefusalText(op) : ex.Message;
                return null;
            }
        }

        SmartPlaceResult ApplySmartMaskTo(SmartMaterial mask, string name, Guid layerId)
        {
            var layer = document.Layers.FirstOrDefault(l => l.Id == layerId);
            if (layer == null) { message = L.Tr("Select the layer to put {0} on first.", name); return null; }
            try
            {
                var result = document.ApplySmartMask(mask, layer.Id);
                selectedLayer = layer.Id; editMask = true; repaintPixels = true;
                message = (result.ReplacedMask ? L.Tr("Put {0} on the mask of {1}, replacing its mask (Undo brings it back).", name, layer.Name) : L.Tr("Put {0} on {1} as its mask.", name, layer.Name)) + PlacedNotes(mask, result);
                Repaint();
                return result;
            }
            catch (SmartRefusedException ex) { message = SmartRefusalText(ex); return null; }
            catch (LayerOpException ex) { message = RefusalText(ex); return null; }
        }

        /// <summary>置いた後の知らせ: 大きさを変えた・オフにしたチャンネル・ピン・効いていない Generator・大きさで変えた設定。</summary>
        string PlacedNotes(SmartMaterial material, SmartPlaceResult result)
        {
            string text = "";
            if (result.Resampled) text += " " + L.Tr("It was made at {0} × {1} and was resized to {2} × {3}.", material.Width, material.Height, document.Width, document.Height);
            if (result.SwitchedOff.Count > 0)
                text += " " + L.Tr("This texture set does not use {0}: those values were placed switched off (switch the channels on in the layers to use them).", string.Join(", ", result.SwitchedOff.Select(c => L.Tr(c.ToString()))));
            if (result.Pinned > 0) text += " " + L.Tr("{0} generator(s) were pinned to this texture set's bake.", result.Pinned);
            if (result.InactiveGenerators.Count > 0) text += " " + L.Tr("Its generators have no effect until this texture set's mesh maps are baked (3D ▸ Bake Mesh Maps…).");
            if (result.Notes.Count > 0) text += " " + string.Join(" ", result.Notes);
            return text;
        }

        /// <summary>Core が断った理由の、窓の言葉。</summary>
        internal static string SmartRefusalText(SmartRefusedException ex)
        {
            switch (ex.Reason)
            {
                case SmartRefusal.NothingToSave: return L.Tr("Select the layers to save as a smart material first.");
                case SmartRefusal.NoMask: return L.Tr("The layer has no mask to save as a smart mask.");
                case SmartRefusal.WrongKind: return L.Tr("A smart mask goes on a layer's mask, and a smart material is placed as layers.");
                case SmartRefusal.Budget: return L.Tr("It needs {0} MiB of layer pixels, more than the {1} MiB left under the layer pixel budget (Project Settings ▸ YoluPainter). Nothing was changed.", (ex.Bytes + (1 << 20) - 1) >> 20, ex.Limit >> 20)
                    + (ex.Bytes == 0 ? " " + ex.Message : "");
                case SmartRefusal.LayerLimit: return L.Tr("The texture set would hold more than {0} layers. Nothing was placed.", PaintDocument.MaxLayers);
                case SmartRefusal.Filters: return L.Tr("A filter of it cannot run in this texture set: {0}", ex.Message);
                case SmartRefusal.Size: return L.Tr("It cannot be placed at this texture set's size: {0}", ex.Message);
                default: return ex.Message;
            }
        }

        // ───────── 層の一覧へのドロップ ─────────

        /// <summary>層の一覧の上でアセットのパネルから引いているスマートマテリアル・スマートマスクの、一覧の中身の座標での位置（描く印のため）。</summary>
        Vector2? smartDropAt;
        /// <summary>試験用: 最後に一覧へ落としたときの置き場所（マテリアル）か層（マスク）。</summary>
        internal SmartPlacement LastSmartDropPlacement; internal Guid LastSmartDropLayer;

        /// <summary>
        /// 層の一覧（スクロールの中）へのドロップ: スマートマテリアルは行の間（その下の行の層の上、同じグループ）・グループの行の中ほど（その中の一番上）・
        /// 一番下の線へ、スマートマスクは落とした行の層のマスクへ置く（1 回の Undo）。引いている間は落とす先の線・枠を描く。
        /// </summary>
        void HandleSmartDrop(float width, bool overList)
        {
            var e = Event.current;
            string key = DragAndDrop.GetGenericData(ResourceDragKey) as string;
            if (key == null || !TrySmartKind(key, out var kind)) { smartDropAt = null; return; }
            if (e.type == EventType.DragExited) { smartDropAt = null; Repaint(); return; }
            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
            {
                if (!overList) { smartDropAt = null; return; }
                DragAndDrop.visualMode = stroke == null && !toolDragging ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                smartDropAt = e.mousePosition;
                if (e.type == EventType.DragPerform && stroke == null && !toolDragging)
                {
                    DragAndDrop.AcceptDrag();
                    if (kind == SmartKind.Mask)
                    {
                        var layer = LayerAtRow(e.mousePosition.y);
                        LastSmartDropLayer = layer?.Id ?? Guid.Empty;
                        if (layer != null) TryAction(() => PlaceSmartAsset(key, null, layer.Id));
                    }
                    else
                    {
                        var (gap, into) = LayerDropTarget(e.mousePosition.y);
                        var where = LastSmartDropPlacement = SmartDropPlacement(gap, into);
                        TryAction(() => PlaceSmartAsset(key, where));
                    }
                    smartDropAt = null;
                }
                e.Use(); Repaint();
                return;
            }
            if (e.type == EventType.Repaint && smartDropAt.HasValue)
            {
                float y = smartDropAt.Value.y;
                if (kind == SmartKind.Mask)
                {
                    var under = LayerAtRow(y); // 効果の段の行はその層（Panels/TexturePaintWindow.EffectRows.cs）
                    if (under != null) PaintGui.Outline(new Rect(1, LayerRowY(under) + 1, width - 2, LayerRowHeight - 2), PaintTheme.Accent, 2, 3);
                    return;
                }
                var target = LayerDropTarget(y);
                if (target.into != null) PaintGui.Outline(new Rect(1, LayerRowY(target.into) + 1, width - 2, LayerRowHeight - 2), PaintTheme.Accent, 2, 3);
                else PaintGui.Fill(new Rect(4, LayerGapY(target.gap) - 1, width - 8, 2), PaintTheme.Accent);
            }
        }

        /// <summary>層の一覧の落とす先（<see cref="LayerDropTarget"/>）を置き場所に: グループの中ほどならその中の一番上、一番下の線なら一番下、
        /// ほかは線のすぐ下の行の層の上（その層のグループの中）。</summary>
        internal SmartPlacement SmartDropPlacement(int gap, PaintLayer into)
        {
            if (into != null) return new SmartPlacement { ParentId = into.Id, Position = -1 };
            int n = document.Layers.Count;
            if (gap >= n) return new SmartPlacement { ParentId = Guid.Empty, Position = 0 };
            return SmartPlacement.Above(document, document.Layers[n - 1 - gap].Id);
        }

        // ───────── 取り込む・自分の置き場へ・消す ─────────

        /// <summary>自分の置き場・内蔵のスマートマテリアルを、このプロジェクトに入れる（.ylp と一緒に運ぶ。同じものがあればそれ）。</summary>
        internal SmartResource ImportSmartAsset(string key)
        {
            RequireNoStrokeForResources();
            if (!TryParseAssetKey(key, out var source, out string id)) throw new ArgumentException("Not an asset: " + key, nameof(key));
            if (source == AssetSource.Project) return ImageResources.GetSmart(Guid.Parse(id));
            var material = SmartAsset(key, out string name);
            byte[] bytes; ResourceOrigin origin;
            if (source == AssetSource.Library || source == AssetSource.Unity)
            {
                if (source == AssetSource.Unity)
                {
                    string path = AssetDatabase.GUIDToAssetPath(id); bytes = ResourceLibraryFolder.ReadFile(FileUtil.GetPhysicalPath(path));
                    origin = ResourceOrigin.UnityAsset(id, path, AssetDatabase.GetAssetDependencyHash(path).ToString(), false);
                }
                else
                {
                bytes = ResourceLibraryFolder.ReadFile(ResourceLibraryFolder.Resolve(PainterSettings.LibraryFolder, id));
                origin = ResourceOrigin.Library(id, GenerationStore.Hash(bytes), bytes.LongLength);
                }
            }
            else
            {
                BuiltInSmartMaterials.TryGet(id, out var entry);
                var existing = ImageResources.Smart.FirstOrDefault(s => s.Origin.Kind == ResourceOriginKind.BuiltIn && s.Origin.BuiltInKey == id && s.Origin.BuiltInVersion == entry.Version);
                if (existing != null) { message = L.Tr("{0} is already in this project as \"{1}\"; nothing was added.", name, existing.Name); return existing; }
                bytes = SmartMaterialFile.Write(material, YlpContent.Writer, RgbaPng.Encode(SmartPreview.Render(material, SmartThumbnailSize), SmartThumbnailSize, SmartThumbnailSize), sphereThumbnail: true);
                origin = ResourceOrigin.BuiltIn(id, entry.Version);
            }
            RefreshArchiveRoom();
            var held = ImageResources.AddSmart(name, bytes, material, origin, out bool added, resourceKind: source == AssetSource.Library && id.EndsWith(ResourceLibraryFolder.MaterialExtension, StringComparison.OrdinalIgnoreCase) ? ResourceKind.Material : (ResourceKind?)null);
            message = added ? L.Tr("Imported {0} into this project.", name) : L.Tr("{0} is already in this project as \"{1}\"; nothing was added.", name, held.Name);
            Repaint();
            return held;
        }

        /// <summary>このプロジェクトのスマートマテリアルを自分の置き場へ（.ylsmart。同じバイトのファイルがあればそれ）。</summary>
        internal string AddSmartToLibrary(Guid id)
        {
            var held = ImageResources.GetSmart(id);
            string file = ResourceLibraryFolder.AddSmart(PainterSettings.LibraryFolder, held.Name, held.FileBytes(), out bool existed, held.ResourceKind == ResourceKind.Material ? ResourceLibraryFolder.MaterialExtension : SmartMaterialFile.Extension);
            smartListing = null;
            message = existed ? L.Tr("{0} is already in your library as {1}.", held.Name, file) : L.Tr("Put {0} into your library as {1} ({2}).", held.Name, file, PainterSettings.LibraryFolder);
            Repaint();
            return file;
        }

        /// <summary>このプロジェクトからスマートマテリアルを消す（確かめる）。置いた層はそのまま（写しなので）。</summary>
        internal bool RemoveSmartResource(Guid id)
        {
            RequireNoStrokeForResources();
            var held = ImageResources.GetSmart(id);
            if (!Dialogs.Confirm(L.Tr("Remove resource?"), L.Tr("Remove {0} from this project? Layers already placed from it stay as they are. Your library is not touched.", held.Name), L.Tr("Remove"), L.Tr("Cancel"))) return false;
            ImageResources.Remove(id);
            if (selectedAsset == AssetKey(AssetSource.Project, id.ToString("D"))) selectedAsset = null;
            message = L.Tr("Removed {0} from this project.", held.Name);
            return true;
        }

        // ───────── 自分の置き場の一覧 ─────────

        internal sealed class SmartLibraryItem
        {
            public string FileName, Path; public long Length; public DateTime Modified;
            /// <summary>並び（読めなければ null と理由）。</summary>
            public SmartFileInfo Info; public string Problem;
        }
        List<SmartLibraryItem> smartListing; string smartListingFolder;

        /// <summary>自分の置き場の .ylsmart（並びだけを読む。読めないファイルも理由を付けて並べる。黙って隠さない）。</summary>
        internal List<SmartLibraryItem> SmartLibraryItems()
        {
            string folder = PainterSettings.LibraryFolder;
            if (smartListing != null && smartListingFolder == folder) return smartListing;
            var items = new List<SmartLibraryItem>();
            foreach (var file in ResourceLibraryFolder.ListSmart(folder))
            {
                var item = new SmartLibraryItem { FileName = file.FileName, Path = file.Path, Length = file.Length, Modified = file.Modified };
                try { item.Info = SmartMaterialFile.ReadInfo(ResourceLibraryFolder.ReadFile(file.Path)); }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException) { item.Problem = ex.Message; }
                items.Add(item);
            }
            smartListing = items; smartListingFolder = folder;
            return items;
        }

        /// <summary>球のサムネイル。古いファイルのタイルの見本は使わず、保持する元データから球を作る。</summary>
        static (byte[] rgba, int width, int height) SmartThumbnail(SmartFileInfo info, Func<SmartMaterial> material)
            => SmartThumbnail(info?.SphereThumbnail == true ? info.Thumbnail : null, material);
        static (byte[] rgba, int width, int height) SmartThumbnail(byte[] png, Func<SmartMaterial> material)
        {
            if (png != null)
            {
                try { return RgbaPng.Decode(png); }
                catch (InvalidDataException) { } // 派生のエントリ: 読めなければ作り直す
            }
            var m = material();
            return (SmartPreview.Render(m, (int)AssetThumb), (int)AssetThumb, (int)AssetThumb);
        }
    }
}
