using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 3D ビューの環境・影・トーンマッピング（シーンの小さな窓の右の列）: 環境は無し（前の版と同じ一様な環境光）・内蔵の手続きの空（画像を
    /// 同梱しない）・Unity のプロジェクトのテクスチャ（Cubemap か緯度経度の Texture2D。GPU で読むだけで、読み取りの許可・取り込みの設定は
    /// 変えない）。テクスチャはアセットの GUID で覚え、消えていたら内蔵の空にして知らせる。環境の向き・明るさ・背景に映すか・背景のぼかし、
    /// 主な光からの自己の影（オン/オフと柔らかさ）、トーンマッピング（None・Neutral・ACES）と露出。どれも 3D ビューの絵だけで、テクスチャの値・
    /// 書き出し・2D キャンバス・シーンの RenderSettings とライトには触れない。設定は窓の状態（<see cref="PreviewSceneSettings"/>。.ylp には入らない）。
    /// 3D ビューで Ctrl+右ドラッグすると、環境と主な光を一緒に上の軸のまわりに回す（Substance は Shift+右ドラッグだが、ここではそれがパン）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        Texture environmentTexture; string environmentTextureFor;
        const int EnvironmentPickerId = 0x59500030;
        bool environmentPickerPending;
        string shownDisplayNote;

        /// <summary>環境のテクスチャ（設定の GUID から読む。覚えたものを使い、GUID が変わったか消えたときだけ探し直す）。消えていれば内蔵の空にして知らせる。</summary>
        internal Texture EnvironmentTexture
        {
            get
            {
                var s = PreviewScene;
                if (string.IsNullOrEmpty(s.environmentTexture)) { environmentTexture = null; environmentTextureFor = null; return null; }
                if (environmentTexture != null && environmentTextureFor == s.environmentTexture) return environmentTexture;
                string path = AssetDatabase.GUIDToAssetPath(s.environmentTexture);
                environmentTexture = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Texture>(path);
                environmentTextureFor = s.environmentTexture;
                return environmentTexture;
            }
        }

        /// <summary>描く前に: 環境のテクスチャをプレビューに渡す（消えていれば内蔵の空にして知らせる）。</summary>
        internal void SyncEnvironment()
        {
            if (preview == null) return;
            var s = PreviewScene;
            var texture = s.environment == PreviewEnvironmentSource.Texture ? EnvironmentTexture : null;
            if (s.environment == PreviewEnvironmentSource.Texture && texture == null)
            {
                s.environment = PreviewEnvironmentSource.Sky;
                message = string.IsNullOrEmpty(s.environmentTexture) ? L.Tr("No environment texture; the built-in sky is shown.")
                    : L.Tr("The environment texture is gone (deleted or moved out of the project), so the built-in sky is shown.");
                BindPreviewScene(); repaintPixels = true;
            }
            preview.EnvironmentTexture = texture;
        }

        /// <summary>描いた後: 頼んだ環境・影・トーンマッピングを見せられなかった理由が変わったら知らせる。</summary>
        void NoteDisplayProblems()
        {
            string note = preview?.DisplayNote;
            if (note == shownDisplayNote) return;
            shownDisplayNote = note;
            if (note != null) message = note;
        }

        /// <summary>環境のテクスチャを選ぶ（Cubemap か Texture2D のアセット。それ以外は断る）。</summary>
        internal bool UseEnvironmentTexture(Texture texture)
        {
            if (stroke != null) { message = L.Tr("A stroke is in progress."); return false; }
            string refusal = EnvironmentTextureRefusal(texture);
            if (refusal != null) { message = refusal; return false; }
            var s = PreviewScene;
            s.environmentTexture = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(texture)); s.environment = PreviewEnvironmentSource.Texture;
            environmentTexture = texture; environmentTextureFor = s.environmentTexture;
            message = L.Tr("Environment: {0}", texture.name) + (PreviewEnvironment.AspectNote(texture) is string aspect ? " " + aspect : "");
            BindPreviewScene(); Repaint();
            return true;
        }

        /// <summary>環境に使えないテクスチャの理由（使えれば null）: アセットの Cubemap か 2D のテクスチャ（メインのアセット）だけ。</summary>
        internal static string EnvironmentTextureRefusal(Object candidate)
        {
            if (candidate == null) return L.Tr("No texture.");
            if (!(candidate is Texture texture)) return L.Tr("{0} is a {1}, not a texture.", candidate.name, candidate.GetType().Name);
            string refused = PreviewEnvironment.Refusal(texture);
            if (refused != null) return refused;
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsMainAsset(texture)) return L.Tr("{0} is not a texture asset of its own in the project (only such an asset can be found again by its GUID).", texture.name);
            return null;
        }

        /// <summary>主な窓の OnGUI から: 環境のテクスチャを選ぶ窓を開く（小さな窓から頼まれたら。その窓は選ぶ窓が開くと閉じるので）と、その結果。</summary>
        void HandleEnvironmentPicker(Event e)
        {
            if (environmentPickerPending && e.type == EventType.Repaint)
            {
                environmentPickerPending = false;
                EditorGUIUtility.ShowObjectPicker<Texture>(EnvironmentTexture, false, "t:Cubemap t:Texture2D", EnvironmentPickerId);
            }
            if (e.type != EventType.ExecuteCommand || EditorGUIUtility.GetObjectPickerControlID() != EnvironmentPickerId) return;
            if (e.commandName != "ObjectSelectorClosed" && e.commandName != "ObjectSelectorUpdated") return;
            if (EditorGUIUtility.GetObjectPickerObject() is Texture picked && picked != EnvironmentTexture) UseEnvironmentTexture(picked);
            e.Use();
        }

        // ───────── Ctrl+右ドラッグで照明を回す ─────────

        bool lightingDrag; int lightingDragControl; float lightingDragStartEnvironment, lightingDragStartYaw;
        internal bool IsRotatingLighting => lightingDrag;

        /// <summary>3D ビューで Ctrl（mac は Cmd）+ 右ドラッグ: 環境と主な光を上の軸のまわりに回す（横 1 画素で 0.5°）。Esc で始めた向きに戻す。</summary>
        bool HandleLightingDrag(Event e)
        {
            if (preview == null || !preview.HasModel) return false;
            int id = GUIUtility.GetControlID("YoluPainterLightingDrag".GetHashCode(), FocusType.Passive, surfaceRect);
            if (e.type == EventType.MouseDown && e.button == 1 && (e.control || e.command) && surfaceRect.Contains(e.mousePosition) && GUIUtility.hotControl == 0 && stroke == null)
            {
                lightingDrag = true; lightingDragControl = id; GUIUtility.hotControl = id;
                lightingDragStartEnvironment = PreviewScene.environmentRotation; lightingDragStartYaw = PreviewScene.lightYaw;
                e.Use(); return true;
            }
            if (!lightingDrag) return false;
            if (e.type == EventType.MouseDrag) { PreviewScene.RotateLighting(e.delta.x * .5f); BindPreviewScene(); e.Use(); Repaint(); return true; }
            if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp) { EndLightingDrag(false); e.Use(); return true; }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { EndLightingDrag(true); e.Use(); Repaint(); return true; }
            return false;
        }
        /// <summary>照明を回すドラッグを終える（revert なら始めた向きに戻す）。フォーカスを失ったときにも呼ぶ。</summary>
        void EndLightingDrag(bool revert)
        {
            if (!lightingDrag) return;
            if (revert) { PreviewScene.environmentRotation = lightingDragStartEnvironment; PreviewScene.lightYaw = lightingDragStartYaw; BindPreviewScene(); }
            lightingDrag = false;
            if (GUIUtility.hotControl == lightingDragControl) GUIUtility.hotControl = 0;
        }

        // ───────── シーンの小さな窓の右の列 ─────────

        void DrawDisplayColumn(Rect r)
        {
            var s = PreviewScene; var rows = new UiRows(r, 8);
            bool was = GUI.enabled; GUI.enabled = was && stroke == null;
            try
            {
                PaintGui.GroupLabel(rows.Row(16), L.Tr("Environment"));
                var e = UiRows.Split(rows.Row(), 3);
                if (PaintGui.FitButton(SceneSpot("scene.env.none", e[0]), L.TrIn("environment", "None"), s.environment == PreviewEnvironmentSource.None, GUI.enabled, L.Tr("A plain ambient light (the Ambient color), as before"))) SetEnvironment(PreviewEnvironmentSource.None);
                if (PaintGui.FitButton(SceneSpot("scene.env.sky", e[1]), L.Tr("Sky"), s.environment == PreviewEnvironmentSource.Sky, GUI.enabled, L.Tr("The built-in sky: a gradient from the zenith over the horizon to the ground (no image)"))) SetEnvironment(PreviewEnvironmentSource.Sky);
                if (PaintGui.FitButton(SceneSpot("scene.env.texture", e[2]), L.Tr("Texture"), s.environment == PreviewEnvironmentSource.Texture, GUI.enabled, L.Tr("A Cubemap or a latitude-longitude texture of the project (HDR is fine); read on the GPU, its import settings are not changed"))) { if (EnvironmentTexture != null) SetEnvironment(PreviewEnvironmentSource.Texture); else RequestEnvironmentPicker(); }
                if (s.environment == PreviewEnvironmentSource.Texture)
                {
                    var row = SceneSpot("scene.env.choose", rows.Row());
                    var current = EnvironmentTexture;
                    if (PaintGui.Button(row, PaintGui.Fit(current != null ? current.name : L.Tr("Choose…"), row.width - 40, PaintTheme.Label, false), false, GUI.enabled, L.Tr("Choose the environment texture (or drop a texture here)"), "texture")) RequestEnvironmentPicker();
                    HandleEnvironmentDrop(row);
                }
                else if (s.environment == PreviewEnvironmentSource.Sky)
                {
                    SceneColor(rows, L.Tr("Zenith"), s.skyZenith, v => s.skyZenith = v);
                    SceneColor(rows, L.Tr("Horizon"), s.skyHorizon, v => s.skyHorizon = v);
                    SceneColor(rows, L.Tr("Ground"), s.skyGround, v => s.skyGround = v);
                }
                bool env = s.environment != PreviewEnvironmentSource.None;
                float rotation = (float)PaintGui.KeepSlider(SceneSpot("scene.env.rotation", rows.Row()), L.Tr("Rotation"), s.environmentRotation, -180, 180, "0", "°", L.Tr("Turns the environment around the up axis (Ctrl + right drag in the 3D view turns it with the light)"), GUI.enabled && env);
                float intensity = (float)PaintGui.KeepSlider(SceneSpot("scene.env.intensity", rows.Row()), L.Tr("Brightness"), s.environmentIntensity, 0, 4, "0.00", "", L.Tr("The strength of the environment's light, reflections and background (1 is the default)"), GUI.enabled && env);
                bool background = PaintGui.Toggle(SceneSpot("scene.env.background", rows.Row()), L.Tr("Show as the background"), s.environmentBackground, L.Tr("Off: the background color"), GUI.enabled && env);
                float blur = (float)PaintGui.KeepSlider(SceneSpot("scene.env.blur", rows.Row()), L.Tr("Background blur"), s.environmentBlur, 0, 1, "0", "%", null, GUI.enabled && env && s.environmentBackground, 100);
                rows.Space(4);
                PaintGui.GroupLabel(rows.Row(16), L.Tr("Shadows"));
                bool shadows = PaintGui.Toggle(SceneSpot("scene.shadows", rows.Row()), L.Tr("Self-shadowing from the light"), s.shadows, L.Tr("The model shadows itself from the main light (a shadow map of the preview only; Unity's quality settings are not used or changed)"));
                float softness = (float)PaintGui.KeepSlider(SceneSpot("scene.shadows.softness", rows.Row()), L.Tr("Softness"), s.shadowSoftness, 0, 1, "0", "%", L.Tr("How soft the shadow edges are"), GUI.enabled && s.shadows, 100);
                rows.Space(4);
                PaintGui.GroupLabel(rows.Row(16), L.Tr("Tone mapping"));
                var t = UiRows.Split(rows.Row(), 3);
                if (PaintGui.FitButton(SceneSpot("scene.tone.none", t[0]), L.TrIn("tone mapping", "None"), s.toneMapping == PreviewToneMapping.None, GUI.enabled, L.Tr("Values above 1 are clipped, as before"))) SetToneMapping(PreviewToneMapping.None);
                if (PaintGui.FitButton(SceneSpot("scene.tone.neutral", t[1]), "Neutral", s.toneMapping == PreviewToneMapping.Neutral, GUI.enabled, L.Tr("Unity's Neutral curve: bright light rolls off with little change to the colors"))) SetToneMapping(PreviewToneMapping.Neutral);
                if (PaintGui.FitButton(SceneSpot("scene.tone.aces", t[2]), "ACES", s.toneMapping == PreviewToneMapping.Aces, GUI.enabled, L.Tr("The ACES filmic curve (more contrast and saturation)"))) SetToneMapping(PreviewToneMapping.Aces);
                float exposure = (float)PaintGui.KeepSlider(SceneSpot("scene.exposure", rows.Row()), L.Tr("Exposure"), s.exposure, -4, 4, "0.0", " EV", L.Tr("Brightens or darkens the 3D picture only (+1 doubles it); the textures and the export are not changed"));
                if (rotation != s.environmentRotation || intensity != s.environmentIntensity || background != s.environmentBackground || blur != s.environmentBlur || shadows != s.shadows || softness != s.shadowSoftness || exposure != s.exposure)
                {
                    s.environmentRotation = rotation; s.environmentIntensity = intensity; s.environmentBackground = background; s.environmentBlur = blur;
                    s.shadows = shadows; s.shadowSoftness = softness; s.exposure = exposure;
                    BindPreviewScene(); Repaint();
                }
                string note = preview?.DisplayNote;
                if (note != null) PaintGui.Notice(rows, note, "warning", PaintTheme.Warning);
            }
            finally { GUI.enabled = was; }
        }

        internal void SetEnvironment(PreviewEnvironmentSource source) { PreviewScene.environment = source; BindPreviewScene(); Repaint(); }
        internal void SetToneMapping(PreviewToneMapping mapping) { PreviewScene.toneMapping = mapping; BindPreviewScene(); Repaint(); }

        void RequestEnvironmentPicker() { environmentPickerPending = true; Repaint(); }

        void HandleEnvironmentDrop(Rect row)
        {
            var e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !row.Contains(e.mousePosition) || !GUI.enabled) return;
            var dropped = DragAndDrop.objectReferences.FirstOrDefault();
            DragAndDrop.visualMode = EnvironmentTextureRefusal(dropped) == null ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); UseEnvironmentTexture(dropped as Texture); if (!(dropped is Texture)) message = EnvironmentTextureRefusal(dropped); }
            e.Use();
        }
    }
}
