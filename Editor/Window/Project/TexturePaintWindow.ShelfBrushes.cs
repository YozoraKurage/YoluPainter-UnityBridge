using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        internal sealed partial class BrushState { public int shelfTipSelection; }
        string ShelfBrushUsage(Guid id) => brush.tipId == "shelf:" + id.ToString("D") ? L.Tr("the selected brush") : null;
        readonly Dictionary<string, (BrushTip[] tips, BrushTip texture, BrushTip dual)> shelfTipCache = new Dictionary<string, (BrushTip[], BrushTip, BrushTip)>();
        internal BrushResource SaveShelfBrush(string name = null)
        {
            RequireNoStrokeForResources();
            var files = new Dictionary<string, byte[]>(); var settings = GetBrush();
            var state = ReadBrushState(JsonUtility.ToJson(brush)); state.presetId = ""; state.tipId = state.textureId = state.dualTipId = "";
            state.shelfTipSelection = (int)settings.TipSelection;
            state.presetName = name ?? brush.presetName;
            files[BrushResourceFile.StateName] = Encoding.UTF8.GetBytes(JsonUtility.ToJson(state));
            var tips = settings.Tips ?? (settings.Tip == null ? Array.Empty<BrushTip>() : new[] { settings.Tip });
            for (int i = 0; i < tips.Length; i++) files["tip-" + i + ".png"] = TipPng(tips[i]);
            if (settings.Texture != null) files["texture.png"] = TipPng(settings.Texture);
            if (settings.Dual?.Tip != null) files["dual.png"] = TipPng(settings.Dual.Tip);
            var bytes = BrushResourceFile.Write(files); RefreshArchiveRoom();
            var held = ImageResources.AddBrush(state.presetName, bytes, ResourceOrigin.None, out _);
            assetSource = AssetSource.Project; selectedAsset = AssetKey(assetSource, held.Id.ToString("D")); Repaint(); return held;
        }
        static byte[] TipPng(BrushTip tip)
        {
            var rgba = new byte[tip.Width * tip.Height * 4];
            for (int y = 0; y < tip.Height; y++) for (int x = 0; x < tip.Width; x++) { int i = (y * tip.Width + x) * 4; rgba[i] = rgba[i + 1] = rgba[i + 2] = tip[x, y]; rgba[i + 3] = 255; }
            return RgbaPng.Encode(rgba, tip.Width, tip.Height);
        }
        static BrushTip ReadShelfTip(byte[] png)
        {
            var image = RgbaPng.Decode(png); var alpha = new byte[image.width * image.height];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = image.rgba[i * 4];
            return new BrushTip("Shelf", image.width, image.height, alpha);
        }
        void ApplyShelfBrushImages(BrushSettings settings)
        {
            if (!(brush.tipId ?? "").StartsWith("shelf:", StringComparison.Ordinal)) return;
            if (!Guid.TryParse(brush.tipId.Substring(6), out var id) || !ImageResources.TryGetBrush(id, out var resource)) throw new InvalidDataException("The brush's embedded resource is missing.");
            if (!shelfTipCache.TryGetValue(resource.Hash, out var cached))
            {
                var files = BrushResourceFile.Read(resource.FileBytes());
                var tips = files.Where(f => f.Key.StartsWith("tip-", StringComparison.Ordinal)).OrderBy(f => int.Parse(f.Key.Substring(4, f.Key.Length - 8))).Select(f => ReadShelfTip(f.Value)).ToArray();
                cached = (tips, files.TryGetValue("texture.png", out var texture) ? ReadShelfTip(texture) : null, files.TryGetValue("dual.png", out var dual) ? ReadShelfTip(dual) : null);
                shelfTipCache[resource.Hash] = cached;
            }
            settings.Tip = cached.tips.Length == 1 ? cached.tips[0] : null; settings.Tips = cached.tips.Length > 1 ? cached.tips : null;
            if (!Enum.IsDefined(typeof(TipSelection), brush.shelfTipSelection)) throw new InvalidDataException("Unknown brush tip selection.");
            settings.TipSelection = (TipSelection)brush.shelfTipSelection;
            settings.Texture = cached.texture;
            if (settings.Dual != null) settings.Dual.Tip = cached.dual;
        }
        internal void SelectShelfBrush(Guid id)
        {
            RequireNoStrokeForResources();
            var held = ImageResources.GetBrush(id); var files = BrushResourceFile.Read(held.FileBytes());
            var next = ReadBrushState(Encoding.UTF8.GetString(files[BrushResourceFile.StateName])); next.tipId = "shelf:" + id.ToString("D"); next.presetName = held.Name;
            var old = brush; brush = next;
            try { GetBrush().Validate(); } catch { brush = old; throw; }
            Repaint();
        }
        internal BrushResource ImportShelfBrush(string relative)
        {
            RequireNoStrokeForResources(); string path = ResourceLibraryFolder.Resolve(PainterSettings.LibraryFolder, relative);
            if (new FileInfo(path).Length > YlpArchive.MaxEntryBytes) throw new InvalidDataException("The brush file exceeds the read budget.");
            var bytes = File.ReadAllBytes(path); RefreshArchiveRoom();
            return ImageResources.AddBrush(Path.GetFileNameWithoutExtension(relative), bytes, ResourceOrigin.Library(relative, GenerationStore.Hash(bytes), bytes.LongLength), out _);
        }
        IEnumerable<AssetItem> ShelfBrushItems()
        {
            if (assetSource == AssetSource.Project)
                foreach (var brush in ImageResources.Brushes)
                {
                    var held = brush;
                    yield return new AssetItem { Key = AssetKey(assetSource, held.Id.ToString("D")), Name = held.Name, Kind = ResourceKind.Brush,
                        Thumbnail = () => CachedThumbnail("p:" + held.Id + ":" + held.Hash, () => BrushThumbnail(held.FileBytes())) };
                }
            if (assetSource == AssetSource.Library)
                foreach (var file in ResourceLibraryFolder.ListBrushes(PainterSettings.LibraryFolder))
                {
                    var entry = file;
                    yield return new AssetItem { Key = AssetKey(assetSource, entry.FileName), Name = entry.Name, Detail = entry.FileName, Kind = ResourceKind.Brush,
                        Thumbnail = () => CachedThumbnail("l:" + entry.FileName + ":" + entry.Modified.Ticks, () => BrushThumbnail(ResourceLibraryFolder.ReadFile(entry.Path))) };
                }
            if (assetSource == AssetSource.BuiltIn)
                foreach (var preset in BuiltInBrushes.Presets)
                {
                    var entry = preset;
                    yield return new AssetItem { Key = "b:brush/" + entry.Id, Name = L.Tr(entry.Name), Kind = ResourceKind.Brush,
                        Thumbnail = () => BrushTips.Thumbnail(BrushTips.IdOf(entry.CreateSettings())) };
                }
        }
        static (byte[] rgba, int width, int height) BrushThumbnail(byte[] file)
        {
            var files = BrushResourceFile.Read(file);
            if (files.TryGetValue("tip-0.png", out var png)) return ImageContent.FromPng(png).Preview(64);
            var pixels = new byte[64 * 64 * 4];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) { int i = (y * 64 + x) * 4; pixels[i] = pixels[i + 1] = pixels[i + 2] = 255; pixels[i + 3] = (x - 31.5) * (x - 31.5) + (y - 31.5) * (y - 31.5) < 900 ? (byte)255 : (byte)0; }
            return (pixels, 64, 64);
        }
        bool TryBrushAsset(string key)
        {
            if (!TryParseAssetKey(key, out var source, out string id)) return false;
            return source == AssetSource.Project && Guid.TryParse(id, out var guid) && ImageResources.TryGetBrush(guid, out _)
                || source == AssetSource.Library && id.EndsWith(BrushResourceFile.Extension, StringComparison.OrdinalIgnoreCase)
                || source == AssetSource.BuiltIn && id.StartsWith("brush/", StringComparison.Ordinal);
        }
        void UseBrushAsset(string key)
        {
            if (!TryParseAssetKey(key, out var source, out string id)) return;
            if (source == AssetSource.BuiltIn) { ApplyPreset(BuiltInBrushes.Presets.First(p => p.Id == id.Substring(6))); Repaint(); return; }
            var held = source == AssetSource.Project ? ImageResources.GetBrush(Guid.Parse(id)) : ImportShelfBrush(id);
            SelectShelfBrush(held.Id);
        }
    }
}
