using System;
using System.Text;

namespace Yozolab.YoluPainter.Core.Psd
{
    internal sealed class PsdFormatException : Exception
    {
        internal readonly int Offset;
        internal PsdFormatException(string message, int offset) : base(message) { Offset = offset; }
    }

    /// <summary>Length-delimited view of a single owned source buffer; all arithmetic is checked before indexing.</summary>
    internal sealed class PsdReader
    {
        internal readonly byte[] Data;
        internal readonly int End;
        internal int Position;
        internal int Remaining { get { return End - Position; } }
        internal PsdReader(byte[] data, int start, int length)
        {
            if (start < 0 || length < 0 || start > data.Length - length) throw new PsdFormatException("Invalid section bounds.", start);
            Data = data; Position = start; End = start + length;
        }
        internal void Need(int count)
        { if (count < 0 || count > Remaining) throw new PsdFormatException("Truncated or overflowing PSD section.", Position); }
        internal byte U8() { Need(1); return Data[Position++]; }
        internal int U16() { return (U8() << 8) | U8(); }
        internal short I16() { return unchecked((short)U16()); }
        internal uint U32() { return ((uint)U16() << 16) | (uint)U16(); }
        internal int I32() { return unchecked((int)U32()); }
        internal string Key() { Need(4); string s = Encoding.ASCII.GetString(Data, Position, 4); Position += 4; return s; }
        internal void Skip(int count) { Need(count); Position += count; }
        internal PsdReader Slice(int length) { Need(length); var r = new PsdReader(Data, Position, length); Position += length; return r; }
        internal PsdReader Section()
        {
            uint n = U32();
            if (n > int.MaxValue) throw new PsdFormatException("Section length exceeds addressable bytes.", Position - 4);
            return Slice((int)n);
        }
        internal void Zeros(int count)
        { Need(count); while (count-- > 0) if (U8() != 0) throw new PsdFormatException("Non-zero reserved or padding byte.", Position - 1); }
    }

    internal sealed class PsdWriter
    {
        internal readonly byte[] Data;
        internal int Position;
        internal PsdWriter(int length) { Data = new byte[length]; }
        private void Need(int n) { if (n < 0 || Position > Data.Length - n) throw new InvalidOperationException("PSD writer size preflight mismatch."); }
        internal void U8(int x) { Need(1); Data[Position++] = (byte)x; }
        internal void U16(int x) { U8(x >> 8); U8(x); }
        internal void U32(long x) { U16((int)(x >> 16)); U16((int)x); }
        internal void Double(double v) { long bits = BitConverter.DoubleToInt64Bits(v); U32((bits >> 32) & 0xffffffffL); U32(bits & 0xffffffffL); }
        internal void Key(string s) { if (s.Length != 4) throw new ArgumentException("Four-byte key required."); foreach (char c in s) U8(c); }
        internal void Bytes(byte[] bytes) { Need(bytes.Length); Buffer.BlockCopy(bytes, 0, Data, Position, bytes.Length); Position += bytes.Length; }
        internal void Zeros(int n) { Need(n); Position += n; }
        internal void Patch32(int offset, int value)
        { int saved = Position; Position = offset; U32(value); Position = saved; }
    }
}
