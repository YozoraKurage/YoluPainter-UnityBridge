using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Yozolab.YoluPainter.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>Bounded layer residency on the GPU. Recomposites only what the document reports as changed
    /// (PaintDocument.TryGetChangedTiles), in work blocks of several document tiles, and keeps a bounded, LRU-evicted set of
    /// GPU copies (uploaded layer/mask blocks and the composite below the first changed layer) so that dragging a layer's
    /// opacity, blend mode or visibility, moving it, or editing an adjustment only recomposites from that layer up.
    /// Effects with halos and graph-driven dependency scheduling are not handled here yet.</summary>
    /// <remarks>
    /// <para>合成の単位は「ブロック」（文書のタイル k×k 枚、辺 <see cref="TargetBlockPixels"/> 画素前後）。描画の呼び出しとアップロードの回数を
    /// タイル単位の 1/k² にする。変更記録のタイルを含むブロックを、ブロック全体で合成し直す。</para>
    /// <para>ブロックごとに、最上段の項目それぞれの「署名」（レイヤーの ID・種類・不透明度・合成モード・マスクの設定、ラスターとマスクは
    /// そのブロックのタイルの書き換え番号（<see cref="SparseTileSurface.TileRevision"/>）、塗りつぶしの値、調整の値、グループの中身とクリッピングを
    /// 再帰的に）を覚えておく。前回と最初に違う項目より下の合成結果を「下の写し」として GPU に残し、次からはそこから合成する。
    /// 違う項目以上で使うレイヤー・マスクのブロックは GPU に残す。署名がすべて同じブロックは合成し直さない（結果は同じ）。
    /// 写しはすべて <see cref="ResidentBudgetBytes"/> の内側で、足りなければ古いものから捨て、それでも足りなければ写さずに毎回アップロードする
    /// （結果は同じで、遅くなるだけ）。<see cref="ReleaseResidentCaches"/> で全部捨てられ、しばらく使われない写しは自動で捨てる。</para>
    /// <para>グループは CpuCompositor.EvaluateTile と同じ順・同じ式で GPU 上で合成する。分離グループ（とクリッピングのあるグループ、
    /// クリッピングされたグループ）は 1 つ深い段の作業ブロックで透明から中身を合成し、レイヤーと同じように重ねる。通過グループは下の結果を
    /// 1 つ深い段へ写して中身を重ね、不透明度×マスクでフェードする（不透明度 1 でマスクが無ければ同じ段でそのまま重ねる）。
    /// <see cref="MaxNestedLevels"/> 段と <see cref="NestedLevelBudgetBytes"/> を超える文書では、深いグループが触れるタイルだけ CPU の正本の
    /// 式で合成して上書きする（Backend に書く）。</para>
    /// </remarks>
    internal sealed class TileGpuCompositor : IDisposable
    {
        /// <summary>最上段の下に作ってよい段の数（グループの入れ子の深さ。不透明度 1・マスク無しの通過グループは段を使わない）。</summary>
        internal const int MaxNestedLevels = 8;
        /// <summary>入れ子の段の作業ブロック（1 段 4 枚、blockSize²×4 バイト）の合計の上限。大きなブロックでは段数がこれで減る。</summary>
        internal const long NestedLevelBudgetBytes = 32L << 20;
        /// <summary>作業ブロックの辺の目安（画素）。タイルがこれより小さければ k = 512 / tileSize 枚を 1 辺に並べる（文書より大きくはしない）。</summary>
        internal const int TargetBlockPixels = 512;
        /// <summary>GPU に残す写し（アップロードしたブロックと下の写し）の既定の上限。</summary>
        internal const long DefaultResidentBudgetBytes = 512L << 20;
        /// <summary>この回数の Update のあいだ使われなかった写しは捨てる（ずっと GPU に置きっぱなしにしない）。</summary>
        internal const int IdleUpdatesBeforeRelease = 600;

        /// <summary>1 段の作業ブロック。A/B は段の合成結果のピンポン、ClipA/ClipB はその段のクリッピングのまとまり用。</summary>
        sealed class Level { public RenderTexture A, B, ClipA, ClipB; }
        /// <summary>ブロックの記憶: 前回の最上段の署名、下の写し（Sigs の先頭 BelowIndex 項目を合成した結果）。</summary>
        sealed class BlockState { public string[] Sigs; public RenderTexture Below; public int BelowIndex; public int LastUsed; }
        /// <summary>GPU に残したレイヤー・マスクのブロック。Stamp（ブロック内のタイルの最後の書き換え番号）が今と同じなら有効。</summary>
        sealed class Resident { public Texture2D Texture; public long Stamp; public int LastUsed; }
        /// <summary>層の入力: テクスチャか、塗りつぶしの一定の色。</summary>
        struct Source { public Texture Texture; public bool Constant; public Rgba32 Color; }

        Material material;
        Texture2D cpuFallback, tileUpload;
        readonly Texture2D[] transientLayer = new Texture2D[2], transientMask = new Texture2D[2];
        int transientLayerNext, transientMaskNext;
        RenderTexture composite, cpuTile;
        readonly List<Level> levels = new List<Level>();
        readonly Dictionary<long, BlockState> blocks = new Dictionary<long, BlockState>();
        readonly Dictionary<(long, long), Resident> residents = new Dictionary<(long, long), Resident>();
        byte[] cpuPixels;
        readonly HashSet<TileCoord> previous = new HashSet<TileCoord>();
        public Texture Texture => composite != null ? (Texture)composite : cpuFallback;
        public string Backend { get; private set; } = "Not initialized";
        string gpuBackend;
        /// <summary>Tiles whose composite the last Update refreshed (the change journal's tiles), for diagnostics and tests.</summary>
        public int LastUpdatedTileCount { get; private set; }
        /// <summary>The last Update composited this many tiles on the CPU on the GPU path (groups nested deeper than the
        /// level limit, or <see cref="CompositeGroupsOnCpu"/>). Diagnostics/tests.</summary>
        public int LastCpuTileCount { get; private set; }
        /// <summary>入れ子の段として今確保している作業ブロックの枚数（診断・テスト用）。</summary>
        public int NestedRenderTextureCount { get; private set; }
        /// <summary>この文書・ブロックの大きさで使える入れ子の段の数（<see cref="MaxNestedLevels"/> と予算の小さい方）。</summary>
        public int NestedLevelLimit => blockSize <= 0 ? MaxNestedLevels : (int)Math.Min(MaxNestedLevels, NestedLevelBudgetBytes / (4L * blockSize * blockSize * 4));
        /// <summary>計測・比較用: グループの中身があるタイルを、以前の方式（CPU の正本で合成して載せる）で処理する。</summary>
        internal bool CompositeGroupsOnCpu { get; set; }
        /// <summary>GPU に残す写しの上限（バイト）。0 なら何も残さない（毎回アップロードし、下の写しも作らない）。</summary>
        /// <summary>写しの上限。下げて今の写しが超えたら、すべて捨てる（次の更新で予算の内側に作り直す）。</summary>
        internal long ResidentBudgetBytes
        {
            get => residentBudgetBytes;
            set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); residentBudgetBytes = value; if (ResidentBytes > value) ReleaseResidentCaches(); }
        }
        long residentBudgetBytes = DefaultResidentBudgetBytes;
        /// <summary>今 GPU に残している写しの合計（バイト）。常に <see cref="ResidentBudgetBytes"/> 以下。</summary>
        internal long ResidentBytes { get; private set; }
        /// <summary>作業ブロックの辺（画素）。</summary>
        internal int BlockSize => blockSize;
        // 直近の Update の内訳（診断・テスト・計測用）
        internal int LastBlockCount { get; private set; }
        internal int LastSkippedBlockCount { get; private set; }
        internal int LastBelowReuseCount { get; private set; }
        internal int LastResidentHitCount { get; private set; }
        internal int LastUploadCount { get; private set; }

        int width, height, tileSize, blockTiles, blockSize, updateIndex;
        byte[] tileBuffer, blockBuffer;
        PaintDocument lastDocument;
        PaintChannel lastChannel;
        long lastSerial = -1;
        readonly bool allowGpu, allowCopyTexture;
        bool useCopyTexture;

        /// <param name="allowGpu">false forces the CPU path (used by tests that must run without a graphics device).</param>
        /// <param name="allowCopyTexture">false forces the draw-copy path even where Graphics.CopyTexture is supported (tests).</param>
        public TileGpuCompositor(bool allowGpu = true, bool allowCopyTexture = true) { this.allowGpu = allowGpu; this.allowCopyTexture = allowCopyTexture; }

        public void Update(PaintDocument doc, PaintChannel channel)
        {
            Ensure(doc);
            try { UpdateTiles(doc,channel); }
            catch (Exception ex)
            {
                Dispose(); width=doc.Width;height=doc.Height;tileSize=doc.TileSize;
                cpuFallback=new Texture2D(width,height,TextureFormat.RGBA32,false,true){hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear};
                Backend="CPU composite fallback after GPU failure: "+ex.Message;
                UpdateTiles(doc,channel);
            }
        }

        /// <summary>GPU に残した写し（アップロードしたブロックと下の写し）をすべて捨てる。結果は変わらず、次の更新が遅くなるだけ。</summary>
        public void ReleaseResidentCaches()
        {
            foreach (var r in residents.Values) DestroyTexture(r.Texture);
            residents.Clear();
            foreach (var b in blocks.Values) { Release(b.Below); b.Below = null; b.BelowIndex = 0; }
            ResidentBytes = 0;
        }

        void UpdateTiles(PaintDocument doc,PaintChannel channel)
        {
            var dirty = new HashSet<TileCoord>();
            bool incremental = ReferenceEquals(doc, lastDocument) && channel == lastChannel && doc.TryGetChangedTiles(channel, lastSerial, dirty);
            var occupied = new HashSet<TileCoord>();
            foreach (var layer in doc.Layers)
                foreach (var coord in layer.EnumerateContentTiles(channel)) occupied.Add(coord);
            if (!incremental) { dirty.Clear(); dirty.UnionWith(previous); dirty.UnionWith(occupied); }
            LastUpdatedTileCount = dirty.Count; LastCpuTileCount = 0;
            LastBlockCount = LastSkippedBlockCount = LastBelowReuseCount = LastResidentHitCount = LastUploadCount = 0;

            if (material == null || composite == null) UpdateCpu(doc, channel, dirty, incremental);
            else
            {
                updateIndex++;
                // Normal チャンネルはシェーダーがベクトルとして合成する（CpuCompositor → NormalMaps と同じ式）
                material.SetFloat("_NormalChannel", channel == PaintChannel.Normal ? 1 : 0);
                // 別の文書・チャンネルの署名と下の写しは使えない（アップロードしたブロックは面ごとなので、そのまま使える）
                if (!ReferenceEquals(doc, lastDocument) || channel != lastChannel) ClearBlockStates();
                var plan = CpuCompositor.Plan(doc, channel);
                int needed = LevelsNeeded(plan);
                bool tooDeep = needed > NestedLevelLimit;
                Backend = gpuBackend + (tooDeep ? "; groups nested " + needed + " levels deep exceed the GPU limit of " + NestedLevelLimit + ", so the tiles they touch composite on the CPU" : "");
                var dirtyBlocks = new HashSet<long>();
                foreach (var coord in dirty) dirtyBlocks.Add(BlockKey(coord.X / blockTiles, coord.Y / blockTiles));
                // Graphics.Blit は書き込み先を RenderTexture.active に残すので、呼び出し側の状態を戻す
                var active = RenderTexture.active;
                try
                {
                    foreach (var key in dirtyBlocks)
                    {
                        int bx = (int)(key & 0xffffffff), by = (int)(key >> 32);
                        if ((tooDeep || CompositeGroupsOnCpu) && GroupTouchesBlock(plan, channel, bx, by)) CompositeBlockWithCpuGroups(doc, plan, channel, bx, by);
                        else CompositeBlock(plan, channel, bx, by);
                    }
                }
                finally { RenderTexture.active = active; }
                TrimIdle();
            }

            previous.Clear(); previous.UnionWith(occupied);
            lastDocument = doc; lastChannel = channel; lastSerial = doc.ChangeSerial;
        }
        void UpdateCpu(PaintDocument doc, PaintChannel channel, HashSet<TileCoord> dirty, bool incremental)
        {
            if (!incremental || cpuPixels == null) cpuPixels = doc.Composite(channel);
            else
                foreach (var coord in dirty)
                {
                    int x = coord.X * tileSize, y = coord.Y * tileSize, w = Math.Min(tileSize, width - x), h = Math.Min(tileSize, height - y);
                    var region = CpuCompositor.CompositeRegion(doc, channel, x, y, w, h);
                    for (int row = 0; row < h; row++) Buffer.BlockCopy(region, row * w * 4, cpuPixels, ((y + row) * width + x) * 4, w * 4);
                }
            if (dirty.Count > 0 || !incremental) { cpuFallback.LoadRawTextureData(cpuPixels); cpuFallback.Apply(false, false); }
        }

        // ───────────── ブロックの合成 ─────────────

        static long BlockKey(int bx, int by) => (uint)bx | ((long)by << 32);

        /// <summary>ブロックを合成する。署名が前回とすべて同じなら何もしない。前回と最初に違う項目より下は、下の写しが有効ならそこから始め、
        /// 違う項目の直前で下の写しを取り直す。違う項目以上で使う入力は GPU に残す。</summary>
        void CompositeBlock(IReadOnlyList<CpuCompositor.StackEntry> plan, PaintChannel channel, int bx, int by)
        {
            long key = BlockKey(bx, by);
            if (!blocks.TryGetValue(key, out var state)) blocks.Add(key, state = new BlockState());
            state.LastUsed = updateIndex;
            var sigs = new string[plan.Count];
            for (int i = 0; i < plan.Count; i++) sigs[i] = Signature(plan[i], channel, bx, by);
            int first = state.Sigs == null ? 0 : FirstDifference(state.Sigs, sigs);
            if (state.Sigs != null && first == sigs.Length && state.Sigs.Length == sigs.Length) { LastSkippedBlockCount++; return; }
            LastBlockCount++;

            var top = LevelAt(0);
            RenderTexture current = top.A; int start = 0;
            if (state.Sigs != null && state.Below != null && state.BelowIndex > 0 && state.BelowIndex <= first)
            {
                Blit(state.Below, top.A, 1, null); start = state.BelowIndex; LastBelowReuseCount++;
            }
            else Clear(top.A);
            bool remember = state.Sigs != null && ResidentBudgetBytes > 0;
            int capture = remember && first < plan.Count ? first : -1;
            bool captured = false;
            for (int i = start; i < plan.Count; i++)
            {
                if (i == capture && i > start) { CaptureBelow(state, current, i); captured = true; }
                current = CompositeEntry(plan[i], top, current, 0, channel, bx, by, remember && i >= first, false);
            }
            // 下の写しは「新しい署名の先頭 BelowIndex 項目」の合成でなければならない。取り直さず、先頭が変わっていたら無効にする
            if (!captured && state.BelowIndex > first) state.BelowIndex = 0;
            CopyBlockToComposite(current, bx, by);
            state.Sigs = sigs;
        }

        /// <summary>入れ子が深すぎる（または <see cref="CompositeGroupsOnCpu"/>）とき: グループを飛ばして GPU でブロックを合成し、グループが
        /// 触れるタイルだけ CPU の正本の式で上書きする。触れないタイルではグループの寄与は無い（分離は透明、通過は下のまま）ので正しい。</summary>
        void CompositeBlockWithCpuGroups(PaintDocument doc, IReadOnlyList<CpuCompositor.StackEntry> plan, PaintChannel channel, int bx, int by)
        {
            if (blocks.TryGetValue(BlockKey(bx, by), out var state)) { Release(state.Below); ResidentBytesRemove(state.Below); blocks.Remove(BlockKey(bx, by)); }
            LastBlockCount++;
            var top = LevelAt(0);
            Clear(top.A);
            RenderTexture current = top.A;
            foreach (var entry in plan) current = CompositeEntry(entry, top, current, 0, channel, bx, by, false, true);
            CopyBlockToComposite(current, bx, by);
            for (int ty = by * blockTiles; ty < Math.Min((by + 1) * blockTiles, TilesY); ty++)
                for (int tx = bx * blockTiles; tx < Math.Min((bx + 1) * blockTiles, TilesX); tx++)
                {
                    var coord = new TileCoord(tx, ty);
                    if (GroupTouches(plan, channel, coord)) CompositeTileOnCpu(doc, channel, coord);
                }
        }
        int TilesX => (width + tileSize - 1) / tileSize;
        int TilesY => (height + tileSize - 1) / tileSize;

        static int FirstDifference(string[] before, string[] now)
        {
            int n = Math.Min(before.Length, now.Length);
            for (int i = 0; i < n; i++) if (!string.Equals(before[i], now[i], StringComparison.Ordinal)) return i;
            return n;
        }

        void CaptureBelow(BlockState state, RenderTexture current, int index)
        {
            if (state.Below == null)
            {
                if (!MakeRoom(BlockBytes)) { state.BelowIndex = 0; return; }
                state.Below = MakeRt(blockSize, blockSize, FilterMode.Point); ResidentBytes += BlockBytes;
            }
            Blit(current, state.Below, 1, null);
            state.BelowIndex = index;
        }

        void CopyBlockToComposite(RenderTexture result, int bx, int by)
        {
            int x = bx * blockSize, y = by * blockSize, w = Math.Min(blockSize, width - x), h = Math.Min(blockSize, height - y);
            if (useCopyTexture) Graphics.CopyTexture(result, 0, 0, 0, 0, w, h, composite, 0, 0, x, y);
            else DrawCopy(result, x, y, w, h);
        }

        // ───────────── 署名 ─────────────

        /// <summary>最上段の 1 項目が、このブロックの合成結果に関わるものすべての文字列。同じなら結果も同じ。</summary>
        string Signature(CpuCompositor.StackEntry entry, PaintChannel channel, int bx, int by)
        {
            var sb = new StringBuilder(96);
            AppendSignature(sb, entry, channel, bx, by);
            return sb.ToString();
        }
        void AppendSignature(StringBuilder sb, CpuCompositor.StackEntry entry, PaintChannel channel, int bx, int by)
        {
            var layer = entry.Base;
            sb.Append(layer.Id.ToString("N")).Append('|').Append((int)layer.Kind).Append('|').Append(layer.Opacity.ToString("R", CultureInfo.InvariantCulture))
              .Append('|').Append((int)layer.BlendMode);
            var mask = layer.Mask;
            if (mask == null) sb.Append("|m-");
            else
            {
                sb.Append("|m").Append(mask.Enabled ? 1 : 0).Append(mask.Inverted ? 1 : 0).Append(mask.Density.ToString("R", CultureInfo.InvariantCulture))
                  .Append(mask.IsNeutral ? "n" : "").Append(':').Append(mask.Surface.Id).Append(':').Append(BlockRevision(mask.Surface, bx, by));
            }
            switch (layer.Kind)
            {
                case LayerKind.Raster:
                    if (layer.TryGetChannel(channel, out var surface))
                    {
                        sb.Append("|r").Append(surface.Id).Append(':').Append(BlockRevision(surface, bx, by));
                    }
                    else sb.Append("|r-");
                    break;
                case LayerKind.Fill:
                    var c = layer.GetPixel(channel, 0, 0);
                    sb.Append("|f").Append(c.R).Append(',').Append(c.G).Append(',').Append(c.B).Append(',').Append(c.A);
                    break;
                case LayerKind.Adjustment:
                    var a = layer.Adjustment;
                    sb.Append("|a").Append((int)a.Type).Append(',').Append(R(a.InputBlack)).Append(',').Append(R(a.InputWhite)).Append(',').Append(R(a.Gamma))
                      .Append(',').Append(R(a.OutputBlack)).Append(',').Append(R(a.OutputWhite)).Append(',').Append(R(a.Hue)).Append(',').Append(R(a.Saturation)).Append(',').Append(R(a.Lightness));
                    break;
                case LayerKind.Group:
                    sb.Append("|g[");
                    foreach (var child in entry.Children) { AppendSignature(sb, child, channel, bx, by); sb.Append(';'); }
                    sb.Append(']');
                    break;
            }
            if (entry.ClipEntries.Count > 0)
            {
                sb.Append("|c[");
                foreach (var clip in entry.ClipEntries) { AppendSignature(sb, clip, channel, bx, by); sb.Append(';'); }
                sb.Append(']');
            }
        }
        static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        long BlockRevision(SparseTileSurface surface, int bx, int by)
        { return surface.MaxTileRevision(bx * blockTiles, by * blockTiles, (bx + 1) * blockTiles, (by + 1) * blockTiles); }

        // ───────────── 入れ子の合成（CpuCompositor.EvaluateTile と同じ順と式） ─────────────

        /// <summary>不透明度 1 でマスクが効いていない通過グループは、フェードが中身そのもの（CpuCompositor.Fade の amount ≥ 1）なので
        /// 同じ段で重ねられる。</summary>
        static bool PassesThroughWhole(CpuCompositor.StackEntry e)
        { return e.PassesThrough && e.Base.Opacity >= 1 && (e.Base.Mask == null || e.Base.Mask.IsNeutral); }
        /// <summary>この計画の合成に要る入れ子の段の数（CompositeEntry と同じ規則で数える）。</summary>
        internal static int LevelsNeeded(IReadOnlyList<CpuCompositor.StackEntry> plan)
        {
            int max = 0;
            foreach (var e in plan)
            {
                int n = 0;
                if (e.Base.IsGroup) n = PassesThroughWhole(e) ? LevelsNeeded(e.Children) : 1 + LevelsNeeded(e.Children);
                foreach (var c in e.ClipEntries) if (c.Base.IsGroup) n = Math.Max(n, 1 + LevelsNeeded(c.Children));
                max = Math.Max(max, n);
            }
            return max;
        }
        /// <summary>True when a group of the plan has something in this tile.</summary>
        static bool GroupTouches(IReadOnlyList<CpuCompositor.StackEntry> plan, PaintChannel channel, TileCoord coord)
        {
            foreach (var e in plan)
            {
                if (e.Base.IsGroup && Touches(e, channel, coord.X, coord.Y, coord.X + 1, coord.Y + 1)) return true;
                foreach (var c in e.ClipEntries) if (c.Base.IsGroup && Touches(c, channel, coord.X, coord.Y, coord.X + 1, coord.Y + 1)) return true;
            }
            return false;
        }
        bool GroupTouchesBlock(IReadOnlyList<CpuCompositor.StackEntry> plan, PaintChannel channel, int bx, int by)
        {
            int x0 = bx * blockTiles, y0 = by * blockTiles, x1 = x0 + blockTiles, y1 = y0 + blockTiles;
            foreach (var e in plan)
            {
                if (e.Base.IsGroup && Touches(e, channel, x0, y0, x1, y1)) return true;
                foreach (var c in e.ClipEntries) if (c.Base.IsGroup && Touches(c, channel, x0, y0, x1, y1)) return true;
            }
            return false;
        }
        /// <summary>タイルの範囲 [x0, x1) × [y0, y1) に効くものがあるか。グループは中身（子）だけを見る: 中身が何も無ければ、分離グループの
        /// 結果は透明で（その上にクリッピングされたものも透明に収まる）、通過グループは下をそのまま返すので、飛ばしても結果は同じ。</summary>
        static bool Touches(CpuCompositor.StackEntry e, PaintChannel channel, int x0, int y0, int x1, int y1)
        {
            var layer = e.Base;
            if (layer.IsGroup) { foreach (var child in e.Children) if (Touches(child, channel, x0, y0, x1, y1)) return true; return false; }
            if (layer.Kind != LayerKind.Raster) return true; // Fill と調整はキャンバス全面
            if (!layer.TryGetChannel(channel, out var surface)) return false;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) if (surface.HasTile(new TileCoord(x, y))) return true;
            return false;
        }
        bool TouchesBlock(CpuCompositor.StackEntry e, PaintChannel channel, int bx, int by)
        { return Touches(e, channel, bx * blockTiles, by * blockTiles, (bx + 1) * blockTiles, (by + 1) * blockTiles); }

        RenderTexture CompositeLevel(IReadOnlyList<CpuCompositor.StackEntry> plan, Level level, RenderTexture current, int depth, PaintChannel channel, int bx, int by, bool keep, bool skipGroups)
        {
            foreach (var entry in plan) current = CompositeEntry(entry, level, current, depth, channel, bx, by, keep, skipGroups);
            return current;
        }
        /// <summary>計画の 1 項目を current（level の A か B。下の結果が入っている）の上に重ね、結果の入った作業ブロック（level の A か B）を
        /// 返す。keep: 使う入力を GPU に残す。skipGroups: グループを飛ばす（CPU で上書きするタイル用）。マテリアルの値は、入れ子の合成が
        /// 書き換えるので、各 Blit の直前に設定する。</summary>
        RenderTexture CompositeEntry(CpuCompositor.StackEntry entry, Level level, RenderTexture current, int depth, PaintChannel channel, int bx, int by, bool keep, bool skipGroups)
        {
            var layer = entry.Base;
            if (layer.Kind == LayerKind.Adjustment)
            {
                // 自分の画素は無く、下の合成結果に調整をかける。透明な画素はシェーダーがそのまま返す。
                SetAdjustment(layer.Adjustment); SetLayer(layer.Opacity, layer.BlendMode, layer.Mask, bx, by, keep);
                return Step(current, level, 2, null);
            }
            if (layer.IsGroup && (skipGroups || !TouchesBlock(entry, channel, bx, by))) return current;
            if (entry.PassesThrough)
            {
                if (PassesThroughWhole(entry)) return CompositeLevel(entry.Children, level, current, depth, channel, bx, by, keep, skipGroups);
                // 下の結果を 1 つ深い段へ写し、そこへ中身を重ね、下と中身を不透明度×マスクでフェードする
                var inner = LevelAt(depth + 1);
                Blit(current, inner.A, 1, null);
                var innerResult = CompositeLevel(entry.Children, inner, inner.A, depth + 1, channel, bx, by, keep, skipGroups);
                SetLayer(layer.Opacity, LayerBlendMode.Normal, layer.Mask, bx, by, keep);
                return Step(current, level, 4, innerResult);
            }
            Source source;
            if (layer.IsGroup) source = new Source { Texture = Isolated(entry.Children, depth + 1, channel, bx, by, keep, skipGroups) };
            else if (!TryGetSource(layer, channel, bx, by, keep, out source)) return current; // このブロックに画素が無い（クリッピングのまとまりも透明）
            if (entry.ClipEntries.Count > 0) source = new Source { Texture = BuildClippingGroup(entry, source, level, depth, channel, bx, by, keep, skipGroups) };
            SetLayer(layer.Opacity, ModeOf(layer), layer.Mask, bx, by, keep);
            SetSource(source);
            return Step(current, level, 0, source.Texture);
        }
        /// <summary>分離合成: depth 段の作業ブロックで、透明から中身を合成する。結果はその段の A か B（次にその段を使うまで有効）。</summary>
        RenderTexture Isolated(IReadOnlyList<CpuCompositor.StackEntry> children, int depth, PaintChannel channel, int bx, int by, bool keep, bool skipGroups)
        {
            var level = LevelAt(depth);
            Clear(level.A);
            return CompositeLevel(children, level, level.A, depth, channel, bx, by, keep, skipGroups);
        }
        /// <summary>下地をこの段のまとまり用ブロックへ置き、クリッピングされたものを順に重ねる。まとまりは下地のアルファを保つ。
        /// 戻り値はまとまりの入ったブロック（下地の不透明度・マスク・合成モードでこのあと下に合成する）。クリッピングされたグループの中身は
        /// 1 つ深い段で合成する（下地は置き終えているので、その段を使ってよい）。</summary>
        RenderTexture BuildClippingGroup(CpuCompositor.StackEntry entry, Source baseSource, Level level, int depth, PaintChannel channel, int bx, int by, bool keep, bool skipGroups)
        {
            EnsureClipPair(level);
            if (baseSource.Constant)
            {
                // 一定の色の下地: 透明の上に不透明度 1 で置く（色はそのまま、アルファは下地のもの）
                Clear(level.ClipA);
                SetLayer(1, LayerBlendMode.Normal, null, bx, by, keep); SetSource(baseSource);
                Blit(level.ClipA, level.ClipB, 0, null);
            }
            else Blit(baseSource.Texture, level.ClipB, 1, null); // 下地をそのまま写す
            RenderTexture current = level.ClipB, other = level.ClipA;
            foreach (var clip in entry.ClipEntries)
            {
                var c = clip.Base;
                int pass; Source source = default;
                if (c.Kind == LayerKind.Adjustment) { SetAdjustment(c.Adjustment); pass = 2; }
                else if (c.IsGroup)
                {
                    if (skipGroups || !TouchesBlock(clip, channel, bx, by)) continue;
                    source.Texture = Isolated(clip.Children, depth + 1, channel, bx, by, keep, skipGroups); pass = 3;
                }
                else
                {
                    if (!TryGetSource(c, channel, bx, by, keep, out source)) continue;
                    pass = 3;
                }
                SetLayer(c.Opacity, c.Kind == LayerKind.Adjustment ? c.BlendMode : ModeOf(c), c.Mask, bx, by, keep);
                if (pass == 3) SetSource(source);
                Blit(current, other, pass, source.Texture);
                var swap = current; current = other; other = swap;
            }
            return current;
        }
        static LayerBlendMode ModeOf(PaintLayer layer) { return layer.BlendMode == LayerBlendMode.PassThrough ? LayerBlendMode.Normal : layer.BlendMode; }
        /// <summary>current（level の A か B）を読み、もう片方へ書く。読みと書きが同じブロックになることは無い。</summary>
        RenderTexture Step(RenderTexture current, Level level, int pass, Texture layerTex)
        {
            var next = current == level.A ? level.B : level.A;
            Blit(current, next, pass, layerTex);
            return next;
        }
        /// <summary>_MainTex = source、_LayerTex = layerTex で destination へ描く。source・layerTex と destination は別のテクスチャ。
        /// 描いた後 _LayerTex と一定色の指定を戻し、作業ブロックが入力に残らないようにする。</summary>
        void Blit(Texture source, RenderTexture destination, int pass, Texture layerTex)
        {
            if (ReferenceEquals(source, destination) || ReferenceEquals(layerTex, destination))
                throw new InvalidOperationException("A GPU pass must not read and write the same render texture.");
            if (layerTex != null) material.SetTexture("_LayerTex", layerTex);
            Graphics.Blit(source, destination, material, pass);
            material.SetTexture("_LayerTex", Texture2D.blackTexture);
            material.SetFloat("_LayerConst", 0);
        }
        void SetSource(Source source)
        {
            if (source.Constant)
            {
                material.SetFloat("_LayerConst", 1);
                material.SetVector("_LayerColor", new Vector4(source.Color.R / 255f, source.Color.G / 255f, source.Color.B / 255f, source.Color.A / 255f));
            }
            else material.SetFloat("_LayerConst", 0);
        }

        // ───────────── 入力（アップロードと GPU に残した写し） ─────────────

        /// <summary>レイヤーのこのブロックの画素。塗りつぶしは一定の色（アップロードしない）。ラスターはブロック内にタイルが無ければ false。</summary>
        bool TryGetSource(PaintLayer layer, PaintChannel channel, int bx, int by, bool keep, out Source source)
        {
            source = default;
            if (layer.Kind == LayerKind.Fill)
            {
                source.Constant = true; source.Color = layer.GetPixel(channel, 0, 0);
                return true;
            }
            if (!layer.TryGetChannel(channel, out var surface)) return false;
            bool any = false;
            for (int ty = by * blockTiles; ty < (by + 1) * blockTiles && !any; ty++)
                for (int tx = bx * blockTiles; tx < (bx + 1) * blockTiles && !any; tx++) any = surface.HasTile(new TileCoord(tx, ty));
            if (!any) return false;
            source.Texture = Upload(surface, bx, by, keep, transientLayer, ref transientLayerNext);
            return true;
        }
        /// <summary>面のこのブロックを GPU に置く。keep なら予算の内側で残し、書き換え番号が同じならアップロードし直さない。</summary>
        Texture2D Upload(SparseTileSurface surface, int bx, int by, bool keep, Texture2D[] transient, ref int next)
        {
            long stamp = BlockRevision(surface, bx, by);
            var key = (surface.Id, BlockKey(bx, by));
            if (residents.TryGetValue(key, out var resident))
            {
                resident.LastUsed = updateIndex;
                if (resident.Stamp == stamp) { LastResidentHitCount++; return resident.Texture; }
                FillBlock(surface, bx, by); resident.Texture.LoadRawTextureData(blockBuffer); resident.Texture.Apply(false, false); LastUploadCount++;
                resident.Stamp = stamp;
                return resident.Texture;
            }
            FillBlock(surface, bx, by); LastUploadCount++;
            if (keep && MakeRoom(BlockBytes))
            {
                resident = new Resident { Texture = MakeBlockTexture(), Stamp = stamp, LastUsed = updateIndex };
                resident.Texture.LoadRawTextureData(blockBuffer); resident.Texture.Apply(false, false);
                residents.Add(key, resident); ResidentBytes += BlockBytes;
                return resident.Texture;
            }
            var texture = transient[next]; next = (next + 1) % transient.Length;
            texture.LoadRawTextureData(blockBuffer); texture.Apply(false, false);
            return texture;
        }
        /// <summary>ブロックの画素を blockBuffer に並べる（タイルが無いところと文書の外は 0）。</summary>
        void FillBlock(SparseTileSurface surface, int bx, int by)
        {
            int rowBytes = tileSize * 4, blockRow = blockSize * 4;
            for (int j = 0; j < blockTiles; j++)
                for (int i = 0; i < blockTiles; i++)
                {
                    var coord = new TileCoord(bx * blockTiles + i, by * blockTiles + j);
                    bool present = coord.X < TilesX && coord.Y < TilesY && surface.CopyTile(coord, tileBuffer);
                    for (int row = 0; row < tileSize; row++)
                    {
                        int dst = (j * tileSize + row) * blockRow + i * rowBytes;
                        if (present) Buffer.BlockCopy(tileBuffer, row * rowBytes, blockBuffer, dst, rowBytes);
                        else Array.Clear(blockBuffer, dst, rowBytes);
                    }
                }
        }
        void SetLayer(double opacity, LayerBlendMode mode, RasterMask mask, int bx, int by, bool keep)
        {
            material.SetFloat("_Opacity", (float)opacity); material.SetInt("_BlendMode", (int)mode);
            if (mask != null && !mask.IsNeutral)
            {
                material.SetTexture("_MaskTex", Upload(mask.Surface, bx, by, keep, transientMask, ref transientMaskNext)); // 無いタイル = 何も隠さない（0）
                material.SetVector("_Mask", new Vector4(1, mask.Inverted ? 1 : 0, (float)mask.Density, 0));
            }
            else material.SetVector("_Mask", Vector4.zero);
        }
        void SetAdjustment(AdjustmentSettings a)
        {
            material.SetFloat("_AdjType", (int)a.Type);
            material.SetVector("_AdjLevels", new Vector4((float)a.InputBlack, (float)a.InputWhite, (float)a.Gamma, 0));
            material.SetVector("_AdjOutput", new Vector4((float)a.OutputBlack, (float)a.OutputWhite, 0, 0));
            material.SetVector("_AdjHsl", new Vector4((float)a.Hue, (float)a.Saturation, (float)a.Lightness, 0));
        }

        // ───────────── 予算 ─────────────

        long BlockBytes => 4L * blockSize * blockSize;
        /// <summary>写しを 1 つ足す余地を作る。この Update で使っていないものから、古い順に捨てる。作れなければ false。</summary>
        bool MakeRoom(long bytes)
        {
            if (bytes > ResidentBudgetBytes) return false;
            while (ResidentBytes + bytes > ResidentBudgetBytes)
            {
                (long, long) victimKey = default; Resident victim = null; BlockState victimBlock = null; int oldest = int.MaxValue;
                foreach (var pair in residents) if (pair.Value.LastUsed < updateIndex && pair.Value.LastUsed < oldest) { oldest = pair.Value.LastUsed; victim = pair.Value; victimKey = pair.Key; }
                foreach (var b in blocks.Values) if (b.Below != null && b.LastUsed < updateIndex && b.LastUsed < oldest) { oldest = b.LastUsed; victimBlock = b; victim = null; }
                if (victimBlock != null) { Release(victimBlock.Below); victimBlock.Below = null; victimBlock.BelowIndex = 0; ResidentBytes -= BlockBytes; }
                else if (victim != null) { DestroyTexture(victim.Texture); residents.Remove(victimKey); ResidentBytes -= BlockBytes; }
                else return false;
            }
            return true;
        }
        void ResidentBytesRemove(RenderTexture below) { if (below != null) ResidentBytes -= BlockBytes; }
        /// <summary>しばらく使われていない写しを捨てる。</summary>
        void TrimIdle()
        {
            int limit = updateIndex - IdleUpdatesBeforeRelease;
            List<(long, long)> stale = null;
            foreach (var pair in residents) if (pair.Value.LastUsed < limit) (stale ?? (stale = new List<(long, long)>())).Add(pair.Key);
            if (stale != null) foreach (var key in stale) { DestroyTexture(residents[key].Texture); residents.Remove(key); ResidentBytes -= BlockBytes; }
            foreach (var b in blocks.Values) if (b.Below != null && b.LastUsed < limit) { Release(b.Below); b.Below = null; b.BelowIndex = 0; ResidentBytes -= BlockBytes; }
        }
        void ClearBlockStates()
        {
            foreach (var b in blocks.Values) if (b.Below != null) { Release(b.Below); ResidentBytes -= BlockBytes; }
            blocks.Clear();
        }

        // ───────────── CPU で合成するタイル・書き込み ─────────────

        void CompositeTileOnCpu(PaintDocument doc, PaintChannel channel, TileCoord coord)
        {
            int x = coord.X * tileSize, y = coord.Y * tileSize, tw = Math.Min(tileSize, width - x), th = Math.Min(tileSize, height - y);
            var region = CpuCompositor.CompositeRegion(doc, channel, x, y, tw, th);
            Array.Clear(tileBuffer, 0, tileBuffer.Length);
            for (int row = 0; row < th; row++) Buffer.BlockCopy(region, row * tw * 4, tileBuffer, row * tileSize * 4, tw * 4);
            tileUpload.LoadRawTextureData(tileBuffer); tileUpload.Apply(false, false);
            Blit(tileUpload, cpuTile, 1, null);
            if (useCopyTexture) Graphics.CopyTexture(cpuTile, 0, 0, 0, 0, tw, th, composite, 0, 0, x, y);
            else DrawCopy(cpuTile, x, y, tw, th);
            LastCpuTileCount++;
        }
        /// <summary>CopyTexture の代わりに、作業ブロックの左下 w×h を composite の (x, y) へ描き込む。
        /// Unity は OpenGL 4.3 未満（ARB_copy_image を持っていても）や一部の GLES で CopyTexture を無効にする。</summary>
        void DrawCopy(RenderTexture source, int x, int y, int w, int h)
        {
            var old = RenderTexture.active;
            try
            {
                RenderTexture.active = composite;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, width, 0, height);
                material.SetTexture("_MainTex", source);
                material.SetPass(1);
                float u = w / (float)source.width, v = h / (float)source.height;
                GL.Begin(GL.QUADS);
                GL.TexCoord2(0, 0); GL.Vertex3(x, y, 0);
                GL.TexCoord2(0, v); GL.Vertex3(x, y + h, 0);
                GL.TexCoord2(u, v); GL.Vertex3(x + w, y + h, 0);
                GL.TexCoord2(u, 0); GL.Vertex3(x + w, y, 0);
                GL.End();
                GL.PopMatrix();
            }
            finally
            {
                RenderTexture.active = old;
                // 残すと、次の Blit がこの作業ブロックを書き込み先にしたとき「入力と出力が同じ」と判定する。
                material.SetTexture("_MainTex", null);
            }
        }

        // ───────────── 確保と解放 ─────────────

        /// <summary>depth 段の結果用ピンポンを（無ければ作って）返す。0 段は Ensure で作る。段の上限は呼び出し側（LevelsNeeded）で守る。</summary>
        Level LevelAt(int depth)
        {
            if (depth > NestedLevelLimit) throw new InvalidOperationException("Group nesting exceeds the GPU level limit (" + NestedLevelLimit + ").");
            while (levels.Count <= depth) levels.Add(new Level());
            var level = levels[depth];
            if (level.A == null)
            {
                level.A = MakeRt(blockSize, blockSize, FilterMode.Point); level.B = MakeRt(blockSize, blockSize, FilterMode.Point);
                if (depth > 0) NestedRenderTextureCount += 2;
            }
            return level;
        }
        void EnsureClipPair(Level level)
        {
            if (level.ClipA != null) return;
            level.ClipA = MakeRt(blockSize, blockSize, FilterMode.Point); level.ClipB = MakeRt(blockSize, blockSize, FilterMode.Point);
            if (level != levels[0]) NestedRenderTextureCount += 2;
        }
        Texture2D MakeBlockTexture() => new Texture2D(blockSize, blockSize, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        void Ensure(PaintDocument doc)
        {
            if (width == doc.Width && height == doc.Height && tileSize == doc.TileSize && Texture != null) return;
            Dispose(); width = doc.Width; height = doc.Height; tileSize = doc.TileSize;
            int maxTiles = Math.Max((width + tileSize - 1) / tileSize, (height + tileSize - 1) / tileSize);
            blockTiles = Math.Max(1, Math.Min(TargetBlockPixels / tileSize, maxTiles)); blockSize = blockTiles * tileSize;
            var shader = Shader.Find("Hidden/YoluPainter/TileComposite");
            bool supported = allowGpu && ShaderHealth.IsUsable(shader) && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32);
            useCopyTexture = allowCopyTexture && (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0;
            if (supported)
            {
                try
                {
                    tileBuffer = new byte[tileSize * tileSize * 4]; blockBuffer = new byte[blockSize * blockSize * 4];
                    material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    material.SetTexture("_LayerTex", Texture2D.blackTexture);
                    for (int i = 0; i < transientLayer.Length; i++) { transientLayer[i] = MakeBlockTexture(); transientMask[i] = MakeBlockTexture(); }
                    tileUpload = new Texture2D(tileSize, tileSize, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
                    cpuTile = MakeRt(tileSize, tileSize, FilterMode.Point);
                    EnsureClipPair(LevelAt(0));
                    composite = MakeRt(width, height, FilterMode.Bilinear); Clear(composite);
                    gpuBackend = Backend = "CPU source brush / GPU tiled compositor (encoded-space prototype" + (useCopyTexture ? ")" : ", draw copy)"); return;
                }
                catch (Exception ex) { Dispose(); Backend = "GPU allocation failed: " + ex.Message + "; CPU composite fallback"; }
            }
            else Backend = "CPU composite fallback: GPU render texture format or shader unavailable (or the shader failed to compile)";
            width = doc.Width; height = doc.Height; tileSize = doc.TileSize;
            cpuFallback = new Texture2D(width, height, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        }
        static RenderTexture MakeRt(int w, int h, FilterMode filter)
        {
            var rt = new RenderTexture(w,h,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave, filterMode = filter, wrapMode = TextureWrapMode.Clamp, useMipMap = false };
            if (!rt.Create()) { UnityEngine.Object.DestroyImmediate(rt); throw new InvalidOperationException("RenderTexture.Create failed"); } return rt;
        }
        static void Clear(RenderTexture rt)
        { var old = RenderTexture.active; try { RenderTexture.active = rt; GL.Clear(false, true, Color.clear); } finally { RenderTexture.active = old; } }
        static void Release(RenderTexture rt)
        {
            if (rt == null) return;
            if (RenderTexture.active == rt) RenderTexture.active = null;
            rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
        }
        static void DestroyTexture(Texture2D texture) { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
        public void Dispose()
        {
            ReleaseResidentCaches();
            blocks.Clear();
            foreach (var level in levels) { Release(level.A); Release(level.B); Release(level.ClipA); Release(level.ClipB); }
            levels.Clear(); NestedRenderTextureCount = 0;
            Release(composite); Release(cpuTile);
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            for (int i = 0; i < transientLayer.Length; i++) { DestroyTexture(transientLayer[i]); DestroyTexture(transientMask[i]); transientLayer[i] = transientMask[i] = null; }
            DestroyTexture(tileUpload); DestroyTexture(cpuFallback);
            tileBuffer = null; blockBuffer = null; cpuPixels = null; material = null; tileUpload = null; cpuFallback = null; composite = null; cpuTile = null; previous.Clear();
            lastDocument = null; lastSerial = -1; blockSize = blockTiles = 0;
        }
    }
}
