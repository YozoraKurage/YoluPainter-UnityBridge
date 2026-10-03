using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>
    /// 3D ビューの自己の影（<see cref="IsolatedModelPreview"/> の持ち物）: 主な光から見たモデルの深さを、モデルの外接球に合わせた正射影で
    /// <see cref="Size"/>² の RFloat に描く（光の向き・形の世代・外接が変わったときだけ描き直す）。中立のシェーダーは自分で、マテリアル表示は
    /// 掛け算の重ね描き（PreviewShadow.shader のパス 1）で読む。Unity の影（Light.shadows）は使わない: プレビューでも出るが、プロジェクト全体の
    /// QualitySettings（影のオン/オフ・距離・カスケード・解像度）に左右され、組み込みのパイプラインでは柔らかさを選べないため。
    /// </summary>
    internal sealed class PreviewShadowMap : IDisposable
    {
        public const string ShaderName = "Hidden/YoluPainter/PreviewShadow";
        /// <summary>影のマップの 1 辺（RFloat の色 + 16 bit の深さ ≈ 24 MiB）。</summary>
        public const int Size = 2048;
        const int PassCaster = 0;
        internal const int PassOverlay = 1;

        RenderTexture map; Material material; CommandBuffer commands;
        string problem; bool checkedShader;
        string renderedKey;
        Matrix4x4 worldToShadow; float diameter = 1; Vector3 toLight = Vector3.up;

        public string Problem => problem;
        /// <summary>重ね描きの材料（マテリアル表示。描く前に <see cref="Apply"/> で合わせる）。</summary>
        public Material Overlay => material;
        public RenderTexture Map => map;
        /// <summary>描いた回数と直前の時間（試験と計測用）。</summary>
        public int Renders { get; private set; }
        public double LastRenderMilliseconds { get; private set; }

        bool EnsureResources()
        {
            if (!checkedShader)
            {
                checkedShader = true;
                var shader = Shader.Find(ShaderName);
                if (!ShaderHealth.IsUsable(shader)) problem = "The shadow shader (" + ShaderName + ") cannot be used here.";
                else if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat)) problem = "This GPU cannot render to 32-bit float textures, which the shadow map needs.";
                else material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = "YoluPainter shadow (preview only)", renderQueue = 2500 };
            }
            if (problem != null) return false;
            if (map == null)
            {
                map = new RenderTexture(Size, Size, 16, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false, hideFlags = HideFlags.HideAndDontSave, name = "YoluPainter shadow map (preview only)" };
                if (!map.Create()) { Object.DestroyImmediate(map); map = null; problem = "Could not create the " + Size + "² shadow map."; return false; }
                renderedKey = null;
            }
            if (commands == null) commands = new CommandBuffer { name = "YoluPainter preview shadow map" };
            return true;
        }

        /// <summary>
        /// 影のマップを描く（光の向き・形の世代・外接が前と同じなら描かない）。meshes は表示しているメッシュとその置き方（サブメッシュは全部）。
        /// 描けたら true。使えなければ false（理由は <see cref="Problem"/>）。
        /// </summary>
        public bool Update(IReadOnlyList<(Mesh mesh, Matrix4x4 matrix)> meshes, Bounds bounds, Vector3 directionToLight, int revision)
        {
            if (!EnsureResources()) return false;
            directionToLight = directionToLight.sqrMagnitude > 1e-12f ? directionToLight.normalized : Vector3.up;
            string key = revision + "|" + bounds.center.ToString("R") + bounds.extents.ToString("R") + "|" + directionToLight.ToString("R") + "|" + meshes.Count;
            if (key == renderedKey && map.IsCreated()) return true;
            var started = DateTime.UtcNow;
            float radius = Mathf.Max(1e-4f, bounds.extents.magnitude * 1.02f);
            diameter = radius * 2; toLight = directionToLight;
            var rotation = Quaternion.LookRotation(-directionToLight, Mathf.Abs(directionToLight.y) > .99f ? Vector3.forward : Vector3.up);
            Vector3 right = rotation * Vector3.right, up = rotation * Vector3.up, forward = rotation * Vector3.forward, center = bounds.center;
            // 世界 → (u, v, 深さ 0〜1。0 が光の側): u = (p − c)·右 / 直径 + 0.5 …
            worldToShadow = Matrix4x4.identity;
            worldToShadow.SetRow(0, Row(right, center, diameter));
            worldToShadow.SetRow(1, Row(up, center, diameter));
            worldToShadow.SetRow(2, Row(forward, center, diameter));
            worldToShadow.SetRow(3, new Vector4(0, 0, 0, 1));
            // 光のカメラ（外接球の手前から奥まで）。Unity のカメラの空間は −Z が前
            var eye = center - forward * (radius * 1.5f);
            var view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(eye, rotation, Vector3.one).inverse;
            var projection = Matrix4x4.Ortho(-radius, radius, -radius, radius, radius * .25f, radius * 2.75f);
            material.SetMatrix("_YPShadowViewProj", GL.GetGPUProjectionMatrix(projection, true) * view);
            material.SetMatrix("_YPShadowMatrix", worldToShadow);
            commands.Clear();
            commands.SetRenderTarget(map);
            commands.ClearRenderTarget(true, true, Color.white);
            foreach (var (mesh, matrix) in meshes)
                if (mesh != null) for (int sub = 0; sub < mesh.subMeshCount; sub++) commands.DrawMesh(mesh, matrix, material, sub, PassCaster);
            var active = RenderTexture.active;
            try { Graphics.ExecuteCommandBuffer(commands); }
            finally { RenderTexture.active = active; }
            renderedKey = key; Renders++;
            LastRenderMilliseconds = (DateTime.UtcNow - started).TotalMilliseconds;
            return true;
        }

        static Vector4 Row(Vector3 axis, Vector3 center, float diameter)
        {
            var a = axis / diameter;
            return new Vector4(a.x, a.y, a.z, .5f - Vector3.Dot(a, center));
        }

        /// <summary>
        /// 材料に影の読み方を入れる（softness 0〜1: ぼかしの半径をマップの 0.75 画素から 2.5% まで）。on が false なら使わない印だけ入れる。
        /// </summary>
        public void Apply(Material m, bool on, float softness)
        {
            if (m == null) return;
            if (!on || map == null) { m.SetVector("_YPShadowParams", Vector4.zero); return; }
            float radius = Mathf.Lerp(.75f / Size, .025f, Mathf.Clamp01(softness));
            float texelWorld = diameter / Size;
            // 深さの偏り（深さ 1 = 直径）: 1.5 画素と、ぼかしの半径に比例するぶん（広い範囲を読むほど面の傾きで自分を影にしやすい）
            float bias = (1.5f + radius * Size * .35f) / Size;
            m.SetTexture("_YPShadowMap", map);
            m.SetMatrix("_YPShadowMatrix", worldToShadow);
            m.SetVector("_YPShadowParams", new Vector4(1, radius, bias, texelWorld * 1.5f));
            m.SetVector("_YPShadowLight", new Vector4(toLight.x, toLight.y, toLight.z, 0));
        }

        public void Dispose()
        {
            if (map != null) { map.Release(); Object.DestroyImmediate(map); }
            if (material != null) Object.DestroyImmediate(material);
            commands?.Release();
            map = null; material = null; commands = null; renderedKey = null; checkedShader = false; problem = null;
        }
    }
}
