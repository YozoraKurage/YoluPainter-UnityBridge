using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// Live Link の元の絵: モデルを送ったあと、Color の流し込み先（スタンドアロンが描く絵で見せるプロパティ）に入っている元のテクスチャを、
    /// スタンドアロンが新しく作ったテクスチャセットの一番下のレイヤーにできるように送る。つないだ直後に見た目が変わらず、その上に描ける。
    /// 読むだけで、元のテクスチャ・インポート設定・マテリアルには書かない（読めるようにもしない）。
    /// 読み方（スタンドアロンの層の欄に出る）:
    /// <list type="bullet">
    /// <item><b>原本のファイル</b>（PNG・TGA・JPG）: インポートが絵を変えない設定のときだけ（<see cref="ImportKeepsPixels"/>。透明な画素の RGB を周りの色で
    /// 埋める設定・PNG のガンマで画素を補正する取り込みは「絵を変える」）。圧縮の無い本当の値で、透明な画素の RGB も原本のまま。</item>
    /// <item><b>取り込み済みの絵</b>（PSD など、またはインポートが絵を変える設定）: <see cref="UnityTextureReader"/>。CPU が読める絵はその値、
    /// 読めない絵は GPU に描いて読み戻す（圧縮されていれば GPU が解いた値で、原本のファイルの値とは少し違う）。</item>
    /// <item><b>GPU を通して</b>: アセットでない絵（実行時に作った絵・RenderTexture など）。</item>
    /// </list>
    /// 辺は <see cref="MaxEdge"/> まで（超えれば縮めずに断る）、1 回のモデルの送りで画素 <see cref="Budget"/> バイトまで（超えれば断る）。
    /// 絵が付かないとき（読めない・大きすぎる・予算を超える）も、スロットの様子だけは必ず送る（スタンドアロンは、揃うまでそのセットを出さない）。
    /// 積んだ命令が <see cref="PendingLimit"/> バイト以上たまっている間は、次を読まない（送る速さに合わせる）。
    /// 同じテクスチャを使うマテリアルが何枚あっても、読むのは 1 回（画素は使い回す）。送りと予算は枠（マテリアル）ごと: 枠ごとにスタンドアロンが別のセット
    /// として画素を持つので、送った量をそのまま数える。
    /// <para>頼み（機能の印 <see cref="LiveLinkBridge.FeatureMaterialRequest"/> が双方にあるとき）: スタンドアロンが元の絵を入れるセットのマテリアルだけを頼む。
    /// Unity は元の絵を自分から押し出さず、頼まれた絵を読んで送る（<see cref="Plan"/>ではなく <see cref="PlanRequested"/>）。頼みには、スタンドアロンが手元に持つ絵の
    /// 印（<see cref="StampOf"/>）が付くことがあり、今の印と同じなら、絵を読まずに画素なしの <see cref="LiveLinkOriginalState.Cached"/> で答える。
    /// 印はテクスチャのアセット（GUID・ローカルのファイル ID）・取り込みの結果の印（<see cref="AssetDatabase.GetAssetDependencyHash(string)"/>。元のファイルの中身・
    /// インポート設定・インポーターの版が変われば変わる）・取り込んだ絵の中身のハッシュ・原本のファイルの長さと更新時刻・大きさ・プロジェクトの色空間・ビルドターゲットを
    /// 混ぜて決める。どれか 1 つでも取れない・アセットでない絵は 0（印なし。必ず画素を送る）。印は読む前に取る: 読む間にファイルが変わっても、古い絵に新しい印を付けない。</para>
    /// </summary>
    internal static class LiveLinkOriginals
    {
        /// <summary>送る絵の辺の上限（スタンドアロンの MAX_ORIGINAL_SIZE と同じ）。</summary>
        public const int MaxEdge = ImageContent.MaxSide;
        /// <summary>1 回のモデルの送りで送る絵の画素のバイト数の合計の上限。</summary>
        public const long Budget = 1L << 30;
        /// <summary>積んだ命令がこのバイト数以上たまっている間は、次の絵を読まない。</summary>
        public const long PendingLimit = 96L << 20;
        /// <summary>1 回の <see cref="Sender.Pump"/> で読み・送る時間の目安（ミリ秒。絵 1 枚は途中で止めないので、最初の 1 枚は必ず読む）。</summary>
        public const double PumpMs = 30;

        /// <summary>原本のファイルとして読む拡張子。</summary>
        static readonly string[] FileExtensions = { ".png", ".tga", ".jpg", ".jpeg" };

        /// <summary>送る絵 1 つ（モデルのマテリアルの番号と、Color の流し込み先のプロパティ）。</summary>
        internal sealed class Job
        {
            public int Material; public string Property;
            /// <summary>送る前に見た、Unity が見せているテクスチャの大きさ（読めなかったときの様子に付ける）。</summary>
            public int Width, Height;
            /// <summary>Plan のときのテクスチャの同一性（同じテクスチャのマテリアルを隣に並べて、画素を使い回すため）。</summary>
            public int TextureId;
            /// <summary>スタンドアロンが手元に持つ絵の印（頼みの have。0 は持たない・押し出し）。今の印と同じなら、絵を読まずに Cached で答える。</summary>
            public ulong Have;
        }

        /// <summary>読んだ結果（<see cref="Load"/>）。</summary>
        internal sealed class Loaded
        {
            public LiveLinkOriginalState State = LiveLinkOriginalState.Image;
            public LiveLinkOriginalRead Read;
            public bool Compressed, Srgb;
            public int Width, Height;
            public byte[] Pixels;
            /// <summary>読めなかった理由（ログ用）。</summary>
            public string Reason;
            /// <summary>この絵の印（読む前に取ったもの。0 は印なし）。</summary>
            public ulong Stamp;
        }

        /// <summary>モデルのマテリアルのうち、Color の流し込み先のプロパティに絵が入っているものの、送る絵の一覧（スタンドアロンが来るはずの元の絵と同じ決め方）。</summary>
        public static List<Job> Plan(LiveLinkModel model)
        {
            var jobs = new List<Job>();
            for (int i = 0; i < model.Materials.Count; i++)
            {
                var e = model.Materials[i];
                if (e.Material == null) continue;
                var route = e.Shown.FirstOrDefault(c => c.Channel == PaintChannel.Color);
                if (route == null || !e.Material.HasProperty(route.Property)) continue;
                var t = e.Material.GetTexture(route.Property);
                if (t == null || t.width <= 0 || t.height <= 0) continue;
                jobs.Add(new Job { Material = i, Property = route.Property, Width = t.width, Height = t.height, TextureId = t.GetInstanceID() });
            }
            return jobs;
        }

        /// <summary>頼まれた元の絵（マテリアルの番号・スロット・スタンドアロンが持つ絵の印）の、送る絵の一覧。<see cref="Plan"/> と同じ決め方で、頼みに合う枠だけ。
        /// 頼まれた枠に今は絵が入っていない（絵が外された・スロットが違う・マテリアルが無い）ものは、絵の無い様子（読めない）で答える枠にする（頼んだ側を待たせ続けない）。
        /// 範囲外のマテリアルの番号は答えない。同じ枠を重ねて頼まれても 1 つ。</summary>
        public static List<Job> PlanRequested(LiveLinkModel model, IEnumerable<(int Material, string Slot, ulong Have)> requested)
        {
            var planned = Plan(model);
            var jobs = new List<Job>();
            var seen = new HashSet<(int, string)>();
            foreach (var (material, slot, have) in requested)
            {
                if (material < 0 || material >= model.Materials.Count || !seen.Add((material, slot))) continue;
                var job = planned.FirstOrDefault(j => j.Material == material && j.Property == slot);
                if (job == null) job = new Job { Material = material, Property = slot, TextureId = int.MinValue + material };
                job.Have = have;
                jobs.Add(job);
            }
            return jobs;
        }

        /// <summary>この版の読み方の番号。読み方（原本のファイルから・取り込んだ絵から・GPU を通して、の決め方や変換）を変えたら上げる（手元の絵の印が替わり、古い読み方の絵を使わない）。</summary>
        internal const int ReaderVersion = 1;

        /// <summary>
        /// 元のテクスチャの印（スタンドアロンが手元の絵と今の絵が同じかを決める。0 は印なし）。プロジェクトのアセットのテクスチャだけが印を持つ。
        /// 絵を読む前に取る（読む間に変わっても、古い絵に新しい印を付けない）。取れない物が 1 つでもあれば 0（間違って古い絵を使わない側に倒す）。
        /// </summary>
        public static ulong StampOf(Texture texture)
        {
            try
            {
                if (!(texture is Texture2D t2d) || !AssetDatabase.IsMainAsset(t2d)) return 0;
                string path = AssetDatabase.GetAssetPath(t2d);
                if (!IsProjectAsset(path)) return 0;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(t2d, out string guid, out long fileId) || string.IsNullOrEmpty(guid)) return 0;
                var dependency = AssetDatabase.GetAssetDependencyHash(path);
                if (!dependency.isValid) return 0;
                var info = new FileInfo(Path.GetFullPath(path));
                if (!info.Exists) return 0;
                var contents = t2d.imageContentsHash;
                var h = new Stamper();
                h.Add(ReaderVersion); h.Add(guid); h.Add(fileId); h.Add(dependency.ToString());
                h.Add(info.Length); h.Add(info.LastWriteTimeUtc.Ticks);
                h.Add(contents.isValid ? contents.ToString() : "-");
                h.Add(t2d.width); h.Add(t2d.height);
                h.Add((int)PlayerSettings.colorSpace); h.Add((int)EditorUserBuildSettings.activeBuildTarget);
                ulong value = h.Value;
                return value == 0 ? 1 : value;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>64 ビットの FNV-1a（印を決めるだけ。保存しない。文字列は UTF-8、数は 8 バイトのリトルエンディアンで混ぜる）。</summary>
        struct Stamper
        {
            ulong value; bool started;
            public ulong Value => started ? value : 14695981039346656037UL;
            void Byte(byte b)
            {
                if (!started) { value = 14695981039346656037UL; started = true; }
                value ^= b; value *= 1099511628211UL;
            }
            public void Add(long x) { for (int i = 0; i < 8; i++) Byte((byte)(x >> (i * 8))); }
            public void Add(string s) { var bytes = System.Text.Encoding.UTF8.GetBytes(s ?? ""); Add(bytes.Length); foreach (var b in bytes) Byte(b); }
        }

        /// <summary>
        /// 元のテクスチャ 1 つを読む。投げない（読めなければ <see cref="LiveLinkOriginalState.Unreadable"/>・大きすぎれば <see cref="LiveLinkOriginalState.TooLarge"/>）。
        /// ガンマの色空間のプロジェクトは、スタンドアロンの Color も画素をそのまま使うので、sRGB は真にする。
        /// </summary>
        public static Loaded Load(Texture texture)
        {
            var result = new Loaded();
            try
            {
                if (texture == null) { result.State = LiveLinkOriginalState.Unreadable; result.Reason = L.Tr("The texture is gone."); return result; }
                result.Width = texture.width; result.Height = texture.height;
                result.Stamp = StampOf(texture); // 読む前に取る
                if (texture.dimension != UnityEngine.Rendering.TextureDimension.Tex2D) { result.State = LiveLinkOriginalState.Unreadable; result.Reason = L.Tr("The texture is not a 2D texture."); return result; }
                if (texture.width > MaxEdge || texture.height > MaxEdge) { result.State = LiveLinkOriginalState.TooLarge; result.Reason = L.Tr("The texture is {0} × {1}; the most sent is {2} on a side.", texture.width, texture.height, MaxEdge); return result; }
                if (texture is Texture2D t2d && AssetDatabase.IsMainAsset(t2d) && IsProjectAsset(AssetDatabase.GetAssetPath(t2d)))
                {
                    if (!TryReadFile(t2d, result)) ReadImported(t2d, result);
                }
                else ReadGpu(texture, result);
            }
            catch (Exception e)
            {
                result.State = LiveLinkOriginalState.Unreadable; result.Pixels = null;
                result.Reason = L.Tr("The texture could not be read ({0}).", e.Message);
            }
            if (result.State == LiveLinkOriginalState.Image && PlayerSettings.colorSpace == ColorSpace.Gamma) result.Srgb = true;
            if (result.State != LiveLinkOriginalState.Image) result.Pixels = null;
            return result;
        }

        static bool IsProjectAsset(string path) => !string.IsNullOrEmpty(path) && (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal));

        /// <summary>インポート設定が絵を変えず、原本のファイルが読めるなら、そのファイルから読む（<see cref="LiveLinkOriginalRead.File"/>）。読まなければ false。</summary>
        static bool TryReadFile(Texture2D texture, Loaded result)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (!FileExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return false;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (!ImportKeepsPixels(importer)) return false;
            string full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > ImageFiles.MaxFileBytes) return false;
            var bytes = File.ReadAllBytes(full);
            // PNG に gAMA があり、「PNG のガンマを無視」が偽だと、Unity は取り込みで画素の値を補正する（取り込んだ絵はファイルの画素と違う）
            if (RgbaPng.LooksLikePng(bytes) && !importer.ignorePngGamma && PngHasGamma(bytes)) return false;
            var (rgba, w, h) = DecodeFile(bytes);
            if (rgba == null || w > MaxEdge || h > MaxEdge) return false;
            result.Pixels = rgba; result.Width = w; result.Height = h;
            result.Read = LiveLinkOriginalRead.File; result.Compressed = false; result.Srgb = importer.sRGBTexture;
            return true;
        }

        /// <summary>このインポート設定は、ファイルの画素をそのまま絵にするか（法線マップ・スプライト・アルファの取り出し・法線への変換・「アルファを透明として扱う」
        /// （完全に透明な画素の RGB を周りの色で埋める）などは、絵を変える）。PNG のガンマ（gAMA）による補正は、ファイルの中身を見て <see cref="TryReadFile"/> が決める。</summary>
        internal static bool ImportKeepsPixels(TextureImporter importer)
        {
            if (importer == null) return false;
            if (importer.textureType != TextureImporterType.Default) return false;
            if (importer.textureShape != TextureImporterShape.Texture2D) return false;
            if (importer.alphaSource != TextureImporterAlphaSource.FromInput) return false;
            if (importer.convertToNormalmap) return false;
            if (importer.alphaIsTransparency) return false;
            return true;
        }

        /// <summary>PNG が gAMA を持つか（IDAT より前のチャンクの並びだけを見る）。壊れていれば false（読みはそのあとの復号が断る）。</summary>
        internal static bool PngHasGamma(byte[] bytes)
        {
            int pos = 8;
            while (bytes != null && pos + 12 <= bytes.Length)
            {
                long length = ((long)bytes[pos] << 24) | ((long)bytes[pos + 1] << 16) | ((long)bytes[pos + 2] << 8) | bytes[pos + 3];
                string type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
                if (type == "gAMA") return true;
                if (type == "IDAT" || type == "IEND" || length > bytes.Length) return false;
                pos += 12 + (int)length;
            }
            return false;
        }

        /// <summary>原本のファイルの中身を、straight RGBA8（行は下から）に読む。読めなければ null。PNG は厳密に（透明な画素の RGB を守る）、
        /// TGA は <see cref="TgaReader"/>、そのほか（パレット・16 ビットの PNG・JPG）は Unity の復号器。</summary>
        internal static (byte[] rgba, int width, int height) DecodeFile(byte[] bytes)
        {
            if (RgbaPng.LooksLikePng(bytes))
            {
                try { return RgbaPng.Decode(bytes, MaxEdge); }
                catch (InvalidDataException) { /* パレット・16 ビット・インターレースなど: Unity の復号器で読む */ }
                catch (ResourceRefusedException) { return (null, 0, 0); }
            }
            else if (bytes.Length >= 18)
            {
                var tga = TgaReader.Decode(bytes, MaxEdge, out int tw, out int th);
                if (tga != null) return (tga, tw, th);
                if (!LooksLikeJpeg(bytes)) return (null, 0, 0);
            }
            if (!RgbaPng.LooksLikePng(bytes) && !LooksLikeJpeg(bytes)) return (null, 0, 0);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!texture.LoadImage(bytes, false) || texture.width > MaxEdge || texture.height > MaxEdge) return (null, 0, 0);
                var pixels = texture.GetPixels32(0);
                var rgba = new byte[(long)pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++) { int o = i * 4; var c = pixels[i]; rgba[o] = c.r; rgba[o + 1] = c.g; rgba[o + 2] = c.b; rgba[o + 3] = c.a; }
                return (rgba, texture.width, texture.height);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        static bool LooksLikeJpeg(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

        /// <summary>Unity が取り込んだ絵から読む（<see cref="UnityTextureReader"/>。元のテクスチャ・インポート設定は変えない）。</summary>
        static void ReadImported(Texture2D texture, Loaded result)
        {
            try
            {
                var read = UnityTextureReader.Read(texture);
                result.Pixels = read.Content.CopyPixels();
                result.Width = read.Content.Width; result.Height = read.Content.Height;
                result.Read = read.ThroughGpu ? LiveLinkOriginalRead.Gpu : LiveLinkOriginalRead.Imported;
                result.Compressed = GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat);
                result.Srgb = read.ColorSpace != ResourceColorSpace.Linear;
            }
            catch (ResourceRefusedException e)
            {
                result.State = e.Refusal == ResourceRefusal.TooLarge ? LiveLinkOriginalState.TooLarge : LiveLinkOriginalState.Unreadable;
                result.Reason = e.Message;
            }
        }

        /// <summary>アセットでない絵（実行時に作った絵・RenderTexture など）を、GPU に描いて読み戻す。</summary>
        static void ReadGpu(Texture texture, Loaded result)
        {
            bool srgb = texture is RenderTexture rt ? rt.sRGB : GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
            var pixels = LiveLinkMaterialValues.ReadPixels(texture, texture.width, texture.height, srgb);
            if (pixels == null) { result.State = LiveLinkOriginalState.Unreadable; result.Reason = L.Tr("The texture could not be drawn and read back here."); return; }
            result.Pixels = pixels; result.Read = LiveLinkOriginalRead.Gpu; result.Srgb = srgb;
            result.Compressed = !(texture is RenderTexture) && GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat);
        }

        /// <summary>送った結果（ログ用）。</summary>
        internal struct Report { public int Images, Declined, Cached; public long Bytes; public string Problem; }

        /// <summary>
        /// 送る絵の列（モデルを送るたびに作り直す）。<see cref="Pump"/> を更新ごとに呼ぶと、時間の許す限り 1 枚ずつ読んで送る。
        /// モデルの世代が替わったら捨てる（世代はブリッジが送ったモデルのものを使うので、古い列は届いても断られる）。
        /// 同じテクスチャを使う枠は隣に並べ、最初の 1 枠で読んだ画素を残りの枠で使い回す（読むのは 1 回。送る量と予算は枠ごと）。
        /// </summary>
        internal sealed class Sender
        {
            readonly LiveLinkModel model; readonly Queue<Job> jobs;
            long remaining;
            int cachedId; Loaded cached;
            public int Generation { get; }
            public bool Done => jobs.Count == 0;
            public int Images { get; private set; }
            public int Declined { get; private set; }
            /// <summary>画素を送らず「印が同じ」と答えた数（頼みの have が今の印と同じ）。</summary>
            public int Cached { get; private set; }
            public long Bytes { get; private set; }
            /// <summary>テクスチャを読んだ回数（同じテクスチャの枠は 1 回にまとまる。試験・診断用）。</summary>
            public int Reads { get; private set; }

            /// <param name="budget">この送りで送る絵の画素のバイト数の合計の上限（既定は <see cref="Budget"/>。試験が小さくする）。</param>
            public Sender(LiveLinkModel model, List<Job> plan, long budget = Budget)
            {
                this.model = model; Generation = model.Generation; remaining = budget;
                jobs = new Queue<Job>(GroupByTexture(plan));
            }

            /// <summary>頼まれた枠を足す（同じ枠がもう列にあれば、頼みの印を新しい方にする。送りが済んだ枠は、また足す）。</summary>
            public void Enqueue(IEnumerable<Job> more)
            {
                foreach (var job in more)
                {
                    var queued = jobs.FirstOrDefault(j => j.Material == job.Material && j.Property == job.Property);
                    if (queued != null) { queued.Have = job.Have; continue; }
                    jobs.Enqueue(job);
                }
            }

            /// <summary>同じテクスチャの枠を、最初に出てきた位置にまとめる（順番は安定。共有が無ければそのまま）。</summary>
            static List<Job> GroupByTexture(List<Job> plan)
            {
                var first = new Dictionary<int, int>();
                foreach (var j in plan) if (!first.ContainsKey(j.TextureId)) first[j.TextureId] = first.Count;
                return plan.Select((j, i) => (j, i)).OrderBy(x => first[x.j.TextureId]).ThenBy(x => x.i).Select(x => x.j).ToList();
            }

            /// <summary>時間の許す限り（<paramref name="budgetMs"/> まで。最初の 1 枚は必ず）読んで送る。ブリッジが失敗したら理由を返す
            /// （<paramref name="pendingLimit"/> は積んだ命令の上限。既定は <see cref="PendingLimit"/>）。</summary>
            public Report Pump(ulong handle, double budgetMs = PumpMs, long pendingLimit = PendingLimit)
            {
                var report = new Report();
                var clock = Stopwatch.StartNew();
                while (jobs.Count > 0)
                {
                    if (report.Images + report.Declined > 0 && clock.Elapsed.TotalMilliseconds > budgetMs) break;
                    if (LiveLinkBridge.PendingBytes(handle) >= pendingLimit) break;
                    var job = jobs.Peek();
                    var entry = job.Material < model.Materials.Count ? model.Materials[job.Material] : null;
                    Texture texture = entry != null && entry.Material != null && entry.Material.HasProperty(job.Property) ? entry.Material.GetTexture(job.Property) : null;
                    // 印は読む前に取り、頼みの have と同じなら、絵を読まずに「印が同じ」と答える（予算も使わない）
                    ulong stamp = job.Have != 0 && texture != null ? StampOf(texture) : 0;
                    if (job.Have != 0 && stamp != 0 && stamp == job.Have)
                    {
                        int hit = LiveLinkBridge.OriginalSend(handle, job.Material, job.Property, LiveLinkOriginalState.Cached, LiveLinkOriginalRead.File, false, texture.width, texture.height, true, null, stamp);
                        if (hit < 0) { report.Problem = L.Tr("The Live Link library refused the original texture ({0}).", entry != null ? entry.Name : job.Property); Finish(); return report; }
                        if (hit == 0) { Finish(); return report; }
                        jobs.Dequeue(); report.Cached++; Cached++;
                        continue;
                    }
                    Loaded loaded;
                    long estimate = (long)Math.Max(1, job.Width) * Math.Max(1, job.Height) * 4;
                    if (texture != null && estimate > remaining) loaded = new Loaded { State = LiveLinkOriginalState.OverBudget, Width = texture.width, Height = texture.height };
                    else
                    {
                        loaded = LoadShared(texture);
                        if (loaded.State == LiveLinkOriginalState.Image && loaded.Pixels.LongLength > remaining) { loaded = new Loaded { State = LiveLinkOriginalState.OverBudget, Width = loaded.Width, Height = loaded.Height }; }
                    }
                    int sent = LiveLinkBridge.OriginalSend(handle, job.Material, job.Property, loaded.State, loaded.Read, loaded.Compressed, loaded.Width, loaded.Height, loaded.Srgb, loaded.State == LiveLinkOriginalState.Image ? loaded.Pixels : null, loaded.State == LiveLinkOriginalState.Image ? loaded.Stamp : 0);
                    if (sent < 0) { report.Problem = L.Tr("The Live Link library refused the original texture ({0}).", entry != null ? entry.Name : job.Property); Finish(); return report; }
                    if (sent == 0) { Finish(); return report; } // スタンドアロンの印が無くなった（つながり直し）
                    jobs.Dequeue();
                    if (loaded.State == LiveLinkOriginalState.Image) { remaining -= loaded.Pixels.LongLength; report.Images++; report.Bytes += loaded.Pixels.LongLength; Images++; Bytes += loaded.Pixels.LongLength; }
                    else { report.Declined++; Declined++; if (loaded.Reason != null) report.Problem = loaded.Reason; }
                }
                if (jobs.Count == 0) { cached = null; }
                return report;
            }

            /// <summary>列を空にする（送れない・送らない。読んだ画素も手放す）。</summary>
            void Finish() { jobs.Clear(); cached = null; }

            /// <summary>前の枠と同じテクスチャなら、その画素を使い回す（読み直さない）。</summary>
            Loaded LoadShared(Texture texture)
            {
                if (texture == null) return Load(null);
                int id = texture.GetInstanceID();
                if (cached != null && cachedId == id) return cached;
                Reads++;
                cached = Load(texture); cachedId = id;
                return cached;
            }
        }
    }
}
