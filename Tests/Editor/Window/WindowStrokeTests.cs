using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウの手ぶれ補正と入り抜き: 値がストロークの設定に渡り、プリセットを選び直しても描き手の設定として残り、
    /// 補正した線は離したときにポインタまで描かれる。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void StabilizerAndTapersReachTheStrokeAndSurvivePresetChanges()
        {
            var b = window.Brush; b.stabilizer = 30; b.taperIn = 4; b.taperOut = 6; b.pressureSize = false; window.Brush = b;
            var s = window.GetBrush();
            Assert.That((s.Stabilizer, s.TaperIn, s.TaperOut), Is.EqualTo((30.0, 4.0, 6.0)));
            window.ApplyPreset(BuiltInBrushes.Presets[1]);
            Assert.That(window.GetBrush().Stabilizer, Is.EqualTo(30), "a preset does not reset the stabilizer");
            b = window.Brush; b.taperIn = 0; b.taperOut = 0; window.Brush = b;
            var d = window.Document;
            // 糸より短い動きでも、離したときに最後の点まで描く
            Mouse(window, EventType.MouseDown, At(window, 300, 300));
            Mouse(window, EventType.MouseDrag, At(window, 310, 300));
            Mouse(window, EventType.MouseUp, At(window, 320, 300));
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            Assert.That(d.CompositePixel(PaintChannel.Color, 310, 300).A, Is.GreaterThan(0), "the stabilized line is finished to the last pointer position");
        }
    }
}
