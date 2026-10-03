using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.Callbacks;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>.ylp を Unity のアセットとして読み込む。主オブジェクトは <see cref="YlpImportInfo"/>（大きさ・チャンネル・エラー）だけで、
    /// テクスチャは作らない。
    /// <list type="bullet">
    /// <item>.ylp は YoluPainter の作業ファイルで、配布した先に YoluPainter があるとは限らない。インポーターが無い環境では .ylp は
    /// ただのファイルになり、そこから作ったテクスチャを参照するマテリアルは参照が切れる。なのでマテリアルのテクスチャ欄に入れられる
    /// もの（Texture2D のサブアセット）を出さない。マテリアルには Export Images・lilToon への割り当てで書き出した PNG を使う。</item>
    /// <item>Project ウィンドウのサムネイルとプレビューは <see cref="YlpImportInfoEditor"/> が .ylp の thumbnail.png から描く
    /// （アセットには入れない）。</item>
    /// <item>大きさとチャンネルは、テクスチャセットごとに合成済みの画像の名前と PNG の頭（IHDR）から読む。画像が無いときだけそのセットの正本
    /// （document.utpaint）を読む。セットの並びは project.json（形式 3 から。形式 2 までは根の合成の 1 つのセット）。主オブジェクトの大きさと
    /// チャンネルは保存したときの今のセットのもの。</item>
    /// <item>読めないファイルは理由をインポートのエラーに出し、<see cref="YlpImportInfo.error"/> に残す（アセットは消えない）。
    /// .ylp そのものには一切書かない。ダブルクリックで YoluPainter で開く。</item>
    /// </list></summary>
    [ScriptedImporter(4, "ylp")]
    internal sealed class YlpImporter : ScriptedImporter
    {
        /// <summary>主オブジェクトの識別子。</summary>
        public const string InfoIdentifier = "ImportInfo";
        /// <summary>Unity のテクスチャの 1 辺の上限（書き出す PNG もこれを超えられない）。</summary>
        public const int MaxTextureSize = 16384;

        static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>取り込んだ結果。まだ取り込まれていないアセットでは null。</summary>
        internal static YlpImportInfo LoadInfo(string assetPath) => AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<YlpImportInfo>().FirstOrDefault();

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var info = ScriptableObject.CreateInstance<YlpImportInfo>();
            info.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            try
            {
                var warnings = new List<string>();
                Describe(File.ReadAllBytes(FileUtil.GetPhysicalPath(ctx.assetPath)), info, warnings);
                foreach (var warning in warnings) ctx.LogImportWarning(warning);
            }
            catch (Exception ex)
            {
                string reason = ex is InvalidDataException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                ctx.LogImportError("YoluPainter could not import " + ctx.assetPath + ": " + reason);
                info.width = info.height = 0; info.channels = new PaintChannel[0]; info.fromNativeDocument = false; info.textureSets = new YlpImportInfo.TextureSetSummary[0]; info.error = reason;
            }
            ctx.AddObjectToAsset(InfoIdentifier, info);
            ctx.SetMainObject(info);
        }

        /// <summary>テクスチャセットごとの大きさとチャンネル。合成済みの画像があればその名前と IHDR から（画素は展開しない）、無い・使えないときは
        /// 正本から（使えなかった理由は <paramref name="warnings"/> に足す）。</summary>
        static void Describe(byte[] data, YlpImportInfo info, List<string> warnings)
        {
            // 形式を先に確かめる（新しすぎる形式は、中身の並びが違うかもしれないので読まずに断る）。合成の画像の置き場は形式で決まる
            // （形式 1・2 は根の composite/、形式 3 からはセットごとの sets/<ID>/composite/）。
            var head = YlpArchive.Read(data, entry => entry == YlpFormat.InfoName || entry == YlpFormat.ProjectName || entry == YlpFormat.ViewName);
            var format = head.TryGetValue(YlpFormat.InfoName, out var record) ? YlpFormat.ReadInfo(record) : new YlpFormatInfo(1, null, null);
            YlpFormat.CheckReadable(format);
            info.format = format.Format; info.savedBy = format.SavedBy?.ToString() ?? ""; info.createdBy = format.CreatedBy?.ToString() ?? "";
            var sets = new List<YlpImportInfo.TextureSetSummary>();
            if (format.Format >= 3)
            {
                if (!head.TryGetValue(YlpFormat.ProjectName, out var projectBytes)) throw new InvalidDataException("The file has no " + YlpFormat.ProjectName + " (its list of texture sets).");
                var project = YlpFormat.ReadProject(projectBytes);
                var composites = YlpArchive.Read(data, entry => YlpFormat.TrySplitSetEntry(entry, out _, out var leaf) && YlpContent.TryParseComposite(leaf, out _));
                foreach (var set in project.Sets)
                {
                    string folder = YlpFormat.SetFolder(set.Id);
                    var mine = composites.Where(c => c.Key.StartsWith(folder, StringComparison.Ordinal)).ToDictionary(c => c.Key.Substring(folder.Length), c => c.Value, StringComparer.Ordinal);
                    sets.Add(DescribeSet(data, mine, YlpFormat.SetEntry(set.Id, YlpArchive.NativeName), set.Name, set.MaterialSlot, set.Id == project.CurrentSet, project.Sets.Count > 1, warnings));
                }
            }
            else
            {
                int slot = 0;
                if (head.TryGetValue(YlpFormat.ViewName, out var view))
                {
                    try { slot = YlpFormat.LegacyMaterialSlot(view); }
                    catch (InvalidDataException ex) { warnings.Add("The material slot in view.json could not be read (" + ex.Message + "); slot 0 is shown."); }
                }
                var composites = YlpArchive.Read(data, entry => YlpContent.TryParseComposite(entry, out _));
                sets.Add(DescribeSet(data, composites, YlpArchive.NativeName, YlpFormat.MigratedSetName, slot, true, false, warnings));
            }
            info.textureSets = sets.ToArray();
            var current = sets.First(s => s.current);
            info.width = current.width; info.height = current.height; info.channels = current.channels; info.fromNativeDocument = current.fromNativeDocument; info.error = "";
        }

        /// <summary>1 つのテクスチャセット（composites はセットの下の名前）。</summary>
        static YlpImportInfo.TextureSetSummary DescribeSet(byte[] data, Dictionary<string, byte[]> composites, string nativeEntry, string name, int slot, bool current, bool named, List<string> warnings)
        {
            var summary = new YlpImportInfo.TextureSetSummary { name = name, materialSlot = slot, current = current };
            if (composites.Count > 0)
            {
                try { DescribeComposites(composites, summary); return summary; }
                catch (InvalidDataException ex) { warnings.Add((named ? "Texture set " + name + ": " : "") + "The composite images in the file could not be used (" + ex.Message + "); the size and channels were read from the native document."); }
            }
            var native = YlpArchive.Read(data, entry => entry == nativeEntry);
            if (!native.TryGetValue(nativeEntry, out var bytes)) throw new InvalidDataException("The file has no " + nativeEntry + ".");
            var document = DocumentBinary.Read(bytes);
            CheckSize(document.Width, document.Height, nativeEntry);
            summary.width = document.Width; summary.height = document.Height; summary.channels = YlpContent.UsedChannels(document).ToArray(); summary.fromNativeDocument = true;
            return summary;
        }

        static void DescribeComposites(Dictionary<string, byte[]> composites, YlpImportInfo.TextureSetSummary info)
        {
            var channels = new List<PaintChannel>(); int width = 0, height = 0;
            foreach (var entry in composites)
            {
                YlpContent.TryParseComposite(entry.Key, out var channel);
                PngSize(entry.Value, entry.Key, out int w, out int h);
                if (channels.Count > 0 && (w != width || h != height))
                    throw new InvalidDataException(entry.Key + " is " + w + "x" + h + " but the other composite images are " + width + "x" + height);
                width = w; height = h; channels.Add(channel);
            }
            channels.Sort();
            info.width = width; info.height = height; info.channels = channels.ToArray(); info.fromNativeDocument = false;
        }

        /// <summary>PNG の大きさ（IHDR）。</summary>
        internal static void PngSize(byte[] png, string entry, out int width, out int height)
        {
            if (png.Length < 33 || !png.Take(PngSignature.Length).SequenceEqual(PngSignature) || png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
                throw new InvalidDataException(entry + " is not a PNG image");
            width = ReadBigEndian(png, 16); height = ReadBigEndian(png, 20);
            CheckSize(width, height, entry);
        }

        static int ReadBigEndian(byte[] bytes, int offset) => bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];

        static void CheckSize(int width, int height, string what)
        {
            if (width < 1 || height < 1 || width > MaxTextureSize || height > MaxTextureSize)
                throw new InvalidDataException(what + " is " + width + "x" + height + ", outside the 1-" + MaxTextureSize + " pixel range of a Unity texture");
        }

        /// <summary>.ylp のアセットをダブルクリックしたら YoluPainter で開く。他のアセットは Unity に任せる。</summary>
        [OnOpenAsset]
        static bool OnOpenAsset(int instanceID, int line) => OpenAsset(instanceID);

        internal static bool OpenAsset(int instanceID)
        {
            string assetPath = AssetDatabase.GetAssetPath(instanceID);
            if (!IsYlp(assetPath)) return false;
            TexturePaintWindow.OpenFileInWindow(FullPath(assetPath));
            return true;
        }

        public static bool IsYlp(string assetPath) => !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(YlpArchive.Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>アセットのパス（"Assets/..."、"Packages/..."）から実際のファイルの絶対パスへ。</summary>
        public static string FullPath(string assetPath) => Path.GetFullPath(FileUtil.GetPhysicalPath(assetPath));

        /// <summary>.ylp の thumbnail.png（無い・読めなければ null）。表示用で、取り込みの結果には入れない。呼んだ側が破棄する。</summary>
        internal static Texture2D LoadThumbnail(string assetPath)
        {
            try
            {
                var files = YlpArchive.Read(File.ReadAllBytes(FileUtil.GetPhysicalPath(assetPath)), entry => entry == YlpContent.ThumbnailName);
                if (!files.TryGetValue(YlpContent.ThumbnailName, out var png)) return null;
                PngSize(png, YlpContent.ThumbnailName, out _, out _);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                if (texture.LoadImage(png, false)) return texture;
                DestroyImmediate(texture); return null;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) { return null; }
        }
    }
}
