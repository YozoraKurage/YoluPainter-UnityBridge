using System;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>カメラの向きのプリセット。Front は Unity のキャラクターが向く +Z の側から見る。</summary>
    public enum PreviewCameraView { Front, Back, Left, Right, Top }

    /// <summary>照明のプリセット。View・Left・Right・Rim・Above は今のカメラの向きから決める（決めた後は世界の向きで残る）。</summary>
    public enum PreviewLightPreset { Default, View, Left, Right, Rim, Above }

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

        /// <summary>値を正しい範囲に収める（壊れた・古いシリアライズ）。</summary>
        public PreviewSceneSettings Normalized()
        {
            if (float.IsNaN(lightYaw) || float.IsInfinity(lightYaw)) lightYaw = Default().lightYaw;
            if (float.IsNaN(lightPitch) || float.IsInfinity(lightPitch)) lightPitch = Default().lightPitch;
            lightYaw = Mathf.Repeat(lightYaw + 180, 360) - 180; lightPitch = Mathf.Clamp(lightPitch, -89, 89);
            intensity = float.IsNaN(intensity) || float.IsInfinity(intensity) ? 1 : Mathf.Clamp(intensity, 0, 4);
            return this;
        }
    }
}
