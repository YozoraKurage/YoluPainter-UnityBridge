using System;
using System.Collections.Generic;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>筆先の ID と実体の対応。ID は "builtin:&lt;id&gt;"（BuiltInBrushes が生成）、"bundled:&lt;set&gt;/&lt;file&gt;"
    /// （BundledBrushSets）、"library:&lt;key&gt;" / "project:&lt;key&gt;"（取り込んだブラシ、BrushLibrary の個人 / 共有の置き場）。
    /// 空文字列は丸い筆先。
    /// 1 つの ID が複数の筆先（GIMP のホースなど）を指すこともある。</summary>
    internal static class BrushTips
    {
        /// <summary>1 枚の筆先、または筆先の組と選び方。</summary>
        internal sealed class TipRef
        {
            public readonly BrushTip Tip; public readonly BrushTip[] Tips; public readonly TipSelection Selection;
            public TipRef(BrushTip tip, BrushTip[] tips, TipSelection selection) { Tip = tip; Tips = tips; Selection = selection; }
            public BrushTip First => Tips != null && Tips.Length > 0 ? Tips[0] : Tip;
            public bool Matches(BrushSettings s)
            {
                if (Tips != null) return s.Tips != null && s.Tips.Length == Tips.Length && System.Linq.Enumerable.SequenceEqual(s.Tips, Tips);
                return s.Tips == null && Tip != null && ReferenceEquals(s.Tip, Tip);
            }
        }
        const string BuiltInPrefix = "builtin:";
        static readonly Dictionary<string, Texture2D> thumbnails = new Dictionary<string, Texture2D>();

        public static TipRef ResolveRef(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith(BuiltInPrefix, StringComparison.Ordinal)) { var t = BuiltInBrushes.Tip(id.Substring(BuiltInPrefix.Length)); return t == null ? null : new TipRef(t, null, TipSelection.Random); }
            if (id.StartsWith(BundledBrushSets.Prefix, StringComparison.Ordinal)) return BundledBrushSets.Resolve(id);
            return BrushLibrary.Owning(id)?.ResolveRef(id);
        }
        /// <summary>1 枚目の筆先（紙の質感とサムネイル用）。</summary>
        public static BrushTip Resolve(string id) => ResolveRef(id)?.First;

        /// <summary>ID の筆先を設定に入れる。知らない ID なら丸い筆先のまま。</summary>
        public static void Apply(BrushSettings settings, string id)
        {
            var r = ResolveRef(id);
            if (r == null) { settings.Tip = null; settings.Tips = null; return; }
            settings.Tip = r.Tip; settings.Tips = r.Tips; settings.TipSelection = r.Selection;
        }

        /// <summary>設定の筆先の ID（内蔵・同梱・取り込み済みのもの）。丸い筆先と未知の筆先は空文字列。</summary>
        public static string IdOf(BrushSettings s)
        {
            if (s.Tip == null && (s.Tips == null || s.Tips.Length == 0)) return "";
            if (s.Tips == null) foreach (var id in BuiltInBrushes.TipIds) if (ReferenceEquals(BuiltInBrushes.Tip(id), s.Tip)) return BuiltInPrefix + id;
            return BundledBrushSets.IdOf(s) ?? BrushLibrary.Personal.IdOf(s) ?? BrushLibrary.Project.IdOf(s) ?? "";
        }
        /// <summary>紙の質感など 1 枚の筆先の ID。</summary>
        public static string IdOf(BrushTip tip)
        {
            if (tip == null) return "";
            return IdOf(new BrushSettings { Tip = tip });
        }


        /// <summary>筆先の白黒サムネイル（48px、上下はキャンバスと同じ左下原点）。丸い筆先と未知の ID は null。</summary>
        public static Texture2D Thumbnail(string id)
        {
            var tip = Resolve(id);
            if (tip == null) return null;
            if (thumbnails.TryGetValue(id, out var cached) && cached != null) return cached;
            const int size = 48;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            double aspect = tip.Width / (double)tip.Height;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                double u = (x + .5) / size, v = (y + .5) / size;
                // 大きい辺を枠いっぱいに合わせる（BrushStroke と同じ）。
                if (aspect >= 1) v = (v - .5) * aspect + .5; else u = (u - .5) / aspect + .5;
                byte c = (byte)(255 - Math.Round(tip.Sample(u, v) * 255));
                pixels[y * size + x] = new Color32(c, c, c, 255);
            }
            texture.SetPixels32(pixels); texture.Apply(false, false);
            thumbnails[id] = texture;
            return texture;
        }
    }
}
