using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>レイヤーの一覧のサムネイル: 今のチャンネルの画素（とマスク）を 32×32 に間引いた絵。中身の書き換え番号が進んだときだけ
    /// 作り直す（ストロークの間は描いている層だけ）。表示用で、保存にも履歴にも入れない。</summary>
    public sealed partial class TexturePaintWindow
    {
        const int ThumbnailSize = 32;
        sealed class Thumb { public long Stamp; public Texture2D Texture; }
        readonly Dictionary<(Guid layer, int what), Thumb> thumbnails = new Dictionary<(Guid, int), Thumb>();
        const int MaskThumb = -1;

        /// <summary>層の今のチャンネルのサムネイル（画素を持たないなら null）。</summary>
        Texture2D LayerThumbnail(PaintLayer layer)
        {
            if (layer.IsGroup || layer.Kind == LayerKind.Adjustment) return null;
            if (layer.Kind == LayerKind.Fill)
            {
                if (!layer.FillValues.TryGetValue(channel, out var fill)) return null;
                return Thumbnail((layer.Id, (int)channel), fill.R | (long)fill.G << 8 | (long)fill.B << 16 | (long)fill.A << 24, (x, y) => fill);
            }
            if (!layer.TryGetChannel(channel, out var surface)) return null;
            return Thumbnail((layer.Id, (int)channel), surface.Revision, (x, y) => surface.GetPixel(x, y));
        }

        /// <summary>マスクのサムネイル（白が見える所、黒が隠れる所。Photoshop と同じ）。</summary>
        Texture2D MaskThumbnail(PaintLayer layer)
        {
            var mask = layer.Mask; if (mask == null) return null;
            long stamp = mask.Surface.Revision * 2 + (mask.Inverted ? 1 : 0);
            return Thumbnail((layer.Id, MaskThumb), stamp, (x, y) =>
            {
                byte hide = mask.Surface.GetPixel(x, y).A; byte v = (byte)(mask.Inverted ? hide : 255 - hide);
                return new Rgba32(v, v, v, 255);
            });
        }

        Texture2D Thumbnail((Guid, int) key, long stamp, Func<int, int, Rgba32> sample)
        {
            if (thumbnails.TryGetValue(key, out var thumb) && thumb.Stamp == stamp && thumb.Texture != null) return thumb.Texture;
            if (thumb == null) thumbnails[key] = thumb = new Thumb();
            if (thumb.Texture == null) thumb.Texture = new Texture2D(ThumbnailSize, ThumbnailSize, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            // 縦横比を保ち、短い辺の側は透明にする（左下原点の画素を、テクスチャも左下原点で）
            float scale = Mathf.Max(document.Width, document.Height) / (float)ThumbnailSize;
            float ox = (ThumbnailSize - document.Width / scale) / 2, oy = (ThumbnailSize - document.Height / scale) / 2;
            var pixels = new Color32[ThumbnailSize * ThumbnailSize];
            for (int j = 0; j < ThumbnailSize; j++)
                for (int i = 0; i < ThumbnailSize; i++)
                {
                    int x = Mathf.FloorToInt((i + .5f - ox) * scale), y = Mathf.FloorToInt((j + .5f - oy) * scale);
                    if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) continue;
                    var c = sample(x, y); pixels[j * ThumbnailSize + i] = new Color32(c.R, c.G, c.B, c.A);
                }
            thumb.Texture.SetPixels32(pixels); thumb.Texture.Apply(false, false);
            thumb.Stamp = stamp;
            return thumb.Texture;
        }

        /// <summary>消えた層のサムネイルを捨てる（一覧を描くたびに安く確かめる）。</summary>
        void ForgetStaleThumbnails()
        {
            if (thumbnails.Count == 0) return;
            var alive = new HashSet<Guid>(document.Layers.Select(l => l.Id));
            foreach (var key in thumbnails.Keys.Where(k => !alive.Contains(k.layer)).ToList()) { if (thumbnails[key].Texture != null) DestroyImmediate(thumbnails[key].Texture); thumbnails.Remove(key); }
        }

        void DisposeThumbnails() { foreach (var t in thumbnails.Values) if (t.Texture != null) DestroyImmediate(t.Texture); thumbnails.Clear(); }

        /// <summary>透明の分かる市松の上にサムネイルを描く。</summary>
        static void DrawThumbnail(Rect r, Texture2D texture)
        {
            if (Event.current.type != EventType.Repaint) return;
            PaintGui.Checker(r, 4);
            if (texture != null) GUI.DrawTexture(r, texture, ScaleMode.StretchToFill, true);
            PaintGui.Outline(r, PaintTheme.Border, 1, 0);
        }
    }
}
