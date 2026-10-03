using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Evaluates filter stacks of one document. The painted source is never changed; results are derived and cached.
    /// <list type="bullet">
    /// <item>The unit of work is an aligned block of document tiles (<see cref="PaintDocument.FilterBlockPixels"/>, 256 pixels by
    /// default). A block is evaluated from its source region grown by the stack's halo (the sum of the
    /// stages' halos), clipped to the canvas; each stage shrinks the region by its own halo, so the last stage produces exactly the
    /// block. Canvas edges repeat the edge pixel of the stage input. Because every stage of every pixel uses the same inputs in
    /// the same order wherever the block starts (blur sums are exact integers), a tile is byte-identical to a full-frame
    /// evaluation.</item>
    /// <item>A global stage (Normalize) first evaluates the stages before it over the whole document (block by block, nothing
    /// full-frame is kept) to get its statistics, then acts like a point stage. Its statistics are cached until the stack or any
    /// source tile changes.</item>
    /// <item>Tiles are cached with a <see cref="FilterStamp"/> (stack revision, latest source tile revision within the halo, or
    /// the whole source revision for a global stack) under <see cref="PaintDocument.FilterCacheBudgetBytes"/>, least recently used
    /// first out. The cache is display state, not history or persistence.</item>
    /// <item>The working memory of one evaluation is estimated before anything is allocated (<see cref="WorkingBytes"/>) and
    /// refused above <see cref="PaintDocument.FilterWorkingBudgetBytes"/>.</item>
    /// <item>A generator stage is a point stage that reads the texture set's mesh maps as the document resolved them
    /// (<see cref="PaintDocument.GeneratorInputs"/>): the source carries that snapshot and its revision, which is part of the stamp,
    /// so new or lost maps never mix with tiles evaluated from the old ones. A generator whose maps are not usable passes its
    /// input through.</item>
    /// </list>
    /// Single-threaded like the document. Stale blocks of a stack are evaluated together, one block per worker thread with
    /// reused working arrays (a single block runs its passes' rows or column strips in parallel instead); every output pixel is
    /// computed independently of the scheduling.</summary>
    internal sealed class FilterEngine
    {
        internal const int MaskKey = -1;

        internal struct Rect
        {
            public int X0, Y0, X1, Y1;
            public Rect(int x0, int y0, int x1, int y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
            public int W { get { return X1 - X0; } }
            public int H { get { return Y1 - Y0; } }
            public int Area { get { return W * H; } }
        }

        /// <summary>What one stack reads: a raster channel, a fill value (or a fill's projected image) or a mask, and the active stages in
        /// order (none for a fill image without filters: its pixels are still evaluated and cached here).</summary>
        internal sealed class Source
        {
            public PaintLayer Layer; public PaintChannel Channel; public bool Mask;
            public SparseTileSurface Surface; public bool IsFill; public Rgba32 Fill;
            /// <summary>A fill channel with an image: the bound projection (its pixels replace the fill value), or null.</summary>
            public FillImageSampler Sampler;
            public FilterEffect[] Chain; public long FilterRevision;
            /// <summary>Generator stacks: the mesh maps by kind as the document resolved them, and that resolution's revision (0
            /// without a generator).</summary>
            public IReadOnlyList<MeshMaps.BakedMeshMap> Maps; public long MapsRevision;
            /// <summary>Generator stacks: where the model root is in the maps' space (shape gradients), from the same resolution.</summary>
            public GeneratorModelFrame Frame;
            public bool Normal { get { return !Mask && Channel == PaintChannel.Normal; } }
            public int Key { get { return Mask ? MaskKey : (int)Channel; } }
        }

        sealed class Entry { public FilterStamp Stamp; public byte[] Bytes; public long Used; }
        sealed class Stats { public FilterStamp Stamp; public int Min, Max; }

        readonly PaintDocument document;
        readonly Dictionary<(Guid, int), Dictionary<TileCoord, Entry>> cache = new Dictionary<(Guid, int), Dictionary<TileCoord, Entry>>();
        readonly Dictionary<(Guid, int, int), Stats> stats = new Dictionary<(Guid, int, int), Stats>();
        long cachedBytes, clock;
        /// <summary>Blocks evaluated since creation (diagnostics and tests).</summary>
        internal long EvaluatedBlocks;
        internal long CachedBytes { get { return cachedBytes; } }

        internal FilterEngine(PaintDocument document) { this.document = document; }

        // ───────────── sources and declarations ─────────────

        internal static Source ContentSource(PaintLayer layer, PaintChannel channel)
        {
            var chain = layer.ActiveChain(channel);
            bool projected = layer.IsProjectedFill(channel); // 画像のチャンネルと、デカールの値のチャンネル
            if (chain.Length == 0 && !projected) return null;
            var s = new Source { Layer = layer, Channel = channel, Chain = chain, FilterRevision = layer.FilterRevision };
            if (layer.Kind == LayerKind.Fill)
            {
                s.IsFill = true; Rgba32 fill; layer.FillValues.TryGetValue(channel, out fill); s.Fill = fill;
                if (projected) s.Sampler = layer.Document.FillSampler(layer, channel);
            }
            else { SparseTileSurface surface; layer.TryGetChannel(channel, out surface); s.Surface = surface; }
            AttachMaps(s, layer.Document);
            return s;
        }
        internal static Source MaskSource(RasterMask mask)
        {
            var chain = mask.ActiveChain(); if (chain.Length == 0) return null;
            var s = new Source { Layer = mask.Owner, Mask = true, Surface = mask.Surface, Chain = chain, FilterRevision = mask.FilterRevision };
            AttachMaps(s, mask.Owner.Document);
            return s;
        }
        static void AttachMaps(Source s, PaintDocument document)
        {
            bool reads = s.Sampler != null && s.Layer.ReadsMeshMapsForFill; // 型の上に投影する塗りつぶしの画像もマップを読む
            foreach (var e in s.Chain) if (e.Settings.IsGenerator) reads = true;
            if (reads) s.Maps = document.GeneratorMapSnapshot(out s.MapsRevision, out s.Frame);
        }
        internal static int Halo(FilterEffect[] chain, int count) { int h = 0; for (int i = 0; i < count; i++) h += chain[i].Settings.HaloPixels; return h; }
        internal static int Expansion(FilterEffect[] chain) { int h = 0; foreach (var e in chain) if (e.Settings.ExpandsCoverage) h += e.Settings.HaloPixels; return h; }
        internal static bool IsGlobal(FilterEffect[] chain, int count) { for (int i = 0; i < count; i++) if (chain[i].Settings.Locality == FilterLocality.Global) return true; return false; }
        static bool PreservesZero(FilterEffect[] chain, int count) { for (int i = 0; i < count; i++) if (!chain[i].Settings.PreservesZero) return false; return true; }
        int TileSize { get { return document.TileSize; } }
        int Tiles(int pixels) { return (pixels + TileSize - 1) / TileSize; }
        int BlockTiles
        {
            get
            {
                int t = Math.Max(1, document.FilterBlockPixels / TileSize);
                return Math.Min(t, Math.Max(Tiles(document.Width), Tiles(document.Height)));
            }
        }

        /// <summary>Working bytes of evaluating count stages over a rect of rectW × rectH (an upper bound: input and output bytes of
        /// each stage, three 16-bit RGBA planes for a blur).</summary>
        internal static long WorkingBytes(FilterEffect[] chain, int count, int rectW, int rectH, int width, int height)
        {
            int after = Halo(chain, count); long peak = Area(after, rectW, rectH, width, height) * 4;
            for (int k = 0; k < count; k++)
            {
                long inA = Area(after, rectW, rectH, width, height); after -= chain[k].Settings.HaloPixels; long outA = Area(after, rectW, rectH, width, height);
                var t = chain[k].Settings.Type;
                long stage = t == FilterType.GaussianBlur || t == FilterType.Sharpen ? inA * 4 + 3 * inA * 8 + outA * 4 : inA * 4; // 16 bit の面 3 枚
                peak = Math.Max(peak, stage);
            }
            return peak;
        }
        static long Area(int margin, int w, int h, int width, int height) { return (long)Math.Min(width, w + 2 * margin) * Math.Min(height, h + 2 * margin); }
        /// <summary>The working bytes of one block of this document for the chain.</summary>
        internal long BlockWorkingBytes(FilterEffect[] chain)
        {
            int side = BlockTiles * TileSize;
            return WorkingBytes(chain, chain.Length, Math.Min(side, document.Width), Math.Min(side, document.Height), document.Width, document.Height);
        }

        /// <summary>True when the stack's output can have content in the tiles [tx0, tx1) × [ty0, ty1). Layer content: alpha only
        /// spreads by the expanding stages (blurs); elsewhere the output is as transparent as the source. Masks: zero-preserving
        /// stacks keep empty regions empty; others (invert, noise, levels with an output black) can produce values anywhere.</summary>
        internal bool MayCover(Source s, int tx0, int ty0, int tx1, int ty1)
        {
            if (s.Mask)
            {
                if (!PreservesZero(s.Chain, s.Chain.Length)) return true;
                int m = Tiles(Halo(s.Chain, s.Chain.Length));
                return AnyTile(s.Surface, tx0 - m, ty0 - m, tx1 + m, ty1 + m);
            }
            if (s.IsFill)
            {
                if (s.Sampler == null) return s.Fill != Rgba32.Transparent;
                int grow = Tiles(Expansion(s.Chain)); // デカールは箱の届くタイルだけ（ぼかしの広がりの分は広げて）
                return s.Sampler.MayCoverTiles(tx0 - grow, ty0 - grow, tx1 + grow, ty1 + grow);
            }
            if (s.Surface == null) return false;
            int e = Tiles(Expansion(s.Chain));
            return AnyTile(s.Surface, tx0 - e, ty0 - e, tx1 + e, ty1 + e);
        }
        bool AnyTile(SparseTileSurface surface, int tx0, int ty0, int tx1, int ty1)
        {
            if (surface == null || surface.TileCount == 0) return false;
            tx0 = Math.Max(0, tx0); ty0 = Math.Max(0, ty0); tx1 = Math.Min(Tiles(document.Width), tx1); ty1 = Math.Min(Tiles(document.Height), ty1);
            for (int y = ty0; y < ty1; y++) for (int x = tx0; x < tx1; x++) if (surface.HasTile(new TileCoord(x, y))) return true;
            return false;
        }
        internal FilterStamp Stamp(Source s, int tx0, int ty0, int tx1, int ty1)
        {
            long input;
            // 塗りつぶしの画像は層の塗りつぶしの版（負にして、値の詰め合わせと重ならないように）。一定の値はその値
            if (s.IsFill) input = s.Sampler != null ? -1 - s.Layer.FillRevision : (long)((uint)s.Fill.R | (uint)s.Fill.G << 8 | (uint)s.Fill.B << 16 | (uint)s.Fill.A << 24);
            else if (s.Surface == null) input = 0;
            else if (IsGlobal(s.Chain, s.Chain.Length)) input = s.Surface.Revision;
            else { int m = Tiles(Halo(s.Chain, s.Chain.Length)); input = s.Surface.MaxTileRevision(tx0 - m, ty0 - m, tx1 + m, ty1 + m); }
            return new FilterStamp(s.FilterRevision, input, s.MapsRevision);
        }

        // ───────────── tiles and pixels (cached) ─────────────

        /// <summary>The stack's output tile (layer pixels: straight RGBA8; masks: hide amount in alpha, RGB zero), padding zero.
        /// False (and zeros) when the output has nothing there.</summary>
        internal bool CopyTile(Source s, TileCoord coord, byte[] destination)
        {
            int length = TileSize * TileSize * 4;
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (destination.Length != length) throw new ArgumentException("Incorrect tile byte length.", nameof(destination));
            if (!MayCover(s, coord.X, coord.Y, coord.X + 1, coord.Y + 1)) { Array.Clear(destination, 0, length); return false; }
            var entry = Lookup(s, coord);
            if (entry != null)
            {
                if (entry.Bytes == null) Array.Clear(destination, 0, length); else Buffer.BlockCopy(entry.Bytes, 0, destination, 0, length);
                return true;
            }
            Rect block; var rgba = EvaluateBlock(s, coord, out block);
            ExtractTile(rgba, block, coord, s.Mask, destination);
            return true;
        }
        /// <summary>One output pixel (see <see cref="CopyTile"/> for the format).</summary>
        internal Rgba32 GetPixel(Source s, int x, int y)
        {
            var coord = new TileCoord(x / TileSize, y / TileSize);
            if (!MayCover(s, coord.X, coord.Y, coord.X + 1, coord.Y + 1)) return Rgba32.Transparent;
            int i = ((y - coord.Y * TileSize) * TileSize + x - coord.X * TileSize) * 4;
            var entry = Lookup(s, coord);
            if (entry != null) return entry.Bytes == null ? Rgba32.Transparent : new Rgba32(entry.Bytes[i], entry.Bytes[i + 1], entry.Bytes[i + 2], entry.Bytes[i + 3]);
            Rect block; var rgba = EvaluateBlock(s, coord, out block);
            int b = ((y - block.Y0) * block.W + x - block.X0) * 4;
            return s.Mask ? new Rgba32(0, 0, 0, rgba[b]) : new Rgba32(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
        }
        Entry Lookup(Source s, TileCoord coord)
        {
            Dictionary<TileCoord, Entry> tiles; Entry entry;
            if (!cache.TryGetValue((s.Layer.Id, s.Key), out tiles) || !tiles.TryGetValue(coord, out entry)) return null;
            if (!entry.Stamp.Equals(Stamp(s, coord.X, coord.Y, coord.X + 1, coord.Y + 1))) return null;
            entry.Used = ++clock; return entry;
        }
        /// <summary>Evaluates the aligned block that holds coord, caches its tiles and returns its pixels (grey for masks). While
        /// the cache has room, stale blocks of the same stack nearest to it are evaluated with it, up to two per worker thread
        /// (2 × <see cref="CoreParallelism.Degree"/>; as many at once as the working budget allows): after a parameter change every block
        /// of the layer is stale, and a thread-pool round trip per pass of one block costs more than the pass itself. Not more: one
        /// request would otherwise evaluate the whole layer, and the display's time-sliced compositing could not stop between blocks.</summary>
        byte[] EvaluateBlock(Source s, TileCoord coord, out Rect block)
        {
            int bt = BlockTiles, side = bt * TileSize;
            block = BlockRect(coord.X / bt, coord.Y / bt);
            long tileBytes = (long)TileSize * TileSize * 4, budget = document.FilterCacheBudgetBytes;
            bool caching = budget >= tileBytes;
            var wanted = new List<Rect> { block };
            if (caching)
            {
                // 一緒に評価するのは、作業者 1 人に 2 つまで（1 回の依頼が層の全部を評価して、表示の時間の予算で区切れなくならないように。
                // 1 つずつでは遅いブロックを待つ回が増え、層の全部を評価し直す時間が延びた）、
                // 頼まれたブロックに近い順（表示は見えている所から順に頼むので、次に頼まれるものから）
                long room = Math.Min(Math.Max(1, budget / ((long)side * side * 4)), Math.Max(1, CoreParallelism.Degree * 2));
                if (room > 1)
                {
                    int cx = coord.X / bt, cy = coord.Y / bt; var near = new List<(int distance, int bx, int by)>();
                    for (int by = 0; by * side < document.Height; by++)
                        for (int bx = 0; bx * side < document.Width; bx++)
                            if (bx != cx || by != cy) near.Add((Math.Max(Math.Abs(bx - cx), Math.Abs(by - cy)), bx, by));
                    near.Sort((a, b) => a.distance != b.distance ? a.distance.CompareTo(b.distance) : a.by != b.by ? a.by.CompareTo(b.by) : a.bx.CompareTo(b.bx));
                    foreach (var (_, bx, by) in near)
                    {
                        if (wanted.Count >= room) break;
                        var r = BlockRect(bx, by);
                        if (Stale(s, r)) wanted.Add(r);
                    }
                }
            }
            var results = EvaluateRects(s, s.Chain.Length, wanted);
            EvaluatedBlocks += wanted.Count;
            if (caching)
            {
                Dictionary<TileCoord, Entry> tiles;
                if (!cache.TryGetValue((s.Layer.Id, s.Key), out tiles)) cache.Add((s.Layer.Id, s.Key), tiles = new Dictionary<TileCoord, Entry>());
                // タイルへの切り出しは並列に、辞書への登録はこのスレッドで
                var jobs = new List<(int block, TileCoord coord, Entry old, byte[] bytes, bool empty)>();
                for (int b = 0; b < wanted.Count; b++)
                {
                    var r = wanted[b];
                    for (int ty = r.Y0 / TileSize; ty < Tiles(r.Y1); ty++)
                        for (int tx = r.X0 / TileSize; tx < Tiles(r.X1); tx++)
                        {
                            var c = new TileCoord(tx, ty); Entry old; tiles.TryGetValue(c, out old);
                            // 古い（無効になった）タイルの配列は使い回す（4K の全面で 64 MiB の確保を毎回しない）
                            jobs.Add((b, c, old, old != null && old.Bytes != null ? old.Bytes : null, false));
                        }
                }
                var done = jobs.ToArray();
                ParallelRange(done.Length, (j0, j1) =>
                {
                    for (int j = j0; j < j1; j++)
                    {
                        var bytes = done[j].bytes ?? new byte[tileBytes];
                        ExtractTile(results[done[j].block], wanted[done[j].block], done[j].coord, s.Mask, bytes);
                        bool empty = true; for (int i = 3; i < bytes.Length && empty; i += 4) empty = bytes[i] == 0 && bytes[i - 1] == 0 && bytes[i - 2] == 0 && bytes[i - 3] == 0;
                        done[j].bytes = bytes; done[j].empty = empty;
                    }
                });
                foreach (var job in done)
                {
                    if (job.old != null && job.old.Bytes != null) cachedBytes -= job.old.Bytes.Length;
                    tiles[job.coord] = new Entry { Stamp = Stamp(s, job.coord.X, job.coord.Y, job.coord.X + 1, job.coord.Y + 1), Bytes = job.empty ? null : job.bytes, Used = ++clock };
                    if (!job.empty) cachedBytes += job.bytes.Length;
                }
                // 頼まれたブロックは最後に使ったことにして、すぐ後の Trim で捨てない
                for (int ty = block.Y0 / TileSize; ty < Tiles(block.Y1); ty++) for (int tx = block.X0 / TileSize; tx < Tiles(block.X1); tx++) tiles[new TileCoord(tx, ty)].Used = ++clock;
                Trim();
            }
            return results[0];
        }
        Rect BlockRect(int bx, int by)
        {
            int side = BlockTiles * TileSize;
            return new Rect(bx * side, by * side, Math.Min(document.Width, (bx + 1) * side), Math.Min(document.Height, (by + 1) * side));
        }
        /// <summary>True when a tile of the block can have output and has no valid cached tile.</summary>
        bool Stale(Source s, Rect r)
        {
            Dictionary<TileCoord, Entry> tiles; cache.TryGetValue((s.Layer.Id, s.Key), out tiles);
            for (int ty = r.Y0 / TileSize; ty < Tiles(r.Y1); ty++)
                for (int tx = r.X0 / TileSize; tx < Tiles(r.X1); tx++)
                {
                    if (!MayCover(s, tx, ty, tx + 1, ty + 1)) continue;
                    Entry e;
                    if (tiles == null || !tiles.TryGetValue(new TileCoord(tx, ty), out e) || !e.Stamp.Equals(Stamp(s, tx, ty, tx + 1, ty + 1))) return true;
                }
            return false;
        }
        /// <summary>Evaluates several rects of the first count stages: one alone with parallel passes, several with one rect per
        /// worker (sequential passes), at most as many at once as fit in the working budget. Global statistics are prepared first
        /// on this thread, so the workers only read them.</summary>
        byte[][] EvaluateRects(Source s, int count, List<Rect> rects)
        {
            for (int k = 0; k < count; k++) if (s.Chain[k].Settings.Locality == FilterLocality.Global) Statistics(s, k);
            var results = new byte[rects.Count][];
            if (rects.Count == 1)
            {
                var one = TakeScratch();
                try { results[0] = EvaluateRect(s, count, rects[0], one); } finally { scratchPool.Add(one); }
                return results;
            }
            long working = 1; foreach (var r in rects) working = Math.Max(working, WorkingBytes(s.Chain, count, r.W, r.H, document.Width, document.Height));
            int degree = (int)Math.Max(1, Math.Min(CoreParallelism.Degree, document.FilterWorkingBudgetBytes / working));
            // 作業者 degree 人が、次のブロックを順に取っていく（Parallel.For の分割に任せると同時に動く作業者が少なかった）
            int nextRect = -1; degree = Math.Min(degree, rects.Count);
            Parallel.For(0, degree, new ParallelOptions { MaxDegreeOfParallelism = degree }, _ =>
            {
                sequentialPasses = true;
                var scratch = TakeScratch();
                try { for (int i; (i = System.Threading.Interlocked.Increment(ref nextRect)) < rects.Count;) results[i] = EvaluateRect(s, count, rects[i], scratch); }
                finally { sequentialPasses = false; scratchPool.Add(scratch); }
            });
            return results;
        }
        [ThreadStatic] static bool sequentialPasses;
        /// <summary>Working arrays kept between evaluations (at most one per worker that ran at once, so never more than the
        /// working budget): a fresh set per block made the editor's garbage collector stop all workers again and again.</summary>
        readonly System.Collections.Concurrent.ConcurrentBag<Scratch> scratchPool = new System.Collections.Concurrent.ConcurrentBag<Scratch>();
        Scratch TakeScratch() { Scratch scratch; return scratchPool.TryTake(out scratch) ? scratch : new Scratch(); }
        /// <summary>Releases the kept working arrays (they are made again on the next evaluation).</summary>
        internal void ReleaseScratch() { Scratch scratch; while (scratchPool.TryTake(out scratch)) { } }
        void ExtractTile(byte[] rgba, Rect block, TileCoord coord, bool mask, byte[] destination)
        {
            int ts = TileSize; Array.Clear(destination, 0, destination.Length);
            int x0 = coord.X * ts, y0 = coord.Y * ts, w = Math.Min(ts, document.Width - x0), h = Math.Min(ts, document.Height - y0);
            for (int y = 0; y < h; y++)
            {
                int src = ((y0 + y - block.Y0) * block.W + x0 - block.X0) * 4, dst = y * ts * 4;
                if (!mask) Buffer.BlockCopy(rgba, src, destination, dst, w * 4);
                else for (int x = 0; x < w; x++) destination[dst + x * 4 + 3] = rgba[src + x * 4];
            }
        }
        /// <summary>Drops least recently used tiles until the cache is within three quarters of its budget.</summary>
        void Trim()
        {
            long budget = document.FilterCacheBudgetBytes;
            if (cachedBytes <= budget) return;
            var all = new List<(long used, (Guid, int) key, TileCoord coord, int bytes)>();
            foreach (var t in cache) foreach (var e in t.Value) all.Add((e.Value.Used, t.Key, e.Key, e.Value.Bytes == null ? 0 : e.Value.Bytes.Length));
            all.Sort((a, b) => a.used.CompareTo(b.used));
            foreach (var item in all)
            {
                if (cachedBytes <= budget * 3 / 4) break;
                cache[item.key].Remove(item.coord); cachedBytes -= item.bytes;
            }
        }
        /// <summary>Forgets the cached tiles and statistics of one stack (its revision changed; they can never be valid again).</summary>
        internal void Forget(Guid layerId, int key)
        {
            Dictionary<TileCoord, Entry> tiles;
            if (cache.TryGetValue((layerId, key), out tiles)) { foreach (var e in tiles.Values) if (e.Bytes != null) cachedBytes -= e.Bytes.Length; cache.Remove((layerId, key)); }
            List<(Guid, int, int)> drop = null;
            foreach (var k in stats.Keys) if (k.Item1 == layerId && k.Item2 == key) (drop ?? (drop = new List<(Guid, int, int)>())).Add(k);
            if (drop != null) foreach (var k in drop) stats.Remove(k);
        }
        internal void Clear() { cache.Clear(); stats.Clear(); cachedBytes = 0; ReleaseScratch(); }

        // ───────────── evaluation ─────────────

        /// <summary>The first count stages over rect (straight RGBA8; masks as opaque grey), row-major from the bottom row.</summary>
        /// <summary>Reused working arrays of one evaluation (the managed allocator serializes large allocations across threads
        /// and every pass would allocate its planes otherwise). Arrays may be longer than needed and are not cleared.</summary>
        internal sealed class Scratch
        {
            readonly ushort[][] ints = new ushort[3][]; readonly byte[][] bytes = new byte[2][];
            public ushort[] Ints(int slot, int length) { var a = ints[slot]; if (a == null || a.Length < length) ints[slot] = a = new ushort[length]; return a; }
            public byte[] Bytes(int slot, int length) { var a = bytes[slot]; if (a == null || a.Length < length) bytes[slot] = a = new byte[length]; return a; }
        }

        internal byte[] EvaluateRect(Source s, int count, Rect rect, Scratch scratch = null)
        {
            if (scratch == null) scratch = new Scratch();
            long working = WorkingBytes(s.Chain, count, rect.W, rect.H, document.Width, document.Height);
            if (working > document.FilterWorkingBudgetBytes)
                throw new InvalidOperationException("Evaluating these filters over " + rect.W + " × " + rect.H + " pixels needs about " + (working >> 20) + " MiB of working memory, more than the filter budget ("
                    + (document.FilterWorkingBudgetBytes >> 20) + " MiB). Nothing was evaluated; use a smaller radius or raise PaintDocument.FilterWorkingBudgetBytes.");
            var after = new int[count + 1];
            for (int k = count - 1; k >= 0; k--) after[k] = after[k + 1] + s.Chain[k].Settings.HaloPixels;
            var cur = Grow(rect, after[0]);
            var buf = Load(s, cur, scratch.Bytes(0, checked(cur.Area * 4))); int slot = 0;
            for (int k = 0; k < count; k++)
            {
                var next = Grow(rect, after[k + 1]);
                var output = Apply(s, k, buf, cur, next, scratch, 1 - slot);
                if (output != buf) slot = 1 - slot;
                buf = output;
                cur = next;
            }
            var result = new byte[checked(rect.Area * 4)]; Buffer.BlockCopy(buf, 0, result, 0, result.Length);
            return result;
        }
        Rect Grow(Rect r, int m)
        { return new Rect(Math.Max(0, r.X0 - m), Math.Max(0, r.Y0 - m), Math.Min(document.Width, r.X1 + m), Math.Min(document.Height, r.Y1 + m)); }

        byte[] Load(Source s, Rect r, byte[] buf)
        {
            int n = r.Area * 4; Array.Clear(buf, 0, n);
            if (s.IsFill)
            {
                if (s.Sampler != null)
                {
                    // 投影した画像: 画素ごとに入力（マップ・画像）だけから決まるので、行の分け方によらず同じバイト
                    var sampler = s.Sampler; int x0 = r.X0, y0 = r.Y0, w = r.W;
                    ParallelRange(r.H, (row0, row1) => sampler.FillRows(x0, y0, w, row0, row1, buf, w));
                    return buf;
                }
                if (s.Fill != Rgba32.Transparent) for (int i = 0; i < n; i += 4) { buf[i] = s.Fill.R; buf[i + 1] = s.Fill.G; buf[i + 2] = s.Fill.B; buf[i + 3] = s.Fill.A; }
                return buf;
            }
            if (s.Mask) for (int i = 3; i < n; i += 4) buf[i] = 255;
            if (s.Surface == null || s.Surface.TileCount == 0) return buf;
            int ts = TileSize; var tile = new byte[ts * ts * 4];
            for (int ty = r.Y0 / ts; ty < Tiles(r.Y1); ty++)
                for (int tx = r.X0 / ts; tx < Tiles(r.X1); tx++)
                {
                    if (!s.Surface.CopyTile(new TileCoord(tx, ty), tile)) continue;
                    int x0 = Math.Max(r.X0, tx * ts), x1 = Math.Min(r.X1, (tx + 1) * ts), y0 = Math.Max(r.Y0, ty * ts), y1 = Math.Min(r.Y1, (ty + 1) * ts);
                    for (int y = y0; y < y1; y++)
                    {
                        int src = ((y - ty * ts) * ts + x0 - tx * ts) * 4, dst = ((y - r.Y0) * r.W + x0 - r.X0) * 4;
                        if (!s.Mask) Buffer.BlockCopy(tile, src, buf, dst, (x1 - x0) * 4);
                        else for (int x = 0; x < x1 - x0; x++) { byte v = tile[src + x * 4 + 3]; buf[dst + x * 4] = v; buf[dst + x * 4 + 1] = v; buf[dst + x * 4 + 2] = v; }
                    }
                }
            return buf;
        }

        byte[] Apply(Source s, int k, byte[] buf, Rect cur, Rect next, Scratch scratch, int outSlot)
        {
            var effect = s.Chain[k]; var f = effect.Settings; double strength = effect.Strength;
            switch (f.Type)
            {
                case FilterType.GaussianBlur: return Blur(s, buf, cur, next, f.Radius, strength, scratch, outSlot);
                case FilterType.Sharpen: return Sharpen(buf, cur, next, f, strength, scratch, outSlot);
                case FilterType.Noise: Noise(buf, cur, f, strength); return buf;
                case FilterType.Levels:
                {
                    var adjust = AdjustmentSettings.Levels(f.InputBlack, f.InputWhite, f.Gamma, f.OutputBlack, f.OutputWhite);
                    var lut = new byte[256]; for (int v = 0; v < 256; v++) lut[v] = adjust.Apply(new Rgba32((byte)v, (byte)v, (byte)v)).R;
                    ApplyLut(buf, cur.Area, lut, strength); return buf;
                }
                case FilterType.Invert:
                {
                    var lut = new byte[256]; for (int v = 0; v < 256; v++) lut[v] = (byte)(255 - v);
                    ApplyLut(buf, cur.Area, lut, strength); return buf;
                }
                case FilterType.Generator: Generate(s, buf, cur, f.Generator, strength); return buf;
                default:
                {
                    var st = Statistics(s, k);
                    var lut = new byte[256];
                    for (int v = 0; v < 256; v++) lut[v] = st.Max > st.Min ? MathUtil.ToByte(Math.Max(0, Math.Min(1, (v - st.Min) / (double)(st.Max - st.Min)))) : (byte)v;
                    ApplyLut(buf, cur.Area, lut, strength); return buf;
                }
            }
        }

        /// <summary>The smallest and largest colour component of the output of the stages before k over the whole document (pixels
        /// with alpha &gt; 0; every pixel of a mask), cached until the stack or the source changes.</summary>
        Stats Statistics(Source s, int k)
        {
            var key = (s.Layer.Id, s.Key, k);
            var stamp = new FilterStamp(s.FilterRevision, s.IsFill ? Stamp(s, 0, 0, 1, 1).Input : s.Surface == null ? 0 : s.Surface.Revision, s.MapsRevision);
            Stats st;
            if (stats.TryGetValue(key, out st) && st.Stamp.Equals(stamp)) return st;
            int min = 255, max = 0; bool any = false;
            int bt = BlockTiles, side = bt * TileSize, e = Tiles(Expansion(s.Chain)), m = Tiles(Halo(s.Chain, k));
            bool zero = PreservesZero(s.Chain, k);
            var rects = new List<Rect>();
            for (int by = 0; by * side < document.Height; by++)
                for (int bx = 0; bx * side < document.Width; bx++)
                {
                    var block = BlockRect(bx, by);
                    int tx0 = block.X0 / TileSize, ty0 = block.Y0 / TileSize, tx1 = Tiles(block.X1), ty1 = Tiles(block.Y1);
                    if (s.Mask && zero && !AnyTile(s.Surface, tx0 - m, ty0 - m, tx1 + m, ty1 + m)) { min = 0; any = true; continue; } // 全部 0 のまま
                    if (!s.Mask && !s.IsFill && !AnyTile(s.Surface, tx0 - e, ty0 - e, tx1 + e, ty1 + e)) continue; // 透明（数えない）
                    rects.Add(block);
                }
            // 前の段を文書全体で評価する（並列のまとまりごとに。全面の画素は持たない）
            int batch = Math.Max(1, CoreParallelism.Degree * 2);
            for (int start = 0; start < rects.Count; start += batch)
                foreach (var rgba in EvaluateRects(s, k, rects.GetRange(start, Math.Min(batch, rects.Count - start))))
                    for (int i = 0; i < rgba.Length; i += 4)
                    {
                        if (rgba[i + 3] == 0) continue;
                        any = true;
                        for (int c = 0; c < 3; c++) { int v = rgba[i + c]; if (v < min) min = v; if (v > max) max = v; }
                    }
            if (!any) { min = 0; max = 0; }
            st = new Stats { Stamp = stamp, Min = min, Max = max };
            stats[key] = st; return st;
        }

        // ───────────── stages ─────────────

        static void ApplyLut(byte[] buf, int area, byte[] lut, double strength)
        {
            ParallelRange(area, (i0, i1) =>
            {
                for (int i = i0 * 4; i < i1 * 4; i += 4)
                {
                    byte r = lut[buf[i]], g = lut[buf[i + 1]], b = lut[buf[i + 2]];
                    if (strength < 1) { r = Lerp(buf[i], r, strength); g = Lerp(buf[i + 1], g, strength); b = Lerp(buf[i + 2], b, strength); }
                    buf[i] = r; buf[i + 1] = g; buf[i + 2] = b;
                }
            });
        }
        void Noise(byte[] buf, Rect r, FilterSettings f, double strength)
        {
            uint seed = Hash((uint)f.Seed); double scale = f.Amount * 127.5; bool mono = f.Monochrome;
            ParallelRange(r.H, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    uint hy = (uint)(r.Y0 + y);
                    for (int x = 0; x < r.W; x++)
                    {
                        int i = (y * r.W + x) * 4; uint hxy = Hash(Hash(seed ^ (uint)(r.X0 + x)) ^ hy);
                        for (int c = 0; c < 3; c++)
                        {
                            double u = (Hash(hxy ^ (uint)(mono ? 0 : c)) >> 8) / 16777215.0 * 2 - 1;
                            double v = Math.Floor(buf[i + c] + scale * u + .5);
                            byte n = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                            buf[i + c] = strength < 1 ? Lerp(buf[i + c], n, strength) : n;
                        }
                    }
                }
            });
        }
        /// <summary>A generator stage in place (a point stage: the rect does not shrink). Masks: the grey hide amount is turned into
        /// visibility, combined and turned back. Layer pixels: each colour component; alpha is unchanged and fully transparent pixels
        /// keep their RGB. Pixels where a map has no data, and the whole stage when its maps are not usable, keep the input.</summary>
        void Generate(Source s, byte[] buf, Rect r, GeneratorSettings g, double strength)
        {
            var bound = BoundGenerator.Bind(g, s.Maps, s.Frame, document.Width, document.Height, out _);
            if (bound == null) return; // 入力のまま（理由は PaintDocument.GetGeneratorStatus が知らせる）
            bool mask = s.Mask; var blend = g.Blend; var unit = MathUtil.ByteUnit;
            ParallelRange(r.H, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                    for (int x = 0; x < r.W; x++)
                    {
                        int i = (y * r.W + x) * 4;
                        if (!mask && buf[i + 3] == 0) continue;
                        if (!bound.TryValue(r.X0 + x, r.Y0 + y, out double v)) continue;
                        if (mask)
                        {
                            byte hide = (byte)(255 - MathUtil.ToByte(BoundGenerator.Combine(blend, 1 - unit[buf[i]], v, strength)));
                            buf[i] = hide; buf[i + 1] = hide; buf[i + 2] = hide;
                        }
                        else for (int c = 0; c < 3; c++) buf[i + c] = MathUtil.ToByte(BoundGenerator.Combine(blend, unit[buf[i + c]], v, strength));
                    }
            });
        }

        /// <summary>A 32-bit integer mix (lowbias32). The noise depends only on the seed and the canvas position.</summary>
        internal static uint Hash(uint h) { unchecked { h ^= h >> 16; h *= 0x7feb352dU; h ^= h >> 15; h *= 0x846ca68bU; h ^= h >> 16; return h; } }

        byte[] Blur(Source s, byte[] buf, Rect cur, Rect next, int radius, double strength, Scratch scratch, int outSlot)
        {
            var q = BoxBlur3(Premultiply(buf, cur.Area, scratch), cur, next, radius, scratch);
            var result = scratch.Bytes(outSlot, checked(next.Area * 4)); bool normal = s.Normal;
            ParallelRange(next.H, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                    for (int x = 0; x < next.W; x++)
                    {
                        int i = (y * next.W + x) * 4, si = ((y + next.Y0 - cur.Y0) * cur.W + x + next.X0 - cur.X0) * 4;
                        int qa = q[i + 3], a = (qa + 127) / 255;
                        var input = new Rgba32(buf[si], buf[si + 1], buf[si + 2], buf[si + 3]);
                        Rgba32 f;
                        if (a == 0) f = new Rgba32(input.R, input.G, input.B, 0); // 見えない画素は入力の色のまま
                        else if (normal) f = NormalMaps.Encode(2.0 * q[i] / qa - 1, 2.0 * q[i + 1] / qa - 1, 2.0 * q[i + 2] / qa - 1, (byte)a);
                        else f = new Rgba32(Unpremultiply(q[i], qa), Unpremultiply(q[i + 1], qa), Unpremultiply(q[i + 2], qa), (byte)a);
                        if (strength < 1) f = Mix(input, f, strength, normal);
                        result[i] = f.R; result[i + 1] = f.G; result[i + 2] = f.B; result[i + 3] = f.A;
                    }
            });
            return result;
        }
        byte[] Sharpen(byte[] buf, Rect cur, Rect next, FilterSettings f, double strength, Scratch scratch, int outSlot)
        {
            var q = BoxBlur3(Premultiply(buf, cur.Area, scratch), cur, next, f.Radius, scratch);
            var result = scratch.Bytes(outSlot, checked(next.Area * 4)); double amount = f.Amount; int threshold = f.Threshold;
            ParallelRange(next.H, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                    for (int x = 0; x < next.W; x++)
                    {
                        int i = (y * next.W + x) * 4, si = ((y + next.Y0 - cur.Y0) * cur.W + x + next.X0 - cur.X0) * 4;
                        int qa = q[i + 3]; byte alpha = buf[si + 3];
                        for (int c = 0; c < 3; c++)
                        {
                            byte input = buf[si + c], v = input;
                            if (alpha != 0 && qa != 0)
                            {
                                double diff = input - q[i + c] * 255.0 / qa;
                                if (Math.Abs(diff) >= threshold)
                                {
                                    double o = Math.Floor(input + amount * diff + .5);
                                    v = (byte)(o < 0 ? 0 : o > 255 ? 255 : o);
                                }
                            }
                            result[i + c] = strength < 1 ? Lerp(input, v, strength) : v;
                        }
                        result[i + 3] = alpha;
                    }
            });
            return result;
        }
        static byte Unpremultiply(int p, int qa) { long v = (2L * p * 255 + qa) / (2L * qa); return (byte)(v > 255 ? 255 : v); }
        static byte Lerp(byte a, byte b, double t) { return (byte)Math.Floor(a + (b - a) * t + .5); }
        /// <summary>Mixes a stage's input and output by the strength: the colour alone when alpha is the same, premultiplied
        /// otherwise (a transparent side does not darken), renormalized vectors on the Normal channel.</summary>
        static Rgba32 Mix(Rgba32 input, Rgba32 output, double t, bool normal)
        {
            if (t >= 1) return output; if (t <= 0) return input;
            if (normal) return NormalMaps.Fade(input, output, t);
            if (input.A == output.A) return new Rgba32(Lerp(input.R, output.R, t), Lerp(input.G, output.G, t), Lerp(input.B, output.B, t), input.A);
            return CpuCompositor.Fade(input, output, t);
        }

        /// <summary>colour × alpha (0..65025) and alpha × 255 per pixel.</summary>
        static ushort[] Premultiply(byte[] buf, int area, Scratch scratch)
        {
            var p = scratch.Ints(0, checked(area * 4));
            ParallelRange(area, (i0, i1) =>
            {
                for (int i = i0 * 4; i < i1 * 4; i += 4)
                { int a = buf[i + 3]; p[i] = (ushort)(buf[i] * a); p[i + 1] = (ushort)(buf[i + 1] * a); p[i + 2] = (ushort)(buf[i + 2] * a); p[i + 3] = (ushort)(a * 255); }
            });
            return p;
        }
        /// <summary>Three box blurs whose radii add up to radius ((r+2)/3, (r+1)/3, r/3), from p over cur to target.</summary>
        ushort[] BoxBlur3(ushort[] p, Rect cur, Rect target, int radius, Scratch scratch)
        {
            int remaining = radius;
            foreach (int r in new[] { (radius + 2) / 3, (radius + 1) / 3, radius / 3 })
            {
                if (r == 0) continue;
                remaining -= r; var next = Grow(target, remaining);
                // 中間は常に 1 番、結果は 0 番と 2 番を交互に（読みと書きが同じ配列にならない）
                int src = p == scratch.Ints(0, 0) ? 0 : 2;
                p = Box(p, cur, next, r, scratch.Ints(1, checked(cur.H * next.W * 4)), scratch.Ints(2 - src, checked(next.Area * 4))); cur = next;
            }
            return p;
        }
        /// <summary>One box blur of radius r (horizontal then vertical), from src over a to b. Each sum is exact; each output is
        /// rounded half up once ((sum + r) / (2r + 1)). Positions outside the canvas read the edge pixel.</summary>
        ushort[] Box(ushort[] src, Rect a, Rect b, int r, ushort[] mid, ushort[] dst)
        {
            int w = 2 * r + 1, aw = a.W, bw = b.W, bh = b.H, width = document.Width, height = document.Height;
            // x / w を掛け算で: inv = ⌈2^40 / w⌉ なら x < 2^40 / w（ここでは x ≤ 173 × 65025 + 86）で floor(x · inv / 2^40) = floor(x / w)
            long inv = ((1L << 40) + w - 1) / w;
            // 窓の出入りの位置（端の画素を繰り返す）は行・列によらないので先に表にする
            var first = new int[w]; var leave = new int[bw]; var enter = new int[bw];
            for (int d = -r; d <= r; d++) first[d + r] = (Clamp(b.X0 + d, width) - a.X0) * 4;
            for (int x = 0; x < bw; x++) { leave[x] = (Clamp(b.X0 + x - r, width) - a.X0) * 4; enter[x] = (Clamp(b.X0 + x + r + 1, width) - a.X0) * 4; }
            ParallelRange(a.H, (r0, r1) => BoxRows(src, mid, aw, bw, r, inv, first, leave, enter, r0, r1));
            var firstRows = new int[w]; var leaveRows = new int[bh]; var enterRows = new int[bh];
            for (int d = -r; d <= r; d++) firstRows[d + r] = (Clamp(b.Y0 + d, height) - a.Y0) * bw * 4;
            for (int y = 0; y < bh; y++) { leaveRows[y] = (Clamp(b.Y0 + y - r, height) - a.Y0) * bw * 4; enterRows[y] = (Clamp(b.Y0 + y + r + 1, height) - a.Y0) * bw * 4; }
            ParallelRange(bw, (c0, c1) => BoxColumns(mid, dst, bw, bh, r, inv, firstRows, leaveRows, enterRows, c0, c1));
            return dst;
        }
        static void BoxRows(ushort[] src, ushort[] mid, int aw, int bw, int r, long inv, int[] first, int[] leave, int[] enter, int r0, int r1)
        {
            for (int row = r0; row < r1; row++)
            {
                int srcRow = row * aw * 4, midRow = row * bw * 4, s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                foreach (int f in first) { int i = srcRow + f; s0 += src[i]; s1 += src[i + 1]; s2 += src[i + 2]; s3 += src[i + 3]; }
                for (int x = 0; x < bw; x++)
                {
                    int o = midRow + x * 4;
                    mid[o] = (ushort)((s0 + r) * inv >> 40); mid[o + 1] = (ushort)((s1 + r) * inv >> 40); mid[o + 2] = (ushort)((s2 + r) * inv >> 40); mid[o + 3] = (ushort)((s3 + r) * inv >> 40);
                    if (x == bw - 1) break;
                    int ir = srcRow + leave[x], ia = srcRow + enter[x];
                    s0 += src[ia] - src[ir]; s1 += src[ia + 1] - src[ir + 1]; s2 += src[ia + 2] - src[ir + 2]; s3 += src[ia + 3] - src[ir + 3];
                }
            }
        }
        static void BoxColumns(ushort[] mid, ushort[] dst, int bw, int bh, int r, long inv, int[] first, int[] leave, int[] enter, int c0, int c1)
        {
            int n = (c1 - c0) * 4, column = c0 * 4; var acc = new int[n];
            foreach (int f in first) { int at = f + column; for (int j = 0; j < n; j++) acc[j] += mid[at + j]; }
            for (int y = 0; y < bh; y++)
            {
                int o = y * bw * 4 + column;
                for (int j = 0; j < n; j++) dst[o + j] = (ushort)((acc[j] + r) * inv >> 40);
                if (y == bh - 1) break;
                int ir = leave[y] + column, ia = enter[y] + column;
                for (int j = 0; j < n; j++) acc[j] += mid[ia + j] - mid[ir + j];
            }
        }
        static int Clamp(int v, int size) { return v < 0 ? 0 : v >= size ? size - 1 : v; }

        /// <summary>Runs body over [0, count) in chunks on the thread pool (sequentially when small). Every index is computed
        /// independently of the chunking.</summary>
        static void ParallelRange(int count, Action<int, int> body)
        {
            if (count <= 0) return;
            int degree = CoreParallelism.Degree;
            if (count < 64 || sequentialPasses || degree <= 1) { body(0, count); return; }
            int chunks = Math.Min(count / 16, degree * 4);
            if (chunks <= 1) { body(0, count); return; }
            int size = (count + chunks - 1) / chunks;
            Parallel.For(0, (count + size - 1) / size, new ParallelOptions { MaxDegreeOfParallelism = degree }, c => body(c * size, Math.Min(count, (c + 1) * size)));
        }
    }
}
