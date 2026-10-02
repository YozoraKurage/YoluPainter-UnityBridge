using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>Bounded layer residency: one upload tile and two ping-pong working tiles.
    /// Recomposites only tiles the document reports as changed (PaintDocument.TryGetChangedTiles); structural changes
    /// (layer order, visibility, opacity, blend, enabled channels) fall back to recompositing every occupied tile.
    /// Effects with halos and graph-driven dependency scheduling are not handled here yet.</summary>
    internal sealed class TileGpuCompositor : IDisposable
    {
        Material material;
        Texture2D upload, cpuFallback;
        RenderTexture ping, pong, composite;
        byte[] cpuPixels;
        readonly HashSet<TileCoord> previous = new HashSet<TileCoord>();
        public Texture Texture => composite != null ? (Texture)composite : cpuFallback;
        public string Backend { get; private set; } = "Not initialized";
        /// <summary>Tiles recomposited by the last Update, for diagnostics and tests.</summary>
        public int LastUpdatedTileCount { get; private set; }
        int width, height, tileSize;
        byte[] uploadPixels;
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
                if (layer.TryGetChannel(channel, out var present)) foreach (var coord in present.EnumerateTileCoordinates()) occupied.Add(coord);
            if (!incremental) { dirty.Clear(); dirty.UnionWith(previous); dirty.UnionWith(occupied); }
            LastUpdatedTileCount = dirty.Count;

            if (material == null || composite == null) UpdateCpu(doc, channel, dirty, incremental);
            else foreach (var coord in dirty) CompositeTileOnGpu(doc, channel, coord);

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
        void CompositeTileOnGpu(PaintDocument doc, PaintChannel channel, TileCoord coord)
        {
            Clear(ping);
            foreach (var layer in doc.Layers)
            {
                if (!layer.Visible || layer.Opacity <= 0 || !layer.IsChannelEnabled(channel) || !layer.TryGetChannel(channel, out var surface)) continue;
                // A layer without this tile contributes transparent pixels; skipping it is exact and saves a pass.
                if (!surface.CopyTile(coord, uploadPixels)) continue;
                upload.LoadRawTextureData(uploadPixels); upload.Apply(false, false);
                material.SetTexture("_LayerTex", upload); material.SetFloat("_Opacity", (float)layer.Opacity); material.SetInt("_BlendMode", (int)layer.BlendMode);
                Graphics.Blit(ping, pong, material, 0);
                var swap = ping; ping = pong; pong = swap;
            }
            int tw = Math.Min(tileSize, width - coord.X * tileSize), th = Math.Min(tileSize, height - coord.Y * tileSize);
            if (useCopyTexture) Graphics.CopyTexture(ping, 0, 0, 0, 0, tw, th, composite, 0, 0, coord.X * tileSize, coord.Y * tileSize);
            else DrawCopy(ping, coord.X * tileSize, coord.Y * tileSize, tw, th);
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
                    uploadPixels = new byte[tileSize*tileSize*4];
                    material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    upload = new Texture2D(tileSize, tileSize, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
                    ping = MakeRt(tileSize, tileSize, FilterMode.Point); pong = MakeRt(tileSize, tileSize, FilterMode.Point); composite = MakeRt(width, height, FilterMode.Bilinear); Clear(composite);
                    Backend = "CPU source brush / GPU tiled compositor (encoded-space prototype" + (useCopyTexture ? ")" : ", draw copy)"); return;
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
        public void Dispose()
        {
            foreach (var rt in new[]{ping,pong,composite}) if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            if (upload != null) UnityEngine.Object.DestroyImmediate(upload);
            if (cpuFallback != null) UnityEngine.Object.DestroyImmediate(cpuFallback);
            uploadPixels=null; cpuPixels=null; material = null; upload = null; cpuFallback = null; ping = pong = composite = null; previous.Clear();
            lastDocument = null; lastSerial = -1;
        }
    }
}
