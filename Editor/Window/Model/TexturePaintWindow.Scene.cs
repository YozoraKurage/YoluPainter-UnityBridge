using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 3D ビューの擬似的なシーン: カメラのプリセット（前・後ろ・左・右・上、今の向きのままモデルに合わせる）と、照明（プリセット、光の向き・
    /// 高さ・強さ・色、環境光、背景の色）。設定はウィンドウの状態（SerializeField。.ylp には入らない）。3D ビューの見出しの光の印から開く小さな
    /// 窓（<see cref="PreviewScenePopup"/>）と、3D メニュー。中立の表示とマテリアルの表示の両方に効き、プレビューの中だけで、シーンのライトや
    /// RenderSettings には触れない。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] PreviewSceneSettings previewScene = PreviewSceneSettings.Default();
        internal PreviewSceneSettings PreviewScene => previewScene ?? (previewScene = PreviewSceneSettings.Default());

        void BindPreviewScene() { if (preview != null) preview.Scene = PreviewScene.Normalized(); }

        internal void ViewFrom(PreviewCameraView view) { preview.ViewFrom(view); Repaint(); }
        /// <summary>今の向きのまま、モデル全体が入るように置き直す。</summary>
        internal void FitView() { preview.ViewFrom(preview.CameraYaw, preview.CameraPitch); Repaint(); }
        internal void ApplyLightPreset(PreviewLightPreset preset) { PreviewScene.Apply(preset, preview.CameraYaw, preview.CameraPitch); BindPreviewScene(); Repaint(); }
        internal void ResetPreviewScene() { previewScene = PreviewSceneSettings.Default(); BindPreviewScene(); Repaint(); }

        void SceneMenuItems(GenericMenu m)
        {
            bool model = preview.HasModel;
            Item(m, L.Tr("Camera") + "/" + L.TrIn("camera", "Front"), () => ViewFrom(PreviewCameraView.Front), model);
            Item(m, L.Tr("Camera") + "/" + L.TrIn("camera", "Back"), () => ViewFrom(PreviewCameraView.Back), model);
            Item(m, L.Tr("Camera") + "/" + L.TrIn("camera", "Left"), () => ViewFrom(PreviewCameraView.Left), model);
            Item(m, L.Tr("Camera") + "/" + L.TrIn("camera", "Right"), () => ViewFrom(PreviewCameraView.Right), model);
            Item(m, L.Tr("Camera") + "/" + L.TrIn("camera", "Top"), () => ViewFrom(PreviewCameraView.Top), model);
            Item(m, L.Tr("Camera") + "/" + L.Tr("Fit to Model"), FitView, model);
            Item(m, L.Tr("Lighting") + "/" + L.TrIn("light", "Default"), () => ApplyLightPreset(PreviewLightPreset.Default));
            Item(m, L.Tr("Lighting") + "/" + L.Tr("From the View"), () => ApplyLightPreset(PreviewLightPreset.View));
            Item(m, L.Tr("Lighting") + "/" + L.Tr("From the Left"), () => ApplyLightPreset(PreviewLightPreset.Left));
            Item(m, L.Tr("Lighting") + "/" + L.Tr("From the Right"), () => ApplyLightPreset(PreviewLightPreset.Right));
            Item(m, L.Tr("Lighting") + "/" + L.Tr("From Behind (Rim)"), () => ApplyLightPreset(PreviewLightPreset.Rim));
            Item(m, L.Tr("Lighting") + "/" + L.Tr("From Above"), () => ApplyLightPreset(PreviewLightPreset.Above));
            Item(m, "Scene Settings…", () => OpenScenePopup(new Rect(surfaceRect.xMax - 300, surfaceRect.y, 0, 0)));
            Item(m, "Reset Scene", ResetPreviewScene);
        }

        void OpenScenePopup(Rect at) { if (!LayoutOverride.HasValue) PopupWindow.Show(at, new PreviewScenePopup(this)); }

        /// <summary>小さな窓の大きさ（左の列がカメラと照明、右の列が環境・影・トーンマッピング。Model/TexturePaintWindow.Display3D.cs）。</summary>
        internal const float ScenePanelWidth = 2 * SceneColumnWidth, ScenePanelHeight = 436, SceneColumnWidth = 280;

        /// <summary>擬似的なシーンの欄（小さな窓の中身。オフスクリーンの描画の試験もこれを描く）。</summary>
        internal void DrawScenePanel(Rect r)
        {
            PaintGui.Fill(r, PaintTheme.PanelBg);
            float column = Mathf.Min(SceneColumnWidth, r.width / 2);
            DrawDisplayColumn(new Rect(r.x + column, r.y, r.width - column, r.height));
            PaintGui.VLine(r.x + column, r.y + 8, r.yMax - 8, PaintTheme.Border);
            r = new Rect(r.x, r.y, column, r.height);
            var s = PreviewScene; var rows = new UiRows(r, 8);
            bool was = GUI.enabled; GUI.enabled = was && stroke == null;
            try
            {
                PaintGui.GroupLabel(rows.Row(16), L.Tr("Camera"));
                bool model = preview != null && preview.HasModel;
                var c = UiRows.Split(rows.Row(), 3);
                if (PaintGui.FitButton(c[0], L.TrIn("camera", "Front"), false, GUI.enabled && model, L.Tr("From the +Z side, where a Unity character faces"))) ViewFrom(PreviewCameraView.Front);
                if (PaintGui.FitButton(c[1], L.TrIn("camera", "Back"), false, GUI.enabled && model)) ViewFrom(PreviewCameraView.Back);
                if (PaintGui.FitButton(c[2], L.TrIn("camera", "Top"), false, GUI.enabled && model)) ViewFrom(PreviewCameraView.Top);
                c = UiRows.Split(rows.Row(), 3);
                if (PaintGui.FitButton(c[0], L.TrIn("camera", "Left"), false, GUI.enabled && model, L.Tr("From the character's left (−X)"))) ViewFrom(PreviewCameraView.Left);
                if (PaintGui.FitButton(c[1], L.TrIn("camera", "Right"), false, GUI.enabled && model, L.Tr("From the character's right (+X)"))) ViewFrom(PreviewCameraView.Right);
                if (PaintGui.FitButton(c[2], L.Tr("Fit to Model"), false, GUI.enabled && model, L.Tr("Keep the angle and frame the whole model"))) FitView();
                rows.Space(4);
                PaintGui.GroupLabel(rows.Row(16), L.Tr("Lighting"));
                var p = UiRows.Split(rows.Row(), 3);
                if (PaintGui.FitButton(p[0], L.TrIn("light", "View"), false, GUI.enabled, L.Tr("The light comes from the camera, a little from above"))) ApplyLightPreset(PreviewLightPreset.View);
                if (PaintGui.FitButton(p[1], L.TrIn("light", "Left"), false, GUI.enabled, L.Tr("From the Left"))) ApplyLightPreset(PreviewLightPreset.Left);
                if (PaintGui.FitButton(p[2], L.TrIn("light", "Right"), false, GUI.enabled, L.Tr("From the Right"))) ApplyLightPreset(PreviewLightPreset.Right);
                p = UiRows.Split(rows.Row(), 3);
                if (PaintGui.FitButton(p[0], L.TrIn("light", "Rim"), false, GUI.enabled, L.Tr("From behind the model, for the outline"))) ApplyLightPreset(PreviewLightPreset.Rim);
                if (PaintGui.FitButton(p[1], L.TrIn("light", "Above"), false, GUI.enabled, L.Tr("From Above"))) ApplyLightPreset(PreviewLightPreset.Above);
                if (PaintGui.FitButton(p[2], L.TrIn("light", "Default"), false, GUI.enabled, L.Tr("The light, its strength and the ambient back to the default"))) ApplyLightPreset(PreviewLightPreset.Default);
                float yaw = (float)PaintGui.KeepSlider(SceneSpot("scene.yaw", rows.Row()), L.Tr("Direction"), s.lightYaw, -180, 180, "0", "°", L.Tr("Where the light comes from, around the model (world)"));
                float pitch = (float)PaintGui.KeepSlider(SceneSpot("scene.pitch", rows.Row()), L.TrIn("light", "Height"), s.lightPitch, -10, 89, "0", "°", L.Tr("How high the light is (90° is straight above)"));
                float intensity = (float)PaintGui.KeepSlider(SceneSpot("scene.intensity", rows.Row()), L.Tr("Intensity"), s.intensity, 0, 2, "0.00", "", L.Tr("The strength of the light (1 is the default)"));
                if (yaw != s.lightYaw || pitch != s.lightPitch || intensity != s.intensity) { s.lightYaw = yaw; s.lightPitch = pitch; s.intensity = intensity; BindPreviewScene(); Repaint(); }
                SceneColor(rows, L.Tr("Light color"), s.lightColor, v => s.lightColor = v);
                SceneColor(rows, L.Tr("Ambient"), s.ambient, v => s.ambient = v, L.Tr("The light on the shaded side"));
                SceneColor(rows, L.Tr("Background"), s.background, v => s.background = v);
                rows.Space(2);
                if (PaintGui.FitButton(rows.Row(24), L.Tr("Reset Scene"), false, GUI.enabled, L.Tr("The light, the ambient, the background, the environment, the shadows and the tone mapping back to the default (the camera stays)"))) ResetPreviewScene();
                PaintGui.Text(rows.Row(18), L.Tr("Only the preview; the scene's lights are not changed."), PaintTheme.LabelSmall);
            }
            finally { GUI.enabled = was; }
        }

        void SceneColor(UiRows rows, string label, Color value, System.Action<Color> set, string tooltip = null)
        {
            var row = rows.Row();
            PaintGui.Text(new Rect(row.x, row.y, row.width - 70, row.height), PaintGui.Fit(label, row.width - 74, PaintTheme.Label), PaintTheme.Label);
            PaintGui.ColorSwatch(new Rect(row.xMax - 64, row.y + 2, 64, row.height - 4), value, v => { set(new Color(v.r, v.g, v.b, 1)); BindPreviewScene(); Repaint(); }, false, tooltip);
        }

        /// <summary>シーンの欄の部品の画面上の矩形（GUI モードの試験用）。</summary>
        internal readonly System.Collections.Generic.Dictionary<string, Rect> SceneControlRects = new System.Collections.Generic.Dictionary<string, Rect>();
        Rect SceneSpot(string id, Rect r) { if (Event.current.type == EventType.Repaint) SceneControlRects[id] = r; return r; }
    }

    /// <summary>3D ビューの見出しから開く、擬似的なシーンの小さな窓（中身は持ち主の <see cref="TexturePaintWindow.DrawScenePanel"/>）。</summary>
    internal sealed class PreviewScenePopup : PopupWindowContent
    {
        readonly TexturePaintWindow owner;
        public PreviewScenePopup(TexturePaintWindow owner) { this.owner = owner; }
        public override Vector2 GetWindowSize() => new Vector2(TexturePaintWindow.ScenePanelWidth, TexturePaintWindow.ScenePanelHeight);
        public override void OnGUI(Rect rect)
        {
            if (owner == null) { editorWindow.Close(); return; }
            if (Event.current.type == EventType.MouseMove) editorWindow.Repaint();
            owner.DrawScenePanel(rect);
        }
        public override void OnOpen() { editorWindow.wantsMouseMove = true; }
    }
}
