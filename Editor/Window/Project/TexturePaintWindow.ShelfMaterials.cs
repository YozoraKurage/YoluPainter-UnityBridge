using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        static Rgba32 Scalar(float value) { byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255), 0, 255); return new Rgba32(v, v, v, 255); }
        internal SmartResource SaveShelfMaterial(string name = "Material")
        {
            RequireNoStrokeForResources(); var d = new PaintDocument(64, 64, 64, 0);
            var layer = d.AddFillLayer(name, StrokeChannels().ToDictionary(c => c.Channel, c => c.Value));
            return HoldShelfMaterial(d.CaptureSmartMaterial(new[] { layer.Id }, name), ResourceOrigin.None);
        }
        SmartResource HoldShelfMaterial(SmartMaterial material, ResourceOrigin origin)
        {
            var bytes = EncodeSmart(material, YlpContent.Writer, System.Threading.CancellationToken.None); RefreshArchiveRoom();
            return ImageResources.AddSmart(material.Name, bytes, material, origin, out _, resourceKind: ResourceKind.Material);
        }
        internal SmartResource ImportUnityMaterial(Material source)
        {
            RequireNoStrokeForResources();
            string path = source == null ? null : AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(path)) throw new ResourceRefusedException(ResourceRefusal.Unsupported, L.Tr("Not a material asset of the Unity project."));
            var binding = PreviewMaterialBindings.Resolve(source);
            if (!binding.CanShow || binding.Kind != PreviewMaterialKind.Standard && binding.Kind != PreviewMaterialKind.LilToon)
                throw new ResourceRefusedException(ResourceRefusal.Unsupported, L.Tr("This material's channel mapping is not verified: {0}", binding.Unusable ?? binding.Summary));
            var d = new PaintDocument(64, 64, 64, 0); var images = new ProjectResources { BudgetBytes = long.MaxValue, ArchiveBudgetBytes = long.MaxValue }; d.ImageResources = images;
            float Value(string property, float fallback) => source.HasProperty(property) ? source.GetFloat(property) : fallback;
            Color Colour(string property, Color fallback) => source.HasProperty(property) ? source.GetColor(property) : fallback;
            Rgba32 Rgba(Color color) { var c = (Color32)color; return new Rgba32(c.r, c.g, c.b, c.a); }
            var fills = new Dictionary<PaintChannel, Rgba32>();
            foreach (var route in binding.Channels)
            {
                var c = route.Channel;
                fills[c] = c == PaintChannel.Color ? Rgba(Colour("_Color", Color.white)) : c == PaintChannel.Emission ? Rgba(Colour("_EmissionColor", Color.black))
                    : c == PaintChannel.Normal ? new Rgba32(128, 128, 255, 255) : Scalar(c == PaintChannel.Metallic ? Value("_Metallic", 0) : c == PaintChannel.Roughness ? 1 - Value(binding.Kind == PreviewMaterialKind.Standard ? "_Glossiness" : "_Smoothness", .5f) : .5f);
            }
            var layer = d.AddFillLayer(source.name, fills);
            var read = new Dictionary<Texture2D, UnityTextureReader.Result>();
            foreach (var route in binding.Channels)
            {
                var texture = source.GetTexture(route.Property);
                if (texture == null) continue;
                if (!(texture is Texture2D t)) throw new ResourceRefusedException(ResourceRefusal.Unsupported, L.Tr("A material texture is not a 2D image."));
                if (!read.TryGetValue(t, out var result)) { result = UnityTextureReader.Read(t); read.Add(t, result); }
                var pixels = result.Content.CopyPixels(); var c = route.Channel;
                Color tint = c == PaintChannel.Color ? Colour("_Color", Color.white) : c == PaintChannel.Emission ? Colour("_EmissionColor", Color.white) : Color.white;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (c == PaintChannel.Color || c == PaintChannel.Emission)
                    { pixels[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(pixels[i] * tint.r), 0, 255); pixels[i + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(pixels[i + 1] * tint.g), 0, 255); pixels[i + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(pixels[i + 2] * tint.b), 0, 255); pixels[i + 3] = c == PaintChannel.Emission ? (byte)255 : (byte)Mathf.Clamp(Mathf.RoundToInt(pixels[i + 3] * tint.a), 0, 255); }
                    else if (c != PaintChannel.Normal)
                    {
                        float v = (c == PaintChannel.Roughness && route.Packing == PreviewPacking.MetallicSmoothness ? pixels[i + 3] : c == PaintChannel.Height && binding.Kind == PreviewMaterialKind.Standard ? pixels[i + 1] : pixels[i]) / 255f;
                        if (c == PaintChannel.Roughness) v = 1 - v * Value(binding.Kind == PreviewMaterialKind.Standard ? "_GlossMapScale" : "_Smoothness", 1);
                        else if (c == PaintChannel.Metallic && binding.Kind == PreviewMaterialKind.LilToon) v *= Value("_Metallic", 1);
                        var value = Scalar(v); pixels[i] = pixels[i + 1] = pixels[i + 2] = value.R; pixels[i + 3] = 255;
                    }
                }
                var content = ImageContent.Adopt(pixels, result.Content.Width, result.Content.Height);
                var image = images.Add(t.name + " " + c, content, ResourceOrigin.None, c == PaintChannel.Color || c == PaintChannel.Emission ? ResourceColorSpace.Srgb : ResourceColorSpace.Linear, out _);
                d.SetFillImage(layer.Id, c, image.Id);
            }
            var material = d.CaptureSmartMaterial(new[] { layer.Id }, source.name, images);
            var origin = ResourceOrigin.UnityAsset(AssetDatabase.AssetPathToGUID(path), path, AssetDatabase.GetAssetDependencyHash(path).ToString(), false, UnityResourceObject.LocalId(source));
            var held = HoldShelfMaterial(material, origin);
            message = L.Tr("Imported {0} as channel values and image copies. Shader effects, texture transforms and shader-specific normal or height scales are not copied.", source.name);
            Repaint(); return held;
        }
        IEnumerable<AssetItem> MaterialAssetItems()
        {
            if (assetSource == AssetSource.Project)
                foreach (var s in ImageResources.Smart.Where(s => s.ResourceKind == ResourceKind.Material))
                {
                    var held = s;
                    yield return new AssetItem { Key = "p:" + held.Id, Name = held.Name, Kind = ResourceKind.Material,
                        Thumbnail = () => CachedThumbnail("p:" + held.Id + ":" + held.Hash, () => SmartThumbnail((byte[])null, () => held.Material)) };
                }
            if (assetSource == AssetSource.Library)
                foreach (var file in ResourceLibraryFolder.ListMaterials(PainterSettings.LibraryFolder))
                {
                    var entry = file;
                    yield return new AssetItem { Key = "l:" + entry.FileName, Name = entry.Name, Kind = ResourceKind.Material, Detail = entry.FileName,
                        Thumbnail = () => CachedThumbnail("l:" + entry.FileName + ":" + entry.Modified.Ticks, () => SmartThumbnail((byte[])null, () => SmartAsset("l:" + entry.FileName, out _))) };
                }
            if (assetSource == AssetSource.Unity)
                foreach (string guid in AssetDatabase.FindAssets("t:Material"))
                    foreach (var m in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)).OfType<Material>())
                    {
                        var source = m;
                        yield return new AssetItem { Key = "u:" + UnityResourceObject.Key(m), Name = m.name, Kind = ResourceKind.Material, Detail = AssetDatabase.GetAssetPath(m),
                            Thumbnail = () => (Texture)AssetPreview.GetAssetPreview(source) ?? AssetPreview.GetMiniThumbnail(source) };
                    }
        }
        bool TryMaterialAsset(string key) => TryParseAssetKey(key, out var source, out string id) && source == AssetSource.Unity && UnityResourceObject.LoadKey<Material>(id) != null;
        void UseMaterialAsset(string key)
        {
            TryParseAssetKey(key, out _, out string id); var held = ImportUnityMaterial(UnityResourceObject.LoadKey<Material>(id));
            PlaceSmartAsset("p:" + held.Id);
        }
        void DrawBrushFooter(Rect buttons, AssetItem item, string id)
        {
            var main = new Rect(buttons.x, buttons.y, buttons.width - (assetSource == AssetSource.Project ? 30 : 0), buttons.height);
            if (PaintGui.FitButton(AssetSpot("place", main), L.Tr("Use Brush"), true, stroke == null)) TryAction(() => UseBrushAsset(item.Key));
            if (assetSource == AssetSource.Project && PaintGui.IconButton(AssetSpot("toLibrary", new Rect(main.xMax + 2, buttons.y, 26, buttons.height)), "library", L.Tr("Put into My Library"), false, stroke == null, 16))
                TryAction(() => { var held = ImageResources.GetBrush(Guid.Parse(id)); ResourceLibraryFolder.AddFile(PainterSettings.LibraryFolder, held.Name, BrushResourceFile.Extension, held.FileBytes()); RefreshAssetPanel(); });
        }
    }
}
