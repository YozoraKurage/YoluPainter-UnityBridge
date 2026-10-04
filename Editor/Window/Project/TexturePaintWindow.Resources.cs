using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// プロジェクトのリソース（Substance Painter のシェルフのリソースに当たる。全部のテクスチャセットで共通の画像）: 取り込み（Unity の
    /// Texture2D・画像のファイル・自分の置き場・内蔵）、自分の置き場へ入れる、名前を変える・消す、レイヤーとして置く、出どころが変わったかの
    /// 確かめと更新。保存と復旧は .ylp の形式 4（<see cref="ResourceIndex"/>。TexturePaintWindow.Files.cs）。
    /// <list type="bullet">
    /// <item>リソースは参照（出どころ）と写し（.ylp に埋め込む画素）を両方持つ。出どころが消えても写しで使える。元のアセット・インポート設定・
    /// ファイルは読むだけ（<see cref="UnityTextureReader"/>）。</item>
    /// <item>リソースの出し入れは文書の Undo の履歴に入らない（テクスチャセットの並びと同じくプロジェクトのもの）。未保存にはなる。消すのは
    /// 確かめてから、使っているもの（<see cref="ProjectResources.AddUsageProbe"/>）があれば断る。置くのは今のセットの文書の 1 回の Undo。</item>
    /// <item>出どころが変わったかは、開いたとき・アセットのパネルを出したとき・Unity のプロジェクトが変わったとき（パネルが出ていれば）に
    /// 確かめる。自動では置き換えず、開いたときは尋ね、パネルでは知らせて「更新」「写しのまま」を選ばせる。</item>
    /// </list>
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        readonly ProjectResources resources = new ProjectResources();
        bool resourcesHooked;

        /// <summary>このプロジェクトのリソース（段階 3 の投影・デカールなどが ID で引く。<see cref="IImageResources"/>）。</summary>
        internal ProjectResources ImageResources { get { HookResources(); return resources; } }

        /// <summary>出どころの状態。</summary>
        internal enum ResourceSourceState { NotLinked, Unchanged, Changed, Missing, Unreadable }
        internal sealed class ResourceSourceCheck
        {
            public ResourceSourceState State;
            public string Reason;
            /// <summary>Changed: 出どころの今の画素と、更新したときの出どころ。</summary>
            public ImageContent Current; public ResourceOrigin CurrentOrigin;
            /// <summary>確かめたときの出どころの印（同じ印なら確かめ直さない）。</summary>
            public string Stamp;
            /// <summary>Unity のアセットの今のパス（動いていれば元と違う）。ファイルはその絶対パス。</summary>
            public string CurrentPath;
            /// <summary>ファイル: 確かめたときの長さと更新時刻（同じなら読み直さない）。</summary>
            public string Quick;
        }
        readonly Dictionary<Guid, ResourceSourceCheck> resourceChecks = new Dictionary<Guid, ResourceSourceCheck>();
        /// <summary>「写しのまま」を選んだリソースと、そのときの出どころの中身（出どころがまた変われば、また知らせる）。このセッションだけ。</summary>
        readonly Dictionary<Guid, string> keptResourceCopies = new Dictionary<Guid, string>();

        void HookResources()
        {
            if (resourcesHooked) return;
            resourcesHooked = true;
            resources.Changed += OnResourcesChanged;
            resources.AddUsageProbe(ShelfBrushUsage);
            resources.AddUsageProbe(FillImageUsage); // 塗りつぶしの画像が使っているリソースは消さない（TexturePaintWindow.FillImages.cs）
            resources.BudgetBytes = PainterSettings.ResourceBudgetBytes;
        }
        void UnhookResources() { if (resourcesHooked) { resources.Changed -= OnResourcesChanged; resources.RemoveUsageProbe(FillImageUsage); resources.RemoveUsageProbe(ShelfBrushUsage); } resourcesHooked = false; }

        void OnResourcesChanged(ResourceChange change)
        {
            switch (change.Kind)
            {
                case ResourceChangeKind.Reset: shelfTipCache.Clear(); CancelSmartSave(); resourceChecks.Clear(); keptResourceCopies.Clear(); resourceChecksStale = true; break;
                case ResourceChangeKind.Removed: resourceChecks.Remove(change.Id); keptResourceCopies.Remove(change.Id); setsRevision++; break;
                default: setsRevision++; break;
            }
            // 塗りつぶしの画像が読む中身・色空間が変われば、文書に問い合わせて表示（合成・3D・サムネイル）を作り直させる（TexturePaintWindow.FillImages.cs）
            if (change.Kind == ResourceChangeKind.ContentReplaced || change.Kind == ResourceChangeKind.ColorSpaceChanged || change.Kind == ResourceChangeKind.Removed) PollGeneratorInputs();
            Repaint();
        }

        /// <summary>個人の設定の予算を入れる。開いたファイルがすでに超えていれば、何も捨てずに知らせる（以後の取り込みは断る）。</summary>
        internal string ApplyResourceBudget()
        {
            HookResources();
            long budget = PainterSettings.ResourceBudgetBytes;
            resources.BudgetBytes = budget;
            RefreshArchiveRoom();
            return resources.UsedBytes > budget
                ? "This project's resources hold " + (resources.UsedBytes >> 20) + " MiB of pixels, above the " + (budget >> 20) + " MiB resource budget in Project Settings > YoluPainter; nothing more can be imported until the budget is raised."
                : null;
        }

        /// <summary>開いた・戻したプロジェクトのリソースに入れ替える（読み込みは呼ぶ側が先に済ませ、失敗なら何も変えない）。</summary>
        string AdoptResources(ProjectResources loaded)
        {
            HookResources();
            resources.ResetTo(loaded);
            return ApplyResourceBudget();
        }

        // ───────── 取り込み ─────────

        void RequireNoStrokeForResources() { if (stroke != null || toolDragging) throw new InvalidOperationException(L.Tr("A stroke is in progress.")); }

        /// <summary>取り込む前に、画素の量で予算を確かめる（大きな画像を読んでから断らない）。同じ中身が既にあれば通るはずだが、読む前には
        /// 分からないので、予算に収まらなければ断る。</summary>
        internal void RefreshArchiveRoom()
        {
            // 描いている間は測らない（文書の大きさを測る DocumentBinary.Measure はストロークの最中を断る。設定の変更はストロークの最中にも
            // 届く）。リソースの取り込みはどれもストロークの最中を断り、その前にここを通るので、古い値のまま取り込むことは無い
            if (stroke != null || textureSets.Any(s => s.Document.HasActiveStroke)) return;
            // 合成の PNG は圧縮の効かない場合も数え、余分なチャンク・小さな状態のエントリ用に余裕を取る。
            long reserved = 2L * 1024 * 1024;
            foreach (var set in textureSets)
            {
                var d = set.Document;
                reserved = checked(reserved + DocumentBinary.Measure(d));
                long rgba = (long)d.Width * d.Height * 4;
                reserved = checked(reserved + (rgba + rgba / 50 + d.Height + 4096) * YlpContent.UsedChannels(d).Count);
                if (d.Selection != null) reserved = checked(reserved + (long)d.Width * d.Height + 65536);
                reserved = checked(reserved + (set.ImportedOriginal?.LongLength ?? 0));
                foreach (var map in set.MeshMaps.Maps) reserved = checked(reserved + map.PayloadBytes + 4096);
            }
            ImageResources.ArchiveBudgetBytes = Math.Max(0, YlpArchive.MaxTotalBytes - reserved);
        }

        void CheckResourceRoom(long bytes, string what)
        {
            HookResources();
            if (resources.Count >= ProjectResources.MaxResources) throw new ResourceRefusedException(ResourceRefusal.TooMany, "A project holds at most " + ProjectResources.MaxResources + " resources.");
            if (resources.UsedBytes + bytes > resources.BudgetBytes)
                throw new ResourceRefusedException(ResourceRefusal.OverBudget, what + " needs " + (bytes >> 20) + " MiB; the project's resources would exceed the " + (resources.BudgetBytes >> 20) + " MiB resource budget (Project Settings > YoluPainter). Nothing was imported.");
        }

        ImageResource AddResource(string name, ImageContent content, ResourceOrigin origin, ResourceColorSpace colorSpace, IEnumerable<string> notes, string source)
        {
            RefreshArchiveRoom();
            var image = ImageResources.Add(name, content, origin, colorSpace, out bool added);
            resourceChecks[image.Id] = new ResourceSourceCheck { State = origin.Kind == ResourceOriginKind.None ? ResourceSourceState.NotLinked : ResourceSourceState.Unchanged, Stamp = origin.SourceStamp, CurrentPath = origin.Path };
            var extra = notes?.Where(n => !string.IsNullOrEmpty(n)).ToList() ?? new List<string>();
            message = (added ? L.Tr("Imported {0} ({1} × {2}) from {3} into this project.", image.Name, image.Width, image.Height, source)
                             : L.Tr("{0} is already in this project as \"{1}\" (the same pixels); nothing was added.", name, image.Name))
                      + (extra.Count > 0 ? " " + string.Join(" ", extra) : "");
            Repaint();
            return image;
        }

        /// <summary>Unity の Texture2D（Assets・Packages の中）を取り込む: 参照（GUID・パス・Unity の印）と写しを持つ。元のアセットと
        /// インポート設定は変えない。</summary>
        internal ImageResource ImportUnityTexture(Texture2D texture)
        {
            RequireNoStrokeForResources();
            var refusal = UnityTextureReader.Refusal(texture, out var kind);
            if (refusal != null) throw new ResourceRefusedException(kind, refusal);
            CheckResourceRoom((long)texture.width * texture.height * 4, "\"" + texture.name + "\"");
            string path = AssetDatabase.GetAssetPath(texture), guid = AssetDatabase.AssetPathToGUID(path);
            var read = UnityTextureReader.Read(texture);
            var origin = ResourceOrigin.UnityAsset(guid, path, UnityTextureReader.Stamp(path), read.ThroughGpu, UnityResourceObject.LocalId(texture));
            return AddResource(texture.name, read.Content, origin, read.ColorSpace, read.Notes, path);
        }

        /// <summary>画像のファイル（PNG・JPEG。プロジェクトの外でもよい）を取り込む: 参照（パス・ファイルの SHA-256）と写し。</summary>
        internal ImageResource ImportImageFile(string path)
        {
            RequireNoStrokeForResources();
            path = Path.GetFullPath(path);
            if (!ImageFiles.IsImageFile(path)) throw new ResourceRefusedException(ResourceRefusal.Unsupported, Path.GetFileName(path) + " is not a PNG or JPEG file.");
            var read = ImageFiles.Read(path);
            CheckRoomFor(read.Content, Path.GetFileName(path));
            return AddResource(Path.GetFileNameWithoutExtension(path), read.Content, ResourceOrigin.File(path, read.Sha256, read.Length), ResourceColorSpace.Unspecified, new[] { read.Note }, path);
        }

        /// <summary>自分の置き場のファイルを取り込む（参照は置き場の中のファイルの名前）。</summary>
        internal ImageResource ImportLibraryImage(string fileName)
        {
            RequireNoStrokeForResources();
            string path = ResourceLibraryFolder.Resolve(PainterSettings.LibraryFolder, fileName);
            var read = ImageFiles.Read(path);
            CheckRoomFor(read.Content, fileName);
            return AddResource(Path.GetFileNameWithoutExtension(fileName), read.Content, ResourceOrigin.Library(fileName, read.Sha256, read.Length), ResourceColorSpace.Unspecified, new[] { read.Note }, L.Tr("My Library"));
        }

        /// <summary>内蔵の画像を取り込む（参照は鍵と版）。</summary>
        internal ImageResource ImportBuiltInImage(string key)
        {
            RequireNoStrokeForResources();
            if (!BuiltInImages.TryGet(key, out var entry)) throw new ResourceRefusedException(ResourceRefusal.Unknown, "No built-in image \"" + key + "\".");
            CheckResourceRoom((long)entry.Width * entry.Height * 4, L.Tr(entry.Name));
            return AddResource(L.Tr(entry.Name), BuiltInImages.Make(key), ResourceOrigin.BuiltIn(key, entry.Version), entry.ColorSpace, null, L.Tr("Built-in"));
        }

        void CheckRoomFor(ImageContent content, string what)
        {
            HookResources();
            if (resources.FindContent(content.Hash) != null) return;
            CheckResourceRoom(content.ByteSize, what);
        }

        /// <summary>File ▸ Import Image Resource…: 画像のファイルを選んで取り込む。</summary>
        internal void ImportImageResourceDialog()
        {
            string path = Dialogs.OpenFile("Import image resource", Application.dataPath, "png,jpg,jpeg");
            if (string.IsNullOrEmpty(path)) return;
            TryAction(() => { var image = ImportImageFile(path); assetSource = AssetSource.Project; selectedAsset = AssetKey(AssetSource.Project, image.Id.ToString("D")); });
        }

        // ───────── 自分の置き場・名前・消す ─────────

        /// <summary>リソースを自分の置き場へ入れる（PNG。同じバイトのファイルがあればそれを使う）。置き場から取り込んだものの参照は変えない。</summary>
        internal string AddResourceToLibrary(Guid id)
        {
            var image = ImageResources.Get(id);
            string folder = PainterSettings.LibraryFolder;
            string name = ResourceLibraryFolder.Add(folder, image, out bool existed);
            libraryListing = null;
            message = existed ? L.Tr("{0} is already in your library as {1}.", image.Name, name) : L.Tr("Put {0} into your library as {1} ({2}).", image.Name, name, folder);
            Repaint();
            return name;
        }

        internal void RenameResource(Guid id, string name) { RequireNoStrokeForResources(); ImageResources.Rename(id, name); }

        /// <summary>リソースを消す（確かめる。使っているものがあれば断る）。消すと保存した .ylp からも次の保存で無くなる。</summary>
        internal bool RemoveResource(Guid id)
        {
            RequireNoStrokeForResources();
            if (ImageResources.TryGetBrush(id, out var heldBrush))
            {
                var usage = ImageResources.UsageOf(id);
                if (usage != null) throw new ResourceRefusedException(ResourceRefusal.InUse, L.Tr("{0} is used by {1}; it was not removed.", heldBrush.Name, usage));
                if (!Dialogs.Confirm(L.Tr("Remove resource?"), L.Tr("Remove {0} from this project?", heldBrush.Name), L.Tr("Remove"), L.Tr("Cancel"))) return false;
                ImageResources.Remove(id); shelfTipCache.Remove(heldBrush.Hash); selectedAsset = null; return true;
            }
            var image = ImageResources.Get(id);
            var use = ImageResources.UsageOf(id);
            if (use != null) throw new ResourceRefusedException(ResourceRefusal.InUse, L.Tr("{0} is used by {1}; it was not removed.", image.Name, use));
            if (!Dialogs.Confirm(L.Tr("Remove resource?"), L.Tr("Remove {0} from this project? Layers already made from it keep their pixels. Its source (if any) is not touched.", image.Name), L.Tr("Remove"), L.Tr("Cancel"))) return false;
            ImageResources.Remove(id);
            if (selectedAsset == AssetKey(AssetSource.Project, id.ToString("D"))) selectedAsset = null;
            message = L.Tr("Removed {0} from this project.", image.Name);
            return true;
        }

        // ───────── 置く ─────────

        /// <summary>
        /// リソースを今のテクスチャセットの新しいレイヤーとして置く: 選んだ層の上（同じグループ）に、今のチャンネルだけを持つペイントの層を作り、
        /// 画像をセットの大きさに合わせて（UV の 0〜1 に引き伸ばす。縮めるなら面積平均、広げるならバイリニア、同じ大きさならそのまま）入れる。
        /// 1 回の Undo。選択範囲は変えない（範囲に関係なく全体に置く）。層の画素は写しから作るので、後でリソースを更新・削除しても変わらない。
        /// </summary>
        internal Guid PlaceResourceAsLayer(Guid id)
        {
            if (stroke != null) throw new InvalidOperationException(L.Tr("A stroke is in progress."));
            var image = ImageResources.Get(id);
            long bytes = (long)document.Width * document.Height * 4;
            if (bytes > PainterSettings.StrokeBudgetBytes)
                throw new ResourceRefusedException(ResourceRefusal.OverBudget, L.Tr("Placing {0} needs {1} MiB, more than the one-operation budget ({2} MiB, Project Settings > YoluPainter). Nothing was placed.", image.Name, bytes >> 20, PainterSettings.StrokeBudgetBytes >> 20));
            bool sameSize = image.Width == document.Width && image.Height == document.Height;
            var resampling = ImageResampling.Automatic(image.Width, image.Height, document.Width, document.Height);
            var rgba = image.Content.Resampled(document.Width, document.Height, resampling);
            Guid layerId = Guid.Empty; var placeChannel = channel;
            document.Batch(() =>
            {
                var layer = document.AddLayer(image.Name, above: AboveSelected());
                if (placeChannel != PaintChannel.Color) { document.SetChannelEnabled(layer.Id, placeChannel, true); document.SetChannelEnabled(layer.Id, PaintChannel.Color, false); }
                document.ReplacePixels(layer.Id, placeChannel, rgba, withinSelection: false);
                layerId = layer.Id;
            });
            selectedLayer = layerId; editMask = false; repaintPixels = true;
            string how = sameSize ? "" : " " + L.Tr("(resized from {0} × {1}, {2})", image.Width, image.Height, resampling == CanvasResampling.Area ? L.Tr("area average") : L.Tr("bilinear"));
            message = L.Tr("Placed {0} as a new layer in {1}.", image.Name, L.Tr(placeChannel.ToString())) + how;
            Repaint();
            return layerId;
        }

        // ───────── 出どころの確かめ ─────────

        bool resourceChecksStale = true;

        /// <summary>リソースの出どころの状態（確かめていなければ確かめる）。</summary>
        internal ResourceSourceCheck ResourceSource(Guid id)
        {
            if (!resourceChecks.TryGetValue(id, out var check)) { check = CheckResourceSource(ImageResources.Get(id), null); resourceChecks[id] = check; }
            return check;
        }

        /// <summary>出どころが変わって、まだ「写しのまま」を選んでいないリソース。</summary>
        internal IReadOnlyList<ImageResource> ChangedResources
            => ImageResources.Images.Where(r => resourceChecks.TryGetValue(r.Id, out var c) && c.State == ResourceSourceState.Changed
                                           && !(keptResourceCopies.TryGetValue(r.Id, out var kept) && kept == c.Current?.Hash)).ToList();

        /// <summary>全部のリソースの出どころを確かめ直す（印が同じなら前の答えを使う）。変わったリソースを返す。</summary>
        internal IReadOnlyList<ImageResource> CheckResourceSources()
        {
            foreach (var image in ImageResources.Images.ToList())
            {
                resourceChecks.TryGetValue(image.Id, out var previous);
                resourceChecks[image.Id] = CheckResourceSource(image, previous);
            }
            resourceChecksStale = false;
            Repaint();
            return ChangedResources;
        }

        /// <summary>1 つのリソースの出どころを確かめる。印（Unity の依存ハッシュ・ファイルの SHA-256・内蔵の版）が取り込んだときと同じなら
        /// 変わっていない。違えば画素を読み直して中身のハッシュで比べる（インポート設定だけが変わって画素が同じなら変わっていない）。</summary>
        ResourceSourceCheck CheckResourceSource(ImageResource image, ResourceSourceCheck previous)
        {
            var o = image.Origin;
            try
            {
                switch (o.Kind)
                {
                    case ResourceOriginKind.UnityAsset:
                    {
                        string path = AssetDatabase.GUIDToAssetPath(o.AssetGuid);
                        var texture = string.IsNullOrEmpty(path) ? null : UnityResourceObject.Load<Texture2D>(o.AssetGuid, o.LocalFileId);
                        if (texture == null) return new ResourceSourceCheck { State = ResourceSourceState.Missing, Reason = L.Tr("The Unity asset is gone ({0}); the copy in this project is used.", o.Path) };
                        string stamp = UnityTextureReader.Stamp(path);
                        if (stamp == o.SourceStamp) return new ResourceSourceCheck { State = ResourceSourceState.Unchanged, Stamp = stamp, CurrentPath = path };
                        if (previous != null && previous.Stamp == stamp && previous.State != ResourceSourceState.Missing) { previous.CurrentPath = path; return previous; }
                        var read = UnityTextureReader.Read(texture);
                        return Compared(image, read.Content, ResourceOrigin.UnityAsset(o.AssetGuid, path, stamp, read.ThroughGpu, o.LocalFileId), stamp, path);
                    }
                    case ResourceOriginKind.File:
                    case ResourceOriginKind.Library:
                    {
                        string path = o.Kind == ResourceOriginKind.File ? o.Path : ResourceLibraryFolder.Resolve(PainterSettings.LibraryFolder, o.Path);
                        if (!File.Exists(path)) return new ResourceSourceCheck { State = ResourceSourceState.Missing, Reason = L.Tr("The file is gone ({0}); the copy in this project is used.", path) };
                        var info = new FileInfo(path);
                        string quick = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
                        if (previous != null && previous.CurrentPath == path && previous.Quick == quick) return previous;
                        string sha = GenerationStore.Hash(File.ReadAllBytes(path));
                        if (sha == o.SourceStamp) return new ResourceSourceCheck { State = ResourceSourceState.Unchanged, Stamp = sha, CurrentPath = path, Quick = quick };
                        var read = ImageFiles.Read(path);
                        var origin = o.Kind == ResourceOriginKind.File ? ResourceOrigin.File(o.Path, read.Sha256, read.Length) : ResourceOrigin.Library(o.Path, read.Sha256, read.Length);
                        var result = Compared(image, read.Content, origin, read.Sha256, path);
                        result.Quick = quick;
                        return result;
                    }
                    case ResourceOriginKind.BuiltIn:
                    {
                        if (!BuiltInImages.TryGet(o.BuiltInKey, out var entry)) return new ResourceSourceCheck { State = ResourceSourceState.Missing, Reason = L.Tr("This YoluPainter has no built-in image \"{0}\"; the copy in this project is used.", o.BuiltInKey) };
                        string stamp = entry.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (entry.Version == o.BuiltInVersion) return new ResourceSourceCheck { State = ResourceSourceState.Unchanged, Stamp = stamp };
                        if (previous != null && previous.Stamp == stamp) return previous;
                        return Compared(image, BuiltInImages.Make(o.BuiltInKey), ResourceOrigin.BuiltIn(o.BuiltInKey, entry.Version), stamp, null);
                    }
                    default: return new ResourceSourceCheck { State = ResourceSourceState.NotLinked };
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ResourceRefusedException || ex is UnauthorizedAccessException)
            {
                return new ResourceSourceCheck { State = ResourceSourceState.Unreadable, Reason = L.Tr("The source could not be read ({0}); the copy in this project is used.", ex.Message) };
            }
        }

        static ResourceSourceCheck Compared(ImageResource image, ImageContent current, ResourceOrigin origin, string stamp, string path)
            => current.Hash == image.ContentHash
                ? new ResourceSourceCheck { State = ResourceSourceState.Unchanged, Stamp = stamp, CurrentPath = path, CurrentOrigin = origin }
                : new ResourceSourceCheck { State = ResourceSourceState.Changed, Current = current, CurrentOrigin = origin, Stamp = stamp, CurrentPath = path };

        /// <summary>出どころの今の画素で写しを置き換える（同じ ID。使っている段は変わったと知らされる）。出どころが変わっていなければ何もしない。</summary>
        internal bool UpdateResourceFromSource(Guid id)
        {
            RequireNoStrokeForResources();
            var image = ImageResources.Get(id);
            var check = CheckResourceSource(image, resourceChecks.TryGetValue(id, out var previous) ? previous : null);
            resourceChecks[id] = check;
            if (check.State != ResourceSourceState.Changed) { message = L.Tr("{0} matches its source; nothing to update.", image.Name); return false; }
            RefreshArchiveRoom();
            ImageResources.ReplaceContent(id, check.Current, check.CurrentOrigin);
            resourceChecks[id] = new ResourceSourceCheck { State = ResourceSourceState.Unchanged, Stamp = check.Stamp, CurrentPath = check.CurrentPath };
            keptResourceCopies.Remove(id);
            message = L.Tr("Updated {0} from its source ({1} × {2}).", image.Name, image.Width, image.Height);
            return true;
        }

        /// <summary>出どころが変わっても今の写しのまま使う（このセッションでは、出どころがまた変わるまで知らせない）。</summary>
        internal void KeepResourceCopy(Guid id)
        {
            if (resourceChecks.TryGetValue(id, out var check) && check.State == ResourceSourceState.Changed && check.Current != null) keptResourceCopies[id] = check.Current.Hash;
            Repaint();
        }

        /// <summary>開いた・戻したときの確かめ: 出どころが変わったリソースがあれば一覧を見せて、更新するか尋ねる（自動では置き換えない）。
        /// 知らせを返す（無ければ null）。</summary>
        string AskAboutChangedResources()
        {
            var changed = CheckResourceSources();
            var missing = ImageResources.Images.Where(r => resourceChecks.TryGetValue(r.Id, out var c) && c.State == ResourceSourceState.Missing).ToList();
            string note = missing.Count > 0 ? L.Tr("The source of {0} resource(s) is gone ({1}); their copies in this project are used.", missing.Count, string.Join(", ", missing.Take(5).Select(r => r.Name))) : null;
            if (changed.Count == 0) return note;
            string list = string.Join("\n", changed.Take(12).Select(r => "• " + r.Name + " — " + SourceLabel(r))) + (changed.Count > 12 ? "\n…" : "");
            if (Dialogs.Confirm(L.Tr("Resources changed"), L.Tr("The source of these resources changed since they were copied into this project:\n\n{0}\n\nUpdate the copies? Layers already made from them keep their pixels.", list), L.TrIn("assets", "Update"), L.Tr("Keep the copies")))
            {
                int updated = 0;
                foreach (var r in changed) { try { if (UpdateResourceFromSource(r.Id)) updated++; } catch (ResourceRefusedException ex) { note = (note == null ? "" : note + " ") + ex.Message; } }
                return L.Tr("Updated {0} resource(s) from their sources.", updated) + (note != null ? " " + note : "");
            }
            foreach (var r in changed) KeepResourceCopy(r.Id);
            return L.Tr("{0} resource(s) changed in their source; the copies in this project are kept.", changed.Count) + (note != null ? " " + note : "");
        }

        /// <summary>リソースの出どころを人に見せる短い文。</summary>
        internal string SourceLabel(ImageResource image)
        {
            var o = image.Origin;
            switch (o.Kind)
            {
                case ResourceOriginKind.UnityAsset:
                    string now = resourceChecks.TryGetValue(image.Id, out var c) && !string.IsNullOrEmpty(c.CurrentPath) ? c.CurrentPath : o.Path;
                    return L.Tr("Unity: {0}", now) + (o.ReadThroughGpu ? " " + L.Tr("(read through the GPU)") : "");
                case ResourceOriginKind.File: return L.Tr("File: {0}", o.Path);
                case ResourceOriginKind.Library: return L.Tr("My Library: {0}", o.Path);
                case ResourceOriginKind.BuiltIn: return L.Tr("Built-in: {0}", BuiltInImages.TryGet(o.BuiltInKey, out var e) ? L.Tr(e.Name) : o.BuiltInKey);
                default: return L.Tr("Embedded only");
            }
        }
    }
}
