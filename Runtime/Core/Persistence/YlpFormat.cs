using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Core.Persistence
{
    /// <summary>.ylp を書いたアプリ（名前・版・Unity の版）。</summary>
    public sealed class YlpWriterInfo
    {
        public string App { get; }
        public string Version { get; }
        public string Unity { get; }
        public YlpWriterInfo(string app, string version, string unity)
        {
            App = Check(app, nameof(app)); Version = Check(version, nameof(version)); Unity = Check(unity, nameof(unity));
        }
        static string Check(string value, string name)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("Empty " + name + ".", name);
            if (value.Length > YlpFormat.MaxText) throw new ArgumentException(name + " is longer than " + YlpFormat.MaxText + " characters.", name);
            return value;
        }
        public override string ToString() => App + " " + Version + " (Unity " + Unity + ")";
    }

    /// <summary>ylp.json の中身: 中身の形式の版と、最後に保存したアプリ・最初に作ったアプリ（分からなければ null）。</summary>
    public sealed class YlpFormatInfo
    {
        public int Format { get; }
        public YlpWriterInfo SavedBy { get; }
        public YlpWriterInfo CreatedBy { get; }
        public YlpFormatInfo(int format, YlpWriterInfo savedBy, YlpWriterInfo createdBy)
        {
            if (format < 1) throw new ArgumentOutOfRangeException(nameof(format));
            Format = format; SavedBy = savedBy; CreatedBy = createdBy;
        }
    }

    /// <summary>開いた .ylp の中身を今の形式にしたもの。</summary>
    public sealed class YlpOpened
    {
        /// <summary>今の形式（<see cref="YlpFormat.Current"/>）の並びのエントリ。ylp.json は含まない（<see cref="Info"/> に読んだ）。</summary>
        public Dictionary<string, byte[]> Files { get; internal set; }
        /// <summary>ファイルに書かれていた形式と書いたアプリ（形式 1 のファイルは書いたアプリが分からない）。</summary>
        public YlpFormatInfo Info { get; internal set; }
        /// <summary>古い形式から今の形式へ移したか（保存すると今の形式で書き、古い YoluPainter では開けなくなる）。</summary>
        public bool Upgraded => Info.Format < YlpFormat.Current;
        /// <summary>この版の YoluPainter が知らないエントリ（保存すると残らない）。</summary>
        public IReadOnlyList<string> UnknownEntries { get; internal set; }
        /// <summary>移すときの知らせ。</summary>
        public IReadOnlyList<string> Notes { get; internal set; }
    }

    /// <summary>
    /// .ylp の中身の形式（Documentation~/YLP_FORMAT.md）。外側（zip・mimetype・manifest の SHA-256）は <see cref="YlpArchive"/>
    /// の層で、ここはその中のエントリの並びと、それを書いたアプリの記録（ylp.json）を受け持つ。
    /// <list type="bullet">
    /// <item>形式 1: ylp.json の無いもの（2026-10-03 より前の YoluPainter）。</item>
    /// <item>形式 2: ylp.json を足した（エントリの並びは形式 1 と同じ）。</item>
    /// </list>
    /// 開くときは <see cref="Open"/> が形式を読み、古い形式なら <see cref="Steps"/> を順に通して今の形式の並びにする（メモリの上だけで、
    /// ファイルは書き換えない）。今より新しい形式は、どのエントリにも触れずに断る。保存は <see cref="Stamp"/> でいつも今の形式で書く。
    /// エントリの中身の版（document.utpaint の版 1〜10 など）は、それぞれの読み手が読み替える（ここでは扱わない）。
    /// </summary>
    public static class YlpFormat
    {
        /// <summary>今の形式。</summary>
        public const int Current = 2;
        /// <summary>形式と書いたアプリの記録（形式 2 から）。</summary>
        public const string InfoName = "ylp.json";
        public const string ViewName = "view.json", BrushName = "brush.json", ThumbnailName = "thumbnail.png", ImportedOriginalName = "imported-original.psd";
        internal const int MaxText = 256, MaxInfoBytes = 64 * 1024;

        /// <summary>形式 k から k+1 へ移す段（Steps[k - 1]）。エントリの並びを変え、知らせを足す。</summary>
        static readonly Action<Dictionary<string, byte[]>, List<string>>[] Steps =
        {
            (files, notes) => { }, // 1 → 2: 並びは同じ（ylp.json を足しただけ）
        };

        /// <summary>エントリの種類。</summary>
        public enum EntryKind
        {
            /// <summary>形式と書いたアプリの記録。</summary>
            Info,
            /// <summary>正本と、描き手の作業そのもの（失うと作業を失う）。</summary>
            Source,
            /// <summary>ウィンドウの状態（モデル・選んだチャンネル・ブラシ）。失うと開いた後の状態が既定に戻る。</summary>
            State,
            /// <summary>正本から作り直せるもの（合成の PNG・サムネイル・焼いたメッシュマップ）。</summary>
            Derived,
        }

        /// <summary>今の形式で知っているエントリなら種類を返す。</summary>
        public static EntryKind? KindOf(string name)
        {
            if (name == InfoName) return EntryKind.Info;
            if (name == YlpArchive.NativeName || name == SelectionBinary.EntryName || name == ImportedOriginalName) return EntryKind.Source;
            if (name == ViewName || name == BrushName) return EntryKind.State;
            if (name == ThumbnailName) return EntryKind.Derived;
            if (name.StartsWith(YlpArchive.CompositeFolder, StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.Ordinal))
            {
                string channel = name.Substring(YlpArchive.CompositeFolder.Length, name.Length - YlpArchive.CompositeFolder.Length - 4);
                return Enum.TryParse(channel, false, out PaintChannel c) && Enum.IsDefined(typeof(PaintChannel), c) && c.ToString() == channel ? EntryKind.Derived : (EntryKind?)null;
            }
            if (name.StartsWith(MeshMapBinary.EntryPrefix, StringComparison.Ordinal) && name.EndsWith(MeshMapBinary.EntrySuffix, StringComparison.Ordinal) && name.IndexOf('/') < 0) return EntryKind.Derived;
            return null;
        }

        /// <summary>
        /// 開いたエントリを今の形式の並びにする。形式が新しすぎる・記録が壊れているときは InvalidDataException（理由と書いたアプリを添える）。
        /// 渡した辞書は変えない。
        /// </summary>
        public static YlpOpened Open(IReadOnlyDictionary<string, byte[]> files)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            var info = files.TryGetValue(InfoName, out var bytes) ? ReadInfo(bytes) : new YlpFormatInfo(1, null, null);
            if (info.Format > Current)
                throw new InvalidDataException("This file uses .ylp format " + info.Format + (info.SavedBy != null ? ", saved by " + info.SavedBy : "") +
                    ". This YoluPainter reads up to format " + Current + "; update YoluPainter to open it. The file was not changed.");
            var upgraded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in files) if (entry.Key != InfoName) upgraded.Add(entry.Key, entry.Value);
            var notes = new List<string>();
            for (int format = info.Format; format < Current; format++) Steps[format - 1](upgraded, notes);
            var unknown = upgraded.Keys.Where(k => KindOf(k) == null).OrderBy(k => k, StringComparer.Ordinal).ToList();
            return new YlpOpened { Files = upgraded, Info = info, UnknownEntries = unknown, Notes = notes };
        }

        /// <summary>保存するエントリに ylp.json（今の形式・保存したアプリ・最初に作ったアプリ）を足す。既にあれば置き換える。</summary>
        public static void Stamp(IDictionary<string, byte[]> files, YlpWriterInfo savedBy, YlpWriterInfo createdBy)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            if (savedBy == null) throw new ArgumentNullException(nameof(savedBy));
            files[InfoName] = WriteInfo(new YlpFormatInfo(Current, savedBy, createdBy));
        }

        // ───────── ylp.json ─────────

        public static byte[] WriteInfo(YlpFormatInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (info.Format < 2) throw new ArgumentException("Format 1 has no ylp.json.", nameof(info));
            if (info.SavedBy == null) throw new ArgumentException("ylp.json records the application that saved the file.", nameof(info));
            var s = new StringBuilder("{\n  \"format\": ").Append(info.Format.ToString(CultureInfo.InvariantCulture));
            void Append(string key, YlpWriterInfo w)
            {
                s.Append(",\n  \"").Append(key).Append("\": { \"app\": ").Append(Quote(w.App)).Append(", \"version\": ").Append(Quote(w.Version))
                 .Append(", \"unity\": ").Append(Quote(w.Unity)).Append(" }");
            }
            Append("savedBy", info.SavedBy);
            if (info.CreatedBy != null) Append("createdBy", info.CreatedBy);
            return Encoding.UTF8.GetBytes(s.Append("\n}\n").ToString());
        }

        /// <summary>ylp.json を読む。知らないキーは読み飛ばす（形式の版が同じなら、足したキーは古い読み手に要らない約束）。</summary>
        public static YlpFormatInfo ReadInfo(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length > MaxInfoBytes) throw new InvalidDataException("ylp.json is larger than " + (MaxInfoBytes >> 10) + " KiB.");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new InvalidDataException("ylp.json is not valid UTF-8."); }
            if (!(new Json(text).ParseDocument() is Dictionary<string, object> root)) throw new InvalidDataException("ylp.json is not a JSON object.");
            if (!root.TryGetValue("format", out var formatValue) || !(formatValue is long format) || format < 2 || format > int.MaxValue)
                throw new InvalidDataException("ylp.json has no valid \"format\" (an integer of at least 2).");
            return new YlpFormatInfo((int)format, Writer(root, "savedBy", required: true), Writer(root, "createdBy", required: false));
        }

        static YlpWriterInfo Writer(Dictionary<string, object> root, string key, bool required)
        {
            if (!root.TryGetValue(key, out var value) || value == null)
            {
                if (required) throw new InvalidDataException("ylp.json has no \"" + key + "\".");
                return null;
            }
            if (!(value is Dictionary<string, object> o)) throw new InvalidDataException("ylp.json \"" + key + "\" is not an object.");
            string Text(string field)
            {
                if (!o.TryGetValue(field, out var v) || !(v is string t) || t.Length == 0 || t.Length > MaxText)
                    throw new InvalidDataException("ylp.json \"" + key + "." + field + "\" is not a string of 1–" + MaxText + " characters.");
                return t;
            }
            return new YlpWriterInfo(Text("app"), Text("version"), Text("unity"));
        }

        static string Quote(string value)
        {
            var s = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') s.Append('\\').Append(c);
                else if (c < 0x20) s.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else s.Append(c);
            }
            return s.Append('"').ToString();
        }

        /// <summary>小さな JSON の読み手（オブジェクト・配列・文字列・整数・小数・true/false/null。深さと長さに上限）。</summary>
        sealed class Json
        {
            readonly string text; int at;
            const int MaxDepth = 16;
            public Json(string text) { this.text = text; }

            public object ParseDocument()
            {
                var value = Value(0); Space();
                if (at != text.Length) throw Error("unexpected text after the value");
                return value;
            }
            InvalidDataException Error(string what) => new InvalidDataException("ylp.json is not valid JSON (" + what + " at character " + at + ").");
            void Space() { while (at < text.Length && (text[at] == ' ' || text[at] == '\t' || text[at] == '\n' || text[at] == '\r')) at++; }
            bool Take(char c) { Space(); if (at < text.Length && text[at] == c) { at++; return true; } return false; }
            void Expect(char c) { if (!Take(c)) throw Error("'" + c + "' expected"); }

            object Value(int depth)
            {
                if (depth > MaxDepth) throw Error("nested too deeply");
                Space();
                if (at >= text.Length) throw Error("a value expected");
                char c = text[at];
                if (c == '{')
                {
                    at++; var o = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (Take('}')) return o;
                    do
                    {
                        Space(); if (at >= text.Length || text[at] != '"') throw Error("a key expected");
                        string key = String(); Expect(':');
                        if (o.ContainsKey(key)) throw Error("duplicate key \"" + key + "\"");
                        o.Add(key, Value(depth + 1));
                    } while (Take(','));
                    Expect('}'); return o;
                }
                if (c == '[')
                {
                    at++; var list = new List<object>();
                    if (Take(']')) return list;
                    do list.Add(Value(depth + 1)); while (Take(','));
                    Expect(']'); return list;
                }
                if (c == '"') return String();
                if (Word("true")) return true;
                if (Word("false")) return false;
                if (Word("null")) return null;
                return Number();
            }
            bool Word(string w) { if (string.CompareOrdinal(text, at, w, 0, w.Length) == 0) { at += w.Length; return true; } return false; }
            string String()
            {
                at++; var s = new StringBuilder();
                while (true)
                {
                    if (at >= text.Length) throw Error("unterminated string");
                    char c = text[at++];
                    if (c == '"') break;
                    if (c < 0x20) throw Error("control character in a string");
                    if (c != '\\') { s.Append(c); if (s.Length > MaxText * 4) throw Error("string too long"); continue; }
                    if (at >= text.Length) throw Error("unterminated escape");
                    char e = text[at++];
                    switch (e)
                    {
                        case '"': s.Append('"'); break; case '\\': s.Append('\\'); break; case '/': s.Append('/'); break;
                        case 'b': s.Append('\b'); break; case 'f': s.Append('\f'); break; case 'n': s.Append('\n'); break;
                        case 'r': s.Append('\r'); break; case 't': s.Append('\t'); break;
                        case 'u':
                            if (at + 4 > text.Length || !ushort.TryParse(text.Substring(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort u)) throw Error("bad \\u escape");
                            s.Append((char)u); at += 4; break;
                        default: throw Error("unknown escape \\" + e);
                    }
                }
                return s.ToString();
            }
            object Number()
            {
                int start = at;
                if (at < text.Length && text[at] == '-') at++;
                while (at < text.Length && (char.IsDigit(text[at]) || text[at] == '.' || text[at] == 'e' || text[at] == 'E' || text[at] == '+' || text[at] == '-')) at++;
                string n = text.Substring(start, at - start);
                if (n.Length == 0) throw Error("a value expected");
                if (long.TryParse(n, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
                if (double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsInfinity(d)) return d;
                throw Error("bad number");
            }
        }
    }
}
