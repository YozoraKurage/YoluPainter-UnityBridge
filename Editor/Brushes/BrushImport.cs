using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Brushes;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>ブラシのファイルを拡張子で読み分ける。読めない形式は理由を添えて断る（黙って丸い筆先にしない）。</summary>
    internal static class BrushImport
    {
        /// <summary>ファイル選択で示す拡張子。</summary>
        public const string Extensions = "abr,pat,gbr,gih,vbr,png";
        /// <summary>これより大きいファイルは読み込む前に断る。市販の .abr の大きいもの（数十 MB）が入る大きさ。</summary>
        public const long MaxFileBytes = 256L * 1024 * 1024;

        public static IReadOnlyList<ImportedBrush> ReadFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new BrushImportException("The file does not exist.");
            if (info.Length > MaxFileBytes) throw new BrushImportException("The file is larger than " + MaxFileBytes / (1024 * 1024) + " MB.");
            string name = PrettyName(info.Name);
            switch (info.Extension.ToLowerInvariant())
            {
                case ".abr": return PhotoshopBrushReader.Read(File.ReadAllBytes(path), name);
                case ".pat": return PhotoshopPatternReader.ReadPatBrushes(File.ReadAllBytes(path));
                case ".gbr": return new[] { GimpBrushReader.ReadGbr(File.ReadAllBytes(path), name) };
                case ".gih": return new[] { GimpBrushReader.ReadGih(File.ReadAllBytes(path), name) };
                case ".vbr": return new[] { GimpBrushReader.ReadVbr(File.ReadAllText(path, Encoding.UTF8), name) };
                case ".png":
                {
                    var tip = TipFromPng(File.ReadAllBytes(path), name);
                    return new[] { new ImportedBrush(name, "PNG tip", new BrushSettings { Tip = tip, Radius = Math.Max(tip.Width, tip.Height) / 2.0, Spacing = .1 }) };
                }
                case ".sut":
                    throw new BrushImportException("Clip Studio Paint brushes (.sut) are not supported: their tips are stored in a protected container. Export the tip as a PNG and import that instead.");
                case ".kpp":
                    throw new BrushImportException("Krita brush presets (.kpp) are not supported; import the tip files (.gbr, .gih or .png) instead.");
                default:
                    throw new BrushImportException("Unsupported brush file type '" + info.Extension + "'. Supported: " + Extensions.Replace(",", ", ") + ".");
            }
        }

        /// <summary>PNG の筆先を Krita / GIMP の約束で読む: 暗いほど塗り、白と透明は塗らない（被覆率 = (1 − 輝度) × アルファ）。</summary>
        public static BrushTip TipFromPng(byte[] png, string name)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!texture.LoadImage(png)) throw new BrushImportException("Not a readable PNG image.");
                if (texture.width > BrushTip.MaxSize || texture.height > BrushTip.MaxSize) throw new BrushImportException("The tip is larger than " + BrushTip.MaxSize + " pixels.");
                var pixels = texture.GetPixels32(); var alpha = new byte[pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i]; int luminance = (p.r * 299 + p.g * 587 + p.b * 114 + 500) / 1000;
                    alpha[i] = (byte)((255 - luminance) * p.a / 255); // Unity のテクスチャは左下原点なので行はそのまま
                }
                return new BrushTip(name, texture.width, texture.height, alpha);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        /// <summary>ファイル名からの表示名（"chalk_grainy-01.gbr" → "Chalk grainy 01"）。</summary>
        public static string PrettyName(string file)
        {
            var stem = Path.GetFileNameWithoutExtension(file).Replace('_', ' ').Replace('-', ' ').Trim();
            return stem.Length == 0 ? file : char.ToUpperInvariant(stem[0]) + stem.Substring(1);
        }
    }
}
