using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Yozolab.YoluPainter.Core
{
    public sealed partial class SelectionMask
    {
        /// <summary>A mask from stored tiles of amounts (TileSize² bytes each, padding zero). Only the codec uses it; it trusts its validation.</summary>
        internal static SelectionMask FromAmountTiles(int width, int height, int tileSize, IEnumerable<KeyValuePair<TileCoord, byte[]>> tiles)
        {
            var mask = new SelectionMask(width, height, tileSize);
            foreach (var tile in tiles)
            {
                var rgba = new byte[tileSize * tileSize * 4];
                for (int i = 0; i < tile.Value.Length; i++) rgba[i * 4 + 3] = tile.Value[i];
                mask.surface.ImportTile(tile.Key, rgba);
            }
            return mask;
        }
    }
}

namespace Yozolab.YoluPainter.Core.Persistence
{
    /// <summary>
    /// The selection as its own file next to the native document (.ylp entry <see cref="EntryName"/>, and recovery checkpoints).
    /// Layout, little-endian: "YLSL", version, width, height, tile size, tile count, then per tile its x, y and TileSize² amounts,
    /// tiles ordered by (y, x) and all-zero tiles left out, so one selection always writes the same bytes. Reading refuses anything
    /// else (a size that is not the document's, a tile outside it, a duplicate or out-of-order tile, set padding, an all-zero
    /// tile, missing or trailing bytes) rather than guessing.
    /// </summary>
    public static class SelectionBinary
    {
        public const string EntryName = "selection.bin";
        public const int Version = 1;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("YLSL");

        public static byte[] Write(SelectionMask selection)
        {
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            int ts = selection.TileSize, n = ts * ts;
            var tiles = new List<KeyValuePair<TileCoord, byte[]>>();
            foreach (var coord in selection.Tiles.OrderBy(c => c.Y).ThenBy(c => c.X))
            {
                var amounts = new byte[n];
                if (selection.CopyTile(coord, amounts) && amounts.Any(a => a != 0)) tiles.Add(new KeyValuePair<TileCoord, byte[]>(coord, amounts));
            }
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write(Version);
                writer.Write(selection.Width); writer.Write(selection.Height); writer.Write(ts); writer.Write(tiles.Count);
                foreach (var tile in tiles) { writer.Write(tile.Key.X); writer.Write(tile.Key.Y); writer.Write(tile.Value); }
                writer.Flush(); return stream.ToArray();
            }
        }

        /// <summary>Reads a selection for <paramref name="document"/>. Throws <see cref="InvalidDataException"/> for anything it does not
        /// write itself; an empty selection reads as null (nothing selected).</summary>
        public static SelectionMask Read(byte[] bytes, PaintDocument document)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (document == null) throw new ArgumentNullException(nameof(document));
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    if (bytes.Length < 24 || !reader.ReadBytes(4).SequenceEqual(Magic)) throw new InvalidDataException("This is not a YoluPainter selection.");
                    int version = reader.ReadInt32();
                    if (version != Version) throw new InvalidDataException("Unsupported selection version " + version + ".");
                    int width = reader.ReadInt32(), height = reader.ReadInt32(), ts = reader.ReadInt32(), count = reader.ReadInt32();
                    if (width != document.Width || height != document.Height || ts != document.TileSize)
                        throw new InvalidDataException("The selection is " + width + "×" + height + " (tile " + ts + ") but the document is " + document.Width + "×" + document.Height + " (tile " + document.TileSize + ").");
                    int tilesX = (width + ts - 1) / ts, tilesY = (height + ts - 1) / ts, n = ts * ts;
                    if (count < 0 || count > (long)tilesX * tilesY) throw new InvalidDataException("The selection has an impossible tile count.");
                    if (bytes.Length != 24 + (long)count * (8 + n)) throw new InvalidDataException("The selection's length does not match its tiles.");
                    var tiles = new List<KeyValuePair<TileCoord, byte[]>>(count);
                    long previous = -1;
                    for (int i = 0; i < count; i++)
                    {
                        int x = reader.ReadInt32(), y = reader.ReadInt32();
                        if (x < 0 || y < 0 || x >= tilesX || y >= tilesY) throw new InvalidDataException("A selection tile lies outside the document.");
                        long order = (long)y * tilesX + x;
                        if (order <= previous) throw new InvalidDataException("Selection tiles are duplicated or out of order.");
                        previous = order;
                        var amounts = reader.ReadBytes(n); bool any = false;
                        int w = Math.Min(ts, width - x * ts), h = Math.Min(ts, height - y * ts);
                        for (int py = 0; py < ts; py++) for (int px = 0; px < ts; px++)
                        {
                            byte a = amounts[py * ts + px]; if (a == 0) continue;
                            if (px >= w || py >= h) throw new InvalidDataException("A selection tile has amounts outside the document.");
                            any = true;
                        }
                        if (!any) throw new InvalidDataException("A selection tile is empty.");
                        tiles.Add(new KeyValuePair<TileCoord, byte[]>(new TileCoord(x, y), amounts));
                    }
                    return count == 0 ? null : SelectionMask.FromAmountTiles(width, height, ts, tiles);
                }
            }
            catch (EndOfStreamException) { throw new InvalidDataException("The selection is truncated."); }
        }
    }
}
