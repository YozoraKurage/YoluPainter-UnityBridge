using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>.ylp に入れる中身の約束（書く側のウィンドウと読む側のインポーターで共有する）。
    /// <list type="bullet">
    /// <item>document.utpaint: ネイティブ正本（唯一の正本）</item>
    /// <item>composite/&lt;チャンネル&gt;.png: そのチャンネルを使うレイヤーがあるときの合成結果。straight RGBA8、PNG なので外から見ても
    /// 上下は正しい。正本から作った派生物で、読み込み時の正本にはしない。Normal は Unity 向けの出力（<see cref="Image"/>）</item>
    /// <item>thumbnail.png: Color（無ければ最初のチャンネル）の合成を長辺 256px 以下に縮めたもの</item>
    /// <item>view.json / brush.json / imported-original.psd（PSD から取り込んだときの原本のバイト列）</item>
    /// <item>meshmap-&lt;種類&gt;.bin: ベイクした mesh map と由来（Core の MeshMapBinary。派生物で、読めなければ焼き直す。
    /// 知らない版のウィンドウは読み飛ばし、そのウィンドウで保存し直すと落ちる）</item>
    /// </list></summary>
    internal static class YlpContent
    {
        public const string ThumbnailName = "thumbnail.png", ViewName = "view.json", BrushName = "brush.json", ImportedOriginalName = "imported-original.psd";
        public const int ThumbnailSize = 256;

        public static string CompositeName(PaintChannel channel) => YlpArchive.CompositeFolder + channel + ".png";
        public static bool TryParseComposite(string entry, out PaintChannel channel)
        {
            channel = default;
            if (entry == null || !entry.StartsWith(YlpArchive.CompositeFolder, StringComparison.Ordinal) || !entry.EndsWith(".png", StringComparison.Ordinal)) return false;
            string name = entry.Substring(YlpArchive.CompositeFolder.Length, entry.Length - YlpArchive.CompositeFolder.Length - 4);
            return Enum.TryParse(name, false, out channel) && Enum.IsDefined(typeof(PaintChannel), channel) && channel.ToString() == name;
        }
        /// <summary>色として扱うチャンネル（sRGB）。他はデータ（リニア）。</summary>
        public static bool IsColor(PaintChannel channel) => channel == PaintChannel.Color || channel == PaintChannel.Emission;

        /// <summary>どれかのレイヤーが使っているチャンネル（列挙の順）。Height → Normal が有効で Height を使っていれば Normal も。</summary>
        public static List<PaintChannel> UsedChannels(PaintDocument document)
        {
            return Enum.GetValues(typeof(PaintChannel)).Cast<PaintChannel>().Where(c => document.Layers.Any(l => l.IsChannelEnabled(c)) || c == PaintChannel.Normal && NormalMaps.DerivesNormal(document)).ToList();
        }
        /// <summary>Unity に渡すチャンネルの画像（左下原点の straight RGBA8）。Normal は <see cref="NormalMaps.Output"/>（OpenGL の向き・
        /// 不透明・塗っていない所は平ら・Height → Normal 込み）、他はレイヤーの合成。</summary>
        public static byte[] Image(PaintDocument document, PaintChannel channel)
        { return channel == PaintChannel.Normal ? NormalMaps.Output(document) : document.Composite(channel); }
        /// <summary>ほかのツール向けのファイルに書く画像。Normal は文書の設定の Y の向き（<see cref="NormalMaps.FileOutput"/>）。</summary>
        public static byte[] FileImage(PaintDocument document, PaintChannel channel)
        { return channel == PaintChannel.Normal ? NormalMaps.FileOutput(document) : document.Composite(channel); }

        /// <summary>合成済み PNG とサムネイル。</summary>
        public static Dictionary<string, byte[]> Composites(PaintDocument document)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var channels = UsedChannels(document);
            foreach (var channel in channels) files.Add(CompositeName(channel), EncodePng(Image(document, channel), document.Width, document.Height));
            if (channels.Count > 0)
            {
                var main = channels.Contains(PaintChannel.Color) ? PaintChannel.Color : channels[0];
                files.Add(ThumbnailName, Thumbnail(Image(document, main), document.Width, document.Height));
            }
            return files;
        }

        /// <summary>左下原点の RGBA8 を PNG に。</summary>
        public static byte[] EncodePng(byte[] rgba, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try { texture.LoadRawTextureData(rgba); texture.Apply(false, false); return texture.EncodeToPNG(); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        /// <summary>箱フィルタで長辺 <see cref="ThumbnailSize"/> 以下に縮める（アルファで重み付けし、透明画素の色を混ぜない）。</summary>
        static byte[] Thumbnail(byte[] rgba, int width, int height)
        {
            int step = Math.Max(1, (Math.Max(width, height) + ThumbnailSize - 1) / ThumbnailSize);
            int w = Math.Max(1, width / step), h = Math.Max(1, height / step);
            var result = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                long r = 0, g = 0, b = 0, a = 0; int n = 0;
                for (int sy = y * step; sy < Math.Min(height, (y + 1) * step); sy++) for (int sx = x * step; sx < Math.Min(width, (x + 1) * step); sx++)
                {
                    int i = (sy * width + sx) * 4; int alpha = rgba[i + 3];
                    r += rgba[i] * alpha; g += rgba[i + 1] * alpha; b += rgba[i + 2] * alpha; a += alpha; n++;
                }
                int o = (y * w + x) * 4;
                if (a > 0) { result[o] = (byte)(r / a); result[o + 1] = (byte)(g / a); result[o + 2] = (byte)(b / a); }
                result[o + 3] = (byte)(a / Math.Max(1, n));
            }
            return EncodePng(result, w, h);
        }
    }
}
