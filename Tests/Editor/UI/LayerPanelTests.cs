using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// プロパティの欄のレイヤー・チャンネル側（レイヤー・レイヤーマスク・フィルター・ノーマル・メッシュマップ・ポーズ）: Unity の標準の部品
    /// （LegacySection・EditorGUILayout など）を使っていないこと、どの状態でも英語・日本語で例外なく描け、既定のドックの幅（最小のウィンドウ
    /// 980×640 でも大きいウィンドウでも同じ）で UI の文字が欄に収まる（… で詰めない）こと、ドックを最小まで狭めても描けること、描くだけでは
    /// 文書も設定も変わらないこと（double の値を float に丸めて Undo を作らない）。描いた PNG はテストプロジェクトの
    /// Logs/YoluPainterSnapshots/layer-panels に残す（見て確かめる用。リポジトリには入れない）。
    /// </summary>
    public sealed class LayerPanelTests
    {
        /// <summary>このセクションを描くファイル（ほかの人が持つツール側のファイルは含めない）。</summary>
        static readonly string[] Files =
        {
            "Window/Layers/TexturePaintWindow.LayerPanels.cs", "Window/Layers/TexturePaintWindow.Filters.cs", "Window/Layers/TexturePaintWindow.Normal.cs",
            "Window/Model/TexturePaintWindow.MeshMaps.cs", "Window/Model/TexturePaintWindow.MeshBake.cs", "Window/Model/MeshBakeWindow.cs", "Window/Model/TexturePaintWindow.Pose.cs", "UI/PaintGui.Layers.cs",
        };
        static readonly Regex Standard = new Regex(@"\bLegacySection\s*\(|\bEditorGUILayout\.|\bGUILayout\.|\bEditorGUI\.|\bEditorStyles\.", RegexOptions.Compiled);
        static readonly string[] Sections = { "layer", "mask", "filters", "normal", "mesh-maps", "pose" };
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "layer-panels"));
        /// <summary>プロパティの欄の中身の幅: 既定のドック（300）から枠の 1 と、はみ出したときのスクロールの印の 8 を引いたもの。もう一つは前の
        /// 版の最小のウィンドウのドック（294）のときの幅。どちらでも UI の文字を詰めない。</summary>
        static readonly int[] PanelWidths = { 291, 285 };
        /// <summary>利用者がドックを最小（220）まで狭めたときの幅。詰めた文字はツールチップに全文が出るので、描けることだけを確かめる。</summary>
        const int NarrowestPanel = 220 - 1 - 8;

        /// <summary>
        /// Unity の標準の部品が入らないこと。この欄に標準の部品が入る口は LegacySection（と、それを経ずに EditorGUILayout などを直に呼ぶこと）
        /// だけで、オフスクリーンの描画（batch-gl）では LegacySection は置き換えの文字を描くだけなので絵からは見分けられない。そこで描く
        /// ファイルそのものを調べる（コメントは除く）。GUI モードでの描画が LegacySection を通らないことは WindowTests の側で確かめる。
        /// </summary>
        [Test] public void TheSectionsUseOnlyThePaintKit()
        {
            string editor = PackagePaths.Physical("Editor");
            var found = new List<string>();
            foreach (var name in Files)
            {
                string path = Path.Combine(editor, name);
                Assert.That(File.Exists(path), Is.True, path);
                var lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = lines[i]; int comment = code.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) code = code.Substring(0, comment);
                    if (Standard.IsMatch(code)) found.Add(name + ":" + (i + 1) + ": " + lines[i].Trim());
                }
            }
            Assert.That(found, Is.Empty, "Unity's standard controls are still used:\n" + string.Join("\n", found));
        }

        [Test] public void EveryStateDrawsInBothLanguagesWithoutShortenedText()
        {
            RequireOffscreen();
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>(); var made = new List<Object>();
            try
            {
                OpenSections(w);
                foreach (var (state, setup) in States(w, made))
                {
                    setup();
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        foreach (int width in PanelWidths)
                        {
                            string name = state + "-" + (language == PainterLanguage.English ? "en" : "ja") + "-" + width;
                            int before = PaintGui.ShortenedTexts;
                            float height = Draw(w, width, name);
                            Assert.That(height, Is.GreaterThan(100), name);
                            Assert.That(PaintGui.ShortenedTexts - before, Is.Zero, name + ": a UI text did not fit and was shortened with …");
                        }
                        Assert.That(Draw(w, NarrowestPanel, state + "-" + (language == PainterLanguage.English ? "en" : "ja") + "-narrowest"), Is.GreaterThan(100));
                    }
                }
            }
            finally { L.OverrideLanguage(PainterLanguage.English); Object.DestroyImmediate(w); foreach (var o in made) if (o != null) Object.DestroyImmediate(o); }
        }

        /// <summary>描くだけでは何も変わらない: 欄の部品は float だが、文書と設定の値は double（読み込んだ値・PSD の値）。触っていない値を丸めて
        /// 書き戻すと、描くたびに Undo が増え、メッシュマップは「設定が変わった」と古くなる。前の版はレベル補正・塗りつぶしの値でそうなりえた。</summary>
        [Test] public void DrawingTheSectionsLeavesTheDocumentAndSettingsAlone()
        {
            RequireOffscreen();
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                OpenSections(w);
                w.Preview.LoadDemoMesh();
                var d = w.Document; double third = 1.0 / 3;
                var layer = d.AddLayer("Odd"); d.AddLayerMask(layer.Id); d.SetLayerMaskDensity(layer.Id, third);
                var noise = d.AddFilter(layer.Id, FilterTarget.Content, FilterSettings.Noise(third, (1 << 24) + 1, true), new[] { PaintChannel.Color });
                var levels = d.AddFilter(layer.Id, FilterTarget.Mask, FilterSettings.Levels(third, 2 * third, 1 + third, .1, .9), strength: third);
                var adjustment = d.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels(third, 2 * third, 1 + third, .1, .9));
                var hue = d.AddAdjustmentLayer("Hue", AdjustmentSettings.HueSaturation(third, third, -third));
                var fill = d.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Roughness, new Rgba32(100, 120, 140, 200) } });
                d.SetNormalSettings(new NormalSettings(true, third, HeightEdgeMode.Wrap, NormalYDirection.DirectX));
                var s = w.MeshBakeSettings; s.AoMaxDistance = third; s.CurvatureRadius = third / 10; s.ThicknessSpreadDegrees = 45 + third; s.AoSamples = 600; // 欄の範囲（〜512）の外
                d.ClearHistory();
                var settingsKey = string.Join("|", MeshBakeSettings.AllKinds.Select(s.KindKey));
                var maps = s.Maps.ToArray(); long revision = d.Revision; int undo = d.UndoCount;
                foreach (var (selected, filter, channel) in new[] { (layer.Id, noise.Id, PaintChannel.Color), (layer.Id, levels.Id, PaintChannel.Normal), (adjustment.Id, Guid.Empty, PaintChannel.Color), (hue.Id, Guid.Empty, PaintChannel.Color), (fill.Id, Guid.Empty, PaintChannel.Roughness), (fill.Id, Guid.Empty, PaintChannel.Height) })
                {
                    w.SelectedLayer = selected; w.SelectedFilter = filter; w.Channel = channel;
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    { L.OverrideLanguage(language); Draw(w, PanelWidths[0], "unchanged-" + d.GetLayer(selected).Name + "-" + channel + "-" + language); }
                }
                Assert.That(d.Revision, Is.EqualTo(revision), "drawing changed the document");
                Assert.That(d.UndoCount, Is.EqualTo(undo), "drawing made undo steps");
                Assert.That(d.GetLayer(layer.Id).Mask.Density, Is.EqualTo(third));
                Assert.That(d.GetLayer(layer.Id).Filters.Single().Settings, Is.EqualTo(FilterSettings.Noise(third, (1 << 24) + 1, true)), "a seed above 2^24 survives the float field");
                Assert.That(d.GetLayer(layer.Id).Mask.Filters.Single().Strength, Is.EqualTo(third));
                Assert.That(d.GetLayer(adjustment.Id).Adjustment, Is.EqualTo(AdjustmentSettings.Levels(third, 2 * third, 1 + third, .1, .9)));
                Assert.That(d.GetLayer(hue.Id).Adjustment, Is.EqualTo(AdjustmentSettings.HueSaturation(third, third, -third)));
                Assert.That(d.GetLayer(fill.Id).FillValues[PaintChannel.Roughness], Is.EqualTo(new Rgba32(100, 120, 140, 200)), "a scalar fill value whose RGB differ is not normalized by drawing");
                Assert.That(d.NormalSettings, Is.EqualTo(new NormalSettings(true, third, HeightEdgeMode.Wrap, NormalYDirection.DirectX)));
                Assert.That(string.Join("|", MeshBakeSettings.AllKinds.Select(s.KindKey)), Is.EqualTo(settingsKey), "the bake settings (and so the maps' freshness) are unchanged");
                Assert.That(s.Maps, Is.EqualTo(maps));
            }
            finally { L.OverrideLanguage(PainterLanguage.English); Object.DestroyImmediate(w); }
        }

        /// <summary>フィルターを足すメニュー: 足せないもの（ノーマルのチャンネルのシャープ、スカラーのチャンネルのカラーノイズ、マスクの無い層の
        /// マスク、調整レイヤーの画素）は隠さず、理由を添えて押せない項目にする。名前は訳す。</summary>
        [Test] public void TheAddFilterMenuKeepsRefusedFiltersWithTheirReason()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                var d = w.Document; var layer = d.Layers[0]; w.SelectedLayer = layer.Id;
                string Refusal(FilterTarget target, FilterType type, bool? monochrome = null)
                    => w.FilterChoices(layer.Id, target).Single(c => c.settings.Type == type && (monochrome == null || c.settings.Monochrome == monochrome)).refusal;
                Assert.That(w.FilterChoices(layer.Id, FilterTarget.Content).Count, Is.EqualTo(7), "every filter is listed");
                Assert.That(w.FilterChoices(layer.Id, FilterTarget.Content).All(c => c.refusal == null), Is.True, "the Color channel takes every filter");
                w.Channel = PaintChannel.Normal;
                Assert.That(Refusal(FilterTarget.Content, FilterType.Sharpen), Does.Contain("normals"));
                Assert.That(Refusal(FilterTarget.Content, FilterType.GaussianBlur), Is.Null, "the blur is defined for normals");
                w.Channel = PaintChannel.Roughness;
                Assert.That(Refusal(FilterTarget.Content, FilterType.Noise, false), Does.Contain("monochrome"));
                Assert.That(Refusal(FilterTarget.Content, FilterType.Noise, true), Is.Null);
                Assert.That(w.FilterChoices(layer.Id, FilterTarget.Mask).All(c => c.refusal != null && c.refusal.Contains("no mask")), Is.True, "no mask yet");
                d.AddLayerMask(layer.Id);
                Assert.That(Refusal(FilterTarget.Mask, FilterType.Noise, false), Does.Contain("scalar"));
                Assert.That(Refusal(FilterTarget.Mask, FilterType.Levels), Is.Null);
                var adjustment = d.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels());
                Assert.That(w.FilterChoices(adjustment.Id, FilterTarget.Content).All(c => c.refusal != null && c.refusal.Contains("Adjustment layers")), Is.True);
                L.OverrideLanguage(PainterLanguage.Japanese);
                Assert.That(w.FilterChoices(layer.Id, FilterTarget.Content).Select(c => c.label), Does.Contain("シャープ"));
            }
            finally { L.OverrideLanguage(PainterLanguage.English); Object.DestroyImmediate(w); }
        }

        [Test] public void LongPanelsStartClosedAndTheMeshMapPanelFollowsItsSection()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                Assert.That(w.ShowMeshMapPanel, Is.False, "the mesh-map panel starts closed (as before)");
                w.ShowMeshMapPanel = true;
                var open = (Dictionary<string, bool>)typeof(TexturePaintWindow).GetField("sectionOpen", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(w);
                Assert.That(open["mesh-maps"], Is.True);
                open["mesh-maps"] = false;
                Assert.That(w.ShowMeshMapPanel, Is.False);
            }
            finally { Object.DestroyImmediate(w); }
        }

        // ───────── 状態 ─────────

        /// <summary>描く状態: 画素のレイヤー（マスク・フィルターの各種の設定を開く）、塗りつぶし（色・スカラー・値なし）、調整の 3 種類（効かない
        /// チャンネルも）、グループ（通過・分離）、ノーマルとハイトのチャンネル、メッシュマップ（ベイク前・ベイク中・ベイク済み・古い）、ポーズ。</summary>
        static IEnumerable<(string, Action)> States(TexturePaintWindow w, List<Object> made)
        {
            var d = w.Document; PaintLayer raster = null, plain = null; FilterEffect blur = null, sharpen = null, noise = null, maskLevels = null;
            yield return ("raster-blur", () =>
            {
                w.Preview.LoadDemoMesh();
                raster = d.AddLayer("Details"); d.AddLayerMask(raster.Id); w.SelectedLayer = raster.Id;
                blur = d.AddFilter(raster.Id, FilterTarget.Content, FilterSettings.GaussianBlur(6), new[] { PaintChannel.Color });
                sharpen = d.AddFilter(raster.Id, FilterTarget.Content, FilterSettings.Sharpen(2, 1.5, 3), new[] { PaintChannel.Color }, strength: .6);
                noise = d.AddFilter(raster.Id, FilterTarget.Content, FilterSettings.Noise(.3, 7, true), new[] { PaintChannel.Roughness }, enabled: false);
                maskLevels = d.AddFilter(raster.Id, FilterTarget.Mask, FilterSettings.Levels(.1, .9, 1.2, 0, 1));
                d.AddFilter(raster.Id, FilterTarget.Content, FilterSettings.Normalize(), new[] { PaintChannel.Color });
                w.SelectedFilter = blur.Id;
            });
            yield return ("raster-sharpen", () => w.SelectedFilter = sharpen.Id);
            yield return ("raster-noise", () => w.SelectedFilter = noise.Id);
            yield return ("raster-mask-levels", () => w.SelectedFilter = maskLevels.Id);
            yield return ("raster-normalize", () => w.SelectedFilter = d.GetLayer(raster.Id).Filters.Last().Id);
            yield return ("raster-plain", () => { plain = d.AddLayer("Plain"); w.SelectedLayer = plain.Id; w.SelectedFilter = Guid.Empty; });
            PaintLayer fill = null;
            yield return ("fill-color", () => { fill = d.AddFillLayer("Tint", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(200, 120, 80) } }); w.SelectedLayer = fill.Id; });
            yield return ("fill-roughness-empty", () => w.Channel = PaintChannel.Roughness);
            yield return ("fill-roughness", () => d.SetFillValue(fill.Id, PaintChannel.Roughness, new Rgba32(100, 100, 100)));
            yield return ("adjustment-levels", () => { w.Channel = PaintChannel.Color; w.SelectedLayer = d.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels(.05, .95, 1.1, 0, 1)).Id; });
            yield return ("adjustment-hue", () => w.SelectedLayer = d.AddAdjustmentLayer("Hue", AdjustmentSettings.HueSaturation(30, -.2, .1)).Id);
            yield return ("adjustment-hue-roughness", () => w.Channel = PaintChannel.Roughness);
            yield return ("adjustment-invert", () => { w.Channel = PaintChannel.Color; w.SelectedLayer = d.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert()).Id; });
            PaintLayer group = null;
            yield return ("group-a", () => { group = d.GroupLayers(new[] { plain.Id }, "Group"); w.SelectedLayer = group.Id; });
            yield return ("group-b", () => d.SetLayerBlendMode(group.Id, group.BlendMode == LayerBlendMode.PassThrough ? LayerBlendMode.Normal : LayerBlendMode.PassThrough));
            yield return ("normal", () => { w.SelectedLayer = raster.Id; w.Channel = PaintChannel.Normal; d.SetLayerBlendMode(raster.Id, LayerBlendMode.Multiply); });
            yield return ("normal-adjustment", () => w.SelectedLayer = d.Layers.First(l => l.Kind == LayerKind.Adjustment).Id);
            yield return ("height", () => { w.SelectedLayer = raster.Id; w.Channel = PaintChannel.Height; d.SetNormalSettings(d.NormalSettings.WithDerive(true)); });
            // メッシュマップの区画: ベイクの窓を開く口と、焼いたマップの一覧だけ（焼くマップと設定は窓。MeshBakeWindowTests）
            yield return ("mesh-maps", () => { w.Channel = PaintChannel.Color; w.SelectedLayer = plain.Id; });
            yield return ("mesh-maps-baking", () =>
            {
                w.MeshBakeSettings.Maps = MeshBakeSettings.AllKinds.ToArray(); w.MeshBakeSettings.AoSamples = 512; w.MeshBakeUseGpu = false;
                Assert.That(w.StartMeshBake(), Is.Null, w.StatusMessage);
                w.PumpMeshBake(1); // 進み具合と取消の行
            });
            yield return ("mesh-maps-baked", () =>
            {
                w.CancelMeshBake(); while (w.PumpMeshBake(20)) System.Threading.Thread.Sleep(2);
                w.MeshBakeSettings.Maps = new[] { MeshMapKind.WorldNormal, MeshMapKind.Position }; // レイを使わない種類（速い）
                w.MeshBakeProgress = (title, info, progress) => false;
                Assert.That(w.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), w.StatusMessage);
                w.MeshMapOverlay = TexturePaintWindow.MeshMapView.Coverage;
            });
            yield return ("mesh-maps-stale", () => w.MeshBakeSettings.Padding = 3);
            yield return ("pose", () =>
            {
                var root = new GameObject("Pose source"); made.Add(root);
                var bone = new GameObject("bone"); bone.transform.SetParent(root.transform, false);
                var mesh = new Mesh { vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) }, uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }, triangles = new[] { 0, 2, 1, 1, 2, 3 } };
                made.Add(mesh);
                var weights = new BoneWeight[4]; for (int i = 0; i < 4; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 };
                mesh.boneWeights = weights; mesh.bindposes = new[] { Matrix4x4.identity };
                foreach (var shape in new[] { "blink", "a_blend_shape_name_that_is_much_too_long_for_the_panel" }) { var delta = new Vector3[4]; delta[0] = new Vector3(0, 0, -.5f); mesh.AddBlendShapeFrame(shape, 100, delta, null, null); }
                mesh.RecalculateNormals();
                var skin = root.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; skin.bones = new[] { bone.transform }; skin.rootBone = bone.transform;
                Assert.That(w.Preview.Load(root).CanPaint, Is.True);
            });
        }

        // ───────── 描く ─────────

        static void RequireOffscreen()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
        }

        static void OpenSections(TexturePaintWindow w)
        {
            var open = (Dictionary<string, bool>)typeof(TexturePaintWindow).GetField("sectionOpen", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(w);
            foreach (var key in Sections) open[key] = true;
        }

        /// <summary>レイヤーとチャンネルのセクションだけを、プロパティの欄と同じ呼び方（中身の幅 width の UiRows）で描いて PNG にする。中身の高さを返す。</summary>
        static float Draw(TexturePaintWindow w, int width, string name)
        {
            var type = typeof(TexturePaintWindow); var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var layer = type.GetMethod("LayerSections", flags); var channel = type.GetMethod("ChannelSections", flags);
            float used = 0; string path = Path.Combine(Folder, name + ".png");
            OffscreenGui.RenderToPng(width, 1600, () =>
            {
                PaintGui.Fill(new Rect(0, 0, width, 1600), PaintTheme.PanelBg);
                var rows = new UiRows(new Rect(0, 0, width, 1e6f), 0);
                layer.Invoke(w, new object[] { rows }); channel.Invoke(w, new object[] { rows });
                used = rows.Used;
            }, path, PaintTheme.PanelBg);
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, name);
                int colors = texture.GetPixels32().Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Take(100).Count();
                Assert.That(colors, Is.GreaterThan(20), name + ": the panel looks empty");
            }
            finally { Object.DestroyImmediate(texture); }
            return used;
        }
    }
}
