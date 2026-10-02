using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>Bounded layer residency: one upload tile and, per nesting level, a ping-pong pair of working tiles (plus a
    /// pair for clipping groups). Recomposites only tiles the document reports as changed (PaintDocument.TryGetChangedTiles);
    /// structural changes (layer order, grouping, visibility, opacity, blend, enabled channels) fall back to recompositing every
    /// occupied tile. Effects with halos and graph-driven dependency scheduling are not handled here yet.</summary>
    /// <remarks>グループは CpuCompositor.EvaluateTile と同じ順・同じ式で GPU 上で合成する。分離グループ（とクリッピングのある
    /// グループ、クリッピングされたグループ）は 1 つ深い段の作業タイルで透明から中身を合成し、レイヤーと同じように重ねる。
    /// 通過グループは下の結果を 1 つ深い段へ写して中身を重ね、不透明度×マスクでフェードする（不透明度 1 でマスクが無ければ
    /// フェードは中身そのものなので、同じ段でそのまま重ねる）。段ごとの作業タイルは必要になったときに作り、
    /// <see cref="MaxNestedLevels"/> 段とメモリ予算 <see cref="NestedLevelBudgetBytes"/> を超える文書では、深いグループが
    /// 触れるタイルだけ CPU の正本の式で合成する（Backend に書く）。</remarks>
    internal sealed class TileGpuCompositor : IDisposable
    {
        /// <summary>最上段の下に作ってよい段の数（グループの入れ子の深さ。不透明度 1・マスク無しの通過グループは段を使わない）。</summary>
        internal const int MaxNestedLevels = 8;
        /// <summary>入れ子の段の作業タイル（1 段 4 枚、tileSize²×4 バイト）の合計の上限。大きなタイルでは段数がこれで減る。</summary>
        internal const long NestedLevelBudgetBytes = 32L << 20;

        /// <summary>1 段の作業タイル。A/B は段の合成結果のピンポン、ClipA/ClipB はその段のクリッピングのまとまり用。</summary>
        sealed class Level { public RenderTexture A, B, ClipA, ClipB; }

        Material material;
        Texture2D upload, uploadMask, cpuFallback;
        RenderTexture composite;
        readonly List<Level> levels = new List<Level>();
        byte[] cpuPixels;
        readonly HashSet<TileCoord> previous = new HashSet<TileCoord>();
        public Texture Texture => composite != null ? (Texture)composite : cpuFallback;
        public string Backend { get; private set; } = "Not initialized";
        string gpuBackend;
        /// <summary>Tiles recomposited by the last Update, for diagnostics and tests.</summary>
        public int LastUpdatedTileCount { get; private set; }
        /// <summary>The last Update composited this many tiles on the CPU on the GPU path (groups nested deeper than the
        /// level limit, or <see cref="CompositeGroupsOnCpu"/>). Diagnostics/tests.</summary>
        public int LastCpuTileCount { get; private set; }
        /// <summary>入れ子の段として今確保している作業タイルの枚数（診断・テスト用）。</summary>
        public int NestedRenderTextureCount { get; private set; }
        /// <summary>この文書・タイルの大きさで使える入れ子の段の数（<see cref="MaxNestedLevels"/> と予算の小さい方）。</summary>
        public int NestedLevelLimit => tileSize <= 0 ? MaxNestedLevels : (int)Math.Min(MaxNestedLevels, NestedLevelBudgetBytes / (4L * tileSize * tileSize * 4));
        /// <summary>計測・比較用: グループの中身があるタイルを、以前の方式（CPU の正本で合成して載せる）で処理する。</summary>
        internal bool CompositeGroupsOnCpu { get; set; }
        int width, height, tileSize;
        byte[] uploadPixels, maskPixels;
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
        void UpdateTiles(PaintDocument doc,PaintChannel channel)
        {
            var dirty = new HashSet<TileCoord>();
            bool incremental = ReferenceEquals(doc, lastDocument) && channel == lastChannel && doc.TryGetChangedTiles(channel, lastSerial, dirty);
            var occupied = new HashSet<TileCoord>();
            foreach (var layer in doc.Layers)
                foreach (var coord in layer.EnumerateContentTiles(channel)) occupied.Add(coord);
            if (!incremental) { dirty.Clear(); dirty.UnionWith(previous); dirty.UnionWith(occupied); }
            LastUpdatedTileCount = dirty.Count; LastCpuTileCount = 0;

            if (material == null || composite == null) UpdateCpu(doc, channel, dirty, incremental);
            else
            {
                var plan = CpuCompositor.Plan(doc, channel);
                int needed = LevelsNeeded(plan);
                bool tooDeep = needed > NestedLevelLimit;
                Backend = gpuBackend + (tooDeep ? "; groups nested " + needed + " levels deep exceed the GPU limit of " + NestedLevelLimit + ", so the tiles they touch composite on the CPU" : "");
                // Graphics.Blit は書き込み先を RenderTexture.active に残すので、呼び出し側の状態を戻す
                var active = RenderTexture.active;
                try
                {
                    foreach (var coord in dirty)
                    {
                        if ((tooDeep || CompositeGroupsOnCpu) && GroupTouches(plan, channel, coord)) CompositeTileOnCpu(doc, channel, coord);
                        else CompositeTileOnGpu(plan, channel, coord);
                    }
                }
                finally { RenderTexture.active = active; }
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

        /// <summary>不透明度 1 でマスクが効いていない通過グループは、フェードが中身そのもの（CpuCompositor.Fade の amount ≥ 1）なので
        /// 同じ段で重ねられる。</summary>
        static bool PassesThroughWhole(CpuCompositor.StackEntry e)
        { return e.PassesThrough && e.Base.Opacity >= 1 && (e.Base.Mask == null || e.Base.Mask.IsNeutral); }
        /// <summary>この計画の合成に要る入れ子の段の数（CompositeLevel と同じ規則で数える）。</summary>
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
                if (e.Base.IsGroup && Touches(e, channel, coord)) return true;
                foreach (var c in e.ClipEntries) if (c.Base.IsGroup && Touches(c, channel, coord)) return true;
            }
            return false;
        }
        /// <summary>このタイルに効くものがあるか。グループは中身（子）だけを見る: 中身が何も無ければ、分離グループの結果は透明で
        /// （その上にクリッピングされたものも透明に収まる）、通過グループは下をそのまま返すので、飛ばしても結果は同じ。</summary>
        static bool Touches(CpuCompositor.StackEntry e, PaintChannel channel, TileCoord coord)
        {
            var layer = e.Base;
            if (layer.IsGroup) { foreach (var child in e.Children) if (Touches(child, channel, coord)) return true; return false; }
            if (layer.Kind != LayerKind.Raster) return true; // Fill と調整はキャンバス全面
            return layer.TryGetChannel(channel, out var surface) && surface.HasTile(coord);
        }

        void CompositeTileOnGpu(IReadOnlyList<CpuCompositor.StackEntry> plan, PaintChannel channel, TileCoord coord)
        {
            var top = LevelAt(0);
            Clear(top.A);
            var result = CompositeLevel(plan, top, top.A, 0, channel, coord);
            int tw = Math.Min(tileSize, width - coord.X * tileSize), th = Math.Min(tileSize, height - coord.Y * tileSize);
            if (useCopyTexture) Graphics.CopyTexture(result, 0, 0, 0, 0, tw, th, composite, 0, 0, coord.X * tileSize, coord.Y * tileSize);
            else DrawCopy(result, coord.X * tileSize, coord.Y * tileSize, tw, th);
        }

        /// <summary>計画の 1 段を、current（level の A か B。下の結果が入っている）の上に重ね、結果の入った作業タイル（level の A か B）を
        /// 返す。CpuCompositor.EvaluateTile と同じ順と式。マテリアルの値は、入れ子の合成が書き換えるので、各 Blit の直前に設定する。</summary>
        RenderTexture CompositeLevel(IReadOnlyList<CpuCompositor.StackEntry> plan, Level level, RenderTexture current, int depth, PaintChannel channel, TileCoord coord)
        {
            foreach (var entry in plan)
            {
                var layer = entry.Base;
                if (layer.Kind == LayerKind.Adjustment)
                {
                    // 自分の画素は無く、下の合成結果に調整をかける。透明な画素はシェーダーがそのまま返す。
                    SetAdjustment(layer.Adjustment); SetLayer(layer.Opacity, layer.BlendMode, layer.Mask, coord);
                    current = Step(current, level, 2, null);
                    continue;
                }
                if (layer.IsGroup && !Touches(entry, channel, coord)) continue;
                if (entry.PassesThrough)
                {
                    if (PassesThroughWhole(entry)) { current = CompositeLevel(entry.Children, level, current, depth, channel, coord); continue; }
                    // 下の結果を 1 つ深い段へ写し、そこへ中身を重ね、下と中身を不透明度×マスクでフェードする
                    var inner = LevelAt(depth + 1);
                    Blit(current, inner.A, 1, null);
                    var innerResult = CompositeLevel(entry.Children, inner, inner.A, depth + 1, channel, coord);
                    SetLayer(layer.Opacity, LayerBlendMode.Normal, layer.Mask, coord);
                    current = Step(current, level, 4, innerResult);
                    continue;
                }
                Texture source;
                if (layer.IsGroup) source = Isolated(entry.Children, depth + 1, channel, coord);
                else
                {
                    // A layer without this tile contributes transparent pixels (and so does its clipping group).
                    if (!layer.CopyTile(channel, coord, uploadPixels)) continue;
                    upload.LoadRawTextureData(uploadPixels); upload.Apply(false, false);
                    source = upload;
                }
                if (entry.ClipEntries.Count > 0) source = BuildClippingGroup(entry, source, level, depth, channel, coord);
                SetLayer(layer.Opacity, ModeOf(layer), layer.Mask, coord);
                current = Step(current, level, 0, source);
            }
            return current;
        }
        /// <summary>分離合成: depth 段の作業タイルで、透明から中身を合成する。結果はその段の A か B（次にその段を使うまで有効）。</summary>
        RenderTexture Isolated(IReadOnlyList<CpuCompositor.StackEntry> children, int depth, PaintChannel channel, TileCoord coord)
        {
            var level = LevelAt(depth);
            Clear(level.A);
            return CompositeLevel(children, level, level.A, depth, channel, coord);
        }
        /// <summary>下地（source: upload か、1 つ深い段のグループの結果）をこの段のまとまり用タイルへ写し、クリッピングされたものを
        /// 順に重ねる。まとまりは下地のアルファを保つ。戻り値はまとまりの入ったタイル（下地の不透明度・マスク・合成モードでこのあと
        /// 下に合成する）。クリッピングされたグループの中身は 1 つ深い段で合成する（下地は写し終えているので、その段を使ってよい）。</summary>
        RenderTexture BuildClippingGroup(CpuCompositor.StackEntry entry, Texture source, Level level, int depth, PaintChannel channel, TileCoord coord)
        {
            EnsureClipPair(level);
            Blit(source, level.ClipB, 1, null); // 下地をそのまま写す
            RenderTexture current = level.ClipB, other = level.ClipA;
            foreach (var clip in entry.ClipEntries)
            {
                var c = clip.Base;
                int pass; Texture layerTex = null;
                if (c.Kind == LayerKind.Adjustment) { SetAdjustment(c.Adjustment); pass = 2; }
                else if (c.IsGroup)
                {
                    if (!Touches(clip, channel, coord)) continue;
                    layerTex = Isolated(clip.Children, depth + 1, channel, coord); pass = 3;
                }
                else
                {
                    if (!c.CopyTile(channel, coord, uploadPixels)) continue;
                    upload.LoadRawTextureData(uploadPixels); upload.Apply(false, false);
                    layerTex = upload; pass = 3;
                }
                SetLayer(c.Opacity, c.Kind == LayerKind.Adjustment ? c.BlendMode : ModeOf(c), c.Mask, coord);
                Blit(current, other, pass, layerTex);
                var swap = current; current = other; other = swap;
            }
            return current;
        }
        static LayerBlendMode ModeOf(PaintLayer layer) { return layer.BlendMode == LayerBlendMode.PassThrough ? LayerBlendMode.Normal : layer.BlendMode; }
        /// <summary>current（level の A か B）を読み、もう片方へ書く。読みと書きが同じタイルになることは無い。</summary>
        RenderTexture Step(RenderTexture current, Level level, int pass, Texture layerTex)
        {
            var next = current == level.A ? level.B : level.A;
            Blit(current, next, pass, layerTex);
            return next;
        }
        /// <summary>_MainTex = source、_LayerTex = layerTex で destination へ描く。source・layerTex と destination は別のタイル。
        /// 描いた後 _LayerTex を upload（書き込み先にならない Texture2D）へ戻し、作業タイルが入力に残らないようにする。</summary>
        void Blit(Texture source, RenderTexture destination, int pass, Texture layerTex)
        {
            if (ReferenceEquals(source, destination) || ReferenceEquals(layerTex, destination))
                throw new InvalidOperationException("A GPU pass must not read and write the same render texture.");
            if (layerTex != null) material.SetTexture("_LayerTex", layerTex);
            Graphics.Blit(source, destination, material, pass);
            material.SetTexture("_LayerTex", upload);
        }
        void CompositeTileOnCpu(PaintDocument doc, PaintChannel channel, TileCoord coord)
        {
            int x = coord.X * tileSize, y = coord.Y * tileSize, tw = Math.Min(tileSize, width - x), th = Math.Min(tileSize, height - y);
            var region = CpuCompositor.CompositeRegion(doc, channel, x, y, tw, th);
            Array.Clear(uploadPixels, 0, uploadPixels.Length);
            for (int row = 0; row < th; row++) Buffer.BlockCopy(region, row * tw * 4, uploadPixels, row * tileSize * 4, tw * 4);
            upload.LoadRawTextureData(uploadPixels); upload.Apply(false, false);
            var top = LevelAt(0);
            Blit(upload, top.A, 1, null);
            if (useCopyTexture) Graphics.CopyTexture(top.A, 0, 0, 0, 0, tw, th, composite, 0, 0, x, y);
            else DrawCopy(top.A, x, y, tw, th);
            LastCpuTileCount++;
        }
        void SetLayer(double opacity, LayerBlendMode mode, RasterMask mask, TileCoord coord)
        {
            material.SetFloat("_Opacity", (float)opacity); material.SetInt("_BlendMode", (int)mode);
            SetMask(mask, coord);
        }
        void SetMask(RasterMask mask, TileCoord coord)
        {
            if (mask != null && !mask.IsNeutral)
            {
                mask.Surface.CopyTile(coord, maskPixels); // absent tile = nothing hidden (zeros)
                uploadMask.LoadRawTextureData(maskPixels); uploadMask.Apply(false, false);
                material.SetTexture("_MaskTex", uploadMask);
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
        /// <summary>CopyTexture の代わりに、作業タイルの左下 w×h を composite の (x, y) へ描き込む。
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
                float u = w / (float)tileSize, v = h / (float)tileSize;
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
                // 残すと、次の Blit がこの作業タイルを書き込み先にしたとき「入力と出力が同じ」と判定する。
                material.SetTexture("_MainTex", null);
            }
        }
        /// <summary>depth 段の結果用ピンポンを（無ければ作って）返す。0 段は Ensure で作る。段の上限は呼び出し側（LevelsNeeded）で守る。</summary>
        Level LevelAt(int depth)
        {
            if (depth > NestedLevelLimit) throw new InvalidOperationException("Group nesting exceeds the GPU level limit (" + NestedLevelLimit + ").");
            while (levels.Count <= depth) levels.Add(new Level());
            var level = levels[depth];
            if (level.A == null)
            {
                level.A = MakeRt(tileSize, tileSize, FilterMode.Point); level.B = MakeRt(tileSize, tileSize, FilterMode.Point);
                if (depth > 0) NestedRenderTextureCount += 2;
            }
            return level;
        }
        void EnsureClipPair(Level level)
        {
            if (level.ClipA != null) return;
            level.ClipA = MakeRt(tileSize, tileSize, FilterMode.Point); level.ClipB = MakeRt(tileSize, tileSize, FilterMode.Point);
            if (level != levels[0]) NestedRenderTextureCount += 2;
        }
        void Ensure(PaintDocument doc)
        {
            if (width == doc.Width && height == doc.Height && tileSize == doc.TileSize && Texture != null) return;
            Dispose(); width = doc.Width; height = doc.Height; tileSize = doc.TileSize;
            var shader = Shader.Find("Hidden/YoluPainter/TileComposite");
            bool supported = allowGpu && ShaderHealth.IsUsable(shader) && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32);
            useCopyTexture = allowCopyTexture && (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0;
            if (supported)
            {
                try
                {
                    uploadPixels = new byte[tileSize*tileSize*4]; maskPixels = new byte[tileSize*tileSize*4];
                    material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    upload = new Texture2D(tileSize, tileSize, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
                    uploadMask = new Texture2D(tileSize, tileSize, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
                    material.SetTexture("_LayerTex", upload);
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
        public void Dispose()
        {
            foreach (var level in levels) { Release(level.A); Release(level.B); Release(level.ClipA); Release(level.ClipB); }
            levels.Clear(); NestedRenderTextureCount = 0;
            Release(composite);
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            if (upload != null) UnityEngine.Object.DestroyImmediate(upload);
            if (uploadMask != null) UnityEngine.Object.DestroyImmediate(uploadMask);
            if (cpuFallback != null) UnityEngine.Object.DestroyImmediate(cpuFallback);
            uploadPixels=null; maskPixels=null; cpuPixels=null; material = null; upload = null; uploadMask = null; cpuFallback = null; composite = null; previous.Clear();
            lastDocument = null; lastSerial = -1;
        }
    }
}
