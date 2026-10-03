using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// Reads the pixels of a Texture2D asset in the Unity project without changing the asset, its import settings or the loaded
    /// texture object (nothing is applied, made readable, re-imported or saved).
    /// <list type="bullet">
    /// <item>A texture readable on the CPU (Read/Write on) is read with GetPixels32 (mip 0): the stored values, decoded on the CPU when
    /// the texture is compressed.</item>
    /// <item>Any other texture is drawn into a temporary ARGB32 render texture of the same size (Graphics.Blit, mip 0 at texel
    /// centres) and read back with ReadPixels. The render texture is sRGB exactly when the texture is sampled with sRGB decoding, so
    /// in a linear-colour-space project an sRGB texture is decoded on sampling and encoded again on writing and the bytes come back
    /// as stored; a linear (data) texture is never converted; in a gamma-colour-space project nothing is converted at all. A
    /// compressed texture gives the values the GPU decodes, which are not the source file's.</item>
    /// <item>The GPU path is checked once per session on a small known texture (sRGB and linear); where the readback does not
    /// return those bytes (no graphics device, broken shaders), unreadable textures are refused with the reason instead of read
    /// wrongly.</item>
    /// </list>
    /// Refused (<see cref="ResourceRefusedException"/>): anything but a Texture2D main asset in Assets or Packages (a texture inside
    /// another asset cannot be found again by its GUID alone), larger than
    /// <see cref="ImageContent.MaxSide"/>, or HDR (half/float/BC6H/RGB9e5: reading would clip it to 0–1).
    /// </summary>
    internal static class UnityTextureReader
    {
        internal sealed class Result
        {
            public ImageContent Content;
            public bool ThroughGpu;
            public ResourceColorSpace ColorSpace;
            public readonly List<string> Notes = new List<string>();
        }

        /// <summary>The quick "unchanged since" stamp of an asset: Unity's hash of the asset, its import settings and the importer.</summary>
        internal static string Stamp(string assetPath) => AssetDatabase.GetAssetDependencyHash(assetPath).ToString();

        /// <summary>Why this object cannot be imported as an image resource, or null.</summary>
        internal static string Refusal(UnityEngine.Object asset) => Refusal(asset, out _);
        internal static string Refusal(UnityEngine.Object asset, out ResourceRefusal kind)
        {
            kind = ResourceRefusal.Unsupported;
            if (asset == null) return "Nothing to import.";
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path) || !(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)))
                return "\"" + asset.name + "\" is not an asset in Assets or Packages.";
            if (!(asset is Texture2D texture)) return "\"" + asset.name + "\" is a " + asset.GetType().Name + ", not a 2D texture.";
            if (texture.width > ImageContent.MaxSide || texture.height > ImageContent.MaxSide) { kind = ResourceRefusal.TooLarge; return "\"" + asset.name + "\" is " + texture.width + " × " + texture.height + "; images are at most " + ImageContent.MaxSide + " on a side."; }
            if (IsHdr(texture.format)) return "\"" + asset.name + "\" is an HDR texture (" + texture.format + "); reading it as 8-bit colour would clip its values.";
            return null;
        }

        static bool IsHdr(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.RHalf: case TextureFormat.RGHalf: case TextureFormat.RGBAHalf:
                case TextureFormat.RFloat: case TextureFormat.RGFloat: case TextureFormat.RGBAFloat:
                case TextureFormat.BC6H: case TextureFormat.RGB9e5Float:
                case TextureFormat.ASTC_HDR_4x4: case TextureFormat.ASTC_HDR_5x5: case TextureFormat.ASTC_HDR_6x6: case TextureFormat.ASTC_HDR_8x8: case TextureFormat.ASTC_HDR_10x10: case TextureFormat.ASTC_HDR_12x12:
                    return true;
                default: return false;
            }
        }

        /// <summary>Reads mip 0 of a Texture2D asset (see the class summary). Refusals throw <see cref="ResourceRefusedException"/>.</summary>
        internal static Result Read(Texture2D texture)
        {
            var refusal = Refusal(texture, out var kind);
            if (refusal != null) throw new ResourceRefusedException(kind, refusal);
            string path = AssetDatabase.GetAssetPath(texture);
            var result = new Result();
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            bool srgbSampling = GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
            result.ColorSpace = importer != null ? (importer.sRGBTexture ? ResourceColorSpace.Srgb : ResourceColorSpace.Linear) : srgbSampling ? ResourceColorSpace.Srgb : ResourceColorSpace.Linear;
            int w = texture.width, h = texture.height;
            Color32[] pixels;
            bool normal = importer != null && importer.textureType == TextureImporterType.NormalMap;
            if (normal)
            {
                var shader = Shader.Find("Hidden/YoluPainter/NormalResourceReadback");
                if (!GpuReadbackWorks(out string why) || shader == null || !shader.isSupported || UnityEditor.ShaderUtil.ShaderHasError(shader))
                    throw new ResourceRefusedException(ResourceRefusal.Unsupported, L.Tr("The normal map cannot be decoded through the GPU here: {0}", why));
                var decode = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                try { pixels = ReadThroughGpu(texture, false, decode); }
                finally { UnityEngine.Object.DestroyImmediate(decode); }
                result.ThroughGpu = true; result.ColorSpace = ResourceColorSpace.Linear;
            }
            else if (texture.isReadable) pixels = texture.GetPixels32(0);
            else
            {
                if (!GpuReadbackWorks(out string why)) throw new ResourceRefusedException(ResourceRefusal.Unsupported, "\"" + texture.name + "\" is not readable on the CPU (Read/Write is off in its import settings) and it cannot be read through the GPU here: " + why + " Its import settings were not changed.");
                pixels = ReadThroughGpu(texture, srgbSampling);
                result.ThroughGpu = true;
            }
            if (GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat)) result.Notes.Add("\"" + texture.name + "\" is compressed (" + texture.format + "); the copy holds the values it shows, not the source file's.");
            if (importer != null)
            {
                if (importer.textureType == TextureImporterType.NormalMap) result.Notes.Add(L.Tr("{0} is imported as a normal map; the copy is decoded to unit XYZ normals (RGB, OpenGL orientation, opaque).", texture.name));
                importer.GetSourceTextureWidthAndHeight(out int sw, out int sh);
                if (sw > 0 && sh > 0 && (sw != w || sh != h)) result.Notes.Add("\"" + texture.name + "\" is imported at " + w + " × " + h + " (its source is " + sw + " × " + sh + "); the copy is the imported size.");
            }
            var rgba = new byte[(long)w * h * 4];
            for (int i = 0; i < pixels.Length; i++) { int o = i * 4; var c = pixels[i]; rgba[o] = c.r; rgba[o + 1] = c.g; rgba[o + 2] = c.b; rgba[o + 3] = c.a; }
            result.Content = ImageContent.Adopt(rgba, w, h);
            return result;
        }

        /// <summary>Draws mip 0 into a temporary render texture of the same size and reads it back (see the class summary).</summary>
        static Color32[] ReadThroughGpu(Texture texture, bool srgbSampling, Material decode = null)
        {
            int w = texture.width, h = texture.height;
            var descriptor = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0) { sRGB = srgbSampling, useMipMap = false, autoGenerateMips = false, msaaSamples = 1 };
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(descriptor);
            var read = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            try
            {
                if (decode == null) Graphics.Blit(texture, rt); else Graphics.Blit(texture, rt, decode);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                return read.GetPixels32(0);
            }
            finally
            {
                RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(read);
            }
        }

        static bool? gpuWorks; static string gpuReason;
        /// <summary>Whether the GPU readback returns known bytes (an sRGB and a linear 4 × 4 texture with transparent pixels). Checked
        /// once per session; tests reset it.</summary>
        internal static bool GpuReadbackWorks(out string reason)
        {
            if (gpuWorks == null)
            {
                gpuReason = null;
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) gpuReason = "there is no graphics device.";
                else
                {
                    foreach (bool linear in new[] { false, true })
                    {
                        var probe = new Texture2D(4, 4, TextureFormat.RGBA32, false, linear);
                        try
                        {
                            var known = new Color32[16];
                            for (int i = 0; i < 16; i++) known[i] = new Color32((byte)(i * 16 + 3), (byte)(255 - i * 13), (byte)(i * 7 + 100), (byte)(i % 4 == 0 ? 0 : i * 17));
                            probe.SetPixels32(known); probe.Apply(false, true); // 読めない（CPU の写しを捨てた）テクスチャにする
                            var back = ReadThroughGpu(probe, GraphicsFormatUtility.IsSRGBFormat(probe.graphicsFormat));
                            for (int i = 0; i < 16 && gpuReason == null; i++)
                                if (!back[i].Equals(known[i])) gpuReason = "the GPU readback returned other values (" + (linear ? "linear" : "sRGB") + " texel " + i + ": " + back[i] + " for " + known[i] + ").";
                        }
                        catch (Exception ex) { gpuReason = "the GPU readback failed (" + ex.Message + ")."; }
                        finally { UnityEngine.Object.DestroyImmediate(probe); }
                        if (gpuReason != null) break;
                    }
                }
                gpuWorks = gpuReason == null;
            }
            reason = gpuReason;
            return gpuWorks.Value;
        }
        internal static void ResetGpuCheck() { gpuWorks = null; gpuReason = null; }
    }

    /// <summary>
    /// Reads image files (the library folder and files outside the project). PNG files of the kinds <see cref="RgbaPng"/> reads are
    /// decoded by it (exact, the RGB of transparent pixels kept); other PNG and JPEG files go through Unity's decoder (its values;
    /// a note says so). Files above <see cref="MaxFileBytes"/> are refused before reading.
    /// </summary>
    internal static class ImageFiles
    {
        public const long MaxFileBytes = 512L * 1024 * 1024;
        public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };
        public static bool IsImageFile(string path) => Array.IndexOf(Extensions, Path.GetExtension(path ?? "").ToLowerInvariant()) >= 0;

        internal sealed class Result { public ImageContent Content; public string Sha256; public long Length; public string Note; }

        public static Result Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("The image file is gone: " + path, path);
            if (info.Length > MaxFileBytes) throw new ResourceRefusedException(ResourceRefusal.TooLarge, Path.GetFileName(path) + " is larger than " + (MaxFileBytes >> 20) + " MiB.");
            var bytes = File.ReadAllBytes(path);
            var result = new Result { Sha256 = Core.Persistence.GenerationStore.Hash(bytes), Length = bytes.LongLength };
            if (RgbaPng.LooksLikePng(bytes))
            {
                var (w, h) = RgbaPng.ReadSize(bytes);
                if (w > ImageContent.MaxSide || h > ImageContent.MaxSide) throw new ResourceRefusedException(ResourceRefusal.TooLarge, Path.GetFileName(path) + " is " + w + " × " + h + "; images are at most " + ImageContent.MaxSide + " on a side.");
                try { var (rgba, dw, dh) = RgbaPng.Decode(bytes); result.Content = ImageContent.Adopt(rgba, dw, dh); return result; }
                catch (InvalidDataException) { /* パレット・16 bit・インターレースなど: Unity の復号器で読む */ }
            }
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!texture.LoadImage(bytes, false)) throw new InvalidDataException(Path.GetFileName(path) + " is not a PNG or JPEG image Unity can read.");
                if (texture.width > ImageContent.MaxSide || texture.height > ImageContent.MaxSide) throw new ResourceRefusedException(ResourceRefusal.TooLarge, Path.GetFileName(path) + " is " + texture.width + " × " + texture.height + "; images are at most " + ImageContent.MaxSide + " on a side.");
                var pixels = texture.GetPixels32(0); var rgba = new byte[(long)pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++) { int o = i * 4; rgba[o] = pixels[i].r; rgba[o + 1] = pixels[i].g; rgba[o + 2] = pixels[i].b; rgba[o + 3] = pixels[i].a; }
                result.Content = ImageContent.Adopt(rgba, texture.width, texture.height);
                result.Note = Path.GetFileName(path) + " was decoded by Unity (" + texture.format + ").";
                return result;
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
    }
}
