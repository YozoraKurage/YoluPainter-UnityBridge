using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>
    /// 3D ビューの表示（環境・影・トーンマッピング・照明なしの見せ方）。どれもプレビューの中だけで、元のマテリアル・テクスチャ・取り込みの設定、
    /// シーンのライト・RenderSettings には触れない（環境光と映り込みは PreviewRenderUtility と同じく、プレビューのシーンの照明の上書き
    /// <c>Unsupported.SetOverrideLightingSettings</c> の中で入れ、EndPreview が戻す）。描き込み・ピック・ブラシのカーソルは見せ方によらない。
    /// </summary>
    public sealed partial class IsolatedModelPreview
    {
        /// <summary>中立の表示の映り込みの粗さ（誘電体。塗った値は変えず、形を読む手がかりとして足す）。</summary>
        internal const float NeutralReflectionRoughness = .5f;

        PreviewEnvironment environment; PreviewShadowMap shadowMap;
        Material toneMap; bool toneMapChecked;
        Mesh backgroundQuad;
        bool environmentActive, shadowActive, toneMapActive;
        readonly List<string> displayNotes = new List<string>();
        readonly List<(Mesh, Matrix4x4)> shadowCasters = new List<(Mesh, Matrix4x4)>();

        /// <summary>環境の元のテクスチャ（Scene の environment が Texture のときに使う。Cubemap か緯度経度の Texture2D。GPU で読むだけ）。</summary>
        public Texture EnvironmentTexture { get => environmentTexture; set { if (!ReferenceEquals(environmentTexture, value)) { environmentTexture = value; contentVersion++; } } } // 消えたテクスチャ（Unity の null）も入れ替える
        Texture environmentTexture;
        /// <summary>直前の描画で、頼まれた環境・影・トーンマッピングを見せられなかった理由（全部見せられたら null）。</summary>
        public string DisplayNote => displayNotes.Count == 0 ? null : string.Join(" ", displayNotes);
        /// <summary>試験用: 直前の描画で使ったもの。</summary>
        internal bool EnvironmentShown => environmentActive;
        internal bool ShadowShown => shadowActive;
        internal bool ToneMapped => toneMapActive;
        internal bool BackgroundShown { get; private set; }
        internal bool ShadowOverlayDrawn { get; private set; }
        internal PreviewEnvironment Environment => environment;
        internal PreviewShadowMap Shadows => shadowMap;
        /// <summary>計測用: 直前の描画の準備（環境の焼き・影のマップ）にかかった時間。</summary>
        internal double LastPrepareMilliseconds { get; private set; }

        // ───────────── 照明なしの見せ方（チャンネル・メッシュマップだけ） ─────────────

        readonly List<Material> unlitMaterials = new List<Material>();
        IReadOnlyDictionary<int, Texture> unlitTextures;
        static Texture2D s_missing;
        /// <summary>照明なしの見せ方で、テクスチャの無いスロットに見せる暗い灰色。</summary>
        static Texture2D Missing
        {
            get
            {
                if (s_missing != null) return s_missing;
                s_missing = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, name = "YoluPainter unlit view: nothing to show" };
                s_missing.SetPixel(0, 0, new Color(.16f, .16f, .17f, 1)); s_missing.Apply(false, true);
                return s_missing;
            }
        }

        /// <summary>照明なしで見せているか（<see cref="SetUnlitTextures"/>）。</summary>
        public bool ShowsUnlit => unlitTextures != null;

        /// <summary>
        /// スロットごとのテクスチャを、照明・環境・影・トーンマッピングなしでそのまま見せる（Substance の 1 チャンネルだけ・メッシュマップだけの
        /// 表示）。textures に無いスロットは暗い灰色。null で描き方（中立・マテリアル）の表示に戻す。表示だけで、元のマテリアルには触れない。
        /// </summary>
        public void SetUnlitTextures(IReadOnlyDictionary<int, Texture> textures)
        {
            ThrowIfDisposed();
            bool was = unlitTextures != null;
            unlitTextures = textures; contentVersion++;
            if (textures != null) for (int i = 0; i < materials.Count; i++) UnlitMaterial(i);
            if (was != (textures != null)) ApplyRendererMaterials();
        }
        /// <summary>試験用: 照明なしの見せ方でスロットに見せているテクスチャ（照明なしでなければ null）。</summary>
        internal Texture ShownUnlitTexture(int slot) => ShowsUnlit && slot >= 0 && slot < unlitMaterials.Count && unlitMaterials[slot] != null ? unlitMaterials[slot].GetTexture("_MainTex") : null;

        /// <summary>照明なしの材料（中立のシェーダーを照明なしで。テクスチャは今の <see cref="unlitTextures"/>）。</summary>
        Material UnlitMaterial(int slot)
        {
            while (unlitMaterials.Count <= slot) unlitMaterials.Add(null);
            var m = unlitMaterials[slot];
            if (m == null)
            {
                var shader = Shader.Find("Hidden/YoluPainter/PreviewSurface");
                if (shader == null) return materials[slot];
                m = unlitMaterials[slot] = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = "YoluPainter unlit view (preview only)" };
                m.SetFloat("_PreviewLit", 0); m.SetFloat("_UseNormalMap", 0); m.SetColor("_Color", Color.white);
            }
            Texture texture = null;
            if (unlitTextures != null) unlitTextures.TryGetValue(slot, out texture);
            m.SetTexture("_MainTex", texture != null ? texture : Missing);
            return m;
        }
        void DisposeUnlit() { foreach (var m in unlitMaterials) if (m != null) Object.DestroyImmediate(m); unlitMaterials.Clear(); }

        // ───────────── 環境・影・トーンマッピングの準備（BeginPreview の前） ─────────────

        /// <summary>
        /// 描く前に: 環境を焼き（変わったときだけ）、影のマップを描き（変わったときだけ）、中立の材料に入れ、トーンマッピングのために描き先を
        /// HDR にするか決める。照明なしの見せ方では何も使わない。heightPixels は描く画像の高さ（背景の元を読む mip に使う）。
        /// </summary>
        void PrepareDisplay()
        {
            var started = DateTime.UtcNow;
            var s = Scene ?? defaultScene;
            displayNotes.Clear(); environmentActive = false; shadowActive = false;
            bool unlit = ShowsUnlit;
            if (!unlit && s.environment != PreviewEnvironmentSource.None)
            {
                if (environment == null) environment = new PreviewEnvironment();
                Texture texture = null;
                if (s.environment == PreviewEnvironmentSource.Texture)
                {
                    texture = EnvironmentTexture;
                    string refusal = PreviewEnvironment.Refusal(texture);
                    if (refusal != null) { displayNotes.Add(refusal + " The built-in sky is shown instead."); texture = null; }
                }
                try { environmentActive = environment.Update(s, texture); if (!environmentActive) displayNotes.Add(environment.Problem); }
                catch (Exception ex) { displayNotes.Add("The environment could not be prepared: " + ex.Message); }
            }
            if (!unlit && s.shadows && HasModel)
            {
                if (shadowMap == null) shadowMap = new PreviewShadowMap();
                shadowCasters.Clear();
                foreach (var (renderer, _) in slotRenderers)
                {
                    if (renderer == null) continue;
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null) shadowCasters.Add((filter.sharedMesh, renderer.transform.localToWorldMatrix));
                }
                try { shadowActive = shadowMap.Update(shadowCasters, Bounds, s.LightDirection, revision); if (!shadowActive) displayNotes.Add(shadowMap.Problem); }
                catch (Exception ex) { displayNotes.Add("The shadow map could not be drawn: " + ex.Message); }
            }
            // 中立の材料: 環境か影を使うあいだだけ、それを読むシェーダー（PreviewSurfaceLit）に替える。使えなければ見せずに知らせる
            if ((environmentActive || shadowActive) && !LitShaderUsable())
            {
                displayNotes.Add("The neutral view's lit shader (" + LitShaderName + ") cannot be used here, so the neutral view has no environment or shadows.");
                neutralLit = false;
            }
            else neutralLit = environmentActive || shadowActive;
            foreach (var material in materials) ApplyNeutralDisplay(material, s);
            // トーンマッピング: 描き先を HDR にする（前の版と同じ絵のときは 8 bit のまま）。描き先の形式は PreviewRenderUtility が大きさの
            // 変わったときにしか作り直さないので、HDR の切り替えでは今の描き先を捨てて作り直させる
            toneMapActive = !unlit && s.UsesToneMapping && ToneMapUsable();
            if (!unlit && s.UsesToneMapping && !toneMapActive) displayNotes.Add("The tone-mapping shader cannot be used here; the 3D view is shown without it.");
            if (preview.camera.allowHDR != toneMapActive)
            {
                preview.camera.allowHDR = toneMapActive;
                var old = preview.camera.targetTexture;
                if (old != null) { preview.camera.targetTexture = null; Object.DestroyImmediate(old); }
            }
            LastPrepareMilliseconds = (DateTime.UtcNow - started).TotalMilliseconds;
        }

        internal const string LitShaderName = "Hidden/YoluPainter/PreviewSurfaceLit";
        Shader litShader, neutralShader; bool litChecked; bool neutralLit;
        bool LitShaderUsable()
        {
            if (!litChecked) { litChecked = true; var shader = Shader.Find(LitShaderName); litShader = ShaderHealth.IsUsable(shader) ? shader : null; }
            return litShader != null;
        }

        void ApplyNeutralDisplay(Material m, PreviewSceneSettings s)
        {
            if (neutralShader == null) neutralShader = Shader.Find("Hidden/YoluPainter/PreviewSurface");
            var wanted = neutralLit ? litShader : neutralShader;
            if (wanted != null && m.shader != wanted) m.shader = wanted;
            if (!neutralLit) return;
            m.SetFloat("_YPEnvOn", environmentActive ? 1 : 0);
            if (environmentActive)
            {
                m.SetVectorArray("_YPSH", environment.ShaderSH);
                m.SetTexture("_YPEnvCube", environment.Cube);
                m.SetVector("_YPEnvParams", new Vector4(s.environmentIntensity, PreviewEnvironment.MipOf(NeutralReflectionRoughness), 1, 0));
            }
            if (shadowMap != null) shadowMap.Apply(m, shadowActive, s.shadowSoftness); else m.SetVector("_YPShadowParams", Vector4.zero);
        }

        bool ToneMapUsable()
        {
            if (!toneMapChecked)
            {
                toneMapChecked = true;
                var shader = Shader.Find("Hidden/YoluPainter/PreviewToneMap");
                if (ShaderHealth.IsUsable(shader) && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
                    toneMap = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = "YoluPainter tone mapping (preview only)" };
            }
            return toneMap != null;
        }

        // ───────────── BeginPreview と EndPreview のあいだ ─────────────

        /// <summary>カメラで描く前に積む物: 背景の環境（画面いっぱいの四角、待ち行列 1000）と、マテリアル表示の影の重ね描き（2500）。</summary>
        void DrawDisplayExtras(float heightPixels)
        {
            BackgroundShown = false; ShadowOverlayDrawn = false;
            var s = Scene ?? defaultScene;
            if (environmentActive && s.environmentBackground)
            {
                var texture = s.environment == PreviewEnvironmentSource.Texture ? EnvironmentTexture : null;
                environment.PrepareBackground(s, texture, preview.camera, heightPixels);
                preview.DrawMesh(BackgroundQuad, Matrix4x4.identity, environment.BackgroundMaterial, 0);
                BackgroundShown = true;
            }
            if (shadowActive && shading == PreviewShading.Material)
            {
                var overlay = shadowMap.Overlay;
                shadowMap.Apply(overlay, true, s.shadowSoftness);
                bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Color direct = s.lightColor * (.769f * s.intensity), flat = s.ambient * .4f;
                if (linear) { direct = direct.linear; flat = flat.linear; }
                overlay.SetVector("_YPOverlay", new Vector4(Luminance(direct), Luminance(flat), environmentActive ? 1 : 0, s.environmentIntensity));
                if (environmentActive) overlay.SetVectorArray("_YPSH", environment.ShaderSH);
                foreach (var (renderer, slots) in slotRenderers)
                {
                    if (renderer == null) continue;
                    var filter = renderer.GetComponent<MeshFilter>(); var mesh = filter != null ? filter.sharedMesh : null;
                    if (mesh == null) continue;
                    for (int sub = 0; sub < slots.Length && sub < mesh.subMeshCount; sub++)
                        if (MaterialView.Display(slots[sub]) != null) { preview.DrawMesh(mesh, renderer.transform.localToWorldMatrix, overlay, sub); ShadowOverlayDrawn = true; }
                }
            }
        }
        static float Luminance(Color c) => .2126f * c.r + .7152f * c.g + .0722f * c.b;

        Mesh BackgroundQuad
        {
            get
            {
                if (backgroundQuad != null) return backgroundQuad;
                backgroundQuad = new Mesh { name = "YoluPainter environment background (preview only)", hideFlags = HideFlags.HideAndDontSave };
                backgroundQuad.SetVertices(new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) });
                backgroundQuad.SetUVs(0, new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up });
                backgroundQuad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
                // 頂点はクリップ空間に置くので、カメラの視錐台で間引かれないよう境界を大きくする
                backgroundQuad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
                return backgroundQuad;
            }
        }

        /// <summary>
        /// PreviewRenderUtility.Render の代わり: 同じ手順（プレビューのシーンの照明の上書き・光・SRP の旗）でカメラを描くが、環境を使うときは
        /// 一様な環境光の代わりに環境の SH と映り込みのキューブを入れる（Render は描く直前に一様な環境光に戻してしまうので自分で呼ぶ）。
        /// 上書きは EndPreview / EndStaticPreview が戻す。シーンの RenderSettings には届かない。
        /// </summary>
        void RenderCamera()
        {
            var s = Scene ?? defaultScene;
            if (!EditorApplication.isUpdating && Unsupported.SetOverrideLightingSettings(preview.camera.scene))
            {
                if (environmentActive && !ShowsUnlit)
                {
                    RenderSettings.ambientMode = AmbientMode.Custom;
                    RenderSettings.ambientProbe = environment.Ambient * s.environmentIntensity;
                    RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                    RenderSettings.customReflectionTexture = environment.Cube;
                    RenderSettings.reflectionIntensity = Mathf.Min(1, s.environmentIntensity);
                }
                else { RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = preview.ambientColor; }
            }
            foreach (var light in preview.lights) light.enabled = true;
            bool previous = Unsupported.useScriptableRenderPipeline;
            Unsupported.useScriptableRenderPipeline = false;
            try { preview.camera.Render(); }
            finally { Unsupported.useScriptableRenderPipeline = previous; }
        }

        RenderTexture toneMapped;
        /// <summary>
        /// カメラで描いた後: HDR の描き先に露出とトーンマッピングを当て、前の版と同じ 8 bit（UNorm）の描き先に書く（窓はそれを描く。描き先の
        /// 値の意味は前の版と同じ「画面にそのまま出す値」なので、どちらのカラースペースでも、ガンマの値としてリニアに直して当てて戻す）。
        /// copyBack なら HDR の描き先にも書き戻す（EndStaticPreview が描き先を読むため）。同じテクスチャは読み書きしない。当てなければ null。
        /// </summary>
        Texture FinishDisplay(bool copyBack = false)
        {
            if (!toneMapActive) return null;
            var target = preview.camera.targetTexture;
            if (target == null) return null;
            var s = Scene ?? defaultScene;
            if (toneMapped == null || toneMapped.width != target.width || toneMapped.height != target.height)
            {
                if (toneMapped != null) { toneMapped.Release(); Object.DestroyImmediate(toneMapped); }
                toneMapped = new RenderTexture(target.width, target.height, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm) { hideFlags = HideFlags.HideAndDontSave, name = "YoluPainter tone-mapped preview" };
            }
            toneMap.SetVector("_YPToneMap", new Vector4((int)s.toneMapping, Mathf.Pow(2, s.exposure), 1, 0));
            Graphics.Blit(target, toneMapped, toneMap, 0);
            if (copyBack) Graphics.Blit(toneMapped, target);
            return toneMapped;
        }

        void DisposeDisplay()
        {
            environment?.Dispose(); environment = null;
            shadowMap?.Dispose(); shadowMap = null;
            if (toneMap != null) Object.DestroyImmediate(toneMap);
            toneMap = null; toneMapChecked = false; litChecked = false; litShader = null;
            if (backgroundQuad != null) Object.DestroyImmediate(backgroundQuad);
            backgroundQuad = null;
            if (toneMapped != null) { toneMapped.Release(); Object.DestroyImmediate(toneMapped); }
            toneMapped = null;
            DisposeUnlit();
        }
    }
}
