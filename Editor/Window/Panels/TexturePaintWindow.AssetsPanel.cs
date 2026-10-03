using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// アセットのパネル（Substance Painter の Assets / シェルフに当たる）: 出どころの切り替え（このプロジェクト・Unity のプロジェクト・自分の置き場・
    /// 内蔵）、名前の検索、種類の絞り込み（今は画像だけ。ブラシ・マテリアル・スマートマテリアルが同じ一覧に入る形）、サムネイルの格子、選んだものの
    /// 説明と操作（置く・取り込む・自分の置き場へ・出どころから更新・消す）。ダブルクリックでも置く・取り込む。格子からキャンバス（2D・3D）へ
    /// ドラッグすると置き、Unity の Project ウィンドウからテクスチャをパネルやキャンバスへ落とすと取り込む（キャンバスなら置く）。
    /// 出どころの確かめは、パネルが出たとき・Unity のプロジェクトが変わったとき（パネルが出ていれば）に行う。ドックの別のウィンドウでも同じ描画。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        internal enum AssetSource { Project, Unity, Library, BuiltIn }
        internal enum AssetKind { All, Images }
        static readonly AssetSource[] AssetSources = { AssetSource.Project, AssetSource.Unity, AssetSource.Library, AssetSource.BuiltIn };

        [SerializeField] AssetSource assetSource = AssetSource.Project;
        [SerializeField] AssetKind assetKind = AssetKind.All;
        [SerializeField] string assetSearch = "";
        [SerializeField] string selectedAsset;
        Vector2 assetScroll;
        /// <summary>格子の部品の画面上の矩形（Repaint のたびに覚え直す。テストが本物のマウスの入力で押すため）。</summary>
        internal readonly Dictionary<string, Rect> AssetScreenRects = new Dictionary<string, Rect>();
        const float AssetCell = 76, AssetCellHeight = 92, AssetThumb = 64, AssetGap = 6;
        const string ResourceDragKey = "YoluPainter.Asset";
        string assetDragKey; Vector2 assetDragStart;

        internal AssetSource AssetPanelSource { get => assetSource; set { assetSource = value; assetScroll = Vector2.zero; Repaint(); } }
        internal string AssetPanelSearch { get => assetSearch; set { assetSearch = value ?? ""; assetScroll = Vector2.zero; Repaint(); } }
        internal AssetKind AssetPanelKind { get => assetKind; set { assetKind = value; Repaint(); } }
        internal string SelectedAsset { get => selectedAsset; set { selectedAsset = value; Repaint(); } }

        static string AssetKey(AssetSource source, string id) => (source == AssetSource.Project ? "p:" : source == AssetSource.Unity ? "u:" : source == AssetSource.Library ? "l:" : "b:") + id;
        static bool TryParseAssetKey(string key, out AssetSource source, out string id)
        {
            source = default; id = null;
            if (key == null || key.Length < 3 || key[1] != ':') return false;
            switch (key[0]) { case 'p': source = AssetSource.Project; break; case 'u': source = AssetSource.Unity; break; case 'l': source = AssetSource.Library; break; case 'b': source = AssetSource.BuiltIn; break; default: return false; }
            id = key.Substring(2); return true;
        }

        static string AssetSourceName(AssetSource s)
        {
            switch (s)
            {
                case AssetSource.Project: return "This Project";
                case AssetSource.Unity: return "Unity Project";
                case AssetSource.Library: return "My Library";
                default: return "Built-in";
            }
        }
        static string AssetSourceIcon(AssetSource s) => s == AssetSource.Project ? "folder" : s == AssetSource.Unity ? "view_in_ar" : s == AssetSource.Library ? "library" : "shapes";
        static string AssetSourceTip(AssetSource s)
        {
            switch (s)
            {
                case AssetSource.Project: return "The resources this project holds (copied into the .ylp, shared by all texture sets)";
                case AssetSource.Unity: return "Texture2D assets in this Unity project (Assets and Packages); importing copies them and keeps the reference";
                case AssetSource.Library: return "Your own library folder (Project Settings > YoluPainter), shared between projects that point at it";
                default: return "Images that come with YoluPainter";
            }
        }
        static string AssetKindName(AssetKind k) => k == AssetKind.Images ? "Images" : "All Kinds";

        /// <summary>格子に並べる 1 つ。</summary>
        sealed class AssetItem
        {
            public string Key, Name, Detail, Badge, BadgeTip; public Color BadgeColor;
            public Func<Texture> Thumbnail;
        }

        // ───────── 描く ─────────

        float AssetFooterHeight => 6 + 18 + 4 + 26 + 8;

        void DrawAssetsPanel(Rect r)
        {
            HookResources();
            var e = Event.current;
            if (e.type == EventType.Repaint) AssetScreenRects.Clear();
            HandleAssetPanelDrop(r);
            var rows = new UiRows(r, 6);
            // 出どころ（アイコンの切り替えと今の名前）と、確かめ直す・読み直す
            var row = rows.Row(24, 4);
            float x = row.x;
            foreach (var s in AssetSources)
            {
                var b = AssetSpot("source." + s, new Rect(x, row.y, 26, row.height));
                if (PaintGui.IconButton(b, AssetSourceIcon(s), L.Tr(AssetSourceName(s)) + "\n" + L.Tr(AssetSourceTip(s)), s == assetSource, true, 16)) AssetPanelSource = s;
                x += 28;
            }
            var refresh = AssetSpot("refresh", new Rect(row.xMax - 26, row.y, 26, row.height));
            if (PaintGui.IconButton(refresh, "sync", L.Tr(assetSource == AssetSource.Project ? "Check the sources again" : "Read the list again"), false, stroke == null, 16)) TryAction(RefreshAssetPanel);
            var title = new Rect(x + 4, row.y, refresh.x - x - 8, row.height);
            PaintGui.Text(title, PaintGui.Fit(L.Tr(AssetSourceName(assetSource)), title.width, PaintTheme.LabelBold), PaintTheme.LabelBold);
            // 検索と種類
            row = rows.Row(22, 6);
            string kindText = L.Tr(AssetKindName(assetKind));
            float kindWidth = Mathf.Min(row.width * .45f, PaintGui.TextWidth(kindText, PaintTheme.Label) + 34);
            assetSearch = PaintGui.SearchField(AssetSpot("search", new Rect(row.x, row.y, row.width - kindWidth - 4, row.height)), assetSearch, L.Tr("Search by name"));
            PaintGui.FitDropdown(AssetSpot("kind", new Rect(row.xMax - kindWidth, row.y, kindWidth, row.height)), null, kindText, OpenAssetKindMenu, L.Tr("Show only one kind of asset"));
            // 出どころが変わったリソースの知らせ
            if (assetSource == AssetSource.Project) DrawChangedResourcesNotice(rows);
            var items = AssetItems();
            float top = r.y + rows.Used;
            var grid = new Rect(r.x + PaintTheme.Padding, top, r.width - 2 * PaintTheme.Padding, Mathf.Max(0, r.yMax - AssetFooterHeight - top));
            DrawAssetGrid(grid, items);
            DrawAssetFooter(new Rect(r.x + PaintTheme.Padding, grid.yMax + 6, r.width - 2 * PaintTheme.Padding, AssetFooterHeight - 6), items);
            if (e.type == EventType.Repaint) assetsPanelDrawnFrame = Time.frameCount;
        }
        int assetsPanelDrawnFrame = -1;

        Rect AssetSpot(string id, Rect r)
        {
            if (Event.current.type == EventType.Repaint) AssetScreenRects[id] = GUIUtility.GUIToScreenRect(r);
            return r;
        }

        void OpenAssetKindMenu(Rect at)
        {
            var menu = new GenericMenu();
            foreach (AssetKind k in Enum.GetValues(typeof(AssetKind))) { var kind = k; menu.AddItem(new GUIContent(L.Tr(AssetKindName(k))), k == assetKind, () => AssetPanelKind = kind); }
            menu.DropDown(at);
        }

        void DrawChangedResourcesNotice(UiRows rows)
        {
            var changed = ChangedResources;
            if (changed.Count == 0) return;
            PaintGui.Notice(rows, changed.Count == 1 ? L.Tr("The source of {0} changed since it was copied into this project.", changed[0].Name) : L.Tr("The source of {0} resources changed since they were copied into this project.", changed.Count), "warning", PaintTheme.Warning);
            var cells = UiRows.Split(rows.Row(24, 6), 2, 6);
            if (PaintGui.FitButton(AssetSpot("notice.update", cells[0]), L.TrIn("assets", "Update"), true, stroke == null, L.Tr("Replace the copies with the sources' pixels (the same resources; layers already made keep their pixels)")))
                TryAction(() => { foreach (var r in changed) UpdateResourceFromSource(r.Id); });
            if (PaintGui.FitButton(AssetSpot("notice.keep", cells[1]), L.Tr("Keep the copies"), false, true, L.Tr("Go on using the copies; you are told again when the sources change again")))
                foreach (var r in changed) KeepResourceCopy(r.Id);
        }

        void DrawAssetGrid(Rect grid, List<AssetItem> items)
        {
            var e = Event.current;
            PaintGui.Rounded(grid, PaintTheme.MenuBg, 4);
            if (items.Count == 0)
            {
                var lines = PaintGui.WrapLines(AssetEmptyText(), grid.width - 16, PaintTheme.Wrap);
                float lh = PaintTheme.Wrap.lineHeight + 2, y = grid.y + 10;
                foreach (var line in lines) { PaintGui.Text(new Rect(grid.x + 8, y, grid.width - 16, lh), line, PaintTheme.Wrap); y += lh; }
                return;
            }
            int columns = Mathf.Max(1, Mathf.FloorToInt((grid.width - AssetGap + AssetGap) / (AssetCell + AssetGap)));
            float cellWidth = (grid.width - AssetGap * (columns + 1)) / columns;
            int rowsCount = (items.Count + columns - 1) / columns;
            float content = AssetGap + rowsCount * (AssetCellHeight + AssetGap);
            assetScroll.y = Mathf.Clamp(assetScroll.y, 0, Mathf.Max(0, content - grid.height));
            if (e.type == EventType.ScrollWheel && grid.Contains(e.mousePosition)) { assetScroll.y = Mathf.Clamp(assetScroll.y + e.delta.y * 20, 0, Mathf.Max(0, content - grid.height)); e.Use(); Repaint(); }
            int newThumbs = 0;
            PaintGui.BeginScroll(grid, assetScroll);
            var view = new Rect(0, assetScroll.y, grid.width, grid.height);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                int cx = i % columns, cy = i / columns;
                var cell = new Rect(AssetGap + cx * (cellWidth + AssetGap), AssetGap + cy * (AssetCellHeight + AssetGap), cellWidth, AssetCellHeight);
                if (!cell.Overlaps(view)) continue;
                bool selected = item.Key == selectedAsset, hover = GUI.enabled && cell.Contains(e.mousePosition) && view.Contains(e.mousePosition);
                if (selected) PaintGui.Rounded(cell, PaintTheme.AccentDim, 4); else if (hover) PaintGui.Rounded(cell, PaintTheme.ControlHover, 4);
                var thumb = new Rect(cell.x + (cell.width - AssetThumb) / 2, cell.y + 4, AssetThumb, AssetThumb);
                if (e.type == EventType.Repaint)
                {
                    PaintGui.Checker(thumb, 6);
                    bool cached = HasCachedThumbnail(item.Key);
                    Texture texture = item.Thumbnail != null && (cached || newThumbs < 3) ? item.Thumbnail() : null;
                    if (!cached) newThumbs++;
                    if (texture != null) GUI.DrawTexture(thumb, texture, ScaleMode.ScaleToFit, true);
                    PaintGui.Outline(thumb, PaintTheme.Border, 1, 0);
                    if (item.Badge != null) { var badge = new Rect(thumb.xMax - 16, thumb.y + 2, 14, 14); PaintGui.Rounded(badge, PaintTheme.PanelBg, 7); PaintGui.Icon(badge, item.Badge, item.BadgeColor, 11); }
                }
                var label = new Rect(cell.x + 3, thumb.yMax + 2, cell.width - 6, AssetCellHeight - AssetThumb - 8);
                PaintGui.Text(label, PaintGui.Fit(item.Name, label.width, PaintTheme.LabelSmall, false), new GUIStyle(PaintTheme.LabelSmall) { alignment = TextAnchor.UpperCenter }, selected ? Color.white : PaintTheme.Text);
                PaintGui.Tooltip(cell, item.Name + (string.IsNullOrEmpty(item.Detail) ? "" : "\n" + item.Detail) + (item.BadgeTip != null ? "\n" + item.BadgeTip : ""));
                if (e.type == EventType.Repaint) AssetScreenRects["cell." + item.Key] = GUIUtility.GUIToScreenRect(cell);
                if (e.type == EventType.MouseDown && e.button == 0 && hover)
                {
                    selectedAsset = item.Key; assetDragKey = item.Key; assetDragStart = e.mousePosition;
                    if (e.clickCount == 2) { assetDragKey = null; TryAction(() => PrimaryAssetAction(item.Key)); }
                    e.Use(); Repaint();
                }
            }
            if (e.type == EventType.MouseDrag && assetDragKey != null && (e.mousePosition - assetDragStart).sqrMagnitude > 36) { StartAssetDrag(assetDragKey); assetDragKey = null; e.Use(); }
            if (e.type == EventType.MouseUp) assetDragKey = null;
            PaintGui.EndScroll();
            if (newThumbs > 3) Repaint(); // 残りのサムネイルは次の描画で
            if (assetSource == AssetSource.Unity && AssetPreview.IsLoadingAssetPreviews()) Repaint();
        }

        string AssetEmptyText()
        {
            bool searching = !string.IsNullOrWhiteSpace(assetSearch);
            switch (assetSource)
            {
                case AssetSource.Project: return searching ? L.Tr("No resource in this project matches the search.") : L.Tr("This project has no resources yet. Import images from the Unity project, your library or the built-ins, or drop textures from the Project window here.");
                case AssetSource.Unity: return searching ? L.Tr("No texture in the Unity project matches the search.") : L.Tr("The Unity project has no Texture2D assets.");
                case AssetSource.Library: return searching ? L.Tr("No image in your library matches the search.") : L.Tr("Your library is empty ({0}). Put resources into it from This Project, or copy PNG and JPEG files into the folder.", PainterSettings.LibraryFolder);
                default: return L.Tr("No built-in image matches the search.");
            }
        }

        void DrawAssetFooter(Rect r, List<AssetItem> items)
        {
            var item = items.FirstOrDefault(i => i.Key == selectedAsset);
            var info = new Rect(r.x, r.y, r.width, 18);
            if (item == null) PaintGui.Text(info, PaintGui.Fit(L.Tr("Select an asset to see what you can do with it."), info.width, PaintTheme.LabelDim), PaintTheme.LabelDim);
            else
            {
                string text = item.Name + (string.IsNullOrEmpty(item.Detail) ? "" : " · " + item.Detail);
                PaintGui.Text(info, PaintGui.Fit(text, info.width, PaintTheme.LabelDim, false), PaintTheme.LabelDim);
                PaintGui.Tooltip(info, text + (item.BadgeTip != null ? "\n" + item.BadgeTip : ""));
            }
            var buttons = new Rect(r.x, info.yMax + 4, r.width, 26);
            bool any = item != null && stroke == null;
            TryParseAssetKey(item?.Key, out var source, out string id);
            if (assetSource == AssetSource.Project)
            {
                var icons = new[] { ("library", L.Tr("Put into My Library (a PNG file in your library folder)"), "toLibrary"), ("sync", L.Tr("Update from the source (when it changed)"), "update"), ("delete", L.Tr("Remove from this project"), "remove") };
                float iconsWidth = icons.Length * 28;
                var main = AssetSpot("place", new Rect(buttons.x, buttons.y, buttons.width - iconsWidth - 2, buttons.height));
                if (PaintGui.FitButton(main, L.Tr("Place as Layer"), true, any, L.Tr("A new layer above the selected one, in the current channel, with the image fitted to the texture set (one undo step)")))
                    TryAction(() => PlaceResourceAsLayer(Guid.Parse(id)));
                float x = main.xMax + 2;
                foreach (var (icon, tip, spot) in icons)
                {
                    bool enabled = any && (spot != "update" || ChangedResources.Any(c => c.Id.ToString("D") == id));
                    if (PaintGui.IconButton(AssetSpot(spot, new Rect(x + 2, buttons.y, 26, buttons.height)), icon, tip, false, enabled, 16))
                    {
                        var target = Guid.Parse(id);
                        if (spot == "toLibrary") TryAction(() => AddResourceToLibrary(target));
                        else if (spot == "update") TryAction(() => UpdateResourceFromSource(target));
                        else TryAction(() => RemoveResource(target));
                    }
                    x += 28;
                }
                return;
            }
            string extraIcon = assetSource == AssetSource.Unity ? "target" : assetSource == AssetSource.Library ? "folder_open" : null;
            string extraTip = assetSource == AssetSource.Unity ? L.Tr("Show it in the Project window") : L.Tr("Open your library folder");
            var import = AssetSpot("import", new Rect(buttons.x, buttons.y, buttons.width - (extraIcon != null ? 30 : 0), buttons.height));
            if (PaintGui.FitButton(import, L.Tr("Import into Project"), true, any, L.Tr("Copy it into this project as a resource (the reference to its source is kept)")))
                TryAction(() => PrimaryAssetAction(item.Key));
            if (extraIcon != null && PaintGui.IconButton(AssetSpot("reveal", new Rect(import.xMax + 4, buttons.y, 26, buttons.height)), extraIcon, extraTip, false, assetSource == AssetSource.Library || item != null, 16))
            {
                if (assetSource == AssetSource.Unity) { var o = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(id)); if (o != null) EditorGUIUtility.PingObject(o); }
                else { Directory.CreateDirectory(PainterSettings.LibraryFolder); EditorUtility.RevealInFinder(PainterSettings.LibraryFolder); }
            }
        }

        // ───────── 一覧 ─────────

        List<AssetItem> AssetItems()
        {
            if (assetKind != AssetKind.All && assetKind != AssetKind.Images) return new List<AssetItem>();
            IEnumerable<AssetItem> all;
            switch (assetSource)
            {
                case AssetSource.Project: all = ProjectAssetItems(); break;
                case AssetSource.Unity: all = UnityAssetItems(); break;
                case AssetSource.Library: all = LibraryAssetItems(); break;
                default: all = BuiltInAssetItems(); break;
            }
            string search = (assetSearch ?? "").Trim();
            return all.Where(i => search.Length == 0 || i.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        IEnumerable<AssetItem> ProjectAssetItems()
        {
            foreach (var r in resources.Images)
            {
                var image = r;
                resourceChecks.TryGetValue(image.Id, out var check);
                var item = new AssetItem { Key = AssetKey(AssetSource.Project, image.Id.ToString("D")), Name = image.Name, Detail = image.Width + " × " + image.Height + " · " + SourceLabel(image) };
                item.Thumbnail = () => CachedThumbnail(item.Key + ":" + image.ContentHash, () => image.Content.Preview((int)AssetThumb));
                if (check != null)
                {
                    bool kept = keptResourceCopies.TryGetValue(image.Id, out var keptHash) && keptHash == check.Current?.Hash;
                    if (check.State == ResourceSourceState.Changed) { item.Badge = "sync"; item.BadgeColor = kept ? PaintTheme.TextDim : PaintTheme.Warning; item.BadgeTip = kept ? L.Tr("The source changed; you chose to keep the copy.") : L.Tr("The source changed since it was copied. Update to take the new pixels."); }
                    else if (check.State == ResourceSourceState.Missing || check.State == ResourceSourceState.Unreadable) { item.Badge = "link_off"; item.BadgeColor = PaintTheme.TextDim; item.BadgeTip = check.Reason; }
                }
                yield return item;
            }
        }

        List<string> unityTextureGuids; bool unityListingStale = true; int unityListingTotal;
        const int MaxUnityListing = 500;
        IEnumerable<AssetItem> UnityAssetItems()
        {
            if (unityTextureGuids == null || unityListingStale)
            {
                var guids = AssetDatabase.FindAssets("t:Texture2D").Distinct().Select(g => (g, path: AssetDatabase.GUIDToAssetPath(g)))
                    .Where(t => t.path.StartsWith("Assets/", StringComparison.Ordinal) || t.path.StartsWith("Packages/", StringComparison.Ordinal))
                    .OrderBy(t => t.path, StringComparer.OrdinalIgnoreCase).Select(t => t.g).ToList();
                unityListingTotal = guids.Count; unityTextureGuids = guids; unityListingStale = false;
            }
            var imported = new HashSet<string>(resources.Images.Where(r => r.Origin.Kind == ResourceOriginKind.UnityAsset).Select(r => r.Origin.AssetGuid));
            int shown = 0; string search = (assetSearch ?? "").Trim();
            foreach (var guid in unityTextureGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                string name = Path.GetFileNameWithoutExtension(path);
                if (search.Length > 0 && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++shown > MaxUnityListing) yield break;
                var item = new AssetItem { Key = AssetKey(AssetSource.Unity, guid), Name = name, Detail = path };
                string p = path;
                item.Thumbnail = () => { var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p); return t == null ? null : (Texture)AssetPreview.GetAssetPreview(t) ?? AssetPreview.GetMiniThumbnail(t); };
                if (imported.Contains(guid)) { item.Badge = "check"; item.BadgeColor = PaintTheme.Accent; item.BadgeTip = L.Tr("Already in this project."); }
                yield return item;
            }
        }

        List<ResourceLibraryFolder.Item> libraryListing; string libraryListingFolder;
        IEnumerable<AssetItem> LibraryAssetItems()
        {
            string folder = PainterSettings.LibraryFolder;
            if (libraryListing == null || libraryListingFolder != folder) { libraryListing = ResourceLibraryFolder.List(folder); libraryListingFolder = folder; }
            var imported = new HashSet<string>(resources.Images.Where(r => r.Origin.Kind == ResourceOriginKind.Library).Select(r => r.Origin.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in libraryListing)
            {
                var file = entry;
                var item = new AssetItem { Key = AssetKey(AssetSource.Library, file.FileName), Name = file.Name, Detail = file.FileName + " · " + (file.Length >= 1 << 20 ? (file.Length >> 20) + " MiB" : (file.Length >> 10) + " KiB") };
                item.Thumbnail = () => CachedThumbnail(item.Key + ":" + file.Length + ":" + file.Modified.Ticks, () => ImageFiles.Read(file.Path).Content.Preview((int)AssetThumb));
                if (imported.Contains(file.FileName)) { item.Badge = "check"; item.BadgeColor = PaintTheme.Accent; item.BadgeTip = L.Tr("Already in this project."); }
                yield return item;
            }
        }

        IEnumerable<AssetItem> BuiltInAssetItems()
        {
            var imported = new HashSet<string>(resources.Images.Where(r => r.Origin.Kind == ResourceOriginKind.BuiltIn).Select(r => r.Origin.BuiltInKey));
            foreach (var e in BuiltInImages.All)
            {
                var entry = e;
                var item = new AssetItem { Key = AssetKey(AssetSource.BuiltIn, entry.Key), Name = L.Tr(entry.Name), Detail = entry.Width + " × " + entry.Height };
                item.Thumbnail = () => CachedThumbnail(item.Key + ":" + entry.Version, () => BuiltInImages.Make(entry.Key).Preview((int)AssetThumb));
                if (imported.Contains(entry.Key)) { item.Badge = "check"; item.BadgeColor = PaintTheme.Accent; item.BadgeTip = L.Tr("Already in this project."); }
                yield return item;
            }
        }

        // ───────── 操作 ─────────

        /// <summary>ダブルクリックと主のボタン: このプロジェクトのリソースは置き、ほかは取り込む（今の一覧のまま。取り込んだものには印が付く）。</summary>
        internal void PrimaryAssetAction(string key)
        {
            if (!TryParseAssetKey(key, out var source, out string id)) return;
            if (source == AssetSource.Project) { PlaceResourceAsLayer(Guid.Parse(id)); return; }
            ImportAsset(source, id);
        }

        ImageResource ImportAsset(AssetSource source, string id)
        {
            switch (source)
            {
                case AssetSource.Unity:
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(id));
                    if (texture == null) throw new ResourceRefusedException(ResourceRefusal.Unknown, L.Tr("The texture is no longer in the Unity project."));
                    return ImportUnityTexture(texture);
                case AssetSource.Library: return ImportLibraryImage(id);
                case AssetSource.BuiltIn: return ImportBuiltInImage(id);
                default: return ImageResources.Get(Guid.Parse(id));
            }
        }

        void RefreshAssetPanel()
        {
            unityListingStale = true; libraryListing = null; DisposeAssetThumbnails();
            if (assetSource == AssetSource.Project)
            {
                var changed = CheckResourceSources();
                message = changed.Count == 0 ? L.Tr("Every resource matches its source.") : L.Tr("The source of {0} resource(s) changed.", changed.Count);
            }
            Repaint();
        }

        /// <summary>パネルが出た（畳んだ・隠した状態から）・Unity のプロジェクトが変わったときに、出どころを確かめる（描画の外で）。</summary>
        bool assetsPanelWasShown;
        void TickAssetsPanel()
        {
            bool shown = dockLayout != null && dockLayout.groups.Any(g => g.Active == "assets" && !g.collapsed);
            if (shown && (!assetsPanelWasShown || resourceChecksStale) && stroke == null && resources.Count > 0)
            {
                resourceChecksStale = false;
                try { CheckResourceSources(); } catch (Exception ex) { Debug.LogWarning("Texture Painter: " + ex.Message); }
            }
            assetsPanelWasShown = shown;
        }
        void OnUnityProjectChanged() { unityListingStale = true; resourceChecksStale = true; Repaint(); }

        // ───────── ドラッグ ─────────

        void StartAssetDrag(string key)
        {
            if (!TryParseAssetKey(key, out var source, out string id)) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.SetGenericData(ResourceDragKey, key);
            if (source == AssetSource.Unity) { var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(id)); DragAndDrop.objectReferences = texture != null ? new UnityEngine.Object[] { texture } : new UnityEngine.Object[0]; }
            else DragAndDrop.objectReferences = new UnityEngine.Object[0];
            DragAndDrop.StartDrag(key);
        }

        /// <summary>Unity の Project ウィンドウからパネルへ落としたテクスチャを取り込む。</summary>
        void HandleAssetPanelDrop(Rect r)
        {
            var e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !r.Contains(e.mousePosition) || DragAndDrop.GetGenericData(ResourceDragKey) != null) return;
            var textures = DragAndDrop.objectReferences.OfType<Texture2D>().Where(t => !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(t))).ToList();
            if (textures.Count == 0) return;
            DragAndDrop.visualMode = stroke == null ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (e.type == EventType.DragPerform && stroke == null)
            {
                DragAndDrop.AcceptDrag();
                TryAction(() => { ImageResource last = null; foreach (var t in textures) last = ImportUnityTexture(t); assetSource = AssetSource.Project; if (last != null) selectedAsset = AssetKey(AssetSource.Project, last.Id.ToString("D")); });
            }
            e.Use();
        }

        /// <summary>キャンバス（2D・3D の表示域）へ落としたもの: パネルの格子からのアセットは置く（このプロジェクトに無ければ取り込んでから）、
        /// Unity の Project ウィンドウからのテクスチャは取り込んで置く。</summary>
        bool HandleResourceDrop(Event e)
        {
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return false;
            bool over = canvasRect.Contains(e.mousePosition) || surfaceRect.Contains(e.mousePosition);
            if (!over) return false;
            string key = DragAndDrop.GetGenericData(ResourceDragKey) as string;
            var textures = key == null ? DragAndDrop.objectReferences.OfType<Texture2D>().Where(t => !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(t))).ToList() : null;
            if (key == null && (textures == null || textures.Count == 0)) return false;
            DragAndDrop.visualMode = stroke == null ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (e.type == EventType.DragPerform && stroke == null)
            {
                DragAndDrop.AcceptDrag();
                TryAction(() =>
                {
                    if (key != null && TryParseAssetKey(key, out var source, out string id))
                    {
                        var image = ImportAsset(source, id);
                        PlaceResourceAsLayer(image.Id);
                        selectedAsset = AssetKey(AssetSource.Project, image.Id.ToString("D"));
                    }
                    else foreach (var t in textures) { var image = ImportUnityTexture(t); PlaceResourceAsLayer(image.Id); }
                });
            }
            e.Use();
            return true;
        }

        // ───────── サムネイル ─────────

        readonly Dictionary<string, Texture2D> assetThumbnails = new Dictionary<string, Texture2D>();
        /// <summary>サムネイルが作ってあるか（Unity のアセットは Unity の AssetPreview が非同期に作るので、いつも作ってあるものとして数える）。</summary>
        bool HasCachedThumbnail(string itemKey) => itemKey.StartsWith("u:", StringComparison.Ordinal) || assetThumbnails.Keys.Any(k => k.StartsWith(itemKey + ":", StringComparison.Ordinal));

        Texture2D CachedThumbnail(string key, Func<(byte[] rgba, int width, int height)> make)
        {
            if (assetThumbnails.TryGetValue(key, out var cached)) return cached; // 作れなかったもの（null）も覚えて、作り直さない
            if (assetThumbnails.Count > 300) DisposeAssetThumbnails();
            Texture2D texture = null;
            try
            {
                var (rgba, w, h) = make();
                texture = new Texture2D(w, h, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                texture.LoadRawTextureData(rgba); texture.Apply(false, false);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ResourceRefusedException || ex is UnauthorizedAccessException) { texture = null; }
            assetThumbnails[key] = texture;
            return texture;
        }

        void DisposeAssetThumbnails()
        {
            foreach (var t in assetThumbnails.Values) if (t != null) DestroyImmediate(t);
            assetThumbnails.Clear();
        }
    }
}
