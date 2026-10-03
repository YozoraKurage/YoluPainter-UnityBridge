using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// アセットのパネル（Substance Painter の Assets / シェルフに当たる）: 出どころの切り替え（このプロジェクト・Unity のプロジェクト・自分の置き場・
    /// 内蔵）、名前の検索、種類の絞り込み（画像・スマートマテリアル・スマートマスク。ブラシ・マテリアルも同じ一覧に入る形）、サムネイルの格子、選んだ
    /// ものの説明と操作（置く・取り込む・自分の置き場へ・出どころから更新・消す）。ダブルクリックでも置く・取り込む（スマートマテリアルはいつも置く）。
    /// 格子からキャンバス（2D・3D）へドラッグすると置き、Unity の Project ウィンドウからテクスチャをパネルやキャンバスへ落とすと取り込む（キャンバスなら
    /// 置く）。スマートマテリアルは層の一覧の落とした所へ、スマートマスクは落とした行の層のマスクへも置ける（TexturePaintWindow.LayersPanel.cs）。
    /// 出どころの確かめは、パネルが出たとき・Unity のプロジェクトが変わったとき（パネルが出ていれば）に行う。ドックの別のウィンドウでも同じ描画。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        internal enum AssetSource { Project, Unity, Library, BuiltIn }
        internal enum AssetKind { All, Images, SmartMaterials, SmartMasks }
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
        static string AssetKindName(AssetKind k) => k == AssetKind.Images ? "Images" : k == AssetKind.SmartMaterials ? "Smart Materials" : k == AssetKind.SmartMasks ? "Smart Masks" : "All Kinds";

        /// <summary>格子に並べる 1 つ。</summary>
        sealed class AssetItem
        {
            public string Key, Name, Detail, Badge, BadgeTip; public Color BadgeColor;
            public Func<Texture> Thumbnail;
            /// <summary>画像・スマートマテリアル・スマートマスク。</summary>
            public ResourceKind Kind;
            /// <summary>読めないファイル（自分の置き場の壊れた .ylsmart）。並べて理由を見せるが、置けない。</summary>
            public bool Broken;
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
            // 種類の名前（スマートマテリアル）が入るだけ広げる。検索の欄は案内の文字が入る 124 を残す
            float kindWidth = Mathf.Min(Mathf.Max(row.width * .45f, row.width - 128), PaintGui.TextWidth(kindText, PaintTheme.Label) + 34);
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
                    if (item.Kind != ResourceKind.Image) { var mark = new Rect(thumb.x + 2, thumb.yMax - 16, 14, 14); PaintGui.Rounded(mark, PaintTheme.PanelBg, 3); PaintGui.Icon(mark, item.Kind == ResourceKind.SmartMask ? "vignette" : "layers", PaintTheme.Accent, 11); }
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
            if (assetKind == AssetKind.SmartMaterials || assetKind == AssetKind.SmartMasks)
            {
                bool masks = assetKind == AssetKind.SmartMasks;
                if (searching && assetSource != AssetSource.Unity) return masks ? L.Tr("No smart mask here matches the search.") : L.Tr("No smart material here matches the search.");
                switch (assetSource)
                {
                    case AssetSource.Project: return masks ? L.Tr("This project has no smart masks yet. Save a layer's mask with Layer ▸ Save Mask as Smart Mask, or import one from your library or the built-ins.")
                        : L.Tr("This project has no smart materials yet. Save layers with Layer ▸ Save as Smart Material, or import one from your library or the built-ins.");
                    case AssetSource.Unity: return L.Tr("Smart materials and smart masks are not Unity assets. Look in This Project, My Library or Built-in.");
                    case AssetSource.Library: return masks ? L.Tr("Your library has no smart masks yet ({0}). Saving one puts it there.", PainterSettings.LibraryFolder) : L.Tr("Your library has no smart materials yet ({0}). Saving one puts it there.", PainterSettings.LibraryFolder);
                    default: return masks ? L.Tr("No smart mask here matches the search.") : L.Tr("No smart material here matches the search.");
                }
            }
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
            bool smartKinds = assetKind == AssetKind.SmartMaterials || assetKind == AssetKind.SmartMasks;
            if (item != null ? item.Kind != ResourceKind.Image : smartKinds) { DrawSmartFooter(buttons, item, assetSource, id); return; }
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
            IEnumerable<AssetItem> all = new AssetItem[0];
            if (assetKind == AssetKind.All || assetKind == AssetKind.Images)
            {
                switch (assetSource)
                {
                    case AssetSource.Project: all = ProjectAssetItems(); break;
                    case AssetSource.Unity: all = UnityAssetItems(); break;
                    case AssetSource.Library: all = LibraryAssetItems(); break;
                    default: all = BuiltInAssetItems(); break;
                }
            }
            // 画像の後にスマートマテリアル、スマートマスク
            if (assetKind == AssetKind.All || assetKind == AssetKind.SmartMaterials) all = all.Concat(SmartAssetItems(SmartKind.Material));
            if (assetKind == AssetKind.All || assetKind == AssetKind.SmartMasks) all = all.Concat(SmartAssetItems(SmartKind.Mask));
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

        /// <summary>スマートマテリアル（またはスマートマスク）の項目: このプロジェクトのもの・自分の置き場の .ylsmart（読めないものも理由を付けて）・内蔵。
        /// Unity のアセットには無い。</summary>
        IEnumerable<AssetItem> SmartAssetItems(SmartKind kind)
        {
            var resourceKind = ResourceIndex.KindOf(kind);
            switch (assetSource)
            {
                case AssetSource.Project:
                    foreach (var s in ImageResources.Smart.Where(s => s.Kind == kind))
                    {
                        var held = s;
                        var item = new AssetItem { Key = AssetKey(AssetSource.Project, held.Id.ToString("D")), Name = held.Name, Kind = resourceKind, Detail = SmartDetail(kind, held.Material.LayerCount, held.Material.Width, held.Material.Height, held.Material.Channels) + " · " + SmartSourceLabel(held) };
                        item.Thumbnail = () => CachedThumbnail(item.Key + ":" + held.Hash, () => SmartThumbnail(SmartMaterialFile.ReadInfo(held.FileBytes()).Thumbnail, () => held.Material));
                        yield return item;
                    }
                    break;
                case AssetSource.Library:
                {
                    var imported = new HashSet<string>(ImageResources.Smart.Where(r => r.Origin.Kind == ResourceOriginKind.Library).Select(r => r.Origin.Path), StringComparer.OrdinalIgnoreCase);
                    foreach (var f in SmartLibraryItems())
                    {
                        // 読めないファイルはスマートマテリアルの方に 1 度だけ並べる
                        if (f.Info != null ? f.Info.Kind != kind : kind != SmartKind.Material) continue;
                        var file = f;
                        var item = new AssetItem { Key = AssetKey(AssetSource.Library, file.FileName), Name = file.Info?.Name ?? Path.GetFileNameWithoutExtension(file.FileName), Kind = resourceKind, Broken = file.Info == null };
                        if (file.Info == null)
                        {
                            item.Detail = file.FileName; item.Badge = "warning"; item.BadgeColor = PaintTheme.Warning; item.BadgeTip = L.Tr("This file cannot be used: {0}", file.Problem);
                        }
                        else
                        {
                            item.Detail = SmartDetail(kind, file.Info.LayerCount, file.Info.Width, file.Info.Height, file.Info.Channels) + " · " + file.FileName;
                            item.Thumbnail = () => CachedThumbnail(item.Key + ":" + file.Length + ":" + file.Modified.Ticks, () => SmartThumbnail(file.Info.Thumbnail, () => SmartAsset(item.Key, out _)));
                            if (imported.Contains(file.FileName)) { item.Badge = "check"; item.BadgeColor = PaintTheme.Accent; item.BadgeTip = L.Tr("Already in this project."); }
                        }
                        yield return item;
                    }
                    break;
                }
                case AssetSource.BuiltIn:
                {
                    var imported = new HashSet<string>(ImageResources.Smart.Where(r => r.Origin.Kind == ResourceOriginKind.BuiltIn).Select(r => r.Origin.BuiltInKey));
                    foreach (var e in BuiltInSmartMaterials.All.Where(e => e.Kind == kind))
                    {
                        var entry = e;
                        var item = new AssetItem { Key = AssetKey(AssetSource.BuiltIn, entry.Key), Name = L.Tr(entry.Name), Detail = L.Tr(entry.Description), Kind = resourceKind };
                        item.Thumbnail = () => CachedThumbnail(item.Key + ":" + entry.Version, () => SmartThumbnail(null, () => SmartAsset(item.Key, out _)));
                        if (imported.Contains(entry.Key)) { item.Badge = "check"; item.BadgeColor = PaintTheme.Accent; item.BadgeTip = L.Tr("Already in this project."); }
                        yield return item;
                    }
                    break;
                }
            }
        }

        static string SmartDetail(SmartKind kind, int layers, int width, int height, IReadOnlyList<Core.PaintChannel> channels)
            => (kind == SmartKind.Mask ? L.Tr("Smart mask") : L.Tr("{0} layers", layers)) + " · " + width + " × " + height
               + (channels.Count > 0 ? " · " + string.Join(", ", channels.Select(c => L.Tr(c.ToString()))) : "");

        /// <summary>このプロジェクトのスマートマテリアルの出どころ。</summary>
        string SmartSourceLabel(SmartResource held)
        {
            switch (held.Origin.Kind)
            {
                case ResourceOriginKind.Library: return L.Tr("My Library: {0}", held.Origin.Path);
                case ResourceOriginKind.BuiltIn: return L.Tr("Built-in: {0}", BuiltInSmartMaterials.TryGet(held.Origin.BuiltInKey, out var e) ? L.Tr(e.Name) : held.Origin.BuiltInKey);
                default: return L.Tr("Saved in this project");
            }
        }

        /// <summary>スマートマテリアル・スマートマスクを選んだときのフッター: 置く（マスクは選んだ層のマスクへ）と、出どころごとのアイコン。</summary>
        void DrawSmartFooter(Rect buttons, AssetItem item, AssetSource source, string id)
        {
            bool any = item != null && stroke == null && !item.Broken, mask = item != null ? item.Kind == ResourceKind.SmartMask : assetKind == AssetKind.SmartMasks;
            var icons = source == AssetSource.Project
                ? new[] { ("library", L.Tr("Put into My Library (a .ylsmart file in your library folder)"), "toLibrary"), ("delete", L.Tr("Remove from this project"), "remove") }
                : source == AssetSource.Library
                    ? new[] { ("import", L.Tr("Import into this project (it is then saved with the .ylp)"), "import"), ("folder_open", L.Tr("Open your library folder"), "reveal") }
                    : new[] { ("import", L.Tr("Import into this project (it is then saved with the .ylp)"), "import") };
            float iconsWidth = icons.Length * 28;
            var main = AssetSpot("place", new Rect(buttons.x, buttons.y, buttons.width - iconsWidth - 2, buttons.height));
            string text = mask ? L.Tr("Apply to Mask") : L.TrIn("assets", "Place");
            string tip = mask ? L.Tr("Put it on the selected layer's mask, replacing the mask it has (one undo step)") : L.Tr("New layers above the selected layer, fitted to this texture set (one undo step)");
            if (PaintGui.FitButton(main, text, true, any, tip) && item != null) TryAction(() => PlaceSmartAsset(item.Key));
            float x = main.xMax + 2;
            foreach (var (icon, iconTip, spot) in icons)
            {
                bool enabled = spot == "reveal" || any;
                if (item == null && spot != "reveal") { PaintGui.IconButton(AssetSpot(spot, new Rect(x + 2, buttons.y, 26, buttons.height)), icon, iconTip, false, false, 16); x += 28; continue; }
                if (PaintGui.IconButton(AssetSpot(spot, new Rect(x + 2, buttons.y, 26, buttons.height)), icon, iconTip, false, enabled, 16))
                {
                    if (spot == "toLibrary") TryAction(() => AddSmartToLibrary(Guid.Parse(id)));
                    else if (spot == "remove") TryAction(() => RemoveSmartResource(Guid.Parse(id)));
                    else if (spot == "import") TryAction(() => { var held = ImportSmartAsset(item.Key); });
                    else { Directory.CreateDirectory(PainterSettings.LibraryFolder); EditorUtility.RevealInFinder(PainterSettings.LibraryFolder); }
                }
                x += 28;
            }
        }

        // ───────── 操作 ─────────

        /// <summary>ダブルクリックと主のボタン: このプロジェクトのリソースは置き、ほかは取り込む（今の一覧のまま。取り込んだものには印が付く）。
        /// スマートマテリアル・スマートマスクはどの出どころでも置く（取り込まない。置いた層は写し）。</summary>
        internal void PrimaryAssetAction(string key)
        {
            if (TrySmartKind(key, out _)) { PlaceSmartAsset(key); return; }
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
            unityListingStale = true; libraryListing = null; smartListing = null; librarySmart.Clear(); DisposeAssetThumbnails();
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
                    if (key != null && TrySmartKind(key, out _)) { PlaceSmartAsset(key); return; } // スマートマテリアルは選んだ層の上、スマートマスクは選んだ層のマスクへ
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
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ResourceRefusedException || ex is UnauthorizedAccessException || ex is SmartRefusedException) { texture = null; }
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
