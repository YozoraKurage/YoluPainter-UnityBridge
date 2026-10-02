using System;
using Yozolab.YoluPainter.Core;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>表示用の Normal の出力（<see cref="NormalMaps.Output"/> と同じ画素）。Normal チャンネルのレイヤーの合成（呼び出し側の
    /// <see cref="TileGpuCompositor"/> の結果）と、Height → Normal が有効なら自前の <see cref="TileGpuCompositor"/> で合成した Height を、
    /// NormalOutput.shader の 1 パスで全面に描く。Height の合成器は GPU の写しを持たない（ResidentBudgetBytes = 0。変更されたブロックだけ
    /// 合成し直すのは同じ）。GPU で描けないとき（シェーダーが使えない、入力が CPU 代替のテクスチャ）は CPU の正本の式で全面を計算して
    /// Texture2D に載せる。VRAM は出力と Height の合成の 2 枚（各 幅×高さ×4 バイト）と Height の作業ブロック。使わなくなったら Dispose で放す。</summary>
    internal sealed class NormalOutputView : IDisposable
    {
        const string ShaderName = "Hidden/YoluPainter/NormalOutput";
        readonly bool allowGpu;
        TileGpuCompositor height;
        Material material;
        RenderTexture output;
        Texture2D cpuOutput;
        // CPU 経路で前回計算したときの状態（同じなら計算し直さない）
        PaintDocument cpuDocument; long cpuRevision = -1, cpuSerial = -1; NormalSettings cpuSettings;

        public Texture Texture => output != null ? (Texture)output : cpuOutput;
        public string Backend { get; private set; } = "Not initialized";
        /// <summary>Height → Normal 用の Height の合成器（無効のあいだは null）。診断・テスト用。</summary>
        internal TileGpuCompositor HeightCompositor => height;
        /// <summary>直近の Update が GPU で描いたか。</summary>
        public bool UsedGpu { get; private set; }

        /// <param name="allowGpu">false なら CPU の経路だけを使う（テスト用）。</param>
        public NormalOutputView(bool allowGpu = true) { this.allowGpu = allowGpu; }

        /// <param name="normalComposite">Normal チャンネルのレイヤーの合成（<see cref="TileGpuCompositor.Texture"/>）。</param>
        public void Update(PaintDocument doc, Texture normalComposite)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var settings = doc.NormalSettings;
            var shader = allowGpu ? Shader.Find(ShaderName) : null;
            bool gpu = ShaderHealth.IsUsable(shader) && normalComposite is RenderTexture && normalComposite.width == doc.Width && normalComposite.height == doc.Height;
            Texture heights = null;
            if (gpu && settings.DeriveFromHeight)
            {
                if (height == null) height = new TileGpuCompositor(allowGpu) { ResidentBudgetBytes = 0 };
                height.Update(doc, PaintChannel.Height); heights = height.Texture;
                gpu = heights is RenderTexture;
            }
            if (!gpu || !settings.DeriveFromHeight) ReleaseHeight(); // CPU の経路は Height を正本から合成する
            if (gpu)
            {
                try { RenderGpu(doc, shader, normalComposite, heights, settings); UsedGpu = true; Backend = "GPU Normal output"; return; }
                catch (Exception ex) { ReleaseGpu(); Backend = "CPU Normal output after GPU failure: " + ex.Message; }
            }
            else Backend = "CPU Normal output (the GPU output pass or a GPU composite is unavailable)";
            UsedGpu = false;
            RenderCpu(doc, settings);
        }

        void RenderGpu(PaintDocument doc, Shader shader, Texture normal, Texture heights, NormalSettings settings)
        {
            int w = doc.Width, h = doc.Height;
            if (material == null) material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (output == null || output.width != w || output.height != h)
            {
                ReleaseOutput();
                output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false };
                if (!output.Create()) { ReleaseOutput(); throw new InvalidOperationException("RenderTexture.Create failed"); }
            }
            if (ReferenceEquals(normal, output) || ReferenceEquals(heights, output)) throw new InvalidOperationException("A GPU pass must not read and write the same render texture.");
            DestroyCpu();
            material.SetTexture("_HeightTex", heights != null ? heights : Texture2D.blackTexture);
            material.SetVector("_Size", new Vector4(w, h, 1f / w, 1f / h));
            material.SetVector("_Settings", new Vector4(settings.DeriveFromHeight ? 1 : 0, (float)settings.Strength, settings.Edges == HeightEdgeMode.Wrap ? 1 : 0, 0));
            // 入力は画素の中心で読む。読むあいだだけ点サンプリングにして、双線形の重みの誤差が入らないようにする（持ち主の設定は戻す）
            var active = RenderTexture.active; var normalFilter = normal.filterMode; var heightFilter = heights != null ? heights.filterMode : FilterMode.Point;
            try
            {
                normal.filterMode = FilterMode.Point; if (heights != null) heights.filterMode = FilterMode.Point;
                Graphics.Blit(normal, output, material, 0);
            }
            finally
            {
                normal.filterMode = normalFilter; if (heights != null) heights.filterMode = heightFilter;
                material.SetTexture("_HeightTex", Texture2D.blackTexture);
                RenderTexture.active = active;
            }
            cpuDocument = null;
        }

        void RenderCpu(PaintDocument doc, NormalSettings settings)
        {
            ReleaseOutput();
            if (ReferenceEquals(cpuDocument, doc) && cpuRevision == doc.Revision && cpuSerial == doc.ChangeSerial && settings.Equals(cpuSettings) && cpuOutput != null) return;
            var pixels = NormalMaps.Output(doc);
            if (cpuOutput == null || cpuOutput.width != doc.Width || cpuOutput.height != doc.Height)
            {
                DestroyCpu();
                cpuOutput = new Texture2D(doc.Width, doc.Height, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            }
            cpuOutput.LoadRawTextureData(pixels); cpuOutput.Apply(false, false);
            cpuDocument = doc; cpuRevision = doc.Revision; cpuSerial = doc.ChangeSerial; cpuSettings = settings;
        }

        void ReleaseHeight() { height?.Dispose(); height = null; }
        void ReleaseOutput()
        {
            if (output == null) return;
            if (RenderTexture.active == output) RenderTexture.active = null;
            output.Release(); UnityEngine.Object.DestroyImmediate(output); output = null;
        }
        void ReleaseGpu() { ReleaseOutput(); if (material != null) UnityEngine.Object.DestroyImmediate(material); material = null; }
        void DestroyCpu() { if (cpuOutput != null) UnityEngine.Object.DestroyImmediate(cpuOutput); cpuOutput = null; cpuDocument = null; }

        public void Dispose() { ReleaseHeight(); ReleaseGpu(); DestroyCpu(); }
    }
}
