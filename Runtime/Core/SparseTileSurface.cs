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
        /// <summary>Raised after any pixel write, tile restore, import or clear changes a tile, whether the change is
        /// history-tracked (strokes, undo) or external (SetPixel/ImportTile/Clear, which also raise AfterExternalMutation).</summary>
        internal Action<TileCoord> TileChanged;
        private static long nextId;
        private long revision;
        private readonly Dictionary<TileCoord, long> tileRevisions = new Dictionary<TileCoord, long>();
        /// <summary>A number unique to this surface in this process (caches key copies of its tiles by it).</summary>
        public long Id { get; private set; }
        /// <summary>Increases on every tile change of any kind (the same events as TileChanged), from creation on. A cache of
        /// some tiles is still valid while their <see cref="TileRevision"/> values are unchanged; no pixel has to be read.</summary>
        public long Revision { get { return revision; } }
        /// <summary>The <see cref="Revision"/> at which the tile last changed; 0 if it never changed.</summary>
        public long TileRevision(TileCoord coord) { long r; return tileRevisions.TryGetValue(coord, out r) ? r : 0; }
        /// <summary>The latest <see cref="TileRevision"/> among the tiles [x0, x1) × [y0, y1) (tile coordinates).</summary>
        public long MaxTileRevision(int x0, int y0, int x1, int y1)
        {
            if (tileRevisions.Count == 0) return 0;
            long max = 0, r;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                if (tileRevisions.TryGetValue(new TileCoord(x, y), out r) && r > max) max = r;
            return max;
        }
        private void Touched(TileCoord coord)
        {
            tileRevisions[coord] = ++revision;
            if (TileChanged != null) TileChanged(coord);
        }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int TileSize { get; private set; }
        public int TileCount { get { return tiles.Count; } }
        /// <summary>True when the tile holds data (an absent tile is transparent).</summary>
        public bool HasTile(TileCoord coord) { return tiles.ContainsKey(coord); }
        // タイルの ByteSize の合計。タイルを足す・外す・入れ替える・広げる所で増減する（予算の確認ごとに全タイルを数えない）
        private long allocatedBytes;
        public long AllocatedBytes { get { return allocatedBytes; } }
        public SparseTileSurface(int width, int height, int tileSize = 128)
        {
            if (width <= 0 || width > 32768) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0 || height > 32768) throw new ArgumentOutOfRangeException(nameof(height));
            if (tileSize < 1 || tileSize > 1024) throw new ArgumentOutOfRangeException(nameof(tileSize));
            Width = width; Height = height; TileSize = tileSize;
            Id = System.Threading.Interlocked.Increment(ref nextId);
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
                tiles.Add(coord, tile); allocatedBytes += tile.ByteSize;
            }
            int index = ((y % TileSize) * TileSize + x % TileSize) * 4;
            if (tile.Get(index) == color) return false;
            long size = tile.ByteSize;
            if (size == 4) EnsureGrowth(checked(TileSize * TileSize * 4) - 4);
            tile.Set(index, color, checked(TileSize * TileSize * 4)); allocatedBytes += tile.ByteSize - size;
            Touched(coord);
            return true;
        }
        /// <summary>The stored tile itself (not a copy), or null when the tile is absent. A writer that stays in one tile for
        /// many pixels (BrushStroke) keeps it instead of looking the tile up per pixel; the object is replaced only by
        /// Restore, Compact and Clear, so it must not be kept across those.</summary>
        internal TileStorage PeekTile(TileCoord coord) { TileStorage tile; return tiles.TryGetValue(coord, out tile) ? tile : null; }
        /// <summary><see cref="SetPixelInternal"/> on a tile the caller already looked up (tile is the result of
        /// <see cref="PeekTile"/> and is updated when the write creates it), without the change notification: the caller must
        /// call <see cref="NotifyTileChanged"/> once for every tile this returned true for, before anything else reads the
        /// revisions or the change journal. Same budget checks, in the same order, as SetPixelInternal.</summary>
        internal bool WritePixelQuiet(TileCoord coord, ref TileStorage tile, int index, Rgba32 color)
        {
            if (tile == null)
            {
                if (color == Rgba32.Transparent) return false;
                EnsureGrowth(checked(TileSize * TileSize * 4));
                tile = TileStorage.Uniform(Rgba32.Transparent);
                tiles.Add(coord, tile); allocatedBytes += tile.ByteSize;
            }
            if (tile.Get(index) == color) return false;
            long size = tile.ByteSize;
            if (size == 4) EnsureGrowth(checked(TileSize * TileSize * 4) - 4);
            tile.Set(index, color, checked(TileSize * TileSize * 4)); allocatedBytes += tile.ByteSize - size;
            return true;
        }
        /// <summary>The change notification of pixel writes made with <see cref="WritePixelQuiet"/> (once per tile).</summary>
        internal void NotifyTileChanged(TileCoord coord) { Touched(coord); }
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
            var cleared = new List<TileCoord>(tiles.Keys);
            tiles.Clear(); allocatedBytes = 0;
            foreach (var coord in cleared) Touched(coord);
            if (AfterExternalMutation != null) AfterExternalMutation();
        }
        internal long TileBytesAt(TileCoord coord) { TileStorage tile; return tiles.TryGetValue(coord, out tile) ? tile.ByteSize : 0; }
        internal void EnsureGrowth(long bytes) { if (BeforeSourceGrowth != null && bytes > 0) BeforeSourceGrowth(bytes); }
        internal TileCoord CoordAt(int x, int y) { return new TileCoord(x / TileSize, y / TileSize); }
        internal TileStorage Capture(TileCoord coord)
        { TileStorage tile; return tiles.TryGetValue(coord, out tile) ? tile.Clone() : null; }
        internal void Restore(TileCoord coord, TileStorage snapshot)
        {
            allocatedBytes -= TileBytesAt(coord);
            if (snapshot == null) tiles.Remove(coord);
            else { var restored = snapshot.Clone(); tiles[coord] = restored; allocatedBytes += restored.ByteSize; }
            Touched(coord);
        }
        internal void Compact(TileCoord coord)
        {
            TileStorage tile;
            if (!tiles.TryGetValue(coord, out tile)) return;
            TileStorage compact = tile.Compact();
            allocatedBytes -= tile.ByteSize;
            if (compact == null) tiles.Remove(coord); else { tiles[coord] = compact; allocatedBytes += compact.ByteSize; }
        }
        private void CheckPixel(int x, int y)
        { if (x < 0 || y < 0 || x >= Width || y >= Height) throw new ArgumentOutOfRangeException("pixel", "Pixel is outside the surface."); }
        internal void RequireCoord(TileCoord coord) { CheckCoord(coord); }
        private void CheckCoord(TileCoord coord)
        { if (coord.X < 0 || coord.Y < 0 || (long)coord.X * TileSize >= Width || (long)coord.Y * TileSize >= Height) throw new ArgumentOutOfRangeException(nameof(coord)); }
    }

    internal sealed class TileStorage
    {
        private byte[] data;
        private bool shared;
        private Rgba32 uniform;
        internal long ByteSize { get { return data == null ? 4 : data.Length; } }
        /// <summary>True when Set writes straight into the tile's own buffer (no allocation, no copy-on-write).</summary>
        internal bool Writable { get { return data != null && !shared; } }
        private TileStorage() { }
        internal static TileStorage Uniform(Rgba32 color) { return new TileStorage { uniform = color }; }
        internal static long EstimateBytes(byte[] bytes)
        {
            for (int i = 4; i < bytes.Length; i += 4)
                if (bytes[i] != bytes[0] || bytes[i + 1] != bytes[1] || bytes[i + 2] != bytes[2] || bytes[i + 3] != bytes[3]) return bytes.Length;
            return (bytes[0] | bytes[1] | bytes[2] | bytes[3]) == 0 ? 0 : 4;
        }
        /// <summary>A tile with these pixels: absent (null) when all are transparent zero, uniform when all are equal, otherwise a
        /// copy (the same as copying and then <see cref="Compact"/>, without copying tiles that compact).</summary>
        internal static TileStorage FromBytes(byte[] bytes) { return FromBytes(bytes, Uniformity(bytes)); }
        /// <summary>FromBytes when the caller already knows <see cref="Uniformity"/> of bytes (worked out on a worker thread).</summary>
        internal static TileStorage FromBytes(byte[] bytes, bool uniform)
        {
            if (uniform) return bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 ? null : Uniform(new Rgba32(bytes[0], bytes[1], bytes[2], bytes[3]));
            return new TileStorage { data = (byte[])bytes.Clone() };
        }
        /// <summary>True when every pixel equals the first.</summary>
        internal static bool Uniformity(byte[] bytes)
        {
            byte r = bytes[0], g = bytes[1], b = bytes[2], a = bytes[3];
            for (int i = 4; i < bytes.Length; i += 4) if (bytes[i] != r || bytes[i + 1] != g || bytes[i + 2] != b || bytes[i + 3] != a) return false;
            return true;
        }
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
            if (!Uniformity(data)) return this;
            Rgba32 first = Get(0);
            return first == Rgba32.Transparent ? null : Uniform(first);
        }
        /// <summary>Same(a, FromBytes(bytes)) without making the tile (bytes is a full tile).</summary>
        internal static bool SameAs(TileStorage a, byte[] bytes)
        {
            if (a == null) { for (int i = 0; i < bytes.Length; i++) if (bytes[i] != 0) return false; return true; }
            if (a.data != null) { var x = a.data; if (x.Length != bytes.Length) return false; for (int i = 0; i < x.Length; i++) if (x[i] != bytes[i]) return false; return true; }
            var u = a.uniform;
            for (int p = 0; p < bytes.Length; p += 4) if (bytes[p] != u.R || bytes[p + 1] != u.G || bytes[p + 2] != u.B || bytes[p + 3] != u.A) return false;
            return true;
        }
        /// <summary>Writes the alpha of every pixel into amounts (TileSize² bytes).</summary>
        internal void CopyAlpha(byte[] amounts)
        {
            if (data == null) { for (int i = 0; i < amounts.Length; i++) amounts[i] = uniform.A; return; }
            for (int i = 0; i < amounts.Length; i++) amounts[i] = data[i * 4 + 3];
        }
        internal static bool Same(TileStorage a, TileStorage b)
        {
            if (a == null || b == null) return a == b;
            if (a.data == null && b.data == null) return a.uniform == b.uniform;
            if (a.data != null && b.data != null)
            {
                if (a.data.Length != b.data.Length) return false;
                if (ReferenceEquals(a.data, b.data)) return true;
                var x = a.data; var y = b.data;
                for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
                return true;
            }
            // 片方だけが一様: もう片方の全画素がその色か
            var full = a.data ?? b.data; var u = a.data == null ? a.uniform : b.uniform;
            for (int p = 0; p < full.Length; p += 4) if (full[p] != u.R || full[p + 1] != u.G || full[p + 2] != u.B || full[p + 3] != u.A) return false;
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
