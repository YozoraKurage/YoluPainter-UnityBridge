using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// ペイントソフトとしての見た目（Photoshop・CLIP STUDIO・Substance Painter に近い、暗いグレーと青のアクセント）。Unity の標準の
    /// スタイル（ボタンの立体の背景など）は使わず、背景は <see cref="PaintGui"/> が色の矩形と角丸で描く。文字は Unity エディタの
    /// フォント（日本語は OS のフォントで補われる）。
    /// </summary>
    internal static class PaintTheme
    {
        public static readonly Color WindowBg = Hex(0x1B1B1D), PanelBg = Hex(0x252528), PanelHeader = Hex(0x2E2E32), MenuBg = Hex(0x202023),
            ControlBg = Hex(0x18181A), ControlHover = Hex(0x36363B), ControlActive = Hex(0x404048), Border = Hex(0x111113),
            Separator = Hex(0x343438), CanvasBg = Hex(0x131314), Accent = Hex(0x3D8EF0), AccentDim = Hex(0x2B5D9C), AccentSoft = new Color(.24f, .56f, .94f, .22f),
            Text = Hex(0xD9D9DC), TextDim = Hex(0x9A9AA0), TextDisabled = Hex(0x5C5C62), Warning = Hex(0xE8B03C), Error = Hex(0xE5534B),
            SliderFill = Hex(0x355F96), SliderFillHover = Hex(0x3F70B0);

        public const float MenuBarHeight = 24, OptionsBarHeight = 36, StatusBarHeight = 22, ToolStripWidth = 44, DockWidth = 300, RowHeight = 22, Padding = 8;

        static GUIStyle s_label, s_labelDim, s_labelSmall, s_labelBold, s_labelCenter, s_header, s_menu, s_field, s_value, s_wrap;
        public static GUIStyle Label => s_label ?? (s_label = Make(12, Text));
        public static GUIStyle LabelDim => s_labelDim ?? (s_labelDim = Make(11, TextDim));
        public static GUIStyle LabelSmall => s_labelSmall ?? (s_labelSmall = Make(10, TextDim));
        public static GUIStyle LabelBold => s_labelBold ?? (s_labelBold = Make(12, Text, FontStyle.Bold));
        public static GUIStyle LabelCenter => s_labelCenter ?? (s_labelCenter = Make(12, Text, FontStyle.Normal, TextAnchor.MiddleCenter));
        public static GUIStyle Header => s_header ?? (s_header = Make(11, Text, FontStyle.Bold));
        public static GUIStyle Menu => s_menu ?? (s_menu = Make(12, Text, FontStyle.Normal, TextAnchor.MiddleCenter));
        public static GUIStyle Value => s_value ?? (s_value = Make(11, Text, FontStyle.Normal, TextAnchor.MiddleRight));
        public static GUIStyle Wrap => s_wrap ?? (s_wrap = new GUIStyle(Make(11, TextDim)) { wordWrap = true, alignment = TextAnchor.UpperLeft });
        /// <summary>文字の入力欄（背景は描かない。<see cref="PaintGui.TextField"/> が描く）。</summary>
        public static GUIStyle Field
        {
            get
            {
                if (s_field != null) return s_field;
                s_field = new GUIStyle(Make(12, Text)) { padding = new RectOffset(6, 6, 2, 2) };
                s_field.focused.textColor = Text; s_field.hover.textColor = Text; s_field.active.textColor = Text;
                return s_field;
            }
        }

        static GUIStyle Make(int size, Color color, FontStyle style = FontStyle.Normal, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            Font font = null; try { font = EditorStyles.label?.font; } catch (System.NullReferenceException) { } // バッチモードでは EditorStyles がまだ無いことがある（null は既定のフォント）
            var s = new GUIStyle { font = font, fontSize = size, fontStyle = style, alignment = anchor, clipping = TextClipping.Clip, richText = false, wordWrap = false };
            s.normal.textColor = color; s.hover.textColor = color; s.active.textColor = color; s.focused.textColor = color;
            s.padding = new RectOffset(0, 0, 0, 0); s.margin = new RectOffset(0, 0, 0, 0);
            return s;
        }

        static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
    }

    /// <summary>Editor/Icons の Material Symbols（Apache License 2.0、白の 48 px）。名前は Material Symbols の名前。</summary>
    internal static class PaintIcons
    {
        static readonly Dictionary<string, Texture2D> s_cache = new Dictionary<string, Texture2D>();
        static string s_folder;

        /// <summary>アイコン（無ければ null。描く側は文字で代える）。</summary>
        public static Texture2D Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (s_cache.TryGetValue(name, out var cached) && cached != null) return cached;
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "/" + name + ".png");
            s_cache[name] = texture;
            return texture;
        }

        /// <summary>アイコンのフォルダ（アセットのパス）。ローカライズの目印のスクリプトから辿る（パッケージがどこに入っても見つかる）。</summary>
        static string Folder
        {
            get
            {
                if (s_folder != null) return s_folder;
                var probe = ScriptableObject.CreateInstance<LocalizationAnchor>();
                try
                {
                    var script = MonoScript.FromScriptableObject(probe);
                    string path = script == null ? null : AssetDatabase.GetAssetPath(script); // .../Editor/Localization/LocalizationAnchor.cs
                    s_folder = path == null ? "Packages/net.yozolab.yolupainter/Editor/Icons" : Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace('\\', '/') + "/Icons";
                }
                finally { Object.DestroyImmediate(probe); }
                return s_folder;
            }
        }
    }
}
