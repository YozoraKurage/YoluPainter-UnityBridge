using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>gettext の .po の最小限の読み手: msgctxt・msgid・msgstr（複数行の続き、\n \t \" \\ のエスケープ）。msgctxt の
    /// ある項目のキーは gettext と同じく「文脈 + \u0004 + msgid」。複数形は使わないので読まない（あれば読み飛ばさずに失敗させる）。</summary>
    internal static class PoCatalog
    {
        /// <summary>Editor/Localization/&lt;locale&gt;.po と、Editor/Localization/&lt;locale&gt;/*.po（画面の部分ごとに分けた表。名前の順）を
        /// まとめた表。同じキーが二つの表にあれば読み込みを断る（どちらが効くか分からなくなるので）。無ければ空。</summary>
        public static Dictionary<string, string> Load(string locale)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Files(locale))
                foreach (var entry in Parse(File.ReadAllText(path, Encoding.UTF8), path))
                {
                    if (result.ContainsKey(entry.Key)) throw new InvalidDataException(Path.GetFileName(path) + ": \"" + entry.Key.Replace("\u0004", " | ") + "\" is already in another " + locale + " catalog.");
                    result.Add(entry.Key, entry.Value);
                }
            return result;
        }

        /// <summary>ある言語の .po のファイル（無ければ空）。</summary>
        public static List<string> Files(string locale)
        {
            var files = new List<string>(); string folder = Folder();
            if (folder == null) return files;
            string main = Path.Combine(folder, locale + ".po");
            if (File.Exists(main)) files.Add(main);
            string parts = Path.Combine(folder, locale);
            if (Directory.Exists(parts)) { var more = Directory.GetFiles(parts, "*.po"); Array.Sort(more, StringComparer.Ordinal); files.AddRange(more); }
            return files;
        }

        /// <summary>.po のあるフォルダの絶対パス（見つからなければ null）。</summary>
        public static string Folder()
        {
            string folder = PackagePaths.Physical("Editor/Localization");
            return Directory.Exists(folder) ? folder : null;
        }

        public static Dictionary<string, string> Parse(string text, string source = "po")
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            string context = null, id = null, str = null; StringBuilder current = null; int line = 0; bool inContext = false;
            void Flush()
            {
                if (id != null && str != null && id.Length > 0) result[context == null ? id : context + "\u0004" + id] = str;
                context = null; id = null; str = null; current = null; inContext = false;
            }
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                line++;
                var s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#", StringComparison.Ordinal)) { if (s.Length == 0 && str != null) { str = current.ToString(); Flush(); } continue; }
                if (s.StartsWith("msgid_plural", StringComparison.Ordinal) || s.StartsWith("msgstr[", StringComparison.Ordinal))
                    throw new InvalidDataException(source + ":" + line + ": plural forms are not used by YoluPainter.");
                if (s.StartsWith("msgctxt ", StringComparison.Ordinal))
                {
                    if (str != null) { str = current.ToString(); Flush(); }
                    current = new StringBuilder(Unquote(s.Substring(8), source, line)); inContext = true; continue;
                }
                if (s.StartsWith("msgid ", StringComparison.Ordinal))
                {
                    if (str != null) { str = current.ToString(); Flush(); }
                    if (inContext) { context = current.ToString(); inContext = false; }
                    current = new StringBuilder(Unquote(s.Substring(6), source, line)); id = ""; continue;
                }
                if (s.StartsWith("msgstr ", StringComparison.Ordinal))
                {
                    if (current == null || str != null) throw new InvalidDataException(source + ":" + line + ": msgstr without msgid.");
                    id = current.ToString(); current = new StringBuilder(Unquote(s.Substring(7), source, line)); str = ""; continue;
                }
                if (s.StartsWith("\"", StringComparison.Ordinal))
                {
                    if (current == null) throw new InvalidDataException(source + ":" + line + ": a continued string without msgid or msgstr.");
                    current.Append(Unquote(s, source, line)); continue;
                }
                throw new InvalidDataException(source + ":" + line + ": unexpected line.");
            }
            if (str != null) { str = current.ToString(); Flush(); }
            return result;
        }

        static string Unquote(string s, string source, int line)
        {
            s = s.Trim();
            if (s.Length < 2 || s[0] != '"' || s[s.Length - 1] != '"') throw new InvalidDataException(source + ":" + line + ": expected a quoted string.");
            var sb = new StringBuilder();
            for (int i = 1; i < s.Length - 1; i++)
            {
                char c = s[i];
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= s.Length - 1) throw new InvalidDataException(source + ":" + line + ": a dangling backslash.");
                switch (s[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    default: throw new InvalidDataException(source + ":" + line + ": unknown escape \\" + s[i] + ".");
                }
            }
            return sb.ToString();
        }
    }
}
