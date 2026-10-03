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

    /// <summary>表示の合成を何回かの <see cref="TileGpuCompositor.Update(PaintDocument, PaintChannel, CompositeSchedule)"/> に分けるときの
    /// 指示（ウィンドウが描くたびに作る）。1 回に使う時間・急ぎの印・先に合成する範囲。保存・書き出しなどの正本には関わらない。</summary>
    internal sealed class CompositeSchedule
    {
        /// <summary>1 回の Update に使ってよい時間（ms）。超えたら残りのブロックは次の Update に回す（判断はブロックの合間なので、
        /// ブロック 1 つ分（CPU の経路は 1 回にまとめて合成する見込みの誤差の分）は超え得る）。無限大なら全部（引数の無い Update と同じ）。
        /// 0 以下でも 1 回に 1 ブロックは進める。</summary>
        public double BudgetMilliseconds = double.PositiveInfinity;
        /// <summary>ストロークの最中: 予定のうち印の付いたブロック（描いた所）を、予算に関わらずこの回に出す（筆の跡が遅れない）。</summary>
        public bool Urgent;
        /// <summary>2D の表示に見えている文書の範囲（画素、左下原点）。掛かるブロックを先に、真ん中に近い順に。null なら 2D の表示は無い。</summary>
        public RectInt? Visible;
        /// <summary>3D ビューに映り得るタイル（今のスロットの UV が掛かるタイル）。2D の範囲の次に。null なら 3D ビューは無い。</summary>
        public ISet<TileCoord> ModelTiles;
        /// <summary>スライダーなどのドラッグの最中で、表示が全解像度を要らないとき（2D の拡大率・3D の大きさから）の間引きの幅（2 か 4。
        /// 1 なら使わない）。前の回で終わらなかった変更が続くあいだ、待っているタイルをこの幅で間引いた正確な合成
        /// （CpuCompositor.CompositeSampledRegions）で先に全部見せ、全解像度は残りの時間で続ける（Krita の Instant Preview と同じ考え。
        /// CPU の経路だけ）。</summary>
        public int PreviewStep = 1;
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
    /// 写しはすべて <see cref="ResidentBudgetBytes"/> の内側で、足りなければ層・マスクの入力から捨て、合成結果の写しを優先して残す。
    /// 入力のアップロードは合成結果を追い出さず、足りなければ使い捨ての載せ台を使う
    /// （結果は同じで、遅くなるだけ）。<see cref="ReleaseResidentCaches"/> で全部捨てられ、しばらく使われない写しは自動で捨てる。
    /// グループの中身の並びにも同じ写しを持つ（クリッピングされたグループも。TileGpuCompositor.Nested.cs）。
    /// CPU の古いグループの写しの配列は予算に数えたまま使い回し、足りなければ未使用の配列から手放す。</para>
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
    /// <para>表示の合成は何回かの Update に分けられる（<see cref="CompositeSchedule"/>。GIMP の表示と同じ考え: 変わった所に印を付けるだけで、
    /// 見えている所から、1 回の時間を決めて少しずつ）。変更記録のタイルを「予定」（ブロックごとの変わったタイル）に入れ、急ぎ（ストローク）→
    /// 2D で見えている所 → 3D ビューの UV の所 → 残り、同じ組では長く待っている順に、ブロック単位で合成する。合成はいつもその時の文書から
    /// するので、古い値の仕事は残らない（スライダーを動かし続けても、待っているブロックは最新の値で合成される）。ブロックは丸ごと合成して
    /// から表示へ写すので、表示のブロックは前の絵か新しい絵のどちらか（途中の合成は出ない）。別の文書・チャンネルに替わったら予定・署名・
    /// 写しを捨て、表示を透明にしてから合成し直す。<see cref="CompositePath.CpuFrame"/>（全面を載せ直す）は分けずに全部をする。</para>
    /// </remarks>
    internal sealed partial class TileGpuCompositor : IDisposable
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
        /// <summary>ブロックの記憶: 前回の署名の木、下の写し（Sigs の先頭 BelowIndex 項目を合成した結果）、グループの写し（グループの ID ごと）。</summary>
        sealed class BlockState { public SigNode[] Sigs; public RenderTexture Below; public int BelowIndex; public int LastUsed; public readonly Dictionary<Guid, GroupCopy> Groups = new Dictionary<Guid, GroupCopy>(); }
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
        /// <summary>載せ台から表示の RenderTexture へ CopyTexture で写せるか（false なら TileComposite の写しのパスで描き込む）。</summary>
        bool copyStageToDisplay;
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
        /// <summary>直近の Update が変更記録を使えず（初めて・別の文書かチャンネル）、表示を透明にして中身のあるブロックを全部予定に入れたか。</summary>
        internal bool LastCpuFullFrame { get; private set; }
        /// <summary>Tiles whose composite the last Update refreshed (the change journal's tiles of the blocks it composited), for
        /// diagnostics and tests.</summary>
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

        /// <summary>今の文書の合成を全部表示に入れる（予定に残っていた仕事も含めて、この 1 回で終える）。</summary>
        public void Update(PaintDocument doc, PaintChannel channel) => Update(doc, channel, null);

        /// <summary>表示の合成を、schedule の時間の中で優先する所から進める（null なら全部）。残りは次の Update で続ける。表示のブロックは
        /// いつも前の絵か新しい絵のどちらか。</summary>
        /// <returns>表示が今の文書の合成をすべて表しているか（false なら予定が残っている: <see cref="HasPendingWork"/>）。</returns>
        public bool Update(PaintDocument doc, PaintChannel channel, CompositeSchedule schedule)
        {
            Ensure(doc);
            try { UpdateTiles(doc, channel, schedule); }
            catch (Exception ex)
            {
                bool wasGpu = Path == CompositePath.Gpu;
                Dispose(); SetSize(doc);
                cpuFallback=new Texture2D(width,height,TextureFormat.RGBA32,false,true){hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear};
                Path = CompositePath.CpuFrame; FellBackToCpu |= wasGpu;
                Backend=(wasGpu ? "CPU composite fallback after GPU failure: " : "CPU composite fallback after a display failure: ")+ex.Message+"; the whole frame is uploaded to the display texture"+KernelsNote();
                UpdateTiles(doc, channel, schedule);
            }
            return pending.Count == 0;
        }

        /// <summary>残した写し（GPU の経路はアップロードしたブロックと下の写し、CPU の経路はメモリの下の写しと置き場）をすべて捨てる。
        /// 結果は変わらず、次の更新が遅くなるだけ。</summary>
        public void ReleaseResidentCaches()
        {
            foreach (var r in residents.Values) DestroyTexture(r.Texture);
            residents.Clear();
            foreach (var b in blocks.Values) { Release(b.Below); b.Below = null; b.BelowIndex = 0; ReleaseGroupCopies(b.Groups); }
            foreach (var b in cpuBlocks.Values) { b.Below = null; b.BelowIndex = 0; b.BelowVersion++; b.Sampled = null; ReleaseGroupCopies(b.Groups); }
            cpuPool.Clear();
            residentPixelPool.Clear(); PooledResidentBytes = 0;
            ResidentBytes = 0;
        }

        // ───────────── 予定（表示の合成を何回かに分ける） ─────────────

        /// <summary>まだ表示に入れていない変更のあるブロック: 変わったタイル、透明から合成し直すか、急ぎか、いつから待っているか。</summary>
        sealed class PendingBlock
        {
            public int Bx, By, Since, Rank; public long Distance; public bool Full, Urgent;
            public readonly HashSet<TileCoord> Tiles = new HashSet<TileCoord>();
        }
        readonly Dictionary<long, PendingBlock> pending = new Dictionary<long, PendingBlock>();
        /// <summary>表示に入れていない変更が残っている（次の Update で続ける）。</summary>
        public bool HasPendingWork => pending.Count > 0;
        /// <summary>予定に残っているブロックの数（診断・テスト用）。</summary>
        internal int PendingBlockCount => pending.Count;
        /// <summary>直近の Update が合成したブロック（署名が同じで飛ばしたものも含む）の、合成した順（診断・テスト用）。</summary>
        internal IReadOnlyList<(int bx, int by)> LastProcessedBlocks => lastProcessed;
        readonly List<(int bx, int by)> lastProcessed = new List<(int bx, int by)>();
        /// <summary>直近の Update にかかった時間（ms。GPU の経路は命令を出し終えるまで）。</summary>
        internal double LastUpdateMilliseconds { get; private set; }
        /// <summary>今、間引いた合成（<see cref="CompositeSchedule.PreviewStep"/>）を見せているタイルの数（全解像度が来るまで）。</summary>
        internal int PreviewTileCount => previewed.Count;
        /// <summary>間引いた合成を見せているときの幅（見せていなければ 0）。</summary>
        internal int PreviewStepShown => previewed.Count > 0 ? previewTextureStep : 0;
        /// <summary>直近の Update が間引いた合成を作り直したか、とそれにかかった時間（ms）。</summary>
        internal bool LastPreviewed { get; private set; }
        internal double LastPreviewMilliseconds { get; private set; }
        /// <summary>CPU の経路の見込み（直前までの実測の指数平均。<see cref="CpuCostMeasured"/> が false ならまだ無い）: 合成の 1 単位
        /// （画素 × 重ねる項目の数。写しから始める領域は写しを読む分の 1 を足す）あたりの時間と、表示へ送る 1 画素あたりの時間（ms）。
        /// 合成はワーカーが並列に、送るのはメインスレッドがするので、分けて測る。</summary>
        internal double CpuMillisecondsPerLayerPixel { get; private set; }
        internal double CpuMillisecondsPerSentPixel { get; private set; }
        internal bool CpuCostMeasured { get; private set; }
        /// <summary>テスト用: 時間の測り方（ms を返す）。null なら Stopwatch。替えると CPU の経路の見込みは測り直す。</summary>
        internal Func<double> ClockForTests { get => clockForTests; set { clockForTests = value; CpuCostMeasured = false; CpuMillisecondsPerLayerPixel = CpuMillisecondsPerSentPixel = cpuMsPerBlock = previewMsPerSample = 0; } }
        Func<double> clockForTests;
        /// <summary>テスト用: ブロックを 1 つ合成し終えるたびに呼ぶ（CPU の経路は 1 回にまとめた合成の直後、表示へ送る前に、そのブロックの
        /// 数だけ）。</summary>
        internal Action<int, int> BlockCompositedForTests;

        void UpdateTiles(PaintDocument doc, PaintChannel channel, CompositeSchedule schedule)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var clock = ClockForTests;
            double started = clock != null ? clock() : 0;
            double Elapsed() => clock != null ? clock() - started : stopwatch.Elapsed.TotalMilliseconds;
            updateIndex++;
            LastUpdatedTileCount = LastCpuTileCount = 0;
            LastBlockCount = LastSkippedBlockCount = LastBelowReuseCount = LastResidentHitCount = LastUploadCount = 0;
            LastSentTileCount = LastCpuCompositeCalls = LastCpuJobCount = 0; lastProcessed.Clear();
            LastNestedReuseCount = LastNestedCaptureCount = 0;
            LastResidentArrayAllocationCount = LastResidentArrayReuseCount = 0;
            LastInputEvictionCount = LastCopyEvictionCount = 0;

            var dirty = new HashSet<TileCoord>();
            bool frameLost = Path == CompositePath.CpuFrame && (cpuPixels == null || cpuPixels.Length != width * height * 4);
            if (frameLost) cpuPixels = new byte[width * height * 4];
            bool incremental = !frameLost && ReferenceEquals(doc, lastDocument) && channel == lastChannel && doc.TryGetChangedTiles(channel, lastSerial, dirty);
            LastCpuFullFrame = !incremental;
            if (!incremental)
            {
                // 初めて・別の文書かチャンネル: 前の予定・署名・写しは使えない。表示を透明にして（前の文書の絵と混ぜない）、中身のあるタイル
                // （塗りつぶしと調整はキャンバス全面）のブロックを全部、透明から合成する予定にする
                pending.Clear(); ClearBlockStates(); ClearCpuBlocks(); ClearDisplay();
                dirty.Clear();
                foreach (var layer in doc.Layers) foreach (var coord in layer.EnumerateContentTiles(channel)) dirty.Add(coord);
            }
            // 変更記録はここで読み終える（このあとの合成で Generator の入力が見直されて増えた変更は、次の Update が拾う）
            lastDocument = doc; lastChannel = channel; lastSerial = doc.ChangeSerial;
            bool urgent = schedule != null && schedule.Urgent;
            foreach (var coord in dirty) Mark(coord, !incremental, urgent);

            double budget = schedule == null || Path == CompositePath.CpuFrame ? double.PositiveInfinity
                : double.IsNaN(schedule.BudgetMilliseconds) ? 0 : schedule.BudgetMilliseconds;
            LastPreviewed = false; LastPreviewMilliseconds = 0;
            if (!incremental) previewed.Clear(); // 表示は透明にした
            int step = schedule == null ? 1 : schedule.PreviewStep;
            // 間引いた合成を先に見せる: ドラッグの最中（step > 1）、前の回で終わらなかった（1 回では揃わない変更）、文書がその後また
            // 変わった、全解像度で揃えるのに予算か間引いた合成の 4 回分以上かかる見込み（PreviewPays）、のすべてのとき。マウスを
            // 止めているあいだは作り直さず、時間を全解像度に回す
            if (step > 1 && pending.Count > 0 && !lastFinished && doc.ChangeSerial != previewSerial && CanPreview(step) && !HasEvaluatedSources(CpuCompositor.Plan(doc, channel), channel) && PreviewPays(step, budget))
            {
                double t0 = Elapsed();
                long samples = Preview(doc, channel, step);
                LastPreviewed = true; LastPreviewMilliseconds = Elapsed() - t0;
                if (samples > 0)
                {
                    double rate = LastPreviewMilliseconds / samples;
                    previewMsPerSample = previewMsPerSample > 0 ? .5 * previewMsPerSample + .5 * rate : rate;
                }
            }
            if (pending.Count > 0)
            {
                var order = Ordered(schedule);
                if (Path == CompositePath.Gpu) RunGpu(doc, channel, order, budget, Elapsed);
                else RunCpu(doc, channel, order, budget, Elapsed);
            }
            if (Path == CompositePath.CpuFrame && (lastProcessed.Count > 0 || !incremental)) UploadFrame();
            if (Path == CompositePath.Gpu) TrimIdle(); else TrimIdleCpu();
            if (previewed.Count == 0 && step <= 1) ReleasePreview(); // ドラッグが終わって全解像度が揃った
            lastFinished = pending.Count == 0;
            LastUpdateMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        }

        void Mark(TileCoord coord, bool full, bool urgent)
        {
            if (coord.X < 0 || coord.Y < 0 || coord.X >= TilesX || coord.Y >= TilesY) return;
            int bx = coord.X / blockTiles, by = coord.Y / blockTiles; long key = BlockKey(bx, by);
            if (!pending.TryGetValue(key, out var p)) pending.Add(key, p = new PendingBlock { Bx = bx, By = by, Since = updateIndex });
            p.Tiles.Add(coord); p.Full |= full; p.Urgent |= urgent;
        }

        /// <summary>予定のブロックを合成する順に並べる: 急ぎ（ストローク）→ 2D で見えている所 → 3D ビューの UV の所 → 残り。同じ組では
        /// 長く待っている順（スライダーを動かし続けても、どのブロックもいずれ新しくなる）、同じなら見ている所の真ん中に近い順。
        /// 指示が無ければ行の順。</summary>
        List<PendingBlock> Ordered(CompositeSchedule schedule)
        {
            var list = new List<PendingBlock>(pending.Values);
            var visible = schedule?.Visible; var model = schedule?.ModelTiles;
            long cx = visible.HasValue ? (long)visible.Value.x * 2 + visible.Value.width : width, cy = visible.HasValue ? (long)visible.Value.y * 2 + visible.Value.height : height;
            foreach (var p in list)
            {
                int x0 = p.Bx * blockSize, y0 = p.By * blockSize, x1 = Math.Min(x0 + blockSize, width), y1 = Math.Min(y0 + blockSize, height);
                p.Rank = schedule == null ? 3 : p.Urgent ? 0
                    : visible.HasValue && x0 < visible.Value.xMax && visible.Value.x < x1 && y0 < visible.Value.yMax && visible.Value.y < y1 ? 1
                    : model != null && TouchesModel(model, p.Bx, p.By) ? 2 : 3;
                long dx = x0 + x1 - cx, dy = y0 + y1 - cy; p.Distance = schedule == null ? 0 : dx * dx + dy * dy;
            }
            list.Sort((a, b) =>
                a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Since != b.Since ? a.Since.CompareTo(b.Since) : a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance)
                : a.By != b.By ? a.By.CompareTo(b.By) : a.Bx.CompareTo(b.Bx));
            return list;
        }
        bool TouchesModel(ISet<TileCoord> model, int bx, int by)
        {
            for (int ty = by * blockTiles; ty < Math.Min((by + 1) * blockTiles, TilesY); ty++)
                for (int tx = bx * blockTiles; tx < Math.Min((bx + 1) * blockTiles, TilesX); tx++) if (model.Contains(new TileCoord(tx, ty))) return true;
            return false;
        }
        /// <summary>表示を透明にする（別の文書・チャンネルに替わったとき）。</summary>
        void ClearDisplay()
        {
            if (composite != null) Clear(composite);
            if (cpuPixels != null) Array.Clear(cpuPixels, 0, cpuPixels.Length);
        }
        void Finished(PendingBlock p)
        {
            pending.Remove(BlockKey(p.Bx, p.By)); LastUpdatedTileCount += p.Tiles.Count; lastProcessed.Add((p.Bx, p.By));
            if (previewed.Count > 0) previewed.ExceptWith(p.Tiles); // 全解像度で送る（間引いた合成のタイルは予定のタイルに入っている）
        }

        // ───────────── 間引いた合成（ドラッグの最中の速い見せ方） ─────────────

        Texture2D previewTexture; byte[] previewPixels; Material previewMaterial; int previewTextureStep;
        /// <summary>表示が間引いた合成を見せているタイル（全解像度が来るまで予定に残っている）。</summary>
        readonly HashSet<TileCoord> previewed = new HashSet<TileCoord>();
        /// <summary>最後に間引いた合成を作ったときの文書の変更の通し番号。</summary>
        long previewSerial = -1;
        /// <summary>前の Update が予定を全部終えたか（終えていなければ、続く変更は間引いた合成で先に見せる）。</summary>
        bool lastFinished = true;

        /// <summary>CPU の経路の見込み: 全解像度のブロック 1 つの時間と、間引いた合成の 1 標本の時間（ms。指数平均。0 はまだ無い）。</summary>
        double cpuMsPerBlock, previewMsPerSample;
        /// <summary>間引いた合成が割に合うか: 待っているブロックを全解像度で揃える見込みが、1 回の予算と間引いた合成 1 回の見込みの大きい方の
        /// 4 倍以上（それより早く揃うなら、間引いた絵を挟まずに全解像度を少しずつ出すほうが良い。4096²・10 層の真ん中の層で
        /// 全解像度約 150 ms・間引き 1/4 約 16 ms は使い、2048² の約 30 ms は使わない。VALIDATION）。全解像度の見込みがまだ無ければ使わない。</summary>
        bool PreviewPays(int step, double budget)
        {
            if (cpuMsPerBlock <= 0) return false;
            double full = pending.Count * cpuMsPerBlock, samples = 0;
            foreach (var p in pending.Values) samples += (double)Math.Min(blockSize, width - p.Bx * blockSize) * Math.Min(blockSize, height - p.By * blockSize) / ((double)step * step);
            double preview = samples * previewMsPerSample;
            return full >= 4 * Math.Max(double.IsPositiveInfinity(budget) ? 0 : budget, preview);
        }

        /// <summary>この幅で間引いて見せられるか: CPU で合成して表示の RenderTexture へ送る経路で、幅がタイル・文書の幅と高さを割り切り、
        /// 拡大して描く TileComposite の写しのパスが使える。</summary>
        bool CanPreview(int step)
        {
            if (Path != CompositePath.CpuTiles || composite == null || step < 2 || tileSize % step != 0 || width % step != 0 || height % step != 0) return false;
            if (material == null && previewMaterial == null)
            {
                var shader = Shader.Find(ShaderName);
                if (!ShaderHealth.IsUsable(shader)) return false;
                previewMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                previewMaterial.SetTexture("_LayerTex", Texture2D.blackTexture);
            }
            return true;
        }

        /// <summary>間引く合成でも評価する層は全解像度のタイルを読む。予定の全部を先に間引くと、その層の全部も同じ回に評価して
        /// 時間の区切りを越えるため、その場合は全解像度のブロックを予算ごとに進める。入れ子・クリッピング・マスクも同じ。
        /// 評価しない層のドラッグは従来どおり間引ける。</summary>
        static bool HasEvaluatedSources(IReadOnlyList<CpuCompositor.StackEntry> entries, PaintChannel channel)
        {
            foreach (var entry in entries)
            {
                var layer = entry.Base;
                if (layer.HasEvaluatedOutput(channel) || layer.Mask != null && !layer.Mask.IsNeutral && layer.Mask.HasActiveFilters
                    || HasEvaluatedSources(entry.Children, channel) || HasEvaluatedSources(entry.ClipEntries, channel)) return true;
            }
            return false;
        }

        /// <summary>予定のブロックの全部を、step で間引いた正確な合成（各ブロックの下の写しが今も使えればそこから）で作り、待っている
        /// タイルへ拡大して描く。予定と署名・写しの記憶は変えない（全解像度はあとで普通に合成する）。</summary>
        /// <returns>合成した標本の数。</returns>
        long Preview(PaintDocument doc, PaintChannel channel, int step)
        {
            int gw = width / step, gh = height / step;
            if (previewTexture == null || previewTextureStep != step || previewTexture.width != gw || previewTexture.height != gh)
            {
                ReleasePreviewTexture();
                previewTexture = new Texture2D(gw, gh, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                previewPixels = new byte[gw * gh * 4]; previewTextureStep = step;
            }
            var plan = CpuCompositor.Plan(doc, channel);
            var jobs = new List<CpuCompositor.CompositeJob>(pending.Count);
            foreach (var p in pending.Values)
            {
                int x0 = p.Bx * blockSize, y0 = p.By * blockSize, bw = Math.Min(blockSize, width - x0), bh = Math.Min(blockSize, height - y0);
                int sx = x0 / step, sy = y0 / step, sw = bw / step, sh = bh / step;
                int start = 0; byte[] below = null;
                if (!p.Full && cpuBlocks.TryGetValue(BlockKey(p.Bx, p.By), out var state) && state.Sigs != null && state.Below != null && state.BelowIndex > 0)
                {
                    // 下の写しは、その下の項目の署名がどれも前と同じなら今も正しい（PlanCpuBlock の usable と同じ規則）
                    int k = state.BelowIndex, same = 0;
                    if (k <= Math.Min(state.Sigs.Length, plan.Count))
                        while (same < k && string.Equals(state.Sigs[same].Sig, Signature(plan[same], channel, p.Bx, p.By), StringComparison.Ordinal)) same++;
                    if (same == k) { start = k; below = SampledBelow(state, bw, bh, step); state.LastUsed = updateIndex; }
                }
                long key = BlockKey(p.Bx, p.By);
                if (!previewBlocks.TryGetValue(key, out var pixels) || pixels.Length != sw * sh * 4) previewBlocks[key] = pixels = new byte[sw * sh * 4]; // 回ごとに作らない
                // 領域は間引いた格子の上（ブロックの原点 / step から）
                jobs.Add(below == null ? new CpuCompositor.CompositeJob(sx, sy, sw, sh, pixels) : new CpuCompositor.CompositeJob(sx, sy, sw, sh, pixels, start, below, 0, sw * 4));
            }
            CpuCompositor.CompositeSampledRegions(doc, channel, step, jobs);
            foreach (var j in jobs) for (int row = 0; row < j.Height; row++) Buffer.BlockCopy(j.Pixels, row * j.Width * 4, previewPixels, ((j.Y + row) * gw + j.X) * 4, j.Width * 4);
            previewTexture.LoadRawTextureData(previewPixels); previewTexture.Apply(false, false);
            // 待っているタイルへ拡大して描く（ブロックのタイルが全部待っていればブロックで 1 つ。全部を 1 回の描画で）
            var rects = new List<RectInt>();
            foreach (var p in pending.Values)
            {
                int tx0 = p.Bx * blockTiles, ty0 = p.By * blockTiles, tx1 = Math.Min(tx0 + blockTiles, TilesX), ty1 = Math.Min(ty0 + blockTiles, TilesY);
                if (p.Tiles.Count == (tx1 - tx0) * (ty1 - ty0)) rects.Add(new RectInt(tx0 * tileSize, ty0 * tileSize, Math.Min(tx1 * tileSize, width) - tx0 * tileSize, Math.Min(ty1 * tileSize, height) - ty0 * tileSize));
                else foreach (var t in p.Tiles) rects.Add(new RectInt(t.X * tileSize, t.Y * tileSize, Math.Min(tileSize, width - t.X * tileSize), Math.Min(tileSize, height - t.Y * tileSize)));
                previewed.UnionWith(p.Tiles);
            }
            DrawPreview(rects);
            previewSerial = doc.ChangeSerial;
            long samples = 0; foreach (var j in jobs) samples += (long)j.Width * j.Height;
            return samples;
        }
        /// <summary>間引いた合成のブロックごとの置き場（ドラッグのあいだ使い回す）。</summary>
        readonly Dictionary<long, byte[]> previewBlocks = new Dictionary<long, byte[]>();
        /// <summary>ブロックの下の写しを step で間引いたもの。写しが同じあいだは残す（予算に余りがあれば。無ければその回だけ作る）。</summary>
        byte[] SampledBelow(CpuBlock state, int bw, int bh, int step)
        {
            if (state.Sampled != null && state.SampledVersion == state.BelowVersion && state.SampledStep == step) return state.Sampled;
            int sw = bw / step, sh = bh / step, half = step / 2; long bytes = 4L * sw * sh;
            if (state.Sampled != null && state.Sampled.Length != bytes) ReleaseSampled(state);
            var sampled = state.Sampled;
            if (sampled == null)
            {
                sampled = new byte[bytes];
                if (ResidentBytes + bytes <= ResidentBudgetBytes) { state.Sampled = sampled; ResidentBytes += bytes; }
            }
            // 画素（4 バイト）を int として写す（Buffer.BlockCopy を画素ごとに呼ぶと 4096² で 50 ms ほどかかった）
            var from = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(state.Below.AsSpan(0, bw * bh * 4));
            var to = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(sampled.AsSpan());
            for (int y = 0; y < sh; y++)
                for (int x = 0, f = (y * step + half) * bw + half, t = y * sw; x < sw; x++, f += step, t++) to[t] = from[f];
            if (ReferenceEquals(sampled, state.Sampled)) { state.SampledVersion = state.BelowVersion; state.SampledStep = step; }
            return sampled;
        }
        void ReleaseSampled(CpuBlock b) { if (b.Sampled == null) return; ResidentBytes -= b.Sampled.Length; b.Sampled = null; }
        /// <summary>間引いた合成の矩形（文書の画素）を、表示の同じ所へ拡大して描く（点の補間なので、step × step の画素が同じ色）。</summary>
        void DrawPreview(List<RectInt> rects)
        {
            if (rects.Count == 0) return;
            var m = material ?? previewMaterial; var old = RenderTexture.active;
            try
            {
                RenderTexture.active = composite;
                GL.PushMatrix(); GL.LoadPixelMatrix(0, width, 0, height);
                m.SetTexture("_MainTex", previewTexture); m.SetPass(1);
                GL.Begin(GL.QUADS);
                foreach (var r in rects)
                {
                    float u0 = r.x / (float)width, v0 = r.y / (float)height, u1 = r.xMax / (float)width, v1 = r.yMax / (float)height;
                    GL.TexCoord2(u0, v0); GL.Vertex3(r.x, r.y, 0);
                    GL.TexCoord2(u0, v1); GL.Vertex3(r.x, r.yMax, 0);
                    GL.TexCoord2(u1, v1); GL.Vertex3(r.xMax, r.yMax, 0);
                    GL.TexCoord2(u1, v0); GL.Vertex3(r.xMax, r.y, 0);
                }
                GL.End(); GL.PopMatrix();
            }
            finally { RenderTexture.active = old; m.SetTexture("_MainTex", null); }
        }
        void ReleasePreviewTexture() { DestroyTexture(previewTexture); previewTexture = null; previewPixels = null; previewTextureStep = 0; previewBlocks.Clear(); }
        /// <summary>間引いた合成の置き場を手放す（見せているタイルが無くなってドラッグも終わったとき、Dispose）。</summary>
        void ReleasePreview()
        {
            ReleasePreviewTexture();
            foreach (var b in cpuBlocks.Values) ReleaseSampled(b);
        }

        /// <summary>GPU の経路: 予定のブロックを順に 1 つずつ合成し、時間を過ぎたら残りを次へ回す（急ぎのブロックと、1 回目の 1 つは必ず）。</summary>
        void RunGpu(PaintDocument doc, PaintChannel channel, List<PendingBlock> order, double budget, Func<double> elapsed)
        {
            // Normal チャンネルはシェーダーがベクトルとして合成する（CpuCompositor → NormalMaps と同じ式）
            material.SetFloat("_NormalChannel", channel == PaintChannel.Normal ? 1 : 0);
            var plan = CpuCompositor.Plan(doc, channel);
            int needed = LevelsNeeded(plan);
            bool tooDeep = needed > NestedLevelLimit;
            Backend = gpuBackend + (tooDeep ? "; groups nested " + needed + " levels deep exceed the GPU limit of " + NestedLevelLimit + ", so the tiles they touch composite on the CPU" : "");
            // Graphics.Blit は書き込み先を RenderTexture.active に残すので、呼び出し側の状態を戻す
            var active = RenderTexture.active;
            try
            {
                foreach (var p in order)
                {
                    if (lastProcessed.Count > 0 && !p.Urgent && elapsed() >= budget) break;
                    if ((tooDeep || CompositeGroupsOnCpu) && GroupTouchesBlock(plan, channel, p.Bx, p.By)) CompositeBlockWithCpuGroups(doc, plan, channel, p.Bx, p.By);
                    else CompositeBlock(plan, channel, p.Bx, p.By);
                    Finished(p);
                    BlockCompositedForTests?.Invoke(p.Bx, p.By);
                }
            }
            finally { RenderTexture.active = active; }
        }
        // ───────────── CPU の経路 ─────────────

        /// <summary>CPU の経路のブロックの記憶（GPU の <see cref="BlockState"/> と同じ考え）: 前回の最上段の署名と、下の写し（最上段の項目
        /// [0, BelowIndex) の合成。ブロックの画素の行を詰めて並べたもの）。Below があっても BelowIndex が 0 なら中身は使えない（配列だけ
        /// 使い回す）。</summary>
        sealed class CpuBlock
        {
            public SigNode[] Sigs; public byte[] Below; public int BelowIndex; public int LastUsed;
            /// <summary>グループの写し（グループの ID ごと。Pixels は Below と同じ並び）。</summary>
            public readonly Dictionary<Guid, GroupCopy> Groups = new Dictionary<Guid, GroupCopy>();
            /// <summary>下の写しの中身が変わるたびに増える（間引いた写し Sampled が古いかを見る）。</summary>
            public int BelowVersion;
            /// <summary>間引いた合成の下地: Below を SampledStep で間引いたもの（SampledVersion の写しの）。</summary>
            public byte[] Sampled; public int SampledVersion, SampledStep;
        }
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
            /// <summary>表示へ送るタイル（ブロックの変わったタイル。null なら領域の全部）。</summary>
            public HashSet<TileCoord> Send;
            /// <summary>中身を写しから始めるグループと、写しを取るグループ（取った写しは合成の後にブロックへ入れる）。</summary>
            public List<(GroupCopy Copy, int[] Address)> Resume;
            public List<(GroupCopy Copy, int[] Address, int Index)> Captures;
        }

        /// <summary>CPU の正本の式（Core の CompositeRegions）で、予定のブロックを合成して表示へ送る。GPU の経路と同じく、ブロックごとに
        /// 最上段の項目の署名を覚え、前回と最初に違う項目より下の合成結果（下の写し）を予算の内側で残し、次からはそこから上だけを合成する
        /// （レイヤーの不透明度・合成モード・表示を変える、上の層に描くなど）。GPU と違い、写しから始めるときは変わったタイルだけを合成する。
        /// 時間を分けるときは、何ブロックかを 1 回の CompositeRegions にまとめ（並列に合成するため）、直前までの実測の見込み
        /// （<see cref="CpuMillisecondsPerLayerPixel"/>・<see cref="CpuMillisecondsPerSentPixel"/>）が残りの時間に収まるところまでを 1 回に入れる。表示の画素はどの場合も CPU の正本と
        /// バイト単位で同じ。</summary>
        void RunCpu(PaintDocument doc, PaintChannel channel, List<PendingBlock> order, double budget, Func<double> elapsed)
        {
            bool limited = !double.IsPositiveInfinity(budget);
            var plan = CpuCompositor.Plan(doc, channel);
            int next = 0;
            while (next < order.Count)
            {
                if (limited && lastProcessed.Count > 0 && !order[next].Urgent && elapsed() >= budget) break;
                // 1 回の合成に入れるブロック: 急ぎは全部、ほかは見込みが残りの時間に収まるうち（収まらなくなった 1 つまで。どの回も
                // 少なくとも 1 つ）。見込みがまだ無ければ 1 つだけ入れて測る
                var jobs = new List<CpuJob>(); var taken = new List<PendingBlock>(); double predicted = 0;
                double remaining = budget - elapsed();
                while (next < order.Count)
                {
                    var p = order[next];
                    if (limited && !p.Urgent && taken.Count > 0 && (!CpuCostMeasured || predicted >= remaining)) break;
                    int from = jobs.Count;
                    PlanCpuBlock(plan, channel, p, jobs);
                    for (int i = from; i < jobs.Count; i++)
                        predicted += LayerPixels(jobs[i], plan.Count) * CpuMillisecondsPerLayerPixel + (double)jobs[i].W * jobs[i].H * CpuMillisecondsPerSentPixel;
                    taken.Add(p); Finished(p); next++;
                }
                // 置き場の大きさごとに分けて合成し、終わった分から表示へ送る。合成と送りの時間を分けて測り、見込みを直す
                double composite = 0, send = 0, layerPixels = 0, pixels = 0;
                for (int i = 0; i < jobs.Count;)
                {
                    int end = i; long bytes = 0;
                    while (end < jobs.Count && (end == i || bytes + (long)jobs[end].W * jobs[end].H * 4 <= (long)CpuWaveBlocks * BlockBytes)) { bytes += (long)jobs[end].W * jobs[end].H * 4; end++; }
                    var (c, t) = RunCpuWave(doc, channel, jobs, i, end, elapsed);
                    composite += c; send += t;
                    for (int k = i; k < end; k++) { layerPixels += LayerPixels(jobs[k], plan.Count); pixels += (double)jobs[k].W * jobs[k].H; }
                    i = end;
                }
                if (taken.Count > 0)
                {
                    double perBlock = (composite + send) / taken.Count;
                    cpuMsPerBlock = cpuMsPerBlock > 0 ? .5 * cpuMsPerBlock + .5 * perBlock : perBlock;
                }
                if (layerPixels > 0)
                {
                    double perLayerPixel = composite / layerPixels, perPixel = send / pixels;
                    if (CpuCostMeasured) { CpuMillisecondsPerLayerPixel = .5 * CpuMillisecondsPerLayerPixel + .5 * perLayerPixel; CpuMillisecondsPerSentPixel = .5 * CpuMillisecondsPerSentPixel + .5 * perPixel; }
                    else { CpuMillisecondsPerLayerPixel = perLayerPixel; CpuMillisecondsPerSentPixel = perPixel; CpuCostMeasured = true; }
                }
            }
        }
        /// <summary>見込みの合成の単位: 画素 × 重ねる項目の数（写しから始める領域は、写しを読む分の 1 を足す）。</summary>
        static double LayerPixels(CpuJob job, int entries) => (double)job.W * job.H * (entries - job.Start + (job.Start > 0 ? 1 : 0));

        /// <summary>予定のブロック 1 つで何をどこから合成するかを決めて jobs に足す（ブロックの署名と写しの記憶はここで進める）。</summary>
        void PlanCpuBlock(List<CpuCompositor.StackEntry> plan, PaintChannel channel, PendingBlock p, List<CpuJob> jobs)
        {
            int bx = p.Bx, by = p.By; var tiles = p.Tiles; bool full = p.Full; int first0 = jobs.Count;
            long key = BlockKey(bx, by);
            if (!cpuBlocks.TryGetValue(key, out var state)) cpuBlocks.Add(key, state = new CpuBlock());
            state.LastUsed = updateIndex;
            var sigs = BuildSigs(plan, channel, bx, by);
            int n = sigs.Length, first = state.Sigs == null ? 0 : FirstDifference(state.Sigs, sigs);
            bool known = !full && state.Sigs != null;
            // 合成は前と同じ（ただし間引いた合成を見せているタイルがあれば、全解像度で送り直す）
            bool shown = previewed.Count > 0 && previewed.Overlaps(tiles);
            if (known && first == n && state.Sigs.Length == n && !shown) { LastSkippedBlockCount++; state.Sigs = sigs; return; }
            LastBlockCount++;
            int k = state.Below != null ? state.BelowIndex : 0;
            bool usable = known && k > 0 && k <= first;
            int tx0 = bx * blockTiles, ty0 = by * blockTiles, tx1 = Math.Min(tx0 + blockTiles, TilesX), ty1 = Math.Min(ty0 + blockTiles, TilesY);
            var whole = new TileRect(tx0, ty0, tx1, ty1);
            // グループの写し: 今も正しいものだけ残し（前回と比べられなければ全部捨てる）、違う道に沿って取るものを決める
            var nest = PlanNested(state.Groups, known ? Diff(state.Sigs, sigs) : null, ResidentBudgetBytes > 0, usable ? k : 0);
            if (!known)
            {
                // 初めて・全部を合成し直す: 透明から。写しは次に違いが出たときに取る（GPU と同じ）
                InvalidateBelow(state);
                jobs.Add(NewJob(state, bx, by, whole, 0));
            }
            else
            {
                int start = usable ? k : 0;
                var resume = ReachableCopies(nest, start);
                // 最上段の写しを最初に違う項目の下へ取り直すか（すぐ上から変わったなら今の写しのまま）、グループの中で写しを取るか
                bool rootCapture = first > 0 && first < n && !(usable && k == first) && ReserveBelow(state, whole);
                var captures = ReserveGroupCopies(state, nest, whole, false);
                // ブロック全体を合成するとき（写しを取る、変わったタイルがブロックの全部）は、先回りの写しも（空いた予算で）取る
                if (rootCapture || captures.Count > 0 || tiles.Count >= whole.Count) captures.AddRange(ReserveGroupCopies(state, nest, whole, true));
                if (rootCapture || captures.Count > 0)
                {
                    // 写しを取る: ブロック全体を（使える写しがあればそこから）合成する
                    var job = NewJob(state, bx, by, whole, start);
                    if (rootCapture) { job.CaptureAt = first; job.Capture = state.Below; state.BelowIndex = 0; state.BelowVersion++; } // 取り終えるまで使えない
                    else if (!usable) InvalidateBelow(state);
                    job.Resume = resume; job.Captures = captures;
                    jobs.Add(job);
                }
                else
                {
                    // 写しのすぐ上から変わった、写しより下が変わった（一番下の層に描くなど）、写しを取れない: 変わったタイルだけを（使えれば写しから）
                    // 合成する。古くなった写しは捨てる（変わっていないタイルでも、項目の並びが変われば下の部分の合成は変わり得る）
                    if (!usable) InvalidateBelow(state);
                    foreach (var r in CoverTiles(tiles, TilesX, TilesY)) { var job = NewJob(state, bx, by, r, start); job.Resume = resume; jobs.Add(job); }
                }
                if (usable) LastBelowReuseCount++;
                LastNestedReuseCount += resume.Count;
            }
            state.Sigs = sigs;
            for (int i = first0; i < jobs.Count; i++) jobs[i].Send = full ? null : tiles;
        }
        /// <summary>写しのうち、この合成が中身を始めから合成するはずのグループのもの（最上段の start 以上で、中身を写しから始める外側のグループの
        /// 写しより上）。浅いグループから決める。</summary>
        static List<(GroupCopy Copy, int[] Address)> ReachableCopies(NestPlan nest, int start)
        {
            var list = new List<(GroupCopy Copy, int[] Address)>();
            if (nest == null) return list;
            foreach (var c in nest.Resume.Values) list.Add((c, nest.Diff.Groups[c.Group].Address));
            list.Sort((a, b) => a.Address.Length.CompareTo(b.Address.Length));
            var reached = new List<(GroupCopy Copy, int[] Address)>();
            foreach (var item in list)
            {
                if (item.Address[0] < start) continue;
                bool inside = false;
                foreach (var outer in reached)
                    if (CpuCompositor.SkipsNestedGroup(outer.Address, outer.Copy.Index, item.Address)) { inside = true; break; }
                if (!inside) reached.Add(item);
            }
            return reached;
        }
        /// <summary>違う道に沿って取るグループの写しの配列を、浅い段から予算の内側で用意する（同じグループの写しがあればそれへ取り直す。足りなければ
        /// その段は取らない）。</summary>
        /// <param name="spare">先回りの写し（空いた予算だけで）を用意する。false なら違う道の写し。</param>
        List<(GroupCopy Copy, int[] Address, int Index)> ReserveGroupCopies(CpuBlock state, NestPlan nest, TileRect whole, bool spare)
        {
            var list = new List<(GroupCopy Copy, int[] Address, int Index)>();
            if (nest == null) return list;
            IEnumerable<Guid> order = spare ? (IEnumerable<Guid>)nest.Spare : nest.Diff.Path.ConvertAll(p => p.Group);
            foreach (var group in order)
            {
                if (!nest.Capture.TryGetValue(group, out int index)) continue;
                var d = nest.Diff.Groups[group];
                if (!nest.Resume.TryGetValue(group, out var copy))
                {
                    long bytes = BelowBytes(whole);
                    var pixels = RentResidentPixels(bytes, spare);
                    if (pixels == null) continue;
                    copy = new GroupCopy { Group = group, Pixels = pixels };
                }
                copy.Isolated = d.Node.Isolated; copy.Depth = d.Address.Length;
                list.Add((copy, d.Address, index));
            }
            return list;
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
        /// <summary>jobs[from, to) を 1 回の CompositeRegions で合成し、表示へ送る。戻り値は合成と送りにかかった時間（ms）。</summary>
        (double composite, double send) RunCpuWave(PaintDocument doc, PaintChannel channel, List<CpuJob> jobs, int from, int to, Func<double> elapsed)
        {
            double t0 = elapsed();
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
            for (int i = from; i < to; i++)
            {
                var j = jobs[i];
                int bx0 = j.Bx * blockSize, by0 = j.By * blockSize, bw = Math.Min(blockSize, width - bx0), offset = ((j.Y - by0) * bw + (j.X - bx0)) * 4;
                if (j.Resume != null && j.Resume.Count > 0)
                {
                    var resume = new List<CpuCompositor.NestedCopy>(j.Resume.Count);
                    foreach (var (copy, address) in j.Resume) resume.Add(new CpuCompositor.NestedCopy(address, copy.Index, copy.Pixels, offset, bw * 4));
                    core[i - from].Resume = resume;
                }
                if (j.Captures != null && j.Captures.Count > 0)
                {
                    var captures = new List<CpuCompositor.NestedCopy>(j.Captures.Count);
                    foreach (var (copy, address, index) in j.Captures) captures.Add(new CpuCompositor.NestedCopy(address, index, copy.Pixels, offset, bw * 4));
                    core[i - from].Captures = captures;
                }
            }
            CpuCompositor.CompositeRegions(doc, channel, core);
            LastCpuCompositeCalls++; LastCpuJobCount += to - from;
            if (BlockCompositedForTests != null)
            {
                var seen = new HashSet<(int, int)>();
                for (int i = from; i < to; i++) if (seen.Add((jobs[i].Bx, jobs[i].By))) BlockCompositedForTests(jobs[i].Bx, jobs[i].By);
            }
            double t1 = elapsed();
            for (int i = from; i < to; i++)
            {
                var j = jobs[i];
                if (j.CaptureAt >= 0) { j.State.BelowIndex = j.CaptureAt; j.State.BelowVersion++; }
                if (j.Captures != null) foreach (var (copy, _, index) in j.Captures) { copy.Index = index; j.State.Groups[copy.Group] = copy; LastNestedCaptureCount++; }
                if (Path == CompositePath.CpuTiles) SendRegion(j.Pixels, j.X, j.Y, j.W, j.Rect, j.Send);
                else for (int row = 0; row < j.H; row++) Buffer.BlockCopy(j.Pixels, row * j.W * 4, cpuPixels, ((j.Y + row) * width + j.X) * 4, j.W * 4);
                ReturnCpu(j.Pixels); j.Pixels = null;
            }
            return (t1 - t0, elapsed() - t1);
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
            state.Below = RentResidentPixels(bytes, false);
            return state.Below != null;
        }
        long BelowBytes(TileRect whole) => 4L * (Math.Min(whole.X1 * tileSize, width) - whole.X0 * tileSize) * (Math.Min(whole.Y1 * tileSize, height) - whole.Y0 * tileSize);
        bool MakeRoomCpu(long bytes)
        {
            if (bytes > ResidentBudgetBytes) return false;
            while (ResidentBytes + bytes > ResidentBudgetBytes)
            {
                if (residentPixelPool.Count > 0) { DropPooledResidentPixels(); continue; }
                CpuBlock victim = null; int oldest = int.MaxValue;
                foreach (var b in cpuBlocks.Values) if ((b.Below != null || b.Groups.Count > 0) && b.LastUsed < updateIndex && b.LastUsed < oldest) { oldest = b.LastUsed; victim = b; }
                if (victim == null) return false;
                // ブロックの写しは深いグループの写しから捨て、最上段の下の写しは最後に
                var deepest = DeepestCopy(victim.Groups);
                if (deepest != null) { ReleaseGroupCopy(deepest); victim.Groups.Remove(deepest.Group); }
                else ReleaseBelow(victim);
            }
            return true;
        }
        void ReleaseBelow(CpuBlock b) { ReleaseSampled(b); if (b.Below == null) return; ResidentBytes -= b.Below.Length; b.Below = null; b.BelowIndex = 0; b.BelowVersion++; }
        static void InvalidateBelow(CpuBlock b) { b.BelowIndex = 0; b.BelowVersion++; }
        void TrimIdleCpu()
        {
            int limit = updateIndex - IdleUpdatesBeforeRelease;
            foreach (var b in cpuBlocks.Values) if (b.LastUsed < limit) { if (b.Below != null) ReleaseBelow(b); ReleaseGroupCopies(b.Groups); }
            if (residentPixelPoolLastUsed < limit) ClearResidentPixelPool();
        }
        void ClearCpuBlocks()
        {
            foreach (var b in cpuBlocks.Values) { ReleaseBelow(b); ReleaseGroupCopies(b.Groups); }
            cpuBlocks.Clear();
            ClearResidentPixelPool();
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
            var sigs = BuildSigs(plan, channel, bx, by);
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
            // グループの写し: 今も正しいものから中身を始め、違う道に沿って取る
            var nest = PlanNested(state.Groups, state.Sigs == null ? null : Diff(state.Sigs, sigs), remember, start);
            int capture = remember && first < plan.Count ? first : -1;
            bool captured = false;
            for (int i = start; i < plan.Count; i++)
            {
                if (i == capture && i > start) { CaptureBelow(state, current, i); captured = true; }
                current = CompositeEntry(plan[i], top, current, 0, channel, bx, by, remember && i >= first, false, nest);
            }
            // 下の写しは「新しい署名の先頭 BelowIndex 項目」の合成でなければならない。取り直さず、先頭が変わっていたら無効にする
            if (!captured && state.BelowIndex > first) state.BelowIndex = 0;
            if (nest != null) foreach (var pair in nest.Taken) state.Groups[pair.Key] = pair.Value;
            CopyBlockToComposite(current, bx, by);
            state.Sigs = sigs;
        }
        /// <summary>入れ子が深すぎる（または <see cref="CompositeGroupsOnCpu"/>）とき: グループを飛ばして GPU でブロックを合成し、グループが
        /// 触れるタイルだけ CPU の正本の式で上書きする。触れないタイルではグループの寄与は無い（分離は透明、通過は下のまま）ので正しい。</summary>
        void CompositeBlockWithCpuGroups(PaintDocument doc, IReadOnlyList<CpuCompositor.StackEntry> plan, PaintChannel channel, int bx, int by)
        {
            if (blocks.TryGetValue(BlockKey(bx, by), out var state)) { Release(state.Below); ResidentBytesRemove(state.Below); ReleaseGroupCopies(state.Groups); blocks.Remove(BlockKey(bx, by)); }
            LastBlockCount++;
            var top = LevelAt(0);
            Clear(top.A);
            RenderTexture current = top.A;
            foreach (var entry in plan) current = CompositeEntry(entry, top, current, 0, channel, bx, by, false, true, null);
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

        void CaptureBelow(BlockState state, RenderTexture current, int index)
        {
            if (state.Below == null)
            {
                if (!MakeRoom(BlockBytes, forCopy: true)) { state.BelowIndex = 0; return; }
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
            AppendSignature(sb, entry, channel, bx, by, null);
            return sb.ToString();
        }
        /// <param name="children">null, or the signatures of a group's contents made before (the same strings, not made again).</param>
        void AppendSignature(StringBuilder sb, CpuCompositor.StackEntry entry, PaintChannel channel, int bx, int by, SigNode[] children, SigNode[] clips = null)
        {
            var layer = entry.Base;
            // 不透明度と合成モードは、このチャンネルでのもの（層のチャンネルごとの設定があればそれ）
            sb.Append(layer.Id.ToString("N")).Append('|').Append((int)layer.Kind).Append('|').Append(entry.Opacity.ToString("R", CultureInfo.InvariantCulture))
              .Append('|').Append((int)entry.BlendMode);
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
                    layer.FillValues.TryGetValue(channel, out var c); // 画像のチャンネルは下の出力の印（塗りつぶしの版を含む）で変わる
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
                    if (children != null) foreach (var child in children) sb.Append(child.Sig).Append(';');
                    else foreach (var child in entry.Children) { AppendSignature(sb, child, channel, bx, by, null); sb.Append(';'); }
                    sb.Append(']');
                    break;
            }
            if (entry.ClipEntries.Count > 0)
            {
                sb.Append("|c[");
                if (clips != null) foreach (var clip in clips) sb.Append(clip.Sig).Append(';');
                else foreach (var clip in entry.ClipEntries) { AppendSignature(sb, clip, channel, bx, by, null); sb.Append(';'); }
                sb.Append(']');
            }
        }
        static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        /// <summary>フィルターのある層と画像を投影する塗りつぶし: 出力の印（スタックの版と、halo だけ広げた範囲の入力の書き換え番号。塗りつぶしは塗りつぶしの版）。</summary>
        void AppendFilterStamp(StringBuilder sb, PaintLayer layer, PaintChannel channel, int bx, int by)
        {
            if (layer.HasEvaluatedOutput(channel)) sb.Append("|F").Append(layer.OutputStamp(channel, bx * blockTiles, by * blockTiles, (bx + 1) * blockTiles, (by + 1) * blockTiles));
        }
        long BlockRevision(SparseTileSurface surface, int bx, int by)
        { return surface.MaxTileRevision(bx * blockTiles, by * blockTiles, (bx + 1) * blockTiles, (by + 1) * blockTiles); }

        // ───────────── 入れ子の合成（CpuCompositor.EvaluateSpan と同じ順と式） ─────────────

        /// <summary>不透明度 1 でマスクが効いていない通過グループは、フェードが中身そのもの（CpuCompositor.Fade の amount ≥ 1）なので
        /// 同じ段で重ねられる。</summary>
        static bool PassesThroughWhole(CpuCompositor.StackEntry e)
        { return e.PassesThrough && e.Opacity >= 1 && (e.Base.Mask == null || e.Base.Mask.IsNeutral); }
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
            foreach (var entry in plan) current = CompositeEntry(entry, level, current, depth, channel, bx, by, keep, skipGroups, null);
            return current;
        }
        /// <summary>計画の項目を 1 つ current（level の A か B。下の結果が入っている）の上に重ね、結果の入った作業ブロック（level の A か B）を
        /// 返す。keep: 使う入力を GPU に残す。skipGroups: グループを飛ばす（CPU で上書きするタイル用）。nest: グループの写し（null なら使わない）。
        /// マテリアルの値は、入れ子の合成が書き換えるので、各 Blit の直前に設定する。</summary>
        RenderTexture CompositeEntry(CpuCompositor.StackEntry entry, Level level, RenderTexture current, int depth, PaintChannel channel, int bx, int by, bool keep, bool skipGroups, NestPlan nest)
        {
            var layer = entry.Base;
            if (layer.Kind == LayerKind.Adjustment)
            {
                // 自分の画素は無く、下の合成結果に調整をかける。透明な画素はシェーダーがそのまま返す。
                SetAdjustment(layer.Adjustment); SetLayer(entry.Opacity, entry.BlendMode, layer.Mask, bx, by, keep);
                return Step(current, level, 2, null);
            }
            if (layer.IsGroup && (skipGroups || !TouchesBlock(entry, channel, bx, by))) return current;
            // グループの写し: 中身をそこから始める（通過は下の結果を含む）、中身の途中で取る
            GroupCopy resume = null; int captureAt = -1;
            if (nest != null && layer.IsGroup) { nest.Resume.TryGetValue(layer.Id, out resume); if (!nest.Capture.TryGetValue(layer.Id, out captureAt)) captureAt = -1; }
            int from = resume?.Index ?? 0;
            if (resume != null) LastNestedReuseCount++;
            if (entry.PassesThrough)
            {
                if (PassesThroughWhole(entry))
                {
                    if (resume != null) { var next = current == level.A ? level.B : level.A; Blit(resume.Texture, next, 1, null); current = next; }
                    return CompositeChildren(entry, level, current, depth, from, captureAt, channel, bx, by, keep, skipGroups, nest);
                }
                // 下の結果を 1 つ深い段へ写し、そこへ中身を重ね、下と中身を不透明度×マスクでフェードする
                var inner = LevelAt(depth + 1);
                Blit(resume != null ? resume.Texture : (Texture)current, inner.A, 1, null);
                var innerResult = CompositeChildren(entry, inner, inner.A, depth + 1, from, captureAt, channel, bx, by, keep, skipGroups, nest);
                SetLayer(entry.Opacity, LayerBlendMode.Normal, layer.Mask, bx, by, keep);
                return Step(current, level, 4, innerResult);
            }
            Source source;
            if (layer.IsGroup)
            {
                // 分離合成: 1 つ深い段の作業ブロックで、透明（か写し）から中身を合成する
                var inner = LevelAt(depth + 1);
                if (resume != null) Blit(resume.Texture, inner.A, 1, null); else Clear(inner.A);
                source = new Source { Texture = CompositeChildren(entry, inner, inner.A, depth + 1, from, captureAt, channel, bx, by, keep, skipGroups, nest) };
            }
            else if (!TryGetSource(layer, channel, bx, by, keep, out source)) return current; // このブロックに画素が無い（クリッピングのまとまりも透明）
            if (entry.ClipEntries.Count > 0) source = new Source { Texture = BuildClippingGroup(entry, source, level, depth, channel, bx, by, keep, skipGroups, nest) };
            SetLayer(entry.Opacity, ModeOf(entry), layer.Mask, bx, by, keep);
            SetSource(source);
            return Step(current, level, 0, source.Texture);
        }
        /// <summary>グループの中身の項目 [from, 数) を current（level の A か B）の上に重ねる。captureAt（−1 でなければ from 以上）の項目の前で、
        /// 内側の結果をグループの写しに取る（中身の数なら最後に）。</summary>
        RenderTexture CompositeChildren(CpuCompositor.StackEntry group, Level level, RenderTexture current, int depth, int from, int captureAt, PaintChannel channel, int bx, int by, bool keep, bool skipGroups, NestPlan nest)
        {
            var children = group.Children;
            for (int i = from; i < children.Count; i++)
            {
                if (i == captureAt) TakeGroupCopy(nest, group, i, current);
                current = CompositeEntry(children[i], level, current, depth, channel, bx, by, keep, skipGroups, nest);
            }
            if (captureAt == children.Count) TakeGroupCopy(nest, group, captureAt, current);
            return current;
        }
        /// <summary>グループの内側の結果（中身の先頭 index 項目）を写しに取る。同じグループの写しを読んだ後ならその RenderTexture へ（読みはグループの
        /// 始めで済んでいる）、無ければ予算の内側で作る（足りなければ取らない）。</summary>
        void TakeGroupCopy(NestPlan nest, CpuCompositor.StackEntry group, int index, RenderTexture current)
        {
            var id = group.Base.Id;
            if (!nest.Resume.TryGetValue(id, out var copy))
            {
                // 先回りの写しは空いた予算だけで（違う道の写しの分の余りを残す）。違う道の写しは古い写しを捨ててでも取る
                if (nest.Spare.Contains(id) ? !SpareRoom(BlockBytes) : !MakeRoom(BlockBytes, forCopy: true)) return;
                copy = new GroupCopy { Group = id, Texture = MakeRt(blockSize, blockSize, FilterMode.Point) }; ResidentBytes += BlockBytes;
            }
            Blit(current, copy.Texture, 1, null);
            copy.Index = index; copy.Isolated = nest.Diff.Groups[id].Node.Isolated; copy.Depth = nest.Diff.Groups[id].Address.Length;
            nest.Taken[id] = copy; LastNestedCaptureCount++;
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
        RenderTexture BuildClippingGroup(CpuCompositor.StackEntry entry, Source baseSource, Level level, int depth, PaintChannel channel, int bx, int by, bool keep, bool skipGroups, NestPlan nest)
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
                    GroupCopy resume = null; int captureAt = -1;
                    if (nest != null) { nest.Resume.TryGetValue(c.Id, out resume); if (!nest.Capture.TryGetValue(c.Id, out captureAt)) captureAt = -1; }
                    var inner = LevelAt(depth + 1);
                    if (resume != null) { Blit(resume.Texture, inner.A, 1, null); LastNestedReuseCount++; } else Clear(inner.A);
                    source.Texture = CompositeChildren(clip, inner, inner.A, depth + 1, resume?.Index ?? 0, captureAt, channel, bx, by, keep, skipGroups, nest); pass = 3;
                }
                else
                {
                    if (!TryGetSource(c, channel, bx, by, keep, out source)) continue;
                    pass = 3;
                }
                SetLayer(clip.Opacity, c.Kind == LayerKind.Adjustment ? clip.BlendMode : ModeOf(clip), c.Mask, bx, by, keep);
                if (pass == 3) SetSource(source);
                Blit(current, other, pass, source.Texture);
                var swap = current; current = other; other = swap;
            }
            return current;
        }
        /// <summary>計画の項目の合成モード（このチャンネルでのもの。通過は Normal）。</summary>
        static LayerBlendMode ModeOf(CpuCompositor.StackEntry entry) { return entry.BlendMode == LayerBlendMode.PassThrough ? LayerBlendMode.Normal : entry.BlendMode; }
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
            if (layer.HasEvaluatedOutput(channel))
            {
                // フィルターのある層と画像を投影する塗りつぶしは、Core が（halo 込みで）評価した出力のタイルを載せる（CPU の正本と同じバイト）
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
        /// <summary>層の入力を先に捨て、合成結果の写しを優先して残す。入力の確保は合成結果を追い出さない。
        /// 合成結果を取る地点では入力を読み終えているので、この Update で使った入力も捨てられる。作れなければ false。</summary>
        bool MakeRoom(long bytes, bool forCopy = false)
        {
            if (bytes > ResidentBudgetBytes) return false;
            while (ResidentBytes + bytes > ResidentBudgetBytes)
            {
                (long, long) victimKey = default; Resident victim = null; BlockState victimBlock = null; int oldest = int.MaxValue;
                foreach (var pair in residents) if ((forCopy || pair.Value.LastUsed < updateIndex) && pair.Value.LastUsed < oldest) { oldest = pair.Value.LastUsed; victim = pair.Value; victimKey = pair.Key; }
                if (victim == null && forCopy)
                    foreach (var b in blocks.Values) if ((b.Below != null || b.Groups.Count > 0) && b.LastUsed < updateIndex && b.LastUsed < oldest) { oldest = b.LastUsed; victimBlock = b; }
                if (victimBlock != null)
                {
                    // ブロックの写しは深いグループの写しから捨て、最上段の下の写しは最後に
                    var deepest = DeepestCopy(victimBlock.Groups);
                    if (deepest != null) { ReleaseGroupCopy(deepest); victimBlock.Groups.Remove(deepest.Group); }
                    else { Release(victimBlock.Below); victimBlock.Below = null; victimBlock.BelowIndex = 0; ResidentBytes -= BlockBytes; }
                    LastCopyEvictionCount++;
                }
                else if (victim != null) { DestroyTexture(victim.Texture); residents.Remove(victimKey); ResidentBytes -= BlockBytes; LastInputEvictionCount++; }
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
            foreach (var b in blocks.Values)
                if (b.LastUsed < limit)
                {
                    if (b.Below != null) { Release(b.Below); b.Below = null; b.BelowIndex = 0; ResidentBytes -= BlockBytes; }
                    ReleaseGroupCopies(b.Groups);
                }
        }
        void ClearBlockStates()
        {
            foreach (var b in blocks.Values) { if (b.Below != null) { Release(b.Below); ResidentBytes -= BlockBytes; } ReleaseGroupCopies(b.Groups); }
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
            tileBuffer = null; blockBuffer = null; cpuPixels = null; material = null; tileUpload = null; cpuFallback = null; composite = null; cpuTile = null;
            pending.Clear(); // 残った予定は捨てる（文書を持ち続けない）
            ReleasePreviewTexture(); previewed.Clear(); previewSerial = -1; lastFinished = true;
            if (previewMaterial != null) UnityEngine.Object.DestroyImmediate(previewMaterial);
            previewMaterial = null;
            lastDocument = null; lastSerial = -1; blockSize = blockTiles = 0;
        }
    }
}
