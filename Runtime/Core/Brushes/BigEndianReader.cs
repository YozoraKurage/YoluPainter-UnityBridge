using System;
using System.Text;

namespace Yozolab.YoluPainter.Core.Brushes
{
    /// <summary>Bounds-checked big-endian reader over a byte array. Running past the end throws BrushImportException, so a
    /// truncated file is reported instead of half-read.</summary>
    internal sealed class BigEndianReader
    {
        readonly byte[] data;
        public int Position;
        public BigEndianReader(byte[] data, int position = 0) { this.data = data ?? throw new ArgumentNullException(nameof(data)); Position = position; }
        public int Length => data.Length;
        public int Remaining => data.Length - Position;
        void Need(int count) { if (count < 0 || count > Remaining) throw new BrushImportException("The file is truncated (needed " + count + " more bytes at offset " + Position + ")."); }
        public byte U8() { Need(1); return data[Position++]; }
        public ushort U16() { Need(2); var v = (ushort)(data[Position] << 8 | data[Position + 1]); Position += 2; return v; }
        public short I16() { return (short)U16(); }
        public uint U32() { Need(4); uint v = (uint)(data[Position] << 24 | data[Position + 1] << 16 | data[Position + 2] << 8 | data[Position + 3]); Position += 4; return v; }
        public int I32() { return (int)U32(); }
        public long I64() { long hi = U32(), lo = U32(); return hi << 32 | lo; }
        public double Double() { return BitConverter.Int64BitsToDouble(I64()); }
        public byte[] Bytes(int count) { Need(count); var b = new byte[count]; Buffer.BlockCopy(data, Position, b, 0, count); Position += count; return b; }
        public void Skip(int count) { Need(count); Position += count; }
        public string Ascii(int count) { return Encoding.ASCII.GetString(Bytes(count)); }
        /// <summary>A length that must fit in the rest of the file (guards against absurd allocations from corrupt data).</summary>
        public int Count(uint value, int elementSize, string what)
        {
            if (value > int.MaxValue || (long)value * elementSize > Remaining)
                throw new BrushImportException("Invalid " + what + " (" + value + ") at offset " + Position + ": the file is truncated or corrupt.");
            return (int)value;
        }
    }

    internal static class PackBits
    {
        /// <summary>Decodes one PackBits-compressed row of exactly width bytes.</summary>
        public static byte[] DecodeRow(byte[] source, int width)
        {
            var row = new byte[width]; int s = 0, d = 0;
            while (d < width)
            {
                if (s >= source.Length) throw new BrushImportException("A compressed row ended early.");
                int n = (sbyte)source[s++];
                if (n >= 0)
                {
                    int count = n + 1;
                    if (s + count > source.Length || d + count > width) throw new BrushImportException("A compressed row overflows its width.");
                    Buffer.BlockCopy(source, s, row, d, count); s += count; d += count;
                }
                else if (n != -128)
                {
                    int count = 1 - n;
                    if (s >= source.Length || d + count > width) throw new BrushImportException("A compressed row overflows its width.");
                    byte v = source[s++]; for (int i = 0; i < count; i++) row[d++] = v;
                }
            }
            return row;
        }
    }
}
