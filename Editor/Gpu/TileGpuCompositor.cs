using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Yozolab.YoluPainter.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>表示の合成（2D の表示と 3D のプレビューに出す合成）をどこで行うか。個人の設定「Display compositing」。
    /// 保存・書き出し・復旧に入る合成はこれに依らず、いつも CPU の正本（YlpContent / PaintDocument.Composite）。</summary>
    internal enum CompositorBackend
    {
        /// <summary>GPU で合成できれば GPU、ただし GPU がソフトウェアの描画（llvmpipe など）なら CPU（<see cref="TileGpuCompositor.ResolveAutomatic"/>）。</summary>
        Automatic = 0,
        /// <summary>GPU で合成する。使えなければ CPU に落ちて <see cref="TileGpuCompositor.FellBackToCpu"/> を立てる。</summary>
        Gpu = 1,
        /// <summary>CPU（Core の CpuCompositor）で合成し、変わったタイルだけを表示のテクスチャへ送る。</summary>
        Cpu = 2,
    }

    /// <summary>Bounded layer residency on the GPU. Recomposites only what the document reports as changed
    /// (PaintDocument.TryGetChangedTiles), in work blocks of several document tiles, and keeps a bounded, LRU-evicted set of
    /// GPU copies (uploaded layer/mask blocks and the composite below the first changed layer) so that dragging a layer's
    /// opacity, blend mode or visibility, moving it, or editing an adjustment only recomposites from that layer up.
    /// Layers and masks with active filters upload their filtered tiles (evaluated on the CPU by Core with halos, the same bytes
    /// as the CPU reference); their signatures and resident stamps include the filter stamp of the block grown by the halo.
    /// Graph-driven dependency scheduling is not handled here yet.</summary>
    /// <remarks>
    /// <para>合成の単位は「ブロック」（文書のタイル k×k 枚、辺 <see cref="TargetBlockPixels"/> 画素前後）。描画の呼び出しとアップロードの回数を
    /// タイル単位の 1/k² にする。変更記録のタイルを含むブロックを、ブロック全体で合成し直す。</para>
    /// <para>ブロックごとに、最上段の項目それぞれの「署名」（レイヤーの ID・種類・不透明度・合成モード・マスクの設定、ラスターとマスクは
    /// そのブロックのタイルの書き換え番号（<see cref="SparseTileSurface.TileRevision"/>）、塗りつぶしの値、調整の値、グループの中身とクリッピングを
    /// 再帰的に）を覚えておく。前回と最初に違う項目より下の合成結果を「下の写し」として GPU に残し、次からはそこから合成する。
    /// 違う項目以上で使うレイヤー・マスクのブロックは GPU に残す。署名がすべて同じブロックは合成し直さない（結果は同じ）。
    /// 写しはすべて <see cref="ResidentBudgetBytes"/> の内側で、足りなければ古いものから捨て、それでも足りなければ写さずに毎回アップロードする
    /// （結果は同じで、遅くなるだけ）。<see cref="ReleaseResidentCaches"/> で全部捨てられ、しばらく使われない写しは自動で捨てる。</para>
    /// <para>グループは CpuCompositor.EvaluateSpan と同じ順・同じ式で GPU 上で合成する。分離グループ（とクリッピングのあるグループ、
    /// クリッピングされたグループ）は 1 つ深い段の作業ブロックで透明から中身を合成し、レイヤーと同じように重ねる。通過グループは下の結果を
    /// 1 つ深い段へ写して中身を重ね、不透明度×マスクでフェードする（不透明度 1 でマスクが無ければ同じ段でそのまま重ねる）。
    /// <see cref="MaxNestedLevels"/> 段と <see cref="NestedLevelBudgetBytes"/> を超える文書では、深いグループが触れるタイルだけ CPU の正本の
    /// 式で合成して上書きする（Backend に書く）。</para>
    /// <para>CPU の経路（<see cref="CompositorBackend.Cpu"/> を選んだとき、GPU が使えないとき）は Core の CpuCompositor.CompositeRegions で
    /// 合成する。ブロックごとに GPU の経路と同じ署名を覚え、最初に違う項目より下の合成結果をメモリに「下の写し」として残す（予算は同じ
    /// <see cref="ResidentBudgetBytes"/>）。写しのすぐ上から変わったブロックは、変わったタイルだけ（<see cref="CoverTiles"/> の長方形ごと）を
    /// 写しから上だけ合成し、写しを取り直すときはブロック全体を合成する。結果は表示の RenderTexture へ、変わったタイルだけ（ブロックが
    /// 丸ごと変わっていればブロックで）小さな Texture2D に載せて CopyTexture（無ければ描き込み）で写す。グラフィックスデバイスや
    /// RenderTexture が使えなければ、CPU 側の 1 枚の Texture2D に全面を載せ直す（<see cref="CompositePath.CpuFrame"/>）。どの経路でも表示の
    /// 画素は CPU の正本とバイト単位で同じ。</para>
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
        sealed class Resident { public Texture2D Texture; public FilterStamp Stamp; public int LastUsed; }
        /// <summary>層の入力: テクスチャか、塗りつぶしの一定の色。</summary>
        struct Source { public Texture Texture; public bool Constant; public Rgba32 Color; }

        /// <summary>合成と表示の経路。</summary>
        internal enum CompositePath
        {
            /// <summary>GPU で合成する（ブロック・写し）。</summary>
            Gpu,
            /// <summary>CPU で合成し、変わったタイルだけを表示の RenderTexture へ写す（CopyTexture か、描き込み）。</summary>
            CpuTiles,
            /// <summary>CPU で合成し、CPU 側の Texture2D へ全面を載せ直す（グラフィックスデバイス・RenderTexture・写す手段のどれかが無いとき）。</summary>
            CpuFrame,
        }

        Material material;
        Texture2D cpuFallback, tileUpload;
        readonly Texture2D[] transientLayer = new Texture2D[2], transientMask = new Texture2D[2];
        int transientLayerNext, transientMaskNext;
        /// <summary>CPU の経路で表示へ送るときの載せ台（タイル 1 枚分とブロック 1 つ分。2 枚ずつ交互に使う）。</summary>
        readonly Texture2D[] tileStage = new Texture2D[2], blockStage = new Texture2D[2];
        int tileStageNext, blockStageNext;
        RenderTexture composite, cpuTile;
        readonly List<Level> levels = new List<Level>();
        readonly Dictionary<long, BlockState> blocks = new Dictionary<long, BlockState>();
        readonly Dictionary<(long, long), Resident> residents = new Dictionary<(long, long), Resident>();
        /// <summary><see cref="CompositePath.CpuFrame"/> の全面の画素（<see cref="CompositePath.CpuTiles"/> は全面を持たない）。</summary>
        byte[] cpuPixels;
        /// <summary>CPU の経路の表示に、前回までの合成が全部入っているか（false なら次は全面）。</summary>
        bool cpuValid;
        /// <summary>載せ台から表示の RenderTexture へ CopyTexture で写せるか（false なら TileComposite の写しのパスで描き込む）。</summary>
        bool copyStageToDisplay;
        readonly HashSet<TileCoord> previous = new HashSet<TileCoord>();
        public Texture Texture => composite != null ? (Texture)composite : cpuFallback;
        public string Backend { get; private set; } = "Not initialized";
        string gpuBackend;
        /// <summary>今の合成と表示の経路（最初の <see cref="Update"/> で決まる）。</summary>
        internal CompositePath Path { get; private set; } = CompositePath.CpuFrame;
        /// <summary>GPU で合成するつもり（自動か GPU）だったのに使えず、CPU で合成している。理由は <see cref="Backend"/>。</summary>
        public bool FellBackToCpu { get; private set; }
        /// <summary>作ったときの「表示の合成」の選択。</summary>
        internal CompositorBackend Preference => preference;
        /// <summary>直近の Update が表示のテクスチャへ送ったタイルの数（CPU の経路。<see cref="CompositePath.CpuFrame"/> は全面のタイルの数）。</summary>
        internal int LastSentTileCount { get; private set; }
        /// <summary>直近の Update が CPU の経路で呼んだ CompositeRegions の回数（置き場 <see cref="CpuWaveBlocks"/> 個ぶんごとに 1 回）。</summary>
        internal int LastCpuCompositeCalls { get; private set; }
        /// <summary>直近の Update が CPU の経路で全部のブロックを透明から合成し直したか（変更記録が使えないとき）。</summary>
        internal bool LastCpuFullFrame { get; private set; }
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
        /// <summary>残す写しの上限（バイト。GPU の経路は GPU のテクスチャ、CPU の経路はメモリの下の写し）。0 なら何も残さない（毎回
        /// アップロードし、下の写しも作らない）。下げて今の写しが超えたら、すべて捨てる（次の更新で予算の内側に作り直す）。</summary>
        internal long ResidentBudgetBytes
        {
            get => residentBudgetBytes;
            set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); residentBudgetBytes = value; if (ResidentBytes > value) ReleaseResidentCaches(); }
        }
        long residentBudgetBytes = DefaultResidentBudgetBytes;
        /// <summary>今残している写しの合計（バイト）。常に <see cref="ResidentBudgetBytes"/> 以下。</summary>
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
        readonly CompositorBackend preference;
        bool useCopyTexture;

        /// <param name="allowGpu">true: GPU compositing where usable (whatever the settings say; GPU tests use this). false forces CPU
        /// compositing into a CPU-side Texture2D without touching the graphics device (tests that must run without one).</param>
        /// <param name="allowCopyTexture">false forces the draw-copy path even where Graphics.CopyTexture is supported (tests).</param>
        public TileGpuCompositor(bool allowGpu = true, bool allowCopyTexture = true)
        { this.allowGpu = allowGpu; this.allowCopyTexture = allowCopyTexture; preference = allowGpu ? CompositorBackend.Gpu : CompositorBackend.Cpu; }
        /// <summary>「表示の合成」の選択に従う合成器（ウィンドウが設定から作る）。CPU を選んでも、表示のテクスチャは使えれば GPU の
        /// RenderTexture（変わったタイルだけ送る）。</summary>
        public TileGpuCompositor(CompositorBackend backend, bool allowCopyTexture = true)
        {
            if (!Enum.IsDefined(typeof(CompositorBackend), backend)) throw new ArgumentOutOfRangeException(nameof(backend));
            allowGpu = true; this.allowCopyTexture = allowCopyTexture; preference = backend;
        }

        public void Update(PaintDocument doc, PaintChannel channel)
        {
            Ensure(doc);
            try { UpdateTiles(doc,channel); }
            catch (Exception ex)
            {
                bool wasGpu = Path == CompositePath.Gpu;
                Dispose(); SetSize(doc);
                cpuFallback=new Texture2D(width,height,TextureFormat.RGBA32,false,true){hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear};
                Path = CompositePath.CpuFrame; FellBackToCpu |= wasGpu;
                Backend=(wasGpu ? "CPU composite fallback after GPU failure: " : "CPU composite fallback after a display failure: ")+ex.Message+"; the whole frame is uploaded to the display texture"+KernelsNote();
                UpdateTiles(doc,channel);
            }
        }

        /// <summary>残した写し（GPU の経路はアップロードしたブロックと下の写し、CPU の経路はメモリの下の写しと置き場）をすべて捨てる。
        /// 結果は変わらず、次の更新が遅くなるだけ。</summary>
        public void ReleaseResidentCaches()
        {
            foreach (var r in residents.Values) DestroyTexture(r.Texture);
            residents.Clear();
            foreach (var b in blocks.Values) { Release(b.Below); b.Below = null; b.BelowIndex = 0; }
            foreach (var b in cpuBlocks.Values) { b.Below = null; b.BelowIndex = 0; }
            cpuPool.Clear();
            ResidentBytes = 0;
        }

        void UpdateTiles(PaintDocument doc,PaintChannel channel)
        {
            var dirty = new HashSet<TileCoord>();
            bool incremental = ReferenceEquals(doc, lastDocument) && channel == lastChannel && doc.TryGetChangedTiles(channel, lastSerial, dirty);
            // 中身のあるタイル（変更記録が使えないとき、前回あって今は無いタイルも消すため）。CPU の経路はそのとき全面を合成し直すので、
            // 差分のときは数えない（4096²・10 層で 1 回 0.9 ms）
            HashSet<TileCoord> occupied = null;
            if (Path == CompositePath.Gpu || !incremental)
            {
                occupied = new HashSet<TileCoord>();
                foreach (var layer in doc.Layers)
                    foreach (var coord in layer.EnumerateContentTiles(channel)) occupied.Add(coord);
            }
            if (!incremental) { dirty.Clear(); dirty.UnionWith(previous); dirty.UnionWith(occupied); }
            LastUpdatedTileCount = dirty.Count; LastCpuTileCount = 0;
            LastBlockCount = LastSkippedBlockCount = LastBelowReuseCount = LastResidentHitCount = LastUploadCount = 0;
            LastSentTileCount = LastCpuCompositeCalls = LastCpuJobCount = 0; LastCpuFullFrame = false;

            if (Path != CompositePath.Gpu) UpdateCpu(doc, channel, dirty, incremental);
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

            if (occupied != null) { previous.Clear(); previous.UnionWith(occupied); }
            lastDocument = doc; lastChannel = channel; lastSerial = doc.ChangeSerial;
        }
        // ───────────── CPU の経路 ─────────────

        /// <summary>CPU の経路のブロックの記憶（GPU の <see cref="BlockState"/> と同じ考え）: 前回の最上段の署名と、下の写し（最上段の項目
        /// [0, BelowIndex) の合成。ブロックの画素の行を詰めて並べたもの）。Below があっても BelowIndex が 0 なら中身は使えない（配列だけ
        /// 使い回す）。</summary>
        sealed class CpuBlock { public string[] Sigs; public byte[] Below; public int BelowIndex; public int LastUsed; }
        readonly Dictionary<long, CpuBlock> cpuBlocks = new Dictionary<long, CpuBlock>();
        /// <summary>CPU の経路で 1 回の合成に渡す領域の置き場（ブロック 1 つ分の配列。<see cref="CpuWaveBlocks"/> 個まで使い回す）。</summary>
        readonly List<byte[]> cpuPool = new List<byte[]>();
        /// <summary>CPU の経路で 1 回の CompositeRegions に渡すブロックの数の上限（置き場の大きさ。512² のブロックで 16 MiB）。</summary>
        internal const int CpuWaveBlocks = 16;
        /// <summary>直近の Update が CPU の経路で合成した領域の数（CompositeRegions に渡したジョブ）。</summary>
        internal int LastCpuJobCount { get; private set; }

        /// <summary>1 つのブロックで合成する領域。</summary>
        sealed class CpuJob
        {
            public CpuBlock State; public int Bx, By; public TileRect Rect; public int X, Y, W, H; public byte[] Pixels;
            /// <summary>下の写しから始める（Pixels に写しを置いて渡す）。</summary>
            public int Start;
            /// <summary>下の写しを取り直す（ブロック全体を合成して State.Below へ）。</summary>
            public int CaptureAt = -1; public byte[] Capture;
        }

        /// <summary>CPU の正本の式（Core の CompositeRegions）で、変わったタイルを合成して表示へ送る。GPU の経路と同じく、ブロックごとに
        /// 最上段の項目の署名を覚え、前回と最初に違う項目より下の合成結果（下の写し）を予算の内側で残し、次からはそこから上だけを合成する
        /// （レイヤーの不透明度・合成モード・表示を変える、上の層に描くなど）。GPU と違い、写しから始めるときは変わったタイルだけを合成する。
        /// 変更記録が使えない（別の文書・チャンネル）ときは全部のブロックを合成し直す。表示の画素はどの場合も CPU の正本とバイト単位で同じ。</summary>
        void UpdateCpu(PaintDocument doc, PaintChannel channel, HashSet<TileCoord> dirty, bool incremental)
        {
            bool full = !incremental || !cpuValid;
            updateIndex++;
            if (!ReferenceEquals(doc, lastDocument) || channel != lastChannel) ClearCpuBlocks();
            if (Path == CompositePath.CpuFrame && (cpuPixels == null || cpuPixels.Length != width * height * 4)) { cpuPixels = new byte[width * height * 4]; full = true; }
            var plan = CpuCompositor.Plan(doc, channel);
            // ブロックごとの変わったタイル（全部のときは null = ブロックの全タイル）
            var byBlock = new SortedDictionary<long, List<TileCoord>>();
            if (full)
            {
                for (int by = 0; by * blockTiles < TilesY; by++) for (int bx = 0; bx * blockTiles < TilesX; bx++) byBlock.Add(BlockOrder(bx, by), null);
            }
            else
            {
                foreach (var t in dirty)
                {
                    if (t.X < 0 || t.Y < 0 || t.X >= TilesX || t.Y >= TilesY) continue;
                    long key = BlockOrder(t.X / blockTiles, t.Y / blockTiles);
                    if (!byBlock.TryGetValue(key, out var list)) byBlock.Add(key, list = new List<TileCoord>());
                    list.Add(t);
                }
                if (byBlock.Count == 0) return;
            }
            var jobs = new List<CpuJob>();
            foreach (var pair in byBlock)
            {
                int bx = (int)(pair.Key % BlockColumns), by = (int)(pair.Key / BlockColumns);
                PlanCpuBlock(doc, plan, channel, bx, by, pair.Value, full, jobs);
            }
            // 置き場の大きさごとに分けて合成し、終わった分から表示へ送る
            for (int i = 0; i < jobs.Count;)
            {
                int end = i; long bytes = 0;
                while (end < jobs.Count && (end == i || bytes + (long)jobs[end].W * jobs[end].H * 4 <= (long)CpuWaveBlocks * BlockBytes)) { bytes += (long)jobs[end].W * jobs[end].H * 4; end++; }
                RunCpuWave(doc, channel, jobs, i, end, full ? null : dirty);
                i = end;
            }
            if (Path == CompositePath.CpuFrame) UploadFrame();
            cpuValid = true; LastCpuFullFrame = full;
            TrimIdleCpu();
        }
        int BlockColumns => (TilesX + blockTiles - 1) / blockTiles;
        long BlockOrder(int bx, int by) => (long)by * BlockColumns + bx;

        /// <summary>ブロック 1 つで何をどこから合成するかを決めて jobs に足す。</summary>
        void PlanCpuBlock(PaintDocument doc, List<CpuCompositor.StackEntry> plan, PaintChannel channel, int bx, int by, List<TileCoord> tiles, bool full, List<CpuJob> jobs)
        {
            long key = BlockKey(bx, by);
            if (!cpuBlocks.TryGetValue(key, out var state)) cpuBlocks.Add(key, state = new CpuBlock());
            state.LastUsed = updateIndex;
            var sigs = new string[plan.Count];
            for (int i = 0; i < plan.Count; i++) sigs[i] = Signature(plan[i], channel, bx, by);
            int n = sigs.Length, first = state.Sigs == null ? 0 : FirstDifference(state.Sigs, sigs);
            bool known = !full && state.Sigs != null;
            if (known && first == n && state.Sigs.Length == n) { LastSkippedBlockCount++; state.Sigs = sigs; return; } // 合成は前と同じ
            LastBlockCount++;
            int k = state.Below != null ? state.BelowIndex : 0;
            bool usable = known && k > 0 && k <= first;
            int tx0 = bx * blockTiles, ty0 = by * blockTiles, tx1 = Math.Min(tx0 + blockTiles, TilesX), ty1 = Math.Min(ty0 + blockTiles, TilesY);
            var whole = new TileRect(tx0, ty0, tx1, ty1);
            if (!known)
            {
                // 初めて・全部を合成し直す: 透明から。写しは次に違いが出たときに取る（GPU と同じ）
                InvalidateBelow(state);
                jobs.Add(NewJob(state, bx, by, whole, 0));
            }
            else if (usable && k == first)
            {
                // 写しのすぐ上から変わった: 変わったタイルだけを写しから
                foreach (var r in CoverTiles(tiles, TilesX, TilesY)) jobs.Add(NewJob(state, bx, by, r, k));
                LastBelowReuseCount++;
            }
            else if (first > 0 && first < n && ReserveBelow(state, whole))
            {
                // 写しを最初に違う項目の下へ取り直す: ブロック全体を（使える写しがあればそこから）合成する
                var job = NewJob(state, bx, by, whole, usable ? k : 0);
                job.CaptureAt = first; job.Capture = state.Below; state.BelowIndex = 0; // 取り終えるまで使えない
                jobs.Add(job);
                if (usable) LastBelowReuseCount++;
            }
            else
            {
                // 写しより下が変わった（一番下の層に描くなど）か、写しを取れない: 変わったタイルだけを（使えれば写しから）合成する。
                // 古くなった写しは捨てる（変わっていないタイルでも、項目の並びが変われば下の部分の合成は変わり得る）
                if (!usable) InvalidateBelow(state);
                foreach (var r in CoverTiles(tiles, TilesX, TilesY)) jobs.Add(NewJob(state, bx, by, r, usable ? k : 0));
                if (usable) LastBelowReuseCount++;
            }
            state.Sigs = sigs;
        }
        CpuJob NewJob(CpuBlock state, int bx, int by, TileRect r, int start)
        {
            int x = r.X0 * tileSize, y = r.Y0 * tileSize, w = Math.Min(r.X1 * tileSize, width) - x, h = Math.Min(r.Y1 * tileSize, height) - y;
            return new CpuJob { State = state, Bx = bx, By = by, Rect = r, X = x, Y = y, W = w, H = h, Start = start };
        }
        /// <summary>下の写し（ブロックの画素を詰めた行）の領域 (x, y, w, h) を、詰めた配列 region へ写す。</summary>
        void CopyBelow(CpuBlock state, int bx, int by, int x, int y, int w, int h, byte[] region)
        {
            int bx0 = bx * blockSize, by0 = by * blockSize, bw = Math.Min(blockSize, width - bx0);
            for (int row = 0; row < h; row++) Buffer.BlockCopy(state.Below, ((y - by0 + row) * bw + (x - bx0)) * 4, region, row * w * 4, w * 4);
        }
        void RunCpuWave(PaintDocument doc, PaintChannel channel, List<CpuJob> jobs, int from, int to, HashSet<TileCoord> dirty)
        {
            var core = new List<CpuCompositor.CompositeJob>(to - from);
            for (int i = from; i < to; i++)
            {
                var j = jobs[i];
                // 置き場はここで借りる（ブロック全部の分を一度に持たない）
                j.Pixels = RentCpu();
                if (j.Start == 0) core.Add(new CpuCompositor.CompositeJob(j.X, j.Y, j.W, j.H, j.Pixels, 0, j.CaptureAt, j.Capture));
                else if (ReferenceEquals(j.Capture, j.State.Below))
                {
                    // 写しを取り直すジョブは写しへ書くので、写しから始める分は先に置き場へ並べる（写しを動かすときだけ）
                    CopyBelow(j.State, j.Bx, j.By, j.X, j.Y, j.W, j.H, j.Pixels);
                    core.Add(new CpuCompositor.CompositeJob(j.X, j.Y, j.W, j.H, j.Pixels, j.Start, j.CaptureAt, j.Capture));
                }
                else
                {
                    // 写しの行はワーカーが読む（呼び出し側で 4K の写しを並べ直さない）
                    int bx0 = j.Bx * blockSize, by0 = j.By * blockSize, bw = Math.Min(blockSize, width - bx0);
                    core.Add(new CpuCompositor.CompositeJob(j.X, j.Y, j.W, j.H, j.Pixels, j.Start, j.State.Below, ((j.Y - by0) * bw + (j.X - bx0)) * 4, bw * 4, j.CaptureAt, j.Capture));
                }
            }
            CpuCompositor.CompositeRegions(doc, channel, core);
            LastCpuCompositeCalls++; LastCpuJobCount += to - from;
            for (int i = from; i < to; i++)
            {
                var j = jobs[i];
                if (j.CaptureAt >= 0) j.State.BelowIndex = j.CaptureAt;
                if (Path == CompositePath.CpuTiles) SendRegion(j.Pixels, j.X, j.Y, j.W, j.Rect, dirty);
                else for (int row = 0; row < j.H; row++) Buffer.BlockCopy(j.Pixels, row * j.W * 4, cpuPixels, ((j.Y + row) * width + j.X) * 4, j.W * 4);
                ReturnCpu(j.Pixels); j.Pixels = null;
            }
        }
        byte[] RentCpu()
        {
            if (cpuPool.Count == 0) return new byte[BlockBytes];
            var a = cpuPool[cpuPool.Count - 1]; cpuPool.RemoveAt(cpuPool.Count - 1); return a;
        }
        void ReturnCpu(byte[] a) { if (cpuPool.Count < CpuWaveBlocks) cpuPool.Add(a); }
        /// <summary>ブロックの下の写しの配列を用意する（予算の内側。足りなければこの更新で使っていない写しを古い順に捨てる）。</summary>
        bool ReserveBelow(CpuBlock state, TileRect whole)
        {
            if (state.Below != null) return true;
            long bytes = BelowBytes(whole);
            if (!MakeRoomCpu(bytes)) return false;
            state.Below = new byte[bytes]; ResidentBytes += bytes;
            return true;
        }
        long BelowBytes(TileRect whole) => 4L * (Math.Min(whole.X1 * tileSize, width) - whole.X0 * tileSize) * (Math.Min(whole.Y1 * tileSize, height) - whole.Y0 * tileSize);
        bool MakeRoomCpu(long bytes)
        {
            if (bytes > ResidentBudgetBytes) return false;
            while (ResidentBytes + bytes > ResidentBudgetBytes)
            {
                CpuBlock victim = null; int oldest = int.MaxValue;
                foreach (var b in cpuBlocks.Values) if (b.Below != null && b.LastUsed < updateIndex && b.LastUsed < oldest) { oldest = b.LastUsed; victim = b; }
                if (victim == null) return false;
                ReleaseBelow(victim);
            }
            return true;
        }
        void ReleaseBelow(CpuBlock b) { if (b.Below == null) return; ResidentBytes -= b.Below.Length; b.Below = null; b.BelowIndex = 0; }
        static void InvalidateBelow(CpuBlock b) { b.BelowIndex = 0; }
        void TrimIdleCpu()
        {
            int limit = updateIndex - IdleUpdatesBeforeRelease;
            foreach (var b in cpuBlocks.Values) if (b.Below != null && b.LastUsed < limit) ReleaseBelow(b);
        }
        void ClearCpuBlocks()
        {
            foreach (var b in cpuBlocks.Values) ReleaseBelow(b);
            cpuBlocks.Clear();
        }
        void UploadFrame()
        {
            cpuFallback.LoadRawTextureData(cpuPixels); cpuFallback.Apply(false, false);
            LastSentTileCount = TilesX * TilesY;
        }

        /// <summary>タイルの長方形 [X0, X1) × [Y0, Y1)（タイルの番号）。</summary>
        internal readonly struct TileRect
        {
            public readonly int X0, Y0, X1, Y1;
            public TileRect(int x0, int y0, int x1, int y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
            public int Count => (X1 - X0) * (Y1 - Y0);
            public override string ToString() => "[" + X0 + "," + X1 + ")x[" + Y0 + "," + Y1 + ")";
        }
        /// <summary>長方形を 1 つ増やす手間を、タイル何枚の合成と見るか（外接長方形 1 つにまとめるかの目安）。CompositeRegion をタイルごとに
        /// 呼んでいたときの 4096²・タイル 128・32 スレッドの実測で、全面 1 回より 1 層で 1 回 0.12 ms（タイル 2.6 枚分）、10 層で 0.9 ms
        /// （1.6 枚分）多くかかった。今は長方形をまとめて 1 回の CompositeRegions に渡すので、手間はこれより小さい。</summary>
        internal const int CallCostInTiles = 2;

        /// <summary>tiles（tilesX × tilesY の外は除く）を覆う長方形の並び。行ごとの連続を、すぐ下の行の同じ幅の連続とつないで、tiles だけを
        /// 覆う長方形にする。外接長方形 1 つのほうが（余分なタイルの合成と、減る呼び出しの手間で）安ければ外接長方形にする。</summary>
        internal static List<TileRect> CoverTiles(IEnumerable<TileCoord> tiles, int tilesX, int tilesY)
        {
            var rows = new SortedDictionary<int, List<int>>();
            int count = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            foreach (var t in tiles)
            {
                if (t.X < 0 || t.Y < 0 || t.X >= tilesX || t.Y >= tilesY) continue;
                if (!rows.TryGetValue(t.Y, out var xs)) rows.Add(t.Y, xs = new List<int>());
                xs.Add(t.X); count++;
                minX = Math.Min(minX, t.X); maxX = Math.Max(maxX, t.X); minY = Math.Min(minY, t.Y); maxY = Math.Max(maxY, t.Y);
            }
            var result = new List<TileRect>();
            if (count == 0) return result;
            var open = new Dictionary<(int, int), int>(); // 前の行まで伸びている長方形: (x0, x1) → result の添え字
            int previousRow = int.MinValue; count = 0;
            foreach (var pair in rows)
            {
                int y = pair.Key; var xs = pair.Value; xs.Sort();
                var next = new Dictionary<(int, int), int>();
                for (int i = 0; i < xs.Count;)
                {
                    int x0 = xs[i], x1 = x0 + 1; i++;
                    for (; i < xs.Count && xs[i] <= x1; i++) if (xs[i] == x1) x1++; // 同じ番号が重なっていても 1 枚
                    count += x1 - x0;
                    if (y == previousRow + 1 && open.TryGetValue((x0, x1), out int index))
                    { var r = result[index]; result[index] = new TileRect(r.X0, r.Y0, r.X1, y + 1); next[(x0, x1)] = index; }
                    else { next[(x0, x1)] = result.Count; result.Add(new TileRect(x0, y, x1, y + 1)); }
                }
                open = next; previousRow = y;
            }
            long box = (long)(maxX - minX + 1) * (maxY - minY + 1);
            if (result.Count > 1 && box - count <= (long)CallCostInTiles * (result.Count - 1))
                return new List<TileRect> { new TileRect(minX, minY, maxX + 1, maxY + 1) };
            return result;
        }

        /// <summary>pixels（文書の (px, py) から幅 pw の画素）のうち、長方形 r の中の変わったタイル（dirty が null なら全部）を表示の
        /// RenderTexture へ送る。ブロックが丸ごと r の中で全部変わっていればブロック 1 つで、そうでなければタイル 1 枚ずつ。</summary>
        void SendRegion(byte[] pixels, int px, int py, int pw, TileRect r, HashSet<TileCoord> dirty)
        {
            for (int by = r.Y0 / blockTiles; by <= (r.Y1 - 1) / blockTiles; by++)
                for (int bx = r.X0 / blockTiles; bx <= (r.X1 - 1) / blockTiles; bx++)
                {
                    int tx0 = bx * blockTiles, ty0 = by * blockTiles, tx1 = Math.Min(tx0 + blockTiles, TilesX), ty1 = Math.Min(ty0 + blockTiles, TilesY);
                    if (blockTiles > 1 && tx0 >= r.X0 && ty0 >= r.Y0 && tx1 <= r.X1 && ty1 <= r.Y1 && AllChanged(dirty, tx0, ty0, tx1, ty1))
                    {
                        int x = tx0 * tileSize, y = ty0 * tileSize;
                        SendPixels(pixels, px, py, pw, x, y, Math.Min(tx1 * tileSize, width) - x, Math.Min(ty1 * tileSize, height) - y, blockStage, ref blockStageNext, blockSize);
                        LastSentTileCount += (tx1 - tx0) * (ty1 - ty0);
                        continue;
                    }
                    for (int ty = Math.Max(ty0, r.Y0); ty < Math.Min(ty1, r.Y1); ty++)
                        for (int tx = Math.Max(tx0, r.X0); tx < Math.Min(tx1, r.X1); tx++)
                        {
                            if (dirty != null && !dirty.Contains(new TileCoord(tx, ty))) continue; // 外接長方形で一緒に合成しただけのタイル（変わっていない）
                            int x = tx * tileSize, y = ty * tileSize;
                            SendPixels(pixels, px, py, pw, x, y, Math.Min(tileSize, width - x), Math.Min(tileSize, height - y), tileStage, ref tileStageNext, tileSize);
                            LastSentTileCount++;
                        }
                }
        }
        static bool AllChanged(HashSet<TileCoord> dirty, int tx0, int ty0, int tx1, int ty1)
        {
            if (dirty == null) return true;
            for (int ty = ty0; ty < ty1; ty++) for (int tx = tx0; tx < tx1; tx++) if (!dirty.Contains(new TileCoord(tx, ty))) return false;
            return true;
        }
        /// <summary>pixels の w×h（文書の (x, y) から）を載せ台に置き、表示の (x, y) へ写す。載せ台は 2 枚を交互に使う。行は管理側の
        /// バッファへ並べてから 1 回で載せる（4096² の全面で、載せ台の NativeArray へ行ごとに写すより 30 → 20 ms と速かった）。
        /// w×h の外の画素（端の欠けたタイル）は前の内容のままで、写さない。</summary>
        void SendPixels(byte[] pixels, int px, int py, int pw, int x, int y, int w, int h, Texture2D[] ring, ref int next, int size)
        {
            var stage = ring[next] ?? (ring[next] = MakeStage(size));
            next = (next + 1) % ring.Length;
            var buffer = size == tileSize ? (tileBuffer ?? (tileBuffer = new byte[size * size * 4])) : (blockBuffer ?? (blockBuffer = new byte[size * size * 4]));
            for (int row = 0; row < h; row++) Buffer.BlockCopy(pixels, ((y - py + row) * pw + (x - px)) * 4, buffer, row * size * 4, w * 4);
            stage.LoadRawTextureData(buffer); stage.Apply(false, false);
            if (copyStageToDisplay) Graphics.CopyTexture(stage, 0, 0, 0, 0, w, h, composite, 0, 0, x, y);
            else DrawCopy(stage, x, y, w, h);
        }
        static Texture2D MakeStage(int size) => new Texture2D(size, size, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };

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
                if (mask.HasActiveFilters) sb.Append("|F").Append(mask.OutputStamp(bx * blockTiles, by * blockTiles, (bx + 1) * blockTiles, (by + 1) * blockTiles));
            }
            switch (layer.Kind)
            {
                case LayerKind.Raster:
                    if (layer.TryGetChannel(channel, out var surface))
                    {
                        sb.Append("|r").Append(surface.Id).Append(':').Append(BlockRevision(surface, bx, by));
                    }
                    else sb.Append("|r-");
                    AppendFilterStamp(sb, layer, channel, bx, by);
                    break;
                case LayerKind.Fill:
                    var c = layer.GetPixel(channel, 0, 0);
                    sb.Append("|f").Append(c.R).Append(',').Append(c.G).Append(',').Append(c.B).Append(',').Append(c.A);
                    AppendFilterStamp(sb, layer, channel, bx, by);
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
        /// <summary>フィルターのある層: 出力の印（スタックの版と、halo だけ広げた範囲の入力の書き換え番号）。</summary>
        void AppendFilterStamp(StringBuilder sb, PaintLayer layer, PaintChannel channel, int bx, int by)
        {
            if (layer.HasActiveFilters(channel)) sb.Append("|F").Append(layer.OutputStamp(channel, bx * blockTiles, by * blockTiles, (bx + 1) * blockTiles, (by + 1) * blockTiles));
        }
        long BlockRevision(SparseTileSurface surface, int bx, int by)
        { return surface.MaxTileRevision(bx * blockTiles, by * blockTiles, (bx + 1) * blockTiles, (by + 1) * blockTiles); }

        // ───────────── 入れ子の合成（CpuCompositor.EvaluateSpan と同じ順と式） ─────────────

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
            return layer.OutputMayCover(channel, x0, y0, x1, y1); // フィルターのぼかしで広がる分も含む
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
            if (layer.HasActiveFilters(channel))
            {
                // フィルターのある層は、Core が halo 込みで評価した出力のタイルを載せる（CPU の正本と同じバイト）
                int tx0 = bx * blockTiles, ty0 = by * blockTiles, tx1 = tx0 + blockTiles, ty1 = ty0 + blockTiles;
                if (!layer.OutputMayCover(channel, tx0, ty0, tx1, ty1)) return false;
                source.Texture = Upload(FilteredId(layer, channel), layer.OutputStamp(channel, tx0, ty0, tx1, ty1), (c, b) => layer.CopyOutputTile(channel, c, b), bx, by, keep, transientLayer, ref transientLayerNext);
                return true;
            }
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
        { return Upload(surface.Id, new FilterStamp(0, BlockRevision(surface, bx, by)), surface.CopyTile, bx, by, keep, transient, ref next); }
        /// <summary>フィルターを通した出力の写しの番号: 面の番号の符号を変えたもの（塗りつぶしは層とチャンネルごとに振る）。面の番号とは重ならない。</summary>
        long FilteredId(PaintLayer layer, PaintChannel channel)
        {
            if (layer.TryGetChannel(channel, out var surface)) return -surface.Id;
            if (!fillOutputIds.TryGetValue((layer.Id, channel), out long id)) fillOutputIds.Add((layer.Id, channel), id = long.MinValue / 2 - fillOutputIds.Count);
            return id;
        }
        readonly Dictionary<(Guid, PaintChannel), long> fillOutputIds = new Dictionary<(Guid, PaintChannel), long>();
        /// <summary>id の出力のこのブロックを GPU に置く。copy がタイルを書く。keep なら予算の内側で残し、印が同じならアップロードし直さない。</summary>
        Texture2D Upload(long id, FilterStamp stamp, Func<TileCoord, byte[], bool> copy, int bx, int by, bool keep, Texture2D[] transient, ref int next)
        {
            var key = (id, BlockKey(bx, by));
            if (residents.TryGetValue(key, out var resident))
            {
                resident.LastUsed = updateIndex;
                if (resident.Stamp.Equals(stamp)) { LastResidentHitCount++; return resident.Texture; }
                FillBlock(copy, bx, by); resident.Texture.LoadRawTextureData(blockBuffer); resident.Texture.Apply(false, false); LastUploadCount++;
                resident.Stamp = stamp;
                return resident.Texture;
            }
            FillBlock(copy, bx, by); LastUploadCount++;
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
        void FillBlock(Func<TileCoord, byte[], bool> copy, int bx, int by)
        {
            int rowBytes = tileSize * 4, blockRow = blockSize * 4;
            for (int j = 0; j < blockTiles; j++)
                for (int i = 0; i < blockTiles; i++)
                {
                    var coord = new TileCoord(bx * blockTiles + i, by * blockTiles + j);
                    bool present = coord.X < TilesX && coord.Y < TilesY && copy(coord, tileBuffer);
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
                material.SetTexture("_MaskTex", mask.HasActiveFilters // フィルターのあるマスクは、フィルターを通した隠す量
                    ? Upload(-mask.Surface.Id, mask.OutputStamp(bx * blockTiles, by * blockTiles, (bx + 1) * blockTiles, (by + 1) * blockTiles), mask.CopyOutputTile, bx, by, keep, transientMask, ref transientMaskNext)
                    : Upload(mask.Surface, bx, by, keep, transientMask, ref transientMaskNext)); // 無いタイル = 何も隠さない（0）
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
        void DrawCopy(Texture source, int x, int y, int w, int h)
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
        void SetSize(PaintDocument doc)
        {
            width = doc.Width; height = doc.Height; tileSize = doc.TileSize;
            int maxTiles = Math.Max(TilesX, TilesY);
            blockTiles = Math.Max(1, Math.Min(TargetBlockPixels / tileSize, maxTiles)); blockSize = blockTiles * tileSize;
        }

        /// <summary>この環境で <see cref="CompositorBackend.Automatic"/> がどちらで合成するか。GPU がソフトウェアの描画（<see cref="IsSoftwareRenderer"/>）
        /// なら CPU: llvmpipe では CPU の経路が測ったすべての条件で速かった（4096² 10 層で最初の全面 295–311 対 1038–1168 ms、一番上の層の
        /// 不透明度 49–58 対 141–154 ms、真ん中の層 187–192 対 433–447 ms。VALIDATION.md「CPU の合成の内側のループと、CPU の表示の下の写し」）。
        /// 実際の GPU なら GPU: 構造の変更（不透明度など）は GPU のほうがまだ速い（同じ文書で 7–8 対 54–66 ms）。GPU が使えなければ CPU に
        /// 落ちるのは GPU を選んだときと同じ。</summary>
        /// <param name="note">自動で CPU にした理由（GPU のときは null）。</param>
        internal static CompositorBackend ResolveAutomatic(out string note)
        {
            string name = SimulatedDeviceName ?? SystemInfo.graphicsDeviceName, vendor = SimulatedDeviceName != null ? "" : SystemInfo.graphicsDeviceVendor;
            if (IsSoftwareRenderer(name, vendor))
            {
                note = "the GPU is a software renderer (" + name + "), where the CPU composites faster";
                return CompositorBackend.Cpu;
            }
            note = null; return CompositorBackend.Gpu;
        }
        /// <summary>テスト用: null でなければ、この環境の GPU の名前をこれとして <see cref="ResolveAutomatic"/> を決める。</summary>
        internal static string SimulatedDeviceName;
        /// <summary>CPU で描くソフトウェアの GPU の名前に含まれる語（Mesa の llvmpipe・softpipe・lavapipe・swrast・OpenSWR、Google の SwiftShader、
        /// Windows の WARP = Microsoft Basic Render Driver）。小文字で比べる。</summary>
        static readonly string[] SoftwareRenderers = { "llvmpipe", "softpipe", "lavapipe", "swrast", "software rasterizer", "openswr", "swiftshader", "basic render driver" };
        /// <summary>GPU の名前か製造元の名前が、CPU で描くソフトウェアの描画を示すか（SystemInfo.graphicsDeviceName / graphicsDeviceVendor）。</summary>
        internal static bool IsSoftwareRenderer(string deviceName, string vendor)
        {
            var text = ((deviceName ?? "") + " " + (vendor ?? "")).ToLowerInvariant();
            foreach (var s in SoftwareRenderers) if (text.Contains(s)) return true;
            return false;
        }
        /// <summary>CPU の経路の表示用: Core の内側のループに使っている核（Burst など）。</summary>
        static string KernelsNote() { var k = CompositeKernels.Current; return k == null ? "; inner loops: managed" : "; inner loops: " + k.Name; }

        /// <summary>この環境で GPU の合成が使えるか（グラフィックスデバイス、RenderTexture の形式、TileComposite のシェーダー）。設定の画面の表示用。</summary>
        internal static bool GpuCompositingAvailable(out string reason)
        {
            reason = SimulatedGpuUnavailable != null ? SimulatedGpuUnavailable
                : SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ? "no graphics device"
                : !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32) ? "the ARGB32 render texture format is unsupported"
                : !ShaderHealth.IsUsable(Shader.Find(ShaderName)) ? "the compositing shader is unavailable or failed to compile" : null;
            return reason == null;
        }
        const string ShaderName = "Hidden/YoluPainter/TileComposite";
        /// <summary>テスト用: null でなければ、この環境の GPU の合成は使えないものとして扱う（値は理由。CPU の経路の表示には GPU を使ってよい）。</summary>
        internal static string SimulatedGpuUnavailable;

        void Ensure(PaintDocument doc)
        {
            if (width == doc.Width && height == doc.Height && tileSize == doc.TileSize && Texture != null) return;
            Dispose(); SetSize(doc);
            bool device = allowGpu && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            var shader = device ? Shader.Find(ShaderName) : null;
            bool shaderUsable = ShaderHealth.IsUsable(shader), renderTextures = device && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32);
            useCopyTexture = allowCopyTexture && (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0;
            string automaticNote = null;
            var wanted = preference == CompositorBackend.Automatic ? ResolveAutomatic(out automaticNote) : preference;
            string reason;
            FellBackToCpu = false;
            if (wanted == CompositorBackend.Gpu)
            {
                if (shaderUsable && renderTextures && SimulatedGpuUnavailable == null)
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
                        Path = CompositePath.Gpu;
                        gpuBackend = Backend = "CPU source brush / GPU tiled compositor (encoded-space prototype" + (useCopyTexture ? ")" : ", draw copy)"); return;
                    }
                    catch (Exception ex) { Dispose(); SetSize(doc); reason = "CPU composite fallback: GPU allocation failed (" + ex.Message + ")"; }
                }
                else reason = "CPU composite fallback: " + (SimulatedGpuUnavailable ?? "GPU render texture format or shader unavailable (or the shader failed to compile)");
                FellBackToCpu = true;
            }
            else if (!allowGpu) reason = "CPU composite fallback: the GPU is not used by this compositor";
            else reason = "CPU compositor (" + (preference == CompositorBackend.Cpu ? "chosen in Project Settings > YoluPainter" : "automatic: " + automaticNote) + ")";

            // CPU で合成する。表示は使えれば RenderTexture（変わったタイルだけ写す）。写す手段は Texture2D → RenderTexture の CopyTexture、
            // 無ければ TileComposite の写しのパス。どちらも無ければ CPU 側の Texture2D に全面を載せる
            bool copyToRenderTexture = useCopyTexture && (SystemInfo.copyTextureSupport & CopyTextureSupport.TextureToRT) != 0;
            if (renderTextures && (copyToRenderTexture || shaderUsable))
            {
                try
                {
                    if (!copyToRenderTexture) { material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; material.SetTexture("_LayerTex", Texture2D.blackTexture); }
                    composite = MakeRt(width, height, FilterMode.Bilinear); Clear(composite);
                    copyStageToDisplay = copyToRenderTexture; Path = CompositePath.CpuTiles;
                    Backend = reason + "; changed tiles are copied to the display texture" + (copyToRenderTexture ? "" : " (draw copy)") + KernelsNote();
                    return;
                }
                catch (Exception ex) { Dispose(); SetSize(doc); reason += "; the display render texture failed (" + ex.Message + ")"; }
            }
            cpuFallback = new Texture2D(width, height, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            Path = CompositePath.CpuFrame;
            Backend = reason + "; the whole frame is uploaded to the display texture" + KernelsNote();
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
            blocks.Clear(); cpuBlocks.Clear();
            foreach (var level in levels) { Release(level.A); Release(level.B); Release(level.ClipA); Release(level.ClipB); }
            levels.Clear(); NestedRenderTextureCount = 0;
            Release(composite); Release(cpuTile);
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            for (int i = 0; i < transientLayer.Length; i++) { DestroyTexture(transientLayer[i]); DestroyTexture(transientMask[i]); transientLayer[i] = transientMask[i] = null; }
            for (int i = 0; i < tileStage.Length; i++) { DestroyTexture(tileStage[i]); DestroyTexture(blockStage[i]); tileStage[i] = blockStage[i] = null; }
            DestroyTexture(tileUpload); DestroyTexture(cpuFallback);
            tileBuffer = null; blockBuffer = null; cpuPixels = null; material = null; tileUpload = null; cpuFallback = null; composite = null; cpuTile = null; previous.Clear();
            lastDocument = null; lastSerial = -1; blockSize = blockTiles = 0; cpuValid = false;
        }
    }
}
