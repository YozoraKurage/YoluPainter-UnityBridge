using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// マテリアルで塗る（本物のウィンドウに SendEvent、GUI モード）: ブラシの欄の「マテリアル」をマウスで入れ、チャンネルを選ぶと、2D の
    /// ストロークも 3D ビューのストロークも組の全部のチャンネルに同じ覆いで塗り、層で無効のチャンネルは有効にして、1 回の Undo で全部
    /// （有効にしたことも）戻る。Esc・フォーカスを失うと何も残らない。マスクの編集中はマスクだけ。画像のロックではチャンネルを有効にもしない。
    /// オフなら以前と同じく今のチャンネル 1 つ。スポイトはマテリアルの今のチャンネルの値に入る。プリセットを選んでも残り、ブラシの
    /// プリセットの JSON で往復する。レイヤーのパネルでは、今のチャンネルの合成モードと不透明度を切り替えで「このチャンネルだけ」にし、
    /// スライダーのドラッグは 1 回の Undo。
    /// </summary>
    public sealed partial class WindowTests
    {
        static readonly PaintChannel[] MaterialChannels = { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Height, PaintChannel.Emission };

        /// <summary>硬い・筆圧の効かないブラシで、Color・Roughness・Height・Emission のマテリアル（マウスで入れる）。</summary>
        void UseMaterialBrush()
        {
            var b = window.Brush; b.radius = 12; b.hardness = 1; b.opacity = 1; b.flow = 1; b.spacing = .1f; b.pressureSize = b.pressureOpacity = b.pressureFlow = false;
            b.color = new Color(.8f, .1f, .2f, 1); b.materialRoughness = .2f; b.materialHeight = .9f; b.materialEmission = new Color(0, 1, .5f, 1);
            window.Brush = b; window.Tool = TexturePaintWindow.PaintTool.Brush;
            ClickToolControl("material.toggle");
            Assert.That(window.MaterialMode, Is.True, "the toggle turns material painting on");
            Assert.That(window.Brush.materialChannels, Is.EqualTo(1 << (int)PaintChannel.Color), "it starts with the selected channel");
            foreach (var c in new[] { PaintChannel.Roughness, PaintChannel.Height, PaintChannel.Emission }) ClickToolControl("material.chip." + c);
            Assert.That(MaterialChannels.All(window.MaterialIncludes), Is.True);
            Assert.That(window.MaterialIncludes(PaintChannel.Metallic), Is.False);
            foreach (var c in MaterialChannels) Assert.That(window.MaterialValue(c), Is.EqualTo(Expected[c]), c + ": the value the stroke paints");
            Assert.That(window.StrokeChannels().Select(p => p.Channel), Is.EqualTo(MaterialChannels.OrderBy(c => c)));
        }
        /// <summary>The values UseMaterialBrush sets, as bytes (each component × 255 in float, rounded half to even as GetBrush does:
        /// .9f × 255 is 229.5 in float, so 230; .5f × 255 = 127.5, so 128).</summary>
        static readonly Dictionary<PaintChannel, Rgba32> Expected = new Dictionary<PaintChannel, Rgba32>
        {
            { PaintChannel.Color, new Rgba32(204, 26, 51, 255) }, { PaintChannel.Roughness, new Rgba32(51, 51, 51, 255) },
            { PaintChannel.Height, new Rgba32(230, 230, 230, 255) }, { PaintChannel.Emission, new Rgba32(0, 255, 128, 255) },
        };
        Dictionary<PaintChannel, byte[]> AllComposites() => ((PaintChannel[])System.Enum.GetValues(typeof(PaintChannel))).ToDictionary(c => c, c => window.Document.Composite(c));

        [Test] public void A2DMaterialStrokePaintsEveryCheckedChannelAndOneUndoTakesItBack()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            UseMaterialBrush();
            var before = AllComposites(); var saved = DocumentBinary.Write(d); int steps = d.UndoCount;
            Assert.That(layer.IsChannelEnabled(PaintChannel.Roughness), Is.False);
            BeginLine(300, 400); Mouse(window, EventType.MouseUp, At(window, 360, 400));
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "painting four channels and switching three on is one step");
            foreach (var c in MaterialChannels)
            {
                Assert.That(layer.IsChannelEnabled(c), Is.True, c.ToString());
                Assert.That(d.CompositePixel(c, 330, 400), Is.EqualTo(Expected[c]), c + " under the line");
            }
            Assert.That(d.Composite(PaintChannel.Metallic), Is.EqualTo(before[PaintChannel.Metallic]), "Metallic is not in the material");
            // どのチャンネルも同じ覆い（同じ画素に塗られている）
            var covered = MaterialChannels.Select(c => d.Composite(c).Where((v, i) => i % 4 == 3).ToArray()).ToList();
            Assert.That(covered[0].Count(a => a > 0), Is.GreaterThan(100));
            foreach (var other in covered.Skip(1)) CpuCompositingTests.AssertSameBytes(covered[0], other, "alpha");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(saved), "one undo: every channel back, the switched-on channels off again");
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            foreach (var c in MaterialChannels) Assert.That(d.CompositePixel(c, 330, 400), Is.EqualTo(Expected[c]), c + " after redo");
            Repaint(window); // 表示（今のチャンネル）も正本どおり
        }

        [TestCase("escape")] [TestCase("focus")]
        public void CancellingAMaterialStrokeLeavesNothingInAnyChannel(string how)
        {
            var d = window.Document; UseMaterialBrush();
            var saved = DocumentBinary.Write(d); int steps = d.UndoCount;
            BeginLine(400, 300);
            if (how == "escape") Key(window, KeyCode.Escape); else Invoke(window, "OnLostFocus");
            Assert.That(window.IsStroking, Is.False);
            Mouse(window, EventType.MouseUp, At(window, 460, 300));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(saved), how + ": nothing painted and no channel left switched on");
            Assert.That(d.UndoCount, Is.EqualTo(steps));
        }

        [Test] public void A3DMaterialStrokePaintsEveryChannelOnTheModel()
        {
            LoadSymmetricBox();
            UseMaterialBrush();
            var b = window.Brush; b.radius = 24; b.hardness = .5f; window.Brush = b;
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer); int steps = d.UndoCount;
            Mouse(window, EventType.MouseDown, SurfacePoint(-80, -40));
            Mouse(window, EventType.MouseDrag, SurfacePoint(-60, -30));
            Mouse(window, EventType.MouseDrag, SurfacePoint(-40, -36));
            Mouse(window, EventType.MouseUp, SurfacePoint(-40, -36));
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            // 面のダブの覆いは同じなので、どのチャンネルもアルファが画素ごとに同じ（値の色は違う）
            var alpha = MaterialChannels.Select(c => d.Composite(c).Where((v, i) => i % 4 == 3).ToArray()).ToList();
            Assert.That(alpha[0].Count(a => a > 0), Is.GreaterThan(50), "the stroke reached the model");
            foreach (var a in alpha.Skip(1)) Assert.That(a, Is.EqualTo(alpha[0]));
            int opaque = Enumerable.Range(0, alpha[0].Length).First(i => alpha[0][i] == 255);
            foreach (var c in MaterialChannels) Assert.That(layer.GetPixel(c, opaque % d.Width, opaque / d.Width), Is.EqualTo(Expected[c]), c.ToString());
            Key(window, KeyCode.Z, EventModifiers.Control);
            foreach (var c in MaterialChannels.Skip(1)) Assert.That(layer.IsChannelEnabled(c), Is.False, c + " is off again after undo");
        }

        [Test] public void WhileEditingAMaskTheMaterialBrushPaintsTheMaskOnly()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            UseMaterialBrush();
            d.AddLayerMask(layer.Id); window.EditMask = true;
            var before = AllComposites();
            BeginLine(300, 300); Mouse(window, EventType.MouseUp, At(window, 360, 300));
            Assert.That(layer.Mask.Surface.TileCount, Is.GreaterThan(0), "the mask was painted");
            foreach (var c in MaterialChannels.Skip(1)) Assert.That(layer.IsChannelEnabled(c), Is.False, c + " was not switched on");
            Assert.That(layer.GetChannel(PaintChannel.Color).TileCount, Is.Zero, "no colour pixels");
        }

        [Test] public void LockedImagePixelsRefuseTheMaterialStrokeWithoutSwitchingChannelsOn()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            UseMaterialBrush();
            d.SetLayerLocks(layer.Id, LayerLocks.Pixels);
            var saved = DocumentBinary.Write(d); int steps = d.UndoCount;
            BeginLineRefused(300, 300);
            Assert.That(window.StatusMessage, Does.Contain("locked"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(saved)); Assert.That(d.UndoCount, Is.EqualTo(steps));
        }
        void BeginLineRefused(int x, int y)
        {
            Mouse(window, EventType.MouseDown, At(window, x, y)); Mouse(window, EventType.MouseDrag, At(window, x + 60, y)); Mouse(window, EventType.MouseUp, At(window, x + 60, y));
            Assert.That(window.IsStroking, Is.False);
        }

        [Test] public void OffTheBrushPaintsTheSelectedChannelAsBefore()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            var b = window.Brush; b.radius = 10; b.hardness = 1; b.opacity = 1; b.flow = 1; b.pressureSize = b.pressureOpacity = false; b.color = new Color(.4f, .4f, .4f, 1); window.Brush = b;
            window.Channel = PaintChannel.Metallic;
            Assert.That(window.StrokeChannels().Select(p => (p.Channel, p.Value)), Is.EqualTo(new[] { (PaintChannel.Metallic, new Rgba32(102, 102, 102, 255)) }));
            int steps = d.UndoCount;
            BeginLine(300, 300); Mouse(window, EventType.MouseUp, At(window, 360, 300));
            Assert.That(d.CompositePixel(PaintChannel.Metallic, 330, 300), Is.EqualTo(new Rgba32(102, 102, 102, 255)));
            Assert.That(layer.GetChannel(PaintChannel.Color).TileCount, Is.Zero, "only the selected channel");
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "switching Metallic on is part of the stroke's step");
            d.Undo(); Assert.That(layer.IsChannelEnabled(PaintChannel.Metallic), Is.False);
        }

        [Test] public void TheEyedropperAndPresetsKeepTheMaterial()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            UseMaterialBrush();
            layer.GetChannel(PaintChannel.Roughness).SetPixel(100, 100, new Rgba32(77, 77, 77, 255)); d.ClearHistory();
            window.Channel = PaintChannel.Roughness;
            window.PickColor(new Vector2(100.5f, 100.5f));
            Assert.That(window.Brush.materialRoughness * 255, Is.EqualTo(77).Within(.01), "the eyedropper fills the material's value of the selected channel");
            Assert.That(window.Brush.color, Is.EqualTo(new Color(.8f, .1f, .2f, 1)), "and leaves the foreground colour");
            window.ApplyPreset(BuiltInBrushes.Presets[2]);
            Assert.That(window.MaterialMode, Is.True); Assert.That(MaterialChannels.All(window.MaterialIncludes), Is.True, "a preset keeps the material");
            Assert.That(window.Brush.materialRoughness * 255, Is.EqualTo(77).Within(.01));
            // プリセットの JSON（brush.json と同じ形）で往復し、知らない印は断る
            var json = JsonUtility.ToJson(window.Brush);
            var read = (TexturePaintWindow.BrushState)typeof(TexturePaintWindow).GetMethod("ReadBrushState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { json });
            Assert.That((read.material, read.materialChannels, read.materialHeight, read.materialEmission), Is.EqualTo((true, window.Brush.materialChannels, .9f, new Color(0, 1, .5f, 1))));
            var bad = json.Replace("\"materialChannels\":" + window.Brush.materialChannels, "\"materialChannels\":4096");
            Assert.That(bad, Is.Not.EqualTo(json));
            var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => typeof(TexturePaintWindow).GetMethod("ReadBrushState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { bad }));
            Assert.That(ex.InnerException, Is.InstanceOf<InvalidDataException>());
            // 古いファイル（マテリアルの項目が無い）はオフで読む
            var old = (TexturePaintWindow.BrushState)typeof(TexturePaintWindow).GetMethod("ReadBrushState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { "{\"schema\":3,\"radius\":5}" });
            Assert.That((old.material, old.materialChannels, old.materialRoughness, old.radius), Is.EqualTo((false, 0, .5f, 5f)));
        }

        [Test] public void TheLastChannelOfTheMaterialStays()
        {
            window.Tool = TexturePaintWindow.PaintTool.Brush;
            ClickToolControl("material.toggle");
            ClickToolControl("material.chip." + PaintChannel.Color);
            Assert.That(window.MaterialIncludes(PaintChannel.Color), Is.True, "the only channel cannot be left out");
            Assert.That(window.StatusMessage, Does.Contain("A material paints at least one channel."));
            ClickToolControl("material.toggle");
            Assert.That(window.MaterialMode, Is.False);
        }

        [Test] public void TheMaterialAndChannelBlendsSurviveSaveAndOpenAndABadBrushFileDoesNotStopOpening()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            UseMaterialBrush();
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            BeginLine(300, 400); Mouse(window, EventType.MouseUp, At(window, 360, 400));
            d.SetChannelBlend(layer.Id, PaintChannel.Height, new ChannelBlend(LayerBlendMode.LinearDodge, .75));
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            byte[] saved = DocumentBinary.Write(d); var brushJson = JsonUtility.ToJson(window.Brush);
            var other = Open();
            try
            {
                other.OpenProjectAt(fake.File);
                Assert.That(DocumentBinary.Write(other.Document), Is.EqualTo(saved), other.StatusMessage);
                Assert.That(other.Document.GetLayer(layer.Id).ChannelBlendOf(PaintChannel.Height), Is.EqualTo(new ChannelBlend(LayerBlendMode.LinearDodge, .75)));
                Assert.That(other.MaterialMode, Is.True); Assert.That(MaterialChannels.All(other.MaterialIncludes), Is.True, "brush.json keeps the material");
                Assert.That(other.MaterialValue(PaintChannel.Height), Is.EqualTo(Expected[PaintChannel.Height]));
            }
            finally { Close(other); }
            // brush.json（状態）が読めなくても、正本は開いて理由を知らせる
            var files = YlpArchive.Read(File.ReadAllBytes(fake.File));
            files["brush.json"] = System.Text.Encoding.UTF8.GetBytes(brushJson.Replace("\"materialChannels\":" + window.Brush.materialChannels, "\"materialChannels\":4096"));
            string bad = NewYlpPath("Bad.ylp"); File.WriteAllBytes(bad, YlpArchive.Write(files));
            other = Open();
            try
            {
                other.OpenProjectAt(bad);
                Assert.That(DocumentBinary.Write(other.Document), Is.EqualTo(saved), other.StatusMessage);
                Assert.That(other.StatusMessage, Does.Contain("brush settings could not be read"));
                Assert.That(other.MaterialMode, Is.False, "the brush keeps its own settings");
            }
            finally { Close(other); }
        }

        // ───────── レイヤーのパネル: チャンネルごとの合成モードと不透明度 ─────────

        [Test] public void TheLayersPanelEditsTheSelectedChannelsOwnBlendAndOpacity()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            d.SetLayerOpacity(layer.Id, .8); d.ClearHistory();
            window.Channel = PaintChannel.Roughness; Repaint(window);
            var toggle = LayerPanelPoint("channelBlend");
            SendHost(EventType.MouseDown, toggle); SendHost(EventType.MouseUp, toggle); Repaint(window);
            Assert.That(layer.ChannelBlendOf(PaintChannel.Roughness), Is.EqualTo(new ChannelBlend(LayerBlendMode.Normal, .8)), window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(1));
            // 不透明度のスライダーをドラッグ: このチャンネルだけ、1 回の Undo
            var a = LayerPanelPoint("opacity", .9f); var bPoint = LayerPanelPoint("opacity", .35f);
            SendHost(EventType.MouseDown, a); SendHost(EventType.MouseDrag, Vector2.Lerp(a, bPoint, .5f)); SendHost(EventType.MouseDrag, bPoint); SendHost(EventType.MouseUp, bPoint); Repaint(window);
            Assert.That(layer.OpacityIn(PaintChannel.Roughness), Is.LessThan(.6));
            Assert.That(layer.Opacity, Is.EqualTo(.8), "the layer's own opacity is unchanged");
            Assert.That(d.UndoCount, Is.EqualTo(2), "one drag, one step");
            // Color に切り替えると層の値が見え、スライダーは層の値を変える
            window.Channel = PaintChannel.Color; Repaint(window);
            SendHost(EventType.MouseDown, LayerPanelPoint("opacity", .9f)); SendHost(EventType.MouseUp, LayerPanelPoint("opacity", .9f)); Repaint(window);
            Assert.That(layer.Opacity, Is.GreaterThan(.85)); Assert.That(layer.OpacityIn(PaintChannel.Roughness), Is.LessThan(.6), "Roughness keeps its own");
            // 上の切り替えで層の値に戻す（ペイントの層を選ぶとプロパティの欄はブラシなので、一覧の × は塗りつぶし・調整・グループの層の欄と、
            // レイヤーの右クリックの「チャンネルごとの合成」にある）
            window.Channel = PaintChannel.Roughness; Repaint(window);
            toggle = LayerPanelPoint("channelBlend");
            SendHost(EventType.MouseDown, toggle); SendHost(EventType.MouseUp, toggle); Repaint(window);
            Assert.That(layer.HasChannelBlends, Is.False);
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(layer.ChannelBlends.ContainsKey(PaintChannel.Roughness), Is.True);
        }
    }
}
