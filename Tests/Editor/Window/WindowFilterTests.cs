using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウのフィルター: 足したフィルターが表示に出て、Ctrl+Z / Ctrl+Shift+Z で戻り・進むこと、スライダーのドラッグがマウスを
    /// 離すまで 1 回の Undo にまとまること、チャンネルの型で断られたら理由を出して何も残さないこと、描画中は変えられず Esc で何も残らないこと、
    /// .ylp の保存と開き直しで残ること、PSD 書き出しは理由を示して断り、焼き込めば書けること。入力は SendEvent で本物の経路に通す。</summary>
    public sealed partial class WindowTests
    {
        PaintLayer RedSquare()
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer); var s = layer.GetChannel(PaintChannel.Color);
            for (int y = 200; y < 300; y++) for (int x = 200; x < 300; x++) s.SetPixel(x, y, new Rgba32(230, 20, 20, 255));
            d.ClearHistory(); return layer;
        }

        [Test] public void AFilterShowsAndUndoesWithTheKeyboard()
        {
            var d = window.Document; var layer = RedSquare();
            var plain = d.Composite(PaintChannel.Color);
            var blur = window.AddFilter(FilterTarget.Content, FilterSettings.GaussianBlur(12));
            Assert.That(blur, Is.Not.Null, window.StatusMessage);
            Assert.That(blur.Channels, Is.EqualTo(new[] { PaintChannel.Color }), "the window adds it to the channel on screen");
            Assert.That(window.StatusMessage, Does.StartWith("Added"));
            Assert.That(d.CompositePixel(PaintChannel.Color, 205, 250).A, Is.LessThan(255)); Assert.That(d.CompositePixel(PaintChannel.Color, 195, 250).A, Is.GreaterThan(0));
            AssertShows(window, d.Composite(PaintChannel.Color), "blurred");
            Assert.That(layer.GetPixel(PaintChannel.Color, 200, 250), Is.EqualTo(new Rgba32(230, 20, 20, 255)), "the painted pixels are unchanged");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(layer.Filters, Is.Empty); AssertShows(window, plain, "undone");
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(layer.Filters.Single().Id, Is.EqualTo(blur.Id)); AssertShows(window, d.Composite(PaintChannel.Color), "redone");
            window.ToggleFilter(blur.Id, false); AssertShows(window, plain, "disabled");
            window.ToggleFilter(blur.Id, true);
            var inv = window.AddFilter(FilterTarget.Content, FilterSettings.Invert());
            window.MoveFilter(inv.Id, 0);
            Assert.That(layer.Filters.First().Id, Is.EqualTo(inv.Id));
            AssertShows(window, d.Composite(PaintChannel.Color), "reordered");
            window.RemoveFilter(inv.Id); window.RemoveFilter(blur.Id);
            AssertShows(window, plain, "removed");
        }

        [Test] public void SliderDragsAreOneUndoStepUntilTheMouseIsReleased()
        {
            var d = window.Document; RedSquare();
            var blur = window.AddFilter(FilterTarget.Content, FilterSettings.GaussianBlur(2));
            int steps = d.UndoCount;
            for (int r = 3; r <= 20; r++) window.ApplyFilterSettings(blur.Id, FilterSettings.GaussianBlur(r), coalesce: true);
            for (int i = 1; i <= 4; i++) window.ApplyFilterStrength(blur.Id, 1 - i * .1, coalesce: true);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 2), "radius and strength are separate drags");
            Mouse(window, EventType.MouseUp, new Vector2(5, 5)); // ドラッグの終わり
            window.ApplyFilterSettings(blur.Id, FilterSettings.GaussianBlur(25), coalesce: true);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 3), "releasing the mouse ends the run");
            AssertShows(window, d.Composite(PaintChannel.Color), "after the drags");
        }

        [Test] public void RefusedFiltersExplainAndLeaveNothing()
        {
            var d = window.Document; RedSquare(); int steps = d.UndoCount;
            window.Channel = PaintChannel.Normal;
            Assert.That(window.AddFilter(FilterTarget.Content, FilterSettings.Sharpen()), Is.Null);
            Assert.That(window.StatusMessage, Does.Contain("normals"));
            window.Channel = PaintChannel.Roughness;
            Assert.That(window.AddFilter(FilterTarget.Content, FilterSettings.Noise(.3, 1, false)), Is.Null);
            Assert.That(window.StatusMessage, Does.Contain("monochrome"));
            Assert.That(window.AddFilter(FilterTarget.Mask, FilterSettings.Invert()), Is.Null, "no mask yet");
            Assert.That(d.UndoCount, Is.EqualTo(steps));
            BeginLine(250, 250);
            Assert.That(window.AddFilter(FilterTarget.Content, FilterSettings.GaussianBlur(3)), Is.Null, "not during a stroke");
            Key(window, KeyCode.Escape);
            Mouse(window, EventType.MouseUp, At(window, 310, 250));
            Assert.That(window.IsStroking, Is.False);
            Assert.That(d.GetLayer(window.SelectedLayer).Filters, Is.Empty);
        }

        [Test] public void FiltersSurviveSaveAndOpenAndPsdExportAsksForABake()
        {
            var d = window.Document; var layer = RedSquare(); d.AddLayerMask(layer.Id); d.ClearHistory();
            var blur = window.AddFilter(FilterTarget.Content, FilterSettings.GaussianBlur(6));
            window.AddFilter(FilterTarget.Mask, FilterSettings.Levels(0, 1, 1, .3, 1));
            var shown = d.Composite(PaintChannel.Color);
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File; other.OpenProject();
                var reopened = other.Document.GetLayer(layer.Id);
                Assert.That(reopened.Filters.Single().Id, Is.EqualTo(blur.Id), other.StatusMessage);
                Assert.That(reopened.Mask.Filters.Single().Settings, Is.EqualTo(FilterSettings.Levels(0, 1, 1, .3, 1)));
                Assert.That(other.Document.Composite(PaintChannel.Color), Is.EqualTo(shown));
            }
            finally { Close(other); }
            fake.File = NewTempPath(".psd"); fake.Asked.Clear();
            window.ExportPsd();
            Assert.That(fake.Asked, Is.EqualTo(new[] { "Inform: PSD export unavailable" })); Assert.That(File.Exists(fake.File), Is.False);
            Assert.That(window.StatusMessage, Does.Contain("non-destructive filters"));
            int steps = d.UndoCount;
            window.BakeFilters();
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1)); Assert.That(layer.Filters, Is.Empty);
            AssertShows(window, shown, "baking keeps what is shown");
            window.ExportPsd();
            Assert.That(File.Exists(fake.File), Is.True, window.StatusMessage);
        }
    }
}
