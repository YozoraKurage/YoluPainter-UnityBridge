using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>Bounded layer residency: one upload tile and two ping-pong working tiles.
    /// Prototype recomposites occupied tiles on revision changes; incremental dependency scheduling is pending.</summary>
    internal sealed class TileGpuCompositor : IDisposable
    {
        Material material;
        Texture2D upload, cpuFallback;
        RenderTexture ping, pong, composite;
        readonly HashSet<TileCoord> previous = new HashSet<TileCoord>();
        public Texture Texture => composite != null ? (Texture)composite : cpuFallback;
        public string Backend { get; private set; } = "Not initialized";
        int width, height, tileSize;
        byte[] uploadPixels;
        public void Update(PaintDocument doc, PaintChannel channel)
        {
            Ensure(doc);
            try { UpdateTiles(doc,channel); }
            catch (Exception ex)
            {
                Dispose(); width=doc.Width;height=doc.Height;tileSize=doc.TileSize;
                cpuFallback=new Texture2D(width,height,TextureFormat.RGBA32,false,true){hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear};
                Backend="CPU composite fallback after GPU failure: "+ex.Message;
                cpuFallback.LoadRawTextureData(doc.Composite(channel));cpuFallback.Apply(false,false);
            }
        }
        void UpdateTiles(PaintDocument doc,PaintChannel channel)
        {
            if (material == null || composite == null)
            {
                cpuFallback.LoadRawTextureData(doc.Composite(channel)); cpuFallback.Apply(false, false); return;
            }
            var tiles = new HashSet<TileCoord>(previous);
            foreach (var layer in doc.Layers)
                if (layer.TryGetChannel(channel, out var present)) foreach (var coord in present.EnumerateTileCoordinates()) tiles.Add(coord);
            foreach (var coord in tiles)
            {
                Clear(ping);
                foreach (var layer in doc.Layers)
                {
                    if (!layer.Visible || layer.Opacity <= 0 || !layer.IsChannelEnabled(channel) || !layer.TryGetChannel(channel, out var surface)) continue;
                    var pixels = uploadPixels;
                    Array.Clear(pixels,0,pixels.Length);
                    
                    for (int y = 0; y < tileSize && coord.Y * tileSize + y < height; y++)
                        for (int x = 0; x < tileSize && coord.X * tileSize + x < width; x++)
                        {
                            var p = surface.GetPixel(coord.X * tileSize + x, coord.Y * tileSize + y);
                            int n = (y * tileSize + x) * 4; pixels[n] = p.R; pixels[n+1] = p.G; pixels[n+2] = p.B; pixels[n+3] = p.A;
                        }
                    upload.LoadRawTextureData(pixels); upload.Apply(false, false);
                    material.SetTexture("_LayerTex", upload); material.SetFloat("_Opacity", (float)layer.Opacity); material.SetInt("_BlendMode", (int)layer.BlendMode);
                    Graphics.Blit(ping, pong, material, 0);
                    var swap = ping; ping = pong; pong = swap;
                }
                int tw = Math.Min(tileSize, width - coord.X * tileSize), th = Math.Min(tileSize, height - coord.Y * tileSize);
                Graphics.CopyTexture(ping, 0, 0, 0, 0, tw, th, composite, 0, 0, coord.X * tileSize, coord.Y * tileSize);
            }
            previous.Clear();
            foreach (var layer in doc.Layers) if (layer.TryGetChannel(channel, out var present)) foreach (var coord in present.EnumerateTileCoordinates()) previous.Add(coord);
        }
        void Ensure(PaintDocument doc)
        {
            if (width == doc.Width && height == doc.Height && tileSize == doc.TileSize && Texture != null) return;
            Dispose(); width = doc.Width; height = doc.Height; tileSize = doc.TileSize;
            var shader = Shader.Find("Hidden/YoluPainter/TileComposite");
            bool supported = shader != null && shader.isSupported && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32) && (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0;
            if (supported)
            {
                try
                {
                    uploadPixels = new byte[tileSize*tileSize*4];
                    material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    upload = new Texture2D(tileSize, tileSize, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
                    ping = MakeRt(tileSize, tileSize); pong = MakeRt(tileSize, tileSize); composite = MakeRt(width, height); Clear(composite);
                    Backend = "CPU source brush / GPU tiled compositor (encoded-space prototype)"; return;
                }
                catch (Exception ex) { Dispose(); Backend = "GPU allocation failed: " + ex.Message + "; CPU composite fallback"; }
            }
            else Backend = "CPU composite fallback: GPU format, copy or shader unavailable";
            width = doc.Width; height = doc.Height; tileSize = doc.TileSize;
            cpuFallback = new Texture2D(width, height, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        }
        static RenderTexture MakeRt(int w, int h)
        {
            var rt = new RenderTexture(w,h,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false };
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
            uploadPixels=null; material = null; upload = null; cpuFallback = null; ping = pong = composite = null; previous.Clear();
        }
    }
}
