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
        internal struct Report { public int Images, Declined; public long Bytes; public string Problem; }

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
            public long Bytes { get; private set; }
            /// <summary>テクスチャを読んだ回数（同じテクスチャの枠は 1 回にまとまる。試験・診断用）。</summary>
            public int Reads { get; private set; }

            /// <param name="budget">この送りで送る絵の画素のバイト数の合計の上限（既定は <see cref="Budget"/>。試験が小さくする）。</param>
            public Sender(LiveLinkModel model, List<Job> plan, long budget = Budget)
            {
                this.model = model; Generation = model.Generation; remaining = budget;
                jobs = new Queue<Job>(GroupByTexture(plan));
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
                    Loaded loaded;
                    long estimate = (long)Math.Max(1, job.Width) * Math.Max(1, job.Height) * 4;
                    if (texture != null && estimate > remaining) loaded = new Loaded { State = LiveLinkOriginalState.OverBudget, Width = texture.width, Height = texture.height };
                    else
                    {
                        loaded = LoadShared(texture);
                        if (loaded.State == LiveLinkOriginalState.Image && loaded.Pixels.LongLength > remaining) { loaded = new Loaded { State = LiveLinkOriginalState.OverBudget, Width = loaded.Width, Height = loaded.Height }; }
                    }
                    int sent = LiveLinkBridge.OriginalSend(handle, job.Material, job.Property, loaded.State, loaded.Read, loaded.Compressed, loaded.Width, loaded.Height, loaded.Srgb, loaded.State == LiveLinkOriginalState.Image ? loaded.Pixels : null);
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
