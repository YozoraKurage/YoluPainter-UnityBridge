using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウの Generator: テクスチャセットの焼いたマップを読み、マップが無い・古いあいだは入力のまま（理由を出す）、焼く・
    /// 消す・ベイクの設定を変えると表示が描き直される、セットごとに自分のマップを読む、キーボードの Undo/Redo とスライダーのまとめ、型と
    /// ストローク中の拒否、.ylp に残って開き直せる（モデルを読むまでは照合できないので入力のまま）、マップの無い Generator を書き出す前に
    /// 確かめ、保存では知らせる。入力は SendEvent で本物の経路に通す。</summary>
    public sealed partial class WindowTests
    {
        PaintLayer WornFill()
        {
            var d = window.Document;
            var fill = d.AddFillLayer("Worn", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(40, 120, 220, 255) } });
            d.AddLayerMask(fill.Id); d.ClearHistory();
            window.SelectedLayer = fill.Id;
            return fill;
        }
        static void BakeCurvature(TexturePaintWindow w)
        {
            QuickBake(w); w.MeshBakeSettings.Maps = new[] { MeshMapKind.Curvature, MeshMapKind.Position };
            Assert.That(w.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), w.StatusMessage);
        }

        [Test] public void AMaskGeneratorReadsTheBakedMapsAndRedrawsWhenTheyChange()
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            var d = window.Document; var fill = WornFill();
            var plain = d.Composite(PaintChannel.Color);
            var gen = window.AddGenerator(FilterTarget.Mask, GeneratorType.EdgeWear);
            Assert.That(gen, Is.Not.Null, window.StatusMessage);
            Assert.That(window.StatusMessage, Does.Contain("no effect until its mesh maps are baked"));
            var status = d.GetGeneratorStatus(fill.Id, gen.Id);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("Curvature has not been baked"));
            AssertShows(window, plain, "no maps: the mask passes through, nothing turns black");

            BakeCurvature(window);
            Assert.That(window.PollGeneratorInputs(), Is.True, "the bake changed what the generator reads");
            Assert.That(d.GetGeneratorStatus(fill.Id, gen.Id).Active, Is.True);
            var worn = d.Composite(PaintChannel.Color);
            Assert.That(worn, Is.Not.EqualTo(plain));
            AssertShows(window, worn, "edge wear from the cube's curvature");
            Assert.That(window.PollGeneratorInputs(), Is.False, "nothing changed since");

            // ベイクの設定を変えると、焼き直すまでマップは古い（使わない）
            window.MeshBakeSettings.CurvatureRadius *= 2;
            Assert.That(window.PollGeneratorInputs(), Is.True);
            status = d.GetGeneratorStatus(fill.Id, gen.Id);
            Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("bake settings changed"));
            AssertShows(window, plain, "stale maps are not used");
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed));
            Assert.That(window.PollGeneratorInputs(), Is.True);
            Assert.That(d.GetGeneratorStatus(fill.Id, gen.Id).Active, Is.True);
            AssertShows(window, d.Composite(PaintChannel.Color), "rebaked");

            // 消すと入力のまま
            window.MeshMaps.Clear();
            Assert.That(window.PollGeneratorInputs(), Is.True);
            AssertShows(window, plain, "cleared");
            Assert.That(d.InactiveGenerators().Single(), Does.Contain("Edge wear has no effect"));
        }

        [Test] public void EachTextureSetsGeneratorsReadThatSetsMaps()
        {
            window.Preview.LoadDemoMesh();
            var first = window.CurrentTextureSet; var fill = WornFill();
            var gen = window.AddGenerator(FilterTarget.Mask, GeneratorType.EdgeWear);
            BakeCurvature(window); window.PollGeneratorInputs();
            Assert.That(first.Document.GetGeneratorStatus(fill.Id, gen.Id).Active, Is.True);
            var second = window.AddTextureSet(1);
            Assert.That(window.CurrentTextureSet, Is.SameAs(second));
            Assert.That(second.Document.GeneratorInputs, Is.Not.Null.And.Not.SameAs(first.Document.GeneratorInputs), "each set has its own maps");
            var other = WornFill();
            var gen2 = window.AddGenerator(FilterTarget.Mask, GeneratorType.EdgeWear);
            Assert.That(second.Document.GetGeneratorStatus(other.Id, gen2.Id).Reason, Does.Contain("Curvature has not been baked"), "the other set's maps are not borrowed");
            Assert.That(first.Document.GetGeneratorStatus(fill.Id, gen.Id).Active, Is.True, "the first set keeps its own");
            // 今でないセットのマップを消すと、そのセットの 3D の表示も作り直す
            var shown = window.SetDisplay(first);
            first.MeshMaps.Clear();
            Assert.That(window.PollGeneratorInputs(), Is.True);
            Assert.That(first.Document.GetGeneratorStatus(fill.Id, gen.Id).Active, Is.False);
            Assert.That(first.DisplayKey, Is.Null, "the other set's 3D texture is rebuilt");
            window.SwitchTextureSet(first.Id);
            AssertShows(window, first.Document.Composite(PaintChannel.Color), "switched back");
        }

        [Test] public void GeneratorEditsUndoWithTheKeyboardAndAreRefusedWhereTheyCannotGo()
        {
            var d = window.Document; var paint = d.Layers[0]; var fill = WornFill(); int steps = d.UndoCount;
            var gen = window.AddGenerator(FilterTarget.Mask, GeneratorType.Dirt);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            for (int i = 1; i <= 8; i++) window.ApplyFilterSettings(gen.Id, gen.Settings.WithGenerator(gen.Settings.Generator.WithLevels(.05 * i, .9, .5)), coalesce: true);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 2), "one drag, one step");
            Mouse(window, EventType.MouseUp, new Vector2(5, 5)); // ドラッグの終わり
            window.ApplyFilterSettings(gen.Id, gen.Settings.WithGenerator(gen.Settings.Generator.WithInvert(true)), coalesce: true);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 3));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(fill.Mask.Filters.Single().Settings.Generator.Invert, Is.False);
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(fill.Mask.Filters.Single().Settings, Is.EqualTo(gen.Settings), "the drag undoes to before its first move");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(fill.Mask.Filters, Is.Empty);
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(fill.Mask.Filters.Single().Id, Is.EqualTo(gen.Id));
            // 型: Normal は単位ベクトルなので、画素の Generator は断る（理由を出して何も残さない）
            window.Channel = PaintChannel.Normal; int now = d.UndoCount;
            Assert.That(window.AddGenerator(FilterTarget.Content, GeneratorType.EdgeWear), Is.Null);
            Assert.That(window.StatusMessage, Does.Contain("unit vectors"));
            Assert.That(window.GeneratorChoices(fill.Id, FilterTarget.Content).All(c => c.refusal != null), Is.True, "every choice says why");
            Assert.That(window.GeneratorChoices(fill.Id, FilterTarget.Mask).Where(c => c.type != GeneratorType.Anchor).All(c => c.refusal == null), Is.True, "a mask takes every generator");
            Assert.That(window.GeneratorChoices(fill.Id, FilterTarget.Mask).Single(c => c.type == GeneratorType.Anchor).refusal, Does.Contain("no anchor below"), "the anchor generator needs an anchor below");
            Assert.That(d.UndoCount, Is.EqualTo(now));
            // 描いているあいだは変えない。Esc で何も残らない
            window.Channel = PaintChannel.Color; window.SelectedLayer = paint.Id; window.EditMask = false;
            BeginLine(250, 250);
            Assert.That(window.AddGenerator(FilterTarget.Content, GeneratorType.Thickness), Is.Null, "not during a stroke");
            Key(window, KeyCode.Escape);
            Mouse(window, EventType.MouseUp, At(window, 310, 250));
            Assert.That(window.IsStroking, Is.False); Assert.That(paint.Filters, Is.Empty); Assert.That(d.UndoCount, Is.EqualTo(now));
        }

        [Test] public void GeneratorsSurviveSaveAndOpenAndExportsAskWithoutMaps()
        {
            window.Preview.LoadDemoMesh();
            var d = window.Document; var fill = WornFill();
            var gen = window.AddGenerator(FilterTarget.Mask, GeneratorType.EdgeWear);
            BakeCurvature(window); window.PollGeneratorInputs();
            var shown = d.Composite(PaintChannel.Color);
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            Assert.That(window.StatusMessage, Does.Not.Contain("generator"));
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File; other.OpenProject();
                var od = other.Document;
                Assert.That(od.GetLayer(fill.Id).Mask.Filters.Single().Settings, Is.EqualTo(gen.Settings), other.StatusMessage);
                // マップは戻るが、デモのキューブはアセットではないので読まれない: 照合できないマップは使わない
                var status = od.GetGeneratorStatus(fill.Id, gen.Id);
                Assert.That(status.Active, Is.False); Assert.That(status.Reason, Does.Contain("no model is loaded"));
                other.Preview.LoadDemoMesh();
                Assert.That(other.PollGeneratorInputs(), Is.True);
                Assert.That(od.GetGeneratorStatus(fill.Id, gen.Id).Active, Is.True);
                Assert.That(od.Composite(PaintChannel.Color), Is.EqualTo(shown), "the same maps, the same result");
            }
            finally { Close(other); }

            // マップの無い Generator は、書き出す前に確かめる（断れば何も書かない）
            window.MeshMaps.Clear(); window.PollGeneratorInputs();
            fake.Folder = NewTempPath(); Directory.CreateDirectory(fake.Folder);
            fake.ConfirmAnswer = false; fake.Asked.Clear();
            window.ExportImages();
            Assert.That(fake.Asked, Is.EqualTo(new[] { "Confirm: Generators without mesh maps" }));
            Assert.That(Directory.GetFiles(fake.Folder), Is.Empty); Assert.That(window.StatusMessage, Does.Contain("Nothing was exported"));
            fake.ConfirmAnswer = true;
            window.ExportImages();
            Assert.That(Directory.GetFiles(fake.Folder), Is.Not.Empty, window.StatusMessage);
            window.SaveProject(false);
            Assert.That(window.StatusMessage, Does.Contain("1 generator(s) have no usable mesh maps"));
        }
    }
}
