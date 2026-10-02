using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Sparse, bottom-left-origin RGBA8 surface. Empty tiles have no allocation; uniform tiles hold four bytes.
    /// Detached export/import prevents a caller from modifying pixels behind the history manager. Not thread-safe.</summary>
    public sealed class SparseTileSurface
    {
        private readonly Dictionary<TileCoord, TileStorage> tiles = new Dictionary<TileCoord, TileStorage>();
        internal Action BeforeExternalMutation;
        internal Action AfterExternalMutation;
        internal Action<long> BeforeSourceGrowth;
        /// <summary>Raised after a history-tracked pixel write or tile restore changes a tile. External mutations
        /// (SetPixel/ImportTile/Clear) are reported through AfterExternalMutation instead.</summary>
        internal Action<TileCoord> TileChanged;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int TileSize { get; private set; }
        public int TileCount { get { return tiles.Count; } }
        public long AllocatedBytes
        {
            get { long bytes = 0; foreach (var tile in tiles.Values) bytes += tile.ByteSize; return bytes; }
        }
        public SparseTileSurface(int width, int height, int tileSize = 128)
        {
            if (width <= 0 || width > 32768) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0 || height > 32768) throw new ArgumentOutOfRangeException(nameof(height));
            if (tileSize < 1 || tileSize > 1024) throw new ArgumentOutOfRangeException(nameof(tileSize));
            Width = width; Height = height; TileSize = tileSize;
        }
        public Rgba32 GetPixel(int x, int y)
        {
            CheckPixel(x, y);
            TileStorage tile;
            if (!tiles.TryGetValue(CoordAt(x, y), out tile)) return Rgba32.Transparent;
            return tile.Get(((y % TileSize) * TileSize + x % TileSize) * 4);
        }
        /// <summary>Direct import/edit operation. An owning document clears undo/redo; use BrushStroke to retain undo.</summary>
        public void SetPixel(int x, int y, Rgba32 color)
        {
            CheckPixel(x, y);
            if (GetPixel(x, y) == color) return;
            if (BeforeExternalMutation != null) BeforeExternalMutation();
            SetPixelInternal(x, y, color);
            Compact(CoordAt(x, y));
            if (AfterExternalMutation != null) AfterExternalMutation();
        }
        internal bool SetPixelInternal(int x, int y, Rgba32 color)
        {
            TileCoord coord = CoordAt(x, y); TileStorage tile;
            if (!tiles.TryGetValue(coord, out tile))
            {
                if (color == Rgba32.Transparent) return false;
                EnsureGrowth(checked(TileSize * TileSize * 4));
                tile = TileStorage.Uniform(Rgba32.Transparent);
                tiles.Add(coord, tile);
            }
            int index = ((y % TileSize) * TileSize + x % TileSize) * 4;
            if (tile.Get(index) == color) return false;
            if (tile.ByteSize == 4) EnsureGrowth(checked(TileSize * TileSize * 4) - 4);
            tile.Set(index, color, checked(TileSize * TileSize * 4));
            if (TileChanged != null) TileChanged(coord);
            return true;
        }
        /// <summary>Imports a complete tile. Padding outside the canvas must be zero; caller buffers are copied.</summary>
        public void ImportTile(TileCoord coord, byte[] bytes)
        {
            CheckCoord(coord);
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length != checked(TileSize * TileSize * 4)) throw new ArgumentException("Incorrect tile byte length.", nameof(bytes));
            int startX = coord.X * TileSize, startY = coord.Y * TileSize;
            for (int y = 0; y < TileSize; y++) for (int x = 0; x < TileSize; x++)
            {
                if (startX + x < Width && startY + y < Height) continue;
                int p = (y * TileSize + x) * 4;
                if ((bytes[p] | bytes[p + 1] | bytes[p + 2] | bytes[p + 3]) != 0)
                    throw new ArgumentException("Edge tile padding must be zero.", nameof(bytes));
            }
            EnsureGrowth(TileStorage.EstimateBytes(bytes) - TileBytesAt(coord));
            TileStorage next = TileStorage.FromBytes(bytes);
            TileStorage current = Capture(coord);
            if (TileStorage.Same(current, next)) return;
            if (BeforeExternalMutation != null) BeforeExternalMutation();
            Restore(coord, next);
            if (AfterExternalMutation != null) AfterExternalMutation();
        }
        /// <summary>Copies one tile into a caller-owned buffer of TileSize*TileSize*4 bytes (row-major RGBA8, lowest Y first,
        /// edge padding zero). Absent tiles are written as zero and return false. The surface never keeps the buffer.</summary>
        public bool CopyTile(TileCoord coord, byte[] destination)
        {
            CheckCoord(coord);
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int length = checked(TileSize * TileSize * 4);
            if (destination.Length != length) throw new ArgumentException("Incorrect tile byte length.", nameof(destination));
            TileStorage tile;
            if (!tiles.TryGetValue(coord, out tile)) { Array.Clear(destination, 0, length); return false; }
            tile.CopyTo(destination, length);
            return true;
        }
        /// <summary>Stable Y-then-X snapshot of occupied coordinates. Copies only keys, never pixel buffers.
        /// The returned snapshot is read-only and unaffected by later surface edits.</summary>
        public IEnumerable<TileCoord> EnumerateTileCoordinates()
        {
            var coordinates = new List<TileCoord>(tiles.Keys); coordinates.Sort(); return coordinates.AsReadOnly();
        }
        /// <summary>Stable Y-then-X enumeration of detached full tile buffers.</summary>
        public IEnumerable<TileData> EnumerateTiles()
        {
            foreach (TileCoord coord in EnumerateTileCoordinates())
                yield return new TileData(coord, tiles[coord].ToBytes(TileSize * TileSize * 4));
        }
        public void Clear()
        {
            if (tiles.Count == 0) return;
            if (BeforeExternalMutation != null) BeforeExternalMutation();
            tiles.Clear();
            if (AfterExternalMutation != null) AfterExternalMutation();
        }
        internal long TileBytesAt(TileCoord coord) { TileStorage tile; return tiles.TryGetValue(coord, out tile) ? tile.ByteSize : 0; }
        internal void EnsureGrowth(long bytes) { if (BeforeSourceGrowth != null && bytes > 0) BeforeSourceGrowth(bytes); }
        internal TileCoord CoordAt(int x, int y) { return new TileCoord(x / TileSize, y / TileSize); }
        internal TileStorage Capture(TileCoord coord)
        { TileStorage tile; return tiles.TryGetValue(coord, out tile) ? tile.Clone() : null; }
        internal void Restore(TileCoord coord, TileStorage snapshot)
        {
            if (snapshot == null) tiles.Remove(coord);
            else tiles[coord] = snapshot.Clone();
            if (TileChanged != null) TileChanged(coord);
        }
        internal void Compact(TileCoord coord)
        {
            TileStorage tile;
            if (!tiles.TryGetValue(coord, out tile)) return;
            TileStorage compact = tile.Compact();
            if (compact == null) tiles.Remove(coord); else tiles[coord] = compact;
        }
        private void CheckPixel(int x, int y)
        { if (x < 0 || y < 0 || x >= Width || y >= Height) throw new ArgumentOutOfRangeException("pixel", "Pixel is outside the surface."); }
        private void CheckCoord(TileCoord coord)
        { if (coord.X < 0 || coord.Y < 0 || (long)coord.X * TileSize >= Width || (long)coord.Y * TileSize >= Height) throw new ArgumentOutOfRangeException(nameof(coord)); }
    }

    internal sealed class TileStorage
    {
        private byte[] data;
        private bool shared;
        private Rgba32 uniform;
        internal long ByteSize { get { return data == null ? 4 : data.Length; } }
        private TileStorage() { }
        internal static TileStorage Uniform(Rgba32 color) { return new TileStorage { uniform = color }; }
        internal static long EstimateBytes(byte[] bytes)
        {
            for (int i = 4; i < bytes.Length; i += 4)
                if (bytes[i] != bytes[0] || bytes[i + 1] != bytes[1] || bytes[i + 2] != bytes[2] || bytes[i + 3] != bytes[3]) return bytes.Length;
            return (bytes[0] | bytes[1] | bytes[2] | bytes[3]) == 0 ? 0 : 4;
        }
        internal static TileStorage FromBytes(byte[] bytes) { return new TileStorage { data = (byte[])bytes.Clone() }.Compact(); }
        internal Rgba32 Get(int index) { return data == null ? uniform : new Rgba32(data[index], data[index + 1], data[index + 2], data[index + 3]); }
        internal void Set(int index, Rgba32 color, int byteLength)
        {
            if (data == null) { data = new byte[byteLength]; Fill(data, uniform); }
            else if (shared) { data = (byte[])data.Clone(); shared = false; }
            data[index] = color.R; data[index + 1] = color.G; data[index + 2] = color.B; data[index + 3] = color.A;
        }
        // Snapshots share immutable buffers; the first subsequent write detaches once per tile.
        internal TileStorage Clone()
        {
            if (data == null) return Uniform(uniform);
            shared = true; return new TileStorage { data = data, shared = true };
        }
        internal byte[] ToBytes(int length)
        {
            if (data != null) return (byte[])data.Clone();
            var result = new byte[length]; Fill(result, uniform); return result;
        }
        internal void CopyTo(byte[] destination, int length)
        {
            if (data != null) Buffer.BlockCopy(data, 0, destination, 0, length);
            else if (uniform == Rgba32.Transparent) Array.Clear(destination, 0, length);
            else Fill(destination, uniform);
        }
        internal TileStorage Compact()
        {
            if (data == null) return uniform == Rgba32.Transparent ? null : this;
            Rgba32 first = Get(0);
            for (int i = 4; i < data.Length; i += 4) if (Get(i) != first) return this;
            return first == Rgba32.Transparent ? null : Uniform(first);
        }
        internal static bool Same(TileStorage a, TileStorage b)
        {
            if (a == null || b == null) return a == b;
            if (a.data == null && b.data == null) return a.uniform == b.uniform;
            int length = a.data != null ? a.data.Length : b.data.Length;
            if (a.data != null && b.data != null && a.data.Length != b.data.Length) return false;
            for (int p = 0; p < length; p += 4) if (a.Get(p) != b.Get(p)) return false;
            return true;
        }
        private static void Fill(byte[] bytes, Rgba32 color)
        {
            if (color == Rgba32.Transparent) return;
            for (int i = 0; i < bytes.Length; i += 4)
            { bytes[i] = color.R; bytes[i + 1] = color.G; bytes[i + 2] = color.B; bytes[i + 3] = color.A; }
        }
    }
}
