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
    /// <summary>.ylp を Unity のアセットとして読み込む。合成済みの画像（composite/&lt;チャンネル&gt;.png）を 1 チャンネル 1 枚の
    /// Texture2D のサブアセットにする。
    /// <list type="bullet">
    /// <item>サブアセットの識別子はチャンネル名（"Color"、"Roughness" など）。.ylp を描き直して取り込み直しても識別子が
    /// 変わらないので、マテリアルからの参照は切れない。</item>
    /// <item>主オブジェクトは Color（無ければ列挙の順で最初のチャンネル）。.ylp をそのままマテリアルのテクスチャ欄へ
    /// ドラッグできる。</item>
    /// <item>画素は PNG のバイト列そのもの（straight RGBA32。乗算済みにも色変換もしない）。Color / Emission は sRGB、
    /// 他はリニアのデータとして作る。</item>
    /// <item>合成済みの画像が無い・使えないときだけ document.utpaint（正本）を展開して Core で合成する。画像があるときは
    /// 正本を展開しない。</item>
    /// <item>読めないファイルは理由をインポートのエラーに出し、1 画素の代わりのテクスチャを主オブジェクトにする
    /// （アセットは消えない）。.ylp そのものには一切書かない。</item>
    /// <item>取り込んだ結果（大きさ・チャンネル・正本から合成したか・エラー）は隠したサブアセット <see cref="YlpImportInfo"/>
    /// に残す。</item>
    /// <item>テクスチャは読み書き可能のまま。YoluPainter やユーザーのスクリプトが画素を読めるようにするためで、
    /// <see cref="EditorUtility.CompressTexture(Texture2D, TextureFormat, TextureCompressionQuality)"/> も読み書き可能な
    /// テクスチャにしか使えない。その分、ビルドしたものでは CPU 側の写しもメモリに載る。</item>
    /// </list></summary>
    [ScriptedImporter(1, "ylp")]
    internal sealed class YlpImporter : ScriptedImporter
    {
        public enum TextureCompression { None, Compressed }

        public bool generateMipMaps = true;
        public FilterMode filterMode = FilterMode.Bilinear;
        public TextureWrapMode wrapMode = TextureWrapMode.Repeat;
        [Range(0, 16)] public int anisoLevel = 1;
        public TextureCompression compression = TextureCompression.None;

        /// <summary>Compressed で使う形式。デスクトップ向けの BC7（アルファ付き、sRGB・リニアのどちらにも使え、BC3 より
        /// 劣化が少ない）。幅と高さが 4 の倍数でないと使えない。</summary>
        public const TextureFormat CompressedFormat = TextureFormat.BC7;
        /// <summary>読めなかったときの代わりのテクスチャと、取り込んだ結果のサブアセットの識別子。</summary>
        public const string PlaceholderIdentifier = "Placeholder", InfoIdentifier = "ImportInfo";
        /// <summary>Unity のテクスチャの 1 辺の上限。PNG はこれを超えるものを展開する前に断る。</summary>
        public const int MaxTextureSize = 16384;

        static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>1 チャンネルの画素（左下原点の straight RGBA8）。</summary>
        sealed class ChannelImage
        {
            public PaintChannel Channel;
            public int Width, Height;
            public byte[] Rgba;
        }

        /// <summary>チャンネルのテクスチャの名前。主オブジェクトだけは Unity がファイル名に置き換える。</summary>
        public static string TextureName(string assetPath, PaintChannel channel) => Path.GetFileNameWithoutExtension(assetPath) + " " + channel;
        /// <summary>主オブジェクトにするチャンネル: Color、無ければ列挙の順で最初のもの。</summary>
        public static PaintChannel MainChannel(IEnumerable<PaintChannel> channels) => channels.Contains(PaintChannel.Color) ? PaintChannel.Color : channels.Min();

        /// <summary>取り込んだ結果。まだ取り込まれていないアセットでは null。</summary>
        internal static YlpImportInfo LoadInfo(string assetPath) => AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<YlpImportInfo>().FirstOrDefault();

        /// <summary>取り込んだチャンネルのテクスチャ。読めなかったファイルでは空。</summary>
        internal static Dictionary<PaintChannel, Texture2D> LoadTextures(string assetPath)
        {
            var result = new Dictionary<PaintChannel, Texture2D>();
            var info = LoadInfo(assetPath);
            if (info == null || info.channels.Length == 0) return result;
            var textures = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Texture2D>().ToList();
            var main = MainChannel(info.channels);
            foreach (var channel in info.channels)
            {
                var texture = channel == main ? AssetDatabase.LoadMainAssetAtPath(assetPath) as Texture2D : textures.FirstOrDefault(t => t.name == TextureName(assetPath, channel));
                if (texture != null) result.Add(channel, texture);
            }
            return result;
        }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var built = new List<KeyValuePair<PaintChannel, Texture2D>>();
            bool added = false;
            try
            {
                var warnings = new List<string>();
                var images = ReadImages(File.ReadAllBytes(FileUtil.GetPhysicalPath(ctx.assetPath)), warnings, out bool fromNative);
                foreach (var image in images) built.Add(new KeyValuePair<PaintChannel, Texture2D>(image.Channel, CreateTexture(image, TextureName(ctx.assetPath, image.Channel), warnings)));
                foreach (var warning in warnings) ctx.LogImportWarning(warning);
                added = true;
                foreach (var entry in built) ctx.AddObjectToAsset(entry.Key.ToString(), entry.Value);
                var main = MainChannel(built.Select(e => e.Key));
                ctx.SetMainObject(built.First(e => e.Key == main).Value);
                AddInfo(ctx, images[0].Width, images[0].Height, built.Select(e => e.Key).ToArray(), fromNative, "");
            }
            catch (Exception ex)
            {
                // 途中まで作ったテクスチャは出さない（一部のチャンネルだけのアセットにしない）。アセットに渡した後のものは Unity の持ち物
                if (!added) foreach (var entry in built) if (entry.Value != null) DestroyImmediate(entry.Value);
                string reason = ex is InvalidDataException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                ctx.LogImportError("YoluPainter could not import " + ctx.assetPath + ": " + reason);
                var placeholder = new Texture2D(1, 1, TextureFormat.RGBA32, false, false) { name = Path.GetFileNameWithoutExtension(ctx.assetPath) };
                placeholder.SetPixels32(new[] { new Color32(255, 0, 255, 255) });
                placeholder.Apply(false, false);
                ctx.AddObjectToAsset(PlaceholderIdentifier, placeholder);
                ctx.SetMainObject(placeholder);
                AddInfo(ctx, 0, 0, new PaintChannel[0], false, reason);
            }
        }

        static void AddInfo(AssetImportContext ctx, int width, int height, PaintChannel[] channels, bool fromNative, string error)
        {
            var info = ScriptableObject.CreateInstance<YlpImportInfo>();
            info.name = "Import Info"; info.hideFlags = HideFlags.HideInHierarchy;
            info.width = width; info.height = height; info.channels = channels; info.fromNativeDocument = fromNative; info.error = error;
            ctx.AddObjectToAsset(InfoIdentifier, info);
        }

        /// <summary>チャンネルごとの画素を列挙の順で返す。合成済みの画像が 1 枚も無いか、使えない（PNG として読めない・大きさが
        /// 揃わない）ときは正本から合成し、使えなかった理由は <paramref name="warnings"/> に足す。</summary>
        static List<ChannelImage> ReadImages(byte[] data, List<string> warnings, out bool fromNative)
        {
            fromNative = false;
            var composites = YlpArchive.Read(data, entry => YlpContent.TryParseComposite(entry, out _));
            if (composites.Count > 0)
            {
                try { return DecodeComposites(composites); }
                catch (InvalidDataException ex) { warnings.Add("The composite images in the file could not be used (" + ex.Message + "); the textures were rebuilt from the native document."); }
            }
            fromNative = true;
            var native = YlpArchive.Read(data, entry => entry == YlpArchive.NativeName);
            var document = DocumentBinary.Read(native[YlpArchive.NativeName]);
            CheckSize(document.Width, document.Height, YlpArchive.NativeName);
            var channels = YlpContent.UsedChannels(document);
            // どのチャンネルも使っていない（レイヤーが無いなど）ドキュメントも、透明な Color を 1 枚出してテクスチャとして扱えるようにする
            if (channels.Count == 0) channels.Add(PaintChannel.Color);
            return channels.Select(c => new ChannelImage { Channel = c, Width = document.Width, Height = document.Height, Rgba = document.Composite(c) }).ToList();
        }

        static List<ChannelImage> DecodeComposites(Dictionary<string, byte[]> composites)
        {
            var images = new List<ChannelImage>();
            foreach (var entry in composites)
            {
                YlpContent.TryParseComposite(entry.Key, out var channel);
                var image = DecodePng(entry.Value, entry.Key); image.Channel = channel;
                if (images.Count > 0 && (image.Width != images[0].Width || image.Height != images[0].Height))
                    throw new InvalidDataException(entry.Key + " is " + image.Width + "x" + image.Height + " but " + YlpContent.CompositeName(images[0].Channel) + " is " + images[0].Width + "x" + images[0].Height);
                images.Add(image);
            }
            images.Sort((a, b) => a.Channel.CompareTo(b.Channel));
            return images;
        }

        /// <summary>PNG を左下原点の RGBA8 に（Texture2D.LoadImage と同じ向き）。IHDR の大きさを展開の前に確かめる。</summary>
        static ChannelImage DecodePng(byte[] png, string entry)
        {
            if (png.Length < 33 || !png.Take(PngSignature.Length).SequenceEqual(PngSignature) || png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
                throw new InvalidDataException(entry + " is not a PNG image");
            int width = ReadBigEndian(png, 16), height = ReadBigEndian(png, 20);
            CheckSize(width, height, entry);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!texture.LoadImage(png, false) || texture.width != width || texture.height != height) throw new InvalidDataException(entry + " could not be decoded");
                // LoadImage は ARGB32 などで返すことがあるので、GetPixels32 で RGBA の順に揃える（値は変えない）
                var pixels = texture.GetPixels32();
                var rgba = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i]; int o = i * 4;
                    rgba[o] = p.r; rgba[o + 1] = p.g; rgba[o + 2] = p.b; rgba[o + 3] = p.a;
                }
                return new ChannelImage { Width = width, Height = height, Rgba = rgba };
            }
            finally { DestroyImmediate(texture); }
        }

        static int ReadBigEndian(byte[] bytes, int offset) => bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];

        static void CheckSize(int width, int height, string what)
        {
            if (width < 1 || height < 1 || width > MaxTextureSize || height > MaxTextureSize)
                throw new InvalidDataException(what + " is " + width + "x" + height + ", outside the 1-" + MaxTextureSize + " pixel range of a Unity texture");
        }

        Texture2D CreateTexture(ChannelImage image, string name, List<string> warnings)
        {
            var texture = BuildUncompressed(image, name);
            if (compression != TextureCompression.Compressed) return texture;
            if (image.Width % 4 != 0 || image.Height % 4 != 0)
            {
                warnings.Add(name + " is kept uncompressed: " + CompressedFormat + " needs a width and height that are multiples of 4 (the image is " + image.Width + "x" + image.Height + ").");
                return texture;
            }
            string failure;
            try
            {
                EditorUtility.CompressTexture(texture, CompressedFormat, TextureCompressionQuality.Normal);
                failure = texture.format == CompressedFormat ? null : "the result was " + texture.format;
            }
            catch (Exception ex) { failure = ex.Message; }
            if (failure == null) return texture;
            // 失敗した圧縮はテクスチャの中身を壊していることがある（画素が無くなる）ので、元の画素から作り直す
            warnings.Add(name + " is kept uncompressed: compressing to " + CompressedFormat + " failed (" + failure + ").");
            DestroyImmediate(texture);
            return BuildUncompressed(image, name);
        }

        Texture2D BuildUncompressed(ChannelImage image, string name)
        {
            var texture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, generateMipMaps, !YlpContent.IsColor(image.Channel))
            {
                name = name, filterMode = filterMode, wrapMode = wrapMode, anisoLevel = anisoLevel,
            };
            texture.SetPixelData(image.Rgba, 0);
            texture.Apply(generateMipMaps, false);
            return texture;
        }

        /// <summary>.ylp のアセット（またはそのサブアセット）をダブルクリックしたら YoluPainter で開く。他のアセットは Unity に任せる。</summary>
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
    }
}
