using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// スタンドアロンから来たテクスチャセットを、シーンのレンダラーに MaterialPropertyBlock で当てて見せる。マテリアル・テクスチャの
    /// アセットには触れない（元の PropertyBlock は覚えておき、外すときに戻す）。
    /// チャンネルごとに RenderTexture（ARGB32、ミップ付き。Color と Emission は sRGB、ほかはリニア）を GPU に持ち、CPU には全体の写しを
    /// 持たない。変わったタイルは、ブリッジが共有メモリから帯（タイルを横に並べた小さな Texture2D）へ写し、帯だけを上げて
    /// <see cref="Graphics.CopyTexture"/> でタイルの所へ置く。全面の更新も同じ帯を数フレームに分けて上げる（1 回の呼び出しで使う時間は
    /// <see cref="BudgetMs"/> まで。帯 1 本は分けられないので、1 本が予算を超える環境では呼び出しごとに 1 本。複数のチャンネルが汚れているときは、
    /// 呼び出しをまたいで順番に回す）。ミップは、予算で打ち切られていない呼び出しで、上げたチャンネルだけ <see cref="RenderTexture.GenerateMips"/> で
    /// GPU が作る（予算に数える。全面の更新の途中では作らず、上げ終えてから 1 回）。CopyTexture が使えない環境では、Texture2D の全体を Apply する（ミップも Apply が作る）。
    /// 流し込み先の詰め方は YoluPainter の 3D ビューと同じ（値・1 − 値・Standard の Metallic と Smoothness は PreviewChannelPack.shader で
    /// 詰め直した RenderTexture。元のチャンネルが上がり終えて落ち着いてから 1 回だけ作り直し、その全サイズの Blit とミップは予算の外）。
    /// </summary>
    internal sealed unsafe class LiveLinkDisplay : IDisposable
    {
        /// <summary>帯に並べるタイルの数の上限（帯の幅はテクスチャの大きさの上限まで）。</summary>
        public const int StripTiles = 32;
        /// <summary>1 回の <see cref="Upload"/> で、帯の上げ（共有メモリからの写し・帯の Apply・CopyTexture）とミップの作り直しに使う主スレッドの時間（ミリ秒。
        /// 既定 8。エディタの 1 フレームの半分ほど）。次の帯（ミップ）がそこまでに収まらないと見たら（今の呼び出しで一番かかったものの時間を足して）残りは
        /// 次の更新へ回す。帯 1 本は分けられないので、最初の 1 本は必ず上げる。0 にすると、1 回の呼び出しで帯 1 本（かミップ 1 チャンネル）だけを行う（試験）。
        /// 流し込み先の詰め直し（<see cref="Bind"/>）は数えない。</summary>
        public static double BudgetMs { get; set; } = 8;

        internal sealed class ChannelTexture
        {
            public PaintChannel Channel; public bool Srgb;
            /// <summary>全体（GPU。ミップ付き）。CopyTexture が使えない環境では null。</summary>
            public RenderTexture Target;
            /// <summary>CopyTexture が使えない環境の全体（CPU の写しがある Texture2D）。</summary>
            public Texture2D Fallback;
            public Texture Texture => Target != null ? (Texture)Target : Fallback;
            /// <summary>タイルを上げた後で、ミップをまだ作り直していない。</summary>
            public bool MipsStale;
            /// <summary>帯（タイルの数 1・2・4・8・16・32 ごと。少ない変化で大きな帯を上げない）。</summary>
            public readonly Dictionary<int, Texture2D> Strips = new Dictionary<int, Texture2D>();
        }

        internal sealed class SetView
        {
            public YlbSetInfo Info; public string Name;
            public readonly Dictionary<PaintChannel, ChannelTexture> Channels = new Dictionary<PaintChannel, ChannelTexture>();
            public readonly Dictionary<string, RenderTexture> Packed = new Dictionary<string, RenderTexture>();
            public bool NeedsBind = true;
            /// <summary>詰め直したテクスチャ（Value・InvertedValue・MetallicSmoothness）があり、元のチャンネルが上がった後でまだ詰め直していない。</summary>
            public bool PackStale, HasPacked;
            public int Uploads;
        }

        /// <summary>1 回の上げの記録（計測と窓の表示）。</summary>
        internal struct UploadRecord
        {
            public uint Set; public PaintChannel Channel; public int Tiles, Torn; public bool Strip;
            /// <summary>共有メモリ → 帯（Texture2D の CPU の写し）、GPU へ上げる（帯の Apply と CopyTexture、または全体の Apply）、書き終えてから上げ終えるまで。</summary>
            public double CopyMs, UploadMs, LatencyMs;
        }

        sealed class Applied { public Renderer Renderer; public int Index; public MaterialPropertyBlock Original, Ours; public bool OriginalEmpty; }

        readonly Dictionary<uint, SetView> sets = new Dictionary<uint, SetView>();
        readonly List<Applied> applied = new List<Applied>();
        readonly uint[] coords = new uint[StripTiles * 2];
        Material pack; bool packChecked; string packProblem;

        public IEnumerable<SetView> Sets => sets.Values.OrderBy(s => s.Info.set);
        public UploadRecord LastUpload { get; private set; }
        /// <summary>最後の <see cref="Upload"/> が予算で打ち切られておらず、作り直しの残ったミップも無い（全面の更新の途中ではない）。詰め直しはこの時だけする。</summary>
        public bool Settled { get; private set; } = true;
        /// <summary>次の <see cref="Upload"/> が回り始めるチャンネルの番号（予算で打ち切った所から続けて、後ろのチャンネルも順番に上がるように）。</summary>
        int cursor;
        /// <summary>これまでに上げた帯の数とタイルの数（<see cref="History"/> は新しい 64 件だけなので、数えるにはこちら）。</summary>
        public long UploadCount { get; private set; }
        public long TilesUploaded { get; private set; }
        public readonly List<UploadRecord> History = new List<UploadRecord>();
        /// <summary>これまでに詰め直した（Blit とミップを作った）テクスチャの数（試験が、全面の更新の途中で毎回は詰め直さないことを数える）。</summary>
        public int PackBuilds { get; private set; }
        /// <summary>見せている PropertyBlock の数（レンダラー × マテリアルの番号）。</summary>
        public int AppliedCount => applied.Count(a => a.Renderer != null);
        /// <summary>CopyTexture の道を使ってよいか（既定は使う。試験が、使えない環境の道を通すために切る）。</summary>
        public static bool AllowCopyTexture { get; set; } = true;

        /// <summary>帯から全体の RenderTexture へ GPU の中で写せるか（Texture2D → RenderTexture の CopyTexture）。</summary>
        public static bool CopySupported => AllowCopyTexture && (SystemInfo.copyTextureSupport & (CopyTextureSupport.Basic | CopyTextureSupport.TextureToRT)) == (CopyTextureSupport.Basic | CopyTextureSupport.TextureToRT) && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
        /// <summary>帯の幅に使えるタイルの数の上限（帯は 1 枚のテクスチャなので、大きなタイルでは上限が下がる）。</summary>
        static int MaxStripTiles(int tileSize)
        {
            int n = 1;
            while (n * 2 <= StripTiles && n * 2 * tileSize <= SystemInfo.maxTextureSize) n *= 2;
            return n;
        }

        /// <summary>ブリッジのセットの並びに合わせる（新しい・作り直したセットのテクスチャを作り、消えたセットを片付ける）。</summary>
        public bool SyncSets(ulong handle)
        {
            bool changed = false;
            var seen = new HashSet<uint>();
            int count = LiveLinkBridge.SetCount(handle);
            for (int i = 0; i < count; i++)
            {
                if (!LiveLinkBridge.SetInfo(handle, i, out var info)) continue;
                seen.Add(info.set);
                if (sets.TryGetValue(info.set, out var view) && view.Info.revision == info.revision) continue;
                if (view != null) Release(view);
                sets[info.set] = Create(info, LiveLinkBridge.SetName(handle, info.set));
                changed = true;
            }
            foreach (var gone in sets.Keys.Where(k => !seen.Contains(k)).ToList()) { Release(sets[gone]); sets.Remove(gone); changed = true; }
            if (changed) RemoveStaleBlocks();
            return changed;
        }

        SetView Create(YlbSetInfo info, string name)
        {
            var view = new SetView { Info = info, Name = name ?? "" };
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
            {
                if ((info.channel_mask & (1u << (int)c)) == 0) continue;
                bool srgb = c == PaintChannel.Color || c == PaintChannel.Emission;
                string label = "YoluPainter Live Link " + view.Name + " " + c;
                var channel = new ChannelTexture { Channel = c, Srgb = srgb };
                if (CopySupported) channel.Target = NewTarget((int)info.width, (int)info.height, srgb, label);
                else
                    channel.Fallback = new Texture2D((int)info.width, (int)info.height, TextureFormat.RGBA32, true, !srgb)
                    { hideFlags = HideFlags.HideAndDontSave, name = label, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear };
                view.Channels[c] = channel;
            }
            return view;
        }

        /// <summary>全体の RenderTexture（ミップ付き。ミップは上げ終えた後に <see cref="RenderTexture.GenerateMips"/> で作る）。</summary>
        static RenderTexture NewTarget(int w, int h, bool srgb, string label)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear)
            { hideFlags = HideFlags.HideAndDontSave, name = label, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, useMipMap = true, autoGenerateMips = false };
            rt.Create();
            return rt;
        }

        /// <summary>GPU の側で失われた（デバイスの再初期化など）全体の RenderTexture を作り直し、全面を写し直すよう頼む。作り直したものがあれば true。
        /// 更新のたびに呼ぶ（失われても、ブリッジからの知らせは来ない）。</summary>
        public bool RebuildLost(ulong handle)
        {
            bool rebuilt = false;
            foreach (var view in sets.Values)
                foreach (var c in view.Channels.Values)
                {
                    if (c.Target == null || c.Target.IsCreated()) continue;
                    c.Target.Create();
                    LiveLinkNative.ylb_channel_mark_all_dirty(handle, view.Info.set, (int)c.Channel);
                    view.NeedsBind = true;
                    rebuilt = true;
                }
            return rebuilt;
        }

        /// <summary>汚れたタイルを帯で上げる（1 回の呼び出しで <see cref="BudgetMs"/> まで。使い切ったら残りは次の更新）。上げた・ミップを作ったチャンネルがあれば true。
        /// remaining は、まだ汚れている（予算で回した・ちぎれた）タイルや、作り直しの残ったミップがあるか。</summary>
        public bool Upload(ulong handle, out bool remaining)
        {
            bool any = false, cut = false; remaining = false;
            var clock = Stopwatch.StartNew();
            var channels = new List<(SetView View, ChannelTexture Channel)>();
            foreach (var view in sets.Values) foreach (var c in view.Channels.Values) channels.Add((view, c));
            if (channels.Count == 0) { Settled = true; return false; }
            int start = cursor % channels.Count;
            double slowest = 0;   // この呼び出しで一番かかった帯の時間（次の帯がこれだけかかると見て、予算に収まらなければ始めない）
            // 全部のチャンネルに 1 本ずつ配りながら回す。打ち切った所を覚え、次の呼び出しはそこから始める（先頭のチャンネルの全面の更新が、後ろを待たせ続けない）
            bool progressed;
            do
            {
                progressed = false;
                for (int n = 0; n < channels.Count; n++)
                {
                    int index = (start + n) % channels.Count;
                    var (view, c) = channels[index];
                    int dirty = LiveLinkNative.ylb_channel_dirty(handle, view.Info.set, (int)c.Channel);
                    if (dirty <= 0) continue;
                    if (any && clock.Elapsed.TotalMilliseconds + slowest > BudgetMs) { remaining = cut = true; cursor = index; goto finish; }
                    var record = UploadChannel(handle, view, c, dirty);
                    slowest = Math.Max(slowest, record.CopyMs + record.UploadMs);
                    if (record.Tiles > 0)
                    {
                        any = progressed = true; view.Uploads++; c.MipsStale = true;
                        if (view.HasPacked) view.PackStale = true;
                        LastUpload = record; History.Add(record); if (History.Count > 64) History.RemoveAt(0);
                        UploadCount++; TilesUploaded += record.Tiles;
                    }
                    // ちぎれた（書いている途中だった）タイルは次の更新でもう一度
                    if (record.Torn > 0 || record.Tiles == 0) remaining = true;
                }
            } while (progressed);
            finish:
            if (!remaining)
                foreach (var (view, c) in channels)
                    if (LiveLinkNative.ylb_channel_dirty(handle, view.Info.set, (int)c.Channel) > 0) remaining = true;
            Settled = !cut;
            // ミップは、予算で打ち切っていない呼び出しで、上げたチャンネルだけ作り直す（全面の更新の途中で毎回は作らない）。予算に収まらなければ次の更新へ
            bool mips = false; double slowestMip = 0;
            if (!cut)
                foreach (var (_, c) in channels)
                {
                    if (!c.MipsStale || c.Target == null) continue;
                    if (mips && clock.Elapsed.TotalMilliseconds + slowestMip > BudgetMs) { remaining = true; Settled = false; break; }
                    var one = Stopwatch.StartNew();
                    c.Target.GenerateMips(); c.MipsStale = false; mips = true;
                    slowestMip = Math.Max(slowestMip, one.Elapsed.TotalMilliseconds);
                }
            return any || mips;
        }

        UploadRecord UploadChannel(ulong handle, SetView view, ChannelTexture c, int dirty)
        {
            var record = new UploadRecord { Set = view.Info.set, Channel = c.Channel };
            int ts = (int)view.Info.tile_size, w = (int)view.Info.width, h = (int)view.Info.height;
            YlbCopyResult result;
            var clock = Stopwatch.StartNew();
            int n;
            if (c.Target != null)
            {
                // 帯だけを CPU に写し（全体の CPU の写しは持たない）、帯を上げて、タイルの所へ GPU の中で写す
                int most = MaxStripTiles(ts), capacity = 1;
                while (capacity < dirty && capacity < most) capacity *= 2;
                if (!c.Strips.TryGetValue(capacity, out var staging) || staging == null)
                    // 全体の RenderTexture と同じ形式（色空間がガンマなら sRGB でない、リニアなら sRGB）にそろえる。CopyTexture は形式が違うと使えない環境がある
                    c.Strips[capacity] = staging = new Texture2D(capacity * ts, ts, c.Target.graphicsFormat, TextureCreationFlags.None) { hideFlags = HideFlags.HideAndDontSave, name = "YoluPainter Live Link strip " + capacity };
                var stripRaw = staging.GetRawTextureData<byte>();
                byte* stripPtr = (byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(stripRaw);
                fixed (uint* pc = coords)
                    n = LiveLinkNative.ylb_copy_dirty(handle, view.Info.set, (int)c.Channel, null, 0, stripPtr, (ulong)stripRaw.Length, capacity, pc, &result);
                record.CopyMs = clock.Elapsed.TotalMilliseconds; clock.Restart();
                if (n > 0)
                {
                    staging.Apply(false);
                    for (int i = 0; i < n; i++)
                    {
                        int x = (int)coords[i * 2], y = (int)coords[i * 2 + 1];
                        int tw = Math.Min(ts, w - x * ts), th = Math.Min(ts, h - y * ts);
                        Graphics.CopyTexture(staging, 0, 0, i * ts, 0, tw, th, c.Target, 0, 0, x * ts, y * ts);
                    }
                }
                record.Strip = true;
            }
            else
            {
                // CopyTexture が無い環境: CPU の全体の写しへ写して、全体を上げる（ミップも Apply が作る）
                var raw = c.Fallback.GetRawTextureData<byte>();
                byte* image = (byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(raw);
                // 生の並びはミップも含む（先頭がミップ 0）ので、ミップ 0 の大きさだけを渡す
                n = LiveLinkNative.ylb_copy_dirty(handle, view.Info.set, (int)c.Channel, image, (ulong)w * (ulong)h * 4, null, 0, 0, null, &result);
                record.CopyMs = clock.Elapsed.TotalMilliseconds; clock.Restart();
                if (n > 0) c.Fallback.Apply(true);
            }
            record.UploadMs = clock.Elapsed.TotalMilliseconds;
            record.Tiles = Math.Max(0, n); record.Torn = (int)result.torn;
            if (n > 0 && result.stamp_us > 0) record.LatencyMs = (LiveLinkNative.ylb_now_us() - result.stamp_us) / 1000.0;
            return record;
        }

        /// <summary>モデルのレンダラーに、テクスチャセットのテクスチャを PropertyBlock で当てる（変わったセットだけ）。</summary>
        public void Bind(LiveLinkModel model, bool all = false)
        {
            if (model == null) return;
            foreach (var view in sets.Values)
            {
                // 詰め直したテクスチャは、元のチャンネルが上がり終えて落ち着いてから 1 回だけ作り直す（全面の更新の途中で、更新のたびに全サイズの Blit とミップをしない）
                if (view.PackStale && Settled) view.NeedsBind = true;
                if (!view.NeedsBind && !all) continue;
                view.NeedsBind = false;
                if (view.Info.generation != (uint)model.Generation || view.Info.material >= model.Materials.Count) { view.PackStale = false; continue; }
                var entry = model.Materials[(int)view.Info.material];
                if (entry.Material == null || entry.Shown.Count == 0) { view.PackStale = false; continue; }
                var textures = new Dictionary<string, Texture>();
                foreach (var group in entry.Shown.GroupBy(c => c.Property))
                {
                    var t = Pack(view, entry, group.Key, group.ToList());
                    if (t != null) textures[group.Key] = t;
                }
                view.HasPacked = view.Packed.Count > 0;
                // 上がり終えていない間に作った分は、落ち着いてからもう一度作り直す
                view.PackStale = view.HasPacked && !Settled;
                if (textures.Count == 0) continue;
                foreach (var mesh in model.Meshes)
                {
                    var r = mesh.Renderer;
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != entry.Material) continue;
                        var a = applied.FirstOrDefault(x => x.Renderer == r && x.Index == i);
                        if (a == null)
                        {
                            a = new Applied { Renderer = r, Index = i, Original = new MaterialPropertyBlock(), Ours = new MaterialPropertyBlock() };
                            r.GetPropertyBlock(a.Original, i);
                            r.GetPropertyBlock(a.Ours, i);
                            a.OriginalEmpty = a.Original.isEmpty;
                            applied.Add(a);
                        }
                        foreach (var pair in textures) a.Ours.SetTexture(pair.Key, pair.Value);
                        r.SetPropertyBlock(a.Ours, i);
                    }
                }
            }
        }

        /// <summary>1 つのプロパティに入れるテクスチャ（無ければ null）。</summary>
        Texture Pack(SetView view, LiveLinkModel.MaterialEntry entry, string property, List<PreviewChannelBinding> group)
        {
            var first = group[0];
            Texture Channel(PaintChannel c) => view.Channels.TryGetValue(c, out var t) ? t.Texture : null;
            switch (first.Packing)
            {
                case PreviewPacking.Color:
                case PreviewPacking.Normal:
                    return Channel(first.Channel);
                case PreviewPacking.MetallicSmoothness:
                {
                    var metal = group.Any(c => c.Channel == PaintChannel.Metallic) ? Channel(PaintChannel.Metallic) : null;
                    var rough = group.Any(c => c.Channel == PaintChannel.Roughness) ? Channel(PaintChannel.Roughness) : null;
                    if ((metal == null && rough == null) || PackProblem() != null) return null;
                    var original = entry.Material.GetTexture(property);
                    var target = Target(view, property, Math.Max(metal != null ? metal.width : 1, rough != null ? rough.width : 1), Math.Max(metal != null ? metal.height : 1, rough != null ? rough.height : 1));
                    pack.SetTexture("_SecondTex", rough != null ? rough : (Texture)Texture2D.blackTexture);
                    pack.SetTexture("_OrigTex", original != null ? original : Texture2D.whiteTexture);
                    pack.SetVector("_Flags", new Vector4(metal != null ? 1 : 0, rough != null ? 1 : 0, original != null ? 1 : 0, 0));
                    pack.SetVector("_Consts", new Vector4(entry.Material.HasProperty("_Metallic") ? entry.Material.GetFloat("_Metallic") : 0, entry.Material.HasProperty("_Glossiness") ? entry.Material.GetFloat("_Glossiness") : .5f, 0, 0));
                    try { Blit(metal != null ? metal : Texture2D.blackTexture, target, 1, rough); }
                    finally { pack.SetTexture("_SecondTex", null); pack.SetTexture("_OrigTex", null); }
                    target.GenerateMips(); PackBuilds++;
                    return target;
                }
                default:
                {
                    var value = Channel(first.Channel);
                    if (value == null || PackProblem() != null) return null;
                    var target = Target(view, property, value.width, value.height);
                    pack.SetVector("_Params", new Vector4(first.Packing == PreviewPacking.InvertedValue ? 1 : 0, 0, 0, 0));
                    Blit(value, target, 0, null);
                    target.GenerateMips(); PackBuilds++;
                    return target;
                }
            }
        }

        RenderTexture Target(SetView view, string property, int w, int h)
        {
            if (view.Packed.TryGetValue(property, out var rt) && rt != null && rt.width == w && rt.height == h) return rt;
            if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
            rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat, useMipMap = true, autoGenerateMips = false, name = "YoluPainter Live Link " + property };
            rt.Create();
            view.Packed[property] = rt;
            return rt;
        }

        void Blit(Texture source, RenderTexture target, int pass, Texture second)
        {
            var active = RenderTexture.active;
            var filter = source.filterMode; var secondFilter = second != null ? second.filterMode : FilterMode.Point;
            try
            {
                // 同じ大きさなら画素の中心を点で読む（詰め直しで隣と混ぜない）
                if (source.width == target.width && source.height == target.height) source.filterMode = FilterMode.Point;
                if (second != null && second.width == target.width && second.height == target.height) second.filterMode = FilterMode.Point;
                Graphics.Blit(source, target, pack, pass);
            }
            finally { source.filterMode = filter; if (second != null) second.filterMode = secondFilter; RenderTexture.active = active; }
        }

        /// <summary>詰め直すシェーダーが使えない理由（使えれば null）。</summary>
        public string PackProblem()
        {
            if (packChecked) return packProblem;
            packChecked = true;
            var shader = Shader.Find(PreviewMaterialView.PackShaderName);
            packProblem = ShaderHealth.IsUsable(shader) ? null : L.Tr("The channel packing shader ({0}) cannot be used here, so only Color, Emission and Normal are shown.", PreviewMaterialView.PackShaderName);
            if (packProblem == null) pack = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return packProblem;
        }

        /// <summary>今のセットの無いマテリアルに当てた PropertyBlock を外す。</summary>
        void RemoveStaleBlocks()
        {
            // セットの作り直し・消えたときは、全部を外してから当て直す（古いテクスチャを指したままにしない）
            RemoveBlocks();
            foreach (var v in sets.Values) v.NeedsBind = true;
        }

        /// <summary>当てた PropertyBlock を全部外し、元に戻す。</summary>
        public void RemoveBlocks()
        {
            foreach (var a in applied)
                if (a.Renderer != null) a.Renderer.SetPropertyBlock(a.OriginalEmpty ? null : a.Original, a.Index);
            applied.Clear();
        }

        void Release(SetView view)
        {
            foreach (var c in view.Channels.Values)
            {
                if (c.Target != null) { if (RenderTexture.active == c.Target) RenderTexture.active = null; c.Target.Release(); Object.DestroyImmediate(c.Target); }
                if (c.Fallback != null) Object.DestroyImmediate(c.Fallback);
                foreach (var t in c.Strips.Values) if (t != null) Object.DestroyImmediate(t);
                c.Strips.Clear();
            }
            view.Channels.Clear();
            foreach (var rt in view.Packed.Values) if (rt != null) { if (RenderTexture.active == rt) RenderTexture.active = null; rt.Release(); Object.DestroyImmediate(rt); }
            view.Packed.Clear();
        }

        /// <summary>当てた PropertyBlock を外してから、全部のセットを当て直す（マテリアルの情報が変わって、見せるチャンネルや流し込み先が変わったとき）。</summary>
        public void Rebind(LiveLinkModel model)
        {
            RemoveBlocks();
            foreach (var v in sets.Values) v.NeedsBind = true;
            Bind(model, true);
        }

        /// <summary>見せているものを全部外して片付ける（PropertyBlock を元に戻し、テクスチャを捨てる）。</summary>
        public void Clear()
        {
            RemoveBlocks();
            foreach (var v in sets.Values) Release(v);
            sets.Clear();
        }

        public void Dispose()
        {
            Clear();
            if (pack != null) Object.DestroyImmediate(pack);
            pack = null; packChecked = false;
        }
    }
}
