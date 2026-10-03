using System;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>カメラの向きのプリセット。Front は Unity のキャラクターが向く +Z の側から見る。</summary>
    public enum PreviewCameraView { Front, Back, Left, Right, Top }

    /// <summary>照明のプリセット。View・Left・Right・Rim・Above は今のカメラの向きから決める（決めた後は世界の向きで残る）。</summary>
    public enum PreviewLightPreset { Default, View, Left, Right, Rim, Above }

    /// <summary>3D ビューの環境: None は前の版と同じ一様な環境光、Sky は内蔵の手続きの空（天頂・地平線・地面の色の勾配。画像は使わない）、
    /// Texture は Unity のプロジェクトのテクスチャ（Cubemap か緯度経度の Texture2D。HDR でもよい）。</summary>
    public enum PreviewEnvironmentSource { None, Sky, Texture }

    /// <summary>3D ビューの絵だけに当てるトーンマッピング（テクスチャの値・書き出し・2D には当てない）。None は 0〜1 で切るだけ（前の版と同じ）。
    /// Neutral と ACES は Unity の後処理（URP・Post Processing）と同じ名前の曲線。</summary>
    public enum PreviewToneMapping { None, Neutral, Aces }

    /// <summary>
    /// 3D ビューの擬似的なシーン: 主な光の向き（世界の空間で、光の来る向き = 方位 yaw と高さ pitch）・強さ・色、環境光の色、背景の色。
    /// 中立の表示（PreviewSurface.shader）とマテリアルの表示（PreviewRenderUtility の光と環境光）の両方に効く。プレビューの中だけで、
    /// シーンのライトや RenderSettings には触れない。既定は前の版の中立の表示と同じ照明（光の向き (−0.3, 0.65, −0.7)、0.35 + 0.65 × 明るさ）。
    /// </summary>
    [Serializable]
    public sealed class PreviewSceneSettings
    {
        /// <summary>既定の光の来る向き（前の版の中立のシェーダーの固定の向き (−0.3, 0.65, −0.7)）。</summary>
        public static readonly Vector3 DefaultDirection = new Vector3(-0.3f, 0.65f, -0.7f).normalized;
        public static readonly Color DefaultBackground = new Color(0.12f, 0.13f, 0.15f, 1);

        public float lightYaw = Mathf.Atan2(DefaultDirection.x, DefaultDirection.z) * Mathf.Rad2Deg;
        public float lightPitch = Mathf.Asin(DefaultDirection.y) * Mathf.Rad2Deg;
        /// <summary>主な光の強さ（1 が既定）。</summary>
        public float intensity = 1;
        public Color lightColor = Color.white;
        /// <summary>環境光（灰色 0.5 が既定。中立の表示では 0.7 倍が影の側の明るさ = 0.35、マテリアルの表示では 0.4 倍が環境光）。</summary>
        public Color ambient = new Color(.5f, .5f, .5f, 1);
        public Color background = DefaultBackground;

        // ── 環境（HDRI）。既定は None（前の版と同じ絵） ──
        public PreviewEnvironmentSource environment = PreviewEnvironmentSource.None;
        /// <summary>環境のテクスチャのアセットの GUID（Texture のときだけ使う。消えていたら内蔵の空で描いて知らせる）。</summary>
        public string environmentTexture = "";
        /// <summary>環境の向き（上の軸のまわりの度。世界の空間）。</summary>
        public float environmentRotation;
        /// <summary>環境の明るさ（拡散と映り込みと背景に掛ける。1 が既定）。</summary>
        public float environmentIntensity = 1;
        /// <summary>背景に環境を映すか（false なら背景の色）。</summary>
        public bool environmentBackground = true;
        /// <summary>背景に映す環境のぼかし（0〜1。0 は元の解像度）。</summary>
        public float environmentBlur = .3f;
        /// <summary>内蔵の空の色（天頂・地平線・地面）。</summary>
        public Color skyZenith = DefaultSkyZenith, skyHorizon = DefaultSkyHorizon, skyGround = DefaultSkyGround;
        public static readonly Color DefaultSkyZenith = new Color(.42f, .55f, .75f, 1), DefaultSkyHorizon = new Color(.86f, .87f, .88f, 1), DefaultSkyGround = new Color(.24f, .23f, .22f, 1);

        // ── 影（主な光からモデル自身への影）。既定はオフ ──
        public bool shadows;
        /// <summary>影の柔らかさ（0〜1。0 は影のマップの 1 画素ぶん、1 はモデルの大きさの約 4%）。</summary>
        public float shadowSoftness = .25f;

        // ── トーンマッピングと露出（3D ビューの絵だけ）。既定は None・0（前の版と同じ絵） ──
        public PreviewToneMapping toneMapping = PreviewToneMapping.None;
        /// <summary>露出（EV。+1 で 2 倍の明るさ）。</summary>
        public float exposure;

        /// <summary>トーンマッピングか露出を当てるか（当てないときは前の版と同じ 8 bit の描き先で描く）。</summary>
        public bool UsesToneMapping => toneMapping != PreviewToneMapping.None || Mathf.Abs(exposure) > 1e-4f;

        public static PreviewSceneSettings Default() => new PreviewSceneSettings();
        public PreviewSceneSettings Clone() => (PreviewSceneSettings)MemberwiseClone();

        /// <summary>光の来る向き（世界の空間の単位ベクトル）。</summary>
        public Vector3 LightDirection
        {
            get
            {
                float y = lightYaw * Mathf.Deg2Rad, p = lightPitch * Mathf.Deg2Rad;
                return new Vector3(Mathf.Sin(y) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Cos(y) * Mathf.Cos(p));
            }
        }

        /// <summary>プリセットを当てる（cameraYaw・cameraPitch は今のカメラ。Default は全部を既定に戻す）。</summary>
        public void Apply(PreviewLightPreset preset, float cameraYaw, float cameraPitch)
        {
            if (preset == PreviewLightPreset.Default) { var d = Default(); lightYaw = d.lightYaw; lightPitch = d.lightPitch; intensity = d.intensity; lightColor = d.lightColor; ambient = d.ambient; return; }
            // カメラの向き f = (sin cy cos cp, −sin cp, cos cy cos cp)、右 r = (cos cy, 0, −sin cy)。光の来る向きを yaw・pitch で表す
            switch (preset)
            {
                case PreviewLightPreset.View: lightYaw = cameraYaw + 180 + 25; lightPitch = Mathf.Clamp(cameraPitch + 25, -10, 85); break;
                case PreviewLightPreset.Left: lightYaw = cameraYaw - 90; lightPitch = 30; break;
                case PreviewLightPreset.Right: lightYaw = cameraYaw + 90; lightPitch = 30; break;
                case PreviewLightPreset.Rim: lightYaw = cameraYaw; lightPitch = 30; break;
                case PreviewLightPreset.Above: lightYaw = cameraYaw + 180; lightPitch = 80; break;
            }
            lightYaw = Mathf.Repeat(lightYaw + 180, 360) - 180;
        }

        /// <summary>カメラのプリセットの向き（yaw, pitch）。カメラは向き f の逆の側に置く（IsolatedModelPreview と同じ）。</summary>
        public static (float yaw, float pitch) CameraAngles(PreviewCameraView view)
        {
            switch (view)
            {
                case PreviewCameraView.Front: return (180, 0);
                case PreviewCameraView.Back: return (0, 0);
                case PreviewCameraView.Left: return (90, 0);
                case PreviewCameraView.Right: return (-90, 0);
                default: return (180, 89);
            }
        }

        static float Finite(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;

        /// <summary>照明を上の軸のまわりに回す（環境と主な光を一緒に。3D ビューの Ctrl+右ドラッグ）。</summary>
        public void RotateLighting(float degrees)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees)) return;
            environmentRotation = Mathf.Repeat(environmentRotation + degrees + 180, 360) - 180;
            lightYaw = Mathf.Repeat(lightYaw + degrees + 180, 360) - 180;
        }

        /// <summary>値を正しい範囲に収める（壊れた・古いシリアライズ）。</summary>
        public PreviewSceneSettings Normalized()
        {
            if (float.IsNaN(lightYaw) || float.IsInfinity(lightYaw)) lightYaw = Default().lightYaw;
            if (float.IsNaN(lightPitch) || float.IsInfinity(lightPitch)) lightPitch = Default().lightPitch;
            lightYaw = Mathf.Repeat(lightYaw + 180, 360) - 180; lightPitch = Mathf.Clamp(lightPitch, -89, 89);
            intensity = float.IsNaN(intensity) || float.IsInfinity(intensity) ? 1 : Mathf.Clamp(intensity, 0, 4);
            if (!Enum.IsDefined(typeof(PreviewEnvironmentSource), environment)) environment = PreviewEnvironmentSource.None;
            if (environmentTexture == null) environmentTexture = "";
            environmentRotation = Finite(environmentRotation, 0); environmentRotation = Mathf.Repeat(environmentRotation + 180, 360) - 180;
            environmentIntensity = Mathf.Clamp(Finite(environmentIntensity, 1), 0, 8);
            environmentBlur = Mathf.Clamp01(Finite(environmentBlur, .3f));
            shadowSoftness = Mathf.Clamp01(Finite(shadowSoftness, .25f));
            if (!Enum.IsDefined(typeof(PreviewToneMapping), toneMapping)) toneMapping = PreviewToneMapping.None;
            exposure = Mathf.Clamp(Finite(exposure, 0), -6, 6);
            return this;
        }
    }
}
