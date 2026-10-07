using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// 受け渡しの JSON の書き手（頼み）。数は有限のものだけ（呼び手が NaN・無限を落とす。ここでは書かずに例外にする）。文字は UTF-8 で書く前提で、
    /// 制御文字と引用符・逆斜線だけを逃がす。読みやすさより、決まった形（キーの順は呼び手の順）を優先する。
    /// </summary>
    internal sealed class JsonWriter
    {
        readonly StringBuilder sb = new StringBuilder();
        // 入れ子ごとに「次の要素の前にカンマが要るか」
        readonly Stack<bool> needComma = new Stack<bool>();
        bool afterName;

        public override string ToString() => sb.ToString();

        void BeforeValue()
        {
            if (afterName) { afterName = false; return; }
            if (needComma.Count == 0) return;
            if (needComma.Peek()) sb.Append(',');
            needComma.Pop(); needComma.Push(true);
        }

        public JsonWriter BeginObject() { BeforeValue(); sb.Append('{'); needComma.Push(false); return this; }
        public JsonWriter EndObject() { needComma.Pop(); sb.Append('}'); return this; }
        public JsonWriter BeginArray() { BeforeValue(); sb.Append('['); needComma.Push(false); return this; }
        public JsonWriter EndArray() { needComma.Pop(); sb.Append(']'); return this; }

        public JsonWriter Name(string name)
        {
            BeforeValue();
            Quote(name);
            sb.Append(':');
            afterName = true;
            return this;
        }

        public JsonWriter String(string value)
        {
            BeforeValue();
            if (value == null) sb.Append("null"); else Quote(value);
            return this;
        }

        public JsonWriter Null() { BeforeValue(); sb.Append("null"); return this; }
        public JsonWriter Bool(bool value) { BeforeValue(); sb.Append(value ? "true" : "false"); return this; }
        public JsonWriter Int(long value) { BeforeValue(); sb.Append(value.ToString(CultureInfo.InvariantCulture)); return this; }

        public JsonWriter Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("JSON has no non-finite numbers.");
            BeforeValue();
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Number(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("JSON has no non-finite numbers.");
            BeforeValue();
            // 0 の負号は書かない（-0 は JSON として読めるが、比べる試験と人の目に紛らわしい）
            sb.Append(value == 0f ? "0" : value.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Numbers(params float[] values)
        {
            BeginArray();
            foreach (var v in values) Number(v);
            return EndArray();
        }

        void Quote(string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>
    /// 受け渡しの JSON の読み手（返事・起きている印）。オブジェクトは <see cref="Dictionary{TKey,TValue}"/>、配列は <see cref="List{T}"/>、数は double、
    /// 文字列・真偽・null。決まりに合わない入力は <see cref="FormatException"/>。入れ子の深さは 64 まで（壊れたファイルで止まらない）。
    /// </summary>
    internal static class JsonReader
    {
        const int MaxDepth = 64;

        public static object Parse(string text)
        {
            int i = 0;
            var value = ReadValue(text, ref i, 0);
            SkipSpace(text, ref i);
            if (i != text.Length) throw new FormatException("Extra characters after the JSON value.");
            return value;
        }

        static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object ReadValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON is nested too deeply.");
            SkipSpace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");
            char c = s[i];
            switch (c)
            {
                case '{': return ReadObject(s, ref i, depth);
                case '[': return ReadArray(s, ref i, depth);
                case '"': return ReadString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber(s, ref i);
                    throw new FormatException("Unexpected character '" + c + "' in JSON.");
            }
        }

        static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("Unexpected word in JSON.");
            i += word.Length;
        }

        static Dictionary<string, object> ReadObject(string s, ref int i, int depth)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }
            while (true)
            {
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("Expected a name in a JSON object.");
                string name = ReadString(s, ref i);
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' in a JSON object.");
                i++;
                result[name] = ReadValue(s, ref i, depth + 1);
                SkipSpace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unexpected end of a JSON object.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new FormatException("Expected ',' or '}' in a JSON object.");
            }
        }

        static List<object> ReadArray(string s, ref int i, int depth)
        {
            var result = new List<object>();
            i++; // [
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }
            while (true)
            {
                result.Add(ReadValue(s, ref i, depth + 1));
                SkipSpace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unexpected end of a JSON array.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new FormatException("Expected ',' or ']' in a JSON array.");
            }
        }

        static string ReadString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // "
            while (true)
            {
                if (i >= s.Length) throw new FormatException("Unterminated JSON string.");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw new FormatException("A control character in a JSON string.");
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("Unterminated JSON escape.");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            throw new FormatException("A bad \\u escape in a JSON string.");
                        sb.Append((char)code); i += 4; break;
                    default: throw new FormatException("A bad escape in a JSON string.");
                }
            }
        }

        static double ReadNumber(string s, ref int i)
        {
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || double.IsNaN(value) || double.IsInfinity(value))
                throw new FormatException("A bad number in JSON.");
            return value;
        }

        // ───────── 読んだ値から取り出す口（型が違えば既定） ─────────

        public static string Str(Dictionary<string, object> o, string name) => o != null && o.TryGetValue(name, out var v) ? v as string : null;

        public static double? Num(Dictionary<string, object> o, string name) => o != null && o.TryGetValue(name, out var v) && v is double d ? d : (double?)null;

        public static bool? Bool(Dictionary<string, object> o, string name) => o != null && o.TryGetValue(name, out var v) && v is bool b ? b : (bool?)null;

        public static Dictionary<string, object> Obj(Dictionary<string, object> o, string name) => o != null && o.TryGetValue(name, out var v) ? v as Dictionary<string, object> : null;

        public static List<object> Arr(Dictionary<string, object> o, string name) => o != null && o.TryGetValue(name, out var v) ? v as List<object> : null;
    }
}
