using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// ツールのアイコン（UI のアイコンとは別の種類）。CLIP STUDIO のサブツールのように、描き手やプラグインが絵を差し替えられる。
    /// 探す順は (1) プラグインの登録 <see cref="Register"/>、(2) 描き手の画像（UserSettings/YoluPainter/ToolIcons/&lt;id&gt;.png、選択中は
    /// &lt;id&gt;_selected.png。無ければ通常の絵）、(3) 同梱の絵（Editor/UI/Icons/Tools。白なので UI の色を付けて描く）。描き手と
    /// プラグインの絵は色を付けずにそのまま描く（色付きの絵も使える）。
    /// </summary>
    public static class PainterToolIcons
    {
        /// <summary>同梱の絵のあるツールの ID。</summary>
        public static readonly IReadOnlyList<string> BuiltInIds = new[] { "brush", "eraser", "fill", "gradient", "select-rectangle", "select-ellipse", "lasso", "magic-wand", "move", "path", "eyedropper", "polygon-fill", "id-select" };
        /// <summary>描き手の画像の上限（バイト数と一辺）。</summary>
        public const int MaxFileBytes = 1 << 20, MaxSide = 256;

        /// <summary>アイコンが変わったとき（ウィンドウは描き直す）。</summary>
        public static event Action Changed;

        /// <summary>解決したアイコン。Tint のときは白い絵なので UI の色を付けて描く。</summary>
        public readonly struct Icon
        {
            public readonly Texture2D Texture; public readonly bool Tint;
            public Icon(Texture2D texture, bool tint) { Texture = texture; Tint = tint; }
        }

        static readonly Dictionary<string, (Texture2D normal, Texture2D selected)> s_plugins = new Dictionary<string, (Texture2D, Texture2D)>();
        sealed class UserIcon { public Texture2D Normal, Selected; public DateTime Stamp, SelectedStamp; }
        static readonly Dictionary<string, UserIcon> s_user = new Dictionary<string, UserIcon>();
        static double s_lastCheck;

        /// <summary>プラグインがツールの絵を登録する（selected を省くと選択中も同じ絵）。テクスチャの持ち主はプラグインのまま。</summary>
        public static void Register(string toolId, Texture2D icon, Texture2D selected = null)
        {
            RequireId(toolId);
            if (icon == null) throw new ArgumentNullException(nameof(icon));
            s_plugins[toolId] = (icon, selected); Changed?.Invoke();
        }
        public static void Unregister(string toolId) { if (toolId != null && s_plugins.Remove(toolId)) Changed?.Invoke(); }

        /// <summary>描き手の画像のフォルダ（このプロジェクトの UserSettings。この人だけのもの）。</summary>
        public static string UserFolder => Path.Combine(Path.GetDirectoryName(PainterSettings.PersonalPath), "ToolIcons");

        /// <summary>描き手の画像を置く（PNG を検めてから写す）。selected なら選択中の絵。</summary>
        public static void SetUserIcon(string toolId, string pngPath, bool selected = false)
        {
            RequireId(toolId);
            if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath)) throw new FileNotFoundException("The icon image was not found.", pngPath);
            var info = new FileInfo(pngPath);
            if (info.Length > MaxFileBytes) throw new InvalidDataException("The icon image is larger than " + (MaxFileBytes >> 10) + " KiB.");
            byte[] bytes = File.ReadAllBytes(pngPath);
            var probe = Decode(bytes) ?? throw new InvalidDataException("The icon image is not a PNG that Unity can read.");
            try { if (probe.width > MaxSide || probe.height > MaxSide) throw new InvalidDataException("The icon image is " + probe.width + "×" + probe.height + "; at most " + MaxSide + "×" + MaxSide + "."); }
            finally { UnityEngine.Object.DestroyImmediate(probe); }
            Directory.CreateDirectory(UserFolder);
            File.WriteAllBytes(UserPath(toolId, selected), bytes);
            Forget(toolId); Changed?.Invoke();
        }

        /// <summary>描き手の画像を消して同梱の絵に戻す（通常と選択中の両方）。</summary>
        public static void ResetUserIcon(string toolId)
        {
            RequireId(toolId);
            foreach (var selected in new[] { false, true }) { var path = UserPath(toolId, selected); if (File.Exists(path)) File.Delete(path); }
            Forget(toolId); Changed?.Invoke();
        }
        public static bool HasUserIcon(string toolId) => File.Exists(UserPath(toolId, false)) || File.Exists(UserPath(toolId, true));

        /// <summary>ツールの絵（無ければ Texture が null）。</summary>
        public static Icon Get(string toolId, bool selected)
        {
            if (string.IsNullOrEmpty(toolId)) return default;
            if (s_plugins.TryGetValue(toolId, out var plugin)) return new Icon(selected && plugin.selected != null ? plugin.selected : plugin.normal, false);
            var user = User(toolId);
            if (user != null && (user.Normal != null || user.Selected != null)) return new Icon(selected && user.Selected != null ? user.Selected : user.Normal ?? user.Selected, false);
            var bundled = PaintIcons.Get("Tools/" + toolId + (selected ? "_selected" : "")) ?? PaintIcons.Get("Tools/" + toolId);
            return new Icon(bundled, true);
        }

        static UserIcon User(string toolId)
        {
            // ファイルの変化は 2 秒ごとに見る（描き直しのたびに調べない）
            if (EditorApplication.timeSinceStartup - s_lastCheck > 2) { s_lastCheck = EditorApplication.timeSinceStartup; foreach (var id in s_user.Keys.ToList()) if (Stale(id, s_user[id])) Forget(id); }
            if (s_user.TryGetValue(toolId, out var cached)) return cached;
            var icon = new UserIcon();
            string normal = UserPath(toolId, false), selected = UserPath(toolId, true);
            if (File.Exists(normal)) { icon.Normal = Decode(File.ReadAllBytes(normal)); icon.Stamp = File.GetLastWriteTimeUtc(normal); }
            if (File.Exists(selected)) { icon.Selected = Decode(File.ReadAllBytes(selected)); icon.SelectedStamp = File.GetLastWriteTimeUtc(selected); }
            s_user[toolId] = icon; return icon;
        }
        static bool Stale(string id, UserIcon icon)
        {
            DateTime Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            return Stamp(UserPath(id, false)) != icon.Stamp || Stamp(UserPath(id, true)) != icon.SelectedStamp;
        }
        static void Forget(string toolId)
        {
            if (!s_user.TryGetValue(toolId, out var icon)) return;
            if (icon.Normal != null) UnityEngine.Object.DestroyImmediate(icon.Normal);
            if (icon.Selected != null) UnityEngine.Object.DestroyImmediate(icon.Selected);
            s_user.Remove(toolId);
        }
        /// <summary>描き手の画像の読み込みを捨てる（設定の場所が変わったとき・テスト）。</summary>
        internal static void ForgetAll() { foreach (var id in s_user.Keys.ToList()) Forget(id); s_lastCheck = 0; }

        static string UserPath(string toolId, bool selected) => Path.Combine(UserFolder, toolId + (selected ? "_selected" : "") + ".png");
        static Texture2D Decode(byte[] bytes)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            if (texture.LoadImage(bytes, false)) return texture;
            UnityEngine.Object.DestroyImmediate(texture); return null;
        }
        static void RequireId(string toolId)
        {
            if (string.IsNullOrEmpty(toolId) || toolId.Length > 64 || toolId.Any(c => !(char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_' || c == '.')))
                throw new ArgumentException("A tool id is 1–64 ASCII letters, digits or - _ . characters (it is also a file name).", nameof(toolId));
        }
    }
}
