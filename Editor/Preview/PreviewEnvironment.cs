using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>
    /// 3D ビューの環境（<see cref="IsolatedModelPreview"/> の持ち物）: 元（内蔵の手続きの空、Unity のプロジェクトの Cubemap か緯度経度の Texture2D）を
    /// 環境の向きに回して 1 面 <see cref="CubeSize"/> のキューブの写しに焼き（元の mip を読む。元のテクスチャは GPU で読むだけで、読み取りの
    /// 許可・取り込みの設定・テクスチャそのものは変えない）、粗さの段（Unity の映り込みと同じ mip 0〜6）ごとに GGX で畳み込んだキューブ
    /// （<see cref="Cube"/>。中立の表示の映り込みとマテリアル表示の映り込みに使う）と、環境光の SH（<see cref="Ambient"/>。小さな緯度経度に
    /// 写して読み戻し、CPU で射影する）を作る。明るさは焼かない（使う所で掛ける）。設定と元が前と同じなら焼き直さない。
    /// キューブの面の向きと読み戻しの上下は描画の API で違い得るので、最初に一度、向きの分かる模様を焼いて読み戻して決める（決められなければ
    /// 環境を使わず、理由を <see cref="Problem"/> に置く）。
    /// </summary>
    internal sealed class PreviewEnvironment : IDisposable
    {
        public const string ShaderName = "Hidden/YoluPainter/PreviewEnvironment";
        /// <summary>写しのキューブの 1 面の画素数（RGBA half、mip 込みで 2 枚 ≈ 8 MiB）。</summary>
        public const int CubeSize = 256;
        /// <summary>Unity の映り込みが粗さで読む mip の数（UNITY_SPECCUBE_LOD_STEPS = 6 → mip 0〜6）。</summary>
        public const int ReflectionSteps = 6;
        /// <summary>環境光のために読み戻す緯度経度の大きさ。</summary>
        public const int LatLongWidth = 64, LatLongHeight = 32;
        const int Samples = 64;
        const int PassBake = 0, PassConvolve = 1, PassLatLong = 2, PassBackground = 3;

        Material bake, background;
        RenderTexture source, cube;
        readonly Vector4[] shaderSH = new Vector4[9];
        SphericalHarmonicsL2 ambient;
        string bakedKey;
        string problem; bool checkedShader;

        /// <summary>畳み込んだキューブ（世界の向き。mip 0〜<see cref="ReflectionSteps"/> が粗さ 0〜1）。焼けていなければ null。</summary>
        public RenderTexture Cube => cube;
        /// <summary>環境光（Unity の SphericalHarmonicsL2 の並び。拡散の色 = 照度 / π。明るさ 1）。</summary>
        public SphericalHarmonicsL2 Ambient => ambient;
        /// <summary>中立のシェーダーの _YPSH（<see cref="Ambient"/> の係数を rgb に並べたもの）。</summary>
        public Vector4[] ShaderSH => shaderSH;
        /// <summary>環境を使えない理由（使えれば null）。</summary>
        public string Problem => problem;
        /// <summary>焼いた回数と直前の焼きの時間（試験と計測用）。</summary>
        public int Bakes { get; private set; }
        public double LastBakeMilliseconds { get; private set; }
        /// <summary>背景を描く材料（<see cref="PrepareBackground"/> で合わせる）。</summary>
        internal Material BackgroundMaterial => background;

        // 描画の API ごとの向き（初回に決める）: キューブの面の v を逆にするか、読み戻しの行を逆にするか
        static bool? s_faceFlip, s_readFlip; static string s_orientationProblem;
        internal static void ResetOrientationCheck() { s_faceFlip = null; s_readFlip = null; s_orientationProblem = null; }

        /// <summary>元に使えるテクスチャか（使えなければその理由）。Cubemap と 2D のテクスチャ（緯度経度）だけ。</summary>
        public static string Refusal(Texture texture)
        {
            if (texture == null) return "No environment texture is chosen.";
            if (texture is Cubemap) return null;
            if (texture is Texture2D t2)
            {
                if (t2.width < 8 || t2.height < 4) return "\"" + texture.name + "\" is " + t2.width + " × " + t2.height + "; an environment needs at least 8 × 4.";
                return null;
            }
            if (texture is RenderTexture) return "\"" + texture.name + "\" is a render texture; choose a Cubemap or a latitude-longitude 2D texture asset.";
            return "\"" + texture.name + "\" is a " + texture.GetType().Name + " (" + texture.dimension + "); choose a Cubemap or a latitude-longitude 2D texture.";
        }

        /// <summary>緯度経度の 2D のテクスチャが 2:1 から外れているときの知らせ（使えるが歪む）。</summary>
        public static string AspectNote(Texture texture)
        {
            if (!(texture is Texture2D t)) return null;
            float aspect = t.width / (float)t.height;
            return aspect < 1.8f || aspect > 2.2f ? "\"" + texture.name + "\" is " + t.width + " × " + t.height + ", not 2:1; it is read as a latitude-longitude panorama, so it looks stretched." : null;
        }

        bool EnsureResources()
        {
            if (!checkedShader)
            {
                checkedShader = true;
                var shader = Shader.Find(ShaderName);
                if (!ShaderHealth.IsUsable(shader)) problem = "The environment shader (" + ShaderName + ") cannot be used here.";
                else if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)) problem = "This GPU cannot render to half-float textures, which the environment needs.";
                else
                {
                    bake = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = "YoluPainter environment bake (preview only)" };
                    background = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = "YoluPainter environment background (preview only)", renderQueue = 1000 };
                    var samples = new Vector4[Samples];
                    for (int i = 0; i < Samples; i++) samples[i] = new Vector4((i + .5f) / Samples, RadicalInverse((uint)i), 0, 0);
                    bake.SetVectorArray("_YPSamples", samples);
                }
            }
            if (problem != null) return false;
            if (s_faceFlip == null && s_orientationProblem == null) CheckOrientation();
            if (s_orientationProblem != null) { problem = s_orientationProblem; return false; }
            if (source == null) source = NewCube("YoluPainter environment (box mips)", CubeSize);
            if (cube == null) cube = NewCube("YoluPainter environment (convolved)", CubeSize);
            return true;
        }

        static float RadicalInverse(uint bits)
        {
            bits = (bits << 16) | (bits >> 16);
            bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
            bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
            bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
            bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
            return bits * 2.3283064365386963e-10f;
        }

        static RenderTexture NewCube(string name, int size)
        {
            var rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            { dimension = TextureDimension.Cube, useMipMap = true, autoGenerateMips = false, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave, name = name };
            if (!rt.Create()) { Object.DestroyImmediate(rt); throw new InvalidOperationException("Could not create the environment cube (" + size + ")."); }
            return rt;
        }

        /// <summary>
        /// 設定 s の環境を焼く（元・向き・空の色が前と同じなら何もしない）。sourceTexture は Texture のときの元（Sky なら使わない）。
        /// 焼けたら true。使えなければ false（理由は <see cref="Problem"/>）。
        /// </summary>
        public bool Update(PreviewSceneSettings s, Texture sourceTexture)
        {
            if (!EnsureResources()) return false;
            bool useTexture = s.environment == PreviewEnvironmentSource.Texture && sourceTexture != null && Refusal(sourceTexture) == null;
            string sourceKey = useTexture
                ? "T" + sourceTexture.GetInstanceID() + "/" + sourceTexture.updateCount + "/" + sourceTexture.width + "x" + sourceTexture.height
                : "S" + s.skyZenith + s.skyHorizon + s.skyGround;
            string key = sourceKey + "|" + s.environmentRotation.ToString("R");
            if (key == bakedKey && cube.IsCreated() && source.IsCreated()) return true;
            var started = DateTime.UtcNow;
            SetSource(bake, s, useTexture ? sourceTexture : null);
            float sourceLod = 0;
            if (useTexture)
            {
                // 元の 1 画素の立体角と写しの 1 画素の立体角の比から、元を読む mip を決める（細かい元を粗い写しに焼くときのちらつきを抑える）
                double texel = sourceTexture is Cubemap ? 4 * Math.PI / (6.0 * sourceTexture.width * sourceTexture.width) : 4 * Math.PI / ((double)sourceTexture.width * sourceTexture.height);
                double target = 4 * Math.PI / (6.0 * CubeSize * CubeSize);
                sourceLod = Mathf.Max(0, (float)(0.5 * Math.Log(target / texel, 2)));
            }
            var active = RenderTexture.active;
            try
            {
                bake.SetVector("_YPSource", new Vector4(Kind(s, useTexture ? sourceTexture : null), sourceLod, 1, 0));
                for (int face = 0; face < 6; face++) DrawFace(source, 0, face, PassBake);
                source.GenerateMips();
                bake.SetTexture("_YPSrcCube", source);
                int lastMip = source.mipmapCount - 1;
                for (int mip = 0; mip < cube.mipmapCount; mip++)
                {
                    float roughness = PerceptualRoughness(Mathf.Min(mip, ReflectionSteps));
                    float alpha = roughness * roughness;
                    bake.SetVector("_YPConvolve", new Vector4(mip == 0 ? 0 : Mathf.Max(alpha, 1e-3f), CubeSize, Samples, lastMip));
                    for (int face = 0; face < 6; face++) DrawFace(cube, mip, face, PassConvolve);
                }
                // 環境光: 元が変わったときだけ読み戻して射影する（GPU を待つ）。向きだけが変わったら、前の SH を上の軸のまわりに回す
                if (sourceKey != ambientSourceKey) { ReadAmbient(); ambientSourceKey = sourceKey; ambientRotation = s.environmentRotation; unrotatedAmbient = ambient; }
                else SetAmbient(RotateY(unrotatedAmbient, s.environmentRotation - ambientRotation));
            }
            finally { RenderTexture.active = active; bake.SetTexture("_YPSourceLatLong", null); bake.SetTexture("_YPSourceCube", null); }
            bakedKey = key; Bakes++;
            LastBakeMilliseconds = (DateTime.UtcNow - started).TotalMilliseconds;
            return true;
        }

        /// <summary>Unity の映り込みの mip（perceptualRoughness × (1.7 − 0.7 × perceptualRoughness) × 6）から粗さを戻す。</summary>
        internal static float PerceptualRoughness(int mip)
        {
            float m = Mathf.Clamp(mip, 0, ReflectionSteps) / (float)ReflectionSteps;
            return Mathf.Clamp01((1.7f - Mathf.Sqrt(Mathf.Max(0, 2.89f - 2.8f * m))) / 1.4f);
        }
        /// <summary>粗さから Unity と同じ mip。</summary>
        internal static float MipOf(float perceptualRoughness) => perceptualRoughness * (1.7f - .7f * perceptualRoughness) * ReflectionSteps;

        static float Kind(PreviewSceneSettings s, Texture texture) => texture == null ? 0 : texture is Cubemap ? 2 : 1;

        void SetSource(Material m, PreviewSceneSettings s, Texture texture)
        {
            float radians = s.environmentRotation * Mathf.Deg2Rad;
            m.SetVector("_YPRotation", new Vector4(Mathf.Cos(radians), Mathf.Sin(radians), 0, 0));
            // 空の色は色の欄の値（sRGB）。SetColor はリニアのカラースペースならリニアに直して入れる
            m.SetColor("_YPSkyZenith", s.skyZenith); m.SetColor("_YPSkyHorizon", s.skyHorizon); m.SetColor("_YPSkyGround", s.skyGround);
            m.SetTexture("_YPSourceLatLong", texture is Texture2D ? texture : null);
            m.SetTexture("_YPSourceCube", texture is Cubemap ? texture : null);
        }

        /// <summary>キューブの 1 面（mip）を画面いっぱいの四角で塗る。面の向きは <see cref="FaceBasis"/>（向きの確かめで決めた上下）。</summary>
        void DrawFace(RenderTexture target, int mip, int face, int pass) => DrawFace(bake, target, mip, face, pass, s_faceFlip == true);

        static void DrawFace(Material m, RenderTexture target, int mip, int face, int pass, bool flip)
        {
            var (z, x, y) = FaceBasis(face);
            if (flip) y = -y;
            m.SetVector("_YPFaceZ", z); m.SetVector("_YPFaceX", x); m.SetVector("_YPFaceY", y);
            Graphics.SetRenderTarget(target, mip, (CubemapFace)face);
            FullscreenQuad(m, pass);
        }

        static void FullscreenQuad(Material m, int pass)
        {
            GL.PushMatrix();
            try
            {
                GL.LoadOrtho();
                m.SetPass(pass);
                GL.Begin(GL.QUADS);
                GL.TexCoord2(0, 0); GL.Vertex3(0, 0, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(1, 0, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(1, 1, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(0, 1, 0);
                GL.End();
            }
            finally { GL.PopMatrix(); }
        }

        /// <summary>キューブの面の向き（主軸 Z、面の u が進む向き X、v が進む向き Y）。GL・D3D の表の (sc, tc) に合わせた並び。</summary>
        internal static (Vector4 z, Vector4 x, Vector4 y) FaceBasis(int face)
        {
            switch ((CubemapFace)face)
            {
                case CubemapFace.PositiveX: return (new Vector4(1, 0, 0), new Vector4(0, 0, -1), new Vector4(0, -1, 0));
                case CubemapFace.NegativeX: return (new Vector4(-1, 0, 0), new Vector4(0, 0, 1), new Vector4(0, -1, 0));
                case CubemapFace.PositiveY: return (new Vector4(0, 1, 0), new Vector4(1, 0, 0), new Vector4(0, 0, 1));
                case CubemapFace.NegativeY: return (new Vector4(0, -1, 0), new Vector4(1, 0, 0), new Vector4(0, 0, -1));
                case CubemapFace.PositiveZ: return (new Vector4(0, 0, 1), new Vector4(1, 0, 0), new Vector4(0, -1, 0));
                default: return (new Vector4(0, 0, -1), new Vector4(-1, 0, 0), new Vector4(0, -1, 0));
            }
        }

        // ───── 環境光（SH） ─────

        static readonly float[] K = { .282095f, .488603f, .488603f, .488603f, 1.092548f, 1.092548f, .315392f, 1.092548f, .546274f };
        static readonly float[] Band = { 1, 2f / 3, 2f / 3, 2f / 3, .25f, .25f, .25f, .25f, .25f };
        static float Basis(int i, Vector3 d)
        {
            switch (i)
            {
                case 0: return 1;
                case 1: return d.y;
                case 2: return d.z;
                case 3: return d.x;
                case 4: return d.x * d.y;
                case 5: return d.y * d.z;
                case 6: return 3 * d.z * d.z - 1;
                case 7: return d.x * d.z;
                default: return d.x * d.x - d.y * d.y;
            }
        }

        /// <summary>緯度経度の向き（Unity の Skybox/Panoramic と同じ読み方）。</summary>
        internal static Vector3 FromLatLong(float u, float v)
        {
            float longitude = (.5f - u) * 2 * Mathf.PI, latitude = (1 - v) * Mathf.PI, s = Mathf.Sin(latitude);
            return new Vector3(s * Mathf.Cos(longitude), Mathf.Cos(latitude), s * Mathf.Sin(longitude));
        }

        /// <summary>
        /// 緯度経度に並べた放射輝度（行は下から。row-major、w × h）から、拡散の色（照度 / π）の SH を作る（Unity の SphericalHarmonicsL2 の
        /// 並びと式: c_i = 帯の畳み込み × ∫ L Y_i dω × 正規化の定数。一様な 1 の環境で c0 = 1）。
        /// </summary>
        internal static SphericalHarmonicsL2 Project(Color[] radiance, int w, int h)
        {
            var sums = new double[3, 9];
            double du = 2 * Math.PI / w, dv = Math.PI / h;
            for (int y = 0; y < h; y++)
            {
                float v = (y + .5f) / h; double solid = du * dv * Math.Sin((1 - v) * Math.PI);
                for (int x = 0; x < w; x++)
                {
                    var d = FromLatLong((x + .5f) / w, v); var c = radiance[y * w + x];
                    for (int i = 0; i < 9; i++)
                    {
                        double b = K[i] * Basis(i, d) * solid;
                        sums[0, i] += c.r * b; sums[1, i] += c.g * b; sums[2, i] += c.b * b;
                    }
                }
            }
            var sh = new SphericalHarmonicsL2();
            for (int ch = 0; ch < 3; ch++) for (int i = 0; i < 9; i++) sh[ch, i] = (float)(sums[ch, i] * Band[i] * K[i]);
            return sh;
        }

        string ambientSourceKey; float ambientRotation; SphericalHarmonicsL2 unrotatedAmbient;

        void ReadAmbient()
        {
            var radiance = ReadLatLong(bake, source, LatLongWidth, LatLongHeight, 2, s_readFlip == true);
            SetAmbient(Project(radiance, LatLongWidth, LatLongHeight));
        }
        void SetAmbient(SphericalHarmonicsL2 sh)
        {
            ambient = sh;
            for (int i = 0; i < 9; i++) shaderSH[i] = new Vector4(ambient[0, i], ambient[1, i], ambient[2, i], 0);
        }

        /// <summary>
        /// SH（Unity の並びと式）で表した関数を上の軸のまわりに degrees 回したもの（f'(d) = f(R(−θ) d)。帯ごとに閉じた式。帯 2 は単位球の上で
        /// x² + y² + z² = 1 を使って 3z² − 1 と x² − y² に直した）。
        /// </summary>
        internal static SphericalHarmonicsL2 RotateY(SphericalHarmonicsL2 sh, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            var o = new SphericalHarmonicsL2();
            for (int ch = 0; ch < 3; ch++)
            {
                float c1 = sh[ch, 1], c2 = sh[ch, 2], c3 = sh[ch, 3], c4 = sh[ch, 4], c5 = sh[ch, 5], c6 = sh[ch, 6], c7 = sh[ch, 7], c8 = sh[ch, 8];
                o[ch, 0] = sh[ch, 0];
                o[ch, 1] = c1;
                o[ch, 2] = c * c2 - s * c3;
                o[ch, 3] = s * c2 + c * c3;
                o[ch, 4] = c * c4 + s * c5;
                o[ch, 5] = -s * c4 + c * c5;
                o[ch, 6] = c6 * (c * c - s * s / 2) - c7 * c * s / 2 + c8 * s * s / 2;
                o[ch, 7] = 6 * s * c * c6 + (c * c - s * s) * c7 - 2 * c * s * c8;
                o[ch, 8] = 1.5f * s * s * c6 + c * s / 2 * c7 + (1 + c * c) / 2 * c8;
            }
            return o;
        }

        /// <summary>キューブを緯度経度の w × h に写して読み戻す（行は下から）。</summary>
        static Color[] ReadLatLong(Material m, RenderTexture cubeTexture, int w, int h, float lod, bool flipRows)
        {
            bool full = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat);
            var rt = RenderTexture.GetTemporary(new RenderTextureDescriptor(w, h, full ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGBHalf, 0) { sRGB = false, useMipMap = false, msaaSamples = 1 });
            var read = new Texture2D(w, h, full ? TextureFormat.RGBAFloat : TextureFormat.RGBAHalf, false, true);
            var active = RenderTexture.active;
            try
            {
                m.SetTexture("_YPSrcCube", cubeTexture);
                m.SetVector("_YPSource", new Vector4(0, lod, 1, 0));
                Graphics.SetRenderTarget(rt);
                FullscreenQuad(m, PassLatLong);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                var pixels = read.GetPixels();
                if (!flipRows) return pixels;
                var flipped = new Color[pixels.Length];
                for (int y = 0; y < h; y++) Array.Copy(pixels, y * w, flipped, (h - 1 - y) * w, w);
                return flipped;
            }
            finally { RenderTexture.active = active; RenderTexture.ReleaseTemporary(rt); Object.DestroyImmediate(read); }
        }

        /// <summary>
        /// 向きの確かめ（セッションで 1 回）: 向き d に d × 0.5 + 0.5 の色を持つ模様をキューブに焼き、緯度経度に写して読み戻し、面の v と読み戻しの
        /// 行の向きの 4 通りのうち、決まった点の色が合うものを選ぶ。どれも合わなければ環境を使わない。
        /// </summary>
        void CheckOrientation()
        {
            const int size = 16, w = 16, h = 8;
            RenderTexture probe = null; var active = RenderTexture.active;
            try
            {
                probe = NewCube("YoluPainter environment orientation check", size);
                bake.SetVector("_YPSource", new Vector4(3, 0, 1, 0)); bake.SetVector("_YPRotation", new Vector4(1, 0, 0, 0));
                foreach (bool faceFlip in new[] { false, true })
                {
                    for (int face = 0; face < 6; face++) DrawFace(bake, probe, 0, face, PassBake, faceFlip);
                    foreach (bool readFlip in new[] { false, true })
                    {
                        var pixels = ReadLatLong(bake, probe, w, h, 0, readFlip);
                        if (MatchesPattern(pixels, w, h)) { s_faceFlip = faceFlip; s_readFlip = readFlip; return; }
                    }
                }
                s_orientationProblem = "The cube map orientation of this GPU could not be confirmed, so the environment is not used (it would be drawn turned or upside down).";
            }
            catch (Exception ex) { s_orientationProblem = "The environment could not be prepared on this GPU: " + ex.Message; }
            finally { RenderTexture.active = active; if (probe != null) { probe.Release(); Object.DestroyImmediate(probe); } }
        }

        static bool MatchesPattern(Color[] pixels, int w, int h)
        {
            for (int y = 1; y < h - 1; y++)
                for (int x = 0; x < w; x++)
                {
                    var d = FromLatLong((x + .5f) / w, (y + .5f) / h); var c = pixels[y * w + x];
                    if (Mathf.Abs(c.r - (d.x * .5f + .5f)) > .12f || Mathf.Abs(c.g - (d.y * .5f + .5f)) > .12f || Mathf.Abs(c.b - (d.z * .5f + .5f)) > .12f) return false;
                }
            return true;
        }

        // ───── 背景 ─────

        /// <summary>
        /// 背景の材料を今のカメラと設定に合わせる（描くのは呼ぶ側が画面いっぱいの四角で）。blur が 0 なら元を直に読み（内蔵の空は式のまま、
        /// テクスチャは 1 画素の角度に合った mip）、それ以外は畳み込んだキューブの mip を読む。
        /// </summary>
        public void PrepareBackground(PreviewSceneSettings s, Texture sourceTexture, Camera camera, float viewHeightPixels)
        {
            if (background == null || cube == null) return;
            bool useTexture = s.environment == PreviewEnvironmentSource.Texture && sourceTexture != null && Refusal(sourceTexture) == null;
            SetSource(background, s, useTexture ? sourceTexture : null);
            float directLod = 0;
            if (useTexture)
            {
                double pixelAngle = camera.fieldOfView * Mathf.Deg2Rad / Mathf.Max(1, viewHeightPixels);
                double texelAngle = sourceTexture is Cubemap ? Math.PI / 2 / sourceTexture.width : 2 * Math.PI / sourceTexture.width;
                directLod = Mathf.Max(0, (float)Math.Log(pixelAngle / texelAngle, 2));
            }
            background.SetVector("_YPSource", new Vector4(Kind(s, useTexture ? sourceTexture : null), 0, 1, 0));
            background.SetTexture("_YPEnvCube", cube);
            background.SetVector("_YPBackground", new Vector4(s.environmentBlur <= 0 ? -1 : s.environmentBlur * ReflectionSteps, s.environmentIntensity, directLod, 0));
            var viewProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix;
            background.SetMatrix("_YPInvViewProj", viewProjection.inverse);
        }

        public void Dispose()
        {
            foreach (var rt in new[] { source, cube }) if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
            source = null; cube = null;
            if (bake != null) Object.DestroyImmediate(bake);
            if (background != null) Object.DestroyImmediate(background);
            bake = null; background = null; bakedKey = null; ambientSourceKey = null; checkedShader = false; problem = null;
        }
    }
}
