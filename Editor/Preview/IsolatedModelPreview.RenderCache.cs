using System;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>
    /// 3D ビューの絵を「変わったときだけ」描く。絵に効く入力を 1 か所（<see cref="ComputeRenderKey"/>）で鍵にまとめ、鍵が前の描画と同じなら
    /// 前の絵（PreviewRenderUtility の描き先。中身はほかに誰も書かない）を返すだけにする。窓の Repaint は 3D の描画とは別に何度でも来る
    /// （ほかの窓の操作・マウスの移動・シーンビューの描き直しにつられることもある）ので、Repaint ごとに描いていたときはそれだけ GPU を使っていた。
    /// 入力が変わり続けても、描くのは 1 秒あたり <see cref="FrameRateLimit"/> 回まで。上限で描かなかった変更は <see cref="WantsRepaint"/> が
    /// 窓に知らせ、次の機会に必ず描く。マテリアル表示の複製のシェーダーがコンパイル中のあいだは 0.25 秒ごとに、終わったら 1 回描き直す。
    /// </summary>
    public sealed partial class IsolatedModelPreview
    {
        /// <summary>3D の絵を描き直す入力（どれかが変われば描く）。</summary>
        internal readonly struct RenderKey : IEquatable<RenderKey>
        {
            public readonly int PixelWidth, PixelHeight, Revision; public readonly long Content, CompileTick;
            public readonly Vector3 CameraPosition; public readonly Quaternion CameraRotation; public readonly float FieldOfView, Near, Far;
            public readonly PreviewShading Shading; public readonly bool Lit; public readonly int Scene;
            public readonly MirrorPlane? Plane; public readonly ShapeGradientOverlay? Shape;
            public readonly bool Region; public readonly long RegionKey; public readonly Color RegionColor;
            public readonly (int, int, int)? IdHighlight;

            public RenderKey(int pixelWidth, int pixelHeight, int revision, long content, long compileTick, Camera camera, PreviewShading shading, bool lit, int scene,
                MirrorPlane? plane, ShapeGradientOverlay? shape, bool region, long regionKey, Color regionColor, (int, int, int)? idHighlight)
            {
                PixelWidth = pixelWidth; PixelHeight = pixelHeight; Revision = revision; Content = content; CompileTick = compileTick;
                CameraPosition = camera.transform.position; CameraRotation = camera.transform.rotation; FieldOfView = camera.fieldOfView; Near = camera.nearClipPlane; Far = camera.farClipPlane;
                Shading = shading; Lit = lit; Scene = scene; Plane = plane; Shape = shape; Region = region; RegionKey = regionKey; RegionColor = regionColor; IdHighlight = idHighlight;
            }

            public bool Equals(RenderKey o) => PixelWidth == o.PixelWidth && PixelHeight == o.PixelHeight && Revision == o.Revision && Content == o.Content && CompileTick == o.CompileTick
                && CameraPosition == o.CameraPosition && CameraRotation == o.CameraRotation && FieldOfView == o.FieldOfView && Near == o.Near && Far == o.Far
                && Shading == o.Shading && Lit == o.Lit && Scene == o.Scene && Nullable.Equals(Plane, o.Plane) && Nullable.Equals(Shape, o.Shape)
                && Region == o.Region && RegionKey == o.RegionKey && RegionColor == o.RegionColor && Nullable.Equals(IdHighlight, o.IdHighlight);
            public override bool Equals(object obj) => obj is RenderKey other && Equals(other);
            public override int GetHashCode() => unchecked(PixelWidth * 31 + PixelHeight * 17 + Revision * 13 + (int)Content * 7 + CameraPosition.GetHashCode());
        }

        /// <summary>試験用・計測用の時計（秒）。既定はエディタの時刻。</summary>
        internal Func<double> Clock = () => EditorApplication.timeSinceStartup;
        /// <summary>1 秒あたりに 3D を描く回数の上限（個人の設定 Preview frame rate。0 は上限なし）。</summary>
        public int FrameRateLimit { get; set; } = 60;
        /// <summary>実際に 3D を描いた回数（前の絵を貼っただけの回は数えない）。</summary>
        public int RenderCount { get; private set; }
        /// <summary>前の描画の後に、上限のために描かなかった変更がある。</summary>
        internal bool RenderDeferred => deferred;
        /// <summary>中身が変わったことを知らせる（渡したテクスチャの中身を、渡し直さずに書き換えたとき）。</summary>
        public void InvalidateRender() => contentVersion++;

        /// <summary>中身の版: 渡すテクスチャ・描き方・マテリアル表示の中身・モデルを変える口で増やす。</summary>
        long contentVersion;
        RenderKey lastKey; bool hasPicture, deferred, renderedWhileCompiling; double lastRenderTime = double.NegativeInfinity;
        /// <summary>前に描いた絵（PreviewRenderUtility の描き先か、トーンマッピングを当てた 8 bit の描き先）。</summary>
        Texture lastPicture;
        /// <summary>コンパイル中に描き直す間隔（秒）。</summary>
        internal const double CompileRedrawInterval = .25;

        /// <summary>今の入力の鍵（rect は GUI の点、pixelsPerPoint で画素に。カメラは rect に合わせてから読む）。</summary>
        internal RenderKey ComputeRenderKey(Rect rect, float pixelsPerPoint)
        {
            UpdateCamera(rect);
            var s = Scene ?? defaultScene;
            int scene = unchecked(((s.lightYaw.GetHashCode() * 31 + s.lightPitch.GetHashCode()) * 31 + s.intensity.GetHashCode()) * 31
                + (s.lightColor.GetHashCode() * 31 + s.ambient.GetHashCode()) * 17 + s.background.GetHashCode());
            // 環境・影・トーンマッピング（IsolatedModelPreview.Display.cs）と、環境のテクスチャの中身（取り込み直しで updateCount が進む）
            var environmentTexture = s.environment == PreviewEnvironmentSource.Texture ? EnvironmentTexture : null;
            scene = unchecked(scene * 31 + ((((int)s.environment * 31 + (s.environmentTexture ?? "").GetHashCode()) * 31 + s.environmentRotation.GetHashCode()) * 31
                + s.environmentIntensity.GetHashCode()) * 31 + (s.environmentBackground ? 1 : 0) + s.environmentBlur.GetHashCode() * 7);
            scene = unchecked(scene * 31 + (s.skyZenith.GetHashCode() * 31 + s.skyHorizon.GetHashCode()) * 31 + s.skyGround.GetHashCode());
            scene = unchecked(scene * 31 + ((s.shadows ? 1 : 0) * 31 + s.shadowSoftness.GetHashCode()) * 31 + (int)s.toneMapping * 7 + s.exposure.GetHashCode());
            if (environmentTexture != null) scene = unchecked(scene * 31 + environmentTexture.GetInstanceID() * 7 + (int)environmentTexture.updateCount);
            bool compiling = CompilingShaders;
            long compileTick = compiling ? 1 + (long)Math.Floor(Clock() / CompileRedrawInterval) : 0;
            bool region = regionWanted && HasModel && ReferenceEquals(regionGeometry, geometry) && regionTriangles != null && regionTriangles.Count > 0;
            return new RenderKey((int)(rect.width * pixelsPerPoint), (int)(rect.height * pixelsPerPoint), revision, contentVersion, compileTick, preview.camera,
                shading, LitPreview, scene, ShownSymmetryPlane.HasValue && HasModel ? ShownSymmetryPlane : null, ShownShapeGradient.HasValue && HasModel ? ShownShapeGradient : null,
                region, region ? regionKey : 0, region ? regionColor : default, region ? IdHighlightInput : null);
        }

        /// <summary>
        /// 3D の絵（rect の大きさの描き先）を返す。鍵が前と同じなら描かずに前の絵。変わっていても、前の描画から 1 / 上限 秒たっていなければ
        /// 前の絵を返して <see cref="RenderDeferred"/> を立てる（大きさが変わったときは待たずに描く）。
        /// </summary>
        internal Texture RenderCached(Rect rect, float pixelsPerPoint)
        {
            ThrowIfDisposed();
            if (!HasSnapshot || rect.width < 2 || rect.height < 2) return null;
            EnsurePreview();
            var key = ComputeRenderKey(rect, pixelsPerPoint);
            var picture = lastPicture;
            bool old = hasPicture && picture != null && (!(picture is RenderTexture rt) || rt.IsCreated());
            if (old && key.Equals(lastKey)) { deferred = false; return picture; }
            double now = Clock();
            bool sameSize = key.PixelWidth == lastKey.PixelWidth && key.PixelHeight == lastKey.PixelHeight; // 大きさが変わったら待たない（引き伸ばした絵を見せない）
            if (old && sameSize && FrameRateLimit > 0 && now - lastRenderTime < 1.0 / FrameRateLimit) { deferred = true; return picture; }
            var texture = DrawPreview(rect);
            lastKey = key; lastPicture = texture; hasPicture = texture != null; deferred = false; lastRenderTime = now; renderedWhileCompiling = key.CompileTick != 0;
            RenderCount++;
            return texture;
        }

        /// <summary>
        /// 入力のイベントが無くても 3D を描き直したいか（窓の Tick が Repaint を頼む）: 上限で描かなかった変更の時刻が来た、マテリアル表示の
        /// 複製のコンパイル中に間隔がたった、コンパイルが終わった。3D ビューが見えていないときは呼ぶ側が頼まない。
        /// </summary>
        public bool WantsRepaint()
        {
            if (disposed || !hasPicture || !HasModel) return false;
            double now = Clock();
            if (deferred && (FrameRateLimit <= 0 || now - lastRenderTime >= 1.0 / FrameRateLimit)) return true;
            if (renderedWhileCompiling && (!CompilingShaders || now - lastRenderTime >= CompileRedrawInterval)) return true;
            return false;
        }

        /// <summary>前の絵を使わせない（同じ描き先に別の大きさで描いた後など）。</summary>
        void ForgetRenderedPicture() { hasPicture = false; deferred = false; lastPicture = null; }
    }
}
