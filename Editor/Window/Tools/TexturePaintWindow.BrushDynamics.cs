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
        /// キャンバス（上向き）へ符号を返す。表示が回っている・反転しているときは、ペンの倒れた向きを画面からキャンバスの向きに直す
        /// （<see cref="TiltToCanvas"/>）。マウスは 0（直立）。</summary>
        BrushSample PenSample(CanvasView view, double x, double y, float pressure)
        {
            var tilt = Event.current != null ? Event.current.tilt : Vector2.zero;
            if (float.IsNaN(tilt.x) || float.IsNaN(tilt.y) || float.IsInfinity(tilt.x) || float.IsInfinity(tilt.y)) tilt = Vector2.zero;
            if (view.AxisAligned || tilt == Vector2.zero) return new BrushSample(x, y, pressure, EditorApplication.timeSinceStartup, tilt.x, -tilt.y);
            var (tx, ty) = TiltToCanvas(view, tilt);
            return new BrushSample(x, y, pressure, EditorApplication.timeSinceStartup, tx, ty);
        }
        /// <summary>画面の傾き（X・Y 軸ごとの直立からの角度、画面の y は下向き）を、表示の回転・反転を戻したキャンバスの軸ごとの角度に。
        /// 傾きの向き (tan θx, tan θy) を向きとして回し、軸ごとの角度に戻す（Core の PenTilt と同じ考え方なので、倒れた量は変わらず、向きだけ回る）。</summary>
        internal static (double x, double y) TiltToCanvas(CanvasView view, Vector2 tilt)
        {
            const double max = Math.PI / 2 - 1e-6;
            double tx = Math.Tan(Math.Max(-max, Math.Min(max, tilt.x))), ty = Math.Tan(Math.Max(-max, Math.Min(max, tilt.y)));
            var d = view.ScreenToCanvasDirection(new Vector2((float)tx, (float)ty));
            return (Math.Atan(d.x), Math.Atan(d.y));
        }

        // ───────── プロパティの欄（テクスチャ・デュアルブラシ・色の変化・フェードとペンの傾き） ─────────

        void TextureSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-texture", L.Tr("Texture"), "grid_dots")) return;
            bool none = string.IsNullOrEmpty(brush.textureId), missing = !none && BrushTips.ResolveRef(brush.textureId) == null;
            string name = none ? L.Tr("None") : missing ? L.Tr("Missing") + ": " + brush.textureId : brush.textureId;
            PaintGui.FitDropdown(Mark("texture", rows.Row()), null, name, TextureMenu,
                L.Tr("Paper texture: built-in grains, or the textures of imported brushes (Photoshop .pat / .abr patterns)"), valueIsData: !none);
            if (!none)
            {
                brush.textureDepth = PercentSlider(rows.SliderRow(), L.TrIn("brush", "Depth"), brush.textureDepth, 0, 1, L.Tr("Texture depth"));
                brush.textureScale = PercentSlider(rows.SliderRow(), L.TrIn("brush", "Scale"), brush.textureScale, .05f, 16, L.Tr("Texture scale"));
            }
            rows.Space(4);
        }

        void DualBrushSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-dual", L.Tr("Dual Brush"), "content_copy")) return;
            brush.dualEnabled = PaintGui.FitToggle(rows.Row(), L.Tr("Use a dual brush"), brush.dualEnabled, L.Tr("A second tip along the same path masks the main tip (2D canvas)"));
            bool on = brush.dualEnabled, round = string.IsNullOrEmpty(brush.dualTipId), missing = !round && BrushTips.ResolveRef(brush.dualTipId) == null;
            string tip = round ? L.Tr("Round (hardness)") : missing ? L.Tr("Missing") + ": " + brush.dualTipId : brush.dualTipId;
            PaintGui.FitDropdown(rows.Row(), L.Tr("Tip"), tip, DualTipMenu, missing ? L.Tr("This tip is not in this Unity project; a round tip is used instead.") : null, on, LabelColumn, !round);
            PaintGui.FitDropdown(rows.Row(), L.TrIn("brush", "Mode"), DualModeName((DualBrushMode)brush.dualMode), at =>
            {
                var menu = new PaintMenu();
                foreach (DualBrushMode mode in Enum.GetValues(typeof(DualBrushMode))) { var m = mode; menu.AddItem(new GUIContent(DualModeName(m)), (int)m == brush.dualMode, () => brush.dualMode = (int)m); }
                menu.DropDown(at);
            }, L.Tr("How the second tip's coverage combines with the main tip's"), on, LabelColumn);
            float size = brush.dualRadius * 2, nextSize = PaintGui.FitSlider(rows.SliderRow(), L.Tr("Size"), size, 1, 256, "0", " px", L.Tr("Diameter of the second tip"), on);
            if (nextSize != size) brush.dualRadius = Mathf.Max(.5f, nextSize / 2);
            brush.dualHardness = PercentSlider(rows.SliderRow(), L.Tr("Hardness"), brush.dualHardness, 0, 1, L.Tr("Hardness of the round second tip (an image tip keeps its own edge)"), on && round);
            brush.dualSpacing = PercentSlider(rows.SliderRow(), L.Tr("Spacing"), brush.dualSpacing, .01f, 1, null, on);
            brush.dualAngle = PaintGui.FitSlider(rows.SliderRow(), L.Tr("Angle"), brush.dualAngle, -180, 180, "0", "°", null, on);
            brush.dualRoundness = PercentSlider(rows.SliderRow(), L.Tr("Roundness"), brush.dualRoundness, .01f, 1, null, on);
            brush.dualScatter = PercentSlider(rows.SliderRow(), L.Tr("Scatter"), brush.dualScatter, 0, 10, L.Tr("How far the dabs spread across the stroke, in % of the diameter"), on);
            brush.dualCount = PaintGui.FitIntSlider(rows.SliderRow(), L.Tr("Count"), brush.dualCount, 1, 16, "", L.Tr("Dabs placed at every spacing step"), on);
            rows.Space(4);
        }

        static string DualModeName(DualBrushMode mode)
        {
            switch (mode)
            {
                case DualBrushMode.Multiply: return L.TrIn("blend mode", "Multiply");
                case DualBrushMode.Darken: return L.TrIn("blend mode", "Darken");
                case DualBrushMode.Overlay: return L.TrIn("blend mode", "Overlay");
                case DualBrushMode.ColorDodge: return L.TrIn("blend mode", "Color Dodge");
                case DualBrushMode.ColorBurn: return L.TrIn("blend mode", "Color Burn");
                case DualBrushMode.LinearBurn: return L.TrIn("blend mode", "Linear Burn");
                case DualBrushMode.HardMix: return L.TrIn("blend mode", "Hard Mix");
                case DualBrushMode.Subtract: return L.TrIn("blend mode", "Subtract");
                default: return mode.ToString();
            }
        }

        void ColorDynamicsSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-color", L.Tr("Color Dynamics"), "palette")) return;
            if (EditingMask || !BrushSettings.CarriesColor(channel)) PaintGui.Notice(rows, L.Tr("Only Color and Emission use color dynamics."), "info", PaintTheme.TextDim);
            var row = rows.SliderRow();
            PaintGui.ColorSwatch(new Rect(row.x, row.y + 4, 30, row.height - 8), brush.secondaryColor, color => { color.a = 1; brush.secondaryColor = color; Repaint(); }, false,
                L.Tr("Background color") + "\n" + L.Tr("The second color for foreground/background jitter"));
            brush.fgBgJitter = PercentSlider(new Rect(row.x + 36, row.y, row.width - 36, row.height), L.TrIn("brush", "Fg/Bg jitter"), brush.fgBgJitter, 0, 1,
                L.Tr("Each dab mixes toward the background color by a random amount up to this"));
            PaintGui.GroupLabel(rows.Row(16), L.TrIn("brush", "Jitter"));
            brush.hueJitter = PercentSlider(rows.SliderRow(), L.TrIn("brush", "Hue"), brush.hueJitter, 0, 1, L.Tr("The hue moves by up to ± this × 180°"));
            brush.saturationJitter = PercentSlider(rows.SliderRow(), L.TrIn("brush", "Saturation"), brush.saturationJitter, 0, 1, L.Tr("HSV saturation moves by up to ± this"));
            brush.brightnessJitter = PercentSlider(rows.SliderRow(), L.TrIn("brush", "Brightness"), brush.brightnessJitter, 0, 1, L.Tr("HSV value moves by up to ± this"));
            brush.purity = PercentSlider(rows.SliderRow(), L.TrIn("brush", "Purity"), brush.purity, -1, 1, L.Tr("-100% gray … 0 unchanged … 100% fully saturated"));
            brush.colorPerTip = PaintGui.FitToggle(rows.Row(), L.Tr("Apply per tip"), brush.colorPerTip,
                L.Tr("A new color for every dab (2D canvas). Off: one color per stroke. The 3D brush always uses one color per stroke."));
            rows.Space(4);
        }

        void FadeTiltSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-fade", L.Tr("Fade & Pen Tilt"), "stylus")) return;
            PaintGui.GroupLabel(rows.Row(16), L.Tr("Fade (steps, 0 = off)"), L.Tr("The value falls from full to nothing over this many dabs from the start of the stroke. Drag sideways or click to type."));
            var c = UiRows.Split(rows.Row(), 2, 6);
            brush.fadeSize = PaintGui.IntField(c[0], L.Tr("Size"), brush.fadeSize, 0, BrushSettings.MaxFade, "", L.Tr("The size fades out over this many dabs"));
            brush.fadeOpacity = PaintGui.IntField(c[1], L.Tr("Opacity"), brush.fadeOpacity, 0, BrushSettings.MaxFade, "", L.Tr("The opacity fades out over this many dabs"));
            c = UiRows.Split(rows.Row(), 2, 6);
            brush.fadeFlow = PaintGui.IntField(c[0], L.Tr("Flow"), brush.fadeFlow, 0, BrushSettings.MaxFade, "", L.Tr("The flow fades out over this many dabs"));
            PaintGui.GroupLabel(rows.Row(16), L.Tr("Pen tilt (upright = full)"), L.Tr("From Unity's Event.tilt. A mouse reports no tilt, so these change nothing for it. Not yet checked with a real pen."));
            c = UiRows.Split(rows.Row(), 2, 6);
            brush.tiltSize = PaintGui.FitToggle(c[0], L.Tr("Size"), brush.tiltSize, L.Tr("The more the pen leans, the smaller the tip"));
            brush.tiltOpacity = PaintGui.FitToggle(c[1], L.Tr("Opacity"), brush.tiltOpacity, L.Tr("The more the pen leans, the lower the opacity"));
            c = UiRows.Split(rows.Row(), 2, 6);
            brush.tiltFlow = PaintGui.FitToggle(c[0], L.Tr("Flow"), brush.tiltFlow, L.Tr("The more the pen leans, the lower the flow"));
            brush.tiltAngle = PaintGui.FitToggle(c[1], L.Tr("Angle"), brush.tiltAngle, L.Tr("Turns the tip toward the direction the pen leans"));
            rows.Space(4);
        }

        void DualTipMenu(Rect at)
        {
            var menu = new PaintMenu();
            menu.AddItem(new GUIContent(L.TrIn("brush", "Round")), string.IsNullOrEmpty(brush.dualTipId), () => brush.dualTipId = "");
            if (!string.IsNullOrEmpty(brush.tipId)) menu.AddItem(new GUIContent(L.Tr("Same as the main tip")), brush.dualTipId == brush.tipId, () => brush.dualTipId = brush.tipId);
            foreach (var id in BuiltInBrushes.TipIds) { string full = "builtin:" + id; menu.AddItem(new GUIContent(L.TrIn("brush", "Built-in") + "/" + id), brush.dualTipId == full, () => brush.dualTipId = full); }
            menu.DropDown(at);
        }
        void TextureMenu(Rect at)
        {
            var menu = new PaintMenu();
            menu.AddItem(new GUIContent(L.Tr("None")), string.IsNullOrEmpty(brush.textureId), () => brush.textureId = "");
            foreach (var id in BuiltInBrushes.TipIds) { string full = "builtin:" + id; menu.AddItem(new GUIContent(L.TrIn("brush", "Built-in") + "/" + id), brush.textureId == full, () => SetTexture(full)); }
            foreach (var library in BrushLibrary.All)
                foreach (var preset in library.Presets)
                {
                    string id = BrushTips.IdOf(preset.CreateSettings().Texture);
                    if (string.IsNullOrEmpty(id)) continue;
                    menu.AddItem(new GUIContent(library.MenuName + "/" + preset.Name), brush.textureId == id, () => SetTexture(id));
                }
            menu.DropDown(at);
        }

        /// <summary>紙の質感を選ぶ。効かないまま選ばれないよう、深さが 0 なら 1 にする。</summary>
        internal void SetTexture(string id)
        {
            brush.textureId = id ?? "";
            if (!string.IsNullOrEmpty(brush.textureId) && brush.textureDepth <= 0) brush.textureDepth = 1;
        }
    }
}
