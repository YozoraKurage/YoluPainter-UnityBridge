using System;
using System.Collections.Generic;
using System.Text;

namespace Yozolab.YoluPainter.Core.Brushes
{
    /// <summary>A parsed Photoshop ActionDescriptor value (the format of ABR "desc" sections and many PSD blocks), read
    /// from the published "Descriptor structure" of the Photoshop File Formats Specification. Unknown value types stop the
    /// parse (their length cannot be known), and the caller reports that instead of guessing.</summary>
    public abstract class DescriptorValue { }

    public sealed class DescriptorObject : DescriptorValue
    {
        public string Name { get; internal set; } = "";
        public string ClassId { get; internal set; } = "";
        /// <summary>Items in file order (keys can repeat in principle; the first wins in TryGet).</summary>
        public List<KeyValuePair<string, DescriptorValue>> Items { get; } = new List<KeyValuePair<string, DescriptorValue>>();
        public DescriptorValue this[string key] { get { foreach (var i in Items) if (i.Key == key) return i.Value; return null; } }
        public T Get<T>(string key) where T : DescriptorValue { return this[key] as T; }
        public double? Number(string key) { var v = this[key]; return v is DescriptorNumber n ? n.Value : v is DescriptorInteger i ? i.Value : (double?)null; }
        public bool? Bool(string key) { return (this[key] as DescriptorBool)?.Value; }
        public string Text(string key) { return (this[key] as DescriptorText)?.Value; }
    }
    public sealed class DescriptorList : DescriptorValue { public List<DescriptorValue> Items { get; } = new List<DescriptorValue>(); }
    public sealed class DescriptorNumber : DescriptorValue { public double Value; public string Unit = ""; }
    public sealed class DescriptorInteger : DescriptorValue { public long Value; }
    public sealed class DescriptorBool : DescriptorValue { public bool Value; }
    public sealed class DescriptorText : DescriptorValue { public string Value = ""; }
    public sealed class DescriptorEnum : DescriptorValue { public string Type = "", Value = ""; }
    public sealed class DescriptorClass : DescriptorValue { public string Name = "", ClassId = ""; }
    /// <summary>References, aliases and raw data: kept as bytes (not interpreted).</summary>
    public sealed class DescriptorRaw : DescriptorValue { public string Type = ""; public byte[] Data = new byte[0]; }

    public static class ActionDescriptorReader
    {
        const int MaxDepth = 32, MaxItems = 100000;

        /// <summary>Reads a descriptor (name, class id, items) at the reader's position.</summary>
        internal static DescriptorObject ReadDescriptor(BigEndianReader r, int depth = 0)
        {
            if (depth > MaxDepth) throw new BrushImportException("Descriptor nesting is too deep.");
            var o = new DescriptorObject { Name = UnicodeString(r), ClassId = Key(r) };
            uint count = r.U32(); if (count > MaxItems) throw new BrushImportException("Too many descriptor items.");
            for (uint i = 0; i < count; i++) { string key = Key(r); o.Items.Add(new KeyValuePair<string, DescriptorValue>(key, Value(r, r.Ascii(4), depth))); }
            return o;
        }

        static DescriptorValue Value(BigEndianReader r, string type, int depth)
        {
            switch (type)
            {
                case "Objc": case "GlbO": return ReadDescriptor(r, depth + 1);
                case "VlLs":
                {
                    var list = new DescriptorList(); uint n = r.U32(); if (n > MaxItems) throw new BrushImportException("Too many list items.");
                    for (uint i = 0; i < n; i++) list.Items.Add(Value(r, r.Ascii(4), depth + 1));
                    return list;
                }
                case "UntF": { string unit = r.Ascii(4); return new DescriptorNumber { Unit = unit, Value = r.Double() }; }
                case "doub": return new DescriptorNumber { Value = r.Double() };
                case "long": return new DescriptorInteger { Value = r.I32() };
                case "comp": return new DescriptorInteger { Value = r.I64() };
                case "bool": return new DescriptorBool { Value = r.U8() != 0 };
                case "TEXT": return new DescriptorText { Value = UnicodeString(r) };
                case "enum": return new DescriptorEnum { Type = Key(r), Value = Key(r) };
                case "type": case "GlbC": return new DescriptorClass { Name = UnicodeString(r), ClassId = Key(r) };
                case "alis": case "tdta": return new DescriptorRaw { Type = type, Data = r.Bytes(r.Count(r.U32(), 1, type + " length")) };
                case "obj ": return Reference(r);
                default: throw new BrushImportException("Unsupported descriptor value type '" + type + "' at offset " + (r.Position - 4) + ".");
            }
        }

        static DescriptorValue Reference(BigEndianReader r)
        {
            int start = r.Position; uint n = r.U32(); if (n > MaxItems) throw new BrushImportException("Too many reference items.");
            for (uint i = 0; i < n; i++)
            {
                string form = r.Ascii(4);
                switch (form)
                {
                    case "prop": UnicodeString(r); Key(r); Key(r); break;
                    case "Clss": UnicodeString(r); Key(r); break;
                    case "Enmr": UnicodeString(r); Key(r); Key(r); Key(r); break;
                    case "rele": UnicodeString(r); Key(r); r.I32(); break;
                    case "Idnt": case "indx": r.I32(); break;
                    case "name": UnicodeString(r); Key(r); UnicodeString(r); break;
                    default: throw new BrushImportException("Unsupported reference form '" + form + "'.");
                }
            }
            var raw = new DescriptorRaw { Type = "obj " }; int length = r.Position - start; r.Position = start; raw.Data = r.Bytes(length); return raw;
        }

        internal static string Key(BigEndianReader r)
        {
            uint length = r.U32();
            return length == 0 ? r.Ascii(4) : r.Ascii(r.Count(length, 1, "key length"));
        }
        internal static string UnicodeString(BigEndianReader r)
        {
            int units = r.Count(r.U32(), 2, "string length");
            var bytes = r.Bytes(units * 2);
            return Encoding.BigEndianUnicode.GetString(bytes).TrimEnd('\0');
        }
    }
}
