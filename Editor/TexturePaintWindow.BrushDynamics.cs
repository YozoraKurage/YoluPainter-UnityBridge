using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>ブラシのカラーダイナミクス・デュアルブラシ・フェード・傾き・紙の質感の選択 の欄と、その brush.json（schema 3）。
    /// ダブごとの色・デュアルブラシ・フェード・傾きは 2D キャンバスのストロークに効く（3D ビューのブラシは面の上のダブで描くので、
    /// 色はストロークごとの 1 色になり、ほかは効かない）。</summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>schema 3 で足した項目。無い版のファイルは既定値（どれも以前と同じに描く値）で読む。</summary>
        internal sealed partial class BrushState
        {
            public Color secondaryColor = Color.black;
            public float fgBgJitter, hueJitter, saturationJitter, brightnessJitter, purity;
            public bool colorPerTip = true;
            public bool dualEnabled; public string dualTipId = "";
            public float dualRadius = 8, dualHardness = 1, dualSpacing = .25f, dualAngle, dualRoundness = 1, dualScatter;
            public int dualCount = 1, dualMode;
            public int fadeSize, fadeOpacity, fadeFlow;
            public bool tiltSize, tiltOpacity, tiltFlow, tiltAngle;
        }
        bool showColorDynamics, showDualBrush, showFadeTilt;

        static void UpgradeBrushState(BrushState b)
        {
            if (b.schema >= 3) return;
            b.secondaryColor = Color.black; b.fgBgJitter = b.hueJitter = b.saturationJitter = b.brightnessJitter = b.purity = 0; b.colorPerTip = true;
            b.dualEnabled = false; b.dualTipId = ""; b.dualRadius = 8; b.dualHardness = 1; b.dualSpacing = .25f; b.dualAngle = 0; b.dualRoundness = 1; b.dualScatter = 0; b.dualCount = 1; b.dualMode = 0;
            b.fadeSize = b.fadeOpacity = b.fadeFlow = 0; b.tiltSize = b.tiltOpacity = b.tiltFlow = b.tiltAngle = false;
            b.schema = 3;
        }
        static Rgba32 ToRgba(Color c) => new Rgba32((byte)Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255), (byte)Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255), (byte)Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255), (byte)Mathf.RoundToInt(Mathf.Clamp01(c.a) * 255));

        /// <summary>GetBrush の続き: ウィンドウの設定からダイナミクスを入れる。</summary>
        void ApplyBrushDynamics(BrushSettings s)
        {
            s.SecondaryColor = ToRgba(brush.secondaryColor); s.ForegroundBackgroundJitter = brush.fgBgJitter; s.HueJitter = brush.hueJitter;
            s.SaturationJitter = brush.saturationJitter; s.BrightnessJitter = brush.brightnessJitter; s.Purity = brush.purity; s.ColorPerTip = brush.colorPerTip;
            s.FadeSize = brush.fadeSize; s.FadeOpacity = brush.fadeOpacity; s.FadeFlow = brush.fadeFlow;
            s.TiltSize = brush.tiltSize; s.TiltOpacity = brush.tiltOpacity; s.TiltFlow = brush.tiltFlow; s.TiltAngle = brush.tiltAngle;
            s.Dual = !brush.dualEnabled ? null : new DualBrush { Tip = BrushTips.Resolve(brush.dualTipId), Radius = brush.dualRadius, Hardness = brush.dualHardness, Spacing = brush.dualSpacing,
                Angle = brush.dualAngle, Roundness = brush.dualRoundness, Scatter = brush.dualScatter, Count = brush.dualCount, Mode = (DualBrushMode)brush.dualMode };
        }
        /// <summary>ApplyPreset の続き: プリセットのダイナミクスを写す。背景色は色と同じく描き手のものとして残す。</summary>
        void CopyPresetDynamics(BrushSettings s, Color secondary)
        {
            brush.secondaryColor = secondary; brush.fgBgJitter = (float)s.ForegroundBackgroundJitter; brush.hueJitter = (float)s.HueJitter;
            brush.saturationJitter = (float)s.SaturationJitter; brush.brightnessJitter = (float)s.BrightnessJitter; brush.purity = (float)s.Purity; brush.colorPerTip = s.ColorPerTip;
            brush.fadeSize = s.FadeSize; brush.fadeOpacity = s.FadeOpacity; brush.fadeFlow = s.FadeFlow;
            brush.tiltSize = s.TiltSize; brush.tiltOpacity = s.TiltOpacity; brush.tiltFlow = s.TiltFlow; brush.tiltAngle = s.TiltAngle;
            var d = s.Dual; brush.dualEnabled = d != null;
            if (d != null)
            {
                brush.dualTipId = BrushTips.IdOf(d.Tip); brush.dualRadius = (float)d.Radius; brush.dualHardness = (float)d.Hardness; brush.dualSpacing = (float)d.Spacing;
                brush.dualAngle = (float)d.Angle; brush.dualRoundness = (float)d.Roundness; brush.dualScatter = (float)d.Scatter; brush.dualCount = d.Count; brush.dualMode = (int)d.Mode;
            }
        }
        /// <summary>2D キャンバスの入力点。傾きは Unity の Event.tilt（直立からのラジアン、X・Y 軸ごと）で、画面の Y は下向きなので
        /// キャンバス（上向き）へ符号を返す。マウスは 0（直立）。</summary>
        BrushSample PenSample(double x, double y, float pressure)
        {
            var tilt = Event.current != null ? Event.current.tilt : Vector2.zero;
            if (float.IsNaN(tilt.x) || float.IsNaN(tilt.y) || float.IsInfinity(tilt.x) || float.IsInfinity(tilt.y)) tilt = Vector2.zero;
            return new BrushSample(x, y, pressure, EditorApplication.timeSinceStartup, tilt.x, -tilt.y);
        }

        void DrawBrushDynamics()
        {
            showColorDynamics = EditorGUILayout.Foldout(showColorDynamics, "Color dynamics", true);
            if (showColorDynamics)
            {
                if (EditingMask || !BrushSettings.CarriesColor(channel))
                    EditorGUILayout.HelpBox("Colour dynamics apply to the Color and Emission channels only. This " + (EditingMask ? "mask" : channel + " channel") + " is painted with the exact value.", MessageType.None);
                brush.secondaryColor = EditorGUILayout.ColorField(new GUIContent("Background", "The second colour for foreground/background jitter"), brush.secondaryColor);
                brush.fgBgJitter = EditorGUILayout.Slider(new GUIContent("Fg/Bg jitter", "Each dab mixes toward the background colour by a random amount up to this"), brush.fgBgJitter, 0, 1);
                brush.hueJitter = EditorGUILayout.Slider(new GUIContent("Hue jitter", "Up to ± this × 180°"), brush.hueJitter, 0, 1);
                brush.saturationJitter = EditorGUILayout.Slider(new GUIContent("Saturation jitter", "HSV saturation moves by up to ± this"), brush.saturationJitter, 0, 1);
                brush.brightnessJitter = EditorGUILayout.Slider(new GUIContent("Brightness jitter", "HSV value moves by up to ± this"), brush.brightnessJitter, 0, 1);
                brush.purity = EditorGUILayout.Slider(new GUIContent("Purity", "-1 grey … 0 unchanged … 1 fully saturated"), brush.purity, -1, 1);
                brush.colorPerTip = EditorGUILayout.Toggle(new GUIContent("Per tip", "A new colour for every dab (2D canvas). Off: one colour per stroke. The 3D brush always uses one colour per stroke."), brush.colorPerTip);
            }
            showDualBrush = EditorGUILayout.Foldout(showDualBrush, "Dual brush", true);
            if (showDualBrush)
            {
                brush.dualEnabled = EditorGUILayout.Toggle(new GUIContent("Enabled", "A second tip along the same path masks the main tip (2D canvas)"), brush.dualEnabled);
                using (new EditorGUI.DisabledScope(!brush.dualEnabled))
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.PrefixLabel("Tip");
                    string label = string.IsNullOrEmpty(brush.dualTipId) ? "Round (hardness)" : BrushTips.ResolveRef(brush.dualTipId) == null ? "Missing: " + brush.dualTipId + " (round)" : brush.dualTipId;
                    if (GUILayout.Button(label, EditorStyles.popup)) DualTipMenu();
                    GUILayout.EndHorizontal();
                    brush.dualMode = (int)(DualBrushMode)EditorGUILayout.EnumPopup("Mode", (DualBrushMode)brush.dualMode);
                    brush.dualRadius = EditorGUILayout.Slider("Radius px", brush.dualRadius, .5f, 128);
                    if (string.IsNullOrEmpty(brush.dualTipId)) brush.dualHardness = EditorGUILayout.Slider("Hardness", brush.dualHardness, 0, 1);
                    brush.dualSpacing = EditorGUILayout.Slider("Spacing", brush.dualSpacing, .01f, 1);
                    brush.dualAngle = EditorGUILayout.Slider("Angle", brush.dualAngle, -180, 180);
                    brush.dualRoundness = EditorGUILayout.Slider("Roundness", brush.dualRoundness, .01f, 1);
                    brush.dualScatter = EditorGUILayout.Slider("Scatter", brush.dualScatter, 0, 10);
                    brush.dualCount = EditorGUILayout.IntSlider("Count", brush.dualCount, 1, 16);
                }
            }
            showFadeTilt = EditorGUILayout.Foldout(showFadeTilt, "Fade & tilt", true);
            if (showFadeTilt)
            {
                EditorGUILayout.LabelField("Fade over N dabs (0 = off)", EditorStyles.miniLabel);
                brush.fadeSize = Mathf.Clamp(EditorGUILayout.IntField("Fade size", brush.fadeSize), 0, BrushSettings.MaxFade);
                brush.fadeOpacity = Mathf.Clamp(EditorGUILayout.IntField("Fade opacity", brush.fadeOpacity), 0, BrushSettings.MaxFade);
                brush.fadeFlow = Mathf.Clamp(EditorGUILayout.IntField("Fade flow", brush.fadeFlow), 0, BrushSettings.MaxFade);
                EditorGUILayout.LabelField(new GUIContent("Pen tilt (upright = full)", "From Unity's Event.tilt. A mouse reports no tilt, so these change nothing for it. Not yet checked with a real pen."), EditorStyles.miniLabel);
                brush.tiltSize = EditorGUILayout.Toggle("Tilt size", brush.tiltSize);
                brush.tiltOpacity = EditorGUILayout.Toggle("Tilt opacity", brush.tiltOpacity);
                brush.tiltFlow = EditorGUILayout.Toggle("Tilt flow", brush.tiltFlow);
                brush.tiltAngle = EditorGUILayout.Toggle(new GUIContent("Tilt angle", "Turns the tip toward the direction the pen leans"), brush.tiltAngle);
            }
            GUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Texture");
            if (GUILayout.Button(new GUIContent("Choose…", "Paper texture: built-in grains, or the textures of imported brushes (Photoshop .pat / .abr patterns)"), EditorStyles.miniButton)) TextureMenu();
            GUILayout.EndHorizontal();
        }

        void DualTipMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Round"), string.IsNullOrEmpty(brush.dualTipId), () => brush.dualTipId = "");
            if (!string.IsNullOrEmpty(brush.tipId)) menu.AddItem(new GUIContent("Same as the main tip"), brush.dualTipId == brush.tipId, () => brush.dualTipId = brush.tipId);
            foreach (var id in BuiltInBrushes.TipIds) { string full = "builtin:" + id; menu.AddItem(new GUIContent("Built-in/" + id), brush.dualTipId == full, () => brush.dualTipId = full); }
            menu.ShowAsContext();
        }
        void TextureMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("None"), string.IsNullOrEmpty(brush.textureId), () => brush.textureId = "");
            foreach (var id in BuiltInBrushes.TipIds) { string full = "builtin:" + id; menu.AddItem(new GUIContent("Built-in/" + id), brush.textureId == full, () => SetTexture(full)); }
            foreach (var library in BrushLibrary.All)
                foreach (var preset in library.Presets)
                {
                    string id = BrushTips.IdOf(preset.CreateSettings().Texture);
                    if (string.IsNullOrEmpty(id)) continue;
                    menu.AddItem(new GUIContent(library.MenuName + "/" + preset.Name), brush.textureId == id, () => SetTexture(id));
                }
            menu.ShowAsContext();
        }
        /// <summary>紙の質感を選ぶ。効かないまま選ばれないよう、深さが 0 なら 1 にする。</summary>
        internal void SetTexture(string id)
        {
            brush.textureId = id ?? "";
            if (!string.IsNullOrEmpty(brush.textureId) && brush.textureDepth <= 0) brush.textureDepth = 1;
        }
    }
}
